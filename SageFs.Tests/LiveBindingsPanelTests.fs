/// The live-bindings pane as the dashboard renders it: a row listed and not read says why and offers a click, the header shows
/// the session's walk mode as a control that reflects the real mode, and a failed click reads as unknown with its reason,
/// never as empty. Every control goes through Datastar's typed helpers, so none of it is a hand-written data-* string.
module SageFs.Tests.LiveBindingsPanelTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Features
open SageFs.Features.LiveBindingsPane
open SageFs.Features.LiveValueTree
open SageFs.FsiHost.FsiProtocol
open SageFs.Server
open SageFs.Server.DashboardTypes

let private decoded (node: XmlNode) = Net.WebUtility.HtmlDecode(renderNode node)

let private node label kind children : LiveValueNode =
  { Label = label
    TypeName = "int"
    Preview = "p"
    Kind = kind
    Children = children
    BestEffort = false
    Depth = 0 }

let private held label reason =
  { node label (NodeKind.NotEvaluated reason) [] with Preview = sprintf "not evaluated: %A" reason }

let private view (mode: ValueWalk) (click: ClickReport) (bindings: LiveBindingValue list) : PaneView =
  { Snapshot =
      { SessionId = "abcd1234"
        Generation = 7L
        Bindings = bindings
        Truncated = false
        CapturedAt = DateTimeOffset.UnixEpoch }
    Notes = { Mode = mode; Click = click } }

let private box (children: LiveValueNode list) : LiveBindingValue =
  { Name = "box"; TypeSignature = "Box"; Root = node "box" NodeKind.Class children }

/// The open pane for a walked view, drawn the way the dock draws it for a session that has these bindings.
let private openPane (v: PaneView) : XmlNode =
  LiveBindingsDock.renderPane "abcd1234" (LiveBindingsDock.WalkedBindings v) (DockPanes.SessionHasBindings v.Snapshot.Bindings.Length) LiveBindingsDock.ShownAlways

let private panel (v: PaneView) = openPane v |> decoded

/// A class binding with one held getter at the end of `path`: a bare label is a getter of the binding, more labels are
/// nested records on the way down to it.
let private heldAt (binding: string) (path: string list) : LiveBindingValue =
  let rec nest (labels: string list) : LiveValueNode list =
    match labels with
    | [] -> []
    | [ last ] -> [ held last NotEvaluatedReason.GetterRunsCode ]
    | step :: rest -> [ node step NodeKind.Record (nest rest) ]
  { Name = binding; TypeSignature = "T"; Root = node binding NodeKind.Class (nest path) }

/// The signal each held row's button is disabled by while its click is out: the row's own "evaluating" state.
let private evaluatingSignals (html: string) : string list =
  Text.RegularExpressions.Regex.Matches(html, "live-held-btn\"[^>]*data-attr:disabled=\"\\$([^\"]+)\"")
  |> Seq.map (fun found -> found.Groups[1].Value)
  |> List.ofSeq

/// Rows that name the same thing in different ways: separators inside a name, a name that is a prefix of another, a
/// different case, a character that is not an identifier character.
let private lookAlikeRows : (string * string list) list =
  [ "a", [ "b_c" ]
    "a_b", [ "c" ]
    "a", [ "b"; "c" ]
    "a_b_c", [ "d" ]
    "A", [ "b_c" ]
    "a", [ "B_c" ]
    "a-b", [ "c" ]
    "a.b", [ "c" ]
    "a", [ "b.c" ] ]

[<Tests>]
let heldRowTests =
  testList "a row listed and not read" [
    testCase "it says why, and a getter that can be run gets one square button that posts the click with its binding and path" <| fun _ ->
      let html = panel (view WalkSafe NoClickYet [ box [ held "Slow" NotEvaluatedReason.GetterRunsCode ] ])
      html |> Expect.stringContains "the reason" "GetterRunsCode"
      html |> Expect.stringContains "stages the binding" "$binding = 'box';"
      html |> Expect.stringContains "stages the path below the binding" "$path = ['Slow'];"
      html |> Expect.stringContains "posts through Datastar to the daemon route" "@post('/api/sessions/abcd1234/live-values/evaluate')"
      html |> Expect.stringContains "the uniform square button" "session-btn"
      html.Contains(" onclick=", StringComparison.Ordinal) |> Expect.isFalse "no raw onclick"

    testCase "a nested getter's path is every label from the binding down to it" <| fun _ ->
      let tree = box [ node "inner" NodeKind.Record [ held "Loops" NotEvaluatedReason.GetterLoops ] ]
      panel (view WalkSafe NoClickYet [ tree ])
      |> Expect.stringContains "the whole path" "$path = ['inner', 'Loops'];"

    testCase "pressing it shows an in-row evaluating state at once, and disables the button while the request is out" <| fun _ ->
      let html = panel (view WalkSafe NoClickYet [ box [ held "Slow" NotEvaluatedReason.GetterRunsCode ] ])
      html |> Expect.stringContains "an indicator signal for this row" "data-indicator"
      html |> Expect.stringContains "the row says it is evaluating" "evaluating"
      html |> Expect.stringContains "the button is disabled by the same signal" "data-attr:disabled"

    testCase "a sequence and a collapsed class say why and offer no click, because a click cannot do anything there" <| fun _ ->
      let html =
        panel (view WalkOff NoClickYet [ box [ held "Items" NotEvaluatedReason.SequenceNotEnumerated; held "Inner" NotEvaluatedReason.ClassesCollapsed ] ])
      html |> Expect.stringContains "sequence reason" "SequenceNotEnumerated"
      html |> Expect.stringContains "collapsed reason" "ClassesCollapsed"
      html.Contains "live-values/evaluate" |> Expect.isFalse "no click was offered"

    testCase "a click that timed out, threw or could not be contained reads as unknown with its reason, never as empty" <| fun _ ->
      let html =
        panel
          (view WalkSafe NoClickYet
            [ box
                [ held "Slow" NotEvaluatedReason.EvaluationTimedOut
                  held "Boom" (NotEvaluatedReason.EvaluationThrew "kaput")
                  held "Wild" (NotEvaluatedReason.EvaluationNotContained "no filter here") ] ])
      html |> Expect.stringContains "says unknown" "unknown"
      html |> Expect.stringContains "the throw's own message" "kaput"
      html |> Expect.stringContains "the containment reason" "no filter here"
      html |> Expect.stringContains "the timeout" "EvaluationTimedOut"

    testCase "every clickable row has its own evaluating mark, so a pane with two of them shows two marks and a click can only turn on its own" <| fun _ ->
      let html = panel (view WalkSafe NoClickYet [ heldAt "probe" [ "RunsCode" ]; heldAt "spinner" [ "Self" ] ])
      let signals = evaluatingSignals html
      signals |> List.length |> Expect.equal "one signal per clickable row" 2
      signals |> List.distinct |> List.length |> Expect.equal "and no two rows share one" 2
      (html.Split([| "live-held-evaluating" |], StringSplitOptions.None).Length - 1)
      |> Expect.equal "one mark per clickable row, hidden until its own click" 2

    testCase "rows whose names look alike never share an evaluating signal, so pressing one cannot show or disable another" <| fun _ ->
      let signalOfRow (binding, path) =
        match evaluatingSignals (panel (view WalkSafe NoClickYet [ heldAt binding path ])) with
        | [ signal ] -> signal
        | other -> failwithf "expected one evaluating signal for %s %A, got %A" binding path other
      let signals = lookAlikeRows |> List.map signalOfRow
      signals
      |> List.distinct
      |> List.length
      |> Expect.equal (sprintf "every look-alike row has a signal of its own, got %A" (List.zip lookAlikeRows signals)) lookAlikeRows.Length

    testCase "a label that is markup is shown as text" <| fun _ ->
      let html = openPane (view WalkSafe NoClickYet [ box [ held "<script>alert(1)</script>" NotEvaluatedReason.GetterRunsCode ] ])
      let raw = renderNode html
      raw.Contains "<script>alert(1)" |> Expect.isFalse "never injected into the page"
  ]

[<Tests>]
let headerTests =
  testList "the pane header" [
    testCase "it shows the session's real mode as the pressed one, and every choice posts through Datastar with its tooltip" <| fun _ ->
      let html = panel (view WalkEverything NoClickYet [ box [] ])
      for choice in ValueWalk.all do
        html |> Expect.stringContains "stages the mode" (sprintf "$mode = '%s';" (ValueWalk.name choice))
        html |> Expect.stringContains "says what it does" (ValueWalk.consequence choice)
      html |> Expect.stringContains "posts to the daemon route" "@post('/api/sessions/abcd1234/live-values/mode')"
      html |> Expect.stringContains "one is pressed" "aria-pressed=\"true\""
      html |> Expect.stringContains "Everything warns that it runs the user's getters" "getters"

    testCase "exactly one choice is pressed, and it is the current one" <| fun _ ->
      for choice in ValueWalk.all do
        let html = panel (view choice NoClickYet [ box [] ])
        (html.Split([| "aria-pressed=\"true\"" |], StringSplitOptions.None).Length - 1)
        |> Expect.equal (sprintf "only %s is pressed" (ValueWalk.name choice)) 1

    testCase "it counts the rows that are listed and not read" <| fun _ ->
      let html =
        panel (view WalkSafe NoClickYet [ box [ held "A" NotEvaluatedReason.GetterRunsCode; held "B" NotEvaluatedReason.GetterLoops ] ])
      html |> Expect.stringContains "the count" "2 not evaluated"

    testCase "with nothing held there is no count to show" <| fun _ ->
      let html = panel (view WalkSafe NoClickYet [ box [ node "ok" NodeKind.Leaf [] ] ])
      html.Contains "not evaluated" |> Expect.isFalse "no count"

    testCase "the last click's containment line is shown, and absent before any click" <| fun _ ->
      let ran = ClickAnswered(MemberShown(box [], ContainedBy SandboxPolicy.NoNetworkNoWritesNoSpawn, { Coverage = GuardCoverage.NotGuarded NotGuardedReason.SwitchedOff; Trip = GuardTrip.NotTripped }))
      panel (view WalkSafe ran [ box [] ]) |> Expect.stringContains "contained" "ran under a syscall filter"
      let uncontained = ClickAnswered(MemberShown(box [], NotContained NotLinux, { Coverage = GuardCoverage.NotGuarded NotGuardedReason.SwitchedOff; Trip = GuardTrip.NotTripped }))
      panel (view WalkSafe uncontained [ box [] ]) |> Expect.stringContains "not contained, with why" "no I/O containment here: "
      (panel (view WalkSafe NoClickYet [ box [] ])).Contains "syscall filter" |> Expect.isFalse "nothing before a click"

    testCase "the controls wrap: the header and the held rows are flex-wrap, so no width makes them overflow or overlap" <| fun _ ->
      let html = panel (view WalkSafe NoClickYet [ box [ held "Slow" NotEvaluatedReason.GetterRunsCode ] ])
      html |> Expect.stringContains "a wrapping header" "live-pane-head"
      html |> Expect.stringContains "a wrapping row" "live-held-row"

    testCase "a session with no snapshot yet still renders the panel, with no controls to mislead" <| fun _ ->
      let html = LiveBindingsDock.renderPane "abcd1234" LiveBindingsDock.NothingBound DockPanes.PinnedByUser LiveBindingsDock.ShownAlways |> decoded
      html |> Expect.stringContains "the panel keeps its id" DomIds.BindingsPanel
      html.Contains "live-values/mode" |> Expect.isFalse "no mode control with no values"
  ]
