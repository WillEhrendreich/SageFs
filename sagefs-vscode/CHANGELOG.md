# Changelog

## 0.6.43

### New Features
- **Coverage gutter bars** — Green/red/gray `▸`/`○` markers in the editor gutter showing test coverage health per line (AllPassing, SomeFailing, NoCoverage)
- **Inline failure decorations** — `⊘` markers showing test name and Expected/Actual diff, exception messages, and timeouts inline next to code
- **Failure narrative enrichment** — Test failure messages now include causal analysis via `ℹ️` summary: what changed, when it last passed, causal changes
- **Test source navigation** — Test Explorer items automatically link to source file and line number via `test_source_locations` SSE events
- **Evaluate & Advance** (`Shift+Enter`) — Evaluate current `;;` block and move cursor to the next
- **Evaluate All Blocks** (`Ctrl+Alt+Enter`) — Run every `;;` block in the file sequentially
- **Block navigation** — `Ctrl+Down` / `Ctrl+Up` to jump between `;;` code blocks
- **Cancel Evaluation** (`Ctrl+Shift+C`) — Cancel a long-running evaluation
- **Load Current Script** — Load the active `.fsx` file directly into the FSI session
- **Session Menu** — Quick-access menu combining all session operations
- **Export Session as .fsx** — Save current session state to a replayable script
- **Sessions sidebar** — View all sessions with inline switch/stop/reset actions
- **FSI Bindings browser** — View all current FSI bindings via QuickPick
- **Test Trace viewer** — Browse test cycle events
- **Density cycling** — Toggle Full → Normal → Minimal inline result display

### Improvements
- SSE event handling for `file_annotations`, `failure_narratives`, and `test_source_locations`
- FileAnnotationsListener module for parsing coverage and failure data from SSE events
- Failure presentation types: AssertionDiff, ExceptionMessage, Timeout, RawMessage
- Test status bar now shows background colors: red for failures, yellow for stale, clear for passing
- Eval performance sparkline in status bar with P50/P95/P99 percentiles
- Restart Daemon command added alongside Start/Stop

## 0.6.0

### New Features
- No VS Code-specific feature additions in this sprint

### Bug Fixes
- (Neovim) `eval_result` SSE event was silently dropped; now dispatches to the `SageFsEvalResult` autocmd (Neovim plugin fix, no VS Code change required)

## 0.5.461

### Bug Fixes
- Fixed status bar transient race condition: no longer clobbers fresh status with stale text
- Fixed CompletionProvider offset calculation: uses `doc.offsetAt()` instead of manual line splitting (fixes CRLF bug on Windows)
- Removed dead `getClient()` throwing getter

### Improvements
- Added `.vscodeignore` to keep VSIX package lean (excludes source, build artifacts, old .vsix files)
- Added `extensionKind: ["workspace"]` for Remote SSH / WSL / Codespaces support

## 0.5.460

### New Features
- Enriched code completions with type signatures (detail field from FCS)

### Improvements
- Graceful degradation: eliminated throwing getters in critical paths (refreshStatus, evalCore, withClient, openDashboard)

## 0.5.458

### New Features
- Extension icon for marketplace visibility
- `sagefs.logLevel` setting for output channel verbosity

### Bug Fixes
- Fixed Shift+Enter keybinding conflict with Jupyter/notebook extensions

### Improvements
- Status bar transient messages replace toast spam for command results
- Removed redundant startup toasts
- Fixed marketplace categories: Programming Languages, Testing, Linters
- Added LICENSE file (MIT)

## 0.5.457

### Bug Fixes
- Fixed `isRunning` false positive: HTTP 500 no longer counts as "running"
- Fixed `postCommand` crash on non-200 responses (guards HTTP status before JSON parse)
- Fixed TestCodeLens: clicking test results now runs tests (was empty command)
- Fixed test adapter debounce: increased from 500ms to 2000ms to prevent rapid-fire reruns
- Fixed timer leak: tree providers now clean up refresh timers on deactivate

### New Features
- Daemon crash detection: shows restart prompt when daemon stops unexpectedly

### Improvements
- Fixed Shift+Enter keybinding: no longer conflicts with Jupyter/notebook extensions
- Removed redundant toast notifications during startup
- Added extension icon for marketplace visibility
- Fixed marketplace categories: Programming Languages, Testing, Linters
- Added LICENSE file (MIT)
