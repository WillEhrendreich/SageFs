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
    /// The health verdict, serialized as its caller produced it. Kept as the
    /// caller's own value so this module never restates the health rules.
    Health: obj
    /// The outcome of the last requested rebuild, in the caller's own words.
    LastRestart: LastRestart
  }

  /// What a session's rebuild history says, in the payload's terms. The one
  /// projection every surface uses (MCP status, /api/sessions), so none can read
  /// the record differently. `coreVersion` is the session's own SageFs.Core when
  /// the caller has asked its worker.
  let lastRestartOfRebuild (now: System.DateTime)(coreVersion: string option) (rebuild: LastRebuild) : LastRestart =
    match rebuild with
    | LastRebuild.Latest outcome -> LastRestart.Recorded(restartKindOf outcome, RebuildOutcome.describe now coreVersion outcome)
    | LastRebuild.NeverRebuilt -> LastRestart.NoneRecorded

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

    System.Text.Json.JsonSerializer.Serialize(
      {| state = stateLabelOf sessionState
         scope = "Session"
         sessionId = facts.SessionId
         target = facts.Target
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
         available = Affordances.availableTools sessionState |})
