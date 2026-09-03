module SageFs.Tests.HttpApiIntegrationTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Features

// ─── Shared Helpers ───────────────────────────────────────────────

let repoRoot =
  Path.GetFullPath(
    Path.Combine(__SOURCE_DIRECTORY__, ".."))

// The Falco + Datastar sample webapp. The HTTP API integration tests run
// against this instead of SageFs.Tests.fsproj so the session loads a small,
// separate project whose init profile starts a real app.
let webSampleProject =
  Path.Combine(
    repoRoot,
    "samples", "demos", "SageFs.Samples.WebappDatastar",
    "SageFs.Samples.WebappDatastar.fsproj")

let testProjectDir = Path.GetDirectoryName(webSampleProject)

let smokeSampleProject =
  Path.Combine(
    repoRoot,
    "samples",
    "from-csharp",
    "SageFs.Samples.FromCSharp",
    "SageFs.Samples.FromCSharp.fsproj")

let smokeSampleProjectDir = Path.GetDirectoryName(smokeSampleProject)

let sageFsExe =
  let localExe =
    Path.Combine(repoRoot, "SageFs", "bin", "Debug", "net10.0", "SageFs.exe")
  let toolDir =
    Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
      ".dotnet", "tools")
  let exe = Path.Combine(toolDir, "SageFs.exe")
  if File.Exists localExe then localExe
  elif File.Exists exe then exe
  else "SageFs"

let private daemonStartupHealthPollInterval = TimeSpan.FromMilliseconds(100.0)

let private daemonStartupHealthTimeout =
  TimeSpan.FromSeconds(60.0)

let private daemonStartupHealthMaxAttempts =
  int (Math.Ceiling(daemonStartupHealthTimeout.TotalMilliseconds / daemonStartupHealthPollInterval.TotalMilliseconds))

let private tryReserveLoopbackPort (port: int) =
  try
    use listener = new TcpListener(IPAddress.Loopback, port)
    listener.Start()
    (listener.LocalEndpoint :?> IPEndPoint).Port |> Some
  with
  | :? SocketException -> None

let reserveLoopbackPort (preferredPort: int option) =
  match preferredPort |> Option.bind tryReserveLoopbackPort with
  | Some port -> port
  | None ->
    match tryReserveLoopbackPort 0 with
    | Some port -> port
    | None -> failwith "Unable to reserve a loopback port for HTTP integration tests."

let runProcessExpectSuccess (fileName: string) (workingDir: string) (args: string list) =
  let psi = ProcessStartInfo()
  psi.FileName <- fileName
  psi.WorkingDirectory <- workingDir
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true

  for arg in args do
    psi.ArgumentList.Add(arg)

  use proc = Process.Start(psi)
  let stdout = proc.StandardOutput.ReadToEnd()
  let stderr = proc.StandardError.ReadToEnd()
  proc.WaitForExit()

  match proc.ExitCode with
  | 0 -> ()
  | code ->
      failwith (
        sprintf
          "Process failed: %s %s (exit %d)%s%s"
          fileName
          (String.concat " " args)
          code
          Environment.NewLine
          ((stdout + Environment.NewLine + stderr).Trim()))

/// Start a daemon on a given port, wait until /health responds, return (process, HttpClient).
let startDaemonWithArgs (port: int) (workingDir: string) (args: string list) = task {
  let psi = ProcessStartInfo()
  psi.FileName <- sageFsExe
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- workingDir
  psi.ArgumentList.Add("--mcp-port")
  psi.ArgumentList.Add(string port)
  for arg in args do
    psi.ArgumentList.Add(arg)

  // Isolate this daemon's persisted state so it never resumes (or pollutes)
  // sessions from the real ~/.SageFs or from earlier test runs.
  let dataDir = Path.Combine(Path.GetTempPath(), "sagefs-test", Guid.NewGuid().ToString("N"))
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir

  let proc = Process.Start(psi)
  let client = new HttpClient()
  client.BaseAddress <- Uri(sprintf "http://localhost:%d" port)
  client.Timeout <- TimeSpan.FromSeconds(30.0)

  // Poll until /health responds (up to 60s)
  let mutable ready = false
  let mutable attempts = 0
  while not ready && attempts < daemonStartupHealthMaxAttempts do
    do! Threading.Tasks.Task.Delay(daemonStartupHealthPollInterval)
    try
      let! resp = client.GetAsync("/health")
      if int resp.StatusCode > 0 then ready <- true
    with _ -> ()
    attempts <- attempts + 1

  if not ready then
    try proc.Kill() with _ -> ()
    proc.Dispose()
    client.Dispose()
    failwith (sprintf "Daemon failed to start on port %d within %O" port daemonStartupHealthTimeout)

  return proc, client
}

let startDaemon (port: int) =
  startDaemonWithArgs port testProjectDir []

/// POST JSON to a path, return (statusCode, body).
let postJson (client: HttpClient) (path: string) (payload: obj) = task {
  let json = JsonSerializer.Serialize(payload)
  use content = new StringContent(json, Encoding.UTF8, "application/json")
  let! resp = client.PostAsync(path, content)
  let! body = resp.Content.ReadAsStringAsync()
  return int resp.StatusCode, body
}

let utf8NoBom = new UTF8Encoding(false)

/// GET a path, return (statusCode, body).
let getJson (client: HttpClient) (path: string) = task {
  let! resp = client.GetAsync(path)
  let! body = resp.Content.ReadAsStringAsync()
  return int resp.StatusCode, body
}

let normalizeDir (path: string) =
  Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

let createSession (client: HttpClient) (projectPath: string) (workingDir: string) = task {
  let payload =
    {| projects = [| projectPath |]
       workingDirectory = workingDir |}
  let! status, body = postJson client "/api/sessions/create" payload
  return status, body
}

let waitForReadySession (client: HttpClient) (targetDir: string) (timeout: TimeSpan) = task {
  let started = DateTime.UtcNow
  let mutable ready = false
  let mutable lastBody = ""
  let expectedDir = normalizeDir targetDir

  while not ready && DateTime.UtcNow - started < timeout do
    do! Task.Delay(1000)
    let! status, body = getJson client "/api/sessions"
    lastBody <- body
    if status = 200 then
      use doc = JsonDocument.Parse(body)
      ready <-
        doc.RootElement.GetProperty("sessions").EnumerateArray()
        |> Seq.exists (fun session ->
          let sessionDir = session.GetProperty("workingDirectory").GetString() |> normalizeDir
          sessionDir = expectedDir
          && session.GetProperty("status").GetString() = "Ready")

  return ready, lastBody
}

/// Ensure a Ready session exists for targetDir, creating one if absent.
/// The daemon routes /exec by workingDirectory but does not auto-create,
/// so tests that eval against a directory depend on this.
let ensureSession (client: HttpClient) (projectPath: string) (targetDir: string) = task {
  let! ready, _ = waitForReadySession client targetDir (TimeSpan.FromSeconds 5.0)
  if not ready then
    let payload =
      {| projects = [| projectPath |]
         workingDirectory = targetDir |}
    let! status, body = postJson client "/api/sessions/create" payload
    if status <> 200 then
      failwith (sprintf "session create failed: %d %s" status body)
    let! ready, _ = waitForReadySession client targetDir (TimeSpan.FromSeconds 60.0)
    if not ready then
      failwith "session did not reach Ready"
}

type LiveTestingStatusSnapshot = {
  DiscoveryState: string
  Total: int
  Passed: int
  Failed: int
  Stale: int
  Running: int
  FailedTests: string list
}

let getLiveTestingStatusSnapshot (client: HttpClient) (fileFilter: string option) = task {
  let path =
    match fileFilter with
    | Some file -> sprintf "/api/live-testing/status?file=%s" (Uri.EscapeDataString file)
    | None -> "/api/live-testing/status"

  let! status, body = getJson client path

  if status <> 200 then
    failwith (sprintf "Expected 200 from %s, got %d with body: %s" path status body)

  use doc = JsonDocument.Parse(body)
  let root = doc.RootElement
  let summary = root.GetProperty("Summary")
  let failedTests =
    match root.TryGetProperty("FailedTests") with
    | true, tests ->
      tests.EnumerateArray()
      |> Seq.map (fun entry -> entry.GetProperty("Name").GetString())
      |> Seq.toList
    | false, _ -> []

  let snapshot = {
    DiscoveryState = root.GetProperty("DiscoveryState").GetString()
    Total = summary.GetProperty("Total").GetInt32()
    Passed = summary.GetProperty("Passed").GetInt32()
    Failed = summary.GetProperty("Failed").GetInt32()
    Stale = summary.GetProperty("Stale").GetInt32()
    Running = summary.GetProperty("Running").GetInt32()
    FailedTests = failedTests
  }

  return snapshot, body
}

let waitForLiveTestingStatus
  (client: HttpClient)
  (fileFilter: string option)
  (timeout: TimeSpan)
  (predicate: LiveTestingStatusSnapshot -> bool)
  =
  task {
    let started = DateTime.UtcNow
    let mutable matched = false
    let mutable lastBody = ""
    let mutable lastSnapshot = {
      DiscoveryState = ""
      Total = 0
      Passed = 0
      Failed = 0
      Stale = 0
      Running = 0
      FailedTests = []
    }

    while not matched && DateTime.UtcNow - started < timeout do
      let! snapshot, body = getLiveTestingStatusSnapshot client fileFilter
      lastSnapshot <- snapshot
      lastBody <- body
      matched <- predicate snapshot

      if not matched then
        do! Task.Delay(250)

    return matched, lastSnapshot, lastBody
  }

/// Cleanup daemon process.
let killDaemon (proc: Process) =
  try
    if not proc.HasExited then
      proc.Kill(entireProcessTree = true)
      proc.WaitForExit(5000) |> ignore
  with _ -> ()
  proc.Dispose()

[<Tests>]
let httpApiHarnessTests =
  testList "HTTP API harness" [
    testCase "daemon startup wait budget matches the documented 60 seconds" <| fun _ ->
      daemonStartupHealthTimeout
      |> Expect.equal "startup wait should match documented 60 seconds" (TimeSpan.FromSeconds(60.0))

    testCase "reserveLoopbackPort skips an occupied preferred port" <| fun _ ->
      use occupied = new TcpListener(IPAddress.Loopback, 0)
      occupied.Start()

      let occupiedPort = (occupied.LocalEndpoint :?> IPEndPoint).Port
      let reserved = reserveLoopbackPort (Some occupiedPort)

      (reserved = occupiedPort)
      |> Expect.isFalse "occupied preferred port should not be reused"

      (reserved, 0)
      |> Expect.isGreaterThan "reserved port should be positive"

      use validation = new TcpListener(IPAddress.Loopback, reserved)
      validation.Start()
  ]

// ─── Shared Daemon Fixture ─────────────────────────────────────────
// One daemon shared across all integration tests (saves ~170s of startup).
// Tests run sequenced since they share daemon state.

let private sharedPort = reserveLoopbackPort (Some 38500)

let private sharedDaemon =
  lazy (startDaemon sharedPort |> Async.AwaitTask |> Async.RunSynchronously) // lazy one-time startup, acceptable

let private getSharedClient () = snd sharedDaemon.Value
let private getSharedProc () = fst sharedDaemon.Value

do AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
  if sharedDaemon.IsValueCreated then
    let proc, client = sharedDaemon.Value
    client.Dispose()
    killDaemon proc)

// ─── Tests (shared daemon, sequenced) ─────────────────────────────

[<Tests>]
let integrationTests =
  testSequenced <| testList "[Integration] HTTP API" [

    // ── Core endpoints ──────────────────────────────────────────

    testTask "GET /health returns 200" {
      let client = getSharedClient()
      let! status, body = getJson client "/health"
      status |> Expect.equal "200 OK" 200
      body |> Expect.isNotEmpty "body is not empty"
    }

    testTask "GET /health includes session diagnostics when a session exists" {
      let client = getSharedClient()
      do! ensureSession client webSampleProject testProjectDir
      let payload =
        {| code = "let healthDiagnostics = 42;;"
           working_directory = testProjectDir |}
      let! evalStatus, _ = postJson client "/exec" payload
      evalStatus |> Expect.equal "eval 200" 200

      let! status, body = getJson client "/health"
      status |> Expect.equal "200 OK" 200

      let doc = JsonDocument.Parse(body: string)
      try
        let root = doc.RootElement
        let sessionCount = root.GetProperty("sessionCount").GetInt32()
        Expect.isGreaterThan "has at least one session" (sessionCount, 0)

        let sessionStates =
          root.GetProperty("sessionStates").EnumerateArray()
          |> Seq.toArray

        sessionStates.Length
        |> Expect.equal "sessionStates length matches sessionCount" sessionCount

        let matchingSession =
          sessionStates
          |> Array.tryFind (fun session ->
            let sessionDir = session.GetProperty("workingDirectory").GetString()
            normalizeDir sessionDir = normalizeDir testProjectDir)

        matchingSession |> Expect.isSome "health should include the auto-created test session"

        let summary = root.GetProperty("diagnosticSummary").GetString()
        summary |> Expect.isNotEmpty "diagnostic summary should be populated"
      finally
        doc.Dispose()
    }

    // ── Eval endpoints ──────────────────────────────────────────

    testTask "POST /exec evaluates F# code and returns result" {
      let client = getSharedClient()
      let payload =
        {| code = "1 + 1;;"
           working_directory = testProjectDir |}
      let! status, body = postJson client "/exec" payload
      status |> Expect.equal "200 OK" 200

      let doc = JsonDocument.Parse(body: string)
      let root = doc.RootElement

      root.GetProperty("success").GetBoolean()
      |> Expect.isTrue "eval succeeded"

      root.GetProperty("result").GetString()
      |> Expect.stringContains "result has 2" "2"
      doc.Dispose()
    }

    testTask "POST /exec reports eval failure truthfully (200, success=false)" {
      let client = getSharedClient()
      do! ensureSession client webSampleProject testProjectDir
      let payload =
        {| code = """let x: int = "not an int";;"""
           working_directory = testProjectDir |}
      let! status, body = postJson client "/exec" payload
      // HTTP stays 200 — the request was processed; success reflects the
      // typed worker outcome so a compile failure is distinguishable from
      // a successful eval without string sniffing.
      status |> Expect.equal "200 OK" 200

      let doc = JsonDocument.Parse(body: string)
      let root = doc.RootElement

      root.GetProperty("success").GetBoolean()
      |> Expect.isFalse "eval failed"
      root.GetProperty("result").GetString()
      |> Expect.stringContains "result reports the type error" "expected to have type"
      doc.Dispose()
    }

    testTask "POST /exec routes to existing session by working_directory" {
      let client = getSharedClient()
      do! ensureSession client webSampleProject testProjectDir
      let payload =
        {| code = "let routedEval = true;;"
           working_directory = testProjectDir |}
      let! status, body = postJson client "/exec" payload
      status |> Expect.equal "200 OK" 200

      let doc = JsonDocument.Parse(body: string)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "eval against existing session succeeded"
      doc.Dispose()

      let! sessStatus, sessBody = getJson client "/api/sessions"
      sessStatus |> Expect.equal "sessions 200" 200

      let sessDoc = JsonDocument.Parse(sessBody: string)
      let sessCount = sessDoc.RootElement.GetProperty("sessions").GetArrayLength()
      Expect.isGreaterThan "at least 1 session exists" (sessCount, 0)
      sessDoc.Dispose()
    }

    testTask "Multiple sequential evals maintain session scope" {
      let client = getSharedClient()
      do! ensureSession client webSampleProject testProjectDir
      let p1 = {| code = "let scopeVal = 42;;" ; working_directory = testProjectDir |}
      let! s1, _ = postJson client "/exec" p1
      s1 |> Expect.equal "eval1 200" 200

      let p2 = {| code = "scopeVal * 2;;" ; working_directory = testProjectDir |}
      let! s2, body2 = postJson client "/exec" p2
      s2 |> Expect.equal "eval2 200" 200

      let doc = JsonDocument.Parse(body2: string)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "scope preserved across evals"

      doc.RootElement.GetProperty("result").GetString()
      |> Expect.stringContains "result has 84" "84"
      doc.Dispose()
    }

    // ── Session state queries ───────────────────────────────────

    testTask "GET /api/sessions returns session list" {
      let client = getSharedClient()
      let! status, body = getJson client "/api/sessions"
      status |> Expect.equal "200 OK" 200

      let doc = JsonDocument.Parse(body: string)
      let root = doc.RootElement

      let sessCount = root.GetProperty("sessions").GetArrayLength()
      Expect.isGreaterThanOrEqual "sessions is an array" (sessCount, 0)
      doc.Dispose()
    }

    testTask "POST /exec then GET /api/status shows eval count > 0" {
      let client = getSharedClient()
      do! ensureSession client webSampleProject testProjectDir
      let payload =
        {| code = "let apiTestVal = 42;;"
           working_directory = testProjectDir |}
      let! evalStatus, _ = postJson client "/exec" payload
      evalStatus |> Expect.equal "eval 200" 200

      let! status, body = getJson client "/api/status"
      status |> Expect.equal "status 200" 200

      let doc = JsonDocument.Parse(body: string)
      let root = doc.RootElement

      let evalCount = root.GetProperty("evalCount").GetInt32()
      Expect.isGreaterThan "at least 1 eval" (evalCount, 0)

      root.GetProperty("version").GetString()
      |> Expect.isNotEmpty "has version"

      root.GetProperty("pid").GetInt32()
      |> Expect.equal "pid matches daemon" (getSharedProc().Id)
      doc.Dispose()
    }

    // ── SSE streams ─────────────────────────────────────────────

    testTask "GET /events SSE stream sends at least one event" {
      let client = getSharedClient()
      let cts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0))
      let eventsReceived = System.Collections.Concurrent.ConcurrentBag<string>()

      let sseTask = task {
        try
          use sseClient = new HttpClient()
          sseClient.BaseAddress <- Uri(sprintf "http://localhost:%d" sharedPort)
          sseClient.Timeout <- TimeSpan.FromSeconds(15.0)
          use! stream = sseClient.GetStreamAsync("/events")
          use reader = new StreamReader(stream)

          while not cts.Token.IsCancellationRequested do
            let! line = reader.ReadLineAsync(cts.Token).AsTask()
            if line <> null && line.StartsWith("data:") then
              eventsReceived.Add(line)
              cts.Cancel()
        with
        | :? OperationCanceledException -> ()
        | _ -> ()
      }

      // Let the SSE connection establish before triggering an event.
      do! Task.Delay 200
      let payload = {| code = "1 + 2;;" ; working_directory = testProjectDir |}
      let! _, _ = postJson client "/exec" payload

      try do! sseTask with _ -> ()

      Expect.isGreaterThan "received at least 1 SSE event" (eventsReceived.Count, 0)
      cts.Dispose()
    }

    testTask "GET /diagnostics SSE responds with text/event-stream" {
      let client = getSharedClient()
      let cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
      let req = new HttpRequestMessage(HttpMethod.Get, "/diagnostics")
      let! (resp: HttpResponseMessage) =
        client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
      int resp.StatusCode |> Expect.equal "200 OK" 200
      let ct = resp.Content.Headers.ContentType
      ct.MediaType |> Expect.equal "SSE content type" "text/event-stream"
      cts.Cancel()
      cts.Dispose()
    }

    // ── Extension endpoints ─────────────────────────────────────

    testTask "POST /api/live-testing/enable returns success and message" {
      let client = getSharedClient()
      let! status, body = postJson client "/api/live-testing/enable" {||}
      status |> Expect.equal "200 OK" 200

      let doc = JsonDocument.Parse(body: string)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "enable succeeded"
      doc.RootElement.GetProperty("message").GetString()
      |> Expect.isNotEmpty "has message"
      doc.Dispose()
    }

    testTask "POST /api/live-testing/policy sets unit policy" {
      let client = getSharedClient()
      let payload = {| category = "unit"; policy = "every" |}
      let! status, body = postJson client "/api/live-testing/policy" payload
      status |> Expect.equal "200 OK" 200

      let doc = JsonDocument.Parse(body: string)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "policy set succeeded"
      doc.Dispose()
    }

    testTask "POST /api/live-testing/run returns 409 until tests are discovered" {
      let client = getSharedClient()
      let payload = {| pattern = ""; category = "" |}
      let! status, body = postJson client "/api/live-testing/run" payload

      // Live testing must be enabled and discover tests first. Without that,
      // the endpoint rejects with 409 (no tests discovered yet). This test
      // does not enable live testing, so 409 is the expected contract.
      status |> Expect.equal "no tests discovered => 409" 409
      body |> Expect.isNotEmpty "has error body"

      let doc = JsonDocument.Parse(body: string)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isFalse "run should fail until tests are discovered"
      doc.RootElement.GetProperty("error").GetString()
      |> Expect.stringContains "error explains discovery requirement" "tests discovered"
      doc.Dispose()
    }

    testTask "GET /api/dependency-graph returns TotalSymbols" {
      let client = getSharedClient()
      let! status, body = getJson client "/api/dependency-graph"
      status |> Expect.equal "200 OK" 200

      let doc = JsonDocument.Parse(body: string)
      let root = doc.RootElement
      root.TryGetProperty("TotalSymbols") |> fst
      |> Expect.isTrue "has TotalSymbols"

      let total = root.GetProperty("TotalSymbols").GetInt32()
      Expect.isGreaterThanOrEqual "TotalSymbols >= 0" (total, 0)
      doc.Dispose()
    }

    testTask "GET /api/dependency-graph?symbol=unknown returns empty tests" {
      let client = getSharedClient()
      let! status, body = getJson client "/api/dependency-graph?symbol=NonExistent.symbol"
      status |> Expect.equal "200 OK" 200

      let doc = JsonDocument.Parse(body: string)
      doc.RootElement.GetProperty("Tests").GetArrayLength()
      |> Expect.equal "no tests for unknown symbol" 0
      doc.Dispose()
    }

    testTask "GET /api/recent-events returns content after eval" {
      let client = getSharedClient()
      do! ensureSession client webSampleProject testProjectDir
      let payload =
        {| code = "1 + 1;;"
           working_directory = testProjectDir |}
      let! evalStatus, _ = postJson client "/exec" payload
      evalStatus |> Expect.equal "eval 200" 200

      let! status, body = getJson client "/api/recent-events?count=5"
      status |> Expect.equal "200 OK" 200
      body |> Expect.isNotEmpty "has recent events content"
    }

    // ── Mutations (reset, hard-reset) ───────────────────────────

    testTask "POST /reset resets the session" {
      let client = getSharedClient()
      do! ensureSession client webSampleProject testProjectDir
      let payload = {| code = "let resetTestVal = 1;;" ; working_directory = testProjectDir |}
      let! evalStatus, _ = postJson client "/exec" payload
      evalStatus |> Expect.equal "eval 200" 200

      let! resetStatus, resetBody = postJson client "/reset" {||}
      resetStatus |> Expect.equal "reset 200" 200

      let doc = JsonDocument.Parse(resetBody: string)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "reset succeeded"
      doc.Dispose()
    }

    testTask "POST /reset after eval allows re-eval" {
      let client = getSharedClient()
      do! ensureSession client webSampleProject testProjectDir
      let p1 = {| code = "let resetReeval = 99;;" ; working_directory = testProjectDir |}
      let! _, _ = postJson client "/exec" p1

      let! _, _ = postJson client "/reset" {||}

      let p2 = {| code = "let resetReeval = 42;;" ; working_directory = testProjectDir |}
      let! s2, body2 = postJson client "/exec" p2
      s2 |> Expect.equal "200" 200

      let doc = JsonDocument.Parse(body2: string)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "re-eval after reset succeeded"

      doc.RootElement.GetProperty("result").GetString()
      |> Expect.stringContains "result has 42" "42"
      doc.Dispose()
    }

    // ── Session lifecycle ───────────────────────────────────────

    testTask "POST /api/sessions/create creates a new session" {
      let client = getSharedClient()
      // Create for the smoke sample dir — NOT the web sample dir — so we do
      // not end up with two sessions for the same workingDirectory (which
      // breaks /exec routing with "Multiple sessions match").
      let payload =
        {| projects = [| Path.GetFileName(smokeSampleProject) |]
           workingDirectory = smokeSampleProjectDir |}
      let! status, body = postJson client "/api/sessions/create" payload
      status |> Expect.equal "200 OK" 200

      let doc = JsonDocument.Parse(body: string)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "session created"
      doc.Dispose()
    }

    testTask "POST /api/sessions/switch returns 404 for unknown session" {
      let client = getSharedClient()
      // Session IDs are 8-char lowercase hex; well-formed-but-unknown must 404.
      // Malformed IDs are rejected earlier with 400.
      let! status, body = postJson client "/api/sessions/switch" {| sessionId = "deadbeef" |}
      status |> Expect.equal "404 not found" 404

      let doc = JsonDocument.Parse(body: string)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isFalse "switch should fail for unknown session"
      doc.Dispose()
    }

    testTask "POST /api/sessions/stop stops a session" {
      let client = getSharedClient()
      do! ensureSession client webSampleProject testProjectDir
      let p = {| code = "let stopTest = 1;;" ; working_directory = testProjectDir |}
      let! _, _ = postJson client "/exec" p

      let! _, sessBody = getJson client "/api/sessions"
      let sessDoc = JsonDocument.Parse(sessBody: string)
      let sessions = sessDoc.RootElement.GetProperty("sessions")
      let sessionId =
        if sessions.GetArrayLength() > 0 then
          sessions.[0].GetProperty("id").GetString()
        else failwith "no session found"
      sessDoc.Dispose()

      let! stopStatus, stopBody = postJson client "/api/sessions/stop" {| sessionId = sessionId |}
      stopStatus |> Expect.equal "200" 200

      let stopDoc = JsonDocument.Parse(stopBody: string)
      stopDoc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "session stopped"
      stopDoc.Dispose()
    }

    testTask "POST /hard-reset with rebuild=false succeeds" {
      let client = getSharedClient()
      do! ensureSession client webSampleProject testProjectDir
      let payload = {| code = "let hrTest = 1;;" ; working_directory = testProjectDir |}
      let! evalStatus, _ = postJson client "/exec" payload
      evalStatus |> Expect.equal "eval 200" 200

      let! hrStatus, hrBody =
        postJson client "/hard-reset" {| rebuild = false |}
      hrStatus |> Expect.equal "hard-reset 200" 200

      let doc = JsonDocument.Parse(hrBody: string)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "hard reset succeeded"
      doc.Dispose()
    }
  ]

[<Tests>]
let httpApiRoutingTests =
  testList "[Integration] HTTP API routing" [
    testTask "POST /api/sessions/{sid}/buffer-changed accepts unsaved buffer content" {
      let port = reserveLoopbackPort (Some (38800 + (Random().Next(100))))
      let! proc, client =
        startDaemonWithArgs port repoRoot []
      try
        let! createStatus, createBody =
          createSession client "SageFs.Tests.fsproj" testProjectDir
        createStatus |> Expect.equal "session create should succeed" 200

        let! ready, sessionsBody =
          waitForReadySession client testProjectDir (TimeSpan.FromSeconds 60.0)
        ready
        |> Expect.isTrue (sprintf "session should reach Ready before buffer ingress. Create: %s Sessions: %s" createBody sessionsBody)

        let sessionsDoc = JsonDocument.Parse(sessionsBody: string)
        let sessionId =
          sessionsDoc.RootElement.GetProperty("sessions").EnumerateArray()
          |> Seq.find (fun session ->
            let sessionDir = session.GetProperty("workingDirectory").GetString() |> normalizeDir
            sessionDir = normalizeDir testProjectDir)
          |> fun session -> session.GetProperty("id").GetString()
        sessionsDoc.Dispose()

        let payload =
          {| filePath = Path.Combine(testProjectDir, "Unsaved.fs")
             content = "module Unsaved\nlet value = 42" |}

        let! status, _body =
          postJson client (sprintf "/api/sessions/%s/buffer-changed" (Uri.EscapeDataString sessionId)) payload
        status |> Expect.equal "buffer change accepted" 202
      finally
        client.Dispose()
        killDaemon proc
    }

    testTask "POST /api/sessions/{sid}/buffer-changed returns 404 for unknown session" {
      let port = reserveLoopbackPort (Some (38900 + (Random().Next(100))))
      let! proc, client =
        startDaemonWithArgs port repoRoot []
      try
        let payload =
          {| filePath = Path.Combine(testProjectDir, "Unsaved.fs")
             content = "module Unsaved\nlet value = 42" |}
        let! status, body =
          postJson client "/api/sessions/deadbeef/buffer-changed" payload
        status |> Expect.equal "unknown session rejected" 404

        let doc = JsonDocument.Parse(body: string)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isFalse "unknown session should not be accepted"
        doc.Dispose()
      finally
        client.Dispose()
        killDaemon proc
    }

    testTask "POST /api/completions uses workingDirectory for startup session routing" {
      let port = reserveLoopbackPort (Some (38600 + (Random().Next(100))))
      let! proc, client =
        startDaemonWithArgs port repoRoot []
      try
        let! createStatus, createBody =
          createSession client smokeSampleProject smokeSampleProjectDir
        createStatus |> Expect.equal "session create should succeed" 200

        let! ready, sessionsBody =
          waitForReadySession client smokeSampleProjectDir (TimeSpan.FromSeconds(60.0))

        ready
        |> Expect.isTrue (sprintf "explicitly created session should reach Ready. Create: %s Sessions: %s" createBody sessionsBody)

        let payload =
          {| code = "System."
             cursorPosition = 7
             workingDirectory = smokeSampleProjectDir |}

        let! status, body = postJson client "/api/completions" payload
        status |> Expect.equal "200 OK" 200
        let doc = JsonDocument.Parse(body: string)
        let labels =
          doc.RootElement.GetProperty("completions").EnumerateArray()
          |> Seq.map (fun item -> item.GetProperty("label").GetString())
          |> Seq.toList
        doc.Dispose()
        labels |> List.contains "String"
        |> Expect.isTrue (sprintf "completions should route via workingDirectory, got: %s" body)
      finally
        client.Dispose()
        killDaemon proc
    }

    testTask "POST /api/completions accepts snake_case cursor_position" {
      let port = reserveLoopbackPort (Some (38700 + (Random().Next(100))))
      let! proc, client =
        startDaemonWithArgs port repoRoot []
      try
        let! createStatus, createBody =
          createSession client smokeSampleProject smokeSampleProjectDir
        createStatus |> Expect.equal "session create should succeed" 200

        let! ready, sessionsBody =
          waitForReadySession client smokeSampleProjectDir (TimeSpan.FromSeconds(60.0))

        ready
        |> Expect.isTrue (sprintf "explicitly created session should reach Ready. Create: %s Sessions: %s" createBody sessionsBody)

        let payload =
          {| code = "System."
             cursor_position = 7
             working_directory = smokeSampleProjectDir |}

        let! status, body = postJson client "/api/completions" payload
        status |> Expect.equal "200 OK" 200
        let doc = JsonDocument.Parse(body: string)
        let labels =
          doc.RootElement.GetProperty("completions").EnumerateArray()
          |> Seq.map (fun item -> item.GetProperty("label").GetString())
          |> Seq.toList
        doc.Dispose()
        labels |> List.contains "String"
        |> Expect.isTrue (sprintf "completions should accept snake_case cursor_position, got: %s" body)
      finally
        client.Dispose()
        killDaemon proc
    }

    testTask "WHY — POST /api/completions — a cursor past end-of-string is clamped instead of silently returning zero items because editors and agents routinely send end-relative offsets (smoke-test failure 2026-08)" {
      let port = reserveLoopbackPort (Some (38700 + (Random().Next(100))))
      let! proc, client =
        startDaemonWithArgs port repoRoot []
      try
        let! createStatus, createBody =
          createSession client smokeSampleProject smokeSampleProjectDir
        createStatus |> Expect.equal "session create should succeed" 200

        let! ready, sessionsBody =
          waitForReadySession client smokeSampleProjectDir (TimeSpan.FromSeconds(60.0))

        ready
        |> Expect.isTrue (sprintf "explicitly created session should reach Ready. Create: %s Sessions: %s" createBody sessionsBody)

        // "System." is 7 chars; 99 is far past the end and must clamp to 7.
        let payload =
          {| code = "System."
             cursorPosition = 99
             workingDirectory = smokeSampleProjectDir |}

        let! status, body = postJson client "/api/completions" payload
        status |> Expect.equal "200 OK" 200
        let doc = JsonDocument.Parse(body: string)
        let labels =
          doc.RootElement.GetProperty("completions").EnumerateArray()
          |> Seq.map (fun item -> item.GetProperty("label").GetString())
          |> Seq.toList
        doc.Dispose()
        labels |> List.contains "String"
        |> Expect.isTrue (sprintf "out-of-range cursor should clamp to end-of-string, got: %s" body)
      finally
        client.Dispose()
        killDaemon proc
    }
  ]

[<Tests>]
let httpApiLiveTestingCompiledProjectTests =
  testList "[Integration] HTTP API compiled live testing" [
    testTask "editing a compiled F# file reruns tests against rebuilt output without an explicit rerun" {
      let tempProjectDir = smokeSampleProjectDir
      let tempProjectPath = smokeSampleProject
      let helloPath = Path.Combine(smokeSampleProjectDir, "Hello.fs")
      let canonicalAdd = "let add a b = a + b"
      let brokenAdd = "let add a b = a + b + 1"
      let originalHello = File.ReadAllText(helloPath)
      let baselineHello = originalHello.Replace(brokenAdd, canonicalAdd)
      let editedHello =
        baselineHello.Replace(canonicalAdd, brokenAdd)

      (baselineHello <> editedHello)
      |> Expect.isTrue "sample mutation should change Hello.fs"

      File.WriteAllText(helloPath, baselineHello, utf8NoBom)
      runProcessExpectSuccess
        "dotnet"
        tempProjectDir
        [ "build"
          tempProjectPath
          "--no-restore"
          "--nologo"
          "-v:q" ]

      let port = reserveLoopbackPort (Some (38800 + (Random().Next(100))))
      let! proc, client =
        startDaemonWithArgs port repoRoot [ "--no-resume" ]

      try
        let! createStatus, createBody =
          createSession client tempProjectPath tempProjectDir
        createStatus |> Expect.equal "session create should succeed" 200

        let! ready, sessionsBody =
          waitForReadySession client tempProjectDir (TimeSpan.FromSeconds(60.0))

        ready
        |> Expect.isTrue (
          sprintf
            "compiled sample session should reach Ready. Create: %s Sessions: %s"
            createBody
            sessionsBody)

        let! enableStatus, enableBody =
          postJson client "/api/live-testing/enable" {||}
        enableStatus |> Expect.equal "enable should succeed" 200

        let! policyStatus, policyBody =
          postJson client "/api/live-testing/policy" {| category = "unit"; policy = "every" |}
        policyStatus |> Expect.equal "policy update should succeed" 200

        let! discovered, discoveredSnapshot, discoveredBody =
          waitForLiveTestingStatus client None (TimeSpan.FromSeconds(60.0)) (fun snapshot ->
            snapshot.DiscoveryState = "ready_with_tests"
            && snapshot.Total >= 11)

        discovered
        |> Expect.isTrue (
          sprintf
            "live testing should discover the compiled sample tests. Enable: %s Policy: %s Status: %s Snapshot: %+A"
            enableBody
            policyBody
            discoveredBody
            discoveredSnapshot)

        let! settledAfterDiscovery, settledSnapshot, settledBody =
          waitForLiveTestingStatus client None (TimeSpan.FromSeconds(60.0)) (fun snapshot ->
            snapshot.Total >= 11
            && snapshot.Running = 0)

        settledAfterDiscovery
        |> Expect.isTrue (
          sprintf
            "live testing discovery should settle before baseline evaluation. Status: %s Snapshot: %+A"
            settledBody
            settledSnapshot)

        let autoBaselineReady =
          settledSnapshot.Total >= 11
          && settledSnapshot.Passed >= 11
          && settledSnapshot.Failed = 0
          && settledSnapshot.Running = 0
          && settledSnapshot.Stale = 0

        let mutable runBody = "auto-run"
        let mutable baselineReady = true
        let mutable baselineSnapshot = settledSnapshot
        let mutable baselineBody = settledBody

        if not autoBaselineReady then
          let! runStatus, runBody' =
            postJson client "/api/live-testing/run" {| pattern = ""; category = "" |}
          runStatus |> Expect.equal "baseline run request should succeed" 200
          runBody <- runBody'

          let! ready, snapshot, body =
            waitForLiveTestingStatus client None (TimeSpan.FromSeconds(60.0)) (fun snapshot ->
              snapshot.Total >= 11
              && snapshot.Passed >= 11
              && snapshot.Failed = 0
              && snapshot.Running = 0
              && snapshot.Stale = 0)
          baselineReady <- ready
          baselineSnapshot <- snapshot
          baselineBody <- body

        baselineReady
        |> Expect.isTrue (
          sprintf
            "baseline run should pass before editing the sample. Run: %s Status: %s Snapshot: %+A"
            runBody
            baselineBody
            baselineSnapshot)

        File.WriteAllText(helloPath, editedHello, utf8NoBom)

        let! failedAfterEdit, failedSnapshot, failedBody =
          waitForLiveTestingStatus client None (TimeSpan.FromSeconds(60.0)) (fun snapshot ->
            snapshot.FailedTests
            |> List.exists (fun name -> name = "add infers int"))

        failedAfterEdit
        |> Expect.isTrue (
          sprintf
            "editing Hello.fs should automatically rerun compiled tests and surface the failing add test without an explicit rerun. Status: %s Snapshot: %+A"
            failedBody
            failedSnapshot)

        failedSnapshot.Failed > 0
        |> Expect.isTrue "edited sample should report at least one failing test"
      finally
        try
          File.WriteAllText(helloPath, baselineHello, utf8NoBom)
        with _ -> ()
        client.Dispose()
        killDaemon proc
    }
  ]

// ─── E2E Smoke Test (full Process.Start lifecycle) ────────────────
// Kept per expert guidance: 1 test verifying real daemon startup.

[<Tests>]
let daemonStartupSmokeTest =
  testList "[Integration] Daemon startup smoke" [
    testCase "Daemon starts on fresh port and /health responds" <| fun _ ->
      let port = reserveLoopbackPort (Some (38100 + (Random().Next(100))))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let status, body = getJson client "/health" |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200
        body |> Expect.isNotEmpty "body is not empty"
      finally
        client.Dispose()
        killDaemon proc
  ]
