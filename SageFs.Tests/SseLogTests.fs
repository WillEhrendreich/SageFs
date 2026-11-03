module SageFs.Tests.SseLogTests

open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs

let epoch = "run-1"

/// Room for every frame a test publishes, so only what a test asks for is ever evicted.
let roomy = { MaxFrames = 1 <<< 14; MaxBytes = 1 <<< 24 }

let newLog () = SseLog(epoch, roomy)

let frameFor (scope: FrameScope) (n: int) : SseFrame =
  { Scope = scope; Wire = sprintf "event: x\ndata: %d\n\n" n }

let daemonFrame (n: int) : SseFrame = frameFor FrameScope.Daemon n

let seqsOf (items: seq<Replayable>) : int64 list = items |> Seq.map (fun r -> r.Id.Seq) |> List.ofSeq

let replayed (attachment: SseAttachment) : Replayable list =
  match attachment.CatchUp with
  | SseReplay.CatchUp.Replay items -> items
  | SseReplay.CatchUp.Resync _ -> []

let attachCollecting (log: SseLog) (stream: StreamScope) (lastEventId: string option) =
  let live = ResizeArray<Replayable>()
  let attachment = log.Attach(stream, lastEventId, live.Add)
  attachment, live

let idAt (n: int64) : string option = Some (EventId.toWire { Epoch = epoch; Seq = n })

[<Tests>]
let sseLogTests =
  testList "SSE log" [

    testList "publishing" [
      test "frames are numbered from 1 in the order they were published, under this run's epoch" {
        let log = newLog ()
        let ids = [ 1 .. 3 ] |> List.map (fun n -> (log.Publish (daemonFrame n)).Id)
        ids |> Expect.equal "epoch and seq" [ for n in 1L .. 3L -> { Epoch = epoch; Seq = n } ]
      }
    ]

    testList "attaching" [
      test "no id is a first connection, and what is published after it arrives live, in order" {
        let log = newLog ()
        log.Publish (daemonFrame 1) |> ignore
        let attachment, live = attachCollecting log StreamScope.EverySession None
        attachment.CatchUp
        |> Expect.equal "first connection" (SseReplay.CatchUp.Resync SseReplay.ResyncReason.FirstConnection)
        [ 2; 3 ] |> List.iter (fun n -> log.Publish (daemonFrame n) |> ignore)
        seqsOf live |> Expect.equal "2 then 3" [ 2L; 3L ]
      }

      test "an id replays what was missed, and what is published after it arrives live from the next seq" {
        let log = newLog ()
        [ 1 .. 4 ] |> List.iter (fun n -> log.Publish (daemonFrame n) |> ignore)
        let attachment, live = attachCollecting log StreamScope.EverySession (idAt 2L)
        replayed attachment |> seqsOf |> Expect.equal "3,4 replayed" [ 3L; 4L ]
        log.Publish (daemonFrame 5) |> ignore
        seqsOf live |> Expect.equal "5 live" [ 5L ]
      }

      test "the position is the newest id at the moment of attach, and none before anything was published" {
        let log = newLog ()
        let before, _ = attachCollecting log StreamScope.EverySession None
        before.Position |> Expect.equal "nothing published yet" LogPosition.Empty
        [ 1 .. 3 ] |> List.iter (fun n -> log.Publish (daemonFrame n) |> ignore)
        let after, _ = attachCollecting log StreamScope.EverySession None
        after.Position |> Expect.equal "newest" (LogPosition.At { Epoch = epoch; Seq = 3L })
      }

      test "a detached observer hears nothing more" {
        let log = newLog ()
        let attachment, live = attachCollecting log StreamScope.EverySession None
        log.Publish (daemonFrame 1) |> ignore
        attachment.Detach ()
        log.Publish (daemonFrame 2) |> ignore
        seqsOf live |> Expect.equal "only the first" [ 1L ]
      }

      test "a stream scoped to one session is replayed and fed its session's frames and the daemon's, and no other session's" {
        let log = newLog ()
        let mine = FrameScope.Session "aaaaaaaa"
        let theirs = FrameScope.Session "bbbbbbbb"
        log.Publish (frameFor mine 1) |> ignore
        log.Publish (frameFor theirs 2) |> ignore
        log.Publish (frameFor FrameScope.Daemon 3) |> ignore
        let attachment, live = attachCollecting log (StreamScope.OnlySession "aaaaaaaa") (idAt 1L)
        replayed attachment |> seqsOf |> Expect.equal "skips theirs" [ 3L ]
        log.Publish (frameFor theirs 4) |> ignore
        log.Publish (frameFor mine 5) |> ignore
        seqsOf live |> Expect.equal "only mine live" [ 5L ]
      }

      test "an observer that attaches from inside a publish gets that frame once, by replay, and not again live" {
        let log = newLog ()
        [ 1 .. 2 ] |> List.iter (fun n -> log.Publish (daemonFrame n) |> ignore)
        let inner = ResizeArray<Replayable>()
        let innerReplay = ref []
        let attachedInside = ref false
        // Fires while frame 3 is being published, after it is recorded and before the publish returns.
        let outer =
          log.Attach(StreamScope.EverySession, idAt 2L, fun item ->
            match attachedInside.Value with
            | true -> ()
            | false ->
              attachedInside.Value <- true
              let attachment = log.Attach(StreamScope.EverySession, idAt 2L, inner.Add)
              innerReplay.Value <- replayed attachment)
        log.Publish (daemonFrame 3) |> ignore
        log.Publish (daemonFrame 4) |> ignore
        outer.Detach ()
        let heard = (innerReplay.Value |> seqsOf) @ seqsOf inner
        heard |> Expect.equal "3 once, then 4" [ 3L; 4L ]
      }
    ]

    testList "on the wire" [
      test "a frame is written with its id first and its own text whole after it" {
        let item = { Id = { Epoch = epoch; Seq = 7L }; Frame = daemonFrame 1; Bytes = 0 }
        Replayable.toWire item |> Expect.equal "id then frame" (sprintf "id: %s\n%s" (EventId.toWire item.Id) item.Frame.Wire)
      }

      test "an id-only frame moves a client's last id and carries nothing else" {
        let id = { Epoch = epoch; Seq = 9L }
        EventId.toIdOnlyFrame id |> Expect.equal "just the id" (sprintf "id: %s\n\n" (EventId.toWire id))
      }
    ]

    testList "under concurrent publishers" [
      testTask "WHY — a client that keeps reconnecting while others publish sees each seq once, in order, whatever the interleaving" {
        let log = newLog ()
        let publishers = 4
        let perPublisher = 400
        let publishing =
          [ for p in 1 .. publishers ->
              Task.Run(fun () ->
                for i in 1 .. perPublisher do
                  log.Publish (daemonFrame (p * 1000 + i)) |> ignore) ]
        let violations = ResizeArray<string>()
        let lastSeen = ref None
        let cycles = ref 0
        let reconnect () =
          let attachment, live = attachCollecting log StreamScope.EverySession (lastSeen.Value |> Option.map EventId.toWire)
          for _ in 1 .. 50 do Thread.Yield() |> ignore
          attachment.Detach ()
          let heard = (replayed attachment |> seqsOf) @ seqsOf live
          let positionSeq =
            match attachment.Position with
            | LogPosition.At position -> position.Seq
            | LogPosition.Empty -> 0L
          let firstExpected =
            match attachment.CatchUp, lastSeen.Value with
            | SseReplay.CatchUp.Replay _, Some last -> last.Seq + 1L
            | _ -> positionSeq + 1L
          let expected = [ firstExpected .. firstExpected + int64 (List.length heard) - 1L ]
          match heard = expected with
          | true -> ()
          | false -> violations.Add (sprintf "after %A the client heard %A, expected %A" lastSeen.Value heard expected)
          lastSeen.Value <-
            (match List.tryLast heard, attachment.Position with
             | Some last, _ -> Some { Epoch = epoch; Seq = last }
             | None, LogPosition.At position -> Some position
             | None, LogPosition.Empty -> lastSeen.Value)
          cycles.Value <- cycles.Value + 1
        let reconnecting =
          Task.Run(fun () ->
            while not (publishing |> List.forall (fun t -> t.IsCompleted)) || cycles.Value < 20 do
              reconnect ())
        do! Task.WhenAll (reconnecting :: publishing)
        reconnect ()
        violations |> Expect.isEmpty "no gap, no repeat, no reorder on any reconnect"
        lastSeen.Value |> Expect.equal "the last reconnect reached the newest frame" (Some { Epoch = epoch; Seq = int64 (publishers * perPublisher) })
      }
    ]
  ]
