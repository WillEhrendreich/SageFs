module SageFs.Tests.SseOutboxTests

open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs

let collecting () : ResizeArray<string> * (string -> Task) =
  let written = ResizeArray<string>()
  written, (fun frame -> written.Add frame; Task.CompletedTask)

[<Tests>]
let sseOutboxTests =
  testList "SSE outbox" [

    testTask "frames are written in the order they were offered, and a closed outbox ends the drain after the last" {
      let outbox = SseOutbox 8
      let written, write = collecting ()
      [ "a"; "b"; "c" ] |> List.iter outbox.Offer
      outbox.Close ()
      let! ended = SseOutbox.drain outbox write CancellationToken.None
      ended |> Expect.equal "closed" SseOutbox.DrainEnd.Closed
      List.ofSeq written |> Expect.equal "in order" [ "a"; "b"; "c" ]
    }

    testTask "WHY — a client that falls behind loses the connection, never a frame it is not told about" {
      let outbox = SseOutbox 2
      let written, write = collecting ()
      [ "a"; "b"; "c"; "d" ] |> List.iter outbox.Offer
      let! ended = SseOutbox.drain outbox write CancellationToken.None
      ended |> Expect.equal "overflowed" SseOutbox.DrainEnd.Overflowed
      List.ofSeq written |> Expect.equal "what fit was written, in order, and nothing after the gap" [ "a"; "b" ]
    }

    testTask "a heartbeat that finds the queue full is dropped, and is not an overflow" {
      let outbox = SseOutbox 1
      let written, write = collecting ()
      outbox.Offer "a"
      outbox.Keepalive ()
      outbox.Close ()
      let! ended = SseOutbox.drain outbox write CancellationToken.None
      ended |> Expect.equal "closed, not overflowed" SseOutbox.DrainEnd.Closed
      List.ofSeq written |> Expect.equal "only the frame" [ "a" ]
    }

    testTask "a heartbeat is written when there is room" {
      let outbox = SseOutbox 4
      let written, write = collecting ()
      outbox.Offer "a"
      outbox.Keepalive ()
      outbox.Close ()
      let! _ = SseOutbox.drain outbox write CancellationToken.None
      List.ofSeq written |> Expect.equal "frame then keepalive" [ "a"; SseOutbox.KeepaliveFrame ]
    }

    testTask "a cancelled connection ends the drain quietly" {
      let outbox = SseOutbox 4
      let _, write = collecting ()
      let cts = new CancellationTokenSource()
      cts.Cancel ()
      let! ended = SseOutbox.drain outbox write cts.Token
      ended |> Expect.equal "client left" SseOutbox.DrainEnd.ClientLeft
    }

    testTask "a write that fails because the client went away ends the drain quietly" {
      let outbox = SseOutbox 4
      outbox.Offer "a"
      let! ended = SseOutbox.drain outbox (fun _ -> raise (IOException "broken pipe")) CancellationToken.None
      ended |> Expect.equal "client left" SseOutbox.DrainEnd.ClientLeft
    }
  ]
