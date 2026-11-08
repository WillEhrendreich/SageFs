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
    RetentionAge.clamped (TimeSpan.FromDays days)

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
    WholeCount.clamped least n

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
      seconds "rebuildReadySeconds" "Rebuild ready wait"
        "How long live testing waits, after a rebuild restart, for the session to be Ready before it reports the rebuild as failed."
        "SAGEFS_REBUILD_READY_SECONDS" 30.0 true (fun () -> Timeouts.rebuildReadyWait)
      seconds "patchConfirmSeconds" "Patch confirmation wait"
        "How long a patched function may go without its new body running before the save reports it as never entered."
        "SAGEFS_PATCH_CONFIRM_SECONDS" 10.0 false (fun () -> Timeouts.patchConfirmation)
      seconds "hotReloadCompileQueueSeconds" "Hot reload compile queue wait"
        "How long one save waits for the previous save's compile before it gives up and says so."
        "SAGEFS_HOT_RELOAD_COMPILE_QUEUE_SECONDS" 60.0 true (fun () -> Timeouts.compileQueueWait)
      seconds "referencesCheckSeconds" "List references check limit"
        "How long the compiler checks behind `list_references` may take, all of a session's projects together. Raise it on a very large solution."
        "SAGEFS_REFERENCES_CHECK_SECONDS" 60.0 true (fun () -> Timeouts.referencesCheck)
      seconds "dashboardHeartbeatSeconds" "Dashboard heartbeat"
        "How often the dashboard stream sends a heartbeat, so the page can tell the daemon is alive."
        "SAGEFS_DASHBOARD_HEARTBEAT_SECONDS" 5.0 false (fun () -> Timeouts.dashboardHeartbeat)
      seconds "dashboardStaleAfterSeconds" "Dashboard stale after"
        "How long the page waits without a heartbeat before it asks the daemon directly whether it is running."
        "SAGEFS_DASHBOARD_STALE_AFTER_SECONDS" 15.0 false (fun () -> Timeouts.dashboardStaleAfter)
      seconds "stopSessionSeconds" "Stop session reply limit"
        "How long `stop_session` waits for the session manager's reply before it fails with an error instead of hanging."
        "SAGEFS_STOP_SESSION_TIMEOUT_SECONDS" 20.0 true (fun () -> Timeouts.stopSessionMailboxTimeout)
      seconds "supervisorWedgeSeconds" "Supervisor wedge limit"
        "How long one command may hold the session manager's loop before the supervisor calls it wedged."
        "SAGEFS_SUPERVISOR_WEDGE_SECONDS" 30.0 true (fun () -> Timeouts.supervisorWedgeAfter)
      seconds "manifestSaveSeconds" "Manifest save interval"
        "How often the daemon saves its session manifest, which is what a restart resumes from."
        "SAGEFS_MANIFEST_SAVE_INTERVAL_SECONDS" 60.0 false (fun () -> Timeouts.manifestSaveInterval)
      seconds "warmupReadyPollSeconds" "Warm-up ready poll ceiling"
        "The longest SageFs polls a worker for Ready after a spawn or restart before it faults the session."
        "SAGEFS_WARMUP_READY_POLL_SECONDS" 120.0 true (fun () -> Timeouts.warmupReadyPollMax)
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
    [ count WaitsAndLimits "limits.listMembersMax" "Members per list_members reply"
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
      VEnum (EnumValue.createOrElse names (EditorChoice.name choice) (EditorChoice.name choice))
    let auto = VEnum (EnumValue.createOrElse names "auto" "auto")
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

  /// Every setting that is saved as a file value and resolved into the settings snapshot at start.
  let all : EnvBacked list =
    EnvBackedSettings.firstBatch @ [ devReload; editor; referencesResidentCeiling ] @ waitRows @ ageRows @ countRows

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
    [ watchdogRow "daemon.supervised" "Running under the watchdog"
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
