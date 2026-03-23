# SageTUI Lockup Investigation

## Symptom
The SageTUI-based TUI (default, non-legacy) **locks up** — no keyboard input, no rendering updates.

## Changes Made (v0.6.226)
1. Upgraded SageTUI from 0.9.3 → 0.9.4
2. Temporarily disabled `Program.withDebugger` to isolate the issue

## Hypotheses

### H1: SSE Connection Hangs
- The `sseSub` subscription calls `DaemonClient.runSseListener`
- `runSseListener` does a blocking `GetStreamAsync` call
- The `/api/state` endpoint in Dashboard.fs should immediately call `pushJson()` before subscribing to state changes (line 78 in createApiStateHandler)
- **Test**: Add logging to `runSseListener` to see if the connection establishes and if data arrives

### H2: SageTUI Render Loop Blocks
- The `App.run` call from SageTUI might be blocking on something internal
- Could be related to terminal size detection, initial render, or subscription setup
- **Test**: Add logging in `init`, `update`, and `view` to see which one never returns

### H3: Debugger Combinator Issue
- `Program.withDebugger` might have introduced a deadlock or blocking behavior
- Wraps model in `DebuggerModel<Model>` and messages in `DebuggerMsg<Msg>`
- **Status**: DISABLED in v0.6.226 — if lockup persists, this hypothesis is ruled out

### H4: Race Condition at Startup
- The pre-flight connectivity check (line 763 in SageTuiClient.fs) succeeds
- But between that check and the SSE connection, the daemon might not be fully ready
- **Test**: Add a small delay or retry logic after connectivity check

## Next Steps
1. **Reproduce locally**: `sagefs tui` with a fresh daemon
2. **Add instrumentation**: Log statements in `sseSub`, `init`, `update`, `view`
3. **Check SageTUI 0.9.4 CHANGELOG**: Review for any breaking behavioral changes (already reviewed — only additive)
4. **Bisect**: If needed, test with SageTUI 0.9.3 to confirm 0.9.4 isn't the cause
5. **Minimal repro**: Create a trivial SageTUI app with SSE subscription to isolate SageFs-specific issues

## Related Files
- `SageFs/SageTuiClient.fs` — TUI client implementation
- `SageFs.Core/DaemonClient.fs` — SSE listener (`runSseListener`)
- `SageFs/Dashboard.fs` — `/api/state` endpoint (`createApiStateHandler`)
- `C:\Code\Repos\SageTUI\Tea.fs` — CustomSub implementation

## Notes
- The legacy TUI (`TuiClient.fs`) is imperative and has 0% coverage — scheduled for removal in Phase A3
- The SageTUI-based TUI (`SageTuiClient.fs`) is the production path — **DO NOT REMOVE**
