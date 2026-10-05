/// The live bindings, in the dashboard's bottom dock: a watch window, where the Evaluate box used to be.
///
/// What is on screen is `DockPanes.PaneState.decide`'s answer, drawn. A session with bindings shows the pane. A session with none
/// collapses it to ONE line that says why and offers a pin that opens it anyway. No session says that, with no pin to offer.
/// The pin is a Datastar signal (`DockSignals.LivePanePinned`): the server renders both answers and the signal picks, so the
/// GET render and the SSE stream cannot disagree about it, and a morph cannot undo it.
///
/// The pane is a tree: a record by field, a union by case, a collection by item, bounded in depth and size by the walk itself
/// (a row that was cut says so). A change shows as a brief flash on the value that changed, drawn by `dashboard.css` from
/// what the SSE morph really rewrote, so only a changed value flashes and nothing polls. Each top-level binding can be pinned to
/// the top, and a filter by name hides the rest. Both are signals too, applied with `data-class`, so the one `#main` morph is
/// still the only render path and a push never resets what the user chose.
///
/// A row the walk listed without reading keeps its click-to-run button, its deadline and its refusals exactly as they were.
module SageFs.Server.LiveBindingsDock

open System
open Falco.Markup
open Falco.Datastar
open SageFs
open SageFs.Features
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Server.DockPanes

/// Client-side state of the dock. All three are plain signals declared once in the page shell.
module DockSignals =
  /// What the filter box holds.
  let [<Literal>] LiveFilter = "liveFilter"
  /// The top-level bindings pinned to the top, as a comma-joined list of the names' hex, with a comma on both ends.
  let [<Literal>] LivePins = "livePins"
  /// Whether the pane is pinned open even with no bindings.
  let [<Literal>] LivePanePinned = "livePanePinned"

  /// The initial value of every signal above, for the page shell.
  let initial : XmlAttribute list =
    [ Ds.signal (LiveFilter, "")
      Ds.signal (LivePins, ",")
      Ds.signal (LivePanePinned, false) ]

module DockIds =
  let [<Literal>] Dock = "live-dock"
  let [<Literal>] Pane = "live-pane"
  let [<Literal>] CollapsedBar = "live-dock-bar"
  let [<Literal>] Filter = "live-filter"
  let [<Literal>] Tree = "live-tree"
  let [<Literal>] BarPin = "live-bar-pin"
  let [<Literal>] PanePin = "live-pane-pin"
  let [<Literal>] EmptyState = "live-empty-state"
  let [<Literal>] NoMatch = "live-nomatch"

  /// Every element the morph keeps client-owned attributes on has an id of its own, so the morph matches it by id and never
  /// by position: matched by position, a preserved `class` would be carried from whatever stood in that slot before.
  let binding (name: string) : string = sprintf "lb_%s" (Convert.ToHexStringLower(Text.Encoding.UTF8.GetBytes name))
  let bindingPin (name: string) : string = sprintf "lbp_%s" (Convert.ToHexStringLower(Text.Encoding.UTF8.GetBytes name))

/// What the dock has to show, before it is decided how.
type BindingsSource =
  /// The worker's walked values: the tree.
  | WalkedBindings of LiveBindingsPane.PaneView
  /// No walk has come back (a session that has not answered one yet), but FSI printed bindings: the text panel that always
  /// listed them, with its shadowed names.
  | PrintedBindings of BindingExplorer.BindingScopeSnapshot
  | NothingBound

module BindingsSource =
  let presence (source: BindingsSource) : BindingsPresence =
    match source with
    | WalkedBindings view -> BindingsPresence.ofCount view.Snapshot.Bindings.Length
    | PrintedBindings scope -> BindingsPresence.ofCount scope.ActiveBindings.Count
    | NothingBound -> NoBindingsYet

  /// The prefix of the names the tooling binds in every session (`_SageFsHotReload`, `_SageFsCompExpr`). They are not the
  /// user's bindings, so they are not on the watch window and not in its count.
  let [<Literal>] InfrastructurePrefix = "_SageFs"

  let isInfrastructure (name: string) : bool = name.StartsWith(InfrastructurePrefix, StringComparison.Ordinal)

  /// The walked values of a session, without the tooling's own bindings.
  let ofWalked (view: LiveBindingsPane.PaneView) : BindingsSource =
    let own = view.Snapshot.Bindings |> List.filter (fun binding -> not (isInfrastructure binding.Name))
    WalkedBindings { view with Snapshot = { view.Snapshot with Bindings = own } }

  /// What FSI printed, if it printed any. For a session with no walk yet; the walked values win whenever there are any.
  let ofPrinted (printed: BindingExplorer.BindingScopeSnapshot option) : BindingsSource =
    match printed with
    | Some scope when scope.ActiveBindings.Count > 0 -> PrintedBindings scope
    | Some _
    | None -> NothingBound

/// How a pane's visibility follows the pin.
type PinDisplay =
  | ShownAlways
  | ShownUnlessPinned
  | ShownOnlyWhilePinned

/// What the pane's kind of value is, in one word. Exhaustive, so a new kind cannot arrive unnamed.
let kindLabel (kind: LiveValueTree.NodeKind) : string =
  match kind with
  | LiveValueTree.NodeKind.Leaf -> "value"
  | LiveValueTree.NodeKind.NotEvaluated _ -> "held"
  | LiveValueTree.NodeKind.Record -> "record"
  | LiveValueTree.NodeKind.List -> "list"
  | LiveValueTree.NodeKind.Map -> "map"
  | LiveValueTree.NodeKind.Option -> "option"
  | LiveValueTree.NodeKind.Union -> "union"
  | LiveValueTree.NodeKind.Tuple -> "tuple"
  | LiveValueTree.NodeKind.Array -> "array"
  | LiveValueTree.NodeKind.Class -> "class"
  | LiveValueTree.NodeKind.Closure -> "closure"
  | LiveValueTree.NodeKind.Cycle -> "cycle"
  | LiveValueTree.NodeKind.Truncated -> "truncated"

let private hexOf (text: string) : string = Convert.ToHexStringLower(Text.Encoding.UTF8.GetBytes text)

/// The class the filter and the pins toggle on a top-level binding. Both are written with `Ds.class'`, and the morph is told to
/// leave `class` alone on that element, so a push never resets them.
let private signalClasses (cls: string) : XmlAttribute list =
  [ Attr.class' cls
    Ds.preserveAttr "class" ]

let private pinToken (binding: string) : string = sprintf ",%s," (hexOf binding)

let private isPinnedExpr (binding: string) : string =
  sprintf "$%s.includes('%s')" DockSignals.LivePins (pinToken binding)

let private togglePinExpr (binding: string) : string =
  sprintf "$%s = %s ? $%s.replace('%s', ',') : $%s + '%s'" DockSignals.LivePins (isPinnedExpr binding) DockSignals.LivePins (pinToken binding) DockSignals.LivePins (hexOf binding + ",")

/// True while the filter is set and this name does not contain it.
let private filteredOutExpr (binding: string) : string =
  sprintf "$%s !== '' && !%s.includes($%s.toLowerCase())" DockSignals.LiveFilter (jsStringLiteral (binding.ToLowerInvariant())) DockSignals.LiveFilter

/// True while the filter is set and no binding's name contains it.
let private nothingMatchesExpr (bindings: string list) : string =
  let names = bindings |> List.map (fun name -> jsStringLiteral (name.ToLowerInvariant())) |> String.concat ", "
  sprintf "$%s !== '' && ![%s].some(n => n.includes($%s.toLowerCase()))" DockSignals.LiveFilter names DockSignals.LiveFilter

/// A uniform square button that toggles the pane's pin. It shows the real state, from the signal, and keeps it across a morph.
let private panePinButton (id: string) : XmlNode =
  Elem.button
    [ Attr.id id
      Attr.class' "session-btn live-pane-pin"
      testid "live-pane-pin"
      Attr.type' "button"
      Attr.create "aria-label" "Pin the live bindings open, or let them collapse when there are none"
      Attr.create "aria-pressed" "false"
      Ds.attr' ("aria-pressed", sprintf "$%s ? 'true' : 'false'" DockSignals.LivePanePinned)
      Ds.class' ("session-btn-on", sprintf "$%s" DockSignals.LivePanePinned)
      Ds.preserveAttr "class aria-pressed"
      Ds.onClick (sprintf "$%s = !$%s" DockSignals.LivePanePinned DockSignals.LivePanePinned) ]
    [ Text.raw "📌" ]

/// Where a pane's visibility follows the pin: the extra class it is drawn with, and the attributes that keep it in step with the
/// signal. Server-rendered in the state the pin starts in, so the first paint is right before Datastar runs.
let private followPin (display: PinDisplay) (baseClass: string) : XmlAttribute list =
  match display with
  | ShownAlways -> [ Attr.class' baseClass ]
  | ShownUnlessPinned ->
    [ Attr.class' baseClass
      Ds.class' ("live-hidden", sprintf "$%s" DockSignals.LivePanePinned)
      Ds.preserveAttr "class" ]
  | ShownOnlyWhilePinned ->
    [ Attr.class' (baseClass + " live-hidden")
      Ds.class' ("live-hidden", sprintf "!$%s" DockSignals.LivePanePinned)
      Ds.preserveAttr "class" ]

/// The one-line pane: says why there is nothing to show, and offers the pin when a pin can help.
let renderCollapsedBar (why: CollapsedBecause) (display: PinDisplay) : XmlNode =
  Elem.div (Attr.id DockIds.CollapsedBar :: Attr.create "role" "status" :: followPin display "live-dock-bar") [
    Elem.span [ Attr.class' "live-pane-title" ] [ Text.raw "🔴 Live bindings" ]
    Elem.span [ Attr.class' "live-dock-reason" ] [ textEnc (CollapsedBecause.text why) ]
    match CollapsedBecause.pin why with
    | PinOffered -> panePinButton DockIds.BarPin
    | PinNotOffered -> ()
  ]

/// The pane's body when there are no bindings to draw. It says why in the words the collapsed bar uses.
let private renderEmptyState : XmlNode =
  Elem.div [ Attr.id DockIds.EmptyState; Attr.class' "live-empty"; testid "live-bindings-empty" ] [ textEnc (CollapsedBecause.text NothingBoundYet) ]

let private renderTree (sessionId: string) (view: LiveBindingsPane.PaneView) : XmlNode list =
  let evaluateEndpoint = sprintf "/api/sessions/%s/live-values/evaluate" (Uri.EscapeDataString sessionId)
  let modeEndpoint = sprintf "/api/sessions/%s/live-values/mode" (Uri.EscapeDataString sessionId)
  let kindBadge (node: LiveValueTree.LiveValueNode) =
    match node.Kind with
    | LiveValueTree.NodeKind.Closure ->
      [ Elem.span [ Attr.class' "live-closure-badge"; Attr.create "aria-label" "Best-effort expansion of captured values" ] [ Text.raw "~(best-effort)" ] ]
    | LiveValueTree.NodeKind.Cycle ->
      [ Elem.span [ Attr.class' "live-cycle" ] [ Text.raw "↩ (cycle)" ] ]
    | LiveValueTree.NodeKind.Truncated ->
      [ Elem.span [ Attr.class' "live-truncated" ] [ Text.raw "… (truncated)" ] ]
    | _ -> []
  // The click's payload is staged in the signals the daemon route reads (`$binding`, `$path`), then posted.
  let evaluateClick (binding: string) (path: string list) =
    let stagedPath = path |> List.map jsStringLiteral |> String.concat ", "
    attrEnc (sprintf "$binding = %s; $path = [%s]; " (jsStringLiteral binding) stagedPath + Ds.post evaluateEndpoint)
  let nameCell (node: LiveValueTree.LiveValueNode) = Elem.code [ Attr.class' "live-name" ] [ textEnc node.Label ]
  let kindCell (node: LiveValueTree.LiveValueNode) = Elem.span [ Attr.class' "live-kind" ] [ textEnc (kindLabel node.Kind) ]
  let typeCell (node: LiveValueTree.LiveValueNode) = Elem.span [ Attr.class' "live-type" ] [ textEnc node.TypeName ]
  let heldRow (binding: string) (path: string list) (node: LiveValueTree.LiveValueNode) (reason: LiveValueTree.NotEvaluatedReason) =
    let action = LiveBindingsPane.rowActionOf reason
    let rowClass =
      match action with
      | LiveBindingsPane.ClickToRun -> "live-binding-node live-held-row"
      | LiveBindingsPane.NothingToClick -> "live-binding-node live-held-row"
      | LiveBindingsPane.ClickFailed -> "live-binding-node live-held-row live-held-unknown"
    let indicator = liveEvaluatingSignal binding path
    Elem.div [ Attr.class' rowClass ] [
      nameCell node
      kindCell node
      typeCell node
      Elem.span [ Attr.class' "live-held-reason" ] [
        match action with
        | LiveBindingsPane.ClickFailed -> textEnc (sprintf "unknown: %s" node.Preview)
        | LiveBindingsPane.ClickToRun
        | LiveBindingsPane.NothingToClick -> textEnc node.Preview
      ]
      match action with
      | LiveBindingsPane.ClickToRun ->
        Elem.button
          [ Attr.class' "session-btn live-held-btn"
            Attr.create "aria-label" (attrEnc (sprintf "Run %s now: it runs your code under a deadline" node.Label))
            Attr.create "title" (attrEnc (sprintf "Run %s now. It runs your code, under a deadline and, where the OS allows, a syscall filter." node.Label))
            Ds.indicator indicator
            Ds.attr' ("disabled", sprintf "$%s" indicator)
            Ds.onClick (evaluateClick binding path) ]
          [ Text.raw "▶" ]
        Elem.span [ Attr.class' "live-held-evaluating"; Ds.show (sprintf "$%s" indicator) ] [ Text.raw "⏳ evaluating…" ]
      | LiveBindingsPane.NothingToClick
      | LiveBindingsPane.ClickFailed -> ()
    ]
  // The open state of a node's <details> is its own signal, named from the binding and every label down to the node, each
  // written as hex, so two nodes that share a label never share a signal.
  let nodeSignal (binding: string) (path: string list) : string =
    let pathToken =
      match path with
      | [] -> "r"
      | labels -> "r_" + (labels |> List.map hexOf |> String.concat "_")
    sprintf "open_%s_%s_%s" (signalIdent binding) (hexOf binding) pathToken
  let rec renderNode (binding: string) (path: string list) (node: LiveValueTree.LiveValueNode) : XmlNode =
    let row =
      // Text.create escapes: labels, type names and previews are FSI-derived DATA, not trusted markup. A string
      // preview holding <script> must never inject into the dashboard DOM.
      Elem.div [ Attr.class' "live-binding-node" ] [
        nameCell node
        kindCell node
        typeCell node
        Elem.span [ Attr.class' "live-preview" ] [ textEnc (sprintf "= %s" node.Preview) ]
        yield! kindBadge node
      ]
    match node.Kind, List.isEmpty node.Children with
    | LiveValueTree.NodeKind.NotEvaluated reason, _ -> heldRow binding path node reason
    | _, true -> row
    | _, false ->
      signalDetails (nodeSignal binding path) [ Attr.class' "live-node" ] [
        Elem.summary [ Attr.class' "live-node-summary" ] [ row ]
        Elem.div [ Attr.class' "live-children" ] [
          yield! node.Children |> List.map (fun child -> renderNode binding (path @ [ child.Label ]) child)
        ]
      ]
  let walkModeLoading = "liveWalkModeLoading"
  let modeButton (current: ValueWalk) (choice: ValueWalk) =
    let name = ValueWalk.name choice
    let pressed, label =
      match choice = current with
      | true -> "true", sprintf "● %s" name
      | false -> "false", name
    Elem.button
      [ Attr.class' "eval-btn live-mode-btn"
        Attr.create "aria-pressed" pressed
        Attr.title (attrEnc (ValueWalk.consequence choice))
        Attr.create "aria-label" (attrEnc (sprintf "Walk live values in %s mode. %s" name (ValueWalk.consequence choice)))
        Ds.indicator walkModeLoading
        Ds.attr' ("disabled", sprintf "$%s" walkModeLoading)
        Ds.onClick (attrEnc (sprintf "$mode = %s; " (ValueWalk.name choice |> jsStringLiteral) + Ds.post modeEndpoint)) ]
      [ textEnc label ]
  let held = LiveBindingsPane.notEvaluatedCount view.Snapshot
  let names = view.Snapshot.Bindings |> List.map (fun b -> b.Name)
  [ Elem.div [ Attr.class' "live-pane-head" ] [
      Elem.div [ Attr.class' "live-mode"; Attr.create "role" "group"; Attr.create "aria-label" "How much of a class the live bindings run to show it" ] [
        yield! ValueWalk.all |> List.map (modeButton view.Notes.Mode)
      ]
      match held with
      | 0 -> ()
      | n -> Elem.span [ Attr.class' "live-held-count" ] [ textEnc (sprintf "%d not evaluated" n) ]
      match LiveBindingsPane.containmentLine view.Notes.Click with
      | LiveBindingsPane.NothingClickedYet -> ()
      | LiveBindingsPane.LineSays text -> Elem.div [ Attr.class' "live-containment" ] [ textEnc (sprintf "last click: %s" text) ]
    ]
    Elem.div [ Attr.id DockIds.Tree; Attr.class' "live-tree" ] [
      match view.Snapshot.Bindings with
      | [] -> renderEmptyState
      | bindings ->
        yield!
          bindings
          |> List.map (fun b ->
            Elem.div
              ( Attr.id (DockIds.binding b.Name) :: signalClasses "live-binding"
                @ [ Ds.class' ("live-hidden", attrEnc (filteredOutExpr b.Name))
                    Ds.class' ("live-pinned", isPinnedExpr b.Name) ])
              [ Elem.button
                  [ Attr.id (DockIds.bindingPin b.Name)
                    Attr.class' "session-btn live-binding-pin"
                    Attr.type' "button"
                    Attr.create "aria-label" (attrEnc (sprintf "Pin %s to the top of the list" b.Name))
                    Attr.create "aria-pressed" "false"
                    Ds.attr' ("aria-pressed", sprintf "%s ? 'true' : 'false'" (isPinnedExpr b.Name))
                    Ds.class' ("session-btn-on", isPinnedExpr b.Name)
                    Ds.preserveAttr "class aria-pressed"
                    Ds.onClick (attrEnc (togglePinExpr b.Name)) ]
                  [ Text.raw "📌" ]
                Elem.div [ Attr.class' "live-binding-tree" ] [ renderNode b.Name [] b.Root ] ])
        Elem.div
          [ Attr.id DockIds.NoMatch
            Attr.class' "live-empty live-hidden"
            Ds.class' ("live-hidden", attrEnc (sprintf "!(%s)" (nothingMatchesExpr names)))
            Ds.preserveAttr "class" ]
          [ Text.raw "no binding's name contains that" ]
        match view.Snapshot.Truncated with
        | true -> Elem.div [ Attr.class' "live-truncated live-truncated-note" ] [ Text.raw "Some values truncated (depth/children limits)" ]
        | false -> ()
    ] ]

/// The open pane: a header (what opened it, when it was read, a filter, the pin), then the tree.
let renderPane (sessionId: string) (source: BindingsSource) (because: OpenBecause) (display: PinDisplay) : XmlNode =
  let stamp =
    match source with
    | WalkedBindings view -> sprintf "gen %d · %s" view.Snapshot.Generation (view.Snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss"))
    | PrintedBindings _ -> "read from FSI's printed output"
    | NothingBound -> ""
  let body =
    match source with
    | WalkedBindings view -> renderTree sessionId view
    | PrintedBindings scope -> [ Elem.div [ Attr.id DockIds.Tree; Attr.class' "live-tree" ] [ renderBindingsPanel (Some scope) ] ]
    | NothingBound -> [ Elem.div [ Attr.id DockIds.Tree; Attr.class' "live-tree" ] [ renderEmptyState ] ]
  // The text panel carries `bindings-panel` itself, so the wrapper must not repeat it.
  let paneId =
    match source with
    | PrintedBindings _ -> DockIds.Pane
    | WalkedBindings _
    | NothingBound -> DomIds.BindingsPanel
  Elem.div (Attr.id paneId :: followPin display "panel live-pane") [
    Elem.div [ Attr.class' "live-pane-bar" ] [
      Elem.span [ Attr.class' "live-pane-title" ] [ Text.raw "🔴 Live bindings" ]
      Elem.span [ Attr.class' "live-pane-count" ] [ textEnc (OpenBecause.text because) ]
      Elem.span [ Attr.class' "live-pane-stamp" ] [ textEnc stamp ]
      Elem.input
        [ Attr.id DockIds.Filter
          Attr.class' "live-filter"
          Attr.type' "search"
          Attr.create "placeholder" "filter by name"
          Attr.create "aria-label" "Filter the live bindings by name"
          Attr.create "autocomplete" "off"
          Attr.create "spellcheck" "false"
          Ds.bind DockSignals.LiveFilter ]
      panePinButton DockIds.PanePin
    ]
    yield! body
  ]

/// The dock, decided and drawn. Both answers of the pin are rendered when the pin can matter (the signal picks between them);
/// when a session has bindings, or there is no session to pin on, only the one answer exists.
let renderDock (session: SessionInView) (sessionId: string) (source: BindingsSource) : XmlNode =
  let facts : PaneFacts = { Session = session; Bindings = BindingsSource.presence source; Pin = Unpinned }
  let shell (children: XmlNode list) = Elem.div [ Attr.id DockIds.Dock; Attr.class' "live-dock" ] children
  match PaneState.decide facts DockPane.LiveBindings with
  | PaneOpen because -> shell [ renderPane sessionId source because ShownAlways ]
  | PaneCollapsed why ->
    match CollapsedBecause.pin why with
    | PinNotOffered -> shell [ renderCollapsedBar why ShownAlways ]
    | PinOffered ->
      match PaneState.decide { facts with Pin = Pinned } DockPane.LiveBindings with
      | PaneOpen pinnedBecause ->
        shell [ renderCollapsedBar why ShownUnlessPinned; renderPane sessionId source pinnedBecause ShownOnlyWhilePinned ]
      | PaneCollapsed _ -> shell [ renderCollapsedBar why ShownAlways ]

/// A brief highlight on a value that changed. It is drawn from what the SSE morph really rewrote, so only a changed value
/// flashes and nothing polls: the browser's own mutation record says a `.live-preview` text node was replaced, and that cell
/// gets a short fade from the accent colour. It uses the Web Animations API, not a class, because the morph owns `class` and
/// would cut a class-driven animation short. A value that is merely added (a new row) does not flash, and a reload does not
/// replay old changes.
let flashScript () : XmlNode =
  Elem.script [] [ Text.raw (sprintf """
    (function() {
      var dock = '#%s';
      var cell = '.live-preview';
      new MutationObserver(function(records) {
        records.forEach(function(record) {
          var node = record.type === 'characterData' ? record.target.parentElement : record.target;
          var replaced = record.type === 'characterData' || (record.removedNodes.length > 0 && record.addedNodes.length > 0);
          if (!replaced || !node || !node.closest || !node.closest(dock)) return;
          var preview = node.closest(cell);
          if (!preview || !preview.animate) return;
          var accent = getComputedStyle(document.documentElement).getPropertyValue('--fg-yellow') || '#e5c07b';
          preview.getAnimations().forEach(function(running) { running.cancel(); });
          preview.animate([{ backgroundColor: accent }, { backgroundColor: 'transparent' }], { duration: 1000, easing: 'ease-out' });
        });
      }).observe(document.body, { subtree: true, childList: true, characterData: true });
    })();
  """ DockIds.Dock) ]
