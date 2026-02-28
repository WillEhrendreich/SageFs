module SageFs.Features.Replay

open System
open SageFs.Features.Events

/// Status as reconstructed from the event stream.
type ReplayStatus =
  | NotStarted
  | WarmingUp
  | Ready
  | Evaluating
  | Faulted of error: string

/// A single eval captured during replay.
type EvalRecord = {
  Code: string
  Result: string
  TypeSignature: string option
  Duration: TimeSpan
  Timestamp: DateTimeOffset
}

/// Session state reconstructed purely from events.
/// No side effects — just a fold over the event stream.
type SessionReplayState = {
  Status: ReplayStatus
  EvalCount: int
  FailedEvalCount: int
  ResetCount: int
  HardResetCount: int
  LastEvalResult: string option
  WarmupErrors: string list
  EvalHistory: EvalRecord list
  LastDiagnostics: DiagnosticEvent list
  StartedAt: DateTimeOffset option
  LastActivity: DateTimeOffset option
}

module SessionReplayState =
  let empty : SessionReplayState = {
    Status = NotStarted
    EvalCount = 0
    FailedEvalCount = 0
    ResetCount = 0
    HardResetCount = 0
    LastEvalResult = None
    WarmupErrors = []
    EvalHistory = []
    LastDiagnostics = []
    StartedAt = None
    LastActivity = None
  }

  /// Pure fold: apply one event to produce the next state.
  let applyEvent
    (timestamp: DateTimeOffset)
    (state: SessionReplayState)
    (event: SageFsEvent)
    : SessionReplayState =
    let withActivity s = { s with LastActivity = Some timestamp }
    match event with
    | SessionStarted e ->
      { state with
          Status = WarmingUp
          StartedAt = Some e.StartedAt }
      |> withActivity
    | SessionWarmUpCompleted e ->
      { state with WarmupErrors = e.Errors }
      |> withActivity
    | SessionWarmUpProgress _ ->
      state |> withActivity
    | SessionReady ->
      { state with Status = Ready }
      |> withActivity
    | SessionFaulted e ->
      { state with Status = Faulted e.Error }
      |> withActivity
    | SessionReset ->
      { state with
          Status = WarmingUp
          ResetCount = state.ResetCount + 1 }
      |> withActivity
    | SessionHardReset _ ->
      { state with
          Status = WarmingUp
          HardResetCount = state.HardResetCount + 1 }
      |> withActivity
    | EvalRequested _ ->
      { state with Status = Evaluating }
      |> withActivity
    | EvalCompleted e ->
      let record = {
        Code = e.Code
        Result = e.Result
        TypeSignature = e.TypeSignature
        Duration = e.Duration
        Timestamp = timestamp
      }
      { state with
          Status = Ready
          EvalCount = state.EvalCount + 1
          LastEvalResult = Some e.Result
          EvalHistory = state.EvalHistory @ [record] }
      |> withActivity
    | EvalFailed e ->
      { state with
          Status = Ready
          FailedEvalCount = state.FailedEvalCount + 1
          LastEvalResult = Some (sprintf "Error: %s" e.Error)
          LastDiagnostics = e.Diagnostics }
      |> withActivity
    | DiagnosticsChecked e ->
      { state with LastDiagnostics = e.Diagnostics }
      |> withActivity
    | DiagnosticsCleared ->
      { state with LastDiagnostics = [] }
      |> withActivity
    | ScriptLoaded _ -> state |> withActivity
    | ScriptLoadFailed _ -> state |> withActivity
    | McpInputReceived _ -> state |> withActivity
    | McpOutputSent _ -> state |> withActivity
    // Daemon-level events are not relevant to per-session replay
    | DaemonSessionCreated _ -> state
    | DaemonSessionStopped _ -> state
    | DaemonSessionSwitched _ -> state

  /// Replay an entire event stream to reconstruct session state.
  let replayStream
    (events: (DateTimeOffset * SageFsEvent) list)
    : SessionReplayState =
    events
    |> List.fold (fun state (ts, evt) -> applyEvent ts state evt) empty

  /// Format replay state for display.
  let format (state: SessionReplayState) : string =
    let status =
      match state.Status with
      | NotStarted -> "Not Started"
      | WarmingUp -> "Warming Up"
      | Ready -> "Ready"
      | Evaluating -> "Evaluating"
      | Faulted err -> sprintf "Faulted: %s" err
    let started =
      state.StartedAt
      |> Option.map (fun t -> t.ToString("o"))
      |> Option.defaultValue "N/A"
    let lastAct =
      state.LastActivity
      |> Option.map (fun t -> t.ToString("o"))
      |> Option.defaultValue "N/A"
    sprintf "Status: %s\nEvals: %d succeeded, %d failed\nResets: %d soft, %d hard\nStarted: %s\nLast Activity: %s\nWarmup Errors: %d\nLast Result: %s"
      status
      state.EvalCount
      state.FailedEvalCount
      state.ResetCount
      state.HardResetCount
      started
      lastAct
      state.WarmupErrors.Length
      (state.LastEvalResult |> Option.defaultValue "N/A")

  /// Pure: format eval history as an .fsx script for export.
  let exportAsFsx (state: SessionReplayState) : string =
    let header =
      sprintf "// SageFs session export — %s\n// %d evaluations\n"
        (DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"))
        state.EvalHistory.Length
    let formatRecord (i: int) (r: EvalRecord) =
      let typeSig = r.TypeSignature |> Option.defaultValue ""
      sprintf "// [%d] %s — %s\n%s\n"
        (i + 1)
        (r.Timestamp.ToLocalTime().ToString("HH:mm:ss"))
        typeSig
        r.Code
    state.EvalHistory
    |> List.mapi formatRecord
    |> String.concat "\n"
    |> sprintf "%s%s" header

/// A session record as known to the daemon (from event replay).
type DaemonSessionRecord = {
  SessionId: string
  Projects: string list
  WorkingDir: string
  CreatedAt: DateTimeOffset
  StoppedAt: DateTimeOffset option
}

/// Daemon-level state reconstructed from the daemon-sessions stream.
type DaemonReplayState = {
  Sessions: Map<string, DaemonSessionRecord>
  ActiveSessionId: string option
}

module DaemonReplayState =
  let empty : DaemonReplayState = {
    Sessions = Map.empty
    ActiveSessionId = None
  }

  /// Pure fold: apply one daemon event.
  let applyEvent
    (state: DaemonReplayState)
    (event: SageFsEvent)
    : DaemonReplayState =
    match event with
    | DaemonSessionCreated e ->
      let record = {
        SessionId = e.SessionId
        Projects = e.Projects
        WorkingDir = e.WorkingDir
        CreatedAt = e.CreatedAt
        StoppedAt = None
      }
      { state with
          Sessions = state.Sessions |> Map.add e.SessionId record
          ActiveSessionId = Some e.SessionId }
    | DaemonSessionStopped e ->
      let sessions =
        state.Sessions
        |> Map.change e.SessionId (Option.map (fun r -> { r with StoppedAt = Some e.StoppedAt }))
      let activeId =
        if state.ActiveSessionId = Some e.SessionId then
          sessions
          |> Map.toSeq
          |> Seq.tryFind (fun (_, r) -> r.StoppedAt.IsNone)
          |> Option.map fst
        else state.ActiveSessionId
      { state with Sessions = sessions; ActiveSessionId = activeId }
    | DaemonSessionSwitched e ->
      { state with ActiveSessionId = Some e.ToId }
    | _ -> state

  /// Replay the daemon-sessions stream.
  let replayStream (events: (DateTimeOffset * SageFsEvent) list) : DaemonReplayState =
    events
    |> List.fold (fun state (_, evt) -> applyEvent state evt) empty

  /// Get sessions that were alive (not explicitly stopped).
  let aliveSessions (state: DaemonReplayState) : DaemonSessionRecord list =
    state.Sessions
    |> Map.values
    |> Seq.filter (fun r -> r.StoppedAt.IsNone)
    |> Seq.toList

  /// Generate stop events for all alive sessions (for --prune).
  let pruneAllSessions (state: DaemonReplayState) : SageFsEvent list =
    aliveSessions state
    |> List.map (fun r ->
      SageFsEvent.DaemonSessionStopped
        {| SessionId = r.SessionId; StoppedAt = System.DateTimeOffset.UtcNow |})
