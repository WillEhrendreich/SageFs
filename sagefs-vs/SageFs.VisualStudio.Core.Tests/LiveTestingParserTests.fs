module SageFs.VisualStudio.Core.Tests.LiveTestingParserTests

open System.IO
open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

let private fixturePath name =
  Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "SageFs.Tests", "Fixtures", "LiveTesting", name)

let private readFixture name = File.ReadAllText(fixturePath name)

// -- parseDurationToMs -----------------------------------------------------

[<Theory>]
[<InlineData("00:00:01.000", 1000.0)>]
[<InlineData("00:01:30.500", 90500.0)>]
[<InlineData("00:00:00.045", 45.0)>]
[<InlineData("01:00:00.000", 3600000.0)>]
let ``parseDurationToMs returns correct milliseconds for valid timespan strings``
  (input: string, expected: float) =
  match LiveTestingParser.parseDurationToMs input with
  | Some ms -> ms |> should equal expected
  | None -> failwith (sprintf "expected Some %f but got None" expected)

[<Fact>]
let ``parseDurationToMs returns None for invalid string`` () =
  LiveTestingParser.parseDurationToMs "invalid" |> should equal None

[<Fact>]
let ``parseDurationToMs returns None for empty string`` () =
  LiveTestingParser.parseDurationToMs "" |> should equal None

// -- parseSseEvent: test_summary -------------------------------------------

[<Fact>]
let ``parseSseEvent test_summary with valid JSON returns SummaryUpdated with correct fields`` () =
  let json = readFixture "summary-with-fallback-decision.json"
  match LiveTestingParser.parseSseEvent "test_summary" json with
  | [ LiveTestEvent.SummaryUpdated s ] ->
    s.Total |> should equal 5
    s.Passed |> should equal 3
    s.Failed |> should equal 1
    s.Stale |> should equal 1
    s.Disabled |> should equal 0
    s.LastDecision.IsSome |> should equal true
  | other -> failwith (sprintf "expected [SummaryUpdated] but got %A" other)

[<Fact>]
let ``parseSseEvent test_summary with suppressed decision fixture preserves policy deferral semantics`` () =
  let json = readFixture "summary-with-suppressed-decision.json"
  match LiveTestingParser.parseSseEvent "test_summary" json with
  | [ LiveTestEvent.SummaryUpdated s ] ->
    s.Total |> should equal 4
    s.Stale |> should equal 2
    match s.LastDecision with
    | Some decision ->
      decision.Precision |> should equal SelectionPrecision.SuppressedByPolicy
      decision.Trust |> should equal FreshnessTrust.Suppressed
      decision.DeferredTests.Length |> should equal 1
    | None -> failwith "expected suppressed decision"
  | other -> failwith (sprintf "expected [SummaryUpdated] but got %A" other)

[<Fact>]
let ``parseSseEvent test_summary with empty JSON returns SummaryUpdated with all zeros`` () =
  let json = """{}"""
  match LiveTestingParser.parseSseEvent "test_summary" json with
  | [ LiveTestEvent.SummaryUpdated s ] ->
    s.Total |> should equal 0
    s.Passed |> should equal 0
    s.Failed |> should equal 0
  | other -> failwith (sprintf "expected [SummaryUpdated] but got %A" other)

[<Fact>]
let ``parseSseEvent test_summary with invalid JSON returns empty list`` () =
  LiveTestingParser.parseSseEvent "test_summary" "not json" |> should be Empty

[<Fact>]
let ``parseSseEvent test_summary with discovery fields reads DiscoveryState and DiscoveryGeneration`` () =
  let json = """{ "Total": 2, "DiscoveryState": "ready_with_tests", "DiscoveryGeneration": 4 }"""
  match LiveTestingParser.parseSseEvent "test_summary" json with
  | [ LiveTestEvent.SummaryUpdated s ] ->
    s.DiscoveryState |> should equal "ready_with_tests"
    s.DiscoveryGeneration |> should equal 4L
  | other -> failwith (sprintf "expected [SummaryUpdated] but got %A" other)

[<Fact>]
let ``parseSseEvent test_summary missing discovery fields defaults to disabled and zero`` () =
  let json = """{ "Total": 0 }"""
  match LiveTestingParser.parseSseEvent "test_summary" json with
  | [ LiveTestEvent.SummaryUpdated s ] ->
    s.DiscoveryState |> should equal "disabled"
    s.DiscoveryGeneration |> should equal 0L
  | other -> failwith (sprintf "expected [SummaryUpdated] but got %A" other)

// -- parseSseEvent: test_results_batch -------------------------------------

let private passedBatchJson =
  readFixture "results-batch-with-coverage-decision.json"

let private fixtureBatchJson =
  readFixture "results-batch-with-coverage-decision.json"

let private failedBatchJson =
  """{"Entries":[{"TestId":{"Fields":["test-id-2"]},"DisplayName":"failing test","FullName":"Module.failingTest","Origin":{"Case":"NoOrigin"},"Status":{"Case":"Failed","Fields":[{"Case":"AssertionFailed","Fields":["expected 1 but got 2"]},"00:00:00.012"]}}],"Freshness":"Fresh"}"""

let private skippedBatchJson =
  """{"Entries":[{"TestId":{"Fields":["test-id-3"]},"DisplayName":"skipped test","FullName":"Module.skippedTest","Origin":{"Case":"NoOrigin"},"Status":{"Case":"Skipped","Fields":["not applicable"]}}],"Freshness":"Fresh"}"""

let private staleBatchJson =
  """{"Entries":[{"TestId":{"Fields":["test-id-4"]},"DisplayName":"stale test","FullName":"Module.staleTest","Origin":{"Case":"NoOrigin"},"Status":{"Case":"Stale"}}],"Freshness":"Fresh"}"""

let private staleCodeEditedBatchJson =
  """{"Entries":[{"TestId":{"Fields":["test-id-5"]},"DisplayName":"stale test","FullName":"Module.staleTest","Origin":{"Case":"NoOrigin"},"Status":{"Case":"Passed","Fields":["00:00:00.001"]}}],"Freshness":{"Case":"StaleCodeEdited"}}"""

let private staleWrongGenBatchJson =
  """{"Entries":[{"TestId":{"Fields":["test-id-6"]},"DisplayName":"stale test","FullName":"Module.staleTest","Origin":{"Case":"NoOrigin"},"Status":{"Case":"Passed","Fields":["00:00:00.001"]}}],"Freshness":{"Case":"StaleWrongGeneration"}}"""

[<Fact>]
let ``parseSseEvent test_results_batch valid JSON returns TestsDiscovered and TestResultBatch`` () =
  let result = LiveTestingParser.parseSseEvent "test_results_batch" passedBatchJson
  result.Length |> should equal 2
  result
  |> List.exists (function LiveTestEvent.TestsDiscovered _ -> true | _ -> false)
  |> should equal true
  result
  |> List.exists (function LiveTestEvent.TestResultBatch _ -> true | _ -> false)
  |> should equal true

[<Fact>]
let ``parseSseEvent test_results_batch passed result has Passed outcome with duration`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" passedBatchJson with
  | [ _; LiveTestEvent.TestResultBatch(results, _) ] ->
    match results.[0].Outcome with
    | TestOutcome.Passed ms -> ms |> should equal 45.0
    | other -> failwith (sprintf "expected Passed but got %A" other)
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch passed result has correct test ID`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" passedBatchJson with
  | [ LiveTestEvent.TestsDiscovered (infos, _, _); _ ] ->
    TestId.value infos.[0].Id |> should equal "test-id-1"
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch passed result has source file and line from Origin`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" fixtureBatchJson with
  | [ LiveTestEvent.TestsDiscovered (infos, _, _); _ ] ->
    infos.[0].FilePath |> should equal (Some "src/Tests.fs")
    infos.[0].Line |> should equal (Some 10)
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch fixture retains latest decision for downstream UI explanations`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" fixtureBatchJson with
  | [ LiveTestEvent.TestsDiscovered _; LiveTestEvent.TestResultBatch(_, freshness) ] ->
    freshness |> should equal ResultFreshness.Fresh
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch failed result has Failed outcome with message`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" failedBatchJson with
  | [ _; LiveTestEvent.TestResultBatch([| r |], _) ] ->
    match r.Outcome with
    | TestOutcome.Failed(msg, _) -> msg |> should equal "expected 1 but got 2"
    | other -> failwith (sprintf "expected Failed but got %A" other)
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch skipped result has Skipped outcome`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" skippedBatchJson with
  | [ _; LiveTestEvent.TestResultBatch([| r |], _) ] ->
    match r.Outcome with
    | TestOutcome.Skipped _ -> ()
    | other -> failwith (sprintf "expected Skipped but got %A" other)
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch stale result has Stale outcome`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" staleBatchJson with
  | [ _; LiveTestEvent.TestResultBatch([| r |], _) ] ->
    r.Outcome |> should equal TestOutcome.Stale
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch with StaleCodeEdited freshness parses correctly`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" staleCodeEditedBatchJson with
  | [ _; LiveTestEvent.TestResultBatch(_, freshness) ] ->
    freshness |> should equal ResultFreshness.StaleCodeEdited
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEventWithGeneration threads discovery generation into TestsDiscovered`` () =
  match LiveTestingParser.parseSseEventWithGeneration 7L "test_results_batch" passedBatchJson with
  | [ LiveTestEvent.TestsDiscovered (infos, isComplete, gen); _ ] ->
    infos.Length |> should be (greaterThan 0)
    gen |> should equal 7L
    // the fixture is a real server payload with Completion=Complete —
    // a complete batch is the authoritative discovery set
    isComplete |> should equal true
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch defaults generation to zero`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" passedBatchJson with
  | [ LiveTestEvent.TestsDiscovered (_, _, gen); _ ] ->
    gen |> should equal 0L
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch with StaleWrongGeneration freshness parses correctly`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" staleWrongGenBatchJson with
  | [ _; LiveTestEvent.TestResultBatch(_, freshness) ] ->
    freshness |> should equal ResultFreshness.StaleWrongGeneration
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch with missing Entries returns empty list`` () =
  LiveTestingParser.parseSseEvent "test_results_batch" """{}""" |> should be Empty

[<Fact>]
let ``parseSseEvent test_results_batch with invalid JSON returns empty list`` () =
  LiveTestingParser.parseSseEvent "test_results_batch" "not json" |> should be Empty

// -- parseSseEvent: unknown event type -------------------------------------

[<Fact>]
let ``parseSseEvent with unknown event type returns empty list`` () =
  let json = """{"Total":5,"Passed":5}"""
  LiveTestingParser.parseSseEvent "unknown_event_xyz" json |> should be Empty

[<Fact>]
let ``parseSseEvent with empty event type returns empty list`` () =
  LiveTestingParser.parseSseEvent "" """{}""" |> should be Empty

// -- freshness string form -------------------------------------------------

[<Fact>]
let ``parseSseEvent test_results_batch with string Fresh freshness returns Fresh`` () =
  let json =
    """{"Entries":[{"TestId":{"Fields":["t1"]},"DisplayName":"t","FullName":"t","Origin":{"Case":"NoOrigin"},"Status":{"Case":"Stale"}}],"Freshness":"Fresh"}"""
  match LiveTestingParser.parseSseEvent "test_results_batch" json with
  | [ _; LiveTestEvent.TestResultBatch(_, freshness) ] ->
    freshness |> should equal ResultFreshness.Fresh
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch with string StaleCodeEdited freshness returns StaleCodeEdited`` () =
  let json =
    """{"Entries":[{"TestId":{"Fields":["t1"]},"DisplayName":"t","FullName":"t","Origin":{"Case":"NoOrigin"},"Status":{"Case":"Stale"}}],"Freshness":"StaleCodeEdited"}"""
  match LiveTestingParser.parseSseEvent "test_results_batch" json with
  | [ _; LiveTestEvent.TestResultBatch(_, freshness) ] ->
    freshness |> should equal ResultFreshness.StaleCodeEdited
  | other -> failwith (sprintf "unexpected result %A" other)
