module SageFs.Tests.CoverageIntelMcpTests

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.Features
open SageFs.Features.LiveTesting
open SageFs.Features.CoverageIntel

// ── Helpers ──────────────────────────────────────────────────────────

let private mkTestId name = TestId.TestId name

let private mkNarrative causalChanges = {
  LastPassedAt = Some DateTimeOffset.UtcNow
  TimeSinceLastPass = Some (TimeSpan.FromMinutes 3.0)
  CausalChanges = causalChanges
  PropertyViolation = None
  Summary = "test broke"
}

let private mkSequencePoint file line branchId = {
  File = file
  Line = line
  Column = 0
  EndLine = line
  EndColumn = 0
  BranchId = branchId
}

let private mkInstrumentationMap (slots: SequencePoint array) = {
  Slots = slots
  TotalProbes = slots.Length
  TrackerTypeName = "TestTracker"
  HitsFieldName = "Hits"
}

/// Create a minimal McpContext that returns a specific SageFsModel.
let private mkCtxWithModel (model: SageFsModel) : McpContext =
  let diagEvent = Event<Features.DiagnosticsStore.T>()
  { FrictionStore = None
    DiagnosticsChanged = diagEvent.Publish
    StateChanged = None
    SessionOps = SessionManagementOps.stub
    SessionMap = ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = None
    GetElmModel = Some (fun () -> model)
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveSnapshotSink = None }

/// Build a SageFsModel with one discovered+failing test and a failure narrative.
let private mkModelWithTest
    (testId: TestId)
    (testName: string)
    (instrMaps: Map<string, InstrumentationMap array>)
    : SageFsModel =
  let tc : TestCase = {
    Id = testId
    FullName = testName
    DisplayName = testName
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit
  }
  let narrative = mkNarrative [ CausalChange.FileChanged "src.fs" ]
  let testState = {
    LiveTestState.empty with
      DiscoveredTests = [| tc |]
      FailureNarratives = Map.ofList [ testId, narrative ]
  }
  let cycleState = {
    LiveTestCycleState.empty with
      TestState = testState
      InstrumentationMaps = instrMaps
  }
  { SageFsModel.initial() with LiveTesting = cycleState }

// ── Test 1: toCoverageIntelJson produces correct verdict strings ──────

[<Tests>]
let verdictStringTests = testList "toCoverageIntelJson verdict strings" [

  testCase "WellCovered verdict serializes to 'WellCovered'" <| fun _ ->
    let report = {
      CoverageIntelReport.empty (mkTestId "t1") "my test" with
        Verdict = WellCovered
        CoveragePercent = 90.0
        CoveredBranches = 9
        TotalBranches = 10
    }
    let json = toCoverageIntelJson report
    json.Verdict |> Expect.equal "should be WellCovered" "WellCovered"

  testCase "PartialBlindSpot verdict serializes to 'PartialBlindSpot'" <| fun _ ->
    let report = {
      CoverageIntelReport.empty (mkTestId "t2") "partial test" with
        Verdict = PartialBlindSpot
        CoveragePercent = 55.0
    }
    let json = toCoverageIntelJson report
    json.Verdict |> Expect.equal "should be PartialBlindSpot" "PartialBlindSpot"

  testCase "DiagnosticBlindSpot verdict serializes to 'DiagnosticBlindSpot'" <| fun _ ->
    let report = {
      CoverageIntelReport.empty (mkTestId "t3") "blind test" with
        Verdict = DiagnosticBlindSpot
        CoveragePercent = 20.0
    }
    let json = toCoverageIntelJson report
    json.Verdict |> Expect.equal "should be DiagnosticBlindSpot" "DiagnosticBlindSpot"

  testCase "toCoverageIntelJson includes summary from CoverageIntel.summarize" <| fun _ ->
    let report = {
      CoverageIntelReport.empty (mkTestId "t4") "named test" with
        Verdict = WellCovered
        CoveragePercent = 85.0
        CoveredBranches = 17
        TotalBranches = 20
    }
    let json = toCoverageIntelJson report
    let expected = CoverageIntel.summarize report
    json.Summary |> Expect.equal "summary should match" expected

  testCase "toCoverageIntelJson maps blind spots correctly" <| fun _ ->
    let gap : BranchGap = { FilePath = "a.fs"; Line = 10; EndLine = 10; BranchId = 3; NearestCoveredLine = Some 8 }
    let report = {
      CoverageIntelReport.empty (mkTestId "t5") "gap test" with
        BlindSpots = [ gap ]
        Verdict = PartialBlindSpot
    }
    let json = toCoverageIntelJson report
    json.BlindSpots |> Expect.hasLength "one blind spot" 1
    json.BlindSpots.[0].FilePath |> Expect.equal "file path" "a.fs"
    json.BlindSpots.[0].BranchId |> Expect.equal "branch id" 3

  testCase "toCoverageIntelJson maps correlated failures to string IDs" <| fun _ ->
    let report = {
      CoverageIntelReport.empty (mkTestId "t6") "corr test" with
        CorrelatedFailures = [ mkTestId "other1"; mkTestId "other2" ]
        Verdict = WellCovered
    }
    let json = toCoverageIntelJson report
    json.CorrelatedFailures |> Expect.hasLength "two correlated" 2
    json.CorrelatedFailures |> Expect.contains "other1 in list" "other1"
    json.CorrelatedFailures |> Expect.contains "other2 in list" "other2"
]

// ── Test 2: explain_test_failure includes coverageIntel when maps present ─

[<Tests>]
let explainWithCoverageTests = testList "explain_test_failure coverageIntel present" [

  testTask "response includes non-null coverageIntel when instrumentation maps exist" {
    let testId = mkTestId "myTest"
    let maps = Map.ofList [
      "sess1", [| mkInstrumentationMap [| mkSequencePoint "src.fs" 10 0 |] |]
    ]
    let model = mkModelWithTest testId "my failing test" maps
    let ctx = mkCtxWithModel model

    let! json =
      McpTools.explainTestFailure ctx "my failing test"

    let doc = JsonDocument.Parse(json)
    let narratives = doc.RootElement.GetProperty("Narratives")
    narratives.GetArrayLength() |> Expect.equal "one narrative" 1
    let narr = narratives.[0]
    let coverageIntel = narr.GetProperty("CoverageIntel")
    coverageIntel.ValueKind
    |> Expect.notEqual "coverageIntel should not be null" JsonValueKind.Null
  }

  testTask "coverageIntel includes verdict field when maps are present" {
    let testId = mkTestId "verdictTest"
    let maps = Map.ofList [
      "sess1", [| mkInstrumentationMap [| mkSequencePoint "src.fs" 5 0 |] |]
    ]
    let model = mkModelWithTest testId "verdict test" maps
    let ctx = mkCtxWithModel model

    let! json =
      McpTools.explainTestFailure ctx "verdict test"

    let doc = JsonDocument.Parse(json)
    let narr = doc.RootElement.GetProperty("Narratives").[0]
    let intel = narr.GetProperty("CoverageIntel")
    let verdict = intel.GetProperty("Verdict").GetString()
    verdict
    |> Expect.isNotNull "verdict should be a string"
    // 0/1 coverage → DiagnosticBlindSpot (causal file is src.fs, 1 point, no bitmap hits)
    verdict |> Expect.equal "verdict should be DiagnosticBlindSpot" "DiagnosticBlindSpot"
  }
]

// ── Test 3: explain_test_failure has coverageIntel=null when no maps ───

[<Tests>]
let explainWithoutCoverageTests = testList "explain_test_failure coverageIntel null" [

  testTask "coverageIntel is null when InstrumentationMaps is empty" {
    let testId = mkTestId "noMapsTest"
    let model = mkModelWithTest testId "no maps test" Map.empty
    let ctx = mkCtxWithModel model

    let! json =
      McpTools.explainTestFailure ctx "no maps test"

    let doc = JsonDocument.Parse(json)
    let narratives = doc.RootElement.GetProperty("Narratives")
    narratives.GetArrayLength() |> Expect.equal "one narrative" 1
    let narr = narratives.[0]
    let coverageIntel = narr.GetProperty("CoverageIntel")
    coverageIntel.ValueKind
    |> Expect.equal "coverageIntel should be null when no maps" JsonValueKind.Null
  }

  testTask "response is well-formed JSON with coverageIntel null field when no maps" {
    let testId = mkTestId "structTest"
    let model = mkModelWithTest testId "struct test" Map.empty
    let ctx = mkCtxWithModel model

    let! json =
      McpTools.explainTestFailure ctx "struct test"

    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement
    // Response structure is preserved
    root.GetProperty("MatchCount").GetInt32() |> Expect.equal "match count" 1
    root.GetProperty("Narratives").GetArrayLength() |> Expect.equal "narratives count" 1
    let narr = root.GetProperty("Narratives").[0]
    // All original fields still present
    narr.TryGetProperty("DisplayName") |> fst |> Expect.isTrue "DisplayName field present"
    narr.TryGetProperty("Summary") |> fst |> Expect.isTrue "Summary field present"
    narr.TryGetProperty("CoverageIntel") |> fst |> Expect.isTrue "CoverageIntel field present"
  }
]
