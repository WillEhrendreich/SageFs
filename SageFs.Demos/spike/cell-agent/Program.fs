module CellAgent.Program

// Stage 4 cell-agent: proves demo-gif-plan.md §4.1's control/data-plane
// boundary literally. This process is meant to run as bwrap's PID 1 inside a
// sealed cell. It never opens a socket to the outside world for control: it
// reads ONE line of ScenarioPlan JSON from stdin and writes ONE line of
// StepLog JSON to stdout when done. The host-side "runner" (stage4 script)
// never reaches into the cell - it only pipes bytes through the process it
// launched via bwrap, which is exactly what stdio redirection gives you for
// free across a pid/net/user namespace wall.
//
// Scope for this spike: one step (open the dashboard, click one element via
// XTEST, observe a second element appear). The real Composer/Actor machinery
// in the plan is not built here - only the boundary and the input edge are
// being proven (Phase 0's two riskiest unknowns per demo-gif-plan.md §10).

open System
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.Playwright

module Native =
    [<DllImport("libX11.so.6")>]
    extern nativeint XOpenDisplay(string display)

    [<DllImport("libX11.so.6")>]
    extern int XCloseDisplay(nativeint display)

    [<DllImport("libX11.so.6")>]
    extern int XFlush(nativeint display)

    [<DllImport("libX11.so.6")>]
    extern int XDefaultScreen(nativeint display)

    [<DllImport("libXtst.so.6")>]
    extern int XTestFakeMotionEvent(nativeint display, int screen, int x, int y, int delay)

    [<DllImport("libXtst.so.6")>]
    extern int XTestFakeButtonEvent(nativeint display, uint32 button, [<MarshalAs(UnmanagedType.Bool)>] bool isPress, int delay)

let clickAt (display: nativeint) (screen: int) (x: int) (y: int) =
    Native.XTestFakeMotionEvent(display, screen, x, y, 0) |> ignore
    Native.XFlush(display) |> ignore
    System.Threading.Thread.Sleep(150)
    Native.XTestFakeButtonEvent(display, 1u, true, 0) |> ignore
    Native.XFlush(display) |> ignore
    System.Threading.Thread.Sleep(60)
    Native.XTestFakeButtonEvent(display, 1u, false, 0) |> ignore
    Native.XFlush(display) |> ignore

// ---- The one JSON contract crossing the namespace wall (demo-gif-plan.md §4.1) ----

type ScenarioPlan =
    { ScenarioId: string
      ChromePath: string
      PageUrl: string
      ClickSelector: string
      ExpectSelector: string
      UserDataDir: string }

type StepLog =
    { ScenarioId: string
      Outcome: string          // "Passed" | "Failed"
      ClickedAt: int[]         // [x, y]
      StartedMs: int64
      EndedMs: int64
      Message: string }

let jsonOptions =
    let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    o

let readPlanFromStdin () : ScenarioPlan =
    let line = Console.In.ReadLine()
    if String.IsNullOrWhiteSpace line then
        failwith "no ScenarioPlan JSON received on stdin"
    JsonSerializer.Deserialize<ScenarioPlan>(line, jsonOptions)

let writeStepLogToStdout (log: StepLog) =
    let json = JsonSerializer.Serialize(log, jsonOptions)
    Console.Out.WriteLine(json)
    Console.Out.Flush()

let run () =
    async {
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let plan = readPlanFromStdin ()

        let display = Native.XOpenDisplay(null)
        if display = IntPtr.Zero then
            return
                { ScenarioId = plan.ScenarioId; Outcome = "Failed"; ClickedAt = [| 0; 0 |]
                  StartedMs = 0L; EndedMs = sw.ElapsedMilliseconds
                  Message = "XOpenDisplay returned NULL - is $DISPLAY set and is Xvfb up?" }
        else
        let screen = Native.XDefaultScreen(display)

        use! playwright = Playwright.CreateAsync() |> Async.AwaitTask
        let launchOptions = BrowserTypeLaunchPersistentContextOptions()
        launchOptions.ExecutablePath <- plan.ChromePath
        launchOptions.Headless <- false
        launchOptions.Args <- ResizeArray [
            "--no-sandbox"; "--disable-gpu"; "--ozone-platform=x11"
            "--window-position=0,0"; "--window-size=1280,720"
            "--no-first-run"; "--disable-features=Translate"; "--disable-extensions"
            "--disable-infobars"; "--no-default-browser-check"
            sprintf "--app=%s" plan.PageUrl
        ]
        let! context =
            playwright.Chromium.LaunchPersistentContextAsync(plan.UserDataDir, launchOptions)
            |> Async.AwaitTask
        do! Async.Sleep 1000
        let page =
            if context.Pages.Count > 0 then context.Pages.[0]
            else context.WaitForPageAsync() |> Async.AwaitTask |> Async.RunSynchronously
        // NOT NetworkIdle: the dashboard holds an open SSE connection
        // (EventSource) by design, so the network is never "idle" - Playwright
        // would time out waiting for a state this page never reaches.
        let! _ = page.WaitForLoadStateAsync(LoadState.Load) |> Async.AwaitTask

        // Optional debug aid: CELLAGENT_DEBUG_SCREENSHOT=<path> dumps a
        // Playwright screenshot of the resolved page before the click, useful
        // when a target selector can't be found (e.g. version skew between
        // the daemon binary under test and the dashboard markup it serves -
        // see STAGE-REPORT.md for the real instance of this that came up).
        match Environment.GetEnvironmentVariable "CELLAGENT_DEBUG_SCREENSHOT" with
        | null | "" -> ()
        | path ->
            let opts = PageScreenshotOptions(Path = path)
            let! _ = page.ScreenshotAsync(opts) |> Async.AwaitTask
            ()

        // Resolve the click target's on-screen rect through Playwright (the
        // "semantic target -> ScreenRect" step from demo-gif-plan.md §4.3) -
        // NOT a hardcoded coordinate. --app mode has no chrome, so page
        // coordinates equal screen coordinates at window position (0,0).
        let clickLocator = page.Locator(plan.ClickSelector)
        let! box = clickLocator.BoundingBoxAsync() |> Async.AwaitTask
        match box with
        | null ->
            do! context.CloseAsync() |> Async.AwaitTask
            Native.XCloseDisplay(display) |> ignore
            return
                { ScenarioId = plan.ScenarioId; Outcome = "Failed"; ClickedAt = [| 0; 0 |]
                  StartedMs = 0L; EndedMs = sw.ElapsedMilliseconds
                  Message = sprintf "click target '%s' not found (no bounding box)" plan.ClickSelector }
        | b ->
        let cx = int (b.X + b.Width / 2.0f)
        let cy = int (b.Y + b.Height / 2.0f)

        // THE PROOF: the click is delivered by XTEST fake input, not
        // Playwright's own .ClickAsync().
        clickAt display screen cx cy

        let! found =
            async {
                try
                    let opts = PageWaitForSelectorOptions(Timeout = 8000.0f)
                    let! _ = page.WaitForSelectorAsync(plan.ExpectSelector, opts) |> Async.AwaitTask
                    return true
                with _ -> return false
            }

        do! context.CloseAsync() |> Async.AwaitTask
        Native.XCloseDisplay(display) |> ignore

        return
            { ScenarioId = plan.ScenarioId
              Outcome = (if found then "Passed" else "Failed")
              ClickedAt = [| cx; cy |]
              StartedMs = 0L
              EndedMs = sw.ElapsedMilliseconds
              Message =
                if found then sprintf "clicked (%d,%d), '%s' appeared" cx cy plan.ExpectSelector
                else sprintf "clicked (%d,%d), '%s' did not appear within timeout" cx cy plan.ExpectSelector }
    }

[<EntryPoint>]
let main _argv =
    try
        let log = run () |> Async.RunSynchronously
        writeStepLogToStdout log
        if log.Outcome = "Passed" then 0 else 1
    with ex ->
        writeStepLogToStdout
            { ScenarioId = "unknown"; Outcome = "Failed"; ClickedAt = [| 0; 0 |]
              StartedMs = 0L; EndedMs = 0L; Message = sprintf "unhandled exception: %s" (ex.ToString()) }
        1
