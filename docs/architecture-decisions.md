# Architecture Decision Records

Key decisions that shape SageFs's architecture. Written for future contributors
who need to understand **why**, not just what.

---

## ADR-1: SSE as the Only Read Channel (CQRS)

**Decision**: All client reads flow through a single SSE stream (`/events` or `/api/state`).
No GET endpoints return domain data. POST endpoints return only acknowledgment (`202 Accepted`).

**Why**: SageFs has 6 concurrent frontends — VS Code, Neovim, Visual Studio, TUI, Raylib GUI,
and the web dashboard. If each frontend polled for state, we'd need rate limiting, cache
invalidation, and stale-data reconciliation across all 6. SSE gives us push semantics: when
state changes, every connected client gets the update within milliseconds.

**The SSE Parity Contract**: `SseParityTests.fs` is a living test that ensures every SSE event
type is handled by every client. When a new event type is added to the daemon, the test fails
until every client adds a handler. This prevents the "VS Code handles it but Neovim doesn't"
class of bugs.

**Tradeoff**: Clients that connect after an event miss it. We handle this via:
- The `state` event broadcasts a full snapshot periodically
- Clients request `GET /api/state` on initial connect (the one exception to "no GET for data")

**What would change this**: If we needed offline-capable clients or multi-node daemon deployment,
we'd need event persistence (not just in-memory broadcast). Binary manifest persistence
(`.sagefm` files) partially addresses this for session state.

---

## ADR-2: SageTUI Migration (Elm Architecture for Terminal UI)

**Decision**: The terminal UI was rebuilt from imperative mutable code (~457 lines of `CellGrid.rent`
→ `Screen.drawWith` → `AnsiEmitter.emit`) to SageTUI's Elm Architecture (~736 lines of
`init/update/view/subscribe`).

**Why**: The imperative TUI had three problems:
1. **State synchronization** — SSE events arrived asynchronously and mutated shared state.
   Race conditions were possible between render and update.
2. **Testability** — The imperative renderer coupled state, rendering, and I/O. Testing
   required mocking terminal output.
3. **Feature velocity** — Adding a new pane or keybinding required understanding the entire
   mutable state graph.

The Elm Architecture (TEA) solves all three: `update` is a pure function (testable),
`view` is a pure function (snapshot-testable), and `subscribe` declares what external events
to listen to. SageTUI handles terminal setup (alt screen, raw mode, mouse protocol),
SIMD-accelerated cell diffing, and frame scheduling.

**Tradeoff**: 736 lines > 457 lines. The functional approach has more ceremony (discriminated
unions for messages, explicit model threading). But the code is locally reasonable — you can
understand `update` without understanding `view`, and vice versa.

**Legacy fallback**: `sagefs tui --legacy-tui` preserves the old renderer. This will be removed
once the SageTUI client reaches feature parity and stability confidence.

---

## ADR-3: Binary Manifest Persistence (.sagefm)

**Decision**: Session state and test results are persisted in a custom binary format (`.sagefm`)
with CRC-32C validation, not JSON or SQLite.

**Why**: Session state is written on every eval completion and test run. At ~200-500ms cycle
times, that's 2-5 writes/second. JSON serialization + file I/O at that rate introduces GC
pressure from string allocation. The binary format writes fixed-size records with no allocation:
- `DateTimeOffset` → `int64` (Unix ms)
- `string` → length-prefixed UTF-8 bytes
- `TestResult` → tag byte + payload

CRC-32C validates integrity on read. Corrupted manifests are discarded (not repaired).

**Tradeoff**: The format is opaque. You can't `cat` a `.sagefm` file. Debugging requires
`sagefs dump-manifest` or the test helpers.

**What would change this**: If we needed cross-tool interop (e.g., other tools reading SageFs
state), we'd add a JSON export alongside the binary format, not replace it.

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
appropriate UI *and* offer contextual rescue actions ("Retry with longer timeout", "Cancel").
A raw string like `"Evaluation timed out after 5000ms"` forces each client to regex-parse
for context.

**Tradeoff**: Every new error condition requires adding a DU case, updating `toJson`, and
potentially updating all 6 clients. This is deliberate — it forces us to think about the
user-facing error experience for every failure mode.

---

## ADR-5: Dual Renderer (TUI + Raylib GUI)

**Decision**: TUI and Raylib GUI share the same `Cell[,]` grid abstraction and `Screen.draw`
pipeline. Backend-specific code (ANSI emission vs Raylib draw calls) is isolated to emitters.

**Why**: SageFs targets developers who might prefer either a terminal workflow or a GPU-rendered
window. Maintaining two completely separate rendering pipelines would mean every visual feature
is implemented twice with divergent behavior. The shared pipeline means:
- Feature parity is enforced by construction (same `RenderRegion` list, same `PaneRenderer`)
- Theme colors are abstract IDs — TUI maps to 256-color ANSI, Raylib maps to RGB
- Snapshot tests validate at the `CellGrid` level — backend-agnostic

**Tradeoff**: The abstraction introduces a lowest-common-denominator constraint. Raylib can do
things terminals can't (alpha blending, smooth scrolling, sub-character positioning). We accept
this limitation for parity, and add Raylib-only features (font zoom, text selection) as
enhancements on top of the shared pipeline.

---

## ADR-6: MCP as the AI Interface

**Decision**: SageFs exposes 50 tools via [Model Context Protocol](https://modelcontextprotocol.io/)
with an affordance-driven state machine — AI agents only see tools valid for the current
session state.

**Why**: AI agents (Copilot, Claude, etc.) need structured interfaces, not CLI output parsing.
MCP provides:
- Tool discovery with typed schemas
- Session-aware state (agents get tools relevant to their current context)
- No terminal emulation required

The affordance-driven state machine is critical: an agent with no active session sees
`create_session` prominently but not `run_tests`. This reduces wasted tokens and invalid
tool calls.

**Tradeoff**: MCP is relatively new. If the protocol evolves significantly, we'll need to
update. We mitigate by keeping MCP as a thin wrapper over the same HTTP+SSE daemon APIs
that all other clients use — MCP tools call the same endpoints.

---

## ADR-7: No Interfaces (F# Module Composition)

**Decision**: SageFs uses zero C#-style interfaces. Abstraction is via function signatures
and module composition.

**Why**: F#'s type inference + higher-order functions + discriminated unions provide all the
polymorphism we need without the ceremony of interface hierarchies. Where C# would define
`ITestRunner`, F# passes `TestCase -> Async<TestResult>`. Where C# would use dependency
injection containers, F# partially applies functions at the composition root.

**The exception**: Interop boundaries. The Visual Studio extension (C#) uses interfaces
because the VS extensibility SDK requires them. The F# core logic behind those interfaces
uses module composition internally.

**What would change this**: If we ever needed runtime plugin loading (e.g., third-party test
framework adapters loaded from NuGet), we might introduce a minimal interface for the plugin
contract. But for now, all providers are statically composed.
