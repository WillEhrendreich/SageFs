/// The regression test for the daemon-RSS-eating incident this module
/// exists to close: a directory tree containing a symlink cycle must not
/// send `walkFiles` into unbounded recursion. The real incident was Wine's
/// `dosdevices/z:` symlinking to `/`, discovered under `~/.local/share/
/// Steam/...`, which a project-discovery walk followed forever, building an
/// ever-longer path string on every lap (a live process dump showed one over
/// 3,000 characters and still growing). `a/b -> a` here is the same shape,
/// minimized.
module SageFs.Tests.SafeDirectoryWalkTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

let private freshTempDir () =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-walk-test", Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  dir

let private isFsproj (path: string) = path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
let private noPrune (_: string) = false

[<Tests>]
let tests =
  testList "SafeDirectoryWalk" [

    testCase "WHY — walkFiles — finds files matching the predicate under a plain tree" <| fun _ ->
      let root = freshTempDir ()
      try
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        File.WriteAllText(Path.Combine(root, "Root.fsproj"), "")
        File.WriteAllText(Path.Combine(root, "src", "Nested.fsproj"), "")
        File.WriteAllText(Path.Combine(root, "src", "Readme.md"), "")

        let result = SafeDirectoryWalk.walkFiles root isFsproj noPrune SafeDirectoryWalk.Bounds.standard

        result.Files |> List.map Path.GetFileName |> List.sort
        |> Expect.equal "finds both .fsproj files, ignores the .md" [ "Nested.fsproj"; "Root.fsproj" ]
        result.Truncated |> Expect.isFalse "a small plain tree never hits a bound"
      finally
        Directory.Delete(root, true)

    testCase "WHY — walkFiles — prunes a subtree before descending, so its files are never seen" <| fun _ ->
      let root = freshTempDir ()
      try
        let obj = Path.Combine(root, "obj")
        Directory.CreateDirectory obj |> ignore
        File.WriteAllText(Path.Combine(obj, "Generated.fsproj"), "")
        File.WriteAllText(Path.Combine(root, "Real.fsproj"), "")

        let pruneObj (path: string) = Path.GetFileName(path) = "obj"
        let result = SafeDirectoryWalk.walkFiles root isFsproj pruneObj SafeDirectoryWalk.Bounds.standard

        result.Files |> List.map Path.GetFileName
        |> Expect.equal "obj/ is never descended into, so its .fsproj never appears" [ "Real.fsproj" ]
      finally
        Directory.Delete(root, true)

    testCase "WHY — walkFiles — a symlink cycle terminates instead of recursing forever (the Wine z: incident, minimized)" <| fun _ ->
      let root = freshTempDir ()
      try
        let a = Path.Combine(root, "a")
        Directory.CreateDirectory a |> ignore
        let cycleLink = Path.Combine(a, "b")
        let canMakeSymlinks =
          try
            Directory.CreateSymbolicLink(cycleLink, a) |> ignore
            true
          with _ -> false
        match canMakeSymlinks with
        | false -> skiptest "this environment cannot create directory symlinks"
        | true ->
          File.WriteAllText(Path.Combine(a, "Project.fsproj"), "")

          // The whole point: this call returns at all. A pre-fix walk built
          // on `Directory.EnumerateFiles(_, _, AllDirectories)` would follow
          // a/b -> a -> b -> a -> ... and never come back.
          let result = SafeDirectoryWalk.walkFiles root isFsproj noPrune SafeDirectoryWalk.Bounds.standard

          result.Files |> List.map Path.GetFileName
          |> Expect.equal "the real project file is found exactly once, the cycle contributes nothing" [ "Project.fsproj" ]
          result.Truncated
          |> Expect.isFalse "the symlink is refused outright — this never needed to hit a bound to survive"
      finally
        Directory.Delete(root, true)

    testCase "WHY — walkFiles — hits MaxEntries and says so, rather than silently returning a partial list as if it were complete" <| fun _ ->
      let root = freshTempDir ()
      try
        for i in 1 .. 20 do
          File.WriteAllText(Path.Combine(root, sprintf "P%d.fsproj" i), "")

        let tinyBounds : SafeDirectoryWalk.Bounds = { MaxDepth = 64; MaxEntries = 5 }
        let result = SafeDirectoryWalk.walkFiles root isFsproj noPrune tinyBounds

        result.Truncated |> Expect.isTrue "20 entries against a cap of 5 must report truncation, not pretend to be complete"
        (result.Files.Length, 20) |> Expect.isLessThan "it actually stopped early, not just flagged and kept going"
      finally
        Directory.Delete(root, true)

    testCase "WHY — walkFiles — hits MaxDepth and says so" <| fun _ ->
      let root = freshTempDir ()
      try
        let mutable deepest = root
        for _ in 1 .. 6 do
          deepest <- Path.Combine(deepest, "d")
          Directory.CreateDirectory deepest |> ignore
        File.WriteAllText(Path.Combine(deepest, "Deep.fsproj"), "")

        let shallowBounds : SafeDirectoryWalk.Bounds = { MaxDepth = 2; MaxEntries = 50_000 }
        let result = SafeDirectoryWalk.walkFiles root isFsproj noPrune shallowBounds

        result.Truncated |> Expect.isTrue "a tree 6 levels deep against a cap of 2 must report truncation"
        result.Files |> Expect.isEmpty "the deeply nested project was never reached"
      finally
        Directory.Delete(root, true)
  ]
