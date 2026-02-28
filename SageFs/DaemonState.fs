namespace SageFs.Server

/// Typed state-change events for SSE subscribers.
/// Replaces stringly-typed JSON routing — compiler catches missing handlers.
type DaemonStateChange =
  | StandbyProgress
  | SessionReady of sessionId: string
  | HotReloadChanged
  | ModelChanged of json: string

module DaemonStateChange =
  /// Serialize to JSON for SSE wire format. Single source of truth — used by bridge and SSE stream.
  let toJson = function
    | ModelChanged j -> j
    | SessionReady sid -> sprintf """{"sessionReady":"%s"}""" sid
    | HotReloadChanged -> """{"hotReloadChanged":true}"""
    | StandbyProgress -> """{"standbyProgress":true}"""

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
