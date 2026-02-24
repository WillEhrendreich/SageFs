namespace SageFs

open System
open SageFs.WorkerProtocol
open SageFs.Features.Diagnostics

/// The unified message type for the SageFs Elm loop.
/// All state changes flow through here — user actions and system events.
[<RequireQualifiedAccess>]
type SageFsMsg =
  | Editor of EditorAction
  | Event of SageFsEvent
  | CycleTheme
  | ToggleLiveTesting
  | CycleRunPolicy
  | ToggleCoverage
  | PipelineTick of now: DateTimeOffset
  | FileContentChanged of filePath: string * content: string
  | FcsTypeCheckCompleted of Features.LiveTesting.FcsTypeCheckResult

/// Side effects the Elm loop can request.
/// Wraps EditorEffect and PipelineEffect for async execution.
[<RequireQualifiedAccess>]
type SageFsEffect =
  | Editor of EditorEffect
  | Pipeline of Features.LiveTesting.PipelineEffect

/// The complete application state managed by the Elm loop.
type SageFsModel = {
  Editor: EditorState
  Sessions: SessionRegistryView
  RecentOutput: OutputLine list
  Diagnostics: Map<string, Features.Diagnostics.Diagnostic list>
  CreatingSession: bool
  Theme: ThemeConfig
  ThemeName: string
  SessionContext: SessionContext option
  LiveTesting: Features.LiveTesting.LiveTestPipelineState
}

module SageFsModel =
  let initial = {
    Editor = EditorState.initial
    Sessions = {
      Sessions = []
      ActiveSessionId = ActiveSession.AwaitingSession
      TotalEvals = 0
      WatchStatus = None
      Standby = StandbyInfo.NoPool
    }
    RecentOutput = []
    Diagnostics = Map.empty
    CreatingSession = false
    Theme =
      match ThemePresets.tryFind "Kanagawa" with
      | Some t -> t
      | None -> Theme.defaults
    ThemeName = "Kanagawa"
    SessionContext = None
    LiveTesting = Features.LiveTesting.LiveTestPipelineState.empty
  }

/// Pure update function: routes SageFsMsg through the right handler.
module SageFsUpdate =
  let resolveSessionId (model: SageFsModel) : string option =
    match model.Editor.SelectedSessionIndex with
    | None -> None
    | Some idx ->
      let sessions = model.Sessions.Sessions
      if idx >= 0 && idx < sessions.Length then
        Some sessions.[idx].Id
      else None

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
          |> List.map (fun s -> SageFsEffect.Editor (EditorEffect.RequestSessionStop s.Id))
        model, others
      | EditorAction.SessionCycleNext ->
        let count = model.Sessions.Sessions.Length
        if count <= 1 then model, []
        else
          let currentIdx = model.Editor.SelectedSessionIndex |> Option.defaultValue 0
          let nextIdx = (currentIdx + 1) % count
          let sid = model.Sessions.Sessions.[nextIdx].Id
          let newEditor = { model.Editor with SelectedSessionIndex = Some nextIdx }
          { model with Editor = newEditor },
          [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch sid)]
      | EditorAction.SessionCyclePrev ->
        let count = model.Sessions.Sessions.Length
        if count <= 1 then model, []
        else
          let currentIdx = model.Editor.SelectedSessionIndex |> Option.defaultValue 0
          let prevIdx = (currentIdx - 1 + count) % count
          let sid = model.Sessions.Sessions.[prevIdx].Id
          let newEditor = { model.Editor with SelectedSessionIndex = Some prevIdx }
          { model with Editor = newEditor },
          [SageFsEffect.Editor (EditorEffect.RequestSessionSwitch sid)]
      | EditorAction.ClearOutput ->
        { model with RecentOutput = [] },
        []
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
            RecentOutput = line :: model.RecentOutput
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
            RecentOutput = line :: model.RecentOutput
            CreatingSession = if clearCreating then false else model.CreatingSession }, []

      | SageFsEvent.EvalStarted (sid, code) ->
        let line = {
          Kind = OutputKind.Info
          Text = code
          Timestamp = DateTime.UtcNow
          SessionId = sid
        }
        { model with RecentOutput = line :: model.RecentOutput }, []

      | SageFsEvent.EvalCancelled sid ->
        let line = {
          Kind = OutputKind.Info
          Text = "Eval cancelled"
          Timestamp = DateTime.UtcNow
          SessionId = sid
        }
        { model with RecentOutput = line :: model.RecentOutput }, []

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
        let snap = if isFirst then { snap with IsActive = true } else snap
        let existing = model.Sessions.Sessions |> List.exists (fun s -> s.Id = snap.Id)
        let sessions =
          if existing then
            model.Sessions.Sessions |> List.map (fun s -> if s.Id = snap.Id then snap else s)
          else
            snap :: model.Sessions.Sessions
        { model with
            CreatingSession = false
            Sessions = {
              model.Sessions with
                Sessions = sessions
                ActiveSessionId =
                  if isFirst then ActiveSession.Viewing snap.Id
                  else model.Sessions.ActiveSessionId } }, []

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
                    if s.Id = sessionId then { s with Status = status }
                    else s) } }, []

      | SageFsEvent.SessionSwitched (_, toId) ->
        { model with
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
          |> List.filter (fun s -> s.Id <> sessionId)
        let wasActive = ActiveSession.isViewing sessionId model.Sessions.ActiveSessionId
        let newActive =
          if wasActive then
            remaining
            |> List.tryHead
            |> Option.map (fun s -> ActiveSession.Viewing s.Id)
            |> Option.defaultValue ActiveSession.AwaitingSession
          else model.Sessions.ActiveSessionId
        let remaining =
          remaining
          |> List.map (fun s -> { s with IsActive = ActiveSession.isViewing s.Id newActive })
        { model with
            Sessions = {
              model.Sessions with
                Sessions = remaining
                ActiveSessionId = newActive }
            Diagnostics = model.Diagnostics |> Map.remove sessionId }, []

      | SageFsEvent.SessionStale (sessionId, _) ->
        { model with
            Sessions = {
              model.Sessions with
                Sessions =
                  model.Sessions.Sessions
                  |> List.map (fun s ->
                    if s.Id = sessionId
                    then { s with Status = SessionDisplayStatus.Stale }
                    else s) } }, []

      | SageFsEvent.FileChanged _ -> model, []

      | SageFsEvent.FileReloaded (path, _, result) ->
        let activeId = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.defaultValue ""
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
        { model with RecentOutput = line :: model.RecentOutput }, []

      | SageFsEvent.WarmupProgress(step, total, msg) ->
        let activeId = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.defaultValue ""
        let line = {
          Kind = OutputKind.Info
          Text = sprintf "⏳ [%d/%d] %s" step total msg
          Timestamp = DateTime.UtcNow
          SessionId = activeId }
        { model with RecentOutput = line :: model.RecentOutput }, []

      | SageFsEvent.WarmupCompleted (_, failures) ->
        let activeId = ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.defaultValue ""
        if failures.IsEmpty then
          let line = {
            Kind = OutputKind.Info
            Text = "Warmup complete"
            Timestamp = DateTime.UtcNow
            SessionId = activeId
          }
          { model with RecentOutput = line :: model.RecentOutput }, []
        else
          let lines =
            failures |> List.map (fun f ->
              { Kind = OutputKind.Error
                Text = sprintf "Warmup failure: %s" f
                Timestamp = DateTime.UtcNow
                SessionId = activeId })
          { model with RecentOutput = lines @ model.RecentOutput }, []

      | SageFsEvent.WarmupContextUpdated ctx ->
        { model with SessionContext = Some ctx }, []

      // ── Live testing events ──
      | SageFsEvent.TestLocationsDetected locations ->
        let lt = model.LiveTesting
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with SourceLocations = locations } } }, []

      | SageFsEvent.TestsDiscovered tests ->
        let lt = model.LiveTesting
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with DiscoveredTests = tests } } }, []

      | SageFsEvent.TestRunStarted testIds ->
        let lt = model.LiveTesting
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with IsRunning = true; AffectedTests = Set.ofArray testIds } } }, []

      | SageFsEvent.TestResultsBatch results ->
        let lt = Features.LiveTesting.LiveTesting.mergeResults model.LiveTesting.TestState results
        { model with LiveTesting = { model.LiveTesting with TestState = lt } }, []

      | SageFsEvent.LiveTestingToggled enabled ->
        let lt = model.LiveTesting
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with Enabled = enabled } } }, []

      | SageFsEvent.AffectedTestsComputed testIds ->
        let lt = model.LiveTesting
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with AffectedTests = Set.ofArray testIds } } }, []

      | SageFsEvent.CoverageUpdated coverage ->
        let lt = model.LiveTesting
        let annotations : Features.LiveTesting.CoverageAnnotation array =
          coverage.Slots
          |> Array.mapi (fun i slot ->
            let status =
              if coverage.Hits.[i] then
                Features.LiveTesting.CoverageStatus.Covered (1, true)
              else
                Features.LiveTesting.CoverageStatus.NotCovered
            { Symbol = sprintf "%s:%d" slot.File slot.Line
              FilePath = slot.File
              DefinitionLine = slot.Line
              Status = status })
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with CoverageAnnotations = annotations } } }, []

      | SageFsEvent.RunPolicyChanged (category, policy) ->
        let lt = model.LiveTesting
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with RunPolicies = Map.add category policy lt.TestState.RunPolicies } } }, []

      | SageFsEvent.ProvidersDetected providers ->
        let lt = model.LiveTesting
        { model with
            LiveTesting = { lt with TestState = { lt.TestState with DetectedProviders = providers } } }, []

    | SageFsMsg.CycleTheme ->
      let name, theme = ThemePresets.cycleNext model.Theme
      { model with Theme = theme; ThemeName = name }, []

    | SageFsMsg.ToggleLiveTesting ->
      let lt = model.LiveTesting
      { model with
          LiveTesting = { lt with TestState = { lt.TestState with Enabled = not lt.TestState.Enabled } } }, []

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
      { model with
          LiveTesting =
            { lt with
                TestState =
                  { lt.TestState with
                      RunPolicies =
                        lt.TestState.RunPolicies
                        |> Map.add Features.LiveTesting.TestCategory.Unit (nextPolicy unitPolicy) } } }, []

    | SageFsMsg.ToggleCoverage ->
      let lt = model.LiveTesting
      { model with
          LiveTesting = { lt with TestState = { lt.TestState with ShowCoverage = not lt.TestState.ShowCoverage } } }, []

    | SageFsMsg.PipelineTick now ->
      let effects, pipeline' = model.LiveTesting |> Features.LiveTesting.LiveTestPipelineState.tick now
      let mappedEffects = effects |> List.map SageFsEffect.Pipeline
      { model with LiveTesting = pipeline' }, mappedEffects

    | SageFsMsg.FileContentChanged (filePath, content) ->
      if model.LiveTesting.TestState.Enabled then
        let now = DateTimeOffset.UtcNow
        let pipeline' =
          model.LiveTesting
          |> Features.LiveTesting.LiveTestPipelineState.onKeystroke content filePath now
        { model with LiveTesting = pipeline' }, []
      else
        model, []

    | SageFsMsg.FcsTypeCheckCompleted result ->
      let effects, pipeline' =
        model.LiveTesting
        |> Features.LiveTesting.LiveTestPipelineState.handleFcsResult result
      let mappedEffects = effects |> List.map SageFsEffect.Pipeline
      { model with LiveTesting = pipeline' }, mappedEffects

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
    let editorAnnotations =
      if model.LiveTesting.TestState.Enabled then
        Features.LiveTesting.LiveTesting.annotationsForFile "editor" model.LiveTesting.TestState
      else [||]
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
      | ActiveSession.Viewing sid -> sid
      | ActiveSession.AwaitingSession -> ""
    let outputRegion = {
      Id = "output"
      Flags = RegionFlags.Scrollable ||| RegionFlags.LiveUpdate
      Content =
        model.RecentOutput
        |> List.filter (fun line ->
          System.String.IsNullOrEmpty line.SessionId || line.SessionId = activeSessionId)
        |> List.rev
        |> List.map (fun line ->
          let kindLabel =
            match line.Kind with
            | OutputKind.Result -> "result"
            | OutputKind.Error -> "error"
            | OutputKind.Info -> "info"
            | OutputKind.System -> "system"
          sprintf "[%s] [%s] %s"
            (line.Timestamp.ToString("HH:mm:ss")) kindLabel line.Text)
        |> String.concat "\n"
      Affordances = []
      Cursor = None
      Completions = None
      LineAnnotations = [||]
    }

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
          let active = if s.IsActive then " *" else ""
          let selected = if model.Editor.SelectedSessionIndex = Some i then ">" else " "
          let projects =
            if s.Projects.IsEmpty then ""
            else sprintf " (%s)" (s.Projects |> List.map System.IO.Path.GetFileNameWithoutExtension |> String.concat ", ")
          let evals =
            if s.EvalCount > 0 then sprintf " evals:%d" s.EvalCount else ""
          let uptime =
            let ts = now - s.UpSince
            if ts.TotalDays >= 1.0 then sprintf " up:%dd%dh" (int ts.TotalDays) ts.Hours
            elif ts.TotalHours >= 1.0 then sprintf " up:%dh%dm" (int ts.TotalHours) ts.Minutes
            elif ts.TotalMinutes >= 1.0 then sprintf " up:%dm" (int ts.TotalMinutes)
            else " up:just now"
          let dir =
            if s.WorkingDirectory.Length > 0 then sprintf " dir:%s" s.WorkingDirectory
            else ""
          let lastAct =
            let diff = now - s.LastActivity
            if diff.TotalSeconds < 60.0 then " last:just now"
            elif diff.TotalMinutes < 60.0 then sprintf " last:%dm ago" (int diff.TotalMinutes)
            elif diff.TotalHours < 24.0 then sprintf " last:%dh ago" (int diff.TotalHours)
            else sprintf " last:%dd ago" (int diff.TotalDays)
          sprintf "%s %s [%s]%s%s%s%s%s%s" selected s.Id statusLabel active projects evals uptime dir lastAct)
        |> String.concat "\n"
        |> fun s ->
          let creatingLine =
            if model.CreatingSession then "\n⏳ Creating session..."
            else ""
          if s.Length > 0 then sprintf "%s%s\n... ↑↓ nav · Enter switch · Del stop · ^Tab cycle" s creatingLine
          else if model.CreatingSession then "⏳ Creating session..."
          else s
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
  /// Create a new session
  CreateSession: string list -> string -> Async<Result<SessionInfo, SageFsError>>
  /// Stop a session
  StopSession: SessionId -> Async<Result<unit, SageFsError>>
  /// List all sessions
  ListSessions: unit -> Async<SessionInfo list>
  /// Fetch warmup context for a session (optional — None disables warmup dispatch)
  GetWarmupContext: (SessionId -> Async<SessionContext option>) option
  /// Pipeline cancellation for stale work
  PipelineCancellation: Features.LiveTesting.PipelineCancellation
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
        SageFsEvent.EvalCompleted (sessionId, output, diagnostics))
    | WorkerResponse.EvalResult (_, Error err, _, _) ->
      SageFsMsg.Event (
        SageFsEvent.EvalFailed (sessionId, SageFsError.describe err))
    | WorkerResponse.EvalCancelled _ ->
      SageFsMsg.Event (SageFsEvent.EvalCancelled sessionId)
    | other ->
      SageFsMsg.Event (
        SageFsEvent.EvalFailed (
          sessionId, sprintf "Unexpected response: %A" other))

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
              id, sprintf "No proxy for session %s" id)))
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
              SageFsEvent.SessionSwitched (None, info.Id)))
          | Error err ->
            dispatch (SageFsMsg.Event (
              SageFsEvent.EvalFailed (
                "", sprintf "Create failed: %s" (SageFsError.describe err))))
        }

      | EditorEffect.RequestSessionStop sessionId ->
        async {
          let! result = deps.StopSession sessionId
          match result with
          | Ok () ->
            dispatch (SageFsMsg.Event (
              SageFsEvent.SessionStopped sessionId))
          | Error err ->
            dispatch (SageFsMsg.Event (
              SageFsEvent.EvalFailed (
                sessionId,
                sprintf "Stop failed: %s" (SageFsError.describe err))))
        }

      | EditorEffect.RequestReset ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let replyId = newReplyId ()
            let! _ = proxy (WorkerMessage.ResetSession replyId)
            dispatch (SageFsMsg.Event (
              SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Starting)))
          })

      | EditorEffect.RequestHardReset ->
        withSession deps dispatch None (fun sid proxy ->
          async {
            let replyId = newReplyId ()
            let! _ = proxy (WorkerMessage.HardResetSession (false, replyId))
            dispatch (SageFsMsg.Event (
              SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Restarting)))
          })

    | SageFsEffect.Pipeline pipelineEffect ->
      async {
        match pipelineEffect with
        | Features.LiveTesting.PipelineEffect.ParseTreeSitter (content, filePath) ->
          let locations = Features.LiveTesting.TestTreeSitter.discover filePath content
          dispatch (SageFsMsg.Event (SageFsEvent.TestLocationsDetected locations))
        | Features.LiveTesting.PipelineEffect.RequestFcsTypeCheck filePath ->
          do! withSession deps dispatch None (fun _sid proxy ->
            async {
              let code =
                try System.IO.File.ReadAllText(filePath)
                with _ -> ""
              if code <> "" then
                let replyId = newReplyId ()
                let! resp = proxy (WorkerMessage.TypeCheckWithSymbols(code, filePath, replyId))
                let result =
                  match resp with
                  | WorkerResponse.TypeCheckWithSymbolsResult(_rid, hasErrors, _diags, symRefs) ->
                    if hasErrors then
                      Features.LiveTesting.FcsTypeCheckResult.Failed(filePath, [])
                    else
                      let refs = symRefs |> List.map WorkerProtocol.WorkerSymbolRef.toDomain
                      Features.LiveTesting.FcsTypeCheckResult.Success(filePath, refs)
                  | _ ->
                    Features.LiveTesting.FcsTypeCheckResult.Cancelled filePath
                dispatch (SageFsMsg.FcsTypeCheckCompleted result)
            })
        | Features.LiveTesting.PipelineEffect.RunAffectedTests (tests, _trigger) ->
          if Array.isEmpty tests then ()
          else
            let testIds = tests |> Array.map (fun tc -> tc.Id)
            dispatch (SageFsMsg.Event (SageFsEvent.TestRunStarted testIds))
            let ct = deps.PipelineCancellation.TestRun.next()
            Async.Start(async {
              try
                let! results =
                  Features.LiveTesting.TestOrchestrator.executeFiltered
                    Features.LiveTesting.BuiltInExecutors.builtIn 4 tests ct
                dispatch (SageFsMsg.Event (SageFsEvent.TestResultsBatch results))
              with ex ->
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
                       Timestamp = System.DateTimeOffset.UtcNow }
                     : Features.LiveTesting.TestRunResult))
                dispatch (SageFsMsg.Event (SageFsEvent.TestResultsBatch errResults))
            }, ct)
      }
