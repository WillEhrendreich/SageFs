module SageFs.Tests.RunStallSimTests

/// DST for the wait on a test run (`SageFs.Core/Features/RunStall.fs` through `SageFs.Simulation.RunStallSim`), the fix for the cap
/// that was in two places: the worker cancelled a run two minutes after it started and the landing gate gave up two and a half
/// minutes after it asked, while one cohort run's tests were still reporting (19067 asked for, 18529 never reported).
///
/// The sim runs the real `RunStall.await` over a seeded script of reports on a virtual clock, so hours of run cost microseconds and
/// the same seed replays forever. The twin is the fixed deadline from the start of the run that was there before: the invariants
/// must hold for the real wait and fail for the twin, or they prove nothing.
open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features.LiveTesting.RunStall
open SageFs.Simulation
open SageFs.Simulation.RunStallSim
open SageFs.Simulation.RunStallInvariants

let simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

/// The cap the run had before this fix: two minutes from the start of the run, which is the stall window's own default.
let historicalCap : TimeSpan = Timeouts.testRunStallWindow ()

let assertHolds (trace: Trace) : unit =
  match violations trace with
  | [] -> ()
  | found ->
    failtestf
      "INVARIANT VIOLATION (%s). Replay: Seed=%d Window=%A Requested=%d StartsAt=%A Ending=%A Reports=%d Violations=%A"
      trace.Reducer trace.Scenario.Seed trace.Scenario.Window trace.Scenario.Requested trace.Scenario.StartsAt
      trace.Scenario.Ending trace.Scenario.Reports.Length found

[<Tests>]
let tests =
  testList "DST the wait on a test run" [

    testList "the real wait holds every invariant" [
      testPropertyWithConfig simConfig
        "seeded runs that report inside every window are waited for, and a run that goes quiet is named with its count"
        <| fun (seed: int) -> assertHolds (run (RunStallGenerators.fromSeed seed))

      testCase "WHY — the cohort's 19067-test run, 2.2 windows long, finishes: a run that is still reporting is never cut by a clock" <| fun _ ->
        let trace = run RunStallGenerators.cohortFullRun
        trace.Outcome |> Expect.equal "finished" Outcome.Finished
        (trace.EndedAt > historicalCap) |> Expect.isTrue "it ran past the old cap"
        assertHolds trace

      testCase "WHY — a run of a hundred tests reporting at nine tenths of a window each (over an hour) finishes" <| fun _ ->
        let trace = run RunStallGenerators.slowButReporting
        trace.Outcome |> Expect.equal "finished" Outcome.Finished
        assertHolds trace

      testCase "WHY — a run that reports 538 of 19067 and goes quiet is cancelled within two windows, naming 538 reported and 18529 not" <| fun _ ->
        let trace = run (RunStallGenerators.hangsAfter 19067 538)
        match trace.Outcome with
        | Outcome.Stalled (StallCause.StoppedReporting (reported, unreported), window) ->
          reported |> Expect.equal "the count that reported" 538
          unreported |> Expect.equal "the count that did not" 18529
          window |> Expect.equal "the window it was judged by" trace.Scenario.Window
        | other -> failtestf "expected a stall naming the counts, got %A" other
        assertHolds trace

      testCase "WHY — a run that was asked for and never starts is named as that" <| fun _ ->
        let trace = run RunStallGenerators.neverStarts
        match trace.Outcome with
        | Outcome.Stalled (StallCause.NeverStarted, _) -> ()
        | other -> failtestf "expected NeverStarted, got %A" other
        assertHolds trace

      testPropertyWithConfig simConfig
        "a hang after any number of reports is always caught, with that count"
        <| fun (PositiveInt requested, NonNegativeInt reporting) ->
          assertHolds (run (RunStallGenerators.hangsAfter requested (reporting % (requested + 1))))
    ]

    testList "the twin reproduces the historical bug (teeth)" [
      testCase "REPRODUCED — the fixed-deadline twin cuts the cohort's run at the old cap, still reporting, with most tests unreported" <| fun _ ->
        let trace = runFixedDeadline historicalCap RunStallGenerators.cohortFullRun
        match trace.Outcome with
        | Outcome.Stalled (StallCause.StoppedReporting (reported, unreported), _) ->
          (reported > 0 && unreported > reported) |> Expect.isTrue "it was cut while reporting, with most of the run unreported"
        | other -> failtestf "expected the twin to cut the run, got %A" other
        violations trace |> List.map fst |> Expect.contains "the never-cut invariant separates the twin" "a-run-that-keeps-reporting-is-never-cut"

      testCase "REPRODUCED — the fixed-deadline twin cuts an hour-long run that reports in every window" <| fun _ ->
        let trace = runFixedDeadline historicalCap RunStallGenerators.slowButReporting
        violations trace |> List.map fst |> Expect.contains "the never-cut invariant separates the twin" "a-run-that-keeps-reporting-is-never-cut"

      testCase "the twin still finishes a quick run and ends a hung one, so the difference is the deadline and nothing else" <| fun _ ->
        let quick = { RunStallGenerators.slowButReporting with Reports = RunStallGenerators.evenly TimeSpan.Zero (TimeSpan.FromTicks (historicalCap.Ticks / 1000L)) 10; Ending = Ending.Finishes TimeSpan.Zero }
        (runFixedDeadline historicalCap quick).Outcome |> Expect.equal "a quick run finishes in both" Outcome.Finished
        let hung = RunStallGenerators.hangsAfter 100 3
        let reportedAtCut = reportedBy hung historicalCap
        match (runFixedDeadline historicalCap hung).Outcome with
        | Outcome.Stalled (StallCause.StoppedReporting (reported, unreported), _) ->
          (reported, unreported) |> Expect.equal "the count at the cut" (reportedAtCut, 100 - reportedAtCut)
        | other -> failtestf "expected the twin to end a hung run with its count, got %A" other

      testPropertyWithConfig simConfig
        "the invariant has teeth: any seeded run that keeps reporting past the old cap separates the real wait from the twin"
        <| fun (seed: int) ->
          let scenario = RunStallGenerators.fromSeed seed
          let neverCut = "a-run-that-keeps-reporting-is-never-cut"
          let runsPastTheCap =
            (match finishAt scenario with Some finished -> finished > historicalCap | None -> false)
          match runsPastTheCap with
          | false -> ()
          | true ->
            let applies = RunStallInvariants.aRunThatKeepsReportingIsNeverCut.Applies (run scenario)
            let real = run scenario |> violations |> List.map fst |> List.contains neverCut
            let twin = runFixedDeadline historicalCap scenario |> violations |> List.map fst |> List.contains neverCut
            match applies, real, twin with
            | true, false, true -> ()
            | false, _, _ -> ()
            | _ -> failtestf "seed %d: real violated=%b twin violated=%b (expected the real wait clean and the twin violated)" seed real twin
    ]

    testList "the battery is not vacuous" [
      testCase "WHY — every invariant applies to at least one scenario the battery runs, so a green battery is not green by default" <| fun _ ->
        let traces =
          [ yield! [ for seed in 0 .. 300 -> run (RunStallGenerators.fromSeed seed) ]
            yield run RunStallGenerators.cohortFullRun
            yield run RunStallGenerators.slowButReporting
            yield run (RunStallGenerators.hangsAfter 100 5)
            yield run RunStallGenerators.neverStarts ]
        for invariant in all do
          traces |> List.exists invariant.Applies |> Expect.isTrue (sprintf "%s applies to some trace" invariant.Id)

      testCase "WHY — the twin is caught by something on a seeded battery too, not only on the hand-made cases" <| fun _ ->
        let caught =
          [ for seed in 0 .. 300 -> runFixedDeadline historicalCap (RunStallGenerators.fromSeed seed) |> violations ]
          |> List.exists (List.isEmpty >> not)
        caught |> Expect.isTrue "some seeded run is cut by the twin while it was still reporting"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical outcome and virtual end time" <| fun (seed: int) ->
        let a = run (RunStallGenerators.fromSeed seed)
        let b = run (RunStallGenerators.fromSeed seed)
        (a.Outcome = b.Outcome && a.EndedAt = b.EndedAt && a.Looks = b.Looks)
    ]
  ]
