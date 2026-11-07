module SageFs.Tests.ConfirmationGateSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.ConfirmationGateSim

/// DST for the claim "only manage_local_data asks a person; the five everyday tools (stop_session, reset_fsi_session,
/// hard_reset_fsi_session, switch_workflow, stop_app) never do, for any session". See
/// `SageFs.Simulation/ConfirmationGateSim.fs` for the fold (the real `ConfirmationPolicy.decide`), the independent truth
/// it is checked against, and the twins.

let simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let assertHolds (t: Trace) =
  match ConfirmationGateInvariants.violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

let violatedIds (t: Trace) : string list = ConfirmationGateInvariants.violations t |> List.map fst

/// Whether a person was asked at each call in a trace, in order.
let askedPerCall (t: Trace) : bool list =
  t.Snapshots
  |> List.choose (fun s ->
    match s.Op with
    | Op.Calls _ -> Some s.Asked
    | Op.Creates _
    | Op.PersonCreates _
    | Op.RunsCode _
    | Op.Stops _
    | Op.Restarts -> None)

let expected = ConfirmationGateGenerators.expectedAsks

[<Tests>]
let tests =
  testList "DST confirmation gate" [

    testList "the real decision holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded histories of creates, evals, stops, restarts and calls: only manage_local_data ever asks a person"
        <| fun (seed: int) -> assertHolds (run (ConfirmationGateGenerators.fromSeed seed))

      testCase "the tools the sim calls are the five everyday tools and manage_local_data, in that order" <| fun _ ->
        expected |> Expect.equal "five that never ask, then the one that does" [ false; false; false; false; false; true ]

      testCase "ownSession: the caller that made the session is asked only about manage_local_data" <| fun _ ->
        let t = run ConfirmationGateGenerators.ownSession
        askedPerCall t |> Expect.equal "asked" expected
        assertHolds t

      testCase "someoneElsesSession: a session another caller made asks about manage_local_data only" <| fun _ ->
        let t = run ConfirmationGateGenerators.someoneElsesSession
        askedPerCall t |> Expect.equal "asked" expected
        assertHolds t

      testCase "aPersonsSession: a session with no creator on record asks about manage_local_data only" <| fun _ ->
        let t = run ConfirmationGateGenerators.aPersonsSession
        askedPerCall t |> Expect.equal "asked" expected
        assertHolds t

      testCase "afterARestart: the daemon forgot who made the session, and still only manage_local_data asks" <| fun _ ->
        let t = run ConfirmationGateGenerators.afterARestart
        askedPerCall t |> Expect.equal "asked" expected
        assertHolds t

      testCase "aPersonRanCodeInIt: code from the dashboard in the session changes nothing" <| fun _ ->
        let t = run ConfirmationGateGenerators.aPersonRanCodeInIt
        askedPerCall t |> Expect.equal "asked" expected
        assertHolds t

      testCase "anotherCallerRanCodeInIt: another agent's code in the session changes nothing" <| fun _ ->
        let t = run ConfirmationGateGenerators.anotherCallerRanCodeInIt
        askedPerCall t |> Expect.equal "asked" expected
        assertHolds t

      testCase "ownCodeOnly: manage_local_data asks even on a session its caller made and alone worked in" <| fun _ ->
        let t = run ConfirmationGateGenerators.ownCodeOnly
        askedPerCall t |> Expect.equal "asked" expected
        assertHolds t

      testCase "noSuchSession: a session that was stopped changes nothing" <| fun _ ->
        let t = run ConfirmationGateGenerators.noSuchSession
        askedPerCall t |> Expect.equal "asked" expected
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED — the skips-every-tool twin spares manage_local_data" <| fun _ ->
        let t = runSkipsEveryTool ConfirmationGateGenerators.someoneElsesSession
        violatedIds t |> Expect.contains "sparing local data must be caught" "local-data-always-asks"

      testCase "REPRODUCED — the spares-the-creator twin spares manage_local_data on the caller's own session" <| fun _ ->
        let t = runSparesTheCreator ConfirmationGateGenerators.ownCodeOnly
        violatedIds t |> Expect.contains "sparing the creator on local data must be caught" "local-data-always-asks"

      testCase "REPRODUCED — the old gate asks about everyday tools on a session another caller made" <| fun _ ->
        let t = runAsksAboutSessionTools ConfirmationGateGenerators.someoneElsesSession
        violatedIds t |> Expect.contains "asking about an everyday tool must be caught" "normal-workflow-tools-never-ask"

      testCase "REPRODUCED — the old gate asks about everyday tools after a daemon restart" <| fun _ ->
        let t = runAsksAboutSessionTools ConfirmationGateGenerators.afterARestart
        violatedIds t |> Expect.contains "asking after a restart must be caught" "normal-workflow-tools-never-ask"

      testCase "the old gate spared the creator, so the one case it got right is not a violation" <| fun _ ->
        let t = runAsksAboutSessionTools ConfirmationGateGenerators.ownCodeOnly
        violatedIds t |> Expect.isEmpty "the creator's own session was never the problem"

      testCase "the invariants have teeth: some seeded history violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 300 ]
        let anyViolates runTwin =
          seeds |> List.exists (fun seed -> ConfirmationGateInvariants.violations (runTwin (ConfirmationGateGenerators.fromSeed seed)) |> List.isEmpty |> not)
        anyViolates runSkipsEveryTool |> Expect.isTrue "skipping every tool is caught by a generated history"
        anyViolates runSparesTheCreator |> Expect.isTrue "sparing the creator on local data is caught by a generated history"
        anyViolates runAsksAboutSessionTools |> Expect.isTrue "the old gate is caught by a generated history"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical snapshots (real decision)" <| fun (seed: int) ->
        let a = run (ConfirmationGateGenerators.fromSeed seed)
        let b = run (ConfirmationGateGenerators.fromSeed seed)
        a.Snapshots = b.Snapshots
    ]
  ]
