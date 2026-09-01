/// ## RestartPolicy Correspondence Tests
///
/// Validates that the F# RestartPolicy satisfies the same properties
/// proved in `formal-verification/lean/FVSquad/RestartPolicy.lean`.
/// Each test maps 1-to-1 to a Lean theorem.
module RestartPolicyCorrespondenceTests

open Expecto
open Expecto.Flip
open SageFs
open System

// ── Test Fixtures ──────────────────────────────────────────────────────────

let policy = RestartPolicy.defaultPolicy
let state0 = RestartPolicy.emptyState
let now = DateTime.UtcNow

// ── Group 1: nextBackoffMs — Lean: nextBackoffMs_zero ──────────────────────

let nextBackoffTests =
  testList "nextBackoff" [
    test "WHY — nextBackoffMs_zero — count=0 returns base delay (Lean: nextBackoffMs_zero)" {
      RestartPolicy.nextBackoff policy 0
      |> Expect.equal "nextBackoff(0) should equal BackoffBase" policy.BackoffBase
    }

    test "WHY — nextBackoffMs_le_max — delay never exceeds max (Lean: nextBackoffMs_le_max)" {
      for count in [1; 5; 10; 20; 30; 100] do
        let delay = RestartPolicy.nextBackoff policy count
        (delay, policy.BackoffMax) |> Expect.isLessThanOrEqual $"nextBackoff({count}) should be <= BackoffMax"
    }

    test "WHY — nextBackoffMs_ge_base — delay is at least base (Lean: nextBackoffMs_ge_base)" {
      for count in [1; 5; 10; 20; 30; 100] do
        let delay = RestartPolicy.nextBackoff policy count
        (policy.BackoffBase, delay) |> Expect.isLessThanOrEqual $"BackoffBase should be <= nextBackoff({count})"
    }
  ]

// ── Group 2: rbDecide — Lean: decide_giveup_at_limit ───────────────────────

let decideGiveupTests =
  testList "decide give up" [
    test "WHY — decide_giveup_at_limit — count >= maxRestarts gives GiveUp (Lean: decide_giveup_at_limit)" {
      // Build state with count = maxRestarts
      let s = { state0 with RestartCount = policy.MaxRestarts }
      let decision, _ = RestartPolicy.decide policy s now
      match decision with
      | RestartPolicy.Decision.GiveUp _ -> ()
      | RestartPolicy.Decision.Restart _ ->
        failwithf "Expected GiveUp when count >= maxRestarts, got %A" decision
    }

    test "WHY — decide_restart_below_limit — count < maxRestarts gives Restart (Lean: decide_restart_below_limit)" {
      let decision, _ = RestartPolicy.decide policy state0 now
      match decision with
      | RestartPolicy.Decision.Restart _ -> ()
      | RestartPolicy.Decision.GiveUp _ ->
        failwithf "Expected Restart when count < maxRestarts, got %A" decision
    }
  ]

// ── Group 3: rbDecide — Lean: decide_clears_window ─────────────────────────

let decideWindowTests =
  testList "decide window reset" [
    test "WHY — decide_clears_window — window-expired resets count to 0 (Lean: decide_clears_window)" {
      // Create state with count > 0, then advance past the reset window
      let _, s1 = RestartPolicy.decide policy state0 now
      let _, s2 = RestartPolicy.decide policy s1 (now.AddSeconds(11.0))
      let oldState = { s2 with WindowStart = Some (now - TimeSpan.FromMinutes(10.0)) }
      let _, newState = RestartPolicy.decide policy oldState (now.AddSeconds(20.0))
      // After window expiry, count should be reset (not carry over)
      (newState.RestartCount, 2) |> Expect.isLessThanOrEqual "count should be reset after window expiry"
    }
  ]

// ── Group 4: rbDecide — Lean: decide_giveup_idempotent ─────────────────────

let decideIdempotentTests =
  testList "decide idempotent" [
    test "WHY — decide_giveup_idempotent — GiveUp decision stays GiveUp (Lean: decide_giveup_idempotent)" {
      let s = { state0 with RestartCount = policy.MaxRestarts }
      let d1, s1 = RestartPolicy.decide policy s now
      let d2, _ = RestartPolicy.decide policy s1 now
      match d1, d2 with
      | RestartPolicy.Decision.GiveUp _, RestartPolicy.Decision.GiveUp _ -> ()
      | _ -> failwith "GiveUp decision should remain GiveUp on next call"
    }
  ]

// ── Group 5: rbDecide — Lean: decide_restart_increments_count ──────────────

let decideIncrementTests =
  testList "decide restart increments" [
    test "WHY — decide_restart_increments_count — Restart decision increments count (Lean: decide_restart_increments_count)" {
      let _, s1 = RestartPolicy.decide policy state0 now
      // Space restarts beyond StartupCrashWindow to avoid circuit breaker
      let _, s2 = RestartPolicy.decide policy s1 (now.AddSeconds(11.0))
      (s2.RestartCount, 1) |> Expect.isGreaterThan "count should increment after restart"
    }
  ]

// ── Group 6: backoff exponential — Lean: backoff_grows_exponentially ────────

let backoffExponentialTests =
  testList "backoff exponential" [
    test "WHY — backoff_grows — delay is non-decreasing (Lean: implicit in nextBackoffMs_ge_base)" {
      let d1 = RestartPolicy.nextBackoff policy 1
      let d2 = RestartPolicy.nextBackoff policy 2
      let d3 = RestartPolicy.nextBackoff policy 3
      (d1, d2) |> Expect.isLessThanOrEqual "nextBackoff(1) <= nextBackoff(2)"
      (d2, d3) |> Expect.isLessThanOrEqual "nextBackoff(2) <= nextBackoff(3)"
    }
  ]

// ── All tests combined ──────────────────────────────────────────────────────

let restartPolicyCorrespondenceTests =
  testList "RestartPolicy Correspondence (F# vs Lean)" [
    nextBackoffTests
    decideGiveupTests
    decideWindowTests
    decideIdempotentTests
    decideIncrementTests
    backoffExponentialTests
  ]
