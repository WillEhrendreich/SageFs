namespace SageFs

open System
open System.Collections.Concurrent
open System.Threading

/// Time-and-count bounded batch flusher.
/// Flushes when count >= maxBatchSize OR timer fires (flushIntervalMs) OR explicit Flush() OR Dispose().
/// Empty flushes are no-ops (onFlush never called with empty array).
type BatchFlusher<'T>(maxBatchSize: int, flushIntervalMs: int, onFlush: 'T array -> unit) =
  let buffer = ConcurrentQueue<'T>()
  let flushLock = obj()
  let mutable disposed = 0 // Interlocked: 0=active, 1=disposed

  let doFlush () =
    lock flushLock (fun () ->
      let batch = System.Collections.Generic.List<'T>()
      let mutable item = Unchecked.defaultof<'T>
      while buffer.TryDequeue(&item) do
        batch.Add(item)
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
      buffer.Enqueue(item)
      match buffer.Count >= maxBatchSize with
      | true -> doFlush()
      | false -> ()
    | _ -> ()

  member _.Flush() = doFlush()

  member _.Count = buffer.Count

  interface IDisposable with
    member _.Dispose() =
      match Interlocked.Exchange(&disposed, 1) with
      | 0 ->
        match timer with
        | Some t -> t.Dispose()
        | None -> ()
        doFlush()
      | _ -> () // already disposed
