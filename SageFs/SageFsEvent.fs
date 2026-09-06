namespace SageFs

open System

/// The kind of output line in the REPL output area
[<RequireQualifiedAccess>]
type OutputKind =
  | Result
  | Error
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
/// Mutable internally — safe because ElmLoop is single-writer (CAS drain).
[<Sealed>]
type OutputRingBuffer(capacity: int) =
  let items = Array.zeroCreate<OutputLine> capacity
  let mutable writeIdx = 0
  let mutable count = 0
  let mutable version = 0
  let mutable cachedRenderVersion = -1
  let mutable cachedRenderContent = ""

  member _.Capacity = capacity
  member _.Count = count
  member _.Length = count
  member _.IsEmpty = count = 0
  /// Monotonically increasing version — increments on every mutation.
  member _.Version = version

  /// Add a single line. Overwrites oldest when full.
  member _.Add(line: OutputLine) =
    items.[writeIdx] <- line
    writeIdx <- (writeIdx + 1) % capacity
    match count < capacity with
    | true -> count <- count + 1
    | false -> ()
    version <- version + 1

  /// Add multiple lines in order. Overwrites oldest when full.
  member rb.AddRange(lines: OutputLine seq) =
    for line in lines do rb.Add(line)

  /// Clear all items.
  member _.Clear() =
    writeIdx <- 0
    count <- 0
    version <- version + 1

  /// Newest-first indexer: .[0] = most recently added (backward-compat with old list).
  member _.Item(index: int) =
    match index >= count with
    | true -> raise (System.IndexOutOfRangeException())
    | false ->
      let i = (writeIdx - 1 - index + capacity) % capacity
      items.[i]

  /// Render filtered output directly into StringBuilder (oldest→newest).
  /// Zero intermediate allocations — the hot path.
  member _.RenderFiltered(sessionId: string, sb: System.Text.StringBuilder) =
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
          | OutputKind.Error -> "error"
          | OutputKind.Info -> "info"
          | OutputKind.System -> "system"
        sb.Append('[').Append(line.Timestamp.ToString("HH:mm:ss")).Append("] [")
          .Append(kindLabel).Append("] ").Append(line.Text) |> ignore
      | false -> ()

  /// Render all output directly into StringBuilder (oldest→newest).
  /// No session filtering — use when buffer is already per-session.
  member _.RenderAll(sb: System.Text.StringBuilder) =
    let start = match count < capacity with | true -> 0 | false -> writeIdx
    for i = 0 to count - 1 do
      let line = items.[(start + i) % capacity]
      match i > 0 with
      | true -> sb.Append('\n') |> ignore
      | false -> ()
      let kindLabel =
        match line.Kind with
        | OutputKind.Result -> "result"
        | OutputKind.Error -> "error"
        | OutputKind.Info -> "info"
        | OutputKind.System -> "system"
      sb.Append('[').Append(line.Timestamp.ToString("HH:mm:ss")).Append("] [")
        .Append(kindLabel).Append("] ").Append(line.Text) |> ignore

  /// Cached render — returns cached string when buffer hasn't changed since last call.
  member this.RenderAllCached() =
    match version = cachedRenderVersion with
    | true -> cachedRenderContent
    | false ->
      let sb = System.Text.StringBuilder(count * 40)
      this.RenderAll(sb)
      let s = sb.ToString()
      cachedRenderVersion <- version
      cachedRenderContent <- s
      s

  /// Check if any line matches a predicate (newest-first search).
  member _.Exists(predicate: OutputLine -> bool) =
    let mutable found = false
    let mutable i = 0
    while not found && i < count do
      let idx = (writeIdx - 1 - i + capacity) % capacity
      match predicate items.[idx] with
      | true -> found <- true
      | false -> ()
      i <- i + 1
    found

  /// Filter to list (newest-first, for backward compat with old code).
  member _.FilterToList(predicate: OutputLine -> bool) =
    [ for i = 0 to count - 1 do
        let idx = (writeIdx - 1 - i + capacity) % capacity
        let line = items.[idx]
        match predicate line with
        | true -> yield line
        | false -> () ]

  /// Create from list (list is newest-first, like old model convention).
  static member ofList (lines: OutputLine list) =
    let rb = OutputRingBuffer(max (List.length lines) 500)
    for line in List.rev lines do rb.Add(line)
    rb

  static member empty with get() = OutputRingBuffer(500)

  interface System.Collections.Generic.IEnumerable<OutputLine> with
    member this.GetEnumerator() =
      let arr = [| for i = 0 to count - 1 do yield this.[i] |]
      (arr :> System.Collections.Generic.IEnumerable<_>).GetEnumerator()

  interface System.Collections.IEnumerable with
    member this.GetEnumerator() =
      (this :> System.Collections.Generic.IEnumerable<_>).GetEnumerator() :> _

/// Per-session output storage. Each session gets its own ring buffer.
/// Global lines (empty SessionId) broadcast to all existing session buffers.
/// Pre-session globals are held in a staging buffer and merged into the first session.
[<Sealed>]
type SessionOutputStore(bufferCapacity: int) =
  let buffers = System.Collections.Generic.Dictionary<string, OutputRingBuffer>()
  let staging = OutputRingBuffer(bufferCapacity)
  let mutable version = 0L

  new() = SessionOutputStore(500)

  member _.GetOrCreate(sessionId: string) =
    match buffers.TryGetValue(sessionId) with
    | true, buf -> buf
    | false, _ ->
      let buf = OutputRingBuffer(bufferCapacity)
      // New session inherits staged globals (oldest-first)
      match staging.Count > 0 with
      | true ->
        let globals = staging |> Seq.toArray |> Array.rev
        for line in globals do buf.Add(line)
      | false -> ()
      buffers.[sessionId] <- buf
      buf

  /// Route a line to the correct session buffer. Global lines broadcast to all.
  member this.Add(line: OutputLine) =
    match System.String.IsNullOrEmpty line.SessionId with
    | true ->
      staging.Add(line)
      for kvp in buffers do kvp.Value.Add(line)
    | false ->
      let buf = this.GetOrCreate(line.SessionId)
      buf.Add(line)
    version <- version + 1L

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
    match buffers.TryGetValue(sessionId) with
    | true, buf ->
      buf.Clear()
      version <- version + 1L
    | false, _ -> ()

  /// Drop a session's ring buffer entirely (session stopped/purged) so the
  /// store does not retain memory per dead session (roast queue item 2).
  member _.Remove(sessionId: string) =
    match buffers.ContainsKey sessionId with
    | true ->
      buffers.Remove(sessionId) |> ignore
      version <- version + 1L
    | false -> ()

  /// Clear all session buffers and staging.
  member _.ClearAll() =
    for kvp in buffers do kvp.Value.Clear()
    staging.Clear()
    version <- version + 1L

  member _.SessionCount = buffers.Count

  /// Monotonic version for the entire output store. Any session mutation
  /// increments it so browser streams pinned to non-global sessions wake up.
  member _.Version = version

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

/// Events that flow through the Elm loop, driving all UI updates.
/// Every state change in SageFs is expressed as one of these events.
[<RequireQualifiedAccess>]
type SageFsEvent =
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
  | TestRunStarted of testIds: Features.LiveTesting.TestId array * sessionId: string option
  | TestResultsBatch of results: Features.LiveTesting.TestRunResult array
  | TestRunCompleted of sessionId: string option
  | LiveTestingEnabled
  | LiveTestingDisabled
  | AffectedTestsComputed of testIds: Features.LiveTesting.TestId array
  | CoverageUpdated of coverage: Features.LiveTesting.CoverageState
  | CoverageBitmapCollected of testIds: Features.LiveTesting.TestId array * bitmap: Features.LiveTesting.CoverageBitmap
  | RunPolicyChanged of category: Features.LiveTesting.TestCategory * policy: Features.LiveTesting.RunPolicy
  | ProvidersDetected of providers: Features.LiveTesting.ProviderDescription list
  | TestCycleTimingRecorded of timing: Features.LiveTesting.TestCycleTiming
  | RunTestsRequested of tests: Features.LiveTesting.TestCase array
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
