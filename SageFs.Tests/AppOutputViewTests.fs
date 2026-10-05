/// The app-output pane's DRAWN answer, case for case against the decision it draws.
///
/// Every test here starts from `PaneState.decide` and asserts what the renderer does with that answer, so a
/// renderer that draws its own idea of the state cannot pass: the decision and the drawing are asserted in
/// one place. The expected strings are the ones the REPL printed for the real renderer, not retyped from
/// memory.
module SageFs.Tests.AppOutputViewTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs.Server.DockPanes
open SageFs.Server.AppOutputPane
open SageFs.Server.AppOutputView

/// A pane with one line on each stream, so the assertion about WHICH stream a line is coloured by has both
/// to be right. stderr is the whole point of the pane, so a renderer that showed them alike fails here.
let private paneWithBothLines =
  AppOutputPane.create
  |> AppOutputPane.feedText OutputStream.Stderr "boom"
  |> AppOutputPane.feedText OutputStream.Stdout "hello"

let private facts presence session =
  { Session = session
    Bindings = NoBindingsYet
    AppOutput = presence
    Pin = Unpinned }

let private draw (pane: AppOutputPane) (presence: AppOutputPresence) (session: SessionInView) =
  PaneState.decide (facts presence session) DockPane.AppOutput
  |> fun state -> render state pane
  |> renderNode

[<Tests>]
let appOutputViewTests =
  testList "the app output pane, as the dock draws it" [

    testCase "WHY — an open pane draws the lines, each coloured by the stream it came from, because a stack trace shown as ordinary output is the failure this pane exists to stop" <| fun _ ->
      let html = draw paneWithBothLines (HasAppOutput 2) SessionInView
      html |> Expect.stringContains "names the pane" "app-output-pane"
      html |> Expect.stringContains "says how many opened it" "2 lines of app output"
      html |> Expect.stringContains "the stderr line carries the stderr class" "app-output-line app-output-stderr"
      html |> Expect.stringContains "and says its stream in the markup" "data-stream=\"stderr\""
      html |> Expect.stringContains "the stdout line carries the stdout class" "app-output-line app-output-stdout"
      html |> Expect.stringContains "stderr text is present" "boom"
      html |> Expect.stringContains "stdout text is present" "hello"

    testCase "WHY — no output collapses to ONE line that says how to make some, rather than an empty box" <| fun _ ->
      let html = draw paneWithBothLines NoAppOutput SessionInView
      html |> Expect.stringContains "collapses to the bar" "app-output-bar"
      html |> Expect.stringContains "it is a status, not a silent box" "role=\"status\""
      html |> Expect.stringContains "says what to do" "run_app"
      // The lines the pane still holds are NOT drawn while it has nothing to show — that would be a
      // contradiction: presence decided "none", so drawing output here would disagree with the decision.
      html.Contains "app-output-lines" |> Expect.isFalse "no line list when there is none"

    testCase "WHY — with no session the pane says so, because the fix is opening a session and not starting an app" <| fun _ ->
      let html = draw AppOutputPane.create NoAppOutput NoSessionInView
      html |> Expect.stringContains "names the missing session" "no session is open"
      html.Contains "run_app" |> Expect.isFalse "does not blame an app that was never started"

    testCase "WHY — a search matching nothing says it, because an empty pane and an unmatched search are different facts with different fixes" <| fun _ ->
      let searching = { paneWithBothLines with Search = "zzzz" }
      let html = draw searching (HasAppOutput 2) SessionInView
      html |> Expect.stringContains "says what it was looking for" "no line matches"
      html |> Expect.stringContains "quotes the search" "zzzz"
      html |> Expect.stringContains "and still reports what it holds" "2 lines, filtered"
      html.Contains "app-output-lines" |> Expect.isFalse "and does not draw the lines it filtered out"

    testCase "the pane's own header says what it is doing, so a still pane is not mistaken for a dead app" <| fun _ ->
      let html = draw paneWithBothLines (HasAppOutput 2) SessionInView
      html |> Expect.stringContains "the header carries the pane's own words" "2 lines"

    testCase "every case the decision can answer has a drawing, and the two agree" <| fun _ ->
      // Both directions of the same fact, as rendered output: something must be drawn either way, and a
      // collapsed answer must not draw the body an open answer draws.
      let openHtml = draw paneWithBothLines (HasAppOutput 1) SessionInView
      let closedHtml = draw paneWithBothLines NoAppOutput SessionInView
      (openHtml <> closedHtml) |> Expect.isTrue "open and collapsed are not the same drawing"
      openHtml |> Expect.stringContains "open draws the body" "app-output-lines"
      closedHtml |> Expect.stringContains "collapsed draws the bar" "app-output-bar"

    // ── the controls, and why they are POSTs ──────────────────────────────────────────────────────

    testCase "WHY — each control posts the setting it changes, because the decision reads the server's buffer and a client-only toggle would disagree with it at the first morph" <| fun _ ->
      let html = draw paneWithBothLines (HasAppOutput 2) SessionInView
      html |> Expect.stringContains "follow" "/dashboard/app-output/follow"
      html |> Expect.stringContains "pause" "/dashboard/app-output/pause"
      html |> Expect.stringContains "search" "/dashboard/app-output/search"
      html |> Expect.stringContains "stream" "/dashboard/app-output/stream"

    testCase "WHY — nothing is hand-written: every interaction is a Datastar attribute, the same rule the rest of the dock is held to" <| fun _ ->
      let html = draw paneWithBothLines (HasAppOutput 2) SessionInView
      html.Contains " onclick=" |> Expect.isFalse "no raw onclick"
      html.Contains " oninput=" |> Expect.isFalse "no raw oninput"

    testCase "WHY — a control shows the state the SERVER holds, so it can never look on while the decision has it off" <| fun _ ->
      let paused = { paneWithBothLines with Paused = Held }
      let html = draw paused (HasAppOutput 2) SessionInView
      html |> Expect.stringContains "the pause control says it is paused" "paused"
      html |> Expect.stringContains "and reports it to assistive tech" "aria-pressed=\"true\""
      // The pane is held, so its own header says how much it is holding — not "2 lines" as if showing.
      html |> Expect.stringContains "and the header agrees" "while paused"

    testCase "every control has an id of its own, so the morph matches it by id and never by position" <| fun _ ->
      let html = draw paneWithBothLines (HasAppOutput 2) SessionInView
      html |> Expect.stringContains "follow" "id=\"app-output-follow\""
      html |> Expect.stringContains "pause" "id=\"app-output-pause\""
      html |> Expect.stringContains "search" "id=\"app-output-search\""
  ]
