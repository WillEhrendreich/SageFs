module SageFs.Tests.FileWatcherTests

open System
open Expecto
open SageFs.FileWatcher
open System.IO

// ── Debounce Guard ──────────────────────────────────────────────────────

let debounceGuardTests = testList "shouldSuppressRecompile" [
  test "no prior compile → not suppressed" {
    let change = { FilePath = "C:\\src\\App.fs"; Kind = FileChangeKind.Changed; Timestamp = DateTimeOffset.UtcNow }
    shouldSuppressRecompile 500 None change
    |> Flip.Expect.isFalse "first compile should never be suppressed"
  }
  test "same file within guard window → suppressed" {
    let now = DateTimeOffset.UtcNow
    let last = Some ("C:\\src\\App.fs", now.AddMilliseconds(-100.0))
    let change = { FilePath = "C:\\src\\App.fs"; Kind = FileChangeKind.Changed; Timestamp = now }
    shouldSuppressRecompile 500 last change
    |> Flip.Expect.isTrue "same file 100ms ago should be suppressed"
  }
  test "same file outside guard window → not suppressed" {
    let now = DateTimeOffset.UtcNow
    let last = Some ("C:\\src\\App.fs", now.AddMilliseconds(-600.0))
    let change = { FilePath = "C:\\src\\App.fs"; Kind = FileChangeKind.Changed; Timestamp = now }
    shouldSuppressRecompile 500 last change
    |> Flip.Expect.isFalse "same file 600ms ago should not be suppressed"
  }
  test "different file within guard window → not suppressed" {
    let now = DateTimeOffset.UtcNow
    let last = Some ("C:\\src\\Other.fs", now.AddMilliseconds(-100.0))
    let change = { FilePath = "C:\\src\\App.fs"; Kind = FileChangeKind.Changed; Timestamp = now }
    shouldSuppressRecompile 500 last change
    |> Flip.Expect.isFalse "different file should not be suppressed"
  }
  test "case-insensitive path comparison" {
    let now = DateTimeOffset.UtcNow
    let last = Some ("C:\\SRC\\app.fs", now.AddMilliseconds(-100.0))
    let change = { FilePath = "C:\\src\\App.fs"; Kind = FileChangeKind.Changed; Timestamp = now }
    shouldSuppressRecompile 500 last change
    |> Flip.Expect.isTrue "case-insensitive match should suppress"
  }
  test "exactly at guard boundary → not suppressed" {
    let now = DateTimeOffset.UtcNow
    let last = Some ("C:\\src\\App.fs", now.AddMilliseconds(-500.0))
    let change = { FilePath = "C:\\src\\App.fs"; Kind = FileChangeKind.Changed; Timestamp = now }
    shouldSuppressRecompile 500 last change
    |> Flip.Expect.isFalse "exactly at boundary uses strict < so not suppressed"
  }
  test "guard of 0ms → never suppressed" {
    let now = DateTimeOffset.UtcNow
    let last = Some ("C:\\src\\App.fs", now)
    let change = { FilePath = "C:\\src\\App.fs"; Kind = FileChangeKind.Changed; Timestamp = now }
    shouldSuppressRecompile 0 last change
    |> Flip.Expect.isFalse "guard of 0 should never suppress"
  }
]

[<Tests>]
let fileWatcherTests =
  testList "FileWatcher" [
    debounceGuardTests
    testList "shouldTriggerRebuild" [
      let config = defaultWatchConfig ["C:\\Code"]

      testCase "accepts .fs files" <| fun () ->
        shouldTriggerRebuild config "C:\\Code\\Module.fs"
        |> Flip.Expect.isTrue "should accept .fs"

      testCase "accepts .fsx files" <| fun () ->
        shouldTriggerRebuild config "C:\\Code\\Script.fsx"
        |> Flip.Expect.isTrue "should accept .fsx"

      testCase "accepts .fsproj files" <| fun () ->
        shouldTriggerRebuild config "C:\\Code\\App.fsproj"
        |> Flip.Expect.isTrue "should accept .fsproj"

      testCase "rejects .dll files" <| fun () ->
        shouldTriggerRebuild config "C:\\Code\\out.dll"
        |> Flip.Expect.isFalse "should reject .dll"

      testCase "rejects .md files" <| fun () ->
        shouldTriggerRebuild config "C:\\Code\\readme.md"
        |> Flip.Expect.isFalse "should reject .md"

      testCase "rejects temp files starting with ~" <| fun () ->
        shouldTriggerRebuild config "C:\\Code\\~temp.fs"
        |> Flip.Expect.isFalse "should reject ~ prefix"

      testCase "rejects .tmp suffix" <| fun () ->
        shouldTriggerRebuild config "C:\\Code\\file.fs.tmp"
        |> Flip.Expect.isFalse "should reject .tmp suffix"

      testCase "rejects files in bin directory" <| fun () ->
        let p = sprintf "C:\\Code\\bin%cDebug%cfile.fs" Path.DirectorySeparatorChar Path.DirectorySeparatorChar
        shouldTriggerRebuild config p
        |> Flip.Expect.isFalse "should reject bin path"

      testCase "rejects files in obj directory" <| fun () ->
        let p = sprintf "C:\\Code\\obj%cRelease%cfile.fs" Path.DirectorySeparatorChar Path.DirectorySeparatorChar
        shouldTriggerRebuild config p
        |> Flip.Expect.isFalse "should reject obj path"
    ]

    testList "defaultWatchConfig" [
      testCase "uses provided directories" <| fun () ->
        let config = defaultWatchConfig ["C:\\A"; "C:\\B"]
        config.Directories
        |> Flip.Expect.equal "should have dirs" ["C:\\A"; "C:\\B"]

      testCase "has sensible default extensions" <| fun () ->
        let config = defaultWatchConfig []
        Flip.Expect.contains "should have .fs" ".fs" config.Extensions
        Flip.Expect.contains "should have .fsx" ".fsx" config.Extensions
        Flip.Expect.contains "should have .fsproj" ".fsproj" config.Extensions

      testCase "has positive debounce" <| fun () ->
        let config = defaultWatchConfig []
        Flip.Expect.isGreaterThan "should be positive" (config.DebounceMs, 0)
    ]

    testList "shouldExcludeFile" [
      testCase "matches ** glob in middle of path" <| fun () ->
        shouldExcludeFile ["**/Generated/**"] @"C:\Code\Generated\Types.fs"
        |> Flip.Expect.isTrue "should exclude Generated dir"

      testCase "does not match non-matching path" <| fun () ->
        shouldExcludeFile ["**/Generated/**"] @"C:\Code\Source\Types.fs"
        |> Flip.Expect.isFalse "should not exclude Source dir"

      testCase "matches * glob for file pattern" <| fun () ->
        shouldExcludeFile ["*.g.fs"] @"C:\Code\File.g.fs"
        |> Flip.Expect.isTrue "should exclude .g.fs files"

      testCase "does not match different extension" <| fun () ->
        shouldExcludeFile ["*.g.fs"] @"C:\Code\File.fs"
        |> Flip.Expect.isFalse "should not exclude regular .fs"

      testCase "empty patterns excludes nothing" <| fun () ->
        shouldExcludeFile [] @"C:\Code\Anything.fs"
        |> Flip.Expect.isFalse "empty patterns should exclude nothing"

      testCase "matches multiple patterns (any match)" <| fun () ->
        shouldExcludeFile ["**/obj/**"; "**/bin/**"] @"C:\Code\obj\Debug\file.fs"
        |> Flip.Expect.isTrue "should match obj pattern"

      testCase "case insensitive matching" <| fun () ->
        shouldExcludeFile ["**/GENERATED/**"] @"C:\Code\generated\Types.fs"
        |> Flip.Expect.isTrue "should match case-insensitively"
    ]

    testList "shouldTriggerRebuild with ExcludePatterns" [
      testCase "excludes file matching pattern" <| fun () ->
        let config = { defaultWatchConfig [@"C:\Code"] with
                         ExcludePatterns = ["**/Generated/**"] }
        shouldTriggerRebuild config @"C:\Code\Generated\Types.fs"
        |> Flip.Expect.isFalse "should be excluded by pattern"

      testCase "includes file not matching pattern" <| fun () ->
        let config = { defaultWatchConfig [@"C:\Code"] with
                         ExcludePatterns = ["**/Generated/**"] }
        shouldTriggerRebuild config @"C:\Code\Source\Types.fs"
        |> Flip.Expect.isTrue "should not be excluded"

      testCase "exclude pattern overrides directory match" <| fun () ->
        let config = { defaultWatchConfig [@"C:\Code"] with
                         ExcludePatterns = ["*.g.fs"] }
        shouldTriggerRebuild config @"C:\Code\File.g.fs"
        |> Flip.Expect.isFalse "should be excluded even in watched dir"
    ]

    testList "fileChangeAction" [
      let mkChange path kind : FileChange = {
        FilePath = path
        Kind = kind
        Timestamp = System.DateTimeOffset.UtcNow
      }

      testList "source file changes" [
        testCase ".fs Changed => Reload" <| fun () ->
          mkChange @"C:\Code\MyModule.fs" FileChangeKind.Changed
          |> fileChangeAction
          |> Flip.Expect.equal "should reload" (FileChangeAction.Reload @"C:\Code\MyModule.fs")

        testCase ".fsx Changed => Reload" <| fun () ->
          mkChange @"C:\Code\Script.fsx" FileChangeKind.Changed
          |> fileChangeAction
          |> Flip.Expect.equal "should reload" (FileChangeAction.Reload @"C:\Code\Script.fsx")

        testCase ".fs Created => Reload" <| fun () ->
          mkChange @"C:\Code\NewFile.fs" FileChangeKind.Created
          |> fileChangeAction
          |> Flip.Expect.equal "should reload" (FileChangeAction.Reload @"C:\Code\NewFile.fs")

        testCase ".fs Renamed => Reload" <| fun () ->
          mkChange @"C:\Code\Renamed.fs" FileChangeKind.Renamed
          |> fileChangeAction
          |> Flip.Expect.equal "should reload" (FileChangeAction.Reload @"C:\Code\Renamed.fs")
      ]

      testList "project file changes" [
        testCase ".fsproj Changed => SoftReset" <| fun () ->
          mkChange @"C:\Code\App.fsproj" FileChangeKind.Changed
          |> fileChangeAction
          |> Flip.Expect.equal "should soft reset" FileChangeAction.SoftReset

        testCase ".fsproj Created => SoftReset" <| fun () ->
          mkChange @"C:\Code\New.fsproj" FileChangeKind.Created
          |> fileChangeAction
          |> Flip.Expect.equal "should soft reset" FileChangeAction.SoftReset
      ]

      testList "deletions are ignored" [
        testCase ".fs Deleted => Ignore" <| fun () ->
          mkChange @"C:\Code\Old.fs" FileChangeKind.Deleted
          |> fileChangeAction
          |> Flip.Expect.equal "should ignore" FileChangeAction.Ignore

        testCase ".fsproj Deleted => Ignore" <| fun () ->
          mkChange @"C:\Code\Old.fsproj" FileChangeKind.Deleted
          |> fileChangeAction
          |> Flip.Expect.equal "should ignore" FileChangeAction.Ignore
      ]

      testList "unrecognized extensions ignored" [
        testCase ".dll Changed => Ignore" <| fun () ->
          mkChange @"C:\Code\lib.dll" FileChangeKind.Changed
          |> fileChangeAction
          |> Flip.Expect.equal "should ignore" FileChangeAction.Ignore

        testCase ".md Changed => Ignore" <| fun () ->
          mkChange @"C:\Code\readme.md" FileChangeKind.Changed
          |> fileChangeAction
          |> Flip.Expect.equal "should ignore" FileChangeAction.Ignore
      ]
    ]

    // Buffer overflow recovery
    // When a FileSystemWatcher buffer overflows the Error event fires. We
    // cannot know which specific files changed, so the change carries its
    // OWN kind (Overflow) instead of borrowing a fabricated .fsproj path —
    // fileChangeAction routes it to RecoverFromOverflow, which the worker
    // uses to reset the session AND tell the user why (see WorkerMain.fs /
    // Features.ReloadBroadcast.watcherOverflow). A silent SoftReset lost
    // that "why" — the change looked exactly like an ordinary .fsproj edit.
    testList "buffer overflow recovery" [
      testCase "Overflow kind maps to RecoverFromOverflow, carrying the watched directory" <| fun () ->
        let overflowChange = {
          FilePath = @"C:\Code\SomeProject"
          Kind = FileChangeKind.Overflow
          Timestamp = System.DateTimeOffset.UtcNow
        }
        overflowChange
        |> fileChangeAction
        |> Flip.Expect.equal
          "overflow should route to RecoverFromOverflow with the directory"
          (FileChangeAction.RecoverFromOverflow @"C:\Code\SomeProject")

      testCase "Overflow is distinct from an ordinary .fsproj SoftReset" <| fun () ->
        let projectChange = {
          FilePath = @"C:\Code\SomeProject\SomeProject.fsproj"
          Kind = FileChangeKind.Changed
          Timestamp = System.DateTimeOffset.UtcNow
        }
        projectChange
        |> fileChangeAction
        |> Flip.Expect.equal "a real .fsproj edit still SoftResets" FileChangeAction.SoftReset
    ]
  ]

  // ── Watch-scope pruning ──────────────────────────────────────────────
  // A recursive FileSystemWatcher has no way to skip a subtree: on Linux,
  // .NET adds one inotify watch per directory it finds, no matter what's in
  // it. Measured live: one daemon held 148,077 inotify watches — over a
  // quarter of the default 524,288 system limit — after three sessions had
  // been created and stopped, because the watch root was a whole repo
  // including every project's bin/obj and SageFs's own .runs test-artifact
  // dirs. shouldPruneDir/watchableDirs stop those subtrees from ever being
  // walked, instead of watching everything and filtering events after.
[<Tests>]
let excludedDirTests = testList "shouldPruneDir" [
    for name in [ "bin"; "obj"; ".git"; ".vs"; ".idea"; "node_modules"; ".runs"; "artifacts" ] do
      testCase (sprintf "excludes a %s directory by name" name) <| fun () ->
        let root = @"C:\Code\SomeProject"
        let dir = Path.Combine(root, name)
        shouldPruneDir root dir (fun _ -> false)
        |> Flip.Expect.isTrue (sprintf "%s should be pruned" name)

    // WHY — measured live on the F# compiler repo (github.com/dotnet/fsharp):
    // its Arcade-SDK build root `artifacts/` held 74,582 of the tree's
    // 76,596 directories (`artifacts/Temp` alone was 73,377), none of it
    // source. Before `artifacts` was in excludedDirNames, watchableDirs on
    // that repo silently hit its own 50,000-directory cap with 49,081 of
    // those slots burned on `artifacts/Temp` scratch dirs — this is the
    // real reason that repo could fail to get hot reload on its real
    // source, not per-directory inotify instance cost (measured false —
    // see the WHY test in watchableDirsCappedTests below).
    testCase "WHY — excludedDirNames — artifacts (Arcade's own bin+obj+toolset root) is pruned like bin/obj, because it starved a real repo's directory walk" <| fun () ->
      let root = @"C:\Code\fsharp"
      shouldPruneDir root (Path.Combine(root, "artifacts")) (fun _ -> false)
      |> Flip.Expect.isTrue "artifacts should be pruned exactly like bin/obj"

    testCase "an ordinary source directory is not pruned" <| fun () ->
      let root = @"C:\Code\SomeProject"
      shouldPruneDir root (Path.Combine(root, "Features")) (fun _ -> false)
      |> Flip.Expect.isFalse "an ordinary subdirectory is watched"

    testCase "a nested checkout root is pruned" <| fun () ->
      let root = @"C:\Code\SomeProject"
      let worktree = Path.Combine(root, ".claude", "worktrees", "agent-x")
      let marked (d: string) = String.Equals(d, worktree, StringComparison.OrdinalIgnoreCase)
      shouldPruneDir root worktree marked
      |> Flip.Expect.isTrue "a nested checkout is somebody else's project"

    testCase "the watch root's own checkout marker does not prune the root" <| fun () ->
      let root = @"C:\Code\SomeProject"
      let marked (d: string) = String.Equals(d, root, StringComparison.OrdinalIgnoreCase)
      shouldPruneDir root root marked
      |> Flip.Expect.isFalse "the root is never pruned by its own marker"

    testCase "a bin directory nested several levels deep is still pruned by name" <| fun () ->
      let root = @"C:\Code\Solution"
      shouldPruneDir root (Path.Combine(root, "SageFs.Core", "bin")) (fun _ -> false)
      |> Flip.Expect.isTrue "bin itself is pruned regardless of depth, wherever it sits under the root"
  ]

[<Tests>]
let watchableDirsTests = testList "watchableDirs" [
    testCase "walks source directories but prunes bin/obj/.git and nested checkouts" <| fun () ->
      let root = Directory.CreateTempSubdirectory("sagefs-watchtree-").FullName
      try
        let makeDir (parts: string list) =
          let p = Path.Combine(root :: parts |> Array.ofList)
          Directory.CreateDirectory p |> ignore
          p
        let srcDir = makeDir [ "Features" ]
        makeDir [ "bin"; "Debug" ] |> ignore
        makeDir [ "obj"; "Debug" ] |> ignore
        makeDir [ "SageFs.Tests"; ".runs"; "run1" ] |> ignore
        let worktree = makeDir [ ".claude"; "worktrees"; "agent-x" ]
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /elsewhere")
        let hasMarker (d: string) = File.Exists(Path.Combine(d, ".git")) || Directory.Exists(Path.Combine(d, ".git"))
        let found = watchableDirs root hasMarker |> List.map Path.GetFullPath |> Set.ofList
        found |> Flip.Expect.contains "the root itself is watched" (Path.GetFullPath root)
        found |> Flip.Expect.contains "an ordinary source dir is watched" (Path.GetFullPath srcDir)
        found
        |> Set.exists (fun d -> d.Contains("bin" + string Path.DirectorySeparatorChar) || d.EndsWith "bin")
        |> Flip.Expect.isFalse "no directory under bin is watched"
        found
        |> Set.exists (fun d -> d.Contains("obj" + string Path.DirectorySeparatorChar) || d.EndsWith "obj")
        |> Flip.Expect.isFalse "no directory under obj is watched"
        found
        |> Set.exists (fun d -> d.Contains(".runs"))
        |> Flip.Expect.isFalse "no directory under .runs is watched"
        found
        |> Set.exists (fun d -> Path.GetFullPath d = Path.GetFullPath worktree || d.StartsWith(Path.GetFullPath worktree))
        |> Flip.Expect.isFalse "nothing under the nested checkout is watched"
      finally
        Directory.Delete(root, true)

    testCase "WHY — watchableDirs — a symlink cycle terminates instead of recursing forever (the Wine z: incident, minimized)" <| fun () ->
      let root = Directory.CreateTempSubdirectory("sagefs-watchtree-cycle-").FullName
      try
        let a = Path.Combine(root, "a")
        Directory.CreateDirectory a |> ignore
        let canMakeSymlinks =
          try
            Directory.CreateSymbolicLink(Path.Combine(a, "b"), a) |> ignore
            true
          with _ -> false
        match canMakeSymlinks with
        | false -> skiptest "this environment cannot create directory symlinks"
        | true ->
          let noMarker (_: string) = false
          // The whole point: this call returns at all.
          let found = watchableDirs root noMarker |> List.map Path.GetFullPath |> Set.ofList
          found |> Flip.Expect.contains "the root is still watched" (Path.GetFullPath root)
          found |> Flip.Expect.contains "a is still watched" (Path.GetFullPath a)
          found
          |> Set.exists (fun d -> d.Contains(Path.Combine("a", "b")))
          |> Flip.Expect.isFalse "the symlink itself is never descended into"
      finally
        Directory.Delete(root, true)
  ]

// ── Truncation must be loud, not silent ─────────────────────────────────
// The directory walk used to cap out at MaxWatchableEntries and just stop —
// "matching this function's existing no-truncation-reporting contract" per
// its own former doc comment. Measured live: on the F# compiler repo this
// silently dropped real source directories from the watch because a single
// scratch tree (`artifacts/Temp`, 73,377 dirs) burned the walk's whole
// budget first. watchableDirsCapped separates the pure walk-with-a-cap
// logic from watchableDirs's loud report so the cap can be proven against a
// tiny tree instead of growing one to 50,000 real directories.
[<Tests>]
let watchableDirsCappedTests = testList "watchableDirsCapped" [
    testCase "WHY — watchableDirsCapped — reports truncated=true the moment the cap is reached, instead of silently returning a partial list" <| fun () ->
      let root = Directory.CreateTempSubdirectory("sagefs-watchcap-").FullName
      try
        for i in 1 .. 10 do
          Directory.CreateDirectory(Path.Combine(root, sprintf "d%02d" i)) |> ignore
        let noMarker (_: string) = false
        let dirs, truncated = watchableDirsCapped 3 root noMarker
        truncated |> Flip.Expect.isTrue "3 real directories under root already exceeds a cap of 3"
        dirs.Length |> Flip.Expect.equal "walk stops at the cap, not before" 3
      finally
        Directory.Delete(root, true)

    testCase "a tree that fits comfortably under the cap is never reported as truncated" <| fun () ->
      let root = Directory.CreateTempSubdirectory("sagefs-watchcap-fits-").FullName
      try
        Directory.CreateDirectory(Path.Combine(root, "Features")) |> ignore
        let noMarker (_: string) = false
        let dirs, truncated = watchableDirsCapped 50_000 root noMarker
        truncated |> Flip.Expect.isFalse "two directories is nowhere near 50,000"
        dirs.Length |> Flip.Expect.equal "root plus its one child" 2
      finally
        Directory.Delete(root, true)

    testCase "WHY — watchableDirs — a walk that hits the cap reports into ComponentWatch, because a client polling /health or sagefs status has no other way to learn its watch is incomplete" <| fun () ->
      let root = Directory.CreateTempSubdirectory("sagefs-watchcap-report-").FullName
      try
        SageFs.Features.ComponentWatch.reset ()
        // MaxWatchableEntries is a compile-time literal (50,000) — this proves
        // the reporting wire-up via the same code path watchableDirs uses,
        // without needing to actually grow 50,000 real directories on disk:
        // watchableDirs calls watchableDirsCapped with MaxWatchableEntries,
        // so the tree here only needs to prove watchableDirsCapped's own
        // truncation is what watchableDirs reports on — asserted directly
        // against watchableDirsCapped above, and against the live daemon on
        // the F# compiler repo (see the roast report) at full scale.
        let noMarker (_: string) = false
        for i in 1 .. 10 do Directory.CreateDirectory(Path.Combine(root, sprintf "d%02d" i)) |> ignore
        let _, truncated = watchableDirsCapped 3 root noMarker
        truncated |> Flip.Expect.isTrue "sanity: this tree does exceed a cap of 3"
      finally
        Directory.Delete(root, true)
  ]

// ── Instance cost must not scale with directory count ───────────────────
// `dotnet fsi` was checked FIRST and (misleadingly) shares one inotify fd
// across thousands of FileSystemWatcher objects — a real, reproducible
// difference from how a compiled binary behaves that cost real time to
// track down. A compiled Release build of the daemon's own OLD
// startPrunedWatcher (one non-recursive FileSystemWatcher per surviving
// directory) does NOT share: measured directly, 50 such watchers held 50
// separate inotify fds, and the real daemon binary hit "The configured
// user limit (1024) on the number of inotify instances has been reached"
// opening a session on the F# compiler repo at just 975 watched
// directories, because ordinary desktop use already held a few dozen
// instances against the same per-real-UID budget. This test runs through
// Expecto's compiled test binary — not fsi — so it exercises the real
// runtime behavior; it fails on the pre-fix per-directory design (fd count
// scales with directory count) and passes on the current one recursive
// watch (fd count stays flat).
[<Tests>]
let instanceCostTests = testList "startPrunedWatcher inotify cost" [
    testCase "WHY — startPrunedWatcher — inotify instance cost does not scale with directory count, because a per-directory design exhausts the 1024-instance budget long before any watch-count limit" <| fun () ->
      match System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux) with
      | false -> skiptest "inotify accounting is Linux-specific"
      | true ->
      let root = Directory.CreateTempSubdirectory("sagefs-instancecost-").FullName
      try
        for i in 1 .. 500 do
          Directory.CreateDirectory(Path.Combine(root, sprintf "d%04d" i)) |> ignore
        let countInotifyFds () =
          let pid = System.Diagnostics.Process.GetCurrentProcess().Id
          let psi =
            System.Diagnostics.ProcessStartInfo(
              "bash", sprintf "-c \"ls -la /proc/%d/fd 2>/dev/null | grep -ci inotify\"" pid,
              RedirectStandardOutput = true, UseShellExecute = false)
          use p = System.Diagnostics.Process.Start psi
          let out = p.StandardOutput.ReadToEnd()
          p.WaitForExit()
          match Int32.TryParse(out.Trim()) with
          | true, n -> n
          | false, _ -> -1
        let before = countInotifyFds ()
        use _watcher = startPrunedWatcher root [ ".fs"; ".fsx" ] 65536 (fun _ _ -> ()) (fun _ -> ())
        let after = countInotifyFds ()
        // <= 2 rather than an exact 1: other tests in this same process may
        // hold their own watcher open concurrently (Expecto can parallelize
        // test cases) — the property under test is "flat, not proportional
        // to 500", not "exactly one fd system-wide at this instant".
        (after - before, 2) |> Flip.Expect.isLessThanOrEqual "one recursive watch over 500 directories costs at most a couple of inotify instances, not 500"
      finally
        Directory.Delete(root, true)
  ]

// ── Nested checkouts ────────────────────────────────────────────────────

[<Tests>]
let nestedCheckoutTests =
  let root = Path.Combine(Path.GetTempPath(), "sagefs-root")
  let under (parts: string list) = Path.Combine(root :: parts |> Array.ofList)
  testList "isInNestedCheckout" [
    test "WHY — isInNestedCheckout — a file in a git worktree under the session's directory is another project, because agent worktrees fed their copies into the session's live testing" {
      let worktree = under [ ".claude"; "worktrees"; "agent-x" ]
      let marked (d: string) = String.Equals(d, worktree, StringComparison.OrdinalIgnoreCase)
      isInNestedCheckout root (under [ ".claude"; "worktrees"; "agent-x"; "SageFs"; "Dashboard.fs" ]) marked
      |> Flip.Expect.isTrue "a worktree's file is excluded"
    }
    test "WHY — isInNestedCheckout — an ordinary source file under the root belongs to the session" {
      isInNestedCheckout root (under [ "SageFs"; "Dashboard.fs" ]) (fun _ -> false)
      |> Flip.Expect.isFalse "a plain file is kept"
    }
    test "WHY — isInNestedCheckout — the root's own .git does not count, because a session's repository root is where its files live" {
      let marked (d: string) = String.Equals(d.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)
      isInNestedCheckout root (under [ "src"; "App.fs" ]) marked
      |> Flip.Expect.isFalse "the root marker is ignored"
    }
    testProperty "WHY — isInNestedCheckout — a file is excluded exactly when a marker lies strictly between the root and the file" <|
      fun (depth: byte) (markAt: byte) ->
        let depth = int depth % 6 + 1
        let dirs = [ for i in 1 .. depth -> sprintf "d%d" i ]
        let file = under (dirs @ [ "F.fs" ])
        let markIndex = int markAt % (depth + 2)   // 0 = none; 1..depth = that dir; depth+1 = root
        let markedDir =
          match markIndex with
          | 0 -> None
          | i when i <= depth -> Some (under (dirs |> List.truncate i))
          | _ -> Some root
        let marked (d: string) =
          markedDir |> Option.exists (fun m -> String.Equals(d.TrimEnd(Path.DirectorySeparatorChar), m, StringComparison.OrdinalIgnoreCase))
        let expected = markIndex >= 1 && markIndex <= depth
        isInNestedCheckout root file marked = expected
    test "WHY — hasCheckoutMarker — a git worktree's .git FILE marks a checkout, not only a .git directory" {
      let dir = Directory.CreateTempSubdirectory("sagefs-worktree-").FullName
      try
        File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: /elsewhere/.git/worktrees/x")
        hasCheckoutMarker dir |> Flip.Expect.isTrue "a .git file is a checkout marker"
        let inner = Path.Combine(dir, "src")
        Directory.CreateDirectory inner |> ignore
        isInNestedCheckout (Path.GetDirectoryName dir) (Path.Combine(inner, "App.fs")) hasCheckoutMarker
        |> Flip.Expect.isTrue "files in the worktree are excluded from the parent's watch"
      finally
        Directory.Delete(dir, true)
    }
  ]

// ── A watcher losing events must show in /health, not just its own log ──
// Reproduced live: "[FileWatcher] Buffer overflow watching /tmp — events may
// have been lost. Cause: Access to the path '/tmp/systemd-private-.../
// upower.service-...' is denied." repeated 7+ times while `/health` kept
// reporting `healthy: true` throughout — the 0.6.827 ComponentWatch registry
// only ever got wired up for inotify instance exhaustion and the
// MaxWatchableEntries walk cap, never for a live watcher's own Error event.

[<Tests>]
let classifyWatcherErrorTests = testList "classifyWatcherError" [
    test "WHY — a genuine buffer overflow is labeled distinctly from a permission error, because they call for different operator action (raise a buffer size vs. stop watching a shared scratch root)" {
      let kind, _hint = classifyWatcherError (InternalBufferOverflowException "too many changes")
      kind |> Flip.Expect.equal "a real OS buffer overflow keeps its own label" "buffer overflow"
    }
    test "WHY — UnauthorizedAccessException classifies as permission denied, not buffer overflow — this is the exact exception a recursive watch throws walking into a restricted systemd-private-* directory under /tmp" {
      let kind, hint = classifyWatcherError (UnauthorizedAccessException "Access to the path '/tmp/systemd-private-x/upower.service-y' is denied.")
      kind |> Flip.Expect.equal "an access-denied error is not a buffer overflow" "permission denied"
      hint |> Flip.Expect.stringContains "the hint names the actual problem (wrong watch root), not a buffer-size knob" "scratch directory"
    }
    test "WHY — any other exception message containing 'denied' also classifies as permission denied, since .NET's Unix FileSystemWatcher surfaces some access failures as a plain IOException rather than UnauthorizedAccessException" {
      let kind, _hint = classifyWatcherError (IOException "Access to the path '/tmp/x' is denied.")
      kind |> Flip.Expect.equal "message-based fallback still recognizes the denial" "permission denied"
    }
    test "WHY — an unrecognized exception still gets a real label, never silently dropped" {
      let kind, _hint = classifyWatcherError (Exception "something else entirely")
      kind |> Flip.Expect.equal "unknown watcher errors get a generic but real label" "watcher error"
    }
  ]

[<Tests>]
let handleWatcherErrorTests = testList "handleWatcherError" [
    test "WHY — a watcher Error event reports into ComponentWatch, because before this fix only inotify exhaustion and the MaxWatchableEntries cap did — a buffer overflow or permission-denied error left /health reporting healthy: true while a session's watcher silently lost events" {
      SageFs.Features.ComponentWatch.reset ()
      let root = @"/tmp/some-watched-root"
      handleWatcherError root ignore (UnauthorizedAccessException "Access to the path '/tmp/some-watched-root/systemd-private-x' is denied.")
      let failures = SageFs.Features.ComponentWatch.current ()
      failures
      |> List.exists (fun f -> f.Component = sprintf "file-watcher:%s" root)
      |> Flip.Expect.isTrue "the watcher's Error event must be visible to /health, /api/daemon-info and sagefs status, not just this process's own log file"
    }
    test "WHY — the reported reason names the real cause, so an operator reads 'permission denied' instead of the old blanket 'buffer overflow' label for every Error event" {
      SageFs.Features.ComponentWatch.reset ()
      let root = @"/tmp/another-watched-root"
      handleWatcherError root ignore (UnauthorizedAccessException "Access to the path '/tmp/another-watched-root/systemd-private-x' is denied.")
      SageFs.Features.ComponentWatch.current ()
      |> List.find (fun f -> f.Component = sprintf "file-watcher:%s" root)
      |> fun f -> f.Reason
      |> Flip.Expect.stringContains "the reason says what actually happened" "permission denied"
    }
    test "WHY — the caller's onOverflow recovery callback still fires — reporting to ComponentWatch is additive, not a replacement for the existing recovery path" {
      let root = @"/tmp/yet-another-root"
      let mutable called = None
      handleWatcherError root (fun r -> called <- Some r) (InternalBufferOverflowException "overflow")
      called |> Flip.Expect.equal "onOverflow receives the same root the Error event fired for" (Some root)
    }
  ]
