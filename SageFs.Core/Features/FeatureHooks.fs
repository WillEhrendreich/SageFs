module SageFs.Features.FeatureHooks

type EvalHistoryEntry = EvalStore.EvalHistoryEntry

type FeaturePushState = {
  LastOutputText: string
  /// The last frame pushed for each feature, kept so a client that connects later can be
  /// caught up. ONE slot per feature for the whole daemon, so each carries the scope it was
  /// built for: a replay hands it only to a connection that wants that session.
  LastEvalDiffSse: SageFs.SseFrame option
  LastCellDepsSse: SageFs.SseFrame option
  LastBindingScopeSse: SageFs.SseFrame option
  LastEvalTimelineSse: SageFs.SseFrame option
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
  /// `History.NextId` at the last time the history-derived pushes (cell deps,
  /// binding scope, eval timeline) were emitted. App output changes the output
  /// count but not the history, so comparing against this lets those pushes
  /// skip the O(history) scope/graph rebuild on pure-output ticks.
  LastPushedHistoryVersion: int
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
    // -1 (never the initial empty history's NextId of 0) so the first push
    // always recomputes.
    LastPushedHistoryVersion = -1
  }

  let empty = withCap EvalStore.HistoryCap.standard

let recordEval (code: string) (result: string) (durationMs: int64) (state: FeaturePushState) =
  let history = EvalStore.record code result durationMs System.DateTimeOffset.UtcNow state.History
  let timelineEntry: EvalTimeline.TimelineEntry =
    { CellId = state.History.NextId; StartMs = int64 SageFs.Timeouts.notRun.TotalMilliseconds; DurationMs = durationMs; Status = EvalTimeline.Succeeded }
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
  let frame = SageFs.SseFrame.ofSession sessionId (SageFs.SseWriter.formatEvalDiffEvent opts sessionId summary)
  let updatedState = { state with LastOutputText = currentOutputText }
  if Some frame = state.LastEvalDiffSse then
    { updatedState with LastEvalDiffSse = Some frame }, None
  else
    { updatedState with LastEvalDiffSse = Some frame }, Some frame

let computeCellDepsPush (opts: System.Text.Json.JsonSerializerOptions) (sessionId: string option) (state: FeaturePushState) =
  let frame = SageFs.SseFrame.ofSession sessionId (SageFs.SseWriter.formatCellDependenciesEvent opts sessionId (cellGraph state))
  if Some frame = state.LastCellDepsSse then
    { state with LastCellDepsSse = Some frame }, None
  else
    { state with LastCellDepsSse = Some frame }, Some frame

let computeBindingScopePush (opts: System.Text.Json.JsonSerializerOptions) (sessionId: string option) (state: FeaturePushState) =
  let frame = SageFs.SseFrame.ofSession sessionId (SageFs.SseWriter.formatBindingScopeMapEvent opts sessionId (scope state))
  if Some frame = state.LastBindingScopeSse then
    { state with LastBindingScopeSse = Some frame }, None
  else
    { state with LastBindingScopeSse = Some frame }, Some frame

let computeEvalTimelinePush (opts: System.Text.Json.JsonSerializerOptions) (sessionId: string option) (state: FeaturePushState) =
  let stats = EvalTimeline.timelineStats 20 state.CachedTimeline
  let frame = SageFs.SseFrame.ofSession sessionId (SageFs.SseWriter.formatEvalTimelineEvent opts sessionId stats)
  if Some frame = state.LastEvalTimelineSse then
    { state with LastEvalTimelineSse = Some frame }, None
  else
    { state with LastEvalTimelineSse = Some frame }, Some frame

/// Recompute and emit the history-derived pushes (cell deps, binding scope,
/// eval timeline) ONLY when the eval history advanced since they were last
/// emitted. App output changes the output count but not the history, so on a
/// pure-output tick this skips the O(history) scope/graph rebuild entirely
/// (the server view is unchanged, so nothing is lost — server-authoritative,
/// no client-side state). Returns the updated state, the SSE frames to emit,
/// and the binding scope snapshot to share (Some only when it was recomputed).
let computeHistoryDerivedPushes
  (opts: System.Text.Json.JsonSerializerOptions)
  (sessionId: string option)
  (state: FeaturePushState)
  : FeaturePushState * SageFs.SseFrame list * BindingExplorer.BindingScopeSnapshot option =
  match state.History.NextId = state.LastPushedHistoryVersion with
  | true -> state, [], None
  | false ->
    let state, depsSse = computeCellDepsPush opts sessionId state
    let state, scopeSse = computeBindingScopePush opts sessionId state
    let scopeSnapshot = scope state
    let state, timelineSse = computeEvalTimelinePush opts sessionId state
    let state = { state with LastPushedHistoryVersion = state.History.NextId }
    state, ([ depsSse; scopeSse; timelineSse ] |> List.choose id), Some scopeSnapshot
