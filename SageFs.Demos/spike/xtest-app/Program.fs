module XTestApp.Program

open System
open System.Runtime.InteropServices
open Microsoft.Playwright

// ---- P/Invoke into the system libX11 / libXtst (Stage 3 of the Phase-0 spike) ----
// Mirrors demo-gif-plan.md §4.3: pure planning stays in F#, the only impure edge
// is XOpenDisplay + the XTest calls + XFlush. On this machine libXtst IS present
// (contrary to the plan's §2 note, which is stale — see STAGE-REPORT.md), so this
// proves the P/Invoke edge directly against the system library, no bundling needed
// to pass Stage 3 here.

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

// No exception may escape main uncaught: an unhandled exception makes the CLR
// call abort() (SIGABRT + core dump) rather than exit cleanly. Every failure
// mode here (bad argv, XOpenDisplay returning NULL, Playwright/Chromium
// startup failing) is reported as a plain FAIL: line and a non-zero exit
// instead, so a real crash (an actual SIGSEGV/SIGABRT from this process) is
// never confused with an expected, reported failure.
let run (argv: string[]) =
    async {
        // argv: [0]=chromiumExecutablePath [1]=pageUrl [2]=clickX [3]=clickY [4]=userDataDir
        let chromePath = argv.[0]
        let pageUrl = argv.[1]
        let clickX = int argv.[2]
        let clickY = int argv.[3]
        let userDataDir = argv.[4]

        // 1. Open the X display this cell's Xvfb is running on ($DISPLAY, e.g. :99).
        let display = Native.XOpenDisplay(null)
        if display = IntPtr.Zero then
            eprintfn "FAIL: XOpenDisplay returned NULL (is $DISPLAY set and is Xvfb up?)"
            return 1
        else
        let screen = Native.XDefaultScreen(display)
        printfn "OK: XOpenDisplay succeeded, default screen=%d" screen

        // 2. Launch a real headed Chromium via Playwright (bundled browser), --app
        //    mode so window content coordinates == page coordinates, matching Stage 2.
        use! playwright = Playwright.CreateAsync() |> Async.AwaitTask
        let launchOptions = BrowserTypeLaunchPersistentContextOptions()
        launchOptions.ExecutablePath <- chromePath
        launchOptions.Headless <- false
        launchOptions.Args <- ResizeArray [
            "--no-sandbox"; "--disable-gpu"; "--ozone-platform=x11"
            "--window-position=0,0"; "--window-size=1280,720"
            "--no-first-run"; "--disable-features=Translate"; "--disable-extensions"
            "--disable-infobars"; "--no-default-browser-check"
            sprintf "--app=%s" pageUrl
        ]
        let! context =
            playwright.Chromium.LaunchPersistentContextAsync(userDataDir, launchOptions)
            |> Async.AwaitTask
        // --app mode opens its own page; Playwright still sees it via context.Pages.
        do! Async.Sleep 1500
        let page =
            if context.Pages.Count > 0 then context.Pages.[0]
            else (context.WaitForPageAsync() |> Async.AwaitTask |> Async.RunSynchronously)
        let! _ = page.WaitForLoadStateAsync(LoadState.Load) |> Async.AwaitTask

        let! titleBefore = page.TitleAsync() |> Async.AwaitTask
        printfn "OK: page loaded, title before click = %s" titleBefore

        // 3. THE PROOF: deliver the click via XTEST fake input, not Playwright's
        //    own .ClickAsync() — this is the input edge the real product will use.
        clickAt display screen clickX clickY
        printfn "OK: XTestFakeButtonEvent delivered at (%d,%d)" clickX clickY

        // 4. Read the result back through Playwright (semantic observation, not
        //    pixels) - the button's onclick sets document.title = "clicked".
        do! Async.Sleep 300
        let! titleAfter = page.TitleAsync() |> Async.AwaitTask
        printfn "OK: page title after click = %s" titleAfter

        do! context.CloseAsync() |> Async.AwaitTask
        Native.XCloseDisplay(display) |> ignore

        if titleAfter = "clicked" then
            printfn "PASS: XTest-delivered click registered with the real Chromium page"
            return 0
        else
            eprintfn "FAIL: title after click was '%s', expected 'clicked'" titleAfter
            return 1
    }

[<EntryPoint>]
let main argv =
    try
        run argv |> Async.RunSynchronously
    with ex ->
        eprintfn "FAIL: unhandled exception: %s" (ex.ToString())
        1
