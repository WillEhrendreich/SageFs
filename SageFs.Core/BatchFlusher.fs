namespace SageFs

open System
open System.Collections.Concurrent
open System.Threading

/// Time-and-count bounded batch flusher, with an optional byte budget.
/// Flushes when count >= maxBatchSize OR the buffered bytes reach
/// maxBufferBytes OR the timer fires (flushIntervalMs) OR explicit Flush()
/// OR Dispose(). Empty flushes are no-ops (onFlush never called with empty
/// array).
///
/// `maxBufferBytes`/`estimateBytes` are both optional and, if either is
/// omitted, byte-budget enforcement is disabled entirely (the buffer
/// behaves exactly as before this pair was added) — 'T is unconstrained, so
/// there is no way to size an item without a caller-supplied estimator, and
/// no honest generic default exists. A batch item that itself holds
/// unbounded text/bytes (the case that motivates this — see EvalStore's
/// byte budget for the same concern on retained eval history) should pass
/// both.
type BatchFlusher<'T>(maxBatchSize: int, flushIntervalMs: int, onFlush: 'T array -> unit, ?maxBufferBytes: int, ?estimateBytes: 'T -> int) =
  let maxBufferBytes = defaultArg maxBufferBytes Int32.MaxValue
  let estimateBytes = defaultArg estimateBytes (fun _ -> 0)
  // Each queued entry carries its own precomputed byte estimate, so a flush
  // can subtract exactly what it drains from `bufferedBytes` without
  // re-invoking `estimateBytes` (which may not be cheap) a second time.
  let buffer = ConcurrentQueue<'T * int>()
  let flushLock = obj()
  let mutable disposed = 0 // Interlocked: 0=active, 1=disposed
  let mutable bufferedBytes = 0L

  let doFlush () =
    lock flushLock (fun () ->
      let batch = System.Collections.Generic.List<'T>()
      let mutable entry = Unchecked.defaultof<'T * int>
      while buffer.TryDequeue(&entry) do
        let item, bytes = entry
        batch.Add(item)
        Interlocked.Add(&bufferedBytes, int64 -bytes) |> ignore
      match batch.Count > 0 with
      | true -> onFlush (batch.ToArray())
      | false -> ()
    )

  let timer =
    match flushIntervalMs > 0 with
    | true ->
      let t = new Timer(TimerCallback(fun _ -> doFlush()), null, flushIntervalMs, flushIntervalMs)
      Some t
    | false -> None

  member _.Add(item: 'T) =
    match Volatile.Read(&disposed) with
    | 0 ->
      let bytes = estimateBytes item
      buffer.Enqueue((item, bytes))
      let totalBytes = Interlocked.Add(&bufferedBytes, int64 bytes)
      match buffer.Count >= maxBatchSize || totalBytes >= int64 maxBufferBytes with
      | true -> doFlush()
      | false -> ()
    | _ -> ()

  member _.Flush() = doFlush()

  member _.Count = buffer.Count

  /// Approximate bytes currently buffered per `estimateBytes` (0 when no
  /// estimator was supplied). Exposed for tests/telemetry.
  member _.BufferedBytes = Volatile.Read(&bufferedBytes)

  interface IDisposable with
    member _.Dispose() =
      match Interlocked.Exchange(&disposed, 1) with
      | 0 ->
        match timer with
        | Some t -> t.Dispose()
        | None -> ()
        doFlush()
      | _ -> () // already disposed
