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

  // ── roast-6 #8: byte budget alongside the count/time bounds ────────────

  testPropertyWithConfig cfg "buffered bytes never exceed maxBufferBytes after Add returns, when a budget and estimator are supplied" <|
    fun (PositiveInt maxBufferBytes) (itemSizes: NonEmptyArray<PositiveInt>) ->
      let maxBufferBytes = max 1 (min maxBufferBytes 5000)
      let sizes = itemSizes.Get |> Array.map (fun (PositiveInt n) -> min n 200)
      let flushed = ResizeArray<int array>()
      // maxBatchSize deliberately huge so only the byte budget can trigger a
      // flush — isolates the property this test is about.
      use flusher = new BatchFlusher<int>(1_000_000, 0, (fun batch -> flushed.Add(batch)), maxBufferBytes, id)
      for size in sizes do flusher.Add(size)
      (flusher.BufferedBytes <= int64 maxBufferBytes)
      |> Expect.isTrue "after every Add, buffered bytes must never exceed the budget (a flush must have drained it)"

  testPropertyWithConfig cfg "byte-budget flush never loses items, same guarantee as count-based flush" <|
    fun (PositiveInt maxBufferBytes) (itemSizes: NonEmptyArray<PositiveInt>) ->
      let maxBufferBytes = max 1 (min maxBufferBytes 5000)
      let items = itemSizes.Get |> Array.map (fun (PositiveInt n) -> min n 200)
      let flushed = ResizeArray<int array>()
      use flusher = new BatchFlusher<int>(1_000_000, 0, (fun batch -> flushed.Add(batch)), maxBufferBytes, id)
      for item in items do flusher.Add(item)
      flusher.Flush()
      let result = flushed |> Seq.collect id |> Seq.sort |> Seq.toArray
      let expected = items |> Array.sort
      result |> Expect.equal "every item is still delivered exactly once, byte-triggered flushes included" expected

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
