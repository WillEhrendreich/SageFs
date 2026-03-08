/// Centralised runtime configuration for SageFs.
/// All environment variable reads happen here — no other module calls
/// Environment.GetEnvironmentVariable directly (except OTEL SDK integration points).
///
/// Rule: [<Literal>] for compile-time constants (usable in patterns/attributes/sprintf).
///       Plain let for runtime values (env-var derived, evaluated once at module init).
module SageFs.SageFsConfig

open System

// ---------------------------------------------------------------------------
// Private helpers — parse env vars with logged fallback, never throw.
// Using eprintfn instead of a logger: config is read before LoggerFactory exists.
// ---------------------------------------------------------------------------

let private envInt (name: string) (defaultValue: int) =
  match Environment.GetEnvironmentVariable(name) with
  | null | "" -> defaultValue
  | s ->
    match Int32.TryParse(s) with
    | true, v -> v
    | false, _ ->
      eprintfn
        "[SageFsConfig] WARNING: env var %s=%s is not a valid integer; using default %d"
        name s defaultValue
      defaultValue

let private envBool (name: string) (defaultValue: bool) =
  match Environment.GetEnvironmentVariable(name) with
  | null | "" -> defaultValue
  | s ->
    match s.Trim().ToLowerInvariant() with
    | "1" | "true" | "yes" -> true
    | "0" | "false" | "no" -> false
    | _ ->
      eprintfn
        "[SageFsConfig] WARNING: env var %s=%s is not a valid bool; using default %b"
        name s defaultValue
      defaultValue

let private envString (name: string) (defaultValue: string) =
  match Environment.GetEnvironmentVariable(name) with
  | null | "" -> defaultValue
  | s -> s

// ---------------------------------------------------------------------------
// Compile-time constants — use [<Literal>] so they're usable in patterns.
// ---------------------------------------------------------------------------

/// Default MCP server port. Used in help text, match arms, and as base for DashboardPort.
[<Literal>]
let DefaultMcpPort = 37749

/// Default Dashboard port (MCP + 1). Named here so the "+1" relationship is documented once.
[<Literal>]
let DefaultDashboardPort = 37750

// ---------------------------------------------------------------------------
// Runtime-derived configuration — evaluated once at module initialisation.
// ---------------------------------------------------------------------------

/// How long (ms) awaitWorkerPort waits for WORKER_PORT= before declaring startup failure.
/// Default: 120 seconds — covers cold .NET startup on slow machines.
let WorkerStartupTimeoutMs : int =
  envInt "SAGEFS_WORKER_STARTUP_TIMEOUT_MS" 120_000

/// Bind host for HTTP servers (MCP and Dashboard).
/// SAGEFS_BIND_HOST is read here only — all other modules use this value.
let BindHost : string =
  envString "SAGEFS_BIND_HOST" "127.0.0.1"

/// Whether this process is running under external supervision (auto-restart on crash).
let IsSupervised : bool =
  envBool "SAGEFS_SUPERVISED" false

/// Worker restart count injected by the supervisor. 0 = first launch.
/// Guards against restart storms — see RestartPolicy.
let RestartCount : int =
  envInt "SAGEFS_RESTART_COUNT" 0

/// Whether hot reload (assembly patching) is enabled for this worker session.
let HotReloadEnabled : bool =
  envBool "SAGEFS_HOT_RELOAD" false

/// Whether browser dev-reload (live page refresh) is enabled.
let DevReloadEnabled : bool =
  envBool "SAGEFS_DEVRELOAD" false

/// OTLP exporter endpoint. Empty string means OTEL is not configured.
let OtelEndpoint : string =
  envString "OTEL_EXPORTER_OTLP_ENDPOINT" ""

/// OTLP protocol (grpc or http/protobuf).
let OtelProtocol : string =
  envString "OTEL_EXPORTER_OTLP_PROTOCOL" "grpc"

/// OTEL service name for this process.
let OtelServiceName : string =
  envString "OTEL_SERVICE_NAME" "sagefs"

/// Whether OpenTelemetry is configured (endpoint is non-empty).
let OtelConfigured : bool =
  OtelEndpoint <> ""

/// MCP port parsed from the env var only (CLI arg takes precedence — see Program.parseMcpPort).
/// Most code should use the resolved port from Program.parseMcpPort, not this value directly.
let McpPortFromEnv : int =
  envInt "SAGEFS_MCP_PORT" DefaultMcpPort
