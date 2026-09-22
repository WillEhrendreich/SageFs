/// A real host, a real app, a real save, on a chosen runtime.
///
/// The state outcome tests need the same thing the shape matrix in
/// WebAppHotReloadVerificationTests does (spawn SageFs.Host, start an app in
/// it, save a file, read what the SAME process serves), plus two things it
/// doesn't have: the runtime is a parameter, because hot reload has to hold on
/// net10.0 AND net11.0, and each run gets its own scratch copy of the fixture,
/// so a net10 build and a net11 build never fight over one obj/ folder and a
/// test's edits never land in the checked-in source.
module SageFs.Tests.HotReloadStateHarness

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

/// The runtimes the shipped host is built for. Every state outcome test runs
/// once per case, so a gap on either one is a red test, not a footnote.
[<RequireQualifiedAccess>]
type HostRuntime =
  | Net10
  | Net11

module HostRuntime =
  let all = [ HostRuntime.Net10; HostRuntime.Net11 ]

  let moniker =
    function
    | HostRuntime.Net10 -> "net10.0"
    | HostRuntime.Net11 -> "net11.0"

/// From this file's own folder, not AppContext.BaseDirectory: in a SageFs
/// session the base directory is the host's, so walking up from it finds the
/// wrong repo, or none.
let private repoRoot () =
  Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private buildConfiguration () =
  match AppContext.BaseDirectory.Contains("Release") with
  | true -> "Release"
  | false -> "Debug"

/// The host the daemon itself spawns for this runtime: SageFs/bin/<cfg>/<tfm>/host.
let hostExePath (runtime: HostRuntime) =
  let hostDir = Path.Combine(repoRoot (), "SageFs", "bin", buildConfiguration (), HostRuntime.moniker runtime, "host")
  let exe = Path.Combine(hostDir, "SageFs.Host.exe")
  let noExt = Path.Combine(hostDir, "SageFs.Host")
  match File.Exists exe, File.Exists noExt with
  | true, _ -> exe
  | _, true -> noExt
  | false, false ->
    failwithf "There's no %s host at %s. Build the solution in %s first, the state tests run against the host the daemon ships." (HostRuntime.moniker runtime) hostDir (buildConfiguration ())

let private fixtureSourceDir () =
  Path.Combine(repoRoot (), "SageFs.Tests", "fixtures", "HotReloadStateFixture")

/// Sources the scratch project compiles, in order.
let private fixtureSources = [ "State.fs"; "App.fs" ]

/// The scratch project. Written per run rather than checked in so the target
/// framework is whatever the run asks for. It lives under the repo on purpose,
/// so Directory.Packages.props pins FSharp.Core exactly like it does for the
/// real fixtures, and the lock file is switched off because a scratch
/// project's lock file is noise.
let private fixtureProject (runtime: HostRuntime) =
  String.concat "\n" [
    "<Project Sdk=\"Microsoft.NET.Sdk.Web\">"
    "  <PropertyGroup>"
    sprintf "    <TargetFramework>%s</TargetFramework>" (HostRuntime.moniker runtime)
    "    <OutputType>Library</OutputType>"
    "    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>"
    "    <RestoreLockedMode>false</RestoreLockedMode>"
    "  </PropertyGroup>"
    "  <ItemGroup>"
    yield! fixtureSources |> List.map (sprintf "    <Compile Include=\"%s\" />")
    "  </ItemGroup>"
    "</Project>"
    "" ]

type RunningApp = {
  Runtime: HostRuntime
  RunDir: string
  /// The copy of State.fs the host watches. Tests edit this one.
  StateSource: string
  Host: Process
  WorkerUrl: string
  Proxy: SessionProxy
  AppPort: int
  HostLog: StringBuilder
}

module RunningApp =
  let log (app: RunningApp) = lock app.HostLog (fun () -> app.HostLog.ToString())

let private http =
  let client = new HttpClient()
  client.Timeout <- TimeSpan.FromSeconds 30.0
  client

/// The SDK a real user on this runtime builds with. The isolated FSI host (the
/// process the app actually runs in) is built with the PROJECT's SDK
/// (FsiHostBuild), so a scratch project that just inherited the repo's
/// global.json would run its "net10" app on the .NET 11 FSI host. A net10 run
/// pins the newest installed 10.x SDK so the whole stack, SageFs.Host, MSBuild,
/// FCS and the FSI host, is net10. A net11 run keeps the repo's pin.
let private sdkPin (runtime: HostRuntime) : string option =
  match runtime with
  | HostRuntime.Net11 -> None
  | HostRuntime.Net10 ->
    let psi = ProcessStartInfo("dotnet", "--list-sdks")
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    psi.WorkingDirectory <- Path.GetTempPath()
    use p = Process.Start psi
    let listed = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    match FsiHostBuild.parseSdkList listed |> List.filter (fun v -> v.StartsWith("10.", StringComparison.Ordinal)) with
    | [] -> failwith "The net10 state tests need a .NET 10 SDK installed (dotnet --list-sdks shows none). Install one from https://dotnet.microsoft.com/download/dotnet/10.0."
    | versions -> versions |> List.maxBy (fun v -> Version(v.Split('-').[0])) |> Some

let private copyFixture (runtime: HostRuntime) =
  let runDir =
    Path.Combine(fixtureSourceDir (), ".runs", sprintf "%s-%s" (HostRuntime.moniker runtime) (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory runDir |> ignore
  match sdkPin runtime with
  | Some sdk ->
    File.WriteAllText(
      Path.Combine(runDir, "global.json"),
      sprintf """{"sdk":{"version":"%s","rollForward":"latestPatch","allowPrerelease":false}}""" sdk)
  | None -> ()
  for source in fixtureSources do
    File.Copy(Path.Combine(fixtureSourceDir (), source), Path.Combine(runDir, source))
  let project = Path.Combine(runDir, "StateFixture.fsproj")
  File.WriteAllText(project, fixtureProject runtime)
  // The copies must be OLDER than the build, or the host decides the source
  // was edited after the build and re-evaluates it whole instead of patching.
  let past = DateTime.UtcNow.AddMinutes -5.0
  for source in fixtureSources do
    File.SetLastWriteTimeUtc(Path.Combine(runDir, source), past)
  runDir, project

/// Built with SageFs's own session-build command, so what the host loads is
/// what a user's session loads (Optimize=false included).
let private buildAsSageFsDoes (runDir: string) (project: string) = task {
  let psi = ProcessStartInfo("dotnet")
  for a in SessionBuild.buildArguments true project do
    psi.ArgumentList.Add a
  psi.WorkingDirectory <- runDir
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  use p = Process.Start psi
  let out = p.StandardOutput.ReadToEndAsync()
  let err = p.StandardError.ReadToEndAsync()
  do! p.WaitForExitAsync()
  let! out = out
  let! err = err
  p.ExitCode
  |> Expect.equal (sprintf "the state fixture has to build with SageFs's session-build command:\n%s\n%s" out err) 0
}

let private spawnHost (runtime: HostRuntime) (runDir: string) (project: string) (hostLog: StringBuilder) = task {
  let sessionId = sprintf "state-%s" (Guid.NewGuid().ToString("N"))
  let args, envVars =
    Args.buildWorkerSpawnConfig sessionId [] false false true
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
  let! winner = Task.WhenAny(port.Task, Task.Delay(TimeSpan.FromSeconds 120.0))
  match obj.ReferenceEquals(winner, port.Task) with
  | false ->
    try proc.Kill(entireProcessTree = true) with _ -> ()
    return failwithf "the %s host never printed WORKER_PORT. Host log:\n%s" (HostRuntime.moniker runtime) (lock hostLog (fun () -> hostLog.ToString()))
  | true ->
    let workerUrl = port.Task.Result.TrimEnd('/')
    return proc, workerUrl
}

let private evalOk (proxy: SessionProxy) (code: string) = task {
  match! proxy (WorkerMessage.EvalCode(code, Guid.NewGuid().ToString("N"))) |> Async.StartAsTask with
  | WorkerResponse.EvalResult(_, Ok result, _, _) -> return result
  | WorkerResponse.EvalResult(_, Error err, diagnostics, _) ->
    return failwithf "eval failed: %A\nDiagnostics: %A\nCode: %s" err diagnostics code
  | other -> return failwithf "unexpected response: %A" other
}

/// Bounded wait for an external process to get somewhere. There's no push
/// signal for "the host's FSI session finished warming up" that a test can
/// subscribe to from outside the process, so this asks, with a deadline.
let private until (budget: TimeSpan) (describe: unit -> string) (probe: unit -> Task<bool>) = task {
  let sw = Stopwatch.StartNew()
  let mutable ok = false
  while not ok && sw.Elapsed < budget do
    let! answer = task { try return! probe () with _ -> return false }
    ok <- answer
    match ok with
    | true -> ()
    | false -> do! Task.Delay 250
  match ok with
  | true -> ()
  | false -> failwithf "gave up after %.0fs: %s" budget.TotalSeconds (describe ())
}

let get (app: RunningApp) (route: string) : Task<string> =
  http.GetStringAsync(sprintf "http://127.0.0.1:%d/%s" app.AppPort route)

/// Read `route` until it serves `want`, for up to 5s. The verdict and the app
/// travel on different sockets, so one read right after the verdict can race
/// the detour. If it never flips, the final value comes back and the caller's
/// assertion says what it was.
let settle (app: RunningApp) (route: string) (want: string) : Task<string> = task {
  let sw = Stopwatch.StartNew()
  let! first = get app route
  let mutable served = first
  while served <> want && sw.ElapsedMilliseconds < 5000L do
    do! Task.Delay 100
    let! next = get app route
    served <- next
  return served
}

let private postJson (url: string) (body: string) = task {
  use content = new StringContent(body, Encoding.UTF8, "application/json")
  let! resp = http.PostAsync(url, content)
  let! text = resp.Content.ReadAsStringAsync()
  return int resp.StatusCode, text
}

/// Spin the whole thing up: scratch copy, SageFs build, host, app, watch set.
/// `configureRepo` runs against the scratch run dir before the host spawns —
/// the worker's own CWD becomes that dir, so a `.SageFs/settings.json` it
/// writes there is the repo layer `reflectionSettingsFor` resolves against.
let startConfigured (runtime: HostRuntime) (configureRepo: string -> unit) : Task<RunningApp> = task {
  let runDir, project = copyFixture runtime
  configureRepo runDir
  do! buildAsSageFsDoes runDir project
  let hostLog = StringBuilder()
  let! proc, workerUrl = spawnHost runtime runDir project hostLog
  let proxy = HttpWorkerClient.httpProxy workerUrl
  let logText () = lock hostLog (fun () -> hostLog.ToString())
  do!
    until (TimeSpan.FromSeconds 180.0) (fun () -> "the session never reached Ready.\n" + logText ()) (fun () -> task {
      match! proxy (WorkerMessage.GetStatus(Guid.NewGuid().ToString("N"))) |> Async.StartAsTask with
      | WorkerResponse.StatusResult(_, s) -> return s.Status = SessionStatus.Ready
      | _ -> return false })
  // The app runs in the isolated FSI host, not in SageFs.Host, so THAT is the
  // runtime the run has to be on. Checked, not assumed: an inherited
  // global.json once put a "net10" run's app on .NET 11 without a word.
  let wantRuntime = sprintf "Isolated FSI host started: .NET %s." ((HostRuntime.moniker runtime).Substring(3).Split('.').[0])
  logText ()
  |> Expect.stringContains (sprintf "the app has to run on %s" (HostRuntime.moniker runtime)) wantRuntime
  let appPort, _ = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
  let! _ = evalOk proxy (sprintf "StateFixture.App.run %d" appPort)
  let app =
    { Runtime = runtime
      RunDir = runDir
      StateSource = Path.Combine(runDir, "State.fs")
      Host = proc
      WorkerUrl = workerUrl
      Proxy = proxy
      AppPort = appPort
      HostLog = hostLog }
  do! until (TimeSpan.FromSeconds 30.0) (fun () -> "the app never answered /count.\n" + logText ()) (fun () -> task {
    let! _ = get app "count"
    return true })
  let! status, body = postJson (workerUrl + "/hotreload/watch-all") "{}"
  status |> Expect.equal (sprintf "watch-all should succeed: %s" body) 200
  do! until (TimeSpan.FromSeconds 10.0) (fun () -> "no file was reported as watched") (fun () -> task {
    let! json = http.GetStringAsync(workerUrl + "/hotreload")
    return not (json.Contains "\"watchedCount\":0") })
  return app
}

let start (runtime: HostRuntime) : Task<RunningApp> = startConfigured runtime (fun _ -> ())

let private isVerdict (payload: string) =
  [ "reload"; "failed"; "noeffect"; "restarted" ]
  |> List.exists (fun t -> payload.Contains(sprintf "\"type\":\"%s\"" t))

/// Open the reload stream, write the edit, and return the terminal verdict the
/// worker sent for it. The stream is open BEFORE the write so nothing is missed.
///
/// The wait before the write is the double-compile guard, not a sleep standing
/// in for a signal: one editor save fires several filesystem events, so the
/// watcher drops a second change to the same file inside `DoubleCompileGuardMs`
/// on purpose. Two saves in a row have to be further apart than that or the
/// product (correctly) sees one.
let saveWithinBudget (budget: TimeSpan) (app: RunningApp) (find: string) (replace: string) : Task<string> = task {
  let before = File.ReadAllText app.StateSource
  let occurrences = before.Split([| find |], StringSplitOptions.None).Length - 1
  occurrences |> Expect.equal (sprintf "the edit anchor has to appear exactly once in State.fs: %s" find) 1
  use req = new HttpRequestMessage(HttpMethod.Get, app.WorkerUrl + "/__sagefs__/reload")
  req.Headers.Accept.ParseAdd "text/event-stream"
  use streamClient = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)
  use! resp = streamClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
  resp.EnsureSuccessStatusCode() |> ignore
  use! stream = resp.Content.ReadAsStreamAsync()
  use reader = new StreamReader(stream)
  do! Task.Delay(DevReload.DevReloadConfig.defaults.DoubleCompileGuardMs * 3)
  File.WriteAllText(app.StateSource, before.Replace(find, replace))
  use cts = new CancellationTokenSource(budget)
  let seen = ResizeArray<string>()
  let mutable verdict = ""
  try
    while verdict = "" do
      let! line = reader.ReadLineAsync(cts.Token)
      match line with
      | null -> cts.Cancel()
      | l when l.StartsWith("data: ", StringComparison.Ordinal) ->
        let payload = l.Substring "data: ".Length
        seen.Add payload
        match isVerdict payload with
        | true -> verdict <- payload
        | false -> ()
      | _ -> ()
  with :? OperationCanceledException -> ()
  match verdict with
  | "" ->
    return
      failwithf "no verdict within %.0fs for the edit %s -> %s. Saw:\n%s\nHost log:\n%s"
        budget.TotalSeconds find replace (String.concat "\n" seen) (RunningApp.log app)
  | v -> return v
}

let save (app: RunningApp) (find: string) (replace: string) : Task<string> =
  saveWithinBudget (TimeSpan.FromSeconds 60.0) app find replace

/// GET a worker route, the same one the daemon proxies for the dashboard.
let getWorker (app: RunningApp) (route: string) : Task<string> =
  http.GetStringAsync(app.WorkerUrl + route)

/// POST to a worker route, for the actions a user takes from the dashboard.
let post (app: RunningApp) (route: string) (body: string) : Task<int * string> =
  postJson (app.WorkerUrl + route) body

let stop (app: RunningApp) =
  try app.Host.Kill(entireProcessTree = true) with _ -> ()
  try app.Host.WaitForExit 10_000 |> ignore with _ -> ()
  try app.Host.Dispose() with _ -> ()
  try Directory.Delete(app.RunDir, true) with _ -> ()
