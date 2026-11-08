/// `check_project` over a real restored project, through the real design-time MSBuild run and the real build of a stale
/// referenced project. The in-process cases (`CheckProjectCompilerTests`) build their arguments by hand; this one starts
/// from a project file, which is where the MSBuild targets, the reference assemblies and the process handling can go wrong.
module SageFs.Tests.CheckProjectMsBuildTests

open System
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open SageFs.Features

let private fsproj (references: string list) (sources: string list) : string =
  let compiles = sources |> List.map (sprintf "<Compile Include=\"%s\" />") |> String.concat ""
  let refs = references |> List.map (sprintf "<ProjectReference Include=\"%s\" />") |> String.concat ""
  sprintf "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>%s</ItemGroup><ItemGroup>%s</ItemGroup></Project>" compiles refs

let private answer (project: string) (overlays: string) : Threading.Tasks.Task<string> =
  CheckProject.answer project "Debug" overlays CancellationToken.None

[<Tests>]
let tests =
  testList "check_project through MSBuild" [
    TestInfrastructure.Integration.hostCaseTask "a project is checked with an overlay, its stale reference is built once, and the project itself is never built" <| fun () ->
      task {
        let dir = Path.Combine(Path.GetTempPath(), "sagefs-check-project-" + Guid.NewGuid().ToString "N")
        let lib = Path.Combine(dir, "lib")
        let app = Path.Combine(dir, "app")
        Directory.CreateDirectory lib |> ignore
        Directory.CreateDirectory app |> ignore
        try
          File.WriteAllText(Path.Combine(lib, "Lib.fsproj"), fsproj [] [ "Lib.fs" ])
          File.WriteAllText(Path.Combine(lib, "Lib.fs"), "module Lib\n\nlet double (x: int) : int = x * 2\n")
          File.WriteAllText(Path.Combine(app, "App.fsproj"), fsproj [ "../lib/Lib.fsproj" ] [ "A.fs" ])
          File.WriteAllText(Path.Combine(app, "A.fs"), "module A\n\nlet quad (x: int) : int = Lib.double (Lib.double x)\n")
          let appProject = Path.Combine(app, "App.fsproj")
          let! restored, restoreOutput = ContentRevisionTests.run dir "dotnet" [ "restore"; appProject ]
          restored |> Expect.equal (sprintf "the fixture restores: %s" restoreOutput) 0

          let! first = answer appProject ""
          first |> Expect.stringStarts "the project agrees with itself" "Clean"
          first |> Expect.stringContains "the arguments were read, not kept" "compiler arguments read"
          first |> Expect.stringContains "the reference had no output, so it was built first" "built first: "
          first |> Expect.stringContains "and it is the library" "Lib.fsproj"
          // The design-time run makes empty output directories; it writes no assembly.
          let assemblies (sub: string) =
            match Directory.Exists(Path.Combine(app, sub)) with
            | true -> Directory.GetFiles(Path.Combine(app, sub), "*.dll", SearchOption.AllDirectories)
            | false -> [||]
          Array.append (assemblies "bin") (assemblies "obj") |> Expect.isEmpty "the project itself is never built"

          let! second = answer appProject ""
          second |> Expect.stringContains "nothing that shaped the arguments moved" "kept from an earlier call"
          second.Contains "built first" |> Expect.isFalse "and the library is current, so nothing is built"

          let broken = """[ { "path": "A.fs", "text": "module A\n\nlet quad (x: int) : int = Lib.double (Lib.double x) + \"x\"\n" } ]"""
          let! third = answer appProject broken
          third |> Expect.stringContains "the overlay breaks the project" "error FS"
          third |> Expect.stringContains "in the project's own file" "A.fs(3,"
          File.ReadAllText(Path.Combine(app, "A.fs")) |> Expect.stringContains "the file on disk is untouched" "Lib.double (Lib.double x)\n"

          File.WriteAllText(Path.Combine(lib, "Lib.fs"), "module Lib\n\nlet double (x: int) : int = x * 2\nlet triple (x: int) : int = x * 3\n")
          let! afterEdit = answer appProject ""
          afterEdit |> Expect.stringContains "the library's source moved past its output, so it is built once" "built first: "

          File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />")
          let! afterProps = answer appProject ""
          afterProps |> Expect.stringContains "a props file that appeared above the project refreshes the arguments" "Directory.Build.props appeared"
        finally
          try Directory.Delete(dir, true) with _ -> ()
      }
  ]
