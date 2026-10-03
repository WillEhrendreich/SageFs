/// Outcome gate: a pending Expecto test is skipped in a `run_tests` receipt, never passed.
///
/// A real daemon this file spawns on its own port and data dir, a real compiled Expecto project
/// (fixtures/ExpectoPendingFixture: two passing tests and one `ptest` whose body would pass), and
/// the `run_tests` tool through a real MCP client. Before the executor read a test's state, the
/// receipt said "3 passed ... AllPassed" for this project. Expecto's own runner calls the third
/// test ignored, and a skip is not a pass, so the receipt must say "2 passed ... 1 skipped",
/// name the reason, and not call the run AllPassed.
///
/// The verdict needs no new case. `RunVerdict.Incomplete` already means "nothing failed, but not
/// every test passed in this run", and its text says "This is not green". A project whose only
/// non-passing test is pending is therefore Incomplete, which is the rule: a skip never spells
/// AllPassed.
module SageFs.Tests.ExpectoPendingOutcomeTests

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
  RepoPaths.repoPath [| "SageFs.Tests"; "fixtures"; "ExpectoPendingFixture" |]

let private fixtureProject = Path.Combine(fixtureDir, "ExpectoPendingFixture.fsproj")

let private pendingTestName = "pending is not a pass"

/// The tool's own answer: the first TextContentBlock only. The server appends a second block of
/// session events to a result when some accumulated, and joining the blocks corrupts the JSON.
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

/// The receipt as data: `run_tests` puts it in StructuredContent and writes only its plain-language
/// summary in the text block, so the JSON is read from the protocol's own field.
let private callToolStructured (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.toolCall)
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
    match result.StructuredContent.HasValue with
    | true -> return result.StructuredContent.Value.GetRawText()
    | false -> return failwithf "%s returned no StructuredContent. Text: %s" name (textOf result)
  }

/// What the receipt must say for two passing tests and one pending test. Pure, so the task that
/// fetches the receipt stays small.
let private expectPendingIsSkipped (raw: string) : unit =
  use doc = JsonDocument.Parse raw
  let root = doc.RootElement
  let counts = root.GetProperty("counts")
  root.GetProperty("status").GetString()
  |> Expect.equal (sprintf "the run should finish within the wait. Raw: %s" raw) "Ran"
  counts.GetProperty("passing").GetInt32()
  |> Expect.equal (sprintf "only the two normal tests passed. Raw: %s" raw) 2
  counts.GetProperty("skipping").GetInt32()
  |> Expect.equal (sprintf "the pending test is counted as skipped. Raw: %s" raw) 1
  root.GetProperty("verdict").GetString()
  |> Expect.equal (sprintf "a skip is not a pass, so the verdict is Incomplete and never AllPassed. Raw: %s" raw) "Incomplete"
  let message = root.GetProperty("message").GetString()
  message.Contains "2 passed, 0 failed, 1 skipped, 0 did not report"
  |> Expect.isTrue (sprintf "the receipt text names the skip. Message: %s" message)
  message.Contains "Every requested test passed"
  |> Expect.isFalse (sprintf "the receipt text must not say every test passed. Message: %s" message)
  let pendingLine =
    [ for line in root.GetProperty("lines").EnumerateArray() -> line ]
    |> List.find (fun line -> line.GetProperty("name").GetString().EndsWith pendingTestName)
  pendingLine.GetProperty("outcome").GetString()
  |> Expect.equal (sprintf "the pending test's line is Skipped. Raw: %s" raw) "Skipped"
  pendingLine.GetProperty("detail").GetString()
  |> Expect.stringContains "the line names why it was skipped" "pending (ptest)"

/// Create the fixture session, wait for discovery, and ask for a receipt.
let private runTestsReceipt (client: McpClient) (httpClient: Net.Http.HttpClient) : Task<string> =
  task {
    let! createBody =
      callTool client "create_project_session"
        [ "project", box fixtureProject; "working_directory", box fixtureDir; "workflow", box "livetesting" ]
    let! ready, sessionsBody = Http.waitForReadySession httpClient fixtureDir LiveTestingBudgets.baselineRun
    ready
    |> Expect.isTrue (sprintf "the fixture session should reach Ready. Create: %s Sessions: %s" createBody sessionsBody)
    let! enableStatus, enableBody = Http.postJson httpClient "/api/live-testing/enable" {||}
    enableStatus |> Expect.equal (sprintf "enable should succeed. Body: %s" enableBody) 200
    let! discovered, snapshot, statusBody =
      Http.waitForLiveTestingStatus httpClient None LiveTestingBudgets.baselineRun (fun s ->
        s.DiscoveryState = "ready_with_tests" && s.Total >= 3)
    discovered
    |> Expect.isTrue (sprintf "the three fixture tests should be discovered. Status: %s Snapshot: %+A" statusBody snapshot)
    return!
      callToolStructured client "run_tests" [ "wait_seconds", box (int TestTimeouts.runTestsWait.TotalSeconds) ]
  }

let private runPendingGate () : Task<unit> =
  task {
    Http.runProcessExpectSuccess "dotnet" fixtureDir [ "build"; fixtureProject; "--nologo"; "-v:q" ]
    let port = Http.reserveLoopbackPort ()
    let! proc, httpClient = Http.startDaemonWithArgs port fixtureDir [ "--no-resume" ]
    let! client = connect port
    // Observe the outcome without a try/finally in the computation expression, the way
    // McpToolOutcomeTests does: ContinueWith never faults, so the cleanup is always reached
    // and the original exception is re-raised after it.
    let! outcome =
      (runTestsReceipt client httpClient)
        .ContinueWith(fun (t: Task<string>) ->
          match t.IsFaulted with
          | true -> Error (t.Exception :> exn)
          | false -> Ok t.Result)
    try (client :> IAsyncDisposable).DisposeAsync() |> ignore with _ -> ()
    httpClient.Dispose()
    Http.killDaemon proc
    match outcome with
    | Error e -> raise e
    | Ok raw -> expectPendingIsSkipped raw
  }

[<Tests>]
let expectoPendingOutcomeTests =
  testSequencedGroup LiveTestingWorkerSuites.groupName <|
    Integration.hostList "Expecto pending outcome gates" [
      testTask "WHY — run_tests reports two passing tests and one ptest as '2 passed, 1 skipped' and never AllPassed (the executor used to run the pending body and count it as passed)" {
        do! runPendingGate ()
      }
    ]
