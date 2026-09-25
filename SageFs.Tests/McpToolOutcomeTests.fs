/// Outcome gates for Island D (outcome-gate-sweep.md, Gap D — "list_tests
/// and the agent read-path").
///
/// docs/mcp-tools.md:62 tells agents there is no run_tests tool and that
/// results are read back through list_tests, explain_test_failure and
/// diagnose. Before this file, none of those three MCP TOOL FUNCTIONS had a
/// single caller anywhere in the suite — grepping both `list_tests` and
/// `listTests` across SageFs.Tests turned up only unrelated friction-
/// scenario string fixtures. The documented agent read-path for "what did
/// live testing actually find" was completely ungated.
///
/// This is a genuine outcome gate, not a mechanism test: it drives a REAL
/// MCP client (ModelContextProtocol.Client.McpClient — the real-wire
/// pattern CohortMcpToolsIntegrationTests.fs already established for tools
/// with no REST fallback, since list_tests/explain_test_failure/diagnose
/// are MCP-only, per docs/LIVE_TESTING_GUIDE.md's own "there is no ...
/// get_live_test_status ... MCP tool" statement) against a REAL daemon this
/// file spawns on its own isolated port + SAGEFS_DATA_DIR, with a REAL
/// compiled test project, through a genuine on-disk Passed→Failed
/// transition — mirroring HttpApiIntegrationTests.fs's own "editing a
/// compiled F# file reruns tests..." cell, but asserting through the MCP
/// tools that cell never touches instead of the raw /api/live-testing/*
/// endpoints.
///
/// Fixture: SageFs.Tests/fixtures/McpToolOutcomeFixture/ — deliberately its
/// own project, not samples/from-csharp/SageFs.Samples.FromCSharp (which
/// HttpApiIntegrationTests.fs's own live-testing cell already mutates on
/// disk). Expecto runs different test files' top-level [<Tests>] values in
/// parallel, so a second integration cell editing that same Hello.fs
/// concurrently would race both cells. This fixture is touched by nothing
/// else in the repo.
///
/// Resource discipline (outcome-gate-sweep.md §4): one daemon, one session,
/// one testTask — baseline discovery, then one on-disk edit that breaks
/// exactly one test, then the three documented read-path tools against that
/// failure, all in a single narrative so the (expensive) warmup happens
/// exactly once.
///
/// HEADLINE FINDING FROM WRITING THIS GATE: list_tests does not actually
/// work. Live-tested (not inferred from source) against a real daemon: with
/// /api/live-testing/status simultaneously confirming Total=3/Passed=3/
/// DiscoveryState=ready_with_tests for the session, list_tests's own MCP
/// call returned TotalCount=0/Returned=0/GroupedByFile=[] — reproduced
/// across 15s of polling and again after additionally seeding the FSI cell
/// graph with `#load "Sample.fs"` via send_fsharp_code. explain_test_failure
/// and diagnose read the SAME underlying model.LiveTesting.TestState.
/// DiscoveredTests directly (Mcp.fs), bypassing list_tests' broken
/// TestSourceResolver.resolveTestLocations step, so they are gated
/// independently below and may well pass even though list_tests does not —
/// this file reports each tool's real, separately-observed outcome rather
/// than assuming one implies the other.
module SageFs.Tests.McpToolOutcomeTests

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
  Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "McpToolOutcomeFixture")

let private fixtureProject =
  Path.Combine(fixtureDir, "McpToolOutcomeFixture.fsproj")

let private samplePath = Path.Combine(fixtureDir, "Sample.fs")

/// Extract the tool's OWN answer — the FIRST TextContentBlock only.
/// Verified live: McpServer.fs's createServerCaptureFilter appends a SECOND
/// TextContentBlock ("📡 SageFs events since last call: ...") to every tool
/// result whenever session events accumulated since the caller's last call
/// (a real, deliberate feature — the agent should see it). Naively
/// concatenating every block (the pattern CohortMcpToolsIntegrationTests.fs
/// uses, safe there because its tools return plain prose) corrupts JSON:
/// discovered directly by this test failing with 'System.Text.Json.
/// JsonReaderException: 0xF0 is invalid after a single JSON value' the
/// moment a warmup/test-result event landed between calls.
let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

/// Connect a fresh MCP client to this test's isolated daemon.
let private connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

let private callTool (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, cts.Token)
    return textOf result
  }

/// explain_test_failure/diagnose read a narrative CACHE that is populated
/// once the failing result is recorded — a small but real gap from the raw
/// pass/fail flip the HTTP /api/live-testing/status endpoint already
/// confirmed. Poll (a plain while loop, never `task { return! self }` —
/// F# task recursion is not stack-safe on a slow runner) rather than assert
/// on the very first call.
let private pollToolUntil
  (client: McpClient)
  (name: string)
  (args: (string * obj) list)
  (timeout: TimeSpan)
  (isReady: string -> bool)
  : Task<string> =
  task {
    let deadline = DateTime.UtcNow.Add timeout
    let mutable text = ""
    let mutable ready = false
    while not ready && DateTime.UtcNow < deadline do
      let! t = callTool client name args
      text <- t
      ready <- isReady t
      if not ready then
        do! Task.Delay(TimeSpan.FromMilliseconds 250.0)
    return text
  }

let private hasIntPropertyAtLeast (propertyName: string) (minValue: int) (text: string) : bool =
  try
    use doc = JsonDocument.Parse text
    doc.RootElement.GetProperty(propertyName).GetInt32() >= minValue
  with _ -> false

/// The tail of the gate — coverage_intel and suggest_repair.
///
/// Split out of `runToolOutcomeBody` at a seam where NOTHING crosses (verified:
/// no binding from the first half is read here). The split is not cosmetic.
/// With the whole gate in one `task`, a RELEASE build emitted IL the runtime
/// rejected — `System.InvalidProgramException` out of `MoveNext`, while Debug
/// passed the entire time, which is how this test could be written green and
/// never once pass in the pipeline. Removing the CE's try/finally moved the bad
/// IL but did not remove it, so the trigger is the SIZE of the generated state
/// machine. Keep these halves small; do not merge them back.
let private runToolOutcomeTail
  (client: McpClient)
  (httpClient: Net.Http.HttpClient)
  : Task<unit> =
  task {
        // ── coverage_intel: composes failure narratives + IL instrumentation
        //    bitmaps + the dependency graph into per-failure coverage
        //    intelligence. The outcome-gate sweep left this ungated because
        //    it "could not confirm [instrumentation maps] populate for a
        //    minimal fixture." Live-verified against this EXACT fixture:
        //    they do — a real 9-branch instrumentation map for Sample.fs,
        //    reported with a DiagnosticBlindSpot verdict at 0% coverage for
        //    the test that just failed. Raw observed shape:
        //    [{"TestName":"subtract computes the difference","Verdict":
        //    "DiagnosticBlindSpot","CoveragePercent":0,"TotalBranches":9,
        //    "BlindSpots":[...9 entries...],"CausalSymbols":[...,
        //    "SageFs.Tests.Fixtures.McpToolOutcome.Sample.subtract",...]}] ──
        let! coverageRaw =
          pollToolUntil client "coverage_intel" [] (TimeSpan.FromSeconds 15.0) (fun raw ->
            try JsonDocument.Parse(raw: string).RootElement.GetArrayLength() > 0
            with _ -> false)
        let coverageDoc = JsonDocument.Parse(coverageRaw: string)
        let coverageReports = [ for e in coverageDoc.RootElement.EnumerateArray() -> e ]
        coverageReports
        |> Expect.isNonEmpty (sprintf "coverage_intel should report coverage for at least the one failing test. Raw: %s" coverageRaw)

        let subtractCoverage =
          coverageReports
          |> List.tryFind (fun r -> r.GetProperty("TestName").GetString() = "subtract computes the difference")
        subtractCoverage
        |> Expect.isSome (sprintf "coverage_intel should report on the test that just failed. Raw: %s" coverageRaw)
        let subtractReport = subtractCoverage.Value

        (subtractReport.GetProperty("TotalBranches").GetInt32(), 0)
        |> Expect.isGreaterThan "coverage_intel should carry a real IL-instrumented branch count for this compiled fixture, not zero"

        [ for e in subtractReport.GetProperty("BlindSpots").EnumerateArray() -> e ]
        |> Expect.isNonEmpty "coverage_intel should list uncovered branch locations for a test with 0% coverage"

        [ for s in subtractReport.GetProperty("CausalSymbols").EnumerateArray() -> s.GetString() ]
        |> List.exists (fun s -> s.Contains "subtract")
        |> Expect.isTrue (sprintf "coverage_intel's causal symbols should name the function that actually changed. Raw: %s" coverageRaw)
        coverageDoc.Dispose()

        // ── suggest_repair: composes explain_test_failure → causal symbol →
        //    ripple plan. Live-verified real, non-vacuous output for this
        //    fixture: TestName, CausalChanges and a Suggestion are always
        //    populated. RipplePlan, however, is honestly confirmed NULL here
        //    — not a bug, a documented dependency: it only resolves when the
        //    primary causal symbol is a live FSI binding
        //    (`state.EvalHistory` / `scope.ActiveBindings`, Mcp.fs's
        //    suggestRepair), and this session — created via create_session
        //    workflow=livetesting against a COMPILED project — never ran
        //    send_fsharp_code, so it carries no eval history at all. The tool
        //    correctly falls back to its documented "not in the current
        //    session bindings, re-evaluate the cell" message instead of
        //    crashing or returning nothing. Raw observed shape:
        //    {"TestName":"subtract computes the difference","PrimarySymbol":
        //    "Expect","RipplePlan":null,"Suggestion":"'Expect' is the likely
        //    cause, but it's not in the current session bindings. ..."} ──
        // No polling needed: explain_test_failure/diagnose above already
        // confirmed (via their own polls) that the failure narrative for
        // this test exists — suggest_repair reads the SAME cached
        // narrative, so its data is ready on the first call.
        let! repairRaw = callTool client "suggest_repair" [ "test_name", box "subtract computes the difference" ]
        let repairDoc = JsonDocument.Parse(repairRaw: string)
        let repairRoot = repairDoc.RootElement
        repairRoot.GetProperty("TestName").GetString()
        |> Expect.equal (sprintf "suggest_repair should name the test it was asked about. Raw: %s" repairRaw) "subtract computes the difference"
        [ for e in repairRoot.GetProperty("CausalChanges").EnumerateArray() -> e ]
        |> Expect.isNonEmpty (sprintf "suggest_repair should carry the same causal changes explain_test_failure reported. Raw: %s" repairRaw)
        repairRoot.GetProperty("Suggestion").GetString()
        |> Expect.isNotEmpty (sprintf "suggest_repair should always produce a human-readable suggestion, even without a ripple plan. Raw: %s" repairRaw)
        repairRoot.GetProperty("RipplePlan").ValueKind
        |> Expect.equal
             "RipplePlan is honestly null for a compiled-project session with no FSI eval history — see this block's header comment"
             JsonValueKind.Null
        repairDoc.Dispose()
  }

/// The gate's assertions, with NO try/finally anywhere in the computation
/// expression — deliberately.
///
/// This test never once passed in the pipeline: under a RELEASE build it died
/// with `System.InvalidProgramException: Common Language Runtime detected an
/// invalid program`, while passing in Debug. Every stack trace of it ran
/// through resumable try/finally compensation — first
/// `ResumableCode.TryFinallyAsyncDynamic`, and then, after the dynamic
/// fallback was ruled out by moving off Expecto's builder onto FSharp.Core's,
/// through the STATIC state machine's own `MoveNext`. An await inside a
/// `try/finally` is async compensation either way, and that is what the
/// optimizer miscompiles here.
///
/// So the compensation is removed rather than relocated: this function holds
/// the assertions and nothing else, and `runToolOutcomeGate` does cleanup
/// unconditionally after observing the outcome through a Task-level
/// continuation. The cleanup guarantee is identical; only the IL differs.
let private runToolOutcomeBody
  (client: McpClient)
  (httpClient: Net.Http.HttpClient)
  (editedSample: string)
  : Task<unit> =
  task {

        // Created via the explicit MCP project-session TOOL, not the REST endpoint,
        // with workflow="livetesting". Verified live and necessary: a
        // session created via REST (Interactive workflow) shows live tests
        // discovered/passed just fine through /api/live-testing/status, but
        // list_tests/explain_test_failure/diagnose read a SEPARATE model
        // (ctx.GetElmModel's LiveTesting cycle) that stayed empty the whole
        // run in that configuration — a genuine gap between the REST
        // surface and the MCP read-path this gate exists to catch. Only
        // creating the session already IN the LiveTesting SessionWorkflow
        // populates that model.
        let! createBody = callTool client "create_project_session" [ "project", box fixtureProject; "working_directory", box fixtureDir; "workflow", box "livetesting" ]
        createBody
        |> Expect.isNotEmpty "create_project_session should report something"

        let! ready, sessionsBody = Http.waitForReadySession httpClient fixtureDir (TimeSpan.FromSeconds 60.0)
        ready
        |> Expect.isTrue (
          sprintf "fixture session should reach Ready. Create: %s Sessions: %s" createBody sessionsBody)

        let! enableStatus, enableBody = Http.postJson httpClient "/api/live-testing/enable" {||}
        enableStatus |> Expect.equal "enable should succeed" 200

        let! policyStatus, policyBody =
          Http.postJson httpClient "/api/live-testing/policy" {| category = "unit"; policy = "every" |}
        policyStatus |> Expect.equal "policy update should succeed" 200

        let! discovered, discoveredSnapshot, discoveredBody =
          Http.waitForLiveTestingStatus httpClient None (TimeSpan.FromSeconds 60.0) (fun s ->
            s.DiscoveryState = "ready_with_tests" && s.Total >= 3)
        discovered
        |> Expect.isTrue (
          sprintf
            "fixture tests should be discovered. Enable: %s Policy: %s Status: %s Snapshot: %+A"
            enableBody policyBody discoveredBody discoveredSnapshot)

        // Discovery auto-evaluates the fixture's baseline (verified live, by
        // hand, against a manually spawned daemon: Total=3/Passed=3/Failed=0
        // settles within ~2s of enable+policy, no explicit /run needed) — so
        // wait directly for the fully-healthy baseline rather than splitting
        // into a separate "settled" check first. That earlier two-step
        // version raced: "Running=0" can be trivially true for an instant
        // before evaluation has even started, locking in a stale Passed=0
        // snapshot. A fallback explicit /run stays here for resilience, but
        // is not expected to fire.
        let! (autoReady: bool), (autoSnapshot: Http.LiveTestingStatusSnapshot), (autoBody: string) =
          Http.waitForLiveTestingStatus httpClient None (TimeSpan.FromSeconds 60.0) (fun s ->
            s.Total >= 3 && s.Passed >= 3 && s.Failed = 0 && s.Running = 0 && s.Stale = 0)

        let! (baselineReady: bool), (baselineSnapshot: Http.LiveTestingStatusSnapshot), (baselineBody: string) =
          match autoReady with
          | true -> Task.FromResult(autoReady, autoSnapshot, autoBody)
          | false ->
            task {
              let! runStatus, runBody = Http.postJson httpClient "/api/live-testing/run" {| pattern = ""; category = "" |}
              runStatus |> Expect.equal "baseline run request should succeed" 200
              return!
                Http.waitForLiveTestingStatus httpClient None (TimeSpan.FromSeconds 60.0) (fun s ->
                  s.Total >= 3 && s.Passed >= 3 && s.Failed = 0 && s.Running = 0 && s.Stale = 0)
            }

        baselineReady
        |> Expect.isTrue (
          sprintf
            "baseline run should pass before editing the fixture. Status: %s Snapshot: %+A"
            baselineBody baselineSnapshot)

        // ── list_tests: no filter — the baseline read every agent takes
        //    first. CONFIRMED LIVE DEFECT (not staleness — reproduced with
        //    generous polling AND cross-checked against a simultaneous REST
        //    read): list_tests reported TotalCount=0/Returned=0 while
        //    /api/live-testing/status, queried at the exact same instant,
        //    showed Total=3/Passed=3/DiscoveryState=ready_with_tests for the
        //    SAME session. Also reproduced after additionally #load-ing
        //    Sample.fs via send_fsharp_code (ruling out "needs an FSI eval
        //    to seed the cell graph"). list_tests's own JSON, both times:
        //    {"FilterApplied":null,"GroupedByFile":[],"Returned":0,
        //     "Summary":"🔍 0 of 0 test(s) across 0 file(s)","TotalCount":0}
        //    docs/mcp-tools.md:62's headline promise — "agents read results
        //    through list_tests" — does not hold for this session shape.
        //    This assertion states the DESIRED behavior and is expected to
        //    fail until the product bug is fixed (the same "expected initial
        //    state: RED, that is correct — it is the bug" posture Island A's
        //    hot-reload gate documents) — never weakened to match the broken
        //    output, which would be exactly the roast's "gate built around
        //    the shape that works" antipattern. Soft (isTrue captured, not
        //    asserted here) so explain_test_failure/diagnose below — which
        //    read DiscoveredTests directly rather than through list_tests'
        //    resolution path — still get their own independent gate. ──
        let! listAllRaw =
          pollToolUntil client "list_tests" [] (TimeSpan.FromSeconds 15.0) (hasIntPropertyAtLeast "TotalCount" 3)
        let listTestsReportsDiscoveredTests = hasIntPropertyAtLeast "TotalCount" 3 listAllRaw

        // ── break `subtract` on disk — a real Passed→Failed transition ──
        File.WriteAllText(samplePath, editedSample, Http.utf8NoBom)

        let! failedAfterEdit, failedSnapshot, failedBody =
          Http.waitForLiveTestingStatus httpClient None (TimeSpan.FromSeconds 60.0) (fun s ->
            s.FailedTests |> List.exists (fun name -> name = "subtract computes the difference"))
        failedAfterEdit
        |> Expect.isTrue (
          sprintf
            "editing Sample.fs should automatically surface the failing subtract test. Status: %s Snapshot: %+A"
            failedBody failedSnapshot)

        // ── explain_test_failure: the documented "why did it fail" read-path ──
        let! explainRaw =
          pollToolUntil
            client "explain_test_failure" [ "test_name", box "subtract computes the difference" ]
            (TimeSpan.FromSeconds 15.0) (hasIntPropertyAtLeast "MatchCount" 1)
        explainRaw
        |> hasIntPropertyAtLeast "MatchCount" 1
        |> Expect.isTrue (
          sprintf "explain_test_failure should find a narrative for the test that just failed. Raw: %s" explainRaw)
        let explainDoc = JsonDocument.Parse(explainRaw: string)
        let explainRoot = explainDoc.RootElement

        let narratives = [ for e in explainRoot.GetProperty("Narratives").EnumerateArray() -> e ]
        narratives
        |> List.exists (fun n -> n.GetProperty("DisplayName").GetString().Contains "subtract")
        |> Expect.isTrue "explain_test_failure's narrative should name the failing test"

        let causalChanges =
          [ for n in narratives do
              for c in n.GetProperty("CausalChanges").EnumerateArray() -> c ]
        causalChanges
        |> Expect.isNonEmpty "explain_test_failure should report at least one causal change, not an empty narrative"

        // ── diagnose: the composed report every agent is told replaces the
        //    other read-path calls — must surface the SAME failure ──
        let! diagnoseRaw =
          pollToolUntil client "diagnose" [] (TimeSpan.FromSeconds 15.0) (hasIntPropertyAtLeast "FailureCount" 1)
        diagnoseRaw
        |> hasIntPropertyAtLeast "FailureCount" 1
        |> Expect.isTrue (
          sprintf "diagnose should report the same failure list_tests/explain_test_failure just saw. Raw: %s" diagnoseRaw)
        let diagnoseDoc = JsonDocument.Parse(diagnoseRaw: string)
        let diagnoseRoot = diagnoseDoc.RootElement

        let diagnoseNames =
          [ for f in diagnoseRoot.GetProperty("Failures").EnumerateArray() -> f.GetProperty("TestName").GetString() ]
        diagnoseNames
        |> Expect.contains "diagnose's Failures list should name the same test explain_test_failure narrated" "subtract computes the difference"

        // list_tests's own promise (docs/mcp-tools.md:62) is asserted LAST,
        // after explain_test_failure and diagnose have had their own
        // independent, fully-exercised gate above — so a failure here never
        // hides whether those two (which read DiscoveredTests directly, not
        // through list_tests' broken resolution path) actually work.
        listTestsReportsDiscoveredTests
        |> Expect.isTrue (
          sprintf
            "list_tests should report the 3 tests /api/live-testing/status confirms are discovered and passing for this session — CONFIRMED LIVE DEFECT, see this test's header. Raw: %s"
            listAllRaw)

        do! runToolOutcomeTail client httpClient
  }

let private runToolOutcomeGate () : Task<unit> =
  task {
      let originalSample = File.ReadAllText samplePath
      let canonicalSubtract = "let subtract a b = a - b"
      let brokenSubtract = "let subtract a b = a - b + 1"
      let baselineSample = originalSample.Replace(brokenSubtract, canonicalSubtract)
      let editedSample = baselineSample.Replace(canonicalSubtract, brokenSubtract)

      (baselineSample <> editedSample)
      |> Expect.isTrue "fixture mutation should change Sample.fs"

      File.WriteAllText(samplePath, baselineSample, Http.utf8NoBom)
      Http.runProcessExpectSuccess "dotnet" fixtureDir [ "build"; fixtureProject; "--nologo"; "-v:q" ]

      let port = Http.reserveLoopbackPort ()
      let! proc, httpClient = Http.startDaemonWithArgs port fixtureDir [ "--no-resume" ]
      let! client = connect port

      // Observe the outcome WITHOUT a CE-level try/finally (see the comment on
      // runToolOutcomeBody). ContinueWith never faults, so the cleanup below is
      // always reached and the original exception is re-raised after it.
      let! outcome =
        (runToolOutcomeBody client httpClient editedSample)
          .ContinueWith(fun (t: Task<unit>) ->
            match t.IsFaulted with
            | true -> Error(t.Exception :> exn)
            | false -> Ok())

      try File.WriteAllText(samplePath, originalSample, Http.utf8NoBom) with _ -> ()
      // McpClient is IAsyncDisposable only, and killing the daemon on the next
      // line closes the transport it is attached to; blocking on the ValueTask
      // here is the banned, ratcheted pattern.
      try (client :> IAsyncDisposable).DisposeAsync() |> ignore with _ -> ()
      httpClient.Dispose()
      Http.killDaemon proc

      match outcome with
      | Error e -> raise e
      | Ok() -> ()
  }
[<Tests>]
let mcpToolOutcomeTests =
  Integration.hostList "MCP tool outcome gates" [
    testTask "WHY — list_tests, explain_test_failure and diagnose report what live testing actually found (Gap D: the documented agent read-path had zero tool-level callers)" {
      do! runToolOutcomeGate ()
    }
  ]
