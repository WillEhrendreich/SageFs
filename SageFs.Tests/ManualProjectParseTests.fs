module SageFs.Tests.ManualProjectParseTests

open System
open System.IO
open Expecto
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
        Expect.equal options.Length 1 "should produce one FSharpProjectOptions"
        let srcFiles = options.[0].SourceFiles |> Array.map Path.GetFileName |> Set.ofArray
        Expect.equal srcFiles (Set.ofList [ "A.fs"; "B.fs" ]) "should find both source files"
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
        Expect.equal options.Length 1 "should produce one FSharpProjectOptions"
        let srcFiles = options.[0].SourceFiles |> Array.map Path.GetFileName |> Set.ofArray
        Expect.equal srcFiles (Set.ofList [ "Main.fs"; "Lib.fs" ]) "should include referenced project's sources"
      finally
        Directory.Delete(dir, true))

    testCase "missing fsproj returns empty" (fun () ->
      let options = ManualProjectParse.parseFsproj quietLogger "Z:\\does-not-exist\\Missing.fsproj"
      Expect.isEmpty options "missing project should yield no options")

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
        Expect.hasLength coreRefs 1 "exactly one SageFs.Core.dll reference after dedup"
        Expect.equal coreRefs.Head fresh "the fresh (newest) copy must win over the stale orphan"
      finally
        Directory.Delete(dir, true))
  ]
