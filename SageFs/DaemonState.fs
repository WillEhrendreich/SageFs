namespace SageFs.Server

/// Typed state-change events for SSE subscribers.
/// Replaces stringly-typed JSON routing — compiler catches missing handlers.
type DaemonStateChange =
  | StandbyProgress
  | SessionReady of sessionId: string
  | HotReloadChanged
  | FileReloaded of path: string
  | SessionFaulted of sessionId: string * error: string
  | ModelChanged of outputCount: int * diagCount: int
  | WarmupProgress of sessionId: string * step: int * total: int * message: string
  | SystemAlarm of phase: string * message: string

module DaemonStateChange =
  /// SSE event type name for DaemonStateChange events.
  let sseEventType = "state"
  /// Serialize to JSON for SSE wire format. Single source of truth — used by bridge and SSE stream.
  let toJson = function
    | ModelChanged (outputCount, diagCount) ->
      sprintf """{"outputCount":%d,"diagCount":%d}""" outputCount diagCount
    | SessionReady sid -> sprintf """{"sessionReady":"%s"}""" sid
    | HotReloadChanged -> """{"hotReloadChanged":true}"""
    | FileReloaded path -> sprintf """{"fileReloaded":"%s"}""" (path.Replace("\\", "\\\\"))
    | SessionFaulted (sid, err) -> sprintf """{"sessionFaulted":"%s","error":"%s"}""" sid (err.Replace("\"", "\\\""))
    | StandbyProgress -> """{"standbyProgress":true}"""
    | WarmupProgress (sid, step, total, _msg) ->
      sprintf """{"warmupProgress":true,"sessionId":"%s","step":%d,"total":%d}""" sid step total
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
