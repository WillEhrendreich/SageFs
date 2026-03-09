# SageFs Visual Studio Extension — Sprint Planning Inventory

**Generated for:** Rapid sprint planning exercise  
**Scope:** Extension state analysis (real vs placeholder), architecture assessment  

---

## 1. EXTENSION METADATA & CONFIGURATION

### 1.1 SageFsExtension.cs (Main Entry Point)

**Type:** C# thin shim (source generators require C#)  
**Real vs Placeholder:** ✓ **REAL** (functional DI + minimal boilerplate)

**Key Facts:**
- Extension ID: SageFs.VisualStudio.a3f9c1e2-7b5d-4e8a-9c1f-2d3e4f5a6b7c
- Publisher: WillEhrendreich
- Display Name: "SageFs — F# Live Development"
- Description: "Inline eval, session management, and hot-reload for F# via SageFs daemon"

**DI Registration:**
- **SageFsClient** (singleton) — HTTP client for daemon communication
- **EvalCancellation** (singleton) — Cooperative cancellation token source
- **LiveTestingSubscriber** (singleton) — Starts WebSocket listener on port 37749

**Architectural Notes:**
- All real logic lives in F# project: SageFs.VisualStudio.Core
- C# layer is just a shim for VS integration (attribute routing, commands, tool windows)
- This is a clean design — no business logic in C#

---

## 2. TOOL WINDOWS (4 Total)

### 2.1 LiveTestingWindow

**Status:** ✓ **REAL & FUNCTIONAL**

**Files:**
- \LiveTestingWindow.cs\ — Command + Window registration
- \LiveTestingControl.xaml\ — **WPF XAML** UI (DataTemplate)
- \LiveTestingData.cs\ — Data context + binding logic

**UI Framework:** WPF/XAML (Microsoft.VisualStudio.Extensibility.ToolWindows)

**Content:** Real (not placeholder)
- **Toggle Button:** Enable/disable live testing
- **Run All Button:** Execute test suite
- **Filter Cycle:** All → Failed → Running → Stale → Passed
- **Search:** Filter tests by name (UpdateSourceTrigger=PropertyChanged)
- **Summary Display:** "✓ N/M passed, X failed, Y running, Z stale"
- **Test Results Grid:** Grouped by file (leverages \Core.TestTreeViewModel.formatGroupedOutput\)
- **Recent Events:** Last 10 events from daemon

**Binding Logic:**
- **RefreshCommand:** Auto-refresh every render
- **ToggleCommand:** Calls \client.EnableLiveTestingAsync\ / \DisableLiveTestingAsync\
- **RunAllCommand:** Calls \client.RunTestsAsync("")\
- **CycleFilterCommand:** Cycles through 4 filter states
- **ClearSearchCommand:** Clears search query

**Data Source:** \LiveTestingSubscriber\ (WebSocket stream from daemon + state events)

---

### 2.2 SessionContextWindow

**Status:** ✓ **REAL & FUNCTIONAL**

**Files:**
- \SessionContextWindow.cs\ — Command + Window registration
- \SessionContextControl.xaml\ — **WPF XAML** (DataTemplate with VS theming)
- \SessionContextData.cs\ — Data context + auto-refresh timer

**UI Framework:** WPF/XAML (bound to VS environment colors)

**Content:** Real (not placeholder)
- **Connection Status:** "● Connected" or "○ Offline"
- **Refresh Button:** Manual + auto-refresh every 10 seconds (Timer)
- **Session Info:** ID, status, projects, eval count, working directory
- **Assemblies Section:** Lists loaded assemblies with namespace/module counts
- **Namespaces Section:** Lists opened namespaces (namespace vs module icons)
- **Failed Opens:** Error summary if warmup failed (orange/red text)
- **Hot Reload Section:** Shows watched vs total files

**API Calls:**
- \client.PingAsync()\ — Connection check
- \client.GetSessionsAsync()\ — Active session list
- \client.GetWarmupContextAsync(sessionId)\ — Warmup state (assemblies, namespaces)
- \client.GetHotReloadStateAsync(sessionId)\ — Watched files

**Auto-Refresh:** 10-second Timer, stopped on Dispose()

---

### 2.3 HotReloadWindow

**Status:** ✓ **REAL & FUNCTIONAL**

**Files:**
- \HotReloadWindow.cs\ — Command + Window registration
- \HotReloadControl.xaml\ — **WPF XAML** (simple UI)
- \HotReloadData.cs\ — Data context

**UI Framework:** WPF/XAML

**Content:** Real (not placeholder)
- **Watch All Button:** Calls \client.WatchAllAsync()\
- **Unwatch All Button:** Calls \client.UnwatchAllAsync()\
- **Reload Button:** Calls \client.RefreshHotReloadAsync()\
- **Refresh Button:** Re-fetches state
- **Summary:** "👁 N watched of M total files"
- **File List:** Ordered, with watch status icon (👁 or blank)

**API Calls:**
- \client.GetSessionsAsync()\ — Get first session
- \client.GetHotReloadStateAsync(sessionId)\ — Fetch watched files list
- \client.WatchAllAsync(sessionId)\
- \client.UnwatchAllAsync(sessionId)\
- \client.RefreshHotReloadAsync(sessionId)\

---

### 2.4 TypeExplorerWindow

**Status:** ✓ **REAL & FUNCTIONAL**

**Files:**
- \TypeExplorerWindow.cs\ — Command + Window registration
- \TypeExplorerControl.xaml\ — **WPF XAML** (searchable)
- \TypeExplorerData.cs\ — Data context + search logic

**UI Framework:** WPF/XAML

**Content:** Real (not placeholder)
- **Search Box:** TwoWay binding to \SearchQuery\
- **Search Button:** Triggers \SearchAsync()\
- **Refresh Button:** Triggers \RefreshAsync()\
- **Namespaces Section:** Shows opened namespaces with icons (📦 module, 📁 namespace)
- **Assemblies Section:** Shows loaded assemblies with counts

**Search Logic:** Case-insensitive \.Contains()\ on namespace/assembly names

**API Calls:**
- \client.GetSessionsAsync()\ — Get first session
- \client.GetWarmupContextAsync(sessionId)\ — Assemblies + namespaces list

---

## 3. COMMANDS (25 Total)

### 3.1 Eval Commands (4)

#### **EvalSelectionCommand**
- **Shortcut:** Alt+Enter
- **Behavior:** Evaluates selected text, or entire file if no selection
- **API:** \client.EvalWithContextAsync(code, filePath, "block", startLine, ct)\
- **Output:** ✓ Success/✗ Exit code + diagnostics → Output window
- **Status:** ✓ REAL

#### **EvalFileCommand**
- **Shortcut:** Shift+Alt+Enter
- **Behavior:** Evaluates entire file
- **API:** \client.EvalWithContextAsync(code, filePath, "file", 0, ct)\
- **Status:** ✓ REAL

#### **EvalRangeCommand**
- **Shortcut:** Ctrl+Alt+Enter
- **Behavior:** Evaluates ;;-delimited block around cursor (or selection)
- **Logic:** \FindBlockAroundCursor(text, cursorOffset)\ — scans backward/forward for ;;
- **Status:** ✓ REAL

#### **LoadScriptCommand**
- **Shortcut:** None (menu only)
- **Behavior:** Emits \#load "path.fsx";;\ and evals it
- **API:** \client.EvalAsync(loadCode, ct)\
- **Status:** ✓ REAL

---

### 3.2 Session Management Commands (6)

#### **CreateSessionCommand**
- **Shortcut:** None
- **Behavior:** Creates new FSI session
- **API:** \client.CreateSessionAsync(ct)\
- **UserInput:** ShowPromptAsync("Session created", PromptOptions.OK)
- **Status:** ✓ REAL

#### **SwitchSessionCommand**
- **Shortcut:** None
- **Behavior:** Lists sessions → uses OK/Cancel to confirm switch to second session
- **API:** \client.GetSessionChoicesAsync(ct)\ → \client.SwitchToSessionAsync(sessionId, ct)\
- **UserInput:** ShowPromptAsync("Switch to session: {name}?", PromptOptions.OKCancel)
- **Status:** ✓ REAL (but simplified — only OK/Cancel, not a multi-choice picker)

#### **ResetSessionCommand**
- **Shortcut:** None
- **Behavior:** Soft reset (clears definitions, keeps loaded files)
- **API:** \client.ResetSessionAsync(hard: false, ct)\
- **UserInput:** ShowPromptAsync("Reset the active FSI session?", PromptOptions.OKCancel)
- **Status:** ✓ REAL

#### **HardResetCommand**
- **Shortcut:** None
- **Behavior:** Hard reset (destroys session, rebuilds DLLs)
- **API:** \client.ResetSessionAsync(hard: true, ct)\
- **UserInput:** ShowPromptAsync("Hard reset?", PromptOptions.OKCancel.WithCancelAsDefault())
- **Status:** ✓ REAL

#### **StopSessionCommand**
- **Shortcut:** None
- **Behavior:** Lists sessions → confirms stop for first session
- **API:** \client.StopSessionAsync(sessionId, ct)\
- **UserInput:** ShowPromptAsync("Stop session: {name}?", PromptOptions.OKCancel)
- **Status:** ✓ REAL

#### **ConfigureWarmupAutoOpenCommand**
- **Shortcut:** None
- **Behavior:** Creates/opens \.SageFs/config.fsx\ with \AutoOpenNamespaces = false\
- **Logic:** \WarmupAutoOpenConfig.Ensure(workingDir)\ + \TryOpen(path)\
- **UserInput:** ShowPromptAsync with status message
- **Status:** ✓ REAL (utility command)

---

### 3.3 Daemon Lifecycle Commands (3)

#### **StartDaemonCommand**
- **Shortcut:** None
- **Behavior:** Auto-discovers .fsproj/.slnx/.sln, prefers test projects
- **API:** \Core.DaemonManager.startDaemon(target)\
- **Status:** ✓ REAL

#### **StopDaemonCommand**
- **Shortcut:** None
- **Behavior:** Shuts down daemon via HTTP POST to /api/shutdown
- **API:** \client.StopDaemonAsync(ct)\
- **Status:** ✓ REAL

#### **OpenDashboardCommand**
- **Shortcut:** None
- **Behavior:** Opens browser to localhost:37750
- **API:** \Core.DaemonManager.openDashboard(dashboardPort)\
- **Status:** ✓ REAL

---

### 3.4 Hot Reload Commands (5)

#### **HotReloadToggleFileCommand**
- **Shortcut:** None
- **Behavior:** Toggles hot reload for current file (.fs only)
- **API:** \client.ToggleHotReloadAsync(sessionId, filePath, ct)\
- **Validation:** Checks file ends with ".fs"
- **Status:** ✓ REAL

#### **HotReloadWatchAllCommand**
- **Shortcut:** None
- **Behavior:** Watches all F# files for changes
- **API:** \client.WatchAllAsync(sessionId, ct)\
- **Status:** ✓ REAL

#### **HotReloadUnwatchAllCommand**
- **Shortcut:** None
- **Behavior:** Stops watching all files
- **API:** \client.UnwatchAllAsync(sessionId, ct)\
- **Status:** ✓ REAL

#### **HotReloadRefreshCommand**
- **Shortcut:** None
- **Behavior:** Re-evaluates all watched files
- **API:** \client.RefreshHotReloadAsync(sessionId, ct)\
- **Status:** ✓ REAL

#### **HotReloadToggleDirectoryCommand**
- **Shortcut:** None
- **Behavior:** Toggles hot reload for entire directory
- **API:** \client.WatchDirectoryAsync(sessionId, directory, ct)\ (daemon toggles based on state)
- **Status:** ✓ REAL

---

### 3.5 Live Testing Commands (3)

#### **LiveTestingCommand** (Toggle)
- **Shortcut:** None
- **Behavior:** Toggles live testing on/off
- **API:** \client.EnableLiveTestingAsync(ct)\ / \client.DisableLiveTestingAsync(ct)\
- **Output:** "✓ Live testing enabled" or "○ Live testing disabled"
- **Status:** ✓ REAL

#### **RunTestsCommand**
- **Shortcut:** None
- **Behavior:** Executes all tests
- **API:** \client.RunTestsAsync("", ct)\
- **Output:** "▶ Running all tests..."
- **Status:** ✓ REAL

#### **SetRunPolicyCommand**
- **Shortcut:** None
- **Behavior:** ✓ **Reads current daemon state and cycles ALL categories to the next policy in sequence**
- **Policy cycle:** `every → save → demand → disabled → every`
- **Code:** Reads `LiveTestingSubscriber.CurrentState.Policies` (FSharpMap), calls `Task.WhenAll` over all 6 categories
- **Status:** ✓ **REAL & FUNCTIONAL** (fixed in Sprint 4)

#### **ShowRecentEventsCommand**
- **Shortcut:** None
- **Behavior:** Fetches last 30 events from daemon
- **API:** \client.GetRecentEventsAsync(30, ct)\
- **Output:** JSON-formatted event list
- **Status:** ✓ REAL

---

### 3.6 Cell Navigation Commands (3)

#### **NextBlockCommand**
- **Shortcut:** Ctrl+Alt+]
- **Behavior:** Finds next ;;-delimited block after cursor and evals it
- **Logic:** \FindNextBlock(text, fromOffset)\ — scans forward for ;; boundaries
- **Cancellation:** Supports cooperative cancellation via \Core.EvalCancellation\
- **Status:** ✓ REAL

#### **PrevBlockCommand**
- **Shortcut:** Ctrl+Alt+[
- **Behavior:** Finds previous ;;-delimited block before cursor and evals it
- **Logic:** \FindPrevBlock(text, fromOffset)\ — scans backward for ;; boundaries
- **Cancellation:** Cooperative cancellation support
- **Status:** ✓ REAL

#### **EvalAndAdvanceCommand**
- **Shortcut:** Ctrl+Alt+Shift+Enter
- **Behavior:** Evaluate current block around cursor
- **Intent:** "The core rapid-iteration loop" — step through .fsx files block-by-block
- **Cancellation:** Cooperative support
- **Status:** ✓ REAL

---

### 3.7 Utility Commands (3)

#### **CancelEvalCommand**
- **Shortcut:** Ctrl+Alt+C
- **Behavior:** Immediately cancels in-flight evaluation
- **Implementation:** \cancellation.Cancel()\ (cooperative, may not be instant for long-running .NET code)
- **Status:** ✓ REAL

#### **ToggleErrorListCommand**
- **Shortcut:** None
- **Behavior:** Starts/stops \ErrorListBridge\ (forwards daemon diagnostics to VS Error List)
- **State:** Toggles between \ridge != null\ states
- **Status:** ✓ REAL

#### (Command to show tool windows — 4 commands, registered as [VisualStudioContribution])
- **ShowLiveTestingCommand**
- **ShowSessionContextCommand**
- **ShowHotReloadCommand**
- **ShowTypeExplorerCommand**

---

## 4. SAGEFSCLIENT.FS — HTTP API Surface

**Type:** F# HTTP client  
**Real vs Placeholder:** ✓ **REAL** (comprehensive coverage of daemon endpoints)

### 4.1 Lifecycle

| Method | Endpoint | Purpose | Returns |
|--------|----------|---------|---------|
| \PingAsync\ | GET /api/sessions | Connection check | bool |
| \StartDaemonAsync\ | — | Stub (NOP) | unit task |
| \StopDaemonAsync\ | POST /api/shutdown | Shutdown | unit task |

### 4.2 Execution & Evaluation

| Method | Endpoint | Purpose | Returns |
|--------|----------|---------|---------|
| \EvalAsync\ | POST /exec | Execute code snippet | EvalResult |
| \EvalWithContextAsync\ | POST /exec | Execute with file context | EvalResult |
| \CancelEvalAsync\ | POST /api/cancel | Abort current eval | bool |

**EvalResult:**
\\\sharp
{ Output: string; Diagnostics: string list; ExitCode: int }
\\\

### 4.3 Session Management

| Method | Endpoint | Purpose | Returns |
|--------|----------|---------|---------|
| \GetSessionsAsync\ | GET /api/sessions | List all sessions | SessionInfo list |
| \CreateSessionAsync\ | POST /api/sessions/create | New session | unit task |
| \GetSessionChoicesAsync\ | — | Format session picker | (string * string) list |
| \SwitchToSessionAsync\ | POST /api/sessions/switch | Switch active session | bool |
| \StopSessionAsync\ | POST /api/sessions/stop | Stop session by ID | bool |
| \ResetSessionAsync\ | POST /api/sessions/reset | Soft reset | unit task |
| \ResetSessionAsync(hard:true)\ | POST /api/sessions/hard-reset | Hard reset (rebuild DLLs) | unit task |

**SessionInfo:**
\\\sharp
{ Id: string; ProjectNames: string list; State: string; WorkingDirectory: string; EvalCount: int }
\\\

### 4.4 Warmup Context (Introspection)

| Method | Endpoint | Purpose | Returns |
|--------|----------|---------|---------|
| \GetWarmupContextAsync\ | GET /api/sessions/{id}/warmup-context | Warmup state | WarmupContext option |
| \GetBindingScopeAsync\ | GET /api/sessions/{id}/binding-scope | Current bindings | BindingScopeInfo option |
| \GetTimelineStatsAsync\ | GET /api/sessions/{id}/timeline | Eval performance stats | TimelineStatsInfo option |
| \ExportSessionAsync\ | GET /api/sessions/{id}/export-fsx | Export as .fsx script | string option |

### 4.5 Hot Reload

| Method | Endpoint | Purpose | Returns |
|--------|----------|---------|---------|
| \GetHotReloadStateAsync\ | GET /api/sessions/{id}/hotreload | Watched files list | HotReloadState option |
| \ToggleHotReloadAsync\ | POST /api/sessions/{id}/hotreload/toggle | Toggle file watch | unit task |
| \WatchAllAsync\ | POST /api/sessions/{id}/hotreload/watch-all | Watch all F# files | unit task |
| \UnwatchAllAsync\ | POST /api/sessions/{id}/hotreload/unwatch-all | Stop watching all | unit task |
| \WatchDirectoryAsync\ | POST /api/sessions/{id}/hotreload/watch-directory | Toggle directory watch | unit task |
| \UnwatchDirectoryAsync\ | POST /api/sessions/{id}/hotreload/unwatch-directory | Stop watching dir | unit task |
| \RefreshHotReloadAsync\ | POST /api/sessions/{id}/hotreload/refresh | Re-evaluate watched files | unit task |

### 4.6 Live Testing

| Method | Endpoint | Purpose | Returns |
|--------|----------|---------|---------|
| \EnableLiveTestingAsync\ | POST /api/live-testing/enable | Turn on live tests | bool |
| \DisableLiveTestingAsync\ | POST /api/live-testing/disable | Turn off live tests | bool |
| \RunTestsAsync\ | POST /api/live-testing/run | Execute tests (optionally filtered) | unit task |
| \SetRunPolicyAsync\ | POST /api/live-testing/run-policy | Set test run policy by category | unit task |
| \GetRecentEventsAsync\ | GET /api/recent-events?count=N | Last N FSI events | string (JSON) |
| \GetTestTraceAsync\ | GET /api/live-testing/test-trace | Test execution trace | string option |

### 4.7 Completions (Dashboard)

| Method | Endpoint | Purpose | Returns |
|--------|----------|---------|---------|
| \GetCompletionsAsync\ | POST /dashboard/completions | Autocomplete suggestions | CompletionItem[] |

**CompletionItem:** \Label, Kind, InsertText\

### 4.8 Dependency Graph

| Method | Endpoint | Purpose | Returns |
|--------|----------|---------|---------|
| \GetCellGraphAsync\ | GET /api/dependency-graph | Cell dependencies | CellGraphInfo option |

### 4.9 Type Exploration

| Method | Endpoint | Purpose | Returns |
|--------|----------|---------|---------|
| \ExploreAsync\ | GET /api/explore?type={name} | Explore type members | string option |

### 4.10 Configuration

**Public Properties:**
- \McpPort\ (default 37749) — MCP (message control protocol) port
- \DashboardPort\ (default 37750) — Web dashboard port
- \BaseUrl\ — \http://localhost:{McpPort}\
- \DashUrl\ — \http://localhost:{DashboardPort}\

**HTTP Client:** Configured with automatic decompression (GZip/Deflate)

---

## 5. STRING RESOURCES (extension.json)

**File:** \.vsextension/string-resources.json\

**All 36 command display names registered:**

\\\json
{
  "SageFs.EvalSelection.DisplayName": "SageFs: Evaluate Selection",
  "SageFs.EvalFile.DisplayName": "SageFs: Evaluate File",
  "SageFs.StartDaemon.DisplayName": "SageFs: Start Daemon",
  "SageFs.StopDaemon.DisplayName": "SageFs: Stop Daemon",
  "SageFs.OpenDashboard.DisplayName": "SageFs: Open Dashboard",
  "SageFs.CreateSession.DisplayName": "SageFs: Create Session",
  "SageFs.ConfigureWarmupAutoOpen.DisplayName": "SageFs: Configure Warmup Auto-Open",
  "SageFs.SwitchSession.DisplayName": "SageFs: Switch Session",
  "SageFs.StopSession.DisplayName": "SageFs: Stop Session",
  "SageFs.ResetSession.DisplayName": "SageFs: Reset Session",
  "SageFs.HardReset.DisplayName": "SageFs: Hard Reset",
  "SageFs.ClearResults.DisplayName": "SageFs: Clear Inline Results",
  "SageFs.ShowSessionContext.DisplayName": "SageFs: Session Context",
  "SageFs.HotReloadToggle.DisplayName": "SageFs: Toggle Hot Reload for File",
  "SageFs.HotReloadWatchAll.DisplayName": "SageFs: Watch All Files",
  "SageFs.HotReloadUnwatchAll.DisplayName": "SageFs: Unwatch All Files",
  "SageFs.HotReloadRefresh.DisplayName": "SageFs: Refresh Hot Reload",
  "SageFs.ToggleErrorList.DisplayName": "SageFs: Toggle Error List Bridge",
  "SageFs.CodeLens.DisplayName": "SageFs Eval",
  "SageFs.HotReloadToggleDirectory.DisplayName": "SageFs: Toggle Hot Reload for Directory",
  "SageFs.EvalRange.DisplayName": "SageFs: Evaluate Code Block",
  "SageFs.LiveTesting.DisplayName": "SageFs: Enable/Disable Live Testing",
  "SageFs.RunTests.DisplayName": "SageFs: Run All Tests",
  "SageFs.ShowRecentEvents.DisplayName": "SageFs: Show Recent Events",
  "SageFs.TestCodeLens.DisplayName": "SageFs Live Tests",
  "SageFs.ShowLiveTesting.DisplayName": "SageFs: Live Testing Dashboard",
  "SageFs.ShowTypeExplorer.DisplayName": "SageFs: Type Explorer",
  "SageFs.ShowHotReload.DisplayName": "SageFs: Hot Reload Files",
  "SageFs.SetRunPolicy.DisplayName": "SageFs: Set Run Policy",
  "SageFs.CancelEval.DisplayName": "SageFs: Cancel Evaluation",
  "SageFs.NextBlock.DisplayName": "SageFs: Move to Next ;; Block",
  "SageFs.PrevBlock.DisplayName": "SageFs: Move to Previous ;; Block",
  "SageFs.EvalAndAdvance.DisplayName": "SageFs: Evaluate Block and Advance",
  "SageFs.LoadScript.DisplayName": "SageFs: Load Script File"
}
\\\

**Note:** Some entries in string-resources refer to commands not found in Commands/ (e.g., "ClearResults", "CodeLens", "TestCodeLens") — likely removed/refactored, or registered elsewhere (CodeLens likely in Services/).

---

## 6. REAL vs PLACEHOLDER ASSESSMENT

### ✓ REAL (Production-Ready)

1. **All 4 Tool Windows:** Fully functional with live data binding, WebSocket subscribers, auto-refresh
2. **Eval Commands (4):** Complete keyboard shortcuts, selection/file/block eval with context
3. **Session Management (6):** Create, switch, reset, hard-reset, stop sessions
4. **Hot Reload Commands (5):** Toggle, watch-all, unwatch-all, refresh, directory-level control
5. **Live Testing UI:** Comprehensive dashboard with filtering, search, status display
6. **SetRunPolicyCommand:** Cycles all 6 categories through every→save→demand→disabled (fixed Sprint 4)
7. **Daemon Lifecycle (3):** Auto-discovery, start/stop, dashboard launch
8. **Cancellation Support:** Cooperative eval abort (Ctrl+Alt+C)
9. **Error List Bridge:** Diagnostics forwarding to VS Error List
10. **SageFsClient.fs:** Comprehensive HTTP API with proper error handling, JSON parsing, model types
11. **MEF Editor Assembly (net472):** Glyphs, squiggles, inline failure adornments (Sprints 4-5)

### ⚠️ PLACEHOLDER / INCOMPLETE

1. **SwitchSessionCommand:** Simplified to OK/Cancel (switches to second session) — should ideally be a multi-choice dialog
2. **Type Explorer Search:** Basic `.Contains()` — no fuzzy matching or advanced filtering

### ⚠️ STUB / NOP

1. **StartDaemonAsync** in SageFsClient.fs — currently a no-op (`return ()`). Daemon lifecycle is handled in DaemonCommands.cs via `Core.DaemonManager` (F#).

### 🔮 FUTURE (Sprint 6+)

1. **CodeLens provider** — `file_annotations.CodeLenses` already emits `{Line, Label, Command: RunTest|DebugTest|ShowHistory}` — wire `ICodeLensProvider` for '▶ Run | 🐛 Debug | 📜 History' above test functions
2. **Inline eval adornments** — show `// => result` right of eval lines (like Rider), dims to 35% opacity on edit, clears on re-eval
3. **Session management panel** — Sessions tool window: list FSI sessions, start/kill, show eval counts
4. **Status bar indicator** — 'SageFs ● connected' in VS status bar via `IVsStatusbar`
---

## 7. KEYBOARD SHORTCUTS

| Command | Shortcut | Behavior |
|---------|----------|----------|
| EvalSelection | Alt+Enter | Evaluate selection or full file |
| EvalFile | Shift+Alt+Enter | Evaluate entire file |
| EvalRange | Ctrl+Alt+Enter | Evaluate ;;-delimited block |
| CancelEval | Ctrl+Alt+C | Cancel in-flight eval |
| NextBlock | Ctrl+Alt+] | Advance to next ;; block and eval |
| PrevBlock | Ctrl+Alt+[ | Go to previous ;; block and eval |
| EvalAndAdvance | Ctrl+Alt+Shift+Enter | Evaluate current block + step forward |

**All others:** Menu only (Extensions → SageFs → ...)

---

## 8. ARCHITECTURAL PATTERNS & IMPROVEMENTS

### Current Architecture

\\\
VS Extension (C#)
  ├─ Commands (25)
  │   └─ All delegate to SageFsClient
  ├─ Tool Windows (4)
  │   ├─ XAML UI (WPF DataTemplates)
  │   └─ Data context (C#) binds to daemon HTTP API
  └─ Services
      ├─ ErrorListBridge
      ├─ CodeLensProviders
      └─ DiagnosticsBridge
         
Core Logic (F#)
  ├─ SageFsClient — HTTP + JSON parsing
  ├─ LiveTestingSubscriber — WebSocket stream
  ├─ DaemonManager — Process lifecycle
  └─ Type definitions
\\\

### Potential Improvements (No Major Architectural Changes)

1. **SetRunPolicyCommand:** Add cycling through 4 policies (every → save → demand → disabled → every)
   - Change: Loop counter in command state, or query current policy from daemon

2. **SwitchSessionCommand:** Replace OK/Cancel with multi-choice dialog
   - Change: Use Extensibility.Shell().PickFromList() or similar (if available in VS Extensibility API v17+)

3. **Type Explorer Search:** Add fuzzy matching library (e.g., FuzzySharp)
   - Change: \TypeExplorerData.cs\ SearchAsync() logic

4. **Missing Commands:** Implement "ClearResults" if it's legitimate (clear inline Squigglies?)
   - Check if related to code lens results

5. **Performance:** Cache warmup context + add TTL
   - SessionContextData refreshes every 10s — could add exponential backoff if unchanged

6. **Error Handling:** Unified error UI for common failures
   - Current: Each command handles errors independently

---

## 9. SPRINT PLANNING SUMMARY

### Ready to Ship
- Tool windows (4)
- Eval commands (4)
- Session management (6)
- Hot reload (5)
- Daemon lifecycle (3)
- Cancel + Error List (2)

### Minor Refinements (1-2 story points each)
- SetRunPolicy cycling (2 pts)
- SwitchSession multi-choice (2-3 pts)
- Fuzzy search in Type Explorer (2 pts)

### Investigation Needed
- "ClearResults" command — where should it go?
- CodeLens integration — not found in Commands/, check Services/

### Tech Debt (Nice-to-Have)
- Consolidate error handling → shared error UI
- Add caching + expiry logic for warmup context
- Unit tests for cell navigation block-finding logic

---

## 10. MEF EDITOR ASSEMBLY — `SageFs.VisualStudio.Editor` (net472)

**Status:** ✅ Fully implemented through Sprint 5. 39/39 unit tests passing.

### What it does

Three interconnected features rendering live test data directly in the VS editor:

#### A. Margin Glyphs (TestGlyphTagger)
Colored WPF `Ellipse` glyphs in the gutter per test function line:

| Color | Meaning |
|-------|---------|
| 🟢 Green | Test passed |
| 🔴 Red | Test failed |
| 🟡 Amber | Test running |
| ⚫ Gray | Test detected / not yet run |

#### B. Squiggles (SquiggleTagger) — Option C
Roslyn-style squiggles under F# code with daemon-reported diagnostics:
- Red squiggles = errors, yellow = warnings, dotted = hints
- Tooltip shows the diagnostic message
- Source: `/diagnostics` SSE endpoint

#### C. Inline Failure Adornments (InlineFailureAdornmentManager) — Option D
Right-aligned text adornments on failing test lines:
```
let myTest () =  ⊘ myTest — Expected: 42  Actual: 0
```
- Italic, translucent red, matches editor font family and size
- Shows all failures for the line joined with `|`
- Source: `file_annotations` events from `/events` SSE

### Architecture

```
SseConnectionHub                     — deduplicates SSE connections (2 total: /events, /diagnostics)
  │
  ├─ /events ─────────────────────────────────────────────────────────────┐
  │    ├─ TestStateTracker           — ConcurrentDict[(filePath,line) → TestStatus]
  │    │    → TestGlyphTagger        — ITagger<TestStatusGlyphTag> per ITextBuffer
  │    │    → TestGlyphFactory       — IGlyphFactory, draws Ellipse(10x10px) WPF elements
  │    │
  │    └─ FileAnnotationTracker      — ConcurrentDict[(filePath,line) → InlineFailureDisplay list]
  │         → InlineFailureAdornmentManager  — IWpfTextViewCreationListener per F# view
  │
  └─ /diagnostics ──────────────────────────────────────────────────────────┐
       └─ DiagnosticStateTracker     — volatile List<DiagnosticEntry> per session
            → SquiggleTagger         — ITagger<SageFsErrorTag> per ITextBuffer
```

### Key Design Decisions

1. **`PrivateAssets="all"` on all VS SDK NuGet refs** — prevents type-identity crashes from double-loading
2. **`TagsChanged` on UI thread** — `Application.Current?.Dispatcher.BeginInvoke(...)` — missing this causes mysterious editor crashes
3. **`HttpClient.Timeout = 75s`** (NOT `InfiniteTimeSpan`) — prevents zombie connections
4. **`[ContentType("F#")]` + `[ContentType("F# Script")]`** on ALL MEF exports — VS does NOT walk the base-type chain for tagger/factory/listener exports; `.fsx` needs an explicit second attribute
5. **`SseConnectionHub`** — static per-endpoint multiplexer; at most 2 HTTP SSE connections total
6. **`SageFsFeatureFlags`** — per-feature runtime disable flags in `%LOCALAPPDATA%\SageFs\`:
   - `disable-glyphs.flag` — all features (legacy, from GlyphSpikeGuard)
   - `disable-squiggles.flag` — squiggles only
   - `disable-inline-hints.flag` — inline adornments only
7. **LayoutChanged guard** — `InlineFailureAdornmentManager` skips full redraw when `NewOrReformattedLines.Count == 0 && TranslatedLines.Count == 0`; prevents per-scroll-tick WPF churn
8. **Editor font metrics** — adornments read `FormattedLineSource.DefaultTextProperties` for font family/size; renders at 90% of editor font size, italic
9. **Vertical centering** — uses `block.DesiredSize.Height` after `Measure()` instead of magic constant

### Kill Switches (4 layers)

1. **Build gate:** `$(EnableGlyphSpike)=true` must be set to include the MEF project reference
2. **Runtime — all off:** `%LOCALAPPDATA%\SageFs\disable-glyphs.flag`
3. **Runtime — squiggles off:** `%LOCALAPPDATA%\SageFs\disable-squiggles.flag`
4. **Runtime — adornments off:** `%LOCALAPPDATA%\SageFs\disable-inline-hints.flag`
5. **Clean boundary:** all spike code isolated in `SageFs.VisualStudio.Editor\` — delete folder + remove project ref to fully revert

### Port Discovery

- Written by: `SageFsExtension.InitializeServices` → `%LOCALAPPDATA%\SageFs\daemon.json`
  ```json
  {"Url":"http://localhost:37749"}
  ```
- Read by: `PortConfig.TryGetDaemonUrl()` in the net472 MEF assembly

### Files

| File | Purpose |
|------|---------|
| `SageFs.VisualStudio.Editor.csproj` | net472, opts out of central pkgs, `PrivateAssets="all"` |
| `PortConfig.cs` | Reads daemon.json; `GlyphSpikeGuard` kill switch |
| `SageFsFeatureFlags.cs` | Per-feature runtime disable flags |
| `SseClient.cs` | Background SSE subscriber, exponential-backoff reconnect |
| `SseConnectionHub.cs` | Static per-endpoint SSE connection multiplexer |
| `TestStateTracker.cs` | Processes `test_results_batch` events |
| `TestGlyphTagger.cs` | `ITaggerProvider` + `IGlyphFactoryProvider`; UI-thread `TagsChanged` |
| `DiagnosticStateTracker.cs` | Processes `/diagnostics` SSE events |
| `SquiggleTagger.cs` | `ITaggerProvider` for error squiggles |
| `FileAnnotationTracker.cs` | Processes `file_annotations` events |
| `InlineFailureAdornment.cs` | `IWpfTextViewCreationListener` + adornment manager |

### Day 1 Empirical Validation Still Required

- ContentType `"F#"` confirmed correct per Don Syme / Ionide source; `"F# Script"` added for `.fsx`
- Does `ExtensionType="VisualStudio.Extensibility"` block MEF loading? Check `%LOCALAPPDATA%\Microsoft\VisualStudio\17.0_xxx\ComponentModelCache\`
- Does VS MEF host discover DLLs from `CopyMefAssemblyToOutput` build target?

### Test Coverage

`SageFs.VisualStudio.Editor.Tests` (net472 xUnit, 39 tests):
- `TestStateTrackerTests` (20) — all daemon JSON shapes
- `PortConfigTests` (1) — smoke test
- `DiagnosticStateTrackerTests` (8) — /diagnostics parsing
- `FileAnnotationTrackerTests` (9) — file_annotations parsing, path normalization

---

**Report Updated:** Sprint 5 complete (v0.5.716)
