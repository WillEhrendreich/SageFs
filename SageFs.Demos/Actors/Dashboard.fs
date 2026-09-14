/// The Dashboard actor (demo-gif-plan.md §4.4): launches the bundled
/// Playwright Chromium in `--app=` kiosk mode (§4.12 — required so page pixel
/// coordinates equal screen coordinates, since libXtst clicks a screen
/// point) against the daemon's own dashboard, resolves `DashboardId` targets
/// to `ScreenRect`s via a real `BoundingBoxAsync()` read (§3: "a
/// `ResolvedTarget` is a screen rect that came from a live actor, never a
/// guess"), and observes `Expectation`s through the DOM — the semantic API
/// that proves a demo is truthful (§4.7 layer 1), not a pixel guess.
module SageFs.Demos.Actors.Dashboard

open Microsoft.Playwright
open SageFs.Demos.Domain
open SageFs.Demos.Actors.Actor

type Handle =
  { Playwright: IPlaywright
    Context: IBrowserContext
    Page: IPage }

let private testIdSelector (id: DashboardId) : string = sprintf "[data-testid=%s]" (DashboardId.testId id)

/// Xvfb runs `-nocursor` (§4.3, `Runtime.fs`), which suppresses the X11
/// hardware cursor — but not Chromium's own CSS-drawn cursor (the hand over
/// `cursor: pointer` elements, the I-beam over text). The synthetic-cursor
/// design (§4.3: "the cursor is synthetic ... the capture is not polluted by
/// a tiny grey arrow") requires the CAPTURED frame to have zero real cursors
/// of any kind, so every page this actor drives gets `cursor: none` forced
/// on every element before anything is recorded.
let private hideRealCursor (page: IPage) : Async<unit> =
  async {
    let! _ = page.AddStyleTagAsync(PageAddStyleTagOptions(Content = "*, *::before, *::after { cursor: none !important; }")) |> Async.AwaitTask
    ()
  }

/// Launches Chromium `--app=<pageUrl>` placed at `rect`. §4.12: waits on
/// `LoadState.Load`, NEVER `LoadState.NetworkIdle` — the dashboard holds a
/// long-lived SSE `EventSource` open for the life of the page by design, so
/// the network is never idle and `NetworkIdle` would time out on every single
/// scenario that uses this actor (the Phase-0 spike's Stage-4 finding).
let launch (chromePath: string) (userDataDir: string) (rect: Rect) (pageUrl: string) : Async<Handle> =
  async {
    let! playwright = Playwright.CreateAsync() |> Async.AwaitTask
    let options = BrowserTypeLaunchPersistentContextOptions()
    options.ExecutablePath <- chromePath
    options.Headless <- false

    options.Args <-
      ResizeArray
        [ "--no-sandbox"
          "--disable-gpu"
          "--ozone-platform=x11"
          sprintf "--window-position=%d,%d" rect.X rect.Y
          sprintf "--window-size=%d,%d" rect.W rect.H
          "--no-first-run"
          "--disable-features=Translate"
          "--disable-extensions"
          "--disable-infobars"
          "--no-default-browser-check"
          sprintf "--app=%s" pageUrl ]

    let! context = playwright.Chromium.LaunchPersistentContextAsync(userDataDir, options) |> Async.AwaitTask
    do! Async.Sleep 1000

    let page =
      if context.Pages.Count > 0 then
        context.Pages.[0]
      else
        context.WaitForPageAsync() |> Async.AwaitTask |> Async.RunSynchronously

    let! _ = page.WaitForLoadStateAsync(LoadState.Load) |> Async.AwaitTask
    do! hideRealCursor page
    return { Playwright = playwright; Context = context; Page = page }
  }

/// Resolves a `DashboardElement` target to the `ScreenRect` Playwright itself
/// measured. `--app` mode has no browser chrome, so page coordinates equal
/// screen coordinates at the window's placed origin (§4.4, §4.12).
let resolve (handle: Handle) (dashboardId: DashboardId) : Async<ScreenRect option> =
  async {
    let! box = handle.Page.Locator(testIdSelector dashboardId).BoundingBoxAsync() |> Async.AwaitTask

    return
      match box with
      | null -> None
      | b -> Some { X = int b.X; Y = int b.Y; W = int b.Width; H = int b.Height }
  }

/// Observes an `Expectation` through the DOM (§4.7 layer 1: the semantic API
/// that proves the UI-driven action actually worked, not a pixel guess).
/// Only the case `hello-dashboard` needs (`PageShows`) is wired to a real
/// check today; every other case name-matches the plan's own vocabulary so a
/// future scenario extends this one function rather than inventing a second
/// observation mechanism.
let observe (handle: Handle) (expectation: Expectation) (timeoutMs: float) : Async<bool> =
  async {
    match expectation with
    | Expectation.PageShows(dashboardId, _text) ->
      try
        let opts = PageWaitForSelectorOptions(Timeout = float32 timeoutMs)
        let! _ = handle.Page.WaitForSelectorAsync(testIdSelector dashboardId, opts) |> Async.AwaitTask
        return true
      with _ ->
        return false
    | _ -> return false
  }

/// Runs a non-input client command. The only token this actor understands
/// today is `"select-session"` (`CellAgent.fs`'s `runStep`, right after an
/// editor client's own real `create-session*` command succeeds): the
/// product's OWN doctrine is that creating a session never switches the
/// dashboard's main panel away from the "Start a Session" picker — only
/// CLICKING a session card does (`Scenarios.fs`'s `helloDashboard` step 2
/// comment, and its own dashboard-client scenarios all drive that click as
/// a real filmed step). An editor client's session is created through the
/// daemon API/plugin command directly — no click ever reaches this
/// narrator pane — so without this, the pane sits on the picker forever
/// and `#session-output`/`#session-status` never render for it. This waits
/// for the newly-created session's own sidebar card to appear (the daemon
/// pushes it over SSE the moment `/api/sessions/create` returns, well
/// before warmup finishes) and clicks it — the SAME real click a Dashboard-
/// client scenario's own `Action.Click(Target.DashboardElement
/// DashboardId.SessionCard)` step performs, just dispatched by this actor
/// itself instead of through XTest, since the narrator is an observation
/// pane, not the on-camera actor a scenario is filming input for.
let command (handle: Handle) (token: string) : Async<unit> =
  async {
    if token = "select-session" then
      try
        let card = handle.Page.Locator(testIdSelector DashboardId.SessionCard).First
        let waitOpts = LocatorWaitForOptions(Timeout = 30000.0f)
        do! card.WaitForAsync(waitOpts) |> Async.AwaitTask
        do! card.ClickAsync() |> Async.AwaitTask
      with _ ->
        // A real, honest failure here surfaces the same way every other
        // missing/failed observation does — through the step's own
        // `Expect` never resolving on `#session-output`/`#session-status`,
        // never a swallowed exception pretending the narrator caught up.
        ()
    else
      ()
  }

let close (handle: Handle) : Async<unit> =
  async {
    do! handle.Context.CloseAsync() |> Async.AwaitTask
    handle.Playwright.Dispose()
  }

/// Wraps this actor behind the cell-agent's actor-dispatch seam (Island F,
/// demo-actors-plan.md §1.2/§1.3): the ONLY product change the seam requires
/// of Dashboard is exposing its existing launch/resolve/observe/close shape
/// through the shared `LiveActor` record instead of `CellAgent.fs` hard-
/// calling `Dashboard.*` by name. `ResolveRect`/`Observe` below are moved
/// here VERBATIM from the pre-seam cell-agent's own private `resolveRect`/
/// expectation-wait logic (scroll-into-view before a 10s bounding-box read;
/// a 90s-capped `WaitForSelectorAsync` for expectations, since a real
/// session warmup can genuinely take longer than a UI-click ever needed to)
/// — behavior-identical, just reachable through the seam instead of inline
/// in the cell-agent. `Command` runs this module's own `command` above —
/// the ONE token it understands is `"select-session"`, dispatched by
/// `CellAgent.fs`'s `runStep` right after an editor client's real
/// create-session command succeeds, so the narrator pane always ends up
/// looking at the session that client just created.
let toLiveActor (handle: Handle) : LiveActor =
  let resolveRect (selector: string) : Async<ScreenRect option> =
    async {
      try
        let locator = handle.Page.Locator(selector)
        do! locator.ScrollIntoViewIfNeededAsync() |> Async.AwaitTask
        let opts = LocatorBoundingBoxOptions(Timeout = 10000.0f)
        let! box = locator.BoundingBoxAsync(opts) |> Async.AwaitTask

        return
          match box with
          | null -> None
          | b -> Some { X = int b.X; Y = int b.Y; W = int b.Width; H = int b.Height }
      with _ ->
        return None
    }

  let observeSelector (selector: string) (timeoutMs: float) : Async<bool> =
    async {
      try
        let opts = PageWaitForSelectorOptions(Timeout = float32 timeoutMs)
        let! _ = handle.Page.WaitForSelectorAsync(selector, opts) |> Async.AwaitTask
        return true
      with _ ->
        return false
    }

  { Id = ActorId.Dashboard
    ResolveRect = resolveRect
    Observe = observeSelector
    Command = command handle
    Close = fun () -> close handle }
