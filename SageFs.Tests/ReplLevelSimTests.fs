module SageFs.Tests.ReplLevelSimTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.ReplLevelSim
open SageFs.Simulation.ReplLevelInvariants

/// DST for a session bringing its own FSI host level. The real `EvalActorDecision.decide`, the real `TestRunGate`, the real
/// `ReplFreshness` transitions and the real pid judgement are folded; nothing is reimplemented. Each twin breaks one
/// invariant, and the test beside it requires that violation, so a green run of the real fold means something.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION (%s) - replay:\n  Seed=%d\n  Ops=%A\n  Violations=%A" t.Reducer t.Scenario.Seed t.Scenario.Ops vs

let private idsOf (t: Trace) : string list = violations t |> List.map (fun v -> v.Split(':').[0])

/// The twin must break `invariantId` on `scenario`, which the real fold must not.
let private teeth (twin: Policy) (invariantId: string) (scenario: Scenario) : Test =
  testCase (sprintf "WHY - %s breaks %s on its scenario, so the invariant has teeth" twin.Name invariantId) <| fun _ ->
    assertHolds (run scenario)
    idsOf (runWith twin scenario) |> Expect.contains (sprintf "the twin violates %s" invariantId) invariantId

[<Tests>]
let tests =
  testList "DST repl leveling" [
    testList "the real protocol holds the invariants" [
      testPropertyWithConfig simConfig "seeded scenarios - no level resets a host under work or over definitions, held runs are served, answers are judged by pid, and level means level" <|
        fun (seed: int) -> assertHolds (run (ReplLevelGenerators.fromSeed seed))

      testCase "a clean host with one save levels, and the state ends InSync once the answer arrives" <| fun _ ->
        let t = run ReplLevelGenerators.cleanLevel
        assertHolds t
        t.Final.Freshness |> Expect.equal "level" ReplFreshness.InSync
        t.Final.Generation |> Expect.notEqual "the host was replaced" SageFs.EvalActorDecision.SessionGeneration.initial

      testCase "a level asked while a run is in flight waits for the run and then acts" <| fun _ ->
        let t = run ReplLevelGenerators.levelWaitsForRun
        assertHolds t
        t.Final.RunsKilledByReset |> Expect.equal "the run ended before the reset" 0
        t.Final.Freshness |> Expect.equal "level" ReplFreshness.InSync

      testCase "a run that arrives while the level is pending is held, then served on the new host" <| fun _ ->
        let t = run ReplLevelGenerators.runArrivesDuringLevel
        assertHolds t
        t.Final.Held |> Expect.equal "nothing is left held" 0
        t.Final.RunsServed |> Expect.equal "both runs were served" 2
        t.Final.RunsOnHost |> List.length |> Expect.equal "the held run is on the host" 1

      testCase "a REPL that ran an eval is never leveled: the answer defers and the state says what held it" <| fun _ ->
        let t = run ReplLevelGenerators.evalThenLevel
        assertHolds t
        match t.Final.Freshness with
        | ReplFreshness.CannotLevel (_, _, [ LevelBlocker.ReplHoldsDefinitions 1 ]) -> ()
        | other -> failtestf "expected CannotLevel with the eval named, got %A" other

      testCase "a value the init script defined is never leveled over, and the state names the script" <| fun _ ->
        let t = run ReplLevelGenerators.initScriptThenLevel
        assertHolds t
        match t.Final.Freshness with
        | ReplFreshness.CannotLevel (_, _, [ LevelBlocker.InitScriptDefined _ ]) -> ()
        | other -> failtestf "expected CannotLevel naming the init script, got %A" other

      testCase "tests an editor buffer defined are never leveled over" <| fun _ ->
        let t = run ReplLevelGenerators.dynamicTestsThenLevel
        assertHolds t
        match t.Final.Freshness with
        | ReplFreshness.CannotLevel (_, _, [ LevelBlocker.LiveTestsDefined ]) -> ()
        | other -> failtestf "expected CannotLevel naming the live tests, got %A" other

      testCase "a project whose reset is slow levels once and records the cost, and the next level is left to a person" <| fun _ ->
        let t = run ReplLevelGenerators.largeProjectLevelsOnce
        assertHolds t
        match t.Final.Freshness with
        | ReplFreshness.CannotLevel (_, _, [ LevelBlocker.LevelWouldTakeTooLong took ]) -> (took > Timeouts.replLevelCostBudget) |> Expect.isTrue "the cost it names is over the budget"
        | other -> failtestf "expected CannotLevel naming the cost, got %A" other
        t.Final.Resets |> List.filter (fun r -> r.Decision = SageFs.EvalActorDecision.EvalDecision.RunLevel) |> List.length |> Expect.equal "the host was reset by a level once" 1

      testCase "a small project's resets never hold a level back, however often it is asked" <| fun _ ->
        let t = run ReplLevelGenerators.smallProjectLevelsTwice
        assertHolds t
        t.Final.Freshness |> Expect.equal "level" ReplFreshness.InSync
        t.Final.Resets |> List.filter (fun r -> r.Decision = SageFs.EvalActorDecision.EvalDecision.RunLevel) |> List.length |> Expect.equal "both levels ran" 2

      testCase "a level asked while an eval runs is deferred, and the eval is left alone" <| fun _ ->
        let t = run ReplLevelGenerators.levelDuringEval
        assertHolds t
        t.Final.Activity |> Expect.equal "still evaluating" SageFs.EvalActorDecision.SessionActivity.Evaluating

      testCase "an answer from a worker that has been replaced is dropped, and the new worker's ask is still waited for" <| fun _ ->
        let t = run ReplLevelGenerators.answerFromReplacedWorker
        assertHolds t
        t.Final.Applied |> Expect.isEmpty "the stale answer was not applied"
        match t.Final.Freshness with
        | ReplFreshness.Leveling _ -> ()
        | other -> failtestf "expected Leveling for the new worker's ask, got %A" other

      testCase "a save that lands before the answer arrives keeps the gap open" <| fun _ ->
        let t = run ReplLevelGenerators.saveBeforeAnswer
        assertHolds t
        match t.Final.Freshness with
        | ReplFreshness.BehindApp (2, _) -> ()
        | other -> failtestf "expected BehindApp at 2 saves, got %A" other

      testCase "a deferred level still serves the runs it held" <| fun _ ->
        let t = run ReplLevelGenerators.deferredLevelStillServesHeld
        assertHolds t
        t.Final.Held |> Expect.equal "none left held" 0

      testCase "WHY - a refetch that arrives while a status read has left Starting in the registry is applied, because its worker still serves the session" <| fun _ ->
        let t = run ReplLevelGenerators.refetchWhileRegistryReadsStarting
        assertHolds t
        t.Final.HeldHost |> Expect.equal "the daemon holds the host the level made" t.Final.Generation
        t.Final.Refetched |> List.map (fun r -> r.Applied) |> Expect.equal "the one refetch was applied" [ true ]

      testCase "a refetch that arrives after the registry was put back is applied" <| fun _ ->
        let t = run ReplLevelGenerators.refetchAfterRegistryRetires
        assertHolds t
        t.Final.HeldHost |> Expect.equal "the daemon holds the host the level made" t.Final.Generation

      testCase "WHY - the tests and maps fetched after a caller's hard reset are applied while the registry reads Starting" <| fun _ ->
        let t = run ReplLevelGenerators.hardResetWhileRegistryReadsStarting
        assertHolds t
        t.Final.HeldHost |> Expect.equal "the daemon holds the rebuilt host" t.Final.Generation

      testPropertyWithConfig simConfig "same seed, same trace" <| fun (seed: int) ->
        let a = run (ReplLevelGenerators.fromSeed seed)
        let b = run (ReplLevelGenerators.fromSeed seed)
        a.Final = b.Final
    ]

    testList "each twin is caught by the invariant it breaks" [
      teeth runCancelsFirstPolicy "L1-no-reset-under-work" ReplLevelGenerators.levelDuringEval
      teeth runCheckThenActPolicy "L1-no-reset-under-work" ReplLevelGenerators.runBetweenReadAndAct
      teeth runAdmitsDuringLevelPolicy "L1-no-reset-under-work" ReplLevelGenerators.runArrivesDuringLevel
      teeth runIgnoresEvalsPolicy "L3-no-reset-over-definitions" ReplLevelGenerators.evalThenLevel
      teeth runNoPidJudgePolicy "L4-answers-judged-by-pid" ReplLevelGenerators.answerFromReplacedWorker
      teeth runNoPidJudgePolicy "L5-level-means-level" ReplLevelGenerators.answerFromReplacedWorker
      teeth runLevelsBlindlyPolicy "L5-level-means-level" ReplLevelGenerators.saveBeforeAnswer
      teeth runDropsHeldPolicy "L2-held-runs-are-served" ReplLevelGenerators.deferredLevelStillServesHeld
      teeth runIgnoresCostPolicy "L6-no-level-over-budget" ReplLevelGenerators.largeProjectLevelsOnce
      teeth runRefetchNeedsReadyPolicy "L7-refetch-from-serving-worker-is-applied" ReplLevelGenerators.refetchWhileRegistryReadsStarting
      teeth runRefetchNeedsReadyPolicy "L8-daemon-holds-the-leveled-host" ReplLevelGenerators.refetchWhileRegistryReadsStarting
      teeth runRefetchNeedsReadyPolicy "L8-daemon-holds-the-leveled-host" ReplLevelGenerators.hardResetWhileRegistryReadsStarting

      testCase "over 2000 seeded scenarios every twin is caught at least once, so no invariant is vacuous on the generator" <| fun _ ->
        let seeds = [ 0 .. 1999 ]
        for twin in
          [ runCancelsFirstPolicy; runCheckThenActPolicy; runAdmitsDuringLevelPolicy; runIgnoresEvalsPolicy
            runNoPidJudgePolicy; runLevelsBlindlyPolicy; runDropsHeldPolicy; runIgnoresCostPolicy; runRefetchNeedsReadyPolicy ] do
          seeds
          |> List.exists (fun s -> not (List.isEmpty (violations (runWith twin (ReplLevelGenerators.fromSeed s)))))
          |> Expect.isTrue (sprintf "%s is caught somewhere in the seeded scenarios" twin.Name)
    ]
  ]
