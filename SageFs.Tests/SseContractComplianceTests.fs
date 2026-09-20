module SageFs.Tests.SseContractComplianceTests

open System
open System.Reflection
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs.SseWriter
open SageFs.Features.LiveTesting
open SageFs.Features.EvalDiff
open SageFs.Features.CellDependencyGraph
open SageFs.Features.BindingExplorer
open SageFs.Features.EvalTimeline
open SageFs.Features.Diagnostician
open SageFs.Features.FsiOutputParser
open SageFs.Features.EvalProvenance
open SageFs.Features.Ghostwriter

// ── JSON options matching daemon configuration ──

let private jsonOpts =
  let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
  opts.Converters.Add(JsonFSharpConverter())
  opts

// ── Helpers ──

let private extractDataPayload (sse: string) =
  sse.Split('\n')
  |> Array.choose (fun line ->
    match line.StartsWith("data: ") with
    | true -> Some (line.Substring(6))
    | false -> None)
  |> String.concat "\n"

let private countDataLines (sse: string) =
  sse.Split('\n')
  |> Array.filter (fun l -> l.StartsWith("data: "))
  |> Array.length

let private hasJsonProperty (name: string) (el: JsonElement) =
  let mutable p = Unchecked.defaultof<JsonElement>
  el.TryGetProperty(name, &p)

let private assertJsonProperties (eventName: string) (expectedProps: string list) (json: string) =
  use doc = JsonDocument.Parse(json)
  let root = doc.RootElement
  for prop in expectedProps do
    root |> hasJsonProperty prop
    |> Expect.isTrue (sprintf "%s JSON should have property '%s'" eventName prop)

let private toSnakeCase (s: string) =
  Regex.Replace(s, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant()

// ── Minimal test data factories ──

let private mkTestSummary () : TestSummary =
  { Total = 50; Passed = 45; Failed = 3; Stale = 2; Running = 0; Disabled = 1; Enabled = true }

let private mkTestResultsBatch (n: int) : TestResultsBatchPayload =
  let entries =
    [| for i in 1 .. n do
        { TestStatusEntry.TestId = TestId.TestId (sprintf "test_%d" i)
          DisplayName = sprintf "test %d" i
          FullName = sprintf "Mod.test_%d" i
          Origin = TestOrigin.ReflectionOnly
          Framework = TestFramework.Expecto
          Category = TestCategory.Unit
          CurrentPolicy = RunPolicy.OnEveryChange
          Status = TestRunStatus.Passed (TimeSpan.FromMilliseconds 10.0)
          PreviousStatus = TestRunStatus.Passed (TimeSpan.FromMilliseconds 10.0) } |]
  { Generation = RunGeneration 1
    Freshness = ResultFreshness.Fresh
    Completion = BatchCompletion.Complete (n, n)
    Entries = entries
    Summary = mkTestSummary ()
    LastDecision = None }

let private mkFileAnnotations () : FileAnnotations =
  { FilePath = "Test.fs"
    TestAnnotations =
      [| { TestLineAnnotation.Line = 10
           TestId = TestId.TestId "test1"
           DisplayName = "test 1"
           Status = TestRunStatus.Passed (TimeSpan.FromMilliseconds 5.0)
           Freshness = AnnotationFreshness.Current } |]
    CoverageAnnotations =
      [| { CoverageLineAnnotation.Line = 5; EndLine = 5; EndColumn = 40
           Detail = CoverageStatus.Covered (1, CoverageHealth.AllPassing)
           CoveringTestIds = [| TestId.TestId "t1" |]
           BranchCoverage = None } |]
    InlineFailures = [||]
    CodeLenses = [||]
    PerformanceAnnotations = [||] }

let private mkDiffSummary () : DiffSummary =
  { Lines = [ Added "let x = 1"; Removed "let y = 2"; Unchanged "let z = 3" ]
    AddedCount = 1; RemovedCount = 1; ModifiedCount = 0; UnchangedCount = 1 }

let private mkCellGraph () : CellGraph =
  { Cells =
      [ 0, { CellInfo.Id = 0; Source = "let x = 1;;"; Produces = [ "x" ]; Consumes = [] }
        1, { CellInfo.Id = 1; Source = "let y = x + 1;;"; Produces = [ "y" ]; Consumes = [ "x" ] } ]
      |> Map.ofList
    Edges = [ (0, 1) ] }

let private mkBindingScopeSnapshot () : BindingScopeSnapshot =
  let bindings =
    [ { BindingInfo.Name = "x"; TypeSig = "int"; Value = Some "42"
        CellIndex = 0; ShadowedBy = []; ReferencedIn = [ 1 ] } ]
  { Bindings = bindings
    ActiveBindings = bindings |> List.map (fun b -> b.Name, b) |> Map.ofList
    ShadowedBindings = [] }

let private mkTimelineStats () : TimelineStats =
  { Count = 10; P50Ms = Some 42.0; P95Ms = Some 180.0; P99Ms = Some 450.0
    MeanMs = Some 67.0; Sparkline = "▁▂▃▅▇" }

let private mkFailureNarratives () : Map<TestId, FailureNarrative> =
  [ TestId.TestId "fail1",
    { LastPassedAt = Some (DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
      TimeSinceLastPass = Some (TimeSpan.FromMinutes 5.0)
      CausalChanges = [ CausalChange.SymbolChanged "myFunc" ]
      PropertyViolation = None
      Summary = "Test failed because myFunc changed" } ]
  |> Map.ofList

let private mkTestSourceLocations () : TestSourceLocation list =
  [ { CellId = 0; TestName = "test1"; FilePath = "Test.fs"; StartLine = 10; EndLine = 15 } ]

let private mkBindingValues () : BindingValue list =
  [ { Name = "x"; TypeSig = "int"; DisplayValue = "42"; IsTruncated = false
      IsFunctionValue = false; CellIndex = 0; EvalDurationMs = 1.5; SourceLine = 1 } ]

let private mkFsiBindings () : FsiBinding array =
  [| { Name = "x"; TypeSig = "int"; Value = Some "42"; ShadowCount = 0 } |]

let private mkDiagnosticReport () : DiagnosticReport =
  { Failures =
      [ { TestId = TestId.TestId "diag1"
          TestName = "should compute"
          Narrative =
            { LastPassedAt = None; TimeSinceLastPass = None
              CausalChanges = [ CausalChange.SymbolChanged "compute" ]
              PropertyViolation = None; Summary = "Failed" }
          CausalCells = [ 0 ]
          Staleness = Map.ofList [ 0, Staleness.Fresh ] } ]
    AffectedCells = [ 0, Staleness.Fresh ]
    RipplePlan = None
    SuggestedFixes = [ { Code = "fix"; Explanation = "apply fix"; Confidence = 0.9 } ]
    PerformanceContext = Some (mkTimelineStats ())
    Severity = DiagnosticSeverity.Warning
    Summary = "1 failure" }

// ── Cohort test data factories (item 15a) ──
// Fully-qualified throughout: `SageFs.Cohort.TestId` would otherwise collide
// with `Features.LiveTesting.TestId`, already opened unqualified above.

module private CohortTestData =
  let private clock = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
  let private noEntropy : byte[] = [||]
  let private alice = SageFs.MemberTable.MemberId.Minted "alice"

  // `Result.Error` (not the bare `Error` pattern) because
  // `Features.EvalTimeline.EvalStatus` — opened unqualified above — also
  // declares a nullary `Error` case that would otherwise shadow it here.
  let private applyOk state cmd =
    match SageFs.Cohort.decide clock noEntropy state cmd with
    | Ok(s, _, _) -> s
    | Result.Error e -> failwithf "unexpected cohort decide error: %A" e

  let private joinAndClaim () =
    SageFs.Cohort.CohortState.empty ()
    |> fun s -> applyOk s (SageFs.Cohort.CohortCommand.Join(alice, SageFs.Cohort.JoinableRole.Implementer, Some "sess-1"))
    |> fun s -> applyOk s (SageFs.Cohort.CohortCommand.AcquireClaim(alice, SageFs.Cohort.ClaimScope.File "src/Foo.fs", "testing"))

  /// One `Held` claim by `alice` over `src/Foo.fs`.
  let mkClaim () : SageFs.Cohort.Claim<SageFs.MemberTable.MemberId> =
    (joinAndClaim ()).Claims |> Map.toList |> List.exactlyOne |> snd

  /// alice's claim, presented as a landing and driven to
  /// `Blocked(FailingTests, FixTests)` — shared by `mkBlockedLanding` (the
  /// `LandingRequest` for `landing_changed`'s test) and `mkFrame` (the whole
  /// `CohortFrame` for `cohort_matrix`'s test), so both prove the blocker/
  /// next-action wire shape round-trips from the SAME underlying state.
  let private stateWithBlockedLanding () : SageFs.Cohort.CohortState<SageFs.MemberTable.MemberId> * SageFs.Cohort.LandingId =
    let state0 = joinAndClaim ()
    let claimId, claim = state0.Claims |> Map.toList |> List.exactlyOne
    let state1 = applyOk state0 (SageFs.Cohort.CohortCommand.RequestLanding(alice, [ claimId, claim.Fence ], [ "abc123" ], "land it"))
    let landingId = state1.Landings |> Map.toList |> List.exactlyOne |> fst
    let state2 = applyOk state1 (SageFs.Cohort.CohortCommand.RebaseCompleted(landingId, Ok "def456"))
    let state3 = applyOk state2 (SageFs.Cohort.CohortCommand.AffectedComputed(landingId, [ SageFs.Cohort.TestId "t1" ]))
    let state4 = applyOk state3 (SageFs.Cohort.CohortCommand.TestsCompleted(landingId, [ SageFs.Cohort.TestId "t1" ]))
    state4, landingId

  /// One landing, driven to `Blocked(FailingTests, FixTests)` so the
  /// blocker/next-action wire shape round-trips too.
  let mkBlockedLanding () : SageFs.Cohort.LandingRequest<SageFs.MemberTable.MemberId> =
    let state, landingId = stateWithBlockedLanding ()
    state.Landings |> Map.find landingId

  /// A one-member, one-claim, two-test frame carrying one Blocked landing
  /// (dogfood gap closure: `cohort_matrix`'s landing row is proven non-empty,
  /// not merely present-but-vacant).
  let mkFrame () : SageFs.Cohort.CohortFrame<SageFs.MemberTable.MemberId> =
    let state, _ = stateWithBlockedLanding ()
    let head : SageFs.Cohort.LedgerHead<SageFs.MemberTable.MemberId> = { Seq = 3L<SageFs.Measures.ledgerSeq>; State = state }
    let snapshot : SageFs.Cohort.SessionSnapshot<SageFs.MemberTable.MemberId> =
      { Member = Some alice
        SessionId = "sess-1"
        Generation = 2L
        PassingTests = [ SageFs.Cohort.TestId "t1" ]
        FailingTests = [ SageFs.Cohort.TestId "t2" ]
        StaleTests = [] }
    SageFs.Cohort.project head [| snapshot |]

  /// A second member (`bob`) whose save into alice's `Held` claim produces
  /// the `ClaimViolationObserved` `save_observed` reports on.
  let mkSaveObservation () : SageFs.Cohort.Claim<SageFs.MemberTable.MemberId> * SageFs.MemberTable.MemberId * SageFs.MemberTable.MemberId * string =
    let bob = SageFs.MemberTable.MemberId.Minted "bob"
    let state0 = joinAndClaim ()
    let state1 = applyOk state0 (SageFs.Cohort.CohortCommand.Join(bob, SageFs.Cohort.JoinableRole.Implementer, Some "sess-2"))
    let claim = state1.Claims |> Map.toList |> List.exactlyOne |> snd
    claim, bob, alice, "src/Foo.fs"

// ── Tests ──

[<Tests>]
let sseContractComplianceTests = testList "SSE contract compliance" [

  // ── Group 1: Registry exhaustiveness ──

  testList "registry exhaustiveness" [
    testCase "allSseEventTypes has exactly 22 items" <| fun () ->
      allSseEventTypes |> List.length
      |> Expect.equal "should have exactly 22 event types (domain_model deleted, roast-8 §2: zero production callers of the emitter or its data source)" 22

    testCase "allSseEventTypes has no duplicates" <| fun () ->
      let distinct = allSseEventTypes |> List.distinct
      distinct |> List.length
      |> Expect.equal "all event types should be unique" (allSseEventTypes |> List.length)

    testCase "every formatXxxEvent function has a matching registry entry" <| fun () ->
      let sseWriterType = typeof<FsiBinding>.DeclaringType
      let formatMethods =
        sseWriterType.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
        |> Array.filter (fun m ->
          m.Name.StartsWith("format")
          && m.Name.EndsWith("Event")
          && m.Name <> "formatSseEvent")
        |> Array.map (fun m ->
          m.Name.Substring("format".Length, m.Name.Length - "format".Length - "Event".Length)
          |> toSnakeCase)
      (formatMethods |> Array.length, 0)
      |> Expect.isGreaterThan "should find at least 22 format functions"
      for derivedName in formatMethods do
        allSseEventTypes |> List.contains derivedName
        |> Expect.isTrue (sprintf "'%s' derived from formatter should exist in registry" derivedName)
  ]

  // ── Group 2: JSON shape contracts ──

  testList "JSON shape contracts" [
    testCase "warmup_progress has expected properties" <| fun () ->
      formatWarmupProgressEvent jsonOpts None 1 5 "Loading..."
      |> extractDataPayload
      |> assertJsonProperties "warmup_progress"
        [ "step"; "total"; "message"; "progress"; "phase" ]

    testCase "test_summary has expected properties" <| fun () ->
      formatTestSummaryEvent jsonOpts None (mkTestSummary ()) None
      |> extractDataPayload
      |> assertJsonProperties "test_summary"
        [ "total"; "passed"; "failed"; "stale"; "running"; "disabled"; "enabled"; "lastDecision" ]

    testCase "test_results_batch has expected properties" <| fun () ->
      formatTestResultsBatchEvent jsonOpts None (mkTestResultsBatch 2)
      |> extractDataPayload
      |> assertJsonProperties "test_results_batch"
        [ "generation"; "freshness"; "completion"; "entries"; "summary"; "lastDecision" ]

    testCase "file_annotations has expected properties" <| fun () ->
      formatFileAnnotationsEvent jsonOpts None (mkFileAnnotations ())
      |> extractDataPayload
      |> assertJsonProperties "file_annotations"
        [ "filePath"; "testAnnotations"; "coverageAnnotations"
          "inlineFailures"; "codeLenses"; "performanceAnnotations" ]

    testCase "failure_narratives array elements have expected properties" <| fun () ->
      let json =
        formatFailureNarrativesEvent jsonOpts None (mkFailureNarratives ())
        |> extractDataPayload
      use doc = JsonDocument.Parse(json)
      doc.RootElement.ValueKind
      |> Expect.equal "root should be array" JsonValueKind.Array
      let first = doc.RootElement.EnumerateArray() |> Seq.head
      for prop in [ "testId"; "lastPassedAt"; "timeSinceLastPass"
                    "causalChanges"; "propertyViolation"; "summary" ] do
        first |> hasJsonProperty prop
        |> Expect.isTrue (sprintf "failure_narratives element should have '%s'" prop)

    testCase "test_source_locations has expected properties" <| fun () ->
      formatTestSourceLocationsEvent jsonOpts None (mkTestSourceLocations ())
      |> extractDataPayload
      |> assertJsonProperties "test_source_locations" [ "locations" ]

    testCase "bindings_snapshot has expected properties" <| fun () ->
      formatBindingsSnapshotEvent jsonOpts None (mkBindingValues ()) 42 (Some "Foo.fs") (mkFsiBindings ())
      |> extractDataPayload
      |> assertJsonProperties "bindings_snapshot" [ "bindings"; "bindingValues"; "blockStartLine"; "filePath" ]

    testCase "test_trace preserves JSON payload" <| fun () ->
      let traceJson = """{"enabled":true,"providers":["Expecto"],"summary":{"total":100}}"""
      let payload = formatTestTraceEvent None traceJson |> extractDataPayload
      use doc = JsonDocument.Parse(payload)
      for prop in [ "enabled"; "providers"; "summary" ] do
        doc.RootElement |> hasJsonProperty prop
        |> Expect.isTrue (sprintf "test_trace should preserve '%s'" prop)

    testCase "test_summary carries nullable lastDecision for explanation-aware clients" <| fun () ->
      let payload =
        formatTestSummaryEvent jsonOpts None (mkTestSummary ())
          (Some (
            LiveTestingDecision.fromSelection
              (RerunCause.FileSaved "src/Compiled.fs")
              SelectionPrecision.ConservativeFallback
              []
              [| "Compiled.Tests.should_build_a" |]
              [||]
              "fallback rebuild"))
        |> extractDataPayload
      use doc = JsonDocument.Parse(payload)
      let lastDecision = doc.RootElement.GetProperty("lastDecision")
      lastDecision |> hasJsonProperty "precision"
      |> Expect.isTrue "test_summary lastDecision should expose precision"
      lastDecision |> hasJsonProperty "trust"
      |> Expect.isTrue "test_summary lastDecision should expose trust"

    testCase "test_results_batch carries nullable lastDecision for explanation-aware clients" <| fun () ->
      let payload =
        let batch = mkTestResultsBatch 2
        { batch with
            LastDecision =
              Some (
                LiveTestingDecision.fromSelection
                  (RerunCause.KeystrokeBuffered "src/Module.fs")
                  SelectionPrecision.CoverageApproximation
                  [ "Module.add" ]
                  [| "Module.Tests.should_add" |]
                  [||]
                  "coverage widened") }
        |> formatTestResultsBatchEvent jsonOpts None
        |> extractDataPayload
      use doc = JsonDocument.Parse(payload)
      let lastDecision = doc.RootElement.GetProperty("lastDecision")
      lastDecision |> hasJsonProperty "cause"
      |> Expect.isTrue "test_results_batch lastDecision should expose cause"
      lastDecision |> hasJsonProperty "reason"
      |> Expect.isTrue "test_results_batch lastDecision should expose reason"

    testCase "eval_diff has expected properties" <| fun () ->
      formatEvalDiffEvent jsonOpts None (mkDiffSummary ())
      |> extractDataPayload
      |> assertJsonProperties "eval_diff"
        [ "lines"; "added"; "removed"; "modified"; "unchanged" ]

    testCase "eval_started has expected properties" <| fun () ->
      formatEvalStartedEvent jsonOpts None "test.fsx" 42
      |> extractDataPayload
      |> assertJsonProperties "eval_started" [ "filePath"; "blockStartLine" ]

    testCase "eval_result has expected properties" <| fun () ->
      formatEvalResultEvent jsonOpts None "test.fsx" 1 "val x = 42" true 23.5
      |> extractDataPayload
      |> assertJsonProperties "eval_result"
        [ "filePath"; "blockStartLine"; "output"; "success"; "durationMs" ]

    testCase "cell_dependencies has expected properties" <| fun () ->
      formatCellDependenciesEvent jsonOpts None (mkCellGraph ())
      |> extractDataPayload
      |> assertJsonProperties "cell_dependencies" [ "nodes"; "edges" ]

    testCase "binding_scope_map has expected properties" <| fun () ->
      formatBindingScopeMapEvent jsonOpts None (mkBindingScopeSnapshot ())
      |> extractDataPayload
      |> assertJsonProperties "binding_scope_map"
        [ "bindings"; "activeCount"; "shadowedCount" ]

    testCase "eval_timeline has expected properties" <| fun () ->
      formatEvalTimelineEvent jsonOpts None (mkTimelineStats ())
      |> extractDataPayload
      |> assertJsonProperties "eval_timeline"
        [ "count"; "p50Ms"; "p95Ms"; "p99Ms"; "meanMs"; "sparkline" ]

    testCase "diagnosis_ready has expected properties" <| fun () ->
      formatDiagnosisReadyEvent jsonOpts None (mkDiagnosticReport ())
      |> extractDataPayload
      |> assertJsonProperties "diagnosis_ready"
        [ "severity"; "failureCount"; "affectedCells"; "suggestionCount"
          "topSuggestions"; "failures"; "performance"; "summary" ]

    testCase "Cohort cohort_matrix has expected properties" <| fun () ->
      formatCohortMatrixEvent jsonOpts (CohortTestData.mkFrame ())
      |> extractDataPayload
      |> assertJsonProperties "cohort_matrix"
        // "integrationHead"/"landings" (dogfood gap closure — CohortFrame's
        // additive Landings/IntegrationHead projection): CohortTestData.mkFrame's
        // fixture carries the blocked landing `mkBlockedLanding` builds, so this
        // also proves the payload isn't just present but non-empty for a real
        // in-flight landing.
        [ "version"; "members"; "claims"; "tests"; "rows"; "integrationHead"; "landings" ]

    testCase "Cohort cohort_matrix landing row exposes id/requester/state/blocker/nextAction" <| fun () ->
      let payload =
        formatCohortMatrixEvent jsonOpts (CohortTestData.mkFrame ())
        |> extractDataPayload
      use doc = JsonDocument.Parse(payload)
      let landings = doc.RootElement.GetProperty("landings")
      (landings.GetArrayLength(), 0) |> Expect.isGreaterThan "the fixture's landing must appear as a row"
      let row = landings.[0]
      for prop in [ "id"; "requester"; "statement"; "commits"; "state"; "queuePosition"; "blocker"; "nextAction" ] do
        row |> hasJsonProperty prop
        |> Expect.isTrue (sprintf "cohort_matrix landing row should have property '%s'" prop)

    testCase "Cohort claim_changed has expected properties" <| fun () ->
      formatClaimChangedEvent jsonOpts "acquired" (CohortTestData.mkClaim ())
      |> extractDataPayload
      |> assertJsonProperties "claim_changed"
        [ "claimId"; "scope"; "holder"; "fence"; "kind" ]

    testCase "Cohort landing_changed has expected properties" <| fun () ->
      formatLandingChangedEvent jsonOpts (CohortTestData.mkBlockedLanding ())
      |> extractDataPayload
      |> assertJsonProperties "landing_changed"
        [ "landingId"; "requester"; "state"; "blocker"; "nextAction" ]

    testCase "Cohort landing_changed exposes blocker/nextAction detail when Blocked" <| fun () ->
      let payload =
        formatLandingChangedEvent jsonOpts (CohortTestData.mkBlockedLanding ())
        |> extractDataPayload
      use doc = JsonDocument.Parse(payload)
      doc.RootElement.GetProperty("state").GetString()
      |> Expect.equal "landing should be blocked" "blocked"
      let blocker = doc.RootElement.GetProperty("blocker")
      blocker |> hasJsonProperty "kind"
      |> Expect.isTrue "blocker should expose kind"
      let nextAction = doc.RootElement.GetProperty("nextAction")
      nextAction |> hasJsonProperty "kind"
      |> Expect.isTrue "nextAction should expose kind"

    testCase "Cohort save_observed has expected properties" <| fun () ->
      let claim, observer, holder, path = CohortTestData.mkSaveObservation ()
      formatSaveObservedEvent jsonOpts claim observer holder path
      |> extractDataPayload
      |> assertJsonProperties "save_observed"
        [ "claimId"; "observer"; "holder"; "scope"; "path" ]
  ]

  // ── Group 3: Event type name conventions ──

  testList "event type name conventions" [
    testCase "all event types are snake_case" <| fun () ->
      let snakeCasePattern = Regex("^[a-z][a-z0-9_]*$")
      for name in allSseEventTypes do
        snakeCasePattern.IsMatch(name)
        |> Expect.isTrue (sprintf "event type '%s' should be snake_case" name)

    testCase "no event type exceeds 30 characters" <| fun () ->
      for name in allSseEventTypes do
        (name.Length, 31)
        |> Expect.isLessThan (sprintf "event type '%s' should be <= 30 chars" name)

    testCase "all event types are ASCII-only" <| fun () ->
      for name in allSseEventTypes do
        name |> Seq.forall (fun c -> int c < 128)
        |> Expect.isTrue (sprintf "event type '%s' should be ASCII-only" name)
  ]

  // ── Group 4: Multiline safety ──

  testList "multiline safety" [
    testCase "large test_results_batch produces single data line" <| fun () ->
      formatTestResultsBatchEvent jsonOpts None (mkTestResultsBatch 150)
      |> countDataLines
      |> Expect.equal "150-entry batch should have exactly 1 data line" 1

    testCase "large test_source_locations produces single data line" <| fun () ->
      let locations =
        [ for i in 1 .. 100 do
            { CellId = i; TestName = sprintf "test_%d" i; FilePath = sprintf "Mod%d.fs" i
              StartLine = i * 10; EndLine = i * 10 + 5 } ]
      formatTestSourceLocationsEvent jsonOpts None locations
      |> countDataLines
      |> Expect.equal "100-location payload should have exactly 1 data line" 1

    testCase "large cell_dependencies produces single data line" <| fun () ->
      let graph : CellGraph =
        { Cells =
            [ for i in 0 .. 99 do
                i, { CellInfo.Id = i; Source = sprintf "cell_%d" i
                     Produces = [ sprintf "v_%d" i ]; Consumes = [] } ]
            |> Map.ofList
          Edges = [ for i in 0 .. 98 do (i, i + 1) ] }
      formatCellDependenciesEvent jsonOpts None graph
      |> countDataLines
      |> Expect.equal "100-cell graph should have exactly 1 data line" 1

    testCase "large binding_scope_map produces single data line" <| fun () ->
      let bindings =
        [ for i in 1 .. 100 do
            { BindingInfo.Name = sprintf "b_%d" i; TypeSig = "int"; Value = None
              CellIndex = i; ShadowedBy = []; ReferencedIn = [] } ]
      let snapshot =
        { Bindings = bindings
          ActiveBindings = bindings |> List.map (fun b -> b.Name, b) |> Map.ofList
          ShadowedBindings = [] }
      formatBindingScopeMapEvent jsonOpts None snapshot
      |> countDataLines
      |> Expect.equal "100-binding scope map should have exactly 1 data line" 1
  ]
]
