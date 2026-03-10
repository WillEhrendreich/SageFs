# SAGEFS ARCHITECTURE - CONCRETE CODE SIGNATURES

## Features Layer - Exact Public APIs

### 1. CellDependencyGraph.fs
`sharp
type CellId = int
type CellInfo = {
  Id: CellId
  Source: string
  Produces: string list
  Consumes: string list
}
type CellGraph = {
  Cells: Map<CellId, CellInfo>
  Edges: (CellId * CellId) list
}

val analyzeCell : Map<string,CellId> → CellId → string → string → CellInfo
val buildGraph : CellInfo list → CellGraph
val transitiveStale : CellGraph → CellId → CellId list
`

### 2. EvalProvenance.fs
`sharp
type Staleness =
  | Fresh
  | StaleUpstream of upstreamCellIds: CellId list

type EvalProvenance = {
  CellId: CellId
  DependsOn: CellId list
  Staleness: Staleness
}

val compute : CellGraph → CellId → Set<CellId> → EvalProvenance
`

### 3. EvalTimeline.fs
`sharp
type TimelineEntry = {
  CellId: CellId
  StartMs: int64
  DurationMs: int64
  Status: EvalStatus
}
type TimelineState = { Entries: TimelineEntry list }
type TimelineStats = {
  P50Ms: float option
  P95Ms: float option
  Sparkline: string
}

val timelineStats : int → TimelineState → TimelineStats
val record : TimelineEntry → TimelineState → TimelineState
`

### 4. EvalDedup.fs
`sharp
type DedupEntry = {
  Hash: int
  Result: string
  Timestamp: DateTimeOffset
}
type DedupCache = {
  Entries: ConcurrentDictionary<string, DedupEntry>
  WindowMs: int
}

val tryGet : DedupCache → string → string → DateTimeOffset → string option
val record : DedupCache → string → string → string → DateTimeOffset → unit
`

### 5. CoverageInstrumenter.fs
`sharp
val collectSequencePoints : ModuleDefinition → (MethodDefinition * SequencePoint * int) array
val injectTracker : ModuleDefinition → int → unit
val instrumentMethod : MethodDefinition → (MethodDefinition * SequencePoint * int) array → unit
`

### 6. LiveTestingTypes.fs
`sharp
type TestId = TestId of string
type TestFramework = Expecto | XUnit | NUnit | MSTest | TUnit | Unknown
type TestResult = Passed | Failed | Skipped | NotRun
type RunPolicy = OnEveryChange | OnSaveOnly | OnDemand | Disabled

type TestCase = {
  Id: TestId
  FullName: string
  DisplayName: string
  Origin: TestOrigin
  Labels: string list
  Framework: TestFramework
  Category: TestCategory
}

type CoverageBitmap = {
  Bits: uint64 array  // Packed bits
  Count: int
}

type LiveTestState = {
  Tests: TestStatusEntry array
  Results: Map<TestId, TestRunResult>
  Coverage: Map<TestId, CoverageBitmap>
  InstrumentationMaps: InstrumentationMap array
  SymbolCoverageIndex: Map<string, CoveringTestInfo array>
  DependencyGraph: TestDependencyGraph
  …
}
`

### 7. Diagnostician.fs (Master Composition)
`sharp
type DiagnosticSeverity = Info | Warning | Critical
type DiagnosedFailure = {
  TestId: TestId
  TestName: string
  Narrative: FailureNarrative
  CausalCells: CellId list
  Staleness: Map<CellId, Staleness>
}
type DiagnosticReport = {
  Failures: DiagnosedFailure list
  AffectedCells: (CellId * Staleness) list
  RipplePlan: RipplePlan option
  SuggestedFixes: Suggestion list
  PerformanceContext: TimelineStats option
  Severity: DiagnosticSeverity
  Summary: string
}

val compose : CellGraph → (TestId*string*FailureNarrative) list
            → ScopeBinding list → TimelineState → DiagnosticReport
val summarize : DiagnosticReport → string
`

### 8. FeatureHooks.fs (Push State)
`sharp
type EvalHistoryEntry = {
  CellIndex: int
  Code: string
  Result: string
  DurationMs: int64
  Timestamp: DateTimeOffset
}
type FeaturePushState = {
  LastOutputText: string
  LastEvalDiffSse: string option
  LastCellDepsSse: string option
  LastBindingScopeSse: string option
  LastEvalTimelineSse: string option
  EvalHistory: EvalHistoryEntry list
  NextCellIndex: int
  KnownBindings: Map<string, int>
  CachedScope: BindingScopeSnapshot option
  CachedTimeline: TimelineState
}

val recordEval : string → string → int64 → FeaturePushState → FeaturePushState
val computeEvalDiffPush : JsonSerializerOptions → string option → string → FeaturePushState
                        → FeaturePushState * string option
val computeCellDepsPush : JsonSerializerOptions → string option → FeaturePushState
                       → FeaturePushState * string option
val computeBindingScopePush : JsonSerializerOptions → string option → FeaturePushState
                            → FeaturePushState * string option
val computeEvalTimelinePush : JsonSerializerOptions → string option → FeaturePushState
                            → FeaturePushState * string option
`

---

## MCP Tool Layer - McpContext + Tool Functions

`sharp
type McpContext = {
  Persistence: EventStore.EventPersistence
  DiagnosticsChanged: IEvent<DiagnosticsStore.T>
  StateChanged: IEvent<string> option
  SessionOps: SessionManagementOps
  SessionMap: ConcurrentDictionary<string, string>
  McpPort: int
  Dispatch: (SageFsMsg → unit) option
  GetElmModel: (unit → SageFsModel) option
  GetElmRegions: (unit → RenderRegion list) option
  GetWarmupContext: (string → Task<WarmupContext option>) option
  GetFeatureState: (unit → FeaturePushState) option
}

// Routing & Resolution
val resolveSessionId : McpContext → string → string option → string option
                    → Task<Result<string, string>>
val routeToSession : McpContext → string
                  → (SessionId → WorkerMessage) → Task<Result<WorkerResponse, string>>

// Tool Implementations
val sendFSharpCode : McpContext → string → string → OutputFormat
                  → string option → string option → string option → string option → int option
                  → Task<string>
val getStatus : McpContext → string → string option → string option → Task<string>
val getStartupInfo : McpContext → string → string option → Task<string>
val getRecentEvents : McpContext → string → int → string option → Task<string>
`

---

## SSE Push Events - Exact Types

`sharp
type PushEvent =
  | DiagnosticsChanged of (string*int*string) list
  | StateChanged of int * int
  | FileReloaded of string
  | SessionFaulted of string
  | WarmupCompleted
  | TestSummaryChanged of TestSummary
  | TestResultsBatch of TestResultsBatchPayload
  | FileAnnotationsUpdated of FileAnnotations
  | DiagnosisReady of DiagnosticReport

type AccumulatedEvent = {
  Timestamp: DateTimeOffset
  Event: PushEvent
}

type EventAccumulator =
  new : unit → EventAccumulator
  member Add : PushEvent → unit
  member Drain : unit → AccumulatedEvent array
  member Count : int
`

---

## Test Patterns - Exact Helpers

`sharp
module DiagnosticianTests =
  let private mkTestId (name: string) : TestId
  let private mkNarrative (causalChanges: CausalChange list) (summary: string)
    : FailureNarrative
  let private mkGraph (cells: CellInfo list) (edges: (CellId*CellId) list)
    : CellGraph
  let private mkCell (id: CellId) (source: string) (produces: string list)
    (consumes: string list) : CellInfo
  
  val compositionTests : Expecto.Tests
`

---

## Data Structures - The 6 Unmapped Compositions

### 1. TestId ↔ CellId
**Missing Type:**
`sharp
type TestSourceMap = {
  TestToCell: Map<TestId, CellId option>
  CellToTests: Map<CellId, TestId list>
}
`

### 2. Flaky Analysis
**Partial - Need Composition:**
`sharp
type FlakyAnalysis = {
  TestId: TestId
  Classification: FlakyClassification
  FailureRoot: FailureNarrative  // WHY is it flaky?
  RecentOutcomes: TestOutcome array
}
`

### 3. Coverage Blind Spots
**Missing Type:**
`sharp
type BlindSpot = {
  SequencePointIds: int array
  File: string
  Lines: int list
  ProbeCount: int
}
`

### 4. Annotation Hierarchy
**Missing Type:**
`sharp
type AnnotationLayer = {
  Priority: int
  Icon: GutterIcon
  Tooltip: string
}
`

### 5. Binding Shadows
**Missing Type:**
`sharp
type ShadowBinding = {
  Name: string
  DefinedInCells: CellId list  // All places it's bound
  Shadows: CellId → CellId list  // For each cell, which it shadows
}
`

### 6. TestResult ↔ DepGraph Impact
**Missing Type:**
`sharp
type ImpactAnalysis = {
  TestId: TestId
  Failure: TestFailure
  CoverageHits: int array  // Bitmap indices
  SuspectedCells: CellId list
  Confidence: float
}
`

---

## Key Architectural Insights

### Incremental State Pattern (FeaturePushState)
- NextCellIndex: monotonic counter that survives capping
- EvalHistory: capped at 10k, prepend-only (O(1))
- KnownBindings: incremental Map.add from FSI "val" lines
- CachedScope: full rebuild each eval (rebuild cost acceptable)
- CachedTimeline: append-only timeline entries

**Property W5(R9):** NextCellIndex never decreases, even when EvalHistory truncated
**Property W1(R9):** EvalHistory capped at MaxEvalHistory to prevent unbounded O(n) growth

### SSE Dedup Pattern
- Replace strategy: remove prior event with same tag before enqueuing new
- Accumulate strategy: append and trim if exceeds max (50 events)
- Drain: convert AccumulatedEvent[] → string[] via formatForLlm
- Broadcast: append events banner to tool response content

### Composition Pattern (Diagnostician as Exemplar)
- Pure function signature: (Input1, Input2, ..., InputN) → Output
- No side effects: compute only
- Feature modules are independent, can be mocked in tests
- Type system enforces composition constraints

### MCP Tool Pattern
- McpContext has session map (agent → sessionId)
- withSession helper: resolve → route → return/error
- NotifyElm: fire Elm events for daemon-mode side effects
- EventTracking: persist input/output to event store
- EvalDedup: skip redundant evals within temporal window
