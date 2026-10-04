namespace SageFs.Server

open SageFs

// The SSE state-change event vocabulary formerly defined here as
// `DaemonStateChange` now lives in SseEvent.fs, unified with the former
// SessionEvents.SessionEvent type into one `SseEvent` DU with one
// serializer (roast-5 §1). This file keeps only daemon info/probe state.

module DaemonInfo =
  /// The build this daemon is, as `/health`, `/api/daemon-info` and the
  /// dashboard statusline show it: version plus the commit it was built from.
  /// It used to be the 4-part assembly version (`0.6.892.0`), which carries
  /// no commit, so a locally installed build read as the published one.
  let version =
    SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current ()
    |> SageFs.Features.FrictionTelemetryTypes.SageFsVersion.display

  let otelConfigured =
    System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    |> Option.ofObj |> Option.isSome
