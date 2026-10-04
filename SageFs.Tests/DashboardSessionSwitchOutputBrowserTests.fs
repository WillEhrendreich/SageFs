/// Clicking a session in the sidebar has to move the OUTPUT PANEL to that session, and the other session's output
/// must never show up in it. Not for a frame, not on the way there, not on the way back.
///
/// Someone clicked a Molina run and still saw Nehemiah's output. The mechanism that makes it work today is a per-tab
/// stream, a `ViewingSessionId` signal in the browser, and a click that retargets that connection's own view. The
/// output panel reads that session's buffer and never a global one. It works, and nothing locked it in: the journey
/// in DashboardBrowserTests only checks `#main`'s `data-viewing-session-id` attribute, which can be right while the
/// panel under it is wrong. This reads the panel itself.
///
/// Two real sessions on two small samples, each told to print its own marker. Then the real dashboard, in a real
/// browser: view A, click B, click A. A MutationObserver rides along the whole time and writes down every state the
/// panel passes through, so a flash of the other session's marker that lives long enough to paint fails this just as
/// hard as a stuck one.
///
/// Owns its own daemon (fresh ports, fresh SAGEFS_DATA_DIR, `--no-resume`) because it needs two sessions and the
/// shared browser daemon has one that every other journey leans on.
module SageFs.Tests.DashboardSessionSwitchOutputBrowserTests

open System
open System.IO
open System.Net.Http
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Playwright
open SageFs.Tests.DashboardBrowserTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let repoRoot = RepoPaths.repoPathFull [||]

/// Two small samples in two directories. A working directory holds one session, so two sessions need two.
let sampleA = Path.Combine(repoRoot, "samples", "demos", "SageFs.Samples.ConsoleTicker")
let projectA = Path.Combine(sampleA, "SageFs.Samples.ConsoleTicker.fsproj")
let sampleB = Path.Combine(repoRoot, "samples", "demos", "SageFs.Samples.ConsoleTicker.Tests")
let projectB = Path.Combine(sampleB, "SageFs.Samples.ConsoleTicker.Tests.fsproj")

type Daemon =
  { Process: Diagnostics.Process
    McpPort: int
    DashboardPort: int
    DataDir: string
    OutLog: string
    ErrLog: string }

/// One session under test: where it lives and the line only it prints.
type Subject =
  { Id: string
    WorkingDirectory: string
    Marker: string }

/// Drain a redirected stream into a file. Files cannot deadlock the daemon the way an unread pipe does.
let drain (stream: StreamReader) (path: string) =
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

let startDaemon () : Daemon =
  let mcpPort, dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
  let dataDir = SageFs.Tests.RunnerDirs.create SageFs.Tests.RunnerDirs.Family.BrowserRuns
  let psi = Diagnostics.ProcessStartInfo()
  psi.FileName <- SageFs.Tests.TestInfrastructure.SageFsBinary.path ()
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- repoRoot
  psi.ArgumentList.Add("--mcp-port")
  psi.ArgumentList.Add(string mcpPort)
  psi.ArgumentList.Add("--owner-pid")
  psi.ArgumentList.Add(string (Diagnostics.Process.GetCurrentProcess().Id))
  psi.ArgumentList.Add("--no-resume")
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  let proc = Diagnostics.Process.Start(psi)
  let outLog = Path.Combine(dataDir, "daemon.stdout.log")
  let errLog = Path.Combine(dataDir, "daemon.stderr.log")
  drain proc.StandardOutput outLog
  drain proc.StandardError errLog
  { Process = proc; McpPort = mcpPort; DashboardPort = dashboardPort
    DataDir = dataDir; OutLog = outLog; ErrLog = errLog }

let dumpLogs (d: Daemon) =
  for path in [ d.OutLog; d.ErrLog ] do
    try
      if File.Exists path then
        let text = File.ReadAllText(path)
        if not (String.IsNullOrWhiteSpace text) then
          eprintfn "--- %s (tail) ---" (Path.GetFileName path)
          let lines = text.Split('\n')
          lines |> Array.skip (max 0 (lines.Length - 40)) |> Array.iter (eprintfn "%s")
    with _ -> ()

/// Kill this journey's own daemon by its own process, never by name, and take its data dir with it.
let killDaemon (d: Daemon) =
  try
    if not d.Process.HasExited then d.Process.Kill(entireProcessTree = true)
  with _ -> ()
  try d.Process.WaitForExit(TestTimeouts.childExit) |> ignore with _ -> ()
  try d.Process.Dispose() with _ -> ()
  SageFs.Tests.RunnerDirs.remove d.DataDir

let closeBrowserSafely (browser: IBrowser) : Task =
  try browser.CloseAsync() with _ -> Task.CompletedTask

let apiClientWith (timeout: TimeSpan) (d: Daemon) =
  let client = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" d.McpPort))
  client.Timeout <- timeout
  client

let apiClient (d: Daemon) = apiClientWith TestTimeouts.httpRequest d

/// Poll a real condition until it holds or the budget runs out. Never a sleep and an assumption.
let waitUntil (budgetMs: int) (condition: unit -> Task<bool>) : Task<bool> = task {
  let sw = Diagnostics.Stopwatch.StartNew()
  let mutable ok = false
  while not ok && sw.ElapsedMilliseconds < int64 budgetMs do
    let! result = condition ()
    if result then ok <- true
    else do! Task.Delay(TestTimeouts.pollPage)
  return ok
}

/// The client lives for the whole wait. Returning the poll's task from under a `use` would dispose it before the
/// first probe ran, and every probe would then fail as "daemon never became healthy".
let waitHealthy (d: Daemon) : Task<bool> = task {
  use client = apiClient d
  return!
    waitUntil (TestTimeouts.asMs TestTimeouts.readyBudget) (fun () -> task {
      try
        use! resp = client.GetAsync("/health")
        return resp.IsSuccessStatusCode
      with _ -> return false
    })
}

/// `timeout` is per call because the calls are not alike: a create request builds the sample first when it is not built
/// yet (a fresh checkout), which is a build's worth of waiting, and the first eval in a session pays the FSI's own warmup.
let post (d: Daemon) (timeout: TimeSpan) (path: string) (body: obj) : Task<Result<string, string>> = task {
  use client = apiClientWith timeout d
  let payload = System.Text.Json.JsonSerializer.Serialize body
  use content = new StringContent(payload, Text.Encoding.UTF8, "application/json")
  use! resp = client.PostAsync(path, content)
  let! text = resp.Content.ReadAsStringAsync()
  match resp.IsSuccessStatusCode with
  | true -> return Ok text
  | false -> return Error (sprintf "HTTP %d: %s" (int resp.StatusCode) text)
}

let createSession (d: Daemon) (project: string) (dir: string) : Task<unit> = task {
  let! created = post d SageFs.Timeouts.browserJourneyWarmup "/api/sessions/create" {| projects = [| project |]; workingDirectory = dir |}
  match created with
  | Ok _ -> ()
  | Error why -> Tests.failtestf "session create for %s was refused: %s" dir why
}

/// (id, status, working directory) of every session the daemon holds, read straight from the API and never through
/// the browser, so "the daemon is Ready" and "the page shows it" stay two separate facts.
let sessionsNow (d: Daemon) : Task<(string * string * string) list> = task {
  use client = apiClient d
  let! body = client.GetStringAsync("/api/sessions")
  use doc = System.Text.Json.JsonDocument.Parse(body)
  return
    doc.RootElement.GetProperty("sessions").EnumerateArray()
    |> Seq.map (fun s ->
      s.GetProperty("id").GetString(), s.GetProperty("status").GetString(), s.GetProperty("workingDirectory").GetString())
    |> Seq.toList
}

let normalize (p: string) = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar)

let idFor (sessions: (string * string * string) list) (dir: string) : string =
  sessions
  |> List.tryFind (fun (_, _, wd) -> String.Equals(normalize wd, normalize dir, StringComparison.OrdinalIgnoreCase))
  |> Option.map (fun (id, _, _) -> id)
  |> Option.defaultWith (fun () -> failwithf "no session for %s among %A" dir sessions)

/// Make a session print its marker, routed by working directory so it lands in the right session.
let printMarker (d: Daemon) (subject: Subject) : Task<unit> = task {
  let! answered =
    post d TestTimeouts.requestPatience "/exec" {| code = sprintf "printfn \"%s\";;" subject.Marker; working_directory = subject.WorkingDirectory |}
  match answered with
  | Ok _ -> ()
  | Error why -> Tests.failtestf "eval in %s was refused: %s" subject.WorkingDirectory why
}

/// What the output panel says right now: whose it is, and every line in it.
let panelNow (page: IPage) : Task<string * string> = task {
  let! pair =
    page.EvaluateAsync<string[]>(
      "() => { var p = document.querySelector('#output-panel'); return p ? [p.getAttribute('data-session-id') || '', p.innerText || ''] : ['', '']; }")
  return pair.[0], pair.[1]
}

/// Write down every state the panel passes through. The observer runs inside the page, so it sees states a poll from
/// outside would miss. A "leak" is any moment the panel names one session while showing the other one's marker.
let watchForLeaks (page: IPage) (a: Subject) (b: Subject) : Task<unit> = task {
  let script =
    """([a, b]) => {
      window.__leaks = [];
      var check = function () {
        var p = document.querySelector('#output-panel');
        if (!p) return;
        var sid = p.getAttribute('data-session-id') || '';
        var text = p.innerText || '';
        if (sid === a.id && text.indexOf(b.marker) >= 0) window.__leaks.push('panel of ' + a.id + ' showed ' + b.marker);
        if (sid === b.id && text.indexOf(a.marker) >= 0) window.__leaks.push('panel of ' + b.id + ' showed ' + a.marker);
      };
      new MutationObserver(check).observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true });
      check();
    }"""
  let toWire (s: Subject) = {| id = s.Id; marker = s.Marker |}
  let! _ = page.EvaluateAsync(script, [| box (toWire a); box (toWire b) |])
  ()
}

let leaksSeen (page: IPage) : Task<string[]> =
  page.EvaluateAsync<string[]>("() => window.__leaks || []")

/// The panel is on `want` and shows its marker. Waits for the morph to land rather than reading in the gap.
let waitForPanelOn (page: IPage) (want: Subject) : Task<bool> =
  waitUntil BrowserWaits.daemonWork (fun () -> task {
    let! sid, text = panelNow page
    return sid = want.Id && text.Contains want.Marker
  })

let assertOnlyOwnOutput (page: IPage) (label: string) (own: Subject) (other: Subject) : Task<unit> = task {
  let! landed = waitForPanelOn page own
  let! sid, text = panelNow page
  landed |> Expect.isTrue (sprintf "[%s] the output panel moved to session %s and shows %s (panel says %s: %s)" label own.Id own.Marker sid text)
  text.Contains other.Marker |> Expect.isFalse (sprintf "[%s] the output panel of %s never shows %s" label own.Id other.Marker)
}

let journey () : Task<unit> = task {
  let daemon = startDaemon ()
  let mutable playwright: IPlaywright option = None
  let mutable browser: IBrowser option = None
  let mutable failure: exn option = None
  try
    let! healthy = waitHealthy daemon
    if not healthy then
      dumpLogs daemon
      Tests.failtestf "the daemon on port %d never became healthy" daemon.McpPort

    do! createSession daemon projectA sampleA
    do! createSession daemon projectB sampleB
    let! bothReady =
      waitUntil (TestTimeouts.asMs SageFs.Timeouts.browserJourneyWarmup) (fun () -> task {
        try
          let! now = sessionsNow daemon
          return now.Length >= 2 && now |> List.forall (fun (_, status, _) -> status = "Ready")
        with _ -> return false
      })
    if not bothReady then dumpLogs daemon
    bothReady |> Expect.isTrue "both sessions reached Ready on the daemon"

    let! sessions = sessionsNow daemon
    let tag = Guid.NewGuid().ToString("N").Substring(0, 8)
    let a = { Id = idFor sessions sampleA; WorkingDirectory = sampleA; Marker = sprintf "SWITCHPROBE-A-%s" tag }
    let b = { Id = idFor sessions sampleB; WorkingDirectory = sampleB; Marker = sprintf "SWITCHPROBE-B-%s" tag }
    do! printMarker daemon a
    do! printMarker daemon b

    let! pw = Playwright.CreateAsync()
    let! chromium = pw.Chromium.LaunchAsync(BrowserTypeLaunchOptions(Headless = true))
    playwright <- Some pw
    browser <- Some chromium
    let! ctx = chromium.NewContextAsync()
    let! page = ctx.NewPageAsync()
    let errors = Collections.Generic.List<string>()
    page.Console.Add(fun msg -> if msg.Type = "error" then errors.Add(sprintf "[console] %s" msg.Text))
    page.PageError.Add(fun err -> errors.Add(sprintf "[pageerror] %s" err))

    let! _ = page.GotoAsync(sprintf "http://localhost:%d/dashboard" daemon.DashboardPort)
    do! watchForLeaks page a b

    // A bare load picks any live session, so choose A the way a user does and see its own output.
    do! DashboardDom.selectViewingSession page a.Id BrowserWaits.daemonWork
    do! assertOnlyOwnOutput page "on A" a b

    // Click B's row. The panel moves to B and A's marker is gone from it.
    do! page.Locator(sprintf "#session-card-%s" b.Id).ClickAsync()
    do! assertOnlyOwnOutput page "click B" b a

    // And back. This is the direction that showed the wrong session's output.
    do! page.Locator(sprintf "#session-card-%s" a.Id).ClickAsync()
    do! assertOnlyOwnOutput page "click back to A" a b

    let! leaks = leaksSeen page
    List.ofArray leaks
    |> Expect.isEmpty (sprintf "no moment in the whole journey showed one session's marker in the other's panel: %s" (String.concat " | " leaks))

    let datastar = errors |> Seq.filter (fun m -> m.ToLowerInvariant().Contains "datastar") |> List.ofSeq
    datastar
    |> Expect.isEmpty (sprintf "zero Datastar errors (PatchElementsNoTargetsFound is the blank-screen signature): %s" (String.concat " | " datastar))
    List.ofSeq errors
    |> Expect.isEmpty (sprintf "zero console or page errors across the journey: %s" (String.concat " | " errors))
    try do! ctx.CloseAsync() with _ -> ()
  with ex ->
    failure <- Some ex
  match browser with
  | Some b -> do! closeBrowserSafely b
  | None -> ()
  playwright |> Option.iter (fun p -> try p.Dispose() with _ -> ())
  killDaemon daemon
  match failure with
  | Some ex -> return raise ex
  | None -> ()
}

[<Tests>]
let tests =
  testSequenced <|
  testList "Dashboard session switch output browser tests" [

    testTask "[Integration] Dashboard browser: clicking a session moves the output panel to it and the other session's output never shows, both ways" {
      do! journey () }
    |> Integration.register (Integration.Dedicated "--integration-browser")
  ]
