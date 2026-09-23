module SageFs.Server.DaemonMode

open System
open System.Threading
open System.Threading.Tasks
open SageFs
open SageFs.WarmUp
open SageFs.Utils
open SageFs.Server
open SageFs.Server.DashboardTypes
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.ResponseCompression
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open OpenTelemetry.Logs

/// Send a message through the session proxy with railway error handling.
/// Centralizes error recovery for IO, pipe, and disposed exceptions.
/// Wrapped in a daemon.proxy_to_worker span for trace propagation to workers.
///
/// onWorkerDied: called synchronously when a pipe break reveals the worker is dead.
/// Posts SessionCommand to accelerate Faulted transition — closes race window between
/// pipe failure and process.Exited event firing.
let proxyToSession
  (getProxy: string -> Threading.Tasks.Task<(WorkerProtocol.WorkerMessage -> Async<WorkerProtocol.WorkerResponse>) option>)
  (onWorkerDied: string -> unit)
  (sid: string)
  (msg: WorkerProtocol.WorkerMessage)
  : Threading.Tasks.Task<Result<WorkerProtocol.WorkerResponse, SageFsError>> = task {
  let sw = System.Diagnostics.Stopwatch.StartNew()
  let activity =
    Instrumentation.startSpanWithKind
      Instrumentation.daemonSource "daemon.proxy_to_worker"
      System.Diagnostics.ActivityKind.Client
      [("session.id", box sid); ("worker.message_type", box (msg.GetType().Name))]
  match sid with
  | null | "" ->
    sw.Stop()
    Instrumentation.workerRequestErrors.Add(1L)
    Instrumentation.failSpan activity "empty session id"
    return Error (SageFsError.SessionNotFound (sid |> Option.ofObj |> Option.defaultValue ""))
  | _ ->
    try
      let! proxy = getProxy sid
      match proxy with
      | Some send ->
        let! resp = send msg |> Async.StartAsTask
        sw.Stop()
        Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
        Instrumentation.succeedSpan activity
        return Ok resp
      | None ->
        sw.Stop()
        Instrumentation.workerRequestErrors.Add(1L)
        Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
        Instrumentation.failSpan activity "No proxy available for session"
        return Error (SageFsError.WorkerCommunicationFailed(sid, "No proxy available for session"))
    with
    | :? IO.IOException as ex ->
      sw.Stop()
      Instrumentation.workerRequestErrors.Add(1L)
      Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
      Instrumentation.failSpan activity ex.Message
      onWorkerDied sid
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe broken — %s" ex.Message))
    | :? AggregateException as ae when (ae.InnerException :? IO.IOException) ->
      sw.Stop()
      Instrumentation.workerRequestErrors.Add(1L)
      Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
      Instrumentation.failSpan activity ae.InnerException.Message
      onWorkerDied sid
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe broken — %s" ae.InnerException.Message))
    | :? AggregateException as ae when (ae.InnerException :? ObjectDisposedException) ->
      sw.Stop()
      Instrumentation.workerRequestErrors.Add(1L)
      Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
      Instrumentation.failSpan activity ae.InnerException.Message
      onWorkerDied sid
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe closed — %s" ae.InnerException.Message))
    | :? ObjectDisposedException as ex ->
      sw.Stop()
      Instrumentation.workerRequestErrors.Add(1L)
      Instrumentation.workerRequestDurationMs.Record(sw.Elapsed.TotalMilliseconds)
      Instrumentation.failSpan activity ex.Message
      onWorkerDied sid
      return Error (SageFsError.WorkerCommunicationFailed(sid, sprintf "Session pipe closed — %s" ex.Message))
}

/// Convert a known-good session ID string to SessionId.
/// Only use for strings that originated from a valid SessionId.
let private toSessionId (s: string) =
  match WorkerProtocol.SessionId.validate s with
  | Ok sid -> sid
  | Error msg -> failwithf "Invalid session ID '%s': %s" s msg

// ---------------------------------------------------------------------------
// DaemonInfra — lifetime group 1: one-time daemon infrastructure
// ---------------------------------------------------------------------------

/// Infrastructure created once at daemon startup.
/// Groups logger, HTTP client, friction store, cancellation, and state-change event.
type DaemonInfra = {
  Log: ILogger
  LoggerFactory: ILoggerFactory
  HttpClient: Net.Http.HttpClient
  FrictionStore: SageFs.Features.FrictionSqlite.FrictionStore option
  DaemonStreamId: string
  Cts: CancellationTokenSource
  StateChangedEvent: Event<SseEvent>
  /// Timeout for agent-facing worker fetches (MCP tools, SSE).
  McpFetchTimeoutSec: float
  /// Timeout for user-facing worker fetches (dashboard).
  DashboardFetchTimeoutSec: float
}

/// Create one-time daemon infrastructure (logger, HTTP client, friction store, CTS).
/// The Present cohort members whose bound identity shows fresh activity — the
/// ones whose lease the reaper renews on this tick before reaping the silent
/// rest (roast-7 §5). Pure: `isActive` is the freshness probe (production:
/// AgentActivityTracker.getActivePresences with a window shorter than the
/// tracker's own 5-min hard eviction, so an active member is always caught
/// before its presence is dropped). Keyed on the cohort's OWN MemberIds, so a
/// renewal can never miss a member on a display-string round-trip.
let cohortMembersToRenew
  (isActive: MemberTable.MemberId -> bool)
  (members: Map<MemberTable.MemberId, SageFs.Cohort.MemberRecord>)
  : MemberTable.MemberId list =
  members
  |> Map.toList
  |> List.choose (fun (m, r) ->
    match r.Presence with
    | SageFs.Cohort.MemberPresence.Present when isActive m -> Some m
    | _ -> None)

/// Marker written into every daemon's own data dir recording the daemon's
/// own pid — the ground truth `sweepOrphanedTempDirs` proves an isolated
/// test data dir's owner is gone against. Harmless on a real `~/.SageFs`
/// (a normal daemon's own dir is never a sweep target — only
/// `<tmp>/sagefs-test/*` is scanned below).
let private dataDirOwnerMarkerFileName = "daemon.pid"

let private testDataDirRoot () =
  IO.Path.Combine(IO.Path.GetTempPath(), "sagefs-test")

/// Reclaims temp-root directories any SageFs process may have leaked when it
/// died before its own `finally`/`Exited` handler could run: private
/// self-host launch roots (`sagefs-host-adopt-*`, ~680-835MB each) and
/// isolated test data dirs (`sagefs-test/<guid>`, one per test-spawned
/// daemon's `SAGEFS_DATA_DIR`) — the exact two families that took a machine
/// down by filling a 32GB /tmp tmpfs (13 launch roots ≈ 9.5GB, plus 449
/// stale test data dirs). Fail-closed like `ShadowCopy.cleanupStaleDirs`:
/// only a directory whose recorded owner pid is provably gone is ever
/// removed; a directory with no marker, a live owner, or an owner whose
/// liveness cannot be determined is always left alone.
///
/// Run once at startup — so ANY daemon starting after a hard-killed one
/// reclaims its mess, without depending on that daemon ever running code
/// again — and re-run periodically, so a single long-lived daemon does not
/// sit next to another dead process's leak for its whole uptime.
let sweepOrphanedTempDirs (log: ILogger) : unit =
  try
    match HostCoreAdoption.sweepStaleAdoptedRoots () with
    | [] -> ()
    | removed ->
      log.LogInformation(
        "Swept {Count} orphaned self-host launch root(s) from prior runs, reclaiming {Bytes} bytes: {Dirs}",
        List.length removed,
        removed |> List.sumBy snd,
        removed |> List.map fst |> String.concat ", ")
  with ex ->
    log.LogWarning("Self-host launch root sweep failed: {Error}", ex.Message)
  try
    match OrphanTempDirSweep.sweep (testDataDirRoot ()) "*" dataDirOwnerMarkerFileName ShadowCopy.processLiveness with
    | [] -> ()
    | removed ->
      log.LogInformation(
        "Swept {Count} orphaned isolated test data dir(s) from prior runs, reclaiming {Bytes} bytes",
        List.length removed,
        removed |> List.sumBy snd)
  with ex ->
    log.LogWarning("Isolated test data dir sweep failed: {Error}", ex.Message)

/// Friction retention: keep only the running version's rows, inside the age
/// and row caps, and roll everything else into the per-version aggregate. Run
/// on start and from `frictionPruneTimer`. Never throws; a failed prune just
/// gets logged and tried again next time.
let pruneFriction (log: ILogger) (store: SageFs.Features.FrictionSqlite.FrictionStore) =
  let version = SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current ()
  match store.Prune (SageFs.Features.LocalDataRetention.defaultPolicy ()) DateTimeOffset.UtcNow version with
  | Ok outcome ->
    match outcome.EventsDropped + outcome.FeedbackDropped + outcome.AggregateVersionsDropped.Length with
    | 0 -> ()
    | _ ->
      log.LogInformation(
        "Friction pruned: {Events} events and {Feedback} feedback rows rolled into the per-version counts; dropped counts for {Versions} old versions",
        outcome.EventsDropped, outcome.FeedbackDropped, outcome.AggregateVersionsDropped.Length)
  | Error err -> log.LogWarning("Friction prune failed: {Error}", err)

let createDaemonInfrastructure () : DaemonInfra =
  let otelConfigured = DaemonInfo.otelConfigured
  let loggerFactory =
    LoggerFactory.Create(fun builder ->
      builder
        .AddConsole()
        .SetMinimumLevel(LogLevel.Information)
        .AddFilter("Microsoft", LogLevel.Warning)
      |> ignore
      match otelConfigured with
      | true ->
        builder.AddOpenTelemetry(fun otel ->
          otel.IncludeFormattedMessage <- true
          otel.IncludeScopes <- true
          otel.AddOtlpExporter() |> ignore
        ) |> ignore
      | false -> ()
    )
  let log = loggerFactory.CreateLogger("SageFs.Daemon")
  let httpClient = new Net.Http.HttpClient()

  log.LogInformation("SageFs daemon v{Version} starting", DaemonInfo.version)

  // Sweep shadow-copy directories left behind by worker processes that are
  // provably gone. cleanupStaleDirs otherwise only runs when a live session
  // hard-resets, so a daemon that was hard-killed (or its workers were) leaks
  // its /tmp/sagefs-shadow-* dirs until some future session happens to rebuild.
  // Doing it on every boot keeps the graveyard swept and fails closed — only
  // dead-owner dirs are ever removed, never a live session's.
  try
    ShadowCopy.cleanupStaleDirs ()
    log.LogInformation("Swept stale shadow-copy directories from prior runs")
  with ex ->
    log.LogWarning("Shadow-copy sweep on startup failed: {Error}", ex.Message)

  // Same treatment for sagefs-host-adopt-* private launch roots and
  // sagefs-test/<guid> isolated data dirs — see sweepOrphanedTempDirs.
  sweepOrphanedTempDirs log

  // Ensure adequate thread pool for concurrent SSE/MCP/effects
  let minWorker, minIO = System.Threading.ThreadPool.GetMinThreads()
  let desiredMin = max 32 (System.Environment.ProcessorCount * 4)
  match minWorker < desiredMin with
  | true ->
    System.Threading.ThreadPool.SetMinThreads(desiredMin, max minIO desiredMin) |> ignore
    log.LogInformation("ThreadPool min threads: {Old} → {New}", minWorker, desiredMin)
  | false -> ()

  // Create durable SQLite friction store at ~/.SageFs/friction.db
  let frictionStore =
    try
      let dir = DaemonState.SageFsDir
      // Creates the dir if missing AND hardens it to owner-only on Unix, even if
      // another writer created it first (roast-9 §8).
      DaemonState.ensureDataDir ()
      // Record this daemon as the owner of its own data dir — the ground
      // truth a LATER daemon's sweepOrphanedTempDirs proves liveness
      // against when `dir` is an isolated `sagefs-test/<guid>` throwaway
      // (SAGEFS_DATA_DIR). Harmless (and never a sweep target) on a real
      // ~/.SageFs.
      OrphanTempDirSweep.writeOwnerPid dataDirOwnerMarkerFileName dir Environment.ProcessId
      let dbPath = LocalData.frictionPath dir
      let connStr = sprintf "Data Source=%s" dbPath
      let store = SageFs.Features.FrictionSqlite.Store.create connStr
      match store.Initialize() with
      | Ok () ->
        log.LogInformation("Friction store initialized at {Path}", dbPath)
        pruneFriction log store
        Some store
      | Error err ->
        log.LogWarning("Friction store initialization failed: {Error}. Friction telemetry will not persist.", err)
        None
    with ex ->
      log.LogWarning("Friction store creation failed: {Error}. Friction telemetry will not persist.", ex.Message)
      None

  {
    Log = log
    LoggerFactory = loggerFactory
    HttpClient = httpClient
    FrictionStore = frictionStore
    DaemonStreamId = "daemon-sessions"
    Cts = new CancellationTokenSource()
    StateChangedEvent = Event<SseEvent>()
    McpFetchTimeoutSec = 5.0
    DashboardFetchTimeoutSec = 0.5
  }

/// Interpret the manifest owner's answer to a --prune request. Synchronous and
/// separate so handlePrune's task{} stays shallow (avoids FS3511).
let private pruneOutcome (log: ILogger) (result: Features.ManifestOwner.CommitResult) : Result<bool, string> =
  match result with
  | Ok committed ->
    match committed.Persisted with
    | Features.ManifestOwner.Persisted.WrittenTo _ ->
      let pruned = Features.DaemonManifest.DaemonManifestState.aliveSessions committed.Previous
      log.LogInformation("Pruned {Count} session(s) from binary manifest", pruned.Length)
    | Features.ManifestOwner.Persisted.Unchanged ->
      log.LogInformation("No alive sessions to prune")
    Result.Ok true
  // W31(R13)/W36(R14): an unreadable manifest is an error, never "nothing to prune".
  | Error (Features.ManifestOwner.CommitError.BaseUnreadable (Features.ManifestTypes.ManifestLoadError.IoError err)) ->
    log.LogWarning("Cannot read manifest for prune — leaving untouched: {Error}", err)
    Result.Error (sprintf "Cannot prune: manifest read failed: %s" err)
  | Error (Features.ManifestOwner.CommitError.BaseUnreadable (Features.ManifestTypes.ManifestLoadError.CorruptData err)) ->
    log.LogWarning("Manifest corrupt — prune skipped, manual recovery needed: {Error}", err)
    Result.Error (sprintf "Cannot prune: manifest corrupt: %s" err)
  | Error (Features.ManifestOwner.CommitError.BaseUnreadable Features.ManifestTypes.ManifestLoadError.NotFound) ->
    log.LogInformation("No binary manifest found — nothing to prune")
    Result.Ok true
  | Error (Features.ManifestOwner.CommitError.WriteFailed err) ->
    log.LogWarning("Prune save failed: {Error}", err)
    Result.Error (sprintf "Cannot prune: manifest write failed: %s" err)
  | Error Features.ManifestOwner.CommitError.AfterShutdown ->
    Result.Error (sprintf "Cannot prune: %s" (Features.ManifestOwner.CommitError.describe Features.ManifestOwner.CommitError.AfterShutdown))

/// Handle --prune flag: clear the binary manifest and return a Result.
/// W28+W31(R13): Parametrized dir/log/checkDaemonRunning for testability.
/// W36(R14): Returns Result<bool, string> — Ok true=pruned/exit, Ok false=not-requested/continue,
///           Error msg=prune-was-requested-but-failed → caller exits with error.
/// W42(R14): checkDaemonRunning: unit -> Task<DaemonInfo option> to avoid Async.RunSynchronously
///           inside task{} (thread pool starvation risk).
let handlePrune (dir: string) (log: ILogger) (checkDaemonRunning: unit -> System.Threading.Tasks.Task<DaemonInfo option>) (flags: Args.DaemonFlags) = task {
  match flags.Prune with
  | true ->
    // W28(R13): Refuse to prune if daemon is running — cross-process TOCTOU guard.
    // W42(R14): await Task directly instead of Async.RunSynchronously in task{}.
    let! daemonInfo = checkDaemonRunning()
    match daemonInfo with
    | Some info ->
      log.LogWarning("Cannot prune while daemon is running (PID {Pid}) — stop the daemon first", info.Pid)
      return Result.Error (sprintf "Cannot prune: daemon running at PID %d — stop it first" info.Pid)
    | None ->
      // Even a one-shot prune writes through the single manifest owner.
      use owner = Features.ManifestOwner.start (Log.asILogger ()) dir
      let! committed = owner.Commit (Features.DaemonManifest.ManifestMutation.StampAllStopped DateTimeOffset.UtcNow)
      return pruneOutcome log committed
  | false -> return Result.Ok false
}

/// Remove adaptive-store snapshots AND recent-output ring buffers for
/// sessions that no longer exist, and reset the shared feature push state
/// when the last session is gone. Returns the ids that were swept (roast
/// queue item 2 — session state must die with the session instead of
/// accumulating for the daemon's lifetime).
///
/// `liveIds` is deliberately not "every session ManagerState still knows
/// about" — the caller passes only sessions worth RETAINING state for.
/// `SessionLifecycleStatus.isDead` (Faulted/Stopped) is the daemon's own
/// call: a session record can stay registered so a user can see why it
/// died, while its adaptive live-bindings snapshot and recent output (both
/// meaningless once the worker that produced them is gone) are freed
/// immediately rather than lingering until the record itself is purged.
let sweepStaleSessionState
  (liveIds: Set<string>)
  (adaptive: SageFs.Features.LiveBindingsAdaptive.State)
  (featureState: SageFs.Features.FeatureHooks.FeaturePushState ref)
  (outputStore: SessionOutputStore)
  : string list =
  let staleAdaptive =
    adaptive.SessionSnapshots.Keys
    |> Seq.filter (fun k -> not (liveIds.Contains k))
    |> Seq.toList
  staleAdaptive |> List.iter (fun k -> SageFs.Features.LiveBindingsAdaptive.remove adaptive k)
  let staleOutput =
    outputStore.LiveSessionIds
    |> List.filter (fun k -> not (liveIds.Contains k))
  staleOutput |> List.iter outputStore.Remove
  if liveIds.IsEmpty then
    System.Threading.Volatile.Write(&featureState.contents, SageFs.Features.FeatureHooks.FeaturePushState.empty)
  staleAdaptive @ staleOutput |> List.distinct

/// Admission gate for the SessionManager mailbox — mirrors ElmLoop's own
/// 256-message high-watermark alarm (`ElmLoop.fs:104-131`) for the mailbox
/// that didn't have an equivalent bound. `MailboxProcessor.CurrentQueueLength`
/// is the real, already-maintained queue depth — no extra counter needed.
/// Applied only to CreateSession/RestartSession (the ops that ADD work to an
/// already-loaded daemon); StopSession is deliberately exempt — refusing a
/// stop during overload would block the one thing that relieves it, and it
/// already has its own bounded timeout (`Timeouts.stopSessionMailboxTimeout`).
/// Checked and refused BEFORE posting, so an overloaded mailbox never grows
/// its queue further — the refusal itself is O(1) and never touches the
/// mailbox.
let private checkMailboxAdmission (sessionManager: MailboxProcessor<SessionManager.SessionCommand>) : Result<unit, SageFsError> =
  let pending = sessionManager.CurrentQueueLength
  let capacity = SageFsConfig.SessionManagerQueueCapacity
  let decision = SageFsError.admissionDecision pending capacity
  match decision with
  | Result.Error _ -> Log.warn "[SessionManager] Mailbox admission refused: %d pending >= capacity %d" pending capacity
  | Result.Ok () -> ()
  decision

/// The real sink: feeds a `MailboxQueueDepth` reading to the health watch.
/// A named top-level function, not an inline lambda, so `sampleMailboxQueueDepth`
/// below never allocates a closure over `HealthWatch` per tick.
let private observeMailboxQueueDepthToHealthWatch (depth: float) : unit =
  SageFs.Features.HealthWatch.observe
    SageFs.Features.HealthAnomaly.SignalId.MailboxQueueDepth
    depth
    System.DateTimeOffset.UtcNow
  |> ignore

/// Reports a `MailboxQueueDepth` reading to `observe`. Takes the depth as a
/// getter, not the mailbox itself, so this is testable with a fake counter —
/// the real caller passes `(fun () -> sessionManager.CurrentQueueLength)`, the
/// same already-maintained counter `checkMailboxAdmission` above reads. O(1),
/// never posts to the mailbox, never blocks it. `observe` is a parameter, not
/// baked in, so this is testable with a plain capture instead of the shared,
/// globally-mutable `HealthWatch` registry other tests also touch.
let sampleMailboxQueueDepth (observe: float -> unit) (currentQueueLength: unit -> int) : unit =
  observe (float (currentQueueLength ()))

/// A `SessionInfo`'s memory-shedding shape, for `MemorySupervisor`: Dead
/// (see `SessionLifecycleStatus.isDead`) or Idle-for-this-long since
/// `LastActivity`. `isUserActive` is a caller-supplied predicate rather than
/// a fixed rule, because "who the user is looking at" only exists at the
/// dashboard/Elm-model layer this function does not have — the admission
/// check below passes `fun _ -> false` (admission refusal never depends on
/// it), while the periodic sweep passes the real active-session check.
let memorySessionSnapshotOf
  (now: System.DateTime)
  (isUserActive: WorkerProtocol.SessionId -> bool)
  (si: WorkerProtocol.SessionInfo)
  : MemorySupervisor.SessionSnapshot =
  let status =
    match WorkerProtocol.SessionLifecycleStatus.isDead si.Status with
    | true -> MemorySupervisor.SessionMemoryStatus.Dead
    | false -> MemorySupervisor.SessionMemoryStatus.Idle(now - si.LastActivity)
  { Id = WorkerProtocol.SessionId.value si.Id
    Status = status
    IsUserActive = isUserActive si.Id }

/// Machine memory admission gate — mirrors `checkMailboxAdmission` above,
/// one level up: that one refuses when the MAILBOX is overloaded, this one
/// refuses when the MACHINE is short on memory
/// (`MemoryPressure.Critical`). Checked and refused
/// BEFORE posting, same as the mailbox gate, so a starving machine never
/// gains one more session to feed.
let private checkMemoryAdmission (readSnapshot: unit -> SessionManager.QuerySnapshot) : Result<unit, SageFsError> =
  let now = System.DateTime.UtcNow
  let sessions =
    SessionManager.QuerySnapshot.allSessions (readSnapshot())
    |> List.map (memorySessionSnapshotOf now (fun _ -> false))
  let rss = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64
  let machineMemory = Features.MachineMemory.current ()
  let machine : MemorySupervisor.MachineStats =
    { DaemonRssBytes = rss
      MachineAvailableBytes = machineMemory.AvailableBytes
      MachineTotalBytes = machineMemory.TotalBytes }
  let decision = Features.MemoryPressureWatch.evaluate MemorySupervisor.defaultThresholds machine sessions
  decision.Actions
  |> List.tryPick (function MemorySupervisor.ShedAction.RefuseNewSessions reason -> Some reason | _ -> None)
  |> function
     | Some reason ->
       Log.warn "[MemorySupervisor] Admission refused: %s" reason
       Result.Error(SageFsError.MemoryPressureRefused reason)
     | None -> Result.Ok ()

/// Build SessionManagementOps record from mailbox + snapshot reader.
/// Session lifecycle events are recorded directly in the daemon.sagefm binary
/// manifest (the sole source of truth for session resume) — there is no
/// separate event-append step or per-session event stream.
let createSessionOps
  (sessionManager: MailboxProcessor<SessionManager.SessionCommand>)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (manifestOwner: Features.ManifestOwner.Handle)
  : SessionManagementOps =
  {
    CreateSession = fun projects workingDir workflow ->
      task {
        match checkMailboxAdmission sessionManager with
        | Result.Error busy -> return Result.Error busy
        | Result.Ok () ->
        match checkMemoryAdmission readSnapshot with
        | Result.Error refused -> return Result.Error refused
        | Result.Ok () ->
        let autoOpenNamespaces = DirectoryConfig.autoOpenNamespacesForDirectory workingDir
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.CreateSession(projects, workingDir, autoOpenNamespaces, workflow, reply))
          |> Async.StartAsTask
        return
          result
          |> Result.map (fun info -> WorkerProtocol.SessionId.value info.Id)
      }
    ListSessions = fun () ->
      task {
        let sessions = SessionManager.QuerySnapshot.allSessions (readSnapshot())
        return SessionOperations.formatSessionList DateTime.UtcNow None sessions
      }
    StopSession = fun sessionId ->
      task {
        // Bounded — see Timeouts.stopSessionMailboxTimeout: "a stop always
        // completes" must be true of stop_session itself, not just of the
        // mailbox command it sends. A stale/never-answered reply channel
        // (a future regression, not a known path today) fails loud and
        // fast here instead of hanging until the MCP client's own external
        // timeout does.
        try
          let! result =
            sessionManager.PostAndAsyncReply(
              (fun reply -> SessionManager.SessionCommand.StopSession(toSessionId sessionId, reply)),
              timeout = int Timeouts.stopSessionMailboxTimeout.TotalMilliseconds)
            |> Async.StartAsTask
          return
            result
            |> Result.map (fun () ->
              sprintf "Session '%s' stopped." sessionId)
        with :? System.TimeoutException ->
          return
            Result.Error (
              SageFsError.SessionStopFailed (
                sessionId,
                sprintf
                  "The session supervisor did not respond within %.0fs. The daemon's session mailbox may be stuck — check the daemon log. (SAGEFS_STOP_SESSION_TIMEOUT_SECONDS to adjust)"
                  Timeouts.stopSessionMailboxTimeout.TotalSeconds))
      }
    PurgeSession = fun sessionId ->
      task {
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.StopSession(toSessionId sessionId, reply))
          |> Async.StartAsTask
        match result with
        | Ok () ->
          // Remove the manifest entry entirely — the session is gone from the
          // resume picker too (purge = the corrupted-state escape hatch).
          let! committed = manifestOwner.Commit (Features.DaemonManifest.ManifestMutation.Remove sessionId)
          match committed with
          | Ok _ -> ()
          | Error err ->
            Log.warn "[DaemonMode] Purge session %s (remove manifest entry): %s" sessionId (Features.ManifestOwner.CommitError.describe err)
        | Error _ -> ()
        return
          result
          |> Result.map (fun () ->
            sprintf "Session '%s' purged — manifest entry removed." sessionId)
      }
    RestartSession = fun sessionId rebuild ->
      task {
        match checkMailboxAdmission sessionManager with
        | Result.Error busy -> return Result.Error busy
        | Result.Ok () ->
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.RestartSession(sessionId, rebuild, reply))
          |> Async.StartAsTask
        return result
      }
    GetProxy = fun sessionId ->
      // The one place SessionManagementOps hands out a session's proxy —
      // every route into a worker (eval, check, load-script, run-tests,
      // run_app, MCP, dashboard) resolves it here, so wrapping it with
      // `SessionProxy.touching` is enough to keep LastActivity honest for
      // all of them without threading a touch call through each call site.
      let snapshot = readSnapshot()
      let sidStr = WorkerProtocol.SessionId.value sessionId
      let urlMap = snapshot.WorkerBaseUrls |> Map.fold (fun acc k v -> Map.add (WorkerProtocol.SessionId.value k) v acc) Map.empty
      task {
        return
          HttpWorkerClient.proxyFromUrls sidStr urlMap
          |> Option.map (WorkerProtocol.SessionProxy.touching (fun () ->
            sessionManager.Post(SessionManager.SessionCommand.TouchSession sessionId)))
      }
    GetSessionInfo = fun sessionId ->
      task { return SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) }
    GetAllSessions = fun () ->
      task { return SessionManager.QuerySnapshot.allSessions (readSnapshot()) }
    GetAdoptedCore = fun sessionId ->
      task { return (readSnapshot()).AdoptedCore |> Map.tryFind sessionId }
    GetWarmupProgress = fun sessionId ->
      task { return (readSnapshot()).WarmupProgress |> Map.tryFind sessionId }
    UpdateSessionStatus = fun sessionId (status: WorkerProtocol.SessionLifecycleStatus) ->
      task {
        sessionManager.Post(
          SessionManager.SessionCommand.UpdateSessionStatus(sessionId, status))
      }
    NotifyWorkerDied = fun sessionId ->
      // Post WorkerExited with pid=-1 to accelerate Faulted transition.
      // The real proc.Exited event will also fire; the stale-event guard
      // in WorkerExited handler (checks currentPid <> workerPid) prevents double-restart.
      sessionManager.Post(
        SessionManager.SessionCommand.WorkerExited(sessionId, -1, -1))
    ClaimRun = fun sessionId project ->
      sessionManager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.ClaimRun(sessionId, project, reply))
      |> Async.StartAsTask
    ClaimStop = fun sessionId ->
      sessionManager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.ClaimStop(sessionId, reply))
      |> Async.StartAsTask
    AdvanceRun = fun sessionId generation next ->
      sessionManager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.AdvanceRun(sessionId, generation, next, reply))
      |> Async.StartAsTask
    EndAppRun = fun sessionId generation runId final ->
      sessionManager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.EndAppRun(sessionId, generation, runId, final, reply))
      |> Async.StartAsTask
    AwaitReady = fun sessionId timeout ->
      task {
        try
          return!
            sessionManager.PostAndAsyncReply(
              (fun reply -> SessionManager.SessionCommand.AwaitReady(sessionId, reply)),
              int timeout.TotalMilliseconds)
            |> Async.StartAsTask
        with :? TimeoutException ->
          return Error (SageFsError.WorkerTimeout (WorkerProtocol.SessionId.value sessionId, "restart", timeout.TotalSeconds))
      }
    SwitchWorkflow = fun sessionIdStr workflow ->
      task {
        let sessionId = toSessionId sessionIdStr
        let! result =
          sessionManager.PostAndAsyncReply(fun reply ->
            SessionManager.SessionCommand.SwitchWorkflow(sessionId, workflow, reply))
          |> Async.StartAsTask
        return result
      }
  }

/// Look up worker HTTP base URL for a session from CQRS snapshot.
let getWorkerBaseUrl (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: WorkerProtocol.SessionId) =
  let snapshot = readSnapshot()
  match Map.tryFind sid snapshot.WorkerBaseUrls with
  | Some url when url.Length > 0 -> Some url
  | _ -> None

/// Fetch JSON from a worker endpoint with timeout, returning None on failure.
let fetchWorkerEndpoint
  (httpClient: Net.Http.HttpClient)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (sessionId: WorkerProtocol.SessionId)
  (path: string)
  (timeout: float)
  (parse: string -> 'T)
  : Threading.Tasks.Task<'T option> = task {
  match getWorkerBaseUrl readSnapshot sessionId with
  | Some baseUrl ->
    try
      use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout))
      let! resp = httpClient.GetStringAsync(sprintf "%s%s" baseUrl path, cts.Token)
      return Some (parse resp)
    with
    | :? Threading.Tasks.TaskCanceledException ->
      Log.warn "[fetchWorkerEndpoint] Timeout (%.0fs) fetching %s for session %s" timeout path (WorkerProtocol.SessionId.value sessionId)
      return None
    | :? Net.Http.HttpRequestException as ex ->
      Log.error "[fetchWorkerEndpoint] HTTP error fetching %s for session %s: %s\n%s" path (WorkerProtocol.SessionId.value sessionId) ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      return None
    | ex ->
      Log.error "[fetchWorkerEndpoint] Unexpected error fetching %s for session %s: %s" path (WorkerProtocol.SessionId.value sessionId) (ex.GetType().Name)
      return None
  | None -> return None
}

/// Build DaemonManifestState from active sessions (used in periodic save + shutdown).
/// `activeSessionId` must come from the live Elm model (not the snapshot) since the
/// snapshot has no concept of "which session is currently active".
let buildManifestState (snapshot: SessionManager.QuerySnapshot) (activeSessionId: string option) =
  let activeSessions = SessionManager.QuerySnapshot.allSessions snapshot
  let toRecord (s: WorkerProtocol.SessionInfo) : Features.DaemonManifest.DaemonSessionRecord =
    { SessionId = WorkerProtocol.SessionId.value s.Id; Projects = s.Projects; WorkingDir = s.WorkingDirectory
      CreatedAt = DateTimeOffset(s.CreatedAt, TimeSpan.Zero); StoppedAt = None }
  { Features.DaemonManifest.DaemonManifestState.Sessions =
      activeSessions |> List.map (fun s -> WorkerProtocol.SessionId.value s.Id, toRecord s) |> Map.ofList
    Features.DaemonManifest.DaemonManifestState.ActiveSessionId = activeSessionId }

/// The sessions running right now, as manifest records (StoppedAt = None).
let liveSessionRecords (snapshot: SessionManager.QuerySnapshot) : Features.DaemonManifest.DaemonSessionRecord list =
  (buildManifestState snapshot None).Sessions |> Map.values |> List.ofSeq

/// The live sync the manifest owner commits: a periodic save (`stampActive = None`,
/// live sessions stay alive) or the shutdown save (`stampActive = Some now`, every
/// live session is stamped stopped). Takes the snapshot as a value (W25) so one
/// consistent read feeds the whole sync.
let liveSyncMutation
  (snapshot: SessionManager.QuerySnapshot)
  (activeSessionId: string option)
  (stampActive: DateTimeOffset option)
  : Features.DaemonManifest.ManifestMutation =
  let at, mode =
    match stampActive with
    | Some ts -> ts, Features.DaemonManifest.LiveSync.ShuttingDown
    | None -> DateTimeOffset.UtcNow, Features.DaemonManifest.LiveSync.Running
  Features.DaemonManifest.ManifestMutation.SyncLive (liveSessionRecords snapshot, activeSessionId, at, mode)

/// What a live sync would make of the manifest currently in `dir` — read only,
/// never writes. Production writes go through Features.ManifestOwner, which
/// applies the same pure ManifestMutation.apply; this keeps the merge rules
/// (W10/W20/W23/W34/W38) checkable against a directory.
/// Returns Error on an unreadable manifest: a write based on it would erase history.
let mergeManifestWithExisting
  (dir: string)
  (log: Microsoft.Extensions.Logging.ILogger)
  (snapshot: SessionManager.QuerySnapshot)
  (activeSessionId: string option)
  (stampActive: DateTimeOffset option)
  : Result<Features.DaemonManifest.DaemonManifestState, Features.ManifestTypes.ManifestLoadError> =
  let sync = liveSyncMutation snapshot activeSessionId stampActive
  match Features.DaemonPersistence.loadManifest dir with
  | Ok existing -> Ok (Features.DaemonManifest.ManifestMutation.apply sync existing)
  | Error Features.ManifestTypes.ManifestLoadError.NotFound ->
    Ok (Features.DaemonManifest.ManifestMutation.apply sync Features.DaemonManifest.DaemonManifestState.empty)
  | Error (Features.ManifestTypes.ManifestLoadError.IoError err) ->
    log.LogWarning("Cannot read manifest for merge — skipping write to preserve history: {Error}", err)
    Error (Features.ManifestTypes.ManifestLoadError.IoError err)
  | Error (Features.ManifestTypes.ManifestLoadError.CorruptData err) ->
    log.LogWarning("Manifest data corrupt — skipping write to preserve history: {Error}", err)
    Error (Features.ManifestTypes.ManifestLoadError.CorruptData err)

/// Log the manifest owner's answer to a live sync.
let logManifestCommit (log: ILogger) (level: LogLevel) (what: string) (result: Features.ManifestOwner.CommitResult) =
  match result with
  | Ok committed ->
    match committed.Persisted with
    | Features.ManifestOwner.Persisted.WrittenTo path -> log.Log(level, "{What}: saved session manifest to {Path}", what, path)
    | Features.ManifestOwner.Persisted.Unchanged -> log.LogDebug("{What}: session manifest unchanged", what)
  | Error (Features.ManifestOwner.CommitError.BaseUnreadable _ as err) ->
    log.LogWarning("{What} skipped — {Error}; writing now would erase the manifest's history", what, Features.ManifestOwner.CommitError.describe err)
  | Error err ->
    log.LogWarning("{What} failed: {Error}", what, Features.ManifestOwner.CommitError.describe err)

/// Get session state from CQRS snapshot.
let getSessionStateFromSnapshot (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: WorkerProtocol.SessionId) =
  let snapshot = readSnapshot()
  match SessionManager.QuerySnapshot.tryGetSession sid snapshot with
  | Some info -> WorkerProtocol.SessionLifecycleStatus.toSessionState info.Status
  | None -> SessionState.Uninitialized

/// Get working directory for a session from CQRS snapshot.
let getSessionWorkingDirFromSnapshot (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: WorkerProtocol.SessionId) =
  let snapshot = readSnapshot()
  match SessionManager.QuerySnapshot.tryGetSession sid snapshot with
  | Some info -> info.WorkingDirectory
  | None -> ""

/// Get warmup status message for a session.
let getStatusMsgFromSnapshot (readSnapshot: unit -> SessionManager.QuerySnapshot) (sid: WorkerProtocol.SessionId) =
  readSnapshot().WarmupProgress |> Map.tryFind sid

/// Whether an eval-stats read reflects the worker's actual answer, or the
/// daemon's own inability to obtain one. Collapsing both into
/// `EvalStats.empty` (the old behaviour) made "no evals yet" and "worker
/// unreachable" indistinguishable to every reader — the same defect class
/// as the parse bug above, one level up (sagefs-ux-roast.md §3.1/§11 Island
/// B item 2). `Live EvalStats.empty` (zero evals, worker answered) and
/// `Unreachable _` (no answer at all) are now different values; a caller
/// that only wants a number still gets one via `toEvalStats`.
[<RequireQualifiedAccess>]
type EvalStatsReading =
  | Live of Affordances.EvalStats
  | Unreachable of reason: string

module EvalStatsReading =
  let toEvalStats (reading: EvalStatsReading) : Affordances.EvalStats =
    match reading with
    | EvalStatsReading.Live stats -> stats
    | EvalStatsReading.Unreachable _ -> Affordances.EvalStats.empty

/// Fetch eval stats from the worker via the SAME typed daemon<->worker proxy
/// `/api/sessions` uses (`McpServer.fs`'s `mapSessionRoutes`:
/// `WorkerMessage.GetStatus` / `WorkerResponse.StatusResult`) instead of a
/// second, hand-rolled JSON parse of the worker's raw `/status` body. The
/// old parse read `evalCount`/`avgDurationMs`/`minDurationMs`/
/// `maxDurationMs` off the response envelope's ROOT; the worker's actual
/// reply nests them one level down inside the envelope's `value` — a
/// `StatusResult(replyId, snapshot)` — so `JsonElement.TryGetProperty`
/// silently returned `getInt`/`getLong`'s `0`/`0L` default on EVERY read,
/// and the dashboard's entire eval-performance readout (count, avg/min/max,
/// P50/P95, the sparkline) was permanently zero for every user
/// (sagefs-ux-roast.md §3.1/§11 Island B item 1).
let getEvalStatsReadingFromWorker
  (getProxy: WorkerProtocol.SessionId -> Threading.Tasks.Task<WorkerProtocol.SessionProxy option>)
  (sid: WorkerProtocol.SessionId)
  : Threading.Tasks.Task<EvalStatsReading> = task {
  let! proxy = getProxy sid
  match proxy with
  | None -> return EvalStatsReading.Unreachable "no worker is registered for this session"
  | Some send ->
    try
      let! resp = send (WorkerProtocol.WorkerMessage.GetStatus "dash-stats") |> Async.StartAsTask
      match resp with
      | WorkerProtocol.WorkerResponse.StatusResult(_, snap) ->
        return
          EvalStatsReading.Live
            { EvalCount = snap.EvalCount
              TotalDuration = TimeSpan.FromMilliseconds(float snap.AvgDurationMs * float snap.EvalCount)
              MinDuration = TimeSpan.FromMilliseconds(float snap.MinDurationMs)
              MaxDuration = TimeSpan.FromMilliseconds(float snap.MaxDurationMs) }
      | WorkerProtocol.WorkerResponse.WorkerError err ->
        return EvalStatsReading.Unreachable (sprintf "worker reported an error: %s" (SageFsError.describe err))
      | other ->
        return EvalStatsReading.Unreachable (sprintf "worker replied with an unexpected message (%s)" (other.GetType().Name))
    with
    | :? Net.Http.HttpRequestException as ex ->
      Log.warn "[getEvalStats] Worker unreachable for %s: %s" (WorkerProtocol.SessionId.value sid) ex.Message
      return EvalStatsReading.Unreachable (sprintf "worker unreachable: %s" ex.Message)
    | :? Threading.Tasks.TaskCanceledException ->
      Log.warn "[getEvalStats] Timed out reaching worker for %s" (WorkerProtocol.SessionId.value sid)
      return EvalStatsReading.Unreachable "timed out waiting for the worker"
    | ex ->
      Log.error "[getEvalStats] Unexpected error for %s: %s (%s)\n%s" (WorkerProtocol.SessionId.value sid) ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
      return EvalStatsReading.Unreachable (sprintf "unexpected error: %s" ex.Message)
}

/// Backward-compatible entry point for `DashboardQueries.GetEvalStats`
/// (`Task<Affordances.EvalStats>`) — collapses `Unreachable` to `.empty` for
/// callers that only render a number. Anything that needs to tell "couldn't
/// look" from "nothing there" should call `getEvalStatsReadingFromWorker`
/// directly instead.
let getEvalStatsFromWorker
  (getProxy: WorkerProtocol.SessionId -> Threading.Tasks.Task<WorkerProtocol.SessionProxy option>)
  (sid: WorkerProtocol.SessionId)
  : Threading.Tasks.Task<Affordances.EvalStats> = task {
  let! reading = getEvalStatsReadingFromWorker getProxy sid
  return EvalStatsReading.toEvalStats reading
}

/// Create hot-reload proxy HTTP endpoints that forward to worker servers.
let createHotReloadProxyEndpoints
  (getWorkerBaseUrl: WorkerProtocol.SessionId -> string option)
  (httpClient: Net.Http.HttpClient)
  (stateChangedEvent: Event<SseEvent>)
  : HttpEndpoint list =
  let proxyToWorker (sidStr: string) (workerPath: string) (httpCall: string -> Threading.Tasks.Task<string * int * bool>) (ctx: HttpContext) = task {
    match WorkerProtocol.SessionId.validate sidStr with
    | Error _ ->
      ctx.Response.StatusCode <- 400
      do! ctx.Response.WriteAsJsonAsync({| error = "Invalid session ID" |})
    | Ok sid ->
    match getWorkerBaseUrl sid with
    | Some baseUrl ->
      try
        let url = sprintf "%s%s" baseUrl workerPath
        let! (respBody, statusCode, triggerChange) = httpCall url
        ctx.Response.ContentType <- "application/json"
        ctx.Response.StatusCode <- statusCode
        do! ctx.Response.WriteAsync(respBody)
        match triggerChange with
        | true -> stateChangedEvent.Trigger (HotReloadChanged sid)
        | false -> ()
      with ex ->
        // Log the detail server-side; never leak ex.Message (URLs, paths,
        // exception internals) into the client body.
        Log.warn "[hotReloadProxy] proxy to worker failed for %s%s: %s\n%s"
          (WorkerProtocol.SessionId.value sid) workerPath ex.Message
          (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        ctx.Response.StatusCode <- 502
        do! ctx.Response.WriteAsJsonAsync({| error = "Hot-reload proxy to the worker failed" |})
    | None ->
      ctx.Response.StatusCode <- 404
      do! ctx.Response.WriteAsJsonAsync({| error = "Session not found or not ready" |})
  }
  let proxyGet (sid: string) (workerPath: string) (ctx: HttpContext) =
    proxyToWorker sid workerPath (fun url -> task {
      use timeoutCts = new System.Threading.CancellationTokenSource(5000)
      use linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, timeoutCts.Token)
      let! resp = httpClient.GetStringAsync(url, linked.Token)
      return (resp, 200, false)
    }) ctx
  let proxyPost (sid: string) (workerPath: string) (ctx: HttpContext) =
    proxyToWorker sid workerPath (fun url -> task {
      use timeoutCts = new System.Threading.CancellationTokenSource(5000)
      use linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, timeoutCts.Token)
      // Guard against oversized payloads (hot-reload control messages are always < 1 KB)
      let maxBodyBytes = 1_048_576L  // 1 MB hard limit
      match ctx.Request.ContentLength with
      | contentLength when contentLength.HasValue && contentLength.Value > maxBodyBytes ->
        return (sprintf """{"error":"Request body too large (%d bytes, max 1 MB)"}""" contentLength.Value, 413, false)
      | _ ->
      use reader = new IO.StreamReader(ctx.Request.Body)
      let! body = reader.ReadToEndAsync(linked.Token)
      match int64 (System.Text.Encoding.UTF8.GetByteCount(body)) > maxBodyBytes with
      | true -> return ("""{"error":"Request body too large (max 1 MB)"}""", 413, false)
      | false ->
      use content = new Net.Http.StringContent(body, Text.Encoding.UTF8, "application/json")
      let! resp = httpClient.PostAsync(url, content, linked.Token)
      let! respBody = resp.Content.ReadAsStringAsync(linked.Token)
      return (respBody, int resp.StatusCode, resp.IsSuccessStatusCode)
    }) ctx
  let extractSid = Dashboard.routeValue "sid"
  let proxyGetRoute path = Dashboard.mapGetRaw (sprintf "/api/sessions/{sid}%s" path) extractSid (fun sid -> fun ctx -> proxyGet sid path ctx)
  let proxyPostRoute path = Dashboard.mapPostRaw (sprintf "/api/sessions/{sid}%s" path) extractSid (fun sid -> fun ctx -> proxyPost sid path ctx)
  [
    proxyGetRoute "/hotreload"
    proxyPostRoute "/hotreload/toggle"
    proxyPostRoute "/hotreload/reset-state"
    proxyPostRoute "/hotreload/reflection-mode"
    proxyPostRoute "/hotreload/watch-all"
    proxyPostRoute "/hotreload/unwatch-all"
    proxyPostRoute "/hotreload/watch-project"
    proxyPostRoute "/hotreload/unwatch-project"
    proxyPostRoute "/hotreload/watch-directory"
    proxyPostRoute "/hotreload/unwatch-directory"
    proxyGetRoute "/warmup-context"
  ]

/// Graceful shutdown: save caches, persist manifest, stop all workers.
let performGracefulShutdown
  (log: ILogger)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (getModel: unit -> SageFsModel)
  (sessionManager: MailboxProcessor<SessionManager.SessionCommand>)
  (manifestOwner: Features.ManifestOwner.Handle)
  = task {
  // W25(R12): Read snapshot ONCE — pass as value throughout to ensure a consistent view
  // across test-cache saves, event appends, and manifest merge. Multiple readSnapshot()
  // calls during shutdown can observe different state if sessions exit between calls.
  let snapshot = readSnapshot()
  // W40(R14): Read model ONCE before any async operations to prevent divergence.
  // The old code called getModel() at line 545 (for testState) and again at line 577
  // (for activeSessionId) — after the 5s event-append await. A session starting between
  // those two calls would cause activeSessionId to reference a session absent from snapshot.
  let model = getModel()
  let activeSessions = SessionManager.QuerySnapshot.allSessions snapshot
  // Save test cache for each unique project set
  let testState = model.LiveTesting.TestState
  let uniqueProjectSets =
    activeSessions
    |> List.map (fun s -> s.Projects)
    |> List.distinctBy (fun ps ->
      ps |> List.sort |> List.map (fun p -> p.Replace("\\", "/").ToLowerInvariant()) |> String.concat "|")
  for projects in uniqueProjectSets do
    match Features.DaemonPersistence.saveTestCache DaemonState.SageFsDir projects testState with
    | Ok path -> log.LogInformation("Saved test cache to {Path}", path)
    | Error err ->
      Instrumentation.persistenceSaveErrors.Add(
        1L, System.Collections.Generic.KeyValuePair("format", box "stc1"))
      log.LogWarning("Failed to save test cache: {Error}", err)

  // W29(R12): Event append to daemon stream removed — using binary manifest only for persistence.
  // Session stop events are recorded in the manifest instead.

  // Persist session manifest for binary-first resume
  // W4(R9) + W10(R10): Use mergeManifestWithExisting shared helper — loads existing manifest,
  // preserves stopped sessions' original StoppedAt, stamps active sessions as stopped now.
  // W23+W25(R12): Skip write on Error — returning active-only state would erase history.
  // W40(R14): activeSessionId derived from model read at function entry (not a second getModel call).
  let activeSessionId = model.Sessions.ActiveSessionId |> ActiveSession.sessionId |> Option.map WorkerProtocol.SessionId.value
  let now = DateTimeOffset.UtcNow
  // The owner applies this after every manifest change queued before it and
  // replies only once it is on disk, so shutdown awaits a durable manifest.
  // Once applied, the owner refuses live syncs: a late periodic save cannot
  // mark these sessions alive again.
  let shutdownSync = manifestOwner.Commit (liveSyncMutation snapshot activeSessionId (Some now))
  try
    let! committed = shutdownSync.WaitAsync(TimeSpan.FromSeconds 10.0)
    logManifestCommit log LogLevel.Information "Shutdown manifest save" committed
  with
  | :? TimeoutException ->
    log.LogWarning("Shutdown manifest save did not finish within 10s — this shutdown may not be recorded")
  | ex ->
    log.LogWarning("Shutdown manifest save failed: {Error}", ex.Message)

  // Stop all workers with a timeout
  let stopTask =
    sessionManager.PostAndAsyncReply(fun reply ->
      SessionManager.SessionCommand.StopAll reply)
    |> Async.StartAsTask
  let! stop_winner = System.Threading.Tasks.Task.WhenAny(stopTask, System.Threading.Tasks.Task.Delay(Timeouts.processNormalExit))
  match System.Object.ReferenceEquals(stop_winner, stopTask) with
  | false -> log.LogWarning("StopAll timed out — some workers may not have stopped cleanly")
  | true -> ()
}

/// Scans project source files with tree-sitter, then dispatches
/// locations and test cases to the Elm loop.
let private dispatchDiscoveredTests
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (workingDir: string)
  (log: ILogger)
  (dispatch: SageFsMsg -> unit)
  (sid: WorkerProtocol.SessionId)
  (tests: Features.LiveTesting.TestCase array)
  (providers: Features.LiveTesting.ProviderDescription list) =
  let sessionInfo = SessionManager.QuerySnapshot.tryGetSession sid (readSnapshot())
  let projectDirs =
    match sessionInfo with
    | Some info ->
      info.Projects
      |> List.map (fun proj ->
        let fullPath =
          match IO.Path.IsPathRooted proj with
          | true -> proj
          | false -> IO.Path.Combine(info.WorkingDirectory, proj)
        IO.Path.GetDirectoryName fullPath)
      |> List.distinct
    | None -> [ workingDir ]
  let locations =
    match Features.LiveTesting.TestTreeSitter.isAvailable () with
    | true ->
      projectDirs
      |> List.toArray
      |> Array.collect (fun dir ->
        match IO.Directory.Exists dir with
        | true ->
          // Pruned BEFORE descending (SafeDirectoryWalk) against the SAME
          // canonical noise list the dashboard/MCP discovery walks use
          // (bin/obj/.git/node_modules/... — this ad-hoc filter only ever
          // checked two of them), and cycle-proof: a directory symlink
          // loop under `dir` used to make `GetFiles(_, _, AllDirectories)`
          // never come back.
          let result = SafeDirectoryWalk.walkFiles dir (fun f -> f.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)) isNoiseProjectPath SafeDirectoryWalk.Bounds.standard
          if result.Truncated then
            log.LogWarning("[Daemon] Tree-sitter file walk in {Dir} hit its depth/entry bound — some test files may be missed", dir)
          result.Files
          |> List.toArray
          |> Array.collect (fun f ->
            try
              let code = IO.File.ReadAllText f
              Features.LiveTesting.TestTreeSitter.discover f code
            with ex ->
              log.LogWarning("[Daemon] Tree-sitter discovery failed for {File}: {Error}", f, ex.Message)
              Array.empty)
        | false -> Array.empty)
    | false -> Array.empty
  match Array.isEmpty locations with
  | false -> dispatch (SageFsMsg.Event (TuiEvent.TestLocationsDetected (WorkerProtocol.SessionId.value sid, locations)))
  | true -> ()
  // Zero tests is still an answer: it completes this session's discovery.
  dispatch (SageFsMsg.Event (TuiEvent.TestsDiscovered (WorkerProtocol.SessionId.value sid, tests)))
  match List.isEmpty providers with
  | false -> dispatch (SageFsMsg.Event (TuiEvent.ProvidersDetected providers))
  | true -> ()

/// Handle a worker's test discovery report from SessionManager → Elm model.
/// A failure reaches the model with its reason instead of only the log.
let handleTestDiscovery
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (workingDir: string)
  (log: ILogger)
  (dispatch: SageFsMsg -> unit)
  (sid: WorkerProtocol.SessionId)
  (report: SessionManager.TestDiscoveryReport) =
  match report with
  | SessionManager.TestDiscoveryReport.Discovered (tests, providers) ->
    dispatchDiscoveredTests readSnapshot workingDir log dispatch sid tests providers
  | SessionManager.TestDiscoveryReport.DiscoveryFailed reason ->
    let id = WorkerProtocol.SessionId.value sid
    log.LogWarning("[Daemon] Test discovery failed for {SessionId}: {Reason}", id, reason)
    dispatch (SageFsMsg.Event (TuiEvent.TestDiscoveryFailed (id, reason)))

/// Parse warmup progress string ("step/total msg") into structured fields.
let tryParseWarmupProgress (progress: string) =
  WarmupProgressLine.tryParsePayload progress

/// Parse warmup progress string ("step/total msg") and dispatch to Elm.
let handleWarmupProgress (dispatch: SageFsMsg -> unit) (_sid: string) (progress: string) =
  match tryParseWarmupProgress progress with
  | Some (step, total, msg) ->
    dispatch (SageFsMsg.Event (TuiEvent.WarmupProgress (step, total, msg)))
  | None -> ()

/// Periodic cache + manifest save callback.
/// Only writes when RunGeneration has advanced since last save.
let periodicCacheSave
  (log: ILogger)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (getModel: unit -> SageFsModel)
  (lastSavedGeneration: int ref) =
  try
    let model = getModel()
    let (Features.LiveTesting.RunGeneration gen) = model.LiveTesting.TestState.LastGeneration
    match gen > lastSavedGeneration.Value with
    | true ->
      let sw = System.Diagnostics.Stopwatch.StartNew()
      let activeSessions = SessionManager.QuerySnapshot.allSessions (readSnapshot())
      let uniqueProjectSets =
        activeSessions
        |> List.map (fun s -> s.Projects)
        |> List.distinctBy (fun ps ->
          ps |> List.sort |> List.map (fun p -> p.Replace("\\", "/").ToLowerInvariant()) |> String.concat "|")
      // W22(R11): Track per-project-set success. Only advance lastSavedGeneration if ALL saves
      // succeed — prevents suppressing a retry when one project set fails.
      let mutable allSavesSucceeded = true
      for projects in uniqueProjectSets do
        match Features.DaemonPersistence.saveTestCache DaemonState.SageFsDir projects model.LiveTesting.TestState with
        | Ok path -> log.LogDebug("Periodic cache save to {Path} (gen {Gen})", path, gen)
        | Error err ->
          allSavesSucceeded <- false
          Instrumentation.persistenceSaveErrors.Add(
            1L, System.Collections.Generic.KeyValuePair("format", box "stc1"))
          log.LogWarning("Periodic cache save failed: {Error}", err)
      sw.Stop()
      Instrumentation.cacheSaveCount.Add(1L)
      Instrumentation.cacheSaveMs.Record(
        sw.Elapsed.TotalMilliseconds,
        System.Collections.Generic.KeyValuePair("coverage_entries", box (int64 model.LiveTesting.TestState.TestCoverageBitmaps.Count)),
        System.Collections.Generic.KeyValuePair("result_entries", box (int64 model.LiveTesting.TestState.LastResults.Count)))
      // W11(R10): Volatile.Write ensures the store is visible across threads.
      // W22(R11): Only advance if all project-set saves succeeded — enables retry on next tick.
      if allSavesSucceeded then
        System.Threading.Volatile.Write(&lastSavedGeneration.contents, gen)
    | false -> ()
  with ex ->
    Instrumentation.periodicTaskErrors.Add(
      1L, System.Collections.Generic.KeyValuePair("task", box "cache_save"))
    log.LogWarning("Periodic cache save error: {Error}", ex.Message)

/// Periodic manifest save (binary session resume): queue a live sync on the
/// manifest owner. The timer never writes the file itself — the owner applies
/// the sync in order with every purge and forget, so neither can lose the other.
let periodicManifestSave
  (log: ILogger)
  (manifestOwner: Features.ManifestOwner.Handle)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (getModel: unit -> SageFsModel) =
  try
    // W40(R14): Read model ONCE before snapshot to prevent divergence.
    // If getModel() were called after readSnapshot(), a session starting/stopping between
    // the two calls could cause activeSessionId to reference a session absent from snapshot.
    let model = getModel()
    let activeSessionId = model.Sessions.ActiveSessionId |> ActiveSession.sessionId |> Option.map WorkerProtocol.SessionId.value
    // W10(R10): the sync keeps stopped sessions (live ones stay StoppedAt = None).
    // W23+W25(R12): one snapshot read; the owner skips the write on an unreadable manifest.
    let snapshot = readSnapshot()
    manifestOwner.Post(liveSyncMutation snapshot activeSessionId None, fun result ->
      match result with
      | Error (Features.ManifestOwner.CommitError.BaseUnreadable _) ->
        Instrumentation.periodicTaskErrors.Add(
          1L, System.Collections.Generic.KeyValuePair("task", box "manifest_read_error"))
      | _ -> ()
      logManifestCommit log LogLevel.Debug "Periodic manifest save" result)
  with ex ->
    Instrumentation.periodicTaskErrors.Add(
      1L, System.Collections.Generic.KeyValuePair("task", box "manifest_save"))
    log.LogWarning("Periodic manifest save error: {Error}", ex.Message)

/// Messages the LiveTestWatcherManager actor accepts: the pure
/// `LiveTestWatcherCore.Msg` protocol, plus a read-only query and teardown
/// that the pure core has no reason to know about. Kept out of
/// `LiveTestWatcherCore.Msg` so that type stays exactly the tested, proven
/// decision protocol.
type private ActorMsg =
  | Core of LiveTestWatcherCore.Msg
  | GetWatched of AsyncReplyChannel<string list>
  | Shutdown of AsyncReplyChannel<unit>

/// Manages per-session file watchers for live testing, as a single-owner
/// mailbox actor over the pure `LiveTestWatcherCore` decision core (roast-9
/// #10). Each session directory gets its own FileSystemWatcher. Watchers are
/// created when sessions are discovered and disposed when sessions are
/// removed.
///
/// All state (which dirs are claimed by which sessions, which paths are
/// pending a debounced drain, which dirs are actually watched) is owned
/// solely by one MailboxProcessor loop — no ConcurrentDictionary, no lock, no
/// epoch/generation counter. FileSystemWatcher callbacks and the debounce
/// timer only ever `Post` a message; they never read or write state
/// directly. See `LiveTestWatcherCore` for why this makes the old
/// stale-event race impossible by construction instead of guarded against.
type LiveTestWatcherManager
  ( dispatch: SageFsMsg -> unit,
    onFileReloaded: WorkerProtocol.SessionId -> string -> unit,
    fallbackDir: string option ) =
  // onFileReloaded: sessionId -> path -> unit. Carries the owning session so
  // downstream FileReloaded events are session-attributed (the isolation
  // blocker: two sessions can share one working dir; a path alone cannot
  // disambiguate which session's live-test state changed).
  //
  // fallbackDir: the daemon-CWD fallback watcher. It claims no session — files
  // under it fire FileContentChanged but never FileReloaded (a path with no
  // owning session must not be attributed to a fabricated one).

  let debounceMs = 75
  let normalizedFallback = fallbackDir |> Option.map System.IO.Path.GetFullPath
  let logger = Log.asILogger ()
  // 0 = not disposed, 1 = disposed. Guards against a second Dispose() call
  // hanging forever on PostAndReply — the actor loop does not recurse after
  // Shutdown, so nothing would ever answer a second reply channel.
  let mutable disposedFlag = 0

  let mailbox =
    MailboxProcessor<ActorMsg>.Start(fun inbox ->

      // Impure edge: the live FileSystemWatcher handles and the debounce
      // timer. Touched ONLY from inside this loop — the single owner of all
      // watcher state. Rooted in the loop's own closure (not a class field),
      // so nothing outside this actor can reach in and mutate it, and the
      // timer can never be GC'd out from under a live daemon (see memory:
      // daemon timer GC bug — a timer with no other root gets collected and
      // silently stops firing).
      // Value is an IDisposable, not a bare FileSystemWatcher: each claimed
      // directory is now watched by `SageFs.FileWatcher.startPrunedWatcher`
      // — one non-recursive watch per surviving subdirectory, with bin/obj/
      // .git/node_modules/.runs and nested checkouts pruned from the walk
      // instead of watched and filtered after the fact. A naive recursive
      // watch over a whole checkout is exactly how one daemon was measured
      // holding 148,077 inotify watches (a quarter of the system limit)
      // after every session watching it had already stopped.
      let watchers = System.Collections.Generic.Dictionary<string, System.IDisposable>()

      let handleFileChanged (directories: string list) (e: System.IO.FileSystemEventArgs) =
        let path = e.FullPath
        // A file inside another checkout nested under the watched directory (a
        // git worktree such as .claude/worktrees/*, a vendored repo) belongs
        // to that project — feeding it to this session's live testing
        // type-checked foreign copies of the session's own files. Pruning
        // already keeps a nested checkout from ever being walked, so this is
        // now defense in depth rather than the only guard.
        let inNestedCheckout =
          directories |> List.exists (fun root -> SageFs.FileWatcher.isInNestedCheckout root path SageFs.FileWatcher.hasCheckoutMarker)
        let watchedSource =
          SageFs.FileWatcher.shouldTriggerRebuild
            { Directories = directories; Extensions = [".fs"; ".fsx"]; ExcludePatterns = []; DebounceMs = debounceMs }
            path
        match watchedSource && not inNestedCheckout with
        | true -> inbox.Post (Core (LiveTestWatcherCore.FileSaved path))
        | false -> ()

      let startWatcher (dir: string) =
        match watchers.ContainsKey dir with
        | true -> ()
        | false ->
          // FileName too: editors that save safely (vim, JetBrains, sed -i)
          // write a temp file and rename it over the source, which is a
          // rename, not a write.
          let handler = fun (_kind: SageFs.FileWatcher.FileChangeKind) (e: System.IO.FileSystemEventArgs) -> handleFileChanged [dir] e
          let onOverflow (overflowDir: string) =
            Log.warn "[watcher] Buffer overflow watching %s for live testing — some file-save events under it may have been lost" overflowDir
          let disposable =
            SageFs.FileWatcher.startPrunedWatcher
              dir
              [ ".fs"; ".fsx" ]
              DevReload.DevReloadConfig.defaults.FileWatcherBufferSizeBytes
              handler
              onOverflow
          watchers.[dir] <- disposable
          Log.info "[watcher] Registered file watcher for %s" dir

      let stopWatcher (dir: string) =
        match watchers.TryGetValue dir with
        | true, disposable ->
          disposable.Dispose()
          watchers.Remove(dir) |> ignore
          Log.info "[watcher] Disposed file watcher for %s" dir
        | false, _ -> ()

      // One rooted debounce timer for the whole actor. Its callback only
      // ever posts DebounceElapsed — no state is touched on the timer thread.
      let debounceTimer =
        new System.Threading.Timer(
          System.Threading.TimerCallback(fun _ -> inbox.Post (Core LiveTestWatcherCore.DebounceElapsed)),
          null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)

      let drainPath (state: LiveTestWatcherCore.State) (path: string) =
        // FileContentChanged fires for any path under a currently-watched dir
        // — a claimed session dir OR the session-less fallback dir. That is the
        // fallback contract ("files under the fallback fire FileContentChanged
        // but never FileReloaded") and it is what triggers a rebuild/rerun. A
        // save queued before its dir was removed resolves to a dir no longer in
        // Watched and is dropped here (the old stale-event case). onFileReloaded
        // is attributed only to the sessions that actually claim the dir.
        match LiveTestWatcherCore.isUnderWatchedDir state.Watched path with
        | false -> ()
        | true ->
          try
            let fi = System.IO.FileInfo(path)
            match fi.Exists && fi.Length < 1_048_576L with
            | true ->
              let content = System.IO.File.ReadAllText(path)
              dispatch (SageFsMsg.FileContentChanged(path, content))
              for sessionId in LiveTestWatcherCore.sessionsForPath state.DirSessions path do
                onFileReloaded sessionId path
            | false -> ()
          with
          | :? System.IO.IOException -> ()
          | :? System.UnauthorizedAccessException -> ()

      /// Apply one pure-core message and run the effects it produces. This is
      /// the only place that mutates the impure edge (watchers dict, timer).
      let processCoreMessage (state: LiveTestWatcherCore.State) (msg: LiveTestWatcherCore.Msg) = async {
        let newState, effects = LiveTestWatcherCore.apply normalizedFallback state msg
        for effect in effects do
          match effect with
          | LiveTestWatcherCore.StartWatch dir -> startWatcher dir
          | LiveTestWatcherCore.StopWatch dir -> stopWatcher dir
          | LiveTestWatcherCore.ArmDebounce -> debounceTimer.Change(debounceMs, System.Threading.Timeout.Infinite) |> ignore
          | LiveTestWatcherCore.DrainPending paths -> for path in paths do drainPath newState path
        return newState
      }

      let resilientProcess = ResilientActor.wrapLoop logger "live-test-watcher" processCoreMessage

      let rec loop (state: LiveTestWatcherCore.State) = async {
        let! actorMsg = inbox.Receive()
        match actorMsg with
        | Core coreMsg ->
          let! newState = resilientProcess state coreMsg
          return! loop newState
        | GetWatched reply ->
          reply.Reply(state.Watched |> Set.toList)
          return! loop state
        | Shutdown reply ->
          // Teardown runs on the owner thread too — no race with an
          // in-flight StartWatch/StopWatch effect from an earlier message.
          debounceTimer.Dispose()
          for KeyValue(_, w) in watchers do
            w.Dispose()
          watchers.Clear()
          reply.Reply(())
          // Deliberately do not recurse — the actor stops processing here.
      }
      loop LiveTestWatcherCore.empty)

  /// Register a watcher for a directory, attributed to a session (idempotent).
  member _.AddDirectory(dir: string, sessionId: WorkerProtocol.SessionId) =
    match System.IO.Directory.Exists(dir) with
    | false -> ()
    | true -> mailbox.Post (Core (LiveTestWatcherCore.AddDirectory(System.IO.Path.GetFullPath dir, sessionId)))

  /// Remove one session's claim on a directory. The watcher is disposed only
  /// when nothing needs it anymore — a dir shared by two sessions keeps its
  /// watcher when one session stops.
  member _.RemoveDirectory(dir: string, sessionId: WorkerProtocol.SessionId) =
    mailbox.Post (Core (LiveTestWatcherCore.RemoveDirectory(System.IO.Path.GetFullPath dir, sessionId)))

  /// Sync watchers to match the current sessions. Each entry is
  /// (sessionId, workingDir). One dir may host several sessions; the watcher
  /// is created once and attributes reloads to every owning session.
  member _.SyncToSessions(sessions: (WorkerProtocol.SessionId * string) list) =
    let normalized = sessions |> List.map (fun (sessionId, dir) -> sessionId, System.IO.Path.GetFullPath dir)
    mailbox.Post (Core (LiveTestWatcherCore.SyncToSessions normalized))

  member _.WatchedDirectories = mailbox.PostAndReply(GetWatched)

  interface System.IDisposable with
    member _.Dispose() =
      match System.Threading.Interlocked.Exchange(&disposedFlag, 1) with
      | 1 -> () // already disposed — no-op, matches the original's idempotent Dispose
      | _ ->
        try mailbox.PostAndReply((fun reply -> Shutdown reply), timeout = 5000) |> ignore
        with :? System.TimeoutException -> Log.warn "[watcher] Shutdown timed out waiting for the mailbox"

/// Cohort claim early-warning (multi-agent vision §5.1): resolve which
/// cohort member (if any) should receive an `ObserveSave` advisory for a
/// file the watcher just saw saved inside `sessionId`'s working directory,
/// plus the path made repo-relative to that session's checkout root — the
/// same "overlap is path arithmetic" rule Phase 0's
/// `CrossCheckoutOverlap.repoRelative` already applies for the cross-checkout
/// advisory. Pure: no IO, no mailbox post — the caller (the watcher's
/// `onFileReloaded` callback) decides what to do with the result and must
/// never block on it. `None` when `sessionId` has no matching `SessionInfo`
/// (shouldn't happen — the caller already routed via `sessionsForPath`) or
/// when that session isn't bound to any cohort member (`MemberRecord.Session`
/// only ever matches when `join_cohort` recorded this session) — a solo,
/// non-cohort save is unchanged behavior either way.
let resolveSaveObserver
  (sessions: WorkerProtocol.SessionInfo list)
  (members: Map<MemberTable.MemberId, Cohort.MemberRecord>)
  (sessionId: WorkerProtocol.SessionId)
  (path: string)
  : (MemberTable.MemberId * string) option =
  let sidStr = WorkerProtocol.SessionId.value sessionId
  sessions
  |> List.tryFind (fun s -> WorkerProtocol.SessionId.value s.Id = sidStr)
  |> Option.bind (fun s -> CrossCheckoutOverlap.repoRelative s.WorkingDirectory path)
  |> Option.bind (fun relPath ->
    members
    |> Map.toList
    |> List.tryFind (fun (_, record) -> record.Session = Some sidStr)
    |> Option.map (fun (m, _) -> m, relPath))

/// Get previous sessions: active from CQRS snapshot + historical from binary manifest.
let getPreviousSessions
  (manifestOwner: Features.ManifestOwner.Handle)
  (readSnapshot: unit -> SessionManager.QuerySnapshot) = task {
  let snapshot = readSnapshot()
  let activeSessions =
    SessionManager.QuerySnapshot.allSessions snapshot
    |> List.map (fun (info: WorkerProtocol.SessionInfo) ->
      { PreviousSession.Id = WorkerProtocol.SessionId.value info.Id
        PreviousSession.WorkingDir = info.WorkingDirectory
        PreviousSession.Projects = info.Projects
        PreviousSession.LastSeen = info.LastActivity })
  let activeIds = activeSessions |> List.map (fun s -> s.Id) |> Set.ofList
  let! manifest = manifestOwner.Read()
  let historicalSessions =
    match manifest with
    | Ok daemonState ->
      daemonState.Sessions
      |> Map.values
      |> Seq.filter (fun r -> r.StoppedAt.IsSome && not (activeIds.Contains r.SessionId))
      |> Seq.map (fun r ->
        { PreviousSession.Id = r.SessionId
          PreviousSession.WorkingDir = r.WorkingDir
          PreviousSession.Projects = r.Projects
          PreviousSession.LastSeen = r.StoppedAt |> Option.map (fun t -> t.DateTime) |> Option.defaultValue r.CreatedAt.DateTime })
      |> Seq.toList
    | Error _ -> []
  return activeSessions @ historicalSessions
}

/// Start dashboard web server with Brotli compression.
/// How a timer's disposal ended: its in-flight callback finished, or it did not in time.
[<RequireQualifiedAccess>]
type TimerStop =
  | Joined
  | StillRunning

/// Disposes a timer and waits for any in-flight callback to finish.
let disposeTimerAndWait (timer: System.Threading.Timer) (timeout: TimeSpan) : Task<TimerStop> =
  task {
    // Timer.Dispose(WaitHandle) signals the kernel handle directly; a
    // ManualResetEventSlim's own Wait never observes that, so wait on a kernel event.
    let finished = new System.Threading.ManualResetEvent(false)
    match timer.Dispose(finished) with
    | false ->
      finished.Dispose()
      return TimerStop.Joined
    | true ->
      let! joined = Task.Run(fun () -> finished.WaitOne(timeout))
      match joined with
      | true ->
        finished.Dispose()
        return TimerStop.Joined
      | false ->
        // The timer may still signal the event later, so it stays undisposed.
        return TimerStop.StillRunning
  }

let startDashboardServer
  (log: ILogger)
  (bindHost: SageFs.SageFsConfig.LoopbackHost)
  (ownOrigins: SageFs.Server.HttpOriginGuard.OwnOrigins)
  (dashboardPort: int)
  (endpoints: HttpEndpoint list)
  (stopping: System.Threading.CancellationToken) = task {
  try
    let builder = WebApplication.CreateBuilder()
    builder.Logging
      .AddFilter("Microsoft.AspNetCore", LogLevel.Warning)
      .AddFilter("Microsoft.Hosting", LogLevel.Warning)
    |> ignore
    builder.Services.AddResponseCompression(fun opts ->
      opts.EnableForHttps <- true
      // The dashboard's repeated full-#main morph compresses extremely well
      // (highly repetitive markup) — payload economy belongs to the
      // transport, not to fragmenting the render into per-panel patches.
      // text/event-stream is NOT in ResponseCompressionDefaults.MimeTypes,
      // so it must be added explicitly for the SSE stream to compress at all.
      // A client that disconnects mid-stream can make the compression
      // middleware's own teardown log a warning for that one connection
      // (Kestrel isolates the failure to that connection; it does not affect
      // other sessions or the daemon process) — an acceptable, already-guarded
      // cost for compressing the dominant per-tick payload.
      opts.MimeTypes <- Seq.append ResponseCompressionDefaults.MimeTypes [ "text/event-stream" ]
      opts.Providers.Add<BrotliCompressionProvider>()
      opts.Providers.Add<GzipCompressionProvider>()
    ) |> ignore
    builder.Services.Configure<BrotliCompressionProviderOptions>(fun (opts: BrotliCompressionProviderOptions) ->
      opts.Level <- System.IO.Compression.CompressionLevel.Fastest
    ) |> ignore
    let app = builder.Build()
    app.Urls.Add(SageFs.SageFsConfig.LoopbackHost.listenUrl bindHost dashboardPort)
    app.UseResponseCompression() |> ignore
    // Origin/CSRF gate (see HttpOriginGuard): only this daemon's own pages and
    // local tooling reach the dashboard's mutating endpoints (eval, session
    // create/stop, shutdown).
    SageFs.Server.McpServer.useOriginGuard ownOrigins app
    app.UseRouting().UseFalco(endpoints) |> ignore
    log.LogInformation("Dashboard available at http://localhost:{Port}/dashboard", dashboardPort)
    do! McpServer.runUntilCancelled app stopping
  with ex ->
    log.LogWarning("Dashboard failed to start: {Error}", ex.Message)
}

/// Resume previous sessions from binary manifest.
/// Creates new sessions for each alive-but-deduplicated entry, or
/// starts bare if no previous sessions exist.
let resumePreviousSessions
  (infra: DaemonInfra)
  (sessionOps: SessionManagementOps)
  (manifestOwner: Features.ManifestOwner.Handle)
  (workingDir: string)
  (onSessionResumed: unit -> unit)
  = task {
  let log = infra.Log
  let startupSw = System.Diagnostics.Stopwatch.StartNew()
  let startupSpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.startup" []

  // Load session manifest from binary — the sole source of truth
  let binarySpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.binary_manifest_load" []
  let binarySw = System.Diagnostics.Stopwatch.StartNew()
  let! manifestResult = manifestOwner.Read()
  binarySw.Stop()
  match isNull binarySpan with
  | false -> binarySpan.SetTag("binary_load_ms", binarySw.Elapsed.TotalMilliseconds) |> ignore
  | true -> ()
  // W35(R14): a corrupt manifest is renamed aside — by its owner, the only
  // component that touches the file — so saves are not blocked for this run.
  let! quarantined =
    match manifestResult with
    | Error (Features.ManifestTypes.ManifestLoadError.CorruptData _) -> manifestOwner.QuarantineCorrupt()
    | _ -> System.Threading.Tasks.Task.FromResult false

  let daemonState =
    match manifestResult with
    | Ok state ->
      log.LogInformation("Loaded session manifest from binary ({Count} sessions, {Ms:F1}ms)",
        state.Sessions.Count, binarySw.Elapsed.TotalMilliseconds)
      match isNull binarySpan with
      | false -> binarySpan.SetTag("source", "binary") |> ignore
      | true -> ()
      Instrumentation.succeedSpan binarySpan
      state
    | Error Features.ManifestTypes.ManifestLoadError.NotFound ->
      // W32(R13): NotFound is an expected first-run condition → Info + succeedSpan.
      log.LogInformation("No binary manifest found — starting fresh")
      match isNull binarySpan with
      | false -> binarySpan.SetTag("source", "none") |> ignore
      | true -> ()
      Instrumentation.succeedSpan binarySpan
      Features.DaemonManifest.DaemonManifestState.empty
    | Error (Features.ManifestTypes.ManifestLoadError.IoError err) ->
      // W32(R13): File EXISTS but can't be read (lock/permissions) → Warning + failSpan.
      // Old code used LogInformation+succeedSpan for ALL error cases — wrong severity.
      log.LogWarning("Binary manifest unreadable — starting fresh (HISTORY NOT RESTORED): {Error}", err)
      match isNull binarySpan with
      | false -> binarySpan.SetTag("source", "error_io") |> ignore
      | true -> ()
      Instrumentation.failSpan binarySpan err
      Features.DaemonManifest.DaemonManifestState.empty
    | Error (Features.ManifestTypes.ManifestLoadError.CorruptData err) ->
      // W32(R13): File is permanently corrupt → Error + failSpan.
      // W35(R14): Rename the corrupt file so periodic saves are unblocked for this run.
      // Without rename: mergeManifestWithExisting reads daemon.sagefm → CorruptData → Error →
      // skips write → ALL new sessions lost for the daemon's entire lifetime.
      // IoError is NOT renamed (transient lock; file may recover on its own).
      log.LogError("Binary manifest corrupt — starting fresh (HISTORY NOT RESTORED): {Error}", err)
      match quarantined with
      | true -> log.LogWarning("Corrupt manifest renamed — periodic saves unblocked for this run")
      | false -> log.LogWarning("Could not rename corrupt manifest — periodic saves may be blocked")
      match isNull binarySpan with
      | false -> binarySpan.SetTag("source", "error_corrupt") |> ignore
      | true -> ()
      Instrumentation.failSpan binarySpan err
      Features.DaemonManifest.DaemonManifestState.empty

  let aliveSessions = Features.DaemonManifest.DaemonManifestState.aliveSessions daemonState

  match aliveSessions.IsEmpty with
  | false ->
    // Dedup phase
    let dedupSpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.session_dedup" []
    // Deduplicate by working directory + projects — resume one session per (dir, projects) pair
    let uniqueByDir =
      aliveSessions
      |> List.groupBy (fun r -> r.WorkingDir, r.Projects |> List.sort)
      |> List.map (fun (_, group) ->
        // Pick the most recently created session for each (dir, projects) pair
        group |> List.maxBy (fun r -> r.CreatedAt))
    // Mark all stale duplicates as stopped
    let staleIds =
      aliveSessions
      |> List.map (fun r -> r.SessionId)
      |> Set.ofList
    let keptIds =
      uniqueByDir |> List.map (fun r -> r.SessionId) |> Set.ofList
    let prunedCount = (Set.difference staleIds keptIds).Count
    // W14(R10): Session dedup stop-event persistence removed — binary manifest is sole source of truth.
    // Pruned sessions are no longer recorded as events.
    match prunedCount > 0 with
    | true -> Instrumentation.daemonDuplicatesPruned.Add(int64 prunedCount)
    | false -> ()
    match isNull dedupSpan with
    | false ->
      dedupSpan.SetTag("alive_count", aliveSessions.Length) |> ignore
      dedupSpan.SetTag("dedup_removed", prunedCount) |> ignore
    | true -> ()
    Instrumentation.succeedSpan dedupSpan

    log.LogInformation("Resuming {Count} previous session(s) ({Stale} stale duplicates cleaned)",
      uniqueByDir.Length, (aliveSessions.Length - uniqueByDir.Length))
    // A session whose directory or every project is gone can never start again:
    // forget it once rather than retrying (and warning) on every start. One
    // deleted project among several drops just that project.
    let decisions =
      uniqueByDir
      |> List.map (fun prev ->
        prev, Features.DaemonManifest.ResumeDecision.decide IO.Directory.Exists IO.File.Exists prev)
    for prev, decision in decisions do
      match decision with
      | Features.DaemonManifest.ResumeDecision.Forget reason ->
        log.LogWarning("Forgetting session {SessionId} for {WorkingDir}: {Reason}", prev.SessionId, prev.WorkingDir, reason)
      | Features.DaemonManifest.ResumeDecision.Resume projects when projects.Length < prev.Projects.Length ->
        let dropped = prev.Projects |> List.filter (fun p -> not (List.contains p projects))
        log.LogWarning("Resuming session for {WorkingDir} without deleted project(s): {Dropped}", prev.WorkingDir, String.concat ", " dropped)
      | Features.DaemonManifest.ResumeDecision.Resume _ -> ()
    let! forgotten =
      decisions
      |> List.choose (fun (prev, decision) ->
        match decision with
        | Features.DaemonManifest.ResumeDecision.Forget _ -> Some prev.SessionId
        | Features.DaemonManifest.ResumeDecision.Resume _ -> None)
      |> List.map (fun sessionId -> task {
        let! committed = manifestOwner.Commit (Features.DaemonManifest.ManifestMutation.Remove sessionId)
        return sessionId, committed })
      |> System.Threading.Tasks.Task.WhenAll
    for sessionId, committed in forgotten do
      match committed with
      | Ok _ -> ()
      | Error err ->
        log.LogWarning("Could not remove session {SessionId} from the manifest: {Error}", sessionId, Features.ManifestOwner.CommitError.describe err)
    let relevant =
      decisions
      |> List.choose (fun (prev, decision) ->
        match decision with
        | Features.DaemonManifest.ResumeDecision.Resume projects -> Some { prev with Projects = projects }
        | Features.DaemonManifest.ResumeDecision.Forget _ -> None)
    // Resume all valid sessions in parallel — each is an independent worker process
    let resumeSpan = Instrumentation.startSpan Instrumentation.sessionSource "sagefs.daemon.session_resume" []
    let resumeTasks =
      relevant
      |> List.map (fun prev -> task {
        log.LogInformation("Resuming session for {WorkingDir}", prev.WorkingDir)
        let! result = sessionOps.CreateSession prev.Projects prev.WorkingDir WorkflowTypes.SessionWorkflow.Interactive
        match result with
        | Ok info ->
          Instrumentation.daemonSessionsResumed.Add(1L)
          // W32(R13): Stop-event persistence removed — no longer tracking session stop in events.
          // Binary manifest is the sole source of truth for session state.
          log.LogInformation("Resumed session {Info} (retired old id {OldSessionId})", info, prev.SessionId)
          onSessionResumed ()
        | Error err ->
          log.LogWarning("Failed to resume session for {WorkingDir}: {Error}", prev.WorkingDir, err)
      })
    do! System.Threading.Tasks.Task.WhenAll(resumeTasks) :> System.Threading.Tasks.Task
    match isNull resumeSpan with
    | false -> resumeSpan.SetTag("resumed_count", relevant.Length) |> ignore
    | true -> ()
    Instrumentation.succeedSpan resumeSpan

    // Sessions restored — clients will discover them via listing
    // No global "active session" to restore; each client picks its own
    match daemonState.ActiveSessionId with
    | Some _ -> () // Previously tracked active session — clients resolve on connect
    | None -> ()
  | true ->
    log.LogInformation("No previous sessions to resume. Waiting for clients to create sessions")

  startupSw.Stop()
  Instrumentation.daemonStartupMs.Record(startupSw.Elapsed.TotalMilliseconds)
  Instrumentation.succeedSpan startupSpan
}

/// Create the Elm runtime with warmup context, streaming test proxy, and SSE dedup.
let createElmRuntime
  (sessionManager: MailboxProcessor<SessionManager.SessionCommand>)
  (readSnapshot: unit -> SessionManager.QuerySnapshot)
  (httpClient: System.Net.Http.HttpClient)
  (stateChangedEvent: Event<SseEvent>)
  (watcherManagerRef: LiveTestWatcherManager option ref)
  (onModel: SageFsModel -> unit)
  (ct: System.Threading.CancellationToken) =
  let mutable lastStateJson = ""
  let mutable lastLoggedOutputCount = 0
  let mutable lastLoggedDiagCount = 0
  let getWarmupContextForElm (sessionId: string) : Async<SessionContext option> =
    async {
      try
        let snapshot = readSnapshot()
        match WorkerProtocol.SessionId.validate sessionId with
        | Error _ -> return None
        | Ok sidTyped ->
        match Map.tryFind sidTyped snapshot.WorkerBaseUrls with
        | Some url when url.Length > 0 ->
          use timeoutCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(5.0))
          use linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
          let! resp =
            httpClient.GetStringAsync(System.Uri(sprintf "%s/warmup-context" url), linkedCts.Token)
            |> Async.AwaitTask
          let warmup = WorkerProtocol.Serialization.deserialize<WarmupContext> resp
          let sessions = SessionManager.QuerySnapshot.allSessions snapshot
          let info = sessions |> List.tryFind (fun si -> WorkerProtocol.SessionId.value si.Id = sessionId)
          return Some {
            SessionId = sessionId
            ProjectNames =
              info |> Option.map (fun i -> i.Projects) |> Option.defaultValue []
            WorkingDir =
              info |> Option.map (fun i -> i.WorkingDirectory)
              |> Option.defaultValue ""
            Status =
              info |> Option.map (fun i -> sprintf "%A" i.Status)
              |> Option.defaultValue "Unknown"
            Warmup = warmup
            FileStatuses = []
            Workflow = WorkflowTypes.SessionWorkflow.Interactive
            AutoOpenNamespaces = DirectoryConfig.autoOpenNamespacesForDirectory (info |> Option.map (fun i -> i.WorkingDirectory) |> Option.defaultValue "")
          }
        | _ -> return None
      with
      | :? System.IO.IOException as ex ->
        Log.error "[getWarmupContextForElm] IO error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        return None
      | :? System.Net.Http.HttpRequestException as ex ->
        Log.error "[getWarmupContextForElm] HTTP error: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        return None
      | :? System.Threading.Tasks.TaskCanceledException ->
        return None
      | ex ->
        Log.error "[getWarmupContextForElm] Unexpected: %s (%s)\n%s" ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        return None
    }
  let configureWarmupAutoOpen workingDir =
    let now = DateTime.UtcNow
    let mkLine kind text = {
      Kind = kind
      Text = text
      Timestamp = now
      SessionId = ""
    }

    match DirectoryConfig.ensureAutoOpenNamespacesOptOut workingDir with
    | Ok (AutoOpenNamespacesOptOutResult.Created path) ->
      Ok (mkLine OutputKind.System (sprintf "Disabled warmup auto-open for %s (%s)" workingDir path))
    | Ok (AutoOpenNamespacesOptOutResult.AlreadyDisabled path) ->
      Ok (mkLine OutputKind.System (sprintf "Warmup auto-open is already disabled for %s (%s)" workingDir path))
    | Ok (AutoOpenNamespacesOptOutResult.RequiresManualEdit path) ->
      Ok (mkLine OutputKind.System (sprintf "Existing config needs manual edit to disable warmup auto-open: %s" path))
    | Error err ->
      Error err

  let effectDeps =
    { ElmDaemon.createEffectDeps sessionManager readSnapshot DirectoryConfig.autoOpenNamespacesForDirectory configureWarmupAutoOpen with
        GetWarmupContext = Some (fun sid -> getWarmupContextForElm (WorkerProtocol.SessionId.value sid))
        GetStreamingTestProxy = fun sid ->
          let snapshot = readSnapshot()
          match Map.tryFind sid snapshot.WorkerBaseUrls with
          | Some url when url.Length > 0 ->
            Some (HttpWorkerClient.streamingTestProxyWithCoverage Timeouts.workerHttpRead url)
          | _ -> None
        RegisterFileWatcher = fun sessionIdStr directory ->
          match !watcherManagerRef with
          | Some mgr ->
            // The effect carries a raw session-ID string (Elm model type);
            // the watcher manager attributes by typed SessionId. Session IDs
            // originate from validated snapshot state, so validate() is
            // expected to succeed.
            match WorkerProtocol.SessionId.validate sessionIdStr with
            | Ok sid -> mgr.AddDirectory(directory, sid)
            | Error _ -> Log.warn "[watcher] RegisterFileWatcher with invalid session id '%s'" sessionIdStr
          | None -> ()
        DisposeFileWatcher = fun sessionIdStr directory ->
          match !watcherManagerRef with
          | Some mgr ->
            match WorkerProtocol.SessionId.validate sessionIdStr with
            | Ok sid -> mgr.RemoveDirectory(directory, sid)
            | Error _ -> Log.warn "[watcher] DisposeFileWatcher with invalid session id '%s'" sessionIdStr
          | None -> () }
  let runtime =
    ElmDaemon.startHeadless
      effectDeps
      (fun model _regions ->
        (try onModel model
         with ex -> Log.warn "[elm] model hook threw: %s" ex.Message)
        let activeBuf = model.RecentOutput.GetActiveBuffer(model.Sessions.ActiveSessionId)
        let outputCount = activeBuf.Count
        let diagCount =
          model.Diagnostics |> Map.values |> Seq.sumBy List.length
        try
          let json = SseDedupKey.fromModel model
          match json <> lastStateJson with
          | true ->
            lastStateJson <- json
            // DIAGNOSTIC: log every state change propagation
            Log.info "[elm-OnModelChanged] output=%d diags=%d json.Length=%d" outputCount diagCount json.Length
            let significantOutputChange = abs (outputCount - lastLoggedOutputCount) >= 50
            let diagChanged = diagCount <> lastLoggedDiagCount
            match significantOutputChange || diagChanged with
            | true ->
              lastLoggedOutputCount <- outputCount
              lastLoggedDiagCount <- diagCount
              let latest =
                match activeBuf.IsEmpty with
                | true -> ""
                | false -> activeBuf.[0].Text
              Log.info "[elm] output=%d diags=%d | %s"
                outputCount diagCount latest
            | false -> ()
            // Eval-to-pixel latency chain, stage 3/5 (vision §3.4, §7.4):
            // the Elm model changed and is about to notify subscribers
            // (dashboard push agents, MCP push). Stamped here rather than
            // inside the thread-pool work item so it reflects when the
            // change was decided, not when the work item happened to run.
            EvalLatencyTrace.shared.StampModelChanged()
            System.Threading.ThreadPool.QueueUserWorkItem(fun _ ->
              stateChangedEvent.Trigger (ModelChanged (outputCount, diagCount))) |> ignore
          | false -> ()
        with ex -> Log.error "[elm] State change propagation error: %s (%s)\n%s" ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue ""))
      (fun phase msg ->
        Log.warn "[elm] 🚨 System alarm [%s]: %s" phase msg
        stateChangedEvent.Trigger (SystemAlarm (phase, msg)))
      ct
  runtime

/// Dispatch an Elm output event and wait until the target session's output
/// buffer has committed. The daemon state event is raised by OnModelChanged
/// after the model update, so this avoids returning a dashboard action while
/// its long-lived SSE stream still sees the previous output snapshot.
let dispatchOutputAndWait
  (elmRuntime: ElmRuntime<SageFsModel, SageFsMsg, RenderRegion>)
  (stateChanged: IEvent<SseEvent>)
  (sessionId: string)
  (message: SageFsMsg)
  = task {
    let beforeVersion = elmRuntime.GetModel().RecentOutput.GetBuffer(sessionId).Version
    let committed = System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
    use _subscription =
      stateChanged.Subscribe(fun _ ->
        let afterVersion = elmRuntime.GetModel().RecentOutput.GetBuffer(sessionId).Version
        match afterVersion > beforeVersion with
        | true -> committed.TrySetResult(true) |> ignore
        | false -> ())
    elmRuntime.Dispatch message
    let! completed = System.Threading.Tasks.Task.WhenAny(committed.Task, System.Threading.Tasks.Task.Delay(2000))
    return obj.ReferenceEquals(completed, committed.Task) && committed.Task.Result
  }

/// Which required listener(s) never stayed up during the startup window. The
/// MCP host and the dashboard host each run until the daemon's own
/// `stopping` token is cancelled (`runUntilCancelled`/`startDashboardServer`)
/// — completing on their own this early can only mean their bind failed.
/// Both catch and LOG their own bind exception rather than letting it
/// propagate (`startMcpServer`, `startDashboardServer`), so a faulted `Task`
/// never happens here: `IsCompleted` this early is the only signal there is.
[<RequireQualifiedAccess>]
type ListenerBindFailure =
  | Mcp
  | Dashboard
  | Both

/// Pure classification of the two host tasks' completion state at the end of
/// the startup window. `None` is the healthy case: neither task has ever
/// returned, because neither one is supposed to until shutdown.
let listenerBindFailureOf (mcpCompletedEarly: bool) (dashboardCompletedEarly: bool) : ListenerBindFailure option =
  match mcpCompletedEarly, dashboardCompletedEarly with
  | false, false -> None
  | true, true -> Some ListenerBindFailure.Both
  | true, false -> Some ListenerBindFailure.Mcp
  | false, true -> Some ListenerBindFailure.Dashboard

let describeListenerBindFailure =
  function
  | ListenerBindFailure.Mcp -> "the MCP listener"
  | ListenerBindFailure.Dashboard -> "the dashboard listener"
  | ListenerBindFailure.Both -> "the MCP and dashboard listeners"

/// Exit code for a startup that never got both required listeners up. Kept
/// distinct from a bare `1` so log-reading tooling can name this failure mode
/// specifically; the daemon must never announce "ready" once this fires.
let listenerBindFailureExitCode = 1

/// Run SageFs as a headless daemon.
/// MCP server + SessionManager + Dashboard — all frontends are clients.
/// Every session is a worker sub-process managed by SessionManager.
///
/// `ownership` (multi-agent vision §3.1, §10 item 2) is this daemon's own
/// ownership: an `--owner-pid` watchdog that exits this daemon when its
/// owner process is gone, and/or a `--ttl` that self-terminates it once
/// idle — closing the "34 orphaned daemons" seam (S1) for daemons an
/// agent, test, or demo runner spawns directly (not via a supervised
/// worker, which already has `ParentMonitor`/`OwnerMonitor`).
let run
  (bindHost: SageFs.SageFsConfig.LoopbackHost)
  (mcpPort: int)
  (flags: Args.DaemonFlags)
  (ownership: DaemonOwnership.EffectiveOwnership)
  = task {
  let startupSw = System.Diagnostics.Stopwatch.StartNew()
  let daemonStartTime = System.DateTimeOffset.UtcNow
  let startupSpan =
    Instrumentation.startSpan Instrumentation.daemonSource "sagefs.daemon.startup" [
      "daemon.port", box mcpPort
    ]
  let version = DaemonInfo.version
  let infra =
    Instrumentation.traced Instrumentation.daemonSource "sagefs.daemon.infra_create" [] (fun () ->
      createDaemonInfrastructure ())
  let log = infra.Log
  let httpClient = infra.HttpClient
  let frictionStore = infra.FrictionStore
  let daemonStreamId = infra.DaemonStreamId
  let mcpFetchTimeoutSec = infra.McpFetchTimeoutSec
  let dashboardFetchTimeoutSec = infra.DashboardFetchTimeoutSec
  let stateChangedEvent = infra.StateChangedEvent

  log.LogInformation("SageFs daemon v{Version} starting on port {Port}", version, mcpPort)

  // Handle --prune: mark all alive sessions as stopped and exit
  // W36+W42(R14): handlePrune now returns Result<bool,string> and takes Task-returning checkFn.
  // Ok true = pruned/exit, Ok false = not-requested/continue, Error msg = failed/exit with error.
  let! pruneResult = handlePrune DaemonState.SageFsDir infra.Log (fun () -> DaemonState.readAsync() |> Async.StartAsTask) flags
  match pruneResult with
  | Result.Ok true -> return ()
  | Result.Ok false -> ()
  | Result.Error msg ->
    infra.Log.LogError("Prune failed: {Error}", msg)
    return ()

  // The single owner of daemon.sagefm: every manifest write in this daemon goes through it.
  use manifestOwner = Features.ManifestOwner.start (Log.asILogger ()) DaemonState.SageFsDir

  use cts = infra.Cts

  // Ownership rule 2 (§3.1): an externally-spawned daemon with --owner-pid
  // exits when that process is gone — fenced on (pid, startTime) when
  // --owner-start was also given, so a recycled pid can never keep this
  // daemon alive forever. Same mechanism as the worker watchdog, one layer up.
  match ownership.OwnerPid with
  | Some ownerPid ->
    let owner : OwnerMonitor.Owner = { Pid = ownerPid; StartTimeTicks = ownership.OwnerStartTicks }
    log.LogInformation("Daemon monitoring owner PID {OwnerPid} (fenced={Fenced})", ownerPid, Option.isSome ownership.OwnerStartTicks)
    Async.Start(
      OwnerMonitor.run OwnerMonitor.getProcessById owner cts (fun msg -> log.LogWarning("{Message}", msg)),
      cts.Token)
  | None -> ()
  // Test discovery callback — set after elmRuntime is created
  let mutable onTestDiscoveryCallback : (WorkerProtocol.SessionId -> SessionManager.TestDiscoveryReport -> unit) =
    fun _ _ -> ()
  let mutable onInstrumentationMapsCallback : (WorkerProtocol.SessionId -> Features.LiveTesting.InstrumentationMap array -> unit) =
    fun _ _ -> ()
  let mutable onWarmupProgressCallback : (string -> string -> unit) =
    fun _ _ -> ()
  // #82: assigned after elmRuntime exists (below) — routes a run_app'd app's
  // stdout lines to the session output panel via TuiEvent.OutputEmitted.
  let mutable onAppOutputCallback : (string -> string -> unit) =
    fun _ _ -> ()
  // Assigned after elmRuntime exists (below): when a session becomes ready,
  // auto-enable live testing if its workflow is LiveTesting, so a LiveTesting
  // session re-runs the affected tests as you type (debounced keystrokes) from
  // the moment it is ready, without a manual enable toggle.
  let mutable onSessionReadyExtra : (WorkerProtocol.SessionId -> unit) =
    fun _ -> ()

  // Create SessionManager — the single source of truth for all sessions
  // Returns (mailbox, readSnapshot) — CQRS: reads go to snapshot, writes to mailbox
  let sessionManager, readSnapshot =
    SessionManager.create cts.Token
      (fun () -> stateChangedEvent.Trigger SessionProgress)
      (fun sid report -> onTestDiscoveryCallback sid report)
      (fun sid maps -> onInstrumentationMapsCallback sid maps)
      (fun sid -> stateChangedEvent.Trigger (SessionReady sid); onSessionReadyExtra sid)
      (fun sid progress -> onWarmupProgressCallback (WorkerProtocol.SessionId.value sid) progress)
      (fun sid error -> stateChangedEvent.Trigger (SessionFaulted (sid, error)))
      (fun sid line -> onAppOutputCallback (WorkerProtocol.SessionId.value sid) line)

  let sessionOps = createSessionOps sessionManager readSnapshot manifestOwner
  // String-to-SessionId adapters for proxyToSession (which takes string callbacks)
  let getProxyStr s = sessionOps.GetProxy (toSessionId s)
  let notifyWorkerDiedStr s = sessionOps.NotifyWorkerDied (toSessionId s)

  let noResume = flags.NoResume

  let workingDir = Environment.CurrentDirectory

  // resumeSessions delegates to module-level function with captured infra
  let resumeSessions onSessionResumed =
    resumePreviousSessions infra sessionOps manifestOwner workingDir onSessionResumed

  // Create EffectDeps from SessionManager + start Elm loop
  let watcherManagerRef = ref (None: LiveTestWatcherManager option)
  // Wakes the idle live-testing tick when a model change queues a debounce;
  // assigned once the tick timer exists (below).
  let wakeLiveTestTick : (SageFsModel -> unit) ref = ref ignore
  let elmRuntime = createElmRuntime sessionManager readSnapshot httpClient stateChangedEvent watcherManagerRef (fun model -> wakeLiveTestTick.Value model) cts.Token

  // #82: route a run_app'd app's stdout into that session's output panel via the
  // existing OutputEmitted append path — BATCHED (#88). A continuous app (a
  // console ticker, a 60fps game) emits far faster than the dashboard needs to
  // repaint, and one model change per line churns the daemon heap into
  // multi-GB RSS. So coalesce lines per session and flush at most every 150ms as
  // a single OutputEmitted — the model-change rate is bounded regardless of
  // output rate. Kind = Info (not Result) so app output never triggers the
  // binding-scope rebuild, which parses only Result output for `val` bindings.
  let appOutputGate = obj ()
  let appOutputPending = System.Collections.Generic.Dictionary<string, System.Text.StringBuilder>()
  let flushAppOutput () =
    let toFlush =
      lock appOutputGate (fun () ->
        let items = [ for kv in appOutputPending -> kv.Key, kv.Value.ToString() ]
        appOutputPending.Clear()
        items)
    for (sidStr, text) in toFlush do
      let trimmed = text.TrimEnd('\n')
      if trimmed.Length > 0 then
        elmRuntime.Dispatch(
          SageFsMsg.Event(
            TuiEvent.OutputEmitted
              { Kind = OutputKind.Info
                Text = trimmed
                Timestamp = System.DateTime.UtcNow
                SessionId = sidStr }))
  // Persistent periodic flusher (every 150ms; a no-op when nothing is pending).
  // MUST be rooted for the daemon's lifetime: a `System.Threading.Timer` whose
  // only reference is an unused local is collected (the task state machine never
  // captures it), silently stopping the flush. It is kept alive by its disposal
  // at graceful shutdown below (mirrors activityCleanupTimer/cohortReaperTimer).
  let appOutputFlushTimer =
    new System.Threading.Timer(
      (fun _ -> try flushAppOutput () with ex -> Log.warn "[app-output] flush failed: %s" ex.Message),
      null, 150, 150)
  onAppOutputCallback <-
    fun sidStr line ->
      lock appOutputGate (fun () ->
        match appOutputPending.TryGetValue sidStr with
        | true, sb -> sb.Append(line: string).Append('\n') |> ignore
        | false, _ ->
          let sb = System.Text.StringBuilder()
          sb.Append(line: string).Append('\n') |> ignore
          appOutputPending.[sidStr] <- sb)

  // The single owner of this daemon's implicit cohort (cohort-integration-plan.md
  // Slice 2, D1/D2/D4/D5): holds the live CohortState, appends every applied
  // command to a SQLite ledger, and publishes the projected CohortFrame
  // wait-free for reads (Claims v1 MCP tools, McpTools.fs). Constructed here,
  // AFTER `elmRuntime` (not alongside `manifestOwner` above), because item
  // 13c's `getSessionTestOutcomes` closure reads `elmRuntime.GetModel()` —
  // there is nothing to close over any earlier in this function.
  //
  // item 13a left this hardcoded to `[||]`/no outcomes because no
  // session->member mapping existed. Item 13c closed that gap: `join_cohort`
  // (Mcp.fs) now resolves and records the caller's session on `MemberRecord`,
  // so `CohortOwner.frameOf` can build its own `SessionSnapshot[]` straight
  // off `head.State.Members` — this closure only needs to answer "what are
  // THIS session's Pass/Fail/Stale tests and generation", the exact question
  // `Features.CohortTestProjection.projectSession` (item 13a) already
  // answers from a `LiveTestState`, filtered the SAME way
  // `GetSessionTestSummary` below filters it
  // (`LiveTestState.statusEntriesForSession`).
  let getCohortSessionTestOutcomes (sessionId: string) : Features.CohortOwner.SessionTestOutcomes =
    let state = (SageFsModel.cycleForSession sessionId (elmRuntime.GetModel())).TestState
    let projection, generation = Features.CohortTestProjection.projectSession sessionId state
    projection.PassingTests, projection.FailingTests, projection.StaleTests, generation

  // The real landing performer (item 14c — this is the slice that makes
  // request_landing actually run git and tests instead of parking forever
  // in Rebasing/Verifying against `LandingPerformer.stub`). Every field
  // reads the daemon-held integration binding
  // (`McpTools.cohortIntegrationRef`, written by the `set_integration_ref`
  // MCP tool) fresh on every call, so a landing dispatched before
  // `set_integration_ref` has ever run — or after a restart, since the
  // binding is process-local, see its own doc comment — fails closed with
  // an explanatory reason instead of silently doing nothing.
  let cohortToLiveTestId (Cohort.TestId t) : Features.LiveTesting.TestId =
    Features.LiveTesting.TestId.TestId t

  let cohortIntegrationNotConfigured () : string list =
    [ "integration not configured — call set_integration_ref first" ]

  // The performer is honest: `Rebase` returns the REAL rebased head and
  // `FastForward` targets it. `Cohort.decide`'s `LandingState.Verifying` now
  // carries `base` and `rebasedHead` separately, so its land-time HeadMoved
  // guard compares the BASE against IntegrationHead (Property 11) while
  // FastForward targets the rebased head — no echo/re-read workaround needed.
  // Daemon-held content-addressed test-result cache for landing verification
  // (§5.4), owned by `Features.CohortOwner.LandingCacheOwner` (roast-6 #7a) —
  // a single mailbox, not a shared `ref` read-modify-written by the RunTests
  // performer below. The old `ref` was safe only because v1's landing queue
  // is strictly serial (CohortOwner runs one landing's effects at a time);
  // this makes that correctness structural instead of resting on the
  // invariant holding forever. A test is cached only when its coverage gives
  // a trustworthy InputHash (see the RunTests wiring); a same-input test on
  // a later landing is then skipped instead of re-run.
  use cohortLandingCacheOwner = Features.CohortOwner.LandingCacheOwner.start (Log.asILogger ())

  // (Gap 3) A rebase rewrites the integration worktree's files, so the session's
  // own file watcher fires a rebuild and the session re-enters warmup. Reading
  // its discovery (ComputeAffected) or running tests in it (RunTests) DURING
  // that rebuild is wrong both ways: at best `SessionTrust.classify` returns
  // `WarmingUp` and the run is refused; at worst discovery is transiently empty
  // and a real change looks like it affects no tests (a fail-OPEN land). So both
  // performer steps wait here for the session to settle into a trustworthy
  // (Ready) state before they read it. Bounded: on timeout the caller signals
  // `VerificationInconclusive` — never a false pass, never a permanent queue jam.
  // Runs on the effect worker (Async.Start'd off the CohortOwner mailbox), so a
  // long wait here never blocks the single writer.
  let integrationSettleTimeout = Timeouts.cohortIntegrationSettle
  let awaitIntegrationSessionTrusted (sessionId: string) : Async<Result<Features.Verification.SessionTrust.SessionObservation, string>> =
    async {
      let observe () =
        async {
          let! sessionInfo = sessionOps.GetSessionInfo(toSessionId sessionId) |> Async.AwaitTask
          return
            ({ MatchingSessionIds = [ sessionId ]
               SessionStatus = sessionInfo |> Option.map (fun s -> s.Status)
               LoadedState = None
               TypeIdentityDiagnostic = None } : Features.Verification.SessionTrust.SessionObservation)
        }
      // Event-driven + location-transparent: PARK on the SessionManager actor's
      // AwaitReady, which releases the instant the session reaches Ready (or the
      // moment it can't) — a message to the owner, never a poll cadence, so it
      // completes exactly as fast as the transition and would work unchanged if
      // that owner were on another machine. Then classify ONCE (a pure decision
      // over the observed status) to distinguish a genuinely trustworthy session
      // from one that registered Ready but is Faulted/Stale.
      match! sessionOps.AwaitReady (toSessionId sessionId) integrationSettleTimeout |> Async.AwaitTask with
      | Error e -> return Error (sprintf "integration session '%s' did not become ready: %s" sessionId (SageFsError.describeForAgent e))
      | Ok () ->
        let! obs = observe ()
        match Features.Verification.SessionTrust.settleDecision (Features.Verification.SessionTrust.classify obs) with
        | Features.Verification.SessionTrust.SettleDecision.Ready _ -> return Ok obs
        | Features.Verification.SessionTrust.SettleDecision.Terminal reason -> return Error reason
        | Features.Verification.SessionTrust.SettleDecision.Retry ->
          return Error (sprintf "integration session '%s' registered Ready but is not yet trustworthy" sessionId)
    }

  /// Wait until a PURE condition over the current model holds, driven by the
  /// daemon's model-changed notification — completes the instant the condition
  /// flips, never on a poll cadence, so it takes exactly as long as the work and
  /// no longer. `timeout` is only a safety bound. The condition is a pure
  /// `unit -> bool` read; the notification is the `StateChangedEvent` interface
  /// (in-process today, a swappable transport for a remote model owner tomorrow —
  /// location transparency). Returns true if the condition held, false on timeout.
  let awaitModelCondition (timeout: System.TimeSpan) (cond: unit -> bool) : Async<bool> =
    async {
      if cond () then return true
      else
        let tcs = System.Threading.Tasks.TaskCompletionSource<bool>()
        use _sub = stateChangedEvent.Publish.Subscribe(fun _ -> if cond () then tcs.TrySetResult true |> ignore)
        // Re-check AFTER subscribing to close the lost-wakeup window (the model
        // may have changed between the first check and the subscription).
        if cond () then return true
        else
          let! winner =
            System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay timeout)
            |> Async.AwaitTask
          return System.Object.ReferenceEquals(winner, tcs.Task :> System.Threading.Tasks.Task) && tcs.Task.Result
    }

  /// After a landing rebase rewrites the integration worktree's files, make
  /// the integration session's OWN verification of those files provably
  /// reflect the rebase before `ComputeAffected`/`RunTests` read anything —
  /// the F17 attributable-settle close (cohort-dogfood-findings.md's F17
  /// timing dig).
  ///
  /// PRIOR DESIGN (retired): dispatch `SageFsMsg.FileContentChanged` (the
  /// as-you-type editing path) and wait for the live-testing FCS pipeline's
  /// OWN incremental decision (`generationOf`, a shared run/discovery
  /// counter) to advance. That was provably insufficient, not merely racy:
  /// for a rebase whose diff is a same-signature BODY edit to an existing
  /// function, the FCS pipeline's symbol-NAME diff sees no change at all
  /// (`TestCycleEffects.decideAfterTypeCheck`'s `changedSymbols = []`
  /// branch), decides "no impacted tests", and emits ZERO effects — no eval,
  /// no run, nothing that could ever bump the counter the wait watched. No
  /// window size fixes that (confirmed: tripling it changed nothing). Worse,
  /// even a satisfied wait would not have been safe: the FSI session's bound
  /// definition of the edited function stays STALE until something actually
  /// re-evals the file's content into it, so reading discovery/results
  /// without that re-eval risks running the OLD binding and reporting a
  /// genuine regression as passing — the exact fail-open F17 exists to
  /// prevent, one layer deeper than the shared-counter symptom.
  ///
  /// CURRENT DESIGN: don't route through the auto-detect editing pipeline at
  /// all — it decides WHETHER to verify from a signal (symbol names) that
  /// cannot see this class of change. Instead:
  ///  1. Force-eval every rebased `.fs` file directly against the worker
  ///     (`WorkerMessage.EvalLiveTestFile`, the same primitive the editing
  ///     pipeline eventually calls) UNCONDITIONALLY, rebinding the session's
  ///     live definitions to the rebase's actual content — awaited directly,
  ///     no polling, no shared counter.
  ///  2. Compute the CONSERVATIVE set of tests verification must cover via
  ///     the pure `AffectedTests.verificationTestSet` (coverage-based when
  ///     trustworthy, the WHOLE discovered suite when it isn't — NEVER empty
  ///     on a real diff; see that function's NO-EMPTY-ESCAPE doc, proven in
  ///     `AffectedTestsTests.fs`).
  ///  3. Actually run that set (`CohortLandingVerify.runTestsInSession`, the
  ///     same attributable, per-run-generation primitive `RunTests` already
  ///     trusts) and wait for ITS OWN real completion — attributable because
  ///     it is keyed to the specific generation THIS call's run started, not
  ///     to any run that happens to complete in a window.
  let rediscoverRebasedFiles (sessionId: string) (worktreePath: string) (baseSha: string) (headSha: string) : Async<Result<unit, string>> =
    // Eval one rebased file at a time (path order — git diff is sorted,
    // matching the fixture's compile order Alice < Bob < Tests, so a file's
    // dependencies are hot-loaded before the file that references them),
    // merging each success's discovery. Fails CLOSED on the first eval that
    // cannot be reached or does not compile — a partially re-evaluated
    // session is not trustworthy enough to verify against.
    let rec evalRebasedFiles (files: string list) : Async<Result<unit, string>> =
      async {
        match files with
        | [] -> return Ok ()
        | full :: rest ->
          match (try Some(System.IO.File.ReadAllText full) with _ -> None) with
          | None | Some "" -> return! evalRebasedFiles rest
          | Some content ->
            let replyId = sprintf "cohort-rediscover-%s" (System.Guid.NewGuid().ToString("N"))
            let! outcome =
              proxyToSession getProxyStr notifyWorkerDiedStr sessionId
                (WorkerProtocol.WorkerMessage.EvalLiveTestFile(full, content, replyId))
              |> Async.AwaitTask
            match outcome with
            | Error err ->
              return Error (sprintf "could not re-eval rebased file %s: %s" full (SageFsError.describe err))
            | Ok (WorkerProtocol.WorkerResponse.EvalLiveTestFileResult (_, Error err)) ->
              return Error (sprintf "re-eval of rebased file %s failed: %s" full (SageFsError.describe err))
            | Ok (WorkerProtocol.WorkerResponse.EvalLiveTestFileResult (_, Ok (tests, _providers))) ->
              elmRuntime.Dispatch(SageFsMsg.Event (TuiEvent.LiveDiscoveryMerged (sessionId, tests)))
              return! evalRebasedFiles rest
            | Ok other ->
              return Error (sprintf "unexpected worker response re-evaling rebased file %s: %A" full other)
      }
    async {
      // FileContentChanged/RunTestsRequested are honored only for an Active
      // live-testing cycle; an Interactive session isn't Active until this
      // (idempotent) enable.
      elmRuntime.Dispatch(SageFsMsg.EnableLiveTestingForSession sessionId)
      match! Features.CohortGit.diffNames worktreePath baseSha headSha with
      | Error _ ->
        // Can't determine what the rebase changed => can't re-discover it => we
        // would verify on stale discovery. Fail CLOSED (inconclusive/resubmit).
        Log.warn "[cohort-landing] could not diff the rebased files for session %s — cannot trust verification, failing closed" sessionId
        return Error "could not diff the rebased files to re-discover them; cannot trust verification — resubmit"
      | Ok changedFiles ->
        let changedFsFiles =
          changedFiles
          |> List.filter (fun f -> f.EndsWith(".fs", System.StringComparison.OrdinalIgnoreCase))
          |> List.map (fun rel -> System.IO.Path.Combine(worktreePath, rel))
          |> List.filter System.IO.File.Exists
        match changedFsFiles with
        | [] -> return Ok () // nothing to rediscover; the existing discovery is valid
        | _ ->
        match! evalRebasedFiles changedFsFiles with
        | Error reason ->
          Log.warn "[cohort-landing] rediscovery of session %s could not re-eval the rebase's own files — %s — failing closed" sessionId reason
          return Error (sprintf "post-rebase re-eval did not complete: %s — resubmit once the integration session settles" reason)
        | Ok () ->
        // Every rebased file is now genuinely bound into the FSI session.
        // Compute the CONSERVATIVE set of tests that must actually run to
        // verify this rebase — coverage-based narrow with a NO-EMPTY-ESCAPE
        // floor (never trust "nothing affected" on a real diff).
        let cycle = SageFsModel.cycleOwnedBySession sessionId (elmRuntime.GetModel())
        let state = cycle.TestState
        let allTests = state.DiscoveredTests |> Array.map (fun tc -> tc.Id) |> Array.toList
        let maps =
          match Map.tryFind sessionId cycle.InstrumentationMaps with
          | Some m when m.Length > 0 -> m
          | _ -> cycle.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
        let merged = Features.LiveTesting.InstrumentationMap.merge maps
        let coveredFilesOf (tid: Features.LiveTesting.TestId) : string list option =
          match merged.Slots.Length with
          | 0 -> None
          | _ ->
            match Map.tryFind tid state.TestCoverageBitmaps with
            | Some bm when bm.Count = merged.TotalProbes && bm.Count > 0 ->
              Some(Features.LiveTesting.InputHashCoverage.coveredFiles merged bm)
            | _ -> None
        let toVerify =
          Features.LiveTesting.AffectedTests.verificationTestSet changedFiles coveredFilesOf allTests
        match toVerify with
        | [] ->
          // Nothing discovered at all yet — nothing to verify; ComputeAffected's
          // own (unchanged) narrowing below reads the same empty state and agrees.
          return Ok ()
        | _ ->
        match! awaitIntegrationSessionTrusted sessionId with
        | Error reason -> return Error reason
        | Ok observation ->
          let! runResult =
            Features.CohortLandingVerify.runTestsInSession elmRuntime awaitModelCondition observation sessionId toVerify
          match runResult with
          | Ok _failing ->
            Log.info "[cohort-landing] rediscovered + verified %d test(s) covering %d rebased file(s) for session %s" toVerify.Length changedFsFiles.Length sessionId
            return Ok ()
          | Error reason ->
            // THE FAIL-CLOSED GUARANTEE (F17): a conservative verification run
            // that could not be trusted to complete must never be treated as
            // "nothing failed" — report inconclusive so `decide` records
            // Blocked(Inconclusive)/RebaseAndResubmit and the requester
            // retries once the integration session settles, rather than
            // fast-forwarding an unverified change.
            Log.warn "[cohort-landing] rediscovery of session %s could not run the conservative verification set — %s — failing closed" sessionId reason
            return Error (sprintf "post-rebase conservative verification run did not complete: %s — resubmit once the integration session settles" reason)
    }

  let cohortLandingPerformer : Features.CohortOwner.LandingPerformer<MemberTable.MemberId> =
    { Rebase = fun _landingId onto commits ->
        async {
          match McpTools.cohortIntegrationRef.Value with
          | None -> return Error(cohortIntegrationNotConfigured ())
          | Some binding ->
            Log.info "[cohort-landing] Rebase worktree=%s onto=%s commits=%A" binding.WorktreePath onto commits
            // Bring the member's OWN commits onto the integration head — the
            // realistic model: a member's commits live on their own branch, not
            // pre-positioned in the integration worktree.
            let! result = Features.CohortGit.rebaseCommitsOnto binding.WorktreePath onto commits
            Log.info "[cohort-landing] Rebase result=%A" result
            match result with
            | Ok realNewHead -> return Ok realNewHead
            | Error files -> return Error files
        }
      // v1 conservative (design decision, sagefs-multiagent-vision.md item
      // 14c): every test discovered in the integration session, never a
      // diff-narrowed subset — running everything is always correct, if not
      // maximally fast. `baseSha`/`headSha` are now genuinely distinct (real
      // rebase base vs rebased head), so a precise coverage-based affected-set
      // via `CohortGit.diffNames baseSha headSha` is a viable later
      // optimization — deferred, not this item.
      ComputeAffected = fun _landingId baseSha headSha ->
        async {
          match McpTools.cohortIntegrationRef.Value with
          | None -> return Error "integration not configured — call set_integration_ref first"
          | Some { Session = McpTools.IntegrationSession.Failed reason } -> return Error (sprintf "integration session failed to start: %s" reason)
          | Some { Session = McpTools.IntegrationSession.Pending } -> return Error "integration session not started yet — retry set_integration_ref"
          | Some ({ Session = McpTools.IntegrationSession.Started sessionId } as binding) ->
            // (Gap 3) Settle first: the rebase that just ran retriggered the
            // session's rebuild, and reading discovery mid-rebuild can see it
            // transiently empty — which would compute an EMPTY affected set and
            // land the change with nothing verified (fail-open). Wait for the
            // session to become trustworthy, then read.
            match! awaitIntegrationSessionTrusted sessionId with
            | Error reason -> return Error reason
            | Ok _settledObservation ->
            // The rebase rewrote the worktree source; recompile + rediscover it
            // (fast FSI hot-eval, not a full rebuild) and wait for discovery to
            // advance BEFORE reading tests, so the affected set + the run below
            // reflect the member's ACTUAL code, never the stale pre-rebase binary.
            // Fail CLOSED if the rebased code did not actually re-discover: the
            // owner maps this Error to VerificationInconclusive (never a false
            // "0 failing"), so a landing can never fast-forward on stale discovery.
            match! rediscoverRebasedFiles sessionId binding.WorktreePath baseSha headSha with
            | Error reason -> return Error reason
            | Ok () ->
            let cycle = SageFsModel.cycleOwnedBySession sessionId (elmRuntime.GetModel())
            let state = cycle.TestState
            let allTests =
              Features.LiveTesting.LiveTestState.statusEntriesForSession sessionId state
              |> Array.map (fun e -> e.TestId)
              |> Array.toList
            // v2 (§5.4): narrow to the tests the base..head diff can actually
            // affect (a test's coverage bitmap intersecting the changed files),
            // instead of the whole suite. ANY failure to compute the diff, and
            // any test whose coverage is absent/stale, falls back to running it
            // — running everything is always correct, an unsafe narrow is not.
            let! diff = Features.CohortGit.diffNames binding.WorktreePath baseSha headSha
            let narrowed =
              match diff with
              | Error _ -> allTests
              | Ok changedFiles ->
                let maps =
                  match Map.tryFind sessionId cycle.InstrumentationMaps with
                  | Some m when m.Length > 0 -> m
                  | _ -> cycle.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
                let merged = Features.LiveTesting.InstrumentationMap.merge maps
                let coveredFilesOf (tid: Features.LiveTesting.TestId) : string list option =
                  match merged.Slots.Length with
                  | 0 -> None
                  | _ ->
                    match Map.tryFind tid state.TestCoverageBitmaps with
                    | Some bm when bm.Count = merged.TotalProbes && bm.Count > 0 ->
                      Some(Features.LiveTesting.InputHashCoverage.coveredFiles merged bm)
                    | _ -> None
                let affectedTests =
                  Features.LiveTesting.AffectedTests.affected changedFiles coveredFilesOf allTests
                // FAIL-CLOSED gate. A landing that carries real file changes but
                // narrows to ZERO affected tests would run nothing and land
                // trivially "green" — the fail-OPEN that lets a test-breaking
                // change through the gate. It happens when coverage can't be
                // trusted: the integration worktree inherits the main repo's
                // prebuilt PDB, so a test's covered-file paths point at the wrong
                // build and never match the diff. Never trust an empty narrow on a
                // real diff — run the whole suite. Running everything is always
                // correct; an unverified landing is not.
                match affectedTests with
                | [] when not (List.isEmpty changedFiles) && not (List.isEmpty allTests) -> allTests
                | _ -> affectedTests
            return Ok (narrowed |> List.map Features.CohortTestProjection.toCohortTestId)
        }
      // `CohortLandingVerify` reads and writes the integration session's OWN
      // cycle (`SageFsModel.cycleOwnedBySession`, attribution-by-containsKey),
      // so this performer no longer forces the session to Primary via a
      // `SessionSwitched` dispatch. That dispatch was not just redundant — it
      // was actively destructive: mid-rebuild the session's Primary cycle is
      // transiently empty (owner=None), and `switchActiveLiveTestingState` then
      // treats the switch as a real one and PROMOTES the empty Background cycle
      // into Primary, WIPING the discovery `ComputeAffected` just used — the
      // "N of N not attributed" landing-gate flake. Reads/writes route by
      // sessionId regardless of the active pointer, so the switch is gone.
      RunTests = fun _landingId tests ->
        async {
          match McpTools.cohortIntegrationRef.Value with
          | None -> return Error "integration not configured — call set_integration_ref first"
          | Some { Session = McpTools.IntegrationSession.Failed reason } -> return Error (sprintf "integration session failed to start: %s" reason)
          | Some { Session = McpTools.IntegrationSession.Pending } -> return Error "integration session not started yet — retry set_integration_ref"
          | Some ({ Session = McpTools.IntegrationSession.Started sessionId } as binding) ->
            // (Gap 3) Settle first, then verify against the SETTLED observation.
            // Verifying while the rebase-triggered rebuild is still in flight is
            // exactly what made a good landing block on "session still warming
            // up" — now it waits for Ready, and if it never settles the run is
            // reported inconclusive (below) instead of as a false test failure.
            match! awaitIntegrationSessionTrusted sessionId with
            | Error reason -> return Error reason
            | Ok observation ->
            let liveTests = tests |> List.map cohortToLiveTestId
            // Content-addressed cache lookup (§5.4). Compute each test's
            // InputHash from the integration session's OWN coverage: the merged
            // instrumentation map + the test's coverage bitmap. A test is
            // trustworthy-hashable only when it has a bitmap whose size matches
            // the current instrumentation (a stale/absent bitmap → None → the
            // test always runs and is never cached, so a source change can never
            // be masked by a stale skip). LandingCache.verify then runs only the
            // cache misses (+ untrusted tests) and records the trustworthy ones.
            let lt = SageFsModel.cycleOwnedBySession sessionId (elmRuntime.GetModel())
            let maps =
              match Map.tryFind sessionId lt.InstrumentationMaps with
              | Some m when m.Length > 0 -> m
              | _ -> lt.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
            let merged = Features.LiveTesting.InstrumentationMap.merge maps
            let fileReader (path: string) =
              try Some(System.IO.File.ReadAllText path) with _ -> None
            // Fold the build/toolchain fingerprint into every test's InputHash
            // (roast-6 #1): an SDK / dependency / config change with no source
            // edit must still be a cache MISS, or a landing could serve a stale
            // "verified" result. Computed once from the integration worktree's
            // props + the loaded FSharp.Core + the running runtime, PLUS (roast-7
            // §7) the daemon's OWN semantics version, so a change to how SageFs
            // itself discovers/selects/runs/interprets tests invalidates the
            // cache too — not just a change to the user project's toolchain.
            let toolchain =
              Features.LiveTesting.InputHashCoverage.toolchainFingerprint
                (Features.LiveTesting.InputHashCoverage.sagefsSemanticsVersion ())
                fileReader
                binding.WorktreePath
            let inputHashOf (tid: Features.LiveTesting.TestId) : string option =
              match merged.Slots.Length with
              | 0 -> None
              | _ ->
                match Map.tryFind tid lt.TestState.TestCoverageBitmaps with
                | Some bm when bm.Count = merged.TotalProbes && bm.Count > 0 ->
                  Some(Features.LiveTesting.InputHashCoverage.ofCoverage toolchain fileReader merged bm)
                | _ -> None
            let runMisses toRun =
              Features.CohortLandingVerify.runTestsInSession elmRuntime awaitModelCondition observation sessionId toRun
            // Talks to the single owner via a message (roast-6 #7a) — never
            // a shared ref read-modify-written from this performer.
            let! result =
              cohortLandingCacheOwner.Verify inputHashOf sessionId liveTests runMisses
              |> Async.AwaitTask
            match result with
            | Ok failing -> return Ok (failing |> List.map Features.CohortTestProjection.toCohortTestId)
            | Error reason ->
              // Fail-closed AND honest (Clef mode-shift, roast-7 §7): a session
              // that can't be trusted enough to run tests in must never read as
              // "all passed" — but it must ALSO not read as "all failed". Report
              // it as INCONCLUSIVE so `Cohort.decide` records
              // `Blocked(Inconclusive)` and un-jams the queue, rather than
              // permanently stranding the landing behind a fabricated test
              // failure. The landing still does not fast-forward — safe either way.
              Log.warn "[cohort-landing] RunTests could not verify session %s: %s — reporting inconclusive (fail-closed, never a false pass)" sessionId reason
              return Error reason
        }
      // `toSha` is `decide`'s FastForward target = the landing's `rebasedHead`
      // (the real commit `Rebase` returned, now that `Verifying` carries base
      // and rebasedHead separately). Fast-forward the integration branch to it.
      FastForward = fun _landingId toSha ->
        async {
          match McpTools.cohortIntegrationRef.Value with
          | None -> return Error "integration not configured — call set_integration_ref first"
          | Some binding -> return! Features.CohortGit.fastForwardBranch workingDir binding.Branch toSha
        }
      Notify = fun who event ->
        Log.info "[cohort-landing] notify %s: %A" (MemberTable.MemberId.display who) event
    }

  // Bound to a name (not inlined into startWithPerformer's call) so the
  // dashboard's lane view (§6.5) can read the same ledger back via
  // `ReadAll` — `CohortOwner.Handle` publishes only the projected
  // `CohortFrame` (`ReadFrame`), which carries no timing data at all (see
  // `Features/CohortLanes.fs`'s module doc), so the lane view reads this
  // port directly instead.
  let cohortLedgerPort =
    let ledgerPath = LocalData.cohortLedgerPath DaemonState.SageFsDir
    // Before the owner replays it: clear the ledger if the cohort it holds
    // finished longer ago than the retention window. Done here, not on a
    // timer, so the owner's state and the ledger on disk never disagree.
    try
      match Features.CohortLedgerSqlite.Sqlite.pruneFinished DataRetention.cohortLedgerRetention DateTime.UtcNow ledgerPath with
      | Features.LocalDataRetention.LedgerDecision.Clear(finishedAt, rows) ->
        Log.info "[cohort] cleared %d ledger rows from a cohort that finished %s" rows (finishedAt.ToString "O")
      | Features.LocalDataRetention.LedgerDecision.NothingStored
      | Features.LocalDataRetention.LedgerDecision.KeepActive _
      | Features.LocalDataRetention.LedgerDecision.KeepRecent _ -> ()
    with ex ->
      Log.warn "[cohort] ledger retention check failed, leaving it alone: %s" ex.Message
    Features.CohortLedgerSqlite.Sqlite.create ledgerPath

  use cohortOwner =
    Features.CohortOwner.startWithPerformer
      (Log.asILogger ())
      cohortLedgerPort
      (fun () -> System.DateTime.UtcNow)
      Features.CohortOwner.productionEntropy
      getCohortSessionTestOutcomes
      cohortLandingPerformer

  // Create a diagnostics-changed event (aggregated from workers)
  let diagnosticsChanged = Event<Features.DiagnosticsStore.T>()

  // Partially applied worker helpers (capture httpClient + readSnapshot)
  let getWorkerBaseUrl = getWorkerBaseUrl readSnapshot

  // Every event on a worker's reload stream means the worker's hot-reload
  // state may have moved (a save patched, restarted, or kept live state), so
  // it's a HotReloadChanged: the dashboard refetches the worker's panels and
  // morphs if anything changed. Without this the only trigger was the
  // daemon's own file event, which fires before the worker has decided.
  let ensureReloadRelay =
    WorkerReloadRelay.start getWorkerBaseUrl (fun sid -> stateChangedEvent.Trigger (HotReloadChanged sid)) cts.Token
  // Anything that can mean a session got a worker, lost one, or got a new one.
  // HotReloadChanged is left out on purpose: the relay raises it.
  stateChangedEvent.Publish.Add(fun change ->
    match change with
    | SessionReady sid
    | SessionSwitched sid
    | FileReloaded (sid, _)
    | SessionFaulted (sid, _) -> ensureReloadRelay sid
    | SessionProgress
    | HotReloadChanged _
    | ModelChanged _
    | WarmupProgress _
    | SystemAlarm _
    | CohortChanged
    | WarmupContextSnapshot _
    | HotReloadSnapshot _
    | HotReloadFileToggled _
    | SessionActivated _
    | SessionCreated _
    | SessionStopped _
    | WorkflowSwitching _
    | WorkflowSwitched _
    | SessionHealthChanged _ -> ())
  // Every applied cohort command tells the dashboard to redraw, so the cohort
  // panel shows up when a member joins and goes when the last one leaves.
  // Lease renewals are left out: the reaper renews every present member once
  // a minute, and a redraw for that changes nothing anyone can see.
  cohortOwner.Events.Add(fun events ->
    let visible =
      events
      |> List.exists (fun ev ->
        match ev with
        | SageFs.Cohort.CohortEvent.LeaseRenewed _ -> false
        | _ -> true)
    match visible with
    | true -> stateChangedEvent.Trigger CohortChanged
    | false -> ())
  // Sessions that were ready before this line ran.
  SessionManager.QuerySnapshot.allSessions (readSnapshot ())
  |> List.iter (fun info -> ensureReloadRelay info.Id)

  let fetchWorkerEndpoint sessionId path timeout parse =
    fetchWorkerEndpoint httpClient readSnapshot sessionId path timeout parse

  // Warmup context fetcher for MCP — uses session manager to find worker URL
  let getWarmupContextForMcp (sessionId: WorkerProtocol.SessionId) : System.Threading.Tasks.Task<WarmupContext option> =
    fetchWorkerEndpoint sessionId "/warmup-context" mcpFetchTimeoutSec
      (WorkerProtocol.Serialization.deserialize<WarmupContext>)

  // Hotreload state fetcher for MCP — returns watched file paths
  let getHotReloadStateForMcp (sessionId: WorkerProtocol.SessionId) : System.Threading.Tasks.Task<string list option> =
    fetchWorkerEndpoint sessionId "/hotreload" mcpFetchTimeoutSec (fun resp ->
      use doc = System.Text.Json.JsonDocument.Parse(resp)
      doc.RootElement.GetProperty("files").EnumerateArray()
      |> Seq.filter (fun f -> f.GetProperty("watched").GetBoolean())
      |> Seq.map (fun f -> f.GetProperty("path").GetString())
      |> Seq.toList)

  // Wire test discovery from SessionManager → Elm model
  onTestDiscoveryCallback <- handleTestDiscovery readSnapshot workingDir log elmRuntime.Dispatch

  // Auto-enable live testing for LiveTesting-workflow sessions the moment they
  // become ready (covers create, switch-workflow, HTTP, dashboard, and resume
  // — every path funnels through onSessionReady). EnableLiveTestingForSession
  // is idempotent (no-ops if already Active) and tolerates warmup discovery not
  // having landed yet, so firing on every ready transition is safe. The runtime
  // enable/disable toggle can still turn it off within the session.
  onSessionReadyExtra <- fun sid ->
    SessionManager.QuerySnapshot.tryGetSession sid (readSnapshot())
    |> Option.iter (fun info ->
      match info.Workflow with
      | WorkflowTypes.SessionWorkflow.LiveTesting ->
        elmRuntime.Dispatch(SageFsMsg.EnableLiveTestingForSession (WorkerProtocol.SessionId.value sid))
      | WorkflowTypes.SessionWorkflow.Interactive
      | WorkflowTypes.SessionWorkflow.HotReload _ -> ())

  // Wire instrumentation maps from SessionManager → Elm model
  onInstrumentationMapsCallback <- fun sid maps ->
    elmRuntime.Dispatch(SageFsMsg.Event (TuiEvent.InstrumentationMapsReady (WorkerProtocol.SessionId.value sid, maps)))

  // Wire warmup progress from SessionManager → Elm model + SSE broadcast
  onWarmupProgressCallback <- fun sid progress ->
    handleWarmupProgress elmRuntime.Dispatch sid progress
    match tryParseWarmupProgress progress with
    | Some (step, total, msg) ->
      match WorkerProtocol.SessionId.validate sid with
      | Ok sidTyped -> stateChangedEvent.Trigger(WarmupProgress(sidTyped, step, total, msg))
      | Error _ -> ()
    | None -> ()

  // W12(R10): Use Volatile.Read/Write to ensure MCP-thread writes are visible to HTTP-thread
  // readers without data races. Plain ref cell field access has no memory barrier on ARM.
  let sharedBindingScope : SageFs.Features.BindingExplorer.BindingScopeSnapshot option ref = ref None
  // Shared feature push state — gives Dashboard access to EvalTimeline sparkline data.
  let sharedFeatureState : SageFs.Features.FeatureHooks.FeaturePushState ref = ref SageFs.Features.FeatureHooks.FeaturePushState.empty
  // Adaptive live-bindings store — per-session cval cells updated after each eval;
  // subscribers fire only when the snapshot actually changed (FSharp.Data.Adaptive).
  let liveBindingsAdaptive = SageFs.Features.LiveBindingsAdaptive.create ()

  // Permanent binding-scope subscriber — updates sharedBindingScope on eval
  // completion regardless of MCP SSE client connectivity. Fixes the dashboard
  // "0 bindings" problem when no editor client is connected. W12(R10):
  // Volatile.Write for ARM memory barrier.
  //
  // #88: this fires on EVERY output change (outputCount delta), and each rebuild
  // re-reads the whole active buffer and re-parses it. Under continuous output —
  // a run_app'd console/game app (#82), or a burst of evals — that ran per
  // output line and grew daemon RSS unboundedly. Two guards fix it without
  // losing the no-client fallback: (1) DEBOUNCE — coalesce a burst of changes
  // into at most one rebuild per quiet window (bindings are a display panel;
  // sub-second latency is fine); (2) NEVER WIPE — if the latest output parses to
  // no bindings (plain app/printfn output), keep the last good scope instead of
  // overwriting it with None.
  let lastBindingOutputCount = ref -1
  let bindingRebuildGate = obj ()
  let mutable bindingRebuildTimer : System.Threading.Timer = null
  let rebuildBindingScope () =
    let model = elmRuntime.GetModel()
    // Use GetActiveBuffer (not GetBuffer) to handle AwaitingSession → staging buffer case.
    let activeBuf = model.RecentOutput.GetActiveBuffer(model.Sessions.ActiveSessionId)
    let rawOutput =
      activeBuf.FilterToList(fun o -> o.Kind = OutputKind.Result)
      |> List.rev
      |> List.map (fun o -> o.Text)
      |> String.concat "\n"
    match SageFs.Features.BindingExplorer.fromRawOutput rawOutput with
    | Some scope -> System.Threading.Volatile.Write(&sharedBindingScope.contents, Some scope)
    | None -> ()  // no parseable bindings (e.g. app output) — keep the last good scope
  let _bindingScopeSubscription =
    stateChangedEvent.Publish.Subscribe(fun change ->
      match change with
      | SseEvent.ModelChanged (outputCount, _) when outputCount <> lastBindingOutputCount.Value ->
        lastBindingOutputCount.Value <- outputCount
        lock bindingRebuildGate (fun () ->
          match bindingRebuildTimer with
          | null ->
            bindingRebuildTimer <-
              new System.Threading.Timer(
                (fun _ -> try rebuildBindingScope () with ex -> Log.warn "[binding-scope] rebuild failed: %s" ex.Message),
                null, 250, System.Threading.Timeout.Infinite)
          | t -> t.Change(250, System.Threading.Timeout.Infinite) |> ignore)
      | _ -> ())

  // Create the multi-agent coordination tracker (in-memory, daemon-lifetime)
  let activityTracker = AgentActivityTracker.create ()

  // Both listeners share one origin set: the dashboard page (mcpPort + 1, see
  // dashboardPort below) calls MCP-port endpoints as a same-site own origin.
  let daemonOrigins = SageFs.Server.HttpOriginGuard.OwnOrigins.ofPorts [ mcpPort; mcpPort + 1 ]

  let mcpTask =
    McpServer.startMcpServer {
      DiagnosticsChanged = diagnosticsChanged.Publish
      StateChanged = Some stateChangedEvent.Publish
      FrictionStore = frictionStore
      Port = mcpPort
      BindHost = bindHost
      OwnOrigins = daemonOrigins
      SessionOps = sessionOps
      ElmRuntime = Some elmRuntime
      GetWarmupContext = Some (fun (sidStr: string) ->
        match WorkerProtocol.SessionId.validate sidStr with
        | Ok sid -> getWarmupContextForMcp sid
        | Error _ -> Threading.Tasks.Task.FromResult None)
      GetHotReloadState = Some (fun (sidStr: string) ->
        match WorkerProtocol.SessionId.validate sidStr with
        | Ok sid -> getHotReloadStateForMcp sid
        | Error _ -> Threading.Tasks.Task.FromResult None)
      SharedBindingScope = sharedBindingScope
      SharedFeatureState = Some sharedFeatureState
      ActivityTracker = activityTracker
      LiveSnapshotSink = Some (fun sid snap ->
        SageFs.Features.LiveBindingsAdaptive.update liveBindingsAdaptive sid snap)
      CohortOwner = Some cohortOwner
    } cts.Token

  let liveTestTickMs = 25

  // Test cycle tick timer — drives debounce channels for live testing.
  // Keep this comfortably below the 50ms tree-sitter debounce so as-you-type
  // feedback can land near the intended debounce threshold instead of being
  // quantized up to an outer 200ms polling loop.
  // Elmish-style batching means rapid ticks coalesce: N ticks → N updates → 1 render
  // Idle-down: when NO session is actively live-testing the timer switches to a
  // 1s heartbeat instead of a constant 40Hz scan (roast queue item 6). The
  // one-shot reschedule pattern (same as cacheSaveTimer) prevents reentrancy.
  let mutable testCycleTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  // The tick only fires debounces, so it runs fast exactly while one is pending
  // (needsLiveTestTick) and otherwise idles on a slow heartbeat. It used to
  // run at 40 Hz for as long as any test had ever been discovered. A model
  // change that queues a debounce wakes an idle timer at once, so a save is
  // never held back by the heartbeat. 1 = idling on the heartbeat.
  let testCycleIdle = ref 0
  let rescheduleTestCycle (periodMs: int) =
    if not (isNull testCycleTimerRef) then
      try testCycleTimerRef.Change(periodMs, System.Threading.Timeout.Infinite) |> ignore
      with :? System.ObjectDisposedException -> ()
  let testCycleCallback _ =
    try
      let model = elmRuntime.GetModel()
      let periodMs =
        match SageFsModel.needsLiveTestTick model with
        | true ->
          System.Threading.Volatile.Write(&testCycleIdle.contents, 0)
          elmRuntime.Dispatch(SageFsMsg.TestCycleTick DateTimeOffset.UtcNow)
          liveTestTickMs
        | false ->
          System.Threading.Volatile.Write(&testCycleIdle.contents, 1)
          // Re-check after going idle: a debounce queued since the read above
          // found the timer not yet idle, so its wake was a no-op.
          match SageFsModel.needsLiveTestTick (elmRuntime.GetModel()) with
          | true ->
            System.Threading.Volatile.Write(&testCycleIdle.contents, 0)
            liveTestTickMs
          | false -> 1000
      rescheduleTestCycle periodMs
    with ex ->
      log.LogWarning("TestCycleTimer callback threw unexpectedly: {Error}", ex.Message)
  let testCycleTimer =
    let t = new System.Threading.Timer(
      System.Threading.TimerCallback(testCycleCallback),
      null, liveTestTickMs, System.Threading.Timeout.Infinite)
    testCycleTimerRef <- t
    t
  // Wake only from idle: re-arming on every model change would keep pushing
  // the fire time back under a steady stream of output events.
  wakeLiveTestTick.Value <- fun model ->
    match SageFsModel.needsLiveTestTick model
          && System.Threading.Interlocked.CompareExchange(&testCycleIdle.contents, 0, 1) = 1 with
    | true -> rescheduleTestCycle liveTestTickMs
    | false -> ()

  // Periodic test cache save — crash recovery for test results.
  // Fires every Timeouts.manifestSaveInterval (default 60s, env-overridable via
  // SAGEFS_MANIFEST_SAVE_INTERVAL_SECONDS), only writes when RunGeneration has
  // advanced since last save.
  // W11(R10): Use a one-shot timer that reschedules AFTER completion to prevent reentrancy.
  // A periodic timer with System.Threading.Timer fires on ThreadPool; if the callback takes
  // longer than the interval, two threads both write to the same .tmp file → corrupt save file.
  // One-shot semantics: the next tick is scheduled only after the current tick finishes.
  let lastSavedGeneration = ref 0
  let mutable cacheSaveTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  let cacheSaveCallback _ =
    try
      // W27(R12): Inner try/with catches unexpected exceptions from callees and logs them.
      // The outer try/finally ensures rescheduling always runs, even on catastrophic failure.
      // Without this, an unhandled exception would propagate to the ThreadPool and kill the process.
      try
        periodicCacheSave log readSnapshot elmRuntime.GetModel lastSavedGeneration
        periodicManifestSave log manifestOwner readSnapshot elmRuntime.GetModel
      with ex ->
        log.LogWarning("Periodic cache/manifest save threw unexpectedly: {Error}", ex.Message)
    finally
      // Reschedule for the next period only after this run finishes — prevents concurrent runs.
      // W18(R11): Guard ObjectDisposedException — if Dispose() was called during this callback
      // (shutdown race), the Change() call throws ODE. The null check guards the startup window
      // only; the ODE guard handles the shutdown window.
      if not (isNull cacheSaveTimerRef) then
        try cacheSaveTimerRef.Change(int Timeouts.manifestSaveInterval.TotalMilliseconds, System.Threading.Timeout.Infinite) |> ignore
        with :? System.ObjectDisposedException -> ()
  let cacheSaveTimer =
    let t = new System.Threading.Timer(
      System.Threading.TimerCallback(cacheSaveCallback),
      null, int Timeouts.manifestSaveInterval.TotalMilliseconds, System.Threading.Timeout.Infinite)
    cacheSaveTimerRef <- t
    t

  // Periodic agent activity cleanup — evicts stale entries from the activity tracker.
  // One-shot timer pattern (same as cache save) to prevent reentrancy.
  let mutable activityCleanupTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  let activityCleanupCallback _ =
    try
      let outcome = AgentActivityTracker.cleanup activityTracker (TimeSpan.FromMinutes 5.0) DateTime.UtcNow
      match outcome with
      | SessionOperations.OccupancyCleanupOutcome.EvictedStale agents ->
        log.LogInformation("Agent cleanup: evicted stale agents: {Agents}", String.concat ", " agents)
      | _ -> ()
    finally
      if not (isNull activityCleanupTimerRef) then
        try activityCleanupTimerRef.Change(60_000, System.Threading.Timeout.Infinite) |> ignore
        with :? System.ObjectDisposedException -> ()
  let activityCleanupTimer =
    let t = new System.Threading.Timer(
      System.Threading.TimerCallback(activityCleanupCallback),
      null, 60_000, System.Threading.Timeout.Infinite)
    activityCleanupTimerRef <- t
    t

  // Periodic orphaned-temp-dir re-sweep — the cross-process backstop for the
  // sagefs-host-adopt-*/sagefs-test/* leak (see sweepOrphanedTempDirs): the
  // startup sweep above only reclaims another process's mess when THIS
  // daemon happens to (re)start. A single daemon that stays up for hours (the
  // actual /tmp-exhaustion incident) would otherwise sit next to a leak from
  // some OTHER process that died during its own uptime until its own next
  // restart. One-shot timer pattern (same as cache save / activity cleanup)
  // to prevent reentrancy; a generous 15-minute period because this walks
  // the OS temp directory and every sweep is otherwise a no-op.
  let orphanTempDirSweepIntervalMs = 15 * 60 * 1000
  let mutable orphanTempDirSweepTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  let orphanTempDirSweepCallback _ =
    try
      sweepOrphanedTempDirs log
    finally
      if not (isNull orphanTempDirSweepTimerRef) then
        try orphanTempDirSweepTimerRef.Change(orphanTempDirSweepIntervalMs, System.Threading.Timeout.Infinite) |> ignore
        with :? System.ObjectDisposedException -> ()
  let orphanTempDirSweepTimer =
    let t = new System.Threading.Timer(
      System.Threading.TimerCallback(orphanTempDirSweepCallback),
      null, orphanTempDirSweepIntervalMs, System.Threading.Timeout.Infinite)
    orphanTempDirSweepTimerRef <- t
    t

  // Cohort lease reaper (roast-7 §5) — makes the 30-minute lease actually cost
  // silence. Every 60s: renew the lease of each Present member seen active in
  // the last 2 minutes (a window shorter than the 5-min tracker eviction, so an
  // active member is always renewed before its presence is dropped), THEN post
  // Tick so decide departs members silent past leaseWindow and orphans their
  // claims. Without this the Tick handler and leaseWindow were dead — a departed
  // member's claims were never released on a live daemon. Renewal is keyed on
  // the cohort's OWN MemberIds (ReadCohortState), decoupled from the display
  // tracker's short eviction that broke the earlier attempt.
  let cohortReaperRenewWindow = TimeSpan.FromMinutes 2.0
  let mutable cohortReaperTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  let cohortReaperCallback _ =
    try
      let now = DateTime.UtcNow
      let state = cohortOwner.ReadCohortState()
      match Map.isEmpty state.Members with
      | true -> () // no members — nothing to renew or reap
      | false ->
        let freshKeys =
          AgentActivityTracker.getActivePresences activityTracker None cohortReaperRenewWindow now
          |> List.map (fun p -> p.AgentName)
          |> Set.ofList
        let isActive (m: MemberTable.MemberId) = Set.contains (MemberTable.MemberId.display m) freshKeys
        for m in cohortMembersToRenew isActive state.Members do
          cohortOwner.Post(SageFs.Cohort.CohortCommand.RenewLease m, ignore)
        cohortOwner.Post(SageFs.Cohort.CohortCommand.Tick, ignore)
    with ex ->
      log.LogWarning("Cohort reaper tick threw unexpectedly: {Error}", ex.Message)
    // reschedule after this run (one-shot pattern, guards the shutdown race)
    if not (isNull cohortReaperTimerRef) then
      try cohortReaperTimerRef.Change(60_000, System.Threading.Timeout.Infinite) |> ignore
      with :? System.ObjectDisposedException -> ()
  let cohortReaperTimer =
    let t = new System.Threading.Timer(
      System.Threading.TimerCallback(cohortReaperCallback),
      null, 60_000, System.Threading.Timeout.Infinite)
    cohortReaperTimerRef <- t
    t

  // Friction retention, again every DataRetention.pruneInterval (it already
  // ran once when the store was opened). One-shot pattern like the timers
  // above, and disposed in the shutdown block below, which is also what keeps
  // it alive: a Timer nothing references gets collected and stops firing.
  let frictionPruneIntervalMs = int DataRetention.pruneInterval.TotalMilliseconds
  let mutable frictionPruneTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  let frictionPruneCallback _ =
    try
      match frictionStore with
      | Some store -> pruneFriction log store
      | None -> ()
    finally
      if not (isNull frictionPruneTimerRef) then
        try frictionPruneTimerRef.Change(frictionPruneIntervalMs, System.Threading.Timeout.Infinite) |> ignore
        with :? System.ObjectDisposedException -> ()
  let frictionPruneTimer =
    let t = new System.Threading.Timer(
      System.Threading.TimerCallback(frictionPruneCallback),
      null, frictionPruneIntervalMs, System.Threading.Timeout.Infinite)
    frictionPruneTimerRef <- t
    t

  // Live testing file watcher manager — per-session directory watchers.
  // onFileReloaded receives only REAL session IDs: the daemon-CWD fallback
  // watcher claims no session, so files it sees never fire FileReloaded (a
  // path with no owning session must not be attributed to a fabricated one).
  let liveTestWatcherManager =
    new LiveTestWatcherManager(
      elmRuntime.Dispatch,
      (fun sessionId path ->
        stateChangedEvent.Trigger (FileReloaded (sessionId, path))
        // Cohort claim early-warning (multi-agent vision §5.1): a save inside
        // another cohort member's claimed scope is advisory, never blocking
        // (`Cohort.decide`'s `ObserveSave` never refuses) — so this is a
        // fire-and-forget `Post`, never awaited, and never on the hot watcher
        // path for a solo/non-cohort session (`resolveSaveObserver` returns
        // `None` immediately for those).
        match
          resolveSaveObserver
            (SessionManager.QuerySnapshot.allSessions (readSnapshot ()))
            (cohortOwner.ReadCohortState().Members)
            sessionId
            path
        with
        | Some(observer, relPath) ->
          cohortOwner.Post(Cohort.CohortCommand.ObserveSave(observer, relPath), ignore)
        | None -> ()),
      Some workingDir)
  watcherManagerRef := Some liveTestWatcherManager
  // Seed with any existing session directories (fallback dir handled by the
  // manager's fallbackDir).
  let seedSessionDirs () =
    let snapshot = readSnapshot()
    let sessions = SessionManager.QuerySnapshot.allSessions snapshot
    let sessionDirPairs =
      sessions
      |> List.map (fun si -> si.Id, si.WorkingDirectory)
    liveTestWatcherManager.SyncToSessions(sessionDirPairs)
  seedSessionDirs ()

  // Sessions worth RETAINING adaptive/output state for: everything except
  // Faulted/Stopped (see sweepStaleSessionState's own doc comment). A
  // Faulted session's record can stay registered for the user to inspect
  // while its retained per-session state is freed the moment it dies,
  // instead of only when the record itself is later purged.
  let retentionWorthySessionIds () =
    SessionManager.QuerySnapshot.allSessions (readSnapshot())
    |> List.filter (fun si -> not (WorkerProtocol.SessionLifecycleStatus.isDead si.Status))
    |> List.map (fun si -> WorkerProtocol.SessionId.value si.Id)
    |> Set.ofList

  // Sweep stale adaptive-store + feature-push state + recent output for
  // sessions that are gone OR dead (roast queue item 2). Idempotent — cheap
  // at idle. Called from the periodic timer below (a backstop) AND
  // immediately on the lifecycle transitions that actually cause it
  // (session faulted, or stopped/purged via ModelChanged below) so a dead
  // session's memory is released at the moment it dies, not up to 5s later.
  let runStaleSweep () =
    let liveIds = retentionWorthySessionIds ()
    match sweepStaleSessionState liveIds liveBindingsAdaptive sharedFeatureState (elmRuntime.GetModel().RecentOutput) with
    | [] -> ()
    | stale -> log.LogInformation("Swept {Count} stale session state entries", stale.Length)

  // Evaluate the FULL memory-shedding policy (with the real "who is the
  // user looking at right now" flag, which only exists at this Elm-model
  // layer) and stop whatever it names — job 4, "shed load before the
  // machine dies." Run on the same periodic cadence as the stale-state
  // sweep, not on every model change: shedding idle SESSIONS (unlike
  // reaping already-dead state) is disruptive enough that it belongs on a
  // steady, low-frequency heartbeat rather than firing on every transition.
  let shedIdleSessionsIfNeeded () =
    let now = System.DateTime.UtcNow
    let model = elmRuntime.GetModel()
    let isUserActive sid = SageFs.ActiveSession.isViewing sid model.Sessions.ActiveSessionId
    let sessions =
      SessionManager.QuerySnapshot.allSessions (readSnapshot())
      |> List.map (memorySessionSnapshotOf now isUserActive)
    let rss = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64
    let machineMemory = Features.MachineMemory.current ()
    let machine : MemorySupervisor.MachineStats =
      { DaemonRssBytes = rss
        MachineAvailableBytes = machineMemory.AvailableBytes
        MachineTotalBytes = machineMemory.TotalBytes }
    let decision = Features.MemoryPressureWatch.evaluate MemorySupervisor.defaultThresholds machine sessions
    decision.Actions
    |> List.iter (function
      | MemorySupervisor.ShedAction.StopIdleSessions ids ->
        for idStr in ids do
          Log.warn "[MemorySupervisor] Shedding idle session %s under memory pressure (%s)" idStr (decision.Reason |> Option.defaultValue "")
          match WorkerProtocol.SessionId.validate idStr with
          | Ok sid ->
            sessionManager.PostAndAsyncReply(fun reply -> SessionManager.SessionCommand.StopSession(sid, reply))
            |> Async.StartAsTask
            |> ignore
          | Error _ -> ()
      | MemorySupervisor.ShedAction.ReapDeadSessions _
      | MemorySupervisor.ShedAction.RefuseNewSessions _ -> ())

  // Reconcile watchers AND stale retained state the moment a session's
  // lifecycle changes, instead of waiting on the periodic sync below.
  // `stop_session`/`create_session` (dashboard AND MCP alike) dispatch
  // `EditorAction.ListSessions`, which this same event stream turns into
  // `ModelChanged` — so a stopped session's directory claim is dropped, its
  // watcher disposed, AND its recent-output/adaptive-bindings state freed
  // within this subscription rather than up to 5s later on the periodic
  // sweep. `SessionFaulted` gets its own arm for the same reason: a fault
  // does not always also emit a `ModelChanged` on the same tick. Scoped to
  // just these two (not every event) so a hot path like FileReloaded
  // doesn't pay for a resync it has no reason to need.
  stateChangedEvent.Publish.Add(fun change ->
    match change with
    | ModelChanged _ ->
      seedSessionDirs ()
      runStaleSweep ()
    | SessionFaulted _ -> runStaleSweep ()
    | _ -> ())

  // Periodic session-watcher sync — ensures new sessions get watchers
  let mutable watcherSyncTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  let watcherSyncCallback _ =
    try
      seedSessionDirs ()
      runStaleSweep ()
      shedIdleSessionsIfNeeded ()
    finally
      match isNull watcherSyncTimerRef with
      | true -> ()
      | false ->
        try watcherSyncTimerRef.Change(5_000, System.Threading.Timeout.Infinite) |> ignore
        with :? System.ObjectDisposedException -> ()
  let watcherSyncTimer =
    let t = new System.Threading.Timer(
      System.Threading.TimerCallback(watcherSyncCallback),
      null, 5_000, System.Threading.Timeout.Infinite)
    watcherSyncTimerRef <- t
    t

  // Start dashboard web server on MCP port + 1
  let dashboardPort = mcpPort + 1
  let connectionTracker = ConnectionTracker()

  // TTL idle-check (ownership rule 2, §3.1): when this daemon carries a
  // --ttl (explicit, or the nested-checkout default), self-terminate once
  // there are no live sessions and no MCP/SSE clients for at least that
  // long. "Clients" is approximated from what the daemon already tracks —
  // connected dashboard tabs (ConnectionTracker) and recent MCP tool
  // activity (AgentActivityTracker) — rather than a new connection count.
  // One-shot timer, same reschedule-after-completion idiom as the other
  // periodic checks above.
  //
  // Two daemons observed live (--ttl 60m / --ttl 30m) outlived their TTL by
  // 4+ hours before being killed manually. Root-caused to two bugs in what
  // this callback fed `DaemonOwnership.shouldSelfTerminate` (the pure
  // decision itself was already correct and tested):
  //   1. `hasClients` used `ttl` ITSELF as the "still around" freshness
  //      window, so a single MCP call stayed "fresh" for a full extra `ttl`
  //      — and every tick that saw it re-stamped `ttlLastActiveAt` to `now`
  //      while it did, pushing genuine idle-out to roughly 2x`ttl` after the
  //      last real activity, worse with each subsequent call. Fixed:
  //      `DaemonOwnership.ttlClientActivityWindow` — a small, capped window.
  //   2. `hasLiveSessions` counted ANY registered session, including a
  //      `Faulted` tombstone SessionManager deliberately keeps registered
  //      after a worker crashes and exhausts its restart budget — one
  //      permanently-dead session blocked idle-out forever. Fixed:
  //      `DaemonOwnership.isUsableSessionStatus` excludes Faulted/Stopped.
  // A third, structural gap: on any exception this callback logged a
  // warning and kept retrying forever — fail-OPEN, the opposite of what a
  // daemon that "cannot determine its own deadline" must do. Fixed with
  // `OwnerMonitor.hasGivenUp`: after enough CONSECUTIVE failures to rule out
  // a transient blip, the daemon fails safe and exits rather than running
  // unbounded.
  let mutable ttlLastActiveAt = DateTime.UtcNow
  let mutable ttlConsecutiveFailures = 0
  let mutable ttlTimerRef : System.Threading.Timer = Unchecked.defaultof<_>
  let ttlTimer : System.Threading.Timer option =
    match ownership.Ttl with
    | None -> None
    | Some ttl ->
      let ttlCheckIntervalMs =
        max 1_000 (min 30_000 (int (ttl.TotalMilliseconds / 4.0)))
      let ttlCallback _ =
        try
          try
            let now = DateTime.UtcNow
            let hasLiveSessions =
              SessionManager.QuerySnapshot.allSessions (readSnapshot())
              |> List.exists (fun si -> DaemonOwnership.isUsableSessionStatus si.Status)
            let activityWindow = DaemonOwnership.ttlClientActivityWindow ttl
            let hasClients =
              connectionTracker.GetAllCounts().Browsers > 0
              || not (AgentActivityTracker.getActivePresences activityTracker None activityWindow now |> List.isEmpty)
            match hasLiveSessions || hasClients with
            | true -> ttlLastActiveAt <- now
            | false -> ()
            ttlConsecutiveFailures <- 0
            match DaemonOwnership.shouldSelfTerminate now ttl ttlLastActiveAt hasLiveSessions hasClients with
            | true ->
              log.LogWarning("Daemon idle for --ttl {Ttl} with no live sessions and no clients — self-terminating", ttl)
              try cts.Cancel() with :? ObjectDisposedException -> ()
            | false -> ()
          with ex ->
            ttlConsecutiveFailures <- ttlConsecutiveFailures + 1
            log.LogWarning(
              "TTL idle-check callback threw unexpectedly ({Count}/{Max} consecutive): {Error}",
              ttlConsecutiveFailures, OwnerMonitor.giveUpAfterFailures, ex.Message)
            match OwnerMonitor.hasGivenUp ttlConsecutiveFailures with
            | true ->
              log.LogWarning(
                "TTL idle-check has failed {Count} times in a row — this daemon cannot determine its own deadline; self-terminating (fail-safe) rather than running unbounded",
                ttlConsecutiveFailures)
              try cts.Cancel() with :? ObjectDisposedException -> ()
            | false -> ()
        finally
          if not (isNull ttlTimerRef) then
            try ttlTimerRef.Change(ttlCheckIntervalMs, System.Threading.Timeout.Infinite) |> ignore
            with :? System.ObjectDisposedException -> ()
      let t =
        new System.Threading.Timer(
          System.Threading.TimerCallback(ttlCallback),
          null, ttlCheckIntervalMs, System.Threading.Timeout.Infinite)
      ttlTimerRef <- t
      Some t

  // Dashboard status helpers — partially applied module-level functions
  let getSessionState = getSessionStateFromSnapshot readSnapshot
  let getEvalStatsAsync = getEvalStatsFromWorker sessionOps.GetProxy
  let getSessionWorkingDir = getSessionWorkingDirFromSnapshot readSnapshot
  let getStatusMsg = getStatusMsgFromSnapshot readSnapshot

  let sessionThemes = DashboardTypes.loadThemes DaemonState.SageFsDir

  let dashboardQueries : DashboardQueries = {
    GetSessionState = getSessionState
    GetStatusMsg = getStatusMsg
    GetEvalStats = getEvalStatsAsync
    GetFrictionStore = fun () -> task { return frictionStore }
    GetSessionWorkingDir = getSessionWorkingDir
    GetElmRegionsForSession = fun sessionId ->
      ElmDaemon.renderRegionsForSession elmRuntime (WorkerProtocol.SessionId.value sessionId) |> Some
    GetPreviousSessions = fun () ->
      getPreviousSessions manifestOwner readSnapshot
    GetAllSessions = fun () -> task { return SessionManager.QuerySnapshot.allSessions (readSnapshot()) }
    GetHotReloadState = fun sessionId ->
      fetchWorkerEndpoint sessionId "/hotreload" dashboardFetchTimeoutSec (fun resp ->
        use doc = Text.Json.JsonDocument.Parse(resp)
        let root = doc.RootElement
        let files =
          root.GetProperty("files").EnumerateArray()
          |> Seq.map (fun el ->
            {| path = el.GetProperty("path").GetString()
               watched = el.GetProperty("watched").GetBoolean() |})
          |> Seq.toList
        let watchedCount = root.GetProperty("watchedCount").GetInt32()
        // Initializers saves kept waiting for a reset (rule 3). A worker from
        // before rule 3 doesn't send `kept`, and that just means nothing's kept.
        let kept =
          match root.TryGetProperty "kept" with
          | true, arr when arr.ValueKind = Text.Json.JsonValueKind.Array ->
            arr.EnumerateArray()
            |> Seq.map (fun k ->
              ({ Binding = k.GetProperty("binding").GetString() |> Option.ofObj |> Option.defaultValue ""
                 KeptValue = k.GetProperty("keptValue").GetString() |> Option.ofObj |> Option.defaultValue ""
                 NewInitializer = k.GetProperty("newInitializer").GetString() |> Option.ofObj |> Option.defaultValue "" }
               : Features.ReloadOutcome.KeptValue))
            |> Seq.toList
          | _ -> []
        // Rule 2's reflection reads: the mode and the hot-loop questions. A
        // worker that doesn't send them gets no mode control, not a guessed one.
        let reflection =
          match root.TryGetProperty "reflectionReads" with
          | true, el ->
            match Features.KeptState.ReflectionReadsJson.parse el with
            | Ok report -> Features.KeptState.ReflectionReadsView.Reported report
            | Error why ->
              let why = Features.KeptState.ReflectionReadsError.describe why
              match el.TryGetProperty "unavailable" with
              | true, reason -> Features.KeptState.ReflectionReadsView.NotReported(reason.GetString() |> Option.ofObj |> Option.defaultValue why)
              | false, _ -> Features.KeptState.ReflectionReadsView.NotReported why
          | false, _ -> Features.KeptState.ReflectionReadsView.NotReported "this worker doesn't report reflection reads"
        {| files = files; watchedCount = watchedCount; kept = kept; reflection = reflection |})
    GetWarmupContext = fun sessionId ->
      fetchWorkerEndpoint sessionId "/warmup-context" dashboardFetchTimeoutSec
        (WorkerProtocol.Serialization.deserialize<WarmupContext>)
    GetWarmupProgress = fun sessionId ->
      let snapshot = readSnapshot()
      match Map.tryFind sessionId snapshot.WarmupProgress with
      | Some progress -> progress
      | None -> ""
    GetSessionTestSummary = fun sessionId ->
      let sidStr = WorkerProtocol.SessionId.value sessionId
      let lt = SageFsModel.cycleForSession sidStr (elmRuntime.GetModel())
      let state = lt.TestState
      match state.Activation with
      | Features.LiveTesting.LiveTestingActivation.Inactive -> None
      | _ ->
      let entries =
        Features.LiveTesting.LiveTestState.statusEntriesForSession sidStr state
      match entries.Length with
      | 0 -> None
      | _ ->
        Features.LiveTesting.TestSummary.fromStatuses
          state.Activation (entries |> Array.map (fun e -> e.Status))
        |> Some
    GetSessionCoverageSummary = fun sessionId ->
      let sidStr = WorkerProtocol.SessionId.value sessionId
      let lt = SageFsModel.cycleForSession sidStr (elmRuntime.GetModel())
      let state = lt.TestState
      match state.Activation with
      | Features.LiveTesting.LiveTestingActivation.Inactive -> None
      | _ ->
      // `state` belongs wholly to this session (see `SageFsModel.cycleForSession`).
      let bitmaps = state.TestCoverageBitmaps |> Map.values |> Seq.toArray
      match bitmaps.Length with
      | 0 -> None
      | _ ->
        Features.LiveTesting.CoverageSummary.fromBitmaps 16 (bitmaps |> Seq.ofArray)
        |> Some
    GetSessionTestTreemap = fun sessionId ->
      let sidStr = WorkerProtocol.SessionId.value sessionId
      let lt = SageFsModel.cycleForSession sidStr (elmRuntime.GetModel())
      let state = lt.TestState
      match state.Activation with
      | Features.LiveTesting.LiveTestingActivation.Inactive -> [||]
      | _ ->
      let entries =
        Features.LiveTesting.LiveTestState.statusEntriesForSession sidStr state
      Features.LiveTesting.TestTreemap.fromStatusEntries entries
    GetSessionCoverageTreemap = fun sessionId ->
      let sidStr = WorkerProtocol.SessionId.value sessionId
      let lt = SageFsModel.cycleForSession sidStr (elmRuntime.GetModel())
      match lt.TestState.Activation with
      | Features.LiveTesting.LiveTestingActivation.Inactive -> None
      | _ ->
      let maps =
        match Map.tryFind sidStr lt.InstrumentationMaps with
        | Some m when m.Length > 0 -> m
        | _ -> lt.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
      let merged = Features.LiveTesting.InstrumentationMap.merge maps
      match merged.Slots.Length = 0 with
      | true -> None
      | false ->
      // `lt.TestState` belongs wholly to this session (see `SageFsModel.cycleForSession`).
      let sessionTestIds = lt.TestState.TestCoverageBitmaps |> Map.keys |> Set.ofSeq
      let compatibleBitmap (tid: Features.LiveTesting.TestId) =
        Map.tryFind tid lt.TestState.TestCoverageBitmaps
        |> Option.filter (fun bm -> bm.Count = merged.TotalProbes)
      let sessionBitmaps = sessionTestIds |> Seq.choose compatibleBitmap |> Seq.toArray
      match sessionBitmaps.Length = 0 with
      | true -> None
      | false ->
      let combinedHits =
        sessionBitmaps
        |> Array.reduce (fun acc bm ->
          { acc with Bits = Array.init acc.Bits.Length (fun i -> acc.Bits.[i] ||| bm.Bits.[i]) })
        |> Features.LiveTesting.CoverageBitmap.toBoolArray
      let entries = Features.LiveTesting.LiveTestState.statusEntriesForSession sidStr lt.TestState
      let failedTestIds =
        entries
        |> Array.choose (fun e ->
          match e.Status with
          | Features.LiveTesting.TestRunStatus.Failed _ -> Some e.TestId
          | _ -> None)
        |> Set.ofArray
      let passedTestIds =
        entries
        |> Array.choose (fun e ->
          match e.Status with
          | Features.LiveTesting.TestRunStatus.Passed _ -> Some e.TestId
          | _ -> None)
        |> Set.ofArray
      // Probes hit by any failing test — these taint their file/symbol red
      // even where the merged (all-tests) coverage otherwise looks green.
      let failingSlotIndices =
        sessionTestIds
        |> Seq.filter failedTestIds.Contains
        |> Seq.choose compatibleBitmap
        |> Seq.collect (fun bm ->
          seq { for i in 0 .. merged.Slots.Length - 1 do
                  if Features.LiveTesting.CoverageBitmap.isSet i bm then yield i })
        |> Set.ofSeq
      let projectDirs =
        match SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) with
        | None -> []
        | Some info ->
          info.Projects
          |> List.map (fun p ->
            System.IO.Path.GetFileNameWithoutExtension p, System.IO.Path.GetDirectoryName p)
      let facts =
        merged.Slots
        |> Array.indexed
        |> Array.groupBy (fun (_, sp) -> sp.File)
        |> Array.map (fun (file, idxSps) ->
          let probeCount = idxSps.Length
          let coveredCount = idxSps |> Array.filter (fun (i, _) -> combinedHits.[i]) |> Array.length
          let hasFailing = idxSps |> Array.exists (fun (i, _) -> failingSlotIndices.Contains i)
          let projectName = Features.Treemap.CoverageTreemapNode.projectNameForFile projectDirs file
          let symbols =
            lt.AnalysisCache.FileSymbols
            |> Map.tryFind file
            |> Option.defaultValue []
            |> List.map (fun sr ->
              let testIds =
                lt.DepGraph.SymbolToTests |> Map.tryFind sr.SymbolFullName |> Option.defaultValue [||]
              ({ SymbolName = sr.SymbolFullName
                 Line = sr.Line
                 TestCount = testIds.Length
                 PassingCount = testIds |> Array.filter passedTestIds.Contains |> Array.length
                 FailingCount = testIds |> Array.filter failedTestIds.Contains |> Array.length }
               : Features.Treemap.SymbolCoverageFact))
          ({ FilePath = file
             ProjectName = projectName
             ProbeCount = probeCount
             CoveredCount = coveredCount
             HasFailingTest = hasFailing
             Symbols = symbols }
           : Features.Treemap.FileCoverageFact))
        |> Array.toList
      Some (Features.Treemap.CoverageTreemapNode.build "Solution" facts)
    GetSessionBindings = fun sessionId ->
      match System.Threading.Volatile.Read(&sharedBindingScope.contents) with
      | Some scope ->
        scope.ActiveBindings
        |> Map.values |> Array.ofSeq
      | None -> [||]
    GetBindingScopeSnapshot = fun () -> System.Threading.Volatile.Read(&sharedBindingScope.contents)
    GetLiveBindings = fun sessionId ->
      SageFs.Features.LiveBindingsAdaptive.tryGet liveBindingsAdaptive (WorkerProtocol.SessionId.value sessionId)
    GetLiveTestingStatus = fun () ->
      let model = elmRuntime.GetModel()
      let activeId =
        SageFs.ActiveSession.sessionId model.Sessions.ActiveSessionId
        |> Option.map WorkerProtocol.SessionId.value |> Option.defaultValue ""
      SageFs.Features.LiveTesting.LiveTestCycleState.liveTestingStatusBarForSession activeId model.LiveTesting
    GetLiveTestActivity = fun sessionId ->
      SageFsModel.liveTestActivityFor sessionId (elmRuntime.GetModel())
    GetLiveTestingActive = fun () ->
      let model = elmRuntime.GetModel()
      match model.LiveTesting.TestState.Activation with
      | SageFs.Features.LiveTesting.LiveTestingActivation.Active -> true
      | SageFs.Features.LiveTesting.LiveTestingActivation.Inactive -> false
    GetEvalTimeline = fun () ->
      let state = System.Threading.Volatile.Read(&sharedFeatureState.contents)
      SageFs.Features.EvalTimeline.timelineStats 20 state.CachedTimeline
    GetDaemonHealth = fun () ->
      let model = elmRuntime.GetModel()
      let sessions =
        model.Sessions.Sessions
        |> List.map (fun s ->
          let healthStatus : SageFs.Features.SessionHealthStatus =
            match s.Status with
            | SageFs.SessionDisplayStatus.Running -> SageFs.Features.SessionHealthStatus.Ready
            | SageFs.SessionDisplayStatus.Starting -> SageFs.Features.SessionHealthStatus.WarmingUp
            | SageFs.SessionDisplayStatus.Restarting -> SageFs.Features.SessionHealthStatus.WarmingUp
            | SageFs.SessionDisplayStatus.Faulted _ -> SageFs.Features.SessionHealthStatus.Faulted
            | SageFs.SessionDisplayStatus.Lost -> SageFs.Features.SessionHealthStatus.Faulted
            | SageFs.SessionDisplayStatus.Stopped -> SageFs.Features.SessionHealthStatus.Stopped
            // Idle is a normal, connected, working session that just hasn't
            // been used in a while — it is Ready, not Stopped.
            | SageFs.SessionDisplayStatus.Idle -> SageFs.Features.SessionHealthStatus.Ready
          let projectName =
            match s.Projects with
            | p :: _ -> System.IO.Path.GetFileName p
            | [] -> s.Name |> Option.defaultValue (WorkerProtocol.SessionId.value s.Id)
          ({ SessionId = WorkerProtocol.SessionId.value s.Id
             ProjectName = projectName
             Status = healthStatus
             EvalCount = s.EvalCount
             LastActivity = System.DateTimeOffset(s.LastActivity, System.TimeSpan.Zero) }
           : SageFs.Features.SessionHealthSummary))
      let testingSummary =
        let ts = model.LiveTesting.TestState
        match ts.Activation with
        | SageFs.Features.LiveTesting.LiveTestingActivation.Inactive -> None
        | SageFs.Features.LiveTesting.LiveTestingActivation.Active ->
          let entries = SageFs.Features.LiveTesting.LiveTestState.statusEntriesForSession "" ts
          match entries.Length with
          | 0 -> None
          | _ ->
            let summary = SageFs.Features.LiveTesting.TestSummary.fromStatuses ts.Activation (entries |> Array.map (fun e -> e.Status))
            Some ({ TotalTests = summary.Total
                    Passed = summary.Passed
                    Failed = summary.Failed
                    Running = summary.Running }
                  : SageFs.Features.LiveTestHealthSummary)
      // Resident memory, not the managed heap: the daemon that ate 51.7GB of a
      // 62GB machine looked fine by GC.GetTotalMemory. Every reading also
      // teaches the detector what this daemon's normal is.
      let rssBytes = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64
      let memoryMB = int (rssBytes / 1_048_576L)
      let rssVerdict =
        SageFs.Features.HealthWatch.observe
          SageFs.Features.HealthAnomaly.SignalId.WorkerRss
          (float memoryMB)
          System.DateTimeOffset.UtcNow
      // The moment RSS is first confirmed Broken (not every sample after) —
      // capture the evidence both incidents died without: by the time
      // anyone noticed the daemon was eating the machine, there was no
      // memory left to safely take a dump with. Off the hot path
      // (GcDumpWatch.maybeCapture backgrounds the actual capture), at most
      // once per daemon run, and only with enough machine headroom to try
      // safely — see GcDumpCapture's own doc comment.
      SageFs.Features.GcDumpWatch.maybeCapture
        (System.Diagnostics.Process.GetCurrentProcess().Id)
        (System.IO.Path.Combine(DaemonState.SageFsDir, "diagnostics"))
        rssBytes
        (SageFs.Features.MachineMemory.current ()).AvailableBytes
        SageFs.Features.HealthAnomaly.SignalId.WorkerRss
        rssVerdict
      // Same tick, same cheap in-memory read: CurrentQueueLength is a counter
      // the mailbox already maintains (checkMailboxAdmission reads the same
      // one), so this can never itself become the thing that starves the
      // daemon.
      sampleMailboxQueueDepth observeMailboxQueueDepthToHealthWatch (fun () -> sessionManager.CurrentQueueLength)
      Some ({ DaemonPid = System.Diagnostics.Process.GetCurrentProcess().Id
              DaemonPort = mcpPort
              Uptime = System.DateTimeOffset.UtcNow - daemonStartTime
              Version = version
              SessionSummaries = sessions
              LiveTestingSummary = testingSummary
              MemoryMB = memoryMB
              Anomalies = SageFs.Features.HealthWatch.troubled ()
              GcDumpOutcome = SageFs.Features.GcDumpWatch.lastCaptureOutcome ()
              // The same hysteresis-tracked level `shedIdleSessionsIfNeeded`
              // (watcherSyncTimer, every 5s) already maintains — reading it
              // here rather than recomputing keeps ONE authoritative level
              // instead of two that could disagree.
              MemoryPressure = SageFs.Features.MemoryPressureWatch.currentLevel () }
            : SageFs.Features.HealthSnapshot)
    GetFailureNarratives = fun () ->
      let model = elmRuntime.GetModel()
      let testState = model.LiveTesting.TestState
      match testState.Activation with
      | Features.LiveTesting.LiveTestingActivation.Inactive -> []
      | _ ->
      testState.Cached.FailureNarratives
      |> Map.toList
      |> List.choose (fun (testId, narrative) ->
        testState.DiscoveredTests
        |> Array.tryFind (fun tc -> tc.Id = testId)
        |> Option.map (fun tc -> tc.DisplayName, narrative))
    GetCurrentDiagnostics = fun () ->
      let model = elmRuntime.GetModel()
      model.Diagnostics
      |> Features.DiagnosticsStore.allFlat
      |> List.map Diagnostic.fromFeatureDiag
      |> List.sortBy (fun d -> match d.Severity with DiagError -> 0 | DiagWarning -> 1)
    GetFilmstripEntries = fun () ->
      let state = System.Threading.Volatile.Read(&sharedFeatureState.contents)
      // The 20 most recent evals, oldest first — the filmstrip shows its
      // newest frame last. (Reversing the whole history and taking 20 showed
      // the session's FIRST 20 evals forever, after walking up to 10k cells.)
      SageFs.Features.FeatureHooks.recentEvals 20 state
      |> List.map (fun entry ->
        let outcome =
          match entry.Result with
          | r when r.Contains("Operation was cancelled") -> EvalCancelled
          | r when r.Contains("error FS") || r.Contains("; error") || r.StartsWith("error") -> EvalError
          | _ -> EvalSuccess
        let label =
          let first = entry.Code.Split('\n') |> Array.tryHead |> Option.defaultValue ""
          if first.Length > 60 then first.[..59] else first
        { Index = entry.CellIndex
          Label = label
          DurationMs = entry.DurationMs
          Outcome = outcome
          Timestamp = entry.Timestamp })
    GetTestSourceLocations = fun () ->
      let model = elmRuntime.GetModel()
      model.ResolvedSourceLocations
    GetSessionAgentBadges = fun sessionId ->
      let presences =
        AgentActivityTracker.getActivePresences
          activityTracker (Some (WorkerProtocol.SessionId.value sessionId)) (TimeSpan.FromMinutes 5.0) DateTime.UtcNow
      presences
      |> List.map (fun p ->
        let freshness = SessionOperations.AgentPresence.freshness DateTime.UtcNow (TimeSpan.FromMinutes 2.0) p
        let cssClass =
          match freshness with
          | SessionOperations.AgentFreshness.Fresh -> "badge-agent"
          | SessionOperations.AgentFreshness.Stale -> "badge-agent badge-agent-stale"
        let intentLabel =
          match p.Intent with
          | Some i -> i
          | None -> ""
        let detail =
          match p.RecentFiles with
          | [] -> ""
          | files -> files |> List.truncate 3 |> String.concat ", "
        { Name = p.AgentName; IntentLabel = intentLabel; CssClass = cssClass; DetailLabel = detail })
    GetSessionGuidanceCss = fun sessionId ->
      let presences =
        AgentActivityTracker.getActivePresences
          activityTracker (Some (WorkerProtocol.SessionId.value sessionId)) (TimeSpan.FromMinutes 5.0) DateTime.UtcNow
      let workers =
        presences
        |> List.filter (fun p -> p.Role = SessionOperations.OccupantRole.Worker)
      match workers with
      | [] -> ""
      | _ -> "guidance-contested"
    GetSessionSelfHostStaleness = fun sessionId ->
      let snap = readSnapshot()
      // Only a session that adopted its own SageFs.Core carries an AdoptedCore
      // identity, so the on-disk scan runs ONLY for those (rare) self-hosting
      // sessions — the common dashboard tick pays nothing.
      match snap.AdoptedCore |> Map.tryFind sessionId with
      | None -> None
      | Some adopted ->
        match SessionManager.QuerySnapshot.tryGetSession sessionId snap with
        | None -> None
        | Some info ->
          SageFs.HostCoreAdoption.newestCandidateIdentity info.Projects
          |> SageFs.HostCoreAdoption.selfHostFreshness (Some adopted)
          |> SageFs.HostCoreAdoption.formatFreshnessAffordance
    GetSessionWorkflow = fun sessionId ->
      match SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) with
      | Some info -> info.Workflow
      | None -> WorkflowTypes.SessionWorkflow.Interactive
    GetSessionActiveProject = fun sessionId ->
      match SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) with
      | Some info -> info.ActiveProject
      | None -> None
    GetSessionProjectRoles = fun sessionId ->
      match SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) with
      | Some info -> info.ProjectRoles
      | None -> []
    GetSessionApp = fun sessionId ->
      match SessionManager.QuerySnapshot.tryGetSession sessionId (readSnapshot()) with
      | Some info -> info.App
      | None -> AppRun.AppRunState.NotRunning
    GetSessionEvalCounts = fun () ->
      // From the finished-eval counter the Elm update keeps. The registry's
      // own SessionSnapshot.EvalCount is never filled in (it's always 0).
      EvalTally.toList (elmRuntime.GetModel().EvalsFinished)
      |> List.choose (fun (key, n) ->
        match WorkerProtocol.SessionId.validate key with
        | Ok sid -> Some (sid, n)
        | Error _ -> None)
      |> Map.ofList
    IsCreatingSession = fun () -> elmRuntime.GetModel().CreatingSession
  }

  let dashboardActions : DashboardActions = {
    EvalCode = fun sid code -> task {
      let sidStr = WorkerProtocol.SessionId.value sid
      let! result = proxyToSession getProxyStr notifyWorkerDiedStr sidStr (WorkerProtocol.WorkerMessage.EvalCode(code, "dash"))
      match result with
      | Ok (WorkerProtocol.WorkerResponse.EvalResult(_, Ok msg, diags, _metadata)) ->
        let! _ =
          dispatchOutputAndWait
            elmRuntime
            stateChangedEvent.Publish
            sidStr
            (SageFsMsg.Event (
              TuiEvent.EvalCompleted (sidStr, msg, diags |> List.map WorkerProtocol.WorkerDiagnostic.toDiagnostic)))
        // Live binding watch window: pulled AFTER the eval reply, never
        // attached to it (roast-4 #2) — fire-and-forget so a slow or failed
        // reflection walk can never delay the caller's eval result. Fed into
        // the adaptive store; subscribers fire only on real change, and the
        // existing EvalCompleted → ModelChanged morph re-renders the panel.
        Async.Start (
          async {
            let! liveResult =
              proxyToSession getProxyStr notifyWorkerDiedStr sidStr
                (WorkerProtocol.WorkerMessage.GetLiveValues (sprintf "live-%s" sidStr))
              |> Async.AwaitTask
            match liveResult with
            | Ok (WorkerProtocol.WorkerResponse.LiveValuesResult(_, json)) ->
              try
                let snap =
                  WorkerProtocol.Serialization.deserialize<SageFs.Features.LiveValueTree.LiveValueSnapshot> json
                SageFs.Features.LiveBindingsAdaptive.update liveBindingsAdaptive sidStr { snap with SessionId = sidStr }
              with ex ->
                Log.warn "[DaemonMode] Failed to parse live value snapshot for %s: %s" sidStr ex.Message
            | Ok other ->
              Log.warn "[DaemonMode] Unexpected live-values response for %s: %A" sidStr other
            | Error e ->
              Log.warn "[DaemonMode] Live value pull failed for %s: %s" sidStr (SageFsError.describe e)
          })
        return Ok msg
      | Ok (WorkerProtocol.WorkerResponse.EvalResult(_, Error err, _, _)) ->
        let msg = SageFsError.describe err
        let! _ =
          dispatchOutputAndWait
            elmRuntime
            stateChangedEvent.Publish
            sidStr
            (SageFsMsg.Event (TuiEvent.EvalFailed (sidStr, msg)))
        return Error msg
      | Ok other -> return Error (sprintf "Unexpected: %A" other)
      | Error e -> return Error (SageFsError.describe e)
    }
    CancelEval = fun sid -> task {
      let sidStr = WorkerProtocol.SessionId.value sid
      let! result = proxyToSession getProxyStr notifyWorkerDiedStr sidStr WorkerProtocol.WorkerMessage.CancelEval
      return
        match result with
        | Ok (WorkerProtocol.WorkerResponse.EvalCancelled true) -> Ok "Cancellation requested — evaluation cancelled."
        | Ok (WorkerProtocol.WorkerResponse.EvalCancelled false) -> Ok "No evaluation in progress."
        | Ok other -> Error (sprintf "Unexpected: %A" other)
        | Error e -> Error (SageFsError.describe e)
    }
    ResetSession = fun sid -> task {
      let sidStr = WorkerProtocol.SessionId.value sid
      let! result = proxyToSession getProxyStr notifyWorkerDiedStr sidStr (WorkerProtocol.WorkerMessage.ResetSession "dash")
      return
        match result with
        | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Ok ())) -> Ok "Session reset successfully"
        | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Error e)) -> Error (sprintf "Reset failed: %A" e)
        | Ok other -> Error (sprintf "Unexpected: %A" other)
        | Error e -> Error (SageFsError.describe e)
    }
    HardResetSession = fun sid -> task {
      let! result = sessionOps.RestartSession sid true
      return
        result
        |> Result.map (sprintf "Hard reset: %s")
        |> Result.mapError (fun e -> sprintf "Hard reset failed: %s" (SageFsError.describe e))
    }
    Dispatch = fun msg -> elmRuntime.Dispatch msg
    SwitchSession = fun sid -> task {
      let sidStr = WorkerProtocol.SessionId.value sid
      elmRuntime.Dispatch(SageFsMsg.Event (TuiEvent.SessionSwitched (None, sidStr)))
      stateChangedEvent.Trigger(SessionSwitched sid)
      return Ok (sprintf "Switched to session '%s'" sidStr)
    }
    StopSession = fun sid -> task {
      let sidStr = WorkerProtocol.SessionId.value sid
      let! result = sessionOps.StopSession sidStr
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result |> Result.mapError SageFsError.describe
    }
    PurgeSession = fun sid -> task {
      let sidStr = WorkerProtocol.SessionId.value sid
      let! result = sessionOps.PurgeSession sidStr
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result |> Result.mapError SageFsError.describe
    }
    CreateSession = fun projects workingDir -> task {
      let! result = sessionOps.CreateSession projects workingDir WorkflowTypes.SessionWorkflow.Interactive
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result
        |> Result.map (fun sidStr -> WorkerProtocol.SessionId.validate sidStr |> Result.defaultValue (WorkerProtocol.SessionId.newId ()))
        |> Result.mapError SageFsError.describe
    }
    ShutdownCallback = Some (fun () -> cts.Cancel())
    RunApp = fun sid request -> task {
      let! result =
        AppRunOrchestration.runApp sessionOps (fun () -> DateTime.UtcNow) Timeouts.warmupReadyPollMax sid request
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result |> Result.map AppRun.describeState |> Result.mapError SageFsError.describe
    }
    StopApp = fun sid -> task {
      let! result = AppRunOrchestration.stopApp sessionOps sid
      elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
      return result |> Result.map AppRun.describeState |> Result.mapError SageFsError.describe
    }
  }

  let dashboardInfra : DashboardInfra = {
    Version = version
    McpPort = mcpPort
    StateChanged = stateChangedEvent.Publish
    ConnectionTracker = Some connectionTracker
    SessionThemes = sessionThemes
    GetSessionCount = fun () -> task {
      let! sessions = sessionOps.GetAllSessions()
      return sessions.Length
    }
    SystemAlarmBuffer =
      // Intercept SystemAlarm events and prepend to the buffer (max 3, newest-first).
      let buf : SageFs.Server.DashboardTypes.SystemAlarmEntry list ref = ref []
      stateChangedEvent.Publish.Add(fun change ->
        match change with
        | SystemAlarm (phase, msg) ->
          let entry : SageFs.Server.DashboardTypes.SystemAlarmEntry =
            { Phase = phase; Message = msg; Timestamp = System.DateTimeOffset.UtcNow }
          buf.Value <- (entry :: buf.Value) |> List.truncate 3
        | _ -> ())
      buf
    TriggerStateChange = fun () -> stateChangedEvent.Trigger (ModelChanged (0, 0))
    ActivityTracker = Some activityTracker
    LiveBindingsAdaptive = Some liveBindingsAdaptive
    ConnectionChannels = System.Collections.Concurrent.ConcurrentDictionary<string, MailboxProcessor<DashboardStreamCommand>>()
    // Wait-free (D4): dereferences CohortOwner's published frame pointer
    // directly — no mailbox round-trip, no IO.
    ReadCohortFrame = cohortOwner.ReadFrame
    ReadCohortLedger = cohortLedgerPort.ReadAll
    GetCompletions = fun (sessionId: WorkerProtocol.SessionId) (code: string) (cursorPos: int) -> task {
      try
        let! proxy = sessionOps.GetProxy sessionId
        match proxy with
        | Some send ->
          let replyId = sprintf "dash-comp-%d" (System.Random.Shared.Next())
          let! resp =
            send (WorkerProtocol.WorkerMessage.GetCompletions(code, cursorPos, replyId))
            |> Async.StartAsTask
          return
            match resp with
            | WorkerProtocol.WorkerResponse.CompletionResult(_, items) ->
              items |> List.map (fun label ->
                { SageFs.Features.AutoCompletion.DisplayText = label
                  SageFs.Features.AutoCompletion.ReplacementText = label
                  SageFs.Features.AutoCompletion.Kind = SageFs.Features.AutoCompletion.CompletionKind.Variable
                  SageFs.Features.AutoCompletion.GetDescription = None })
            | _ -> []
        | None -> return []
      with
      | :? System.Net.Http.HttpRequestException | :? Threading.Tasks.TaskCanceledException -> return []
      | ex ->
        Log.error "[getCompletions] Error for session: %s (%s)\n%s" ex.Message (ex.GetType().Name) (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        return []
    }
  }

  let dashboardEndpoints =
    Dashboard.createEndpoints dashboardQueries dashboardActions dashboardInfra

  let hotReloadProxyEndpoints = createHotReloadProxyEndpoints getWorkerBaseUrl httpClient stateChangedEvent

  let dashboardTask =
    startDashboardServer log bindHost daemonOrigins dashboardPort (dashboardEndpoints @ hotReloadProxyEndpoints) cts.Token

  // Workers handle their own warmup, middleware, and file watching.
  // The daemon just needs to wait for the MCP and dashboard servers.

  Console.CancelKeyPress.Add(fun e ->
    e.Cancel <- true
    log.LogInformation("Shutting down...")
    // Start a watchdog — if graceful shutdown takes too long, force exit
    System.Threading.Tasks.Task.Delay(5000).ContinueWith(fun (_: System.Threading.Tasks.Task) ->
      log.LogWarning("Graceful shutdown timed out — forcing exit")
      // StopAll has its own graceful budget, but timer and watcher cleanup can
      // consume the watchdog window before it runs. Sweep the session PIDs from
      // the lock-free snapshot before exiting so the daemon never leaves its
      // workers behind when graceful shutdown is delayed or wedged.
      readSnapshot()
      |> SessionManager.QuerySnapshot.allSessions
      |> List.choose (fun session -> WorkerProtocol.SessionLifecycleStatus.workerPid session.Status)
      |> SessionManager.killWorkerPids
      Environment.Exit(1)) |> ignore
    try cts.Cancel() with :? ObjectDisposedException -> ())

  AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
    log.LogInformation("Daemon stopped")
    // Belt-and-suspenders for issue #126: if the process is exiting through a
    // path that skipped the graceful StopAll (e.g. Environment.Exit elsewhere,
    // taskkill without /F), sweep worker PIDs so they don't become orphans.
    // The worker-side parent-death watchdog (WorkerMain.ParentMonitor) is the
    // primary defense for hard kills that skip ProcessExit entirely.
    readSnapshot()
    |> SessionManager.QuerySnapshot.allSessions
    |> List.choose (fun session -> WorkerProtocol.SessionLifecycleStatus.workerPid session.Status)
    |> SessionManager.killWorkerPids)

  // Start MCP and dashboard servers FIRST so ports are listening
  let mcpRunning =
    System.Threading.Tasks.Task.Run(
      System.Func<System.Threading.Tasks.Task>(fun () -> mcpTask),
      cts.Token)
  let dashboardRunning =
    System.Threading.Tasks.Task.Run(
      System.Func<System.Threading.Tasks.Task>(fun () -> dashboardTask),
      cts.Token)

  // Brief yield to let servers bind their ports
  do! System.Threading.Tasks.Task.Delay(200)

  // Both host tasks run until `cts` is cancelled — completing on their own
  // this early means a required listener's bind failed (each one already
  // logged why, above). Reporting "ready" over a listener that never bound
  // left every client/test polling a port nobody would ever answer on until
  // ITS OWN timeout, instead of a daemon that fails fast and says why.
  match listenerBindFailureOf mcpRunning.IsCompleted dashboardRunning.IsCompleted with
  | Some failure ->
    log.LogError(
      "SageFs daemon failed to start: {Listener} did not stay up (see the bind error logged above). Exiting.",
      describeListenerBindFailure failure)
    Environment.Exit(listenerBindFailureExitCode)
  | None -> ()

  startupSw.Stop()
  Instrumentation.startupDurationMs.Record(startupSw.Elapsed.TotalMilliseconds)
  match isNull startupSpan |> not with
  | true ->
    startupSpan.SetTag("startup_ms", startupSw.Elapsed.TotalMilliseconds) |> ignore
    Instrumentation.succeedSpan startupSpan
  | false -> ()
  log.LogInformation("SageFs daemon ready in {StartupMs:F0}ms (PID {Pid}, MCP port {McpPort}, dashboard port {DashboardPort})",
    startupSw.Elapsed.TotalMilliseconds, Environment.ProcessId, mcpPort, dashboardPort)
  log.LogInformation("Dashboard: http://localhost:{Port}/dashboard", dashboardPort)
  log.LogInformation("SSE events: http://localhost:{Port}/events", mcpPort)
  log.LogInformation("Health: http://localhost:{Port}/health", mcpPort)

  // Ownership rules 2 & 3 (§3.1): write a daemon-info file into this
  // daemon's own data dir — what `sagefs sweep` reads when the HTTP
  // /api/daemon-info side is wedged — and, when this daemon's data dir
  // isn't the real ~/.SageFs (an isolated SAGEFS_DATA_DIR, e.g. a test or
  // an agent's throwaway daemon), ALSO register a copy under the real
  // ~/.SageFs/spawned/ so sweep can find it without scanning the
  // filesystem for arbitrary data dirs.
  let daemonInfoFile : DaemonOwnership.DaemonInfoFile =
    { Pid = Environment.ProcessId
      StartTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
      OwnerPid = ownership.OwnerPid
      OwnerStart = ownership.OwnerStartTicks
      McpPort = mcpPort
      DashboardPort = dashboardPort
      DataDir = DaemonState.SageFsDir }
  try
    DaemonOwnership.DaemonInfoFile.write DaemonState.SageFsDir daemonInfoFile
    let realDir = DaemonOwnership.realHomeSageFsDir ()
    match String.Equals(System.IO.Path.GetFullPath DaemonState.SageFsDir, System.IO.Path.GetFullPath realDir, StringComparison.OrdinalIgnoreCase) with
    | true -> ()
    | false -> DaemonOwnership.registerSpawned realDir daemonInfoFile
  with ex ->
    log.LogWarning("Could not write daemon-info file: {Error}", ex.Message)

  // Cleanup orphaned .tmp files from interrupted writes
  // (.sagefs per-session replay files no longer exist — only the .sagetc test cache)
  let stcOrphans = Features.TestCacheFile.cleanupOrphanedTmpFiles DaemonState.SageFsDir
  match stcOrphans > 0 with
  | true ->
    Instrumentation.persistenceOrphanedTmpCleanup.Add(int64 stcOrphans, System.Collections.Generic.KeyValuePair("format", box "stc1"))
    log.LogInformation("Cleaned up {Count} orphaned .tmp files ({Stc} .sagetc)", stcOrphans, stcOrphans)
  | false -> ()

  // Test cache pre-loading happens per-session after resume, not eagerly

  // Resume sessions in background — don't block the daemon main task.
  // Each resumed session dispatches ListSessions so dashboard sees them incrementally.
  let _resumeTask =
    System.Threading.Tasks.Task.Run(fun () ->
      task {
        try
          match noResume with
          | true ->
            log.LogInformation("Session resume skipped (--no-resume)")
          | false ->
            do! resumeSessions (fun () ->
              elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions))

          // Load cached test state after sessions are restored
          let activeSessions = SessionManager.QuerySnapshot.allSessions (readSnapshot())
          let uniqueProjectSets =
            activeSessions
            |> List.map (fun s -> s.Projects)
            |> List.distinctBy (fun ps ->
              ps |> List.sort |> List.map (fun p -> p.Replace("\\", "/").ToLowerInvariant()) |> String.concat "|")
          for projects in uniqueProjectSets do
            match Features.DaemonPersistence.loadTestCache DaemonState.SageFsDir projects with
            | Ok cachedState ->
              log.LogInformation("Restored test cache ({CoverageCount} coverage, {ResultCount} results)",
                cachedState.TestCoverageBitmaps.Count, cachedState.LastResults.Count)
              elmRuntime.Dispatch(SageFsMsg.RestoreTestCache cachedState)
              let (Features.LiveTesting.RunGeneration gen) = cachedState.LastGeneration
              match gen > System.Threading.Volatile.Read(&lastSavedGeneration.contents) with
              | true -> System.Threading.Volatile.Write(&lastSavedGeneration.contents, gen)
              | false -> ()
            | Error msg -> log.LogDebug("No test cache available: {Reason}", msg)
        with ex ->
          log.LogWarning("Session resume failed: {Error}", ex.Message)
      } :> System.Threading.Tasks.Task)

  // Periodic status polling — refreshes session status (Starting → Ready)
  // so SSE subscribers see warmup progress in real time.
  // 10s interval: steady-state polling is cheap (short-circuited when unchanged)
  // but avoids the 2s cascade that dominated the render budget.
  let _statusPollTask =
    System.Threading.Tasks.Task.Run(fun () ->
      task {
        try
          while not cts.Token.IsCancellationRequested do
            do! System.Threading.Tasks.Task.Delay(10_000, cts.Token)
            elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)
        with
        | :? OperationCanceledException -> ()
        | ex -> log.LogWarning("Status poll failed: {Error}", ex.Message)
      } :> System.Threading.Tasks.Task)

  try
    let! _ = System.Threading.Tasks.Task.WhenAny(mcpRunning, dashboardRunning)
    ()
  with
  | :? OperationCanceledException -> ()

  // Graceful shutdown: stop test cycle timer, file watcher, and all sessions
  // W30(R12): Use Dispose(WaitHandle) for testCycleTimer — matches cacheSaveTimer treatment.
  // Bare Dispose() returns immediately; any in-flight 200ms tick callback could still be running
  // and call elmRuntime.Dispatch() after the elm runtime starts shutting down.
  match! disposeTimerAndWait testCycleTimer (TimeSpan.FromSeconds 3.0) with
  | TimerStop.StillRunning -> log.LogWarning("testCycleTimer shutdown wait timed out — callback may still be running")
  | TimerStop.Joined -> ()
  // Wait for an in-flight cacheSaveCallback before performGracefulShutdown writes the
  // manifest, so a late periodic save cannot overwrite its StoppedAt stamps.
  match! disposeTimerAndWait cacheSaveTimer (TimeSpan.FromSeconds 5.0) with
  | TimerStop.StillRunning -> log.LogWarning("cacheSaveTimer shutdown wait timed out — callback may still be running")
  | TimerStop.Joined -> ()
  // Dispose activity cleanup timer (best-effort, no wait needed — cleanup is idempotent)
  try activityCleanupTimer.Dispose()
  with :? System.ObjectDisposedException -> ()
  // Dispose the orphaned-temp-dir sweep timer (best-effort — the sweep itself is idempotent)
  try orphanTempDirSweepTimer.Dispose()
  with :? System.ObjectDisposedException -> ()
  // Dispose the cohort lease reaper (best-effort — Tick/RenewLease are idempotent)
  try cohortReaperTimer.Dispose()
  with :? System.ObjectDisposedException -> ()
  // Also what roots frictionPruneTimer for the daemon's lifetime (see its def).
  try frictionPruneTimer.Dispose()
  with :? System.ObjectDisposedException -> ()
  try watcherSyncTimer.Dispose()
  with :? System.ObjectDisposedException -> ()
  // #82: also roots appOutputFlushTimer for the daemon's lifetime (see its def).
  try appOutputFlushTimer.Dispose()
  with :? System.ObjectDisposedException -> ()
  match ttlTimer with
  | Some t ->
    try t.Dispose()
    with :? System.ObjectDisposedException -> ()
  | None -> ()
  (liveTestWatcherManager :> System.IDisposable).Dispose()
  // Ownership rule 3 (§3.1): a daemon that shut down gracefully is no
  // longer a sweep target — remove its own daemon-info file and, if it was
  // registered externally, its spawned/ registry entry.
  try
    DaemonOwnership.DaemonInfoFile.delete DaemonState.SageFsDir
    DaemonOwnership.unregisterSpawned (DaemonOwnership.realHomeSageFsDir ()) Environment.ProcessId
  with ex ->
    log.LogWarning("Could not clean up daemon-info file: {Error}", ex.Message)
  try
    do! performGracefulShutdown log readSnapshot elmRuntime.GetModel sessionManager manifestOwner
  with ex ->
    log.LogWarning("Shutdown cleanup error: {Error}", ex.Message)
}
