module SageFs.Tests.TrunkFollowSimTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Features.TrunkFollow
open SageFs.Simulation
open SageFs.Simulation.TrunkFollowSim
open SageFs.Simulation.TrunkFollowInvariants

/// DST for the claim "what the cohort lands reaches the trunk session's running app, in the order it landed, once, and the record
/// says what really happened to the app". The fold under test is the real `TrunkFollow.step`; see
/// `SageFs.Simulation/TrunkFollowSim.fs` for the world around it, the op order that serves as the scheduler, and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

let private effectsOf (t: Trace) : TrunkEffect list = t.Steps |> List.collect (fun s -> s.Effects)

let private movedFor (t: Trace) : LandingId list =
  effectsOf t |> List.choose (function TrunkEffect.MoveTrunk l -> Some l.Landing | TrunkEffect.Deliver _ -> None)

let private deliveredTo (t: Trace) : LandingId list =
  effectsOf t |> List.choose (function TrunkEffect.Deliver (l, _, _) -> Some l.Landing | TrunkEffect.MoveTrunk _ -> None)

let private verdictOf (landing: LandingId) (t: Trace) : TrunkVerdict =
  (t.Final.Records |> List.find (fun r -> r.Landing = landing)).Verdict

let private violatedIds (t: Trace) : string list = violations t |> List.map fst

[<Tests>]
let trunkFollowSimTests =
  testList "DST trunk follow" [

    testList "the real decision holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded scenarios: only landings that landed, in order, none lost, the record matches what happened to the app, patched only after it ran"
        <| fun (seed: int) -> assertHolds (run (TrunkFollowGenerators.fromSeed seed))

      testCase "oneLanding: the checkout moves, the worker patches, and the record is Patched once the new body has run" <| fun _ ->
        let t = run TrunkFollowGenerators.oneLanding
        movedFor t |> Expect.equal "the trunk moved for the one landing" [ landingId 1 ]
        match verdictOf (landingId 1) t with
        | TrunkVerdict.Followed [ { Outcome = SessionOutcome.Delivered [ { Outcome = FileOutcome.Reloaded (facts, []) } ] } ] ->
          facts.Case |> Expect.equal "it ends settled" ReloadCase.Patched
        | other -> failtestf "expected one delivered verdict, got %A" other
        assertHolds t

      testCase "oneLanding: until the body runs the record says PatchPending, never Patched" <| fun _ ->
        let t = run { TrunkFollowGenerators.oneLanding with Ops = TrunkFollowGenerators.oneLanding.Ops |> List.filter (function Op.BodyRuns _ -> false | _ -> true) }
        // The drain runs the patched body at the end, so the pending state is read off the step that recorded the landing.
        let recording =
          t.Steps |> List.find (fun s -> s.After.Records |> List.exists (fun r -> r.Landing = landingId 1) && s.Before.Records.IsEmpty)
        match (recording.After.Records |> List.head).Verdict with
        | TrunkVerdict.Followed [ { Outcome = SessionOutcome.Delivered [ { Outcome = FileOutcome.Reloaded (facts, _) } ] } ] ->
          facts.Case |> Expect.equal "recorded as applied and not yet seen running" ReloadCase.PatchPending
        | other -> failtestf "expected one delivered verdict, got %A" other

      testCase "threeQueued: landings that land while one is followed wait, and are followed in the order they landed" <| fun _ ->
        let t = run TrunkFollowGenerators.threeQueued
        movedFor t |> Expect.equal "moved in landing order" [ landingId 1; landingId 2; landingId 3 ]
        t.Final.Records |> List.map (fun r -> r.Landing) |> Expect.equal "recorded in landing order" [ landingId 1; landingId 2; landingId 3 ]
        assertHolds t

      testCase "refusedNeverFollowed: a landing that was verified and refused never reaches the trunk" <| fun _ ->
        let t = run TrunkFollowGenerators.refusedNeverFollowed
        movedFor t |> Expect.equal "only the landing that landed" [ landingId 1 ]
        t.Final.Records |> List.map (fun r -> r.Landing) |> Expect.equal "and only it is recorded" [ landingId 1 ]
        assertHolds t

      testCase "verifyingNeverFollowed: a landing still being verified is not followed" <| fun _ ->
        let t = run TrunkFollowGenerators.verifyingNeverFollowed
        movedFor t |> Expect.equal "only the landing that landed" [ landingId 1 ]
        assertHolds t

      testCase "restarts: a restart is recorded as one, with its cause, and the app really was restarted" <| fun _ ->
        let t = run TrunkFollowGenerators.restarts
        match verdictOf (landingId 1) t with
        | TrunkVerdict.Followed [ { Outcome = SessionOutcome.Delivered [ { Outcome = FileOutcome.Reloaded (facts, causes) } ] } ] ->
          facts.Case |> Expect.equal "recorded as restarted" ReloadCase.Restarted
          causes |> List.map (fun c -> c.Case) |> Expect.equal "naming the cause" [ "VirtualSignatureChanged" ]
        | other -> failtestf "expected one delivered verdict, got %A" other
        t.Truths |> Map.find (landingId 1) |> Expect.equal "the world did restart the app" Truth.Restarted
        assertHolds t

      testCase "earlyReport: a patched body that runs before the answer reaches the daemon still settles the record" <| fun _ ->
        let t = run TrunkFollowGenerators.earlyReport
        match verdictOf (landingId 1) t with
        | TrunkVerdict.Followed [ { Outcome = SessionOutcome.Delivered [ { Outcome = FileOutcome.Reloaded (facts, _) } ] } ] ->
          facts.Case |> Expect.equal "settled by the report that came first" ReloadCase.Patched
        | other -> failtestf "expected one delivered verdict, got %A" other
        assertHolds t

      testCase "landDuringDelivery: landings that land mid-delivery are followed after it, one at a time" <| fun _ ->
        let t = run TrunkFollowGenerators.landDuringDelivery
        movedFor t |> Expect.equal "moved in landing order" [ landingId 1; landingId 2; landingId 3 ]
        deliveredTo t |> Expect.equal "delivered in landing order" [ landingId 1; landingId 2; landingId 3 ]
        assertHolds t

      testCase "noRunningApp: a session with no app is recorded as having nothing to update, and nothing is delivered" <| fun _ ->
        let t = run TrunkFollowGenerators.noRunningApp
        deliveredTo t |> Expect.isEmpty "no delivery"
        match verdictOf (landingId 1) t with
        | TrunkVerdict.Followed [ { Outcome = SessionOutcome.NoApp } ] -> ()
        | other -> failtestf "expected NoApp, got %A" other
        assertHolds t

      testCase "noTrunkSession: a landing with no session in the trunk checkout is recorded, and nothing is delivered" <| fun _ ->
        let t = run TrunkFollowGenerators.noTrunkSession
        verdictOf (landingId 1) t |> Expect.equal "no trunk session" TrunkVerdict.NoTrunkSession
        assertHolds t

      testCase "moveRefused: a checkout that cannot be moved is recorded, and the next landing is followed all the same" <| fun _ ->
        let t = run TrunkFollowGenerators.moveRefused
        match verdictOf (landingId 1) t with
        | TrunkVerdict.NotMoved _ -> ()
        | other -> failtestf "expected NotMoved, got %A" other
        movedFor t |> Expect.equal "the second landing was followed" [ landingId 1; landingId 2 ]
        assertHolds t

      testCase "silentWorker: a worker that never answers is recorded as unreachable, and the machine is not left waiting" <| fun _ ->
        let t = run TrunkFollowGenerators.silentWorker
        match verdictOf (landingId 1) t with
        | TrunkVerdict.Followed [ { Outcome = SessionOutcome.Unreachable _ } ] -> ()
        | other -> failtestf "expected Unreachable, got %A" other
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED: the applies-unlanded twin follows a landing that was only being verified" <| fun _ ->
        let t = runAppliesUnlanded TrunkFollowGenerators.verifyingNeverFollowed
        violatedIds t |> Expect.contains "never-apply-a-landing-that-did-not-land must fire" "never-apply-a-landing-that-did-not-land"

      testCase "REPRODUCED: the applies-unlanded twin follows a landing that was refused" <| fun _ ->
        let t = runAppliesUnlanded TrunkFollowGenerators.refusedNeverFollowed
        violatedIds t |> Expect.contains "never-apply-a-landing-that-did-not-land must fire" "never-apply-a-landing-that-did-not-land"

      testCase "REPRODUCED: the reorders-landings twin follows the newest queued landing first" <| fun _ ->
        let t = runReordersLandings TrunkFollowGenerators.threeQueued
        violatedIds t |> Expect.contains "landings-applied-in-order must fire" "landings-applied-in-order"

      testCase "REPRODUCED: the drops-while-busy twin loses a landing that lands while another is followed" <| fun _ ->
        let t = runDropsWhileBusy TrunkFollowGenerators.threeQueued
        violatedIds t |> Expect.contains "no-lost-landing must fire" "no-lost-landing"

      testCase "REPRODUCED: the hides-restarts twin records a restart as a patch" <| fun _ ->
        let t = runHidesRestarts TrunkFollowGenerators.restarts
        violatedIds t |> Expect.contains "state-kept-or-restart-named must fire" "state-kept-or-restart-named"

      testCase "REPRODUCED: the promotes-pending twin records a patch as settled before its new body ran" <| fun _ ->
        let t = runPromotesPending TrunkFollowGenerators.oneLanding
        violatedIds t |> Expect.contains "patched-only-after-ran must fire" "patched-only-after-ran"

      testCase "REPRODUCED: the drops-early-reports twin leaves a patch pending after its body ran" <| fun _ ->
        let t = runDropsEarlyReports TrunkFollowGenerators.earlyReport
        violatedIds t |> Expect.contains "pending-resolves must fire" "pending-resolves"

      testCase "the invariants have teeth: some seeded scenario violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 400 ]
        let anyViolates runTwin (id: string) =
          seeds |> List.exists (fun seed -> violatedIds (runTwin (TrunkFollowGenerators.fromSeed seed)) |> List.contains id)
        anyViolates runAppliesUnlanded "never-apply-a-landing-that-did-not-land" |> Expect.isTrue "applies-unlanded is caught by a generated scenario"
        anyViolates runReordersLandings "landings-applied-in-order" |> Expect.isTrue "reorders-landings is caught by a generated scenario"
        anyViolates runDropsWhileBusy "no-lost-landing" |> Expect.isTrue "drops-while-busy is caught by a generated scenario"
        anyViolates runHidesRestarts "state-kept-or-restart-named" |> Expect.isTrue "hides-restarts is caught by a generated scenario"
        anyViolates runPromotesPending "patched-only-after-ran" |> Expect.isTrue "promotes-pending is caught by a generated scenario"
        anyViolates runDropsEarlyReports "pending-resolves" |> Expect.isTrue "drops-early-reports is caught by a generated scenario"
    ]
  ]
