/// Which panes the dashboard's bottom dock shows, as one pure decision. Will's call: the live bindings are where the Evaluate
/// box used to be, so the dock's first pane is the live bindings, shown when the session has bindings, collapsed to ONE line that
/// says why when it has none (never a blank), and openable anyway by pinning it. A pane that is not shown always carries its reason.
module SageFs.Tests.DockPanesTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Server.DockPanes
open SageFs.Tests.SharedGenerators

let private genSession = Gen.elements [ NoSessionInView; SessionInView ]
let private genPin = Gen.elements [ Unpinned; Pinned ]
let private genCount = Gen.choose (-3, 40)

let private genFacts : Gen<PaneFacts> =
  gen {
    let! session = genSession
    let! pin = genPin
    let! count = genCount
    let! output = genCount
    return
      { Session = session
        Bindings = BindingsPresence.ofCount count
        AppOutput = AppOutputPresence.ofCount output
        Pin = pin }
  }

let private arbFacts = Arb.fromGen genFacts

let private liveBindings facts = PaneState.decide facts DockPane.LiveBindings

[<Tests>]
let dockPaneTests =
  testList "the dock's panes: which are shown, and why not" [

    testCase "a count of bindings is either none yet or that many, and a negative count is none" <| fun _ ->
      BindingsPresence.ofCount 0 |> Expect.equal "zero" NoBindingsYet
      BindingsPresence.ofCount -4 |> Expect.equal "negative" NoBindingsYet
      BindingsPresence.ofCount 3 |> Expect.equal "three" (HasBindings 3)

    testPropertyWithConfig propConfig "with no session in view the pane is collapsed for that reason, whatever else is true"
      (Prop.forAll arbFacts (fun facts ->
        let facts = { facts with Session = NoSessionInView }
        liveBindings facts = PaneCollapsed NoSessionOpen))

    testPropertyWithConfig propConfig "a session with bindings shows the pane, pinned or not, and says how many"
      (Prop.forAll (Arb.fromGen (Gen.zip genPin (Gen.choose (1, 500)))) (fun (pin, count) ->
        liveBindings { Session = SessionInView; Bindings = HasBindings count; AppOutput = NoAppOutput; Pin = pin } = PaneOpen(SessionHasBindings count)))

    testCase "a session with no bindings collapses the pane to its reason until the user pins it" <| fun _ ->
      liveBindings { Session = SessionInView; Bindings = NoBindingsYet; AppOutput = NoAppOutput; Pin = Unpinned }
      |> Expect.equal "collapsed" (PaneCollapsed NothingBoundYet)
      liveBindings { Session = SessionInView; Bindings = NoBindingsYet; AppOutput = NoAppOutput; Pin = Pinned }
      |> Expect.equal "pinned open" (PaneOpen PinnedByUser)

    testPropertyWithConfig propConfig "pinning never collapses a pane that a session in view would show"
      (Prop.forAll arbFacts (fun facts ->
        match facts.Session with
        | NoSessionInView -> true
        | SessionInView ->
          match liveBindings { facts with Pin = Pinned } with
          | PaneOpen _ -> true
          | PaneCollapsed _ -> false))

    testPropertyWithConfig propConfig "unpinning never opens a pane that has nothing to show"
      (Prop.forAll arbFacts (fun facts ->
        match facts.Bindings, liveBindings { facts with Pin = Unpinned } with
        | NoBindingsYet, PaneOpen _ -> false
        | _ -> true))

    testPropertyWithConfig propConfig "every collapsed pane says why in words"
      (Prop.forAll arbFacts (fun facts ->
        match liveBindings facts with
        | PaneOpen _ -> true
        | PaneCollapsed why -> not (String.IsNullOrWhiteSpace(CollapsedBecause.text why))))

    testCase "the reasons differ and tell the user what to do" <| fun _ ->
      let none = CollapsedBecause.text NothingBoundYet
      let noSession = CollapsedBecause.text NoSessionOpen
      (none <> noSession) |> Expect.isTrue "different reasons"
      none |> Expect.stringContains "says what to do" "evaluate"
      noSession |> Expect.stringContains "says what to do" "session"

    testCase "a pin is offered only where there is a session to pin it on" <| fun _ ->
      CollapsedBecause.pin NothingBoundYet |> Expect.equal "offered" PinOffered
      CollapsedBecause.pin NoSessionOpen |> Expect.equal "not offered" PinNotOffered

    testCase "the decision covers every pane the dock has, in order, once each" <| fun _ ->
      let facts = { Session = SessionInView; Bindings = HasBindings 2; AppOutput = HasAppOutput 7; Pin = Unpinned }
      PaneState.all facts |> List.map fst |> Expect.equal "one entry per pane" DockPane.all
      DockPane.all |> List.distinct |> List.length |> Expect.equal "no pane twice" DockPane.all.Length

    testCase "the open pane's header says whether the bindings or the pin opened it" <| fun _ ->
      OpenBecause.text (SessionHasBindings 1) |> Expect.stringContains "singular" "1 binding"
      OpenBecause.text (SessionHasBindings 5) |> Expect.stringContains "plural" "5 bindings"
      OpenBecause.text PinnedByUser |> Expect.stringContains "pinned" "pinned"

    // ── the dock's second pane, the running app's stdout and stderr ─────────────────────────────

    testCase "a count of app output is either none yet or that many, and a negative count is none" <| fun _ ->
      AppOutputPresence.ofCount 0 |> Expect.equal "zero" NoAppOutput
      AppOutputPresence.ofCount -4 |> Expect.equal "negative" NoAppOutput
      AppOutputPresence.ofCount 5 |> Expect.equal "five" (HasAppOutput 5)

    testCase "WHY — the pane opens when the app has written, and collapses when it has not, paired so neither side can go stale" <| fun _ ->
      // Both directions, deliberately: an implementation that always opened would pass the first
      // half, and one that always collapsed would pass the second.
      let facts appOutput pin =
        { Session = SessionInView; Bindings = NoBindingsYet; AppOutput = appOutput; Pin = pin }
      PaneState.decide (facts (HasAppOutput 3) Unpinned) DockPane.AppOutput
      |> Expect.equal "output opens it" (PaneOpen(SessionProducedOutput 3))
      PaneState.decide (facts NoAppOutput Unpinned) DockPane.AppOutput
      |> Expect.equal "no output collapses it" (PaneCollapsed NoAppOutputYet)
      PaneState.decide (facts NoAppOutput Pinned) DockPane.AppOutput
      |> Expect.equal "pinned open anyway" (PaneOpen PinnedByUser)

    testCase "WHY — no session beats every other fact, because there is nothing to show output from" <| fun _ ->
      PaneState.decide
        { Session = NoSessionInView
          Bindings = NoBindingsYet
          AppOutput = HasAppOutput 9
          Pin = Pinned }
        DockPane.AppOutput
      |> Expect.equal "collapsed, not open" (PaneCollapsed NoSessionOpen)

    testCase "WHY — the collapsed app-output pane says how to make it write, not just that it is empty" <| fun _ ->
      let why = CollapsedBecause.text NoAppOutputYet
      why |> Expect.stringContains "says what to do" "run_app"
      (why <> CollapsedBecause.text NothingBoundYet) |> Expect.isTrue "different reason from the bindings pane"

    testCase "a pin is offered for app output too, so a pane you are about to feed is not something you feed first" <| fun _ ->
      CollapsedBecause.pin NoAppOutputYet |> Expect.equal "offered" PinOffered

    testCase "the app-output header counts lines, and says line once" <| fun _ ->
      OpenBecause.text (SessionProducedOutput 1) |> Expect.stringContains "singular" "1 line"
      OpenBecause.text (SessionProducedOutput 5) |> Expect.stringContains "plural" "5 lines"
  ]
