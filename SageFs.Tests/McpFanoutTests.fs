module SageFs.Tests.McpFanoutTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.Server.McpTools
open SageFs.Tests.TestInfrastructure

/// The 2026-11-07 daemon held 25 GB because a connected client that stopped reading made every notification stack
/// up in the SDK's write buffer, and a tool call failed with OutOfMemoryException because the daemon's own echo
/// (the friction record) threw. These pin both repairs.

/// Run `body` with the daemon's warning log captured, and restore it after. The sink is a process-wide
/// mutable, so every test that does this sits in a sequenced list.
let private withCapturedWarnings (body: unit -> Task<'a>) : Task<'a * string list> =
  task {
    let lines = ConcurrentQueue<string>()
    let previous = SageFs.Utils.Log.logWarn
    SageFs.Utils.Log.logWarn <- fun line -> lines.Enqueue line
    try
      let! result = body ()
      return result, List.ofSeq lines
    finally
      SageFs.Utils.Log.logWarn <- previous
  }

let private failingFrictionStore (makeException: unit -> exn) =
  { tempFrictionStore () with AppendEvent = fun _ -> raise (makeException ()) }

let private ctxWithFailingStore (makeException: unit -> exn) : McpContext =
  { sharedCtx () with FrictionStore = Some(failingFrictionStore makeException) }

let private target (id: string) (send: CancellationToken -> Task) (released: ConcurrentQueue<string>) : FanoutTarget =
  { Id = id
    Bytes = 300L
    Send = send
    Release = fun () -> released.Enqueue id; Task.CompletedTask }

/// The product's policy, and a fresh ledger for each broadcast a test makes by itself.
let private policy : FanoutPolicy = FanoutPolicy.standard Timeouts.mcpNotificationSend
let private broadcast (targets: FanoutTarget[]) : Task<string[]> =
  McpFanout.broadcast policy (FanoutLedger()) Timeouts.mcpConnectionRelease targets

[<Tests>]
let tests =
  testSequenced <| testList "MCP echo and notification fan-out are best effort" [

    testList "a tool call's echo never fails the call" [

      testTask "WHY — a friction store that throws OutOfMemoryException still lets withEcho return the tool's own text" {
        EchoGuard.reset ()
        let ctx = ctxWithFailingStore (fun () -> OutOfMemoryException())
        let! text = withEcho ctx "echo_probe" (Task.FromResult "the tool answered")
        text |> Expect.equal "the call's result is the tool's text" "the tool answered"
        EchoGuard.failureCount EchoSink.Friction |> Expect.equal "the failed friction record is counted" 1L
      }

      testTask "withEchoOutcome and withEchoNoAwaitRecord and withEchoOutcomeNoAwaitRecord answer the same way" {
        EchoGuard.reset ()
        let ctx = ctxWithFailingStore (fun () -> OutOfMemoryException())
        let! a = withEchoNoAwaitRecord ctx "echo_probe" (Task.FromResult "b")
        let! b = withEchoOutcome ctx "echo_probe" (Task.FromResult("c", None))
        let! c = withEchoOutcomeNoAwaitRecord ctx "echo_probe" (Task.FromResult("d", None))
        [ a; b; c ] |> Expect.equal "each wrapper returns its tool's text" [ "b"; "c"; "d" ]
        EchoGuard.failureCount EchoSink.Friction |> Expect.equal "each failed record is counted" 3L
      }

      testTask "a tool that fails still fails with its own exception, not the echo's" {
        EchoGuard.reset ()
        let ctx = ctxWithFailingStore (fun () -> OutOfMemoryException())
        let! (outcome: exn option) =
          task {
            try
              let! _ = withEcho ctx "echo_probe" (Task.FromException<string>(InvalidOperationException "tool broke"))
              return (None: exn option)
            with ex -> return Some ex
          }
        match outcome with
        | Some ex when (ex :? InvalidOperationException) -> ex.Message |> Expect.equal "the tool's own error reaches the caller" "tool broke"
        | other -> failtestf "expected the tool's InvalidOperationException, got %A" other
      }

      testTask "the failure is written to the log once, however many calls hit it" {
        EchoGuard.reset ()
        let ctx = ctxWithFailingStore (fun () -> OutOfMemoryException())
        let! (_, (warnings: string list)) =
          withCapturedWarnings (fun () ->
            task {
              for _ in 1 .. 25 do
                let! _ = withEcho ctx "echo_probe" (Task.FromResult "ok")
                ()
              return ()
            })
        EchoGuard.failureCount EchoSink.Friction |> Expect.equal "all 25 are counted" 25L
        warnings
        |> List.filter (fun line -> line.Contains "friction")
        |> List.length
        |> Expect.equal "one log line for 25 failures" 1
      }

      testCase "attemptSync swallows whatever a decoration throws" <| fun _ ->
        EchoGuard.reset ()
        EchoGuard.attemptSync EchoSink.Span (fun () -> raise (OutOfMemoryException()))
        EchoGuard.failureCount EchoSink.Span |> Expect.equal "counted" 1L
    ]

    testList "a connection that stops reading is released, not buffered" [

      testTask "a send that fails is released once, and the others still get their notification" {
        let released = ConcurrentQueue<string>()
        let delivered = ConcurrentQueue<string>()
        let healthy name = target name (fun _ -> delivered.Enqueue name; Task.CompletedTask) released
        let stuck = target "stuck" (fun _ -> Task.FromException(OperationCanceledException())) released
        let! (dropped: string[]) = broadcast [| healthy "a"; stuck; healthy "b" |]
        dropped |> Expect.equal "only the failed connection is dropped" [| "stuck" |]
        List.ofSeq released |> Expect.equal "and released exactly once" [ "stuck" ]
        List.ofSeq delivered |> List.sort |> Expect.equal "the healthy ones were notified" [ "a"; "b" ]
      }

      testTask "WHY — a send that never completes is judged by the bytes it leaves unfinished: under the ceiling it is left alone, past it the connection is released and its send cancelled" {
        let released = ConcurrentQueue<string>()
        let cancelled = TaskCompletionSource<bool>()
        let ledger = FanoutLedger()
        let big = { target "hangs" (fun token -> token.Register(fun () -> cancelled.TrySetResult true |> ignore) |> ignore; Task.Delay(Timeout.Infinite, token)) released with Bytes = FanoutPolicy.StandardMaxBufferedBytes / 2L + 1L }
        let lookAndMove = { policy with Look = TimeSpan.Zero }
        let! first = McpFanout.broadcast lookAndMove ledger Timeouts.mcpConnectionRelease [| big |]
        first |> Expect.isEmpty "one unfinished send is under the ceiling, so the connection stays"
        ledger.Unfinished "hangs" |> Expect.equal "and its bytes are held against it" big.Bytes
        let! second = McpFanout.broadcast lookAndMove ledger Timeouts.mcpConnectionRelease [| big |]
        second |> Expect.equal "the second pushes it past the ceiling, so it is dropped" [| "hangs" |]
        List.ofSeq released |> Expect.equal "and released once" [ "hangs" ]
        let! sawCancel = cancelled.Task.WaitAsync TestTimeouts.patienceInProcess
        sawCancel |> Expect.isTrue "the stuck write was cancelled, so the lock it holds is free before the connection is disposed"
        ledger.Unfinished "hangs" |> Expect.equal "and the ledger forgot it" 0L
      }

      testTask "WHY — a send slower than the look is not a fault: the connection stays, and its bytes leave the ledger when the send finishes" {
        let released = ConcurrentQueue<string>()
        let finish = TaskCompletionSource()
        let ledger = FanoutLedger()
        let slow = target "slow" (fun _ -> finish.Task) released
        let! dropped = McpFanout.broadcast { policy with Look = TimeSpan.Zero } ledger Timeouts.mcpConnectionRelease [| slow |]
        dropped |> Expect.isEmpty "not dropped for being slow"
        ledger.Unfinished "slow" |> Expect.equal "still unfinished" slow.Bytes
        finish.SetResult()
        ledger.Unfinished "slow" |> Expect.equal "finished, so nothing is held against it" 0L
        released.IsEmpty |> Expect.isTrue "and it was never released"
      }

      testTask "WHY — a slow send that fails later drops the connection at the next broadcast, without sending to it again" {
        let released = ConcurrentQueue<string>()
        let finish = TaskCompletionSource()
        let sends = ref 0
        let ledger = FanoutLedger()
        let late = target "late" (fun _ -> Interlocked.Increment sends |> ignore; finish.Task) released
        let! first = McpFanout.broadcast { policy with Look = TimeSpan.Zero } ledger Timeouts.mcpConnectionRelease [| late |]
        first |> Expect.isEmpty "slow, not failed, so it stays for now"
        finish.SetException(OperationCanceledException())
        ledger.HasFaulted "late" |> Expect.isTrue "the late failure is noted"
        let! second = McpFanout.broadcast policy ledger Timeouts.mcpConnectionRelease [| late |]
        second |> Expect.equal "the next broadcast drops it" [| "late" |]
        sends.Value |> Expect.equal "without a second send" 1
        List.ofSeq released |> Expect.equal "and releases it once" [ "late" ]
      }

      testCase "WHY — the backlog decision is the ceiling and nothing else: at it the connection stays, past it the connection goes" <| fun _ ->
        FanoutBacklog.decide policy 0L |> Expect.isNone "nothing unfinished"
        FanoutBacklog.decide policy policy.MaxBufferedBytes |> Expect.isNone "exactly at the ceiling"
        FanoutBacklog.decide policy (policy.MaxBufferedBytes + 1L)
        |> Expect.equal "one byte past it" (Some(DropReason.BacklogTooLarge(policy.MaxBufferedBytes + 1L)))

      testTask "an OutOfMemoryException from a send or from a release never escapes the broadcast" {
        let released = ConcurrentQueue<string>()
        let oom = { target "oom" (fun _ -> Task.FromException(OutOfMemoryException())) released with Release = fun () -> Task.FromException(OutOfMemoryException()) }
        let! dropped = broadcast [| oom |]
        dropped |> Expect.equal "dropped, with no exception" [| "oom" |]
      }

      testTask "the failure is counted per kind, not per connection, so a thousand dead connections hold no thousand counters" {
        EchoGuard.reset ()
        let released = ConcurrentQueue<string>()
        let many = [| for i in 1 .. 1000 -> target (string i) (fun _ -> Task.FromException(OperationCanceledException())) released |]
        let! (dropped: string[]) = broadcast many
        dropped.Length |> Expect.equal "all dropped" 1000
        (EchoGuard.snapshot () |> List.length <= 2) |> Expect.isTrue "one row per sink, not one per connection"
        EchoGuard.failureCount EchoSink.NotificationDelivery |> Expect.equal "every drop counted" 1000L
      }
    ]
  ]
