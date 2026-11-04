module SageFs.Tests.SseConnectionTrackingTests

// `serveEvents` counted a connection open, and counted it closed only when it got to the line after
// its `finally`. A write that threw during the catch-up skipped that line: the active-connections
// gauge stayed one too high for good, and the span never ended. A daemon whose clients reconnect
// through flaky networks climbs without bound.
//
// The counting is `ConnectionTracking.around`, which takes the four effects as a record so the
// order and the guarantee can be tested without the process-wide instruments.

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Server.McpServer

/// What happened to the instruments, in order.
type private Tape = { mutable Events: string list }

let private tapeTracking (tape: Tape) : ConnectionTracking =
  let note (event: string) = tape.Events <- tape.Events @ [ event ]
  { Opened = fun () -> note "opened"
    Closed = fun _ -> note "closed"
    Succeeded = fun () -> note "succeeded"
    Failed = fun reason -> note (sprintf "failed: %s" reason) }

let private runThrowing (tape: Tape) (error: exn) : Task<exn option> =
  task {
    try
      do! ConnectionTracking.around (tapeTracking tape) (fun () -> task { return raise error })
      return None
    with ex -> return Some ex
  }

[<Tests>]
let tests =
  testList "SSE connection tracking" [

    testTask "a connection that is served is opened, succeeds, then closes" {
      let tape = { Events = [] }
      do! ConnectionTracking.around (tapeTracking tape) (fun () -> task { return ConnectionOutcome.Served })
      tape.Events |> Expect.equal "in this order" [ "opened"; "succeeded"; "closed" ]
    }

    testTask "a connection that is refused fails with the reason, then closes" {
      let tape = { Events = [] }
      do! ConnectionTracking.around (tapeTracking tape) (fun () -> task { return ConnectionOutcome.Refused "no loop" })
      tape.Events |> Expect.equal "in this order" [ "opened"; "failed: no loop"; "closed" ]
    }

    testTask "WHY - a write that throws mid-catch-up still closes the connection, and the error reaches the caller" {
      let tape = { Events = [] }
      let! raised = runThrowing tape (InvalidOperationException "boom")
      raised |> Option.map (fun (ex: exn) -> ex.Message) |> Expect.equal "the caller sees the error" (Some "boom")
      tape.Events |> Expect.equal "failed, and still closed" [ "opened"; "failed: boom"; "closed" ]
    }

    testTask "a client that leaves mid-write is a normal end, not a failure" {
      let tape = { Events = [] }
      let! raised = runThrowing tape (OperationCanceledException())
      raised |> Option.isSome |> Expect.isTrue "the cancellation still reaches the caller"
      tape.Events |> Expect.equal "succeeded, then closed" [ "opened"; "succeeded"; "closed" ]
    }
  ]
