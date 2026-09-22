/// Deterministic Simulation Testing for local data retention: seeded months
/// of days passing, SageFs upgrades, friction bursts, restarts and cohort
/// traffic, folded through the REAL `LocalDataRetention` decisions and the
/// real `Cohort.decide`. The twin prunes by age only and ignores the version,
/// and the caps invariant catches it.
module SageFs.Tests.RetentionSimDstTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.RetentionSim

let private seeds = [ 1 .. 500 ]

let private violationsFor (behavior: FrictionBehavior) (invariant: State list -> RetentionSimInvariants.Violation list) =
  seeds
  |> List.map (fun seed -> let scenario = scenarioOf seed in scenario, invariant (trace behavior scenario))
  |> List.filter (fun (_, vs) -> not (List.isEmpty vs))

let private expectNone (label: string) (bad: (Scenario * RetentionSimInvariants.Violation list) list) =
  match bad with
  | [] -> ()
  | _ ->
    failtestf "%s: %d of %d seeds.\n%s" label bad.Length seeds.Length
      (bad
       |> List.truncate 3
       |> List.map (fun (s, vs) ->
         sprintf "seed=%d events=%A\n  %s" s.Seed s.Events
           (vs |> List.truncate 3 |> List.map (fun v -> sprintf "[%d] %s" v.Index v.Why) |> String.concat "\n  "))
       |> String.concat "\n")

[<Tests>]
let retentionSimDstTests =
  testList "Local data retention DST" [

    testCase "the scenarios are deterministic: the same seed gives the same trace" <| fun _ ->
      let a = trace FrictionBehavior.Real (scenarioOf 7) |> List.last
      let b = trace FrictionBehavior.Real (scenarioOf 7) |> List.last
      (a.Rows, a.Aggregate, a.Ledger.Length) |> Expect.equal "replaying a seed gives the identical end state" (b.Rows, b.Aggregate, b.Ledger.Length)

    testList "the real retention holds every invariant" [
      testCase "STORE-NEVER-EXCEEDS-ITS-CAPS after any prune" <| fun _ ->
        violationsFor FrictionBehavior.Real RetentionSimInvariants.storeNeverExceedsItsCaps
        |> expectNone "the store went over a cap after a prune"

      testCase "CURRENT-VERSION-ROWS-IN-THE-WINDOW-ARE-NEVER-LOST" <| fun _ ->
        violationsFor FrictionBehavior.Real RetentionSimInvariants.currentVersionRowsInsideTheWindowAreNeverLost
        |> expectNone "a running-version row inside the window was lost"

      testCase "NO-COUNT-IS-SILENTLY-LOST" <| fun _ ->
        violationsFor FrictionBehavior.Real RetentionSimInvariants.noCountIsSilentlyLost
        |> expectNone "a written row is neither stored nor counted"

      testCase "AN-ACTIVE-COHORT-LEDGER-IS-NEVER-CLEARED" <| fun _ ->
        violationsFor FrictionBehavior.Real RetentionSimInvariants.activeCohortLedgerIsNeverCleared
        |> expectNone "a running cohort's ledger was cleared"
    ]

    testList "the seeds reach the interesting cases, so a green run means something" [
      let traces = seeds |> List.map (scenarioOf >> trace FrictionBehavior.Real)

      testCase "some seeds hit the row cap" <| fun _ ->
        traces
        |> List.exists (List.exists (fun s -> s.Rows.Length = policy.MaxRows))
        |> Expect.isTrue "at least one seed fills the store to the row cap"

      testCase "some seeds drop an old version's aggregate" <| fun _ ->
        traces
        |> List.exists (fun t -> not (Set.isEmpty (List.last t).AggregateDropped))
        |> Expect.isTrue "at least one seed upgrades past the aggregate version cap"

      testCase "some seeds clear a finished cohort's ledger" <| fun _ ->
        traces
        |> List.exists (fun t -> t |> List.pairwise |> List.exists (fun (a, b) -> b.Ledger.Length < a.Ledger.Length))
        |> Expect.isTrue "at least one seed restarts after a cohort finished long enough ago"
    ]

    testCase "TWIN: pruning by age only, ignoring the version, is caught by the caps invariant" <| fun _ ->
      let caught = violationsFor FrictionBehavior.AgeOnlyTwin RetentionSimInvariants.storeNeverExceedsItsCaps
      caught
      |> List.isEmpty
      |> Expect.isFalse "an upgrade leaves the old version's fresh rows behind and the sim sees it"
  ]
