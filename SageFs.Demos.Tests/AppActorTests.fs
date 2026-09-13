/// RED-first proof for the App co-actor (demo-actors-plan.md §2.3): every
/// test here spawns a REAL private Xvfb display and a REAL second X11
/// window on it — never a mock display, never a fabricated rect — so a
/// passing run is genuine proof that `Actors.App` resolves a live window's
/// geometry and observes a live pixel change, not that the types merely
/// compile. Skipped loud (never a silent pass) when the host is missing
/// `Xvfb` — the fail-loud external-dependency doctrine (§2) applies to the
/// test harness itself, not only to the product code.
///
/// The "second window" here is a tiny, purpose-built Xlib client this file
/// spawns on its own connection (`FakeAppWindow` below) rather than a real
/// browser: `Actors.App.launch`'s `Raylib`/`Console` path (the ONLY path
/// that does its own X11 window discovery — `Web` never touches X11 at all,
/// it drives Chromium through `Actors/Dashboard.fs`'s Playwright handle) is
/// exercised against exactly the shape those kinds actually produce — one
/// stable top-level window that maps once and stays put — which is a more
/// faithful stand-in for "the app the daemon's run-app started" than a real
/// browser's own multi-window, multi-generation startup churn under
/// software rendering.
module SageFs.Demos.Tests.AppActorTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open Expecto
open Expecto.Flip
open SageFs.Demos.Domain
open SageFs.Demos.Actors

/// Test-only X11 helpers: a private Xvfb spawner, a minimal real top-level
/// window ("the app"), and a raw drawing call used ONLY to simulate "the
/// app visibly changed its own output". Deliberately kept out of
/// `Actors/App.fs` (which never creates or draws into a window; it only
/// ever WATCHES one) — a genuinely separate concern from the actor under
/// test, so it stays test-local rather than growing the product's own
/// P/Invoke surface for a capability only a test harness needs.
module private TestX11 =
  [<DllImport("libX11.so.6")>]
  extern nativeint XOpenDisplay(string display)

  [<DllImport("libX11.so.6")>]
  extern int XCloseDisplay(nativeint display)

  [<DllImport("libX11.so.6")>]
  extern nativeint XDefaultRootWindow(nativeint display)

  [<DllImport("libX11.so.6")>]
  extern nativeint XCreateSimpleWindow(nativeint display, nativeint parent, int x, int y, uint32 width, uint32 height, uint32 borderWidth, unativeint border, unativeint background)

  [<DllImport("libX11.so.6")>]
  extern int XMapWindow(nativeint display, nativeint w)

  [<DllImport("libX11.so.6")>]
  extern int XDestroyWindow(nativeint display, nativeint w)

  [<DllImport("libX11.so.6")>]
  extern nativeint XCreateGC(nativeint display, nativeint d, unativeint valuemask, nativeint values)

  [<DllImport("libX11.so.6")>]
  extern int XSetForeground(nativeint display, nativeint gc, unativeint foreground)

  [<DllImport("libX11.so.6")>]
  extern int XFillRectangle(nativeint display, nativeint d, nativeint gc, int x, int y, uint32 width, uint32 height)

  [<DllImport("libX11.so.6")>]
  extern int XFreeGC(nativeint display, nativeint gc)

  [<DllImport("libX11.so.6")>]
  extern int XFlush(nativeint display)

  [<DllImport("libX11.so.6")>]
  extern int XSync(nativeint display, bool discard)

/// A minimal, real, stable top-level X11 window this test owns end to end —
/// the "second window a session launches" stand-in `Actors.App`'s
/// `Raylib`/`Console` discovery path must find, place, and observe. Created
/// on its OWN connection (a real second X11 client on the display, exactly
/// the topology the actor faces against a genuinely separate app process),
/// mapped once, and never torn down or replaced until `Dispose` — unlike a
/// real browser's own startup window churn, this never confounds the
/// discovery/capture proof with a second concern this island does not own.
type private FakeAppWindow(displayName: string, initialRect: Rect) =
  let display = TestX11.XOpenDisplay displayName

  do
    if display = IntPtr.Zero then
      failwithf "FakeAppWindow: could not open display '%s'" displayName

  let root = TestX11.XDefaultRootWindow display

  let window =
    TestX11.XCreateSimpleWindow(
      display,
      root,
      initialRect.X,
      initialRect.Y,
      uint32 initialRect.W,
      uint32 initialRect.H,
      0u,
      unativeint 0UL,
      unativeint 0x000000UL
    )

  do
    TestX11.XMapWindow(display, window) |> ignore
    TestX11.XFlush display |> ignore
    TestX11.XSync(display, false) |> ignore

  member _.Window = window

  /// Draws a solid, opaque rectangle covering the whole window via a real
  /// `XFillRectangle` core-protocol request — a genuine, externally-caused
  /// visible change `Actors.App.observe`'s pixel fingerprint must detect.
  member _.Paint(color: uint32) =
    let gc = TestX11.XCreateGC(display, window, unativeint 0UL, IntPtr.Zero)
    TestX11.XSetForeground(display, gc, unativeint color) |> ignore
    TestX11.XFillRectangle(display, window, gc, 0, 0, 4000u, 4000u) |> ignore
    TestX11.XFlush display |> ignore
    TestX11.XSync(display, false) |> ignore
    TestX11.XFreeGC(display, gc) |> ignore

  interface IDisposable with
    member _.Dispose() =
      TestX11.XDestroyWindow(display, window) |> ignore
      TestX11.XCloseDisplay display |> ignore

/// Picks a display number this test's Xvfb can safely claim (never `:0`,
/// the host's own live desktop this box is running — corrupting it would be
/// a real regression, not just a flaky test) and that has no existing X11
/// socket, so parallel test runs never collide with each other or with a
/// real cell.
let private freeDisplayNumber () : int =
  let rec pick attempt =
    let n = 150 + (Random.Shared.Next 400)
    if File.Exists(sprintf "/tmp/.X11-unix/X%d" n) && attempt < 50 then pick (attempt + 1) else n
  pick 0

let private startProcess (fileName: string) (args: string list) : Process option =
  try
    let psi = ProcessStartInfo(fileName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    for a in args do
      psi.ArgumentList.Add a
    let proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    Some proc
  with _ ->
    None

/// A private Xvfb this test owns end to end: started here, torn down by the
/// caller's `Dispose`. `IsReady = false` (with the test skipped, never faked
/// green) if `Xvfb` never produces a socket — the harness itself fails loud
/// rather than silently asserting nothing.
type private TestDisplay(displayNum: int) =
  let displayName = sprintf ":%d" displayNum
  let socketPath = sprintf "/tmp/.X11-unix/X%d" displayNum
  let proc = startProcess "Xvfb" [ displayName; "-screen"; "0"; "1280x720x24"; "-nocursor" ]

  let ready =
    match proc with
    | None -> false
    | Some _ ->
      let sw = Stopwatch.StartNew()
      let mutable up = false
      while not up && sw.ElapsedMilliseconds < 10_000L do
        if File.Exists socketPath then up <- true else Threading.Thread.Sleep 100
      up

  member _.DisplayName = displayName
  member _.IsReady = ready

  interface IDisposable with
    member _.Dispose() =
      match proc with
      | Some p ->
        try
          if not p.HasExited then
            p.Kill()
            p.WaitForExit 3000 |> ignore
        with _ ->
          ()
      | None -> ()

let private xvfbAvailable =
  startProcess "Xvfb" [ "-help" ]
  |> Option.map (fun p ->
    p.WaitForExit 2000 |> ignore
    true)
  |> Option.defaultValue false

let private baseConfig (displayName: string) : App.LaunchConfig =
  { Display = displayName
    AppUrl = None
    ChromePath = "/usr/bin/chromium"
    UserDataDir = ""
    ReadyTimeoutMs = 15_000 }

[<Tests>]
let tests =
  testSequenced (
    testList "App actor" [

      testCase "AppKind.NoApp fails loud instead of silently launching nothing" <| fun _ ->
        let config = baseConfig ":1234" // never opened — NoApp must short-circuit before touching X11
        let result = App.launch config { X = 0; Y = 0; W = 100; H = 100 } AppKind.NoApp |> Async.RunSynchronously

        match result with
        | Error message -> message |> Expect.stringContains "explains why NoApp cannot launch" "NoApp"
        | Ok _ -> failwith "AppKind.NoApp must never succeed — there is no window to capture"

      testCase "AppKind.Web with no resolved AppUrl fails loud instead of hanging" <| fun _ ->
        let config = { baseConfig ":1234" with AppUrl = None }
        let result = App.launch config { X = 0; Y = 0; W = 100; H = 100 } AppKind.Web |> Async.RunSynchronously

        match result with
        | Error message -> message |> Expect.stringContains "explains the missing AppUrl" "AppUrl"
        | Ok _ -> failwith "AppKind.Web with no AppUrl must never succeed"

      testCase "a genuinely absent second window times out with an actionable Error, never a hang" <| fun _ ->
        if not xvfbAvailable then
          skiptest "Xvfb not available on this host"
        else

        let displayNum = freeDisplayNumber ()
        use display = new TestDisplay(displayNum)

        if not display.IsReady then
          skiptest "Xvfb did not become ready"
        else

        // No second window is ever created on this display — the poll must
        // fail loud on its own short timeout, not hang the test suite.
        let config = { baseConfig display.DisplayName with ReadyTimeoutMs = 800 }
        let sw = Stopwatch.StartNew()
        let result = App.launch config { X = 0; Y = 0; W = 200; H = 200 } AppKind.Console |> Async.RunSynchronously
        sw.Stop()

        sw.ElapsedMilliseconds < 5000L |> Expect.isTrue "the poll actually respected its own timeout instead of hanging"

        match result with
        | Error message -> message |> Expect.stringContains "explains no window ever appeared" "window"
        | Ok _ -> failwith "no window was ever created on this display — Ok is impossible here"

      // The two RED-gate tests demo-actors-plan.md §2.3 requires: (a) a host
      // geometry-harness proof that the actor resolves a REAL second-window
      // rect it never guessed, and (b) proof it can attach to and CAPTURE a
      // real launched window (a genuine, externally-caused pixel change).
      // Combined into one scenario because both need the same live window.
      testCase "resolves a real second-window rect and genuinely observes a live pixel change on it (RED until the actor attaches for real)" <| fun _ ->
        if not xvfbAvailable then
          skiptest "Xvfb not available on this host"
        else

        let displayNum = freeDisplayNumber ()
        use display = new TestDisplay(displayNum)

        if not display.IsReady then
          skiptest "Xvfb did not become ready"
        else

        let targetRect: Rect = { X = 40; Y = 20; W = 640; H = 480 }
        let config = { baseConfig display.DisplayName with ReadyTimeoutMs = 20_000 }

        // Launch starts POLLING for a new window BEFORE the "app" (a real
        // second X11 window on this same display — exactly the topology the
        // actor faces against whatever the daemon's run-app actually
        // starts) is created, a beat later — proof this is a genuine live
        // `XQueryTree` diff, not "whatever window happened to already
        // exist".
        let launchTask = App.launch config targetRect AppKind.Console |> Async.StartAsTask
        Threading.Thread.Sleep 500
        use fakeApp = new FakeAppWindow(display.DisplayName, { X = 5; Y = 5; W = 300; H = 200 })

        let result = launchTask.Result

        match result with
        | Error message -> failwithf "App actor failed to attach to the real launched window: %s" message
        | Ok handle ->
          try
            // (a) the geometry harness: a REAL rect, live-queried, not a
            // guess — and it must actually equal where the actor placed
            // the window (proves the X11 move/resize round-tripped, not
            // just "some rect came back").
            let resolved = App.resolveRect handle "ignored" |> Async.RunSynchronously

            match resolved with
            | None -> failwith "resolveRect returned None for a window the actor just attached to"
            | Some rect ->
              rect.W > 0 |> Expect.isTrue "resolved rect has a real, positive width"
              rect.H > 0 |> Expect.isTrue "resolved rect has a real, positive height"
              rect |> Expect.equal "the live-queried rect matches exactly where the actor placed the window" targetRect

            handle.Window |> Expect.equal "the actor attached to the exact window this test created" (Some fakeApp.Window)

            // (b) the capture proof: paint the REAL window a solid color
            // through an entirely separate X11 connection (simulating the
            // app visibly changing its own output) and confirm the actor
            // genuinely observes it — a true positive.
            let observeTask = App.observe handle "ignored" 10_000.0 |> Async.StartAsTask
            Threading.Thread.Sleep 300
            fakeApp.Paint 0xFF00FFu

            observeTask.Result |> Expect.isTrue "the actor genuinely observed the real, externally-painted pixel change"

            // Negative control: with nothing changing, observe must
            // honestly report false rather than always returning true —
            // proves this isn't a rigged/trivial detector.
            App.observe handle "ignored" 1200.0
            |> Async.RunSynchronously
            |> Expect.isFalse "with no further change, the actor honestly reports no observed change"
          finally
            App.close handle |> Async.RunSynchronously
    ]
  )
