/// Roast-6 #3: production only detects a dead worker PROCESS
/// (`proc.Exited`) and stalled warmup — a worker that stays alive but stops
/// answering HTTP is never reaped. These tests prove the pure decision core
/// (`WorkerHealthProbe.decide`) and the generic probe loop (`.run`) close
/// that gap: consecutive misses restart at the threshold, a Healthy probe
/// resets the streak, and a probe that cannot complete (exception) is
/// fail-closed to a miss rather than silently counted as healthy.
module SageFs.Tests.WorkerHealthProbeTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.WorkerHealthProbe

let decideTests =
  testList "WorkerHealthProbe.decide" [

    testProperty "a Healthy outcome always resets the miss streak to 0" <|
      fun (PositiveInt threshold) (NonNegativeInt missCount) ->
        decide threshold missCount ProbeOutcome.Healthy
        |> Expect.equal "Healthy resets to Continue 0" (Decision.Continue 0)

    testProperty "a Missed outcome restarts exactly when the streak reaches the threshold" <|
      fun (PositiveInt threshold) (NonNegativeInt missCount) ->
        let expected =
          match missCount + 1 >= threshold with
          | true -> Decision.Restart
          | false -> Decision.Continue(missCount + 1)
        decide threshold missCount ProbeOutcome.Missed
        |> Expect.equal "Missed increments, restarting at/over threshold" expected

    testCase "threshold 1: a single Missed restarts immediately" <| fun () ->
      decide 1 0 ProbeOutcome.Missed
      |> Expect.equal "one miss is enough at threshold 1" Decision.Restart

    testCase "threshold 3: two misses continue, a third restarts" <| fun () ->
      decide 3 0 ProbeOutcome.Missed |> Expect.equal "miss 1 of 3" (Decision.Continue 1)
      decide 3 1 ProbeOutcome.Missed |> Expect.equal "miss 2 of 3" (Decision.Continue 2)
      decide 3 2 ProbeOutcome.Missed |> Expect.equal "miss 3 of 3 restarts" Decision.Restart

    testCase "a Healthy probe between misses resets the streak — it never accumulates across gaps" <| fun () ->
      let afterMiss1 =
        match decide 3 0 ProbeOutcome.Missed with
        | Decision.Continue n -> n
        | Decision.Restart -> failtest "should not restart after one miss at threshold 3"
      let afterHealthy =
        match decide 3 afterMiss1 ProbeOutcome.Healthy with
        | Decision.Continue n -> n
        | Decision.Restart -> failtest "Healthy must never restart"
      afterHealthy |> Expect.equal "streak reset to 0" 0
      decide 3 afterHealthy ProbeOutcome.Missed
      |> Expect.equal "a fresh miss after the reset is only miss 1 of 3 again" (Decision.Continue 1)
  ]

let runLoopTests =
  testList "WorkerHealthProbe.run" [

    testTask "fail-closed: a probe that always throws restarts after exactly `threshold` iterations" {
      let mutable restartCount = 0
      let mutable probeCalls = 0
      let throwingProbe () : Async<ProbeOutcome> =
        async {
          probeCalls <- probeCalls + 1
          return raise (InvalidOperationException "simulated transport failure")
        }
      let threshold = 3
      let loop =
        run throwingProbe threshold 5 (fun () -> true) (fun () -> restartCount <- restartCount + 1)
      do! loop |> Async.StartAsTask :> Task

      restartCount |> Expect.equal "onRestart fired exactly once" 1
      probeCalls |> Expect.equal "exactly `threshold` probes ran before restarting" threshold
    }

    testTask "a consistently Healthy probe never restarts, even after many intervals" {
      let mutable restartCount = 0
      let mutable probeCalls = 0
      let healthyProbe () : Async<ProbeOutcome> =
        async {
          probeCalls <- probeCalls + 1
          return ProbeOutcome.Healthy
        }
      let stopAfter = DateTime.UtcNow.AddMilliseconds 120.0
      let shouldContinue () = DateTime.UtcNow < stopAfter
      do!
        run healthyProbe 3 10 shouldContinue (fun () -> restartCount <- restartCount + 1)
        |> Async.StartAsTask :> Task

      restartCount |> Expect.equal "never restarted" 0
      (probeCalls > 0) |> Expect.isTrue "the probe actually ran at least once"
    }

    testTask "shouldContinue turning false stops the loop before the threshold is reached" {
      let mutable restartCount = 0
      let mutable probeCalls = 0
      let missingProbe () : Async<ProbeOutcome> =
        async {
          probeCalls <- probeCalls + 1
          return ProbeOutcome.Missed
        }
      // The worker this loop watches gets replaced after two misses — e.g. a
      // manual hard reset raced the probe. Driven off the probe-call count
      // (not wall-clock timing) so the stop point is deterministic: exactly
      // 2 misses run, well short of a threshold of 5, then the loop must
      // stop on its own rather than restart a worker it no longer owns.
      let shouldContinue () = probeCalls < 2
      do!
        run missingProbe 5 1 shouldContinue (fun () -> restartCount <- restartCount + 1)
        |> Async.StartAsTask :> Task

      probeCalls |> Expect.equal "stopped after exactly 2 misses" 2
      restartCount |> Expect.equal "never restarted once shouldContinue turned false" 0
    }
  ]

[<Tests>]
let workerHealthProbeTests =
  testList "WorkerHealthProbe" [ decideTests; runLoopTests ]
