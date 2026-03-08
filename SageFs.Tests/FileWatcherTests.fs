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
    // When a FileSystemWatcher buffer overflows the Error event fires.
    // The handler synthesises a .fsproj change so the caller triggers a SoftReset —
    // the safest recovery when we don't know which specific files changed.
    testList "buffer overflow recovery" [
      testCase "synthetic .fsproj overflow change maps to SoftReset" <| fun () ->
        // The overflow handler creates a change with a .fsproj path.
        // Verify that fileChangeAction correctly routes it to SoftReset.
        let overflowChange = {
          FilePath = @"C:\Code\SomeProject\__overflow_recovery__.fsproj"
          Kind = FileChangeKind.Changed
          Timestamp = System.DateTimeOffset.UtcNow
        }
        overflowChange
        |> fileChangeAction
        |> Flip.Expect.equal "overflow recovery change should trigger SoftReset" FileChangeAction.SoftReset

      testCase "real .fsproj overflow change maps to SoftReset" <| fun () ->
        // The overflow handler uses the real .fsproj path when found by Directory.GetFiles.
        let overflowChange = {
          FilePath = @"C:\Code\SomeProject\SomeProject.fsproj"
          Kind = FileChangeKind.Changed
          Timestamp = System.DateTimeOffset.UtcNow
        }
        overflowChange
        |> fileChangeAction
        |> Flip.Expect.equal "real fsproj overflow should trigger SoftReset" FileChangeAction.SoftReset

      testCase "shouldTriggerRebuild accepts overflow recovery path" <| fun () ->
        // Ensure the synthetic path passes shouldTriggerRebuild so it reaches the callback.
        let config = defaultWatchConfig [@"C:\Code\SomeProject"]
        shouldTriggerRebuild config @"C:\Code\SomeProject\__overflow_recovery__.fsproj"
        |> Flip.Expect.isTrue "overflow recovery path should pass shouldTriggerRebuild"
    ]
  ]
