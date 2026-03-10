# EXECUTIVE SUMMARY - SageFs Phase 3 Design

## What Exists Today

### Feature Modules (Pure Composition)
- **Diagnostician.fs**: Master composition of 5 features
  - Input: CellGraph, test failures, scope bindings, timeline
  - Output: DiagnosticReport (failures + staleness + ripple plan + suggestions + perf context)
  - Pattern: Joins symbolic causal changes → cell IDs → staleness tracking

- **CellDependencyGraph.fs**: Notebook cell dependencies
  - Cells identified by int (CellId)
  - Symbols tracked as produces/consumes lists
  - Pure graph building from FSI outputs

- **EvalProvenance.fs**: Staleness classification
  - Fresh vs StaleUpstream (which cells changed)
  - Pure computation from graph + changed set

- **EvalTimeline.fs**: Performance tracking
  - P50/P95 detection for perf regression
  - Entry-by-entry timeline

- **EvalDedup.fs**: Temporal dedup cache
  - 2-second window per session
  - Hash-based duplicate detection

- **CoverageInstrumenter.fs**: IL-level branch coverage
  - Cecil-based probe injection (__SageFsCoverage class)
  - bool[] hits array indexed by sequence point

- **LiveTestingTypes.fs**: Test state & coverage
  - TestId: stable SHA256 identity (16 hex chars)
  - CoverageBitmap: uint64[] for memory efficiency (8× vs bool[])
  - LiveTestState: unified test + coverage state

### Integration Layer (MCP)
- **McpTools**: All tools route through SessionManager
  - sendFSharpCode: eval with dedup + preprocessing + event dispatch
  - getStatus, getStartupInfo, etc: status queries
  - Routing: agent string → session ID resolution + worker message routing

- **McpPushNotifications**: Event accumulation with smart dedup
  - Replace strategy: latest event only (diagnostics, test summary)
  - Accumulate strategy: collect all (file reloads)
  - EventAccumulator: thread-safe ConcurrentQueue with tag-based dedup

### Incremental State (FeaturePushState)
- EvalHistory: capped at 10k (prepend O(1))
- NextCellIndex: monotonic counter (survives capping)
- KnownBindings: incremental name → cellId map
- CachedScope, CachedTimeline: rebuilt on each eval
- SSE push functions: only emit if content differs from last

---

## What's Partially Built

### Live Testing Infrastructure
- Test discovery by framework attributes
- Test execution scheduling + result collection
- Coverage bitmap equivalence checks (SIMD-friendly)
- Flaky test circular buffer (ResultWindow)

**Gap:** No flaky root analysis (why is test flaky?)

### Coverage Analysis
- Line-level coverage computation
- Per-file coverage masks
- Test-to-affected-tests mapping

**Gap:** No blind spot detection (uncovered branches)

---

## The 6 Unmapped Compositions (Phase 3 Opportunity)

All data is present. Composition logic is missing.

### 1. TestId ↔ CellId Binding Discovery
**Data Available:**
- TestCase.Origin.SourceMapped(file, line)
- CellInfo.Source (actual code text)

**Composition:** Match source locations → cell IDs
**Purpose:** Answer "which cell defines test X?"

### 2. Flaky Root Analysis
**Data Available:**
- Map<TestId, ResultWindow> (pass/fail history)
- FailureNarrative.CausalChanges (symbol names)
- FlakyClassification (Stable|Intermittent|EnvironmentallyFlaky)

**Composition:** Join classification + narrative → "why flaky?"
**Purpose:** Demote environmental flakiness from tier 0 to tier 2

### 3. Coverage Blind Spots
**Data Available:**
- CoverageBitmap per test (probes hit)
- InstrumentationMap (slot → SequencePoint)
- CoverageBitmap.computeLineCoverageForFile

**Composition:** Find (~union of bitmaps) = uncovered branches
**Purpose:** "Which branches should be tested?"

### 4. Annotation Hierarchy
**Data Available:**
- 9 GutterIcon types (TestPassed, Covered, CellStale, etc.)
- LineAnnotation per line
- TestRunStatus + coverage status per cell

**Composition:** Rank annotation priority per line
**Purpose:** "Show most urgent annotation (stale > failed > uncovered)"

### 5. Binding Shadowing Detection
**Data Available:**
- KnownBindings: Map<string, CellId> (name → producer)
- EvalHistory: chronological cell list
- CellDependencyGraph.Produces (symbols)

**Composition:** Detect name redefined in later cell
**Purpose:** "Warn: binding 'x' from cell 1 is shadowed by cell 5"

### 6. TestResult ↔ DepGraph Impact Analysis
**Data Available:**
- TestRunResult (which tests failed)
- CoverageBitmap (which probes the test hit)
- CellDependencyGraph (cell dependencies)

**Composition:** TestId + failure → suspect cells
**Purpose:** More precise failure diagnosis than heuristics

---

## Architecture Patterns to Adopt

### 1. Pure Composition (Diagnostician Model)
`
(Input1, Input2, ...) → Output
`
- No side effects in feature modules
- Type signatures enforce composition
- Easy to test (mock inputs)
- Easy to reuse (no IO coupling)

### 2. Incremental State (FeaturePushState Model)
`
state ↦ recordEval(code, result) ↦ state'
state ↦ computeXxxPush(...) ↦ (state'', sse_event option)
`
- Monotonic counters survive truncation
- Dedup by content equality
- Only emit on change
- Bounded memory (capped collections)

### 3. Event Accumulation (McpPushNotifications Model)
`
events.Add(evt) → dedup by strategy → events' (bounded queue)
events.Drain() → formatForLlm[] → append to response
`
- Thread-safe ConcurrentQueue
- Replace vs Accumulate strategies
- Format once per drain (LLM consumption)

### 4. Session Routing (McpTools Model)
`
agent + workingDir → resolve sessionId
sessionId → worker proxy → WorkerMessage
response + elm events + event persistence
`
- Multiple agents per daemon
- Working directory as resolution hint
- Elm event dispatch for daemon-mode side effects

---

## Code Locations - Quick Reference

| Concept | File | Type/Function |
|---------|------|---------------|
| Cell deps | CellDependencyGraph.fs | CellId=int, CellGraph |
| Staleness | EvalProvenance.fs | Staleness, compute |
| Timeline | EvalTimeline.fs | TimelineEntry, timelineStats |
| Composition | Diagnostician.fs | DiagnosticReport, compose |
| Push state | FeatureHooks.fs | FeaturePushState, recordEval |
| MCP tools | Mcp.fs | McpTools module |
| MCP routing | Mcp.fs | McpContext, routeToSession |
| SSE events | McpPushNotifications.fs | PushEvent, EventAccumulator |
| Test state | LiveTestingTypes.fs | LiveTestState |
| Coverage | LiveTestingTypes.fs | CoverageBitmap |
| Instrumentation | CoverageInstrumenter.fs | __SageFsCoverage injection |

---

## Phase 3 Design Goals

1. **Build 6 unmapped compositions** as orthogonal feature modules
2. **Follow Diagnostician pattern:** (InputA, InputB, ...) → Output
3. **Add SSE push support:** Each composition emits DiagnosisReady event
4. **Integrate with MCP tools:** New tools expose the compositions
5. **Test with composed features:** Follow DiagnosticianTests pattern

---

## Success Criteria

- [ ] Each of 6 compositions has a pure compose function
- [ ] Each emits PushEvent.DiagnosisReady on discovery
- [ ] MCP tools wired to call compositions (e.g., get_flaky_roots)
- [ ] Test suite covers composed features end-to-end
- [ ] Documentation shows data flow (feature module → composition → SSE → LLM)

---

## Technical Debt / Risk Mitigation

**Risk:** Binding shadowing detection scope creep
**Mitigation:** Start with 2-cell detection (immediately previous), extend later

**Risk:** Coverage blind spots producing noise
**Mitigation:** Rank by probe count (ignore 1-probe blind spots)

**Risk:** Flaky root analysis false positives
**Mitigation:** Require 3+ flips in ResultWindow before classification

---

## Next Steps

1. Implement TestId ↔ CellId binding discovery (simplest)
2. Extend Diagnostician to detect shadowing as warning
3. Build flaky root analysis (join FlakyClassification + FailureNarrative)
4. Add coverage blind spot detection to LiveTestState
5. Wire all 4 as MCP tools
6. Implement annotation hierarchy (most UX impact)
7. TestResult impact analysis (most complex)
