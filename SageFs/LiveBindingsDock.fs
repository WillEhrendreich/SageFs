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
///
/// A row the session's files have something to say about gets a strip under it (`LiveBindingsTweakView`): where its value lives, and
/// the knob the state allows. The pane is handed that as a `TweakView` (`renderDockWith`); one with none draws exactly what it drew
/// before, and a row not in the view gets no strip.
module SageFs.Server.LiveBindingsDock

open System
open Falco.Markup
open Falco.Datastar
open SageFs
open SageFs.Features
open SageFs.Features.Tweak.BindingTweak
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Server.DockPanes
open SageFs.Server.LiveBindingsTweakView

/// Client-side state of the dock. All three are plain signals declared once in the page shell.
module DockSignals =
  /// What the filter box holds.
  let [<Literal>] LiveFilter = "liveFilter"
  /// The top-level bindings pinned to the top, as a comma-joined list of the names' hex, with a comma on both ends.
  let [<Literal>] LivePins = "livePins"
  /// Whether the pane is pinned open even with no bindings.
  let [<Literal>] LivePanePinned = "livePanePinned"
  /// Whether the pane's tree is folded away, so the dock is one row again.
  let [<Literal>] LivePaneCollapsed = "livePaneCollapsed"

  /// The initial value of every signal above, for the page shell.
  let initial : XmlAttribute list =
    [ Ds.signal (LiveFilter, "")
      Ds.signal (LivePins, ",")
      Ds.signal (LivePanePinned, false)
      Ds.signal (LivePaneCollapsed, false) ]
    // What the view panel stages before it posts (LiveBindingsViewPanel): declared once, with the dock's own.
    @ LiveBindingsViewPanel.ViewSignals.initial
    // What a row's knob stages before it posts (LiveBindingsTweakView): declared once, with the dock's own.
    @ TweakSignals.initial

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
  let [<Literal>] PaneCollapse = "live-pane-collapse"

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

/// The .NET names of the types a leaf is drawn as a number for.
let numericTypeNames : Set<string> =
  set [ "Byte"; "SByte"; "Int16"; "UInt16"; "Int32"; "UInt32"; "Int64"; "UInt64"; "Single"; "Double"; "Decimal"; "BigInteger" ]

/// What a leaf's value is, for the colour it is drawn in and the glyph it carries. Anything that is neither text nor a number keeps
/// the default colour, so a colour always means something.
[<RequireQualifiedAccess>]
type ValueTone =
  | TextTone
  | NumberTone
  | PlainTone

let valueToneOf (kind: LiveValueTree.NodeKind) (typeName: string) : ValueTone =
  match kind with
  | LiveValueTree.NodeKind.Leaf ->
    match typeName = "String", numericTypeNames.Contains typeName with
    | true, _ -> ValueTone.TextTone
    | false, true -> ValueTone.NumberTone
    | false, false -> ValueTone.PlainTone
  | LiveValueTree.NodeKind.NotEvaluated _
  | LiveValueTree.NodeKind.Record
  | LiveValueTree.NodeKind.List
  | LiveValueTree.NodeKind.Map
  | LiveValueTree.NodeKind.Option
  | LiveValueTree.NodeKind.Union
  | LiveValueTree.NodeKind.Tuple
  | LiveValueTree.NodeKind.Array
  | LiveValueTree.NodeKind.Class
  | LiveValueTree.NodeKind.Closure
  | LiveValueTree.NodeKind.Cycle
  | LiveValueTree.NodeKind.Truncated -> ValueTone.PlainTone

/// The extra class a value is drawn with, from the theme's syntax tokens (`--syn-string`, `--syn-number`).
let valueToneClass (tone: ValueTone) : string =
  match tone with
  | ValueTone.TextTone -> " live-v-string"
  | ValueTone.NumberTone -> " live-v-number"
  | ValueTone.PlainTone -> ""

/// A node's kind as a glyph of one or two characters. The shape says what the node is, the word is on hover and in the accessible
/// name (`kindLabel`), and the glyph takes no colour of its own, because every theme has a syntax colour that equals a meaning colour.
let kindGlyph (kind: LiveValueTree.NodeKind) (typeName: string) : string =
  match kind with
  | LiveValueTree.NodeKind.Leaf ->
    match valueToneOf kind typeName with
    | ValueTone.TextTone -> "ab"
    | ValueTone.NumberTone -> "#"
    | ValueTone.PlainTone -> "·"
  | LiveValueTree.NodeKind.NotEvaluated _ -> "…"
  | LiveValueTree.NodeKind.Record -> "{}"
  | LiveValueTree.NodeKind.List -> "[]"
  | LiveValueTree.NodeKind.Map -> "⇄"
  | LiveValueTree.NodeKind.Option -> "?"
  | LiveValueTree.NodeKind.Union -> "∪"
  | LiveValueTree.NodeKind.Tuple -> "()"
  | LiveValueTree.NodeKind.Array -> "[]"
  | LiveValueTree.NodeKind.Class -> "◇"
  | LiveValueTree.NodeKind.Closure -> "ƒ"
  | LiveValueTree.NodeKind.Cycle -> "↩"
  | LiveValueTree.NodeKind.Truncated -> "…"

/// A pin, drawn in the text's own colour so a theme and the pressed state reach it.
let private pinIcon : XmlNode =
  Text.raw "<svg viewBox=\"0 0 16 16\" width=\"12\" height=\"12\" aria-hidden=\"true\"><path d=\"M9.5 1.5l5 5-1.6.6-2.4 2.4.3 3.1-1.1 1.1-2.6-2.6-4 4-.7-.7 4-4L3.8 7.2l1.1-1.1 3.1.3 2.4-2.4z\" fill=\"currentColor\"/></svg>"

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
    [ pinIcon ]

/// Fold the pane's tree away, or open it again.
///
/// The dock's ceiling is a drag handle, and a drag handle is a thin line to find. The tree can
/// also be taller than the ceiling and the user is left with a dock that eats the transcript, so
/// the pane carries its own fold: one click leaves the header and takes the tree away, and the
/// dock is a row again. The state is a client signal, so a push never undoes it.
let private collapseButton () : XmlNode =
  Elem.button
    [ Attr.id DockIds.PaneCollapse
      Attr.class' "session-btn live-pane-collapse"
      Attr.type' "button"
      Attr.create "aria-label" "Fold the live bindings away, or open them again"
      Attr.create "aria-expanded" "true"
      Ds.attr' ("aria-expanded", sprintf "!$%s ? 'true' : 'false'" DockSignals.LivePaneCollapsed)
      Ds.preserveAttr "aria-expanded"
      Ds.onClick (sprintf "$%s = !$%s" DockSignals.LivePaneCollapsed DockSignals.LivePaneCollapsed) ]
    [ Elem.span [ Ds.show (sprintf "!$%s" DockSignals.LivePaneCollapsed) ] [ Text.raw "▼" ]
      Elem.span [ Ds.show (sprintf "$%s" DockSignals.LivePaneCollapsed) ] [ Text.raw "▲" ] ]

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
    Elem.span [ Attr.class' "live-pane-title" ] [ Text.raw "Live bindings" ]
    Elem.span [ Attr.class' "live-dock-reason" ] [ textEnc (CollapsedBecause.text why) ]
    match CollapsedBecause.pin why with
    | PinOffered -> panePinButton DockIds.BarPin
    | PinNotOffered -> ()
  ]

/// The pane's body when there are no bindings to draw. It says why in the words the collapsed bar uses.
let private renderEmptyState : XmlNode =
  Elem.div [ Attr.id DockIds.EmptyState; Attr.class' "live-empty"; testid "live-bindings-empty" ] [ textEnc (CollapsedBecause.text NothingBoundYet) ]

/// The tree, or whatever stands in for it, with the fold on it.
///
/// One place, so the fold works for the walked tree and for the printed bindings that stand in
/// for it while no walk has come back. The class is preserved, so the morph leaves the fold alone:
/// it is the user's, and a push must not take it back.
/// The view's two row-shaping axes are attributes, not classes: the tree's `class` is the user's fold and is preserved across a
/// morph, so a class written here would never change once drawn. An attribute is rewritten by every push, which is how a changed
/// setting reaches a page that is already open.
let private treeElement (look: View) (children: XmlNode list) : XmlNode =
  Elem.div
    [ Attr.id DockIds.Tree
      Attr.class' "live-tree"
      Attr.create "data-values" (AxisValue.token (AxisValue.ValueMode look.Values))
      Attr.create "data-row" (AxisValue.token (AxisValue.RowHeight look.Row))
      Ds.class' ("live-hidden", sprintf "$%s" DockSignals.LivePaneCollapsed)
      Ds.preserveAttr "class" ]
    children

let private renderTree (look: View) (sessionId: string) (tweaks: TweakView) (view: LiveBindingsPane.PaneView) : XmlNode list =
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
  let glyphCell (node: LiveValueTree.LiveValueNode) =
    let word = kindLabel node.Kind
    Elem.span
      [ Attr.class' "live-glyph"
        Attr.create "title" word
        Attr.create "role" "img"
        Attr.create "aria-label" word ]
      [ textEnc (kindGlyph node.Kind node.TypeName) ]
  // The Type column is a view axis: off, the cell is not drawn at all (and is not a hidden node a screen reader would still read).
  let typeCells (node: LiveValueTree.LiveValueNode) : XmlNode list =
    match look.Layout.Type with
    | On -> [ Elem.span [ Attr.class' "live-type" ] [ textEnc node.TypeName ] ]
    | Off -> []
  let heldRow (binding: string) (path: string list) (node: LiveValueTree.LiveValueNode) (reason: LiveValueTree.NotEvaluatedReason) =
    let action = LiveBindingsPane.rowActionOf reason
    let rowClass =
      match action with
      | LiveBindingsPane.ClickToRun -> "live-binding-node live-held-row"
      | LiveBindingsPane.NothingToClick -> "live-binding-node live-held-row"
      | LiveBindingsPane.ClickFailed -> "live-binding-node live-held-row live-held-unknown"
    let indicator = liveEvaluatingSignal binding path
    Elem.div [ Attr.class' rowClass ] [
      glyphCell node
      nameCell node
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
      yield! typeCells node
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
        glyphCell node
        nameCell node
        Elem.span [ Attr.class' ("live-preview" + valueToneClass (valueToneOf node.Kind node.TypeName)) ] [ textEnc (sprintf "= %s" node.Preview) ]
        yield! typeCells node
        yield! kindBadge node
      ]
    // What the files say about this row and what can be done about it (LiveBindingsTweakView): one strip under a row the pane
    // has something to say about, and nothing at all under one it has not. A strip inside a summary is never interactive, because
    // a click there would toggle the details.
    let key : RowKey = { Binding = binding; Labels = path }
    let stripFor (placement: Placement) : XmlNode list =
      match TweakView.tryRow tweaks key with
      | Some view -> [ renderStrip key view placement ]
      | None -> []
    let withStrip (rowNode: XmlNode) : XmlNode =
      match stripFor Placement.OnLeafRow with
      | [] -> rowNode
      | strip -> Elem.div [ Attr.class' "live-row" ] (rowNode :: strip)
    match node.Kind, List.isEmpty node.Children with
    | LiveValueTree.NodeKind.NotEvaluated reason, _ -> withStrip (heldRow binding path node reason)
    | _, true -> withStrip row
    | _, false ->
      signalDetails (nodeSignal binding path) [ Attr.class' "live-node" ] [
        Elem.summary [ Attr.class' "live-node-summary" ] [ row; yield! stripFor Placement.InsideSummary ]
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
    treeElement look [
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
                  [ pinIcon ]
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
let renderPaneWith (look: View) (controls: LiveBindingsViewPanel.ViewControls option) (tweaks: TweakView) (sessionId: string) (source: BindingsSource) (because: OpenBecause) (display: PinDisplay) : XmlNode =
  let stamp =
    match source with
    | WalkedBindings view -> sprintf "gen %d · %s" view.Snapshot.Generation (view.Snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss"))
    | PrintedBindings _ -> "read from FSI's printed output"
    | NothingBound -> ""
  let body =
    match source with
    | WalkedBindings view -> renderTree look sessionId tweaks view
    | PrintedBindings scope -> [ treeElement look [ renderBindingsPanel (Some scope) ] ]
    | NothingBound -> [ treeElement look [ renderEmptyState ] ]
  // The text panel carries `bindings-panel` itself, so the wrapper must not repeat it.
  let paneId =
    match source with
    | PrintedBindings _ -> DockIds.Pane
    | WalkedBindings _
    | NothingBound -> DomIds.BindingsPanel
  Elem.div (Attr.id paneId :: followPin display "panel live-pane") [
    Elem.div [ Attr.class' "live-pane-bar" ] [
      Elem.span [ Attr.class' "live-pane-title" ] [ Text.raw "Live bindings" ]
      Elem.span [ Attr.class' "live-pane-count" ] [ textEnc (OpenBecause.text because) ]
      Elem.span [ Attr.class' "live-pane-stamp" ] [ textEnc stamp ]
      // A binding no file holds is said once here, with its reason on hover, and not on every row.
      match source with
      | WalkedBindings view ->
        match TweakView.replOnlyCount tweaks (view.Snapshot.Bindings |> List.map (fun b -> b.Name)) with
        | 0 -> ()
        | count ->
          Elem.span
            [ Attr.class' "live-repl-chip"
              testid "live-repl-only"
              Attr.title (attrEnc (describe (PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt)).Detail) ]
            [ textEnc (sprintf "%d REPL only" count) ]
      | PrintedBindings _
      | NothingBound -> ()
      Elem.input
        [ Attr.id DockIds.Filter
          Attr.class' "live-filter"
          Attr.type' "search"
          Attr.create "placeholder" "filter by name"
          Attr.create "aria-label" "Filter the live bindings by name"
          Attr.create "autocomplete" "off"
          Attr.create "spellcheck" "false"
          Ds.bind DockSignals.LiveFilter ]
      collapseButton ()
      match controls with
      | Some viewControls -> viewControls.Button
      | None -> ()
      panePinButton DockIds.PanePin
    ]
    match controls with
    | Some viewControls -> viewControls.Panel
    | None -> ()
    yield! body
  ]

/// The open pane with no knob, in the REPL workflow's recommended view: what a session whose files have not been asked about shows.
let renderPane (sessionId: string) (source: BindingsSource) (because: OpenBecause) (display: PinDisplay) : XmlNode =
  renderPaneWith (View.recommended ViewWorkflow.Repl) None TweakView.none sessionId source because display

/// The dock, decided and drawn. Both answers of the pin are rendered when the pin can matter (the signal picks between them);
/// when a session has bindings, or there is no session to pin on, only the one answer exists. `tweaks` is what the session's files
/// say about each row (the persistence state and the knob): rows it has nothing for are drawn as they always were. `look` is the
/// view the session's workflow and app resolved to (`SettingsCatalog.viewFor`): which columns a row has, how much of a value
/// prints, how tall a row is. `controls` is the pane bar's view button and the panel it opens; a page with no session to choose a view
/// for has none.
let renderDockWith
  (look: View)
  (controls: LiveBindingsViewPanel.ViewControls option)
  (tweaks: TweakView)
  (session: SessionInView)
  (sessionId: string)
  (source: BindingsSource)
  (appOutput: SageFs.Server.AppOutputPane.AppOutputPane option)
  : XmlNode =
  // The app-output pane's fact comes from the buffer it is handed, not from a separate count: two
  // sources for one decision is how a pane opens on output the drawer cannot find.
  let appPresence =
    match appOutput with
    | Some pane -> AppOutputPresence.ofCount (SageFs.Server.AppOutputPane.AppOutputBuffer.count pane.Buffer)
    | None -> AppOutputPresence.NoAppOutput

  let facts : PaneFacts =
    { Session = session
      Bindings = BindingsSource.presence source
      AppOutput = appPresence
      Pin = Unpinned }

  let shell (children: XmlNode list) = Elem.div [ Attr.id DockIds.Dock; Attr.class' "live-dock" ] children

  let liveBindings =
    match PaneState.decide facts DockPane.LiveBindings with
    | PaneOpen because -> [ renderPaneWith look controls tweaks sessionId source because ShownAlways ]
    | PaneCollapsed why ->
      match CollapsedBecause.pin why with
      | PinNotOffered -> [ renderCollapsedBar why ShownAlways ]
      | PinOffered ->
        match PaneState.decide { facts with Pin = Pinned } DockPane.LiveBindings with
        | PaneOpen pinnedBecause ->
          [ renderCollapsedBar why ShownUnlessPinned; renderPaneWith look controls tweaks sessionId source pinnedBecause ShownOnlyWhilePinned ]
        | PaneCollapsed _ -> [ renderCollapsedBar why ShownAlways ]

  // Qualified rather than `open`: AppOutputView has its own `renderCollapsedBar`, and opening it here
  // would make this file's own one silently resolve to the other pane's.
  let appOutputPane =
    let pane = Option.defaultValue SageFs.Server.AppOutputPane.AppOutputPane.create appOutput
    [ SageFs.Server.AppOutputView.render (PaneState.decide facts DockPane.AppOutput) pane ]

  shell (liveBindings @ appOutputPane)

/// The dock with no knob (a session whose files have not been asked about, and the no-session page).
let renderDock (session: SessionInView) (sessionId: string) (source: BindingsSource) : XmlNode =
  renderDockWith (View.recommended ViewWorkflow.Repl) None TweakView.none session sessionId source None

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
          var accent = getComputedStyle(document.documentElement).getPropertyValue('--color-info') || '#7fb4ca';
          preview.getAnimations().forEach(function(running) { running.cancel(); });
          preview.animate([{ backgroundColor: accent }, { backgroundColor: 'transparent' }], { duration: 1000, easing: 'ease-out' });
        });
      }).observe(document.body, { subtree: true, childList: true, characterData: true });
    })();
  """ DockIds.Dock) ]
