/// The workspace hygiene panel in a real browser, against a daemon of its own that manages a throwaway repository and
/// nothing else on this machine: its data dir, gate dir, host cache and temp dir are all inside the sandbox, and a daemon
/// with its own data dir only lists the processes it started. The journey walks the state matrix a person meets: nothing
/// scanned yet, a dry run shown, a tidy executed, nothing left to tidy, and the panel at a narrow width, with zero
/// console errors throughout.
module SageFs.Tests.HygieneBrowserTests

open System
open System.IO
open System.Net.Http
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright
open SageFs
open SageFs.Server.DashboardTypes
open SageFs.Tests.HygieneSandbox
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The width a phone gives the dashboard. The panel must wrap its rows at it and never scroll sideways.
let private narrowViewportWidth = 380
let private narrowViewportHeight = 900

type private Daemon =
  { Process: Diagnostics.Process
    McpPort: int
    DashboardPort: int
    OutLog: string
    ErrLog: string }

let private drain (stream: StreamReader) (path: string) =
  let writer = new StreamWriter(path, append = true)
  let rec loop () =
    async {
      let! line = stream.ReadLineAsync() |> Async.AwaitTask
      if not (isNull line) then
        do! writer.WriteLineAsync(line) |> Async.AwaitTask
        return! loop ()
    }
  async {
    try do! loop () with _ -> ()
    writer.Dispose()
  }
  |> Async.Start

/// A daemon whose every directory is inside the sandbox, so nothing it tidies can be anything real.
let private startDaemon (sb: Sandbox) : Daemon =
  let mcpPort, dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
  let loc = sb.Locations
  for dir in [ loc.DataDir; loc.GateDir; loc.HostCacheDir; loc.TempDir ] do
    Directory.CreateDirectory dir |> ignore
  let psi = Diagnostics.ProcessStartInfo()
  psi.FileName <- SageFs.Tests.TestInfrastructure.SageFsBinary.path ()
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- sb.Root
  for a in [ "--mcp-port"; string mcpPort; "--owner-pid"; string Environment.ProcessId; "--no-resume" ] do
    psi.ArgumentList.Add a
  psi.Environment["SAGEFS_DATA_DIR"] <- loc.DataDir
  psi.Environment["SAGEFS_GATE_HOME"] <- loc.GateDir
  psi.Environment["SAGEFS_HOST_CACHE_DIR"] <- loc.HostCacheDir
  psi.Environment["TMPDIR"] <- loc.TempDir
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  let proc = Diagnostics.Process.Start psi
  let outLog = Path.Combine(sb.Root, "daemon.stdout.log")
  let errLog = Path.Combine(sb.Root, "daemon.stderr.log")
  drain proc.StandardOutput outLog
  drain proc.StandardError errLog
  { Process = proc; McpPort = mcpPort; DashboardPort = dashboardPort; OutLog = outLog; ErrLog = errLog }

let private dumpLogs (d: Daemon) =
  for path in [ d.OutLog; d.ErrLog ] do
    try
      if File.Exists path then
        let lines = (File.ReadAllText path).Split('\n')
        eprintfn "--- %s (tail) ---" (Path.GetFileName path)
        lines |> Array.skip (max 0 (lines.Length - 40)) |> Array.iter (eprintfn "%s")
    with _ -> ()

let private killDaemon (d: Daemon) =
  try if not d.Process.HasExited then d.Process.Kill(entireProcessTree = true) with _ -> ()
  try d.Process.WaitForExit(TestTimeouts.childExit) |> ignore with _ -> ()
  try d.Process.Dispose() with _ -> ()

let private waitUntil (budgetMs: int) (condition: unit -> Task<bool>) : Task<bool> = task {
  let sw = Diagnostics.Stopwatch.StartNew()
  let mutable ok = false
  while not ok && sw.ElapsedMilliseconds < int64 budgetMs do
    let! result = condition ()
    match result with
    | true -> ok <- true
    | false -> do! Task.Delay(TestTimeouts.pollPage)
  return ok
}

let private http (d: Daemon) = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" d.McpPort), Timeout = TestTimeouts.httpRequest)

let private healthy (d: Daemon) : Task<bool> =
  waitUntil BrowserWaits.daemonWork (fun () -> task {
    try
      use client = http d
      use! resp = client.GetAsync "/health"
      return true
    with _ -> return false
  })

/// A bare session whose working directory is the sandbox repository, so the panel is about that repository.
let private createSession (d: Daemon) (dir: string) : Task<string> = task {
  use client = http d
  let payload = System.Text.Json.JsonSerializer.Serialize({| projects = Array.empty<string>; workingDirectory = dir |})
  use content = new StringContent(payload, Text.Encoding.UTF8, "application/json")
  use! resp = client.PostAsync("/api/sessions/create", content)
  let! body = resp.Content.ReadAsStringAsync()
  match resp.IsSuccessStatusCode with
  | false -> return failwithf "creating the session was refused: HTTP %d: %s" (int resp.StatusCode) body
  | true -> return body
}

let private sessionReady (d: Daemon) : Task<bool> =
  waitUntil BrowserWaits.hotReloadBuild (fun () -> task {
    try
      use client = http d
      let! body = client.GetStringAsync "/api/sessions"
      use doc = System.Text.Json.JsonDocument.Parse body
      return
        doc.RootElement.GetProperty("sessions").EnumerateArray()
        |> Seq.exists (fun s -> s.GetProperty("status").GetString() = "Ready")
    with _ -> return false
  })

let private attachErrorCollector (page: IPage) : Collections.Generic.List<string> =
  let errors = Collections.Generic.List<string>()
  page.Console.Add(fun msg -> if msg.Type = "error" then errors.Add(sprintf "[console] %s" msg.Text))
  page.PageError.Add(fun err -> errors.Add(sprintf "[pageerror] %s" err))
  errors

let private panel (page: IPage) = page.Locator "#hygiene-panel"
let private stateLine (page: IPage) = page.Locator "#hygiene-state"
let private scanButton (page: IPage) = page.Locator "#hygiene-scan"
let private tidyButton (page: IPage) = page.Locator "#hygiene-tidy"

/// The panel lives in the expanded-only sidebar block and is a `<details>`: reveal it and open it. The 1 s fallback push
/// can snap an accordion shut, so callers wrap a step in `throughPanelReset`.
let private openPanel (page: IPage) : Task<unit> = task {
  do! DashboardDom.ensureExpanded page
  let! isOpen = page.EvaluateAsync<bool>("() => { var el = document.querySelector('#hygiene-panel'); return el ? el.open : false; }")
  match isOpen with
  | true -> ()
  | false -> do! page.Locator("#hygiene-panel summary").First.ClickAsync()
}

let private panelText (page: IPage) : Task<string> = task {
  let! text = (panel page).TextContentAsync()
  return text
}

let private expectText (what: string) (needle: string) (text: string) =
  Expect.isTrue (text.Contains needle) (sprintf "%s: expected the panel to say '%s', it said: %s" what needle text)

let private branchExists (sb: Sandbox) (name: string) = HygieneSandbox.branchExists sb name

let private journey () : Task<unit> = task {
  use sb = new Sandbox()
  let paths = populate sb
  // A host nobody has used for months, planted before the daemon starts: the daemon's own housekeeping prunes it.
  let plantStaleHost (name: string) : string =
    let host = Path.Combine(sb.Locations.HostCacheDir, name)
    write (Path.Combine(host, "bin", "FsiHost.dll")) "x"
    let marker = Path.Combine(host, FsiHostBuild.HostLastUsedMarker)
    write marker "used"
    File.SetLastWriteTimeUtc(marker, DateTime.UtcNow - HygieneAges.ancient)
    Directory.SetLastWriteTimeUtc(host, DateTime.UtcNow - HygieneAges.ancient)
    host
  let staleAtStart = plantStaleHost "sdk-10.0.100-stale-at-start"
  // Surplus gate pass records: only the newest few are kept.
  let surplusPass = Path.Combine(sb.Locations.GateDir, GateReaper.Names.passedDir, "sha-surplus")
  write (Path.Combine(surplusPass, "ok")) "ok"
  Directory.SetLastWriteTimeUtc(surplusPass, DateTime.UtcNow - HygieneAges.ancient)
  for i in 1 .. DataRetention.gatePassRecordsKept do
    let keep = Path.Combine(sb.Locations.GateDir, GateReaper.Names.passedDir, sprintf "sha-keep-%02d" i)
    write (Path.Combine(keep, "ok")) "ok"
    Directory.SetLastWriteTimeUtc(keep, DateTime.UtcNow - TimeSpan.FromHours(float i))
  let daemon = startDaemon sb
  let mutable playwright : IPlaywright option = None
  let mutable browser : IBrowser option = None
  try
    let! up = healthy daemon
    match up with
    | true -> ()
    | false ->
      dumpLogs daemon
      failtestf "the hygiene daemon on port %d never became healthy" daemon.McpPort
    let! _ = createSession daemon sb.Repo
    let! ready = sessionReady daemon
    match ready with
    | true -> ()
    | false ->
      dumpLogs daemon
      failtest "the session in the sandbox repository never reached Ready"
    // The daemon pruned the host that was already stale when it started, on its own, with nobody asking.
    let! prunedAtStart = waitUntil BrowserWaits.panelUpdates (fun () -> Task.FromResult(not (Directory.Exists staleAtStart)))
    Expect.isTrue prunedAtStart "the daemon's start-up housekeeping prunes a host nobody used for months"
    // A host that goes stale while the daemon runs is a leftover the plan lists, and tidy removes.
    let staleHost = plantStaleHost "sdk-10.0.100-stale-later"
    let! pw = Playwright.CreateAsync()
    playwright <- Some pw
    let! b = pw.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
    browser <- Some b
    let! page = b.NewPageAsync()
    let errors = attachErrorCollector page
    let! _ = page.GotoAsync(sprintf "http://localhost:%d/dashboard" daemon.DashboardPort)
    do! PlaywrightExpect.waitForSSE BrowserWaits.panelUpdates page

    // 1. Nothing scanned yet: the panel says so and offers a scan, not a tidy.
    do! DashboardDom.throughPanelReset (fun () -> openPanel page) 3 (fun () -> task {
      do! PlaywrightExpect.waitForText BrowserWaits.panelUpdates (stateLine page) "Not scanned yet"
      do! PlaywrightExpect.isVisibleAsync (scanButton page) "the scan button is offered"
      do! PlaywrightExpect.waitForCount BrowserWaits.pageProbe (tidyButton page) 0 })

    // 2. A scan shows a dry run and changes nothing on disk.
    do! DashboardDom.throughPanelReset (fun () -> openPanel page) 3 (fun () -> task {
      do! (scanButton page).ClickAsync()
      do! PlaywrightExpect.waitForText BrowserWaits.daemonWork (stateLine page) "Dry run:" })
    let! dryRun = panelText page
    expectText "dry run" "safe to reclaim" dryRun
    expectText "dry run" "need a look" dryRun
    do! PlaywrightExpect.waitForText BrowserWaits.pageProbe (tidyButton page) "Tidy "
    for name, path in Map.toList paths do
      Expect.isTrue (Directory.Exists path) (sprintf "a scan removes nothing, but %s is gone" name)
    Expect.isTrue (Directory.Exists staleHost) "a scan leaves the stale host"
    // The listing names what it found and how to save what needs saving.
    expectText "the plan" "clean-merged" dryRun
    expectText "the plan" "git -C" dryRun

    // 3. At a narrow width the panel wraps its rows and never scrolls sideways.
    do! page.SetViewportSizeAsync(narrowViewportWidth, narrowViewportHeight)
    do! DashboardDom.throughPanelReset (fun () -> openPanel page) 3 (fun () -> task {
      let! fits = page.EvaluateAsync<bool>("() => { var p = document.querySelector('#hygiene-panel'); return p ? p.scrollWidth <= p.clientWidth + 1 : false; }")
      Expect.isTrue fits "the panel does not overflow its box at a narrow width" })

    // 4. Tidy: the safe part goes, everything that needs a look stays.
    do! DashboardDom.throughPanelReset (fun () -> openPanel page) 3 (fun () -> task {
      do! (tidyButton page).ClickAsync()
      do! PlaywrightExpect.waitForText BrowserWaits.daemonWork (page.Locator "#hygiene-tidied") "Tidied:" })
    for name in [ "clean-merged"; "ff-merged"; "squashed"; "dirty-generated" ] do
      Expect.isFalse (Directory.Exists paths.[name]) (sprintf "%s was safe and should be gone" name)
      Expect.isFalse (branchExists sb ("worktree-agent-" + name)) (sprintf "the merged branch of %s should be gone" name)
    for name in [ "unmerged"; "dirty-real" ] do
      Expect.isTrue (Directory.Exists paths.[name]) (sprintf "%s needs a look and must stay" name)
      Expect.isTrue (branchExists sb ("worktree-agent-" + name)) (sprintf "the branch of %s must stay" name)
    Expect.isTrue (File.Exists(Path.Combine(paths.["dirty-real"], "src", "precious.fs"))) "uncommitted work is untouched"
    Expect.equal ((git paths.["unmerged"] [ "log"; "-1"; "--format=%s" ]).Trim()) "unmerged work" "the unmerged commit is still reachable"
    Expect.isFalse (Directory.Exists staleHost) "the stale host was pruned"
    Expect.isFalse (Directory.Exists surplusPass) "the surplus gate pass record was reclaimed"
    Expect.equal (Directory.GetDirectories(Path.Combine(sb.Locations.GateDir, GateReaper.Names.passedDir)).Length) DataRetention.gatePassRecordsKept "only the newest pass records stay"

    // 5. After the tidy the control reflects the state: nothing safe is left, so it says so and does nothing.
    do! DashboardDom.throughPanelReset (fun () -> openPanel page) 3 (fun () -> task {
      do! PlaywrightExpect.waitForText BrowserWaits.panelUpdates (tidyButton page) "Nothing to tidy"
      let! disabled = (tidyButton page).IsDisabledAsync()
      Expect.isTrue disabled "the tidy control does nothing when nothing is safe" })

    // 6. With the last two worktrees gone too, a rescan finds nothing at all.
    for name in [ "unmerged"; "dirty-real" ] do
      git sb.Repo [ "worktree"; "remove"; "--force"; paths.[name] ] |> ignore
    do! DashboardDom.throughPanelReset (fun () -> openPanel page) 3 (fun () -> task {
      do! (scanButton page).ClickAsync()
      do! PlaywrightExpect.waitForText BrowserWaits.daemonWork (stateLine page) "Dry run: 0 safe to reclaim" })
    do! PlaywrightExpect.waitForText BrowserWaits.pageProbe (tidyButton page) "Nothing to tidy"

    // 7. Through all of it, not one console or page error.
    Expect.isEmpty (List.ofSeq errors) (sprintf "zero console and page errors across the journey, got: %s" (String.concat " | " errors))
  finally
    match browser with
    | Some b -> try (b.CloseAsync()).GetAwaiter().GetResult() with _ -> ()
    | None -> ()
    match playwright with
    | Some p -> try p.Dispose() with _ -> ()
    | None -> ()
    killDaemon daemon
}

[<Tests>]
let tests =
  testSequenced <|
  testList "Workspace hygiene browser journeys" [
    testTask "[Integration] Hygiene browser: the panel through not scanned, dry run, tidy, nothing to tidy and a narrow width, with zero console errors" {
      do! journey () }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
