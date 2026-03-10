# SageFs Feature Modules Survey

## Summary

Total Feature Modules: **30** in SageFs.Core/Features/
Plus **2** root-level SageFs.Core modules with feature characteristics

---

## FEATURES/ DIRECTORY MODULES

### 1. **AutoCompletion** — Code completion via FuzzySharp ranking.
- **Module**: SageFs.Features.AutoCompletion
- **Types**: CompletionKind (enum), completion ranking
- **Functions**: label, rankByType, etc.
- **MCP Tool**: ❌ No direct tool (via explore_type/explore_namespace)
- **SSE Emission**: ❌ No
- **Tests**: ❌ No dedicated test (covered in AutoCompletionAndEventsTests.fs)
- **Status**: DARK (pure logic, exposed through other tools)

### 2. **BindingExplorer** — Tracks FSI bindings, shadowing, references.
- **Module**: SageFs.Features.BindingExplorer
- **Types**: BindingInfo, BindingScopeSnapshot, CellInput
- **Functions**: parseBinding, buildScopeSnapshot, etc.
- **MCP Tool**: ❌ No (data pushed via SSE in explore_namespace)
- **SSE Emission**: ✅ YES - formatBindingScopeMapEvent
- **Tests**: ✅ BindingExplorerTests.fs
- **Status**: LIT (SSE emission in FeatureHooks)

### 3. **CellDependencyGraph** — Builds dependency DAG from eval history.
- **Module**: SageFs.Features.CellDependencyGraph
- **Types**: CellId, CellInfo, CellGraph
- **Functions**: analyzeCell, buildGraph, topologicalSort
- **MCP Tool**: ❌ No (data structure only)
- **SSE Emission**: ✅ YES - formatCellDependenciesEvent
- **Tests**: ✅ CellDependencyGraphTests.fs
- **Status**: LIT (SSE push in FeatureHooks)

### 4. **CoverageInstrumenter** — IL-level branch coverage via Mono.Cecil.
- **Module**: SageFs.Features.LiveTesting.CoverageInstrumenter
- **Types**: Coverage bitmap tracking
- **Functions**: collectSequencePoints, injectCoverageTracking, etc.
- **MCP Tool**: ❌ No (internal to test instrumentation)
- **SSE Emission**: ❌ No (output via test results)
- **Tests**: ✅ CoverageInstrumenterTests.fs
- **Status**: DARK (pure IL transformation, no external wiring)

### 5. **DaemonHealth** — Session health monitoring and aggregation.
- **Module**: SageFs.Features.DaemonHealth
- **Types**: SessionHealthStatus, OverallHealth, HealthSnapshot
- **Functions**: aggregateHealth, sessionStatusLabel, etc.
- **MCP Tool**: ❌ No (health query is in get_fsi_status)
- **SSE Emission**: ❌ No (health is pull-based)
- **Tests**: ✅ DaemonHealthTests.fs
- **Status**: DARK (pure domain model, no external wiring)

### 6. **DaemonPersistence** — Binaries (.sagetc, .sagefs) I/O orchestration.
- **Module**: SageFs.Features.DaemonPersistence
- **Types**: ManifestSessionEntry, DaemonManifestData
- **Functions**: projectHash, saveTestCache, loadTestCache, saveSession, loadSession
- **MCP Tool**: ❌ No (internal orchestration)
- **SSE Emission**: ❌ No
- **Tests**: ❌ No (complex I/O, likely in integration tests)
- **Status**: DARK (pure I/O coordination, no external wiring)

### 7. **Diagnostician** — Composes failures, ripples, suggestions, perf into report.
- **Module**: SageFs.Features.Diagnostician
- **Types**: DiagnosedFailure, DiagnosticReport, DiagnosticSeverity
- **Functions**: composeReport, rankFailures, suggestFixes
- **MCP Tool**: ✅ YES - diagnose (composes 6 modules into one report)
- **SSE Emission**: ✅ YES - formatDiagnosisReadyEvent
- **Tests**: ✅ DiagnosticianTests.fs
- **Status**: LIT (MCP tool + SSE emission)

### 8. **Diagnostics** — F# compiler diagnostic parsing.
- **Module**: SageFs.Features.Diagnostics
- **Types**: Range, DiagnosticSeverity, Diagnostic
- **Functions**: mkDiagnostic (adapter from FSharpDiagnostic)
- **MCP Tool**: ❌ No (used internally by check_fsharp_code)
- **SSE Emission**: ❌ No
- **Tests**: ❌ No dedicated test (covered in diagnostics tests)
- **Status**: DARK (pure parsing, used internally)

### 9. **DomainModelViz** — Extracts state machines from DU + functions.
- **Module**: SageFs.Features.DomainModelViz
- **Types**: DUCaseInfo, StateTransition, StateMachineModel
- **Functions**: DUExtractor.fromType, renderStateDiagram, etc.
- **MCP Tool**: ✅ YES - visualize_domain_model
- **SSE Emission**: ✅ YES - formatDomainModelEvent
- **Tests**: ✅ DomainModelVizTests.fs, DomainModelSseTests.fs
- **Status**: LIT (MCP tool + SSE emission)

### 10. **EvalDedup** — Hash-based temporal dedup for repeated evals.
- **Module**: SageFs.Features.EvalDedup
- **Types**: DedupEntry, DedupCache
- **Functions**: tryGet, addEntry, isStale
- **MCP Tool**: ❌ No (internal optimization)
- **SSE Emission**: ❌ No
- **Tests**: ✅ EvalDedupTests.fs
- **Status**: DARK (pure caching, not exposed)

### 11. **EvalDiff** — Line-by-line diff of eval outputs.
- **Module**: SageFs.Features.EvalDiff
- **Types**: DiffLine, DiffSummary
- **Functions**: diffLines, summarize
- **MCP Tool**: ✅ YES - get_eval_diff
- **SSE Emission**: ✅ YES - formatEvalDiffEvent
- **Tests**: ✅ EvalDiffTests.fs
- **Status**: LIT (MCP tool + SSE emission)

### 12. **EvalLens** — Pipeline stage purity classification (Pure/Effectful/Unknown).
- **Module**: SageFs.Features.EvalLens
- **Types**: LensClassification, PipelineStage, LensResult
- **Functions**: classifyPipeline, decomposeExpression
- **MCP Tool**: ✅ YES - decompose_pipeline
- **SSE Emission**: ❌ No (stateless tool, returns text)
- **Tests**: ✅ EvalLensTests.fs
- **Status**: LIT (MCP tool, no SSE needed)

### 13. **EvalProvenance** — Staleness tracking (Fresh/StaleUpstream).
- **Module**: SageFs.Features.EvalProvenance
- **Types**: Staleness, EvalProvenance
- **Functions**: compute, describe
- **MCP Tool**: ❌ No (used internally by ripple)
- **SSE Emission**: ❌ No (output via ripple plan)
- **Tests**: ✅ EvalProvenanceTests.fs
- **Status**: DARK (pure logic, used internally)

### 14. **EvalRipple** — Cascade re-evaluation plan via topological sort.
- **Module**: SageFs.Features.EvalRipple
- **Types**: RippleStatus, RippleStep, RipplePlan
- **Functions**: toposort, planRipple
- **MCP Tool**: ✅ YES - plan_ripple
- **SSE Emission**: ❌ No (returns text plan)
- **Tests**: ✅ EvalRippleTests.fs
- **Status**: LIT (MCP tool)

### 15. **EvalTimeline** — Performance sparkline + percentile stats.
- **Module**: SageFs.Features.EvalTimeline
- **Types**: EvalStatus, TimelineEntry, TimelineState, TimelineStats
- **Functions**: sparkline, percentiles, record
- **MCP Tool**: ✅ YES - get_eval_timeline
- **SSE Emission**: ✅ YES - formatEvalTimelineEvent
- **Tests**: ✅ EvalTimelineTests.fs
- **Status**: LIT (MCP tool + SSE emission)

### 16. **Ghostwriter** — Type-directed suggestions for next cell.
- **Module**: SageFs.Features.Ghostwriter
- **Types**: ScopeBinding, Suggestion
- **Functions**: suggest, rankSuggestions
- **MCP Tool**: ✅ YES - suggest_next_cell
- **SSE Emission**: ❌ No (returns text)
- **Tests**: ✅ GhostwriterTests.fs
- **Status**: LIT (MCP tool)

### 17. **LiveTestingExecutors** — Attribute-based + custom test executors.
- **Module**: SageFs.Features.LiveTesting.LiveTestingExecutors
- **Types**: AttributeTestExecutor, CustomTestExecutor, DiscoveryResult, TestExecutor
- **Functions**: runTest, discoverTests
- **MCP Tool**: ❌ No (internal executor, exposed via run_tests)
- **SSE Emission**: ❌ No (output via test results)
- **Tests**: ✅ LiveTestingExecutorTests.fs
- **Status**: DARK (pure execution engine, data pumped via McpServer)

### 18. **LiveTestingInstrumentation** — OTEL activity/meter setup.
- **Module**: SageFs.Features.LiveTesting.LiveTestingInstrumentation
- **Types**: Histograms, Counters (ActivitySource, Meter)
- **Functions**: (static OTEL setup)
- **MCP Tool**: ❌ No (observability only)
- **SSE Emission**: ❌ No
- **Tests**: ❌ No dedicated test
- **Status**: DARK (pure observability, no business logic)

### 19. **LiveTestingTypes** — Domain types for tests, results, coverage, failures.
- **Module**: SageFs.Features.LiveTesting
- **Types**: TestCase, TestResult, TestSummary, FailureNarrative, CausalChange, TestId, etc.
- **Functions**: testStatusLabel, narrateFailure, etc.
- **MCP Tool**: ❌ No (data structures, exposed via get_live_test_status)
- **SSE Emission**: ✅ YES - formatTestSummaryEvent, formatTestResultsBatchEvent, formatFailureNarrativesEvent
- **Tests**: ✅ LiveTestingTypesTests.fs, LiveTestingCoreTests.fs
- **Status**: LIT (SSE emission in McpServer.fs)

### 20. **ManifestPersistence** — .sagefm v1 binary format I/O.
- **Module**: SageFs.Features.ManifestPersistence (ManifestTypes, ManifestWriter, ManifestReader)
- **Types**: ManifestSessionEntry, DaemonManifestData
- **Functions**: save, load, read, write
- **MCP Tool**: ❌ No (internal persistence)
- **SSE Emission**: ❌ No
- **Tests**: ✅ ManifestPersistenceTests.fs
- **Status**: DARK (pure I/O, no external wiring)

### 21. **MessageJournal** — Audit log of eval events (Debug/Info/Warn/Error).
- **Module**: SageFs.Features.MessageJournal
- **Types**: JournalLevel, JournalEntry, JournalState
- **Functions**: add, filter, format
- **MCP Tool**: ✅ YES - get_message_journal
- **SSE Emission**: ❌ No (returns formatted text)
- **Tests**: ✅ MessageJournalTests.fs
- **Status**: LIT (MCP tool)

### 22. **NotebookExport** — Cell metadata parsing/formatting for .fsx export.
- **Module**: SageFs.Features.NotebookExport
- **Types**: CellMetadata, CellMarker
- **Functions**: format, parse
- **MCP Tool**: ✅ YES - export_notebook
- **SSE Emission**: ❌ No (returns .fsx text)
- **Tests**: ✅ NotebookExportTests.fs
- **Status**: LIT (MCP tool)

### 23. **Replay** — Session state reconstruction from events (pure fold).
- **Module**: SageFs.Features.Replay
- **Types**: ReplayStatus, EvalRecord, SessionReplayState
- **Functions**: fold, recover
- **MCP Tool**: ❌ No (used internally for state recovery)
- **SSE Emission**: ❌ No
- **Tests**: ✅ ReplayTests.fs
- **Status**: DARK (pure event fold, internal use)

### 24. **ScratchPad** — Ephemeral snippets that don't pollute history.
- **Module**: SageFs.Features.ScratchPad
- **Types**: ScratchSnippet, ScratchPadState
- **Functions**: create, addSnippet, markResult, export
- **MCP Tool**: ✅ YES - manage_scratch_pad
- **SSE Emission**: ❌ No (returned as text)
- **Tests**: ✅ ScratchPadTests.fs
- **Status**: LIT (MCP tool)

### 25. **SessionFilmstrip** — Visual history of evaluations (frame-based).
- **Module**: SageFs.Features.SessionFilmstrip
- **Types**: FilmstripEvent, FilmstripFrame
- **Functions**: buildFilmstrip, filterFrames
- **MCP Tool**: ✅ YES - get_session_filmstrip
- **SSE Emission**: ❌ No (returned as text)
- **Tests**: ✅ SessionFilmstripTests.fs
- **Status**: LIT (MCP tool)

### 26. **SessionPersistence** — .sagefs v3 binary format I/O.
- **Module**: SageFs.Features.SessionPersistence (SessionBinaryTypes, SessionBinaryReader, SessionBinaryWriter)
- **Types**: InteractionKind, EntryFlags, RefKind, SessionBinaryData
- **Functions**: read, write
- **MCP Tool**: ❌ No (internal persistence)
- **SSE Emission**: ❌ No
- **Tests**: ❌ No (complex I/O, likely in integration tests)
- **Status**: DARK (pure I/O codec, no external wiring)

### 27. **SessionScribe** — Topological sort + dedup for script export.
- **Module**: SageFs.Features.SessionScribe
- **Types**: ScribeEntry
- **Functions**: dedup, toposort
- **MCP Tool**: ✅ YES - export_session_transcript
- **SSE Emission**: ❌ No (returns .fsx text)
- **Tests**: ✅ SessionScribeTests.fs
- **Status**: LIT (MCP tool)

### 28. **TestCachePersistence** — .sagetc v1 binary format I/O.
- **Module**: SageFs.Features.TestCachePersistence (TestCacheTypes, TestCacheFile, TestCacheMapping)
- **Types**: Outcome, CoverageEntry, ResultEntry, StcData
- **Functions**: save, load, toStruct
- **MCP Tool**: ❌ No (internal persistence)
- **SSE Emission**: ❌ No
- **Tests**: ❌ No (complex I/O, likely in integration tests)
- **Status**: DARK (pure I/O codec, no external wiring)

### 29. **TestNarration** — Human-readable failure narratives.
- **Module**: SageFs.Features.TestNarration
- **Types**: NarrationDetail
- **Functions**: statusLabel, narrateFailure, narrateOutcome
- **MCP Tool**: ❌ No (output via explain_test_failure)
- **SSE Emission**: ✅ YES (embedded in formatFailureNarrativesEvent)
- **Tests**: ✅ TestNarrationTests.fs
- **Status**: LIT (SSE emission via LiveTesting)

### 30. **WhatIf** — Preview hypothetical binding overrides.
- **Module**: SageFs.Features.WhatIf
- **Types**: WhatIfOverride, WhatIfPlan, WhatIfDiffResult
- **Functions**: createOverride, formatOverride, planWhatIf
- **MCP Tool**: ✅ YES - preview_what_if
- **SSE Emission**: ❌ No (returns text plan)
- **Tests**: ✅ WhatIfTests.fs
- **Status**: LIT (MCP tool)

---

## ROOT-LEVEL SageFs.Core FEATURE MODULES

### 1. **SessionEvents** — Typed SSE events for session lifecycle.
- **Module**: SageFs.SessionEvents
- **Types**: SessionEvent, SessionEventSubtype
- **Functions**: serializeSessionEvent, formatSessionSseEvent
- **MCP Tool**: ❌ No (infrastructure, auto-pushed)
- **SSE Emission**: ✅ YES (primary SSE producer for session events)
- **Tests**: ✅ (covered in end-to-end tests)
- **Status**: LIT (SSE infrastructure)

### 2. **TimeTravel** — Ring buffer snapshots for historical debugging.
- **Module**: SageFs.TimeTravel
- **Types**: TimeTravelMode, TimeTravelState<'Model>
- **Functions**: create, record, navigate, view
- **MCP Tool**: ❌ No (future feature, not exposed yet)
- **SSE Emission**: ❌ No
- **Tests**: ✅ TimeTravelTests.fs
- **Status**: DARK (infrastructure for future TUI time-travel, no MCP/SSE wiring)

---

## WIRING SUMMARY

### MCP-Wired Features (42 methods across 16 modules):
1. **send_fsharp_code** → EvalPipeline
2. **load_fsharp_script** → EvalPipeline
3. **get_recent_fsi_events** → Replay
4. **get_fsi_status** → DaemonHealth + SessionManager
5. **get_startup_info** → SageFsApp
6. **get_available_projects** → ProjectLoading
7. **reset_fsi_session** → SessionManager
8. **hard_reset_fsi_session** → SessionManager
9. **check_fsharp_code** → Diagnostics (checker)
10. **cancel_eval** → SessionManager
11. **get_completions** → AutoCompletion
12. **explore_namespace** → BindingExplorer (reflection)
13. **explore_type** → BindingExplorer (reflection)
14. **visualize_domain_model** → **DomainModelViz** ✅
15. **create_session** → SessionManager
16. **list_sessions** → SessionManager
17. **stop_session** → SessionManager
18. **switch_session** → SessionManager
19. **get_elm_state** → ElmDaemon
20. **get_live_test_status** → **LiveTestingTypes** ✅
21. **enable_live_testing** → LiveTestingCycle
22. **disable_live_testing** → LiveTestingCycle
23. **set_run_policy** → LiveTestingCycle
24. **set_test_timeouts** → LiveTestingCycle
25. **get_test_trace** → LiveTestingCycle
26. **run_tests** → LiveTestingCycle
27. **explain_test_run** → TestTreeSitter (dependency graph)
28. **query_test_coverage** → LiveTestingCycle
29. **get_file_coverage** → **CoverageInstrumenter** (via LiveTestingCycle)
30. **explain_test_failure** → **TestNarration** ✅
31. **decompose_pipeline** → **EvalLens** ✅
32. **diagnose** → **Diagnostician** ✅
33. **plan_ripple** → **EvalRipple** ✅
34. **preview_what_if** → **WhatIf** ✅
35. **suggest_next_cell** → **Ghostwriter** ✅
36. **get_session_filmstrip** → **SessionFilmstrip** ✅
37. **export_notebook** → **NotebookExport** ✅
38. **export_session_transcript** → **SessionScribe** ✅
39. **get_message_journal** → **MessageJournal** ✅
40. **get_eval_timeline** → **EvalTimeline** ✅
41. **manage_scratch_pad** → **ScratchPad** ✅
42. **get_eval_diff** → **EvalDiff** ✅

### SSE-Emitting Features:
1. **BindingExplorer** → formatBindingScopeMapEvent
2. **CellDependencyGraph** → formatCellDependenciesEvent
3. **DomainModelViz** → formatDomainModelEvent
4. **EvalDiff** → formatEvalDiffEvent
5. **EvalTimeline** → formatEvalTimelineEvent
6. **LiveTestingTypes** → formatTestSummaryEvent, formatTestResultsBatchEvent, formatFailureNarrativesEvent, formatFileAnnotationsEvent
7. **TestNarration** → (embedded in formatFailureNarrativesEvent)
8. **Diagnostician** → formatDiagnosisReadyEvent
9. **SessionEvents** → formatSessionSseEvent
10. **FeatureHooks** → (orchestrates above emissions)

### Dark/Pure-Logic-Only (Unexposed):
1. **AutoCompletion** (exposed via explore_type/explore_namespace)
2. **CoverageInstrumenter** (exposed via get_file_coverage)
3. **DaemonHealth** (exposed via get_fsi_status)
4. **DaemonPersistence** (internal I/O)
5. **Diagnostics** (exposed via check_fsharp_code)
6. **EvalDedup** (internal optimization)
7. **EvalProvenance** (used by EvalRipple)
8. **LiveTestingExecutors** (exposed via run_tests)
9. **LiveTestingInstrumentation** (OTEL only)
10. **ManifestPersistence** (internal I/O)
11. **Replay** (internal recovery)
12. **SessionPersistence** (internal I/O)
13. **TestCachePersistence** (internal I/O)
14. **TimeTravel** (infrastructure, not exposed)

---

## METRIC SUMMARY

| Category | Count |
|----------|-------|
| Total Feature Modules | 32 |
| MCP-Wired (direct or indirect) | 16 |
| SSE-Emitting | 9 |
| LIT (MCP + SSE) | 8 |
| LIT (MCP only) | 8 |
| LIT (SSE only) | 1 |
| DARK (pure/unexposed) | 14 |
| With Tests | 24 |
| Without Tests | 8 |

---

## KEY OBSERVATIONS

1. **Composition Pattern**: Core logic modules (EvalRipple, EvalProvenance, Ghostwriter) are dark-pure, exposed via MCP tools (plan_ripple, suggest_next_cell).

2. **SSE Hub**: Most SSE events generated via **FeatureHooks.fs**, which wraps feature modules (EvalDiff, CellDependencyGraph, EvalTimeline, BindingExplorer).

3. **Three-Tier Architecture**:
   - **Tier 1 (Pure)**: EvalDedup, Diagnostics, EvalLens, CoverageInstrumenter
   - **Tier 2 (MCP)**: Exposed as tools (decompose_pipeline, plan_ripple, etc.)
   - **Tier 3 (SSE)**: Pushed server-side (test results, eval diffs, bindings)

4. **Test Coverage**: 24/32 modules have tests. Notable gaps: AutoCompletion (covered in integration test), DaemonPersistence, Diagnostics, LiveTestingExecutors, LiveTestingInstrumentation, SessionPersistence, TestCachePersistence (likely I/O testing happens elsewhere).

5. **Wiring Entry Points**:
   - MCP: **McpTools.fs** (42 methods)
   - SSE: **McpServer.fs** (orchestrates pushes) + **FeatureHooks.fs** (coordinates emissions)
