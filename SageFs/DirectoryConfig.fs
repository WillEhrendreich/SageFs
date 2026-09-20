namespace SageFs

open System
open System.IO
open SageFs.Utils

[<RequireQualifiedAccess>]
type AutoOpenNamespacesOptOutResult =
  | Created of path: string
  | AlreadyDisabled of path: string
  | RequiresManualEdit of path: string

[<RequireQualifiedAccess>]
type AutoOpenNamespacesOptInResult =
  | Enabled of path: string
  | AlreadyEnabled
  | RequiresManualEdit of path: string

/// File operations for .SageFs/config.fsx. The config TYPES live in SageFs.Core/DirectoryConfigTypes.fs (they are
/// also compiled into the isolated FSI host, where a user's config.fsx is actually evaluated).
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DirectoryConfig =
  let empty = DirectoryConfigDefaults.empty

  let configDir (workingDir: string) =
    Path.Combine(workingDir, ".SageFs")

  let configPath (workingDir: string) =
    Path.Combine(configDir workingDir, "config.fsx")

  /// Must be a SINGLE-LINE expression: the config is evaluated by an FSI
  /// session via EvalExpressionNonThrowing, and a multi-line record update
  /// starting at column 1 is an offside error ("this token is offside of
  /// context started at position (1:3)"). A single line avoids that entirely.
  let autoOpenNamespacesOptOutTemplate =
    "{ DirectoryConfig.empty with AutoOpenNamespaces = false }"

  /// Template for re-enabling — the plain default config. Note: this must NOT
  /// be wrapped in braces (`{ DirectoryConfig.empty }` parses as a computation
  /// expression, not a record — "Invalid record, sequence or computation
  /// expression"). The bare expression is the default value itself.
  let autoOpenNamespacesOptInTemplate =
    "DirectoryConfig.empty"

  /// Evaluate a config.fsx expression as F# code, returning a DirectoryConfig. The script is arbitrary user code,
  /// so it runs in an isolated FSI host (SageFs.ConfigHost), never in this process; `workingDir` picks the SDK.
  /// The config file should contain a DirectoryConfig expression, e.g.:
  ///   { DirectoryConfig.empty with Load = Solution "MyApp.slnx" }
  let evaluateIn (workingDir: string) (content: string) : Result<DirectoryConfig, string> =
    ConfigHost.evaluate workingDir content |> Result.mapError ConfigHost.describeError

  /// `evaluateIn` for the current directory.
  let evaluate (content: string) : Result<DirectoryConfig, string> =
    evaluateIn Environment.CurrentDirectory content

  let load (workingDir: string) : DirectoryConfig option =
    let path = configPath workingDir
    match File.Exists path with
    | true ->
      let content = File.ReadAllText path
      match evaluateIn workingDir content with
      | Ok cfg -> Some cfg
      | Error msg ->
        Log.warn "Failed to load %s: %s (using defaults)" path msg
        Some empty
    | false ->
      None

  let autoOpenNamespacesForDirectory (workingDir: string) =
    load workingDir
    |> Option.map (fun cfg -> cfg.AutoOpenNamespaces)
    |> Option.defaultValue true

  let ensureAutoOpenNamespacesOptOut (workingDir: string) =
    try
      let path = configPath workingDir
      match File.Exists path with
      | false ->
        Directory.CreateDirectory(configDir workingDir) |> ignore
        File.WriteAllText(path, autoOpenNamespacesOptOutTemplate)
        Ok (AutoOpenNamespacesOptOutResult.Created path)
      | true ->
        match load workingDir with
        | Some cfg when not cfg.AutoOpenNamespaces ->
          Ok (AutoOpenNamespacesOptOutResult.AlreadyDisabled path)
        | Some _ ->
          Ok (AutoOpenNamespacesOptOutResult.RequiresManualEdit path)
        | None ->
          Directory.CreateDirectory(configDir workingDir) |> ignore
          File.WriteAllText(path, autoOpenNamespacesOptOutTemplate)
          Ok (AutoOpenNamespacesOptOutResult.Created path)
    with ex ->
      Error (sprintf "Failed to configure %s: %s" (configPath workingDir) ex.Message)

  /// Re-enable warmup auto-open for a directory. If the existing config is a
  /// plain opt-out (only AutoOpenNamespaces = false), rewrite it to the
  /// default; otherwise ask the user to edit manually (their config may have
  /// other customizations we must not clobber).
  let ensureAutoOpenNamespacesOptIn (workingDir: string) =
    try
      let path = configPath workingDir
      match File.Exists path with
      | false ->
        // No config file means the default (auto-open enabled) already applies.
        Ok (AutoOpenNamespacesOptInResult.AlreadyEnabled)
      | true ->
        match load workingDir with
        | Some cfg when cfg.AutoOpenNamespaces ->
          Ok (AutoOpenNamespacesOptInResult.AlreadyEnabled)
        | Some cfg ->
          // Only rewrite when the config is exactly the opt-out template
          // (no other customizations). Detect via structural equality with
          // the default except for AutoOpenNamespaces.
          let isBareOptOut =
            cfg.Load = empty.Load
            && cfg.InitScript = empty.InitScript
            && cfg.DefaultArgs = empty.DefaultArgs
            && cfg.IsRoot = empty.IsRoot
            && cfg.SessionName = empty.SessionName
          match isBareOptOut with
          | true ->
            File.WriteAllText(path, autoOpenNamespacesOptInTemplate)
            Ok (AutoOpenNamespacesOptInResult.Enabled path)
          | false ->
            Ok (AutoOpenNamespacesOptInResult.RequiresManualEdit path)
        | None ->
          Ok (AutoOpenNamespacesOptInResult.AlreadyEnabled)
    with ex ->
      Error (sprintf "Failed to configure %s: %s" (configPath workingDir) ex.Message)
