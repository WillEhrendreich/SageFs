/// A host that is alive and answers nothing.
///
/// A debugger stopped at a breakpoint, an inspector attached to the process (`dotnet-debug`, `dotnet-dump`), or a dump in
/// progress each stop the host's runtime. The process is there and its connection is open, so nothing says it died, and a
/// request sent to it is read by nobody. A wait with no bound waits for ever, and the session reads Ready the whole time.
///
/// This file is the decisions around that, all pure: how a wait ends (`settle`: the first of the answer, the ceiling, the
/// host's death), what the daemon knows about the host's voice (`pulse`), what holds a silent host (`findHolder`), what
/// to tell the person (`describe`), and how a silent host shows in a session's health (`healthOf`). The two edges are at
/// the bottom: the `/proc` look that finds a holder, and the per-session note the daemon keeps between a silent answer and
/// the host speaking again.
module SageFs.SilentHost

open System
open System.Collections.Concurrent
open System.IO
open SageFs.HostAgent.TestDebug
open SageFs.WorkerProtocol

// ---- how a wait ends ---------------------------------------------------------------------------------

/// What can end a wait on the host, in the order the shell hears them.
[<RequireQualifiedAccess>]
type WaitEvent<'answer> =
  | Answer of 'answer
  /// The ceiling passed with nothing from the host.
  | CeilingSpent
  /// The connection to the host ended.
  | HostEnded of reason: string

/// How a wait ended.
[<RequireQualifiedAccess>]
type WaitEnd<'answer> =
  | Replied of 'answer
  | NoAnswer
  | HostGone of reason: string

[<RequireQualifiedAccess>]
type Wait<'answer> =
  | Open
  | Settled of WaitEnd<'answer>

module Wait =
  /// What an event ends a wait as.
  let endOf (event: WaitEvent<'answer>) : WaitEnd<'answer> =
    match event with
    | WaitEvent.Answer answer -> WaitEnd.Replied answer
    | WaitEvent.CeilingSpent -> WaitEnd.NoAnswer
    | WaitEvent.HostEnded reason -> WaitEnd.HostGone reason

  /// The first event ends the wait and the rest change nothing: a ceiling that passes a moment after the answer came
  /// does not take the answer back, and an answer that comes after the ceiling does not undo having said so.
  let settle (wait: Wait<'answer>) (event: WaitEvent<'answer>) : Wait<'answer> =
    match wait with
    | Wait.Settled _ -> wait
    | Wait.Open -> Wait.Settled(endOf event)

  /// The longest a continue may take to answer: what the host owes (its park) and the grace for the wire.
  let ceilingFor (owed: TimeSpan) (grace: TimeSpan) : TimeSpan = owed + grace

  /// The longest the daemon waits on the worker that relays the host's answer: the host's ceiling and one more grace,
  /// so a silent host is always the worker's to say first.
  let relayCeilingFor (owed: TimeSpan) (grace: TimeSpan) : TimeSpan = ceilingFor owed grace + grace

// ---- what the daemon knows about the host's voice ----------------------------------------------------

/// Whether the host is known to be silent. Written only by what was heard: a wait that ended with no answer, and any line
/// the host sent afterwards.
[<RequireQualifiedAccess>]
type Pulse =
  | Responsive
  | Silent of HostSilence

[<RequireQualifiedAccess>]
type PulseEvent =
  | WentSilent of HostSilence
  /// Any line from the host: an answer, even to a call given up on long ago, or output.
  | HostSpoke

module Pulse =
  let step (_: Pulse) (event: PulseEvent) : Pulse =
    match event with
    | PulseEvent.WentSilent silence -> Pulse.Silent silence
    | PulseEvent.HostSpoke -> Pulse.Responsive

// ---- what holds it -----------------------------------------------------------------------------------

/// One process as the process table shows it.
type ProcessLook = { Pid: int; Args: string list }

/// Programs that attach to a running process and stop it. A closed list of names, matched on the program in a command line.
let inspectorTools : string list =
  [ "dotnet-debug"
    "dotnet-dump"
    "dotnet-gcdump"
    "dotnet-trace"
    "dotnet-counters"
    "dotnet-stack"
    "createdump"
    "netcoredbg"
    "vsdbg"
    "lldb"
    "gdb" ]

/// The inspector a command line runs: its program, or the dll `dotnet` was told to run.
let toolOf (args: string list) : string option =
  args
  |> List.truncate 2
  |> List.tryPick (fun arg ->
    let name = Path.GetFileNameWithoutExtension arg
    inspectorTools |> List.tryFind (fun tool -> tool = name))

/// Whether a command line names this pid as an argument, bare or after an `=`.
let namesPid (pid: int) (args: string list) : bool =
  let text = string pid
  args |> List.exists (fun arg -> arg = text || arg.EndsWith("=" + text, StringComparison.Ordinal))

/// What holds the host: the process the kernel says is tracing it, else the lowest-numbered known inspector that names its
/// pid, else nothing that could be found.
let findHolder (hostPid: int) (tracer: (int * string) option) (processes: ProcessLook list) : HostHolder =
  match tracer with
  | Some(pid, name) -> HostHolder.TracedBy(pid, name)
  | None ->
    processes
    |> List.filter (fun p -> p.Pid <> hostPid)
    |> List.sortBy (fun p -> p.Pid)
    |> List.tryPick (fun p ->
      match toolOf p.Args with
      | Some tool when namesPid hostPid p.Args -> Some(HostHolder.NamedAsTarget(p.Pid, tool))
      | _ -> None)
    |> Option.defaultValue HostHolder.NotFound

// ---- what to say -------------------------------------------------------------------------------------

let private seconds (span: TimeSpan) : string = sprintf "%.0f seconds" span.TotalSeconds

let private holderText (holder: HostHolder) : string =
  match holder with
  | HostHolder.TracedBy(pid, name) -> sprintf "Process %d (%s) is attached to it with ptrace." pid name
  | HostHolder.NamedAsTarget(pid, name) -> sprintf "Process %d (%s) names it as its target." pid name
  | HostHolder.NotFound ->
    "No process that names it could be found, so a debugger or inspector may hold it. A debugger that was killed can leave the host stopped."

/// What a silent host means, what holds it if that is known, and what to do next. The ticket is still valid.
let describe (silence: HostSilence) : string =
  sprintf
    "The test host (process %d) did not answer in %s. A host answers nothing while a debugger has stopped it at a breakpoint or an inspector has suspended it. %s Continue it in the debugger, or detach the debugger or quit the inspector, then ask again: the ticket is still valid. If nothing is attached and the host stays silent, hard reset the session to start a new host."
    silence.HostPid
    (seconds silence.Waited)
    (holderText silence.Holder)

// ---- the session's health ----------------------------------------------------------------------------

/// What the daemon noted about a silent host: the silence, and the worker it was seen under, so a restarted worker (a new
/// host) does not inherit it.
type Noted = { Silence: HostSilence; WorkerPid: int option }

/// A session's health once a silent host is taken into account. A session whose worker is not at rest (starting, building,
/// restarting) or is already failed says what it says. A Ready session with a silent host is Degraded, with the reason.
let withHostSilence (noted: Noted option) (status: SessionLifecycleStatus) (health: SessionHealth) : SessionHealth =
  match noted with
  | None -> health
  | Some note ->
    // A note made without knowing the worker applies to whichever worker the session has; one made under a worker applies
    // to that worker only, so a restarted worker (a new host) does not inherit it.
    let sameWorker =
      match note.WorkerPid with
      | None -> true
      | Some pid -> Some pid = SessionLifecycleStatus.workerPid status
    let reason = describe note.Silence
    match health, sameWorker with
    | SessionHealth.Starting, _
    | SessionHealth.Failed _, _
    | SessionHealth.Healthy, false
    | SessionHealth.Degraded _, false -> health
    | SessionHealth.Healthy, true -> SessionHealth.Degraded reason
    | SessionHealth.Degraded other, true -> SessionHealth.Degraded(sprintf "%s %s" reason other)

// ---- the daemon's note, per session --------------------------------------------------------------------

/// What the daemon has noted, by session id. Session ids are random, so a process that runs several daemons does not mix them.
let notes = ConcurrentDictionary<string, Noted>()

let note (session: string) (workerPid: int option) (silence: HostSilence) : unit =
  notes[session] <- { Silence = silence; WorkerPid = workerPid }

let clear (session: string) : unit = notes.TryRemove session |> ignore

let notedFor (session: string) : Noted option =
  match notes.TryGetValue session with
  | true, noted -> Some noted
  | false, _ -> None

/// The session's health with whatever the daemon noted about its host. The call sites that classify a session apply this.
let healthOf (session: string) (status: SessionLifecycleStatus) (health: SessionHealth) : SessionHealth =
  withHostSilence (notedFor session) status health

// ---- looking at the machine --------------------------------------------------------------------------

/// The `/proc` look. Linux only; elsewhere it finds nothing, which `describe` says in words.
module Probe =
  /// A file or directory that is gone, unreadable, or not ours to read (a process ended between the listing and the read, or
  /// belongs to another user) is no information, which is a normal answer here. Anything else is a bug and propagates.
  let private readOrNone (path: string) : string option =
    try
      Some(File.ReadAllText path)
    with
    | :? IOException -> None
    | :? UnauthorizedAccessException -> None

  /// The first thread of the process that something is tracing: its tracer's pid and name.
  let tracerOf (hostPid: int) : (int * string) option =
    try
      Directory.GetDirectories(sprintf "/proc/%d/task" hostPid)
      |> Array.tryPick (fun task ->
        readOrNone (Path.Combine(task, "status"))
        |> Option.bind (fun text ->
          text.Split '\n'
          |> Array.tryPick (fun line ->
            match line.StartsWith("TracerPid:", StringComparison.Ordinal) with
            | false -> None
            | true ->
              match Int32.TryParse(line.Substring("TracerPid:".Length).Trim()) with
              | true, pid when pid > 0 ->
                let name = readOrNone (sprintf "/proc/%d/comm" pid) |> Option.map (fun n -> n.Trim()) |> Option.defaultValue "unknown"
                Some(pid, name)
              | _ -> None)))
    with
    | :? IOException -> None
    | :? UnauthorizedAccessException -> None

  /// Every process whose command line is readable.
  let processes () : ProcessLook list =
    try
      Directory.GetDirectories "/proc"
      |> Array.choose (fun dir ->
        match Int32.TryParse(Path.GetFileName dir) with
        | true, pid ->
          readOrNone (Path.Combine(dir, "cmdline"))
          |> Option.map (fun text -> { Pid = pid; Args = text.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> Array.toList })
        | false, _ -> None)
      |> Array.toList
    with
    | :? IOException -> []
    | :? UnauthorizedAccessException -> []

  /// What holds the host, as far as this machine can tell.
  let holderOf (hostPid: int) : HostHolder =
    match OperatingSystem.IsLinux() with
    | true -> findHolder hostPid (tracerOf hostPid) (processes ())
    | false -> HostHolder.NotFound
