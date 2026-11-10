/// What the client side of the FSI host does when the host is alive and answers nothing.
///
/// Found by hand on 2026-11-09: with `dotnet-debug attach <pid>` on the host (or after the debugger was killed), a release sent to
/// the daemon never answered, because `AgentDebugContinue` waited on the host with no bound, and the session read Ready the whole
/// time. These cases drive a real FsiHostSession against a host that is only a socket and a gate: while the gate is shut the host
/// has read a request and answers nothing, as a host whose runtime is stopped does, and when it opens the host answers what it
/// had been sent, in order.
module SageFs.Tests.FsiHostSilentTests

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
open SageFs.HostAgent.TestDebug
open SageFs.Tests

/// The pid the fake host says it has.
let private hostPid = 4242

let private ticket = DebugTicket "debug-4242-1"

type private FakeHost =
  { Session: FsiHostSession
    /// Shut: the host answers nothing. Open: it answers what it has read.
    Gate: ManualResetEventSlim
    /// The tickets of the continues the host read, in order.
    Continues: ResizeArray<DebugTicket>
    Exit: TaskCompletionSource<int> }

let private startSilent (frozen: bool) : FakeHost =
  let listener = new TcpListener(IPAddress.Loopback, 0)
  listener.Start()
  let port = (listener.LocalEndpoint :?> IPEndPoint).Port
  let client = new TcpClient()
  client.Connect(IPAddress.Loopback, port)
  let wire = listener.AcceptTcpClient()
  listener.Stop()
  let exit = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
  let gate = new ManualResetEventSlim(not frozen)
  let continues = ResizeArray<DebugTicket>()
  let hostReader = new StreamReader(wire.GetStream(), UTF8Encoding false)
  let hostWriter = new StreamWriter(wire.GetStream(), UTF8Encoding false, AutoFlush = true)
  let serve () =
    try
      let mutable go = true
      while go do
        match hostReader.ReadLine() with
        | null -> go <- false
        | line ->
          // The line is read and the host then does nothing until the gate opens: a stopped runtime holds what it was sent.
          gate.WaitHandle.WaitOne() |> ignore
          match decodeRequest line with
          | Result.Ok(AgentDebugContinue(id, held, _)) ->
            lock continues (fun () -> continues.Add held)
            hostWriter.WriteLine(encodeResponse (AgentDebugContinueResult(id, DebugProgress.StillRunning)))
          | Result.Ok(AgentDebugBegin(id, _)) ->
            hostWriter.WriteLine(
              encodeResponse (AgentDebugBeginResult(id, DebugBegin.Unsupported(UnsupportedReason.HoldAlreadyOpen ticket)))
            )
          | Result.Ok Shutdown ->
            exit.TrySetResult 0 |> ignore
            wire.Close()
            go <- false
          | _ -> ()
    with _ -> ()
  TestInfrastructure.DedicatedThread.run "silent-fsi-host" serve |> ignore
  let proc : HostProcess = { Id = hostPid; Exit = exit.Task; Kill = fun () -> exit.TrySetResult -9 |> ignore }
  let reader = new StreamReader(client.GetStream(), UTF8Encoding false)
  let session = new FsiHostSession(proc, client, reader, ".NET fake", "0.0", "", (fun _ _ -> ()), ignore, HostOutputTail())
  { Session = session; Gate = gate; Continues = continues; Exit = exit }

let private stop (host: FakeHost) =
  host.Gate.Set()
  (host.Session :> IDisposable).Dispose()

/// What the caller waits for a continue to take at the most: the host's ceiling, and then the time a test gives anything to
/// finish once that has passed.
let private deadlineFor (park: TimeSpan) : TimeSpan =
  SilentHost.Wait.ceilingFor park Timeouts.debugHostAnswerGrace + TestTimeouts.patienceBrief

let private within (bound: TimeSpan) (pending: Task<'a>) : Task<'a> =
  task {
    let! first = Task.WhenAny(pending :> Task, Task.Delay bound)
    match obj.ReferenceEquals(first, pending) with
    | true -> return! pending
    | false -> return failtestf "did not answer within %A: a call on a silent host must end at its ceiling" bound
  }

let private park = TestTimeouts.settle

let private aTest : SageFs.Features.LiveTesting.TestCase =
  { Id = SageFs.Features.LiveTesting.TestId.create "suite/adds" SageFs.Features.LiveTesting.TestFramework.Expecto
    FullName = "suite/adds"
    DisplayName = "suite/adds"
    NamePath = []
    Origin = SageFs.Features.LiveTesting.TestOrigin.ReflectionOnly
    Labels = []
    Framework = SageFs.Features.LiveTesting.TestFramework.Expecto
    Category = SageFs.Features.LiveTesting.TestCategory.Unit }

[<Tests>]
let tests =
  testList "FsiHostSession: a host that is alive and answers nothing" [

    testTask "WHY: a host that answers inside the ceiling is believed, and the pulse stays responsive" {
      let host = startSilent false
      let! answer = within (deadlineFor park) (Started.asTask (host.Session.AgentDebugContinue(ticket, park)))
      stop host
      answer |> Expect.equal "the host's own answer" (Answered DebugProgress.StillRunning)
      host.Session.Pulse |> Expect.equal "responsive" SilentHost.Pulse.Responsive
    }

    testTask "WHY: a frozen host gets a named answer at the ceiling, and the pulse says it is silent" {
      let host = startSilent true
      let ceiling = SilentHost.Wait.ceilingFor park Timeouts.debugHostAnswerGrace
      let! answer = within (deadlineFor park) (Started.asTask (host.Session.AgentDebugContinue(ticket, park)))
      match answer with
      | Answered(DebugProgress.HostSilent silence) ->
        silence.HostPid |> Expect.equal "the host's pid" hostPid
        silence.Waited |> Expect.equal "how long the host was given" ceiling
        host.Session.Pulse |> Expect.equal "silent, with the same account" (SilentHost.Pulse.Silent silence)
      | other -> failtestf "expected the host to be called silent, got %A" other
      stop host
    }

    testTask "WHY: after the host thaws the same ticket still works, and the pulse goes back to responsive" {
      let host = startSilent true
      let! first = within (deadlineFor park) (Started.asTask (host.Session.AgentDebugContinue(ticket, park)))
      let wasSilent =
        match first with
        | Answered(DebugProgress.HostSilent _) -> true
        | _ -> false
      wasSilent |> Expect.isTrue (sprintf "expected silence first, got %A" first)
      host.Gate.Set()
      let! second = within (deadlineFor park) (Started.asTask (host.Session.AgentDebugContinue(ticket, park)))
      second |> Expect.equal "the host's real answer, not another refusal" (Answered DebugProgress.StillRunning)
      host.Session.Pulse |> Expect.equal "the host spoke, so it is responsive" SilentHost.Pulse.Responsive
      lock host.Continues (fun () -> host.Continues |> Seq.toList)
      |> Expect.equal "the host read the first release too, with the same ticket" [ ticket; ticket ]
      stop host
    }

    testTask "WHY: a begin on a frozen host says the host is unavailable and why, instead of waiting" {
      let host = startSilent true
      let! answer = within (deadlineFor TimeSpan.Zero) (Started.asTask (host.Session.AgentDebugBegin aTest))
      stop host
      match answer with
      | Answered(DebugBegin.Unsupported(UnsupportedReason.HostUnavailable reason)) ->
        reason |> Expect.stringContains "names the host" (string hostPid)
        reason |> Expect.stringContains "says what to do" "ask again"
      | other -> failtestf "expected HostUnavailable, got %A" other
    }
  ]
