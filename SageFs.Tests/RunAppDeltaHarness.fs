/// A real host, an app started the way a user starts one (`run_app`), a real save.
///
/// HotReloadStateHarness starts its app by evaluating `App.run port` in the FSI host, which is where the
/// reload agent lives. An app started with `run_app` is a different thing: the worker runs the project's
/// compiled entry point itself, on a thread of its own process. This harness starts THAT, on the host the
/// daemon ships, with a project of its own (RunAppDeltaFixture), so a row can ask what a save does to a
/// process that `run_app` started.
///
/// It uses the shared harness's types and its save, verdict and confirmation reads, and it keeps its own
/// copy of the scratch-project steps that module keeps private: that file is under change for the
/// detour rows, and a second copy here is cheaper than a merge conflict. Fold them back once it is quiet.
module SageFs.Tests.RunAppDeltaHarness

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading.Tasks
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Tests.HotReloadStateHarness

/// The executable the rows run: Handlers.fs holds what a row edits, Program.fs serves it.
let runAppFixture : Fixture =
  { Folder = "RunAppDeltaFixture"
    Sources = [ "Handlers.fs"; "Program.fs" ]
    Project = "RunAppDeltaFixture"
    ReadyRoute = "ready" }

let private repoRoot () =
  RepoPaths.repoPathFull [||]

let private fixtureSourceDir (fixture: Fixture) =
  Path.Combine(repoRoot (), "SageFs.Tests", "fixtures", fixture.Folder)

/// The scratch project: an executable, so `run_app` has an entry point to run.
let private executableProject (fixture: Fixture) (runtime: HostRuntime) =
  String.concat "\n" [
    "<Project Sdk=\"Microsoft.NET.Sdk.Web\">"
    "  <PropertyGroup>"
    sprintf "    <TargetFramework>%s</TargetFramework>" (HostRuntime.moniker runtime)
    "    <OutputType>Exe</OutputType>"
    "    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>"
    "    <RestoreLockedMode>false</RestoreLockedMode>"
    "  </PropertyGroup>"
    "  <ItemGroup>"
    yield! fixture.Sources |> List.map (sprintf "    <Compile Include=\"%s\" />")
    "  </ItemGroup>"
    "</Project>"
    "" ]

let private copyFixture (fixture: Fixture) (runtime: HostRuntime) =
  let runDir =
    Path.Combine(fixtureSourceDir fixture, ".runs", sprintf "%s-%s" (HostRuntime.moniker runtime) (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory runDir |> ignore
  match sdkPin runtime with
  | Some sdk ->
    File.WriteAllText(
      Path.Combine(runDir, "global.json"),
      sprintf """{"sdk":{"version":"%s","rollForward":"latestPatch","allowPrerelease":false}}""" sdk)
  | None -> ()
  for source in fixture.Sources do
    File.Copy(Path.Combine(fixtureSourceDir fixture, source), Path.Combine(runDir, source))
  let project = Path.Combine(runDir, fixture.Project + ".fsproj")
  File.WriteAllText(project, executableProject fixture runtime)
  // The copies must be OLDER than the build, or the host decides the source was edited after the build
  // and re-evaluates it whole instead of diffing it against what was built.
  let past = DateTime.UtcNow.AddMinutes -5.0
  for source in fixture.Sources do
    File.SetLastWriteTimeUtc(Path.Combine(runDir, source), past)
  runDir, project

let private spawnHost (deltaMode: SageFs.Features.MetadataDelta.MetadataDeltaMode) (runtime: HostRuntime) (runDir: string) (project: string) (hostLog: StringBuilder) = task {
  let sessionId = sprintf "runapp-%s" (Guid.NewGuid().ToString("N"))
  let args, envVars =
    Args.buildWorkerSpawnConfigWith deltaMode sessionId [ SageFs.SessionProjectTarget.Project project ] false true
      (WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults)
  let psi = ProcessStartInfo(hostExePath runtime, args)
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  psi.WorkingDirectory <- runDir
  for k, v in envVars do
    match k = Args.WorkerConfig.envVar with
    | true -> psi.EnvironmentVariables[k] <- project
    | false -> psi.EnvironmentVariables[k] <- v
  let proc = Process.Start psi
  let port = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
  let drain (reader: StreamReader) =
    Task.Run(fun () ->
      task {
        let mutable line = ""
        let! first = reader.ReadLineAsync()
        line <- first
        while not (isNull line) do
          lock hostLog (fun () -> hostLog.AppendLine line |> ignore)
          match line.StartsWith("WORKER_PORT=", StringComparison.Ordinal) with
          | true -> port.TrySetResult(line.Substring("WORKER_PORT=".Length)) |> ignore
          | false -> ()
          let! next = reader.ReadLineAsync()
          line <- next
      } :> Task)
    |> ignore
  drain proc.StandardOutput
  drain proc.StandardError
  let! winner = Task.WhenAny(port.Task, Task.Delay(TestTimeouts.workerPortReport))
  match obj.ReferenceEquals(winner, port.Task) with
  | false ->
    try proc.Kill(entireProcessTree = true) with _ -> ()
    return failwithf "the %s host never printed WORKER_PORT. Host log:\n%s" (HostRuntime.moniker runtime) (lock hostLog (fun () -> hostLog.ToString()))
  | true -> return proc, port.Task.Result.TrimEnd('/')
}

/// The worker says when the session is Ready, and the only way for a test outside the process to hear it
/// is to ask, with a deadline.
let private waitReady (proxy: SessionProxy) (hostLog: StringBuilder) = task {
  let sw = Stopwatch.StartNew()
  let mutable ready = false
  while not ready && sw.Elapsed < TestTimeouts.workerSessionReady do
    let! status = task {
      try
        match! proxy (WorkerMessage.GetStatus(Guid.NewGuid().ToString("N"))) |> Async.StartAsTask with
        | WorkerResponse.StatusResult(_, s) -> return s.Status = SessionStatus.Ready
        | _ -> return false
      with _ -> return false }
    ready <- status
    match ready with
    | true -> ()
    | false -> do! Task.Delay TestTimeouts.pollService
  match ready with
  | true -> ()
  | false -> failwithf "the session never reached Ready.\n%s" (lock hostLog (fun () -> hostLog.ToString()))
}

/// Start the app with `run_app` and say where it listens.
let private runApp (proxy: SessionProxy) (project: string) : Task<int> = task {
  match! proxy (WorkerMessage.RunApp(project, AppRun.PreviousAddress.NoPreviousAddress, Guid.NewGuid().ToString("N"))) |> Async.StartAsTask with
  | WorkerResponse.AppRunResult(_, Ok (AppRun.AppRunState.Running app)) ->
    match app.Endpoint with
    | AppRun.AppEndpoint.Http (url, _) -> return Uri(url).Port
    | AppRun.AppEndpoint.NoServer -> return failwithf "run_app started the fixture and it listens nowhere: %A" app
  | other -> return failwithf "run_app did not start the fixture: %A" other
}

/// Spin the whole thing up: scratch copy, SageFs build, host, `run_app`, the app answering. The worker
/// watches the project's sources for hot reload as soon as the app is running.
/// How long the parts of a start took.
type StartTimes =
  { /// `dotnet build` of the scratch project, which every route pays.
    BuildMs: float
    /// From starting the host to the app answering: a new process, its session, the project loaded, the app started.
    /// This is what a restart pays on top of the build.
    ProcessMs: float }

let startRunAppBuilt (mode: BuildMode) (deltaMode: SageFs.Features.MetadataDelta.MetadataDeltaMode) (runtime: HostRuntime) : Task<RunningApp * StartTimes> = task {
  let runDir, project = copyFixture runAppFixture runtime
  let watch = Stopwatch.StartNew()
  do! buildFixture mode runAppFixture runtime runDir project
  let buildMs = watch.Elapsed.TotalMilliseconds
  watch.Restart()
  let hostLog = StringBuilder()
  let! proc, workerUrl = spawnHost deltaMode runtime runDir project hostLog
  let proxy = HttpWorkerClient.httpProxy workerUrl
  do! waitReady proxy hostLog
  let! appPort = runApp proxy project
  let app =
    { Runtime = runtime
      RunDir = runDir
      StateSource = Path.Combine(runDir, List.head runAppFixture.Sources)
      Host = proc
      WorkerUrl = workerUrl
      Proxy = proxy
      AppPort = appPort
      HostLog = hostLog }
  // The first read is the app being up. Kestrel reports its address once it is listening, so this is
  // normally the only read.
  let sw = Stopwatch.StartNew()
  let mutable answered = false
  while not answered && sw.Elapsed < TestTimeouts.appFirstAnswer do
    let! read = task {
      try
        let! _ = get app runAppFixture.ReadyRoute
        return true
      with _ -> return false }
    answered <- read
    match answered with
    | true -> ()
    | false -> do! Task.Delay TestTimeouts.pollService
  match answered with
  | true -> return app, { BuildMs = buildMs; ProcessMs = watch.Elapsed.TotalMilliseconds }
  | false -> return failwithf "the run_app fixture never answered /%s.\n%s" runAppFixture.ReadyRoute (RunningApp.log app)
}

/// A start that times the build as a real one: the case that reports what a restart pays on top of the build has to
/// measure that build, so it does not take it from the cache.
let startRunAppTimed (deltaMode: SageFs.Features.MetadataDelta.MetadataDeltaMode) (runtime: HostRuntime) : Task<RunningApp * StartTimes> =
  startRunAppBuilt BuildMode.Fresh deltaMode runtime

/// The route's app on a host of its own, its build copied from the per-process cache.
let startRunAppWith (deltaMode: SageFs.Features.MetadataDelta.MetadataDeltaMode) (runtime: HostRuntime) : Task<RunningApp> = task {
  let! app, _ = startRunAppBuilt BuildMode.Cached deltaMode runtime
  return app
}

/// The route as a user gets it when the flag is on.
let startRunApp (runtime: HostRuntime) : Task<RunningApp> =
  startRunAppWith SageFs.Features.MetadataDelta.MetadataDeltaMode.On runtime
