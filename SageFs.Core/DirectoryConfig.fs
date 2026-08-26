namespace SageFs

open System
open System.IO
open SageFs.Utils

/// Specifies how projects/solutions should be loaded for a session.
type LoadStrategy =
  /// Load a specific solution file (.sln/.slnx)
  | Solution of path: string
  /// Load specific project files (.fsproj)
  | Projects of paths: string list
  /// Auto-detect projects/solutions from the directory (default)
  | AutoDetect
  /// Bare FSI session — no project loading
  | NoLoad

/// Per-directory configuration via .SageFs/config.fsx.
/// Provides load strategy, init scripts, default args, and keybindings.
type DirectoryConfig = {
  Load: LoadStrategy
  InitScript: string option
  DefaultArgs: string list
  AutoOpenNamespaces: bool
  Keybindings: KeyMap
  ThemeOverrides: Map<string, byte>
  /// When true, treat this directory as a session root — don't walk up to git/solution root.
  /// Use for monorepos where each subdirectory is an independent project.
  IsRoot: bool
  /// Optional friendly name for auto-created sessions. Defaults to the directory name.
  SessionName: string option
}

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

module DirectoryConfig =
  let empty = {
    Load = AutoDetect
    InitScript = None
    DefaultArgs = []
    AutoOpenNamespaces = true
    Keybindings = Map.empty
    ThemeOverrides = Map.empty
    IsRoot = false
    SessionName = None
  }

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

  /// Evaluate a config.fsx file as F# code, returning a DirectoryConfig.
  /// The config file should contain a DirectoryConfig expression, e.g.:
  ///   { DirectoryConfig.empty with Load = Solution "MyApp.slnx" }
  let evaluate (content: string) : Result<DirectoryConfig, string> =
    try
      let coreAssembly = typeof<DirectoryConfig>.Assembly.Location
      let fsiConfig = FSharp.Compiler.Interactive.Shell.FsiEvaluationSession.GetDefaultConfiguration()
      let args = [| "fsi.exe"; "--noninteractive"; "--nologo"; "-r"; coreAssembly |]
      use inStream = new StreamReader(IO.Stream.Null)
      use outStream = new StringWriter()
      use errStream = new StringWriter()
      let session =
        FSharp.Compiler.Interactive.Shell.FsiEvaluationSession.Create(
          fsiConfig, args, inStream, outStream, errStream, collectible = true)
      session.EvalInteractionNonThrowing("open SageFs;;", Threading.CancellationToken.None) |> ignore
      let result, diagnostics =
        session.EvalExpressionNonThrowing(content)
      let errors =
        diagnostics
        |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
      match errors.Length > 0 with
      | true ->
        let msgs = errors |> Array.map (fun d -> d.Message) |> String.concat "; "
        Error (sprintf "Config evaluation errors: %s" msgs)
      | false ->
        match result with
        | Choice1Of2 (Some fsiValue) ->
          match fsiValue.ReflectionValue with
          | :? DirectoryConfig as cfg -> Ok cfg
          | other -> Error (sprintf "Config expression returned %s, expected DirectoryConfig" (other.GetType().Name))
        | Choice1Of2 None ->
          Error "Config expression returned no value"
        | Choice2Of2 ex ->
          Error (sprintf "Config evaluation failed: %s" ex.Message)
    with ex ->
      Error (sprintf "Config evaluation error: %s" ex.Message)

  let load (workingDir: string) : DirectoryConfig option =
    let path = configPath workingDir
    match File.Exists path with
    | true ->
      let content = File.ReadAllText path
      match evaluate content with
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
            && cfg.Keybindings = empty.Keybindings
            && cfg.ThemeOverrides = empty.ThemeOverrides
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
