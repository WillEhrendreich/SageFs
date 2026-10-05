/// What each state of the guided new-session dialog draws. One exhaustive match over the state, in Falco.Markup
/// with the Datastar builders, so there is no hand-written attribute string and nothing the page has to
/// interpret.
///
/// It is part of the one whole-page morph: `renderRegion` is what the Sessions panel's slot holds, with the
/// dialog always present (empty while Closed) so the open signal always has a node to drive. Open or closed is a
/// browser signal and a native `<dialog>` opened with `showModal()`, which gives the focus trap, Esc, the
/// inert page behind it and the return of focus to the [+] that opened it. What was found, what overlaps and what
/// was refused is the state, drawn here.
module SageFs.Server.NewSessionDialogView

open System
open System.IO
open Falco.Markup
open Falco.Datastar
open SageFs
open SageFs.WorkflowTypes
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Server.NewSessionDialog

/// How long after the last keystroke the directory box asks the daemon for suggestions, in the text form
/// Datastar's `debounce` modifier takes: short enough to feel live, long enough that one word typed is one
/// request. It is a pause tuned to a person typing, and the page's own timer owns it, not the daemon.
[<Literal>]
let SuggestDebounce = "250ms"

let signalRef (name: string) : string = sprintf "$%s" name

let closeExpr : string = sprintf "$%s = false" Signals.NewSessionOpen

/// Opens the native modal when the signal turns true and closes it when it turns false. Idempotent, so the
/// effect re-running does nothing, and Esc (which closes the dialog natively) is folded back into the signal by
/// the `close` handler.
let openEffectExpr : string =
  sprintf "%s ? (el.open || el.showModal()) : (!el.open || el.close())" (signalRef Signals.NewSessionOpen)

/// Closing by a button: the signal goes false (the effect closes the dialog) and the server is told.
let closeAndTellExpr : string = sprintf "%s; %s" closeExpr (Ds.post NewSessionNames.CloseRoute)

/// The native close event is raised by Esc and by `close()` alike. When the signal is already false a control
/// closed it and said what it needed to (Create is mid-request, and must not be undone by a late Close), so
/// there is nothing to add. When it is still true the browser closed the dialog by itself (Esc), so the signal
/// follows and the server is told.
let onCloseExpr : string = sprintf "if(%s){%s}" (signalRef Signals.NewSessionOpen) closeAndTellExpr

let loadKey : string = TargetKind.key TargetKind.LoadProjects

/// True while projects are to be loaded and none is ticked: the one reason Create waits that the person can fix.
/// The ticks are an array bound to the boxes by position (an entry per box, empty when not ticked), so "none
/// ticked" is "no entry has anything in it", never "the array is empty".
let pickNeededExpr : string =
  sprintf "%s === '%s' && !%s.some(Boolean)"
    (signalRef NewSessionNames.TargetSignal)
    loadKey
    (signalRef NewSessionNames.ProjectsSignal)

/// Create is available when there is a directory and, if projects are to be loaded, at least one is ticked.
let createDisabledExpr : string =
  sprintf "!%s.trim() || (%s)" (signalRef Signals.NewSessionDir) pickNeededExpr

let workflowName (workflow: SessionWorkflow) : string = SessionWorkflow.label workflow

// ── Pieces ───────────────────────────────────────────────────────────────

let head : XmlNode =
  Elem.div [ Attr.class' "nsd-head" ] [
    Elem.h2 [ Attr.id NewSessionNames.TitleId ] [ Text.raw "New session" ]
    Elem.button
      [ Attr.type' "button"
        Attr.class' "icon-btn"
        testid NewSessionNames.CloseTestId
        Attr.create "aria-label" "Close the new session dialog"
        Attr.create "title" "Close (Esc)"
        Ds.onClick closeAndTellExpr ]
      [ Text.raw "✕" ]
  ]

let directoryField : XmlNode =
  Elem.div [ Attr.class' "nsd-field" ] [
    Elem.label [ Attr.create "for" NewSessionNames.DirectoryInputId ] [ Text.raw "Working directory" ]
    Elem.div [ Attr.class' "nsd-dir" ] [
      Elem.input
        [ Attr.id NewSessionNames.DirectoryInputId
          Attr.type' "text"
          Attr.class' "eval-input"
          Attr.create "autofocus" "autofocus"
          Attr.create "autocomplete" "off"
          Attr.create "spellcheck" "false"
          Attr.create "list" DomIds.DirSuggestions
          Attr.create "placeholder" workingDirPlaceholder
          Ds.bind Signals.NewSessionDir
          Ds.onEvent (sprintf "input.debounce_%s" SuggestDebounce, Ds.post "/dashboard/dir-suggest")
          Ds.onEvent ("change", Ds.post NewSessionNames.DiscoverRoute)
          Ds.onEvent ("keydown", sprintf "if(evt.key==='Enter'){evt.preventDefault();%s}" (Ds.post NewSessionNames.DiscoverRoute)) ]
      Elem.button
        [ Attr.type' "button"
          Attr.class' "eval-btn nsd-find"
          Attr.create "aria-label" "Find projects in this directory"
          Attr.create "title" "Find projects in this directory"
          Ds.onClick (Ds.post NewSessionNames.DiscoverRoute) ]
        [ Text.raw "Find" ]
    ]
    renderDirSuggestions []
  ]

let statusLine (message: string) : XmlNode =
  Elem.div
    [ Attr.class' "nsd-status"
      testid NewSessionNames.StatusTestId
      Attr.create "role" "status"
      Attr.create "aria-live" "polite" ]
    [ textEnc message ]

let relationText (overlap: Overlap) : string =
  match overlap.Relation, overlap.Session.Boundary with
  | Relation.SameDirectory, _ -> "is working in this exact directory"
  | Relation.SameRepository _, Boundary.Worktree(_, branch) ->
    sprintf "is working in the worktree on branch %s" branch
  | Relation.SameRepository _, Boundary.Repository _ -> "is working in the same repository"
  | Relation.SameRepository _, Boundary.Plain _ -> "is working in a directory next to this one"

let overlapRow (overlap: Overlap) : XmlNode =
  let sid = overlap.Session.Id
  Elem.div [ Attr.class' "nsd-overlap" ] [
    Elem.div [ Attr.class' "nsd-overlap-text" ] [
      Elem.strong [] [ textEnc (sprintf "Session %s" sid) ]
      Elem.span [] [ textEnc (sprintf " %s" (relationText overlap)) ]
      Elem.div [ Attr.class' "nsd-meta nsd-path" ] [ textEnc overlap.Session.WorkingDirectory ]
    ]
    Elem.button
      [ Attr.type' "button"
        Attr.class' "eval-btn"
        testid NewSessionNames.SwitchTestId
        Attr.create "aria-label" (attrEnc (sprintf "Switch to session %s instead of creating another" sid))
        Ds.onClick (sprintf "%s; %s" closeExpr (Ds.post (sprintf "/dashboard/session/switch/%s" sid))) ]
      [ Text.raw "Switch to it" ]
  ]

let warning (first: Overlap) (rest: Overlap list) : XmlNode =
  Elem.div
    [ Attr.class' "nsd-notice nsd-warning"
      testid NewSessionNames.WarningTestId
      Attr.create "role" "alert" ]
    [ Elem.strong [] [ Text.raw "A session already exists here" ]
      yield! (first :: rest) |> List.map overlapRow
      Elem.p [ Attr.class' "nsd-meta" ] [
        Text.raw "You can still make another one on purpose. It will be its own worker, with its own state."
      ] ]

let refusal (reason: Refusal) : XmlNode =
  Elem.div
    [ Attr.class' "nsd-notice nsd-refusal"
      testid NewSessionNames.RefusalTestId
      Attr.create "role" "alert" ]
    [ Elem.strong [] [ textEnc (Refusal.title reason) ]
      Elem.p [] [ textEnc (Refusal.detail reason) ]
      Elem.p [ Attr.class' "nsd-next" ] [
        Elem.strong [] [ Text.raw "Next: " ]
        textEnc (Refusal.nextAction reason)
      ] ]

/// Whether a choice can be made right now.
[<RequireQualifiedAccess>]
type Availability =
  | Enabled
  | Disabled

let radio (signal: string) (value: string) (availability: Availability) : XmlNode =
  Elem.input
    [ yield Attr.type' "radio"
      yield Attr.create "name" signal
      yield Attr.create "value" (attrEnc value)
      yield Ds.bind signal
      match availability with
      | Availability.Disabled -> yield Attr.create "disabled" "disabled"
      | Availability.Enabled -> () ]

let candidateRow (candidate: Candidate) : XmlNode =
  Elem.label [ Attr.class' "nsd-row"; testid NewSessionNames.CandidateTestId ] [
    Elem.input
      [ Attr.type' "checkbox"
        Attr.create "value" (attrEnc candidate.Path)
        Ds.bind NewSessionNames.ProjectsSignal ]
    Elem.span [ Attr.class' "nsd-row-text" ] [
      Elem.span [ Attr.class' "nsd-path" ] [ textEnc candidate.Path ]
      Elem.span [ Attr.class' "nsd-meta" ] [
        match candidate.Kind with
        | CandidateKind.Solution -> Text.raw "solution · "
        | CandidateKind.Project -> Text.raw "project · "
        textEnc (Frameworks.describe candidate.Frameworks)
      ]
    ]
  ]

let targetGroup (found: Found) : XmlNode =
  let loadAvailability =
    match found.Candidates with
    | [] -> Availability.Disabled
    | _ :: _ -> Availability.Enabled
  Elem.fieldset [ Attr.class' "nsd-group" ] [
    Elem.legend [] [ Text.raw "What to load" ]
    Elem.label [ Attr.class' "nsd-row" ] [
      radio NewSessionNames.TargetSignal loadKey loadAvailability
      Elem.span [ Attr.class' "nsd-row-text" ] [
        Elem.span [ Attr.class' "nsd-path" ] [ Text.raw "Projects found here" ]
        Elem.span [ Attr.class' "nsd-meta" ] [ Text.raw "Tick one or several. Their code is loaded into the session." ]
      ]
    ]
    match found.Candidates, String.IsNullOrWhiteSpace found.Directory with
    | [], true ->
      Elem.p [ Attr.class' "nsd-meta nsd-empty" ] [
        Text.raw "Choose a directory above and press Find to see its projects, or start a bare session."
      ]
    | [], false ->
      Elem.p [ Attr.class' "nsd-meta nsd-empty" ] [
        textEnc (sprintf "No projects or solutions found in %s. A bare session starts without one." found.Directory)
      ]
    | candidates, _ ->
      Elem.div [ Attr.class' "nsd-list" ] (candidates |> List.map candidateRow)
    Elem.label [ Attr.class' "nsd-row" ] [
      radio NewSessionNames.TargetSignal (TargetKind.key TargetKind.BareSession) Availability.Enabled
      Elem.span [ Attr.class' "nsd-row-text" ] [
        Elem.span [ Attr.class' "nsd-path" ] [ Text.raw "Bare" ]
        Elem.span [ Attr.class' "nsd-meta" ] [ Text.raw "No project. An empty F# session you can load into later." ]
      ]
    ]
  ]

let suggestionFor (hint: WorkflowHint) (workflow: SessionWorkflow) : XmlNode list =
  match hint with
  | WorkflowHint.NoneSuggested -> []
  | WorkflowHint.Suggested suggestion ->
    match WorkflowChoice.key suggestion.SuggestedWorkflow = WorkflowChoice.key workflow with
    | true -> [ Elem.span [ Attr.class' "nsd-meta nsd-suggested" ] [ textEnc (sprintf "Suggested: %s." suggestion.Reason) ] ]
    | false -> []

let workflowGroup (hint: WorkflowHint) : XmlNode =
  Elem.fieldset [ Attr.class' "nsd-group"; testid NewSessionNames.WorkflowTestId ] [
    Elem.legend [] [ Text.raw "How you will work" ]
    yield!
      WorkflowChoice.all
      |> List.map (fun workflow ->
        Elem.label [ Attr.class' "nsd-row" ] [
          radio NewSessionNames.WorkflowSignal (WorkflowChoice.key workflow) Availability.Enabled
          Elem.span [ Attr.class' "nsd-row-text" ] [
            Elem.span [ Attr.class' "nsd-path" ] [ textEnc (workflowName workflow) ]
            Elem.span [ Attr.class' "nsd-meta" ] [ textEnc (WorkflowChoice.oneLine workflow) ]
            yield! suggestionFor hint workflow
          ]
        ])
  ]

let footer (createLabel: string) (disabledExpr: string) : XmlNode =
  Elem.div [ Attr.class' "nsd-foot" ] [
    Elem.span
      [ Attr.class' "nsd-meta nsd-hint"
        Attr.create "role" "status"
        Ds.show pickNeededExpr ]
      [ Text.raw "Tick a project, or choose Bare, to create." ]
    Elem.button
      [ Attr.type' "button"
        Attr.class' "eval-btn nsd-secondary"
        testid NewSessionNames.CancelTestId
        Ds.onClick closeAndTellExpr ]
      [ Text.raw "Cancel" ]
    Elem.button
      [ Attr.type' "button"
        Attr.class' "eval-btn nsd-primary"
        testid NewSessionNames.CreateTestId
        Attr.create "aria-label" (attrEnc createLabel)
        Ds.attr' ("disabled", disabledExpr)
        Ds.onClick (sprintf "%s; %s" closeExpr (Ds.post NewSessionNames.CreateRoute)) ]
      [ textEnc createLabel ]
  ]

let choices (found: Found) : XmlNode list =
  [ targetGroup found; workflowGroup found.Hint ]

let createLabel = "Create session"
let createAnotherLabel = "Create another session here"

// ── The dialog ───────────────────────────────────────────────────────────

let frame (state: NewSessionDialog) (body: XmlNode list) (foot: XmlNode list) : XmlNode =
  Elem.dialog
    [ Attr.id NewSessionNames.DialogId
      Attr.class' "nsd"
      testid NewSessionNames.DialogTestId
      Attr.create "data-state" (NewSessionDialog.stateKey state)
      Attr.create "aria-labelledby" NewSessionNames.TitleId
      Ds.preserveAttr "open"
      Ds.effect openEffectExpr
      Ds.onEvent ("close", onCloseExpr) ]
    [ yield head
      match body with
      | [] -> ()
      | _ -> yield Elem.div [ Attr.class' "nsd-body" ] body
      // The footer is the dialog's own row, outside the scrolling body, so Create is always on screen.
      yield! foot ]

/// The dialog for a state. Total: a new state cannot be added without being drawn here.
let render (state: NewSessionDialog) : XmlNode =
  match state with
  | NewSessionDialog.Closed -> frame state [] []
  | NewSessionDialog.Discovering directory ->
    frame state
      [ directoryField
        statusLine (sprintf "Looking for projects in %s…" directory) ]
      [ footer createLabel "true" ]
  | NewSessionDialog.Choosing found ->
    frame state
      [ yield directoryField
        yield! choices found ]
      [ footer createLabel createDisabledExpr ]
  | NewSessionDialog.Warning(found, first, rest) ->
    frame state
      [ yield directoryField
        yield warning first rest
        yield! choices found ]
      [ footer createAnotherLabel createDisabledExpr ]
  | NewSessionDialog.Creating(request, found) ->
    frame state
      [ yield directoryField
        yield statusLine (sprintf "Starting a %s session in %s…" (workflowName request.Workflow) request.Directory)
        yield! choices found ]
      [ footer createLabel "true" ]
  | NewSessionDialog.Refused(reason, found) ->
    frame state
      [ yield directoryField
        yield refusal reason
        yield! choices found ]
      [ footer createLabel createDisabledExpr ]

/// The card a create shows in the Sessions list the moment the dialog closes, before the daemon has a session
/// to list. Same slot, same morph: it goes when the state leaves Creating, and the real card is in the list.
let startingCard (request: Request) : XmlNode =
  Elem.div
    [ Attr.id NewSessionNames.StartingCardId
      Attr.class' "session-row session-starting"
      testid NewSessionNames.StartingTestId
      Attr.create "role" "status"
      Attr.create "aria-live" "polite" ]
    [ Elem.div [ Attr.class' "session-card-body" ] [
        Elem.div [ Attr.class' "session-card-status-row" ] [
          Elem.span [ Attr.style "font-weight: bold;" ] [ textEnc (Path.GetFileName(request.Directory.TrimEnd('/', '\\'))) ]
          Elem.span [ Attr.class' "status badge status-starting" ] [ Text.raw "Starting" ]
        ]
        Elem.div [ Attr.class' "status-msg" ] [
          textEnc (sprintf "⏳ Starting a %s session…" (workflowName request.Workflow))
        ]
        Elem.div [ Attr.class' "nsd-meta nsd-path" ] [ textEnc request.Directory ]
      ] ]

/// The Sessions panel's slot: the starting card while a create runs, the session list, and the dialog.
let renderRegion (state: NewSessionDialog) (sessionsPanel: XmlNode) : XmlNode =
  Elem.div [ Attr.id NewSessionNames.RegionId ] [
    match state with
    | NewSessionDialog.Creating(request, _) -> yield startingCard request
    | NewSessionDialog.Closed
    | NewSessionDialog.Discovering _
    | NewSessionDialog.Choosing _
    | NewSessionDialog.Warning _
    | NewSessionDialog.Refused _ -> ()
    yield sessionsPanel
    yield render state
  ]
