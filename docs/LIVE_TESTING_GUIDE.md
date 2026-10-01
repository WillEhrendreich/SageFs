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
   - run_tests is the one MCP door for running tests. It dispatches the same
     RunTestsRequested event the editors and the dashboard use (POST
     /api/live-testing/run) and reads the engine's own record of that request back
     as a receipt, so the result is never a second runner's opinion.
   - There is no enable_live_testing, get_live_test_status, get_test_trace,
     explain_test_run, or get_file_coverage MCP tool. Live-testing
     enable/disable/status are HTTP API endpoints under /api/live-testing/...
     used by editors and the dashboard, on purpose.

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

## Debugging a failing test

You can debug a failing test from VS Code. A failing test gets a Debug lens next to its result, a Debug link in the hover of its gutter mark, and Debug Test in the Test Explorer (that is also what puts Debug Test on the glyph). Neovim does not have it yet.

How it works: the test runs in the process that loaded your code, which is the isolated FSI host (or the worker, for an in-process session). So the editor attaches a .NET debugger to that process.

1. The editor asks the daemon to debug a test. The host holds the test and answers with its process id and a ticket.
2. The editor attaches its `coreclr` debugger to that process.
3. The editor continues. The host checks that a debugger is really attached, runs the test under it, and answers when it finishes. The editor then detaches.

If nobody releases the test within two minutes (`Timeouts.debugHold`), the host drops the hold and the test never runs. If the debugger is not attached when the test is released, the test does not run either. Nothing runs without a debugger.

**Which debugger you need.** One that provides the `coreclr` debug type. In Microsoft's VS Code that is the C# extension (`ms-dotnettools.csharp`). If nothing installed provides it, the extension says so and offers to install it. Microsoft's debugger is licensed for Microsoft's own build of VS Code. In another build you need some other extension that provides `coreclr`.

**What the debugger can stop in.**

- Code in your project's compiled assemblies has a PDB, so breakpoints bind. Build in Debug, or with portable PDBs. An optimized build will skip lines and hide locals, like any optimized build.
- Code you evaluated in the session (a test file SageFs re-evaluated when you saved it) lives in a dynamic assembly. It has no PDB, so breakpoints inside it will not bind. The debugger still runs the test, it just will not stop in that code. SageFs says so when you start. To debug the compiled copy, hard reset the session with a rebuild first.
- A compiled method that hot reload has detoured to a new body has the same problem for that method.

**Linux.** Attaching to a process that is not your child needs ptrace permission. With `kernel.yama.ptrace_scope` at 1 (the default on many distros) the host opens that door itself for the length of the hold and closes it after, so the attach works without you changing anything. At 2 only root may attach, and at 3 nobody may. SageFs tells you which one it hit and what to change, but it cannot fix those for you.

**What it does not do yet.**

- It debugs one test at a time. The host holds one test, and a second request is refused until the first ends.
- The result of a debug run is shown in the editor. It is not recorded in the live-testing state, so the gutter updates on the next normal run.
- I have run the host side against a real isolated host, and checked the ptrace door opens and closes on a real process. I have not run a full attach with the C# extension's debugger from this repo's tests. The VS Code side is covered by contract tests that run without VS Code.

The two routes, for any editor client. Both answer the same JSON shape (camelCase) and read the body whatever the status code is.

| Route | Body | Answers |
|---|---|---|
| `POST /api/live-testing/debug` | `{ "testId": "<id>" }` or `{ "pattern": "<name>" }`, optional `"sessionId"` | `status` `held` with `pid`, `ticket`, `testName`, `symbols` (`compiled` or `eval`), `symbolsNote`, `access` (`open` or `blocked`), `accessNote`, `holdMs` |
| `POST /api/live-testing/debug/continue` | `{ "ticket": "<ticket>" }`, optional `"sessionId"` | `still_running` (ask again), or `attached` with `outcome` and `detail`, or one of the dead ends below |

The other statuses are `hold_already_open`, `no_debugger_within`, `released_without_debugger`, `no_such_hold`, `host_lost`, `host_unavailable`, `not_discovered`, `no_test_matched`, `ambiguous_test`, `no_session`, `no_worker`, `worker_failed` and `bad_request`. Every response carries a `message` that says what happened and what to do about it.

## Editor Integrations

- **VS Code**: `FileAnnotationsListener.fs` parses file_annotations. `Extension.fs` renders coverage gutter decorations + inline failures. `TestControllerAdapter.fs` enriches test items with failure narratives, and registers the Debug profile. `TestDebugCommand.fs` and `TestDebugPure.fs` debug a failing test (see above).
- **Neovim**: lives in its own repo, [`sagefs.nvim`](https://github.com/WillEhrendreich/sagefs.nvim) (not in this tree). Its `testing.lua` caches source_locations and failure_narratives, `telescope_picker.lua` jumps to source on `<CR>`, and `commands.lua` shows a narrative floating window on `<C-d>`.