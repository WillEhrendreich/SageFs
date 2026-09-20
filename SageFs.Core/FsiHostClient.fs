/// SageFs's side of the isolated FSI host: spawns the host (built by FsiHostBuild), does the port
/// handshake, and multiplexes requests over the FsiProtocol socket. The host's stdout/stderr are drained
/// continuously (an undrained pipe deadlocks a chatty child before it ever prints its ready line).
///
/// A call NEVER hangs on a dead host: when the connection closes, every pending call completes as lost
/// and so does every later call.
module SageFs.FsiHostClient

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open SageFs.FsiHost.FsiProtocol

/// How one eval call ended from the caller's point of view.
type EvalCall =
  | Completed of outcome: EvalOutcome * diagnostics: FsiDiagnostic list
  | HostLost of reason: string

/// A non-eval request's answer, or the fact that the host was lost first.
type HostCall<'T> =
  | Answered of 'T
  | HostGone of reason: string

/// Why a host could not be started. Every case that has one carries the host's own last output.
type StartError =
  | CouldNotStartProcess of command: string * detail: string
  | NoPortReported of timeoutMs: int * hostOutput: string
  | ConnectFailed of detail: string * hostOutput: string
  | NotReady of timeoutMs: int * hostOutput: string
  | ClosedBeforeReady of hostOutput: string

let describeStartError (error: StartError) : string =
  let withOutput (message: string) (output: string) =
    if output.Length = 0 then message else message + "\nHost output:\n" + output
  match error with
  | CouldNotStartProcess(command, detail) -> sprintf "could not start the FSI host (%s): %s" command detail
  | NoPortReported(timeoutMs, output) -> withOutput (sprintf "the FSI host did not report a port within %d ms" timeoutMs) output
  | ConnectFailed(detail, output) -> withOutput (sprintf "could not connect to the FSI host: %s" detail) output
  | NotReady(timeoutMs, output) -> withOutput (sprintf "the FSI host did not become ready within %d ms" timeoutMs) output
  | ClosedBeforeReady output -> withOutput "the FSI host closed the connection before it was ready" output

type StartOptions =
  { HostDll: string
    Dotnet: string
    /// The complete FSI command line (starting with "fsi"), one argument per element.
    FsiArgs: string list
    WorkingDir: string
    /// Extra environment for the host process, e.g. RuntimeCompat.rollForwardEnv.
    Environment: (string * string) list
    /// Text the user's code (or FSI itself) wrote to the console.
    OnOutput: OutputStream -> string -> unit
    /// Host stdout/stderr lines that are not protocol (diagnostics for the daemon log).
    OnLog: string -> unit
    StartupTimeoutMs: int }

let private portPrefix = "FSIHOST_PORT="

/// What a pending request is completed with: the host's response, or the reason it was lost.
type private Reply =
  | Got of Response
  | Gone of reason: string

/// One running isolated FSI host and the connection to it.
[<Sealed; AllowNullLiteral>]
type FsiHostSession
  (
    proc: Process,
    client: TcpClient,
    reader: StreamReader,
    runtime: string,
    fsharpCore: string,
    argsFile: string,
    onOutput: OutputStream -> string -> unit,
    onLog: string -> unit
  ) =
  let writer = new StreamWriter(client.GetStream(), UTF8Encoding false, AutoFlush = true)
  let pending = ConcurrentDictionary<int64, TaskCompletionSource<Reply>>()
  let sendLock = obj ()
  let lost = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
  let exited = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
  let mutable nextId = 0L
  let mutable disposed = 0

  let markLost (reason: string) =
    if lost.TrySetResult reason then
      for entry in pending.ToArray() do
        entry.Value.TrySetResult(Gone reason) |> ignore

  let describeExit () =
    match proc.HasExited with
    | true -> sprintf "the FSI host exited (code %d)" proc.ExitCode
    | false -> "the connection to the FSI host closed"

  let complete (id: int64) (response: Response) =
    match pending.TryRemove id with
    | true, waiting -> waiting.TrySetResult(Got response) |> ignore
    | false, _ -> ()

  let readLoop () =
    try
      let mutable go = true
      while go do
        match reader.ReadLine() with
        | null -> go <- false
        | line ->
          match decodeResponse line with
          | Result.Error reason -> onLog (sprintf "[fsihost] unreadable response: %s" (describeError reason))
          | Result.Ok(Ready _) -> ()
          | Result.Ok(Output(stream, text)) -> (try onOutput stream text with _ -> ())
          // Every request/response pair is matched by id. Listing the cases (no wildcard) means a new
          // Response case is a compile error here until it is routed.
          | Result.Ok(EvalResult(id, _, _) as answer) -> complete id answer
          | Result.Ok(FlagResult(id, _) as answer) -> complete id answer
          | Result.Ok(ValueResult(id, _) as answer) -> complete id answer
          | Result.Ok(LiveValuesResult(id, _) as answer) -> complete id answer
          | Result.Ok(CheckResult(id, _) as answer) -> complete id answer
          | Result.Ok(SymbolsResult(id, _, _) as answer) -> complete id answer
          | Result.Ok(CompletionsResult(id, _) as answer) -> complete id answer
          | Result.Ok(DescriptionResult(id, _) as answer) -> complete id answer
          | Result.Ok(ConfigResult(id, _) as answer) -> complete id answer
          | Result.Ok(AgentStartResult(id, _) as answer) -> complete id answer
          | Result.Ok(AgentAfterEvalResult(id, _) as answer) -> complete id answer
          | Result.Ok(AgentDiscoveryResult(id, _) as answer) -> complete id answer
          | Result.Ok(AgentTestResult(id, _) as answer) -> complete id answer
          | Result.Ok(AgentRefused(id, _) as answer) -> complete id answer
    with ex ->
      onLog (sprintf "[fsihost] read loop ended: %s" ex.Message)
    // Give the process a moment to report its exit code, then fail everything still waiting.
    proc.WaitForExit 2000 |> ignore
    markLost (describeExit ())

  do
    proc.EnableRaisingEvents <- true
    proc.Exited.Add(fun _ -> exited.TrySetResult(try proc.ExitCode with _ -> -1) |> ignore)
    if proc.HasExited then exited.TrySetResult proc.ExitCode |> ignore
    Task.Run readLoop |> ignore

  let send (request: Request) : bool =
    lock sendLock (fun () ->
      try
        writer.WriteLine(encodeRequest request)
        true
      with _ -> false)

  /// Send a request carrying a fresh id and wait for its answer. Cancelling interrupts the host.
  let roundTrip (cancellationToken: CancellationToken) (make: int64 -> Request) : Async<Reply> =
    async {
      match lost.Task.IsCompleted with
      | true -> return Gone lost.Task.Result
      | false ->
        let id = Interlocked.Increment(&nextId)
        let waiting = TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously)
        pending[id] <- waiting
        match send (make id) with
        | false ->
          pending.TryRemove id |> ignore
          markLost (describeExit ())
          return Gone lost.Task.Result
        | true ->
          // The connection may have dropped between the check and the registration.
          if lost.Task.IsCompleted then waiting.TrySetResult(Gone lost.Task.Result) |> ignore
          use _ = cancellationToken.Register(fun () -> send Interrupt |> ignore)
          return! Async.AwaitTask waiting.Task
    }

  let unexpected (expected: string) (response: Response) =
    sprintf "the FSI host answered a %s request with %A" expected response

  /// The .NET runtime the host is running on, e.g. ".NET 11.0.0-rc.1.26425.128".
  member _.Runtime = runtime
  /// The FSharp.Core version the host loaded (the SDK's own).
  member _.FSharpCoreVersion = fsharpCore
  member _.ProcessId = proc.Id
  /// Completes with the host's exit code when the process ends.
  member _.Exited: Task<int> = exited.Task

  /// Evaluate a submission. Cancelling the token interrupts the running eval.
  member _.Eval(code: string, cancellationToken: CancellationToken) : Async<EvalCall> =
    async {
      match! roundTrip cancellationToken (fun id -> Eval(id, code)) with
      | Got(EvalResult(_, outcome, diagnostics)) -> return Completed(outcome, diagnostics)
      | Got other -> return HostLost(unexpected "eval" other)
      | Gone reason -> return HostLost reason
    }

  /// Read a boolean feature gate bound in the session.
  member _.ReadFlag(name: string) : Async<HostCall<FlagReading>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> ReadFlag(id, name)) with
      | Got(FlagResult(_, reading)) -> return Answered reading
      | Got other -> return HostGone(unexpected "flag" other)
      | Gone reason -> return HostGone reason
    }

  /// Read a bound name as display text.
  member _.ReadValue(name: string) : Async<HostCall<ValueReading>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> ReadValue(id, name)) with
      | Got(ValueResult(_, reading)) -> return Answered reading
      | Got other -> return HostGone(unexpected "value" other)
      | Gone reason -> return HostGone reason
    }

  /// The session's bound values as the bounded watch-window tree; the caller supplies the generation.
  member _.ReadLiveValues(generation: int64) : Async<HostCall<SageFs.Features.LiveValueTree.LiveValueSnapshot>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> ReadLiveValues(id, generation)) with
      | Got(LiveValuesResult(_, snapshot)) -> return Answered snapshot
      | Got other -> return HostGone(unexpected "live values" other)
      | Gone reason -> return HostGone reason
    }

  /// Diagnostics for a snippet checked against the session's current state.
  member _.Check(text: string) : Async<HostCall<FsiDiagnostic list>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> Check(id, text)) with
      | Got(CheckResult(_, diagnostics)) -> return Answered diagnostics
      | Got other -> return HostGone(unexpected "check" other)
      | Gone reason -> return HostGone reason
    }

  /// Diagnostics plus the symbol references of error-free code.
  member _.CheckWithSymbols(filePath: string, text: string) : Async<HostCall<FsiDiagnostic list * WireSymbolRef list>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> CheckWithSymbols(id, filePath, text)) with
      | Got(SymbolsResult(_, diagnostics, symbols)) -> return Answered(diagnostics, symbols)
      | Got other -> return HostGone(unexpected "check with symbols" other)
      | Gone reason -> return HostGone reason
    }

  /// Unsorted completion candidates at the caret, with the id to pass to `Describe`.
  member _.Complete(text: string, caret: int) : Async<HostCall<int64 * WireCompletion list>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> Complete(id, text, caret)) with
      | Got(CompletionsResult(id, items)) -> return Answered(id, items)
      | Got other -> return HostGone(unexpected "complete" other)
      | Gone reason -> return HostGone reason
    }

  /// The description of candidate `index` of the `Complete` that returned `completionsId` (empty if superseded).
  member _.Describe(completionsId: int64, index: int) : Async<HostCall<string>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> Describe(id, completionsId, index)) with
      | Got(DescriptionResult(_, text)) -> return Answered text
      | Got other -> return HostGone(unexpected "describe" other)
      | Gone reason -> return HostGone reason
    }

  /// Evaluate a config.fsx expression in the host and get the DirectoryConfig it builds (or why it does not).
  member _.EvalConfig(content: string) : Async<HostCall<ConfigOutcome>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> EvalConfig(id, content)) with
      | Got(ConfigResult(_, outcome)) -> return Answered outcome
      | Got other -> return HostGone(unexpected "config" other)
      | Gone reason -> return HostGone reason
    }

  /// Start the agent (hot reload and live testing) beside the user's code, and learn which projects it could not load.
  member _.AgentStart(init: SageFs.HostAgent.AgentInit) : Async<HostCall<SageFs.HostAgent.AgentStarted>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> AgentStart(id, init)) with
      | Got(AgentStartResult(_, started)) -> return Answered started
      | Got(AgentRefused(_, reason)) -> return HostGone reason
      | Got other -> return HostGone(unexpected "agent start" other)
      | Gone reason -> return HostGone reason
    }

  /// The agent's work after an eval: methods redefined (detoured when asked) and the tests found.
  member _.AgentAfterEval(request: SageFs.HostAgent.AfterEval) : Async<HostCall<SageFs.HostAgent.AfterEvalReport>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> AgentAfterEval(id, request)) with
      | Got(AgentAfterEvalResult(_, report)) -> return Answered report
      | Got(AgentRefused(_, reason)) -> return HostGone reason
      | Got other -> return HostGone(unexpected "agent after-eval" other)
      | Gone reason -> return HostGone reason
    }

  /// Scan what the host process has loaded for tests.
  member _.AgentDiscoverLoaded() : Async<HostCall<SageFs.HostAgent.Discovery>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> AgentDiscoverLoaded id) with
      | Got(AgentDiscoveryResult(_, discovery)) -> return Answered discovery
      | Got(AgentRefused(_, reason)) -> return HostGone reason
      | Got other -> return HostGone(unexpected "agent discovery" other)
      | Gone reason -> return HostGone reason
    }

  /// Run one test in the host, beside the session thread.
  member _.AgentRunTest(test: SageFs.Features.LiveTesting.TestCase) : Async<HostCall<SageFs.Features.LiveTesting.TestResult>> =
    async {
      match! roundTrip CancellationToken.None (fun id -> AgentRunTest(id, test)) with
      | Got(AgentTestResult(_, result)) -> return Answered result
      | Got(AgentRefused(_, reason)) -> return HostGone reason
      | Got other -> return HostGone(unexpected "agent run-test" other)
      | Gone reason -> return HostGone reason
    }

  /// Interrupt whatever is running (no effect when idle).
  member _.Interrupt() = send Interrupt |> ignore

  interface IDisposable with
    member _.Dispose() =
      if Interlocked.Exchange(&disposed, 1) = 0 then
        send Shutdown |> ignore
        (try
          if not (proc.WaitForExit 5000) then proc.Kill true
         with _ -> ())
        markLost "the FSI host session was disposed"
        (try client.Close() with _ -> ())
        (try File.Delete argsFile with _ -> ())

/// What the handshake read: the host is ready, or the connection ended first.
type private Handshake =
  | HostReady of runtime: string * fsharpCore: string
  | ConnectionEnded

/// Start a host process and complete the handshake. Errors say what happened and include the host's own output.
let start (options: StartOptions) : Async<Result<FsiHostSession, StartError>> =
  async {
    let argsFile = Path.Combine(Path.GetTempPath(), sprintf "sagefs-fsihost-%s.args" (Guid.NewGuid().ToString "N"))
    File.WriteAllLines(argsFile, options.FsiArgs)
    let deleteArgsFile () = try File.Delete argsFile with _ -> ()
    let recent = ConcurrentQueue<string>()
    let log (line: string) =
      recent.Enqueue line
      while recent.Count > 40 do
        recent.TryDequeue() |> ignore
      options.OnLog line
    let hostOutput () = recent.ToArray() |> String.concat "\n"
    let command = options.Dotnet + " " + options.HostDll
    let psi = ProcessStartInfo(options.Dotnet)
    psi.ArgumentList.Add options.HostDll
    psi.ArgumentList.Add "--args-file"
    psi.ArgumentList.Add argsFile
    psi.WorkingDirectory <- options.WorkingDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    for key, value in options.Environment do
      psi.Environment[key] <- value
    let fail (proc: Process) (error: StartError) : Result<FsiHostSession, StartError> =
      (try proc.Kill true with _ -> ())
      deleteArgsFile ()
      Error error
    // Wait for `computation`, but no longer than the startup timeout: Choice2Of2 means it timed out.
    let withinTimeout (computation: Async<'a>) : Async<Choice<'a, exn>> =
      async {
        let! child = Async.StartChild(computation, options.StartupTimeoutMs)
        return! Async.Catch child
      }
    match (try Ok(Process.Start psi) with ex -> Error ex.Message) with
    | Error detail ->
      deleteArgsFile ()
      return Error(CouldNotStartProcess(command, detail))
    | Ok proc ->
      let port = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
      let _stdoutPump =
        Task.Run(fun () ->
          let mutable line = proc.StandardOutput.ReadLine()
          while not (isNull line) do
            if line.StartsWith portPrefix then port.TrySetResult(int (line.Substring portPrefix.Length)) |> ignore
            else log line
            line <- proc.StandardOutput.ReadLine()
          port.TrySetResult -1 |> ignore)
      let _stderrPump =
        Task.Run(fun () ->
          let mutable line = proc.StandardError.ReadLine()
          while not (isNull line) do
            log line
            line <- proc.StandardError.ReadLine())
      match! withinTimeout (Async.AwaitTask port.Task) with
      | Choice2Of2 _ -> return fail proc (NoPortReported(options.StartupTimeoutMs, hostOutput ()))
      | Choice1Of2 portNumber when portNumber <= 0 -> return fail proc (ClosedBeforeReady(hostOutput ()))
      | Choice1Of2 portNumber ->
        let tcp = new TcpClient()
        match! Async.AwaitTask(tcp.ConnectAsync(IPAddress.Loopback, portNumber)) |> Async.Catch with
        | Choice2Of2 ex -> return fail proc (ConnectFailed(ex.Message, hostOutput ()))
        | Choice1Of2() ->
          let reader = new StreamReader(tcp.GetStream(), UTF8Encoding false)
          // FSI prints its own startup output before Ready: forward it and stop at Ready.
          let rec awaitReady () : Async<Handshake> =
            async {
              match! Async.AwaitTask(reader.ReadLineAsync()) with
              | null -> return ConnectionEnded
              | text ->
                match decodeResponse text with
                | Result.Ok(Ready(runtime, fsharpCore)) -> return HostReady(runtime, fsharpCore)
                | Result.Ok(Output(stream, output)) ->
                  options.OnOutput stream output
                  return! awaitReady ()
                | Result.Ok _ -> return! awaitReady ()
                | Result.Error reason ->
                  options.OnLog(sprintf "[fsihost] unreadable response: %s" (describeError reason))
                  return! awaitReady ()
            }
          match! withinTimeout (awaitReady ()) with
          | Choice2Of2 _ -> return fail proc (NotReady(options.StartupTimeoutMs, hostOutput ()))
          | Choice1Of2 ConnectionEnded -> return fail proc (ClosedBeforeReady(hostOutput ()))
          | Choice1Of2(HostReady(runtime, fsharpCore)) ->
            return
              Ok(new FsiHostSession(proc, tcp, reader, runtime, fsharpCore, argsFile, options.OnOutput, options.OnLog))
  }
