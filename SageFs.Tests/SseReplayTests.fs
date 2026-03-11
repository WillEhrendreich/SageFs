module SageFs.Tests.SseReplayTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.SseReplayBuffer
open SageFs.SseWriter

// ── Helpers ──

let pushN (n: int) (buf: Buffer) : Buffer =
  [ 1 .. n ]
  |> List.fold (fun b i ->
    push "test" (sprintf "data_%d" i) b |> snd
  ) buf

// ── Tests ──

[<Tests>]
let sseReplayTests =
  testList "SSE Replay" [

    testList "Buffer mechanics" [
      test "fresh empty buffer replayFrom 0 returns Replayed empty" {
        let buf = create 16
        buf
        |> replayFrom 0L
        |> Expect.equal "should return empty replay" (ReplayResult.Replayed [])
      }

      test "push increments SeqId monotonically" {
        let buf = create 16
        let (id1, buf) = push "a" "1" buf
        let (id2, buf) = push "b" "2" buf
        let (id3, _) = push "c" "3" buf
        id1 |> Expect.equal "first id" 1L
        id2 |> Expect.equal "second id" 2L
        id3 |> Expect.equal "third id" 3L
      }

      test "push returns correct SeqId matching currentSeqId" {
        let buf = create 8
        let (id1, buf1) = push "x" "payload" buf
        id1 |> Expect.equal "seqId matches currentSeqId" (currentSeqId buf1)
      }

      test "buffer wraps correctly at capacity" {
        let buf = create 3 |> pushN 5
        count buf |> Expect.equal "count at capacity" 3
        currentSeqId buf |> Expect.equal "totalPushed" 5L
      }

      test "evicted events are not replayable" {
        let buf = create 3 |> pushN 10
        match replayFrom 5L buf with
        | ReplayResult.GapDetected first ->
          first |> Expect.equal "first available" 8L
        | ReplayResult.Replayed _ ->
          failtest "should have returned GapDetected"
      }
    ]

    testList "Replay logic" [
      test "replayFrom with valid recent ID returns correct events" {
        let buf = create 16
        let (_, buf) = push "a" "data_a" buf
        let (_, buf) = push "b" "data_b" buf
        let (_, buf) = push "c" "data_c" buf
        match replayFrom 1L buf with
        | ReplayResult.Replayed events ->
          events |> List.length |> Expect.equal "should have 2 events" 2
          events.[0].EventType |> Expect.equal "first replayed" "b"
          events.[1].EventType |> Expect.equal "second replayed" "c"
        | ReplayResult.GapDetected _ ->
          failtest "should have returned Replayed"
      }

      test "replayFrom with evicted ID returns GapDetected" {
        let buf = create 3 |> pushN 10
        match replayFrom 2L buf with
        | ReplayResult.GapDetected firstAvail ->
          firstAvail |> Expect.equal "oldest available" 8L
        | ReplayResult.Replayed _ ->
          failtest "should have returned GapDetected"
      }

      test "replayFrom with future ID returns empty Replayed" {
        let buf = create 16 |> pushN 5
        buf
        |> replayFrom 100L
        |> Expect.equal "future ID" (ReplayResult.Replayed [])
      }

      test "replayFrom with exact last ID returns empty Replayed" {
        let buf = create 16 |> pushN 5
        buf
        |> replayFrom 5L
        |> Expect.equal "exact last" (ReplayResult.Replayed [])
      }

      test "replay preserves event order oldest first" {
        let buf = create 16
        let (_, buf) = push "e1" "p1" buf
        let (_, buf) = push "e2" "p2" buf
        let (_, buf) = push "e3" "p3" buf
        let (_, buf) = push "e4" "p4" buf
        match replayFrom 1L buf with
        | ReplayResult.Replayed events ->
          events
          |> List.map (fun e -> e.SeqId)
          |> Expect.equal "ascending order" [ 2L; 3L; 4L ]
        | ReplayResult.GapDetected _ ->
          failtest "should have returned Replayed"
      }
    ]

    testList "SSE format" [
      test "formatWithId produces correct SSE id line" {
        let frame = formatSseEvent "test" "hello"
        let withId = formatWithId 42L frame
        withId.StartsWith("id: 42\n")
        |> Expect.isTrue "should start with id: 42"
      }

      test "formatWithId works with multiline data" {
        let frame = formatSseEvent "update" "line1\nline2"
        let withId = formatWithId 7L frame
        withId.StartsWith("id: 7\n")
        |> Expect.isTrue "should start with id: 7"
        withId.Contains("event: update")
        |> Expect.isTrue "should contain event type"
        withId.Contains("data: line1")
        |> Expect.isTrue "should contain first data line"
        withId.Contains("data: line2")
        |> Expect.isTrue "should contain second data line"
      }

      test "id field appears before event field per SSE spec" {
        let frame = formatSseEvent "ping" "pong"
        let withId = formatWithId 99L frame
        let idIdx = withId.IndexOf("id:")
        let eventIdx = withId.IndexOf("event:")
        (idIdx < eventIdx)
        |> Expect.isTrue "id should precede event"
      }

      test "formatSseEventWithId produces complete SSE frame with id" {
        let result = formatSseEventWithId 42L "test_summary" """{"total":100}"""
        result
        |> Expect.equal "full frame"
          "id: 42\nevent: test_summary\ndata: {\"total\":100}\n\n"
      }
    ]

    testList "Integration-style" [
      test "1000 events pushed, replay last 10 works" {
        let buf =
          [ 1 .. 1000 ]
          |> List.fold (fun b i ->
            push "evt" (sprintf "payload_%d" i) b |> snd
          ) (create 512)
        match replayFrom 990L buf with
        | ReplayResult.Replayed events ->
          events |> List.length |> Expect.equal "should have 10 events" 10
          events |> List.head |> fun e -> e.SeqId |> Expect.equal "first replayed" 991L
          events |> List.last |> fun e -> e.SeqId |> Expect.equal "last replayed" 1000L
        | ReplayResult.GapDetected _ ->
          failtest "should have returned Replayed"
      }

      test "buffer at capacity, replay full buffer works" {
        let cap = 64
        let buf =
          [ 1 .. cap ]
          |> List.fold (fun b i ->
            push "evt" (sprintf "p%d" i) b |> snd
          ) (create cap)
        match replayFrom 0L buf with
        | ReplayResult.Replayed events ->
          events |> List.length |> Expect.equal "full buffer replay" cap
        | ReplayResult.GapDetected _ ->
          failtest "should have returned Replayed"
      }

      test "buffer overflow, GapDetected has correct firstAvailable" {
        let buf =
          [ 1 .. 100 ]
          |> List.fold (fun b i ->
            push "evt" (sprintf "p%d" i) b |> snd
          ) (create 10)
        match replayFrom 50L buf with
        | ReplayResult.GapDetected firstAvail ->
          firstAvail |> Expect.equal "first available" 91L
        | ReplayResult.Replayed _ ->
          failtest "should have returned GapDetected"
      }
    ]

    testList "Property-based" [
      testProperty "all pushed events are replayable until evicted"
        (fun (PositiveInt cap) ->
          let c = min cap 100
          let n = c + 5
          let buf =
            [ 1 .. n ]
            |> List.fold (fun b i ->
              push "evt" (sprintf "p%d" i) b |> snd
            ) (create c)
          let oldestAvailable = int64 n - int64 c + 1L
          match replayFrom (oldestAvailable - 1L) buf with
          | ReplayResult.Replayed events -> events |> List.length = c
          | ReplayResult.GapDetected _ -> false
        )

      testProperty "SeqId is strictly monotonic across pushes"
        (fun (items: NonEmptyArray<string>) ->
          let items = items.Get |> Array.truncate 50
          let (ids, _) =
            items
            |> Array.fold (fun (acc, b) item ->
              let (id, newBuf) = push "evt" item b
              (acc @ [id], newBuf)
            ) ([], create 100)
          ids |> List.pairwise |> List.forall (fun (a, b) -> b = a + 1L)
        )

      testProperty "replayFrom lastSeqId always returns empty Replayed"
        (fun (PositiveInt n) ->
          let n = min n 100
          let buf =
            [ 1 .. n ]
            |> List.fold (fun b i ->
              push "evt" (sprintf "p%d" i) b |> snd
            ) (create 50)
          let lastId = currentSeqId buf
          match replayFrom lastId buf with
          | ReplayResult.Replayed events -> events.IsEmpty
          | ReplayResult.GapDetected _ -> false
        )

      testProperty "replayed events preserve insertion order"
        (fun (PositiveInt cap) (PositiveInt n) ->
          let c = max 2 (min cap 50)
          let total = min n 100
          let buf =
            [ 1 .. total ]
            |> List.fold (fun b i ->
              push "evt" (sprintf "p%d" i) b |> snd
            ) (create c)
          let replayPoint = max 0L (currentSeqId buf - int64 (min c total) / 2L)
          match replayFrom replayPoint buf with
          | ReplayResult.Replayed events ->
            let seqIds = events |> List.map (fun e -> e.SeqId)
            seqIds = List.sort seqIds
          | ReplayResult.GapDetected _ -> true
        )
    ]

    testList "Edge cases" [
      test "replayFrom 0 on populated buffer returns all events" {
        let buf = create 16
        let (_, buf) = push "a" "1" buf
        let (_, buf) = push "b" "2" buf
        let (_, buf) = push "c" "3" buf
        match replayFrom 0L buf with
        | ReplayResult.Replayed events ->
          events |> List.length |> Expect.equal "all events" 3
        | ReplayResult.GapDetected _ ->
          failtest "should have returned Replayed"
      }

      test "capacity 1 buffer replays correctly" {
        let buf = create 1
        let (_, buf) = push "only" "data" buf
        match replayFrom 0L buf with
        | ReplayResult.Replayed events ->
          events |> List.length |> Expect.equal "single event" 1
          events.[0].EventType |> Expect.equal "event type" "only"
        | ReplayResult.GapDetected _ ->
          failtest "should have returned Replayed"
      }

      test "capacity 1 buffer evicts previous on second push" {
        let buf = create 1
        let (_, buf) = push "first" "1" buf
        let (_, buf) = push "second" "2" buf
        match replayFrom 0L buf with
        | ReplayResult.GapDetected first ->
          first |> Expect.equal "only second available" 2L
        | ReplayResult.Replayed _ ->
          failtest "should have returned GapDetected"
      }

      test "sequenced events have correct timestamps" {
        let before = System.DateTimeOffset.UtcNow
        let buf = create 16
        let (_, buf) = push "evt" "data" buf
        let after = System.DateTimeOffset.UtcNow
        match replayFrom 0L buf with
        | ReplayResult.Replayed [ evt ] ->
          (evt.Timestamp >= before) |> Expect.isTrue "timestamp >= before"
          (evt.Timestamp <= after) |> Expect.isTrue "timestamp <= after"
        | _ -> failtest "expected single event"
      }
    ]
  ]
