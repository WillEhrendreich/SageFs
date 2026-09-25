namespace SageFs

open System

/// The kind of output line in the REPL output area
[<RequireQualifiedAccess>]
type OutputKind =
  | Result
  | Failure
  | Info
  | System

/// A single line of REPL output
type OutputLine = {
  Kind: OutputKind
  Text: string
  Timestamp: DateTime
  SessionId: string
}

/// Fixed-capacity circular buffer for output lines.
/// O(1) add, cache-friendly iteration, zero GC pressure during steady state.
/// Mutations and reads share one gate so every published buffer remains safe
/// after a reference is handed to readers outside the Elm writer.
[<Sealed>]
type OutputRingBuffer(capacity: int) =
  let items = Array.zeroCreate<OutputLine> capacity
  let gate = obj ()
  let mutable writeIdx = 0
  let mutable count = 0
  let mutable version = 0
  let mutable cachedRenderVersion = -1
  let mutable cachedRenderContent = ""

  member _.Capacity = capacity
  member _.Count = lock gate (fun () -> count)
  member _.Length = lock gate (fun () -> count)
  member _.IsEmpty = lock gate (fun () -> count = 0)
  /// Monotonically increasing version — increments on every mutation.
  member _.Version = lock gate (fun () -> version)

  /// Add a single line. Overwrites oldest when full.
  member _.Add(line: OutputLine) =
    lock gate (fun () ->
      items.[writeIdx] <- line
      writeIdx <- (writeIdx + 1) % capacity
      match count < capacity with
      | true -> count <- count + 1
      | false -> ()
      version <- version + 1)

  /// Add multiple lines in order. Overwrites oldest when full.
  member rb.AddRange(lines: OutputLine seq) =
    for line in lines do rb.Add(line)

  /// Clear all items.
  member _.Clear() =
    lock gate (fun () ->
      writeIdx <- 0
      count <- 0
      version <- version + 1)

  /// Newest-first indexer: .[0] = most recently added (backward-compat with old list).
  member this.Item(index: int) =
    lock gate (fun () ->
      match index >= count with
      | true -> raise (System.IndexOutOfRangeException())
      | false ->
        let i = (writeIdx - 1 - index + capacity) % capacity
        items.[i])

  /// Render filtered output directly into StringBuilder (oldest→newest).
  /// Zero intermediate allocations — the hot path.
  member this.RenderFiltered(sessionId: string, sb: System.Text.StringBuilder) =
    lock gate (fun () ->
      let start = match count < capacity with | true -> 0 | false -> writeIdx
      let mutable first = true
      for i = 0 to count - 1 do
        let line = items.[(start + i) % capacity]
        match String.IsNullOrEmpty line.SessionId || line.SessionId = sessionId with
        | true ->
          match first with
          | true -> first <- false
          | false -> sb.Append('\n') |> ignore
          let kindLabel =
            match line.Kind with
            | OutputKind.Result -> "result"
            | OutputKind.Failure -> "error"
            | OutputKind.Info -> "info"
            | OutputKind.System -> "system"
          sb.Append('[').Append(line.Timestamp.ToString("HH:mm:ss")).Append("] [")
            .Append(kindLabel).Append("] ").Append(line.Text) |> ignore
        | false -> ())

  /// Render all output directly into StringBuilder (oldest→newest).
  /// No session filtering — use when buffer is already per-session.
  member _.RenderAll(sb: System.Text.StringBuilder) =
    lock gate (fun () ->
      let start = match count < capacity with | true -> 0 | false -> writeIdx
      for i = 0 to count - 1 do
        let line = items.[(start + i) % capacity]
        match i > 0 with
        | true -> sb.Append('\n') |> ignore
        | false -> ()
        let kindLabel =
          match line.Kind with
          | OutputKind.Result -> "result"
          | OutputKind.Failure -> "error"
          | OutputKind.Info -> "info"
          | OutputKind.System -> "system"
        sb.Append('[').Append(line.Timestamp.ToString("HH:mm:ss")).Append("] [")
          .Append(kindLabel).Append("] ").Append(line.Text) |> ignore)

  /// Cached render — returns cached string when buffer hasn't changed since last call.
  member this.RenderAllCached() =
    lock gate (fun () ->
      match version = cachedRenderVersion with
      | true -> cachedRenderContent
      | false ->
        let sb = System.Text.StringBuilder(count * 40)
        this.RenderAll(sb)
        let s = sb.ToString()
        cachedRenderVersion <- version
        cachedRenderContent <- s
        s)

  /// Check if any line matches a predicate (newest-first search).
  member _.Exists(predicate: OutputLine -> bool) =
    lock gate (fun () ->
      let mutable found = false
      let mutable i = 0
      while not found && i < count do
        let idx = (writeIdx - 1 - i + capacity) % capacity
        match predicate items.[idx] with
        | true -> found <- true
        | false -> ()
        i <- i + 1
      found)

  /// Filter to list (newest-first, for backward compat with old code).
  member _.FilterToList(predicate: OutputLine -> bool) =
    lock gate (fun () ->
      [ for i = 0 to count - 1 do
          let idx = (writeIdx - 1 - i + capacity) % capacity
          let line = items.[idx]
          match predicate line with
          | true -> yield line
          | false -> () ])

  /// Create from list (list is newest-first, like old model convention).
  static member ofList (lines: OutputLine list) =
    let rb = OutputRingBuffer(max (List.length lines) 500)
    for line in List.rev lines do rb.Add(line)
    rb

  static member empty with get() = OutputRingBuffer(500)

  interface System.Collections.Generic.IEnumerable<OutputLine> with
    member this.GetEnumerator() =
      (this.FilterToList (fun _ -> true) :> System.Collections.Generic.IEnumerable<_>).GetEnumerator()

  interface System.Collections.IEnumerable with
    member this.GetEnumerator() =
      (this :> System.Collections.Generic.IEnumerable<_>).GetEnumerator() :> _

/// Per-session output storage. Each session gets its own ring buffer.
/// Global lines (empty SessionId) broadcast to all existing session buffers.
/// Pre-session globals are held in a staging buffer and merged into the first session.
[<Sealed>]
type SessionOutputStore(bufferCapacity: int) =
  let buffers = System.Collections.Concurrent.ConcurrentDictionary<string, OutputRingBuffer>()
  let gate = obj ()
  let staging = OutputRingBuffer(bufferCapacity)
  let mutable version = 0L

  new() = SessionOutputStore(500)

  member _.GetOrCreate(sessionId: string) =
    lock gate (fun () ->
      match buffers.TryGetValue sessionId with
      | true, buf -> buf
      | false, _ ->
        let buf = OutputRingBuffer(bufferCapacity)
        match staging.Count > 0 with
        | true ->
          let globals = staging |> Seq.toArray |> Array.rev
          for line in globals do buf.Add(line)
        | false -> ()
        buffers.[sessionId] <- buf
        buf)

  /// Route a line to the correct session buffer. Global lines broadcast to all.
  member this.Add(line: OutputLine) =
    lock gate (fun () ->
      match System.String.IsNullOrEmpty line.SessionId with
      | true ->
        staging.Add(line)
        for kvp in buffers do kvp.Value.Add(line)
      | false ->
        let buf = this.GetOrCreate(line.SessionId)
        buf.Add(line)
      version <- version + 1L)

  /// Add multiple lines, routing each to the correct session buffer.
  member this.AddRange(lines: OutputLine seq) =
    for line in lines do this.Add(line)

  /// Get the buffer for a specific session. Returns empty for unknown sessions.
  member _.GetBuffer(sessionId: string) =
    match buffers.TryGetValue(sessionId) with
    | true, buf -> buf
    | false, _ -> OutputRingBuffer.empty

  /// Get the buffer for the active session (resolves ActiveSession DU).
  member this.GetActiveBuffer(active: ActiveSession) =
    match active with
    | ActiveSession.Viewing sid -> this.GetBuffer(WorkerProtocol.SessionId.value sid)
    | ActiveSession.AwaitingSession -> staging

  /// Clear a specific session's buffer.
  member _.Clear(sessionId: string) =
    lock gate (fun () ->
      match buffers.TryGetValue sessionId with
      | true, buf ->
        buf.Clear()
        version <- version + 1L
      | false, _ -> ())

  /// Drop a session's ring buffer entirely (session stopped/purged) so the
  /// store does not retain memory per dead session (roast queue item 2).
  member _.Remove(sessionId: string) =
    lock gate (fun () ->
      let mutable removed = Unchecked.defaultof<OutputRingBuffer>
      if buffers.TryRemove(sessionId, &removed) then
        version <- version + 1L)

  /// Every session id this store currently holds a buffer for — the sweep
  /// (`DaemonMode.sweepStaleSessionState`) reads this to find ids to
  /// `Remove` that no longer exist as a live session. A snapshot, not a
  /// live view: the caller filters it against its own current session set.
  member _.LiveSessionIds : string list = lock gate (fun () -> buffers.Keys |> List.ofSeq)

  /// Clear all session buffers and staging.
  member _.ClearAll() =
    lock gate (fun () ->
      for kvp in buffers do kvp.Value.Clear()
      staging.Clear()
      version <- version + 1L)

  member _.SessionCount = lock gate (fun () -> buffers.Count)

  /// Monotonic version for the entire output store. Any session mutation
  /// increments it so browser streams pinned to non-global sessions wake up.
  member _.Version = lock gate (fun () -> version)

  /// True if the active session's buffer is empty.
  member this.IsEmpty(active: ActiveSession) = this.GetActiveBuffer(active).IsEmpty

  /// Count of lines in the active session's buffer.
  member this.ActiveCount(active: ActiveSession) = this.GetActiveBuffer(active).Count

  /// Monotonic version of the active session's buffer (changes on every Add/Clear).
  member this.ActiveVersion(active: ActiveSession) = this.GetActiveBuffer(active).Version

  /// Create from list of lines (newest-first convention). Routes each to its session.
  static member ofLines (lines: OutputLine list) =
    let store = SessionOutputStore(500)
    for line in List.rev lines do store.Add(line)
    store

  static member empty with get() = SessionOutputStore(500)

/// File change actions for the event system
[<RequireQualifiedAccess>]
type FileWatchAction =
  | Changed
  | Created
  | Deleted
  | Renamed

/// Events that flow through the Elm loop, driving all UI updates for the
/// deprecated TUI frontend. Named `TuiEvent` (not `SageFsEvent`) because
/// `SageFs.Features.Events.SageFsEvent` (Core) is the canonical F# domain
/// event vocabulary — two distinct types sharing one name was a roast-5 §1
/// finding (SageFs.Core/Features/Events.fs:37 vs the pre-rename name here).
/// Every state change in the deprecated TUI is expressed as one of these events.
[<RequireQualifiedAccess>]
type TuiEvent =
  // ── Eval lifecycle ──
  | EvalStarted of sessionId: string * code: string
  | EvalCompleted of sessionId: string * output: string * diagnostics: Features.Diagnostics.Diagnostic list
  | EvalFailed of sessionId: string * error: string
  | EvalCancelled of sessionId: string
  | OutputEmitted of OutputLine
  // ── Session lifecycle ──
  | SessionCreated of SessionSnapshot
  | SessionsRefreshed of SessionSnapshot list
  | SessionStatusChanged of sessionId: string * status: SessionDisplayStatus
  | SessionSwitched of fromId: string option * toId: string
  | SessionStopped of sessionId: string
  | SessionStale of sessionId: string * inactiveDuration: TimeSpan
  // ── File watcher ──
  | FileChanged of path: string * action: FileWatchAction
  | FileReloaded of path: string * duration: TimeSpan * result: Result<string, string>
  // ── Editor state ──
  | CompletionReady of items: CompletionItem list
  | DiagnosticsUpdated of sessionId: string * diagnostics: Features.Diagnostics.Diagnostic list
  // ── Warmup ──
  | WarmupProgress of step: int * total: int * assemblyName: string
  | WarmupCompleted of duration: TimeSpan * failures: string list
  | WarmupContextUpdated of SessionContext
  // ── Live testing ──
  | TestLocationsDetected of sessionId: string * locations: Features.LiveTesting.SourceTestLocation array
  | TestsDiscovered of sessionId: string * tests: Features.LiveTesting.TestCase array
  /// Merges a fresh live-merged discovery report (Brief 3's
  /// `TestDiscoveryMerge.merge` — compiled ∪ FSI-eval'd, dynamic wins by
  /// `TestId`) into `sessionId`'s cycle state EXACTLY as `TestsDiscovered`
  /// does (source mapping, `DiscoveryGeneration` bump, zero-test
  /// completion), but WITHOUT `TestsDiscovered`'s one-time-activation
  /// side effect of auto-running every discovered test. The
  /// eval-then-affected loop (live-testing-asyoutype-plan.md §2/Brief 4)
  /// already knows exactly which tests to re-run (the coverage/graph-
  /// selected set `decideAfterTypeCheck` chose before the eval) and
  /// dispatches `RunTestsRequested` for that set separately — merging
  /// discovery must never ALSO re-run the whole suite on every keystroke.
  | LiveDiscoveryMerged of sessionId: string * tests: Features.LiveTesting.TestCase array
  | TestDiscoveryFailed of sessionId: string * reason: string
  | TestRunStarted of testIds: Features.LiveTesting.TestId array * sessionId: string option
  /// The worker started an explicitly requested run whose generation was
  /// allocated when it was requested (`RequestedRuns.request`). Unlike
  /// `TestRunStarted`, this does NOT bump a new generation: the run keeps the
  /// identity its requester is waiting on.
  | TestRunStartedAt of testIds: Features.LiveTesting.TestId array * sessionId: string * generation: Features.LiveTesting.RunGeneration
  | TestResultsBatch of sessionId: string option * results: Features.LiveTesting.TestRunResult array
  | TestRunCompleted of sessionId: string option
  | LiveTestingEnabled
  | LiveTestingDisabled
  | AffectedTestsComputed of testIds: Features.LiveTesting.TestId array * changedSymbolNames: string list
  | CoverageUpdated of coverage: Features.LiveTesting.CoverageState
  | CoverageBitmapCollected of sessionId: string option * testIds: Features.LiveTesting.TestId array * bitmap: Features.LiveTesting.CoverageBitmap
  | RunPolicyChanged of category: Features.LiveTesting.TestCategory * policy: Features.LiveTesting.RunPolicy
  | ProvidersDetected of providers: Features.LiveTesting.ProviderDescription list
  | TestCycleTimingRecorded of timing: Features.LiveTesting.TestCycleTiming
  /// An explicit run. `requestId`, when given, lets the requester find ITS run
  /// (generation + status) in `LiveTestState.RunRequests` afterwards.
  | RunTestsRequested of targetSession: string option * tests: Features.LiveTesting.TestCase array * requestId: Features.LiveTesting.RunRequestId option
  | AssemblyLoadFailed of errors: Features.LiveTesting.AssemblyLoadError list
  | InstrumentationMapsReady of sessionId: string * maps: Features.LiveTesting.InstrumentationMap array
  | TestSourceLocations of locations: Features.LiveTesting.TestSourceLocation list

/// The complete view state for any SageFs frontend.
/// Pure data — renderers read this to produce UI.
type SageFsView = {
  Buffer: ValidatedBuffer
  CompletionMenu: CompletionMenu option
  ActiveSession: SessionSnapshot
  RecentOutput: OutputLine list
  Diagnostics: Features.Diagnostics.Diagnostic list
  WatchStatus: WatchStatus option
}

/// How many evals have finished in each session: completed, failed or
/// cancelled, keyed by session id. It only goes up, and clearing the output
/// doesn't touch it. The dashboard renders a session's count into the output
/// panel, and the "N new evals" pill is that count minus the one you saw
/// when you scrolled away. Private, so nothing can lower a count.
type EvalTally = private EvalTally of Map<string, int>

module EvalTally =
  let empty = EvalTally Map.empty

  /// One more finished eval in this session. An event with no session (a
  /// daemon-wide failure like a failed create) isn't anybody's eval.
  let record (sessionId: string) (EvalTally counts as tally) =
    match String.IsNullOrEmpty sessionId with
    | true -> tally
    | false ->
      let n = counts |> Map.tryFind sessionId |> Option.defaultValue 0
      EvalTally (counts |> Map.add sessionId (n + 1))

  /// How many evals have finished in this session so far.
  let count (sessionId: string) (EvalTally counts) =
    counts |> Map.tryFind sessionId |> Option.defaultValue 0

  /// Every session that has finished at least one eval, with its count.
  let toList (EvalTally counts) = Map.toList counts
