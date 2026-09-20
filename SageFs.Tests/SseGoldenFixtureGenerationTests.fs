module SageFs.Tests.SseGoldenFixtureGenerationTests

/// Island C (outcome-gate-sweep.md, Gap C item 1): the golden fixtures in
/// `fixtures/LiveTesting/*.json` used to be hand-authored literals — nothing
/// forced them to agree with what the daemon actually emits. One of them was
/// worse than stale: `summary-with-fallback-decision.json` and
/// `summary-with-suppressed-decision.json` were shaped after
/// `SseWriter.formatTestSummaryEvent`, a function **no production code path
/// calls** (only `formatTestSummaryEventWithDiscovery` is wired into
/// `McpServer.fs`) — so the fixture a client golden-tests against was never
/// something the daemon could send.
///
/// This file closes that gap: every fixture is generated HERE, from the real
/// `SseWriter.format*Event` functions, using the exact `JsonSerializerOptions`
/// production wires up (`McpServer.fs` — a raw `JsonSerializerOptions()` plus
/// `JsonFSharpConverter()`, no naming-policy transform, mirrored below), and
/// the test fails if the checked-in file differs from what that real call
/// produces right now. A field rename in `TestSummary`, `LiveTestingDecision`,
/// `TestResultsBatchPayload`, or any type they carry, changes the generated
/// JSON and fails this test — the fixture stops being a hand-authored literal
/// and becomes the server's actual output, pinned.
///
/// To intentionally accept a real, deliberate shape change: set
/// `SAGEFS_UPDATE_SSE_FIXTURES=1` and run this test list once — it overwrites
/// the checked-in files with the freshly generated payload — then re-run
/// without the env var to confirm the pin holds, and review the diff like any
/// other generated-artifact change.

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open Expecto
open Expecto.Flip
open SageFs.SseWriter
open SageFs.Features.LiveTesting
open SageFs.Features.LiveTestActivity

// ── The exact JSON options production uses ──────────────────────────────────
// Mirrors McpServer.fs's `sseJsonOpts` construction verbatim: a bare
// JsonSerializerOptions with only the F# converter added — no
// PropertyNamingPolicy. (SseContractComplianceTests.fs, by contrast, builds
// its own jsonOpts with a CamelCase naming policy that production does NOT
// use — that test is a self-consistent shape check, not a wire-format
// fixture, and is out of this island's scope to touch.)
let private productionJsonOpts () =
  let opts = JsonSerializerOptions()
  opts.Converters.Add(JsonFSharpConverter())
  opts

let private fixturesDir =
  Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "LiveTesting")

let private extractDataPayload (sse: string) : string =
  sse.Split('\n')
  |> Array.choose (fun line ->
    match line.StartsWith("data: ") with
    | true -> Some(line.Substring(6))
    | false -> None)
  |> String.concat "\n"

/// Fail-closed: an env var flips this from "compare" to "regenerate", and
/// only that mode may touch disk. The default path never writes.
let private updateFixturesRequested () =
  match Environment.GetEnvironmentVariable "SAGEFS_UPDATE_SSE_FIXTURES" with
  | null -> false
  | v -> v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)

/// Compare two JSON payloads structurally (key order must not matter — these
/// are objects, not ordered documents) and, on mismatch, fail with both
/// payloads pretty-printed so a rename shows up as an obvious diff.
let private assertMatchesFixture (fixtureFileName: string) (generatedPayload: string) =
  let fixturePath = Path.Combine(fixturesDir, fixtureFileName)
  match updateFixturesRequested () with
  | true ->
    File.WriteAllText(fixturePath, generatedPayload)
    printfn "SAGEFS_UPDATE_SSE_FIXTURES=1: wrote %s" fixturePath
  | false ->
    File.Exists fixturePath
    |> Expect.isTrue (sprintf "golden fixture '%s' should exist — run with SAGEFS_UPDATE_SSE_FIXTURES=1 to create it" fixtureFileName)
    let onDisk = File.ReadAllText fixturePath
    let onDiskNode = JsonNode.Parse onDisk
    let generatedNode = JsonNode.Parse generatedPayload
    let pretty (n: JsonNode) = n.ToJsonString(JsonSerializerOptions(WriteIndented = true))
    JsonNode.DeepEquals(onDiskNode, generatedNode)
    |> Expect.isTrue
         (sprintf
           "fixture '%s' no longer matches the REAL SseWriter output — this is either a rename that must be fixed, \
            or a deliberate change that must be re-pinned (set SAGEFS_UPDATE_SSE_FIXTURES=1 and re-run).\n\
            --- fixture on disk ---\n%s\n--- real server output now ---\n%s"
           fixtureFileName (pretty onDiskNode) (pretty generatedNode))

// ── Real payload builders — every value below is a real SageFs.Core type,
// constructed directly (no fakes, no fixtures-of-fixtures) ──────────────────

let private mkFallbackSummary () : TestSummary =
  { Total = 5; Passed = 3; Failed = 1; Stale = 1; Running = 0; Disabled = 0; Enabled = true }

let private mkFallbackDecision () : LiveTestingDecision =
  LiveTestingDecision.fromSelection
    (RerunCause.FileSaved "src/Compiled.fs")
    SelectionPrecision.ConservativeFallback
    []
    [| "Compiled.Tests.should_build_a" |]
    [||]
    "fallback rebuild"

let private mkFallbackActivity () : LiveTestActivity =
  LiveTestActivity.Settled
    { Passed = 3; Failed = 1; Running = 0; Stale = 1; NotYetRun = 0; Skipped = 0; Disabled = 0 }

let private mkSuppressedSummary () : TestSummary =
  { Total = 4; Passed = 2; Failed = 0; Stale = 2; Running = 0; Disabled = 0; Enabled = true }

let private mkSuppressedDecision () : LiveTestingDecision =
  LiveTestingDecision.fromSelection
    (RerunCause.KeystrokeBuffered "src/Architecture.fs")
    SelectionPrecision.SuppressedByPolicy
    [ "Architecture.Rule" ]
    [||]
    [| "Architecture.Tests.should_hold" |]
    "ambient live testing stayed quiet on purpose"

let private mkSuppressedActivity () : LiveTestActivity =
  LiveTestActivity.Settled
    { Passed = 2; Failed = 0; Running = 0; Stale = 2; NotYetRun = 0; Skipped = 0; Disabled = 0 }

let private mkCoverageBatch () : TestResultsBatchPayload =
  let entry (id: string) (displayName: string) (fullName: string) (line: int) (durationMs: float) : TestStatusEntry =
    { TestId = TestId.TestId id
      DisplayName = displayName
      FullName = fullName
      Origin = TestOrigin.SourceMapped("src/Tests.fs", line)
      Framework = TestFramework.Expecto
      Category = TestCategory.Unit
      CurrentPolicy = RunPolicy.OnEveryChange
      Status = TestRunStatus.Passed(TimeSpan.FromMilliseconds durationMs)
      PreviousStatus = TestRunStatus.Running }
  { Generation = RunGeneration 1
    Freshness = ResultFreshness.Fresh
    Completion = BatchCompletion.Complete(2, 2)
    Entries =
      [| entry "test-id-1" "should add" "Module.shouldAdd" 10 45.0
         entry "test-id-2" "should guard edges" "Module.shouldGuardEdges" 20 12.0 |]
    Summary = { Total = 2; Passed = 2; Failed = 0; Stale = 0; Running = 0; Disabled = 0; Enabled = true }
    LastDecision =
      Some(
        LiveTestingDecision.fromSelection
          (RerunCause.KeystrokeBuffered "src/Module.fs")
          SelectionPrecision.CoverageApproximation
          [ "Module.add" ]
          [| "Module.Tests.should_add"; "Module.Tests.should_guard_edges" |]
          [||]
          "coverage widened") }

// ── Tests ────────────────────────────────────────────────────────────────────

[<Tests>]
let sseGoldenFixtureGenerationTests =
  testList "SSE golden fixture generation (real emitter -> pinned fixture)" [

    testCase "summary-with-fallback-decision.json is exactly what formatTestSummaryEventWithDiscovery emits today" <| fun () ->
      let opts = productionJsonOpts ()
      let payload =
        formatTestSummaryEventWithDiscovery
          opts
          None
          (mkFallbackSummary ())
          (Some(mkFallbackDecision ()))
          (LiveTestDiscoveryState.ReadyWithTests 5)
          1L
          (mkFallbackActivity ())
        |> extractDataPayload
      assertMatchesFixture "summary-with-fallback-decision.json" payload

    testCase "results-batch-with-coverage-decision.json is exactly what formatTestResultsBatchEvent emits today" <| fun () ->
      let opts = productionJsonOpts ()
      let payload =
        formatTestResultsBatchEvent opts None (mkCoverageBatch ())
        |> extractDataPayload
      assertMatchesFixture "results-batch-with-coverage-decision.json" payload

    testCase "summary-with-suppressed-decision.json is exactly what formatTestSummaryEventWithDiscovery emits today" <| fun () ->
      let opts = productionJsonOpts ()
      let payload =
        formatTestSummaryEventWithDiscovery
          opts
          None
          (mkSuppressedSummary ())
          (Some(mkSuppressedDecision ()))
          (LiveTestDiscoveryState.ReadyWithTests 4)
          1L
          (mkSuppressedActivity ())
        |> extractDataPayload
      assertMatchesFixture "summary-with-suppressed-decision.json" payload

    testCase "no fixture file is orphaned from a generator (every *.json here has a pinning test above)" <| fun () ->
      let knownFixtures =
        set [ "summary-with-fallback-decision.json"
              "results-batch-with-coverage-decision.json"
              "summary-with-suppressed-decision.json" ]
      let onDisk =
        Directory.GetFiles(fixturesDir, "*.json")
        |> Array.map Path.GetFileName
        |> Set.ofArray
      onDisk
      |> Expect.equal
           "every fixture under fixtures/LiveTesting must be generated by a test in this file — \
            a hand-added fixture with no generator is exactly the drift this island closes"
           knownFixtures
  ]
