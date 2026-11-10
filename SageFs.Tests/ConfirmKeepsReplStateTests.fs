/// Outcome gate for "Confirm builds without restarting the worker"
/// (docs/plans/confirm-builds-without-restarting-the-worker.md).
///
/// A confirming build used to replace the session's worker, so a binding made in the REPL, and everything else the
/// session held, went with it. The gate drives a REAL daemon, a REAL session on a REAL compiled project, and a REAL
/// confirming build, and reads the binding back afterwards:
///
///   1. the session holds a binding (`let marker = 42`, evaluated through the MCP tool);
///   2. an edit that stays green is saved, the session evaluates it, and a real build confirms it: every row ends
///      `VerifiedByBuild`, the worker is the same process, and `marker` is still 42;
///   3. an edit the session accepts and the compiler rejects is saved: the rows say the build failed, with the
///      compiler's code, the worker is still the same process, and `marker` is still bound.
///
/// Its own fixture (`SageFs.Tests/fixtures/ConfirmKeepsReplFixture`), so no other host case edits the file it waits on.
module SageFs.Tests.ConfirmKeepsReplStateTests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

module Integration = SageFs.Tests.TestInfrastructure.Integration
module Http = SageFs.Tests.HttpApiIntegrationTests

let private fixtureDir =
  RepoPaths.repoPath [| "SageFs.Tests"; "fixtures"; "ConfirmKeepsReplFixture" |]

let private fixtureProject = Path.Combine(fixtureDir, "ConfirmKeepsReplFixture.fsproj")

let private samplePath = Path.Combine(fixtureDir, "Sample.fs")

let private addLine = "let add a b = a + b"

/// The tool's own answer: the first text block (a second one carries the events since the caller's last call).
let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

let private connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

let private callTool (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.toolCall)
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
    return textOf result
  }

/// What the session's REPL says to this code.
let private evalIn (client: McpClient) (code: string) : Task<string> =
  callTool client "send_fsharp_code" [ "agentName", box "confirm-gate"; "code", box code; "working_directory", box fixtureDir ]

/// The pid of the session's worker.
let private workerPid (client: McpClient) : Task<int> =
  task {
    let! status = callTool client "get_session_status" [ "working_directory", box fixtureDir ]
    use doc = JsonDocument.Parse status
    return doc.RootElement.GetProperty("workerPid").GetInt32()
  }

/// `Confirmation` and `Unconfirmed` from the live-testing status: where the confirmation stands, and how many rows still
/// say they ran against evaluated code.
let private confirmationOf (http: Net.Http.HttpClient) : Task<string * int> =
  task {
    let! status, body = Http.getJson http "/api/live-testing/status"
    status |> Expect.equal "the status answers" 200
    use doc = JsonDocument.Parse body
    return doc.RootElement.GetProperty("Confirmation").GetString(), doc.RootElement.GetProperty("Unconfirmed").GetInt32()
  }

/// Wait until `isDone` says so of the confirmation's state, asking again after each pause between polls.
let private awaitConfirmation (http: Net.Http.HttpClient) (budget: TimeSpan) (isDone: string * int -> bool) : Task<bool * (string * int)> =
  task {
    let clock = Diagnostics.Stopwatch.StartNew()
    let mutable last = ("", -1)
    let mutable finished = false
    while not finished && clock.Elapsed < budget do
      let! state = confirmationOf http
      last <- state
      finished <- isDone state
      if not finished then do! Task.Delay LiveTestingBudgets.pollInterval
    return finished, last
  }

/// Each row's name with the case its provenance is in, and the provenance's own words.
let private provenances (http: Net.Http.HttpClient) (sessionId: string) : Task<(string * string * string) list> =
  task {
    let! status, body = Http.getJson http (sprintf "/api/live-testing/entries?session=%s" (Uri.EscapeDataString sessionId))
    status |> Expect.equal "the entries answer" 200
    use doc = JsonDocument.Parse body
    return
      [ for entry in doc.RootElement.EnumerateArray() ->
          let provenance = entry.GetProperty "Provenance"
          let fields =
            match provenance.TryGetProperty "Fields" with
            | true, f -> f.GetRawText()
            | false, _ -> ""
          entry.GetProperty("DisplayName").GetString(), provenance.GetProperty("Case").GetString(), fields ]
  }

let private sessionIdOf (http: Net.Http.HttpClient) : Task<string> =
  task {
    let! _, body = Http.getJson http "/api/sessions"
    use doc = JsonDocument.Parse body
    return doc.RootElement.GetProperty("sessions").EnumerateArray() |> Seq.head |> fun s -> s.GetProperty("id").GetString()
  }

let private body (client: McpClient) (http: Net.Http.HttpClient) (baseline: string) : Task<unit> =
  task {
    let! _ = callTool client "create_project_session" [ "project", box fixtureProject; "working_directory", box fixtureDir; "workflow", box "livetesting" ]
    let! ready, sessionsBody = Http.waitForReadySession http fixtureDir LiveTestingBudgets.baselineRun
    ready |> Expect.isTrue (sprintf "the fixture session reaches Ready. Sessions: %s" sessionsBody)
    let! enableStatus, _ = Http.postJson http "/api/live-testing/enable" {||}
    enableStatus |> Expect.equal "live testing is enabled" 200
    let! policyStatus, _ = Http.postJson http "/api/live-testing/policy" {| category = "unit"; policy = "every" |}
    policyStatus |> Expect.equal "the policy is set" 200
    let! baselineReady, _, baselineBody =
      Http.waitForLiveTestingStatus http None LiveTestingBudgets.baselineRun (fun s ->
        s.Total >= 3 && s.Passed >= 3 && s.Failed = 0 && s.Running = 0 && s.Stale = 0)
    baselineReady |> Expect.isTrue (sprintf "the baseline run passes before anything is edited. Status: %s" baselineBody)
    let! sessionId = sessionIdOf http
    let! restedBefore, restedState = awaitConfirmation http TestTimeouts.buildConfirmation (fun (phase, _) -> phase = "idle")
    restedBefore |> Expect.isTrue (sprintf "the session is at rest before the gate starts. Last: %A" restedState)

    // 1. The session holds a binding.
    let! made = evalIn client "let marker = 42;;"
    made |> Expect.stringContains "the REPL made the binding" "42"
    let! pidBefore = workerPid client

    // 2. A green edit is evaluated, then confirmed by a real build, and the binding survives it.
    File.WriteAllText(samplePath, baseline.Replace(addLine, addLine + " + 0"), Http.utf8NoBom)
    let! opened, openedState = awaitConfirmation http TestTimeouts.buildConfirmation (fun (phase, unconfirmed) -> phase <> "idle" || unconfirmed > 0)
    opened |> Expect.isTrue (sprintf "the edit is evaluated and a confirmation opens. Last: %A" openedState)
    let! confirmed, confirmedState = awaitConfirmation http TestTimeouts.buildConfirmation (fun (phase, unconfirmed) -> phase = "idle" && unconfirmed = 0)
    confirmed |> Expect.isTrue (sprintf "the confirmation ends with no row left unconfirmed. Last: %A" confirmedState)
    let! rows = provenances http sessionId
    rows |> Expect.isNonEmpty "the session has rows"
    rows
    |> List.filter (fun (_, case, _) -> case <> "VerifiedByBuild")
    |> Expect.isEmpty (sprintf "every row is confirmed by the real build. Rows: %A" rows)
    let! pidAfter = workerPid client
    pidAfter |> Expect.equal "the confirming build left the session's worker alone" pidBefore
    let! kept = evalIn client "marker + 1;;"
    kept |> Expect.stringContains "the binding made before the build is still bound, and still 42" "43"

    // 3. An edit the session accepts and the compiler rejects: the build fails, the rows say so, nothing is lost.
    let forward = addLine + " - 0\nlet reachesForward = SageFs.Tests.Fixtures.ConfirmKeepsRepl.Program.main"
    File.WriteAllText(samplePath, baseline.Replace(addLine, forward), Http.utf8NoBom)
    let! opened2, opened2State = awaitConfirmation http TestTimeouts.buildConfirmation (fun (phase, unconfirmed) -> phase <> "idle" || unconfirmed > 0)
    opened2 |> Expect.isTrue (sprintf "the second edit opens a confirmation. Last: %A" opened2State)
    let! ended, endedState = awaitConfirmation http TestTimeouts.buildConfirmation (fun (phase, unconfirmed) -> phase = "idle" && unconfirmed = 0)
    ended |> Expect.isTrue (sprintf "the second confirmation ends. Last: %A" endedState)
    let! failedRows = provenances http sessionId
    failedRows
    |> List.exists (fun (_, case, fields) -> case = "BuildDisagrees" && fields.Contains "FS0039")
    |> Expect.isTrue (sprintf "a row says the real build failed, with the compiler's code. Rows: %A" failedRows)
    let! pidFinal = workerPid client
    pidFinal |> Expect.equal "a failed build left the session's worker alone" pidBefore
    let! stillKept = evalIn client "marker + 2;;"
    stillKept |> Expect.stringContains "the binding is still there after a build that failed" "44"
  }

let private gate () : Task<unit> =
  task {
    let original = File.ReadAllText samplePath
    File.WriteAllText(samplePath, original, Http.utf8NoBom)
    (original.Contains addLine) |> Expect.isTrue "the fixture has the line the gate edits"
    Http.runProcessExpectSuccess "dotnet" fixtureDir [ "build"; fixtureProject; "--nologo"; "-v:q" ]
    let port = Http.reserveLoopbackPort ()
    let! proc, http = Http.startDaemonWithArgs port fixtureDir [ "--no-resume" ]
    let! client = connect port
    // Observe the outcome without a try/finally in the computation expression (see McpToolOutcomeTests for why).
    let! outcome =
      (body client http original)
        .ContinueWith(fun (t: Task<unit>) ->
          match t.IsFaulted with
          | true -> Error (t.Exception :> exn)
          | false -> Ok ())
    try File.WriteAllText(samplePath, original, Http.utf8NoBom) with _ -> ()
    try (client :> IAsyncDisposable).DisposeAsync() |> ignore with _ -> ()
    http.Dispose()
    Http.killDaemon proc
    match outcome with
    | Error e -> raise e
    | Ok () -> ()
  }

[<Tests>]
let confirmKeepsReplStateTests =
  // Waits on a live worker, so it joins the group those suites share and never overlaps another one.
  testSequencedGroup LiveTestingWorkerSuites.groupName <|
    Integration.hostList "Confirming build keeps the REPL" [
      testTask "WHY — a confirming build used to replace the worker, and a binding made in the REPL went with it" {
        do! gate ()
      }
    ]
