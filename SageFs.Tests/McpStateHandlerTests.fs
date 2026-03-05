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
    Message = msg }

let extractDiagErrorsTests = testList "extractDiagErrors" [
  test "empty diagnostics yields empty list" {
    extractDiagErrors Map.empty
    |> Expect.isEmpty "should be empty"
  }

  test "filters out warnings, keeps errors" {
    let diags = Map.ofList [
      "file1.fs", [
        mkDiag DiagnosticSeverity.Warning 1 "warn1"
        mkDiag DiagnosticSeverity.Error 2 "err1"
        mkDiag DiagnosticSeverity.Info 3 "info1"
      ]
    ]
    let errors = extractDiagErrors diags
    errors |> Expect.hasLength "should have 1 error" 1
    errors.[0] |> Expect.equal "should be err1" ("fsi", 2, "err1")
  }

  test "collects errors across multiple files" {
    let diags = Map.ofList [
      "a.fs", [ mkDiag DiagnosticSeverity.Error 1 "errA" ]
      "b.fs", [ mkDiag DiagnosticSeverity.Error 5 "errB" ]
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
      "f.fs", [ mkDiag DiagnosticSeverity.Error 10 "type mismatch" ]
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

[<Tests>]
let allStateHandlerTests = testList "McpStateHandlers" [
  extractDiagErrorsTests
  processDiagnosticsChangeTests
  processTestTraceChangeTests
  processBindingsChangeTests
  throttleTests
]
