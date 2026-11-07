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
    Send = send
    Release = fun () -> released.Enqueue id; Task.CompletedTask }

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
        let! (dropped: string[]) = McpFanout.broadcast Timeouts.mcpNotificationSend Timeouts.mcpConnectionRelease [| healthy "a"; stuck; healthy "b" |]
        dropped |> Expect.equal "only the failed connection is dropped" [| "stuck" |]
        List.ofSeq released |> Expect.equal "and released exactly once" [ "stuck" ]
        List.ofSeq delivered |> List.sort |> Expect.equal "the healthy ones were notified" [ "a"; "b" ]
      }

      testTask "a send that never completes is cut off at the budget and released" {
        let released = ConcurrentQueue<string>()
        let hangs = target "hangs" (fun token -> Task.Delay(Timeout.Infinite, token)) released
        let! dropped = McpFanout.broadcast Timeouts.mcpNotificationSend Timeouts.mcpConnectionRelease [| hangs |]
        dropped |> Expect.equal "the hung connection is dropped" [| "hangs" |]
        List.ofSeq released |> Expect.equal "and released" [ "hangs" ]
      }

      testTask "an OutOfMemoryException from a send or from a release never escapes the broadcast" {
        let released = ConcurrentQueue<string>()
        let oom = { target "oom" (fun _ -> Task.FromException(OutOfMemoryException())) released with Release = fun () -> Task.FromException(OutOfMemoryException()) }
        let! dropped = McpFanout.broadcast Timeouts.mcpNotificationSend Timeouts.mcpConnectionRelease [| oom |]
        dropped |> Expect.equal "dropped, with no exception" [| "oom" |]
      }

      testTask "the failure is counted per kind, not per connection, so a thousand dead connections hold no thousand counters" {
        EchoGuard.reset ()
        let released = ConcurrentQueue<string>()
        let many = [| for i in 1 .. 1000 -> target (string i) (fun _ -> Task.FromException(OperationCanceledException())) released |]
        let! (dropped: string[]) = McpFanout.broadcast Timeouts.mcpNotificationSend Timeouts.mcpConnectionRelease many
        dropped.Length |> Expect.equal "all dropped" 1000
        (EchoGuard.snapshot () |> List.length <= 2) |> Expect.isTrue "one row per sink, not one per connection"
        EchoGuard.failureCount EchoSink.NotificationDelivery |> Expect.equal "every drop counted" 1000L
      }
    ]
  ]
