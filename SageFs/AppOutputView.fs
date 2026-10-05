/// The running app's stdout and stderr, in the dashboard's bottom dock: the second pane's DRAWN answer.
///
/// `AppOutputPane` decides which lines a person sees; this draws exactly that decision and adds nothing to
/// it. Every case of the decision has a case here: open draws the header and the lines, collapsed draws ONE
/// line saying why (never a blank), and a search that matched nothing says so rather than looking like an
/// empty pane that simply has no output.
///
/// WHY THE LINES ARE SERVER-SIDE. The decision is a pure function of the buffer and the pane's settings, so
/// the server renders what it says and the SSE morph carries it. The pane's own follow/pause/search/filter
/// controls are not drawn yet — they change the settings the decision reads, which needs a route and a
/// signal, and that is the next piece. Until then the pane runs at its defaults (following, showing, both
/// streams, no search), which is what `AppOutputPane.create` gives it.
///
/// WHY NO PIN BUTTON. The pin only matters for a pane collapsed because the app has not written yet, and an
/// empty pane has nothing to show. `CollapsedBecause` still reports the offer and `DockPanes` still decides
/// it, so nothing about the decision changes when the button arrives; only the drawing does.
module SageFs.Server.AppOutputView

open Falco.Markup
open SageFs
open SageFs.Server.DashboardFragments
open SageFs.Server.DockPanes
open SageFs.Server.AppOutputPane

/// The app-output pane's own element ids. Separate from `DockIds` (which names the live-bindings pane)
/// because the morph matches client-owned attributes by id, and two panes in one dock must never share one.
module AppOutputIds =
  let [<Literal>] Pane = "app-output-pane"
  let [<Literal>] CollapsedBar = "app-output-bar"
  let [<Literal>] Lines = "app-output-lines"
  let [<Literal>] EmptyState = "app-output-empty"
  let [<Literal>] NoMatch = "app-output-nomatch"

/// One line of app output. The class comes from `AppOutputLine.cssClass`, so the name a line is coloured by
/// is decided in one place and the CSS cannot drift from the decision about which stream it came from.
let private renderLine (line: AppOutputLine) : XmlNode =
  Elem.div [ Attr.class' (sprintf "app-output-line %s" (AppOutputLine.cssClass line)); Attr.create "data-stream" (match line.Stream with OutputStream.Stdout -> "stdout" | OutputStream.Stderr -> "stderr") ] [
    textEnc line.Text
  ]

/// Why the pane is not showing: one line, and it says what to do about it.
///
/// Its own bar class, not `live-dock-bar`: two panes share one dock, so a generic "collapsed bar" class
/// makes an open live-bindings pane next to a collapsed output pane look like a collapsed bindings pane —
/// which is exactly how it failed "a session with bindings … no collapsed bar".
let renderCollapsedBar (why: CollapsedBecause) : XmlNode =
  Elem.div [ Attr.id AppOutputIds.CollapsedBar; Attr.create "role" "status"; Attr.class' "app-output-bar" ] [
    Elem.span [ Attr.class' "live-pane-title" ] [ textEnc "App output" ]
    Elem.span [ Attr.class' "live-dock-reason" ] [ textEnc (CollapsedBecause.text why) ]
  ]

/// The pane when it has something to show: what opened it, what the pane says about itself, and the lines
/// the decision said are visible.
let renderBody (pane: AppOutputPane) (because: OpenBecause) : XmlNode =
  Elem.div [ Attr.id AppOutputIds.Pane; Attr.class' "panel live-pane"; testid "app-output-pane" ] [
    Elem.div [ Attr.class' "live-pane-bar" ] [
      Elem.span [ Attr.class' "live-pane-title" ] [ textEnc "App output" ]
      Elem.span [ Attr.class' "live-pane-count" ] [ textEnc (OpenBecause.text because) ]
      Elem.span [ Attr.class' "live-pane-stamp" ] [ textEnc (AppOutputPane.header pane) ]
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
/// to draw, and a collapsed pane never renders an empty body.
let render (state: PaneState) (pane: AppOutputPane) : XmlNode =
  match state with
  | PaneOpen because -> renderBody pane because
  | PaneCollapsed why -> renderCollapsedBar why
