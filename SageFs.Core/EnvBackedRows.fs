namespace SageFs

open System
open System.Globalization
open EnvBackedSettings

/// The rest of the variables `Timeouts.fs` and `SageFsConfig.fs` read. Each is read once, when its process starts, so
/// each is `restart`. A wait that is scaled by the machine tier says so on its row, because a value typed here is used
/// as written. A variable that is not for editing has a read-only row that says why.
///
/// Like `EnvBackedSettings`, this file builds closures only: nothing in `Timeouts` or `SageFsConfig` runs when it loads.
module EnvBackedRows =

  let private scaledNote (scaled: bool) : string =
    match scaled with
    | true -> " The default is for a fast machine and is scaled for slower ones; a value set here is used as written."
    | false -> ""

  /// A wait: `duration`, with the sentence about the machine tier added where it applies.
  let private wait
    (key: string)
    (name: string)
    (help: string)
    (variable: string)
    (unit: DurationUnit)
    (defaultAmount: float)
    (scaled: bool)
    (inForce: unit -> TimeSpan)
    : EnvBacked =
    duration (sprintf "waits.%s" key) name (help + scaledNote scaled) variable unit defaultAmount inForce

  let private seconds key name help variable amount scaled inForce = wait key name help variable Seconds amount scaled inForce
  let private minutes key name help variable amount scaled inForce = wait key name help variable Minutes amount scaled inForce

  let private parseNumber (raw: string) : Result<float, ConfigError> =
    match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, n when Double.IsFinite n -> Ok n
    | true, _
    | false, _ -> Error (Malformed (sprintf "expected a number, got '%s'" raw))

  let private parseAge (raw: string) : Result<SettingValue, ConfigError> =
    parseNumber raw
    |> Result.bind (fun days ->
      match days > 0.0 && days <= Timeouts.settingAgeLongestDays with
      | false -> Error (OutOfRange (sprintf "expected more than zero and at most %g days, got %s" Timeouts.settingAgeLongestDays raw))
      | true -> RetentionAge.create (TimeSpan.FromDays days) |> Result.map VAge)

  let private renderAge (v: SettingValue) : string =
    SettingValue.age v
    |> Option.map (fun held -> DurationUnit.amountOf Days (RetentionAge.value held) |> sprintf "%g")
    |> Option.defaultValue ""

  let private legalAge (days: float) : RetentionAge =
    match RetentionAge.create (TimeSpan.FromDays days) with
    | Ok held -> held
    | Error _ -> failwith "a built-in age default must be within ten years"

  /// A grace: like an age, but zero is accepted and means no grace.
  let private parseGrace (raw: string) : Result<SettingValue, ConfigError> =
    parseNumber raw
    |> Result.bind (fun days ->
      match days >= 0.0 && days <= Timeouts.settingAgeLongestDays with
      | false -> Error (OutOfRange (sprintf "expected zero (no grace) or more, and at most %g days, got %s" Timeouts.settingAgeLongestDays raw))
      | true -> RetentionAge.createAllowingZero (TimeSpan.FromDays days) |> Result.map VAge)

  /// A row that holds a span of days: `parse` says what text is legal, `make` builds the value from what the subsystem holds.
  let private ageRowWith
    (parse: string -> Result<SettingValue, ConfigError>)
    (make: TimeSpan -> Result<RetentionAge, ConfigError>)
    (key: string)
    (name: string)
    (help: string)
    (variable: string)
    (defaultDays: float)
    (inForce: unit -> TimeSpan)
    : EnvBacked =
    { Descriptor =
        { Key = sprintf "data.%s" key
          Name = name
          Description = help
          Category = LocalData
          Scope = Global
          Applicability = RestartRequired
          Default = VAge (legalAge defaultDays)
          Parse = parse
          Render = renderAge
          Apply = ignore
          External = FromEnvironment variable
          Current =
            InUseFromConsumer (fun () ->
              match make (inForce ()) with
              | Ok held -> VAge held
              | Error _ -> VAge (legalAge defaultDays)) }
      Variable = variable
      EnvText = (fun v -> match renderAge v with | "" -> None | text -> Some text) }

  /// How long local data is kept, in days. More than zero.
  let private age key name help variable defaultDays inForce =
    ageRowWith parseAge RetentionAge.create key name help variable defaultDays inForce

  /// How long something is held back after it qualifies, in days. Zero means it is not held back.
  let private grace key name help variable defaultDays inForce =
    ageRowWith parseGrace RetentionAge.createAllowingZero key name help variable defaultDays inForce

  let private parseCount (least: int) (raw: string) : Result<SettingValue, ConfigError> =
    match Int32.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, n -> WholeCount.create least n |> Result.map VCount
    | false, _ -> Error (Malformed (sprintf "expected a whole number, got '%s'" raw))

  let private renderCount (v: SettingValue) : string =
    SettingValue.count v |> Option.map (WholeCount.value >> string) |> Option.defaultValue ""

  let private legalCount (least: int) (n: int) : WholeCount =
    match WholeCount.create least n with
    | Ok c -> c
    | Error _ -> failwith "a built-in count default must be within its bounds"

  /// A cap or a count, a whole number of at least one.
  let private count
    (category: SettingCategory)
    (key: string)
    (name: string)
    (help: string)
    (variable: string)
    (defaultCount: int)
    (inForce: unit -> int)
    : EnvBacked =
    { Descriptor =
        { Key = key
          Name = name
          Description = help
          Category = category
          Scope = Global
          Applicability = RestartRequired
          Default = VCount (legalCount 1 defaultCount)
          Parse = parseCount 1
          Render = renderCount
          Apply = ignore
          External = FromEnvironment variable
          Current =
            InUseFromConsumer (fun () ->
              match WholeCount.create 1 (inForce ()) with
              | Ok held -> VCount held
              | Error _ -> VCount (legalCount 1 defaultCount)) }
      Variable = variable
      EnvText = (fun v -> match renderCount v with | "" -> None | text -> Some text) }

  let private waitRows : EnvBacked list =
    [ seconds "fsiHostStartupSeconds" "FSI host start-up limit"
        "How long a started FSI host process has to finish its start-up handshake before the start is given up as failed."
        "SAGEFS_FSI_HOST_STARTUP_SECONDS" 120.0 true (fun () -> Timeouts.fsiHostStartup)
      seconds "dotnetSdkQuerySeconds" "SDK query limit"
        "How long `dotnet --version` and `dotnet --list-sdks` may each take while SageFs picks the SDK to build the FSI host with. A wait this long means a hung dotnet."
        "SAGEFS_DOTNET_SDK_QUERY_SECONDS" 30.0 true (fun () -> Timeouts.dotnetSdkQuery)
      seconds "ambientSdkProbeSeconds" "SDK pre-check limit"
        "How long the `dotnet --version` pre-check made while a project loads may take before it is killed. A failure only means the normal in-process load runs."
        "SAGEFS_AMBIENT_SDK_PROBE_SECONDS" 15.0 true (fun () -> Timeouts.ambientSdkProbe)
      seconds "targetFrameworkEvaluationSeconds" "Target framework lookup limit"
        "How long SageFs may spend asking MSBuild for a project's target framework, when the project file does not name one, before the worker's own evaluation decides."
        "SAGEFS_TARGET_FRAMEWORK_EVALUATION_SECONDS" 60.0 true (fun () -> Timeouts.targetFrameworkEvaluation)
      seconds "gcDumpCaptureSeconds" "Memory dump limit"
        "How long one `dotnet-gcdump collect` may run."
        "SAGEFS_GC_DUMP_CAPTURE_SECONDS" 120.0 true (fun () -> Timeouts.gcDumpCapture)
      seconds "stopGracefulSeconds" "Daemon stop wait"
        "How long `sagefs stop` waits for the daemon to exit after the shutdown request, before it kills the process."
        "SAGEFS_STOP_GRACEFUL_SECONDS" 30.0 true (fun () -> Timeouts.stopGracefulExit)
      seconds "workerHttpReadSeconds" "Worker read limit"
        "How long one short read from a worker's HTTP endpoint may take before it counts as failed."
        "SAGEFS_WORKER_HTTP_READ_SECONDS" 30.0 true (fun () -> Timeouts.workerHttpRead)
      minutes "workerHttpRequestMinutes" "Worker call limit"
        "How long one eval, check, typecheck or reset call to a worker may take before it fails with a timeout instead of hanging."
        "SAGEFS_WORKER_HTTP_REQUEST_MINUTES" 10.0 true (fun () -> Timeouts.workerHttpRequest)
      seconds "confirmAnswerSeconds" "Confirmation answer wait"
        "How long a confirmation prompt waits for the person to answer before the call is refused. A slow reader is who would raise it."
        "SAGEFS_CONFIRM_ANSWER_SECONDS" 60.0 false (fun () -> Timeouts.confirmationAnswer)
      seconds "daemonSessionsProbeSeconds" "Daemon probe limit"
        "How long the CLI waits for the daemon's session list (`sagefs status` and the environment check) before it reports the daemon as wedged."
        "SAGEFS_DAEMON_SESSIONS_PROBE_SECONDS" 3.0 true (fun () -> Timeouts.daemonSessionsProbe)
      seconds "frictionPostSeconds" "Friction report post limit"
        "How long posting a friction report to the configured endpoint may take. The dashboard shows the failure."
        "SAGEFS_FRICTION_POST_SECONDS" 15.0 false (fun () -> Timeouts.frictionReportPost)
      seconds "updateCheckTimeoutSeconds" "Update check limit"
        "How long the NuGet update check may take. A failure is swallowed, so it never delays a session."
        "SAGEFS_UPDATE_CHECK_TIMEOUT_SECONDS" 3.0 false (fun () -> Timeouts.updateCheckFetch)
      minutes "updateCheckIntervalMinutes" "Update check interval"
        "How often a running daemon asks NuGet whether a newer SageFs exists. It also asks once at start when its cache is older than this."
        "SAGEFS_UPDATE_CHECK_INTERVAL_MINUTES" 360.0 false (fun () -> Timeouts.updateCheckInterval)
      seconds "rebuildReadySeconds" "Rebuild ready wait"
        "How long live testing waits, after a rebuild restart, for the session to be Ready before it reports the rebuild as failed."
        "SAGEFS_REBUILD_READY_SECONDS" 30.0 true (fun () -> Timeouts.rebuildReadyWait)
      seconds "gitQuickSeconds" "Quick git command limit"
        "How long a quick git command (rev-parse, diff, update-ref, worktree remove) may take."
        "SAGEFS_GIT_QUICK_SECONDS" 30.0 true (fun () -> Timeouts.gitQuick)
      minutes "gitRebaseMinutes" "Git rebase limit"
        "How long a git rebase may take, hooks included."
        "SAGEFS_GIT_REBASE_MINUTES" 5.0 true (fun () -> Timeouts.gitRebase)
      minutes "gitWorktreeAddMinutes" "Git worktree add limit"
        "How long `git worktree add`, which checks out a whole tree, may take."
        "SAGEFS_GIT_WORKTREE_ADD_MINUTES" 2.0 true (fun () -> Timeouts.gitWorktreeAdd)
      seconds "cohortSettleSeconds" "Cohort trunk settle wait"
        "How long the trunk follower waits for a session in the trunk checkout to settle after the checkout moves to a landed commit. The landing gate's own wait on the integration session has no deadline: it ends when the session is ready, dead, or stuck in one phase."
        "SAGEFS_COHORT_SETTLE_SECONDS" 120.0 true (fun () -> Timeouts.cohortIntegrationSettle)
      seconds "patchConfirmSeconds" "Patch confirmation wait"
        "How long a patched function may go without its new body running before the save reports it as never entered."
        "SAGEFS_PATCH_CONFIRM_SECONDS" 10.0 false (fun () -> Timeouts.patchConfirmation)
      seconds "hotReloadCompileQueueSeconds" "Hot reload compile queue wait"
        "How long one save waits for the previous save's compile before it gives up and says so."
        "SAGEFS_HOT_RELOAD_COMPILE_QUEUE_SECONDS" 60.0 true (fun () -> Timeouts.compileQueueWait)
      minutes "hotReloadCompileBudgetMinutes" "Hot reload compile budget"
        "The ceiling on one save's re-evaluation, so a wedged submission cannot hold the compiler forever."
        "SAGEFS_HOT_RELOAD_COMPILE_BUDGET_MINUTES" 5.0 true (fun () -> Timeouts.compileBudget)
      seconds "referencesCheckSeconds" "List references check limit"
        "How long the compiler checks behind `list_references` may take, all of a session's projects together. Raise it on a very large solution."
        "SAGEFS_REFERENCES_CHECK_SECONDS" 60.0 true (fun () -> Timeouts.referencesCheck)
      seconds "referencesCacheRetentionSeconds" "List references cache retention"
        "How long the compiler's cache behind `list_references` is kept after the last question. The cache can hold gigabytes on a large session."
        "SAGEFS_REFERENCES_CACHE_RETENTION_SECONDS" 600.0 false (fun () -> Timeouts.referencesCacheRetention)
      seconds "referencesCacheSweepSeconds" "List references cache sweep"
        "How often the worker checks whether that cache has outlived its retention."
        "SAGEFS_REFERENCES_CACHE_SWEEP_SECONDS" 30.0 false (fun () -> Timeouts.referencesCacheSweep)
      seconds "appHostAppearSeconds" "App host appear grace"
        "How long an app's entry point may run before it builds a host. If it has not by then, the app is treated as a console app with no server."
        "SAGEFS_APP_HOST_APPEAR_SECONDS" 10.0 true (fun () -> Timeouts.appHostAppearGrace)
      seconds "appHostStartSeconds" "App host start limit"
        "How long a host that has been built may take to start listening."
        "SAGEFS_APP_HOST_START_SECONDS" 90.0 true (fun () -> Timeouts.appHostStart)
      seconds "workerProbeIntervalSeconds" "Worker health probe interval"
        "How often a Ready worker is probed for health."
        "SAGEFS_WORKER_PROBE_INTERVAL_SECONDS" 5.0 false (fun () -> Timeouts.workerHealthProbeInterval)
      seconds "workerProbeTimeoutSeconds" "Worker health probe limit"
        "How long one health probe may take before it counts as missed. Three misses in a row restart the worker."
        "SAGEFS_WORKER_PROBE_TIMEOUT_SECONDS" 3.0 true (fun () -> Timeouts.workerHealthProbeTimeout)
      seconds "workerReadyPollSeconds" "Worker ready poll"
        "How often the watchdog asks a starting worker for its status until it is Ready."
        "SAGEFS_WORKER_READY_POLL_SECONDS" 1.0 false (fun () -> Timeouts.workerReadyPoll)
      seconds "ownerPollSeconds" "Owner liveness poll"
        "How often a worker checks that the process that owns it is still alive, so a killed owner cannot orphan it."
        "SAGEFS_OWNER_POLL_SECONDS" 2.0 false (fun () -> Timeouts.ownerLivenessPoll)
      seconds "agentCleanupSeconds" "Agent activity cleanup"
        "How often the daemon drops agents that have gone silent from the activity tracker."
        "SAGEFS_AGENT_CLEANUP_SECONDS" 60.0 false (fun () -> Timeouts.agentActivityCleanupInterval)
      seconds "cohortReaperSeconds" "Cohort reaper interval"
        "How often the cohort reaper renews each active member's lease and lets silent members depart."
        "SAGEFS_COHORT_REAPER_SECONDS" 60.0 false (fun () -> Timeouts.cohortReaperInterval)
      minutes "orphanSweepMinutes" "Orphan temp folder sweep"
        "How often the daemon sweeps the temp directory for `sagefs-host-adopt-*` and `sagefs-test` folders that dead processes left."
        "SAGEFS_ORPHAN_SWEEP_MINUTES" 15.0 false (fun () -> Timeouts.orphanTempDirSweepInterval)
      seconds "watcherSyncSeconds" "File watcher sync"
        "How often the daemon makes sure every session has a file watcher and drops stale ones, in case a state-change event was missed."
        "SAGEFS_WATCHER_SYNC_SECONDS" 5.0 false (fun () -> Timeouts.sessionWatcherSyncInterval)
      seconds "dashboardHeartbeatSeconds" "Dashboard heartbeat"
        "How often the dashboard stream sends a heartbeat, so the page can tell the daemon is alive."
        "SAGEFS_DASHBOARD_HEARTBEAT_SECONDS" 5.0 false (fun () -> Timeouts.dashboardHeartbeat)
      seconds "dashboardStaleAfterSeconds" "Dashboard stale after"
        "How long the page waits without a heartbeat before it asks the daemon directly whether it is running."
        "SAGEFS_DASHBOARD_STALE_AFTER_SECONDS" 15.0 false (fun () -> Timeouts.dashboardStaleAfter)
      seconds "jupyterWarmupSeconds" "Jupyter session warm-up wait"
        "How long the Jupyter kernel waits for a session it just created to warm up before it retries an eval the daemon refused."
        "SAGEFS_JUPYTER_WARMUP_SECONDS" 600.0 true (fun () -> Timeouts.jupyterSessionWarmup)
      seconds "stopSessionSeconds" "Stop session reply limit"
        "How long `stop_session` waits for the session manager's reply before it fails with an error instead of hanging."
        "SAGEFS_STOP_SESSION_TIMEOUT_SECONDS" 20.0 true (fun () -> Timeouts.stopSessionMailboxTimeout)
      seconds "supervisorWedgeSeconds" "Supervisor wedge limit"
        "How long one command may hold the session manager's loop before the supervisor calls it wedged."
        "SAGEFS_SUPERVISOR_WEDGE_SECONDS" 30.0 true (fun () -> Timeouts.supervisorWedgeAfter)
      seconds "manifestSaveSeconds" "Manifest save interval"
        "How often the daemon saves its session manifest, which is what a restart resumes from."
        "SAGEFS_MANIFEST_SAVE_INTERVAL_SECONDS" 60.0 false (fun () -> Timeouts.manifestSaveInterval)
      minutes "idleSessionMinutes" "Idle session threshold"
        "How long a Ready session can go untouched before the dashboard calls it idle instead of running."
        "SAGEFS_IDLE_SESSION_THRESHOLD_MINUTES" 10.0 false (fun () -> Timeouts.idleSessionThreshold)
      seconds "warmupReadyPollSeconds" "Warm-up ready poll ceiling"
        "The longest SageFs polls a worker for Ready after a spawn or restart before it faults the session."
        "SAGEFS_WARMUP_READY_POLL_SECONDS" 120.0 true (fun () -> Timeouts.warmupReadyPollMax)
      seconds "workerProxyWaitSeconds" "Test proxy registration wait"
        "How long a live-testing run waits for the worker to register its test proxy before it runs with none, which reports every test as not run."
        "SAGEFS_WORKER_PROXY_WAIT_SECONDS" 15.0 true (fun () -> Timeouts.workerProxyRegister)
      minutes "dataPruneMinutes" "Friction prune interval"
        "How often a running daemon prunes friction data again. It also prunes once at start."
        "SAGEFS_DATA_PRUNE_INTERVAL_MINUTES" 60.0 false (fun () -> DataRetention.pruneInterval)
      minutes "hygieneSweepMinutes" "Workspace hygiene sweep"
        "How often the daemon looks at what agents left behind in the workspace: it saves uncommitted worktree files to a salvage branch, records what it saw and tidies what is provably safe. It also runs once at start and when a session stops."
        "SAGEFS_HYGIENE_SWEEP_MINUTES" 15.0 false (fun () -> Timeouts.hygieneSweepInterval) ]

  let private ageRows : EnvBacked list =
    [ age "frictionMaxAgeDays" "Friction rows kept for"
        "Friction rows older than this are rolled into the per-version counts and deleted."
        "SAGEFS_FRICTION_MAX_AGE_DAYS" 30.0 (fun () -> DataRetention.frictionMaxAge)
      age "cohortLedgerRetentionDays" "Finished cohort ledger kept for"
        "A finished cohort's ledger rows are cleared once its last entry is older than this. A cohort that is running is never touched."
        "SAGEFS_COHORT_LEDGER_RETENTION_DAYS" 7.0 (fun () -> DataRetention.cohortLedgerRetention)
      age "hostCacheMaxAgeDays" "Unused FSI hosts kept for"
        "A built FSI host that no session has used for this long is pruned. A host rebuilds in a few minutes."
        "SAGEFS_HOST_CACHE_MAX_AGE_DAYS" 30.0 (fun () -> DataRetention.hostCacheMaxAge)
      age "gateRetentionDays" "Gate checkouts kept for"
        "A gate checkout or tier clone whose repository is still there is kept this long after its last gate, so the next gate on that repository stays warm."
        "SAGEFS_GATE_RETENTION_DAYS" 14.0 (fun () -> DataRetention.gateCheckoutRetention)
      age "tempRunMaxAgeDays" "Test run folders kept for"
        "A test runner's temp data folder, and any other `sagefs-*` temp entry with no recorded owner, can be reclaimed this long after it was last touched."
        "SAGEFS_TEMP_RUN_MAX_AGE_DAYS" 2.0 (fun () -> DataRetention.tempRunMaxAge)
      age "workerLogMaxAgeDays" "Worker logs kept for"
        "A worker's log is kept this long after its last write. It is what you read after a crash."
        "SAGEFS_WORKER_LOG_MAX_AGE_DAYS" 14.0 (fun () -> DataRetention.workerLogMaxAge)
      grace "finishedWorktreeGraceDays" "Merged agent worktrees kept for"
        "A merged agent worktree is kept this long after it was last in use, because the agent that made it may be resumed. 0 means no grace. Work that is not merged is never removed, whatever this says."
        "SAGEFS_FINISHED_WORKTREE_GRACE_DAYS" 1.0 (fun () -> DataRetention.finishedWorktreeGrace) ]

  let private countRows : EnvBacked list =
    [ count WaitsAndLimits "limits.sessionManagerQueueCapacity" "Session manager queue size"
        "How many commands may wait for the session manager before create, stop and restart are refused as busy. It is sized never to engage in normal use."
        "SAGEFS_SESSION_MANAGER_QUEUE_CAPACITY" 256 (fun () -> SageFsConfig.SessionManagerQueueCapacity)
      count WaitsAndLimits "limits.listMembersMax" "Members per list_members reply"
        "How many members one `list_members` reply carries. The reply states the total and names the offset for the rest."
        "SAGEFS_LIST_MEMBERS_MAX" 100 (fun () -> SageFsConfig.ListMembersMax)
      count WaitsAndLimits "limits.referencesProjectCache" "Projects kept checked for list_references"
        "How many projects the compiler keeps checked for `list_references`. Each holds its typed trees, so raise it only with memory to spare."
        "SAGEFS_REFERENCES_PROJECT_CACHE" 12 (fun () -> SageFsConfig.ReferencesProjectCacheSize)
      count WaitsAndLimits "limits.listReferencesMax" "References per list_references reply"
        "How many references one `list_references` reply carries. The reply states the total and names the offset for the rest."
        "SAGEFS_LIST_REFERENCES_MAX" 100 (fun () -> SageFsConfig.ListReferencesMax)
      count LocalData "data.frictionMaxRows" "Friction rows kept"
        "How many raw friction rows are kept per table. The newest win."
        "SAGEFS_FRICTION_MAX_ROWS" 5000 (fun () -> DataRetention.frictionMaxRows)
      count LocalData "data.frictionMaxAggregateVersions" "SageFs versions kept in friction counts"
        "How many SageFs versions keep their small count per kind of friction."
        "SAGEFS_FRICTION_MAX_AGGREGATE_VERSIONS" 20 (fun () -> DataRetention.frictionMaxAggregateVersions)
      count LocalData "data.gatePassRecordsKept" "Gate pass records kept"
        "How many of the local gate's pass records are kept, newest first. Each holds a commit's release bundle, about 270 MB."
        "SAGEFS_GATE_PASS_RECORDS_KEPT" 10 (fun () -> DataRetention.gatePassRecordsKept)
      count LocalData "data.gateLogsKept" "Gate logs kept"
        "How many of the gate's logs are kept, newest first. They are what you read after a red gate."
        "SAGEFS_GATE_LOGS_KEPT" 20 (fun () -> DataRetention.gateLogsKept) ]

  /// The compiler's memory ceiling, in megabytes, for `list_references`. Unset means a share of the machine's memory.
  let referencesResidentCeiling : EnvBacked =
    let variable = "SAGEFS_REFERENCES_RESIDENT_CEILING_MB"
    let parse (raw: string) : Result<SettingValue, ConfigError> =
      match raw.Trim().ToLowerInvariant() with
      | ""
      | "auto" -> Ok (VOptionalCount CountLeftToSageFs)
      | text ->
        parseCount 1 text
        |> Result.bind (fun v ->
          match SettingValue.count v with
          | Some c -> Ok (VOptionalCount (CountIs c))
          | None -> Error (Malformed text))
    let render (v: SettingValue) : string =
      match SettingValue.optionalCount v with
      | Some (CountIs c) -> string (WholeCount.value c)
      | Some CountLeftToSageFs -> "auto"
      | None -> ""
    { Descriptor =
        { Key = "limits.referencesResidentCeilingMb"
          Name = "List references memory ceiling (MB)"
          Description =
            "How many megabytes the worker may hold before `list_references` drops the compiler's cache. auto is a share of this machine's memory, which fits a 4 GB laptop and a 64 GB workstation."
          Category = WaitsAndLimits
          Scope = Global
          Applicability = RestartRequired
          Default = VOptionalCount CountLeftToSageFs
          Parse = parse
          Render = render
          Apply = ignore
          External = FromEnvironment variable
          Current =
            InUseFromConsumer (fun () ->
              match SageFsConfig.ReferencesResidentCeilingSetting with
              | SageFsConfig.ResidentCeilingSetting.Megabytes mb ->
                match WholeCount.create 1 mb with
                | Ok c -> VOptionalCount (CountIs c)
                | Error _ -> VOptionalCount CountLeftToSageFs
              | SageFsConfig.ResidentCeilingSetting.Unset
              | SageFsConfig.ResidentCeilingSetting.Rejected _ -> VOptionalCount CountLeftToSageFs) }
      Variable = variable
      EnvText =
        (fun v ->
          match SettingValue.optionalCount v with
          | Some (CountIs c) -> Some (string (WholeCount.value c))
          | Some CountLeftToSageFs
          | None -> None) }

  /// The browser dev-reload switch. One rule (`DevReloadMode.parse`) is read by the worker's injector, by
  /// `enable_hot_reload` and by this row.
  let devReload : EnvBacked =
    let variable = DevReloadMode.envVar
    { Descriptor =
        { Key = "hotreload.devReload"
          Name = "Browser dev-reload"
          Description =
            "On puts the live-refresh script into the pages of an app a hot reload session runs. Off turns it off for every session, and `enable_hot_reload` answers disabledByEnvVar. Read when a worker starts."
          Category = SessionSettings
          Scope = Global
          Applicability = RestartRequired
          Default = VToggle On
          Parse = toggleOf
          Render = renderToggle
          Apply = ignore
          External = FromEnvironment variable
          Current =
            InUseFromConsumer (fun () ->
              match DevReloadMode.fromEnvironment () with
              | DevReloadMode.Enabled -> VToggle On
              | DevReloadMode.Disabled -> VToggle Off) }
      Variable = variable
      EnvText =
        (fun v ->
          match SettingValue.toggle v with
          | Some On -> Some "1"
          | Some Off -> Some "0"
          | None -> None) }

  /// Which editor the clickable file links open. A text that names no editor is VS Code, as the links already did.
  let editor : EnvBacked =
    let names = "auto" :: (EditorChoice.all |> List.map EditorChoice.name)
    let valueOf (choice: EditorChoice) : SettingValue =
      match EnumValue.create names (EditorChoice.name choice) with
      | Ok e -> VEnum e
      | Error _ -> failwith "every editor choice is one of the names"
    let auto =
      match EnumValue.create names "auto" with
      | Ok e -> VEnum e
      | Error _ -> failwith "auto is one of the names"
    let parse (raw: string) : Result<SettingValue, ConfigError> =
      match raw.Trim().ToLowerInvariant() with
      | ""
      | "auto" -> Ok auto
      // The same judgment the links make: text is the editor it names, and text naming none is VS Code.
      | text -> Ok (valueOf (EditorChoice.ofText text))
    let render (v: SettingValue) : string = SettingValue.enum v |> Option.map EnumValue.value |> Option.defaultValue ""
    { Descriptor =
        { Key = "devreload.editor"
          Name = "Editor for file links"
          Description =
            "Which editor the clickable file:line links in hot reload pages open: vscode, cursor or jetbrains. auto follows the EDITOR variable and then VS Code."
          Category = SessionSettings
          Scope = Global
          Applicability = RestartRequired
          Default = auto
          Parse = parse
          Render = render
          Apply = ignore
          External = FromEnvironment EditorChoice.envVar
          Current = InUseFromConsumer (fun () -> valueOf (EditorChoice.fromEnvironment ())) }
      Variable = EditorChoice.envVar
      EnvText =
        (fun v ->
          match render v with
          | "auto"
          | "" -> None
          | text -> Some text) }

  /// Every setting that is saved as a file value and imported into the environment at start.
  let all : EnvBacked list =
    EnvBackedSettings.firstBatch @ [ devReload; editor; referencesResidentCeiling ] @ waitRows @ ageRows @ countRows

  /// A row for a variable the page does not edit. It reads the same variable, so the row shows the value in use.
  let private shownOnly (reason: ReadOnlyReason) (category: SettingCategory) (setting: EnvBacked) : SettingDescriptor =
    { setting.Descriptor with Applicability = ReadOnly reason; Category = category }

  let private watchdogRow (key: string) (name: string) (help: string) (variable: string) (def: SettingValue) (parse: string -> Result<SettingValue, ConfigError>) (render: SettingValue -> string) (current: unit -> SettingValue) : SettingDescriptor =
    { Key = key
      Name = name
      Description = help
      Category = DaemonSettings
      Scope = Global
      Applicability = ReadOnly SetBySageFs
      Default = def
      Parse = parse
      Render = render
      Apply = ignore
      External = FromEnvironment variable
      Current = InUseFromConsumer current }

  /// The variables shown and never edited here, each with the reason on its row.
  let readOnly : SettingDescriptor list =
    [ shownOnly TestHarnessOnly TestHarness
        (seconds "testDaemonReadySeconds" "Test daemon ready deadline"
          "How long a test waits for a daemon it started to reach a readable Ready state."
          "SAGEFS_TEST_DAEMON_READY_SECONDS" 120.0 false (fun () -> Timeouts.integrationDaemonReady))
      shownOnly TestHarnessOnly TestHarness
        (seconds "testWorkerRestartSeconds" "Test worker restart deadline"
          "How long the crash and restart smoke test waits for a killed worker to come back on a new process."
          "SAGEFS_TEST_WORKER_RESTART_SECONDS" 60.0 false (fun () -> Timeouts.integrationWorkerRestart))
      shownOnly TestHarnessOnly TestHarness
        (seconds "testBrowserWarmupSeconds" "Test browser warm-up deadline"
          "How long a browser journey waits for its dashboard session to warm up."
          "SAGEFS_TEST_BROWSER_WARMUP_SECONDS" 300.0 false (fun () -> Timeouts.browserJourneyWarmup))
      shownOnly TestHarnessOnly TestHarness
        (seconds "testWebAppBuildSeconds" "Test web app build deadline"
          "How long the hot reload verification waits for its web app sample to build."
          "SAGEFS_TEST_WEBAPP_BUILD_SECONDS" 180.0 false (fun () -> Timeouts.webAppHotReloadBuild))
      shownOnly TestHarnessOnly TestHarness
        (seconds "testWebAppPortSeconds" "Test web app port deadline"
          "How long the hot reload verification waits for its web app sample to bind its port and report ready."
          "SAGEFS_TEST_WEBAPP_PORT_SECONDS" 120.0 false (fun () -> Timeouts.webAppPortReady))
      shownOnly Superseded DaemonSettings
        (count DaemonSettings "waits.workerStartupTimeoutMs" "Worker start-up timeout (milliseconds)"
          "The flat wait for a worker's port that the warm-up inactivity limit and ceiling replaced."
          "SAGEFS_WORKER_STARTUP_TIMEOUT_MS" 120000 (fun () -> SageFsConfig.WorkerStartupTimeoutMs))
      watchdogRow "daemon.supervised" "Running under the watchdog"
        "On when the watchdog started this daemon, so it restarts after a crash."
        "SAGEFS_SUPERVISED" (VToggle Off) toggleOf renderToggle
        (fun () -> VToggle (Toggle.ofBool SageFsConfig.IsSupervised))
      watchdogRow "daemon.restartCount" "Watchdog restart count"
        "How many times the watchdog has restarted this daemon. It stops restarting a daemon that keeps crashing."
        "SAGEFS_RESTART_COUNT" (VCount (legalCount 0 0)) (parseCount 0) renderCount
        (fun () ->
          match WholeCount.create 0 SageFsConfig.RestartCount with
          | Ok held -> VCount held
          | Error _ -> VCount (legalCount 0 0)) ]

  let descriptors : SettingDescriptor list = (all |> List.map (fun e -> e.Descriptor)) @ readOnly
