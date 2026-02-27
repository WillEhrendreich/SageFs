module SageFs.Tests.SseWriterTests

open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open Expecto
open Expecto.Flip
open SageFs.SseWriter
open SageFs.Features.LiveTesting

[<Tests>]
let sseTests = testList "SSE Writer" [
  testList "formatSseEvent" [
    testCase "formats single-line event" <| fun () ->
      formatSseEvent "test_summary" """{"total":5}"""
      |> Expect.equal "should format correctly"
           "event: test_summary\ndata: {\"total\":5}\n\n"

    testCase "handles empty data" <| fun () ->
      formatSseEvent "ping" ""
      |> Expect.equal "should format with empty data"
           "event: ping\ndata: \n\n"
  ]

  testList "formatSseEventMultiline" [
    testCase "formats multiline event" <| fun () ->
      formatSseEventMultiline "update" [ "line1"; "line2"; "line3" ]
      |> Expect.equal "should format each line with data:"
           "event: update\ndata: line1\ndata: line2\ndata: line3\n\n"

    testCase "handles empty lines list" <| fun () ->
      formatSseEventMultiline "empty" []
      |> Expect.equal "should format with no data lines"
           "event: empty\n\n"

    testCase "handles single line" <| fun () ->
      formatSseEventMultiline "single" [ "only" ]
      |> Expect.equal "should format single line"
           "event: single\ndata: only\n\n"
  ]

  testList "trySendBytes" [
    testCase "writes bytes to stream successfully" <| fun () ->
      use ms = new MemoryStream()
      let bytes = Encoding.UTF8.GetBytes("hello")
      let result = trySendBytes ms bytes |> Async.AwaitTask |> Async.RunSynchronously
      result |> Expect.isOk "should succeed"
      ms.ToArray() |> Encoding.UTF8.GetString
      |> Expect.equal "should have written content" "hello"

    testCase "returns Error on disposed stream" <| fun () ->
      let ms = new MemoryStream()
      ms.Dispose()
      let bytes = Encoding.UTF8.GetBytes("hello")
      let result = trySendBytes ms bytes |> Async.AwaitTask |> Async.RunSynchronously
      result |> Expect.isError "should fail on disposed stream"
  ]

  testList "trySendSseEvent" [
    testCase "sends formatted SSE event to stream" <| fun () ->
      use ms = new MemoryStream()
      let result = trySendSseEvent ms "test" "data" |> Async.AwaitTask |> Async.RunSynchronously
      result |> Expect.isOk "should succeed"
      ms.ToArray() |> Encoding.UTF8.GetString
      |> Expect.equal "should have formatted SSE" "event: test\ndata: data\n\n"
  ]

  testList "formatTestSummaryEvent" [
    testCase "serializes TestSummary to SSE with PascalCase" <| fun () ->
      // Production SSE uses default JsonSerializerOptions + JsonFSharpConverter (PascalCase)
      // NOT camelCase — see McpServer.fs sseJsonOpts
      let opts = JsonSerializerOptions()
      opts.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
      let summary: SageFs.Features.LiveTesting.TestSummary = {
        Total = 10; Passed = 8; Failed = 1; Stale = 1; Running = 0; Disabled = 0; Enabled = true
      }
      let result = formatTestSummaryEvent opts None summary
      result |> Expect.stringContains "should contain event type" "event: test_summary"
      result |> Expect.stringContains "should contain PascalCase Total" "\"Total\":10"
      result |> Expect.stringContains "should contain PascalCase Passed" "\"Passed\":8"
      result |> Expect.stringContains "should end with double newline" "\n\n"
  ]
]

/// Production-equivalent SSE serialization options (must match McpServer.fs sseJsonOpts)
let productionSseOpts =
  let opts = JsonSerializerOptions()
  opts.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
  opts

let extractSseData (sseEvent: string) =
  sseEvent.Split('\n')
  |> Array.tryFind (fun l -> l.StartsWith("data: "))
  |> Option.map (fun l -> l.Substring(6))

[<Tests>]
let wireProtocolTests = testList "Wire Protocol Contract" [

  testList "TestSummary shape" [
    testCase "has all expected PascalCase fields" <| fun () ->
      let summary: SageFs.Features.LiveTesting.TestSummary = {
        Total = 10; Passed = 7; Failed = 1; Stale = 1; Running = 0; Disabled = 1; Enabled = true
      }
      let json = JsonSerializer.Serialize(summary, productionSseOpts)
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      let mutable v = Unchecked.defaultof<JsonElement>
      root.TryGetProperty("Total", &v) |> Expect.isTrue "has Total"
      root.TryGetProperty("Passed", &v) |> Expect.isTrue "has Passed"
      root.TryGetProperty("Failed", &v) |> Expect.isTrue "has Failed"
      root.TryGetProperty("Stale", &v) |> Expect.isTrue "has Stale"
      root.TryGetProperty("Running", &v) |> Expect.isTrue "has Running"
      root.TryGetProperty("Disabled", &v) |> Expect.isTrue "has Disabled"

    testCase "round-trip values are correct" <| fun () ->
      let summary: SageFs.Features.LiveTesting.TestSummary = {
        Total = 47; Passed = 40; Failed = 3; Stale = 2; Running = 1; Disabled = 1; Enabled = true
      }
      let sse = formatTestSummaryEvent productionSseOpts None summary
      let data = extractSseData sse |> Option.get
      let doc = JsonDocument.Parse(data)
      let root = doc.RootElement
      root.GetProperty("Total").GetInt32() |> Expect.equal "Total" 47
      root.GetProperty("Passed").GetInt32() |> Expect.equal "Passed" 40
      root.GetProperty("Failed").GetInt32() |> Expect.equal "Failed" 3
      root.GetProperty("Stale").GetInt32() |> Expect.equal "Stale" 2
      root.GetProperty("Disabled").GetInt32() |> Expect.equal "Disabled" 1

    testCase "no camelCase properties in output" <| fun () ->
      let summary: SageFs.Features.LiveTesting.TestSummary = {
        Total = 10; Passed = 8; Failed = 1; Stale = 1; Running = 0; Disabled = 0; Enabled = true
      }
      let sse = formatTestSummaryEvent productionSseOpts None summary
      let data = extractSseData sse |> Option.get
      data.Contains("\"total\"") |> Expect.isFalse "no camelCase total"
      data.Contains("\"passed\"") |> Expect.isFalse "no camelCase passed"
  ]

  testList "ResultFreshness DU shape" [
    testCase "Fresh serializes as Case/Fields" <| fun () ->
      let json = JsonSerializer.Serialize(SageFs.Features.LiveTesting.ResultFreshness.Fresh, productionSseOpts)
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("Case").GetString() |> Expect.equal "case" "Fresh"

    testCase "StaleCodeEdited serializes as Case/Fields" <| fun () ->
      let json = JsonSerializer.Serialize(SageFs.Features.LiveTesting.ResultFreshness.StaleCodeEdited, productionSseOpts)
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("Case").GetString() |> Expect.equal "case" "StaleCodeEdited"

    testCase "StaleWrongGeneration serializes as Case/Fields" <| fun () ->
      let json = JsonSerializer.Serialize(SageFs.Features.LiveTesting.ResultFreshness.StaleWrongGeneration, productionSseOpts)
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("Case").GetString() |> Expect.equal "case" "StaleWrongGeneration"
  ]

  testList "TestRunStatus DU shape" [
    testCase "Stale has Case:Stale" <| fun () ->
      let json = JsonSerializer.Serialize(SageFs.Features.LiveTesting.TestRunStatus.Stale, productionSseOpts)
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("Case").GetString() |> Expect.equal "case" "Stale"

    testCase "PolicyDisabled has Case:PolicyDisabled" <| fun () ->
      let json = JsonSerializer.Serialize(SageFs.Features.LiveTesting.TestRunStatus.PolicyDisabled, productionSseOpts)
      let doc = JsonDocument.Parse(json)
      doc.RootElement.GetProperty("Case").GetString() |> Expect.equal "case" "PolicyDisabled"

    testCase "Passed carries duration in Fields" <| fun () ->
      let status = SageFs.Features.LiveTesting.TestRunStatus.Passed (System.TimeSpan.FromMilliseconds(42.5))
      let json = JsonSerializer.Serialize(status, productionSseOpts)
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root.GetProperty("Case").GetString() |> Expect.equal "case" "Passed"
      root.GetProperty("Fields").GetArrayLength() |> Expect.equal "1 field" 1
      root.GetProperty("Fields").[0].ValueKind |> Expect.equal "field is string" JsonValueKind.String

    testCase "Failed carries failure+duration in Fields" <| fun () ->
      let failure = SageFs.Features.LiveTesting.TestFailure.AssertionFailed "oops"
      let dur = System.TimeSpan.FromMilliseconds(100.0)
      let status = SageFs.Features.LiveTesting.TestRunStatus.Failed(failure, dur)
      let json = JsonSerializer.Serialize(status, productionSseOpts)
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root.GetProperty("Case").GetString() |> Expect.equal "case" "Failed"
      root.GetProperty("Fields").GetArrayLength() |> Expect.equal "2 fields" 2

    testCase "Skipped carries reason in Fields" <| fun () ->
      let json = JsonSerializer.Serialize(SageFs.Features.LiveTesting.TestRunStatus.Skipped "not applicable", productionSseOpts)
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root.GetProperty("Case").GetString() |> Expect.equal "case" "Skipped"
      root.GetProperty("Fields").[0].GetString() |> Expect.equal "reason" "not applicable"
  ]

  testList "TestResultsBatchPayload shape" [
    testCase "has Freshness, Entries, Summary, Generation, Completion" <| fun () ->
      let payload: SageFs.Features.LiveTesting.TestResultsBatchPayload = {
        Generation = SageFs.Features.LiveTesting.RunGeneration 1
        Freshness = SageFs.Features.LiveTesting.ResultFreshness.StaleCodeEdited
        Completion = SageFs.Features.LiveTesting.BatchCompletion.Superseded
        Entries = [||]
        Summary = { Total = 0; Passed = 0; Failed = 0; Stale = 0; Running = 0; Disabled = 0; Enabled = true }
      }
      let json = JsonSerializer.Serialize(payload, productionSseOpts)
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      let mutable v = Unchecked.defaultof<JsonElement>
      root.TryGetProperty("Freshness", &v) |> Expect.isTrue "has Freshness"
      root.TryGetProperty("Entries", &v) |> Expect.isTrue "has Entries"
      root.TryGetProperty("Summary", &v) |> Expect.isTrue "has Summary"
      root.TryGetProperty("Generation", &v) |> Expect.isTrue "has Generation"
      root.TryGetProperty("Completion", &v) |> Expect.isTrue "has Completion"

    testCase "Freshness is nested DU object" <| fun () ->
      let payload: SageFs.Features.LiveTesting.TestResultsBatchPayload = {
        Generation = SageFs.Features.LiveTesting.RunGeneration 1
        Freshness = SageFs.Features.LiveTesting.ResultFreshness.StaleCodeEdited
        Completion = SageFs.Features.LiveTesting.BatchCompletion.Complete(5, 5)
        Entries = [||]
        Summary = { Total = 5; Passed = 5; Failed = 0; Stale = 0; Running = 0; Disabled = 0; Enabled = true }
      }
      let json = JsonSerializer.Serialize(payload, productionSseOpts)
      let doc = JsonDocument.Parse(json)
      let freshEl = doc.RootElement.GetProperty("Freshness")
      freshEl.ValueKind |> Expect.equal "is object" JsonValueKind.Object
      freshEl.GetProperty("Case").GetString() |> Expect.equal "case" "StaleCodeEdited"
  ]
]

/// Helper to serialize and parse in one call
let private serAndParse<'T> (value: 'T) =
  let json = JsonSerializer.Serialize<'T>(value, productionSseOpts)
  JsonDocument.Parse(json).RootElement

[<Tests>]
let protocolSnapshotTests = testList "Protocol Snapshots" [

  testList "RunPolicy wire format" [
    testCase "OnEveryChange" <| fun () ->
      (serAndParse RunPolicy.OnEveryChange).GetProperty("Case").GetString()
      |> Expect.equal "case" "OnEveryChange"
    testCase "OnSaveOnly" <| fun () ->
      (serAndParse RunPolicy.OnSaveOnly).GetProperty("Case").GetString()
      |> Expect.equal "case" "OnSaveOnly"
    testCase "OnDemand" <| fun () ->
      (serAndParse RunPolicy.OnDemand).GetProperty("Case").GetString()
      |> Expect.equal "case" "OnDemand"
    testCase "Disabled" <| fun () ->
      (serAndParse RunPolicy.Disabled).GetProperty("Case").GetString()
      |> Expect.equal "case" "Disabled"
  ]

  testList "TestCategory wire format" [
    testCase "Unit" <| fun () ->
      (serAndParse TestCategory.Unit).GetProperty("Case").GetString()
      |> Expect.equal "case" "Unit"
    testCase "Integration" <| fun () ->
      (serAndParse TestCategory.Integration).GetProperty("Case").GetString()
      |> Expect.equal "case" "Integration"
    testCase "Custom with value" <| fun () ->
      let root = serAndParse (TestCategory.Custom "smoke")
      root.GetProperty("Case").GetString() |> Expect.equal "case" "Custom"
      root.GetProperty("Fields").[0].GetString() |> Expect.equal "value" "smoke"
  ]

  testList "CoverageHealth wire format" [
    testCase "AllPassing" <| fun () ->
      (serAndParse CoverageHealth.AllPassing).GetProperty("Case").GetString()
      |> Expect.equal "case" "AllPassing"
    testCase "SomeFailing" <| fun () ->
      (serAndParse CoverageHealth.SomeFailing).GetProperty("Case").GetString()
      |> Expect.equal "case" "SomeFailing"
  ]

  testList "CoverageStatus wire format" [
    testCase "Covered has testCount and health" <| fun () ->
      let root = serAndParse (CoverageStatus.Covered(3, CoverageHealth.AllPassing))
      root.GetProperty("Case").GetString() |> Expect.equal "case" "Covered"
      root.GetProperty("Fields").GetArrayLength() |> Expect.equal "2 fields" 2
      root.GetProperty("Fields").[0].GetInt32() |> Expect.equal "testCount" 3
    testCase "NotCovered" <| fun () ->
      (serAndParse CoverageStatus.NotCovered).GetProperty("Case").GetString()
      |> Expect.equal "case" "NotCovered"
    testCase "Pending" <| fun () ->
      (serAndParse CoverageStatus.Pending).GetProperty("Case").GetString()
      |> Expect.equal "case" "Pending"
  ]

  testList "LineCoverage wire format" [
    testCase "FullyCovered" <| fun () ->
      (serAndParse LineCoverage.FullyCovered).GetProperty("Case").GetString()
      |> Expect.equal "case" "FullyCovered"
    testCase "PartiallyCovered" <| fun () ->
      let root = serAndParse (LineCoverage.PartiallyCovered(2, 5))
      root.GetProperty("Case").GetString() |> Expect.equal "case" "PartiallyCovered"
      root.GetProperty("Fields").[0].GetInt32() |> Expect.equal "covered" 2
    testCase "NotCovered" <| fun () ->
      (serAndParse LineCoverage.NotCovered).GetProperty("Case").GetString()
      |> Expect.equal "case" "NotCovered"
  ]

  testList "FileAnnotations wire format" [
    testCase "empty has all PascalCase fields" <| fun () ->
      let fa : FileAnnotations = {
        FilePath = "src/Foo.fs"; TestAnnotations = [||]
        CoverageAnnotations = [||]; InlineFailures = [||]; CodeLenses = [||]
      }
      let root = serAndParse fa
      let mutable v = Unchecked.defaultof<JsonElement>
      root.TryGetProperty("FilePath", &v) |> Expect.isTrue "has FilePath"
      root.TryGetProperty("CoverageAnnotations", &v) |> Expect.isTrue "has CoverageAnnotations"
      root.TryGetProperty("CodeLenses", &v) |> Expect.isTrue "has CodeLenses"

    testCase "CoverageLineAnnotation shape" <| fun () ->
      let cla : CoverageLineAnnotation = {
        Line = 42; Detail = CoverageStatus.Covered(2, CoverageHealth.AllPassing)
        CoveringTestIds = [| TestId.TestId "t1"; TestId.TestId "t2" |]
        BranchCoverage = None
      }
      let root = serAndParse cla
      root.GetProperty("Line").GetInt32() |> Expect.equal "line" 42
      root.GetProperty("Detail").GetProperty("Case").GetString() |> Expect.equal "detail" "Covered"
      root.GetProperty("CoveringTestIds").GetArrayLength() |> Expect.equal "ids" 2

    testCase "TestLineAnnotation shape" <| fun () ->
      let tla : TestLineAnnotation = {
        TestId = TestId.TestId "my-test"; DisplayName = "MyModule.should_work"
        Line = 10; Status = TestRunStatus.Passed(System.TimeSpan.FromMilliseconds(50.0))
        Freshness = AnnotationFreshness.Current
      }
      let root = serAndParse tla
      root.GetProperty("Line").GetInt32() |> Expect.equal "line" 10
      root.GetProperty("Status").GetProperty("Case").GetString() |> Expect.equal "status" "Passed"

    testCase "TestCodeLens shape" <| fun () ->
      let cl : TestCodeLens = {
        TestId = TestId.TestId "my-test"; Label = "✓ Passed"
        Line = 5; Command = CodeLensCommand.RunTest
      }
      let root = serAndParse cl
      root.GetProperty("Label").GetString() |> Expect.stringContains "label" "Passed"
      root.GetProperty("Command").GetProperty("Case").GetString() |> Expect.equal "cmd" "RunTest"
  ]

  testList "BatchCompletion wire format" [
    testCase "Complete" <| fun () ->
      (serAndParse (BatchCompletion.Complete(10, 10))).GetProperty("Case").GetString()
      |> Expect.equal "case" "Complete"
    testCase "Partial" <| fun () ->
      (serAndParse (BatchCompletion.Partial(5, 10))).GetProperty("Case").GetString()
      |> Expect.equal "case" "Partial"
    testCase "Superseded" <| fun () ->
      (serAndParse BatchCompletion.Superseded).GetProperty("Case").GetString()
      |> Expect.equal "case" "Superseded"
  ]

  testList "PipelineTiming wire format" [
    testCase "has all expected fields" <| fun () ->
      let ts = System.TimeSpan.FromMilliseconds(10.0)
      let pt : PipelineTiming = {
        Depth = PipelineDepth.ThroughExecution(ts, ts, ts)
        TotalTests = 100; AffectedTests = 12
        Trigger = RunTrigger.Keystroke
        Timestamp = System.DateTimeOffset(2026, 2, 27, 0, 0, 0, System.TimeSpan.Zero)
      }
      let root = serAndParse pt
      let mutable v = Unchecked.defaultof<JsonElement>
      root.TryGetProperty("TotalTests", &v) |> Expect.isTrue "has TotalTests"
      root.GetProperty("TotalTests").GetInt32() |> Expect.equal "total" 100
  ]

  testList "AnnotationFreshness wire format" [
    testCase "Current" <| fun () ->
      (serAndParse AnnotationFreshness.Current).GetProperty("Case").GetString()
      |> Expect.equal "case" "Current"
    testCase "Stale" <| fun () ->
      (serAndParse AnnotationFreshness.Stale).GetProperty("Case").GetString()
      |> Expect.equal "case" "Stale"
    testCase "Running" <| fun () ->
      (serAndParse AnnotationFreshness.Running).GetProperty("Case").GetString()
      |> Expect.equal "case" "Running"
  ]

  testList "CodeLensCommand wire format" [
    testCase "RunTest" <| fun () ->
      (serAndParse CodeLensCommand.RunTest).GetProperty("Case").GetString()
      |> Expect.equal "case" "RunTest"
    testCase "DebugTest" <| fun () ->
      (serAndParse CodeLensCommand.DebugTest).GetProperty("Case").GetString()
      |> Expect.equal "case" "DebugTest"
    testCase "ShowHistory" <| fun () ->
      (serAndParse CodeLensCommand.ShowHistory).GetProperty("Case").GetString()
      |> Expect.equal "case" "ShowHistory"
  ]
]

[<Tests>]
let sessionScopingTests = testList "SSE Session Scoping" [
  testList "injectSessionId" [
    testCase "None returns json unchanged" <| fun () ->
      let json = """{"Total":5}"""
      injectSessionId None json
      |> Expect.equal "unchanged" """{"Total":5}"""

    testCase "Some prepends SessionId field" <| fun () ->
      let json = """{"Total":5}"""
      let result = injectSessionId (Some "sess-123") json
      result |> Expect.stringContains "has SessionId" "\"SessionId\":\"sess-123\""

    testCase "injected json is valid JSON" <| fun () ->
      let json = """{"Total":5,"Passed":3}"""
      let result = injectSessionId (Some "abc") json
      let doc = JsonDocument.Parse(result)
      doc.RootElement.GetProperty("SessionId").GetString()
      |> Expect.equal "sessionId" "abc"
      doc.RootElement.GetProperty("Total").GetInt32()
      |> Expect.equal "total preserved" 5

    testCase "non-object json returned unchanged" <| fun () ->
      injectSessionId (Some "abc") "[1,2,3]"
      |> Expect.equal "unchanged" "[1,2,3]"
  ]

  testList "format functions with sessionId" [
    testCase "formatTestSummaryEvent None has no SessionId" <| fun () ->
      let summary: TestSummary = {
        Total = 10; Passed = 8; Failed = 1; Stale = 1; Running = 0; Disabled = 0; Enabled = true
      }
      let result = formatTestSummaryEvent productionSseOpts None summary
      result.Contains("SessionId") |> Expect.isFalse "no SessionId"

    testCase "formatTestSummaryEvent Some injects SessionId" <| fun () ->
      let summary: TestSummary = {
        Total = 10; Passed = 8; Failed = 1; Stale = 1; Running = 0; Disabled = 0; Enabled = true
      }
      let result = formatTestSummaryEvent productionSseOpts (Some "sess-456") summary
      let data = extractSseData result |> Option.get
      let doc = JsonDocument.Parse(data)
      doc.RootElement.GetProperty("SessionId").GetString()
      |> Expect.equal "sessionId" "sess-456"
      doc.RootElement.GetProperty("Total").GetInt32()
      |> Expect.equal "total preserved" 10

    testCase "formatTestResultsBatchEvent Some injects SessionId" <| fun () ->
      let payload: TestResultsBatchPayload = {
        Generation = RunGeneration 1
        Freshness = ResultFreshness.Fresh
        Completion = BatchCompletion.Complete(5, 5)
        Entries = [||]
        Summary = { Total = 5; Passed = 5; Failed = 0; Stale = 0; Running = 0; Disabled = 0; Enabled = true }
      }
      let result = formatTestResultsBatchEvent productionSseOpts (Some "sess-789") payload
      let data = extractSseData result |> Option.get
      let doc = JsonDocument.Parse(data)
      doc.RootElement.GetProperty("SessionId").GetString()
      |> Expect.equal "sessionId" "sess-789"

    testCase "formatFileAnnotationsEvent Some injects SessionId" <| fun () ->
      let fa : FileAnnotations = {
        FilePath = "src/Foo.fs"; TestAnnotations = [||]
        CoverageAnnotations = [||]; InlineFailures = [||]; CodeLenses = [||]
      }
      let result = formatFileAnnotationsEvent productionSseOpts (Some "sess-abc") fa
      let data = extractSseData result |> Option.get
      let doc = JsonDocument.Parse(data)
      doc.RootElement.GetProperty("SessionId").GetString()
      |> Expect.equal "sessionId" "sess-abc"
      doc.RootElement.GetProperty("FilePath").GetString()
      |> Expect.equal "path preserved" "src/Foo.fs"
  ]
]

let private mkSp file line col bid : SequencePoint =
  { File = file; Line = line; Column = col; BranchId = bid }

[<Tests>]
let branchCoverageTests = testList "Branch Coverage Wiring" [
  testList "CoverageBitmap.union" [
    testCase "union of disjoint bitmaps" <| fun () ->
      let a = CoverageBitmap.ofBoolArray [| true; false; false; false |]
      let b = CoverageBitmap.ofBoolArray [| false; true; false; false |]
      CoverageBitmap.union a b |> CoverageBitmap.toBoolArray
      |> Expect.equal "combined" [| true; true; false; false |]

    testCase "union of empty" <| fun () ->
      CoverageBitmap.union CoverageBitmap.empty CoverageBitmap.empty
      |> fun r -> r.Count |> Expect.equal "count" 0
  ]

  testList "computeLineCoverageForFile" [
    testCase "partial coverage returns PartiallyCovered" <| fun () ->
      let map : InstrumentationMap = {
        Slots = [| mkSp "f.fs" 10 0 0; mkSp "f.fs" 10 10 1; mkSp "f.fs" 10 20 2 |]
        TotalProbes = 3; TrackerTypeName = "t"; HitsFieldName = "h"
      }
      let bm = CoverageBitmap.ofBoolArray [| true; false; true |]
      let bms = Map.ofList [ TestId.TestId "t1", bm ]
      CoverageBitmap.computeLineCoverageForFile "f.fs" [| map |] bms
      |> Map.tryFind 10
      |> Expect.equal "partial" (Some (LineCoverage.PartiallyCovered(2, 3)))

    testCase "all probes hit returns FullyCovered" <| fun () ->
      let map : InstrumentationMap = {
        Slots = [| mkSp "f.fs" 10 0 0; mkSp "f.fs" 10 10 1 |]
        TotalProbes = 2; TrackerTypeName = "t"; HitsFieldName = "h"
      }
      let bm = CoverageBitmap.ofBoolArray [| true; true |]
      let bms = Map.ofList [ TestId.TestId "t1", bm ]
      CoverageBitmap.computeLineCoverageForFile "f.fs" [| map |] bms
      |> Map.tryFind 10
      |> Expect.equal "full" (Some LineCoverage.FullyCovered)

    testCase "no probes hit returns NotCovered" <| fun () ->
      let map : InstrumentationMap = {
        Slots = [| mkSp "f.fs" 10 0 0; mkSp "f.fs" 10 10 1 |]
        TotalProbes = 2; TrackerTypeName = "t"; HitsFieldName = "h"
      }
      let bm = CoverageBitmap.ofBoolArray [| false; false |]
      let bms = Map.ofList [ TestId.TestId "t1", bm ]
      CoverageBitmap.computeLineCoverageForFile "f.fs" [| map |] bms
      |> Map.tryFind 10
      |> Expect.equal "not covered" (Some LineCoverage.NotCovered)

    testCase "multiple tests OR coverage together" <| fun () ->
      let map : InstrumentationMap = {
        Slots = [| mkSp "f.fs" 10 0 0; mkSp "f.fs" 10 10 1; mkSp "f.fs" 10 20 2 |]
        TotalProbes = 3; TrackerTypeName = "t"; HitsFieldName = "h"
      }
      let bm1 = CoverageBitmap.ofBoolArray [| true; false; false |]
      let bm2 = CoverageBitmap.ofBoolArray [| false; false; true |]
      let bms = Map.ofList [ TestId.TestId "t1", bm1; TestId.TestId "t2", bm2 ]
      CoverageBitmap.computeLineCoverageForFile "f.fs" [| map |] bms
      |> Map.tryFind 10
      |> Expect.equal "OR'd partial" (Some (LineCoverage.PartiallyCovered(2, 3)))

    testCase "empty maps returns empty" <| fun () ->
      CoverageBitmap.computeLineCoverageForFile "f.fs" [||] Map.empty
      |> Expect.isEmpty "empty"

    testCase "no matching bitmaps returns empty" <| fun () ->
      let map : InstrumentationMap = {
        Slots = [| mkSp "f.fs" 10 0 0 |]
        TotalProbes = 1; TrackerTypeName = "t"; HitsFieldName = "h"
      }
      CoverageBitmap.computeLineCoverageForFile "f.fs" [| map |] Map.empty
      |> Expect.isEmpty "empty"

    testCase "different file returns empty" <| fun () ->
      let map : InstrumentationMap = {
        Slots = [| mkSp "other.fs" 10 0 0 |]
        TotalProbes = 1; TrackerTypeName = "t"; HitsFieldName = "h"
      }
      let bm = CoverageBitmap.ofBoolArray [| true |]
      let bms = Map.ofList [ TestId.TestId "t1", bm ]
      CoverageBitmap.computeLineCoverageForFile "f.fs" [| map |] bms
      |> Expect.isEmpty "empty"
  ]
]
