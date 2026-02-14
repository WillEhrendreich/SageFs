namespace SageFs

open System

/// The unified message type for the SageFs Elm loop.
/// All state changes flow through here — user actions and system events.
[<RequireQualifiedAccess>]
type SageFsMsg =
  | Editor of EditorAction
  | Event of SageFsEvent

/// Side effects the Elm loop can request.
/// Wraps EditorEffect — will grow as app-level effects emerge.
[<RequireQualifiedAccess>]
type SageFsEffect =
  | Editor of EditorEffect

/// The complete application state managed by the Elm loop.
type SageFsModel = {
  Editor: EditorState
  Sessions: SessionRegistryView
  RecentOutput: OutputLine list
  Diagnostics: Features.Diagnostics.Diagnostic list
}

module SageFsModel =
  let initial = {
    Editor = EditorState.initial
    Sessions = {
      Sessions = []
      ActiveSessionId = None
      TotalEvals = 0
      WatchStatus = None
    }
    RecentOutput = []
    Diagnostics = []
  }

/// Pure update function: routes SageFsMsg through the right handler.
module SageFsUpdate =
  let update (msg: SageFsMsg) (model: SageFsModel) : SageFsModel * SageFsEffect list =
    match msg with
    | SageFsMsg.Editor action ->
      let newEditor, effects = EditorUpdate.update action model.Editor
      { model with Editor = newEditor },
      effects |> List.map SageFsEffect.Editor

    | SageFsMsg.Event event ->
      match event with
      | SageFsEvent.EvalCompleted (_, output, diags) ->
        let line = {
          Kind = OutputKind.Result
          Text = output
          Timestamp = DateTime.UtcNow
        }
        { model with
            RecentOutput = line :: model.RecentOutput
            Diagnostics = diags }, []

      | SageFsEvent.EvalFailed (_, error) ->
        let line = {
          Kind = OutputKind.Error
          Text = error
          Timestamp = DateTime.UtcNow
        }
        { model with RecentOutput = line :: model.RecentOutput }, []

      | SageFsEvent.EvalStarted _ -> model, []

      | SageFsEvent.EvalCancelled _ ->
        let line = {
          Kind = OutputKind.Info
          Text = "Eval cancelled"
          Timestamp = DateTime.UtcNow
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

      | SageFsEvent.DiagnosticsUpdated (_, diags) ->
        { model with Diagnostics = diags }, []

      | SageFsEvent.SessionCreated snap ->
        { model with
            Sessions = {
              model.Sessions with
                Sessions = snap :: model.Sessions.Sessions } }, []

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
                ActiveSessionId = Some toId
                Sessions =
                  model.Sessions.Sessions
                  |> List.map (fun s ->
                    { s with IsActive = s.Id = toId }) } }, []

      | SageFsEvent.SessionStopped sessionId ->
        { model with
            Sessions = {
              model.Sessions with
                Sessions =
                  model.Sessions.Sessions
                  |> List.filter (fun s -> s.Id <> sessionId) } }, []

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
        let line =
          match result with
          | Ok msg ->
            { Kind = OutputKind.Info
              Text = sprintf "Reloaded %s: %s" path msg
              Timestamp = DateTime.UtcNow }
          | Error err ->
            { Kind = OutputKind.Error
              Text = sprintf "Reload failed %s: %s" path err
              Timestamp = DateTime.UtcNow }
        { model with RecentOutput = line :: model.RecentOutput }, []

      | SageFsEvent.WarmupProgress _ -> model, []

      | SageFsEvent.WarmupCompleted (_, failures) ->
        if failures.IsEmpty then
          let line = {
            Kind = OutputKind.Info
            Text = "Warmup complete"
            Timestamp = DateTime.UtcNow
          }
          { model with RecentOutput = line :: model.RecentOutput }, []
        else
          let lines =
            failures |> List.map (fun f ->
              { Kind = OutputKind.Error
                Text = sprintf "Warmup failure: %s" f
                Timestamp = DateTime.UtcNow })
          { model with RecentOutput = lines @ model.RecentOutput }, []

/// Pure render function: produces RenderRegion list from model.
/// Every frontend consumes these regions — terminal, web, Neovim, etc.
module SageFsRender =
  let render (model: SageFsModel) : RenderRegion list =
    let editorRegion = {
      Id = "editor"
      Flags = RegionFlags.Focusable ||| RegionFlags.LiveUpdate
      Content = ValidatedBuffer.text model.Editor.Buffer
      Affordances = []
    }

    let outputRegion = {
      Id = "output"
      Flags = RegionFlags.Scrollable ||| RegionFlags.LiveUpdate
      Content =
        model.RecentOutput
        |> List.map (fun line ->
          sprintf "[%s] %s" (
            match line.Kind with
            | OutputKind.Result -> "result"
            | OutputKind.Error -> "error"
            | OutputKind.Info -> "info"
            | OutputKind.System -> "system") line.Text)
        |> String.concat "\n"
      Affordances = []
    }

    let diagnosticsRegion = {
      Id = "diagnostics"
      Flags = RegionFlags.LiveUpdate
      Content =
        model.Diagnostics
        |> List.map (fun d ->
          sprintf "[%s] (%d,%d) %s"
            (Features.Diagnostics.DiagnosticSeverity.label d.Severity)
            d.Range.StartLine d.Range.StartColumn d.Message)
        |> String.concat "\n"
      Affordances = []
    }

    let sessionsRegion = {
      Id = "sessions"
      Flags = RegionFlags.Clickable ||| RegionFlags.LiveUpdate
      Content =
        model.Sessions.Sessions
        |> List.map (fun s ->
          let statusLabel =
            match s.Status with
            | SessionDisplayStatus.Running -> "running"
            | SessionDisplayStatus.Starting -> "starting"
            | SessionDisplayStatus.Errored r -> sprintf "error: %s" r
            | SessionDisplayStatus.Suspended -> "suspended"
            | SessionDisplayStatus.Stale -> "stale"
            | SessionDisplayStatus.Restarting -> "restarting"
          let active = if s.IsActive then " *" else ""
          sprintf "%s [%s]%s" s.Id statusLabel active)
        |> String.concat "\n"
      Affordances = []
    }

    [ editorRegion; outputRegion; diagnosticsRegion; sessionsRegion ]
