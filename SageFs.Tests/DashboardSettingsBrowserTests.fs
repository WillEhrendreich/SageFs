/// The Settings page in a real browser, against daemons of their own that share one throwaway data directory. The
/// outcome the page promises is walked as a person meets it: a row at its default, a value saved from the page, the same
/// row after the daemon restarts, an environment variable that overrides what was saved, a flag, and a read-only row
/// whose value comes from the environment. Each cell asserts the value shown, the source badge, and what is in use.
/// The matrix is (warm-up inactivity limit, abandoned-session hours, metadata-delta, MCP port, bind host) by the cells
/// above; every one is an assertion and none is skipped.
module SageFs.Tests.DashboardSettingsBrowserTests

open System
open System.IO
open System.Net.Http
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright
open SageFs.Server.DashboardTypes
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The width a phone gives the dashboard. The rows wrap at it and the page never scrolls sideways.
let private narrowViewportWidth = 375
let private narrowViewportHeight = 900

type private Daemon =
  { Process: Diagnostics.Process
    McpPort: int
    DashboardPort: int
    OutLog: string
    ErrLog: string
    /// The two threads that copy the daemon's output into the logs. They end when the process's pipes close.
    Drains: Task<unit> list }

/// Copies one of the daemon's output streams into a log. A blocking `ReadLine` on a thread of its own, so a starved
/// pool cannot hold the daemon's pipe full.
let private drain (name: string) (stream: StreamReader) (path: string) : Task<unit> =
  SageFs.Tests.TestInfrastructure.DedicatedThread.run name (fun () ->
    use writer = new StreamWriter(path, append = true)
    try
      let mutable line = stream.ReadLine()
      while not (isNull line) do
        writer.WriteLine line
        line <- stream.ReadLine()
    with _ -> ())

/// A daemon whose data directory is the sandbox's, started with the environment variables a cell asks for.
let private startDaemon (root: string) (generation: int) (extraEnvironment: (string * string) list) : Daemon =
  let mcpPort, dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
  let dataDir = Path.Combine(root, "data")
  Directory.CreateDirectory dataDir |> ignore
  let psi = Diagnostics.ProcessStartInfo()
  psi.FileName <- SageFs.Tests.TestInfrastructure.SageFsBinary.path ()
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- root
  for a in [ "--mcp-port"; string mcpPort; "--owner-pid"; string Environment.ProcessId; "--no-resume" ] do
    psi.ArgumentList.Add a
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
  for name, value in extraEnvironment do
    psi.Environment[name] <- value
  let proc = Diagnostics.Process.Start psi
  let outLog = Path.Combine(root, sprintf "daemon%d.stdout.log" generation)
  let errLog = Path.Combine(root, sprintf "daemon%d.stderr.log" generation)
  let drains =
    [ drain (sprintf "settings-daemon%d-stdout" generation) proc.StandardOutput outLog
      drain (sprintf "settings-daemon%d-stderr" generation) proc.StandardError errLog ]
  { Process = proc; McpPort = mcpPort; DashboardPort = dashboardPort; OutLog = outLog; ErrLog = errLog; Drains = drains }

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

/// A killed daemon's pipes are closed, so its drain threads end at once; this waits for them to finish the logs.
let private awaitDrains (d: Daemon) : Task<unit> = task {
  for drained in d.Drains do
    try do! drained.WaitAsync TestTimeouts.childExit with _ -> ()
}

let private healthy (d: Daemon) : Task<bool> = task {
  let sw = Diagnostics.Stopwatch.StartNew()
  let mutable ok = false
  while not ok && sw.ElapsedMilliseconds < int64 BrowserWaits.daemonWork do
    try
      use client = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" d.McpPort), Timeout = TestTimeouts.httpRequest)
      use! _resp = client.GetAsync "/health"
      ok <- true
    with _ -> do! Task.Delay(TestTimeouts.pollPage)
  return ok
}

let private attachErrorCollector (page: IPage) : Collections.Generic.List<string> =
  let errors = Collections.Generic.List<string>()
  page.Console.Add(fun msg -> if msg.Type = "error" then errors.Add(sprintf "[console] %s" msg.Text))
  page.PageError.Add(fun err -> errors.Add(sprintf "[pageerror] %s" err))
  errors

let private row (page: IPage) (key: string) : ILocator = page.Locator(sprintf "[data-setting-key='%s']" key)

/// What one row says, read from the page.
type private RowFacts =
  { Effective: string
    Source: string
    Text: string }

let private factsOf (page: IPage) (key: string) : Task<RowFacts> = task {
  let r = row page key
  let! effective = r.Locator(".settings-effective").TextContentAsync()
  let! source = r.Locator(".settings-badge").First.TextContentAsync()
  let! text = r.TextContentAsync()
  return { Effective = effective.Trim(); Source = source.Trim(); Text = text }
}

/// Wait until the row shows the badge, because the first server render and the morph after a save are not the same moment.
let private waitForSource (page: IPage) (key: string) (source: string) : Task<unit> =
  PlaywrightExpect.waitForText BrowserWaits.panelUpdates ((row page key).Locator ".settings-badge") source

let private expectCell (cell: string) (facts: RowFacts) (effective: string) (source: string) =
  Expect.equal facts.Effective effective (sprintf "%s: the value shown" cell)
  Expect.equal facts.Source source (sprintf "%s: the source badge" cell)

/// Save a value in a row from the page, the way a person does: type it and press Save.
let private save (page: IPage) (key: string) (value: string) : Task<unit> = task {
  let r = row page key
  do! r.Locator(".settings-input").FillAsync value
  do! r.Locator("button.settings-btn").First.ClickAsync()
  do! PlaywrightExpect.waitForText BrowserWaits.panelUpdates (page.Locator ".settings-notice-ok") "saved"
}

/// A bare session whose working directory is `dir`, and its id. The page only needs the daemon to know the session and
/// its directory, so this does not wait for the session to warm up.
let private createSessionIn (d: Daemon) (dir: string) : Task<string> = task {
  use client = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" d.McpPort), Timeout = TestTimeouts.httpRequest)
  let payload = System.Text.Json.JsonSerializer.Serialize({| projects = Array.empty<string>; workingDirectory = dir |})
  use content = new StringContent(payload, Text.Encoding.UTF8, "application/json")
  use! resp = client.PostAsync("/api/sessions/create", content)
  let! body = resp.Content.ReadAsStringAsync()
  match resp.IsSuccessStatusCode with
  | false -> return failwithf "creating the session was refused: HTTP %d: %s" (int resp.StatusCode) body
  | true ->
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable found = ""
    while found = "" && sw.ElapsedMilliseconds < int64 BrowserWaits.panelUpdates do
      let! listed = client.GetStringAsync "/api/sessions"
      use doc = System.Text.Json.JsonDocument.Parse listed
      match doc.RootElement.GetProperty("sessions").EnumerateArray() |> Seq.tryHead with
      | Some s -> found <- s.GetProperty("id").GetString()
      | None -> do! Task.Delay(TestTimeouts.pollPage)
    return found
}

let private perTestKey = "livetest.perTestTimeoutSeconds"
let private warmup = "waits.warmupInactivitySeconds"
let private abandoned = "waits.abandonedSessionHours"
let private delta = "hotreload.metadataDelta"
let private mcpPortKey = "daemon.mcpPort"
let private bindHostKey = "daemon.bindHost"
let private buildKey = "waits.buildTimeoutMinutes"
let private updateKey = "daemon.updateCheckDisabled"
let private tierKey = "waits.machineTier"

let private journey () : Task<unit> = task {
  let root = Path.Combine(Path.GetTempPath(), "sagefs-settings-journey-" + Guid.NewGuid().ToString("N").[..7])
  Directory.CreateDirectory root |> ignore
  let mutable daemons : Daemon list = []
  let mutable reachedTheEnd = false
  let start (extra: (string * string) list) : Daemon =
    let d = startDaemon root (List.length daemons + 1) extra
    daemons <- d :: daemons
    d
  try
    use! pw = Playwright.CreateAsync()
    use! b = pw.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
    let! page = b.NewPageAsync()
    let errors = attachErrorCollector page
    let up (d: Daemon) = task {
      let! ok = healthy d
      match ok with
      | true -> ()
      | false ->
        dumpLogs d
        failtestf "the daemon on port %d never became healthy" d.McpPort
    }

    // 1. A fresh data directory: every row is at its default, and the rows with a flag or a variable behind them say so.
    let first = start []
    do! up first
    let! _ = page.GotoAsync(sprintf "http://localhost:%d/dashboard/settings" first.DashboardPort)
    do! waitForSource page warmup "default"
    let! facts = factsOf page warmup
    expectCell "warm-up limit at its default" facts "30" "default"
    let! facts = factsOf page abandoned
    expectCell "abandoned sessions at their default" facts "12" "default"
    let! facts = factsOf page delta
    expectCell "metadata delta at its default" facts "on" "default"
    // The daemon was started with --mcp-port, so the flag is the source and the port is the one it bound.
    let! facts = factsOf page mcpPortKey
    expectCell "MCP port from the flag" facts (string first.McpPort) "flag"
    let! facts = factsOf page bindHostKey
    expectCell "bind host at its default" facts "localhost" "default"
    let! bindText = (row page bindHostKey).TextContentAsync()
    Expect.stringContains bindText "read-only" "the bind host row says it is read-only"
    let! inputs = (row page bindHostKey).Locator(".settings-input").CountAsync()
    Expect.equal inputs 0 "the bind host row has no edit control"

    // 2. Save from the page. Before a restart the row shows the saved value, names the global file, and says what is in use.
    do! save page warmup "90"
    do! waitForSource page warmup "global"
    let! facts = factsOf page warmup
    expectCell "warm-up limit saved, before the restart" facts "90" "global"
    // What the daemon uses is its default for this machine's tier (30 s scaled by the tier), whatever that is here.
    Expect.stringContains facts.Text "In use now:" "before the restart the row says what the daemon is using"
    Expect.isFalse (facts.Text.Contains "In use now: 90") "before the restart the daemon is not using the saved 90"
    Expect.stringContains facts.Text "The saved value applies after a restart." "the row says a restart applies it"
    do! save page abandoned "0"
    do! waitForSource page abandoned "global"
    let! facts = factsOf page abandoned
    expectCell "reaper off, saved, before the restart" facts "0" "global"
    do! save page delta "off"
    do! waitForSource page delta "global"
    do! save page buildKey "25"
    do! waitForSource page buildKey "global"
    do! save page updateKey "on"
    do! waitForSource page updateKey "global"
    // A tier name is accepted in any case and shown in lower case.
    do! save page tierKey "Fast"
    do! waitForSource page tierKey "global"
    let! facts = factsOf page tierKey
    expectCell "machine tier saved, before the restart" facts "fast" "global"

    // 2b. The repo layer. A session in a working directory gives the page a repo to write: the edit target names that
    // directory, a per-test timeout saved there beats the global one, and the file is the directory's own.
    let work = Path.Combine(root, "work")
    Directory.CreateDirectory work |> ignore
    let! sessionId = createSessionIn first work
    Expect.isFalse (String.IsNullOrEmpty sessionId) "the daemon lists the session it was asked to create"
    let! _ = page.GotoAsync(sprintf "http://localhost:%d/dashboard/settings?session=%s" first.DashboardPort sessionId)
    do! waitForSource page perTestKey "default"
    let! scopeText = page.Locator(".settings-scope").TextContentAsync()
    Expect.stringContains scopeText (sprintf "This repo (%s)" work) "the edit target names the session's directory"
    do! save page perTestKey "42"
    do! waitForSource page perTestKey "global"
    let! facts = factsOf page perTestKey
    expectCell "per-test timeout saved globally" facts "42" "global"
    Expect.isFalse (facts.Text.Contains "In use now") "a live row is in use as soon as it is saved"
    let! _ = page.Locator(".settings-scope-select").SelectOptionAsync("repo")
    do! save page perTestKey "33"
    do! waitForSource page perTestKey "repo"
    let! facts = factsOf page perTestKey
    expectCell "per-test timeout saved for the repo" facts "33" "repo"
    let repoFile = Path.Combine(work, ".SageFs", "settings.json")
    Expect.isTrue (File.Exists repoFile) "the repo layer is the session directory's own .SageFs/settings.json"
    Expect.stringContains (File.ReadAllText repoFile) perTestKey "the repo file holds the per-test timeout"

    // 3. The same rows after a restart: what the page saved is what the daemon uses, so nothing more is said about in use.
    killDaemon first
    let second = start []
    do! up second
    let! _ = page.GotoAsync(sprintf "http://localhost:%d/dashboard/settings" second.DashboardPort)
    do! waitForSource page warmup "global"
    let! facts = factsOf page warmup
    expectCell "warm-up limit saved, after the restart" facts "90" "global"
    Expect.isFalse (facts.Text.Contains "In use now") "after the restart the saved 90 is the one in use"
    // A live setting saved from the page is the one in force after a restart: `applyAtStart` pushed it into Timeouts.
    do! waitForSource page perTestKey "global"
    let! facts = factsOf page perTestKey
    expectCell "per-test timeout, after the restart" facts "42" "global"
    Expect.isFalse (facts.Text.Contains "In use now") "after the restart the saved 42 is the per-test timeout in force"
    let! facts = factsOf page abandoned
    expectCell "reaper off, after the restart" facts "0" "global"
    Expect.isFalse (facts.Text.Contains "In use now") "after the restart the reaper is off, as saved"
    let! facts = factsOf page delta
    expectCell "metadata delta off, after the restart" facts "off" "global"
    Expect.isFalse (facts.Text.Contains "In use now") "after the restart metadata delta is off, as saved"
    let! facts = factsOf page buildKey
    expectCell "build timeout saved, after the restart" facts "25" "global"
    Expect.isFalse (facts.Text.Contains "In use now") "after the restart the saved 25 minutes is the one in use"
    let! facts = factsOf page updateKey
    expectCell "update check off, after the restart" facts "on" "global"
    Expect.isFalse (facts.Text.Contains "In use now") "after the restart the update check is off, as saved"
    // A saved tier is a person's choice: the daemon reads it as if they had set the variable.
    let! facts = factsOf page tierKey
    expectCell "machine tier saved, after the restart" facts "fast" "global"
    Expect.isFalse (facts.Text.Contains "In use now") "after the restart the daemon is on the tier that was saved"

    // 4. A variable overrides what was saved, and the row says so, naming the variable.
    killDaemon second
    let third =
      start
        [ "SAGEFS_WARMUP_INACTIVITY_SECONDS", "15"
          "SAGEFS_BUILD_TIMEOUT_MINUTES", "7"
          "SAGEFS_BIND_HOST", "127.0.0.1" ]
    do! up third
    let! _ = page.GotoAsync(sprintf "http://localhost:%d/dashboard/settings" third.DashboardPort)
    do! waitForSource page warmup "environment"
    let! facts = factsOf page warmup
    expectCell "warm-up limit with the variable set" facts "15" "environment"
    Expect.stringContains facts.Text "Saved 90. Ignored while SAGEFS_WARMUP_INACTIVITY_SECONDS is set." "the row names the variable and the saved value"
    Expect.isFalse (facts.Text.Contains "In use now") "the variable's 15 is what the daemon uses"
    do! waitForSource page buildKey "environment"
    let! facts = factsOf page buildKey
    expectCell "build timeout with the variable set" facts "7" "environment"
    Expect.stringContains facts.Text "Saved 25. Ignored while SAGEFS_BUILD_TIMEOUT_MINUTES is set." "the row names the variable and the saved value"
    let! facts = factsOf page bindHostKey
    expectCell "bind host from the variable" facts "127.0.0.1" "environment"

    // 5. At a narrow width the rows wrap and the page never scrolls sideways.
    do! page.SetViewportSizeAsync(narrowViewportWidth, narrowViewportHeight)
    let! fits = page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1")
    Expect.isTrue fits "the settings page does not scroll sideways at a narrow width"

    // 6. Through all of it, not one console or page error.
    Expect.isEmpty (List.ofSeq errors) (sprintf "zero console and page errors across the journey, got: %s" (String.concat " | " errors))
    reachedTheEnd <- true
  finally
    if not reachedTheEnd then for d in daemons do dumpLogs d
    for d in daemons do killDaemon d
  for d in daemons do do! awaitDrains d
  try Directory.Delete(root, true) with _ -> ()
}

[<Tests>]
let tests =
  testSequenced <|
  testList "Settings browser journeys" [
    testTask "[Integration] Settings browser: a value saved from the page is in force after a restart, an environment variable overrides it and says so, and read-only rows have no control, with zero console errors" {
      do! journey () }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
