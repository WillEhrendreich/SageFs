/// Proves the Phase-1 smoke scenario's shape (demo-gif-plan.md §10 Phase 1):
/// `hello-dashboard` is a real `Scenario` value with a stable id and three
/// steps that actually tell a story (start a session, watch it reach
/// Ready, evaluate a real F# expression and see the result) — not just
/// "a session card appears", which fires while still `WarmingUp` and
/// stops the recording before anything interesting happens (§9 fix). Step
/// 3 folds "expand the dashboard's collapsed Evaluate accordion", "type
/// the expression" and "click Eval" into ONE `ClickThenTypeThenClick`
/// action with no step boundary between the expand and the type — the
/// accordion's open state does not survive the dashboard's own
/// server-driven re-render (a real upstream defect another agent is
/// fixing concurrently, out of scope here), so the expand and the typing
/// that depends on it must land in the same step. A `DashboardOnly`
/// layout stays consistent with "dashboard only" (no editor pane, so no
/// magnifier — `Compose.fs`'s own rule).
module SageFs.Demos.Tests.ScenariosTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain
open SageFs.Demos.Scenarios
open SageFs.Demos.Layout

[<Tests>]
let tests =
  testList "Scenarios" [

    testCase "helloDashboard's id is exactly 'hello-dashboard' (the CLI verb the job wires: record hello-dashboard)" <| fun _ ->
      helloDashboard.Id |> ScenarioId.value |> Expect.equal "raw scenario id" "hello-dashboard"

    testCase "helloDashboard is dashboard-only: no editor client, no app pane" <| fun _ ->
      helloDashboard.Client |> Expect.equal "client" Client.Dashboard
      helloDashboard.App |> Expect.equal "app kind" AppKind.NoApp
      helloDashboard.Layout |> Expect.equal "layout" LayoutTemplate.DashboardOnly

    testCase "helloDashboard has exactly three steps: start, warm up to Ready, evaluate" <| fun _ ->
      helloDashboard.Steps |> List.length |> Expect.equal "step count" 3

    testCase "step 1 clicks Quick Start and expects the session card to appear" <| fun _ ->
      match helloDashboard.Steps.[0].Action with
      | Action.Click(Target.DashboardElement DashboardId.QuickStart) -> ()
      | other -> failtestf "expected Click(DashboardElement QuickStart), got %A" other

      match helloDashboard.Steps.[0].Expect with
      | Expectation.PageShows(DashboardId.SessionCard, _) -> ()
      | other -> failtestf "expected PageShows(SessionCard, _), got %A" other

    testCase "step 2 clicks the session card (navigating into its own view — Quick Start alone doesn't) and expects the status text to actually say Ready, not just the card existing (§9 fix)" <| fun _ ->
      match helloDashboard.Steps.[1].Action with
      | Action.Click(Target.DashboardElement DashboardId.SessionCard) -> ()
      | other -> failtestf "expected Click(DashboardElement SessionCard), got %A" other

      match helloDashboard.Steps.[1].Expect with
      | Expectation.PageTextContains(selector, "Ready") -> selector |> Expect.isNotEmpty "a real selector, not a blank string"
      | other -> failtestf "expected PageTextContains(_, \"Ready\"), got %A" other

      // §9: dwell so the viewer actually sees the transition, not a snap-cut.
      helloDashboard.Steps.[1].Dwell |> Expect.equal "dwell is Long for the warmup->ready transition" Dwell.Long

    testCase "step 3 expands the collapsed Evaluate accordion, types a real F# expression, then clicks Eval — all in one step, no boundary in between (§9 fix for the accordion-collapse race)" <| fun _ ->
      match helloDashboard.Steps.[2].Action with
      | Action.ClickThenTypeThenClick(Target.DashboardCssSelector preClickSelector, Target.DashboardCssSelector typeSelector, text, _, Target.DashboardElement DashboardId.Eval) ->
        preClickSelector |> Expect.isNotEmpty "a real selector for the collapsed Evaluate header"
        typeSelector |> Expect.isNotEmpty "a real CSS selector for the eval textarea"
        Text.value text |> Expect.isNotEmpty "a real F# expression, not a blank string"
      | other -> failtestf "expected ClickThenTypeThenClick(DashboardCssSelector _, DashboardCssSelector _, _, _, DashboardElement Eval), got %A" other

      match helloDashboard.Steps.[2].Expect with
      | Expectation.PageTextContains(selector, text) ->
        selector |> Expect.equal "expects the result in the session-output panel" "[data-testid=session-output]"
        text |> Expect.isNotEmpty "expects a real evaluated value, not a blank string"
      | other -> failtestf "expected PageTextContains(session-output, _), got %A" other

    testCase "step 3's typed expression evaluates (by construction) to the text step 3 expects — the demo cannot silently drift out of sync" <| fun _ ->
      match helloDashboard.Steps.[2].Action, helloDashboard.Steps.[2].Expect with
      | Action.ClickThenTypeThenClick(_, _, text, _, _), Expectation.PageTextContains(_, expected) ->
        Text.value text |> Expect.equal "typed expression" "[1..10] |> List.sum"
        expected |> Expect.equal "expected result text (55 = sum of 1..10)" "55"
      | other -> failtestf "expected ClickThenTypeThenClick and PageTextContains, got %A" other

    testCase "helloDashboard's DashboardOnly layout leaves Layout.rects with no editor pane to magnify (Compose.fs's rule)" <| fun _ ->
      let layout = rects helloDashboard.Layout { Width = 1280; Height = 720 }
      layout |> Map.containsKey ActorId.VsCode |> Expect.isFalse "no VsCode pane"
      layout |> Map.containsKey ActorId.Neovim |> Expect.isFalse "no Neovim pane"
      layout |> Map.containsKey ActorId.Dashboard |> Expect.isTrue "the dashboard fills the screen"
  ]
