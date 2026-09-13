namespace SageFs.Server

open SageFs

// The SSE state-change event vocabulary formerly defined here as
// `DaemonStateChange` now lives in SseEvent.fs, unified with the former
// SessionEvents.SessionEvent type into one `SseEvent` DU with one
// serializer (roast-5 §1). This file keeps only daemon info/probe state.

module DaemonInfo =
  let version =
    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
    |> Option.ofObj
    |> Option.map (fun v -> v.ToString())
    |> Option.defaultValue "unknown"

  let otelConfigured =
    System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    |> Option.ofObj |> Option.isSome
