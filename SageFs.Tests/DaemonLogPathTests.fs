module SageFs.Tests.DaemonLogPathTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs

let private userLogDir = Path.Combine(Path.GetTempPath(), "sagefs-user-default-logs")

let private isUnder (parent: string) (child: string) =
  let p = Path.GetFullPath parent |> fun s -> s.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
  Path.GetFullPath(child).StartsWith(p, StringComparison.Ordinal)

/// Two distinct isolated data dirs, neither nested in the other.
let private twoDataDirs : Arbitrary<string * string> =
  ArbMap.defaults
  |> ArbMap.generate<Guid * Guid>
  |> Gen.filter (fun (a, b) -> a <> b)
  |> Gen.map (fun (a, b) ->
    Path.Combine(Path.GetTempPath(), "d-" + a.ToString "N"), Path.Combine(Path.GetTempPath(), "d-" + b.ToString "N"))
  |> Arb.fromGen

[<Tests>]
let daemonLogPathTests =
  testList "Daemon log location follows the data dir" [
    testCase "WHY — an unset or blank SAGEFS_DATA_DIR is the user's own state, not an isolated dir" <| fun _ ->
      for value in [ null; ""; "   " ] do
        DataDirChoice.ofEnvValue value
        |> Expect.equal (sprintf "%A is not an isolation request" value) DataDirChoice.UserDefault

    testCase "WHY — a named SAGEFS_DATA_DIR is an isolation request, resolved to a full path" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "iso")
      DataDirChoice.ofEnvValue dir
      |> Expect.equal "isolated at the full path" (DataDirChoice.Isolated (Path.GetFullPath dir))

    testCase "WHY — with no isolation the log stays where existing users already have theirs" <| fun _ ->
      DaemonLog.directory DataDirChoice.UserDefault userLogDir
      |> Expect.equal "user default log dir" userLogDir

    testProperty "WHY — two daemons with different SAGEFS_DATA_DIR never write into each other's log directory" <| fun () ->
      Prop.forAll twoDataDirs (fun (a, b) ->
        let logA = DaemonLog.directory (DataDirChoice.ofEnvValue a) userLogDir
        let logB = DaemonLog.directory (DataDirChoice.ofEnvValue b) userLogDir
        let fileA = DaemonLog.fileOn logA (DateOnly(2026, 9, 30))
        let fileB = DaemonLog.fileOn logB (DateOnly(2026, 9, 30))
        logA <> logB
        && not (isUnder logA fileB)
        && not (isUnder logB fileA)
        && isUnder a fileA
        && isUnder b fileB)

    testCase "WHY — an isolated daemon never falls back to the user's real log directory" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "iso-2")
      let logDir = DaemonLog.directory (DataDirChoice.ofEnvValue dir) userLogDir
      isUnder userLogDir (DaemonLog.fileOn logDir (DateOnly(2026, 9, 30)))
      |> Expect.isFalse "an isolated daemon's log is not in the user's directory"

    testCase "WHY — the printed path is the file that exists: date-suffixed, exactly as the sink names it" <| fun _ ->
      DaemonLog.fileOn "/logs" (DateOnly(2026, 9, 30))
      |> Expect.equal "stem + yyyyMMdd + extension" (Path.Combine("/logs", "mcp-server20260930.log"))

    testCase "WHY — the sink is handed a path whose date-suffixed form is the printed one" <| fun _ ->
      let sinkPath = DaemonLog.sinkPath "/logs"
      Path.Combine(Path.GetDirectoryName sinkPath |> Option.ofObj |> Option.defaultValue "",
                   Path.GetFileNameWithoutExtension sinkPath + "20260930" + Path.GetExtension sinkPath)
      |> Expect.equal "inserting the date before the extension gives fileOn" (DaemonLog.fileOn "/logs" (DateOnly(2026, 9, 30)))

    testCase "WHY — a day that rolled reports the rolled file, because that is the one being written" <| fun _ ->
      let day = DateOnly(2026, 9, 30)
      let named n = "mcp-server20260930" + n + ".log"
      DaemonLog.activeFileOn "/logs" day [ named ""; named "_001" ]
      |> Expect.equal "the highest roll wins" (Path.Combine("/logs", named "_001"))
      DaemonLog.activeFileOn "/logs" day [ named "_002"; named ""; named "_001" ]
      |> Expect.equal "order of the listing does not matter" (Path.Combine("/logs", named "_002"))

    testCase "WHY — before the sink has written anything, the reported file is the day's first" <| fun _ ->
      DaemonLog.activeFileOn "/logs" (DateOnly(2026, 9, 30)) []
      |> Expect.equal "falls back to fileOn" (DaemonLog.fileOn "/logs" (DateOnly(2026, 9, 30)))

    testCase "WHY — other days, other files and malformed roll suffixes are never mistaken for today's log" <| fun _ ->
      let day = DateOnly(2026, 9, 30)
      DaemonLog.activeFileOn "/logs" day
        [ "mcp-server20260929_009.log"; "daemon-stdout.log"; "mcp-server20260930_x.log"; "mcp-server20260930_.log" ]
      |> Expect.equal "none of them is today's" (DaemonLog.fileOn "/logs" day)

    testCase "WHY — the log bound is finite, named, and small enough that 7 days cannot fill a disk" <| fun _ ->
      let b = DaemonLog.defaultBounds
      b.MaxFileBytes |> Expect.equal "50 MB per file" (50L * 1024L * 1024L)
      b.RetainedFiles |> Expect.equal "7 files kept" 7
      ((b.MaxFileBytes * int64 b.RetainedFiles) < 1024L * 1024L * 1024L)
      |> Expect.isTrue "worst case total is under a gibibyte"
  ]
