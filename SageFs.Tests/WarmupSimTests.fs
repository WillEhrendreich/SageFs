module SageFs.Tests.WarmupSimTests

/// DST for a session's warmup lifecycle (`SageFs.Core/WarmupSupervision.fs`
/// via `SageFs.Simulation.WarmupSim`) — see `WarmupSim.fs`'s header for the
/// historical bug (a flat elapsed bound with no inactivity check) and why
/// this is a seeded model over the real pure core, not a reimplementation.
///
/// `WarmupSupervisionTests.fs` covers `decidePoll`/`step` directly, example
/// by example; this DST covers ordering/timing SPACE — seeded scenarios,
/// the five shapes named in the fix's brief (never-finishes, faults-halfway,
/// stop-while-starting, stop-after-fault, discovery of 1/10/100 projects,
/// a status poll racing a fault), and a twin that reproduces the historical
/// regression — far more exhaustively than a handful of hand-written
/// examples could.
open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.WarmupSupervision
open SageFs.Simulation
open SageFs.Simulation.WarmupSim
open SageFs.Simulation.WarmupInvariants

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Bounds=%A\n  Events=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Bounds t.Scenario.Events vs

[<Tests>]
let tests =
  testList "DST session warmup lifecycle" [

    testList "the fixed contract holds — seeded scenarios" [
      testPropertyWithConfig simConfig
        "seeded scenarios — the real reducer never leaves a stop unhonored, never resurrects a terminal state, and never outlives its bounds while Starting"
        <| fun (seed: int) -> assertHolds (run (WarmupGenerators.fromSeed seed))
    ]

    testList "the four onboarding-trial shapes, named" [

      testCase "warmup that never finishes: silence past the inactivity bound reaches Faulted, not left Starting forever (defect #1)" <| fun _ ->
        let t = run WarmupGenerators.warmupNeverFinishes
        match t.Final.State with
        | WarmupSupervision.LifecycleState.Faulted _ -> ()
        | other -> failtestf "expected Faulted after sustained silence, got %A" other
        assertHolds t

      testCase "warmup that faults halfway: real progress then a real fault reports the fault, not a timeout" <| fun _ ->
        let t = run WarmupGenerators.warmupFaultsHalfway
        match t.Final.State with
        | WarmupSupervision.LifecycleState.Faulted reason -> reason |> Expect.stringContains "the worker's own fault reason survives" "Not all DLLs"
        | other -> failtestf "expected Faulted(worker's own reason), got %A" other
        assertHolds t

      testCase "a stop arriving WHILE starting always yields Stopped (defect #2)" <| fun _ ->
        let t = run WarmupGenerators.stopWhileStarting
        t.Final.State |> Expect.equal "Stopped" WarmupSupervision.LifecycleState.Stopped
        assertHolds t

      testCase "a stop arriving AFTER a fault always yields Stopped, not stuck Faulted (defect #2)" <| fun _ ->
        let t = run WarmupGenerators.stopAfterFault
        t.Final.State |> Expect.equal "Stopped, not left Faulted" WarmupSupervision.LifecycleState.Stopped
        assertHolds t

      testCase "discovery of 1 project reaches Ready" <| fun _ ->
        let t = run WarmupGenerators.discoveryOf1Project
        match t.Final.State with WarmupSupervision.LifecycleState.Ready _ -> () | other -> failtestf "expected Ready, got %A" other
        assertHolds t

      testCase "discovery of 10 projects reaches Ready despite 10 separate progress ticks" <| fun _ ->
        let t = run WarmupGenerators.discoveryOf10Projects
        match t.Final.State with WarmupSupervision.LifecycleState.Ready _ -> () | other -> failtestf "expected Ready, got %A" other
        assertHolds t

      testCase "discovery of 100 projects reaches Ready — a big repo genuinely making progress is never punished for its size (defect #1)" <| fun _ ->
        let t = run WarmupGenerators.discoveryOf100Projects
        match t.Final.State with WarmupSupervision.LifecycleState.Ready _ -> () | other -> failtestf "expected Ready, got %A" other
        assertHolds t

      testCase "a status poll racing a fault: whichever order the tick/fault/late-ready land in, the final read is Faulted, not Starting or Ready (defect #3)" <| fun _ ->
        let t = run WarmupGenerators.statusPollRacingFault
        match t.Final.State with
        | WarmupSupervision.LifecycleState.Faulted _ -> ()
        | other -> failtestf "expected Faulted (the fault came first and is terminal), got %A" other
        assertHolds t
    ]

    testList "the twin reproduces the historical regression (teeth)" [

      testCase "REPRODUCED — the flat-bound-only twin leaves a silent warmup Starting, exactly fcs-trial-a's shape" <| fun _ ->
        // A generous flat bound (10 minutes — the real fix's own absolute
        // ceiling) tolerates a large repo, which is exactly what makes it
        // blind to a session that has gone silent well within that window.
        let t = runFlatBoundOnly (TimeSpan.FromMinutes 10.0) WarmupGenerators.warmupNeverFinishes
        match t.Final.State with
        | WarmupSupervision.LifecycleState.Starting -> ()
        | other -> failtestf "expected the twin to reproduce the historical bug (left Starting), got %A" other
        violations t
        |> List.map fst
        |> Expect.contains "silence-is-caught-by-inactivity must fire against the twin" "silence-is-caught-by-inactivity"

      testCase "the twin behaves like the real reducer once a real Ready/Faulted is actually reported" <| fun _ ->
        let t = runFlatBoundOnly (TimeSpan.FromMinutes 10.0) WarmupGenerators.warmupFaultsHalfway
        match t.Final.State with
        | WarmupSupervision.LifecycleState.Faulted _ -> ()
        | other -> failtestf "the twin should still report a REAL fault correctly, got %A" other

      testCase "the twin still honors Stop — StopRequested's handling didn't change between real and twin" <| fun _ ->
        let t = runFlatBoundOnly (TimeSpan.FromMinutes 10.0) WarmupGenerators.stopAfterFault
        t.Final.State |> Expect.equal "Stopped" WarmupSupervision.LifecycleState.Stopped

      testPropertyWithConfig simConfig
        "the invariant has teeth: ANY pure-silence scenario longer than the inactivity bound (and shorter than the flat twin bound) discriminates real from twin" <|
        fun (PositiveInt extraSeconds) ->
          // A targeted generator, not the generic mixed `fromSeed` — this
          // property needs the EXACT shape the regression lives in: silence
          // beyond Bounds.Inactivity, comfortably under a flat 10-minute
          // bound, no Stop, no Ready/Faulted ever reported. `fromSeed`'s
          // generic mix rarely produces this shape (most random scenarios
          // contain a Ready/Faulted observation, which both reducers handle
          // identically and correctly) — a property that searched the wrong
          // space would pass vacuously without proving anything.
          let bounds = WarmupGenerators.defaultBounds
          let silentFor = bounds.Inactivity + TimeSpan.FromSeconds(float (extraSeconds % 200))
          let scenario =
            { Seed = extraSeconds
              Bounds = bounds
              Events = [ LifecycleEvent.ClockAdvance silentFor; LifecycleEvent.PollTick PollObservation.StillWarming ] }
          let realT = run scenario
          let twinT = runFlatBoundOnly (TimeSpan.FromMinutes 10.0) scenario
          let realViolated = violations realT |> List.map fst |> List.contains "silence-is-caught-by-inactivity"
          let twinViolated = violations twinT |> List.map fst |> List.contains "silence-is-caught-by-inactivity"
          match realViolated, twinViolated with
          | false, true -> ()
          | _ ->
            failtestf
              "expected the real reducer clean and the twin violated for %.0fs of silence (inactivity bound %.0fs) — real violated=%b twin violated=%b"
              silentFor.TotalSeconds bounds.Inactivity.TotalSeconds realViolated twinViolated
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical trace" <|
        fun (seed: int) ->
          let a = run (WarmupGenerators.fromSeed seed)
          let b = run (WarmupGenerators.fromSeed seed)
          a.Final |> Expect.equal "replaying the same seed yields the identical final fold state" b.Final
    ]
  ]
