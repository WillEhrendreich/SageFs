module SageFs.Tests.SupervisorWatchdogSimTests

open Expecto
open Expecto.Flip
open SageFs.SupervisorWatchdog
open SageFs.Simulation
open SageFs.Simulation.SupervisorWatchdogSim
open SageFs.Simulation.SupervisorWatchdogInvariants

/// DST for the session manager's wedge watchdog. The real
/// `SupervisorWatchdog.decide` is folded against a virtual clock (no sleeps, no
/// threads), and two twins prove the invariants can fail: one that never
/// alarms (the status quo this change replaces) and one that pages on every
/// busy tick.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) - replay this scenario:\n  Seed=%d\n  Bound=%O CheckEvery=%O Horizon=%O\n  Commands=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Bound t.Scenario.CheckEvery t.Scenario.Horizon t.Scenario.Commands vs

let private violated (inv: Invariant) (t: Trace) =
  match inv.Check t with
  | Outcome.Violated _ -> true
  | Outcome.Holds -> false

[<Tests>]
let tests =
  testList "DST supervisor wedge watchdog" [

    testList "the real decision holds every invariant" [

      testPropertyWithConfig simConfig "seeded scenarios: every wedge is reported once, in time, and nothing healthy is paged" <|
        fun (seed: int) -> assertHolds (run (fromSeed seed))

      testCase "a StopWorker that never returns is alarmed Wedged, once, within the bound" <| fun _ ->
        let t = run stopWorkerNeverReturns
        assertHolds t
        t.Raised
        |> List.map (fun r -> r.Alarm)
        |> Expect.hasLength "one alarm for the one wedge" 1
        let deadline = stopWorkerNeverReturns.Commands.Head.StartsAt + stopWorkerNeverReturns.Bound + stopWorkerNeverReturns.CheckEvery
        (List.head t.Raised).At <= deadline
        |> Expect.isTrue "raised no later than start + bound + one check"

      testCase "a busy loop whose commands all finish inside the bound raises nothing" <| fun _ ->
        let t = run busyButHealthy
        assertHolds t
        t.Raised |> Expect.isEmpty "nothing to report"

      testCase "two separate wedges are two alarms, one each" <| fun _ ->
        let t = run twoSeparateWedges
        assertHolds t
        t.Raised |> Expect.hasLength "one alarm per wedge" 2
    ]

    testList "the twins prove the invariants can fail" [

      testCase "TWIN - a watchdog that never alarms misses the wedge" <| fun _ ->
        violated wedgeDetectedWithinBound (runNeverAlarms stopWorkerNeverReturns)
        |> Expect.isTrue "wedge-detected-within-bound must fail for a silent watchdog"

      testCase "TWIN - a watchdog that pages on every busy tick cries wolf on a healthy loop" <| fun _ ->
        violated noFalseAlarm (runAlarmEveryTick busyButHealthy)
        |> Expect.isTrue "no-false-alarm must fail for a noisy watchdog"

      testCase "TWIN - a watchdog that pages on every tick repeats the same wedge" <| fun _ ->
        violated oneAlarmPerWedge (runAlarmEveryTick stopWorkerNeverReturns)
        |> Expect.isTrue "one-alarm-per-wedge must fail for a noisy watchdog"
    ]
  ]
