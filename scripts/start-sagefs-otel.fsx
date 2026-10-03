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

// Locate the repo at RUNTIME, walking up from this script's own location until we
// find the solution file. A build-time constant would bake in the directory the
// script was COMPILED, which is not where it RUNS.
let repoRoot =
  let rec walk (dir: string) (depth: int) : string =
    if depth > 24 then "" else
    let full =
      try
        let f = Path.GetFullPath dir
        let r = Path.GetPathRoot f
        if f = r then f else Path.TrimEndingDirectorySeparator f
      with _ -> dir
    if File.Exists(Path.Combine(full, "SageFs.slnx")) then full
    else
      let parent = Path.GetDirectoryName full
      if String.IsNullOrEmpty parent || parent = full then ""
      else walk parent (depth + 1)
  // Start from the directory this script lives in. A build-time constant only names where it was
  // built, so the walk up to SageFs.slnx is what locates the repo where the code actually runs.
  let start =
    try Path.GetDirectoryName __SOURCE_DIRECTORY__ with _ -> "."
  walk start 0
let repo =
  if repoRoot = "" then
    failwith "Could not locate the SageFs repository (no SageFs.slnx found walking up from this script)."
  else repoRoot
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
