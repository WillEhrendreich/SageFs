/// Evaluating a directory's `.SageFs/config.fsx` — arbitrary user F# — in an isolated FSI host instead of the
/// daemon. A short-lived host is started, asked to evaluate the expression, and disposed; the DirectoryConfig comes
/// back over the wire protocol as a typed value, so the daemon never runs the user's script.
///
/// Evaluation is deterministic in the script text, so results (values and script errors, never infrastructure
/// failures) are cached by that text: a dashboard that asks about the same config repeatedly starts one host.
module SageFs.ConfigHost

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open SageFs.FsiHost.FsiProtocol
open SageFs.FsiHostBuild
open SageFs.FsiHostClient

/// Why a config could not be evaluated. Infrastructure failures and the script's own problems are different
/// cases: only the second is the user's to fix.
type ConfigHostError =
  | HostUnavailable of reason: string
  | HostLostWhileEvaluating of reason: string
  | ScriptRejected of failure: ConfigFailure

let describeError (error: ConfigHostError) : string =
  match error with
  | HostUnavailable reason -> sprintf "Could not start the FSI host to evaluate the config: %s" reason
  | HostLostWhileEvaluating reason -> sprintf "The FSI host stopped while evaluating the config: %s" reason
  | ScriptRejected(ConfigDoesNotCompile diagnostics) ->
    sprintf "Config evaluation errors: %s" (diagnostics |> List.map (fun d -> d.Message) |> String.concat "; ")
  | ScriptRejected(ConfigWrongType typeName) -> sprintf "Config expression returned %s, expected DirectoryConfig" typeName
  | ScriptRejected ConfigNoValue -> "Config expression returned no value"
  | ScriptRejected(ConfigThrew message) -> sprintf "Config evaluation failed: %s" message

let private cache = ConcurrentDictionary<string, Result<DirectoryConfig, ConfigHostError>>()

/// Start a host (no project), evaluate `content`, dispose. Blocking: config loading is a synchronous seam.
let private evaluateUncached (workingDir: string) (content: string) : Result<DirectoryConfig, ConfigHostError> =
  let dotnet = IsolatedFsiSession.dotnetPath ()
  match resolveSdk dotnet workingDir |> Result.bind (fun sdk -> ensureBuiltWith dotnet sdk (IsolatedFsiSession.hostCacheRoot ())) with
  | Error reason -> Error(HostUnavailable(describeBuildError reason))
  | Ok build ->
    let dll =
      match build with
      | Built dll
      | Reused dll -> dll
    let options =
      { HostDll = dll
        Dotnet = dotnet
        // The host references its own assembly, which carries DirectoryConfig and LoadStrategy for the script.
        FsiArgs = [ "fsi"; "--noninteractive"; "--nologo"; "--readline-"; "-r:" + dll ]
        WorkingDir = workingDir
        Environment = []
        OnOutput = fun _ _ -> ()
        OnLog = ignore
        StartupTimeoutMs = 120_000 }
    match Async.RunSynchronously(start options) with
    | Error reason -> Error(HostUnavailable(describeStartError reason))
    | Ok host ->
      use _ = host :> IDisposable
      match Async.RunSynchronously(host.EvalConfig content) with
      | Answered(ConfigEvaluated config) -> Ok config
      | Answered(ConfigRejected failure) -> Error(ScriptRejected failure)
      | HostGone reason -> Error(HostLostWhileEvaluating reason)

/// Evaluate a config.fsx expression. Script outcomes are cached by text; infrastructure failures are not.
let evaluate (workingDir: string) (content: string) : Result<DirectoryConfig, ConfigHostError> =
  match cache.TryGetValue content with
  | true, cached -> cached
  | false, _ ->
    let result = evaluateUncached workingDir content
    match result with
    | Ok _
    | Error(ScriptRejected _) -> cache[content] <- result
    | Error(HostUnavailable _)
    | Error(HostLostWhileEvaluating _) -> ()
    result
