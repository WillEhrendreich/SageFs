module SageFs.Tests.WebAppHotReloadVerificationTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

// ============================================================================
// Deterministic end-to-end verification of the web-app hot-reload path:
//   real SageFs.Host process  ->  FSI session  ->  ASP.NET Core app starts
//   ->  HTTP responds  ->  re-eval changes greeting  ->  app serves new value
// The host speaks HTTP (WorkerHttpTransport): it prints WORKER_PORT=<url> on
// stdout, then serves /eval, /status, etc. We drive it with the canonical
// HttpWorkerClient.httpProxy.
// ============================================================================

let private hostExePath () =
  let here = DirectoryInfo(AppContext.BaseDirectory)
  let root = here.Parent.Parent.Parent.Parent.FullName // repo root
  let cfg =
    if AppContext.BaseDirectory.Contains("Release") then "Release" else "Debug"
  let hostDir = Path.Combine(root, "SageFs", "bin", cfg, "net11.0", "host")
  // Windows: SageFs.Host.exe; Linux/macOS: extensionless SageFs.Host.
  let exe = Path.Combine(hostDir, "SageFs.Host.exe")
  let noExt = Path.Combine(hostDir, "SageFs.Host")
  if File.Exists exe then exe
  elif File.Exists noExt then noExt
  else failwithf "Could not locate SageFs.Host at %s or %s" exe noExt

let private freePort () =
  let l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
  l.Start()
  let p = (l.LocalEndpoint :?> System.Net.IPEndPoint).Port
  l.Stop()
  p

/// Spawn the real host, read WORKER_PORT= from stdout, return (proc, proxy).
/// The fixture project is passed EXPLICITLY via SAGEFS_SESSION_PROJECTS so
/// the host never walks up to the repo root and loads SageFs.slnx (which
/// would warm up 200+ namespaces and make the test take minutes).
/// `hostLog` accumulates the host's stdout/stderr for failure diagnostics.
let private spawnHost (sessionId: string) (hostLog: StringBuilder) =
  let exe = hostExePath ()
  let args, envVars = Args.buildWorkerSpawnConfig sessionId [] false false true (SageFs.WorkflowTypes.SessionWorkflow.WebLive SageFs.WorkflowTypes.BrowserRefreshConfig.defaults)
  let psi = ProcessStartInfo(exe, args)
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  // cwd = fixture dir; project passed explicitly so discovery stays tiny.
  let fixtureDir =
    let here = DirectoryInfo(AppContext.BaseDirectory)
    let root = here.Parent.Parent.Parent.Parent.FullName
    Path.Combine(root, "SageFs.Tests", "fixtures", "WebAppFixture")
  psi.WorkingDirectory <- fixtureDir
  let fixtureProj = Path.Combine(fixtureDir, "WebAppFixture.fsproj")
  for k, v in envVars do
    if k = SageFs.Args.WorkerConfig.envVar then
      psi.EnvironmentVariables[k] <- fixtureProj
    else
      psi.EnvironmentVariables[k] <- v
  let proc = Process.Start(psi)
  // Drain stdout AND stderr on background tasks so neither pipe ever fills
  // (an undrained redirect pipe deadlocks the host before WORKER_PORT prints).
  let portLine = TaskCompletionSource<string>()
  let drainOut = Task.Run(fun () ->
    try
      let mutable line = proc.StandardOutput.ReadLine()
      while line <> null do
        lock hostLog (fun () -> hostLog.AppendLine(line) |> ignore)
        if line.StartsWith("WORKER_PORT=", StringComparison.Ordinal) then
          portLine.TrySetResult(line.Substring("WORKER_PORT=".Length)) |> ignore
        line <- proc.StandardOutput.ReadLine()
    with _ -> ())
  let drainErr = Task.Run(fun () ->
    try
      let mutable line = proc.StandardError.ReadLine()
      while line <> null do
        lock hostLog (fun () -> hostLog.AppendLine(line) |> ignore)
        line <- proc.StandardError.ReadLine()
    with _ -> ())
  let ok = portLine.Task.Wait(TimeSpan.FromSeconds(120.0))
  if not ok then
    failwithf "host did not print WORKER_PORT within 120s. Host log:\n%s" (hostLog.ToString())
  let baseUrl = portLine.Task.Result.TrimEnd('/')
  let proxy = HttpWorkerClient.httpProxy baseUrl
  proc, proxy

let private evalOk (proxy: WorkerProtocol.SessionProxy) (code: string) =
  match proxy (WorkerProtocol.WorkerMessage.EvalCode(code, Guid.NewGuid().ToString("N"))) |> Async.RunSynchronously with
  | WorkerProtocol.WorkerResponse.EvalResult (_, Ok result, _, _) -> result
  | WorkerProtocol.WorkerResponse.EvalResult (_, Error err, _, _) -> failwithf "eval failed: %A" err
  | other -> failwithf "unexpected response: %A" other

let private waitReady (proxy: WorkerProtocol.SessionProxy) (hostLog: StringBuilder) =
  let mutable ready = false
  let sw = Stopwatch.StartNew()
  // Cold CI runners (Linux) can take >60s to warm up FSI + load the project.
  // Transient HTTP errors are expected while Kestrel is coming up — retry.
  while not ready && sw.ElapsedMilliseconds < 180000 do
    try
      match proxy (WorkerProtocol.WorkerMessage.GetStatus(Guid.NewGuid().ToString("N"))) |> Async.RunSynchronously with
      | WorkerProtocol.WorkerResponse.StatusResult (_, s) when s.Status = SessionStatus.Ready -> ready <- true
      | _ -> Thread.Sleep 500
    with _ ->
      Thread.Sleep 500
  if not ready then
    failwithf "session did not reach Ready within 180s. Host log:\n%s" (hostLog.ToString())

let private httpGet (port: int) (path: string) =
  use client = new HttpClient()
  client.Timeout <- TimeSpan.FromSeconds(10.0)
  client.GetStringAsync(sprintf "http://127.0.0.1:%d%s" port path)
  |> Async.AwaitTask
  |> Async.RunSynchronously

[<Tests>]
let webAppHotReloadVerificationTests =
  testList "WebApp hot-reload verification" [

    testCase "[Integration] host starts, loads web fixture source, serves HTTP, and hot-reloads the greeting" <| fun () ->
      let fixtureDir =
        let here = DirectoryInfo(AppContext.BaseDirectory)
        let root = here.Parent.Parent.Parent.Parent.FullName
        Path.Combine(root, "SageFs.Tests", "fixtures", "WebAppFixture")
      let appSource = Path.Combine(fixtureDir, "App.fs")
      Expect.isTrue "fixture App.fs should exist" (File.Exists appSource)

      let sessionId = sprintf "webapp-verify-%s" (Guid.NewGuid().ToString("N"))
      let hostLog = StringBuilder()
      let proc, proxy = spawnHost sessionId hostLog
      try
        // 1. Wait for the session to be Ready.
        waitReady proxy hostLog

        // 2. Load the fixture source into the FSI session.
        let loadResult = evalOk proxy (sprintf "#load @\"%s\"" appSource)
        Expect.stringContains "source should load" "App.fs" loadResult

        // 3. Start the app on a free port inside the host.
        let port = freePort ()
        let startResult = evalOk proxy (sprintf "let appTask = WebAppFixture.App.run %d" port)
        Expect.stringContains "app start should succeed" "appTask" startResult

        // 4. HTTP GET the running app.
        let body = httpGet port "/"
        Expect.stringContains "first response should be the original greeting" "hello from sagefs" body

        // 5. Hot reload: re-eval the greeting module with a new value.
        evalOk proxy "WebAppFixture.Greeting.text <- \"hello after hot reload\"" |> ignore

        // 6. HTTP GET again — the running app must serve the NEW value.
        let body2 = httpGet port "/"
        Expect.stringContains "hot reload should serve the new greeting" "hello after hot reload" body2
      finally
        try proc.Kill(entireProcessTree = true) with _ -> ()
        try proc.Dispose() with _ -> ()
  ]
