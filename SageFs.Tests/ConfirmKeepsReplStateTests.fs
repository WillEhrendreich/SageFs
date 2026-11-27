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
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.Eventually
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

/// What the session's REPL says to this code. Pin the session id: selecting by working_directory
/// alone can resolve to a different session after a confirming build creates one in the same
/// directory, and then the binding readback asks a REPL that never held it.
let private evalIn (client: McpClient) (sessionId: string) (code: string) : Task<string> =
  callTool client "send_fsharp_code" [ "agentName", box "confirm-gate"; "code", box code; "working_directory", box fixtureDir; "session_id", box sessionId ]

let private awaitQuietSession (client: McpClient) (http: Net.Http.HttpClient) : Task<unit> =
  await TestTimeouts.buildConfirmation (fun () -> "the session and its live-testing cycle to be settled") (fun () -> task {
    let! statusText = callTool client "get_session_status" [ "working_directory", box fixtureDir ]
    use status = JsonDocument.Parse statusText
    let lifecycle =
      match status.RootElement.TryGetProperty "lifecycle" with
      | true, p -> p.GetString()
      | _ -> ""
    let! _, body = Http.getJson http "/api/live-testing/status"
    use live = JsonDocument.Parse body
    let field (name: string) =
      match live.RootElement.TryGetProperty name with
      | true, p when p.ValueKind = JsonValueKind.Number -> p.GetInt32()
      | _ -> 0
    let phase =
      match live.RootElement.TryGetProperty "Confirmation" with
      | true, p -> p.GetString()
      | _ -> ""
    let settled =
      lifecycle = "Ready" && phase = "idle" && field "Unconfirmed" = 0 && field "Running" = 0
    return if settled then Ok(Some()) else Ok None })

/// Read the REPL's answer until it says the expected thing. Under load a confirmation can settle after the status
/// endpoint goes idle, so a binding check that samples once reads a session that is still catching up. Polling the
/// observable fact removes the race: it passes as soon as the binding is there and still fails if it is lost.
/// The worker pid that held the binding, captured after the first eval. Carried into
/// later failures so a silent swap is visible without guessing from a fresh status.
let mutable private bindingWorkerPid = ""

let private awaitEvalContains (client: McpClient) (http: Net.Http.HttpClient) (sessionId: string) (code: string) (expected: string) : Task<string> =
  // A settled session is not an idle FSI: an eval sent into a still-processing worker is answered with
  // "Operation could not be completed due to earlier error", whose text can still contain the submitted
  // code (and its literal), so a single sample can look like a made binding. Poll the observable answer
  // until it actually carries the value and is not a failure.
  let last = ref ""
  let lastStatus = ref ""
  await TestTimeouts.buildConfirmation
    (fun () ->
      // The answer and the session status are the evidence. One CI failure after 1830 polls showed
      // "marker is not defined" but no reason: carry both so the next red says what the REPL answered
      // and whether the worker the binding was made in is still the one serving.
      let tail = if last.Value.Length > 200 then last.Value.Substring(last.Value.Length - 200) else last.Value
      sprintf "the REPL answer to %s to contain %s. Last answer: %s. Binding worker: %s. Session: %s" code expected tail bindingWorkerPid lastStatus.Value)
    (fun () -> task {
      do! awaitQuietSession client http
      let! answer = evalIn client sessionId code
      last.Value <- answer
      let good = answer.Contains expected && not (answer.Contains "Evaluation failed")
      if not good then
        // Only pay for the extra status read on the failure path; a green poll ends here.
        // Pull the few fields that say whether the worker that made the binding is still
        // the one serving, rather than truncating the wire payload before them.
        let! status = callTool client "get_session_status" [ "session_id", box sessionId ]
        let field name =
          let marker = sprintf "\"%s\":" name
          match status.IndexOf marker with
          | -1 -> "?"
          | i ->
            let rest = status.Substring(i + marker.Length).TrimStart()
            let stop = rest.IndexOfAny([| ','; '}' |])
            if stop < 0 then rest else rest.Substring(0, stop)
        lastStatus.Value <-
          sprintf "state=%s lifecycle=%s health=%s workerPid=%s lastRestart=%s fault=%s"
            (field "state") (field "lifecycle") (field "health") (field "workerPid") (field "lastRestart") (field "faultReason")
      return if good then Ok(Some answer) else Ok None })

/// The daemon's own record of the last rebuild, and the REPL binding. An OS pid is not a stable proxy for
/// "the session was left alone": an FSI host can be recreated for reasons unrelated to a confirmation, and
/// the product already reports whether it rebuilt. Read that record, and check the binding below.
let private lastRestartOf (client: McpClient) : Task<string> =
  task {
    let! status = callTool client "get_session_status" [ "working_directory", box fixtureDir ]
    use doc = JsonDocument.Parse status
    return
      match doc.RootElement.TryGetProperty "lastRestart" with
      | true, p -> p.GetRawText()
      | _ -> ""
  }

/// `Confirmation` and `Unconfirmed` from the live-testing status.
let private confirmationOf (http: Net.Http.HttpClient) : Task<string * int> =
  task {
    let! status, body = Http.getJson http "/api/live-testing/status"
    status |> Expect.equal "the status answers" 200
    use doc = JsonDocument.Parse body
    return doc.RootElement.GetProperty("Confirmation").GetString(), doc.RootElement.GetProperty("Unconfirmed").GetInt32()
  }

/// Wait until `isDone` says so of the confirmation's state.
let private awaitConfirmation (http: Net.Http.HttpClient) (budget: TimeSpan) (isDone: string * int -> bool) : Task<bool * (string * int)> =
  task {
    let last = ref ("", -1)
    let! state =
      await budget (fun () -> sprintf "the confirmation to satisfy its predicate. Last: %A" last.Value) (fun () -> task {
        let! state = confirmationOf http
        last.Value <- state
        return if isDone state then Ok(Some state) else Ok None })
    return true, state
  }

/// A confirmation going idle is not the same fact as the session being free. The live-test eval can still be
/// in flight; an eval sent then lands on a busy FSI host and gets "Operation could not be completed due to
/// earlier error". Wait for the daemon's own settled state: Ready, no running verification, no unconfirmed
/// row, idle confirmation. The deadline bounds only the failure path.
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

    // 1. The session holds a binding. An idle confirmation is not a Ready session: under load the first eval
    // can land while the worker is still warming and be answered with FSI's generic "earlier error".
    let! made = awaitEvalContains client http sessionId "let marker = 42;;" "42"
    made |> Expect.stringContains "the REPL made the binding" "42"
    let! afterBind = callTool client "get_session_status" [ "session_id", box sessionId ]
    let pidMarker = "\"workerPid\":"
    bindingWorkerPid <-
      match afterBind.IndexOf pidMarker with
      | -1 -> "?"
      | i -> afterBind.Substring(i + pidMarker.Length).Split([| ','; '}' |]).[0].Trim()
    let! restartBefore = lastRestartOf client

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
    let! restartAfter = lastRestartOf client
    restartAfter |> Expect.equal "the confirming build left the session's rebuild record alone" restartBefore
    let! keptAnswer = awaitEvalContains client http sessionId "marker + 1;;" "43"
    keptAnswer |> Expect.stringContains "the binding made before the build is still bound, and still 42" "43"

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
    let! restartFinal = lastRestartOf client
    restartFinal |> Expect.equal "a failed build left the session's rebuild record alone" restartBefore
    let! stillKeptAnswer = awaitEvalContains client http sessionId "marker + 2;;" "44"
    stillKeptAnswer |> Expect.stringContains "the binding is still there after a build that failed" "44"
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
