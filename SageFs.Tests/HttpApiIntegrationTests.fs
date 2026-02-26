module SageFs.Tests.HttpApiIntegrationTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip

// ─── Shared Helpers ───────────────────────────────────────────────

let testProjectDir =
  Path.GetFullPath(
    Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.Tests"))

let sageFsExe =
  let toolDir =
    Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
      ".dotnet", "tools")
  let exe = Path.Combine(toolDir, "SageFs.exe")
  if File.Exists exe then exe else "SageFs"

/// Start a daemon on a given port, wait until /health responds, return (process, HttpClient).
let startDaemon (port: int) = task {
  let psi = ProcessStartInfo()
  psi.FileName <- sageFsExe
  psi.Arguments <- sprintf "--mcp-port %d" port
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- testProjectDir

  let proc = Process.Start(psi)
  let client = new HttpClient()
  client.BaseAddress <- Uri(sprintf "http://localhost:%d" port)
  client.Timeout <- TimeSpan.FromSeconds(30.0)

  // Poll until /health responds (up to 60s)
  let mutable ready = false
  let mutable attempts = 0
  while not ready && attempts < 120 do
    do! Threading.Tasks.Task.Delay(500)
    try
      let! resp = client.GetAsync("/health")
      if int resp.StatusCode > 0 then ready <- true
    with _ -> ()
    attempts <- attempts + 1

  if not ready then
    try proc.Kill() with _ -> ()
    proc.Dispose()
    client.Dispose()
    failwith (sprintf "Daemon failed to start on port %d within 60s" port)

  return proc, client
}

/// POST JSON to a path, return (statusCode, body).
let postJson (client: HttpClient) (path: string) (payload: obj) = task {
  let json = JsonSerializer.Serialize(payload)
  use content = new StringContent(json, Encoding.UTF8, "application/json")
  let! resp = client.PostAsync(path, content)
  let! body = resp.Content.ReadAsStringAsync()
  return int resp.StatusCode, body
}

/// GET a path, return (statusCode, body).
let getJson (client: HttpClient) (path: string) = task {
  let! resp = client.GetAsync(path)
  let! body = resp.Content.ReadAsStringAsync()
  return int resp.StatusCode, body
}

/// Cleanup daemon process.
let killDaemon (proc: Process) =
  try
    if not proc.HasExited then
      proc.Kill(entireProcessTree = true)
      proc.WaitForExit(5000) |> ignore
  with _ -> ()
  proc.Dispose()

// ─── Tests ────────────────────────────────────────────────────────

[<Tests>]
let httpApiTests =
  testList "[Integration] HTTP API (plugin contract)" [

    testCase "GET /health returns 200" <| fun _ ->
      let port = 38100 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let status, body = getJson client "/health" |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200
        body |> Expect.isNotEmpty "body is not empty"
      finally
        client.Dispose()
        killDaemon proc

    testCase "GET /api/system/status returns supervised=false and version" <| fun _ ->
      let port = 38200 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
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
        |> Expect.equal "mcpPort matches" port
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /exec evaluates F# code and returns result" <| fun _ ->
      let port = 38300 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
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
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /exec returns error for invalid code" <| fun _ ->
      let port = 38400 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let payload =
          {| code = """let x: int = "not an int";;"""
             working_directory = testProjectDir |}
        let status, body = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200

        use doc = JsonDocument.Parse(body)
        let root = doc.RootElement

        root.GetProperty("success").GetBoolean()
        |> Expect.isFalse "eval should fail for type error"
      finally
        client.Dispose()
        killDaemon proc

    testCase "GET /api/sessions returns session list" <| fun _ ->
      let port = 38500 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let status, body = getJson client "/api/sessions" |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200

        use doc = JsonDocument.Parse(body)
        let root = doc.RootElement

        // Should have a sessions array (may be empty or have auto-created session)
        let sessCount = root.GetProperty("sessions").GetArrayLength()
        Expect.isGreaterThanOrEqual "sessions is an array" (sessCount, 0)
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /exec then GET /api/status shows eval count > 0" <| fun _ ->
      let port = 38600 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        // First eval some code so there's an active session
        let payload =
          {| code = "let apiTestVal = 42;;"
             working_directory = testProjectDir |}
        let evalStatus, _ = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
        evalStatus |> Expect.equal "eval 200" 200

        // Now check status
        let status, body = getJson client "/api/status" |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "status 200" 200

        use doc = JsonDocument.Parse(body)
        let root = doc.RootElement

        let evalCount = root.GetProperty("evalCount").GetInt32()
        Expect.isGreaterThan "at least 1 eval" (evalCount, 0)

        root.GetProperty("version").GetString()
        |> Expect.isNotEmpty "has version"

        root.GetProperty("pid").GetInt32()
        |> Expect.equal "pid matches daemon" proc.Id
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /reset resets the session" <| fun _ ->
      let port = 38700 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        // Eval code first to create a session
        let payload = {| code = "let resetTestVal = 1;;" ; working_directory = testProjectDir |}
        let evalStatus, _ = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
        evalStatus |> Expect.equal "eval 200" 200

        // Reset the session
        let resetStatus, resetBody = postJson client "/reset" {||} |> Async.AwaitTask |> Async.RunSynchronously
        resetStatus |> Expect.equal "reset 200" 200

        use doc = JsonDocument.Parse(resetBody)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isTrue "reset succeeded"
      finally
        client.Dispose()
        killDaemon proc

    testCase "GET /events SSE stream sends at least one event" <| fun _ ->
      let port = 38800 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        // Start reading SSE stream in background
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0))
        let eventsReceived = System.Collections.Concurrent.ConcurrentBag<string>()

        let sseTask = task {
          try
            use sseClient = new HttpClient()
            sseClient.BaseAddress <- Uri(sprintf "http://localhost:%d" port)
            sseClient.Timeout <- TimeSpan.FromSeconds(15.0)
            use! stream = sseClient.GetStreamAsync("/events")
            use reader = new StreamReader(stream)

            while not cts.Token.IsCancellationRequested do
              let! line = reader.ReadLineAsync(cts.Token).AsTask()
              if line <> null && line.StartsWith("data:") then
                eventsReceived.Add(line)
                // Got at least one event, we can stop
                cts.Cancel()
          with
          | :? OperationCanceledException -> ()
          | _ -> ()
        }

        // Trigger an eval to generate events
        Thread.Sleep(1000) // let SSE connect
        let payload = {| code = "1 + 2;;" ; working_directory = testProjectDir |}
        let _, _ = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously

        // Wait for SSE task to receive events or timeout
        try sseTask |> Async.AwaitTask |> Async.RunSynchronously with _ -> ()

        Expect.isGreaterThan "received at least 1 SSE event" (eventsReceived.Count, 0)
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /exec with working_directory auto-creates session" <| fun _ ->
      let port = 38900 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        // Post /exec should auto-create a session based on working_directory
        let payload =
          {| code = "let autoCreate = true;;"
             working_directory = testProjectDir |}
        let status, body = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200

        use doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isTrue "auto-created session and eval succeeded"

        // Now sessions endpoint should show at least one session
        let sessStatus, sessBody = getJson client "/api/sessions" |> Async.AwaitTask |> Async.RunSynchronously
        sessStatus |> Expect.equal "sessions 200" 200

        use sessDoc = JsonDocument.Parse(sessBody)
        let sessCount = sessDoc.RootElement.GetProperty("sessions").GetArrayLength()
        Expect.isGreaterThan "at least 1 session auto-created" (sessCount, 0)
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /hard-reset with rebuild=false succeeds" <| fun _ ->
      let port = 39000 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        // Create session first
        let payload = {| code = "let hrTest = 1;;" ; working_directory = testProjectDir |}
        let evalStatus, _ = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
        evalStatus |> Expect.equal "eval 200" 200

        // Hard reset without rebuild
        let hrStatus, hrBody =
          postJson client "/hard-reset" {| rebuild = false |}
          |> Async.AwaitTask |> Async.RunSynchronously
        hrStatus |> Expect.equal "hard-reset 200" 200

        use doc = JsonDocument.Parse(hrBody)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isTrue "hard reset succeeded"
      finally
        client.Dispose()
        killDaemon proc

    testCase "Multiple sequential evals maintain session scope" <| fun _ ->
      let port = 39100 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        // Define a value
        let p1 = {| code = "let scopeVal = 42;;" ; working_directory = testProjectDir |}
        let s1, _ = postJson client "/exec" p1 |> Async.AwaitTask |> Async.RunSynchronously
        s1 |> Expect.equal "eval1 200" 200

        // Use the value
        let p2 = {| code = "scopeVal * 2;;" ; working_directory = testProjectDir |}
        let s2, body2 = postJson client "/exec" p2 |> Async.AwaitTask |> Async.RunSynchronously
        s2 |> Expect.equal "eval2 200" 200

        use doc = JsonDocument.Parse(body2)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isTrue "scope preserved across evals"

        doc.RootElement.GetProperty("result").GetString()
        |> Expect.stringContains "result has 84" "84"
      finally
        client.Dispose()
        killDaemon proc
  ]

[<Tests>]
let sessionLifecycleTests =
  testList "[Integration] Session lifecycle" [

    testCase "POST /api/sessions/create creates a new session" <| fun _ ->
      let port = 39200 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let payload =
          {| projects = [| "SageFs.Tests.fsproj" |]
             workingDirectory = testProjectDir |}
        let status, body = postJson client "/api/sessions/create" payload |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200

        use doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isTrue "session created"
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /api/sessions/stop stops a session" <| fun _ ->
      let port = 39300 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
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
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /api/sessions/switch returns 404 for unknown session" <| fun _ ->
      let port = 39400 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let status, body = postJson client "/api/sessions/switch" {| sessionId = "nonexistent-session" |} |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "404 not found" 404

        use doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isFalse "switch should fail for unknown session"
      finally
        client.Dispose()
        killDaemon proc

    testCase "GET /diagnostics SSE responds with text/event-stream" <| fun _ ->
      let port = 39500 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
        let req = new HttpRequestMessage(HttpMethod.Get, "/diagnostics")
        let resp =
          client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
          |> Async.AwaitTask |> Async.RunSynchronously
        int resp.StatusCode |> Expect.equal "200 OK" 200
        let ct = resp.Content.Headers.ContentType
        ct.MediaType |> Expect.equal "SSE content type" "text/event-stream"
        cts.Cancel()
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /reset after eval allows re-eval" <| fun _ ->
      let port = 39600 + (Random().Next(100))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let p1 = {| code = "let resetReeval = 99;;" ; working_directory = testProjectDir |}
        let _, _ = postJson client "/exec" p1 |> Async.AwaitTask |> Async.RunSynchronously

        let _, _ = postJson client "/reset" {||} |> Async.AwaitTask |> Async.RunSynchronously

        let p2 = {| code = "let resetReeval = 42;;" ; working_directory = testProjectDir |}
        let s2, body2 = postJson client "/exec" p2 |> Async.AwaitTask |> Async.RunSynchronously
        s2 |> Expect.equal "200" 200

        use doc = JsonDocument.Parse(body2)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isTrue "re-eval after reset succeeded"
      finally
        client.Dispose()
        killDaemon proc
  ]

[<Tests>]
let vsCodeEndpointTests =
  testList "[Integration] VS Code extension endpoints" [

    testCase "POST /api/live-testing/enable returns success and message" <| fun _ ->
      let port = 39950 + (Random().Next(50))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let status, body = postJson client "/api/live-testing/enable" {||} |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200

        use doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isTrue "enable succeeded"
        doc.RootElement.GetProperty("message").GetString()
        |> Expect.isNotEmpty "has message"
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /api/live-testing/policy sets unit policy" <| fun _ ->
      let port = 40000 + (Random().Next(50))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let payload = {| category = "unit"; policy = "every" |}
        let status, body = postJson client "/api/live-testing/policy" payload |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200

        use doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("success").GetBoolean()
        |> Expect.isTrue "policy set succeeded"
      finally
        client.Dispose()
        killDaemon proc

    testCase "POST /api/live-testing/run returns response" <| fun _ ->
      let port = 40050 + (Random().Next(50))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let payload = {| pattern = ""; category = "" |}
        let status, body = postJson client "/api/live-testing/run" payload |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200
        body |> Expect.isNotEmpty "has response body"
      finally
        client.Dispose()
        killDaemon proc

    testCase "GET /api/dependency-graph returns TotalSymbols" <| fun _ ->
      let port = 40100 + (Random().Next(50))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let status, body = getJson client "/api/dependency-graph" |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200

        use doc = JsonDocument.Parse(body)
        let root = doc.RootElement
        root.TryGetProperty("TotalSymbols") |> fst
        |> Expect.isTrue "has TotalSymbols"

        let total = root.GetProperty("TotalSymbols").GetInt32()
        Expect.isGreaterThanOrEqual "TotalSymbols >= 0" (total, 0)
      finally
        client.Dispose()
        killDaemon proc

    testCase "GET /api/dependency-graph?symbol=unknown returns empty tests" <| fun _ ->
      let port = 40150 + (Random().Next(50))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        let status, body = getJson client "/api/dependency-graph?symbol=NonExistent.symbol" |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200

        use doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("Tests").GetArrayLength()
        |> Expect.equal "no tests for unknown symbol" 0
      finally
        client.Dispose()
        killDaemon proc

    testCase "GET /api/recent-events returns content after eval" <| fun _ ->
      let port = 40200 + (Random().Next(50))
      let proc, client = startDaemon port |> Async.AwaitTask |> Async.RunSynchronously
      try
        // Generate events first
        let payload =
          {| code = "1 + 1;;"
             working_directory = testProjectDir |}
        let evalStatus, _ = postJson client "/exec" payload |> Async.AwaitTask |> Async.RunSynchronously
        evalStatus |> Expect.equal "eval 200" 200

        let status, body = getJson client "/api/recent-events?count=5" |> Async.AwaitTask |> Async.RunSynchronously
        status |> Expect.equal "200 OK" 200
        body |> Expect.isNotEmpty "has recent events content"
      finally
        client.Dispose()
        killDaemon proc
  ]
