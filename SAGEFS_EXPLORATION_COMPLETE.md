# SAGEFS - COMPREHENSIVE EXPLORATION

## EXECUTIVE SUMMARY

SageFs is a live F# interactive engine providing sub-500ms live testing, hot reload, and AI-native capabilities. It's a 
 free alternative to VS Enterprise Live Unit Testing (~\,000/year).

**Key Stats:**
- 207 test files across 13 categories
- 33 MCP tools for AI agents
- 220+ F# modules in Core
- Sub-500ms feedback loop
- Supports 6+ editors simultaneously

---

## 1. ARCHITECTURE OVERVIEW

### Three-Tier Model
1. **Elm Program** - Pure update/render (TestCase: time-travel debugging)
2. **Effect Handler** - Async side effects (Session ops, worker comm)
3. **Worker Process** - Isolated FSI per session (hot reload + tests)

### Directory Structure
- SageFs/ (CLI daemon)
- SageFs.Core/ (220+ modules)
- SageFs.Tests/ (207 test files)
- SageFs.Gui/ (Raylib GPU renderer)
- sagefs-vscode/ (VS Code extension)
- sagefs-vs/ (Visual Studio extension)
- samples/ (6 language bridges + demos)

---

## 2. DAEMON STARTUP

**Entry Point:** SageFs/Program.fs:139-378

Flow:
1. Parse CLI command (daemon/tui/gui/worker/jupyter)
2. Call DaemonMode.run(mcpPort, flags)
3. Create SessionManager (mailbox actor)
4. Create ElmDaemon runtime
5. Start MCP server (port 37749)
6. Start Dashboard server (port 37750)
7. Start FileWatcher
8. Run ElmLoop.start() → message pump

Arguments:
- --proj PATH: Load specific project
- --mcp-port PORT: Custom MCP port
- --supervised: Erlang-style watchdog
- --no-watch: Disable file watching
- --jupyter CONN.JSON: Jupyter kernel mode

---

## 3. MCP SERVER (33 Tools)

**File:** SageFs.Core/Mcp.fs (2000+ lines)

Categories:

Code Evaluation (5):
- send_fsharp_code, send_fsharp_code_file, eval_select, check_code, get_completions

Session Management (6):
- create_session, list_sessions, set_session, stop_session, get_session_info, reset_fsi_session

Live Testing (7):
- run_tests, list_tests, get_live_test_status, get_file_coverage, explain_test_run, explain_test_failure, get_cell_depen
ndencies

Diagnostics (7):
- get_diagnostics, discover_features, get_dependency_graph, get_type_info, get_call_graph, get_binding_explorer, get_eva
al_timeline

Infrastructure (8):
- get_daemon_health, get_version, get_system_status, get_feature_flags, get_api_version, hot_reload_toggle, subscribe_ev
vents, subscribe_to_stream

Protocol: Streamable HTTP (recommended) or SSE

---

## 4. SSE EVENTS

**File:** SageFs.Core/SseWriter.fs (600+ lines)

Event Types:

Test Lifecycle:
- test_summary: {Total, Passed, Failed, Running}
- test_results_batch: {Entries[], Summary}
- file_annotations: {Line coverage}
- failure_narratives: {TestId, LastPass, CausalChanges, Summary}
- test_source_locations: {File/line mappings}

Evaluation:
- eval_started, eval_completed, eval_diff, eval_timeline

System:
- warmup_progress, diagnostics_update, session_state, bindings_snapshot

Format:
  event: <type>
  data: <json>
  data: <more-if-multiline>

---

## 5. BINARY PERSISTENCE (.sagefs / .sagetc)

**File:** SageFs.Core/BinaryFormat.fs (400+ lines)

Format (.sagefs v3):
- Header (64 bytes): Magic "SFS3", version, CRC-32
- Section Directory: Tag → Offset/Size
- Sections: META, INPT, REFS, PROF, BIND

String Encoding:
- lp-string: u32(length) + u8[length]
- lp-string-option: 0xFFFFFFFF (None) | lp-string (Some)

Design Principles:
1. One format for disk + wire
2. Binary from day one (4-8× faster than JSON)
3. Section-based extensibility
4. CRC-32 at header + per-section
5. Schema compiled into reader
6. Validate early, trust late
7. Human-inspectable with xxd/hex tools

---

## 6. HOT RELOAD MECHANISM

**Files:** FileWatcher.fs, DevReload.fs, DevReloadMiddleware.fs

End-to-End:
1. FileWatcher detects .fs/.fsproj change (500ms debounce)
2. Determine action: Reload (.fs) or SoftReset (.fsproj)
3. Broadcast "Compiling..." via SSE
4. Send #load to FSI with hotReload=true
5. FSI generates new assembly
6. Harmony middleware fuzzy-matches + patches methods
7. Broadcast Reload → Browser auto-refreshes
8. On error: broadcast CompilationFailed → error overlay

Safety:
- Infinite-reload guard (sessionStorage counter)
- Error overlay shown in browser
- Idempotent injection (data-sagefs-injected)
- SSE retry on network hiccup
- SAGEFS_DEVRELOAD=0 to disable

---

## 7. LIVE TESTING & FAILURE NARRATIVES

**Files:** LiveTestingTypes.fs, LiveTestingExecutors.fs, CoverageInstrumenter.fs

3-Speed Feedback:
1. ~50ms: Tree-sitter detects test attributes (gutter markers)
2. ~350ms: FCS type-check + dependency graph (affected tests)
3. ~500ms: Execute affected tests (✓/✗ results)

Failure Narratives:
- Track last 10 outcomes (flakiness detection)
- Distinguish environmental vs property-based flakiness
- Auto-quarantine noisy tests
- Analyze causal changes (symbols/files)
- Generate human-readable summary

Test Categories & Run Policies:
- Unit: OnEveryChange
- Integration: OnSaveOnly
- Browser: OnDemand
- Property: OnEveryChange
- Benchmark: OnDemand

Flaky Classification:
- Insufficient: <3 samples
- Stable: ≤2 flips in 10 runs
- Environmental: ≥2 flips in 10 runs (auto-quarantine)
- PropertyCounterexample: FsCheck shrunk case

---

## 8. ERROR HANDLING

**File:** SageFs.Core/SageFsError.fs (150 lines)

Unified Error Type (39 cases):
- ToolNotAvailable(toolName, state, available)
- SessionNotFound, NoActiveSessions, AmbiguousSessions
- WorkerCommunicationFailed, WorkerSpawnFailed, WorkerTimeout
- EvalFailed, ResetFailed, HardResetFailed
- HotReloadFailed, HotReloadStateError
- DaemonStartFailed, PortInUse, SseConnectionError
- Unexpected(exn)

Patterns:
- Result<'T, SageFsError> everywhere (compiler enforces exhaustive matching)
- describe(error) → user-facing message
- Retry policy with exponential backoff
- Watchdog auto-restart for crashed workers
- Lock-free CQRS reads prevent blocking on errors

---

## 9. TEST SUITE (207 Files)

Categories:
- Daemon (5): DaemonHealthTests, DaemonIntegrationTests
- LiveTesting (7): LiveTestingCoreTests, LiveTestingCoverageTests
- Persistence (3): ManifestPersistenceTests
- HotReload (8): HotReloadTests, DevReloadTests
- Integration (4): E2E test coordination
- Property (12): FsCheck-based fuzzing
- Round Hardening (13): Edge case stress tests
- Other (155): Architecture, types, utils

Framework: Expecto + FsCheck + BenchmarkDotNet
Command: dotnet run [--all] [--compliance] [--benchmark]

---

## 10. README.md - NEW USER EXPERIENCE

Quick Start (5 minutes):
1. dotnet tool install --global SageFs
2. sagefs (start daemon)
3. Open VS Code → install extension
4. Edit .fsx + Alt+Enter
5. Tests run on save

Why Choose SageFs:
- Sub-500ms feedback (vs 5-30s in VS Enterprise)
- Works on broken code (Tree-sitter)
- Every editor (not just Visual Studio)
- Free (MIT licensed)
- AI-native (MCP protocol)

Language Bridges:
- Python, Jupyter, C#, Java, JavaScript, Rust
- 22 F# Koans (162 tests)

---

## 11. SPRINT 20 HIGHLIGHTS (CHANGELOG)

Major Features:
- Test source locations + MCP tools (list_tests, get_cell_dependencies)
- Failure narratives (MCP explain_test_failure tool)
- Composed test execution (FSI hook + project providers chained)
- VS Code inline decorations (Expected vs Actual)
- Visual Studio CoverageGlyphTagger MEF pipeline
- Neovim Telescope test source jump + narrative window
- OTel metrics (standby pool, file watcher)

Bug Fixes:
- MCP escapeJson now handles \b, \f, all control chars
- FSI rewriteInlineUse preserves indentation
- TestSourceLocations event now emitted

Breaking Changes:
- discoverTests() → discoverAll()
- SessionManager.create takes onTestDiscovery callback
- ~40 private modifiers removed (REPL testing)

---

## 12. DOCUMENTATION (docs/)

- README.md: Navigation hub
- live-testing-as-you-type.md: 3-speed pipeline
- session-isolation.md: Multi-project design
- why-fsharp.md: Language philosophy
- binary-format-spec.md: .sagefs/.sagetc wire format
- binary-format-benchmarks.md: 4-8× faster vs JSON
- architecture-graph.html: Component diagram

---

## 13. SAMPLES (samples/)

Learning Path:
- from-koans/: 22 exercises, 162 tests
- from-csharp/: Types, LINQ→pipes
- from-python/: Functions, recursion
- from-java/: OOP→FP
- from-javascript/: Async, higher-order
- from-rust/: Ownership, unions
- from-jupyter/: Cell eval
- demos/: RaylibHello, RaylibGame, WebappDatastar

Running:
- sagefs watch . (live feedback)
- dotnet run (execute)
- dotnet test (CI)

---

## 14. NEW USER END-TO-END

Installation (30 sec):
1. Prerequisites: .NET 10 SDK
2. dotnet tool install --global SageFs
3. sagefs

First 5 Minutes:
1. Install VS Code extension
2. Open .fsx file
3. Alt+Enter on expression → result inline
4. Save → tests run automatically

Daily Workflow:
- Morning: sagefs (start once)
- Edit: Alt+Enter for eval
- Save: Tests run (< 500ms)
- Hover: See Expected vs Actual
- Commit: All tests green ✓

---

## KEY INSIGHTS FOR DEVELOPERS

### Elm Architecture (Chesterton's Fence)
- Pure Update prevents time-travel bugs
- Render outputs RenderRegion list (diff-able)
- OnModelChanged callback enables different frontends
- No side effects in Update (compiler + tests enforce this)

### Session Isolation (Erlang Model)
- Each session = separate OS process
- Worker crashes don't kill daemon
- Standby pool swaps for instant recovery
- CQRS reads non-blocking (SessionManager snapshot)

### One Daemon, Many Clients
- FileWatcher runs once (shared state)
- MCP server handles 33 tools
- Dashboard streams live updates
- Clients can share sessions or use separate

### Error Handling (Result-based)
- 39 discriminated union cases
- describe() → user-facing message
- Compiler enforces exhaustive matching
- No bare catches anywhere

---

## NEXT STEPS

New Developers:
1. Read docs/README.md (Architecture Deep Dive)
2. Explore SageFs.Core/ElmDaemon.fs (entry point)
3. Trace test run: LiveTestingExecutors → Mcp → SseWriter
4. Add MCP tool (Mcp.fs line ~250)
5. Run tests: dotnet run [--all] (3-5 min)
6. Deploy: code --install-extension sagefs-*.vsix

New Features:
- Add SSE event type (SseWriter.fs)
- Add MCP tool (Mcp.fs)
- Add test category (LiveTestingTypes.fs)
- Update binary format (BinaryFormat.fs + version bump)

---

## SUMMARY STATISTICS

Component | Purpose | File | LOC
-----------|---------|------|-----
Daemon     | CLI entry | Program.fs | 380
Elm Loop   | Message pump | ElmLoop.fs | 400+
MCP Server | AI tools | Mcp.fs | 2000+
Live Testing | Test exec | LiveTestingExecutors.fs | 500+
Coverage   | IL instrumentation | CoverageInstrumenter.fs | 300+
Hot Reload | File watch | FileWatcher.fs | 200+
Persistence| Binary format | BinaryFormat.fs | 400+
SSE Events | Real-time updates | SseWriter.fs | 600+
Error Types | Unified error | SageFsError.fs | 150

Test Coverage:
- 207 test files
- 13 categories
- 3500+ tests
- FsCheck property-based
- BenchmarkDotNet perf

✅ Exploration complete!
___BEGIN___COMMAND_DONE_MARKER___0
PS C:\Code\Repos\SageFs>