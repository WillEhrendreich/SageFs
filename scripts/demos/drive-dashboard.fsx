// scripts/demos/drive-dashboard.fsx -- SageFs-specific driver for the dashboard demo recording.
// Run with: dotnet fsi scripts/demos/drive-dashboard.fsx -- --mcp-port N --data-dir DIR [options]
//
// Meant to run as the --command of record-x11 (which owns Xvfb and ffmpeg). It:
//   1. starts an isolated SageFs daemon (own --mcp-port, own SAGEFS_DATA_DIR, never touches the real ~/.SageFs)
//   2. creates a bare FSI session through the daemon's HTTP API and runs a couple of small evals, so the
//      dashboard has real content to show
//   3. opens the dashboard in Chromium under the X display it inherits via $DISPLAY, and holds it open so
//      record-x11's x11grab has something to film
//   4. tears down everything it started (chromium, the daemon, and the worker process the daemon spawns)
//      before it exits
//
// Xvfb is the "headless" part here (no real monitor). Chromium itself runs in its normal windowed mode,
// not its own --headless flag, because --headless rendering never paints a window onto the X display and
// x11grab would capture nothing.
//
// This script owns cleanup of only what it starts. It never signals or kills any process by name, only
// by the pid it recorded when it started that process (or that process's own descendants).
//
// Required:  --mcp-port N   port for this job's isolated SageFs daemon (the dashboard listens on N+1). Must not
//                           collide with any other running daemon (the main daemon uses 37749/37750).
//            --data-dir DIR a fresh directory dedicated to this job; the real ~/.SageFs is never touched.
// Options:   --duration S   roughly how long the caller will record; chromium is held open for S+10s (default 6)
//            --sagefs-bin PATH  the built SageFs executable (default <repo>/SageFs/bin/Release/net10.0/SageFs)
//            --chromium PATH    a chromium binary (default: chromium or chromium-browser on PATH)
//            --workdir DIR      working directory for the FSI session (default: a fresh empty dir under --data-dir)
//            --log-dir DIR      daemon and chromium logs (default <data-dir>/logs)
//            --no-session       skip session creation and evals; show the dashboard's no-session picker landing
//
// Must be run with $DISPLAY already set (record-x11 sets it).
// Exit: 0 done; 64 bad arguments; 4 the daemon or a binary would not start; 6 the daemon never became healthy.
#load "demo-common.fsx"
open System
open System.IO
open System.Text.RegularExpressions
open DemoCommon

let script = "drive-dashboard"

// ── named values ─────────────────────────────────────────────────────────────

let defaultDuration = 6
/// Chromium is held open this long past the recording, so the caller's window fits inside it.
let holdMargin = TimeSpan.FromSeconds 10.
let daemonTtl = "5m"
let healthSeconds = 60
let readySeconds = 60
let settleAfterKill = TimeSpan.FromMilliseconds 500.
/// Plain, quote-free F# so no runtime JSON escaping is needed.
let demoEvals =
  [ "1 + 1;;"
    "let sageFsDemoNumbers = List.map (fun x -> x * 2) [ 1 .. 5 ];;"
    "System.DateTime.UtcNow.ToString();;" ]
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
  { Valued = [ "--mcp-port"; "--data-dir"; "--duration"; "--sagefs-bin"; "--chromium"; "--workdir"; "--log-dir" ]
    Switches = [ "--no-session" ] }

let usage = "usage: drive-dashboard.fsx --mcp-port N --data-dir DIR [--duration S] [--sagefs-bin PATH] [--chromium PATH] [--workdir DIR] [--log-dir DIR] [--no-session]"

/// The session id in a create response (`"message":"<id>"`), if there is one.
let sessionIdIn (response: string) : string option =
  let m = Regex.Match(response, "\"message\":\"([^\"]*)\"")
  if m.Success && m.Groups[1].Value <> "" then Some m.Groups[1].Value else None

let drive (p: Parsed) : int =
  let port = intValue p "--mcp-port" 0
  if port = 0 then fail (Usage "--mcp-port is required")
  let dataDir = required p "--data-dir"
  let duration = intValue p "--duration" defaultDuration
  let display = Environment.GetEnvironmentVariable "DISPLAY"
  if String.IsNullOrEmpty display then fail (Usage "$DISPLAY is not set; run this under record-x11")
  let dashboardPort = port + 1
  let sagefs = value p "--sagefs-bin" (Path.Combine(repoRoot, "SageFs", "bin", "Release", "net10.0", "SageFs"))
  if not (File.Exists sagefs) then fail (WouldNotStart (sprintf "sagefs binary not found or not executable: %s (build it first: dotnet build SageFs/SageFs.fsproj -c Release)" sagefs))
  let chromium = findChromium (Map.tryFind "--chromium" p.Values)
  let logDir = value p "--log-dir" (Path.Combine(dataDir, "logs"))
  Directory.CreateDirectory dataDir |> ignore
  Directory.CreateDirectory logDir |> ignore
  let workdir = value p "--workdir" (Path.Combine(dataDir, "workdir"))
  Directory.CreateDirectory workdir |> ignore
  log script (sprintf "mcp-port=%d dashboard-port=%d data-dir=%s display=%s" port dashboardPort dataDir display)
  log script (sprintf "sagefs-bin=%s chromium=%s workdir=%s" sagefs chromium workdir)
  let mutable daemon : Started option = None
  let mutable browser : Started option = None
  try
    log script "starting isolated SageFs daemon"
    daemon <-
      Some (start sagefs [ "--mcp-port"; string port; "--owner-pid"; string Environment.ProcessId; "--ttl"; daemonTtl; "--no-resume" ]
              None [ ("SAGEFS_DATA_DIR", dataDir) ] [] false (Path.Combine(logDir, "daemon.log")))
    log script (sprintf "daemon pid=%d" daemon.Value.Process.Id)
    log script (sprintf "waiting for daemon health on port %d" port)
    waitHealthy script daemon.Value.Process port healthSeconds (Path.Combine(logDir, "daemon.log"))
    let baseUrl = sprintf "http://localhost:%d" port
    let sessionId =
      match p.On.Contains "--no-session" with
      | true -> None
      | false ->
        log script (sprintf "creating a bare session at %s" workdir)
        let body = sprintf "{\"workingDirectory\": \"%s\", \"projects\": [], \"workflow\": \"Interactive\"}" workdir
        let response = post (baseUrl + "/api/sessions/create") body createSessionTimeout |> Option.defaultValue ""
        log script (sprintf "create response: %s" response)
        match sessionIdIn response with
        | Some id ->
          log script (sprintf "session %s created, waiting for it to report Ready" id)
          let readyPattern = Regex(sprintf "\"id\":\"%s\"[^}]*\"status\":\"Ready\"" (Regex.Escape id))
          let isReady () = (get (baseUrl + "/api/sessions") sessionsTimeout |> Option.map readyPattern.IsMatch) = Some true
          if waitUntil (TimeSpan.FromSeconds (float readySeconds)) isReady then log script (sprintf "session %s is Ready" id)
          log script "running small evals so the dashboard has real content"
          for code in demoEvals do
            let evalBody = sprintf "{\"code\": \"%s\"}" code
            match post (baseUrl + "/exec") evalBody evalTimeout with
            | Some reply -> File.AppendAllText(Path.Combine(logDir, "eval.log"), reply + "\n")
            | None -> log script (sprintf "eval failed for: %s (see %s), continuing anyway, the session view still proves the pipeline" code (Path.Combine(logDir, "eval.log")))
          Some id
        | None ->
          log script (sprintf "session creation did not return a session id, falling back to the no-session picker landing (see %s)" (Path.Combine(logDir, "session-create.log")))
          None
    let url =
      match sessionId with
      | Some id -> sprintf "http://localhost:%d/dashboard?session=%s" dashboardPort id
      | None -> sprintf "http://localhost:%d/dashboard" dashboardPort
    log script (sprintf "launching chromium at %s" url)
    browser <- Some (startChromium chromium dataDir url (Path.Combine(logDir, "chromium.log")))
    log script (sprintf "chromium pid=%d" browser.Value.Process.Id)
    let hold = TimeSpan.FromSeconds (float duration) + holdMargin
    log script (sprintf "holding chromium open for %.0fs (the caller's recording window fits inside this)" hold.TotalSeconds)
    Threading.Thread.Sleep hold
    log script "hold time elapsed, exiting, cleanup tears everything down"
    0
  finally
    log script (sprintf "cleanup: daemon pid=%s chromium pid=%s" (match daemon with Some d -> string d.Process.Id | None -> "none") (match browser with Some b -> string b.Process.Id | None -> "none"))
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
