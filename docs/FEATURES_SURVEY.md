# SageFs Feature Modules Survey

> **Stale — historical snapshot.** The module count below (33) was right when this survey was
> written and is wrong now: `SageFs.Core/Features/` has grown well past a hundred `.fs` files,
> and the directory listing (`ls SageFs.Core/Features/*.fs | wc -l`, which also misses the
> `MetadataDelta/` and `Tweak/` subfolders) is the only way to get the current number. The
> per-module wiring claims below are a snapshot of one day and several have since rotted, most
> visibly the `visualize_domain_model` entry, which is not an MCP tool and has no SSE emitter
> left. For the current MCP tool list, see [MCP Tools](mcp-tools.md). This document is kept for
> historical context, not as a live reference.

## Summary

Total Feature Modules: **33** in SageFs.Core/Features/ (as of this survey's last update — see stale-data note above)
Plus **2** root-level SageFs.Core modules with feature characteristics

---

## FEATURES/ DIRECTORY MODULES

### 1. **AutoCompletion** — Code completion via FuzzySharp ranking.
- **Module**: SageFs.Features.AutoCompletion
- **Types**: CompletionKind (enum), completion ranking
- **Functions**: label, rankByType, etc.
- **MCP Tool**: ❌ No direct tool. AutoCompletion has never been exposed over MCP (the survey
  once said it was reached "via explore_type/explore_namespace"; neither of those is a registered
  tool and neither has one).
- **SSE Emission**: ❌ No
- **Tests**: ❌ No dedicated test (covered in AutoCompletionAndEventsTests.fs)
- **Status**: DARK (pure logic, exposed through other tools)

### 2. **BindingExplorer** — Tracks FSI bindings, shadowing, references.
- **Module**: SageFs.Features.BindingExplorer
- **Types**: BindingInfo, BindingScopeSnapshot, CellInput
- **Functions**: parseBinding, buildScopeSnapshot, etc.
- **MCP Tool**: ❌ No. The scope map is pushed over SSE. (This entry used to say the data went
  "via SSE in explore_namespace"; `explore_namespace` is not a registered tool.)
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
- **MCP Tool**: ❌ No (health query is in get_session_status)
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
- **MCP Tool**: ❌ No. The survey once recorded `visualize_domain_model` here. It was never a
  registered `[<McpServerTool>]` and there is no HTTP route for it either; it is a retired name
  (`SageFs.Core/Affordances.fs`, `RetiredTool.VisualizeDomainModel`).
- **SSE Emission**: ❌ No. The `domain_model` event was deleted; `SseWriter.allSseEventTypes`
  does not carry it and `SageFs.Tests/LiveBindingsSseWiringTests.fs` says so in a test name.
- **Tests**: ✅ DomainModelVizTests.fs
- **Status**: DARK (the module survives; nothing calls it over the wire)

### 10. **EvalDedup** — REMOVED.
- It returned the previous result for identical code sent within 2 seconds, without
  running it. A REPL runs what it is sent, and the cache also stored failures as
  results, matched on a 32-bit hash, and never evicted. Sending the same
  side-effecting snippet twice now runs it twice
  (`McpToolExecutionTests`, "the same side-effecting code sent twice in a row RUNS twice").

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
- **MCP Tool**: ❌ No (read live-testing status via `get_session_status`)
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

### 26. **SessionPersistence** — .sagefs v3 per-session binary format (REMOVED).
- **Status**: REMOVED. The `SageFs.Features.SessionPersistence` module and the
  `.sagefs` per-session format had no production caller and were deleted. Durable
  session state now lives in the daemon manifest (`.sagefm` v1, see
  `ManifestPersistence`), replayed on startup to rebuild sessions.

### 27. **SessionScribe** — Topological sort + dedup for script export.
- **Module**: SageFs.Features.SessionScribe
- **Types**: ScribeEntry
- **Functions**: dedup, toposort
- **MCP Tool**: ✅ YES - export_session_transcript
- **SSE Emission**: ❌ No (returns .fsx text)
- **Tests**: ✅ SessionScribeTests.fs
- **Status**: LIT (MCP tool)

### 28. **TestCachePersistence** — .sagetc binary format I/O (writer format version 3).
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

### MCP-Wired Features

**This list is generated-checked, not hand-maintained.** `scripts/regenerate-feature-survey.fsx`
scans the tool registration in `SageFs/McpTools.fs`, `SageFs/Mcp.fs` and `SageFs/McpResources.fs`
and names any tool this page claims that no source registers. Run it after adding or renaming a
tool:

```
dotnet fsi scripts/regenerate-feature-survey.fsx
```

It carries a **negative control**: it first proves it can find tools that are definitely registered
(`send_fsharp_code`, `run_tests`, `list_tests`) and exits non-zero if it cannot. That control
exists because the script's first version scanned for a registration shape this codebase does not
use, found zero tools, and reported every tool on this page as unregistered — a probe that always
answers "not found" looks like a working probe and answers nothing.

The daemon registers **65** tools (measured live via `discover_features` on 2026-10-02). The tools
named below that are NOT registered — a reader chasing any of these will find nothing:

- `get_live_test_status` — retired; live-testing status is read via `get_session_status`
- `get_fsi_status` — superseded by `get_session_status`
- `create_session` — the `create_*_session` family takes its place
- `load_fsharp_script`, `get_completions`, `explore_namespace`, `explore_type` — not exposed over MCP
- `enable_live_testing`, `disable_live_testing`, `set_run_policy`, `set_test_timeouts`,
  `get_test_trace`, `query_test_coverage`, `get_file_coverage` — live testing is driven through
  `run_tests` / `list_tests` / `run_app` and the session's workflow
- `visualize_domain_model` — not exposed over MCP

Registered tools, by area:

1. **Eval and diagnostics**: send_fsharp_code, check_fsharp_code, cancel_eval, decompose_pipeline,
   get_recent_fsi_events, get_eval_diff, get_eval_timeline, get_message_journal,
   get_session_filmstrip, export_notebook, export_session_transcript, manage_scratch_pad,
   diagnose, targeted_verify, suggest_repair, preview_what_if, plan_ripple, impact_forecast,
   suggest_next_action, suggest_next_cell, get_cell_dependencies
2. **Sessions**: create_project_session, create_solution_session, create_bare_session,
   list_sessions, switch_session, stop_session, get_session_status, get_daemon_status,
   get_available_projects, reset_fsi_session, hard_reset_fsi_session, switch_workflow,
   list_runnable_projects, run_app, stop_app
3. **Testing**: list_tests, run_tests, explain_test_failure, coverage_intel, discover_features
4. **Hot reload**: enable_hot_reload, disable_hot_reload, set_reflection_read_mode,
   reset_hot_reload_state
5. **Cohort and landings**: join_cohort, leave_cohort, get_cohort_status, acquire_claim,
   release_claim, request_landing, reassign_claim, set_integration_ref, mint_member, revoke_member,
   delegate_conductor, withdraw_landing, veto_landing, resolve_veto
6. **Leases and workspace**: acquire_full_build_lease, acquire_test_suite_lease,
   acquire_run_app_lease, release_work_lease, get_workspace_hygiene, tidy_workspace,
   manage_local_data
7. **Friction**: report_friction, get_friction_report, get_friction_summary

### SSE-Emitting Features:
1. **BindingExplorer** → formatBindingScopeMapEvent
2. **CellDependencyGraph** → formatCellDependenciesEvent
3. **EvalDiff** → formatEvalDiffEvent
4. **EvalTimeline** → formatEvalTimelineEvent
5. **LiveTestingTypes** → formatTestSummaryEvent, formatTestResultsBatchEvent, formatFailureNarrativesEvent, formatFileAnnotationsEvent
6. **TestNarration** → (embedded in formatFailureNarrativesEvent)
7. **Diagnostician** → formatDiagnosisReadyEvent
8. **SessionEvents** → formatSessionSseEvent
9. **TestDiscovery** → formatTestSourceLocationsEvent
10. **FeatureHooks** → (orchestrates above emissions)

Entry 3 of the original list was DomainModelViz. Its `domain_model` event was deleted, and the
authoritative registry is `SseWriter.allSseEventTypes` (`SageFs.Core/SseWriter.fs`), which lists
22 event types and does not include `domain_model`.

### Dark/Pure-Logic-Only (Unexposed):
1. **AutoCompletion** (not exposed; the survey once pointed at explore_type/explore_namespace, which are not tools)
2. **CoverageInstrumenter** (per-file coverage is read over HTTP: `GET /api/live-testing/file-annotations`)
3. **DaemonHealth** (health is read via `get_daemon_status` and `get_session_status`)
4. **DaemonPersistence** (internal I/O)
5. **Diagnostics** (exposed via check_fsharp_code)
6. ~~EvalDedup~~ (removed)
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

This table used to carry counts of MCP-wired, SSE-emitting, LIT and DARK modules, plus a
with-tests / without-tests split. They are gone, and deliberately so. Every one was arithmetic
over the module list in this document, which is a snapshot of a day and no longer describes
`SageFs.Core/Features/`. They cannot be recomputed from anything, and a figure that reads as
measured but was counted against a list that no longer exists is worse than no figure.

Two counts below are the exception, because they can be measured from the tree right now:

| Category | Count | How to measure it |
|----------|-------|-------------------|
| Registered MCP tools | 65 | `RegisteredTools.describe typeof<SageFsTools>`, the same reflection `discover_features` is built from |
| SSE event types | 22 | `SseWriter.allSseEventTypes` in `SageFs.Core/SseWriter.fs` |

Counts of feature modules, MCP-wired modules, LIT/DARK modules, or tests per module are not
given here. Count them from the directory and the test project.

---

## KEY OBSERVATIONS

1. **Composition Pattern**: Core logic modules (EvalRipple, EvalProvenance, Ghostwriter) are dark-pure, exposed via MCP tools (plan_ripple, suggest_next_cell).

2. **SSE Hub**: Most SSE events generated via **FeatureHooks.fs**, which wraps feature modules (EvalDiff, CellDependencyGraph, EvalTimeline, BindingExplorer).

3. **Three-Tier Architecture**:
   - **Tier 1 (Pure)**: Diagnostics, EvalLens, CoverageInstrumenter
   - **Tier 2 (MCP)**: Exposed as tools (decompose_pipeline, plan_ripple, etc.)
   - **Tier 3 (SSE)**: Pushed server-side (test results, eval diffs, bindings)

4. **Test Coverage**: not counted here. The old "24/32 modules have tests" line contradicted the
   metric table's own "Total Feature Modules | 35" and was counted against the same stale list,
   so both are gone. Count tests from `SageFs.Tests/`.

5. **Wiring Entry Points**:
   - MCP: **McpTools.fs** (69 declared members, 65 of them registered; see below)
   - SSE: **McpServer.fs** (orchestrates pushes) + **FeatureHooks.fs** (coordinates emissions)

`McpTools.fs` declares more members than it registers. Four are declared, have working bodies,
and carry no `[<McpServerTool>]`, so `tools/list` does not have them:

| Member | Registered? | What to use instead |
|--------|-------------|---------------------|
| `load_fsharp_script` | No | Retired. Read and `send_fsharp_code` the blocks yourself; `#load` still works from inside a submitted script. |
| `get_startup_info` | No | `get_daemon_status` for daemon-wide facts, `get_session_status` for live session facts. |
| `get_elm_state` | No | Retired. Read the editor or dashboard state directly. |
| `explain_test_run` | No | Retired. `explain_test_failure` covers a test that went from passing to failing; for a run-level story read the `run_tests` receipt. |

All four are in `SageFs.Core/Affordances.fs` (`RetiredTool.toolNames`) except `get_elm_state`
and `explain_test_run`, which have no replacement mapping because the product never defined
one. Do not invent one.
