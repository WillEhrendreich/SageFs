namespace SageFs

open System
open SageFs.WorkerProtocol
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
      let kind = match f.IsModule with | true -> "module" | false -> "namespace"
      lines.Add {
        Kind = OutputKind.Error
        Text = sprintf "  ✖ Failed to open %s (%s) — %s" f.Name kind f.ErrorMessage
        Timestamp = now; SessionId = sid
      }
      for d in f.Diagnostics do
        let loc =
          match d.FileName with
          | Some fn -> sprintf "%s:%d:%d" fn d.StartLine d.StartColumn
          | None -> "unknown"
        lines.Add {
          Kind = OutputKind.Error
          Text = sprintf "    FS%04d %s — %s" d.ErrorNumber loc d.Message
          Timestamp = now; SessionId = sid
        }
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
  let private formatDuration (ts: TimeSpan) =
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
      { Kind = OutputKind.Error
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

  let summaryLine (results: TestRunResult array) : OutputLine =
    let passed = results |> Array.filter (fun r -> match r.Result with TestResult.Passed _ -> true | _ -> false) |> Array.length
    let failed = results |> Array.filter (fun r -> match r.Result with TestResult.Failed _ -> true | _ -> false) |> Array.length
    let skipped = results |> Array.filter (fun r -> match r.Result with TestResult.Skipped _ -> true | _ -> false) |> Array.length
    let totalDuration =
      results |> Array.sumBy (fun r ->
        match r.Result with
        | TestResult.Passed d -> d.TotalMilliseconds
        | TestResult.Failed (_, d) -> d.TotalMilliseconds
        | _ -> 0.0)
    let kind = match failed > 0 with | true -> OutputKind.Error | false -> OutputKind.Info
    { Kind = kind
      Text = sprintf "🧪 Test run complete: %d passed, %d failed, %d skipped (%s)" passed failed skipped (formatDuration (TimeSpan.FromMilliseconds totalDuration))
      Timestamp = DateTime.UtcNow; SessionId = "" }

/// The unified message type for the SageFs Elm loop.
/// All state changes flow through here — user actions and system events.
[<RequireQualifiedAccess>]
type SageFsMsg =
  | Editor of EditorAction
  | Event of SageFsEvent
  | CycleTheme
  | EnableLiveTesting
  | DisableLiveTesting
  | CycleRunPolicy
  | ToggleCoverage
  | TestCycleTick of now: DateTimeOffset
  | FileContentChanged of filePath: string * content: string
  | FcsTypeCheckCompleted of Features.LiveTesting.FcsTypeCheckResult
  | RestoreTestCache of Features.LiveTesting.LiveTestState

/// Side effects the Elm loop can request.
/// Wraps EditorEffect and TestCycleEffect for async execution.
[<RequireQualifiedAccess>]
type SageFsEffect =
  | Editor of EditorEffect
  | TestCycle of Features.LiveTesting.TestCycleEffect

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
  /// Accumulates test results from batches for the summary on TestRunCompleted.
  PendingTestResults: Features.LiveTesting.TestRunResult list
}

module SageFsModel =
  let initial () = {
    Editor = EditorState.initial
    Sessions = {
      Sessions = []
      ActiveSessionId = ActiveSession.AwaitingSession
      TotalEvals = 0
      WatchStatus = None
      Standby = StandbyInfo.NoPool
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
    PendingTestResults = []
  }

  /// Add a single output line, routing to the correct session buffer.
  let addOutputLine (line: OutputLine) (store: SessionOutputStore) =
    store.Add(line)
    store

  /// Add multiple output lines, routing each to the correct session buffer.
  let addOutput (lines: OutputLine list) (store: SessionOutputStore) =
    store.AddRange(lines)
    store

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

  /// Every LiveTestState mutation that affects test lifecycle MUST recompute StatusEntries.
  /// This helper encodes that invariant in one place.
  let recomputeStatuses (lt: Features.LiveTesting.LiveTestCycleState) (updateState: Features.LiveTesting.LiveTestState -> Features.LiveTesting.LiveTestState) =
    let previous =
      lt.TestState.StatusEntries
      |> Array.map (fun e -> e.TestId, e.Status)
      |> Map.ofArray
    let updated = updateState lt.TestState
    let withStatuses = { updated with StatusEntries = Features.LiveTesting.LiveTesting.computeStatusEntriesWithHistory previous updated }
    let summary = Features.LiveTesting.TestSummary.fromStatuses withStatuses.Activation (withStatuses.StatusEntries |> Array.map (fun e -> e.Status))
    let withCache = { withStatuses with StateVersion = lt.TestState.StateVersion + 1L; CachedTestSummary = summary }
    let withNarratives =
      let narratives =
        Features.LiveTesting.LiveTesting.computeFailureNarratives
          System.DateTimeOffset.UtcNow lt.ChangedSymbols [] withCache.FailureNarratives withCache
      { withCache with FailureNarratives = narratives }
    let withAnnotations = { withNarratives with CachedEditorAnnotations = Features.LiveTesting.LiveTesting.recomputeEditorAnnotations lt.ActiveFile withNarratives }
    { lt with TestState = withAnnotations }

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
        { model with
            Editor = newEditor
            CreatingSession = model.CreatingSession || isCreating },
        effects |> List.map SageFsEffect.Editor

    | SageFsMsg.Event event ->
      match event with
      | SageFsEvent.EvalCompleted (sid, output, diags) ->
        let line = {
          Kind = OutputKind.Result
          Text = output
          Timestamp = DateTime.UtcNow
          SessionId = sid
        }
        { model with
            RecentOutput = SageFsModel.addOutputLine line model.RecentOutput
            Diagnostics = model.Diagnostics |> Map.add sid diags }, []

      | SageFsEvent.EvalFailed (sid, error) ->
        let line = {
          Kind = OutputKind.Error
          Text = error
          Timestamp = DateTime.UtcNow
          SessionId = sid
        }
        let clearCreating = error.Contains "Create failed:"
        { model with
            RecentOutput = SageFsModel.addOutputLine line model.RecentOutput
            CreatingSession = match clearCreating with | true -> false | false -> model.CreatingSession }, []

      | SageFsEvent.EvalStarted (sid, code) ->
        let line = {
          Kind = OutputKind.Info
          Text = code
          Timestamp = DateTime.UtcNow
          SessionId = sid
        }
        { model with RecentOutput = SageFsModel.addOutputLine line model.RecentOutput }, []

      | SageFsEvent.EvalCancelled sid ->
        let line = {
          Kind = OutputKind.Info
          Text = "Eval cancelled"
          Timestamp = DateTime.UtcNow
          SessionId = sid
        }
        { model with RecentOutput = SageFsModel.addOutputLine line model.RecentOutput }, []

      | SageFsEvent.OutputEmitted line ->
        { model with RecentOutput = SageFsModel.addOutputLine line model.RecentOutput }, []

      | SageFsEvent.CompletionReady items ->
        let menu = {
          Items = items
          SelectedIndex = 0
          FilterText = ""
        }
        { model with
            Editor = { model.Editor with CompletionMenu = Some menu } }, []

      | SageFsEvent.DiagnosticsUpdated (sid, diags) ->
        { model with Diagnostics = model.Diagnostics |> Map.add sid diags }, []

      | SageFsEvent.SessionCreated snap ->
        let isFirst = model.Sessions.ActiveSessionId = ActiveSession.AwaitingSession
        let snap = match isFirst with | true -> { snap with IsActive = true } | false -> snap
        let existing = model.Sessions.Sessions |> List.exists (fun s -> s.Id = snap.Id)
        let sessions =
          match existing with
          | true ->
            model.Sessions.Sessions |> List.map (fun s -> match s.Id = snap.Id with | true -> snap | false -> s)
          | false ->
            snap :: model.Sessions.Sessions
        { model with
            CreatingSession = false
            Sessions = {
              model.Sessions with
                Sessions = sessions
                ActiveSessionId =
                  match isFirst with
                  | true -> ActiveSession.Viewing snap.Id
                  | false -> model.Sessions.ActiveSessionId } }, []

      | SageFsEvent.SessionsRefreshed snaps ->
        let activeId = model.Sessions.ActiveSessionId
        let merged =
          snaps |> List.map (fun snap ->
            let isActive =
              match activeId with
              | ActiveSession.Viewing id -> id = snap.Id
              | _ -> false
            { snap with IsActive = isActive })
        let activeId' =
          match activeId with
          | ActiveSession.AwaitingSession when not (List.isEmpty merged) ->
            ActiveSession.Viewing merged.Head.Id
          | _ -> activeId
        { model with
            Sessions = {
              model.Sessions with
                Sessions = merged
                ActiveSessionId = activeId' } }, []

      | SageFsEvent.SessionStatusChanged (sessionId, status) ->
        { model with
            Sessions = {
              model.Sessions with
                Sessions =
                  model.Sessions.Sessions
                  |> List.map (fun s ->
                    match SessionId.value s.Id = sessionId with
                    | true -> { s with Status = status }
                    | false -> s) } }, []

      | SageFsEvent.SessionSwitched (_, toIdStr) ->
        match SessionId.validate toIdStr with
        | Error _ -> model, []
        | Ok toId ->
        { model with
            SessionContext = None
            Sessions = {
              model.Sessions with
                ActiveSessionId = ActiveSession.Viewing toId
                Sessions =
                  model.Sessions.Sessions
                  |> List.map (fun s ->
                    { s with IsActive = s.Id = toId }) } }, []

      | SageFsEvent.SessionStopped sessionId ->
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
        let remaining =
          remaining
          |> List.map (fun s -> { s with IsActive = ActiveSession.isViewing s.Id newActive })
        let clearedMap =
          model.LiveTesting.TestState.TestSessionMap
          |> Map.filter (fun _ sid -> sid <> sessionId)
        let lt =
          { model.LiveTesting with
              TestState = { model.LiveTesting.TestState with TestSessionMap = clearedMap } }
        { model with
            Sessions = {
              model.Sessions with
                Sessions = remaining
                ActiveSessionId = newActive }
            LiveTesting = lt
            Diagnostics = model.Diagnostics |> Map.remove sessionId }, []

      | SageFsEvent.SessionStale (sessionId, _) ->
        { model with
            Sessions = {
              model.Sessions with
                Sessions =
                  model.Sessions.Sessions
                  |> List.map (fun s ->
                    match SessionId.value s.Id = sessionId with
                    | true -> { s with Status = SessionDisplayStatus.Stale }
                    | false -> s) } }, []

      | SageFsEvent.FileChanged _ -> model, []

      | SageFsEvent.FileReloaded (path, _, result) ->
        let activeId = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.map SessionId.value |> Option.defaultValue ""
        let line =
          match result with
          | Ok msg ->
            { Kind = OutputKind.Info
              Text = sprintf "Reloaded %s: %s" path msg
              Timestamp = DateTime.UtcNow
              SessionId = activeId }
          | Error err ->
            { Kind = OutputKind.Error
              Text = sprintf "Reload failed %s: %s" path err
              Timestamp = DateTime.UtcNow
              SessionId = activeId }
        { model with RecentOutput = SageFsModel.addOutputLine line model.RecentOutput }, []

      | SageFsEvent.WarmupProgress(step, total, msg) ->
        let activeId = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.map SessionId.value |> Option.defaultValue ""
        let line = {
          Kind = OutputKind.Info
          Text = sprintf "⏳ [%d/%d] %s" step total msg
          Timestamp = DateTime.UtcNow
          SessionId = activeId }
        { model with RecentOutput = SageFsModel.addOutputLine line model.RecentOutput }, []

      | SageFsEvent.WarmupCompleted (_, failures) ->
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
              { Kind = OutputKind.Error
                Text = sprintf "Warmup failure: %s" f
                Timestamp = DateTime.UtcNow
                SessionId = activeId })
          { model with RecentOutput = SageFsModel.addOutput lines model.RecentOutput }, []

      | SageFsEvent.WarmupContextUpdated ctx ->
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
      | SageFsEvent.TestLocationsDetected (_, locations) ->
        let lt = recomputeStatuses model.LiveTesting (fun s ->
          let merged =
            match Array.isEmpty s.DiscoveredTests with
            | true -> s.DiscoveredTests
            | false -> Features.LiveTesting.SourceMapping.mergeSourceLocations locations s.DiscoveredTests
          { s with SourceLocations = locations; DiscoveredTests = merged })
        { model with LiveTesting = lt }, []

      | SageFsEvent.TestsDiscovered (sessionId, tests) ->
        let lt = recomputeStatuses model.LiveTesting (fun s ->
          let disc = Features.LiveTesting.LiveTesting.mergeDiscoveredTests s.DiscoveredTests tests
          let withSourceMap =
            match Array.isEmpty s.SourceLocations with
            | true ->
              // No tree-sitter yet — map tests to files using module name → file name heuristic
              let sourceFiles =
                match model.SessionContext with
                | Some ctx -> ctx.FileStatuses |> List.map (fun f -> f.Path) |> Array.ofList
                | None -> [||]
              Features.LiveTesting.SourceMapping.mapFromProjectFiles sourceFiles disc
            | false -> Features.LiveTesting.SourceMapping.mergeSourceLocations s.SourceLocations disc
          let newSessionMap =
            tests |> Array.fold (fun m tc -> Map.add tc.Id sessionId m) s.TestSessionMap
          { s with DiscoveredTests = withSourceMap; TestSessionMap = newSessionMap })
        let effects =
          match lt.TestState.Activation = Features.LiveTesting.LiveTestingActivation.Active
                && not (Array.isEmpty tests) with
          | true ->
            // Only trigger execution for the INCOMING session's tests, not all discovered.
            // Other sessions' tests belong to different workers and would return NotRun.
            let incomingIds = tests |> Array.map (fun tc -> tc.Id)
            Features.LiveTesting.LiveTestCycleState.triggerExecutionForAffected
              incomingIds Features.LiveTesting.RunTrigger.FileSave (Some sessionId) lt
            |> List.map SageFsEffect.TestCycle
          | false -> []
        { model with LiveTesting = lt }, effects

      | SageFsEvent.TestRunStarted (testIds, sessionId) ->
        let lt = recomputeStatuses model.LiveTesting (fun s ->
          let phase, gen = TestRunPhase.startRun s.LastGeneration
          let phases =
            match sessionId with
            | Some sid -> s.RunPhases |> Map.add sid phase
            | None -> s.RunPhases
          { s with LastGeneration = gen; AffectedTests = Set.ofArray testIds; RunPhases = phases })
        { model with LiveTesting = lt }, []

      | SageFsEvent.TestResultsBatch results ->
        let merged = Features.LiveTesting.LiveTesting.mergeResults model.LiveTesting.TestState results
        let lt = recomputeStatuses model.LiveTesting (fun _ -> merged)
        let outputLines = TestOutputFormatter.toOutputLines results
        { model with
            LiveTesting = lt
            PendingTestResults = model.PendingTestResults @ (Array.toList results)
            RecentOutput = SageFsModel.addOutput (List.rev outputLines) model.RecentOutput }, []

      | SageFsEvent.TestRunCompleted sessionId ->
        let lt = recomputeStatuses model.LiveTesting (fun s ->
          let phases =
            match sessionId with
            | Some sid -> s.RunPhases |> Map.add sid Features.LiveTesting.TestRunPhase.Idle
            | None -> s.RunPhases
          { s with AffectedTests = Set.empty; RunPhases = phases })
        let summary = TestOutputFormatter.summaryLine (model.PendingTestResults |> Array.ofList)
        { model with
            LiveTesting = lt
            PendingTestResults = []
            RecentOutput = SageFsModel.addOutputLine summary model.RecentOutput }, []

      | SageFsEvent.LiveTestingEnabled ->
        let lt = recomputeStatuses model.LiveTesting (fun s -> { s with Activation = Features.LiveTesting.LiveTestingActivation.Active })
        { model with LiveTesting = lt }, []

      | SageFsEvent.LiveTestingDisabled ->
        let lt = recomputeStatuses model.LiveTesting (fun s -> { s with Activation = Features.LiveTesting.LiveTestingActivation.Inactive })
        { model with LiveTesting = lt }, []

      | SageFsEvent.AffectedTestsComputed testIds ->
        let lt = recomputeStatuses model.LiveTesting (fun s -> { s with AffectedTests = Set.ofArray testIds })
        let targetSession =
          testIds |> Array.tryPick (fun tid -> Map.tryFind tid lt.TestState.TestSessionMap)
        let effects =
          Features.LiveTesting.LiveTestCycleState.triggerExecutionForAffected
            testIds Features.LiveTesting.RunTrigger.FileSave targetSession lt
          |> List.map SageFsEffect.TestCycle
        { model with LiveTesting = lt }, effects

      | SageFsEvent.RunTestsRequested tests ->
        let testIds = tests |> Array.map (fun t -> t.Id)
        let lt = recomputeStatuses model.LiveTesting (fun s ->
          let phase, gen = TestRunPhase.startRun s.LastGeneration
          let sessionIds =
            testIds
            |> Array.choose (fun tid -> Map.tryFind tid s.TestSessionMap)
            |> Array.distinct
          let phases =
            sessionIds |> Array.fold (fun m sid -> Map.add sid phase m) s.RunPhases
          { s with LastGeneration = gen; AffectedTests = Set.ofArray testIds; RunPhases = phases })
        let effects =
          match Array.isEmpty tests || lt.TestState.Activation = Features.LiveTesting.LiveTestingActivation.Inactive with
          | true -> []
          | false ->
            let sessionMap = lt.TestState.TestSessionMap
            tests
            |> Array.groupBy (fun tc ->
              match Map.tryFind tc.Id sessionMap with
              | Some sid -> sid
              | None -> "")
            |> Array.toList
            |> List.map (fun (sid, groupTests) ->
              let targetSession = match System.String.IsNullOrEmpty sid with | true -> None | false -> Some sid
              let sessionMaps =
                match targetSession |> Option.bind (fun s -> Map.tryFind s lt.InstrumentationMaps) with
                | Some maps -> maps
                | None -> lt.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
              Features.LiveTesting.TestCycleEffect.RunAffectedTests(
                groupTests, Features.LiveTesting.RunTrigger.ExplicitRun,
                System.TimeSpan.Zero, System.TimeSpan.Zero, targetSession, sessionMaps)
              |> SageFsEffect.TestCycle)
        { model with LiveTesting = lt }, effects

      | SageFsEvent.CoverageUpdated coverage ->
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
              Status = status })
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with CoverageAnnotations = annotations } } }, []

      | SageFsEvent.CoverageBitmapCollected (testIds, bitmap) ->
        Instrumentation.coverageBitmapsCollected.Add(1L)
        let lt = model.LiveTesting
        let bitmaps =
          testIds |> Array.fold (fun acc tid -> Map.add tid bitmap acc) lt.TestState.TestCoverageBitmaps
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with TestCoverageBitmaps = bitmaps } } }, []

      | SageFsEvent.RunPolicyChanged (category, policy) ->
        let lt = recomputeStatuses model.LiveTesting (fun s -> { s with RunPolicies = Map.add category policy s.RunPolicies })
        { model with LiveTesting = lt }, []

      | SageFsEvent.InstrumentationMapsReady (sessionId, maps) ->
        Instrumentation.coverageMapsReceived.Add(1L)
        let totalProbes = maps |> Array.sumBy (fun m -> m.TotalProbes) |> int64
        Instrumentation.coverageProbesTotal.Add(totalProbes)
        let lt = model.LiveTesting
        { model with LiveTesting = { lt with InstrumentationMaps = Map.add sessionId maps lt.InstrumentationMaps } }, []

      | SageFsEvent.ProvidersDetected providers ->
        let lt = model.LiveTesting
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with DetectedProviders = providers } } }, []

      | SageFsEvent.TestCycleTimingRecorded timing ->
        { model with
            LiveTesting = { model.LiveTesting with LastTiming = Some timing } }, []

      | SageFsEvent.AssemblyLoadFailed errors ->
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
        let lt = recomputeStatuses model.LiveTesting (fun s -> { s with Activation = Features.LiveTesting.LiveTestingActivation.Active })
        let effects =
          match Array.isEmpty lt.TestState.DiscoveredTests with
          | true -> []
          | false ->
            let sessionMap = lt.TestState.TestSessionMap
            lt.TestState.DiscoveredTests
            |> Array.groupBy (fun tc ->
              match Map.tryFind tc.Id sessionMap with
              | Some sid -> sid
              | None -> "")
            |> Array.toList
            |> List.collect (fun (sid, groupTests) ->
              let targetSession = match System.String.IsNullOrEmpty sid with | true -> None | false -> Some sid
              let groupIds = groupTests |> Array.map (fun tc -> tc.Id)
              Features.LiveTesting.LiveTestCycleState.triggerExecutionForAffected
                groupIds Features.LiveTesting.RunTrigger.ExplicitRun targetSession lt
              |> List.map SageFsEffect.TestCycle)
        { model with LiveTesting = lt }, effects

    | SageFsMsg.DisableLiveTesting ->
      match model.LiveTesting.TestState.Activation = Features.LiveTesting.LiveTestingActivation.Inactive with
      | true ->
        model, []
      | false ->
        let lt = recomputeStatuses model.LiveTesting (fun s -> { s with Activation = Features.LiveTesting.LiveTestingActivation.Inactive })
        { model with LiveTesting = lt }, []

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
      let effects, cycle' = model.LiveTesting |> Features.LiveTesting.LiveTestCycleState.tick now
      // Return same model reference when tick is a no-op (enables ElmLoop skip)
      match effects.IsEmpty && obj.ReferenceEquals(cycle', model.LiveTesting) with
      | true -> model, []
      | false ->
        let mappedEffects = effects |> List.map SageFsEffect.TestCycle
        { model with LiveTesting = cycle' }, mappedEffects

    | SageFsMsg.FileContentChanged (filePath, content) ->
      match model.LiveTesting.TestState.Activation = Features.LiveTesting.LiveTestingActivation.Active with
      | true ->
        let now = DateTimeOffset.UtcNow
        let cycle' =
          model.LiveTesting
          |> Features.LiveTesting.LiveTestCycleState.onKeystroke content filePath now
        { model with LiveTesting = cycle' }, []
      | false ->
        model, []

    | SageFsMsg.FcsTypeCheckCompleted result ->
      let effects, cycle' =
        model.LiveTesting
        |> Features.LiveTesting.LiveTestCycleState.handleFcsResult result
      // OTEL: track dep graph match vs fallback rate
      match effects with
      | [] ->
        Features.LiveTesting.LiveTestingInstrumentation.depGraphFallbackTotal.Add(1L)
      | _ ->
        Features.LiveTesting.LiveTestingInstrumentation.depGraphMatchTotal.Add(1L)
        let affectedCount =
          effects |> List.sumBy (fun e ->
            match e with
            | Features.LiveTesting.TestCycleEffect.RunAffectedTests (tests, _, _, _, _, _) -> tests.Length
            | _ -> 0)
        Features.LiveTesting.LiveTestingInstrumentation.depGraphAffectedCount.Record(affectedCount)
      let mappedEffects = effects |> List.map SageFsEffect.TestCycle
      { model with LiveTesting = cycle' }, mappedEffects

    | SageFsMsg.RestoreTestCache cachedState ->
      let lt = recomputeStatuses model.LiveTesting (fun s ->
        { s with
            TestCoverageBitmaps = cachedState.TestCoverageBitmaps
            LastResults = cachedState.LastResults
            LastGeneration = cachedState.LastGeneration })
      { model with LiveTesting = lt }, []

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
    let editorAnnotations = model.LiveTesting.TestState.CachedEditorAnnotations
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
        model.Sessions.Sessions
        |> List.mapi (fun i s ->
          let statusLabel =
            match s.Status with
            | SessionDisplayStatus.Running -> "running"
            | SessionDisplayStatus.Starting -> "starting"
            | SessionDisplayStatus.Errored r -> sprintf "error: %s" r
            | SessionDisplayStatus.Suspended -> "suspended"
            | SessionDisplayStatus.Stale -> "stale"
            | SessionDisplayStatus.Restarting -> "restarting"
          let active = match s.IsActive with | true -> " *" | false -> ""
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
          | true -> sprintf "%s%s\n... ↑↓ nav · Enter switch · Del stop · Ctrl+N new · Ctrl+Alt+A auto-open off · ^Tab cycle" s creatingLine
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

    [ editorRegion; outputRegion; diagnosticsRegion; sessionsRegion; contextRegion ]

/// Dependencies the effect handler needs — injected, not hard-coded.
/// This is the seam between pure Elm and impure infrastructure.
type EffectDeps = {
  /// Resolve which session to target
  ResolveSession: SessionId option -> Result<SessionOperations.SessionResolution, SageFsError>
  /// Get the proxy for a session
  GetProxy: SessionId -> SessionProxy option
  /// Get a streaming test execution proxy for a session.
  /// The proxy streams test results and IL coverage hits.
  GetStreamingTestProxy: SessionId -> (Features.LiveTesting.TestCase array -> int -> (Features.LiveTesting.TestRunResult -> unit) -> (bool array -> unit) -> Async<unit>) option
  /// Create a new session
  CreateSession: string list -> string -> Async<Result<SessionInfo, SageFsError>>
  /// Ensure the working directory has warmup auto-open disabled.
  ConfigureWarmupAutoOpen: string -> Async<Result<OutputLine, string>>
  /// Stop a session
  StopSession: SessionId -> Async<Result<unit, SageFsError>>
  /// List all sessions
  ListSessions: unit -> Async<SessionInfo list>
  /// Fetch warmup context for a session (optional — None disables warmup dispatch)
  GetWarmupContext: (SessionId -> Async<SessionContext option>) option
  /// Test cycle cancellation for stale work
  TestCycleCancellation: Features.LiveTesting.TestCycleCancellation
}

/// Routes SageFsEffect to real infrastructure via injected deps.
/// Converts WorkerResponses back into SageFsMsg for the Elm loop.
module SageFsEffectHandler =

  let newReplyId () =
    Guid.NewGuid().ToString("N").[..7]

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
        })
      SageFsMsg.Event (
        SageFsEvent.EvalCompleted (SessionId.value sessionId, output, diagnostics))
    | WorkerResponse.EvalResult (_, Error err, _, _) ->
      SageFsMsg.Event (
        SageFsEvent.EvalFailed (SessionId.value sessionId, SageFsError.describe err))
    | WorkerResponse.EvalCancelled _ ->
      SageFsMsg.Event (SageFsEvent.EvalCancelled (SessionId.value sessionId))
    | other ->
      SageFsMsg.Event (
        SageFsEvent.EvalFailed (
          SessionId.value sessionId, sprintf "Unexpected response: %A" other))

  let completionResponseToMsg
    (response: WorkerResponse) : SageFsMsg =
    match response with
    | WorkerResponse.CompletionResult (_, items) ->
      let completionItems =
        items |> List.map (fun label ->
          { Label = label; Kind = "member"; Detail = None })
      SageFsMsg.Event (SageFsEvent.CompletionReady completionItems)
    | _ ->
      SageFsMsg.Event (SageFsEvent.CompletionReady [])

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
            SageFsEvent.EvalFailed (
              SessionId.value id, sprintf "No proxy for session %s" (SessionId.value id))))
      | Error err ->
        dispatch (SageFsMsg.Event (
          SageFsEvent.EvalFailed ("", SageFsError.describe err)))
    }

  let sessionInfoToSnapshot (info: SessionInfo) : SessionSnapshot =
    { Id = info.Id
      Name = info.Name
      Projects = info.Projects
      Status =
        match info.Status with
        | SessionStatus.Ready -> SessionDisplayStatus.Running
        | SessionStatus.Starting -> SessionDisplayStatus.Starting
        | SessionStatus.Evaluating -> SessionDisplayStatus.Running
        | SessionStatus.Building _ -> SessionDisplayStatus.Running
        | SessionStatus.Faulted -> SessionDisplayStatus.Errored "faulted"
        | SessionStatus.Restarting -> SessionDisplayStatus.Restarting
        | SessionStatus.Stopped -> SessionDisplayStatus.Suspended
      LastActivity = info.LastActivity
      EvalCount = 0
      UpSince = info.CreatedAt
      IsActive = false
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
          dispatch (SageFsMsg.Event (SageFsEvent.SessionsRefreshed snaps))
          // Fetch warmup context for the active Ready session
          match deps.GetWarmupContext with
          | Some getCtx ->
            let readySession =
              sessions |> List.tryFind (fun s -> s.Status = SessionStatus.Ready)
            match readySession with
            | Some info ->
              let! ctx = getCtx info.Id
              match ctx with
              | Some sessionCtx ->
                dispatch (SageFsMsg.Event (SageFsEvent.WarmupContextUpdated sessionCtx))
              | None -> ()
            | None -> ()
          | None -> ()
        }

      | EditorEffect.RequestSessionSwitch sessionId ->
        async {
          dispatch (SageFsMsg.Event (
            SageFsEvent.SessionSwitched (None, sessionId)))
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
          let! result = deps.CreateSession projectList workingDir
          match result with
          | Ok info ->
            dispatch (SageFsMsg.Event (
              SageFsEvent.SessionCreated (sessionInfoToSnapshot info)))
            dispatch (SageFsMsg.Event (
              SageFsEvent.SessionSwitched (None, SessionId.value info.Id)))
          | Error err ->
            dispatch (SageFsMsg.Event (
              SageFsEvent.EvalFailed (
                "", sprintf "Create failed: %s" (SageFsError.describe err))))
        }

      | EditorEffect.RequestConfigureWarmupAutoOpen workingDir ->
        async {
          let! result = deps.ConfigureWarmupAutoOpen workingDir
          match result with
          | Ok line ->
            dispatch (SageFsMsg.Event (SageFsEvent.OutputEmitted line))
          | Error err ->
            dispatch (SageFsMsg.Event (SageFsEvent.EvalFailed ("", err)))
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
                SageFsEvent.SessionStopped sessionIdStr))
            | Error err ->
              dispatch (SageFsMsg.Event (
                SageFsEvent.EvalFailed (
                  sessionIdStr,
                  sprintf "Stop failed: %s" (SageFsError.describe err))))
        }

      | EditorEffect.RequestReset ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let replyId = newReplyId ()
            let! _ = proxy (WorkerMessage.ResetSession replyId)
            dispatch (SageFsMsg.Event (
              SageFsEvent.SessionStatusChanged (SessionId.value sid, SessionDisplayStatus.Starting)))
          })

      | EditorEffect.RequestHardReset ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let replyId = newReplyId ()
            let! _ = proxy (WorkerMessage.HardResetSession (false, replyId))
            dispatch (SageFsMsg.Event (
              SageFsEvent.SessionStatusChanged (SessionId.value sid, SessionDisplayStatus.Restarting)))
          })

    | SageFsEffect.TestCycle testCycleEffect ->
      async {
        match testCycleEffect with
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
          dispatch (SageFsMsg.Event (SageFsEvent.TestLocationsDetected ("", locations)))
          let timing : Features.LiveTesting.TestCycleTiming = {
            Depth = Features.LiveTesting.TestCycleDepth.TreeSitterOnly elapsed
            TotalTests = 0; AffectedTests = 0
            Trigger = Features.LiveTesting.RunTrigger.Keystroke
            Timestamp = System.DateTimeOffset.UtcNow
          }
          dispatch (SageFsMsg.Event (SageFsEvent.TestCycleTimingRecorded timing))
        | Features.LiveTesting.TestCycleEffect.RequestFcsTypeCheck (filePath, tsElapsed) ->
          let span = Instrumentation.startSpan Instrumentation.testCycleSource "test_cycle.fcs.typecheck" ["file", box filePath]
          let fcsStopwatch = System.Diagnostics.Stopwatch.StartNew()
          do! withSession deps dispatch None (fun _sid proxy ->
            async {
              let code =
                try System.IO.File.ReadAllText(filePath)
                with ex ->
                  Utils.Log.warn "[SageFsApp] File read failed: %s" ex.Message
                  ""
              match code <> "" with
              | true ->
                let replyId = newReplyId ()
                let! resp = proxy (WorkerMessage.TypeCheckWithSymbols(code, filePath, replyId))
                fcsStopwatch.Stop()
                Instrumentation.fcsTypecheckMs.Record(fcsStopwatch.Elapsed.TotalMilliseconds)
                Features.LiveTesting.LiveTestingInstrumentation.fcsHistogram.Record(fcsStopwatch.Elapsed.TotalMilliseconds)
                let result =
                  match resp with
                  | WorkerResponse.TypeCheckWithSymbolsResult(_rid, hasErrors, _diags, symRefs) ->
                    match hasErrors with
                    | true ->
                      Features.LiveTesting.FcsTypeCheckResult.Failed(filePath, [])
                    | false ->
                      let refs = symRefs |> List.map WorkerProtocol.WorkerSymbolRef.toDomain
                      Features.LiveTesting.FcsTypeCheckResult.Success(filePath, refs)
                  | _ ->
                    Features.LiveTesting.FcsTypeCheckResult.Cancelled filePath
                dispatch (SageFsMsg.FcsTypeCheckCompleted result)
                let timing : Features.LiveTesting.TestCycleTiming = {
                  Depth = Features.LiveTesting.TestCycleDepth.ThroughFcs(tsElapsed, fcsStopwatch.Elapsed)
                  TotalTests = 0; AffectedTests = 0
                  Trigger = Features.LiveTesting.RunTrigger.Keystroke
                  Timestamp = System.DateTimeOffset.UtcNow
                }
                dispatch (SageFsMsg.Event (SageFsEvent.TestCycleTimingRecorded timing))
                Instrumentation.succeedSpan span
              | false -> ()
            })
        | Features.LiveTesting.TestCycleEffect.RunAffectedTests (tests, trigger, tsElapsed, fcsElapsed, targetSession, instrumentationMaps) ->
          match Array.isEmpty tests with
          | true -> ()
          | false ->
            let testIds = tests |> Array.map (fun tc -> tc.Id)
            dispatch (SageFsMsg.Event (SageFsEvent.TestRunStarted (testIds, targetSession)))
            let ct = deps.TestCycleCancellation.TestRun.next()
            let hasInstrMaps = not (Array.isEmpty instrumentationMaps)
            let testCycleSpan = Instrumentation.startSpan Instrumentation.testCycleSource "test_cycle.test.execution" ["test.count", box tests.Length; "trigger", box (sprintf "%A" trigger); "coverage.has_maps", box hasInstrMaps; "coverage.probe_count", box (instrumentationMaps |> Array.sumBy (fun m -> m.TotalProbes))]
            Async.Start(async {
              Instrumentation.testExecutionActiveCount.Add(1L)
              use activity =
                Features.LiveTesting.LiveTestingInstrumentation.activitySource.StartActivity(
                  "SageFs.LiveTesting.TestExecution")
              let sw = System.Diagnostics.Stopwatch.StartNew()
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
                        dispatch (SageFsMsg.Event (SageFsEvent.TestResultsBatch batch))
                      )
                    let onResult (result: Features.LiveTesting.TestRunResult) =
                      resultFlusher.Add(result)
                    let onCoverage (hits: bool array) =
                      let mergedMap = Features.LiveTesting.InstrumentationMap.merge instrumentationMaps
                      match mergedMap.TotalProbes > 0 && hits.Length = mergedMap.TotalProbes with
                      | true ->
                        let coverage = Features.LiveTesting.InstrumentationMap.toCoverageState hits mergedMap
                        dispatch (SageFsMsg.Event (SageFsEvent.CoverageUpdated coverage))
                        let bitmap = Features.LiveTesting.CoverageBitmap.ofBoolArray hits
                        dispatch (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected (testIds, bitmap)))
                        match activity <> null with
                        | true ->
                          activity.SetTag("coverage.total_probes", hits.Length) |> ignore
                          activity.SetTag("coverage.hit_probes", Features.LiveTesting.CoverageBitmap.popCount bitmap) |> ignore
                          activity.SetTag("coverage.tests_in_batch", testIds.Length) |> ignore
                        | false -> ()
                      | false -> ()
                    let parallelism = max 4 (Environment.ProcessorCount / 2)
                    do! streamProxy tests parallelism onResult onCoverage
                    // BatchFlusher's Dispose (via 'use') flushes remaining results
                  | None ->
                    let notRunResults =
                      tests |> Array.map (fun tc ->
                        { TestId = tc.Id
                          TestName = tc.FullName
                          Result = Features.LiveTesting.TestResult.NotRun
                          Timestamp = System.DateTimeOffset.UtcNow
                          Output = None }
                        : Features.LiveTesting.TestRunResult)
                    dispatch (SageFsMsg.Event (SageFsEvent.TestResultsBatch notRunResults))
                | Error _ ->
                  let notRunResults =
                    tests |> Array.map (fun tc ->
                      { TestId = tc.Id
                        TestName = tc.FullName
                        Result = Features.LiveTesting.TestResult.NotRun
                        Timestamp = System.DateTimeOffset.UtcNow
                        Output = None }
                      : Features.LiveTesting.TestRunResult)
                  dispatch (SageFsMsg.Event (SageFsEvent.TestResultsBatch notRunResults))
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
                dispatch (SageFsMsg.Event (SageFsEvent.TestRunCompleted targetSession))
                let timing : Features.LiveTesting.TestCycleTiming = {
                  Depth = Features.LiveTesting.TestCycleDepth.ThroughExecution(
                            tsElapsed, fcsElapsed, sw.Elapsed)
                  TotalTests = tests.Length
                  AffectedTests = tests.Length
                  Trigger = trigger
                  Timestamp = System.DateTimeOffset.UtcNow
                }
                dispatch (SageFsMsg.Event (SageFsEvent.TestCycleTimingRecorded timing))
                Instrumentation.succeedSpan testCycleSpan
                Instrumentation.testExecutionActiveCount.Add(-1L)
              with ex ->
                sw.Stop()
                Instrumentation.failSpan testCycleSpan ex.Message
                let errResults =
                  tests |> Array.map (fun tc ->
                    ({ TestId = tc.Id
                       TestName = tc.FullName
                       Result =
                         Features.LiveTesting.TestResult.Failed(
                           Features.LiveTesting.TestFailure.ExceptionThrown(
                             ex.Message,
                             ex.StackTrace |> Option.ofObj |> Option.defaultValue ""),
                           System.TimeSpan.Zero)
                       Timestamp = System.DateTimeOffset.UtcNow
                       Output = None }
                     : Features.LiveTesting.TestRunResult))
                dispatch (SageFsMsg.Event (SageFsEvent.TestResultsBatch errResults))
                dispatch (SageFsMsg.Event (SageFsEvent.TestRunCompleted targetSession))
                Instrumentation.testExecutionActiveCount.Add(-1L)
            }, ct)
      }

/// Pure dedup-key generation for the SSE state-change event.
/// Including test state fields ensures `/events` SSE fires
/// when tests change even if output/diagnostics stay the same.
module SseDedupKey =
  /// O(1) dedup key — reads cached StateVersion and CachedTestSummary
  /// instead of filtering+counting 3131 entries (was 50-100ms, now <0.01ms).
  let fromModel (model: SageFsModel) : string =
    let sb = System.Text.StringBuilder(128)
    sb.Append(model.RecentOutput.ActiveVersion(model.Sessions.ActiveSessionId)).Append('|') |> ignore
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
    let ts = lt.CachedTestSummary
    sb.Append(ts.Total).Append(',')
      .Append(ts.Passed).Append(',')
      .Append(ts.Failed).Append(',')
      .Append(ts.Running).Append(',')
      .Append(ts.Stale).Append('|') |> ignore
    sb.Append(lt.StateVersion).Append('|') |> ignore
    let (RunGeneration gen) = lt.LastGeneration
    sb.Append(gen).Append('|') |> ignore
    for kvp in lt.RunPhases do
      sb.Append(kvp.Key).Append(':').Append(string kvp.Value).Append(';') |> ignore
    sb.Append('|') |> ignore
    match lt.Activation = LiveTestingActivation.Active with
    | true -> sb.Append('1') |> ignore
    | false -> sb.Append('0') |> ignore
    sb.ToString()
