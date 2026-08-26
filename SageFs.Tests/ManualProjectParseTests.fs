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
  ]
