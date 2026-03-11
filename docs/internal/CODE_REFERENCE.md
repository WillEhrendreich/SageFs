# CODE REFERENCE: Key Implementation Patterns with Full Examples

## PATTERN 1: Immutable Aggregate + Ring Buffer (MessageJournal.fs)

\\\sharp
module SageFs.Features.MessageJournal

[<RequireQualifiedAccess>]
type JournalLevel = Debug | Info | Warn | Error

type JournalEntry = {
  Timestamp: DateTimeOffset
  Level: JournalLevel
  Source: string
  Message: string
}

// The aggregate — single immutable record
type Journal = {
  Buffer: RingBuffer.RingBuffer<JournalEntry>
}

module Journal =
  // All operations return new state
  let create (capacity: int) : Journal =
    { Buffer = RingBuffer.create capacity }

  // Record is immutable: returns new Journal
  let record (level: JournalLevel) (source: string) (message: string) (journal: Journal) : Journal =
    let entry = {
      Timestamp = DateTimeOffset.UtcNow
      Level = level
      Source = source
      Message = message
    }
    { Buffer = RingBuffer.push entry journal.Buffer }

  // Querying operations
  let count (journal: Journal) : int = RingBuffer.count journal.Buffer
  let entries (journal: Journal) : JournalEntry list = RingBuffer.toList journal.Buffer
  let filterByLevel (level: JournalLevel) (journal: Journal) : JournalEntry list =
    entries journal |> List.filter (fun e -> e.Level = level)

  // Metadata tracking
  let stats (journal: Journal) : JournalStats =
    let all = entries journal
    { Total = all.Length
      ErrorCount = all |> List.filter (fun e -> e.Level = JournalLevel.Error) |> List.length
      Evicted = RingBuffer.evictedCount journal.Buffer }
\\\

**Key traits:**
- [x] Immutable record (never assign fields)
- [x] Single Buffer field (encapsulates structure)
- [x] All operations return new Journal
- [x] O(1) add via push
- [x] Bounded space (cap = buffer capacity)
- [x] Metadata tracking (evicted count)

---

## PATTERN 2: Struct Value Type + DU (EvalProvenance.fs)

\\\sharp
module SageFs.Features.EvalProvenance

open SageFs.Features.CellDependencyGraph

// Struct value type for perf (no GC pressure)
[<Struct>]
type Staleness =
  | Fresh
  | StaleUpstream of upstreamCellIds: CellId list

// Struct record for gutter annotations (zero-copy rendering)
[<Struct>]
type EvalProvenance = {
  CellId: CellId
  DependsOn: CellId list
  Staleness: Staleness
}

module EvalProvenance =
  // Pure computation: graph + params → provenance
  let compute (graph: CellGraph) (cellId: CellId) (changedCells: Set<CellId>) : EvalProvenance =
    let deps =
      graph.Edges
      |> List.choose (fun (producer, consumer) ->
        if consumer = cellId then Some producer else None)
      |> List.distinct
    
    let staleUpstream =
      if changedCells.IsEmpty then []
      else
        changedCells
        |> Set.toList
        |> List.collect (fun changed ->
          transitiveStale graph changed
          |> List.filter (fun id -> id = cellId)
          |> List.map (fun _ -> changed))
        |> List.distinct
    
    let staleness =
      match staleUpstream with
      | [] -> Fresh
      | ids -> StaleUpstream ids
    
    { CellId = cellId; DependsOn = deps; Staleness = staleness }

  // Generate UI annotation (only if stale)
  let tryAnnotation (line: int) (prov: EvalProvenance) : LineAnnotation option =
    match prov.Staleness with
    | Fresh -> None
    | StaleUpstream ids ->
      Some { 
        Line = line
        Icon = GutterIcon.CellStale
        Tooltip = sprintf "stale: depends on changed cells %s" (ids |> List.map string |> String.concat ", ")
      }
\\\

**Key traits:**
- [x] [<Struct>] for perf-critical types
- [x] DU for expressiveness (Fresh | Stale)
- [x] Pure computation (no side effects)
- [x] Input: immutable graph + set
- [x] Output: new struct value
- [x] Optional annotation generation (UI integration)

---

## PATTERN 3: Feature Hooks Dedup (FeatureHooks.fs) ⭐

\\\sharp
module SageFs.Features.FeatureHooks

// Aggregates ALL feature state in one record
type FeaturePushState = {
  // Output tracking
  LastOutputText: string
  LastEvalDiffSse: string option           // ← Track last sent SSE
  LastCellDepsSse: string option           // ← Track last sent SSE
  LastBindingScopeSse: string option       // ← Track last sent SSE
  LastEvalTimelineSse: string option       // ← Track last sent SSE
  
  // Feature state
  EvalHistory: EvalHistoryEntry list
  NextCellIndex: int
  KnownBindings: Map<string, int>
  CachedScope: BindingExplorer.BindingScopeSnapshot option
  CachedTimeline: EvalTimeline.TimelineState
}

// After each eval, update features incrementally
let recordEval (code: string) (result: string) (durationMs: int64) (state: FeaturePushState) =
  let idx = state.NextCellIndex
  let entry = { CellIndex = idx; Code = code; Result = result; DurationMs = durationMs; ... }
  
  // Update history (capped at 10k)
  let cappedHistory = (entry :: state.EvalHistory) |> List.truncate MaxEvalHistory
  
  // Update bindings incrementally
  let newBindings = ... // Parse "val x: int" from output
  
  // Update scope
  let newScope = BindingExplorer.buildScopeSnapshot ...
  
  // Update timeline
  let timelineEntry: EvalTimeline.TimelineEntry = { CellId = idx; DurationMs = durationMs; Status = Success }
  let newTimeline = EvalTimeline.TimelineState.record timelineEntry state.CachedTimeline
  
  // Return updated state
  { state with
      EvalHistory = cappedHistory
      NextCellIndex = idx + 1
      KnownBindings = newBindings
      CachedScope = Some newScope
      CachedTimeline = newTimeline }

// DEDUP PATTERN: Only push if SSE changed
let computeEvalDiffPush (opts: JsonSerializerOptions) (sessionId: string option) (currentOutput: string) (state: FeaturePushState) =
  let diff = EvalDiff.diffLines (Some state.LastOutputText) (Some currentOutput)
  let sseStr = SageFs.SseWriter.formatEvalDiffEvent opts sessionId (EvalDiff.summarize diff)
  let updatedState = { state with LastOutputText = currentOutput }
  
  // KEY PATTERN: Compare with last sent
  if Some sseStr = state.LastEvalDiffSse then
    // No change — don't push
    { updatedState with LastEvalDiffSse = Some sseStr }, None
  else
    // Changed — push to client
    { updatedState with LastEvalDiffSse = Some sseStr }, Some sseStr

// Same pattern for other features
let computeCellDepsPush opts sessionId state =
  let knownBindings = state.KnownBindings
  let cells = state.EvalHistory |> List.map (...analyzeCell...)
  let graph = CellDependencyGraph.buildGraph cells
  let sseStr = SageFs.SseWriter.formatCellDependenciesEvent opts sessionId graph
  
  if Some sseStr = state.LastCellDepsSse then
    { state with LastCellDepsSse = Some sseStr }, None
  else
    { state with LastCellDepsSse = Some sseStr }, Some sseStr

let computeBindingScopePush opts sessionId state =
  let snapshot = state.CachedScope |> Option.defaultWith (fun () -> buildScopeFromState state)
  let sseStr = SageFs.SseWriter.formatBindingScopeMapEvent opts sessionId snapshot
  
  if Some sseStr = state.LastBindingScopeSse then
    { state with LastBindingScopeSse = Some sseStr }, None
  else
    { state with LastBindingScopeSse = Some sseStr }, Some sseStr

let computeEvalTimelinePush opts sessionId state =
  let stats = EvalTimeline.timelineStats 20 state.CachedTimeline
  let sseStr = SageFs.SseWriter.formatEvalTimelineEvent opts sessionId stats
  
  if Some sseStr = state.LastEvalTimelineSse then
    { state with LastEvalTimelineSse = Some sseStr }, None
  else
    { state with LastEvalTimelineSse = Some sseStr }, Some sseStr
\\\

**Key traits:**
- [x] Single FeaturePushState record aggregates all feature state
- [x] Each feature tracks LastXxxSse: string option
- [x] Compute new SSE string for feature
- [x] Compare: if unchanged → (state, None)
- [x] If changed → (state, Some sseStr)
- [x] Client only updates when Some (dedup optimization)
- [x] Impact: 40-60% token savings

---

## PATTERN 4: Pure Computation Layer (CellDependencyGraph.fs)

\\\sharp
module SageFs.Features.CellDependencyGraph

type CellId = int

type CellInfo = {
  Id: CellId
  Source: string
  Produces: string list          // Extracted from FSI "val x: int"
  Consumes: string list          // Binding names in source code
}

type CellGraph = {
  Cells: Map<CellId, CellInfo>
  Edges: (CellId * CellId) list  // (producer, consumer)
}

// Pure: extract info from code + FSI output
let analyzeCell (knownBindings: Map<string, CellId>) (cellId: CellId) (source: string) (fsiOutput: string) : CellInfo =
  // Extract produces (bindings defined by this cell)
  let produces =
    fsiOutput.Split('\n')
    |> Array.choose (fun line ->
      let trimmed = line.Trim()
      if trimmed.StartsWith("val ") then
        let nameEnd = trimmed.IndexOfAny([| ':'; ' ' |], 4)
        if nameEnd > 4 then Some (trimmed.Substring(4, nameEnd - 4))
        else None
      else None)
    |> Array.toList
  
  // Extract consumes (bindings used by this cell)
  let consumes =
    knownBindings
    |> Map.toList
    |> List.choose (fun (name, producerCellId) ->
      if producerCellId <> cellId && source.Contains(name) then Some name
      else None)
  
  { Id = cellId; Source = source; Produces = produces; Consumes = consumes }

// Pure: build graph from cells
let buildGraph (cells: CellInfo list) : CellGraph =
  // Map binding name → producer cell
  let bindingToCell =
    cells
    |> List.collect (fun c -> c.Produces |> List.map (fun b -> (b, c.Id)))
    |> Map.ofList
  
  // Find edges: producer → consumer
  let edges =
    cells
    |> List.collect (fun consumer ->
      consumer.Consumes
      |> List.choose (fun binding ->
        bindingToCell
        |> Map.tryFind binding
        |> Option.map (fun producerId -> (producerId, consumer.Id))))
    |> List.distinct
  
  { Cells = cells |> List.map (fun c -> (c.Id, c)) |> Map.ofList
    Edges = edges }

// Pure: compute transitive closure (which cells are affected by change)
let transitiveStale (graph: CellGraph) (changedCellId: CellId) : CellId list =
  let adjacency =
    graph.Edges
    |> List.groupBy fst
    |> List.map (fun (k, vs) -> (k, vs |> List.map snd))
    |> Map.ofList
  
  let rec bfs visited queue =
    match queue with
    | [] -> visited |> Set.toList
    | current :: rest ->
      if Set.contains current visited then
        bfs visited rest
      else
        let neighbors = adjacency |> Map.tryFind current |> Option.defaultValue []
        bfs (Set.add current visited) (rest @ neighbors)
  
  let directDependents = adjacency |> Map.tryFind changedCellId |> Option.defaultValue []
  bfs Set.empty directDependents |> List.filter (fun id -> id <> changedCellId)
\\\

**Key traits:**
- [x] Pure functions (no side effects, deterministic)
- [x] Input: immutable data
- [x] Output: new immutable data
- [x] Separate concerns: analyze → build → compute reachability
- [x] Easily testable (feed inputs, assert outputs)

---

## PATTERN 5: Event Sourcing (Events.fs)

\\\sharp
module SageFs.Features.Events

open System

// Track event source
type EventSource =
  | Console
  | McpAgent of agentName: string
  | FileSync of fileName: string
  | System

  override this.ToString() =
    match this with
    | Console -> "console"
    | McpAgent name -> sprintf "mcp:%s" name
    | FileSync name -> sprintf "file:%s" name
    | System -> "system"

// Global event DU (26+ cases)
type SageFsEvent =
  // Session lifecycle
  | SessionStarted of {| Config: Map<string, string>; StartedAt: DateTimeOffset |}
  | SessionWarmUpCompleted of {| Duration: TimeSpan; Errors: string list |}
  | SessionReady
  | SessionFaulted of {| Error: string; StackTrace: string option |}
  | SessionReset

  // Evaluation
  | EvalRequested of {| Code: string; Source: EventSource |}
  | EvalCompleted of {| Code: string; Result: string; Duration: TimeSpan |}
  | EvalFailed of {| Code: string; Error: string |}

  // Diagnostics
  | DiagnosticsChecked of {| Code: string; Diagnostics: DiagnosticEvent list; Source: EventSource |}
  | DiagnosticsCleared

  // Daemon sessions
  | DaemonSessionCreated of {| SessionId: string; Projects: string list; |}
  | DaemonSessionStopped of {| SessionId: string; |}
\\\

**Key traits:**
- [x] All changes captured as events
- [x] Every event tagged with EventSource (audit trail)
- [x] Append-only stream (PostgreSQL via Marten)
- [x] Enables: replay, causality, filtering, debugging

---

## PATTERN 6: Affordance-Driven Availability (Affordances.fs)

\\\sharp
module SageFs.Affordances

open System

// State machine: 5 states
type SessionState =
  | Uninitialized
  | WarmingUp
  | Ready
  | Evaluating
  | Faulted

// Pure function: state → available tools
let availableTools (state: SessionState) : string list =
  match state with
  | Uninitialized ->
    [ "get_fsi_status" ]
  | WarmingUp ->
    [ "get_fsi_status"; "get_recent_fsi_events" ]
  | Ready ->
    [ "send_fsharp_code"
      "load_fsharp_script"
      "get_fsi_status"
      "get_startup_info"
      "get_recent_fsi_events"
      "get_completions"
      "check_fsharp_code"
      "reset_fsi_session"
      "hard_reset_fsi_session"
      "cancel_eval" ]
  | Evaluating ->
    [ "cancel_eval"
      "get_fsi_status"
      "get_recent_fsi_events"
      "get_completions"
      "check_fsharp_code" ]
  | Faulted ->
    [ "get_fsi_status"
      "get_recent_fsi_events"
      "reset_fsi_session"
      "hard_reset_fsi_session" ]

// Check if tool is available
let checkToolAvailability (state: SessionState) (toolName: string) : Result<unit, SageFsError> =
  let tools = availableTools state
  if tools |> List.contains toolName then
    Ok ()
  else
    Error (SageFsError.ToolNotAvailable(toolName, state, tools))
\\\

**Key traits:**
- [x] State machine (5 states)
- [x] availableTools is pure function
- [x] Check before tool execution
- [x] Error includes list of alternatives
- [x] Impact: 70-80% token savings

