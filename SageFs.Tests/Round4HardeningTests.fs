module SageFs.Tests.Round4HardeningTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

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
