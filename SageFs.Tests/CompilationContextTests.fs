module SageFs.Tests.CompilationContextTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Middleware.CompilationContext
open SageFs.WorkerProtocol

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
      let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
      fs.HasFileLevelModule |> Expect.isTrue "should detect file-level module"
      fs.Containers |> Expect.hasLength "should have one container" 1
      fs.Containers.[0].QualifiedName
      |> Expect.equal "should be GameOfLife" "GameOfLife"
    }

    test "parses namespace + named module" {
      let fs = parseFileStructure "Domain.fs" Fixtures.namespacePlusModule
      fs.HasFileLevelModule |> Expect.isFalse "namespace is not file-level module"
      fs.Containers |> Expect.hasLength "should have one namespace" 1
      fs.Containers.[0].QualifiedName
      |> Expect.equal "should be MyApp.Domain" "MyApp.Domain"
      fs.Containers.[0].Children |> Expect.hasLength "should have nested module" 1
      fs.Containers.[0].Children.[0].QualifiedName
      |> Expect.equal "should be MyApp.Domain.GameOfLife" "MyApp.Domain.GameOfLife"
    }

    test "parses namespace with multiple modules" {
      let fs = parseFileStructure "Domain.fs" Fixtures.namespaceMultiModule
      fs.Containers.[0].Children |> Expect.hasLength "should have 2 nested modules" 2
      fs.Containers.[0].Children.[0].QualifiedName
      |> Expect.equal "first module" "MyApp.Domain.GameOfLife"
      fs.Containers.[0].Children.[1].QualifiedName
      |> Expect.equal "second module" "MyApp.Domain.Rendering"
    }

    test "parses internal module" {
      let fs = parseFileStructure "Internal.fs" Fixtures.internalModule
      fs.HasFileLevelModule |> Expect.isTrue "should detect file-level module"
      fs.Containers.[0].AccessModifier
      |> Expect.isSome "should have access modifier"
    }

    test "parses file with no module or namespace" {
      let fs = parseFileStructure "Script.fsx" Fixtures.noDeclaration
      fs.HasFileLevelModule |> Expect.isFalse "no file-level module"
    }

    test "collects open statements" {
      let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
      fs.Containers.[0].Opens
      |> List.exists (fun o -> o.Contains("System"))
      |> Expect.isTrue "should have open System"
    }

    test "tracks declaration ranges" {
      let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
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
        let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
        let result, _ =
          preprocessForFsi (Some fs) (Some "file") None Set.empty
            Fixtures.fileLevelModule
        result.Code
        |> Expect.stringContains "should start with module decl" "module GameOfLife ="
        result.Code
        |> Expect.stringContains "body should be indented" "  type Grid"
        result.Code
        |> Expect.stringContains "functions indented" "  let randomGrid"
      }

      test "namespace file → namespace stripped" {
        let fs = parseFileStructure "Domain.fs" Fixtures.namespaceOnly
        let result, _ =
          preprocessForFsi (Some fs) (Some "file") None Set.empty
            Fixtures.namespaceOnly
        (result.Code.Contains("namespace MyApp.Domain"))
        |> Expect.isFalse "namespace should be stripped"
      }

      test "namespace + module file → namespace stripped, module preserved" {
        let fs = parseFileStructure "Domain.fs" Fixtures.namespacePlusModule
        let result, _ =
          preprocessForFsi (Some fs) (Some "file") None Set.empty
            Fixtures.namespacePlusModule
        (result.Code.Contains("namespace MyApp.Domain"))
        |> Expect.isFalse "namespace should be stripped"
        result.Code
        |> Expect.stringContains "module should be preserved" "module GameOfLife ="
      }

      test "no declaration file → pass through unchanged" {
        let fs = parseFileStructure "Script.fsx" Fixtures.noDeclaration
        let result, _ =
          preprocessForFsi (Some fs) (Some "file") None Set.empty
            Fixtures.noDeclaration
        result.Code
        |> Expect.equal "should be unchanged" Fixtures.noDeclaration
      }

      test "internal module → access modifier preserved" {
        let fs = parseFileStructure "Internal.fs" Fixtures.internalModule
        let result, _ =
          preprocessForFsi (Some fs) (Some "file") None Set.empty
            Fixtures.internalModule
        result.Code
        |> Expect.stringContains "should have internal" "internal"
        result.Code
        |> Expect.stringContains "should have =" "="
      }

      test "whole file marks all modules as evaluated" {
        let fs = parseFileStructure "Domain.fs" Fixtures.namespaceMultiModule
        let _, mods =
          preprocessForFsi (Some fs) (Some "file") None Set.empty
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
        let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
        let result, evaluatedModules =
          preprocessForFsi (Some fs) (Some "block") None Set.empty
            Fixtures.blockFromModule
        result.Code
        |> Expect.stringContains "should have module wrapper" "module GameOfLife ="
        result.Code
        |> Expect.stringContains "body inside module" "  let step"
        evaluatedModules |> Set.contains "GameOfLife"
        |> Expect.isTrue "should track module"
      }

      test "second block from same module → gets open prepended" {
        let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
        let alreadyEvald = Set.singleton "GameOfLife"
        let result, _ =
          preprocessForFsi (Some fs) (Some "block") None alreadyEvald
            Fixtures.blockFromModule
        result.Code
        |> Expect.stringContains "should have open" "open GameOfLife"
        result.Code
        |> Expect.stringContains "should have module" "module GameOfLife ="
      }

      test "block from namespace+module → wrapped with qualified name" {
        let fs = parseFileStructure "Domain.fs" Fixtures.namespacePlusModule
        let result, evaluatedModules =
          preprocessForFsi (Some fs) (Some "block") (Some 7) Set.empty
            Fixtures.blockFromNamespacedModule
        result.Code
        |> Expect.stringContains "should use qualified name"
            "module MyApp.Domain.GameOfLife ="
        evaluatedModules |> Set.contains "MyApp.Domain.GameOfLife"
        |> Expect.isTrue "should track qualified"
      }

      test "block with no file context → pass through unchanged" {
        let result, _ =
          preprocessForFsi None (Some "block") None Set.empty "let x = 42"
        result.Code
        |> Expect.equal "should be unchanged" "let x = 42"
        result.LineOffset
        |> Expect.equal "no offset" 0
      }
    ]

    testList "line offset tracking" [

      test "whole-file module transform has zero line offset" {
        let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
        let result, _ =
          preprocessForFsi (Some fs) (Some "file") None Set.empty
            Fixtures.fileLevelModule
        result.LineOffset
        |> Expect.equal "module replacement has zero line offset" 0
        result.ColumnOffset
        |> Expect.equal "body indented by 2" 2
      }

      test "block wrap with prior eval adds lines for open + module" {
        let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
        let alreadyEvald = Set.singleton "GameOfLife"
        let result, _ =
          preprocessForFsi (Some fs) (Some "block") None alreadyEvald
            Fixtures.blockFromModule
        // open GameOfLife (1) + open System (1) + module GameOfLife = (1) = 3 lines
        (result.LineOffset, 0)
        |> Expect.isGreaterThan "should add lines"
        result.ColumnOffset
        |> Expect.equal "body indented by 2" 2
      }

      test "first block wrap adds opens + module line" {
        let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
        let result, _ =
          preprocessForFsi (Some fs) (Some "block") None Set.empty
            Fixtures.blockFromModule
        let expectedOpens = fs.Containers.[0].Opens.Length
        result.LineOffset
        |> Expect.equal "opens + module line" (expectedOpens + 1)
      }

      test "no-op transform has zero offsets" {
        let result, _ =
          preprocessForFsi None None None Set.empty "let x = 42"
        result.LineOffset |> Expect.equal "zero" 0
        result.ColumnOffset |> Expect.equal "zero" 0
      }
    ]

    testList "heuristic eval mode detection" [

      test "code starting with file-level module detected as whole file" {
        let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
        let result, _ =
          preprocessForFsi (Some fs) None None Set.empty
            Fixtures.fileLevelModule
        result.Code
        |> Expect.stringContains "should transform" "module GameOfLife ="
      }

      test "code without module/namespace detected as block" {
        let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
        let result, _ =
          preprocessForFsi (Some fs) None None Set.empty "let x = 42"
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
      let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
      let container = locateBlock fs (Some 8) "let randomGrid w h density ="
      container |> Expect.isSome "should find container"
      container.Value.QualifiedName
      |> Expect.equal "should be GameOfLife" "GameOfLife"
    }

    test "locates block in nested module by line number" {
      let fs = parseFileStructure "Domain.fs" Fixtures.namespacePlusModule
      let container = locateBlock fs (Some 7) "let step grid = grid"
      container |> Expect.isSome "should find nested module"
      container.Value.QualifiedName
      |> Expect.equal "should be qualified" "MyApp.Domain.GameOfLife"
    }

    test "locates block in second module of multi-module file" {
      let fs = parseFileStructure "Domain.fs" Fixtures.namespaceMultiModule
      let container = locateBlock fs (Some 10) """let render grid = sprintf "%A" grid"""
      container |> Expect.isSome "should find Rendering"
      container.Value.QualifiedName
      |> Expect.equal "should be Rendering" "MyApp.Domain.Rendering"
    }

    test "falls back to top-level when no line number" {
      let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
      let container = locateBlock fs None "let step grid = grid"
      container |> Expect.isSome "should fall back to top-level"
      container.Value.QualifiedName
      |> Expect.equal "should be GameOfLife" "GameOfLife"
    }

    test "namespace+module with no line falls back to single child" {
      let fs = parseFileStructure "Domain.fs" Fixtures.namespacePlusModule
      let container = locateBlock fs None "let step grid = grid"
      container |> Expect.isSome "should find single child"
      container.Value.QualifiedName
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
        let result, _ = preprocessForFsi None None None Set.empty code
        result.Code
        |> Expect.equal
            (sprintf "identity for: %s..." (code.Substring(0, min 20 code.Length)))
            code
    }

    test "line offset consistency for whole-file module" {
      let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
      let result, _ =
        preprocessForFsi (Some fs) (Some "file") None Set.empty
          Fixtures.fileLevelModule
      let originalLines = Fixtures.fileLevelModule.Split('\n').Length
      let transformedLines = result.Code.Split('\n').Length
      (transformedLines - originalLines)
      |> Expect.equal "line diff matches offset" result.LineOffset
    }

    test "evaluated modules accumulate correctly" {
      let fs = parseFileStructure "GameOfLife.fs" Fixtures.fileLevelModule
      let _, mods1 =
        preprocessForFsi (Some fs) (Some "block") None Set.empty "let x = 1"
      mods1 |> Set.contains "GameOfLife"
      |> Expect.isTrue "should add module on first eval"
      let _, mods2 =
        preprocessForFsi (Some fs) (Some "block") None mods1 "let y = 2"
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
// parseFileStructureCached tests
// ─────────────────────────────────────────────────────────────────

let fileCacheTests =
  testList "parseFileStructureCached" [
    test "cache miss parses and stores" {
      let code = Fixtures.fileLevelModule
      let emptyCache = Map.empty
      let fs, cache = parseFileStructureCached "Test.fs" code emptyCache
      fs.Containers |> Expect.isNonEmpty "should have containers"
      cache |> Map.containsKey "Test.fs"
      |> Expect.isTrue "should be cached"
    }

    test "cache hit returns same structure without re-parse" {
      let code = Fixtures.fileLevelModule
      let fs1, cache1 = parseFileStructureCached "Test.fs" code Map.empty
      let fs2, cache2 = parseFileStructureCached "Test.fs" code cache1
      fs2.Containers.Length
      |> Expect.equal "same container count" fs1.Containers.Length
      cache2 |> Map.count
      |> Expect.equal "still one entry" 1
    }

    test "cache invalidates on content change" {
      let code1 = Fixtures.fileLevelModule
      let _, cache1 = parseFileStructureCached "Test.fs" code1 Map.empty
      let code2 = Fixtures.namespacePlusModule
      let fs2, cache2 = parseFileStructureCached "Test.fs" code2 cache1
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
      let _, cache1 = parseFileStructureCached "A.fs" code Map.empty
      let _, cache2 = parseFileStructureCached "B.fs" code cache1
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
  ]
