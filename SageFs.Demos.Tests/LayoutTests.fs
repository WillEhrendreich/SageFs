/// Proves `Layout.rects` (demo-gif-plan.md §5, §9, §4.8) places every actor's
/// pane on an 8px grid, inside the screen, without overlapping, and tiling
/// the screen exactly with 8px `Ground` gutters between panes — for every
/// `LayoutTemplate`. Enumerated exhaustively rather than sampled: the domain
/// (3 `LayoutTemplate` cases) is small and closed, so exhaustion is stronger
/// than a probabilistic property test here.
module SageFs.Demos.Tests.LayoutTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain

/// The canonical demo screen (§4.5: `Xvfb :99 -screen 0 1280x720x24`) — the
/// only resolution `Layout.rects` is ever asked to plan for.
let private screen = { Width = 1280; Height = 720 }

let private allTemplates =
  [ LayoutTemplate.EditorLeft; LayoutTemplate.EditorFull; LayoutTemplate.DashboardOnly ]

let private isMultipleOf8 (n: int) = n % 8 = 0

let private rectsOverlap (a: Rect) (b: Rect) =
  a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H

[<Tests>]
let tests =
  testList "Layout" [

    testCase "every rect field is a multiple of 8, for every LayoutTemplate" <| fun _ ->
      for template in allTemplates do
        let placed = SageFs.Demos.Layout.rects template screen
        for KeyValue (actorId, rect) in placed do
          isMultipleOf8 rect.X
          |> Expect.isTrue (sprintf "%A/%A X=%d should be a multiple of 8" template actorId rect.X)
          isMultipleOf8 rect.Y
          |> Expect.isTrue (sprintf "%A/%A Y=%d should be a multiple of 8" template actorId rect.Y)
          isMultipleOf8 rect.W
          |> Expect.isTrue (sprintf "%A/%A W=%d should be a multiple of 8" template actorId rect.W)
          isMultipleOf8 rect.H
          |> Expect.isTrue (sprintf "%A/%A H=%d should be a multiple of 8" template actorId rect.H)

    testCase "every rect lies within the 1280x720 screen, for every LayoutTemplate" <| fun _ ->
      for template in allTemplates do
        let placed = SageFs.Demos.Layout.rects template screen
        for KeyValue (actorId, rect) in placed do
          (rect.X >= 0 && rect.Y >= 0)
          |> Expect.isTrue (sprintf "%A/%A should have a non-negative origin" template actorId)
          (rect.X + rect.W <= screen.Width)
          |> Expect.isTrue (sprintf "%A/%A should fit within the screen width" template actorId)
          (rect.Y + rect.H <= screen.Height)
          |> Expect.isTrue (sprintf "%A/%A should fit within the screen height" template actorId)

    testCase "distinct panes never overlap, for every LayoutTemplate" <| fun _ ->
      for template in allTemplates do
        let distinctRects =
          SageFs.Demos.Layout.rects template screen |> Map.toList |> List.map snd |> List.distinct
        for i in 0 .. distinctRects.Length - 1 do
          for j in i + 1 .. distinctRects.Length - 1 do
            rectsOverlap distinctRects.[i] distinctRects.[j]
            |> Expect.isFalse (sprintf "%A panes %A and %A should not overlap" template distinctRects.[i] distinctRects.[j])

    testCase "EditorLeft tiles the screen exactly with 8px gutters (§9)" <| fun _ ->
      let placed = SageFs.Demos.Layout.rects LayoutTemplate.EditorLeft screen
      let editor = placed.[ActorId.VsCode]
      let dashboard = placed.[ActorId.Dashboard]
      let app = placed.[ActorId.App]
      editor.H |> Expect.equal "the editor pane spans the full screen height" screen.Height
      (dashboard.X - (editor.X + editor.W))
      |> Expect.equal "8px gutter between the editor column and the right column" 8
      (app.Y - (dashboard.Y + dashboard.H)) |> Expect.equal "8px gutter between the dashboard and app panes" 8
      (editor.W + 8 + dashboard.W) |> Expect.equal "editor + gutter + right column tiles the screen width exactly" screen.Width
      (dashboard.H + 8 + app.H) |> Expect.equal "dashboard + gutter + app tiles the screen height exactly" screen.Height
      dashboard.W |> Expect.equal "dashboard and app share the same pane width" app.W

    testCase "EditorFull tiles the screen exactly with an 8px gutter (§9)" <| fun _ ->
      let placed = SageFs.Demos.Layout.rects LayoutTemplate.EditorFull screen
      let editor = placed.[ActorId.VsCode]
      let dashboard = placed.[ActorId.Dashboard]
      editor.H |> Expect.equal "the editor pane spans the full screen height" screen.Height
      dashboard.H |> Expect.equal "the dashboard pane spans the full screen height" screen.Height
      (dashboard.X - (editor.X + editor.W)) |> Expect.equal "8px gutter between editor and dashboard" 8
      (editor.W + 8 + dashboard.W) |> Expect.equal "editor + gutter + dashboard tiles the screen width exactly" screen.Width

    testCase "DashboardOnly fills the entire screen (§9)" <| fun _ ->
      let placed = SageFs.Demos.Layout.rects LayoutTemplate.DashboardOnly screen
      placed.[ActorId.Dashboard]
      |> Expect.equal "the dashboard pane fills the whole 1280x720 screen" { X = 0; Y = 0; W = screen.Width; H = screen.Height }

    testCase "VsCode and Neovim share the same editor rect (only one client is on camera per scenario)" <| fun _ ->
      for template in [ LayoutTemplate.EditorLeft; LayoutTemplate.EditorFull ] do
        let placed = SageFs.Demos.Layout.rects template screen
        placed.[ActorId.VsCode]
        |> Expect.equal "VsCode and Neovim occupy the identical editor rect" placed.[ActorId.Neovim]
  ]
