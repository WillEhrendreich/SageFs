module SageFs.Features.FeatureHooks

type EvalHistoryEntry = {
  CellIndex: int
  Code: string
  Result: string
  DurationMs: int64
  Timestamp: System.DateTimeOffset
}

type FeaturePushState = {
  LastOutputText: string
  LastEvalDiffSse: string option
  LastCellDepsSse: string option
  LastBindingScopeSse: string option
  LastEvalTimelineSse: string option
  EvalHistory: EvalHistoryEntry list
  /// Incrementally maintained map of binding name → cell index.
  /// Updated in recordEval to avoid O(n) rebuild on every SSE push.
  KnownBindings: Map<string, int>
  /// Cached binding scope snapshot, updated incrementally in recordEval.
  CachedScope: BindingExplorer.BindingScopeSnapshot option
  /// Cached timeline state, updated incrementally in recordEval.
  CachedTimeline: EvalTimeline.TimelineState
}

module FeaturePushState =
  let empty = {
    LastOutputText = ""
    LastEvalDiffSse = None
    LastCellDepsSse = None
    LastBindingScopeSse = None
    LastEvalTimelineSse = None
    EvalHistory = []
    KnownBindings = Map.empty
    CachedScope = None
    CachedTimeline = EvalTimeline.TimelineState.empty
  }

let recordEval (code: string) (result: string) (durationMs: int64) (state: FeaturePushState) =
  let entry = {
    CellIndex = state.EvalHistory.Length  // length before prepend = 0-based index
    Code = code
    Result = result
    DurationMs = durationMs
    Timestamp = System.DateTimeOffset.UtcNow
  }
  let newBindings =
    result.Split('\n')
    |> Array.choose (fun line ->
      let trimmed = line.Trim()
      match trimmed.StartsWith("val ") with
      | false -> None
      | true ->
        let nameEnd = trimmed.IndexOfAny([| ':'; ' ' |], 4)
        match nameEnd > 4 with
        | false -> None
        | true -> Some (trimmed.Substring(4, nameEnd - 4), entry.CellIndex))
    |> Array.fold (fun acc (name, idx) -> Map.add name idx acc) state.KnownBindings
  // Update cached scope incrementally — rebuild from all history (O(n) once per eval, not per SSE push)
  let allCellInputs =
    (entry :: state.EvalHistory)
    |> List.rev
    |> List.map (fun e ->
      let ci: BindingExplorer.CellInput =
        { CellIndex = e.CellIndex; FsiOutput = e.Result; Source = e.Code }
      ci)
  let newScope = BindingExplorer.buildScopeSnapshot allCellInputs
  // Update cached timeline incrementally — record new entry instead of full fold
  let timelineEntry: EvalTimeline.TimelineEntry =
    { CellId = entry.CellIndex; StartMs = 0L; DurationMs = durationMs; Status = EvalTimeline.Success }
  let newTimeline = EvalTimeline.TimelineState.record timelineEntry state.CachedTimeline
  { state with
      EvalHistory = entry :: state.EvalHistory
      KnownBindings = newBindings
      CachedScope = Some newScope
      CachedTimeline = newTimeline }

let computeEvalDiffPush (opts: System.Text.Json.JsonSerializerOptions) (sessionId: string option) (currentOutputText: string) (state: FeaturePushState) =
  let diff = EvalDiff.diffLines (Some state.LastOutputText) (Some currentOutputText)
  let summary = EvalDiff.summarize diff
  let sseStr = SageFs.SseWriter.formatEvalDiffEvent opts sessionId summary
  let updatedState = { state with LastOutputText = currentOutputText }
  if Some sseStr = state.LastEvalDiffSse then
    { updatedState with LastEvalDiffSse = Some sseStr }, None
  else
    { updatedState with LastEvalDiffSse = Some sseStr }, Some sseStr

let computeCellDepsPush (opts: System.Text.Json.JsonSerializerOptions) (sessionId: string option) (state: FeaturePushState) =
  let knownBindings = state.KnownBindings
  let cells =
    state.EvalHistory
    |> List.map (fun (e: EvalHistoryEntry) ->
      CellDependencyGraph.analyzeCell knownBindings e.CellIndex e.Code e.Result)
  let graph = CellDependencyGraph.buildGraph cells
  let sseStr = SageFs.SseWriter.formatCellDependenciesEvent opts sessionId graph
  if Some sseStr = state.LastCellDepsSse then
    { state with LastCellDepsSse = Some sseStr }, None
  else
    { state with LastCellDepsSse = Some sseStr }, Some sseStr

let buildScopeFromState (state: FeaturePushState) =
  state.EvalHistory
  |> List.map (fun (e: EvalHistoryEntry) ->
    { BindingExplorer.CellInput.CellIndex = e.CellIndex
      BindingExplorer.CellInput.FsiOutput = e.Result
      BindingExplorer.CellInput.Source = e.Code } : BindingExplorer.CellInput)
  |> BindingExplorer.buildScopeSnapshot

let computeBindingScopePush (opts: System.Text.Json.JsonSerializerOptions) (sessionId: string option) (state: FeaturePushState) =
  let snapshot =
    state.CachedScope
    |> Option.defaultWith (fun () -> buildScopeFromState state)
  let sseStr = SageFs.SseWriter.formatBindingScopeMapEvent opts sessionId snapshot
  if Some sseStr = state.LastBindingScopeSse then
    { state with LastBindingScopeSse = Some sseStr }, None
  else
    { state with LastBindingScopeSse = Some sseStr }, Some sseStr

let computeEvalTimelinePush (opts: System.Text.Json.JsonSerializerOptions) (sessionId: string option) (state: FeaturePushState) =
  let stats = EvalTimeline.timelineStats 20 state.CachedTimeline
  let sseStr = SageFs.SseWriter.formatEvalTimelineEvent opts sessionId stats
  if Some sseStr = state.LastEvalTimelineSse then
    { state with LastEvalTimelineSse = Some sseStr }, None
  else
    { state with LastEvalTimelineSse = Some sseStr }, Some sseStr
