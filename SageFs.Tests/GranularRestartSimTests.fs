module SageFs.Tests.GranularRestartSimTests

/// WHY — `SageFs.GranularRestartSim` folds the REAL restart policy, so these
/// tests are about the property that makes granular restart worth having: a
/// subject's restart budget is its own. A busy unit must never be able to
/// spend — or exhaust — the worker.
///
/// The suite follows the established DST shape (`ValueReadSimTests`): a seeded
/// battery, a REPLAY case for determinism, a NON-VACUOUS case so a sim that
/// never reaches an interesting state fails instead of passing quietly, and a
/// TWIN case per invariant that MUST break.

open System
open Expecto
open Expecto.Flip
open SageFs.GranularRestart
open SageFs.Simulation.GranularRestartSim
open SageFs.Simulation.GranularRestartInvariants

let private seeds = [ 1 .. 2000 ]

let private battery =
  lazy (seeds |> List.map (fun seed -> run (scenarioOf seed)))

let private report label (violations: Violation list) =
  if not (List.isEmpty violations) then
    let first = List.head violations

    failtestf
      "%s: seed %d — %s (replay: GranularRestartSim.run (scenarioOf %d))"
      label
      first.Seed
      first.Why
      first.Seed

[<Tests>]
let tests =
  testList "Granular restart DST" [

    testCase "SAFETY — a unit crashing never spends the worker's restart budget" <| fun _ ->
      battery.Value
      |> List.iter (fun trace ->
        trace
        |> neverPatchedOverAnotherSubjectsExhaustion
        |> report "cross-subject budget leak")

    testCase "SAFETY — every recorded budget is backed by a real decision for that subject" <| fun _ ->
      battery.Value
      |> List.iter (fun trace ->
        trace
        |> budgetsTrackTheirOwnDecisions
        |> report "ledger drifted from decisions")

    testCase "SAFETY — a give-up never clears the budget it just spent" <| fun _ ->
      battery.Value
      |> List.iter (fun trace ->
        trace
        |> giveUpNeverClearsASpentBudget
        |> report "give-up resurrected an exhausted subject")

    testCase "REPLAY — the same seed always produces the same decisions" <| fun _ ->
      let first = run (scenarioOf 4242)
      let second = run (scenarioOf 4242)

      (first.Steps |> List.map (fun s -> sprintf "%O@%O" s.Subject s.Verdict))
      |> Expect.equal "a seeded scenario must be deterministic" (second.Steps |> List.map (fun s -> sprintf "%O@%O" s.Subject s.Verdict))

      first.FinalBudgets
      |> Expect.equal "and its final budgets must be deterministic too" second.FinalBudgets

    testCase "NON-VACUOUS — the battery really exercises restarts, give-ups and both subjects" <| fun _ ->
      let traces = battery.Value

      let restarts =
        traces
        |> List.collect (fun t -> t.Steps)
        |> List.filter (fun s ->
          match s.Verdict with
          | Verdict.Restart _ -> true
          | Verdict.GiveUp _ -> false)

      let giveUps =
        traces
        |> List.collect (fun t -> t.Steps)
        |> List.filter (fun s ->
          match s.Verdict with
          | Verdict.GiveUp _ -> true
          | Verdict.Restart _ -> false)

      let unitRestarts =
        restarts
        |> List.filter (fun s ->
          match s.Subject with
          | RestartSubject.UnitScope _ -> true
          | RestartSubject.Worker -> false)

      let workerRestarts =
        restarts
        |> List.filter (fun s ->
          match s.Subject with
          | RestartSubject.Worker -> true
          | RestartSubject.UnitScope _ -> false)

      // Give-ups are the rare, interesting case: they need a long crash
      // streak. If none ever happen the budget invariants cannot fail, and a
      // pass would mean nothing.
      (giveUps.Length, 0)
      |> Expect.isGreaterThan "the battery must reach give-up, or the budget invariants are vacuous"

      (unitRestarts.Length, 0)
      |> Expect.isGreaterThan "the battery must restart units, or cross-subject isolation is untested"

      (workerRestarts.Length, 0)
      |> Expect.isGreaterThan "the battery must restart the worker too, or the coarse tier is untested"

    testCase "TWIN — the shared-counter model breaks cross-subject isolation" <| fun _ ->
      // The twin files every budget under the worker. Run the SAME scenarios
      // through it: if the isolation invariant cannot fire there, it is
      // asserting nothing about the real model either.
      let breaking =
        seeds
        |> List.filter (fun seed -> sharedCounterBreaksIsolation (scenarioOf seed))

      (breaking.Length, 0)
      |> Expect.isGreaterThan "the shared-counter twin must break the isolation invariant on some seed"

    testCase "TWIN — a give-up that resets the budget is caught" <| fun _ ->
      let breaking =
        seeds
        |> List.filter (fun seed -> giveUpResetsBreaks (scenarioOf seed))

      (breaking.Length, 0)
      |> Expect.isGreaterThan "the give-up twin must break its invariant on some seed"
  ]
