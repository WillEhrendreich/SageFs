/// What the client side of the FSI host does when the host process goes away on its own.
///
/// Found live on 0.6.865: a thread in user code threw, the host aborted (exit 134), and nothing noticed.
/// The session kept saying Ready until an eval hit the dead connection. These tests drive a FsiHostSession
/// against a host that is only a socket, an exit signal and a text tail, so the order of the exit, the
/// socket close and a purposeful dispose is chosen by the test, not left to the scheduler.
module SageFs.Tests.FsiHostLostTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.FsiHost.FsiProtocol
open SageFs.FsiHostClient
open SageFs.FsiSession
open SageFs.Tests

/// What the host does when it is asked to shut down: the exit event and the socket close can arrive either way round.
type private ShutdownOrder =
  | ExitThenClose
  | CloseThenExit

type private FakeHost =
  { Session: FsiHostSession
    Output: HostOutputTail
    Exit: TaskCompletionSource<int>
    /// The host's end of the protocol socket.
    Wire: TcpClient
    /// Completes once the host has read a request line.
    RequestSeen: Task }

let private startFake (onShutdown: ShutdownOrder) : FakeHost =
  let listener = new TcpListener(IPAddress.Loopback, 0)
  listener.Start()
  let port = (listener.LocalEndpoint :?> IPEndPoint).Port
  let client = new TcpClient()
  client.Connect(IPAddress.Loopback, port)
  let wire = listener.AcceptTcpClient()
  listener.Stop()
  let exit = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
  let output = HostOutputTail()
  let requestSeen = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
  let hostReader = new StreamReader(wire.GetStream(), UTF8Encoding false)
  let serve () =
    try
      let mutable go = true
      while go do
        match hostReader.ReadLine() with
        | null -> go <- false
        | line ->
          requestSeen.TrySetResult() |> ignore
          match decodeRequest line with
          | Result.Ok Shutdown ->
            output.Finish()
            match onShutdown with
            | ExitThenClose ->
              exit.TrySetResult 0 |> ignore
              wire.Close()
            | CloseThenExit ->
              wire.Close()
              exit.TrySetResult 0 |> ignore
            go <- false
          | _ -> ()
    with _ -> ()
  Task.Run serve |> ignore
  let proc : HostProcess = { Id = 4242; Exit = exit.Task; Kill = fun () -> exit.TrySetResult -9 |> ignore }
  let reader = new StreamReader(client.GetStream(), UTF8Encoding false)
  let session = new FsiHostSession(proc, client, reader, ".NET fake", "0.0", "", (fun _ _ -> ()), ignore, output)
  { Session = session; Output = output; Exit = exit; Wire = wire; RequestSeen = requestSeen.Task }

/// How the host dies: the process exit can be reported before the socket closes, or after.
type private DeathOrder =
  | ExitFirst
  | CloseFirst

let private crashText =
  [ "Unhandled exception. System.OverflowException: Arithmetic operation resulted in an overflow."
    "   at FSI_0004.spike@65.Invoke()"
    "   at System.Threading.Thread.StartHelper.Callback(Object state)" ]

let private die (host: FakeHost) (code: int) (lines: string list) (order: DeathOrder) : unit =
  for line in lines do
    host.Output.Add line
  host.Output.Finish()
  match order with
  | ExitFirst ->
    host.Exit.TrySetResult code |> ignore
    (try host.Wire.Close() with _ -> ())
  | CloseFirst ->
    (try host.Wire.Close() with _ -> ())
    host.Exit.TrySetResult code |> ignore

let private within (bound: TimeSpan) (pending: Task<'a>) : Task<'a> =
  task {
    let! first = Task.WhenAny(pending :> Task, Task.Delay bound)
    match obj.ReferenceEquals(first, pending) with
    | true -> return! pending
    | false -> return failtestf "did not complete within %A" bound
  }

/// Waits for a signal that carries no value.
let private withinSignal (bound: TimeSpan) (signal: Task) : Task<unit> =
  task {
    let! first = Task.WhenAny(signal, Task.Delay bound)
    match obj.ReferenceEquals(first, signal) with
    | true -> return ()
    | false -> return failtestf "did not complete within %A" bound
  }

let private startedStub : SageFs.HostAgent.AgentStarted = { LoadedProjects = []; AssemblyLoadErrors = [] }

let private crashedWith (code: int) (ended: HostEnd) : HostCrash =
  match ended with
  | Crashed crash ->
    crash.Exit |> Expect.equal "the exit code the host reported" (ExitedWith code)
    crash
  | Retired -> failtest "expected the host to have crashed, but it was retired"

[<Tests>]
let tests =
  testList "FsiHostSession: a host that goes away on its own" [

    for order in [ ExitFirst; CloseFirst ] do
      testTask (sprintf "a host that dies while the session is idle ends as Crashed with its exit code and its last output (%A)" order) {
        let host = startFake ExitThenClose
        die host 134 crashText order
        let! ended = within TestTimeouts.patienceBrief host.Session.Ended
        let crash = crashedWith 134 ended
        crash.Output |> Expect.stringContains "the unhandled exception text" "System.OverflowException"
        crash.Output |> Expect.stringContains "the frame it came from" "FSI_0004.spike@65"
      }

    for shutdown in [ ExitThenClose; CloseThenExit ] do
      testTask (sprintf "disposing the session is a purposeful end, never a crash, whichever of the exit and the close the client sees first (%A)" shutdown) {
        let host = startFake shutdown
        do! Task.Run(fun () -> (host.Session :> IDisposable).Dispose())
        let! ended = within TestTimeouts.patienceBrief host.Session.Ended
        ended |> Expect.equal "a requested stop is Retired" Retired
      }

    testTask "a crash that happens first stays the answer when the session is disposed afterwards" {
      let host = startFake ExitThenClose
      die host 134 crashText ExitFirst
      let! first = within TestTimeouts.patienceBrief host.Session.Ended
      do! Task.Run(fun () -> (host.Session :> IDisposable).Dispose())
      let! second = within TestTimeouts.patienceBrief host.Session.Ended
      second |> Expect.equal "Ended completes once, with the crash" first
      crashedWith 134 second |> ignore
    }

    testTask "an eval running when the host dies is answered with the same crash that Ended reports" {
      let host = startFake ExitThenClose
      let running = host.Session.Eval("while true do ()", CancellationToken.None) |> Async.StartAsTask
      do! withinSignal TestTimeouts.patienceBrief host.RequestSeen
      die host 134 crashText ExitFirst
      let! call = within TestTimeouts.patienceBrief running
      let! ended = within TestTimeouts.patienceBrief host.Session.Ended
      let fromEnded = crashedWith 134 ended
      match call with
      | HostCrashed fromEval -> fromEval |> Expect.equal "the eval and the proactive path name the same crash" fromEnded
      | other -> failtestf "expected the eval to report the crash, got %A" other
      match! host.Session.Eval("1;;", CancellationToken.None) with
      | HostCrashed again -> again |> Expect.equal "a later eval reports it again, unchanged" fromEnded
      | other -> failtestf "expected a later eval to report the crash, got %A" other
    }

    testCase "the output kept with a crash is bounded, and it is the END of what the host said" <| fun _ ->
      let lines = [ for i in 1 .. 5000 -> sprintf "line %d %s" i (String('x', 200)) ]
      let crash = HostCrash.ofTail (ExitedWith 134) (List.toArray lines)
      Expect.isLessThanOrEqual "the text stays within the bound" (crash.Output.Length, HostCrash.maxOutputChars)
      crash.Output |> Expect.stringContains "the last line is kept" "line 5000"
      Expect.isFalse "the first line is not" (crash.Output.Contains "line 1 ")

    testTask "RemoteFsiSession reports a host that crashed under an eval as the typed FsiHostCrashed error" {
      let host = startFake ExitThenClose
      let remote = new RemoteFsiSession.RemoteFsiSession(host.Session, startedStub)
      let running = Task.Run(fun () -> (remote :> IFsiSession).Eval("while true do ()", CancellationToken.None))
      do! withinSignal TestTimeouts.patienceBrief host.RequestSeen
      die host 134 crashText ExitFirst
      let! result = within TestTimeouts.patienceBrief running
      let! ended = within TestTimeouts.patienceBrief host.Session.Ended
      match result.Outcome with
      | FsiFailed(:? SageFsErrorException as raised) ->
        raised.Error |> Expect.equal "the structured case reaches the worker boundary" (SageFsError.FsiHostCrashed(crashedWith 134 ended))
      | other -> failtestf "expected FsiFailed carrying SageFsErrorException, got %A" other
    }

    testTask "RemoteFsiSession exposes the host's end so the worker can watch it" {
      let host = startFake ExitThenClose
      let remote = new RemoteFsiSession.RemoteFsiSession(host.Session, startedStub)
      match (remote :> IFsiSession).HostLifetime with
      | SeparateHost ended -> Expect.isTrue "it is the session's own Ended task" (obj.ReferenceEquals(ended, host.Session.Ended))
      | SharesTheWorkerProcess -> failtest "an isolated host is a separate process"
      die host 0 [] ExitFirst
      let! _ = within TestTimeouts.patienceBrief host.Session.Ended
      ()
    }
  ]
