/// ## SessionLifecycle + RestartPolicy Mutation Tests
///
/// Proves the test suite catches mutations in the Erlang-style restart
/// supervision logic. This is the most critical state machine in SageFs —
/// bugs here cause infinite restart loops or premature give-up.
///
/// Each case asserts EXACT equality against the correct value (not merely
/// inequality with one hand-picked wrong value) so a mutant that returns any
/// other wrong value is killed too.
module SessionLifecycleMutationTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open System

// ── Test Fixtures ──────────────────────────────────────────────────────────

let policy = RestartPolicy.defaultPolicy
let state0 = RestartPolicy.emptyState
let now = DateTime.UtcNow

// ── Mutation Tests ─────────────────────────────────────────────────────────

let sessionLifecycleMutationTests = testList "SessionLifecycle mutations" [

  // ── onWorkerExited ────────────────────────────────────────────────────────

  testCase "WHY — onWorkerExited_crash_restarts_with_first_backoff — crashed workers must restart, not exit gracefully" <| fun () ->
    let expectedState : RestartPolicy.State =
      { RestartCount = 1; LastRestartAt = Some now; WindowStart = Some now }
    SessionLifecycle.onWorkerExited policy state0 1 now
    |> Expect.equal "a crash (exit 1) from empty state must restart after 1s with RestartCount=1"
      (SessionLifecycle.ExitOutcome.RestartAfter(TimeSpan.FromSeconds 1.0, expectedState))

  testCase "WHY — onWorkerExited_graceful_is_Graceful — clean exit must not trigger restart" <| fun () ->
    SessionLifecycle.onWorkerExited policy state0 0 now
    |> Expect.equal "a clean exit (code 0) must be Graceful" SessionLifecycle.ExitOutcome.Graceful

  // ── statusAfterExit ───────────────────────────────────────────────────────

  testCase "WHY — statusAfterExit_Graceful_is_Stopped — clean exit means stopped" <| fun () ->
    SessionLifecycle.statusAfterExit None SessionLifecycle.ExitOutcome.Graceful
    |> Expect.equal "Graceful must map to Stopped" SessionLifecycleStatus.Stopped

  testCase "WHY — statusAfterExit_RestartAfter_is_Restarting_with_exited_pid — restart decision carries the exited worker's pid" <| fun () ->
    SessionLifecycle.statusAfterExit (Some 4242) (SessionLifecycle.ExitOutcome.RestartAfter (TimeSpan.FromSeconds(1.0), state0))
    |> Expect.equal "RestartAfter must map to Restarting (Some 4242)" (SessionLifecycleStatus.Restarting (Some 4242))

  testCase "WHY — statusAfterExit_Abandoned_is_Faulted_with_description — give-up means faulted, described" <| fun () ->
    let error = SageFsError.RestartLimitExceeded(5, 5.0)
    SessionLifecycle.statusAfterExit None (SessionLifecycle.ExitOutcome.Abandoned error)
    |> Expect.equal "Abandoned must map to Faulted carrying the error's description" (SessionLifecycleStatus.Faulted (Some (SageFsError.describe error)))

  // ── RestartPolicy.decide ──────────────────────────────────────────────────

  testCase "WHY — decide_gives_up_at_exactly_max_restarts — infinite restart loops are dangerous" <| fun () ->
    // Space restarts > StartupCrashWindow (10s) to avoid the circuit breaker,
    // then hit MaxRestarts (5) exactly on the 6th decision.
    let _, s1 = RestartPolicy.decide policy state0 now
    let _, s2 = RestartPolicy.decide policy s1 (now.AddSeconds(11.0))
    let _, s3 = RestartPolicy.decide policy s2 (now.AddSeconds(22.0))
    let _, s4 = RestartPolicy.decide policy s3 (now.AddSeconds(33.0))
    let _, s5 = RestartPolicy.decide policy s4 (now.AddSeconds(44.0))
    let real, _ = RestartPolicy.decide policy s5 (now.AddSeconds(55.0))
    real
    |> Expect.equal "the 6th decision at RestartCount=5 must give up with RestartLimitExceeded(5, 5.0)"
      (RestartPolicy.Decision.GiveUp(SageFsError.RestartLimitExceeded(5, 5.0)))

  testCase "WHY — decide_restarts_on_first_crash_with_count_1 — premature give-up wastes recovery chances" <| fun () ->
    let expectedState : RestartPolicy.State =
      { RestartCount = 1; LastRestartAt = Some now; WindowStart = Some now }
    RestartPolicy.decide policy state0 now
    |> Expect.equal "the first decision from empty state must restart after 1s with RestartCount=1"
      (RestartPolicy.Decision.Restart(TimeSpan.FromSeconds 1.0), expectedState)

  testCase "WHY — decide_window_expiry_resets_count_to_1 — old restarts must not count against a new window" <| fun () ->
    // Use state with restarts, but advance time past the reset window (5 min)
    let _, s1 = RestartPolicy.decide policy state0 now
    let _, s2 = RestartPolicy.decide policy s1 (now.AddSeconds(1.0))
    let oldState = { s2 with WindowStart = Some (now - TimeSpan.FromMinutes(10.0)) }
    let decideAt = now.AddSeconds(2.0)
    let expectedState : RestartPolicy.State =
      { RestartCount = 1; LastRestartAt = Some decideAt; WindowStart = Some decideAt }
    RestartPolicy.decide policy oldState decideAt
    |> Expect.equal "after window expiry, the count must reset and restart fresh as if it were the first crash"
      (RestartPolicy.Decision.Restart(TimeSpan.FromSeconds 1.0), expectedState)

  testCase "WHY — decide_startup_crash_circuit_breaker_gives_up_at_3 — startup loops must fail fast, below MaxRestarts" <| fun () ->
    // Three rapid crashes within StartupCrashWindow (10s) — the 4th must give
    // up at StartupCrashMaxRestarts (3), not the higher MaxRestarts (5).
    let _, s1 = RestartPolicy.decide policy state0 now
    let _, s2 = RestartPolicy.decide policy s1 (now.AddSeconds(1.0))
    let _, s3 = RestartPolicy.decide policy s2 (now.AddSeconds(2.0))
    let real, _ = RestartPolicy.decide policy s3 (now.AddSeconds(3.0))
    real
    |> Expect.equal "the 4th rapid crash must give up with RestartLimitExceeded(3, 5.0), not restart"
      (RestartPolicy.Decision.GiveUp(SageFsError.RestartLimitExceeded(3, 5.0)))

  // ── RestartPolicy.nextBackoff ─────────────────────────────────────────────

  testCase "WHY — nextBackoff_is_exactly_exponential — linear backoff is too slow for cascading failures" <| fun () ->
    (RestartPolicy.nextBackoff policy 1, RestartPolicy.nextBackoff policy 2, RestartPolicy.nextBackoff policy 3)
    |> Expect.equal "backoff for restarts 1,2,3 must be exactly 1s, 2s, 4s"
      (TimeSpan.FromSeconds 1.0, TimeSpan.FromSeconds 2.0, TimeSpan.FromSeconds 4.0)

  testCase "WHY — nextBackoff_caps_at_BackoffMax — unbounded backoff delays recovery forever" <| fun () ->
    RestartPolicy.nextBackoff policy 20
    |> Expect.equal "backoff at restart 20 must be capped to exactly BackoffMax (30s)" policy.BackoffMax

  testCase "WHY — nextBackoff_zero_returns_BackoffBase — zero restarts means first attempt" <| fun () ->
    RestartPolicy.nextBackoff policy 0
    |> Expect.equal "backoff at restart 0 must be exactly BackoffBase" policy.BackoffBase
]
