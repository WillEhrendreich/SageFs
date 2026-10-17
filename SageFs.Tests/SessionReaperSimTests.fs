module SageFs.Tests.SessionReaperSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.SessionReaperSim

/// DST for the claim "the daemon stops a session an agent abandoned, and only that". See
/// `SageFs.Simulation/SessionReaperSim.fs` for the fold (the real `Reaper.SweepAsync`), the independent truth it is checked
/// against, and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match SessionReaperInvariants.violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

let private violatedIds (t: Trace) : string list = SessionReaperInvariants.violations t |> List.map fst

/// What every sweep in a trace stopped, in order.
let private reapedPerSweep (t: Trace) : int list list =
  t.Snapshots
  |> List.choose (fun s ->
    match s.Op with
    | Op.Sweep -> Some(Set.toList s.Reaped)
    | _ -> None)

[<Tests>]
let tests =
  testList "DST session reaper" [

    testList "the real reaper holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded histories of arrivals, departures, use, restarts and sweeps — a sweep stops exactly the sessions an agent abandoned, never a person's, never one whose owner is here, never inside the grace after a start"
        <| fun (seed: int) -> assertHolds (run (SessionReaperGenerators.fromSeed seed))

      testCase "abandonedIsStopped: the sweep a bound and a minute after the owner left stops it, the first does not" <| fun _ ->
        let t = run SessionReaperGenerators.abandonedIsStopped
        reapedPerSweep t |> Expect.equal "nothing, then the session" [ []; [ 1 ] ]
        assertHolds t

      testCase "aPersonsSessionIsNeverStopped: days of idleness stop nothing without a record" <| fun _ ->
        let t = run SessionReaperGenerators.aPersonsSessionIsNeverStopped
        reapedPerSweep t |> Expect.equal "nothing, twice" [ []; [] ]
        assertHolds t

      testCase "aConnectedOwnerKeepsItsSession: an owner that is here keeps an idle session" <| fun _ ->
        let t = run SessionReaperGenerators.aConnectedOwnerKeepsItsSession
        reapedPerSweep t |> Expect.equal "nothing, twice" [ []; [] ]
        assertHolds t

      testCase "aRestartDoesNotMakeItLookAbandoned: a sweep right after a restart stops nothing, however long ago the owner left" <| fun _ ->
        let t = run SessionReaperGenerators.aRestartDoesNotMakeItLookAbandoned
        reapedPerSweep t |> Expect.equal "nothing" [ [] ]
        assertHolds t

      testCase "aRestartDelaysTheReapByTheBound: after the restart the bound passes in full, and then it is stopped" <| fun _ ->
        let t = run SessionReaperGenerators.aRestartDelaysTheReapByTheBound
        reapedPerSweep t |> Expect.equal "the session" [ [ 1 ] ]
        assertHolds t

      testCase "aVisitRestartsTheAbsence: the absence is counted from the last sweep that saw the owner" <| fun _ ->
        let t = run SessionReaperGenerators.aVisitRestartsTheAbsence
        reapedPerSweep t |> Expect.equal "nothing, twice" [ []; [] ]
        assertHolds t

      testCase "aSessionInUseIsKept: an owner gone for days does not matter to a session used a minute ago" <| fun _ ->
        let t = run SessionReaperGenerators.aSessionInUseIsKept
        reapedPerSweep t |> Expect.equal "nothing" [ [] ]
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED — the forgets-the-restart twin stops a session a restart ago, on evidence from before the restart" <| fun _ ->
        let t = runForgetsTheRestart SessionReaperGenerators.aRestartDoesNotMakeItLookAbandoned
        violatedIds t |> Expect.contains "the grace must be caught" "a-restart-grants-a-grace-of-the-bound"

      testCase "REPRODUCED — the ignores-presence twin stops a session whose owner is here" <| fun _ ->
        let t = runIgnoresPresence SessionReaperGenerators.aConnectedOwnerKeepsItsSession
        violatedIds t |> Expect.contains "a connected owner must be caught" "never-reaps-while-the-owner-is-here"

      testCase "REPRODUCED — the treats-every-session-as-agents twin stops a person's session" <| fun _ ->
        let t = runTreatsEverySessionAsAgents SessionReaperGenerators.aPersonsSessionIsNeverStopped
        violatedIds t |> Expect.contains "a person's session must be caught" "never-reaps-a-session-a-person-made"

      testCase "the invariants have teeth: some seeded history violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 300 ]
        let anyViolates runTwin =
          seeds |> List.exists (fun seed -> SessionReaperInvariants.violations (runTwin (SessionReaperGenerators.fromSeed seed)) |> List.isEmpty |> not)
        anyViolates runForgetsTheRestart |> Expect.isTrue "forgetting the restart is caught by a generated history"
        anyViolates runIgnoresPresence |> Expect.isTrue "ignoring presence is caught by a generated history"
        anyViolates runTreatsEverySessionAsAgents |> Expect.isTrue "treating every session as an agent's is caught by a generated history"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical snapshots (real reaper)" <| fun (seed: int) ->
        let a = run (SessionReaperGenerators.fromSeed seed)
        let b = run (SessionReaperGenerators.fromSeed seed)
        a.Snapshots = b.Snapshots
    ]
  ]
