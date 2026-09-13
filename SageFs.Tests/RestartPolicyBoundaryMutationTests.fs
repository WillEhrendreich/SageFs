/// ## RestartPolicy Boundary Mutation Tests
///
/// Complements SessionLifecycleMutationTests.fs (which pins the common-path
/// `decide`/`nextBackoff` behavior) with the EXACT boundary conditions that a
/// `<` vs `<=`, `>` vs `>=`, or `min`/`max` swap mutant would flip: the
/// reset-window expiry edge, the startup-crash-window edge, the
/// startup-crash-ceiling-vs-MaxRestarts interaction, and backoff exponent
/// capping. Each case asserts EXACT equality against the correct value so
/// any other wrong value is killed too.
module RestartPolicyBoundaryMutationTests

open Expecto
open Expecto.Flip
open SageFs
open System

let policy = RestartPolicy.defaultPolicy
let now = DateTime.UtcNow

let restartPolicyBoundaryMutationTests = testList "RestartPolicy boundary mutations" [

  // ── ResetWindow expiry: `>` not `>=` ────────────────────────────────────

  testCase "WHY — resetWindow_exactly_at_boundary_does_not_reset — a restart AT the window edge must still count against it" <| fun () ->
    let state : RestartPolicy.State = { RestartCount = 2; LastRestartAt = Some (now - policy.ResetWindow); WindowStart = Some (now - policy.ResetWindow) }
    // Time elapsed since WindowStart is EXACTLY ResetWindow — `>` must be
    // false here, so the window has NOT expired and the count carries over.
    let decideAt = now - policy.ResetWindow + policy.ResetWindow
    RestartPolicy.decide policy state decideAt
    |> fst
    |> Expect.equal "exactly at the reset-window edge, restart count 2 -> 3, not reset to 1"
      (RestartPolicy.Decision.Restart(RestartPolicy.nextBackoff policy 3))

  testCase "WHY — resetWindow_one_tick_past_boundary_resets — a restart just past the window edge must reset the count" <| fun () ->
    let state : RestartPolicy.State = { RestartCount = 2; LastRestartAt = Some (now - policy.ResetWindow); WindowStart = Some (now - policy.ResetWindow) }
    let decideAt = now - policy.ResetWindow + policy.ResetWindow + TimeSpan.FromTicks 1L
    RestartPolicy.decide policy state decideAt
    |> fst
    |> Expect.equal "one tick past the reset-window edge, the count must reset and restart as if fresh"
      (RestartPolicy.Decision.Restart(RestartPolicy.nextBackoff policy 1))

  // ── StartupCrashWindow: `<=` not `<` ────────────────────────────────────

  testCase "WHY — startupCrashWindow_exactly_at_boundary_is_startup_crash — a crash exactly at the startup window edge is still a startup crash" <| fun () ->
    let s0 = RestartPolicy.emptyState
    let _, s1 = RestartPolicy.decide policy s0 now
    // s1.LastRestartAt = now. Deciding exactly StartupCrashWindow later must
    // still be classified as a startup crash (isStartupCrash uses `<=`).
    let decideAt = now + policy.StartupCrashWindow
    let real, _ = RestartPolicy.decide policy s1 decideAt
    real
    |> Expect.equal "exactly at the startup-crash-window edge, the 4x circuit-breaker backoff applies"
      (RestartPolicy.Decision.Restart(TimeSpan.FromMilliseconds(policy.BackoffBase.TotalMilliseconds * 4.0)))

  testCase "WHY — startupCrashWindow_one_tick_past_boundary_is_not_startup_crash — a crash just past the startup window edge is an ordinary crash" <| fun () ->
    let s0 = RestartPolicy.emptyState
    let _, s1 = RestartPolicy.decide policy s0 now
    let decideAt = now + policy.StartupCrashWindow + TimeSpan.FromTicks 1L
    let real, _ = RestartPolicy.decide policy s1 decideAt
    real
    |> Expect.equal "one tick past the startup-crash-window edge, ordinary exponential backoff applies (restart 2 = 2s)"
      (RestartPolicy.Decision.Restart(RestartPolicy.nextBackoff policy 2))

  // ── Startup-crash ceiling: min policy.StartupCrashMaxRestarts policy.MaxRestarts ──

  testCase "WHY — startupCrashCeiling_uses_min_of_both_limits — a startup ceiling ABOVE MaxRestarts must not raise the limit" <| fun () ->
    // If the effective max were just StartupCrashMaxRestarts (dropping the
    // `min`), this would give up at 10; the real ceiling must still be capped
    // to MaxRestarts (5) since the policy's ordinary limit is lower.
    let lenientPolicy = { policy with StartupCrashMaxRestarts = 10 }
    let s0 = RestartPolicy.emptyState
    let _, s1 = RestartPolicy.decide lenientPolicy s0 now
    let _, s2 = RestartPolicy.decide lenientPolicy s1 (now.AddSeconds 1.0)
    let _, s3 = RestartPolicy.decide lenientPolicy s2 (now.AddSeconds 2.0)
    let _, s4 = RestartPolicy.decide lenientPolicy s3 (now.AddSeconds 3.0)
    let _, s5 = RestartPolicy.decide lenientPolicy s4 (now.AddSeconds 4.0)
    let real, _ = RestartPolicy.decide lenientPolicy s5 (now.AddSeconds 5.0)
    real
    |> Expect.equal "the 6th rapid crash must give up at min(10, MaxRestarts=5) = 5, not 10"
      (RestartPolicy.Decision.GiveUp(SageFsError.RestartLimitExceeded(5, policy.ResetWindow.TotalMinutes)))

  // ── nextBackoff exponent cap: min restartCount 20 ───────────────────────

  testCase "WHY — nextBackoff_exponent_capped_at_20_beyond_20 — restart counts beyond 20 must not overflow the exponent" <| fun () ->
    (RestartPolicy.nextBackoff policy 20, RestartPolicy.nextBackoff policy 21, RestartPolicy.nextBackoff policy 1000)
    |> Expect.equal "restart counts 20, 21 and 1000 must all compute the SAME capped backoff (BackoffMax, since 2^19 already exceeds it)"
      (policy.BackoffMax, policy.BackoffMax, policy.BackoffMax)

  testCase "WHY — nextBackoff_negative_restartCount_returns_base — a negative count must not be treated as a positive exponent" <| fun () ->
    RestartPolicy.nextBackoff policy -5
    |> Expect.equal "a negative restart count must fall into the `<= 0` branch and return BackoffBase" policy.BackoffBase

  // ── WindowStart is set once, then preserved ─────────────────────────────

  testCase "WHY — windowStart_set_on_first_restart_then_preserved — WindowStart must anchor to the FIRST restart, not update on every restart" <| fun () ->
    let _, s1 = RestartPolicy.decide policy RestartPolicy.emptyState now
    let _, s2 = RestartPolicy.decide policy s1 (now.AddSeconds 1.0)
    let _, s3 = RestartPolicy.decide policy s2 (now.AddSeconds 2.0)
    s3.WindowStart
    |> Expect.equal "WindowStart must still be the time of the FIRST restart (now), not the third" (Some now)

  testCase "WHY — lastRestartAt_updates_on_every_restart — LastRestartAt must track the MOST RECENT restart, not the first" <| fun () ->
    let _, s1 = RestartPolicy.decide policy RestartPolicy.emptyState now
    let _, s2 = RestartPolicy.decide policy s1 (now.AddSeconds 1.0)
    let decideAt = now.AddSeconds 2.0
    let _, s3 = RestartPolicy.decide policy s2 decideAt
    s3.LastRestartAt
    |> Expect.equal "LastRestartAt must be the time of the MOST RECENT decide call" (Some decideAt)

  // ── RestartCount increments by exactly 1 ────────────────────────────────

  testCase "WHY — restartCount_increments_by_exactly_one — the count must not skip or double-count" <| fun () ->
    let _, s1 = RestartPolicy.decide policy RestartPolicy.emptyState now
    let _, s2 = RestartPolicy.decide policy s1 (now.AddSeconds 1.0)
    (s1.RestartCount, s2.RestartCount)
    |> Expect.equal "restart counts after 1 and 2 decisions must be exactly 1 and 2" (1, 2)
]
