# Changelog

All notable changes to SageFs will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `GET /api/health` now returns structured JSON: `{ version, apiVersion, features[], status, healthy }` — clients should check `apiVersion` before using coverage-intel and impact-forecast features
- `apiVersion: 1` — protocol version exposed on `/health` (mirrors `/version`); used by VS Code, Visual Studio, and Neovim clients for capability checks
- `getFeatures()` helper in VS Code extension client — returns daemon feature list from health response
- `GetHealthAsync()` in Visual Studio extension client — parses `apiVersion` and `features[]` from `/health` response
- Neovim `health_check` stores `api_version` and `features` in `M.state` after successful connection
- `TestSourceLocation` type + `TestSourceResolver` module for test→file/line resolution
- `PaneId.Tests` discriminated union case + Tests pane renderer (TUI + Raylib GUI)
- `TogglePane of PaneId` type-safety sweep across UiAction/TerminalCommand/RaylibMode
- BenchmarkDotNet ActionPrioritizer benchmark suite
- FsCheck property tests for CoverageIntel
- 20 unit tests for Tests pane renderer (TestsPaneTests.fs)
- 3 new MCP tools — `list_tests` (query tests by pattern/file), `get_cell_dependencies` (staleness-annotated dependency graph), `discover_features` (context-aware feature suggestions)
- 3 new feature modules — `TestDiscovery`, `CellDependenciesReport`, `FeatureDiscovery`
- VS Code — `file_annotations` SSE handler with coverage gutter bars (green/red/gray `│`) and inline failure decorations (`⊘ testName — Expected: x Actual: y`)
- VS Code — `failure_narratives` SSE handler enriching test failure messages with Summary/CausalChanges
- Visual Studio — `CoverageGlyphTagger` MEF pipeline (tag + tagger + provider + factory) rendering 3×16px colored bars in editor gutter
- Visual Studio — `TestStateTracker` with `ProcessSourceLocations`/`GetSourceLocation` for test→source navigation
- Visual Studio — `failure_narratives` SSE processing for inline failure context enrichment
- Neovim — Telescope `<CR>` now jumps to test source location (falls back to run), `<C-g>` explicit source-jump
- Neovim — `<C-d>` floating window showing failure narrative (Summary, TimeSinceLastPass, CausalChanges)
- Neovim — SSE handlers for `test_source_locations` and `failure_narratives` events

### Added
- Composed multi-provider test execution — `RunTest` closures from multiple providers (FSI hook + project-level) are chained with fallthrough semantics so the first provider that can run a test wins
- `TestRunCompleted` event for signaling the end of a test run batch
- `GetTestDiscovery` worker message and `InitialTestDiscovery` worker response for on-demand test discovery without a full eval cycle
- Test discovery callback wiring in DaemonMode — worker discoveries flow into the Elm model via `TestsDiscovered` and `ProvidersDetected` events
- Streaming test proxy endpoint forwarded from daemon to worker
- OTel standby pool metrics: `sagefs.standby.pool_size`, `sagefs.standby.warmup_ms`, `sagefs.standby.invalidations_total`, `sagefs.standby.age_at_swap_ms`
- OTel file watcher counter: `sagefs.filewatcher.changes_total`
- OTel exemplar filter set to `TraceBased` on both MCP and worker metric pipelines

### Fixed
- MCP `escapeJson` now uses `StringBuilder` and correctly handles `\b`, `\f`, and all control characters below `\u0020` (previously only handled `\\`, `"`, `\n`, `\r`, `\t`)
- FSI `rewriteInlineUseStatements` preserves indentation correctly via `Substring` instead of `String.Replace`, which could corrupt lines containing the substring "use " in non-keyword positions
- `TestSourceLocations` SSE event was computed but never emitted — added `formatTestSourceLocationsEvent` in SseWriter.fs + broadcast trigger in McpServer.fs

### Changed
- ~40 `private` modifiers removed across Core, Tests, VS Code extension, Visual Studio extension, and GUI projects to enable REPL-based interactive testing via SageFs sessions
- Test discovery API changed: `TestOrchestrator.discoverTests` → `TestOrchestrator.discoverAll` returning a composite result with `RunTest` closure
- `LiveTestingHook.discoverTests` returns a composite result instead of a raw `TestCase` array
- `SessionManager.create` takes an additional `onTestDiscovery` callback parameter
- `LiveTestHookResultDto.fromResult` introduced to separate serializable DTO from the function-bearing domain type
- `ValidatedBuffer` type no longer uses `private` constructor (enables REPL construction for testing)

## [0.7.0] - 2026-03-10

### Added

#### Core Daemon
- `/api/live-testing/run` now returns `{success: bool, reason: string option, message: string}` — `success` reflects the actual outcome; `reason` is `"live_testing_disabled"` or `"no_session"` when applicable (previously hardcoded `success: true`)

#### Visual Studio Extension
- Daemon auto-start on VS activation — `DaemonTargetFinder` scans for `.slnx` → `.sln` → `.fsproj` and starts the daemon automatically when VS opens; polls up to 10s for readiness
- `IVsStatusbar` MEF interop — `StatusBarBridge` wires the net472 MEF `StatusBarService` to the net8.0 SDK `StatusBarManager`; the VS status bar now shows real connection and test-pass state
- `CheckVersionAsync` — hard stop with actionable error message if daemon `apiVersion` mismatches extension expectations; message includes exact versions and `dotnet tool update` instructions

#### Infrastructure
- `smoke-test.yml` CI workflow — runs end-to-end smoke test on `windows-latest` on every push to master affecting `SageFs/`, `SageFs.Core/`, or `scripts/smoke-test.ps1`
- `smoke-test.ps1` — 30-second test-discovery poll loop with `WARN` vs `FAIL` distinction; 0 tests after timeout is a warning, not a pipeline failure

### Fixed

#### Visual Studio Extension
- `DaemonTargetFinder` deterministic project selection: `.slnx` > `.sln` > test `.fsproj` > first `.fsproj` alphabetically — no more silent wrong-project guessing
- `StartDaemonCommand` now delegates to `DaemonTargetFinder` instead of duplicated inline logic (~20 lines removed)

## [0.6.0] - 2026-03-10

### Added

#### Core Daemon
- `LastDiscoveryTime` timestamp in `LiveTestState` for reliable discovery-complete detection after assembly reload

#### Visual Studio Extension
- FSI IntelliSense completions — `IAsyncCompletionSourceProvider` MEF export with `working_directory` context, 14 completion-kind mappings, and a 3-second request timeout
- Gutter test-status markers seeded on startup via `InitialStatePoll` — glyphs now populate immediately on VS load, not only after the first SSE event
- TypeExplorer auto-refresh on `WarmupContextSnapshot` SSE event; daemon reachability guard shows "⚠ not running" instead of hanging indefinitely
- `Alt+Enter` smart eval — evaluates the current selection if one exists, or the block around the cursor otherwise
- `EvalBlockCommand` accessible via the Extensions menu
- Startup health check writes daemon status to the SageFs Output channel on VS load
- VSIX Marketplace metadata — icon, preview image, description, and tags
- CI now runs both test suites (`net472` + `net8.0`) on every push
- `SageFsOptions.DaemonUrl` is now configurable and live (was previously dead code)

#### Neovim Plugin
- VHS screenshot/GIF infrastructure — 11 tape scripts with a CI auto-regeneration workflow
- vimdoc expanded to 37 keymaps (was 6 stubs); all 29 SSE autocmd events documented

### Fixed

#### Core Daemon
- `run_tests` hot-reload timing race — now waits for test-discovery refresh after assembly reload, not just FSI idle state; eliminates stale results when `run_tests` is called immediately after editing a test
- Expecto `testProperty` (AsyncFsCheck, tag 3) was silently reported as "Passed" without FsCheck running; now correctly executes `FSharpAsync<bool>` property tests
- FsCheck.Xunit `[<Property>]` methods returning `Property<T>` were silently passing without FsCheck running; now routes through `FsCheck.Check.QuickThrowOnFailure`

#### Visual Studio Extension
- Completions `working_directory` was an empty string; now correctly passed from the active document path
- No HTTP timeout on completions requests; now enforced at 3 seconds
- TypeExplorer hung indefinitely if the daemon was not running
- 8 bare `catch` blocks across the extension now log to `Debug.WriteLine`
- Port configuration wired end-to-end (Tools → Options → SageFs → Daemon URL)

#### Neovim Plugin
- `eval_result` SSE event was silently dropped; now dispatches to the `SageFsEvalResult` autocmd
- `density` config valid values corrected in documentation

## [0.5.19] - 2026-02-20

### Added
- MCP push notifications via McpServerTracker + EventAccumulator + CallToolFilter — agents no longer need to poll

### Performance
- Benchmarked hot paths in FSI: CellGrid, cleanStdout, TUI emit, JSON serialize, sprintf cache keys
- Identified 8× total frame speedup opportunity; implementation starting this release

## [0.5.17] - 2026-02-19

### Fixed
- MCP session routing now prioritizes `working_directory` over cached agent session, fixing multi-project workflows where all commands routed to the first session
- Replaced named pipe transport with HTTP (Kestrel) transport, eliminating hangs when `get_fsi_status` was called during long-running evaluations

### Changed
- Worker processes now communicate over HTTP instead of named pipes
- Each worker starts a Kestrel server on an OS-assigned port, enabling concurrent request handling

## [0.5.15] - 2026-02-18

### Added
- Initial public release as a .NET global tool
- F# REPL with FSI session management
- MCP server for AI assistant integration
- TUI and Raylib GUI dual-renderer architecture
- Multi-session support with per-project isolation
- Hot reloading of F# source files
- Syntax highlighting via tree-sitter
- Expecto test runner integration
