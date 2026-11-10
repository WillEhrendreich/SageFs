namespace SageFs.Features

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open SageFs

/// What one recording is of: the process to record, what to measure, and where the recorder writes. A CPU recording is written at
/// `TraceBase` and converted to Speedscope beside it (`DotnetTrace.speedscopePathOf TraceBase`); an allocation recording is a
/// nettrace file at `DotnetTrace.nettracePathOf TraceBase`.
type RecordingRequest = { HostPid: int; Mode: ProfileMode; TraceBase: string }

/// What a finished recording gives, in the form its mode reads.
type RecordedTrace =
  /// The text of the Speedscope file of a CPU recording.
  | Speedscope of text: string
  /// The bytes of the nettrace file of an allocation recording.
  | NetTrace of bytes: byte[]

/// A recording that has started. `Finish` ends it and answers with the trace, or why there is none. It removes every file the
/// recording wrote, whichever way it ends.
type ActiveRecording = { Finish: unit -> Async<Result<RecordedTrace, ProfileFailure>> }

/// The port the profile tool records through. The real one drives an installed `dotnet-trace`
/// (`DotnetTraceCollector.create`); a simulation supplies one that fails on a schedule.
type TraceCollector = { Begin: RecordingRequest -> Async<Result<ActiveRecording, ProfileFailure>> }

/// An eval that ran under a recording: how it ended, and the trace the recording gave.
type Profiled<'a> = { Eval: Guarded.Attempt<'a>; Trace: Result<RecordedTrace, ProfileFailure> }

module ProfileRecorder =
  /// Record while `run` runs. A recording that cannot start is a refusal and `run` never runs, so code is not executed under
  /// a profile that was never going to be made. Once it has started, `run` runs exactly once and the recording is finished
  /// whether `run` returned or threw.
  let around (collector: TraceCollector) (request: RecordingRequest) (run: unit -> Async<'a>) : Async<Result<Profiled<'a>, ProfileFailure>> =
    async {
      match! collector.Begin request with
      | Result.Error failure -> return Result.Error failure
      | Result.Ok active ->
        let! ran = Async.Catch(run ())
        let! trace = active.Finish()
        let eval =
          match ran with
          | Choice1Of2 value -> Guarded.Attempt.Completed value
          | Choice2Of2 ex -> Guarded.Attempt.Threw ex
        return Result.Ok { Eval = eval; Trace = trace }
    }

/// The `dotnet-trace` command line, and the small decisions about its output that need no process.
module DotnetTrace =
  let toolName = "dotnet-trace"
  let installCommand = "dotnet tool install -g dotnet-trace"

  /// The sample profiler at its default rate and the runtime's JIT and loader events (keywords 0x10 and 0x8), which is all a CPU
  /// profile reads. The default provider set of `dotnet-trace` stretched one real run past eleven minutes in FsiMcp's author's
  /// hands, so nothing else is asked for.
  let providers = "Microsoft-DotNETCore-SampleProfiler:0:5,Microsoft-Windows-DotNETRuntime:0x18:4"

  /// The runtime's GC events at verbose level, which carry the allocation ticks (about one for every 100 KB allocated, with the
  /// stack of the allocating thread), and its JIT and loader events, which name the methods and modules the stacks hold. Measured
  /// 2026-11-10: this set gave 1,319 ticks for 160 MB of allocation, and 64,008,288 bytes sampled for a type the program had
  /// allocated 64,000,000 bytes of.
  let allocationProviders = "Microsoft-Windows-DotNETRuntime:0x19:5"

  let providersFor (mode: ProfileMode) : string =
    match mode with
    | ProfileMode.Cpu -> providers
    | ProfileMode.Alloc -> allocationProviders

  let speedscopePathOf (traceBase: string) : string = traceBase + ".speedscope.json"

  /// Where an allocation recording writes its nettrace file.
  let nettracePathOf (traceBase: string) : string = traceBase + ".nettrace"

  /// A CPU recording is converted to Speedscope by the tool. An allocation recording is kept as the nettrace file it writes.
  let arguments (request: RecordingRequest) : string list =
    let common = [ "collect"; "-p"; string request.HostPid; "--providers"; providersFor request.Mode; "--format" ]
    match request.Mode with
    | ProfileMode.Cpu -> common @ [ "Speedscope"; "-o"; request.TraceBase ]
    | ProfileMode.Alloc -> common @ [ "NetTrace"; "-o"; nettracePathOf request.TraceBase ]

  /// Every file a recording of this request may have written.
  let filesOf (request: RecordingRequest) : string list =
    [ request.TraceBase; speedscopePathOf request.TraceBase; nettracePathOf request.TraceBase ]

  /// The line the tool prints once it has opened its output, which is when its session on the host is running.
  let isAttachedLine (line: string) : bool =
    not (isNull line) && line.TrimStart().StartsWith("Output File", StringComparison.Ordinal)

  type ToolSearch = { IsWindows: bool; PathVariable: string; Home: string }

  /// Where to look for the tool: each directory of PATH, then the dotnet global tools directory.
  let candidates (search: ToolSearch) : string list =
    let file =
      match search.IsWindows with
      | true -> toolName + ".exe"
      | false -> toolName
    let pathVariable =
      match isNull search.PathVariable with
      | true -> ""
      | false -> search.PathVariable
    let onPath =
      pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
      |> Array.map (fun dir -> Path.Combine(dir, file))
      |> List.ofArray
    let globalTools =
      match String.IsNullOrEmpty search.Home with
      | true -> []
      | false -> [ Path.Combine(search.Home, ".dotnet", "tools", file) ]
    onPath @ globalTools

  /// The last few lines the tool printed, bounded, for a refusal that has to say what the tool said.
  let tailOf (lines: string list) : string =
    let joined = lines |> List.rev |> List.truncate 6 |> List.rev |> String.concat " | "
    match joined.Length > 400 with
    | true -> joined.Substring(0, 400) + "..."
    | false -> joined

/// The real collector: an installed `dotnet-trace` run as a child of the daemon, attached to the host's process id. The host
/// loads nothing for it. The recording ends with SIGTERM, which `dotnet-trace` answers by stopping the session, writing the
/// trace and converting it (measured 2026-11-10: SIGINT is ignored when the daemon itself was started with it ignored, as a
/// shell's `&` does, and a newline on stdin does nothing when stdin is a pipe). Windows has no such signal, so it is refused.
module DotnetTraceCollector =
  let locate () : string option =
    let search : DotnetTrace.ToolSearch =
      { IsWindows = OperatingSystem.IsWindows()
        PathVariable = Environment.GetEnvironmentVariable "PATH"
        Home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile }
    DotnetTrace.candidates search |> List.tryFind File.Exists

  /// Remove what a recording wrote. A file that is not there is already gone.
  let removeTrace (request: RecordingRequest) : unit =
    DotnetTrace.filesOf request
    |> List.iter (fun path -> Guarded.call ignore (fun () -> File.Delete path))

  /// Ask a process to stop, the way a shell's `kill -TERM` does.
  let terminate (pid: int) : unit =
    Guarded.call ignore (fun () ->
      let psi = ProcessStartInfo("kill", UseShellExecute = false, CreateNoWindow = true)
      psi.ArgumentList.Add "-TERM"
      psi.ArgumentList.Add(string pid)
      use asked = Process.Start psi
      asked.WaitForExit())

  /// Read the file a finished recording wrote, in the form its mode reads, within the size that mode's reader accepts.
  let readTrace (request: RecordingRequest) : Result<RecordedTrace, ProfileFailure> =
    let path, limit, what =
      match request.Mode with
      | ProfileMode.Cpu -> DotnetTrace.speedscopePathOf request.TraceBase, SpeedscopeTrace.maxCharacters, "Speedscope"
      | ProfileMode.Alloc -> DotnetTrace.nettracePathOf request.TraceBase, NetTrace.maxBytes, "nettrace"
    match File.Exists path with
    | false -> Result.Error(ProfileFailure.TraceUnreadable(sprintf "the recorder wrote no %s file" what))
    | true ->
      let size = FileInfo(path).Length
      match size > limit with
      | true -> Result.Error(ProfileFailure.TraceTooLarge(size, limit))
      | false ->
        match request.Mode, Guarded.attempt (fun () -> File.ReadAllBytes path) with
        | _, Guarded.Attempt.Threw ex -> Result.Error(ProfileFailure.TraceUnreadable ex.Message)
        | ProfileMode.Cpu, Guarded.Attempt.Completed bytes -> Result.Ok(RecordedTrace.Speedscope(Text.Encoding.UTF8.GetString bytes))
        | ProfileMode.Alloc, Guarded.Attempt.Completed bytes -> Result.Ok(RecordedTrace.NetTrace bytes)

  /// What a running `dotnet-trace` has told us so far.
  type Running(proc: Process) =
    let lines = ConcurrentQueue<string>()
    let attached = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
    let exited = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    member _.Process = proc
    member _.Attached: Task<bool> = attached.Task
    member _.Exited: Task<int> = exited.Task
    member _.Lines: string list = lines |> List.ofSeq
    member _.Hear(line: string) =
      match isNull line with
      | true -> ()
      | false ->
        lines.Enqueue line
        match DotnetTrace.isAttachedLine line with
        | true -> attached.TrySetResult true |> ignore
        | false -> ()
    member _.Ended(code: int) = exited.TrySetResult code |> ignore

  let exitCodeOf (proc: Process) : int =
    match Guarded.attempt (fun () -> proc.ExitCode) with
    | Guarded.Attempt.Completed code -> code
    | Guarded.Attempt.Threw _ -> -1

  let start (path: string) (request: RecordingRequest) : Result<Running, ProfileFailure> =
    let begin' () =
      let psi = ProcessStartInfo(path, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true)
      DotnetTrace.arguments request |> List.iter psi.ArgumentList.Add
      let proc = new Process(StartInfo = psi, EnableRaisingEvents = true)
      let running = Running(proc)
      proc.OutputDataReceived.Add(fun e -> running.Hear e.Data)
      proc.ErrorDataReceived.Add(fun e -> running.Hear e.Data)
      proc.Exited.Add(fun _ -> running.Ended(exitCodeOf proc))
      proc.Start() |> ignore
      proc.BeginOutputReadLine()
      proc.BeginErrorReadLine()
      running
    match Guarded.attempt begin' with
    | Guarded.Attempt.Completed running -> Result.Ok running
    | Guarded.Attempt.Threw ex -> Result.Error(ProfileFailure.CouldNotAttach ex.Message)

  let waitFor (task: Task) (limit: TimeSpan) : Async<bool> =
    async {
      let! first = Task.WhenAny(task, Task.Delay limit) |> Async.AwaitTask
      return obj.ReferenceEquals(first, task)
    }

  /// Stop the recorder and read what it wrote. A recorder that already ended before it was asked to stop lost its target.
  let hasExited (proc: Process) : bool =
    match Guarded.attempt (fun () -> proc.HasExited) with
    | Guarded.Attempt.Completed exited -> exited
    | Guarded.Attempt.Threw _ -> true

  let finish (running: Running) (request: RecordingRequest) : Async<Result<RecordedTrace, ProfileFailure>> =
    async {
      let proc = running.Process
      match hasExited proc with
      | true ->
        removeTrace request
        return Result.Error(ProfileFailure.HostGone(sprintf "the recorder ended on its own, which it does when the process it records ends. It said: %s" (DotnetTrace.tailOf running.Lines)))
      | false ->
        terminate proc.Id
        let! stopped = waitFor running.Exited Timeouts.profileRecorderWait
        match stopped with
        | false ->
          Guarded.call ignore (fun () -> proc.Kill true)
          removeTrace request
          return Result.Error(ProfileFailure.StopTimedOut Timeouts.profileRecorderWait)
        | true ->
          let result = readTrace request
          removeTrace request
          return
            match result with
            | Result.Ok text -> Result.Ok text
            | Result.Error(ProfileFailure.TraceUnreadable detail) ->
              Result.Error(ProfileFailure.TraceUnreadable(sprintf "%s. The recorder said: %s" detail (DotnetTrace.tailOf running.Lines)))
            | Result.Error other -> Result.Error other
    }

  /// Start the recorder at `path` on `request.HostPid`, and answer once it says its output is open.
  let beginAt (path: string) (request: RecordingRequest) : Async<Result<ActiveRecording, ProfileFailure>> =
    async {
      match start path request with
      | Result.Error failure -> return Result.Error failure
      | Result.Ok running ->
        let! attached = waitFor (Task.WhenAny(running.Attached :> Task, running.Exited :> Task)) Timeouts.profileRecorderWait
        match attached, running.Attached.IsCompleted with
        | true, true -> return Result.Ok { Finish = fun () -> finish running request }
        | _ ->
          Guarded.call ignore (fun () -> running.Process.Kill true)
          removeTrace request
          return Result.Error(ProfileFailure.CouldNotAttach(sprintf "the recorder did not report an open session. It said: %s" (DotnetTrace.tailOf running.Lines)))
    }

  /// Start a recording of `request.HostPid` with the installed tool.
  let beginRecording (request: RecordingRequest) : Async<Result<ActiveRecording, ProfileFailure>> =
    async {
      match OperatingSystem.IsWindows(), locate () with
      | true, _ -> return Result.Error(ProfileFailure.NotRecordable "stopping the recorder needs a POSIX signal, which this build sends on Linux and macOS only")
      | false, None -> return Result.Error(ProfileFailure.ToolMissing(DotnetTrace.toolName, DotnetTrace.installCommand))
      | false, Some path -> return! beginAt path request
    }

  let create () : TraceCollector = { Begin = beginRecording }
