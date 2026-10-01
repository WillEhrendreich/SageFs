namespace SageFs

open System
open System.Threading

/// Validated timeout value — enforces 1s–10min range at construction.
type ValidTimeout = private ValidTimeout of TimeSpan

[<RequireQualifiedAccess>]
module ValidTimeout =
  let private minTimeout = TimeSpan.FromSeconds(1.0)
  let private maxTimeout = TimeSpan.FromMinutes(10.0)

  let create (t: TimeSpan) =
    match t >= minTimeout && t <= maxTimeout with
    | true -> Ok (ValidTimeout t)
    | false -> Error (sprintf "Timeout must be between 1s and 10min, got %A" t)

  let value (ValidTimeout t) = t

/// Centralized timeout and interval constants.
/// All timeouts in one place for discoverability and future configurability.
/// Top-5 timeouts support environment variable overrides (read once at startup).
[<RequireQualifiedAccess>]
module Timeouts =

  // -- Environment variable helpers --

  let private envOrDefault (varName: string) (defaultSeconds: float) =
    match Environment.GetEnvironmentVariable(varName) with
    | null | "" -> TimeSpan.FromSeconds(defaultSeconds)
    | value ->
      match Double.TryParse(value) with
      | true, v when v > 0.0 -> TimeSpan.FromSeconds(v)
      | _ -> TimeSpan.FromSeconds(defaultSeconds)

  let private envOrDefaultMinutes (varName: string) (defaultMinutes: float) =
    match Environment.GetEnvironmentVariable(varName) with
    | null | "" -> TimeSpan.FromMinutes(defaultMinutes)
    | value ->
      match Double.TryParse(value) with
      | true, v when v > 0.0 -> TimeSpan.FromMinutes(v)
      | _ -> TimeSpan.FromMinutes(defaultMinutes)

  // -- Build & Warmup --
  let warmupAbsoluteMax = envOrDefaultMinutes "SAGEFS_WARMUP_MAX_MINUTES" 10.0
  let warmupInactivityLimit = envOrDefault "SAGEFS_WARMUP_INACTIVITY_SECONDS" 30.0
  let softResetCancellation = TimeSpan.FromMinutes(5.0)
  let initSessionCancellation = TimeSpan.FromMinutes(5.0)

  // -- HTTP / Worker Communication --
  let workerHttpRead = envOrDefault "SAGEFS_WORKER_HTTP_READ_SECONDS" 30.0
  /// Bounded request timeout for worker HTTP calls (eval/check/typecheck/
  /// reset/etc). An eval that hangs in the worker must not hang the caller
  /// forever; 10 minutes matches the ValidTimeout max and the build cap, so
  /// legitimately long evals still complete while a wedged worker eventually
  /// surfaces as a timeout error instead of an infinite hang.
  let workerHttpRequest = envOrDefaultMinutes "SAGEFS_WORKER_HTTP_REQUEST_MINUTES" 10.0
  /// How long the tool-call gate waits for a worker's /status answer before it
  /// refuses the call. The worker answers status while it evaluates, so a probe
  /// that takes this long means a hung or starved worker, and the caller should
  /// hear that now, not after workerHttpRequest's ten minutes.
  let gateStatusProbe = TimeSpan.FromSeconds(5.0)
  /// The longest `wait_seconds` get_session_status will park a caller for.
  /// A larger request is clamped to this, never refused.
  let statusWaitCap = TimeSpan.FromSeconds(60.0)
  let healthCheck = TimeSpan.FromSeconds(2.0)
  let shutdownHttpClient = TimeSpan.FromSeconds(5.0)
  let sseKeepAlive = TimeSpan.FromHours(24.0)

  // -- Update / staleness check (issue #136) --
  /// Hard timeout on the NuGet flat-container GET. Any failure (including a
  /// timeout) must be swallowed by the caller into `CheckFailed`, never
  /// block daemon startup, and never slow a session.
  let updateCheckFetch = envOrDefault "SAGEFS_UPDATE_CHECK_TIMEOUT_SECONDS" 3.0
  /// How often the daemon re-asks NuGet whether a newer version exists.
  /// Checked once on daemon start (if the on-disk cache is older than this)
  /// and re-checked on this cadence for the lifetime of a long-running
  /// daemon — the whole point being to catch a daemon that has been up long
  /// enough to go stale without anyone restarting it.
  let updateCheckInterval = envOrDefaultMinutes "SAGEFS_UPDATE_CHECK_INTERVAL_MINUTES" (6.0 * 60.0)

  // -- Live Testing (configurable at runtime via MCP, thread-safe) --
  let private _perTestLock = obj ()
  let private _globalTestLock = obj ()
  /// The per-test timeout when neither the environment nor a setting says
  /// otherwise. Also the value `SettingsCatalog` falls back to if a requested
  /// per-test timeout is outside ValidTimeout's range. No recorded reason for 5s.
  let perTestTimeoutFallback = TimeSpan.FromSeconds(5.0)
  let mutable private _perTestDefault =
    envOrDefault "SAGEFS_PER_TEST_TIMEOUT_SECONDS" perTestTimeoutFallback.TotalSeconds
  let mutable private _globalTestRun = TimeSpan.FromMinutes(2.0)
  let perTestDefault () = lock _perTestLock (fun () -> _perTestDefault)
  let globalTestRun () = lock _globalTestLock (fun () -> _globalTestRun)

  let setPerTestTimeout (t: TimeSpan) =
    match ValidTimeout.create t with
    | Ok _ -> lock _perTestLock (fun () -> _perTestDefault <- t)
    | Error _ -> ()

  let setGlobalTestRunTimeout (t: TimeSpan) =
    match ValidTimeout.create t with
    | Ok _ -> lock _globalTestLock (fun () -> _globalTestRun <- t)
    | Error _ -> ()

  // -- Process Management --
  /// How long a `dotnet build` run by a session start or rebuild may take before
  /// SessionBuild kills it and the session faults with `BuildFailure.TimedOut`.
  /// Large repos need minutes; 10 matches the ValidTimeout max.
  let buildCompletion = envOrDefaultMinutes "SAGEFS_BUILD_TIMEOUT_MINUTES" 10.0
  let processNormalExit = TimeSpan.FromSeconds(3.0)
  /// How long the failure path waits for a worker's stderr reader to reach EOF
  /// once stdout has closed. stderr closes with the process, so this normally
  /// costs nothing; the bound only matters when a grandchild holds the pipe
  /// open. No recorded reason for 2s.
  let stderrDrainGrace = TimeSpan.FromSeconds(2.0)
  /// How long a session start waits for another process's build of the same FSI
  /// host to finish (the cross-process build lock in FsiHostBuild). A cold host
  /// build is the slowest thing the lock guards, so this is a build-sized
  /// budget. No recorded reason for 5 minutes.
  let hostBuildLockWait = envOrDefaultMinutes "SAGEFS_HOST_BUILD_LOCK_MINUTES" 5.0
  /// How often the host build lock is retried while another process holds it.
  /// This is a poll; the lock is a file handle with no way to wait on it.
  let hostBuildLockPoll = TimeSpan.FromMilliseconds(200.0)

  // -- Cohort landing gate --
  /// How long the landing gate waits for the integration session to settle to a
  /// trustworthy state after a rebase before giving up (a terminal/dead session
  /// fails fast via SessionTrust.settleDecision; this bounds only a genuinely
  /// warming session). Env-overridable.
  let cohortIntegrationSettle = envOrDefault "SAGEFS_COHORT_SETTLE_SECONDS" 120.0
  // `cohortRediscover` (a shared-generation settle-window ceiling) retired by
  // the F17 attributable-settle close: `rediscoverRebasedFiles`
  // (SageFs/DaemonMode.fs) no longer waits on a generation counter that
  // might never move — it directly awaits each rebased file's own re-eval
  // RPC, then the conservative verification run's own attributable
  // generation via `CohortLandingVerify.runTestsInSession`, which is bounded
  // by `CohortLandingVerify.awaitBudget()` (globalTestRun + slack) instead.
  /// Poll cadence while waiting on the settle / rediscovery generation signals.
  let cohortLandingPoll = TimeSpan.FromMilliseconds(200.0)
  /// How long a cohort member may be silent before the reaper departs it and
  /// orphans its claims. Silence, not busyness, costs a seat: any tool call and
  /// any eval renews the lease, so this is generous. No recorded reason for 30
  /// minutes.
  let cohortLeaseWindow = TimeSpan.FromMinutes(30.0)
  /// How long settled cohort history (orphaned and released claims, departed
  /// members, settled landings) stays before the sweep removes it. The same
  /// window as `cohortLeaseWindow` on purpose: a member's seat and the claims
  /// that name it age out together.
  let cohortSettledRetention = cohortLeaseWindow
  let processKillVerify = TimeSpan.FromSeconds(2.0)
  let stdioFlush = TimeSpan.FromSeconds(5.0)

  // -- Agent presence --
  /// How long an agent may go without a tool call before it is treated as gone:
  /// the activity tracker's periodic sweep evicts it, and SessionMap occupancy
  /// claims older than this are dropped. No recorded reason for 5 minutes.
  let agentPresenceEviction = TimeSpan.FromMinutes(5.0)
  /// How recently an agent must have been active to count as present right now:
  /// a fresh (not stale) dashboard badge, a cohort lease renewal by the reaper,
  /// and the "is any client still around" window for daemon idle shutdown.
  /// Shorter than `agentPresenceEviction`, so an active member is renewed before
  /// its presence is evicted.
  let agentActivityFresh = TimeSpan.FromMinutes(2.0)

  // -- File and process time tolerances --
  /// Slack when comparing a started process's start time to a recorded one. PID
  /// reuse needs a process started seconds to hours later, so this sits far
  /// above the read jitter and far below any realistic reuse gap. Protocol
  /// constant, not tunable.
  let processStartTimeTolerance = TimeSpan.FromSeconds(2.0)
  /// Slack when comparing file write times: copy and build pipelines can move a
  /// timestamp by a few hundred ms without the bytes changing, and a same-version
  /// build must not be flagged stale from clock noise. Protocol constant, not
  /// tunable.
  let fileWriteTimeTolerance = TimeSpan.FromSeconds(2.0)

  // -- Memory pressure --
  /// How long a session may go without activity before the memory supervisor
  /// may shed it under pressure. No recorded reason for 30 minutes.
  let memoryIdleShedAfter = TimeSpan.FromMinutes(30.0)

  // -- Expensive-work leases (ExpensiveWorkLease) --
  // A lease TTL is how long a granted lease is good for before it is reclaimed
  // as abandoned; it should exceed the work it guards.
  /// Session create or warmup lease. No recorded reason for 5 minutes.
  let leaseTtlSessionCreate = TimeSpan.FromMinutes(5.0)
  /// Rebuild lease: the build kill timer, so a lease outlives a build exactly as
  /// long as the build itself is allowed to run.
  let leaseTtlRebuild = buildCompletion
  /// Full `dotnet build` lease: the build kill timer, as for `leaseTtlRebuild`.
  let leaseTtlFullBuild = buildCompletion
  /// Test-suite run lease. No recorded reason for 15 minutes.
  let leaseTtlTestSuite = TimeSpan.FromMinutes(15.0)
  /// run_app lease. An app runs until stopped, so this is a long cap rather than
  /// a work estimate. No recorded reason for 4 hours.
  let leaseTtlRunApp = TimeSpan.FromHours(4.0)
  // How long a refused lease request is told to wait before asking again, by
  // memory pressure, before queue-position scaling and jitter.
  /// Retry-after under normal memory pressure.
  let leaseRetryAfterNormal = TimeSpan.FromSeconds(3.0)
  /// Retry-after under tight memory pressure.
  let leaseRetryAfterTight = TimeSpan.FromSeconds(10.0)
  /// Retry-after under critical memory pressure.
  let leaseRetryAfterCritical = TimeSpan.FromSeconds(30.0)
  /// Floor on the jittered retry-after, so jitter near zero never reads as
  /// "retry immediately".
  let leaseMinRetryAfter = TimeSpan.FromMilliseconds(200.0)

  // -- Friction detectors (ObservedFrictionTypes.DetectorConfig) --
  /// Window in which repeated session resets count as thrash. No recorded
  /// reason for 60s.
  let frictionResetThrashWindow = TimeSpan.FromSeconds(60.0)
  /// A hard reset this soon after a session was created is a friction signal.
  /// No recorded reason for 30s.
  let frictionHardResetAfterCreateWindow = TimeSpan.FromSeconds(30.0)
  /// A first success later than this after session create is slow. Described in
  /// the detector config as the warmup budget; it is not derived from
  /// `warmupInactivityLimit` (30s) or `warmupAbsoluteMax` (10 minutes).
  let frictionSlowFirstSuccess = TimeSpan.FromSeconds(45.0)

  // -- Hot reload --
  /// The window in which a thousand reflective reads of one module value count
  /// as a hot loop (ValueReads.HotLoopThreshold.standard). The count is 1000
  /// there; no recorded reason for 1s.
  let hotLoopWindow = TimeSpan.FromSeconds(1.0)
  /// How long a patched function may go without its new body running before the
  /// save reports it as never entered. A page that refreshes on the pending
  /// report usually runs the function within a second; a function nothing
  /// calls (an idle app, or a caller that kept its own copy of the old body)
  /// reads the same, so the report says "unconfirmed, exercise it".
  let patchConfirmation = envOrDefault "SAGEFS_PATCH_CONFIRM_SECONDS" 10.0

  // -- Running an app (run_app) --
  /// How long an app's entry point may run before it builds a host. If it has not by then,
  /// the app is treated as a console app with no server. The wait ends the moment a host
  /// appears, so a generous value costs a web app nothing; it is the delay a console app
  /// pays before it is recognised as one. A cold ASP.NET host build on a loaded machine
  /// takes seconds, which is why this is not sub-second.
  let appHostAppearGrace = envOrDefault "SAGEFS_APP_HOST_APPEAR_SECONDS" 10.0
  /// How long a host that has been built may take to start listening.
  let appHostStart = envOrDefault "SAGEFS_APP_HOST_START_SECONDS" 90.0

  // -- Restart / Backoff --
  /// First delay before a crashed worker is restarted; later restarts double it
  /// (RestartPolicy.nextBackoff) up to `restartMaxBackoff`.
  let restartBaseBackoff = TimeSpan.FromSeconds(1.0)
  /// Cap on the restart delay, so a worker that keeps crashing is retried at
  /// least this often until the policy gives up.
  let restartMaxBackoff = TimeSpan.FromSeconds(30.0)
  /// Restarts older than this are forgotten, so spaced-out transient failures
  /// never add up to a permanent give-up.
  let restartCountResetWindow = TimeSpan.FromMinutes(5.0)
  /// A crash this soon after the previous restart is a startup crash (the host
  /// is failing to come up), which backs off 4x and gives up at a lower ceiling.
  /// No recorded reason for 10s.
  let restartStartupCrashWindow = TimeSpan.FromSeconds(10.0)
  /// What the workflow-switch confirmation tells the user a cold session start
  /// costs. A display estimate, not a bound: nothing waits on it. No recorded
  /// reason for 15s.
  let estimatedColdStart = TimeSpan.FromSeconds(15.0)

  // -- Watchdog / Supervision --
  let watchdogInterval = TimeSpan.FromSeconds(5.0)
  let watchdogGracePeriod = TimeSpan.FromSeconds(30.0)

  // -- Dashboard / UI --
  let dashboardPollInterval = TimeSpan.FromMilliseconds(100.0)
  let sseEventInterval = TimeSpan.FromSeconds(1.0)
  /// Server SSE heartbeat cadence: the stream loop patches a heartbeat signal at
  /// least this often (even on no-change ticks) so the client can prove liveness.
  let dashboardHeartbeat = envOrDefault "SAGEFS_DASHBOARD_HEARTBEAT_SECONDS" 5.0
  /// Client staleness budget: if no heartbeat arrives within this window the
  /// dashboard flips Signals.Connected=false and shows the disconnect banner.
  let dashboardStaleAfter = envOrDefault "SAGEFS_DASHBOARD_STALE_AFTER_SECONDS" 15.0

  // -- Daemon / Server --
  let workerEndpointFetch = TimeSpan.FromMilliseconds(500.0)
  /// How long the daemon waits before listening again after a worker's reload
  /// stream closed on its own (WorkerReloadRelay). A worker that's gone by then
  /// has no URL in the snapshot and the relay just stops.
  let workerReloadRelayRetry = TimeSpan.FromSeconds(1.0)
  let scheduledGraceDelay = TimeSpan.FromSeconds(5.0)
  let startupDelay = TimeSpan.FromMilliseconds(200.0)
  let workerShutdownDelay = TimeSpan.FromSeconds(2.0)
  /// Defense-in-depth bound on a `StopSession` mailbox round-trip
  /// (`DaemonMode.createSessionOps.StopSession`). `stopWorker` itself is
  /// already bounded (workerShutdownDelay + a WaitForExit + a Kill
  /// fallback), and the SessionManager mailbox loop is supervised
  /// (`superviseStep`/`supervise` in SessionManager.fs) so an unhandled
  /// exception replies with an error instead of dropping the caller. This
  /// timeout is the last line: if the reply STILL never arrives — a future
  /// regression, not a known path today — `stop_session` fails with a
  /// stated, actionable error at 20s instead of hanging until the MCP
  /// client's own external timeout (300s in the three fcs-onboarding-trial
  /// reports, 2026-09-22) makes the caller guess why. "A stop always
  /// completes" is an invariant this bound exists to guarantee even if
  /// everything upstream of it were somehow wrong.
  let stopSessionMailboxTimeout = envOrDefault "SAGEFS_STOP_SESSION_TIMEOUT_SECONDS" 20.0
  /// How long one command may hold the session manager's loop before the
  /// supervisor calls it wedged. Above the slowest honest handler (a parallel
  /// stop of every session waits seconds) and below `stopSessionMailboxTimeout`
  /// doubled, so a wedge is named before callers give up on it.
  let supervisorWedgeAfter = envOrDefault "SAGEFS_SUPERVISOR_WEDGE_SECONDS" 30.0
  /// How often the supervisor's watchdog thread looks at the loop.
  let supervisorCheckInterval = TimeSpan.FromSeconds(2.0)
  /// The idle TTL a daemon gets when it starts inside another checkout with no
  /// explicit owner and no explicit TTL, so it cannot run forever unowned (the
  /// leaked worktree-agent daemons). No recorded reason for 30 minutes.
  let nestedCheckoutDaemonTtl = TimeSpan.FromMinutes(30.0)

  // -- Persistence --
  /// Cadence of the daemon's periodic manifest save (`periodicManifestSave`,
  /// `DaemonMode.fs` — `cacheSaveTimer`): both the timer's initial due time
  /// and every reschedule use this value. Default stays 60s in production;
  /// tests that need the crash/resume durability boundary to arrive sooner
  /// (rather than waiting on a fixed 60s wall-clock tick) can shrink it via
  /// the env var. Env-overridable.
  let manifestSaveInterval = envOrDefault "SAGEFS_MANIFEST_SAVE_INTERVAL_SECONDS" 60.0

  // -- Session Lifecycle --
  let sessionDispose = TimeSpan.FromSeconds(10.0)
  /// How long a Ready session can go untouched before the dashboard calls it
  /// idle instead of running. Only Ready is time-gated this way — Evaluating
  /// and Building are never idle regardless of age (see SessionDisplay.displayStatus).
  /// Env-overridable so an integration test can prove the boundary without
  /// waiting ten real minutes for it.
  let idleSessionThreshold = envOrDefaultMinutes "SAGEFS_IDLE_SESSION_THRESHOLD_MINUTES" 10.0
  /// Maximum time to poll a worker waiting for Ready status after spawn/restart.
  /// If exceeded, the session is faulted to prevent infinite WarmingUp states.
  let warmupReadyPollMax = envOrDefault "SAGEFS_WARMUP_READY_POLL_SECONDS" 120.0

  // -- Test harness / integration --
  /// Deadline for a spawned test daemon to reach a readable Ready state.
  /// Replaces the bare 120_000ms literals in the daemon integration tests.
  let integrationDaemonReady = envOrDefault "SAGEFS_TEST_DAEMON_READY_SECONDS" 120.0
  /// Deadline for a killed worker to be restarted on a new pid in the
  /// crash/restart integration smoke. Replaces the bare 60_000L literal.
  let integrationWorkerRestart = envOrDefault "SAGEFS_TEST_WORKER_RESTART_SECONDS" 60.0
  /// Warmup deadline for a browser-journey dashboard session (cold Chromium +
  /// Release sample). Replaces the 300.0s literals in DashboardBrowserRunner.
  let browserJourneyWarmup = envOrDefault "SAGEFS_TEST_BROWSER_WARMUP_SECONDS" 300.0
  /// Build deadline for the web-app hot-reload verification sample.
  /// Replaces the 180000ms literal.
  let webAppHotReloadBuild = envOrDefault "SAGEFS_TEST_WEBAPP_BUILD_SECONDS" 180.0
  /// Deadline for the hot-reload web-app sample to bind its port and report
  /// ready. Replaces the 120.0s literal.
  let webAppPortReady = envOrDefault "SAGEFS_TEST_WEBAPP_PORT_SECONDS" 120.0

/// How long SageFs keeps the local data it writes under its data dir, and how
/// much of it. Read once at startup; every value has an env var so a user (or
/// a test) can change it without a rebuild. `LocalDataRetention` holds the
/// decisions that use these.
[<RequireQualifiedAccess>]
module DataRetention =

  let private envOrDefaultInt (varName: string) (defaultValue: int) =
    match Environment.GetEnvironmentVariable(varName) with
    | null | "" -> defaultValue
    | value ->
      match Int32.TryParse(value) with
      | true, v when v > 0 -> v
      | _ -> defaultValue

  let private envOrDefaultDays (varName: string) (defaultDays: float) =
    match Environment.GetEnvironmentVariable(varName) with
    | null | "" -> TimeSpan.FromDays(defaultDays)
    | value ->
      match Double.TryParse(value) with
      | true, v when v > 0.0 -> TimeSpan.FromDays(v)
      | _ -> TimeSpan.FromDays(defaultDays)

  let frictionMaxAgeEnvVar = "SAGEFS_FRICTION_MAX_AGE_DAYS"
  let frictionMaxRowsEnvVar = "SAGEFS_FRICTION_MAX_ROWS"
  let frictionMaxAggregateVersionsEnvVar = "SAGEFS_FRICTION_MAX_AGGREGATE_VERSIONS"
  let cohortLedgerRetentionEnvVar = "SAGEFS_COHORT_LEDGER_RETENTION_DAYS"
  let pruneIntervalEnvVar = "SAGEFS_DATA_PRUNE_INTERVAL_MINUTES"

  /// Friction rows older than this are rolled into the per-version aggregate
  /// and deleted.
  let frictionMaxAge = envOrDefaultDays frictionMaxAgeEnvVar 30.0
  /// Raw friction rows kept per table. The newest win.
  let frictionMaxRows = envOrDefaultInt frictionMaxRowsEnvVar 5_000
  /// How many SageFs versions keep their small aggregate (a count per kind).
  let frictionMaxAggregateVersions = envOrDefaultInt frictionMaxAggregateVersionsEnvVar 20
  /// A finished cohort's ledger rows are cleared once its last entry is older
  /// than this. An active cohort is never touched.
  let cohortLedgerRetention = envOrDefaultDays cohortLedgerRetentionEnvVar 7.0
  /// How often a running daemon prunes friction again (it also prunes on start).
  let pruneInterval =
    match Environment.GetEnvironmentVariable(pruneIntervalEnvVar) with
    | null | "" -> TimeSpan.FromHours(1.0)
    | value ->
      match Double.TryParse(value) with
      | true, v when v > 0.0 -> TimeSpan.FromMinutes(v)
      | _ -> TimeSpan.FromHours(1.0)
