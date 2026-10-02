namespace SageFs

// ── The session-status payload, and the truth rule inside it ─────────
//
// Extracted from `Mcp.fs` (which is over its line budget) because this is the
// serialization of facts the caller already computed, with no orchestration in
// it. It is a pure function of those facts, so it belongs on its own and can be
// reasoned about without a daemon.
//
// The rule that matters lives in `stateLabelOf`: a session is `Ready` only when
// the SAME reconciled status that supplies the lifecycle, the loaded projects,
// the health verdict and the available tools also says it is ready. That is
// what stops a warming session from reporting "Ready" beside
// "lifecycle: Starting" and an empty `loadedProjects` — the shape a live
// dogfood session actually produced, and the reason an agent gating on
// `state == Ready` evaluated against a session with no assemblies loaded.
module SessionStatusPayload =

  /// The top-level state label for a session state.
  ///
  /// `Evaluating` and `Ready` both report "Ready": an in-flight eval is a LOADED
  /// session, not a readiness failure, and calling that a warming session would
  /// be the opposite over-correction. Everything that is genuinely not ready
  /// reports its own state, so the three fields can never disagree.
  let stateLabelOf (sessionState: SessionState) : string =
    match sessionState with
    | SessionState.Ready -> "Ready"
    | SessionState.Evaluating -> "Ready"
    | SessionState.WarmingUp -> "WarmingUp"
    | SessionState.Faulted -> "Faulted"
    | SessionState.Uninitialized -> "WarmingUp"

  /// What the last rebuild a caller asked for did. A hard reset with
  /// rebuild=true answers "initiated" and does its work later, so this is the
  /// only place its result can be read. A build that FAILS while the old worker
  /// is still serving fires no fault, so without this it looks exactly like a
  /// build that has not finished.
  [<RequireQualifiedAccess>]
  type RestartKind =
    | InProgress
    | Succeeded
    | FailedStillServing
    | FailedNotServing

  module RestartKind =
    /// The one spelling of each kind on the wire.
    let label (kind: RestartKind) : string =
      match kind with
      | RestartKind.InProgress -> "InProgress"
      | RestartKind.Succeeded -> "Succeeded"
      | RestartKind.FailedStillServing -> "FailedStillServing"
      | RestartKind.FailedNotServing -> "FailedNotServing"

  /// The payload's name for what a rebuild outcome is. Exhaustive, so a new
  /// outcome cannot be added without deciding how it is reported.
  let restartKindOf (outcome: RebuildOutcome) : RestartKind =
    match outcome with
    | RebuildOutcome.InProgress _ -> RestartKind.InProgress
    | RebuildOutcome.Succeeded _ -> RestartKind.Succeeded
    | RebuildOutcome.FailedStillServing _ -> RestartKind.FailedStillServing
    | RebuildOutcome.FailedNotServing _ -> RestartKind.FailedNotServing

  /// Nothing recorded is its own case: a session nobody restarted must not be
  /// reported as a restart that succeeded.
  [<RequireQualifiedAccess>]
  type LastRestart =
    | NoneRecorded
    | Recorded of kind: RestartKind * message: string

  /// Everything the payload needs, gathered by the caller. A record so a new
  /// field is a compile error at every construction site rather than a
  /// silently-defaulted hole in the response.
  type Facts = {
    SessionState: SessionState
    SessionId: string
    Target: SessionProjectTarget list
    LoadedProjects: string list
    ReconciledStatus: WorkerProtocol.SessionLifecycleStatus
    Workflow: WorkflowTypes.SessionWorkflow
    CoreVersion: string
    EvalCount: int
    AverageDurationMs: int64
    /// The health verdict as `SessionHealth.toJson` produces it. Typed, so the
    /// payload's shape is checked here instead of hidden behind `obj`; this
    /// module still never restates the health rules.
    Health: HealthView
    /// The outcome of the last requested rebuild, in the caller's own words.
    LastRestart: LastRestart
    /// What the worker last said a save did to the running process.
    LastReload: SessionReload
    /// Whether the REPL and live tests run the same build as the app.
    ReplFreshness: ReplFreshness
    /// Whether the build the session runs is behind the files on disk. Not the REPL's freshness: that is the REPL behind the
    /// app, this is the disk ahead of the build.
    SourceState: SourceState
  }

  /// What a session's rebuild history says, in the payload's terms. The one
  /// projection every surface uses (MCP status, /api/sessions), so none can read
  /// the record differently. `coreVersion` is the session's own SageFs.Core when
  /// the caller has asked its worker.
  let lastRestartOfRebuild (now: System.DateTime)(coreVersion: string option) (rebuild: LastRebuild) : LastRestart =
    match rebuild with
    | LastRebuild.Latest outcome -> LastRestart.Recorded(restartKindOf outcome, RebuildOutcome.describe now coreVersion outcome)
    | LastRebuild.NeverRebuilt -> LastRestart.NoneRecorded

  /// What the last rebuild of the session did, when the caller may not have the
  /// session at all. Read off the session: the manager records it for every caller.
  let lastRestartOfSession (info: WorkerProtocol.SessionInfo option) (coreVersion: string option) : LastRestart =
    match info with
    | Some session -> lastRestartOfRebuild System.DateTime.UtcNow coreVersion session.Rebuild
    | None -> LastRestart.NoneRecorded

  /// What the worker last said a save did, off the session (no session, no reload).
  let lastReloadOfSession (info: WorkerProtocol.SessionInfo option) : SessionReload =
    match info with
    | Some session -> session.Reload
    | None -> SessionReload.NoReloadYet

  /// Whether the REPL runs the app's build, off the session (no session, nothing to be behind).
  let replFreshnessOfSession (info: WorkerProtocol.SessionInfo option) : ReplFreshness =
    match info with
    | Some session -> session.Freshness
    | None -> ReplFreshness.InSync

  /// Whether the REPL of this session runs the build the app runs, read off the registry's own record so every surface agrees. A
  /// session the registry cannot answer for is level: there is nothing it is known to be behind.
  let replFreshnessOf (ops: SessionManagementOps) (sid: string) : System.Threading.Tasks.Task<ReplFreshness> =
    task {
      try
        let! info = ops.GetSessionInfo (McpSessionRouting.toSessionId sid)
        return replFreshnessOfSession info
      with _ -> return ReplFreshness.InSync
    }

  /// How `lastRestart` appears in EVERY status shape (routable, warming,
  /// faulted). One function, so a shape cannot forget it: the warming shape did,
  /// and a cold restart is exactly the warming shape.
  let lastRestartJson (restart: LastRestart) : obj | null =
    match restart with
    | LastRestart.NoneRecorded -> null
    | LastRestart.Recorded (kind, message) -> box {| outcome = RestartKind.label kind; message = message |}

  /// Build the `get_session_status` payload.
  ///
  /// One status drives every field. `available` is derived from the same
  /// `SessionState` the top-level label is, so the tools an agent is told it
  /// may call can never disagree with the state it was told the session is in.
  let serialize (facts: Facts) : string =
    let sessionState = facts.SessionState

    Json.serialize Json.standard
      {| state = stateLabelOf sessionState
         scope = "Session"
         sessionId = facts.SessionId
         target = facts.Target |> List.map SessionProjectTarget.toWire
         loadedProjects = facts.LoadedProjects
         lifecycle = WorkerProtocol.SessionLifecycleStatus.label facts.ReconciledStatus
         workerPid = WorkerProtocol.SessionLifecycleStatus.workerPid facts.ReconciledStatus
         workerPort = WorkerProtocol.SessionLifecycleStatus.workerPort facts.ReconciledStatus
         workflow = WorkflowTypes.SessionWorkflow.label facts.Workflow
         coreVersion = facts.CoreVersion
         evalCount = facts.EvalCount
         averageDurationMs = facts.AverageDurationMs
         health = facts.Health
         lastRestart = lastRestartJson facts.LastRestart
         lastReload = SessionReload.toWire facts.LastReload
         replFreshness = ReplFreshness.toWire facts.ReplFreshness
         sourceState = SourceState.toWire facts.SourceState
         available = Affordances.availableTools sessionState |}

  // ── `wait_seconds` ─────────────────────────────────────────────────
  //
  // A caller that asks get_session_status to wait parks on the
  // SessionManager's event-driven AwaitReady instead of sleeping and polling.
  // What that wait did is a closed set, reported in the payload next to the
  // status the caller then reads.

  /// What a `wait_seconds` request did.
  [<RequireQualifiedAccess>]
  type StatusWait =
    /// Nothing to wait for: the session was Ready with no rebuild running, or
    /// Faulted or Stopped, or no wait was asked for. A rebuild in progress is
    /// never this: it is a state that is waited on.
    | NotNeeded
    /// The session reached Ready (and, if a rebuild was running, the rebuild
    /// finished and its new worker is serving) while the caller was parked.
    | BecameReady
    /// The session faulted or stopped while the caller was parked, or the
    /// rebuild being waited on failed. `wait.lastRebuild` says which.
    | Faulted
    /// The session was still warming, or the rebuild still running, when the
    /// wait ran out.
    | TimedOut

  /// How a wait ended and how long the caller was parked.
  type WaitReport = { Outcome: StatusWait; WaitedMs: int64 }

  /// Whether a session's lifecycle status is worth parking on.
  [<RequireQualifiedAccess>]
  type WaitPlan =
    | Park
    | DoNotPark

  module StatusWait =
    /// The one spelling of each outcome on the wire.
    let label (wait: StatusWait) : string =
      match wait with
      | StatusWait.NotNeeded -> "NotNeeded"
      | StatusWait.BecameReady -> "BecameReady"
      | StatusWait.Faulted -> "Faulted"
      | StatusWait.TimedOut -> "TimedOut"

    /// The report for a call that did not wait.
    let notWaited : WaitReport = { Outcome = StatusWait.NotNeeded; WaitedMs = int64 Timeouts.notRun.TotalMilliseconds }

    /// What the caller asked for, as a TimeSpan: clamped, never refused.
    let clampSeconds (seconds: int) : System.TimeSpan =
      let cap = int Timeouts.statusWaitCap.TotalSeconds
      System.TimeSpan.FromSeconds(float (min cap (max 0 seconds)))

    /// Only a session that is on its way to Ready is worth waiting for, and a
    /// Ready session whose rebuild is still running is on its way to the new
    /// build. `ReadyWait` is the one decision, the same one the session manager
    /// settles the parked caller with, so the two cannot disagree.
    let planFor (status: WorkerProtocol.SessionLifecycleStatus) (rebuild: LastRebuild) : WaitPlan =
      match ReadyWait.plan status rebuild with
      | ReadyWait.Plan.Park -> WaitPlan.Park
      | ReadyWait.Plan.DoNotPark -> WaitPlan.DoNotPark

    /// How AwaitReady answered, in the payload's terms.
    let ofAwaitReady (answer: Result<unit, SageFsError>) : StatusWait =
      match answer with
      | Result.Ok () -> StatusWait.BecameReady
      | Result.Error (SageFsError.WorkerTimeout _) -> StatusWait.TimedOut
      | Result.Error _ -> StatusWait.Faulted

    /// What the last rebuild did, as the payload already says it in `lastRestart`
    /// (read after the wait, so it is the outcome the caller waited for).
    /// `NoneRecorded` when the session was never rebuilt.
    let private lastRebuildOf (body: System.Text.Json.Nodes.JsonObject) : string =
      match body["lastRestart"] with
      | :? System.Text.Json.Nodes.JsonObject as restart ->
        match restart["outcome"] with
        | null -> "NoneRecorded"
        | outcome -> outcome.GetValue<string>()
      | _ -> "NoneRecorded"

    /// Add the `wait` field to a status payload. Every status shape is a JSON
    /// object, so one place adds it to all of them.
    let withReport (report: WaitReport) (payload: string) : string =
      match System.Text.Json.Nodes.JsonNode.Parse payload with
      | :? System.Text.Json.Nodes.JsonObject as body ->
        let wait = System.Text.Json.Nodes.JsonObject()
        wait["outcome"] <- System.Text.Json.Nodes.JsonValue.Create(label report.Outcome)
        wait["waitedMs"] <- System.Text.Json.Nodes.JsonValue.Create(report.WaitedMs)
        wait["lastRebuild"] <- System.Text.Json.Nodes.JsonValue.Create(lastRebuildOf body)
        body["wait"] <- wait
        body.ToJsonString()
      | _ -> payload
