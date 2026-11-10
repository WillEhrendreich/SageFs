namespace SageFs

open System

/// Where the dashboard's movable panels sit, as pure data (docs/plans/move-and-dock-dashboard-panes.md).
///
/// A layout says, for one workflow, which panels are in the sidebar, which are in the dock under the output, and which the person
/// closed, each region in the order the person chose. Every function here is total and pure: an edit takes a layout and gives a
/// layout, a layout that came from text is normalised on the way in, and nothing here knows about HTML, files or the daemon. The
/// store (`PaneLayoutStore`) reads and writes the text; the dashboard draws the answer.
///
/// The output and the Sessions list are not panes. They are always on the page, so no edit can move or close them.

/// A panel a person can move, close and reopen. Listed in the order the sidebar has always drawn them.
[<RequireQualifiedAccess>]
type PanelId =
  | HotReload
  | LiveTesting
  | SessionContext
  | Friction
  | AgentTeam
  | Hygiene

/// A place a panel can be drawn.
[<RequireQualifiedAccess>]
type PaneRegion =
  /// The column beside the output, under the Sessions list.
  | Sidebar
  /// The strip under the output, above the live bindings' own dock.
  | Dock

/// The state of a session a layout belongs to. The workflow of the viewed session picks it; with no session in view the layout is
/// `NoSession`. `WorkflowTypes.SessionWorkflow` carries data, so it cannot be a settings key: this is the closed set a key is built from.
[<RequireQualifiedAccess>]
type LayoutWorkflow =
  | Repl
  | HotReload
  | LiveTesting
  | NoSession

/// Why a piece of text did not become a layout, an edit or a pane. Each case names the text it could not read.
[<RequireQualifiedAccess>]
type LayoutRefusal =
  | UnknownPane of text: string
  | UnknownRegion of text: string
  | UnknownWorkflow of text: string
  | UnknownEdit of text: string
  | MalformedSegment of text: string
  | NoEdit

/// Something about saved text that was read and used, with a part set aside: the menu says so, so a hand-edited file is never
/// silently changed.
[<RequireQualifiedAccess>]
type LayoutNote =
  | UnknownPaneDropped of text: string
  | PaneListedTwice of PanelId
  | PaneMissingTookDefault of PanelId

/// Where one pane is.
[<RequireQualifiedAccess>]
type PanePlace =
  | InRegion of PaneRegion * slot: int
  | ClosedByUser

/// Every pane is in exactly one of the three lists, once.
type PaneLayout =
  { Sidebar: PanelId list
    Dock: PanelId list
    Closed: PanelId list }

/// The names saved text lists for each place, before any of them is known to be a pane.
type SavedPaneNames =
  { SidebarNames: string list
    DockNames: string list
    ClosedNames: string list }

/// One change a person asks for. `Reset` puts this workflow's layout back to its default.
[<RequireQualifiedAccess>]
type LayoutEdit =
  /// To the end of the region. A closed pane is opened there.
  | MoveToRegion of PanelId * PaneRegion
  | MoveUp of PanelId
  | MoveDown of PanelId
  | Close of PanelId
  /// Back to the region it starts in, at the end.
  | Reopen of PanelId
  | Reset

/// An edit aimed at one workflow's layout, or the reset of every workflow's.
[<RequireQualifiedAccess>]
type BookEdit =
  | InWorkflow of LayoutWorkflow * LayoutEdit
  | ResetAll

/// One change to the saved text, by key. The store carries it out against the settings layer file.
[<RequireQualifiedAccess>]
type LayoutWrite =
  | Save of key: string * text: string
  | Clear of key: string

[<RequireQualifiedAccess>]
module PanelId =
  /// Every pane, in the order the sidebar draws them by default.
  let all : PanelId list =
    [ PanelId.HotReload; PanelId.LiveTesting; PanelId.SessionContext; PanelId.Friction; PanelId.AgentTeam; PanelId.Hygiene ]

  /// The word a request and the saved text use.
  let token (pane: PanelId) : string =
    match pane with
    | PanelId.HotReload -> "hot-reload"
    | PanelId.LiveTesting -> "live-testing"
    | PanelId.SessionContext -> "session-context"
    | PanelId.Friction -> "friction"
    | PanelId.AgentTeam -> "cohort"
    | PanelId.Hygiene -> "hygiene"

  /// The name a person reads on the menu.
  let label (pane: PanelId) : string =
    match pane with
    | PanelId.HotReload -> "Hot reload"
    | PanelId.LiveTesting -> "Live testing"
    | PanelId.SessionContext -> "Session context"
    | PanelId.Friction -> "Friction reports"
    | PanelId.AgentTeam -> "Agent team"
    | PanelId.Hygiene -> "Workspace hygiene"

  let ofToken (text: string) : Result<PanelId, LayoutRefusal> =
    match all |> List.tryFind (fun pane -> token pane = text) with
    | Some pane -> Result.Ok pane
    | None -> Result.Error(LayoutRefusal.UnknownPane text)

[<RequireQualifiedAccess>]
module PaneRegion =
  let all : PaneRegion list = [ PaneRegion.Sidebar; PaneRegion.Dock ]

  let token (region: PaneRegion) : string =
    match region with
    | PaneRegion.Sidebar -> "sidebar"
    | PaneRegion.Dock -> "dock"

  let label (region: PaneRegion) : string =
    match region with
    | PaneRegion.Sidebar -> "the sidebar"
    | PaneRegion.Dock -> "the dock"

  /// The other region, for the "move to" button that is always the one that changes something.
  let other (region: PaneRegion) : PaneRegion =
    match region with
    | PaneRegion.Sidebar -> PaneRegion.Dock
    | PaneRegion.Dock -> PaneRegion.Sidebar

  let ofToken (text: string) : Result<PaneRegion, LayoutRefusal> =
    match all |> List.tryFind (fun region -> token region = text) with
    | Some region -> Result.Ok region
    | None -> Result.Error(LayoutRefusal.UnknownRegion text)

[<RequireQualifiedAccess>]
module LayoutWorkflow =
  let all : LayoutWorkflow list =
    [ LayoutWorkflow.Repl; LayoutWorkflow.HotReload; LayoutWorkflow.LiveTesting; LayoutWorkflow.NoSession ]

  let token (workflow: LayoutWorkflow) : string =
    match workflow with
    | LayoutWorkflow.Repl -> "repl"
    | LayoutWorkflow.HotReload -> "hotreload"
    | LayoutWorkflow.LiveTesting -> "livetesting"
    | LayoutWorkflow.NoSession -> "nosession"

  let label (workflow: LayoutWorkflow) : string =
    match workflow with
    | LayoutWorkflow.Repl -> "REPL sessions"
    | LayoutWorkflow.HotReload -> "Hot reload sessions"
    | LayoutWorkflow.LiveTesting -> "Live testing sessions"
    | LayoutWorkflow.NoSession -> "no session"

  let ofToken (text: string) : Result<LayoutWorkflow, LayoutRefusal> =
    match all |> List.tryFind (fun workflow -> token workflow = text) with
    | Some workflow -> Result.Ok workflow
    | None -> Result.Error(LayoutRefusal.UnknownWorkflow text)

  /// The settings key a workflow's layout is saved under, in the global layer file.
  let key (workflow: LayoutWorkflow) : string = "dashboard.layout." + token workflow

[<RequireQualifiedAccess>]
module LayoutRefusal =
  let describe (refusal: LayoutRefusal) : string =
    match refusal with
    | LayoutRefusal.UnknownPane text -> sprintf "'%s' is not a pane" text
    | LayoutRefusal.UnknownRegion text -> sprintf "'%s' is not a region (sidebar or dock)" text
    | LayoutRefusal.UnknownWorkflow text -> sprintf "'%s' is not a workflow" text
    | LayoutRefusal.UnknownEdit text -> sprintf "'%s' is not a layout edit" text
    | LayoutRefusal.MalformedSegment text -> sprintf "'%s' is not of the form region=pane,pane" text
    | LayoutRefusal.NoEdit -> "no layout edit was given"

[<RequireQualifiedAccess>]
module LayoutNote =
  let describe (note: LayoutNote) : string =
    match note with
    | LayoutNote.UnknownPaneDropped text -> sprintf "the saved layout named '%s', which is not a pane, so it was left out" text
    | LayoutNote.PaneListedTwice pane -> sprintf "the saved layout listed %s twice, so the first place counts" (PanelId.label pane)
    | LayoutNote.PaneMissingTookDefault pane -> sprintf "the saved layout did not place %s, so it took its default place" (PanelId.label pane)

[<RequireQualifiedAccess>]
module PaneLayout =
  /// The region a pane starts in. Today every pane starts in the sidebar, for every workflow; the match is here so a workflow
  /// that wants a pane elsewhere says so in one place.
  let defaultRegion (workflow: LayoutWorkflow) (pane: PanelId) : PaneRegion =
    match workflow, pane with
    | LayoutWorkflow.Repl, _
    | LayoutWorkflow.HotReload, _
    | LayoutWorkflow.LiveTesting, _
    | LayoutWorkflow.NoSession, _ -> PaneRegion.Sidebar

  /// The order the panes start in, for one workflow. Today the order the sidebar has always drawn them in.
  let defaultOrder (workflow: LayoutWorkflow) : PanelId list =
    match workflow with
    | LayoutWorkflow.Repl
    | LayoutWorkflow.HotReload
    | LayoutWorkflow.LiveTesting
    | LayoutWorkflow.NoSession -> PanelId.all

  let panes (region: PaneRegion) (layout: PaneLayout) : PanelId list =
    match region with
    | PaneRegion.Sidebar -> layout.Sidebar
    | PaneRegion.Dock -> layout.Dock

  let private withRegion (region: PaneRegion) (panes: PanelId list) (layout: PaneLayout) : PaneLayout =
    match region with
    | PaneRegion.Sidebar -> { layout with Sidebar = panes }
    | PaneRegion.Dock -> { layout with Dock = panes }

  /// Every pane placed once and none twice: the three lists, together, hold exactly `PanelId.all`.
  let isValid (layout: PaneLayout) : bool =
    let placed = layout.Sidebar @ layout.Dock @ layout.Closed
    List.length placed = List.length PanelId.all
    && (placed |> List.sortBy PanelId.token) = (PanelId.all |> List.sortBy PanelId.token)

  /// A layout built from three lists that may repeat a pane, miss one or hold unknown names: the first place a pane is listed
  /// counts, a missing pane goes to its default region at the end, and what was set aside is returned as notes.
  let normalise (workflow: LayoutWorkflow) (saved: SavedPaneNames) : PaneLayout * LayoutNote list =
    let seen = Collections.Generic.HashSet<PanelId>()
    let notes = Collections.Generic.List<LayoutNote>()
    let take (names: string list) : PanelId list =
      [ for name in names do
          match PanelId.ofToken name with
          | Result.Error _ -> notes.Add(LayoutNote.UnknownPaneDropped name)
          | Result.Ok pane ->
            match seen.Add pane with
            | true -> yield pane
            | false -> notes.Add(LayoutNote.PaneListedTwice pane) ]
    let kept = { Sidebar = take saved.SidebarNames; Dock = take saved.DockNames; Closed = take saved.ClosedNames }
    let missing = PanelId.all |> List.filter (fun pane -> not (seen.Contains pane))
    let filled =
      missing
      |> List.fold
        (fun layout pane ->
          notes.Add(LayoutNote.PaneMissingTookDefault pane)
          let region = defaultRegion workflow pane
          withRegion region (panes region layout @ [ pane ]) layout)
        kept
    filled, List.ofSeq notes

  /// The layout a workflow starts with. Reset returns to it.
  let defaultFor (workflow: LayoutWorkflow) : PaneLayout =
    let order = defaultOrder workflow
    let inRegion (region: PaneRegion) = order |> List.filter (fun pane -> defaultRegion workflow pane = region)
    { Sidebar = inRegion PaneRegion.Sidebar
      Dock = inRegion PaneRegion.Dock
      Closed = [] }

  let regionOf (pane: PanelId) (layout: PaneLayout) : PaneRegion option =
    match List.contains pane layout.Sidebar, List.contains pane layout.Dock with
    | true, _ -> Some PaneRegion.Sidebar
    | false, true -> Some PaneRegion.Dock
    | false, false -> None

  let place (pane: PanelId) (layout: PaneLayout) : PanePlace =
    match regionOf pane layout with
    | Some region -> PanePlace.InRegion(region, panes region layout |> List.findIndex ((=) pane))
    | None -> PanePlace.ClosedByUser

  let private without (pane: PanelId) (layout: PaneLayout) : PaneLayout =
    { Sidebar = layout.Sidebar |> List.filter ((<>) pane)
      Dock = layout.Dock |> List.filter ((<>) pane)
      Closed = layout.Closed |> List.filter ((<>) pane) }

  let private appendTo (region: PaneRegion) (pane: PanelId) (layout: PaneLayout) : PaneLayout =
    withRegion region (panes region layout @ [ pane ]) layout

  /// The pane swapped with its neighbour `offset` slots away (-1 up, +1 down); nothing at an end of the region.
  let private shift (offset: int) (pane: PanelId) (layout: PaneLayout) : PaneLayout =
    match regionOf pane layout with
    | None -> layout
    | Some region ->
      let items = panes region layout |> Array.ofList
      let index = Array.IndexOf(items, pane)
      let target = index + offset
      match target >= 0 && target < items.Length with
      | false -> layout
      | true ->
        let swapped = Array.copy items
        swapped.[index] <- items.[target]
        swapped.[target] <- items.[index]
        withRegion region (List.ofArray swapped) layout

  /// Whether a move up or down would change anything, for the buttons that are drawn disabled when it would not.
  let canMoveUp (pane: PanelId) (layout: PaneLayout) : bool =
    match place pane layout with
    | PanePlace.InRegion(_, slot) -> slot > 0
    | PanePlace.ClosedByUser -> false

  let canMoveDown (pane: PanelId) (layout: PaneLayout) : bool =
    match place pane layout with
    | PanePlace.InRegion(region, slot) -> slot < List.length (panes region layout) - 1
    | PanePlace.ClosedByUser -> false

  /// One edit. Total: an edit that cannot change anything (a pane already last, already closed) returns the layout as it was.
  let apply (workflow: LayoutWorkflow) (edit: LayoutEdit) (layout: PaneLayout) : PaneLayout =
    match edit with
    | LayoutEdit.MoveToRegion(pane, region) ->
      match regionOf pane layout with
      | Some current when current = region -> layout
      | Some _
      | None -> layout |> without pane |> appendTo region pane
    | LayoutEdit.MoveUp pane -> shift -1 pane layout
    | LayoutEdit.MoveDown pane -> shift 1 pane layout
    | LayoutEdit.Close pane ->
      match List.contains pane layout.Closed with
      | true -> layout
      | false ->
        let removed = without pane layout
        { removed with Closed = removed.Closed @ [ pane ] }
    | LayoutEdit.Reopen pane ->
      match List.contains pane layout.Closed with
      | false -> layout
      | true -> layout |> without pane |> appendTo (defaultRegion workflow pane) pane
    | LayoutEdit.Reset -> defaultFor workflow

  // ── The saved text ────────────────────────────────────────────────────────

  /// `sidebar=hot-reload,live-testing;dock=friction;closed=hygiene`. The one spelling, so a layout and its text are the same thing.
  let render (layout: PaneLayout) : string =
    let list (panes: PanelId list) = panes |> List.map PanelId.token |> String.concat ","
    sprintf "sidebar=%s;dock=%s;closed=%s" (list layout.Sidebar) (list layout.Dock) (list layout.Closed)

  /// Text to a layout, with the notes for anything set aside. A segment that is not `region=list`, or a region that is not one of
  /// the three, is refused: the file is then not a layout and the default is shown, with the reason on the menu.
  let parse (workflow: LayoutWorkflow) (text: string) : Result<PaneLayout * LayoutNote list, LayoutRefusal> =
    let split (value: string) : string list =
      value.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun item -> item.Trim()) |> List.ofArray
    let segments =
      text.Split([| ';' |], StringSplitOptions.RemoveEmptyEntries)
      |> Array.map (fun segment -> segment.Trim())
      |> Array.filter (fun segment -> segment <> "")
      |> List.ofArray
    let read (acc: Result<Map<string, string list>, LayoutRefusal>) (segment: string) =
      acc
      |> Result.bind (fun found ->
        match segment.IndexOf '=' with
        | -1 -> Result.Error(LayoutRefusal.MalformedSegment segment)
        | at ->
          let name = segment.Substring(0, at).Trim()
          match name with
          | "sidebar" | "dock" | "closed" -> Result.Ok(Map.add name (split (segment.Substring(at + 1))) found)
          | unknownRegion -> Result.Error(LayoutRefusal.MalformedSegment(sprintf "%s=%s" unknownRegion (segment.Substring(at + 1).Trim()))))
    segments
    |> List.fold read (Result.Ok Map.empty)
    |> Result.map (fun found ->
      let listOf (name: string) = Map.tryFind name found |> Option.defaultValue []
      normalise workflow { SidebarNames = listOf "sidebar"; DockNames = listOf "dock"; ClosedNames = listOf "closed" })

[<RequireQualifiedAccess>]
module LayoutEdit =
  /// `move:cohort:dock`, `up:cohort`, `down:cohort`, `close:cohort`, `reopen:cohort` or `reset`.
  let token (edit: LayoutEdit) : string =
    match edit with
    | LayoutEdit.MoveToRegion(pane, region) -> sprintf "move:%s:%s" (PanelId.token pane) (PaneRegion.token region)
    | LayoutEdit.MoveUp pane -> sprintf "up:%s" (PanelId.token pane)
    | LayoutEdit.MoveDown pane -> sprintf "down:%s" (PanelId.token pane)
    | LayoutEdit.Close pane -> sprintf "close:%s" (PanelId.token pane)
    | LayoutEdit.Reopen pane -> sprintf "reopen:%s" (PanelId.token pane)
    | LayoutEdit.Reset -> "reset"

  let parse (text: string) : Result<LayoutEdit, LayoutRefusal> =
    match text.Split ':' |> List.ofArray with
    | [ "" ] -> Result.Error LayoutRefusal.NoEdit
    | [ "reset" ] -> Result.Ok LayoutEdit.Reset
    | [ "move"; pane; region ] ->
      PanelId.ofToken pane
      |> Result.bind (fun pane -> PaneRegion.ofToken region |> Result.map (fun region -> LayoutEdit.MoveToRegion(pane, region)))
    | [ "up"; pane ] -> PanelId.ofToken pane |> Result.map LayoutEdit.MoveUp
    | [ "down"; pane ] -> PanelId.ofToken pane |> Result.map LayoutEdit.MoveDown
    | [ "close"; pane ] -> PanelId.ofToken pane |> Result.map LayoutEdit.Close
    | [ "reopen"; pane ] -> PanelId.ofToken pane |> Result.map LayoutEdit.Reopen
    | otherShape -> Result.Error(LayoutRefusal.UnknownEdit(String.Join(":", otherShape)))

[<RequireQualifiedAccess>]
module BookEdit =
  /// `repl/move:cohort:dock`, `hotreload/reset`, or `all/reset`.
  let token (edit: BookEdit) : string =
    match edit with
    | BookEdit.InWorkflow(workflow, inner) -> sprintf "%s/%s" (LayoutWorkflow.token workflow) (LayoutEdit.token inner)
    | BookEdit.ResetAll -> "all/reset"

  let parse (text: string) : Result<BookEdit, LayoutRefusal> =
    match text.IndexOf '/' with
    | -1 -> Result.Error(LayoutRefusal.UnknownEdit text)
    | at ->
      let head = text.Substring(0, at)
      let rest = text.Substring(at + 1)
      match head, rest with
      | "all", "reset" -> Result.Ok BookEdit.ResetAll
      | "all", _ -> Result.Error(LayoutRefusal.UnknownEdit text)
      | workflowToken, _ ->
        LayoutWorkflow.ofToken workflowToken
        |> Result.bind (fun workflow -> LayoutEdit.parse rest |> Result.map (fun inner -> BookEdit.InWorkflow(workflow, inner)))

/// Every workflow's layout, as the saved layer holds them. Absent means the default; a saved layout that cannot be read means the
/// default too, with the reason kept so the menu can say so.
type LayoutBook =
  { Layouts: Map<LayoutWorkflow, PaneLayout>
    Problems: Map<LayoutWorkflow, LayoutRefusal>
    Notes: Map<LayoutWorkflow, LayoutNote list> }

[<RequireQualifiedAccess>]
module LayoutBook =
  let empty : LayoutBook = { Layouts = Map.empty; Problems = Map.empty; Notes = Map.empty }

  /// Read the layouts out of a settings layer (`key -> raw value`): the keys that are not layout keys are ignored.
  let read (layer: Map<string, string>) : LayoutBook =
    LayoutWorkflow.all
    |> List.fold
      (fun book workflow ->
        match Map.tryFind (LayoutWorkflow.key workflow) layer with
        | None -> book
        | Some text ->
          match PaneLayout.parse workflow text with
          | Result.Ok(layout, notes) ->
            { book with
                Layouts = Map.add workflow layout book.Layouts
                Notes = (match notes with [] -> book.Notes | listed -> Map.add workflow listed book.Notes) }
          | Result.Error refusal -> { book with Problems = Map.add workflow refusal book.Problems })
      empty

  let layoutOf (workflow: LayoutWorkflow) (book: LayoutBook) : PaneLayout =
    Map.tryFind workflow book.Layouts |> Option.defaultWith (fun () -> PaneLayout.defaultFor workflow)

  let problemOf (workflow: LayoutWorkflow) (book: LayoutBook) : LayoutRefusal option = Map.tryFind workflow book.Problems

  let notesOf (workflow: LayoutWorkflow) (book: LayoutBook) : LayoutNote list =
    Map.tryFind workflow book.Notes |> Option.defaultValue []

  /// The writes an edit makes against the layer as it stands now: the touched workflow's layout, saved when it differs from the
  /// default and cleared when it equals it (only an override is stored), and nothing at all for an edit that changes nothing.
  /// Every other workflow's key is left alone, which is the property two tabs editing different workflows rely on.
  let writesFor (layer: Map<string, string>) (edit: BookEdit) : LayoutWrite list =
    let book = read layer
    let writeFor (workflow: LayoutWorkflow) (inner: LayoutEdit) : LayoutWrite list =
      let before = layoutOf workflow book
      let after = PaneLayout.apply workflow inner before
      let key = LayoutWorkflow.key workflow
      match after = PaneLayout.defaultFor workflow, Map.containsKey key layer with
      | true, true -> [ LayoutWrite.Clear key ]
      | true, false -> []
      | false, _ ->
        match after = before && Map.containsKey key layer && not (Map.containsKey workflow book.Problems) with
        | true -> []
        | false -> [ LayoutWrite.Save(key, PaneLayout.render after) ]
    match edit with
    | BookEdit.InWorkflow(workflow, inner) -> writeFor workflow inner
    | BookEdit.ResetAll ->
      LayoutWorkflow.all
      |> List.filter (fun workflow -> Map.containsKey (LayoutWorkflow.key workflow) layer)
      |> List.map (fun workflow -> LayoutWrite.Clear(LayoutWorkflow.key workflow))
