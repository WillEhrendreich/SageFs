module SageFs.VisualStudio.Core.Tests.LiveTestingParserTests

open Xunit
open FsUnit.Xunit
open SageFs.VisualStudio.Core

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
  let json =
    """{"Total":5,"Passed":3,"Failed":1,"Running":0,"Stale":1,"Disabled":0}"""
  match LiveTestingParser.parseSseEvent "test_summary" json with
  | [ LiveTestEvent.SummaryUpdated s ] ->
    s.Total |> should equal 5
    s.Passed |> should equal 3
    s.Failed |> should equal 1
    s.Stale |> should equal 1
    s.Disabled |> should equal 0
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

// -- parseSseEvent: test_results_batch -------------------------------------

let private passedBatchJson =
  """{"Entries":[{"TestId":{"Fields":["test-id-1"]},"DisplayName":"my test","FullName":"Module.myTest","Origin":{"Case":"SourceMapped","Fields":["path/to/Test.fs",42]},"Status":{"Case":"Passed","Fields":["00:00:00.045"]}}],"Freshness":"Fresh"}"""

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
  | [ _; LiveTestEvent.TestResultBatch([| r |], _) ] ->
    match r.Outcome with
    | TestOutcome.Passed ms -> ms |> should equal 45.0
    | other -> failwith (sprintf "expected Passed but got %A" other)
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch passed result has correct test ID`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" passedBatchJson with
  | [ LiveTestEvent.TestsDiscovered([| info |]); _ ] ->
    TestId.value info.Id |> should equal "test-id-1"
  | other -> failwith (sprintf "unexpected result %A" other)

[<Fact>]
let ``parseSseEvent test_results_batch passed result has source file and line from Origin`` () =
  match LiveTestingParser.parseSseEvent "test_results_batch" passedBatchJson with
  | [ LiveTestEvent.TestsDiscovered([| info |]); _ ] ->
    info.FilePath |> should equal (Some "path/to/Test.fs")
    info.Line |> should equal (Some 42)
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
