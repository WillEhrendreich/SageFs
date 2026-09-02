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
//   ->  HTTP responds  ->  EDIT THE SOURCE FILE ON DISK  ->  the file watcher
//   picks up the change -> preprocess -> FSI re-eval -> Harmony detour ->
//   the SAME running process serves the NEW value (no restart).
//
// This is the plan's required first RED test for the shared P0 hot-reload gap:
//   - The app is a real module-declared Falco/ASP.NET fixture whose route
//     closes over a function defined in the source file.
//   - The change is a REAL FILE SAVE, not a direct FSI mutation of a mutable
//     value. Direct mutable assignment (Greeting.text <- ...) is explicitly
//     forbidden as hot-reload proof by the quality-gap closure plan.
//   - We observe the Compiling -> Reload lifecycle through the real worker
//     path (SSE on /__sagefs__/reload) and require the running process to
//     serve value B without restart.
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

let private repoRoot () =
  DirectoryInfo(AppContext.BaseDirectory).Parent.Parent.Parent.Parent.FullName

let private fixtureDir () =
  Path.Combine(repoRoot (), "SageFs.Tests", "fixtures", "WebAppFixture")

/// Spawn the real host, read WORKER_PORT= from stdout, return (proc, baseUrl, proxy).
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
  let fDir = fixtureDir ()
  psi.WorkingDirectory <- fDir
  let fixtureProj = Path.Combine(fDir, "WebAppFixture.fsproj")
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
  proc, baseUrl, proxy

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
  client.Timeout <- TimeSpan.FromSeconds(30.0)
  try
    client.GetStringAsync(sprintf "http://127.0.0.1:%d%s" port path)
    |> Async.AwaitTask
    |> Async.RunSynchronously
  with ex ->
    failwithf "HTTP GET %s failed: %s" path ex.Message

/// Opt every project file into the hot-reload watch set via the REAL worker
/// HTTP endpoint (the same route the dashboard calls:
/// POST /hotreload/watch-all). This proves the save flows through the real
/// worker file watcher.
let private watchAllFiles (baseUrl: string) =
  use client = new HttpClient()
  client.Timeout <- TimeSpan.FromSeconds(10.0)
  use content = new StringContent("{}", Encoding.UTF8, "application/json")
  let resp = client.PostAsync(baseUrl + "/hotreload/watch-all", content) |> Async.AwaitTask |> Async.RunSynchronously
  resp.EnsureSuccessStatusCode() |> ignore

/// Poll GET /hotreload until the worker reports at least one watched file.
/// The watch-all POST updates the worker's HotReloadStateRef asynchronously; a
/// file save racing that update would be ignored by the watcher (not in the
/// watch set yet) and the reload would never fire.
let private waitForWatched (baseUrl: string) (timeoutMs: int) =
  use client = new HttpClient()
  client.Timeout <- TimeSpan.FromSeconds(10.0)
  let sw = Stopwatch.StartNew()
  let mutable watched = false
  while not watched && sw.ElapsedMilliseconds < int64 timeoutMs do
    try
      let resp = client.GetAsync(baseUrl + "/hotreload") |> Async.AwaitTask |> Async.RunSynchronously
      let json = resp.Content.ReadAsStringAsync() |> Async.AwaitTask |> Async.RunSynchronously
      if json.Contains("\"watchedCount\":0") then
        Thread.Sleep 200
      else
        watched <- true
    with _ ->
      Thread.Sleep 200
  if not watched then
    failwithf "no files were reported as watched within %dms" timeoutMs

/// Open the worker's DevReload SSE stream and return a reader positioned at
/// the first event. Must be called BEFORE the file save so no Compiling/Reload
/// event can be missed (the watcher's debounce + eval can complete in well
/// under a second).
let private openSseStream (baseUrl: string) : StreamReader =
  let client = new HttpClient()
  client.Timeout <- TimeSpan.FromSeconds(60.0)
  let req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/__sagefs__/reload")
  req.Headers.Accept.ParseAdd("text/event-stream")
  let resp = client.Send(req, HttpCompletionOption.ResponseHeadersRead)
  resp.EnsureSuccessStatusCode() |> ignore
  new StreamReader(resp.Content.ReadAsStream())

/// Read from an already-open SSE stream until an event matching `predicate`
/// arrives (or `timeoutMs` elapses). Returns the matching event JSON.
let private readSseUntil (reader: StreamReader) (timeoutMs: int) (predicate: string -> bool) : string =
  let sw = Stopwatch.StartNew()
  let mutable found = ""
  let mutable line = reader.ReadLine()
  while found = "" && line <> null && sw.ElapsedMilliseconds < int64 timeoutMs do
    if line.StartsWith("data: ", StringComparison.Ordinal) then
      let payload = line.Substring("data: ".Length)
      if predicate payload then found <- payload
    line <- reader.ReadLine()
  if found = "" then
    failwithf "SSE stream did not produce a matching event within %dms" timeoutMs
  found

/// The fixture's App.fs is the file we edit on disk. This test is the plan's
/// required RED test: a real module-declared Falco/ASP.NET fixture whose route
/// closes over a function. Start the app through a SageFs Live-workflow
/// session, watch the actual source file, request the route (value A), edit
/// the function body on disk to value B, observe Compiling -> Reload through
/// the real worker path, then request the SAME running process and require B.
[<Tests>]
let webAppHotReloadVerificationTests =
  testList "WebApp hot-reload verification" [

    testCase "[Integration] real file save hot-reloads a running module-declared app (save-driven, no restart)" <| fun () ->
      let fDir = fixtureDir ()
      let appSource = Path.Combine(fDir, "Greeting.fs")
      Expect.isTrue "fixture Greeting.fs should exist" (File.Exists appSource)

      // Read the ORIGINAL fixture content; we will write it back at the end.
      let original = File.ReadAllText(appSource)

      let sessionId = sprintf "webapp-verify-%s" (Guid.NewGuid().ToString("N"))
      let hostLog = StringBuilder()
      let proc, baseUrl, proxy = spawnHost sessionId hostLog
      try
        // 1. Wait for the session to be Ready.
        waitReady proxy hostLog

        // 2. Load Greeting.fs FIRST so App.fs's `Greeting.greeting` reference
        //    binds to the FSI-loaded (detourable) version, not the compiled
        //    WebAppFixture.dll the worker pre-loads from the project bin.
        let appFile = Path.Combine(fDir, "App.fs")
        let loadResult = evalOk proxy (sprintf "#load @\"%s\"" appSource)
        Expect.stringContains "Greeting.fs should load" "Greeting.fs" loadResult
        let loadApp = evalOk proxy (sprintf "#load @\"%s\"" appFile)
        Expect.stringContains "App.fs should load" "App.fs" loadApp

        // 3. Start the app on a free port inside the host.
        let port = freePort ()
        let startResult = evalOk proxy (sprintf "let appTask = WebAppFixture.App.run %d" port)
        Expect.stringContains "app start should succeed" "appTask" startResult

        // 4. HTTP GET the running app — record value A.
        let bodyA = httpGet port "/"
        Expect.stringContains "first response should be the original greeting" "hello from sagefs" bodyA

        // 5. Opt the fixture source into the hot-reload watch set via the real
        //    worker endpoint (same route the dashboard uses), and confirm the
        //    worker actually reports the file as watched before editing.
        watchAllFiles baseUrl
        waitForWatched baseUrl 10000

        // 6. Open the DevReload SSE stream BEFORE editing the file, so no
        //    Compiling/Reload event can be missed (the watcher debounce + FSI
        //    eval can complete in well under a second).
        use sseReader = openSseStream baseUrl

        // 7. EDIT THE FILE ON DISK — this is the real save that must propagate.
        let edited =
          original.Replace(
            "let greeting () = \"hello from sagefs\"",
            "let greeting () = \"hello from hot reload (value B)\"")
        Expect.stringContains "fixture should contain the editable greeting function" "let greeting () = \"hello from sagefs\"" original
        File.WriteAllText(appSource, edited)
        try
          // 8. Observe Compiling -> Reload through the real worker SSE path.
          readSseUntil sseReader 30000 (fun payload -> payload.Contains("\"type\":\"reload\""))
          |> ignore

          // 9. Request the SAME running process without restart — require B.
          let bodyB = httpGet port "/"
          Expect.stringContains
            (sprintf "hot reload should serve the new greeting from the running process.\nValue A body: %s\nHost log:\n%s" bodyA (hostLog.ToString()))
            "hello from hot reload (value B)" bodyB
        finally
          // Always restore the fixture so later runs start from value A.
          File.WriteAllText(appSource, original)
      finally
        try proc.Kill(entireProcessTree = true) with _ -> ()
        try proc.Dispose() with _ -> ()
  ]
