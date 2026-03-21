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

let testProjectDir =
  Path.GetFullPath(
    Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.Tests"))

let repoRoot =
  Path.GetFullPath(
    Path.Combine(__SOURCE_DIRECTORY__, ".."))

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
  lazy (startDaemon sharedPort |> Async.AwaitTask |> Async.RunSynchronously)

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
  testSequenced <| ptestList "[Integration] HTTP API" [

    // ── Core endpoints ──────────────────────────────────────────

    testCase "GET /health returns 200" <| fun _ ->
      let client = getSharedClient()
      let status, body = getJson client "/health" |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200
      body |> Expect.isNotEmpty "body is not empty"

    testCase "GET /health includes session diagnostics when a session exists" <| fun _ ->
      let client = getSharedClient()
      let payload =
        {| code = "let healthDiagnostics = 42;;"
           working_directory = testProjectDir |}
      let evalStatus, _ = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
      evalStatus |> Expect.equal "eval 200" 200

      let status, body = getJson client "/health" |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
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

    testCase "GET /health reports Ready when any session is ready" <| fun _ ->
      let client = getSharedClient()
      let status, body = getJson client "/health" |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
      let root = doc.RootElement
      let sessionStates =
        root.GetProperty("sessionStates").EnumerateArray()
        |> Seq.toArray

      let expected =
        sessionStates
        |> Array.map (fun session ->
          match session.GetProperty("status").GetString() with
          | "Ready" -> Some SessionHealthStatus.Ready
          | "Evaluating" -> Some SessionHealthStatus.Evaluating
          | "Starting"
          | "Restarting" -> Some SessionHealthStatus.WarmingUp
          | status when status.StartsWith("Building") -> Some SessionHealthStatus.Evaluating
          | "Faulted" -> Some SessionHealthStatus.Faulted
          | "Stopped" -> Some SessionHealthStatus.Stopped
          | _ -> None)
        |> Array.choose id
        |> Array.map (fun status ->
          { SessionId = ""
            ProjectName = ""
            Status = status
            EvalCount = 0
            LastActivity = DateTimeOffset.UtcNow })
        |> Array.toList
        |> DaemonHealth.primarySessionStatusLabel

      root.GetProperty("status").GetString()
      |> Expect.equal "health status should reflect the strongest available session state" expected
  
    testCase "GET /api/system/status returns supervised=false and version" <| fun _ ->
      let client = getSharedClient()
      let status, body = getJson client "/api/system/status" |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
      let root = doc.RootElement

      root.GetProperty("supervised").GetBoolean()
      |> Expect.isFalse "not supervised (started directly)"

      root.GetProperty("version").GetString()
      |> Expect.isNotEmpty "version is present"

      let pid = root.GetProperty("pid").GetInt32()
      Expect.isGreaterThan "pid is positive" (pid, 0)

      let uptime = root.GetProperty("uptimeSeconds").GetDouble()
      Expect.isGreaterThan "uptime > 0" (uptime, 0.0)

      root.GetProperty("mcpPort").GetInt32()
      |> Expect.equal "mcpPort matches" sharedPort

    // ── Eval endpoints ──────────────────────────────────────────

    testCase "POST /exec evaluates F# code and returns result" <| fun _ ->
      let client = getSharedClient()
      let payload =
        {| code = "1 + 1;;"
           working_directory = testProjectDir |}
      let status, body = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
      let root = doc.RootElement

      root.GetProperty("success").GetBoolean()
      |> Expect.isTrue "eval succeeded"

      root.GetProperty("result").GetString()
      |> Expect.stringContains "result has 2" "2"

    testCase "POST /exec returns error for invalid code" <| fun _ ->
      let client = getSharedClient()
      let payload =
        {| code = """let x: int = "not an int";;"""
           working_directory = testProjectDir |}
      let status, body = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
      let root = doc.RootElement

      root.GetProperty("success").GetBoolean()
      |> Expect.isFalse "eval should fail for type error"

    testCase "POST /exec with working_directory auto-creates session" <| fun _ ->
      let client = getSharedClient()
      let payload =
        {| code = "let autoCreate = true;;"
           working_directory = testProjectDir |}
      let status, body = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "auto-created session and eval succeeded"

      let sessStatus, sessBody = getJson client "/api/sessions" |> Async.AwaitTask |> Async.RunSynchronously
      sessStatus |> Expect.equal "sessions 200" 200

      use sessDoc = JsonDocument.Parse(sessBody)
      let sessCount = sessDoc.RootElement.GetProperty("sessions").GetArrayLength()
      Expect.isGreaterThan "at least 1 session auto-created" (sessCount, 0)

    testCase "Multiple sequential evals maintain session scope" <| fun _ ->
      let client = getSharedClient()
      let p1 = {| code = "let scopeVal = 42;;" ; working_directory = testProjectDir |}
      let s1, _ = postJson client "/exec" p1 |> Async.AwaitTask |> Async.RunSynchronously
      s1 |> Expect.equal "eval1 200" 200

      let p2 = {| code = "scopeVal * 2;;" ; working_directory = testProjectDir |}
      let s2, body2 = postJson client "/exec" p2 |> Async.AwaitTask |> Async.RunSynchronously
      s2 |> Expect.equal "eval2 200" 200

      use doc = JsonDocument.Parse(body2)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "scope preserved across evals"

      doc.RootElement.GetProperty("result").GetString()
      |> Expect.stringContains "result has 84" "84"

    // ── Session state queries ───────────────────────────────────

    testCase "GET /api/sessions returns session list" <| fun _ ->
      let client = getSharedClient()
      let status, body = getJson client "/api/sessions" |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
      let root = doc.RootElement

      let sessCount = root.GetProperty("sessions").GetArrayLength()
      Expect.isGreaterThanOrEqual "sessions is an array" (sessCount, 0)

    testCase "POST /exec then GET /api/status shows eval count > 0" <| fun _ ->
      let client = getSharedClient()
      let payload =
        {| code = "let apiTestVal = 42;;"
           working_directory = testProjectDir |}
      let evalStatus, _ = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
      evalStatus |> Expect.equal "eval 200" 200

      let status, body = getJson client "/api/status" |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "status 200" 200

      use doc = JsonDocument.Parse(body)
      let root = doc.RootElement

      let evalCount = root.GetProperty("evalCount").GetInt32()
      Expect.isGreaterThan "at least 1 eval" (evalCount, 0)

      root.GetProperty("version").GetString()
      |> Expect.isNotEmpty "has version"

      root.GetProperty("pid").GetInt32()
      |> Expect.equal "pid matches daemon" (getSharedProc().Id)

    // ── SSE streams ─────────────────────────────────────────────

    testCase "GET /events SSE stream sends at least one event" <| fun _ ->
      let client = getSharedClient()
      use cts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0))
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

      Thread.Sleep(200) // let SSE connect
      let payload = {| code = "1 + 2;;" ; working_directory = testProjectDir |}
      let _, _ = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously

      try sseTask |> Async.AwaitTask |> Async.RunSynchronously with _ -> ()

      Expect.isGreaterThan "received at least 1 SSE event" (eventsReceived.Count, 0)

    testCase "GET /diagnostics SSE responds with text/event-stream" <| fun _ ->
      let client = getSharedClient()
      use cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
      let req = new HttpRequestMessage(HttpMethod.Get, "/diagnostics")
      let resp =
        client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
        |> Async.AwaitTask |> Async.RunSynchronously
      int resp.StatusCode |> Expect.equal "200 OK" 200
      let ct = resp.Content.Headers.ContentType
      ct.MediaType |> Expect.equal "SSE content type" "text/event-stream"
      cts.Cancel()

    // ── Extension endpoints ─────────────────────────────────────

    testCase "POST /api/live-testing/enable returns success and message" <| fun _ ->
      let client = getSharedClient()
      let status, body = postJson client "/api/live-testing/enable" {||} |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "enable succeeded"
      doc.RootElement.GetProperty("message").GetString()
      |> Expect.isNotEmpty "has message"

    testCase "POST /api/live-testing/policy sets unit policy" <| fun _ ->
      let client = getSharedClient()
      let payload = {| category = "unit"; policy = "every" |}
      let status, body = postJson client "/api/live-testing/policy" payload |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "policy set succeeded"

    testCase "POST /api/live-testing/run returns response" <| fun _ ->
      let client = getSharedClient()
      let payload = {| pattern = ""; category = "" |}
      let status, body = postJson client "/api/live-testing/run" payload |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200
      body |> Expect.isNotEmpty "has response body"

    testCase "GET /api/dependency-graph returns TotalSymbols" <| fun _ ->
      let client = getSharedClient()
      let status, body = getJson client "/api/dependency-graph" |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
      let root = doc.RootElement
      root.TryGetProperty("TotalSymbols") |> fst
      |> Expect.isTrue "has TotalSymbols"

      let total = root.GetProperty("TotalSymbols").GetInt32()
      Expect.isGreaterThanOrEqual "TotalSymbols >= 0" (total, 0)

    testCase "GET /api/dependency-graph?symbol=unknown returns empty tests" <| fun _ ->
      let client = getSharedClient()
      let status, body = getJson client "/api/dependency-graph?symbol=NonExistent.symbol" |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
      doc.RootElement.GetProperty("Tests").GetArrayLength()
      |> Expect.equal "no tests for unknown symbol" 0

    testCase "GET /api/recent-events returns content after eval" <| fun _ ->
      let client = getSharedClient()
      let payload =
        {| code = "1 + 1;;"
           working_directory = testProjectDir |}
      let evalStatus, _ = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
      evalStatus |> Expect.equal "eval 200" 200

      let status, body = getJson client "/api/recent-events?count=5" |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200
      body |> Expect.isNotEmpty "has recent events content"

    // ── Mutations (reset, hard-reset) ───────────────────────────

    testCase "POST /reset resets the session" <| fun _ ->
      let client = getSharedClient()
      let payload = {| code = "let resetTestVal = 1;;" ; working_directory = testProjectDir |}
      let evalStatus, _ = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
      evalStatus |> Expect.equal "eval 200" 200

      let resetStatus, resetBody = postJson client "/reset" {||} |> Async.AwaitTask |> Async.RunSynchronously
      resetStatus |> Expect.equal "reset 200" 200

      use doc = JsonDocument.Parse(resetBody)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "reset succeeded"

    testCase "POST /reset after eval allows re-eval" <| fun _ ->
      let client = getSharedClient()
      let p1 = {| code = "let resetReeval = 99;;" ; working_directory = testProjectDir |}
      let _, _ = postJson client "/exec" p1 |> Async.AwaitTask |> Async.RunSynchronously

      let _, _ = postJson client "/reset" {||} |> Async.AwaitTask |> Async.RunSynchronously

      let p2 = {| code = "let resetReeval = 42;;" ; working_directory = testProjectDir |}
      let s2, body2 = postJson client "/exec" p2 |> Async.AwaitTask |> Async.RunSynchronously
      s2 |> Expect.equal "200" 200

      use doc = JsonDocument.Parse(body2)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "re-eval after reset succeeded"

      doc.RootElement.GetProperty("result").GetString()
      |> Expect.stringContains "result has 42" "42"

    // ── Session lifecycle ───────────────────────────────────────

    testCase "POST /api/sessions/create creates a new session" <| fun _ ->
      let client = getSharedClient()
      let payload =
        {| projects = [| "SageFs.Tests.fsproj" |]
           workingDirectory = testProjectDir |}
      let status, body = postJson client "/api/sessions/create" payload |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "200 OK" 200

      use doc = JsonDocument.Parse(body)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "session created"

    testCase "POST /api/sessions/switch returns 404 for unknown session" <| fun _ ->
      let client = getSharedClient()
      let status, body = postJson client "/api/sessions/switch" {| sessionId = "nonexistent-session" |} |> Async.AwaitTask |> Async.RunSynchronously
      status |> Expect.equal "404 not found" 404

      use doc = JsonDocument.Parse(body)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isFalse "switch should fail for unknown session"

    testCase "POST /api/sessions/stop stops a session" <| fun _ ->
      let client = getSharedClient()
      let p = {| code = "let stopTest = 1;;" ; working_directory = testProjectDir |}
      let _, _ = postJson client "/exec" p |> Async.AwaitTask |> Async.RunSynchronously

      let _, sessBody = getJson client "/api/sessions" |> Async.AwaitTask |> Async.RunSynchronously
      use sessDoc = JsonDocument.Parse(sessBody)
      let sessions = sessDoc.RootElement.GetProperty("sessions")
      let sessionId =
        if sessions.GetArrayLength() > 0 then
          sessions.[0].GetProperty("id").GetString()
        else failwith "no session found"

      let stopStatus, stopBody = postJson client "/api/sessions/stop" {| sessionId = sessionId |} |> Async.AwaitTask |> Async.RunSynchronously
      stopStatus |> Expect.equal "200" 200

      use stopDoc = JsonDocument.Parse(stopBody)
      stopDoc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "session stopped"

    testCase "POST /hard-reset with rebuild=false succeeds" <| fun _ ->
      let client = getSharedClient()
      let payload = {| code = "let hrTest = 1;;" ; working_directory = testProjectDir |}
      let evalStatus, _ = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
      evalStatus |> Expect.equal "eval 200" 200

      let hrStatus, hrBody =
        postJson client "/hard-reset" {| rebuild = false |}
        |> Async.AwaitTask |> Async.RunSynchronously
      hrStatus |> Expect.equal "hard-reset 200" 200

      use doc = JsonDocument.Parse(hrBody)
      doc.RootElement.GetProperty("success").GetBoolean()
      |> Expect.isTrue "hard reset succeeded"
  ]

[<Tests>]
let httpApiRoutingTests =
  testList "[Integration] HTTP API routing" [
    testCase "POST /api/sessions/{sid}/buffer-changed accepts unsaved buffer content" <| fun _ ->
      let port = reserveLoopbackPort (Some (38800 + (Random().Next(100))))
      let proc, client =
        startDaemonWithArgs port repoRoot []
        |> Async.AwaitTask |> Async.RunSynchronously
      try
        let createStatus, createBody =
          createSession client "SageFs.Tests.fsproj" testProjectDir
          |> Async.AwaitTask |> Async.RunSynchronously
        createStatus |> Expect.equal "session create should succeed" 200

        let ready, sessionsBody =
          waitForReadySession client testProjectDir (TimeSpan.FromSeconds 60.0)
          |> Async.AwaitTask |> Async.RunSynchronously
        ready
        |> Expect.isTrue (sprintf "session should reach Ready before buffer ingress. Create: %s Sessions: %s" createBody sessionsBody)

        use sessionsDoc = JsonDocument.Parse(sessionsBody)
        let sessionId =
          sessionsDoc.RootElement.GetProperty("sessions").EnumerateArray()
          |> Seq.find (fun session ->
            let sessionDir = session.GetProperty("workingDirectory").GetString() |> normalizeDir
            sessionDir = normalizeDir testProjectDir)
          |> fun session -> session.GetProperty("id").GetString()

        let payload =
          {| filePath = Path.Combine(testProjectDir, "Unsaved.fs")
             content = "module Unsaved\nlet value = 42" |}

        let status, _body =
          postJson client (sprintf "/api/sessions/%s/buffer-changed" (Uri.EscapeDataString sessionId)) payload
          |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "buffer change accepted" 202
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /api/sessions/{sid}/buffer-changed returns 404 for unknown session" <| fun _ ->
      let port = reserveLoopbackPort (Some (38900 + (Random().Next(100))))
      let proc, client =
        startDaemonWithArgs port repoRoot []
        |> Async.AwaitTask |> Async.RunSynchronously
      try
        let payload =
          {| filePath = Path.Combine(testProjectDir, "Unsaved.fs")
             content = "module Unsaved\nlet value = 42" |}
        let status, body =
          postJson client "/api/sessions/deadbeef/buffer-changed" payload
          |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "unknown session rejected" 404

        use doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isFalse "unknown session should not be accepted"
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /api/completions uses workingDirectory for startup session routing" <| fun _ ->
      let port = reserveLoopbackPort (Some (38600 + (Random().Next(100))))
      let proc, client =
        startDaemonWithArgs port repoRoot []
        |> Async.AwaitTask |> Async.RunSynchronously
      try
        let createStatus, createBody =
          createSession client smokeSampleProject smokeSampleProjectDir
          |> Async.AwaitTask |> Async.RunSynchronously
        createStatus |> Expect.equal "session create should succeed" 200

        let ready, sessionsBody =
          waitForReadySession client smokeSampleProjectDir (TimeSpan.FromSeconds(60.0))
          |> Async.AwaitTask |> Async.RunSynchronously

        ready
        |> Expect.isTrue (sprintf "explicitly created session should reach Ready. Create: %s Sessions: %s" createBody sessionsBody)

        let payload =
          {| code = "System."
             cursorPosition = 7
             workingDirectory = smokeSampleProjectDir |}

        let status, body = postJson client "/api/completions" payload |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200
        body.StartsWith("Error:", StringComparison.Ordinal)
        |> Expect.isFalse (sprintf "completions should route via workingDirectory, got: %s" body)
        body |> Expect.stringContains "should include a System completion" "String"
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /api/completions accepts snake_case cursor_position" <| fun _ ->
      let port = reserveLoopbackPort (Some (38700 + (Random().Next(100))))
      let proc, client =
        startDaemonWithArgs port repoRoot []
        |> Async.AwaitTask |> Async.RunSynchronously
      try
        let createStatus, createBody =
          createSession client smokeSampleProject smokeSampleProjectDir
          |> Async.AwaitTask |> Async.RunSynchronously
        createStatus |> Expect.equal "session create should succeed" 200

        let ready, sessionsBody =
          waitForReadySession client smokeSampleProjectDir (TimeSpan.FromSeconds(60.0))
          |> Async.AwaitTask |> Async.RunSynchronously

        ready
        |> Expect.isTrue (sprintf "explicitly created session should reach Ready. Create: %s Sessions: %s" createBody sessionsBody)

        let payload =
          {| code = "System."
             cursor_position = 7
             working_directory = smokeSampleProjectDir |}

        let status, body = postJson client "/api/completions" payload |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200
        body.StartsWith("Error:", StringComparison.Ordinal)
        |> Expect.isFalse (sprintf "completions should accept snake_case cursor_position, got: %s" body)
        body |> Expect.stringContains "should include a System completion" "String"
      finally
        client.Dispose()
        killDaemon proc
  ]

[<Tests>]
let httpApiLiveTestingCompiledProjectTests =
  testList "[Integration] HTTP API compiled live testing" [
    testCase "editing a compiled F# file reruns tests against rebuilt output without an explicit rerun" <| fun _ ->
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
      let proc, client =
        startDaemonWithArgs port repoRoot [ "--no-resume" ]
        |> Async.AwaitTask |> Async.RunSynchronously

      try
        let createStatus, createBody =
          createSession client tempProjectPath tempProjectDir
          |> Async.AwaitTask |> Async.RunSynchronously
        createStatus |> Expect.equal "session create should succeed" 200

        let ready, sessionsBody =
          waitForReadySession client tempProjectDir (TimeSpan.FromSeconds(60.0))
          |> Async.AwaitTask |> Async.RunSynchronously

        ready
        |> Expect.isTrue (
          sprintf
            "compiled sample session should reach Ready. Create: %s Sessions: %s"
            createBody
            sessionsBody)

        let enableStatus, enableBody =
          postJson client "/api/live-testing/enable" {||}
          |> Async.AwaitTask |> Async.RunSynchronously
        enableStatus |> Expect.equal "enable should succeed" 200

        let policyStatus, policyBody =
          postJson client "/api/live-testing/policy" {| category = "unit"; policy = "every" |}
          |> Async.AwaitTask |> Async.RunSynchronously
        policyStatus |> Expect.equal "policy update should succeed" 200

        let discovered, discoveredSnapshot, discoveredBody =
          waitForLiveTestingStatus client None (TimeSpan.FromSeconds(60.0)) (fun snapshot ->
            snapshot.DiscoveryState = "ready_with_tests"
            && snapshot.Total >= 11)
          |> Async.AwaitTask |> Async.RunSynchronously

        discovered
        |> Expect.isTrue (
          sprintf
            "live testing should discover the compiled sample tests. Enable: %s Policy: %s Status: %s Snapshot: %+A"
            enableBody
            policyBody
            discoveredBody
            discoveredSnapshot)

        let settledAfterDiscovery, settledSnapshot, settledBody =
          waitForLiveTestingStatus client None (TimeSpan.FromSeconds(60.0)) (fun snapshot ->
            snapshot.Total >= 11
            && snapshot.Running = 0)
          |> Async.AwaitTask |> Async.RunSynchronously

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

        let runBody, baselineReady, baselineSnapshot, baselineBody =
          match autoBaselineReady with
          | true ->
              "auto-run", true, settledSnapshot, settledBody
          | false ->
              let runStatus, runBody =
                postJson client "/api/live-testing/run" {| pattern = ""; category = "" |}
                |> Async.AwaitTask |> Async.RunSynchronously
              runStatus |> Expect.equal "baseline run request should succeed" 200

              let baselineReady, baselineSnapshot, baselineBody =
                waitForLiveTestingStatus client None (TimeSpan.FromSeconds(60.0)) (fun snapshot ->
                  snapshot.Total >= 11
                  && snapshot.Passed >= 11
                  && snapshot.Failed = 0
                  && snapshot.Running = 0
                  && snapshot.Stale = 0)
                |> Async.AwaitTask |> Async.RunSynchronously

              runBody, baselineReady, baselineSnapshot, baselineBody

        baselineReady
        |> Expect.isTrue (
          sprintf
            "baseline run should pass before editing the sample. Run: %s Status: %s Snapshot: %+A"
            runBody
            baselineBody
            baselineSnapshot)

        File.WriteAllText(helloPath, editedHello, utf8NoBom)

        let failedAfterEdit, failedSnapshot, failedBody =
          waitForLiveTestingStatus client None (TimeSpan.FromSeconds(60.0)) (fun snapshot ->
            snapshot.FailedTests
            |> List.exists (fun name -> name = "add infers int"))
          |> Async.AwaitTask |> Async.RunSynchronously

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
  ]

// ─── E2E Smoke Test (full Process.Start lifecycle) ────────────────
// Kept per expert guidance: 1 test verifying real daemon startup.

[<Tests>]
let daemonStartupSmokeTest =
  testList "[Integration] Daemon startup smoke" [
    ptestCase "Daemon starts on fresh port and /health responds" <| fun _ ->
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
