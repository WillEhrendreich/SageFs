/// Deterministic Simulation Testing for same-commit tier reuse: seeded histories of commits, byte-different rebuilds,
/// SDK upgrades, a dirty tree, a drifting shard partition, flaky tiers and a damaged store, folded through the REAL
/// `PassRecord.decide`, `serialize` and `parse`. The ground truth is what the tiers actually executed green on. The
/// twins ignore the product binaries, ignore a dirty tree, or never reuse; each is caught by its own invariant.
module SageFs.Tests.PassRecordSimDstTests

open Expecto
open Expecto.Flip
open SageFs.Build
open SageFs.Simulation
open SageFs.Simulation.PassRecordSim

let seeds = [ 1 .. 500 ]

let violationsFor (decider: Decider) (invariant: State list -> PassRecordSimInvariants.Violation list) =
  seeds
  |> List.map (fun seed -> let scenario = scenarioOf seed in scenario, invariant (trace decider scenario))
  |> List.filter (fun (_, vs) -> not (List.isEmpty vs))

let expectNone (label: string) (bad: (Scenario * PassRecordSimInvariants.Violation list) list) =
  match bad with
  | [] -> ()
  | _ ->
    failtestf "%s: %d of %d seeds.\n%s" label bad.Length seeds.Length
      (bad
       |> List.truncate 3
       |> List.map (fun (s, vs) ->
         sprintf "seed=%d events=%d\n  %s" s.Seed s.Events.Length
           (vs |> List.truncate 3 |> List.map (fun v -> sprintf "[%d] %s" v.Index v.Why) |> String.concat "\n  "))
       |> String.concat "\n")

let expectCaught (label: string) (twin: Decider) (invariant: State list -> PassRecordSimInvariants.Violation list) =
  violationsFor twin invariant |> List.isEmpty |> Expect.isFalse label

[<Tests>]
let passRecordSimDstTests =
  testList "Pass record DST" [

    testCase "the scenarios are deterministic: the same seed gives the same trace" <| fun _ ->
      let a = trace Decider.Real (scenarioOf 7) |> List.last
      let b = trace Decider.Real (scenarioOf 7) |> List.last
      (a.Store, a.Truth, a.Gates |> List.map (fun g -> g.Statuses))
      |> Expect.equal "replaying a seed gives the identical end state" (b.Store, b.Truth, b.Gates |> List.map (fun g -> g.Statuses))

    testList "the real decision holds every invariant" [
      testCase "REUSE-IS-SOUND: a reused record is for exactly these inputs and was really run green" <| fun _ ->
        violationsFor Decider.Real PassRecordSimInvariants.reuseIsSound |> expectNone "a record was reused that no execution backs"

      testCase "FRESH-NEVER-REUSES" <| fun _ ->
        violationsFor Decider.Real PassRecordSimInvariants.freshNeverReuses |> expectNone "--fresh took a record"

      testCase "A-DIRTY-TREE-NEVER-REUSES" <| fun _ ->
        violationsFor Decider.Real PassRecordSimInvariants.dirtyTreeNeverReuses |> expectNone "a dirty tree took a record"

      testCase "AN-INELIGIBLE-TIER-NEVER-REUSES" <| fun _ ->
        violationsFor Decider.Real PassRecordSimInvariants.ineligibleNeverReuses |> expectNone "a tier that must always run took a record"

      testCase "AN-INTACT-RECORD-IS-USED: refusing everything is not safety" <| fun _ ->
        violationsFor Decider.Real PassRecordSimInvariants.intactRecordsAreUsed |> expectNone "an intact, exact record was ignored"

      testCase "A-DAMAGED-RECORD-ALWAYS-RERUNS" <| fun _ ->
        violationsFor Decider.Real PassRecordSimInvariants.damagedRecordsRerun |> expectNone "a record the store did not hold intact was reused"

      testCase "A-REUSED-RECORD-WAS-GREEN" <| fun _ ->
        violationsFor Decider.Real PassRecordSimInvariants.noTierIsGreenWithoutEvidence |> expectNone "a reused record was not green"
    ]

    testList "the seeds reach the interesting cases, so a green run means something" [
      let traces = seeds |> List.map (scenarioOf >> trace Decider.Real)
      let gates = traces |> List.collect (fun t -> (List.last t).Gates)
      let anyReused (g: GateRun) = g.Statuses |> Map.exists (fun _ s -> match s with | TierStatus.Reused _ -> true | _ -> false)

      testCase "some runs reuse a green tier after a flaky red one" <| fun _ ->
        gates |> List.exists anyReused |> Expect.isTrue "reuse happens"
        gates
        |> List.exists (fun g -> anyReused g && g.Statuses |> Map.exists (fun _ s -> match s with | TierStatus.Executed Outcome.Red -> true | _ -> false))
        |> Expect.isTrue "a run reuses greens beside a tier that ran and went red"

      testCase "some runs rerun a tier because only the product binaries changed" <| fun _ ->
        gates
        |> List.exists (fun g ->
          g.Stored
          |> Map.exists (fun tier stored ->
            match stored, Map.tryFind tier g.Inputs, Map.tryFind tier g.Statuses with
            | Stored.Present r, Some current, Some(TierStatus.Executed _) ->
              r.Inputs.Closure <> current.Closure && r.Inputs.TestAssembly = current.TestAssembly && r.Inputs.Sha = current.Sha
            | _ -> false))
        |> Expect.isTrue "a rebuilt dependency reran a tier whose own assembly was identical"

      testCase "some runs meet a damaged or missing record" <| fun _ ->
        gates
        |> List.exists (fun g -> g.Stored |> Map.exists (fun _ s -> match s with | Stored.Unreadable _ -> true | _ -> false))
        |> Expect.isTrue "an unreadable record reached a decision"

      testCase "some runs are --fresh, and some are on a dirty tree" <| fun _ ->
        gates |> List.exists (fun g -> g.Freshness = Freshness.Fresh) |> Expect.isTrue "fresh runs happen"
        gates |> List.exists (fun g -> g.Tree = TreeState.Dirty) |> Expect.isTrue "dirty runs happen"

      testCase "some runs find a record swapped in from another tier" <| fun _ ->
        gates
        |> List.exists (fun g ->
          g.Stored
          |> Map.exists (fun tier stored ->
            match stored with
            | Stored.Present r -> r.Inputs.Tier <> tier
            | _ -> false))
        |> Expect.isTrue "a record for another tier was offered"
    ]

    testCase "TWIN: ignoring the product binaries is caught by REUSE-IS-SOUND" <| fun _ ->
      expectCaught "a rebuilt dependency does not rerun a tier, and the simulation sees it" Decider.IgnoreClosureTwin PassRecordSimInvariants.reuseIsSound

    testCase "TWIN: reusing on a dirty tree is caught by A-DIRTY-TREE-NEVER-REUSES" <| fun _ ->
      expectCaught "a dirty tree takes a record, and the simulation sees it" Decider.IgnoreDirtyTreeTwin PassRecordSimInvariants.dirtyTreeNeverReuses

    testCase "TWIN: never reusing is caught by AN-INTACT-RECORD-IS-USED" <| fun _ ->
      expectCaught "a decision that refuses every record is safe and useless, and the simulation sees it" Decider.NeverReuseTwin PassRecordSimInvariants.intactRecordsAreUsed
  ]
