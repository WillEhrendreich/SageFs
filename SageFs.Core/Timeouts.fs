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
  let healthCheck = TimeSpan.FromSeconds(2.0)
  let shutdownHttpClient = TimeSpan.FromSeconds(5.0)
  let sseKeepAlive = TimeSpan.FromHours(24.0)

  // -- Live Testing (configurable at runtime via MCP, thread-safe) --
  let private _perTestLock = obj ()
  let private _globalTestLock = obj ()
  let mutable private _perTestDefault =
    envOrDefault "SAGEFS_PER_TEST_TIMEOUT_SECONDS" 5.0
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
  let buildCompletion = envOrDefaultMinutes "SAGEFS_BUILD_TIMEOUT_MINUTES" 10.0
  let processNormalExit = TimeSpan.FromSeconds(3.0)

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
  let processKillVerify = TimeSpan.FromSeconds(2.0)
  let stdioFlush = TimeSpan.FromSeconds(5.0)

  // -- Restart / Backoff --
  let restartBaseBackoff = TimeSpan.FromSeconds(1.0)
  let restartMaxBackoff = TimeSpan.FromSeconds(30.0)
  let restartCountResetWindow = TimeSpan.FromMinutes(5.0)

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
