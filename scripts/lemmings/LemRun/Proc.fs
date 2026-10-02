/// Starting, waiting for and ending processes. Everything the harness runs goes through here, so
/// there is one place that says how output is captured, how a wait is bounded and how a process
/// is ended: by its exact pid, TERM first, KILL only if it will not go. Nothing is ever ended by name.
module LemRun.Proc

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks

[<DllImport("libc", EntryPoint = "kill")>]
extern int private sysKill(int pid, int signal)

/// The two signals the harness sends.
module Signal =
  let term = 15
  let kill = 9

/// What to run. `Env` is set on top of the inherited environment, `EnvRemove` is taken out of it,
/// and `ClearEnv` starts from nothing (the sandbox does its own `--clearenv`, so most callers leave it off).
type Spec =
  { File: string
    Args: string list
    Cwd: string option
    Env: (string * string) list
    EnvRemove: string list }

let spec (file: string) (args: string list) : Spec =
  { File = file; Args = args; Cwd = None; Env = []; EnvRemove = [] }

let inDir (cwd: string) (s: Spec) : Spec = { s with Cwd = Some cwd }
let withEnv (name: string) (value: string) (s: Spec) : Spec = { s with Env = s.Env @ [ name, value ] }

let private startInfo (s: Spec) (redirectOut: bool) (redirectErr: bool) (redirectIn: bool) : ProcessStartInfo =
  let psi = ProcessStartInfo(s.File)
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- redirectOut
  psi.RedirectStandardError <- redirectErr
  psi.RedirectStandardInput <- redirectIn
  s.Args |> List.iter psi.ArgumentList.Add
  s.Cwd |> Option.iter (fun d -> psi.WorkingDirectory <- d)
  s.EnvRemove |> List.iter (psi.Environment.Remove >> ignore)
  s.Env |> List.iter (fun (k, v) -> psi.Environment[k] <- v)
  psi

/// The result of a process run to completion with its output captured.
type Captured =
  { ExitCode: int
    Stdout: string
    Stderr: string
    TimedOut: bool }

/// Exit code of `timeout(1)` when its limit is reached, which this mimics for a bounded wait.
let timedOutExit = 124

let sendSignal (pid: int) (signal: int) : unit = sysKill (pid, signal) |> ignore

/// Ends exactly one process: TERM, wait up to `grace`, then KILL.
type Ended =
  | Gone
  | Graceful
  | Forced

module Ended =
  let toString (e: Ended) : string =
    match e with
    | Gone -> "gone"
    | Graceful -> "graceful"
    | Forced -> "forced"

let private pollEvery = TimeSpan.FromMilliseconds 250.0

let isAlive (p: Process) : bool =
  try not p.HasExited with _ -> false

/// Waits until `condition` holds or the time is up; true when it held.
let waitUntil (timeout: TimeSpan) (condition: unit -> bool) : bool =
  let deadline = DateTime.UtcNow + timeout
  let mutable ok = condition ()
  while not ok && DateTime.UtcNow < deadline do
    Thread.Sleep pollEvery
    ok <- condition ()
  ok

let terminate (p: Process) (grace: TimeSpan) : Ended =
  match isAlive p with
  | false -> Gone
  | true ->
    sendSignal p.Id Signal.term
    match waitUntil grace (fun () -> not (isAlive p)) with
    | true -> Graceful
    | false ->
      sendSignal p.Id Signal.kill
      p.WaitForExit(int (TimeSpan.FromSeconds 5.0).TotalMilliseconds) |> ignore
      Forced

/// Runs to completion, capturing stdout and stderr. With a limit, a process still running when it is
/// reached is killed (it was started by this call, so it is this call's to end).
let run (s: Spec) (limit: TimeSpan option) : Captured =
  try
    use p = Process.Start(startInfo s true true false)
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    let finished =
      match limit with
      | Some l -> p.WaitForExit(int l.TotalMilliseconds)
      | None -> p.WaitForExit(); true
    match finished with
    | true ->
      p.WaitForExit()
      { ExitCode = p.ExitCode; Stdout = out.Result; Stderr = err.Result; TimedOut = false }
    | false ->
      (try p.Kill true with _ -> ())
      { ExitCode = timedOutExit; Stdout = ""; Stderr = ""; TimedOut = true }
  with ex ->
    { ExitCode = 127; Stdout = ""; Stderr = sprintf "could not start %s: %s" s.File ex.Message; TimedOut = false }

/// `run` with the arguments only, no limit, and stdout and stderr joined: for the many small tools
/// (git, ss, cp) whose output is read as one block.
let runCombined (file: string) (args: string list) : int * string =
  let c = run (spec file args) None
  c.ExitCode, c.Stdout + c.Stderr

/// A process that is still running, with the tasks copying its output to files.
type Running =
  { Process: Process
    Pumps: Task list }

let private pump (reader: StreamReader) (target: string) : Task =
  task {
    use file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read)
    do! reader.BaseStream.CopyToAsync file
  }

/// Starts a process whose stdout and stderr go to files. Stdin is the harness's own.
let start (s: Spec) (stdoutFile: string) (stderrFile: string) : Running =
  let p = Process.Start(startInfo s true true false)
  { Process = p; Pumps = [ pump p.StandardOutput stdoutFile; pump p.StandardError stderrFile ] }

/// Starts a process with no redirection at all: it inherits stdin, stdout and stderr.
let startInheriting (s: Spec) : Process = Process.Start(startInfo s false false false)

/// Waits for the process, then for its output files to be complete, and returns the exit code.
let finish (r: Running) : int =
  r.Process.WaitForExit()
  Task.WaitAll(r.Pumps |> List.toArray)
  r.Process.ExitCode

/// Runs to completion with output going to files, and returns the exit code.
let runToFiles (s: Spec) (stdoutFile: string) (stderrFile: string) : int = start s stdoutFile stderrFile |> finish

/// Runs to completion with the harness's own stdio.
let runInheriting (s: Spec) : int =
  use p = startInheriting s
  p.WaitForExit()
  p.ExitCode

/// `command -v`: the full path of an executable on PATH, if there is one.
let which (name: string) : string option =
  Environment.GetEnvironmentVariable "PATH"
  |> Option.ofObj
  |> Option.defaultValue ""
  |> fun path -> path.Split(':', StringSplitOptions.RemoveEmptyEntries)
  |> Array.tryPick (fun dir ->
    let candidate = Path.Combine(dir, name)
    match File.Exists candidate && (File.GetUnixFileMode candidate &&& (UnixFileMode.UserExecute ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherExecute)) <> UnixFileMode.None with
    | true -> Some candidate
    | false -> None)

/// The target of every symlink on the way, which is exactly what `readlink -f` says.
let realPath (path: string) : string =
  let c = run (spec "readlink" [ "-f"; path ]) None
  match c.ExitCode, c.Stdout.Trim() with
  | 0, resolved when resolved <> "" -> resolved
  | _ -> Path.GetFullPath path

/// Copies the lines of a process's stdout and stderr, interleaved, into one file (and, with `echo`,
/// to the harness's own stdout), until both streams end.
let private mergeLines (p: Process) (file: string) (echo: bool) : Task list =
  let sink = new StreamWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
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
  [ out; err; Task.WhenAll([ out; err ]).ContinueWith(fun (_: Task) -> sink.Dispose()) ]

/// Starts a process with stdout and stderr merged into one log file, as a shell's `> file 2>&1` does.
let startMerged (s: Spec) (logFile: string) : Running =
  let p = Process.Start(startInfo s true true false)
  { Process = p; Pumps = mergeLines p logFile false }

/// Starts a process with stdout and stderr merged into one file AND shown, as `2>&1 | tee file` does.
let startTee (s: Spec) (logFile: string) : Running =
  let p = Process.Start(startInfo s true true false)
  { Process = p; Pumps = mergeLines p logFile true }

/// Runs `handler` when the harness is interrupted (Ctrl-C) or terminated (SIGTERM), then ends the process
/// with 130. Without it a .NET process dies on the signal without running its `finally` blocks, and what it
/// started (an editor, a driver, a window) would be left running. SIGKILL cannot be caught; the sandboxes
/// die with their parent (`--die-with-parent`) either way. Dispose the result to stop listening.
let onInterrupt (handler: unit -> unit) : IDisposable =
  let fired = ref 0
  let handle () =
    // Once: a second signal while the first is cleaning up does not run the cleanup twice.
    if Interlocked.Exchange(fired, 1) = 0 then
      (try handler () with _ -> ())
      exit 130
  let cancel = ConsoleCancelEventHandler(fun _ e -> e.Cancel <- true; handle ())
  Console.CancelKeyPress.AddHandler cancel
  let term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, fun ctx -> ctx.Cancel <- true; handle ())
  { new IDisposable with
      member _.Dispose() =
        Console.CancelKeyPress.RemoveHandler cancel
        term.Dispose() }
