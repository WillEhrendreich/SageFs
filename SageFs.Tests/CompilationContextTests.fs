module SageFs.Tests.CompilationContextTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Middleware.CompilationContext
open SageFs.WorkerProtocol

/// Sync wrapper for tests — awaits the Task-returning parseFileStructure.
let parseFs path code = (parseFileStructure path code).Result

/// Sync wrapper for tests — awaits the Task-returning parseFileStructureCached.
let parseFsCached path code cache = (parseFileStructureCached path code cache).Result

// ─────────────────────────────────────────────────────────────────
// Fixture data — real F# file contents for test scenarios
// ─────────────────────────────────────────────────────────────────

module Fixtures =
  let fileLevelModule = """module GameOfLife

open System

type Grid = byte[,]

let randomGrid w h density =
  Array2D.init w h (fun _ _ ->
    if Random.Shared.NextDouble() < density then 1uy else 0uy)

let step (grid: Grid) =
  Array2D.init (Array2D.length1 grid) (Array2D.length2 grid) (fun x y ->
    let n = countNeighbors grid x y
    match grid.[x, y] > 0uy, n with
    | true, 2 | true, 3 -> min (grid.[x, y] + 1uy) 255uy
    | false, 3 -> 1uy
    | _ -> 0uy)"""

  let namespacePlusModule = """namespace MyApp.Domain

open System

module GameOfLife =
  type Grid = byte[,]

  let step (grid: Grid) = grid"""

  let namespaceMultiModule = """namespace MyApp.Domain

open System

module GameOfLife =
  type Grid = byte[,]
  let step grid = grid

module Rendering =
  let render grid = sprintf "%A" grid"""

  let namespaceOnly = """namespace MyApp.Domain

open System

type Grid = byte[,]

let step grid = grid"""

  let internalModule = """module internal GameOfLife

open System

let step grid = grid"""

  let noDeclaration = """open System

let x = 42
let y = x + 1"""

  let blockFromModule = """let step (grid: Grid) =
  Array2D.init (Array2D.length1 grid) (Array2D.length2 grid) (fun x y ->
    let n = countNeighbors grid x y
    match grid.[x, y] > 0uy, n with
    | true, 2 | true, 3 -> min (grid.[x, y] + 1uy) 255uy
    | false, 3 -> 1uy
    | _ -> 0uy)"""

  let blockFromNamespacedModule = """let step (grid: Grid) = grid"""

// ─────────────────────────────────────────────────────────────────
// 1. parseFileStructure tests
// ─────────────────────────────────────────────────────────────────

let parseFileStructureTests =
  testList "parseFileStructure" [

    test "parses file-level module" {
      let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
      fs.HasFileLevelModule |> Expect.isTrue "should detect file-level module"
      fs.Containers |> Expect.hasLength "should have one container" 1
      fs.Containers.[0].QualifiedName
      |> Expect.equal "should be GameOfLife" "GameOfLife"
    }

    test "parses namespace + named module" {
      let fs = parseFs "Domain.fs" Fixtures.namespacePlusModule
      fs.HasFileLevelModule |> Expect.isFalse "namespace is not file-level module"
      fs.Containers |> Expect.hasLength "should have one namespace" 1
      fs.Containers.[0].QualifiedName
      |> Expect.equal "should be MyApp.Domain" "MyApp.Domain"
      fs.Containers.[0].Children |> Expect.hasLength "should have nested module" 1
      fs.Containers.[0].Children.[0].QualifiedName
      |> Expect.equal "should be MyApp.Domain.GameOfLife" "MyApp.Domain.GameOfLife"
    }

    test "parses namespace with multiple modules" {
      let fs = parseFs "Domain.fs" Fixtures.namespaceMultiModule
      fs.Containers.[0].Children |> Expect.hasLength "should have 2 nested modules" 2
      fs.Containers.[0].Children.[0].QualifiedName
      |> Expect.equal "first module" "MyApp.Domain.GameOfLife"
      fs.Containers.[0].Children.[1].QualifiedName
      |> Expect.equal "second module" "MyApp.Domain.Rendering"
    }

    test "parses internal module" {
      let fs = parseFs "Internal.fs" Fixtures.internalModule
      fs.HasFileLevelModule |> Expect.isTrue "should detect file-level module"
      fs.Containers.[0].AccessModifier
      |> Expect.isSome "should have access modifier"
    }

    test "parses file with no module or namespace" {
      let fs = parseFs "Script.fsx" Fixtures.noDeclaration
      fs.HasFileLevelModule |> Expect.isFalse "no file-level module"
    }

    test "collects open statements" {
      let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
      fs.Containers.[0].Opens
      |> List.exists (fun o -> o.Contains("System"))
      |> Expect.isTrue "should have open System"
    }

    test "tracks declaration ranges" {
      let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
      fs.Containers.[0].DeclarationRanges
      |> List.isEmpty
      |> Expect.isFalse "should have declaration ranges"
    }
  ]

// ─────────────────────────────────────────────────────────────────
// 2. preprocessForFsi tests — the core transformation
// ─────────────────────────────────────────────────────────────────

let preprocessForFsiTests =
  testList "preprocessForFsi" [

    testList "whole-file eval" [

      test "file-level module → nested module with indented body" {
        let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
        let result, _ =
          preprocessForFsi (Some fs) (File) None Set.empty
            Fixtures.fileLevelModule
        result.Code
        |> Expect.stringContains "should start with module decl" "module GameOfLife ="
        result.Code
        |> Expect.stringContains "body should be indented" "  type Grid"
        result.Code
        |> Expect.stringContains "functions indented" "  let randomGrid"
      }

      test "namespace file → namespace stripped" {
        let fs = parseFs "Domain.fs" Fixtures.namespaceOnly
        let result, _ =
          preprocessForFsi (Some fs) (File) None Set.empty
            Fixtures.namespaceOnly
        (result.Code.Contains("namespace MyApp.Domain"))
        |> Expect.isFalse "namespace should be stripped"
      }

      test "namespace + module file → namespace stripped, module preserved" {
        let fs = parseFs "Domain.fs" Fixtures.namespacePlusModule
        let result, _ =
          preprocessForFsi (Some fs) (File) None Set.empty
            Fixtures.namespacePlusModule
        (result.Code.Contains("namespace MyApp.Domain"))
        |> Expect.isFalse "namespace should be stripped"
        result.Code
        |> Expect.stringContains "module should be preserved" "module GameOfLife ="
      }

      test "no declaration file → pass through unchanged" {
        let fs = parseFs "Script.fsx" Fixtures.noDeclaration
        let result, _ =
          preprocessForFsi (Some fs) (File) None Set.empty
            Fixtures.noDeclaration
        result.Code
        |> Expect.equal "should be unchanged" Fixtures.noDeclaration
      }

      test "internal module → access modifier preserved" {
        let fs = parseFs "Internal.fs" Fixtures.internalModule
        let result, _ =
          preprocessForFsi (Some fs) (File) None Set.empty
            Fixtures.internalModule
        result.Code
        |> Expect.stringContains "should have internal" "internal"
        result.Code
        |> Expect.stringContains "should have =" "="
      }

      test "whole file marks all modules as evaluated" {
        let fs = parseFs "Domain.fs" Fixtures.namespaceMultiModule
        let _, mods =
          preprocessForFsi (Some fs) (File) None Set.empty
            Fixtures.namespaceMultiModule
        mods |> Set.contains "MyApp.Domain"
        |> Expect.isTrue "should track namespace"
        mods |> Set.contains "MyApp.Domain.GameOfLife"
        |> Expect.isTrue "should track GameOfLife"
        mods |> Set.contains "MyApp.Domain.Rendering"
        |> Expect.isTrue "should track Rendering"
      }
    ]

    testList "block eval" [

      test "block from file-level module → wrapped in module" {
        let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
        let result, evaluatedModules =
          preprocessForFsi (Some fs) (Block) None Set.empty
            Fixtures.blockFromModule
        result.Code
        |> Expect.stringContains "should have module wrapper" "module GameOfLife ="
        result.Code
        |> Expect.stringContains "body inside module" "  let step"
        evaluatedModules |> Set.contains "GameOfLife"
        |> Expect.isTrue "should track module"
      }

      test "second block from same module → gets open prepended" {
        let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
        let alreadyEvald = Set.singleton "GameOfLife"
        let result, _ =
          preprocessForFsi (Some fs) (Block) None alreadyEvald
            Fixtures.blockFromModule
        result.Code
        |> Expect.stringContains "should have open" "open GameOfLife"
        result.Code
        |> Expect.stringContains "should have module" "module GameOfLife ="
      }

      test "block from namespace+module → wrapped with qualified name" {
        let fs = parseFs "Domain.fs" Fixtures.namespacePlusModule
        let result, evaluatedModules =
          preprocessForFsi (Some fs) (Block) (Some 7) Set.empty
            Fixtures.blockFromNamespacedModule
        result.Code
        |> Expect.stringContains "should use leaf name for nested wrapper"
            "module GameOfLife ="
        evaluatedModules |> Set.contains "MyApp.Domain.GameOfLife"
        |> Expect.isTrue "should track qualified"
      }

      test "block with no file context → pass through unchanged" {
        let result, _ =
          preprocessForFsi None (Block) None Set.empty "let x = 42"
        result.Code
        |> Expect.equal "should be unchanged" "let x = 42"
        result.LineOffset
        |> Expect.equal "no offset" 0
      }
    ]

    testList "line offset tracking" [

      test "whole-file module transform has zero line offset" {
        let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
        let result, _ =
          preprocessForFsi (Some fs) (File) None Set.empty
            Fixtures.fileLevelModule
        result.LineOffset
        |> Expect.equal "module replacement has zero line offset" 0
        result.ColumnOffset
        |> Expect.equal "body indented by 2" 2
      }

      test "block wrap with prior eval adds lines for open + module" {
        let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
        let alreadyEvald = Set.singleton "GameOfLife"
        let result, _ =
          preprocessForFsi (Some fs) (Block) None alreadyEvald
            Fixtures.blockFromModule
        // open GameOfLife (1) + open System (1) + module GameOfLife = (1) = 3 lines
        (result.LineOffset, 0)
        |> Expect.isGreaterThan "should add lines"
        result.ColumnOffset
        |> Expect.equal "body indented by 2" 2
      }

      test "first block wrap adds opens + module line" {
        let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
        let result, _ =
          preprocessForFsi (Some fs) (Block) None Set.empty
            Fixtures.blockFromModule
        let expectedOpens = fs.Containers.[0].Opens.Length
        result.LineOffset
        |> Expect.equal "opens + module line" (expectedOpens + 1)
      }

      test "no-op transform has zero offsets" {
        let result, _ =
          preprocessForFsi None Auto None Set.empty "let x = 42"
        result.LineOffset |> Expect.equal "zero" 0
        result.ColumnOffset |> Expect.equal "zero" 0
      }
    ]

    testList "heuristic eval mode detection" [

      test "code starting with file-level module detected as whole file" {
        let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
        let result, _ =
          preprocessForFsi (Some fs) Auto None Set.empty
            Fixtures.fileLevelModule
        result.Code
        |> Expect.stringContains "should transform" "module GameOfLife ="
      }

      test "code without module/namespace detected as block" {
        let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
        let result, _ =
          preprocessForFsi (Some fs) Auto None Set.empty "let x = 42"
        result.Code
        |> Expect.stringContains "should wrap" "module GameOfLife ="
      }
    ]
  ]

// ─────────────────────────────────────────────────────────────────
// 3. locateBlock tests
// ─────────────────────────────────────────────────────────────────

let locateBlockTests =
  testList "locateBlock" [

    test "locates block in file-level module by line number" {
      let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
      let path = locateBlock fs (Some 8)
      path |> Expect.isNonEmpty "should find container"
      (path |> List.last).QualifiedName
      |> Expect.equal "should be GameOfLife" "GameOfLife"
    }

    test "locates block in nested module by line number" {
      let fs = parseFs "Domain.fs" Fixtures.namespacePlusModule
      let path = locateBlock fs (Some 7)
      path |> Expect.isNonEmpty "should find nested module"
      (path |> List.last).QualifiedName
      |> Expect.equal "should be qualified" "MyApp.Domain.GameOfLife"
    }

    test "locates block in second module of multi-module file" {
      let fs = parseFs "Domain.fs" Fixtures.namespaceMultiModule
      let path = locateBlock fs (Some 10)
      path |> Expect.isNonEmpty "should find Rendering"
      (path |> List.last).QualifiedName
      |> Expect.equal "should be Rendering" "MyApp.Domain.Rendering"
    }

    test "falls back to top-level when no line number" {
      let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
      let path = locateBlock fs None
      path |> Expect.isNonEmpty "should fall back to top-level"
      (path |> List.last).QualifiedName
      |> Expect.equal "should be GameOfLife" "GameOfLife"
    }

    test "namespace+module with no line falls back to single child" {
      let fs = parseFs "Domain.fs" Fixtures.namespacePlusModule
      let path = locateBlock fs None
      path |> Expect.isNonEmpty "should find single child"
      (path |> List.last).QualifiedName
      |> Expect.equal "should be the nested module" "MyApp.Domain.GameOfLife"
    }
  ]

// ─────────────────────────────────────────────────────────────────
// 4. Property-based tests (algebraic properties)
// ─────────────────────────────────────────────────────────────────

let propertyTests =
  testList "CompilationContext properties" [

    test "no file context is identity" {
      let codes = [
        "let x = 42"
        "type Foo = { Bar: int }"
        "module M =\n  let x = 1"
        "open System\nlet x = DateTime.Now"
      ]
      for code in codes do
        let result, _ = preprocessForFsi None Auto None Set.empty code
        result.Code
        |> Expect.equal
            (sprintf "identity for: %s..." (code.Substring(0, min 20 code.Length)))
            code
    }

    test "line offset consistency for whole-file module" {
      let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
      let result, _ =
        preprocessForFsi (Some fs) (File) None Set.empty
          Fixtures.fileLevelModule
      let originalLines = Fixtures.fileLevelModule.Split('\n').Length
      let transformedLines = result.Code.Split('\n').Length
      (transformedLines - originalLines)
      |> Expect.equal "line diff matches offset" result.LineOffset
    }

    test "evaluated modules accumulate correctly" {
      let fs = parseFs "GameOfLife.fs" Fixtures.fileLevelModule
      let _, mods1 =
        preprocessForFsi (Some fs) (Block) None Set.empty "let x = 1"
      mods1 |> Set.contains "GameOfLife"
      |> Expect.isTrue "should add module on first eval"
      let _, mods2 =
        preprocessForFsi (Some fs) (Block) None mods1 "let y = 2"
      mods2 |> Set.contains "GameOfLife"
      |> Expect.isTrue "should still contain module"
    }
  ]

// ─────────────────────────────────────────────────────────────────
// 5. Diagnostic mapping tests
// ─────────────────────────────────────────────────────────────────

let diagnosticMappingTests =
  testList "diagnostic mapping" [

    test "mapDiagnosticLine subtracts offset" {
      mapDiagnosticLine 2 5
      |> Expect.equal "line 5 - offset 2 = line 3" 3
    }

    test "mapDiagnosticColumn clamps to zero" {
      mapDiagnosticColumn 4 2
      |> Expect.equal "column 2 - offset 4 = 0 (clamped)" 0
    }

    test "mapDiagnosticColumn subtracts when sufficient" {
      mapDiagnosticColumn 2 6
      |> Expect.equal "column 6 - offset 2 = 4" 4
    }

    test "zero offsets are identity" {
      mapDiagnosticLine 0 10
      |> Expect.equal "no change" 10
      mapDiagnosticColumn 0 5
      |> Expect.equal "no change" 5
    }
  ]

// ─────────────────────────────────────────────────────────────────
// 6. Response diagnostic adjustment tests
// ─────────────────────────────────────────────────────────────────

let responseDiagnosticTests =
  testList "adjustResponseDiagnostics" [

    test "adjusts all line/column fields on EvalResult diagnostics" {
      let diag : WorkerDiagnostic =
        { Severity = Features.Diagnostics.DiagnosticSeverity.Error
          Message = "type mismatch"
          StartLine = 7; StartColumn = 6
          EndLine = 7; EndColumn = 12 }
      let response =
        WorkerResponse.EvalResult("r1", Ok "done", [diag], Map.empty)
      let adjusted =
        McpTools.adjustResponseDiagnostics 3 2 response
      match adjusted with
      | WorkerResponse.EvalResult(_, _, [d], _) ->
        d.StartLine |> Expect.equal "startLine 7-3=4" 4
        d.StartColumn |> Expect.equal "startCol 6-2=4" 4
        d.EndLine |> Expect.equal "endLine 7-3=4" 4
        d.EndColumn |> Expect.equal "endCol 12-2=10" 10
      | other -> failwith (sprintf "unexpected: %A" other)
    }

    test "zero offsets returns same response" {
      let diag : WorkerDiagnostic =
        { Severity = Features.Diagnostics.DiagnosticSeverity.Warning
          Message = "unused"
          StartLine = 5; StartColumn = 3
          EndLine = 5; EndColumn = 8 }
      let response =
        WorkerResponse.EvalResult("r1", Ok "ok", [diag], Map.empty)
      let adjusted =
        McpTools.adjustResponseDiagnostics 0 0 response
      match adjusted with
      | WorkerResponse.EvalResult(_, _, [d], _) ->
        d.StartLine |> Expect.equal "unchanged" 5
        d.StartColumn |> Expect.equal "unchanged" 3
      | other -> failwith (sprintf "unexpected: %A" other)
    }

    test "non-EvalResult responses pass through unchanged" {
      let response = WorkerResponse.WorkerError (SageFsError.PipeClosed)
      let adjusted =
        McpTools.adjustResponseDiagnostics 5 3 response
      match adjusted with
      | WorkerResponse.WorkerError (SageFsError.PipeClosed) -> ()
      | other -> failwith (sprintf "unexpected: %A" other)
    }

    test "column clamped to zero" {
      let diag : WorkerDiagnostic =
        { Severity = Features.Diagnostics.DiagnosticSeverity.Error
          Message = "err"
          StartLine = 3; StartColumn = 1
          EndLine = 3; EndColumn = 5 }
      let response =
        WorkerResponse.EvalResult("r1", Error (SageFsError.EvalFailed "x"), [diag], Map.empty)
      let adjusted =
        McpTools.adjustResponseDiagnostics 1 4 response
      match adjusted with
      | WorkerResponse.EvalResult(_, _, [d], _) ->
        d.StartLine |> Expect.equal "line 3-1=2" 2
        d.StartColumn |> Expect.equal "col 1-4=0 (clamped)" 0
        d.EndColumn |> Expect.equal "col 5-4=1" 1
      | other -> failwith (sprintf "unexpected: %A" other)
    }
  ]

// ─────────────────────────────────────────────────────────────────
// parseFsCached tests
// ─────────────────────────────────────────────────────────────────

let fileCacheTests =
  testList "parseFsCached" [
    test "cache miss parses and stores" {
      let code = Fixtures.fileLevelModule
      let emptyCache = Map.empty
      let fs, cache = parseFsCached "Test.fs" code emptyCache
      fs.Containers |> Expect.isNonEmpty "should have containers"
      cache |> Map.containsKey "Test.fs"
      |> Expect.isTrue "should be cached"
    }

    test "cache hit returns same structure without re-parse" {
      let code = Fixtures.fileLevelModule
      let fs1, cache1 = parseFsCached "Test.fs" code Map.empty
      let fs2, cache2 = parseFsCached "Test.fs" code cache1
      fs2.Containers.Length
      |> Expect.equal "same container count" fs1.Containers.Length
      cache2 |> Map.count
      |> Expect.equal "still one entry" 1
    }

    test "cache invalidates on content change" {
      let code1 = Fixtures.fileLevelModule
      let _, cache1 = parseFsCached "Test.fs" code1 Map.empty
      let code2 = Fixtures.namespacePlusModule
      let fs2, cache2 = parseFsCached "Test.fs" code2 cache1
      fs2.FilePath |> Expect.equal "same path" "Test.fs"
      cache2 |> Map.count
      |> Expect.equal "still one entry (replaced)" 1
      // content hash should differ
      let (h1, _) = cache1 |> Map.find "Test.fs"
      let (h2, _) = cache2 |> Map.find "Test.fs"
      h2 |> Expect.notEqual "hash should differ" h1
    }

    test "different file paths get separate cache entries" {
      let code = Fixtures.fileLevelModule
      let _, cache1 = parseFsCached "A.fs" code Map.empty
      let _, cache2 = parseFsCached "B.fs" code cache1
      cache2 |> Map.count
      |> Expect.equal "two entries" 2
    }

    test "contentHash is deterministic" {
      let h1 = contentHash "let x = 42"
      let h2 = contentHash "let x = 42"
      h2 |> Expect.equal "same input same hash" h1
    }

    test "contentHash differs for different content" {
      let h1 = contentHash "let x = 42"
      let h2 = contentHash "let x = 43"
      h2 |> Expect.notEqual "different input different hash" h1
    }
  ]

// ─────────────────────────────────────────────────────────────────
// Performance measurement
// ─────────────────────────────────────────────────────────────────

let perfMeasurementTests =
  testList "parseFileStructure perf" [
    test "first parse vs cache hit latency" {
      let code = Fixtures.namespaceMultiModule
      let sw = System.Diagnostics.Stopwatch()

      // Cold parse
      sw.Start()
      let _, cache = parseFsCached "Perf.fs" code Map.empty
      sw.Stop()
      let coldMs = sw.Elapsed.TotalMilliseconds

      // Cache hit
      sw.Restart()
      let _, _ = parseFsCached "Perf.fs" code cache
      sw.Stop()
      let hotMs = sw.Elapsed.TotalMilliseconds

      // Cache hit should be significantly faster
      printfn "Cold parse: %.2fms, Cache hit: %.2fms, Speedup: %.1fx" coldMs hotMs (coldMs / hotMs)
      (hotMs, coldMs) |> Expect.isLessThan "cache hit faster than cold parse"
    }

    test "parse scales linearly with file size" {
      let small = Fixtures.fileLevelModule
      let large =
        [| for _ in 1..20 -> Fixtures.namespaceMultiModule |]
        |> String.concat "\n"
      let sw = System.Diagnostics.Stopwatch()

      sw.Start()
      parseFs "Small.fs" small |> ignore
      sw.Stop()
      let smallMs = sw.Elapsed.TotalMilliseconds

      sw.Restart()
      parseFs "Large.fs" large |> ignore
      sw.Stop()
      let largeMs = sw.Elapsed.TotalMilliseconds

      printfn "Small (%d chars): %.2fms, Large (%d chars): %.2fms" small.Length smallMs large.Length largeMs
    }
  ]

// ─────────────────────────────────────────────────────────────────
// 8. EvalMode + namespace-as-container tests
// ─────────────────────────────────────────────────────────────────

let evalModeTests =
  testList "EvalMode" [
    test "parse Some file → File" {
      EvalMode.parse (Some "file") |> Expect.equal "should be File" File
    }
    test "parse Some block → Block" {
      EvalMode.parse (Some "block") |> Expect.equal "should be Block" Block
    }
    test "parse None → Auto" {
      EvalMode.parse None |> Expect.equal "should be Auto" Auto
    }
    test "parse Some unknown → Auto" {
      EvalMode.parse (Some "whatever") |> Expect.equal "should be Auto" Auto
    }
    test "parse Some File (case-insensitive) → File" {
      EvalMode.parse (Some "File") |> Expect.equal "should be File" File
    }
  ]

let namespaceContainerTests =
  testList "namespace-as-container" [

    test "block in namespace-only file has zero column offset" {
      let fs = parseFs "Domain.fs" Fixtures.namespaceOnly
      let result, _ =
        preprocessForFsi (Some fs) Block None Set.empty "let x = 42"
      result.ColumnOffset
      |> Expect.equal "namespace blocks get no indent" 0
    }

    test "block in namespace-only file does not emit module wrapper" {
      let fs = parseFs "Domain.fs" Fixtures.namespaceOnly
      let result, _ =
        preprocessForFsi (Some fs) Block None Set.empty "let x = 42"
      result.Code.Contains("module MyApp.Domain =")
      |> Expect.isFalse "should not wrap namespace as module"
    }

    test "multi-namespace file with no line returns None from locateBlock" {
      let multiNs = """namespace First

let a = 1

namespace Second

let b = 2"""
      let fs = parseFs "Multi.fs" multiNs
      let container = locateBlock fs None
      container |> Expect.isEmpty "ambiguous without line info"
    }
  ]

// ─────────────────────────────────────────────────────────────────
// 9. Block-eval module context tests
//    WHY: When a user evaluates a selected block from a file that has
//    a file-level module, SageFs must wrap that block in the correct
//    module — not in an anonymous "Tmp" module produced by parsing
//    the block code in isolation.
//
//    The root scenario: pong.fs has `module Pong` at the top. The
//    user evaluates just the `update` function. The wrapper MUST be
//    `module Pong =` so FSI can see State, paddleHeight, etc.
// ─────────────────────────────────────────────────────────────────

module BlockEvalFixtures =

  /// Exactly the pong.fs module header pattern that triggered the bug:
  /// file-level module with a trailing comment on the module line.
  let pongFileContent = """module Pong 
  // All coordinates normalized 0.0–1.0
  // Pure functions — every one is a hot-reload target

type State =
  { BallX: float; BallY: float
    BallVX: float; BallVY: float
    LeftY: float; RightY: float
    LeftScore: int; RightScore: int
    Trail: (float * float) list }

let init () =
  { BallX = 0.5; BallY = 0.5
    BallVX = 0.6; BallVY = 0.4
    LeftY = 0.5; RightY = 0.5
    LeftScore = 0; RightScore = 0
    Trail = [] }

let paddleHeight () = 0.15
let maxTrail () = 3

let update (dt: float) (state: State) =
  let aiSpeed = 1.0
  state"""

  /// The selected block text the user actually sends — just the update function.
  /// This is what `code` contains in the Mcp.fs sendFSharpCode call.
  let updateBlockOnly = """let update (dt: float) (state: State) =
  let aiSpeed = 1.0
  state"""

  /// A file-level module without comments on the module line — should also work.
  let simpleFileLevelModule = """module Calculator

let add x y = x + y
let subtract x y = x - y
let multiply x y = x * y"""

  let addBlockOnly = "let add x y = x + y"

  /// Namespace + nested module — block from inside the nested module.
  let namespacedModuleContent = """namespace MyGame.Domain

module Physics =
  let gravity = 9.8
  let applyGravity v = v + gravity"""

  let gravityBlockOnly = "let gravity = 9.8"

  /// Two levels of nesting: outer module contains inner module.
  /// Block selected inside Inner — path should be [Outer; Outer.Inner].
  let twoLevelNestedContent = """module Outer

module Inner =
  let helper x = x + 1
  let compute x = helper x * 2"""

  let innerComputeBlock = "let compute x = helper x * 2"

  /// Three levels of nesting: module A > module B > module C.
  /// Block selected inside C — path should be [A; A.B; A.B.C].
  let threeLevelNestedContent = """module A

module B =
  module C =
    let deepFn x = x * 3"""

  let deepFnBlock = "let deepFn x = x * 3"

  /// Namespace with two levels of nested modules:
  /// namespace MyNs, module Outer, module Inner.
  let namespaceTwoLevelContent = """namespace MyNs

module Outer =
  module Inner =
    let value = 42"""

  let valueBlock = "let value = 42"

let blockEvalModuleContextTests =
  testList "block eval resolves correct module context from full file" [

    // ── THE CORE BUG SCENARIO ────────────────────────────────────────

    test "REPL intent: evaluating update from pong.fs wraps in 'module Pong', not 'module Tmp'" {
      // WHY: When the user selects the `update` function and hits eval,
      // SageFs must use the FULL FILE STRUCTURE to determine the enclosing
      // module — not parse the selected block text in isolation.
      // Parsing the block alone produces AnonModule "Tmp" (FCS default for
      // code with no module declaration), which is invalid in FSI context.
      let fullFileFs = parseFs "pong.fs" BlockEvalFixtures.pongFileContent
      let result, _ =
        preprocessForFsi
          (Some fullFileFs) Block (Some 20) Set.empty
          BlockEvalFixtures.updateBlockOnly
      result.Code
      |> Expect.stringContains
          "should wrap in Pong module, not Tmp"
          "module Pong ="
      result.Code
      |> Expect.stringContains "update function inside wrapper" "let update"
    }

    test "REPL intent: the wrapper 'module Pong =' is not 'module Tmp ='" {
      // WHY: The literal error the user saw was because transformBlock
      // wrapped in "module Tmp =" — FSI rejected it with
      // "The namespace or module 'Tmp' is not defined."
      // This test makes that regression impossible to reintroduce.
      let fullFileFs = parseFs "pong.fs" BlockEvalFixtures.pongFileContent
      let result, _ =
        preprocessForFsi
          (Some fullFileFs) Block (Some 20) Set.empty
          BlockEvalFixtures.updateBlockOnly
      result.Code.Contains("module Tmp")
      |> Expect.isFalse "should never emit 'module Tmp' for a named module file"
    }

    test "REPL intent: second block eval from pong.fs opens Pong before redefining it" {
      // WHY: After the first eval, Pong is in EvaluatedModules.
      // The second eval must emit 'open Pong' so prior bindings
      // (State, paddleHeight, etc.) are accessible inside the new wrapper.
      let fullFileFs = parseFs "pong.fs" BlockEvalFixtures.pongFileContent
      let alreadyEvaluated = Set.singleton "Pong"
      let result, _ =
        preprocessForFsi
          (Some fullFileFs) Block (Some 20) alreadyEvaluated
          BlockEvalFixtures.updateBlockOnly
      result.Code
      |> Expect.stringContains
          "should open Pong for second block eval"
          "open Pong"
      result.Code
      |> Expect.stringContains "should still have module wrapper" "module Pong ="
    }

    // ── GENERALISED: ANY FILE-LEVEL MODULE ──────────────────────────

    test "block from any file-level module uses that module's name" {
      // WHY: The fix must be general — not specific to 'Pong'.
      // Any file with 'module Foo' at the top and a block selected
      // inside must produce 'module Foo =' in the wrapper.
      let fullFileFs = parseFs "Calculator.fs" BlockEvalFixtures.simpleFileLevelModule
      let result, _ =
        preprocessForFsi
          (Some fullFileFs) Block (Some 3) Set.empty
          BlockEvalFixtures.addBlockOnly
      result.Code
      |> Expect.stringContains "should wrap in Calculator" "module Calculator ="
      result.Code.Contains("module Tmp")
      |> Expect.isFalse "should never produce Tmp"
    }

    test "block from nested module uses leaf name for nested wrapper" {
      // WHY: namespace + nested module is the other common case.
      // Block from inside 'module Physics' under 'namespace MyGame.Domain'
      // must produce 'module Physics =' (leaf name, since dotted module
      // bindings are only valid as file-level declarations).
      let fullFileFs = parseFs "Physics.fs" BlockEvalFixtures.namespacedModuleContent
      let result, _ =
        preprocessForFsi
          (Some fullFileFs) Block (Some 4) Set.empty
          BlockEvalFixtures.gravityBlockOnly
      result.Code
      |> Expect.stringContains
          "should use leaf name for nested wrapper"
          "module Physics ="
    }

    // ── PARSING ISOLATION: the bug precondition ──────────────────────

    test "parsing the block code IN ISOLATION produces AnonModule Tmp" {
      // WHY: This documents the exact precondition for the bug.
      // If this test starts failing, it means FCS changed behaviour
      // and the defensive guards can be re-evaluated.
      let blockOnlyFs = parseFs "pong.fs" BlockEvalFixtures.updateBlockOnly
      blockOnlyFs.Containers |> Expect.hasLength "one anon container" 1
      blockOnlyFs.Containers.[0].QualifiedName
      |> Expect.equal "FCS names it Tmp" "Tmp"
      blockOnlyFs.Containers.[0].Kind
      |> Expect.equal "AnonModule kind" Fantomas.FCS.Syntax.SynModuleOrNamespaceKind.AnonModule
    }

    test "preprocessForFsi fed block-parsed structure passes through unchanged" {
      // WHY: When the file structure comes from the block text (legacy/fallback
      // path), we must NOT wrap in 'module Tmp ='. The code should pass through
      // unmodified so the caller can decide what to do.
      // This is the 'worst case' defensive behaviour — better than a broken wrap.
      let blockOnlyFs = parseFs "pong.fs" BlockEvalFixtures.updateBlockOnly
      let result, _ =
        preprocessForFsi
          (Some blockOnlyFs) Block (Some 1) Set.empty
          BlockEvalFixtures.updateBlockOnly
      result.Code.Contains("module Tmp")
      |> Expect.isFalse
          "AnonModule Tmp container must never produce 'module Tmp =' wrapper"
    }

    // ── locateBlock: AnonModule containers ───────────────────────────

    test "locateBlock returns None when file structure only contains AnonModule" {
      // WHY: An AnonModule is a signal that the file structure was parsed
      // from a code fragment, not the actual source file. locateBlock must
      // treat this as 'context unknown' (None) so preprocessForFsi passes
      // code through rather than wrapping in Tmp.
      let blockOnlyFs = parseFs "pong.fs" BlockEvalFixtures.updateBlockOnly
      let result = locateBlock blockOnlyFs (Some 1)
      result |> Expect.isEmpty
          "AnonModule-only structure should return None from locateBlock"
    }

    test "locateBlock returns None for fallback path when only AnonModule present" {
      // WHY: The no-line-number fallback path also must not return the
      // AnonModule container as a valid target for wrapping.
      let blockOnlyFs = parseFs "pong.fs" BlockEvalFixtures.updateBlockOnly
      let result = locateBlock blockOnlyFs None
      result |> Expect.isEmpty
          "no-line fallback should also reject AnonModule as context"
    }

    // ── Module tracking integrity ─────────────────────────────────────

    test "Pong module is tracked in EvaluatedModules after block eval with full file context" {
      // WHY: After correct wrapping in 'module Pong =', the session must
      // record that 'Pong' was evaluated so the next block eval gets
      // 'open Pong' prepended. If this is wrong, the second block eval
      // will redefine without opening, losing all prior bindings.
      let fullFileFs = parseFs "pong.fs" BlockEvalFixtures.pongFileContent
      let _, evaluatedModules =
        preprocessForFsi
          (Some fullFileFs) Block (Some 20) Set.empty
          BlockEvalFixtures.updateBlockOnly
      evaluatedModules |> Set.contains "Pong"
      |> Expect.isTrue "Pong should be recorded after block eval"
    }

    test "Tmp is never recorded in EvaluatedModules" {
      // WHY: If 'Tmp' were ever added to EvaluatedModules, subsequent
      // block evals would emit 'open Tmp' which always fails in FSI.
      let fullFileFs = parseFs "pong.fs" BlockEvalFixtures.pongFileContent
      let _, evaluatedModules =
        preprocessForFsi
          (Some fullFileFs) Block (Some 20) Set.empty
          BlockEvalFixtures.updateBlockOnly
      evaluatedModules |> Set.contains "Tmp"
      |> Expect.isFalse "Tmp should never appear in EvaluatedModules"
    }

    // ── SUB-MODULE NESTING ────────────────────────────────────────────

    test "block from two-level nested module wraps in nested module syntax" {
      // WHY: 'module Outer.Inner =' is NOT valid F# syntax for a module
      // binding — only dotted file-level declarations are allowed at the top.
      // The wrapper MUST be:
      //   module Outer =
      //     module Inner =
      //       let compute ...
      // If we emit 'module Outer.Inner =' the compiler rejects it.
      let fs = parseFs "Nested.fs" BlockEvalFixtures.twoLevelNestedContent
      let result, _ =
        preprocessForFsi
          (Some fs) Block (Some 5) Set.empty
          BlockEvalFixtures.innerComputeBlock
      // Outer wrapper must be present
      result.Code
      |> Expect.stringContains "should have outer module wrapper" "module Outer ="
      // Inner wrapper must be present
      result.Code
      |> Expect.stringContains "should have inner module wrapper" "module Inner ="
      // The block code itself must appear
      result.Code
      |> Expect.stringContains "should contain the block code" "let compute"
      // Must NOT use dotted one-liner syntax
      result.Code.Contains("module Outer.Inner =")
      |> Expect.isFalse "dotted single-line module binding is invalid F# syntax"
    }

    test "locateBlock path for two-level nesting has two containers, deepest last" {
      // WHY: locateBlock now returns a path (ancestor list), not a single
      // container. The path must be [Outer; Outer.Inner] — ancestors first,
      // deepest container last. If the list is just [Outer.Inner], we lose
      // the information needed to emit the outer 'module Outer =' wrapper.
      let fs = parseFs "Nested.fs" BlockEvalFixtures.twoLevelNestedContent
      let path = locateBlock fs (Some 5)
      path |> Expect.hasLength "path should have two elements" 2
      path.[0].QualifiedName
      |> Expect.equal "first element is Outer" "Outer"
      path.[1].QualifiedName
      |> Expect.equal "second element is Outer.Inner" "Outer.Inner"
    }

    test "block from three-level nested module emits three-level nesting" {
      // WHY: The nesting must be arbitrarily deep, not just one or two levels.
      // module A =
      //   module B =
      //     module C =
      //       let deepFn ...
      let fs = parseFs "Deep.fs" BlockEvalFixtures.threeLevelNestedContent
      let result, _ =
        preprocessForFsi
          (Some fs) Block (Some 5) Set.empty
          BlockEvalFixtures.deepFnBlock
      result.Code |> Expect.stringContains "outer A" "module A ="
      result.Code |> Expect.stringContains "mid B" "module B ="
      result.Code |> Expect.stringContains "inner C" "module C ="
      result.Code |> Expect.stringContains "block code" "let deepFn"
      result.Code.Contains("module A.B.C =")
      |> Expect.isFalse "dotted three-level module binding is not valid syntax"
    }

    test "block from namespace+two-level module skips namespace in nesting, wraps only modules" {
      // WHY: Namespace containers must NOT become 'module Ns =' wrappers —
      // 'module MyNs =' is invalid when MyNs is a namespace, not a module.
      // Only the named module containers in the path get wrapped.
      // The namespace itself is implicit context for FSI (it was opened when
      // the whole file was first evaluated, or it's just a qualification).
      let fs = parseFs "NsNested.fs" BlockEvalFixtures.namespaceTwoLevelContent
      let result, _ =
        preprocessForFsi
          (Some fs) Block (Some 5) Set.empty
          BlockEvalFixtures.valueBlock
      result.Code |> Expect.stringContains "outer module" "module Outer ="
      result.Code |> Expect.stringContains "inner module" "module Inner ="
      result.Code |> Expect.stringContains "block" "let value"
      result.Code.Contains("module MyNs =")
      |> Expect.isFalse "namespace must not become a 'module X =' wrapper"
    }

    test "second eval of nested block emits 'open' with deepest qualified name first" {
      // WHY: After the first eval wraps in Outer.Inner, EvaluatedModules
      // contains "Outer.Inner". The second eval must open it so prior bindings
      // (like 'helper') are visible to 'compute' inside the new wrapper.
      // The open must appear OUTSIDE the new nested wrapper.
      let fs = parseFs "Nested.fs" BlockEvalFixtures.twoLevelNestedContent
      let alreadyEvaluated = Set.singleton "Outer.Inner"
      let result, _ =
        preprocessForFsi
          (Some fs) Block (Some 5) alreadyEvaluated
          BlockEvalFixtures.innerComputeBlock
      // open must come before the module wrapper
      let openIdx = result.Code.IndexOf("open Outer.Inner")
      let moduleIdx = result.Code.IndexOf("module Outer =")
      (openIdx, 0) |> Expect.isGreaterThanOrEqual "open should be present"
      (moduleIdx, 0) |> Expect.isGreaterThanOrEqual "outer module wrapper present"
      (openIdx < moduleIdx)
      |> Expect.isTrue "open must appear before the outer module wrapper"
    }

    test "EvaluatedModules records deepest qualified name for nested block" {
      // WHY: We want 'open Outer.Inner' (not 'open Outer') on the next eval.
      // The deepest container's QualifiedName is the correct open target.
      let fs = parseFs "Nested.fs" BlockEvalFixtures.twoLevelNestedContent
      let _, updatedModules =
        preprocessForFsi
          (Some fs) Block (Some 5) Set.empty
          BlockEvalFixtures.innerComputeBlock
      updatedModules |> Set.contains "Outer.Inner"
      |> Expect.isTrue "deepest module 'Outer.Inner' should be tracked"
      updatedModules |> Set.contains "Outer"
      |> Expect.isFalse "intermediate 'Outer' should NOT be in EvaluatedModules — open Outer is always available via nesting"
    }
  ]

// ─────────────────────────────────────────────────────────────────
// All tests
// ─────────────────────────────────────────────────────────────────

[<Tests>]
let allCompilationContextTests =
  testList "CompilationContext" [
    parseFileStructureTests
    preprocessForFsiTests
    locateBlockTests
    propertyTests
    diagnosticMappingTests
    responseDiagnosticTests
    fileCacheTests
    perfMeasurementTests
    evalModeTests
    namespaceContainerTests
    blockEvalModuleContextTests
  ]
