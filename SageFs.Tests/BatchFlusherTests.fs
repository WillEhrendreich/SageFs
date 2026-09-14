module SageFs.Tests.BatchFlusherTests

open Expecto
open Expecto.Flip
open SageFs
open System.Threading

[<Tests>]
let batchFlusherTests = testList "BatchFlusher never loses items and respects capacity" [

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

  // ── roast-6 #8: byte budget alongside the count/time bounds ────────────

  test "flushes when buffered bytes reach maxBufferBytes, even under maxBatchSize" {
    let flushed = ResizeArray<string array>()
    // maxBatchSize=100 (never reached); maxBufferBytes=25 with each item
    // estimated at 10 bytes, so the 3rd Add (30 bytes) must trigger a flush.
    use flusher = new BatchFlusher<string>(100, 0, (fun batch -> flushed.Add(batch)), 25, (fun (_: string) -> 10))
    flusher.Add("a")
    flusher.Add("b")
    flushed.Count |> Expect.equal "no flush yet at 20 buffered bytes" 0
    flusher.Add("c")
    flushed.Count |> Expect.equal "flushed once 30 bytes >= the 25-byte budget" 1
    flushed.[0] |> Expect.equal "batch has all 3 items" [|"a";"b";"c"|]
  }

  test "BufferedBytes tracks additions and resets after a flush" {
    use flusher = new BatchFlusher<string>(100, 0, ignore, 1000, (fun (s: string) -> s.Length))
    flusher.Add("abc")
    flusher.Add("de")
    flusher.BufferedBytes |> Expect.equal "3 + 2 buffered bytes" 5L
    flusher.Flush()
    flusher.BufferedBytes |> Expect.equal "flush drains the byte counter back to zero" 0L
  }

  test "omitting maxBufferBytes/estimateBytes disables byte-budget enforcement entirely (backward compatible)" {
    let flushed = ResizeArray<string array>()
    use flusher = new BatchFlusher<string>(100, 0, fun batch -> flushed.Add(batch))
    // A single huge item — if a byte budget were silently active with some
    // default estimator, this alone could trigger a flush. It must not: the
    // 3-arg constructor's behavior is byte-oblivious, exactly as before.
    flusher.Add(String.replicate 1_000_000 "x")
    flushed.Count |> Expect.equal "no byte budget applies without an explicit maxBufferBytes/estimateBytes" 0
    flusher.BufferedBytes |> Expect.equal "with no estimator, buffered bytes is always reported as 0" 0L
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
