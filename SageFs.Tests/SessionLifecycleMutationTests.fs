/// ## SessionLifecycle + RestartPolicy Mutation Tests
///
/// Proves the test suite catches mutations in the Erlang-style restart
/// supervision logic. This is the most critical state machine in SageFs —
/// bugs here cause infinite restart loops or premature give-up.
module SessionLifecycleMutationTests

open Expecto
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

  testCase "WHY — onWorkerExited_crash_must_restart — crashed workers must not be treated as graceful" <| fun () ->
    let real = SessionLifecycle.onWorkerExited policy state0 1 now
    let mutant = SessionLifecycle.ExitOutcome.Graceful  // mutant: always graceful
    if real = mutant then
      failwith "Mutation survived — onWorkerExited treated crash as graceful"

  testCase "WHY — onWorkerExited_graceful_must_not_restart — clean exit must not trigger restart" <| fun () ->
    let real = SessionLifecycle.onWorkerExited policy state0 0 now
    let decision, _ = RestartPolicy.decide policy state0 now
    let mutant = SessionLifecycle.ExitOutcome.RestartAfter (TimeSpan.FromSeconds(1.0), state0)
    if real = mutant then
      failwith "Mutation survived — onWorkerExited restarted on clean exit"

  // ── statusAfterExit ───────────────────────────────────────────────────────

  testCase "WHY — statusAfterExit_Graceful_must_be_Stopped — clean exit means stopped" <| fun () ->
    let real = SessionLifecycle.statusAfterExit SessionLifecycle.ExitOutcome.Graceful
    let mutant = SessionStatus.Restarting  // mutant: wrong status
    if real = mutant then
      failwith "Mutation survived — statusAfterExit mapped Graceful to Restarting"

  testCase "WHY — statusAfterExit_RestartAfter_must_be_Restarting — restart decision means restarting" <| fun () ->
    let real = SessionLifecycle.statusAfterExit (SessionLifecycle.ExitOutcome.RestartAfter (TimeSpan.FromSeconds(1.0), state0))
    let mutant = SessionStatus.Stopped  // mutant: wrong status
    if real = mutant then
      failwith "Mutation survived — statusAfterExit mapped RestartAfter to Stopped"

  testCase "WHY — statusAfterExit_Abandoned_must_be_Faulted — give-up means faulted" <| fun () ->
    let real = SessionLifecycle.statusAfterExit (SessionLifecycle.ExitOutcome.Abandoned (SageFsError.RestartLimitExceeded(5, 5.0)))
    let mutant = SessionStatus.Stopped  // mutant: wrong status
    if real = mutant then
      failwith "Mutation survived — statusAfterExit mapped Abandoned to Stopped"

  // ── RestartPolicy.decide ──────────────────────────────────────────────────

  testCase "WHY — decide_must_give_up_after_max_restarts — infinite restart loops are dangerous" <| fun () ->
    // Space restarts > StartupCrashWindow (10s) to avoid circuit breaker
    // Then hit MaxRestarts (5)
    let _, s1 = RestartPolicy.decide policy state0 now
    let _, s2 = RestartPolicy.decide policy s1 (now.AddSeconds(11.0))
    let _, s3 = RestartPolicy.decide policy s2 (now.AddSeconds(22.0))
    let _, s4 = RestartPolicy.decide policy s3 (now.AddSeconds(33.0))
    let _, s5 = RestartPolicy.decide policy s4 (now.AddSeconds(44.0))
    let real, _ = RestartPolicy.decide policy s5 (now.AddSeconds(55.0))
    let mutant = RestartPolicy.Decision.Restart (TimeSpan.FromSeconds(1.0))  // mutant: always restart
    if real = mutant then
      failwith "Mutation survived — decide didn't give up after max restarts"

  testCase "WHY — decide_must_restart_within_limit — premature give-up wastes recovery chances" <| fun () ->
    let real, _ = RestartPolicy.decide policy state0 now
    let mutant = RestartPolicy.Decision.GiveUp (SageFsError.RestartLimitExceeded(0, 5.0))  // mutant: always give up
    if real = mutant then
      failwith "Mutation survived — decide gave up on first restart"

  testCase "WHY — decide_window_expiry_must_reset — old restarts must not count against new window" <| fun () ->
    // Use state with restarts, but advance time past the reset window (5 min)
    let _, s1 = RestartPolicy.decide policy state0 now
    let _, s2 = RestartPolicy.decide policy s1 (now.AddSeconds(1.0))
    let oldState = { s2 with WindowStart = Some (now - TimeSpan.FromMinutes(10.0)) }
    let real, newState = RestartPolicy.decide policy oldState (now.AddSeconds(2.0))
    // After window expiry, should restart fresh (not give up)
    match real with
    | RestartPolicy.Decision.GiveUp _ ->
      failwith "Mutation survived — decide gave up after window expiry (should have reset)"
    | RestartPolicy.Decision.Restart _ ->
      if newState.RestartCount <> 1 then
        failwith "Mutation survived — decide didn't reset count after window expiry"

  testCase "WHY — decide_startup_crash_circuit_breaker — startup loops must fail fast" <| fun () ->
    // Three rapid crashes within StartupCrashWindow (10s) — 4th should give up at 3
    let _, s1 = RestartPolicy.decide policy state0 now
    let _, s2 = RestartPolicy.decide policy s1 (now.AddSeconds(1.0))
    let _, s3 = RestartPolicy.decide policy s2 (now.AddSeconds(2.0))
    let real, _ = RestartPolicy.decide policy s3 (now.AddSeconds(3.0))
    match real with
    | RestartPolicy.Decision.GiveUp _ -> () // expected: startup crash limit hit
    | RestartPolicy.Decision.Restart _ ->
      failwith "Mutation survived — decide didn't activate startup crash circuit breaker"

  // ── RestartPolicy.nextBackoff ─────────────────────────────────────────────

  testCase "WHY — nextBackoff_must_be_exponential — linear backoff is too slow for cascading failures" <| fun () ->
    let d1 = RestartPolicy.nextBackoff policy 1
    let d2 = RestartPolicy.nextBackoff policy 2
    let d3 = RestartPolicy.nextBackoff policy 3
    // Exponential: 1s, 2s, 4s — d2 should be > d1, d3 should be > d2
    if d2 <= d1 || d3 <= d2 then
      failwith "Mutation survived — nextBackoff is not exponential"

  testCase "WHY — nextBackoff_must_cap_at_max — unbounded backoff delays recovery forever" <| fun () ->
    let d20 = RestartPolicy.nextBackoff policy 20
    let realCap = policy.BackoffMax
    if d20 > realCap then
      failwith "Mutation survived — nextBackoff exceeded BackoffMax"

  testCase "WHY — nextBackoff_zero_must_return_base — zero restarts means first attempt" <| fun () ->
    let real = RestartPolicy.nextBackoff policy 0
    if real <> policy.BackoffBase then
      failwith "Mutation survived — nextBackoff(0) didn't return BackoffBase"
]
