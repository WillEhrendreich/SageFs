/// The stdio bridge's run loop, driven by a fake daemon edge: the REAL `McpStdioBridge.run` (mailbox, action
/// batches, push-stream handling) over an `Io` whose every effect the test controls.
///
/// The roast of 2026-11-08 (finding 3): when the daemon came back after an upgrade the bridge was `Ready` on
/// the id the new daemon never issued while its re-handshake was still on the wire, so a stdin line arriving in
/// that gap went out under the stale id, got a 404, and the heal minted a second session: two captures, two push
/// streams, the loser never cancelled. The pure core and its simulation settle the decisions; these cases pin
/// that the run loop carries them out (one stream at a time, the old one cancelled, nothing forwarded while the
/// session is being minted, a failure answered instead of held).
module SageFs.Tests.McpStdioBridgeRunTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpBridge
open SageFs.Server.McpStdioBridge

/// A stdout that says when something was written, so a test waits for the line it expects instead of sleeping.
type SignalingStream() =
  inherit MemoryStream()
  let written = new SemaphoreSlim(0)

  override _.Write(buffer: byte[], offset: int, count: int) =
    base.Write(buffer, offset, count)
    written.Release() |> ignore

  override _.Write(buffer: ReadOnlySpan<byte>) =
    base.Write(buffer)
    written.Release() |> ignore

  member _.Written = written

  member this.Lines: string[] =
    Encoding.UTF8.GetString(this.ToArray()).Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)

let awaitLine (stream: SignalingStream) (found: string -> bool) : Task<string option> =
  task {
    let deadline = DateTime.UtcNow + TestTimeouts.patience
    let mutable result = None
    let mutable looking = true
    while looking do
      match stream.Lines |> Array.tryFind found with
      | Some line ->
        result <- Some line
        looking <- false
      | None ->
        let remaining = deadline - DateTime.UtcNow
        match remaining > TimeSpan.Zero with
        | false -> looking <- false
        | true ->
          let! signalled = stream.Written.WaitAsync remaining
          looking <- signalled
    return result
  }

let idKey (msg: RpcMessage) : string =
  match msg with
  | RpcMessage.Request(RpcId.N n, _, _) -> string n
  | RpcMessage.Request(RpcId.S s, _, _) -> s
  | RpcMessage.Notification(name, _) -> name
  | RpcMessage.Response _
  | RpcMessage.Unparseable _ -> "?"

let request (id: int) (methodName: string) : RpcMessage =
  parseRpcMessage (sprintf """{"jsonrpc":"2.0","id":%d,"method":"%s","params":{}}""" id methodName)

/// Waits for a signal named by a key, however early or late the test asks.
type Signals() =
  let signals = ConcurrentDictionary<string, TaskCompletionSource<unit>>()
  let signalFor (key: string) =
    signals.GetOrAdd(key, fun _ -> TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))
  member _.Raise(key: string) = (signalFor key).TrySetResult() |> ignore
  member _.Await(key: string) : Task<bool> =
    task {
      let! finished = Task.WhenAny([| (signalFor key).Task :> Task; Task.Delay TestTimeouts.patience |])
      return obj.ReferenceEquals(finished, (signalFor key).Task)
    }
  member _.IsRaised(key: string) = (signalFor key).Task.IsCompleted

/// The daemon edge: every effect is recorded, and the ones a test needs to hold or fail are in its hands.
type FakeDaemon() =
  let forwards = ConcurrentQueue<string option * RpcMessage>()
  let streams = ConcurrentQueue<string * CancellationToken>()
  let probes = ConcurrentQueue<unit>()
  let signals = Signals()
  let stdin = TaskCompletionSource<Event -> unit>(TaskCreationOptions.RunContinuationsAsynchronously)
  let rehandshakeRelease =
    TaskCompletionSource<Result<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously)

  member _.Forwards = forwards.ToArray()
  member _.Streams = streams.ToArray()
  member _.ProbeCount = probes.Count
  member _.Signals = signals
  member _.Stdin: Task<Event -> unit> = stdin.Task

  /// Whether a probe finds the daemon.
  member val Up = true with get, set

  /// What the daemon answers a forwarded message with (a session id is captured for an `initialize` regardless).
  member val OnForward: RpcMessage -> Result<unit, ForwardError> = (fun _ -> Ok()) with get, set

  /// Let the held re-handshake finish: with the id it minted, or with a failure.
  member _.FinishRehandshake(outcome: Result<string, string>) = rehandshakeRelease.TrySetResult outcome |> ignore

  member this.Io: Io =
    { Probe =
        fun () ->
          task {
            probes.Enqueue()
            return this.Up
          }
      StartDaemon = fun () -> task { return Ok() }
      Forward =
        fun capture sessionId msg ->
          task {
            forwards.Enqueue(sessionId, msg)
            match msg with
            | RpcMessage.Request(_, "initialize", _) -> capture "s0"
            | _ -> ()
            let outcome = this.OnForward msg
            signals.Raise("forwarded " + idKey msg)
            return outcome
          }
      OpenServerStream =
        fun sessionId token ->
          task {
            streams.Enqueue(sessionId, token)
            signals.Raise("stream " + sessionId)
            try
              do! Task.Delay(Timeout.Infinite, token)
              return Ok()
            with :? OperationCanceledException ->
              return Error "the stream was cancelled"
          }
      Rehandshake =
        fun _ capture ->
          task {
            signals.Raise "rehandshake started"
            match! rehandshakeRelease.Task with
            | Ok sessionId ->
              capture sessionId
              return Ok()
            | Error reason -> return Error reason
          }
      TerminateSession = fun _ -> task { return () }
      PumpStdin =
        fun post ->
          task {
            stdin.TrySetResult post |> ignore
            do! Task.Delay Timeout.Infinite
          }
      ProbeInterval = Timeouts.stdioBridgeProbeInterval }

/// Start the real run loop over the fake, shake hands as a client would, and wait until the first push stream is open.
let connected (fake: FakeDaemon) : Task<SignalingStream * Task<int> * (Event -> unit)> =
  task {
    let stdout = new SignalingStream()
    let exit = run defaultPolicy fake.Io (StdoutWriter stdout)
    let! post = fake.Stdin
    post (Event.StdinLine(request 1 "initialize"))
    let! initialized = fake.Signals.Await "forwarded 1"
    initialized |> Expect.isTrue "the initialize reached the daemon"
    let! streamOpened = fake.Signals.Await "stream s0"
    streamOpened |> Expect.isTrue "the push stream opened on the first session"
    return stdout, exit, post
  }

let finish (exit: Task<int>) (post: Event -> unit) : Task<unit> =
  task {
    post Event.StdinClosed
    let! finished = Task.WhenAny([| exit :> Task; Task.Delay TestTimeouts.patience |])
    obj.ReferenceEquals(finished, exit) |> Expect.isTrue "the bridge ended when stdin closed"
    exit.Result |> Expect.equal "a clean exit" 0
  }

let listChangedLines (stdout: SignalingStream) : string[] =
  stdout.Lines |> Array.filter (fun line -> line.Contains "notifications/tools/list_changed")

[<Tests>]
let tests =
  testList "McpStdioBridge run loop (P17)" [

    testTask "WHY — a line that arrives while the new session is being minted waits for it: it goes out under the NEW id, once, and exactly one push stream replaces the old one" {
      let fake = FakeDaemon()
      let! stdout, exit, post = connected fake
      // The upgrade: the first daemon's push stream ends and a new daemon already answers probes.
      post (Event.ServerStreamClosed("s0", "server stream closed"))
      let! started = fake.Signals.Await "rehandshake started"
      started |> Expect.isTrue "the bridge began the re-handshake"
      // The gap: the client sends while the re-handshake is still on the wire.
      post (Event.StdinLine(request 2 "tools/list"))
      fake.FinishRehandshake(Ok "s1")
      let! forwarded = fake.Signals.Await "forwarded 2"
      forwarded |> Expect.isTrue "the line was forwarded once the session existed"
      let! streamed = fake.Signals.Await "stream s1"
      streamed |> Expect.isTrue "the push stream opened on the new session"
      fake.Forwards
      |> Array.filter (fun (_, msg) -> idKey msg = "2")
      |> Array.map fst
      |> Expect.equal "the line went out exactly once, under the new id and never the stale one" [| Some "s1" |]
      fake.Streams |> Array.map fst |> Expect.equal "one stream per session, no more" [| "s0"; "s1" |]
      let oldToken = fake.Streams |> Array.find (fun (sid, _) -> sid = "s0") |> snd
      oldToken.IsCancellationRequested |> Expect.isTrue "the old session's push stream was cancelled, so notifications arrive once"
      listChangedLines stdout |> Array.length |> Expect.equal "the client is told once that its lists may have changed" 1
      do! finish exit post
    }

    testTask "a session captured by a heal replaces the held one: the old push stream is cancelled and the client is told its lists may have changed" {
      let fake = FakeDaemon()
      let! stdout, exit, post = connected fake
      post (Event.SessionIdCaptured "s9")
      let! streamed = fake.Signals.Await "stream s9"
      streamed |> Expect.isTrue "the push stream opened on the healed session"
      let oldToken = fake.Streams |> Array.find (fun (sid, _) -> sid = "s0") |> snd
      oldToken.IsCancellationRequested |> Expect.isTrue "the replaced session's stream is cancelled"
      let! notice = awaitLine stdout (fun line -> line.Contains "notifications/tools/list_changed")
      notice |> Expect.isSome "the list-changed notice was written"
      do! finish exit post
    }

    testTask "WHY — a re-handshake that fails answers the line that was waiting with the reason, instead of holding it for good or sending it under an id the daemon does not know" {
      let fake = FakeDaemon()
      let! stdout, exit, post = connected fake
      post (Event.ServerStreamClosed("s0", "server stream closed"))
      let! started = fake.Signals.Await "rehandshake started"
      started |> Expect.isTrue "the bridge began the re-handshake"
      post (Event.StdinLine(request 2 "tools/list"))
      // The daemon went away again while the session was being minted; the bridge keeps waiting for it.
      fake.Up <- false
      fake.FinishRehandshake(Error "the daemon is down")
      let! answer = awaitLine stdout (fun line -> line.Contains "\"id\":2" && line.Contains "\"error\"")
      match answer with
      | None -> failtestf "the waiting request was never answered; stdout: %A" stdout.Lines
      | Some line -> line |> Expect.stringContains "the answer says what failed" "the daemon is down"
      fake.Forwards
      |> Array.exists (fun (_, msg) -> idKey msg = "2")
      |> Expect.isFalse "the line was never sent under the stale id"
      do! finish exit post
    }

    testTask "a request that times out is answered with an error and nothing else: no probe, no re-handshake, the session stays" {
      let fake = FakeDaemon()
      let! stdout, exit, post = connected fake
      let probesBefore = fake.ProbeCount
      fake.OnForward <-
        fun msg ->
          match idKey msg with
          | "3" -> Error(ForwardError.TimedOut "no answer within the client deadline")
          | _ -> Ok()
      post (Event.StdinLine(request 3 "tools/call"))
      let! answer = awaitLine stdout (fun line -> line.Contains "\"id\":3" && line.Contains "\"error\"")
      answer |> Expect.isSome "the client got an error for the request that timed out"
      post (Event.StdinLine(request 4 "tools/list"))
      let! forwarded = fake.Signals.Await "forwarded 4"
      forwarded |> Expect.isTrue "the next request was forwarded"
      fake.Forwards
      |> Array.filter (fun (_, msg) -> idKey msg = "4")
      |> Array.map fst
      |> Expect.equal "under the same session, which a slow answer did not end" [| Some "s0" |]
      fake.ProbeCount |> Expect.equal "the daemon was not probed: a slow answer is not an outage" probesBefore
      fake.Signals.IsRaised "rehandshake started" |> Expect.isFalse "no re-handshake was started for a live session"
      do! finish exit post
    }
  ]
