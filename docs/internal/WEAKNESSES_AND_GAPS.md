# SageFs Weaknesses and Gaps Analysis

A comprehensive review of SageFs identifying UX gaps, error handling issues,
configuration discoverability problems, and resilience weaknesses.
Updated after Sprints 20–23 (v0.6.x).

---

## Resolved in v0.6–v0.7 (Sprints 20–23)

Items previously listed as critical gaps that have been addressed:

| # | Original Gap | Resolution | Sprint |
|---|-------------|-----------|--------|
| 1 | Error dialogs with no actionable guidance | Actionable error dialogs shipped for all editors; `SageFsError.suggestedAction()` provides recovery text for all 49 error cases | S20 |
| 2 | No docs/troubleshooting links | TROUBLESHOOTING.md created; error dialogs link to docs | S21 |
| 3 | Timeouts hardcoded, not configurable | 6 env var overrides (`SAGEFS_WARMUP_*`, `SAGEFS_PER_TEST_*`, `SAGEFS_BUILD_*`); `ValidTimeout` DU enforces 1s–10min range; per-test timeout runtime-configurable via MCP | S22–23 |
| 4 | No progress indication during warmup | 5-phase warmup progress SSE events (`creating_fsi` → `scanning_sources` → `loading_assemblies` → `opening_namespaces` → `finalizing`); status bar in all editors | S22 |
| 5 | Mid-eval crash loses result silently | Eval watchdog detects daemon crash during eval (VS Code S21, Neovim S21, VS S23, TUI S23); monotonic ID prevents phantom interruption dialogs | S21–23 |
| 6 | No version compatibility check | Version gate: VS Code checks `apiVersion` on `/health`; mismatch shows actionable update instructions | S22 |
| 7 | Auto-picks wrong project silently | `DaemonTargetFinder` deterministic priority: `.slnx` > `.sln` > test `.fsproj` > first alphabetically; Neovim `:SageFsSwitchProject` for manual override | S21 |
| 8 | No health check command | Health check in all editors; `/api/health` returns typed `SageFsError` with `toJson()` including `suggestedAction` | S20–22 |
| 9 | Daemon stderr not captured | VS Code and VS capture daemon stderr on startup; shown in timeout error dialog | S21 |
| 10 | No error middleware | `wrapErrorMiddleware` catches eval pipeline exceptions; converts to safe `EvalResponse` | S23 |
| 11 | `isClientError` misclassified errors | Bug fixed — correctly distinguishes 4xx client errors from server/gateway errors | S22 |

---

## REMAINING WEAKNESSES

### 1. SSE RECONNECTION GAPS

**SSE stream does not replay missed events.** When an SSE connection drops and
reconnects, events emitted during the disconnection window are lost. No
`Last-Event-Id` / replay mechanism exists.

**Impact**: Test results or eval responses emitted while the editor is
disconnected never appear. The user must re-trigger the action.

**Workaround**: Poll `/api/live-testing/status` or `/api/health` after
reconnection to resync state.

**VS Extension SSE errors still only in Debug output.** The VS extension's
`SseClient.cs` logs reconnection errors to `System.Diagnostics.Debug.WriteLine`
— invisible unless a debugger is attached. VS Code and Neovim have better
reconnection UX.

### 2. VS EXTENSION FEATURE PARITY

The Visual Studio extension still lags behind VS Code and Neovim:

| Feature | VS Code | Neovim | Visual Studio |
|---------|:-------:|:------:|:-------------:|
| Eval history / timeline | ✅ | ✅ | 🔜 |
| Export session as .fsx | ✅ | ✅ | 🔜 |
| Run policy configuration | ✅ | ✅ | 🔜 |
| Coverage gutter signs | ✅ | ✅ | 🔜 |
| Code completions | ✅ | ✅ | 🔜 |
| Dependency graph view | ✅ | ✅ | 🔜 |
| Version gate | ✅ | ➖ | 🔜 |
| Getting Started walkthrough | ✅ | ➖ | 🔜 |
| Settings UI | ✅ | ✅ | ❌ |

### 3. CONFIGURATION DISCOVERABILITY

**VS Code — still missing:**
- No JSON schema file for settings (no IDE autocomplete for `sagefs.*`)
- No `sagefs.daemonStartupTimeout` setting (must use env var)
- No `sagefs.hotReloadEnabled` toggle (always on)
- No `sagefs.testCategoryPolicy` setting

**VS Extension — NO Settings UI.** All configuration requires env vars.

**Env var documentation gap:** The 6 timeout env vars exist in the code but are
only documented in TROUBLESHOOTING.md, not in `sagefs --help` or editor
settings descriptions.

### 4. INSTALLATION & ONBOARDING

- No `sagefs --check-environment` diagnostic command
- No `.fsproj` validation on startup (empty folder fails silently)
- First-time warmup (30–60s) documented in TROUBLESHOOTING.md but still
  surprises non-VS-Code users — walkthrough is VS Code only
- Neovim has no Getting Started walkthrough equivalent

### 5. RAYLIB GUI GAPS

- Raylib GUI connects to an existing daemon but cannot spawn one — user must
  start daemon separately via CLI or editor
- No keybinding reference within the GUI window
- No settings/preferences UI

---

## CURRENT GAPS SUMMARY

| Category | Issue | Severity |
|----------|-------|----------|
| Resilience | SSE reconnect doesn’t replay missed events | HIGH |
| Resilience | VS SSE reconnect errors only in Debug output | MEDIUM |
| Feature Parity | VS extension missing 9 features vs. VS Code | HIGH |
| Configuration | No JSON schema for VS Code settings | LOW |
| Configuration | VS extension has no Settings UI | MEDIUM |
| Configuration | Env var timeouts not in `--help` output | LOW |
| Onboarding | No `--check-environment` diagnostic | MEDIUM |
| Onboarding | First-time warmup surprise (non-VS-Code editors) | MEDIUM |
| Raylib GUI | Cannot spawn daemon; no in-app keybinding help | LOW |

## TOP 5 RECOMMENDATIONS

1. ~~Add health check command~~ ✅ Done — all editors
2. ~~Improve error dialogs~~ ✅ Done — `SageFsError.suggestedAction()` covers 49 cases
3. ~~Make timeouts configurable~~ ✅ Done — 6 env vars + `ValidTimeout` DU
4. Implement SSE `Last-Event-Id` replay to prevent lost events on reconnect
5. Close VS extension feature gap (eval history, coverage gutters, settings UI)
