/// The Panels menu: where each movable panel sits, and the buttons that move, close and reopen it.
///
/// The menu is a native `<details>` in the header, drawn from `PaneLayoutView`, a plain value the stream builds on every push from
/// the saved layout and the facts that decide what each panel has to say. A click posts one edit (`?e=<workflow>/<edit>`), the daemon
/// saves it and every open page is pushed the new layout through the one morph, so what the menu shows is what the server holds and
/// never the browser's idea of it. Every move is a button with a name a screen reader reads: nothing here needs a drag.
///
/// Pure markup over a value: no daemon, no file, no clock. `PaneLayoutView.ofRead` is where the saved layout and the facts meet.
module SageFs.Server.PaneMenuView

open System
open Falco.Markup
open Falco.Datastar
open SageFs
open SageFs.Server.DashboardPanelContext
open SageFs.Server.DashboardTypes

/// What a panel has to say right now, apart from where it sits. A panel the person closed says nothing, so this is only about the
/// panels that are placed.
[<RequireQualifiedAccess>]
type PaneRelevance =
  /// The panel itself is drawn.
  | OnPage
  /// A one-line reason stands in its place ("Hot reload: off"), and it is opened for the sentence that says why.
  | ReasonLine of name: string
  /// It has nothing to report, so it draws nothing; the words say why.
  | NothingToShow of why: string

/// How the saved layout for the workflow in view read.
[<RequireQualifiedAccess>]
type LayoutHealth =
  | Healthy
  /// The saved layout was set aside and the default is shown; the words say why.
  | FellBackToDefault of reason: string
  /// The saved layout is in use with parts set aside or filled in, said one sentence each.
  | UsedWithNotes of notes: string list

/// Whether a layout, or every layout, is the default. A reset button is offered only for `HasChanges`, because a reset of
/// `IsDefault` would change nothing.
[<RequireQualifiedAccess>]
type LayoutStanding =
  | IsDefault
  | HasChanges

[<RequireQualifiedAccess>]
module LayoutStanding =
  /// `IsDefault` when `layout` equals `defaultLayout`.
  let compare (defaultLayout: PaneLayout) (layout: PaneLayout) : LayoutStanding =
    match layout = defaultLayout with
    | true -> LayoutStanding.IsDefault
    | false -> LayoutStanding.HasChanges

/// Everything the menu and the regions need, as one value, so equal values draw the same page.
type PaneLayoutView =
  { Workflow: LayoutWorkflow
    Layout: PaneLayout
    Relevance: (PanelId * PaneRelevance) list
    Health: LayoutHealth
    /// The viewed workflow's layout against its default (Reset this layout would change nothing for `IsDefault`).
    ThisWorkflow: LayoutStanding
    /// Every workflow's layout against its default (Reset all would change nothing for `IsDefault`).
    EveryWorkflow: LayoutStanding }

[<RequireQualifiedAccess>]
module PaneLayoutView =
  /// The panel's own decision, when it has one. Session context and workspace hygiene are drawn whatever the facts are.
  let contextPanelOf (pane: PanelId) : ContextPanel option =
    match pane with
    | PanelId.HotReload -> Some ContextPanel.HotReload
    | PanelId.LiveTesting -> Some ContextPanel.LiveTesting
    | PanelId.AgentTeam -> Some ContextPanel.Cohort
    | PanelId.Friction -> Some ContextPanel.Friction
    | PanelId.SessionContext
    | PanelId.Hygiene -> None

  let relevanceOf (facts: PanelFacts) (pane: PanelId) : PaneRelevance =
    match contextPanelOf pane with
    | None -> PaneRelevance.OnPage
    | Some panel ->
      match PanelContext.decide facts panel with
      | PanelPresence.Shown -> PaneRelevance.OnPage
      | PanelPresence.Hidden _ -> PaneRelevance.ReasonLine(PanelVisibility.reasonName panel)
      | PanelPresence.Quiet because -> PaneRelevance.NothingToShow(QuietBecause.text because)

  let healthOf (workflow: LayoutWorkflow) (read: PaneLayoutRead) : LayoutHealth =
    match read.Reading, LayoutBook.problemOf workflow read.Book, LayoutBook.notesOf workflow read.Book with
    | FileReading.Unreadable reason, _, _ -> LayoutHealth.FellBackToDefault reason
    | FileReading.Readable, Some refusal, _ -> LayoutHealth.FellBackToDefault(sprintf "the saved layout is not readable: %s" (LayoutRefusal.describe refusal))
    | FileReading.Readable, None, [] -> LayoutHealth.Healthy
    | FileReading.Readable, None, notes -> LayoutHealth.UsedWithNotes(notes |> List.map LayoutNote.describe)

  /// The view for one workflow, from what the store read and the facts the page is drawn with.
  let ofRead (workflow: LayoutWorkflow) (read: PaneLayoutRead) (facts: PanelFacts) : PaneLayoutView =
    let layout = LayoutBook.layoutOf workflow read.Book
    { Workflow = workflow
      Layout = layout
      Relevance = PanelId.all |> List.map (fun pane -> pane, relevanceOf facts pane)
      Health = healthOf workflow read
      ThisWorkflow = LayoutStanding.compare (PaneLayout.defaultFor workflow) layout
      EveryWorkflow =
        match LayoutWorkflow.all |> List.forall (fun w -> LayoutBook.layoutOf w read.Book = PaneLayout.defaultFor w) with
        | true -> LayoutStanding.IsDefault
        | false -> LayoutStanding.HasChanges }

  /// The default layout with nothing saved: what a page built from a snapshot alone draws, and what every page drew before panels
  /// could move.
  let today : PaneLayoutView =
    { Workflow = LayoutWorkflow.NoSession
      Layout = PaneLayout.defaultFor LayoutWorkflow.NoSession
      Relevance = PanelId.all |> List.map (fun pane -> pane, PaneRelevance.OnPage)
      Health = LayoutHealth.Healthy
      ThisWorkflow = LayoutStanding.IsDefault
      EveryWorkflow = LayoutStanding.IsDefault }

  let relevanceFor (pane: PanelId) (view: PaneLayoutView) : PaneRelevance =
    view.Relevance |> List.tryFind (fun (p, _) -> p = pane) |> Option.map snd |> Option.defaultValue PaneRelevance.OnPage

let testid (name: string) = Attr.create "data-testid" name

/// The request one button posts. The edit rides the query string like the seat route's member does, so the button needs no signal.
let editPost (edit: BookEdit) : string =
  Ds.post (sprintf "%s?e=%s" DashboardRoutes.PaneLayoutEdit (Uri.EscapeDataString(BookEdit.token edit)))

/// Whether a button can be pressed. One that would change nothing is drawn disabled, which is the truth, and says why in its name.
[<RequireQualifiedAccess>]
type ButtonState =
  | Offers of BookEdit
  | Unavailable

/// One button of the menu: its id (also its test id), the word on it, the name a screen reader says, and whether it can be pressed.
type MenuButton =
  { ButtonId: string
    Word: string
    Name: string
    State: ButtonState }

let menuButton (button: MenuButton) : XmlNode =
  Elem.button
    ([ Attr.id button.ButtonId
       Attr.class' "session-btn session-btn-labeled pane-menu-btn"
       Attr.type' "button"
       testid button.ButtonId
       Attr.create "aria-label" button.Name
       Attr.create "title" button.Name ]
     @ (match button.State with
        | ButtonState.Offers edit -> [ Ds.onClick (editPost edit) ]
        | ButtonState.Unavailable -> [ Attr.create "disabled" "" ]))
    [ Text.enc button.Word ]

/// One group of the menu: the panels in a region, or the closed ones, under a heading, with the words for an empty group.
type MenuGroup =
  { GroupId: string
    Heading: string
    Panes: PanelId list
    EmptyWords: string }

let relevanceNote (relevance: PaneRelevance) : XmlNode =
  match relevance with
  | PaneRelevance.OnPage -> Text.raw ""
  | PaneRelevance.ReasonLine name ->
    Elem.span [ Attr.class' "pane-menu-note meta" ] [ Text.enc (sprintf "shows the line \"%s\" for now" name) ]
  | PaneRelevance.NothingToShow why ->
    Elem.span [ Attr.class' "pane-menu-note meta" ] [ Text.enc (sprintf "nothing to show: %s" why) ]

let row (view: PaneLayoutView) (pane: PanelId) : XmlNode =
  let workflow = view.Workflow
  let edit (inner: LayoutEdit) = BookEdit.InWorkflow(workflow, inner)
  let label = PanelId.label pane
  let place = PaneLayout.place pane view.Layout
  let placeToken =
    match place with
    | PanePlace.InRegion(region, _) -> PaneRegion.token region
    | PanePlace.ClosedByUser -> "closed"
  let buttonId (slug: string) : string = sprintf "pane-%s-%s" (PanelId.token pane) slug
  let offersWhen (allowed: bool) (inner: LayoutEdit) : ButtonState =
    match allowed with
    | true -> ButtonState.Offers(edit inner)
    | false -> ButtonState.Unavailable
  let buttons =
    match place with
    | PanePlace.InRegion(region, _) ->
      let other = PaneRegion.other region
      [ menuButton
          { ButtonId = buttonId "move"
            Word = sprintf "To %s" (PaneRegion.token other)
            Name = sprintf "Move %s to %s" label (PaneRegion.label other)
            State = offersWhen true (LayoutEdit.MoveToRegion(pane, other)) }
        menuButton
          { ButtonId = buttonId "up"
            Word = "Up"
            Name = sprintf "Move %s up in %s" label (PaneRegion.label region)
            State = offersWhen (PaneLayout.canMoveUp pane view.Layout) (LayoutEdit.MoveUp pane) }
        menuButton
          { ButtonId = buttonId "down"
            Word = "Down"
            Name = sprintf "Move %s down in %s" label (PaneRegion.label region)
            State = offersWhen (PaneLayout.canMoveDown pane view.Layout) (LayoutEdit.MoveDown pane) }
        menuButton
          { ButtonId = buttonId "close"
            Word = "Close"
            Name = sprintf "Close %s" label
            State = offersWhen true (LayoutEdit.Close pane) } ]
    | PanePlace.ClosedByUser ->
      [ menuButton
          { ButtonId = buttonId "reopen"
            Word = "Reopen"
            Name = sprintf "Reopen %s in %s" label (PaneRegion.label (PaneLayout.defaultRegion workflow pane))
            State = offersWhen true (LayoutEdit.Reopen pane) } ]
  Elem.li
    [ Attr.class' "pane-menu-row"
      Attr.create "data-pane" (PanelId.token pane)
      Attr.create "data-place" placeToken ]
    [ Elem.span [ Attr.class' "pane-menu-name" ] [ Text.enc label ]
      (match place with
       | PanePlace.InRegion _ -> relevanceNote (PaneLayoutView.relevanceFor pane view)
       | PanePlace.ClosedByUser -> Elem.span [ Attr.class' "pane-menu-note meta" ] [ Text.enc "closed: its place is empty until you reopen it" ])
      Elem.span [ Attr.class' "pane-menu-actions" ] buttons ]

let group (view: PaneLayoutView) (group: MenuGroup) : XmlNode =
  Elem.section [ Attr.class' "pane-menu-group"; Attr.create "aria-labelledby" group.GroupId ] [
    Elem.h3 [ Attr.id group.GroupId; Attr.class' "pane-menu-heading" ] [ Text.enc group.Heading ]
    match group.Panes with
    | [] -> Elem.p [ Attr.class' "pane-menu-empty meta" ] [ Text.enc group.EmptyWords ]
    | panes -> Elem.ul [ Attr.class' "pane-menu-list" ] (panes |> List.map (row view))
  ]

let healthNote (health: LayoutHealth) : XmlNode =
  match health with
  | LayoutHealth.Healthy -> Text.raw ""
  | LayoutHealth.FellBackToDefault reason ->
    Elem.p [ Attr.class' "pane-menu-health"; testid "pane-menu-health"; Attr.create "role" "status" ] [
      Text.enc (sprintf "The saved layout could not be used, so the default is shown: %s" reason) ]
  | LayoutHealth.UsedWithNotes notes ->
    Elem.ul [ Attr.class' "pane-menu-health"; testid "pane-menu-health"; Attr.create "role" "status" ]
      (notes |> List.map (fun note -> Elem.li [] [ Text.enc note ]))

/// The menu. `Ds.preserveAttr "open"` leaves it open across a morph, so a person can move several panels in one visit.
let render (view: PaneLayoutView) : XmlNode =
  let workflow = view.Workflow
  let inRegion (region: PaneRegion) = PaneLayout.panes region view.Layout
  Elem.details [ Attr.id "pane-menu"; Attr.class' "pane-menu"; testid "pane-menu"; Ds.preserveAttr "open" ] [
    Elem.summary
      [ Attr.class' "expand-toggle-btn pane-menu-toggle"
        testid "pane-menu-toggle"
        Attr.create "aria-label" "Panels: move, close and reopen the dashboard's panels"
        Attr.create "title" "Panels: move, close and reopen" ]
      [ Elem.span [ Attr.class' "pane-menu-glyph"; Attr.create "aria-hidden" "true" ] [ Text.enc (Glyphs.text Glyph.List) ]
        Elem.span [ Attr.class' "pane-menu-word" ] [ Text.enc " Panels" ] ]
    Elem.div [ Attr.class' "pane-menu-body"; Attr.create "role" "group"; Attr.create "aria-labelledby" "pane-menu-title" ] [
      Elem.div [ Attr.id "pane-menu-title"; Attr.class' "pane-menu-title" ] [
        Text.enc (sprintf "Panels for %s" (LayoutWorkflow.label workflow)) ]
      Elem.p [ Attr.class' "pane-menu-help meta" ] [
        Text.enc "Each workflow keeps its own layout. The output and the Sessions list stay where they are." ]
      healthNote view.Health
      group view { GroupId = "pane-group-sidebar"; Heading = "In the sidebar"; Panes = inRegion PaneRegion.Sidebar; EmptyWords = "no panel is in the sidebar" }
      group view { GroupId = "pane-group-dock"; Heading = "In the dock, under the output"; Panes = inRegion PaneRegion.Dock; EmptyWords = "no panel is in the dock" }
      group view { GroupId = "pane-group-closed"; Heading = "Closed"; Panes = view.Layout.Closed; EmptyWords = "no panel is closed" }
      Elem.div [ Attr.class' "pane-menu-foot" ] [
        menuButton
          { ButtonId = "pane-reset"
            Word = "Reset this layout"
            Name = sprintf "Reset the layout for %s to its default" (LayoutWorkflow.label workflow)
            State = (match view.ThisWorkflow with
                     | LayoutStanding.IsDefault -> ButtonState.Unavailable
                     | LayoutStanding.HasChanges -> ButtonState.Offers(BookEdit.InWorkflow(workflow, LayoutEdit.Reset))) }
        menuButton
          { ButtonId = "pane-reset-all"
            Word = "Reset all layouts"
            Name = "Reset the layout of every workflow to its default"
            State = (match view.EveryWorkflow with
                     | LayoutStanding.IsDefault -> ButtonState.Unavailable
                     | LayoutStanding.HasChanges -> ButtonState.Offers BookEdit.ResetAll) } ]
    ]
  ]

/// The dock region under the output: the panels placed in it, in order. Nothing at all when none is placed there, so a layout with
/// an empty dock draws the page it always drew.
let renderDock (nodes: XmlNode list) : XmlNode =
  match nodes with
  | [] -> Text.raw ""
  | placed ->
    Elem.div
      [ Attr.id "pane-dock"
        Attr.class' "pane-dock"
        testid "pane-dock"
        Attr.create "role" "region"
        Attr.create "aria-label" "Panels docked under the output" ]
      placed
