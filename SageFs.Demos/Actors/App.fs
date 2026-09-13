/// The App co-actor (demo-actors-plan.md §2.3): captures the SECOND window a
/// session launches when hot-reload runs the app under test. Unlike every
/// other actor, App authors no scenarios of its own — it is a passenger
/// placed at `ActorId.App` in the `EditorLeft` layout (`Layout.fs:69`) and
/// driven only as a co-actor inside hot-reload scenarios the editor islands
/// author. Per §2.3: "the App actor only *finds, places, and observes* the
/// window the session spawned; it does not start it" — the daemon's own
/// run-app endpoint (`/api/sessions/{sid}/run-app`) is what actually starts
/// the app; this module never spawns the app process itself.
///
/// Three `AppKind`s, two real capture mechanisms:
///  - `Web`: the app is a website. This actor opens its OWN second Chromium
///    (`Actors/Dashboard.fs`'s exact `--app=` kiosk launch shape, reused
///    verbatim, never rewritten — §1's "reusable, actor-agnostic edges")
///    pointed at the app's URL, placed at the co-actor's rect. Content
///    changes are observed via a real Playwright screenshot diff — the
///    plan's own words, "region diff via ... Playwright for web".
///  - `Raylib`/`Console`: the app draws its own window directly on the
///    shared `:99` display (a GUI window for Raylib; a terminal surface for
///    Console) — this actor never opens a browser for these. It watches the
///    display's root window for a NEW top-level window to appear (a real,
///    live `XQueryTree` diff — the exact "attach to a real launched window"
///    proof the RED tests demand, never a guess at a window id), moves/
///    resizes it into the co-actor's rect via `XMoveResizeWindow`, and
///    observes content changes via a real `XGetImage` pixel-fingerprint diff
///    — the plan's "region diff" mechanism, implemented for the case that
///    has no DOM to query.
///
/// Deliberately its own small, self-contained P/Invoke surface (`Native`
/// below) rather than growing `XTest.fs`'s contract: `XTest.fs` is the
/// input-delivery edge every actor shares verbatim (§1 — "do NOT rewrite");
/// window discovery/placement/pixel-capture is a DIFFERENT concern only this
/// actor needs, so it does not force every other actor's shared edge to grow
/// a window-management API it will never use.
module SageFs.Demos.Actors.App

open System
open System.Net.Http
open System.Runtime.InteropServices
open SageFs.Demos.Domain
open SageFs.Demos.Actors
open SageFs.Demos.Actors.Actor

/// Xlib's DEFAULT error handler does not just print an `XErrorEvent` — it
/// calls `exit(1)` on the whole process for anything it does not recognize
/// as one of a handful of always-ignorable cases. A transient, genuinely
/// recoverable protocol error (§4.11's own doctrine: "a real X protocol
/// error is a real bug to fix, never something to guard-and-ignore" governs
/// XTEST *input delivery*; a capture race against a window's own map/resize
/// timing is a DIFFERENT, expected transient — e.g. `XGetImage`'s BadMatch
/// on a window this actor just discovered via `XQueryTree` a moment before
/// it finished mapping) must never be allowed to kill the actor, let alone
/// the whole recording run. Installing a handler that returns instead of
/// aborting turns every such error into an ordinary failed call (a NULL/zero
/// return this module already treats as `None`/`false`), which is the
/// correct, honest outcome for a transient capture race.
type private XErrorHandler = delegate of nativeint * nativeint -> int

module private Native =
  [<DllImport("libX11.so.6")>]
  extern nativeint XOpenDisplay(string display)

  [<DllImport("libX11.so.6")>]
  extern nativeint XSetErrorHandler(XErrorHandler handler)

  [<DllImport("libX11.so.6")>]
  extern int XCloseDisplay(nativeint display)

  [<DllImport("libX11.so.6")>]
  extern nativeint XDefaultRootWindow(nativeint display)

  [<DllImport("libX11.so.6")>]
  extern int XQueryTree(nativeint display, nativeint w, nativeint& root_return, nativeint& parent_return, nativeint& children_return, uint32& nchildren_return)

  [<DllImport("libX11.so.6")>]
  extern int XFree(nativeint data)

  [<DllImport("libX11.so.6")>]
  extern int XGetGeometry(nativeint display, nativeint d, nativeint& root_return, int& x_return, int& y_return, uint32& width_return, uint32& height_return, uint32& border_width_return, uint32& depth_return)

  [<DllImport("libX11.so.6")>]
  extern int XMoveResizeWindow(nativeint display, nativeint w, int x, int y, uint32 width, uint32 height)

  [<DllImport("libX11.so.6")>]
  extern int XSync(nativeint display, bool discard)

  [<DllImport("libX11.so.6")>]
  extern nativeint XGetImage(nativeint display, nativeint d, int x, int y, uint32 width, uint32 height, unativeint plane_mask, int format)

  [<DllImport("libX11.so.6")>]
  extern unativeint XGetPixel(nativeint ximage, int x, int y)

  [<DllImport("libX11.so.6")>]
  extern int XDestroyImage(nativeint ximage)

[<Literal>]
let private ZPixmap = 2

/// Xlib's `AllPlanes` macro (`((unsigned long)~0L)`) — every plane, i.e. the
/// full pixel value, not a masked subset.
let private allPlanes: unativeint = unativeint UInt64.MaxValue

/// Kept alive for the whole process (a delegate passed to native code is
/// otherwise eligible for GC as soon as this expression finishes, leaving
/// Xlib holding a dangling function pointer the moment it next errors).
let mutable private errorHandler: XErrorHandler = Unchecked.defaultof<_>

/// Installs the non-aborting error handler (module doc above) exactly once
/// per process — idempotent, since a second `XSetErrorHandler` call is
/// harmless but pointless. Called before this module's first real X11
/// connection so no window-management call can ever take the process down.
let private installErrorHandler =
  lazy
    (errorHandler <- XErrorHandler(fun _ _ -> 0)
     Native.XSetErrorHandler errorHandler |> ignore)

/// The live children of `window`, in server order — with no window manager
/// running inside a cell (`Runtime.fs`'s inner script starts only Xvfb, never
/// a WM), every app's own top-level window is a DIRECT child of the root
/// window, so this is exactly the set of "top-level app windows" on the
/// display, no EWMH `_NET_CLIENT_LIST` (which only a WM publishes) required.
let private queryChildren (display: nativeint) (window: nativeint) : nativeint list =
  let mutable root = IntPtr.Zero
  let mutable parent = IntPtr.Zero
  let mutable children = IntPtr.Zero
  let mutable count = 0u

  let status = Native.XQueryTree(display, window, &root, &parent, &children, &count)

  if status = 0 || children = IntPtr.Zero then
    []
  else
    try
      [ for i in 0 .. int count - 1 -> Marshal.ReadIntPtr(children, i * IntPtr.Size) ]
    finally
      Native.XFree children |> ignore

/// `window`'s CURRENT geometry, always queried live against the display —
/// never cached — per §3's "a `ResolvedTarget` is a screen rect that came
/// from a live actor, never a guess". `None` only when the window is already
/// gone (a real, honest failure to report, not a stale rect).
let private geometryOf (display: nativeint) (window: nativeint) : Rect option =
  let mutable root = IntPtr.Zero
  let mutable x = 0
  let mutable y = 0
  let mutable width = 0u
  let mutable height = 0u
  let mutable border = 0u
  let mutable depth = 0u

  let status =
    Native.XGetGeometry(display, window, &root, &x, &y, &width, &height, &border, &depth)

  if status = 0 then None else Some { X = x; Y = y; W = int width; H = int height }

let private placeWindow (display: nativeint) (window: nativeint) (rect: Rect) : unit =
  Native.XMoveResizeWindow(display, window, rect.X, rect.Y, uint32 rect.W, uint32 rect.H)
  |> ignore

  Native.XSync(display, false) |> ignore

/// A cheap, REAL content fingerprint of `window`'s current on-screen pixels
/// (never faked, per the repo's "a demo step must genuinely pass" doctrine):
/// grabs one real `XImage` of the window's current rect and folds an FNV-1a
/// hash over a sparse grid of real sampled pixels (every 8th row/column —
/// full-resolution capture is unnecessary to answer "did this visibly
/// change", and sparse sampling keeps a per-poll capture cheap enough for a
/// tight observation loop). `None` only on a real capture failure (the
/// window has already gone away, or shrunk to zero) — a genuine signal, not
/// something papered over as "unchanged".
let private capturePixelFingerprint (display: nativeint) (window: nativeint) (rect: Rect) : uint64 option =
  if rect.W <= 0 || rect.H <= 0 then
    None
  else
    let image = Native.XGetImage(display, window, 0, 0, uint32 rect.W, uint32 rect.H, allPlanes, ZPixmap)

    if image = IntPtr.Zero then
      None
    else
      try
        let mutable hash = 1469598103934665603UL // FNV-1a offset basis
        let step = 8

        for py in 0 .. step .. rect.H - 1 do
          for px in 0 .. step .. rect.W - 1 do
            let pixel = uint64 (Native.XGetPixel(image, px, py))
            hash <- (hash ^^^ pixel) * 1099511628211UL // FNV-1a prime

        Some hash
      finally
        Native.XDestroyImage image |> ignore

/// The minimum plausible content-window side, in pixels — small enough to
/// never reject a genuinely tiny real app window, large enough to skip the
/// 1x1/unset-size helper windows a real GUI toolkit routinely creates
/// alongside its real one (a WM-client-leader window, an IPC/drag-and-drop
/// proxy, ...) — none of which is ever a window a scenario wants captured.
[<Literal>]
let private MinPlausibleWindowSide = 8

/// Among a set of candidate windows, the one with the largest current area —
/// the best available live signal for "the real content window" versus a
/// same-process helper window, since a toolkit's own hint/proxy windows are
/// routinely created at a degenerate 1x1 (or unset) size while a real
/// top-level content window is not. Only windows at least
/// `MinPlausibleWindowSide` on each side are even considered; `None` if
/// every candidate is still too small to judge (the poll loop keeps going
/// rather than locking onto a decoy).
let private largestPlausibleWindow (display: nativeint) (candidates: nativeint list) : nativeint option =
  candidates
  |> List.choose (fun w -> geometryOf display w |> Option.map (fun r -> w, r))
  |> List.filter (fun (_, r) -> r.W >= MinPlausibleWindowSide && r.H >= MinPlausibleWindowSide)
  |> List.sortByDescending (fun (_, r) -> r.W * r.H)
  |> List.tryHead
  |> Option.map fst

/// Polls the display's root window until a window id NOT in `before`
/// appears AND is plausibly a real content window (`largestPlausibleWindow`
/// above — never just "whichever window id happened to sort first", which a
/// same-process helper window can just as easily be), or fails loud with an
/// actionable message on timeout — the "never a silent green no-op"
/// doctrine (§2): a missing/never-mapped app window is reported as a real
/// `Error`, never treated as "nothing to capture".
let private pollForNewWindow (display: nativeint) (root: nativeint) (before: Set<nativeint>) (timeoutMs: int) : Async<Result<nativeint, string>> =
  async {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable found: nativeint option = None

    while found.IsNone && sw.ElapsedMilliseconds < int64 timeoutMs do
      let fresh = (queryChildren display root |> Set.ofList) - before

      match largestPlausibleWindow display (fresh |> Set.toList) with
      | Some w -> found <- Some w
      | None -> do! Async.Sleep 200

    match found with
    | Some w -> return Ok w
    | None ->
      return
        Error(
          sprintf
            "no new X11 window appeared on the display within %dms — the run-app-launched app never mapped a top-level window (is run-app actually wired to start it before the App actor waits?)"
            timeoutMs
        )
  }

let private waitForHttpReady (url: string) (timeoutMs: int) : Async<Result<unit, string>> =
  async {
    use client = new HttpClient(Timeout = TimeSpan.FromSeconds 2.0)
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable ok = false

    while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
      try
        let! resp = client.GetAsync(url) |> Async.AwaitTask
        // Any response at all — even a 4xx/5xx from the app's own router —
        // proves a real HTTP server is listening; only a connection failure
        // (server not up yet) should keep polling.
        ok <- true
        resp.Dispose()
      with _ ->
        ()

      if not ok then
        do! Async.Sleep 300

    if ok then
      return Ok()
    else
      return Error(sprintf "app URL %s never answered within %dms — did run-app actually start the web sample?" url timeoutMs)
  }

/// Fully-resolved launch configuration (Runtime.App.fs builds this from the
/// repo/`Wire.AppConfig` once the seam wires it in — this actor itself never
/// resolves a path, a port, or an env var, exactly like `Actors/Dashboard.fs`
/// takes an already-resolved `chromePath`/`userDataDir` rather than finding
/// them itself).
type LaunchConfig =
  { /// The Xvfb display every actor in the cell shares (`CellAgent.CellDisplay`,
    /// kept a plain string here so this module never depends on the
    /// cell-agent).
    Display: string
    /// `Web` only: the already-resolved URL of the running app (the
    /// daemon's run-app response publishes the port; `Runtime.App.fs`
    /// resolves the full URL from it). Ignored for `Raylib`/`Console`.
    AppUrl: string option
    ChromePath: string
    UserDataDir: string
    /// How long to wait for the run-app-launched window to appear on the
    /// shared display (`Raylib`/`Console`) or for the app URL to answer
    /// (`Web`) before failing loud — never silently hanging forever.
    ReadyTimeoutMs: int }

module LaunchConfig =
  /// A reasonable default: a real session warmup + `dotnet run`-launched
  /// sample can genuinely take longer than a UI click ever needed to
  /// (mirrors `CellAgent.fs`'s own 90s `Observe` ceiling's reasoning).
  let DefaultReadyTimeoutMs = 30_000

/// One live App co-actor: which window it captures, and how — a Chromium
/// page for `Web`, or a raw X11 window handle for `Raylib`/`Console`. Exactly
/// one of `Window`/`Chromium` is populated, matching which kind launched it;
/// never both, never neither (enforced by construction — `launchWeb`/
/// `launchWindowed` are the only producers).
type Handle =
  { Kind: AppKind
    /// `IntPtr.Zero` only for `Web` (Chromium's content is captured through
    /// Playwright, not a raw X11 grab — there is no separate display
    /// connection to close).
    Display: nativeint
    Window: nativeint option
    Chromium: Dashboard.Handle option
    /// The rect this actor was placed at — reported by `resolveRect` for the
    /// `Web` kind only (a `--app` kiosk window has no chrome to move itself,
    /// so the placed rect IS the live rect); `Raylib`/`Console` always
    /// re-query live geometry instead of trusting this.
    PlacedRect: Rect }

let private launchWeb (config: LaunchConfig) (rect: Rect) : Async<Result<Handle, string>> =
  async {
    match config.AppUrl with
    | None -> return Error "AppConfig.Kind = web but no AppUrl was resolved (Runtime.App.fs must supply the run-app-published URL before launching the App actor)"
    | Some url ->
      match! waitForHttpReady url config.ReadyTimeoutMs with
      | Error message -> return Error(sprintf "App(web): %s" message)
      | Ok() ->
        try
          let! chromiumHandle = Dashboard.launch config.ChromePath config.UserDataDir rect url

          return
            Ok
              { Kind = AppKind.Web
                Display = IntPtr.Zero
                Window = None
                Chromium = Some chromiumHandle
                PlacedRect = rect }
        with ex ->
          return Error(sprintf "App(web) failed to launch its own Chromium against %s: %s" url ex.Message)
  }

let private launchWindowed (kind: AppKind) (config: LaunchConfig) (rect: Rect) : Async<Result<Handle, string>> =
  async {
    installErrorHandler.Force()
    let display = Native.XOpenDisplay(config.Display)

    if display = IntPtr.Zero then
      return Error(sprintf "App(%A) could not open X11 display '%s' — is Xvfb running on this cell?" kind config.Display)
    else
      let root = Native.XDefaultRootWindow display
      let before = queryChildren display root |> Set.ofList

      match! pollForNewWindow display root before config.ReadyTimeoutMs with
      | Error message ->
        Native.XCloseDisplay display |> ignore
        return Error(sprintf "App(%A): %s" kind message)
      | Ok window ->
        placeWindow display window rect

        return
          Ok
            { Kind = kind
              Display = display
              Window = Some window
              Chromium = None
              PlacedRect = rect }
  }

/// Launches the App co-actor for `appKind` at `rect` — waiting for (never
/// starting) the window/URL the daemon's run-app already produced, per
/// §2.3's "finds, places, and observes ... does not start it". Fails loud
/// (`Result.Error` with an actionable message), never a silent no-op, for
/// every case the common external-dependency doctrine (§2) requires: no
/// resolved app URL, the URL never answers, no new window ever appears, or
/// `AppKind.NoApp` (which has no window to capture at all — a scenario
/// author error, not something this actor can paper over).
let launch (config: LaunchConfig) (rect: Rect) (appKind: AppKind) : Async<Result<Handle, string>> =
  async {
    match appKind with
    | AppKind.Web -> return! launchWeb config rect
    | AppKind.Raylib -> return! launchWindowed AppKind.Raylib config rect
    | AppKind.Console -> return! launchWindowed AppKind.Console config rect
    | AppKind.NoApp ->
      return Error "App co-actor cannot launch for AppKind.NoApp — a NoApp scenario never places the App actor in its Layout (demo-actors-plan.md §2.3)"
  }

/// Resolves the App co-actor's CURRENT window rect. The `_selector`
/// parameter (the `LiveActor.ResolveRect` contract's own shape) is unused —
/// this actor tracks exactly one window, never a set of selectable elements
/// inside it, so every call reports the same live rect regardless of the
/// string passed.
let resolveRect (handle: Handle) (_selector: string) : Async<ScreenRect option> =
  async {
    match handle.Window with
    | Some window when handle.Display <> IntPtr.Zero -> return geometryOf handle.Display window
    | _ ->
      match handle.Chromium with
      | Some _ ->
        // A `--app` kiosk Chromium window has no browser chrome that could
        // move or resize it out from under `--window-position`/
        // `--window-size` (the exact mechanism `Actors/Dashboard.fs` already
        // relies on) — the placed rect IS the live rect for this kind.
        return Some handle.PlacedRect
      | None -> return None
  }

/// Retries a real pixel capture for up to `graceMs` before giving up — a
/// window this actor just attached to via `pollForNewWindow` can be a beat
/// away from actually being viewable (created, but not yet mapped/painted
/// by its owning process), so the FIRST capture attempt failing is not yet
/// the honest "no baseline exists" answer; only exhausting the grace period
/// is.
let private captureWithRetry (display: nativeint) (window: nativeint) (graceMs: int) : Async<uint64 option> =
  async {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable result = None

    while result.IsNone && sw.ElapsedMilliseconds < int64 graceMs do
      let rect = geometryOf display window |> Option.defaultValue { X = 0; Y = 0; W = 0; H = 0 }
      result <- capturePixelFingerprint display window rect

      if result.IsNone then
        do! Async.Sleep 200

    return result
  }

/// Observes `Signal.AppOutputChanged` via a REAL content diff — an XGetImage
/// pixel fingerprint for `Raylib`/`Console`, a Playwright screenshot for
/// `Web` (the plan's own "region diff ... Playwright for web") — polling
/// until the content genuinely differs from its value at the START of this
/// call, or the timeout elapses. Never a faked "yes" — a capture failure
/// (window already gone) reports `false`, the honest "did not observe a
/// change" outcome.
let observe (handle: Handle) (_selector: string) (timeoutMs: float) : Async<bool> =
  async {
    match handle.Window, handle.Chromium with
    | Some window, _ when handle.Display <> IntPtr.Zero ->
      let rect = geometryOf handle.Display window |> Option.defaultValue handle.PlacedRect
      let graceMs = min 3000 (int timeoutMs)

      match! captureWithRetry handle.Display window graceMs with
      | None -> return false
      | Some baseline ->
        let sw = Diagnostics.Stopwatch.StartNew()
        let mutable changed = false

        while not changed && sw.Elapsed.TotalMilliseconds < timeoutMs do
          do! Async.Sleep 250

          match capturePixelFingerprint handle.Display window rect with
          | Some current when current <> baseline -> changed <- true
          | _ -> ()

        return changed
    | _, Some chromium ->
      try
        let! baselineBytes = chromium.Page.ScreenshotAsync() |> Async.AwaitTask
        let sw = Diagnostics.Stopwatch.StartNew()
        let mutable changed = false

        while not changed && sw.Elapsed.TotalMilliseconds < timeoutMs do
          do! Async.Sleep 250
          let! current = chromium.Page.ScreenshotAsync() |> Async.AwaitTask

          if current <> baselineBytes then
            changed <- true

        return changed
      with _ ->
        return false
    | _ -> return false
  }

let close (handle: Handle) : Async<unit> =
  async {
    match handle.Chromium with
    | Some chromium -> do! Dashboard.close chromium
    | None -> ()

    if handle.Display <> IntPtr.Zero then
      Native.XCloseDisplay handle.Display |> ignore
  }

/// Wraps this actor behind the cell-agent's actor-dispatch seam (Island F,
/// demo-actors-plan.md §1.2). `Command` is a no-op: the App co-actor is
/// never the target of a `ClientCommand` (it does not open files or run
/// itself — the primary actor does that), mirroring `Actors/Dashboard.fs`'s
/// own no-op `Command` today.
let toLiveActor (handle: Handle) : LiveActor =
  { Id = ActorId.App
    ResolveRect = resolveRect handle
    Observe = observe handle
    Command = fun _ -> async { return () }
    Close = fun () -> close handle }
