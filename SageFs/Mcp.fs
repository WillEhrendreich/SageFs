namespace SageFs

#nowarn "3511"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open System.Xml.Linq
open SageFs
open SageFs.AppState
open SageFs.WarmUp
open SageFs.Features.CellDependenciesReport
open SageFs.Utils
open SageFs.McpSessionRouting
open SageFs.McpRouteError

/// MCP tool implementations — all tools route through SessionManager.
/// There is no "local embedded session" — every session is a worker.
module McpTools =

  open System.Threading

  type McpContext = {
    /// SQLite-backed friction store for durable telemetry persistence.
    FrictionStore: Features.FrictionSqlite.FrictionStore option
    DiagnosticsChanged: IEvent<Features.DiagnosticsStore.T>
    /// Fires serialized JSON whenever the Elm model changes.
    StateChanged: IEvent<string> option
    SessionOps: SessionManagementOps
    /// Per-connection session tracking, keyed by bound MemberId.
    SessionMap: Collections.Concurrent.ConcurrentDictionary<string, string>
    /// MCP port for status display.
    McpPort: int
    /// Elm loop dispatch function (daemon mode).
    Dispatch: (SageFsMsg -> unit) option
    /// Read the current Elm model (daemon mode).
    GetElmModel: (unit -> SageFsModel) option
    /// Read the current render regions (daemon mode).
    GetElmRegions: (unit -> RenderRegion list) option
    /// Fetch warmup context for a session (daemon mode).
    GetWarmupContext: (string -> Threading.Tasks.Task<WarmupContext option>) option
    /// Read the current feature push state (eval history, bindings, timeline).
    GetFeatureState: (unit -> Features.FeatureHooks.FeaturePushState) option
    /// Record a completed eval (code, result, durationMs) into the feature push
    /// state's history — the WRITE counterpart to GetFeatureState. The daemon
    /// wires this to the same `FeaturePushState ref` its `/exec` bridge writes,
    /// so the pure-MCP `send_fsharp_code` path populates the eval history that
    /// `get_recent_fsi_events`/filmstrip/impact_forecast read (roast-7 §2/§3
    /// follow-up — before this, only the CLI-integrated `/exec` client wrote,
    /// so a pure-MCP agent's `get_recent_fsi_events` lied "no events yet" right
    /// after a successful eval). `None` in tests, like LiveSnapshotSink.
    RecordEval: (string -> string -> int64 -> unit) option
    /// In-memory agent activity tracker for multi-agent coordination.
    ActivityTracker: AgentActivityTracker.Tracker
    /// The live-bindings store, the pane's notes and the config's walk mode. Each successful eval pulls the session's live
    /// values into it (the daemon wires it; None in tests).
    LiveBindings: Features.LiveBindingsPane.Hub option
    /// The single per-daemon cohort owner (cohort-integration-plan.md Slice 2,
    /// item 9). `None` when no cohort owner was wired (most existing unit
    /// tests, which predate cohort support and never construct one) — cohort
    /// tools report a structured error rather than throwing in that case.
    CohortOwner: Features.CohortOwner.Handle option
    GetDaemonHealth: unit -> Features.HealthSnapshot option
    GetProcessTelemetry: unit -> SageFs.Server.DaemonTelemetry.Snapshot option
  }

  /// The MCP transport's per-connection identity, bound by the request
  /// filter (McpServer.createServerCaptureFilter) before a tool body runs —
  /// never by the tool's self-declared `agentName` argument. AsyncLocal, not
  /// a parameter: it flows through the same await chain Activity.Current
  /// already rides in this codebase (Instrumentation.fs), so the ~40 MCP
  /// tool method signatures (whose only caller-supplied "identity" is
  /// agentName) never need touching to close the identity-spoofing gap.
  /// None outside any bound connection (direct in-process calls, and every
  /// Expecto unit test that calls these functions directly rather than
  /// through the ASP.NET Core / MCP SDK pipeline) — see `memberIdFor`.
  let currentTransportSessionId = new System.Threading.AsyncLocal<string option>()

  /// Resolve the BOUND identity for a self-declared agent name
  /// (sagefs-multiagent-vision.md §4.1: "identity is bound to the
  /// connection, not declared"). When a real MCP connection is bound, the
  /// ambient transport session id wins — a caller can never spoof or evict
  /// another connection's presence just by choosing the same agentName,
  /// because the key is the connection, not the name. With no bound
  /// connection (tests, direct calls), falls back to a Minted identity keyed
  /// on the name itself, whose resolved key renders IDENTICAL to the plain
  /// name (MemberTable.MemberId.display) — every existing name-keyed test
  /// and call site keeps working unchanged.
  let memberIdFor (agentName: string) : MemberTable.MemberId =
    match currentTransportSessionId.Value with
    | Some tsid when not (String.IsNullOrWhiteSpace tsid) -> MemberTable.MemberId.Mcp tsid
    | _ -> MemberTable.MemberId.Minted agentName

  /// The resolved routing/presence key for a self-declared agent name — see
  /// `memberIdFor`. This is what SessionMap and ActivityTracker are keyed by,
  /// NOT the raw agentName.
  let resolvedKey (agentName: string) : string =
    MemberTable.MemberId.display (memberIdFor agentName)

  /// Sessions created by MCP, keyed by session id and owned by the bound
  /// transport MemberId. This is separate from SessionMap: switching a
  /// connection to an existing session must not transfer ownership.
  let sessionOwners = Collections.Concurrent.ConcurrentDictionary<string, string>()

  let claimSessionOwner (agent: string) (sessionId: string) =
    sessionOwners.TryAdd(sessionId, resolvedKey agent) |> ignore

  let releaseSessionOwner (sessionId: string) =
    let mutable removed = Unchecked.defaultof<string>
    sessionOwners.TryRemove(sessionId, &removed) |> ignore

  let ownsMcpSession sessionId =
    match currentTransportSessionId.Value with
    | None -> None
    | Some _ ->
      match sessionOwners.TryGetValue sessionId with
      | true, owner when owner = resolvedKey "mcp" -> Some true
      | _ -> Some false

  /// Get the active session ID for a specific agent/client.
  let activeSessionId (ctx: McpContext) (agent: string) =
    match ctx.SessionMap.TryGetValue(resolvedKey agent) with
    | true, sid -> sid
    | _ -> ""

  let featureStates = Collections.Concurrent.ConcurrentDictionary<string, Features.FeatureHooks.FeaturePushState ref>()

  let featureStateForSession (ctx: McpContext) sessionId =
    match currentTransportSessionId.Value with
    | Some _ ->
      match featureStates.TryGetValue sessionId with
      | true, state -> Some state.Value
      | false, _ ->
        let state = ref Features.FeatureHooks.FeaturePushState.empty
        featureStates.TryAdd(sessionId, state) |> ignore
        Some state.Value
    | None ->
      ctx.GetFeatureState |> Option.map (fun getState -> getState ())

  let featureStateForCaller (ctx: McpContext) =
    let key =
      match currentTransportSessionId.Value with
      | Some _ -> resolvedKey "mcp"
      | None -> "mcp"
    match ctx.SessionMap.TryGetValue key with
    | true, sessionId -> featureStateForSession ctx sessionId
    | false, _ -> ctx.GetFeatureState |> Option.map (fun getState -> getState ())

  let recordEvalForSession (ctx: McpContext) sessionId code result durationMs =
    match currentTransportSessionId.Value with
    | Some _ ->
      let state = featureStates.GetOrAdd(sessionId, fun _ -> ref Features.FeatureHooks.FeaturePushState.empty)
      state.Value <- Features.FeatureHooks.recordEval code result durationMs state.Value
    | None ->
      ctx.RecordEval |> Option.iter (fun record -> record code result durationMs)

  /// Set the active session ID for a specific agent/client.
  /// An empty session id CLEARS the agent's mapping instead of storing an
  /// empty-string entry — the old unconditional write left the key present,
  /// so dead-session references accumulated in the map forever.
  let setActiveSessionId (ctx: McpContext) (agent: string) (sid: string) =
    let key = resolvedKey agent
    match sid with
    | "" -> ctx.SessionMap.TryRemove(key) |> ignore
    | _ -> ctx.SessionMap.[key] <- sid

  /// Remove every agent→session entry that points at the given session id.
  /// Called when a session is stopped or purged so references to a dead
  /// session do not linger in SessionMap (previously there was no TryRemove
  /// anywhere — setActiveSessionId was the only write).
  let evictSessionEntries (ctx: McpContext) (sessionId: string) =
    ctx.SessionMap
    |> Seq.iter (fun kv ->
      if kv.Value = sessionId then
        ctx.SessionMap.TryRemove(kv.Key) |> ignore)

  /// Staleness horizon for SessionMap occupancy claims. Matches the
  /// AgentActivityTracker cleanup window (DaemonMode's periodic sweep evicts
  /// agents idle longer than this): an agent that has not recorded a tool
  /// call in this long is treated as gone.
  let private staleAgentTimeout = Timeouts.agentPresenceEviction

  /// Opportunistic SessionMap eviction, run at the reads that surface
  /// occupancy (list_sessions / get_fsi_status) so the map converges no
  /// matter which path stopped the session (MCP tool, HTTP route, dashboard,
  /// worker death):
  ///  - dead-target entries: mapping points at a session not in the live
  ///    registry (stopped/purged) → removed.
  ///  - stale agents: the agent's tracked presence (AgentActivityTracker)
  ///    is older than staleAgentTimeout → removed, so a dead/disconnected
  ///    agent stops claiming "OccupiedBy: X" on sessions it no longer uses.
  /// Agents with no recorded presence are kept — they may be legitimate
  /// routing-only clients (e.g. the "http"/"cli-integrated" aliases) that
  /// never fire a presence-recording tool call. `exemptAgent` (the caller,
  /// provably alive mid-call) is never pruned.
  let pruneSessionMap
    (ctx: McpContext)
    (liveSessionIds: Set<string> option)
    (exemptAgent: string option)
    (now: DateTime) =
    // exemptAgent arrives as a caller-declared name; SessionMap/ActivityTracker
    // are keyed by the resolved (connection-bound) key — resolve once so the
    // comparison below compares like with like.
    let exemptKey = exemptAgent |> Option.map resolvedKey
    ctx.SessionMap
    |> Seq.iter (fun kv ->
      let targetDead =
        match liveSessionIds with
        | Some live -> not (live.Contains kv.Value)
        | None -> false
      let agentStale =
        exemptKey <> Some kv.Key
        && (match AgentActivityTracker.getPresence ctx.ActivityTracker kv.Key with
            | Some presence -> SessionOperations.AgentPresence.isStale now staleAgentTimeout presence
            | None -> false)
      if targetDead || agentStale then
        ctx.SessionMap.TryRemove(kv.Key) |> ignore)

  /// Occupancy for one session, read from ActivityTracker — the ONE store
  /// shared with the dashboard (DaemonMode.fs threads the same Tracker
  /// instance into both McpContext.ActivityTracker and
  /// DashboardInfra.ActivityTracker) — never from SessionMap, which is
  /// MCP-routing-only and has no notion of a browser tab. A dashboard tab
  /// viewing a session therefore appears here as a Browser member exactly
  /// like an MCP agent does.
  let occupantsForSession (ctx: McpContext) (sessionId: string) : SessionOperations.SessionOccupancy list =
    AgentActivityTracker.getActivePresences ctx.ActivityTracker (Some sessionId) staleAgentTimeout DateTime.UtcNow
    |> List.map (fun p -> ({ AgentName = p.AgentName; Role = p.Role } : SessionOperations.SessionOccupancy))

  /// Per-session compilation context state (evaluated modules, file cache).
  let compilationStates =
    Collections.Concurrent.ConcurrentDictionary<string, Middleware.CompilationContext.CompilationState>()

  /// Per-session TypeLoadException diagnostic.
  /// When a TypeLoadException poisons an FSI session, the diagnostic string is stored here
  /// so that targeted_verify and get_fsi_status can report the compromised type identity.
  /// Cleared on hard_reset_fsi_session (which creates a fresh FSI session).
  let typeIdentityDiagnostics =
    Collections.Concurrent.ConcurrentDictionary<string, string>()


  // Session working-directory routing/matching helpers moved to
  // SageFs/McpSessionRouting.fs (pure, testable; roast-8 §2 god-file split).
  // Re-exposed via `open SageFs.McpSessionRouting` above so call sites here are
  // unchanged: normalizePath, sessionsMatchingWorkingDir[Deep[With]],
  // crossesCheckoutBoundaryWith, formatExistingSessionsHint,
  // resolveSessionByWorkingDir, formatWorkingDirectoryAmbiguity.

  /// Notify the Elm loop of an event (fire-and-forget, no-op if no dispatch).
  let notifyElm (ctx: McpContext) (event: TuiEvent) =
    ctx.Dispatch
    |> Option.iter (fun dispatch ->
      dispatch (SageFsMsg.Event event))

  // RouteError (routeToSession's failure channel) + its pure classifiers
  // (routeErrorMessage, routeErrorIsTransportFailure, routeErrorToSageFsError,
  // innermostException, tryMapTransportFailure) moved to SageFs/McpRouteError.fs
  // (roast-8 §2 god-file split), re-exposed via `open SageFs.McpRouteError`
  // above so routeToSession and its consumers here are unchanged.

  /// Route a WorkerMessage to a specific session via proxy, abandoning the
  /// worker call when `cancellation` fires. A cancelled call is re-raised as an
  /// OperationCanceledException and is never mapped to a transport failure, so
  /// it cannot mark the session Faulted or tell the SessionManager the worker
  /// died: abandoning a call says nothing about the worker.
  let routeToSessionWithin
    (ctx: McpContext)
    (cancellation: System.Threading.CancellationToken)
    (sessionId: string)
    (msg: WorkerProtocol.SessionId -> WorkerProtocol.WorkerMessage)
    : Task<Result<WorkerProtocol.WorkerResponse, RouteError>> =
    task {
      match WorkerProtocol.SessionId.validate sessionId with
      | Error e -> return Error (Message (sprintf "Invalid session ID: %s" e))
      | Ok validId ->
        let! proxy = ctx.SessionOps.GetProxy validId
        match proxy with
        | None ->
          let! info = ctx.SessionOps.GetSessionInfo validId
          match info with
          | Some i when (match i.Status with
                         | WorkerProtocol.SessionLifecycleStatus.Starting _
                         | WorkerProtocol.SessionLifecycleStatus.Restarting _ -> true
                         | _ -> false) ->
            return Result.Error (Message (sprintf "Session '%s' is still warming up (%s). This typically takes 15-30s for test projects. Call get_session_status with wait_seconds=60 to wait for readiness; do not sleep or poll. Do NOT create a new session — it will compete for resources and make warmup slower." sessionId (WorkerProtocol.SessionLifecycleStatus.label i.Status)))
          | _ ->
            return Result.Error (Message (sprintf "Session '%s' not found" sessionId))
        | Some send ->
          let replyId = WorkerProtocol.SessionId.newId()
          try
            let! response = Async.StartAsTask(send (msg replyId), cancellationToken = cancellation)
            return Result.Ok response
          with
          | :? OperationCanceledException as cancellation ->
            return raise cancellation
          | ex ->
            match tryMapTransportFailure sessionId ex with
            | Some transportError ->
              // INVARIANT (reader cannot fault a starting/restarting session):
              // a transport failure observed while the SessionManager is
              // deliberately respawning the worker is expected — the worker is
              // being swapped. It must NOT NotifyWorkerDied (which posts a
              // synthetic WorkerExited(pid=-1) that the stale-pid guards do not
              // catch and which can schedule a second restart) and must NOT mark
              // the session Faulted. Only the restart owner faults a restarting
              // session.
              // Discriminator: a daemon-owned cold restart with no prior
              // worker is Restarting with no pid to guard (SessionManager
              // cold-restart path — see RestartSession/ScheduleRestart).
              // A caller-driven reset (resetSession / hardReset rebuild=false)
              // flips Status via UpdateSessionStatus, which PRESERVES the
              // worker handle (see startingWhileReset), so its pid is never None
              // — a transport failure there is a real worker death and must
              // trigger NotifyWorkerDied recovery.
              let! info = ctx.SessionOps.GetSessionInfo validId
              match info with
              | Some i when (match i.Status with WorkerProtocol.SessionLifecycleStatus.Restarting WorkerProtocol.PreviousWorker.ColdStart -> true | _ -> false) ->
                return Error (RestartInProgress (sprintf "Session '%s' is %s — transport is temporarily unavailable by design. Call get_session_status with wait_seconds=60 to wait for it; do NOT retry hard_reset_fsi_session or create a new session." sessionId (WorkerProtocol.SessionLifecycleStatus.label i.Status)))
              | _ ->
                ctx.SessionOps.NotifyWorkerDied validId
                do! ctx.SessionOps.UpdateSessionStatus validId (WorkerProtocol.SessionLifecycleStatus.Faulted (WorkerProtocol.FaultReason.report (routeErrorMessage transportError)))
                return Result.Error transportError
            | None ->
              return raise ex
    }

  /// Route a WorkerMessage to a specific session via proxy.
  let routeToSession
    (ctx: McpContext)
    (sessionId: string)
    (msg: WorkerProtocol.SessionId -> WorkerProtocol.WorkerMessage)
    : Task<Result<WorkerProtocol.WorkerResponse, RouteError>> =
    routeToSessionWithin ctx System.Threading.CancellationToken.None sessionId msg

  /// Typed outcome of resolving which session a tool call should target.
  /// Guidance text is a pure function of this union: a session that exists in
  /// the registry is never reported as gone. `Gone` is produced only when the
  /// session is genuinely absent (never created, or explicitly stopped).
  type SessionResolution =
    | Routable of sessionId: string
    | WarmingUp of sessionId: string * status: WorkerProtocol.SessionLifecycleStatus
    | Unroutable of sessionId: string * status: WorkerProtocol.SessionLifecycleStatus
    | FaultedSession of sessionId: string * cause: FaultCause
    | Gone of message: string

  /// Pure classification: decide the resolution from registry knowledge.
  /// INVARIANT: `Gone` is produced only when the session is absent from the
  /// registry; an existing session is always Routable, WarmingUp, Unroutable,
  /// or FaultedSession — never Gone.
  let classifySessionAvailability
    (info: WorkerProtocol.SessionInfo option)
    (proxyAvailable: bool)
    : SessionResolution =
    match info with
    | Some i when proxyAvailable -> Routable (WorkerProtocol.SessionId.value i.Id)
    | Some i ->
      match i.Status with
      | WorkerProtocol.SessionLifecycleStatus.Starting _
      | WorkerProtocol.SessionLifecycleStatus.Restarting _ ->
        WarmingUp (WorkerProtocol.SessionId.value i.Id, i.Status)
      | WorkerProtocol.SessionLifecycleStatus.Faulted _
      | WorkerProtocol.SessionLifecycleStatus.Stopped ->
        FaultedSession (WorkerProtocol.SessionId.value i.Id, FaultCause.ofStatus i.Status)
      | _ ->
        Unroutable (WorkerProtocol.SessionId.value i.Id, i.Status)
    | None ->
      Gone "Session is no longer running. Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session to start a new one."

  /// Pure guidance: the agent-facing message for a resolution.
  /// INVARIANT: "create_session" and "no longer running" appear only in the
  /// Gone case — an existing session is never presented as missing.
  let formatSessionResolution = function
    | Routable _ -> ""
    | WarmingUp (sid, status) ->
      sprintf "Session '%s' is still warming up (%s). This typically takes 15-30s for test projects. Call get_session_status with wait_seconds=60 to wait for readiness; do not sleep or poll. Do NOT create a new session — it will compete for resources and make warmup slower." sid (WorkerProtocol.SessionLifecycleStatus.label status)
    | Unroutable (sid, status) ->
      sprintf "Session '%s' exists (status: %s) but its worker is not routable yet — it may be mid-restart. Check get_session_status or list_sessions and re-check shortly. Do NOT create a duplicate session." sid (WorkerProtocol.SessionLifecycleStatus.label status)
    | FaultedSession (sid, cause) ->
      sprintf "Session '%s' is faulted. Why: %s\nRun reset_fsi_session or hard_reset_fsi_session to recover." sid (FaultCause.describe cause)
    | Gone msg -> msg

  /// Route to the active session or the specified session.
  /// When no agent mapping exists, resolves by the caller's working directory.
  /// Returns a typed SessionResolution — never a lying string.
  let resolveSessionId (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<SessionResolution> =
    task {
      match sessionId with
      | Some sid ->
        let validId = toSessionId sid
        let! proxy = ctx.SessionOps.GetProxy validId
        match proxy with
        | Some _ -> return Routable sid
        | None ->
          let! info = ctx.SessionOps.GetSessionInfo validId
          match info with
          // The session EXISTS but has no proxy yet, so it is warming up,
          // unroutable, or faulted — not gone. `classifySessionAvailability`
          // takes the option, so pass `info` whole rather than the unwrapped
          // value.
          | Some _ -> return classifySessionAvailability info false
          | None ->
            // Recovery from a dead session id is a POLICY, extracted to
            // `StaleSessionRecovery` (several honest outcomes, and this file
            // is a 4,400-line hub). A client that outlived a daemon restart
            // used to be bricked here: the no-id branch resolved by working
            // directory and re-bound, this branch checked only the given id.
            let! sessions = ctx.SessionOps.GetAllSessions()
            let candidatesFor wd =
              sessionsMatchingWorkingDirDeep sessions wd
              |> List.map (fun s -> s.Id)

            match StaleSessionRecovery.decide sid workingDirectory candidatesFor with
            | StaleSessionRecovery.Recovery.ReBind(matchedId, why) ->
              setActiveSessionId ctx agent matchedId
              Log.info "session '%s' is gone; re-bound to '%s' (%s)" sid matchedId why
              return Routable matchedId
            | StaleSessionRecovery.Recovery.Gone why -> return Gone why

      | None ->
        let! candidateResult =
          task {
            match workingDirectory with
            | Some wd when not (System.String.IsNullOrWhiteSpace wd) ->
              let! sessions = ctx.SessionOps.GetAllSessions()
              match sessionsMatchingWorkingDirDeep sessions wd with
              | [ matched ] ->
                let matchedId = WorkerProtocol.SessionId.value matched.Id
                setActiveSessionId ctx agent matchedId
                return Ok matchedId
              | [] ->
                return Error (sprintf "No sessions match workingDirectory '%s'. Running sessions: %s. Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session for that directory, or switch_session to an existing matching session." wd (formatExistingSessionsHint sessions))
              | matches ->
                return Error (formatWorkingDirectoryAmbiguity "Multiple sessions match workingDirectory" wd matches)
            | _ ->
              return Ok (activeSessionId ctx agent)
          }
        match candidateResult with
        | Error msg -> return Gone msg
        | Ok candidate when candidate <> "" ->
          let validCandidate = toSessionId candidate
          let! proxy = ctx.SessionOps.GetProxy validCandidate
          match proxy with
          | Some _ -> return Routable candidate
          | None ->
            let! info = ctx.SessionOps.GetSessionInfo validCandidate
            match info with
            | Some i when (match i.Status with
                           | WorkerProtocol.SessionLifecycleStatus.Starting _
                           | WorkerProtocol.SessionLifecycleStatus.Restarting _ -> true
                           | _ -> false) ->
              // issue #140: do NOT clear the mapping here. The candidate came
              // from an earlier deliberate switch_session (or create_session);
              // it still IS the session the agent wants, just not routable
              // yet. Clearing it made an unrelated, unrouted status read
              // (e.g. a no-arg get_fsi_status poll) silently undo the switch:
              // once warmup finished, the next unrouted call had nothing
              // cached and fell back to workingDirectory/registry guessing,
              // which is ambiguous whenever two sessions share a directory —
              // exactly the "switch_session called during warmup does not
              // take effect" repro.
              return WarmingUp (candidate, i.Status)
            | Some i when (match i.Status with
                           | WorkerProtocol.SessionLifecycleStatus.Faulted _
                           | WorkerProtocol.SessionLifecycleStatus.Stopped -> true
                           | _ -> false) ->
              // Same reasoning, and it matters for recovery too:
              // reset_fsi_session/hard_reset_fsi_session resolve with no
              // explicit sessionId and rely on this exact mapping surviving
              // a Faulted classification to know which session to recover.
              return FaultedSession (candidate, FaultCause.ofStatus i.Status)
            | Some i ->
              // Unroutable is transient (worker mid-restart) — same reasoning.
              return Unroutable (candidate, i.Status)
            | None ->
              // The session record itself is gone from the registry (stopped
              // and purged by any path) — nothing left to route to, so this
              // IS the one case where the mapping must be cleared.
              setActiveSessionId ctx agent ""
              return Gone "Session is no longer running. Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session to start a new one."
        | Ok _ ->
          let! sessions = ctx.SessionOps.GetAllSessions()
          let currentDir = Environment.CurrentDirectory
          let currentDirMatches = sessionsMatchingWorkingDir sessions currentDir
          match currentDirMatches with
          | [ currentDirSession ] ->
            let sid = WorkerProtocol.SessionId.value currentDirSession.Id
            setActiveSessionId ctx agent sid
            let! proxy = ctx.SessionOps.GetProxy (toSessionId sid)
            match proxy with
            | Some _ -> return Routable sid
            | None ->
              let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
              return classifySessionAvailability info false
          | _ :: _ :: _ as matches ->
            return Gone (formatWorkingDirectoryAmbiguity "Multiple sessions match the current working directory" currentDir matches)
          | [] ->
            match sessions with
            | [ singleSession ] ->
              let sid = WorkerProtocol.SessionId.value singleSession.Id
              setActiveSessionId ctx agent sid
              let! proxy = ctx.SessionOps.GetProxy (toSessionId sid)
              match proxy with
              | Some _ -> return Routable sid
              | None ->
                let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
                return classifySessionAvailability info false
            | _ ->
              return Gone "No active session. Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session to create one first."
    }

  /// Helper: run a function with the resolved session ID, or return the error message.
  let withSession (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) (f: string -> Task<string>) : Task<string> =
    task {
      let! resolution = resolveSessionId ctx agent sessionId workingDirectory
      match resolution with
      | Routable sid -> return! f sid
      | other -> return sprintf "Error: %s" (formatSessionResolution other)
    }

  /// Result-returning sibling of withSession — see withSessionWdResult's doc.
  let withSessionResult (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) (f: string -> Task<Result<string, SageFsError>>) : Task<Result<string, SageFsError>> =
    task {
      let! resolution = resolveSessionId ctx agent sessionId workingDirectory
      match resolution with
      | Routable sid -> return! f sid
      | other -> return Error (SageFsError.SessionNotRoutable (formatSessionResolution other))
    }

  /// Recovery variant for tools that must be able to reach a session that
  /// needs recovery even when its worker proxy is not installed:
  /// reset_fsi_session and hard_reset_fsi_session are exactly how an agent
  /// recovers from Faulted, so FaultedSession routes through to the handler
  /// (which re-spawns via SessionManager). Unroutable (status Ready/Evaluating/
  /// Building but no proxy — the transitional window where the registry is
  /// ahead of proxy installation) also routes through: the reset path goes via
  /// SessionManagementOps, not the worker proxy, so it does not need the proxy;
  /// a rebuild=false reset on a proxy-less session fails gracefully through the
  /// transport-error path.
  /// ONLY WarmingUp (Starting/Restarting) blocks — restarting a session that is
  /// already restarting is the bug we are preventing.
  let withSessionAllowFaulted (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) (f: string -> Task<string>) : Task<string> =
    task {
      let! resolution = resolveSessionId ctx agent sessionId workingDirectory
      match resolution with
      | Routable sid -> return! f sid
      | FaultedSession (sid, _) -> return! f sid
      | Unroutable (sid, _) -> return! f sid
      | other -> return sprintf "Error: %s" (formatSessionResolution other)
    }

  /// Result-returning sibling of withSessionAllowFaulted — see
  /// withSessionWdResult's doc.
  let withSessionAllowFaultedResult (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) (f: string -> Task<Result<string, SageFsError>>) : Task<Result<string, SageFsError>> =
    task {
      let! resolution = resolveSessionId ctx agent sessionId workingDirectory
      match resolution with
      | Routable sid -> return! f sid
      | FaultedSession (sid, _) -> return! f sid
      | Unroutable (sid, _) -> return! f sid
      | other -> return Error (SageFsError.SessionNotRoutable (formatSessionResolution other))
    }

  /// Overload without sessionId parameter (uses None).
  let withSessionWd (ctx: McpContext) (agent: string) (workingDirectory: string option) (f: string -> Task<string>) : Task<string> =
    withSession ctx agent None workingDirectory f

  /// Result-returning sibling of withSessionWd, for callers (the plain HTTP
  /// surface) that need to branch on success/failure structurally instead of
  /// string-sniffing a formatted "Error: ..." prefix. A resolution failure
  /// becomes SessionNotRoutable carrying the exact same message
  /// formatSessionResolution already produces — no information lost, just
  /// not flattened to a bare string before the caller ever sees it.
  let withSessionWdResult (ctx: McpContext) (agent: string) (workingDirectory: string option) (f: string -> Task<Result<string, SageFsError>>) : Task<Result<string, SageFsError>> =
    task {
      let! resolution = resolveSessionId ctx agent None workingDirectory
      match resolution with
      | Routable sid -> return! f sid
      | other -> return Error (SageFsError.SessionNotRoutable (formatSessionResolution other))
    }

  /// Classify a session-routing outcome into a `SageFsError` for FRICTION
  /// telemetry — never by scanning `formatSessionResolution`'s rendered
  /// guidance text. Each non-Routable `SessionResolution` case is matched by
  /// its own constructor tag; `Gone`'s ambiguous-vs-missing split (the two
  /// conditions `formatWorkingDirectoryAmbiguity` and the plain "no sessions
  /// match" message both collapse into `Gone` upstream) is resolved the same
  /// structural way `resolveSessionId` itself resolves it — by re-checking
  /// the session registry for how many sessions actually match — not by
  /// re-parsing the `Gone` message. This is purely additive: it calls only
  /// read-only registry lookups and never touches `resolveSessionId`,
  /// `SessionNotRoutable`, or any of the `*Result` functions the HTTP
  /// surface (McpServer.fs) already depends on, so their behavior is
  /// unchanged. Used only by the MCP tool surface's friction recorder.
  let sessionRoutingError
      (ctx: McpContext) (sessionId: string option) (workingDirectory: string option)
      (resolution: SessionResolution)
      : Task<SageFsError option> =
    task {
      match resolution with
      | Routable _ -> return None
      | WarmingUp (sid, status) ->
        return Some (SageFsError.SessionNotRoutable (sprintf "session '%s' is warming up (%s)" sid (WorkerProtocol.SessionLifecycleStatus.label status)))
      | Unroutable (sid, status) ->
        return Some (SageFsError.SessionNotRoutable (sprintf "session '%s' is not yet routable (%s)" sid (WorkerProtocol.SessionLifecycleStatus.label status)))
      | FaultedSession (sid, cause) ->
        return Some (SageFsError.WorkerCommunicationFailed (sid, sprintf "session is faulted: %s" (FaultCause.describe cause)))
      | Gone _ ->
        match sessionId with
        | Some sid -> return Some (SageFsError.SessionNotFound sid)
        | None ->
          match workingDirectory with
          | Some wd when not (System.String.IsNullOrWhiteSpace wd) ->
            let! sessions = ctx.SessionOps.GetAllSessions()
            match sessionsMatchingWorkingDirDeep sessions wd with
            | _ :: _ :: _ as matches ->
              return Some (SageFsError.AmbiguousSessions (matches |> List.map (fun s -> WorkerProtocol.SessionId.value s.Id)))
            | _ -> return Some SageFsError.NoActiveSessions
          | _ -> return Some SageFsError.NoActiveSessions
    }

  let setSnapshotStatus (ctx: McpContext) (sid: string) (status: WorkerProtocol.SessionLifecycleStatus) =
    ctx.SessionOps.UpdateSessionStatus (toSessionId sid) status

  /// The status to show while a caller-driven reset (reset / hard-reset
  /// rebuild=false) runs. It does NOT respawn the worker, so its pid and port
  /// carry forward. A session with no worker (Faulted) has none to carry, and a
  /// made-up pid would be a worker that never existed, so its status stands.
  let private withCarriedWorker (build: WorkerProtocol.WorkerHandle -> WorkerProtocol.SessionLifecycleStatus) (status: WorkerProtocol.SessionLifecycleStatus) =
    match WorkerProtocol.SessionLifecycleStatus.workerPid status with
    | Some pid -> build { Pid = pid; Port = WorkerProtocol.SessionLifecycleStatus.workerPort status }
    | None -> status

  let private startingWhileReset = withCarriedWorker WorkerProtocol.SessionLifecycleStatus.Starting
  let private readyAfterReset = withCarriedWorker WorkerProtocol.SessionLifecycleStatus.Ready

  /// What the gate resolved for a tool call. `NotResolved` for a tool that
  /// needs no session (monitoring, cohort tools) or was refused before any
  /// session was looked up.
  [<RequireQualifiedAccess>]
  type GateResolution =
    | NotResolved
    | Resolved of SessionResolution

  /// What the gate admitted: the call as the tool body will receive it, and the
  /// session the gate resolved for it.
  type GateAdmission =
    { ToolName: string
      SessionId: string option
      WorkingDirectory: string option
      Resolution: GateResolution }

  /// What the gate's status probe learned from the worker.
  [<RequireQualifiedAccess>]
  type GateProbe =
    /// The worker answered, and this is its state.
    | Answered of SessionState
    /// The worker answered, and its FSI host is dead. The state alone (Faulted) would hide why.
    | HostCrashed of HostCrash
    /// The worker did not answer within `bound`: it is busy or hung.
    | TimedOut of bound: TimeSpan

  /// Ask the worker for its status, waiting at most `bound`. Only the worker
  /// can say whether an eval is running (the registry maps Ready and Evaluating
  /// to one state). A probe that runs out of time is abandoned, not reported as
  /// a worker failure: it neither marks the session Faulted nor tells the
  /// SessionManager the worker died.
  let probeSessionState (ctx: McpContext) (sessionId: string) (bound: TimeSpan) : Task<GateProbe> =
    task {
      use abandon = new System.Threading.CancellationTokenSource()
      let probe =
        routeToSessionWithin ctx abandon.Token sessionId
          (fun replyId -> WorkerProtocol.WorkerMessage.GetStatus (WorkerProtocol.SessionId.value replyId))
      let! first = Task.WhenAny(probe :> Task, Task.Delay(bound, abandon.Token))
      abandon.Cancel()
      match obj.ReferenceEquals(first, probe) with
      | true ->
        let! routeResult = probe
        return
          match routeResult with
          | Ok (WorkerProtocol.WorkerResponse.StatusResult(_, { Status = WorkerProtocol.SessionStatus.HostCrashed crash })) ->
            GateProbe.HostCrashed crash
          | Ok (WorkerProtocol.WorkerResponse.StatusResult(_, snapshot)) ->
            GateProbe.Answered (WorkerProtocol.SessionStatus.toSessionState snapshot.Status)
          | _ -> GateProbe.Answered SessionState.Faulted
      | false ->
        // The abandoned call was cancelled above; observe how it ends so it
        // cannot surface later as an unobserved task exception.
        probe.ContinueWith((fun (finished: Task) -> finished.Exception |> ignore), TaskContinuationOptions.OnlyOnFaulted)
        |> ignore
        return GateProbe.TimedOut bound
    }

  /// Check tool availability against the session's live state.
  let requireTool (ctx: McpContext) (sessionId: string) (toolName: string) (bound: TimeSpan) : Task<Result<unit, string>> =
    task {
      let! probe = probeSessionState ctx sessionId bound
      return
        match probe with
        | GateProbe.Answered state ->
          Affordances.checkToolAvailability state toolName
          |> Result.mapError SageFsError.describeForAgent
        // The tools a crashed session still offers (a reset) pass; every other is refused with the crash, not with
        // the generic wait-for-Ready advice, which would be false.
        | GateProbe.HostCrashed crash ->
          Affordances.checkToolAvailability SessionState.Faulted toolName
          |> Result.mapError (fun _ -> SageFsError.describeForAgent (SageFsError.FsiHostCrashed crash))
        | GateProbe.TimedOut waited ->
          Error (SageFsError.describeForAgent (SageFsError.WorkerTimeout (sessionId, "status check", waited.TotalSeconds)))
    }

  /// Reverse lookup from MCP tool name to `Affordances.CohortTool` — built
  /// once from the single `toToolName` source of truth (no second literal
  /// name list to drift, per MEMORY.md "no magic strings anywhere").
  let private cohortToolByName: Map<string, Affordances.CohortTool> =
    Affordances.CohortTool.all
    |> List.map (fun t -> Affordances.CohortTool.toToolName t, t)
    |> Map.ofList

  /// Authority-aware pre-check for cohort tools (cohort-integration-plan.md
  /// Slice 3, item 11): an EARLIER, friendlier refusal than `Cohort.decide`'s
  /// own `NotConductor`/`NotClaimHolder` — that core enforcement is untouched
  /// and still runs regardless of this gate. `None` when `toolName` is not a
  /// cohort tool at all, so the caller falls through to the existing
  /// session-state gate below (cohort tools are `AlwaysAvailable` there —
  /// this is a second, role-based dimension layered on top of it).
  ///
  /// Resolves the caller's `Authority` from the owner's published
  /// `CohortFrame` (`Affordances.authorityOfMember`) using the BOUND
  /// identity (`memberIdFor agent` — never a self-declared role argument).
  /// No cohort owner wired (`ctx.CohortOwner = None`, pre-Slice-2 unit
  /// tests) resolves to `Authority.Anonymous`, same as an unjoined caller.
  let private checkCohortAuthorityGate (ctx: McpContext) (agent: string) (toolName: string) : Result<unit, string> option =
    match Map.tryFind toolName cohortToolByName with
    | None -> None
    | Some tool ->
      let who = memberIdFor agent
      let authority =
        match ctx.CohortOwner with
        | Some owner -> Affordances.authorityOfMember who (owner.ReadFrame())
        | None -> Cohort.Authority.Anonymous
      if Affordances.checkCohortToolAllowed authority tool then
        Some (Ok ())
      else
        let roleText =
          match authority with
          | Cohort.Authority.Anonymous -> "not (yet) a member of this cohort"
          | Cohort.Authority.Member(_, role) -> sprintf "a %A" role
          | Cohort.Authority.Conductor _ -> "the conductor" // unreachable: every tool is allowed for Conductor
        let reason =
          sprintf "%s cannot call %s: your role (%s) does not permit it."
            (MemberTable.MemberId.display who) toolName roleText
        let suggestion =
          "Join as Implementer for claim/landing tools, or ask the cohort conductor to perform this action."
        Some (Error (SageFsError.describeForAgent (SageFsError.CohortActionFailed(reason, suggestion))))

  /// ── Affordance call gate ─────────────────────────────────────────────────
  ///
  /// Structural enforcement point for the affordance model. The MCP server's
  /// CallToolFilter runs this BEFORE any `[<McpServerTool>]` body executes, so a
  /// tool that is not available in the CURRENT session state is rejected with a
  /// structured error instead of executing.
  ///
  /// Classification comes only from `Affordances.toolGate`:
  ///   - AlwaysAvailable — no session-state dependence; callable in every state
  ///     and before any session exists (monitoring, session listing, telemetry).
  ///   - StateGated      — availability is derived from `availableTools` for the
  ///     state of the session the call would actually act on. `sessionId` /
  ///     `workingDirectory` mirror the routing inputs the tool body will use, so
  ///     the gate evaluates the SAME session the tool would target.
  ///   - undeclared      — fails closed (ToolNotAvailable).
  ///
  /// Cohort tools additionally pass through `checkCohortAuthorityGate` FIRST
  /// (Slice 3, item 11) — a role-based dimension the session-state gate below
  /// has no concept of. `admitToolCallWithin` is the gate itself; it returns
  /// what it resolved, so the tool body need not resolve a second time.
  let admitToolCallWithin
    (probeBound: TimeSpan)
    (ctx: McpContext)
    (agent: string)
    (sessionId: string option)
    (workingDirectory: string option)
    (toolName: string)
    : Task<Result<GateAdmission, string>> =
    let admission (resolution: GateResolution) : GateAdmission =
      { ToolName = toolName; SessionId = sessionId; WorkingDirectory = workingDirectory; Resolution = resolution }
    let allowedIn (state: SessionState) =
      Affordances.checkToolCallAllowed state toolName |> Result.mapError SageFsError.describeForAgent
    task {
      match checkCohortAuthorityGate ctx agent toolName with
      | Some result -> return result |> Result.map (fun () -> admission GateResolution.NotResolved)
      | None ->
      match Affordances.toolGate toolName with
      | Some Affordances.ToolGate.AlwaysAvailable ->
        return Ok (admission GateResolution.NotResolved)
      | Some Affordances.ToolGate.StateGated ->
        let! resolution = resolveSessionId ctx agent sessionId workingDirectory
        let! verdict =
          match resolution with
          // Worker-authoritative state for the routable session.
          | Routable sid -> requireTool ctx sid toolName probeBound
          | WarmingUp (_, status) | Unroutable (_, status) ->
            Task.FromResult (allowedIn (WorkerProtocol.SessionLifecycleStatus.toSessionState status))
          | FaultedSession _ -> Task.FromResult (allowedIn SessionState.Faulted)
          // No session reachable: code tools are refused with the routing reason, not "wait for Ready".
          | Gone message ->
            Task.FromResult (allowedIn SessionState.Uninitialized |> Result.mapError (fun _ -> message))
        return verdict |> Result.map (fun () -> admission (GateResolution.Resolved resolution))
      | None ->
        return allowedIn SessionState.Uninitialized |> Result.map (fun () -> admission GateResolution.NotResolved)
    }

  /// The gate, answering only whether the call may run.
  let enforceToolCallGate
    (ctx: McpContext)
    (agent: string)
    (sessionId: string option)
    (workingDirectory: string option)
    (toolName: string)
    : Task<Result<unit, string>> =
    task {
      let! verdict = admitToolCallWithin Timeouts.gateStatusProbe ctx agent sessionId workingDirectory toolName
      return verdict |> Result.map ignore
    }

  /// The admission for the tool call running on this async flow, set by
  /// `runAdmitted` around the tool body and cleared after it.
  let private currentAdmission = new System.Threading.AsyncLocal<GateAdmission option>()

  /// Run a tool body with the gate's admission in scope, so `resolveAdmitted`
  /// can hand it the session the gate already resolved.
  let runAdmitted (admission: GateAdmission) (run: unit -> Task<'a>) : Task<'a> =
    task {
      currentAdmission.Value <- Some admission
      try
        return! run ()
      finally
        currentAdmission.Value <- None
    }

  /// The session a tool body should use. When the gate resolved this same call
  /// (same tool, same session id and working directory), that resolution is
  /// returned as it is; anything else resolves now, as it always did.
  let resolveAdmitted
    (ctx: McpContext)
    (toolName: string)
    (agent: string)
    (sessionId: string option)
    (workingDirectory: string option)
    : Task<SessionResolution> =
    match currentAdmission.Value with
    | Some { ToolName = admittedTool
             SessionId = admittedSession
             WorkingDirectory = admittedDirectory
             Resolution = GateResolution.Resolved resolution }
      when admittedTool = toolName && admittedSession = sessionId && admittedDirectory = workingDirectory ->
      Task.FromResult resolution
    | _ -> resolveSessionId ctx agent sessionId workingDirectory

  /// Look up the workflow for a session from the Elm model.
  /// Falls back to Interactive (identity for enhancement) when the model is unavailable.
  let getWorkflowForSession (ctx: McpContext) (sid: string) : WorkflowTypes.SessionWorkflow =
    match ctx.GetElmModel with
    | Some getModel ->
      let model = getModel()
      match model.SessionContext with
      | Some sc when sc.SessionId = sid -> sc.Workflow
      | _ -> WorkflowTypes.SessionWorkflow.Interactive
    | None -> WorkflowTypes.SessionWorkflow.Interactive

  /// Format a WorkerResponse.EvalResult for display, with workflow-aware error enhancement.
  let formatWorkerEvalResult (workflow: WorkflowTypes.SessionWorkflow) (response: WorkerProtocol.WorkerResponse) : string =
    match response with
    | WorkerProtocol.WorkerResponse.EvalResult(_, result, diags, _) ->
      let diagStr =
        match List.isEmpty diags with
        | true -> ""
        | false ->
          diags
          |> List.map (fun d ->
            // Include the (line,col) span so an agent can make a surgical edit
            // instead of re-reading the whole snippet (dogfood finding F4 — the
            // text path dropped the span check_fsharp_code already shows). Omit
            // it only when there is no real position (StartLine 0).
            let sev = Features.Diagnostics.DiagnosticSeverity.label d.Severity
            match d.StartLine with
            | line when line > 0 -> sprintf "  [%s] (%d,%d) %s" sev line d.StartColumn d.Message
            | _ -> sprintf "  [%s] %s" sev d.Message)
          |> String.concat "\n"
          |> sprintf "\nDiagnostics:\n%s"
      match result with
      | Ok output ->
        // issue #143: raw ANSI escape codes (e.g. from Expecto's colored
        // console logger, or any printfn'd color) are noise to a human
        // reading this and actively corrupt an agent trying to parse the
        // result — strip them at the one place every eval-returning tool's
        // text output already funnels through.
        sprintf "Result: %s%s" (stripAnsi output) diagStr
      | Error err ->
        let errText = SageFsError.describeForAgent err
        // A typed crash already says what to do; guessing at its text would add the wrong advice.
        let suggestion = match err with SageFsError.FsiHostCrashed _ -> "" | _ -> errText |> ErrorMessages.categorize |> ErrorMessages.getSuggestion
        let enhanced = WorkflowErrorContext.enhance workflow errText suggestion
        match String.IsNullOrEmpty enhanced with
        | true -> sprintf "Error: %s%s" errText diagStr
        | false -> sprintf "Error: %s\n%s%s" errText enhanced diagStr
    | WorkerProtocol.WorkerResponse.WorkerError err ->
      sprintf "Error: %s" (SageFsError.describeForAgent err)
    | other ->
      sprintf "Unexpected response: %A" other

  type OutputFormat = Text | Json

  /// Adjust diagnostic line/column numbers in a WorkerResponse by preprocessing offsets.
  let adjustResponseDiagnostics (lineOffset: int) (colOffset: int) (response: WorkerProtocol.WorkerResponse) =
    match lineOffset, colOffset with
    | 0, 0 -> response
    | _ ->
      match response with
      | WorkerProtocol.WorkerResponse.EvalResult(rid, result, diags, meta) ->
        let adjusted =
          diags |> List.map (fun d ->
            { d with
                StartLine = Middleware.CompilationContext.mapDiagnosticLine lineOffset d.StartLine
                StartColumn = Middleware.CompilationContext.mapDiagnosticColumn colOffset d.StartColumn
                EndLine = Middleware.CompilationContext.mapDiagnosticLine lineOffset d.EndLine
                EndColumn = Middleware.CompilationContext.mapDiagnosticColumn colOffset d.EndColumn })
        WorkerProtocol.WorkerResponse.EvalResult(rid, result, adjusted, meta)
      | other -> other

  /// Outcome of running code through /exec's pipeline: distinguishes an eval
  /// that ran and failed (a compile/runtime error — the request WAS
  /// processed, its text lives in the formatted output) from an eval that
  /// never ran at all because the session couldn't be routed to or the
  /// worker couldn't be reached. `Evaluated` keeps the truthful-200 contract
  /// (client code is wrong, not SageFs); `InfraFailure` carries the
  /// `SageFsError` the algebra already classifies (SessionNotRoutable,
  /// WorkerCommunicationFailed, ...) so a caller — and /exec's HTTP status —
  /// can tell "your code is wrong" from "SageFs itself is unreachable"
  /// instead of both being flattened into the same success=false shape.
  type EvalExecOutcome =
    | Evaluated of failed: bool
    | InfraFailure of SageFsError

  module EvalExecOutcome =
    /// Combine outcomes from statements evaluated in sequence within one
    /// call: an infra failure anywhere means nothing after it could have
    /// run against a session that was never reachable, so it wins outright;
    /// otherwise failures accumulate the way the old bool flag did.
    let combine (a: EvalExecOutcome) (b: EvalExecOutcome) : EvalExecOutcome =
      match a, b with
      | InfraFailure _, _ -> a
      | _, InfraFailure _ -> b
      | Evaluated f1, Evaluated f2 -> Evaluated (f1 || f2)

  /// Evaluate a single FSI statement, dispatch Elm events, return formatted output.
  /// Returns (formatted text, outcome, diagnostics with spans, the
  /// classified `SageFsError` when this statement failed). The last two
  /// elements exist so `evalFSharpCodeWithOutcome` can hand `send_fsharp_code`
  /// structured content instead of re-parsing `formatted` (roast-7 §2) —
  /// every branch below already has `diags`/`err` in hand from the same
  /// match that built `formatted`, so this costs nothing new to compute.
  let private evalSingleStatement (ctx: McpContext) (sid: string) (format: OutputFormat) (lineOffset: int) (colOffset: int) (statement: string) : Task<string * EvalExecOutcome * WorkerProtocol.WorkerDiagnostic list * SageFsError option> = task {
    notifyElm ctx (TuiEvent.EvalStarted (sid, statement))
    let workflow = getWorkflowForSession ctx sid
    let! routeResult =
      routeToSession ctx sid
        (fun replyId -> WorkerProtocol.WorkerMessage.EvalCode(statement, WorkerProtocol.SessionId.value replyId))
    return
      match routeResult with
      | Ok rawResponse ->
        let response = adjustResponseDiagnostics lineOffset colOffset rawResponse
        let formatted =
          match format with
          | Json -> McpAdapter.formatWorkerEvalResultJson response
          | Text -> formatWorkerEvalResult workflow response
        match response with
        | WorkerProtocol.WorkerResponse.EvalResult(_, Ok _, diags, metadata) ->
          // A successful eval proves the session can still function.
          // If a previous TypeLoadException was recorded, clear it — the session recovered.
          match typeIdentityDiagnostics.TryRemove(sid) with
          | true, _ -> Log.info "Session %s recovered from TypeLoadException (successful eval cleared the diagnostic)" sid
          | false, _ -> ()
          notifyElm ctx (
            TuiEvent.EvalCompleted (sid, formatted, diags |> List.map WorkerProtocol.WorkerDiagnostic.toDiagnostic))
          match metadata |> Map.tryFind "liveTestHookResult" with
          | Some json ->
            try
              let hookResult =
                WorkerProtocol.Serialization.deserialize<Features.LiveTesting.LiveTestHookResultDto> json
              match List.isEmpty hookResult.DetectedProviders with
              | false -> notifyElm ctx (TuiEvent.ProvidersDetected hookResult.DetectedProviders)
              | true -> ()
              match Array.isEmpty hookResult.DiscoveredTests with
              | false -> notifyElm ctx (TuiEvent.TestsDiscovered (sid, hookResult.DiscoveredTests))
              | true -> ()
              match Array.isEmpty hookResult.AffectedTestIds with
              | false -> notifyElm ctx (TuiEvent.AffectedTestsComputed (hookResult.AffectedTestIds, hookResult.ChangedSymbolNames |> Array.toList))
              | true -> ()
            with ex -> Log.warn "Failed to deserialize hook result: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
          | None -> ()
          // Live bound-value snapshot → adaptive live-bindings store (dashboard
          // watch window). Pulled AFTER the eval reply, never attached to it
          // (roast-4 #2) — fire-and-forget so a slow or failed reflection
          // walk can never delay this eval's result to the caller.
          match ctx.LiveBindings with
          | Some hub ->
            let ask : Features.LiveBindingsPane.Feed.Ask =
              fun message ->
                async {
                  match! routeToSession ctx sid (fun _ -> message) |> Async.AwaitTask with
                  | Ok response -> return Result.Ok response
                  | Error routeError -> return Result.Error(routeErrorToSageFsError sid routeError)
                }
            Async.Start (Features.LiveBindingsPane.Feed.pull ask hub.Adaptive hub.Notes sid (fun () -> hub.ConfiguredWalk sid))
          | None -> ()
          match metadata |> Map.tryFind "assemblyLoadErrors" with
          | Some json ->
            try
              let errors =
                WorkerProtocol.Serialization.deserialize<Features.LiveTesting.AssemblyLoadError list> json
              match List.isEmpty errors with
              | false -> notifyElm ctx (TuiEvent.AssemblyLoadFailed errors)
              | true -> ()
            with ex -> Log.warn "Failed to deserialize assembly load errors: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
          | None -> ()
          // Success — the typed Ok outcome, not string sniffing, decides truth.
          (formatted, Evaluated false, diags, None)
        | WorkerProtocol.WorkerResponse.EvalResult(_, Error err, diags, _) ->
          let errText = SageFsError.describe err
          // Track TypeLoadException so targeted_verify can flag the session as compromised.
          match ErrorMessages.categorize errText with
          | ErrorMessages.ErrorCategory.TypeLoad ->
            typeIdentityDiagnostics.[sid] <- errText
            Log.warn "TypeLoadException detected for session %s — type identity compromised" sid
          | _ -> ()
          notifyElm ctx (
            TuiEvent.EvalFailed (sid, errText))
          // The eval RAN — it's a compile/runtime failure in the user's code,
          // not an infra failure. Keeps the truthful-200 contract: /exec
          // stays 200 with the error text in `result`.
          (formatted, Evaluated true, diags, Some err)
        | WorkerProtocol.WorkerResponse.WorkerError err ->
          // The worker replied but could not run the eval at all (e.g. still
          // starting up) — this already IS a classified SageFsError, so
          // route it through the algebra instead of flattening it to a bool.
          notifyElm ctx (TuiEvent.EvalFailed (sid, SageFsError.describe err))
          (formatted, InfraFailure err, [], Some err)
        | _ -> (formatted, Evaluated false, [], None)
      | Error msg ->
        let err = routeErrorToSageFsError sid msg
        notifyElm ctx (TuiEvent.EvalFailed (sid, routeErrorMessage msg))
        (sprintf "Error: %s" (routeErrorMessage msg), InfraFailure err, [], Some err)
  }

  /// Evaluate F# code. Returns (formatted output, outcome, diagnostics with
  /// spans, the last classified `SageFsError` if any statement failed) —
  /// `Evaluated failed` when the code ran (a compile/runtime failure still
  /// counts as ran: /exec keeps its truthful-200 contract), `InfraFailure
  /// err` when it never ran because the session/worker was unreachable. The
  /// distinction comes from the typed worker outcome, never string
  /// sniffing. The last two elements let `send_fsharp_code` return
  /// structured content (roast-7 §2) without re-deriving it from `output`.
  /// Most callers use `sendFSharpCode` (string-only view).
  /// Whether the REPL of this session runs the build the app runs. Read off the registry's own record, so every surface agrees.
  /// A session the registry cannot answer for is level: there is nothing it is known to be behind.
  let freshnessOfSession (ctx: McpContext) (sid: string) : Task<ReplFreshness> =
    task {
      try
        let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
        return SessionStatusPayload.replFreshnessOfSession info
      with _ -> return ReplFreshness.InSync
    }

  /// `evalFSharpCodeWithOutcome`, and the freshness of the session the code ran in, for the caller that wants it as data.
  let evalFSharpCodeWithFreshness
      (ctx: McpContext) (agentName: string) (code: string) (format: OutputFormat)
      (sessionId: string option) (workingDirectory: string option)
      (filePath: string option) (evalMode: string option) (blockStartLine: int option)
      (intent: string option)
      : Task<string * EvalExecOutcome * WorkerProtocol.WorkerDiagnostic list * SageFsError option * ReplFreshness> =
    task {
      // The session the gate already resolved for this very call, if any.
      let! resolution = resolveAdmitted ctx "send_fsharp_code" agentName sessionId workingDirectory
      match resolution with
      | Routable sid ->
        return! task {
          let state =
            compilationStates.GetOrAdd(sid, fun _ -> Middleware.CompilationContext.CompilationState.empty)

          let! fileStructure, updatedCache =
            match filePath with
            | Some fp -> task {
              try
                let! fs, cache =
                  Middleware.CompilationContext.parseFileStructureCached fp code state.FileCache
                return Some fs, cache
              with
              | :? System.OperationCanceledException as ex ->
                return raise ex
              | exn ->
                Log.debug "CompilationContext parse failed for %s: %s" fp exn.Message
                return None, state.FileCache
              }
            | None -> Task.FromResult(None, state.FileCache)

          let parsedMode = Middleware.CompilationContext.EvalMode.parse evalMode
          let preprocessed, updatedModules =
            Middleware.CompilationContext.preprocessForFsi
              fileStructure parsedMode blockStartLine state.EvaluatedModules code

          // Note: concurrent MCP calls for the same session could race here.
          // Blast radius is small — lost cache entry means one extra ~7ms parse,
          // lost EvaluatedModules entry means unnecessary `open` or duplicate module error.
          // Acceptable since evals are effectively serialized per session by the FSI lock.
          compilationStates.[sid] <- { state with EvaluatedModules = updatedModules; FileCache = updatedCache }

          let statements = McpAdapter.splitStatements preprocessed.Code
          Instrumentation.fsiEvals.Add(1L)
          Instrumentation.fsiStatements.Add(int64 statements.Length)
          let span = Instrumentation.startSpan Instrumentation.mcpSource "fsi.eval"
                       ["fsi.agent.name", box agentName; "fsi.statement.count", box statements.Length; "fsi.session.id", box sid]
          // Record agent activity for multi-agent coordination — keyed by the
          // BOUND connection, not the self-declared agentName (memberIdFor).
          // recordMemberActivity (not recordToolCall) so Role is derived from
          // the MemberId case itself, never by re-classifying the resolved
          // key's text (Phase 0 item 4 of sagefs-multiagent-vision.md §10).
          AgentActivityTracker.recordMemberActivity ctx.ActivityTracker (memberIdFor agentName) sid filePath intent DateTime.UtcNow

          let evalSw = System.Diagnostics.Stopwatch.StartNew()
          let mutable allOutputs = []
          let mutable outcome = Evaluated false
          let mutable allDiags : WorkerProtocol.WorkerDiagnostic list = []
          let mutable lastError : SageFsError option = None
          for statement in statements do
            let! output, stmtOutcome, diags, errOpt = evalSingleStatement ctx sid format preprocessed.LineOffset preprocessed.ColumnOffset statement
            allOutputs <- output :: allOutputs
            outcome <- EvalExecOutcome.combine outcome stmtOutcome
            allDiags <- allDiags @ diags
            match errOpt with
            | Some _ -> lastError <- errOpt
            | None -> ()

          let finalOutput =
            match format with
            | Json when statements.Length > 1 ->
              let items = List.rev allOutputs |> List.map (fun s -> s) |> String.concat ","
              sprintf "[%s]" items
            | _ when statements.Length > 1 ->
              String.concat "\n\n" (List.rev allOutputs)
            | _ -> allOutputs |> List.tryHead |> Option.defaultValue ""

          evalSw.Stop()
          // Record the eval into the shared feature push-state history so the
          // pure-MCP path feeds get_recent_fsi_events/filmstrip/impact_forecast,
          // exactly as the /exec bridge already does (roast-7 §2/§3). None in
          // tests. Uses the raw finalOutput (not the advisory-enriched text) to
          // match what /exec stores.
          recordEvalForSession ctx sid code finalOutput evalSw.ElapsedMilliseconds
          Instrumentation.succeedSpan span
          // Compute file-overlap advisory AFTER caching raw output
          let enrichedOutput =
            match filePath with
            | Some fp ->
              let presences = AgentActivityTracker.getActivePresences ctx.ActivityTracker (Some sid) Timeouts.agentPresenceEviction DateTime.UtcNow
              let advisories = SessionOperations.FileOverlapAdvisory.compute (resolvedKey agentName) [fp] presences
              SessionOperations.CoordinationEnrichment.enrichEvalWithAdvisories advisories finalOutput
            | None -> finalOutput
          // A REPL that is behind its app ran this against the build from before the patch. Said after the result, in the text a
          // reader sees; a JSON result is data and keeps its shape (the freshness is its own value).
          let! freshness = freshnessOfSession ctx sid
          let shown =
            match format with
            | Text -> ReplFreshness.annotate freshness enrichedOutput
            | Json -> enrichedOutput
          return (shown, outcome, allDiags, lastError, freshness)
        }
      | other ->
        let err = SageFsError.SessionNotRoutable (formatSessionResolution other)
        return (sprintf "Error: %s" (formatSessionResolution other), InfraFailure err, [], Some err, ReplFreshness.InSync)
    }

  let evalFSharpCodeWithOutcome
      (ctx: McpContext) (agentName: string) (code: string) (format: OutputFormat)
      (sessionId: string option) (workingDirectory: string option)
      (filePath: string option) (evalMode: string option) (blockStartLine: int option)
      (intent: string option)
      : Task<string * EvalExecOutcome * WorkerProtocol.WorkerDiagnostic list * SageFsError option> =
    task {
      let! text, outcome, diags, err, _ = evalFSharpCodeWithFreshness ctx agentName code format sessionId workingDirectory filePath evalMode blockStartLine intent
      return text, outcome, diags, err
    }

  /// String-only view of evalFSharpCodeWithOutcome — keeps existing callers.
  let sendFSharpCode
      (ctx: McpContext) (agentName: string) (code: string) (format: OutputFormat)
      (sessionId: string option) (workingDirectory: string option)
      (filePath: string option) (evalMode: string option) (blockStartLine: int option)
      (intent: string option)
      : Task<string> =
    task {
      let! output, _, _, _ = evalFSharpCodeWithOutcome ctx agentName code format sessionId workingDirectory filePath evalMode blockStartLine intent
      return output
    }

  /// Real recent-eval history for the resolved session (roast-7 §3a/§16
  /// item 3) — this used to be hardcoded to "Recent events: none recorded"
  /// regardless of what actually happened, even though `send_fsharp_code`'s
  /// own tool description tells agents to call this "if the return value is
  /// ambiguous". Reads the same `FeaturePushState.EvalHistory` the
  /// dashboard filmstrip and `plan_ripple`/`impact_forecast` already read
  /// (`Features.FeatureHooks.recentEvals`) — never a hardcoded string.
  ///
  /// Both write paths now feed this history: the daemon's `/exec` HTTP bridge
  /// (McpServer.fs, CLI-integrated client) AND the pure-MCP `send_fsharp_code`
  /// path, which records via `McpContext.RecordEval` — the write counterpart to
  /// GetFeatureState, wired to the same `FeaturePushState ref` (roast-7 §2/§3
  /// follow-up, the gap this comment used to describe). This function is honest
  /// either way: real events when they exist, a plain "no events yet" when they
  /// do not — never a lie.
  let getRecentEvents (ctx: McpContext) (agent: string) (count: int) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      match featureStateForSession ctx sid with
      | None ->
        return "Feature state not available — no active session."
      | Some state ->
        match Features.FeatureHooks.recentEvals count state with
        | [] ->
          return "No FSI events recorded yet for this session."
        | events ->
          let lines =
            events
            |> List.map (fun (e: Features.FeatureHooks.EvalHistoryEntry) ->
              let kind =
                match e.Result.StartsWith("Error:", StringComparison.Ordinal) with
                | true -> "Error"
                | false -> "Eval"
              sprintf "[%s] #%d (%dms) %s: %s -> %s"
                (e.Timestamp.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
                e.CellIndex e.DurationMs kind
                (McpAdapter.previewLine 120 e.Code)
                (McpAdapter.previewLine 200 e.Result))
          return sprintf "Recent events (%d, oldest first):\n%s" events.Length (String.concat "\n" lines)
    })

  /// Render a non-Routable resolution (WarmingUp, Unroutable, or
  /// FaultedSession) as get_fsi_status's structured JSON. Shared by the
  /// top-level resolution match AND by the Routable branch's own transport-
  /// failure fallback, so a session that is ACTUALLY Faulted (per the
  /// registry) is reported as Faulted — with its real faultReason — no
  /// matter which path noticed. Before this was shared, a Routable session
  /// whose worker died between the proxy lookup and the GetStatus call fell
  /// through to a bare "Error getting status: ..." string that hid a
  /// faultReason the registry already had, which is exactly the disagreement
  /// three onboarding trials hit (2026-09-22): get_fsi_status reading
  /// "Starting" while /api/sessions and the daemon log already knew Failed.
  /// INVARIANT: called only with WarmingUp / Unroutable / FaultedSession —
  /// Routable and Gone are handled by their own callers and never reach here.
  let internal renderWarmingOrFaulted (ctx: McpContext) (resolution: SessionResolution) : Task<string> =
    task {
      match resolution with
      | WarmingUp (sid, status) | Unroutable (sid, status) ->
        let availableTools = Affordances.availableTools SessionState.WarmingUp
        let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
        let elapsedSeconds =
          info |> Option.map (fun i -> (DateTime.UtcNow - i.CreatedAt).TotalSeconds)
        let! progress = ctx.SessionOps.GetWarmupProgress (toSessionId sid)
        return
          Json.serialize Json.standard
            {| state = "Rebuilding"
               sessionId = sid
               status = WorkerProtocol.SessionLifecycleStatus.label status
               message = formatSessionResolution resolution
               elapsedSeconds = elapsedSeconds
               // The worst-case wall clock a warming session can take: the
               // pre-port phase's absolute ceiling (Timeouts.warmupAbsoluteMax
               // — silence trips it sooner, at warmupInactivityLimit since
               // the last WARMUP_PROGRESS= line) plus the post-port
               // ready-poll's own bound.
               boundSeconds = Timeouts.warmupAbsoluteMax.TotalSeconds + Timeouts.warmupReadyPollMax.TotalSeconds
               inactivityBoundSeconds = Timeouts.warmupInactivityLimit.TotalSeconds
               progress = progress
               available = availableTools |}
      | FaultedSession (sid, cause) ->
        let availableTools = Affordances.availableTools SessionState.Faulted
        return
          Json.serialize Json.standard
            {| state = "Faulted"
               sessionId = sid
               faultReason = FaultCause.describe cause
               message = formatSessionResolution resolution
               available = availableTools |}
      | Routable sid ->
        // Defensive only — see INVARIANT above. Never expected in practice.
        return Json.serialize Json.standard {| state = "Rebuilding"; sessionId = sid; message = "" |}
      | Gone msg ->
        return Json.serialize Json.standard {| state = "NoSession"; message = msg |}
    }

  let getDaemonStatus (ctx: McpContext) : Task<string> =
    task {
      let machineMemory = SageFs.Features.MachineMemory.current ()
      let health = ctx.GetDaemonHealth ()
      let telemetry = ctx.GetProcessTelemetry ()
      let leases = SageFs.Features.LeaseWatch.snapshot ()
      let anomalyRows =
        health
        |> Option.bind (fun h -> Some h.Anomalies)
        |> Option.defaultValue []
        |> List.choose (fun verdict ->
          SageFs.Features.HealthAnomaly.evidenceOf verdict
          |> Option.map (fun evidence ->
            {| signal = SageFs.Features.HealthAnomaly.signalName evidence.Signal
               state = SageFs.Features.HealthAnomaly.verdictName verdict
               message = SageFs.Features.HealthAnomaly.describe verdict |> Option.defaultValue ""
               observedValue = evidence.ObservedValue
               baselineMean = evidence.BaselineMean
               baselineStdDev = evidence.BaselineStdDev
               deviationInSigmas = evidence.DeviationInSigmas
               sustainedForSeconds = evidence.SustainedFor.TotalSeconds
               samplesSustained = evidence.SamplesSustained |}))
      let sessionSummaries =
        health |> Option.map (fun h -> h.SessionSummaries) |> Option.defaultValue []
      let countStatus status =
        sessionSummaries |> List.filter (fun session -> session.Status = status) |> List.length
      let payload = {|
        state = "Ready"
        scope = "Daemon"
        daemonVersion = health |> Option.map (fun h -> h.Version) |> Option.defaultValue SageFs.Server.DaemonInfo.version
        coreVersion = SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current ()
        daemonPid = health |> Option.map (fun h -> h.DaemonPid) |> Option.defaultValue Environment.ProcessId
        mcpPort = health |> Option.map (fun h -> h.DaemonPort) |> Option.defaultValue ctx.McpPort
        uptimeSeconds =
          health |> Option.map (fun h -> h.Uptime.TotalSeconds) |> Option.defaultValue 0.0
        overall = health |> Option.map (fun h -> SageFs.Features.DaemonHealth.healthLabel (SageFs.Features.DaemonHealth.overallStatus h)) |> Option.defaultValue "Unknown"
        memoryPressure =
          health |> Option.map (fun h -> SageFs.MemoryPressure.describe h.MemoryPressure) |> Option.defaultValue (SageFs.MemoryPressure.describe SageFs.MemoryPressure.Normal)
        memoryPressureNote =
          health |> Option.map (fun h -> SageFs.MemoryPressure.explain h.MemoryPressure) |> Option.defaultValue ""
        machineMemory =
          {| totalBytes = machineMemory.TotalBytes
             availableBytes = machineMemory.AvailableBytes |}
        daemonResidentBytes = health |> Option.map (fun h -> int64 h.MemoryMB * 1_048_576L) |> Option.defaultValue 0L
        aggregateResidentBytes = telemetry |> Option.map (fun t -> t.AggregateResidentBytes) |> Option.defaultValue 0L
        aggregateCpuPercent = telemetry |> Option.map (fun t -> t.AggregateCpuPercent) |> Option.defaultValue 0.0
        telemetrySampledAt = telemetry |> Option.map (fun t -> t.SampledAt) |> Option.defaultValue DateTimeOffset.UtcNow
        processes = telemetry |> Option.map (fun t -> t.Processes) |> Option.defaultValue []
        sessions = {|
          total = sessionSummaries |> List.length
          ready = countStatus SageFs.Features.SessionHealthStatus.Ready
          evaluating = countStatus SageFs.Features.SessionHealthStatus.Evaluating
          warmingUp = countStatus SageFs.Features.SessionHealthStatus.WarmingUp
          faulted = countStatus SageFs.Features.SessionHealthStatus.Faulted
          stopped = countStatus SageFs.Features.SessionHealthStatus.Stopped
        |}
        leases = {| activeCount = leases.ActiveCount
                    queueDepth = leases.QueueDepth
                    active = leases.Active
                    queue = leases.Queue |}
        anomalies = anomalyRows
        available = SageFs.Affordances.availableTools SageFs.SessionState.Uninitialized
      |}
      return Json.serialize Json.standard payload
    }

  let private leaseDecisionJson kind decision : string =
    let kindName = SageFs.ExpensiveWorkLease.Kind.toToken kind
    match decision with
    | SageFs.ExpensiveWorkLease.Decision.Granted(leaseId, expiresAt) ->
      Json.serialize Json.standard
        {| kind = kindName
           decision = "granted"
           leaseId = SageFs.ExpensiveWorkLease.LeaseId.value leaseId
           expiresAt = expiresAt |}
    | SageFs.ExpensiveWorkLease.Decision.Wait(retryAfter, reason) ->
      Json.serialize Json.standard
        {| kind = kindName
           decision = "wait"
           retryAfterSeconds = retryAfter.TotalSeconds
           reason = reason |}
    | SageFs.ExpensiveWorkLease.Decision.Refused reason ->
      Json.serialize Json.standard
        {| kind = kindName
           decision = "refused"
           reason = reason |}

  let acquireWorkLease (agent: string) (kind: SageFs.ExpensiveWorkLease.Kind) : string =
    let holder = resolvedKey agent
    let decision = SageFs.Features.LeaseWatch.request holder kind
    leaseDecisionJson kind decision

  let releaseWorkLease (agent: string) (leaseId: string) : Result<string, SageFsError> =
    match String.IsNullOrWhiteSpace leaseId with
    | true -> Error (SageFsError.JsonParseError("release_work_lease", "lease_id is required"))
    | false ->
      let id = SageFs.ExpensiveWorkLease.LeaseId.ofWire leaseId
      let holder = resolvedKey agent
      match SageFs.Features.LeaseWatch.releaseOwned holder id with
      | SageFs.ExpensiveWorkLease.ReleaseOutcome.Released -> Ok "released"
      | SageFs.ExpensiveWorkLease.ReleaseOutcome.AlreadyGone -> Ok "already_gone_or_not_owned"

  let private sessionStatusPayload
    (ctx: McpContext)
    (agent: string)
    (sessionId: string option)
    (workingDirectory: string option)
    : Task<string> =
    task {
      let! resolution = resolveSessionId ctx agent sessionId workingDirectory
      match resolution with
      | Gone message ->
        return Json.serialize Json.standard (
          {| state = "NoSession"
             scope = "Session"
             message = message |})
      | WarmingUp (sid, status) | Unroutable (sid, status) ->
        let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
        let targets = info |> Option.bind (fun value -> SessionProjectTarget.tryCreateMany value.Projects |> Result.toOption) |> Option.defaultValue []
        return Json.serialize Json.standard (
          {| state = "WarmingUp"
             scope = "Session"
             sessionId = sid
             lifecycle = WorkerProtocol.SessionLifecycleStatus.label status
             target = targets |> List.map SessionProjectTarget.toWire
             loadedProjects = info |> Option.map (fun value -> value.ProjectRoles |> List.map _.Path) |> Option.defaultValue []
             workerPid = WorkerProtocol.SessionLifecycleStatus.workerPid status
             workerPort = WorkerProtocol.SessionLifecycleStatus.workerPort status
             lastRestart = SessionStatusPayload.lastRestartJson (SessionStatusPayload.lastRestartOfSession info None)
             lastReload = SessionReload.toWire (SessionStatusPayload.lastReloadOfSession info)
             replFreshness = ReplFreshness.toWire (SessionStatusPayload.replFreshnessOfSession info)
             available = SageFs.Affordances.availableTools SageFs.SessionState.WarmingUp |})
      | FaultedSession (sid, cause) ->
        let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
        let targets = info |> Option.bind (fun value -> SessionProjectTarget.tryCreateMany value.Projects |> Result.toOption) |> Option.defaultValue []
        return Json.serialize Json.standard (
          {| state = "Faulted"
             scope = "Session"
             sessionId = sid
             faultReason = FaultCause.describe cause
             target = targets |> List.map SessionProjectTarget.toWire
             loadedProjects = info |> Option.map (fun value -> value.ProjectRoles |> List.map _.Path) |> Option.defaultValue []
             lastRestart = SessionStatusPayload.lastRestartJson (SessionStatusPayload.lastRestartOfSession info None)
             lastReload = SessionReload.toWire (SessionStatusPayload.lastReloadOfSession info)
             replFreshness = ReplFreshness.toWire (SessionStatusPayload.replFreshnessOfSession info)
             available = SageFs.Affordances.availableTools SageFs.SessionState.Faulted |})
      | Routable sid ->
        let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
        let! routeResult =
          routeToSession ctx sid (fun replyId -> WorkerProtocol.WorkerMessage.GetStatus (WorkerProtocol.SessionId.value replyId))
        match info, routeResult with
        | Some sessionInfo, Ok (WorkerProtocol.WorkerResponse.StatusResult(_, snapshot)) ->
          let targets = SessionProjectTarget.tryCreateMany sessionInfo.Projects |> Result.defaultValue []
          let reconciliation =
            SageFs.ProjectResolution.reconcile targets sessionInfo.ProjectRoles.Length sessionInfo.Status snapshot.Status
          let reconciledStatus =
            match reconciliation with
            | SageFs.ProjectResolution.ReconciledStatus.Reconciled status -> status
            | SageFs.ProjectResolution.ReconciledStatus.NotYetEarned current -> current
          // Agents compare list_sessions with this status before trusting the
          // REPL, so the registry follows the live worker when they differ.
          match reconciliation with
          | SageFs.ProjectResolution.ReconciledStatus.Reconciled status when sessionInfo.Status <> status ->
            do! ctx.SessionOps.UpdateSessionStatus (toSessionId sid) status
          | _ -> ()
          let! warmup =
            match ctx.GetWarmupContext with
            | Some getCtx -> getCtx sid
            | None -> Task.FromResult None
          let health = SessionHealth.classify reconciledStatus sessionInfo.ProjectRoles warmup
          // ONE reconciled status drives the top-level state AND the
          // lifecycle. The old code hard-coded `state = "Ready"` while
          // lifecycle, loadedProjects and health came from the reconciled
          // status, so a warming session reported "Ready" beside
          // "lifecycle: Starting" and "loadedProjects: []" — an agent gating
          // on `state == Ready` walked straight into a session that was not
          // loaded. Dogfooded live: the exact contradiction the roast reported.
          return SessionStatusPayload.serialize
            { SessionState = WorkerProtocol.SessionLifecycleStatus.toSessionState reconciledStatus
              SessionId = sid
              Target = targets
              LoadedProjects = sessionInfo.ProjectRoles |> List.map _.Path
              ReconciledStatus = reconciledStatus
              Workflow = sessionInfo.Workflow
              CoreVersion = snapshot.CoreVersion
              EvalCount = snapshot.EvalCount
              AverageDurationMs = snapshot.AvgDurationMs
              Health = SessionHealth.toJson health
              // The hard-reset tool answers "initiated" and points here for the
              // result. Without this a failed rebuild was recorded and then
              // never shown, so it read exactly like one still running.
              LastRestart = SessionStatusPayload.lastRestartOfSession info (Some snapshot.CoreVersion)
              LastReload = sessionInfo.Reload
              ReplFreshness = sessionInfo.Freshness }
        | _, _ ->
          return! renderWarmingOrFaulted ctx resolution
    }

  /// Park on AwaitReady when the session is on its way to Ready, for at most
  /// `requested`. A session that is Ready, Faulted or Stopped, one that does
  /// not exist, and a request for no wait all return at once.
  let private awaitWarmingSession
    (ctx: McpContext)
    (agent: string)
    (sessionId: string option)
    (workingDirectory: string option)
    (requested: TimeSpan)
    : Task<SessionStatusPayload.WaitReport> =
    task {
      match requested > TimeSpan.Zero with
      | false -> return SessionStatusPayload.StatusWait.notWaited
      | true ->
        let! resolution = resolveSessionId ctx agent sessionId workingDirectory
        let target =
          match resolution with
          | Routable sid
          | WarmingUp (sid, _)
          | Unroutable (sid, _)
          | FaultedSession (sid, _) -> Some sid
          | Gone _ -> None
        match target with
        | None -> return SessionStatusPayload.StatusWait.notWaited
        | Some sid ->
          let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
          match info |> Option.map (fun i -> SessionStatusPayload.StatusWait.planFor i.Status) with
          | Some SessionStatusPayload.WaitPlan.Park ->
            let clock = System.Diagnostics.Stopwatch.StartNew()
            let! answer = ctx.SessionOps.AwaitReady (toSessionId sid) requested
            return
              ({ Outcome = SessionStatusPayload.StatusWait.ofAwaitReady answer
                 WaitedMs = clock.ElapsedMilliseconds } : SessionStatusPayload.WaitReport)
          | Some SessionStatusPayload.WaitPlan.DoNotPark
          | None -> return SessionStatusPayload.StatusWait.notWaited
    }

  /// get_session_status with an optional wait: a session that is Starting,
  /// Building or Restarting parks the call on the SessionManager's AwaitReady
  /// for up to `waitSeconds` (clamped to 0..Timeouts.statusWaitCap), then the
  /// normal payload is read, with a `wait` field saying what the wait did.
  let getSessionStatusAwaiting
    (ctx: McpContext)
    (agent: string)
    (sessionId: string option)
    (workingDirectory: string option)
    (waitSeconds: int)
    : Task<string> =
    task {
      let! report =
        awaitWarmingSession ctx agent sessionId workingDirectory
          (SessionStatusPayload.StatusWait.clampSeconds waitSeconds)
      let! payload = sessionStatusPayload ctx agent sessionId workingDirectory
      return SessionStatusPayload.StatusWait.withReport report payload
    }

  /// get_session_status without a wait.
  let getSessionStatus
    (ctx: McpContext)
    (agent: string)
    (sessionId: string option)
    (workingDirectory: string option)
    : Task<string> =
    getSessionStatusAwaiting ctx agent sessionId workingDirectory 0

  let getStartupInfo (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
      match info with
      | Some sessionInfo ->
        let header =
          sprintf "📋 Startup Information:\n- Session: %s\n- Working Directory: %s\n- Projects: %s\n- MCP Port: %d\n- Status: %s"
            sid
            sessionInfo.WorkingDirectory
            (match sessionInfo.Projects.IsEmpty with
             | true -> "None"
             | false -> String.concat ", " (sessionInfo.Projects |> List.map Path.GetFileName))
            ctx.McpPort
            (WorkerProtocol.SessionLifecycleStatus.label sessionInfo.Status)
        // Fetch and append warmup detail
        let! warmupDetail =
          match ctx.GetWarmupContext with
          | Some getCtx ->
            task {
              let! wCtx = getCtx sid
              match wCtx with
              | Some warmup ->
                let sessionCtx : SessionContext = {
                  SessionId = sid
                  ProjectNames = sessionInfo.Projects
                  WorkingDir = sessionInfo.WorkingDirectory
                  Status = WorkerProtocol.SessionLifecycleStatus.label sessionInfo.Status
                  Warmup = warmup
                  FileStatuses = []
                  Workflow = sessionInfo.Workflow
                  AutoOpenNamespaces = DirectoryConfig.autoOpenNamespacesForDirectory sessionInfo.WorkingDirectory
                }
                return sprintf "\n\n%s" (McpAdapter.formatWarmupDetailForLlm sessionCtx)
              | None -> return ""
            }
          | None -> Task.FromResult("")
        return header + warmupDetail
      | None ->
        return "SageFs startup information not available yet — session is still initializing"
    })

  /// Recursively find `.fsproj` under `root`, PRUNING noise directories
  /// (`McpAdapter.projectNoiseSegments`) so the walk never descends into build
  /// output or the per-agent worktrees under `.claude/worktrees` — each a full
  /// checkout that otherwise multiplies every project into a 1,600-path firehose
  /// (dogfood finding, 2026-09-14). Per-directory IO errors are swallowed so one
  /// unreadable subtree can't fail the whole discovery.
  let rec private walkProjectFiles (root: string) : string seq =
    seq {
      let files = try Directory.EnumerateFiles(root, "*.fsproj") |> Seq.toArray with _ -> [||]
      yield! files
      let subdirs = try Directory.EnumerateDirectories root |> Seq.toArray with _ -> [||]
      for d in subdirs do
        let name = Path.GetFileName(d.TrimEnd('/', '\\'))
        if not (McpAdapter.projectNoiseSegments.Contains name) then
          yield! walkProjectFiles d
    }

  /// How many projects to surface to an agent before summarizing the rest — a
  /// list-style result must fit an agent's context (dogfood finding: the old
  /// unbounded result overflowed it even under the 256 KiB tool-output cap).
  let availableProjectsDisplayCap = 40

  let getAvailableProjects (ctx: McpContext) (_agent: string) (workingDirectory: string option) : Task<string> =
    task {
      // Resolve working directory without requiring a session.
      // Try: explicit working_directory → active session's directory → Environment.CurrentDirectory
      let! workingDir = task {
        match workingDirectory with
        | Some wd when not (String.IsNullOrWhiteSpace wd) -> return wd
        | _ ->
          // Try to get the working directory from any active session, but don't fail if none exists
          let! sessions = ctx.SessionOps.GetAllSessions()
          match sessions with
          | [ single ] -> return single.WorkingDirectory
          | _ -> return Environment.CurrentDirectory
      }

      match Directory.Exists workingDir with
      | false ->
        return sprintf "Directory '%s' does not exist. Pass an existing directory as working_directory (an absolute path is safest)." workingDir
      | true ->
      let shownProjects, totalProjects =
        try
          walkProjectFiles workingDir
          |> Seq.filter McpAdapter.isProjectFile
          |> Seq.map (fun p -> Path.GetRelativePath(workingDir, p))
          |> McpAdapter.selectProjectsForDisplay availableProjectsDisplayCap
        with
        | :? System.OperationCanceledException -> reraise()
        | _ -> [||], 0

      let solutions =
        try
          Directory.EnumerateFiles workingDir
          |> Seq.filter McpAdapter.isSolutionFile
          |> Seq.map Path.GetFileName
          |> Seq.toArray
        with
        | :? System.OperationCanceledException -> reraise()
        | _ -> [||]

      return McpAdapter.formatAvailableProjects workingDir shownProjects solutions (totalProjects - shownProjects.Length)
    }

  let loadFSharpScript (ctx: McpContext) (agentName: string) (filePath: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSession ctx agentName sessionId workingDirectory (fun sid -> task {
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.LoadScript(filePath, WorkerProtocol.SessionId.value replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.ScriptLoaded(_, Ok msg)) -> msg
        | Ok (WorkerProtocol.WorkerResponse.ScriptLoaded(_, Error err)) ->
          sprintf "Error: %s" (SageFsError.describeForAgent err)
        | Ok (WorkerProtocol.WorkerResponse.WorkerError err) ->
          sprintf "Error: %s" (SageFsError.describeForAgent err)
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
    })

  /// Result-returning sibling of loadFSharpScript. ScriptLoaded's own Error
  /// case and WorkerError already carry a real SageFsError — this just stops
  /// throwing that structure away by formatting it into "Error: ..." text
  /// for a caller that is only going to check the prefix and discard the rest.
  let loadFSharpScriptResult (ctx: McpContext) (agentName: string) (filePath: string) (sessionId: string option) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    withSessionResult ctx agentName sessionId workingDirectory (fun sid -> task {
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.LoadScript(filePath, WorkerProtocol.SessionId.value replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.ScriptLoaded(_, Ok msg)) ->
          // issue #143: this is the same raw FSI-captured output as
          // send_fsharp_code's EvalResult (LoadScript evaluates a #load
          // directive through the same Eval path) — it never routed through
          // formatWorkerEvalResult's stripAnsi, so it was a second leak.
          Ok (stripAnsi msg)
        | Ok (WorkerProtocol.WorkerResponse.ScriptLoaded(_, Error err)) -> Error err
        | Ok (WorkerProtocol.WorkerResponse.WorkerError err) -> Error err
        | Ok other -> Ok (sprintf "Unexpected response: %A" other)
        | Error msg -> Error (SageFsError.ScriptLoadFailed (routeErrorMessage msg))
    })

  let resetSession (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSessionAllowFaulted ctx agent sessionId workingDirectory (fun sid -> task {
      let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
      let previousStatus =
        info
        |> Option.map (fun sessionInfo -> sessionInfo.Status)
        |> Option.defaultValue (WorkerProtocol.SessionLifecycleStatus.Faulted (WorkerProtocol.FaultReason.Unexplained WorkerProtocol.FaultOrigin.NotRecorded))
      do! setSnapshotStatus ctx sid (startingWhileReset previousStatus)
      notifyElm ctx (
        TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Starting))
      let! routeResult =
        task {
          try
            let resetTask =
              routeToSession ctx sid
                (fun replyId -> WorkerProtocol.WorkerMessage.ResetSession (WorkerProtocol.SessionId.value replyId))
            return! resetTask.WaitAsync(Timeouts.softResetCancellation)
          with
          | :? OperationCanceledException ->
            return Result.Error (Message (sprintf "Session '%s' did not respond to reset after %A. The session may be stuck. Try recovery: use stop_session followed by one of the explicit create tools (create_project_session, create_solution_session, create_bare_session) to force a fresh start." sid Timeouts.softResetCancellation))
        }
      match routeResult with
      | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Ok ())) ->
        do! setSnapshotStatus ctx sid (readyAfterReset previousStatus)
        compilationStates.TryRemove(sid) |> ignore
        notifyElm ctx (
          TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Running))
        // Pushback: resetting a healthy (Ready) session destroys live REPL
        // definitions — say so explicitly. Faulted/Starting sessions have
        // nothing to lose, so no warning.
        let warning =
          match previousStatus with
          | WorkerProtocol.SessionLifecycleStatus.Ready _ -> "⚠️ NOTE: resetting clears all REPL definitions and evaluation history. "
          | _ -> ""
        return sprintf "%sSession reset successfully. All previous definitions have been cleared." warning
      | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Error err)) ->
        do! setSnapshotStatus ctx sid (WorkerProtocol.SessionLifecycleStatus.Faulted (WorkerProtocol.FaultReason.report (SageFsError.describe err)))
        notifyElm ctx (
          TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Faulted (SageFsError.describe err)))
        return sprintf "Error: %s" (SageFsError.describeForAgent err)
      | Ok other ->
        do! setSnapshotStatus ctx sid previousStatus
        return sprintf "Unexpected response: %A" other
      | Error msg ->
        let err = routeErrorMessage msg
        match routeErrorIsTransportFailure msg with
        | true ->
          do! setSnapshotStatus ctx sid (WorkerProtocol.SessionLifecycleStatus.Faulted (WorkerProtocol.FaultReason.report err))
          notifyElm ctx (
            TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Faulted err))
        | false ->
          do! setSnapshotStatus ctx sid previousStatus
        return sprintf "Error: %s" err
    })

  /// Result-returning sibling of resetSession. Same side effects (status
  /// writes, notifyElm) — only the return value stops being a formatted
  /// "Error: ..."/"...NOTE: ..." string a caller has to sniff a prefix off.
  let resetSessionResult (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    withSessionAllowFaultedResult ctx agent sessionId workingDirectory (fun sid -> task {
      let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
      let previousStatus =
        info
        |> Option.map (fun sessionInfo -> sessionInfo.Status)
        |> Option.defaultValue (WorkerProtocol.SessionLifecycleStatus.Faulted (WorkerProtocol.FaultReason.Unexplained WorkerProtocol.FaultOrigin.NotRecorded))
      do! setSnapshotStatus ctx sid (startingWhileReset previousStatus)
      notifyElm ctx (
        TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Starting))
      let! routeResult =
        task {
          try
            let resetTask =
              routeToSession ctx sid
                (fun replyId -> WorkerProtocol.WorkerMessage.ResetSession (WorkerProtocol.SessionId.value replyId))
            return! resetTask.WaitAsync(Timeouts.softResetCancellation)
          with
          | :? OperationCanceledException ->
            return Result.Error (Message (sprintf "Session '%s' did not respond to reset after %A. The session may be stuck. Try recovery: use stop_session followed by one of the explicit create tools (create_project_session, create_solution_session, create_bare_session) to force a fresh start." sid Timeouts.softResetCancellation))
        }
      match routeResult with
      | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Ok ())) ->
        do! setSnapshotStatus ctx sid (readyAfterReset previousStatus)
        compilationStates.TryRemove(sid) |> ignore
        notifyElm ctx (
          TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Running))
        let warning =
          match previousStatus with
          | WorkerProtocol.SessionLifecycleStatus.Ready _ -> "⚠️ NOTE: resetting clears all REPL definitions and evaluation history. "
          | _ -> ""
        return Ok (sprintf "%sSession reset successfully. All previous definitions have been cleared." warning)
      | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Error err)) ->
        do! setSnapshotStatus ctx sid (WorkerProtocol.SessionLifecycleStatus.Faulted (WorkerProtocol.FaultReason.report (SageFsError.describe err)))
        notifyElm ctx (
          TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Faulted (SageFsError.describe err)))
        return Error err
      | Ok other ->
        do! setSnapshotStatus ctx sid previousStatus
        return Ok (sprintf "Unexpected response: %A" other)
      | Error msg ->
        let reason = routeErrorMessage msg
        match routeErrorIsTransportFailure msg with
        | true ->
          do! setSnapshotStatus ctx sid (WorkerProtocol.SessionLifecycleStatus.Faulted (WorkerProtocol.FaultReason.report reason))
          notifyElm ctx (
            TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Faulted reason))
          return Error (SageFsError.WorkerCommunicationFailed (sid, reason))
        | false ->
          do! setSnapshotStatus ctx sid previousStatus
          return Error (SageFsError.ResetFailed reason)
    })

  let checkFSharpCode (ctx: McpContext) (agent: string) (code: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSession ctx agent sessionId workingDirectory (fun sid -> task {
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.CheckCode(code, WorkerProtocol.SessionId.value replyId))
      // A check runs in the REPL's FSI host, against the build from before a patch the app took.
      let! freshness = freshnessOfSession ctx sid
      let report =
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.CheckResult(_, diags)) ->
          match List.isEmpty diags with
          | true -> "No errors found."
          | false ->
            let lines =
              diags
              |> List.map (fun d ->
                sprintf "[%s] (%d,%d) %s"
                  (Features.Diagnostics.DiagnosticSeverity.label d.Severity)
                  d.StartLine d.StartColumn
                  d.Message)
            let remediation =
              diags
              |> List.tryHead
              |> Option.map (fun d ->
                SageFs.ErrorMessages.getSuggestion (SageFs.ErrorMessages.categorizeByNumber (Some d.ErrorNumber) d.Message))
              |> Option.defaultValue ""
            String.concat "\n" lines + "\n\n" + remediation
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
      return ReplFreshness.annotate freshness report
    })

  /// What a rebuild=true hard reset answers straight away; the outcome lands in
  /// get_session_status (`lastRestart`) when the build finishes.
  let private rebuildInitiatedMessage =
    "Hard reset initiated — building first; the current worker keeps serving until the new build is ready. get_session_status reports the rebuild's progress and outcome."

  /// Starts a rebuild=true hard reset in the background. The text tool and the
  /// Result tool each carried their own copy of this, so a fix had to land twice.
  ///
  /// Fire-and-forget: the build runs in the background so the MCP call doesn't
  /// time out; get_session_status reports progress and the outcome.
  ///
  /// The SessionManager mailbox is the single owner of the session registry,
  /// restart coalescing AND the record of what the rebuild did (`Info.Rebuild`),
  /// so the dashboard button, the live-testing effect and app-run get the same
  /// record this tool does. It rejects a second hard reset while a rebuild is in
  /// flight (a refusal is not an outcome: the record of the rebuild that IS
  /// running is left alone), keeps the live worker serving through a build-first
  /// rebuild, and marks a cold restart Restarting itself. So this tool writes NO
  /// session status and keeps no outcome of its own.
  let private startTrackedRebuild (ctx: McpContext) (sid: string) : unit =
    compilationStates.TryRemove(sid) |> ignore
    typeIdentityDiagnostics.TryRemove(sid) |> ignore
    notifyElm ctx (
      TuiEvent.WarmupProgress (1, 4, "Building project..."))
    task {
      let! threw =
        task {
          try
            let! _ = ctx.SessionOps.RestartSession (toSessionId sid) (RestartPlan.Rebuild GranularRestart.RestartSubject.Worker)
            return None
          with ex -> return Some ex
        }
      let! after = ctx.SessionOps.GetSessionInfo (toSessionId sid)
      let display =
        match threw, after |> Option.map (fun info -> info, info.Rebuild) with
        // The owner never saw this call, so it recorded nothing: say what threw.
        | Some ex, _ -> SessionDisplayStatus.Faulted (SageFsError.describe (SageFsError.Unexpected ex))
        | None, Some (_, LastRebuild.Latest (RebuildOutcome.FailedNotServing (error, _))) -> SessionDisplayStatus.Faulted (SageFsError.describe error)
        | None, Some (info, _) -> SessionDisplay.displayStatus DateTime.UtcNow info
        | None, None -> SessionDisplayStatus.Faulted "Session is no longer registered"
      notifyElm ctx (TuiEvent.SessionStatusChanged (sid, display))
    } |> ignore

  let hardResetSession (ctx: McpContext) (agent: string) (rebuild: bool) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSessionAllowFaulted ctx agent sessionId workingDirectory (fun sid -> task {
      match rebuild with
      | true ->
        startTrackedRebuild ctx sid
        return rebuildInitiatedMessage
      | false ->
        // A hard reset without a rebuild must still replace the worker
        // PROCESS: an in-process FSI rebuild keeps whatever the worker's
        // Default load context already has loaded (the project's own
        // assemblies included), so new code built since the last spawn was
        // never picked up — the reply claimed "re-copied assemblies" while
        // reusing the stale ones. spawnFirst is the only thing that replaces
        // them. The owner decides Ready/Faulted/pid the same way the
        // rebuild=true path does, so this call writes no session status.
        compilationStates.TryRemove(sid) |> ignore
        typeIdentityDiagnostics.TryRemove(sid) |> ignore
        let! result =
          task {
            try return! ctx.SessionOps.RestartSession (toSessionId sid) RestartPlan.RespawnOnly
            with ex -> return Error (SageFsError.Unexpected ex)
          }
        match result with
        | Ok msg ->
          notifyElm ctx (
            TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Running))
          return "⚠️ NOTE: hard reset restarts the session and clears all REPL definitions. " + msg
        | Error err ->
          notifyElm ctx (
            TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Faulted (SageFsError.describe err)))
          return sprintf "Error: %s" (SageFsError.describeForAgent err)
    })

  /// Result-returning sibling of hardResetSession. rebuild=true's own reply
  /// is unconditionally informational (the real outcome lands later via
  /// get_fsi_status, same as before) — only rebuild=false's Error err, which
  /// already carries a real SageFsError, stops being flattened into text.
  let hardResetSessionResult (ctx: McpContext) (agent: string) (rebuild: bool) (sessionId: string option) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    withSessionAllowFaultedResult ctx agent sessionId workingDirectory (fun sid -> task {
      match rebuild with
      | true ->
        startTrackedRebuild ctx sid
        return Ok rebuildInitiatedMessage
      | false ->
        compilationStates.TryRemove(sid) |> ignore
        typeIdentityDiagnostics.TryRemove(sid) |> ignore
        let! result =
          task {
            try return! ctx.SessionOps.RestartSession (toSessionId sid) RestartPlan.RespawnOnly
            with ex -> return Error (SageFsError.Unexpected ex)
          }
        match result with
        | Ok msg ->
          notifyElm ctx (
            TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Running))
          return Ok ("⚠️ NOTE: hard reset restarts the session and clears all REPL definitions. " + msg)
        | Error err ->
          notifyElm ctx (
            TuiEvent.SessionStatusChanged (sid, SessionDisplayStatus.Faulted (SageFsError.describe err)))
          return Error err
    })

  let cancelEval (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let! routeResult =
        routeToSession ctx sid
          (fun _ -> WorkerProtocol.WorkerMessage.CancelEval)
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.EvalCancelled true) ->
          notifyElm ctx (TuiEvent.EvalCancelled sid)
          "Evaluation cancelled."
        | Ok (WorkerProtocol.WorkerResponse.EvalCancelled false) ->
          "No evaluation in progress."
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
    })

  /// Result-returning sibling of cancelEval, for callers that branch on
  /// success/failure structurally (the plain HTTP surface) instead of
  /// string-sniffing. Carries routeToSession's own Result straight through
  /// instead of formatting it into a display string and then re-parsing
  /// that string's prefix one layer up.
  let cancelEvalResult (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    withSessionWdResult ctx agent workingDirectory (fun sid -> task {
      let! routeResult =
        routeToSession ctx sid
          (fun _ -> WorkerProtocol.WorkerMessage.CancelEval)
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.EvalCancelled true) ->
          notifyElm ctx (TuiEvent.EvalCancelled sid)
          Ok "Evaluation cancelled."
        | Ok (WorkerProtocol.WorkerResponse.EvalCancelled false) ->
          Ok "No evaluation in progress."
        | Ok other -> Ok (sprintf "Unexpected response: %A" other)
        | Error msg -> Error (SageFsError.CancelFailed (routeErrorMessage msg))
    })

  let getCompletions (ctx: McpContext) (agent: string) (code: string) (cursorPosition: int) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.GetCompletions(code, cursorPosition, WorkerProtocol.SessionId.value replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.CompletionResult(_, completions)) ->
          match List.isEmpty completions with
          | true -> "No completions available."
          | false -> String.concat "\n" completions
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
    })

  /// Infer a conservative editor completion kind from the labels returned by
  /// the worker transport. The worker currently sends display labels only, so
  /// preserve useful VS Code metadata without pretending this is FCS glyph data.
  let completionKindForLabel (label: string) : Features.AutoCompletion.CompletionKind =
    match System.String.IsNullOrWhiteSpace label with
    | true -> Features.AutoCompletion.CompletionKind.Variable
    | false when label.Contains("(", System.StringComparison.Ordinal) ->
      Features.AutoCompletion.CompletionKind.Method
    | false when System.Char.IsUpper label[0] ->
      Features.AutoCompletion.CompletionKind.Class
    | false -> Features.AutoCompletion.CompletionKind.Variable

  /// Get structured completion items for HTTP/editor clients. Unlike the MCP
  /// tool's human-readable response, this path must always serialize as JSON.
  let getCompletionsItems
    (ctx: McpContext)
    (agent: string)
    (code: string)
    (cursorPosition: int)
    (sessionId: string option)
    (workingDirectory: string option)
    : Task<Features.AutoCompletion.CompletionItem list> =
    task {
      let! resolution = resolveSessionId ctx agent sessionId workingDirectory
      match resolution with
      | Routable sid ->
        let! routeResult =
          routeToSession ctx sid
            (fun replyId ->
              WorkerProtocol.WorkerMessage.GetCompletions(
                code,
                cursorPosition,
                WorkerProtocol.SessionId.value replyId))
        return
          match routeResult with
          | Ok (WorkerProtocol.WorkerResponse.CompletionResult(_, completions)) ->
            completions
            |> List.map (fun label ->
              let item : Features.AutoCompletion.CompletionItem =
                { DisplayText = label
                  ReplacementText = label
                  Kind = completionKindForLabel label
                  GetDescription = None }
              item)
          | _ -> []
      | _ -> return []
    }

  let exploreQualifiedName (ctx: McpContext) (agent: string) (qualifiedName: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let code = sprintf "%s." qualifiedName
      let cursor = code.Length
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.GetCompletions(code, cursor, WorkerProtocol.SessionId.value replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.CompletionResult(_, completions)) ->
          match List.isEmpty completions with
          | true ->
            sprintf "No members found for '%s'" qualifiedName
          | false ->
            let header = sprintf "Members of %s:" qualifiedName
            let items = completions |> List.map (sprintf "  %s") |> String.concat "\n"
            sprintf "%s\n%s" header items
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
    })

  let exploreNamespace (ctx: McpContext) (agent: string) (namespaceName: string) (workingDirectory: string option) : Task<string> =
    exploreQualifiedName ctx agent namespaceName workingDirectory

  let exploreType (ctx: McpContext) (agent: string) (typeName: string) (workingDirectory: string option) : Task<string> =
    exploreQualifiedName ctx agent typeName workingDirectory

  /// MCP tool: visualize a DU type as a state machine diagram.
  /// Sends F# code to the worker that uses reflection to extract DU cases,
  /// then renders an ASCII diagram plus JSON data.
  let visualizeDomainModel (ctx: McpContext) (agent: string) (typeName: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let code =
        sprintf "let _vizType = typeof<%s>\nmatch Microsoft.FSharp.Reflection.FSharpType.IsUnion(_vizType) with\n| true ->\n  let cases =\n    Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(_vizType)\n    |> Array.map (fun uc ->\n      let fields = uc.GetFields() |> Array.map (fun f -> sprintf \"%%s:%%s\" f.Name f.PropertyType.Name)\n      sprintf \"%%s|%%s\" uc.Name (String.concat \",\" fields))\n  printfn \"DUCASES:%%s\" (String.concat \";\" cases)\n| false -> printfn \"DUCASES:NOT_A_DU\"" typeName
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.EvalCode(code, WorkerProtocol.SessionId.value replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.EvalResult(_, result, _, _)) ->
          let output =
            match result with
            | Ok s -> s
            | Error e -> sprintf "%A" e
          let lines = output.Split('\n') |> Array.map (fun s -> s.Trim())
          let duLine = lines |> Array.tryFind (fun l -> l.StartsWith("DUCASES:"))
          match duLine with
          | Some line ->
            let payload = line.Substring(8)
            match payload with
            | "NOT_A_DU" ->
              sprintf "'%s' is not a discriminated union type." typeName
            | casesStr ->
              let cases =
                casesStr.Split(';')
                |> Array.toList
                |> List.choose (fun caseStr ->
                  match caseStr.Split('|') with
                  | [| name; fieldsStr |] ->
                    let fields =
                      match fieldsStr with
                      | "" -> []
                      | fs ->
                        fs.Split(',')
                        |> Array.toList
                        |> List.choose (fun f ->
                          match f.Split(':') with
                          | [| fn; ft |] -> Some (fn, ft)
                          | _ -> None)
                    Some { Features.DomainModelViz.DUCaseInfo.Name = name; Features.DomainModelViz.DUCaseInfo.Fields = fields }
                  | _ -> None)
              let model : Features.DomainModelViz.StateMachineModel =
                { TypeName = typeName; Cases = cases; Transitions = [] }
              let data = Features.DomainModelViz.StateMachineRenderer.renderAsData model
              Json.serialize (Json.indented Json.standard) data
          | None ->
            sprintf "Could not extract DU cases from '%s'. Output: %s" typeName output
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
    })

  // ── Session Management Operations ──────────────────────────────

  /// Pure helper: given package references and the current workflow, format
  /// a non-blocking hint suggesting the user switch to HotReload if detection
  /// finds web packages. Returns None when no suggestion applies.
  /// Decoupled from .fsproj reading for testability — callers provide the list.
  let formatDetectionHint (packageRefs: string list) (currentWorkflow: WorkflowTypes.SessionWorkflow) : string option =
    match currentWorkflow with
    // Interactive and LiveTesting are both non-hot-reload, so a detected web
    // project is worth a hot-reload nudge in either.
    | WorkflowTypes.SessionWorkflow.Interactive
    | WorkflowTypes.SessionWorkflow.LiveTesting ->
      match WorkflowTypes.WorkflowDetection.suggest packageRefs with
      | Some suggestion ->
        let pkgs = suggestion.DetectedPackages |> String.concat ", "
        Some (
          sprintf
            "💡 Detected web packages (%s). Consider switching to Live workflow for hot reload: use switch_workflow tool with target='live'"
            pkgs)
      | None -> None
    | WorkflowTypes.SessionWorkflow.HotReload _ -> None

  /// Read classification markers from a .fsproj: its PackageReference Include
  /// values, PLUS the SDK attribute and FrameworkReference includes that
  /// `WorkflowTypes.ProjectFileMarkers` extracts.
  ///
  /// The project-file markers are what let this see a plain ASP.NET Core /
  /// Minimal API / Oxpecker project at all — those reach ASP.NET through
  /// `Sdk="Microsoft.NET.Sdk.Web"` and a FrameworkReference and have no web
  /// PackageReference, so a package-only read reported them as non-web and the
  /// user was never offered the hot-reload workflow.
  ///
  /// Returns [] on any IO or parse error (non-blocking best-effort).
  let private readFsprojPackageRefs (path: string) : string list =
    let packageRefs =
      try
        let doc = XDocument.Load(path)
        doc.Descendants(XName.Get("PackageReference"))
        |> Seq.choose (fun el ->
          match el.Attribute(XName.Get("Include")) with
          | null -> None
          | a -> Some a.Value)
        |> Seq.toList
      with _ -> []
    // Paket-managed projects (the SAFE stack template among them) carry no
    // <PackageReference> at all — their packages live in a sibling
    // paket.references — so a package-element-only read sees nothing.
    packageRefs
    @ WorkflowTypes.PaketReferences.readForProject path
    @ WorkflowTypes.ProjectFileMarkers.read path

  let createSession (ctx: McpContext) (agent: string) (targets: SessionProjectTarget list) (workingDir: string) (workflowRaw: string) : Task<string> =
    task {
      match SessionProjectTarget.validate targets with
      | Error reason -> return sprintf "Error: %s" reason
      | Ok () ->
      match SessionPathValidation.validateSessionCreateRequest workingDir (SessionProjectTarget.paths targets) with
      | Error err -> return SageFsError.describeForAgent err
      | Ok () ->
      match CreateSessionUx.parseCreateSessionWorkflow workflowRaw with
      | Error msg -> return msg
      | Ok workflow ->
      let! existing = ctx.SessionOps.GetAllSessions()
      let duplicates =
        existing
        |> List.filter (fun s -> CreateSessionUx.isExactDuplicateSession (SessionProjectTarget.paths targets) workingDir s.Projects s.WorkingDirectory)
      match duplicates with
      | dup :: _ ->
        let sid = WorkerProtocol.SessionId.value dup.Id
        let status = WorkerProtocol.SessionLifecycleStatus.label dup.Status
        return sprintf "A session with this exact target and working directory already exists (session '%s', status: %s). Use switch_session to target it instead." sid status
      | [] ->
      let! result = ctx.SessionOps.CreateSession targets workingDir workflow
      ctx.Dispatch |> Option.iter (fun d -> d (SageFsMsg.Editor EditorAction.ListSessions))
      match result with
      | Result.Ok sid ->
        setActiveSessionId ctx agent sid
        claimSessionOwner agent sid
        let perProject =
          targets
          |> List.choose (function
            | SessionProjectTarget.Project path -> Some(path, readFsprojPackageRefs path)
            | SessionProjectTarget.Solution _
            | SessionProjectTarget.Bare -> None)
        let packageRefs = perProject |> List.map snd |> WorkflowTypes.WorkflowDetection.extractPackageNames
        let lines =
          Option.toList (formatDetectionHint packageRefs workflow)
          @ ProjectCompatibility.formatToolchainAdvisories perProject
        let hint = match lines with [] -> None | ls -> Some(String.concat "\n\n" ls)
        return CreateSessionUx.formatCreateSessionReply sid targets hint
      | Result.Error err -> return SageFsError.describeForAgent err
    }

  /// List all active sessions with occupancy information.
  let listSessions (ctx: McpContext) : Task<string> =
    task {
      let! sessions = ctx.SessionOps.GetAllSessions()
      // Prune SessionMap against the live registry before reporting occupancy:
      // a stopped/purged session (by ANY path) must not keep its agents listed
      // as occupants, and stale agents must not claim live sessions forever.
      let liveIds = sessions |> List.map (fun s -> WorkerProtocol.SessionId.value s.Id) |> Set.ofList
      pruneSessionMap ctx (Some liveIds) None DateTime.UtcNow
      let occupancyMap =
        sessions
        |> List.map (fun s ->
          let sid = WorkerProtocol.SessionId.value s.Id
          sid, occupantsForSession ctx sid)
        |> Map.ofList
      return SessionOperations.formatSessionList System.DateTime.UtcNow (Some occupancyMap) sessions
    }

  /// Stop a session by ID.
  let stopSession (ctx: McpContext) (sessionId: string) : Task<string> =
    task {
      let! result = ctx.SessionOps.StopSession sessionId
      ctx.Dispatch |> Option.iter (fun d -> d (SageFsMsg.Editor EditorAction.ListSessions))
      match result with
      | Result.Ok msg ->
        // The session is gone from the registry — release every agent that
        // was routed to it so it no longer claims occupancy.
        evictSessionEntries ctx sessionId
        releaseSessionOwner sessionId
        return msg
      | Result.Error err -> return SageFsError.describeForAgent err
    }

  let stopSessionOwned (ctx: McpContext) (sessionId: string) : Task<string> =
    task {
      match ownsMcpSession sessionId with
      | Some false ->
        return SageFsError.describeForAgent (
          SageFsError.SessionStopFailed (sessionId, "the session is owned by another bound MCP connection; use the connection that created it or stop it through a privileged daemon control"))
      | Some true
      | None -> return! stopSession ctx sessionId
    }

  /// Switch the active session for a specific agent. Validates the target exists.
  let switchSession (ctx: McpContext) (agent: string) (sessionId: string) : Task<string> =
    task {
      match WorkerProtocol.SessionId.validate sessionId with
      | Error e -> return sprintf "Error: invalid session ID: %s" e
      | Ok validId ->
        let! info = ctx.SessionOps.GetSessionInfo validId
        match info with
        | Some sessionInfo ->
          let _prev = activeSessionId ctx agent
          setActiveSessionId ctx agent sessionId
          // Also move the daemon-global active session, so session-less calls
          // (e.g. GET /api/live-testing/status with no ?session=) follow the
          // switch instead of staying on whatever session was last created.
          // Mirrors the create path's SessionSwitched dispatch; the handler
          // also parks/promotes the per-session live-testing state.
          match ctx.Dispatch with
          | Some dispatch -> dispatch (SageFsMsg.Event (TuiEvent.SessionSwitched(None, sessionId)))
          | None -> ()
          // issue #140: the switch itself is unconditional and durable now
          // (resolveSessionId no longer discards it while the target is
          // warming up) — but say so honestly rather than implying the
          // session is immediately usable. Silently doing nothing was the
          // old bug; claiming readiness would be a new one.
          let warmupNote =
            match sessionInfo.Status with
            | WorkerProtocol.SessionLifecycleStatus.Starting _
            | WorkerProtocol.SessionLifecycleStatus.Restarting _ ->
              sprintf " — still warming up (%s). The switch holds: poll get_session_status until it reports Ready before send_fsharp_code." (WorkerProtocol.SessionLifecycleStatus.label sessionInfo.Status)
            | _ -> ""
          return sprintf "Switched to session '%s'%s" sessionId warmupNote
        | None ->
          return sprintf "Error: Session '%s' not found" sessionId
    }

  // ── Workflow Switching ──────────────────────────────────────────

  /// Switch the workflow mode of a session (Interactive ↔ HotReload).
  /// Creates a new session with the target workflow and stops the old one.
  let switchWorkflow
    (ctx: McpContext)
    (agent: string)
    (workingDirectory: string option)
    (targetStr: string)
    (dryRun: bool)
    : Task<string> =
    task {
      // 1. Parse target workflow — one alias table (WorkflowTypes.tryOfString),
      // shared with ofString, so create and switch never disagree on spellings.
      let targetOpt = WorkflowTypes.SessionWorkflow.tryOfString targetStr
      match targetOpt with
      | None ->
        return CreateSessionUx.formatUnknownWorkflowError targetStr
      | Some target ->
      // 2. Resolve session from working directory
      let! resolution = resolveSessionId ctx agent None workingDirectory
      match resolution with
      | WarmingUp _ | Unroutable _ | FaultedSession _ | Gone _ as other ->
        return sprintf "Error: %s" (formatSessionResolution other)
      | Routable sid ->
      // 3. Get session info for current workflow
      let validId = toSessionId sid
      let! info = ctx.SessionOps.GetSessionInfo validId
      match info with
      | None -> return sprintf "Error: session '%s' not found" sid
      | Some sessionInfo ->
      let current = sessionInfo.Workflow
      let cost = WorkflowTypes.TransitionCost.compute 0 0
      let prevLabel = WorkflowTypes.SessionWorkflow.label current
      let targetLabel = WorkflowTypes.SessionWorkflow.label target
      let serializeOutcome outcome =
        match outcome with
        | WorkflowTypes.WorkflowSwitchOutcome.AlreadyActive (c, msg) ->
          Json.serialize (Json.indented Json.standard)
            {| Outcome = "alreadyActive"
               PreviousWorkflow = prevLabel
               TargetWorkflow = targetLabel
               Cost = c; Switched = false
               NewSessionId = (None: string option)
               Message = msg |}
        | WorkflowTypes.WorkflowSwitchOutcome.DryRunPreview (c, msg) ->
          Json.serialize (Json.indented Json.standard)
            {| Outcome = "dryRunPreview"
               PreviousWorkflow = prevLabel
               TargetWorkflow = targetLabel
               Cost = c; Switched = false
               NewSessionId = (None: string option)
               Message = msg |}
        | WorkflowTypes.WorkflowSwitchOutcome.Executed (_, _, c, sid, msg) ->
          Json.serialize (Json.indented Json.standard)
            {| Outcome = "executed"
               PreviousWorkflow = prevLabel
               TargetWorkflow = targetLabel
               Cost = c; Switched = true
               NewSessionId = Some sid
               Message = msg |}
      // 4. If same workflow kind, no-op
      match WorkflowTypes.SessionWorkflow.label current = WorkflowTypes.SessionWorkflow.label target with
      | true ->
        let outcome = WorkflowTypes.WorkflowSwitchOutcome.alreadyInWorkflow current cost
        return serializeOutcome outcome
      | false ->
      // 5. If dry run, return preview only
      match dryRun with
      | true ->
        let outcome = WorkflowTypes.WorkflowSwitchOutcome.preview current target cost
        return serializeOutcome outcome
      | false ->
      // 6. Execute through the daemon's own switch: it restarts THIS session id spawn-first into
      // the target workflow. Creating a second session here hit the duplicate-session guard.
      let! switchResult = ctx.SessionOps.SwitchWorkflow sid target
      ctx.Dispatch |> Option.iter (fun d -> d (SageFsMsg.Editor EditorAction.ListSessions))
      match switchResult with
      | Result.Error err -> return sprintf "Error switching workflow: %s" (SageFsError.describeForAgent err)
      | Result.Ok _ -> return serializeOutcome (WorkflowTypes.WorkflowSwitchOutcome.switched current target cost sid)
    }

  // ── Elm State Query ──────────────────────────────────────────────

  let formatRegionFlags (flags: RegionFlags) =
    [ if flags.HasFlag RegionFlags.Focusable then "focusable"
      if flags.HasFlag RegionFlags.Scrollable then "scrollable"
      if flags.HasFlag RegionFlags.LiveUpdate then "live"
      if flags.HasFlag RegionFlags.Clickable then "clickable"
      if flags.HasFlag RegionFlags.Collapsible then "collapsible" ]
    |> String.concat ", "

  /// Get current Elm render regions (daemon mode only).
  let getElmState (ctx: McpContext) : Task<string> =
    task {
      match ctx.GetElmRegions with
      | None ->
        return "Elm state not available — Elm loop not started."
      | Some getRegions ->
        let regions = getRegions ()
        match regions.IsEmpty with
        | true ->
          return "No render regions available."
        | false ->
          return
            regions
            |> List.map (fun r ->
              let header =
                sprintf "── %s [%s] ──" r.Id (formatRegionFlags r.Flags)
              match String.IsNullOrWhiteSpace r.Content with
              | true -> header
              | false -> sprintf "%s\n%s" header r.Content)
            |> String.concat "\n\n"
    }

  // ── Live Testing MCP Tools ──────────────────────────────────

  let rec getLiveTestStatus (ctx: McpContext) (agentName: string) (fileFilter: string option) : Task<string> =
    getLiveTestStatusForSession ctx agentName fileFilter None

  /// Like `getLiveTestStatus`, but `targetSession` (when given) reads that
  /// session's OWN live-testing cycle via `SageFsModel.cycleForSession`
  /// (Primary when it's the active session, its own `PerSessionLiveTesting`
  /// entry otherwise) instead of always resolving "the" active session —
  /// the read-side half of the roast UX-6 keystone: a background session's
  /// warmup-discovered tests are observable without ever switching to it.
  and getLiveTestStatusForSession
    (ctx: McpContext)
    (agentName: string)
    (fileFilter: string option)
    (targetSession: string option)
    : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "Live testing not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        // Prefer an explicit target session; otherwise the per-client session
        // from SessionMap; otherwise fall back to the global active session.
        // This prevents session A's tests from bleeding into session B's view
        // when the daemon-global active session differs from the calling
        // client's current session (or from the explicitly requested one).
        let activeId =
          match targetSession with
          | Some sid when sid <> "" -> sid
          | _ ->
            let perClient = activeSessionId ctx agentName
            match perClient <> "" with
            | true -> perClient
            | false ->
              ActiveSession.sessionId model.Sessions.ActiveSessionId
              |> Option.map WorkerProtocol.SessionId.value
              |> Option.defaultValue ""
        return LiveTestStatusView.render activeId (SageFsModel.cycleForSession activeId model) fileFilter
    }

  let rec setLiveTesting (ctx: McpContext) (enabled: bool) : Task<string> =
    setLiveTestingForSession ctx enabled None

  /// Like `setLiveTesting`, but `targetSession` (when given) activates
  /// exactly THAT session's own cycle (`EnableLiveTestingForSession`/
  /// `DisableLiveTestingForSession` — auto-vivifying a `PerSessionLiveTesting`
  /// entry when it's not Primary) instead of always the Primary/active
  /// session — the write-side half of the roast UX-6 keystone: a background
  /// session can be enabled without ever switching to it first.
  and setLiveTestingForSession (ctx: McpContext) (enabled: bool) (targetSession: string option) : Task<string> =
    task {
      match ctx.Dispatch with
      | None -> return "Cannot set live testing — Elm loop not started."
      | Some dispatch ->
        let msg =
          match enabled, targetSession with
          | true, Some sid when sid <> "" -> SageFsMsg.EnableLiveTestingForSession sid
          | false, Some sid when sid <> "" -> SageFsMsg.DisableLiveTestingForSession sid
          | true, _ -> SageFsMsg.EnableLiveTesting
          | false, _ -> SageFsMsg.DisableLiveTesting
        dispatch msg
        match enabled with
        | false ->
          return "Live testing disabled."
        | true ->
          match ctx.GetElmModel with
          | Some getModel ->
            let model = getModel ()
            let activeId =
              match targetSession with
              | Some sid when sid <> "" -> sid
              | _ ->
                ActiveSession.sessionId model.Sessions.ActiveSessionId
                |> Option.map WorkerProtocol.SessionId.value
                |> Option.defaultValue ""
            let state = (SageFsModel.cycleForSession activeId model).TestState
            let discovered = state.DiscoveredTests.Length
            match discovered > 0 with
            | true ->
              return sprintf "Live testing enabled. %d tests already discovered. Use get_live_test_status to confirm current DiscoveryState." discovered
            | false ->
              return "Live testing enabled. Initial discovery now runs asynchronously; use get_live_test_status to confirm DiscoveryState."
          | None ->
            return "Live testing enabled. Initial discovery now runs asynchronously; use get_live_test_status to confirm DiscoveryState."
    }

  let setRunPolicy (ctx: McpContext) (category: string) (policy: string) : Task<string> =
    let cat =
      match category.ToLowerInvariant() with
      | "unit" -> Some Features.LiveTesting.TestCategory.Unit
      | "integration" -> Some Features.LiveTesting.TestCategory.Integration
      | "browser" -> Some Features.LiveTesting.TestCategory.Browser
      | "benchmark" -> Some Features.LiveTesting.TestCategory.Benchmark
      | "architecture" -> Some Features.LiveTesting.TestCategory.Architecture
      | "property" -> Some Features.LiveTesting.TestCategory.Property
      | other -> Some (Features.LiveTesting.TestCategory.Custom other)
    let pol =
      match policy.ToLowerInvariant() with
      | "oneverychange" | "every" -> Some Features.LiveTesting.RunPolicy.OnEveryChange
      | "onsaveonly" | "save" -> Some Features.LiveTesting.RunPolicy.OnSaveOnly
      | "ondemand" | "demand" -> Some Features.LiveTesting.RunPolicy.OnDemand
      | "disabled" | "off" -> Some Features.LiveTesting.RunPolicy.Disabled
      | _ -> None
    task {
      match ctx.Dispatch with
      | None -> return "Cannot set policy — Elm loop not started."
      | Some dispatch ->
        match cat, pol with
        | Some c, Some p ->
          dispatch (SageFsMsg.Event (TuiEvent.RunPolicyChanged (c, p)))
          return sprintf "Set %s policy to %A." category p
        | None, _ -> return sprintf "Unknown category: %s. Valid: unit, integration, browser, benchmark, architecture, property." category
        | _, None -> return sprintf "Unknown policy: %s. Valid: every, save, demand, disabled." policy
    }

  let markAllTestsStale (ctx: McpContext) : Task<string> =
    task {
      match ctx.Dispatch with
      | None -> return "Cannot mark tests stale — Elm loop not started."
      | Some dispatch ->
        dispatch SageFsMsg.MarkAllTestsStale
        match ctx.GetElmModel with
        | Some getModel ->
          let count = (getModel ()).LiveTesting.TestState.DiscoveredTests.Length
          return sprintf "All %d test results marked stale." count
        | None ->
          return "All test results marked stale."
    }

  let setTestTimeouts(_ctx: McpContext) (perTestSeconds: float option) (globalRunSeconds: float option) : Task<string> =
    task {
      let mutable error = None
      let parts = System.Collections.Generic.List<string>()
      match perTestSeconds with
      | Some s when s > 0.0 ->
        Timeouts.setPerTestTimeout (TimeSpan.FromSeconds s)
        parts.Add (sprintf "Per-test timeout: %.1fs" s)
      | Some s -> error <- Some (sprintf "Invalid per-test timeout: %.1f (must be > 0)" s)
      | None -> ()
      match globalRunSeconds with
      | Some s when s > 0.0 ->
        Timeouts.setGlobalTestRunTimeout (TimeSpan.FromSeconds s)
        parts.Add (sprintf "Global run timeout: %.1fs" s)
      | Some s -> error <- Some (sprintf "Invalid global run timeout: %.1f (must be > 0)" s)
      | None -> ()
      match error with
      | Some e -> return e
      | None ->
        match parts.Count with
        | 0 ->
          return sprintf "Current timeouts — per-test: %.1fs, global run: %.1fs. Provide per_test_seconds and/or global_run_seconds to change."
            (Timeouts.perTestDefault().TotalSeconds) (Timeouts.globalTestRun().TotalSeconds)
        | _ ->
          parts.Add (sprintf "(effective immediately for next test run)")
          return parts |> Seq.toList |> String.concat ". "
    }

  let getTestTrace (ctx: McpContext) : Task<string> =
    match ctx.GetElmModel with
    | None -> Task.FromResult "Test trace not available — Elm loop not started."
    | Some getModel ->
      let model = getModel ()
      let state = model.LiveTesting.TestState
      let activeId =
        ActiveSession.sessionId model.Sessions.ActiveSessionId
        |> Option.map WorkerProtocol.SessionId.value
        |> Option.defaultValue ""
      let sessionEntries =
        Features.LiveTesting.LiveTestState.statusEntriesForSession activeId state
      let summary =
        Features.LiveTesting.TestSummary.fromStatuses
          state.Activation (sessionEntries |> Array.map (fun e -> e.Status))
      let timing = model.LiveTesting.LastTiming
      let isActive = state.Activation = Features.LiveTesting.LiveTestingActivation.Active
      let discoveryState = Features.LiveTesting.LiveTestState.discoveryState state
      let discoveryRequiresEval = Features.LiveTesting.LiveTestState.requiresPrimingEval state
      let lastDecision =
        state.LastDecision
        |> Option.map Features.LiveTesting.LiveTestingDecision.toWireModel
      let resp = {|
        Enabled = isActive
        IsRunning = Features.LiveTesting.TestRunPhase.isAnyRunning state.RunPhases
        History = state.History
        Summary = summary
        DiscoveryState = Features.LiveTesting.LiveTestDiscoveryState.toWireValue discoveryState
        DiscoveryHint = Features.LiveTesting.LiveTestState.discoveryHint state
        DiscoveryRequiresEval = discoveryRequiresEval
        LastDiscoveryTime =
          match state.LastDiscoveryTime > System.DateTimeOffset.MinValue with
          | true -> Some state.LastDiscoveryTime
          | false -> None
        LastDecision = lastDecision
        Timing = timing |> Option.map Features.LiveTesting.TestCycleTiming.toStatusBar |> Option.defaultValue "no timing yet"
        Providers = state.DetectedProviders |> List.map (fun p ->
          match p with
          | Features.LiveTesting.ProviderDescription.AttributeBased a -> SageFs.TestFramework.toString a.Name
          | Features.LiveTesting.ProviderDescription.Custom c -> SageFs.TestFramework.toString c.Name)
        Policies = state.RunPolicies |> Map.toList |> List.map (fun (c, p) -> sprintf "%A: %A" c p)
        Hint = match isActive with
               | true -> None
               | false -> Some "Live testing is not active. Call enable_live_testing to start test discovery and automatic re-runs."
      |}
      Task.FromResult (Json.serialize Json.standard resp)

  let explainTestRun (ctx: McpContext) (testName: string) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "Explain not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let graph = model.LiveTesting.DepGraph
        let testState = model.LiveTesting.TestState
        let trigger = model.LiveTesting.LastTrigger
        let changedSymbols = model.LiveTesting.ChangedSymbols
        let matchingTests =
          testState.DiscoveredTests
          |> Array.filter (fun tc ->
            tc.FullName.Contains(testName, StringComparison.OrdinalIgnoreCase)
            || tc.DisplayName.Contains(testName, StringComparison.OrdinalIgnoreCase))
        match matchingTests with
        | [||] -> return sprintf "No test found matching '%s'. Use get_live_test_status to list tests." testName
        | tests ->
          let explanations =
            tests
            |> Array.map (Features.LiveTesting.TestRunExplainer.explainTest
              graph testState.LastResults testState.FlakyHistory changedSymbols trigger)
          let resp = {|
            MatchCount = explanations.Length
            Explanations = explanations |> Array.map (fun e ->
              let reasonStr =
                match e.Reason with
                | Features.LiveTesting.TestTriggerReason.SymbolCoverage syms ->
                  sprintf "Symbol coverage: %s" (String.concat ", " syms)
                | Features.LiveTesting.TestTriggerReason.NewTest -> "New test (no prior results)"
                | Features.LiveTesting.TestTriggerReason.ExplicitRun -> "Explicitly triggered"
                | Features.LiveTesting.TestTriggerReason.UnknownCoverage -> "Unknown coverage (dep graph fallback)"
              {| TestId = Features.LiveTesting.TestId.value e.TestId
                 DisplayName = e.DisplayName
                 Reason = reasonStr
                 CoveringSymbols = e.CoveringSymbols
                 Trigger = sprintf "%A" e.Trigger
                 DurationMs = e.DurationMs
                 FlakyClassification =
                   match e.FlakyClassification with
                   | Features.LiveTesting.FlakyClassification.Insufficient -> "insufficient"
                   | Features.LiveTesting.FlakyClassification.Stable -> "stable"
                   | Features.LiveTesting.FlakyClassification.Environmental n -> sprintf "environmental(%d flips)" n
                   | Features.LiveTesting.FlakyClassification.PropertyCounterexample ce -> sprintf "property-counterexample: %s" ce
                 IsFlaky =
                   match e.FlakyClassification with
                   | Features.LiveTesting.FlakyClassification.Environmental _ -> true
                   | Features.LiveTesting.FlakyClassification.PropertyCounterexample _ -> true
                   | _ -> false |})
            ChangedSymbols = changedSymbols
          |}
          return Json.serialize Json.standard resp
    }

  let queryTestCoverage (ctx: McpContext) (symbol: string) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "Coverage query not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let graph = model.LiveTesting.DepGraph
        let testState = model.LiveTesting.TestState
        let coveringTests =
          Features.LiveTesting.TestRunExplainer.queryTestCoverage
            graph testState.DiscoveredTests testState.LastResults symbol
        let resp = {|
          Symbol = symbol
          CoveringTestCount = coveringTests.Length
          Tests = coveringTests |> Array.map (fun ct ->
            let resultStr =
              match ct.Result with
              | Some (Features.LiveTesting.TestResult.Passed d) -> sprintf "Passed (%.0fms)" d.TotalMilliseconds
              | Some (Features.LiveTesting.TestResult.Failed (_, d)) -> sprintf "Failed (%.0fms)" d.TotalMilliseconds
              | Some (Features.LiveTesting.TestResult.Skipped r) -> sprintf "Skipped: %s" r
              | Some Features.LiveTesting.TestResult.NotRun -> "Not run"
              | Some (Features.LiveTesting.TestResult.NoResult reason) ->
                sprintf "Never reported: %s" (Features.LiveTesting.NoResultReason.describe reason)
              | None -> "No result"
            {| TestId = Features.LiveTesting.TestId.value ct.TestId
               DisplayName = ct.DisplayName
               LastResult = resultStr |})
        |}
        return Json.serialize Json.standard resp
    }

  /// Format file-level coverage annotations as JSON for the get_file_coverage MCP tool.
  /// Pure function: takes FileAnnotations + LiveTestState, returns JSON string.
  let formatFileCoverageResponse (annotations: Features.LiveTesting.FileAnnotations) (testState: Features.LiveTesting.LiveTestState) : string =
    let testNameFor (tid: Features.LiveTesting.TestId) =
      testState.DiscoveredTests
      |> Array.tryFind (fun dt -> dt.Id = tid)
      |> Option.map (fun dt -> dt.DisplayName)
      |> Option.defaultValue (Features.LiveTesting.TestId.value tid)
    let lines =
      annotations.CoverageAnnotations
      |> Array.map (fun ca ->
        let covered, testCount, health =
          match ca.Detail with
          | Features.LiveTesting.CoverageStatus.Covered (cnt, h) ->
            true, cnt,
            (match h with
             | Features.LiveTesting.CoverageHealth.AllPassing -> "AllPassing"
             | Features.LiveTesting.CoverageHealth.SomeFailing -> "SomeFailing")
          | Features.LiveTesting.CoverageStatus.NotCovered -> false, 0, "NotCovered"
          | Features.LiveTesting.CoverageStatus.Pending -> false, 0, "Pending"
        let branchObj : obj =
          match ca.BranchCoverage with
          | Some Features.LiveTesting.LineCoverage.FullyCovered ->
            {| Case = "FullyCovered" |} :> obj
          | Some (Features.LiveTesting.LineCoverage.PartiallyCovered (c, t)) ->
            {| Case = "PartiallyCovered"; Covered = c; Total = t |} :> obj
          | Some Features.LiveTesting.LineCoverage.NotCovered ->
            {| Case = "NotCovered" |} :> obj
          | None ->
            {| Case = "Unknown" |} :> obj
        let coveringTests = ca.CoveringTestIds |> Array.map testNameFor
        {| Line = ca.Line; EndLine = ca.EndLine; EndColumn = ca.EndColumn
           Covered = covered; TestCount = testCount; Health = health
           CoveringTests = coveringTests; BranchCoverage = branchObj |})
    let coveredCount = lines |> Array.filter (fun l -> l.Covered) |> Array.length
    let totalCount = lines.Length
    let pct =
      match totalCount with
      | 0 -> 0.0
      | n -> System.Math.Round(float coveredCount / float n * 100.0, 1)
    let resp = {|
      FilePath = annotations.FilePath
      Lines = lines
      Summary = {|
        CoveredLines = coveredCount
        TotalLines = totalCount
        CoveragePercent = pct
      |}
    |}
    Json.serialize Json.standard resp

  /// MCP tool: get per-line coverage data for a specific file.
  /// Resolves partial file paths, then computes line-level coverage from
  /// instrumentation bitmaps + dep graph fallback.
  let getFileCoverage (ctx: McpContext) (filePath: string) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "File coverage not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let cycleState = model.LiveTesting
        let testState = cycleState.TestState
        let entries =
          Features.LiveTesting.LiveTestState.statusEntriesForSession "" testState
        let resolvedPath =
          Features.LiveTesting.FileAnnotations.resolveFilePath
            filePath entries cycleState.InstrumentationMaps
        match resolvedPath with
        | None ->
          let resp = {| FilePath = filePath; Error = "File not found in test sources or instrumentation maps" |}
          return Json.serialize Json.standard resp
        | Some fullPath ->
          let annotations = Features.LiveTesting.FileAnnotations.projectWithCoverage fullPath cycleState
          return formatFileCoverageResponse annotations testState
    }

  /// The dependency graph of the current FeaturePushState — materialized once
  /// per history version from the indexed eval store and shared by all readers.
  let private buildCellGraphFromState (state: Features.FeatureHooks.FeaturePushState) : Features.CellDependencyGraph.CellGraph =
    Features.FeatureHooks.cellGraph state

  /// Convert BindingScopeSnapshot active bindings to Ghostwriter ScopeBinding list.
  let private toScopeBindings (snapshot: Features.BindingExplorer.BindingScopeSnapshot) : Features.ScopeBinding list =
    snapshot.ActiveBindings
    |> Map.toList
    |> List.map (fun (_key, info) ->
      { Features.ScopeBinding.Name = info.Name
        TypeSig = info.TypeSig
        Value = info.Value })

  /// Convert a CoverageVerdict to its JSON string representation.
  let private verdictString (v: Features.CoverageIntel.CoverageVerdict) =
    match v with
    | Features.CoverageIntel.WellCovered -> "WellCovered"
    | Features.CoverageIntel.PartialBlindSpot -> "PartialBlindSpot"
    | Features.CoverageIntel.DiagnosticBlindSpot -> "DiagnosticBlindSpot"

  /// Convert a CoverageIntelReport to a JSON-serializable anonymous record.
  let toCoverageIntelJson (report: Features.CoverageIntel.CoverageIntelReport) =
    {| CoveragePercent = report.CoveragePercent
       CoveredBranches = report.CoveredBranches
       TotalBranches = report.TotalBranches
       Verdict = verdictString report.Verdict
       BlindSpots =
         report.BlindSpots |> List.map (fun g ->
           {| FilePath = g.FilePath
              Line = g.Line
              EndLine = g.EndLine
              BranchId = g.BranchId
              NearestCoveredLine = g.NearestCoveredLine |})
       CorrelatedFailures =
         report.CorrelatedFailures |> List.map Features.LiveTesting.TestId.value
       Summary = Features.CoverageIntel.CoverageIntel.summarize report |}

  let explainTestFailure (ctx: McpContext) (testName: string) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "Failure narrative not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let testState = model.LiveTesting.TestState
        let allMaps =
          model.LiveTesting.InstrumentationMaps
          |> Map.values
          |> Seq.collect id
          |> Array.ofSeq
        let bitmaps = testState.TestCoverageBitmaps
        let depGraph = model.LiveTesting.DepGraph
        let hasMaps = allMaps.Length > 0
        let matchingTests =
          testState.DiscoveredTests
          |> Array.filter (fun tc ->
            tc.FullName.Contains(testName, StringComparison.OrdinalIgnoreCase)
            || tc.DisplayName.Contains(testName, StringComparison.OrdinalIgnoreCase))
        match matchingTests with
        | [||] -> return sprintf "No test found matching '%s'." testName
        | tests ->
          let narratives =
            tests
            |> Array.choose (fun tc ->
              Map.tryFind tc.Id testState.Cached.FailureNarratives
              |> Option.map (fun (n: Features.LiveTesting.FailureNarrative) ->
                let changes =
                  n.CausalChanges |> List.map (fun c ->
                    match c with
                    | Features.LiveTesting.CausalChange.SymbolChanged s -> {| Kind = "symbol"; Name = s |}
                    | Features.LiveTesting.CausalChange.FileChanged f -> {| Kind = "file"; Name = f |}
                    | Features.LiveTesting.CausalChange.Unknown -> {| Kind = "unknown"; Name = "" |})
                let propViolation =
                  n.PropertyViolation |> Option.map (fun pv ->
                    {| PropertyName = pv.PropertyName
                       ShrunkCounterexample = pv.ShrunkCounterexample
                       AlgebraicCategory = pv.AlgebraicCategory |})
                let coverageIntel =
                  match hasMaps with
                  | false -> None
                  | true ->
                    let causalFiles =
                      n.CausalChanges
                      |> List.choose (fun c ->
                        match c with
                        | Features.LiveTesting.CausalChange.FileChanged f -> Some f
                        | _ -> None)
                    let report =
                      Features.CoverageIntel.CoverageIntel.composeForFailure
                        tc.Id tc.DisplayName n causalFiles allMaps bitmaps depGraph
                    Some (toCoverageIntelJson report)
                {| TestId = Features.LiveTesting.TestId.value tc.Id
                   DisplayName = tc.DisplayName
                   Summary = n.Summary
                   LastPassedAt = n.LastPassedAt
                   TimeSinceLastPass = n.TimeSinceLastPass |> Option.map (fun ts -> ts.TotalSeconds)
                   CausalChanges = changes
                   PropertyViolation = propViolation
                   CoverageIntel = coverageIntel |}))
          match narratives with
          | [||] ->
            let failingCount =
              tests |> Array.filter (fun tc ->
                match Map.tryFind tc.Id testState.LastResults with
                | Some r ->
                  match r.Result with
                  | Features.LiveTesting.TestResult.Failed _ -> true
                  | _ -> false
                | None -> false) |> Array.length
            match failingCount with
            | 0 -> return sprintf "Test(s) matching '%s' are not currently failing — no narrative available." testName
            | _ -> return sprintf "Test(s) matching '%s' are failing but no narrative was computed (may not have transitioned from passing)." testName
          | narrs ->
            // Enrich with diagnostic report if feature state is available
            let diagnostics =
              match ctx.GetFeatureState with
              | Some getState ->
                let state = getState ()
                let graph = buildCellGraphFromState state
                let failuresForDiag =
                  tests
                  |> Array.choose (fun tc ->
                    Map.tryFind tc.Id testState.Cached.FailureNarratives
                    |> Option.map (fun n -> (tc.Id, tc.DisplayName, n)))
                  |> Array.toList
                let scopeBindings =
                  toScopeBindings (Features.FeatureHooks.scope state)
                let report =
                  Features.Diagnostician.Diagnostician.compose
                    graph failuresForDiag scopeBindings state.CachedTimeline
                Some {| Severity = report.Severity.ToString()
                        AffectedCells = report.AffectedCells
                        SuggestionCount = report.SuggestedFixes.Length
                        TopSuggestions =
                          report.SuggestedFixes
                          |> List.truncate 3
                          |> List.map (fun s -> {| Code = s.Code; Explanation = s.Explanation |})
                        Performance =
                          report.PerformanceContext
                          |> Option.map (fun s -> {| Sparkline = s.Sparkline; P50Ms = s.P50Ms; P95Ms = s.P95Ms |})
                        Summary = report.Summary |}
              | None -> None
            let resp = {| MatchCount = narrs.Length; Narratives = narrs; Diagnostics = diagnostics |}
            return Json.serialize Json.standard resp
    }



  /// What a session's trustworthiness is judged from: its status, whether its loaded
  /// files are stale, and any type-identity diagnostic. Shared by `targeted_verify`
  /// and `run_tests` so both judge trust from one place. `artifact` names what was
  /// loaded when the session reports no file list.
  let sessionTrustObservation (ctx: McpContext) (sid: string) (artifact: string) =
    task {
      let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
      let status = info |> Option.map (fun session -> session.Status)
      let loadedState,
          sessionLoadedState =
        match ctx.GetElmModel |> Option.map (fun getModel -> (getModel ()).SessionContext) |> Option.flatten with
        | Some sessionCtx ->
          let statuses =
            match sessionCtx.SessionId = sid with
            | true -> sessionCtx.FileStatuses
            | false -> []
          match statuses |> List.tryFind (fun file -> file.Readiness = FileReadiness.Stale) with
          | Some stale ->
            let lastLoaded = stale.LastLoadedAt |> Option.map string |> Option.defaultValue "unknown-loaded-version"
            let state = Features.Verification.LoadedDefinitionState.ConfirmedStale (stale.Path, lastLoaded)
            state, Some state
          | None ->
            let loaded =
              statuses
              |> List.filter (fun file -> file.Readiness = FileReadiness.Loaded)
              |> List.map (fun file -> file.Path)
              |> function
                 | [] -> artifact
                 | files -> String.concat ", " files
            let state = Features.Verification.LoadedDefinitionState.ConfirmedCurrent loaded
            state, Some state
        | None ->
          let state = Features.Verification.LoadedDefinitionState.UnknownLoadState "warmup file status unavailable"
          state, None
      let observation : Features.Verification.SessionTrust.SessionObservation =
        { MatchingSessionIds = [ sid ]
          SessionStatus = status
          LoadedState = sessionLoadedState
          TypeIdentityDiagnostic =
            match typeIdentityDiagnostics.TryGetValue(sid) with
            | true, diag -> Some diag
            | _ -> None }
      return loadedState, observation
    }

  let targetedVerify
    (ctx: McpContext)
    (agent: string)
    (workingDirectory: string option)
    (behavior: string)
    (exactGuard: string option)
    : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let! loadedState, sessionObservation = sessionTrustObservation ctx sid behavior
      let exactGuardRef =
        exactGuard
        |> Option.bind (fun raw ->
          match Features.Verification.ExactTestRef.create raw with
          | Ok exact -> Some exact
          | Error _ -> None)
      let request : Features.Verification.TargetedVerificationRequest =
        { Intent =
            Features.Verification.VerificationIntent.VerifyChangedBehavior (behavior, Features.Verification.RegressionRisk.SharedContract)
          NamedGuard = exactGuardRef
          SessionObservation = sessionObservation
          LoadedState = loadedState }
      let report =
        Features.Verification.TargetedVerification.createReport
          request
          None
          None
      return Features.Verification.TargetedVerification.summarize report
    })

  /// Same computation as `targetedVerify`, returning the IDENTICAL display
  /// text, but also reporting whether the loaded definition was confirmed
  /// stale as a real `SageFsError` — friction telemetry classifies
  /// `LoadedStateStale` from that typed value directly instead of grepping
  /// the summarized report for the word "stale". Duplicated rather than
  /// factored through `targetedVerify` so the latter's tested text (asserted
  /// verbatim by TargetedVerifyMcpToolTests.fs) is never at risk of drifting.
  let targetedVerifyResult
    (ctx: McpContext)
    (agent: string)
    (workingDirectory: string option)
    (behavior: string)
    (exactGuard: string option)
    : Task<string * SageFsError option> =
    task {
      let! resolution = resolveSessionId ctx agent None workingDirectory
      match resolution with
      | Routable sid ->
        let! loadedState, sessionObservation = sessionTrustObservation ctx sid behavior
        let exactGuardRef =
          exactGuard
          |> Option.bind (fun raw ->
            match Features.Verification.ExactTestRef.create raw with
            | Ok exact -> Some exact
            | Error _ -> None)
        let request : Features.Verification.TargetedVerificationRequest =
          { Intent =
              Features.Verification.VerificationIntent.VerifyChangedBehavior (behavior, Features.Verification.RegressionRisk.SharedContract)
            NamedGuard = exactGuardRef
            SessionObservation = sessionObservation
            LoadedState = loadedState }
        let report =
          Features.Verification.TargetedVerification.createReport
            request
            None
            None
        let summary = Features.Verification.TargetedVerification.summarize report
        let blocker =
          match loadedState with
          | Features.Verification.LoadedDefinitionState.ConfirmedStale (path, lastLoaded) ->
            Some (SageFsError.HotReloadStateError (sid, sprintf "loaded definition of '%s' is stale (last loaded %s)" path lastLoaded))
          | _ -> None
        return summary, blocker
      | other ->
        let msg = formatSessionResolution other
        let! blocker = sessionRoutingError ctx None workingDirectory other
        return sprintf "Error: %s" msg, blocker
    }

  // ── Feature Analysis MCP Tools (P15–P19) ─────────────────────

  /// Convert EvalHistory to FilmstripEvent list.
  let private toFilmstripEvents (state: Features.FeatureHooks.FeaturePushState) : Features.FilmstripEvent list =
    state.EvalHistory
    |> List.rev
    |> List.map (fun e ->
      { Features.FilmstripEvent.Timestamp = e.Timestamp
        Label = e.Code |> Features.FsiOutputParser.detectBoundaryKind |> Features.FsiOutputParser.EvalBoundaryKind.toLabel
        BindingCount = state.KnownBindings.Count
        TestSummary = None
        EvalDurationMs = Some (float e.DurationMs) })

  let decomposePipeline (code: string) : Task<string> =
    task {
      let stages = Features.EvalLens.decomposePipeline code
      match stages with
      | [] -> return "No pipeline stages found in the provided code."
      | stages ->
        let classifications =
          stages |> List.map (fun s -> s, Features.EvalLens.classifyStage s.Code)
        return
          classifications
          |> List.map (fun (stage, classification) ->
            let icon =
              match classification with
              | Features.Pure -> "●"
              | Features.Effectful -> "⚡"
              | Features.Unknown -> "?"
            sprintf "  %d. %s %s" stage.StageIndex icon (stage.Code.Trim()))
          |> fun lines ->
            sprintf "Pipeline decomposition (%d stages):\n%s" stages.Length (String.concat "\n" lines)
    }

  let planRipple (ctx: McpContext) (changedCellIds: string) : Task<string> =
    task {
      match featureStateForCaller ctx with
      | None -> return "Feature state not available — no active session."
      | Some state ->
        match state.EvalHistory with
        | [] -> return "No eval history — evaluate some cells first."
        | _ ->
          let graph = buildCellGraphFromState state
          let cellIds =
            changedCellIds.Split([| ','; ' ' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun s -> match System.Int32.TryParse(s) with | true, v -> Some v | _ -> None)
            |> Set.ofArray
          match cellIds.IsEmpty with
          | true -> return "No valid cell IDs provided. Use comma-separated integers (e.g., '0,2,5')."
          | false ->
            let plan = Features.EvalRipple.planRipple graph cellIds
            return
              plan.Steps
              |> List.map (fun step ->
                sprintf "  [%d] %s — %s"
                  step.CellId
                  (step.Code |> fun c -> match c.Length > 50 with | true -> c.[..47] + "..." | false -> c)
                  (match step.Status with
                   | Features.Pending -> "pending"
                   | Features.Evaluating -> "evaluating"
                   | Features.Succeeded o -> sprintf "ok: %s" o
                   | Features.Failed e -> sprintf "FAILED: %s" e
                   | Features.Skipped r -> sprintf "skipped: %s" r))
              |> fun lines ->
                sprintf "Ripple plan (%d steps, %d changed):\n%s"
                  plan.Steps.Length cellIds.Count (String.concat "\n" lines)
    }

  let previewWhatIf (ctx: McpContext) (bindingName: string) (newCode: string) : Task<string> =
    task {
      match featureStateForCaller ctx with
      | None -> return "Feature state not available — no active session."
      | Some state ->
        match state.EvalHistory with
        | [] -> return "No eval history — evaluate some cells first."
        | _ ->
          let graph = buildCellGraphFromState state
          let scope =
            Features.FeatureHooks.scope state
          let existingBinding = scope.ActiveBindings |> Map.tryFind bindingName
          let original =
            existingBinding
            |> Option.map (fun b -> b.Value |> Option.defaultValue "?")
            |> Option.defaultValue "?"
          let typeSig =
            existingBinding
            |> Option.map (fun b -> b.TypeSig)
            |> Option.defaultValue "obj"
          let override' = Features.WhatIf.createOverride bindingName original newCode typeSig
          let plan = Features.WhatIf.planWhatIf graph override'
          return
            [ sprintf "What-If: %s" (Features.WhatIf.formatOverride override')
              sprintf "Affected cells: %d" plan.AffectedCells.Length
              yield!
                plan.RippleSteps
                |> List.map (fun step ->
                  sprintf "  [%d] %s" step.CellId
                    (step.Code |> fun c -> match c.Length > 50 with | true -> c.[..47] + "..." | false -> c)) ]
            |> String.concat "\n"
    }

  let suggestNextCell (ctx: McpContext) : Task<string> =
    task {
      match featureStateForCaller ctx with
      | None -> return "Feature state not available — no active session."
      | Some state ->
        let scope =
          Features.FeatureHooks.scope state
        let bindings = toScopeBindings scope
        match bindings with
        | [] -> return "No bindings in scope — evaluate some cells first."
        | _ ->
          let suggestions = Features.Ghostwriter.suggest bindings
          match suggestions with
          | [] -> return "No suggestions available for the current bindings."
          | _ ->
            return
              suggestions
              |> List.map (fun s ->
                sprintf "  %.0f%% %s — %s" (s.Confidence * 100.0) s.Code s.Explanation)
              |> fun lines ->
                sprintf "Ghostwriter suggestions (%d):\n%s" suggestions.Length (String.concat "\n" lines)
    }

  let getSessionFilmstrip (ctx: McpContext) (filter: string option) : Task<string> =
    task {
      match featureStateForCaller ctx with
      | None -> return "Feature state not available — no active session."
      | Some state ->
        let events = toFilmstripEvents state
        match events with
        | [] -> return "No eval history — the session filmstrip is empty."
        | _ ->
          let frames = Features.SessionFilmstrip.buildFilmstrip events
          let filtered =
            match filter with
            | Some q when q <> "" -> Features.SessionFilmstrip.filterFrames q frames
            | _ -> frames
          let overview = Features.SessionFilmstrip.renderOverview filtered
          let cards =
            filtered
            |> List.map Features.SessionFilmstrip.renderFrame
          return
            [ overview; ""; yield! cards ]
            |> String.concat "\n"
    }

  // ── Phase 1b: Orphaned module MCP tools ──

  /// MCP Tool: Export session as notebook (.fsx with cell metadata)
  let exportNotebook (ctx: McpContext) (projectName: string option) : Task<string> =
    task {
      match featureStateForCaller ctx with
      | None -> return "Feature state not available — no active session."
      | Some state ->
        match state.EvalHistory with
        | [] -> return "No eval history — nothing to export."
        | history ->
          let cells =
            history
            |> List.rev
            |> List.mapi (fun i e ->
              { Features.NotebookExport.Metadata =
                  { Index = i; Label = None; Deps = []; Bindings = [] }
                Code = e.Code
                Output = Some e.Result } : Features.NotebookExport.NotebookCell)
          let header : Features.NotebookExport.NotebookHeader =
            { Project = projectName |> Option.defaultValue "SageFs Session"
              CellCount = cells.Length
              Timestamp = System.DateTimeOffset.UtcNow.ToString("o") }
          return Features.NotebookExport.exportNotebook header cells
    }

  /// MCP Tool: Export session as clean .fsx transcript
  let exportSessionTranscript (ctx: McpContext) (projectName: string option) : Task<string> =
    task {
      match featureStateForCaller ctx with
      | None -> return "Feature state not available — no active session."
      | Some state ->
        match state.EvalHistory with
        | [] -> return "No eval history — nothing to export."
        | _ ->
          let graph = buildCellGraphFromState state
          let entries = Features.SessionScribe.SessionScribe.fromGraph graph
          let name = projectName |> Option.defaultValue "SageFs Session"
          return Features.SessionScribe.SessionScribe.exportFsx name entries
    }

  /// MCP Tool: Get message journal (synthesized from eval history)
  let getMessageJournal (ctx: McpContext) (minLevel: string option) (source: string option) : Task<string> =
    task {
      match featureStateForCaller ctx with
      | None -> return "Feature state not available — no active session."
      | Some state ->
        match state.EvalHistory with
        | [] -> return "No eval history — journal is empty."
        | history ->
          let journal =
            history
            |> List.rev
            |> List.fold (fun j e ->
              Features.MessageJournal.Journal.record
                Features.MessageJournal.JournalLevel.Info "eval" e.Code j)
              (Features.MessageJournal.Journal.create (max 256 history.Length))
          let filtered =
            match minLevel with
            | Some lvl ->
              let level =
                match lvl.ToLowerInvariant() with
                | "debug" -> Features.MessageJournal.JournalLevel.Debug
                | "warn" | "warning" -> Features.MessageJournal.JournalLevel.Warn
                | "error" -> Features.MessageJournal.JournalLevel.Failure
                | _ -> Features.MessageJournal.JournalLevel.Info
              Features.MessageJournal.Journal.filterByMinLevel level journal
            | None -> Features.MessageJournal.Journal.entries journal
          let filtered =
            match source with
            | Some src when src <> "" ->
              filtered |> List.filter (fun e -> e.Source.Contains(src, System.StringComparison.OrdinalIgnoreCase))
            | _ -> filtered
          let stats = Features.MessageJournal.Journal.stats journal
          return
            [ sprintf "Journal: %d entries (%d info, %d warn, %d error)"
                stats.Total stats.InfoCount stats.WarnCount stats.ErrorCount
              ""
              yield!
                filtered
                |> List.map (fun e ->
                  sprintf "[%s] %s | %s: %s"
                    (e.Timestamp.ToString("HH:mm:ss"))
                    (match e.Level with
                     | Features.MessageJournal.JournalLevel.Debug -> "DBG"
                     | Features.MessageJournal.JournalLevel.Info -> "INF"
                     | Features.MessageJournal.JournalLevel.Warn -> "WRN"
                     | Features.MessageJournal.JournalLevel.Failure -> "ERR")
                    e.Source
                    (e.Message |> fun m -> match m.Length > 80 with | true -> m.[..77] + "..." | false -> m)) ]
            |> String.concat "\n"
    }

  /// MCP Tool: Get eval timeline with sparkline and percentiles
  let getEvalTimeline (ctx: McpContext) (sparklineWidth: int option) : Task<string> =
    task {
      match featureStateForCaller ctx with
      | None -> return "Feature state not available — no active session."
      | Some state ->
        let timeline = state.CachedTimeline
        match timeline.Entries with
        | [] -> return "No eval timeline — evaluate some cells first."
        | _ ->
          let width = sparklineWidth |> Option.defaultValue 20
          let stats = Features.EvalTimeline.timelineStats width timeline
          return
            [ sprintf "Eval Timeline (%d evals):" stats.Count
              sprintf "  Sparkline: %s" stats.Sparkline
              stats.P50Ms |> Option.map (sprintf "  P50: %.1fms") |> Option.defaultValue "  P50: —"
              stats.P95Ms |> Option.map (sprintf "  P95: %.1fms") |> Option.defaultValue "  P95: —"
              stats.P99Ms |> Option.map (sprintf "  P99: %.1fms") |> Option.defaultValue "  P99: —"
              stats.MeanMs |> Option.map (sprintf "  Mean: %.1fms") |> Option.defaultValue "  Mean: —"
              ""
              yield!
                Features.EvalTimeline.recentChronological 20 timeline
                |> List.map (fun e ->
                  let icon =
                    match e.Status with
                    | Features.EvalTimeline.Succeeded -> "✓"
                    | Features.EvalTimeline.Failed -> "✗"
                    | Features.EvalTimeline.Cancelled -> "○"
                  sprintf "  [%d] %s %dms" e.CellId icon e.DurationMs) ]
            |> String.concat "\n"
    }

  /// MCP Tool: Manage scratch pad (ephemeral code snippets)
  let manageScratchPad (ctx: McpContext) (action: string) (code: string option) (snippetId: int option) : Task<string> =
    task {
      match featureStateForCaller ctx with
      | None -> return "Feature state not available — no active session."
      | Some state ->
        match action.ToLowerInvariant() with
        | "list" ->
          let pad = Features.ScratchPad.create "session"
          let pad =
            state.EvalHistory
            |> List.rev
            |> List.fold (fun p e -> Features.ScratchPad.addSnippet e.Code p) pad
          let snippets = Features.ScratchPad.snippets pad
          match snippets with
          | [] -> return "Scratch pad is empty."
          | _ ->
            return
              snippets
              |> List.map (fun s ->
                let status =
                  match s.Result with
                  | None -> "pending"
                  | Some (Ok v) -> sprintf "ok: %s" (v |> fun x -> match x.Length > 40 with | true -> x.[..37] + "..." | false -> x)
                  | Some (Error e) -> sprintf "err: %s" (e |> fun x -> match x.Length > 40 with | true -> x.[..37] + "..." | false -> x)
                sprintf "  [%d] %s | %s"
                  s.Id
                  (s.Code |> fun c -> match c.Length > 50 with | true -> c.[..47] + "..." | false -> c)
                  status)
              |> fun lines ->
                sprintf "Scratch pad (%d snippets):\n%s" snippets.Length (String.concat "\n" lines)
        | "export" ->
          let pad = Features.ScratchPad.create "session"
          let pad =
            state.EvalHistory
            |> List.rev
            |> List.fold (fun p e ->
              let p = Features.ScratchPad.addSnippet e.Code p
              let lastId = p.NextId - 1
              Features.ScratchPad.recordResult lastId (Ok e.Result) p) pad
          return Features.ScratchPad.exportFsx pad
        | "promote" ->
          let pad = Features.ScratchPad.create "session"
          let pad =
            state.EvalHistory
            |> List.rev
            |> List.fold (fun p e ->
              let p = Features.ScratchPad.addSnippet e.Code p
              let lastId = p.NextId - 1
              Features.ScratchPad.recordResult lastId (Ok e.Result) p) pad
          let successful = Features.ScratchPad.promoteSuccessful pad
          match successful with
          | [] -> return "No successful snippets to promote."
          | _ ->
            return
              sprintf "Promoted %d successful snippets:\n%s"
                successful.Length (String.concat "\n;;\n" successful)
        | _ -> return "Unknown action. Use 'list', 'export', or 'promote'."
    }

  /// MCP Tool: Get eval diff (before/after output comparison)
  let getEvalDiff (ctx: McpContext) (cellIndex: int option) : Task<string> =
    task {
      match featureStateForCaller ctx with
      | None -> return "Feature state not available — no active session."
      | Some state ->
        match state.EvalHistory with
        | [] -> return "No eval history — nothing to diff."
        | [_single] -> return "Only one eval in history — nothing to diff against."
        | history ->
          let (oldOutput, newOutput) =
            let pairFor predicate =
              Features.EvalStore.recentPair predicate history
              |> Option.map (fun (older, newer) -> Some older.Result, Some newer.Result)
              |> Option.defaultValue (None, None)
            match cellIndex with
            | Some idx ->
              match Features.EvalStore.recentPair (fun e -> e.CellIndex = idx) history with
              | Some (older, newer) -> Some older.Result, Some newer.Result
              | None -> pairFor (fun _ -> true)
            | None -> pairFor (fun _ -> true)
          let lines = Features.EvalDiff.diffLines oldOutput newOutput
          let summary = Features.EvalDiff.summarize lines
          return Features.EvalDiff.formatSummary summary
    }

  /// Compose a full diagnostic report: joins test failures, cell graph,
  /// provenance, ripple plan, suggestions, and performance context.
  let diagnose (ctx: McpContext) : Task<string> =
    task {
      match ctx.GetElmModel, ctx.GetFeatureState with
      | None, _ -> return "Diagnosis not available — Elm loop not started."
      | _, None -> return "Diagnosis not available — no active session."
      | Some getModel, Some getState ->
        let model = getModel ()
        let state = getState ()

        let graph = buildCellGraphFromState state

        // Collect all failing tests with their narratives
        let testState = model.LiveTesting.TestState
        let failuresWithNarratives =
          testState.DiscoveredTests
          |> Array.choose (fun tc ->
            match Map.tryFind tc.Id testState.LastResults with
            | Some r ->
              match r.Result with
              | Features.LiveTesting.TestResult.Failed _ ->
                let narrative =
                  match Map.tryFind tc.Id testState.Cached.FailureNarratives with
                  | Some n -> n
                  | None ->
                    { Features.LiveTesting.FailureNarrative.LastPassedAt = None
                      TimeSinceLastPass = None
                      CausalChanges = []
                      PropertyViolation = None
                      Summary = "No narrative available" }
                Some (tc.Id, tc.DisplayName, narrative)
              | _ -> None
            | None -> None)
          |> Array.toList

        // Get scope bindings for Ghostwriter suggestions
        let scopeBindings =
          toScopeBindings (Features.FeatureHooks.scope state)

        let report =
          Features.Diagnostician.Diagnostician.compose
            graph
            failuresWithNarratives
            scopeBindings
            state.CachedTimeline

        // Format as both structured JSON and human summary
        let jsonData =
          {| FailureCount = report.Failures.Length
             Severity = report.Severity.ToString()
             AffectedCellCount = report.AffectedCells.Length
             RippleStepCount =
               match report.RipplePlan with
               | Some p -> p.Steps.Length
               | None -> 0
             SuggestionCount = report.SuggestedFixes.Length
             Failures =
               report.Failures
               |> List.map (fun f ->
                 {| TestName = f.TestName
                    CausalCells = f.CausalCells
                    CausalChanges =
                      f.Narrative.CausalChanges
                      |> List.map (fun c ->
                        match c with
                        | Features.LiveTesting.CausalChange.SymbolChanged s -> {| Kind = "symbol"; Name = s |}
                        | Features.LiveTesting.CausalChange.FileChanged p -> {| Kind = "file"; Name = p |}
                        | Features.LiveTesting.CausalChange.Unknown -> {| Kind = "unknown"; Name = "" |})
                    PropertyViolation =
                      f.Narrative.PropertyViolation
                      |> Option.map (fun pv ->
                        {| PropertyName = pv.PropertyName
                           ShrunkCounterexample = pv.ShrunkCounterexample
                           AlgebraicCategory = pv.AlgebraicCategory |}) |})
             Suggestions =
               report.SuggestedFixes
               |> List.truncate 5
               |> List.map (fun s ->
                 {| Code = s.Code; Explanation = s.Explanation; Confidence = s.Confidence |})
             Performance = report.PerformanceContext |> Option.map (fun s -> {| Sparkline = s.Sparkline; P50Ms = s.P50Ms; P95Ms = s.P95Ms |})
             Summary = report.Summary |}

        return Json.serialize Json.standard jsonData
    }

  /// Coverage intelligence: joins failure narratives + coverage bitmaps + dep graph
  /// into blind spot analysis and correlated failure discovery.
  let coverageIntel (ctx: McpContext) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "Coverage intel not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let cycleState = model.LiveTesting
        let testState = cycleState.TestState

        let failuresWithNarratives =
          testState.DiscoveredTests
          |> Array.choose (fun tc ->
            match Map.tryFind tc.Id testState.LastResults with
            | Some r ->
              match r.Result with
              | Features.LiveTesting.TestResult.Failed _ ->
                let narrative =
                  match Map.tryFind tc.Id testState.Cached.FailureNarratives with
                  | Some n -> n
                  | None ->
                    { Features.LiveTesting.FailureNarrative.LastPassedAt = None
                      TimeSinceLastPass = None
                      CausalChanges = []
                      PropertyViolation = None
                      Summary = "No narrative available" }
                Some (tc.Id, tc.DisplayName, narrative)
              | _ -> None
            | None -> None)
          |> Array.toList

        let allMaps =
          cycleState.InstrumentationMaps
          |> Map.values |> Seq.collect id |> Seq.toArray

        let causalFileResolver (symbols: string list) =
          symbols
          |> List.collect (fun sym ->
            cycleState.DepGraph.PerFileIndex
            |> Map.toList
            |> List.choose (fun (file, symMap) ->
              match Map.containsKey sym symMap with
              | true -> Some file
              | false -> None))
          |> List.distinct

        let reports =
          Features.CoverageIntel.CoverageIntel.compose
            failuresWithNarratives causalFileResolver allMaps
            testState.TestCoverageBitmaps cycleState.DepGraph

        let jsonData =
          reports |> List.map (fun r ->
            {| TestId = r.TestId
               TestName = r.TestName
               Verdict = r.Verdict.ToString()
               CoveragePercent = r.CoveragePercent
               CoveredBranches = r.CoveredBranches
               TotalBranches = r.TotalBranches
               CausalSymbols = r.CausalSymbols
               BlindSpots = r.BlindSpots |> List.map (fun g ->
                 {| File = g.FilePath; Line = g.Line; Branch = g.BranchId |})
               CorrelatedFailures = r.CorrelatedFailures |> List.map string
               Summary = Features.CoverageIntel.CoverageIntel.summarize r |})

        return Json.serialize Json.standard jsonData
    }

  /// Impact forecast: joins eval timeline + cell dependency graph + performance data
  /// into regression detection and downstream impact analysis.
  let impactForecast (ctx: McpContext) (cellIdOpt: int option) : Task<string> =
    task {
      match ctx.GetElmModel, ctx.GetFeatureState with
      | None, _ -> return "Impact forecast not available — Elm loop not started."
      | _, None -> return "Impact forecast not available — no active session."
      | Some getModel, Some getState ->
        let model = getModel ()
        let state = getState ()
        let graph = buildCellGraphFromState state

        let targetCells =
          match cellIdOpt with
          | Some cid -> [ cid ]
          | None -> graph.Cells |> Map.toList |> List.map fst

        let reports =
          targetCells
          |> List.map (fun cellId ->
            let downstream = Features.CellDependencyGraph.transitiveStale graph cellId
            let timeline = state.CachedTimeline
            let timelineStats =
              Features.EvalTimeline.timelineStats 20 state.CachedTimeline
            let durations =
              timeline.Entries
              |> List.filter (fun e -> e.CellId = cellId)
              |> List.map (fun e -> float e.DurationMs)
              |> List.rev |> List.truncate 10
            let p50 = timelineStats.P50Ms |> Option.defaultValue 0.0
            let p95 = timelineStats.P95Ms |> Option.defaultValue 0.0
            Features.ImpactForecast.ImpactForecast.analyzeCell cellId p50 p95 durations downstream)

        let jsonData =
          reports |> List.map (fun r ->
            {| CellId = r.CellId
               P50Ms = r.P50Ms
               P95Ms = r.P95Ms
               DurationTrend = r.DurationTrendMs
               DownstreamCellCount = r.DownstreamCellCount
               Recommendation = r.Recommendation.ToString()
               RegressionCauses = r.RegressionCauses |> List.map (fun c -> c.ToString())
               Summary = Features.ImpactForecast.ImpactForecast.summarize r |})

        return Json.serialize Json.standard jsonData
    }

  /// Action prioritizer: merges all intelligence into a ranked "what to do next" queue.
  let suggestNextAction (ctx: McpContext) : Task<string> =
    task {
      match ctx.GetElmModel, ctx.GetFeatureState with
      | None, _ -> return "Action suggestions not available — Elm loop not started."
      | _, None -> return "Action suggestions not available — no active session."
      | Some getModel, Some getState ->
        let model = getModel ()
        let state = getState ()

        let cycleState = model.LiveTesting
        let testState = cycleState.TestState

        // Build coverage intel reports
        let failuresWithNarratives =
          testState.DiscoveredTests
          |> Array.choose (fun tc ->
            match Map.tryFind tc.Id testState.LastResults with
            | Some r ->
              match r.Result with
              | Features.LiveTesting.TestResult.Failed _ ->
                let narrative =
                  match Map.tryFind tc.Id testState.Cached.FailureNarratives with
                  | Some n -> n
                  | None ->
                    { Features.LiveTesting.FailureNarrative.LastPassedAt = None
                      TimeSinceLastPass = None; CausalChanges = []; PropertyViolation = None
                      Summary = "No narrative available" }
                Some (tc.Id, tc.DisplayName, narrative)
              | _ -> None
            | None -> None)
          |> Array.toList

        let allMaps =
          cycleState.InstrumentationMaps
          |> Map.values |> Seq.collect id |> Seq.toArray
        let coverageReports =
          Features.CoverageIntel.CoverageIntel.compose
            failuresWithNarratives
            (fun _ -> [])
            allMaps testState.TestCoverageBitmaps cycleState.DepGraph

        // Real per-cell durations from EvalTimeline (was `[]` for every cell).
        let graph = buildCellGraphFromState state
        let stats = Features.EvalTimeline.timelineStats 20 state.CachedTimeline
        let p50, p95 = stats.P50Ms |> Option.defaultValue 0.0, stats.P95Ms |> Option.defaultValue 0.0
        let impactReports =
          graph.Cells |> Map.toList |> List.map (fun (cellId, _) ->
            let ds = state.CachedTimeline.Entries |> List.filter (fun e -> e.CellId = cellId)
            let durations = ds |> List.truncate 10 |> List.map (fun e -> float e.DurationMs)
            Features.ImpactForecast.ImpactForecast.analyzeCell cellId p50 p95 durations
              (Features.CellDependencyGraph.transitiveStale graph cellId))
        // Changed cells = the most recently evaluated cell (roast: was EVERY cell, always stale).
        let changedCellIds =
          state.CachedTimeline.Entries |> List.tryHead
          |> Option.map (fun e -> e.CellId) |> Option.toList |> Set.ofList
        let frictionSignalReports = Features.McpFrictionRecorder.Recorder.computeFrictionSignalReports ctx.FrictionStore
        let report =
          Features.ActionPrioritizer.ActionPrioritizer.compose
            graph coverageReports impactReports changedCellIds frictionSignalReports
        let jsonData =
          {| HealthGrade = report.HealthGrade.ToString()
             TotalFailures = report.TotalFailures
             TotalBlindSpots = report.TotalBlindSpots
             TotalRegressions = report.TotalRegressions
             Actions = report.Actions |> List.truncate 10 |> List.map (fun a ->
               {| Kind = a.Kind.ToString()
                  Priority = a.Priority
                  Reason = a.Reason |})
             Summary = Features.ActionPrioritizer.ActionPrioritizer.summarize report |}

        return Json.serialize Json.standard jsonData
    }

  /// List all discovered tests, optionally filtered by pattern or file path.
  let listTests (ctx: McpContext) (patternOpt: string option) (fileOpt: string option) : Task<string> =
    task {
      match ctx.GetElmModel, ctx.GetFeatureState with
      | None, _ -> return "Test list not available — Elm loop not started."
      | _, None -> return "Test list not available — no active session."
      | Some getModel, Some getState ->
        let model = getModel ()
        let state = getState ()
        let graph = buildCellGraphFromState state
        // partitionForListing, not resolveTestLocations: the latter silently
        // drops every ReflectionOnly test (see ResolvedTest). The caller's OWN
        // session's tests, never Primary's (SessionTestAttribution.listable).
        let locations, unlocated =
          Features.TestSourceResolver.partitionForListing
            graph
            (Array.toList (SessionTestAttribution.listable (fun sid -> SageFsModel.cycleForSession sid model) model.Sessions (activeSessionId ctx "mcp")))
            patternOpt
            fileOpt
        let query : Features.TestDiscovery.TestDiscoveryQuery = {
          Pattern    = patternOpt |> Option.filter (fun s -> s.Length > 0)
          FilePath   = fileOpt |> Option.filter (fun s -> s.Length > 0)
          MaxResults = 200
        }
        let jsonData = Features.TestDiscovery.buildListing query locations unlocated
        return Json.serialize Json.standard jsonData
    }

  /// Expose the cell dependency graph with staleness annotations.
  let getCellDependencies (ctx: McpContext) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Cell dependency graph not available — no active session."
      | Some getState ->
        let state = getState ()
        let graph = buildCellGraphFromState state
        // Pass empty changed set — graph structure and wiring is always useful.
        // Callers can use plan_ripple with a specific cell to see staleness impact.
        let report = Features.CellDependenciesReport.CellDependenciesReport.compose graph Set.empty
        let jsonData =
          {| TotalCells    = report.TotalCells
             TotalStale    = report.TotalStale
             TotalEdges    = report.TotalEdges
             StaleCellIds  = report.StaleCellIds
             Summary       = report.Summary
             Nodes         = report.Nodes |> List.map (fun n ->
               {| Id            = n.Id
                  Produces      = n.Produces
                  Consumes      = n.Consumes
                  DownstreamIds = n.DownstreamIds
                  UpstreamIds   = n.UpstreamIds
                  IsStale       = CellFreshness.isStale n.Staleness
                  StaleCauses   = CellFreshness.causes n.Staleness |}) |}
        return Json.serialize Json.standard jsonData
    }

  /// Discover and rank SageFs features relevant to the current session state.
  let discoverFeatures (ctx: McpContext) (topicOpt: string option) : Task<string> =
    task {
      let discoveryCtx =
        match ctx.GetElmModel, ctx.GetFeatureState with
        | None, _ | _, None -> Features.FeatureDiscovery.FeatureDiscovery.emptyContext
        | Some getModel, Some getState ->
          let model = getModel ()
          let state = getState ()
          let testState = model.LiveTesting.TestState
          let failingCount =
            testState.LastResults
            |> Map.values
            |> Seq.filter (fun r ->
              match r.Result with
              | Features.LiveTesting.TestResult.Failed _ -> true
              | _ -> false)
            |> Seq.length
          {
            Features.FeatureDiscovery.DiscoveryContext.FailingTestCount = failingCount
            StaleCellCount   = 0
            TotalEvals       = state.EvalHistory.Length
            TotalTests       = testState.DiscoveredTests.Length
            RequestedTopic   = topicOpt |> Option.filter (fun s -> s.Length > 0)
          }
      let report = Features.FeatureDiscovery.FeatureDiscovery.discover discoveryCtx
      let jsonData =
        {| ContextSummary     = report.ContextSummary
           TotalKnownFeatures = report.TotalKnownFeatures
           Returned           = report.Suggestions.Length
           Suggestions        = report.Suggestions |> List.map (fun s ->
             {| ToolName          = s.ToolName
                ShortDescription  = s.ShortDescription
                ExampleUsage      = s.ExampleUsage
                WhyNow            = s.WhyNow
                Relevance         = s.Relevance.ToString() |}) |}
      return Json.serialize Json.standard jsonData
    }

  /// suggest_repair: compose explain_test_failure → extract causal symbol → preview_what_if
  /// V1: surfaces the causal symbol + current binding + ripple plan without suggesting a new value.
  let suggestRepair (ctx: McpContext) (testName: string) : Task<string> =
    task {
      match ctx.GetElmModel, ctx.GetFeatureState with
      | None, _ | _, None ->
        return "suggest_repair requires an active session with live testing. Start SageFs with a test project first."
      | Some getModel, Some getState ->
        let model = getModel ()
        let testState = model.LiveTesting.TestState
        let state = getState ()
        let matchingTests =
          testState.DiscoveredTests
          |> Array.filter (fun tc ->
            tc.FullName.Contains(testName, StringComparison.OrdinalIgnoreCase)
            || tc.DisplayName.Contains(testName, StringComparison.OrdinalIgnoreCase))
        match matchingTests with
        | [||] ->
          return sprintf "No test found matching '%s'. Use list_tests to see available tests." testName
        | tests ->
          let narrativeOpt =
            tests |> Array.tryPick (fun tc -> Map.tryFind tc.Id testState.Cached.FailureNarratives)
          match narrativeOpt with
          | None ->
            let testNames = tests |> Array.map (fun tc -> tc.DisplayName) |> String.concat ", "
            return
              sprintf
                "No failure narrative for '%s' (%s). The test may not have transitioned Passed→Failed recently, or live testing may not be running. Call run_tests to trigger a run, then retry."
                testName testNames
          | Some narrative ->
            let allChanges =
              narrative.CausalChanges
              |> List.map (fun c ->
                match c with
                | Features.LiveTesting.CausalChange.SymbolChanged s -> {| Kind = "symbol"; Name = s |}
                | Features.LiveTesting.CausalChange.FileChanged f   -> {| Kind = "file";   Name = f |}
                | Features.LiveTesting.CausalChange.Unknown         -> {| Kind = "unknown"; Name = "" |})
            let primarySymbol =
              narrative.CausalChanges
              |> List.tryPick (fun c ->
                match c with
                | Features.LiveTesting.CausalChange.SymbolChanged s -> Some s
                | _ -> None)
            // Build ripple plan for primary symbol if it's in session bindings
            let ripplePlanOpt =
              match primarySymbol, state.EvalHistory with
              | None, _ | _, [] -> None
              | Some sym, _ ->
                let graph = buildCellGraphFromState state
                let scope =
                  Features.FeatureHooks.scope state
                match scope.ActiveBindings |> Map.tryFind sym with
                | None -> None
                | Some binding ->
                  let currentCode = binding.Value |> Option.defaultValue "?"
                  let typeSig = binding.TypeSig
                  let override' = Features.WhatIf.createOverride sym currentCode "<your-fix>" typeSig
                  let plan = Features.WhatIf.planWhatIf graph override'
                  let steps =
                    plan.RippleSteps
                    |> List.map (fun step ->
                      {| CellId = step.CellId
                         Code = step.Code |> fun c -> if c.Length > 60 then c.[..57] + "..." else c
                         Status =
                           match step.Status with
                           | Features.Pending     -> "pending"
                           | Features.Evaluating  -> "evaluating"
                           | Features.Succeeded _ -> "succeeded"
                           | Features.Failed _    -> "failed"
                           | Features.Skipped _   -> "skipped" |})
                  Some {| Symbol = sym; CurrentCode = currentCode; TypeSig = typeSig; AffectedCellCount = plan.AffectedCells.Length; RippleSteps = steps |}
            let timeSince =
              narrative.TimeSinceLastPass
              |> Option.map (fun ts -> sprintf "%.0fs" ts.TotalSeconds)
              |> Option.defaultValue "unknown"
            let suggestion =
              match primarySymbol, ripplePlanOpt with
              | None, _ ->
                sprintf
                  "This test broke ~%s ago. No symbol-level causal changes were detected — review the file changes above and check recent edits manually."
                  timeSince
              | Some sym, None ->
                sprintf
                  "'%s' is the likely cause, but it's not in the current session bindings. Re-evaluate the cell that defines '%s', then retry suggest_repair."
                  sym sym
              | Some sym, Some plan ->
                sprintf
                  "'%s' (%s) is the likely cause. Call `preview_what_if \"%s\" \"<new-value>\"` to preview the ripple before applying. %d cells downstream will re-evaluate."
                  sym plan.TypeSig sym plan.AffectedCellCount
            let jsonData =
              {| TestName      = testName
                 Summary       = narrative.Summary
                 TimeSinceLastPass = timeSince
                 CausalChanges = allChanges
                 PrimarySymbol = primarySymbol |> Option.toObj
                 RipplePlan    = ripplePlanOpt |> Option.toObj
                 Suggestion    = suggestion |}
            return Json.serialize Json.standard jsonData
    }

  // ─── Run App / Stop App / List Runnable Projects ──────────────────────

  let private appStateJson (state: AppRun.AppRunState) = AppRun.toView state

  let runApp (ctx: McpContext) (project: string) : Task<string> =
    withSessionWd ctx "mcp" None (fun sid -> task {
      let request =
        match String.IsNullOrWhiteSpace project with
        | true -> AppRun.RunRequest.DefaultTarget
        | false -> AppRun.RunRequest.Named project
      let! result =
        AppRunOrchestration.runApp ctx.SessionOps (fun () -> DateTime.UtcNow) Timeouts.warmupReadyPollMax (toSessionId sid) request
      return
        match result with
        | Ok state -> Json.serialize Json.standard (appStateJson state)
        | Error e -> SageFsError.describeForAgent e
    })

  let stopApp (ctx: McpContext) : Task<string> =
    withSessionWd ctx "mcp" None (fun sid -> task {
      let! result = AppRunOrchestration.stopApp ctx.SessionOps (toSessionId sid)
      return
        match result with
        | Ok state -> Json.serialize Json.standard (appStateJson state)
        | Error e -> SageFsError.describeForAgent e
    })

  let listRunnableProjects (ctx: McpContext) : Task<string> =
    withSessionWd ctx "mcp" None (fun sid -> task {
      let! infoOpt = ctx.SessionOps.GetSessionInfo (toSessionId sid)
      match infoOpt with
      | None -> return sprintf "Session %s not found." sid
      | Some info ->
        let projects =
          info.ProjectRoles
          |> List.map (fun cp ->
            {| Path = cp.Path
               Role = string cp.Role
               PackageRefs = cp.PackageRefs |})
        let jsonData =
          {| TotalProjects = projects.Length
             ExecutableCount = projects |> List.filter (fun p -> p.Role = "Executable") |> List.length
             ActiveProject = info.ActiveProject |> Option.defaultValue ""
             App = AppRun.describeState info.App
             Projects = projects |}
        return Json.serialize Json.standard jsonData
    })

  // ── Cohort tools (cohort-integration-plan.md Slice 2, item 9) ────────────
  //
  // Claims v1: one implicit cohort per daemon (D2 — 'm bound to MemberId
  // here, at the shell, never inside Cohort.fs). Every tool resolves the
  // caller with `memberIdFor agentName` (never a self-declared member
  // argument) and dispatches exactly one `CohortCommand` through
  // `ctx.CohortOwner.Commit`. `CohortError` crosses into the product error
  // algebra HERE, via `CohortErrorMapping.toSageFsError` — the one boundary roast §10
  // asks for — mapped onto the closest existing `SageFsError` case (Cohort.fs
  // is additive-only in this slice; no new SageFsError case was added).

  // The mapping itself lives in CohortErrorMapping.fs (split out of this file).

  /// `ctx.CohortOwner` is `None` only when nothing wired a cohort owner
  /// (tests that predate Slice 2) — every production McpContext (DaemonMode.fs)
  /// always supplies one.
  let internal requireCohortOwner (ctx: McpContext) : Result<Features.CohortOwner.Handle, SageFsError> =
    match ctx.CohortOwner with
    | Some owner -> Ok owner
    | None -> Error (SageFsError.SessionCreationFailed "no cohort owner is configured for this daemon")

  /// Dispatch one `CohortCommand` through the owner, mapping any refusal to
  /// `SageFsError` at this boundary (roast §10).
  let internal commitCohort (ctx: McpContext) (cmd: Cohort.CohortCommand<MemberTable.MemberId>)
      : Task<Result<Cohort.CohortEvent<MemberTable.MemberId> list * Cohort.CohortEffect<MemberTable.MemberId> list, SageFsError>> =
    task {
      match requireCohortOwner ctx with
      | Error e -> return Error e
      | Ok owner ->
        let! result = owner.Commit cmd
        match result with
        | Ok(events, effects) -> return Ok(events, effects)
        | Error err -> return Error (CohortErrorMapping.toSageFsError err)
    }

  let private parseJoinableRole (raw: string) : Result<Cohort.JoinableRole, SageFsError> =
    match (if isNull raw then "" else raw.Trim().ToLowerInvariant()) with
    | "implementer" -> Ok Cohort.JoinableRole.Implementer
    | "verifier" -> Ok Cohort.JoinableRole.Verifier
    | "observer" -> Ok Cohort.JoinableRole.Observer
    | other -> Error (SageFsError.SessionCreationFailed (sprintf "unknown cohort role '%s' — expected Implementer, Verifier, or Observer" other))

  /// v1's scope wire format: "file:<repo-relative-path>" or
  /// "project:<repo-relative-.fsproj-path>" — matches `Cohort.ClaimScope`'s
  /// two v1 cases (Module/Symbol/Contract are Phase 3, not constructible yet).
  let private parseClaimScope (raw: string) : Result<Cohort.ClaimScope, SageFsError> =
    let raw = if isNull raw then "" else raw.Trim()
    match raw.IndexOf ':' with
    | -1 -> Error (SageFsError.SessionCreationFailed (sprintf "claim scope '%s' must be 'file:<path>' or 'project:<path>'" raw))
    | i ->
      let kind = raw.Substring(0, i).Trim().ToLowerInvariant()
      let path = raw.Substring(i + 1).Trim()
      if path = "" then Error (SageFsError.SessionCreationFailed "claim scope path is empty")
      else
        match kind with
        | "file" -> Ok (Cohort.ClaimScope.File path)
        | "project" -> Ok (Cohort.ClaimScope.Project path)
        | other -> Error (SageFsError.SessionCreationFailed (sprintf "unknown claim scope kind '%s' — expected 'file' or 'project'" other))

  /// Resolve a landing's presented "claimId:fence" pairs (comma-separated —
  /// v1 has no structured multi-value MCP argument type worth adding for this
  /// small surface, see the Slice 2 report's deviation note).
  let private parseClaimFenceList (raw: string) : Result<(Cohort.ClaimId * int64<Measures.fence>) list, SageFsError> =
    let raw = if isNull raw then "" else raw.Trim()
    if raw = "" then Ok []
    else
      raw.Split(',')
      |> Array.toList
      |> List.map (fun pair ->
        match pair.Trim().Split(':') with
        | [| cid; fenceStr |] when cid.Trim() <> "" ->
          match Int64.TryParse(fenceStr.Trim()) with
          | true, f -> Ok (Cohort.ClaimId (cid.Trim()), LanguagePrimitives.Int64WithMeasure<Measures.fence> f)
          | false, _ -> Error (SageFsError.SessionCreationFailed (sprintf "claim fence '%s' is not an integer in '%s'" fenceStr pair))
        | _ -> Error (SageFsError.SessionCreationFailed (sprintf "expected 'claimId:fence', got '%s'" pair)))
      |> List.fold (fun acc item ->
        match acc, item with
        | Error e, _ -> Error e
        | Ok _, Error e -> Error e
        | Ok xs, Ok x -> Ok (xs @ [ x ]))
        (Ok [])

  /// Resolve a target member named by its OWN display string (as
  /// `get_cohort_status` prints it) back to a `MemberId` — used only for
  /// naming the RECIPIENT of a conductor-only action (`reassign_claim`),
  /// never for the acting caller's own identity (that is always
  /// `memberIdFor agentName`, never a self-declared argument). Looked up
  /// against the live frame first so a real bound connection (Mcp/Browser) is
  /// named exactly as presented; falls back to `Minted` so direct/unbound
  /// callers (most tests) can still name each other by plain agent name.
  let private resolveMemberByDisplay (ctx: McpContext) (display: string) : MemberTable.MemberId =
    match ctx.CohortOwner with
    | None -> MemberTable.MemberId.Minted display
    | Some owner ->
      owner.ReadFrame().MemberIds
      |> Array.tryFind (fun m -> MemberTable.MemberId.display m = display)
      |> Option.defaultValue (MemberTable.MemberId.Minted display)

  let internal renderCohortFrame (frame: Cohort.CohortFrame<MemberTable.MemberId>) : string =
    Features.CohortStatusText.render frame

  /// Resolve the caller's SESSION (checkout) for `join_cohort` (item 13c of
  /// sagefs-multiagent-vision.md), via the SAME routing every other tool
  /// uses (`resolveSessionId`): an explicit `workingDirectory` wins, then the
  /// agent's active-session mapping, then the daemon's own working directory
  /// or its one-and-only session. Any resolution that names a real,
  /// registered session (Routable/WarmingUp/Unroutable/FaultedSession) is
  /// bound — the member doesn't need a currently-ROUTABLE worker, just an
  /// existing session id to attribute test outcomes to later. Only `Gone`
  /// (no matching/ambiguous/no session at all) resolves to `None`: the
  /// member still joins, it just contributes no row to the cohort's test
  /// matrix (`CohortOwner.frameOf`) until it joins again with a resolvable
  /// session.
  let private resolveJoinSession (ctx: McpContext) (agentName: string) (workingDirectory: string option) : Task<string option> =
    task {
      let! resolution = resolveSessionId ctx agentName None workingDirectory
      return
        match resolution with
        | Routable sid
        | WarmingUp(sid, _)
        | Unroutable(sid, _)
        | FaultedSession(sid, _) -> Some sid
        | Gone _ -> None
    }

  /// Join the implicit per-daemon cohort as `role` (Implementer/Verifier/
  /// Observer). v1 has no separate `create_cohort` command — the first
  /// member to join an empty cohort becomes its conductor automatically
  /// (`Cohort.decide`'s own semantics for `Join`), so this tool doubles as
  /// create_cohort for the first caller. `workingDirectory` (item 13c) is
  /// resolved to a session id via `resolveJoinSession` and stored on the
  /// member (`MemberRecord.Session`) so the cohort frame can later attribute
  /// this member's checkout's test outcomes to it (`CohortOwner.frameOf`).
  let joinCohort (ctx: McpContext) (agentName: string) (role: string) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    task {
      match parseJoinableRole role with
      | Error e -> return Error e
      | Ok r ->
        let who = memberIdFor agentName
        let! sessionOpt = resolveJoinSession ctx agentName workingDirectory
        let! result = commitCohort ctx (Cohort.CohortCommand.Join(who, r, sessionOpt))
        return
          result
          |> Result.map (fun (events, _) ->
            let becameConductor = events |> List.exists (function Cohort.CohortEvent.ConductorBound _ -> true | _ -> false)
            let sessionNote =
              match sessionOpt with
              | Some sid -> sprintf " Bound to session %s." sid
              | None -> " No session was resolved — you won't appear in the per-session test matrix until you join again from a working directory that matches a session."
            sprintf "Joined cohort as %s (%s).%s%s" (MemberTable.MemberId.display who) (string r) (if becameConductor then " You are the conductor (first to join)." else "") sessionNote)
    }

  let leaveCohort (ctx: McpContext) (agentName: string) : Task<Result<string, SageFsError>> =
    task {
      let who = memberIdFor agentName
      let! result = commitCohort ctx (Cohort.CohortCommand.Depart who)
      return result |> Result.map (fun _ -> sprintf "%s left the cohort." (MemberTable.MemberId.display who))
    }

  let acquireClaim (ctx: McpContext) (agentName: string) (scope: string) (purpose: string) : Task<Result<string, SageFsError>> =
    task {
      match parseClaimScope scope with
      | Error e -> return Error e
      | Ok claimScope ->
        let who = memberIdFor agentName
        let! result = commitCohort ctx (Cohort.CohortCommand.AcquireClaim(who, claimScope, purpose))
        return
          result
          |> Result.bind (fun (events, _) ->
            match events |> List.tryPick (function Cohort.CohortEvent.ClaimAcquired(cid, _, _, fence) -> Some(cid, fence) | _ -> None) with
            | Some(Cohort.ClaimId cid, fence) -> Ok (sprintf "Acquired claim %s over %s (fence=%d)." cid scope (int64 fence))
            | None -> Error (SageFsError.Unexpected (exn "acquire_claim committed with no ClaimAcquired event")))
    }

  let releaseClaim (ctx: McpContext) (agentName: string) (claimId: string) (fence: int64) : Task<Result<string, SageFsError>> =
    task {
      let who = memberIdFor agentName
      let fenceMeasure = LanguagePrimitives.Int64WithMeasure<Measures.fence> fence
      let! result = commitCohort ctx (Cohort.CohortCommand.ReleaseClaim(who, Cohort.ClaimId claimId, fenceMeasure))
      return result |> Result.map (fun _ -> sprintf "Released claim %s." claimId)
    }

  /// Conductor-only (`Cohort.decide` gates `ReassignClaim` on
  /// `Authority.present by state = Authority.Conductor _`, refusing
  /// `NotConductor` otherwise — surfaced here via `CohortErrorMapping.toSageFsError`).
  /// `toMember` names the recipient by ITS OWN display string, resolved via
  /// `resolveMemberByDisplay` — never trusted as the caller's own identity.
  let reassignClaim (ctx: McpContext) (agentName: string) (claimId: string) (toMember: string) : Task<Result<string, SageFsError>> =
    task {
      let by = memberIdFor agentName
      let target = resolveMemberByDisplay ctx toMember
      let! result = commitCohort ctx (Cohort.CohortCommand.ReassignClaim(by, Cohort.ClaimId claimId, target))
      return result |> Result.map (fun _ -> sprintf "Reassigned claim %s to %s." claimId (MemberTable.MemberId.display target))
    }

  /// `claims` is "claimId:fence,claimId:fence,..." (empty string = no
  /// backing claims); `commits` is a comma-separated list of shas. See
  /// `parseClaimFenceList`'s doc for why v1 uses this flat wire format
  /// instead of a structured argument type.
  let requestLanding (ctx: McpContext) (agentName: string) (claims: string) (commits: string) (statement: string) : Task<Result<string, SageFsError>> =
    task {
      match parseClaimFenceList claims with
      | Error e -> return Error e
      | Ok claimList ->
        let commitList =
          (if isNull commits then "" else commits).Split(',')
          |> Array.map (fun s -> s.Trim())
          |> Array.filter (fun s -> s <> "")
          |> Array.toList
        let requester = memberIdFor agentName
        let! result = commitCohort ctx (Cohort.CohortCommand.RequestLanding(requester, claimList, commitList, statement))
        return
          result
          |> Result.bind (fun (events, _) ->
            match events |> List.tryPick (function Cohort.CohortEvent.LandingQueued(lid, _) -> Some lid | _ -> None) with
            | Some(Cohort.LandingId lid) -> Ok (sprintf "Landing %s queued." lid)
            | None -> Error (SageFsError.Unexpected (exn "request_landing committed with no LandingQueued event")))
    }
