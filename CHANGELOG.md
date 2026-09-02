# Changelog

All notable changes to SageFs will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **Historical frontend note:** Entries about SageTUI, the legacy TUI, and the `SageFs.Gui` Raylib frontend record features available at those release dates. Those built-in product frontends are now deprecated; current product interfaces are the web dashboard, editor integrations, and MCP. Raylib application and game demos remain supported examples and are not part of this deprecation.

## [Unreleased]

> **What's New**: Dashboard redesign aligned with **sagetech.dev** aesthetic
> (flat panels, sharp corners, tabline/statusline/cmdline), **per-client
> viewing session** made structurally impossible to get wrong, **per-project
> theme persistence** with path canonicalization, and **opt-in friction
> report submission** to a Cloudflare Worker + R2 destination you control.
> 6,778 tests green.

### Highlights

- 🎨 **Dashboard redesign** — flat panel aesthetic matching sagetech.dev: fixed top tabline (brand left, session status center, theme picker + expand toggle right), bottom statusline, bottom cmdline (`> SageFs --ready`), bracket-style buttons (`[CLEAR]`, `[EVAL]`, `[RESET]`, `[HARD_RESET]`), sharp corners everywhere, CRT scanline overlay, thin scrollbars, dynamic sidebar collapse/expand at the top of the sessions pane
- 🛡️ **Per-client viewing session (structural)** — removed `GetActiveSessionId` and `GetElmRegions` from `DashboardQueries`. The compiler now rejects any code that tries to read a daemon-global active session from the dashboard layer. Two browser tabs, an MCP client, and the TUI each have their own viewing session. The TUI switching sessions no longer changes what a dashboard tab is displaying
- 🎨 **Per-project theme persistence** — theme picker is per-client via the Datastar theme signal. Working-dir keys are canonicalized (`Path.GetFullPath` + lowercase on Windows + trim trailing separators), so `C:\Foo`, `c:\foo`, and `C:/Foo` all map to the same entry. 8 themes: One Dark, Kanagawa, Tokyo Night, Gruvbox, Catppuccin Mocha, Monokai, Dracula, Nordic
- 📡 **Opt-in friction reports** — new `friction-receiver/` Cloudflare Worker (TypeScript) that stores sanitized reports in R2, with an optional Discord webhook notification on each submit. Fully opt-in: no destination is configured by default, the user pastes their Worker URL into the dashboard, the user reviews and edits free-text fields before sending, nothing is ever sent automatically. Free tier covers everything (100k Worker req/day, 10GB R2, 10M R2 ops/month). F# side: new `FrictionSanitize` module, `sent_reports` table in the existing friction SQLite store, `POST /dashboard/friction/send` handler with SHA-256 endpoint hashing. 31 sanitization unit tests cover paths/IPs/emails/session-ids/round-trip. The dashboard UI drawer is a follow-up — the data path is wired end-to-end now

- 🚨 **"Lost" session state** — when the daemon's actor doesn't know about a session (worker process died), the sidebar shows it as "lost" with a yellow left border rather than hiding it as "stopped". Users can see which sessions need a restart

### Changed

#### Dashboard
- Replaced inline-JS header with structured tabline + statusline + cmdline
- Moved theme picker to tabline right side, removed redundant sidebar toggle from tabline
- Connection-status banner now uses `MutationObserver` on `#main` for accurate health signal (primary) plus a `fetch` interceptor (secondary fallback)
- `set-theme` handler reads from per-client `viewingSessionId` signal with no global fallback — prevents cross-client theme overwrites
- `pushState` patches the `theme` signal on every session switch AND every theme-name change so the picker always tracks the active session
- Initial `/dashboard` render honors `?session=` query param for deep-links
- `/api/state` JSON stream (for TUI) uses `?sessionId=` per-connection param instead of reading the global
- EvalStats avg/min/max now appears in the tabline (was an empty div)

#### Rendering
- `ElmDaemon.renderRegionsForSession` added — renders a specific session's `OutputRingBuffer` instead of the Elm runtime's global active session
- `overrideSessionStatuses` maps `SessionState.Uninitialized` → `"lost"` (was `"stopped"`)
- Sidebar keeps "lost" sessions visible (was hiding them as "stopped")

### Fixed

- **Friction report `send_fsharp_code`**: dashboard showed "Server disconnected" and stale "Uninitialized" state after inactivity. Connection banner now clears on every SSE push via MutationObserver; "Uninitialized" sessions are now clearly labeled as "lost" with a restart action
- **Per-client output coupling**: TUI switching sessions used to also change the dashboard's output panel. Now structurally impossible — output is sourced from the viewing session's buffer

### Developer Notes
- `AGENTS.md` now includes a "STOP" section at the top with three restatements of the rule against blocking on long-running daemon processes. The model repeatedly violated this despite prior corrections.

## [0.8.0] - 2026-03-10

> **What's New in 0.8**: This release focuses on **resilience**, **editor parity**, and the
> **SageTUI migration**. The daemon now catches its own crashes, errors are typed and actionable
> across all editors, and the terminal UI is rebuilt on a functional Elm Architecture. Every
> editor gained test source-jump (F12 on a failing test → its source) and failure narratives
> (why a test broke, what changed, when it last passed).

### Highlights

- 🏗️ **SageTUI migration** — Terminal UI rebuilt on [SageTUI](https://github.com/WillEhrendreich/sagetui)'s Elm Architecture. `sagefs tui` is now purely functional: `init/update/view/subscribe` with SIMD cell diff. Use `--legacy-tui` for the old renderer.
- 🩺 **`sagefs check`** — New pre-flight command validates .NET SDK, FSI, project files, port availability, and daemon state before first run. Actionable hints on every failure.
- 🛡️ **Typed errors everywhere** — `SageFsError.toJson` with 30 exhaustive error cases. All editors show structured errors with contextual rescue actions ("Retry", "Open Settings", "Switch Project").
- ⏱️ **Warmup progress** — 5-phase SSE events (`resolving` → `restoring` → `compiling` → `loading` → `ready`) so editors can show real progress bars instead of spinners.
- 🐛 **Eval watchdog** — Detects silent daemon crashes across VS Code, Visual Studio, TUI, and Raylib GUI. No more "it just stopped working" with no feedback.
- 🔗 **Test source-jump** — F12 / `<CR>` / Go-to-Definition on a test navigates to its source file and line. Works in all 6 frontends.
- 📖 **Failure narratives** — When a test breaks, see what changed (symbols, files), when it last passed, and a human-readable summary. In every editor.

### Added

#### CLI
- `sagefs check` — environment pre-flight command (SDK, FSI, ports, project, daemon)

#### Daemon
- Global error middleware replacing 23 per-endpoint `try/catch` wrappers with centralized `SageFsError` handling
- `ValidTimeout` DU for type-safe timeout validation with environment variable overrides (`SAGEFS_EVAL_TIMEOUT_MS`, `SAGEFS_TEST_TIMEOUT_MS`)
- 5-phase warmup progress SSE events for real-time session initialization feedback
- `SageFsError.toJson` with 30 exhaustive typed error cases for structured error responses
- Daemon stderr capture for detecting silent FSI process failures
- Multi-project picker for workspaces with multiple .fsproj files
- `GET /api/health` returns structured JSON: `{ version, apiVersion, features[], status, healthy }`
- `TestSourceLocation` type + `TestSourceResolver` module for test→file/line resolution
- `PaneId.Tests` discriminated union case + Tests pane renderer (TUI + Raylib GUI)
- Composed multi-provider test execution with fallthrough semantics
- `TestRunCompleted` event for signaling end of a test run batch
- On-demand test discovery via `GetTestDiscovery` / `InitialTestDiscovery` without full eval cycle
- Streaming test proxy endpoint forwarded from daemon to worker
- 3 new MCP tools — `list_tests`, `get_cell_dependencies`, `discover_features`
- OTel standby pool metrics, file watcher counter, `TraceBased` exemplar filter

#### Terminal UI
- **SageTUI migration** — `SageTuiClient.fs` with Model (~22 fields), Msg (~30 cases), CustomSub for SSE, Keys.bindWithMods, MouseSub, FrameTimingsSub
- Theme bridging: `hexToColor` converts `ThemeConfig` hex strings → SageTUI `Color.Rgb`
- `--legacy-tui` fallback preserves the old imperative renderer

#### VS Code
- `sagefs.nextFailingTest` / `sagefs.prevFailingTest` commands with `Alt+Shift+]` / `Alt+Shift+[` keybindings
- `file_annotations` SSE handler with coverage gutter bars and inline failure decorations
- `failure_narratives` SSE handler enriching test failure messages
- Eval watchdog with configurable timeout
- Contextual rescue actions in error notifications
- Walkthrough onboarding with 5 interactive steps
- `getFeatures()` helper for daemon capability checks

#### Visual Studio
- Next/Prev failing test navigation commands (`Alt+Shift+]` / `Alt+Shift+[`)
- `CoverageGlyphTagger` MEF pipeline rendering colored bars in editor gutter
- `TestStateTracker` with `ProcessSourceLocations` / `GetSourceLocation` for test→source navigation
- `failure_narratives` SSE processing for inline failure context enrichment
- Eval watchdog ported from VS Code
- `GetHealthAsync()` for `apiVersion` and `features[]` capability checks

#### Neovim
- Telescope `<CR>` jumps to test source location (falls back to run), `<C-g>` explicit source-jump
- `<C-d>` floating window showing failure narrative (Summary, TimeSinceLastPass, CausalChanges)
- SSE handlers for `test_source_locations` and `failure_narratives` events
- F# snippets (9 entries: `testlist`, `testcase`, `testprop`, `expeq`, `;;`, `matchresult`, `pipemap`, `runtests`)

#### Raylib GUI
- Test source-jump via `JumpToTest` pipeline (shared with TUI)
- Eval watchdog with crash detection

### Fixed
- MCP `escapeJson` handles `\b`, `\f`, and all control characters below `\u0020` (previously only `\\`, `"`, `\n`, `\r`, `\t`)
- FSI `rewriteInlineUseStatements` preserves indentation via `Substring` instead of `String.Replace`
- `TestSourceLocations` SSE event was computed but never emitted — wired `formatTestSourceLocationsEvent` + broadcast trigger
- Dashboard no-baseline test failures filtered from failing test list

### Changed
- `sagefs tui` now launches SageTUI-based Elm Architecture client by default
- TUI rendering pipeline: SageTUI `Element` tree → SIMD cell diff → terminal
- ~40 `private` modifiers removed to enable REPL-based interactive testing
- Test discovery API: `TestOrchestrator.discoverTests` → `TestOrchestrator.discoverAll` with `RunTest` closure
- `ValidatedBuffer` no longer uses `private` constructor

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
