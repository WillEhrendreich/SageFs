namespace SageFs

open System

/// The catalog glue (unified-settings-design.md Phase A4): it ties a
/// `SettingDescriptor` to the layer store, so one call resolves a setting's
/// effective value across layers, and one call edits it — parse → persist to
/// the chosen layer → apply-if-live → re-resolve. The only place a raw string
/// becomes a typed `SettingValue` is the descriptor's `Parse`, which returns
/// *why* on failure; everything past it is legal by construction.
///
/// This module also holds the Phase-A pilot descriptors: test timeouts
/// (Live — wires the previously-dead `setTestTimeouts` path), MCP port
/// (RestartRequired), and bind host (Guarded — the loopback RCE guard is the
/// type, `LoopbackHost`, not a runtime check). Theme and auto-open are
/// existing-subsystem migrations and belong to Phase C.

/// Where the file layers live. `RepoRoot` is `None` for a session with no
/// git checkout (only the global layer applies).
type ConfigPaths = {
  GlobalDir: string
  RepoRoot: string option
}

[<RequireQualifiedAccess>]
module SettingsCatalog =

  /// The raw value present at a file layer for this key, if any.
  let private rawAt (path: string) (key: string) : string option =
    SettingsStore.readLayer path |> Map.tryFind key

  /// Resolve one descriptor's effective value + provenance across the file
  /// layers, plus an optional in-memory session override (the highest layer).
  /// A parse failure at any present layer is surfaced (with why) rather than
  /// silently dropped — a corrupt persisted value must not resolve to a
  /// wrong-but-plausible one.
  let resolve
    (paths: ConfigPaths)
    (sessionOverride: SettingValue option)
    (descriptor: SettingDescriptor)
    : Result<Provenance, string> =
    let parseAt (layer: ConfigLayer) (raw: string option) : Result<(ConfigLayer * SettingValue) option, string> =
      match raw with
      | None -> Ok None
      | Some r ->
        match descriptor.Parse r with
        | Ok v -> Ok (Some(layer, v))
        | Error why -> Error (sprintf "%s (%A layer): %s" descriptor.Key layer why)

    let globalRaw = rawAt (SettingsStore.globalPath paths.GlobalDir) descriptor.Key
    let repoRaw =
      match paths.RepoRoot with
      | Some root -> rawAt (SettingsStore.repoPath root) descriptor.Key
      | None -> None

    match parseAt LGlobal globalRaw, parseAt LRepo repoRaw with
    | Error why, _ -> Error why
    | _, Error why -> Error why
    | Ok g, Ok r ->
      let set =
        [ yield! Option.toList g
          yield! Option.toList r
          match sessionOverride with
          | Some v -> yield (LSession, v)
          | None -> () ]
      Ok (SettingsResolver.resolve descriptor.Default set)

  /// Edit a setting at a persisted layer: parse the raw input (the sole
  /// validation), persist the rendered value to that layer's file, apply it
  /// live when the descriptor is `Live`, and return the freshly re-resolved
  /// provenance. `LSession`/`LDefault` are not persisted layers — edits target
  /// `LGlobal` or `LRepo`.
  let edit
    (paths: ConfigPaths)
    (layer: ConfigLayer)
    (raw: string)
    (descriptor: SettingDescriptor)
    : Result<Provenance, string> =
    match descriptor.Parse raw with
    | Error why -> Error why
    | Ok value ->
      let persisted =
        match layer with
        | LGlobal -> SettingsStore.setKey (SettingsStore.globalPath paths.GlobalDir) descriptor.Key (descriptor.Render value)
        | LRepo ->
          match paths.RepoRoot with
          | Some root -> SettingsStore.setKey (SettingsStore.repoPath root) descriptor.Key (descriptor.Render value)
          | None -> Error "this session has no repo checkout, so there is no repo layer to write to"
        | LDefault | LSession -> Error "edits persist to the global or repo layer, not default/session"
      match persisted with
      | Error e -> Error e
      | Ok () ->
        match descriptor.Applicability with
        | Live -> descriptor.Apply value
        | RestartRequired | Guarded -> ()
        resolve paths None descriptor

  /// Clear a persisted override at a layer so the value falls back to the
  /// next-lower layer, then re-resolve.
  let clear
    (paths: ConfigPaths)
    (layer: ConfigLayer)
    (descriptor: SettingDescriptor)
    : Result<Provenance, string> =
    let cleared =
      match layer with
      | LGlobal -> SettingsStore.clearKey (SettingsStore.globalPath paths.GlobalDir) descriptor.Key
      | LRepo ->
        match paths.RepoRoot with
        | Some root -> SettingsStore.clearKey (SettingsStore.repoPath root) descriptor.Key
        | None -> Ok ()
      | LDefault | LSession -> Error "only the global or repo layer can be cleared"
    match cleared with
    | Error e -> Error e
    | Ok () -> resolve paths None descriptor

  // ---------------------------------------------------------------------------
  // Phase-A pilot descriptors
  // ---------------------------------------------------------------------------

  let private secondsToTimeoutValue (raw: string) : Result<SettingValue, string> =
    match Double.TryParse raw with
    | false, _ -> Error (sprintf "expected a number of seconds, got '%s'" raw)
    | true, s ->
      match ValidTimeout.create (TimeSpan.FromSeconds s) with
      | Ok t -> Ok (VTimeout t)
      | Error why -> Error why

  let private renderTimeout (v: SettingValue) : string =
    match v with
    | VTimeout t -> (ValidTimeout.value t).TotalSeconds |> sprintf "%g"
    | _ -> ""

  /// Default per-test timeout as a legal value (5s is within ValidTimeout's
  /// 1s-10min range, so this never falls through).
  let private defaultTimeout (seconds: float) : SettingValue =
    match ValidTimeout.create (TimeSpan.FromSeconds seconds) with
    | Ok t -> VTimeout t
    | Error _ -> VTimeout (match ValidTimeout.create (TimeSpan.FromSeconds 5.0) with Ok t -> t | Error _ -> failwith "5s must be valid")

  /// Live: the per-test timeout. Wires the previously-dead setTestTimeouts path
  /// — a validated runtime setter that nothing could reach — to a real edit.
  let perTestTimeout : SettingDescriptor = {
    Key = "livetest.perTestTimeoutSeconds"
    Description = "Per-test timeout for live testing (seconds, 1-600)."
    Scope = RepoOverridable
    Applicability = Live
    Default = defaultTimeout 5.0
    Parse = secondsToTimeoutValue
    Render = renderTimeout
    Apply = fun v -> match v with VTimeout t -> Timeouts.setPerTestTimeout (ValidTimeout.value t) | _ -> ()
  }

  /// Live: the whole-run timeout for a live-test cycle.
  let globalTestRunTimeout : SettingDescriptor = {
    Key = "livetest.globalTestRunTimeoutSeconds"
    Description = "Total timeout for one live-test run (seconds, 1-600)."
    Scope = RepoOverridable
    Applicability = Live
    Default = defaultTimeout 120.0
    Parse = secondsToTimeoutValue
    Render = renderTimeout
    Apply = fun v -> match v with VTimeout t -> Timeouts.setGlobalTestRunTimeout (ValidTimeout.value t) | _ -> ()
  }

  /// RestartRequired: the MCP server port. Typed as a bounded Port, so an
  /// out-of-range value cannot be persisted; applied on the next daemon start.
  let mcpPort : SettingDescriptor = {
    Key = "daemon.mcpPort"
    Description = "MCP server port (1024-65535). Applies on the next daemon restart."
    Scope = Global
    Applicability = RestartRequired
    Default = VPort (match Port.create SageFsConfig.DefaultMcpPort with Ok p -> p | Error _ -> failwith "default MCP port must be valid")
    Parse = fun raw ->
      match Int32.TryParse raw with
      | false, _ -> Error (sprintf "expected a port number, got '%s'" raw)
      | true, n -> Port.create n |> Result.map VPort
    Render = fun v -> match v with VPort p -> string (Port.value p) | _ -> ""
    Apply = ignore
  }

  /// Guarded: the HTTP bind host. Typed as LoopbackHost, whose parse refuses
  /// any non-loopback value — so a LAN/all-interfaces bind (remote code
  /// execution, since SageFs evals F# with no auth) is structurally
  /// unrepresentable, not merely rejected at runtime.
  let bindHost : SettingDescriptor = {
    Key = "daemon.bindHost"
    Description = "Loopback bind address for the HTTP servers (localhost, 127.0.0.1, ::1). Non-loopback is refused."
    Scope = Global
    Applicability = Guarded
    Default = VBindHost SageFsConfig.LoopbackHost.Localhost
    Parse = fun raw -> SageFsConfig.LoopbackHost.parse raw |> Result.map VBindHost
    Render = fun v -> match v with VBindHost h -> SageFsConfig.LoopbackHost.urlHost h | _ -> ""
    Apply = ignore
  }

  /// The Phase-A pilot catalog.
  let pilots : SettingDescriptor list =
    [ perTestTimeout; globalTestRunTimeout; mcpPort; bindHost ]
