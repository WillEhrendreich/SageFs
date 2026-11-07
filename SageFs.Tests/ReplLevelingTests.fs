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
    TestRuns = TestRuns.Idle }

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
    TestRuns = (match rnd.Next 4 with | 0 -> TestRuns.InFlight(rnd.Next(1, 5)) | _ -> TestRuns.Idle) }

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
          TestRuns = TestRuns.InFlight 1 }
      |> Expect.equal
        "all five, activity first"
        [ LevelBlocker.EvalRunning
          LevelBlocker.ReplHoldsDefinitions 3
          LevelBlocker.InitScriptDefined "init.fsx"
          LevelBlocker.LiveTestsDefined
          LevelBlocker.TestsRunning 1 ]

    testProperty "WHY - for any facts the verdict is Level exactly when the blocker list is empty, and a Deferred is never empty" <| fun (seed: int) ->
      let facts = factsOfSeed seed
      let blockers = blockersOf facts
      let clean =
        facts.Activity = ReplActivity.Quiet
        && facts.EvalsSinceHost = 0
        && facts.InitScript = InitScript.Absent
        && facts.DynamicTests = DynamicTests.Absent
        && facts.TestRuns = TestRuns.Idle
      match ReplLeveling.decide facts, clean with
      | LevelVerdict.Level, true -> List.isEmpty blockers
      | LevelVerdict.Deferred listed, false -> not (List.isEmpty listed) && listed = blockers
      | _ -> false

    testCase "WHY - every blocker has its own wire token and a description that names what to do or what is in the way" <| fun _ ->
      let all =
        [ LevelBlocker.NotServing; LevelBlocker.EvalRunning; LevelBlocker.HostGone; LevelBlocker.ReplHoldsDefinitions 2
          LevelBlocker.InitScriptDefined "init.fsx"; LevelBlocker.LiveTestsDefined; LevelBlocker.TestsRunning 2
          LevelBlocker.LevelAlreadyRunning; LevelBlocker.SwapInProgress; LevelBlocker.GateDriven ]
      all |> List.map LevelBlocker.token |> List.distinct |> List.length |> Expect.equal "one token each" all.Length
      for blocker in all do
        LevelBlocker.describe blocker |> Expect.isNotEmpty (sprintf "%A is described" blocker)
      LevelBlocker.describe (LevelBlocker.InitScriptDefined "init.fsx") |> Expect.stringContains "names the script" "init.fsx"
      LevelBlocker.describe (LevelBlocker.ReplHoldsDefinitions 5) |> Expect.stringContains "carries the count" "5 evals"
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
  let gateOf (apply: (GateStanding -> GateStanding * GateEntry) -> GateStanding -> GateStanding * GateEntry) : Mutant<GateStanding -> GateStanding * GateEntry> =
    { Name = "admits during a level"; Description = "a run arriving while a level is pending or under way is admitted"; Apply = apply }
  testList "TestRunGate mutants" [
    detectsOutputMutant
      (gateOf (fun real standing ->
        match standing with
        | GateStanding.Leveling -> GateStanding.Leveling, GateEntry.Admitted
        | other -> real other))
      GateStanding.Leveling TestRunGate.enter (fun (_, entry) -> entry = GateEntry.Held)
    detectsOutputMutant
      (gateOf (fun real standing ->
        match standing with
        | GateStanding.LevelPending n -> GateStanding.LevelPending n, GateEntry.Admitted
        | other -> real other))
      (GateStanding.LevelPending 1) TestRunGate.enter (fun (_, entry) -> entry = GateEntry.Held)
  ]
