================================================================================
SAGEFS LIVE TESTING & COVERAGE SYSTEM - COMPLETE IMPLEMENTATION GUIDE
================================================================================

QUICK REFERENCE - KEY FILES & FUNCTIONS
================================================================================

1. Test Discovery & Execution:
   - File: SageFs.Core/Features/LiveTestingExecutors.fs
   - Key Functions:
     * AttributeDiscovery.discoverInAssembly (lines 67-81): Scan types for test attributes
     * AttributeDiscovery.discoverWithRunner (lines 86-103): Discovery + execution closures
     * ReflectionExecutor.executeMethod (lines 109-145): Invoke via MethodInfo.Invoke
     * ExpectoExecutor (lines 204-400+): Custom reflection-based Expecto runner
   - Frameworks Supported: Expecto, xUnit, NUnit, MSTest, TUnit
   - Key Type: DiscoveryResult { Tests: TestCase list; RunTest: TestCase → Async<TestResult> }

2. Coverage Instrumentation (IL-Level):
   - File: SageFs.Core/Features/CoverageInstrumenter.fs
   - Key Functions:
     * collectSequencePoints (lines 18-40): Extract all non-hidden IL probes
     * injectTracker (lines 43-102): Create __SageFsCoverage class
     * insertProbes (lines 152-174): Inject Hit() calls before sequence points
     * instrumentAssembly (lines 178-242): Full instrumentation pipeline
     * collectCoverageHits (lines 268-287): Read coverage data post-test via reflection
   - Key Type: InstrumentationMap { Slots: SequencePoint[]; TotalProbes: int }
   - Coverage Bitmap: CoverageBitmap { Bits: uint64[]; Count: int } (8x memory vs bool[])

3. Dependency Graph (Symbol → Test Mapping):
   - File: SageFs.Core/Features/LiveTestingTypes.fs (lines 1512-1629)
   - Key Functions:
     * TestDependencyGraph.buildFromSymbolUses (lines 1587-1629): Build from FCS extracts
     * TestDependencyGraph.findAffected (lines 1538-1542): Get tests for changed symbols
     * TestDependencyGraph.computeTransitiveCoverage (lines 1561-1582): BFS through call graph
   - Key Type: TestDependencyGraph { SymbolToTests; TransitiveCoverage; PerFileIndex; SourceVersion }

4. Flaky Test Classification:
   - File: SageFs.Core/Features/LiveTestingTypes.fs (lines 791-870)
   - Key Functions:
     * FlakyDetection.classifyFlakiness (lines 851-870): Classify Environmental vs Property
     * FlakyDetection.isFsCheckFailure (lines 810-821): Extract shrunk counterexample
   - Key Type: 
     * FlakyClassification = Insufficient | Stable | Environmental(int) | PropertyCounterexample(string)
     * ResultWindow (circular buffer): Track last 10 outcomes, count flips
   - Defaults: windowSize=10, flipThreshold=2, minSamples=3

5. Failure Narratives (Causal Analysis):
   - File: SageFs.Core/Features/LiveTestingTypes.fs (lines 872-950)
   - Key Types:
     * FailureNarrative { LastPassedAt; TimeSinceLastPass; CausalChanges; PropertyViolation; Summary }
     * CausalChange = SymbolChanged(string) | FileChanged(string) | Unknown
     * PropertyViolationDetail { PropertyName; ShrunkCounterexample; AlgebraicCategory }
   - Algebraic Categories Detected: associativity, commutativity, identity, idempotence, distributivity, inverse, absorption, closure

6. Test Prioritization:
   - File: SageFs.Core/Features/LiveTestingTypes.fs (lines 2212-2259)
   - Key Functions:
     * TestPrioritization.computeTier (lines 2223-2235): Assign tier (0=failed, 4=notrun)
     * TestPrioritization.buildSortKey (lines 2239-2246): Lexicographic (tier, -coverage, duration)
   - Tier Rules: Failed(0) → New(1) → Passed(2) → Skipped(3) → NotRun(4)
   - Environmental flaky failures demoted from tier 0 to tier 2

7. Test Explainer (MCP Tools):
   - File: SageFs.Core/Mcp.fs
   - Key Functions:
     * explainTestRun (lines 1665-1717): Why test ran - explain_test_run tool
     * explainTestFailure (lines 1818-1870+): Why test failed - explain_test_failure tool
     * getFileCoverage (lines 1798-1816): Per-line coverage - get_file_coverage tool
   - Uses TestRunExplainer.explainTest (lines 3267-3299) to compute reason

8. Per-Line Coverage Data:
   - File: SageFs.Core/Features/LiveTestingTypes.fs (lines 3031-3230)
   - Key Functions:
     * FileAnnotations.projectWithCoverage (lines 3166-3204): Get per-line coverage
     * FileAnnotations.resolveFilePath (lines 3208-3230): Resolve partial paths
   - Output: CoverageLineAnnotation { Line; EndLine; EndColumn; Detail; CoveringTestIds; BranchCoverage }
   - BranchCoverage: FullyCovered | PartiallyCovered(covered, total) | NotCovered

================================================================================
EXECUTION FLOW - AS-YOU-TYPE LIVE TESTING
================================================================================

1. KEYSTROKE EVENT:
   - LiveTestCycleState.onKeystroke marks phase as edited
   - Triggers TreeSitter debounce (default 150ms)

2. TREESITTER PARSES FILE:
   - Extract test function locations (source-mapped)
   - Emit TreeSitterComplete effect

3. AFTER TREESITTER:
   - Request FCS type-check for file

4. FCS TYPE-CHECK COMPLETES:
   - Extract symbol references (SymbolUse with FullName, UseKind)
   - FileAnalysisCache.update: compute changed symbols
   - SymbolGraphBuilder.updateGraph: incrementally update DepGraph
   - Emit ChangedSymbols list

5. TEST CYCLE ORCHESTRATOR DECIDES:
   - Get affected tests: TestDependencyGraph.findAffected(changedSymbols)
   - Filter by RunPolicy (OnEveryChange, OnSaveOnly, OnDemand, Disabled)
   - If any tests → FullCycle decision, else TreeSitterOnly

6. TEST EXECUTION (If FullCycle):
   - Prioritize tests: tier → -coverageWeight → duration
   - Execute in parallel chunks (per-session executors)
   - Collect TestRunResult + coverage bitmaps
   - Update FlakyHistory (ResultWindow)
   - Build FailureNarratives for Passed→Failed transitions
   - Recompute StatusEntries

7. STREAM RESULTS:
   - SSE (Server-Sent Events) deduped via StateVersion
   - Only send if LiveTestState changed

================================================================================
STATE MACHINE - KEY TYPES
================================================================================

TestCase:
{
  Id: TestId                          // Stable SHA256 hash
  FullName: string                    // e.g. "MyTests.test_add"
  DisplayName: string                 // e.g. "test_add"
  Origin: TestOrigin                  // SourceMapped(file, line) | ReflectionOnly
  Labels: string list
  Framework: TestFramework            // Expecto | XUnit | NUnit | MSTest | TUnit
  Category: TestCategory              // Unit | Integration | Browser | Benchmark | Architecture | Property
}

TestRunResult:
{
  TestId: TestId
  TestName: string
  Result: TestResult                  // Passed(duration) | Failed(failure, duration) | Skipped | NotRun
  Timestamp: DateTimeOffset
  Output: string option               // Captured console output
}

LiveTestState:
{
  DiscoveredTests: TestCase[]
  LastResults: Map<TestId, TestRunResult>           // Latest outcome per test
  StatusEntries: TestStatusEntry[]                  // UI gutter state
  RunPhases: Map<string, TestRunPhase>              // Per-session execution phase
  TestSessionMap: Map<TestId, string>               // Routing: TestId → session
  TestCoverageBitmaps: Map<TestId, CoverageBitmap>  // IL coverage per test
  FlakyHistory: Map<TestId, ResultWindow>           // Classification input
  FailureNarratives: Map<TestId, FailureNarrative>  // Why test failed
  StateVersion: int64                               // Dedup counter
  // ... more fields
}

LiveTestCycleState:
{
  TestState: LiveTestState
  DepGraph: TestDependencyGraph                        // Symbol → test mapping
  InstrumentationMaps: Map<sessionId, InstrumentationMap[]>  // IL coverage maps
  AnalysisCache: FileAnalysisCache                    // File → symbol defs
  Debounce: TestCycleDebounce                         // TreeSitter + FCS channels
  AdaptiveDebounce: AdaptiveDebounce                  // Dynamic delay tuning
  LastTrigger: RunTrigger
  ChangedSymbols: string[]
  // ... more fields
}

================================================================================
KEY INVARIANTS & GUARANTEES
================================================================================

1. InstrumentationMap Slot Order: Once assigned, slot IDs never change
   → Enables stable CoverageBitmap comparison across test runs

2. CoverageBitmap Compatibility: All bitmaps must match current InstrumentationMap size
   → Stale instrumentation generation detected + skipped

3. TestId Determinism: SHA256(fullName + framework) always produces same ID
   → Enables stable cross-session test identity

4. StateVersion Monotonic: Increments on every LiveTestState mutation
   → O(1) dedup via version check instead of deep equality

5. FlakyHistory Circular: Never shrinks, overwrites oldest when full
   → Bounded memory for result windows

6. FailureNarrative Transient: Cleared when test passes, rebuilt on next failure
   → Captures transition points, not static state

7. Tier Invariant: Environmental flaky failures demoted tier 0→2
   → Prevents attention-stealing; allows honest failures to surface

8. Coverage Bitmap Intersection: All tests in same batch share same bitmap
   → Conservative: any test might have hit any probe → safe upper bound

================================================================================
EXTENSION POINTS FOR CUSTOMIZATION
================================================================================

To build on top of SageFs live testing:

1. Custom Test Frameworks:
   → Implement TestExecutor with custom Discover function
   → Register in BuiltInExecutors list
   → Examples: Pytest, Jest, Go testing, Rust criterion

2. Custom Failure Analysis:
   → Hook FailureNarrativeBuilder.detectAlgebraicCategory for new patterns
   → Extend PropertyViolationDetail with custom fields
   → Add regex matchers for domain-specific failures

3. Custom Test Prioritization:
   → Extend PrioritizationContext with custom fields
   → Implement custom buildSortKey logic
   → Example: prioritize by test cost (speed × resource usage)

4. Custom Symbol Analysis:
   → Override SymbolGraphBuilder.updateGraph for language-specific semantics
   → Implement custom FileAnalysisCache.update for different parsers
   → Example: Support Python, TypeScript, C# analyzers

5. Custom Coverage Analysis:
   → Extend CoverageBitmap operations (beyond intersect/union/xor)
   → Implement custom file masking logic
   → Example: Code-churn based coverage weighting

6. Custom Flaky Detection:
   → Hook FlakyDetection.classifyFlakiness with new classifiers
   → Add custom Result Window strategies (e.g., exponential weighting)
   → Example: Network timeout detection, resource exhaustion patterns

7. Custom Reporting:
   → Extend formatFileCoverageResponse for custom metrics
   → Implement custom FailurePresentation formats
   → Example: Generate HTML reports, integrations with issue tracking

================================================================================

## SSE Event Formats

Wire formats for live-testing SSE events consumed by editor integrations.

### test_source_locations

```
event: test_source_locations
data: {"SessionId":"<id>", "Locations": [{"CellId":int, "TestName":"string", "FilePath":"string", "StartLine":int, "EndLine":int}]}
```

### file_annotations

```
event: file_annotations
data: {"SessionId":"<id>", "Annotations": {"<filePath>": {"CoverageAnnotations": [{"Line":int, "Health":"AllPassing|SomeFailing|NoCoverage", "Tests":["testName"]}], "InlineFailures": [{"Line":int, "TestName":"string", "Presentation":"AssertionDiff|ExceptionMessage|Timeout|RawMessage", "Details":{...}}]}}}
```

### failure_narratives

```
event: failure_narratives
data: {"SessionId":"<id>", "Narratives": [{"TestId":"string", "TestName":"string", "Summary":"string", "TimeSinceLastPass":"string", "CausalChanges":[{"Symbol":"string", "File":"string"}], "PropertyViolation":null|{...}}]}
```

## Editor Integrations

- **VS Code**: `FileAnnotationsListener.fs` parses file_annotations. `Extension.fs` renders coverage gutter decorations + inline failures. `TestControllerAdapter.fs` enriches test items with failure narratives.
- **Visual Studio**: `CoverageGlyphTagger.cs` (MEF pipeline for gutter glyphs). `FileAnnotationTracker.cs` caches coverage + failure data. `TestStateTracker.cs` stores source locations for navigation.
- **Neovim**: `testing.lua` caches source_locations and failure_narratives. `telescope_picker.lua` jumps to source on `<CR>`. `commands.lua` shows narrative floating window on `<C-d>`.
