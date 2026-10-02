// scripts/start-sagefs-otel.fsx [<sagefs args>]   run SageFs with OpenTelemetry export to a local OTLP collector
// Run with: dotnet fsi scripts/start-sagefs-otel.fsx -- [<sagefs args>]
//
// Requires a running OTLP receiver on localhost:4318 (HTTP/Protobuf). Quick start with Jaeger all-in-one:
//   docker run --rm -p 4318:4318 -p 16686:16686 jaegertracing/all-in-one:latest
//   then open http://localhost:16686 to view traces.
//
// Simpler alternative: structured logs go to a file sink even without OTel. Just run SageFs normally; logs are
// written to %LOCALAPPDATA%\SageFs\sagefs.log (Windows) or ~/.local/share/sagefs/sagefs.log (Linux). Set
// OTEL_EXPORTER_OTLP_ENDPOINT only when you want traces and metrics in a collector.
//
// Starts `sagefs` from the repo root with the three OTEL_* variables set, and exits with its exit code.
// Exit codes: 0 sagefs exited cleanly (its own code otherwise); 127 sagefs could not be started.
open System
open System.Diagnostics
open System.IO

// ── named values ─────────────────────────────────────────────────────────────

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let otlpEndpoint = "http://localhost:4318"
let otlpProtocol = "http/protobuf"
let serviceName = "sagefs"
let notStartedExitCode = 127

let argv = fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

let code =
  try
    let psi = ProcessStartInfo("sagefs")
    argv |> List.iter psi.ArgumentList.Add
    psi.WorkingDirectory <- repoRoot
    psi.UseShellExecute <- false
    psi.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] <- otlpEndpoint
    psi.Environment["OTEL_EXPORTER_OTLP_PROTOCOL"] <- otlpProtocol
    psi.Environment["OTEL_SERVICE_NAME"] <- serviceName
    use p = Process.Start psi
    p.WaitForExit()
    p.ExitCode
  with e ->
    eprintfn "start-sagefs-otel: could not start sagefs: %s" e.Message
    notStartedExitCode

exit code
