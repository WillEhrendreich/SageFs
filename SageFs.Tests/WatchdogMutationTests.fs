/// ## Watchdog Mutation Tests
///
/// Proves the test suite catches mutations in the daemon-level watchdog's
/// pure `decide` function — a DU-case swap (Wait vs StartDaemon vs
/// RestartDaemon vs GiveUp) or a grace-period boundary flip here would let
/// the supervisor either spin uselessly or fail to restart a dead daemon.
/// Each case asserts EXACT equality against the correct value so a mutant
/// that returns any other wrong value is killed too.
module WatchdogMutationTests

open Expecto
open Expecto.Flip
open SageFs
open System

let config = Watchdog.defaultConfig
let now = DateTime.UtcNow

let watchdogMutationTests = testList "Watchdog mutations" [

  // ── decide: DaemonStatus -> Action, per case ────────────────────────────

  testCase "WHY — decide_running_waits — a running daemon must never trigger a restart action" <| fun () ->
    let state = Watchdog.emptyState now
    Watchdog.decide config state Watchdog.DaemonStatus.Running now
    |> Expect.equal "Running must map to (Wait, unchanged state)" (Watchdog.Action.Wait, state)

  testCase "WHY — decide_unknown_waits — an unknown status must never trigger a restart action (fail-safe)" <| fun () ->
    let state = Watchdog.emptyState now
    Watchdog.decide config state Watchdog.DaemonStatus.Unknown now
    |> Expect.equal "Unknown must map to (Wait, unchanged state)" (Watchdog.Action.Wait, state)

  testCase "WHY — decide_notRunning_never_started_starts_daemon — a daemon that was never started must be started, not restarted" <| fun () ->
    let state = Watchdog.emptyState now
    Watchdog.decide config state Watchdog.DaemonStatus.NotRunning now
    |> Expect.equal "NotRunning with no prior DaemonPid must map to (StartDaemon, unchanged state)" (Watchdog.Action.StartDaemon, state)

  testCase "WHY — decide_notRunning_within_grace_waits — a daemon still inside its grace period must not be restarted yet" <| fun () ->
    let started = now
    let state = { Watchdog.emptyState now with DaemonPid = Some 111; LastStartedAt = Some started }
    let checkAt = started + config.GracePeriod - TimeSpan.FromTicks 1L
    Watchdog.decide config state Watchdog.DaemonStatus.NotRunning checkAt
    |> Expect.equal "one tick before the grace period elapses, the watchdog must Wait, not restart" (Watchdog.Action.Wait, state)

  testCase "WHY — decide_notRunning_grace_boundary_proceeds_to_restart — EXACTLY at the grace period edge must no longer wait (`<` not `<=`)" <| fun () ->
    let started = now
    let state = { Watchdog.emptyState now with DaemonPid = Some 111; LastStartedAt = Some started }
    let checkAt = started + config.GracePeriod
    Watchdog.decide config state Watchdog.DaemonStatus.NotRunning checkAt
    |> Expect.equal "exactly at the grace-period boundary, the elapsed time is NOT < GracePeriod, so the watchdog proceeds to a restart decision, not Wait"
      (Watchdog.Action.RestartDaemon(RestartPolicy.nextBackoff config.RestartPolicy 1), { state with RestartState = { RestartPolicy.RestartCount = 1; LastRestartAt = Some checkAt; WindowStart = Some checkAt } })

  testCase "WHY — decide_notRunning_past_grace_restarts — a daemon past its grace period with no restart history must restart with the first backoff" <| fun () ->
    let started = now
    let state = { Watchdog.emptyState now with DaemonPid = Some 111; LastStartedAt = Some started }
    let checkAt = started + config.GracePeriod + TimeSpan.FromSeconds 1.0
    let expectedRestartState : RestartPolicy.State =
      { RestartCount = 1; LastRestartAt = Some checkAt; WindowStart = Some checkAt }
    Watchdog.decide config state Watchdog.DaemonStatus.NotRunning checkAt
    |> Expect.equal "past grace with no history must restart after the policy's first backoff, carrying the new restart state"
      (Watchdog.Action.RestartDaemon(RestartPolicy.nextBackoff config.RestartPolicy 1), { state with RestartState = expectedRestartState })

  testCase "WHY — decide_notRunning_exhausted_gives_up — a daemon that has exhausted its restart budget must GiveUp, not loop forever" <| fun () ->
    let started = now
    // Drive RestartPolicy to its give-up state directly (spaced past the
    // startup-crash window so the circuit breaker doesn't lower the ceiling).
    let rp = config.RestartPolicy
    let s0 = RestartPolicy.emptyState
    let _, s1 = RestartPolicy.decide rp s0 now
    let _, s2 = RestartPolicy.decide rp s1 (now.AddSeconds 11.0)
    let _, s3 = RestartPolicy.decide rp s2 (now.AddSeconds 22.0)
    let _, s4 = RestartPolicy.decide rp s3 (now.AddSeconds 33.0)
    let _, s5 = RestartPolicy.decide rp s4 (now.AddSeconds 44.0)
    let state = { Watchdog.emptyState now with DaemonPid = Some 111; LastStartedAt = Some started; RestartState = s5 }
    let checkAt = started + config.GracePeriod + TimeSpan.FromSeconds 55.0
    let expectedError = SageFsError.RestartLimitExceeded(5, rp.ResetWindow.TotalMinutes)
    let _, expectedRestartState = RestartPolicy.decide rp s5 checkAt
    Watchdog.decide config state Watchdog.DaemonStatus.NotRunning checkAt
    |> Expect.equal "an exhausted restart budget must GiveUp with the error's description, carrying the exhausted restart state"
      (Watchdog.Action.GiveUp(SageFsError.describe expectedError), { state with RestartState = expectedRestartState })

  // ── recordStart ──────────────────────────────────────────────────────────

  testCase "WHY — recordStart_sets_pid_and_startedAt_only — recordStart must not touch RestartState or WatchdogStartedAt" <| fun () ->
    let state = Watchdog.emptyState now
    let started = now.AddMinutes 5.0
    Watchdog.recordStart 999 started state
    |> Expect.equal "recordStart must set exactly DaemonPid and LastStartedAt, leaving everything else unchanged"
      { state with DaemonPid = Some 999; LastStartedAt = Some started }

  // ── emptyState ───────────────────────────────────────────────────────────

  testCase "WHY — emptyState_has_no_pid_and_fresh_restart_state — a freshly constructed watchdog must have no prior history" <| fun () ->
    Watchdog.emptyState now
    |> Expect.equal "emptyState must have DaemonPid=None, RestartState=RestartPolicy.emptyState, LastStartedAt=None, WatchdogStartedAt=now"
      { DaemonPid = None; RestartState = RestartPolicy.emptyState; LastStartedAt = None; WatchdogStartedAt = now }
]
