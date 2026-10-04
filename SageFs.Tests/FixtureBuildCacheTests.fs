/// One `dotnet build` per hot-reload fixture per runtime, then copies.
///
/// About a hundred host cases each used to copy a fixture into a run dir of their own and build it, for 3 to 4 CPU
/// seconds apiece, and the fixture differs between cases only by runtime. These cases pin the cache that builds a
/// fixture once per process and copies the result into each case's run dir: what its key is made of, that a build
/// happens once however many cases ask at once, that the copies keep the file times the build left (the host decides a
/// source "was edited after the build" from them), and that any doubt falls back to a real build in the case's own dir.
module SageFs.Tests.FixtureBuildCacheTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.FixtureBuildCache

let private inputs : BuildInputs =
  { Tree = [ "App.fs", "aaaa"; "Proj.fsproj", "bbbb" ]
    Shared = [ "Directory.Packages.props", "cccc" ]
    Sdk = "11.0.100"
    Command = [ "build"; "Proj.fsproj"; "-p:Optimize=false" ] }

/// A run dir holding a source and a project, the way the harness writes one.
let private runDirWith (source: string) : string =
  let dir = SageFs.Tests.RunnerDirs.scratchDir "fbc-run-"
  File.WriteAllText(Path.Combine(dir, "App.fs"), source)
  File.WriteAllText(Path.Combine(dir, "Proj.fsproj"), "<Project />")
  dir

/// The mtime the fake build gives its outputs: far older than any file the test writes after it.
let private builtAt = DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc)

/// A build that writes the outputs a real one does (a dll under bin, an assets file under obj) and counts its runs.
let private fakeBuild (runs: int ref) (dir: string) : Task<Result<unit, string>> = task {
  Threading.Interlocked.Increment runs |> ignore
  // Long enough for concurrent callers to pile up behind the first one.
  do! Task.Delay TestTimeouts.pollQuick
  let outputs = [ Path.Combine("bin", "Debug", "net11.0", "Proj.dll"); Path.Combine("obj", "project.assets.json") ]
  for relative in outputs do
    let path = Path.Combine(dir, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, "built from " + File.ReadAllText(Path.Combine(dir, "App.fs")))
    File.SetLastWriteTimeUtc(path, builtAt)
  return Ok ()
}

let private servedOf (result: Result<Served, string>) : Served =
  match result with
  | Ok served -> served
  | Error why -> failtestf "a provided build should succeed: %s" why

[<Tests>]
let tests =
  testList "Fixture build cache" [
    testList "the key" [
      testCase "the same inputs make the same key, and a key is a SHA-256 in hex" <| fun _ ->
        keyOf inputs |> Expect.equal "stable" (keyOf inputs)
        keyOf inputs |> String.length |> Expect.equal "64 hex digits" 64

      testCase "every input moves the key: a source, a project file, a shared repo file, the SDK, the command" <| fun _ ->
        let baseline = keyOf inputs
        let moved =
          [ "a source changed", { inputs with Tree = [ "App.fs", "zzzz"; "Proj.fsproj", "bbbb" ] }
            "a file renamed", { inputs with Tree = [ "Other.fs", "aaaa"; "Proj.fsproj", "bbbb" ] }
            "a file added", { inputs with Tree = ("Extra.fs", "dddd") :: inputs.Tree }
            "a shared repo file changed", { inputs with Shared = [ "Directory.Packages.props", "zzzz" ] }
            "the SDK changed", { inputs with Sdk = "10.0.200" }
            "the command changed", { inputs with Command = [ "build"; "Proj.fsproj" ] } ]
        for what, changed in moved do
          keyOf changed |> Expect.notEqual (sprintf "%s, so it is another build" what) baseline

      testCase "the order the files are listed in does not move the key" <| fun _ ->
        keyOf { inputs with Tree = List.rev inputs.Tree } |> Expect.equal "same files, same key" (keyOf inputs)

      testCase "a name and a digest that run together cannot be mistaken for another pair" <| fun _ ->
        let a = { inputs with Tree = [ "ab", "c" ] }
        let b = { inputs with Tree = [ "a", "bc" ] }
        keyOf a |> Expect.notEqual "ab+c is not a+bc" (keyOf b)
    ]

    testList "reading a run dir" [
      testCase "every file counts except what a build writes and what the case configures" <| fun _ ->
        let dir = runDirWith "let x = 1"
        for ignored in [ "bin"; "obj"; ".SageFs" ] do
          Directory.CreateDirectory(Path.Combine(dir, ignored)) |> ignore
          File.WriteAllText(Path.Combine(dir, ignored, "noise.txt"), "x")
        Directory.CreateDirectory(Path.Combine(dir, "sub")) |> ignore
        File.WriteAllText(Path.Combine(dir, "sub", "Deep.fs"), "let y = 2")
        treeOf dir
        |> List.map fst
        |> Expect.equal "the sources and the project, nested ones with a forward-slash name" [ "App.fs"; "Proj.fsproj"; "sub/Deep.fs" ]

      testCase "a changed byte in a file changes its digest" <| fun _ ->
        let dir = runDirWith "let x = 1"
        let before = treeOf dir
        File.WriteAllText(Path.Combine(dir, "App.fs"), "let x = 2")
        treeOf dir |> Expect.notEqual "the digest follows the content" before
    ]

    testList "providing a build" [
      testTask "two run dirs with the same sources build once, and the second gets the first's outputs" {
        let root = SageFs.Tests.RunnerDirs.scratchDir "fbc-cache-"
        let cache = Cache.create root
        let runs = ref 0
        let a = runDirWith "let x = 1"
        let b = runDirWith "let x = 1"
        let! first = provide cache inputs a (fakeBuild runs)
        let! second = provide cache inputs b (fakeBuild runs)
        servedOf first |> Expect.equal "the first caller's outputs come out of the cache too" Served.FromCache
        servedOf second |> Expect.equal "the second one is served from it" Served.FromCache
        runs.Value |> Expect.equal "one build" 1
        for dir in [ a; b ] do
          File.ReadAllText(Path.Combine(dir, "bin", "Debug", "net11.0", "Proj.dll"))
          |> Expect.equal "the dll the build wrote" "built from let x = 1"
      }

      testTask "the copies keep the file times the build left, because the host reads them" {
        let root = SageFs.Tests.RunnerDirs.scratchDir "fbc-cache-"
        let cache = Cache.create root
        let runs = ref 0
        let a = runDirWith "let x = 1"
        let b = runDirWith "let x = 1"
        let! _ = provide cache inputs a (fakeBuild runs)
        let! _ = provide cache inputs b (fakeBuild runs)
        for dir in [ a; b ] do
          File.GetLastWriteTimeUtc(Path.Combine(dir, "bin", "Debug", "net11.0", "Proj.dll"))
          |> Expect.equal "as old as the build left it" builtAt
      }

      testTask "other sources are another build, in a tree of their own" {
        let root = SageFs.Tests.RunnerDirs.scratchDir "fbc-cache-"
        let cache = Cache.create root
        let runs = ref 0
        let a = runDirWith "let x = 1"
        let b = runDirWith "let x = 2"
        let! _ = provide cache { inputs with Tree = treeOf a } a (fakeBuild runs)
        let! _ = provide cache { inputs with Tree = treeOf b } b (fakeBuild runs)
        runs.Value |> Expect.equal "two builds" 2
        File.ReadAllText(Path.Combine(b, "bin", "Debug", "net11.0", "Proj.dll"))
        |> Expect.equal "each run dir holds ITS sources' build" "built from let x = 2"
      }

      testTask "eight callers at once with the same key build once" {
        let root = SageFs.Tests.RunnerDirs.scratchDir "fbc-cache-"
        let cache = Cache.create root
        let runs = ref 0
        let dirs = [ for _ in 1 .. 8 -> runDirWith "let x = 1" ]
        let! served = dirs |> List.map (fun dir -> provide cache inputs dir (fakeBuild runs)) |> Task.WhenAll
        runs.Value |> Expect.equal "one build for all eight" 1
        served |> Array.map servedOf |> Array.distinct |> Expect.equal "all served from the cache" [| Served.FromCache |]
      }

      testTask "a build that fails is not cached: every caller builds in its own dir and sees its own failure" {
        let root = SageFs.Tests.RunnerDirs.scratchDir "fbc-cache-"
        let cache = Cache.create root
        let runs = ref 0
        let failing (_: string) : Task<Result<unit, string>> = task {
          Threading.Interlocked.Increment runs |> ignore
          return Error "error FS0001: the fixture does not compile"
        }
        let a = runDirWith "let x = 1"
        let b = runDirWith "let x = 1"
        let! first = provide cache inputs a failing
        let! second = provide cache inputs b failing
        for result in [ first; second ] do
          match result with
          | Error why -> why |> Expect.stringContains "the compiler's words reach the case" "FS0001"
          | Ok served -> failtestf "a failing build must fail the case, got %A" served
        runs.Value |> Expect.equal "the cached attempt and one real build per caller" 3
      }

      testTask "a cached tree that no longer matches what was built is not used: the caller builds for real" {
        let root = SageFs.Tests.RunnerDirs.scratchDir "fbc-cache-"
        let cache = Cache.create root
        let runs = ref 0
        let a = runDirWith "let x = 1"
        let b = runDirWith "let x = 1"
        let! _ = provide cache inputs a (fakeBuild runs)
        // Damage the cached tree behind the cache's back: a file the manifest lists is gone.
        let cached = Directory.GetFiles(root, "Proj.dll", SearchOption.AllDirectories)
        cached |> Array.length |> Expect.equal "the cache holds the build" 1
        File.Delete cached[0]
        let! second = provide cache inputs b (fakeBuild runs)
        servedOf second |> Expect.equal "built in place" Served.BuiltInPlace
        runs.Value |> Expect.equal "the damaged tree cost a second build" 2
        File.ReadAllText(Path.Combine(b, "bin", "Debug", "net11.0", "Proj.dll"))
        |> Expect.equal "and the run dir holds a good build" "built from let x = 1"
      }

      testTask "a run dir that already holds half a build is cleared before the cache copies in" {
        let root = SageFs.Tests.RunnerDirs.scratchDir "fbc-cache-"
        let cache = Cache.create root
        let runs = ref 0
        let a = runDirWith "let x = 1"
        let leftover = Path.Combine(a, "obj", "stale.cache")
        Directory.CreateDirectory(Path.GetDirectoryName leftover) |> ignore
        File.WriteAllText(leftover, "from nowhere")
        let! _ = provide cache inputs a (fakeBuild runs)
        File.Exists leftover |> Expect.isFalse "a build's outputs are exactly the cached ones"
      }

      testTask "disposing the cache removes its trees" {
        let root = SageFs.Tests.RunnerDirs.scratchDir "fbc-cache-"
        let cache = Cache.create root
        let runs = ref 0
        let a = runDirWith "let x = 1"
        let! _ = provide cache inputs a (fakeBuild runs)
        Directory.GetDirectories root |> Array.length |> Expect.equal "one tree" 1
        Cache.clear cache
        Directory.GetDirectories root |> Array.length |> Expect.equal "none left" 0
      }
    ]
  ]
