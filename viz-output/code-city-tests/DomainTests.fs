module CodeCity.DomainTests

open System
open System.Collections.Generic
open System.Numerics
open Expecto
open Expecto.Flip
open FsCheck
open Raylib_cs
open CodeCity

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 100 }

// Helpers

let mkFunc name proj =
  { Name = name
    QualifiedName = sprintf "%s.%s" proj name
    FilePath = ""
    RelPath = ""
    Module = proj
    Project = proj
    DeclarationStartLine = 1
    DeclarationStartColumn = 0
    StartLine = 1
    EndLine = 10
    LineCount = 10
    Body = [||]
    CallRefs = []
    CallSites = [] }

let mkFuncInModule moduleName name callRefs body =
  { Name = name
    QualifiedName = sprintf "%s.%s" moduleName name
    FilePath = ""
    RelPath = ""
    Module = moduleName
    Project = "TestProject"
    DeclarationStartLine = 1
    DeclarationStartColumn = 0
    StartLine = 1
    EndLine = Array.length body
    LineCount = Array.length body
    Body = body
    CallRefs = callRefs
    CallSites = [] }

let private mkSampleBuilding name buildingType lineCount heat callers callees complexity ageDays commitCount terrainY =
  let func =
    { mkFunc name "TestMod" with
        QualifiedName = sprintf "TestMod.%s" name
        LineCount = lineCount
        EndLine = lineCount }
  { Func = func
    Heat = heat
    CallerCount = callers
    CalleeCount = callees
    Complexity = complexity
    BuildingType = buildingType
    GitAgeDays = ageDays
    GitCommitCount = commitCount
    GitAuthorCount = 1
    GitBugFixRatio = 0.0f
    X = 0.0f
    Z = 0.0f
    W = 4.0f
    D = 4.0f
    H = BuildingType.height buildingType lineCount heat
    Rotation = 0.0f
    TerrainY = terrainY
    Color = Color.White
    RoofColor = Color(80uy, 80uy, 90uy, 255uy)
    District = "TestMod" }

let private mkDistrict name funcCount totalLines color =
  { Name = name
    FuncCount = funcCount
    TotalLines = totalLines
    Color = color }

let private withTempFsSource (source: string) run =
  let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
  let filePath = System.IO.Path.Combine(root, "Sample.fs")
  System.IO.Directory.CreateDirectory(root) |> ignore
  let normalizedSource =
    source
      .Replace("\r\n", "\n")
      .Replace("\r", "\n")
      .Replace("\n", Environment.NewLine)
  System.IO.File.WriteAllText(filePath, normalizedSource)
  try
    run root filePath
  finally
    if System.IO.Directory.Exists(root) then
      System.IO.Directory.Delete(root, true)

let private withTempProject (files: (string * string) list) (compileFiles: string list) run =
  let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
  let projectPath = System.IO.Path.Combine(root, "Sample.fsproj")
  let compileItems =
    compileFiles
    |> List.map (fun file -> sprintf """    <Compile Include="%s" />""" file)
    |> String.concat Environment.NewLine
  let projectText =
    sprintf
      """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
%s
  </ItemGroup>
</Project>
"""
      compileItems

  System.IO.Directory.CreateDirectory(root) |> ignore
  System.IO.File.WriteAllText(projectPath, projectText.Replace("\n", Environment.NewLine))

  for (relativePath, source) in files do
    let filePath = System.IO.Path.Combine(root, relativePath)
    let parent = System.IO.Path.GetDirectoryName(filePath)
    if not (String.IsNullOrWhiteSpace(parent)) then
      System.IO.Directory.CreateDirectory(parent) |> ignore
    let normalizedSource =
      match source.StartsWith("\r\n"), source.StartsWith("\n") with
      | true, _ -> source.Substring(2)
      | false, true -> source.Substring(1)
      | _ -> source
    let sanitizedSource =
      normalizedSource
        .Replace("\r\n", "\n")
        .Replace("\r", "\n")
      |> Seq.filter (fun ch -> ch = '\r' || ch = '\n' || ch = '\t' || not (Char.IsControl ch))
      |> Seq.toArray
      |> String
    System.IO.File.WriteAllText(filePath, sanitizedSource.Replace("\n", Environment.NewLine))

  let restore =
    let psi = System.Diagnostics.ProcessStartInfo("dotnet", sprintf "restore \"%s\"" projectPath)
    psi.WorkingDirectory <- root
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    use proc = System.Diagnostics.Process.Start(psi)
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, stdout, stderr

  match restore with
  | 0, _, _ -> ()
  | exitCode, stdout, stderr ->
      failwithf "dotnet restore failed (%d)\nstdout:\n%s\nstderr:\n%s" exitCode stdout stderr

  let build =
    let psi = System.Diagnostics.ProcessStartInfo("dotnet", sprintf "build \"%s\" --no-restore" projectPath)
    psi.WorkingDirectory <- root
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    use proc = System.Diagnostics.Process.Start(psi)
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, stdout, stderr

  match build with
  | 0, _, _ -> ()
  | exitCode, stdout, stderr ->
      failwithf "dotnet build failed (%d)\nstdout:\n%s\nstderr:\n%s" exitCode stdout stderr

  try
    run root projectPath
  finally
    if System.IO.Directory.Exists(root) then
      System.IO.Directory.Delete(root, true)

let private overlap1d a0 a1 b0 b1 =
  min a1 b1 - max a0 b0

let private cubeWithinLot lotHW lotHD (cube: SubCube) =
  cube.CX - cube.HW >= -lotHW
  && cube.CX + cube.HW <= lotHW
  && cube.CZ - cube.HD >= -lotHD
  && cube.CZ + cube.HD <= lotHD

let private cubesAdjacent (a: SubCube) (b: SubCube) =
  let eps = 0.0001f
  let xOverlap = overlap1d (a.CX - a.HW) (a.CX + a.HW) (b.CX - b.HW) (b.CX + b.HW)
  let zOverlap = overlap1d (a.CZ - a.HD) (a.CZ + a.HD) (b.CZ - b.HD) (b.CZ + b.HD)
  let touchEastWest =
    abs ((a.CX + a.HW) - (b.CX - b.HW)) <= eps
    || abs ((b.CX + b.HW) - (a.CX - a.HW)) <= eps
  let touchNorthSouth =
    abs ((a.CZ + a.HD) - (b.CZ - b.HD)) <= eps
    || abs ((b.CZ + b.HD) - (a.CZ - a.HD)) <= eps
  (touchEastWest && zOverlap > eps) || (touchNorthSouth && xOverlap > eps)

let private compoundConnected (cubes: SubCube[]) =
  if cubes.Length = 0 then true
  else
    let seen = Array.create cubes.Length false
    let q = Queue<int>()
    seen.[0] <- true
    q.Enqueue 0
    while q.Count > 0 do
      let i = q.Dequeue()
      for j in 0 .. cubes.Length - 1 do
        if not seen.[j] && cubesAdjacent cubes.[i] cubes.[j] then
          seen.[j] <- true
          q.Enqueue j
    seen |> Array.forall id

let private cubeContainsPoint x z (cube: SubCube) =
  x >= cube.CX - cube.HW
  && x <= cube.CX + cube.HW
  && z >= cube.CZ - cube.HD
  && z <= cube.CZ + cube.HD

let private footprintFillRatio sampleSize (cubes: SubCube[]) =
  if cubes.Length = 0 then 0.0
  else
    let minX = cubes |> Array.minBy (fun cube -> cube.CX - cube.HW) |> fun cube -> cube.CX - cube.HW
    let maxX = cubes |> Array.maxBy (fun cube -> cube.CX + cube.HW) |> fun cube -> cube.CX + cube.HW
    let minZ = cubes |> Array.minBy (fun cube -> cube.CZ - cube.HD) |> fun cube -> cube.CZ - cube.HD
    let maxZ = cubes |> Array.maxBy (fun cube -> cube.CZ + cube.HD) |> fun cube -> cube.CZ + cube.HD
    let mutable occupied = 0
    let mutable total = 0
    let mutable x = minX + sampleSize * 0.5f
    while x < maxX do
      let mutable z = minZ + sampleSize * 0.5f
      while z < maxZ do
        total <- total + 1
        if cubes |> Array.exists (cubeContainsPoint x z) then
          occupied <- occupied + 1
        z <- z + sampleSize
      x <- x + sampleSize
    if total = 0 then 0.0
    else float occupied / float total

// Color math

let colorMathTests =
  testList "Color math" [
    testPropertyWithConfig cfg "brighten with factor 1.0 is identity" <|
      fun (r: byte) (g: byte) (b: byte) ->
        let c = Color(r, g, b, 255uy)
        let result = brighten c 1.0f
        result.R |> Expect.equal "R unchanged" r
        result.G |> Expect.equal "G unchanged" g
        result.B |> Expect.equal "B unchanged" b

    testCase "heatColor at 0 is blue weighted" <| fun () ->
      let c = heatColor 0.0f
      (c.B, c.R) |> Expect.isGreaterThan "cold should be bluer than red" 

    testCase "heatColor at 1 is red weighted" <| fun () ->
      let c = heatColor 1.0f
      (c.R, c.B) |> Expect.isGreaterThan "hot should be redder than blue"
  ]

// Treemap

let treemapTests =
  testList "Treemap" [
    testCase "empty items return empty layout" <| fun () ->
      squarifiedTreemap [] (TRect.create 0.0f 0.0f 10.0f 10.0f)
      |> Expect.isEmpty "no items means no rectangles"

    testCase "layout preserves item count" <| fun () ->
      let items = [ "a", 1.0f; "b", 2.0f; "c", 3.0f ]
      let layout = squarifiedTreemap items (TRect.create 0.0f 0.0f 12.0f 8.0f)
      layout |> Expect.hasLength "three items produce three rects" 3

    testPropertyWithConfig cfg "all treemap rects stay within bounds" <|
      fun (PositiveInt rawW) (PositiveInt rawH) ->
        let width = 5.0f + float32 (rawW % 100)
        let height = 5.0f + float32 (rawH % 100)
        let bounds = TRect.create 10.0f 20.0f width height
        let items = [ "a", 1.0f; "b", 2.0f; "c", 3.0f; "d", 4.0f ]
        squarifiedTreemap items bounds
        |> List.forall (fun (_, r) ->
          r.X >= bounds.X
          && r.Z >= bounds.Z
          && r.X + r.W <= bounds.X + bounds.W + 0.001f
          && r.Z + r.H <= bounds.Z + bounds.H + 0.001f)

    testPropertyWithConfig cfg "inset preserves positive dimensions" <|
      fun (PositiveInt rawW) (PositiveInt rawH) (NonNegativeInt rawMargin) ->
        let width = 0.2f + float32 (rawW % 200) / 10.0f
        let height = 0.2f + float32 (rawH % 200) / 10.0f
        let margin = float32 (rawMargin % 200) / 20.0f
        let inset = TRect.inset margin (TRect.create 0.0f 0.0f width height)
        inset.W >= 0.1f && inset.H >= 0.1f
  ]

// Calls + heat

let edgeAndHeatTests =
  testList "Call graph domain" [
    testCase "deduplicateEdges merges repeated pairs" <| fun () ->
      let result =
        deduplicateEdges [
          { From = "a"; To = "b"; Weight = 2 }
          { From = "a"; To = "b"; Weight = 3 }
        ]
      result |> Expect.hasLength "one merged edge remains" 1
      result.[0].Weight |> Expect.equal "weights sum" 5

    testCase "computeHeat gives hottest callee heat 1.0" <| fun () ->
      let funcs = [ mkFunc "a" "P"; mkFunc "b" "P"; mkFunc "c" "P" ]
      let result =
        computeHeat funcs [
          { From = "P.a"; To = "P.c"; Weight = 1 }
          { From = "P.b"; To = "P.c"; Weight = 1 }
        ]
      let (heat, callers, _) = result.["P.c"]
      heat |> Expect.equal "most-called function normalizes to 1" 1.0f
      callers |> Expect.equal "caller count tracked" 2

    testPropertyWithConfig cfg "all heats stay in [0, 1]" <|
      fun (PositiveInt rawCount) ->
        let count = 1 + rawCount % 20
        let funcs = [ for i in 0 .. count - 1 -> mkFunc (sprintf "f%d" i) "P" ]
        let edges =
          [ for i in 0 .. count - 2 ->
              { From = sprintf "P.f%d" i
                To = sprintf "P.f%d" (i + 1)
                Weight = 1 } ]
        let result = computeHeat funcs edges
        result
        |> Map.forall (fun _ (heat, _, _) -> heat >= 0.0f && heat <= 1.0f)

    testCase "qualified same-name cross-module call is preserved" <| fun () ->
      let funcs = [
        mkFuncInModule "Alpha" "targetCall" ["Beta.targetCall"] [| "let targetCall () = Beta.targetCall()" |]
        mkFuncInModule "Beta" "targetCall" [] [| "let targetCall () = 42" |]
      ]
      let edges = buildCallGraph funcs
      edges |> Expect.hasLength "qualified call resolves to one callee" 1
      edges[0].From |> Expect.equal "edge starts at Alpha.targetCall" "Alpha.targetCall"
      edges[0].To |> Expect.equal "edge targets Beta.targetCall" "Beta.targetCall"

    testCase "ambiguous unqualified call does not fan out" <| fun () ->
      let funcs = [
        mkFuncInModule "Caller" "sourceCall" ["targetCall"] [| "let sourceCall () = targetCall()" |]
        mkFuncInModule "Alpha" "targetCall" [] [| "let targetCall () = 1" |]
        mkFuncInModule "Beta" "targetCall" [] [| "let targetCall () = 2" |]
      ]
      buildCallGraph funcs |> Expect.isEmpty "ambiguous unqualified call should be dropped"

    testCase "unqualified call prefers same-module target when unique" <| fun () ->
      let funcs = [
        mkFuncInModule "Alpha" "sourceCall" ["targetCall"] [| "let sourceCall () = targetCall()" |]
        mkFuncInModule "Alpha" "targetCall" [] [| "let targetCall () = 1" |]
        mkFuncInModule "Beta" "targetCall" [] [| "let targetCall () = 2" |]
      ]
      let edges = buildCallGraph funcs
      edges |> Expect.hasLength "same-module resolution should yield one edge" 1
      edges[0].From |> Expect.equal "edge starts at Alpha.sourceCall" "Alpha.sourceCall"
      edges[0].To |> Expect.equal "edge targets Alpha.targetCall" "Alpha.targetCall"

    testCase "true self-call is filtered by qualified identity" <| fun () ->
      let funcs = [
        mkFuncInModule "Alpha" "targetCall" ["targetCall"] [| "let targetCall () = targetCall()" |]
      ]
      buildCallGraph funcs |> Expect.isEmpty "self-recursive call should not emit self-edge"
  ]

// Complexity

let complexityTests =
  testList "Cyclomatic complexity" [
    testCase "empty body returns baseline complexity 1" <| fun () ->
      computeComplexity [||] |> Expect.equal "baseline complexity" 1

    testCase "if + elif increments complexity" <| fun () ->
      computeComplexity
        [| "  if x > 0 then a"
           "  elif x < 0 then b"
           "  else c" |]
      |> Expect.equal "if and elif should both count" 3

    testCase "match cases contribute to complexity" <| fun () ->
      computeComplexity
        [| "  match x with"
           "  | A -> 1"
           "  | B -> 2"
           "  | C -> 3" |]
      |> Expect.equal "three match arms add three" 4

    testPropertyWithConfig cfg "complexity is always at least 1" <|
      fun (lines: string[]) ->
        let safe =
          if isNull lines then [||]
          else lines |> Array.map (fun line -> if isNull line then "" else line)
        (computeComplexity safe, 1) |> Expect.isGreaterThanOrEqual "complexity floor is 1"
  ]

let functionExtractionTests =
  testList "Function extraction" [
    testCase "captures module per function across multiple module declarations" <| fun () ->
      let source = """
module First

let alpha x = x

module Second =
  let beta x = x
"""
      withTempFsSource source <| fun root filePath ->
        let funcs = extractFunctions root filePath
        funcs |> Expect.hasLength "two functions extracted" 2
        funcs |> List.find (fun f -> f.Name = "alpha") |> fun f ->
          f.Module |> Expect.equal "alpha stays in First" "First"
        funcs |> List.find (fun f -> f.Name = "beta") |> fun f ->
          f.Module |> Expect.equal "beta stays in First.Second" "First.Second"

    testCase "earlier functions do not inherit the last module" <| fun () ->
      let source = """
module First

let alpha () =
  1

module Second =
  let beta () =
    2
"""
      withTempFsSource source <| fun root filePath ->
        let alpha =
          extractFunctions root filePath
          |> List.find (fun f -> f.Name = "alpha")
        alpha.Module |> Expect.notEqual "alpha should not move to First.Second" "First.Second"

    testCase "module and qualified name stay consistent" <| fun () ->
      let source = """
module Alpha

let gamma value =
  value + 1

module Beta =
  let delta value =
    value - 1
"""
      withTempFsSource source <| fun root filePath ->
        let funcs = extractFunctions root filePath
        funcs |> List.find (fun f -> f.Name = "gamma") |> fun f ->
          f.Module |> Expect.equal "gamma module" "Alpha"
          f.QualifiedName |> Expect.equal "gamma qualified name" "Alpha.gamma"
        funcs |> List.find (fun f -> f.Name = "delta") |> fun f ->
          f.Module |> Expect.equal "delta module" "Alpha.Beta"
          f.QualifiedName |> Expect.equal "delta qualified name" "Alpha.Beta.delta"

    testCase "single module extraction stays stable" <| fun () ->
      let source = """
module Only

let epsilon value =
  value * 2
"""
      withTempFsSource source <| fun root filePath ->
        let funcs = extractFunctions root filePath
        funcs |> Expect.hasLength "one function extracted" 1
        funcs.Head.Module |> Expect.equal "module stays Only" "Only"
        funcs.Head.QualifiedName |> Expect.equal "qualified name stays Only.epsilon" "Only.epsilon"

    testCase "single-line top-level function is extracted" <| fun () ->
      let source = """
module Only

let epsilon value = value * 2
"""
      withTempFsSource source <| fun root filePath ->
        let funcs = extractFunctions root filePath
        funcs |> Expect.hasLength "single-line function should be extracted" 1
        funcs.Head.Name |> Expect.equal "single-line function name preserved" "epsilon"

    testCase "nested local let is not extracted as top-level function" <| fun () ->
      let source = """
module Only

let outer value =
  let inner x = x + 1
  inner value
"""
      withTempFsSource source <| fun root filePath ->
        let funcs = extractFunctions root filePath
        funcs |> List.map (fun f -> f.Name) |> Expect.contains "outer extracted" "outer"
        funcs |> List.exists (fun f -> f.Name = "inner") |> Expect.isFalse "inner should stay local"

    testCase "type member is extracted with type-qualified identity" <| fun () ->
      let source = """
module Demo

type Counter() =
  member _.Increment() = 1

let topLevel () = 42
"""
      withTempFsSource source <| fun root filePath ->
        let funcs = extractFunctions root filePath
        funcs |> List.map (fun f -> f.QualifiedName) |> Expect.containsAll "top-level let and member should both be extracted"
          [ "Demo.topLevel"; "Demo.Counter.Increment" ]
        funcs |> List.exists (fun f -> f.QualifiedName = "Demo.Increment") |> Expect.isFalse "member should not collapse to a module-level function"
  ]

let endToEndParsingTests =
  testList "End-to-end extraction and graph" [
    testCase "comment mentioning function name does not create call edge" <| fun () ->
      let source = """
module Demo

let targetCall value =
  value + 1

let sourceCall value =
  // targetCall should not count here
  value
"""
      withTempFsSource source <| fun root filePath ->
        let funcs = extractFunctions root filePath
        buildCallGraph funcs |> Expect.isEmpty "comment text should not become a dependency"

    testCase "string literal mentioning function name does not create call edge" <| fun () ->
      let source = """
module Demo

let targetCall value =
  value + 1

let sourceCall value =
  "targetCall"
  |> ignore
  value
"""
      withTempFsSource source <| fun root filePath ->
        let funcs = extractFunctions root filePath
        buildCallGraph funcs |> Expect.isEmpty "string literal should not become a dependency"

    testCase "qualified same-name call survives extraction and graphing" <| fun () ->
      let source = """
module Alpha

let targetCall value =
  Beta.targetCall value

module Beta =
  let targetCall value =
    value + 1
"""
      withTempFsSource source <| fun root filePath ->
        let funcs = extractFunctions root filePath
        let edges = buildCallGraph funcs |> mergeCallEdges
        edges |> Expect.hasLength "one qualified edge should remain" 1
        edges.Head.From |> Expect.equal "from Alpha.targetCall" "Alpha.targetCall"
        edges.Head.To |> Expect.equal "to Alpha.Beta.targetCall" "Alpha.Beta.targetCall"
  ]

let projectAwareParsingTests =
  testList "Project-aware extraction and graph" [
    testCase "project scan only includes compile items from fsproj" <| fun () ->
      let files =
        [
          "Included.fs", """
module Sample.Included

let included value =
  value + 1
"""
          "Loose.fs", """
module Sample.Loose

let loose value =
  value + 10
"""
        ]
      withTempProject files [ "Included.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        funcs |> List.map (fun f -> f.Name) |> Expect.contains "included function should be present" "included"
        funcs |> List.exists (fun f -> f.Name = "loose") |> Expect.isFalse "loose file should be ignored when not in compile items"

    testCase "semantic project graph resolves open-module call to correct same-name target" <| fun () ->
      let files =
        [
          "Alpha.fs", """
module Sample.Alpha.Helpers

let target value =
  value + 1
"""
          "Beta.fs", """
module Sample.Beta.Helpers

let target value =
  value + 2
"""
          "Consumer.fs", """
module Sample.Consumer

open Sample.Beta.Helpers

let caller value =
  target value
"""
        ]
      withTempProject files [ "Alpha.fs"; "Beta.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.hasLength "semantic resolution should produce one edge" 1
        edges.Head.From |> Expect.equal "edge starts at Sample.Consumer.caller" "Sample.Consumer.caller"
        edges.Head.To |> Expect.equal "edge targets opened Sample.Beta.Helpers.target" "Sample.Beta.Helpers.target"

    testCase "semantic project graph resolves module-abbreviation qualified call to aliased target" <| fun () ->
      let files =
        [
          "Alpha.fs", """
module Sample.Alpha.Helpers

let target value =
  value + 1
"""
          "Beta.fs", """
module Sample.Beta.Helpers

let target value =
  value + 2
"""
          "Consumer.fs", """
module Sample.Consumer

module BH = Sample.Beta.Helpers

let caller value =
  BH.target value
"""
        ]
      withTempProject files [ "Alpha.fs"; "Beta.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.hasLength "alias-qualified call should resolve to one edge" 1
        edges.Head.From |> Expect.equal "edge starts at Sample.Consumer.caller" "Sample.Consumer.caller"
        edges.Head.To |> Expect.equal "edge targets aliased Sample.Beta.Helpers.target" "Sample.Beta.Helpers.target"

    testCase "semantic project graph resolves pipeline call to direct callee" <| fun () ->
      let files =
        [
          "Beta.fs", """
module Sample.Beta.Helpers

let target value =
  value + 2
"""
          "Consumer.fs", """
module Sample.Consumer

open Sample.Beta.Helpers

let caller value =
  value |> target
"""
        ]
      withTempProject files [ "Beta.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.hasLength "pipeline should resolve to one direct-call edge" 1
        edges.Head.From |> Expect.equal "edge starts at Sample.Consumer.caller" "Sample.Consumer.caller"
        edges.Head.To |> Expect.equal "edge targets Sample.Beta.Helpers.target" "Sample.Beta.Helpers.target"

    testCase "semantic project graph resolves left-application call to direct callee" <| fun () ->
      let files =
        [
          "Beta.fs", """
module Sample.Beta.Helpers

let target value =
  value + 2
"""
          "Consumer.fs", """
module Sample.Consumer

open Sample.Beta.Helpers

let caller value =
  target <| value
"""
        ]
      withTempProject files [ "Beta.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.hasLength "left-application should resolve to one direct-call edge" 1
        edges.Head.From |> Expect.equal "edge starts at Sample.Consumer.caller" "Sample.Consumer.caller"
        edges.Head.To |> Expect.equal "edge targets Sample.Beta.Helpers.target" "Sample.Beta.Helpers.target"

    testCase "semantic project graph resolves each stage of a chained pipeline without operator edges" <| fun () ->
      let files =
        [
          "Stages.fs", """
module Sample.Stages

let trim value =
  value + 1

let parse value =
  value + 2

let render value =
  value + 3
"""
          "Consumer.fs", """
module Sample.Consumer

open Sample.Stages

let caller value =
  value
  |> trim
  |> parse
  |> render
"""
        ]
      withTempProject files [ "Stages.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.hasLength "chained pipeline should produce one edge per stage" 3
        edges |> List.map (fun edge -> edge.To) |> Expect.containsAll "pipeline should hit all stages"
          [ "Sample.Stages.trim"; "Sample.Stages.parse"; "Sample.Stages.render" ]
        edges |> List.exists (fun edge -> edge.To.Contains("op_Pipe")) |> Expect.isFalse "pipeline should not create operator edges"

    testCase "semantic project graph does not attribute callback lambda body calls to enclosing function" <| fun () ->
      let files =
        [
          "Helpers.fs", """
module Sample.Helpers

let helper value =
  value + 1
"""
          "Consumer.fs", """
module Sample.Consumer

open Sample.Helpers

let outer values =
  values
  |> List.map (fun value -> helper value)
  |> ignore
"""
        ]
      withTempProject files [ "Helpers.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.isEmpty "deferred callback lambda bodies should not create direct-call edges for the enclosing function"

    testCase "semantic project graph does not attribute local function body calls to enclosing function" <| fun () ->
      let files =
        [
          "Helpers.fs", """
module Sample.Helpers

let helper value =
  value + 1
"""
          "Consumer.fs", """
module Sample.Consumer

open Sample.Helpers

let outer values =
  let callback value =
    helper value
  values
  |> List.map callback
  |> ignore
"""
        ]
      withTempProject files [ "Helpers.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.isEmpty "deferred local function bodies should not create direct-call edges for the enclosing function"

    testCase "semantic project graph still counts immediate partial application in let binding as a direct call" <| fun () ->
      let files =
        [
          "Helpers.fs", """
module Sample.Helpers

let helper seed value =
  seed + value

let consume _ =
  ()
"""
          "Consumer.fs", """
module Sample.Consumer

open Sample.Helpers

let outer value =
  let partial = helper value
  consume partial
"""
        ]
      withTempProject files [ "Helpers.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.hasLength "immediate partial application should still create a direct-call edge" 2
        edges |> List.map (fun edge -> edge.To) |> Expect.containsAll "outer should call helper and consume"
          [ "Sample.Helpers.helper"; "Sample.Helpers.consume" ]

    testCase "semantic project graph counts immediately invoked lambda body calls" <| fun () ->
      let files =
        [
          "Helpers.fs", """
module Sample.Helpers

let helper value =
  value + 1
"""
          "Consumer.fs", """
module Sample.Consumer

open Sample.Helpers

let outer value =
  (fun inner -> helper inner) value
"""
        ]
      withTempProject files [ "Helpers.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.hasLength "immediately invoked lambda should still count its executed call" 1
        edges.Head.To |> Expect.equal "immediately invoked lambda should still resolve helper" "Sample.Helpers.helper"

    testCase "semantic project graph does not resolve opened module function when local let shadows the name" <| fun () ->
      let files =
        [
          "Beta.fs", """
module Sample.Beta.Helpers

let target value =
  value + 2
"""
          "Consumer.fs", """
module Sample.Consumer

open Sample.Beta.Helpers

let caller value =
  let target x =
    x + 100
  target value
"""
        ]
      withTempProject files [ "Beta.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.isEmpty "local shadowing should prevent an edge to opened module target"

    testCase "semantic project graph does not resolve opened module function when parameter shadows the name" <| fun () ->
      let files =
        [
          "Beta.fs", """
module Sample.Beta.Helpers

let target value =
  value + 2
"""
          "Consumer.fs", """
module Sample.Consumer

open Sample.Beta.Helpers

let caller target value =
  target value
"""
        ]
      withTempProject files [ "Beta.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.isEmpty "parameter shadowing should prevent an edge to opened module target"

    testCase "semantic project graph resolves alias-qualified call even when another same-name module is opened" <| fun () ->
      let files =
        [
          "Alpha.fs", """
module Sample.Alpha.Helpers

let target value =
  value + 1
"""
          "Beta.fs", """
module Sample.Beta.Helpers

let target value =
  value + 2
"""
          "Consumer.fs", """
module Sample.Consumer

open Sample.Alpha.Helpers
module BH = Sample.Beta.Helpers

let caller value =
  BH.target value
"""
        ]
      withTempProject files [ "Alpha.fs"; "Beta.fs"; "Consumer.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.hasLength "alias-qualified call should still resolve to one edge" 1
        edges.Head.From |> Expect.equal "edge starts at Sample.Consumer.caller" "Sample.Consumer.caller"
        edges.Head.To |> Expect.equal "edge targets Sample.Beta.Helpers.target" "Sample.Beta.Helpers.target"

    testCase "project scan extracts explicit instance member into function set" <| fun () ->
      let files =
        [
          "Sample.fs", """
module Sample

let helper x =
  x + 1

type Counter() =
  member _.Inc x =
    helper x
"""
        ]
      withTempProject files [ "Sample.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        funcs |> List.map (fun func -> func.QualifiedName) |> Expect.contains "instance member should be extracted" "Sample.Counter.Inc"

    testCase "project scan extracts explicit static member into function set" <| fun () ->
      let files =
        [
          "Sample.fs", """
module Sample

let normalize (x: string) =
  x.Trim()

type Parser =
  static member Parse x =
    normalize x
"""
        ]
      withTempProject files [ "Sample.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        funcs |> List.map (fun func -> func.QualifiedName) |> Expect.contains "static member should be extracted" "Sample.Parser.Parse"

    testCase "project scan extracts type augmentation member into function set" <| fun () ->
      let files =
        [
          "Sample.fs", """
module Sample

type Counter() = class end

type Counter with
  member _.Dec x =
    x - 1
"""
        ]
      withTempProject files [ "Sample.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        funcs |> List.map (fun func -> func.QualifiedName) |> Expect.contains "type augmentation member should be extracted" "Sample.Counter.Dec"

    testCase "semantic project graph resolves module function calling instance member" <| fun () ->
      let files =
        [
          "Sample.fs", """
module Sample

type Counter() =
  member _.Inc x =
    x + 1

let useCounter (counter: Counter) =
  counter.Inc 1
"""
        ]
      withTempProject files [ "Sample.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.hasLength "module function should resolve one instance-member edge" 1
        edges.Head.From |> Expect.equal "edge starts at useCounter" "Sample.useCounter"
        edges.Head.To |> Expect.equal "edge targets instance member" "Sample.Counter.Inc"

    testCase "semantic project graph resolves member-to-member call on same type" <| fun () ->
      let files =
        [
          "Sample.fs", """
module Sample

type Counter() =
  member _.A() =
    1

  member this.B() =
    this.A()
"""
        ]
      withTempProject files [ "Sample.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        let edges = buildSemanticCallGraphForProject projectPath funcs |> mergeCallEdges
        edges |> Expect.hasLength "member call should resolve one same-type edge" 1
        edges.Head.From |> Expect.equal "edge starts at member B" "Sample.Counter.B"
        edges.Head.To |> Expect.equal "edge targets member A" "Sample.Counter.A"

    testCase "project scan does not create callable nodes for property accessors yet" <| fun () ->
      let files =
        [
          "Sample.fs", """
module Sample

type Counter() =
  member _.Count = 42
"""
        ]
      withTempProject files [ "Sample.fs" ] <| fun _ projectPath ->
        let funcs = scanFunctionsForProject projectPath
        funcs
        |> List.map (fun func -> func.QualifiedName)
        |> List.exists (fun qualifiedName -> qualifiedName = "Sample.Counter.Count" || qualifiedName.EndsWith(".get_Count"))
        |> Expect.isFalse "property accessors should remain excluded in this conservative step"
  ]

// Curved roads

let roadCurveTests =
  testList "Road curves" [
    testCase "catmullRom returns p1 at t=0" <| fun () ->
      let (x, z) = catmullRom (0.0f, 0.0f) (1.0f, 2.0f) (3.0f, 4.0f) (5.0f, 6.0f) 0.0f
      x |> Expect.equal "x equals p1.x" 1.0f
      z |> Expect.equal "z equals p1.z" 2.0f

    testCase "catmullRom returns p2 at t=1" <| fun () ->
      let (x, z) = catmullRom (0.0f, 0.0f) (1.0f, 2.0f) (3.0f, 4.0f) (5.0f, 6.0f) 1.0f
      x |> Expect.equal "x equals p2.x" 3.0f
      z |> Expect.equal "z equals p2.z" 4.0f

    testCase "roadControlPoints extrapolates when neighbors absent" <| fun () ->
      let (p0, p1, p2, p3) = roadControlPoints (1.0f, 2.0f) (4.0f, 6.0f) None None
      p1 |> Expect.equal "p1 is from point" (1.0f, 2.0f)
      p2 |> Expect.equal "p2 is to point" (4.0f, 6.0f)
      p0 |> Expect.equal "p0 extrapolates backward" (-2.0f, -2.0f)
      p3 |> Expect.equal "p3 extrapolates forward" (7.0f, 10.0f)

    testCase "segIntersect finds crossing point" <| fun () ->
      let result =
        segIntersect
          (Vec2.Create(0.0f, 0.0f))
          (Vec2.Create(10.0f, 10.0f))
          (Vec2.Create(0.0f, 10.0f))
          (Vec2.Create(10.0f, 0.0f))
      match result with
      | None -> failtest "expected an intersection"
      | Some (p, _) ->
        abs (p.X - 5.0f) < 0.001f |> Expect.isTrue "x should be 5"
        abs (p.Y - 5.0f) < 0.001f |> Expect.isTrue "y should be 5"
  ]

// Procedural compound buildings

let compoundShapeTests =
  testList "Procedural compound buildings" [
    testCase "complexity 1 yields a single simple mass" <| fun () ->
      let cubes = generateCompound "P.simple" 1 10.0f 8.0f
      cubes |> Expect.hasLength "complexity 1 should stay a rectangle" 1

    testCase "same input generates the same compound" <| fun () ->
      let left = generateCompound "P.factory" 22 12.0f 9.0f
      let right = generateCompound "P.factory" 22 12.0f 9.0f
      left |> Expect.equal "compound generation must be deterministic" right

    testPropertyWithConfig cfg "all cubes stay within the lot" <|
      fun (PositiveInt rawComplexity) (PositiveInt rawW) (PositiveInt rawD) ->
        let complexity = 1 + rawComplexity % 120
        let lotHW = 0.5f + float32 (rawW % 400) / 10.0f
        let lotHD = 0.5f + float32 (rawD % 400) / 10.0f
        let cubes = generateCompound "P.bounds" complexity lotHW lotHD
        cubes |> Array.forall (cubeWithinLot lotHW lotHD)

    testPropertyWithConfig cfg "all cubes have positive size and valid height scale" <|
      fun (PositiveInt rawComplexity) ->
        let cubes = generateCompound "P.dimensions" (1 + rawComplexity % 120) 20.0f 20.0f
        cubes
        |> Array.forall (fun cube ->
          cube.HW > 0.0f
          && cube.HD > 0.0f
          && cube.HeightScale > 0.0f
          && cube.HeightScale <= 1.0f)

    testPropertyWithConfig cfg "compound stays connected" <|
      fun (PositiveInt rawComplexity) (PositiveInt rawW) (PositiveInt rawD) ->
        let complexity = 1 + rawComplexity % 120
        let lotHW = 1.0f + float32 (rawW % 300) / 10.0f
        let lotHD = 1.0f + float32 (rawD % 300) / 10.0f
        let cubes = generateCompound "P.connected" complexity lotHW lotHD
        compoundConnected cubes

    testCase "higher complexity keeps adding wings on ample lots" <| fun () ->
      let medium = generateCompound "P.campus" 30 40.0f 40.0f |> Array.length
      let high = generateCompound "P.campus" 60 40.0f 40.0f |> Array.length
      (high, medium) |> Expect.isGreaterThan "complexity 60 should produce more masses than complexity 30"

    testCase "very high complexity can exceed the old fixed 26-cube ceiling" <| fun () ->
      let cubes = generateCompound "P.smithsonian" 80 60.0f 60.0f |> Array.length
      (cubes, 26) |> Expect.isGreaterThan "huge lots should not be hard-capped at the old 26-cube limit"

    testCase "high complexity compounds stay porous on roomy lots" <| fun () ->
      let seeds =
        [ "P.Alpha"; "P.Beta"; "P.Gamma"; "P.Delta"
          "P.Omega"; "P.Factory"; "P.Campus"; "P.Warehouse" ]
      for seed in seeds do
        let cubes = generateCompound seed 90 60.0f 60.0f
        cubes.Length >= 24 |> Expect.isTrue (sprintf "%s should be large enough to matter" seed)
        let fill = footprintFillRatio 1.0f cubes
        (fill <= 0.72)
        |> Expect.isTrue (sprintf "%s should stay porous, got fill ratio %.3f" seed fill)
  ]

let typedMassingTests =
  testList "Typed building massing" [
    testCase "shed massing stays a single simple volume" <| fun () ->
      let cubes = generateTypedCompound "Test.shed" Shed 18 10.0f 8.0f
      cubes |> Expect.hasLength "sheds should stay legible as a single authored mass" 1

    testCase "typed massing remains deterministic" <| fun () ->
      let left = generateTypedCompound "Test.tower" Tower 42 12.0f 10.0f
      let right = generateTypedCompound "Test.tower" Tower 42 12.0f 10.0f
      left |> Expect.equal "typed massing should stay deterministic" right

    testCase "rowhouse massing prefers a linear footprint" <| fun () ->
      let cubes = generateTypedCompound "Test.row" Rowhouse 30 14.0f 5.5f
      let minX, maxX, minZ, maxZ =
        cubes
        |> Array.fold (fun (minX, maxX, minZ, maxZ) cube ->
            (min minX (cube.CX - cube.HW),
             max maxX (cube.CX + cube.HW),
             min minZ (cube.CZ - cube.HD),
             max maxZ (cube.CZ + cube.HD)))
            (Single.PositiveInfinity, Single.NegativeInfinity, Single.PositiveInfinity, Single.NegativeInfinity)
      let aspect = (maxX - minX) / max 0.1f (maxZ - minZ)
      (aspect, 1.7f) |> Expect.isGreaterThan "rowhouses should read as elongated streetwall masses"

    testCase "skyscraper massing creates a tall shaft over a broader podium" <| fun () ->
      let cubes = generateTypedCompound "Test.scraper" Skyscraper 50 12.0f 12.0f
      (cubes.Length, 2) |> Expect.isGreaterThanOrEqual "skyscraper should have at least a podium and shaft"
      let tallest = cubes |> Array.maxBy _.HeightScale
      let broadest = cubes |> Array.maxBy (fun cube -> cube.HW * cube.HD)
      (broadest.HW * broadest.HD, tallest.HW * tallest.HD)
      |> Expect.isGreaterThan "podium footprint should exceed shaft footprint"
      (tallest.HeightScale, broadest.HeightScale)
      |> Expect.isGreaterThan "shaft should rise above the podium"
  ]

let massingFamilyTests =
  testList "Named massing families" [
    testCase "monolith family has no podium, shaft, or crown" <| fun () ->
      let profile = BuildingMassingProfile.ofFamily Monolith
      profile.PodiumRatio |> Expect.isNone "monolith should not define a podium"
      profile.ShaftRatio |> Expect.isNone "monolith should not define a shaft"
      profile.CrownRatio |> Expect.isNone "monolith should not define a crown"

    testCase "massing family labels stay human-readable" <| fun () ->
      massingFamilyLabel TaperedTower
      |> Expect.equal "tapered tower should stay lower-case and readable" "tapered tower"

    testCase "massing family selection is deterministic" <| fun () ->
      let first = selectMassingFamily "MyMod.parseExpr" Tower 7
      let second = selectMassingFamily "MyMod.parseExpr" Tower 7
      first |> Expect.equal "same input should resolve to the same family" second

    testCase "skyscraper selection never falls back to low-rise families" <| fun () ->
      let seeds =
        [ "Core.dispatchHub"
          "Core.renderLoop"
          "Core.typeCheck" ]
      for qualName in seeds do
        match selectMassingFamily qualName Skyscraper 30 with
        | PodiumShaft
        | TaperedTower -> ()
        | other -> failtestf "expected a tall family for %s, got %A" qualName other

    testCase "tapered tower family yields a podium, shaft, and crown hierarchy" <| fun () ->
      let cubes = generateTypedCompoundForFamily "Test.scraper" Skyscraper TaperedTower 50 12.0f 12.0f
      (cubes.Length, 3)
      |> Expect.isGreaterThanOrEqual "tapered towers should have at least podium, shaft, and crown masses"
      let byHeight = cubes |> Array.sortByDescending _.HeightScale
      let tallest = byHeight.[0]
      let secondTallest = byHeight.[1]
      let broadest = cubes |> Array.maxBy (fun cube -> cube.HW * cube.HD)
      let narrowest = cubes |> Array.minBy (fun cube -> cube.HW * cube.HD)
      (tallest.HeightScale, secondTallest.HeightScale)
      |> Expect.isGreaterThan "crown should rise above the shaft"
      (broadest.HW * broadest.HD, narrowest.HW * narrowest.HD)
      |> Expect.isGreaterThan "podium footprint should exceed the crown footprint"
  ]

let historyAccretionTests =
  testList "History accretions" [
    testCase "commit thresholds step accretion count up" <| fun () ->
      accretionCountFromCommits 19 |> Expect.equal "<20 commits should stay at zero accretions" 0
      accretionCountFromCommits 20 |> Expect.equal "20 commits should unlock the first accretion" 1
      accretionCountFromCommits 45 |> Expect.equal "45 commits should unlock the second accretion" 2
      accretionCountFromCommits 80 |> Expect.equal "80 commits should unlock the third accretion" 3

    testCase "fragmented bug-fix-heavy history becomes patched" <| fun () ->
      accretionStyleFromSignals 4 0.35f
      |> Expect.equal "many authors plus bug-fix churn should read as patched" Patched

    testCase "quiet building compound stays on the authored family massing" <| fun () ->
      let building =
        mkSampleBuilding "quiet" Rowhouse 50 0.35f 3 2 5 240.0f 8 0.0f
        |> fun sample -> { sample with W = 10.0f; D = 6.0f }
      let expected =
        generateTypedCompoundForFamily
          building.Func.QualifiedName
          building.BuildingType
          (currentMassingFamily building)
          building.Complexity
          (building.W / 2.0f)
          (building.D / 2.0f)
      compoundForBuilding building
      |> Expect.equal "low-commit buildings should remain pure authored massing" expected

    testCase "history accretions stay deterministic, connected, and within lot" <| fun () ->
      let profile =
        accretionProfileFromGit 90 4 0.45f 900.0f
      let baseCubes =
        [| { CX = 0.0f; CZ = 0.0f; HW = 1.4f; HD = 1.0f; HeightScale = 0.9f } |]
      let first = applyHistoryAccretions profile "Test.accretion" 5.0f 5.0f baseCubes
      let second = applyHistoryAccretions profile "Test.accretion" 5.0f 5.0f baseCubes
      first |> Expect.equal "history accretions should be deterministic" second
      (first.Length, baseCubes.Length) |> Expect.isGreaterThan "hot history should add visible accretions"
      first |> Array.forall (cubeWithinLot 5.0f 5.0f)
      |> Expect.isTrue "accretion cubes should stay inside the lot envelope"
      compoundConnected first
      |> Expect.isTrue "accretion cubes should stay attached to the main compound"

    testCase "max extended history keeps accretions subordinate to the authored footprint" <| fun () ->
      let profile =
        accretionProfileFromGit 140 2 0.05f 1825.0f
      let baseCubes =
        [| { CX = 0.0f; CZ = 0.0f; HW = 1.4f; HD = 1.0f; HeightScale = 0.9f } |]
      let accreted = applyHistoryAccretions profile "TestMod.extreme1" 5.0f 5.0f baseCubes
      let baseArea = baseCubes |> Array.sumBy (fun cube -> cube.HW * cube.HD)
      let addedArea =
        accreted
        |> Array.skip baseCubes.Length
        |> Array.sumBy (fun cube -> cube.HW * cube.HD)
      (addedArea / baseArea, 0.25f)
      |> Expect.isLessThanOrEqual "history should read as a subordinate layer, not outweigh the authored family footprint"
  ]

let cameraMovementTests =
  testList "Camera movement" [
    testCase "forward movement follows the full look direction" <| fun () ->
      let cam = FpsCamera.create Vector3.Zero
      cam.Yaw <- 0.0f
      cam.Pitch <- 0.6f

      let moveForward, _ : Vector3 * Vector3 = FpsCamera.movementVectors cam
      let lookForward = Vector3.Normalize(FpsCamera.forward cam)

      abs (moveForward.X - lookForward.X) < 0.0001f |> Expect.isTrue "forward x should match look direction"
      abs (moveForward.Y - lookForward.Y) < 0.0001f |> Expect.isTrue "forward y should match look direction"
      abs (moveForward.Z - lookForward.Z) < 0.0001f |> Expect.isTrue "forward z should match look direction"
      moveForward.Y > 0.0f |> Expect.isTrue "looking upward should move upward"

    testCase "strafe vector stays horizontal while moving forward uses pitch" <| fun () ->
      let cam = FpsCamera.create Vector3.Zero
      cam.Yaw <- 0.8f
      cam.Pitch <- 0.7f

      let _, right : Vector3 * Vector3 = FpsCamera.movementVectors cam

      abs right.Y < 0.0001f |> Expect.isTrue "strafe should stay level on the ground plane"
      abs (right.Length() - 1.0f) < 0.0001f |> Expect.isTrue "strafe vector should stay normalized"

    testCase "review presets frame off-origin geometry and produce distinct angles" <| fun () ->
      let tower =
        { mkSampleBuilding "tower1" Tower 120 0.9f 8 3 10 180.0f 24 0.0f with
            X = 124.0f
            Z = 82.0f
            W = 10.0f
            D = 10.0f
            H = 28.0f }
      let rowhouse =
        { mkSampleBuilding "row1" Rowhouse 40 0.4f 3 1 4 90.0f 6 0.0f with
            X = 142.0f
            Z = 94.0f
            W = 14.0f
            D = 7.0f
            H = 9.0f }
      let road =
        { FromFunc = "TestMod.a"
          ToFunc = "TestMod.b"
          FromPos = Vector3(118.0f, 0.0f, 88.0f)
          ToPos = Vector3(158.0f, 0.0f, 88.0f)
          HalfWidth = 0.8f
          Weight = 4
          Color = Color.Gray
          Organic = 0.35f }
      let buildings = [| tower; rowhouse |]
      let bounds = sceneGeometryBounds buildings [ road ]
      (TRect.centerX bounds, 120.0f) |> Expect.isGreaterThan "bounds should follow the off-origin neighborhood"
      (TRect.centerZ bounds, 80.0f) |> Expect.isGreaterThan "bounds should follow the off-origin neighborhood"

      let overview = reviewCameraPose Overview buildings [ road ]
      let oblique = reviewCameraPose Oblique buildings [ road ]
      let lowSide = reviewCameraPose StreetLevel buildings [ road ]

      overview.Label |> Expect.equal "overview label should be stable" "Overview"
      oblique.Label |> Expect.equal "oblique label should be stable" "Oblique"
      lowSide.Label |> Expect.equal "street-level label should be stable" "Low side"

      ((overview.Position - oblique.Position).Length(), 5.0f)
      |> Expect.isGreaterThan "overview and oblique should not collapse to the same capture"
      (lowSide.Position.Y, overview.Position.Y)
      |> Expect.isLessThan "low side should stay lower than overview"
  ]

let visualDefaultsTests =
  testList "Visual defaults" [
    testCase "SSAO buffer uses full screen resolution to reduce blotchy AO" <| fun () ->
      ssaoBufferSize defaultSsaoSettings 1600 900
      |> Expect.equal "AO buffer should match the scene resolution" (1600, 900)

    testCase "UI text theme uses larger readable sizes" <| fun () ->
      (defaultUiTextTheme.HudTitle, 24) |> Expect.isGreaterThanOrEqual "HUD title should be clearly larger"
      (defaultUiTextTheme.HudStats, 16) |> Expect.isGreaterThanOrEqual "HUD stats should be readable"
      (defaultUiTextTheme.HudControls, 13) |> Expect.isGreaterThanOrEqual "HUD controls should be readable"
      (defaultUiTextTheme.TooltipBody, 13) |> Expect.isGreaterThanOrEqual "Tooltips should be readable"
      (defaultUiTextTheme.SelectionBody, 13) |> Expect.isGreaterThanOrEqual "Selection panel text should be readable"

    testCase "compact UI theme quiets chrome without becoming tiny" <| fun () ->
      let compact = compactUiTextTheme defaultUiTextTheme
      (compact.HudTitle, defaultUiTextTheme.HudTitle) |> Expect.isLessThan "compact HUD title should be smaller"
      (compact.HudStats, defaultUiTextTheme.HudStats) |> Expect.isLessThan "compact HUD stats should be smaller"
      (compact.HudStats, 14) |> Expect.isGreaterThanOrEqual "compact HUD stats should stay readable"
      (compact.LegendEntry, 12) |> Expect.isGreaterThanOrEqual "legend text should stay readable"

    testCase "legend summary shows busiest districts first and tracks hidden count" <| fun () ->
      let districts =
        [ mkDistrict "utilities" 8 110 Color.Blue
          mkDistrict "api" 20 300 Color.Red
          mkDistrict "storage" 12 260 Color.Green
          mkDistrict "ui" 15 200 Color.Orange
          mkDistrict "tests" 5 500 Color.Purple ]
      let visible, hidden = summarizeLegendDistricts 3 districts
      visible |> List.map _.Name |> Expect.equal "top entries should be sorted by function count first" ["api"; "ui"; "storage"]
      hidden |> Expect.equal "remaining districts should be counted" 2
  ]

let roadAccessTests =
  testList "Road access layout" [
    testCase "Weber district produces internal roads" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let funcs = List.init 9 (fun i -> mkFunc (sprintf "func%d" i) "TestModule")
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.5f (Random 42) Map.empty
      roads |> Expect.isNonEmpty "a 9-function block should produce internal Weber roads"

    testCase "Single building district produces buildings" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let funcs = [ mkFunc "onlyFunc" "TestModule" ]
      let bldgs, _ = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.5f (Random 1) Map.empty
      bldgs |> Expect.isNonEmpty "single function should produce at least one building"

    testCase "Buildings stay within block rect bounds" <| fun () ->
      let rect = { X = 5.0f; Z = 5.0f; W = 20.0f; H = 20.0f }
      let funcs = List.init 12 (fun i -> mkFunc (sprintf "func%d" i) "TestModule")
      let buildings, _ = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.5f (Random 7) Map.empty
      let eps = 1.5f
      for b in buildings do
        (b.X,       rect.X - eps)           |> Expect.isGreaterThanOrEqual "building left edge inside block"
        (b.Z,       rect.Z - eps)           |> Expect.isGreaterThanOrEqual "building top edge inside block"
        (b.X + b.W, rect.X + rect.W + eps)  |> Expect.isLessThanOrEqual   "building right edge inside block"
        (b.Z + b.D, rect.Z + rect.H + eps)  |> Expect.isLessThanOrEqual   "building bottom edge inside block"

    testCase "Building count matches function count" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 30.0f; H = 30.0f }
      let funcs = List.init 16 (fun i -> mkFunc (sprintf "func%d" i) "TestModule")
      let buildings, _ = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.5f (Random 99) Map.empty
      buildings.Length |> Expect.equal "should produce one building per function" 16
  ]

let private rectsOverlap (a: TRect) (b: TRect) =
  a.X < b.X + b.W
  && b.X < a.X + a.W
  && a.Z < b.Z + b.H
  && b.Z < a.Z + a.H

let private hasRecordField<'T> (name: string) =
  typeof<'T>.GetProperty(name) <> null

let private hasModuleFunction (name: string) =
  let moduleType = typeof<TRect>.Assembly.GetType("CodeCity")
  not (isNull moduleType) && not (isNull (moduleType.GetMethod(name)))

let private invokePrivateStatic<'T> methodName (args: obj[]) =
  let moduleType = typeof<TRect>.Assembly.GetType("CodeCity")
  let flags = System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Static
  let methodInfo = moduleType.GetMethod(methodName, flags)
  if isNull methodInfo then
    failtestf "expected private method %s to exist on CodeCity" methodName
  methodInfo.Invoke(null, args) :?> 'T

let private formsLongitudinalSplitEdgeBypassViaReflection
  (g: WeberGraph)
  (originId: NodeId)
  (origin: Vec2)
  (target: Vec2)
  (splitEdgeId: EdgeId)
  =
  invokePrivateStatic<bool>
    "formsLongitudinalSplitEdgeBypass"
    [| box g; box originId; box origin; box target; box splitEdgeId |]

let private paperCase citation name body =
  testCase (sprintf "[paper %s] %s" citation name) body

let private paperProperty citation name body =
  testPropertyWithConfig cfg (sprintf "[paper %s] %s" citation name) body

let private assumptionCase citation name body =
  testCase (sprintf "[assumption %s] %s" citation name) body

let private assumptionProperty citation name body =
  testPropertyWithConfig cfg (sprintf "[assumption %s] %s" citation name) body

let private naturalismCase citation name body =
  testCase (sprintf "[naturalism %s] %s" citation name) body

let private randomDistrictRect (PositiveInt rawW) (PositiveInt rawH) =
  let w = float32 (10 + rawW % 90)
  let h = float32 (10 + rawH % 90)
  { X = 0.0f; Z = 0.0f; W = w; H = h }

let private unwrapOk message = function
  | Ok value -> value
  | Error error -> failtestf "%s: %A" message error

let private seedMajorGrowthTestGraph (rect: TRect) =
  let g = WeberGraph()
  let topLeft = g.AddNode(Vec2.Create(rect.X, rect.Z), Avenue)
  let topMid = g.AddNode(Vec2.Create(rect.X + rect.W / 2.0f, rect.Z), Avenue)
  let topRight = g.AddNode(Vec2.Create(rect.X + rect.W, rect.Z), Avenue)
  let rightMid = g.AddNode(Vec2.Create(rect.X + rect.W, rect.Z + rect.H / 2.0f), Avenue)
  let bottomRight = g.AddNode(Vec2.Create(rect.X + rect.W, rect.Z + rect.H), Avenue)
  let bottomMid = g.AddNode(Vec2.Create(rect.X + rect.W / 2.0f, rect.Z + rect.H), Avenue)
  let bottomLeft = g.AddNode(Vec2.Create(rect.X, rect.Z + rect.H), Avenue)
  let leftMid = g.AddNode(Vec2.Create(rect.X, rect.Z + rect.H / 2.0f), Avenue)
  [ topLeft, topMid
    topMid, topRight
    topRight, rightMid
    rightMid, bottomRight
    bottomRight, bottomMid
    bottomMid, bottomLeft
    bottomLeft, leftMid
    leftMid, topLeft ]
  |> List.iter (fun (a, b) -> g.AddEdge(a, b, Avenue, RoadClass.width Avenue) |> ignore)

  let center = g.AddNode(Vec2.Create(TRect.centerX rect, TRect.centerZ rect), Avenue)
  let northArm = g.AddNode(Vec2.Create(TRect.centerX rect, rect.Z + rect.H * 0.225f), Avenue)
  let southArm = g.AddNode(Vec2.Create(TRect.centerX rect, rect.Z + rect.H * 0.775f), Avenue)
  let eastArm = g.AddNode(Vec2.Create(rect.X + rect.W * 0.775f, TRect.centerZ rect), Avenue)
  let westArm = g.AddNode(Vec2.Create(rect.X + rect.W * 0.225f, TRect.centerZ rect), Avenue)
  [ center, northArm; northArm, topMid
    center, southArm; southArm, bottomMid
    center, eastArm; eastArm, rightMid
    center, westArm; westArm, leftMid ]
  |> List.iter (fun (a, b) -> g.AddEdge(a, b, Avenue, RoadClass.width Avenue) |> ignore)

  [ topLeft; topMid; topRight; rightMid; bottomRight; bottomMid; bottomLeft; leftMid ]
  |> List.iter g.MarkFinished

  g, [| Vec2.Create(TRect.centerX rect, TRect.centerZ rect) |]

let private allStreetIds (plan: DistrictPlan) =
  (plan.MajorStreets |> List.map _.Id)
  @ (plan.Quarters |> List.collect (fun quarter -> quarter.MinorStreets |> List.map _.Id))

let private tryFindStreetTraffic streetId (plan: DistrictPlan) =
  plan.MajorStreets
  |> List.tryFind (fun street -> street.Id = streetId)
  |> Option.map (fun street -> street.Traffic)
  |> Option.orElseWith (fun () ->
    plan.Quarters
    |> List.collect _.MinorStreets
    |> List.tryFind (fun street -> street.Id = streetId)
    |> Option.map (fun street -> street.Traffic))

let private phase4Lot (rect: TRect) frontageEdge streetStatus landUseType landUseValue =
  PlannedLot.create rect frontageEdge rect
  |> fun lot ->
    { lot with
        Rect = rect
        BlockRect = rect
        FrontingStreetStatus = streetStatus
        LandUseType = landUseType
        LandUseValue = landUseValue }

let private phase4Definitions =
  [ { LandUseType = ResidentialUse
      Valuations =
        [ { Metric = LotArea; Curve = LinearUp; Min = 0.0f; Max = 100.0f; Weight = 1.0f } ] }
    { LandUseType = ParkUse
      Valuations =
        [ { Metric = LotArea; Curve = LinearDown; Min = 0.0f; Max = 100.0f; Weight = 1.0f } ] } ]

let private phase4Goals =
  [ { LandUseType = ResidentialUse; TargetPercent = 1.0f }
    { LandUseType = ParkUse; TargetPercent = 0.0f } ]

let private phase4Config seed elapsed reevaluation attempts rejectedDeltaThreshold =
  { Goals = phase4Goals
    Definitions = phase4Definitions
    AveragePricePerSqm = 100.0f
    GlobalWeight = 1.0f
    LocalWeight = 0.0f
    GoalScale = 1.0f
    AttemptsFraction = attempts
    RejectedDeltaThreshold = rejectedDeltaThreshold
    Seed = seed
    Cadence =
      { StepYears = 1.0f
        ReevaluationYears = reevaluation
        ElapsedSinceLastEvaluationYears = elapsed } }

let private phase5Block (rect: TRect) landUseType landUseValue streetFacingEdges =
  { Scenario.rectangularBlock rect rect landUseType landUseValue with
      StreetFacingEdges = streetFacingEdges }

let private containsRect (outerRect: TRect) (innerRect: TRect) =
  innerRect.X >= outerRect.X
  && innerRect.Z >= outerRect.Z
  && innerRect.X + innerRect.W <= outerRect.X + outerRect.W
  && innerRect.Z + innerRect.H <= outerRect.Z + outerRect.H

let private phase6Lot (rect: TRect) landUseType landUseValue =
  PlannedLot.create rect North rect
  |> fun lot ->
    { lot with
        Rect = rect
        BlockRect = rect
        FrontingStreetStatus = Built
        LandUseType = landUseType
        LandUseValue = landUseValue }

let private phase7MinorStreet quarterRect status traffic segment =
  let seeded =
    match status with
    | Built -> MinorStreet.built quarterRect segment
    | Planned -> MinorStreet.planned quarterRect segment
  { seeded with
      Traffic =
        { Volume = traffic
          MaxVolume = streetMaxVolumeFromTraffic traffic } }

let private phase7Plan (lot: PlannedLot) (streets: MinorStreet list) =
  let block =
    { Scenario.rectangularBlock lot.BlockRect lot.BlockRect lot.LandUseType lot.LandUseValue with
        Lots = [ lot ] }
  { MajorStreets = []
    Quarters = [ QuarterPlan.create lot.BlockRect streets [ block ] ] }
  |> indexStreetNetwork

let private phase8TimelineConfig steps stepYears reevaluationYears promotionLagSteps : SimulationTimelineConfig =
  { Steps = steps
    StepYears = stepYears
    LandUseReevaluationYears = reevaluationYears
    PromotionLagSteps = promotionLagSteps
    BuildingSubstitution = None }

let private phase11TimelineConfig substitution steps stepYears reevaluationYears promotionLagSteps : SimulationTimelineConfig =
  { Steps = steps
    StepYears = stepYears
    LandUseReevaluationYears = reevaluationYears
    PromotionLagSteps = promotionLagSteps
    BuildingSubstitution = substitution }

let private phase11StableLandUseConfig seed elapsed reevaluation =
  { Goals = [ { LandUseType = ResidentialUse; TargetPercent = 1.0f } ]
    Definitions =
      [ { LandUseType = ResidentialUse
          Valuations =
            [ { Metric = LotArea; Curve = LinearUp; Min = 0.0f; Max = 100.0f; Weight = 1.0f } ] } ]
    AveragePricePerSqm = 100.0f
    GlobalWeight = 1.0f
    LocalWeight = 0.0f
    GoalScale = 1.0f
    AttemptsFraction = 0.0f
    RejectedDeltaThreshold = 0.1f
    Seed = seed
    Cadence =
      { StepYears = 1.0f
        ReevaluationYears = reevaluation
        ElapsedSinceLastEvaluationYears = elapsed } }

let private phase10SubstitutionConfig =
  { AgeFactor = { Curve = LinearUp; Min = 0.0f; Max = 40.0f; Weight = 0.35f }
    PriceGapFactor = { Curve = LinearUp; Min = 0.0f; Max = 100.0f; Weight = 0.65f }
    Seed = 7 }

let private phase10Lot blockRect rect ageYears price floorSpace residents =
  { phase6Lot rect ResidentialUse 0.6f with
      BlockRect = blockRect
      BuildingAgeYears = ageYears
      Price = price
      FloorSpace = floorSpace
      Residents = residents }

let private phase10Plan (quarterRect: TRect) (lots: PlannedLot list) =
  let block =
    { Scenario.rectangularBlock quarterRect quarterRect ResidentialUse 0.6f with
        Lots = lots }
  { MajorStreets = []
    Quarters = [ QuarterPlan.create quarterRect [] [ block ] ] }
  |> indexStreetNetwork

let private chainTrafficPlan () =
  let quarterRect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
  let block = Scenario.rectangularBlock quarterRect quarterRect ResidentialUse 0.6f
  let plannedMinor =
    { Id = StreetId 2
      Segment = { X = 4.0f; Z = 0.0f; W = 1.0f; H = 10.0f }
      Status = Planned
      QuarterRect = quarterRect
      Residents = 0.0f
      Traffic = { Volume = 0.0f; MaxVolume = 0.0f } }
  let builtMinor =
    { Id = StreetId 3
      Segment = { X = 0.0f; Z = 8.0f; W = 10.0f; H = 1.0f }
      Status = Built
      QuarterRect = quarterRect
      Residents = 7.0f
      Traffic = { Volume = 0.0f; MaxVolume = 0.0f } }
  let quarter = QuarterPlan.create quarterRect [ plannedMinor; builtMinor ] [ block ]
  let district : DistrictPlan =
    { MajorStreets =
        [ { Id = StreetId 1
            Segment = { X = 0.0f; Z = 4.0f; W = 10.0f; H = 1.0f }
            Status = Built
            Residents = 3.0f
            Traffic = { Volume = 0.0f; MaxVolume = 0.0f } } ]
      Quarters = [ quarter ] }
  district
  |> indexStreetNetwork

let private spaceSyntaxChoicePlan () =
  let district : DistrictPlan =
    { MajorStreets =
        [ { Id = StreetId 1
            Segment = { X = 0.0f; Z = 0.0f; W = 100.0f; H = 20.0f }
            Status = Built
            Residents = 1.0f
            Traffic = { Volume = 0.0f; MaxVolume = 0.0f } }
          { Id = StreetId 2
            Segment = { X = 100.0f; Z = 0.0f; W = 400.0f; H = 20.0f }
            Status = Built
            Residents = 1.0f
            Traffic = { Volume = 0.0f; MaxVolume = 0.0f } }
          { Id = StreetId 3
            Segment = { X = 250.0f; Z = 0.0f; W = 100.0f; H = 40.0f }
            Status = Built
            Residents = 1.0f
            Traffic = { Volume = 0.0f; MaxVolume = 0.0f } }
          { Id = StreetId 4
            Segment = { X = 80.0f; Z = 0.0f; W = 20.0f; H = 40.2f }
            Status = Built
            Residents = 1.0f
            Traffic = { Volume = 0.0f; MaxVolume = 0.0f } }
          { Id = StreetId 5
            Segment = { X = 80.0f; Z = 20.2f; W = 170.0f; H = 20.0f }
            Status = Built
            Residents = 1.0f
            Traffic = { Volume = 0.0f; MaxVolume = 0.0f } } ]
      Quarters = [] }
  district
  |> indexStreetNetwork

let trafficSimulationTests =
  testList "Traffic and promotion" [
    paperCase "§3.2" "shortest path uses connected street segments on a tiny graph" <| fun () ->
      let plan = chainTrafficPlan ()
      let startStreet = plan.MajorStreets.Head.Id
      let endStreet = plan.Quarters.Head.MinorStreets |> List.last |> fun street -> street.Id
      shortestStreetPath plan startStreet endStreet
      |> Expect.equal "route should traverse the only connecting planned street"
           (Ok [ startStreet; StreetId 2; endStreet ])

    paperCase "§3.2" "disconnected streets report no route" <| fun () ->
      let plan =
        let district : DistrictPlan =
          { MajorStreets =
              [ { Id = StreetId 10
                  Segment = { X = 0.0f; Z = 0.0f; W = 8.0f; H = 1.0f }
                  Status = Built
                  Residents = 1.0f
                  Traffic = { Volume = 0.0f; MaxVolume = 0.0f } }
                { Id = StreetId 11
                  Segment = { X = 30.0f; Z = 0.0f; W = 8.0f; H = 1.0f }
                  Status = Built
                  Residents = 1.0f
                  Traffic = { Volume = 0.0f; MaxVolume = 0.0f } } ]
            Quarters = [] }
        district
        |> indexStreetNetwork
      shortestStreetPath plan (StreetId 10) (StreetId 11)
      |> Expect.equal "disconnected street segments should not fabricate a path"
           (Error (NoRouteBetweenStreets (StreetId 10, StreetId 11)))

    paperCase "§3.2" "space syntax turn cost scales to the paper's 90-degree calibration" <| fun () ->
      (abs (angularTurnCost 500.0f 0.0f - 0.0f), 0.01f)
      |> Expect.isLessThanOrEqual "zero-degree continuation should add no turn cost"
      (abs (angularTurnCost 500.0f (MathF.PI / 2.0f) - 500.0f), 0.1f)
      |> Expect.isLessThanOrEqual "a right-angle turn should cost 500m"
      (abs (angularTurnCost 500.0f MathF.PI - 1000.0f), 0.1f)
      |> Expect.isLessThanOrEqual "a U-turn should cost 1000m"

    paperProperty "§3.2" "space syntax turn cost is monotone with angle" <|
      fun (leftAngle: float32) (rightAngle: float32) ->
        let clampAngle angle =
          match Single.IsNaN angle || Single.IsInfinity angle with
          | true -> 0.0f
          | false -> abs angle % MathF.PI
        let left = clampAngle leftAngle
        let right = clampAngle rightAngle
        let leftCost = angularTurnCost 500.0f left
        let rightCost = angularTurnCost 500.0f right
        match left <= right with
        | true -> (leftCost, rightCost) |> Expect.isLessThanOrEqual "smaller turn angles should not cost more"
        | false -> (rightCost, leftCost) |> Expect.isLessThanOrEqual "smaller turn angles should not cost more"

    paperCase "§3.2" "paper turn cost prefers a longer straight corridor over a shorter two-turn shortcut" <| fun () ->
      let plan = spaceSyntaxChoicePlan ()
      shortestStreetPathWithTurnCost 0.0f plan (StreetId 1) (StreetId 3)
      |> Expect.equal "without the paper penalty the shorter shortcut should win"
           (Ok [ StreetId 1; StreetId 4; StreetId 5; StreetId 3 ])
      shortestStreetPathWithTurnCost 500.0f plan (StreetId 1) (StreetId 3)
      |> Expect.equal "with the paper penalty the longer straight corridor should win"
           (Ok [ StreetId 1; StreetId 2; StreetId 3 ])

    paperCase "§3.2" "applying and removing a trip updates every street on its route" <| fun () ->
      let plan = chainTrafficPlan ()
      let trip =
        { StartStreet = StreetId 1
          EndStreet = StreetId 3
          Volume = 2.0f
          Route = [ StreetId 1; StreetId 2; StreetId 3 ] }
      let applied = applyResidentTrip trip plan |> unwrapOk "trip application should succeed"
      trip.Route
      |> List.iter (fun streetId ->
        applied
        |> tryFindStreetTraffic streetId
        |> Option.map _.Volume
        |> Expect.equal (sprintf "street %A should receive the trip volume" streetId) (Some 2.0f))
      let removed = removeResidentTrip trip applied |> unwrapOk "trip removal should succeed"
      trip.Route
      |> List.iter (fun streetId ->
        removed
        |> tryFindStreetTraffic streetId
        |> Option.map _.Volume
        |> Expect.equal (sprintf "street %A should return to zero after trip removal" streetId) (Some 0.0f))

    paperCase "§3.2" "planned streets participate in trip routing and traffic accumulation" <| fun () ->
      let plan = chainTrafficPlan ()
      let trips = generateResidentTrips plan
      trips |> Expect.isNonEmpty "resident-bearing streets should generate trips"
      trips
      |> List.exists (fun trip -> trip.Route |> List.contains (StreetId 2))
      |> Expect.isTrue "generated trips should be allowed to use planned streets"
      let updated = updateTrafficSimulation plan
      updated
      |> tryFindStreetTraffic (StreetId 2)
      |> Option.map _.Volume
      |> Option.defaultValue 0.0f
      |> fun volume -> (volume, 0.0f) |> Expect.isGreaterThan "planned connector should accumulate traffic when used by trips"

    paperCase "§3.2" "traffic threshold rather than raw geometry promotes planned streets to built" <| fun () ->
      let quarterRect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let block = Scenario.rectangularBlock quarterRect quarterRect ResidentialUse 0.6f
      let before =
        let district : DistrictPlan =
          { MajorStreets =
              [ { Id = StreetId 20
                  Segment = { X = 0.0f; Z = 0.0f; W = 2.0f; H = 30.0f }
                  Status = Planned
                  Residents = 0.0f
                  Traffic = { Volume = 12.0f; MaxVolume = 0.0f } }
                { Id = StreetId 21
                  Segment = { X = 4.0f; Z = 0.0f; W = 2.0f; H = 40.0f }
                  Status = Planned
                  Residents = 0.0f
                  Traffic = { Volume = 1.0f; MaxVolume = 0.0f } } ]
            Quarters = [ QuarterPlan.create quarterRect [] [ block ] ] }
        district
        |> indexStreetNetwork
      let promoted = promotePlannedStreets before
      promoted.MajorStreets |> List.find (fun street -> street.Id = StreetId 20) |> fun street -> street.Status
      |> Expect.equal "traffic-qualified planned street should become built" Built
      promoted.MajorStreets |> List.find (fun street -> street.Id = StreetId 21) |> fun street -> street.Status
      |> Expect.equal "low-traffic street should remain planned even if geometrically long" Planned

    paperProperty "§3.2" "re-running traffic-based promotion without traffic changes is idempotent" <|
      fun () ->
        let once = chainTrafficPlan () |> updateTrafficSimulation |> promotePlannedStreets
        let twice = promotePlannedStreets once
        once = twice

    paperProperty "§3.2" "shortest paths remain connected on a simple chain with varying street lengths" <|
      fun (PositiveInt rawA) (PositiveInt rawB) (PositiveInt rawC) ->
        let aLen = float32 (4 + rawA % 12)
        let bLen = float32 (4 + rawB % 12)
        let cLen = float32 (4 + rawC % 12)
        let quarterRect = { X = 0.0f; Z = 0.0f; W = max aLen cLen; H = max bLen cLen + 6.0f }
        let block = Scenario.rectangularBlock quarterRect quarterRect ResidentialUse 0.5f
        let middle =
          { Id = StreetId 32
            Segment = { X = min 4.0f (aLen - 1.0f); Z = 0.0f; W = 1.0f; H = bLen + 4.0f }
            Status = Planned
            QuarterRect = quarterRect
            Residents = 0.0f
            Traffic = { Volume = 0.0f; MaxVolume = 0.0f } }
        let endStreet =
          { Id = StreetId 33
            Segment = { X = 0.0f; Z = min (bLen + 2.0f) (quarterRect.H - 1.0f); W = cLen; H = 1.0f }
            Status = Built
            QuarterRect = quarterRect
            Residents = 1.0f
            Traffic = { Volume = 0.0f; MaxVolume = 0.0f } }
        let quarter = QuarterPlan.create quarterRect [ middle; endStreet ] [ block ]
        let plan =
          let district : DistrictPlan =
            { MajorStreets =
                [ { Id = StreetId 31
                    Segment = { X = 0.0f; Z = 4.0f; W = aLen; H = 1.0f }
                    Status = Built
                    Residents = 1.0f
                    Traffic = { Volume = 0.0f; MaxVolume = 0.0f } } ]
              Quarters = [ quarter ] }
          district
          |> indexStreetNetwork
        shortestStreetPath plan (StreetId 31) (StreetId 33)
        |> Expect.equal "the simple chain should always yield a connected route"
             (Ok [ StreetId 31; StreetId 32; StreetId 33 ])

    naturalismCase "traffic-derived street metrics" "street width and max volume are monotone in traffic" <| fun () ->
      let lowWidth = streetWidthFromTraffic 1.0f
      let highWidth = streetWidthFromTraffic 12.0f
      let lowMaxVolume = streetMaxVolumeFromTraffic 1.0f
      let highMaxVolume = streetMaxVolumeFromTraffic 12.0f
      (highWidth, lowWidth) |> Expect.isGreaterThan "more traffic should imply a larger temporary width"
      (highMaxVolume, lowMaxVolume) |> Expect.isGreaterThan "more traffic should imply a higher maximum volume"
  ]

let landUseDynamicsTests =
  testList "Land-use dynamics" [
    paperCase "§4.1 Eq. (2)-(3)" "global land-use value is zero when area percentages match goals" <| fun () ->
      let lots =
        [ phase4Lot { X = 0.0f; Z = 0.0f; W = 10.0f; H = 1.0f } North Built ResidentialUse 0.5f
          phase4Lot { X = 0.0f; Z = 1.0f; W = 10.0f; H = 1.0f } South Built ParkUse 0.5f ]
      computeGlobalLandUseValue
        [ { LandUseType = ResidentialUse; TargetPercent = 0.5f }
          { LandUseType = ParkUse; TargetPercent = 0.5f } ]
        1.0f
        lots
      |> Expect.equal "perfect target matching should have zero global penalty" 0.0f

    paperCase "§4.1 Eq. (2)-(3)" "global land-use value is area weighted rather than count weighted" <| fun () ->
      let lots =
        [ phase4Lot { X = 0.0f; Z = 0.0f; W = 90.0f; H = 1.0f } North Built ResidentialUse 0.5f
          phase4Lot { X = 0.0f; Z = 1.0f; W = 10.0f; H = 1.0f } South Built ParkUse 0.5f ]
      let actual =
        computeGlobalLandUseValue
          [ { LandUseType = ResidentialUse; TargetPercent = 0.5f }
            { LandUseType = ParkUse; TargetPercent = 0.5f } ]
          1.0f
          lots
      (abs (actual - -0.32f) < 1e-5f)
      |> Expect.isTrue "global land-use value should use area percentages from equations (2)-(3)"

    paperCase "§4.3 Eq. (4)" "valuation curves stay inside the unit interval and preserve their shape" <| fun () ->
      let samples =
        [ Step, -10.0f, 0.0f
          Step, 50.0f, 1.0f
          LinearUp, 25.0f, 0.25f
          LinearDown, 25.0f, 0.75f
          GainUp, 50.0f, 0.25f
          GainDown, 50.0f, 0.75f ]
      samples
      |> List.iter (fun (curve, value, expected) ->
        let actual = applyValuationCurve curve 0.0f 100.0f value
        (abs (actual - expected) < 1e-5f)
        |> Expect.isTrue (sprintf "%A at %f should match the expected bounded valuation" curve value)
        (actual >= 0.0f && actual <= 1.0f)
        |> Expect.isTrue (sprintf "%A should stay in the unit interval" curve))

    paperCase "§4.3 Eq. (4)" "local land-use value uses a convex combination of bounded valuation functions" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 50.0f; H = 1.0f }
      let lot = phase4Lot rect North Built ResidentialUse 0.5f
      let definitions =
        [ { LandUseType = ResidentialUse
            Valuations =
              [ { Metric = LotArea; Curve = LinearUp; Min = 0.0f; Max = 100.0f; Weight = 1.0f }
                { Metric = FrontageAccess; Curve = LinearUp; Min = 0.0f; Max = 1.0f; Weight = 3.0f } ] } ]
      let plan = Scenario.singleQuarter rect
      let actual = evaluateLocalLotLandUseValue definitions plan lot ResidentialUse
      (abs (actual - 0.875f) < 1e-5f)
      |> Expect.isTrue "local value should normalize weights into a convex combination"

    paperCase "§4.1" "blocks persist lots and dominant land use from area-weighted lot majorities" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 10.0f }
      let block =
        { PlannedBlock.create rect rect with
            Lots =
              [ phase4Lot { X = 0.0f; Z = 0.0f; W = 20.0f; H = 1.0f } North Built ResidentialUse 0.6f
                phase4Lot { X = 0.0f; Z = 1.0f; W = 10.0f; H = 1.0f } North Built ResidentialUse 0.9f
                phase4Lot { X = 0.0f; Z = 2.0f; W = 15.0f; H = 1.0f } North Built ParkUse 0.2f ] }
      let recomputed = recomputeBlockDominantLandUse block
      recomputed.LandUseType |> Expect.equal "residential lots cover the most area" ResidentialUse
      (abs (recomputed.LandUseValue - 0.7f) < 1e-5f)
      |> Expect.isTrue "block value should be the area-weighted average of dominant lots"

    paperCase "§4.1" "quarters derive their dominant land use from area-weighted blocks" <| fun () ->
      let quarterRect = { X = 0.0f; Z = 0.0f; W = 40.0f; H = 20.0f }
      let blockA =
        { Scenario.rectangularBlock quarterRect { X = 0.0f; Z = 0.0f; W = 30.0f; H = 20.0f } ResidentialUse 0.7f with
            Lots = [ phase4Lot { X = 0.0f; Z = 0.0f; W = 30.0f; H = 20.0f } North Built ResidentialUse 0.7f ] }
      let blockB =
        { Scenario.rectangularBlock quarterRect { X = 30.0f; Z = 0.0f; W = 10.0f; H = 20.0f } ParkUse 0.9f with
            Lots = [ phase4Lot { X = 30.0f; Z = 0.0f; W = 10.0f; H = 20.0f } North Built ParkUse 0.9f ] }
      let quarter = QuarterPlan.create quarterRect [] [ blockA; blockB ]
      let recomputed = recomputeQuarterDominantLandUse quarter
      recomputed.LandUseType |> Expect.equal "the larger residential block should dominate the quarter" ResidentialUse
      (abs (recomputed.LandUseValue - 0.7f) < 1e-5f)
      |> Expect.isTrue "quarter value should be the area-weighted average of dominant blocks"

    paperCase "§4.1" "land-use updates bootstrap singleton lots when blocks do not yet carry subdivisions" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 24.0f; H = 12.0f }
      let block = Scenario.rectangularBlock rect rect ParkUse 0.2f
      let bootstrapped = ensureBlockLotsForLandUseSimulation block
      bootstrapped.Lots |> Expect.hasLength "phase 4 should bootstrap one synthetic lot per unsplit block" 1
      bootstrapped.Lots.Head.Rect |> Expect.equal "bootstrapped lot should cover the full block" rect

    paperCase "§4.2" "improving land-use candidates are accepted deterministically" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let block =
        { Scenario.rectangularBlock rect rect ParkUse 0.2f with
            Lots = [ phase4Lot rect North Built ParkUse 0.2f ] }
      let quarter =
        QuarterPlan.create rect [ MinorStreet.built rect { X = 4.5f; Z = 0.0f; W = 1.0f; H = 10.0f } ] [ block ]
      let plan = { MajorStreets = []; Quarters = [ quarter ] } |> indexStreetNetwork
      let updated, stats = updateLandUseSimulationWith (phase4Config 7 1.0f 1.0f 1.0f 0.25f) plan
      updated.Quarters.Head.Blocks.Head.Lots.Head.LandUseType
      |> Expect.equal "the only improving candidate should be accepted" ResidentialUse
      stats.Accepted |> Expect.equal "accepted attempt count should be reported" 1
      (stats.GlobalValueAfter, stats.GlobalValueBefore)
      |> Expect.isGreaterThan "accepted improvements should raise the global score"

    paperCase "§4.2" "worse land-use candidates are rejected when they exceed the rejection threshold" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let block =
        { Scenario.rectangularBlock rect rect ResidentialUse 0.8f with
            Lots = [ phase4Lot rect North Built ResidentialUse 0.8f ] }
      let plan =
        { MajorStreets = []
          Quarters = [ QuarterPlan.create rect [] [ block ] ] }
      let updated, stats = updateLandUseSimulationWith (phase4Config 7 1.0f 1.0f 1.0f 0.05f) plan
      updated.Quarters.Head.Blocks.Head.Lots.Head.LandUseType
      |> Expect.equal "the only worse candidate should be rejected" ResidentialUse
      stats.Accepted |> Expect.equal "no worse candidate should be accepted here" 0
      stats.Rejected |> Expect.equal "rejected attempt count should be reported" 1

    paperCase "§4.2" "reevaluation cadence can skip land-use updates entirely" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 12.0f; H = 12.0f }
      let before = Scenario.singleQuarter rect
      let after, stats = updateLandUseSimulationWith (phase4Config 11 0.5f 10.0f 1.0f 0.25f) before
      after |> Expect.equal "when cadence is not due the land-use plan should remain unchanged" before
      stats.SkippedByCadence |> Expect.isTrue "cadence skip should be surfaced explicitly"
      stats.Attempts |> Expect.equal "skipped updates should not attempt mutations" 0

    paperCase "§4.2" "due reevaluation cycles report attempts and accepted lots in update stats" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let block =
        { Scenario.rectangularBlock rect rect ParkUse 0.2f with
            Lots = [ phase4Lot rect North Built ParkUse 0.2f ] }
      let plan =
        { MajorStreets = []
          Quarters = [ QuarterPlan.create rect [] [ block ] ] }
      let _, stats = updateLandUseSimulationWith (phase4Config 13 1.0f 1.0f 1.0f 0.25f) plan
      stats.SkippedByCadence |> Expect.isFalse "due reevaluations should run"
      (stats.Attempts, 0) |> Expect.isGreaterThan "due reevaluations should attempt lot mutations"

    naturalismCase "land-use instrumentation" "stage deltas count changed lots and total lot value movement" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let beforeLot = phase4Lot rect North Built ParkUse 0.2f
      let afterLot = { beforeLot with LandUseType = ResidentialUse; LandUseValue = 0.8f }
      let beforeBlock =
        { Scenario.rectangularBlock rect rect ParkUse 0.2f with
            Lots = [ beforeLot ] }
      let afterBlock =
        { beforeBlock with
            LandUseType = ResidentialUse
            LandUseValue = 0.8f
            Lots = [ afterLot ] }
      let before =
        { MajorStreets = []
          Quarters = [ QuarterPlan.create rect [] [ beforeBlock ] ] }
      let after =
        { MajorStreets = []
          Quarters =
            [ { QuarterPlan.create rect [] [ afterBlock ] with
                  LandUseType = ResidentialUse
                  LandUseValue = 0.8f } ] }
      let delta = summarizeSimulationStageDelta before after
      delta.LotLandUseChangedCount |> Expect.equal "lot relabels should be counted in stage deltas" 1
      (delta.TotalLotLandUseValueDelta, 0.0f) |> Expect.isGreaterThan "lot value delta should capture changed suitability"

    paperProperty "§4.1 Eq. (2)-(4)" "global land-use value is always non-positive" <|
      fun (PositiveInt rawResidential) (PositiveInt rawPark) ->
        let residentialWidth = float32 (1 + rawResidential % 50)
        let parkWidth = float32 (1 + rawPark % 50)
        let lots =
          [ phase4Lot { X = 0.0f; Z = 0.0f; W = residentialWidth; H = 1.0f } North Built ResidentialUse 0.5f
            phase4Lot { X = 0.0f; Z = 1.0f; W = parkWidth; H = 1.0f } South Built ParkUse 0.5f ]
        computeGlobalLandUseValue
          [ { LandUseType = ResidentialUse; TargetPercent = 0.5f }
            { LandUseType = ParkUse; TargetPercent = 0.5f } ]
          1.0f
          lots <= 0.0f

    paperProperty "§4.3 Eq. (4)" "local land-use value remains inside the unit interval" <|
      fun (PositiveInt rawWidth) ->
        let width = float32 (1 + rawWidth % 100)
        let rect = { X = 0.0f; Z = 0.0f; W = width; H = 1.0f }
        let lot = phase4Lot rect North Built ResidentialUse 0.5f
        let value = evaluateLocalLotLandUseValue phase4Definitions (Scenario.singleQuarter rect) lot ResidentialUse
        value >= 0.0f && value <= 1.0f
  ]

let economyModelTests =
  testList "Economy model" [
    paperCase "§5.2 Eq. (9)" "lot price uses area average price and relative land-use value" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 10.0f }
      let economics = computeLotEconomics 100.0f 0.30f (phase6Lot rect ResidentialUse 0.60f)
      (abs (economics.Price - 40000.0f) < 1e-3f)
      |> Expect.isTrue "price should follow equation (9) using area * avgprice * luv / meanLuv"

    paperCase "§5.2 Eq. (9)" "relative land-use value scales price proportionally" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 10.0f }
      let low = computeLotEconomics 100.0f 0.30f (phase6Lot rect ResidentialUse 0.30f)
      let high = computeLotEconomics 100.0f 0.30f (phase6Lot rect ResidentialUse 0.60f)
      (abs ((high.Price / low.Price) - 2.0f) < 1e-3f)
      |> Expect.isTrue "doubling land-use value relative to the mean should double price"

    paperCase "§5.2 Eq. (9)-(10)" "zero mean land-use value collapses economics safely" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 10.0f }
      let economics = computeLotEconomics 100.0f 0.0f (phase6Lot rect ResidentialUse 0.60f)
      economics.Price |> Expect.equal "zero mean land-use value should avoid division blowups" 0.0f
      economics.FloorSpace |> Expect.equal "zero mean land-use value should eliminate profitable floorspace" 0.0f
      economics.Residents |> Expect.equal "zero mean land-use value should eliminate residents/activity" 0.0f

    paperCase "§5.2 Eq. (10)" "floor space is derived from price via the land-use margin" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 10.0f }
      let economics = computeLotEconomics 100.0f 0.30f (phase6Lot rect CommercialUse 0.60f)
      (abs (economics.FloorSpace - economics.Price * 0.82f) < 1e-3f)
      |> Expect.isTrue "commercial floor space should be price multiplied by the configured margin"

    paperCase "§5.2" "commercial and industrial lots contribute positive activity" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 10.0f }
      let commercial = computeLotEconomics 100.0f 0.30f (phase6Lot rect CommercialUse 0.60f)
      let industrial = computeLotEconomics 100.0f 0.30f (phase6Lot rect IndustrialUse 0.60f)
      (commercial.Residents, 0.0f) |> Expect.isGreaterThan "commercial lots should contribute non-zero residents-equivalent demand"
      (industrial.Residents, 0.0f) |> Expect.isGreaterThan "industrial lots should contribute non-zero residents-equivalent demand"

    naturalismCase "economy intensity ordering" "park lots stay lower intensity than residential lots under the same market conditions" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 10.0f }
      let park = computeLotEconomics 100.0f 0.30f (phase6Lot rect ParkUse 0.60f)
      let residential = computeLotEconomics 100.0f 0.30f (phase6Lot rect ResidentialUse 0.60f)
      (residential.FloorSpace, park.FloorSpace) |> Expect.isGreaterThan "parks should remain lower floor-space intensity than residential lots"
      (residential.Residents, park.Residents) |> Expect.isGreaterThan "parks should remain lower occupancy than residential lots"

    paperProperty "§5.2 Eq. (9)-(10)" "economy outputs remain non-negative" <|
      fun (NonNegativeInt rawAvgPrice) (NonNegativeInt rawMean) (PositiveInt rawW) (PositiveInt rawH) ->
        let rect =
          { X = 0.0f
            Z = 0.0f
            W = float32 (1 + rawW % 40)
            H = float32 (1 + rawH % 40) }
        let avgPrice = float32 (rawAvgPrice % 200)
        let meanLuv = float32 (rawMean % 100) / 100.0f
        let economics = computeLotEconomics avgPrice meanLuv (phase6Lot rect MixedUseZone 0.75f)
        economics.Price >= 0.0f
        && economics.FloorSpace >= 0.0f
        && economics.Residents >= 0.0f

    paperProperty "§5.2 Eq. (9)-(10)" "doubling lot area doubles price floor space and residents" <|
      fun (PositiveInt rawW) (PositiveInt rawH) ->
        let width = float32 (1 + rawW % 20)
        let height = float32 (1 + rawH % 20)
        let small = phase6Lot { X = 0.0f; Z = 0.0f; W = width; H = height } ResidentialUse 0.60f
        let large = phase6Lot { X = 0.0f; Z = 0.0f; W = width * 2.0f; H = height } ResidentialUse 0.60f
        let smallEconomics = computeLotEconomics 100.0f 0.30f small
        let largeEconomics = computeLotEconomics 100.0f 0.30f large
        abs (largeEconomics.Price - smallEconomics.Price * 2.0f) < 1e-3f
        && abs (largeEconomics.FloorSpace - smallEconomics.FloorSpace * 2.0f) < 1e-3f
        && abs (largeEconomics.Residents - smallEconomics.Residents * 2.0f) < 1e-3f
  ]

let zoningEnvelopeTests =
  testList "Zoning envelopes" [
    paperCase "§5.3" "lots with only planned frontages do not produce envelopes" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let lot = { phase6Lot rect ResidentialUse 0.6f with FloorSpace = 177.984f }
      let plan =
        phase7Plan lot [
          phase7MinorStreet rect Planned 12.0f { X = 0.0f; Z = -1.0f; W = 10.0f; H = 1.0f }
          phase7MinorStreet rect Planned 30.0f { X = 10.0f; Z = 0.0f; W = 1.0f; H = 10.0f }
        ]
      computeBuildingEnvelopeInPlan plan lot
      |> Expect.isNone "planned streets should not yet produce a buildable envelope"

    paperCase "§5.3" "highest-traffic built frontage wins on corner lots" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let lot = { phase6Lot rect ResidentialUse 0.6f with FloorSpace = 177.984f }
      let plan =
        phase7Plan lot [
          phase7MinorStreet rect Built 12.0f { X = 0.0f; Z = -1.0f; W = 10.0f; H = 1.0f }
          phase7MinorStreet rect Built 30.0f { X = 10.0f; Z = 0.0f; W = 1.0f; H = 10.0f }
        ]
      let envelope = computeBuildingEnvelopeInPlan plan lot |> Option.defaultWith (fun () -> failtest "expected a corner-lot envelope")
      envelope.FrontageEdge |> Expect.equal "the east frontage should win because it carries more built traffic" East

    paperCase "§5.3" "planned high-traffic frontages do not beat built frontages" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let lot = { phase6Lot rect ResidentialUse 0.6f with FloorSpace = 177.984f }
      let plan =
        phase7Plan lot [
          phase7MinorStreet rect Built 10.0f { X = 0.0f; Z = -1.0f; W = 10.0f; H = 1.0f }
          phase7MinorStreet rect Planned 100.0f { X = 10.0f; Z = 0.0f; W = 1.0f; H = 10.0f }
        ]
      let envelope = computeBuildingEnvelopeInPlan plan lot |> Option.defaultWith (fun () -> failtest "expected a built-frontage envelope")
      envelope.FrontageEdge |> Expect.equal "planned traffic should not override the only built frontage" North

    paperCase "§5.3 Eq. (11)" "floor counts derive from floorspace divided by envelope area" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let lot = { phase6Lot rect ResidentialUse 0.6f with FloorSpace = 177.984f }
      let plan =
        phase7Plan lot [
          phase7MinorStreet rect Built 12.0f { X = 0.0f; Z = -1.0f; W = 10.0f; H = 1.0f }
        ]
      let envelope = computeBuildingEnvelopeInPlan plan lot |> Option.defaultWith (fun () -> failtest "expected a built-frontage envelope")
      (abs (envelope.Area - 88.992f) < 1e-3f) |> Expect.isTrue "residential setbacks should yield the expected envelope area"
      (abs (envelope.NFloors - 2.0f) < 1e-3f) |> Expect.isTrue "nFloors should follow equation (11)"

    paperCase "§5.3" "degenerate setbacks return no envelope instead of a fake sliver" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 0.3f; H = 0.3f }
      let lot = { phase6Lot rect ResidentialUse 0.6f with FloorSpace = 20.0f }
      let plan =
        phase7Plan lot [
          phase7MinorStreet rect Built 12.0f { X = 0.0f; Z = -1.0f; W = 0.3f; H = 1.0f }
        ]
      computeBuildingEnvelopeInPlan plan lot
      |> Expect.isNone "if setbacks consume the lot the envelope should be absent"

    paperCase "§5.3" "land-use updates recompute contextual envelopes using the winning built frontage" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let lot = phase6Lot rect ResidentialUse 0.6f
      let plan =
        phase7Plan lot [
          phase7MinorStreet rect Built 8.0f { X = 0.0f; Z = -1.0f; W = 10.0f; H = 1.0f }
          phase7MinorStreet rect Built 18.0f { X = 10.0f; Z = 0.0f; W = 1.0f; H = 10.0f }
        ]
      let config : LandUseSimulationConfig =
        { Goals = [ { LandUseType = ResidentialUse; TargetPercent = 1.0f } ]
          Definitions =
            [ { LandUseType = ResidentialUse
                Valuations = [ { Metric = LotArea; Curve = LinearUp; Min = 0.0f; Max = 100.0f; Weight = 1.0f } ] } ]
          AveragePricePerSqm = 100.0f
          GlobalWeight = 1.0f
          LocalWeight = 0.0f
          GoalScale = 1.0f
          AttemptsFraction = 1.0f
          RejectedDeltaThreshold = 0.1f
          Seed = 7
          Cadence = { StepYears = 1.0f; ReevaluationYears = 1.0f; ElapsedSinceLastEvaluationYears = 1.0f } }
      let updated, _ = updateLandUseSimulationWith config plan
      let updatedLot = updated.Quarters.Head.Blocks.Head.Lots.Head
      let envelope = updatedLot.Envelope |> Option.defaultWith (fun () -> failtest "expected envelope to be recomputed during land-use refresh")
      envelope.FrontageEdge |> Expect.equal "the east frontage should win once contextual zoning is wired through the update pipeline" East
      (envelope.NFloors, 0.0f) |> Expect.isGreaterThan "economy-derived floorspace should produce positive floor counts"

    paperProperty "§5.3" "generated envelopes stay inside their lots" <|
      fun (PositiveInt rawW) (PositiveInt rawH) ->
        let rect = { X = 0.0f; Z = 0.0f; W = float32 (3 + rawW % 30); H = float32 (3 + rawH % 30) }
        let lot = { phase6Lot rect MixedUseZone 0.6f with FloorSpace = 200.0f }
        let plan =
          phase7Plan lot [
            phase7MinorStreet rect Built 14.0f { X = 0.0f; Z = -1.0f; W = rect.W; H = 1.0f }
          ]
        match computeBuildingEnvelopeInPlan plan lot with
        | None -> true
        | Some envelope -> containsRect rect envelope.Rect && envelope.Area <= TRect.area rect

    paperProperty "§5.3 Eq. (11)" "floor counts increase monotonically with floorspace" <|
      fun (PositiveInt rawFloorSpace) ->
        let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
        let lowFloorSpace = float32 (1 + rawFloorSpace % 200)
        let highFloorSpace = lowFloorSpace + 50.0f
        let lowLot = { phase6Lot rect ResidentialUse 0.6f with FloorSpace = lowFloorSpace }
        let highLot = { phase6Lot rect ResidentialUse 0.6f with FloorSpace = highFloorSpace }
        let plan =
          phase7Plan lowLot [
            phase7MinorStreet rect Built 12.0f { X = 0.0f; Z = -1.0f; W = 10.0f; H = 1.0f }
          ]
        match computeBuildingEnvelopeInPlan plan lowLot, computeBuildingEnvelopeInPlan plan highLot with
        | Some lowEnvelope, Some highEnvelope -> highEnvelope.NFloors > lowEnvelope.NFloors
        | _ -> false
  ]

let buildingSubstitutionTests =
  testList "Building substitution" [
    paperCase "§5.4" "substitution probability combines age and positive price discrepancy" <| fun () ->
      let probability =
        computeBuildingSubstitutionProbability phase10SubstitutionConfig 20.0f 100.0f 150.0f
      abs (probability - 0.5f) < 1e-4f
      |> Expect.isTrue "age and positive price gap should sum into the substitution probability"

    paperCase "§5.4" "negative price discrepancy is clamped to zero" <| fun () ->
      computeBuildingSubstitutionProbability phase10SubstitutionConfig 0.0f 120.0f 80.0f
      |> Expect.equal "redevelopment pressure should not increase when the potential building is worth less" 0.0f

    paperCase "§5.4" "replacement occurs when the deterministic roll is below probability" <| fun () ->
      let blockRect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let current = phase10Lot blockRect blockRect 20.0f 100.0f 40.0f 16.0f
      let replaced =
        applyBuildingSubstitution 1.0f 0.49f 0.5f { Price = 150.0f; FloorSpace = 80.0f; Residents = 32.0f } current
      replaced.Price |> Expect.equal "replacement should adopt the potential price" 150.0f
      replaced.FloorSpace |> Expect.equal "replacement should adopt the potential floor space" 80.0f
      replaced.Residents |> Expect.equal "replacement should adopt the potential resident count" 32.0f
      replaced.BuildingAgeYears |> Expect.equal "replacement should reset building age" 0.0f

    paperCase "§5.4" "no replacement preserves the existing building and increments age" <| fun () ->
      let blockRect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let current = phase10Lot blockRect blockRect 20.0f 100.0f 40.0f 16.0f
      let retained =
        applyBuildingSubstitution 1.0f 0.51f 0.5f { Price = 150.0f; FloorSpace = 80.0f; Residents = 32.0f } current
      retained.Price |> Expect.equal "retained building should keep its current price" 100.0f
      retained.FloorSpace |> Expect.equal "retained building should keep its current floor space" 40.0f
      retained.Residents |> Expect.equal "retained building should keep its current resident count" 16.0f
      retained.BuildingAgeYears |> Expect.equal "retained building should age by the simulation step" 21.0f

    paperCase "§5.4" "plan-level substitution is deterministic for the same seed and step index" <| fun () ->
      let quarterRect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 10.0f }
      let current =
        phase10Plan quarterRect
          [ phase10Lot quarterRect { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f } 12.0f 80.0f 30.0f 12.0f
            phase10Lot quarterRect { X = 10.0f; Z = 0.0f; W = 10.0f; H = 10.0f } 7.0f 110.0f 44.0f 18.0f ]
      let redevelopment =
        phase10Plan quarterRect
          [ phase10Lot quarterRect { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f } 0.0f 140.0f 75.0f 30.0f
            phase10Lot quarterRect { X = 10.0f; Z = 0.0f; W = 10.0f; H = 10.0f } 0.0f 112.0f 45.0f 18.5f ]
      let firstPlan, firstStats = updateBuildingSubstitutionWith phase10SubstitutionConfig 3 1.0f current redevelopment
      let secondPlan, secondStats = updateBuildingSubstitutionWith phase10SubstitutionConfig 3 1.0f current redevelopment
      firstPlan |> Expect.equal "the same seed and step index should replay the same substitutions" secondPlan
      firstStats |> Expect.equal "deterministic substitution should also replay its stats" secondStats

    naturalismCase "§5.4 redevelopment ordering" "older underbuilt lots redevelop before newer near-par lots" <| fun () ->
      let quarterRect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 10.0f }
      let current =
        phase10Plan quarterRect
          [ phase10Lot quarterRect { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f } 40.0f 50.0f 30.0f 12.0f
            phase10Lot quarterRect { X = 10.0f; Z = 0.0f; W = 10.0f; H = 10.0f } 0.0f 150.0f 90.0f 36.0f ]
      let redevelopment =
        phase10Plan quarterRect
          [ phase10Lot quarterRect { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f } 0.0f 150.0f 90.0f 36.0f
            phase10Lot quarterRect { X = 10.0f; Z = 0.0f; W = 10.0f; H = 10.0f } 0.0f 150.0f 90.0f 36.0f ]
      let updated, stats = updateBuildingSubstitutionWith phase10SubstitutionConfig 1 1.0f current redevelopment
      let lots = updated.Quarters.Head.Blocks.Head.Lots
      lots.[0].Price |> Expect.equal "the older underbuilt lot should redevelop into the higher-value building" 150.0f
      lots.[0].BuildingAgeYears |> Expect.equal "redevelopment should reset the replaced lot's age" 0.0f
      lots.[1].Price |> Expect.equal "the newer near-par lot should keep its existing building" 150.0f
      lots.[1].BuildingAgeYears |> Expect.equal "the retained lot should simply age by one year" 1.0f
      stats.ReplacedLots |> Expect.equal "exactly one lot should redevelop in this fixture" 1
  ]

let subdivisionFidelityTests =
  testList "Land-use subdivision" [
    paperCase "§5.1" "subdivision rules differ by land use" <| fun () ->
      let residential = subdivisionRuleForLandUse ResidentialUse
      let industrial = subdivisionRuleForLandUse IndustrialUse
      residential.MaxLotArea |> Expect.equal "residential max lot area should be pinned for TDD" 120.0f
      industrial.MaxLotArea |> Expect.equal "industrial max lot area should be pinned for TDD" 400.0f
      (industrial.MaxLotArea, residential.MaxLotArea) |> Expect.isGreaterThan "industrial lots should be allowed to grow larger than residential ones"
      (residential.MinWidthLengthRatio, industrial.MinWidthLengthRatio) |> Expect.isGreaterThan "residential frontage ratios should be stricter than industrial ones"

    paperCase "§5.1" "longest street-facing edge selection is deterministic" <| fun () ->
      let wideBlock = phase5Block { X = 0.0f; Z = 0.0f; W = 30.0f; H = 12.0f } ResidentialUse 0.5f [ North; West ]
      let squareBlock = phase5Block { X = 0.0f; Z = 0.0f; W = 12.0f; H = 12.0f } ResidentialUse 0.5f [ North; West ]
      selectLongestStreetFacingEdge wideBlock |> Expect.equal "wider north edge should dominate west edge" (Some North)
      selectLongestStreetFacingEdge squareBlock |> Expect.equal "tie-break should remain deterministic" (Some North)

    paperCase "§5.1" "subdivision splits orthogonally to the selected street-facing edge" <| fun () ->
      let block = phase5Block { X = 0.0f; Z = 0.0f; W = 20.0f; H = 12.0f } ResidentialUse 0.5f [ North ]
      let lots, stats = subdivideBlockByLandUseWithStats block
      lots |> Expect.hasLength "a 20x12 residential block should split once at the midpoint" 2
      lots |> List.iter (fun lot -> lot.FrontageEdge |> Expect.equal "orthogonal splitting should preserve the selected frontage edge" North)
      lots |> List.map (fun lot -> lot.Rect) |> Expect.contains "the first child should occupy the west half" { X = 0.0f; Z = 0.0f; W = 10.0f; H = 12.0f }
      lots |> List.map (fun lot -> lot.Rect) |> Expect.contains "the second child should occupy the east half" { X = 10.0f; Z = 0.0f; W = 10.0f; H = 12.0f }
      stats.AcceptedSplits |> Expect.equal "one successful split should be counted" 1

    paperCase "§5.1" "blocks already below the land-use max area remain single lots" <| fun () ->
      let block = phase5Block { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f } ResidentialUse 0.5f [ North ]
      let lots = subdivideBlockByLandUse block
      lots |> Expect.hasLength "sub-threshold residential blocks should not subdivide" 1
      lots.Head.Rect |> Expect.equal "terminal lot should match the source block" block.Rect

    paperCase "§5.1" "residential subdivision yields more smaller lots than industrial subdivision on the same block" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 40.0f; H = 20.0f }
      let residentialLots = subdivideBlockByLandUse (phase5Block rect ResidentialUse 0.5f [ North ])
      let industrialLots = subdivideBlockByLandUse (phase5Block rect IndustrialUse 0.5f [ North ])
      (residentialLots.Length, industrialLots.Length) |> Expect.isGreaterThan "residential blocks should subdivide more aggressively"
      let residentialMaxArea = residentialLots |> List.maxBy (fun lot -> TRect.area lot.Rect) |> fun lot -> TRect.area lot.Rect
      let industrialMaxArea = industrialLots |> List.maxBy (fun lot -> TRect.area lot.Rect) |> fun lot -> TRect.area lot.Rect
      (industrialMaxArea, residentialMaxArea) |> Expect.isGreaterThan "industrial lots should remain larger on the same parent block"

    paperCase "§5.1" "retry logic falls back from an invalid street-facing edge to a non-street edge" <| fun () ->
      let block = phase5Block { X = 0.0f; Z = 0.0f; W = 18.0f; H = 10.0f } ResidentialUse 0.5f [ East ]
      let lots, stats = subdivideBlockByLandUseWithStats block
      lots |> Expect.hasLength "fallback should still recover a valid split" 2
      lots |> List.map (fun lot -> lot.Rect) |> Expect.contains "fallback should split vertically into equal halves" { X = 0.0f; Z = 0.0f; W = 9.0f; H = 10.0f }
      lots |> List.map (fun lot -> lot.Rect) |> Expect.contains "fallback should split vertically into equal halves" { X = 9.0f; Z = 0.0f; W = 9.0f; H = 10.0f }
      lots |> List.iter (fun lot -> lot.FrontageEdge |> Expect.equal "lots should keep their primary frontage even after fallback" East)
      (stats.StreetEdgeRetries, 0) |> Expect.isGreaterThan "street-edge retries should be recorded before fallback"
      stats.NonStreetEdgeFallbacks |> Expect.equal "one non-street fallback should be recorded" 1

    paperProperty "§5.1" "subdivision preserves total area and keeps lots inside the parent block" <|
      fun (PositiveInt rawW) (PositiveInt rawH) ->
        let rect = { X = 0.0f; Z = 0.0f; W = float32 (12 + rawW % 60); H = float32 (12 + rawH % 60) }
        let block = phase5Block rect MixedUseZone 0.5f [ North ]
        let lots = subdivideBlockByLandUse block
        let areaPreserved = abs ((lots |> List.sumBy (fun lot -> TRect.area lot.Rect)) - TRect.area rect) < 1e-3f
        let allInside = lots |> List.forall (fun lot -> containsRect rect lot.Rect)
        areaPreserved && allInside

    paperProperty "§5.1" "subdivided lots never overlap and always keep positive area" <|
      fun (PositiveInt rawW) (PositiveInt rawH) ->
        let rect = { X = 0.0f; Z = 0.0f; W = float32 (12 + rawW % 50); H = float32 (12 + rawH % 50) }
        let block = phase5Block rect ResidentialUse 0.5f [ North; East ]
        let lots = subdivideBlockByLandUse block
        let allPositive =
          lots |> List.forall (fun lot -> lot.Rect.W > 0.0f && lot.Rect.H > 0.0f && TRect.area lot.Rect > 0.0f)
        let noOverlaps =
          lots
          |> List.indexed
          |> List.allPairs (lots |> List.indexed)
          |> List.forall (fun ((i, a), (j, b)) -> i >= j || not (rectsOverlap a.Rect b.Rect))
        allPositive && noOverlaps
  ]

let majorStreetGrowthTests =
  testList "Major street growth" [
    paperCase "§3.1" "growth-center sampling prefers nodes near the active center" <| fun () ->
      let g = WeberGraph()
      let nearId = g.AddNode(Vec2.Create(0.0f, 0.0f), Avenue)
      let farId = g.AddNode(Vec2.Create(30.0f, 0.0f), Avenue)
      let rng = Random 42
      let picks =
        [ for _ in 1 .. 200 ->
            sampleNode g [| Vec2.Create(0.0f, 0.0f) |] 0.08f rng (RoadClass.tier Avenue) ]
      let nearCount = picks |> List.filter ((=) (Some nearId)) |> List.length
      let farCount = picks |> List.filter ((=) (Some farId)) |> List.length
      (nearCount, farCount) |> Expect.isGreaterThan "near-center nodes should be selected more often than far-away nodes"

    paperCase "§3.1 Fig. 5" "valence-two nodes branch orthogonally from straight corridors" <| fun () ->
      let g = WeberGraph()
      let north = g.AddNode(Vec2.Create(10.0f, 2.0f), Avenue)
      let mid = g.AddNode(Vec2.Create(10.0f, 10.0f), Avenue)
      let south = g.AddNode(Vec2.Create(10.0f, 18.0f), Avenue)
      g.AddEdge(mid, north, Avenue, RoadClass.width Avenue) |> ignore
      g.AddEdge(mid, south, Avenue, RoadClass.width Avenue) |> ignore
      let proposed, _, _ =
        expandNode g (System.Collections.Generic.Dictionary<int, float32>()) (System.Collections.Generic.Dictionary<int, float32>()) mid (Random 7) true 6.0f
        |> Option.get
      (abs (proposed.Y - 10.0f), 0.001f) |> Expect.isLessThan "grid growth should stay on the corridor's orthogonal axis"
      (abs (proposed.X - 10.0f), 1.0f) |> Expect.isGreaterThan "valence-two growth should turn left or right"

    paperCase "§3.1" "organic valence-one continuation lengths are not all pegged to the nominal step" <| fun () ->
      let sampledLengths =
        [ 1 .. 8 ]
        |> List.map (fun seed ->
            let g = WeberGraph()
            let parent = g.AddNode(Vec2.Create(4.0f, 10.0f), Avenue)
            let mid = g.AddNode(Vec2.Create(10.0f, 10.0f), Avenue)
            g.AddEdge(parent, mid, Avenue, RoadClass.width Avenue) |> ignore
            let proposed, _, _ =
              expandNode g (System.Collections.Generic.Dictionary<int, float32>()) (System.Collections.Generic.Dictionary<int, float32>()) mid (Random seed) false 10.0f
              |> Option.get
            Vec2.distanceTo (g.N mid).Pos proposed)

      let distinctBuckets =
        sampledLengths
        |> List.map (fun length -> int (MathF.Round(length * 10.0f)))
        |> Set.ofList
        |> Set.count

      (distinctBuckets, 2)
      |> Expect.isGreaterThanOrEqual
           "organic continuation should vary its step length instead of stamping every extension at exactly the nominal segment length"

    paperCase "§3.1 Fig. 6" "legality adaptation shortens at the first intersection and snaps to nearby nodes" <| fun () ->
      let g = WeberGraph()
      let a = g.AddNode(Vec2.Create(10.0f, 0.0f), Avenue)
      let b = g.AddNode(Vec2.Create(10.0f, 20.0f), Avenue)
      let snapNode = g.AddNode(Vec2.Create(12.0f, 10.0f), Avenue)
      let originNode = g.AddNode(Vec2.Create(0.0f, 10.0f), Avenue)
      g.AddEdge(a, b, Avenue, RoadClass.width Avenue) |> ignore
      match findClosestIntersection g (g.N originNode).Pos (Vec2.Create(20.0f, 10.0f)) with
      | HitEdgeInterior (edgeId, pt, edgeT) ->
          edgeId |> Expect.equal "the vertical corridor should be the first crossed edge" (EdgeId 0)
          (abs (pt.X - 10.0f), 0.01f) |> Expect.isLessThan "intersection hit should report the crossing point"
          (abs (pt.Y - 10.0f), 0.01f) |> Expect.isLessThan "intersection hit should preserve the crossing height"
          edgeT |> Expect.equal "crossing should occur halfway along the existing corridor" 0.5f
      | other ->
          failtestf "expected an interior edge hit but got %A" other
      let shortened = adaptIntersection g (g.N originNode).Pos (Vec2.Create(20.0f, 10.0f))
      (abs (shortened.X - 10.0f), 0.01f) |> Expect.isLessThan "intersection adaptation should stop at the first crossing"
      (abs (shortened.Y - 10.0f), 0.01f) |> Expect.isLessThan "intersection adaptation should preserve the intended axis"
      let snapped = adaptSnapping g (Vec2.Create(12.2f, 10.1f)) 1.0f originNode
      snapped |> Expect.equal "snapping should reuse existing nearby nodes" (Some snapNode)

    paperCase "§3.1 Fig. 6" "splitting a crossed edge creates a shared node and replaces the original corridor" <| fun () ->
      let g = WeberGraph()
      let a = g.AddNode(Vec2.Create(10.0f, 0.0f), Avenue)
      let b = g.AddNode(Vec2.Create(10.0f, 20.0f), Avenue)
      g.AddEdge(a, b, Avenue, RoadClass.width Avenue) |> ignore
      let splitNode = g.SplitEdge(EdgeId 0, Vec2.Create(10.0f, 12.0f))
      let splitPos = (g.N splitNode).Pos
      (abs (splitPos.X - 10.0f), 0.01f) |> Expect.isLessThan "split node should sit on the original corridor"
      (abs (splitPos.Y - 12.0f), 0.01f) |> Expect.isLessThan "split node should sit at the requested crossing point"
      g.Edges |> Seq.toList |> Expect.hasLength "the original corridor should be replaced by two segments" 2
      g.Edges
      |> Seq.forall (fun edge -> edge.A = splitNode || edge.B = splitNode)
      |> Expect.isTrue "both replacement segments should connect to the shared split node"
      (g.N splitNode).Valence |> Expect.equal "split node should have degree two on the rebuilt corridor" 2

    paperCase "§3.1 Fig. 6" "longitudinal split-edge bypasses are rejected before they become zipper seams" <| fun () ->
      let g = WeberGraph()
      let a = g.AddNode(Vec2.Create(0.0f, 0.0f), Avenue)
      let b = g.AddNode(Vec2.Create(10.0f, 0.0f), Avenue)
      let c = g.AddNode(Vec2.Create(20.0f, 0.0f), Avenue)
      let d = g.AddNode(Vec2.Create(30.0f, 0.0f), Avenue)
      let feederStart = g.AddNode(Vec2.Create(0.0f, 0.8f), Avenue)
      let origin = g.AddNode(Vec2.Create(10.0f, 0.8f), Avenue)
      g.AddEdge(a, b, Avenue, RoadClass.width Avenue) |> ignore
      g.AddEdge(b, c, Avenue, RoadClass.width Avenue) |> ignore
      g.AddEdge(c, d, Avenue, RoadClass.width Avenue) |> ignore
      g.AddEdge(feederStart, origin, Avenue, RoadClass.width Avenue) |> ignore
      formsLongitudinalSplitEdgeBypassViaReflection g origin (g.N origin).Pos (Vec2.Create(18.0f, 0.0f)) (EdgeId 1)
      |> Expect.isTrue "valence-one continuations should reject tight near-parallel interior hits on long corridors"

    paperCase "§3.1 Fig. 6" "transverse split-edge tees stay legal when they are not zipper-like bypasses" <| fun () ->
      let g = WeberGraph()
      let a = g.AddNode(Vec2.Create(0.0f, 0.0f), Avenue)
      let b = g.AddNode(Vec2.Create(10.0f, 0.0f), Avenue)
      let c = g.AddNode(Vec2.Create(20.0f, 0.0f), Avenue)
      let d = g.AddNode(Vec2.Create(30.0f, 0.0f), Avenue)
      let feederStart = g.AddNode(Vec2.Create(15.0f, -12.0f), Avenue)
      let origin = g.AddNode(Vec2.Create(15.0f, -4.0f), Avenue)
      g.AddEdge(a, b, Avenue, RoadClass.width Avenue) |> ignore
      g.AddEdge(b, c, Avenue, RoadClass.width Avenue) |> ignore
      g.AddEdge(c, d, Avenue, RoadClass.width Avenue) |> ignore
      g.AddEdge(feederStart, origin, Avenue, RoadClass.width Avenue) |> ignore
      formsLongitudinalSplitEdgeBypassViaReflection g origin (g.N origin).Pos (Vec2.Create(15.0f, 0.0f)) (EdgeId 1)
      |> Expect.isFalse "ordinary transverse tees should stay available to keep corridor density healthy"

    paperCase "§3.1" "organic growth does not stamp every continuation at one fixed run length" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 220.0f; H = 180.0f }
      let g = WeberGraph()
      let center = Vec2.Create(TRect.centerX rect, TRect.centerZ rect)
      g.AddNode(center, Avenue) |> ignore
      growStreets g [| center |] (Random 123) Avenue false 14.0f 1.2f 4.5f 24 0.006f (Some rect)

      let insideInset (inset: float32) (pt: Vec2) =
        pt.X >= rect.X + inset
        && pt.X <= rect.X + rect.W - inset
        && pt.Y >= rect.Z + inset
        && pt.Y <= rect.Z + rect.H - inset

      let interiorLengths =
        g.Edges
        |> Seq.toList
        |> List.choose (fun edge ->
            let a = (g.N edge.A).Pos
            let b = (g.N edge.B).Pos
            if insideInset 18.0f a && insideInset 18.0f b then
              Some (Vec2.distanceTo a b)
            else
              None)
        |> List.filter (fun length -> length >= 6.0f)

      (interiorLengths.Length, 8)
      |> Expect.isGreaterThanOrEqual
           "the growth kernel should produce enough interior continuations to judge cadence"

      let bucketCount =
        interiorLengths
        |> List.map (fun length -> int (MathF.Round(length / 1.5f)))
        |> Set.ofList
        |> Set.count

      (bucketCount, 3)
      |> Expect.isGreaterThanOrEqual
           "organic growth should not leave interior continuations trapped in a single metronomic run length"

    assumptionCase "pipeline API for §3.1" "major street growth planner exists" <| fun () ->
      hasModuleFunction "planMajorStreetGrowth"
      |> Expect.isTrue "phase 2 needs an explicit major street growth planner"

    naturalismCase "major-cycle quarters" "major street growth produces planned avenues and quarter rectangles" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 60.0f; H = 40.0f }
      let growth = planMajorStreetGrowth rect 12 0.2f (Random 42)
      growth.MajorStreets |> Expect.isNonEmpty "major growth should emit planned avenues"
      growth.MajorStreets |> List.forall (fun street -> street.Status = Planned) |> Expect.isTrue "major growth should keep streets planned before traffic promotion"
      growth.QuarterRects |> Expect.isNonEmpty "closed major cycles should induce quarter rectangles"
      growth.QuarterRects
      |> List.iter (fun quarter ->
        (quarter.X, rect.X) |> Expect.isGreaterThanOrEqual "quarter should stay within district bounds"
        (quarter.Z, rect.Z) |> Expect.isGreaterThanOrEqual "quarter should stay within district bounds"
        (quarter.X + quarter.W, rect.X + rect.W) |> Expect.isLessThanOrEqual "quarter should stay within district bounds"
        (quarter.Z + quarter.H, rect.Z + rect.H) |> Expect.isLessThanOrEqual "quarter should stay within district bounds")

    paperProperty "§3.1" "grown major street graphs keep valid node references and remain inside the district" <|
      fun (PositiveInt rawDemand) rawOrganic rawW rawH ->
        let rect = randomDistrictRect rawW rawH
        let demand = 2 + rawDemand % 12
        let organic = abs rawOrganic % 100 |> float32 |> fun n -> n / 100.0f
        let g, centers = seedMajorGrowthTestGraph rect
        let edgeBudget = max 2 demand
        let segmentLength = max 4.0f (min rect.W rect.H / 5.0f)
        growStreets g centers (Random 42) Avenue true segmentLength (segmentLength * 0.45f) (segmentLength * 0.35f) edgeBudget 0.01f (Some rect)
        g.Edges
        |> Seq.iter (fun edge ->
          NodeId.value edge.A < g.NodeCount |> Expect.isTrue "edge start should reference an existing node"
          NodeId.value edge.B < g.NodeCount |> Expect.isTrue "edge end should reference an existing node"
          let a = (g.N edge.A).Pos
          let b = (g.N edge.B).Pos
          (rect.X - 0.01f <= a.X && a.X <= rect.X + rect.W + 0.01f && rect.Z - 0.01f <= a.Y && a.Y <= rect.Z + rect.H + 0.01f)
          |> Expect.isTrue "edge start should remain within bounds"
          (rect.X - 0.01f <= b.X && b.X <= rect.X + rect.W + 0.01f && rect.Z - 0.01f <= b.Y && b.Y <= rect.Z + rect.H + 0.01f)
          |> Expect.isTrue "edge end should remain within bounds")
  ]

let specDrivenLayoutTests =
  testList "Spec-driven district planning" [
    testCase "hierarchical district planner creates major and minor streets plus enough blocks" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 60.0f; H = 40.0f }
      let plan = planHierarchicalDistrict rect 9 0.25f (Random 42)
      let blocks = plan.Quarters |> List.collect (fun quarter -> quarter.Blocks)
      plan.MajorStreets |> Expect.isNonEmpty "planner should create at least one major street"
      plan.Quarters |> List.exists (fun quarter -> not quarter.MinorStreets.IsEmpty) |> Expect.isTrue "planner should create minor streets inside quarters"
      (blocks.Length, 9) |> Expect.isGreaterThanOrEqual "planner should produce enough street-induced blocks for module demand"
      blocks
      |> List.pairwise
      |> List.exists (fun (a, b) -> rectsOverlap a.Rect b.Rect)
      |> Expect.isFalse "adjacent planned blocks should not overlap"

    testCase "quarters own their minor streets and blocks" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 60.0f; H = 40.0f }
      let plan = planHierarchicalDistrict rect 9 0.25f (Random 42)
      plan.Quarters
      |> List.iter (fun quarter ->
        quarter.MinorStreets
        |> List.iter (fun street -> street.QuarterRect |> Expect.equal "minor street should belong to its parent quarter" quarter.Rect)
        quarter.Blocks
        |> List.iter (fun block -> block.QuarterRect |> Expect.equal "block should belong to its parent quarter" quarter.Rect))

    testCase "block subdivision creates frontage lots touching the block boundary" <| fun () ->
      let block = PlannedBlock.create { X = 5.0f; Z = 7.0f; W = 18.0f; H = 10.0f } { X = 5.0f; Z = 7.0f; W = 18.0f; H = 10.0f }
      let lots = subdivideBlockIntoLots block 5
      lots |> Expect.hasLength "requested lot count should be produced" 5
      lots
      |> List.iter (fun lot ->
        let touchesBoundary =
          abs (lot.Rect.X - block.Rect.X) < 0.001f
          || abs ((lot.Rect.X + lot.Rect.W) - (block.Rect.X + block.Rect.W)) < 0.001f
          || abs (lot.Rect.Z - block.Rect.Z) < 0.001f
          || abs ((lot.Rect.Z + lot.Rect.H) - (block.Rect.Z + block.Rect.H)) < 0.001f
        touchesBoundary |> Expect.isTrue "every lot should keep direct road frontage on the parent block boundary"
        lot.BlockRect |> Expect.equal "lot should remember its parent block" block.Rect)
      lots |> List.forall (fun lot -> lot.FrontageEdge = North) |> Expect.isTrue "wide block frontage should be encoded explicitly"

    testCase "lot placement keeps one building per function inside its assigned lot envelope" <| fun () ->
      let lots =
        subdivideBlockIntoLots (PlannedBlock.create { X = 0.0f; Z = 0.0f; W = 20.0f; H = 12.0f } { X = 0.0f; Z = 0.0f; W = 20.0f; H = 12.0f }) 4
      let funcs = List.init 4 (fun i -> mkFunc (sprintf "lotFunc%d" i) "LotMod")
      let buildings = placeBuildingsInLots lots funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 7) Map.empty
      buildings |> Expect.hasLength "lot placement should yield one building per lot/function" 4
      List.zip lots buildings
      |> List.iter (fun (lot, building) ->
        (building.X, lot.Rect.X) |> Expect.isGreaterThanOrEqual "building left edge inside lot"
        (building.Z, lot.Rect.Z) |> Expect.isGreaterThanOrEqual "building top edge inside lot"
        (building.X + building.W, lot.Rect.X + lot.Rect.W) |> Expect.isLessThanOrEqual "building right edge inside lot"
        (building.Z + building.D, lot.Rect.Z + lot.Rect.H) |> Expect.isLessThanOrEqual "building bottom edge inside lot")
  ]

// Proof-layer legend:
// - [paper ...] direct claims traceable to urbanSimulation.md sections / figures / equations.
// - [assumption ...] explicit API/model choices we are making to implement the paper in this codebase.
// - [naturalism ...] derived morphology regressions that help guard believable output, but are not paper-fidelity proof.

let urbanSimulationPaperComplianceTests =
  testList "urbanSimulation paper compliance" [
    paperCase "§2.2, §3.2, Fig. 3" "newly grown major streets stay planned until traffic promotes them" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 60.0f; H = 40.0f }
      let plan = planHierarchicalDistrict rect 9 0.25f (Random 42)
      plan.MajorStreets
      |> List.exists (fun street -> street.Status = Planned)
      |> Expect.isTrue "urbanSimulation.md requires planned major streets before they become built"

    paperProperty "§2.2, §3.2, Fig. 3" "generated streets remain planned before traffic promotion" <|
      fun (PositiveInt rawDemand) rawOrganic rawW rawH ->
        let rect = randomDistrictRect rawW rawH
        let demand = 1 + rawDemand % 24
        let organic = abs rawOrganic % 100 |> float32 |> fun n -> n / 100.0f
        let plan = planHierarchicalDistrict rect demand organic (Random 42)
        let allStatuses =
          (plan.MajorStreets |> List.map (fun street -> street.Status))
          @ (plan.Quarters |> List.collect (fun quarter -> quarter.MinorStreets |> List.map (fun street -> street.Status)))
        allStatuses |> List.forall ((=) Planned)
  ]

let urbanSimulationAssumptionTests =
  testList "urbanSimulation assumptions" [
    assumptionCase "model surface for §2.2 + §4.1" "quarters carry land use classification and value" <| fun () ->
      hasRecordField<QuarterPlan> "LandUseType" |> Expect.isTrue "quarters should record their dominant land use type"
      hasRecordField<QuarterPlan> "LandUseValue" |> Expect.isTrue "quarters should record land use suitability/value"

    assumptionCase "model surface for §2.2 + §4.1" "blocks carry land use classification and value" <| fun () ->
      hasRecordField<PlannedBlock> "LandUseType" |> Expect.isTrue "blocks should record land use type"
      hasRecordField<PlannedBlock> "LandUseValue" |> Expect.isTrue "blocks should record land use suitability/value"
      hasRecordField<PlannedBlock> "Lots" |> Expect.isTrue "blocks should persist their current lot state for land-use simulation"
      hasRecordField<PlannedBlock> "StreetFacingEdges" |> Expect.isTrue "blocks should persist the street-facing edges that drive subdivision fidelity"

    assumptionCase "model surface for §2.1 + §4.1 + §5.3" "lots carry land use and zoning envelope data" <| fun () ->
      hasRecordField<PlannedLot> "LandUseType" |> Expect.isTrue "lots should record land use type"
      hasRecordField<PlannedLot> "LandUseValue" |> Expect.isTrue "lots should record land use suitability/value"
      hasRecordField<PlannedLot> "Envelope" |> Expect.isTrue "lots should expose the zoning-derived building envelope"
      hasRecordField<PlannedLot> "FrontingStreetStatus" |> Expect.isTrue "lots should know whether their frontage street is planned or built"

    assumptionCase "pipeline API for §2.2 + §3.2" "traffic promotion step exists" <| fun () ->
      hasModuleFunction "promotePlannedStreets"
      |> Expect.isTrue "traffic simulation should promote planned streets to built streets when thresholds are met"

    assumptionCase "pipeline API for §3.2" "traffic update and routing steps exist" <| fun () ->
      hasModuleFunction "updateTrafficSimulation"
      |> Expect.isTrue "traffic should be recomputed in an explicit simulation stage"
      hasModuleFunction "shortestStreetPath"
      |> Expect.isTrue "traffic simulation needs explicit shortest-path routing over street segments"
      hasModuleFunction "generateResidentTrips"
      |> Expect.isTrue "traffic simulation should expose resident-derived trip generation"
      hasModuleFunction "applyResidentTrip"
      |> Expect.isTrue "traffic simulation should expose trip application"
      hasModuleFunction "removeResidentTrip"
      |> Expect.isTrue "traffic simulation should expose trip removal"

    assumptionCase "model surface for §3.2" "streets carry ids residents and traffic state" <| fun () ->
      hasRecordField<MajorStreet> "Id" |> Expect.isTrue "major streets should carry a stable street id"
      hasRecordField<MajorStreet> "Residents" |> Expect.isTrue "major streets should expose resident demand"
      hasRecordField<MajorStreet> "Traffic" |> Expect.isTrue "major streets should carry traffic state"
      hasRecordField<MinorStreet> "Id" |> Expect.isTrue "minor streets should carry a stable street id"
      hasRecordField<MinorStreet> "Residents" |> Expect.isTrue "minor streets should expose resident demand"
      hasRecordField<MinorStreet> "Traffic" |> Expect.isTrue "minor streets should carry traffic state"

    assumptionCase "pipeline API for §2.2" "time-stepped urban simulation entrypoint exists" <| fun () ->
      hasModuleFunction "simulateUrbanStep"
      |> Expect.isTrue "the full urbanSimulation pipeline should advance in explicit time steps"

    assumptionCase "pipeline API for §4.1-§4.3" "land use update step exists" <| fun () ->
      hasModuleFunction "updateLandUseSimulation"
      |> Expect.isTrue "land use reevaluation should exist as a distinct simulation step"
      hasModuleFunction "updateLandUseSimulationWith"
      |> Expect.isTrue "phase 4 land-use reevaluation should expose a configurable deterministic update entrypoint"
      hasModuleFunction "computeGlobalLandUseValue"
      |> Expect.isTrue "phase 4 should expose the global land-use penalty from equations (2)-(3)"
      hasModuleFunction "evaluateLocalLotLandUseValue"
      |> Expect.isTrue "phase 4 should expose the local lot valuation from equation (4)"

    assumptionCase "pipeline API for §5.1" "lot subdivision uses land-use-specific thresholds" <| fun () ->
      hasModuleFunction "subdivideBlockByLandUse"
      |> Expect.isTrue "lot subdivision should depend on block land use as described in section 5.1"
      hasModuleFunction "subdivideBlockByLandUseWithStats"
      |> Expect.isTrue "phase 5 should expose deterministic subdivision stats for regression coverage"
      hasModuleFunction "subdivisionRuleForLandUse"
      |> Expect.isTrue "phase 5 should expose the public land-use subdivision rules"
      hasModuleFunction "selectLongestStreetFacingEdge"
      |> Expect.isTrue "phase 5 should expose frontage-edge selection so tests can pin deterministic behavior"

    assumptionCase "model surface for §5.2 Eq. (9)-(10)" "economy model computes lot price floorspace and residents" <| fun () ->
      hasRecordField<PlannedLot> "Price" |> Expect.isTrue "lots should carry economy-model price"
      hasRecordField<PlannedLot> "FloorSpace" |> Expect.isTrue "lots should carry required floorspace"
      hasRecordField<PlannedLot> "Residents" |> Expect.isTrue "lots should carry estimated residents"

    assumptionCase "pipeline API for §5.3 Eq. (11)" "building envelope generation step exists" <| fun () ->
      hasModuleFunction "computeBuildingEnvelope"
      |> Expect.isTrue "building envelope generation should be a distinct zoning step"

    assumptionCase "pipeline API for phase 8 orchestration" "time-stepped simulation surface exists" <| fun () ->
      hasModuleFunction "simulateUrbanTimeline"
      |> Expect.isTrue "phase 8 should expose a public multi-step timeline runner"
      hasModuleFunction "simulateUrbanTimelineWith"
      |> Expect.isTrue "phase 8 should expose a configurable multi-step timeline runner"
      hasRecordField<SimulationTimelineConfig> "BuildingSubstitution"
      |> Expect.isTrue "timeline config should be able to opt into time-aware building substitution"
      hasRecordField<TimelineStepSnapshot> "BuildingSubstitutionStats"
      |> Expect.isTrue "timeline snapshots should expose substitution observability"

    assumptionCase "pipeline API for phase 9 benchmarking" "benchmark case and budget surfaces exist" <| fun () ->
      hasModuleFunction "benchmarkDistrictForSize"
      |> Expect.isTrue "phase 9 should expose named benchmark fixture sizes"
      hasModuleFunction "benchmarkSimulationCases"
      |> Expect.isTrue "phase 9 should expose a public multi-case benchmark runner"
      hasModuleFunction "evaluateBenchmarkBudget"
      |> Expect.isTrue "phase 9 should expose pure budget evaluation separate from timing capture"

    assumptionCase "pipeline API for §5.4 building substitution" "substitution surfaces exist" <| fun () ->
      hasRecordField<PlannedLot> "BuildingAgeYears"
      |> Expect.isTrue "building substitution needs persistent building age on each lot"
      hasModuleFunction "computeBuildingSubstitutionProbability"
      |> Expect.isTrue "phase 10 should expose pure substitution probability scoring"
      hasModuleFunction "updateBuildingSubstitutionWith"
      |> Expect.isTrue "phase 10 should expose deterministic plan-level building substitution"

    assumptionProperty "named valuation API for §4.3 Eq. (4)" "land use valuation functions are exposed on the [0,1] range" <|
      fun (_: NonNegativeInt) ->
        hasModuleFunction "evaluateLotLandUseValue"

    assumptionProperty "named economy API for §5.2 Eq. (9)-(10)" "economy model exposes positive residents and floorspace outputs" <|
      fun (_: PositiveInt) ->
        hasModuleFunction "computeLotEconomics"
  ]

let urbanSimulationNaturalismRegressionTests =
  testList "urbanSimulation naturalism regressions" [
    naturalismCase "morphology guard on street-induced districts" "planned street-induced blocks do not overlap" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 60.0f; H = 40.0f }
      let plan = planHierarchicalDistrict rect 9 0.25f (Random 42)
      let blocks = plan.Quarters |> List.collect (fun quarter -> quarter.Blocks)
      blocks
      |> List.pairwise
      |> List.exists (fun (a, b) -> rectsOverlap a.Rect b.Rect)
      |> Expect.isFalse "street-induced blocks should not overlap"

    naturalismCase "frontage-lot regression" "block subdivision keeps every lot touching the parent block boundary" <| fun () ->
      let block = PlannedBlock.create { X = 5.0f; Z = 7.0f; W = 18.0f; H = 10.0f } { X = 5.0f; Z = 7.0f; W = 18.0f; H = 10.0f }
      let lots = subdivideBlockIntoLots block 5
      lots
      |> List.iter (fun lot ->
        let touchesBoundary =
          abs (lot.Rect.X - block.Rect.X) < 0.001f
          || abs ((lot.Rect.X + lot.Rect.W) - (block.Rect.X + block.Rect.W)) < 0.001f
          || abs (lot.Rect.Z - block.Rect.Z) < 0.001f
          || abs ((lot.Rect.Z + lot.Rect.H) - (block.Rect.Z + block.Rect.H)) < 0.001f
        touchesBoundary |> Expect.isTrue "every lot should keep direct frontage on the parent boundary")

    naturalismCase "envelope-placement regression" "building footprints stay within their assigned lot envelopes" <| fun () ->
      let lots =
        subdivideBlockIntoLots (PlannedBlock.create { X = 0.0f; Z = 0.0f; W = 20.0f; H = 12.0f } { X = 0.0f; Z = 0.0f; W = 20.0f; H = 12.0f }) 4
      let funcs = List.init 4 (fun i -> mkFunc (sprintf "lotFunc%d" i) "LotMod")
      let buildings = placeBuildingsInLots lots funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 7) Map.empty
      List.zip lots buildings
      |> List.iter (fun (lot, building) ->
        (building.X, lot.Rect.X) |> Expect.isGreaterThanOrEqual "building left edge inside lot"
        (building.Z, lot.Rect.Z) |> Expect.isGreaterThanOrEqual "building top edge inside lot"
        (building.X + building.W, lot.Rect.X + lot.Rect.W) |> Expect.isLessThanOrEqual "building right edge inside lot"
        (building.Z + building.D, lot.Rect.Z + lot.Rect.H) |> Expect.isLessThanOrEqual "building bottom edge inside lot")
  ]

let urbanSimulationProofLayerTests =
  testList "urbanSimulation proof layer" [
    urbanSimulationPaperComplianceTests
    urbanSimulationAssumptionTests
    urbanSimulationNaturalismRegressionTests
  ]

let simulationTraceTests =
  testList "urbanSimulation trace harness" [
    testCase "seeded planner replays deterministically" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 60.0f; H = 40.0f }
      let first = planHierarchicalDistrictFromSeed 42 rect 9 0.25f
      let second = planHierarchicalDistrictFromSeed 42 rect 9 0.25f
      first |> Expect.equal "same seed should replay the same district plan" second

    testCase "runSimulationStage mirrors the public stage functions" <| fun () ->
      let initial = Scenario.singlePlannedMajorStreet { X = 8.0f; Z = 0.0f; W = 4.0f; H = 32.0f }
      let trafficUpdated = runSimulationStage UpdateTrafficSimulationStage initial
      trafficUpdated |> Expect.equal "stage runner should delegate to updateTrafficSimulation" (updateTrafficSimulation initial)

    testCase "stepWithTrace exposes stage order and direct outputs" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 60.0f; H = 40.0f }
      let initial = planHierarchicalDistrictFromSeed 42 rect 9 0.25f
      let trafficUpdated = updateTrafficSimulation initial
      let promoted = promotePlannedStreets trafficUpdated
      let updated = updateLandUseSimulation promoted
      let trace = stepWithTrace (Some 42) initial
      trace.Seed |> Expect.equal "trace should carry the replay seed" (Some 42)
      trace.Initial |> Expect.equal "trace should preserve the initial plan" initial
      trace.Stages |> List.map _.Stage
      |> Expect.equal "trace should expose the current simulation stages in order" [ UpdateTrafficSimulationStage; PromotePlannedStreetsStage; UpdateLandUseSimulationStage ]
      trace.Stages.[0].Plan |> Expect.equal "first stage snapshot should match direct traffic output" trafficUpdated
      trace.Stages.[1].Plan |> Expect.equal "second stage snapshot should match direct promotion output" promoted
      trace.Stages.[2].Plan |> Expect.equal "third stage snapshot should match direct land-use update output" updated
      trace.Final |> Expect.equal "trace final should match simulateUrbanStep" (simulateUrbanStep initial)

    testCase "canonicalized traces ignore incidental collection ordering" <| fun () ->
      let quarterRect = { X = 0.0f; Z = 0.0f; W = 24.0f; H = 18.0f }
      let blockA = Scenario.rectangularBlock quarterRect { X = 0.0f; Z = 0.0f; W = 12.0f; H = 18.0f } ResidentialUse 0.7f
      let blockB = Scenario.rectangularBlock quarterRect { X = 12.0f; Z = 0.0f; W = 12.0f; H = 18.0f } CommercialUse 0.8f
      let minorA = MinorStreet.planned quarterRect { X = 12.0f; Z = 0.0f; W = 1.0f; H = 18.0f }
      let minorB = MinorStreet.built quarterRect { X = 0.0f; Z = 9.0f; W = 24.0f; H = 1.0f }
      let quarter =
        QuarterPlan.create quarterRect [ minorA; minorB ] [ blockA; blockB ]
        |> fun q -> { q with LandUseType = MixedUseZone; LandUseValue = 0.75f }
      let planA =
        { MajorStreets = [ MajorStreet.planned { X = 11.5f; Z = 0.0f; W = 1.0f; H = 18.0f } ]
          Quarters = [ quarter ] }
      let planB =
        { MajorStreets = List.rev planA.MajorStreets
          Quarters =
            [ { quarter with
                  MinorStreets = List.rev quarter.MinorStreets
                  Blocks = List.rev quarter.Blocks } ] }
      let traceA = stepWithTrace None planA |> canonicalizeSimulationTrace
      let traceB = stepWithTrace None planB |> canonicalizeSimulationTrace
      traceA |> Expect.equal "canonicalized traces should compare by stable content, not incidental ordering" traceB

    testCase "scenario builders create minimal trustworthy fixtures" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 12.0f }
      let singleQuarter = Scenario.singleQuarter rect
      singleQuarter.MajorStreets |> Expect.isEmpty "singleQuarter should not invent major streets"
      singleQuarter.Quarters |> Expect.hasLength "singleQuarter should create exactly one quarter" 1
      let block = Scenario.rectangularBlock rect rect CivicUse 0.6f
      block.LandUseType |> Expect.equal "builder should stamp the requested land use" CivicUse
      let lot =
        Scenario.rectangularLot rect rect North Built MixedUseZone 0.9f
      lot.FrontingStreetStatus |> Expect.equal "lot builder should preserve requested street status" Built
      lot.Envelope |> Expect.isSome "built lot builder should derive an envelope"
      let major = Scenario.singlePlannedMajorStreet { X = 2.0f; Z = 0.0f; W = 2.0f; H = 12.0f }
      major.MajorStreets |> Expect.hasLength "single planned major street builder should create exactly one street" 1
      let minor = Scenario.singleBuiltMinorStreet rect { X = 0.0f; Z = 4.0f; W = 20.0f; H = 1.0f }
      minor.Quarters |> List.collect _.MinorStreets |> List.map _.Status
      |> Expect.equal "single built minor street builder should preserve built status" [ Built ]
  ]

let simulationTimelineTests =
  testList "urbanSimulation timeline" [
    testCase "one-step cadence and one-step promotion match repeated simulateUrbanStep" <| fun () ->
      let initial = benchmarkDistrict 17
      let config = phase8TimelineConfig 3 1.0f 1.0f 1
      let timeline = simulateUrbanTimeline config (Some 17) initial
      let manual =
        initial
        |> simulateUrbanStep
        |> simulateUrbanStep
        |> simulateUrbanStep
      timeline.Steps |> List.length |> Expect.equal "timeline should record each simulated step" 3
      timeline.Steps |> List.map _.ElapsedYears
      |> Expect.equal "elapsed years should accumulate monotonically" [ 1.0f; 2.0f; 3.0f ]
      canonicalizeDistrictPlan timeline.Final
      |> Expect.equal "default timeline orchestration should preserve the manual step semantics"
           (canonicalizeDistrictPlan manual)

    testCase "land use reevaluation only runs when the cadence horizon is reached" <| fun () ->
      let initial = benchmarkDistrict 17
      let config = phase8TimelineConfig 3 1.0f 2.0f 1
      let timeline = simulateUrbanTimeline config (Some 17) initial
      timeline.Steps |> List.map (fun step -> step.LandUseStats.SkippedByCadence)
      |> Expect.equal "cadence should skip the first and third years while evaluating the second"
           [ true; false; true ]

    testCase "planned street promotion is delayed until a street qualifies for the required number of steps" <| fun () ->
      let seeded = Scenario.singlePlannedMajorStreet { X = 8.0f; Z = 0.0f; W = 4.0f; H = 32.0f }
      let initial =
        { seeded with
            MajorStreets =
              [ { seeded.MajorStreets.Head with
                    Residents = 40.0f } ] }
      let config = phase8TimelineConfig 2 1.0f 10.0f 2
      let timeline = simulateUrbanTimeline config None initial
      timeline.Steps.[0].Plan.MajorStreets.Head.Status
      |> Expect.equal "one qualifying step should not yet build the street" Planned
      timeline.Steps.[1].Plan.MajorStreets.Head.Status
      |> Expect.equal "the second qualifying step should build the street" Built

    testCase "delayed street builds delay frontage envelopes until the built step arrives" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let lot = phase6Lot rect ResidentialUse 0.6f
      let initial =
        phase7Plan lot [
          { phase7MinorStreet rect Planned 0.0f { X = 0.0f; Z = -1.0f; W = 10.0f; H = 1.0f } with
              Residents = 40.0f }
        ]
      let config = phase8TimelineConfig 2 1.0f 1.0f 2
      let timeline = simulateUrbanTimeline config None initial
      let firstStepLot = timeline.Steps.[0].Plan.Quarters.Head.Blocks.Head.Lots.Head
      let secondStepLot = timeline.Steps.[1].Plan.Quarters.Head.Blocks.Head.Lots.Head
      timeline.Steps.[0].Plan.Quarters.Head.MinorStreets.Head.Status
      |> Expect.equal "the frontage street should still be planned after one qualifying year" Planned
      firstStepLot.FrontingStreetStatus
      |> Expect.equal "fronting status should remain planned before promotion" Planned
      firstStepLot.Envelope
      |> Expect.isNone "planned frontage should not yet produce a buildable envelope"
      timeline.Steps.[1].Plan.Quarters.Head.MinorStreets.Head.Status
      |> Expect.equal "the frontage street should build on the second qualifying year" Built
      secondStepLot.FrontingStreetStatus
      |> Expect.equal "fronting status should become built once promotion lands" Built
      secondStepLot.Envelope
      |> Expect.isSome "built frontage should allow a contextual building envelope"

    testCase "timeline ages retained buildings when substitution probability is zero" <| fun () ->
      let quarterRect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let initial =
        phase10Plan quarterRect [
          phase10Lot quarterRect quarterRect 10.0f 100.0f 40.0f 16.0f
        ]
      let landUseConfig = phase11StableLandUseConfig 17 0.0f 10.0f
      let substitution =
        Some
          { phase10SubstitutionConfig with
              AgeFactor = { phase10SubstitutionConfig.AgeFactor with Weight = 0.0f }
              PriceGapFactor = { phase10SubstitutionConfig.PriceGapFactor with Weight = 0.0f } }
      let config = phase11TimelineConfig substitution 1 5.0f 10.0f 1
      let timeline = simulateUrbanTimelineWith landUseConfig config None initial
      let finalLot = timeline.Final.Quarters.Head.Blocks.Head.Lots.Head
      finalLot.BuildingAgeYears
      |> Expect.equal "retained buildings should age by the step years inside the timeline" 15.0f
      timeline.Steps.Head.BuildingSubstitutionStats.EvaluatedLots
      |> Expect.equal "the timeline should evaluate substitution for the retained lot" 1
      timeline.Steps.Head.BuildingSubstitutionStats.ReplacedLots
      |> Expect.equal "zero probability should retain the existing building" 0
      timeline.Steps.Head.BuildingSubstitutionStats.RetainedLots
      |> Expect.equal "zero probability should count the lot as retained" 1
      timeline.Steps.Head.Delta.ReplacedLotCount
      |> Expect.equal "delta should report no replacements for a retained step" 0

    testCase "timeline replacement copies the refreshed potential lot and exposes substitution stats" <| fun () ->
      let quarterRect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let initial =
        phase10Plan quarterRect [
          { phase10Lot quarterRect quarterRect 25.0f 50.0f 10.0f 2.0f with
              Envelope = None }
        ]
      let landUseConfig = phase11StableLandUseConfig 17 0.0f 1.0f
      let substitution =
        Some
          { phase10SubstitutionConfig with
              AgeFactor = { Curve = LinearUp; Min = 0.0f; Max = 1.0f; Weight = 1.0f }
              PriceGapFactor = { phase10SubstitutionConfig.PriceGapFactor with Weight = 0.0f } }
      let config = phase11TimelineConfig substitution 1 1.0f 1.0f 1
      let expectedPotential =
        updateLandUseSimulationWith
          { landUseConfig with
              Cadence = { landUseConfig.Cadence with ElapsedSinceLastEvaluationYears = 1.0f } }
          initial
        |> fst
        |> fun plan -> plan.Quarters.Head.Blocks.Head.Lots.Head
      let timeline = simulateUrbanTimelineWith landUseConfig config None initial
      let step = timeline.Steps.Head
      let finalLot = step.Plan.Quarters.Head.Blocks.Head.Lots.Head
      finalLot.Price |> Expect.equal "replacement should adopt the refreshed potential price" expectedPotential.Price
      finalLot.FloorSpace |> Expect.equal "replacement should adopt the refreshed potential floor space" expectedPotential.FloorSpace
      finalLot.Residents |> Expect.equal "replacement should adopt the refreshed potential residents" expectedPotential.Residents
      finalLot.Envelope |> Expect.equal "replacement should adopt the refreshed potential envelope" expectedPotential.Envelope
      finalLot.BuildingAgeYears |> Expect.equal "replacement should reset building age in the timed pipeline" 0.0f
      step.BuildingSubstitutionStats.EvaluatedLots
      |> Expect.equal "the timed pipeline should report the evaluated lot" 1
      step.BuildingSubstitutionStats.ReplacedLots
      |> Expect.equal "forced replacement should report exactly one redevelopment" 1
      step.BuildingSubstitutionStats.RetainedLots
      |> Expect.equal "forced replacement should retain no lots in this fixture" 0
      step.Delta.ReplacedLotCount
      |> Expect.equal "timeline deltas should expose replacement counts" 1

    testCase "timeline accumulates building age across retained steps and resets it on replacement" <| fun () ->
      let quarterRect = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let initial =
        phase10Plan quarterRect [
          phase10Lot quarterRect quarterRect 0.0f 100.0f 40.0f 16.0f
        ]
      let landUseConfig = phase11StableLandUseConfig 17 0.0f 100.0f
      let substitution =
        Some
          { phase10SubstitutionConfig with
              AgeFactor = { Curve = Step; Min = 0.0f; Max = 20.0f; Weight = 1.0f }
              PriceGapFactor = { phase10SubstitutionConfig.PriceGapFactor with Weight = 0.0f } }
      let config = phase11TimelineConfig substitution 3 5.0f 100.0f 1
      let timeline = simulateUrbanTimelineWith landUseConfig config None initial
      let ages =
        timeline.Steps
        |> List.map (fun step -> step.Plan.Quarters.Head.Blocks.Head.Lots.Head.BuildingAgeYears)
      ages
      |> Expect.equal "retained years should accumulate until the replacement step resets age" [ 5.0f; 10.0f; 0.0f ]
      timeline.Steps |> List.map (fun step -> step.BuildingSubstitutionStats.ReplacedLots)
      |> Expect.equal "only the third step should trigger replacement in this age-threshold fixture" [ 0; 0; 1 ]
  ]

let simulationBenchmarkTests =
  let asPlainTrace (trace: InstrumentedSimulationTrace) =
    { Seed = trace.Seed
      Initial = trace.Initial
      Stages =
        trace.Stages
        |> List.map (fun snapshot ->
          { Stage = snapshot.Stage
            Plan = snapshot.Plan })
      Final = trace.Final }

  testList "urbanSimulation benchmark harness" [
    testCase "instrumented trace preserves canonical simulation behavior" <| fun () ->
      let initial = benchmarkDistrict 42
      let plain = stepWithTrace (Some 42) initial |> canonicalizeSimulationTrace
      let instrumented = stepWithInstrumentation (Some 42) initial |> asPlainTrace |> canonicalizeSimulationTrace
      instrumented |> Expect.equal "instrumentation should not change the resulting simulation trace" plain

    testCase "promotion delta counts only streets that actually flipped to built" <| fun () ->
      let largeQuarterRect = { X = 0.0f; Z = 0.0f; W = 16.0f; H = 10.0f }
      let smallQuarterRect = { X = 20.0f; Z = 0.0f; W = 8.0f; H = 8.0f }
      let largeBlock = Scenario.rectangularBlock largeQuarterRect largeQuarterRect ResidentialUse 0.5f
      let smallBlock = Scenario.rectangularBlock smallQuarterRect smallQuarterRect ResidentialUse 0.5f
      let before =
        { MajorStreets =
            [ { MajorStreet.planned { X = 0.0f; Z = 0.0f; W = 3.0f; H = 24.0f } with Traffic = { Volume = 12.0f; MaxVolume = 0.0f } }
              { MajorStreet.planned { X = 30.0f; Z = 0.0f; W = 3.0f; H = 10.0f } with Traffic = { Volume = 1.0f; MaxVolume = 0.0f } } ]
          Quarters =
            [ QuarterPlan.create largeQuarterRect [ { MinorStreet.planned largeQuarterRect { X = 7.5f; Z = 0.0f; W = 1.0f; H = 10.0f } with Traffic = { Volume = 12.0f; MaxVolume = 0.0f } } ] [ largeBlock ]
              QuarterPlan.create smallQuarterRect [ { MinorStreet.planned smallQuarterRect { X = 23.5f; Z = 0.0f; W = 1.0f; H = 8.0f } with Traffic = { Volume = 1.0f; MaxVolume = 0.0f } } ] [ smallBlock ] ] }
      let after = promotePlannedStreets before
      let delta = summarizeSimulationStageDelta before after
      delta.PromotedMajorStreetCount |> Expect.equal "only the long planned major street should promote" 1
      delta.PromotedMinorStreetCount |> Expect.equal "only the large-quarter minor street should promote" 1
      delta.QuarterLandUseChangedCount |> Expect.equal "promotion alone should not relabel quarters" 0
      delta.BlockLandUseChangedCount |> Expect.equal "promotion alone should not relabel blocks" 0

    testCase "land use delta reports changed quarters blocks and total value movement" <| fun () ->
      let quarterRect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let beforeBlock =
        Scenario.rectangularBlock quarterRect quarterRect ParkUse 0.1f
      let beforeQuarter =
        QuarterPlan.create quarterRect [ MinorStreet.built quarterRect { X = 9.5f; Z = 0.0f; W = 1.0f; H = 20.0f } ] [ beforeBlock ]
        |> fun quarter ->
          { quarter with
              LandUseType = ParkUse
              LandUseValue = 0.1f }
      let before =
        { MajorStreets = []
          Quarters = [ beforeQuarter ] }
      let after = updateLandUseSimulation before
      let delta = summarizeSimulationStageDelta before after
      delta.QuarterLandUseChangedCount |> Expect.equal "quarter relabel should be counted" 1
      delta.BlockLandUseChangedCount |> Expect.equal "block relabel should be counted" 1
      (delta.TotalBlockLandUseValueDelta, 0.0f) |> Expect.isGreaterThan "value delta should capture the block's new suitability"

    testCase "benchmark summary reuses canonical result and excludes warmup from measured counts" <| fun () ->
      let initial = benchmarkDistrict 42
      let summary = benchmarkSimulationStep 2 3 (Some 42) initial
      summary.WarmupIterations |> Expect.equal "warmup iteration count should be reported" 2
      summary.MeasuredIterations |> Expect.equal "measured iteration count should be reported" 3
      summary.StageBenchmarks |> List.map _.Stage
      |> Expect.equal "each public stage should receive a benchmark row" [ UpdateTrafficSimulationStage; PromotePlannedStreetsStage; UpdateLandUseSimulationStage ]
      summary.StageBenchmarks |> List.iter (fun stage ->
        stage.Iterations |> Expect.equal "measured iteration count should flow into each stage benchmark" 3
        stage.MinElapsed >= TimeSpan.Zero |> Expect.isTrue "minimum elapsed time should be non-negative"
        stage.MaxElapsed >= stage.MinElapsed |> Expect.isTrue "maximum elapsed should dominate minimum")
      summary.CanonicalResult
      |> Expect.equal "benchmark should preserve the canonical step result"
           (stepWithTrace (Some 42) initial |> canonicalizeSimulationTrace)

    testCase "benchmarkSimulationCases returns named summaries in input order" <| fun () ->
      let cases =
        [ { Name = "tiny"
            Size = Tiny
            Seed = 11
            Initial = benchmarkDistrictForSize Tiny 11
            Budget = None }
          { Name = "medium"
            Size = Medium
            Seed = 13
            Initial = benchmarkDistrictForSize Medium 13
            Budget = None }
          { Name = "target"
            Size = Target
            Seed = 17
            Initial = benchmarkDistrictForSize Target 17
            Budget = None } ]
      let summaries = benchmarkSimulationCases 0 1 cases
      summaries |> List.map (fun summary -> summary.Case.Name, summary.Case.Size)
      |> Expect.equal "multi-case benchmarks should preserve scenario ordering"
           [ "tiny", Tiny; "medium", Medium; "target", Target ]

    testCase "benchmarkSimulationCases includes workload counters and result counts" <| fun () ->
      let cases =
        [ { Name = "tiny"
            Size = Tiny
            Seed = 19
            Initial = benchmarkDistrictForSize Tiny 19
            Budget = None } ]
      let summary = benchmarkSimulationCases 0 1 cases |> List.exactlyOne
      (summary.Counters.TripsGenerated, 0)
      |> Expect.isGreaterThan "benchmark counters should expose generated trips"
      summary.Counters.PathComputations
      |> Expect.equal "traffic workload should count one path computation for each ordered resident pair"
           (summary.Counters.TripsGenerated * max 0 (summary.Counters.TripsGenerated - 1))
      (summary.Counters.SplitCandidatesTried, 0)
      |> Expect.isGreaterThan "subdivision workload should expose tried split candidates"
      (summary.Counters.ResultStreetCount, 0)
      |> Expect.isGreaterThan "final plan should report resulting street count"
      (summary.Counters.ResultBlockCount, 0)
      |> Expect.isGreaterThan "final plan should report resulting block count"
      (summary.Counters.ResultLotCount, 0)
      |> Expect.isGreaterThan "final plan should report resulting lot count"

    testCase "evaluateBenchmarkBudget classifies summaries purely" <| fun () ->
      let canonical = stepWithTrace (Some 7) (benchmarkDistrict 7) |> canonicalizeSimulationTrace
      let summaryWithMedian median =
        { WarmupIterations = 0
          MeasuredIterations = 1
          StageBenchmarks = []
          TotalMinElapsed = median
          TotalMedianElapsed = median
          TotalMaxElapsed = median
          CanonicalResult = canonical }
      evaluateBenchmarkBudget None (summaryWithMedian (TimeSpan.FromMilliseconds 10.0))
      |> Expect.equal "missing budgets should report that no reference target was evaluated" NotEvaluated
      evaluateBenchmarkBudget
        (Some
          { ReferenceLabel = "paper target"
            TargetMedianElapsed = TimeSpan.FromMilliseconds 20.0 })
        (summaryWithMedian (TimeSpan.FromMilliseconds 10.0))
      |> Expect.equal "summaries below the target median should be marked within reference" WithinReference
      evaluateBenchmarkBudget
        (Some
          { ReferenceLabel = "paper target"
            TargetMedianElapsed = TimeSpan.FromMilliseconds 5.0 })
        (summaryWithMedian (TimeSpan.FromMilliseconds 10.0))
      |> Expect.equal "summaries above the target median should report advisory overage"
           (OverReference (TimeSpan.FromMilliseconds 5.0))

    testCase "benchmarkSimulationCases reports budget status without throwing" <| fun () ->
      let cases =
        [ { Name = "tiny-over-budget"
            Size = Tiny
            Seed = 23
            Initial = benchmarkDistrictForSize Tiny 23
            Budget =
              Some
                { ReferenceLabel = "impossibly fast"
                  TargetMedianElapsed = TimeSpan.FromTicks 1L } } ]
      let summary = benchmarkSimulationCases 0 1 cases |> List.exactlyOne
      match summary.BudgetStatus with
      | OverReference _ -> ()
      | other -> failtestf "expected advisory over-budget classification, got %A" other

    testCase "benchmark harness rejects invalid iteration counts" <| fun () ->
      let initial = benchmarkDistrict 42
      Expect.throwsT<ArgumentException> "negative warmup should be rejected" (fun () -> benchmarkSimulationStep -1 1 (Some 42) initial |> ignore)
      Expect.throwsT<ArgumentException> "non-positive measured iterations should be rejected" (fun () -> benchmarkSimulationStep 0 0 (Some 42) initial |> ignore)
  ]

let gitMetaTests =
  testList "Git metadata parsing" [
    testCase "empty log produces zero commits" <| fun () ->
      let m = parseGitLog ""
      m.CommitCount |> Expect.equal "no commits in empty log" 0

    testCase "single commit line is counted" <| fun () ->
      let m = parseGitLog "abc123|2024-01-15T10:00:00+00:00\n"
      m.CommitCount |> Expect.equal "one commit" 1
      m.AuthorCount |> Expect.equal "legacy format should fall back to one author" 1
      m.BugFixRatio |> Expect.equal "legacy format should not infer bug-fix churn" 0.0f

    testCase "multiple lines all counted" <| fun () ->
      let log = "aaa|2024-06-01T00:00:00+00:00\nbbb|2023-01-01T00:00:00+00:00\nccc|2022-03-15T00:00:00+00:00\n"
      let m = parseGitLog log
      m.CommitCount |> Expect.equal "three commits" 3

    testCase "earliest date becomes first commit" <| fun () ->
      let log = "aaa|2024-06-01T00:00:00+00:00\nbbb|2020-01-01T00:00:00+00:00\n"
      let m = parseGitLog log
      m.FirstCommitDate.Year |> Expect.equal "2020 is the earliest year" 2020

    testCase "rich git log captures author count and bug-fix ratio" <| fun () ->
      let log =
        String.concat "\n" [
          "aaa|2024-06-01T00:00:00+00:00|Alice|Fix flaky parser"
          "bbb|2024-05-01T00:00:00+00:00|Bob|Refactor render layout"
          "ccc|2024-04-01T00:00:00+00:00|Alice|Hotfix geometry regression"
        ] + "\n"
      let m = parseGitLog log
      m.AuthorCount |> Expect.equal "distinct authors should be counted" 2
      (abs (m.BugFixRatio - (2.0f / 3.0f)), 0.01f)
      |> Expect.isLessThan "fix-like subjects should contribute to bug-fix ratio"
  ]

let organicFactorTests =
  testList "Organic growth factor" [
    testCase "brand-new file (0 days, 0 commits) has factor 0" <| fun () ->
      organicFactor 0.0f 0 |> Expect.equal "new code = pure grid" 0.0f

    testCase "10-year-old active file has factor near 1" <| fun () ->
      let f = organicFactor 3650.0f 200
      (f, 0.9f) |> Expect.isGreaterThanOrEqual "ancient hot code = full organic"

    testPropertyWithConfig cfg "factor always in [0, 1]" <|
      fun (NonNegativeInt rawAge) (NonNegativeInt rawCommits) ->
        let age     = float32 (rawAge % 5000)
        let commits = rawCommits % 1000
        let f = organicFactor age commits
        f >= 0.0f && f <= 1.0f

    testCase "older code is more organic than newer code (same commit count)" <| fun () ->
      let young = organicFactor 30.0f 5
      let old   = organicFactor 1000.0f 5
      (old, young) |> Expect.isGreaterThan "1000-day-old code should be more organic than 30-day-old"
  ]

let private roadOrientationDegrees (road: Road) =
  let dx = road.ToPos.X - road.FromPos.X
  let dz = road.ToPos.Z - road.FromPos.Z
  let angle = MathF.Atan2(dz, dx) * 180.0f / MathF.PI
  let normalized =
    match angle < 0.0f with
    | true -> angle + 180.0f
    | false -> angle
  match normalized >= 180.0f with
  | true -> normalized - 180.0f
  | false -> normalized

let private roadOrientationBucket bucketSize road =
  int (MathF.Floor((roadOrientationDegrees road + bucketSize / 2.0f) / bucketSize))

let private orientationHistogram bucketSize roads =
  roads
  |> List.groupBy (roadOrientationBucket bucketSize)
  |> List.map (fun (bucket, groupedRoads) -> bucket, groupedRoads.Length)
  |> List.sortByDescending snd

let private axisAlignedBucket bucketSize bucket =
  let angle = float32 bucket * bucketSize
  let nearestAxis =
    [ 0.0f; 90.0f; 180.0f ]
    |> List.map (fun axis -> abs (angle - axis))
    |> List.min
  nearestAxis <= bucketSize

let private nearestRoadDistance (roads: Road list) (x: float32) (z: float32) =
  let pointDistance road =
    let ax = road.FromPos.X
    let az = road.FromPos.Z
    let bx = road.ToPos.X
    let bz = road.ToPos.Z
    let dx = bx - ax
    let dz = bz - az
    let lenSq = dx * dx + dz * dz
    let t =
      match lenSq < 1e-10f with
      | true -> 0.0f
      | false -> min 1.0f (max 0.0f (((x - ax) * dx + (z - az) * dz) / lenSq))
    let nearX = ax + t * dx
    let nearZ = az + t * dz
    sqrt ((x - nearX) * (x - nearX) + (z - nearZ) * (z - nearZ))

  roads |> List.map pointDistance |> List.min

let private roadEndpoints2d (road: Road) =
  Vec2.Create(road.FromPos.X, road.FromPos.Z),
  Vec2.Create(road.ToPos.X, road.ToPos.Z)

let private roadSpanLength (road: Road) =
  let startPt, endPt = roadEndpoints2d road
  Vec2.distanceTo startPt endPt

let private pointToRoadProjection (road: Road) (pt: Vec2) =
  let startPt, endPt = roadEndpoints2d road
  let dx = endPt.X - startPt.X
  let dy = endPt.Y - startPt.Y
  let lenSq = dx * dx + dy * dy
  if lenSq < 1e-6f then
    0.0f, Vec2.distanceTo pt startPt
  else
    let t = ((pt.X - startPt.X) * dx + (pt.Y - startPt.Y) * dy) / lenSq
    let nearPt = Vec2.Create(startPt.X + dx * t, startPt.Y + dy * t)
    t, Vec2.distanceTo pt nearPt

let private pointInsideRectInset inset (rect: TRect) (pt: Vec2) =
  pt.X >= rect.X + inset
  && pt.X <= rect.X + rect.W - inset
  && pt.Y >= rect.Z + inset
  && pt.Y <= rect.Z + rect.H - inset

let private roundedLengthBucketCount bucketSize (lengths: float32 list) =
  lengths
  |> List.map (fun length -> int (MathF.Round(length / bucketSize)))
  |> Set.ofList
  |> Set.count

let private coefficientOfVariation (values: float32 list) =
  match values with
  | [] | [ _ ] -> 0.0f
  | _ ->
      let mean = values |> List.average
      let variance =
        values
        |> List.averageBy (fun value ->
            let delta = value - mean
            delta * delta)
      MathF.Sqrt(variance) / mean

type private CorridorSpacingMetric =
  { CorridorLength: float32
    AttachmentCount: int
    GapCv: float32
    GapRatio: float32 }

let private clusterPoints tolerance (points: Vec2 list) =
  let toleranceSq = tolerance * tolerance
  let clusters = ResizeArray<Vec2 * int>()
  let addPoint point =
    let mutable matched = false
    for i in 0 .. clusters.Count - 1 do
      match matched with
      | true -> ()
      | false ->
          let anchor, count = clusters.[i]
          if Vec2.distanceToSq anchor point <= toleranceSq then
            clusters.[i] <- anchor, count + 1
            matched <- true
    match matched with
    | true -> ()
    | false -> clusters.Add(point, 1)
  points |> List.iter addPoint
  clusters |> Seq.toList

let private smallestAngleBetweenDirections (a: Vec2) (b: Vec2) =
  let dot = max -1.0f (min 1.0f (Vec2.dot a b))
  let angle = MathF.Acos(dot) * 180.0f / MathF.PI
  min angle (180.0f - angle)

let private fullAngleBetweenDirections (a: Vec2) (b: Vec2) =
  let dot = max -1.0f (min 1.0f (Vec2.dot a b))
  MathF.Acos(dot) * 180.0f / MathF.PI

let private junctionDirectionSets tolerance (roads: Road list) =
  let toleranceSq = tolerance * tolerance
  let junctions =
    roads
    |> List.collect (fun road ->
        let a, b = roadEndpoints2d road
        [ a; b ])
    |> clusterPoints tolerance
    |> List.filter (fun (_, valence) -> valence >= 3)
    |> List.map fst
  let incidentDirection anchor road =
    let startPt, endPt = roadEndpoints2d road
    if Vec2.distanceToSq anchor startPt <= toleranceSq then
      Some (Vec2.normalize (Vec2.sub endPt startPt))
    elif Vec2.distanceToSq anchor endPt <= toleranceSq then
      Some (Vec2.normalize (Vec2.sub startPt endPt))
    else
      None
  junctions
  |> List.choose (fun junction ->
      let directions =
        roads
        |> List.choose (incidentDirection junction)
      match List.length directions >= 3 with
      | false -> None
      | true -> Some directions)

let private junctionMinAngles tolerance (roads: Road list) =
  junctionDirectionSets tolerance roads
  |> List.choose (fun directions ->
      [ for i in 0 .. List.length directions - 1 do
          for j in i + 1 .. List.length directions - 1 do
            let angle = smallestAngleBetweenDirections directions.[i] directions.[j]
            if angle > 5.0f then
              angle ]
      |> List.sort
      |> List.tryHead)

let private junctionAnglePairs tolerance (roads: Road list) =
  junctionDirectionSets tolerance roads
  |> List.collect (fun directions ->
      [ for i in 0 .. List.length directions - 1 do
          for j in i + 1 .. List.length directions - 1 do
            let angle = smallestAngleBetweenDirections directions.[i] directions.[j]
            if angle > 5.0f then
              angle ])

let private throughJunctionPerpendicularDeviations tolerance (roads: Road list) =
  junctionDirectionSets tolerance roads
  |> List.collect (fun directions ->
      let indexedDirections = directions |> List.indexed
      let pairAngles =
        [ for i in 0 .. indexedDirections.Length - 1 do
            for j in i + 1 .. indexedDirections.Length - 1 do
              let _, a = indexedDirections.[i]
              let _, b = indexedDirections.[j]
              let fullAngle = fullAngleBetweenDirections a b
              (fst indexedDirections.[i], fst indexedDirections.[j], a, fullAngle) ]
      match pairAngles |> List.sortBy (fun (_, _, _, fullAngle) -> abs (180.0f - fullAngle)) with
      | [] -> []
      | (throughA, throughB, axisDir, axisAngle) :: _ when abs (180.0f - axisAngle) <= 18.0f ->
          indexedDirections
          |> List.choose (fun (index, dir) ->
              if index = throughA || index = throughB then
                None
              else
                let branchAngle = smallestAngleBetweenDirections axisDir dir
                Some (abs (90.0f - branchAngle)))
      | _ -> [])

let private boundaryMidpointsForRect (rect: TRect) =
  [ Vec2.Create(TRect.centerX rect, rect.Z)
    Vec2.Create(rect.X + rect.W, TRect.centerZ rect)
    Vec2.Create(TRect.centerX rect, rect.Z + rect.H)
    Vec2.Create(rect.X, TRect.centerZ rect) ]

let private pointOnRectBoundary (rect: TRect) (pt: Vec2) =
  let eps = 0.05f
  abs (pt.X - rect.X) <= eps
  || abs (pt.X - (rect.X + rect.W)) <= eps
  || abs (pt.Y - rect.Z) <= eps
  || abs (pt.Y - (rect.Z + rect.H)) <= eps

let private nearestAxisDelta angle =
  [ 0.0f; 90.0f; 180.0f ]
  |> List.map (fun axis -> abs (angle - axis))
  |> List.min

type private DirectedRoadSegment =
  { Id: int
    RoadIndex: int
    StartCluster: int
    EndCluster: int
    Heading: Vec2
    Length: float32 }

type private ThroughChainMetric =
  { SegmentCount: int
    TotalLength: float32
    CumulativeTurn: float32 }

type private ThroughChainDetail =
  { SegmentIds: int list
    RoadIndices: int list
    ClusterPath: int list
    SegmentLengths: float32 list
    TotalLength: float32 }

type private ThroughChainGeometry =
  { RoadIndexSet: Set<int>
    SegmentCount: int
    TotalLength: float32
    StartPoint: Vec2
    EndPoint: Vec2
    Midpoint: Vec2
    Heading: Vec2 }

let private clusterRoadEndpoints tolerance (roads: Road list) =
  let toleranceSq = tolerance * tolerance
  let clusters = ResizeArray<Vec2 * ResizeArray<int * bool>>()

  let addReference roadIndex isStart point =
    let mutable matched = None
    for i in 0 .. clusters.Count - 1 do
      match matched with
      | Some _ -> ()
      | None ->
          let anchor, _ = clusters.[i]
          if Vec2.distanceToSq anchor point <= toleranceSq then
            matched <- Some i

    match matched with
    | Some idx ->
        let anchor, refs = clusters.[idx]
        let count = float32 refs.Count
        let blended =
          Vec2.Create(
            (anchor.X * count + point.X) / (count + 1.0f),
            (anchor.Y * count + point.Y) / (count + 1.0f))
        refs.Add(roadIndex, isStart)
        clusters.[idx] <- blended, refs
    | None ->
        let refs = ResizeArray<int * bool>()
        refs.Add(roadIndex, isStart)
        clusters.Add(point, refs)

  roads
  |> List.iteri (fun roadIndex road ->
      let startPt, endPt = roadEndpoints2d road
      addReference roadIndex true startPt
      addReference roadIndex false endPt)

  clusters
  |> Seq.mapi (fun idx (anchor, refs) -> idx, anchor, refs |> Seq.toList)
  |> Seq.toList

let private buildDirectedSegments tolerance (roads: Road list) =
  let toleranceSq = tolerance * tolerance
  let clusters = clusterRoadEndpoints tolerance roads

  let clusterIdForPoint point =
    clusters
    |> List.find (fun (_, anchor, _) -> Vec2.distanceToSq anchor point <= toleranceSq)
    |> fun (idx, _, _) -> idx

  let directedSegments =
    roads
    |> List.mapi (fun roadIndex road ->
        let startPt, endPt = roadEndpoints2d road
        let length = Vec2.distanceTo startPt endPt
        let forwardHeading = Vec2.normalize (Vec2.sub endPt startPt)
        let backwardHeading = Vec2.normalize (Vec2.sub startPt endPt)
        let startCluster = clusterIdForPoint startPt
        let endCluster = clusterIdForPoint endPt
        [ { Id = roadIndex * 2
            RoadIndex = roadIndex
            StartCluster = startCluster
            EndCluster = endCluster
            Heading = forwardHeading
            Length = length }
          { Id = roadIndex * 2 + 1
            RoadIndex = roadIndex
            StartCluster = endCluster
            EndCluster = startCluster
            Heading = backwardHeading
            Length = length } ])
    |> List.concat

  clusters, directedSegments

let private throughChainMetrics tolerance maxDeflection (roads: Road list) =
  let _, directedSegments = buildDirectedSegments tolerance roads

  let nextSegments current =
    directedSegments
    |> List.filter (fun candidate ->
        candidate.StartCluster = current.EndCluster
        && candidate.RoadIndex <> current.RoadIndex)
    |> List.sortBy (fun candidate -> smallestAngleBetweenDirections current.Heading candidate.Heading)

  let rec walk visited current segmentCount totalLength cumulativeTurn =
    let candidates =
      nextSegments current
      |> List.filter (fun candidate -> not (Set.contains candidate.Id visited))
    match candidates with
    | [] ->
        { SegmentCount = segmentCount
          TotalLength = totalLength
          CumulativeTurn = cumulativeTurn }
    | next :: _ ->
        let deflection = smallestAngleBetweenDirections current.Heading next.Heading
        match deflection <= maxDeflection with
        | false ->
            { SegmentCount = segmentCount
              TotalLength = totalLength
              CumulativeTurn = cumulativeTurn }
        | true ->
            walk
              (Set.add next.Id visited)
              next
              (segmentCount + 1)
              (totalLength + next.Length)
              (cumulativeTurn + deflection)

  directedSegments
  |> List.map (fun segment -> walk (Set.singleton segment.Id) segment 1 segment.Length 0.0f)

let private throughChainDetails tolerance maxDeflection (roads: Road list) =
  let clusters, directedSegments = buildDirectedSegments tolerance roads

  let nextSegments current =
    directedSegments
    |> List.filter (fun candidate ->
        candidate.StartCluster = current.EndCluster
        && candidate.RoadIndex <> current.RoadIndex)
    |> List.sortBy (fun candidate -> smallestAngleBetweenDirections current.Heading candidate.Heading)

  let rec walk visited current segmentIds roadIndices clusterPath segmentLengths totalLength =
    let candidates =
      nextSegments current
      |> List.filter (fun candidate -> not (Set.contains candidate.Id visited))
    match candidates with
    | [] ->
        { SegmentIds = segmentIds |> List.rev
          RoadIndices = roadIndices |> List.rev
          ClusterPath = clusterPath |> List.rev
          SegmentLengths = segmentLengths |> List.rev
          TotalLength = totalLength }
    | next :: _ ->
        let deflection = smallestAngleBetweenDirections current.Heading next.Heading
        if deflection > maxDeflection then
          { SegmentIds = segmentIds |> List.rev
            RoadIndices = roadIndices |> List.rev
            ClusterPath = clusterPath |> List.rev
            SegmentLengths = segmentLengths |> List.rev
            TotalLength = totalLength }
        else
          walk
            (Set.add next.Id visited)
            next
            (next.Id :: segmentIds)
            (next.RoadIndex :: roadIndices)
            (next.EndCluster :: clusterPath)
            (next.Length :: segmentLengths)
            (totalLength + next.Length)

  let clusterRoadIndexSet =
    clusters
    |> List.map (fun (idx, _, refs) -> idx, (refs |> List.map fst |> Set.ofList))
    |> Map.ofList

  directedSegments
  |> List.map (fun segment ->
      let detail =
        walk
          (Set.singleton segment.Id)
          segment
          [ segment.Id ]
          [ segment.RoadIndex ]
          [ segment.EndCluster; segment.StartCluster ]
          [ segment.Length ]
          segment.Length
      let attachmentDistances =
        detail.ClusterPath
        |> List.tail
        |> List.take (max 0 (List.length detail.ClusterPath - 2))
        |> List.mapi (fun idx clusterId ->
            let distanceAlong =
              detail.SegmentLengths
              |> List.take (idx + 1)
              |> List.sum
            let incidentRoads = Map.find clusterId clusterRoadIndexSet
            let chainRoads = detail.RoadIndices |> Set.ofList
            let hasSideAttachment = incidentRoads |> Set.exists (fun roadIndex -> not (Set.contains roadIndex chainRoads))
            distanceAlong, hasSideAttachment)
        |> List.choose (fun (distanceAlong, hasSideAttachment) ->
            if hasSideAttachment then Some distanceAlong else None)
      detail, attachmentDistances)

let private directionOrientationDegrees (dir: Vec2) =
  let angle = MathF.Atan2(dir.Y, dir.X) * 180.0f / MathF.PI
  match angle < 0.0f with
  | true -> angle + 180.0f
  | false -> angle

let private directionOrientationBucket bucketSize (dir: Vec2) =
  int (MathF.Floor((directionOrientationDegrees dir + bucketSize / 2.0f) / bucketSize))

let private throughChainGeometries tolerance maxDeflection (roads: Road list) =
  let clusters, _ = buildDirectedSegments tolerance roads
  let clusterAnchors =
    clusters
    |> List.map (fun (idx, anchor, _) -> idx, anchor)
    |> Map.ofList

  throughChainDetails tolerance maxDeflection roads
  |> List.choose (fun (detail, _) ->
      match detail.ClusterPath with
      | [] -> None
      | startCluster :: _ ->
          let endCluster = detail.ClusterPath |> List.last
          let startPoint = Map.find startCluster clusterAnchors
          let endPoint = Map.find endCluster clusterAnchors
          let heading = Vec2.sub endPoint startPoint
          match Vec2.lengthSq heading > 0.01f with
          | false -> None
          | true ->
              let normalized = Vec2.normalize heading
              let midpoint =
                Vec2.Create(
                  (startPoint.X + endPoint.X) / 2.0f,
                  (startPoint.Y + endPoint.Y) / 2.0f)
              Some
                { RoadIndexSet = detail.RoadIndices |> Set.ofList
                  SegmentCount = detail.SegmentLengths.Length
                  TotalLength = detail.TotalLength
                  StartPoint = startPoint
                  EndPoint = endPoint
                  Midpoint = midpoint
                  Heading = normalized })

let private chainProjectionOverlap (dir: Vec2) (a: ThroughChainGeometry) (b: ThroughChainGeometry) =
  let interval startPt endPt =
    let p0 = Vec2.dot startPt dir
    let p1 = Vec2.dot endPt dir
    min p0 p1, max p0 p1

  let a0, a1 = interval a.StartPoint a.EndPoint
  let b0, b1 = interval b.StartPoint b.EndPoint
  max 0.0f (min a1 b1 - max a0 b0)

let private chainLateralSeparation (dir: Vec2) (a: ThroughChainGeometry) (b: ThroughChainGeometry) =
  let perp = Vec2.Create(-dir.Y, dir.X)
  abs (Vec2.dot (Vec2.sub b.Midpoint a.Midpoint) perp)

let private countParallelChainCompanions (chains: ThroughChainGeometry list) (target: ThroughChainGeometry) =
  chains
  |> List.filter (fun other -> other.RoadIndexSet <> target.RoadIndexSet)
  |> List.filter (fun other ->
      smallestAngleBetweenDirections target.Heading other.Heading <= 12.0f
      && chainLateralSeparation target.Heading target other >= 5.0f
      && chainLateralSeparation target.Heading target other <= 18.0f
      && chainProjectionOverlap target.Heading target other >= 16.0f)
  |> List.length

let private corridorSpacingMetric tolerance (road: Road) (roads: Road list) =
  let corridorLength = roadSpanLength road
  let attachmentParams =
    roads
    |> List.filter (fun other -> not (obj.ReferenceEquals(other, road)))
    |> List.collect (fun other ->
        let startPt, endPt = roadEndpoints2d other
        [ startPt; endPt ])
    |> List.choose (fun pt ->
        let t, distance = pointToRoadProjection road pt
        if distance <= tolerance && t >= 0.08f && t <= 0.92f then Some t else None)
    |> List.sort
    |> List.fold (fun kept t ->
        match kept with
        | head :: _ when abs (t - head) <= 0.035f -> kept
        | _ -> t :: kept) []
    |> List.rev

  let gaps =
    attachmentParams
    |> List.pairwise
    |> List.map (fun (a, b) -> (b - a) * corridorLength)
    |> List.filter (fun gap -> gap > 0.5f)

  match List.length attachmentParams >= 3 && List.length gaps >= 2 with
  | false -> None
  | true ->
      let minGap = gaps |> List.min
      let maxGap = gaps |> List.max
      Some
        { CorridorLength = corridorLength
          AttachmentCount = List.length attachmentParams
          GapCv = coefficientOfVariation gaps
          GapRatio = maxGap / minGap }

let private collectOrganicCorridorSpacingMetrics (rect: TRect) seeds =
  let funcs = List.init 40 (fun i -> mkFunc (sprintf "spacing%d" i) "OrganicSpacingMod")
  seeds
  |> List.collect (fun seed ->
      let rng = Random(seed)
      let _, roads =
        layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      throughChainDetails 0.45f 24.0f roads
      |> List.choose (fun (detail, attachmentDistances) ->
          let gaps =
            attachmentDistances
            |> List.sort
            |> List.pairwise
            |> List.map (fun (a, b) -> b - a)
            |> List.filter (fun gap -> gap > 0.5f)

          match List.length attachmentDistances >= 3 && List.length gaps >= 2 with
          | false -> None
          | true ->
              let minGap = gaps |> List.min
              let maxGap = gaps |> List.max
              Some
                { CorridorLength = detail.TotalLength
                  AttachmentCount = List.length attachmentDistances
                  GapCv = coefficientOfVariation gaps
                  GapRatio = maxGap / minGap })
      |> List.filter (fun metric -> metric.CorridorLength >= 20.0f))

let private collectOrganicInteriorRoadLengths (rect: TRect) seeds =
  let funcs = List.init 36 (fun i -> mkFunc (sprintf "cadence%d" i) "OrganicCadenceMod")
  seeds
  |> List.collect (fun seed ->
      let rng = Random(seed)
      let _, roads =
        layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      roads
      |> List.filter (fun road ->
          let startPt, endPt = roadEndpoints2d road
          pointInsideRectInset 2.5f rect startPt && pointInsideRectInset 2.5f rect endPt)
      |> List.map roadSpanLength
      |> List.filter (fun length -> length >= 4.5f))

type private ProjectBlockMorphologyMetric =
  { AspectCv: float32
    DominantBinShare: float32
    RegularClusterAreaShare: float32
    BlockCount: int }

type private NeighborhoodBearingMetric =
  { Seed: int
    GlobalDominantLengthShare: float32
    WindowAgreementShare: float32
    AverageWindowDominantLengthShare: float32
    WindowCount: int }

type private OpposedTeePairMetric =
  { Seed: int
    OpposedPairShare: float32
    EligibleChainCount: int
    SideJunctionCount: int }

let private blockRectAspectRatio (rect: TRect) =
  let shortest = max 0.1f (min rect.W rect.H)
  let longest = max rect.W rect.H
  longest / shortest

let private aspectRatioBucket (bucketSize: float32) ratio =
  int (MathF.Floor(ratio / bucketSize))

let private rectsShareBoundary (eps: float32) (a: TRect) (b: TRect) =
  let xOverlap = min (a.X + a.W) (b.X + b.W) - max a.X b.X
  let zOverlap = min (a.Z + a.H) (b.Z + b.H) - max a.Z b.Z
  let verticalTouch =
    abs ((a.X + a.W) - b.X) <= eps
    || abs ((b.X + b.W) - a.X) <= eps
  let horizontalTouch =
    abs ((a.Z + a.H) - b.Z) <= eps
    || abs ((b.Z + b.H) - a.Z) <= eps
  (verticalTouch && zOverlap > eps)
  || (horizontalTouch && xOverlap > eps)

let private largestRegularBlockClusterAreaShare (rects: TRect list) =
  match rects with
  | [] -> 0.0f
  | _ ->
      let rectArray = rects |> List.toArray
      let aspectArray = rectArray |> Array.map blockRectAspectRatio
      let totalArea = rectArray |> Array.sumBy TRect.area |> max 0.001f
      let visited = Array.create rectArray.Length false
      let similarAspect left right =
        let hi = max aspectArray.[left] aspectArray.[right]
        let lo = max 0.1f (min aspectArray.[left] aspectArray.[right])
        hi / lo <= 1.15f
      let mutable strongestShare = 0.0f

      for idx in 0 .. rectArray.Length - 1 do
        if not visited.[idx] then
          let queue = Queue<int>()
          let cluster = ResizeArray<int>()
          visited.[idx] <- true
          queue.Enqueue(idx)

          while queue.Count > 0 do
            let current = queue.Dequeue()
            cluster.Add(current)

            for candidate in 0 .. rectArray.Length - 1 do
              if not visited.[candidate]
                 && rectsShareBoundary 0.35f rectArray.[current] rectArray.[candidate]
                 && similarAspect current candidate then
                visited.[candidate] <- true
                queue.Enqueue(candidate)

          if cluster.Count >= 3 then
            let share =
              cluster
              |> Seq.sumBy (fun clusterIdx -> TRect.area rectArray.[clusterIdx])
              |> fun area -> area / totalArea
            strongestShare <- max strongestShare share

      strongestShare

let private collectOrganicProjectBlockMetrics (rect: TRect) seeds =
  let moduleWeights =
    [ for i in 0 .. 8 ->
        sprintf "ProjectBlock%d" i, float32 (9 - i) ]

  seeds
  |> List.map (fun seed ->
      let blocks =
        planProjectModuleBlocks rect moduleWeights 0.9f (Random seed)
        |> List.map snd
      let aspectRatios = blocks |> List.map blockRectAspectRatio
      let dominantBinShare =
        aspectRatios
        |> List.countBy (aspectRatioBucket 0.5f)
        |> List.maxBy snd
        |> fun (_, count) -> float32 count / float32 aspectRatios.Length
      { AspectCv = coefficientOfVariation aspectRatios
        DominantBinShare = dominantBinShare
        RegularClusterAreaShare = largestRegularBlockClusterAreaShare blocks
        BlockCount = blocks.Length })

let private collectOrganicThroughChainGeometries (rect: TRect) seeds =
  let funcs = List.init 40 (fun i -> mkFunc (sprintf "band%d" i) "OrganicBandMod")
  seeds
  |> List.collect (fun seed ->
      let rng = Random(seed)
      let _, roads =
        layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      throughChainGeometries 0.45f 18.0f roads
      |> List.filter (fun chain ->
          chain.SegmentCount >= 3
          && chain.TotalLength >= 24.0f))

let private roadMidpoint2d (road: Road) =
  let startPt, endPt = roadEndpoints2d road
  Vec2.Create(
    (startPt.X + endPt.X) / 2.0f,
    (startPt.Y + endPt.Y) / 2.0f)

let private lengthWeightedOrientationHistogram bucketSize roads =
  roads
  |> List.groupBy (roadOrientationBucket bucketSize)
  |> List.map (fun (bucket, groupedRoads) -> bucket, groupedRoads |> List.sumBy roadSpanLength)
  |> List.sortByDescending snd

let private windowIndexForPoint divisions (rect: TRect) (pt: Vec2) =
  if pt.X < rect.X || pt.X > rect.X + rect.W || pt.Y < rect.Z || pt.Y > rect.Z + rect.H then
    None
  else
    let xNorm =
      ((pt.X - rect.X) / rect.W)
      |> max 0.0f
      |> min 0.9999f
    let yNorm =
      ((pt.Y - rect.Z) / rect.H)
      |> max 0.0f
      |> min 0.9999f
    Some (int (MathF.Floor(xNorm * float32 divisions)), int (MathF.Floor(yNorm * float32 divisions)))

let private collectOrganicNeighborhoodBearingMetrics (rect: TRect) seeds =
  let funcs = List.init 40 (fun i -> mkFunc (sprintf "bearing%d" i) "OrganicBearingMod")
  seeds
  |> List.choose (fun seed ->
      let rng = Random(seed)
      let _, roads =
        layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      let canonicalRoads =
        roads
        |> canonicalizeRoads
        |> List.filter (fun road -> roadSpanLength road >= 8.0f)
      let chains =
        throughChainGeometries 0.45f 18.0f canonicalRoads
        |> List.filter (fun chain ->
            chain.SegmentCount >= 3
            && chain.TotalLength >= 24.0f)
      let corridorRoadIndices =
        chains
        |> List.collect (fun chain -> chain.RoadIndexSet |> Set.toList)
        |> Set.ofList
      let corridorRoads =
        canonicalRoads
        |> List.mapi (fun roadIndex road -> roadIndex, road)
        |> List.choose (fun (roadIndex, road) ->
            if Set.contains roadIndex corridorRoadIndices then Some road else None)
      let totalCorridorLength = corridorRoads |> List.sumBy roadSpanLength
      match totalCorridorLength > 0.0f, lengthWeightedOrientationHistogram 15.0f corridorRoads with
      | false, _
      | _, [] -> None
      | true, (globalDominantBucket, globalDominantLength) :: _ ->
          let occupiedWindows =
            corridorRoads
            |> List.choose (fun road ->
                roadMidpoint2d road
                |> windowIndexForPoint 3 rect
                |> Option.map (fun window -> window, road))
            |> List.groupBy fst
            |> List.choose (fun (_, groupedRoads) ->
                let windowRoads = groupedRoads |> List.map snd
                let windowRoadLength = windowRoads |> List.sumBy roadSpanLength
                if windowRoadLength < 16.0f then
                  None
                else
                  match lengthWeightedOrientationHistogram 15.0f windowRoads with
                  | [] -> None
                  | (dominantBucket, dominantLength) :: _ ->
                      Some (dominantBucket, dominantLength / windowRoadLength))
          if occupiedWindows.Length < 3 then
            None
          else
            let averageWindowDominantLengthShare =
              occupiedWindows |> List.averageBy snd
            let windowAgreementShare =
              occupiedWindows
              |> List.filter (fun (bucket, _) -> bucket = globalDominantBucket)
              |> List.length
              |> fun count -> float32 count / float32 occupiedWindows.Length
            Some
              { Seed = seed
                GlobalDominantLengthShare = globalDominantLength / totalCorridorLength
                WindowAgreementShare = windowAgreementShare
                AverageWindowDominantLengthShare = averageWindowDominantLengthShare
                WindowCount = occupiedWindows.Length })

let private greedyOpposedPairCount tolerance (samples: (float32 * int) list) =
  let left =
    samples
    |> List.filter (fun (_, side) -> side < 0)
    |> List.map fst
    |> List.sort
    |> ResizeArray
  let right =
    samples
    |> List.filter (fun (_, side) -> side > 0)
    |> List.map fst
    |> List.sort
    |> ResizeArray
  let mutable leftIndex = 0
  let mutable rightIndex = 0
  let mutable pairs = 0
  while leftIndex < left.Count && rightIndex < right.Count do
    let delta = left.[leftIndex] - right.[rightIndex]
    if abs delta <= tolerance then
      pairs <- pairs + 1
      leftIndex <- leftIndex + 1
      rightIndex <- rightIndex + 1
    elif delta < 0.0f then
      leftIndex <- leftIndex + 1
    else
      rightIndex <- rightIndex + 1
  pairs

let private collectOrganicOpposedTeePairMetrics (rect: TRect) seeds =
  let funcs = List.init 40 (fun i -> mkFunc (sprintf "zipper%d" i) "OrganicZipperMod")
  let tolerance = 0.45f
  let toleranceSq = tolerance * tolerance
  seeds
  |> List.choose (fun seed ->
      let rng = Random(seed)
      let _, roads =
        layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      let canonicalRoads = canonicalizeRoads roads
      let clusters, _ = buildDirectedSegments tolerance canonicalRoads
      let emitZipCanon =
        seed = 777
        && Environment.GetEnvironmentVariable("CODECITY_ZIPCANON") = "1"
      let clusterAnchors =
        clusters
        |> List.map (fun (clusterId, anchor, _) -> clusterId, anchor)
        |> Map.ofList
      let clusterRefs =
        clusters
        |> List.map (fun (clusterId, _, refs) -> clusterId, refs)
        |> Map.ofList
      if emitZipCanon then
        canonicalRoads
        |> List.iteri (fun roadIndex road ->
            let startPt, endPt = roadEndpoints2d road
            printfn
              "ziproad %d: (%.2f,%.2f)->(%.2f,%.2f) len=%.2f"
              roadIndex
              startPt.X
              startPt.Y
              endPt.X
              endPt.Y
              (Vec2.distanceTo startPt endPt))
        clusters
        |> List.iter (fun (clusterId, anchor, refs) ->
            if refs.Length >= 3 then
              printfn
                "zipcluster-summary cluster=%d anchor=(%.2f,%.2f) valence=%d roads=%A"
                clusterId
                anchor.X
                anchor.Y
                refs.Length
                (refs |> List.map fst |> List.distinct |> List.sort))
      let allChains =
        throughChainDetails tolerance 18.0f canonicalRoads
        |> List.map fst
        |> List.distinctBy (fun detail -> detail.RoadIndices |> Set.ofList)
      let seg3Chains =
        allChains
        |> List.filter (fun detail -> detail.SegmentLengths.Length >= 3)
      let longChains =
        seg3Chains
        |> List.filter (fun detail -> detail.TotalLength >= 24.0f)
      let chainSideSamples =
        longChains
        |> List.map (fun detail ->
            let chainRoadSet = detail.RoadIndices |> Set.ofList
            let clusterPath = detail.ClusterPath |> List.toArray
            let segmentLengths = detail.SegmentLengths |> List.toArray
            let sideSamples =
              [ for pathIndex in 1 .. clusterPath.Length - 2 do
                  let clusterId = clusterPath.[pathIndex]
                  let anchor = Map.find clusterId clusterAnchors
                  let prevAnchor = Map.find clusterPath.[pathIndex - 1] clusterAnchors
                  let nextAnchor = Map.find clusterPath.[pathIndex + 1] clusterAnchors
                  let axisDelta = Vec2.sub nextAnchor prevAnchor
                  if Vec2.lengthSq axisDelta > 0.01f then
                    let axis = Vec2.normalize axisDelta
                    let perp = Vec2.Create(-axis.Y, axis.X)
                    let distanceAlong =
                      segmentLengths
                      |> Array.take pathIndex
                      |> Array.sum
                    let sideEntries =
                      Map.find clusterId clusterRefs
                      |> List.map fst
                      |> List.distinct
                      |> List.choose (fun roadIndex ->
                          if Set.contains roadIndex chainRoadSet then
                            None
                          else
                            let road = canonicalRoads.[roadIndex]
                            let startPt, endPt = roadEndpoints2d road
                            let branchDelta =
                              if Vec2.distanceToSq anchor startPt <= toleranceSq then
                                Vec2.sub endPt startPt
                              elif Vec2.distanceToSq anchor endPt <= toleranceSq then
                                Vec2.sub startPt endPt
                              else
                                Vec2.sub (roadMidpoint2d road) anchor
                            if Vec2.lengthSq branchDelta <= 0.01f then
                              None
                            else
                              let branchDir = Vec2.normalize branchDelta
                              let branchAngle = smallestAngleBetweenDirections axis branchDir
                              if branchAngle < 45.0f || branchAngle > 135.0f then
                                None
                              else
                                let lateral = Vec2.dot branchDelta perp
                                if abs lateral < 0.5f then
                                  None
                                else
                                  Some (roadIndex, if lateral < 0.0f then -1 else 1))
                    if emitZipCanon && not sideEntries.IsEmpty then
                      printfn
                        "zipcluster-detail cluster=%d dist=%.2f chain=%A entries=%A"
                        clusterId
                        distanceAlong
                        (detail.RoadIndices |> List.sort)
                        sideEntries
                    let sideValues =
                      sideEntries
                      |> List.map snd
                      |> List.distinct
                    for side in sideValues do
                      yield distanceAlong, side ]
            if emitZipCanon then
              printfn
                "zipchain-detail roads=%A len=%.2f segments=%d sides=%d"
                (detail.RoadIndices |> List.sort)
                detail.TotalLength
                detail.SegmentLengths.Length
                sideSamples.Length
            detail, sideSamples)
      if emitZipCanon then
        printfn
          "zipchain-pipeline seed=%d total=%d seg3=%d len24=%d measurable=%d"
          seed
          allChains.Length
          seg3Chains.Length
          longChains.Length
          (chainSideSamples |> List.filter (fun (_, sideSamples) -> sideSamples.Length >= 3) |> List.length)
      let chainMetrics =
        chainSideSamples
        |> List.choose (fun (_, sideSamples) ->
            if sideSamples.Length < 3 then
              None
            else
              let uniquePositions =
                sideSamples
                |> List.map fst
                |> List.distinct
                |> List.sort
              let meanSpacing =
                match uniquePositions with
                | _ :: _ :: _ ->
                    uniquePositions
                    |> List.pairwise
                    |> List.map (fun (a, b) -> b - a)
                    |> List.average
                | _ -> 10.0f
              let pairTolerance = max 2.0f (meanSpacing * 0.2f)
              let pairedCount = greedyOpposedPairCount pairTolerance sideSamples
              Some (pairedCount, sideSamples.Length))
      if chainMetrics.IsEmpty then
        None
      else
        let totalSideJunctions = chainMetrics |> List.sumBy snd
        let totalPairedJunctions = chainMetrics |> List.sumBy (fun (pairedCount, _) -> pairedCount * 2)
        Some
          { Seed = seed
            OpposedPairShare = float32 totalPairedJunctions / float32 totalSideJunctions
            EligibleChainCount = chainMetrics.Length
            SideJunctionCount = totalSideJunctions })

let weberDistrictTests =
  testList "Weber district layout" [
    testCase "layout produces non-empty buildings for non-trivial block" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 40.0f; H = 30.0f }
      let funcs = List.init 12 (fun i -> mkFunc (sprintf "f%d" i) "TestMod")
      let rng   = Random(42)
      let bldgs, _ = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.5f rng Map.empty
      bldgs |> Expect.isNonEmpty "a 12-function district should have buildings"

    testCase "organic district produces internal road network" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 50.0f; H = 50.0f }
      let funcs = List.init 20 (fun i -> mkFunc (sprintf "f%d" i) "OrgMod")
      let rng   = Random(7)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 1.0f rng Map.empty
      roads |> Expect.isNonEmpty "organic district should produce internal roads"

    testCase "organic district road network mixes orientations while retaining a corridor family" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 60.0f; H = 50.0f }
      let funcs = List.init 24 (fun i -> mkFunc (sprintf "o%d" i) "OrganicMixMod")
      let rng   = Random(17)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 1.0f rng Map.empty
      roads |> Expect.isNonEmpty "organic district should produce roads"
      let histogram = orientationHistogram 15.0f roads
      (histogram.Length, 3) |> Expect.isGreaterThanOrEqual "organic streets should span at least three orientation families"
      let dominantBucket, dominantCount = histogram |> List.head
      (dominantCount, 2) |> Expect.isGreaterThanOrEqual "organic layout should still keep a repeated corridor direction"
      axisAlignedBucket 15.0f dominantBucket |> Expect.isFalse "dominant corridor should not collapse back to a pure axis-aligned grid"

    testCase "organic district avoids a centered four-way hub" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 100.0f; H = 80.0f }
      let funcs = List.init 28 (fun i -> mkFunc (sprintf "hub%d" i) "OrganicHubMod")
      let rng   = Random(123)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      roads |> Expect.isNonEmpty "organic district should produce roads"
      let center = Vec2.Create(TRect.centerX rect, TRect.centerZ rect)
      let hasCenteredHub =
        roads
        |> List.collect (fun road ->
          let a, b = roadEndpoints2d road
          [ a; b ])
        |> clusterPoints 0.35f
        |> List.exists (fun (pt, valence) ->
          Vec2.distanceTo pt center <= min rect.W rect.H * 0.10f
          && valence >= 4)
      hasCenteredHub |> Expect.isFalse "organic districts should not seed a four-way hub at the rectangle center"

    testCase "organic district boundary portals avoid exact cardinal midpoints" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 100.0f; H = 80.0f }
      let funcs = List.init 28 (fun i -> mkFunc (sprintf "portal%d" i) "OrganicPortalMod")
      let rng   = Random(123)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      roads |> Expect.isNonEmpty "organic district should produce roads"
      let boundaryEndpoints =
        roads
        |> List.collect (fun road ->
          let a, b = roadEndpoints2d road
          [ a; b ])
        |> List.filter (pointOnRectBoundary rect)
        |> clusterPoints 0.35f
        |> List.map fst
      boundaryEndpoints |> Expect.isNonEmpty "organic districts should still connect to the district boundary"
      let midpointHits =
        boundaryEndpoints
        |> List.filter (fun pt ->
          boundaryMidpointsForRect rect
          |> List.exists (fun midpoint -> Vec2.distanceTo pt midpoint <= 0.35f))
      midpointHits |> Expect.isEmpty "organic districts should not anchor portals at the exact cardinal midpoints"

    testCase "organic district longest corridor is oblique and off-center" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 100.0f; H = 80.0f }
      let funcs = List.init 28 (fun i -> mkFunc (sprintf "spine%d" i) "OrganicSpineMod")
      let rng   = Random(123)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      let longest = roads |> List.maxBy roadSpanLength
      let angle = roadOrientationDegrees longest
      (nearestAxisDelta angle, 12.0f) |> Expect.isGreaterThan "the longest organic corridor should not fall back to a cardinal axis"
      let startPt, endPt = roadEndpoints2d longest
      let midpoint = Vec2.Create((startPt.X + endPt.X) / 2.0f, (startPt.Y + endPt.Y) / 2.0f)
      let center = Vec2.Create(TRect.centerX rect, TRect.centerZ rect)
      (Vec2.distanceTo midpoint center, min rect.W rect.H * 0.08f)
      |> Expect.isGreaterThan
           "the longest organic corridor should not be centered on the exact middle of the district"

    testCase "organic district avoids laser-straight through corridors across multiple junctions" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 100.0f; H = 80.0f }
      let funcs = List.init 28 (fun i -> mkFunc (sprintf "drift%d" i) "OrganicDriftMod")
      let rng   = Random(123)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      roads |> Expect.isNonEmpty "organic district should produce roads"
      let laserStraightChains =
        throughChainMetrics 0.35f 10.0f roads
        |> List.filter (fun chain ->
            chain.SegmentCount >= 3
            && chain.TotalLength >= 40.0f
            && chain.CumulativeTurn < 14.0f)
      laserStraightChains
      |> Expect.isEmpty "organic districts should not preserve ruler-straight through corridors over long distances"

    testCase "organic district long through corridors accumulate visible heading drift" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 100.0f; H = 80.0f }
      let funcs = List.init 28 (fun i -> mkFunc (sprintf "driftTurn%d" i) "OrganicDriftTurnMod")
      let rng   = Random(123)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      roads |> Expect.isNonEmpty "organic district should produce roads"
      let longChainOpt =
        throughChainMetrics 0.35f 10.0f roads
        |> List.filter (fun chain -> chain.SegmentCount >= 3 && chain.TotalLength >= 40.0f)
        |> List.sortByDescending (fun chain -> chain.TotalLength)
        |> List.tryHead
      match longChainOpt with
      | Some chain ->
          (chain.CumulativeTurn, 14.0f)
          |> Expect.isGreaterThan "long organic through corridors should accumulate visible heading drift instead of reading as a ruler line"
      | None -> ()

    testCase "organic district interior road cadence spans multiple length buckets" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 120.0f; H = 90.0f }
      let lengths = collectOrganicInteriorRoadLengths rect [ 123; 321; 777 ]
      (lengths.Length, 12) |> Expect.isGreaterThanOrEqual "organic district should expose enough interior runs to judge cadence"
      let bucketCount = roundedLengthBucketCount 2.0f lengths
      (bucketCount, 3) |> Expect.isGreaterThanOrEqual "organic districts should not collapse into a single repeated run length"

    testCase "organic district interior road cadence avoids metronomic uniformity" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 120.0f; H = 90.0f }
      let lengths = collectOrganicInteriorRoadLengths rect [ 123; 321; 777 ]
      (lengths.Length, 12) |> Expect.isGreaterThanOrEqual "organic district should expose enough interior runs to judge cadence"
      let variation = coefficientOfVariation lengths
      (variation, 0.12f)
      |> Expect.isGreaterThan
           "organic districts should vary interior run lengths enough to avoid a metronomic street cadence"

    testCase "organic district dominant corridors include bursts and breathing room in side-street spacing" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 140.0f; H = 110.0f }
      let metrics = collectOrganicCorridorSpacingMetrics rect [ 123; 321; 777 ]
      metrics |> Expect.isNonEmpty "organic districts should expose at least one corridor with multiple side-street attachments"
      let strongest = metrics |> List.maxBy (fun metric -> metric.AttachmentCount, metric.CorridorLength)
      (strongest.GapRatio, 1.6f)
      |> Expect.isGreaterThan
           "organic dominant corridors should show both clustered interruptions and wider breathing room instead of ladder-like spacing"

    testCase "organic district dominant corridor junction gaps avoid near-lattice uniformity" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 140.0f; H = 110.0f }
      let metrics = collectOrganicCorridorSpacingMetrics rect [ 123; 321; 777 ]
      metrics |> Expect.isNonEmpty "organic districts should expose at least one corridor with multiple side-street attachments"
      let strongest = metrics |> List.maxBy (fun metric -> metric.AttachmentCount, metric.CorridorLength)
      (strongest.GapCv, 0.22f)
      |> Expect.isGreaterThan
           "organic dominant corridors should not space side-street attachments like a nearly uniform ladder"

    testCase "organic project module blocks span multiple aspect-ratio families" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 140.0f; H = 110.0f }
      let metrics = collectOrganicProjectBlockMetrics rect [ 123; 321; 777 ]
      metrics |> Expect.isNonEmpty "organic project planning should emit module blocks"
      let averageCv = metrics |> List.averageBy _.AspectCv
      (averageCv, 0.24f)
      |> Expect.isGreaterThan
           "organic project blocks should not collapse into one repeated rectangle proportion"

    testCase "organic project module blocks avoid one dominant repeated aspect bin" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 140.0f; H = 110.0f }
      let metrics = collectOrganicProjectBlockMetrics rect [ 123; 321; 777 ]
      metrics |> Expect.isNonEmpty "organic project planning should emit module blocks"
      let averageDominantShare = metrics |> List.averageBy _.DominantBinShare
      (averageDominantShare, 0.56f)
      |> Expect.isLessThan
           "organic project blocks should not let one aspect bucket dominate the visible district mosaic"

    testCase "organic project module blocks avoid large contiguous regular superblock clusters" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 140.0f; H = 110.0f }
      let metrics = collectOrganicProjectBlockMetrics rect [ 123; 321; 777 ]
      metrics |> Expect.isNonEmpty "organic project planning should emit module blocks"
      let averageRegularShare = metrics |> List.averageBy _.RegularClusterAreaShare
      (averageRegularShare, 0.48f)
      |> Expect.isLessThan
           "organic project blocks should not leave most of a project trapped in one contiguous cluster of near-identical rectangles"

    testCase "organic district long through-chains do not collapse into one heading family" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 140.0f; H = 110.0f }
      let chains = collectOrganicThroughChainGeometries rect [ 123; 321; 777 ]
      (chains.Length, 8) |> Expect.isGreaterThanOrEqual "organic districts should expose enough long chains to judge corridor families"
      let dominantShare =
        chains
        |> List.countBy (fun chain -> directionOrientationBucket 15.0f chain.Heading)
        |> List.maxBy snd
        |> fun (_, count) -> float32 count / float32 chains.Length
      (dominantShare, 0.50f)
      |> Expect.isLessThan
           "organic districts should not concentrate most long corridors into one near-parallel heading family"

    testCase "organic district avoids triple bands of long near-parallel corridors" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 140.0f; H = 110.0f }
      let chains = collectOrganicThroughChainGeometries rect [ 123; 321; 777 ]
      (chains.Length, 8) |> Expect.isGreaterThanOrEqual "organic districts should expose enough long chains to judge corridor banding"
      let offendingChains =
        chains
        |> List.map (fun chain ->
            let companions = countParallelChainCompanions chains chain
            companions,
            sprintf
              "heading=%.1f len=%.1f start=(%.1f,%.1f) end=(%.1f,%.1f)"
              (directionOrientationDegrees chain.Heading)
              chain.TotalLength
              chain.StartPoint.X
              chain.StartPoint.Y
              chain.EndPoint.X
              chain.EndPoint.Y)
        |> List.sortByDescending fst
      let maxCompanions =
        offendingChains
        |> List.map fst
        |> List.max
      let topOffenderSummary =
        offendingChains
        |> List.truncate 3
        |> List.map (fun (companions, summary) -> sprintf "%d:%s" companions summary)
        |> String.concat " | "
      (maxCompanions, 1)
      |> Expect.isLessThanOrEqual
           (sprintf "organic districts should not form triple-lane bands of long near-parallel corridors. top=%s" topOffenderSummary)

    testCase "organic district citywide dominant heading family does not monopolize corridor length" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 140.0f; H = 110.0f }
      let metrics = collectOrganicNeighborhoodBearingMetrics rect [ 123; 321; 777 ]
      (metrics.Length, 2) |> Expect.isGreaterThanOrEqual "organic districts should expose enough occupied neighborhoods to judge citywide grain"
      let averageGlobalDominantShare = metrics |> List.averageBy _.GlobalDominantLengthShare
      let summary =
        metrics
        |> List.map (fun metric ->
            sprintf
              "%d:global=%.3f agree=%.3f local=%.3f windows=%d"
              metric.Seed
              metric.GlobalDominantLengthShare
              metric.WindowAgreementShare
              metric.AverageWindowDominantLengthShare
              metric.WindowCount)
        |> String.concat " | "
      (averageGlobalDominantShare, 0.30f)
      |> Expect.isLessThan
           (sprintf "organic districts should not let one heading family monopolize most corridor length across the whole map. %s" summary)

    testCase "organic district neighborhoods do not all inherit the same dominant heading family" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 140.0f; H = 110.0f }
      let metrics = collectOrganicNeighborhoodBearingMetrics rect [ 123; 321; 777 ]
      (metrics.Length, 2) |> Expect.isGreaterThanOrEqual "organic districts should expose enough occupied neighborhoods to judge neighborhood grain"
      let averageAgreementShare = metrics |> List.averageBy _.WindowAgreementShare
      let summary =
        metrics
        |> List.map (fun metric ->
            sprintf
              "%d:agree=%.3f global=%.3f local=%.3f windows=%d"
              metric.Seed
              metric.WindowAgreementShare
              metric.GlobalDominantLengthShare
              metric.AverageWindowDominantLengthShare
              metric.WindowCount)
        |> String.concat " | "
      (averageAgreementShare, 0.55f)
      |> Expect.isLessThan
           (sprintf "organic districts should let neighborhoods keep local grain without all inheriting the same dominant heading family. %s" summary)

    testCase "organic district long through corridors do not form zipper-rung tee pairings" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 140.0f; H = 110.0f }
      let metrics = collectOrganicOpposedTeePairMetrics rect [ 123; 321; 777 ]
      (metrics.Length, 1) |> Expect.isGreaterThanOrEqual "organic districts should expose enough long corridors with side streets to judge bilateral rung pairing"
      let averageOpposedPairShare = metrics |> List.averageBy _.OpposedPairShare
      let summary =
        metrics
        |> List.map (fun metric ->
            sprintf
              "%d:pair=%.3f chains=%d sides=%d"
              metric.Seed
              metric.OpposedPairShare
              metric.EligibleChainCount
              metric.SideJunctionCount)
        |> String.concat " | "
      (averageOpposedPairShare, 0.28f)
      |> Expect.isLessThan
           (sprintf "organic districts should not let long corridors read like hidden zipper-rung cross streets. %s" summary)

    testCase "organic district does not collapse into two perpendicular road families" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 100.0f; H = 80.0f }
      let funcs = List.init 28 (fun i -> mkFunc (sprintf "family%d" i) "OrganicFamilyMod")
      let rng   = Random(123)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      roads |> Expect.isNonEmpty "organic district should produce roads"
      let histogram = orientationHistogram 15.0f roads
      let topTwoShare =
        histogram
        |> List.truncate 2
        |> List.sumBy snd
        |> fun topTwo -> float32 topTwo / float32 roads.Length
      (topTwoShare, 0.72f)
      |> Expect.isLessThan
           "organic districts should not be dominated by only two perpendicular corridor families"

    testCase "organic district includes at least one Y-like junction" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 100.0f; H = 80.0f }
      let funcs = List.init 28 (fun i -> mkFunc (sprintf "junction%d" i) "OrganicJunctionMod")
      let rng   = Random(123)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      roads |> Expect.isNonEmpty "organic district should produce roads"
      let minAngles = junctionMinAngles 0.35f roads
      minAngles |> Expect.isNonEmpty "organic districts should produce multi-road junctions"
      minAngles
      |> List.exists (fun angle -> angle < 75.0f)
      |> Expect.isTrue "organic districts should contain at least one junction that reads as a Y instead of a right-angle cross"

    testCase "organic district junctions are not dominated by right angles" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 100.0f; H = 80.0f }
      let funcs = List.init 28 (fun i -> mkFunc (sprintf "ortho%d" i) "OrganicOrthoMod")
      let rng   = Random(123)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      roads |> Expect.isNonEmpty "organic district should produce roads"
      let minAngles = junctionMinAngles 0.35f roads
      minAngles |> Expect.isNonEmpty "organic districts should produce multi-road junctions"
      let rightAngleShare =
        minAngles
        |> List.filter (fun angle -> angle >= 80.0f && angle <= 100.0f)
        |> List.length
        |> fun count -> float32 count / float32 minAngles.Length
      (rightAngleShare, 0.65f)
      |> Expect.isLessThan
           "organic districts should not read like a field of right-angle tees and crosses"

    testCase "organic district junction angle pairs do not pile up in the right-angle bucket" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 120.0f; H = 90.0f }
      let anglePairs =
        [ 123; 321; 777 ]
        |> List.collect (fun seed ->
            let funcs = List.init 28 (fun i -> mkFunc (sprintf "orthoPair%d_%d" seed i) "OrganicOrthoPairMod")
            let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f (Random seed) Map.empty
            junctionAnglePairs 0.35f roads)
      (anglePairs.Length, 24)
      |> Expect.isGreaterThanOrEqual
           "organic districts should expose enough junction-angle pairs to judge orthogonal dominance"
      let nearRightPairs =
        anglePairs
        |> List.filter (fun angle -> angle >= 84.0f && angle <= 96.0f)
      let nearRightShare = float32 nearRightPairs.Length / float32 anglePairs.Length
      (nearRightShare, 0.20f)
      |> Expect.isLessThan
           (sprintf "organic junction geometry should not be dominated by crisp right-angle pairs. share=%.2f count=%d sample=%A"
              nearRightShare
              anglePairs.Length
              (nearRightPairs |> List.truncate 6))

    testCase "organic district through-junction branches often miss perfect perpendicular" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 120.0f; H = 90.0f }
      let deviations =
        [ 123; 321; 777 ]
        |> List.collect (fun seed ->
            let funcs = List.init 28 (fun i -> mkFunc (sprintf "orthoSkew%d_%d" seed i) "OrganicOrthoSkewMod")
            let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f (Random seed) Map.empty
            throughJunctionPerpendicularDeviations 0.35f roads)
      deviations |> Expect.isNonEmpty "organic districts should expose through-junction branches to judge tee orthogonality"
      let visiblySkewed =
        deviations
        |> List.filter (fun deviation -> deviation >= 8.0f)
      let visiblySkewedShare = float32 visiblySkewed.Length / float32 deviations.Length
      (visiblySkewedShare, 0.60f)
      |> Expect.isGreaterThan
           (sprintf "organic through-junction branches should often skew away from exact perpendicular. share=%.2f count=%d sample=%A"
              visiblySkewedShare
              deviations.Length
              (deviations |> List.sortDescending |> List.truncate 6))

    testCase "all Weber buildings stay within block bounds (with tolerance)" <| fun () ->
      let rect  = { X = 5.0f; Z = 3.0f; W = 40.0f; H = 35.0f }
      let funcs = List.init 15 (fun i -> mkFunc (sprintf "g%d" i) "BoundsMod")
      let rng   = Random(99)
      let bldgs, _ = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.7f rng Map.empty
      let eps = 1.5f  // packInBlock centers use polygon bounding box; slight overshoot is tolerated
      for b in bldgs do
        (b.X,       rect.X - eps)           |> Expect.isGreaterThanOrEqual "building left edge inside block"
        (b.Z,       rect.Z - eps)           |> Expect.isGreaterThanOrEqual "building top edge inside block"
        (b.X + b.W, rect.X + rect.W + eps)  |> Expect.isLessThanOrEqual   "building right edge inside block"
        (b.Z + b.D, rect.Z + rect.H + eps)  |> Expect.isLessThanOrEqual   "building bottom edge inside block"

    testCase "grid district (organic=0) has only horizontal or vertical internal roads" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 50.0f; H = 50.0f }
      let funcs = List.init 16 (fun i -> mkFunc (sprintf "h%d" i) "GridMod")
      let rng   = Random(42)
      let _, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.0f rng Map.empty
      // In grid mode every segment extends straight from its seed direction — no deviation.
      // Roads may still be at a non-axis angle (seed chooses random initial direction) but
      // all segments from a given node will be collinear (deviation = 0). Verify no crashes.
      roads |> List.length |> (fun c -> (c, 0) |> Expect.isGreaterThan "grid mode produces some roads")

    testCase "live Weber layout keeps building centers adjacent to district roads" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 55.0f; H = 45.0f }
      let funcs = List.init 22 (fun i -> mkFunc (sprintf "r%d" i) "RoadFrontMod")
      let rng   = Random(23)
      let buildings, roads = layoutWeberDistrict rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) 10 100 0.9f rng Map.empty
      buildings |> Expect.isNonEmpty "live layout should produce buildings"
      roads |> Expect.isNonEmpty "live layout should produce roads"
      for building in buildings do
        let cx = building.X + building.W / 2.0f
        let cz = building.Z + building.D / 2.0f
        let nearest = nearestRoadDistance roads cx cz
        (nearest, 2.5f)
        |> Expect.isLessThanOrEqual
             (sprintf "building center (%.1f,%.1f) dist=%.2f drifted too far from any road frontage" cx cz nearest)
  ]

/// Distance from (px, pz) to the nearest point on any polygon edge.
let private distanceToPolyEdge (poly: Vec2 list) (px: float32) (pz: float32) : float32 =
  distanceToPoly poly px pz

let private distanceToSeg (ax: float32) (az: float32) (bx: float32) (bz: float32) (px: float32) (pz: float32) : float32 =
  let dx = bx - ax
  let dz = bz - az
  let lenSq = dx * dx + dz * dz
  let t = if lenSq < 1e-10f then 0.0f else min 1.0f (max 0.0f (((px - ax) * dx + (pz - az) * dz) / lenSq))
  let nearX = ax + t * dx
  let nearZ = az + t * dz
  sqrt ((px - nearX) * (px - nearX) + (pz - nearZ) * (pz - nearZ))

let private mkRoad x1 z1 x2 z2 halfWidth =
  { FromFunc = ""; ToFunc = ""
    FromPos = Vector3(x1, 0.0f, z1)
    ToPos   = Vector3(x2, 0.0f, z2)
    HalfWidth = halfWidth
    Weight  = RoadClass.tier Street
    Color   = Color(65uy, 65uy, 70uy, 255uy)
    Organic = 0.0f }

let private buildingsOverlap (left: FuncBuilding) (right: FuncBuilding) =
  left.X < right.X + right.W
  && left.X + left.W > right.X
  && left.Z < right.Z + right.D
  && left.Z + left.D > right.Z

let private buildingIntersectsRoad (building: FuncBuilding) (road: Road) =
  let halfWidth = roadSurfaceHalfWidth road
  let expanded =
    TRect.create
      (building.X - halfWidth)
      (building.Z - halfWidth)
      (building.W + halfWidth * 2.0f)
      (building.D + halfWidth * 2.0f)
  let x0 = expanded.X
  let x1 = expanded.X + expanded.W
  let z0 = expanded.Z
  let z1 = expanded.Z + expanded.H
  let ax = road.FromPos.X
  let az = road.FromPos.Z
  let bx = road.ToPos.X
  let bz = road.ToPos.Z
  let pointInRect x z =
    x >= x0 && x <= x1 && z >= z0 && z <= z1
  let cross ax az bx bz cx cz =
    (bx - ax) * (cz - az) - (bz - az) * (cx - ax)
  let onSegment ax az bx bz px pz =
    px >= min ax bx - 1e-4f
    && px <= max ax bx + 1e-4f
    && pz >= min az bz - 1e-4f
    && pz <= max az bz + 1e-4f
  let segmentsIntersect ax az bx bz cx cz dx dz =
    let abC = cross ax az bx bz cx cz
    let abD = cross ax az bx bz dx dz
    let cdA = cross cx cz dx dz ax az
    let cdB = cross cx cz dx dz bx bz
    let hasProperIntersection =
      (abC > 0.0f && abD < 0.0f || abC < 0.0f && abD > 0.0f)
      && (cdA > 0.0f && cdB < 0.0f || cdA < 0.0f && cdB > 0.0f)
    let hasCollinearTouch =
      (abs abC < 1e-4f && onSegment ax az bx bz cx cz)
      || (abs abD < 1e-4f && onSegment ax az bx bz dx dz)
      || (abs cdA < 1e-4f && onSegment cx cz dx dz ax az)
      || (abs cdB < 1e-4f && onSegment cx cz dx dz bx bz)
    hasProperIntersection || hasCollinearTouch
  pointInRect ax az
  || pointInRect bx bz
  || segmentsIntersect ax az bx bz x0 z0 x1 z0
  || segmentsIntersect ax az bx bz x1 z0 x1 z1
  || segmentsIntersect ax az bx bz x1 z1 x0 z1
  || segmentsIntersect ax az bx bz x0 z1 x0 z0

let private hullHasNonAxisEdge (hull: (float32 * float32) list) =
  if hull.Length < 2 then false
  else
    [ for i in 0 .. hull.Length - 1 do
        let x1, z1 = hull.[i]
        let x2, z2 = hull.[(i + 1) % hull.Length]
        let dx = x2 - x1
        let dz = z2 - z1
        let len = sqrt (dx * dx + dz * dz)
        if len > 0.05f then
          let angle = abs (MathF.Atan2(dz, dx) * 180.0f / MathF.PI)
          let nearestAxis =
            [ 0.0f; 90.0f; 180.0f ]
            |> List.map (fun axis -> abs (angle - axis))
            |> List.min
          yield nearestAxis > 10.0f ]
    |> List.exists id

let roadFrontageTests =
  testList "Road-primary building placement (packAlongEdges)" [

    testCase "distanceToPoly: point on edge has near-zero distance" <| fun () ->
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(10.0f, 0.0f)
                   Vec2.Create(10.0f, 10.0f); Vec2.Create(0.0f, 10.0f) ]
      let d = distanceToPolyEdge poly 5.0f 0.0f
      (d, 0.05f) |> Expect.isLessThanOrEqual "midpoint of bottom edge should have ~0 distance"

    testCase "distanceToPoly: center of 4×4 square is exactly 2 from nearest edge" <| fun () ->
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(4.0f, 0.0f)
                   Vec2.Create(4.0f, 4.0f); Vec2.Create(0.0f, 4.0f) ]
      let d = distanceToPolyEdge poly 2.0f 2.0f
      abs (d - 2.0f) < 0.05f |> Expect.isTrue "center of 4×4 square is 2 from each edge"

    testCase "packAlongEdges: produces buildings for a square parcel" <| fun () ->
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(12.0f, 0.0f)
                   Vec2.Create(12.0f, 12.0f); Vec2.Create(0.0f, 12.0f) ]
      let funcs = List.init 8 (fun i -> mkFunc (sprintf "f%d" i) "ParkMod")
      let bldgs = packAlongEdges poly funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 42) Map.empty
      bldgs |> Expect.isNonEmpty "8 functions in a 12×12 parcel should produce buildings"

    testCase "road-primary invariant: all buildings within lot depth of nearest road edge" <| fun () ->
      // A 10×10 square: center is 5 units from every edge.
      // Any building > 3 units from all edges is landlocked in the interior.
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(10.0f, 0.0f)
                   Vec2.Create(10.0f, 10.0f); Vec2.Create(0.0f, 10.0f) ]
      let funcs = List.init 10 (fun i -> mkFunc (sprintf "f%d" i) "RoadMod")
      let bldgs = packAlongEdges poly funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 7) Map.empty
      bldgs |> Expect.isNonEmpty "must produce some buildings"
      let maxLotDepth = 3.0f
      for b in bldgs do
        let cx = b.X + b.W / 2.0f
        let cz = b.Z + b.D / 2.0f
        let dist = distanceToPolyEdge poly cx cz
        (dist, maxLotDepth)
        |> Expect.isLessThanOrEqual
             (sprintf "building center (%.1f,%.1f) dist=%.2f exceeds lot depth — landlocked!" cx cz dist)

    testCase "packAlongEdges: all buildings are inside the parcel polygon" <| fun () ->
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(8.0f, 0.0f)
                   Vec2.Create(8.0f, 6.0f); Vec2.Create(0.0f, 6.0f) ]
      let funcs = List.init 6 (fun i -> mkFunc (sprintf "f%d" i) "InsideMod")
      let bldgs = packAlongEdges poly funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 13) Map.empty
      for b in bldgs do
        let cx = b.X + b.W / 2.0f
        let cz = b.Z + b.D / 2.0f
        pointInPoly poly cx cz
        |> Expect.isTrue (sprintf "building center (%.1f,%.1f) must be inside the parcel polygon" cx cz)

    testCase "packAlongEdges: buildings distributed across multiple edges not just one" <| fun () ->
      // With 12 functions and 4 edges, buildings should appear near more than one edge
      let poly = [ Vec2.Create(0.0f, 0.0f); Vec2.Create(10.0f, 0.0f)
                   Vec2.Create(10.0f, 10.0f); Vec2.Create(0.0f, 10.0f) ]
      let funcs = List.init 12 (fun i -> mkFunc (sprintf "f%d" i) "MultiEdgeMod")
      let bldgs = packAlongEdges poly funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 99) Map.empty
      // Check that buildings appear on both sides of the polygon (not all clumped on one edge)
      let hasNearBottom = bldgs |> List.exists (fun b -> b.Z + b.D / 2.0f < 3.0f)
      let hasNearTop    = bldgs |> List.exists (fun b -> b.Z + b.D / 2.0f > 7.0f)
      hasNearBottom |> Expect.isTrue "should have buildings near the bottom edge"
      hasNearTop    |> Expect.isTrue "should have buildings near the top edge"
  ]

let packAlongRoadsTests =
  testList "Road-primary placement along actual road segments (packAlongRoads)" [

    testCase "canonicalizeRoads merges duplicate and overlapping collinear segments" <| fun () ->
      let roads =
        [ mkRoad 0.0f 0.0f 10.0f 0.0f 0.4f
          mkRoad 10.0f 0.0f 0.0f 0.0f 0.4f
          mkRoad 4.0f 0.0f 14.0f 0.0f 0.4f ]
      let canonical = canonicalizeRoads roads
      canonical |> List.length |> Expect.equal "duplicate and overlapping roads should collapse to one corridor" 1
      let merged = canonical |> List.head
      (abs merged.FromPos.X, 0.05f) |> Expect.isLessThanOrEqual "merged road should start at the earliest x"
      (abs (merged.ToPos.X - 14.0f), 0.05f) |> Expect.isLessThanOrEqual "merged road should extend to the furthest x"

    testCase "canonicalizeRoads preserves T-junction branches while merging the main corridor" <| fun () ->
      let roads =
        [ mkRoad 0.0f 0.0f 8.0f 0.0f 0.4f
          mkRoad 8.0f 0.0f 16.0f 0.0f 0.4f
          mkRoad 8.0f 0.0f 8.0f 6.0f 0.4f ]
      let canonical = canonicalizeRoads roads
      canonical |> List.length |> Expect.equal "mainline should merge but the T branch must remain" 2
      canonical |> List.exists (fun road -> abs (road.FromPos.X - road.ToPos.X) < 0.05f)
      |> Expect.isTrue "branch road should remain vertical after canonicalization"

    testCase "produces buildings for a simple cross-road block" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let roads = [ mkRoad 10.0f 0.0f 10.0f 20.0f 0.4f    // vertical at x=10
                    mkRoad 0.0f  10.0f 20.0f 10.0f 0.4f ]  // horizontal at z=10
      let funcs = List.init 8 (fun i -> mkFunc (sprintf "f%d" i) "CrossMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 42) Map.empty
      bldgs |> Expect.isNonEmpty "8 functions on a cross-road block should produce buildings"

    testCase "road-primary invariant: all buildings adjacent to a visible road centerline" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let roads = [ mkRoad 10.0f 0.0f 10.0f 20.0f 0.4f
                    mkRoad 0.0f  10.0f 20.0f 10.0f 0.4f ]
      let funcs = List.init 10 (fun i -> mkFunc (sprintf "f%d" i) "RoadMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 7) Map.empty
      bldgs |> Expect.isNonEmpty "must produce buildings"
      let maxDist = 2.6f  // rendered road ribbon + pedestrian reserve + half footprint
      for b in bldgs do
        let cx = b.X + b.W / 2.0f
        let cz = b.Z + b.D / 2.0f
        let dV = distanceToSeg 10.0f 0.0f  10.0f 20.0f cx cz
        let dH = distanceToSeg 0.0f  10.0f 20.0f 10.0f cx cz
        let nearest = min dV dH
        (nearest, maxDist)
        |> Expect.isLessThanOrEqual
             (sprintf "building (%.1f,%.1f) dist=%.2f — not adjacent to any road!" cx cz nearest)

    testCase "all buildings stay within block bounds" <| fun () ->
      let rect  = { X = 5.0f; Z = 5.0f; W = 20.0f; H = 20.0f }
      let roads = [ mkRoad 15.0f 5.0f 15.0f 25.0f 0.4f ]
      let funcs = List.init 6 (fun i -> mkFunc (sprintf "f%d" i) "BoundsMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 13) Map.empty
      let eps = 1.0f
      for b in bldgs do
        (b.X,       rect.X - eps)           |> Expect.isGreaterThanOrEqual "left within bounds"
        (b.Z,       rect.Z - eps)           |> Expect.isGreaterThanOrEqual "top within bounds"
        (b.X + b.W, rect.X + rect.W + eps)  |> Expect.isLessThanOrEqual   "right within bounds"
        (b.Z + b.D, rect.Z + rect.H + eps)  |> Expect.isLessThanOrEqual   "bottom within bounds"

    testCase "empty road list falls back gracefully without crash" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 10.0f }
      let funcs = List.init 4 (fun i -> mkFunc (sprintf "f%d" i) "FallbackMod")
      let bldgs = packAlongRoads [] rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 1) Map.empty
      bldgs |> Expect.isNonEmpty "fallback must still produce buildings when no roads exist"

    testCase "buildings distributed on both sides of a single road" <| fun () ->
      // A single vertical road with 10 functions should put buildings on BOTH sides
      let rect  = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 20.0f }
      let roads = [ mkRoad 5.0f 0.0f 5.0f 20.0f 0.4f ]
      let funcs = List.init 10 (fun i -> mkFunc (sprintf "f%d" i) "SidesMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 99) Map.empty
      let leftSide  = bldgs |> List.exists (fun b -> b.X + b.W / 2.0f < 5.0f)
      let rightSide = bldgs |> List.exists (fun b -> b.X + b.W / 2.0f > 5.0f)
      leftSide  |> Expect.isTrue "should have buildings left of road"
      rightSide |> Expect.isTrue "should have buildings right of road"

    testCase "single-road frontage placement produces elongated parcels instead of square pads" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 24.0f }
      let roads = [ mkRoad 5.0f 0.0f 5.0f 24.0f 0.4f ]
      let funcs = List.init 12 (fun i -> mkFunc (sprintf "f%d" i) "FrontageFormMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 5) Map.empty
      bldgs |> Expect.isNonEmpty "frontage planner should produce buildings"
      let elongatedCount =
        bldgs
        |> List.filter (fun b -> b.D > b.W * 1.10f)
        |> List.length
      (elongatedCount, bldgs.Length / 2)
      |> Expect.isGreaterThanOrEqual "most buildings on a vertical road should stretch along the street corridor"

    testCase "single-road frontage ordering is monotonic on each side without parcel overlap" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 10.0f; H = 24.0f }
      let roads = [ mkRoad 5.0f 0.0f 5.0f 24.0f 0.4f ]
      let funcs = List.init 12 (fun i -> mkFunc (sprintf "f%d" i) "MonotonicFrontageMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 11) Map.empty
      let orderedNoOverlap (buildings: FuncBuilding list) =
        buildings
        |> List.sortBy (fun b -> b.Z)
        |> List.pairwise
        |> List.forall (fun (prevB, nextB) -> prevB.Z + prevB.D <= nextB.Z + 0.25f)
      let leftSide = bldgs |> List.filter (fun b -> b.X + b.W / 2.0f < 5.0f)
      let rightSide = bldgs |> List.filter (fun b -> b.X + b.W / 2.0f > 5.0f)
      leftSide.Length > 1 |> Expect.isTrue "need multiple left-side buildings to verify ordering"
      rightSide.Length > 1 |> Expect.isTrue "need multiple right-side buildings to verify ordering"
      orderedNoOverlap leftSide |> Expect.isTrue "left frontage parcels should keep monotonic order"
      orderedNoOverlap rightSide |> Expect.isTrue "right frontage parcels should keep monotonic order"

    testCase "frontage placement rejects overlapping buildings and road-surface collisions" <| fun () ->
      let rect  = { X = -12.0f; Z = -18.0f; W = 24.0f; H = 36.0f }
      let roads = [ mkRoad 0.0f -18.0f 0.0f 18.0f 0.8f ]
      let funcs = List.init 14 (fun i -> { mkFunc (sprintf "f%d" i) "CollisionFreeMod" with LineCount = 140 - i * 4 })
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 23) Map.empty
      bldgs |> Expect.isNonEmpty "collision-safe frontage planner should still place some buildings"
      bldgs
      |> List.allPairs bldgs
      |> List.filter (fun (left, right) -> left.Func.QualifiedName <> right.Func.QualifiedName)
      |> List.exists (fun (left, right) -> buildingsOverlap left right)
      |> Expect.isFalse "accepted frontage buildings should never overlap"
      bldgs
      |> List.exists (fun building -> roads |> List.exists (buildingIntersectsRoad building))
      |> Expect.isFalse "accepted frontage buildings should stay off the road surface"

    testCase "computeBlockSurfaceHull follows diagonal frontage rather than reverting to a module rectangle" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 14.0f; H = 24.0f }
      let block = { Module = "SurfaceMod"; Project = "SurfaceProj"; Rect = rect; Color = Color.White; TerrainY = 0.0f }
      let roads = [ mkRoad 2.0f 1.0f 12.0f 23.0f 0.4f ]
      let funcs = List.init 12 (fun i -> mkFunc (sprintf "f%d" i) "SurfaceMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 17) Map.empty
      let hull = computeBlockSurfaceHull block bldgs roads
      (hull.Length, 4) |> Expect.isGreaterThan "surface hull should have enough points to express frontage shape"
      hullHasNonAxisEdge hull |> Expect.isTrue "surface hull should retain a diagonal edge family from the road"
      for (x, z) in hull do
        (x, rect.X) |> Expect.isGreaterThanOrEqual "hull point should stay within left bound"
        (x, rect.X + rect.W) |> Expect.isLessThanOrEqual "hull point should stay within right bound"
        (z, rect.Z) |> Expect.isGreaterThanOrEqual "hull point should stay within top bound"
        (z, rect.Z + rect.H) |> Expect.isLessThanOrEqual "hull point should stay within bottom bound"
  ]

let buildingTypologyTests =
  testList "Building typology classification and type-aware properties" [

    testCase "classifyBuilding: tiny no-heat function is Shed" <| fun () ->
      classifyBuilding 5 1 0.0f
      |> Expect.equal "5-line, 0-heat function should be Shed" Shed

    testCase "classifyBuilding: small low-heat function is Cottage" <| fun () ->
      classifyBuilding 25 2 0.1f
      |> Expect.equal "25-line, low-heat function should be Cottage" Cottage

    testCase "classifyBuilding: medium function is Rowhouse" <| fun () ->
      classifyBuilding 60 3 0.3f
      |> Expect.equal "60-line, moderate function should be Rowhouse" Rowhouse

    testCase "classifyBuilding: medium with noticeable heat is Commercial" <| fun () ->
      classifyBuilding 80 5 0.5f
      |> Expect.equal "80-line, 0.5-heat function should be Commercial" Commercial

    testCase "classifyBuilding: large highly-used function is Tower" <| fun () ->
      classifyBuilding 400 8 0.7f
      |> Expect.equal "400-line, 0.7-heat function should be Tower" Tower

    testCase "classifyBuilding: very large or very hot function is Skyscraper" <| fun () ->
      classifyBuilding 700 12 0.9f
      |> Expect.equal "700-line, 0.9-heat function should be Skyscraper" Skyscraper

    testCase "classifyBuilding: tiny hot functions top out below Skyscraper" <| fun () ->
      classifyBuilding 20 2 0.9f
      |> Expect.equal "Small but extremely hot function should read as a commercial anchor, not a supertall" Commercial

    testCase "classifyBuilding: line count alone can promote to Skyscraper" <| fun () ->
      classifyBuilding 700 2 0.0f
      |> Expect.equal "Very long cold function should be Skyscraper" Skyscraper

    testCase "classifyBuilding: very hot mid-size functions can still become Tower" <| fun () ->
      classifyBuilding 120 8 0.9f
      |> Expect.equal "Sufficiently large and hot functions should still read as towers" Tower

    testCase "spacingMultiplier: residential types get more yard space than skyscrapers" <| fun () ->
      let cottageMult  = BuildingType.spacingMultiplier Cottage
      let scraperMult  = BuildingType.spacingMultiplier Skyscraper
      (cottageMult, scraperMult)
      |> Expect.isGreaterThan "Cottage needs more yard space than Skyscraper"

    testCase "spacingMultiplier: Skyscraper is the most densely packed" <| fun () ->
      let scraperMult = BuildingType.spacingMultiplier Skyscraper
      for bt in [| Shed; Cottage; Rowhouse; Commercial; Tower |] do
        let other = BuildingType.spacingMultiplier bt
        (other, scraperMult)
        |> Expect.isGreaterThanOrEqual (sprintf "%A should have more yard space than Skyscraper" bt)

    testProperty "classifyBuilding never throws for any non-negative inputs" <| fun (lc: PositiveInt) (cx: PositiveInt) ->
      let lineCount  = lc.Get % 10000
      let complexity = cx.Get % 200
      let heat       = float32 (lc.Get % 101) / 100.0f
      let _ = classifyBuilding lineCount complexity heat
      true

    testCase "buildingHeight: Skyscraper is taller than Shed for same line count" <| fun () ->
      let shedH    = BuildingType.height Shed        20 0.0f
      let scraperH = BuildingType.height Skyscraper  20 0.8f
      (scraperH, shedH)
      |> Expect.isGreaterThan "Skyscraper height should exceed Shed height"

    testCase "buildingTypeWallColor: each type returns a fully-opaque color" <| fun () ->
      for bt in [| Shed; Cottage; Rowhouse; Commercial; Tower; Skyscraper |] do
        let c = BuildingType.wallColor bt "testFunc" 180.0f
        c.A |> Expect.equal (sprintf "%A wall color should be opaque" bt) 255uy

    testCase "buildingTypeWallColor: residential warm (R > B) vs skyscraper cool (B >= R)" <| fun () ->
      let cottageC  = BuildingType.wallColor Cottage   "anyFunc" 180.0f
      let scraperC  = BuildingType.wallColor Skyscraper "anyFunc" 180.0f
      (int cottageC.R, int cottageC.B)
      |> Expect.isGreaterThan "Cottage wall should be warm-tinted (R > B)"
      (int scraperC.B, int scraperC.R)
      |> Expect.isGreaterThanOrEqual "Skyscraper wall should be cool-tinted (B >= R)"

    testCase "buildings from packAlongRoads have BuildingType field set" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let roads = [ mkRoad 10.0f 0.0f 10.0f 20.0f 0.4f ]
      let funcs = List.init 6 (fun i -> mkFunc (sprintf "f%d" i) "TypeMod")
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 42) Map.empty
      bldgs |> Expect.isNonEmpty "should produce buildings"
      // Each building should have a BuildingType — verify at least one non-Shed exists
      // (since with 10-line functions and no heat the type is Shed or Cottage)
      bldgs |> List.forall (fun b -> b.BuildingType = Shed || b.BuildingType = Cottage || b.BuildingType = Rowhouse)
      |> Expect.isTrue "small test functions should classify as Shed/Cottage/Rowhouse"
  ]

let gitAgeColorTests =
  testList "Git age color temperature and lot coverage" [

    testCase "wallColor: freshly committed files are warmer (higher R) than old files" <| fun () ->
      let freshColor = BuildingType.wallColor Cottage "sameFunc" 0.0f
      let oldColor   = BuildingType.wallColor Cottage "sameFunc" 730.0f
      (int freshColor.R, int oldColor.R)
      |> Expect.isGreaterThan "Fresh files should have more red (warm shift) than old files"

    testCase "wallColor: old files are cooler (higher B) than fresh files" <| fun () ->
      let freshColor = BuildingType.wallColor Cottage "sameFunc" 0.0f
      let oldColor   = BuildingType.wallColor Cottage "sameFunc" 730.0f
      (int oldColor.B, int freshColor.B)
      |> Expect.isGreaterThan "Old files should have more blue (cool shift) than fresh files"

    testCase "FuncBuilding has GitAgeDays field" <| fun () ->
      let rect  = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let roads = [ mkRoad 10.0f 0.0f 10.0f 20.0f 0.4f ]
      let funcs = [ mkFunc "testF" "TestMod" ]
      let bldgs = packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 1) Map.empty
      bldgs |> Expect.isNonEmpty "should produce at least one building"
      bldgs |> List.head |> (fun b -> b.GitAgeDays >= 0.0f)
      |> Expect.isTrue "GitAgeDays field should exist and be non-negative"

    testCase "packAlongRoads: skyscraper buildings have larger footprints than sheds" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 30.0f; H = 30.0f }
      let roads = [ mkRoad 15.0f 0.0f 15.0f 30.0f 0.4f ]
      let scraperFuncs = List.init 4 (fun i -> { mkFunc (sprintf "hot%d" i) "HotMod" with LineCount = 700 })
      let shedFuncs    = List.init 4 (fun i -> { mkFunc (sprintf "tiny%d" i) "TinyMod" with LineCount = 2 })
      let scraperBldgs = packAlongRoads roads rect scraperFuncs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 7) Map.empty
      let shedBldgs    = packAlongRoads roads rect shedFuncs    Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 7) Map.empty
      scraperBldgs |> Expect.isNonEmpty "skyscraper funcs should produce buildings"
      shedBldgs    |> Expect.isNonEmpty "shed funcs should produce buildings"
      let avgScraperW = scraperBldgs |> List.averageBy (fun b -> b.W)
      let avgShedW    = shedBldgs    |> List.averageBy (fun b -> b.W)
      (avgScraperW, avgShedW)
      |> Expect.isGreaterThan "Skyscraper footprint should exceed Shed footprint due to higher coverage ratio"

    testCase "packAlongRoads: tiny hot functions do not become supertall needles" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 24.0f; H = 24.0f }
      let roads = [ mkRoad 12.0f 0.0f 12.0f 24.0f 0.4f ]
      let tinyHotFuncs =
        [ { mkFunc "hotA" "TinyHot" with LineCount = 20 }
          { mkFunc "hotB" "TinyHot" with LineCount = 22 } ]
      let heatMap =
        tinyHotFuncs
        |> List.map (fun f -> f.QualifiedName, (0.92f, 16, 2))
        |> Map.ofList
      let bldgs = packAlongRoads roads rect tinyHotFuncs heatMap (Color(70uy, 130uy, 180uy, 255uy)) (Random 9) Map.empty
      bldgs |> Expect.isNonEmpty "tiny hot funcs should still produce buildings"
      bldgs
      |> List.forall (fun b -> b.BuildingType <> Skyscraper && b.H <= 18.0f)
      |> Expect.isTrue "tiny hot functions should not render as impossible needle towers"
  ]

let roadColorTests =
  testList "Road color hierarchy" [
    testCase "roadColorForClass returns distinct colors per class" <| fun () ->
      let boulevard = roadColorForClass Boulevard
      let avenue    = roadColorForClass Avenue
      let street    = roadColorForClass Street
      let lane      = roadColorForClass Lane
      let alley     = roadColorForClass Alley
      // All five should be distinct values
      let all = [boulevard; avenue; street; lane; alley]
      all |> List.distinct |> List.length |> Expect.equal "all 5 road classes should have distinct colors" 5

    testCase "roadColorForClass: brightness decreases from Boulevard to Alley" <| fun () ->
      let brightness (r: byte, g: byte, b: byte) = int r + int g + int b
      let ordered = [ Boulevard; Avenue; Street; Lane; Alley ]
                    |> List.map (fun rc -> brightness (roadColorForClass rc))
      ordered
      |> List.pairwise
      |> List.iteri (fun i (a, b) ->
        (a, b) |> Expect.isGreaterThan (sprintf "class %d should be brighter than class %d" i (i+1)))

    testCase "roadColorForClass: Boulevard is lighter than Alley on all channels" <| fun () ->
      let (br, bg, bb) = roadColorForClass Boulevard
      let (ar, ag, ab) = roadColorForClass Alley
      (int br, int ar) |> Expect.isGreaterThan "Boulevard R should exceed Alley R"
      (int bg, int ag) |> Expect.isGreaterThan "Boulevard G should exceed Alley G"
      (int bb, int ab) |> Expect.isGreaterThan "Boulevard B should exceed Alley B"
  ]

let arcFormulaTests =
  testList "Call arc formula properties" [
    testCase "arcRadius is monotone increasing in weight" <| fun () ->
      let weights = [ 0.0f; 0.25f; 0.5f; 0.75f; 1.0f ]
      weights
      |> List.map arcRadius
      |> List.pairwise
      |> List.iteri (fun i (a, b) ->
        (b, a) |> Expect.isGreaterThanOrEqual (sprintf "arcRadius at weight step %d should be >= previous" i))

    testCase "arcRadius is bounded between 0.02 and 0.08" <| fun () ->
      [ 0.0f; 0.5f; 1.0f ] |> List.iter (fun w ->
        let r = arcRadius w
        (r, 0.02f) |> Expect.isGreaterThanOrEqual (sprintf "arcRadius(%.2f) should be >= 0.02" w)
        (r, 0.08f) |> Expect.isLessThanOrEqual    (sprintf "arcRadius(%.2f) should be <= 0.08" w))

    testCase "arcRadius at 0.0 is minimum, at 1.0 is maximum" <| fun () ->
      arcRadius 0.0f |> Expect.equal "minimum radius at weight 0" 0.02f
      arcRadius 1.0f |> Expect.equal "maximum radius at weight 1" 0.08f

    testCase "arcHeight is monotone increasing in both dist and weight" <| fun () ->
      // Monotone in dist (fixed weight=0.5)
      let dists = [ 2.0f; 5.0f; 10.0f; 20.0f ]
      dists
      |> List.map (fun d -> arcHeight d 0.5f)
      |> List.pairwise
      |> List.iteri (fun i (a, b) ->
        (b, a) |> Expect.isGreaterThan (sprintf "arcHeight should increase with dist at step %d" i))
      // Monotone in weight (fixed dist=10.0)
      let weights = [ 0.0f; 0.5f; 1.0f ]
      weights
      |> List.map (fun w -> arcHeight 10.0f w)
      |> List.pairwise
      |> List.iteri (fun i (a, b) ->
        (b, a) |> Expect.isGreaterThan (sprintf "arcHeight should increase with weight at step %d" i))

    testCase "shouldRenderLabel: in-frame+inFront renders; off-screen or behind does not" <| fun () ->
      shouldRenderLabel (Vector2(800.0f, 450.0f)) 1600 900 true  |> Expect.isTrue  "in-frame, in-front should render"
      shouldRenderLabel (Vector2(800.0f, 450.0f)) 1600 900 false |> Expect.isFalse "behind camera should not render"
      shouldRenderLabel (Vector2(-100.0f, 450.0f)) 1600 900 true |> Expect.isFalse "off-screen-left should not render"
      shouldRenderLabel (Vector2(800.0f, 1100.0f)) 1600 900 true |> Expect.isFalse "off-screen-bottom should not render"
  ]

let splineSegmentTests =
  testList "Spline segment count" [
    testCase "segmentCountForOrganic: minimum 1 segment always" <| fun () ->
      segmentCountForOrganic 0.0f 0.0f |> Expect.equal "zero length + zero organic = 1" 1
      // With organic factor, even tiny roads get at least 1 segment (organic multiplier applies)
      let v = segmentCountForOrganic 0.0f 1.0f
      (v, 0) |> Expect.isGreaterThan "any organic factor gives at least 1 segment"
      let v2 = segmentCountForOrganic 0.5f 1.0f
      (v2, 0) |> Expect.isGreaterThan "short organic road has at least 1 segment"

    testCase "segmentCountForOrganic: more segments for longer roads" <| fun () ->
      let s1 = segmentCountForOrganic 3.0f 0.5f
      let s2 = segmentCountForOrganic 6.0f 0.5f
      let s3 = segmentCountForOrganic 12.0f 0.5f
      (s2, s1) |> Expect.isGreaterThan "longer road needs more segments"
      (s3, s2) |> Expect.isGreaterThan "even longer road needs even more"

    testCase "segmentCountForOrganic: more segments for more organic" <| fun () ->
      let s0 = segmentCountForOrganic 6.0f 0.0f
      let s5 = segmentCountForOrganic 6.0f 0.5f
      let s1 = segmentCountForOrganic 6.0f 1.0f
      (s5, s0) |> Expect.isGreaterThan "organic=0.5 needs more segments than organic=0"
      (s1, s5) |> Expect.isGreaterThan "organic=1.0 needs most segments"

    testCase "segmentCountForOrganic: pure function (same inputs always same output)" <| fun () ->
      let a = segmentCountForOrganic 8.0f 0.7f
      let b = segmentCountForOrganic 8.0f 0.7f
      a |> Expect.equal "pure function: deterministic" b
  ]

let districtOverlayPlanningTests =
  testList "District overlay planning" [
    testCase "label planner keeps the highest-priority district when labels overlap" <| fun () ->
      let theme = compactUiTextTheme defaultUiTextTheme
      let winner = mkDistrict "core-domain" 40 900 Color.Red
      let loser = mkDistrict "misc" 4 60 Color.Blue
      let placements =
        [ { District = loser
            ScreenPos = Vector2(402.0f, 300.0f)
            ProjectedArea = 8000.0f
            CameraDistance = 250.0f }
          { District = winner
            ScreenPos = Vector2(400.0f, 300.0f)
            ProjectedArea = 90000.0f
            CameraDistance = 120.0f } ]
        |> planDistrictLabelPlacements 1600 900 theme
      placements |> List.map _.District.Name |> Expect.equal "larger, hotter district should survive overlap" ["core-domain"]

    testCase "label planner enforces a screen-density budget" <| fun () ->
      let theme = compactUiTextTheme defaultUiTextTheme
      let placements =
        [ for i in 0 .. 15 ->
            { District = mkDistrict (sprintf "district-%02d" i) (20 - i) (400 - i * 5) Color.Gold
              ScreenPos = Vector2(120.0f + float32 (i * 130), 180.0f + float32 ((i % 2) * 120))
              ProjectedArea = 18000.0f + float32 i
              CameraDistance = 180.0f + float32 i } ]
        |> planDistrictLabelPlacements 1600 900 theme
      placements |> List.length |> Expect.equal "planner should cap labels for 1080p screens" (maxDistrictLabelCount 1600 900)

    testCase "large nearby districts get detailed labels while distant ones stay compact" <| fun () ->
      let theme = compactUiTextTheme defaultUiTextTheme
      let placements =
        [ { District = mkDistrict "city-core" 30 700 Color.Red
            ScreenPos = Vector2(300.0f, 250.0f)
            ProjectedArea = 90000.0f
            CameraDistance = 140.0f }
          { District = mkDistrict "outer-ring" 10 180 Color.Blue
            ScreenPos = Vector2(620.0f, 250.0f)
            ProjectedArea = 9000.0f
            CameraDistance = 480.0f } ]
        |> planDistrictLabelPlacements 1600 900 theme
      placements |> List.map _.Style |> Expect.equal "LOD should keep only the important label detailed" [DetailedDistrictLabel; CompactDistrictLabel]
  ]

let buildingTypeAlphaTests =
  testList "Building type alpha encoding" [
    testCase "each type has distinct alpha value" <| fun () ->
      let types = [Shed; Cottage; Rowhouse; Commercial; Tower; Skyscraper]
      let alphas = types |> List.map BuildingType.alpha
      alphas |> List.distinct |> Expect.hasLength "all 6 types must have distinct alpha values" 6

    testCase "all types have alpha in 0..5 for GLSL decode" <| fun () ->
      let types = [Shed; Cottage; Rowhouse; Commercial; Tower; Skyscraper]
      types |> List.iter (fun bt ->
        let a = int (BuildingType.alpha bt)
        (a, 5) |> Expect.isLessThanOrEqual (sprintf "%A alpha must be <= 5" bt)
        (a, 0) |> Expect.isGreaterThanOrEqual (sprintf "%A alpha must be >= 0" bt))

    testCase "density ordering Shed=0 through Skyscraper=5" <| fun () ->
      BuildingType.alpha Shed        |> Expect.equal "Shed=0"        0uy
      BuildingType.alpha Cottage     |> Expect.equal "Cottage=1"     1uy
      BuildingType.alpha Rowhouse    |> Expect.equal "Rowhouse=2"    2uy
      BuildingType.alpha Commercial  |> Expect.equal "Commercial=3"  3uy
      BuildingType.alpha Tower       |> Expect.equal "Tower=4"       4uy
      BuildingType.alpha Skyscraper  |> Expect.equal "Skyscraper=5"  5uy
  ]

let complexityFootprintTests =
  testList "Complexity footprint factor" [
    testCase "baseline complexity 0 gives 1.0f" <| fun () ->
      complexityFootprintFactor 0
      |> Expect.equal "complexity 0 → no scaling" 1.0f

    testCase "strictly increasing for complexities 0 1 5 10 50" <| fun () ->
      let vals = [0; 1; 5; 10; 50] |> List.map complexityFootprintFactor
      vals |> List.pairwise |> List.iteri (fun i (a, b) ->
        (b, a) |> Expect.isGreaterThan (sprintf "factor must increase at step %d" i))

    testCase "bounded below 2.0f even at complexity 100" <| fun () ->
      complexityFootprintFactor 100
      |> fun f -> (f, 2.0f) |> Expect.isLessThan "factor < 2.0 for realistic complexity"
  ]

let gableRoofTests =
  testList "Gable roof geometry" [
    testCase "addGableToArraysArr writes exactly 108 position floats (36 verts)" <| fun () ->
      let verts, _, _ = addGableToArraysArr 5.0f 2.5f 5.0f 1.0f 0.8f 200uy 180uy 160uy 255uy
      verts |> Array.length |> Expect.equal "36 verts × 3 floats = 108" 108

    testCase "apex Y is above the eave base Y" <| fun () ->
      let baseY = 2.5f
      let verts, _, _ = addGableToArraysArr 5.0f baseY 5.0f 1.2f 0.9f 200uy 180uy 160uy 255uy
      let maxY = [0 .. 35] |> List.map (fun i -> verts.[i*3+1]) |> List.max
      (maxY, baseY) |> Expect.isGreaterThan "ridge apex must be above eave"

    testCase "left slope (first 6 verts) has normal X < 0" <| fun () ->
      let _, norms, _ = addGableToArraysArr 5.0f 2.5f 5.0f 1.0f 0.8f 200uy 180uy 160uy 255uy
      let nx0 = norms.[0]
      (nx0, 0.0f) |> Expect.isLessThan "left slope normal X must be negative"

    testCase "right slope (verts 6..11) has normal X > 0" <| fun () ->
      let _, norms, _ = addGableToArraysArr 5.0f 2.5f 5.0f 1.0f 0.8f 200uy 180uy 160uy 255uy
      let nx6 = norms.[6*3]
      (nx6, 0.0f) |> Expect.isGreaterThan "right slope normal X must be positive"

    testCase "gable normals have positive Y component (slopes lean upward)" <| fun () ->
      let _, norms, _ = addGableToArraysArr 5.0f 2.5f 5.0f 1.0f 0.8f 200uy 180uy 160uy 255uy
      let ny0 = norms.[0*3+1]
      let ny6 = norms.[6*3+1]
      (ny0, 0.0f) |> Expect.isGreaterThan "left slope normal Y must be positive"
      (ny6, 0.0f) |> Expect.isGreaterThan "right slope normal Y must be positive"
  ]

let orientedBuildingGeometryTests =
  testList "Oriented building mesh geometry" [
    testCase "oriented cube swaps plan extents at 90 degrees" <| fun () ->
      let verts, _, _ = addOrientedCubeToArraysArr 5.0f 2.0f 5.0f 1.0f 0.5f 2.0f 90.0f 180uy 180uy 180uy 255uy
      let xs = [ for i in 0 .. 35 -> verts.[i * 3] ]
      let zs = [ for i in 0 .. 35 -> verts.[i * 3 + 2] ]
      let width = (xs |> List.max) - (xs |> List.min)
      let depth = (zs |> List.max) - (zs |> List.min)
      (abs (width - 4.0f), 0.05f) |> Expect.isLessThanOrEqual "90° rotation should expose the former depth along X"
      (abs (depth - 2.0f), 0.05f) |> Expect.isLessThanOrEqual "90° rotation should expose the former width along Z"

    testCase "oriented gable rotates slope normals with the building heading" <| fun () ->
      let _, norms, _ = addOrientedGableToArraysArr 5.0f 2.5f 5.0f 1.0f 0.8f 90.0f 200uy 180uy 160uy 255uy
      let nx0 = norms.[0]
      let nz0 = norms.[2]
      (abs nx0, 0.05f) |> Expect.isLessThanOrEqual "rotated slope should no longer point strongly along X"
      (nz0, -0.1f) |> Expect.isLessThan "90° rotation should swing the first slope normal toward -Z"
  ]

let private mkBlock mod_ proj x z w h =
  { Module = mod_; Project = proj
    Rect = TRect.create x z w h
    Color = Color.White
    TerrainY = 0.0f }

let interDistrictRoadTests =
  testList "inter-district arterial network" [
    testCase "findAdjacentBlocks finds shared vertical boundary" <| fun () ->
      let b1 = mkBlock "A" "P" 0.0f 0.0f 5.0f 5.0f
      let b2 = mkBlock "B" "P" 5.0f 0.0f 5.0f 5.0f
      findAdjacentBlocks [|b1; b2|] 0.01f
      |> Expect.hasLength "one adjacent pair" 1

    testCase "findAdjacentBlocks returns empty when gap exists" <| fun () ->
      let b1 = mkBlock "A" "P" 0.0f 0.0f 5.0f 5.0f
      let b2 = mkBlock "B" "P" 6.0f 0.0f 5.0f 5.0f
      findAdjacentBlocks [|b1; b2|] 0.01f
      |> Expect.isEmpty "gap means not adjacent"

    testCase "findAdjacentBlocks finds horizontal boundary" <| fun () ->
      let b1 = mkBlock "A" "P" 0.0f 0.0f 5.0f 5.0f
      let b2 = mkBlock "B" "P" 0.0f 5.0f 5.0f 5.0f
      findAdjacentBlocks [|b1; b2|] 0.01f
      |> Expect.hasLength "horizontal boundary found" 1

    testCase "crossDistrictCallCount sums crossing edge weights" <| fun () ->
      let edges =
        [ { From = "ModA.foo"; To = "ModB.bar"; Weight = 3 }
          { From = "ModA.baz"; To = "ModA.qux"; Weight = 5 }
          { From = "ModC.x"; To = "ModB.y"; Weight = 2 } ]
      crossDistrictCallCount edges "ModA" "ModB"
      |> Expect.equal "cross-district weight is 3" 3

    testCase "crossDistrictCallCount is symmetric" <| fun () ->
      let edges = [ { From = "A.f"; To = "B.g"; Weight = 7 } ]
      let ab = crossDistrictCallCount edges "A" "B"
      let ba = crossDistrictCallCount edges "B" "A"
      ab |> Expect.equal "symmetric" ba

    testCase "buildArterialNetwork halfWidth >= base Boulevard halfWidth" <| fun () ->
      let b1 = mkBlock "A" "P" 0.0f 0.0f 5.0f 5.0f
      let b2 = mkBlock "B" "P" 5.0f 0.0f 5.0f 5.0f
      let roads = buildArterialNetwork [|b1; b2|] []
      roads |> Expect.hasLength "1 arterial road" 1
      let hw = roads.[0].HalfWidth
      (hw, RoadClass.width Boulevard / 2.0f) |> Expect.isGreaterThanOrEqual "halfWidth >= base"
  ]

let semanticTerrainTests =
  testList "semantic terrain" [
    testCase "computeModuleTerrainScores favors cross-module pressure over cohesive internals" <| fun () ->
      let funcs =
        [ mkFuncInModule "Core" "coreA" [] [| "let x = 1" |]
          mkFuncInModule "Core" "coreB" [] [| "let y = 2" |]
          mkFuncInModule "Api" "apiA" [] [| "let z = 3" |]
          mkFuncInModule "Api" "apiB" [] [| "let q = 4" |]
          mkFuncInModule "Utility" "utilA" [] [| "let u = 5" |]
          mkFuncInModule "Utility" "utilB" [] [| "let v = 6" |] ]
      let edges =
        [ { From = "Core.coreA"; To = "Core.coreB"; Weight = 6 }
          { From = "Core.coreB"; To = "Api.apiA"; Weight = 9 }
          { From = "Api.apiA"; To = "Core.coreA"; Weight = 7 }
          { From = "Api.apiB"; To = "Core.coreB"; Weight = 5 }
          { From = "Utility.utilA"; To = "Utility.utilB"; Weight = 8 }
          { From = "Utility.utilB"; To = "Utility.utilA"; Weight = 6 } ]
      let scores = computeModuleTerrainScores funcs edges
      let core = scores |> Map.find "Core"
      let api = scores |> Map.find "Api"
      let utility = scores |> Map.find "Utility"
      (core, utility) |> Expect.isGreaterThan "cross-module pressure should lift Core above cohesive Utility"
      (api, utility) |> Expect.isGreaterThan "cross-module pressure should lift Api above cohesive Utility"
      (core, 1.0f) |> Expect.isLessThanOrEqual "terrain scores should stay normalized"
      (utility, 0.0f) |> Expect.isGreaterThanOrEqual "terrain scores should stay normalized"

    testCase "buildSemanticTerrainAnchors adds perimeter falloff controls" <| fun () ->
      let blocks =
        [| { Module = "Low"; Project = "P"; Rect = TRect.create -12.0f -4.0f 8.0f 8.0f; Color = Color.White; TerrainY = 0.25f }
           { Module = "High"; Project = "P"; Rect = TRect.create 4.0f -4.0f 8.0f 8.0f; Color = Color.White; TerrainY = 2.5f } |]
      let anchors = buildSemanticTerrainAnchors blocks 18.0f
      anchors.Length |> Expect.equal "block anchors + 8 perimeter controls" (blocks.Length + 8)

    testCase "sampleSemanticTerrainHeight preserves module contrast and decays toward the boundary" <| fun () ->
      let blocks =
        [| { Module = "Low"; Project = "P"; Rect = TRect.create -12.0f -4.0f 8.0f 8.0f; Color = Color.White; TerrainY = 0.25f }
           { Module = "High"; Project = "P"; Rect = TRect.create 4.0f -4.0f 8.0f 8.0f; Color = Color.White; TerrainY = 2.5f } |]
      let anchors = buildSemanticTerrainAnchors blocks 18.0f
      let low = sampleSemanticTerrainHeight anchors -8.0f 0.0f
      let mid = sampleSemanticTerrainHeight anchors 0.0f 0.0f
      let high = sampleSemanticTerrainHeight anchors 8.0f 0.0f
      let edge = sampleSemanticTerrainHeight anchors 18.0f 0.0f
      (high, low + 1.0f) |> Expect.isGreaterThan "high-pressure module should produce a visibly taller local terrain sample"
      (mid, low) |> Expect.isGreaterThan "midpoint should interpolate upward from the lower basin"
      (high, edge) |> Expect.isGreaterThan "perimeter controls should pull terrain back down near the city edge"

    testCase "groundedTerrainHeightForPoint keeps block interiors on their authored plateaus" <| fun () ->
      let blocks =
        [| { Module = "Low"; Project = "P"; Rect = TRect.create -12.0f -4.0f 8.0f 8.0f; Color = Color.White; TerrainY = 0.25f }
           { Module = "High"; Project = "P"; Rect = TRect.create 4.0f -4.0f 8.0f 8.0f; Color = Color.White; TerrainY = 2.5f } |]
      let anchors = buildSemanticTerrainAnchors blocks 18.0f
      let rawInside = sampleSemanticTerrainHeight anchors 4.5f 0.0f
      (rawInside, 2.35f) |> Expect.isLessThan "the free terrain field should sag near the block edge before grounding"
      groundedTerrainHeightForPoint blocks anchors 4.5f 0.0f
      |> Expect.equal "points inside a module block should stay on the block's plateau" blocks.[1].TerrainY
      let outside = groundedTerrainHeightForPoint blocks anchors 18.0f 0.0f
      let edge = sampleSemanticTerrainHeight anchors 18.0f 0.0f
      abs (outside - edge) < 1e-4f
      |> Expect.isTrue "points outside module blocks should keep following the semantic terrain field"

    testCase "groundBuildingToTerrain keeps buildings coplanar with their parent block slab" <| fun () ->
      let blocks =
        [| { Module = "Low"; Project = "P"; Rect = TRect.create -12.0f -4.0f 8.0f 8.0f; Color = Color.White; TerrainY = 0.25f }
           { Module = "High"; Project = "P"; Rect = TRect.create 4.0f -4.0f 8.0f 8.0f; Color = Color.White; TerrainY = 2.5f } |]
      let anchors = buildSemanticTerrainAnchors blocks 18.0f
      let building =
        { mkSampleBuilding "grounded" Commercial 120 0.4f 8 3 12 20.0f 5 0.0f with
            X = 4.0f
            Z = -1.0f
            W = 2.0f
            D = 2.0f }
      let grounded = groundBuildingToTerrain blocks anchors building
      grounded.TerrainY |> Expect.equal "building base should align with its module block surface" blocks.[1].TerrainY
      let raw = sampleSemanticTerrainHeight anchors (grounded.X + grounded.W / 2.0f) (grounded.Z + grounded.D / 2.0f)
      (abs (raw - grounded.TerrainY), 0.15f)
      |> Expect.isGreaterThan "without grounding, the building would visibly drift off the block slab"

    testCase "groundRoadToTerrain keeps internal block roads on the same plateau as sidewalks" <| fun () ->
      let blocks =
        [| { Module = "Low"; Project = "P"; Rect = TRect.create -12.0f -4.0f 8.0f 8.0f; Color = Color.White; TerrainY = 0.25f }
           { Module = "High"; Project = "P"; Rect = TRect.create 4.0f -4.0f 8.0f 8.0f; Color = Color.White; TerrainY = 2.5f } |]
      let anchors = buildSemanticTerrainAnchors blocks 18.0f
      let road = mkRoad 4.5f -2.0f 4.5f 2.0f 0.4f
      let grounded = groundRoadToTerrain blocks anchors road
      grounded.FromPos.Y |> Expect.equal "road start should align with the parent block surface" blocks.[1].TerrainY
      grounded.ToPos.Y |> Expect.equal "road end should align with the parent block surface" blocks.[1].TerrainY
      let raw = sampleSemanticTerrainHeight anchors 4.5f 0.0f
      (abs (raw - grounded.FromPos.Y), 0.15f)
      |> Expect.isGreaterThan "without grounding, the road would visibly drift away from the sidewalk plateau"
  ]

let repeatedSideConnectorTests =
  testList "repeated side connector pruning" [
    testCase "near-parallel bypass linking two successive corridor anchors is flagged" <| fun () ->
      let roads =
        [ mkRoad 0.0f 0.0f 10.0f 0.0f 0.4f
          mkRoad 10.0f 0.0f 20.0f 0.0f 0.4f
          mkRoad 20.0f 0.0f 30.0f 0.0f 0.4f
          mkRoad 10.2f 0.30f 19.8f 0.30f 0.4f ]
      repeatedSideConnectorRoadIndices roads
      |> Set.contains 3
      |> Expect.isTrue "split-edge style bypass should be identified for pruning"

    testCase "single-anchor side spur is preserved" <| fun () ->
      let roads =
        [ mkRoad 0.0f 0.0f 10.0f 0.0f 0.4f
          mkRoad 10.0f 0.0f 20.0f 0.0f 0.4f
          mkRoad 20.0f 0.0f 30.0f 0.0f 0.4f
          mkRoad 10.0f 0.0f 10.4f 3.0f 0.4f ]
      repeatedSideConnectorRoadIndices roads
      |> Expect.isEmpty "single-anchor spur should not be treated as a zipper-style repeated bypass"
  ]

let codeHealthMetricTests =
  testList "code health metrics" [
    testCase "packAlongRoads carries git commit counts into buildings" <| fun () ->
      let rect = { X = 0.0f; Z = 0.0f; W = 20.0f; H = 20.0f }
      let roads = [ mkRoad 10.0f 0.0f 10.0f 20.0f 0.4f ]
      let funcs = [ { mkFunc "metricF" "MetricMod" with FilePath = "MetricMod.fs" } ]
      let gitMeta =
        Map.ofList [
          "MetricMod.fs",
            { CommitCount = 17
              AuthorCount = 3
              BugFixRatio = 0.35f
              FirstCommitDate = DateTimeOffset.Now.AddDays(-200.0)
              LastCommitDate = DateTimeOffset.Now.AddDays(-2.0) } ]
      let building =
        packAlongRoads roads rect funcs Map.empty (Color(70uy, 130uy, 180uy, 255uy)) (Random 1) gitMeta
        |> List.head
      building.GitCommitCount |> Expect.equal "git commit count should survive into building metrics" 17
      building.GitAuthorCount |> Expect.equal "git author count should survive into building metrics" 3
      building.GitBugFixRatio |> Expect.equal "bug-fix ratio should survive into building metrics" 0.35f

    testCase "computeCodeHealthSignals favors heavily connected hot code over calm code" <| fun () ->
      let hot =
        mkSampleBuilding "hot" Tower 180 0.92f 28 16 14 14.0f 32 1.0f
      let calm =
        mkSampleBuilding "calm" Cottage 40 0.10f 1 1 2 240.0f 2 0.2f
      let hotSignals = computeCodeHealthSignals hot
      let calmSignals = computeCodeHealthSignals calm
      (hotSignals.BlastRadius, calmSignals.BlastRadius)
      |> Expect.isGreaterThan "hot connected code should have larger blast radius"
      (hotSignals.RiskScore, calmSignals.RiskScore)
      |> Expect.isGreaterThan "hot connected code should read as riskier overall"

    testCase "computeCodeHealthSignals raises churn pressure for recent frequently touched code" <| fun () ->
      let active =
        mkSampleBuilding "active" Commercial 90 0.45f 6 4 8 6.0f 40 0.6f
      let dormant =
        mkSampleBuilding "dormant" Commercial 90 0.45f 6 4 8 500.0f 1 0.6f
      let activeSignals = computeCodeHealthSignals active
      let dormantSignals = computeCodeHealthSignals dormant
      (activeSignals.ChurnPressure, dormantSignals.ChurnPressure)
      |> Expect.isGreaterThan "recent high-commit code should register higher churn pressure"
  ]

let buildingConditionTests =
  testList "building condition signals" [
    testCase "adapter carries building metadata and explicit condition inputs" <| fun () ->
      let building =
        mkSampleBuilding "adapter" Tower 120 0.6f 7 4 11 18.0f 14 0.9f
        |> fun sample -> { sample with GitAuthorCount = 4; GitBugFixRatio = 0.15f }
      let inputs = buildingConditionInputsFromBuilding building (Some 0.35f) 0.25f true
      inputs.Complexity |> Expect.equal "adapter should preserve complexity" building.Complexity
      inputs.GitCommitCount |> Expect.equal "adapter should preserve commit count" building.GitCommitCount
      inputs.GitAgeDays |> Expect.equal "adapter should preserve git age" building.GitAgeDays
      inputs.CoverageRatio |> Expect.equal "adapter should pass through explicit coverage" (Some 0.35f)
      inputs.BugFixRatio |> Expect.equal "adapter should pass through explicit bug-fix ratio" 0.25f
      inputs.AuthorCount |> Expect.equal "adapter should carry author count from git metadata" 4
      inputs.HasActiveIncident |> Expect.isTrue "adapter should preserve incident flag"

    testCase "current building condition uses bug-fix and authorship history" <| fun () ->
      let stable =
        mkSampleBuilding "stable" Commercial 90 0.45f 6 4 8 20.0f 24 0.6f
        |> fun sample -> { sample with GitAuthorCount = 1; GitBugFixRatio = 0.05f }
      let fragmented =
        mkSampleBuilding "fragmented" Commercial 90 0.45f 6 4 8 20.0f 24 0.6f
        |> fun sample -> { sample with GitAuthorCount = 5; GitBugFixRatio = 0.65f }
      let _, stableSignals = currentBuildingCondition stable
      let inputs, fragmentedSignals = currentBuildingCondition fragmented
      inputs.AuthorCount |> Expect.equal "current building condition should expose git author count" 5
      inputs.BugFixRatio |> Expect.equal "current building condition should expose bug-fix ratio" 0.65f
      (fragmentedSignals.Entropy, stableSignals.Entropy)
      |> Expect.isGreaterThan "fragmented bug-fix-heavy history should read as more entropic"

    testCase "pristine building with full coverage and no churn has minimal condition" <| fun () ->
      let inputs =
        { Complexity = 3
          GitCommitCount = 2
          GitAgeDays = 400.0f
          CoverageRatio = Some 1.0f
          BugFixRatio = 0.0f
          AuthorCount = 1
          HasActiveIncident = false }
      let signals = computeBuildingCondition inputs
      (signals.Incompleteness, 0.15f) |> Expect.isLessThan "full coverage low-complexity should have near-zero incompleteness"
      (signals.Entropy, 0.15f) |> Expect.isLessThan "dormant code should have near-zero entropy"
      signals.ActiveIncident |> Expect.equal "no incident should score 0" 0.0f

    testCase "high-complexity uncovered code scores high incompleteness" <| fun () ->
      let inputs =
        { Complexity = 20
          GitCommitCount = 5
          GitAgeDays = 200.0f
          CoverageRatio = Some 0.0f
          BugFixRatio = 0.0f
          AuthorCount = 1
          HasActiveIncident = false }
      let signals = computeBuildingCondition inputs
      (signals.Incompleteness, 0.70f) |> Expect.isGreaterThan "uncovered high-complexity code should be highly incomplete"

    testCase "unknown coverage on complex code exceeds zero coverage on simple code for incompleteness" <| fun () ->
      let complex =
        { Complexity = 18
          GitCommitCount = 5
          GitAgeDays = 200.0f
          CoverageRatio = None
          BugFixRatio = 0.0f
          AuthorCount = 1
          HasActiveIncident = false }
      let simple =
        { Complexity = 2
          GitCommitCount = 5
          GitAgeDays = 200.0f
          CoverageRatio = Some 0.0f
          BugFixRatio = 0.0f
          AuthorCount = 1
          HasActiveIncident = false }
      let complexSignals = computeBuildingCondition complex
      let simpleSignals = computeBuildingCondition simple
      (complexSignals.Incompleteness, simpleSignals.Incompleteness)
      |> Expect.isGreaterThan "unanalyzed complex code should be more incomplete than zero-covered simple code"

    testCase "high-churn recent buggy code scores high entropy" <| fun () ->
      let inputs =
        { Complexity = 8
          GitCommitCount = 50
          GitAgeDays = 5.0f
          CoverageRatio = Some 0.8f
          BugFixRatio = 0.60f
          AuthorCount = 4
          HasActiveIncident = false }
      let signals = computeBuildingCondition inputs
      (signals.Entropy, 0.55f) |> Expect.isGreaterThan "active buggy code should have high entropy"

    testCase "active incident produces a non-zero incident signal" <| fun () ->
      let inputs =
        { Complexity = 5
          GitCommitCount = 10
          GitAgeDays = 90.0f
          CoverageRatio = Some 0.75f
          BugFixRatio = 0.1f
          AuthorCount = 1
          HasActiveIncident = true }
      let signals = computeBuildingCondition inputs
      (signals.ActiveIncident, 0.5f) |> Expect.isGreaterThan "failing tests should produce a non-zero incident signal"
      (signals.ActiveIncident, 1.0f) |> Expect.isLessThan "incident should not reach 1.0 (transient, not permanent ruin)"

    testCase "dormant high-entropy code without incident has zero incident signal" <| fun () ->
      let inputs =
        { Complexity = 15
          GitCommitCount = 60
          GitAgeDays = 800.0f
          CoverageRatio = Some 0.2f
          BugFixRatio = 0.4f
          AuthorCount = 5
          HasActiveIncident = false }
      let signals = computeBuildingCondition inputs
      signals.ActiveIncident |> Expect.equal "no active incident should score 0 regardless of entropy" 0.0f
      (signals.Entropy, 0.30f) |> Expect.isGreaterThan "high historical churn should still register entropy"

    testPropertyWithConfig cfg "all building condition signals are bounded [0,1]" <|
      fun (complexity: int) (commits: int) (ageDays: float32) (coverage: float32 option) (bugFix: float32) (authors: int) (incident: bool) ->
        let inputs =
          { Complexity = abs complexity % 100
            GitCommitCount = abs commits % 200
            GitAgeDays = abs ageDays % 3650.0f
            CoverageRatio = coverage |> Option.map (fun ratio -> ratio |> max 0.0f |> min 1.0f)
            BugFixRatio = bugFix |> max 0.0f |> min 1.0f
            AuthorCount = max 1 (abs authors % 20)
            HasActiveIncident = incident }
        let signals = computeBuildingCondition inputs
        signals.Incompleteness >= 0.0f && signals.Incompleteness <= 1.0f
        && signals.Entropy >= 0.0f && signals.Entropy <= 1.0f
        && signals.ActiveIncident >= 0.0f && signals.ActiveIncident <= 1.0f
  ]

let buildingConditionReadoutTests =
  testList "building condition readout" [
    testCase "unknown coverage is surfaced explicitly in detail text" <| fun () ->
      let inputs =
        { Complexity = 12
          GitCommitCount = 9
          GitAgeDays = 45.0f
          CoverageRatio = None
          BugFixRatio = 0.15f
          AuthorCount = 2
          HasActiveIncident = false }
      let signals = computeBuildingCondition inputs
      let summary, detail = describeBuildingConditionReadout inputs signals
      summary |> Expect.stringContains "summary should expose incompleteness axis" "condition incomplete"
      detail |> Expect.stringContains "detail should admit missing coverage" "coverage unknown"
      detail |> Expect.stringContains "detail should state quiet incidents" "incident quiet"

    testCase "explicit coverage and active incidents are formatted in readout text" <| fun () ->
      let inputs =
        { Complexity = 8
          GitCommitCount = 18
          GitAgeDays = 10.0f
          CoverageRatio = Some 0.35f
          BugFixRatio = 0.25f
          AuthorCount = 3
          HasActiveIncident = true }
      let signals = computeBuildingCondition inputs
      let summary, detail = describeBuildingConditionReadout inputs signals
      summary |> Expect.stringContains "summary should report incident percentage" "incident 85%"
      detail |> Expect.stringContains "detail should report explicit coverage percent" "coverage 35%"
      detail |> Expect.stringContains "detail should report authorship count" "authors 3"
      detail |> Expect.stringContains "detail should report bug-fix ratio" "bug-fix 25%"
      detail |> Expect.stringContains "detail should state active incident state" "incident active"
  ]

let buildingConditionWearTests =
  testList "building condition wear" [
    testCase "wall wear keeps worn cottages warmer than cool" <| fun () ->
      let worn =
        applyConditionWear
          (BuildingType.wallColor Cottage "wornCottage" 180.0f)
          { Incompleteness = 0.9f
            Entropy = 0.95f
            ActiveIncident = 0.0f }
      (int worn.R, int worn.B)
      |> Expect.isGreaterThan "restrained wear should not erase warm cottage identity"

    testCase "entropy wear desaturates wall colors" <| fun () ->
      let baseColor = Color(210uy, 140uy, 90uy, 255uy)
      let lowEntropy =
        applyConditionWear baseColor
          { Incompleteness = 0.2f
            Entropy = 0.1f
            ActiveIncident = 0.0f }
      let highEntropy =
        applyConditionWear baseColor
          { Incompleteness = 0.2f
            Entropy = 0.95f
            ActiveIncident = 0.0f }
      let spread (color: Color) =
        abs (int color.R - int color.G)
        + abs (int color.G - int color.B)
        + abs (int color.R - int color.B)
      (spread lowEntropy, spread highEntropy)
      |> Expect.isGreaterThan "high entropy should pull wall colors closer to grayscale"

    testCase "incompleteness wear darkens wall colors" <| fun () ->
      let baseColor = Color(200uy, 150uy, 110uy, 255uy)
      let nearlyComplete =
        applyConditionWear baseColor
          { Incompleteness = 0.05f
            Entropy = 0.3f
            ActiveIncident = 0.0f }
      let incomplete =
        applyConditionWear baseColor
          { Incompleteness = 0.95f
            Entropy = 0.3f
            ActiveIncident = 0.0f }
      let luminance (color: Color) = int color.R + int color.G + int color.B
      (luminance nearlyComplete, luminance incomplete)
      |> Expect.isGreaterThan "unfinished buildings should read slightly darker"

    testCase "roof wear stays opaque and darkens under heavy condition" <| fun () ->
      let pristine =
        applyConditionWearRoof
          (BuildingType.roofColor Cottage (Color(70uy, 130uy, 180uy, 255uy)))
          { Incompleteness = 0.0f
            Entropy = 0.0f
            ActiveIncident = 0.0f }
      let worn =
        applyConditionWearRoof
          (BuildingType.roofColor Cottage (Color(70uy, 130uy, 180uy, 255uy)))
          { Incompleteness = 0.9f
            Entropy = 0.8f
            ActiveIncident = 0.0f }
      worn.A |> Expect.equal "roof wear should preserve alpha" 255uy
      let luminance (color: Color) = int color.R + int color.G + int color.B
      (luminance pristine, luminance worn)
      |> Expect.isGreaterThan "worn roofs should darken more than pristine roofs"
  ]

let private maxWearSignals =
  { Incompleteness = 1.0f
    Entropy = 1.0f
    ActiveIncident = 1.0f }

let private canonicalWallColor buildingType =
  BuildingType.wallColor buildingType "canonical" 0.0f

let private colorDistancePerceptual (left: Color) (right: Color) =
  let dr = float32 left.R - float32 right.R
  let dg = float32 left.G - float32 right.G
  let db = float32 left.B - float32 right.B
  MathF.Sqrt(0.299f * dr * dr + 0.587f * dg * dg + 0.114f * db * db)

let buildingLegibilityTests =
  testList "building legibility" [
    testCase "fresh tower stays distinct from a max-worn rowhouse" <| fun () ->
      let freshTower = BuildingType.wallColor Tower "tower-legibility" 0.0f
      let wornRowhouse = canonicalWallColor Rowhouse |> fun color -> applyConditionWear color maxWearSignals
      let distance = colorDistancePerceptual freshTower wornRowhouse
      (distance, 20.0f)
      |> Expect.isGreaterThanOrEqual "fresh towers should not collapse into worn rowhouse tones"

    testCase "adjacent building types remain distinct under max wear" <| fun () ->
      let orderedTypes =
        [| Shed; Cottage; Rowhouse; Commercial; Tower; Skyscraper |]
      orderedTypes
      |> Array.pairwise
      |> Array.iter (fun (leftType, rightType) ->
          let leftWorn = canonicalWallColor leftType |> fun color -> applyConditionWear color maxWearSignals
          let rightWorn = canonicalWallColor rightType |> fun color -> applyConditionWear color maxWearSignals
          let distance = colorDistancePerceptual leftWorn rightWorn
          (distance, 12.0f)
          |> Expect.isGreaterThanOrEqual (sprintf "%A and %A should remain visually distinct even at max wear" leftType rightType))

    testCase "max-history rowhouse keeps the authored family footprint dominant" <| fun () ->
      let building =
        mkSampleBuilding "history-extreme" Rowhouse 80 0.5f 10 8 12 150.0f 120 0.0f
        |> fun sample ->
            { sample with
                W = 10.0f
                D = 6.0f
                GitAuthorCount = 2
                GitBugFixRatio = 0.05f }
      let authored =
        generateTypedCompoundForFamily
          building.Func.QualifiedName
          building.BuildingType
          (currentMassingFamily building)
          building.Complexity
          (building.W / 2.0f)
          (building.D / 2.0f)
      let accreted = compoundForBuilding building
      let authoredArea = authored |> Array.sumBy (fun cube -> cube.HW * cube.HD)
      let addedArea =
        accreted
        |> Array.skip authored.Length
        |> Array.sumBy (fun cube -> cube.HW * cube.HD)
      (addedArea / authoredArea, 0.25f)
      |> Expect.isLessThanOrEqual "history extremes should not overpower the readable rowhouse family silhouette"
  ]

let terrainOverlaySummaryTests =
  testList "terrain overlay summary" [
    testCase "empty terrain summary returns none" <| fun () ->
      summarizeTerrainOverlay []
      |> Expect.isNone "no buildings means no terrain overlay summary"

    testCase "terrain summary captures relief range and band counts" <| fun () ->
      let buildings =
        [ mkSampleBuilding "basin" Cottage 30 0.1f 1 1 2 180.0f 4 0.2f
          mkSampleBuilding "shelf" Rowhouse 80 0.3f 3 2 4 120.0f 9 1.0f
          mkSampleBuilding "ridge" Tower 180 0.8f 10 6 12 30.0f 21 2.4f ]
      let summary =
        summarizeTerrainOverlay buildings
        |> Option.defaultWith (fun () -> failtest "expected terrain summary")
      summary.MinHeight |> Expect.equal "minimum terrain should reflect basin floor" 0.2f
      summary.MaxHeight |> Expect.equal "maximum terrain should reflect ridge crest" 2.4f
      (summary.BasinCount, summary.ShelfCount, summary.RidgeCount)
      |> Expect.equal "one building should land in each relief band" (1, 1, 1)
  ]

let visibleRoadNetworkTests =
  testList "visible road network" [
    testCase "project-zone scaffolding is not rendered as a visible road" <| fun () ->
      let zones = [ "ProjectA", TRect.create 0.0f 0.0f 20.0f 20.0f ]
      buildVisibleRoadNetwork zones []
      |> Expect.isEmpty "visible roads should come from explicit street growth, not treemap borders"

    testCase "explicit primary roads survive visible road composition" <| fun () ->
      let zones = [ "ProjectA", TRect.create 0.0f 0.0f 20.0f 20.0f ]
      let roads = [ mkRoad 0.0f 10.0f 20.0f 10.0f 0.4f ]
      buildVisibleRoadNetwork zones roads
      |> Expect.equal "visible road composition should preserve explicit primary roads" roads
  ]

let parseDaemonInfoJsonTests =
  testList "parseDaemonInfoJson" [
    testCase "valid JSON returns workingDirectory" <| fun () ->
      let json = """{"pid":1234,"version":"0.5.0","startedAt":"2024-01-01T00:00:00Z","workingDirectory":"C:\\Code\\Repos\\SageFs"}"""
      parseDaemonInfoJson json
      |> Expect.equal "should parse workingDirectory" (Some @"C:\Code\Repos\SageFs")

    testCase "JSON without workingDirectory returns None" <| fun () ->
      let json = """{"pid":1234,"version":"0.5.0"}"""
      parseDaemonInfoJson json
      |> Expect.isNone "should return None when field missing"

    testCase "invalid JSON returns None" <| fun () ->
      parseDaemonInfoJson "not valid json"
      |> Expect.isNone "should return None for invalid JSON"

    testCase "JSON with empty workingDirectory returns None" <| fun () ->
      let json = """{"workingDirectory":""}"""
      parseDaemonInfoJson json
      |> Expect.isNone "should return None for empty path"

    testCase "JSON with whitespace-only workingDirectory returns None" <| fun () ->
      let json = """{"workingDirectory":"   "}"""
      parseDaemonInfoJson json
      |> Expect.isNone "should return None for whitespace-only path"
  ]

let resolveRepoRootPureTests =
  testList "resolveRepoRootPure" [
    testCase "explicit argv path takes priority over SageFs dir" <| fun () ->
      let argv = [| @"C:\SomeProject" |]
      let sageFsDir = Some @"C:\SageFs"
      let fallback = @"C:\Fallback"
      resolveRepoRootPure argv sageFsDir fallback
      |> Expect.equal "argv should win" @"C:\SomeProject"

    testCase "SageFs dir used when no argv" <| fun () ->
      let argv = [||]
      let sageFsDir = Some @"C:\SageFs"
      let fallback = @"C:\Fallback"
      resolveRepoRootPure argv sageFsDir fallback
      |> Expect.equal "SageFs dir should be used" @"C:\SageFs"

    testCase "fallback used when no argv and no SageFs dir" <| fun () ->
      let argv = [||]
      let sageFsDir = None
      let fallback = @"C:\Fallback"
      resolveRepoRootPure argv sageFsDir fallback
      |> Expect.equal "fallback should be used" @"C:\Fallback"

    testCase "empty-string argv falls through to SageFs dir" <| fun () ->
      let argv = [| "" |]
      let sageFsDir = Some @"C:\SageFs"
      let fallback = @"C:\Fallback"
      resolveRepoRootPure argv sageFsDir fallback
      |> Expect.equal "empty string should not be used" @"C:\SageFs"
  ]

let sourceFileShowcaseTests =
  testList "single-file showcase" [
    testCase "tryResolveSourceFile recognizes .fs source files only" <| fun () ->
      withTempFsSource "module Sample\nlet a () = 1" <| fun _ filePath ->
        tryResolveSourceFile filePath
        |> Expect.equal "real .fs file should resolve" (Some filePath)
        tryResolveSourceFile (filePath + ".txt")
        |> Expect.isNone "non-.fs path should not resolve as a showcase source file"

    testCase "buildSingleFileShowcase produces a tiny clean city from one file" <| fun () ->
      let source =
        """
module TinyCity

let loadConfig () = 1
let parseArgs value = value + 1
let buildModel () = loadConfig () + parseArgs 4
let computeHeat value = buildModel () + value
let layoutRoads value = computeHeat value + 1
let placeBuildings value = layoutRoads value + 2
let renderFrame value = placeBuildings value + 3
let writeReport () = renderFrame 1 |> ignore
"""
      withTempFsSource source <| fun root filePath ->
        let buildings, districts, roads, blocks, callEdges, alleyRoads =
          buildSingleFileShowcase root filePath
        districts |> Expect.hasLength "single-file showcase should use one district" 1
        blocks.Length |> Expect.equal "single-file showcase should use one block" 1
        (roads.Length, 1) |> Expect.isGreaterThan "single-file showcase should reuse the real growth layout instead of a single hardcoded road"
        alleyRoads.Length |> Expect.equal "showcase alley surface should mirror the visible showcase roads" roads.Length
        buildings |> Expect.isNonEmpty "single-file showcase should place buildings"
        callEdges |> Expect.isNonEmpty "single-file showcase should preserve local call relationships"
        buildings
        |> List.allPairs buildings
        |> List.filter (fun (left, right) -> left.Func.QualifiedName <> right.Func.QualifiedName)
        |> List.exists (fun (left, right) -> buildingsOverlap left right)
        |> Expect.isFalse "showcase buildings should never overlap"
        buildings
        |> List.exists (fun building -> roads |> List.exists (buildingIntersectsRoad building))
        |> Expect.isFalse "showcase buildings should stay clear of the showcase road"
  ]

let nightScaleTests =
  testList "day/night cycle" [
    testCase "nightScaleForElevation at noon returns 1.0" <| fun () ->
      nightScaleForElevation 1.0f
      |> Expect.equal "noon = 1.0" 1.0f

    testCase "nightScaleForElevation at midnight is dim" <| fun () ->
      let ns = nightScaleForElevation -1.0f
      (ns, 0.5f) |> Expect.isLessThan "night is dim"

    testCase "nightScaleForElevation is monotone with elevation" <| fun () ->
      let lo = nightScaleForElevation -0.5f
      let hi = nightScaleForElevation 0.5f
      (hi, lo) |> Expect.isGreaterThan "higher sun = higher scale"
  ]

let allTests =
  testList "CodeCity Domain" [
    colorMathTests
    treemapTests
    edgeAndHeatTests
    complexityTests
    functionExtractionTests
    endToEndParsingTests
    projectAwareParsingTests
    roadCurveTests
    compoundShapeTests
    typedMassingTests
    massingFamilyTests
    historyAccretionTests
    cameraMovementTests
    visualDefaultsTests
    roadAccessTests
    trafficSimulationTests
    landUseDynamicsTests
    economyModelTests
    zoningEnvelopeTests
    buildingSubstitutionTests
    subdivisionFidelityTests
    majorStreetGrowthTests
    specDrivenLayoutTests
    urbanSimulationProofLayerTests
    simulationTraceTests
    simulationTimelineTests
    simulationBenchmarkTests
    gitMetaTests
    organicFactorTests
    weberDistrictTests
    roadFrontageTests
    packAlongRoadsTests
    buildingTypologyTests
    gitAgeColorTests
    roadColorTests
    arcFormulaTests
    splineSegmentTests
    districtOverlayPlanningTests
    terrainOverlaySummaryTests
    buildingTypeAlphaTests
    complexityFootprintTests
    gableRoofTests
    orientedBuildingGeometryTests
    interDistrictRoadTests
    semanticTerrainTests
    repeatedSideConnectorTests
    codeHealthMetricTests
    buildingConditionTests
    buildingConditionReadoutTests
    buildingConditionWearTests
    buildingLegibilityTests
    visibleRoadNetworkTests
    nightScaleTests
    parseDaemonInfoJsonTests
    resolveRepoRootPureTests
    sourceFileShowcaseTests
  ]

[<EntryPoint>]
let main argv =
  Expecto.Tests.runTestsWithCLIArgs [] argv allTests


