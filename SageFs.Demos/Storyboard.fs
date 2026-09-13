/// Pure storyboard rendering (demo-gif-plan.md §4.8, §5): draws the layout
/// rects, actor placement, cursor path (over the *expected* target rects)
/// and captions for a scenario, with no launch and no sandbox — the review
/// step before a scenario costs a real recording.
module SageFs.Demos.Storyboard

open System.Text
open SageFs.Demos.Domain

// ---------------------------------------------------------------------------
// Small, pure helpers. No IO; everything here is string/data plumbing.
// ---------------------------------------------------------------------------

let private xmlEscape (s: string) : string =
  s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

/// The `ActorId` a scenario's own `Client` is rendered as (§5: "a scenario
/// always has exactly one `Client` — the editor 'on camera'").
let private clientActorId (client: Client) : ActorId =
  match client with
  | Client.Dashboard -> ActorId.Dashboard
  | Client.VsCode -> ActorId.VsCode
  | Client.Neovim -> ActorId.Neovim
  | Client.Agent -> ActorId.Agent

/// Which actor's placed rect a `Target` resolves against, at storyboard time
/// — an approximation of the real per-element resolution an actor performs
/// live (§4.3 Targets): the storyboard only knows the *pane* a target lives
/// in, from the layout template, not the exact pixel a live actor would
/// measure.
let private targetActorId (client: Client) (target: Target) : ActorId option =
  match target with
  | Target.DashboardElement _ -> Some ActorId.Dashboard
  | Target.DashboardCssSelector _ -> Some ActorId.Dashboard
  | Target.EditorPosition _ -> Some (clientActorId client)
  | Target.PaletteItem _ -> Some (clientActorId client)
  | Target.NvimCommandLine -> Some ActorId.Neovim
  | Target.AppWindowPoint _ -> Some ActorId.App
  | Target.WindowCenter actorId -> Some actorId

/// The `Target` an `Action` moves the cursor towards, if any — `Chord`,
/// `Setup` and `Await` do not name a screen position, so the cursor simply
/// holds still for those steps.
let private actionTarget (action: Action) : Target option =
  match action with
  | Action.Click target -> Some target
  | Action.Type (target, _, _) -> Some target
  | Action.Typo (target, _, _) -> Some target
  | Action.TypeThenClick (typeTarget, _, _, _) -> Some typeTarget
  | Action.ClickThenTypeThenClick (preClickTarget, _, _, _, _) -> Some preClickTarget
  | Action.Chord _
  | Action.Setup _
  | Action.Await _ -> None

let private rectCenter (r: Rect) : Point = { X = r.X + r.W / 2; Y = r.Y + r.H / 2 }

/// A simple straight-line-with-midpoint sketch of a cursor move from `from`
/// to `toPoint`. Deliberately NOT `SageFs.Demos.Motion.path` — that planner
/// (the real minimum-jerk/wobble motion used at recording time) is being
/// implemented in parallel by another agent and may still be a stub; the
/// storyboard only needs a readable sketch of the route for review, not the
/// eventual eased/wobbled motion.
/// TODO: unify with Motion.path once both land.
let private simpleSegment (from: Point) (toPoint: Point) : Point list =
  let mid = { X = (from.X + toPoint.X) / 2; Y = (from.Y + toPoint.Y) / 2 }
  [ from; mid; toPoint ]

// ---------------------------------------------------------------------------
// SVG element rendering.
// ---------------------------------------------------------------------------

let private renderPaneRect (style: Style) (actorId: ActorId) (rect: Rect) : string =
  sprintf
    "<rect data-actor=\"%s\" x=\"%d\" y=\"%d\" width=\"%d\" height=\"%d\" fill=\"%s\" stroke=\"%s\" stroke-width=\"1\" />"
    (string actorId)
    rect.X
    rect.Y
    rect.W
    rect.H
    style.Panel
    style.Rule

let private renderPolyline (style: Style) (points: Point list) : string =
  match points with
  | [] | [ _ ] -> ""
  | _ ->
    let coords = points |> List.map (fun p -> sprintf "%d,%d" p.X p.Y) |> String.concat " "
    sprintf "<polyline points=\"%s\" fill=\"none\" stroke=\"%s\" stroke-width=\"2\" />" coords style.Accent

let private renderClickMarker (style: Style) (p: Point) : string =
  sprintf "<circle cx=\"%d\" cy=\"%d\" r=\"6\" fill=\"%s\" />" p.X p.Y style.Accent

let private renderCaption (style: Style) (screenHeight: int) (lineHeight: int) (padding: int) (index: int) (step: Step) : string =
  let y = screenHeight + padding + (index + 1) * lineHeight
  sprintf
    "<text x=\"16\" y=\"%d\" fill=\"%s\" font-size=\"16\">%d. %s</text>"
    y
    style.Ink
    (index + 1)
    (xmlEscape (Caption.value step.Caption))

// ---------------------------------------------------------------------------
// The planner.
// ---------------------------------------------------------------------------

/// Renders `scenario`'s storyboard strip at the given `screen` size and
/// `style`.
let svg (scenario: Scenario) (screen: Screen) (style: Style) : Svg =
  let layout = SageFs.Demos.Layout.rects scenario.Layout screen

  let paneRectsXml =
    layout |> Map.toList |> List.map (fun (actorId, rect) -> renderPaneRect style actorId rect) |> String.concat "\n    "

  let targetFor (step: Step) : Point option =
    actionTarget step.Action
    |> Option.bind (targetActorId scenario.Client)
    |> Option.bind (fun actorId -> Map.tryFind actorId layout)
    |> Option.map rectCenter

  let start = { X = screen.Width / 2; Y = screen.Height / 2 }

  let _, pathPoints, markers =
    scenario.Steps
    |> List.fold
      (fun (current, pathAcc, markerAcc) step ->
        match targetFor step with
        | Some target -> target, pathAcc @ simpleSegment current target, markerAcc @ [ target ]
        | None -> current, pathAcc, markerAcc)
      (start, [ start ], [])

  let polylineXml = renderPolyline style pathPoints
  let markersXml = markers |> List.map (renderClickMarker style) |> String.concat "\n    "

  let lineHeight = 24
  let captionPadding = 16
  let captionsHeight = scenario.Steps.Length * lineHeight + captionPadding * 2
  let totalHeight = screen.Height + captionsHeight

  let captionsXml =
    scenario.Steps
    |> List.mapi (renderCaption style screen.Height lineHeight captionPadding)
    |> String.concat "\n    "

  let document =
    let sb = StringBuilder()
    sb
      .AppendLine(sprintf "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"%d\" height=\"%d\" viewBox=\"0 0 %d %d\">" screen.Width totalHeight screen.Width totalHeight)
      .AppendLine(sprintf "    <rect x=\"0\" y=\"0\" width=\"%d\" height=\"%d\" fill=\"%s\" />" screen.Width screen.Height style.Ground)
      .AppendLine("    " + paneRectsXml)
      .AppendLine("    " + polylineXml)
      .AppendLine("    " + markersXml)
      .AppendLine("    " + captionsXml)
      .AppendLine("</svg>")
    |> string

  Svg document
