namespace SageFs

open System
open WorkerProtocol

/// What the user sees — simplified from internal SessionStatus
[<RequireQualifiedAccess>]
type SessionDisplayStatus =
  | Running
  | Starting
  | Errored of reason: string
  | Suspended
  | Stale
  | Restarting

/// A point-in-time snapshot of a session for display
type SessionSnapshot = {
  Id: SessionId
  Projects: string list
  Status: SessionDisplayStatus
  LastActivity: DateTime
  EvalCount: int
  UpSince: DateTime
  IsActive: bool
  WorkingDirectory: string
}

/// File watcher status for display
[<RequireQualifiedAccess>]
type WatchStatus =
  | Active of watchedFiles: int
  | Paused
  | Disabled

/// The full session registry view — what every UI renders
type SessionRegistryView = {
  Sessions: SessionSnapshot list
  ActiveSessionId: SessionId option
  TotalEvals: int
  WatchStatus: WatchStatus option
}

/// Pure functions to build display state from domain state
module SessionDisplay =
  let private staleDuration = TimeSpan.FromMinutes 10.0

  /// Map internal SessionStatus to display status
  let displayStatus (now: DateTime) (info: SessionInfo) : SessionDisplayStatus =
    match info.Status with
    | SessionStatus.Ready
    | SessionStatus.Evaluating ->
      if now - info.LastActivity > staleDuration then
        SessionDisplayStatus.Stale
      else
        SessionDisplayStatus.Running
    | SessionStatus.Starting ->
      SessionDisplayStatus.Starting
    | SessionStatus.Faulted ->
      SessionDisplayStatus.Errored "Session faulted"
    | SessionStatus.Restarting ->
      SessionDisplayStatus.Restarting
    | SessionStatus.Stopped ->
      SessionDisplayStatus.Errored "Session stopped"

  /// Build a snapshot from internal session info
  let snapshot (now: DateTime) (activeId: SessionId option) (info: SessionInfo) : SessionSnapshot =
    { Id = info.Id
      Projects = info.Projects
      Status = displayStatus now info
      LastActivity = info.LastActivity
      EvalCount = 0
      UpSince = info.CreatedAt
      IsActive = activeId = Some info.Id
      WorkingDirectory = info.WorkingDirectory }

  /// Build the full registry view
  let registryView
    (now: DateTime)
    (activeId: SessionId option)
    (sessions: SessionInfo list)
    (watchStatus: WatchStatus option)
    : SessionRegistryView =
    let snapshots =
      sessions |> List.map (snapshot now activeId)
    { Sessions = snapshots
      ActiveSessionId = activeId
      TotalEvals = snapshots |> List.sumBy (fun s -> s.EvalCount)
      WatchStatus = watchStatus }

  /// Build affordances for a session card
  let sessionAffordances (keyMap: KeyMap) (snap: SessionSnapshot) : Affordance list =
    [ yield
        { Action = EditorAction.SwitchSession snap.Id
          Label = "Switch"
          KeyHint = KeyMap.hintFor keyMap (EditorAction.SwitchSession snap.Id)
          Enabled = not snap.IsActive }
      if snap.Status = SessionDisplayStatus.Stale || not snap.IsActive then
        yield
          { Action = EditorAction.StopSession snap.Id
            Label = "Stop"
            KeyHint = KeyMap.hintFor keyMap (EditorAction.StopSession snap.Id)
            Enabled = true }
      match snap.Status with
      | SessionDisplayStatus.Errored _ ->
        yield
          { Action = EditorAction.CreateSession snap.Projects
            Label = "Restart"
            KeyHint = None
            Enabled = true }
      | _ -> () ]
