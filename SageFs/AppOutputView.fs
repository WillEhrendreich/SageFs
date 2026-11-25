/// The running app's stdout and stderr, in the dashboard's bottom dock: the second pane's DRAWN answer.
///
/// `AppOutputPane` decides which lines a person sees; this draws exactly that decision and adds nothing to
/// it. Every case of the decision has a case here: open draws the header and the lines, collapsed draws ONE
/// line saying why (never a blank), and a search that matched nothing says so rather than looking like an
/// empty pane that simply has no output.
///
/// WHY THE LINES ARE SERVER-SIDE. The decision is a pure function of the buffer and the pane's settings, so
/// the server renders what it says and the SSE morph carries it. The follow/pause/search/filter controls are
/// POSTs rather than client signals for the same reason: a signal would change what the browser shows while
/// the server went on rendering something else, and the two would disagree at the first morph. The control
/// writes the setting and a state change re-renders, so there is one answer to "what does the pane show".
///
/// WHY NO PIN BUTTON. The pin only matters for a pane collapsed because the app has not written yet, and an
/// empty pane has nothing to show. `CollapsedBecause` still reports the offer and `DockPanes` still decides
/// it, so nothing about the decision changes when the button arrives; only the drawing does.
module SageFs.Server.AppOutputView

open Falco.Markup
open Falco.Datastar
open SageFs
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Server.DockPanes
open SageFs.Server.AppOutputPane
open SageFs.Server.DashboardSinks
open SageFs.Server.DockPaneLayout

/// The app-output pane's own element ids. Separate from `DockIds` (which names the live-bindings pane)
/// because the morph matches client-owned attributes by id, and two panes in one dock must never share one.
module AppOutputIds =
  let [<Literal>] Pane = "app-output-pane"
  let [<Literal>] CollapsedBar = "app-output-bar"
  let [<Literal>] Lines = "app-output-lines"
  let [<Literal>] EmptyState = "app-output-empty"
  let [<Literal>] NoMatch = "app-output-nomatch"
  let [<Literal>] Follow = "app-output-follow"
  let [<Literal>] Pause = "app-output-pause"
  let [<Literal>] Search = "app-output-search"
  let [<Literal>] Stream = "app-output-stream"

/// One line of app output. The class comes from `AppOutputLine.cssClass`, so the name a line is coloured by
/// is decided in one place and the CSS cannot drift from the decision about which stream it came from.
let private renderLine (line: AppOutputLine) : XmlNode =
  Elem.div [ Attr.class' (sprintf "app-output-line %s" (AppOutputLine.cssClass line)); Sink.attr "data-stream" (UserText.ofString (match line.Stream with OutputStream.Stdout -> "stdout" | OutputStream.Stderr -> "stderr")) ] [
    textEnc line.Text
  ]

/// A pane with nothing to show draws nothing a person can see. It used to draw a bar saying there was no app output and how to make
/// some, which cost a row of a dock that is short, for a fact that has nothing to act on until an app runs. What is left is a hidden
/// placeholder with the pane's id, so the morph has something to replace when the first line arrives.
///
/// Its own id and class, not `live-dock-bar`: two panes share one dock, so a generic bar class would make an open live-bindings pane
/// next to this one look like a collapsed bindings pane.
let private renderPlaceholder : XmlNode =
  Elem.div [ Attr.id AppOutputIds.CollapsedBar; Attr.class' "app-output-bar live-hidden" ] []

/// The four controls the roadmap asks for: follow, pause, search and the stream filter.
///
/// Each POSTs the value it is moving TO rather than toggling blind, so the request says what it wants and
/// the render says what happened. The pressed state comes from the server's pane, so a control can never
/// show itself on while the decision still thinks it is off.
let private controls (pane: AppOutputPane) : XmlNode list =
  let following = AppOutputPane.following pane
  let paused = AppOutputPane.paused pane
  let filter = pane.StreamFilter

  let toggle (id: string) (testId: string) (label: string) (onLabel: string) (offLabel: string) (isOn: bool) (target: string) =
    Elem.button [
      Attr.id id
      // `session-btn-labeled` instead of the bare uniform square: these controls carry WORDS ("following"/"held still",
      // "paused"/"showing"), and at the 28px square a word spills over the next button. The labeled class sizes the button to
      // its own label; the accessible name stays the full sentence.
      Attr.class' ("session-btn session-btn-labeled" + (if isOn then " session-btn-on" else ""))
      Attr.type' "button"
      Sink.attr "aria-label" (UserText.ofString label)
      Sink.attr "aria-pressed" (UserText.ofString (if isOn then "true" else "false"))
      testid testId
      // The value rides the URL. `@post`'s second argument is fetch options, so a `{value: ...}` there is dropped before it reaches
      // the handler, which reads `Request.Query["value"]`: every toggle then read false and every filter fell back to "both".
      Sink.onEvent (DomEvent.Click, sprintf "@post('/dashboard/app-output/%s?value=%b')" target (not isOn))
    ] [ textEnc (if isOn then onLabel else offLabel) ]

  let streamChoice (value: string) (label: string) (wanted: OutputStreamFilter) =
    let chosen = (filter = wanted)
    Elem.button [
      // Words again, so label-sized again (see the toggle above).
      Attr.class' ("session-btn session-btn-labeled" + (if chosen then " session-btn-on" else ""))
      Attr.type' "button"
      Sink.attr "aria-label" (UserText.ofString (sprintf "Show %s" label))
      Sink.attr "aria-pressed" (UserText.ofString (if chosen then "true" else "false"))
      testid (sprintf "app-output-stream-%s" value)
      Sink.onEvent (DomEvent.Click, sprintf "@post('/dashboard/app-output/stream?value=%s')" value)
    ] [
      // The chosen stream carries a mark as well as a fill, so the choice never rests on colour alone.
      match chosen with
      | true -> Elem.span [ Attr.create "aria-hidden" "true" ] [ Text.raw "✓ " ]
      | false -> ()
      textEnc label
    ]

  [
    toggle AppOutputIds.Follow "app-output-follow" "Follow new output, or hold the view still" "following" "held still" following "follow"
    toggle AppOutputIds.Pause "app-output-pause" "Pause the pane, or show what it is holding" "paused" "showing" paused "pause"
    Elem.input [
      Attr.id AppOutputIds.Search
      Attr.class' "live-filter"
      Attr.type' "text"
      Attr.create "placeholder" "filter lines"
      Attr.create "aria-label" "Filter the app output lines"
      Sink.attr "value" (UserText.ofString pane.Search)
      testid "app-output-search"
      Ds.onEvent ("input", "@post('/dashboard/app-output/search?value=' + encodeURIComponent(event.target.value))")
    ]
    // Three buttons for one choice cost three boxes in a bar that has to stay on one line. A picker answers
    // the same question with one control and keeps the same post; the value is read at event time.
    Elem.select [
      Attr.class' "app-output-stream-select"
      Attr.create "aria-label" "Which stream to show"
      Attr.title "Which stream to show"
      testid "app-output-stream-select"
      Ds.onEvent ("change", "var s=event.target.value; @post('/dashboard/app-output/stream?value=' + encodeURIComponent(s))")
    ] [
      for (value, label, wanted) in [ "both", "all", BothStreams; "errors", "errors", ErrorsOnly; "output", "output", OutputOnly ] do
        Elem.option
          ([ Attr.value (attrEnc value) ] @ (if filter = wanted then [ Attr.create "selected" "selected" ] else []))
          [ textEnc label ]
    ]
  ]

/// The pane when it has something to show: what opened it, what the pane says about itself, and the lines
/// the decision said are visible.
let renderBody (pane: AppOutputPane) (because: OpenBecause) : XmlNode =
  Elem.div [ Attr.id AppOutputIds.Pane; Attr.class' "panel live-pane"; testid "app-output-pane" ] [
    Elem.div [ Attr.class' "live-pane-bar" ] [
      Elem.span [ Attr.class' "live-pane-title"; Attr.title (attrEnc (OpenBecause.text because)) ] [ textEnc "App output" ]
      // Where this pane lives is a choice, not a fixed position: beside the transcript or under it in the dock.
      // The label says where a click MOVES it, and the signal is persisted so the choice survives a reload.
      Elem.button [
        Attr.id PaneLayoutToggle
        Attr.class' "session-btn session-btn-labeled"
        Attr.type' "button"
        Attr.create "aria-label" "Move the app output pane beside the transcript, or back into the dock"
        testid "app-output-layout-toggle"
        Sink.onClick (
          sprintf
            "$%s = $%s === 'side' ? 'stacked' : 'side'; try{localStorage.setItem('sagefs.paneLayout',$%s)}catch(e){}"
            PaneLayoutSignal PaneLayoutSignal PaneLayoutSignal
        )
      ] [
        Elem.span [
          Ds.text (sprintf "$%s === 'side' ? 'In dock' : 'Beside output'" PaneLayoutSignal)
        ] [ Text.raw "Beside output" ]
      ]
      // The count and the stamp are facts about the buffer, not controls. They live in the title's tooltip;
      // the bar carries only what a person can act on, and the filter stays beside the buttons on one line.
      yield! controls pane
    ]
    // A search that matched nothing says so. Without this it is indistinguishable from an app that
    // printed nothing, which is a different fact and has a different fix.
    match AppOutputPane.matching pane, AppOutputPane.visible pane with
    | false, _ ->
      Elem.div [ Attr.id AppOutputIds.NoMatch; Attr.class' "live-empty"; testid "app-output-nomatch" ] [
        textEnc (sprintf "no line matches \"%s\"" (pane.Search.Trim()))
      ]
    | true, [] ->
      Elem.div [ Attr.id AppOutputIds.EmptyState; Attr.class' "live-empty"; testid "app-output-empty" ] [
        textEnc (AppOutputPane.header pane)
      ]
    | true, lines ->
      Elem.div [ Attr.id AppOutputIds.Lines; Attr.class' "app-output-lines"; Attr.create "role" "log" ] [
        for line in lines do
          renderLine line
      ]
  ]

/// The pane, decided and drawn: `DockPanes` has already answered, so this is only which of the two shapes
/// to draw, and a collapsed pane never renders an empty body. Both reasons it can be collapsed (no session, no output yet) draw the
/// same nothing: the bindings pane already says there is no session, and an app that has written nothing has nothing to show.
let render (state: PaneState) (pane: AppOutputPane) : XmlNode =
  match state with
  | PaneOpen because -> renderBody pane because
  | PaneCollapsed NoSessionOpen
  | PaneCollapsed NoAppOutputYet
  | PaneCollapsed NothingBoundYet -> renderPlaceholder
