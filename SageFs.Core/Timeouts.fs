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
  /// How long a started FSI host process has to finish its startup handshake
  /// (the one-shot config host in ConfigHost and the isolated session host in
  /// IsolatedFsiSession) before the start is given up as failed. No recorded
  /// reason for 120s.
  let fsiHostStartup = envOrDefault "SAGEFS_FSI_HOST_STARTUP_SECONDS" 120.0
  /// The default of the superseded `SAGEFS_WORKER_STARTUP_TIMEOUT_MS`
  /// (`SageFsConfig.WorkerStartupTimeoutMs`). Nothing waits on it any more:
  /// `warmupInactivityLimit` and `warmupAbsoluteMax` replaced a flat bound. It
  /// stays only so a value someone has already set keeps parsing.
  let legacyWorkerStartup = TimeSpan.FromSeconds(120.0)
  /// How long a build that failed on a locked DLL waits, after forcing a GC to
  /// drop in-process file handles, before it builds once more. No recorded
  /// reason for 500ms.
  let dllLockRetryDelay = TimeSpan.FromMilliseconds(500.0)
  /// How long `dotnet --version` and `dotnet --list-sdks` may each take while
  /// FsiHostBuild picks the SDK to build the FSI host with. They answer from
  /// disk, so a wait this long means a hung muxer. No recorded reason for 30s.
  let dotnetSdkQuery = envOrDefault "SAGEFS_DOTNET_SDK_QUERY_SECONDS" 30.0
  /// How long ProjectLoading's `dotnet --version` pre-check may take before it
  /// is killed. A hung probe must never hang a warmup, so this is shorter than
  /// `dotnetSdkQuery`; a failure just means the normal in-process load runs as
  /// before. No recorded reason for 15s.
  let ambientSdkProbe = envOrDefault "SAGEFS_AMBIENT_SDK_PROBE_SECONDS" 15.0
  /// How long one `dotnet-gcdump collect` may run. On a process this large it
  /// can take tens of seconds; this is generous without being an unbounded
  /// hang on a machine that is already struggling. No recorded reason for 120s.
  let gcDumpCapture = envOrDefault "SAGEFS_GC_DUMP_CAPTURE_SECONDS" 120.0
  /// How long the environment check lets a `dotnet fsi --exec` probe run before
  /// it kills it and reports fsi as hung. No recorded reason for 3s.
  let fsiAvailabilityProbe = TimeSpan.FromSeconds(3.0)
  /// How long `sagefs stop` waits for the daemon's process to exit after the
  /// shutdown request, which gives a normal graceful shutdown (manifest save,
  /// stopping workers) room before the fallback kill.
  let stopGracefulExit = envOrDefault "SAGEFS_STOP_GRACEFUL_SECONDS" 30.0
  /// How long `sagefs stop` waits for the process to be gone after it killed it.
  /// `processKillVerify` serves the same purpose in SessionManager at 2s; this
  /// site had 3s and keeps it.
  let stopKillExit = TimeSpan.FromSeconds(3.0)

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

  /// Hard timeout on a short GET to the local daemon's `/api/sessions` from the
  /// CLI (`sagefs status` and the environment check). The daemon is on this
  /// machine, so a probe that takes this long means it is wedged, and the caller
  /// reports that instead of waiting. Shorter than `workerHttpRead`.
  let daemonSessionsProbe = envOrDefault "SAGEFS_DAEMON_SESSIONS_PROBE_SECONDS" 3.0
  /// Request timeout for posting a friction report to the configured endpoint.
  /// The post is user-initiated and the dashboard shows the failure, so this is
  /// a network budget with no recorded reason for 15s.
  let frictionReportPost = envOrDefault "SAGEFS_FRICTION_POST_SECONDS" 15.0

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

  // -- Live testing as you type --
  /// How long typing must pause before the tree-sitter pass re-reads the
  /// buffer for the as-you-type test feedback. Short, because tree-sitter is
  /// cheap. No recorded reason for 50ms.
  let liveTestTreeSitterDebounce = TimeSpan.FromMilliseconds(50.0)
  /// The pause before the FCS typecheck pass starts after typing stops, before
  /// any backoff. No recorded reason for 300ms.
  let liveTestFcsDebounce = TimeSpan.FromMilliseconds(300.0)
  /// The ceiling the FCS pause backs off to when typechecks keep being
  /// cancelled by further typing. No recorded reason for 2s.
  let liveTestFcsDebounceMax = TimeSpan.FromMilliseconds(2000.0)
  /// How often the daemon's test cycle timer fires while a debounce is
  /// pending. It only has to be well under `liveTestTreeSitterDebounce`, so the
  /// feedback lands near the intended debounce and is not rounded up to a
  /// coarser tick.
  let liveTestTickActive = TimeSpan.FromMilliseconds(25.0)
  /// How often the test cycle timer fires when no debounce is pending. A model
  /// change that queues one wakes the timer at once, so a save is never held
  /// back by this. No recorded reason for 1s.
  let liveTestTickIdle = TimeSpan.FromSeconds(1.0)
  /// How long the editing must stay quiet after an evaluated run before a real build is spent confirming
  /// its verdicts. A build restarts the session's worker, so it must not start on a pause between two
  /// words that the next keystroke would throw away. Longer than the FCS pause, shorter than the time a
  /// person spends reading a result before changing the code again. No recorded reason for 2s beyond
  /// that: the measurement in the `--integration-lt` tier prints the real save-to-confirmed time.
  let liveTestConfirmationQuiet = TimeSpan.FromSeconds(2.0)
  /// How long a confirmation (the build, then the run against it) may take before its rows say the build
  /// did not answer. A cold build of a large project can take minutes; past this the verdicts stay
  /// evaluated and the rows say why they are not confirmed. No recorded reason for 3 minutes.
  let liveTestConfirmationDeadline = TimeSpan.FromMinutes(3.0)
  /// How long the live-values walk waits on one binding before it gives up on it. Walking a
  /// value runs the user's property getters on the session's one eval thread, so a getter that
  /// never returns would stall every later eval. Past this the binding shows as unreadable and is
  /// not walked again. A second is long for a getter that merely reads a field and short enough
  /// that one blocked getter costs the next eval a second, once. No recorded reason for 1s.
  let liveValueBindingBudget = TimeSpan.FromSeconds(1.0)
  /// The debounce of the daemon's live-test file watcher: a burst of events
  /// from one save settles into one change. No recorded reason for 75ms.
  let liveTestWatcherDebounce = TimeSpan.FromMilliseconds(75.0)
  /// How long disposing the live-test file watcher waits for its mailbox to
  /// acknowledge Shutdown before it logs that it timed out. No recorded reason
  /// for 5s.
  let liveTestWatcherShutdown = TimeSpan.FromSeconds(5.0)
  /// The least time between two test-summary pushes to SSE subscribers while a
  /// run is in progress. A finished run always pushes at once. No recorded
  /// reason for 250ms.
  let testSseThrottle = TimeSpan.FromMilliseconds(250.0)
  /// A cell whose P95 eval time is over this is no longer acceptable on
  /// latency alone (ImpactForecast): the recommendation is Investigate. No
  /// recorded reason for 500ms.
  let impactP95Acceptable = TimeSpan.FromMilliseconds(500.0)
  /// A cell whose P95 eval time is over this is recommended for Refactor
  /// (ImpactForecast). No recorded reason for 2s.
  let impactP95Investigate = TimeSpan.FromMilliseconds(2000.0)
  /// How long after a rebuild restart the live-testing pipeline waits for the
  /// session manager to say the session is Ready (which is also when its
  /// streaming test proxy exists) before it reports the rebuild as failed. It
  /// is the one timer in that wait. No recorded reason for 30s.
  let rebuildReadyWait = envOrDefault "SAGEFS_REBUILD_READY_SECONDS" 30.0

  // -- Process Management --
  /// How long a `dotnet build` run by a session start or rebuild may take before
  /// SessionBuild kills it and the session faults with `BuildFailure.TimedOut`.
  /// Large repos need minutes; 10 matches the ValidTimeout max.
  let buildCompletion = envOrDefaultMinutes "SAGEFS_BUILD_TIMEOUT_MINUTES" 10.0
  /// How long a process that was asked to stop (a worker after its Shutdown
  /// message, the daemon during its own stop) gets to exit on its own before
  /// the caller kills it. No recorded reason for 3s.
  let processNormalExit = TimeSpan.FromSeconds(3.0)
  /// How long a process that was just killed gets to be gone before the caller
  /// logs that it is not and carries on. No recorded reason for 2s.
  let processKillVerify = TimeSpan.FromSeconds(2.0)
  /// How long the failure path waits for a worker's stderr reader to reach EOF
  /// once stdout has closed. stderr closes with the process, so this normally
  /// costs nothing; the bound only matters when a grandchild holds the pipe
  /// open. No recorded reason for 2s.
  let stderrDrainGrace = TimeSpan.FromSeconds(2.0)
  /// How long the client waits, after the FSI host's connection closes, for the
  /// process to report its exit code. The code arrives right behind the close, so
  /// this normally costs nothing; if it never comes the end is reported as a
  /// closed connection instead. Was a bare 2000 ms, kept at that.
  let fsiHostExitReport = TimeSpan.FromSeconds(2.0)
  /// How long disposing an FSI host session waits for the host to exit after the
  /// shutdown request before it kills the process. Was a bare 5000 ms, kept at that.
  let fsiHostShutdownGrace = TimeSpan.FromSeconds(5.0)
  /// How long the `dotnet build` of the FSI host (a cold build, once per host
  /// version and SDK) may run before FsiHostBuild gives up. No recorded reason
  /// for 5 minutes.
  let hostBuildRun = envOrDefaultMinutes "SAGEFS_HOST_BUILD_MINUTES" 5.0
  /// How long a session start waits for another process's build of the same FSI
  /// host to finish (the cross-process build lock in FsiHostBuild). The lock is
  /// held for the whole build, so a waiter waits as long as the holder's build
  /// may run.
  let hostBuildLockWait = hostBuildRun
  /// How often the host build lock is retried while another process holds it.
  /// This is a poll; the lock is a file handle with no way to wait on it.
  let hostBuildLockPoll = TimeSpan.FromMilliseconds(200.0)
  /// Request timeout for a workflow switch (HotReload and back) sent to the
  /// daemon. A switch can rebuild the target project before the new worker is
  /// ready, so it matches the build kill timer rather than an eval-style timeout.
  let workflowSwitchRequest = buildCompletion
  // Budgets for the git subprocesses the cohort landing gate runs (CohortGit).
  // Each kills the process tree on timeout and returns an error. They are
  // separate from `buildCompletion`: a different operation with a different cost.
  /// Plumbing commands (rev-parse, diff, update-ref, worktree remove).
  let gitQuick = envOrDefault "SAGEFS_GIT_QUICK_SECONDS" 30.0
  /// A rebase, which may run hooks and touch many commits.
  let gitRebase = envOrDefaultMinutes "SAGEFS_GIT_REBASE_MINUTES" 5.0
  /// `git worktree add`, which checks out a whole tree.
  let gitWorktreeAdd = envOrDefaultMinutes "SAGEFS_GIT_WORKTREE_ADD_MINUTES" 2.0

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
  /// Added to `globalTestRun` to bound how long the landing gate awaits a test
  /// run's verdict: the worker cancels the run at `globalTestRun`, and this is
  /// room for daemon-side dispatch latency and result aggregation. No recorded
  /// reason for 30s.
  let testRunAwaitSlack = TimeSpan.FromSeconds(30.0)
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
  // The values `DevReload.DevReloadConfig.defaults` is built from. The record
  // keeps its `...Ms` fields because the browser script and the file watcher
  // read them in milliseconds.
  /// How long a burst of file system events settles before the watcher reports
  /// one change (the default watch config and `DevReloadConfig`). An editor
  /// writes a save as several events. No recorded reason for 200ms.
  let fileWatchDebounce = TimeSpan.FromMilliseconds(200.0)
  /// A change to a file within this window of that file's last compile is
  /// dropped as the watcher's duplicate event for one save, not a new edit.
  /// No recorded reason for 500ms.
  let doubleCompileGuard = TimeSpan.FromMilliseconds(500.0)
  /// How long the browser's reload script waits for the hot reload event stream
  /// to connect before it warns that it did not. No recorded reason for 3s.
  let reloadConnectTimeout = TimeSpan.FromMilliseconds(3000.0)
  /// How long after a reload the browser's reload script keeps counting
  /// reloads toward its reload-loop guard before it resets the count. No
  /// recorded reason for 5s.
  let reloadCountResetWindow = TimeSpan.FromMilliseconds(5000.0)
  /// How often the browser's compile timer redraws while a compile runs. No
  /// recorded reason for 200ms.
  let compileTimerRedraw = TimeSpan.FromMilliseconds(200.0)
  /// A compile that takes longer than this shows "click to reload" instead of
  /// reloading the page by itself, so a long compile cannot throw away work in
  /// progress. No recorded reason for 3s.
  let autoReloadThreshold = TimeSpan.FromMilliseconds(3000.0)
  /// The browser's compile timer turns amber once a compile has run this long.
  /// No recorded reason for 5s.
  let longCompileWarning = TimeSpan.FromMilliseconds(5000.0)
  /// How long one save waits for the previous save's compile before it gives up
  /// and reports that it did. The wait used to be unbounded, which let one
  /// wedged eval silently disable hot reload for every other file; bounded, a
  /// stuck compiler shows up. No recorded reason for 60s.
  let compileQueueWait = envOrDefault "SAGEFS_HOT_RELOAD_COMPILE_QUEUE_SECONDS" 60.0
  /// Ceiling on one save's re-evaluation. The eval is posted with a token
  /// nothing else cancels, so without a deadline one wedged submission owns the
  /// compiler forever; with one the compiler is always handed back and the
  /// next save goes through. No recorded reason for 5 minutes.
  let compileBudget = envOrDefaultMinutes "SAGEFS_HOT_RELOAD_COMPILE_BUDGET_MINUTES" 5.0
  /// How long the standalone FCS check that reload planning runs on a source
  /// text may take, for each of its two steps (the script project options and
  /// the type check). No recorded reason for 10s.
  let reloadPlanningCheck = TimeSpan.FromSeconds(10.0)

  // -- Running an app (run_app) --
  /// How long an app's entry point may run before it builds a host. If it has not by then,
  /// the app is treated as a console app with no server. The wait ends the moment a host
  /// appears, so a generous value costs a web app nothing; it is the delay a console app
  /// pays before it is recognised as one. A cold ASP.NET host build on a loaded machine
  /// takes seconds, which is why this is not sub-second.
  let appHostAppearGrace = envOrDefault "SAGEFS_APP_HOST_APPEAR_SECONDS" 10.0
  /// How long a host that has been built may take to start listening.
  let appHostStart = envOrDefault "SAGEFS_APP_HOST_START_SECONDS" 90.0
  /// How long stopping an app's host may take before the stop gives up and logs
  /// that it failed. No recorded reason for 10s.
  let appHostStop = TimeSpan.FromSeconds(10.0)
  /// How long, after an app's host has stopped, its entry point (`main`) gets to
  /// return before the run is reported as ended with exit code 0. `main` can
  /// return early after a fire-and-forget RunAsync, so a host stop is the
  /// authority and the entry point only gets this grace. No recorded reason for
  /// 5s.
  let appEntryFinishGrace = TimeSpan.FromSeconds(5.0)
  /// How long one `AwaitAppChange` request parks the worker for. At the end of
  /// it the request returns the app's state at that moment, so a caller that
  /// wants to keep watching asks again. No recorded reason for 5 minutes.
  let appChangeAwait = TimeSpan.FromMinutes(5.0)
  /// How long disposing the app runner waits for its agent to acknowledge
  /// Shutdown, which stops a running host first. If it passes, the dispose logs
  /// that it timed out and goes on. No recorded reason for 15s.
  let appRunnerShutdown = TimeSpan.FromSeconds(15.0)

  // -- Restart / Backoff --
  /// First delay before a crashed worker is restarted; later restarts double it
  /// (RestartPolicy.nextBackoff) up to `restartMaxBackoff`.
  let restartBaseBackoff = TimeSpan.FromSeconds(1.0)
  /// The base of the generic retry backoff (`RetryPolicy.defaults`): attempt n
  /// waits about n+1 times this, with jitter. No recorded reason for 50ms.
  let retryBaseDelay = TimeSpan.FromMilliseconds(50.0)
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

  // -- Watchdog / Supervision --
  let watchdogInterval = TimeSpan.FromSeconds(5.0)
  let watchdogGracePeriod = TimeSpan.FromSeconds(30.0)
  /// How often an established (Ready) worker is probed for health.
  let workerHealthProbeInterval = envOrDefault "SAGEFS_WORKER_PROBE_INTERVAL_SECONDS" 5.0
  /// How long one health probe round-trip gets to answer before it counts as
  /// missed. Three misses in a row restart the worker (`WorkerHealthProbe`).
  let workerHealthProbeTimeout = envOrDefault "SAGEFS_WORKER_PROBE_TIMEOUT_SECONDS" 3.0
  /// How often the watchdog asks a starting worker for its status until it is
  /// Ready. A poll: the worker reports Ready only by answering this question.
  let workerReadyPoll = envOrDefault "SAGEFS_WORKER_READY_POLL_SECONDS" 1.0
  /// How often a worker checks that the process that owns it is still alive,
  /// so a hard-killed owner cannot orphan it. A poll; a process exit has no
  /// portable event to wait on from a non-parent.
  let ownerLivenessPoll = envOrDefault "SAGEFS_OWNER_POLL_SECONDS" 2.0
  /// How often the daemon evicts agents that have gone silent from the activity
  /// tracker. A timer, one run after the last finishes.
  let agentActivityCleanupInterval = envOrDefault "SAGEFS_AGENT_CLEANUP_SECONDS" 60.0
  /// How often the cohort reaper renews the lease of each active member and
  /// ticks the cohort so silent members depart. No recorded reason for 60s.
  let cohortReaperInterval = envOrDefault "SAGEFS_COHORT_REAPER_SECONDS" 60.0
  /// How often the daemon sweeps the OS temp directory for orphaned
  /// `sagefs-host-adopt-*` and `sagefs-test` directories left by processes that
  /// died. Generous, because the walk touches the temp directory and nearly
  /// every sweep finds nothing.
  let orphanTempDirSweepInterval = envOrDefaultMinutes "SAGEFS_ORPHAN_SWEEP_MINUTES" 15.0
  /// How often the daemon makes sure every session has a file watcher and
  /// sweeps stale ones, in case a state change event was missed. A poll that
  /// the state change events already cover. No recorded reason for 5s.
  let sessionWatcherSyncInterval = envOrDefault "SAGEFS_WATCHER_SYNC_SECONDS" 5.0
  /// The shortest the daemon's idle-TTL check interval is made.
  let ttlCheckFloor = TimeSpan.FromSeconds(1.0)
  /// The longest the daemon's idle-TTL check interval is made.
  let ttlCheckCeiling = TimeSpan.FromSeconds(30.0)
  /// The idle-TTL check runs this many times per TTL (the interval is the TTL
  /// divided by this, clamped to `ttlCheckFloor`..`ttlCheckCeiling`), so a
  /// daemon exits close to its deadline however long the TTL is.
  let ttlChecksPerTtl = 4.0

  // -- Dashboard / UI --
  let dashboardPollInterval = TimeSpan.FromMilliseconds(100.0)
  let sseEventInterval = TimeSpan.FromSeconds(1.0)
  /// Server SSE heartbeat cadence: the stream loop patches a heartbeat signal at
  /// least this often (even on no-change ticks) so the client can prove liveness.
  let dashboardHeartbeat = envOrDefault "SAGEFS_DASHBOARD_HEARTBEAT_SECONDS" 5.0
  /// Client staleness budget: if no heartbeat arrives within this window the
  /// dashboard flips Signals.Connected=false and shows the disconnect banner.
  let dashboardStaleAfter = envOrDefault "SAGEFS_DASHBOARD_STALE_AFTER_SECONDS" 15.0
  /// How long the dashboard reuses the last three worker fetches (eval stats,
  /// hot reload state, warmup context) for a session before it fetches them
  /// again. They are the dominant per-push cost. The render guard still morphs
  /// any tick whose output differs, so a reused fetch cannot send stale HTML.
  /// No recorded reason for 2s.
  let dashboardWorkerDataTtl = TimeSpan.FromSeconds(2.0)
  /// How long the legacy JSON state stream (the deprecated TUI client) may be
  /// idle before it writes an SSE keepalive comment. No recorded reason for 15s.
  let legacyStateStreamKeepAlive = TimeSpan.FromSeconds(15.0)
  /// How long the legacy JSON state stream waits after the first state change
  /// so a burst of changes becomes one push. No recorded reason for 100ms.
  let legacyStateStreamCoalesce = TimeSpan.FromMilliseconds(100.0)

  // -- Daemon / Server --
  let workerEndpointFetch = TimeSpan.FromMilliseconds(500.0)
  /// How long the daemon waits for a worker's `/warmup-context` answer when the
  /// Elm loop asks for a session's warmup context. A worker that is up answers
  /// from memory, so a wait this long means it is wedged. No recorded reason
  /// for 5s.
  let workerWarmupContextFetch = TimeSpan.FromSeconds(5.0)
  /// How long a dashboard action waits for the session's output buffer to commit
  /// the output it just dispatched, so the action's reply does not race the SSE
  /// stream's previous snapshot. If it passes, the action returns anyway (both
  /// callers ignore the "committed" result). No recorded reason for 2s.
  let outputCommitWait = TimeSpan.FromSeconds(2.0)
  /// Daemon shutdown bounds. After Ctrl+C the watchdog forces the process out if
  /// graceful shutdown has not finished.
  let gracefulShutdownWatchdog = TimeSpan.FromSeconds(5.0)
  /// How long shutdown waits for the owner to write the final manifest to disk
  /// before it logs that the shutdown may not be recorded and carries on.
  let shutdownManifestCommit = TimeSpan.FromSeconds(10.0)
  /// How long shutdown waits for an in-flight test cycle timer callback to
  /// return before it logs that the callback may still be running.
  let testCycleTimerStop = TimeSpan.FromSeconds(3.0)
  /// How long shutdown waits for an in-flight periodic manifest save to return,
  /// so a late save cannot overwrite the shutdown manifest's stopped stamps.
  let cacheSaveTimerStop = TimeSpan.FromSeconds(5.0)
  /// How often the daemon asks the session manager to refresh every session's
  /// status, so SSE subscribers see warmup progress (Starting to Ready). A poll
  /// that should be an event: the session manager already knows when a status
  /// changes. 10s because a steady-state refresh is cheap but a 2s cadence
  /// dominated the dashboard's render budget.
  let sessionStatusPoll = TimeSpan.FromSeconds(10.0)
  /// How long the worker's dev reload SSE stream (the one browser tabs connect
  /// to for hot reload notifications) waits for an event before it writes a
  /// heartbeat comment and waits again. No recorded reason for 15s.
  let reloadStreamHeartbeat = TimeSpan.FromSeconds(15.0)
  /// How long disposing a worker's HTTP server waits for it to stop before it
  /// gives up. No recorded reason for 5s.
  let workerHttpServerStop = TimeSpan.FromSeconds(5.0)
  /// How long the daemon waits before listening again after a worker's reload
  /// stream closed on its own (WorkerReloadRelay). A worker that's gone by then
  /// has no URL in the snapshot and the relay just stops.
  let workerReloadRelayRetry = TimeSpan.FromSeconds(1.0)
  let scheduledGraceDelay = TimeSpan.FromSeconds(5.0)
  /// Pause after the MCP and dashboard host tasks are started, to let them bind
  /// their ports before the daemon checks that neither has already failed.
  let startupDelay = TimeSpan.FromMilliseconds(200.0)
  /// How often the file log sink flushes to disk, so a crash loses seconds of
  /// log, not minutes. No recorded reason for 2s.
  let logFlushInterval = TimeSpan.FromSeconds(2.0)
  /// How often the `mcp-stdio` bridge asks whether the daemon it just started is
  /// up yet. A poll: there is nothing to wait on until the daemon listens.
  let stdioBridgeProbeInterval = TimeSpan.FromMilliseconds(500.0)
  /// How long a Jupyter kernel socket receive (shell, and the heartbeat echo)
  /// blocks before it rechecks for cancellation. A poll; it bounds how long
  /// kernel shutdown can wait on a quiet socket.
  let jupyterReceivePoll = TimeSpan.FromMilliseconds(100.0)
  /// How often a running eval's heartbeat event (carrying elapsed time) is
  /// broadcast. It runs on its own thread so it keeps ticking when the eval is
  /// slow. No recorded reason for 500ms.
  let evalHeartbeatInterval = TimeSpan.FromMilliseconds(500.0)
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

  // -- Waiting for a worker's test proxy (WorkerProxyWait) --
  /// The ceiling on how long a live-testing run waits for a worker to register
  /// its test proxy before it dispatches with none (every test then reports
  /// NotRun). Generous on purpose: the wait is only paid when the worker is
  /// absent, and cutting it short strands a real run: an earlier 750ms
  /// schedule failed every run under the load of the integration tier.
  let workerProxyRegister = envOrDefault "SAGEFS_WORKER_PROXY_WAIT_SECONDS" 15.0
  /// The first delay of that wait. Small, so a worker that is already up is
  /// picked up at once; later delays double up to `workerProxyRegister`.
  let workerProxyFirstDelay = TimeSpan.FromMilliseconds(50.0)

  // -- No time --
  /// The elapsed time of work that did not run: an empty summary, a
  /// placeholder result, a failure that was never timed. Not a bound; nothing
  /// waits on it.
  let notRun = TimeSpan.Zero

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
