module SageFs.Tests.FileWatcherTests

open Expecto
open SageFs.FileWatcher
open System.IO

[<Tests>]
let fileWatcherTests =
  testList "FileWatcher" [
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
  ]
