namespace SageFs

open System
open SageFs.WorkerProtocol
open SageFs.WarmUp
open SageFs.Features.Diagnostics
open SageFs.Features.LiveTesting

/// Converts a SessionContext into OutputLine list for the warmup banner.
/// Shown in the output pane BEFORE any eval results so users know what was loaded.
module WarmupBanner =
  let toOutputLines (ctx: SessionContext) : OutputLine list =
    let w = ctx.Warmup
    let sid = ctx.SessionId
    let now = DateTime.UtcNow
    let opened = w.NamespacesOpened.Length
    let failed = w.FailedOpens.Length
    let asmCount = w.AssembliesLoaded.Length
    let lines = Collections.Generic.List<OutputLine>()
    lines.Add {
      Kind = OutputKind.System
      Text = sprintf "🔧 Warmup: %d assemblies, %d opened, %d failed, %dms"
        asmCount opened failed (WarmupContext.totalDurationMs w)
      Timestamp = now; SessionId = sid
    }
    match asmCount > 0 with
    | true ->
      for a in w.AssembliesLoaded do
        lines.Add {
          Kind = OutputKind.System
          Text = sprintf "  📦 %s (%d ns, %d modules)" a.Name a.NamespaceCount a.ModuleCount
          Timestamp = now; SessionId = sid
        }
    | false -> ()
    // Per-namespace opens are streamed via WarmupProgress events during warmup.
    // Banner only shows failures.
    for f in w.FailedOpens do
      let kind = OpenableKind.label f.Kind
      lines.Add {
        Kind = OutputKind.Failure
        Text = sprintf "  ✖ Failed to open %s (%s) — %s" f.Name kind f.ErrorMessage
        Timestamp = now; SessionId = sid
      }
      for d in f.Diagnostics do
        lines.Add {
          Kind = OutputKind.Failure
          Text = sprintf "    %s" (WarmupFcsDiagnostic.formatLine d)
          Timestamp = now; SessionId = sid
        }
      WarmupOpenFailure.suggestedAction f
      |> Option.iter (fun action ->
        lines.Add { Kind = OutputKind.Failure; Text = sprintf "    → %s" action; Timestamp = now; SessionId = sid })
    match ctx.FileStatuses.Length > 0 with
    | true ->
      let loaded = ctx.FileStatuses |> List.filter (fun f -> f.Readiness = Loaded) |> List.length
      lines.Add {
        Kind = OutputKind.System
        Text = sprintf "  Files (%d/%d loaded):" loaded ctx.FileStatuses.Length
        Timestamp = now; SessionId = sid
      }
      for f in ctx.FileStatuses do
        lines.Add {
          Kind = OutputKind.System
          Text = sprintf "    %s %s" (FileReadiness.icon f.Readiness) f.Path
          Timestamp = now; SessionId = sid
        }
    | false -> ()
    lines |> Seq.toList

/// Converts TestRunResult[] into OutputLine list for the session output pane.
/// Shows per-test name, pass/fail status, duration, and failure messages.
module TestOutputFormatter =
  let formatDuration (ts: TimeSpan) =
    match ts.TotalMilliseconds < 1000.0 with
    | true -> sprintf "%dms" (int ts.TotalMilliseconds)
    | false -> sprintf "%.1fs" ts.TotalSeconds

  let private resultLine (r: TestRunResult) : OutputLine =
    let now = DateTime.UtcNow
    match r.Result with
    | TestResult.Passed duration ->
      { Kind = OutputKind.Info
        Text = sprintf "  ✅ %s (%s)" r.TestName (formatDuration duration)
        Timestamp = now; SessionId = "" }
    | TestResult.Failed (failure, duration) ->
      let failMsg =
        match failure with
        | TestFailure.AssertionFailed msg -> msg
        | TestFailure.ExceptionThrown (msg, _) -> msg
        | TestFailure.TimedOut after -> sprintf "Timed out after %s" (formatDuration after)
      { Kind = OutputKind.Failure
        Text = sprintf "  ❌ %s (%s)\n     %s" r.TestName (formatDuration duration) failMsg
        Timestamp = now; SessionId = "" }
    | TestResult.Skipped reason ->
      { Kind = OutputKind.System
        Text = sprintf "  ⏭️ %s — %s" r.TestName reason
        Timestamp = now; SessionId = "" }
    | TestResult.NotRun ->
      { Kind = OutputKind.System
        Text = sprintf "  ⊘ %s (not run)" r.TestName
        Timestamp = now; SessionId = "" }
    | TestResult.NoResult reason ->
      { Kind = OutputKind.System
        Text = sprintf "  ⬜ %s — never reported: %s" r.TestName (NoResultReason.describe reason)
        Timestamp = now; SessionId = "" }

  let private resultLines (r: TestRunResult) : OutputLine list =
    let now = DateTime.UtcNow
    let main = resultLine r
    match r.Output with
    | Some output when not (String.IsNullOrWhiteSpace output) ->
      let outputLine =
        { Kind = OutputKind.System
          Text = sprintf "     │ %s" (output.Replace("\n", "\n     │ "))
          Timestamp = now; SessionId = "" }
      [main; outputLine]
    | _ -> [main]

  let toOutputLines (results: TestRunResult array) : OutputLine list =
    results |> Array.toList |> List.collect resultLines


/// Running aggregate of a live-test run's outcomes. Its ONLY consumer is the
/// run-complete summary line, so it keeps O(1) counts instead of retaining every
/// TestRunResult (each carrying a potentially large Output string). Memory use is
/// therefore constant regardless of how many results stream in OR whether the
/// run's completion event ever fires — the old `TestRunResult array list` grew
/// without bound whenever completion never drained it (the live-testing
/// discovery-completion gap), which is how the daemon reached ~16 GB. The
/// authoritative per-test results already live in LiveTesting.TestState (keyed by
/// TestId, bounded by test count); this aggregate never needed to double-store
/// them — it only ever counted them.
type PendingRunSummary = {
  Passed: int
  Failed: int
  Skipped: int
  /// Total results observed this run — the "N of M never reported" denominator.
  Total: int
  TotalDurationMs: float
  /// Count of results that ended without reporting (NoResult).
  NeverReportedCount: int
  /// Distinct never-reported reasons for the summary text, capped so a run that
  /// produces many distinct TransportFailed messages cannot grow this unbounded.
  NeverReportedReasons: Features.LiveTesting.NoResultReason list
}

module PendingRunSummary =
  /// Max distinct never-reported reasons retained for the summary display.
  let private maxReasons = 16

  let empty : PendingRunSummary =
    { Passed = 0; Failed = 0; Skipped = 0; Total = 0
      TotalDurationMs = 0.0; NeverReportedCount = 0; NeverReportedReasons = [] }

  let private addResult (agg: PendingRunSummary) (r: Features.LiveTesting.TestRunResult) : PendingRunSummary =
    let agg = { agg with Total = agg.Total + 1 }
    match r.Result with
    | Features.LiveTesting.TestResult.Passed d ->
      { agg with Passed = agg.Passed + 1; TotalDurationMs = agg.TotalDurationMs + d.TotalMilliseconds }
    | Features.LiveTesting.TestResult.Failed (_, d) ->
      { agg with Failed = agg.Failed + 1; TotalDurationMs = agg.TotalDurationMs + d.TotalMilliseconds }
    | Features.LiveTesting.TestResult.Skipped _ ->
      { agg with Skipped = agg.Skipped + 1 }
    | Features.LiveTesting.TestResult.NoResult reason ->
      let reasons =
        match List.contains reason agg.NeverReportedReasons
              || List.length agg.NeverReportedReasons >= maxReasons with
        | true -> agg.NeverReportedReasons
        | false -> agg.NeverReportedReasons @ [ reason ]
      { agg with
          NeverReportedCount = agg.NeverReportedCount + 1
          NeverReportedReasons = reasons }
    | Features.LiveTesting.TestResult.NotRun -> agg

  /// Fold one streamed batch of results into the aggregate — O(batch), no retention.
  let addBatch (batch: Features.LiveTesting.TestRunResult array) (agg: PendingRunSummary) : PendingRunSummary =
    Array.fold addResult agg batch

  /// Fold several streamed batches, skipping empties (matches the old buffer's filter).
  let addBatches (batches: Features.LiveTesting.TestRunResult array list) (agg: PendingRunSummary) : PendingRunSummary =
    batches
    |> List.fold
      (fun acc batch ->
        match Array.isEmpty batch with
        | true -> acc
        | false -> addBatch batch acc)
      agg

  /// Total results observed this run (the count the old buffer's `count` returned).
  let count (agg: PendingRunSummary) : int = agg.Total

  /// Render the run-complete summary line from the aggregate — byte-identical text
  /// to the old array-based summaryLine, computed from O(1) counts.
  let toOutputLine (agg: PendingRunSummary) : OutputLine =
    let incomplete =
      match agg.NeverReportedCount with
      | 0 -> ""
      | n ->
        sprintf ", %d of %d never reported — %s"
          n
          agg.Total
          (agg.NeverReportedReasons
           |> List.map Features.LiveTesting.NoResultReason.describe
           |> String.concat "; ")
    let kind =
      match agg.Failed, agg.NeverReportedCount with
      | 0, 0 -> OutputKind.Info
      | _ -> OutputKind.Failure
    { Kind = kind
      Text =
        sprintf "🧪 Test run complete: %d passed, %d failed, %d skipped%s (%s)"
          agg.Passed agg.Failed agg.Skipped incomplete
          (TestOutputFormatter.formatDuration (TimeSpan.FromMilliseconds agg.TotalDurationMs))
      Timestamp = DateTime.UtcNow; SessionId = "" }

/// The unified message type for the SageFs Elm loop.
/// All state changes flow through here — user actions and system events.
type BufferedTestResultsPayload = {
  /// The session every batch below belongs to — batches from a different
  /// session are never absorbed into the same payload (see
  /// `SageFsMsgQueueCoalescing`/`SageFsDispatchReduction`), so this is a
  /// single value, not a per-batch one.
  SessionId: string option
  TotalResultCount: int
  Batches: Features.LiveTesting.TestRunResult array list
}

[<RequireQualifiedAccess>]
type SageFsMsg =
  | Editor of EditorAction
  | Event of TuiEvent
  /// Internal only: queue buffering for several streamed TestResultsBatch payloads.
  /// Lets one Elm drain pay one derived-state refresh while preserving every raw result.
  | BufferedTestResults of BufferedTestResultsPayload
  | CycleTheme
  | EnableLiveTesting
  | DisableLiveTesting
  /// Session-scoped enable/disable (roast UX-6 keystone): targets exactly
  /// this session's own cycle (auto-vivifying `PerSessionLiveTesting` when
  /// it's not Primary), so a background session runs its own independent
  /// live-testing loop without ever being switched to. `EnableLiveTesting`/
  /// `DisableLiveTesting` stay Primary-only for call sites with no session.
  | EnableLiveTestingForSession of sessionId: string
  | DisableLiveTestingForSession of sessionId: string
  | CycleRunPolicy
  | ToggleCoverage
  | TestCycleTick of now: DateTimeOffset
  | BufferContentChanged of targetSession: string option * filePath: string * content: string
  | FileContentChanged of filePath: string * content: string
  | FcsTypeCheckCompleted of string option * Features.LiveTesting.AnalysisIdentity option * Features.LiveTesting.FcsTypeCheckResult
  | RestoreTestCache of Features.LiveTesting.LiveTestState
  | MarkAllTestsStale
  | WorkflowSuggestionReceived of WorkflowTypes.WorkflowSuggestion
  | WorkflowSuggestionDismissed
  | WorkflowSuggestionAccepted
  | RebuildCompleted of string option * int64 * Result<unit, string>

/// Side effects the Elm loop can request.
/// Wraps EditorEffect and TestCycleEffect for async execution.
[<RequireQualifiedAccess>]
type SageFsEffect =
  | Editor of EditorEffect
  | TestCycle of Features.LiveTesting.TestCycleEffect
  | SwitchWorkflow of WorkflowTypes.SessionWorkflow

/// The complete application state managed by the Elm loop.
type SageFsModel = {
  Editor: EditorState
  Sessions: SessionRegistryView
  RecentOutput: SessionOutputStore
  Diagnostics: Map<string, Features.Diagnostics.Diagnostic list>
  CreatingSession: bool
  Theme: ThemeConfig
  ThemeName: string
  SessionContext: SessionContext option
  LiveTesting: Features.LiveTesting.LiveTestCycleState
  /// Accumulates streamed result batches for the summary on TestRunCompleted
  /// without flattening every batch on the hot path.
  PendingRunSummary: PendingRunSummary
  /// Latest resolved test source locations — populated after each discovery pass.
  ResolvedSourceLocations: Features.LiveTesting.TestSourceLocation list
  /// Pending workflow suggestion from project detection — cleared on dismiss or accept.
  PendingSuggestion: WorkflowTypes.WorkflowSuggestion option
  /// Per-session live test cycle state for non-active sessions.
  /// The active session's state lives in LiveTesting; background sessions are tracked here.
  PerSessionLiveTesting: Map<string, Features.LiveTesting.LiveTestCycleState>
  /// Tests currently quarantined (environmentally flaky, or manually) and therefore
  /// excluded from the next automatic selection. Recoverable: `QuarantineLogic.evaluate`
  /// releases an `EnvironmentalFlaky` entry once the test classifies `Stable` again — a
  /// `ManualQuarantine` entry is never auto-released. Session-agnostic: `TestId` is a
  /// content hash of the test's own identity, so one quarantine map covers every session
  /// that discovers the same test.
  QuarantinedTests: Map<Features.LiveTesting.TestId, Features.LiveTesting.QuarantineReason>
  EvalsFinished: EvalTally
}

module SageFsModel =
  /// Whether the live-testing test-cycle tick has anything to fire: a pending
  /// tree-sitter or FCS debounce in the primary or any background session. The
  /// tick only fires debounces, so without one the daemon's timer idles — it
  /// used to run at 40 Hz for as long as any test had ever been discovered.
  let needsLiveTestTick (model: SageFsModel) =
    Features.LiveTesting.TestCycleDebounce.hasPending model.LiveTesting.Debounce
    || (model.PerSessionLiveTesting
        |> Map.exists (fun _ cycle -> Features.LiveTesting.TestCycleDebounce.hasPending cycle.Debounce))

  let initial () = {
    Editor = EditorState.initial
    Sessions = {
      Sessions = []
      ActiveSessionId = ActiveSession.AwaitingSession
      TotalEvals = 0
      WatchStatus = None
    }
    RecentOutput = SessionOutputStore.empty
    Diagnostics = Map.empty
    CreatingSession = false
    Theme =
      match ThemePresets.tryFind "Kanagawa" with
      | Some t -> t
      | None -> Theme.defaults
    ThemeName = "Kanagawa"
    SessionContext = None
    LiveTesting = Features.LiveTesting.LiveTestCycleState.empty
    PendingRunSummary = PendingRunSummary.empty
    ResolvedSourceLocations = []
    PendingSuggestion = None
    PerSessionLiveTesting = Map.empty
    QuarantinedTests = Map.empty
    EvalsFinished = EvalTally.empty
  }

  /// Where a given session's live-testing cycle lives: `Primary` (`LiveTesting`)
  /// when it's the currently-active session, its own slot in
  /// `PerSessionLiveTesting` (`Background`) otherwise. The single source of
  /// truth for this resolution — both the write side (`SageFsUpdate`'s
  /// `tryUpdateLiveTestingState`) and every read side (dashboard/cohort
  /// per-session queries) go through it, so a session's tests/discovery/results
  /// always land in, and are always read from, the same cycle.
  [<RequireQualifiedAccess>]
  type LiveTestingTarget =
    | Primary
    | Background of string

  let tryResolveLiveTestingTarget
    (targetSession: string option)
    (model: SageFsModel)
    : LiveTestingTarget option =
    // An empty id names no session: it is the "no particular session" query
    // (e.g. MCP status with nothing active), which has always meant Primary.
    let targetSession = targetSession |> Option.filter (fun sid -> sid <> "")
    let activeSessionId =
      ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.map SessionId.value
    let primaryCycleOwnsSession sid =
      let cycle = model.LiveTesting
      cycle.TestState.RunPhases |> Map.containsKey sid
      || (cycle.TestState.SessionDiscovery |> Map.containsKey sid)
      || (cycle.PendingRebuild |> Option.exists (fun pending -> pending.SessionId = Some sid))
      || (cycle.QueuedRebuild |> Option.exists (fun queued -> queued.SessionId = Some sid))
    // Primary holds NO session's data yet. With no active pointer, Primary is
    // only ever given to a session it already owns or to the FIRST session into
    // an unowned Primary, so it has at most one owner by construction. The old
    // rule (`PerSessionLiveTesting.IsEmpty`) let a SECOND session write into a
    // Primary that already belonged to another, where TestsDiscovered's
    // wholesale replace erased the first session's tests (found by
    // CohortLandingVerifyDstTests: a daemon-owned integration session plus an
    // agent session with no dashboard viewer).
    let primaryUnowned =
      let cycle = model.LiveTesting
      cycle.TestState.SessionDiscovery.IsEmpty
      && cycle.TestState.RunPhases.IsEmpty
      && cycle.PendingRebuild.IsNone
      && cycle.QueuedRebuild.IsNone
    match targetSession with
    | Some sid when activeSessionId = Some sid ->
      Some LiveTestingTarget.Primary
    | Some sid ->
      match model.PerSessionLiveTesting |> Map.containsKey sid with
      | true -> Some (LiveTestingTarget.Background sid)
      | false ->
          match activeSessionId with
          | None when primaryCycleOwnsSession sid -> Some LiveTestingTarget.Primary
          | None when primaryUnowned -> Some LiveTestingTarget.Primary
          | _ -> None
    | None ->
      Some LiveTestingTarget.Primary

  let cycleFor (target: LiveTestingTarget) (model: SageFsModel) : Features.LiveTesting.LiveTestCycleState =
    match target with
    | LiveTestingTarget.Primary -> model.LiveTesting
    | LiveTestingTarget.Background sid ->
      // Total, not `Map.find`: a write to a freshly-resolved `Background sid`
      // (see `resolveOrCreateLiveTestingTarget`) has no map entry yet.
      model.PerSessionLiveTesting
      |> Map.tryFind sid
      |> Option.defaultValue Features.LiveTesting.LiveTestCycleState.empty

  /// WRITE-path resolution (roast UX-6 keystone): an explicit, daemon-verified
  /// sessionId always lands somewhere, unlike `tryResolveLiveTestingTarget`
  /// (used for reads), which returns `None` for a background session with no
  /// `PerSessionLiveTesting` entry yet — silently dropping its own
  /// warmup-discovered tests/results. Reads never auto-vivify; only writes do.
  let resolveOrCreateLiveTestingTarget
    (targetSession: string option)
    (model: SageFsModel)
    : LiveTestingTarget =
    match tryResolveLiveTestingTarget targetSession model with
    | Some target -> target
    | None ->
      match targetSession with
      | Some sid -> LiveTestingTarget.Background sid
      | None -> LiveTestingTarget.Primary

  /// The `LiveTestCycleState` that belongs to `sessionId`, for reads (dashboard
  /// per-session summaries, cohort outcome queries, SSE activity). Falls back to
  /// an EMPTY cycle — never Primary — when the session can't be resolved, so
  /// querying an unrelated/unknown session can never leak another session's data.
  let cycleForSession (sessionId: string) (model: SageFsModel) : Features.LiveTesting.LiveTestCycleState =
    match tryResolveLiveTestingTarget (Some sessionId) model with
    | Some target -> cycleFor target model
    | None -> Features.LiveTesting.LiveTestCycleState.empty

  /// The cycle that OWNS `sessionId`'s data, resolved INDEPENDENTLY of the active
  /// pointer (so a cohort-landing verdict survives a pointer diverge): Primary
  /// when it CARRIES this session's discovery (containsKey, NOT the tryHead
  /// `ownerSessionId`), else the session's own background slot.
  let cycleOwnedBySession (sessionId: string) (model: SageFsModel) : Features.LiveTesting.LiveTestCycleState =
    match model.LiveTesting.TestState.SessionDiscovery |> Map.containsKey sessionId with
    | true -> model.LiveTesting
    | false ->
      model.PerSessionLiveTesting
      |> Map.tryFind sessionId
      |> Option.defaultValue Features.LiveTesting.LiveTestCycleState.empty

  /// The one live-testing state for a session, as every surface shows it —
  /// tests, discovery, activation, compile block and pending rebuild all come
  /// from that session's own cycle now (see `cycleForSession`).
  let liveTestActivityFor (sessionId: string) (model: SageFsModel) : Features.LiveTestActivity.LiveTestActivity =
    let cycle = cycleForSession sessionId model
    Features.LiveTestActivity.LiveTestActivity.activityInput sessionId cycle
    |> Features.LiveTestActivity.LiveTestActivity.decide

  /// Project the current workflow from the session context.
  /// Defaults to Interactive when no session is active.
  let currentWorkflow (model: SageFsModel) : WorkflowTypes.SessionWorkflow =
    match model.SessionContext with
    | Some ctx -> ctx.Workflow
    | None -> WorkflowTypes.SessionWorkflow.Interactive
  let addOutputLine (line: OutputLine) (store: SessionOutputStore) = store.Add(line); store

  /// Add multiple output lines, routing each to the correct session buffer.
  let addOutput (lines: OutputLine list) (store: SessionOutputStore) =
    store.AddRange(lines)
    store

module SageFsMsgQueueCoalescing =
  let [<Literal>] private MaxBufferedTestResultCount = 400

  type private PendingSearch<'Msg> =
    | Replace of 'Msg
    | Continue
    | Stop

  let private tryReplaceLast
    (pending: ResizeArray<SageFsMsg>)
    (chooseReplacement: SageFsMsg -> PendingSearch<SageFsMsg>)
    =
    let rec loop index =
      match index < 0 with
      | true -> false
      | false ->
        match chooseReplacement pending[index] with
        | Replace replacement ->
          pending[index] <- replacement
          true
        | Continue ->
          loop (index - 1)
        | Stop ->
          false
    loop (pending.Count - 1)

  let private tryCreateBufferedTestResults
    (sessionId: string option)
    (existing: Features.LiveTesting.TestRunResult array)
    (incoming: Features.LiveTesting.TestRunResult array)
    =
    let total = existing.Length + incoming.Length
    match total <= MaxBufferedTestResultCount with
    | true ->
      Some {
        SessionId = sessionId
        TotalResultCount = total
        Batches = [ existing; incoming ]
      }
    | false ->
      None

  let private tryAppendBufferedTestResults
    (buffered: BufferedTestResultsPayload)
    (incoming: Features.LiveTesting.TestRunResult array)
    =
    let total = buffered.TotalResultCount + incoming.Length
    match total <= MaxBufferedTestResultCount with
    | true ->
      Some {
        buffered with
          TotalResultCount = total
          Batches = buffered.Batches @ [ incoming ]
      }
    | false ->
      None

  let tryAbsorbPending
    (pending: ResizeArray<SageFsMsg>)
    (incoming: SageFsMsg)
    =
    match incoming with
    | SageFsMsg.TestCycleTick _ ->
      tryReplaceLast pending (function
        | SageFsMsg.TestCycleTick _ -> Replace incoming
        | _ -> Continue)
    | SageFsMsg.Editor EditorAction.ListSessions ->
      tryReplaceLast pending (function
        | SageFsMsg.Editor EditorAction.ListSessions -> Replace incoming
        | _ -> Continue)
    | SageFsMsg.Event (TuiEvent.SessionsRefreshed _) ->
      tryReplaceLast pending (function
        | SageFsMsg.Event (TuiEvent.SessionsRefreshed _) -> Replace incoming
        | _ -> Continue)
    | SageFsMsg.Event (TuiEvent.WarmupContextUpdated _) ->
      tryReplaceLast pending (function
        | SageFsMsg.Event (TuiEvent.WarmupContextUpdated _) -> Replace incoming
        | _ -> Continue)
    | SageFsMsg.Event (TuiEvent.TestResultsBatch (sessionId, results)) ->
      tryReplaceLast pending (function
        // Never absorb across sessions — each session's results merge into
        // its own cycle (SageFsUpdate), so a batch belonging to a different
        // session must stay a separate message.
        | SageFsMsg.Event (TuiEvent.TestResultsBatch (existingSid, existing)) when existingSid = sessionId ->
          match tryCreateBufferedTestResults sessionId existing results with
          | Some buffered -> Replace (SageFsMsg.BufferedTestResults buffered)
          | None -> Stop
        | SageFsMsg.BufferedTestResults buffered when buffered.SessionId = sessionId ->
          match tryAppendBufferedTestResults buffered results with
          | Some updated -> Replace (SageFsMsg.BufferedTestResults updated)
          | None -> Stop
        | SageFsMsg.Event (TuiEvent.TestResultsBatch _)
        | SageFsMsg.BufferedTestResults _
        | SageFsMsg.Event (TuiEvent.TestRunCompleted _)
        | SageFsMsg.Event (TuiEvent.TestRunStarted _)
        // A requested run's start is a run boundary too: results must never
        // merge across it, or they are stamped with the wrong run's generation.
        | SageFsMsg.Event (TuiEvent.TestRunStartedAt _) ->
          Stop
        | _ ->
          Continue)
    | _ ->
      false

/// Pure update function: routes SageFsMsg through the right handler.
module SageFsUpdate =
  let resolveSessionId (model: SageFsModel) : string option =
    match model.Editor.SelectedSessionIndex with
    | None -> None
    | Some idx ->
      let sessions = model.Sessions.Sessions
      match idx >= 0 && idx < sessions.Length with
      | true -> Some (SessionId.value sessions.[idx].Id)
      | false -> None

  let resolveConfigWorkingDirectory (model: SageFsModel) =
    let tryPromptDir =
      match model.Editor.Prompt with
      | Some prompt when prompt.Purpose = PromptPurpose.CreateSessionDir && not (String.IsNullOrWhiteSpace prompt.Input) ->
        Some prompt.Input
      | _ -> None

    let trySelectedSessionDir =
      match model.Editor.SelectedSessionIndex with
      | Some idx when idx >= 0 && idx < model.Sessions.Sessions.Length ->
        let dir = model.Sessions.Sessions.[idx].WorkingDirectory
        match String.IsNullOrWhiteSpace dir with
        | true -> None
        | false -> Some dir
      | _ -> None

    let tryActiveSessionDir =
      match model.Sessions.ActiveSessionId with
      | ActiveSession.Viewing sessionId ->
        model.Sessions.Sessions
        |> List.tryFind (fun session -> session.Id = sessionId)
        |> Option.bind (fun session ->
          match String.IsNullOrWhiteSpace session.WorkingDirectory with
          | true -> None
          | false -> Some session.WorkingDirectory)
      | ActiveSession.AwaitingSession -> None

    [ tryPromptDir; trySelectedSessionDir; tryActiveSessionDir; Some Environment.CurrentDirectory ]
    |> List.tryPick id
    |> Option.defaultValue Environment.CurrentDirectory

  /// When a prompt is active, remap editor input actions to prompt actions.
  let remapForPrompt (action: EditorAction) (prompt: PromptState option) : EditorAction =
    match prompt with
    | None -> action
    | Some _ ->
      match action with
      | EditorAction.InsertChar c -> EditorAction.PromptChar c
      | EditorAction.DeleteBackward -> EditorAction.PromptBackspace
      | EditorAction.NewLine -> EditorAction.PromptConfirm
      | EditorAction.Submit -> EditorAction.PromptConfirm
      | EditorAction.Cancel -> EditorAction.PromptCancel
      | EditorAction.DismissCompletion -> EditorAction.PromptCancel
      | other -> other

  [<RequireQualifiedAccess>]
  type private LiveTestingStatusRefresh =
    | Recompute
    | KeepExisting
    | PatchChangedEntries of Features.LiveTesting.TestStatusEntry array

  type private BufferedRefreshTimings = {
    SummaryMs: float
    NarrativesMs: float
    AnnotationsMs: float
  }

  let private finalizeLiveTestingState
    (refresh: LiveTestingStatusRefresh)
    (lt: Features.LiveTesting.LiveTestCycleState)
    (updated: Features.LiveTesting.LiveTestState)
    =
    let isBufferedRefresh =
      match refresh with
      | LiveTestingStatusRefresh.PatchChangedEntries _ -> true
      | _ -> false
    let recordBufferedPhase (histogram: System.Diagnostics.Metrics.Histogram<float>) (sw: System.Diagnostics.Stopwatch) =
      match isBufferedRefresh with
      | true ->
        sw.Stop()
        histogram.Record(sw.Elapsed.TotalMilliseconds)
      | false -> ()
    let withStatuses =
      match refresh with
      | LiveTestingStatusRefresh.Recompute ->
        let previous =
          Features.LiveTesting.LiveTestState.orderedStatusEntries lt.TestState
          |> Array.map (fun e -> e.TestId, e.Status)
          |> Map.ofArray
        Features.LiveTesting.LiveTestState.withStatusEntries
          (Features.LiveTesting.LiveTesting.computeStatusEntriesWithHistory previous updated)
          updated
      | LiveTestingStatusRefresh.KeepExisting ->
        updated
      | LiveTestingStatusRefresh.PatchChangedEntries _ ->
        updated
    let summarySw =
      match isBufferedRefresh with
      | true -> Some (System.Diagnostics.Stopwatch.StartNew())
      | false -> None
    let summary =
      match refresh with
      | LiveTestingStatusRefresh.PatchChangedEntries changedEntries ->
        Features.LiveTesting.TestSummary.applyStatusEntryChanges
          withStatuses.Activation
           lt.TestState.Cached.TestSummary
          changedEntries
      | _ ->
        Features.LiveTesting.TestSummary.fromStatuses withStatuses.Activation (withStatuses.StatusIndex.Entries |> Array.map (fun e -> e.Status))
    let summaryMs =
      match summarySw with
      | Some sw ->
        recordBufferedPhase Instrumentation.liveTestingBufferedSummaryMs sw
        sw.Elapsed.TotalMilliseconds
      | None -> 0.0
    let withCache = { withStatuses with Cached = { withStatuses.Cached with StateVersion = lt.TestState.Cached.StateVersion + 1L; TestSummary = summary } }
    let narrativesSw =
      match isBufferedRefresh with
      | true -> Some (System.Diagnostics.Stopwatch.StartNew())
      | false -> None
    let withNarratives =
      let narratives =
        match refresh with
        | LiveTestingStatusRefresh.PatchChangedEntries changedEntries ->
          Features.LiveTesting.LiveTesting.applyFailureNarrativeChanges
            System.DateTimeOffset.UtcNow
            lt.ChangedSymbols
            []
            lt.TestState.Cached.FailureNarratives
            changedEntries
            withCache
        | _ ->
          Features.LiveTesting.LiveTesting.computeFailureNarratives
            System.DateTimeOffset.UtcNow lt.ChangedSymbols [] lt.TestState.Cached.FailureNarratives withCache
      { withCache with Cached = { withCache.Cached with FailureNarratives = narratives } }
    let narrativesMs =
      match narrativesSw with
      | Some sw ->
        recordBufferedPhase Instrumentation.liveTestingBufferedNarrativesMs sw
        sw.Elapsed.TotalMilliseconds
      | None -> 0.0
    let annotationsSw =
      match isBufferedRefresh with
      | true -> Some (System.Diagnostics.Stopwatch.StartNew())
      | false -> None
    let withAnnotations = { withNarratives with Cached = { withNarratives.Cached with EditorAnnotations = Features.LiveTesting.LiveTesting.recomputeEditorAnnotations lt.ActiveFile withNarratives } }
    let annotationsMs =
      match annotationsSw with
      | Some sw ->
        recordBufferedPhase Instrumentation.liveTestingBufferedAnnotationsMs sw
        sw.Elapsed.TotalMilliseconds
      | None -> 0.0
    let timings =
      match isBufferedRefresh with
      | true ->
        Some {
          SummaryMs = summaryMs
          NarrativesMs = narrativesMs
          AnnotationsMs = annotationsMs
        }
      | false -> None
    { lt with TestState = withAnnotations }, timings

  /// Every LiveTestState mutation that affects test lifecycle MUST finalize cached
  /// live-testing views. Most mutations need a full status recompute; a few hot paths
  /// can reuse incrementally prepared StatusEntries and only refresh the cached views.
  let recomputeStatuses (lt: Features.LiveTesting.LiveTestCycleState) (updateState: Features.LiveTesting.LiveTestState -> Features.LiveTesting.LiveTestState) =
    updateState lt.TestState
    |> finalizeLiveTestingState LiveTestingStatusRefresh.Recompute lt
    |> fst

  let refreshStatusesKeepingEntries
    (lt: Features.LiveTesting.LiveTestCycleState)
    (updateState: Features.LiveTesting.LiveTestState -> Features.LiveTesting.LiveTestState)
    =
    updateState lt.TestState
    |> finalizeLiveTestingState LiveTestingStatusRefresh.KeepExisting lt
    |> fst

  let refreshStatusesForChangedIds
    (lt: Features.LiveTesting.LiveTestCycleState)
    (changedIds: Set<Features.LiveTesting.TestId>)
    (updateState: Features.LiveTesting.LiveTestState -> Features.LiveTesting.LiveTestState)
    =
    let priorState = lt.TestState
    let updatedState = updateState priorState
    match Set.isEmpty changedIds, Array.isEmpty priorState.StatusIndex.Entries with
    | true, _ ->
      finalizeLiveTestingState
        LiveTestingStatusRefresh.KeepExisting
        lt
        updatedState
      |> fst
    | _, true ->
      finalizeLiveTestingState
        LiveTestingStatusRefresh.Recompute
        lt
        updatedState
      |> fst
    | _ ->
      let patchedState, changedEntries =
        Features.LiveTesting.LiveTesting.patchStatusEntriesForChangedIds
          priorState
          updatedState
          changedIds
      finalizeLiveTestingState
        (LiveTestingStatusRefresh.PatchChangedEntries changedEntries)
        lt
        patchedState
      |> fst

  let private activeLiveTestingSessionId (model: SageFsModel) =
    ActiveSession.sessionId model.Sessions.ActiveSessionId
    |> Option.map SessionId.value

  // Resolution of "which cycle does this session own" is shared with the read
  // side (`SageFsModel.tryResolveLiveTestingTarget`/`cycleForSession`) — one
  // source of truth for where a session's live-testing state lives.

  let private setLiveTestingState
    (target: SageFsModel.LiveTestingTarget)
    (cycle: Features.LiveTesting.LiveTestCycleState)
    (model: SageFsModel)
    =
    match target with
    | SageFsModel.LiveTestingTarget.Primary ->
      { model with LiveTesting = cycle }
    | SageFsModel.LiveTestingTarget.Background sid ->
      { model with
          PerSessionLiveTesting =
            model.PerSessionLiveTesting
            |> Map.add sid cycle }

  let private tryUpdateLiveTestingState
    (targetSession: string option)
    (updateCycle: Features.LiveTesting.LiveTestCycleState -> Features.LiveTesting.LiveTestCycleState * 'result)
    (model: SageFsModel)
    =
    // Auto-vivifies (roast UX-6 keystone) — see resolveOrCreateLiveTestingTarget.
    let target = SageFsModel.resolveOrCreateLiveTestingTarget targetSession model
    let current = SageFsModel.cycleFor target model
    let cycle', result = updateCycle current
    setLiveTestingState target cycle' model, Some result

  let private retagRequestFcsTypeCheck
    (targetSession: string option)
    (effect: Features.LiveTesting.TestCycleEffect)
    =
    match effect with
    | Features.LiveTesting.TestCycleEffect.RequestFcsTypeCheck req ->
      Features.LiveTesting.TestCycleEffect.RequestFcsTypeCheck { req with SessionId = targetSession }
    | _ ->
      effect

  let private cycleTickChanged
    (original: Features.LiveTesting.LiveTestCycleState)
    (updated: Features.LiveTesting.LiveTestCycleState)
    (effects: Features.LiveTesting.TestCycleEffect list)
    =
    not effects.IsEmpty
    || not (obj.ReferenceEquals(updated, original))

  let private pendingRebuildCancellationEffects
    (cycle: Features.LiveTesting.LiveTestCycleState)
    =
    cycle
    |> Features.LiveTesting.LiveTestCycleState.pendingRebuildCancellationEffect
    |> Option.toList
    |> List.map SageFsEffect.TestCycle

  let private switchActiveLiveTestingState
    (fromId: string option)
    (toId: string)
    (model: SageFsModel)
    =
    // With no active pointer, Primary may still hold a session's data (the
    // first session to write with nobody viewing); its single owner (see
    // `tryResolveLiveTestingTarget`) is the session to PARK. Without this a
    // switch made while nothing was active dropped that session's discovery,
    // which is the same wipe the pre-9a73197a landing performer triggered.
    let currentActiveId =
      activeLiveTestingSessionId model
      |> Option.orElse fromId
      |> Option.orElse (Features.LiveTesting.LiveTestState.ownerSessionId model.LiveTesting.TestState)

    match currentActiveId with
    | Some current when current = toId ->
      model
    | _ ->
      let promotedState =
        model.PerSessionLiveTesting
        |> Map.tryFind toId
        |> Option.defaultValue Features.LiveTesting.LiveTestCycleState.empty

      let parkedBackground =
        model.PerSessionLiveTesting
        |> Map.remove toId
        |> fun background ->
          match currentActiveId with
          | Some current when current <> toId ->
            background |> Map.add current model.LiveTesting
          | _ ->
            background

      { model with
          LiveTesting = promotedState
          PerSessionLiveTesting = parkedBackground }

  /// Fold every result's flaky classification into a quarantine decision.
  /// This is the fold where a test result lands and `FlakyHistory` is
  /// updated (`applyBufferedTestResults` below) — `QuarantineLogic.evaluate`
  /// had zero non-test callers before this (a test could be classified
  /// flaky and the product never demoted it), so this is the natural and
  /// correct place to close that gap: the classification input (the
  /// just-updated history) is already in hand here.
  let private evaluateQuarantineForBatch
    (results: Features.LiveTesting.TestRunResult list)
    (flakyHistory: Map<Features.LiveTesting.TestId, Features.LiveTesting.ResultWindow>)
    (lastResults: Map<Features.LiveTesting.TestId, Features.LiveTesting.TestRunResult>)
    (quarantined: Map<Features.LiveTesting.TestId, Features.LiveTesting.QuarantineReason>)
    : Map<Features.LiveTesting.TestId, Features.LiveTesting.QuarantineReason> =
    let now = DateTimeOffset.UtcNow
    results
    |> List.map (fun r -> r.TestId)
    |> List.distinct
    |> List.fold
      (fun q testId ->
        let classification = Features.LiveTesting.FlakyDetection.classifyFlakiness testId flakyHistory lastResults
        Features.LiveTesting.QuarantineLogic.evaluate testId classification q now
        |> fun action -> Features.LiveTesting.QuarantineLogic.apply action q)
      quarantined

  let private applyBufferedTestResults
    (sessionId: string option)
    (batches: Features.LiveTesting.TestRunResult array list)
    (model: SageFsModel)
    =
    let nonEmptyBatches =
      batches |> List.filter (fun batch -> not (Array.isEmpty batch))
    match nonEmptyBatches with
    | [] ->
      model, []
    | _ ->
      let applySw = System.Diagnostics.Stopwatch.StartNew()
      let mergeSw = System.Diagnostics.Stopwatch.StartNew()
      // Merge into the TARGET session's own cycle — never the shared primary
      // unconditionally — so two sessions' results (even for colliding TestIds
      // across two checkouts of one repo) can never clobber each other.
      let model', outcome =
        tryUpdateLiveTestingState sessionId (fun cycle ->
          let merged, changedEntries =
            Features.LiveTesting.LiveTesting.mergeBufferedResultsWithUpdatedStatusEntriesAndChangedEntries
              cycle.TestState
              nonEmptyBatches
          let allResults = nonEmptyBatches |> List.collect Array.toList
          let updatedHistory =
            allResults
            |> List.fold
              (fun hist result ->
                Features.LiveTesting.FlakyDetection.recordResult result.TestId result.Result hist)
              merged.FlakyHistory
          let mergedWithHistory =
            Features.LiveTesting.RequestedRuns.stampResults sessionId allResults { merged with FlakyHistory = updatedHistory }
          let quarantined' =
            evaluateQuarantineForBatch allResults updatedHistory mergedWithHistory.LastResults model.QuarantinedTests
          let refresh =
            match Array.isEmpty changedEntries with
            | true -> LiveTestingStatusRefresh.KeepExisting
            | false -> LiveTestingStatusRefresh.PatchChangedEntries changedEntries
          let cycle', timings = finalizeLiveTestingState refresh cycle mergedWithHistory
          cycle', (timings, changedEntries, quarantined')) model
      mergeSw.Stop()
      Instrumentation.liveTestingBufferedMergeMs.Record(mergeSw.Elapsed.TotalMilliseconds)
      let pendingResults =
        PendingRunSummary.addBatches nonEmptyBatches model.PendingRunSummary
      applySw.Stop()
      Instrumentation.liveTestingBufferedApplyMs.Record(applySw.Elapsed.TotalMilliseconds)
      match outcome with
      | None -> model, []
      | Some (timings, changedEntries, quarantined') ->
        match timings with
        | Some phaseTimings when applySw.Elapsed.TotalMilliseconds >= 100.0 ->
          Utils.Log.warn
            "[LiveTesting] Slow buffered apply: total=%.1fms merge=%.1fms summary=%.1fms narratives=%.1fms annotations=%.1fms batches=%d changed=%d"
            applySw.Elapsed.TotalMilliseconds
            mergeSw.Elapsed.TotalMilliseconds
            phaseTimings.SummaryMs
            phaseTimings.NarrativesMs
            phaseTimings.AnnotationsMs
            nonEmptyBatches.Length
            changedEntries.Length
        | _ -> ()
        { model' with PendingRunSummary = pendingResults; QuarantinedTests = quarantined' }, []

  let update (msg: SageFsMsg) (model: SageFsModel) : SageFsModel * SageFsEffect list =
    match msg with
    | SageFsMsg.Editor action ->
      let action = remapForPrompt action model.Editor.Prompt
      match action with
      | EditorAction.SessionSelect ->
        match resolveSessionId model with
        | Some sid ->
          let newEditor, _ = EditorUpdate.update action model.Editor
          { model with Editor = newEditor },
          [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch sid)]
        | None -> model, []
      | EditorAction.SessionDelete ->
        match resolveSessionId model with
        | Some sid ->
          let newEditor, _ = EditorUpdate.update action model.Editor
          { model with Editor = newEditor },
          [SageFsEffect.Editor (EditorEffect.RequestSessionStop sid)]
        | None -> model, []
      | EditorAction.SessionStopOthers ->
        let activeId = ActiveSession.sessionId model.Sessions.ActiveSessionId
        let others =
          model.Sessions.Sessions
          |> List.filter (fun s -> Some s.Id <> activeId)
          |> List.map (fun s -> SageFsEffect.Editor (EditorEffect.RequestSessionStop (SessionId.value s.Id)))
        model, others
      | EditorAction.SessionCycleNext ->
        let count = model.Sessions.Sessions.Length
        match count <= 1 with
        | true -> model, []
        | false ->
          let currentIdx = model.Editor.SelectedSessionIndex |> Option.defaultValue 0
          let nextIdx = (currentIdx + 1) % count
          let sid = model.Sessions.Sessions.[nextIdx].Id
          let newEditor = { model.Editor with SelectedSessionIndex = Some nextIdx }
          { model with Editor = newEditor },
          [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch (SessionId.value sid))]
      | EditorAction.SessionCyclePrev ->
        let count = model.Sessions.Sessions.Length
        match count <= 1 with
        | true -> model, []
        | false ->
          let currentIdx = model.Editor.SelectedSessionIndex |> Option.defaultValue 0
          let prevIdx = (currentIdx - 1 + count) % count
          let sid = model.Sessions.Sessions.[prevIdx].Id
          let newEditor = { model.Editor with SelectedSessionIndex = Some prevIdx }
          { model with Editor = newEditor },
          [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch (SessionId.value sid))]
      | EditorAction.ClearOutput ->
        // Clear active session's buffer; new store instance for ref inequality (triggers render)
        let activeId =
          match model.Sessions.ActiveSessionId with
          | ActiveSession.Viewing sid -> SessionId.value sid
          | ActiveSession.AwaitingSession -> ""
        model.RecentOutput.Clear(activeId)
        { model with RecentOutput = SessionOutputStore.empty },
        []
      | EditorAction.ConfigureWarmupAutoOpen ->
        let workingDir = resolveConfigWorkingDirectory model
        model,
        [SageFsEffect.Editor (EditorEffect.RequestConfigureWarmupAutoOpen workingDir)]
      | EditorAction.SessionNavDown | EditorAction.SessionSetIndex _ ->
        let newEditor, effects = EditorUpdate.update action model.Editor
        // Clamp index to session count
        let clamped =
          match newEditor.SelectedSessionIndex with
          | Some idx -> { newEditor with SelectedSessionIndex = Some (min idx (max 0 (model.Sessions.Sessions.Length - 1))) }
          | None -> newEditor
        { model with Editor = clamped },
        effects |> List.map SageFsEffect.Editor
      | EditorAction.CreateSession _ when model.CreatingSession ->
        // Prevent duplicate session creation while one is in progress
        model, []
      | _ ->
        let newEditor, effects = EditorUpdate.update action model.Editor
        let isCreating =
          effects |> List.exists (function EditorEffect.RequestSessionCreate _ -> true | _ -> false)
        let creatingSession = model.CreatingSession || isCreating
        let updatedModel =
          match obj.ReferenceEquals(newEditor, model.Editor) && creatingSession = model.CreatingSession with
          | true -> model
          | false ->
            { model with
                Editor = newEditor
                CreatingSession = creatingSession }
        updatedModel,
        effects |> List.map SageFsEffect.Editor

    | SageFsMsg.BufferedTestResults buffered ->
      applyBufferedTestResults buffered.SessionId buffered.Batches model

    | SageFsMsg.Event event ->
      match event with
      | TuiEvent.EvalCompleted (sid, output, diags) ->
        let line = {
          Kind = OutputKind.Result
          Text = output
          Timestamp = DateTime.UtcNow
          SessionId = sid
        }
        { model with
            RecentOutput = SageFsModel.addOutputLine line model.RecentOutput
            Diagnostics = model.Diagnostics |> Map.add sid diags; EvalsFinished = EvalTally.record sid model.EvalsFinished }, []

      | TuiEvent.EvalFailed (sid, error) ->
        let line = {
          Kind = OutputKind.Failure
          Text = error
          Timestamp = DateTime.UtcNow
          SessionId = sid
        }
        let clearCreating = error.Contains "Create failed:"
        { model with
            RecentOutput = SageFsModel.addOutputLine line model.RecentOutput
            CreatingSession = (match clearCreating with | true -> false | false -> model.CreatingSession); EvalsFinished = EvalTally.record sid model.EvalsFinished }, []

      | TuiEvent.EvalStarted (sid, code) ->
        let line = {
          Kind = OutputKind.Info
          Text = code
          Timestamp = DateTime.UtcNow
          SessionId = sid
        }
        { model with RecentOutput = SageFsModel.addOutputLine line model.RecentOutput }, []

      | TuiEvent.EvalCancelled sid ->
        let line = {
          Kind = OutputKind.Info
          Text = "Eval cancelled"
          Timestamp = DateTime.UtcNow
          SessionId = sid
        }
        { model with RecentOutput = SageFsModel.addOutputLine line model.RecentOutput; EvalsFinished = EvalTally.record sid model.EvalsFinished }, []

      | TuiEvent.OutputEmitted line ->
        { model with RecentOutput = SageFsModel.addOutputLine line model.RecentOutput }, []

      | TuiEvent.CompletionReady items ->
        let menu = {
          Items = items
          SelectedIndex = 0
          FilterText = ""
        }
        { model with
            Editor = { model.Editor with CompletionMenu = Some menu } }, []

      | TuiEvent.DiagnosticsUpdated (sid, diags) ->
        { model with Diagnostics = model.Diagnostics |> Map.add sid diags }, []

      | TuiEvent.SessionCreated snap ->
        let isFirst = model.Sessions.ActiveSessionId = ActiveSession.AwaitingSession
        let existing = model.Sessions.Sessions |> List.exists (fun s -> s.Id = snap.Id)
        let sessions =
          match existing with
          | true ->
            model.Sessions.Sessions |> List.map (fun s -> match s.Id = snap.Id with | true -> snap | false -> s)
          | false ->
            snap :: model.Sessions.Sessions
        let watcherEffects =
          match model.LiveTesting.TestState.Activation with
          | Features.LiveTesting.LiveTestingActivation.Active ->
            [ SageFsEffect.TestCycle (Features.LiveTesting.TestCycleEffect.RegisterFileWatcher
                (SessionId.value snap.Id, snap.WorkingDirectory)) ]
          | _ -> []
        { model with
            CreatingSession = false
            Sessions = {
              model.Sessions with
                Sessions = sessions
                ActiveSessionId =
                  match isFirst with
                  | true -> ActiveSession.Viewing snap.Id
                  | false -> model.Sessions.ActiveSessionId } }, watcherEffects

      | TuiEvent.SessionsRefreshed snaps ->
        let activeId = model.Sessions.ActiveSessionId
        let merged = snaps
        let activeId' =
          match activeId with
          | ActiveSession.AwaitingSession when not (List.isEmpty merged) ->
            ActiveSession.Viewing merged.Head.Id
          | _ -> activeId
        // Short-circuit: if sessions and active ID are unchanged, return same model (no render)
        match merged = model.Sessions.Sessions && activeId' = activeId with
        | true -> model, []
        | false ->
          { model with
              Sessions = {
                model.Sessions with
                  Sessions = merged
                  ActiveSessionId = activeId' } }, []

      | TuiEvent.SessionStatusChanged (sessionId, status) ->
        let priorSession =
          model.Sessions.Sessions
          |> List.tryFind (fun s -> SessionId.value s.Id = sessionId)

        let isWatcherEligible sessionStatus =
          match sessionStatus with
          | SessionDisplayStatus.Running
          | SessionDisplayStatus.Idle -> true
          | _ -> false

        let watcherEffects =
          match model.LiveTesting.TestState.Activation, priorSession with
          | LiveTestingActivation.Active, Some session when not (isWatcherEligible session.Status) && isWatcherEligible status ->
              [ SageFsEffect.TestCycle (
                  Features.LiveTesting.TestCycleEffect.RegisterFileWatcher (
                    sessionId,
                    session.WorkingDirectory)) ]
          // Phase 3.6 dispose/recreate lifecycle: a hard reset emits
          // SessionStatusChanged(Running -> Restarting); the worker is being
          // torn down, so the watcher claim must be released or the fresh
          // worker's registration doubles up on a stale FileSystemWatcher.
          // Register-on-Running (above) recreates the claim after restart.
          | LiveTestingActivation.Active, Some session when isWatcherEligible session.Status && not (isWatcherEligible status) ->
              [ SageFsEffect.TestCycle (
                  Features.LiveTesting.TestCycleEffect.DisposeFileWatcher (
                    sessionId,
                    session.WorkingDirectory)) ]
          | _ -> []

        { model with
            Sessions = {
              model.Sessions with
                Sessions =
                  model.Sessions.Sessions
                  |> List.map (fun s ->
                    match SessionId.value s.Id = sessionId with
                    | true -> { s with Status = status }
                    | false -> s) } }, watcherEffects

      | TuiEvent.SessionSwitched (fromIdStr, toIdStr) ->
        match SessionId.validate toIdStr with
        | Error _ -> model, []
        | Ok toId ->
        let liveTestingSwapped =
          switchActiveLiveTestingState fromIdStr toIdStr model

        { liveTestingSwapped with
            SessionContext = None
            Sessions = {
              liveTestingSwapped.Sessions with
                ActiveSessionId = ActiveSession.Viewing toId } }, []

      | TuiEvent.SessionStopped sessionId ->
        model.RecentOutput.Remove(sessionId)
        let stoppedSession =
          model.Sessions.Sessions |> List.tryFind (fun s -> SessionId.value s.Id = sessionId)
        let remaining =
          model.Sessions.Sessions
          |> List.filter (fun s -> SessionId.value s.Id <> sessionId)
        let wasActive =
          match model.Sessions.ActiveSessionId with
          | ActiveSession.Viewing id -> SessionId.value id = sessionId
          | ActiveSession.AwaitingSession -> false
        let newActive =
          match wasActive with
          | true ->
            remaining
            |> List.tryHead
            |> Option.map (fun s -> ActiveSession.Viewing s.Id)
            |> Option.defaultValue ActiveSession.AwaitingSession
          | false -> model.Sessions.ActiveSessionId
        let watcherEffects =
          match stoppedSession with
          | Some s ->
            [ SageFsEffect.TestCycle (Features.LiveTesting.TestCycleEffect.DisposeFileWatcher
                (sessionId, s.WorkingDirectory)) ]
          | None -> []
        { model with
            Sessions = {
              model.Sessions with
                Sessions = remaining
                ActiveSessionId = newActive }
            // Background cycles are owned per-session, so removing the map entry
            // fully discards the stopped session's tests. When the stopped session
            // instead owned `model.LiveTesting` (Primary), its stale cycle is left
            // as-is — it is superseded the moment another session's own routing
            // (`tryResolveLiveTestingTarget`) claims Primary next, same as before.
            PerSessionLiveTesting = model.PerSessionLiveTesting |> Map.remove sessionId
            Diagnostics = model.Diagnostics |> Map.remove sessionId }, watcherEffects

      | TuiEvent.SessionStale (sessionId, _) ->
        { model with
            Sessions = {
              model.Sessions with
                Sessions =
                  model.Sessions.Sessions
                  |> List.map (fun s ->
                    match SessionId.value s.Id = sessionId with
                    | true -> { s with Status = SessionDisplayStatus.Idle }
                    | false -> s) } }, []

      | TuiEvent.FileChanged _ -> model, []

      | TuiEvent.FileReloaded (path, _, result) ->
        let activeId = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.map SessionId.value |> Option.defaultValue ""
        let line =
          match result with
          | Ok msg ->
            { Kind = OutputKind.Info
              Text = sprintf "Reloaded %s: %s" path msg
              Timestamp = DateTime.UtcNow
              SessionId = activeId }
          | Error err ->
            { Kind = OutputKind.Failure
              Text = sprintf "Reload failed %s: %s" path err
              Timestamp = DateTime.UtcNow
              SessionId = activeId }
        { model with RecentOutput = SageFsModel.addOutputLine line model.RecentOutput }, []

      | TuiEvent.WarmupProgress(step, total, msg) ->
        let activeId = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.map SessionId.value |> Option.defaultValue ""
        let line = {
          Kind = OutputKind.Info
          Text = sprintf "⏳ [%d/%d] %s" step total msg
          Timestamp = DateTime.UtcNow
          SessionId = activeId }
        { model with RecentOutput = SageFsModel.addOutputLine line model.RecentOutput }, []

      | TuiEvent.WarmupCompleted (_, failures) ->
        let activeId = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.map SessionId.value |> Option.defaultValue ""
        match failures.IsEmpty with
        | true ->
          let line = {
            Kind = OutputKind.Info
            Text = "Warmup complete"
            Timestamp = DateTime.UtcNow
            SessionId = activeId
          }
          { model with RecentOutput = SageFsModel.addOutputLine line model.RecentOutput }, []
        | false ->
          let lines =
            failures |> List.map (fun f ->
              { Kind = OutputKind.Failure
                Text = sprintf "Warmup failure: %s" f
                Timestamp = DateTime.UtcNow
                SessionId = activeId })
          { model with RecentOutput = SageFsModel.addOutput lines model.RecentOutput }, []

      | TuiEvent.WarmupContextUpdated ctx ->
        // Short-circuit: if warmup context is identical for every Elm-observable
        // hot-path field, skip update and avoid rerender/remap churn.
        match model.SessionContext with
        | Some existing when SessionContext.equivalentForElmHotPath existing ctx -> model, []
        | _ ->
          // Only show banner on first arrival; repeated dispatches (from periodic
          // RequestSessionList) just update context silently to avoid flooding output.
          let bannerLines =
            match model.SessionContext with
            | None -> WarmupBanner.toOutputLines ctx
            | Some _ -> []
          // Re-map any ReflectionOnly tests now that we have source file paths
          let sourceFiles = ctx.FileStatuses |> List.map (fun f -> f.Path) |> Array.ofList
          let lt =
            match Array.isEmpty sourceFiles with
            | true -> model.LiveTesting
            | false ->
              recomputeStatuses model.LiveTesting (fun s ->
                let remapped = Features.LiveTesting.SourceMapping.mapFromProjectFiles sourceFiles s.DiscoveredTests
                { s with DiscoveredTests = remapped })
          let output =
            match bannerLines with
            | [] -> model.RecentOutput
            | lines -> SageFsModel.addOutput (List.rev lines) model.RecentOutput
          { model with
              SessionContext = Some ctx
              LiveTesting = lt
              RecentOutput = output }, []

      // ── Live testing events ──
      | TuiEvent.TestLocationsDetected (_, locations) ->
        let state = model.LiveTesting.TestState
        let merged =
          match Array.isEmpty state.DiscoveredTests with
          | true -> state.DiscoveredTests
          | false -> Features.LiveTesting.SourceMapping.mergeSourceLocations locations state.DiscoveredTests
        match state.SourceLocations = locations && state.DiscoveredTests = merged with
        | true -> model, []
        | false ->
          let lt = recomputeStatuses model.LiveTesting (fun s ->
            { s with SourceLocations = locations; DiscoveredTests = merged })
          { model with LiveTesting = lt }, []

      | TuiEvent.TestsDiscovered (sessionId, tests) ->
        // Discovery is routed to THIS session's own cycle (Primary when it's the
        // active session, its own Background cycle otherwise — see
        // `tryUpdateLiveTestingState`/`tryResolveLiveTestingTarget`). Two sessions
        // never share a `LiveTestState` any more, so two checkouts of the same repo
        // producing identical `TestId`s can no longer clobber each other's
        // attribution (the bug `TestSessionMap` used to paper over).
        let model', outcome =
          tryUpdateLiveTestingState (Some sessionId) (fun cycle ->
            let state = cycle.TestState
            // This cycle belongs wholly to `sessionId` (see above), so a fresh
            // discovery pass WHOLESALE REPLACES its prior discovered tests —
            // matching the old TestSessionMap-era code's net effect (which
            // dropped every entry attributed to `sessionId` before merging in
            // the new ones). `mergeDiscoveredTests` itself only unions by
            // TestId and never expires anything, so passing `[||]` as
            // "existing" is what makes a renamed/removed test actually
            // disappear instead of accumulating forever.
            let disc = Features.LiveTesting.LiveTesting.mergeDiscoveredTests [||] tests
            let withSourceMap =
              match Array.isEmpty state.SourceLocations with
              | true ->
                // No tree-sitter yet — map tests to files using module name → file name heuristic
                let sourceFiles =
                  match model.SessionContext with
                  | Some ctx -> ctx.FileStatuses |> List.map (fun f -> f.Path) |> Array.ofList
                  | None -> [||]
                Features.LiveTesting.SourceMapping.mapFromProjectFiles sourceFiles disc
              | false -> Features.LiveTesting.SourceMapping.mergeSourceLocations state.SourceLocations disc
            let sessionDiscovery =
              Map.add sessionId Features.LiveTesting.DiscoveryProgress.Completed state.SessionDiscovery
            let locs =
              let emptyGraph : Features.CellDependencyGraph.CellGraph = { Cells = Map.empty; Edges = [] }
              Features.TestSourceResolver.resolveTestLocations emptyGraph (Array.toList tests)
            // Zero-test discovery defect: completing discovery with zero tests must
            // be observable. When discovery has NEVER completed (LastDiscoveryTime
            // still MinValue), stamping it now is a meaningful change even if the
            // test lists are both empty — it flips the derived discovery state from
            // Discovering to ReadyZeroTests. Without this, a zero-test discovery
            // against an empty state short-circuits and no client can ever learn
            // that discovery finished.
            let firstCompletion = state.LastDiscoveryTime = System.DateTimeOffset.MinValue
            let meaningfulChange =
              state.DiscoveredTests <> withSourceMap
              || state.SessionDiscovery <> sessionDiscovery
              || model.ResolvedSourceLocations <> locs
              || (firstCompletion && state.Activation = Features.LiveTesting.LiveTestingActivation.Active)
            match meaningfulChange with
            | false -> cycle, None
            | true ->
              let cycle' = recomputeStatuses cycle (fun s ->
                { s with
                    DiscoveredTests = withSourceMap
                    LastDiscoveryTime = System.DateTimeOffset.UtcNow
                    DiscoveryGeneration = s.DiscoveryGeneration + 1L
                    SessionDiscovery = sessionDiscovery })
              let effects =
                match cycle'.TestState.Activation = Features.LiveTesting.LiveTestingActivation.Active
                      && not (Array.isEmpty tests) with
                | true ->
                  // Only trigger execution for the INCOMING session's tests, not all discovered.
                  // Other sessions' tests belong to different workers and would return NotRun.
                  let incomingIds = tests |> Array.map (fun tc -> tc.Id)
                  Features.LiveTesting.LiveTestCycleState.triggerExecutionForAffected
                    incomingIds Features.LiveTesting.RunTrigger.FileSave (Some sessionId) cycle'
                  |> List.map SageFsEffect.TestCycle
                | false -> []
              cycle', Some (effects, locs)) model
        match outcome with
        | Some (Some (effects, locs)) -> { model' with ResolvedSourceLocations = locs }, effects
        | Some None -> model, []
        | None -> model, []

      | TuiEvent.LiveDiscoveryMerged (sessionId, tests) ->
        // Same merge as `TestsDiscovered` immediately above (source mapping,
        // DiscoveryGeneration bump, zero-test completion) — deliberately
        // WITHOUT its auto-run-every-discovered-test effect, which exists
        // for the one-time activation baseline, not the per-keystroke
        // eval-then-affected loop (Brief 4 — see the TuiEvent doc comment).
        let model', outcome =
          tryUpdateLiveTestingState (Some sessionId) (fun cycle ->
            let state = cycle.TestState
            let disc = Features.LiveTesting.LiveTesting.mergeDiscoveredTests [||] tests
            let withSourceMap =
              match Array.isEmpty state.SourceLocations with
              | true ->
                let sourceFiles =
                  match model.SessionContext with
                  | Some ctx -> ctx.FileStatuses |> List.map (fun f -> f.Path) |> Array.ofList
                  | None -> [||]
                Features.LiveTesting.SourceMapping.mapFromProjectFiles sourceFiles disc
              | false -> Features.LiveTesting.SourceMapping.mergeSourceLocations state.SourceLocations disc
            let sessionDiscovery =
              Map.add sessionId Features.LiveTesting.DiscoveryProgress.Completed state.SessionDiscovery
            let locs =
              let emptyGraph : Features.CellDependencyGraph.CellGraph = { Cells = Map.empty; Edges = [] }
              Features.TestSourceResolver.resolveTestLocations emptyGraph (Array.toList tests)
            let firstCompletion = state.LastDiscoveryTime = System.DateTimeOffset.MinValue
            let meaningfulChange =
              state.DiscoveredTests <> withSourceMap
              || state.SessionDiscovery <> sessionDiscovery
              || model.ResolvedSourceLocations <> locs
              || (firstCompletion && state.Activation = Features.LiveTesting.LiveTestingActivation.Active)
            match meaningfulChange with
            | false -> cycle, None
            | true ->
              let cycle' = recomputeStatuses cycle (fun s ->
                { s with
                    DiscoveredTests = withSourceMap
                    LastDiscoveryTime = System.DateTimeOffset.UtcNow
                    DiscoveryGeneration = s.DiscoveryGeneration + 1L
                    SessionDiscovery = sessionDiscovery })
              cycle', Some locs) model
        match outcome with
        | Some (Some locs) -> { model' with ResolvedSourceLocations = locs }, []
        | Some None -> model, []
        | None -> model, []

      | TuiEvent.TestSourceLocations locations ->
        { model with ResolvedSourceLocations = locations }, []

      | TuiEvent.TestRunStarted (testIds, sessionId) ->
        let model', _ =
          tryUpdateLiveTestingState sessionId (fun cycle ->
            let nextAffected = Set.ofArray testIds
            let changedIds = Set.union cycle.TestState.AffectedTests nextAffected
            let cycle' =
              refreshStatusesForChangedIds cycle changedIds (fun priorState ->
                let phase, gen = TestRunPhase.startRun priorState.LastGeneration
                let phases, displaced =
                  match sessionId with
                  | Some sid ->
                    priorState.RunPhases |> Map.add sid phase,
                    Features.LiveTesting.RequestedRuns.supersede sid gen priorState
                  | None -> priorState.RunPhases, priorState
                { displaced with LastGeneration = gen; AffectedTests = nextAffected; RunPhases = phases })
            cycle', ()) model
        model', []

      | TuiEvent.TestRunStartedAt (testIds, sessionId, generation) ->
        let model', _ =
          tryUpdateLiveTestingState (Some sessionId) (fun cycle ->
            let nextAffected = Set.ofArray testIds
            let changedIds = Set.union cycle.TestState.AffectedTests nextAffected
            let cycle' =
              refreshStatusesForChangedIds cycle changedIds (fun priorState ->
                { Features.LiveTesting.RequestedRuns.started sessionId generation priorState with
                    AffectedTests = nextAffected })
            cycle', ()) model
        model', []

      | TuiEvent.TestResultsBatch (sessionId, results) ->
        applyBufferedTestResults sessionId [ results ] model

      | TuiEvent.TestRunCompleted sessionId ->
        let model', replayEffects =
          tryUpdateLiveTestingState sessionId (fun cycle ->
            let priorState =
              match sessionId with
              | Some sid -> Features.LiveTesting.RequestedRuns.completed sid cycle.TestState
              | None -> cycle.TestState
            let changedIds = priorState.AffectedTests
            let updatedState =
              let phases =
                match sessionId with
                | Some sid -> priorState.RunPhases |> Map.add sid Features.LiveTesting.TestRunPhase.Idle
                | None -> priorState.RunPhases
              { priorState with AffectedTests = Set.empty; RunPhases = phases }
            let cycle' =
              match Array.isEmpty priorState.StatusIndex.Entries with
              | true ->
                finalizeLiveTestingState
                  LiveTestingStatusRefresh.Recompute
                  cycle
                  updatedState
                |> fst
              | false ->
                let patchedState, changedEntries =
                  Features.LiveTesting.LiveTesting.patchStatusEntriesForChangedIds
                    priorState
                    updatedState
                    changedIds
                finalizeLiveTestingState
                  (LiveTestingStatusRefresh.PatchChangedEntries changedEntries)
                  cycle
                  patchedState
                |> fst
            let replayEffects, cycle'' =
              Features.LiveTesting.LiveTestCycleState.promoteQueuedRebuild sessionId cycle'
            cycle'', replayEffects) model
        let summary =
          model'.PendingRunSummary
          |> PendingRunSummary.toOutputLine
        { model' with
            PendingRunSummary = PendingRunSummary.empty
            RecentOutput = SageFsModel.addOutputLine summary model'.RecentOutput },
        replayEffects
        |> Option.defaultValue []
        |> List.map SageFsEffect.TestCycle

      | TuiEvent.LiveTestingEnabled ->
        let lt =
          refreshStatusesKeepingEntries model.LiveTesting (fun s ->
            { s with Activation = Features.LiveTesting.LiveTestingActivation.Active })
        { model with LiveTesting = lt }, []

      | TuiEvent.LiveTestingDisabled ->
        let lt =
          refreshStatusesKeepingEntries model.LiveTesting (fun s ->
            { s with Activation = Features.LiveTesting.LiveTestingActivation.Inactive })
        { model with LiveTesting = lt }, []

      | TuiEvent.AffectedTestsComputed (testIds, changedSymbolNames) ->
        // Augment the worker's NAME-matched set with the live FCS symbol-use
        // graph so a test in another module that USES a changed symbol is not
        // missed (roast-7 §4 follow-up). Fail-safe: augmentAffectedWithGraph
        // returns a superset — it can only add correct tests, never drop one.
        let testIds =
          Features.LiveTesting.LiveTestingHook.augmentAffectedWithGraph
            testIds changedSymbolNames model.LiveTesting.DepGraph
        let changedIds = Set.ofArray testIds
        let lt =
          refreshStatusesForChangedIds model.LiveTesting changedIds (fun s ->
            { s with AffectedTests = changedIds })
        // Primary belongs wholly to one session (see `LiveTestState.ownerSessionId`).
        let targetSession = Features.LiveTesting.LiveTestState.ownerSessionId lt.TestState
        // Quarantine gate: a test QuarantineLogic.evaluate quarantined (see
        // `evaluateQuarantineForBatch`) is excluded from the next selection —
        // closing the gap where QuarantineLogic had zero non-test callers.
        // Never silent: excluded ids are recorded on the cycle's LastDecision
        // (the existing suppressed/deferred reporting channel used for
        // policy-deferred tests) and echoed to the session output.
        let runIds, quarantinedIds =
          testIds
          |> Array.partition (fun id -> not (Features.LiveTesting.QuarantineLogic.isQuarantined id model.QuarantinedTests))
        let fullNameOf id =
          lt.TestState.DiscoveredTests
          |> Array.tryFind (fun tc -> tc.Id = id)
          |> Option.map (fun tc -> tc.FullName)
        let effects =
          Features.LiveTesting.LiveTestCycleState.triggerExecutionForAffected
            runIds Features.LiveTesting.RunTrigger.FileSave targetSession lt
          |> List.map SageFsEffect.TestCycle
        match Array.isEmpty quarantinedIds with
        | true -> { model with LiveTesting = lt }, effects
        | false ->
          let quarantinedNames = quarantinedIds |> Array.choose fullNameOf
          let decision =
            Features.LiveTesting.LiveTestingDecision.fromSelection
              (Features.LiveTesting.RerunCause.FileSaved "")
              Features.LiveTesting.SelectionPrecision.SuppressedByPolicy
              changedSymbolNames
              (runIds |> Array.choose fullNameOf)
              quarantinedNames
              (sprintf "%d test(s) quarantined for flakiness — excluded from this run." quarantinedNames.Length)
          let lt' = { lt with TestState = { lt.TestState with LastDecision = Some decision } }
          let quarantineLine : OutputLine =
            { Kind = OutputKind.System
              Text = sprintf "🔒 Quarantined (flaky), skipped: %s" (String.concat ", " quarantinedNames)
              Timestamp = DateTime.UtcNow
              SessionId = targetSession |> Option.defaultValue "" }
          { model with
              LiveTesting = lt'
              RecentOutput = SageFsModel.addOutputLine quarantineLine model.RecentOutput },
          effects

      | TuiEvent.RunTestsRequested (requestedSession, tests, requestId) ->
        // Routed to its own cycle (Primary/Background — mirrors
        // CoverageBitmapCollected/TestRunStarted). `None` -> Primary as before.
        let testIds = tests |> Array.map (fun t -> t.Id)
        let changedIds = Set.ofArray testIds
        let model', effects =
          tryUpdateLiveTestingState requestedSession (fun cycle ->
            // Explicit session id wins; else the cycle's own owner, as before.
            let targetSession =
              match requestedSession with
              | Some _ -> requestedSession
              | None -> Features.LiveTesting.LiveTestState.ownerSessionId cycle.TestState
            let request (sessionMaps: Features.LiveTesting.InstrumentationMap array) : Features.LiveTesting.TestRunRequest =
              { Tests = tests
                Trigger = Features.LiveTesting.RunTrigger.ExplicitRun
                TreeSitterElapsed = System.TimeSpan.Zero
                FcsElapsed = System.TimeSpan.Zero
                SessionId = targetSession
                InstrumentationMaps = sessionMaps }
            let mapsFor (lt: Features.LiveTesting.LiveTestCycleState) =
              match targetSession |> Option.bind (fun s -> Map.tryFind s lt.InstrumentationMaps) with
              | Some maps -> maps
              | None -> lt.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
            match targetSession, Array.isEmpty tests with
            | _, true -> cycle, []
            | Some sid, false ->
              // The run's generation is allocated ONCE, here, and carried by the
              // run (RunRequestedTests -> TestRunStartedAt). The phase is not
              // set until the worker actually starts it: until then another
              // run's results may still be arriving and must not be stamped
              // with this run's generation.
              let allocated = ref (Features.LiveTesting.RunGeneration 0)
              let lt =
                refreshStatusesForChangedIds cycle changedIds (fun s ->
                  let s', gen = Features.LiveTesting.RequestedRuns.request requestId sid s
                  allocated.Value <- gen
                  { s' with AffectedTests = changedIds })
              lt,
              [ Features.LiveTesting.TestCycleEffect.RunRequestedTests (request (mapsFor lt), allocated.Value)
                |> SageFsEffect.TestCycle ]
            | None, false ->
              // No session to own the run's identity: the legacy path, bumped
              // when the worker starts it.
              let lt =
                refreshStatusesForChangedIds cycle changedIds (fun s -> { s with AffectedTests = changedIds })
              lt,
              [ Features.LiveTesting.TestCycleEffect.RunAffectedTests (request (mapsFor lt))
                |> SageFsEffect.TestCycle ]) model
        model', (effects |> Option.defaultValue [])

      | TuiEvent.CoverageUpdated coverage ->
        let lt = model.LiveTesting
        // Aggregate per file+line: multiple sequence points on same line → single annotation
        let annotations : Features.LiveTesting.CoverageAnnotation array =
          coverage.Slots
          |> Array.mapi (fun i slot -> slot, coverage.Hits.[i])
          |> Array.groupBy (fun (slot, _) -> slot.File, slot.Line)
          |> Array.map (fun ((file, line), slots) ->
            let total = slots.Length
            let covered = slots |> Array.filter snd |> Array.length
            let status =
              match covered with
              | 0 ->
                Features.LiveTesting.CoverageStatus.NotCovered
              | c when c = total ->
                Features.LiveTesting.CoverageStatus.Covered (total, Features.LiveTesting.CoverageHealth.AllPassing)
              | _ ->
                Features.LiveTesting.CoverageStatus.Covered (covered, Features.LiveTesting.CoverageHealth.SomeFailing)
            { Symbol = sprintf "%s:%d" file line
              FilePath = file
              DefinitionLine = line
              Status = status
              BranchCoverage = Features.LiveTesting.BranchCoverage.Unknown })
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with CoverageAnnotations = annotations } } }, []

      | TuiEvent.CoverageBitmapCollected (sessionId, testIds, bitmap) ->
        Instrumentation.coverageBitmapsCollected.Add(1L)
        let model', _ =
          tryUpdateLiveTestingState sessionId (fun cycle ->
            let bitmaps =
              testIds |> Array.fold (fun acc tid -> Map.add tid bitmap acc) cycle.TestState.TestCoverageBitmaps
            { cycle with TestState = { cycle.TestState with TestCoverageBitmaps = bitmaps } }, ()) model
        model', []

      | TuiEvent.RunPolicyChanged (category, policy) ->
        let lt = recomputeStatuses model.LiveTesting (fun s -> { s with RunPolicies = Map.add category policy s.RunPolicies })
        { model with LiveTesting = lt }, []

      | TuiEvent.InstrumentationMapsReady (sessionId, maps) ->
        Instrumentation.coverageMapsReceived.Add(1L)
        let totalProbes = maps |> Array.sumBy (fun m -> m.TotalProbes) |> int64
        Instrumentation.coverageProbesTotal.Add(totalProbes)
        let lt = model.LiveTesting
        { model with LiveTesting = { lt with InstrumentationMaps = Map.add sessionId maps lt.InstrumentationMaps } }, []

      | TuiEvent.TestDiscoveryFailed (sessionId, reason) ->
        let lt = recomputeStatuses model.LiveTesting (fun s ->
          { s with
              SessionDiscovery =
                Map.add sessionId (Features.LiveTesting.DiscoveryProgress.Failed reason) s.SessionDiscovery })
        { model with LiveTesting = lt }, []

      | TuiEvent.ProvidersDetected providers ->
        let lt = model.LiveTesting
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with DetectedProviders = providers } } }, []

      | TuiEvent.TestCycleTimingRecorded timing ->
        { model with
            LiveTesting = { model.LiveTesting with LastTiming = Some timing } }, []

      | TuiEvent.AssemblyLoadFailed errors ->
        let lt = recomputeStatuses model.LiveTesting (fun s ->
          { s with AssemblyLoadErrors = errors })
        { model with LiveTesting = lt }, []

    | SageFsMsg.CycleTheme ->
      let name, theme = ThemePresets.cycleNext model.Theme
      { model with Theme = theme; ThemeName = name }, []

    | SageFsMsg.EnableLiveTesting ->
      match model.LiveTesting.TestState.Activation = Features.LiveTesting.LiveTestingActivation.Active with
      | true ->
        model, []
      | false ->
        let pendingDiscoverySessions =
          model.Sessions.Sessions
          |> List.choose (fun session ->
            match session.Status with
            | SessionDisplayStatus.Running
            | SessionDisplayStatus.Idle -> Some (SessionId.value session.Id)
            | _ -> None)
          |> Set.ofList
        // Discovery is requested only when no tests are known yet; otherwise they just re-run.
        let markDiscovering discovery =
          match Array.isEmpty model.LiveTesting.TestState.DiscoveredTests with
          | true ->
            pendingDiscoverySessions
            |> Set.fold (fun progress sessionId -> Map.add sessionId Features.LiveTesting.DiscoveryProgress.InProgress progress) discovery
          | false -> discovery
        let lt =
          refreshStatusesKeepingEntries model.LiveTesting (fun s ->
            { s with
                Activation = Features.LiveTesting.LiveTestingActivation.Active
                SessionDiscovery = markDiscovering s.SessionDiscovery })
        let effects =
          match Array.isEmpty lt.TestState.DiscoveredTests with
          | true -> [SageFsEffect.TestCycle Features.LiveTesting.TestCycleEffect.RequestInitialDiscovery]
          | false ->
            // Primary belongs wholly to one session (see `LiveTestState.ownerSessionId`).
            let targetSession = Features.LiveTesting.LiveTestState.ownerSessionId lt.TestState
            let allIds = lt.TestState.DiscoveredTests |> Array.map (fun tc -> tc.Id)
            Features.LiveTesting.LiveTestCycleState.triggerExecutionForAffected
              allIds Features.LiveTesting.RunTrigger.ExplicitRun targetSession lt
            |> List.map SageFsEffect.TestCycle
        let watcherEffects =
          model.Sessions.Sessions
          |> List.choose (fun session ->
            match session.Status with
            | SessionDisplayStatus.Running
            | SessionDisplayStatus.Idle ->
              Some (SageFsEffect.TestCycle (Features.LiveTesting.TestCycleEffect.RegisterFileWatcher
                (SessionId.value session.Id, session.WorkingDirectory)))
            | _ -> None)
        { model with LiveTesting = lt }, effects @ watcherEffects

    | SageFsMsg.DisableLiveTesting ->
      match model.LiveTesting.TestState.Activation = Features.LiveTesting.LiveTestingActivation.Inactive with
      | true ->
        model, []
      | false ->
        let lt =
          refreshStatusesKeepingEntries model.LiveTesting (fun s ->
            { s with Activation = Features.LiveTesting.LiveTestingActivation.Inactive })
        // Dispose the watcher claims that EnableLiveTesting registered for
        // every running/stale session. Without this, each enable/disable
        // cycle leaks a FileSystemWatcher per session (OS handles + stale
        // FileReloaded attribution after re-enable).
        let watcherEffects =
          model.Sessions.Sessions
          |> List.choose (fun session ->
            match session.Status with
            | SessionDisplayStatus.Running
            | SessionDisplayStatus.Idle ->
              Some (SageFsEffect.TestCycle (Features.LiveTesting.TestCycleEffect.DisposeFileWatcher
                (SessionId.value session.Id, session.WorkingDirectory)))
            | _ -> None)
        { model with LiveTesting = lt }, watcherEffects

    | SageFsMsg.EnableLiveTestingForSession sessionId ->
      // Mirrors `EnableLiveTesting`, targeting this session's own cycle.
      let target = SageFsModel.resolveOrCreateLiveTestingTarget (Some sessionId) model
      let cycle = SageFsModel.cycleFor target model
      match cycle.TestState.Activation = Features.LiveTesting.LiveTestingActivation.Active with
      | true ->
        model, []
      | false ->
        let sessionInfo =
          model.Sessions.Sessions |> List.tryFind (fun s -> SessionId.value s.Id = sessionId)
        let markDiscovering discovery =
          match Array.isEmpty cycle.TestState.DiscoveredTests with
          | true -> discovery |> Map.add sessionId Features.LiveTesting.DiscoveryProgress.InProgress
          | false -> discovery
        let cycle' =
          refreshStatusesKeepingEntries cycle (fun s ->
            { s with
                Activation = Features.LiveTesting.LiveTestingActivation.Active
                SessionDiscovery = markDiscovering s.SessionDiscovery })
        let effects =
          match Array.isEmpty cycle'.TestState.DiscoveredTests with
          | true ->
            // Rare: warmup discovery hasn't landed yet. Same broadcast
            // request `EnableLiveTesting` uses — harmless for other sessions.
            [SageFsEffect.TestCycle Features.LiveTesting.TestCycleEffect.RequestInitialDiscovery]
          | false ->
            let allIds = cycle'.TestState.DiscoveredTests |> Array.map (fun tc -> tc.Id)
            Features.LiveTesting.LiveTestCycleState.triggerExecutionForAffected
              allIds Features.LiveTesting.RunTrigger.ExplicitRun (Some sessionId) cycle'
            |> List.map SageFsEffect.TestCycle
        let watcherEffects =
          match sessionInfo with
          | Some session ->
            [ SageFsEffect.TestCycle (Features.LiveTesting.TestCycleEffect.RegisterFileWatcher
                (sessionId, session.WorkingDirectory)) ]
          | None -> []
        setLiveTestingState target cycle' model, effects @ watcherEffects

    | SageFsMsg.DisableLiveTestingForSession sessionId ->
      let target = SageFsModel.resolveOrCreateLiveTestingTarget (Some sessionId) model
      let cycle = SageFsModel.cycleFor target model
      match cycle.TestState.Activation = Features.LiveTesting.LiveTestingActivation.Inactive with
      | true ->
        model, []
      | false ->
        let cycle' =
          refreshStatusesKeepingEntries cycle (fun s ->
            { s with Activation = Features.LiveTesting.LiveTestingActivation.Inactive })
        let watcherEffects =
          model.Sessions.Sessions
          |> List.tryFind (fun s -> SessionId.value s.Id = sessionId)
          |> Option.map (fun session ->
            SageFsEffect.TestCycle (Features.LiveTesting.TestCycleEffect.DisposeFileWatcher
              (sessionId, session.WorkingDirectory)))
          |> Option.toList
        setLiveTestingState target cycle' model, watcherEffects

    | SageFsMsg.CycleRunPolicy ->
      let lt = model.LiveTesting
      let nextPolicy (p: Features.LiveTesting.RunPolicy) =
        match p with
        | Features.LiveTesting.RunPolicy.OnEveryChange -> Features.LiveTesting.RunPolicy.OnSaveOnly
        | Features.LiveTesting.RunPolicy.OnSaveOnly -> Features.LiveTesting.RunPolicy.OnDemand
        | Features.LiveTesting.RunPolicy.OnDemand -> Features.LiveTesting.RunPolicy.Disabled
        | Features.LiveTesting.RunPolicy.Disabled -> Features.LiveTesting.RunPolicy.OnEveryChange
      let unitPolicy =
        lt.TestState.RunPolicies
        |> Map.tryFind Features.LiveTesting.TestCategory.Unit
        |> Option.defaultValue Features.LiveTesting.RunPolicy.OnEveryChange
      let lt' = recomputeStatuses lt (fun s ->
        { s with RunPolicies = s.RunPolicies |> Map.add Features.LiveTesting.TestCategory.Unit (nextPolicy unitPolicy) })
      { model with LiveTesting = lt' }, []

    | SageFsMsg.ToggleCoverage ->
      let lt = model.LiveTesting
      let newDisplay =
        match lt.TestState.CoverageDisplay with
        | Features.LiveTesting.CoverageVisibility.Shown -> Features.LiveTesting.CoverageVisibility.Hidden
        | Features.LiveTesting.CoverageVisibility.Hidden -> Features.LiveTesting.CoverageVisibility.Shown
      let ts = { lt.TestState with CoverageDisplay = newDisplay }
      { model with LiveTesting = { lt with TestState = ts } }, []

    | SageFsMsg.TestCycleTick now ->
      let activeSessionId = activeLiveTestingSessionId model
      let primaryEffects, primaryCycle' =
        model.LiveTesting
        |> Features.LiveTesting.LiveTestCycleState.tick now
      let primaryTagged =
        primaryEffects
        |> List.map (retagRequestFcsTypeCheck activeSessionId)
      let perSessionResults =
        model.PerSessionLiveTesting
        |> Map.toList
        |> List.map (fun (sid, cycle) ->
          let effects, cycle' =
            cycle
            |> Features.LiveTesting.LiveTestCycleState.tick now
          sid, cycle, cycle', effects |> List.map (retagRequestFcsTypeCheck (Some sid)))
      let perSessionChanged =
        perSessionResults
        |> List.exists (fun (_, original, updated, effects) ->
          cycleTickChanged original updated effects)
      let perSessionState' =
        match perSessionChanged with
        | true ->
          perSessionResults
          |> List.fold (fun acc (sid, _, cycle', _) ->
            acc |> Map.add sid cycle') Map.empty
        | false ->
          model.PerSessionLiveTesting
      let perSessionEffects =
        perSessionResults
        |> List.collect (fun (_, _, _, effects) -> effects)
      // Return same model reference when every cycle tick is a no-op (enables ElmLoop skip)
      match cycleTickChanged model.LiveTesting primaryCycle' primaryTagged || perSessionChanged with
      | false -> model, []
      | true ->
        let mappedEffects =
          primaryTagged @ perSessionEffects
          |> List.map SageFsEffect.TestCycle
        { model with
            LiveTesting = primaryCycle'
            PerSessionLiveTesting = perSessionState' }, mappedEffects

    | SageFsMsg.BufferContentChanged (targetSession, filePath, content) ->
      let isActive = model.LiveTesting.TestState.Activation = Features.LiveTesting.LiveTestingActivation.Active
      match isActive with
      | false -> model, []
      | true ->
          let now = DateTimeOffset.UtcNow
          match targetSession with
          | Some sid when activeLiveTestingSessionId model = Some sid ->
              let current = model.LiveTesting
              let cycle' = current |> Features.LiveTesting.LiveTestCycleState.onKeystroke content filePath now
              match obj.ReferenceEquals(cycle', current) with
              | true -> model, []
              | false -> { model with LiveTesting = cycle' }, pendingRebuildCancellationEffects current
          | Some sid ->
              let sessionExists =
                model.Sessions.Sessions
                |> List.exists (fun session -> SessionId.value session.Id = sid)
              match sessionExists with
              | false -> model, []
              | true ->
                  let current =
                    model.PerSessionLiveTesting
                    |> Map.tryFind sid
                    |> Option.defaultValue Features.LiveTesting.LiveTestCycleState.empty
                  let cycle' = current |> Features.LiveTesting.LiveTestCycleState.onKeystroke content filePath now
                  match obj.ReferenceEquals(cycle', current) with
                  | true -> model, []
                  | false ->
                      { model with PerSessionLiveTesting = model.PerSessionLiveTesting |> Map.add sid cycle' },
                      pendingRebuildCancellationEffects current
          | None ->
              let current = model.LiveTesting
              let cycle' = current |> Features.LiveTesting.LiveTestCycleState.onKeystroke content filePath now
              match obj.ReferenceEquals(cycle', current) with
              | true -> model, []
              | false -> { model with LiveTesting = cycle' }, pendingRebuildCancellationEffects current

    | SageFsMsg.FileContentChanged (filePath, content) ->
      let isActive = model.LiveTesting.TestState.Activation = Features.LiveTesting.LiveTestingActivation.Active
      match isActive with
      | false -> model, []
      | true ->
        let now = DateTimeOffset.UtcNow
        let owningSession =
          model.Sessions.Sessions
          |> List.tryFind (fun s ->
            not (System.String.IsNullOrEmpty s.WorkingDirectory) &&
            filePath.StartsWith(s.WorkingDirectory, System.StringComparison.OrdinalIgnoreCase))
        match owningSession with
        | None ->
          // No session owns this file — update the primary (active) session
          let current = model.LiveTesting
          let cycle' = current |> Features.LiveTesting.LiveTestCycleState.onFileSaveWithContent content filePath now
          match obj.ReferenceEquals(cycle', current) with
          | true -> model, []
          | false -> { model with LiveTesting = cycle' }, pendingRebuildCancellationEffects current
        | Some s ->
          match model.Sessions.ActiveSessionId with
          | ActiveSession.Viewing activeId when activeId = s.Id ->
            // File belongs to the active session — update primary
            let current = model.LiveTesting
            let cycle' = current |> Features.LiveTesting.LiveTestCycleState.onFileSaveWithContent content filePath now
            match obj.ReferenceEquals(cycle', current) with
            | true -> model, []
            | false -> { model with LiveTesting = cycle' }, pendingRebuildCancellationEffects current
          | _ ->
            // File belongs to a background session — update per-session state
            let sid = SessionId.value s.Id
            let current =
              model.PerSessionLiveTesting
              |> Map.tryFind sid
              |> Option.defaultValue Features.LiveTesting.LiveTestCycleState.empty
            let cycle' = current |> Features.LiveTesting.LiveTestCycleState.onFileSaveWithContent content filePath now
            match obj.ReferenceEquals(cycle', current) with
            | true -> model, []
            | false ->
                { model with PerSessionLiveTesting = model.PerSessionLiveTesting |> Map.add sid cycle' },
                pendingRebuildCancellationEffects current

    | SageFsMsg.FcsTypeCheckCompleted (targetSession, analysisIdentity, result) ->
      let model', maybeEffects =
        tryUpdateLiveTestingState targetSession (fun cycle ->
          match Features.LiveTesting.LiveTestCycleState.acceptsFcsResult analysisIdentity cycle with
          | true ->
              let effects, cycle' =
                cycle
                |> Features.LiveTesting.LiveTestCycleState.handleFcsResult result
              cycle', effects
          | false ->
              cycle, []) model
      match maybeEffects with
      | Some effects ->
        // OTEL: track dep graph match vs fallback rate
        match effects with
        | [] ->
          Features.LiveTesting.LiveTestingInstrumentation.depGraphFallbackTotal.Add(1L)
        | _ ->
          Features.LiveTesting.LiveTestingInstrumentation.depGraphMatchTotal.Add(1L)
          let affectedCount =
            effects |> List.sumBy (fun e ->
              match e with
              | Features.LiveTesting.TestCycleEffect.RunAffectedTests req
              | Features.LiveTesting.TestCycleEffect.RunRequestedTests (req, _) -> req.Tests.Length
              | _ -> 0)
          Features.LiveTesting.LiveTestingInstrumentation.depGraphAffectedCount.Record(affectedCount)
        let mappedEffects = effects |> List.map SageFsEffect.TestCycle
        model', mappedEffects
      | None ->
        model, []

    | SageFsMsg.RestoreTestCache cachedState ->
      let lt = recomputeStatuses model.LiveTesting (fun s ->
        { s with
            TestCoverageBitmaps = cachedState.TestCoverageBitmaps
            LastResults = cachedState.LastResults
            LastGeneration = cachedState.LastGeneration })
      { model with LiveTesting = lt }, []

    | SageFsMsg.WorkflowSuggestionReceived suggestion ->
      { model with PendingSuggestion = Some suggestion }, []

    | SageFsMsg.WorkflowSuggestionDismissed ->
      { model with PendingSuggestion = None }, []

    | SageFsMsg.WorkflowSuggestionAccepted ->
      let effects =
        match model.PendingSuggestion with
        | Some s -> [ SageFsEffect.SwitchWorkflow s.SuggestedWorkflow ]
        | None -> []
      { model with PendingSuggestion = None }, effects

    | SageFsMsg.RebuildCompleted (targetSession, generation, result) ->
      let model', maybeEffects =
        tryUpdateLiveTestingState targetSession (fun cycle ->
          match cycle.PendingRebuild with
          | Some pending when pending.Generation = generation ->
            let compile =
              match result with
              | Ok () -> Features.LiveTesting.CompileBlock.NoCompileErrors
              | Error msg -> Features.LiveTesting.CompileBlock.RebuildFailed msg
            let cycle' = { cycle with PendingRebuild = None; Compile = compile }
            let effects =
              match result with
              | Ok () ->
                let effect =
                  Features.LiveTesting.TestCycleEffect.RunAffectedTests {
                    Tests = pending.Tests
                    Trigger = pending.Trigger
                    TreeSitterElapsed = pending.TreeSitterElapsed
                    FcsElapsed = pending.FcsElapsed
                    SessionId = pending.SessionId
                    InstrumentationMaps = pending.InstrumentationMaps
                  }
                [ SageFsEffect.TestCycle effect ]
              | Error msg ->
                Utils.Log.warn "[rebuild] Build failed, not running tests: %s" (msg.Substring(0, min 200 msg.Length))
                []
            cycle', effects
          | Some pending ->
            Utils.Log.warn
              "[rebuild] RebuildCompleted(gen=%d) ignored; pending rebuild is gen=%d for %A"
              generation
              pending.Generation
              targetSession
            cycle, []
          | None ->
            Utils.Log.warn "[rebuild] RebuildCompleted(gen=%d) with no PendingRebuild for %A — stale dispatch?" generation targetSession
            cycle, []) model
      match maybeEffects with
      | Some effects ->
        model', effects
      | None ->
        Utils.Log.warn "[rebuild] RebuildCompleted(gen=%d) for unknown session %A — stale dispatch?" generation targetSession
        model, []

    | SageFsMsg.MarkAllTestsStale ->
      let lt = model.LiveTesting
      let allTestIds = lt.TestState.DiscoveredTests |> Array.map (fun t -> t.Id) |> Set.ofArray
      let lt' = recomputeStatuses lt (fun s -> { s with AffectedTests = Set.union s.AffectedTests allTestIds })
      { model with LiveTesting = lt' }, []

  let updateWithInvariant (msg: SageFsMsg) (model: SageFsModel) : SageFsModel * SageFsEffect list =
    let model', effects = update msg model
    #if DEBUG
    match SessionInvariant.validate model'.LiveTesting.TestState model'.LiveTesting.InstrumentationMaps with
    | Some violation ->
      System.Diagnostics.Debug.WriteLine(
        sprintf "SESSION INVARIANT VIOLATION after %A: %s" msg violation.Message)
    | None -> ()
    #endif
    model', effects

/// Pure render function: produces RenderRegion list from model.
/// Every frontend consumes these regions — terminal, web, Neovim, etc.
module SageFsRender =
  let render (model: SageFsModel) : RenderRegion list =
    let bufCursor = ValidatedBuffer.cursor model.Editor.Buffer
    let editorCompletions =
      model.Editor.CompletionMenu
      |> Option.map (fun menu ->
        { Items = menu.Items |> List.map (fun i -> i.Label)
          SelectedIndex = menu.SelectedIndex })
    let editorContent =
      let bufText = ValidatedBuffer.text model.Editor.Buffer
      match model.Editor.Prompt with
      | Some prompt ->
        sprintf "%s\n─── %s: %s█" bufText prompt.Label prompt.Input
      | None -> bufText
    let editorAnnotations = model.LiveTesting.TestState.Cached.EditorAnnotations
    let editorRegion = {
      Id = "editor"
      Flags = RegionFlags.Focusable ||| RegionFlags.LiveUpdate
      Content = editorContent
      Affordances = []
      Cursor = Some { Line = bufCursor.Line; Col = bufCursor.Column }
      Completions = editorCompletions
      LineAnnotations = editorAnnotations
    }

    let activeSessionId =
      match model.Sessions.ActiveSessionId with
      | ActiveSession.Viewing sid -> SessionId.value sid
      | ActiveSession.AwaitingSession -> ""
    let outputRegion =
      let buf = model.RecentOutput.GetActiveBuffer(model.Sessions.ActiveSessionId)
      { Id = "output"
        Flags = RegionFlags.Scrollable ||| RegionFlags.LiveUpdate
        Content = buf.RenderAllCached()
        Affordances = []
        Cursor = None
        Completions = None
        LineAnnotations = [||] }

    let diagnosticsRegion = {
      Id = "diagnostics"
      Flags = RegionFlags.LiveUpdate
      Content =
        model.Diagnostics
        |> Map.tryFind activeSessionId
        |> Option.defaultValue []
        |> List.map (fun d ->
          sprintf "[%s] (%d,%d) %s"
            (Features.Diagnostics.DiagnosticSeverity.label d.Severity)
            d.Range.StartLine d.Range.StartColumn d.Message)
        |> String.concat "\n"
      Affordances = []
      Cursor = None
      Completions = None
      LineAnnotations = [||]
    }

    let sessionsRegion = {
      Id = "sessions"
      Flags = RegionFlags.Clickable ||| RegionFlags.LiveUpdate
      Content =
        let now = DateTime.UtcNow
        let activeId = model.Sessions.ActiveSessionId
        model.Sessions.Sessions
        |> List.mapi (fun i s ->
          let statusLabel =
            match s.Status with
            | SessionDisplayStatus.Running -> "running"
            | SessionDisplayStatus.Starting -> "starting"
            | SessionDisplayStatus.Faulted r -> sprintf "error: %s" r
            | SessionDisplayStatus.Lost -> "lost"
            | SessionDisplayStatus.Stopped -> "suspended"
            | SessionDisplayStatus.Idle -> "idle"
            | SessionDisplayStatus.Restarting -> "restarting"
          let active = match activeId with | ActiveSession.Viewing id when id = s.Id -> " *" | _ -> ""
          let selected = match model.Editor.SelectedSessionIndex = Some i with | true -> ">" | false -> " "
          let projects =
            match s.Projects.IsEmpty with
            | true -> ""
            | false -> sprintf " (%s)" (s.Projects |> List.map System.IO.Path.GetFileNameWithoutExtension |> String.concat ", ")
          let evals =
            match s.EvalCount > 0 with
            | true -> sprintf " evals:%d" s.EvalCount
            | false -> ""
          let uptime =
            let ts = now - s.UpSince
            match ts with
            | ts when ts.TotalDays >= 1.0 -> sprintf " up:%dd%dh" (int ts.TotalDays) ts.Hours
            | ts when ts.TotalHours >= 1.0 -> sprintf " up:%dh%dm" (int ts.TotalHours) ts.Minutes
            | ts when ts.TotalMinutes >= 1.0 -> sprintf " up:%dm" (int ts.TotalMinutes)
            | _ -> " up:just now"
          let dir =
            match s.WorkingDirectory.Length > 0 with
            | true -> sprintf " dir:%s" s.WorkingDirectory
            | false -> ""
          let lastAct =
            let diff = now - s.LastActivity
            match diff with
            | diff when diff.TotalSeconds < 60.0 -> " last:just now"
            | diff when diff.TotalMinutes < 60.0 -> sprintf " last:%dm ago" (int diff.TotalMinutes)
            | diff when diff.TotalHours < 24.0 -> sprintf " last:%dh ago" (int diff.TotalHours)
            | _ -> sprintf " last:%dd ago" (int diff.TotalDays)
          sprintf "%s %s [%s]%s%s%s%s%s%s" selected (SessionId.value s.Id) statusLabel active projects evals uptime dir lastAct)
        |> String.concat "\n"
        |> fun s ->
          let creatingLine =
            match model.CreatingSession with
            | true -> "\n⏳ Creating session..."
            | false -> ""
          match s.Length > 0 with
          | true -> sprintf "%s%s\n↑↓ nav  ↵ switch  Del stop  ^N new" s creatingLine
          | false ->
            match model.CreatingSession with
            | true -> "⏳ Creating session..."
            | false -> "No sessions yet.\nCtrl+N new · Ctrl+Alt+A auto-open off"
      Affordances = []
      Cursor = None
      Completions = None
      LineAnnotations = [||]
    }

    let contextRegion = {
      Id = "context"
      Flags = RegionFlags.LiveUpdate
      Content =
        match model.SessionContext with
        | Some ctx -> SessionContextTui.renderContent ctx
        | None -> ""
      Affordances = []
      Cursor = None
      Completions = None
      LineAnnotations = [||]
    }

    let testsRegion = {
      Id = "tests"
      Flags = RegionFlags.Scrollable ||| RegionFlags.LiveUpdate
      Content =
        TestsPane.buildContent
          80
          (Features.LiveTesting.LiveTestState.statusEntriesForSession "" model.LiveTesting.TestState)
      Affordances = []
      Cursor = None
      Completions = None
      LineAnnotations = [||]
    }

    [ editorRegion; outputRegion; diagnosticsRegion; sessionsRegion; contextRegion; testsRegion ]

/// Dependencies the effect handler needs — injected, not hard-coded.
/// This is the seam between pure Elm and impure infrastructure.
type EffectDeps = {
  /// Resolve which session to target
  ResolveSession: SessionId option -> Result<SessionOperations.SessionResolution, SageFsError>
  /// Get the proxy for a session
  GetProxy: SessionId -> SessionProxy option
  /// Get a streaming test execution proxy for a session.
  /// The proxy streams test results and IL coverage hits.
  GetStreamingTestProxy: SessionId -> (Features.LiveTesting.TestCase array -> int -> (Features.LiveTesting.TestRunResult -> unit) -> (bool array -> unit) -> System.Threading.CancellationToken -> Async<HttpWorkerClient.StreamOutcome>) option
  /// Create a new session
  CreateSession: string list -> string -> WorkflowTypes.SessionWorkflow -> Async<Result<SessionInfo, SageFsError>>
  /// Ensure the working directory has warmup auto-open disabled.
  ConfigureWarmupAutoOpen: string -> Async<Result<OutputLine, string>>
  /// Stop a session
  StopSession: SessionId -> Async<Result<unit, SageFsError>>
  /// Restart a session, optionally rebuilding first.
  RestartSession: SessionId -> bool -> Async<Result<string, SageFsError>>
  /// List all sessions
  ListSessions: unit -> Async<SessionInfo list>
  /// Sleep for the requested number of milliseconds.
  SleepMs: int -> Async<unit>
  /// Fetch warmup context for a session (optional — None disables warmup dispatch)
  GetWarmupContext: (SessionId -> Async<SessionContext option>) option
  /// Test cycle cancellation for stale work
  TestCycleCancellation: Features.LiveTesting.TestCycleCancellation
  /// Register an OS file watcher for the given session's project directory
  RegisterFileWatcher: string -> string -> unit
  /// Dispose the OS file watcher for the given session's project directory
  DisposeFileWatcher: string -> string -> unit
}

/// Routes SageFsEffect to real infrastructure via injected deps.
/// Converts WorkerResponses back into SageFsMsg for the Elm loop.
/// Who reports a live-test run's completion once its stream has ended.
[<RequireQualifiedAccess>]
type RunHandoff =
  /// The run ended on its own — cleanly, with gaps, stalled, or failed — and
  /// reports its own completion.
  | ReportsCompletion
  /// The run was cancelled: the run that replaced it owns the session's run
  /// phase and reports completion, so this one must not end that phase early.
  | LeavesCompletionToSuccessor

module SageFsEffectHandler =

  let newReplyId () =
    Guid.NewGuid().ToString("N").[..7]

  /// Resolve a value that may not be ready yet, retrying with short backoff
  /// (50/100/200/400ms; ~750ms total over 4 retries). A worker's HTTP base-URL
  /// is registered a beat AFTER its session goes Ready, so a just-opened
  /// session's proxy can be None for the first few hundred ms. This is the same
  /// race `RunAffectedTests` already handles inline; `RequestInitialDiscovery`
  /// did not, so it silently dropped such sessions — no discovery, no baseline
  /// run, and the live-testing panel never reached "N✓" (the lt-* demos and the
  /// --integration-lt journey both hung on exactly this).
  let resolveWithBackoff (resolve: unit -> 'a option) : Async<'a option> =
    async {
      let mutable value = resolve ()
      let mutable retries = 0
      let mutable delay = 50
      while value.IsNone && retries < 4 do
        retries <- retries + 1
        do! Async.Sleep delay
        delay <- delay * 2
        value <- resolve ()
      return value
    }

  let evalResponseToMsg
    (sessionId: SessionId)
    (response: WorkerResponse) : SageFsMsg =
    match response with
    | WorkerResponse.EvalResult (_, Ok output, diags, _) ->
      let diagnostics =
        diags |> List.map (fun d -> {
          Message = d.Message
          Subcategory = "typecheck"
          Range = {
            StartLine = d.StartLine
            StartColumn = d.StartColumn
            EndLine = d.EndLine
            EndColumn = d.EndColumn
          }
          Severity = d.Severity
          ErrorNumber = d.ErrorNumber
        })
      SageFsMsg.Event (
        TuiEvent.EvalCompleted (SessionId.value sessionId, output, diagnostics))
    | WorkerResponse.EvalResult (_, Error err, _, _) ->
      SageFsMsg.Event (
        TuiEvent.EvalFailed (SessionId.value sessionId, SageFsError.describe err))
    | WorkerResponse.EvalCancelled _ ->
      SageFsMsg.Event (TuiEvent.EvalCancelled (SessionId.value sessionId))
    | other ->
      SageFsMsg.Event (
        TuiEvent.EvalFailed (
          SessionId.value sessionId, sprintf "Unexpected response: %A" other))

  let completionResponseToMsg
    (response: WorkerResponse) : SageFsMsg =
    match response with
    | WorkerResponse.CompletionResult (_, items) ->
      let completionItems =
        items |> List.map (fun label ->
          { Label = label; Kind = "member"; Detail = None })
      SageFsMsg.Event (TuiEvent.CompletionReady completionItems)
    | _ ->
      SageFsMsg.Event (TuiEvent.CompletionReady [])

  let withSession
    (deps: EffectDeps)
    (dispatch: SageFsMsg -> unit)
    (sessionId: SessionId option)
    (action: SessionId -> SessionProxy -> Async<unit>) =
    async {
      match deps.ResolveSession sessionId with
      | Ok resolution ->
        let id = SessionOperations.sessionId resolution
        match deps.GetProxy id with
        | Some proxy -> do! action id proxy
        | None ->
          dispatch (SageFsMsg.Event (
            TuiEvent.EvalFailed (
              SessionId.value id, sprintf "No proxy for session %s" (SessionId.value id))))
      | Error err ->
        dispatch (SageFsMsg.Event (
          TuiEvent.EvalFailed ("", SageFsError.describe err)))
    }

  let sessionInfoToSnapshot (info: SessionInfo) : SessionSnapshot =
    { Id = info.Id
      Name = info.Name
      Projects = info.Projects
      Status =
        match info.Status with
        | SessionLifecycleStatus.Ready _ -> SessionDisplayStatus.Running
        | SessionLifecycleStatus.Starting _ -> SessionDisplayStatus.Starting
        | SessionLifecycleStatus.Evaluating _ -> SessionDisplayStatus.Running
        | SessionLifecycleStatus.Building _ -> SessionDisplayStatus.Running
        | SessionLifecycleStatus.Faulted reason -> SessionDisplayStatus.Faulted (reason |> Option.defaultValue "faulted")
        | SessionLifecycleStatus.Restarting _ -> SessionDisplayStatus.Restarting
        | SessionLifecycleStatus.Stopped -> SessionDisplayStatus.Stopped
      LastActivity = info.LastActivity
      EvalCount = 0
      UpSince = info.CreatedAt
      WorkingDirectory = info.WorkingDirectory }

  /// The main effect handler — plug into ElmProgram.ExecuteEffect
  let execute
    (deps: EffectDeps)
    (dispatch: SageFsMsg -> unit)
    (effect: SageFsEffect) : Async<unit> =
    match effect with
    | SageFsEffect.Editor editorEffect ->
      match editorEffect with
      | EditorEffect.RequestEval code ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let replyId = newReplyId ()
            let! response =
              proxy (WorkerMessage.EvalCode (code, replyId))
            dispatch (evalResponseToMsg sid response)
          })

      | EditorEffect.RequestCompletion (text, cursor) ->
        withSession deps dispatch None (fun _ proxy ->
          async {
            let replyId = newReplyId ()
            let! response =
              proxy (
                WorkerMessage.GetCompletions (text, cursor, replyId))
            dispatch (completionResponseToMsg response)
          })

      | EditorEffect.RequestHistory _ ->
        async { () }

      | EditorEffect.RequestSessionList ->
        async {
          let! sessions = deps.ListSessions ()
          let snaps = sessions |> List.map sessionInfoToSnapshot
          dispatch (SageFsMsg.Event (TuiEvent.SessionsRefreshed snaps))
          // Fetch warmup context for the active Ready session
          match deps.GetWarmupContext with
          | Some getCtx ->
            let readySession =
              sessions |> List.tryFind (fun s -> match s.Status with SessionLifecycleStatus.Ready _ -> true | _ -> false)
            match readySession with
            | Some info ->
              let! ctx = getCtx info.Id
              match ctx with
              | Some sessionCtx ->
                dispatch (SageFsMsg.Event (TuiEvent.WarmupContextUpdated sessionCtx))
              | None -> ()
            | None -> ()
          | None -> ()
        }

      | EditorEffect.RequestSessionSwitch sessionId ->
        async {
          dispatch (SageFsMsg.Event (
            TuiEvent.SessionSwitched (None, sessionId)))
        }

      | EditorEffect.RequestSessionCreate projects ->
        async {
          let workingDir =
            match projects with
            | [dir] when System.IO.Directory.Exists(dir) -> dir
            | _ -> "."
          let projectList =
            match projects with
            | [dir] when System.IO.Directory.Exists(dir) -> []
            | other -> other
          let! result = deps.CreateSession projectList workingDir WorkflowTypes.SessionWorkflow.Interactive
          match result with
          | Ok info ->
            dispatch (SageFsMsg.Event (
              TuiEvent.SessionCreated (sessionInfoToSnapshot info)))
            dispatch (SageFsMsg.Event (
              TuiEvent.SessionSwitched (None, SessionId.value info.Id)))
          | Error err ->
            dispatch (SageFsMsg.Event (
              TuiEvent.EvalFailed (
                "", sprintf "Create failed: %s" (SageFsError.describe err))))
        }

      | EditorEffect.RequestConfigureWarmupAutoOpen workingDir ->
        async {
          let! result = deps.ConfigureWarmupAutoOpen workingDir
          match result with
          | Ok line ->
            dispatch (SageFsMsg.Event (TuiEvent.OutputEmitted line))
          | Error err ->
            dispatch (SageFsMsg.Event (TuiEvent.EvalFailed ("", err)))
        }

      | EditorEffect.RequestSessionStop sessionIdStr ->
        async {
          match SessionId.validate sessionIdStr with
          | Error _ -> ()
          | Ok sessionId ->
            let! result = deps.StopSession sessionId
            match result with
            | Ok () ->
              dispatch (SageFsMsg.Event (
                TuiEvent.SessionStopped sessionIdStr))
            | Error err ->
              dispatch (SageFsMsg.Event (
                TuiEvent.EvalFailed (
                  sessionIdStr,
                  sprintf "Stop failed: %s" (SageFsError.describe err))))
        }

      | EditorEffect.RequestReset ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let replyId = newReplyId ()
            let! _ = proxy (WorkerMessage.ResetSession replyId)
            dispatch (SageFsMsg.Event (
              TuiEvent.SessionStatusChanged (SessionId.value sid, SessionDisplayStatus.Starting)))
          })

      | EditorEffect.RequestHardReset ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let replyId = newReplyId ()
            let! _ = proxy (WorkerMessage.HardResetSession (false, replyId))
            dispatch (SageFsMsg.Event (
              TuiEvent.SessionStatusChanged (SessionId.value sid, SessionDisplayStatus.Restarting)))
          })

      | EditorEffect.RequestSmartReset ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let soft () = task {
              let replyId = newReplyId ()
              let! resp = proxy (WorkerMessage.ResetSession replyId) |> Async.StartAsTask
              match resp with
              | WorkerResponse.ResetResult (_, Ok ()) -> return Ok ()
              | WorkerResponse.ResetResult (_, Error e) -> return Error (SageFsError.describe e)
              | _ -> return Error "unexpected response"
            }
            let hard () = task {
              let replyId = newReplyId ()
              let! resp = proxy (WorkerMessage.HardResetSession (false, replyId)) |> Async.StartAsTask
              match resp with
              | WorkerResponse.HardResetResult (_, Ok msg) -> return Ok msg
              | WorkerResponse.HardResetResult (_, Error e) -> return Error (SageFsError.describe e)
              | _ -> return Error "unexpected response"
            }
            let! outcome = SmartReset.execute soft hard |> Async.AwaitTask
            let status =
              match outcome with
              | SmartReset.Outcome.SoftResetSucceeded ->
                SessionDisplayStatus.Starting
              | SmartReset.Outcome.EscalatedToHardReset _ ->
                SessionDisplayStatus.Restarting
              | SmartReset.Outcome.AllResetsFailed _ ->
                SessionDisplayStatus.Faulted "all resets failed"
            dispatch (SageFsMsg.Event (
              TuiEvent.SessionStatusChanged (SessionId.value sid, status)))
          })

    | SageFsEffect.TestCycle testCycleEffect ->
      async {
        match testCycleEffect with
        | Features.LiveTesting.TestCycleEffect.RequestInitialDiscovery ->
          let! sessions = deps.ListSessions ()
          // Resolve each session's proxy with backoff: a session opened moments
          // before live testing is enabled has no registered worker URL yet, and
          // a one-shot lookup here used to drop it — killing discovery and the
          // baseline run (see resolveWithBackoff). Retry so discovery still fires.
          let! discoveryTargets =
            sessions
            |> List.map (fun session ->
              async {
                match! resolveWithBackoff (fun () -> deps.GetProxy session.Id) with
                | Some proxy -> return Some (session.Id, proxy)
                | None ->
                  Utils.Log.warn "[SageFsApp] Initial test discovery: no worker proxy for %s after retries" (SessionId.value session.Id)
                  return None
              })
            |> Async.Sequential
          let discoveryTargets = discoveryTargets |> Array.choose id
          for sid, proxy in discoveryTargets do
            let replyId = newReplyId ()
            let! report =
              async {
                try
                  let! resp = proxy (WorkerMessage.GetTestDiscovery replyId)
                  return SessionManager.TestDiscoveryReport.ofResponse resp
                with ex ->
                  return SessionManager.TestDiscoveryReport.DiscoveryFailed ex.Message
              }
            match report with
            | SessionManager.TestDiscoveryReport.Discovered (tests, providers) ->
              match List.isEmpty providers with
              | true -> ()
              | false -> dispatch (SageFsMsg.Event (TuiEvent.ProvidersDetected providers))
              dispatch (SageFsMsg.Event (TuiEvent.TestsDiscovered (SessionId.value sid, tests)))
            | SessionManager.TestDiscoveryReport.DiscoveryFailed reason ->
              Utils.Log.warn "[SageFsApp] Initial test discovery failed for %s: %s" (SessionId.value sid) reason
              dispatch (SageFsMsg.Event (TuiEvent.TestDiscoveryFailed (SessionId.value sid, reason)))
        | Features.LiveTesting.TestCycleEffect.ParseTreeSitter (content, filePath) ->
          let span = Instrumentation.startSpan Instrumentation.testCycleSource "test_cycle.treesitter.parse" ["file", box filePath]
          let (locations, elapsed) =
            Features.LiveTesting.LiveTestingInstrumentation.traced
              "SageFs.LiveTesting.TreeSitterParse"
              ["file", box filePath]
              (fun () ->
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let locs = Features.LiveTesting.TestTreeSitter.discover filePath content
                sw.Stop()
                (locs, sw.Elapsed))
          Instrumentation.treeSitterParseMs.Record(elapsed.TotalMilliseconds)
          Features.LiveTesting.LiveTestingInstrumentation.treeSitterHistogram.Record(elapsed.TotalMilliseconds)
          Instrumentation.succeedSpan span
          dispatch (SageFsMsg.Event (TuiEvent.TestLocationsDetected ("", locations)))
          let timing : Features.LiveTesting.TestCycleTiming = {
            Depth = Features.LiveTesting.TestCycleDepth.TreeSitterOnly elapsed
            TotalTests = 0; AffectedTests = 0
            Trigger = Features.LiveTesting.RunTrigger.Keystroke
            Timestamp = System.DateTimeOffset.UtcNow
          }
          dispatch (SageFsMsg.Event (TuiEvent.TestCycleTimingRecorded timing))
        | Features.LiveTesting.TestCycleEffect.RequestFcsTypeCheck req ->
          let span = Instrumentation.startSpan Instrumentation.testCycleSource "test_cycle.fcs.typecheck" ["file", box req.FilePath]
          let fcsStopwatch = System.Diagnostics.Stopwatch.StartNew()
          let targetSid =
            req.SessionId
            |> Option.bind (fun s ->
              match SessionId.validate s with Ok sid -> Some sid | Error _ -> None)
          do! withSession deps dispatch targetSid (fun _sid proxy ->
            async {
              let code =
                match req.Content with
                | Some buffered -> buffered
                | None ->
                    try System.IO.File.ReadAllText(req.FilePath)
                    with ex ->
                      Utils.Log.warn "[SageFsApp] File read failed: %s" ex.Message
                      ""
              match code <> "" with
              | true ->
                let effectiveAnalysisIdentity =
                  req.AnalysisIdentity
                  |> Option.defaultWith (fun () ->
                    Features.LiveTesting.AnalysisIdentity.ofContent code)
                let replyId = newReplyId ()
                // The call runs inside the async so even a synchronous transport
                // throw is caught here.
                let! outcome =
                  async { return! proxy (WorkerMessage.TypeCheckWithSymbols(code, req.FilePath, replyId)) }
                  |> Async.Catch
                fcsStopwatch.Stop()
                Instrumentation.fcsTypecheckMs.Record(fcsStopwatch.Elapsed.TotalMilliseconds)
                Features.LiveTesting.LiveTestingInstrumentation.fcsHistogram.Record(fcsStopwatch.Elapsed.TotalMilliseconds)
                let result =
                  match outcome with
                  | Choice2Of2 ex ->
                    // A worker mid-restart cannot answer: that is a cancelled
                    // check, not a daemon fault for the Elm loop to alarm on.
                    Utils.Log.warn "[SageFsApp] Type-check could not reach the worker for %s: %s" req.FilePath ex.Message
                    Features.LiveTesting.FcsTypeCheckResult.Cancelled req.FilePath
                  | Choice1Of2 resp ->
                  match resp with
                  | WorkerResponse.TypeCheckWithSymbolsResult(_rid, diags, symRefs) ->
                    let hasErrors =
                      diags |> List.exists (fun d -> d.Severity = DiagnosticSeverity.Blocking)
                    match hasErrors with
                    | true ->
                      let errors =
                        diags
                        |> List.filter (fun d -> d.Severity = Features.Diagnostics.DiagnosticSeverity.Blocking)
                        |> List.map (fun d -> d.Message)
                      Features.LiveTesting.FcsTypeCheckResult.Failed(req.FilePath, errors)
                    | false ->
                      let refs = symRefs |> List.map WorkerProtocol.WorkerSymbolRef.toDomain
                      Features.LiveTesting.FcsTypeCheckResult.Success(req.FilePath, refs)
                  | _ ->
                    Features.LiveTesting.FcsTypeCheckResult.Cancelled req.FilePath
                dispatch (SageFsMsg.FcsTypeCheckCompleted (req.SessionId, Some effectiveAnalysisIdentity, result))
                let timing : Features.LiveTesting.TestCycleTiming = {
                  Depth = Features.LiveTesting.TestCycleDepth.ThroughFcs(req.TreeSitterElapsed, fcsStopwatch.Elapsed)
                  TotalTests = 0; AffectedTests = 0
                  Trigger = Features.LiveTesting.RunTrigger.Keystroke
                  Timestamp = System.DateTimeOffset.UtcNow
                }
                dispatch (SageFsMsg.Event (TuiEvent.TestCycleTimingRecorded timing))
                Instrumentation.succeedSpan span
              | false -> ()
            })
        | Features.LiveTesting.TestCycleEffect.EvalBufferThenRunAffected req ->
          // Brief 4 keystone: identity-preserving eval of the edited buffer,
          // then run exactly the tests already selected as affected — never
          // the compiled DLL, never the whole suite (see
          // live-testing-asyoutype-plan.md §2, Invariants 1/2/6).
          let targetSid =
            req.Run.SessionId
            |> Option.bind (fun s -> match SessionId.validate s with Ok sid -> Some sid | Error _ -> None)
          do! withSession deps dispatch targetSid (fun sid proxy ->
            async {
              let replyId = newReplyId ()
              let! outcome =
                async { return! proxy (WorkerMessage.EvalLiveTestFile(req.FilePath, req.Content, replyId)) }
                |> Async.Catch
              match outcome with
              | Choice2Of2 ex ->
                // Worker mid-restart etc.: surface it, but LiveTesting state
                // (discovery + results) is untouched — fail-closed.
                Utils.Log.warn "[SageFsApp] EvalLiveTestFile could not reach the worker for %s: %s" req.FilePath ex.Message
                dispatch (SageFsMsg.Event (
                  TuiEvent.EvalFailed (
                    SessionId.value sid,
                    sprintf "Live-test eval could not reach the worker: %s" ex.Message)))
              | Choice1Of2 (WorkerResponse.EvalLiveTestFileResult (_, Error err)) ->
                // Buffer doesn't compile / eval failed. Fail-closed (Invariant
                // 4): do NOT dispatch a discovery merge or a run — the prior
                // dynamic discovery + last-good results stay exactly as they
                // were. Only the error is surfaced. (Brief 5 owns a dedicated
                // stale/rollback DU; this is the honest interim signal.)
                Utils.Log.warn "[SageFsApp] EvalLiveTestFile failed for %s: %s" req.FilePath (SageFsError.describe err)
                dispatch (SageFsMsg.Event (
                  TuiEvent.EvalFailed (SessionId.value sid, SageFsError.describe err)))
              | Choice1Of2 (WorkerResponse.EvalLiveTestFileResult (_, Ok (tests, providers))) ->
                match List.isEmpty providers with
                | true -> ()
                | false -> dispatch (SageFsMsg.Event (TuiEvent.ProvidersDetected providers))
                // Merge the live (compiled ∪ dynamic) discovery WITHOUT
                // auto-running every discovered test (see
                // TuiEvent.LiveDiscoveryMerged), then run exactly the
                // coverage/graph-selected tests `req.Run` already carries —
                // re-resolved against the fresh merge so a re-run reflects
                // the just-eval'd metadata rather than the pre-eval snapshot.
                dispatch (SageFsMsg.Event (TuiEvent.LiveDiscoveryMerged (SessionId.value sid, tests)))
                let runIds = req.Run.Tests |> Array.map (fun tc -> tc.Id) |> Set.ofArray
                let freshTests = tests |> Array.filter (fun tc -> Set.contains tc.Id runIds)
                let toRun = match Array.isEmpty freshTests with true -> req.Run.Tests | false -> freshTests
                match Array.isEmpty toRun with
                | true -> ()
                | false ->
                  dispatch (SageFsMsg.Event (
                    TuiEvent.RunTestsRequested (Some (SessionId.value sid), toRun, None)))
              | Choice1Of2 _ -> ()
            })
        | Features.LiveTesting.TestCycleEffect.CancelRebuild (targetSession, generation) ->
          match deps.TestCycleCancellation.Rebuild.cancel(targetSession, generation) with
          | true ->
              Utils.Log.info "[rebuild] CancelRebuild cancelled generation %d for %A" generation targetSession
          | false ->
              Utils.Log.info "[rebuild] CancelRebuild ignored for stale generation %d for %A" generation targetSession
        | Features.LiveTesting.TestCycleEffect.RequestRebuild (generation, req) ->
          let targetSession = req.SessionId
          let ct = deps.TestCycleCancellation.Rebuild.start(targetSession, generation)
          Async.Start((async {
            let rebuildStopwatch = System.Diagnostics.Stopwatch.StartNew()
            try
              try
                ct.ThrowIfCancellationRequested()
                let targetSid =
                  targetSession
                  |> Option.bind (fun s ->
                    match SessionId.validate s with Ok sid -> Some sid | Error _ -> None)
                match deps.ResolveSession targetSid with
                | Error err ->
                  match ct.IsCancellationRequested with
                  | true ->
                    rebuildStopwatch.Stop()
                    Utils.Log.info "[rebuild] RequestRebuild cancelled before resolve completed for %A" targetSession
                  | false ->
                    rebuildStopwatch.Stop()
                    dispatch (SageFsMsg.RebuildCompleted (targetSession, generation, Error (SageFsError.describe err)))
                | Ok resolution ->
                  ct.ThrowIfCancellationRequested()
                  let sid = SessionOperations.sessionId resolution
                  let sidStr = SessionId.value sid
                  let restartStopwatch = System.Diagnostics.Stopwatch.StartNew()
                  match! deps.RestartSession sid true with
                  | Error err ->
                    match ct.IsCancellationRequested with
                    | true ->
                      restartStopwatch.Stop()
                      rebuildStopwatch.Stop()
                      Utils.Log.info "[rebuild] RequestRebuild cancelled after restart attempt for %s" sidStr
                    | false ->
                      restartStopwatch.Stop()
                      rebuildStopwatch.Stop()
                      Instrumentation.liveTestingRebuildRestartMs.Record(restartStopwatch.Elapsed.TotalMilliseconds)
                      Instrumentation.liveTestingRebuildPipelineMs.Record(rebuildStopwatch.Elapsed.TotalMilliseconds)
                      let msg = SageFsError.describe err
                      Utils.Log.warn "[rebuild] RestartSession(rebuild=true) failed for %s: %s" sidStr msg
                      dispatch (SageFsMsg.RebuildCompleted (targetSession, generation, Error msg))
                  | Ok msg ->
                    ct.ThrowIfCancellationRequested()
                    restartStopwatch.Stop()
                    Instrumentation.liveTestingRebuildRestartMs.Record(restartStopwatch.Elapsed.TotalMilliseconds)
                    Utils.Log.info
                      "[rebuild] RestartSession(rebuild=true) started for %s in %.1fms: %s"
                      sidStr
                      restartStopwatch.Elapsed.TotalMilliseconds
                      msg
                    let waitTimeoutMs = 30000
                    let fastPollWindowMs = 1000
                    let fastPollDelayMs = 50
                    let slowPollDelayMs = 250
                    let deadline = DateTimeOffset.UtcNow.AddMilliseconds(float waitTimeoutMs)
                    let waitStopwatch = System.Diagnostics.Stopwatch.StartNew()
                    let mutable readyObservedMs : float option = None
                    let mutable proxyObservedMs : float option = None
                    let recordReadyObservation status =
                      match readyObservedMs, status with
                      | None, Some (SessionLifecycleStatus.Ready _) ->
                        let elapsedMs = waitStopwatch.Elapsed.TotalMilliseconds
                        readyObservedMs <- Some elapsedMs
                        Instrumentation.liveTestingRebuildReadyWaitMs.Record(elapsedMs)
                        Utils.Log.info "[rebuild] Session %s reached Ready %.1fms after rebuild restart" sidStr elapsedMs
                      | _ -> ()
                    let recordProxyObservation hasProxy =
                      match proxyObservedMs, hasProxy with
                      | None, true ->
                        let elapsedMs = waitStopwatch.Elapsed.TotalMilliseconds
                        proxyObservedMs <- Some elapsedMs
                        Instrumentation.liveTestingRebuildProxyWaitMs.Record(elapsedMs)
                        Utils.Log.info "[rebuild] Session %s streaming proxy available %.1fms after rebuild restart" sidStr elapsedMs
                      | _ -> ()
                    let rec waitForReadyProxy waitedMs = async {
                      ct.ThrowIfCancellationRequested()
                      let! sessions = deps.ListSessions()
                      ct.ThrowIfCancellationRequested()
                      let status =
                        sessions
                        |> List.tryFind (fun si -> si.Id = sid)
                        |> Option.map (fun si -> si.Status)
                      let streamingProxy = deps.GetStreamingTestProxy sid
                      let hasStreamingProxy = streamingProxy |> Option.isSome
                      recordReadyObservation status
                      recordProxyObservation hasStreamingProxy
                      match status, hasStreamingProxy with
                      | Some (SessionLifecycleStatus.Ready _), true ->
                        waitStopwatch.Stop()
                        rebuildStopwatch.Stop()
                        Instrumentation.liveTestingRebuildPipelineMs.Record(rebuildStopwatch.Elapsed.TotalMilliseconds)
                        let readyMs = readyObservedMs |> Option.defaultValue waitStopwatch.Elapsed.TotalMilliseconds
                        let proxyMs = proxyObservedMs |> Option.defaultValue waitStopwatch.Elapsed.TotalMilliseconds
                        Utils.Log.info
                          "[rebuild] Session %s ready with streaming proxy after rebuild (restart=%.1fms ready=%.1fms proxy=%.1fms total=%.1fms)"
                          sidStr
                          restartStopwatch.Elapsed.TotalMilliseconds
                          readyMs
                            proxyMs
                            rebuildStopwatch.Elapsed.TotalMilliseconds
                        dispatch (SageFsMsg.RebuildCompleted (targetSession, generation, Ok ()))
                      | _ when DateTimeOffset.UtcNow >= deadline ->
                        ct.ThrowIfCancellationRequested()
                        waitStopwatch.Stop()
                        rebuildStopwatch.Stop()
                        Instrumentation.liveTestingRebuildPipelineMs.Record(rebuildStopwatch.Elapsed.TotalMilliseconds)
                        let statusText =
                          status
                          |> Option.map string
                          |> Option.defaultValue "missing"
                        let readyText =
                          readyObservedMs
                          |> Option.map (fun elapsedMs -> sprintf "%.1fms" elapsedMs)
                          |> Option.defaultValue "not-observed"
                        let proxyText =
                          proxyObservedMs
                          |> Option.map (fun elapsedMs -> sprintf "%.1fms" elapsedMs)
                          |> Option.defaultValue "not-observed"
                        let err =
                          sprintf
                            "Rebuild succeeded but session %s never became ready for test execution within %dms (status=%s, restart=%.1fms, ready=%s, proxy=%s, total=%.1fms)."
                            sidStr
                            waitTimeoutMs
                            statusText
                            restartStopwatch.Elapsed.TotalMilliseconds
                            readyText
                            proxyText
                            rebuildStopwatch.Elapsed.TotalMilliseconds
                        Utils.Log.warn "[rebuild] %s" err
                        dispatch (SageFsMsg.RebuildCompleted (targetSession, generation, Error err))
                      | _ ->
                        let pollDelayMs =
                          match waitedMs < fastPollWindowMs with
                          | true -> fastPollDelayMs
                          | false -> slowPollDelayMs
                        do! deps.SleepMs pollDelayMs
                        ct.ThrowIfCancellationRequested()
                        return! waitForReadyProxy (waitedMs + pollDelayMs)
                    }
                    do! waitForReadyProxy 0
              with
              | :? OperationCanceledException ->
                rebuildStopwatch.Stop()
                Utils.Log.info "[rebuild] RequestRebuild cancelled for %A" targetSession
              | ex ->
                rebuildStopwatch.Stop()
                Instrumentation.liveTestingRebuildPipelineMs.Record(rebuildStopwatch.Elapsed.TotalMilliseconds)
                Utils.Log.error "[rebuild] Exception: %s" ex.Message
                dispatch (SageFsMsg.RebuildCompleted (targetSession, generation, Error ex.Message))
            finally
              deps.TestCycleCancellation.Rebuild.complete(targetSession, generation)
            }), ct)
        | Features.LiveTesting.TestCycleEffect.RegisterFileWatcher (_sessionId, directory) ->
          deps.RegisterFileWatcher _sessionId directory
        | Features.LiveTesting.TestCycleEffect.DisposeFileWatcher (_sessionId, directory) ->
          deps.DisposeFileWatcher _sessionId directory
        | Features.LiveTesting.TestCycleEffect.RunAffectedTests req
        | Features.LiveTesting.TestCycleEffect.RunRequestedTests (req, _) ->
          let tests = req.Tests
          let trigger = req.Trigger
          let tsElapsed = req.TreeSitterElapsed
          let fcsElapsed = req.FcsElapsed
          let targetSession = req.SessionId
          let instrumentationMaps = req.InstrumentationMaps
          match Array.isEmpty tests with
          | true -> ()
          | false ->
            let testIds = tests |> Array.map (fun tc -> tc.Id)
            // A requested run starts WITH the generation allocated when it was
            // requested; anything else bumps a new one as it starts.
            let startMessage =
              match testCycleEffect, targetSession with
              | Features.LiveTesting.TestCycleEffect.RunRequestedTests (_, generation), Some sid ->
                TuiEvent.TestRunStartedAt (testIds, sid, generation)
              | _ -> TuiEvent.TestRunStarted (testIds, targetSession)
            dispatch (SageFsMsg.Event startMessage)
            let ct = deps.TestCycleCancellation.TestRun.next()
            let hasInstrMaps = not (Array.isEmpty instrumentationMaps)
            let testCycleSpan = Instrumentation.startSpan Instrumentation.testCycleSource "test_cycle.test.execution" ["test.count", box tests.Length; "trigger", box (sprintf "%A" trigger); "coverage.has_maps", box hasInstrMaps; "coverage.probe_count", box (instrumentationMaps |> Array.sumBy (fun m -> m.TotalProbes))]
            Async.Start(async {
              Instrumentation.testExecutionActiveCount.Add(1L)
              use activity =
                Features.LiveTesting.LiveTestingInstrumentation.activitySource.StartActivity(
                  "SageFs.LiveTesting.TestExecution")
              let sw = System.Diagnostics.Stopwatch.StartNew()
              let receivedIds = System.Collections.Generic.HashSet<Features.LiveTesting.TestId>()
              let mutable handoff = RunHandoff.ReportsCompletion
              try
                let targetSid = targetSession |> Option.bind (fun s -> match SessionId.validate s with Ok sid -> Some sid | Error _ -> None)
                match deps.ResolveSession targetSid with
                | Ok resolution ->
                  let sid = SessionOperations.sessionId resolution
                  // Retry proxy lookup — worker URL may not be registered yet at startup
                  // Short backoff: 50, 100, 200, 400ms (750ms total vs old 10s)
                  let mutable proxy = deps.GetStreamingTestProxy sid
                  let mutable retries = 0
                  let mutable delay = 50
                  while proxy.IsNone && retries < 4 do
                    retries <- retries + 1
                    do! Async.Sleep delay
                    delay <- delay * 2
                    proxy <- deps.GetStreamingTestProxy sid
                  match proxy with
                  | Some streamProxy ->
                    use resultFlusher =
                      new BatchFlusher<Features.LiveTesting.TestRunResult>(25, 200, fun batch ->
                        Instrumentation.testResultBatchSize.Record(int64 batch.Length)
                        dispatch (SageFsMsg.Event (TuiEvent.TestResultsBatch (targetSession, batch)))
                      )
                    let onResult (result: Features.LiveTesting.TestRunResult) =
                      receivedIds.Add(result.TestId) |> ignore
                      resultFlusher.Add(result)
                    let onCoverage (hits: bool array) =
                      let mergedMap = Features.LiveTesting.InstrumentationMap.merge instrumentationMaps
                      match mergedMap.TotalProbes > 0 && hits.Length = mergedMap.TotalProbes with
                      | true ->
                        let coverage = Features.LiveTesting.InstrumentationMap.toCoverageState hits mergedMap
                        dispatch (SageFsMsg.Event (TuiEvent.CoverageUpdated coverage))
                        let bitmap = Features.LiveTesting.CoverageBitmap.ofBoolArray hits
                        dispatch (SageFsMsg.Event (TuiEvent.CoverageBitmapCollected (targetSession, testIds, bitmap)))
                        match activity <> null with
                        | true ->
                          activity.SetTag("coverage.total_probes", hits.Length) |> ignore
                          activity.SetTag("coverage.hit_probes", Features.LiveTesting.CoverageBitmap.popCount bitmap) |> ignore
                          activity.SetTag("coverage.tests_in_batch", testIds.Length) |> ignore
                        | false -> ()
                      | false -> ()
                    let parallelism = max 4 (Environment.ProcessorCount / 2)
                    let! outcome = streamProxy tests parallelism onResult onCoverage ct // ct explicit, not ambient — see HttpWorkerClient.fs's safeAwait
                    // Whichever way the stream ended — a clean end with gaps, a
                    // stall, a cancellation — every requested test that never
                    // reported gets a truthful NoResult saying why, so none is
                    // left spinning and none gets a fabricated failure. Tests that
                    // did report are excluded: their outcome is never replaced.
                    // BatchFlusher's Dispose (via 'use') flushes the reported ones.
                    let reason = HttpWorkerClient.noResultReason outcome
                    let missing =
                      Features.LiveTesting.TestRunResult.neverReported
                        reason System.DateTimeOffset.UtcNow tests (receivedIds |> Set.ofSeq)
                    match missing.Length with
                    | 0 -> ()
                    | _ ->
                      dispatch (SageFsMsg.Event (TuiEvent.TestResultsBatch (targetSession, missing)))
                      Utils.Log.warn
                        "[LiveTesting] %d of %d tests never reported: %s"
                        missing.Length tests.Length (Features.LiveTesting.NoResultReason.describe reason)
                    match outcome with
                    | HttpWorkerClient.StreamOutcome.Cancelled ->
                      handoff <- RunHandoff.LeavesCompletionToSuccessor
                    | HttpWorkerClient.StreamOutcome.Completed
                    | HttpWorkerClient.StreamOutcome.TimedOut _ -> ()
                  | None ->
                    let notRunResults =
                      tests |> Array.map (fun tc ->
                        { TestId = tc.Id
                          TestName = tc.FullName
                          Result = Features.LiveTesting.TestResult.NotRun
                          Timestamp = System.DateTimeOffset.UtcNow
                          Output = None }
                        : Features.LiveTesting.TestRunResult)
                    dispatch (SageFsMsg.Event (TuiEvent.TestResultsBatch (targetSession, notRunResults)))
                | Error _ ->
                  let notRunResults =
                    tests |> Array.map (fun tc ->
                      { TestId = tc.Id
                        TestName = tc.FullName
                        Result = Features.LiveTesting.TestResult.NotRun
                        Timestamp = System.DateTimeOffset.UtcNow
                        Output = None }
                      : Features.LiveTesting.TestRunResult)
                  dispatch (SageFsMsg.Event (TuiEvent.TestResultsBatch (targetSession, notRunResults)))
                match handoff with
                | RunHandoff.LeavesCompletionToSuccessor ->
                  // Superseded: the replacing run owns this session's run phase,
                  // so reporting completion here would end its phase early.
                  sw.Stop()
                  Instrumentation.succeedSpan testCycleSpan
                  Instrumentation.testExecutionActiveCount.Add(-1L)
                | RunHandoff.ReportsCompletion ->
                  sw.Stop()
                  Instrumentation.testExecutionMs.Record(sw.Elapsed.TotalMilliseconds)
                  let endToEndMs = tsElapsed.TotalMilliseconds + fcsElapsed.TotalMilliseconds + sw.Elapsed.TotalMilliseconds
                  Instrumentation.testCycleEndToEnd.Record(endToEndMs)
                  Features.LiveTesting.LiveTestingInstrumentation.executionHistogram.Record(sw.Elapsed.TotalMilliseconds)
                  match activity <> null with
                  | true ->
                    activity.SetTag("test_count", tests.Length) |> ignore
                    activity.SetTag("trigger", sprintf "%A" trigger) |> ignore
                    activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
                  | false -> ()
                  dispatch (SageFsMsg.Event (TuiEvent.TestRunCompleted targetSession))
                  let timing : Features.LiveTesting.TestCycleTiming = {
                    Depth = Features.LiveTesting.TestCycleDepth.ThroughExecution(
                              tsElapsed, fcsElapsed, sw.Elapsed)
                    TotalTests = tests.Length
                    AffectedTests = tests.Length
                    Trigger = trigger
                    Timestamp = System.DateTimeOffset.UtcNow
                  }
                  dispatch (SageFsMsg.Event (TuiEvent.TestCycleTimingRecorded timing))
                  Instrumentation.succeedSpan testCycleSpan
                  Instrumentation.testExecutionActiveCount.Add(-1L)
              with ex ->
                sw.Stop()
                Instrumentation.failSpan testCycleSpan ex.Message
                // Transport failure: mark ONLY the tests that never reported, and
                // as never-reported — the connection failed, not the tests.
                // Tests that already streamed a result keep it (no double report).
                let errResults =
                  Features.LiveTesting.TestRunResult.neverReported
                    (Features.LiveTesting.NoResultReason.TransportFailed ex.Message)
                    System.DateTimeOffset.UtcNow
                    tests
                    (receivedIds |> Set.ofSeq)
                match errResults.Length with
                | 0 ->
                  Utils.Log.warn
                    "[LiveTesting] Transport failure after all tests reported: %s" ex.Message
                | _ ->
                  dispatch (SageFsMsg.Event (TuiEvent.TestResultsBatch (targetSession, errResults)))
                  Utils.Log.warn
                    "[LiveTesting] Transport failure — %d of %d tests never reported: %s"
                    errResults.Length tests.Length ex.Message
                dispatch (SageFsMsg.Event (TuiEvent.TestRunCompleted targetSession))
                Instrumentation.testExecutionActiveCount.Add(-1L)
            }, ct)
      }

    | SageFsEffect.SwitchWorkflow _targetWorkflow ->
      // Phase 4 will implement the actual switch logic (create new session, migrate)
      // For now, this is a placeholder that satisfies exhaustive pattern matching
      async { () }

/// Pure dedup-key generation for the SSE state-change event.
/// Including test state fields ensures `/events` SSE fires
/// when tests change even if output/diagnostics stay the same.
module SseDedupKey =
  /// O(1) dedup key — reads cached StateVersion and CachedTestSummary
  /// instead of filtering+counting 3131 entries (was 50-100ms, now <0.01ms).
  let fromModel (model: SageFsModel) : string =
    let sb = System.Text.StringBuilder(128)
    sb.Append(model.RecentOutput.Version).Append('|') |> ignore
    let diagCount =
      model.Diagnostics |> Map.values |> Seq.sumBy List.length
    sb.Append(diagCount).Append('|') |> ignore
    sb.Append(model.Sessions.Sessions.Length).Append('|') |> ignore
    let activeSessionId = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.map SessionId.value |> Option.defaultValue ""
    sb.Append(activeSessionId).Append('|') |> ignore
    for s in model.Sessions.Sessions do
      sb.Append(s.Id).Append(':').Append(string s.Status).Append(';') |> ignore
    sb.Append('|') |> ignore
    let lt = model.LiveTesting.TestState
    let ts = lt.Cached.TestSummary
    sb.Append(ts.Total).Append(',')
      .Append(ts.Passed).Append(',')
      .Append(ts.Failed).Append(',')
      .Append(ts.Running).Append(',')
      .Append(ts.Stale).Append('|') |> ignore
    sb.Append(lt.Cached.StateVersion).Append('|') |> ignore
    let (RunGeneration gen) = lt.LastGeneration
    sb.Append(gen).Append('|') |> ignore
    for kvp in lt.RunPhases do
      sb.Append(kvp.Key).Append(':').Append(string kvp.Value).Append(';') |> ignore
    sb.Append('|') |> ignore
    match lt.Activation = LiveTestingActivation.Active with
    | true -> sb.Append('1') |> ignore
    | false -> sb.Append('0') |> ignore
    sb.ToString()
