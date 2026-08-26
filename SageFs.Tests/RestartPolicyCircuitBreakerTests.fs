module SageFs.Tests.RestartPolicyCircuitBreakerTests

open System
open Expecto
open SageFs

/// Phase 0 RED: prove the current RestartPolicy lacks the circuit-breaker
/// behavior the supervisor requires.
///
/// The plan requires: a host that crashes within T of startup (startup crash
/// loop) must back off FASTER (x4 instead of x2) and give up SOONER (ceiling
/// 3 instead of 5). Today's `RestartPolicy.decide` knows nothing about
/// crash timing — it treats a startup crash the same as a steady-state crash.
[<Tests>]
let tests =
  testList "RestartPolicy circuit breaker (RED)" [

    testCase "startup crash loop backs off 4x, not 2x" <| fun _ ->
      let policy = RestartPolicy.defaultPolicy
      let state = RestartPolicy.emptyState
      let now = DateTime(2026, 1, 1, 12, 0, 0)

      // Crash 1 at t=0, crash 2 at t+2s — both within a 10s startup window.
      let first, st1 = RestartPolicy.decide policy state now
      let second, _ = RestartPolicy.decide policy st1 (now.AddSeconds 2.0)

      // Expected (circuit breaker): the second backoff is 4x the first.
      let firstDelay =
        match first with
        | RestartPolicy.Decision.Restart d -> d
        | _ -> failwith "expected Restart"
      let secondDelay =
        match second with
        | RestartPolicy.Decision.Restart d -> d
        | _ -> failwith "expected Restart"

      // Today: 2x (1s -> 2s). Required: 4x (1s -> 4s).
      Expect.equal
        (secondDelay.TotalSeconds)
        (firstDelay.TotalSeconds * 4.0)
        "startup-crash backoff should be 4x the previous (circuit breaker)"

    testCase "startup crash loop gives up after 3 attempts, not 5" <| fun _ ->
      let policy = RestartPolicy.defaultPolicy
      let now = DateTime(2026, 1, 1, 12, 0, 0)

      // Simulate 3 rapid startup crashes (each within the 10s window).
      let rec crash n state =
        match n with
        | 0 -> state
        | _ ->
          let decision, st = RestartPolicy.decide policy state (now.AddSeconds(float n * 2.0))
          match decision with
          | RestartPolicy.Decision.Restart _ -> crash (n - 1) st
          | RestartPolicy.Decision.GiveUp _ -> state // stop early

      let after3 = crash 3 RestartPolicy.emptyState

      // The 4th decision must be GiveUp under a circuit breaker (ceiling 3
      // for startup crashes). Today: still Restart (ceiling 5).
      let decision, _ = RestartPolicy.decide policy after3 (now.AddSeconds 8.0)

      match decision with
      | RestartPolicy.Decision.GiveUp _ -> ()
      | RestartPolicy.Decision.Restart _ ->
        failtest "startup crash loop should give up after 3 attempts (circuit breaker)"
  ]
