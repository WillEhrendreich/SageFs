/// Source walks prune build and VCS directories before they descend and never follow a directory symlink,
/// the shape of the 55 GB symlink-cycle incident.
module SageFs.Tests.SourceWalkPruningTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

let private freshTempDir () =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-roast-walk-test", Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  dir

/// A project directory shaped like the incident: a real source, a symlink that points back at its own
/// directory, a build tree with many files, and a package cache.
let private projectWithCycleAndBuildTree (root: string) : unit =
  let src = Path.Combine(root, "src")
  Directory.CreateDirectory src |> ignore
  File.WriteAllText(Path.Combine(src, "A.fs"), "")
  File.WriteAllText(Path.Combine(src, "script.fsx"), "")
  Directory.CreateSymbolicLink(Path.Combine(src, "loop"), src) |> ignore
  let release = Path.Combine(root, "bin", "Release")
  Directory.CreateDirectory release |> ignore
  for i in 1 .. 100 do
    File.WriteAllText(Path.Combine(release, sprintf "Generated%d.fs" i), "")
  Directory.CreateDirectory(Path.Combine(root, "obj")) |> ignore
  File.WriteAllText(Path.Combine(root, "obj", "App.AssemblyInfo.fs"), "")
  Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg")) |> ignore
  File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "Dep.fs"), "")

let private relativeTo (root: string) (files: string list) : string list =
  files |> List.map (fun f -> Path.GetRelativePath(root, f).Replace('\\', '/')) |> List.sort

/// A directory symlink needs a privilege a Windows runner may not hold, so these cases exist only where one
/// can be made. Where they exist, a symlink that cannot be made fails the case.
let private walkCases : Test list =
  match OperatingSystem.IsWindows() with
  | true -> []
  | false ->
    [ testCase "WHY — sourceFiles — a symlink cycle and a build tree yield exactly the real sources" <| fun _ ->
        let root = freshTempDir ()
        try
          projectWithCycleAndBuildTree root
          let result = SafeDirectoryWalk.sourceFiles [ ".fs"; ".fsx" ] SafeDirectoryWalk.isBuildOrVcsDir root
          result.Files |> relativeTo root
          |> Expect.equal "the two sources under src, and nothing from bin, obj, node_modules or the loop" [ "src/A.fs"; "src/script.fsx" ]
          result.Truncated |> Expect.isFalse "the walk ended on its own, without reaching a bound"
        finally
          Directory.Delete(root, true)

      testCase "WHY — isBuildOrVcsDir — a build tree is pruned before it is entered, so its size never counts against the walk" <| fun _ ->
        let root = freshTempDir ()
        try
          projectWithCycleAndBuildTree root
          // A bound that fits the real tree and not the 100-file build tree: a walk that entered bin and
          // filtered afterwards would hit it and report Truncated.
          let tight : SafeDirectoryWalk.Bounds = { MaxDepth = 64; MaxEntries = 20 }
          let isSource (path: string) = path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
          let pruned = SafeDirectoryWalk.walkFiles root isSource SafeDirectoryWalk.isBuildOrVcsDir tight
          pruned.Truncated |> Expect.isFalse "pruned before descending, the tight bound is enough"
          let unpruned = SafeDirectoryWalk.walkFiles root isSource (fun _ -> false) tight
          unpruned.Truncated |> Expect.isTrue "the same bound is exceeded once the build tree is entered"
        finally
          Directory.Delete(root, true) ]

[<Tests>]
let walkTests =
  testList "source walks" (walkCases @ [
    testCase "WHY — isBuildOrVcsDir — it names bin, obj, .git and node_modules by the directory's own name, whatever the root" <| fun _ ->
      for name in [ "bin"; "obj"; ".git"; "node_modules"; "BIN" ] do
        SafeDirectoryWalk.isBuildOrVcsDir (Path.Combine("/any/where", name))
        |> Expect.isTrue (sprintf "%s is pruned" name)
      for name in [ "src"; "binary"; "objects"; "Tests" ] do
        SafeDirectoryWalk.isBuildOrVcsDir (Path.Combine("/any/where", name))
        |> Expect.isFalse (sprintf "%s is walked" name)
      SafeDirectoryWalk.isBuildOrVcsDir (Path.Combine("/work/obj/checkout", "src"))
      |> Expect.isFalse "a directory under a pruned name is judged on its own name, the walk never reaches it anyway"
  ])
