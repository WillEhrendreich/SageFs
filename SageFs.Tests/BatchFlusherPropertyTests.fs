module SageFs.Tests.BatchFlusherPropertyTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 200 }

[<Tests>]
let batchFlusherPropertyTests = testList "BatchFlusher properties" [

  testPropertyWithConfig cfg "flush never loses items" <|
    fun (items: NonEmptyArray<int>) ->
      let flushed = ResizeArray<int array>()
      use flusher = new BatchFlusher<int>(100, 0, fun batch -> flushed.Add(batch))
      for item in items.Get do flusher.Add(item)
      flusher.Flush()
      let result = flushed |> Seq.collect id |> Seq.sort |> Seq.toArray
      let expected = items.Get |> Array.sort
      result |> Expect.equal "all items present after flush" expected

  testPropertyWithConfig cfg "batch sizes never exceed maxBatchSize" <|
    fun (PositiveInt maxSize) (items: NonEmptyArray<int>) ->
      let maxSize = max 1 (min maxSize 50)
      let flushed = ResizeArray<int array>()
      use flusher = new BatchFlusher<int>(maxSize, 0, fun batch -> flushed.Add(batch))
      for item in items.Get do flusher.Add(item)
      flusher.Flush()
      for batch in flushed do
        (batch.Length, maxSize) |> Expect.isLessThanOrEqual "batch within limit"

  testPropertyWithConfig cfg "double flush is idempotent" <|
    fun (items: int array) ->
      let flushed = ResizeArray<int array>()
      use flusher = new BatchFlusher<int>(100, 0, fun batch -> flushed.Add(batch))
      for item in items do flusher.Add(item)
      flusher.Flush()
      let countAfterFirst = flushed |> Seq.sumBy Array.length
      flusher.Flush()
      let countAfterSecond = flushed |> Seq.sumBy Array.length
      countAfterSecond |> Expect.equal "second flush adds nothing" countAfterFirst

  testPropertyWithConfig cfg "empty flush is a no-op" <|
    fun (PositiveInt n) ->
      let mutable callCount = 0
      use flusher = new BatchFlusher<int>(10, 0, fun _ -> callCount <- callCount + 1)
      for _ in 1..n do flusher.Flush()
      callCount |> Expect.equal "onFlush never called for empty buffer" 0

  testPropertyWithConfig cfg "add after dispose is silent" <|
    fun (items: NonEmptyArray<int>) ->
      let flushed = ResizeArray<int array>()
      let flusher = new BatchFlusher<int>(100, 0, fun batch -> flushed.Add(batch))
      (flusher :> System.IDisposable).Dispose()
      let countAfterDispose = flushed |> Seq.sumBy Array.length
      for item in items.Get do flusher.Add(item)
      flusher.Flush()
      let countAfterAdds = flushed |> Seq.sumBy Array.length
      countAfterAdds |> Expect.equal "no new items after dispose" countAfterDispose
]
