module SageFs.Tests.HotReloadBrowserTests

open System
open System.IO
open System.Net.Http
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright
open SageFs.Tests.DashboardBrowserTests

/// HR-DASH browser journeys — real save -> changed running app through the
/// live dashboard. These run under `--integration-hr` (HotReloadBrowserRunner
/// owns the daemon + a WebLive session on a temp WebAppFixture copy whose app
/// serves `Greeting.greeting()`).
///
/// The runner sets:
///   SAGEFS_DASHBOARD_PORT — dashboard URL for the page
///   SAGEFS_HR_APP_URL     — base URL of the running fixture app (value A)
///   SAGEFS_HR_FIXTURE_DIR — the temp fixture dir (Greeting.fs lives here)
///
/// The env is read LAZILY (per access, not at module load): the module is
/// always linked into the test assembly, so a static throw would break
/// Expecto's discovery of every other suite on machines where the env is
/// unset. Only a journey that actually runs touches these.
module HrEnv =
  let appUrl =
    lazy
      (match Environment.GetEnvironmentVariable("SAGEFS_HR_APP_URL") with
       | null | "" -> failwith "SAGEFS_HR_APP_URL not set (run under --integration-hr)"
       | u -> u.TrimEnd('/'))

  let fixtureDir =
    lazy
      (match Environment.GetEnvironmentVariable("SAGEFS_HR_FIXTURE_DIR") with
       | null | "" -> failwith "SAGEFS_HR_FIXTURE_DIR not set (run under --integration-hr)"
       | d -> d)

  let greetingFile = lazy Path.Combine(fixtureDir.Value, "Greeting.fs")

/// HTTP GET the running app's / route.
let private httpGet (url: string) =
  use client = new HttpClient()
  client.Timeout <- TimeSpan.FromSeconds(10.0)
  try
    client.GetStringAsync(url + "/").GetAwaiter().GetResult()
  with ex ->
    failwithf "HTTP GET %s/ failed: %s" url ex.Message

/// Poll the running app until its body contains `needle` (or timeout).
let private waitForAppBody (url: string) (needle: string) (timeoutMs: int) = task {
  let sw = Diagnostics.Stopwatch.StartNew()
  let mutable found = ""
  while found = "" && sw.ElapsedMilliseconds < int64 timeoutMs do
    try
      let body = httpGet url
      if body.Contains needle then found <- body
    with _ -> ()
    if found = "" then do! Task.Delay(250)
  Expect.isTrue (found <> "") (sprintf "app at %s never served '%s' within %dms" url needle timeoutMs)
  return found
}

/// Write Greeting.fs with retry (the host can briefly hold the file).
let private writeGreeting (content: string) =
  let sw = Diagnostics.Stopwatch.StartNew()
  let mutable written = false
  while not written && sw.ElapsedMilliseconds < 15000L do
    try
      File.WriteAllText(HrEnv.greetingFile.Value, content)
      written <- true
    with :? IOException ->
      Threading.Thread.Sleep(200)
  Expect.isTrue written "Greeting.fs should be writable within 15s"

/// The original fixture content (value A).
let private valueAGreeting = "let greeting () = \"hello from sagefs\""
let private valueBGreeting = "let greeting () = \"hello from hot reload (value B)\""

let private hrPlaywrightTest name (body: IPage -> Task<unit>) =
  testCase (sprintf "[Integration] HR browser: %s" name) (fun () ->
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

[<Tests>]
let tests =
  testSequenced <|
  testList "Hot-reload dashboard browser tests" [

    hrPlaywrightTest "watch all arms the file watcher and the panel reflects it" (fun page -> task {
      do! PlaywrightExpect.waitForSelectorText 30_000 page "#session-status" "Ready"
      // The hot-reload panel lives in the sidebar; open it if collapsed.
      let panel = page.Locator("#hot-reload-panel")
      do! PlaywrightExpect.isVisibleAsync panel "hot reload panel visible"
      do! PlaywrightExpect.waitForText 10_000 panel "Hot Reload: OFF"
      // Click Watch All.
      let watchAll =
        panel.GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Watch All", Exact = true))
      do! watchAll.ClickAsync()
      // The panel header flips to ON with a watched count > 0.
      do! PlaywrightExpect.waitForText 30_000 panel "Hot Reload: ON"
      let! header = panel.Locator("h2").TextContentAsync()
      Expect.isTrue (header.Contains("of") && header.Contains("files"))
        (sprintf "header should show 'N of N files', was: %s" header)
    })

    hrPlaywrightTest "saving a watched file hot-reloads the running app (value A -> value B)" (fun page -> task {
      do! PlaywrightExpect.waitForSelectorText 30_000 page "#session-status" "Ready"
      // Establish value A from the running app.
      let! bodyA = waitForAppBody HrEnv.appUrl.Value "hello from sagefs" 15_000
      Expect.isTrue (bodyA.Contains("hello from sagefs")) "app should serve value A before the edit"

      // Arm the watch set via the dashboard panel (Watch All), same as the UI.
      let panel = page.Locator("#hot-reload-panel")
      do! PlaywrightExpect.isVisibleAsync panel "hot reload panel visible"
      let watchAll =
        panel.GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Watch All", Exact = true))
      do! watchAll.ClickAsync()
      do! PlaywrightExpect.waitForText 30_000 panel "Hot Reload: ON"
      // Give the watcher a moment to arm before the edit (a save racing the
      // watch-set update would be ignored).
      do! page.WaitForTimeoutAsync(1500.0f)

      // Read the ORIGINAL fixture content, edit value A -> value B on disk.
      let original = File.ReadAllText(HrEnv.greetingFile.Value)
      Expect.stringContains original valueAGreeting "fixture should contain the editable greeting"
      let edited = original.Replace(valueAGreeting, valueBGreeting)
      writeGreeting edited
      try
        // The SAME running process must serve value B (no restart). If the
        // first save lands while the watcher is mid-cycle (debounce/cancel),
        // a re-save of the same content kicks the watcher again — mirroring
        // the repair path that reliably reloads.
        let sw = Diagnostics.Stopwatch.StartNew()
        let mutable served = false
        while not served && sw.ElapsedMilliseconds < 60_000L do
          try
            let body = httpGet HrEnv.appUrl.Value
            if body.Contains("hello from hot reload (value B)") then
              served <- true
            else
              if sw.ElapsedMilliseconds > 15_000L then writeGreeting edited
              do! Task.Delay(1000)
          with _ ->
            do! Task.Delay(1000)
        Expect.isTrue served
          (sprintf "hot reload should serve the new greeting from the running process (value A body: %s)" bodyA)
      finally
        // Restore value A so later runs start clean.
        writeGreeting original
    })

    hrPlaywrightTest "compile-error save keeps last valid behavior and repair hot-reloads it" (fun page -> task {
      do! PlaywrightExpect.waitForSelectorText 30_000 page "#session-status" "Ready"
      let panel = page.Locator("#hot-reload-panel")
      do! PlaywrightExpect.isVisibleAsync panel "hot reload panel visible"
      let watchAll =
        panel.GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Watch All", Exact = true))
      do! watchAll.ClickAsync()
      do! PlaywrightExpect.waitForText 30_000 panel "Hot Reload: ON"
      do! page.WaitForTimeoutAsync(1500.0f)

      let original = File.ReadAllText(HrEnv.greetingFile.Value)
      Expect.stringContains original valueAGreeting "fixture should start from value A"

      // 1. Save BROKEN F# — the app must keep serving value A.
      let broken = original.Replace(valueAGreeting, "let greeting () : int = \"this will not compile\"")
      writeGreeting broken
      try
        let! bodyAfterFail = waitForAppBody HrEnv.appUrl.Value "hello from sagefs" 15_000
        Expect.stringContains bodyAfterFail "hello from sagefs"
          "compile error must not take down the running app (last valid behavior retained)"

        // 2. Repair — the fix must hot-reload into the running app.
        Threading.Thread.Sleep(1000)
        let repaired = original.Replace(valueAGreeting, valueBGreeting)
        writeGreeting repaired
        let! bodyB = waitForAppBody HrEnv.appUrl.Value "hello from hot reload (value B)" 45_000
        Expect.stringContains bodyB "hello from hot reload (value B)"
          "repair should hot-reload the new greeting from the running process"
      finally
        writeGreeting original
    })
  ]
