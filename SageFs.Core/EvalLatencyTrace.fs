namespace SageFs

open System
open System.Diagnostics
open System.Collections.Generic

/// The eval-to-pixel latency chain (multiagent vision §3.4, §7.4; roast-6
/// Phase 0 item 1): a Stopwatch-timestamp per hop from the moment an eval is
/// requested to the moment its effect is written to the SSE wire as a morph.
///
/// SINGLE-SLOT DESIGN, DELIBERATELY APPROXIMATE: SageFs today runs one active
/// eval pipeline feeding one Elm model feeding one dashboard render loop, so
/// "the most recently Requested eval that has not yet completed its chain"
/// correctly identifies which eval a given ModelChanged/push/morph carries.
/// This is a Phase 0 MEASUREMENT tool (p50/p99 in the statusline), not a
/// per-eval audit trail: concurrent evals across multiple sessions can
/// attribute one eval's pixel-latency to a different eval's request. Phase 1's
/// frame `Version`/ledger `Seq` (vision §7) replaces this with an exact id
/// when cohorts make multi-eval concurrency real.
///
/// PROCESS BOUNDARY (found measuring against a live throwaway daemon, not
/// mentioned in the vision doc's own citations): `AppState`'s eval actor —
/// where `StampRequested`/`StampFinished` fire — runs inside the FSI WORKER
/// subprocess (`SessionManager` spawns `SageFs.Host.exe`), while
/// `DaemonMode`'s `ModelChanged` trigger and `Dashboard`'s push agent/morph
/// write run inside the DAEMON process. `shared` is one instance PER
/// PROCESS, so the worker's Requested/Finished stamps and the daemon's
/// ModelChanged/PushReceived/MorphWritten stamps are, today, two disjoint
/// trackers — never the same in-flight `Sample`. `StampModelChanged` (below)
/// starts a fresh chain when none is in flight for exactly this reason: the
/// daemon-process ring measures "ModelChanged -> pixel" (the dashboard's own
/// responsiveness — what this Phase 0 item actually fixes), and the
/// worker-process ring (not read by the dashboard) measures the FSI
/// pipeline's own Requested -> Finished span. Bridging the two into one
/// true end-to-end number needs the timestamp carried over the wire in
/// `WorkerResponse` — a Phase 1+ change, out of this item's scope.
module EvalLatencyTrace =

  /// One stage in the eval-to-pixel chain, in chain order.
  [<RequireQualifiedAccess>]
  type Stage =
    | Requested
    | Finished
    | ModelChanged
    | PushReceived
    | MorphWritten

  /// One eval's stamps. Immutable — each stage produces a new value.
  type Sample = {
    EvalId: Guid
    RequestedAt: int64
    FinishedAt: int64 option
    ModelChangedAt: int64 option
    PushReceivedAt: int64 option
    MorphWrittenAt: int64 option
  }

  module Sample =
    let elapsedMs (fromTicks: int64) (toTicks: int64) : float =
      (float (toTicks - fromTicks)) * 1000.0 / float Stopwatch.Frequency

    /// Total eval-to-pixel latency in ms — only meaningful once the chain
    /// reached the morph-write stage.
    let totalMs (s: Sample) : float option =
      s.MorphWrittenAt |> Option.map (elapsedMs s.RequestedAt)

  /// Thread-safe in-flight chain + a bounded ring of completed chains.
  /// One instance is process-wide (`shared`, below) — the daemon hosts one
  /// Elm loop and one render pipeline per process.
  type Tracker(capacity: int) =
    let gate = obj ()
    let mutable current : Sample option = None
    let ring = Queue<Sample>()

    let now () = Stopwatch.GetTimestamp()

    let enqueue (s: Sample) =
      ring.Enqueue s
      while ring.Count > capacity do ring.Dequeue() |> ignore

    let complete (updated: Sample) =
      match updated.MorphWrittenAt with
      | Some _ ->
        enqueue updated
        current <- None
      | None ->
        current <- Some updated

    /// Start a new in-flight chain — a fresh eval was just requested.
    /// Returns the id (discardable; see the module doc for why callers don't
    /// need to thread it through the Elm/SSE pipeline).
    member _.StampRequested() : Guid =
      let id = Guid.NewGuid()
      lock gate (fun () ->
        current <- Some {
          EvalId = id
          RequestedAt = now ()
          FinishedAt = None
          ModelChangedAt = None
          PushReceivedAt = None
          MorphWrittenAt = None
        })
      id

    member private _.StampStage (setter: Sample -> int64 -> Sample) =
      lock gate (fun () ->
        match current with
        | Some sample -> complete (setter sample (now ()))
        | None -> ())

    member this.StampFinished() = this.StampStage (fun s t -> { s with FinishedAt = Some t })

    /// Stamp the ModelChanged stage. IMPORTANT — process boundary: the
    /// daemon and the FSI worker are SEPARATE OS PROCESSES (SessionManager
    /// spawns the worker as a subprocess; AppState's eval actor — and
    /// StampRequested/StampFinished — run inside THAT process's own
    /// `EvalLatencyTrace.shared`, never the daemon's). A chain that began
    /// with StampRequested in the worker can therefore never be observed by
    /// the daemon's tracker. So in the daemon process, ModelChanged IS the
    /// entry point of the chain the statusline measures — "ModelChanged ->
    /// pixel", the dashboard's own responsiveness (exactly the part this
    /// Phase 0 item fixes: the fixed 100ms coalesce sleep and the 1s poll
    /// fallback) — honestly, without fabricating a cross-process number.
    ///
    /// It therefore ALWAYS starts a fresh sample, discarding whatever was
    /// in flight — the same "single-slot, newest wins" policy
    /// StampRequested uses. This is not optional: `ModelChanged` fires on
    /// every daemon-internal state change (progress ticks, the 10s
    /// ListSessions poll, etc.), most of which have no SSE client connected
    /// to ever reach PushReceived/MorphWritten. Merging a later
    /// ModelChangedAt into an OLD un-completed sample (rather than
    /// restarting) was tried and measured wrong: against a live throwaway
    /// daemon it produced a fabricated "p50 51488.6ms" — the gap since a
    /// stale ModelChanged from long before any client connected, not a
    /// real render latency.
    member _.StampModelChanged() =
      let t = now ()
      lock gate (fun () ->
        current <- Some {
          EvalId = Guid.NewGuid()
          RequestedAt = t
          FinishedAt = None
          ModelChangedAt = Some t
          PushReceivedAt = None
          MorphWrittenAt = None
        })

    member this.StampPushReceived() = this.StampStage (fun s t -> { s with PushReceivedAt = Some t })
    member this.StampMorphWritten() = this.StampStage (fun s t -> { s with MorphWrittenAt = Some t })

    /// Snapshot of completed chains, oldest first. Safe to call concurrently
    /// with stamping.
    member _.Snapshot() : Sample list =
      lock gate (fun () -> ring |> List.ofSeq)

    /// Test/reset seam — clears the ring and any in-flight sample.
    member _.Reset() =
      lock gate (fun () ->
        current <- None
        ring.Clear())

  /// Default ring capacity per vision §3.4 ("ring of the last 256").
  let [<Literal>] DefaultCapacity = 256

  /// One process-wide tracker.
  let shared = Tracker(DefaultCapacity)

  /// p-th percentile (0.0-1.0) of a sorted, non-empty list, nearest-rank.
  let private percentile (p: float) (sorted: float[]) : float =
    let idx = max 0 (min (sorted.Length - 1) (int (ceil (p * float sorted.Length)) - 1))
    sorted.[idx]

  /// (p50, p99) of total eval-to-pixel latency in ms over the given samples.
  /// None when no sample has completed the whole chain yet.
  let percentiles (samples: Sample list) : float option * float option =
    let totals =
      samples
      |> List.choose Sample.totalMs
      |> List.toArray
      |> Array.sort
    match totals.Length with
    | 0 -> None, None
    | _ -> Some (percentile 0.50 totals), Some (percentile 0.99 totals)
