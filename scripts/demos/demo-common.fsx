// scripts/demos/demo-common.fsx -- #load'ed by the demo recorders and drivers; not run on its own.
//
// What the demo scripts share: a small `run` helper around Process, ending processes by exact pid
// (never by name), the argument parser, and the HTTP calls a driver makes against its own isolated
// SageFs daemon. Every script that loads this one says which failures it can stop with, as a closed
// union, and exits with the code that union names.
module DemoCommon

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks

// ── named values ─────────────────────────────────────────────────────────────

/// How often a wait polls.
let pollEvery = TimeSpan.FromMilliseconds 250.
/// After TERM, how long a process gets before it is killed.
let termGrace = TimeSpan.FromMilliseconds 500.
let stopGrace = TimeSpan.FromSeconds 5.
/// A health or API probe is short and bounded.
let probeTimeout = TimeSpan.FromSeconds 2.
let createSessionTimeout = TimeSpan.FromSeconds 15.
let evalTimeout = TimeSpan.FromSeconds 20.
let sessionsTimeout = TimeSpan.FromSeconds 5.

/// Why a script stopped. One exit code per kind, so a caller (record-orchestrator, a person at a shell)
/// can tell them apart.
type Failure =
  | Usage of string
  | MissingTool of string
  | WouldNotStart of string
  | CaptureFailed of string
  | DaemonNotHealthy of string
  | JobsFailed of string

let exitCodeOf = function
  | Usage _ -> 64
  | MissingTool _ -> 3
  | WouldNotStart _ -> 4
  | CaptureFailed _ -> 5
  | DaemonNotHealthy _ -> 6
  | JobsFailed _ -> 1

let describe = function
  | Usage m | MissingTool m | WouldNotStart m | CaptureFailed m | DaemonNotHealthy m | JobsFailed m -> m

exception Stop of Failure
let fail (f: Failure) = raise (Stop f)

let log (script: string) (message: string) = eprintfn "[%s] %s" script message

// ── arguments ────────────────────────────────────────────────────────────────

/// What a script accepts: flags that take a value, and flags that are on or off.
type ArgSpec = { Valued: string list; Switches: string list }

type Parsed = { Values: Map<string, string>; On: Set<string>; Help: bool }

/// `--flag value` pairs and bare switches, in any order. Anything else is a usage failure.
let parseArgs (spec: ArgSpec) (argv: string list) : Parsed =
  let rec go (p: Parsed) (rest: string list) =
    match rest with
    | [] -> p
    | ("-h" | "--help") :: _ -> { p with Help = true }
    | flag :: value :: tail when List.contains flag spec.Valued -> go { p with Values = Map.add flag value p.Values } tail
    | flag :: [] when List.contains flag spec.Valued -> fail (Usage (sprintf "%s needs a value" flag))
    | flag :: tail when List.contains flag spec.Switches -> go { p with On = Set.add flag p.On } tail
    | other :: _ -> fail (Usage (sprintf "unknown argument: %s (see --help)" other))
  go { Values = Map.empty; On = Set.empty; Help = false } argv

let value (p: Parsed) (flag: string) (fallback: string) : string = Map.tryFind flag p.Values |> Option.defaultValue fallback

let required (p: Parsed) (flag: string) : string =
  match Map.tryFind flag p.Values with
  | Some v when v <> "" -> v
  | _ -> fail (Usage (sprintf "%s is required" flag))

let intValue (p: Parsed) (flag: string) (fallback: int) : int =
  match Map.tryFind flag p.Values with
  | None -> fallback
  | Some text ->
    match Int32.TryParse text with
    | true, n -> n
    | false, _ -> fail (Usage (sprintf "%s needs a whole number, not '%s'" flag text))

/// The arguments after the script, without fsi's `--` separators.
let scriptArgs () : string list =
  fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

/// A command line in a string, split the way a shell would split it for a plain command: whitespace
/// separates words, single and double quotes group, a backslash escapes the next character. No
/// expansion of any kind; a caller that needs a shell pipeline names `sh -c "..."` itself.
let splitCommand (text: string) : string list =
  let words = ResizeArray<string>()
  let current = StringBuilder()
  let mutable inWord = false
  let mutable quote = '\000'
  let mutable escaped = false
  for c in text do
    match escaped, quote, c with
    | true, _, _ ->
      current.Append c |> ignore
      escaped <- false
    | false, '\000', '\\' | false, '"', '\\' ->
      escaped <- true
      inWord <- true
    | false, '\000', ('\'' | '"') ->
      quote <- c
      inWord <- true
    | false, q, c when q <> '\000' && c = q -> quote <- '\000'
    | false, '\000', c when Char.IsWhiteSpace c ->
      if inWord then
        words.Add(current.ToString())
        current.Clear() |> ignore
        inWord <- false
    | false, _, c ->
      current.Append c |> ignore
      inWord <- true
  if inWord then words.Add(current.ToString())
  List.ofSeq words

// ── processes ────────────────────────────────────────────────────────────────

[<DllImport("libc", EntryPoint = "kill")>]
extern int private sysKill(int pid, int signal)

let private sigterm = 15
let private sigkill = 9

type Captured = { ExitCode: int; Stdout: string; Stderr: string }

let private startInfo (file: string) (args: string list) (cwd: string option) (env: (string * string) list) (envRemove: string list) : ProcessStartInfo =
  let psi = ProcessStartInfo(file)
  psi.UseShellExecute <- false
  args |> List.iter psi.ArgumentList.Add
  cwd |> Option.iter (fun d -> psi.WorkingDirectory <- d)
  envRemove |> List.iter (psi.Environment.Remove >> ignore)
  env |> List.iter (fun (k, v) -> psi.Environment[k] <- v)
  psi

/// Runs a program to completion and returns its exit code and output.
let run (file: string) (args: string list) : Captured =
  let psi = startInfo file args None [] []
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  try
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    p.WaitForExit()
    { ExitCode = p.ExitCode; Stdout = out.Result; Stderr = err.Result }
  with ex -> { ExitCode = 127; Stdout = ""; Stderr = sprintf "could not start %s: %s" file ex.Message }

/// Is the program on PATH?
let onPath (name: string) : string option =
  Environment.GetEnvironmentVariable "PATH"
  |> Option.ofObj
  |> Option.defaultValue ""
  |> fun path -> path.Split(':', StringSplitOptions.RemoveEmptyEntries)
  |> Array.tryPick (fun dir ->
    let candidate = Path.Combine(dir, name)
    match File.Exists candidate with
    | true -> Some candidate
    | false -> None)

let requireTools (tools: string list) : unit =
  for t in tools do
    if (onPath t).IsNone then fail (MissingTool (sprintf "'%s' not found in PATH" t))

/// A process this script started, with its output going to a log file (stdout and stderr merged, as `> log 2>&1`).
type Started = { Process: Process; Pumps: Task list }

let private startWith (echo: bool) (file: string) (args: string list) (cwd: string option) (env: (string * string) list) (envRemove: string list) (detached: bool) (log: string) : Started =
  let psi =
    match detached with
    | true -> startInfo "setsid" (file :: args) cwd env envRemove
    | false -> startInfo file args cwd env envRemove
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  let p = Process.Start psi
  let sink = new StreamWriter(new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.Read))
  let gate = obj ()
  let copy (reader: StreamReader) =
    Task.Run(fun () ->
      let mutable line = reader.ReadLine()
      while not (isNull line) do
        lock gate (fun () ->
          sink.WriteLine line
          sink.Flush()
          if echo then Console.Out.WriteLine line)
        line <- reader.ReadLine())
  let out, err = copy p.StandardOutput, copy p.StandardError
  { Process = p; Pumps = [ out; err; Task.WhenAll([ out; err ]).ContinueWith(fun (_: Task) -> sink.Dispose()) ] }

/// Starts a program with its output in `log` (stdout and stderr merged). `detached` runs it under
/// `setsid`, so it leads its own process group and the whole group can be ended at once. `env` is set and
/// `envRemove` removed from the inherited environment.
let start (file: string) (args: string list) (cwd: string option) (env: (string * string) list) (envRemove: string list) (detached: bool) (log: string) : Started =
  startWith false file args cwd env envRemove detached log

/// Like `start`, and the output is also shown, as `2>&1 | tee log` does.
let startEchoing (file: string) (args: string list) (cwd: string option) (env: (string * string) list) (envRemove: string list) (log: string) : Started =
  startWith true file args cwd env envRemove false log

let alive (p: Process) : bool = try not p.HasExited with _ -> false

let waitUntil (timeout: TimeSpan) (condition: unit -> bool) : bool =
  let deadline = DateTime.UtcNow + timeout
  let mutable ok = condition ()
  while not ok && DateTime.UtcNow < deadline do
    Thread.Sleep pollEvery
    ok <- condition ()
  ok

/// The pids whose parent is `pid`, read from /proc (no pgrep needed).
let private childrenOf (pid: int) : int list =
  try
    Directory.GetDirectories "/proc"
    |> Array.choose (fun d ->
      match Int32.TryParse(Path.GetFileName d) with
      | true, child ->
        try
          let stat = File.ReadAllText(Path.Combine(d, "stat"))
          // pid (comm) state ppid ...: comm may hold spaces and parentheses, so read after the last ')'
          let after = stat.Substring(stat.LastIndexOf ')' + 2).Split(' ')
          match Int32.TryParse after[1] with
          | true, ppid when ppid = pid -> Some child
          | _ -> None
        with _ -> None
      | false, _ -> None)
    |> Array.toList
  with _ -> []

/// A pid and all of its descendants, children before parents.
let rec private treeOf (pid: int) : int list =
  (childrenOf pid |> List.collect treeOf) @ [ pid ]

/// Ends a process and everything it started: TERM to each (children first), a moment, then KILL to any
/// that remain. Only ever called with a pid this script started, never by name.
let killTree (pid: int) : unit =
  let tree = treeOf pid
  tree |> List.iter (fun p -> sysKill (p, sigterm) |> ignore)
  Thread.Sleep termGrace
  tree |> List.iter (fun p -> if Directory.Exists(sprintf "/proc/%d" p) then sysKill (p, sigkill) |> ignore)

/// Ends a process group the process leads (it was started detached): TERM to the group, KILL if it stays.
let killGroup (p: Process) : unit =
  if alive p then
    sysKill (-p.Id, sigterm) |> ignore
    if not (waitUntil stopGrace (fun () -> not (alive p))) then sysKill (-p.Id, sigkill) |> ignore

let terminate (p: Process) : unit =
  if alive p then
    sysKill (p.Id, sigterm) |> ignore
    if not (waitUntil stopGrace (fun () -> not (alive p))) then sysKill (p.Id, sigkill) |> ignore

// ── the isolated daemon and the browser ──────────────────────────────────────

let private http = lazy (new HttpClient())

let private request (method': HttpMethod) (url: string) (body: string option) (timeout: TimeSpan) : string option =
  try
    use cts = new CancellationTokenSource(timeout)
    use req = new HttpRequestMessage(method', url)
    body |> Option.iter (fun b ->
      req.Content <- new StringContent(b, Encoding.UTF8, "application/json")
      req.Headers.Add("Origin", sprintf "http://localhost:%d" (Uri(url).Port)))
    use resp = http.Value.SendAsync(req, cts.Token).GetAwaiter().GetResult()
    match resp.IsSuccessStatusCode with
    | true -> Some (resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())
    | false -> None
  with _ -> None

let get (url: string) (timeout: TimeSpan) : string option = request HttpMethod.Get url None timeout
let post (url: string) (body: string) (timeout: TimeSpan) : string option = request HttpMethod.Post url (Some body) timeout

/// Waits for the daemon's /health, bounded; fails with the log's path when it never answers or dies first.
let waitHealthy (script: string) (daemon: Process) (port: int) (seconds: int) (logFile: string) : unit =
  let healthy () = (get (sprintf "http://localhost:%d/health" port) probeTimeout).IsSome
  let ready = waitUntil (TimeSpan.FromSeconds (float seconds)) (fun () -> healthy () || not (alive daemon))
  if not (alive daemon) then fail (DaemonNotHealthy (sprintf "daemon exited before becoming healthy, see %s" logFile))
  if not (ready && healthy ()) then fail (DaemonNotHealthy (sprintf "daemon did not become healthy within %ds, see %s" seconds logFile))
  log script "daemon healthy"

/// Where a browser finds a binary: the argument, else the first of chromium and chromium-browser on PATH.
let findChromium (given: string option) : string =
  match given |> Option.orElse (onPath "chromium") |> Option.orElse (onPath "chromium-browser") with
  | Some path when File.Exists path -> path
  | _ -> fail (Usage "no chromium binary found; pass --chromium PATH")

/// Chromium on the X11 backend only, in a window the size of the virtual screen.
///
/// CRITICAL on a Wayland box: chromium defaults to the Wayland backend, which ignores $DISPLAY and renders on
/// the user's REAL screen. Force the X11 backend AND remove WAYLAND_DISPLAY so chromium can only ever reach the
/// Xvfb display, never the real compositor. A normal window (no --kiosk) cannot hijack a real display if
/// anything about the isolation is off. Chromium reads $XDG_CONFIG_HOME/chromium-flags.conf, which on this
/// machine can force `--ozone-platform=wayland` and desktop extensions (under Xvfb it SIGTRAPs), so
/// XDG_CONFIG_HOME points at an empty directory under the job's data dir. It is not --headless, which never
/// paints a window onto the display, so x11grab would capture nothing.
let startChromium (chromium: string) (dataDir: string) (url: string) (logFile: string) : Started =
  let profile = Path.Combine(dataDir, "chrome-profile")
  let xdg = Path.Combine(dataDir, "xdg-config")
  Directory.CreateDirectory profile |> ignore
  Directory.CreateDirectory xdg |> ignore
  start chromium
    [ "--no-sandbox"; "--disable-gpu"; "--disable-dev-shm-usage"; "--ozone-platform=x11"
      "--window-size=1280,800"; "--window-position=0,0"; sprintf "--user-data-dir=%s" profile
      "--no-first-run"; "--disable-fre"; "--disable-background-networking"
      "--disable-session-crashed-bubble"; "--disable-infobars"; "--autoplay-policy=no-user-gesture-required"; url ]
    None [ ("XDG_CONFIG_HOME", xdg) ] [ "WAYLAND_DISPLAY" ] false logFile
