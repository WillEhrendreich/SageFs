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

/// Start a host (no project), evaluate `content`, dispose. The SDK lookup and the host build are synchronous process
/// work, so they run on the pool and are awaited; the host's own startup and evaluation are awaited as they are.
let private evaluateUncachedAsync (workingDir: string) (content: string) : Async<Result<DirectoryConfig, ConfigHostError>> =
  async {
    let dotnet = IsolatedFsiSession.dotnetPath ()
    let! built =
      Threading.Tasks.Task.Run(fun () ->
        resolveSdk dotnet workingDir |> Result.bind (fun sdk -> ensureBuiltWith dotnet sdk (IsolatedFsiSession.hostCacheRoot ())))
      |> Async.AwaitTask
    match built with
    | Error reason -> return Error(HostUnavailable(describeBuildError reason))
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
          Libraries = HostAdaptation.HostLibraries.AsBuilt
          OnOutput = fun _ _ -> ()
          OnLog = ignore
          StartupTimeoutMs = int Timeouts.fsiHostStartup.TotalMilliseconds }
      match! start options with
      | Error reason -> return Error(HostUnavailable(describeStartError reason))
      | Ok host ->
        use _ = host :> IDisposable
        match! host.EvalConfig content with
        | Answered(ConfigEvaluated config) -> return Ok config
        | Answered(ConfigRejected failure) -> return Error(ScriptRejected failure)
        | HostGone reason -> return Error(HostLostWhileEvaluating reason)
  }

/// Evaluate a config.fsx expression. Script outcomes are cached by text; infrastructure failures are not.
let evaluateAsync (workingDir: string) (content: string) : Async<Result<DirectoryConfig, ConfigHostError>> =
  async {
    match cache.TryGetValue content with
    | true, cached -> return cached
    | false, _ ->
      let! result = evaluateUncachedAsync workingDir content
      match result with
      | Ok _
      | Error(ScriptRejected _) -> cache[content] <- result
      | Error(HostUnavailable _)
      | Error(HostLostWhileEvaluating _) -> ()
      return result
  }

/// `evaluateAsync` for the callers that are synchronous (`DirectoryConfig.load` and the project-resolution code
/// above it, and the tests that pin them). A cached answer returns without waiting at all, and `evaluateAsync` is
/// what a caller that can await uses, so this is the one place config loading blocks.
let evaluate (workingDir: string) (content: string) : Result<DirectoryConfig, ConfigHostError> =
  evaluateAsync workingDir content |> Async.RunSynchronously
