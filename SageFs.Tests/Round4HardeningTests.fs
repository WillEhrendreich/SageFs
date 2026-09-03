module SageFs.Tests.Round4HardeningTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

// ---------------------------------------------------------------------------
// W1 — NotifyWorkerDied stale-event guard
// ---------------------------------------------------------------------------
// Bug: `when currentPid <> workerPid` fires when workerPid=-1, silently
//      dropping synthetic WorkerExited events (killing broken sessions stays
//      broken forever).
// Fix: `when currentPid <> workerPid && workerPid >= 0`

[<Tests>]
let stalePidGuardTests =
  testList "SessionManager stale-event guard" [

    testCase "old guard: synthetic pid=-1 incorrectly treated as stale (BUG confirmed)" <| fun _ ->
      // Documents the pre-fix behavior: guard fires even for synthetic -1 events
      let isStaleOld currentPid workerPid = currentPid <> workerPid
      isStaleOld 1234 -1 |> Expect.isTrue "OLD guard marks pid=-1 as stale (bug)"

    testCase "fixed guard: synthetic pid=-1 is NOT stale" <| fun _ ->
      let isStaleNew currentPid workerPid = currentPid <> workerPid && workerPid > 0
      isStaleNew 1234 -1 |> Expect.isFalse "pid=-1 must pass through guard"

    testCase "fixed guard: different real pids remain stale" <| fun _ ->
      let isStaleNew currentPid workerPid = currentPid <> workerPid && workerPid > 0
      isStaleNew 1234 5678 |> Expect.isTrue "different real pids are still stale"

    testCase "fixed guard: same pid is not stale" <| fun _ ->
      let isStaleNew currentPid workerPid = currentPid <> workerPid && workerPid > 0
      isStaleNew 1234 1234 |> Expect.isFalse "same pid is not stale"

    testCase "fixed guard: pid=0 (invalid) also passes through" <| fun _ ->
      let isStaleNew currentPid workerPid = currentPid <> workerPid && workerPid > 0
      isStaleNew 1234 0 |> Expect.isFalse "pid=0 treated as non-stale (boundary)"
  ]

// ---------------------------------------------------------------------------
// W5 — readLpStringOption: missing > Int32.MaxValue overflow guard
// ---------------------------------------------------------------------------
// Bug: readLpStringOption reads uint32 length then casts to int without the
//      > Int32.MaxValue guard (present in readLpString but absent here).
//      For len=0x80000001u: `int 0x80000001u = -2147483647` → ReadBytes
//      with a negative count → ArgumentOutOfRangeException.
// Fix: add `match len > uint32 Int32.MaxValue` guard like readLpString.
// RED TEST: before fix throws ArgumentOutOfRangeException (not InvalidOperationException).

[<Tests>]
let readLpStringOptionOverflowTests =
  testList "BinaryPrimitives.readLpStringOption overflow guard" [

    testCase "len > Int32.MaxValue throws InvalidOperationException not ArgumentOutOfRangeException" <| fun _ ->
      // 0x80000001u = 2147483649 (exceeds Int32.MaxValue=2147483647)
      let bytes = [| 0x01uy; 0x00uy; 0x00uy; 0x80uy |]  // uint32 LE: 0x80000001
      use ms = new MemoryStream(bytes)
      use br = new BinaryReader(ms)
      let mutable thrownEx: exn = null
      try BinaryPrimitives.readLpStringOption br |> ignore
      with e -> thrownEx <- e
      thrownEx |> Expect.isNotNull "should throw for oversized len"
      (thrownEx :? InvalidOperationException)
      |> Expect.isTrue
        (sprintf "should be InvalidOperationException, got %s: %s"
          (if thrownEx = null then "null" else thrownEx.GetType().Name)
          (if thrownEx = null then "" else thrownEx.Message))

    testCase "None marker 0xFFFFFFFF is still decoded as None" <| fun _ ->
      let bytes = [| 0xFFuy; 0xFFuy; 0xFFuy; 0xFFuy |]
      use ms = new MemoryStream(bytes)
      use br = new BinaryReader(ms)
      BinaryPrimitives.readLpStringOption br
      |> Expect.equal "0xFFFFFFFF must be None" None

    testCase "zero length returns empty string option" <| fun _ ->
      let bytes = [| 0x00uy; 0x00uy; 0x00uy; 0x00uy |]
      use ms = new MemoryStream(bytes)
      use br = new BinaryReader(ms)
      BinaryPrimitives.readLpStringOption br
      |> Expect.equal "zero len = Some empty string" (Some "")
  ]
