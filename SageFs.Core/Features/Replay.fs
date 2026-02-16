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
