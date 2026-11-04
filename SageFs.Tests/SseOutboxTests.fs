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

    testTask "WHY — a client stalled behind big frames is cut off by their size, long before the frame count would" {
      // 20 chars is 40 bytes in memory. Two fit in 100; the third would make 120.
      let outbox = SseOutbox({ QueuedFrames = 1000; QueuedBytes = 100 })
      let written, write = collecting ()
      let frame = System.String('x', 20)
      for _ in 1 .. 4 do outbox.Offer frame
      let! ended = SseOutbox.drain outbox write CancellationToken.None
      ended |> Expect.equal "overflowed on bytes" SseOutbox.DrainEnd.Overflowed
      written.Count |> Expect.equal "the two that fit were written before the cut" 2
    }

    testTask "WHY — a frame bigger than the whole byte cap is still delivered when nothing is waiting, or no client could ever get it" {
      let outbox = SseOutbox({ QueuedFrames = 8; QueuedBytes = 100 })
      let written, write = collecting ()
      let huge = System.String('x', 500)
      outbox.Offer huge
      outbox.Close ()
      let! ended = SseOutbox.drain outbox write CancellationToken.None
      ended |> Expect.equal "closed, not overflowed" SseOutbox.DrainEnd.Closed
      List.ofSeq written |> Expect.equal "delivered whole" [ huge ]
    }

    testTask "what the client has read gives its room back" {
      let outbox = SseOutbox({ QueuedFrames = 8; QueuedBytes = 100 })
      let frame = System.String('x', 20)
      outbox.Offer frame
      outbox.Offer frame
      let! first = outbox.ReadAsync CancellationToken.None
      first |> Expect.equal "the oldest frame" frame
      outbox.Offer frame
      outbox.Overflowed |> Expect.isFalse "80 bytes queued again, inside the cap"
    }

    testTask "a logged frame is written as its id and then its text, built when it is written" {
      let outbox = SseOutbox 4
      let written, write = collecting ()
      let item : Replayable =
        { Id = { Epoch = "e1"; Seq = 7L }
          Frame = { Scope = FrameScope.Daemon; Wire = "event: x\ndata: {}\n\n" }
          Bytes = 19 }
      outbox.OfferLogged item
      outbox.Close ()
      let! _ = SseOutbox.drain outbox write CancellationToken.None
      List.ofSeq written |> Expect.equal "the same text Replayable.toWire gives" [ Replayable.toWire item ]
    }

    testTask "WHY — a logged frame weighs what the log says it weighs, so a queue of shared frames is capped too" {
      let outbox = SseOutbox({ QueuedFrames = 8; QueuedBytes = 100 })
      let item : Replayable =
        { Id = { Epoch = "e1"; Seq = 1L }
          Frame = { Scope = FrameScope.Daemon; Wire = "x" }
          Bytes = 60 }
      outbox.OfferLogged item
      outbox.OfferLogged { item with Id = { item.Id with Seq = 2L } }
      outbox.Overflowed |> Expect.isTrue "120 queued is over 100"
    }

    testList "the heartbeat's position" [
      testTask "WHY — names the log's position when it moved, and is the keepalive comment when it did not" {
        let log = SseLog("e1", { MaxFrames = 100; MaxBytes = 1 <<< 20 })
        let outbox = SseOutbox 8
        let beat = HeartbeatPosition(log, outbox)
        beat.Beat ()
        log.Publish { Scope = FrameScope.Daemon; Wire = "a" } |> ignore
        log.Publish { Scope = FrameScope.Daemon; Wire = "b" } |> ignore
        beat.Beat ()
        beat.Beat ()
        log.Publish { Scope = FrameScope.Daemon; Wire = "c" } |> ignore
        beat.Beat ()
        outbox.Close ()
        let taken, write = collecting ()
        let! _ = SseOutbox.drain outbox write CancellationToken.None
        List.ofSeq taken
        |> Expect.equal "keepalive (empty log), id 2, keepalive (unmoved), id 3"
             [ SseOutbox.KeepaliveFrame
               EventId.toIdOnlyFrame { Epoch = "e1"; Seq = 2L }
               SseOutbox.KeepaliveFrame
               EventId.toIdOnlyFrame { Epoch = "e1"; Seq = 3L } ]
      }
    ]
  ]
