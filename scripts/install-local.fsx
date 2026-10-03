// scripts/install-local.fsx [<commit>] [--build] [--force]   make the daemon you use the build of <commit>
// scripts/install-local.fsx --status                         say how far the running daemon lags master
//
// Run with: dotnet fsi scripts/install-local.fsx -- <args>
//
// The daemon this machine runs must be the build the work is done against. A daemon that lags the code
// costs signal twice: a session that loads a newer SageFs.Core than the daemon refuses with a version
// mismatch, and a fix that is merged but not running looks like a fix that does not work. So the build is
// installed and the daemon restarted as part of the workflow, not as an afterthought.
//
//   <commit>   default HEAD. With a gate pass, the nupkg the gate itself built for that commit is installed
//              (the exact bytes CI will publish). Without one, pass --build.
//   --build    pack the checkout at <commit> under a unique local version suffix (NuGet serves a stale cached
//              package for a reused version, so the version always changes) and install that.
//   --force    restart even when sessions are open (they are dropped and not resumed). scripts/ship.fsx always
//              passes it: upgrading the daemon beats whatever another agent had open.
//
// Every step is fatal on failure, and success is only reported after the daemon that answers has been read
// back and reports the version that was asked for.
open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Threading

// ── named values ─────────────────────────────────────────────────────────────

let mcpPort = match Environment.GetEnvironmentVariable "SAGEFS_MCP_PORT" with | null | "" -> 37749 | p -> int p
let healthProbeTimeout = TimeSpan.FromSeconds 3.
let sessionsProbeTimeout = TimeSpan.FromSeconds 5.
let daemonStopTimeout = TimeSpan.FromSeconds 30.
let daemonStartTimeout = TimeSpan.FromSeconds 90.
let pollEvery = TimeSpan.FromSeconds 1.
let shortShaLength = 8
let localVersionTag = "local"

/// Why the script stopped. One exit code per kind, so a caller can tell them apart.
type Failure =
  | Usage of string
  | NothingToInstall of string
  | PackFailed of string
  | InstallFailed of string
  | OpenSessions of count: int
  | OldDaemonStillUp
  | NewDaemonNotUp of logPath: string
  | WrongVersionRunning of expected: string * actual: string

let exitCodeOf = function
  | Usage _ -> 64
  | NothingToInstall _ -> 3
  | PackFailed _ | InstallFailed _ -> 4
  | OpenSessions _ -> 5
  | OldDaemonStillUp -> 6
  | NewDaemonNotUp _ | WrongVersionRunning _ -> 7

let describe = function
  | Usage m | NothingToInstall m | PackFailed m | InstallFailed m -> m
  | OpenSessions n -> sprintf "the daemon has %d open session(s) and --force was not given" n
  | OldDaemonStillUp -> sprintf "the old daemon is still answering on %d after %.0fs; stop it and rerun" mcpPort daemonStopTimeout.TotalSeconds
  | NewDaemonNotUp log -> sprintf "the new daemon did not answer on %d within %.0fs; log: %s" mcpPort daemonStartTimeout.TotalSeconds log
  | WrongVersionRunning (expected, actual) -> sprintf "the daemon that answered reports '%s', not %s" actual expected

exception Stop of Failure
let fail f = raise (Stop f)
let say (s: string) = printfn "install-local: %s" s

// ── processes and probes ─────────────────────────────────────────────────────

let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
let gateRoot =
  match Environment.GetEnvironmentVariable "SAGEFS_GATE_HOME" with
  | null | "" -> Path.Combine(home, ".local", "share", "sagefs-gate")
  | p -> p
let markDir =
  match Environment.GetEnvironmentVariable "SAGEFS_INSTALL_STATE" with
  | null | "" -> Path.Combine(home, ".local", "state", "sagefs-install")
  | p -> p
Directory.CreateDirectory markDir |> ignore

/// Runs a program to completion and returns its exit code and combined output.
let run (file: string) (args: string list) (cwd: string) : int * string =
  let psi = ProcessStartInfo(file)
  psi.WorkingDirectory <- cwd
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  args |> List.iter psi.ArgumentList.Add
  use p = Process.Start psi
  let out = p.StandardOutput.ReadToEndAsync()
  let err = p.StandardError.ReadToEndAsync()
  p.WaitForExit()
  p.ExitCode, out.Result + err.Result

let git (repo: string) args = run "git" ("-C" :: repo :: args) repo
let gitOut repo args =
  match git repo args with
  | 0, o -> o.Trim()
  | code, o -> failwithf "git %s failed (%d): %s" (String.Join(" ", args)) code o

let http = new HttpClient()
let probe (path: string) (timeout: TimeSpan) : string option =
  try
    use cts = new CancellationTokenSource(timeout)
    let r = http.GetAsync(sprintf "http://localhost:%d%s" mcpPort path, cts.Token).Result
    if r.IsSuccessStatusCode then Some (r.Content.ReadAsStringAsync().Result) else None
  with _ -> None

let daemonUp () = (probe "/health" healthProbeTimeout).IsSome
let openSessionCount () =
  match probe "/api/sessions" sessionsProbeTimeout with
  | Some body -> System.Text.RegularExpressions.Regex.Matches(body, "\"id\"").Count
  | None -> 0

let waitUntil (timeout: TimeSpan) (condition: unit -> bool) =
  let deadline = DateTime.UtcNow + timeout
  let mutable ok = condition ()
  while not ok && DateTime.UtcNow < deadline do
    Thread.Sleep pollEvery
    ok <- condition ()
  ok

let versionIn (text: string) =
  let m = System.Text.RegularExpressions.Regex.Match(text, @"\d+\.\d+\.\d+[^\s]*")
  if m.Success then m.Value else ""

let versionAt (repo: string) (sha: string) =
  let props = gitOut repo [ "show"; sprintf "%s:Directory.Build.props" sha ]
  let m = System.Text.RegularExpressions.Regex.Match(props, "<Version>([^<]+)</Version>")
  if m.Success then m.Groups[1].Value else failwithf "no <Version> in Directory.Build.props at %s" sha

// ── modes ────────────────────────────────────────────────────────────────────

type Mode =
  | Status
  | Install of sha: string option * build: bool * force: bool

let parse (argv: string list) : Mode =
  match argv with
  | [ "--status" ] -> Status
  | _ ->
    let flags = argv |> List.filter (fun a -> a.StartsWith "-")
    let plain = argv |> List.filter (fun a -> not (a.StartsWith "-"))
    match flags |> List.tryFind (fun f -> f <> "--build" && f <> "--force") with
    | Some bad -> fail (Usage (sprintf "unknown option %s" bad))
    | None ->
      match plain with
      | [] -> Install (None, List.contains "--build" flags, List.contains "--force" flags)
      | [ sha ] -> Install (Some sha, List.contains "--build" flags, List.contains "--force" flags)
      | _ -> fail (Usage "at most one commit")

let status (repo: string) =
  let read name = let p = Path.Combine(markDir, name) in if File.Exists p then File.ReadAllText(p).Trim() else ""
  let sha = read "sha"
  let running = match run "sagefs" [ "status" ] repo with | 0, o -> versionIn o | _ -> ""
  let running = if running = "" then "not running" else running
  if sha = "" then
    say (sprintf "no install recorded. Running daemon: %s. Master is at %s." running (versionAt repo "master"))
    2
  else
    let behind = match git repo [ "rev-list"; "--count"; sprintf "%s..master" sha ] with | 0, o -> o.Trim() | _ -> "?"
    say (sprintf "installed %s (%s), running: %s, master is %s commits ahead" (sha.Substring(0, shortShaLength)) (read "version") running behind)
    if behind = "0" then 0 else 2

let install (repo: string) (shaArg: string option) (build: bool) (force: bool) =
  let sha = gitOut repo [ "rev-parse"; sprintf "%s^{commit}" (defaultArg shaArg "HEAD") ]
  let short = sha.Substring(0, shortShaLength)
  let version = versionAt repo sha

  // 1. Where the package comes from.
  let gated = Path.Combine(gateRoot, "passed", sha, "release", sprintf "SageFs.%s.nupkg" version)
  let packageDir, installVersion =
    if File.Exists gated && not build then
      say (sprintf "using the nupkg the gate built for %s (%s)" short version)
      Path.GetDirectoryName gated, version
    else
      if not build then
        fail (NothingToInstall (sprintf "no gate pass for %s, so no gated package. Run dotnet fsi scripts/local-gate.fsx -- %s, or pass --build to pack this checkout." short short))
      if sha <> gitOut repo [ "rev-parse"; "HEAD" ] then
        fail (NothingToInstall (sprintf "--build packs the checkout, which is not at %s" short))
      if (gitOut repo [ "status"; "--porcelain"; "--untracked-files=no" ]) <> "" then
        fail (NothingToInstall "tracked files have uncommitted changes; commit or stash them so the package matches the commit")
      // The commit's own time goes in as a number, so a newer commit always sorts as a newer version.
      let commitTime = gitOut repo [ "show"; "-s"; "--format=%ct"; sha ]
      let v = sprintf "%s-%s.%s.%s" version localVersionTag commitTime short
      let dir = Path.Combine(markDir, "pack-" + short)
      if Directory.Exists dir then Directory.Delete(dir, true)
      Directory.CreateDirectory dir |> ignore
      say (sprintf "packing %s as %s" short v)
      match run "nice" [ "-n"; "10"; "dotnet"; "pack"; "SageFs"; "-c"; "Release"; "-o"; dir; sprintf "-p:Version=%s" v; "--nologo"; "-v"; "q" ] repo with
      | 0, _ -> dir, v
      | _, o -> fail (PackFailed ("dotnet pack failed:\n" + o))

  // 2. Open sessions are dropped only when asked.
  let open' = openSessionCount ()
  if open' > 0 && not force then fail (OpenSessions open')
  if open' > 0 then say (sprintf "dropping %d open session(s), as --force says" open')

  // 3. Install the package as the global tool, and prove the tool is the right version.
  say (sprintf "installing sagefs %s as the global tool" installVersion)
  // Uninstall first: `dotnet tool update` refuses to go to a lower version, and installing an older gated
  // build is a legitimate thing to do. Nothing to uninstall is fine.
  run "dotnet" [ "tool"; "uninstall"; "--global"; "sagefs" ] repo |> ignore
  match run "dotnet" [ "tool"; "install"; "--global"; "sagefs"; "--add-source"; packageDir; "--version"; installVersion ] repo with
  | 0, _ -> ()
  | _, o -> fail (InstallFailed ("dotnet tool install failed:\n" + o))
  let toolVersion = versionIn (snd (run "sagefs" [ "--version" ] repo))
  if not (toolVersion.StartsWith version) then fail (WrongVersionRunning (version, toolVersion))

  // 4. Stop the old daemon.
  if daemonUp () then
    say "stopping the running daemon"
    run "timeout" [ string (int daemonStopTimeout.TotalSeconds); "sagefs"; "stop" ] repo |> ignore
    if not (waitUntil daemonStopTimeout (fun () -> not (daemonUp ()))) then fail OldDaemonStillUp

  // 5. Start the new one, detached from this process (setsid), with its output in a log. The one-line sh
  //    only redirects the daemon's output to the log; it does no scripting.
  say "starting the new daemon"
  let log = Path.Combine(markDir, sprintf "daemon-%s.log" short)
  run "setsid" [ "--fork"; "sh"; "-c"; sprintf "exec sagefs --no-resume > '%s' 2>&1 < /dev/null" log ] repo |> ignore
  if not (waitUntil daemonStartTimeout daemonUp) then fail (NewDaemonNotUp log)

  // 6. Read the version back from the daemon that answered.
  let running = match run "sagefs" [ "status" ] repo with | _, o -> versionIn o
  if not (running.StartsWith version) then fail (WrongVersionRunning (version, running))

  File.WriteAllText(Path.Combine(markDir, "sha"), sha + "\n")
  File.WriteAllText(Path.Combine(markDir, "version"), installVersion + "\n")
  say (sprintf "daemon is %s (%s), answering on %d. Log: %s" running short mcpPort log)
  0

let argv =
  fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

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
  // Start from the directory this script lives in, so the walk up finds the repo root at run time
  // rather than a build-time constant naming where it was compiled.
  let start =
    try Path.GetDirectoryName __SOURCE_DIRECTORY__ with _ -> "."
  walk start 0

let code =
  try
    let repo = gitOut repoRoot [ "rev-parse"; "--show-toplevel" ]
    match parse argv with
    | Status -> status repo
    | Install (sha, build, force) -> install repo sha build force
  with
  | Stop f ->
    eprintfn "install-local: %s" (describe f)
    exitCodeOf f

exit code
