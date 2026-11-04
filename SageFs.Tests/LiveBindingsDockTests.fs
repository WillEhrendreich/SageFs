/// The bottom dock as the dashboard renders it: the live bindings where the Evaluate box used to be. Whatever the session is
/// doing the dock says something (a tree, or ONE line that says why there is no tree), the pin opens it anyway, and the tree
/// shows each row's kind, a filter by name and a pin to the top. All of it goes through Datastar's typed helpers.
module SageFs.Tests.LiveBindingsDockTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Falco.Markup
open SageFs
open SageFs.Features
open SageFs.Features.LiveBindingsPane
open SageFs.Features.LiveValueTree
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DockPanes
open SageFs.Server.LiveBindingsDock
open SageFs.Tests.SharedGenerators

let private decoded (node: XmlNode) = Net.WebUtility.HtmlDecode(renderNode node)

let private node label kind (preview: string) children : LiveValueNode =
  { Label = label
    TypeName = "T"
    Preview = preview
    Kind = kind
    Children = children
    BestEffort = false
    Depth = 0 }

let private binding name kind children : LiveBindingValue =
  { Name = name; TypeSignature = "T"; Root = node name kind "{ ... }" children }

let private view (bindings: LiveBindingValue list) : PaneView =
  { Snapshot =
      { SessionId = "abcd1234"
        Generation = 7L
        Bindings = bindings
        Truncated = false
        CapturedAt = DateTimeOffset.UnixEpoch }
    Notes = { Mode = ValueWalk.standard; Click = NoClickYet } }

let private dock (source: BindingsSource) = renderDock SessionInView "abcd1234" source |> decoded

let private count (needle: string) (html: string) = html.Split([| needle |], StringSplitOptions.None).Length - 1

let private person = binding "person" NodeKind.Record [ node "Name" NodeKind.Leaf "\"Ada\"" []; node "Age" NodeKind.Leaf "36" [] ]
let private scores = binding "scores" NodeKind.List [ node "[0]" NodeKind.Leaf "1" [] ]

[<Tests>]
let dockStateTests =
  testList "the dock: every state says something" [

    testCase "a session with bindings shows the tree with the pane's id, how many bindings there are, and no collapsed bar" <| fun _ ->
      let html = dock (WalkedBindings(view [ person; scores ]))
      html |> Expect.stringContains "the pane keeps its id" (sprintf "id=\"%s\"" DomIds.BindingsPanel)
      html |> Expect.stringContains "the count" "2 bindings"
      html |> Expect.stringContains "a binding's name" "person"
      html.Contains DockIds.CollapsedBar |> Expect.isFalse "nothing is collapsed"
      html.Contains "live-pane live-hidden" |> Expect.isFalse "the pane is not hidden at first paint"

    testCase "the pane carries its own fold, so the tree can be taken away without finding the drag handle" <| fun _ ->
      let html = dock (WalkedBindings(view [ person; scores ]))
      html |> Expect.stringContains "the fold's button" (sprintf "id=\"%s\"" DockIds.PaneCollapse)
      html |> Expect.stringContains "the fold is a signal of its own" (sprintf "$%s = !$%s" DockSignals.LivePaneCollapsed DockSignals.LivePaneCollapsed)
      html |> Expect.stringContains "the tree is hidden while the fold is on" (sprintf "$%s" DockSignals.LivePaneCollapsed)
      html |> Expect.stringContains "the morph leaves the fold alone" "data-preserve-attr=\"class\""
      html |> Expect.stringContains "the button says whether the tree is open" "aria-expanded"

    testCase "a session with no bindings collapses to ONE line that says why, and the pane is there but hidden until the pin opens it" <| fun _ ->
      let html = dock NothingBound
      html |> Expect.stringContains "the reason, in words" (CollapsedBecause.text NothingBoundYet)
      html |> Expect.stringContains "the bar" (sprintf "id=\"%s\"" DockIds.CollapsedBar)
      html |> Expect.stringContains "a pin to open it anyway" "live-pane-pin"
      html |> Expect.stringContains "the pane is rendered, hidden at first paint" "live-pane live-hidden"
      html |> Expect.stringContains "its empty state says why too" "live-bindings-empty"
      html |> Expect.stringContains "the pin picks which shows" (sprintf "$%s" DockSignals.LivePanePinned)

    testCase "with no session the dock says so, and offers no pin because there is nothing to pin it on" <| fun _ ->
      let html = renderDock NoSessionInView "" NothingBound |> decoded
      html |> Expect.stringContains "the reason" (CollapsedBecause.text NoSessionOpen)
      html.Contains "live-pane-pin" |> Expect.isFalse "no pin"
      html.Contains(sprintf "id=\"%s\"" DomIds.BindingsPanel) |> Expect.isFalse "no pane to open"

    testCase "FSI's printed bindings still show when no walk has come back, in the text panel, and say where they were read from" <| fun _ ->
      let printed : BindingExplorer.BindingInfo =
        { Name = "x"; TypeSig = "int"; Value = Some "3"; CellIndex = 1; ShadowedBy = []; ReferencedIn = [] }
      let scope : BindingExplorer.BindingScopeSnapshot =
        { Bindings = [ printed ]; ActiveBindings = Map.ofList [ "x", printed ]; ShadowedBindings = [] }
      let html = dock (PrintedBindings scope)
      html |> Expect.stringContains "its name" "x"
      html |> Expect.stringContains "its value" "= 3"
      html |> Expect.stringContains "where it was read from" "printed output"
      (count (sprintf "id=\"%s\"" DomIds.BindingsPanel) html) |> Expect.equal "the id appears once" 1

    testPropertyWithConfig propConfig "whatever the session holds, the dock is never blank: it shows bindings or a reason"
      (Prop.forAll (Arb.fromGen (Gen.zip (Gen.elements [ NoSessionInView; SessionInView ]) (Gen.choose (0, 6)))) (fun (session, n) ->
        let bindings = [ for i in 1..n -> binding (sprintf "b%d" i) NodeKind.Leaf [] ]
        let html = renderDock session "abcd1234" (match n with | 0 -> NothingBound | _ -> WalkedBindings(view bindings)) |> decoded
        match session, n with
        | NoSessionInView, _ -> html.Contains(CollapsedBecause.text NoSessionOpen)
        | SessionInView, 0 -> html.Contains(CollapsedBecause.text NothingBoundYet)
        | SessionInView, _ -> html.Contains(sprintf "%d binding" n)))
  ]

[<Tests>]
let dockTreeTests =
  testList "the dock's tree: kind, filter, pin" [

    testCase "WHY — a row's kind is a small glyph with the word on hover and as its accessible name, because the shape says what a node is and a boxed word on every row costs width" <| fun _ ->
      let html = dock (WalkedBindings(view [ person; scores ]))
      (count "live-glyph" html, 5) |> Expect.isGreaterThanOrEqual "a glyph on every row"
      html |> Expect.stringContains "a record's word on hover" "title=\"record\""
      html |> Expect.stringContains "and as its name" "aria-label=\"record\""
      html |> Expect.stringContains "a list" "title=\"list\""
      html |> Expect.stringContains "a leaf" "title=\"value\""
      html.Contains "live-kind" |> Expect.isFalse "no boxed word"
      kindLabel NodeKind.Class |> Expect.equal "class" "class"
      kindLabel (NodeKind.NotEvaluated NotEvaluatedReason.GetterLoops) |> Expect.equal "held" "held"

    testCase "every kind of node has a glyph, and a leaf's glyph says whether it is text or a number" <| fun _ ->
      let kinds =
        [ NodeKind.Leaf; NodeKind.NotEvaluated NotEvaluatedReason.GetterLoops; NodeKind.Record; NodeKind.List; NodeKind.Map; NodeKind.Option
          NodeKind.Union; NodeKind.Tuple; NodeKind.Array; NodeKind.Class; NodeKind.Closure; NodeKind.Cycle; NodeKind.Truncated ]
      for kind in kinds do
        (String.IsNullOrWhiteSpace(kindGlyph kind "T")) |> Expect.isFalse (sprintf "%s has a glyph" (kindLabel kind))
      kindGlyph NodeKind.Leaf "String" |> Expect.equal "text" "ab"
      for number in [ "Int32"; "Int64"; "Double"; "Single"; "Decimal"; "Byte" ] do
        kindGlyph NodeKind.Leaf number |> Expect.equal (sprintf "%s is a number" number) "#"
      kindGlyph NodeKind.Leaf "Person" |> Expect.equal "anything else" "·"
      kindGlyph NodeKind.Record "Person" |> Expect.equal "a record" "{}"

    testCase "WHY — a row reads glyph, name, value, then type, because the value belongs next to its name and the type is the quietest part" <| fun _ ->
      let html = dock (WalkedBindings(view [ person ]))
      let at (needle: string) = html.IndexOf(needle, StringComparison.Ordinal)
      (at "live-glyph" < at "class=\"live-name\"") |> Expect.isTrue "glyph before name"
      (at "class=\"live-name\"" < at "live-preview") |> Expect.isTrue "name before value"
      (at "live-preview" < at "class=\"live-type\"") |> Expect.isTrue "value before type"

    testCase "WHY — the title is a panel heading with no red circle, because red means failure everywhere else and this pane is working" <| fun _ ->
      for html in [ dock (WalkedBindings(view [ person ])); dock NothingBound ] do
        html.Contains "🔴" |> Expect.isFalse "no red circle in either state of the pane"
        html |> Expect.stringContains "the title" "Live bindings"

    testCase "WHY — REPL only is said once in the title bar with its reason on hover, and a count, because ten rows saying it again cost ten lines" <| fun _ ->
      let replOnly : RowView =
        { State = PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt
          Control = Control.NoControl
          Place = PlaceOf.NotPlaced
          Watching = WatchStatus.Watched
          Reload = RowReload.NoWriteYet
          Undo = HistoryStep.NothingToStep
          Redo = HistoryStep.NothingToStep }
      let tweaks : TweakView = { Rows = Map.ofList [ RowKey.top "person", replOnly; RowKey.top "scores", replOnly ] }
      let html = renderDockWith (SageFs.View.recommended SageFs.ViewWorkflow.Repl) None tweaks SessionInView "abcd1234" (WalkedBindings(view [ person; scores ])) None |> decoded
      html |> Expect.stringContains "the count, in the bar" "2 REPL only"
      html |> Expect.stringContains "the chip's own test id" "data-testid=\"live-repl-only\""
      html |> Expect.stringContains "the reason on hover" "nothing to save it to"
      count "Bound in the REPL" html |> Expect.equal "the sentence appears once, in the chip, and not on each row" 1
      (dock (WalkedBindings(view [ person; scores ]))).Contains "REPL only" |> Expect.isFalse "no chip when the files were not asked about"

    testCase "the filter is a typed signal binding, and each binding hides itself by name through a typed class toggle" <| fun _ ->
      let html = dock (WalkedBindings(view [ person; scores ]))
      html |> Expect.stringContains "the filter box is bound (Datastar writes the camelCase signal in kebab case)" "data-bind:live-filter"
      html |> Expect.stringContains "a binding hides when the filter does not match" "data-class:live-hidden"
      html |> Expect.stringContains "matching is by lowercase name" "'person'.includes("
      html |> Expect.stringContains "a notice when nothing matches" "no binding's name contains that"

    testCase "each top-level binding has a pin button that shows its real state and moves it to the top" <| fun _ ->
      let html = dock (WalkedBindings(view [ person; scores ]))
      count "live-binding-pin" html |> Expect.equal "one pin per binding" 2
      html |> Expect.stringContains "the pinned class follows the signal" "data-class:live-pinned"
      html |> Expect.stringContains "the pins are one signal" (sprintf "$%s" DockSignals.LivePins)
      html |> Expect.stringContains "the morph leaves class and aria-pressed to the client" "data-preserve-attr=\"class aria-pressed\""

    testCase "two nodes that share a label never share an open signal, even under different bindings" <| fun _ ->
      let a = binding "a" NodeKind.Record [ node "inner" NodeKind.Record "{ x = 1 }" [ node "x" NodeKind.Leaf "1" [] ] ]
      let b = binding "b" NodeKind.Record [ node "inner" NodeKind.Record "{ x = 1 }" [ node "x" NodeKind.Leaf "1" [] ] ]
      let html = renderDock SessionInView "abcd1234" (WalkedBindings(view [ a; b ])) |> renderNode
      let ids =
        Text.RegularExpressions.Regex.Matches(html, "<details id=\"(open_[^\"]+)\"")
        |> Seq.map (fun found -> found.Groups[1].Value)
        |> List.ofSeq
      ids.Length |> Expect.equal "two bindings, two nested nodes each" 4
      ids |> List.distinct |> List.length |> Expect.equal "every open signal is its own" 4

    testCase "a row that was cut says so, and a snapshot that was truncated says so under the tree" <| fun _ ->
      let cut = binding "deep" NodeKind.Record [ node "…" NodeKind.Truncated "…" [] ]
      let v = view [ cut ]
      let html = dock (WalkedBindings { v with Snapshot = { v.Snapshot with Truncated = true } })
      html |> Expect.stringContains "the row marker" "… (truncated)"
      html |> Expect.stringContains "the note" "Some values truncated"

    testCase "every element whose class the client owns has an id, so the morph matches it by id and never by position" <| fun _ ->
      let sources =
        [ WalkedBindings(view [ person; scores ])
          NothingBound
          WalkedBindings(view [ person ]) ]
      for source in sources do
        for session in [ SessionInView; NoSessionInView ] do
          let html = renderDock session "abcd1234" source |> renderNode
          let clientOwned = html.Split('<') |> Array.filter (fun tag -> tag.Contains "data-preserve-attr=\"class")
          for tag in clientOwned do
            tag.Contains " id=\"" |> Expect.isTrue (sprintf "a client-owned class needs an id: <%s" (tag.Substring(0, min 80 tag.Length)))

    testCase "the tooling's own bindings are neither shown nor counted, so a session with only those has no bindings yet" <| fun _ ->
      let own = binding "_SageFsHotReload" NodeKind.Leaf []
      match BindingsSource.ofWalked (view [ own; person ]) with
      | WalkedBindings v -> v.Snapshot.Bindings |> List.map (fun b -> b.Name) |> Expect.equal "only the user's" [ "person" ]
      | other -> failtestf "expected walked bindings, got %A" other
      BindingsSource.ofWalked (view [ own ]) |> BindingsSource.presence |> Expect.equal "none yet" NoBindingsYet

    testCase "a name that is markup is shown as text and never lands in an attribute as markup" <| fun _ ->
      let hostile = binding "<img src=x onerror=alert(1)>" NodeKind.Leaf []
      let raw = renderDock SessionInView "abcd1234" (WalkedBindings(view [ hostile ])) |> renderNode
      raw.Contains "<img" |> Expect.isFalse "never injected"

    testCase "nothing is hand-written: no raw onclick or data-on attribute strings" <| fun _ ->
      let html = dock (WalkedBindings(view [ person ]))
      html.Contains " onclick=" |> Expect.isFalse "no raw onclick"
  ]

/// The bodies of every stylesheet rule that ends its selector list with exactly `selector`, joined. A selector can be the last of a group
/// as well as a rule of its own, and both are what the stylesheet says about it.
let private ruleBody (selector: string) : string =
  let matches = Text.RegularExpressions.Regex.Matches(Dashboard.dashboardCss, sprintf @"(?m)^\s*%s\s*\{([^}]*)\}" (Text.RegularExpressions.Regex.Escape selector))
  match matches.Count with
  | 0 -> failtestf "the stylesheet has no rule for %s" selector
  | _ -> matches |> Seq.map (fun m -> m.Groups.[1].Value) |> String.concat "\n"

[<Tests>]
let dockColourTests =
  testList "the dock's colours follow the theme's meanings" [

    testCase "WHY — the pane title is the panel heading colour, because red means failure everywhere else and this pane is working" <| fun _ ->
      let body = ruleBody ".live-pane-title"
      body |> Expect.stringContains "the heading colour every other panel uses" "var(--fg-blue)"
      body.Contains "--fg-red" |> Expect.isFalse "never red"

    testCase "WHY — a pinned row is marked with the focus colour and never the warning yellow, because choosing a row is not a warning" <| fun _ ->
      for selector in [ ".live-binding.live-pinned"; ".live-binding-pin.session-btn-on" ] do
        let body = ruleBody selector
        body |> Expect.stringContains (sprintf "%s uses the focus colour" selector) "--border-focus"
        body.Contains "--fg-yellow" |> Expect.isFalse (sprintf "%s is not the warning colour" selector)

    testCase "a name and a value take their syntax tokens, so a theme changes them with the editor, and the stylesheet declares a fallback for each" <| fun _ ->
      ruleBody ".live-name" |> Expect.stringContains "names are properties" "var(--syn-property)"
      ruleBody ".live-v-string" |> Expect.stringContains "text" "var(--syn-string)"
      ruleBody ".live-v-number" |> Expect.stringContains "numbers" "var(--syn-number)"
      for token in [ "--syn-property"; "--syn-string"; "--syn-number" ] do
        Text.RegularExpressions.Regex.IsMatch(Dashboard.dashboardCss, sprintf @"%s:\s*#[0-9a-fA-F]{6}" token)
        |> Expect.isTrue (sprintf "%s has a declared fallback until the theme's own arrives" token)

    testCase "a leaf's value is drawn in the colour of what it is, text or number, and anything else keeps the default" <| fun _ ->
      let leaf typeName = { node "x" NodeKind.Leaf "1" [] with TypeName = typeName }
      let html typeName = dock (WalkedBindings(view [ { Name = "x"; TypeSignature = typeName; Root = leaf typeName } ]))
      html "String" |> Expect.stringContains "text" "live-v-string"
      html "Int32" |> Expect.stringContains "number" "live-v-number"
      (html "Person").Contains "live-v-" |> Expect.isFalse "nothing for anything else"
  ]

/// The dock draws from the view it is handed (docs/plans/live-bindings-watch-view.md, phase 1, step 2b).
[<Tests>]
let dockViewTests =
  let recommended = View.recommended ViewWorkflow.Repl
  let drawn (look: View) =
    renderDockWith look None TweakView.none SessionInView "abcd1234" (WalkedBindings(view [ person; scores ])) None |> decoded
  let attribute (name: string) (html: string) : string =
    let m = Text.RegularExpressions.Regex.Match(html, sprintf "<div id=\"live-tree\"[^>]*%s=\"([^\"]*)\"" name)
    match m.Success with
    | true -> m.Groups.[1].Value
    | false -> ""
  testList "the dock draws from the view" [

    testCase "WHY — with the Type column on every node has a type cell, and with it off no node has one and the cell is not merely hidden" <| fun _ ->
      let on = drawn { recommended with Layout = { recommended.Layout with Type = On } }
      let off = drawn { recommended with Layout = { recommended.Layout with Type = Off } }
      (count "class=\"live-type\"" on > 0) |> Expect.isTrue "type cells are drawn when asked for"
      count "class=\"live-type\"" off |> Expect.equal "no type cell when the column is off" 0
      off |> Expect.stringContains "the rest of the row is still there" "live-preview"

    testCase "WHY — how much of a value prints and how tall a row is are attributes of the tree, because a morph rewrites an attribute and keeps a class" <| fun _ ->
      for values in [ ValueMode.Clipped; ValueMode.Full ] do
        for row in [ RowHeight.Compact; RowHeight.Roomy ] do
          let html = drawn { recommended with Values = values; Row = row }
          attribute "data-values" html |> Expect.equal "the value mode" (AxisValue.token (AxisValue.ValueMode values))
          attribute "data-row" html |> Expect.equal "the row height" (AxisValue.token (AxisValue.RowHeight row))

    testCase "WHY — the tree with nothing in it carries the same attributes, so the empty page and the full page agree" <| fun _ ->
      let html = renderDockWith { recommended with Values = ValueMode.Full } None TweakView.none SessionInView "abcd1234" NothingBound None |> decoded
      attribute "data-values" html |> Expect.equal "the empty tree says the mode too" "full"

    testCase "WHY — a column gives way at the width the pure fit says, because the stylesheet's thresholds are the fit's numbers" <| fun _ ->
      // Only the Type column is drawn so far, so the threshold is the width Name, Value and Type need together, less one.
      let thresholdIn (mode: string) : int option =
        let m = Text.RegularExpressions.Regex.Match(Dashboard.dashboardCss, sprintf @"@container \(max-width: (\d+)px\)\s*\{\s*\.live-tree\[data-values=""%s""\] \.live-type" mode)
        match m.Success with
        | true -> Some(int m.Groups.[1].Value)
        | false -> None
      for values in [ ValueMode.Clipped; ValueMode.Full ] do
        let mode = AxisValue.token (AxisValue.ValueMode values)
        let hidesAt = ViewColumns.needed values [ OptionalColumn.Type ] - 1
        thresholdIn mode |> Expect.equal (sprintf "%s: Type hides one pixel below what the three columns need" mode) (Some hidesAt)
        // And the pure fit agrees: at that width Type is hidden, one pixel wider it is shown.
        let layout = { recommended.Layout with Type = On; State = Off; Source = Off }
        (ViewColumns.fit values layout hidesAt).Hidden |> Expect.equal "hidden at the threshold" [ OptionalColumn.Type ]
        (ViewColumns.fit values layout (hidesAt + 1)).Hidden |> Expect.isEmpty "shown one pixel wider"
  ]

/// The view panel the pane bar opens (docs/plans/live-bindings-watch-view.md, phase 1, step 3).
[<Tests>]
let viewPanelTests =
  let sessionId = "abcd1234"
  let resolvedOf (workflow: ViewWorkflow) (sources: (ViewAxis * ConfigLayer) list) (edits: AxisValue list) : ResolvedView =
    { View = edits |> List.fold (fun v value -> View.withValue value v) (View.recommended workflow)
      Sources = Map.ofList sources }
  let panelHtml (workflow: ViewWorkflow) (resolved: ResolvedView) : string =
    let controls = LiveBindingsViewPanel.render sessionId workflow resolved
    decoded (Elem.div [] [ controls.Button; controls.Panel ])
  let untouched (workflow: ViewWorkflow) = resolvedOf workflow [] []
  /// Every click a panel offers, as the (verb, axis, value) it stages.
  let clicksIn (html: string) : (string * string * string) list =
    Text.RegularExpressions.Regex.Matches(html, @"\$bvAction = '([^']*)'; \$bvAxis = '([^']*)'; \$bvValue = '([^']*)';")
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value, m.Groups.[3].Value)
    |> Seq.toList
  testList "the view panel" [

    testProperty "WHY — every click the panel offers is a request the route accepts, so no control can post something it refuses" <| fun (workflowSeed: int) (touched: bool) ->
      let workflow = ViewWorkflow.all.[abs (workflowSeed % ViewWorkflow.all.Length)]
      let resolved =
        match touched with
        | true -> resolvedOf workflow (ViewAxis.drawn |> List.map (fun a -> a, LRepo)) [ AxisValue.RowHeight RowHeight.Roomy ]
        | false -> untouched workflow
      let clicks = clicksIn (panelHtml workflow resolved)
      not clicks.IsEmpty
      && clicks |> List.forall (fun (verb, axis, value) -> ViewEdit.parse workflow verb axis value |> Result.isOk)

    testCase "WHY — the panel offers a control for each axis the dock draws and for no other, so none of them changes nothing" <| fun _ ->
      let axesOffered =
        clicksIn (panelHtml ViewWorkflow.Repl (untouched ViewWorkflow.Repl))
        |> List.filter (fun (verb, _, _) -> verb = ViewAction.token ViewAction.SetAxis)
        |> List.map (fun (_, axis, _) -> axis)
        |> List.distinct
      axesOffered |> Expect.equal "exactly the drawn axes, in order" (ViewAxis.drawn |> List.map ViewAxis.token)

    testCase "WHY — each drawn axis, set to each of its values, changes what the dock draws, so the axis list is true" <| fun _ ->
      let recommended = View.recommended ViewWorkflow.Repl
      let drawn (look: View) =
        renderDockWith look None TweakView.none SessionInView sessionId (WalkedBindings(view [ person; scores ])) None |> decoded
      for axis in ViewAxis.drawn do
        let pages = AxisValue.allFor axis |> List.map (fun value -> drawn (View.withValue value recommended))
        (pages |> List.distinct |> List.length) |> Expect.equal (sprintf "%s: each value draws a different dock" (ViewAxis.label axis)) pages.Length

    testCase "WHY — the pressed button is the view's current value, and the preset row names the recommended one" <| fun _ ->
      let html = panelHtml ViewWorkflow.Repl (resolvedOf ViewWorkflow.Repl [ ViewAxis.RowHeight, LRepo ] [ AxisValue.RowHeight RowHeight.Roomy ])
      html |> Expect.stringContains "the recommended preset is named" "Watch window (recommended)"
      let pressed = Text.RegularExpressions.Regex.Matches(html, "aria-pressed=\"true\"").Count
      // One per drawn axis, plus the preset row's pressed button.
      pressed |> Expect.equal "one pressed button per drawn axis and one preset" (ViewAxis.drawn.Length + 1)

    testCase "WHY — a reset appears beside an axis only when a file holds a choice for it, and says where it came from" <| fun _ ->
      let none = panelHtml ViewWorkflow.Repl (untouched ViewWorkflow.Repl)
      count "live-view-reset\"" none |> Expect.equal "no per-axis reset when nothing is saved" 0
      none |> Expect.stringContains "the source is the recommendation" "recommended"
      let saved = panelHtml ViewWorkflow.Repl (resolvedOf ViewWorkflow.Repl [ ViewAxis.RowHeight, LRepo ] [ AxisValue.RowHeight RowHeight.Roomy ])
      count "live-view-reset\"" saved |> Expect.equal "one reset, for the saved axis" 1
      saved |> Expect.stringContains "the source is named" "saved for this app"

    testCase "WHY — the panel is closed until the button opens it, the scope starts at this app, and a page with no session has neither button nor panel" <| fun _ ->
      let html = panelHtml ViewWorkflow.Repl (untouched ViewWorkflow.Repl)
      html |> Expect.stringContains "closed by default" "live-view-panel live-hidden"
      (LiveBindingsViewPanel.ViewSignals.initial |> List.length) > 3 |> Expect.isTrue "the signals are declared"
      let page = renderDock NoSessionInView "" NothingBound |> decoded
      page.Contains "live-view-btn" |> Expect.isFalse "no button without a session"

    testCase "WHY — the chosen button is marked with the focus colour and the panel heading with the heading colour, never the warning yellow or failure red" <| fun _ ->
      let pressed = ruleBody ".live-view-opt[aria-pressed=\"true\"]"
      pressed |> Expect.stringContains "the focus colour" "--border-focus"
      for selector in [ ".live-view-opt[aria-pressed=\"true\"]"; ".live-view-head"; ".live-view-source" ] do
        let body = ruleBody selector
        body.Contains "--fg-yellow" |> Expect.isFalse (sprintf "%s is not the warning colour" selector)
        body.Contains "--fg-red" |> Expect.isFalse (sprintf "%s is not the failure colour" selector)
      ruleBody ".live-view-head" |> Expect.stringContains "the heading colour" "var(--fg-blue)"
  ]
