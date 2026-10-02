module SageFs.Tests.McpAnalysisTests

// WHY — the session-analysis tools used to answer from the daemon-global eval store and the
// primary live-testing cycle. An MCP eval never reached the first, a `run_tests` in a session that
// is not the primary never reached the second, so the tools said "0 downstream", "No bindings in
// scope" and "No issues detected" about sessions that had bindings and failing tests. These tests
// pin what each tool says given what ONE session has recorded: a measurement, or a reason.
open System.Text.Json
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features
open SageFs.Features.LiveTesting
open SageFs.Features.ToolAnswers
open SageFs.McpAnalysis
open SageFs.Tests.LiveTestingTestHelpers

// ── What a session recorded ───────────────────────────────────

/// Two evals as `send_fsharp_code` records them: with the MCP result prefix.
let private twoMcpEvals =
  FeatureHooks.FeaturePushState.empty
  |> FeatureHooks.recordEval "let a6x = 1" "Result: val a6x: int = 1" 4L
  |> FeatureHooks.recordEval "let a6y = a6x + 1" "Result: val a6y: int = 2" 4L

let private noEvals = FeatureHooks.FeaturePushState.empty

/// One eval that bound no name: it only printed.
let private evalsThatBindNothing =
  FeatureHooks.FeaturePushState.empty
  |> FeatureHooks.recordEval "printfn \"hi\"" "Result: hi" 3L

let private expecto = TestFramework.Expecto

let private failingTest = mkTestCase "Math.Tests.adds" expecto TestCategory.Unit
let private passingTest = mkTestCase "Math.Tests.subtracts" expecto TestCategory.Unit

let private failed = TestResult.Failed (TestFailure.AssertionFailed "boom", ts 3.0)

let private cycleWith (results: (TestCase * TestResult) list) (activation: LiveTestingActivation) : LiveTestCycleState =
  { LiveTestCycleState.empty with
      TestState =
        { LiveTestState.empty with
            Activation = activation
            DiscoveredTests = results |> List.map fst |> Array.ofList
            LastResults = results |> List.map (fun (tc, r) -> (tc.Id, mkResult tc.Id r)) |> Map.ofList } }

let private failingCycle = cycleWith [ (failingTest, failed) ] LiveTestingActivation.Inactive

let private sideOf (cycle: LiveTestCycleState) (workflow: WorkflowKind) : TestSide =
  { Cycle = cycle; Maps = [||]; Workflow = workflow }

// ── Reading an answer ─────────────────────────────────────────

let private measuredText (answer: ToolAnswer<Measurement>) : string =
  match answer with
  | ToolAnswer.Measured _ -> textOf answer
  | ToolAnswer.NotAvailable reason -> failtestf "expected a measurement, got NotAvailable %s" (NotAvailableReason.token reason)

let private measuredJson (answer: ToolAnswer<Measurement>) : JsonElement =
  JsonDocument.Parse(measuredText answer).RootElement.Clone()

let private reasonOf (answer: ToolAnswer<Measurement>) : NotAvailableReason =
  match answer with
  | ToolAnswer.NotAvailable reason -> reason
  | ToolAnswer.Measured m -> failtestf "expected NotAvailable, got a measurement: %A" m.Body

let private unmeasuredOf (answer: ToolAnswer<Measurement>) : UnmeasuredScope list =
  match answer with
  | ToolAnswer.Measured m -> m.Unmeasured
  | ToolAnswer.NotAvailable reason -> failtestf "expected a measurement, got NotAvailable %s" (NotAvailableReason.token reason)

// ── The cell-history tools ────────────────────────────────────

[<Tests>]
let cellHistoryTests =
  testList "analysis tools read one session's recorded evals" [

    testCase "WHY — get_cell_dependencies sees both MCP cells and the edge between them" <| fun _ ->
      let json = measuredJson (Answer.cellDependencies twoMcpEvals)
      json.GetProperty("TotalCells").GetInt32() |> Expect.equal "two cells" 2
      json.GetProperty("TotalEdges").GetInt32() |> Expect.equal "one edge" 1

    testCase "WHY — get_cell_dependencies reports staleness as NotMeasured, never as a count of zero" <| fun _ ->
      let json = measuredJson (Answer.cellDependencies twoMcpEvals)
      json.GetProperty("Staleness").GetString() |> Expect.equal "said, not zeroed" "NotMeasured"
      json.TryGetProperty("TotalStale") |> fst |> Expect.isFalse "no stale count at all"

    testCase "WHY — impact_forecast counts the cell downstream of an MCP cell" <| fun _ ->
      let rows = measuredJson (Answer.impactForecast twoMcpEvals CellScope.AllCells)
      let first = rows.EnumerateArray() |> Seq.find (fun r -> r.GetProperty("CellId").GetInt32() = 0)
      first.GetProperty("DownstreamCellCount").GetInt32() |> Expect.equal "cell 1 uses a6x" 1

    testCase "WHY — impact_forecast for one cell answers for that cell only" <| fun _ ->
      let rows = measuredJson (Answer.impactForecast twoMcpEvals (CellScope.OneCell 1))
      rows.GetArrayLength() |> Expect.equal "one row" 1

    testCase "WHY — impact_forecast for a cell the session never recorded is NotAvailable, not a row of zeros" <| fun _ ->
      reasonOf (Answer.impactForecast twoMcpEvals (CellScope.OneCell 9))
      |> Expect.equal "unknown cell" (NotAvailableReason.CellsNotInHistory [ 9 ])

    testCase "WHY — plan_ripple names the cell that uses a changed MCP cell" <| fun _ ->
      measuredText (Answer.planRipple twoMcpEvals "0")
      |> Expect.stringContains "cell 1 is planned" "[1] let a6y = a6x + 1"

    testCase "WHY — plan_ripple for a cell with nothing downstream is a measured zero, not a refusal" <| fun _ ->
      measuredText (Answer.planRipple twoMcpEvals "1")
      |> Expect.stringContains "zero steps, said plainly" "Ripple plan (0 steps, 1 changed)"

    testCase "WHY — plan_ripple for an unknown cell id is NotAvailable (CellsNotInHistory)" <| fun _ ->
      reasonOf (Answer.planRipple twoMcpEvals "0,5")
      |> Expect.equal "the missing id is named" (NotAvailableReason.CellsNotInHistory [ 5 ])

    testCase "WHY — plan_ripple with ids that are not integers is NotAvailable (NoUsableCellIds)" <| fun _ ->
      reasonOf (Answer.planRipple twoMcpEvals "a6x")
      |> Expect.equal "unreadable ids" NotAvailableReason.NoUsableCellIds

    testCase "WHY — preview_what_if finds the binding an MCP eval made and plans its downstream" <| fun _ ->
      let text = measuredText (Answer.previewWhatIf twoMcpEvals "a6x" "5")
      text |> Expect.stringContains "the original value is read" "a6x: 1"
      text |> Expect.stringContains "one affected cell" "Affected cells: 1"

    testCase "WHY — preview_what_if for a name nothing bound is NotAvailable, and invents no value" <| fun _ ->
      reasonOf (Answer.previewWhatIf twoMcpEvals "threshold" "0.5")
      |> Expect.equal "not in scope" (NotAvailableReason.BindingNotInScope "threshold")

    testCase "WHY — suggest_next_cell suggests from the bindings an MCP eval made" <| fun _ ->
      measuredText (Answer.suggestNextCell twoMcpEvals)
      |> Expect.stringContains "a suggestion over a6x" "a6x"

    testCase "WHY — suggest_next_cell with evals that bound nothing says so, as a measured zero" <| fun _ ->
      measuredText (Answer.suggestNextCell evalsThatBindNothing)
      |> Expect.stringContains "counted, not guessed" "1 eval(s) are recorded and none of them bound a name"

    testCase "WHY — every cell-history tool says NoEvalsYet for a session with no recorded eval" <| fun _ ->
      [ "get_cell_dependencies", Answer.cellDependencies noEvals
        "impact_forecast", Answer.impactForecast noEvals CellScope.AllCells
        "plan_ripple", Answer.planRipple noEvals "0"
        "preview_what_if", Answer.previewWhatIf noEvals "x" "1"
        "suggest_next_cell", Answer.suggestNextCell noEvals ]
      |> List.iter (fun (tool, answer) ->
        reasonOf answer |> Expect.equal (sprintf "%s refuses an empty session" tool) NotAvailableReason.NoEvalsYet)
  ]

// ── diagnose ──────────────────────────────────────────────────

[<Tests>]
let diagnoseTests =
  testList "diagnose reads one session's cells and tests" [

    testCase "WHY — with nothing recorded it is NotAvailable (NothingObservedYet), not 'No issues detected'" <| fun _ ->
      reasonOf (Answer.diagnose noEvals (TestSide.none WorkflowKind.Interactive))
      |> Expect.equal "nothing to diagnose" NotAvailableReason.NothingObservedYet

    testCase "WHY — with evals and no test result it names the tests as unmeasured and does not call the session clean" <| fun _ ->
      let answer = Answer.diagnose twoMcpEvals (TestSide.none WorkflowKind.Interactive)
      unmeasuredOf answer |> Expect.equal "tests unmeasured" [ UnmeasuredScope.Tests ]
      let summary = (measuredJson answer).GetProperty("Summary").GetString()
      summary |> Expect.stringContains "says what was not measured" "Not measured: tests"
      summary.Contains "No issues detected" |> Expect.isFalse "never the bare all-clear"

    testCase "WHY — a failing test in the session's own cycle is reported" <| fun _ ->
      let json = measuredJson (Answer.diagnose twoMcpEvals (sideOf failingCycle WorkflowKind.Interactive))
      json.GetProperty("FailureCount").GetInt32() |> Expect.equal "one failure" 1
      json.GetProperty("Severity").GetString() |> Expect.equal "critical" "Critical"

    testCase "WHY — test results alone are enough: the cells are then the unmeasured side" <| fun _ ->
      let answer = Answer.diagnose noEvals (sideOf failingCycle WorkflowKind.Interactive)
      unmeasuredOf answer |> Expect.equal "cells unmeasured" [ UnmeasuredScope.Cells ]
      (measuredJson answer).GetProperty("FailureCount").GetInt32() |> Expect.equal "the failure is still reported" 1

    testCase "WHY — passing tests and evals leave nothing unmeasured" <| fun _ ->
      let passing = cycleWith [ (passingTest, TestResult.Passed (ts 1.0)) ] LiveTestingActivation.Inactive
      let answer = Answer.diagnose twoMcpEvals (sideOf passing WorkflowKind.Interactive)
      unmeasuredOf answer |> Expect.isEmpty "everything was read"
      (measuredJson answer).GetProperty("FailureCount").GetInt32() |> Expect.equal "no failures" 0
  ]

// ── coverage_intel ────────────────────────────────────────────

[<Tests>]
let coverageTests =
  testList "coverage_intel needs coverage" [

    testCase "WHY — with no test result recorded it is NotAvailable (NoTestRunYet), not an empty list" <| fun _ ->
      reasonOf (Answer.coverageIntel (TestSide.none WorkflowKind.Interactive))
      |> Expect.equal "nothing has run" NotAvailableReason.NoTestRunYet

    testCase "WHY — tests ran and none failed is a measured empty list" <| fun _ ->
      let passing = cycleWith [ (passingTest, TestResult.Passed (ts 1.0)) ] LiveTestingActivation.Inactive
      measuredText (Answer.coverageIntel (sideOf passing WorkflowKind.Interactive))
      |> Expect.equal "a real zero" "[]"

    testCase "WHY — a failing test with no coverage, outside the live-testing workflow, needs that workflow" <| fun _ ->
      reasonOf (Answer.coverageIntel (sideOf failingCycle WorkflowKind.Interactive))
      |> Expect.equal "switch workflow" (NotAvailableReason.NeedsAWorkflow WorkflowKind.LiveTesting)

    testCase "WHY — a failing test with no coverage, in the live-testing workflow with live testing off, needs live testing" <| fun _ ->
      reasonOf (Answer.coverageIntel (sideOf failingCycle WorkflowKind.LiveTesting))
      |> Expect.equal "turn it on" NotAvailableReason.NeedsLiveTesting

    testCase "WHY — a failing test with live testing on but no instrumented run yet says no coverage is recorded" <| fun _ ->
      let active = cycleWith [ (failingTest, failed) ] LiveTestingActivation.Active
      reasonOf (Answer.coverageIntel (sideOf active WorkflowKind.LiveTesting))
      |> Expect.equal "run the tests" NotAvailableReason.NoCoverageRecorded

    testCase "WHY — a failing test with a recorded coverage bitmap is measured" <| fun _ ->
      let withBitmap =
        { failingCycle with
            TestState =
              { failingCycle.TestState with
                  TestCoverageBitmaps = Map.ofList [ (failingTest.Id, CoverageBitmap.ofBoolArray [| true; false |]) ] } }
      match Answer.coverageIntel (sideOf withBitmap WorkflowKind.Interactive) with
      | ToolAnswer.Measured _ -> ()
      | ToolAnswer.NotAvailable reason -> failtestf "expected a measurement, got %s" (NotAvailableReason.token reason)
  ]

// ── Routing: the session's own cycle, never the primary's ─────

let private sessionA = "aa000001"
let private sessionB = "bb000002"

[<Tests>]
let routingTests =
  testList "the test side belongs to the session asked about" [

    testCase "WHY — a failing run in a non-primary session's own cycle is that session's failure" <| fun _ ->
      let model = { SageFsModel.initial () with PerSessionLiveTesting = Map.ofList [ (sessionB, failingCycle) ] }
      let side = TestSide.ofModel model sessionB WorkflowKind.Interactive
      (measuredJson (Answer.diagnose twoMcpEvals side)).GetProperty("FailureCount").GetInt32()
      |> Expect.equal "B's failure is seen" 1

    testCase "WHY — another session's failure is not this session's, so this one says its tests are unmeasured" <| fun _ ->
      let model = { SageFsModel.initial () with PerSessionLiveTesting = Map.ofList [ (sessionB, failingCycle) ] }
      let side = TestSide.ofModel model sessionA WorkflowKind.Interactive
      unmeasuredOf (Answer.diagnose twoMcpEvals side)
      |> Expect.equal "A never ran a test" [ UnmeasuredScope.Tests ]

    testCase "WHY — the primary cycle answers for a session only when it carries that session's discovery" <| fun _ ->
      let initial = SageFsModel.initial ()
      let primary =
        { failingCycle with
            TestState =
              { failingCycle.TestState with
                  SessionDiscovery = Map.ofList [ (sessionA, DiscoveryProgress.Completed) ] } }
      let model = { initial with LiveTesting = primary }
      (measuredJson (Answer.diagnose twoMcpEvals (TestSide.ofModel model sessionA WorkflowKind.Interactive))).GetProperty("FailureCount").GetInt32()
      |> Expect.equal "A owns the primary cycle" 1
      unmeasuredOf (Answer.diagnose twoMcpEvals (TestSide.ofModel model sessionB WorkflowKind.Interactive))
      |> Expect.equal "B does not" [ UnmeasuredScope.Tests ]

    testCase "WHY — a session with no cycle of its own has an empty test side, never the primary's" <| fun _ ->
      let model = { SageFsModel.initial () with LiveTesting = failingCycle }
      (TestSide.ofModel model sessionA WorkflowKind.Interactive).Cycle.TestState.LastResults
      |> Expect.isEmpty "nothing borrowed from the primary"
  ]

// ── The wire ──────────────────────────────────────────────────

let private genReason =
  Gen.elements
    [ NotAvailableReason.NoSessionResolved "Two sessions share that directory."
      NotAvailableReason.SessionNotReady SessionReadiness.WarmingUp
      NotAvailableReason.NoEvalsYet
      NotAvailableReason.NoTestRunYet
      NotAvailableReason.NothingObservedYet
      NotAvailableReason.CellsNotInHistory [ 4 ]
      NotAvailableReason.NoUsableCellIds
      NotAvailableReason.BindingNotInScope "x"
      NotAvailableReason.NeedsAWorkflow WorkflowKind.LiveTesting
      NotAvailableReason.NeedsLiveTesting
      NotAvailableReason.NoCoverageRecorded ]

[<Tests>]
let wireTests =
  testList "the answer on the wire" [

    testCase "WHY — a measurement's text block is the body, as it was before the typed field existed" <| fun _ ->
      let answer = ToolAnswer.measured (Rendered.Json """{"TotalCells":2}""")
      textOf answer |> Expect.equal "unchanged text" """{"TotalCells":2}"""

    testCase "WHY — a measurement's structured content carries answer, unmeasured and the parsed payload" <| fun _ ->
      let doc = JsonDocument.Parse(structuredOf (ToolAnswer.measured (Rendered.Json """{"TotalCells":2}""")))
      doc.RootElement.GetProperty("answer").GetString() |> Expect.equal "the kind" "Measured"
      doc.RootElement.GetProperty("unmeasured").GetArrayLength() |> Expect.equal "nothing left out" 0
      doc.RootElement.GetProperty("payload").GetProperty("TotalCells").GetInt32() |> Expect.equal "payload is JSON, not a string" 2

    testCase "WHY — prose bodies are carried as a string payload" <| fun _ ->
      let doc = JsonDocument.Parse(structuredOf (ToolAnswer.measured (Rendered.Prose "Ripple plan (0 steps, 1 changed):")))
      doc.RootElement.GetProperty("payload").GetString() |> Expect.equal "the sentence" "Ripple plan (0 steps, 1 changed):"

    testCase "WHY — an unmeasured scope is listed by token" <| fun _ ->
      let answer = ToolAnswer.Measured { Body = Rendered.Json "{}"; Unmeasured = [ UnmeasuredScope.Tests ] }
      let doc = JsonDocument.Parse(structuredOf answer)
      [ for e in doc.RootElement.GetProperty("unmeasured").EnumerateArray() -> e.GetString() ]
      |> Expect.equal "tests" [ "Tests" ]

    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with maxTest = 100 }
      "WHY — an unavailable answer carries its reason token, a message and what to do, and no payload" <|
      Prop.forAll (Arb.fromGen genReason) (fun reason ->
        let answer = ToolAnswer<Measurement>.NotAvailable reason
        let root = JsonDocument.Parse(structuredOf answer).RootElement
        root.GetProperty("answer").GetString() = "NotAvailable"
        && root.GetProperty("reason").GetString() = NotAvailableReason.token reason
        && root.GetProperty("message").GetString() = NotAvailableReason.describe reason
        && root.GetProperty("whatToDo").GetString() = NotAvailableReason.whatToDo reason
        && not (root.TryGetProperty "payload" |> fst))

    testPropertyWithConfig
      { FsCheckConfig.defaultConfig with maxTest = 100 }
      "WHY — an unavailable answer's text is one plain sentence that starts with the token" <|
      Prop.forAll (Arb.fromGen genReason) (fun reason ->
        let text = textOf (ToolAnswer<Measurement>.NotAvailable reason)
        text.StartsWith(sprintf "Not available (%s)." (NotAvailableReason.token reason)) && not (text.Contains "\n"))
  ]
