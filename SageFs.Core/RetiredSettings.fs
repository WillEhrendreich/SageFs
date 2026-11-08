namespace SageFs

/// What decides a wait or a limit now that it has no override of its own.
[<RequireQualifiedAccess>]
type RetiredBecause =
  /// A wait for the machine: its value on a `Fast` machine, scaled by the machine tier (`waits.machineTier`).
  | DerivedFromMachineTier
  /// A pace, a bound or a keep-for period that nobody tunes: one number, fixed in `Timeouts`.
  | FixedNumber
  /// Replaced by other settings, and nothing reads it.
  | Superseded
  /// A deadline only the test suites pick: it stays in `Timeouts` for the harness.
  | TestHarnessDeadline

/// A setting that used to have a row and an environment variable, and has neither now. A value saved under its key is
/// ignored with a note, and its variable is no longer read, so a saved file or an environment from before still loads.
type RetiredSetting =
  { Key: string
    Variable: string
    Because: RetiredBecause }

/// The settings retired when the overrides were cut to the short list a person plausibly sets. The evidence for the
/// cut is in `docs/plans/more-settings-editable.md` ("The settings snapshot and the short list"): how often the docs,
/// the troubleshooting page, the test harnesses and the benchmark scripts name each variable. Nothing here is deleted
/// from a settings file or an environment.
[<RequireQualifiedAccess>]
module RetiredSettings =

  let private retire (because: RetiredBecause) (pairs: (string * string) list) : RetiredSetting list =
    pairs |> List.map (fun (key, variable) -> { Key = key; Variable = variable; Because = because })

  let all : RetiredSetting list =
    retire RetiredBecause.DerivedFromMachineTier
      [ "waits.ambientSdkProbeSeconds", "SAGEFS_AMBIENT_SDK_PROBE_SECONDS"
        "waits.targetFrameworkEvaluationSeconds", "SAGEFS_TARGET_FRAMEWORK_EVALUATION_SECONDS"
        "waits.gcDumpCaptureSeconds", "SAGEFS_GC_DUMP_CAPTURE_SECONDS"
        "waits.daemonSessionsProbeSeconds", "SAGEFS_DAEMON_SESSIONS_PROBE_SECONDS"
        "waits.gitQuickSeconds", "SAGEFS_GIT_QUICK_SECONDS"
        "waits.gitRebaseMinutes", "SAGEFS_GIT_REBASE_MINUTES"
        "waits.gitWorktreeAddMinutes", "SAGEFS_GIT_WORKTREE_ADD_MINUTES"
        "waits.cohortSettleSeconds", "SAGEFS_COHORT_SETTLE_SECONDS"
        "waits.hotReloadCompileBudgetMinutes", "SAGEFS_HOT_RELOAD_COMPILE_BUDGET_MINUTES"
        "waits.appHostAppearSeconds", "SAGEFS_APP_HOST_APPEAR_SECONDS"
        "waits.appHostStartSeconds", "SAGEFS_APP_HOST_START_SECONDS"
        "waits.workerProbeTimeoutSeconds", "SAGEFS_WORKER_PROBE_TIMEOUT_SECONDS"
        "waits.jupyterWarmupSeconds", "SAGEFS_JUPYTER_WARMUP_SECONDS"
        "waits.workerProxyWaitSeconds", "SAGEFS_WORKER_PROXY_WAIT_SECONDS" ]
    @ retire RetiredBecause.FixedNumber
      [ "waits.frictionPostSeconds", "SAGEFS_FRICTION_POST_SECONDS"
        "waits.updateCheckTimeoutSeconds", "SAGEFS_UPDATE_CHECK_TIMEOUT_SECONDS"
        "waits.updateCheckIntervalMinutes", "SAGEFS_UPDATE_CHECK_INTERVAL_MINUTES"
        "waits.referencesCacheRetentionSeconds", "SAGEFS_REFERENCES_CACHE_RETENTION_SECONDS"
        "waits.referencesCacheSweepSeconds", "SAGEFS_REFERENCES_CACHE_SWEEP_SECONDS"
        "waits.workerProbeIntervalSeconds", "SAGEFS_WORKER_PROBE_INTERVAL_SECONDS"
        "waits.workerReadyPollSeconds", "SAGEFS_WORKER_READY_POLL_SECONDS"
        "waits.ownerPollSeconds", "SAGEFS_OWNER_POLL_SECONDS"
        "waits.agentCleanupSeconds", "SAGEFS_AGENT_CLEANUP_SECONDS"
        "waits.cohortReaperSeconds", "SAGEFS_COHORT_REAPER_SECONDS"
        "waits.orphanSweepMinutes", "SAGEFS_ORPHAN_SWEEP_MINUTES"
        "waits.watcherSyncSeconds", "SAGEFS_WATCHER_SYNC_SECONDS"
        "waits.idleSessionMinutes", "SAGEFS_IDLE_SESSION_THRESHOLD_MINUTES"
        "limits.sessionManagerQueueCapacity", "SAGEFS_SESSION_MANAGER_QUEUE_CAPACITY" ]
    @ retire RetiredBecause.Superseded
      [ "waits.workerStartupTimeoutMs", "SAGEFS_WORKER_STARTUP_TIMEOUT_MS" ]
    @ retire RetiredBecause.TestHarnessDeadline
      [ "waits.testDaemonReadySeconds", "SAGEFS_TEST_DAEMON_READY_SECONDS"
        "waits.testWorkerRestartSeconds", "SAGEFS_TEST_WORKER_RESTART_SECONDS"
        "waits.testBrowserWarmupSeconds", "SAGEFS_TEST_BROWSER_WARMUP_SECONDS"
        "waits.testWebAppBuildSeconds", "SAGEFS_TEST_WEBAPP_BUILD_SECONDS"
        "waits.testWebAppPortSeconds", "SAGEFS_TEST_WEBAPP_PORT_SECONDS" ]

  let describeBecause (because: RetiredBecause) : string =
    match because with
    | RetiredBecause.DerivedFromMachineTier ->
      "this wait is derived from the machine tier: SageFs takes its value for a fast machine and scales it for yours, so a slower machine gets more time by setting waits.machineTier."
    | RetiredBecause.FixedNumber ->
      "this is one fixed number that nobody had a reason to tune."
    | RetiredBecause.Superseded ->
      "the warm-up inactivity limit and ceiling replaced this flat wait."
    | RetiredBecause.TestHarnessDeadline ->
      "only the test suites pick this deadline, and they take it from Timeouts."

  /// The note for one retired setting, for the daemon's log and the Settings page: what it was, and what decides the
  /// wait now.
  let describe (setting: RetiredSetting) : string =
    sprintf "%s (%s) is retired and ignored: %s" setting.Key setting.Variable (describeBecause setting.Because)

  /// The keys of the families that only env-backed rows own (`waits.`, `data.`, `limits.`). A key in one of them that
  /// no row and no retired setting names is unknown.
  let ownsKey (key: string) : bool =
    key.StartsWith "waits." || key.StartsWith "data." || key.StartsWith "limits."

  /// The retired variables a person still has set, so the daemon can say each one no longer does anything.
  let inEnvironment (readEnv: string -> EnvRead) : RetiredSetting list =
    all
    |> List.filter (fun setting ->
      match readEnv setting.Variable with
      | EnvSet _ -> true
      | EnvUnset -> false)
