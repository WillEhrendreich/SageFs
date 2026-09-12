namespace SageFs

open System
open WorkerProtocol

/// What the user sees — simplified from internal SessionStatus. The single
/// definition for every surface (sidebar, MCP/TUI event stream, dashboard):
/// this DU used to be declared twice — once here with Errored/Suspended, once
/// in DashboardTypes.fs with reason-less Faulted/Lost/Stopped — so a session
/// could read Errored on one surface and Faulted on another for the same
/// underlying state.
[<RequireQualifiedAccess>]
type SessionDisplayStatus =
  | Running
  | Starting
  | Restarting
  /// Carries why: replaces both the old Errored (which had a reason) and the
  /// old reason-less Faulted — a card must always be able to say why.
  | Faulted of reason: string
  /// The daemon lost track of the session (e.g. no worker was ever recorded).
  | Lost
  /// Stopped deliberately — the same meaning as the old Suspended case.
  | Stopped
  | Stale

/// Formatting and derivation for SessionDisplayStatus, kept next to the type
/// itself (not as a separately, differently-scoped module) so exactly one
/// entity in the assembly is named SessionDisplayStatus.
module SessionDisplayStatus =
  let label = function
    | SessionDisplayStatus.Running -> "running"
    | SessionDisplayStatus.Starting -> "starting"
    | SessionDisplayStatus.Restarting -> "restarting"
    | SessionDisplayStatus.Faulted _ -> "faulted"
    | SessionDisplayStatus.Lost -> "lost"
    | SessionDisplayStatus.Stopped -> "stopped"
    | SessionDisplayStatus.Stale -> "stale"

  /// Only for a caller that genuinely has no SessionInfo (and so no fault
  /// reason to carry). Everywhere a SessionInfo is available, derive the
  /// status via SessionDisplay.displayStatus instead, which carries the real
  /// fault reason from SessionLifecycleStatus.
  let ofSessionState = function
    | SessionState.Ready -> SessionDisplayStatus.Running
    | SessionState.Evaluating -> SessionDisplayStatus.Running
    | SessionState.WarmingUp -> SessionDisplayStatus.Starting
    | SessionState.Faulted -> SessionDisplayStatus.Faulted "session faulted"
    | SessionState.Uninitialized -> SessionDisplayStatus.Lost

  let cssClass = function
    | SessionDisplayStatus.Running -> "status-ready"
    | SessionDisplayStatus.Starting -> "status-warming"
    | SessionDisplayStatus.Restarting -> "status-warming"
    | SessionDisplayStatus.Faulted _ -> "status-faulted"
    | SessionDisplayStatus.Lost -> "status-faulted"
    | SessionDisplayStatus.Stopped -> "status-faulted"
    | SessionDisplayStatus.Stale -> "status-faulted"

/// A point-in-time snapshot of a session for display
type SessionSnapshot = {
  Id: SessionId
  Name: string option
  Projects: string list
  Status: SessionDisplayStatus
  LastActivity: DateTime
  EvalCount: int
  UpSince: DateTime
  WorkingDirectory: string
}

/// File watcher status for display
[<RequireQualifiedAccess>]
type WatchStatus =
  | Active of watchedFiles: int
  | Paused
  | Disabled

/// Which session this view is focused on — explicit state machine.
[<RequireQualifiedAccess>]
type ActiveSession =
  /// No session exists yet; UI should show "awaiting session" state.
  | AwaitingSession
  /// Actively viewing a valid session.
  | Viewing of SessionId

module ActiveSession =
  let sessionId = function
    | ActiveSession.Viewing sid -> Some sid
    | ActiveSession.AwaitingSession -> None

  let isViewing sid = function
    | ActiveSession.Viewing id -> id = sid
    | ActiveSession.AwaitingSession -> false

/// The full session registry view — what every UI renders
type SessionRegistryView = {
  Sessions: SessionSnapshot list
  ActiveSessionId: ActiveSession
  TotalEvals: int
  WatchStatus: WatchStatus option
}

/// Pure functions to build display state from domain state
module SessionDisplay =
  let staleDuration = Timeouts.staleSessionThreshold

  /// A session is active iff the registry's ActiveSessionId views it.
  let isActive (active: ActiveSession) (snap: SessionSnapshot) =
    ActiveSession.isViewing snap.Id active

  /// Map internal SessionStatus to display status
  let displayStatus (now: DateTime) (info: SessionInfo) : SessionDisplayStatus =
    match info.Status with
    | SessionLifecycleStatus.Ready _
    | SessionLifecycleStatus.Evaluating _
    | SessionLifecycleStatus.Building _ ->
      match now - info.LastActivity > staleDuration with
      | true -> SessionDisplayStatus.Stale
      | false -> SessionDisplayStatus.Running
    | SessionLifecycleStatus.Starting _ ->
      SessionDisplayStatus.Starting
    | SessionLifecycleStatus.Faulted reason ->
      SessionDisplayStatus.Faulted (reason |> Option.defaultValue "Session faulted")
    | SessionLifecycleStatus.Restarting _ ->
      SessionDisplayStatus.Restarting
    | SessionLifecycleStatus.Stopped ->
      SessionDisplayStatus.Stopped

  /// Build a snapshot from internal session info
  let snapshot (now: DateTime) (info: SessionInfo) : SessionSnapshot =
    { Id = info.Id
      Name = info.Name
      Projects = info.Projects
      Status = displayStatus now info
      LastActivity = info.LastActivity
      EvalCount = 0
      UpSince = info.CreatedAt
      WorkingDirectory = info.WorkingDirectory }

  /// Build the full registry view
  let registryView
    (now: DateTime)
    (active: ActiveSession)
    (sessions: SessionInfo list)
    (watchStatus: WatchStatus option)
    : SessionRegistryView =
    let snapshots =
      sessions |> List.map (snapshot now)
    { Sessions = snapshots
      ActiveSessionId = active
      TotalEvals = snapshots |> List.sumBy (fun s -> s.EvalCount)
      WatchStatus = watchStatus }

  /// Build affordances for a session card
  let sessionAffordances (keyMap: KeyMap) (active: ActiveSession) (snap: SessionSnapshot) : Affordance list =
    [ yield
        { Action = EditorAction.SwitchSession (SessionId.value snap.Id)
          Label = "Switch"
          KeyHint = KeyMap.hintFor keyMap (EditorAction.SwitchSession (SessionId.value snap.Id))
          Enabled =
            match active with
            | ActiveSession.Viewing id when id = snap.Id -> false
            | _ -> true }
      match snap.Status = SessionDisplayStatus.Stale || not (isActive active snap) with
      | true ->
        yield
          { Action = EditorAction.StopSession (SessionId.value snap.Id)
            Label = "Stop"
            KeyHint = KeyMap.hintFor keyMap (EditorAction.StopSession (SessionId.value snap.Id))
            Enabled = true }
      | false -> ()
      match snap.Status with
      | SessionDisplayStatus.Faulted _ ->
        yield
          { Action = EditorAction.CreateSession snap.Projects
            Label = "Restart"
            KeyHint = None
            Enabled = true }
      | SessionDisplayStatus.Running
      | SessionDisplayStatus.Starting
      | SessionDisplayStatus.Restarting
      | SessionDisplayStatus.Lost
      | SessionDisplayStatus.Stopped
      | SessionDisplayStatus.Stale -> () ]
