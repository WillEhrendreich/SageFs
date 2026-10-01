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

let private panel (v: PaneView) = DashboardFragments.renderLiveBindingsPanel "abcd1234" (Some v) |> decoded

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

    testCase "a label that is markup is shown as text" <| fun _ ->
      let html = DashboardFragments.renderLiveBindingsPanel "abcd1234" (Some(view WalkSafe NoClickYet [ box [ held "<script>alert(1)</script>" NotEvaluatedReason.GetterRunsCode ] ]))
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
      let ran = ClickAnswered(MemberShown(box [], ContainedBy SandboxPolicy.NoNetworkNoWritesNoSpawn))
      panel (view WalkSafe ran [ box [] ]) |> Expect.stringContains "contained" "ran under a syscall filter"
      let uncontained = ClickAnswered(MemberShown(box [], NotContained NotLinux))
      panel (view WalkSafe uncontained [ box [] ]) |> Expect.stringContains "not contained, with why" "no I/O containment here: "
      (panel (view WalkSafe NoClickYet [ box [] ])).Contains "syscall filter" |> Expect.isFalse "nothing before a click"

    testCase "the controls wrap: the header and the held rows are flex-wrap, so no width makes them overflow or overlap" <| fun _ ->
      let html = panel (view WalkSafe NoClickYet [ box [ held "Slow" NotEvaluatedReason.GetterRunsCode ] ])
      html |> Expect.stringContains "a wrapping header" "live-pane-head"
      html |> Expect.stringContains "a wrapping row" "live-held-row"

    testCase "a session with no snapshot yet still renders the panel, with no controls to mislead" <| fun _ ->
      let html = DashboardFragments.renderLiveBindingsPanel "abcd1234" None |> decoded
      html |> Expect.stringContains "the panel keeps its id" DomIds.BindingsPanel
      html.Contains "live-values/mode" |> Expect.isFalse "no mode control with no values"
  ]
