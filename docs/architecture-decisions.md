# Architecture Decision Records

These are the key decisions that shape SageFs's architecture, written to explain the reasoning behind them for future contributors, not just the decisions themselves.

> **Historical status:** ADR-2 and ADR-5, along with the frontend lists in other early ADRs, describe the former built-in SageTUI, legacy TUI, and `SageFs.Gui` Raylib product frontends. Those frontends are now deprecated, and for current product direction these decisions are superseded by the web dashboard, editor integrations, and MCP. The original records are unchanged and kept as architectural history. Raylib application and game demos are not deprecated.

---

## ADR-1: SSE as the Only Read Channel (CQRS)

**Decision**: All client reads flow through a single SSE stream (`/events` or `/api/state`).
No GET endpoints return domain data. POST endpoints return only acknowledgment (`202 Accepted`).

**Why**: SageFs has 6 concurrent frontends — VS Code, Neovim, Visual Studio, TUI, Raylib GUI,
and the web dashboard. If each frontend polled for state, we'd need rate limiting, cache
invalidation, and stale-data reconciliation across all 6. SSE pushes updates instead: when
state changes, every connected client gets the update within milliseconds.

**The SSE Parity Contract**: `SseParityTests.fs` is a test that checks every SSE event type
is handled by every client. When a new event type is added to the daemon, the test fails
until every client adds a handler. This catches bugs where, for example, VS Code handles an
event but Neovim doesn't.

**Tradeoff**: Clients that connect after an event miss it. This is handled two ways:
- The `state` event broadcasts a full snapshot periodically
- Clients request `GET /api/state` on initial connect (the one exception to "no GET for data")

**What would change this**: Supporting offline-capable clients or multi-node daemon deployment
would require event persistence instead of just in-memory broadcast. Binary manifest
persistence (`.sagefm` files) partially covers this for session state.

---

## ADR-2: SageTUI Migration (Elm Architecture for Terminal UI)

**Decision**: The terminal UI was rebuilt from imperative mutable code (~457 lines of `CellGrid.rent`
→ `Screen.drawWith` → `AnsiEmitter.emit`) to SageTUI's Elm Architecture (~736 lines of
`init/update/view/subscribe`).

**Why**: The imperative TUI had three problems:
1. **State synchronization**: SSE events arrived asynchronously and mutated shared state,
   which could cause race conditions between render and update.
2. **Testability**: The imperative renderer coupled state, rendering, and I/O, so testing
   required mocking terminal output.
3. **Feature velocity**: Adding a new pane or keybinding required understanding the entire
   mutable state graph.

The Elm Architecture (TEA) addresses all three: `update` is a pure function, so it's testable;
`view` is a pure function, so it's snapshot-testable; and `subscribe` declares which external
events to listen to. SageTUI handles terminal setup (alt screen, raw mode, mouse protocol),
SIMD-accelerated cell diffing, and frame scheduling.

**Tradeoff**: 736 lines is more than 457 lines, and the functional approach has more ceremony
(discriminated unions for messages, explicit model threading). Each part is still reasoned
about locally: you can understand `update` without understanding `view`, and vice versa.

**Legacy fallback**: `sagefs tui --legacy-tui` preserves the old renderer. It will be removed
once the SageTUI client reaches feature parity and is stable enough.

---

## ADR-3: Binary Manifest Persistence (.sagefm)

**Decision**: Session state and test results are persisted in a custom binary format (`.sagefm`)
with CRC-32C validation, not JSON or SQLite.

**Why**: Session state is written on every eval completion and test run. At roughly 200-500ms
cycle times, that's 2-5 writes per second. JSON serialization plus file I/O at that rate
creates GC pressure from string allocation. The binary format writes fixed-size records with
no allocation:
- `DateTimeOffset` → `int64` (Unix ms)
- `string` → length-prefixed UTF-8 bytes
- `TestResult` → tag byte + payload

CRC-32C validates integrity on read. Corrupted manifests are discarded (not repaired).

**Tradeoff**: The format is opaque: you can't `cat` a `.sagefm` file directly, and debugging
requires `sagefs dump-manifest` or the test helpers.

**What would change this**: Supporting cross-tool interop (for example, other tools reading
SageFs state) would mean adding a JSON export alongside the binary format, not replacing it.

---

## ADR-4: Typed Errors (SageFsError DU)

**Decision**: All daemon errors are represented as a 30-case discriminated union (`SageFsError`)
serialized to JSON with `SageFsError.toJson`. No raw exception messages reach clients.

**Why**: SageFs has 6 different frontend rendering technologies. Each needs to display errors
differently:
- VS Code: notification toasts with action buttons
- Neovim: `vim.notify` with structured detail in floating windows
- Visual Studio: InfoBar with clickable actions
- TUI: status bar flash + output pane
- Raylib: overlay panel

A typed error like `EvalTimeout { sessionId; elapsedMs; limitMs }` lets each client render
the right UI and offer contextual actions ("Retry with longer timeout", "Cancel"). A raw
string like `"Evaluation timed out after 5000ms"` would force each client to regex-parse it
for context.

**Tradeoff**: Every new error condition requires adding a DU case, updating `toJson`, and
potentially updating all 6 clients. This is deliberate: it forces us to think through the
user-facing error experience for every failure mode.

---

## ADR-5: Dual Renderer (TUI + Raylib GUI)

**Decision**: TUI and Raylib GUI share the same `Cell[,]` grid abstraction and `Screen.draw`
pipeline. Backend-specific code (ANSI emission vs Raylib draw calls) is isolated to emitters.

**Why**: SageFs targets developers who might prefer either a terminal workflow or a
GPU-rendered window. Maintaining two completely separate rendering pipelines would mean
implementing every visual feature twice, with a risk of divergent behavior. The shared
pipeline means:
- Feature parity is enforced by construction (same `RenderRegion` list, same `PaneRenderer`)
- Theme colors are abstract IDs: TUI maps them to 256-color ANSI, Raylib maps them to RGB
- Snapshot tests validate at the `CellGrid` level, so they work for both backends

**Tradeoff**: The abstraction limits both renderers to their common ground. Raylib can do
things terminals can't, like alpha blending, smooth scrolling, and sub-character
positioning. This limitation is accepted for the sake of parity, and Raylib-only features
(font zoom, text selection) are added as extras on top of the shared pipeline.

---

## ADR-6: MCP as the AI Interface

**Decision**: SageFs exposes 60 tools via [Model Context Protocol](https://modelcontextprotocol.io/).
A state machine decides which tools are valid to *call* in the current session state.

**Why**: AI agents (Copilot, Claude, and others) need structured interfaces instead of
parsing CLI output. MCP provides tool discovery with typed schemas and no need for
terminal emulation.

**Current status — call-time gate, not a filtered list**: the `tools/list` response is
static and unfiltered — an agent always sees the full 60-tool catalog, in every session
state. What the state machine actually gates is *calling* a tool: `enforceToolCallGate`
rejects a call to a tool that doesn't apply to the current state with a structured error
([`SageFs/Mcp.fs:614`](https://github.com/WillEhrendreich/SageFs/blob/073bd7f3f1324233b747bd7cb31c343dc318021c/SageFs/Mcp.fs#L614),
wired in [`SageFs/McpServer.fs:433`](https://github.com/WillEhrendreich/SageFs/blob/073bd7f3f1324233b747bd7cb31c343dc318021c/SageFs/McpServer.fs#L433)).
`get_session_status` reports which tools currently apply, but that's a self-service hint an
agent has to read — not an enforced visibility filter. There is no `AddListToolsFilter`
wired anywhere in the codebase. See [MCP Tools](mcp-tools.md) for the accurate framing.

**Tradeoff**: MCP is relatively new, so significant protocol changes will require updates
on our side. To limit that risk, MCP is kept as a thin wrapper over the same HTTP+SSE
daemon APIs every other client uses; MCP tools call the same endpoints.

---

## ADR-7: No Interfaces (F# Module Composition)

**Decision**: SageFs uses zero C#-style interfaces. Abstraction is via function signatures
and module composition.

**Why**: F#'s type inference, higher-order functions, and discriminated unions provide all
the polymorphism needed, without the ceremony of interface hierarchies. Where C# would
define `ITestRunner`, F# passes `TestCase -> Async<TestResult>`. Where C# would use
dependency injection containers, F# partially applies functions at the composition root.

**The exception**: interop boundaries. The Visual Studio extension (C#) uses interfaces
because the VS extensibility SDK requires them, but the F# core logic behind those
interfaces still uses module composition internally.

**What would change this**: Runtime plugin loading (for example, third-party test framework
adapters loaded from NuGet) might call for a minimal interface for the plugin contract. For
now, all providers are statically composed.
