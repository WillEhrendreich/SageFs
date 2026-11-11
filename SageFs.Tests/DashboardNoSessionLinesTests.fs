/// The always-visible line of a panel that is not drawn follows the decision behind it.
///
/// With no session open the sidebar said "Hot reload: off" and "Live testing: off" while the sentence one click
/// away said "no session is selected". The line was chosen by the panel alone. It now comes from the reason
/// (`HiddenBecause.headline`), so the two cannot disagree, and the diagnostics sentence no longer repeats its own
/// word and says what to do.
module SageFs.Tests.DashboardNoSessionLinesTests

open System
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs.Server
open SageFs.Server.DashboardPanelContext
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Tests.DashboardPanelVisibilityTests

let private noSession =
  { replFacts with Viewed = ViewedSession.NoSession; Diagnostics = DiagnosticsCheck.NotChecked }

let private hiddenReasons (facts: PanelFacts) : (ContextPanel * HiddenBecause * NextStep) list =
  PanelContext.decisions facts
  |> List.choose (fun (panel, presence) ->
    match presence with
    | PanelPresence.Hidden(because, next) -> Some(panel, because, next)
    | PanelPresence.Shown
    | PanelPresence.Quiet _ -> None)

[<Tests>]
let tests =
  testList "the line a hidden panel always shows" [
    testCase "WHY — with no session, no panel line claims a mode is off: there is no session to have a mode" <| fun _ ->
      let reasons = hiddenReasons noSession
      (List.length reasons, 3) |> Expect.isGreaterThan "hot reload, live testing, cohort and diagnostics are hidden"
      for panel, because, _ in reasons do
        match because with
        | HiddenBecause.NoSessionSelected _ ->
          HiddenBecause.headline because
          |> Expect.stringContains (sprintf "%A names the missing session" panel) "no session selected"
          (HiddenBecause.headline because).Contains ": off"
          |> Expect.isFalse (sprintf "%A does not say off" panel)
        | HiddenBecause.SessionInReplMode
        | HiddenBecause.SessionInLiveTestingMode
        | HiddenBecause.LiveTestingOff
        | HiddenBecause.NoCohortRunning
        | HiddenBecause.NotCheckedYet _ -> ()

    testCase "WHY — the page's summary line is the headline of the decision, for every hidden panel, in every state" <| fun _ ->
      let states = [ noSession; replFacts; { replFacts with Viewed = ViewedSession.Viewing(hotReload, off) } ]
      for state in states do
        let html = PanelVisibility.apply state (snapshotWith off []) |> renderMainContent |> renderNode
        for panel, because, _ in hiddenReasons state do
          html
          |> Expect.stringContains (sprintf "%A line is its headline" panel) (sprintf ">%s<" (HiddenBecause.headline because))

    testCase "WHY — the no-session page names the missing session where it used to say off" <| fun _ ->
      let html = PanelVisibility.apply noSession (snapshotWith off []) |> renderMainContent |> renderNode
      html |> Expect.stringContains "hot reload" "Hot reload: no session selected"
      html |> Expect.stringContains "live testing" "Live testing: no session selected"
      html.Contains "Hot reload: off" |> Expect.isFalse "the lie is gone"
      html.Contains "Live testing: off" |> Expect.isFalse "the lie is gone"

    testCase "WHY — the headline and the sentence behind it open with the same subject" <| fun _ ->
      for state in [ noSession; replFacts ] do
        for panel, because, _ in hiddenReasons state do
          match because with
          | HiddenBecause.NoSessionSelected p ->
            let subject = ContextPanel.sentenceName p
            (HiddenBecause.headline because).StartsWith subject |> Expect.isTrue (sprintf "%A headline starts with %s" panel subject)
            (HiddenBecause.text because).StartsWith subject |> Expect.isTrue (sprintf "%A sentence starts with %s" panel subject)
          | HiddenBecause.SessionInReplMode
          | HiddenBecause.SessionInLiveTestingMode
          | HiddenBecause.LiveTestingOff
          | HiddenBecause.NoCohortRunning
          | HiddenBecause.NotCheckedYet _ -> ()

    testCase "WHY — the diagnostics sentence does not say the word twice and tells the person what to do" <| fun _ ->
      match PanelContext.decide noSession ContextPanel.Diagnostics with
      | PanelPresence.Hidden(because, next) ->
        let text = HiddenBecause.text because
        Regex.IsMatch(text, @"diagnostics\W+diagnostics", RegexOptions.IgnoreCase)
        |> Expect.isFalse (sprintf "no doubled word in: %s" text)
        text |> Expect.stringContains "it says why" "No session is open"
        next |> Expect.notEqual "a next step is named" NextStep.NoNextStep
        match next with
        | NextStep.UseControl name -> name |> Expect.stringContains "the control is the one that opens a session" "Quick Start"
        | NextStep.PostAction _
        | NextStep.NoNextStep -> failtestf "expected a named control, got %A" next
      | other -> failtestf "expected Hidden, got %A" other
  ]
