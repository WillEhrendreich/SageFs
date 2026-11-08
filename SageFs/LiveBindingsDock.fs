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
open SageFs.Features.Tweak.WatchTabs
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
  /// The open tab (`WatchTab.token`): which of the bindings are listed.
  let [<Literal>] LiveTab = "liveTab"
  /// Where a tab click posts, so the server knows which tab the page has open.
  let [<Literal>] LiveTabEndpoint = "/dashboard/live-tab"

  /// The initial value of every signal above, for the page shell.
  let initial : XmlAttribute list =
    [ Ds.signal (LiveFilter, "")
      Ds.signal (LiveTab, WatchTab.token WatchTab.All)
      Ds.signal (LivePins, ",")
      Ds.signal (LivePanePinned, false)
      Ds.signal (LivePaneCollapsed, false) ]
    // What the view panel stages before it posts (LiveBindingsViewPanel): declared once, with the dock's own.
    @ LiveBindingsViewPanel.ViewSignals.initial
    // What a row's knob stages before it posts (LiveBindingsTweakView): declared once, with the dock's own.
    @ TweakSignals.initial
    // What the Watch tab's box holds and its last note (LiveBindingsWatch).
    @ LiveBindingsWatch.WatchSignals.initial

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
  let [<Literal>] ModeLabel = "live-mode-label"

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

/// The class a freshness chip is drawn with, one per tone, from the theme's semantic tokens (pass, info, warn, fail).
let freshnessClass (tone: Tweak.LoopFreshness.FreshnessTone) : string =
  match tone with
  | Tweak.LoopFreshness.FreshnessTone.Current -> "live-fresh-current"
  | Tweak.LoopFreshness.FreshnessTone.Moving -> "live-fresh-moving"
  | Tweak.LoopFreshness.FreshnessTone.Held -> "live-fresh-held"
  | Tweak.LoopFreshness.FreshnessTone.Failed -> "live-fresh-failed"

/// How current the values are, from the test loop: a square dot in the state's colour and the state in a word, with the sentence as the
/// tooltip. The word is markup, so the state is never told by colour alone.
let freshnessChip (chip: Tweak.LoopFreshness.Chip) : XmlNode =
  Elem.span
    [ Attr.class' ("live-freshness " + freshnessClass chip.Tone)
      testid "live-freshness"
      Attr.title (attrEnc chip.Detail) ]
    [ Elem.span [ Attr.class' "live-freshness-dot"; Attr.create "aria-hidden" "true" ] []
      textEnc chip.Word ]

/// What a glyph stands for. The glyph drawn on a row and the line that explains it in the legend both come from this, so a glyph
/// cannot be drawn that the legend does not name.
[<RequireQualifiedAccess>]
type GlyphKind =
  | TextValue
  | NumberValue
  | PlainValue
  | RecordValue
  | ListValue
  | ArrayValue
  | MapValue
  | OptionValue
  | UnionValue
  | TupleValue
  | ClassValue
  | FunctionValue
  | SelfReference
  | CutShort
  | Unread

module GlyphKind =
  /// Every glyph, in the order the legend lists them.
  let all : GlyphKind list =
    [ GlyphKind.TextValue
      GlyphKind.NumberValue
      GlyphKind.PlainValue
      GlyphKind.RecordValue
      GlyphKind.ListValue
      GlyphKind.ArrayValue
      GlyphKind.MapValue
      GlyphKind.OptionValue
      GlyphKind.UnionValue
      GlyphKind.TupleValue
      GlyphKind.ClassValue
      GlyphKind.FunctionValue
      GlyphKind.SelfReference
      GlyphKind.CutShort
      GlyphKind.Unread ]

  let ofNode (kind: LiveValueTree.NodeKind) (typeName: string) : GlyphKind =
    match kind with
    | LiveValueTree.NodeKind.Leaf ->
      match valueToneOf kind typeName with
      | ValueTone.TextTone -> GlyphKind.TextValue
      | ValueTone.NumberTone -> GlyphKind.NumberValue
      | ValueTone.PlainTone -> GlyphKind.PlainValue
    | LiveValueTree.NodeKind.NotEvaluated _ -> GlyphKind.Unread
    | LiveValueTree.NodeKind.Record -> GlyphKind.RecordValue
    | LiveValueTree.NodeKind.List -> GlyphKind.ListValue
    | LiveValueTree.NodeKind.Map -> GlyphKind.MapValue
    | LiveValueTree.NodeKind.Option -> GlyphKind.OptionValue
    | LiveValueTree.NodeKind.Union -> GlyphKind.UnionValue
    | LiveValueTree.NodeKind.Tuple -> GlyphKind.TupleValue
    | LiveValueTree.NodeKind.Array -> GlyphKind.ArrayValue
    | LiveValueTree.NodeKind.Class -> GlyphKind.ClassValue
    | LiveValueTree.NodeKind.Closure -> GlyphKind.FunctionValue
    | LiveValueTree.NodeKind.Cycle -> GlyphKind.SelfReference
    | LiveValueTree.NodeKind.Truncated -> GlyphKind.CutShort

  /// The one or two characters drawn on a row.
  let glyph (kind: GlyphKind) : string =
    match kind with
    | GlyphKind.TextValue -> "ab"
    | GlyphKind.NumberValue -> "#"
    | GlyphKind.PlainValue -> "·"
    | GlyphKind.RecordValue -> "{}"
    | GlyphKind.ListValue -> "[]"
    | GlyphKind.ArrayValue -> "[]"
    | GlyphKind.MapValue -> "⇄"
    | GlyphKind.OptionValue -> "?"
    | GlyphKind.UnionValue -> "∪"
    | GlyphKind.TupleValue -> "()"
    | GlyphKind.ClassValue -> "◇"
    | GlyphKind.FunctionValue -> "ƒ"
    | GlyphKind.SelfReference -> "↩"
    | GlyphKind.CutShort -> "…"
    | GlyphKind.Unread -> "…"

  /// What a glyph means, in one plain line: its line in the legend.
  let meaning (kind: GlyphKind) : string =
    match kind with
    | GlyphKind.TextValue -> "A text value."
    | GlyphKind.NumberValue -> "A number."
    | GlyphKind.PlainValue -> "Another plain value, such as true or false."
    | GlyphKind.RecordValue -> "A record. Its fields are the rows below it."
    | GlyphKind.ListValue -> "A list. Its items are the rows below it."
    | GlyphKind.ArrayValue -> "An array. Its items are the rows below it."
    | GlyphKind.MapValue -> "A map. Its keys and values are the rows below it."
    | GlyphKind.OptionValue -> "An option: a value, or nothing."
    | GlyphKind.UnionValue -> "A union: one of several cases."
    | GlyphKind.TupleValue -> "A tuple: a few values kept together."
    | GlyphKind.ClassValue -> "A class instance."
    | GlyphKind.FunctionValue -> "A function, with the values it captured."
    | GlyphKind.SelfReference -> "A value that refers back to itself."
    | GlyphKind.CutShort -> "Cut short: the pane stops at its depth or size limit."
    | GlyphKind.Unread -> "Not read yet: reading it would run your code."

/// A node's kind as a glyph of one or two characters. The shape says what the node is, the word is on hover and in the accessible
/// name (`kindLabel`), and the glyph takes no colour of its own, because every theme has a syntax colour that equals a meaning colour.
let kindGlyph (kind: LiveValueTree.NodeKind) (typeName: string) : string = GlyphKind.glyph (GlyphKind.ofNode kind typeName)

/// The legend: a closed disclosure with one plain line for each badge a row can carry and each glyph it can wear. Both lists come from
/// the closed sets the badges and glyphs come from (`StateTag.all`, `GlyphKind.all`), so a tag cannot be drawn without a line here.
let renderLegend : XmlNode =
  signalDetails "liveLegend" [ Attr.id "live-legend"; Attr.class' "live-help live-legend"; testid "live-legend" ] [
    Elem.summary [ Attr.class' "live-help-summary" ] [ textEnc (LiveBindingsPane.PaneNote.text LiveBindingsPane.PaneNote.LegendSummary) ]
    Elem.div [ Attr.class' "live-legend-body" ] [
      Elem.ul [ Attr.class' "live-legend-list"; testid "live-legend-tags" ] (
        StateTag.all
        |> List.map (fun tag ->
          Elem.li [ testid "live-legend-tag" ] [
            Elem.span [ Attr.class' "live-state live-tone-quiet" ] [ textEnc (tagLabel tag) ]
            Text.raw " "
            textEnc (tagMeaning tag) ]))
      Elem.ul [ Attr.class' "live-legend-list"; testid "live-legend-glyphs" ] (
        GlyphKind.all
        |> List.map (fun kind ->
          Elem.li [ testid "live-legend-glyph" ] [
            Elem.span [ Attr.class' "live-glyph" ] [ textEnc (GlyphKind.glyph kind) ]
            Text.raw " "
            textEnc (GlyphKind.meaning kind) ])) ] ]

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

/// True while the open tab does not list this binding: `WatchTabs.lists` for each tab that is not All, with the binding's pin read from
/// the signal and whether it holds a change written in by the server (a change is a fact of the rows, which the page does not hold).
let private tabHidesExpr (binding: string) (changed: bool) (moved: bool) : string =
  let open' (tab: WatchTab) = sprintf "$%s === '%s'" DockSignals.LiveTab (WatchTab.token tab)
  let literal (value: bool) = match value with | true -> "true" | false -> "false"
  [ WatchTab.Pinned; WatchTab.Changes; WatchTab.SinceGreen; WatchTab.Watch ]
  |> List.map (fun tab -> sprintf "(%s && !(%s))" (open' tab) (Tweak.WatchTabs.listsJs tab (isPinnedExpr binding) (literal changed) (literal moved)))
  |> String.concat " || "

/// True while the filter is set and this name does not contain it, or the open tab does not list it.
let private hiddenExpr (binding: string) (changed: bool) (moved: bool) : string =
  sprintf "(%s) || %s" (filteredOutExpr binding) (tabHidesExpr binding changed moved)

/// True while the open tab is `tab` and none of the bindings is listed on it.
let private tabEmptyExpr (tab: WatchTab) (bindings: string list) (changedNames: Set<string>) (movedNames: Set<string>) : string =
  let open' = sprintf "$%s === '%s'" DockSignals.LiveTab (WatchTab.token tab)
  match tab with
  | WatchTab.All
  // the Watch tab has its own empty words, drawn with its rows
  | WatchTab.Watch -> "false"
  | WatchTab.Changes ->
    let any = bindings |> List.exists (fun name -> Tweak.WatchTabs.lists tab false (Set.contains name changedNames) false)
    sprintf "%s && %s" open' (match any with | true -> "false" | false -> "true")
  | WatchTab.SinceGreen ->
    let any = bindings |> List.exists (fun name -> Tweak.WatchTabs.lists tab false false (Set.contains name movedNames))
    sprintf "%s && %s" open' (match any with | true -> "false" | false -> "true")
  | WatchTab.Pinned ->
    let tokens = bindings |> List.map (fun name -> jsStringLiteral (pinToken name)) |> String.concat ", "
    sprintf "%s && ![%s].some(t => $%s.includes(t))" open' tokens DockSignals.LivePins

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
      Attr.title "Keep the live bindings panel open, even when the session has no bindings"
      Attr.create "aria-pressed" "false"
      Ds.attr' ("aria-pressed", sprintf "$%s ? 'true' : 'false'" DockSignals.LivePanePinned)
      Ds.class' ("session-btn-on", sprintf "$%s" DockSignals.LivePanePinned)
      Ds.preserveAttr "class aria-pressed"
      Ds.onClick (sprintf "$%s = !$%s" DockSignals.LivePanePinned DockSignals.LivePanePinned) ]
    // The word is in the markup (it was drawn by CSS `::before`, which no one can select, search or translate).
    [ pinIcon; Elem.span [ Attr.class' "session-btn-word" ] [ Text.raw "Keep open" ] ]

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
      Attr.title "Hide the live bindings panel, or show it again"
      Attr.create "aria-expanded" "true"
      Ds.attr' ("aria-expanded", sprintf "!$%s ? 'true' : 'false'" DockSignals.LivePaneCollapsed)
      Ds.preserveAttr "aria-expanded"
      Ds.onClick (sprintf "$%s = !$%s" DockSignals.LivePaneCollapsed DockSignals.LivePaneCollapsed) ]
    // The word says what a click does now: "Hide panel" while it is open and "Show panel" once it is folded. It read "Hide panel"
    // in both states.
    [ Elem.span [ Ds.show (sprintf "!$%s" DockSignals.LivePaneCollapsed) ] [ Text.raw "▼" ]
      Elem.span [ Ds.show (sprintf "$%s" DockSignals.LivePaneCollapsed) ] [ Text.raw "▲" ]
      Elem.span [ Attr.class' "session-btn-word"; Ds.text (sprintf "$%s ? 'Show panel' : 'Hide panel'" DockSignals.LivePaneCollapsed) ] [ Text.raw "Hide panel" ] ]

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

/// The container-query rules that give a column way when the tree is too narrow, one per column a layout can ask for at each width
/// (`ViewColumns.containerRules`), written as the stylesheet's own text. `dashboard.css` holds this text verbatim between its two
/// marker comments and a test fails if it does not, so the thresholds in the stylesheet are the pure fit's numbers and are never
/// typed by hand. A State or Source column that gives way hands its job back to the strip under the row, which is drawn either way
/// and hidden while the column is showing, so a narrow pane loses the column and not the fact.
let columnCss : string =
  let cellOf (column: OptionalColumn) : string =
    match column with
    | OptionalColumn.Type -> ".live-type"
    | OptionalColumn.Source -> ".live-cell-source"
    | OptionalColumn.State -> ".live-cell-state"
  let stripCounterpartOf (column: OptionalColumn) : string option =
    match column with
    | OptionalColumn.Type -> None
    | OptionalColumn.Source -> Some ".live-where"
    | OptionalColumn.State -> Some ".live-state"
  let ruleText (rule: ContainerRule) : string =
    let scope = sprintf ".live-tree[data-values=\"%s\"][data-cols=\"%s\"]" (AxisValue.token (AxisValue.ValueMode rule.Values)) (rule.Requested |> List.map ViewColumns.token |> String.concat " ")
    let block (target: string) (display: string) = sprintf "  %s %s {\n    display: %s;\n  }" scope target display
    let blocks =
      [ yield block (cellOf rule.Hides) "none"
        match stripCounterpartOf rule.Hides with
        | Some counterpart -> yield block counterpart "inline"
        | None -> () ]
    // The fit measures in pixels at the default font size (the page's root is 14px); the stylesheet says the same width in rem, so a
    // larger font moves the point where a column gives way. "At most `Below` px" is "less than one pixel more", with no gap before a wider one.
    let defaultRootPx = 14.0
    let widthRem = Math.Round(float (rule.Below + 1) / defaultRootPx, 3).ToString("0.###", Globalization.CultureInfo.InvariantCulture)
    sprintf "@container (width < %srem) {\n%s\n}" widthRem (String.concat "\n" blocks)
  String.concat "\n\n" [ yield "/* columns: generated by LiveBindingsDock.columnCss; do not edit by hand */"; yield! ViewColumns.containerRules |> List.map ruleText; yield "/* columns: end */" ]

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
      Attr.create "data-cols" (ViewColumns.requestedToken look.Layout)
      Ds.class' ("live-hidden", sprintf "$%s" DockSignals.LivePaneCollapsed)
      Ds.preserveAttr "class" ]
    children

/// The names of the columns the rows share. Above 900px the CSS lays each row out in columns (the name, the value and the type; the
/// state; the file and line; the knob), and a column nobody names is one a reader has to guess at. It is decoration for the eye, so
/// it is hidden from assistive technology: every row carries its own accessible names. The type label follows the Type setting.
let private columnHeader (look: View) : XmlNode =
  Elem.div
    [ Attr.id "live-cols"
      Attr.class' "live-cols"
      Attr.create "aria-hidden" "true"
      // The Watch tab lists no bindings, so the columns of a binding's row have nothing under them there.
      Ds.class' ("live-hidden", sprintf "$%s === '%s'" DockSignals.LiveTab (WatchTab.token WatchTab.Watch))
      Ds.preserveAttr "class" ] [
    Elem.span [ Attr.class' "live-cols-name" ] [
      Elem.span [] [ Text.raw "Name · running value" ]
      match look.Layout.Type with
      | On -> Elem.span [] [ Text.raw "Type" ]
      | Off -> ()
    ]
    Elem.span [] [ Text.raw "State" ]
    Elem.span [] [ Text.raw "Source" ]
    Elem.span [] [ Text.raw "Edit (saves to file)" ]
    // The pin has a column of its own at the right edge, so the glyph in it has a word over it.
    Elem.span [ Attr.class' "live-cols-pin" ] [ Text.raw "Pin" ]
  ]

/// The mode buttons and the legend, as rows of the Display panel. Both govern how the bindings are read, and the pane's own bar had no room
/// for them on a phone: they pushed the first value down the screen. The chosen mode carries the same tick the panel's other choices do.
let modeRows (sessionId: string) (current: ValueWalk) : XmlNode list =
  let modeEndpoint = sprintf "/api/sessions/%s/live-values/mode" (Uri.EscapeDataString sessionId)
  let walkModeLoading = "liveWalkModeLoading"
  let modeButton (choice: ValueWalk) =
    let name = ValueWalk.name choice
    let pressed, label =
      match choice = current with
      | true -> "true", sprintf "✓ %s" name
      | false -> "false", name
    Elem.button
      [ Attr.class' "session-btn session-btn-labeled live-view-opt live-mode-btn"
        Attr.type' "button"
        Attr.create "aria-pressed" pressed
        Attr.title (attrEnc (ValueWalk.consequence choice))
        Attr.create "aria-label" (attrEnc (sprintf "Walk live values in %s mode. %s" name (ValueWalk.consequence choice)))
        Ds.indicator walkModeLoading
        Ds.attr' ("disabled", sprintf "$%s" walkModeLoading)
        Ds.onClick (attrEnc (sprintf "$mode = %s; " (ValueWalk.name choice |> jsStringLiteral) + Ds.post modeEndpoint)) ]
      [ textEnc label ]
  [ Elem.div [ Attr.class' "live-view-row live-mode"; Attr.create "role" "group"; Attr.create "aria-labelledby" DockIds.ModeLabel ] [
      Elem.span [ Attr.id DockIds.ModeLabel; Attr.class' "live-view-label live-mode-label"; testid "live-mode-label" ] [
        textEnc (LiveBindingsPane.PaneNote.text LiveBindingsPane.PaneNote.ModeGoverns) ]
      yield! ValueWalk.all |> List.map modeButton ]
    renderLegend ]

let private renderTree (look: View) (sessionId: string) (tweaks: TweakView) (formats: Map<string, Tweak.RowFormatting.FormatChoice>) (green: LiveBindingsViewPanel.GreenSaid) (view: LiveBindingsPane.PaneView) (watchTab: XmlNode list) : XmlNode list =
  // The bindings the Changes tab lists: a fact of the rows, so the server writes it into each binding's expression.
  let changedNames = changedBindings (Tweak.ChangesStrip.Changes.ofView tweaks)
  // What moved since the tests last settled green, by place, and the bindings it belongs to.
  let moves =
    match green with
    | LiveBindingsViewPanel.GreenSaid.Moved moved -> moved
    | LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet -> Map.empty
  let movedNames = Tweak.SinceGreen.movedBindings moves
  let sinceGreenOpen = sprintf "$%s === '%s'" DockSignals.LiveTab (WatchTab.token WatchTab.SinceGreen)
  let evaluateEndpoint = sprintf "/api/sessions/%s/live-values/evaluate" (Uri.EscapeDataString sessionId)
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
    | On -> [ Elem.span [ Attr.class' "live-type"; Attr.title (attrEnc node.TypeName) ] [ textEnc (FSharpTypeName.display node.TypeName) ] ]
    | Off -> []
  // The Source and State columns are cells of the row's own line, drawn from what the pane knows about the row, so a row it has nothing
  // to say about draws neither (a column is never an empty box). The strip under the row says the same, and CSS shows one of the two.
  let tweakCells (binding: string) (path: string list) : XmlNode list =
    match TweakView.tryRow tweaks { Binding = binding; Labels = path } with
    | None -> []
    | Some rowView ->
      [ match look.Layout.Source, rowView.Place with
        | On, PlaceOf.Placed place ->
          yield Elem.span [ Attr.class' "live-cell-source"; Attr.title (attrEnc place.File) ] [ textEnc (whereOf place) ]
        | On, PlaceOf.NotPlaced
        | Off, _ -> ()
        let described = describe rowView.State
        match look.Layout.State with
        | On ->
          yield
            Elem.span
              [ Attr.class' (sprintf "live-cell-state %s" (Tone.cssClass described.Tone))
                testid "tweak-cell-state"
                Attr.title (attrEnc (badgeTitle rowView.State)) ]
              [ textEnc described.Label ]
        | Off ->
          // With no State column the badge is in the row's strip, except for a row whose strip draws nothing: its badge is here.
          match badgePlace rowView.State with
          | BadgePlace.OnTheRow ->
            yield
              Elem.span
                [ Attr.class' (sprintf "live-state %s" (Tone.cssClass described.Tone))
                  testid "tweak-row-badge"
                  Attr.create "data-label" (attrEnc described.Label)
                  Attr.title (attrEnc (badgeTitle rowView.State)) ]
                [ textEnc described.Label ]
          | BadgePlace.InTheStrip -> () ]
  // While a row's file and running program disagree, the value in the heading is the running program's, and it says so: the knob
  // below carries the file's number with its own word (`LiveBindingsTweakView.renderStrip`), so no number is left unlabelled.
  let runningTag (binding: string) (path: string list) : XmlNode list =
    match TweakView.tryRow tweaks { Binding = binding; Labels = path } with
    | None -> []
    | Some rowView ->
      match NumberLabels.ofState rowView.State with
      | NumberLabels.RunningAndFile ->
        [ Elem.span [ Attr.class' "live-number-tag"; Attr.title NumberLabels.runningMeaning; testid "tweak-running-tag" ] [ textEnc NumberLabels.runningWord ] ]
      | NumberLabels.OneNumber -> []
  let wasNote (words: string) : XmlNode =
    Elem.span
      [ Attr.class' "live-was live-hidden"
        testid "live-was"
        Ds.class' ("live-hidden", attrEnc (sprintf "!(%s)" sinceGreenOpen))
        Ds.preserveAttr "class" ]
      [ textEnc words ]
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
      // The sentence is chosen by the reason, and says only what is on this row or in the pane's header: the run button, or the mode.
      Elem.span [ Attr.class' "live-held-reason" ] [ textEnc (LiveBindingsPane.heldSaying reason) ]
      match LiveBindingsPane.heldNext reason with
      | LiveBindingsPane.HeldNext.RunWithButton
      | LiveBindingsPane.HeldNext.RetryWithButton ->
        Elem.button
          [ Attr.class' "session-btn live-held-btn"
            Attr.create "aria-label" (attrEnc (sprintf "Run %s now: it runs your code under a deadline" node.Label))
            Attr.create "title" (attrEnc (sprintf "Run %s now. It runs your code, under a deadline and, where the OS allows, a syscall filter." node.Label))
            Ds.indicator indicator
            Ds.attr' ("disabled", sprintf "$%s" indicator)
            Ds.onClick (evaluateClick binding path) ]
          [ Text.raw "▶" ]
        Elem.span [ Attr.class' "live-held-evaluating"; Ds.show (sprintf "$%s" indicator) ] [ Text.enc (Glyphs.lead Glyph.Busy + "evaluating…") ]
      | LiveBindingsPane.HeldNext.SwitchMode _
      | LiveBindingsPane.HeldNext.NothingOnScreen -> ()
      yield! typeCells node
      yield! tweakCells binding path
    ]
  // The open state of a node's <details> is its own signal, named from the binding and every label down to the node, each
  // written as hex, so two nodes that share a label never share a signal.
  let nodeSignal (binding: string) (path: string list) : string =
    let pathToken =
      match path with
      | [] -> "r"
      | labels -> "r_" + (labels |> List.map hexOf |> String.concat "_")
    sprintf "open_%s_%s_%s" (signalIdent binding) (hexOf binding) pathToken
  /// `alreadySaid` is the sentence about a call that the row just before this one (its sibling) said. Rows that owe the same sentence
  /// (the fields of a record built by one call) say it once, on the first: the repeat read as clutter.
  let rec renderNode (binding: string) (path: string list) (node: LiveValueTree.LiveValueNode) (alreadySaid: string option) : XmlNode =
    // How this page shows the value (`LiveBindingsRowFormat`): a whole number carries a picker that moves it through as it is, hex,
    // binary and colour, and a number with a range of its own can be a bar. Every other node, and a leaf with nothing else to show,
    // is the cell it always was.
    let valueClass = "live-preview" + valueToneClass (valueToneOf node.Kind node.TypeName)
    let asIs =
      // The whole value is the tooltip: the cell clips a long one to a line with an ellipsis (always at phone width), and the
      // rest was out of reach. The pane's own "Long values: Full" setting is the way to see it without hovering.
      Elem.span [ Attr.class' valueClass; Attr.create "title" (attrEnc node.Preview) ] [ textEnc (sprintf "= %s" node.Preview) ]
    let shown =
      match node.Kind with
      | LiveValueTree.NodeKind.Leaf ->
        let id = LiveBindingsRowFormat.rowId binding path
        let range =
          TweakView.tryRow tweaks { Binding = binding; Labels = path }
          |> Option.bind (fun rowView -> LiveBindingsRowFormat.rangeOf rowView.Limits)
        LiveBindingsRowFormat.shownFor formats id node.TypeName range |> Option.map (fun chosen -> id, chosen)
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
      | LiveValueTree.NodeKind.Truncated -> None
    let formatAttrs, valueCells =
      match shown with
      | None -> [], [ asIs ]
      | Some(id, chosen) ->
        let choice = Tweak.RowFormatting.FormatChoice.ofFormat chosen.Current
        let button = LiveBindingsRowFormat.picker id chosen.Offered choice node.Label
        let cells =
          match chosen.Current with
          | Tweak.RowFormatting.RowFormat.AsIs -> [ asIs ]
          | Tweak.RowFormatting.RowFormat.Hex
          | Tweak.RowFormatting.RowFormat.Binary
          | Tweak.RowFormatting.RowFormat.Colour
          | Tweak.RowFormatting.RowFormat.Slider _ -> LiveBindingsRowFormat.valueNodes valueClass chosen.Current node.TypeName node.Preview
        [ Attr.create "data-format" (Tweak.RowFormatting.FormatChoice.token choice) ], cells @ LiveBindingsRowFormat.stateNote choice @ [ button ]
    let row =
      // Text.create escapes: labels, type names and previews are FSI-derived DATA, not trusted markup. A string
      // preview holding <script> must never inject into the dashboard DOM.
      Elem.div (Attr.class' "live-binding-node" :: formatAttrs) [
        glyphCell node
        nameCell node
        yield! valueCells
        // What the value was at the last green settle, on the Since green tab only.
        match Map.tryFind (Tweak.SinceGreen.pathKey binding path) moves with
        | Some(Tweak.SinceGreen.Moved.Differs(before, _)) -> wasNote (sprintf "was %s" before)
        | Some(Tweak.SinceGreen.Moved.Appeared _) -> wasNote "new since green"
        | Some(Tweak.SinceGreen.Moved.Left _)
        | None -> ()
        yield! runningTag binding path
        yield! typeCells node
        yield! tweakCells binding path
        yield! kindBadge node
      ]
    // What the files say about this row and what can be done about it (LiveBindingsTweakView): one strip under a row the pane
    // has something to say about, and nothing at all under one it has not. A strip inside a summary is never interactive, because
    // a click there would toggle the details.
    let key : RowKey = { Binding = binding; Labels = path }
    let stripFor (placement: Placement) : XmlNode list =
      match TweakView.tryRow tweaks key with
      | Some view ->
        let mine = describeThrough view.Through |> Option.map (fun said -> said.Detail)
        let view' =
          match mine, alreadySaid with
          | Some sentence, Some before when sentence = before -> { view with Through = ThroughCall.NoCall }
          | _ -> view
        // A value no file holds changes only by evaluating code, so the row carries the button that opens the Evaluate box.
        let way =
          match LiveBindingsTweakView.changesOnlyByEvaluating view.State with
          | true -> [ LiveBindingsTweakView.evaluateLink ]
          | false -> []
        renderStripWith (dependencyNotes tweaks key) key view' placement :: way
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
          let sayingOf (child: LiveValueTree.LiveValueNode) : string option =
            TweakView.tryRow tweaks { Binding = binding; Labels = path @ [ child.Label ] }
            |> Option.bind (fun view -> describeThrough view.Through)
            |> Option.map (fun said -> said.Detail)
          let said = node.Children |> List.map sayingOf
          // When every field came through the same call, the sentence is said once, above the fields it covers. Under the first
          // field it read as a note about that field alone (three reviewers).
          let shared =
            match said with
            | (Some first) :: rest when rest |> List.forall (fun s -> s = Some first) -> Some first
            | _ -> None
          match shared with
          | Some sentence ->
            Elem.div [ Attr.class' "live-shared-note"; testid "live-shared-note" ] [ textEnc (sprintf "Every field below: %s" sentence) ]
          | None -> ()
          yield!
            node.Children
            |> List.mapi (fun index child ->
              let before =
                match shared, index with
                | Some _, _ -> shared
                | None, 0 -> None
                | None, n -> said.[n - 1]
              renderNode binding (path @ [ child.Label ]) child before)
        ]
      ]
  let held = LiveBindingsPane.notEvaluatedCount view.Snapshot
  let names = view.Snapshot.Bindings |> List.map (fun b -> b.Name)
  [ Elem.div [ Attr.class' "live-pane-head" ] [
      // Values come first, and the head holds only what the pane says about itself in a sentence: where an edit goes. The mode buttons and
      // the legend are rows of the Display panel (`modeRows`). The chosen mode's meaning takes a line here only when the mode is not the
      // standard one, so a person is never reading values in a mode the head does not name.
      match TweakView.hasKnob tweaks with
      | true ->
        // While a change is saved and not running, the caption is the one place that names it and both remedies; before any change
        // it is the standing rule; with watched files it is the short one.
        let caption =
          match LiveBindingsTweakView.pendingChanges tweaks with
          | [] ->
            match TweakView.anyUnwatched tweaks with
            | true -> LiveBindingsPane.PaneNote.ChangesGoToAnUnwatchedFile
            | false -> LiveBindingsPane.PaneNote.ChangesGoToTheFile
          | names -> LiveBindingsPane.PaneNote.PendingChange names
        // The standing rule is plain text; a pending change is the one loud sentence (`live-writes-pending`).
        let loud = match LiveBindingsTweakView.pendingChanges tweaks with | [] -> "" | _ -> " live-writes-pending"
        let captionNode =
          Elem.span [ Attr.class' ("live-pane-count live-writes-caption" + loud); testid "live-writes-caption" ] [
            textEnc (LiveBindingsPane.PaneNote.text caption) ]
        // A pending change is a banner: the sentence and the button that answers it sit together, so nobody hunts for the remedy.
        match LiveBindingsTweakView.pendingChanges tweaks with
        | [] -> ()
        | names ->
          // The banner names two remedies, so it offers both: the row's own Apply for each changed binding, and the rebuild.
          let applyButtons =
            names
            |> List.collect (fun name ->
              let key : RowKey = { Binding = name; Labels = [] }
              match TweakView.tryRow tweaks key with
              | Some rowView -> LiveBindingsTweakView.applyNodes key rowView
              | None -> [])
          Elem.div [ Attr.class' "live-writes-banner"; testid "live-writes-banner" ] [
            captionNode
            yield! applyButtons
            RebuildRemedy.button RemedyPlace.InPaneBar ]
      | false -> ()
      match LiveBindingsPane.ModeCaption.ofMode view.Notes.Mode with
      | LiveBindingsPane.ModeCaption.Shown chosen ->
        Elem.div [ Attr.class' "live-containment live-mode-caption"; testid "live-mode-caption" ] [
          textEnc (LiveBindingsPane.PaneNote.text (LiveBindingsPane.PaneNote.ModeMeans chosen)) ]
      | LiveBindingsPane.ModeCaption.NotShown -> ()
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
        yield! [ columnHeader look ]
        yield!
          bindings
          |> List.map (fun b ->
            Elem.div
              ( Attr.id (DockIds.binding b.Name) :: signalClasses "live-binding"
                @ [ Ds.class' ("live-hidden", attrEnc (hiddenExpr b.Name (Set.contains b.Name changedNames) (Set.contains b.Name movedNames)))
                    Ds.class' ("live-pinned", isPinnedExpr b.Name) ])
              [ Elem.button
                  [ Attr.id (DockIds.bindingPin b.Name)
                    Attr.class' "session-btn live-binding-pin"
                    Attr.type' "button"
                    Attr.create "aria-label" (attrEnc (sprintf "Pin %s to the top of the list" b.Name))
                    Attr.title (attrEnc (sprintf "Pin %s to the top of the list" b.Name))
                    Attr.create "aria-pressed" "false"
                    Ds.attr' ("aria-pressed", sprintf "%s ? 'true' : 'false'" (isPinnedExpr b.Name))
                    Ds.class' ("session-btn-on", isPinnedExpr b.Name)
                    Ds.preserveAttr "class aria-pressed"
                    Ds.onClick (attrEnc (togglePinExpr b.Name)) ]
                  [ pinIcon ]
                Elem.div [ Attr.class' "live-binding-tree" ] [ renderNode b.Name [] b.Root None ] ])
        Elem.div
          [ Attr.id DockIds.NoMatch
            Attr.class' "live-empty live-hidden"
            Ds.class' ("live-hidden", attrEnc (sprintf "!(%s) || $%s === '%s'" (nothingMatchesExpr names) DockSignals.LiveTab (WatchTab.token WatchTab.Watch)))
            Ds.preserveAttr "class" ]
          [ textEnc (LiveBindingsPane.PaneNote.text LiveBindingsPane.PaneNote.NoNameMatchesFilter) ]
        // An open tab with nothing on it says so, in words, instead of showing an empty pane.
        yield!
          [ WatchTab.Pinned; WatchTab.Changes; WatchTab.SinceGreen ]
          |> List.map (fun tab ->
            Elem.div
              [ Attr.id (sprintf "live-tab-empty-%s" (WatchTab.token tab))
                Attr.class' "live-empty live-hidden"
                testid (sprintf "live-tab-empty-%s" (WatchTab.token tab))
                Ds.class' ("live-hidden", attrEnc (sprintf "!(%s)" (tabEmptyExpr tab names changedNames movedNames)))
                Ds.preserveAttr "class" ]
              [ textEnc
                  (match tab, green with
                   | WatchTab.SinceGreen, LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet -> "The tests have not settled green yet, so there is nothing to compare with."
                   | _ -> WatchTab.emptyWords tab) ])
        // A binding that was there at the last green settle and is not now has no row to show it on, so it is named here.
        yield!
          moves
          |> Map.toList
          |> List.choose (fun (key, moved) ->
            match moved with
            | Tweak.SinceGreen.Moved.Left before when Tweak.SinceGreen.bindingOf key = key -> Some(key, before)
            | Tweak.SinceGreen.Moved.Left _
            | Tweak.SinceGreen.Moved.Differs _
            | Tweak.SinceGreen.Moved.Appeared _ -> None)
          |> function
            | [] -> []
            | gone ->
              [ Elem.div
                  [ Attr.id "live-since-green-left"
                    Attr.class' "live-since-green-left live-hidden"
                    testid "live-since-green-left"
                    Ds.class' ("live-hidden", attrEnc (sprintf "!(%s)" sinceGreenOpen))
                    Ds.preserveAttr "class" ]
                  [ textEnc ("No longer bound: " + (gone |> List.map (fun (name, before) -> sprintf "%s (was %s)" name before) |> String.concat ", ")) ] ]
        match view.Snapshot.Truncated with
        | true -> Elem.div [ Attr.class' "live-truncated live-truncated-note" ] [ textEnc (LiveBindingsPane.PaneNote.text LiveBindingsPane.PaneNote.ValuesCutShort) ]
        | false -> ()
      // The Watch tab's rows share the tree's room and its scroll.
      yield! watchTab
    ] ]

/// The tab bar: All, Pinned and Changes, each with how many it lists. The open tab is a signal, so a push never closes it, and the pressed
/// state is read from the signal and said in the markup (`aria-pressed`) as well as in the fill. The Pinned count is the page's own, from
/// the pins it holds; the other two are the server's.
let tabBar (bindingCount: int) (changedCount: int) (movedCount: int) (watchCount: int) : XmlNode =
  let open' (tab: WatchTab) = sprintf "$%s === '%s'" DockSignals.LiveTab (WatchTab.token tab)
  Elem.div
    [ Attr.class' "live-tabs"
      testid "live-tabs"
      Attr.create "role" "group"
      Attr.create "aria-label" "Which of the bindings are listed" ]
    [ for tab in WatchTab.all ->
        let count =
          match tab with
          | WatchTab.All -> [ textEnc (string bindingCount) ]
          | WatchTab.Changes -> [ textEnc (string changedCount) ]
          | WatchTab.SinceGreen -> [ textEnc (string movedCount) ]
          | WatchTab.Watch -> [ textEnc (string watchCount) ]
          | WatchTab.Pinned -> [ textEnc "0" ]
        Elem.button
          [ Attr.id (sprintf "live-tab-%s" (WatchTab.token tab))
            Attr.class' "session-btn session-btn-labeled live-tab"
            Attr.type' "button"
            testid (sprintf "live-tab-%s" (WatchTab.token tab))
            Attr.create "aria-pressed" (match tab with | WatchTab.All -> "true" | WatchTab.Pinned | WatchTab.Changes | WatchTab.SinceGreen | WatchTab.Watch -> "false")
            Ds.attr' ("aria-pressed", sprintf "%s ? 'true' : 'false'" (open' tab))
            Ds.class' ("session-btn-on", open' tab)
            Ds.preserveAttr "class aria-pressed"
            // The page hides what the tab does not list at once, and the post tells the server which tab the page has open, so it draws
            // the view that tab has.
            Ds.onClick (sprintf "$%s = '%s'; %s" DockSignals.LiveTab (WatchTab.token tab) (Ds.post DockSignals.LiveTabEndpoint)) ]
          [ Elem.span [ Attr.class' "session-btn-word" ] [ textEnc (WatchTab.label tab) ]
            Elem.span
              ([ Attr.class' "live-tab-count"; testid (sprintf "live-tab-count-%s" (WatchTab.token tab)) ]
               @ (match tab with
                  | WatchTab.Pinned -> [ Ds.text (sprintf "$%s.split(',').filter(Boolean).length" DockSignals.LivePins) ]
                  | WatchTab.All
                  | WatchTab.Changes
                  | WatchTab.SinceGreen
                  | WatchTab.Watch -> []))
              count ] ]

/// The open pane: a header (what opened it, when it was read, a filter, the pin), then the tree.
/// What the pane is for, in a sentence: under the title while the dock is tall, and the title's tooltip and a note in the Display panel
/// once the dock is too short for the line.
let [<Literal>] PaneSentence = "The values in this session. Edit one: the file changes now, and the row says how the session catches up."

let renderPaneWithRemedy (remedy: XmlNode) (look: View) (controls: LiveBindingsViewPanel.ViewControls option) (tweaks: TweakView) (sessionId: string) (source: BindingsSource) (because: OpenBecause) (display: PinDisplay) : XmlNode =
  let stamp =
    match source with
    | WalkedBindings view ->
      // The output panel beside this pane prints the daemon's UTC clock, so the stamp uses it too and says so: a local time here
      // read as a different moment under output lines stamped hours apart.
      LiveBindingsPane.PaneNote.text (LiveBindingsPane.PaneNote.ValuesAsOf(view.Snapshot.CapturedAt.UtcDateTime.ToString("HH:mm:ss 'UTC'")))
    | PrintedBindings _ -> LiveBindingsPane.PaneNote.text LiveBindingsPane.PaneNote.ReadFromPrinted
    | NothingBound -> ""
  // The Watch tab's expressions are the last thing in the tree, so they scroll with it in the room the tree has.
  let watchTab : XmlNode list =
    match controls with
    | Some viewControls ->
      [ Elem.div
          [ Attr.id "live-watch-tab"
            Attr.class' "live-watch-tab live-hidden"
            Ds.class' ("live-hidden", sprintf "$%s !== '%s'" DockSignals.LiveTab (WatchTab.token WatchTab.Watch))
            Ds.preserveAttr "class" ]
          [ viewControls.Watch.Body ] ]
    | None -> []
  // What the walked pane says about itself (`live-pane-head`) is chrome, so it goes with the title row; the rest is the tree.
  let headNodes, body =
    match source with
    | WalkedBindings view ->
      let formats =
        match controls with
        | Some viewControls -> viewControls.Formats
        | None -> Map.empty
      let green =
        match controls with
        | Some viewControls -> viewControls.SinceGreen
        | None -> LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet
      match renderTree look sessionId tweaks formats green view watchTab with
      | head :: rest -> [ head ], rest
      | [] -> [], []
    | PrintedBindings scope -> [], [ treeElement look [ renderBindingsPanel (Some scope) ] ]
    | NothingBound -> [], [ treeElement look [ renderEmptyState ] ]
  // The text panel carries `bindings-panel` itself, so the wrapper must not repeat it.
  let paneId =
    match source with
    | PrintedBindings _ -> DockIds.Pane
    | WalkedBindings _
    | NothingBound -> DomIds.BindingsPanel
  Elem.div (Attr.id paneId :: followPin display "panel live-pane") [
   // Everything above the tree, in one box that is never more than a share of the dock (`DockCompaction.ChromeSharePercent`) and scrolls
   // on its own if it must, so the tree always has the rest of the dock.
   Elem.div [ Attr.id "live-pane-chrome"; Attr.class' "live-pane-chrome"; testid "live-pane-chrome" ] [
    Elem.div [ Attr.class' "live-pane-bar" ] [
      // The sentence under the title is also the title's tooltip, so a dock too short for the line still has it.
      Elem.span [ Attr.class' "live-pane-title"; Attr.title (attrEnc PaneSentence) ] [ Text.raw "Live bindings" ]
      Elem.span [ Attr.class' "live-pane-count" ] [ textEnc (OpenBecause.text because) ]
      match controls |> Option.map (fun viewControls -> viewControls.Freshness) with
      | Some(LiveBindingsViewPanel.LoopSaid.Said chip) -> freshnessChip chip
      | Some LiveBindingsViewPanel.LoopSaid.NothingSaid
      | None -> ()
      Elem.span [ Attr.class' "live-pane-stamp"; Attr.create "title" (attrEnc (stamp + ". The values are read again after every eval.")) ] [
        textEnc stamp
        // How old the reading is, ticking in the browser: an absolute UTC time alone could not tell a frozen display from a current one.
        match source with
        | WalkedBindings view ->
          Elem.span [] [ textEnc " · "; ElapsedClock.span (ElapsedClock.agoExpr (view.Snapshot.CapturedAt.ToUnixTimeMilliseconds())) ]
        | PrintedBindings _
        | NothingBound -> () ]
      // When the session runs older code than its files say: what is wrong, and the button that fixes it (empty otherwise).
      remedy
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
            [ textEnc (LiveBindingsPane.PaneNote.text (LiveBindingsPane.PaneNote.NotInAnyFile count)) ]
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
    // What the pane is for, in one line under its title (reviewers could say what it was only by inference). Wide windows only:
    // on a phone every line before the first binding is a line of the list pushed down.
    Elem.div [ Attr.class' "live-pane-sub"; testid "live-pane-sub" ] [
      // A phone gets the short form: the long one took three lines of the first screen before the first binding.
      Elem.span [ Attr.class' "pane-sub-long" ] [ Text.raw PaneSentence ]
      Elem.span [ Attr.class' "pane-sub-short" ] [ Text.raw "Edit a value: the file changes now." ] ]
    // On a phone the page is one column and Output and Sessions sit below the bindings. These say so, and take you there.
    Elem.nav [ Attr.class' "live-jump"; testid "live-jump"; Attr.create "aria-label" "Jump to another part of the page" ] [
      Elem.a [ Attr.href "#output-section" ] [ Text.raw "Output ↓" ]
      Elem.a [ Attr.href "#sidebar" ] [ Text.raw "Sessions ↓" ] ]
    // What the session holds that the files do not, and what the program changed: above the tree so it stays in view.
    match look.Changes with
    | On -> yield! changesStrip (Tweak.ChangesStrip.Changes.ofView tweaks)
    | Off -> ()
    match source with
    | WalkedBindings view ->
      let watchCount = match controls with | Some c -> c.Watch.Count | None -> 0
      let movedCount =
        match controls with
        | Some c ->
          match c.SinceGreen with
          | LiveBindingsViewPanel.GreenSaid.Moved moved -> (Tweak.SinceGreen.movedBindings moved).Count
          | LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet -> 0
        | None -> 0
      tabBar view.Snapshot.Bindings.Length (changedBindings (Tweak.ChangesStrip.Changes.ofView tweaks)).Count movedCount watchCount
    | PrintedBindings _
    | NothingBound -> ()
    yield! headNodes
   ]
   // The Display panel carries the mode buttons and the legend when there are walked values to read in a mode. It sits between the chrome
   // and the tree: a popover over the tree in a tall dock, and in the tree's place in a short one (`DockCompaction.PanelPlacement`).
   match controls with
   | Some viewControls ->
     match source with
     | WalkedBindings view ->
       viewControls.PanelWith (modeRows sessionId view.Notes.Mode @ [ Elem.div [ Attr.class' "live-view-note" ] [ textEnc PaneSentence ] ])
     | PrintedBindings _
     | NothingBound -> viewControls.Panel
   | None -> ()
   yield! body
  ]

/// The open pane with no remedy note in its bar.
let renderPaneWith (look: View) (controls: LiveBindingsViewPanel.ViewControls option) (tweaks: TweakView) (sessionId: string) (source: BindingsSource) (because: OpenBecause) (display: PinDisplay) : XmlNode =
  renderPaneWithRemedy (Text.raw "") look controls tweaks sessionId source because display

/// The open pane with no knob, in the REPL workflow's recommended view: what a session whose files have not been asked about shows.
let renderPane (sessionId: string) (source: BindingsSource) (because: OpenBecause) (display: PinDisplay) : XmlNode =
  renderPaneWith (View.recommended ViewWorkflow.Repl) None TweakView.none sessionId source because display

/// The dock, decided and drawn. Both answers of the pin are rendered when the pin can matter (the signal picks between them);
/// when a session has bindings, or there is no session to pin on, only the one answer exists. `tweaks` is what the session's files
/// say about each row (the persistence state and the knob): rows it has nothing for are drawn as they always were. `look` is the
/// view the session's workflow and app resolved to (`SettingsCatalog.viewFor`): which columns a row has, how much of a value
/// prints, how tall a row is. `controls` is the pane bar's view button and the panel it opens; a page with no session to choose a view
/// for has none.
let renderDockWithRemedy
  (remedy: XmlNode)
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
    | PaneOpen because -> [ renderPaneWithRemedy remedy look controls tweaks sessionId source because ShownAlways ]
    | PaneCollapsed why ->
      match CollapsedBecause.pin why with
      | PinNotOffered -> [ renderCollapsedBar why ShownAlways ]
      | PinOffered ->
        match PaneState.decide { facts with Pin = Pinned } DockPane.LiveBindings with
        | PaneOpen pinnedBecause ->
          [ renderCollapsedBar why ShownUnlessPinned; renderPaneWithRemedy remedy look controls tweaks sessionId source pinnedBecause ShownOnlyWhilePinned ]
        | PaneCollapsed _ -> [ renderCollapsedBar why ShownAlways ]

  // Qualified rather than `open`: AppOutputView has its own `renderCollapsedBar`, and opening it here
  // would make this file's own one silently resolve to the other pane's.
  let appOutputPane =
    let pane = Option.defaultValue SageFs.Server.AppOutputPane.AppOutputPane.create appOutput
    [ SageFs.Server.AppOutputView.render (PaneState.decide facts DockPane.AppOutput) pane ]

  shell (liveBindings @ appOutputPane)

/// The dock with no remedy note in the pane bar.
let renderDockWith
  (look: View)
  (controls: LiveBindingsViewPanel.ViewControls option)
  (tweaks: TweakView)
  (session: SessionInView)
  (sessionId: string)
  (source: BindingsSource)
  (appOutput: SageFs.Server.AppOutputPane.AppOutputPane option)
  : XmlNode =
  renderDockWithRemedy (Text.raw "") look controls tweaks session sessionId source appOutput

/// The dock with no knob (a session whose files have not been asked about, and the no-session page).
let renderDock (session: SessionInView) (sessionId: string) (source: BindingsSource) : XmlNode =
  renderDockWith (View.recommended ViewWorkflow.Repl) None TweakView.none session sessionId source None

/// A brief highlight on a value that changed. It is drawn from what the SSE morph really rewrote, so only a changed value
/// flashes and nothing polls: the browser's own mutation record says a `.live-preview` text node was replaced, and that cell
/// gets an underline in the accent colour that fades. An underline and no background fill: a fill of the accent colour put the value's
/// own colours (and the dim words beside it) on a light band, where they could not be read. It uses the Web Animations API, not a class, because the morph owns `class` and
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
          preview.animate([{ boxShadow: 'inset 0 -0.2em 0 0 ' + accent }, { boxShadow: 'inset 0 -0.2em 0 0 transparent' }], { duration: 1000, easing: 'ease-out' });
        });
      }).observe(document.body, { subtree: true, childList: true, characterData: true });
    })();
  """ DockIds.Dock) ]
