/// The options a session's builds and worker use are written by one place: session create (`DirectoryConfig.prepareForCreate`).
/// Reading a config, rendering a page, listing sessions, none of them writes, so what a build and a worker use cannot depend on who
/// looked at a directory last. This counts the product's writes and fails when there is a second one.
module SageFs.Tests.SessionOptionsOwnerTests

open System.IO
open Expecto
open Expecto.Flip

let private repoRoot = RepoPaths.repoPathFull [||]

let private writes : (string * int) list =
  [ for project in [ "SageFs"; "SageFs.Core"; "SageFs.Host" ] do
      for file in Directory.EnumerateFiles(Path.Combine(repoRoot, project), "*.fs", SearchOption.AllDirectories) do
        let relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/')
        match relative.Contains "/obj/" || relative.Contains "/bin/" || relative = "SageFs.Core/SessionOptionsRegistry.fs" with
        | true -> ()
        | false ->
          let count = File.ReadAllText(file).Split("SessionOptionsRegistry.remember").Length - 1
          match count with
          | 0 -> ()
          | n -> yield relative, n ]

[<Tests>]
let tests =
  testList "Session options owner" [

    testCase "WHY - the registry of a directory's session options is written in one place, session create, so a read or a render can never change what a build uses" <| fun _ ->
      writes |> Expect.equal "exactly one write, in DirectoryConfig.prepareForCreate" [ "SageFs/DirectoryConfig.fs", 1 ]
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant tests |> ignore
