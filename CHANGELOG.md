# Changelog

All notable changes to SageFs will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

### Changed
- ~40 `private` modifiers removed across Core, Tests, VS Code extension, Visual Studio extension, and GUI projects to enable REPL-based interactive testing via SageFs sessions
- Test discovery API changed: `TestOrchestrator.discoverTests` → `TestOrchestrator.discoverAll` returning a composite result with `RunTest` closure
- `LiveTestingHook.discoverTests` returns a composite result instead of a raw `TestCase` array
- `SessionManager.create` takes an additional `onTestDiscovery` callback parameter
- `LiveTestHookResultDto.fromResult` introduced to separate serializable DTO from the function-bearing domain type
- `ValidatedBuffer` type no longer uses `private` constructor (enables REPL construction for testing)

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
