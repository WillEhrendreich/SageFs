namespace SageFs.Server

open SageFs

/// Typed state-change events for SSE subscribers.
/// Replaces stringly-typed JSON routing — compiler catches missing handlers.
type DaemonStateChange =
  | StandbyProgress
  | SessionReady of sessionId: WorkerProtocol.SessionId
  | SessionSwitched of sessionId: WorkerProtocol.SessionId
  /// Hot-reload state changed for the given session. Carries the session ID so
  /// downstream code never has to guess the affected session from a global
  /// "active" selection (the session-isolation blocker: an event for session B
  /// must not push session A's state just because A is the active tab).
  | HotReloadChanged of sessionId: WorkerProtocol.SessionId
  | FileReloaded of sessionId: WorkerProtocol.SessionId * path: string
  | SessionFaulted of sessionId: WorkerProtocol.SessionId * error: string
  | ModelChanged of outputCount: int * diagCount: int
  | WarmupProgress of sessionId: WorkerProtocol.SessionId * step: int * total: int * message: string
  | SystemAlarm of phase: string * message: string

module DaemonStateChange =
  /// SSE event type name for DaemonStateChange events.
  let sseEventType = "state"
  /// Serialize to JSON for SSE wire format. Single source of truth — used by bridge and SSE stream.
  let toJson = function
    | ModelChanged (outputCount, diagCount) ->
      sprintf """{"outputCount":%d,"diagCount":%d}""" outputCount diagCount
    | SessionReady sid -> sprintf """{"sessionReady":"%s"}""" (WorkerProtocol.SessionId.value sid)
    | SessionSwitched sid -> sprintf """{"sessionSwitched":"%s"}""" (WorkerProtocol.SessionId.value sid)
    | HotReloadChanged sid -> sprintf """{"hotReloadChanged":true,"sessionId":"%s"}""" (WorkerProtocol.SessionId.value sid)
    | FileReloaded (sid, path) -> sprintf """{"fileReloaded":"%s","sessionId":"%s"}""" (path.Replace("\\", "\\\\")) (WorkerProtocol.SessionId.value sid)
    | SessionFaulted (sid, err) ->
      sprintf """{"sessionFaulted":"%s","error":"%s"}""" (WorkerProtocol.SessionId.value sid) (err.Replace("\"", "\\\""))
    | StandbyProgress -> """{"standbyProgress":true}"""
    | WarmupProgress (sid, step, total, _msg) ->
      sprintf """{"warmupProgress":true,"sessionId":"%s","step":%d,"total":%d}""" (WorkerProtocol.SessionId.value sid) step total
    | SystemAlarm (phase, msg) ->
      sprintf """{"systemAlarm":true,"phase":"%s","message":"%s"}""" phase (msg.Replace("\"", "\\\""))

module DaemonInfo =
  let version =
    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
    |> Option.ofObj
    |> Option.map (fun v -> v.ToString())
    |> Option.defaultValue "unknown"

  let otelConfigured =
    System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    |> Option.ofObj |> Option.isSome

// DaemonInfo and DaemonState are now in SageFs namespace (SageFs.Core).
// This module re-exports functions so existing code using SageFs.Server.DaemonState compiles.
module DaemonState =
  let SageFsDir = SageFs.DaemonState.SageFsDir
  let isProcessAlive = SageFs.DaemonState.isProcessAlive
  let read = SageFs.DaemonState.read
  let readOnPort = SageFs.DaemonState.readOnPort
  let requestShutdown = SageFs.DaemonState.requestShutdown
