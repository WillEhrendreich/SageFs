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
  /// W5(R9): Monotonic cell index counter — never derived from EvalHistory.Length.
  /// Survives EvalHistory capping without producing duplicate CellIndex values.
  NextCellIndex: int
  /// Incrementally maintained map of binding name → cell index.
  /// Updated in recordEval to avoid O(n) rebuild on every SSE push.
  KnownBindings: Map<string, int>
  /// Cached binding scope snapshot, updated incrementally in recordEval.
  CachedScope: BindingExplorer.BindingScopeSnapshot option
  /// Cached cell-dependency graph, updated incrementally in recordEval
  /// (roast item 8 — the push previously rebuilt the whole ≤10k-cell graph).
  CachedCellGraph: CellDependencyGraph.CellGraph option
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
    NextCellIndex = 0
    KnownBindings = Map.empty
    CachedScope = None
    CachedCellGraph = None
    CachedTimeline = EvalTimeline.TimelineState.empty
  }

let [<Literal>] MaxEvalHistory = 10_000

let recordEval (code: string) (result: string) (durationMs: int64) (state: FeaturePushState) =
  // W5(R9): Use NextCellIndex (monotonic counter) not EvalHistory.Length.
  // EvalHistory.Length decreases when the cap is applied; NextCellIndex never does.
  let idx = state.NextCellIndex
  let entry = {
    CellIndex = idx
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
    |> Array.fold (fun acc (name, cellIdx) -> Map.add name cellIdx acc) state.KnownBindings
  // W1(R9): Cap EvalHistory at MaxEvalHistory to prevent unbounded O(n) growth.
  // Prepend is O(1); truncate drops the oldest entries at the tail.
  let cappedHistory = (entry :: state.EvalHistory) |> List.truncate MaxEvalHistory
  // The binding scope is updated INCREMENTALLY: only the new cell can change
  // the scope, so merge it into the cached snapshot instead of rebuilding the
  // whole scope from up to 10,000 retained cells on every eval (roast §6 —
  // that was O(n) per eval, O(n²) total). The merge cannot evict cells, so
  // when the history cap actually truncated (an eviction), fall back to the
  // full rebuild.
  let didTruncate = cappedHistory.Length < (entry :: state.EvalHistory).Length
  let newScope =
    match didTruncate with
    | true ->
      let allCellInputs =
        cappedHistory
        |> List.rev
        |> List.map (fun e ->
          let ci: BindingExplorer.CellInput =
            { CellIndex = e.CellIndex; FsiOutput = e.Result; Source = e.Code }
          ci)
      BindingExplorer.buildScopeSnapshot allCellInputs
    | false ->
      match state.CachedScope with
      | None ->
        // First eval — build from the single cell.
        BindingExplorer.buildScopeSnapshot
          [ { BindingExplorer.CellInput.CellIndex = idx
              FsiOutput = result
              Source = code } ]
      | Some prior ->
        let priorCellInputs =
          state.EvalHistory
          |> List.rev
          |> List.map (fun e ->
            let ci: BindingExplorer.CellInput =
              { CellIndex = e.CellIndex; FsiOutput = e.Result; Source = e.Code }
            ci)
        let newCell: BindingExplorer.CellInput =
          { CellIndex = idx; FsiOutput = result; Source = code }
        BindingExplorer.appendCell newCell priorCellInputs prior
  // The cell-dependency graph is maintained the same way: append the new
  // cell incrementally; on history-cap eviction fall back to a full rebuild
  // (an evicted cell's frozen Consumes could reference a name whose producer
  // was evicted, and only a rebuild re-resolves the survivors).
  let newCellGraph =
    match didTruncate with
    | true ->
      cappedHistory
      |> List.rev
      |> List.map (fun e -> CellDependencyGraph.analyzeCell newBindings e.CellIndex e.Code e.Result)
      |> CellDependencyGraph.buildGraph
    | false ->
      match state.CachedCellGraph with
      | None ->
        // First eval — build from the single cell.
        CellDependencyGraph.buildGraph
          [ CellDependencyGraph.analyzeCell newBindings idx code result ]
      | Some prior ->
        CellDependencyGraph.appendCell newBindings prior idx code result
  let timelineEntry: EvalTimeline.TimelineEntry =
    { CellId = idx; StartMs = 0L; DurationMs = durationMs; Status = EvalTimeline.Success }
  let newTimeline = EvalTimeline.TimelineState.record timelineEntry state.CachedTimeline
  { state with
      EvalHistory = cappedHistory
      NextCellIndex = idx + 1
      KnownBindings = newBindings
      CachedScope = Some newScope
      CachedCellGraph = Some newCellGraph
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
  // Use the incrementally-maintained graph; fall back to a full rebuild only
  // if no eval has populated the cache yet (roast item 8).
  let graph =
    state.CachedCellGraph
    |> Option.defaultWith (fun () ->
      state.EvalHistory
      |> List.map (fun (e: EvalHistoryEntry) ->
        CellDependencyGraph.analyzeCell state.KnownBindings e.CellIndex e.Code e.Result)
      |> CellDependencyGraph.buildGraph)
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
