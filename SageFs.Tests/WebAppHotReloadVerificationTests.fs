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
let private buildFixtureAsSageFsDoes () : Task<unit> =
  task {
    let fDir = fixtureDir ()
    let psi = ProcessStartInfo("dotnet")
    for a in SessionBuild.buildArguments true (Path.Combine(fDir, "WebAppFixture.fsproj")) do
      psi.ArgumentList.Add a
    psi.WorkingDirectory <- fDir
    psi.UseShellExecute <- false
    use p = Process.Start psi
    do! p.WaitForExitAsync()
    p.ExitCode
    |> Expect.equal "the fixture must build with SageFs's own session-build command" 0
  }

/// Rethrow `ex` with its original stack from a `with` handler that has already awaited (`reraise ()` cannot
/// follow an await).
let private rethrow (ex: exn) : unit =
  System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()

/// The host log as text. The drain tasks append to `hostLog` under `lock hostLog`, so a read takes the
/// same lock: `StringBuilder` is not thread-safe, and an unlocked `ToString()` racing an append throws
/// `ArgumentOutOfRangeException ('chunkLength')`, which flaked a release gate on a case that was passing.
let hostLogText (hostLog: StringBuilder) : string = lock hostLog (fun () -> hostLog.ToString())

/// Spawn the real host, read WORKER_PORT= from stdout, return (proc, baseUrl, proxy).
/// The fixture project is passed EXPLICITLY via SAGEFS_SESSION_PROJECTS so
/// the host never walks up to the repo root and loads SageFs.slnx (which
/// would warm up 200+ namespaces and make the test take minutes).
/// `hostLog` accumulates the host's stdout/stderr for failure diagnostics.
let private spawnHost (sessionId: string) (hostLog: StringBuilder) : Task<Process * string * WorkerProtocol.SessionProxy> =
  task {
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
    // Drain stdout AND stderr with async readers so neither pipe ever fills
    // (an undrained redirect pipe deadlocks the host before WORKER_PORT prints), and neither drain parks a
    // pool thread on a blocking ReadLine for the life of the host.
    let portLine = TaskCompletionSource<string>()
    let drain (reader: StreamReader) (onLine: string -> unit) : Task =
      task {
        try
          let mutable reading = true
          while reading do
            let! line = reader.ReadLineAsync()
            match isNull line with
            | true -> reading <- false
            | false ->
              lock hostLog (fun () -> hostLog.AppendLine(line) |> ignore)
              onLine line
        with _ -> ()
      }
    let drainOut =
      drain proc.StandardOutput (fun line ->
        if line.StartsWith("WORKER_PORT=", StringComparison.Ordinal) then
          portLine.TrySetResult(line.Substring("WORKER_PORT=".Length)) |> ignore)
    let drainErr = drain proc.StandardError ignore
    let! winner = Task.WhenAny(portLine.Task, Task.Delay Timeouts.webAppPortReady)
    if not (obj.ReferenceEquals(winner, portLine.Task)) then
      failwithf "host did not print WORKER_PORT within %.0fs. Host log:\n%s" Timeouts.webAppPortReady.TotalSeconds (hostLogText hostLog)
    let! port = portLine.Task
    let baseUrl = port.TrimEnd('/')
    let proxy = HttpWorkerClient.httpProxy baseUrl
    return proc, baseUrl, proxy
  }

let private evalOk (proxy: WorkerProtocol.SessionProxy) (code: string) : Task<string> =
  task {
    let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode(code, Guid.NewGuid().ToString("N"))) |> Async.StartAsTask
    match response with
    | WorkerProtocol.WorkerResponse.EvalResult (_, Ok result, _, _) -> return result
    // The compiler diagnostics say WHY (FSI's own message is just "earlier error"), so a red run explains itself.
    | WorkerProtocol.WorkerResponse.EvalResult (_, Error err, diagnostics, _) -> return failwithf "eval failed: %A\nDiagnostics: %A\nCode: %s" err diagnostics code
    | other -> return failwithf "unexpected response: %A" other
  }

let private waitReady (proxy: WorkerProtocol.SessionProxy) (hostLog: StringBuilder) : Task<unit> =
  task {
    let mutable ready = false
    let sw = Stopwatch.StartNew()
    // Cold CI runners (Linux) can take >60s to warm up FSI + load the project.
    // Transient HTTP errors are expected while Kestrel is coming up — retry.
    while not ready && sw.ElapsedMilliseconds < 180000 do
      try
        let! response = proxy (WorkerProtocol.WorkerMessage.GetStatus(Guid.NewGuid().ToString("N"))) |> Async.StartAsTask
        match response with
        | WorkerProtocol.WorkerResponse.StatusResult (_, s) when s.Status = SessionStatus.Ready -> ready <- true
        | _ -> do! Task.Delay TestTimeouts.warmupPoll
      with _ ->
        do! Task.Delay TestTimeouts.warmupPoll
    if not ready then
      failwithf "session did not reach Ready within 180s. Host log:\n%s" (hostLogText hostLog)
  }

let private httpGet (port: int) (path: string) : Task<string> =
  task {
    use client = new HttpClient()
    client.Timeout <- TestTimeouts.requestPatience
    try
      return! client.GetStringAsync(sprintf "http://127.0.0.1:%d%s" port path)
    with ex ->
      return failwithf "HTTP GET %s failed: %s" path ex.Message
  }

/// Opt every project file into the hot-reload watch set via the REAL worker
/// HTTP endpoint (the same route the dashboard calls:
/// POST /hotreload/watch-all). This proves the save flows through the real
/// worker file watcher.
let private watchAllFiles (baseUrl: string) : Task<unit> =
  task {
    use client = new HttpClient()
    client.Timeout <- TestTimeouts.shortPatience
    use content = new StringContent("{}", Encoding.UTF8, "application/json")
    let! resp = client.PostAsync(baseUrl + "/hotreload/watch-all", content)
    resp.EnsureSuccessStatusCode() |> ignore
  }

/// Poll GET /hotreload until the worker reports at least one watched file.
/// The watch-all POST updates the worker's HotReloadStateRef asynchronously; a
/// file save racing that update would be ignored by the watcher (not in the
/// watch set yet) and the reload would never fire.
let private waitForWatched (baseUrl: string) (timeoutMs: int) : Task<unit> =
  task {
    use client = new HttpClient()
    client.Timeout <- TestTimeouts.shortPatience
    let sw = Stopwatch.StartNew()
    let mutable watched = false
    while not watched && sw.ElapsedMilliseconds < int64 timeoutMs do
      try
        let! resp = client.GetAsync(baseUrl + "/hotreload")
        let! json = resp.Content.ReadAsStringAsync()
        if json.Contains("\"watchedCount\":0") then
          do! Task.Delay TestTimeouts.localHttpPoll
        else
          watched <- true
      with _ ->
        do! Task.Delay TestTimeouts.localHttpPoll
    if not watched then
      failwithf "no files were reported as watched within %dms" timeoutMs
  }

/// Open the worker's DevReload SSE stream and return a reader positioned at
/// the first event. Must be called BEFORE the file save so no Compiling/Reload
/// event can be missed (the watcher's debounce + eval can complete in well
/// under a second).
let private openSseStream (baseUrl: string) : Task<StreamReader> =
  task {
    let client = new HttpClient()
    client.Timeout <- TestTimeouts.streamReadPatience
    let req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/__sagefs__/reload")
    req.Headers.Accept.ParseAdd("text/event-stream")
    let! resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
    resp.EnsureSuccessStatusCode() |> ignore
    let! stream = resp.Content.ReadAsStreamAsync()
    return new StreamReader(stream)
  }

/// Read from an already-open SSE stream until an event matching `predicate`
/// arrives (or `timeoutMs` elapses). Returns the matching event JSON. The budget bounds every read, so a stream
/// that goes quiet fails the case at the budget instead of parking on the next line.
let private readSseUntil (reader: StreamReader) (timeoutMs: int) (predicate: string -> bool) : Task<string> =
  task {
    use budget = new CancellationTokenSource(timeoutMs)
    let seen = ResizeArray<string>()
    let mutable found = ""
    let mutable finished = false
    while found = "" && not finished do
      let! line =
        task {
          try
            return! reader.ReadLineAsync(budget.Token).AsTask()
          with :? OperationCanceledException -> return null
        }
      if isNull line then
        finished <- true
      elif line.StartsWith("data: ", StringComparison.Ordinal) then
        let payload = line.Substring("data: ".Length)
        seen.Add payload
        if predicate payload then found <- payload
    if found = "" then
      failwithf "SSE stream did not produce a matching event within %dms. SAW %d events:\n%s" timeoutMs seen.Count (String.concat "\n" seen)
    return found
  }

/// A patch's first verdict is `pending` (applied, and the new code has not been seen
/// running). Once the app has run the patched code the same stream carries
/// `patched`; when the bound passes without that it carries `neverentered`.
let private readConfirmation (reader: StreamReader) : Task<string> =
  readSseUntil reader 40000 (fun payload ->
    payload.Contains("\"type\":\"patched\"") || payload.Contains("\"type\":\"neverentered\""))

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
let private waitOutDoubleCompileGuard () : Task<unit> =
  task { do! Task.Delay(DevReload.DevReloadConfig.defaults.DoubleCompileGuardMs * 3) }

/// Write a fixture file with retry: a host process killed at the end of a
/// previous test can briefly hold the file (FileSystemWatcher + FSI handle
/// teardown), and two [Integration] tests share the fixture directory.
let private writeFixtureFile (path: string) (content: string) : Task<unit> =
  task {
    let sw = Stopwatch.StartNew()
    let mutable written = false
    while not written && sw.ElapsedMilliseconds < 15000L do
      try
        File.WriteAllText(path, content)
        written <- true
      with :? IOException ->
        do! Task.Delay TestTimeouts.localHttpPoll
    if not written then
      failwithf "could not write fixture file %s within 15s (locked by a previous host?)" path
  }

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

    Integration.hostCaseTask "real file save hot-reloads a running module-declared app (save-driven, no restart)" <| fun () -> task {
      let fDir = fixtureDir ()
      let appSource = Path.Combine(fDir, "Greeting.fs")
      Expect.isTrue "fixture Greeting.fs should exist" (File.Exists appSource)

      // Read the ORIGINAL fixture content; we will write it back at the end.
      let original = File.ReadAllText(appSource)

      let sessionId = sprintf "webapp-verify-%s" (Guid.NewGuid().ToString("N"))
      let hostLog = StringBuilder()
      let! (proc: Process), baseUrl, proxy = spawnHost sessionId hostLog
      try
        // 1. Wait for the session to be Ready.
        do! waitReady proxy hostLog

        // 2. Load Greeting.fs FIRST so App.fs's `Greeting.greeting` reference
        //    binds to the FSI-loaded (detourable) version, not the compiled
        //    WebAppFixture.dll the worker pre-loads from the project bin.
        let appFile = Path.Combine(fDir, "App.fs")
        let! loadResult = evalOk proxy (sprintf "#load @\"%s\"" appSource)
        Expect.stringContains "Greeting.fs should load" "Greeting.fs" loadResult
        let! loadApp = evalOk proxy (sprintf "#load @\"%s\"" appFile)
        Expect.stringContains "App.fs should load" "App.fs" loadApp

        // 3. Start the app on a free port inside the host.
        let port = freePort ()
        let! startResult = evalOk proxy (sprintf "let appTask = WebAppFixture.App.run %d" port)
        Expect.stringContains "app start should succeed" "appTask" startResult

        // 4. HTTP GET the running app — record value A.
        let! bodyA = httpGet port "/"
        Expect.stringContains "first response should be the original greeting" "hello from sagefs" bodyA

        // 5. Opt the fixture source into the hot-reload watch set via the real
        //    worker endpoint (same route the dashboard uses), and confirm the
        //    worker actually reports the file as watched before editing.
        do! watchAllFiles baseUrl
        do! waitForWatched baseUrl 10000

        // 6. Open the DevReload SSE stream BEFORE editing the file, so no
        //    Compiling/Reload event can be missed (the watcher debounce + FSI
        //    eval can complete in well under a second).
        use! sseReader = openSseStream baseUrl

        // 7. EDIT THE FILE ON DISK — this is the real save that must propagate.
        let edited =
          original.Replace(
            "let greeting () = \"hello from sagefs\"",
            "let greeting () = \"hello from hot reload (value B)\"")
        Expect.stringContains "fixture should contain the editable greeting function" "let greeting () = \"hello from sagefs\"" original
        do! writeFixtureFile appSource edited
        try
          // 8. Observe Compiling -> Reload through the real worker SSE path.
          let! _ = readSseUntil sseReader 30000 (fun payload -> payload.Contains("\"type\":\"pending\""))

          // 9. Request the SAME running process without restart — require B.
          let! bodyB = httpGet port "/"
          Expect.stringContains
            (sprintf "hot reload should serve the new greeting from the running process.\nValue A body: %s\nHost log:\n%s" bodyA (hostLogText hostLog))
            "hello from hot reload (value B)" bodyB

          // 9b. The request above ran the patched function, so the same stream now
          //     carries the confirmation: the new code was seen running.
          let! confirmation = readConfirmation sseReader
          confirmation
          |> Expect.stringContains "the patch is confirmed once its new body has run" "\"type\":\"patched\""
          // Restore the fixture so later runs start from value A.
          do! writeFixtureFile appSource original
        with ex ->
          // Always restore the fixture so later runs start from value A (a `finally` cannot await).
          do! writeFixtureFile appSource original
          rethrow ex
      finally
        try proc.Kill(entireProcessTree = true) with _ -> ()
        try proc.Dispose() with _ -> ()
    }
    Integration.hostCaseTask "compile-error save keeps last valid behavior and repair hot-reloads it" <| fun () -> task {
      let fDir = fixtureDir ()
      let appSource = Path.Combine(fDir, "Greeting.fs")
      let original = File.ReadAllText(appSource)

      let sessionId = sprintf "webapp-repair-%s" (Guid.NewGuid().ToString("N"))
      let hostLog = StringBuilder()
      let! (proc: Process), baseUrl, proxy = spawnHost sessionId hostLog
      try
        do! waitReady proxy hostLog
        let appFile = Path.Combine(fDir, "App.fs")
        let! _ = evalOk proxy (sprintf "#load @\"%s\"" appSource)
        let! _ = evalOk proxy (sprintf "#load @\"%s\"" appFile)
        let port = freePort ()
        let! _ = evalOk proxy (sprintf "let appTask = WebAppFixture.App.run %d" port)

        let! bodyA = httpGet port "/"
        Expect.stringContains "first response should be the original greeting" "hello from sagefs" bodyA

        do! watchAllFiles baseUrl
        do! waitForWatched baseUrl 10000
        use! sseReader = openSseStream baseUrl

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
        do! writeFixtureFile appSource broken
        try
          let! failedEvt =
            task {
              try
                return!
                  readSseUntil sseReader 30000 (fun payload ->
                    payload.Contains("\"type\":\"failed\"") && payload.Contains("diagnostics"))
              with ex ->
                let dumpPath = Path.Combine(Path.GetTempPath(), sprintf "sagefs-repair-%s.log" sessionId)
                File.WriteAllText(dumpPath, hostLogText hostLog)
                return failwithf "%s\nHost log dumped to %s" ex.Message dumpPath
            }
          Expect.stringContains
            "failed event should carry the error summary" "error" failedEvt

          // App must still be alive and serving the last valid behavior.
          let! bodyAfterFail = httpGet port "/"
          Expect.stringContains
            "compile error must not take down the running app (last valid behavior retained)"
            "hello from sagefs" bodyAfterFail

          // 2. Repair the file — the fix must hot-reload into the running app.
          //    Brief pause so the FileSystemWatcher has re-armed after the
          //    failed eval's event burst before writing again.
          do! Task.Delay TestTimeouts.watcherRearmSettle
          let repaired =
            original.Replace(
              "let greeting () = \"hello from sagefs\"",
              "let greeting () = \"hello from hot reload (value B)\"")
          do! writeFixtureFile appSource repaired
          let written = File.ReadAllText(appSource)
          Expect.stringContains "repair write should have landed on disk" "hello from hot reload (value B)" written
          try
            // The repair save must produce Reload. If it instead produces
            // another `failed`, that's a real bug (a failed eval corrupting
            // the watcher's cache so the fix can't reload) — report it.
            let! evt =
              readSseUntil sseReader 30000 (fun payload ->
                payload.Contains("\"type\":\"pending\"")
                || payload.Contains("\"type\":\"failed\""))
            Expect.stringContains
              "repair save should apply the patch, not fail again" "\"type\":\"pending\"" evt
          with ex ->
            let dumpPath = Path.Combine(Path.GetTempPath(), sprintf "sagefs-repair2-%s.log" sessionId)
            File.WriteAllText(dumpPath, hostLogText hostLog)
            failwithf "%s\nHost log dumped to %s" ex.Message dumpPath
          let! bodyB = httpGet port "/"
          Expect.stringContains
            (sprintf "repair should hot-reload the new greeting from the running process.\nHost log:\n%s" (hostLogText hostLog))
            "hello from hot reload (value B)" bodyB
          do! writeFixtureFile appSource original
        with ex ->
          // Always restore the fixture so later runs start from value A (a `finally` cannot await).
          do! writeFixtureFile appSource original
          rethrow ex
      finally
        try proc.Kill(entireProcessTree = true) with _ -> ()
        try proc.Dispose() with _ -> ()
    }

    // The shape matrix: one cell per F# binding shape a user can save, through a
    // real host, asserting the reload CLAIM equals the OBSERVED change. It was
    // once a standalone `--integration-shapes` entry point kept out of the
    // pipeline while 4 of 7 cells failed ("Patched 1 of 1" while the running
    // app still served the old body). All 7 now hold, so it runs in the main
    // pipeline under --integration-host like every other host suite. Every
    // assertion is exactly as first written; none was relaxed to get here.
    Integration.hostCaseTask "hot-reload shape matrix: a startup-captured handler table, one cell per F# binding shape" <| fun () -> task {
      do! buildFixtureAsSageFsDoes ()
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
      let! (proc: Process), baseUrl, proxy = spawnHost sessionId hostLog
      try
        do! waitReady proxy hostLog

        // The REAL user path: the project is loaded by the session, its sources
        // are baselined at session start, and the app runs from the COMPILED
        // assembly. No `#load` anywhere — a `#load` would put an FSI copy of the
        // module in front of the compiled one and hide exactly the bug this
        // matrix exists to catch.
        let port = freePort ()
        let! _ = evalOk proxy (sprintf "WebAppFixture.App.run %d" port)
        let shape (name: string) : Task<string> = httpGet port ("/shape/" + name)

        for cell in ShapeMatrix.cells do
          let! preEdit = shape cell.Name
          Expect.equal
            (sprintf "%s: the running app should serve the pre-edit value" cell.Name)
            "A" preEdit

        do! watchAllFiles baseUrl
        do! waitForWatched baseUrl 10000

        try
          for cell in ShapeMatrix.cells do
            let before = File.ReadAllText shapesSource
            use! sseReader = openSseStream baseUrl
            do! waitOutDoubleCompileGuard ()
            do! writeFixtureFile shapesSource (before.Replace(cell.Find, cell.Replace))
            // Every save must close the Compiling -> terminal contract: a cell
            // that cannot be patched still has to answer, and the answer it
            // gives is `noeffect`/`restarted`, not `reload`/`failed`. Waiting
            // only for the latter two made every RestartOnly cell burn the full
            // 60s budget on a verdict the worker had already sent.
            let! verdict =
              readSseUntil sseReader 60000 (fun payload ->
                [ "pending"; "failed"; "noeffect"; "restarted" ]
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
            let servedSettled (name: string) (want: string) : Task<string> =
              task {
                let sw = Stopwatch.StartNew()
                let! first = shape name
                let mutable v = first
                while v <> want && sw.ElapsedMilliseconds < 5000L do
                  do! Task.Delay TestTimeouts.poll
                  let! next = shape name
                  v <- next
                return v
              }
            let! served =
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
            //
            // A patch's first verdict is `pending` (applied, not yet seen running), so
            // the claim that has to agree with the app is the FINAL one: `patched`
            // once the new code has run, `neverentered` when the bound passed first.
            // The request above has already run whatever the route calls.
            let! finalVerdict =
              match verdict.Contains "\"type\":\"pending\"" with
              | true -> readConfirmation sseReader
              | false -> Task.FromResult verdict
            let claimedChange =
              [ "\"type\":\"patched\""; "\"type\":\"restarted\"" ]
              |> List.exists finalVerdict.Contains
            let observedChange = served = "B"
            Expect.equal
              (sprintf
                "%s: the wire and the running app must agree about whether anything changed. The app serves %s, so the process %s changed; the worker sent:\n  %s\nA save that moves behaviour while reporting no effect (or reports a patch that did not land) is the dishonest-count failure ReloadOutcome was built to prevent.\nHost log:\n%s"
                cell.Name served (if observedChange then "DID" else "did NOT") finalVerdict (hostLogText hostLog))
              observedChange claimedChange

            match cell.Expected with
            | ShapeMatrix.Reloads ->
              Expect.equal
                (sprintf
                  "%s — %s\nThe running app must serve the new code after the save, with no restart.\nWorker said: %s\nHost log:\n%s"
                  cell.Name cell.Why verdict (hostLogText hostLog))
                "B" served
              // The request that served B ran the patched function, so the worker has seen
              // its new code run and says so: not merely applied.
              finalVerdict
              |> Expect.stringContains (sprintf "%s: the new code ran, so the save is confirmed" cell.Name) "\"outcome\":\"Patched\""
            | ShapeMatrix.RestartOnly reason ->
              Expect.equal
                (sprintf
                  "%s — this shape CANNOT be patched in place (%s), so the running app must still serve the pre-edit value. If this now serves the new value the limitation is gone: move the cell to Reloads and update docs/hot-reload.md.\nWorker said: %s\nHost log:\n%s"
                  cell.Name reason verdict (hostLogText hostLog))
                "A" served
            | ShapeMatrix.KeepsLiveValue reason ->
              Expect.equal
                (sprintf
                  "%s: %s, so the running app must still serve its live value.\nWorker said: %s\nHost log:\n%s"
                  cell.Name reason verdict (hostLogText hostLog))
                "A" served
              verdict
              |> Expect.stringContains (sprintf "%s: the save has to say what it kept" cell.Name) "\"outcome\":\"KeptLiveState\""
          do! writeFixtureFile shapesSource original
        with ex ->
          // Always restore the fixture so later runs start from the original (a `finally` cannot await).
          do! writeFixtureFile shapesSource original
          rethrow ex
      finally
        try proc.Kill(entireProcessTree = true) with _ -> ()
        try proc.Dispose() with _ -> ()
    }

    // WHY — sagefs-ux-roast.md §11 Island C: "nobody has confirmed a real
    // client reading the wire can distinguish a no-op from a real reload."
    // Everything above proves the running PROCESS behaves correctly; this
    // proves the WIRE a client actually reads (the `/__sagefs__/reload` SSE
    // payload) carries the difference — same real host, same real file save,
    // no synthetic ReloadOutcome values anywhere in this test.
    Integration.hostCaseTask "a real reload and a real no-op save produce SSE payloads a client can tell apart" <| fun () -> task {
      let fDir = fixtureDir ()
      let appSource = Path.Combine(fDir, "Greeting.fs")
      let original = File.ReadAllText(appSource)

      let sessionId = sprintf "webapp-distinguish-%s" (Guid.NewGuid().ToString("N"))
      let hostLog = StringBuilder()
      let! (proc: Process), baseUrl, proxy = spawnHost sessionId hostLog
      let killHost () =
        try proc.Kill(entireProcessTree = true) with _ -> ()
        try proc.Dispose() with _ -> ()
      try
        do! waitReady proxy hostLog
        let appFile = Path.Combine(fDir, "App.fs")
        let! _ = evalOk proxy (sprintf "#load @\"%s\"" appSource)
        let! _ = evalOk proxy (sprintf "#load @\"%s\"" appFile)
        let port = freePort ()
        let! _ = evalOk proxy (sprintf "let appTask = WebAppFixture.App.run %d" port)
        let! _ = httpGet port "/"

        do! watchAllFiles baseUrl
        do! waitForWatched baseUrl 10000

        let edited =
          original.Replace(
            "let greeting () = \"hello from sagefs\"",
            "let greeting () = \"hello from hot reload (value B)\"")

        // 1. A REAL reload: the payload must announce it landed, with counts.
        let! reloadPayload =
          task {
            use! sseReader = openSseStream baseUrl
            do! writeFixtureFile appSource edited
            try
              return! readSseUntil sseReader 30000 (fun payload -> payload.Contains("\"type\":\"pending\""))
            with ex ->
              return failwithf "%s\nHost log:\n%s" ex.Message (hostLogText hostLog)
          }
        Expect.stringContains "an applied reload names its own case" "\"outcome\":\"PatchPending\"" reloadPayload
        // An applied patch has had nothing confirmed yet, so its `patched` count (what has
        // been seen running) is zero, and it still says how many definitions it put in
        // front of the process.
        Expect.isTrue "an applied reload has confirmed nothing yet, and says so" (reloadPayload.Contains("\"patched\":0"))
        Expect.isFalse "but it never claims it put nothing in front of the process" (reloadPayload.Contains("\"considered\":0"))
        let! servedAfterReload = httpGet port "/"
        servedAfterReload
        |> Expect.stringContains "the running process must actually serve the new code" "hello from hot reload (value B)"

        // 2. A REAL no-op: saving the SAME content again changes no
        //    declaration, so the wire must announce NOTHING landed — the
        //    exact distinction a client needs to stop rendering a save as a
        //    silent success when it changed nothing.
        let! noopPayload =
          task {
            use! sseReader = openSseStream baseUrl
            do! waitOutDoubleCompileGuard ()
            do! writeFixtureFile appSource edited
            try
              return! readSseUntil sseReader 30000 (fun payload -> payload.Contains("\"type\":\"noeffect\""))
            with ex ->
              return failwithf "a byte-identical resave must report noeffect, not silently re-announce reload: %s\nHost log:\n%s" ex.Message (hostLogText hostLog)
          }
        Expect.stringContains "a no-op save never claims the Patched case" "\"outcome\":\"Unchanged\"" noopPayload

        // 3. The two payloads must actually differ where a client looks:
        //    the type a client switches on, and the outcome case it renders.
        Expect.isFalse "the reload and no-op payloads must carry different wire types" (reloadPayload.Contains("\"type\":\"noeffect\""))
        Expect.isFalse "the no-op payload must never carry the refresh cue" (noopPayload.Contains("\"type\":\"pending\""))
        reloadPayload = noopPayload
        |> Expect.isFalse "the two payloads must not be byte-identical — that is the whole bug this wire exists to prevent"

        // The running process is unaffected by the no-op save — still B.
        let! servedAfterNoop = httpGet port "/"
        servedAfterNoop
        |> Expect.stringContains "a no-op save must not disturb the running process" "hello from hot reload (value B)"
        do! writeFixtureFile appSource original
        killHost ()
      with ex ->
        // Restore the fixture, then take the host down, then fail with the original error (a `finally` cannot await).
        do! writeFixtureFile appSource original
        killHost ()
        rethrow ex
    }
  ]
