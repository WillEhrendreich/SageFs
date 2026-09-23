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
  | AgentAttachFailed of AttachError

/// Every case says what happened and, where the user can act, what to do.
let describeStartError (error: IsolatedStartError) : string =
  match error with
  | SdkUnresolved reason -> describeBuildError reason
  | HostBuildFailed reason -> describeBuildError reason
  | RuntimeNotInstalled instructions -> instructions
  | HostStartFailed reason -> SageFs.FsiHostClient.describeStartError reason
  | AgentAttachFailed reason -> describeAttachError reason

/// The dotnet muxer: DOTNET_HOST_PATH, else the one next to the running runtime.
let dotnetPath () : string =
  match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
  | null
  | "" ->
    Args.muxerFromRuntimeDir
      (System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())
      (OperatingSystem.IsWindows())
  | path -> path

/// Overrides where built hosts are cached. A host is content-addressed (SDK version + exact sources + its Harmony), so
/// sharing one cache between daemons is safe; a test harness whose daemons each get a fresh data dir uses this to build the
/// host once instead of once per daemon.
[<Literal>]
let HostCacheEnvironmentVariable = "SAGEFS_HOST_CACHE_DIR"

/// Where built hosts are cached: the override, else under the SageFs data dir.
let hostCacheRootWith (getEnv: string -> string | null) (sageFsDir: string) : string =
  match getEnv HostCacheEnvironmentVariable with
  | null -> Path.Combine(sageFsDir, "hosts")
  | value when String.IsNullOrWhiteSpace value -> Path.Combine(sageFsDir, "hosts")
  | value -> value

let hostCacheRoot () : string = hostCacheRootWith Environment.GetEnvironmentVariable DaemonState.SageFsDir

/// The env var that carries the primary project's own build output directory into the isolated host process
/// (issue #142's own suggested name). `AppContext.BaseDirectory` and `Assembly.Location` inside a session
/// point at the HOST's own directory — or, for a referenced project's assembly, wherever FSI's runtime
/// loaded it from, which is not necessarily the project's build output either — never at the project's own
/// bin/<config>/<tfm>/, so any code that resolves config, fixtures or native libs relative to its own
/// assembly breaks. The host sets `AppContext.BaseDirectory` from this at startup (see FsiHost/Program.fs);
/// code that reads the env var directly gets the same answer without the AppContext indirection.
[<Literal>]
let ProjectOutputEnvironmentVariable = "SAGEFS_PROJECT_OUTPUT"

/// Pure over injected filesystem primitives: the primary project's (the first of `projects` — the one
/// explicitly requested, not a transitive reference) own build output directory — the directory holding
/// the NEWEST (by write time) `<ProjectName>.dll` found anywhere under its `bin/`, mirroring
/// `RuntimeSelection.projectRuntimeRequirement`'s identical "newest match under bin/, by write time" rule
/// so the two can never disagree about which build is "the" one.
///
/// Deliberately NOT derived from the `-r:` references `solutionToFsiArgs` builds: the worker shadow-copies
/// the whole solution to a `/tmp/sagefs-shadow-*` directory before FSI ever sees it, rewriting every `-r:`
/// path to point there — so reading a `-r:` entry would report the SAME shadow-copy temp directory #142
/// already complained about for `Assembly.Location`, not the project's real build output. Reading straight
/// from disk, independent of what FSI was told to load, is what makes this answer actually different from
/// the bug it's fixing.
let primaryProjectOutputDirWith
    (directoryExists: string -> bool)
    (matchingFiles: string -> string -> string list)
    (writeTimeUtc: string -> DateTime)
    (projects: string list)
    : string option =
  match projects with
  | [] -> None
  | primary :: _ ->
    let projectDir = Path.GetDirectoryName(primary: string)
    let binDir = Path.Combine(projectDir, "bin")
    let expectedName = Path.GetFileNameWithoutExtension(primary: string) + ".dll"
    match directoryExists binDir with
    | false -> None
    | true ->
      matchingFiles binDir expectedName
      |> List.sortByDescending writeTimeUtc
      |> List.tryHead
      |> Option.bind (Path.GetDirectoryName >> Option.ofObj)

let primaryProjectOutputDir (projects: string list) : string option =
  primaryProjectOutputDirWith
    Directory.Exists
    (fun dir pattern -> Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories) |> Seq.toList)
    File.GetLastWriteTimeUtc
    projects

/// Start an isolated FSI session for `projects`, run from `workingDir`. `recorder` receives everything the user's
/// code and FSI write to stdout (so per-eval output capture works exactly as it does in-process).
let start
  (logger: ILogger)
  (recorder: TextWriter)
  (fsiArgs: string list)
  (workingDir: string)
  (projects: string list)
  (agent: HostAgent.AgentInit)
  : Async<Result<IFsiSession, IsolatedStartError>> =
  async {
    let dotnet = dotnetPath ()
    match resolveSdk dotnet workingDir with
    | Error reason -> return Error(SdkUnresolved reason)
    | Ok sdk ->
      // Building is a blocking process run; keep it off the caller's thread.
      let! built = Async.AwaitTask(Task.Run(fun () -> ensureBuiltWith dotnet sdk (hostCacheRoot ())))
      match built with
      | Error reason -> return Error(HostBuildFailed reason)
      | Ok build ->
        let dll =
          match build with
          | Built dll -> dll
          | Reused dll -> dll
        // The host is built for its SDK's target framework, so that major is the runtime it runs on by default.
        let hostMajor = int (sdk.Version.Split('.').[0])
        match RuntimeSelection.resolveRuntimeChoiceFor hostMajor projects with
        | RuntimeCompat.RuntimeMissing _ as choice -> return Error(RuntimeNotInstalled(RuntimeCompat.describe choice))
        | choice ->
          match choice with
          | RuntimeCompat.RollForward _ -> logger.LogInfo(sprintf "  Isolated FSI host: %s" (RuntimeCompat.describe choice))
          | _ -> ()
          // #142: give the project's own code a way back to its real build output directory — the host
          // process sets AppContext.BaseDirectory from this at startup (see FsiHost/Program.fs).
          let projectOutputEnv =
            match primaryProjectOutputDir projects with
            | Some dir -> [ ProjectOutputEnvironmentVariable, dir ]
            | None -> []
          let options =
            { HostDll = dll
              Dotnet = dotnet
              FsiArgs = fsiArgs
              WorkingDir = workingDir
              Environment = projectOutputEnv @ RuntimeCompat.rollForwardEnv choice @ Middleware.ValueReadTracking.processEnvironment agent.ValueReads
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
            match! attach host agent with
            | Result.Ok session -> return Ok(session :> IFsiSession)
            | Result.Error reason -> return Error(AgentAttachFailed reason)
          | Error reason -> return Error(HostStartFailed reason)
  }
