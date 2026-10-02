module LemScore.Tests.ProvenanceTests

open System
open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open LemScore
open LemScore.Types
open LemScore.Provenance
open LemScore.SharedDaemon
open LemScore.Prune
open LemScore.Tests.Samples

let private daemon = "0.6.875+d7e110776ff30c34db60a19949da26dadd940819"
let private bridgeNow = "0.6.875+72b286a92e88b44d974844a060f6438be097a644"

[<Tests>]
let skewTests =
  testList "bridge and daemon version skew" [
    testCase "the exact skew the verifier saw (d7e11077 daemon, 72b286a9 bridge) is Skewed" <| fun _ ->
      compareBuilds daemon bridgeNow |> Expect.equal "skewed" SkewedBuild

    testCase "the same commit is Same, and a short hash on one side still matches" <| fun _ ->
      compareBuilds daemon daemon |> Expect.equal "same" SameBuild
      compareBuilds daemon "0.6.875+d7e11077" |> Expect.equal "short hash" SameBuild

    testCase "a hash on one side only, or no version at all, proves nothing" <| fun _ ->
      compareBuilds daemon "0.6.875" |> Expect.equal "one side" UnknownBuild
      compareBuilds daemon "unknown" |> Expect.equal "unknown" UnknownBuild
      compareBuilds "unknown" "unknown" |> Expect.equal "both unknown" UnknownBuild

    testCase "versions with no hash compare as plain versions" <| fun _ ->
      compareBuilds "0.6.875" "0.6.875" |> Expect.equal "equal" SameBuild
      compareBuilds "0.6.875" "0.6.870" |> Expect.equal "different" SkewedBuild

    testCase "a version is found inside the free text `sagefs --version` prints" <| fun _ ->
      versionOf "SageFs 0.6.875+72b286a9 (net11.0)" |> Expect.equal "token" (Some "0.6.875+72b286a9")
      versionOf "no version here" |> Expect.isNone "nothing"

    testCase "only a skewed pair produces a Preflight finding" <| fun _ ->
      skewFellOver daemon bridgeNow |> List.map _.Stage |> Expect.equal "preflight" [ Preflight ]
      (skewFellOver daemon bridgeNow).Head.Evidence |> Expect.stringContains "both versions named" "d7e11077"
      skewFellOver daemon daemon |> Expect.isEmpty "same build, no finding"
      skewFellOver daemon "unknown" |> Expect.isEmpty "unknown, no claim"

    testCase "the match round-trips through its text" <| fun _ ->
      for m in [ SameBuild; SkewedBuild; UnknownBuild ] do
        VersionMatch.tryParse (VersionMatch.toString m) |> Expect.equal "round trip" (Ok m)
  ]

let private evalCall (turn: int) (tool: string) (input: string) =
  stream [ sagefsCall turn tool input "ok" ] finishedOk
  |> fun s -> s.Calls

[<Tests>]
let isolationTests =
  testList "the daemon-side tripwire" [
    testCase "a session created outside the run directory is noticed" <| fun _ ->
      let calls = evalCall 1 "create_project_session" """{"project":"/home/will/Work/nehemiah/Nehemiah.fsproj"}"""
      match outsideRunDir "/tmp/lem/run-01/w" calls with
      | [ f ] ->
        f.Stage |> Expect.equal "stage" Isolation
        f.Evidence |> Expect.stringContains "path named" "/home/will/Work/nehemiah/Nehemiah.fsproj"
      | other -> failtestf "expected one finding, got %A" other

    testCase "a session inside the run directory is not" <| fun _ ->
      let calls = evalCall 1 "create_project_session" """{"project":"/tmp/lem/run-01/w/DemoEnv/DemoEnv.fsproj"}"""
      outsideRunDir "/tmp/lem/run-01/w" calls |> Expect.isEmpty "inside"

    testCase "an eval that reads the user's home is noticed, one that works on its own data is not" <| fun _ ->
      let reads = evalCall 1 "send_fsharp_code" """{"code":"System.IO.File.ReadAllText \"/home/will/.ssh/id_ed25519\";;"}"""
      outsideRunDir "/tmp/lem/run-01/w" reads |> List.length |> Expect.equal "one finding" 1
      let pure' = evalCall 1 "send_fsharp_code" """{"code":"[1;2;3] |> List.sum;;"}"""
      outsideRunDir "/tmp/lem/run-01/w" pure' |> Expect.isEmpty "none"

    testCase "a prefix that only looks like the run directory is outside it" <| fun _ ->
      let calls = evalCall 1 "send_fsharp_code" """{"code":"File.ReadAllText \"/tmp/lem/run-01/w-other/x\""}"""
      // /tmp is not a watched root; only home, etc, var and friends are, so this is quiet by design.
      outsideRunDir "/tmp/lem/run-01/w" calls |> Expect.isEmpty "tmp is the sandbox's own scratch"
      let home = evalCall 1 "send_fsharp_code" """{"code":"File.ReadAllText \"/home/will/Work/x\""}"""
      outsideRunDir "/home/will/Work/xy" home |> List.length |> Expect.equal "prefix is not containment" 1

    testCase "tools that do not run in the daemon are not scanned" <| fun _ ->
      let calls = evalCall 1 "get_daemon_status" """{"note":"/home/will/x"}"""
      outsideRunDir "/tmp/lem/run-01/w" calls |> Expect.isEmpty "not scanned"

    testCase "the known limits name the daemon, the credential and, for editors, the driver" <| fun _ ->
      let cmdc = knownLimits Cmdc |> String.concat "\n"
      cmdc |> Expect.stringContains "daemon" "NOT inside the sandbox"
      cmdc |> Expect.stringContains "credential" "auth.json"
      cmdc.Contains "The editor driver" |> Expect.isFalse "no driver line for plain cmdc"
      (knownLimits CmdcVscode |> String.concat "\n") |> Expect.stringContains "driver" "The sandbox is the boundary"
  ]

[<Tests>]
let seenTests =
  testList "the watcher keeps every status" [
    let session id status : SessionInfo =
      { Id = id; Status = status; WorkingDirectory = "/tmp/lem/run-01/w"; ProjectPaths = []; Workflow = "REPL" }
    let t0 = DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)

    yield testCase "Starting, Ready, Busy, Ready, Disconnected is the history, not just the last" <| fun _ ->
      let readings = [ "Starting"; "Starting"; "Ready"; "Busy"; "Ready"; "Disconnected" ]
      let seen = readings |> List.fold (fun acc s -> observe t0 "/tmp/lem/run-01" acc [ session "a1" s ]) []
      seen |> List.map _.Statuses |> Expect.equal "history" [ [ "Starting"; "Ready"; "Busy"; "Ready"; "Disconnected" ] ]
      seen.Head.Seen.Status |> Expect.equal "last is still last" "Disconnected"

    yield testCase "a session that is not the run's is never recorded" <| fun _ ->
      let other = { session "z9" "Ready" with WorkingDirectory = "/home/will/Work/nehemiah" }
      observe t0 "/tmp/lem/run-01" [] [ other ] |> Expect.isEmpty "not ours"
  ]

let private runDir id finished bytes age : RunDir =
  { Id = id; Path = "/tmp/lem/" + id; Bytes = bytes; Finished = finished; AgeDays = age; EvidenceBytes = 1000L }

[<Tests>]
let pruneTests =
  let runs =
    [ runDir "old-a-01" true 400000000L 9.0
      runDir "old-b-02" true 400000000L 8.0
      runDir "new-c-03" true 400000000L 0.5
      runDir "live-d-04" false 100000000L 0.0 ]
  testList "pruning finished runs" [
    testCase "nothing is selected by default: no ids and no age is refused" <| fun _ ->
      Prune.select runs [] None |> Result.isError |> Expect.isTrue "refused"

    testCase "older-than selects only finished runs that old" <| fun _ ->
      match Prune.select runs [] (Some 7.0) with
      | Ok plan -> plan.Delete |> List.map _.Id |> Expect.equal "old ones" [ "old-a-01"; "old-b-02" ]
      | Error e -> failtest e

    testCase "a named run that is still running is kept, with the reason" <| fun _ ->
      match Prune.select runs [ "live-d-04" ] None with
      | Ok plan ->
        plan.Delete |> Expect.isEmpty "kept"
        plan.Skipped |> List.map fst |> Expect.equal "named" [ "live-d-04" ]
      | Error e -> failtest e

    testCase "a named run that does not exist, or is not a bare id, is reported not guessed" <| fun _ ->
      match Prune.select runs [ "nope"; "../w"; ".matrix-logs" ] None with
      | Ok plan ->
        plan.Delete |> Expect.isEmpty "nothing"
        plan.Skipped |> List.map fst |> Expect.equal "all three" [ "nope"; "../w"; ".matrix-logs" ]
      | Error e -> failtest e

    testCase "a named id and the age rule do not double count" <| fun _ ->
      match Prune.select runs [ "old-a-01"; "new-c-03" ] (Some 7.0) with
      | Ok plan -> plan.Delete |> List.map _.Id |> Expect.equal "union once" [ "old-a-01"; "new-c-03"; "old-b-02" ]
      | Error e -> failtest e

    testCase "a root that is not at least two segments deep is refused" <| fun _ ->
      Prune.plan "/tmp" DateTime.UtcNow [ "x" ] None |> Result.isError |> Expect.isTrue "/tmp"
      Prune.plan "/" DateTime.UtcNow [ "x" ] None |> Result.isError |> Expect.isTrue "/"
      Prune.plan "tmp/lem" DateTime.UtcNow [ "x" ] None |> Result.isError |> Expect.isTrue "relative"

    testCase "on a real directory only a finished named run is planned" <| fun _ ->
      let root = Path.Combine(Path.GetTempPath(), "lem-prune-test-" + Guid.NewGuid().ToString "N")
      try
        for id, finished in [ "done-01", true; "live-02", false ] do
          Directory.CreateDirectory(Path.Combine(root, id, "out")) |> ignore
          File.WriteAllText(Path.Combine(root, id, "w.txt"), String('x', 2048))
          if finished then File.WriteAllText(Path.Combine(root, id, "out", "summary.json"), "{}")
        match Prune.plan root DateTime.UtcNow [ "done-01"; "live-02" ] None with
        | Ok plan ->
          plan.Delete |> List.map _.Id |> Expect.equal "finished only" [ "done-01" ]
          (plan.Delete.Head.Bytes > 2000L) |> Expect.isTrue "sized"
          plan.Skipped |> List.map fst |> Expect.equal "live kept" [ "live-02" ]
        | Error e -> failtest e
      finally
        if Directory.Exists root then Directory.Delete(root, true)

    testCase "humanBytes reads in MB and GB" <| fun _ ->
      Prune.humanBytes 417000000L |> Expect.equal "mb" "398 MB"
      Prune.humanBytes 3221225472L |> Expect.equal "gb" "3.0 GB"
  ]
