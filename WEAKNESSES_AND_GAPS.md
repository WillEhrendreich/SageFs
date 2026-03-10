# SageFs Weaknesses and Gaps Analysis

A comprehensive review of SageFs identifying UX gaps, error handling issues, configuration discoverability problems, and resilience weaknesses.

## 1. ERROR MESSAGES WITHOUT ACTIONABLE GUIDANCE

### VS Code Extension

**File:** sagefs-vscode/src/Extension.fs (lines 624, 656, 813)

#### Error 1: "SageFs daemon failed to start after 120s"
`
showErrorMessage "SageFs daemon failed to start after 120s." 
    [| "Retry"; "Show Output"; "Check Installation" |]
`
GAPS:
- No indication of WHY it failed (missing dotnet SDK? Wrong .NET version? .fsproj not found?)
- "Check Installation" button does NOTHING (no guidance provided)
- User must infer from raw output logs
- No link to troubleshooting docs

#### Error 2: "Cannot reach SageFs daemon"
`
showErrorMessage "Cannot reach SageFs daemon. Is it running?" 
    [| "Show Output"; "Restart" |]
`
GAPS:
- Assumes user knows what "running" means
- No option to check daemon health/status
- If daemon crashed mid-eval, no hint about recovery
- No indication daemon may have crashed (vs. slow startup)

#### Error 3: "SageFs not activated"
`
showErrorMessage "SageFs not activated." [| "Retry"; "Show Output" |]
`
GAPS:
- Does NOT explain what "activated" means
- No hint that opening an F# project is required
- No suggestion to check if .fsproj exists

### VS Extension

**File:** sagefs-vs/SageFs.VisualStudio/Commands/DaemonCommands.cs

#### Error 4: "No solution is open"
`
await output.WriteLineAsync("✗ No solution is open. Open a solution first, then start the daemon.");
`
GAPS:
- Written ONLY to output channel (hidden by default)
- No pop-up notification shown
- User must actively check output pane to see error

#### Error 5: DaemonTargetFinder error
From DaemonTargetFinder.fs: "No F# projects found. Open a folder with .fsproj files first."
GAPS:
- Only in output pane
- No UI notification
- Multi-project solutions: silently picks first test project or first alphabetically
- User never offered choice

### General Pattern
NONE of these messages offer:
- Links to docs/troubleshooting
- Automatic diagnostic capture (dotnet version, SDK info)
- Recommended next steps (only passive button list)
- Copy-paste commands to try
- Links to dashboard/logs


## 2. TIMEOUT/RETRY PATTERNS AND SLOW MACHINE HANDLING

### Timeout Values

**sagefs-vscode/src/Extension.fs:**
- Line 619: ttempts > 120 → 120 seconds max for daemon startup (1s polling)
- Line 707: ttempts < 30 → 60 seconds max for session warmup (2s polling)

**SageFs/Program.fs:**
- TimeSpan.FromSeconds(3.0) → 3s HTTP timeout (will fail on slow machines with 30s startup)

**sagefs-vs/SageFs.VisualStudio.Editor/SseClient.cs:**
- Timeout = TimeSpan.FromSeconds(75) → 75s HTTP timeout
- Backoff: 1s, 2s, 4s, 8s, 16s, 30s (exponential with cap)

### Problems
1. **Not configurable** — Users on slow machines cannot adjust timeouts
2. **No progress indication** — During 120s wait, only timestamp shown
3. **Silent failures** — Error shown AFTER 2 minutes of waiting
4. **No cancellation** — User cannot interrupt wait
5. **No health check** — Cannot distinguish "stuck" from "just slow"
6. **Binary retry** — Only "Retry" (from start) or nothing

### Missing Features
- No sagefs.daemonStartupTimeout config
- No verbose startup logging option
- No "Check daemon status" command (fast ping)


## 3. CONFIGURATION DISCOVERABILITY

### VS Code Settings (package.json)

**Exists:**
- mcpPort (default 37749)
- dashboardPort (default 37750)
- autoStart (default true)
- projectPath (empty)
- logLevel (error/info/debug)
- inlineResultTimeout (30000ms)
- cellHighlight (true)
- density (full/normal/minimal)
- typeExplorerRoot (empty)

**MISSING:**
- No JSON schema file (no IDE autocomplete)
- No enum descriptions for logLevel
- No sagefs.daemonStartupTimeout (hardcoded 120s)
- No sagefs.enableSessionWarmupLogging
- No sagefs.hotReloadEnabled (always on)
- No sagefs.testCategoryPolicy

**Environment variables exist but undocumented:**
- SageFs_MCP_PORT
- SAGEFS_BIND_HOST

### VS Extension
**NO Settings UI found** — All configuration is hardcoded.


## 4. DAEMON CRASH MID-EVAL: ERROR RECOVERY PATH

### SSE Reconnection (sagefs-vs/SageFs.VisualStudio.Editor/SseClient.cs)

`csharp
catch (Exception ex) {
    System.Diagnostics.Debug.WriteLine(
        $"[SageFs] SSE reconnect error: {ex.GetType().Name}: {ex.Message}");
}
`

GAPS:
- Exception ONLY logged to Debug output (not user-facing)
- If daemon crashes during eval, UI hangs silently
- No partial recovery tracking (eval result lost)
- Reconnect just retries stream, doesn't verify daemon responsive
- Timing ambiguous (network issue vs. daemon crash?)

### What Happens When Daemon Crashes Mid-Eval
1. Daemon crashes
2. SSE stream ends
3. SseClient catches exception, logs to Debug only
4. Starts exponential backoff (1s, 2s, ... 30s)
5. UI shows NO feedback (user assumes still evaluating)
6. Eval result NEVER appears (no timeout shown)
7. After 30s backoff, reconnects
8. Old eval command times out server-side, stale state


## 5. FIRST PROJECT DETECTION AND MULTI-PROJECT SOLUTIONS

### DaemonTargetFinder Logic (sagefs-vs/SageFs.VisualStudio.Core/DaemonTargetFinder.fs)

With 10+ .fsproj files:
1. Looks for one with "Test" in name
2. If not found, picks fsproj.[0] (first alphabetically)
3. User NEVER sees the choice
4. If alphabetically first is wrong, user must manually set sagefs.projectPath

### Problems
- Silent selection with no prompt
- Heuristic is fragile (misses Suite.fsproj, Specs.fsproj, E2E.fsproj)
- No persistence (repicks on every workspace open)
- No workspace setting to save choice

### Scenario: 15-project solution
Alphabetically first is probably Benchmarks.fsproj. User starts daemon and all evals run against benchmark code, not the app.

**Result: User confused why evals run against wrong project.**


## 6. INSTALLATION VERIFICATION: END-TO-END

### Documented Prerequisites
File: Readme.md (line 75)
`
**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). That's it.
`

### Gaps
1. No version info (what if user has .NET 6? Too old?)
2. No installation verification tool
3. No sagefs --check-environment diagnostic
4. No .fsproj validation (user can open empty folder, fails silently)
5. No version compatibility check (if CLI version 0.5 + extension 0.6, no warning until error)
6. Warmup requirements undocumented (30-120s first session) — users expect instant startup
7. No progress during warmup
8. VS Extension has no Settings page


## CRITICAL GAPS SUMMARY

| Category | Issue | Severity |
|----------|-------|----------|
| Error Messages | 7 dialogs with no actionable guidance | HIGH |
| Error Messages | No docs/troubleshooting links | HIGH |
| Timeouts | Hardcoded 120s, not configurable | MEDIUM |
| Timeouts | No progress indication | MEDIUM |
| Timeouts | 3s HTTP timeout fails on slow machines | MEDIUM |
| Configuration | No JSON schema | LOW |
| Configuration | VS 2022 has NO Settings UI | HIGH |
| Resilience | SSE errors only in Debug output | HIGH |
| Resilience | Mid-eval crash loses result silently | CRITICAL |
| Project Detection | Auto-picks wrong project silently | HIGH |
| Installation | No verification tool | MEDIUM |
| Installation | No version compatibility check | MEDIUM |

## TOP 5 RECOMMENDATIONS

1. Add health check command showing version, .NET info, daemon status
2. Improve error dialogs: add docs links, specific next steps, diagnostic info
3. Make timeouts configurable with UI slider
4. Show dialog for multi-project solutions
5. Show notification on SSE disconnect with "Restart Daemon" button
