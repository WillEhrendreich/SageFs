/// Pure per-actor rect layout for a `LayoutTemplate` on a given `Screen`
/// (demo-gif-plan.md §5, §9). Wave 2 property tests: rects are multiples of
/// 8, tile without overlap, respect the 8 px gutter / 1 px separator rule.
module SageFs.Demos.Layout

open SageFs.Demos.Domain

/// The `Ground` gap between adjacent panes (§9: "8px Ground gutters"). The
/// 1px `Rule` separator §9 also names is drawn as a hairline centered inside
/// this gap by the storyboard/composer — it is a rendering detail, not an
/// extra span the layout has to reserve, which is why the plan's own numbers
/// tile exactly (e.g. 704 + 8 + 568 = 1280) with nothing left over for it.
[<Literal>]
let private Gutter = 8

/// Rounds `value` to the nearest multiple of 8, biasing up on an exact tie so
/// the result is deterministic (used only where a screen dimension does not
/// divide evenly into the grid — see `splitInHalf` below).
let private snapToGrid8 (value: int) : int =
  let lower = (value / 8) * 8
  let upper = lower + 8
  if value - lower <= upper - value then lower else upper

/// Splits `total` into two panes separated by an 8px gutter, both snapped to
/// the 8px grid, that still sum exactly back to `total` (§9: "tiling
/// exactly"). §9 states the two right-column panes of `EditorLeft` as an
/// identical "568×356" — but 356 is not itself a multiple of 8 (720 - 8 = 712
/// does not halve into two multiples of 8), so an exact 8px-grid tiling
/// necessarily makes the two panes 8px apart in size (352/360) rather than
/// perfectly equal. Grid alignment and exact tiling are the load-bearing
/// invariants (both are property-tested); the two panes are still within 8px
/// of the plan's stated size.
let private splitInHalf (total: int) : int * int =
  let available = total - Gutter
  let first = (available / 2 / 8) * 8
  let second = available - first
  first, second

/// The placed rect for every actor in a scenario using `template`, on a
/// screen of the given size.
let rects (template: LayoutTemplate) (screen: Screen) : Map<ActorId, Rect> =
  match template with
  | LayoutTemplate.DashboardOnly ->
    // §9: "1280×720" — the dashboard alone fills the whole screen (sessions,
    // agent scenarios: no separate editor or app pane).
    Map.ofList [ ActorId.Dashboard, { X = 0; Y = 0; W = screen.Width; H = screen.Height } ]

  | LayoutTemplate.EditorFull ->
    // §9: "editor 896×720 · dashboard 376×720" — one 8px gutter between the
    // two full-height columns (REPL and live-testing scenarios, where the
    // app pane would be empty).
    let editorW = snapToGrid8 (screen.Width * 896 / 1280)
    let dashboardW = screen.Width - Gutter - editorW
    let editor = { X = 0; Y = 0; W = editorW; H = screen.Height }
    let dashboard = { X = editorW + Gutter; Y = 0; W = dashboardW; H = screen.Height }
    Map.ofList [ ActorId.VsCode, editor; ActorId.Neovim, editor; ActorId.Dashboard, dashboard ]

  | LayoutTemplate.EditorLeft ->
    // §9: "editor 704×720 · dashboard 568×356 (top right) · app 568×356
    // (bottom right)" — the story reads left→right: cause, then SageFs, then
    // effect. One 8px gutter separates the editor column from the right
    // column; another separates the dashboard (top) from the app (bottom).
    let editorW = snapToGrid8 (screen.Width * 704 / 1280)
    let rightW = screen.Width - Gutter - editorW
    let dashboardH, appH = splitInHalf screen.Height
    let editor = { X = 0; Y = 0; W = editorW; H = screen.Height }
    let dashboard = { X = editorW + Gutter; Y = 0; W = rightW; H = dashboardH }
    let app = { X = editorW + Gutter; Y = dashboardH + Gutter; W = rightW; H = appH }
    Map.ofList [ ActorId.VsCode, editor; ActorId.Neovim, editor; ActorId.Dashboard, dashboard; ActorId.App, app ]
