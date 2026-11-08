module SageFs.Tests.LiveTestingBrowserTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// LT-DASH browser journeys — real live-testing through the live dashboard:
/// enable in the panel, watch the session's Expecto tests get discovered and
/// run, then edit a source file on disk and watch the failing test surface in
/// the panel, and recover after the fix. These run under `--integration-lt`
/// (LiveTestingBrowserRunner owns the daemon + a session on a temp copy of
/// the FromCSharp sample whose Hello.fs defines `let add a b = a + b` and 11
/// Expecto tests including "add infers int").
///
/// The runner sets:
///   SAGEFS_DASHBOARD_PORT — dashboard URL for the page
///   SAGEFS_LT_FIXTURE_DIR — the temp fixture dir (Hello.fs lives here)
///
/// The env is read LAZILY (per access, not at module load): the module is
/// always linked into the test assembly, so a static throw would break
/// Expecto's discovery of every other suite on machines where the env is
/// unset. Only a journey that actually runs touches these.
module LtEnv =
  let fixtureDir =
    lazy
      (match Environment.GetEnvironmentVariable("SAGEFS_LT_FIXTURE_DIR") with
       | null | "" -> failwith "SAGEFS_LT_FIXTURE_DIR not set (run under --integration-lt)"
       | d -> d)

  let helloFile = lazy Path.Combine(fixtureDir.Value, "Hello.fs")

let private canonicalAdd = "let add a b = a + b"
let private brokenAdd = "let add a b = a + b + 1"

/// Write Hello.fs with retry (the host/compiler can briefly hold the file).
let private writeHello (content: string) =
  let sw = Diagnostics.Stopwatch.StartNew()
  let mutable written = false
  while not written && sw.ElapsedMilliseconds < int64 BrowserWaits.panelUpdates do
    try
      File.WriteAllText(LtEnv.helloFile.Value, content)
      written <- true
    with :? IOException ->
      Threading.Thread.Sleep(TestTimeouts.retryInterval)
  Expect.isTrue written "Hello.fs should be writable within 15s"

/// Read the current Hello.fs content.
let private readHello () = File.ReadAllText(LtEnv.helloFile.Value)

let private ltPlaywrightTest name (body: IPage -> Task<unit>) =
  testCase (sprintf "[Integration] LT browser: %s" name) (fun () ->
    let t = task {
      let! page = PlaywrightFixture.newPage ()
      try
        let! _ = page.GotoAsync(
          sprintf "%s/dashboard" PlaywrightFixture.dashboardUrl)
        do! body page
      finally
        PlaywrightFixture.closePage(page).GetAwaiter().GetResult()
    }
    t.GetAwaiter().GetResult())
  |> Integration.register (Integration.Dedicated "--integration-lt")

/// Turn live testing on until the panel shows ON. A session with live testing off has no #live-testing-panel:
/// the sidebar shows the line "Live testing: off" with a "Turn on" button on it (`#hidden-reason-live-testing`),
/// and the panel replaces that line once live testing is on. A session that started with it on already has the panel.
let private ensureLiveTestingOn (page: IPage) = task {
  let panel = page.Locator("#live-testing-panel")
  let turnOn =
    page.Locator("#hidden-reason-live-testing")
      .GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Turn on"))
  let mutable on = false
  let sw = Diagnostics.Stopwatch.StartNew()
  while not on && sw.ElapsedMilliseconds < int64 BrowserWaits.panelUpdates do
    let! panelCount = panel.CountAsync()
    let! text =
      match panelCount with
      | 0 -> Task.FromResult ""
      | _ -> panel.TextContentAsync()
    if text.Contains("Live Testing: ON") then on <- true
    else
      let! turnOnCount = turnOn.CountAsync()
      match turnOnCount with
      | 0 ->
        let enableBtn =
          panel.GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Enable"))
        let! count = enableBtn.CountAsync()
        if count > 0 then do! enableBtn.ClickAsync()
      | _ -> do! turnOn.ClickAsync()
      do! Task.Delay(TestTimeouts.pollInterval)
  Expect.isTrue on "live testing should reach ON within 15s"
}

[<Tests>]
let tests =
  testSequenced <|
  testList "Live-testing dashboard browser tests" [

    ltPlaywrightTest "enable discovers and runs the sample's tests through the panel" (fun page -> task {
      do! PlaywrightExpect.waitForSelectorText BrowserWaits.daemonWork page "#session-status" "Ready"
      do! ensureLiveTestingOn page
      // Discovery + baseline run: the panel header carries "N✓" for passed.
      let panel = page.Locator("#live-testing-panel")
      do! PlaywrightExpect.waitForText BrowserWaits.liveTestsReport panel "11✓"
      let! header = panel.Locator("h2").TextContentAsync()
      Expect.isTrue (header.Contains("Live Testing: ON"))
        (sprintf "header should show Live Testing ON, was: %s" header)
      Expect.isTrue (header.Contains("11✓"))
        (sprintf "header should show 11 passed, was: %s" header)
      Expect.isFalse (header.Contains("1✗"))
        (sprintf "baseline should have no failing test (0✗ allowed), was: %s" header)
    })

    ltPlaywrightTest "editing a source file reruns tests and surfaces the failure live" (fun page -> task {
      do! PlaywrightExpect.waitForSelectorText BrowserWaits.daemonWork page "#session-status" "Ready"
      do! ensureLiveTestingOn page
      let panel = page.Locator("#live-testing-panel")
      do! PlaywrightExpect.waitForText BrowserWaits.liveTestsReport panel "11✓"

      // Mutate `add` on disk: the edit-triggered rerun must surface the
      // failing "add infers int" test in the panel.
      let original = readHello ()
      Expect.stringContains original canonicalAdd "fixture should contain the editable add"
      let edited = original.Replace(canonicalAdd, brokenAdd)
      try
        writeHello edited
        // The header shows "10✓ 1✗" once the failure lands.
        do! PlaywrightExpect.waitForText BrowserWaits.liveTestsRerun panel "1✗"
        let! header = panel.Locator("h2").TextContentAsync()
        Expect.isTrue (header.Contains("10✓") && header.Contains("1✗"))
          (sprintf "header should show 10 passed / 1 failed, was: %s" header)
      finally
        // Restore synchronously (finally cannot await); the recovery assert
        // happens after the try/finally.
        writeHello original
      // The fix must rerun and return the panel to all-green.
      do! PlaywrightExpect.waitForText BrowserWaits.liveTestsRerun panel "11✓"
      let! header = panel.Locator("h2").TextContentAsync()
      Expect.isFalse (header.Contains("1✗"))
        (sprintf "header should be all-green after restore, was: %s" header)
    })

    ltPlaywrightTest "the bindings pane follows the loop: the chip says current, and Since green lists what moved while a test was red" (fun page -> task {
      do! PlaywrightExpect.waitForSelectorText BrowserWaits.daemonWork page "#session-status" "Ready"
      do! ensureLiveTestingOn page
      let panel = page.Locator("#live-testing-panel")
      do! PlaywrightExpect.waitForText BrowserWaits.liveTestsReport panel "11✓"
      // A value for the bindings pane to hold, put in through the Evaluate popover as a person would.
      let evaluate (code: string) (echoed: string) = task {
        let! isOpen = page.EvaluateAsync<bool>("() => document.querySelector('#evaluate-section').open")
        if not isOpen then do! page.Locator("[data-testid=evaluate-toggle]").ClickAsync()
        do! page.Locator("#eval-textarea").FillAsync code
        do! page.Locator("#evaluate-section .eval-btn").First.ClickAsync()
        do! PlaywrightExpect.waitForSelectorText BrowserWaits.daemonWork page "#output-panel" echoed
        do! page.Locator("[data-testid=evaluate-close]").ClickAsync()
      }
      let chip = page.Locator("[data-testid=live-freshness]")
      do! evaluate "let marker = 1;;" "marker: int = 1"
      do! PlaywrightExpect.waitForSelectorText BrowserWaits.daemonWork page "#bindings-panel" "marker"
      // The loop settled with every test passing, so the pane says its values are current.
      do! PlaywrightExpect.waitForText BrowserWaits.liveTestsReport chip "current"
      for width in [ 1400; 900; 400 ] do
        do! page.SetViewportSizeAsync(width, 900)
        do! PlaywrightFixture.takeScreenshot page false (IO.Path.Combine(PlaywrightFixture.screenshotDir, sprintf "lt-chip-current-%d.png" width))
      do! page.SetViewportSizeAsync(1400, 900)
      let original = readHello ()
      let edited = original.Replace(canonicalAdd, brokenAdd)
      try
        writeHello edited
        do! PlaywrightExpect.waitForText BrowserWaits.liveTestsRerun panel "1✗"
        // A test went red and the value moved after it: Since green names it with what it was when the tests last passed.
        do! evaluate "let marker = 2;;" "marker: int = 2"
        do! page.Locator("[data-testid=live-tab-since-green]").ClickAsync()
        let was = page.Locator("[data-testid=live-was]")
        do! PlaywrightExpect.waitForText BrowserWaits.daemonWork was "was 1"
        for width in [ 1400; 900; 400 ] do
          do! page.SetViewportSizeAsync(width, 900)
          do! PlaywrightFixture.takeScreenshot page false (IO.Path.Combine(PlaywrightFixture.screenshotDir, sprintf "lt-since-green-red-%d.png" width))
        do! page.SetViewportSizeAsync(1400, 900)
      finally
        writeHello original
      // The tests pass again: the comparison point is now, so nothing has moved since it.
      do! PlaywrightExpect.waitForText BrowserWaits.liveTestsRerun panel "11✓"
      do! PlaywrightExpect.waitForSelectorText BrowserWaits.daemonWork page "[data-testid=live-tab-empty-since-green]" "Nothing has moved"
    })
  ]
