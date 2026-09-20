/// Starting an FSI session in an isolated host process, for the worker: build (or reuse) the host with the
/// project's own SDK, decide which runtime the host should run on, start it, and wrap it as an IFsiSession.
///
/// The worker itself stays on its own runtime; only the host — the process the user's code actually runs in —
/// is launched on the runtime the project needs, and it shares no assembly with SageFs.
module SageFs.IsolatedFsiSession

open System
open System.IO
open System.Threading.Tasks
open SageFs.FsiHostBuild
open SageFs.FsiHostClient
open SageFs.FsiSession
open SageFs.RemoteFsiSession
open SageFs.Utils

/// Why an isolated session could not be started.
type IsolatedStartError =
  | SdkUnresolved of HostBuildError
  | HostBuildFailed of HostBuildError
  | RuntimeNotInstalled of instructions: string
  | HostStartFailed of StartError

/// Every case says what happened and, where the user can act, what to do.
let describeStartError (error: IsolatedStartError) : string =
  match error with
  | SdkUnresolved reason -> describeBuildError reason
  | HostBuildFailed reason -> describeBuildError reason
  | RuntimeNotInstalled instructions -> instructions
  | HostStartFailed reason -> SageFs.FsiHostClient.describeStartError reason

/// The dotnet muxer: DOTNET_HOST_PATH, else the one next to the running runtime.
let dotnetPath () : string =
  match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
  | null
  | "" ->
    Args.muxerFromRuntimeDir
      (System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())
      (OperatingSystem.IsWindows())
  | path -> path

/// Where built hosts are cached (under the SageFs data dir, so tests with an isolated data dir stay isolated).
let hostCacheRoot () : string = Path.Combine(DaemonState.SageFsDir, "hosts")

/// Start an isolated FSI session for `projects`, run from `workingDir`. `recorder` receives everything the user's
/// code and FSI write to stdout (so per-eval output capture works exactly as it does in-process).
let start
  (logger: ILogger)
  (recorder: TextWriter)
  (fsiArgs: string list)
  (workingDir: string)
  (projects: string list)
  : Async<Result<IFsiSession, IsolatedStartError>> =
  async {
    let dotnet = dotnetPath ()
    match resolveSdkVersion dotnet workingDir with
    | Error reason -> return Error(SdkUnresolved reason)
    | Ok sdkVersion ->
      // Building is a blocking process run; keep it off the caller's thread.
      let! built = Async.AwaitTask(Task.Run(fun () -> ensureBuilt dotnet sdkVersion (hostCacheRoot ())))
      match built with
      | Error reason -> return Error(HostBuildFailed reason)
      | Ok build ->
        let dll =
          match build with
          | Built dll -> dll
          | Reused dll -> dll
        // The host is built for its SDK's target framework, so that major is the runtime it runs on by default.
        let hostMajor = int (sdkVersion.Split('.').[0])
        match RuntimeSelection.resolveRuntimeChoiceFor hostMajor projects with
        | RuntimeCompat.RuntimeMissing _ as choice -> return Error(RuntimeNotInstalled(RuntimeCompat.describe choice))
        | choice ->
          match choice with
          | RuntimeCompat.RollForward _ -> logger.LogInfo(sprintf "  Isolated FSI host: %s" (RuntimeCompat.describe choice))
          | _ -> ()
          let options =
            { HostDll = dll
              Dotnet = dotnet
              FsiArgs = fsiArgs
              WorkingDir = workingDir
              Environment = RuntimeCompat.rollForwardEnv choice
              OnOutput =
                fun stream text ->
                  match stream with
                  | FsiHost.FsiProtocol.StdOut -> recorder.Write text
                  | FsiHost.FsiProtocol.StdErr -> logger.LogDebug(sprintf "[fsihost stderr] %s" (text.TrimEnd()))
              OnLog = fun line -> logger.LogDebug(sprintf "[fsihost] %s" line)
              StartupTimeoutMs = 120_000 }
          match! start options with
          | Ok host ->
            logger.LogInfo(sprintf "  Isolated FSI host started: %s, FSharp.Core %s (pid %d)" host.Runtime host.FSharpCoreVersion host.ProcessId)
            return Ok(new RemoteFsiSession(host) :> IFsiSession)
          | Error reason -> return Error(HostStartFailed reason)
  }
