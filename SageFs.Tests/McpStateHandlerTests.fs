module SageFs.Tests.McpStateHandlerTests

open Expecto
open Expecto.Flip
open SageFs.McpStateHandlers
open SageFs.McpPushNotifications
open SageFs.Features.Diagnostics
open SageFs.SseWriter

let mkDiag severity line msg : Diagnostic =
  { Severity = severity
    Range = { StartLine = line; StartColumn = 0; EndLine = line; EndColumn = 0 }
    Subcategory = ""
    Message = msg
    ErrorNumber = 0 }

let extractDiagErrorsTests = testList "extractDiagErrors" [
  test "empty diagnostics yields empty list" {
    extractDiagErrors Map.empty
    |> Expect.isEmpty "should be empty"
  }

  test "filters out warnings, keeps errors" {
    let diags = Map.ofList [
      "file1.fs", [
        mkDiag DiagnosticSeverity.Warning 1 "warn1"
        mkDiag DiagnosticSeverity.Blocking 2 "err1"
        mkDiag DiagnosticSeverity.Info 3 "info1"
      ]
    ]
    let errors = extractDiagErrors diags
    errors |> Expect.hasLength "should have 1 error" 1
    errors.[0] |> Expect.equal "should be err1" ("fsi", 2, "err1")
  }

  test "collects errors across multiple files" {
    let diags = Map.ofList [
      "a.fs", [ mkDiag DiagnosticSeverity.Blocking 1 "errA" ]
      "b.fs", [ mkDiag DiagnosticSeverity.Blocking 5 "errB" ]
    ]
    let errors = extractDiagErrors diags
    errors |> Expect.hasLength "should have 2 errors" 2
  }
]

let processDiagnosticsChangeTests = testList "processDiagnosticsChange" [
  test "no change when diagCount unchanged" {
    let state = { ModelChangeState.empty with LastDiagCount = 3 }
    let state', effects = processDiagnosticsChange 3 Map.empty state
    effects |> Expect.isEmpty "no effects when count unchanged"
    state'.LastDiagCount |> Expect.equal "state unchanged" 3
  }

  test "produces AccumulatePush when diagCount changes" {
    let state = ModelChangeState.empty
    let diags = Map.ofList [
      "f.fs", [ mkDiag DiagnosticSeverity.Blocking 10 "type mismatch" ]
    ]
    let state', effects = processDiagnosticsChange 1 diags state
    state'.LastDiagCount |> Expect.equal "updated count" 1
    effects |> Expect.hasLength "one effect" 1
    match effects.[0] with
    | AccumulatePush (PushEvent.DiagnosticsChanged errors) ->
      errors |> Expect.hasLength "one error" 1
    | other -> failwith (sprintf "unexpected effect: %A" other)
  }

  test "count change with no errors still produces event" {
    let state = ModelChangeState.empty
    let state', effects = processDiagnosticsChange 5 Map.empty state
    state'.LastDiagCount |> Expect.equal "updated to 5" 5
    effects |> Expect.hasLength "one effect" 1
    match effects.[0] with
    | AccumulatePush (PushEvent.DiagnosticsChanged errors) ->
      errors |> Expect.isEmpty "empty error list"
    | other -> failwith (sprintf "unexpected effect: %A" other)
  }
]

let processTestTraceChangeTests = testList "processTestTraceChange" [
  test "broadcasts when trace changes" {
    let state = ModelChangeState.empty
    let json = """{"Enabled":true,"IsRunning":false}"""
    let state', effects = processTestTraceChange json state
    state'.LastTestTraceJson |> Expect.equal "updated trace" json
    effects |> Expect.hasLength "one effect" 1
    match effects.[0] with
    | BroadcastTestSse s -> s |> Expect.equal "json matches" json
    | _ -> failwith "wrong effect"
  }

  test "suppresses duplicate trace" {
    let json = """{"Enabled":true}"""
    let state = { ModelChangeState.empty with LastTestTraceJson = json }
    let _, effects = processTestTraceChange json state
    effects |> Expect.isEmpty "no effects for duplicate"
  }

  test "suppresses empty trace" {
    let state = ModelChangeState.empty
    let _, effects = processTestTraceChange "" state
    effects |> Expect.isEmpty "no effects for empty trace"
  }
]

let processBindingsChangeTests = testList "processBindingsChange" [
  test "no change when output count unchanged" {
    let _, bindings, effects = processBindingsChange 5 Map.empty 5 Map.empty
    effects |> Expect.isEmpty "no effects"
    bindings |> Expect.equal "unchanged" Map.empty
  }

  test "resets bindings when output count decreases (session reset)" {
    let oldBindings =
      Map.ofList [ "x", { Name = "x"; TypeSig = "int"; ShadowCount = 0; Value = None } ]
    let count, _, effects = processBindingsChange 1 Map.empty 5 oldBindings
    count |> Expect.equal "count updated" 1
    effects |> Expect.isEmpty "no broadcast for empty->empty"
  }

  test "broadcasts when bindings change" {
    let newB =
      Map.ofList [ "y", { Name = "y"; TypeSig = "string"; ShadowCount = 0; Value = None } ]
    let _, _, effects = processBindingsChange 2 newB 1 Map.empty
    effects |> Expect.hasLength "one effect" 1
    match effects.[0] with
    | BroadcastBindings b -> b |> Expect.hasLength "one binding" 1
    | _ -> failwith "wrong effect"
  }
]

let throttleTests = testList "shouldPushTestSummary" [
  test "push when elapsed exceeds throttle" {
    let freq = System.Diagnostics.Stopwatch.Frequency
    let lastPush = 0L
    let now = freq // 1 second later
    shouldPushTestSummary now lastPush 250L false
    |> Expect.isTrue "1s > 250ms should push"
  }

  test "suppress when within throttle window" {
    let freq = System.Diagnostics.Stopwatch.Frequency
    let lastPush = 0L
    let now = freq / 10L // 100ms later
    shouldPushTestSummary now lastPush 250L false
    |> Expect.isFalse "100ms < 250ms should suppress"
  }

  test "always push when run is complete" {
    let lastPush = 0L
    let now = 1L // basically no time elapsed
    shouldPushTestSummary now lastPush 250L true
    |> Expect.isTrue "run complete always pushes"
  }
]

let testSummaryDedupTests = testList "testSummaryProjectionKey / shouldRecomputeTestSummary" [
  test "key is stable for identical inputs" {
    let a = testSummaryProjectionKey "Active" 3L 7 false 42 "abcd1234"
    let b = testSummaryProjectionKey "Active" 3L 7 false 42 "abcd1234"
    a |> Expect.equal "same inputs => same key" b
  }

  test "key changes when the run generation advances" {
    let before = testSummaryProjectionKey "Active" 3L 7 false 42 "abcd1234"
    let after = testSummaryProjectionKey "Active" 3L 8 false 42 "abcd1234"
    after |> Expect.notEqual "a new run generation must change the key" before
  }

  test "key changes when the discovery generation advances" {
    let before = testSummaryProjectionKey "Active" 3L 7 false 42 "abcd1234"
    let after = testSummaryProjectionKey "Active" 4L 7 false 42 "abcd1234"
    after |> Expect.notEqual "a new discovery generation must change the key" before
  }

  test "key changes when entry count changes" {
    let before = testSummaryProjectionKey "Active" 3L 7 false 42 "abcd1234"
    let after = testSummaryProjectionKey "Active" 3L 7 false 43 "abcd1234"
    after |> Expect.notEqual "a different entry count must change the key" before
  }

  test "key changes when a run starts (anyRunning flips)" {
    let idle = testSummaryProjectionKey "Active" 3L 7 false 42 "abcd1234"
    let running = testSummaryProjectionKey "Active" 3L 7 true 42 "abcd1234"
    running |> Expect.notEqual "anyRunning must change the key" idle
  }

  test "key changes per active session so a switch always re-projects" {
    let s1 = testSummaryProjectionKey "Active" 3L 7 false 42 "aaaa1111"
    let s2 = testSummaryProjectionKey "Active" 3L 7 false 42 "bbbb2222"
    s2 |> Expect.notEqual "a different active session must change the key" s1
  }

  test "recompute is skipped when the key matches the last projection" {
    let key = testSummaryProjectionKey "Active" 3L 7 false 42 "abcd1234"
    let state = { ModelChangeState.empty with LastTestSummaryKey = key }
    let recompute, state' = shouldRecomputeTestSummary key state
    recompute |> Expect.isFalse "unchanged state must skip the expensive projection"
    state'.LastTestSummaryKey |> Expect.equal "state is untouched on a skip" key
  }

  test "recompute proceeds and records the key when it changed" {
    let oldKey = testSummaryProjectionKey "Active" 3L 7 false 42 "abcd1234"
    let newKey = testSummaryProjectionKey "Active" 3L 8 false 42 "abcd1234"
    let state = { ModelChangeState.empty with LastTestSummaryKey = oldKey }
    let recompute, state' = shouldRecomputeTestSummary newKey state
    recompute |> Expect.isTrue "a changed key must recompute"
    state'.LastTestSummaryKey |> Expect.equal "the new key is recorded" newKey
  }

  test "first projection (empty last key) always recomputes" {
    let key = testSummaryProjectionKey "ReadyZeroTests" 1L 0 false 0 "abcd1234"
    let recompute, _ = shouldRecomputeTestSummary key ModelChangeState.empty
    recompute |> Expect.isTrue "the first appearance of a state must push (zero-test observability)"
  }
]

[<Tests>]
let allStateHandlerTests = testList "McpStateHandlers" [
  extractDiagErrorsTests
  processDiagnosticsChangeTests
  processTestTraceChangeTests
  processBindingsChangeTests
  throttleTests
  testSummaryDedupTests
]
