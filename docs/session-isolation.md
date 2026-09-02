# Session Isolation Architecture

> **Status note:** This document records the design that introduced per-client session routing. Current product clients are editor integrations, dashboard tabs, and MCP connections. The built-in TUI was part of the original implementation context but is now deprecated.

## Problem

A single `activeSessionId: string ref` in `DaemonMode.fs` was shared across all clients. When one client switched sessions, other clients could be forced to switch:

```
MCP client calls switch_session("agent-session")
  → sets activeSessionId.Value
  → dispatches SessionSwitched to Elm
  → Elm pushes new state via SSE
  → ALL dashboard browsers switch to the agent session
  → A user viewing another session in the browser is forced away
```

### Secondary: Dashboard Connectivity

When the daemon goes down (e.g., during pack/install cycle), the dashboard shows no indication of disconnection. The `serverConnected` Datastar signal was set server-side and stays `true` even after the server dies. Users sit wondering why nothing is happening.

### Secondary: Session Resume Visibility

During startup, sessions resume sequentially but the dashboard only gets the full session list after ALL sessions have finished resuming. Users see an empty session list for several seconds.

## Design: Per-Client Session Routing

### Core Principle

**Each client connection maintains its own "active session" independently.** No client's session switch affects another client.

### Independent Session Scopes

```
┌─────────────────────────────────────────────────────┐
│ Daemon (DaemonMode.fs)                              │
│                                                     │
│  defaultSessionId: string ref                       │
│  (used only for "what session should new clients    │
│   default to?" — never for routing)                 │
│                                                     │
│  ┌─────────────┐  ┌──────────────┐  ┌───────────┐  │
│  │ MCP Conn 1  │  │ Dashboard    │  │ Editor    │  │
│  │ activeId: A │  │ Browser 1    │  │ activeId  │  │
│  │             │  │ activeId: B  │  │ C         │  │
│  ├─────────────┤  ├──────────────┤  └───────────┘  │
│  │ MCP Conn 2  │  │ Dashboard    │                  │
│  │ activeId: B │  │ Browser 2    │                  │
│  │             │  │ activeId: A  │                  │
│  └─────────────┘  └──────────────┘                  │
└─────────────────────────────────────────────────────┘
```

### MCP: Per-Connection (Already Correct Scope)

Each MCP stdio connection gets its own `McpContext` with its own `ActiveSessionId: string ref`. The fix is to stop `switchSession` from side-effecting the Elm loop:

**Before** (Mcp.fs:907-924):
```fsharp
let switchSession ctx sessionId = task {
  let! info = ctx.SessionOps.GetSessionInfo sessionId
  match info with
  | Some _ ->
    ctx.ActiveSessionId.Value <- sessionId      // ← per-connection, good
    notifyElm ctx (SessionSwitched ...)          // ← side-effect on ALL UIs, bad
    ctx.Dispatch |> Option.iter (fun d ->
      d (SageFsMsg.Editor EditorAction.ListSessions))  // ← forces UI refresh, bad
    do! EventStore.appendEvents ...              // ← persistence, good
    return sprintf "Switched to session '%s'" sessionId
}
```

**After**:
```fsharp
let switchSession ctx sessionId = task {
  let! info = ctx.SessionOps.GetSessionInfo sessionId
  match info with
  | Some _ ->
    let prev = !ctx.ActiveSessionId
    ctx.ActiveSessionId.Value <- sessionId       // per-connection only
    do! EventStore.appendEvents ...              // persist for restart
    return sprintf "Switched to session '%s'" sessionId
  | None ->
    return sprintf "Error: Session '%s' not found" sessionId
}
```

### Dashboard: Per-SSE-Connection

Each browser tab opens an SSE connection via `/dashboard/stream`. Currently, `pushState()` reads the global `getSessionId()` closure. Fix: maintain a per-connection session ID.

**`createStreamHandler` changes**:
```fsharp
let createStreamHandler ... getDefaultSessionId ... : HttpHandler =
  fun ctx -> task {
    // Per-connection mutable session ID, initialized from daemon default
    let mutable mySessionId = getDefaultSessionId ()

    let pushState () = task {
      // Use mySessionId instead of global getSessionId()
      let state = getSessionStateFor mySessionId
      ...
    }
    ...
  }
```

**Dashboard switch endpoint** (`/dashboard/session/switch/{id}`) needs to communicate which SSE connection to update. Options:
1. **Cookie-based**: Set a browser cookie with a connection ID, SSE handler reads it
2. **Signal-based**: Dashboard switch sets a Datastar signal `$activeSession`, SSE reads it from the POST body
3. **Direct mutation**: SSE handler exposes a thread-safe slot; switch endpoint finds the right connection and mutates it

Option 2 (signal-based) is most natural with Datastar — the switch POST includes the selected session ID, and the SSE handler can observe signal changes.

### Dashboard Eval/Reset Routing

Currently, eval and reset endpoints read `!activeSessionId` to decide which session to route to. They need to route to the *requesting browser's* active session instead.

**Approach**: The eval/reset POST handlers receive the active session from a Datastar signal (`$activeSession`) sent with the request body. The dashboard JS already sends signals with POST requests.

```fsharp
// Dashboard eval reads session from Datastar signal
let createEvalHandler evalCodeForSession : HttpHandler =
  fun ctx -> task {
    let sessionId = Ds.readSignal ctx "activeSession" |> Option.defaultValue ""
    let! result = evalCodeForSession sessionId code
    ...
  }
```

### Editor Integrations

Each supported editor integration keeps its selected session in client state and sends that session identity with commands. Switching a session in one editor must not change another editor, dashboard tab, or MCP connection.

## Dashboard Connectivity Banner

### Problem
When daemon dies, the `serverConnected` Datastar signal stays `true` (it was set server-side). The `Ds.show "!$serverConnected"` binding keeps the disconnect banner hidden.

### Design Principle
**The banner is ONLY for problems.** When connected, it is invisible — no "✅ Connected" message. Users should never see a "Connected" status; that's the assumed happy state. The banner appears only when something is wrong (disconnected, reconnecting, error).

### Fix
Remove Datastar signal control from the banner. Use pure JS:

1. **On page load**: Banner starts hidden (`display: none`) — assume things will work
2. **On SSE stream failure/close**: JS shows the banner ("❌ Disconnected — reconnecting...")
3. **On SSE stream success**: JS hides the banner (back to invisible)
4. **Reconnection poller**: JS polls `/api/daemon-info` every 2s. On success, `location.reload()` to reinitialize Datastar

```javascript
(function() {
  var origFetch = window.fetch;
  var reconnecting = false;
  function showProblem(text) {
    var b = document.getElementById('server-status');
    if (b) { b.className = 'conn-banner conn-disconnected'; b.textContent = text; b.style.display = 'block'; }
  }
  function hideBanner() {
    var b = document.getElementById('server-status');
    if (b) b.style.display = 'none';
  }
  function startReconnect() {
    if (reconnecting) return;
    reconnecting = true;
    showProblem('❌ Disconnected — reconnecting...');
    var interval = setInterval(function() {
      fetch('/api/daemon-info').then(function(r) {
        if (r.ok) { clearInterval(interval); location.reload(); }
      }).catch(function() {});
    }, 2000);
  }
  window.fetch = function(url) {
    var isStream = typeof url === 'string' && url.indexOf('/dashboard/stream') !== -1;
    var p = origFetch.apply(this, arguments);
    if (isStream) {
      p.then(function(resp) {
        if (resp.ok) { reconnecting = false; hideBanner(); }
        else { startReconnect(); }
      }).catch(function() { startReconnect(); });
    }
    return p;
  };
})();
```

The banner HTML drops the `Ds.show` binding and starts hidden:
```fsharp
Elem.div [ Attr.id "server-status"; Attr.class' "conn-banner conn-disconnected"; Attr.style "display:none" ] [
  Text.raw "⏳ Connecting to server..."
]
```

## Incremental Session Resume

### Problem
`resumeSessions()` runs before `elmRuntime` is defined (line 126 vs 199 in DaemonMode.fs). Can't dispatch `ListSessions` from inside the loop.

### Fix
Pass an optional callback to `resumeSessions`:

```fsharp
let resumeSessions (onSessionResumed: (unit -> unit) option) = task {
  ...
  for prev in uniqueByDir do
    let! result = sessionOps.CreateSession prev.Projects prev.WorkingDir
    match result with
    | Ok info ->
      eprintfn "  Resumed: %s" info
      onSessionResumed |> Option.iter (fun f -> f ())
    | Error err -> ...
}
```

At the call site (after `elmRuntime` exists):
```fsharp
do! resumeSessions (Some (fun () ->
  elmRuntime.Dispatch(SageFsMsg.Editor EditorAction.ListSessions)))
```

## Test Plan

### Unit Tests (Expecto, in SageFs.Tests)

#### 1. MCP Session Isolation

```fsharp
module McpSessionIsolationTests

// Test: MCP switchSession updates only the given context's ActiveSessionId
// Setup: Two McpContext instances sharing the same SessionOps but different ActiveSessionId refs
// Act: switchSession ctx1 "session-B"
// Assert: ctx1.ActiveSessionId = "session-B", ctx2.ActiveSessionId unchanged

// Test: MCP switchSession does NOT dispatch SessionSwitched to Elm
// Setup: McpContext with a tracking Dispatch function
// Act: switchSession ctx "session-B"
// Assert: Dispatch was NOT called with SessionSwitched (or with any SageFsMsg)

// Test: MCP switchSession persists DaemonSessionSwitched event
// Setup: McpContext with event store
// Act: switchSession ctx "session-B"
// Assert: daemon-sessions stream contains DaemonSessionSwitched event

// Test: MCP switchSession returns error for nonexistent session
// Setup: McpContext with no sessions
// Act: switchSession ctx "nonexistent"
// Assert: returns "Error: Session 'nonexistent' not found"

// Test: Two concurrent MCP connections can have different active sessions
// Setup: ctx1 with ActiveSessionId = ref "A", ctx2 with ActiveSessionId = ref "B"
// Act: switchSession ctx1 "C"
// Assert: ctx1 active = "C", ctx2 active = "B"
```

#### 2. Dashboard Per-Connection Session

```fsharp
module DashboardSessionIsolationTests

// Test: Dashboard renderSessionStatus uses provided session ID, not global
// Setup: Two different session IDs
// Act: renderSessionStatus "Ready" "session-A" "/path/A"
//      renderSessionStatus "Ready" "session-B" "/path/B"
// Assert: Each renders with its own session info

// Test: Dashboard parseSessionLines extracts active marker correctly
// (Existing test — verify it works with multiple active sessions)

// Test: Output filtering by session ID works correctly
// Setup: OutputLines with mixed session IDs
// Act: Filter for session "A"
// Assert: Only session "A" lines returned

// Test: Dashboard eval routes to requesting browser's session (signal-based)
// Setup: Eval request with Datastar signal $activeSession = "session-B"
// Act: createEvalHandler receives the request
// Assert: Eval is routed to session-B, not global active session
```

#### 3. Dashboard Connectivity

```fsharp
module DashboardConnectivityTests

// Test: Connection banner renders without Ds.show binding
// Setup: renderShell "1.0"
// Act: Check the server-status div attributes
// Assert: No data-show attribute, has class "conn-banner conn-disconnected"

// Test: Connection banner has id "server-status"
// (Snapshot test — verify HTML structure)

// Test: Reconnection script polls /api/daemon-info
// (Integration test — verify JS behavior in browser)
// Tagged [Integration]
```

#### 4. Incremental Session Resume

```fsharp
module SessionResumeTests

// Test: resumeSessions calls onSessionResumed for each successfully resumed session
// Setup: Mock SessionOps.CreateSession that succeeds
// Act: resumeSessions (Some callback) with 3 sessions
// Assert: callback called 3 times

// Test: resumeSessions does NOT call onSessionResumed for failed sessions
// Setup: Mock SessionOps.CreateSession that fails
// Act: resumeSessions (Some callback) with 1 session
// Assert: callback not called

// Test: resumeSessions deduplicates by working directory
// Setup: 5 alive sessions, 3 in same dir, 2 in different dir
// Act: resumeSessions
// Assert: Only 2 sessions resumed, 3 marked as stopped
```

### Integration Tests (Browser, tagged [Integration])

#### 5. Browser Session Isolation

```
// Test: Browser A switches session, Browser B stays on its session
// Setup: Two browser tabs open to /dashboard
// Act: Browser A clicks switch to session "harmony"
// Assert: Browser A shows harmony session, Browser B still shows sagefs session

// Test: Server disconnect shows reconnection banner
// Setup: Browser connected to dashboard
// Act: Kill sagefs daemon
// Assert: Banner shows "❌ Server disconnected — reconnecting..."

// Test: Server reconnect auto-reloads page
// Setup: Browser showing disconnect banner
// Act: Restart sagefs daemon
// Assert: Page reloads, banner disappears, sessions shown
```

## Implementation Order

1. **Tests first** — Write all unit tests (RED). They define the contracts.
2. **MCP isolation** (Phase 1) — Simplest: remove 2 lines from `switchSession`
3. **Dashboard connectivity** (Phase 0 in plan) — Pure JS, apply stashed changes
4. **Incremental resume** (Phase 3) — Add callback parameter
5. **Dashboard per-connection** (Phase 2) — Most complex, needs signal-based routing

## Key Files

| File | What Changes |
|------|-------------|
| `SageFs.Core/Mcp.fs:907-924` | `switchSession` — remove Elm dispatch |
| `SageFs/Dashboard.fs:131-155` | Connection monitor JS — reconnection logic |
| `SageFs/Dashboard.fs:266` | Banner HTML — remove `Ds.show` |
| `SageFs/Dashboard.fs:907+` | `createStreamHandler` — per-connection session |
| `SageFs/Dashboard.fs:1436+` | `createEndpoints` — eval/reset routing |
| `SageFs/DaemonMode.fs:33` | `activeSessionId` → `defaultSessionId` (semantic rename) |
| `SageFs/DaemonMode.fs:126` | `resumeSessions` — add callback parameter |
| `SageFs.Tests/McpSessionIsolationTests.fs` | NEW — MCP isolation tests |
| `SageFs.Tests/DashboardSessionIsolationTests.fs` | NEW — Dashboard isolation tests |
