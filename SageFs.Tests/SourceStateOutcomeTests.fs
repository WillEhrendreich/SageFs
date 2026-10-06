/// Outcome gate for the `run_tests` receipt's claim about its source: the receipt says whether the build the tests ran
/// against is behind the files on disk, and a pass over a build that is behind is never spelled AllPassed.
///
/// The Nehemiah repro, through a REAL daemon and a REAL MCP client: edit a test file on disk, do not rebuild, call
/// run_tests. Before this gate the receipt said "3 passed" and nothing else. This file drives the whole story against
/// SageFs.Tests/fixtures/SourceStateFixture/, a compiled Expecto project of its own (the gate edits its sources, rebuilds
/// it and takes a directory of it away, so no other test may share it):
///
///   1. nothing edited: AllPassed, source InSync;
///   2. a test file edited, no rebuild: PassedOnStaleSource, source Stale, naming the file, with the warning in the text;
///   3. a source file edited as well: both files named;
///   4. get_session_status and list_sessions carry the same state under `sourceState`, next to (and not mistaken for)
///      `replFreshness`;
///   5. a rebuild: while it runs get_session_status says Rebuilding; afterwards run_tests is AllPassed, source InSync;
///   6. a source that cannot be read: Unknown with the reason, PassedOnUnknownSource, never a quiet pass.
///
/// One daemon, one session, one narrative, so the (expensive) warmup and the one rebuild are paid once. The assertions
/// live in a body with no try/finally in its computation expression; cleanup is done by the caller after observing the
/// outcome through a Task-level continuation (see McpToolOutcomeTests for why).
module SageFs.Tests.SourceStateOutcomeTests

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

let private repoRoot = RepoPaths.repoPathFull [||]

let private fixtureDir = Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "SourceStateFixture")
let private fixtureProject = Path.Combine(fixtureDir, "SourceStateFixture.fsproj")
let private domainPath = Path.Combine(fixtureDir, "Domain.fs")
let private testsPath = Path.Combine(fixtureDir, "DomainTests.fs")
let private subDir = Path.Combine(fixtureDir, "Sub")

/// The tool's own answer: the first text block (the daemon may append a second one about session events).
let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

/// What a tool returned as data, when it returned any.
let private structuredOf (result: CallToolResult) : JsonElement voption =
  match result.StructuredContent.HasValue with
  | true -> ValueSome (result.StructuredContent.Value.Clone())
  | false -> ValueNone

let private connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

let private callTool (client: McpClient) (name: string) (args: (string * obj) list) : Task<CallToolResult> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.toolCallThatRestarts)
    return! client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
  }

let private callText (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    let! result = callTool client name args
    return textOf result
  }

/// run_tests over the whole fixture, waiting for the run to finish. The receipt as data, and as text.
let private runTests (client: McpClient) : Task<JsonElement * string> =
  task {
    let! result =
      callTool client "run_tests" [ "wait_seconds", box 60; "working_directory", box fixtureDir ]
    match structuredOf result with
    | ValueSome data -> return data, textOf result
    | ValueNone -> return failwithf "run_tests returned no structured content. Text: %s" (textOf result)
  }

/// get_session_status for the fixture's session, as data.
let private sessionStatus (client: McpClient) (waitSeconds: int) : Task<JsonElement> =
  task {
    let! text = callText client "get_session_status" [ "working_directory", box fixtureDir; "wait_seconds", box waitSeconds ]
    return JsonDocument.Parse(text).RootElement.Clone()
  }

/// Rewrite a file on disk. The change must change the file, or the row proves nothing.
let private edit (path: string) (change: string -> string) : unit =
  let before = File.ReadAllText path
  let after = change before
  (before <> after) |> Expect.isTrue (sprintf "the edit of %s must change it" path)
  File.WriteAllText(path, after, Http.utf8NoBom)

let private stateOf (receipt: JsonElement) : string = receipt.GetProperty("source").GetProperty("state").GetString()
let private verdictOf (receipt: JsonElement) : string = receipt.GetProperty("verdict").GetString()

let private changedFilesOf (receipt: JsonElement) : string list =
  [ for f in receipt.GetProperty("source").GetProperty("changedFiles").EnumerateArray() -> f.GetProperty("path").GetString() ]

let private namesFile (files: string list) (path: string) : bool =
  files |> List.exists (fun f -> Path.GetFullPath f = Path.GetFullPath path)

/// The first rows: a clean receipt, then the Nehemiah edit.
let private runReceiptRows (client: McpClient) : Task<unit> =
  task {
    // 1. Nothing edited since the build.
    let! baseline, baselineText = runTests client
    baseline.GetProperty("status").GetString() |> Expect.equal (sprintf "the run finished. Text: %s" baselineText) "Ran"
    verdictOf baseline |> Expect.equal (sprintf "an untouched build passes plainly. Text: %s" baselineText) "AllPassed"
    stateOf baseline |> Expect.equal (sprintf "an untouched build is in sync. Receipt: %s" (baseline.ToString())) "InSync"
    baseline.GetProperty("counts").GetProperty("passing").GetInt32() |> Expect.equal "all three tests passed" 3

    // 2. A test file edited on disk, no rebuild. The tests that ran are the build's, not the file's.
    edit testsPath (fun text -> text + "\n// edited after the build\n")
    let! edited, editedText = runTests client
    verdictOf edited |> Expect.equal (sprintf "an edit after the build is never AllPassed. Text: %s" editedText) "PassedOnStaleSource"
    stateOf edited |> Expect.equal "the source is stale" "Stale"
    namesFile (changedFilesOf edited) testsPath |> Expect.isTrue (sprintf "the receipt names the edited test file. Receipt: %s" (edited.ToString()))
    editedText |> Expect.stringContains "the text says STALE" "STALE"
    editedText |> Expect.stringContains "the text still says the tests passed" "passed"
    edited.GetProperty("counts").GetProperty("passing").GetInt32() |> Expect.equal "the tests did pass" 3

    // 3. A source file edited as well: both are named.
    edit domainPath (fun text -> text + "\n// edited after the build\n")
    let! both, bothText = runTests client
    stateOf both |> Expect.equal (sprintf "still stale. Text: %s" bothText) "Stale"
    let files = changedFilesOf both
    namesFile files testsPath |> Expect.isTrue "the test file is named"
    namesFile files domainPath |> Expect.isTrue "the source file is named"
  }

/// The same state on get_session_status and list_sessions, next to the REPL's freshness and not mistaken for it.
let private statusRows (client: McpClient) : Task<unit> =
  task {
    let! status = sessionStatus client 0
    let source = status.GetProperty("sourceState")
    source.GetProperty("state").GetString() |> Expect.equal (sprintf "get_session_status says stale. Status: %s" (status.ToString())) "Stale"
    status.GetProperty("replFreshness").GetProperty("state").GetString()
    |> Expect.equal "the REPL is not behind an app: it is a different fact" "InSync"
    let! listing = callText client "list_sessions" []
    listing |> Expect.stringContains "list_sessions says the source is stale" "STALE"
  }

/// The rebuild: Rebuilding while it runs, in sync after.
let private rebuildRows (client: McpClient) (httpClient: Net.Http.HttpClient) : Task<unit> =
  task {
    let! reset = callText client "hard_reset_fsi_session" [ "rebuild", box true; "working_directory", box fixtureDir ]
    reset |> Expect.isNotEmpty "the reset answers"
    // The reset answers when the rebuild is REQUESTED; how fast the record appears behind that answer is the
    // daemon's. Reading once here raced it under load — the gate's own failure read `Stale` while the rebuild
    // had not registered yet — so wait for the state under test instead of assuming its first instant. The claim
    // is "Rebuilding WHILE it runs", which is a state to observe, not a moment to hit; the build itself takes far
    // longer than the read that follows, so the rows below still see it mid-flight.
    let duringStatus = ref ""
    let! rebuilding =
      SageFs.Tests.TestInfrastructure.waitForAsync (int TestTimeouts.workerSessionReady.TotalMilliseconds) (fun () ->
        task {
          let! status = sessionStatus client 0
          duringStatus.Value <- status.ToString()
          match status.TryGetProperty "sourceState" with
          | true, source when source.ValueKind = JsonValueKind.Object ->
            return source.GetProperty("state").GetString() = "Rebuilding"
          | _ -> return false
        })
    rebuilding
    |> Expect.isTrue (sprintf "while the rebuild runs the source is Rebuilding. Last status: %s" duringStatus.Value)
    // A run asked for while the rebuild runs says so, however it passes: the old worker is still serving the old build.
    let! mid, midText = runTests client
    stateOf mid |> Expect.equal (sprintf "the receipt says Rebuilding. Text: %s" midText) "Rebuilding"
    verdictOf mid |> Expect.equal "a pass during a rebuild is never AllPassed" "PassedWhileRebuilding"
    // The old worker stays Ready while the new build is made, so the status wait has nothing to park on. The rebuild is over when
    // its record says it succeeded and the session is Ready.
    let last = ref ""
    let! rebuilt =
      SageFs.Tests.TestInfrastructure.waitForAsync (int TestTimeouts.workerSessionReady.TotalMilliseconds) (fun () ->
        task {
          let! status = sessionStatus client 0
          last.Value <- status.ToString()
          let succeeded =
            match status.TryGetProperty "lastRestart" with
            | true, restart when restart.ValueKind = JsonValueKind.Object -> restart.GetProperty("outcome").GetString() = "Succeeded"
            | _ -> false
          return succeeded && status.GetProperty("lifecycle").GetString() = "Ready"
        })
    rebuilt |> Expect.isTrue (sprintf "the rebuild finishes and the session is Ready again. Last status: %s" last.Value)
    let! enableStatus, _ = Http.postJson httpClient "/api/live-testing/enable" {||}
    enableStatus |> Expect.equal "live testing can be enabled again" 200
    let! discovered, snapshot, body =
      Http.waitForLiveTestingStatus httpClient None LiveTestingBudgets.baselineRun (fun s -> s.DiscoveryState = "ready_with_tests" && s.Total >= 3)
    discovered |> Expect.isTrue (sprintf "the tests are discovered again. Status: %s Snapshot: %+A" body snapshot)
    let! rebuilt, rebuiltText = runTests client
    stateOf rebuilt |> Expect.equal (sprintf "after the rebuild the source is in sync. Text: %s" rebuiltText) "InSync"
    verdictOf rebuilt |> Expect.equal "and the pass is a plain AllPassed again" "AllPassed"
  }

/// A source that cannot be read.
let private unreadableRows (client: McpClient) : Task<unit> =
  task {
    File.SetUnixFileMode(subDir, UnixFileMode.None)
    let! blind, blindText = runTests client
    stateOf blind |> Expect.equal (sprintf "a file that cannot be read is Unknown. Text: %s" blindText) "Unknown"
    verdictOf blind |> Expect.equal "never a quiet pass" "PassedOnUnknownSource"
    blind.GetProperty("source").GetProperty("reason").GetProperty("kind").GetString() |> Expect.equal "the reason is a token" "Unreadable"
    blindText |> Expect.stringContains "the text names the file it could not read" "Extra.fs"
  }

let private runGate (client: McpClient) (httpClient: Net.Http.HttpClient) : Task<unit> =
  task {
    let! createBody =
      callText client "create_project_session" [ "project", box fixtureProject; "working_directory", box fixtureDir; "workflow", box "livetesting" ]
    createBody |> Expect.isNotEmpty "create_project_session should report something"
    let! ready, sessionsBody = Http.waitForReadySession httpClient fixtureDir LiveTestingBudgets.sessionReady
    ready |> Expect.isTrue (sprintf "the fixture session should reach Ready. Create: %s Sessions: %s" createBody sessionsBody)
    let! enableStatus, enableBody = Http.postJson httpClient "/api/live-testing/enable" {||}
    enableStatus |> Expect.equal (sprintf "enable should succeed: %s" enableBody) 200
    // Nothing runs on its own: a run happens when the gate asks for one.
    let! policyStatus, policyBody = Http.postJson httpClient "/api/live-testing/policy" {| category = "unit"; policy = "ondemand" |}
    policyStatus |> Expect.equal (sprintf "policy update should succeed: %s" policyBody) 200
    let! discovered, snapshot, body =
      Http.waitForLiveTestingStatus httpClient None LiveTestingBudgets.baselineRun (fun s -> s.DiscoveryState = "ready_with_tests" && s.Total >= 3)
    discovered |> Expect.isTrue (sprintf "the fixture tests should be discovered. Status: %s Snapshot: %+A" body snapshot)
    do! runReceiptRows client
    do! statusRows client
    do! rebuildRows client httpClient
    do! unreadableRows client
  }

let private originalOf (path: string) : string * string = path, File.ReadAllText path

let private runOutcomeGate () : Task<unit> =
  task {
    let originals = [ originalOf domainPath; originalOf testsPath ]
    Http.runProcessExpectSuccess "dotnet" fixtureDir [ "build"; fixtureProject; "--nologo"; "-v:q" ]
    let port = Http.reserveLoopbackPort ()
    let! proc, httpClient = Http.startDaemonWithArgs port fixtureDir [ "--no-resume" ]
    let! client = connect port
    let! outcome =
      (runGate client httpClient)
        .ContinueWith(fun (t: Task<unit>) ->
          match t.IsFaulted with
          | true -> Error (t.Exception :> exn)
          | false -> Ok ())
    // Put the fixture back as it was, and give the directory its permissions back, whatever happened.
    try File.SetUnixFileMode(subDir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) with _ -> ()
    for path, text in originals do
      try File.WriteAllText(path, text, Http.utf8NoBom) with _ -> ()
    try (client :> IAsyncDisposable).DisposeAsync() |> ignore with _ -> ()
    httpClient.Dispose()
    Http.killDaemon proc
    match outcome with
    | Error e -> raise e
    | Ok () -> ()
  }

[<Tests>]
let sourceStateOutcomeTests =
  // Sequenced with the other suites that wait on a live worker (LiveTestingWorkerSuites says why).
  testSequencedGroup LiveTestingWorkerSuites.groupName <|
    Integration.hostList "source state outcome gates" [
      testTask "WHY — a receipt over files edited after the build is never AllPassed, and says which files; a rebuild says Rebuilding then InSync; a file that cannot be read says Unknown and why" {
        match OperatingSystem.IsWindows() with
        | true -> skiptest "the unreadable-source row takes the execute bit off a directory, a Unix permission"
        | false -> do! runOutcomeGate ()
      }
    ]
