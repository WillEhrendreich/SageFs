module SageFs.McpStateHandlers

open SageFs.McpPushNotifications
open SageFs.Features.Diagnostics

/// Immutable state for the model-change handler pipeline.
/// Replaces mutable locals in startMcpServer's state-change subscription.
type ModelChangeState = {
  LastDiagCount: int
  LastOutputCount: int
  LastTestSsePushTicks: int64
  LastTestTraceJson: string
  TestSseThrottleMs: int64
  /// Dedup key of the last test-summary/coverage projection (see
  /// `testSummaryProjectionKey`). "" until the first push.
  LastTestSummaryKey: string
}

module ModelChangeState =
  let empty = {
    LastDiagCount = 0
    LastOutputCount = 0
    LastTestSsePushTicks = 0L
    LastTestTraceJson = ""
    TestSseThrottleMs = 250L
    LastTestSummaryKey = ""
  }

/// Effects produced by processing a model change.
/// The thin shell in McpServer executes these against real infrastructure.
type ModelChangeEffect =
  | AccumulatePush of PushEvent
  | BroadcastTestSse of string

/// Extract error diagnostics as (source, line, message) tuples.
let extractDiagErrors (diagnostics: Map<string, Diagnostic list>) : (string * int * string) list =
  diagnostics
  |> Map.toList
  |> List.collect (fun (_, diags) ->
    diags
    |> List.filter (fun d -> d.Severity = DiagnosticSeverity.Error)
    |> List.map (fun d -> ("fsi", d.Range.StartLine, d.Message)))

/// Pure diagnostics dirty-check: produces AccumulatePush when diagCount changes.
let processDiagnosticsChange
  (diagCount: int)
  (diagnostics: Map<string, Diagnostic list>)
  (state: ModelChangeState)
  : ModelChangeState * ModelChangeEffect list =
  match diagCount <> state.LastDiagCount with
  | true ->
    let errors = extractDiagErrors diagnostics
    { state with LastDiagCount = diagCount },
    [ AccumulatePush (PushEvent.DiagnosticsChanged errors) ]
  | false ->
    state, []

/// Pure test trace dedup: broadcasts when JSON changes and is non-empty.
let processTestTraceChange
  (traceJson: string)
  (state: ModelChangeState)
  : ModelChangeState * ModelChangeEffect list =
  match traceJson.Length > 0 && traceJson <> state.LastTestTraceJson with
  | true ->
    { state with LastTestTraceJson = traceJson },
    [ BroadcastTestSse traceJson ]
  | false ->
    state, []

/// A cheap dedup key for the test-summary + per-file coverage projection.
/// The projection's output is a pure function of the live-test generations,
/// activation, run-in-flight state and discovered-entry count. When this key is
/// unchanged the model changed for some OTHER reason (stdout, diagnostics, a
/// heartbeat test-cycle tick that fired no test work), so re-running the
/// O(files) coverage projection — a `Map.ofSeq` tree-rebalance per annotated
/// file — would produce identical output. Skipping it there is what keeps a
/// 40 Hz tick off the CPU. The first appearance of each distinct state still
/// passes (its key differs from the previous one), so completion/zero-test
/// observability is preserved.
let testSummaryProjectionKey
  (activationTag: string)
  (discoveryGeneration: int64)
  (runGeneration: int)
  (anyRunning: bool)
  (entryCount: int)
  (activeId: string)
  : string =
  System.String.Concat
    [| activationTag; "|"; string discoveryGeneration; "|"; string runGeneration
       "|"; (if anyRunning then "1" else "0"); "|"; string entryCount; "|"; activeId |]

/// Decide whether the (expensive) test-summary/coverage projection should run
/// for this model change. Returns (recompute, updatedState): false skips the
/// push when `key` matches the last projection; true records the new key.
let shouldRecomputeTestSummary (key: string) (state: ModelChangeState) : bool * ModelChangeState =
  match key = state.LastTestSummaryKey with
  | true -> false, state
  | false -> true, { state with LastTestSummaryKey = key }

/// Decide whether a test summary SSE push should go through.
/// Returns true when elapsed >= throttleMs OR run is complete.
let shouldPushTestSummary
  (nowTicks: int64)
  (lastPushTicks: int64)
  (throttleMs: int64)
  (isRunComplete: bool)
  : bool =
  let elapsedMs =
    (nowTicks - lastPushTicks) * 1000L / System.Diagnostics.Stopwatch.Frequency
  elapsedMs >= throttleMs || isRunComplete

/// Bindings change effect — separate from ModelChangeEffect because
/// bindings broadcast carries the full binding map, not a PushEvent.
type BindingsChangeEffect =
  | ResetBindings
  | BroadcastBindings of Map<string, SageFs.SseWriter.FsiBinding>

/// Pure bindings dirty-check: detects output count changes, reset, and binding diffs.
let processBindingsChange
  (outputCount: int)
  (newBindings: Map<string, SageFs.SseWriter.FsiBinding>)
  (lastOutputCount: int)
  (oldBindings: Map<string, SageFs.SseWriter.FsiBinding>)
  : int * Map<string, SageFs.SseWriter.FsiBinding> * BindingsChangeEffect list =
  match outputCount <> lastOutputCount with
  | true ->
    let bindings =
      match outputCount < lastOutputCount with
      | true -> Map.empty
      | false -> oldBindings
    match newBindings <> bindings with
    | true ->
      outputCount, newBindings, [ BroadcastBindings newBindings ]
    | false ->
      outputCount, bindings, []
  | false ->
    lastOutputCount, oldBindings, []
