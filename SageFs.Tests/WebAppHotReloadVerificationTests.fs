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

module Integration = SageFs.Tests.TestInfrastructure.Integration

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

/// Build the fixture EXACTLY the way SageFs builds a session's project —
/// `SessionBuild.buildArguments`, the product's own command, which always
/// carries `-p:Optimize=false` — so the host loads what a real user's session
/// loads.
///
/// Without this the shape matrix loaded whatever the TEST SUITE's own build
/// left on disk, and `ProjectLoading.chooseFreshestConfigOutput` picks the
/// newest config output. Building SageFs.Tests in Release (CI, the pre-push
/// gate) builds this fixture Release as a side effect, so that was the one
/// loaded — an FSC-optimized assembly. Read out of its IL: the Release
/// `handlers@69` closure calls `String.Concat` directly, because FSC inlined
/// `localTypeHandler`'s body into it; `handlers@71-2` calls nothing at all. A
/// detour on `Shapes.localTypeHandler` then lands (the canary confirms it) on a
/// method nothing calls, and the save is reported "Hot reloaded 1 of 1" while
/// the app serves the old value. The Debug build of the same closures calls
/// `Shapes.localTypeHandler` and friends by name.
///
/// That is not the path a user is on — SageFs forces `Optimize=false` for
/// precisely this reason (see `SessionBuild.optimizationDisablingProperty`) —
/// and which config the test got depended on build ORDER, which is why the
/// matrix passed on one run and failed on the next. The remaining product gap,
/// a user who builds Release by hand AFTER SageFs's build, is separate and is
/// not what this gate claims to prove.
let private buildFixtureAsSageFsDoes () =
  let fDir = fixtureDir ()
  let psi = ProcessStartInfo("dotnet")
  for a in SessionBuild.buildArguments true (Path.Combine(fDir, "WebAppFixture.fsproj")) do
    psi.ArgumentList.Add a
  psi.WorkingDirectory <- fDir
  psi.UseShellExecute <- false
  use p = Process.Start psi
  p.WaitForExit()
  p.ExitCode
  |> Expect.equal "the fixture must build with SageFs's own session-build command" 0

/// Spawn the real host, read WORKER_PORT= from stdout, return (proc, baseUrl, proxy).
/// The fixture project is passed EXPLICITLY via SAGEFS_SESSION_PROJECTS so
/// the host never walks up to the repo root and loads SageFs.slnx (which
/// would warm up 200+ namespaces and make the test take minutes).
/// `hostLog` accumulates the host's stdout/stderr for failure diagnostics.
let private spawnHost (sessionId: string) (hostLog: StringBuilder) =
  let exe = hostExePath ()
  let args, envVars = Args.buildWorkerSpawnConfig sessionId [ SageFs.SessionProjectTarget.Project (Path.Combine(fixtureDir (), "WebAppFixture.fsproj")) ] false true (SageFs.WorkflowTypes.SessionWorkflow.HotReload SageFs.WorkflowTypes.BrowserRefreshConfig.defaults)
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
  // The compiler diagnostics say WHY (FSI's own message is just "earlier error"), so a red run explains itself.
  | WorkerProtocol.WorkerResponse.EvalResult (_, Error err, diagnostics, _) -> failwithf "eval failed: %A\nDiagnostics: %A\nCode: %s" err diagnostics code
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
  let seen = ResizeArray<string>()
  let mutable found = ""
  let mutable line = reader.ReadLine()
  while found = "" && line <> null && sw.ElapsedMilliseconds < int64 timeoutMs do
    if line.StartsWith("data: ", StringComparison.Ordinal) then
      let payload = line.Substring("data: ".Length)
      seen.Add payload
      if predicate payload then found <- payload
    line <- reader.ReadLine()
  if found = "" then
    failwithf "SSE stream did not produce a matching event within %dms. SAW %d events:\n%s" timeoutMs seen.Count (String.concat "\n" seen)
  found

/// Wait out the worker's double-compile guard before saving the SAME file again.
///
/// This is not a sleep-poll standing in for a missing signal: it is this test
/// stepping out of the way of a DELIBERATE product behaviour. An editor emits
/// several filesystem events for one save, so `FileWatcher.shouldSuppressRecompile`
/// drops a second change to the same file within `DoubleCompileGuardMs` and
/// compiles once. A test that saves the same file twice inside that window is
/// therefore making ONE save as far as the product is concerned — the second
/// save produces no event of any kind, and the test times out waiting for a
/// verdict the product correctly never sends. Two saves a user would experience
/// as two saves have to be separated by the guard, so the budget is read from
/// the product's own constant rather than guessed at.
let private waitOutDoubleCompileGuard () =
  Thread.Sleep(DevReload.DevReloadConfig.defaults.DoubleCompileGuardMs * 3)

/// Write a fixture file with retry: a host process killed at the end of a
/// previous test can briefly hold the file (FileSystemWatcher + FSI handle
/// teardown), and two [Integration] tests share the fixture directory.
let private writeFixtureFile (path: string) (content: string) =
  let sw = Stopwatch.StartNew()
  let mutable written = false
  while not written && sw.ElapsedMilliseconds < 15000L do
    try
      File.WriteAllText(path, content)
      written <- true
    with :? IOException ->
      Thread.Sleep 200
  if not written then
    failwithf "could not write fixture file %s within 15s (locked by a previous host?)" path

/// The hot-reload shape matrix.
///
/// WHY it is a matrix and not one case: the older gate below proves ONE shape —
/// a `namespace`-declared `[<MethodImpl(NoInlining)>]` function the route CALLS
/// at request time. That is the one shape a method detour has always been able
/// to rewire, so a genuinely green outcome gate coexisted for months with a
/// feature that was broken for every real web app. The fixture's call shape is
/// therefore a DIMENSION of this matrix, not an implementation detail: each cell
/// names the binding shape it drives, and the set deliberately includes the
/// shapes the mechanism CANNOT handle so they are asserted as limitations rather
/// than quietly left out.
///
/// Each cell's assertion is the same user-visible fact: the app is running, its
/// handler table was captured at startup, a source file is saved, and the SAME
/// process either serves the new code or is required to still serve the old one
/// because that shape genuinely cannot be patched.
module ShapeMatrix =

  type Verdict =
    /// The save must reach the running app.
    | Reloads
    /// The shape cannot be patched in place; the running app must still serve
    /// the pre-edit value, and the reason is stated here.
    | RestartOnly of reason: string
    /// Live state: the edit is to an initializer, and the app KEEPS its live
    /// value (rule 3 of hot-reload-state-spec.md). It must still serve the
    /// pre-edit value, and the wire must not claim a change.
    | KeepsLiveValue of reason: string

  type Cell = {
    /// The route segment: GET /shape/<Name>.
    Name: string
    /// What the cell drives, in one line, for the failure message.
    Why: string
    /// Unique source text to replace, and its replacement.
    Find: string
    Replace: string
    Expected: Verdict
  }

  let cells : Cell list = [
    { Name = "localType"
      Why = "a module-level FUNCTION whose parameter type is declared in the SAME file — the shape that broke Falco/Giraffe/Saturn/Oxpecker route tables, because a whole-file re-evaluation re-declares that type and the detour matcher then rejects the pair on parameter types"
      Find = "let localTypeHandler (reply: Reply) : string = \"A\" + reply.Body"
      Replace = "let localTypeHandler (reply: Reply) : string = \"B\" + reply.Body"
      Expected = Reloads }

    { Name = "plain"
      Why = "a module-level FUNCTION with a BCL-only signature, captured by value into the table at startup"
      Find = "let plainHandler (who: string) : string = \"A\" + who"
      Replace = "let plainHandler (who: string) : string = \"B\" + who"
      Expected = Reloads }

    { Name = "tiny"
      Why = "a FUNCTION small enough for the JIT to want to inline into its caller, with NO [<MethodImpl(NoInlining)>] — a real user never writes that attribute, so hot reload has to hold without it"
      Find = "let tinyHandler () : string = \"A\""
      Replace = "let tinyHandler () : string = \"B\""
      Expected = Reloads }

    { Name = "member"
      Why = "a static TYPE MEMBER rather than a module-level function"
      Find = "  static member Render() : string = \"A\""
      Replace = "  static member Render() : string = \"B\""
      Expected = Reloads }

    { Name = "lambda"
      Why = "a VALUE binding holding a lambda (`let h : HttpHandler = fun ctx -> ...`)"
      Find = "let lambdaHandler : string -> string = fun who -> \"A\" + who"
      Replace = "let lambdaHandler : string -> string = fun who -> \"B\" + who"
      // Was RestartOnly ("a value binding is re-run by module initialisation").
      // That was the PLANNER's belief, not the compiler's: F# compiles a
      // module-level value bound DIRECTLY to a lambda as a METHOD, and the
      // captured closure calls it by name — read out of the fixture's IL, where
      // `handlers@72-3` calls `Shapes.lambdaHandler`. Once the planner stopped
      // classifying it as a value (`ReloadPlanning.isLambdaBody`), the save
      // re-points that method, the app serves the new body, and the wire says
      // Patched 1 of 1 — the honesty check above confirms the two agree. The
      // limitation is gone, so the cell moved, as its own failure message asks.
      Expected = Reloads }

    { Name = "eager"
      Why = "a handler whose output is computed ONCE at module initialisation and closed over — `let getHome : HttpHandler = Response.ofHtml (pageLayout [])` in Falco terms"
      Find = "let private computeEager () = \"A\""
      Replace = "let private computeEager () = \"B\""
      Expected =
        RestartOnly
          "nothing is called at request time, so there is no method entry point to re-point: the value was baked into the captured closure at startup" }

    { Name = "mutable"
      Why = "a MUTABLE module-level field read by the handler, whose INITIALIZER is edited"
      Find = "let mutable mutableField = \"A\""
      Replace = "let mutable mutableField = \"B\""
      // Was RestartOnly. Rule 3 of the state spec: the field is the app's live
      // data, so an edited initializer keeps the live value and waits for a
      // reset instead of forcing a restart. The served value is the same "A"
      // either way; what changed is that it's on purpose and the save says so.
      Expected =
        KeepsLiveValue
          "the field is live data the app is holding, so the edited initializer waits for a reset instead of replacing it" }
  ]

/// The fixture's App.fs is the file we edit on disk. This test is the plan's
/// required RED test: a real module-declared Falco/ASP.NET fixture whose route
/// closes over a function. Start the app through a SageFs Live-workflow
/// session, watch the actual source file, request the route (value A), edit
/// the function body on disk to value B, observe Compiling -> Reload through
/// the real worker path, then request the SAME running process and require B.
[<Tests>]
let webAppHotReloadVerificationTests =
  testList "WebApp hot-reload verification" [

    Integration.hostCase "real file save hot-reloads a running module-declared app (save-driven, no restart)" <| fun () ->
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
        writeFixtureFile appSource edited
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
          writeFixtureFile appSource original
      finally
        try proc.Kill(entireProcessTree = true) with _ -> ()
        try proc.Dispose() with _ -> ()
    Integration.hostCase "compile-error save keeps last valid behavior and repair hot-reloads it" <| fun () ->
      let fDir = fixtureDir ()
      let appSource = Path.Combine(fDir, "Greeting.fs")
      let original = File.ReadAllText(appSource)

      let sessionId = sprintf "webapp-repair-%s" (Guid.NewGuid().ToString("N"))
      let hostLog = StringBuilder()
      let proc, baseUrl, proxy = spawnHost sessionId hostLog
      try
        waitReady proxy hostLog
        let appFile = Path.Combine(fDir, "App.fs")
        evalOk proxy (sprintf "#load @\"%s\"" appSource) |> ignore
        evalOk proxy (sprintf "#load @\"%s\"" appFile) |> ignore
        let port = freePort ()
        evalOk proxy (sprintf "let appTask = WebAppFixture.App.run %d" port) |> ignore

        let bodyA = httpGet port "/"
        Expect.stringContains "first response should be the original greeting" "hello from sagefs" bodyA

        watchAllFiles baseUrl
        waitForWatched baseUrl 10000
        use sseReader = openSseStream baseUrl

        // 1. Save BROKEN F# — this must NOT take down the running process.
        //    The watcher broadcasts CompilationFailed with a diagnostic; the
        //    app keeps serving the last valid behavior (value A).
        //    NOTE: the broken text must be a real compile error (type
        //    mismatch). A syntactically incomplete expression (trailing binary
        //    operator) sends FSI into continuation mode — it waits for more
        //    input instead of erroring, and the watcher never broadcasts.
        let broken =
          original.Replace(
            "let greeting () = \"hello from sagefs\"",
            "let greeting () : int = \"this will not compile\"")
        writeFixtureFile appSource broken
        try
          let failedEvt =
            try
              readSseUntil sseReader 30000 (fun payload ->
                payload.Contains("\"type\":\"failed\"") && payload.Contains("diagnostics"))
            with ex ->
              let dumpPath = Path.Combine(Path.GetTempPath(), sprintf "sagefs-repair-%s.log" sessionId)
              File.WriteAllText(dumpPath, hostLog.ToString())
              failwithf "%s\nHost log dumped to %s" ex.Message dumpPath
          Expect.stringContains
            "failed event should carry the error summary" "error" failedEvt

          // App must still be alive and serving the last valid behavior.
          let bodyAfterFail = httpGet port "/"
          Expect.stringContains
            "compile error must not take down the running app (last valid behavior retained)"
            "hello from sagefs" bodyAfterFail

          // 2. Repair the file — the fix must hot-reload into the running app.
          //    Brief pause so the FileSystemWatcher has re-armed after the
          //    failed eval's event burst before writing again.
          Thread.Sleep 1000
          let repaired =
            original.Replace(
              "let greeting () = \"hello from sagefs\"",
              "let greeting () = \"hello from hot reload (value B)\"")
          writeFixtureFile appSource repaired
          let written = File.ReadAllText(appSource)
          Expect.stringContains "repair write should have landed on disk" "hello from hot reload (value B)" written
          try
            // The repair save must produce Reload. If it instead produces
            // another `failed`, that's a real bug (a failed eval corrupting
            // the watcher's cache so the fix can't reload) — report it.
            let evt =
              readSseUntil sseReader 30000 (fun payload ->
                payload.Contains("\"type\":\"reload\"")
                || payload.Contains("\"type\":\"failed\""))
            Expect.stringContains
              "repair save should produce reload, not another failure" "\"type\":\"reload\"" evt
          with ex ->
            let dumpPath = Path.Combine(Path.GetTempPath(), sprintf "sagefs-repair2-%s.log" sessionId)
            File.WriteAllText(dumpPath, hostLog.ToString())
            failwithf "%s\nHost log dumped to %s" ex.Message dumpPath
          let bodyB = httpGet port "/"
          Expect.stringContains
            (sprintf "repair should hot-reload the new greeting from the running process.\nHost log:\n%s" (hostLog.ToString()))
            "hello from hot reload (value B)" bodyB
        finally
          writeFixtureFile appSource original
      finally
        try proc.Kill(entireProcessTree = true) with _ -> ()
        try proc.Dispose() with _ -> ()

    // The shape matrix: one cell per F# binding shape a user can save, through a
    // real host, asserting the reload CLAIM equals the OBSERVED change. It was
    // once a standalone `--integration-shapes` entry point kept out of the
    // pipeline while 4 of 7 cells failed ("Patched 1 of 1" while the running
    // app still served the old body). All 7 now hold, so it runs in the main
    // pipeline under --integration-host like every other host suite. Every
    // assertion is exactly as first written; none was relaxed to get here.
    Integration.hostCase "hot-reload shape matrix: a startup-captured handler table, one cell per F# binding shape" <| fun () ->
      buildFixtureAsSageFsDoes ()
      let fDir = fixtureDir ()
      let shapesSource = Path.Combine(fDir, "Shapes.fs")
      Expect.isTrue "fixture Shapes.fs should exist" (File.Exists shapesSource)
      let original = File.ReadAllText shapesSource
      // Every cell's edit must be unique text, so a cell can never silently
      // rewrite another cell's source and report the wrong verdict.
      for cell in ShapeMatrix.cells do
        let occurrences =
          original.Split([| cell.Find |], StringSplitOptions.None).Length - 1
        Expect.equal
          (sprintf "%s: its edit anchor must appear exactly once in Shapes.fs" cell.Name)
          1 occurrences

      let sessionId = sprintf "shape-matrix-%s" (Guid.NewGuid().ToString("N"))
      let hostLog = StringBuilder()
      let proc, baseUrl, proxy = spawnHost sessionId hostLog
      try
        waitReady proxy hostLog

        // The REAL user path: the project is loaded by the session, its sources
        // are baselined at session start, and the app runs from the COMPILED
        // assembly. No `#load` anywhere — a `#load` would put an FSI copy of the
        // module in front of the compiled one and hide exactly the bug this
        // matrix exists to catch.
        let port = freePort ()
        evalOk proxy (sprintf "WebAppFixture.App.run %d" port) |> ignore
        let shape (name: string) = httpGet port ("/shape/" + name)

        for cell in ShapeMatrix.cells do
          Expect.equal
            (sprintf "%s: the running app should serve the pre-edit value" cell.Name)
            "A" (shape cell.Name)

        watchAllFiles baseUrl
        waitForWatched baseUrl 10000

        try
          for cell in ShapeMatrix.cells do
            let before = File.ReadAllText shapesSource
            use sseReader = openSseStream baseUrl
            waitOutDoubleCompileGuard ()
            writeFixtureFile shapesSource (before.Replace(cell.Find, cell.Replace))
            // Every save must close the Compiling -> terminal contract: a cell
            // that cannot be patched still has to answer, and the answer it
            // gives is `noeffect`/`restarted`, not `reload`/`failed`. Waiting
            // only for the latter two made every RestartOnly cell burn the full
            // 60s budget on a verdict the worker had already sent.
            let verdict =
              readSseUntil sseReader 60000 (fun payload ->
                [ "reload"; "failed"; "noeffect"; "restarted" ]
                |> List.exists (fun t -> payload.Contains(sprintf "\"type\":\"%s\"" t)))

            // Settle before reading, and say WHY this is not a sleep-poll
            // standing in for a missing signal. The verdict and the HTTP read
            // travel different channels: the worker broadcasts once
            // `confirmPatch` reports Applied, and the app is asked over its own
            // socket afterwards. Sampling once raced that and produced a
            // NON-DETERMINISTIC matrix — the same cell served the new value on
            // one run and the old one on the next, with the wire saying
            // `Patched 1 of 1` both times. Settling to a stable answer makes a
            // failure mean "the patch did not take effect", not "the read was
            // early", which is the difference between a real finding and a
            // flake. If the value never flips, this costs the budget once.
            let servedSettled (name: string) (want: string) =
              let sw = Stopwatch.StartNew()
              let mutable v = shape name
              while v <> want && sw.ElapsedMilliseconds < 5000L do
                Thread.Sleep 100
                v <- shape name
              v
            let served =
              match cell.Expected with
              | ShapeMatrix.Reloads -> servedSettled cell.Name "B"
              | ShapeMatrix.RestartOnly _
              | ShapeMatrix.KeepsLiveValue _ -> shape cell.Name

            // ── The honesty invariant, asserted BEFORE the per-cell verdict ──
            //
            // The matrix used to read only the served value and throw the wire
            // payload away. That cannot tell an honest in-place patch apart
            // from a whole-file fallback that moved the value while reporting
            // that nothing was patched — and "the counts do not reflect
            // reality" is the exact failure `ReloadOutcome` exists to stop
            // (see SageFs.Core/Features/ReloadOutcome.fs: a save that patched
            // nothing but broadcast `Reload` anyway is why that type was
            // written). A cell can therefore go green on behaviour while the
            // agent and the dashboard are being told something false.
            //
            // `ReloadOutcome.processChanged` is the ONE place that answers
            // "did the running process change?", so the wire has to agree with
            // what the process actually serves, whatever the cell expects.
            let claimedChange =
              [ "\"type\":\"reload\""; "\"type\":\"restarted\"" ]
              |> List.exists verdict.Contains
            let observedChange = served = "B"
            Expect.equal
              (sprintf
                "%s: the wire and the running app must agree about whether anything changed. The app serves %s, so the process %s changed; the worker sent:\n  %s\nA save that moves behaviour while reporting no effect (or reports a patch that did not land) is the dishonest-count failure ReloadOutcome was built to prevent.\nHost log:\n%s"
                cell.Name served (if observedChange then "DID" else "did NOT") verdict (hostLog.ToString()))
              observedChange claimedChange

            match cell.Expected with
            | ShapeMatrix.Reloads ->
              Expect.equal
                (sprintf
                  "%s — %s\nThe running app must serve the new code after the save, with no restart.\nWorker said: %s\nHost log:\n%s"
                  cell.Name cell.Why verdict (hostLog.ToString()))
                "B" served
            | ShapeMatrix.RestartOnly reason ->
              Expect.equal
                (sprintf
                  "%s — this shape CANNOT be patched in place (%s), so the running app must still serve the pre-edit value. If this now serves the new value the limitation is gone: move the cell to Reloads and update docs/hot-reload.md.\nWorker said: %s\nHost log:\n%s"
                  cell.Name reason verdict (hostLog.ToString()))
                "A" served
            | ShapeMatrix.KeepsLiveValue reason ->
              Expect.equal
                (sprintf
                  "%s: %s, so the running app must still serve its live value.\nWorker said: %s\nHost log:\n%s"
                  cell.Name reason verdict (hostLog.ToString()))
                "A" served
              verdict
              |> Expect.stringContains (sprintf "%s: the save has to say what it kept" cell.Name) "\"outcome\":\"KeptLiveState\""
        finally
          writeFixtureFile shapesSource original
      finally
        try proc.Kill(entireProcessTree = true) with _ -> ()
        try proc.Dispose() with _ -> ()

    // WHY — sagefs-ux-roast.md §11 Island C: "nobody has confirmed a real
    // client reading the wire can distinguish a no-op from a real reload."
    // Everything above proves the running PROCESS behaves correctly; this
    // proves the WIRE a client actually reads (the `/__sagefs__/reload` SSE
    // payload) carries the difference — same real host, same real file save,
    // no synthetic ReloadOutcome values anywhere in this test.
    Integration.hostCase "a real reload and a real no-op save produce SSE payloads a client can tell apart" <| fun () ->
      let fDir = fixtureDir ()
      let appSource = Path.Combine(fDir, "Greeting.fs")
      let original = File.ReadAllText(appSource)

      let sessionId = sprintf "webapp-distinguish-%s" (Guid.NewGuid().ToString("N"))
      let hostLog = StringBuilder()
      let proc, baseUrl, proxy = spawnHost sessionId hostLog
      try
        waitReady proxy hostLog
        let appFile = Path.Combine(fDir, "App.fs")
        evalOk proxy (sprintf "#load @\"%s\"" appSource) |> ignore
        evalOk proxy (sprintf "#load @\"%s\"" appFile) |> ignore
        let port = freePort ()
        evalOk proxy (sprintf "let appTask = WebAppFixture.App.run %d" port) |> ignore
        httpGet port "/" |> ignore

        watchAllFiles baseUrl
        waitForWatched baseUrl 10000

        let edited =
          original.Replace(
            "let greeting () = \"hello from sagefs\"",
            "let greeting () = \"hello from hot reload (value B)\"")

        // 1. A REAL reload: the payload must announce it landed, with counts.
        let reloadPayload =
          use sseReader = openSseStream baseUrl
          writeFixtureFile appSource edited
          try readSseUntil sseReader 30000 (fun payload -> payload.Contains("\"type\":\"reload\""))
          with ex ->
            failwithf "%s\nHost log:\n%s" ex.Message (hostLog.ToString())
        Expect.stringContains "a landed reload names its own case" "\"outcome\":\"Patched\"" reloadPayload
        Expect.isFalse "a landed reload never claims zero patched" (reloadPayload.Contains("\"patched\":0"))
        httpGet port "/"
        |> Expect.stringContains "the running process must actually serve the new code" "hello from hot reload (value B)"

        // 2. A REAL no-op: saving the SAME content again changes no
        //    declaration, so the wire must announce NOTHING landed — the
        //    exact distinction a client needs to stop rendering a save as a
        //    silent success when it changed nothing.
        let noopPayload =
          use sseReader = openSseStream baseUrl
          waitOutDoubleCompileGuard ()
          writeFixtureFile appSource edited
          try readSseUntil sseReader 30000 (fun payload -> payload.Contains("\"type\":\"noeffect\""))
          with ex ->
            failwithf "a byte-identical resave must report noeffect, not silently re-announce reload: %s\nHost log:\n%s" ex.Message (hostLog.ToString())
        Expect.stringContains "a no-op save never claims the Patched case" "\"outcome\":\"Unchanged\"" noopPayload

        // 3. The two payloads must actually differ where a client looks:
        //    the type a client switches on, and the outcome case it renders.
        Expect.isFalse "the reload and no-op payloads must carry different wire types" (reloadPayload.Contains("\"type\":\"noeffect\""))
        Expect.isFalse "the no-op payload must never carry the refresh cue" (noopPayload.Contains("\"type\":\"reload\""))
        reloadPayload = noopPayload
        |> Expect.isFalse "the two payloads must not be byte-identical — that is the whole bug this wire exists to prevent"

        // The running process is unaffected by the no-op save — still B.
        httpGet port "/"
        |> Expect.stringContains "a no-op save must not disturb the running process" "hello from hot reload (value B)"
      finally
        writeFixtureFile appSource original
        try proc.Kill(entireProcessTree = true) with _ -> ()
        try proc.Dispose() with _ -> ()
  ]
