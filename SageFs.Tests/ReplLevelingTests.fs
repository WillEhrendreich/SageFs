/// A session brings its FSI host up to the patched build by itself when that loses nothing. This is the pure rule: what counts
/// as "nothing to lose" (`ReplLeveling.decide`), and the gate that keeps a test run and a reset of the host off each other
/// (`TestRunGate`). The actor, the worker and the manager that use them have their own tests and simulations.
module SageFs.Tests.ReplLevelingTests

open Expecto
open Expecto.Flip
open FsCheck
open MutationTestingFramework
open SageFs

let private quiet : LevelFacts =
  { Activity = ReplActivity.Quiet
    EvalsSinceHost = 0
    InitScript = InitScript.Absent
    DynamicTests = DynamicTests.Absent
    TestRuns = TestRuns.Idle
    LastReset = ResetCost.NeverReset }

let private blockersOf (facts: LevelFacts) : LevelBlocker list =
  match ReplLeveling.decide facts with
  | LevelVerdict.Level -> []
  | LevelVerdict.Deferred blockers -> blockers

/// Facts built from a seed, every shape reachable: the property tests replay from the seed.
let private factsOfSeed (seed: int) : LevelFacts =
  let rnd = System.Random seed
  { Activity = [| ReplActivity.NotServing; ReplActivity.Quiet; ReplActivity.Quiet; ReplActivity.EvalInFlight; ReplActivity.HostGone |].[rnd.Next 5]
    EvalsSinceHost = (match rnd.Next 3 with | 0 -> 0 | 1 -> 0 | _ -> rnd.Next(1, 40))
    InitScript = (match rnd.Next 4 with | 0 -> InitScript.Defined ".SageFs/init.fsx" | _ -> InitScript.Absent)
    DynamicTests = (match rnd.Next 4 with | 0 -> DynamicTests.Defined | _ -> DynamicTests.Absent)
    TestRuns = (match rnd.Next 4 with | 0 -> TestRuns.InFlight(rnd.Next(1, 5)) | _ -> TestRuns.Idle)
    LastReset = (match rnd.Next 4 with | 0 -> ResetCost.ResetTook (System.TimeSpan.FromSeconds (float (rnd.Next(0, 40)))) | _ -> ResetCost.NeverReset) }

[<Tests>]
let decideTests =
  testList "ReplLeveling.decide" [
    testCase "WHY - an idle REPL with no evals, no init script, no dynamic tests and no runs levels, because nothing in the host would be lost" <| fun _ ->
      ReplLeveling.decide quiet |> Expect.equal "level" LevelVerdict.Level

    testCase "WHY - a running eval blocks, and so does a cancelled one that has not stopped, because the reset would kill it" <| fun _ ->
      blockersOf { quiet with Activity = ReplActivity.EvalInFlight } |> Expect.equal "named" [ LevelBlocker.EvalRunning ]

    testCase "WHY - a dead host and a session that is not serving block, because there is no live host to bring up" <| fun _ ->
      blockersOf { quiet with Activity = ReplActivity.HostGone } |> Expect.equal "host gone" [ LevelBlocker.HostGone ]
      blockersOf { quiet with Activity = ReplActivity.NotServing } |> Expect.equal "not serving" [ LevelBlocker.NotServing ]

    testCase "WHY - one user eval since the host was built blocks and the count is carried, because its definitions would be lost" <| fun _ ->
      blockersOf { quiet with EvalsSinceHost = 1 } |> Expect.equal "one eval" [ LevelBlocker.ReplHoldsDefinitions 1 ]
      blockersOf { quiet with EvalsSinceHost = 7 } |> Expect.equal "seven evals" [ LevelBlocker.ReplHoldsDefinitions 7 ]

    testCase "WHY - a value the init script defined blocks and names the script, because running it again repeats what it did" <| fun _ ->
      blockersOf { quiet with InitScript = InitScript.Defined ".SageFs/init.fsx" } |> Expect.equal "init script" [ LevelBlocker.InitScriptDefined ".SageFs/init.fsx" ]

    testCase "WHY - tests an editor buffer defined in the host block, because the worker's run-test slot points into that host" <| fun _ ->
      blockersOf { quiet with DynamicTests = DynamicTests.Defined } |> Expect.equal "live tests" [ LevelBlocker.LiveTestsDefined ]

    testCase "WHY - a test run in flight blocks and its count is carried, because a reset would kill it" <| fun _ ->
      blockersOf { quiet with TestRuns = TestRuns.InFlight 2 } |> Expect.equal "two runs" [ LevelBlocker.TestsRunning 2 ]

    testCase "WHY - every blocker is listed together, in a fixed order, so the person told to reset by hand is told all it costs" <| fun _ ->
      blockersOf
        { Activity = ReplActivity.EvalInFlight
          EvalsSinceHost = 3
          InitScript = InitScript.Defined "init.fsx"
          DynamicTests = DynamicTests.Defined
          TestRuns = TestRuns.InFlight 1
          LastReset = ResetCost.NeverReset }
      |> Expect.equal
        "all five, activity first"
        [ LevelBlocker.EvalRunning
          LevelBlocker.ReplHoldsDefinitions 3
          LevelBlocker.InitScriptDefined "init.fsx"
          LevelBlocker.LiveTestsDefined
          LevelBlocker.TestsRunning 1 ]

    testCase "WHY - a project whose last host reset took longer than the automatic level's budget is not leveled, and the blocker carries how long it took, so the banner can say what a level costs" <| fun _ ->
      let slow = FixtureDurations.largeProjectReset
      blockersOf { quiet with LastReset = ResetCost.ResetTook slow } |> Expect.equal "SageFs.Tests' 26 s level is left to a person" [ LevelBlocker.LevelWouldTakeTooLong slow ]

    testCase "WHY - a small project's level still happens by itself: the 2.8 s level measured on the run_app fixture and a host never reset are both under the budget" <| fun _ ->
      ReplLeveling.decide { quiet with LastReset = ResetCost.ResetTook (FixtureDurations.smallProjectReset) } |> Expect.equal "2.8 s levels" LevelVerdict.Level
      ReplLeveling.decide { quiet with LastReset = ResetCost.NeverReset } |> Expect.equal "a host that has not reset yet levels once, and records" LevelVerdict.Level

    testCase "WHY - the budget's own boundary levels, and a millisecond over does not, so the comparison is pinned" <| fun _ ->
      ReplLeveling.decide { quiet with LastReset = ResetCost.ResetTook Timeouts.replLevelCostBudget } |> Expect.equal "exactly the budget" LevelVerdict.Level
      let over = Timeouts.replLevelCostBudget + FixtureDurations.fastResult
      blockersOf { quiet with LastReset = ResetCost.ResetTook over } |> Expect.equal "a millisecond over" [ LevelBlocker.LevelWouldTakeTooLong over ]

    testProperty "WHY - for any facts the verdict is Level exactly when the blocker list is empty, and a Deferred is never empty" <| fun (seed: int) ->
      let facts = factsOfSeed seed
      let blockers = blockersOf facts
      let clean =
        facts.Activity = ReplActivity.Quiet
        && facts.EvalsSinceHost = 0
        && facts.InitScript = InitScript.Absent
        && facts.DynamicTests = DynamicTests.Absent
        && facts.TestRuns = TestRuns.Idle
        && (match facts.LastReset with | ResetCost.NeverReset -> true | ResetCost.ResetTook took -> took <= Timeouts.replLevelCostBudget)
      match ReplLeveling.decide facts, clean with
      | LevelVerdict.Level, true -> List.isEmpty blockers
      | LevelVerdict.Deferred listed, false -> not (List.isEmpty listed) && listed = blockers
      | _ -> false

    testCase "WHY - every blocker has its own wire token and a description that names what to do or what is in the way" <| fun _ ->
      let all =
        [ LevelBlocker.NotServing; LevelBlocker.EvalRunning; LevelBlocker.HostGone; LevelBlocker.ReplHoldsDefinitions 2
          LevelBlocker.InitScriptDefined "init.fsx"; LevelBlocker.LiveTestsDefined; LevelBlocker.TestsRunning 2
          LevelBlocker.LevelAlreadyRunning; LevelBlocker.SwapInProgress; LevelBlocker.GateDriven
          LevelBlocker.LevelWouldTakeTooLong (FixtureDurations.largeProjectReset) ]
      all |> List.map LevelBlocker.token |> List.distinct |> List.length |> Expect.equal "one token each" all.Length
      for blocker in all do
        LevelBlocker.describe blocker |> Expect.isNotEmpty (sprintf "%A is described" blocker)
      LevelBlocker.describe (LevelBlocker.InitScriptDefined "init.fsx") |> Expect.stringContains "names the script" "init.fsx"
      LevelBlocker.describe (LevelBlocker.ReplHoldsDefinitions 5) |> Expect.stringContains "carries the count" "5 evals"
      LevelBlocker.describe (LevelBlocker.LevelWouldTakeTooLong (FixtureDurations.largeProjectReset)) |> Expect.stringContains "carries how long" "26 s"
  ]

/// Each mutant breaks exactly one branch of the rule, and an assertion above must notice.
[<Tests>]
let decideMutationTests =
  let mutate (name: string) (description: string) (apply: (LevelFacts -> LevelVerdict) -> LevelFacts -> LevelVerdict) : Mutant<LevelFacts -> LevelVerdict> =
    { Name = name; Description = description; Apply = apply }
  let ignoring (clear: LevelFacts -> LevelFacts) = fun real facts -> real (clear facts)
  let isLevel (v: LevelVerdict) = (v = LevelVerdict.Level)
  testList "ReplLeveling.decide mutants" [
    detectsOutputMutant
      (mutate "ignores a running eval" "an eval in flight no longer blocks" (ignoring (fun f -> { f with Activity = ReplActivity.Quiet })))
      { quiet with Activity = ReplActivity.EvalInFlight } ReplLeveling.decide (fun v -> not (isLevel v))
    detectsOutputMutant
      (mutate "ignores a dead host" "a dead host no longer blocks" (ignoring (fun f -> { f with Activity = ReplActivity.Quiet })))
      { quiet with Activity = ReplActivity.HostGone } ReplLeveling.decide (fun v -> not (isLevel v))
    detectsOutputMutant
      (mutate "off by one on evals" "exactly one eval no longer counts as a definition" (ignoring (fun f -> { f with EvalsSinceHost = max 0 (f.EvalsSinceHost - 1) })))
      { quiet with EvalsSinceHost = 1 } ReplLeveling.decide (fun v -> not (isLevel v))
    detectsOutputMutant
      (mutate "ignores the init script" "a value the init script defined no longer blocks" (ignoring (fun f -> { f with InitScript = InitScript.Absent })))
      { quiet with InitScript = InitScript.Defined "init.fsx" } ReplLeveling.decide (fun v -> not (isLevel v))
    detectsOutputMutant
      (mutate "ignores dynamic tests" "tests defined from a buffer no longer block" (ignoring (fun f -> { f with DynamicTests = DynamicTests.Absent })))
      { quiet with DynamicTests = DynamicTests.Defined } ReplLeveling.decide (fun v -> not (isLevel v))
    detectsOutputMutant
      (mutate "ignores a run in flight" "a test run in flight no longer blocks" (ignoring (fun f -> { f with TestRuns = TestRuns.Idle })))
      { quiet with TestRuns = TestRuns.InFlight 1 } ReplLeveling.decide (fun v -> not (isLevel v))
    detectsOutputMutant
      (mutate "ignores the cost" "a reset that took 26 s no longer blocks the level" (ignoring (fun f -> { f with LastReset = ResetCost.NeverReset })))
      { quiet with LastReset = ResetCost.ResetTook (FixtureDurations.largeProjectReset) } ReplLeveling.decide (fun v -> not (isLevel v))
    detectsOutputMutant
      (mutate "budget boundary shifted" "a reset of exactly the budget plus a millisecond is taken as within it" (ignoring (fun f -> match f.LastReset with | ResetCost.ResetTook took -> { f with LastReset = ResetCost.ResetTook (took - FixtureDurations.fastResult - FixtureDurations.fastResult) } | ResetCost.NeverReset -> f)))
      { quiet with LastReset = ResetCost.ResetTook (Timeouts.replLevelCostBudget + FixtureDurations.fastResult) } ReplLeveling.decide (fun v -> not (isLevel v))
    detectsOutputMutant
      (mutate "levels on a deferred list" "a Deferred with blockers is reported as Level" (fun real facts -> match real facts with | LevelVerdict.Deferred _ -> LevelVerdict.Level | other -> other))
      { quiet with EvalsSinceHost = 4; TestRuns = TestRuns.InFlight 1 } ReplLeveling.decide (fun v -> not (isLevel v))
  ]

// -- the gate -------------------------------------------------------------------------------------------------------

[<Tests>]
let gateTests =
  testList "TestRunGate" [
    testCase "WHY - runs enter and leave one at a time, and the last to leave drains the gate" <| fun _ ->
      let one, entry1 = TestRunGate.enter GateStanding.Idle
      let two, entry2 = TestRunGate.enter one
      (entry1, entry2) |> Expect.equal "both admitted" (GateEntry.Admitted, GateEntry.Admitted)
      two |> Expect.equal "two in flight" (GateStanding.Running 2)
      let afterFirst, exit1 = TestRunGate.leave two
      afterFirst |> Expect.equal "one left" (GateStanding.Running 1)
      exit1 |> Expect.equal "others still run" GateExit.Left
      let afterLast, exit2 = TestRunGate.leave afterFirst
      afterLast |> Expect.equal "idle" GateStanding.Idle
      exit2 |> Expect.equal "drained" GateExit.Drained

    testCase "WHY - a level asked of an idle gate starts at once, and a run arriving during it is held, never dropped and never run on the old host" <| fun _ ->
      let leveling, request = TestRunGate.requestLevel GateStanding.Idle
      request |> Expect.equal "starts" GateLevelRequest.Start
      leveling |> Expect.equal "leveling" GateStanding.Leveling
      let after, entry = TestRunGate.enter leveling
      entry |> Expect.equal "held" GateEntry.Held
      after |> Expect.equal "the gate is unchanged by a held run" GateStanding.Leveling
      TestRunGate.finishLevel after |> Expect.equal "the level ends and the gate is open again" GateStanding.Idle

    testCase "WHY - a level asked while runs are in flight waits for the last, and a run arriving meanwhile is held so a steady stream cannot starve it" <| fun _ ->
      let pending, request = TestRunGate.requestLevel (GateStanding.Running 2)
      request |> Expect.equal "waits for two" (GateLevelRequest.WaitFor 2)
      pending |> Expect.equal "pending" (GateStanding.LevelPending 2)
      TestRunGate.enter pending |> snd |> Expect.equal "a new run is held" GateEntry.Held
      let afterOne, exit1 = TestRunGate.leave pending
      exit1 |> Expect.equal "not the last" GateExit.Left
      let afterLast, exit2 = TestRunGate.leave afterOne
      exit2 |> Expect.equal "the last run lets the level start" GateExit.LevelMayStart
      afterLast |> Expect.equal "leveling" GateStanding.Leveling

    testCase "WHY - a second level asked while one is pending or under way is told so, and changes nothing" <| fun _ ->
      for standing in [ GateStanding.LevelPending 1; GateStanding.Leveling ] do
        let after, request = TestRunGate.requestLevel standing
        request |> Expect.equal "already requested" GateLevelRequest.AlreadyRequested
        after |> Expect.equal "unchanged" standing

    testCase "WHY - a leave with nothing running is a stray and changes nothing, so a double leave cannot open the gate under a level" <| fun _ ->
      for standing in [ GateStanding.Idle; GateStanding.Leveling ] do
        let after, exit = TestRunGate.leave standing
        exit |> Expect.equal "stray" GateExit.NotRunning
        after |> Expect.equal "unchanged" standing

    testCase "WHY - finishing a level moves only a gate that is leveling" <| fun _ ->
      for standing in [ GateStanding.Idle; GateStanding.Running 1; GateStanding.LevelPending 1 ] do
        TestRunGate.finishLevel standing |> Expect.equal "unchanged" standing

    testCase "WHY - the runs a level's facts count are those in flight, and a level that has begun has none" <| fun _ ->
      TestRunGate.runsOf (GateStanding.Running 2) |> Expect.equal "running" (TestRuns.InFlight 2)
      TestRunGate.runsOf (GateStanding.LevelPending 1) |> Expect.equal "pending" (TestRuns.InFlight 1)
      TestRunGate.runsOf GateStanding.Idle |> Expect.equal "idle" TestRuns.Idle
      TestRunGate.runsOf GateStanding.Leveling |> Expect.equal "leveling" TestRuns.Idle

    testProperty "WHY - however enters, leaves and level requests interleave, a run is admitted only when no level is pending or under way, and a level starts only with nothing in flight" <| fun (seed: int) ->
      let rnd = System.Random seed
      let mutable standing = GateStanding.Idle
      let mutable ok = true
      for _ in 1 .. 60 do
        let before = standing
        match rnd.Next 4 with
        | 0 ->
          let after, entry = TestRunGate.enter standing
          standing <- after
          match entry, before with
          | GateEntry.Admitted, (GateStanding.LevelPending _ | GateStanding.Leveling) -> ok <- false
          | GateEntry.Held, (GateStanding.Idle | GateStanding.Running _) -> ok <- false
          | _ -> ()
        | 1 -> standing <- TestRunGate.leave standing |> fst
        | 2 ->
          let after, request = TestRunGate.requestLevel standing
          standing <- after
          match request, before with
          | GateLevelRequest.Start, GateStanding.Idle -> ()
          | GateLevelRequest.Start, _ -> ok <- false
          | _ -> ()
        | _ -> standing <- TestRunGate.finishLevel standing
        match standing with
        | GateStanding.Running n | GateStanding.LevelPending n when n < 1 -> ok <- false
        | _ -> ()
      ok
  ]

[<Tests>]
let gateMutationTests =
  let gateOf (name: string) (description: string) (apply: (GateStanding -> GateStanding * GateEntry) -> GateStanding -> GateStanding * GateEntry) : Mutant<GateStanding -> GateStanding * GateEntry> =
    { Name = name; Description = description; Apply = apply }
  testList "TestRunGate mutants" [
    detectsOutputMutant
      (gateOf "admits during a level" "a run arriving while a level is under way is admitted" (fun real standing ->
        match standing with
        | GateStanding.Leveling -> GateStanding.Leveling, GateEntry.Admitted
        | other -> real other))
      GateStanding.Leveling TestRunGate.enter (fun (_, entry) -> entry = GateEntry.Held)
    detectsOutputMutant
      (gateOf "admits behind a pending level" "a run arriving while a level waits for the last run is admitted, so a stream of runs starves it" (fun real standing ->
        match standing with
        | GateStanding.LevelPending n -> GateStanding.LevelPending n, GateEntry.Admitted
        | other -> real other))
      (GateStanding.LevelPending 1) TestRunGate.enter (fun (_, entry) -> entry = GateEntry.Held)
  ]

/// What happens to the tests and maps a worker reports again, by why they were asked for. A level's fetch describes the worker's
/// own new host, so which worker gave it decides; the registry may read `Starting` for a while (a status read writes the worker's
/// own report into it while the host is replaced) without making the answer wrong.
[<Tests>]
let refetchFateTests =
  let handle (pid: int) : WorkerProtocol.WorkerHandle = { Pid = pid; Port = Some 5000 }
  let noSwap : WorkerReadyCommit.ParkedSwap<obj> = WorkerReadyCommit.ParkedSwap.NoSwap
  let parkedSwap : WorkerReadyCommit.ParkedSwap<obj> = WorkerReadyCommit.ParkedSwap.Parked (obj ())
  let starting (pid: int) = WorkerProtocol.SessionLifecycleStatus.Starting (handle pid)
  let ready (pid: int) = WorkerProtocol.SessionLifecycleStatus.Ready (handle pid)
  let judge origin pid status = WorkerPostReady.RefetchGate.judgeRefetch origin pid (Some (status, noSwap))
  // Every shape of the registry that names a worker, and two that name none.
  let registryReadings (pid: int) : WorkerProtocol.SessionLifecycleStatus list =
    [ starting pid
      ready pid
      WorkerProtocol.SessionLifecycleStatus.Building ("rebuilding", handle pid)
      WorkerProtocol.SessionLifecycleStatus.Faulted (WorkerProtocol.FaultReason.report "boom")
      WorkerProtocol.SessionLifecycleStatus.Stopped ]

  testList "RefetchGate.judgeRefetch" [
    testCase "WHY - a level's refetch from the worker that still serves is kept while the registry reads Starting, as the level's own answer is" <| fun _ ->
      judge WorkerPostReady.RefetchOrigin.AfterLevel 4242 (starting 4242)
      |> Expect.equal "the level's tests and maps are kept" WorkerPostReady.ReplyFate.Current
      WorkerPostReady.RefetchGate.judgeLevelAnswer 4242 (Some (starting 4242))
      |> Expect.equal "and so is the level's own answer" WorkerPostReady.ReplyFate.Current

    testCase "a level's refetch from a worker that has been replaced is dropped" <| fun _ ->
      judge WorkerPostReady.RefetchOrigin.AfterLevel 4242 (ready 4243)
      |> Expect.equal "the new worker's tests and maps are not the old one's" WorkerPostReady.ReplyFate.Stale

    testCase "a level's refetch for a session that is gone is dropped" <| fun _ ->
      WorkerPostReady.RefetchGate.judgeRefetch WorkerPostReady.RefetchOrigin.AfterLevel 4242 (None: (WorkerProtocol.SessionLifecycleStatus * WorkerReadyCommit.ParkedSwap<obj>) option)
      |> Expect.equal "nothing it was asked can still matter" WorkerPostReady.ReplyFate.Stale

    testCase "a swap parked behind the worker does not make a level's refetch wrong, because it describes that worker's host" <| fun _ ->
      WorkerPostReady.RefetchGate.judgeRefetch WorkerPostReady.RefetchOrigin.AfterLevel 4242 (Some (ready 4242, parkedSwap))
      |> Expect.equal "kept" WorkerPostReady.ReplyFate.Current

    testCase "a refetch of a Ready worker is still kept only while the registry reads that worker Ready" <| fun _ ->
      judge WorkerPostReady.RefetchOrigin.OfReadyWorker 4242 (ready 4242)
      |> Expect.equal "Ready, same worker" WorkerPostReady.ReplyFate.Current
      judge WorkerPostReady.RefetchOrigin.OfReadyWorker 4242 (starting 4242)
      |> Expect.equal "Starting is not Ready" WorkerPostReady.ReplyFate.Stale

    testProperty "a level's refetch and the level's own answer are judged alike, whatever the registry reads" <| fun (asked: int) (serving: int) ->
      [ asked; serving ]
      |> List.collect registryReadings
      |> List.forall (fun status ->
        let refetch = judge WorkerPostReady.RefetchOrigin.AfterLevel asked status
        let answer = WorkerPostReady.RefetchGate.judgeLevelAnswer asked (Some status)
        refetch = answer)
  ]
