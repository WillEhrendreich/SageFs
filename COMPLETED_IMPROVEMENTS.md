# SageFs — Completed Improvements

> Implemented features extracted from [IMPROVEMENT_PLAN.md](IMPROVEMENT_PLAN.md) to keep the active plan focused on remaining work.

---

## 0.0 Marten + PostgreSQL Event Store — ✅ DONE

**Implemented:** Domain event DUs (`SageFsEvent`, `EventSource`, `DiagnosticEvent`, `EventMetadata`), Marten integration with Testcontainers, `EventStore` module (`configureStore`, `appendEvents`, `fetchStream`, `tryCreateFromEnv`), actor event emission via `onEvent` callback (SessionStarted, SessionReady, EvalRequested, EvalCompleted/EvalFailed, DiagnosticsChecked, SessionReset, SessionHardReset), CLI integration via `SageFs_CONNECTION_STRING`, Docker Compose for local PostgreSQL.

**Files:** `SageFs\Features\Events.fs`, `SageFs.Server\EventStore.fs`, `compose.yml`

**Tests:** 4 Testcontainers-based integration tests + 5 EventStore tests + 5 actor event emission tests.

---

## 1.0 MCP Response Strategy (LLM Context Window Protection) — ✅ DONE

**Problem:** MCP tool responses wasted LLM context with redundant data — echoed code, static tool lists, unbounded event re-sends, overlapping status tools, double-reported diagnostics.

**Solution — six changes:**
1. Strip echoed code from MCP response (console echo stays for human-readable transcript)
2. Move boilerplate to `ServerInstructions` (sent once at session start)
3. Delta events with cursor via Marten sequence numbers
4. Merge overlapping `get_fsi_status`/`get_startup_info` tools
5. Separate diagnostics from eval results
6. Structured JSON option (`format: "json"` parameter)

**Impact:** ~40-60% reduction in tokens consumed per typical agent session.

**Files:** `Mcp.fs`, `McpTools.fs`, `McpServer.fs`

---

## 1.0b Affordance-Driven MCP (LARP-Inspired State Machine) — ✅ DONE

**Problem:** SageFs presented 8 static tools regardless of session state. Agents wasted tokens reasoning about which tools might work.

**Solution — session state machine with affordance filtering:**
1. `SessionState` DU — `Uninitialized | WarmingUp | Ready | Evaluating | Faulted`
2. `availableTools` pure function — computes valid tools per state
3. Tool-level enforcement — invalid state returns structured error listing alternatives
4. `get_fsi_status` includes affordances
5. SSE state broadcast via Datastar morph events
6. `ServerInstructions` guidance at connection time
7. State transitions as actor messages

**Impact:** Agents succeed on first attempt. ~70-80% reduction in wasted tokens (combined with 1.0).

**Files:** `AppState.fs`, `Mcp.fs`, `McpTools.fs`, `McpServer.fs`. 13 tests.

---

## 1.1 Eval Cancellation & Opt-In Background Evals — ✅ DONE

**Problem:** Infinite loops blocked the actor forever with no recovery.

**Solution — two-mode eval:**
- **Normal eval** (default): No timeout. Cancellable via `cancel_eval` MCP tool / `POST /cancel`. Uses `thread.Interrupt()` pattern.
- **Background eval** (opt-in): `background: bool` parameter on `send_fsharp_code`. Runs on separate thread, doesn't block actor.
- No default timeout — long-running processes (test suites, Aspire) are first-class.

**Files:** `AppState.fs`, `Mcp.fs`, `McpTools.fs`, `McpServer.fs`. 2 integration tests.

---

## 1.2 MCP Autocomplete Tool — ✅ DONE

**Problem:** AI agents couldn't get code completions — only PrettyPrompt users could.

**Solution:** `get_completions` MCP tool with `code` and `cursor_position` parameters. Delegates to existing `AutoCompletion.getCompletions`. `formatCompletions` adapter for MCP response.

**Files:** `McpTools.fs`, `Mcp.fs`, `McpServer.fs`

---

## 1.3 MCP Diagnostics Tool + HTTP Endpoints — ✅ DONE

**Problem:** Agents couldn't pre-validate syntax or type-check without committing to evaluation.

**Implemented:**
- `check_fsharp_code` MCP tool — parses + type-checks WITHOUT evaluating. 9 tests.
- `POST /diagnostics` — accepts `{code}`, returns 202 Accepted. Fire-and-forget.
- `GET /diagnostics` — SSE stream. Full state on connect, pushes updates on change.
- `DiagnosticsStore.T` — accumulated diagnostics keyed by code hash. 9 store unit tests + 2 integration tests.

**Files:** `McpTools.fs`, `Mcp.fs`, `McpServer.fs`, `AppState.fs`

---

## ~~1.4 Event Ring Buffer~~ — Subsumed by 0.0

Marten stores all events in PostgreSQL with built-in pagination. Query with `Sequence > N` for delta events. No ring buffer needed.

---

## 1.5 QualityOfLife.fs — Integrate, Don't Delete — ✅ DONE

**Implemented:** ErrorMessages migrated to own `ErrorMessages.fs`. EvalStats tracking (eval count, timing) moved to `Affordances.fs`. `retryWithBackoff` moved to `Utils.fs`. `QualityOfLife.fs` deleted.

---

## 2.0 Actor Split: Eval vs. Query — ✅ DONE

**Implemented:** Three-actor pattern (Router → EvalActor + QueryActor) with `QuerySnapshot` for immutable data sharing. Internal `EvalCommand`/`QueryCommand` DUs route mutations vs reads. Query actor serves from immutable snapshot — no shared mutable state. Router provides instant dispatch with cooperative cancellation via shared CTS.

---

## 2.4 Structured Output (Beyond Plain Text) — ✅ DONE

**Implemented:** `OutputFormat.Text|Json`, `StructuredEvalResult`, `formatEvalResultJson`, `outputFormat` parameter on `send_fsharp_code`/`check_fsharp_code` MCP tools. 8+ tests.

---

## ~~3.3 Script Persistence & Replay~~ — Subsumed by 0.0

Session replay from Marten event stream. No separate persistence mechanism needed.

---

## Phase 0 Eval Blocking Fix — ✅ DONE

**Problem:** Eval actor blocked on long-running evals — couldn't process `CancelEval` or `HardResetSession`.

**Solution:**
- Eval pipeline runs on dedicated background thread; actor stays responsive
- Router cancels CTS + interrupts eval thread before forwarding reset commands
- Reset handlers wait up to 2s for eval thread to exit, then proceed
- `Console.CancelKeyPress` handler: Ctrl-C cancels running eval without killing process

**Verified:** 205/205 tests pass, MCP stays responsive during blocked evals, hard_reset works while eval is blocked.

---

## 1.0c MCP Error Guidance & Reset Pushback — ✅ DONE

**Implemented (tool descriptions & error messages):**
- `send_fsharp_code` tool description: transaction semantics, error handling guidance, anti-reset language
- `hard_reset_fsi_session` tool description: "⚠️ ALMOST NEVER WHAT YOU WANT", valid/invalid reasons
- `reset_fsi_session` tool description: clarified scope, anti-reset guidance
- `ErrorMessages.fs`: detect "earlier error" specifically, all error suggestions include "do NOT reset" guidance

**Implemented (pushback mechanism):**
- `AppState.HadWarmupFailures: bool` — tracks whether namespace opens failed during `createFsiSession`
- `GetWarmupHealth` function exposed through `ActorResult` → `McpContext`
- `resetPushbackWarning` in `Mcp.fs` — checks `(SessionState, HadWarmupFailures)`: Ready + no failures → ⚠️ warning; warmup failures → no warning
- Both `hardResetSession` and `resetSession` prepend warning when session is healthy, but always execute the reset (education, not blocking)
- 4 Expecto tests: healthy=warning, warmup-failed=no-warning, for both hard & soft reset

**Files changed:** `McpTools.fs`, `ErrorMessages.fs`, `AppState.fs`, `Mcp.fs`, `ActorCreation.fs`, `McpServer.fs`, `CliEventLoop.fs`, `DaemonMode.fs`, `TestInfrastructure.fs`, `SessionResetTests.fs`

---

## Execution Order (Completed Phases)

### Phase 0: Diagnostics First
1. ✅ `check_fsharp_code` MCP tool (1.3a)
2. ✅ `POST /diagnostics` + `GET /diagnostics` SSE (1.3b)
3. ✅ Accumulated diagnostics in AppState (1.3c)

### Phase 0b: Event Store Infrastructure
4. ✅ Marten + Postgres setup (0.0)

### Phase 1: Foundation
5. ✅ Actor split (2.0)
6. ✅ MCP response strategy (1.0)
7. ✅ Affordance-driven MCP (1.0b)
8. ✅ MCP autocomplete tool (1.2)
9. ✅ Eval cancellation (1.1)
10. ✅ QualityOfLife.fs integration (1.5)
11. ✅ Structured output format (2.4)

---

## Quick Wins — ✅ DONE

### JSON Escaping Fix (formatStartupInfoJson)

**Problem:** `formatStartupInfoJson` used manual string interpolation (`sprintf`) to build JSON. Windows paths containing `\n`, `\t`, `\r` produced invalid JSON because JSON parsers interpret these as control characters.

**Fix:** Replaced with `System.Text.Json.JsonSerializer.Serialize` using F# anonymous records. Handles all escaping correctly.

**Commit:** `ac28724`

### JSON Escaping Fix (formatDiagnosticsStoreAsJson)

**Problem:** `formatDiagnosticsStoreAsJson` used manual `sprintf` with format strings to build JSON structure. While string values were escaped via `JsonSerializer.Serialize`, the overall structure was fragile.

**Fix:** Replaced entire function with `JsonSerializer.Serialize` using anonymous records and arrays.

**Commit:** `00395f2`

### DLL Lock Fix (hard_reset with rebuild)

**Problem:** After `FsiEvaluationSession.Dispose()`, the collectible AssemblyLoadContext may not be GC'd immediately, leaving file locks on DLLs. `dotnet build` fails with "Access denied" when trying to overwrite locked files.

**Fix:** Added `GC.Collect() + WaitForPendingFinalizers + GC.Collect()` after session disposal to force ALC unload. Added retry logic: if build fails with "denied"/"locked" in stderr, does another GC cycle + 500ms wait + retry.

**Commit:** `d137005`

### Build Optimization (hard_reset rebuild)

**Problem:** Hard reset with rebuild built each project separately in a loop. For multi-project solutions (e.g., 8 projects), this was 8 redundant `dotnet build` calls.

**Fix:** Build only the primary project. `dotnet build` resolves dependencies transitively, so one build is sufficient.

**Commit:** `93acf9b`

### Flaky Test Fix (DiagnosticsChecked event emission)

**Problem:** `actor emits DiagnosticsChecked on diagnostics request` test was flaky. Event emission happens after `reply.Reply` in the query actor, so the test thread could check `captured` events before `emit` fired.

**Fix:** Added brief spin-wait (up to 100ms in 5ms increments) to let the event propagate.

**Commit:** `9c7e599`

### SessionManager Lifecycle Integration Tests

**Added 3 integration tests** verifying the SessionManager spawn/eval/stop lifecycle with real `SageFs worker` subprocesses:

1. **create session, eval code, stop session** — full lifecycle: spawn worker, connect via named pipe, eval F# code, verify result, check status, stop session
2. **worker crash is detected and session cleaned up** — Erlang-style supervision: spawn worker, kill the process, verify SessionManager detects crash and removes session
3. **multiple sessions are independent** — spawn 2 workers, eval different code in each, verify both return correct results independently

**Commit:** `42e1e59`

---

## 3.2 Daemon Mode & Multi-Session Support (1.0 Scope) — ✅ DONE

### Phase 1: Daemon Extraction (Headless Server)

**Problem:** SageFs dies when the terminal closes because PrettyPrompt crashes without a TTY, taking the MCP server with it.

**Implemented:**
- `SageFs.Server\DaemonMode.fs` — headless daemon entry point, no PrettyPrompt, no console dependencies
- `SageFs.Server\DaemonState.fs` — `~/.SageFs/daemon.json` lifecycle (write/read/clear with PID validation)
- `SageFs.Server\ClientMode.fs` — REPL client that discovers daemon via daemon.json, starts one in background if needed
- `SageFs.Server\Program.fs` — smart CLI routing: `SageFs` (embedded), `SageFs -d` (daemon), `SageFs status/stop` (subcommands)
- `SageFs.Server\WorkerMain.fs` — headless worker process entry point for sub-process sessions

**Commits:** `d7610de`, `e068a27`

### Phase 1b: Worker Process Infrastructure

**Implemented:**
- `SageFs\WorkerProtocol.fs` — `WorkerMessage`/`WorkerResponse` DUs, `SessionProxy` transport abstraction, `SessionInfo` metadata, `SessionStatus` DU, JSON serialization via System.Text.Json + FSharpJsonConverter
- `SageFs\Transports\NamedPipeTransport.fs` — length-prefixed JSON framing over named pipes (fastest local IPC on Windows)
- `SageFs\SessionManager.fs` — Erlang-style supervisor MailboxProcessor: spawn/monitor/restart worker sub-processes, named pipe IPC, exponential backoff via RestartPolicy
- `SageFs\SessionOperations.fs` — pure domain routing (`resolveSession`), session formatting, `SessionResolution` DU
- `SageFs\RestartPolicy.fs` — pure domain module for exponential backoff decisions
- `SageFs\SessionLifecycle.fs` — pure exit outcome classification (Graceful/RestartAfter/Abandoned)

**Key design decisions:**
- Sub-process sessions (not in-process) for true fault isolation — one session crash doesn't affect others
- `SessionProxy = WorkerMessage -> Async<WorkerResponse>` — transport-agnostic, same interface for named pipes, HTTP, or in-process
- `SessionManager` in core `SageFs` library (not `SageFs.Server`) for independent testability
- Rich session metadata: WorkingDirectory, SolutionRoot (auto-detected), CreatedAt, LastActivity, WorkerPid

**Commits:** `251c165`, `306893e`, `e6823fe`, `80bd6ea`, `d1e899a`, `ad7c5b4`, `aca618b`, `4367dd1`, `0e43d0d`

**Tests:** 3 integration tests — full lifecycle (spawn/eval/stop), worker crash detection, multi-session independence. **Commit:** `42e1e59`

### Phase 2: DDD Type Safety Hardening

**Implemented:**
- `WorkerStatusSnapshot.State` changed from `string` to `SessionStatus` DU — **Commits:** `7664801`, `2c77298`
- `DiagnosticSeverity` DU replaced string severity — **Commit:** `44a3b03`
- `SessionState.label` replaced `%A` formatting — **Commit:** `c4a3cbb`
- `ToolUnavailable` DU replaced `checkToolAvailability` string error — **Commit:** `d6b5305`
- `SageFsError` unified DU — consolidated ALL errors (15+ scattered types) into single discriminated union across all layers — **Commit:** `fa5b93b`
- `CompletionKind` DU replaced string Kind on completions — **Commit:** `66323e9`
- `SessionMode` DU — `Embedded | Daemon of SessionManagementOps` for session routing dispatch — **Commit:** `8a6f6c4`
- `SessionManagementOps` record — Task-based function interface decoupling MCP tools from SessionManager internals

### Phase 2b: SessionManager Wiring

**Implemented:**
- DaemonMode creates real SessionManager, builds SessionManagementOps bridge (MailboxProcessor async → Task-based MCP), passes `SessionMode.Daemon` to MCP server
- Graceful shutdown: `StopAll` called before daemon exits
- `McpContext.Mode` field routes session management tools (create/list/stop) through SessionMode DU
- Embedded mode (CLI) uses `SessionMode.Embedded`, daemon mode uses `SessionMode.Daemon`

**Commit:** `665f469`

### Early MCP Status & Bare Mode

**Implemented:**
- MCP server starts BEFORE warm-up completes — `get_fsi_status` available immediately during WarmingUp state
- `--bare` flag for quick sessions without project loading
- `WarmupFailure` details surfaced in status for debugging namespace open failures

**Commit:** `317aa3b`

### What's Remaining (Post-1.0)

Per IMPROVEMENT_PLAN.md priority matrix:
- **Phase 3: Watchdog** — supervisor process with exponential backoff restart
- **Phase 4: REPL client polish** — interactive session picker, `:switch`/`:sessions` commands

---

## Post-1.0 Completed Features

### 3.2b Per-Tool Session Routing — ✅ DONE

**Problem:** All MCP tools operated on the local in-process FSI actor. In daemon mode with multiple sessions, there was no way to route tool calls to a specific worker session.

**Implemented:**
- `SessionMode` DU (`Embedded | Daemon of SessionManagementOps`) in `SageFs\SessionMode.fs`
- `SessionManagementOps` record with `GetProxy: SessionId -> Task<SessionProxy option>`
- All 5 MCP tool functions gained `sessionId: string option` parameter
- `routeToSession` helper resolves proxy and dispatches `WorkerMessage`
- `formatWorkerEvalResult` translates `WorkerResponse` to MCP format
- Default behavior (sessionId = None) uses local actor — backward compatible

**Commit:** `fd11c03`

### 3.5 Startup Profile — ✅ DONE

**Implemented:** `~/.SageFs/init.fsx` auto-loaded on session start. Per-project startup scripts.

**Commit:** `cca2051`

### 3.4 Package Explorer — ✅ DONE

**Implemented:** `explore_namespace`, `explore_type` MCP tools registered for assembly exploration.

### 2.2 File Watching & Incremental Reload — ✅ DONE

**Problem:** When a user edits `.fs` files in their project, they had to manually hard-reset SageFs to pick up changes — a 30-60s rebuild every time.

**Implemented:**
- `SageFs\FileWatcher.fs` — pure types (`FileChangeKind` DU, `FileChange` record, `WatchConfig`, `FileChangeAction` DU) and pure functions (`shouldTriggerRebuild`, `shouldExcludeFile`, `fileChangeAction`)
- **Incremental `#load` reload** (~100ms) instead of hard reset (30-60s) for `.fs`/`.fsx` changes
- **Escalation chain**: `.fs`/`.fsx` → `Reload` (FSI `#load`), `.fsproj` → `SoftReset`, deletions → `Ignore`
- **Glob-based exclude patterns** (`ExcludePatterns` on `WatchConfig`) — supports `**`, `*`, case-insensitive matching
- **`--no-watch` CLI flag** to disable file watching entirely
- Debounced `FileSystemWatcher` with configurable delay (default 500ms) coalesces rapid file changes
- Wired into `CliEventLoop.fs` and `DaemonMode.fs` — starts after warm-up completes
- `ActorCreation.fs` — `projectDirectories` extracts unique project dirs from solution for watch targets
- Filters: watches `.fs`, `.fsx`, `.fsproj`; excludes temp files (`~`, `.tmp`), `bin/`, `obj/`
- MCP tool descriptions and server instructions updated to reflect automatic file watching
- `formatEnhancedStatus` shows file watcher status in startup info

**Tests:** 32 tests for `shouldTriggerRebuild`, `shouldExcludeFile`, `fileChangeAction`, `defaultWatchConfig` (293/293 total)

**Commits:** `bae50c4` (initial), `22164b1` (incremental reload)

---

## REPL/TUI Architecture Research — ✅ DONE

**Problem:** PrettyPrompt couples the REPL to a terminal-only, Windows-console-dependent, closed-source library. SageFs needs a multi-frontend architecture (terminal, Datastar web, Neovim, VSCode, Raylib/ImGui) with a unified immediate-mode rendering pipeline.

**Research output:** `docs/repl-tui-research.md` (~2650 lines) — comprehensive analysis of:
- Candidate libraries (RadLine, Terminal.Gui, Spectre.Console, Reedline, Bubble Tea, python-prompt-toolkit)
- Tree-sitter foundation (ionide/tree-sitter-fsharp via TreeSitter.DotNet)
- Push-based reactive streaming architecture (SageFsEvent bus, IObservable)
- Neovim UI protocol precedent (batch-and-flush, highlight table indirection, ext_ capabilities)
- Immediate-mode philosophy (Fleury RAD Debugger feature flags, Muratori IMGUI, Datastar)
- Core domain types (RenderRegion, RegionFlags, Affordance, EditorAction, EditorEffect, SageFsView, KeyMap)
- Session registry with multi-session display
- Interface contract (IFrontendAdapter, HATEOAS/Datastar web adapter)
- Elm Architecture decision (custom ~40-line loop over Fable.Elmish)
- Five recommended build phases with Datastar as architecture validation
- REPL killer features taxonomy (4 priority tiers from 7+ research sources)

**Architecture decision:** Option D — Immediate-Mode Elm Architecture in F#. Custom Elm loop, `RenderRegion list` as universal render output, Fleury-style `RegionFlags` over discriminated union widget kinds, affordance-driven HATEOAS for all UIs.

**Files:** `docs/repl-tui-research.md`

**Commits:** `6698ce1` (initial research), `01407ff` (Neovim + Elm loop + scope corrections), `b32b41e` (coherence fixes)

---

## Live Testing Engine — ✅ DONE

**Problem:** Visual Studio Enterprise charges $250/month for "Live Unit Testing." SageFs should make that look foolish — instant, framework-agnostic, editor-agnostic live test feedback.

**Implemented — Full end-to-end pipeline:**

1. **Test Discovery** — Reflection-based discovery via `LiveTestingExecutors.fs` (622 lines). Two-tier provider model:
   - Tier 1: Attribute-based (xUnit `[<Fact>]`, NUnit `[<Test>]`, MSTest `[<TestMethod>]`, TUnit `[<Test>]`) — ~10 lines per framework
   - Tier 2: Custom (Expecto) — runtime reflection to enumerate value-based test trees
2. **Hot Reload Integration** — `HotReloading.fs` calls `LiveTestingHook.afterReload` after every eval. Discovers providers, finds affected tests. `injectNoInlining` ensures Harmony detours work.
3. **Pipeline State Machine** — `LiveTestPipelineState` in `LiveTestingTypes.fs` (1,808 lines). Pure domain model with:
   - `onKeystroke`/`onFileSave` trigger debounce channels
   - `tick` (50ms timer) fires pipeline effects
   - `PipelineEffect` DU: ParseTreeSitter → RequestFcsTypeCheck → RunAffectedTests
   - `CancellationChain` prevents stale results from overwriting newer ones
   - `RunGeneration` + `ResultFreshness` track staleness across code edits
   - `AdaptiveDebounce` adjusts timing based on project size
4. **Elm Loop Integration** — `SageFsApp.fs` handles all live testing messages:
   - `ProvidersDetected`, `TestsDiscovered`, `AffectedTestsComputed` → populate state
   - `TestRunStarted` → update run phase
   - `TestResultsBatch` → merge results, detect retrigger needs, recompute statuses
   - `RunAffectedTests` effect handler calls `TestOrchestrator.executeFiltered` with semaphore-limited parallelism
5. **Gutter Icon Rendering** — 8 `GutterIcon` types (TestDiscovered, TestPassed, TestFailed, TestRunning, TestSkipped, TestFlaky, Covered, NotCovered) mapped to Unicode chars and colors. `CachedEditorAnnotations` recomputed on every state change.
6. **Run Policies** — `RunPolicy` (OnEveryChange, OnSaveOnly, OnDemand, Disabled) per `TestCategory` (Unit, Integration, Browser, Benchmark, Architecture, Property, Custom). Configurable via MCP tool and HTTP endpoint.
7. **OTEL Instrumentation** — `LiveTestingInstrumentation.fs` with ActivitySource and Histogram metrics for pipeline phase timing (~50ns overhead when no collector).
8. **Tree-Sitter Source Detection** — `tests.scm` (84 lines) detects `[<Fact>]`, `[<Test>]`, `[<Tests>]`, `[<Benchmark>]`, `[<Property>]` attributes for source-level test location mapping.
9. **HTTP Endpoints** — `POST /api/live-testing/toggle`, `POST /api/live-testing/policy`, `POST /api/live-testing/run`, `GET /api/live-testing/status`
10. **SSE Broadcasting** — `SseWriter.formatTestResultsBatchEvent` serializes `TestResultsBatchPayload` as SSE events for editor consumption

**Files:** `SageFs.Core/Features/LiveTestingTypes.fs`, `LiveTestingExecutors.fs`, `LiveTestingInstrumentation.fs`, `TestTreeSitter.fs`, `SageFs.Core/Middleware/HotReloading.fs`, `SageFs.Core/SageFsApp.fs`, `SageFs.Core/SseWriter.fs`

**Tests:** Full pipeline roundtrip integration tests (keystroke → debounce → FCS → affected tests → execution), debounce property tests, cancellation chain tests, policy filtering tests — part of 1,400+ test suite across 81 test files.

---

## CI/CD Auto-Release Pipeline — ✅ DONE

**Implemented:** GitHub Actions on every master push:
- Build + test on Ubuntu + Windows matrix
- Expecto exit code 2 (no TTY) handled as success
- Auto-pack NuGet tool, build VS Code VSIX, build VS VSIX
- Push to NuGet, create GitHub Release with all artifacts
- Tag `sagefs.nvim` repo with matching version
- VSIX packages clearly named: `sagefs-vscode-*.vsix` and `sagefs-visualstudio-*.vsix`

**File:** `.github/workflows/main.yml`

---

## VS Code Extension — ✅ DONE

**Implemented:** Full-featured VS Code extension (`sagefs-vscode/`) built with Fable (F# → JavaScript):
- 23 commands (eval, file eval, range eval, daemon start/stop, session management, hot reload, live testing, run policy)
- CodeLens providers (eval + test)
- Diagnostics integration
- SSE subscriber for real-time updates
- Tree views: Hot Reload files, Session Context, Type Explorer
- Test decorations (inline pass/fail indicators)
- Dashboard webview panel
- Test Explorer integration via TestController adapter
- Call graph viewer
- Event history QuickPick

**Files:** 14 .fs files in `sagefs-vscode/src/`

---

## Visual Studio Extension — ✅ DONE

**Implemented:** VS 2022+ extension using new OOP extensibility SDK (`sagefs-vs/`):
- 24 commands (eval, file eval, range eval, daemon, sessions, hot reload, live testing, run policy)
- 4 tool windows: Live Testing Dashboard, Type Explorer, Hot Reload Files, Session Context
- CodeLens providers (eval + test)
- Diagnostics bridge for Error List integration
- SSE subscriber for real-time updates
- F# core library + C# shim pattern for SDK compatibility

**Files:** 7 .fs files in `SageFs.VisualStudio.Core/`, 26 .cs files in `SageFs.VisualStudio/`

**SDK limitations (blocked, not SageFs issues):**
- No text adornment/decoration API (inline test results)
- No completion/IntelliSense provider API
- No Test Explorer integration API

---

## Neovim Plugin (sagefs.nvim) — ✅ DONE

**Implemented:** Standalone Lua plugin at `WillEhrendreich/sagefs.nvim`:
- 23 Lua modules, 669 tests (617 busted + 52 integration)
- Cell evaluation, inline results, gutter signs
- SSE live updates, session management
- Live test panel, coverage gutter signs
- Type explorer, history browser
- Daemon lifecycle management
- Code completion integration
- CI with GitHub Actions (unit + E2E)

---

## Unified CLI Arg Parser — ✅ DONE

**Problem:** Argu dependency added weight and didn't support `--help` correctly.

**Implemented:** Custom arg parser removing Argu dependency entirely. Supports `--help`, `--version`, `--bare`, `--no-watch`, `--no-resume`, `--prune`, `--sln`, `--proj`, `--dir`, `--reference`, `--load`, `--use`, `--lib`, `--other`.

**Commits:** `d31b95e`, `74d3c2c`
