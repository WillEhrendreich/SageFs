/// Outcome gate for the honest-empties item: an agent (or a policy engine) can tell "zero" from
/// "unmeasured", and the tool surface it is shown is the one the daemon really has.
///
/// It drives a REAL MCP client against a REAL daemon this file spawns on its own port and data
/// dir, and asserts on what an agent reads: the typed `answer` field of the structured content,
/// and the tool list. Three things it would catch if they came back:
///
///  - an MCP eval that the analysis tools cannot see (they read the daemon-global store while
///    `send_fsharp_code` feeds a per-session one, and the `Result: ` prefix hid the first `val`
///    line of every statement);
///  - `diagnose` answering from the primary live-testing cycle, so it says "No issues detected"
///    while a test failed in another session's run, or reports another session's failure as the
///    caller's;
///  - `discover_features` advertising tools `tools/list` does not have.
module SageFs.Tests.HonestEmptiesOutcomeTests

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
  RepoPaths.repoPath [| "SageFs.Tests"; "fixtures"; "HonestEmptiesFixture" |]

let private fixtureProject = Path.Combine(fixtureDir, "HonestEmptiesFixture.fsproj")

/// The wait, in whole seconds, a status or run call asks the daemon to hold it for.
let private readyWaitSeconds = int TestTimeouts.daemonHeldWait.TotalSeconds

/// The two kinds of answer, as the wire spells them.
[<Literal>]
let private Measured = "Measured"

[<Literal>]
let private NotAvailable = "NotAvailable"

let private connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

/// What a tool answered: the first text block (the second is the events banner) and the typed structured content.
type private Reply = { Text: string; Structured: JsonElement }

let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

/// A tool whose answer is text only (the session tools and `discover_features` return JSON text).
let private callText (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.toolCall)
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
    return textOf result
  }

/// A tool that answers with typed structured content.
let private call (client: McpClient) (name: string) (args: (string * obj) list) : Task<Reply> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.toolCall)
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
    let text = textOf result
    let structured =
      match result.StructuredContent.HasValue with
      | true -> result.StructuredContent.Value.Clone()
      | false -> failtestf "%s returned no structured content. Text: %s" name text
    return { Text = text; Structured = structured }
  }

let private answerKind (r: Reply) = r.Structured.GetProperty("answer").GetString()

let private reasonOf (r: Reply) = r.Structured.GetProperty("reason").GetString()

let private payloadOf (r: Reply) = r.Structured.GetProperty("payload")

let private unmeasuredOf (r: Reply) =
  [ for e in r.Structured.GetProperty("unmeasured").EnumerateArray() -> e.GetString() ]

/// A session's id is the first token a create tool prints.
let private sessionIdOf (createText: string) : string =
  createText.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries).[0].Trim()

/// Create a session and wait, on the daemon's side and without polling, until it is Ready.
let private createReady (client: McpClient) (createTool: string) (args: (string * obj) list) : Task<string> =
  task {
    let! created = callText client createTool args
    let sid = sessionIdOf created
    let! status = callText client "get_session_status" [ "session_id", box sid; "wait_seconds", box readyWaitSeconds ]
    use statusDoc = JsonDocument.Parse status
    statusDoc.RootElement.GetProperty("lifecycle").GetString()
    |> Expect.equal (sprintf "session %s should reach Ready. Create said: %s" sid created) "Ready"
    return sid
  }

let private expectNotAvailable (what: string) (reason: string) (r: Reply) =
  answerKind r |> Expect.equal (sprintf "%s: not measured. Text: %s" what r.Text) NotAvailable
  reasonOf r |> Expect.equal (sprintf "%s: the reason" what) reason
  r.Structured.GetProperty("whatToDo").GetString() |> Expect.isNotEmpty (sprintf "%s: says what to do" what)
  r.Text |> Expect.stringStarts (sprintf "%s: the text is one plain sentence" what) (sprintf "Not available (%s)." reason)

let private expectMeasured (what: string) (r: Reply) =
  answerKind r |> Expect.equal (sprintf "%s: measured. Text: %s" what r.Text) Measured

/// An empty session: every analysis tool says why it cannot answer, and none says zero.
let private emptySessionSays (client: McpClient) (sid: string) : Task<unit> =
  task {
    let session = [ "session_id", box sid ]
    let! deps = call client "get_cell_dependencies" session
    expectNotAvailable "get_cell_dependencies" "NoEvalsYet" deps
    let! impact = call client "impact_forecast" session
    expectNotAvailable "impact_forecast" "NoEvalsYet" impact
    let! next = call client "suggest_next_cell" session
    expectNotAvailable "suggest_next_cell" "NoEvalsYet" next
    let! ripple = call client "plan_ripple" ([ "changed_cells", box "0" ] @ session)
    expectNotAvailable "plan_ripple" "NoEvalsYet" ripple
    let! whatIf = call client "preview_what_if" ([ "binding_name", box "x"; "new_code", box "1" ] @ session)
    expectNotAvailable "preview_what_if" "NoEvalsYet" whatIf
    let! diagnosis = call client "diagnose" session
    expectNotAvailable "diagnose" "NothingObservedYet" diagnosis
    let! coverage = call client "coverage_intel" session
    expectNotAvailable "coverage_intel" "NoTestRunYet" coverage
    let! action = call client "suggest_next_action" session
    expectNotAvailable "suggest_next_action" "NothingObservedYet" action
  }

/// After two MCP evals the same tools measure them.
let private evaluatedSessionMeasures (client: McpClient) (sid: string) : Task<unit> =
  task {
    let session = [ "session_id", box sid ]
    // One send_fsharp_code call is one cell, so the two cells are two calls.
    for code in [ "let a6x = 1;;"; "let a6y = a6x + 1;;" ] do
      let! sent = call client "send_fsharp_code" [ "agentName", box "honest-empties-gate"; "code", box code; "session_id", box sid ]
      sent.Structured.GetProperty("success").GetBoolean() |> Expect.isTrue (sprintf "the eval ran. Text: %s" sent.Text)

    let! deps = call client "get_cell_dependencies" session
    expectMeasured "get_cell_dependencies" deps
    (payloadOf deps).GetProperty("TotalCells").GetInt32() |> Expect.equal "both MCP cells are in the graph" 2
    (payloadOf deps).GetProperty("TotalEdges").GetInt32() |> Expect.equal "the second uses the first" 1
    (payloadOf deps).GetProperty("Staleness").GetString() |> Expect.equal "staleness is said, not zeroed" "NotMeasured"

    let! impact = call client "impact_forecast" session
    expectMeasured "impact_forecast" impact
    let cellZero = [ for row in (payloadOf impact).EnumerateArray() -> row ] |> List.find (fun row -> row.GetProperty("CellId").GetInt32() = 0)
    cellZero.GetProperty("DownstreamCellCount").GetInt32() |> Expect.equal "cell 1 is downstream of cell 0" 1

    let! ripple = call client "plan_ripple" ([ "changed_cells", box "0" ] @ session)
    expectMeasured "plan_ripple" ripple
    ripple.Text |> Expect.stringContains "the ripple names cell 1" "[1] let a6y = a6x + 1"

    let! missing = call client "plan_ripple" ([ "changed_cells", box "41" ] @ session)
    expectNotAvailable "plan_ripple for a cell that does not exist" "CellsNotInHistory" missing

    let! next = call client "suggest_next_cell" session
    expectMeasured "suggest_next_cell" next
    next.Text |> Expect.stringContains "suggests over an MCP binding" "a6x"

    let! whatIf = call client "preview_what_if" ([ "binding_name", box "a6x"; "new_code", box "5" ] @ session)
    expectMeasured "preview_what_if" whatIf
    whatIf.Text |> Expect.stringContains "one cell would re-run" "Affected cells: 1"

    let! unknownBinding = call client "preview_what_if" ([ "binding_name", box "nothing_bound_this"; "new_code", box "5" ] @ session)
    expectNotAvailable "preview_what_if for an unbound name" "BindingNotInScope" unknownBinding
  }

/// `diagnose` over a session with evals and no test run does not call it clean.
let private untestedSessionIsNotClean (client: McpClient) (sid: string) : Task<unit> =
  task {
    let! diagnosis = call client "diagnose" [ "session_id", box sid ]
    expectMeasured "diagnose over evals" diagnosis
    unmeasuredOf diagnosis |> Expect.equal "the test side is named as unmeasured" [ "Tests" ]
    let summary = (payloadOf diagnosis).GetProperty("Summary").GetString()
    summary |> Expect.stringContains "says what it did not measure" "Not measured: tests"
    summary.Contains "No issues detected" |> Expect.isFalse "never the bare all-clear"
    let! action = call client "suggest_next_action" [ "session_id", box sid ]
    expectMeasured "suggest_next_action over evals" action
    unmeasuredOf action |> Expect.equal "suggest_next_action names the unmeasured tests too" [ "Tests" ]
  }

/// `discover_features` lists exactly the tools `tools/list` has.
let private discoveryMatchesToolList (client: McpClient) (sid: string) : Task<unit> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.toolCall)
    let! listed = client.ListToolsAsync(cancellationToken = cts.Token)
    let registered = listed |> Seq.map (fun t -> t.Name) |> Set.ofSeq

    let! discovery = callText client "discover_features" [ "session_id", box sid ]
    use doc = JsonDocument.Parse discovery
    let advertised = [ for s in doc.RootElement.GetProperty("Suggestions").EnumerateArray() -> s.GetProperty("ToolName").GetString() ]

    advertised |> Set.ofList |> Expect.equal "discover_features advertises exactly the registered tools" registered
    advertised |> Expect.hasLength "each tool once" registered.Count
    doc.RootElement.GetProperty("TotalKnownFeatures").GetInt32() |> Expect.equal "the count is the registered count" registered.Count
    doc.RootElement.GetProperty("ContextSummary").GetString()
    |> Expect.stringContains "ranked by this session's two evals" "2 evals"
    SageFs.Affordances.RetiredTool.toolNames
    |> List.iter (fun retired -> registered.Contains retired |> Expect.isFalse (sprintf "%s is retired and must not be listed" retired))
  }

/// A failing run in one session is that session's, and only that session's.
let private failureBelongsToItsSession (client: McpClient) (bareSid: string) (fixtureSid: string) : Task<unit> =
  task {
    let! run = call client "run_tests" [ "session_id", box fixtureSid; "wait_seconds", box readyWaitSeconds ]
    run.Structured.GetProperty("verdict").GetString() |> Expect.equal (sprintf "the fixture has one failing test. Text: %s" run.Text) "SomeFailed"

    let! own = call client "diagnose" [ "session_id", box fixtureSid ]
    expectMeasured "diagnose in the session that ran the test" own
    (payloadOf own).GetProperty("FailureCount").GetInt32() |> Expect.equal "its failing test is reported" 1
    [ for f in (payloadOf own).GetProperty("Failures").EnumerateArray() -> f.GetProperty("TestName").GetString() ]
    |> List.exists (fun name -> name.Contains "add is broken on purpose")
    |> Expect.isTrue (sprintf "the failure is named. Text: %s" own.Text)

    let! other = call client "diagnose" [ "session_id", box bareSid ]
    expectMeasured "diagnose in the session that ran no test" other
    (payloadOf other).GetProperty("FailureCount").GetInt32() |> Expect.equal "the other session's failure is not this one's" 0
    unmeasuredOf other |> Expect.equal "its tests are unmeasured, not clean" [ "Tests" ]

    // suggest_next_action ranks over the same session's cycle: the fixture's failure is its own, and the bare session's queue has none.
    let! ownActions = call client "suggest_next_action" [ "session_id", box fixtureSid ]
    expectMeasured "suggest_next_action in the session that ran the test" ownActions
    (payloadOf ownActions).GetProperty("TotalFailures").GetInt32() |> Expect.equal "its own failing test is ranked" 1
    let! otherActions = call client "suggest_next_action" [ "session_id", box bareSid ]
    expectMeasured "suggest_next_action in the session that ran no test" otherActions
    (payloadOf otherActions).GetProperty("TotalFailures").GetInt32() |> Expect.equal "the other session's failure is not ranked here" 0

    let! coverage = call client "coverage_intel" [ "session_id", box fixtureSid ]
    answerKind coverage |> Expect.equal (sprintf "a failing test and no coverage is not an empty list. Text: %s" coverage.Text) NotAvailable
    [ "NeedsAWorkflow"; "NeedsLiveTesting"; "NoCoverageRecorded" ]
    |> Expect.contains (sprintf "the reason says which switch is missing. Text: %s" coverage.Text) (reasonOf coverage)
  }

/// An eval sent the way an editor sends it (POST /exec, which VS Code and Neovim use) is in the session's
/// own history, so the analysis tools see it beside the two MCP evals.
let private editorEvalIsVisible (client: McpClient) (http: System.Net.Http.HttpClient) (bareDir: string) (sid: string) : Task<unit> =
  task {
    let! status, body = Http.postJson http "/exec" {| code = "let edQ = 9;;"; working_directory = bareDir |}
    status |> Expect.equal (sprintf "the editor's eval was accepted. Body: %s" body) 200
    let! deps = call client "get_cell_dependencies" [ "session_id", box sid ]
    expectMeasured "get_cell_dependencies after an editor eval" deps
    (payloadOf deps).GetProperty("TotalCells").GetInt32()
    |> Expect.equal "the editor's cell is in the graph beside the two MCP cells" 3
  }

/// Every session row on the daemon's two JSON session lists carries both facts about the build, each under its own name:
/// whether the REPL is behind the app (`replFreshness`) and whether the files are ahead of the build (`sourceState`).
let private sourceStates = [ "InSync"; "Stale"; "Rebuilding"; "Unknown" ]

let private rowsCarryBothFacts (what: string) (rows: JsonElement list) =
  rows |> Expect.isNonEmpty (sprintf "%s lists the sessions this gate made" what)
  for row in rows do
    row.GetProperty("replFreshness").GetProperty("state").GetString() |> Expect.isNotEmpty (sprintf "%s: the REPL freshness is there" what)
    sourceStates |> Expect.contains (sprintf "%s: the source state is one of the closed states" what) (row.GetProperty("sourceState").GetProperty("state").GetString())

let private sessionsSurfacesCarryBothFacts (client: McpClient) (http: System.Net.Http.HttpClient) : Task<unit> =
  task {
    let! status, body = Http.getJson http "/api/sessions"
    status |> Expect.equal "the session list answers" 200
    use apiDoc = JsonDocument.Parse(body: string)
    rowsCarryBothFacts "/api/sessions" [ for row in apiDoc.RootElement.GetProperty("sessions").EnumerateArray() -> row ]

    use cts = new CancellationTokenSource(TestTimeouts.toolCall)
    let! resource = client.ReadResourceAsync("sessions://list", cancellationToken = cts.Token)
    let text = resource.Contents |> Seq.choose (function :? TextResourceContents as t -> Some t.Text | _ -> None) |> Seq.head
    use resourceDoc = JsonDocument.Parse text
    rowsCarryBothFacts "sessions://list" [ for row in resourceDoc.RootElement.GetProperty("sessions").EnumerateArray() -> row ]
  }

/// The whole gate, in small tasks. Each is its own state machine on purpose: one large task over
/// all of it made a Release build emit IL the runtime rejected (see McpToolOutcomeTests.fs).
let private runGate (client: McpClient) (http: System.Net.Http.HttpClient) (bareDir: string) : Task<unit> =
  task {
    let! bare = createReady client "create_bare_session" [ "working_directory", box bareDir ]
    do! emptySessionSays client bare
    do! evaluatedSessionMeasures client bare
    do! untestedSessionIsNotClean client bare
    do! discoveryMatchesToolList client bare
    let! fixtureSid =
      createReady client "create_project_session" [ "project", box fixtureProject; "working_directory", box fixtureDir ]
    do! failureBelongsToItsSession client bare fixtureSid
    do! editorEvalIsVisible client http bareDir bare
    do! sessionsSurfacesCarryBothFacts client http
  }

let private runHonestEmptiesGate () : Task<unit> =
  task {
    Http.runProcessExpectSuccess "dotnet" fixtureDir [ "build"; fixtureProject; "--nologo"; "-v:q" ]
    let bareDir = Directory.CreateTempSubdirectory("sagefs-honest-empties-").FullName
    let port = Http.reserveLoopbackPort ()
    let! proc, httpClient = Http.startDaemonWithArgs port bareDir [ "--no-resume" ]
    let! client = connect port

    // No try/finally around an await: observe the outcome through a continuation, clean up, then re-raise.
    let! outcome =
      (runGate client httpClient bareDir)
        .ContinueWith(fun (t: Task<unit>) ->
          match t.IsFaulted with
          | true -> Error(t.Exception :> exn)
          | false -> Ok())

    try (client :> IAsyncDisposable).DisposeAsync() |> ignore with _ -> ()
    httpClient.Dispose()
    Http.killDaemon proc
    try Directory.Delete(bareDir, true) with _ -> ()

    match outcome with
    | Error e -> raise e
    | Ok() -> ()
  }

[<Tests>]
let honestEmptiesOutcomeTests =
  Integration.hostList "Honest empties outcome gate" [
    testTask "WHY — the analysis tools answer for the session asked about, say 'not available' instead of zero, and discover_features lists exactly the registered tools" {
      do! runHonestEmptiesGate ()
    }
  ]
