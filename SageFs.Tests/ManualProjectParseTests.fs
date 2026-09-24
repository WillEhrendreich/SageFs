module SageFs.Tests.ManualProjectParseTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.ProjectLoading

/// Quiet logger for tests.
let quietLogger =
  { new Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let simpleFsproj =
  "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
  "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n" +
  "  <ItemGroup>\n" +
  "    <Compile Include=\"A.fs\" />\n" +
  "    <Compile Include=\"B.fs\" />\n" +
  "  </ItemGroup>\n" +
  "</Project>\n"

let refFsproj =
  "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
  "  <ItemGroup>\n" +
  "    <Compile Include=\"Main.fs\" />\n" +
  "    <ProjectReference Include=\"Lib.fsproj\" />\n" +
  "  </ItemGroup>\n" +
  "</Project>\n"

let libFsproj =
  "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
  "  <ItemGroup><Compile Include=\"Lib.fs\" /></ItemGroup>\n" +
  "</Project>\n"

[<Tests>]
let tests =
  testList "ManualProjectParse" [
    testCase "parses source files from a simple fsproj" (fun () ->
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-manual-parse-" + Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory dir |> ignore
      try
        File.WriteAllText(Path.Combine(dir, "A.fs"), "module A\nlet x = 1\n")
        File.WriteAllText(Path.Combine(dir, "B.fs"), "module B\nlet y = 2\n")
        File.WriteAllText(Path.Combine(dir, "App.fsproj"), simpleFsproj)
        let options = ManualProjectParse.parseFsproj quietLogger (Path.Combine(dir, "App.fsproj"))
        options.Length |> Expect.equal "should produce one FSharpProjectOptions" 1
        let srcFiles = options.[0].SourceFiles |> Array.map Path.GetFileName |> Set.ofArray
        srcFiles |> Expect.equal "should find both source files" (Set.ofList [ "A.fs"; "B.fs" ])
      finally
        Directory.Delete(dir, true))

    testCase "recurses into project references" (fun () ->
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-manual-parse-" + Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory dir |> ignore
      try
        File.WriteAllText(Path.Combine(dir, "Lib.fs"), "module Lib\nlet z = 3\n")
        File.WriteAllText(Path.Combine(dir, "Lib.fsproj"), libFsproj)
        File.WriteAllText(Path.Combine(dir, "Main.fs"), "module Main\n")
        File.WriteAllText(Path.Combine(dir, "App.fsproj"), refFsproj)
        let options = ManualProjectParse.parseFsproj quietLogger (Path.Combine(dir, "App.fsproj"))
        options.Length |> Expect.equal "should produce one FSharpProjectOptions" 1
        let srcFiles = options.[0].SourceFiles |> Array.map Path.GetFileName |> Set.ofArray
        srcFiles |> Expect.equal "should include referenced project's sources" (Set.ofList [ "Main.fs"; "Lib.fs" ])
      finally
        Directory.Delete(dir, true))

    testCase "missing fsproj returns empty" (fun () ->
      let options = ManualProjectParse.parseFsproj quietLogger "Z:\\does-not-exist\\Missing.fsproj"
      options |> Expect.isEmpty "missing project should yield no options")

    testCase "bin reference collection dedupes same-named DLLs across TFM dirs, keeping the newest" (fun () ->
      // Regression: a project bin holding orphaned same-named DLLs in multiple
      // TFM subdirs (e.g. a stale net9 SageFs.Core.dll left after the project
      // retargeted to net10) made the REPL compile against ancient metadata —
      // the old copy shadowed the fresh build. The collector must keep only
      // the NEWEST copy of each assembly name.
      let dir = Path.Combine(Path.GetTempPath(), "sagefs-manual-parse-" + Guid.NewGuid().ToString("N"))
      Directory.CreateDirectory dir |> ignore
      try
        File.WriteAllText(Path.Combine(dir, "App.fs"), "module App\nlet x = 1\n")
        File.WriteAllText(Path.Combine(dir, "App.fsproj"), simpleFsproj)
        // Build the bin layout: cfg dir newest = Release; under it two TFM
        // subdirs each holding a same-named SageFs.Core.dll — the current
        // net10.0 copy in the fresh build dir, and a stale net9.0 orphan from
        // when the project previously targeted net9. The net10.0 copy is the
        // NEWER one (the stale-shadow scenario).
        let binDir = Path.Combine(dir, "bin", "Release")
        let net9Dir = Path.Combine(binDir, "net9.0")
        let net10Dir = Path.Combine(binDir, "net10.0")
        Directory.CreateDirectory net9Dir |> ignore
        Directory.CreateDirectory net10Dir |> ignore
        let stale = Path.Combine(net9Dir, "SageFs.Core.dll")
        let fresh = Path.Combine(net10Dir, "SageFs.Core.dll")
        File.WriteAllBytes(stale, Array.init 256 byte)
        File.WriteAllBytes(fresh, Array.init 256 (fun i -> 255uy - byte i))
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2.0))
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow)

        let refs = ManualProjectParse.collectBinReferences quietLogger [ Path.Combine(dir, "App.fsproj") ]
        let coreRefs = refs |> List.filter (fun r -> Path.GetFileName r = "SageFs.Core.dll")
        coreRefs |> Expect.hasLength "exactly one SageFs.Core.dll reference after dedup" 1
        coreRefs.Head |> Expect.equal "the fresh (newest) copy must win over the stale orphan" fresh
      finally
        Directory.Delete(dir, true))

    testCase "WHY — Arcade-style repos (fsharp/runtime/sdk) route build output through <repo>/artifacts/bin/<ProjectName>/, not <projectDir>/bin/; the manual fallback must find it there instead of reporting 'no bin dir'" (fun () ->
      // Reproduces the real fsharp-compiler-services layout: the .fsproj lives
      // at <repo>/src/Compiler/FSharp.Compiler.Service.fsproj, has NO bin/
      // directory anywhere under src/Compiler, and its real build output is at
      // <repo>/artifacts/bin/FSharp.Compiler.Service/<Config>/<Tfm>/*.dll.
      let repoDir = Path.Combine(Path.GetTempPath(), "sagefs-manual-parse-arcade-" + Guid.NewGuid().ToString("N"))
      let projDir = Path.Combine(repoDir, "src", "Compiler")
      Directory.CreateDirectory projDir |> ignore
      try
        File.WriteAllText(Path.Combine(projDir, "App.fs"), "module App\nlet x = 1\n")
        File.WriteAllText(Path.Combine(projDir, "FSharp.Compiler.Service.fsproj"), simpleFsproj)
        let arcadeOut = Path.Combine(repoDir, "artifacts", "bin", "FSharp.Compiler.Service", "Release", "netstandard2.0")
        Directory.CreateDirectory arcadeOut |> ignore
        let dll = Path.Combine(arcadeOut, "FSharp.Compiler.Service.dll")
        File.WriteAllBytes(dll, Array.init 32 byte)

        let refs = ManualProjectParse.collectBinReferences quietLogger [ Path.Combine(projDir, "FSharp.Compiler.Service.fsproj") ]
        refs
        |> List.map Path.GetFileName
        |> Expect.contains "the Arcade artifacts/bin output must be found even though <projectDir>/bin does not exist" "FSharp.Compiler.Service.dll"
      finally
        Directory.Delete(repoDir, true))
  ]
