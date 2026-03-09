module SageFs.Tests.BatchFlusherTests

open Expecto
open Expecto.Flip
open SageFs
open System.Threading

[<Tests>]
let batchFlusherTests = testList "BatchFlusher" [

  test "flushes when count reaches maxBatchSize" {
    let flushed = ResizeArray<int array>()
    use flusher = new BatchFlusher<int>(3, 0, fun batch -> flushed.Add(batch))
    flusher.Add(1)
    flusher.Add(2)
    flushed.Count |> Expect.equal "no flush yet at 2 items" 0
    flusher.Add(3)
    flushed.Count |> Expect.equal "flushed at 3 items" 1
    flushed.[0] |> Expect.equal "batch contains all 3" [|1;2;3|]
  }

  test "explicit Flush drains buffer" {
    let flushed = ResizeArray<int array>()
    use flusher = new BatchFlusher<int>(100, 0, fun batch -> flushed.Add(batch))
    flusher.Add(10)
    flusher.Add(20)
    flusher.Flush()
    flushed.Count |> Expect.equal "flushed once" 1
    flushed.[0] |> Expect.equal "batch has both items" [|10;20|]
  }

  test "empty Flush is a no-op" {
    let mutable callCount = 0
    use flusher = new BatchFlusher<int>(10, 0, fun _ -> callCount <- callCount + 1)
    flusher.Flush()
    callCount |> Expect.equal "onFlush never called for empty" 0
  }

  test "Dispose flushes remaining items" {
    let flushed = ResizeArray<int array>()
    let flusher = new BatchFlusher<int>(100, 0, fun batch -> flushed.Add(batch))
    flusher.Add(42)
    flusher.Add(99)
    (flusher :> System.IDisposable).Dispose()
    flushed.Count |> Expect.equal "disposal flushed" 1
    flushed.[0] |> Expect.equal "got remaining items" [|42;99|]
  }

  test "timer-based flush fires within interval" {
    let flushed = ResizeArray<int array>()
    use flusher = new BatchFlusher<int>(100, 100, fun batch -> flushed.Add(batch))
    flusher.Add(7)
    // Poll up to 3 seconds (CI has ~10× variance vs dev machine under ThreadPool pressure)
    let deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 3L
    while flushed.Count = 0 && System.Diagnostics.Stopwatch.GetTimestamp() < deadline do
      Thread.Sleep(20)
    (flushed.Count, 1) |> Expect.isGreaterThanOrEqual "timer flushed at least once within 3s"
    flushed.[0] |> Expect.sequenceEqual "contains the item" [|7|]
  }

  test "multiple batches accumulate correctly" {
    let flushed = ResizeArray<int array>()
    use flusher = new BatchFlusher<int>(2, 0, fun batch -> flushed.Add(batch))
    for i in 1..6 do flusher.Add(i)
    flushed.Count |> Expect.equal "3 batches of 2" 3
    let allItems = flushed |> Seq.collect id |> Seq.toArray
    allItems |> Expect.equal "all items present in order" [|1;2;3;4;5;6|]
  }

  test "Add after Dispose is silently ignored" {
    let mutable callCount = 0
    let flusher = new BatchFlusher<int>(2, 0, fun _ -> callCount <- callCount + 1)
    (flusher :> System.IDisposable).Dispose()
    let beforeCount = callCount
    flusher.Add(1)
    flusher.Add(2)
    flusher.Add(3)
    callCount |> Expect.equal "no additional flushes after dispose" beforeCount
  }
]
