# Changelog

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
