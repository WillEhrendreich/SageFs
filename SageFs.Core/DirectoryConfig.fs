namespace SageFs

open System
open System.IO

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
  Keybindings: KeyMap
  ThemeOverrides: Map<string, byte>
  /// When true, treat this directory as a session root — don't walk up to git/solution root.
  /// Use for monorepos where each subdirectory is an independent project.
  IsRoot: bool
  /// Optional friendly name for auto-created sessions. Defaults to the directory name.
  SessionName: string option
}

module DirectoryConfig =
  let empty = {
    Load = AutoDetect
    InitScript = None
    DefaultArgs = []
    Keybindings = Map.empty
    ThemeOverrides = Map.empty
    IsRoot = false
    SessionName = None
  }

  let configDir (workingDir: string) =
    Path.Combine(workingDir, ".SageFs")

  let configPath (workingDir: string) =
    Path.Combine(configDir workingDir, "config.fsx")

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
      if errors.Length > 0 then
        let msgs = errors |> Array.map (fun d -> d.Message) |> String.concat "; "
        Error (sprintf "Config evaluation errors: %s" msgs)
      else
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
    if File.Exists path then
      let content = File.ReadAllText path
      match evaluate content with
      | Ok cfg -> Some cfg
      | Error msg ->
        eprintfn "Warning: failed to load %s: %s (using defaults)" path msg
        Some empty
    else
      None
