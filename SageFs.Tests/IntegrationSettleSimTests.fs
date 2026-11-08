module SageFs.Tests.IntegrationSettleSimTests

/// DST for the landing gate's wait on the integration session (`SageFs.Core/Features/IntegrationSettle.fs` through
/// `SageFs.Simulation.IntegrationSettleSim`), the fix for finding C-restart of the first cohort run: a landing was Blocked
/// Inconclusive after a fixed 120 s while the restart of the integration session was still making progress.
///
/// The sim runs the real `IntegrationSettle.await` over a seeded script on a virtual clock, so an hour of restart costs
/// microseconds and the same seed replays forever. The twin is the fixed-deadline wait that was there before: the
/// invariants must hold for the real wait and fail for the twin, or they prove nothing.
open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features.IntegrationSettle
open SageFs.Simulation
open SageFs.Simulation.IntegrationSettleSim
open SageFs.Simulation.IntegrationSettleInvariants

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

/// The wait the gate had before this fix: one park, as long as the old settle deadline.
let private historicalDeadline : TimeSpan = Timeouts.cohortIntegrationSettle

let private assertHolds (trace: Trace) : unit =
  match violations trace with
  | [] -> ()
  | found ->
    failtestf
      "INVARIANT VIOLATION (%s). Replay: Seed=%d Window=%A Ending=%A Script=%A Violations=%A"
      trace.Reducer trace.Scenario.Seed trace.Scenario.Window trace.Scenario.Ending trace.Scenario.Script found

[<Tests>]
let tests =
  testList "DST the landing gate's wait on the integration session" [

    testList "the real wait holds every invariant" [
      testPropertyWithConfig simConfig
        "seeded restarts that change phase within every window settle, whatever their total length"
        <| fun (seed: int) -> assertHolds (run (IntegrationSettleGenerators.fromSeed seed))

      testCase "WHY — a restart of eighty steps, over an hour of virtual time, settles Ready: progress is never cut off by a clock (finding C-restart)" <| fun _ ->
        let trace = run IntegrationSettleGenerators.slowButProgressing
        trace.Outcome |> Expect.equal "settled" Outcome.Settled
        (trace.EndedAt > historicalDeadline) |> Expect.isTrue "it ran past the old fixed deadline"
        assertHolds trace

      testCase "WHY — a worker that goes quiet in a startup phase is Blocked with that phase, within two windows of going quiet" <| fun _ ->
        let trace = run (IntegrationSettleGenerators.hangsAfter 5)
        match trace.Outcome with
        | Outcome.StuckIn (Phase.Starting (WorkerSaid.Said text), window) ->
          text |> Expect.equal "the phase it stuck in" "loading project 7 of 11"
          window |> Expect.equal "the window it was judged by" trace.Scenario.Window
        | other -> failtestf "expected StuckIn the startup phase, got %A" other
        assertHolds trace

      testCase "WHY — a rebuild in flight for twenty windows then finishing is waited for, because its bound is the build runner's" <| fun _ ->
        let trace = run IntegrationSettleGenerators.longRebuild
        trace.Outcome |> Expect.equal "settled" Outcome.Settled
        assertHolds trace

      testCase "WHY — a fault the session manager answers with reaches the caller word for word" <| fun _ ->
        let trace = run (IntegrationSettleGenerators.faultsAfter 3)
        trace.Outcome |> Expect.equal "refused with the fault" (Outcome.Refused "worker faulted while loading Fantomas")
        assertHolds trace

      testPropertyWithConfig simConfig
        "a hang after any number of progressing steps is always caught, with the phase it hung in"
        <| fun (PositiveInt steps) -> assertHolds (run (IntegrationSettleGenerators.hangsAfter (steps % 60)))
    ]

    testList "the twin reproduces the historical bug (teeth)" [
      testCase "REPRODUCED — the fixed-deadline twin cuts the slow restart off at the old deadline, still making progress" <| fun _ ->
        let trace = runFixedDeadline historicalDeadline IntegrationSettleGenerators.slowButProgressing
        match trace.Outcome with
        | Outcome.Refused reason -> reason |> Expect.stringContains "the historical wording" "timed out during restart"
        | other -> failtestf "expected the twin to time out, got %A" other
        violations trace |> List.map fst |> Expect.contains "progress-is-never-cut separates the twin" "progress-is-never-cut"

      testCase "REPRODUCED — the fixed-deadline twin cuts a long quiet rebuild off too" <| fun _ ->
        let trace = runFixedDeadline historicalDeadline IntegrationSettleGenerators.longRebuild
        violations trace |> List.map fst |> Expect.contains "a-long-rebuild-is-not-a-stall separates the twin" "a-long-rebuild-is-not-a-stall"

      testCase "the twin still reports a real refusal and a quick Ready correctly, so the difference is the deadline and nothing else" <| fun _ ->
        let quick = runFixedDeadline historicalDeadline { IntegrationSettleGenerators.slowButProgressing with Script = IntegrationSettleGenerators.steps 1 0.5 }
        quick.Outcome |> Expect.equal "a quick restart settles in both" Outcome.Settled
        let refused = runFixedDeadline historicalDeadline (IntegrationSettleGenerators.faultsAfter 1)
        refused.Outcome |> Expect.equal "the refusal passes through" (Outcome.Refused "worker faulted while loading Fantomas")

      testPropertyWithConfig simConfig
        "the invariant has teeth: any seeded restart longer than the old deadline separates the real wait from the twin"
        <| fun (seed: int) ->
          let scenario = IntegrationSettleGenerators.fromSeed seed
          match scriptLength scenario > historicalDeadline with
          | false -> ()
          | true ->
            let real = run scenario |> violations |> List.map fst |> List.contains "progress-is-never-cut"
            let twin = runFixedDeadline historicalDeadline scenario |> violations |> List.map fst |> List.contains "progress-is-never-cut"
            match real, twin with
            | false, true -> ()
            | _ -> failtestf "seed %d: real violated=%b twin violated=%b (expected the real wait clean and the twin violated)" seed real twin
    ]

    testList "the battery is not vacuous" [
      testCase "WHY — every invariant applies to at least one scenario the battery runs, so a green battery is not green by default" <| fun _ ->
        let traces =
          [ yield! [ for seed in 0 .. 200 -> run (IntegrationSettleGenerators.fromSeed seed) ]
            yield run IntegrationSettleGenerators.slowButProgressing
            yield run (IntegrationSettleGenerators.hangsAfter 5)
            yield run IntegrationSettleGenerators.longRebuild
            yield run (IntegrationSettleGenerators.faultsAfter 3) ]
        for invariant in all do
          traces |> List.exists invariant.Applies |> Expect.isTrue (sprintf "%s applies to some trace" invariant.Id)
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical outcome and virtual end time" <| fun (seed: int) ->
        let a = run (IntegrationSettleGenerators.fromSeed seed)
        let b = run (IntegrationSettleGenerators.fromSeed seed)
        (a.Outcome = b.Outcome && a.EndedAt = b.EndedAt && a.Looks = b.Looks)
    ]
  ]
