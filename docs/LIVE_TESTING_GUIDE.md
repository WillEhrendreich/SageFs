================================================================================
SAGEFS LIVE TESTING & COVERAGE SYSTEM - IMPLEMENTATION GUIDE
================================================================================

STATUS: Live testing is functional but still being stabilized. Rough edges
remain around session switching and test-discovery timing. Expecto has the
best coverage. It's what I test against every day, so it's what gets
exercised hardest. This guide describes the internal design for people who
want to go spelunking in the source; line numbers are approximate and drift
as the code changes, so treat them as "look near here," not "line X exactly."
I'd rather you find the real thing three lines off than trust a number I
never re-checked.

QUICK REFERENCE - KEY FILES & FUNCTIONS
================================================================================

1. Test Discovery & Execution:
   - File: SageFs.Core/Features/LiveTestingExecutors.fs
   - Key Functions:
     * AttributeDiscovery.discoverInAssembly: Scan types for test attributes
     * AttributeDiscovery.discoverWithRunner: Discovery + execution closures
     * ReflectionExecutor.executeMethod: Invoke via MethodInfo.Invoke
     * ExpectoExecutor: Custom reflection-based Expecto runner
   - Frameworks Supported: Expecto, xUnit (incl. xUnit v3), NUnit, MSTest, TUnit
   - Key Type: DiscoveryResult { Tests: TestCase list; RunTest: TestCase → Async<TestResult> }

2. Coverage Instrumentation (IL-Level):
   - File: SageFs.Core/Features/CoverageInstrumenter.fs
   - Key Functions:
     * collectSequencePoints: Extract all non-hidden IL probes
     * injectTracker: Create __SageFsCoverage class
     * insertProbes: Inject Hit() calls before sequence points
     * instrumentAssembly: Full instrumentation pipeline
     * collectCoverageHits: Read coverage data post-test via reflection
   - Key Type: InstrumentationMap { Slots: SequencePoint[]; TotalProbes: int }
   - Coverage Bitmap: CoverageBitmap { Bits: uint64[]; Count: int } (8x memory vs bool[])

3. Dependency Graph (Symbol → Test Mapping):
   - File: SageFs.Core/Features/LiveTestingTypes.fs
   - Key Functions:
     * TestDependencyGraph.buildFromSymbolUses: Build from FCS extracts
     * TestDependencyGraph.findAffected: Get tests for changed symbols
     * TestDependencyGraph.computeTransitiveCoverage: BFS through call graph
   - Key Type: TestDependencyGraph { SymbolToTests; TransitiveCoverage; PerFileIndex; SourceVersion }

4. Flaky Test Classification:
   - File: SageFs.Core/Features/LiveTestingTypes.fs
   - Key Functions:
     * FlakyDetection.classifyFlakiness: Classify Environmental vs Property
     * FlakyDetection.isFsCheckFailure: Extract shrunk counterexample
   - Key Type:
     * FlakyClassification = Insufficient | Stable | Environmental(int) | PropertyCounterexample(string)
     * ResultWindow (circular buffer): Track last 10 outcomes, count flips
   - Defaults: windowSize=10, flipThreshold=2, minSamples=3

5. Failure Narratives (Causal Analysis):
   - File: SageFs.Core/Features/LiveTestingTypes.fs
   - Key Types:
     * FailureNarrative { LastPassedAt; TimeSinceLastPass; CausalChanges; PropertyViolation; Summary }
     * CausalChange = SymbolChanged(string) | FileChanged(string) | Unknown
     * PropertyViolationDetail { PropertyName; ShrunkCounterexample; AlgebraicCategory }
   - Algebraic Categories Detected: associativity, commutativity, identity, idempotence, distributivity, inverse, absorption, closure

6. Test Prioritization:
   - File: SageFs.Core/Features/LiveTestingTypes.fs
   - Key Functions:
     * TestPrioritization.computeTier: Assign tier (0=failed, 4=notrun)
     * TestPrioritization.buildSortKey: Lexicographic (tier, -coverage, duration)
   - Tier Rules: Failed(0) → New(1) → Passed(2) → Skipped(3) → NotRun(4)
   - Environmental flaky failures demoted from tier 0 to tier 2

7. Test Explainer & Verification:
   - File: SageFs/Mcp.fs
   - MCP tools (what agents actually call):
     * explain_test_failure — why a test failed
     * targeted_verify — run and verify specific tests
     * list_tests — list discovered tests
     * coverage_intel — coverage summary
   - Internal functions behind these (not standalone MCP tools):
     * explainTestRun — computes why a test ran
     * getFileCoverage — per-line coverage; surfaced to editors via SSE
       file_annotations and the HTTP API (GET /api/live-testing/file-annotations),
       not as an MCP tool
   - There is no run_tests, enable_live_testing, get_live_test_status,
     get_test_trace, explain_test_run, or get_file_coverage MCP tool. Live-testing
     enable/disable/status/run are HTTP API endpoints under /api/live-testing/...
     used by editors and the dashboard, on purpose. This is machinery an editor
     drives, not something an agent should be poking at directly.

8. Per-Line Coverage Data:
   - File: SageFs.Core/Features/LiveTestingTypes.fs
   - Key Functions:
     * FileAnnotations.projectWithCoverage: Get per-line coverage
     * FileAnnotations.resolveFilePath: Resolve partial paths
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
  Framework: TestFramework            // Expecto | XUnit | NUnit | MSTest | TUnit | Unknown of string
  Category: TestCategory              // Unit | Integration | Browser | Benchmark | Architecture | Property | Custom of string
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

None of these extension points have an actual second implementation behind
them yet. I've kept the seams open (interfaces, registries, extend-not-edit
shapes) because I've been burned before by code that assumed it would only
ever have one test framework, one language, one output format. Nobody's
built a Pytest bridge. If you do, I'd love to hear about it.

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
- **Neovim**: lives in its own repo, [`sagefs.nvim`](https://github.com/WillEhrendreich/sagefs.nvim) (not in this tree). Its `testing.lua` caches source_locations and failure_narratives, `telescope_picker.lua` jumps to source on `<CR>`, and `commands.lua` shows a narrative floating window on `<C-d>`.