/// The one rule for which port the daemon binds, and which port the clients that have to find it ask for.
///
/// Four sources, in order: `--mcp-port` on the command line, `SAGEFS_MCP_PORT`, the port the Settings
/// panel persisted, then the default. It lives here rather than in the CLI because two other places have
/// to answer the same question and must not answer it differently: the watchdog picks a jitter seed from
/// the port it thinks the supervised daemon has, and the stdio bridge decides from it whether the daemon
/// it spawns is everybody's daemon or one of its own. Two of them resolving it differently is a client
/// that cannot find the daemon it is talking to.
namespace SageFs

open System

module DaemonPort =

  /// The port the command line names, when it names one. A `--mcp-port` whose value is not a number
  /// names nothing, so the configured port keeps its say rather than the flag failing silently.
  let fromFlag (args: string list) : int option =
    let rec find (rest: string list) =
      match rest with
      | "--mcp-port" :: value :: _ ->
        match Int32.TryParse value with
        | true, port -> Some port
        | _ -> None
      | _ :: tail -> find tail
      | [] -> None
    find args

  /// The port a run that names no `--mcp-port` uses: the environment, then the port the Settings panel
  /// persisted, then the default. This is the port the user's own daemon is on, whatever set it there,
  /// and it is what "home port" means everywhere below.
  let configured () : int =
    SageFsConfig.chooseMcpPort
      None
      SageFsConfig.McpPortFromEnvOpt
      (SettingsCatalog.mcpPortFromLayers DaemonState.SageFsDir)

  /// The port this invocation binds: the command line over the configured port. The environment and the
  /// panel are already inside `configured`, so there is nothing else to pass. The configured port is a
  /// parameter rather than a read, so the rule can be driven without the machine's settings file.
  let resolveWith (configuredPort: int) (args: string list) : int =
    SageFsConfig.chooseMcpPort (fromFlag args) None (Some configuredPort)

  /// `resolveWith`, with the configured port read for you.
  let resolve (args: string list) : int = resolveWith (configured ()) args

  /// Whether the command line asks for a port that is NOT the configured one. This is the whole
  /// distinction the ownership gate and the bridge draw: a plain `sagefs`, and `sagefs --mcp-port` naming
  /// exactly what is configured, both get the daemon everyone shares; only the other case is a daemon of
  /// somebody's own, which has to say who owns it or when it gives up. Stated against the resolved port
  /// so there is one answer to "is this mine", not a second reading of the flag.
  let hasOwnPortWith (configuredPort: int) (args: string list) : bool =
    resolveWith configuredPort args <> configuredPort

  /// `hasOwnPortWith`, with the configured port read for you.
  let hasOwnPort (args: string list) : bool = hasOwnPortWith (configured ()) args
