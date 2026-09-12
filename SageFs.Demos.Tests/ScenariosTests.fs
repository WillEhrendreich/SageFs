/// Proves the Phase-1 smoke scenario's shape (demo-gif-plan.md §10 Phase 1):
/// `hello-dashboard` is a real `Scenario` value with a stable id, exactly the
/// one step the job asks for (click Quick Start, observe a session card),
/// and a `DashboardOnly` layout consistent with "dashboard only" (no editor
/// pane, so no magnifier — `Compose.fs`'s own rule).
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

    testCase "helloDashboard has exactly one step: click Quick Start" <| fun _ ->
      helloDashboard.Steps |> List.length |> Expect.equal "step count" 1

      match helloDashboard.Steps.[0].Action with
      | Action.Click(Target.DashboardElement DashboardId.QuickStart) -> ()
      | other -> failtestf "expected Click(DashboardElement QuickStart), got %A" other

    testCase "helloDashboard's one step expects a session card to appear" <| fun _ ->
      match helloDashboard.Steps.[0].Expect with
      | Expectation.PageShows(DashboardId.SessionCard, _) -> ()
      | other -> failtestf "expected PageShows(SessionCard, _), got %A" other

    testCase "helloDashboard's DashboardOnly layout leaves Layout.rects with no editor pane to magnify (Compose.fs's rule)" <| fun _ ->
      let layout = rects helloDashboard.Layout { Width = 1280; Height = 720 }
      layout |> Map.containsKey ActorId.VsCode |> Expect.isFalse "no VsCode pane"
      layout |> Map.containsKey ActorId.Neovim |> Expect.isFalse "no Neovim pane"
      layout |> Map.containsKey ActorId.Dashboard |> Expect.isTrue "the dashboard fills the screen"
  ]
