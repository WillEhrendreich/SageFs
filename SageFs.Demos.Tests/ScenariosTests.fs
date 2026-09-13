/// Proves the Phase-1 smoke scenario's shape (demo-gif-plan.md §10 Phase 1):
/// `hello-dashboard` is a real `Scenario` value with a stable id and three
/// steps that actually tell a story — open a REAL sample project (not a
/// bare Quick Start temp session, which has nothing to auto-open and
/// nothing real to eval against, §10), watch it reach Ready, then evaluate
/// an expression that reads the project's OWN mutable state and see the
/// result — not just "a session card appears", which fires while still
/// `WarmingUp` and stops the recording before anything interesting happens
/// (§9 fix). Step 3 folds "expand the dashboard's collapsed Evaluate
/// accordion", "type the expression" and "click Eval" into ONE
/// `ClickThenTypeThenClick` action with no step boundary between the expand
/// and the type — the accordion's open state does not survive the
/// dashboard's own server-driven re-render (a real upstream defect another
/// agent is fixing concurrently, out of scope here), so the expand and the
/// typing that depends on it must land in the same step. A `DashboardOnly`
/// layout stays consistent with "dashboard only" (no editor pane, so no
/// magnifier — `Compose.fs`'s own rule).
module SageFs.Demos.Tests.ScenariosTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain
open SageFs.Demos.Scenarios.Dashboard
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

    testCase "helloDashboard opens a real runnable sample, not just a bare Quick Start session (§10)" <| fun _ ->
      helloDashboard.Sample |> Expect.equal "sample" Sample.WebappDatastar

    testCase "helloDashboard has exactly three steps: open a project, warm up to Ready, evaluate" <| fun _ ->
      helloDashboard.Steps |> List.length |> Expect.equal "step count" 3

    testCase "step 1 types the sample's real, repo-root-relative directory into the 'Open Directory' picker and clicks Create — never Quick Start (§10)" <| fun _ ->
      match helloDashboard.Steps.[0].Action with
      | Action.TypeThenClick(Target.DashboardCssSelector typeSelector, text, _, Target.DashboardCssSelector submitSelector) ->
        typeSelector |> Expect.isNotEmpty "a real selector for the 'Open Directory' input"
        submitSelector |> Expect.isNotEmpty "a real selector for the Create button"
        let typed = Text.value text
        typed |> Expect.stringContains "types a path derived from repoRoot, resolved later by Runtime.fs" Text.RepoRootToken
        typed |> Expect.stringContains "points at the real sample project's own relative directory" (Sample.relativePath helloDashboard.Sample)
      | other -> failtestf "expected TypeThenClick(DashboardCssSelector _, _, _, DashboardCssSelector _), got %A" other

    testCase "step 1 expects the output panel to show a REAL, non-empty source scan — structural proof the project actually loaded, not just that a card exists (§10)" <| fun _ ->
      match helloDashboard.Steps.[0].Expect with
      | Expectation.PageTextContains(selector, text) ->
        selector |> Expect.equal "checks the session output log" "[data-testid=session-output]"
        text |> Expect.equal "a real, non-zero source-file scan (AppState.fs's own wording)" "Scanned 1 source files"
      | other -> failtestf "expected PageTextContains(session-output, \"Scanned 1 source files\"), got %A" other

    testCase "step 2 clicks the session card (navigating into its own view — creating a session alone doesn't) and expects the status text to actually say Ready, not just the card existing (§9 fix)" <| fun _ ->
      match helloDashboard.Steps.[1].Action with
      | Action.Click(Target.DashboardElement DashboardId.SessionCard) -> ()
      | other -> failtestf "expected Click(DashboardElement SessionCard), got %A" other

      match helloDashboard.Steps.[1].Expect with
      | Expectation.PageTextContains(selector, "Ready") -> selector |> Expect.isNotEmpty "a real selector, not a blank string"
      | other -> failtestf "expected PageTextContains(_, \"Ready\"), got %A" other

      // §9: dwell so the viewer actually sees the transition, not a snap-cut.
      helloDashboard.Steps.[1].Dwell |> Expect.equal "dwell is Long for the warmup->ready transition" Dwell.Long

    testCase "step 3 expands the collapsed Evaluate accordion, types an expression that reads the project's OWN state, then clicks Eval — all in one step, no boundary in between (§9/§10 fix)" <| fun _ ->
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

    testCase "step 3's typed expression references the sample project's own mutable state (todos), not arithmetic that would pass identically on an empty session (§10)" <| fun _ ->
      match helloDashboard.Steps.[2].Action, helloDashboard.Steps.[2].Expect with
      | Action.ClickThenTypeThenClick(_, _, text, _, _), Expectation.PageTextContains(_, expected) ->
        Text.value text
        |> Expect.equal "typed expression reads the project's own todos list, fully qualified (warmup auto-open never opens the current project's own module)" "SageFs.Samples.WebappDatastar.Program.todos.Length"
        expected |> Expect.equal "expected result text — specific enough not to false-match warmup's own \"[3/4]\" log line" "int = 3"
      | other -> failtestf "expected ClickThenTypeThenClick and PageTextContains, got %A" other

    testCase "helloDashboard's DashboardOnly layout leaves Layout.rects with no editor pane to magnify (Compose.fs's rule)" <| fun _ ->
      let layout = rects helloDashboard.Layout { Width = 1280; Height = 720 }
      layout |> Map.containsKey ActorId.VsCode |> Expect.isFalse "no VsCode pane"
      layout |> Map.containsKey ActorId.Neovim |> Expect.isFalse "no Neovim pane"
      layout |> Map.containsKey ActorId.Dashboard |> Expect.isTrue "the dashboard fills the screen"
  ]
