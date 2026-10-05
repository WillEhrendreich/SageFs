module SageFs.Tests.CallerPendingSimTests

open Expecto
open Expecto.Flip
open SageFs.Features.CallerState
open SageFs.Simulation
open SageFs.Simulation.CallerPendingSim

/// DST for the claim a save of a re-signed function makes about its callers in other files: "they are still on the old
/// method, and here is who". See `SageFs.Simulation/CallerPendingSim.fs` for the fold, the independent truth it is
/// checked against, and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match CallerPendingInvariants.violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

let private violatedIds (t: Trace) : string list = CallerPendingInvariants.violations t |> List.map fst

let private finalState (t: Trace) : CallersState = (List.last t.Snapshots).Reported

let private isPending (state: CallersState) : bool =
  match state with
  | CallersState.CallersPending _ -> true
  | CallersState.CallersCurrent
  | CallersState.CallersNotChecked _
  | CallersState.CallersNotReported -> false

[<Tests>]
let tests =
  testList "DST caller pending" [

    testList "the real ledger over the real planner holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded multi-file edit histories — a re-signed or removed declaration with an unsaved caller is never reported clean, and the pending state clears exactly when the caller lands"
        <| fun (seed: int) -> assertHolds (run (CallerPendingGenerators.fromSeed seed))

      testCase "resignThenCallerSaved: pending after the re-sign, clear after the caller's file lands" <| fun _ ->
        let t = run CallerPendingGenerators.resignThenCallerSaved
        t.Snapshots |> List.map (fun s -> isPending s.Reported) |> Expect.equal "pending, then clear" [ true; false ]
        assertHolds t

      testCase "callerSavedBeforeTheResign: the caller that was saved first is stranded by the re-sign, so it is pending" <| fun _ ->
        let t = run CallerPendingGenerators.callerSavedBeforeTheResign
        isPending (finalState t) |> Expect.isTrue "the caller was written against the old signature"
        assertHolds t

      testCase "callerSavedTwice: the second save of a landed caller changes nothing" <| fun _ ->
        let t = run CallerPendingGenerators.callerSavedTwice
        isPending (finalState t) |> Expect.isFalse "clear and stays clear"
        assertHolds t

      testCase "resignedTwice: a caller that landed between the two re-signs is stranded again" <| fun _ ->
        let t = run CallerPendingGenerators.resignedTwice
        isPending (finalState t) |> Expect.isTrue "the second signature stranded it again"
        assertHolds t

      testCase "removalStrandsTheCaller: a removed function's caller is pending until it drops the call" <| fun _ ->
        let t = run CallerPendingGenerators.removalStrandsTheCaller
        t.Snapshots |> List.map (fun s -> isPending s.Reported) |> Expect.equal "pending, then clear" [ true; false ]
        assertHolds t

      testCase "renameStrandsTheOldName: a rename is a removal of the old name" <| fun _ ->
        let t = run CallerPendingGenerators.renameStrandsTheOldName
        isPending (finalState t) |> Expect.isTrue "the caller still calls the old name"
        assertHolds t

      testCase "bodyEditStrandsNobody: a body-only edit of the callee moves every caller at once" <| fun _ ->
        let t = run CallerPendingGenerators.bodyEditStrandsNobody
        isPending (finalState t) |> Expect.isFalse "same signature, same method"
        assertHolds t

      testCase "unchangedCallerSave: saving the caller with no edit does not clear it" <| fun _ ->
        let t = run CallerPendingGenerators.unchangedCallerSave
        isPending (finalState t) |> Expect.isTrue "the call is still on the old method"
        assertHolds t

      testCase "restartClears: a restarted app has no old methods" <| fun _ ->
        let t = run CallerPendingGenerators.restartClears
        isPending (finalState t) |> Expect.isFalse "current"
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED — the drops-the-check twin reports Patched with nothing about the stranded caller" <| fun _ ->
        let t = runDropsTheCheck CallerPendingGenerators.resignThenCallerSaved
        violatedIds t |> Expect.contains "the silent window must be caught" "resigned-with-unsaved-caller-is-never-clean"

      testCase "REPRODUCED — the clears-on-any-save twin forgets a caller that has not landed" <| fun _ ->
        let t = runClearsOnAnySave CallerPendingGenerators.callerSavedTwice
        violatedIds t |> Expect.contains "clearing on the wrong file must be caught" "resigned-with-unsaved-caller-is-never-clean"

      testCase "REPRODUCED — the never-clears twin keeps reporting a caller that already landed" <| fun _ ->
        let t = runNeverClears CallerPendingGenerators.resignThenCallerSaved
        violatedIds t |> Expect.contains "a stale pending state must be caught" "pending-clears-exactly-when-the-caller-lands"

      testCase "the invariants have teeth: some seeded history violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 150 ]
        let anyViolates runTwin =
          seeds |> List.exists (fun seed -> CallerPendingInvariants.violations (runTwin (CallerPendingGenerators.fromSeed seed)) |> List.isEmpty |> not)
        anyViolates runDropsTheCheck |> Expect.isTrue "dropping the check is caught by a generated history"
        anyViolates runClearsOnAnySave |> Expect.isTrue "clearing on any save is caught by a generated history"
        anyViolates runNeverClears |> Expect.isTrue "never clearing is caught by a generated history"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical snapshots (real ledger)" <| fun (seed: int) ->
        let a = run (CallerPendingGenerators.fromSeed seed)
        let b = run (CallerPendingGenerators.fromSeed seed)
        a.Snapshots = b.Snapshots
    ]
  ]
