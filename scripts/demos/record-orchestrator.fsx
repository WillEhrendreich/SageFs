// scripts/demos/record-orchestrator.fsx -- parallel, fully-headless demo-recording harness.
// Run with: dotnet fsi scripts/demos/record-orchestrator.fsx -- --job '<spec>' [--job '<spec>' ...] [options]
//
// Given a list of demo "jobs", this allocates a distinct X display number, a distinct free daemon port, and a
// distinct temp SAGEFS_DATA_DIR to each job, then runs each job through the existing generic recorders:
//   - record-x11.fsx (Xvfb + ffmpeg x11grab -> gif) for "video" jobs
//   - record-terminal.fsx (asciinema + agg -> gif) for "terminal" jobs
//
// Concurrency: video jobs are bounded by --pool (default 2), because x11grab captures live frames and CPU/GPU
// contention drops them. Terminal jobs run fully unbounded, because asciinema records an event stream, not live
// frames: it is not timing sensitive, so contention there only costs CPU, not correctness.
//
// This script does not know anything about SageFs beyond the isolation convention (an mcp port and a
// SAGEFS_DATA_DIR handed to the job's own driver). Each job's driver (e.g. drive-dashboard.fsx) is the part that
// actually knows how to stand up and drive a SageFs daemon for its particular demo. This script does NOT edit or
// replace record-x11.fsx / record-terminal.fsx; it drives them.
//
// Job spec (repeatable --job flag), semicolon-separated key=value pairs:
//   name=NAME       unique job name (used for logs/temp dirs/default output)
//   type=video|terminal   which recorder to use
//   driver=PATH     the driver to invoke with the isolated resources: a .fsx (run with `dotnet fsi`) or a program
//   duration=SECONDS   recording duration, video jobs only (default 6)
//   output=PATH     where to write the final .gif (default <out-dir>/<name>.gif)
//   args=EXTRA ARGS    extra args appended verbatim to the driver invocation (e.g. --sagefs-bin / --chromium)
//
// Every video-job driver is invoked as:    <driver> --mcp-port PORT --data-dir DIR --duration SECONDS <args>
// with $DISPLAY set by record-x11 to this job's allocated display.
// Every terminal-job driver is invoked as: <driver> --mcp-port PORT --data-dir DIR <args>   (no $DISPLAY)
//
// Example (the dashboard proof run):
//   dotnet fsi scripts/demos/record-orchestrator.fsx -- --pool 2 \
//     --job 'name=dashboard;type=video;driver=scripts/demos/drive-dashboard.fsx;duration=6;args=--sagefs-bin /path/to/SageFs --chromium /usr/bin/chromium'
//
// Options:
//   --pool N            max concurrent VIDEO jobs (default 2); terminal jobs are never pool-limited
//   --base-display N    first Xvfb display NUMBER; job i gets :N+i (default 100; never :99, which manual tooling uses)
//   --base-port N       first daemon port; job i gets N+i*step (default 46000). Keep clear of 37749/37750 (the real
//                       daemon) and any other range in use.
//   --port-step N       port spacing between jobs (default 10: a daemon uses its port and port+1 for the dashboard;
//                       the gap leaves room for the worker's own dynamic port allocation)
//   --size WxH          virtual screen size for video jobs (default 1280x800)
//   --out-dir DIR       default directory for job output gifs (default scripts/demos/out)
//   --record-x11 PATH, --record-terminal PATH   the recorders (default: alongside this script)
//   --keep-status       do not delete the per-run status dir (job logs, markers) on exit
//
// Exit: 0 every job passed and left nothing running; 1 a job failed or left a process behind; 64 bad arguments;
// 3 a tool is missing.
#load "demo-common.fsx"
open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading
open DemoCommon

let script = "record-orchestrator"

// ── named values ─────────────────────────────────────────────────────────────

let defaultPool = 2
let defaultBaseDisplay = 100
let defaultBasePort = 46000
let defaultPortStep = 10
let defaultSize = "1280x800"
let defaultVideoSeconds = 6
let poolPollEvery = TimeSpan.FromSeconds 1.
/// A finished gif has at least this many frames, or nothing moved.
let minimumFrames = 2
// Locate the repo at RUNTIME, walking up from this script's own location until we
// find the solution file. A build-time constant would bake in the directory the
// script was COMPILED, which is not where it RUNS. The sibling record-*.fsx scripts and the default
// output directory are then named under scripts/demos/, found at run time.
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
  let start =
    try Path.GetDirectoryName __SOURCE_DIRECTORY__ with _ -> "."
  walk start 0
let here =
  if repoRoot = "" then
    failwith "Could not locate the SageFs repository (no SageFs.slnx found walking up from this script)."
  else Path.Combine(repoRoot, "scripts", "demos")

type Kind =
  | Video
  | Terminal

let kindOf (text: string) : Kind =
  match text with
  | "video" -> Video
  | "terminal" -> Terminal
  | other -> fail (Usage (sprintf "type must be video or terminal, not '%s'" other))

let kindName = function Video -> "video" | Terminal -> "terminal"

type Job =
  { Name: string
    Kind: Kind
    Driver: string
    Duration: int
    Output: string option
    Args: string }

/// `key=value` pairs separated by semicolons. An unknown key is refused.
let parseJob (spec: string) : Job =
  let fields =
    spec.Split(';', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun pair ->
      match pair.IndexOf '=' with
      | -1 -> fail (Usage (sprintf "job field without '=' in spec: %s" spec))
      | i -> pair.Substring(0, i), pair.Substring(i + 1))
  for key, _ in fields do
    if not (List.contains key [ "name"; "type"; "driver"; "duration"; "output"; "args" ]) then
      fail (Usage (sprintf "unknown job field '%s' in spec: %s" key spec))
  let get key = fields |> Array.tryFindBack (fun (k, _) -> k = key) |> Option.map snd
  let name = match get "name" with Some n when n <> "" -> n | _ -> fail (Usage (sprintf "job spec missing name=: %s" spec))
  let kind = match get "type" with Some t -> kindOf t | None -> fail (Usage (sprintf "job '%s': type must be video or terminal" name))
  let driver = match get "driver" with Some d when d <> "" -> d | _ -> fail (Usage (sprintf "job '%s': driver= is required" name))
  if not (File.Exists driver) then fail (Usage (sprintf "job '%s': driver not found: %s" name driver))
  let duration =
    match get "duration" with
    | None -> defaultVideoSeconds
    | Some d ->
      match Int32.TryParse d with
      | true, n -> n
      | false, _ -> fail (Usage (sprintf "job '%s': duration needs a whole number, not '%s'" name d))
  { Name = name; Kind = kind; Driver = driver; Duration = duration; Output = get "output" |> Option.filter (fun o -> o <> ""); Args = defaultArg (get "args") "" }

/// What one job is given: its own display, ports and data directory, and where its gif goes.
type Resources =
  { Display: string
    McpPort: int
    DataDir: string
    Output: string }

/// One word of a command string, quoted so `splitCommand` reads it back as one word.
let quoted (word: string) : string = "\"" + word.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

/// A program that is a .fsx runs under `dotnet fsi` (the script's own arguments follow `--`); any other runs as it is.
let invocation (program: string) : string * string list =
  match program.EndsWith ".fsx" with
  | true -> "dotnet", [ "fsi"; program; "--" ]
  | false -> program, []

/// The command string record-x11 / record-terminal run for a job's driver.
let driverCommand (job: Job) (r: Resources) : string =
  let file, lead = invocation job.Driver
  let words =
    [ quoted file ] @ (lead |> List.map quoted) @ [ "--mcp-port"; string r.McpPort; "--data-dir"; quoted r.DataDir ]
    @ (match job.Kind with Video -> [ "--duration"; string job.Duration ] | Terminal -> [])
  String.Join(" ", words) + (if job.Args <> "" then " " + job.Args else "")

/// The command line that starts a recorder for a job.
let recorderArgs (recorder: string) (job: Job) (r: Resources) (size: string) : string * string list =
  let file, lead = invocation recorder
  let common = [ "--output"; r.Output; "--command"; driverCommand job r ]
  let own =
    match job.Kind with
    | Video -> [ "--display"; r.Display; "--size"; size; "--duration"; string job.Duration ]
    | Terminal -> []
  file, lead @ own @ common

// ── what a finished job left ─────────────────────────────────────────────────

type GifFacts = { Dims: string; Frames: string; Bytes: int64 }

let probeGif (path: string) : GifFacts option =
  match File.Exists path && FileInfo(path).Length > 0L with
  | false -> None
  | true ->
    let ask (extra: string list) (entries: string) (format: string) =
      let c = run "ffprobe" ([ "-v"; "error" ] @ extra @ [ "-select_streams"; "v"; "-show_entries"; entries; "-of"; format; path ])
      if c.ExitCode = 0 then c.Stdout.Trim() else ""
    let frames =
      match ask [] "stream=nb_frames" "default=noprint_wrappers=1:nokey=1" with
      | "" | "N/A" ->
        match ask [ "-count_frames" ] "stream=nb_read_frames" "default=noprint_wrappers=1:nokey=1" with
        | "" -> "0"
        | counted -> counted
      | reported -> reported
    Some { Dims = (match ask [] "stream=width,height" "csv=p=0:s=x" with "" -> "unknown" | d -> d); Frames = frames; Bytes = FileInfo(path).Length }

/// The gif is good when it has content and more than one frame.
let gifIsGood (facts: GifFacts option) : bool =
  match facts with
  | Some f -> f.Bytes > 0L && (match Int32.TryParse f.Frames with | true, n -> n >= minimumFrames | false, _ -> false)
  | None -> false

/// Processes whose command line names this job's own data directory: the only thing that identifies a process
/// as one this run started. Matched on the path, never on a process name, so other SageFs daemons already
/// running on this machine are left completely alone.
let strayProcesses (snapshot: string) (dataDir: string) : (int * string) list =
  snapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries)
  |> Array.choose (fun line ->
    let t = line.Trim()
    match t.IndexOf ' ' with
    | -1 -> None
    | i ->
      match Int32.TryParse(t.Substring(0, i)) with
      | true, pid when t.Substring(i + 1).Contains dataDir -> Some (pid, t.Substring(i + 1).Trim())
      | _ -> None)
  |> Array.toList

// ── the run ──────────────────────────────────────────────────────────────────

let spec : ArgSpec =
  { Valued = [ "--job"; "--pool"; "--base-display"; "--base-port"; "--port-step"; "--size"; "--out-dir"; "--record-x11"; "--record-terminal" ]
    Switches = [ "--keep-status" ] }

let usage =
  "usage: record-orchestrator.fsx --job '<spec>' [--job '<spec>' ...] [--pool N] [--base-display N] [--base-port N] [--port-step N] [--size WxH] [--out-dir DIR] [--record-x11 PATH] [--record-terminal PATH] [--keep-status]"

/// `--job` repeats, which the flag map cannot hold; they are read from the raw arguments.
let jobSpecs (argv: string list) : string list =
  argv |> List.pairwise |> List.choose (fun (a, b) -> if a = "--job" then Some b else None)

type Running = { Job: Job; Resources: Resources; Recorder: Started }

let orchestrate (argv: string list) (p: Parsed) : int =
  let specs = jobSpecs argv
  if specs.IsEmpty then fail (Usage "at least one --job is required")
  let jobs = specs |> List.map parseJob
  let pool = intValue p "--pool" defaultPool
  let baseDisplay = intValue p "--base-display" defaultBaseDisplay
  let basePort = intValue p "--base-port" defaultBasePort
  let portStep = intValue p "--port-step" defaultPortStep
  let size = value p "--size" defaultSize
  let outDir = value p "--out-dir" (Path.Combine(here, "out"))
  let recordX11 = value p "--record-x11" (Path.Combine(here, "record-x11.fsx"))
  let recordTerminal = value p "--record-terminal" (Path.Combine(here, "record-terminal.fsx"))
  for r in [ recordX11; recordTerminal ] do
    if not (File.Exists r) then fail (MissingTool (sprintf "recorder not found at %s" r))
  requireTools [ "ffprobe"; "ps" ]
  Directory.CreateDirectory outDir |> ignore
  let status = Path.Combine(Path.GetTempPath(), "record-orchestrator." + Guid.NewGuid().ToString("N").Substring(0, 6))
  Directory.CreateDirectory status |> ignore
  log script (sprintf "status dir: %s" status)
  let keep = p.On.Contains "--keep-status"
  let running = List<Running>()
  let finishedRc = Dictionary<string, int>()
  try
    // Launch every job, bounding only the video pool: a terminal job never waits on it.
    let videosRunning () = running |> Seq.filter (fun r -> r.Job.Kind = Video && alive r.Recorder.Process) |> Seq.length
    jobs
    |> List.iteri (fun idx job ->
      if job.Kind = Video then
        while videosRunning () >= pool do Thread.Sleep poolPollEvery
      let dataDir = Path.Combine(status, sprintf "%s-data.%d" job.Name idx)
      Directory.CreateDirectory dataDir |> ignore
      let output = job.Output |> Option.defaultValue (Path.Combine(outDir, job.Name + ".gif"))
      Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath output) |> Option.ofObj |> Option.defaultValue ".") |> ignore
      let resources = { Display = sprintf ":%d" (baseDisplay + idx); McpPort = basePort + idx * portStep; DataDir = dataDir; Output = output }
      // The markers are the ONLY thing that identifies "processes this job owns" to the rest of this script.
      File.WriteAllLines(
        Path.Combine(status, job.Name + ".markers"),
        [ sprintf "MCP_PORT=%d" resources.McpPort; sprintf "DATA_DIR=%s" dataDir; sprintf "DISPLAY_NUM=%s" resources.Display; sprintf "TYPE=%s" (kindName job.Kind) ])
      log script (sprintf "job '%s' (%s): display=%s mcp-port=%d data-dir=%s output=%s" job.Name (kindName job.Kind) resources.Display resources.McpPort dataDir output)
      let recorder = match job.Kind with Video -> recordX11 | Terminal -> recordTerminal
      let file, args = recorderArgs recorder job resources size
      let started = start file args None [] [] false (Path.Combine(status, job.Name + ".log"))
      running.Add { Job = job; Resources = resources; Recorder = started })
    log script (sprintf "all %d job(s) launched, waiting for completion" running.Count)
    for r in running do
      r.Recorder.Process.WaitForExit()
      Threading.Tasks.Task.WaitAll(r.Recorder.Pumps |> List.toArray)
      finishedRc[r.Job.Name] <- r.Recorder.Process.ExitCode
      File.WriteAllText(Path.Combine(status, r.Job.Name + ".rc"), string r.Recorder.Process.ExitCode + "\n")
    log script "all jobs finished"
    // Verify each job's output gif.
    let mutable allOk = true
    log script "----- results -----"
    for r in running do
      let rc = finishedRc[r.Job.Name]
      let facts = probeGif r.Resources.Output
      let ok = rc = 0 && gifIsGood facts
      if not ok then allOk <- false
      let dims, frames, bytes = match facts with Some f -> f.Dims, f.Frames, f.Bytes | None -> "", "0", 0L
      log script (sprintf "%s  job=%s  recorder-exit=%d  output=%s  dims=%s  frames=%s  bytes=%d" (if ok then "PASS" else "FAIL") r.Job.Name rc r.Resources.Output dims frames bytes)
      if not ok then log script (sprintf "  see %s for detail" (Path.Combine(status, r.Job.Name + ".log")))
    // Sweep: confirm no process this run started is still alive. The process snapshot is taken ONCE, before
    // any per-job filtering, so a filter can never show up as a false stray against its own argument.
    log script "----- sweep -----"
    let snapshot = (run "ps" [ "-eo"; "pid=,args=" ]).Stdout
    let mutable strays = false
    for r in running do
      for pid, args in strayProcesses snapshot r.Resources.DataDir do
        strays <- true
        log script (sprintf "stray process for job '%s': pid=%d args=%s" r.Job.Name pid args)
        (try Process.GetProcessById(pid).Kill() with _ -> ())
        log script (sprintf "  killed pid %d" pid)
    match strays with
    | false -> log script "no stray processes found for any job: all daemons/workers/browsers/Xvfb torn down cleanly"
    | true -> log script "stray processes were found and killed above: investigate the driver's own cleanup if this recurs"
    log script "----- summary -----"
    match allOk && not strays with
    | true ->
      log script "all jobs PASSED, no leftover processes"
      0
    | false ->
      log script "one or more jobs FAILED or left stray processes, see above"
      1
  finally
    // Fires on our own interruption or an unexpected error; on the normal path every job wrapper has already
    // exited. Each wrapper's own cleanup handles its Xvfb/driver/daemon/browser tree.
    for r in running do
      if alive r.Recorder.Process then
        log script (sprintf "orchestrator exiting, stopping still-running job wrapper pid %d" r.Recorder.Process.Id)
        killTree r.Recorder.Process.Id
    if keep then log script (sprintf "status dir kept at %s" status) else try Directory.Delete(status, true) with _ -> ()

let code =
  try
    let argv = scriptArgs ()
    let p = parseArgs spec argv
    match p.Help with
    | true ->
      printfn "%s" usage
      0
    | false -> orchestrate argv p
  with Stop f ->
    eprintfn "[%s] ERROR: %s" script (describe f)
    (match f with Usage _ -> eprintfn "%s" usage | _ -> ())
    exitCodeOf f

exit code
