// scripts/local-gate.fsx [<commit>]                  gate a commit (default HEAD) on this machine
// scripts/local-gate.fsx --force <commit>            re-gate even if it already passed
// scripts/local-gate.fsx --promote <commit> <dir>    copy a passed commit's release bundle into <dir>
//                                                    (used by the self-hosted "main build" job)
//
// Run with: dotnet fsi scripts/local-gate.fsx -- <args>
//
// THE gate is the pipeline itself: `dotnet fsi ci-pipeline.fsx -- ci release`, run on a CLEAN checkout of
// exactly one commit. Nothing here re-derives or re-lists stages, so the gate can never drift from what CI
// means. Running it on a clean checkout (never the working tree) is what answers "does THIS COMMIT pass"
// rather than "does my tree pass". Local dev state (Debug outputs, stray files, another agent's uncommitted
// edits) has masked real failures here more than once.
//
// A pass is recorded under $SAGEFS_GATE_HOME/passed/<sha>/: the trust ledger, the trust report table and the
// release bundle. The pre-push hook refuses to push master to a commit with no pass. The self-hosted "main
// build" job promotes that bundle instead of rebuilding it; with no pass (someone bypassed the hook) it runs
// this same gate first. There is one gate and one definition of "passed", and nothing waits on a GitHub-hosted
// runner.
//
// The pure parts (argument reading, the checkout's name, the console filter, the lease reply) are
// ReleaseRules.fs, tested in SageFs.Tests; this file is the IO around them.
//
// Exit codes: 0 passed (or already had); 1 --promote of a commit with no pass; 64 bad arguments; 9 a git step
// failed; any other code is the pipeline's own, passed through.
#load "ReleaseRules.fs"

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Text
open System.Threading

// ── named values ─────────────────────────────────────────────────────────────

let gateHome =
  match Environment.GetEnvironmentVariable "SAGEFS_GATE_HOME" with
  | null | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "share", "sagefs-gate")
  | path -> path

let passedRoot = Path.Combine(gateHome, "passed")
let logsRoot = Path.Combine(gateHome, "logs")
let ownersRoot = Path.Combine(gateHome, "owners")
let lockPath = Path.Combine(gateHome, "lock")
let currentPath = Path.Combine(gateHome, "current")
let dataDir = Path.Combine(gateHome, "data")
let tmpDir = Path.Combine(gateHome, "tmp")
let tierHistoryPath = Path.Combine(gateHome, "tier-durations.json")
let suiteHistoryPath = Path.Combine(gateHome, "suite-durations.json")

/// The daemon whose lease pool the gate joins, so agents back off while the gate runs.
let mcpPort = match Int32.TryParse(Environment.GetEnvironmentVariable "SAGEFS_MCP_PORT") with | true, p -> p | _ -> 37749
let leaseRequestTimeout = TimeSpan.FromSeconds 5.
/// How many times to ask for the lease while the answer is "wait", and how long to pause between asks:
/// two minutes in all, then the gate goes ahead without one.
let leaseAttempts = 12
let leaseRetryPause = TimeSpan.FromSeconds 10.

/// How the newest gate takes over from an older one: ask it to stop (TERM, children first), look every half
/// second for twenty seconds, then insist (KILL). The lock is held until that process really dies.
let supersedePollEvery = TimeSpan.FromMilliseconds 500.
let supersedeAttempts = 40
/// How often a gate that is queued behind another one looks at the lock.
let lockPollEvery = TimeSpan.FromMilliseconds 500.

let gitTimeout = TimeSpan.FromMinutes 5.
let helperTimeout = TimeSpan.FromSeconds 30.
/// The failed-gate summary repeats this many of the log's last matching lines on the console.
let failureTailLines = 15
let pipelineArgs = [ "fsi"; "ci-pipeline.fsx"; "--"; "ci"; "release" ]

/// Why the script stopped. One exit code per kind.
type Failure =
  | Usage of string
  | GitFailed of what: string * output: string
  | NoPassToPromote of sha: string
  | PipelineFailed of exitCode: int

let exitCodeOf = function
  | Usage _ -> 64
  | GitFailed _ -> 9
  | NoPassToPromote _ -> 1
  | PipelineFailed code -> code

let describe = function
  | Usage m -> m
  | GitFailed (what, output) -> sprintf "git %s failed: %s" what output
  | NoPassToPromote sha -> sprintf "%s has no pass to promote" sha
  | PipelineFailed code -> sprintf "the pipeline exited %d" code

exception Stop of Failure
let fail f = raise (Stop f)
let say (s: string) = printfn "local-gate: %s" s

// ── processes ────────────────────────────────────────────────────────────────

/// Runs a program to completion and returns its exit code and combined output. A program that is not there,
/// or that outlives the timeout, comes back as a non-zero code rather than an exception.
let run (file: string) (args: string list) (cwd: string) (timeout: TimeSpan) : int * string =
  try
    let psi = ProcessStartInfo(file)
    psi.WorkingDirectory <- cwd
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    args |> List.iter psi.ArgumentList.Add
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    match p.WaitForExit timeout with
    | true -> p.ExitCode, out.Result + err.Result
    | false ->
      (try p.Kill true with _ -> ())
      124, "timed out\n" + out.Result + err.Result
  with e -> 127, e.Message

let scriptsDir = __SOURCE_DIRECTORY__

let repo : string =
  match run "git" [ "-C"; scriptsDir; "rev-parse"; "--show-toplevel" ] scriptsDir helperTimeout with
  | 0, root -> root.Trim()
  | _, output -> fail (GitFailed ("rev-parse --show-toplevel", output.Trim()))

/// A git command that has to work: the gate's verdict is about a commit, so a git step failing is fatal.
let mustGit (cwd: string) (args: string list) : string =
  match run "git" args cwd gitTimeout with
  | 0, out -> out.Trim()
  | _, out -> fail (GitFailed (String.Join(" ", args), out.Trim()))

let isAlive (pid: int) : bool =
  try
    use p = Process.GetProcessById pid
    not p.HasExited
  with _ -> false

/// Kill a process and everything it spawned, children first. The gate's tree is dotnet fsi -> test processes ->
/// daemons and FSI hosts, and a daemon outlives its parent by design, so killing only the top of the tree leaks
/// processes that still hold the test ports. Walks by PID rather than by name: a pattern match on the command
/// line here would match this script's own.
let rec killTree (pid: int) : unit =
  match run "pgrep" [ "-P"; string pid ] scriptsDir helperTimeout with
  | 0, out ->
    for line in out.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) do
      match Int32.TryParse(line.Trim()) with
      | true, child -> killTree child
      | _ -> ()
  | _ -> ()
  run "kill" [ "-TERM"; string pid ] scriptsDir helperTimeout |> ignore

let passedDir (sha: string) = Path.Combine(passedRoot, sha)
let isoNow () = DateTimeOffset.Now.ToString "yyyy-MM-dd'T'HH:mm:sszzz"

/// Copies the files directly inside `source` (what `cp source/* dest/` did; a subdirectory was skipped there too).
let copyFiles (source: string) (dest: string) : unit =
  Directory.CreateDirectory dest |> ignore
  for file in Directory.EnumerateFiles source do
    File.Copy(file, Path.Combine(dest, Path.GetFileName file), true)

// ── --promote ────────────────────────────────────────────────────────────────

let promote (commit: string) (into: string) : int =
  let sha = mustGit repo [ "-C"; repo; "rev-parse"; commit ]
  let dir = passedDir sha
  if not (File.Exists(Path.Combine(dir, "ok"))) then fail (NoPassToPromote sha)
  copyFiles (Path.Combine(dir, "release")) into
  match Environment.GetEnvironmentVariable "GITHUB_STEP_SUMMARY", Path.Combine(dir, "trust-report.md") with
  | summary, report when not (String.IsNullOrEmpty summary) && File.Exists report ->
    File.AppendAllText(summary, File.ReadAllText report)
  | _ -> ()
  say (sprintf "promoted the gated bundle for %s" sha)
  for file in Directory.EnumerateFiles into |> Seq.sort do
    printfn "%10d %s" (FileInfo(file).Length) (Path.GetFileName file)
  0

// ── one gate at a time, and the newest one wins ──────────────────────────────

/// The lock, or None while another gate holds it. FileShare.None is an advisory flock on Linux, the same lock
/// `flock(1)` takes, and it is opened close-on-exec, so nothing the pipeline spawns can ever carry it (MSBuild
/// reuse nodes outlive their build and used to hold it long after the gate had finished).
let tryLock () : FileStream option =
  try Some (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
  with :? IOException -> None

let rec waitForLock () : FileStream =
  match tryLock () with
  | Some held -> held
  | None ->
    Thread.Sleep lockPollEvery
    waitForLock ()

/// Who holds the gate right now: the pid and commit its `current` file names.
let runningGate () : (int * string) option =
  try
    match (File.ReadAllText currentPath).Trim().Split(' ') with
    | [| pid; sha |] -> (match Int32.TryParse pid with | true, p -> Some (p, sha) | _ -> None)
    | _ -> None
  with _ -> None

/// A manual gate and the runner's fallback must never share the checkout, the build output or the test ports.
///
/// Superseding rather than queueing, because an older gate is validating a commit we are no longer shipping:
/// its verdict is already worthless, and waiting for it just pays for that worthlessness in wall clock, twice,
/// since the machine is saturated the whole time. The one exception is a gate already running THIS commit,
/// which is doing your work for you.
type Turn =
  | MyTurn of FileStream
  | PassedWhileWaiting of FileStream

let takeTurn (sha: string) (force: bool) : Turn =
  let short = ReleaseRules.shortSha sha
  match tryLock () with
  | Some held -> MyTurn held
  | None ->
    match runningGate () with
    | Some (_, runningSha) when runningSha = sha ->
      say (sprintf "%s is already being gated; waiting for that run" short)
      let held = waitForLock ()
      match not force && File.Exists(Path.Combine(passedDir sha, "ok")) with
      | true ->
        say (sprintf "%s passed while waiting" short)
        PassedWhileWaiting held
      | false -> MyTurn held
    | running ->
      let runningSha = running |> Option.map snd |> Option.defaultValue ""
      say (sprintf "superseding the gate for %s - this run gates %s" (if runningSha = "" then "" else ReleaseRules.shortSha runningSha) short)
      match running with
      | Some (pid, _) when isAlive pid ->
        killTree pid
        // TERM first so a tier can flush its log, then insist.
        let rec waitDead attempts =
          match attempts > 0 && isAlive pid with
          | true ->
            Thread.Sleep supersedePollEvery
            waitDead (attempts - 1)
          | false -> ()
        waitDead supersedeAttempts
        (try Process.GetProcessById(pid).Kill() with _ -> ())
      | _ -> ()
      MyTurn (waitForLock ())

// ── the build lease ──────────────────────────────────────────────────────────

let http = new HttpClient(Timeout = leaseRequestTimeout)

let post (path: string) (json: string) : string =
  try
    use content = new StringContent(json, Encoding.UTF8, "application/json")
    use reply = http.PostAsync(sprintf "http://localhost:%d%s" mcpPort path, content).GetAwaiter().GetResult()
    reply.Content.ReadAsStringAsync().GetAwaiter().GetResult()
  with _ -> ""

let mutable heldLeaseId = ""

/// Take a lease from a running daemon, so AGENTS back off while the gate runs. The lock above only stops a
/// second gate; it does nothing about a sub-agent starting a `dotnet build` on top of this one, which is what
/// actually happened three times in one day. Every tier then reports `errored` rather than `failed` (starved,
/// not wrong) and a 350s tier stretches past 1700s, so the verdict is garbage and the recorded durations that
/// feed future timeouts are poisoned too.
///
/// Deliberately fail-open and bounded. A release must never be blocked by a daemon that is down, wedged, or
/// simply busy: if the lease is not granted within the attempts above, say so loudly and gate anyway.
/// Coordination that can deadlock a release is worse than no coordination.
let takeLease () : unit =
  let holder = sprintf "local-gate-%d" Environment.ProcessId
  let rec ask attempt =
    match attempt > leaseAttempts with
    | true -> ""
    | false ->
      match ReleaseRules.parseLeaseReply (post "/api/lease/request" (sprintf """{"holder":"%s","kind":"full_build"}""" holder)) with
      | ReleaseRules.LeaseGranted id ->
        say "holding the build lease; agents will queue behind this run"
        id
      | ReleaseRules.LeaseWait ->
        say "waiting on an agent's build lease..."
        Thread.Sleep leaseRetryPause
        ask (attempt + 1)
      | ReleaseRules.LeaseOther -> "" // refused, unreachable, or no daemon: not ours to solve here
  heldLeaseId <- ask 1
  if heldLeaseId = "" then say "no lease (daemon down or busy) - gating anyway, expect noise if an agent builds"

let releaseLease () : unit =
  match heldLeaseId with
  | "" -> ()
  | id ->
    heldLeaseId <- ""
    post "/api/lease/release" (sprintf """{"leaseId":"%s"}""" id) |> ignore

// ── the gate ─────────────────────────────────────────────────────────────────

/// A persistent clean checkout per source repo, on real disk (a Release build is several GB and the integration
/// suites write temp dirs; a tmpfs has filled and hung this before). Persistent means bin/obj, node_modules,
/// the MCP SDK clone and the Harmony nupkg stay warm between runs. `git clean` without -x keeps ignored build
/// output while removing every untracked source file.
let prepareCheckout (sha: string) : string =
  let wt = Path.Combine(gateHome, ReleaseRules.checkoutName repo)
  // Say who this checkout is for, then reap what nobody needs. One checkout per invoking repo path (an agent's
  // worktree is a repo path of its own) and a release bundle per passed commit piled up to 172 GB with nothing
  // ever removing any of it. The record names the repo and the pid, so a checkout whose repo is gone is reaped
  // by the next gate; the reap itself is F# (scripts/gate-reap.fsx, SageFs.Core/GateReaper.fs), the same rules
  // the daemon's hygiene plan uses. It never touches the checkout this run is about to use, and a failure here
  // never blocks the gate.
  Directory.CreateDirectory ownersRoot |> ignore
  File.WriteAllText(Path.Combine(ownersRoot, Path.GetFileName wt), ReleaseRules.ownerRecord repo Environment.ProcessId (isoNow ()))
  let _, reaped =
    run "dotnet" [ "fsi"; Path.Combine(scriptsDir, "gate-reap.fsx"); gateHome; repo; string Environment.ProcessId ] repo (TimeSpan.FromMinutes 5.)
  for line in reaped.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) do
    say line
  run "git" [ "-C"; repo; "worktree"; "prune" ] repo helperTimeout |> ignore
  if not (Directory.Exists(Path.Combine(wt, ".git")) || File.Exists(Path.Combine(wt, ".git"))) then
    mustGit repo [ "-C"; repo; "worktree"; "add"; "--detach"; wt; sha; "-q" ] |> ignore
  mustGit wt [ "checkout"; "--detach"; "-q"; sha ] |> ignore
  // Failures here were never fatal: a reset or clean that cannot finish leaves the checkout dirty, and the
  // pipeline's own stages say so.
  run "git" [ "reset"; "--hard"; "-q" ] wt gitTimeout |> ignore
  run "git" [ "clean"; "-fdq" ] wt gitTimeout |> ignore
  wt

/// Runs the pipeline in the checkout with its output merged into one stream: all of it to the log, and the
/// lines worth watching (stage boundaries, one line per finished tier, failures) to the console as they happen.
/// Returns the pipeline's exit code.
let runPipeline (wt: string) (sha: string) (log: string) : int =
  // Data and temp state live in the gate's own home, never the user's real ~/.SageFs.
  for dir in [ dataDir; tmpDir ] do
    (try Directory.Delete(dir, true) with :? DirectoryNotFoundException -> ())
    Directory.CreateDirectory dir |> ignore
  let psi = ProcessStartInfo("dotnet")
  psi.WorkingDirectory <- wt
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  pipelineArgs |> List.iter psi.ArgumentList.Add
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
  psi.Environment["TMPDIR"] <- tmpDir
  psi.Environment["SOURCE_SHA"] <- sha
  // MSBuild reuse nodes outlive the build that spawned them (about 15 minutes idle).
  psi.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
  // Recorded tier durations persist across gates, so longest-first ordering learns from every run; per-suite
  // seconds balance the sharded host tier (TierPlan.assign).
  psi.Environment["SAGEFS_TIER_HISTORY"] <- tierHistoryPath
  psi.Environment["SAGEFS_SUITE_HISTORY"] <- suiteHistoryPath
  use writer = new StreamWriter(log, false, UTF8Encoding(false), AutoFlush = true)
  let gate = obj ()
  let onLine (raw: string) =
    match isNull raw with
    | true -> ()
    | false ->
      lock gate (fun () ->
        writer.WriteLine raw
        if ReleaseRules.isProgressLine raw then Console.Out.WriteLine(ReleaseRules.stripAnsi raw))
  use p = new Process(StartInfo = psi)
  p.OutputDataReceived.Add(fun e -> onLine e.Data)
  p.ErrorDataReceived.Add(fun e -> onLine e.Data)
  p.Start() |> ignore
  p.BeginOutputReadLine()
  p.BeginErrorReadLine()
  p.WaitForExit()
  p.ExitCode

let gate (sha: string) (force: bool) : int =
  let short = ReleaseRules.shortSha sha
  let skip = not force && File.Exists(Path.Combine(passedDir sha, "ok"))
  match skip with
  | true ->
    say (sprintf "%s already passed (%s)" short ((File.ReadAllText(Path.Combine(passedDir sha, "ok"))).Trim()))
    0
  | false ->
    match takeTurn sha force with
    | PassedWhileWaiting held ->
      held.Dispose()
      0
    | MyTurn held ->
      use _held = held
      // Own the lock: publish who we are so the next gate can supersede us the same way. Written after the
      // lock, never before, or a crashed starter would leave a pid that a newcomer would try to kill.
      File.WriteAllText(currentPath, sprintf "%d %s\n" Environment.ProcessId sha)
      takeLease ()
      try
        let wt = prepareCheckout sha
        let log = Path.Combine(logsRoot, short + ".log")
        printfn "=== local gate: %s ===" (mustGit wt [ "log"; "--oneline"; "-1" ])
        printfn "    log: %s" log
        let clock = Stopwatch.StartNew()
        let code = runPipeline wt sha log
        let elapsed = ReleaseRules.formatElapsed clock.Elapsed
        printfn ""
        match code with
        | 0 ->
          let dest = passedDir sha
          (try Directory.Delete(dest, true) with :? DirectoryNotFoundException -> ())
          copyFiles (Path.Combine(wt, "release")) (Path.Combine(dest, "release"))
          for (name, source) in [ "trust-ledger.jsonl", Path.Combine(wt, "test-results", "trust-ledger.jsonl"); "trust-report.md", Path.Combine(wt, "test-results", "trust-report.md") ] do
            if File.Exists source then File.Copy(source, Path.Combine(dest, name), true)
          File.WriteAllText(Path.Combine(dest, "ok"), ReleaseRules.passRecord (isoNow ()) clock.Elapsed + "\n")
          printfn "=== local gate PASSED for %s in %s ===" short elapsed
          0
        | failed ->
          printfn "=== local gate FAILED for %s after %s (exit %d) ===" short elapsed failed
          File.ReadAllLines log
          |> Array.filter ReleaseRules.isFailureSummaryLine
          |> Array.map ReleaseRules.stripAnsi
          |> Array.rev |> Array.truncate failureTailLines |> Array.rev
          |> Array.iter (printfn "%s")
          fail (PipelineFailed failed)
      finally
        releaseLease ()

// ── entry ────────────────────────────────────────────────────────────────────

let argv = fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

/// On TERM, Ctrl-C or hangup: give the lease back and take the pipeline's tree down with us, then leave. The
/// pool would otherwise leak a seat until the lease expires on its own, and a daemon the pipeline started would
/// keep holding the test ports.
let onSignal (code: int) (context: PosixSignalContext) : unit =
  context.Cancel <- true
  releaseLease ()
  match run "pgrep" [ "-P"; string Environment.ProcessId ] scriptsDir helperTimeout with
  | 0, out ->
    for line in out.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) do
      match Int32.TryParse(line.Trim()) with
      | true, child -> killTree child
      | _ -> ()
  | _ -> ()
  exit code

let code =
  try
    use _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, onSignal 143)
    use _int = PosixSignalRegistration.Create(PosixSignal.SIGINT, onSignal 130)
    use _hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, onSignal 129)
    Directory.CreateDirectory passedRoot |> ignore
    Directory.CreateDirectory logsRoot |> ignore
    match ReleaseRules.parseGateArgs argv with
    | ReleaseRules.Usage m -> fail (Usage m)
    | ReleaseRules.Promote (commit, into) -> promote commit into
    | ReleaseRules.Gate (commit, force) -> gate (mustGit repo [ "-C"; repo; "rev-parse"; commit + "^{commit}" ]) force
  with
  | Stop (PipelineFailed failed) -> failed
  | Stop f ->
    eprintfn "local-gate: %s" (describe f)
    exitCodeOf f

exit code
