// scripts/demos/drive-cohort.fsx -- SageFs-specific driver for the COHORT landing-gate demo recording.
// Run with: dotnet fsi scripts/demos/drive-cohort.fsx -- --mcp-port N --data-dir DIR [options]
//
// Meant to run as the --command of record-x11 (which owns Xvfb and ffmpeg), exactly like drive-dashboard. It:
//   1. Builds a throwaway temp git repo carrying a real, tiny, PREBUILT Expecto fixture project via the
//      CohortOrchestrator's `setup-fixture` subcommand (see scripts/demos/cohort-orchestrator/Program.fs and
//      SageFs.Tests/CohortLandingGateIntegrationTests.fs's header for why the fixture must be prebuilt and
//      SDK-pinned before the daemon ever sees it).
//   2. Starts an ISOLATED SageFs daemon (own --mcp-port, own SAGEFS_DATA_DIR, --owner-pid/--ttl/--no-resume,
//      NEVER the real ~/.SageFs) rooted at that fixture repo.
//   3. Opens the dashboard in Chromium under the X display it inherits via $DISPLAY (forced X11 backend,
//      windowed, not --headless, which never paints onto the display and x11grab would capture nothing).
//   4. Runs the CohortOrchestrator's `run-beats` subcommand, which drives two real MCP client connections
//      ("alice", "bob") through the five demo beats (join -> disjoint claims + a real conflict ->
//      set_integration_ref + live testing -> a GOOD landing lands -> a BREAKING landing is automatically
//      BLOCKED by a real, discovered, failing test), printing each beat's MCP response to stdout so the
//      transcript proves the flow independent of the recording.
//   5. Holds Chromium open a bit longer so the final "Blocked" state stays on screen, then tears down everything
//      it started (chromium, orchestrator, daemon, and any worker the daemon spawns), never by process name,
//      only by the pid this script itself recorded.
//
// Required:  --mcp-port N   port for this job's isolated SageFs daemon (the dashboard listens on N+1). Must not
//                           collide with any other running daemon (the main daemon uses 37749/37750).
//            --data-dir DIR a fresh directory for this job's SAGEFS_DATA_DIR AND the throwaway fixture git repo.
// Options:   --duration S   roughly how long the caller records for (default 200: the full five-beat choreography,
//                           including the gate-proof poll, needs real wall-clock time)
//            --sagefs-bin PATH         the built SageFs executable (default <repo>/SageFs/bin/Release/net10.0/SageFs)
//            --chromium PATH           a chromium binary (default: chromium or chromium-browser on PATH)
//            --orchestrator-dll PATH   the built CohortOrchestrator.dll (default <repo>/scripts/demos/cohort-orchestrator/
//                                      bin/Release/net10.0/CohortOrchestrator.dll; build it first with
//                                      dotnet build scripts/demos/cohort-orchestrator/CohortOrchestrator.fsproj -c Release)
//            --log-dir DIR             daemon, chromium and orchestrator logs (default <data-dir>/logs)
//            --pause-seconds N         camera pause between beats (default 3)
//            --gate-timeout-seconds N  how long the orchestrator polls to prove the breaking landing NEVER fast-forwards
//                                      the integration branch (default 40)
//
// Must be run with $DISPLAY already set (record-x11 sets it).
// Exit: the orchestrator's own exit code; 64 bad arguments; 3 a tool is missing; 4 the fixture, the daemon or
// a binary would not start; 6 the daemon never became healthy.
#load "demo-common.fsx"
open System
open System.IO
open DemoCommon

let script = "drive-cohort"

// ── named values ─────────────────────────────────────────────────────────────

let defaultDuration = 200
let defaultPauseSeconds = 3
let defaultGateTimeoutSeconds = 40
let daemonTtl = "10m"
let healthSeconds = 120
/// Chromium paints its first frame before the orchestrator starts driving MCP calls against the same daemon.
let settleBeforeBeats = TimeSpan.FromSeconds 1.5
/// After the beats, the final BLOCKED state stays on screen this long.
let holdAfterBeats = TimeSpan.FromSeconds 10.
/// How long the navigation watcher waits for the orchestrator to write the integration session's id.
let sessionFileSeconds = 120
let xdotoolStep = TimeSpan.FromMilliseconds 200.
let xdotoolTypeDelayMs = "5"
let settleAfterKill = TimeSpan.FromMilliseconds 500.
// Locate the repo at RUNTIME, walking up from this script's own location until we
// find the solution file. A build-time constant would bake in the directory the
// script was COMPILED, which is not where it RUNS.
let repoRoot =
  let rec walk (dir: string) (depth: int) : string =
    if depth > 24 then "" else
    let full =
      try
        let f = Path.GetFullPath dir
        let r = Path.GetPathRoot f
        if f = r then f else Path.TrimEndingDirectorySeparator f
      with _ -> dir
    if File.Exists(Path.Combine(full, "SageFs.slnx")) then full
    else
      let parent = Path.GetDirectoryName full
      if String.IsNullOrEmpty parent || parent = full then ""
      else walk parent (depth + 1)
  // Start from the directory this script lives in (scripts/demos), so the walk up finds the repo
  // root; a build-time constant only names where it was built, not where it runs.
  let start =
    try Path.GetDirectoryName __SOURCE_DIRECTORY__ with _ -> "."
  walk start 0
let repo =
  if repoRoot = "" then
    failwith "Could not locate the SageFs repository (no SageFs.slnx found walking up from this script)."
  else repoRoot

let spec : ArgSpec =
  { Valued =
      [ "--mcp-port"; "--data-dir"; "--duration"; "--sagefs-bin"; "--chromium"; "--orchestrator-dll"; "--log-dir"; "--pause-seconds"; "--gate-timeout-seconds" ]
    Switches = [] }

let usage = "usage: drive-cohort.fsx --mcp-port N --data-dir DIR [--duration S] [--sagefs-bin PATH] [--chromium PATH] [--orchestrator-dll PATH] [--log-dir DIR] [--pause-seconds N] [--gate-timeout-seconds N]"

/// The cohort panels (Cohort/Lanes) only render inside a session VIEW (/dashboard?session=<id>), not on the bare
/// no-session picker landing. The orchestrator writes the real integration session id to a file the moment
/// set_integration_ref creates it; this retargets the already-open chromium window at that session's URL
/// (Ctrl+L, type, Enter) so the SSE-driven cohort panel becomes reachable for the rest of the recording.
/// Best-effort: if xdotool is missing or the navigation does not land, the orchestrator's own printed MCP transcript
/// still proves every beat; only the video's visual framing degrades.
let navigateWhenSessionKnown (display: string) (dashboardPort: int) (sessionFile: string) : unit =
  let xdotool (args: string list) =
    let c =
      Diagnostics.ProcessStartInfo("xdotool", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
    c.Environment["DISPLAY"] <- display
    args |> List.iter c.ArgumentList.Add
    try
      use p = Diagnostics.Process.Start c
      let out = p.StandardOutput.ReadToEnd()
      p.WaitForExit()
      out
    with _ -> ""
  let known () = File.Exists sessionFile && FileInfo(sessionFile).Length > 0L
  match waitUntil (TimeSpan.FromSeconds (float sessionFileSeconds)) known with
  | false -> ()
  | true ->
    let sid = File.ReadAllText(sessionFile).Trim()
    let window = (xdotool [ "search"; "--sync"; "--onlyvisible"; "--class"; "chromium" ]).Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead
    match window with
    | Some win ->
      xdotool [ "windowactivate"; win ] |> ignore
      Threading.Thread.Sleep xdotoolStep
      xdotool [ "key"; "--window"; win; "ctrl+l" ] |> ignore
      Threading.Thread.Sleep xdotoolStep
      xdotool [ "type"; "--window"; win; "--delay"; xdotoolTypeDelayMs; sprintf "http://localhost:%d/dashboard?session=%s" dashboardPort sid ] |> ignore
      xdotool [ "key"; "--window"; win; "Return" ] |> ignore
      log script (sprintf "navigated chromium to the integration session view (%s)" sid)
    | None -> log script "xdotool could not find the chromium window, session-view navigation skipped (cosmetic only)"

let drive (p: Parsed) : int =
  let port = intValue p "--mcp-port" 0
  if port = 0 then fail (Usage "--mcp-port is required")
  let dataDir = required p "--data-dir"
  let display = Environment.GetEnvironmentVariable "DISPLAY"
  if String.IsNullOrEmpty display then fail (Usage "$DISPLAY is not set; run this under record-x11")
  requireTools [ "git"; "dotnet" ]
  let dashboardPort = port + 1
  let sagefs = value p "--sagefs-bin" (Path.Combine(repoRoot, "SageFs", "bin", "Release", "net10.0", "SageFs"))
  if not (File.Exists sagefs) then fail (WouldNotStart (sprintf "sagefs binary not found or not executable: %s (build it first: dotnet build SageFs/SageFs.fsproj -c Release)" sagefs))
  let orchestrator =
    value p "--orchestrator-dll" (Path.Combine(repoRoot, "scripts", "demos", "cohort-orchestrator", "bin", "Release", "net10.0", "CohortOrchestrator.dll"))
  if not (File.Exists orchestrator) then
    fail (WouldNotStart (sprintf "orchestrator dll not found: %s (build it first: dotnet build scripts/demos/cohort-orchestrator/CohortOrchestrator.fsproj -c Release)" orchestrator))
  let chromium = findChromium (Map.tryFind "--chromium" p.Values)
  let logDir = value p "--log-dir" (Path.Combine(dataDir, "logs"))
  let pause = intValue p "--pause-seconds" defaultPauseSeconds
  let gateTimeout = intValue p "--gate-timeout-seconds" defaultGateTimeoutSeconds
  Directory.CreateDirectory dataDir |> ignore
  Directory.CreateDirectory logDir |> ignore
  let fixtureRepo = Path.Combine(dataDir, "fixture-repo")
  log script (sprintf "mcp-port=%d dashboard-port=%d data-dir=%s display=%s" port dashboardPort dataDir display)
  log script (sprintf "sagefs-bin=%s chromium=%s orchestrator-dll=%s" sagefs chromium orchestrator)
  log script (sprintf "fixture-repo=%s pause=%ds gate-timeout=%ds" fixtureRepo pause gateTimeout)
  let mutable daemon : Started option = None
  let mutable browser : Started option = None
  let mutable orchestratorRun : Started option = None
  let mutable watcher : Threading.Tasks.Task option = None
  try
    log script "setting up the fixture git repo (real, tiny, prebuilt Expecto project)"
    let setup = run "dotnet" [ orchestrator; "setup-fixture"; "--dir"; fixtureRepo ]
    File.WriteAllText(Path.Combine(logDir, "fixture-setup.log"), setup.Stdout + setup.Stderr)
    if setup.ExitCode <> 0 then fail (WouldNotStart (sprintf "fixture setup failed, see %s" (Path.Combine(logDir, "fixture-setup.log"))))
    log script (sprintf "fixture ready at %s" fixtureRepo)
    log script "starting isolated SageFs daemon rooted at the fixture repo"
    // The daemon's OWN process working directory is what set_integration_ref treats as "the main repo" (there is
    // no --working-directory flag), exactly CohortLandingGateIntegrationTests.fs's startIsolatedDaemon
    // (psi.WorkingDirectory <- workingDir).
    daemon <-
      Some (start sagefs [ "--mcp-port"; string port; "--owner-pid"; string Environment.ProcessId; "--ttl"; daemonTtl; "--no-resume" ]
              (Some fixtureRepo) [ ("SAGEFS_DATA_DIR", dataDir) ] [] false (Path.Combine(logDir, "daemon.log")))
    log script (sprintf "daemon pid=%d" daemon.Value.Process.Id)
    log script (sprintf "waiting for daemon health on port %d" port)
    waitHealthy script daemon.Value.Process port healthSeconds (Path.Combine(logDir, "daemon.log"))
    let url = sprintf "http://localhost:%d/dashboard" dashboardPort
    log script (sprintf "launching chromium at %s" url)
    browser <- Some (startChromium chromium dataDir url (Path.Combine(logDir, "chromium.log")))
    log script (sprintf "chromium pid=%d" browser.Value.Process.Id)
    Threading.Thread.Sleep settleBeforeBeats
    let sessionFile = Path.Combine(dataDir, "integration-session-id.txt")
    File.Delete sessionFile
    match onPath "xdotool" with
    | Some _ -> watcher <- Some (Threading.Tasks.Task.Run(fun () -> navigateWhenSessionKnown display dashboardPort sessionFile))
    | None -> log script "xdotool not found, session-view navigation skipped (cosmetic only)"
    log script "running the cohort orchestrator (two real MCP connections, five beats)"
    orchestratorRun <-
      Some (startEchoing "dotnet"
              [ orchestrator; "run-beats"; "--mcp-port"; string port; "--main-repo"; fixtureRepo; "--pause-seconds"; string pause
                "--gate-timeout-seconds"; string gateTimeout; "--session-file"; sessionFile ]
              None [] [] (Path.Combine(logDir, "orchestrator.log")))
    log script (sprintf "orchestrator pid=%d" orchestratorRun.Value.Process.Id)
    orchestratorRun.Value.Process.WaitForExit()
    Threading.Tasks.Task.WaitAll(orchestratorRun.Value.Pumps |> List.toArray)
    let orchestratorExit = orchestratorRun.Value.Process.ExitCode
    orchestratorRun <- None
    log script (sprintf "orchestrator exited with code %d (see %s)" orchestratorExit (Path.Combine(logDir, "orchestrator.log")))
    log script (sprintf "holding chromium open for %.0fs so the final BLOCKED state stays on screen" holdAfterBeats.TotalSeconds)
    Threading.Thread.Sleep holdAfterBeats
    log script "hold time elapsed, exiting, cleanup tears everything down"
    orchestratorExit
  finally
    let pid (s: Started option) = match s with Some x -> string x.Process.Id | None -> "none"
    log script (sprintf "cleanup: orchestrator pid=%s chromium pid=%s daemon pid=%s" (pid orchestratorRun) (pid browser) (pid daemon))
    orchestratorRun |> Option.iter (fun o -> killTree o.Process.Id)
    browser |> Option.iter (fun b -> killTree b.Process.Id)
    daemon |> Option.iter (fun d -> killTree d.Process.Id)
    Threading.Thread.Sleep settleAfterKill
    log script "cleanup done"

let code =
  try
    let p = parseArgs spec (scriptArgs ())
    match p.Help with
    | true ->
      printfn "%s" usage
      0
    | false -> drive p
  with Stop f ->
    eprintfn "[%s] ERROR: %s" script (describe f)
    (match f with Usage _ -> eprintfn "%s" usage | _ -> ())
    exitCodeOf f

exit code
