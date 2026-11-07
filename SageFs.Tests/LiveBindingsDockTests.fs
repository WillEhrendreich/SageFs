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
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak
open SageFs.Features.Tweak.BindingTweakRows
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DockPanes
open SageFs.Server.LiveBindingsDock
open SageFs.Server.LiveBindingsTweakView
open SageFs.Tests.SharedGenerators

let private decoded (node: XmlNode) = Net.WebUtility.HtmlDecode(renderNode node)

let private node label kind (preview: string) children : LiveValueNode =
  { Label = label
    TypeName = "T"
    Preview = preview
    Kind = kind
    Children = children
    BestEffort = false
    Assign = Assignment.NotKnown
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
          Redo = HistoryStep.NothingToStep
          Through = ThroughCall.NoCall; Limits = SageFs.Features.Tweak.NumberLimits.unlimited; Storage = Storage.NotKnown; Push = PushOffer.NotOffered; Via = PushVia.RebindBinding }
      let tweaks : TweakView = { Rows = Map.ofList [ RowKey.top "person", replOnly; RowKey.top "scores", replOnly ] }
      let html = renderDockWith (SageFs.View.recommended SageFs.ViewWorkflow.Repl) None tweaks SessionInView "abcd1234" (WalkedBindings(view [ person; scores ])) None |> decoded
      html |> Expect.stringContains "the count, in the bar, in words that say what it counts" (PaneNote.text (PaneNote.NotInAnyFile 2))
      html |> Expect.stringContains "the chip's own test id" "data-testid=\"live-repl-only\""
      html |> Expect.stringContains "the reason on hover" "nothing to save it to"
      count "Bound in the REPL" html |> Expect.equal "the sentence appears once, in the chip, and not on each row" 1
      (dock (WalkedBindings(view [ person; scores ]))).Contains "live-repl-only" |> Expect.isFalse "no chip when the files were not asked about"

    testCase "WHY — the chip says what it counts: bindings no file defines, and never the word REPL a new user has not met" <| fun _ ->
      for count in [ 1; 2; 30 ] do
        let said = PaneNote.text (PaneNote.NotInAnyFile count)
        said |> Expect.stringContains "the number" (string count)
        said.Contains "REPL" |> Expect.isFalse "no jargon on the chip"
        said |> Expect.stringContains "what it counts" "file"

    testCase "the filter is a typed signal binding, and each binding hides itself by name through a typed class toggle" <| fun _ ->
      let html = dock (WalkedBindings(view [ person; scores ]))
      html |> Expect.stringContains "the filter box is bound (Datastar writes the camelCase signal in kebab case)" "data-bind:live-filter"
      html |> Expect.stringContains "a binding hides when the filter does not match" "data-class:live-hidden"
      html |> Expect.stringContains "matching is by lowercase name" "'person'.includes("
      html |> Expect.stringContains "a notice when nothing matches, with a way out" (PaneNote.text PaneNote.NoNameMatchesFilter)

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
      html |> Expect.stringContains "the note, with the limits and what to do" (PaneNote.text PaneNote.ValuesCutShort)

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

/// A binding the files hold as a literal, as the pane would know it: the row view the service builds for `gravity`.
let private gravityTweaks : TweakView =
  let place : SourceRef =
    { File = "/work/game/Tuning.fs"
      Address = SageFs.Tests.BindingTweakTests.addressOf "gravity" []
      Text = "9.8"
      Hash = String('a', 64)
      Span = { Line = 3; Column = 14; EndLine = 3; EndColumn = 17 }
      Kind = ItemKind.Knob(SageFs.Features.Tweak.LiteralEdit.LiteralValue.Real 9.8) }
  let row : RowView =
    { State = PersistenceState.InSource place
      Control = Control.NoControl
      Place = PlaceOf.Placed place
      Watching = WatchStatus.Watched
      Reload = RowReload.NoWriteYet
      Undo = HistoryStep.NothingToStep
      Redo = HistoryStep.NothingToStep
      Through = ThroughCall.NoCall; Limits = SageFs.Features.Tweak.NumberLimits.unlimited
      Storage = Storage.NotKnown; Push = PushOffer.NotOffered; Via = PushVia.RebindBinding }
  { Rows = Map.ofList [ RowKey.top "gravity", row ] }

/// The same binding with a value tried in the session and not saved: the pane has a tweak to count, so the changes strip has something to say.
let private gravityTweaked : TweakView =
  match Map.tryFind (RowKey.top "gravity") gravityTweaks.Rows with
  | Some row ->
    match row.Place with
    | PlaceOf.Placed place -> { Rows = Map.ofList [ RowKey.top "gravity", { row with State = PersistenceState.Tweaked("9.5", place) } ] }
    | PlaceOf.NotPlaced -> gravityTweaks
  | None -> gravityTweaks

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

    testCase "WHY — the stylesheet holds the generated container rules verbatim, because the thresholds are the pure fit's numbers and a hand edit would drift from them" <| fun _ ->
      Dashboard.dashboardCss |> Expect.stringContains "the generated block, between its markers" columnCss
      // The pure fit agrees with a threshold: at the width Name, Value and Type need, less one, Type is hidden; one pixel wider it is shown.
      for values in [ ValueMode.Clipped; ValueMode.Full ] do
        let hidesAt = ViewColumns.needed values [ OptionalColumn.Type ] - 1
        let layout = { recommended.Layout with Type = On; State = Off; Source = Off }
        (ViewColumns.fit values layout hidesAt).Hidden |> Expect.equal "hidden at the threshold" [ OptionalColumn.Type ]
        (ViewColumns.fit values layout (hidesAt + 1)).Hidden |> Expect.isEmpty "shown one pixel wider"

    testCase "WHY — the tree names the columns it was asked for, so the stylesheet can pick the rules that hide them in the right order" <| fun _ ->
      let tokenOf (layout: Layout) = attribute "data-cols" (drawn { recommended with Layout = layout })
      tokenOf { recommended.Layout with Type = On; State = Off; Source = Off } |> Expect.equal "type alone" "type"
      tokenOf { recommended.Layout with Type = On; State = On; Source = On } |> Expect.equal "all three" "type source state"
      tokenOf { recommended.Layout with Type = Off; State = Off; Source = Off } |> Expect.equal "none" ""

    testCase "WHY — with the State column on, the row says its state in its own line, and with it off the row has no state cell and the strip alone says it" <| fun _ ->
      let leaf = binding "gravity" NodeKind.Leaf []
      let withLayout (layout: Layout) =
        renderDockWith { recommended with Layout = layout } None gravityTweaks SessionInView "abcd1234" (WalkedBindings(view [ leaf ])) None |> decoded
      let rowOf (html: string) : string =
        // The row is the one flex line; its cells are spans, so the first closing div ends it.
        let start = html.IndexOf("class=\"live-binding-node\"", StringComparison.Ordinal)
        html.Substring(start, html.IndexOf("</div>", start, StringComparison.Ordinal) - start)
      let on = withLayout { recommended.Layout with State = On; Source = Off }
      let off = withLayout { recommended.Layout with State = Off; Source = Off }
      rowOf on |> Expect.stringContains "the cell is in the row" "live-cell-state"
      rowOf on |> Expect.stringContains "and says the state's word" (LiveBindingsTweakView.tagLabel StateTag.InSource)
      (rowOf off).Contains "live-cell-state" |> Expect.isFalse "no cell when the column is off"
      off |> Expect.stringContains "the strip still says it" "live-state"
      on |> Expect.stringContains "the strip says it too, for a pane too narrow for the column" "live-state"

    testCase "WHY — with the Source column on, the row names its file and line, and the cell is a column of its own and not the strip's chip" <| fun _ ->
      let leaf = binding "gravity" NodeKind.Leaf []
      let html (source: Toggle) =
        renderDockWith { recommended with Layout = { recommended.Layout with State = Off; Source = source } } None gravityTweaks SessionInView "abcd1234" (WalkedBindings(view [ leaf ])) None |> decoded
      let start = (html On).IndexOf("class=\"live-binding-node\"", StringComparison.Ordinal)
      let row = (html On).Substring(start, (html On).IndexOf("</div>", start, StringComparison.Ordinal) - start)
      row |> Expect.stringContains "the file and line are in the row" "Tuning.fs:3"
      row |> Expect.stringContains "as its own cell" "live-cell-source"
      (html Off).Contains "live-cell-source" |> Expect.isFalse "no cell when the column is off"

    testCase "WHY — a row the pane has nothing to say about draws no state or source cell, so a column is never an empty box" <| fun _ ->
      let html =
        renderDockWith { recommended with Layout = { recommended.Layout with State = On; Source = On } } None TweakView.none SessionInView "abcd1234" (WalkedBindings(view [ person; scores ])) None |> decoded
      html.Contains "live-cell-state" |> Expect.isFalse "no state cell without a row view"
      html.Contains "live-cell-source" |> Expect.isFalse "no source cell without a row view"
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
      && clicks |> List.forall (fun (verb, axis, value) -> ViewEdit.parse workflow { Verb = verb; Setting = axis; Choice = value } |> Result.isOk)

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
        renderDockWith look None gravityTweaked SessionInView sessionId (WalkedBindings(view [ person; scores ])) None |> decoded
      for axis in ViewAxis.drawn do
        let pages = AxisValue.allFor axis |> List.map (fun value -> drawn (View.withValue value recommended))
        (pages |> List.distinct |> List.length) |> Expect.equal (sprintf "%s: each value draws a different dock" (ViewAxis.label axis)) pages.Length

    testCase "WHY — the pressed button is the view's current value, and one button puts everything back to the recommended view" <| fun _ ->
      let html = panelHtml ViewWorkflow.Repl (resolvedOf ViewWorkflow.Repl [ ViewAxis.RowHeight, LRepo ] [ AxisValue.RowHeight RowHeight.Roomy ])
      html |> Expect.stringContains "the way back to the recommended view is offered" "Reset to recommended"
      let pressed = Text.RegularExpressions.Regex.Matches(html, "aria-pressed=\"true\"").Count
      // One per drawn axis.
      pressed |> Expect.equal "one pressed button per drawn axis" ViewAxis.drawn.Length

    testCase "WHY — the panel's visible words are plain: none of 'column', 'workflow', 'fact' or 'pane', which three reviewers could not decode" <| fun _ ->
      // What a person reads is the text outside the tags. The words are the code's nouns for a thing the person sees as a
      // table column, a kind of work, or a part of the window, and "fact" is a developer's word for a datum.
      let saved = resolvedOf ViewWorkflow.Repl [ ViewAxis.RowHeight, LRepo ] [ AxisValue.RowHeight RowHeight.Roomy ]
      for html in [ panelHtml ViewWorkflow.Repl (untouched ViewWorkflow.Repl); panelHtml ViewWorkflow.Repl saved ] do
        let visible = Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ").ToLowerInvariant()
        for word in [ "column"; "workflow"; "fact"; "pane" ] do
          Text.RegularExpressions.Regex.IsMatch(visible, sprintf @"\b%s\b" word)
          |> Expect.isFalse (sprintf "the visible text never says '%s': %s" word visible)

    testCase "WHY — a reset appears beside an axis only when a file holds a choice for it, and says where it came from" <| fun _ ->
      let none = panelHtml ViewWorkflow.Repl (untouched ViewWorkflow.Repl)
      count "live-view-reset\"" none |> Expect.equal "no per-axis reset when nothing is saved" 0
      // A default says nothing beside its axis: the panel's one reset names the recommended view once. The word repeated beside
      // every pair read as noise to three reviewers.
      count "live-view-source\"" none |> Expect.equal "no source word beside an axis nobody saved" 0
      none |> Expect.stringContains "the panel's one reset names the recommendation once" "Reset to recommended"
      let saved = panelHtml ViewWorkflow.Repl (resolvedOf ViewWorkflow.Repl [ ViewAxis.RowHeight, LRepo ] [ AxisValue.RowHeight RowHeight.Roomy ])
      count "live-view-reset\"" saved |> Expect.equal "one reset, for the saved axis" 1
      saved |> Expect.stringContains "the source is named" "saved for this project"

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

    testCase "WHY — the view panel is a popover under the View button, and its position rule outranks the tooltip rule that makes every aria-label element relative" <| fun _ ->
      // `[aria-label]:not(input):not(textarea) { position: relative }` (specificity 0,1,2) sets position on the panel, which has an
      // aria-label. `.live-view-panel` alone (0,1,0) lost to it and the panel sat inside the dock, hiding the bindings. The rule
      // with the dock in its selector (0,2,0) wins.
      ruleBody ".live-dock .live-view-panel" |> Expect.stringContains "absolute, so it leaves the dock's flow" "position: absolute"
      // It sits under the pane's bar, beside the button that opened it, and no longer above the dock over Output, where it left Output
      // one line tall and far from its button.
      ruleBody ".live-view-panel" |> Expect.stringContains "anchored under the bar, at the button" "top: 2.9rem"
      (ruleBody ".live-view-panel").Contains "translateY(-100%)" |> Expect.isFalse "and not lifted above the dock over Output"

    testCase "WHY — every control in the dock shows where keyboard focus is, because the step buttons and the slider drew no ring at all" <| fun _ ->
      // Measured in a real browser: `outline-style` was none on `.live-step` and `.live-slider` when focused by keyboard, so a person
      // tabbing through a row could not see which control they were on. One rule covers the dock, in the focus colour every chosen
      // control in the pane already uses.
      let body = ruleBody ".live-dock :is(button, input, select, summary):focus-visible"
      body |> Expect.stringContains "a solid outline" "outline: 2px solid var(--border-focus)"
      body |> Expect.stringContains "that stands off the control" "outline-offset"

    testCase "WHY — the dock's default ceiling has one home: the stylesheet starts at the same length the page's signal and the double-click reset use" <| fun _ ->
      // The body's style attribute writes `--dock-max-height` from the DockHeight signal, so a stylesheet default that differs
      // from the signal's initial value is never seen: the first change to the CSS did nothing until this was held.
      Dashboard.dashboardCss
      |> Expect.stringContains "the stylesheet's default is the signal's default" (sprintf "--dock-max-height: %s;" Signals.DockHeightDefault)

    testCase "WHY — no text in the live bindings pane is set below 11px, because the state chip at 0.6rem was 8.4px and three reviewers could not read it" <| fun _ ->
      // The root font size is 14px, so a `rem` size is 14 times its number. 11px is the smallest size the pane's chips and
      // captions use; anything under it is the unreadable text the reviewers named.
      let floorPx = 11.0
      let rootPx = 14.0
      let rules =
        Text.RegularExpressions.Regex.Matches(Dashboard.dashboardCss, @"(?m)^\s*(\.live-[^{}]*)\{([^}]*)\}")
        |> Seq.map (fun m -> m.Groups.[1].Value.Trim(), m.Groups.[2].Value)
        |> List.ofSeq
      (rules.Length > 20) |> Expect.isTrue "the stylesheet's live-* rules were found (the pattern did not silently match nothing)"
      let tooSmall =
        rules
        |> List.collect (fun (selector, body) ->
          Text.RegularExpressions.Regex.Matches(body, @"font-size:\s*([0-9.]+)(px|rem)")
          |> Seq.map (fun m ->
            let n = float m.Groups.[1].Value
            let px = match m.Groups.[2].Value with | "rem" -> n * rootPx | _ -> n
            selector, px)
          |> List.ofSeq)
        |> List.filter (fun (_, px) -> px < floorPx)
      tooSmall |> Expect.isEmpty (sprintf "every live-* font size is at least %gpx, but these are smaller: %A" floorPx tooSmall)

    testCase "WHY — the page has one corner language, square, so no label, chip or button is a pill beside square controls" <| fun _ ->
      // A filled state label and the agent chips were first drawn with `border-radius: 999px` beside buttons with none, and it looked
      // wrong at once. Nothing is rounded: the switch's track and thumb, the settings chips and the select boxes are square too.
      let allowed : Set<string> = Set.empty
      let rules =
        Text.RegularExpressions.Regex.Matches(Dashboard.dashboardCss, @"(?m)^([^\s{}/*@][^{}]*)\{([^}]*)\}")
        |> Seq.map (fun m -> m.Groups.[1].Value.Trim(), m.Groups.[2].Value)
        |> List.ofSeq
      (rules.Length > 100) |> Expect.isTrue "the stylesheet's rules were found (the pattern did not silently match nothing)"
      let rounded =
        rules
        |> List.filter (fun (selector, body) ->
          Text.RegularExpressions.Regex.IsMatch(body, @"border-radius:(?>[ \t]*)(?!0[ \t]*[;}])[^;]+;") && not (allowed.Contains selector))
        |> List.map fst
      rounded |> Expect.isEmpty (sprintf "no rule rounds a corner, but these do: %A" rounded)
  ]

/// What the pane says about itself and its rows in words a person can read cold (three reviewers who saw only screenshots scored the
/// wording 4 of 10). The sentences are chosen by DU cases, so these tests check the data and the property that no sentence leaks a raw
/// case name, not the exact words.
[<Tests>]
let dockWordingTests =
  let withKnob : TweakView =
    let place : SourceRef =
      { File = "/work/Tuning.fs"
        Address = { ModulePath = []; BindingName = "person"; Path = [] }
        Text = "36"
        Hash = String('a', 64)
        Span = { Line = 3; Column = 1; EndLine = 3; EndColumn = 3 }
        Kind = ItemKind.Knob(LiteralValue.Integer 36L) }
    { Rows =
        Map.ofList
          [ RowKey.top "person",
            { State = PersistenceState.InSource place
              Control = Control.IntegerStepper 36L
              Place = PlaceOf.Placed place
              Watching = WatchStatus.Watched
              Reload = RowReload.NoWriteYet
              Undo = HistoryStep.NothingToStep
              Redo = HistoryStep.NothingToStep
              Through = ThroughCall.NoCall; Limits = SageFs.Features.Tweak.NumberLimits.unlimited; Storage = Storage.NotKnown; Push = PushOffer.NotOffered; Via = PushVia.RebindBinding } ] }
  let allNotes : PaneNote list =
    [ PaneNote.NoNameMatchesFilter
      PaneNote.ValuesCutShort
      PaneNote.ValuesAsOf "14:03:22 UTC"
      PaneNote.LegendSummary
      PaneNote.ReadFromPrinted
      PaneNote.ChangesGoToTheFile
      PaneNote.ChangesGoToAnUnwatchedFile
      PaneNote.PendingChange [ "gravity" ]
      PaneNote.PendingChange [ "gravity"; "maxHealth" ]
      PaneNote.ModeGoverns
      PaneNote.ModeMeans WalkSafe
      PaneNote.ModeMeans WalkEverything
      PaneNote.ModeMeans WalkOff
      PaneNote.NotInAnyFile 3 ]
  let dockWith (tweaks: TweakView) (mode: ValueWalk) (typeColumn: Toggle) (bindings: LiveBindingValue list) : string =
    let recommended = SageFs.View.recommended SageFs.ViewWorkflow.Repl
    let look = { recommended with Layout = { recommended.Layout with Type = typeColumn } }
    let v = view bindings
    renderDockWith look None tweaks SessionInView "abcd1234" (WalkedBindings { v with Notes = { v.Notes with Mode = mode } }) None |> decoded
  /// The same dock drawn with the Display panel, which holds the mode buttons and the legend (`LiveBindingsDock.modeRows`).
  let dockWithPanel (tweaks: TweakView) (mode: ValueWalk) (bindings: LiveBindingValue list) : string =
    let recommended = SageFs.View.recommended SageFs.ViewWorkflow.Repl
    let controls = LiveBindingsViewPanel.render "abcd1234" SageFs.ViewWorkflow.Repl { View = recommended; Sources = Map.empty }
    let v = view bindings
    renderDockWith recommended (Some controls) tweaks SessionInView "abcd1234" (WalkedBindings { v with Notes = { v.Notes with Mode = mode } }) None |> decoded

  testList "the pane says what its controls govern and where a change goes, before anyone has to find out" [

    testCase "WHY — the pane's one line under its title says where an edit goes, and a list with an editable row adds no standing caption of its own to the header" <| fun _ ->
      let editable = dockWith withKnob ValueWalk.standard On [ person ]
      editable |> Expect.stringContains "the line under the title is there, under its own test id" "data-testid=\"live-pane-sub\""
      editable |> Expect.stringContains "it says the file changes now" "Edit one: the file changes now"
      editable |> Expect.stringContains "and that the row says how the session catches up" "the row says how the session catches up"
      editable.Contains "live-writes-caption" |> Expect.isFalse "no change is pending, so the header has no caption"
      editable.Contains "live-writes-banner" |> Expect.isFalse "and no banner"
      editable.Contains (PaneNote.text PaneNote.ChangesGoToTheFile) |> Expect.isFalse "the standing sentence is not drawn"
      (dockWith TweakView.none ValueWalk.standard On [ person ]) |> Expect.stringContains "the line under the title is there with no editable row too" "data-testid=\"live-pane-sub\""
      TweakView.hasKnob withKnob |> Expect.isTrue "the pure fact: this list has an editable row"
      TweakView.hasKnob TweakView.none |> Expect.isFalse "no rows, no knob"

    testCase "WHY — the line under the title has a one-line form for a phone, because at 400px the full sentence took three lines of the first screen before the first binding" <| fun _ ->
      let html = dockWith withKnob ValueWalk.standard On [ person ]
      html |> Expect.stringContains "the short form says where an edit goes" "class=\"pane-sub-short\">Edit a value: the file changes now."
      html |> Expect.stringContains "the long form is still there for a wide pane" "class=\"pane-sub-long\">The values in this session."

    testCase "WHY — when nothing watches the files the rows are in and nothing is pending, the header says nothing about it, and a row nobody has written says nothing: the sentence under every row was noise" <| fun _ ->
      // Seen in a real browser once hot reload was left off (the usual state in a REPL session): after one nudge every row in the file
      // said "Nothing is watching Tuning.fs, so a change saved to it will not reach the running session", ten rows of the same sentence.
      // The line under the pane's title carries the one general rule, and a change that is saved and not live gets the banner.
      let unwatched : TweakView = { Rows = withKnob.Rows |> Map.map (fun _ row -> { row with Watching = WatchStatus.NotWatched }) }
      let said = PaneNote.text PaneNote.ChangesGoToAnUnwatchedFile
      let html = dockWith unwatched ValueWalk.standard On [ person ]
      html.Contains said |> Expect.isFalse "no standing caption names the consequence"
      html.Contains "live-writes-caption" |> Expect.isFalse "the header draws no caption while nothing is pending"
      html |> Expect.stringContains "the line under the title is still there" "data-testid=\"live-pane-sub\""
      html.Contains "tweak-watching" |> Expect.isFalse "and no unwritten row repeats the sentence"
      TweakView.anyUnwatched unwatched |> Expect.isTrue "the pure fact: nothing watches these files"
      TweakView.anyUnwatched withKnob |> Expect.isFalse "a watched one has none"

    testCase "WHY — while a change is saved and not live, the banner is the one place that names it and both remedies, with the buttons beside the sentence: the same fact was said four times" <| fun _ ->
      let pending : TweakView =
        { Rows =
            withKnob.Rows
            |> Map.map (fun _ row ->
              match row.Place with
              | PlaceOf.Placed place -> { row with Watching = WatchStatus.NotWatched; State = PersistenceState.DiffersFromFile("35", place) }
              | PlaceOf.NotPlaced -> row) }
      let html = dockWith pending ValueWalk.standard On [ person ]
      let said = PaneNote.text (PaneNote.PendingChange [ "person" ])
      html |> Expect.stringContains "the banner is drawn, under its own test id" "data-testid=\"live-writes-banner\""
      html |> Expect.stringContains "its caption names the changed binding and what to do" said
      said |> Expect.stringContains "it says the change is saved but not live" "saved but not live"
      said |> Expect.stringContains "it names the first remedy" "Apply to this session"
      said |> Expect.stringContains "and the second, with what it covers" "Rebuild and reload makes every changed file live"
      html.Contains (PaneNote.text PaneNote.ChangesGoToAnUnwatchedFile) |> Expect.isFalse "the standing rule is not said beside it"
      LiveBindingsTweakView.pendingChanges pending |> Expect.equal "the names the caption follows" [ "person" ]
      LiveBindingsTweakView.pendingChanges withKnob |> Expect.isEmpty "nothing pending, nothing named"

    testCase "WHY — values come first: the mode buttons live in the Display panel, the standard mode spends no line on its meaning, and a mode that is not the standard says what it does on one line" <| fun _ ->
      for mode in ValueWalk.all do
        let html = dockWithPanel TweakView.none mode [ person ]
        (dockWith TweakView.none mode On [ person ]).Contains DockIds.ModeLabel |> Expect.isFalse "the pane's own header holds no mode buttons"
        html |> Expect.stringContains "the group's label" (PaneNote.text PaneNote.ModeGoverns)
        html |> Expect.stringContains "the group is named by that label" (sprintf "aria-labelledby=\"%s\"" DockIds.ModeLabel)
        html |> Expect.stringContains "the label has the id the group names" (sprintf "id=\"%s\"" DockIds.ModeLabel)
        match ModeCaption.ofMode mode with
        | ModeCaption.NotShown ->
          mode |> Expect.equal "only the standard mode is quiet" ValueWalk.standard
          html.Contains "live-mode-caption" |> Expect.isFalse "no line for the standard mode"
        | ModeCaption.Shown chosen ->
          chosen |> Expect.equal "the caption is for the chosen mode" mode
          html |> Expect.stringContains (sprintf "the meaning of %s, as text" (ValueWalk.name mode)) (PaneNote.text (PaneNote.ModeMeans mode))
          count "data-testid=\"live-mode-caption\"" html |> Expect.equal "exactly one caption" 1
      ValueWalk.all |> List.filter (fun mode -> ModeCaption.ofMode mode = ModeCaption.NotShown) |> Expect.equal "exactly one mode is the standard" [ ValueWalk.standard ]
      ValueWalk.all |> List.map (fun mode -> PaneNote.text (PaneNote.ModeMeans mode)) |> List.distinct |> List.length |> Expect.equal "each mode says its own thing" 3
      PaneNote.text (PaneNote.ModeMeans WalkEverything) |> Expect.stringContains "Everything says it runs getters" "runs your property getters"

    testCase "WHY — the Type column shows F# names and keeps the full .NET name in the tooltip" <| fun _ ->
      let typed = { node "xs" NodeKind.Leaf "[1; 2]" [] with TypeName = "FSharpList`1[Int32]" }
      let root = { Name = "xs"; TypeSignature = "T"; Root = typed }
      let html = dockWith TweakView.none ValueWalk.standard On [ root ]
      html |> Expect.stringContains "the F# spelling is the text" ">int list<"
      html |> Expect.stringContains "the full .NET name is the tooltip" "title=\"FSharpList`1[Int32]\""
      let visible = Text.RegularExpressions.Regex.Matches(html, "class=\"live-type\"[^>]*>([^<]*)<") |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq
      visible |> Expect.isNonEmpty "a type cell was drawn"
      for text in visible do text.Contains "`" |> Expect.isFalse "no backtick in the visible type"

    testCase "the stamp says what it is, with no generation number and no eval jargon: the values as of a time" <| fun _ ->
      let html = dockWith TweakView.none ValueWalk.standard On [ person ]
      html |> Expect.stringContains "the stamp" (PaneNote.text (PaneNote.ValuesAsOf ""))
      html.Contains "gen 7" |> Expect.isFalse "no bare generation number"
      html.Contains "after eval" |> Expect.isFalse "no eval jargon"

    testCase "WHY — the stamp is on the output panel's clock, UTC, and says so, because two times on one screen that differ by the machine's offset read as a bug" <| fun _ ->
      // The output lines beside the pane print the daemon's UTC clock. The stamp used the machine's local time, so a machine at
      // UTC-6 showed 11:17:31 under output lines stamped 17:17:31: the same moment, written two ways.
      let walked = view [ person ]
      let at = DateTimeOffset(2026, 11, 5, 17, 17, 31, TimeSpan.Zero)
      let html = dock (WalkedBindings { walked with Snapshot = { walked.Snapshot with CapturedAt = at } })
      html |> Expect.stringContains "the stamp is the UTC time, labelled" (PaneNote.text (PaneNote.ValuesAsOf "17:17:31 UTC"))

    testCase "WHY — the legend sits in the Display panel, closed until opened, and has one line for every badge and every glyph, generated from the sets they come from" <| fun _ ->
      (dockWith withKnob ValueWalk.standard On [ person ]).Contains "live-legend" |> Expect.isFalse "the pane's own header holds no legend"
      let html = dockWithPanel withKnob ValueWalk.standard [ person ]
      let opening = Text.RegularExpressions.Regex.Match(html, "<details[^>]*id=\"live-legend\"[^>]*>")
      opening.Success |> Expect.isTrue "the legend is a details element"
      opening.Value.Contains " open" |> Expect.isFalse "and it starts closed"
      html |> Expect.stringContains "its summary says what it is for" (PaneNote.text PaneNote.LegendSummary)
      count "data-testid=\"live-legend-tag\"" html |> Expect.equal "a line per badge" StateTag.all.Length
      count "data-testid=\"live-legend-glyph\"" html |> Expect.equal "a line per glyph" GlyphKind.all.Length
      for tag in StateTag.all do
        html |> Expect.stringContains (sprintf "the badge %A is named" tag) (tagLabel tag)
        html |> Expect.stringContains (sprintf "and explained" ) (tagMeaning tag)
      for glyph in GlyphKind.all do
        html |> Expect.stringContains (sprintf "the glyph %A is explained" glyph) (GlyphKind.meaning glyph)

    testCase "WHY — information reads as information: the reach hint is in the dim text colour and not a link's blue, and the count of bindings no file holds is plain text and not a bordered box" <| fun _ ->
      let hint = ruleBody ".live-detail.live-tone-info"
      hint |> Expect.stringContains "the dim text colour" "var(--fg-dim)"
      hint.Contains "--fg-blue" |> Expect.isFalse "not the colour of a link"
      hint.Contains "--fg-yellow" |> Expect.isFalse "not the warning colour"
      ruleBody ".live-binding-node > .live-preview:has(+ .live-number-tag)" |> Expect.stringContains "a value with its word keeps to its own width, so the word sits beside it" "flex: 0 1 auto"
      let chip = ruleBody ".live-repl-chip"
      chip.Contains "border" |> Expect.isFalse "no box around the count"
      chip.Contains "padding" |> Expect.isFalse "and no padding that makes it a button's shape"

    testCase "every glyph a row can wear is one the legend lists, and the closed set names every case of the glyph's own kinds" <| fun _ ->
      GlyphKind.all.Length |> Expect.equal "the list names every case" (Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<GlyphKind>).Length)
      let kinds =
        [ NodeKind.Leaf, "String"; NodeKind.Leaf, "Int32"; NodeKind.Leaf, "Boolean"; NodeKind.NotEvaluated NotEvaluatedReason.GetterRunsCode, "T"
          NodeKind.Record, "T"; NodeKind.List, "T"; NodeKind.Map, "T"; NodeKind.Option, "T"; NodeKind.Union, "T"; NodeKind.Tuple, "T"
          NodeKind.Array, "T"; NodeKind.Class, "T"; NodeKind.Closure, "T"; NodeKind.Cycle, "T"; NodeKind.Truncated, "T" ]
      for kind, typeName in kinds do
        let glyph = GlyphKind.ofNode kind typeName
        List.contains glyph GlyphKind.all |> Expect.isTrue (sprintf "%A is in the legend" kind)
        kindGlyph kind typeName |> Expect.equal "the drawn glyph is the kind's glyph" (GlyphKind.glyph glyph)
      (kinds |> List.map (fun (kind, typeName) -> GlyphKind.ofNode kind typeName) |> List.distinct).Length
      |> Expect.equal "the kinds reach every glyph" GlyphKind.all.Length

    testCase "WHY — while a row differs from its file the heading's value says running and the knob's number says file, so no number is unlabelled" <| fun _ ->
      let place : SourceRef =
        { File = "/work/Tuning.fs"
          Address = { ModulePath = []; BindingName = "gravity"; Path = [] }
          Text = "9.9"
          Hash = String('a', 64)
          Span = { Line = 3; Column = 1; EndLine = 3; EndColumn = 3 }
          Kind = ItemKind.Knob(LiteralValue.Real 9.9) }
      let rowOf state : RowView =
        { State = state
          Control = Control.RealStepper(9.9, Step.Fraction 1)
          Place = PlaceOf.Placed place
          Watching = WatchStatus.Watched
          Reload = RowReload.NoWriteYet
          Undo = HistoryStep.NothingToStep
          Redo = HistoryStep.NothingToStep
          Through = ThroughCall.NoCall; Limits = SageFs.Features.Tweak.NumberLimits.unlimited; Storage = Storage.NotKnown; Push = PushOffer.NotOffered; Via = PushVia.RebindBinding }
      let gravity = binding "gravity" NodeKind.Leaf []
      let differing = dockWith { Rows = Map.ofList [ RowKey.top "gravity", rowOf (PersistenceState.DiffersFromFile("9.8", place)) ] } ValueWalk.standard On [ gravity ]
      differing |> Expect.stringContains "the heading's value says running" "data-testid=\"tweak-running-tag\""
      differing |> Expect.stringContains "the knob's number says file" "data-testid=\"tweak-file-tag\""
      let inStep = dockWith { Rows = Map.ofList [ RowKey.top "gravity", rowOf (PersistenceState.InSource place) ] } ValueWalk.standard On [ gravity ]
      inStep.Contains "tweak-running-tag" |> Expect.isFalse "a row in step has one number"
      inStep.Contains "tweak-file-tag" |> Expect.isFalse "and needs no word for it"

    testCase "WHY — every binding the title bar counts as not in a file wears its badge on its row, so the number and the rows agree" <| fun _ ->
      let replOnly : RowView =
        { State = PersistenceState.NotInAFile NotInFileWhy.NoOwnedFileBindsIt
          Control = Control.NoControl
          Place = PlaceOf.NotPlaced
          Watching = WatchStatus.Watched
          Reload = RowReload.NoWriteYet
          Undo = HistoryStep.NothingToStep
          Redo = HistoryStep.NothingToStep
          Through = ThroughCall.NoCall; Limits = SageFs.Features.Tweak.NumberLimits.unlimited; Storage = Storage.NotKnown; Push = PushOffer.NotOffered; Via = PushVia.RebindBinding }
      let tweaks : TweakView = { Rows = Map.ofList [ RowKey.top "person", replOnly; RowKey.top "scores", replOnly ] }
      let html = dockWith tweaks ValueWalk.standard On [ person; scores ]
      let counted = TweakView.replOnlyCount tweaks [ "person"; "scores" ]
      count "data-testid=\"tweak-row-badge\"" html |> Expect.equal "a badge per counted binding" counted
      html |> Expect.stringContains "the chip's number is that count" (PaneNote.text (PaneNote.NotInAnyFile counted))
      html |> Expect.stringContains "the badge says what it is" (tagLabel StateTag.NotInAFile)

    testCase "WHY — a sentence about a call is said once for the rows it covers, because 'makeRig builds this value' under each of a record's fields was the same line twice" <| fun _ ->
      // Reviewers read the repeat as clutter that "reads as a separate item". Consecutive rows that owe the same sentence say it on the
      // first, and the others are silent; a row with a different sentence says its own.
      let viaCall (callee: string) : RowView =
        { State = PersistenceState.Held
          Control = Control.NoControl
          Place = PlaceOf.NotPlaced
          Watching = WatchStatus.Watched
          Reload = RowReload.NoWriteYet
          Undo = HistoryStep.NothingToStep
          Redo = HistoryStep.NothingToStep
          Through = ThroughCall.CouldNotFollow(Refusal.NoCalleeInFiles callee); Limits = SageFs.Features.Tweak.NumberLimits.unlimited; Storage = Storage.NotKnown; Push = PushOffer.NotOffered; Via = PushVia.RebindBinding }
      let fieldKey (label: string) : RowKey = { Binding = "rig"; Labels = [ label ] }
      let tweaks : TweakView =
        { Rows = Map.ofList [ fieldKey "Mass", viaCall "makeRig"; fieldKey "Drag", viaCall "makeRig"; fieldKey "Spin", viaCall "makeSpin" ] }
      let rig = binding "rig" NodeKind.Record [ node "Mass" NodeKind.Leaf "80.0" []; node "Drag" NodeKind.Leaf "0.25" []; node "Spin" NodeKind.Leaf "1.0" [] ]
      let html = dockWith tweaks ValueWalk.standard On [ rig ]
      count "Built by makeRig," html |> Expect.equal "the shared sentence is said once" 1
      count "Built by makeSpin," html |> Expect.equal "a different sentence is still said" 1

    testCase "WHY — at the standard mode the header spends no line at all: no line of its own for the mode, no standing edit caption, and no mode buttons or legend, which are in the Display panel" <| fun _ ->
      let head (html: string) =
        let start = html.IndexOf "class=\"live-pane-head\""
        html.Substring(start, html.IndexOf("id=\"live-tree\"", start) - start)
      let standardHead = head (dockWith withKnob ValueWalk.standard On [ person ])
      standardHead.Contains "live-containment" |> Expect.isFalse "nothing in the head takes a full line at the standard mode"
      standardHead.Contains "live-writes-caption" |> Expect.isFalse "no change is pending, so no edit caption"
      standardHead.Contains "live-mode-btn" |> Expect.isFalse "the mode buttons are not in the head"
      standardHead.Contains "live-legend" |> Expect.isFalse "and neither is the legend"
      let otherHead = head (dockWith withKnob WalkEverything On [ person ])
      count "live-containment" otherHead |> Expect.equal "a mode that is not the standard takes exactly one more line" 1

    testCase "WHY — a state tag says what is true, in words a person uses: 'in the file', 'expandable', and 'saved but not live' for a file that is ahead of the app" <| fun _ ->
      // Reviewers read "DIFFERS FROM THE FILE" over a sentence that said "Saved to the file" as a contradiction (is the file the same
      // or not?), "IN SOURCE" as unrelated to "NOT IN A FILE", and "MADE OF PARTS" as vague. The pairs now read as pairs.
      LiveBindingsTweakView.tagLabel StateTag.InSource |> Expect.equal "the pair of 'not in a file'" "in the file"
      LiveBindingsTweakView.tagLabel StateTag.MadeOfParts |> Expect.equal "what a record or list is" "expandable"
      LiveBindingsTweakView.tagLabel StateTag.DiffersFromFile |> Expect.equal "what the row says about the app: the file's value has not been applied, and the program is not stopped" "saved but not live"

    testCase "WHY — the label on the mode buttons is plain: it says they govern what is looked at inside an object, not 'class values'" <| fun _ ->
      // Three reviewers: "Class values is not explained, what is a class, what does Safe do". The label names the act.
      PaneNote.text PaneNote.ModeGoverns |> Expect.equal "the label" "Show inside objects (Safe skips properties that could run your code):"

    testCase "WHY — the view panel offers only the settings the list still obeys: state and source always have their own columns above 900px, so a Show state switch set to Off beside a State column on screen read as a bug" <| fun _ ->
      ViewAxis.drawn |> Expect.equal "type, the changes strip, long values and row height" [ ViewAxis.TypeColumn; ViewAxis.ChangesStrip; ViewAxis.ValueMode; ViewAxis.RowHeight ]

    testCase "WHY — a value carries its full text as a title, because the cell clips a long one with an ellipsis and the rest was out of reach" <| fun _ ->
      let long = "{ JumpVelocity = 13.2; CoyoteTime = 0.12; Buffer = 0.08; Apex = 0.4 }"
      let b = { Name = "feel"; TypeSignature = "Feel"; Root = node "feel" NodeKind.Leaf long [] }
      let html = dock (WalkedBindings(view [ b ]))
      let titles = Text.RegularExpressions.Regex.Matches(html, "class=\"live-preview[^\"]*\" title=\"([^\"]*)\"") |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq
      titles |> Expect.contains "the preview's title is the whole value" long

    testCase "WHY — the walked tree is headed by the names of its columns, so a chip or a knob in a column has a label, and the type label follows the Type setting" <| fun _ ->
      // Above 900px the rows share columns (the CSS lays them out), and a column nobody names is a column a reader has to guess.
      // The header is decoration for the eye: the rows carry their own accessible names.
      let on = dock (WalkedBindings(view [ person ]))
      on |> Expect.stringContains "the header is drawn" "live-cols"
      for name in [ "Name · running value"; "State"; "Source"; "Edit (saves to file)" ] do on |> Expect.stringContains (sprintf "the %s column is named" name) name
      on |> Expect.stringContains "the type column is named while it is shown" ">Type<"
      let off = renderDockWith { View.recommended ViewWorkflow.Repl with Layout = { (View.recommended ViewWorkflow.Repl).Layout with Type = Off } } None TweakView.none SessionInView "abcd1234" (WalkedBindings(view [ person ])) None |> decoded
      off.Contains ">Type<" |> Expect.isFalse "and the label goes when the column is off"
      let header = Text.RegularExpressions.Regex.Match(on, "<div[^>]*class=\"live-cols\"[^>]*>").Value
      header |> Expect.stringContains "it is hidden from assistive technology, the rows speak for themselves" "aria-hidden=\"true\""

    testPropertyWithConfig propConfig "WHY — no sentence the pane says about itself carries a raw case name, a backtick or a format hole"
      (Prop.forAll (Arb.fromGen (Gen.elements allNotes)) (fun note ->
        let said = PaneNote.text note
        not (String.IsNullOrWhiteSpace said) && not (said.Contains "`") && not (said.Contains "%") && not (said.Contains "WalkSafe") && not (said.Contains "NoNameMatches")))
  ]
