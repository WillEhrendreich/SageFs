module SageFs.Features.FeatureHooks

type EvalHistoryEntry = EvalStore.EvalHistoryEntry

type FeaturePushState = {
  LastOutputText: string
  LastEvalDiffSse: string option
  LastCellDepsSse: string option
  LastBindingScopeSse: string option
  LastEvalTimelineSse: string option
  /// The retained eval history: bounded, id-indexed, each cell tokenized
  /// once when recorded, so recording an eval costs the same at 100 cells
  /// as at the 10,000-cell cap (see EvalStore).
  History: EvalStore.Store
  /// Binding scope of `History`, built on first read and then shared by
  /// every reader of this state (SSE push, dashboard, MCP tools). Never
  /// built on the `/exec` path.
  Scope: Lazy<BindingExplorer.BindingScopeSnapshot>
  /// Cell-dependency graph of `History`, built on first read and shared.
  CellGraph: Lazy<CellDependencyGraph.CellGraph>
  /// Cached timeline state, updated incrementally in recordEval.
  CachedTimeline: EvalTimeline.TimelineState
}
  with
  /// Retained entries, newest first. Walks the whole history — hot paths use
  /// `recentEvals` instead.
  member s.EvalHistory : EvalHistoryEntry list = EvalStore.newestFirst s.History
  /// Monotonic id of the next cell — never derived from the history length,
  /// so ids stay unique after the cap starts evicting.
  member s.NextCellIndex = s.History.NextId
  /// Newest cell that bound each name.
  member s.KnownBindings = s.History.KnownBindings

[<Literal>]
let MaxEvalHistory = EvalStore.HistoryCap.StandardCells

module FeaturePushState =
  let private emptyScope : BindingExplorer.BindingScopeSnapshot =
    { Bindings = []; ActiveBindings = Map.empty; ShadowedBindings = [] }

  let private emptyGraph : CellDependencyGraph.CellGraph =
    { Cells = Map.empty; Edges = [] }

  /// A fresh state whose history retains the most recent `cap` cells.
  let withCap (cap: EvalStore.HistoryCap) = {
    LastOutputText = ""
    LastEvalDiffSse = None
    LastCellDepsSse = None
    LastBindingScopeSse = None
    LastEvalTimelineSse = None
    History = EvalStore.empty cap
    Scope = Lazy<_>.CreateFromValue emptyScope
    CellGraph = Lazy<_>.CreateFromValue emptyGraph
    CachedTimeline = EvalTimeline.TimelineState.empty
  }

  let empty = withCap EvalStore.HistoryCap.standard

let recordEval (code: string) (result: string) (durationMs: int64) (state: FeaturePushState) =
  let history = EvalStore.record code result durationMs System.DateTimeOffset.UtcNow state.History
  let timelineEntry: EvalTimeline.TimelineEntry =
    { CellId = state.History.NextId; StartMs = 0L; DurationMs = durationMs; Status = EvalTimeline.Success }
  { state with
      History = history
      Scope = lazy (EvalStore.materializeScope history)
      CellGraph = lazy (EvalStore.materializeGraph history)
      CachedTimeline = EvalTimeline.TimelineState.record timelineEntry state.CachedTimeline }

/// The binding scope of the retained history (empty before the first eval).
let scope (state: FeaturePushState) : BindingExplorer.BindingScopeSnapshot =
  state.Scope.Force()

/// The dependency graph of the retained history (empty before the first eval).
let cellGraph (state: FeaturePushState) : CellDependencyGraph.CellGraph =
  state.CellGraph.Force()

/// The `count` most recent evals, oldest first, so the newest is last.
/// Costs O(count), not O(history).
let recentEvals (count: int) (state: FeaturePushState) : EvalHistoryEntry list =
  EvalStore.newest count state.History |> List.rev

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
  let sseStr = SageFs.SseWriter.formatCellDependenciesEvent opts sessionId (cellGraph state)
  if Some sseStr = state.LastCellDepsSse then
    { state with LastCellDepsSse = Some sseStr }, None
  else
    { state with LastCellDepsSse = Some sseStr }, Some sseStr

let computeBindingScopePush (opts: System.Text.Json.JsonSerializerOptions) (sessionId: string option) (state: FeaturePushState) =
  let sseStr = SageFs.SseWriter.formatBindingScopeMapEvent opts sessionId (scope state)
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
