module SageFs.Tests.ConfirmationGateSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.ConfirmationGateSim

/// DST for the claim "a confirm-tier tool asks a person unless the caller created that very session itself". See
/// `SageFs.Simulation/ConfirmationGateSim.fs` for the fold (the real `ConfirmationPolicy.decide`), the independent truth it is
/// checked against, and the twins.

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
    | Op.Stops _
    | Op.Restarts -> None)

[<Tests>]
let tests =
  testList "DST confirmation gate" [

    testList "the real decision holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded histories of creates, stops, restarts and calls — a person is asked exactly unless a one-session tool runs on a remembered session its caller made"
        <| fun (seed: int) -> assertHolds (run (ConfirmationGateGenerators.fromSeed seed))

      testCase "ownSession: the caller that made the session is not asked" <| fun _ ->
        let t = run ConfirmationGateGenerators.ownSession
        askedPerCall t |> Expect.equal "nobody asked" [ false ]
        assertHolds t

      testCase "someoneElsesSession: another caller is asked" <| fun _ ->
        let t = run ConfirmationGateGenerators.someoneElsesSession
        askedPerCall t |> Expect.equal "asked" [ true ]
        assertHolds t

      testCase "aPersonsSession: a session with no creator on record is asked about" <| fun _ ->
        let t = run ConfirmationGateGenerators.aPersonsSession
        askedPerCall t |> Expect.equal "asked" [ true ]
        assertHolds t

      testCase "afterARestart: the daemon forgot who made it, so a person is asked" <| fun _ ->
        let t = run ConfirmationGateGenerators.afterARestart
        askedPerCall t |> Expect.equal "asked" [ true ]
        assertHolds t

      testCase "localDataIsNeverSpared: manage_local_data asks even on the caller's own session" <| fun _ ->
        let t = run ConfirmationGateGenerators.localDataIsNeverSpared
        askedPerCall t |> Expect.equal "asked" [ true ]
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED — the skips-every-session twin spares a session another caller made" <| fun _ ->
        let t = runSkipsEverySession ConfirmationGateGenerators.someoneElsesSession
        violatedIds t |> Expect.contains "sparing another's session must be caught" "nobody-is-spared-a-session-they-did-not-make"

      testCase "REPRODUCED — the treats-no-record-as-own twin spares a person's session" <| fun _ ->
        let t = runTreatsNoRecordAsOwn ConfirmationGateGenerators.aPersonsSession
        violatedIds t |> Expect.contains "sparing a person's session must be caught" "nobody-is-spared-a-session-they-did-not-make"

      testCase "REPRODUCED — the treats-every-tool-as-one-session twin spares manage_local_data" <| fun _ ->
        let t = runTreatsEveryToolAsOneSession ConfirmationGateGenerators.localDataIsNeverSpared
        violatedIds t |> Expect.contains "sparing local data must be caught" "nobody-is-spared-a-session-they-did-not-make"

      testCase "the invariants have teeth: some seeded history violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 300 ]
        let anyViolates runTwin =
          seeds |> List.exists (fun seed -> ConfirmationGateInvariants.violations (runTwin (ConfirmationGateGenerators.fromSeed seed)) |> List.isEmpty |> not)
        anyViolates runSkipsEverySession |> Expect.isTrue "skipping the confirmation for every session is caught by a generated history"
        anyViolates runTreatsNoRecordAsOwn |> Expect.isTrue "taking an unrecorded session as the caller's own is caught by a generated history"
        anyViolates runTreatsEveryToolAsOneSession |> Expect.isTrue "treating every tool as one-session is caught by a generated history"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical snapshots (real decision)" <| fun (seed: int) ->
        let a = run (ConfirmationGateGenerators.fromSeed seed)
        let b = run (ConfirmationGateGenerators.fromSeed seed)
        a.Snapshots = b.Snapshots
    ]
  ]
