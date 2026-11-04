module SageFs.Tests.SseReplayTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs

// ── Fixtures ──

let epoch = "run-1"
/// More bytes than any test frame adds up to, so only the frame count ever bites.
let plentyOfBytes = 1 <<< 20
let roomy = { MaxFrames = 100; MaxBytes = plentyOfBytes }

let frameOf (scope: FrameScope) (wire: string) : SseFrame = { Scope = scope; Wire = wire }

let fill (buffer: SseReplay.Buffer) (frames: SseFrame list) : SseReplay.Buffer =
  frames |> List.fold (fun b f -> SseReplay.record f b |> snd) buffer

let seqsOf (catchUp: SseReplay.CatchUp) : int64 list option =
  match catchUp with
  | SseReplay.CatchUp.Replay got -> got |> List.map (fun r -> r.Id.Seq) |> Some
  | SseReplay.CatchUp.Resync _ -> None

let idAt (n: int64) : string option = Some (EventId.toWire { Epoch = epoch; Seq = n })

let daemonFrames (n: int) : SseFrame list =
  [ for i in 1 .. n -> frameOf FrameScope.Daemon (sprintf "d%d" i) ]

// ── Generators for the model check ──

let scopes = [| FrameScope.Daemon; FrameScope.Session "aaaaaaaa"; FrameScope.Session "bbbbbbbb" |]
let streams = [| StreamScope.EverySession; StreamScope.OnlySession "aaaaaaaa"; StreamScope.OnlySession "bbbbbbbb" |]

/// frames as (scope index, payload length); limits; the seq the client last saw; which stream it asked for.
/// lastSeen starts at 1: the first id a daemon issues is 1, and a client that has seen nothing sends no header.
let scenarioGen =
  gen {
    let! frames = Gen.listOf (gen { let! s = Gen.choose (0, 2) in let! n = Gen.choose (0, 120) in return s, n })
    let! maxFrames = Gen.choose (1, 12)
    let! maxBytes = Gen.choose (20, 600)
    let! lastSeen = Gen.choose (1, 40)
    let! stream = Gen.choose (0, 2)
    return frames, { MaxFrames = maxFrames; MaxBytes = maxBytes }, lastSeen, stream
  }

let materialize (frames: (int * int) list, limits: ReplayLimits, lastSeen: int, stream: int) =
  let all = frames |> List.map (fun (s, n) -> frameOf scopes.[s] (String.replicate n "z"))
  let buffer = fill (SseReplay.create epoch limits) all
  let model = all |> List.mapi (fun i f -> int64 (i + 1), f)
  buffer, model, streams.[stream], int64 lastSeen

let check (name: string) (property: (int * int) list * ReplayLimits * int * int -> bool) : Test =
  testProperty name (Prop.forAll (Arb.fromGen scenarioGen) property)

// ── Tests ──

[<Tests>]
let sseReplayTests =
  testList "SSE replay" [

    testList "event ids" [
      test "an id round-trips through its wire form" {
        let id = { Epoch = epoch; Seq = 42L }
        id |> EventId.toWire |> EventId.tryParse |> Expect.equal "same id back" (Ok id)
      }

      test "an epoch that contains colons still round-trips" {
        let id = { Epoch = "host:37749:7"; Seq = 9L }
        id |> EventId.toWire |> EventId.tryParse |> Expect.equal "same id back" (Ok id)
      }

      test "a blank id is refused as Blank" {
        EventId.tryParse "  " |> Expect.equal "blank" (Result.Error EventIdRefusal.Blank)
      }

      test "text with no epoch and seq is refused with the raw text" {
        [ "nonsense"; ":5"; "run-1:"; "run-1:abc"; "run-1:0"; "run-1:-3" ]
        |> List.iter (fun raw ->
          EventId.tryParse raw
          |> Expect.equal (sprintf "'%s' is unreadable" raw) (Result.Error (EventIdRefusal.NotEpochColonSeq raw)))
      }
    ]

    testList "recording" [
      test "record numbers frames from 1, one apart" {
        let buffer = fill (SseReplay.create epoch roomy) (daemonFrames 3)
        buffer.Latest |> Expect.equal "three recorded" 3L
        buffer.Frames |> Map.toList |> List.map fst |> Expect.equal "seqs" [ 1L; 2L; 3L ]
      }

      test "record hands back the frame's own id" {
        let item, _ = SseReplay.record (frameOf FrameScope.Daemon "x") (SseReplay.create epoch roomy)
        item.Id |> Expect.equal "epoch and first seq" { Epoch = epoch; Seq = 1L }
      }

      test "a frame bigger than the byte bound evicts itself, and its seq is still spent" {
        let big = frameOf FrameScope.Daemon (String.replicate 200 "z")
        let _, buffer = SseReplay.record big (SseReplay.create epoch { MaxFrames = 10; MaxBytes = 50 })
        buffer.Frames |> Map.count |> Expect.equal "nothing kept" 0
        buffer.Latest |> Expect.equal "seq still advanced" 1L
        buffer.Bytes |> Expect.equal "no bytes held" 0
      }

      test "WHY — one frame over the byte bound clears the window, so a client behind it resyncs and one after it replays" {
        // Dropping the old frames and keeping the seq spent is what stops a hole going unseen: a client at
        // seq 1 must not be told "replay from 2" when 2 is gone.
        let small = fill (SseReplay.create epoch { MaxFrames = 10; MaxBytes = 50 }) (daemonFrames 3)
        let _, afterBig = SseReplay.record (frameOf FrameScope.Daemon (String.replicate 200 "z")) small
        let afterNext = fill afterBig (daemonFrames 1)
        afterNext.Count |> Expect.equal "only the frame after the big one is kept" 1
        SseReplay.catchUp StreamScope.EverySession (idAt 1L) afterNext
        |> function
          | SseReplay.CatchUp.Resync (SseReplay.ResyncReason.Evicted _) -> ()
          | other -> failtestf "a client behind the big frame must resync, got %A" other
        SseReplay.catchUp StreamScope.EverySession (idAt 4L) afterNext
        |> seqsOf
        |> Expect.equal "a client current through the big frame replays what came after" (Some [ 5L ])
      }

      test "WHY — a frame's size is its UTF-8 bytes, which a char count would understate for a path or test name with an accent" {
        // 'h' 1 byte, 'é' 2, 'l' 'l' 'o' 3, '→' 3: six chars and nine bytes. Every other fixture here is made of "z".
        let wire = "héllo→"
        let item, _ = SseReplay.record (frameOf FrameScope.Daemon wire) (SseReplay.create epoch roomy)
        item.Bytes |> Expect.equal "nine bytes, not six chars" 9
        let _, tight = SseReplay.record (frameOf FrameScope.Daemon wire) (SseReplay.create epoch { MaxFrames = 10; MaxBytes = 8 })
        tight.Count |> Expect.equal "over an 8-byte bound by bytes though inside it by chars, so it evicts itself" 0
      }

      test "the count of kept frames is kept, not recounted" {
        let buffer = fill (SseReplay.create epoch { MaxFrames = 3; MaxBytes = plentyOfBytes }) (daemonFrames 5)
        buffer.Count |> Expect.equal "three kept" 3
      }

      test "the oldest frames go first when the count bound is hit" {
        let buffer = fill (SseReplay.create epoch { MaxFrames = 2; MaxBytes = plentyOfBytes }) (daemonFrames 5)
        buffer.Frames |> Map.toList |> List.map fst |> Expect.equal "newest two" [ 4L; 5L ]
      }
    ]

    testList "catching up" [
      test "no Last-Event-ID is a first connection" {
        SseReplay.create epoch roomy
        |> SseReplay.catchUp StreamScope.EverySession None
        |> Expect.equal "resync" (SseReplay.CatchUp.Resync SseReplay.ResyncReason.FirstConnection)
      }

      test "a client that is current gets an empty replay" {
        let buffer = fill (SseReplay.create epoch roomy) (daemonFrames 3)
        SseReplay.catchUp StreamScope.EverySession (idAt 3L) buffer
        |> seqsOf
        |> Expect.equal "nothing missed" (Some [])
      }

      test "a client behind gets exactly the frames after its id, in order" {
        let buffer = fill (SseReplay.create epoch roomy) (daemonFrames 5)
        SseReplay.catchUp StreamScope.EverySession (idAt 2L) buffer
        |> seqsOf
        |> Expect.equal "3,4,5" (Some [ 3L; 4L; 5L ])
      }

      test "a session-scoped client is replayed its session's frames and the daemon's, and no other session's" {
        let buffer =
          fill (SseReplay.create epoch roomy)
            [ frameOf (FrameScope.Session "aaaaaaaa") "a1"
              frameOf (FrameScope.Session "bbbbbbbb") "b1"
              frameOf FrameScope.Daemon "d1"
              frameOf (FrameScope.Session "aaaaaaaa") "a2" ]
        SseReplay.catchUp (StreamScope.OnlySession "aaaaaaaa") (idAt 1L) buffer
        |> seqsOf
        |> Expect.equal "skips b1" (Some [ 3L; 4L ])
      }

      test "an id from another run is a restarted daemon, naming the old epoch" {
        let buffer = fill (SseReplay.create epoch roomy) (daemonFrames 3)
        SseReplay.catchUp StreamScope.EverySession (Some "run-0:2") buffer
        |> Expect.equal "restarted" (SseReplay.CatchUp.Resync (SseReplay.ResyncReason.DaemonRestarted "run-0"))
      }

      test "an id past this run's newest is ahead of this run" {
        let buffer = fill (SseReplay.create epoch roomy) (daemonFrames 3)
        SseReplay.catchUp StreamScope.EverySession (idAt 9L) buffer
        |> Expect.equal "ahead" (SseReplay.CatchUp.Resync (SseReplay.ResyncReason.AheadOfThisRun (9L, 3L)))
      }

      test "an id whose next frame was evicted is a resync saying what was wanted and what is left" {
        let buffer = fill (SseReplay.create epoch { MaxFrames = 2; MaxBytes = plentyOfBytes }) (daemonFrames 5)
        SseReplay.catchUp StreamScope.EverySession (idAt 1L) buffer
        |> Expect.equal "evicted" (SseReplay.CatchUp.Resync (SseReplay.ResyncReason.Evicted (2L, 4L)))
      }

      test "unreadable text is a resync carrying the refusal" {
        SseReplay.create epoch roomy
        |> SseReplay.catchUp StreamScope.EverySession (Some "garbage")
        |> Expect.equal "unreadable"
             (SseReplay.CatchUp.Resync (SseReplay.ResyncReason.IdUnreadable (EventIdRefusal.NotEpochColonSeq "garbage")))
      }
    ]

    testList "against a naive model" [
      check "an answered replay is exactly the model's missed, visible frames; a refusal names the true reason" (fun scenario ->
        let buffer, model, stream, lastSeen = materialize scenario
        let latest = int64 (List.length model)
        match SseReplay.catchUp stream (Some (EventId.toWire { Epoch = epoch; Seq = lastSeen })) buffer with
        | SseReplay.CatchUp.Replay got ->
          lastSeen <= latest
          && (got |> List.map (fun r -> r.Id.Seq))
             = (model |> List.filter (fun (seq, f) -> seq > lastSeen && SseFrame.visibleTo stream f) |> List.map fst)
        | SseReplay.CatchUp.Resync (SseReplay.ResyncReason.Evicted (wanted, first)) ->
          wanted = lastSeen + 1L && first > wanted && first <= latest + 1L
        | SseReplay.CatchUp.Resync (SseReplay.ResyncReason.AheadOfThisRun (theirs, l)) ->
          theirs = lastSeen && lastSeen > latest && l = latest
        | SseReplay.CatchUp.Resync _ -> false)

      test "WHY — an evicted wanted frame is refused with exactly the seq wanted and the first one still held" {
        // Five frames, room for two: seqs 4 and 5 are kept. A client at seq 1 wants 2, which is gone.
        let buffer = fill (SseReplay.create epoch { MaxFrames = 2; MaxBytes = plentyOfBytes }) (daemonFrames 5)
        SseReplay.catchUp StreamScope.EverySession (idAt 1L) buffer
        |> Expect.equal "wanted 2, first held 4" (SseReplay.CatchUp.Resync (SseReplay.ResyncReason.Evicted (2L, 4L)))
        SseReplay.catchUp StreamScope.EverySession (idAt 3L) buffer
        |> seqsOf
        |> Expect.equal "a client at seq 3 wants 4, which is held, and is replayed 4 and 5" (Some [ 4L; 5L ])
      }

      check "the frame and byte bounds hold, and the byte total is the sum of what is kept" (fun scenario ->
        let buffer, model, _, _ = materialize scenario
        Map.count buffer.Frames <= buffer.Limits.MaxFrames
        && buffer.Count = Map.count buffer.Frames
        && buffer.Bytes <= buffer.Limits.MaxBytes
        && buffer.Bytes = (buffer.Frames |> Map.toList |> List.sumBy (fun (_, r) -> r.Bytes))
        && buffer.Latest = int64 (List.length model))

      check "an id from another run never replays" (fun scenario ->
        let buffer, _, stream, lastSeen = materialize scenario
        match SseReplay.catchUp stream (Some (EventId.toWire { Epoch = "run-0"; Seq = lastSeen })) buffer with
        | SseReplay.CatchUp.Resync (SseReplay.ResyncReason.DaemonRestarted "run-0") -> true
        | _ -> false)
    ]
  ]
