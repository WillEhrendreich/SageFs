# SageFs Architecture Deep Dive - Phase 3 Analysis

## 1. FEATURE MODULE STRUCTURE

### Public Module Organization (SageFs.Core/Features/)

**Core Composition Features:**
- Diagnostician.fs - Master composition (5 features → DiagnosticReport)
- FeatureHooks.fs - FeaturePushState incremental state machine

**Evaluation Features:**
- EvalProvenance.fs - Staleness tracking (Fresh | StaleUpstream ids)
- EvalDedup.fs - Temporal dedup cache (2s window per session)
- EvalTimeline.fs - Performance timeline with P95 spike detection
- EvalDiff.fs - Line-based diff computation for output changes
- EvalRipple.fs - Transitive dependency graph ripple plans

**Cell-Level Analysis:**
- CellDependencyGraph.fs - Pure dependency graph from FSI
  * CellId = int (notebook cell index)
  * CellInfo = {Id, Source, Produces: symbols, Consumes: symbols}
  * CellGraph = {Cells: Map<CellId,CellInfo>, Edges: (CellId*CellId) list}

**Live Testing & Coverage:**
- LiveTestingTypes.fs - Core test state types
  * TestId = 16-hex SHA256(fullName|framework)
  * CoverageState = {Slots: SequencePoint[], Hits: bool[]}
  * CoverageBitmap = uint64[] for 8x memory vs bool[]
- CoverageInstrumenter.fs - Cecil IL probe injection
  * Injects __SageFsCoverage class with bool[] Hits
  * Hit(slotId) calls at every non-hidden sequence point

**Supporting Features:**
- Diagnostics.fs - FCS integration with symbol extraction
- AutoCompletion.fs - Fuzzy completion
- BindingExplorer.fs - Scope snapshot from FSI outputs
- Replay.fs - Pure event fold for SessionReplayState
- Ghostwriter.fs - Suggestion generation

---

## 2. DIAGNOSTICIAN COMPOSITION PATTERN

**Module:** SageFs.Core/Features/Diagnostician.fs

**Pure Composition Function:**
`sharp
let compose
  (graph: CellGraph)
  (testFailures: (TestId * string * FailureNarrative) list)
  (scope: ScopeBinding list)
  (timeline: TimelineState)
  : DiagnosticReport
`

**Joins 5 Feature Outputs:**
1. CellDependencyGraph.transitiveStale + EvalProvenance.compute
2. EvalTimeline.timelineStats (P50/P95 detection)
3. EvalRipple.planRipple
4. Ghostwriter.suggest
5. Custom summarization

**Output:**
`sharp
type DiagnosticReport = {
  Failures: DiagnosedFailure list
  AffectedCells: (CellId * Staleness) list
  RipplePlan: RipplePlan option
  SuggestedFixes: Suggestion list
  PerformanceContext: TimelineStats option
  Severity: DiagnosticSeverity    // Info | Warning | Critical
  Summary: string                 // ≤10 lines
}
`

**Key Pattern: Symbol Resolution**
- Input: FailureNarrative.CausalChanges (symbolic names)
- Lookup: graph.Cells[*] find producers of those symbols
- Output: Map of CellId → Staleness for each causal cell

---

## 3. MCP TOOL WIRING (2 EXAMPLES)

### Example 1: sendFSharpCode Tool

**Type Signature in McpContext:**
`sharp
let sendFSharpCode
  (ctx: McpContext) (agentName: string) (code: string) (format: OutputFormat)
  (sessionId: string option) (workingDirectory: string option)
  (filePath: string option) (evalMode: string option) (blockStartLine: int option)
  : Task<string>
`

**Wiring Steps:**
1. Resolve session ID: agent string → cached session ID
2. Check EvalDedup.DedupCache for recent identical code
3. Load CompilationContext (per-session module cache)
4. Parse file structure via parseFileStructureCached
5. Preprocess via Middleware.CompilationContext.preprocessForFsi
6. Route WorkerProtocol.EvalCode message to worker session
7. Dispatch Elm events (EvalStarted, EvalCompleted/Failed)
8. Return formatted result (Text or JSON)

**Composition with Features:**
- EvalDedup prevents 2nd eval if code hash matches within 2s
- CompilationContext preserves open modules across evals
- WorkerProtocol.EvalCode returns WorkerResponse with coverage metadata

### Example 2: getStatus Tool

**Type Signature:**
`sharp
let getStatus (ctx: McpContext) (agent: string)
  (sessionId: string option) (workingDirectory: string option) : Task<string>
`

**Wiring:**
1. Resolve session ID
2. Fetch event count from EventTracking
3. Route GetStatus message to worker
4. Get SessionInfo from SessionOps
5. Format with McpAdapter.formatProxyStatus

### Routing Pattern

All tools use:
`sharp
let routeToSession ctx sessionId msg : Task<Result<WorkerResponse, string>> =
  task {
    let! proxy = ctx.SessionOps.GetProxy(toSessionId sessionId)
    match proxy with
    | None → return Error "Session not found"
    | Some send →
      let replyId = WorkerProtocol.SessionId.newId()
      let! response = send (msg replyId) |> Async.StartAsTask
      return Ok response
  }
`

**McpContext has:**
- SessionMap: agent name → session ID (per-client mapping)
- SessionOps: session lifecycle management
- GetFeatureState: read FeaturePushState for diagnostics
- Dispatch: fire Elm events for daemon mode

---

## 4. SSE EVENT WIRING

**Location:** SageFs.Core/McpPushNotifications.fs

**PushEvent Union:**
`sharp
type PushEvent =
  | DiagnosticsChanged of errors: (string*int*string) list
  | StateChanged of outputCount: int * diagCount: int
  | FileReloaded of path: string
  | SessionFaulted of error: string
  | WarmupCompleted
  | TestSummaryChanged of summary: TestSummary
  | TestResultsBatch of payload: TestResultsBatchPayload
  | FileAnnotationsUpdated of annotations: FileAnnotations
  | DiagnosisReady of report: Diagnostician.DiagnosticReport
`

**Merge Strategy:**
`sharp
type MergeStrategy = Replace | Accumulate

let mergeStrategy = function
  | DiagnosisReady _ → Replace        // Latest diagnosis only
  | FileReloaded _ → Accumulate       // Collect all reloads
  | TestSummaryChanged _ → Replace    // Latest summary
`

**EventAccumulator (Thread-Safe):**
- ConcurrentQueue for Replace events (deduplicated by tag)
- Replace strategy: remove prior event with same type tag, add new
- Accumulate strategy: append to queue (max 50 items)
- Drain on tool response: converts events to LLM-readable format

**DiagnosisReady Push Format:**
`sharp
| DiagnosisReady report →
  let severity = match report.Severity with
                 | Critical → "🔴"
                 | Warning → "🟡"
                 | Info → "🟢"
  sprintf "%s diagnosis: %d failure(s), %d affected cell(s), %d suggestion(s)"
    severity report.Failures.Length report.AffectedCells.Length report.SuggestedFixes.Length
`

---

## 5. TEST PATTERNS

**Module:** SageFs.Tests/DiagnosticianTests.fs

**Composed Feature Test Example:**
`sharp
let private mkTestId name = TestId.TestId name
let private mkGraph cells edges =
  { Cells = cells |> List.map (fun c → c.Id, c) |> Map.ofList
    Edges = edges }

[<Tests>]
let compositionTests = testList "Diagnostician.compose" [
  testCase "joins CausalChange.SymbolChanged to CellGraph" <| fun _ →
    let cell0 = mkCell 0 "let tokenize x = x" ["tokenize"] []
    let cell1 = mkCell 1 "let parse = tokenize" ["parse"] ["tokenize"]
    let graph = mkGraph [cell0; cell1] [(0, 1)]
    
    let narrative = mkNarrative [CausalChange.SymbolChanged "tokenize"] "..."
    let failures = [mkTestId "t1", "test_parse", narrative]
    
    let report = Diagnostician.compose graph failures [] emptyTimeline
    
    report.Failures.[0].CausalCells
    |> Expect.equal "should resolve to cell 0" [0]
]
`

**Pattern:**
- Pure input builders (no IO)
- Direct composition function calls
- Assertions on output structure
- No side effects

---

## 6. THE 6 UNMAPPED COMPOSITIONS

Data infrastructure exists, but compositions missing:

### 1. TestId ↔ CellId Binding
- TestCase.Origin has TestOrigin.SourceMapped(file, line)
- CellInfo.Source contains actual code text
- **Missing:** Map<TestId, CellId> discovery function
- **Needed for:** "which cell's code defines test X?"

### 2. Flaky Test Detection (Partial)
- ResultWindow: circular buffer of TestOutcome (Pass|Fail)
- Map<TestId, ResultWindow> exists
- **Missing:** FlakyClassification ↔ FailureNarrative join
- **Needed for:** "why is test X flaky?"

### 3. Coverage Blind Spots
- CoverageBitmap.computeLineCoverageForFile merges all tests
- **Missing:** "find branches NO test exercises"
- **Idea:** ~(union of all bitmaps) = uncovered regions
- **Needed for:** "test these blind spots"

### 4. Annotation Hierarchy
- 9 GutterIcon variants (TestPassed, Covered, CellStale, etc.)
- LineAnnotation per line, but no priority ranking
- **Missing:** Enum of annotation layers with priority
- **Needed for:** "show most urgent annotation per line"

### 5. Binding Shadowing
- KnownBindings: Map<string, CellId> (name → producer cell)
- CellHistory: chronological cell list
- **Missing:** Shadow detection pass
- **Needed for:** "warn: name 'x' redefined in cell 5"

### 6. TestResult Impact → DepGraph
- Currently: FailureNarrative.CausalChanges (manual/heuristic)
- Available: CoverageBitmap(testId) shows all probes test hits
- **Missing:** Invert bitmap to find suspect cells
- **Idea:** TestId + failure → (CoverageBitmap ∩ changed cells)

---

## 7. FEATUREPUSHSTATE MODULE

**Location:** SageFs.Core/Features/FeatureHooks.fs

**State Structure:**
`sharp
type FeaturePushState = {
  LastOutputText: string
  LastEvalDiffSse: string option
  LastCellDepsSse: string option
  LastBindingScopeSse: string option
  LastEvalTimelineSse: string option
  EvalHistory: EvalHistoryEntry list
  NextCellIndex: int                 // W5(R9): Monotonic counter
  KnownBindings: Map<string, int>    // name → cellIndex
  CachedScope: BindingScopeSnapshot option
  CachedTimeline: TimelineState
}
`

**Incremental Update:**
`sharp
let recordEval (code: string) (result: string) (durationMs: int64)
  (state: FeaturePushState) : FeaturePushState =
  
  // Use NextCellIndex (never decreases) not EvalHistory.Length
  let idx = state.NextCellIndex
  
  // Extract "val name: type" from FSI output
  let newBindings = ... // incremental Map.add
  
  // W1(R9): Cap at MaxEvalHistory=10,000 to prevent O(n) growth
  let cappedHistory = (entry :: state.EvalHistory) |> List.truncate MaxEvalHistory
  
  // Rebuild scope snapshot from ALL history (in chronological order)
  let newScope = BindingExplorer.buildScopeSnapshot allCellInputs
  
  // Record timeline entry for performance tracking
  let newTimeline = EvalTimeline.TimelineState.record timelineEntry state.CachedTimeline
  
  { state with
      EvalHistory = cappedHistory
      NextCellIndex = idx + 1
      KnownBindings = newBindings
      CachedScope = Some newScope
      CachedTimeline = newTimeline }
`

**SSE Push Computations (Dedup by Content):**
`sharp
let computeEvalDiffPush opts sessionId currentOutputText state =
  let diff = EvalDiff.diffLines (Some state.LastOutputText) (Some currentOutputText)
  let sseStr = SageFs.SseWriter.formatEvalDiffEvent opts sessionId (EvalDiff.summarize diff)
  
  // Only return SSE if content changed (string comparison)
  if Some sseStr = state.LastEvalDiffSse then
    { state with LastEvalDiffSse = Some sseStr }, None
  else
    { state with LastEvalDiffSse = Some sseStr }, Some sseStr
`

**Key Design:** Incremental, monotonic CellIndex, dedup by equality

---

## 8. PROJECTWITHCOVERAGE COMPOSITION

**Not found as named function.** Exists implicitly in LiveTestState:

`sharp
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

**Implicit Composition:**
1. LiveTestState.Results (TestState)
2. JOIN InstrumentationMap.Slots (CoverageInstrumentation)
   via CoverageBitmap.computeLineCoverageForFile()
3. JOIN CellDependencyGraph.Edges (DepGraph)
   via CoverageBitmap.findCoverageAffected()
4. OUTPUT: SymbolCoverageIndex

**Missing Explicit Function:**
- (LiveTestState, CellDependencyGraph) → ProjectCoverageMap
- Would map: CellId → LineCoverage per test
- Enables: "which cells are untested?"

---

## SYNTHESIS: ARCHITECTURE PATTERNS

### Pure Composition Pattern
`
Feature1(A) ↔ Feature2(B) ↔ Feature3(C)
   ↓            ↓              ↓
   └────→ Compose(A,B,C) → Report
`

### Incremental State Pattern (FeaturePushState)
`
recordEval(code, result) updates:
- EvalHistory (capped at 10k)
- NextCellIndex (monotonic)
- KnownBindings (incremental map)
- CachedScope (full rebuild)
- CachedTimeline (append entry)
`

### SSE Push Pattern (Accumulator)
`
Events accumulate in ConcurrentQueue
- Replace strategy: deduplicate by tag
- Accumulate strategy: collect all

On tool response: DrainEvents() → formatForLlm() → append to result
`

### MCP Tool Pattern (McpContext)
`
Agent calls tool(sessionId, workingDir)
  ↓
Resolve session ID (by ID or by workingDir match)
  ↓
Route message to worker proxy
  ↓
Dispatch Elm event + format response
`

---

## PHASE 3 OPPORTUNITY

6 compositions ready for architectural design:
1. TestId ↔ CellId binding discovery
2. Flaky root analysis
3. Coverage blind spot detection
4. Annotation priority hierarchy
5. Binding shadow detection
6. TestResult impact analysis

Each follows Diagnostician's pattern:
- Pure function with typed inputs/outputs
- Feature module encapsulation
- Composable with SSE push

Would form foundation for deeper debugging aids.
