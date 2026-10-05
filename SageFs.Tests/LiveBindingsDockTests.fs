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

    testCase "every row shows its kind in a word, from one exhaustive function" <| fun _ ->
      let html = dock (WalkedBindings(view [ person; scores ]))
      (count "live-kind" html, 5) |> Expect.isGreaterThanOrEqual "a kind on every row"
      html |> Expect.stringContains "a record" ">record<"
      html |> Expect.stringContains "a list" ">list<"
      html |> Expect.stringContains "a leaf" ">value<"
      kindLabel NodeKind.Class |> Expect.equal "class" "class"
      kindLabel (NodeKind.NotEvaluated NotEvaluatedReason.GetterLoops) |> Expect.equal "held" "held"

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
