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

/// Superseded by Timeouts.warmupInactivityLimit / Timeouts.warmupAbsoluteMax,
/// which awaitWorkerPort now actually reads: a flat "no port within N ms"
/// bound couldn't tell a stuck worker from a large repo genuinely still
/// discovering projects (fcs-trial-a/b/c, 2026-09-22). Kept — not read by
/// awaitWorkerPort any more — because SAGEFS_WORKER_STARTUP_TIMEOUT_MS may
/// still be set in someone's environment; do not repurpose this name.
let WorkerStartupTimeoutMs : int =
  envInt "SAGEFS_WORKER_STARTUP_TIMEOUT_MS" (int Timeouts.legacyWorkerStartup.TotalMilliseconds)

/// Admission ceiling for the SessionManager mailbox's queue
/// (`MailboxProcessor<SessionCommand>.CurrentQueueLength`), mirroring
/// ElmLoop's own 256-message high-watermark alarm (`ElmLoop.fs:104-131`) —
/// the mailbox that didn't have an equivalent bound. Caller-facing ops
/// (create/stop/restart session) check this BEFORE posting and refuse with
/// `SageFsError.SupervisorBusy` instead of queueing indefinitely behind
/// whatever is already backed up. Sized generously so it never engages
/// under real usage — only under genuine overload.
let SessionManagerQueueCapacity : int =
  envInt "SAGEFS_SESSION_MANAGER_QUEUE_CAPACITY" 256

/// Members any one `list_members` reply carries (the rung cap of the progressive map,
/// docs/progressive-map-research-2026-10-24.md). The reply always states the total and names the
/// offset for the rest, so the cap bounds the message without hiding the size of the set. Sized
/// around the house reply ceiling; measure a real namespace before raising it, and lower it if
/// measured replies run long.
let ListMembersMax : int =
  envInt "SAGEFS_LIST_MEMBERS_MAX" 100

/// Projects the compiler keeps checked for `list_references`, so a second ask after a run that ran out of budget goes
/// further instead of starting over. Sized above the project count of the largest session measured (eight, the test
/// project's session); each cached project holds its typed trees, so raise it only with memory to spare.
let ReferencesProjectCacheSize : int =
  envInt "SAGEFS_REFERENCES_PROJECT_CACHE" 12

/// What an operator said about the megabytes the worker may hold before `list_references` drops the compiler's cache,
/// as a closed set. A value that is not a positive integer is kept with its text, so the warning can quote it and the
/// default for the machine applies; it is never read as zero, which would drop the cache on every ask.
[<RequireQualifiedAccess>]
type ResidentCeilingSetting =
  /// Nothing set: the ceiling is a share of the machine's memory (`ResidentCeiling.ceilingOf`).
  | Unset
  | Megabytes of megabytes: int
  | Rejected of text: string

[<RequireQualifiedAccess>]
module ResidentCeilingSetting =

  /// A positive integer is a setting; empty or absent is unset; anything else is rejected with its text.
  let parse (text: string | null) : ResidentCeilingSetting =
    match text with
    | null -> ResidentCeilingSetting.Unset
    | "" -> ResidentCeilingSetting.Unset
    | s ->
      match Int32.TryParse s with
      | true, megabytes when megabytes > 0 -> ResidentCeilingSetting.Megabytes megabytes
      | _ -> ResidentCeilingSetting.Rejected s

/// SAGEFS_REFERENCES_RESIDENT_CEILING_MB, read once. The ceiling's default is not a number here: it is a share of the
/// machine's memory, worked out where the machine can be read (`ResidentCeiling.resolve`, which compiles after this
/// file). A flat default could not fit both a 4 GB laptop and the 64 GB workstation where the eight-project
/// SageFs.Tests session needs 4.0 to 4.3 GB to be answered project-wide.
let ReferencesResidentCeilingSetting : ResidentCeilingSetting =
  let setting = ResidentCeilingSetting.parse (Environment.GetEnvironmentVariable "SAGEFS_REFERENCES_RESIDENT_CEILING_MB")
  match setting with
  | ResidentCeilingSetting.Rejected text ->
    eprintfn
      "[SageFsConfig] WARNING: env var SAGEFS_REFERENCES_RESIDENT_CEILING_MB=%s is not a positive integer; using the default for this machine"
      text
  | ResidentCeilingSetting.Unset
  | ResidentCeilingSetting.Megabytes _ -> ()
  setting

/// References any one `list_references` reply carries. A line is a file position and a kind, shorter than a member
/// name, so the cap matches `ListMembersMax`. The reply states the total and names the offset for the rest.
let ListReferencesMax : int =
  envInt "SAGEFS_LIST_REFERENCES_MAX" 100

/// The loopback interface the daemon's HTTP listeners (MCP, dashboard) bind.
/// There is deliberately no non-loopback case: SageFs evaluates arbitrary F#
/// for whoever reaches those ports and has no authentication, so a LAN bind
/// would be remote code execution. See LoopbackHost.parse.
[<RequireQualifiedAccess>]
type LoopbackHost =
  /// "localhost" — Kestrel listens on both 127.0.0.1 and [::1].
  | Localhost
  | Ipv4
  | Ipv6

[<RequireQualifiedAccess>]
module LoopbackHost =

  /// The host as it appears in a URL.
  let urlHost (host: LoopbackHost) : string =
    match host with
    | LoopbackHost.Localhost -> "localhost"
    | LoopbackHost.Ipv4 -> "127.0.0.1"
    | LoopbackHost.Ipv6 -> "[::1]"

  /// The Kestrel listen URL for a port on this host.
  let listenUrl (host: LoopbackHost) (port: int) : string =
    sprintf "http://%s:%d" (urlHost host) port

  /// Parse a SAGEFS_BIND_HOST value. Empty means the default (localhost);
  /// anything that is not a loopback form is an error that says why and what
  /// to do instead.
  let parse (raw: string) : Result<LoopbackHost, string> =
    match raw.Trim().ToLowerInvariant() with
    | "" | "localhost" -> Ok LoopbackHost.Localhost
    | "127.0.0.1" -> Ok LoopbackHost.Ipv4
    | "::1" | "[::1]" -> Ok LoopbackHost.Ipv6
    | _ ->
      Error (
        sprintf
          "SAGEFS_BIND_HOST=%s is not supported. SageFs runs F# for anyone who can reach its HTTP ports and has no authentication, so it only listens on loopback. Unset SAGEFS_BIND_HOST, or set it to localhost, 127.0.0.1 or ::1. To use SageFs inside a container, forward ports 37749 and 37750 to the container's loopback instead of binding all interfaces: VS Code Dev Containers \"forwardPorts\", `docker run --network host` (Linux), or `ssh -L 37749:localhost:37749 -L 37750:localhost:37750`."
          (raw.Trim()))

/// Bind host for HTTP servers (MCP and Dashboard).
/// SAGEFS_BIND_HOST is read here only — all other modules use this value.
/// An Error here stops the daemon at startup (Program.runDaemon) and fails
/// `sagefs check`.
let BindHost : Result<LoopbackHost, string> =
  LoopbackHost.parse (envString "SAGEFS_BIND_HOST" "")

/// Whether this process is running under external supervision (auto-restart on crash).
let IsSupervised : bool =
  envBool "SAGEFS_SUPERVISED" false

/// Worker restart count injected by the supervisor. 0 = first launch.
/// Guards against restart storms — see RestartPolicy.
let RestartCount : int =
  envInt "SAGEFS_RESTART_COUNT" 0

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

/// The MCP port the environment names, when it names one. `McpPortFromEnv` cannot
/// tell "unset" from "set to the default", and the port the Settings panel persists
/// has to be able to win over the default without losing to an env var that was
/// actually set.
let McpPortFromEnvOpt : int option =
  match Environment.GetEnvironmentVariable "SAGEFS_MCP_PORT" with
  | null | "" -> None
  | s ->
    match Int32.TryParse s with
    | true, v -> Some v
    | false, _ -> None

/// The MCP port, in the order the sources get to speak: the command line, then the
/// environment, then the port the Settings panel persisted, then the default. The
/// order is one pure function so the daemon and its clients cannot disagree about
/// which port they are talking to.
let chooseMcpPort (fromArgs: int option) (fromEnv: int option) (fromSettings: int option) : int =
  fromArgs
  |> Option.orElse fromEnv
  |> Option.orElse fromSettings
  |> Option.defaultValue DefaultMcpPort
