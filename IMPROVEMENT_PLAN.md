# SageFs Improvement Plan — The Premier F# Interactive Experience

## Vision

SageFs should be the definitive way to work with F# interactively — whether you're a developer at a terminal, an AI agent over MCP, or an IDE extension. It should be to F# what Jupyter is to Python, but native, fast, and deeply integrated with the .NET ecosystem.

### Architectural Philosophy: Affordance-Driven Interaction (Inspired by LARP)

SageFs adopts the core principle of the [LARP protocol](https://speakez.tech/blog/larp-protocol/): **the server tells agents what they can do right now, not what they might be able to do in general.**

Current MCP integration presents 8 static tools to every agent at all times. The agent reasons about which might apply, constructs an invocation, and sometimes fails because the session isn't in the right state. This wastes tokens on navigation — LARP estimates 60-70% of an agent's context goes to figuring out what's valid rather than doing actual work.

**SageFs's affordance model:**

The SageFs session is an explicit state machine. At each state, only the valid actions are surfaced to the agent. Invalid tools don't exist in the agent's world.

```
                    ┌─────────────┐
                    │ Uninitialized│
                    └──────┬──────┘
                           │ startup
                    ┌──────▼──────┐
              ┌────►│  Warming Up  │───────────┐
              │     └──────┬──────┘            │
              │            │ warm-up complete   │ warm-up failed
              │     ┌──────▼──────┐     ┌──────▼──────┐
              │┌───►│    Ready     │◄──┐│   Faulted    │
              ││    └──────┬──────┘   │└──────────────┘
              ││           │ eval     │  (reset / hard-reset ──► WarmingUp)
              ││    ┌──────▼──────┐   │
              ││    │  Evaluating  │──┘ eval complete / cancel
              ││    └──────┬──────┘
              ││           │ error (unrecoverable)
              │└───────────┼──────── Faulted
              │            │
              └────────────┘ (hard-reset from ANY state ──► WarmingUp)
```

> **Note on HardReset:** `HardReset` is valid from *any* state, including `Evaluating` and `WarmingUp`. It always transitions to `WarmingUp`. This is the "escape hatch" for any stuck session.

**Affordance table — what's available at each state:**

| State | Available Tools | NOT Available | Rationale |
|-------|----------------|---------------|-----------|
| **Uninitialized** | `get_fsi_status` | Everything else | Session doesn't exist yet. Don't offer eval, completions, diagnostics. |
| **Warming Up** | `get_fsi_status` | Everything else | Warm-up is in progress. Agent should wait for Ready. |
| **Ready** | `send_fsharp_code`, `check_fsharp_code`, `get_completions`, `load_fsharp_script`, `get_recent_fsi_events`, `get_fsi_status`, `get_available_projects`, `reset_fsi_session`, `hard_reset_fsi_session` | `cancel_eval` | Full capability. No eval in progress to cancel. |
| **Evaluating** | `cancel_eval`, `get_fsi_status`, `get_recent_fsi_events`, `check_fsharp_code`, `get_completions` | `send_fsharp_code`, `load_fsharp_script`, `reset_fsi_session`, `hard_reset_fsi_session` | Can't submit more code while evaluating. CAN still check diagnostics/completions (query actor). CAN cancel. |
| **Faulted** | `reset_fsi_session`, `hard_reset_fsi_session`, `get_fsi_status`, `get_recent_fsi_events` | `send_fsharp_code`, `load_fsharp_script`, `check_fsharp_code`, `get_completions` | Session is broken. Only recovery actions and inspection. |

**How this is implemented:**

MCP doesn't natively support dynamic tool lists. But it does support `ServerInstructions` (sent once at connection) and tool descriptions. The implementation:

1. **Session state as a first-class type** — `SessionState` DU: `Uninitialized | WarmingUp | Ready | Evaluating | Faulted of exn`. The CTS for cancellation lives in the actor's mutable context alongside the state, not inside the DU — keeping the state pure, serializable, and testable ([Functional Core, Imperative Shell](https://blog.ploeh.dk/2020/03/02/impureim-sandwich/)).
2. **State transitions as a type** — `SessionTransition` DU ensures the state machine is *total*:
   ```fsharp
   type SessionTransition =
     | StartWarmUp       // Uninitialized → WarmingUp
     | WarmUpCompleted   // WarmingUp → Ready
     | WarmUpFailed      // WarmingUp → Faulted
     | StartEval         // Ready → Evaluating
     | EvalCompleted     // Evaluating → Ready
     | EvalFailed        // Evaluating → Faulted (unrecoverable only)
     | EvalCancelled     // Evaluating → Ready
     | Reset             // Faulted|Ready → WarmingUp
     | HardReset         // Any → WarmingUp

   let applyTransition (state: SessionState) (transition: SessionTransition)
     : Result<SessionState, SessionError> =
     match state, transition with
     | Uninitialized, StartWarmUp -> Ok WarmingUp
     | WarmingUp, WarmUpCompleted -> Ok Ready
     | WarmingUp, WarmUpFailed -> Ok (Faulted ...)
     | Ready, StartEval -> Ok Evaluating
     | Evaluating, EvalCompleted -> Ok Ready
     | Evaluating, EvalCancelled -> Ok Ready
     | _, HardReset -> Ok WarmingUp  // always valid
     | _ -> Error (InvalidTransition (state, transition))
   ```
3. **Domain-specific error types** — replace `string` errors with typed DU:
   ```fsharp
   type SessionError =
     | ProjectNotFound of path: string
     | CompilationFailed of diagnostics: Diagnostic list
     | EvalTimeout of code: string * elapsed: TimeSpan
     | FsiCrashed of message: string
     | InvalidTransition of state: SessionState * attempted: SessionTransition
     | SessionNotFound of id: SessionId
     | DaemonNotRunning
     | PortInUse of port: int
   ```
2. **Affordance computation** — pure function `availableTools : SessionState -> ToolName list` returns which tools are valid
3. **Tool-level enforcement** — each MCP tool checks session state before executing. If invalid, returns a structured error: `"Cannot eval: session is in 'Evaluating' state. Available actions: cancel_eval, get_fsi_status"`
4. **SSE state broadcast** — `GET /status` SSE stream pushes `datastar-patch-signals` with current state + available affordances on every transition. The Neovim plugin uses this to enable/disable keybindings
5. **ServerInstructions** — initial connection message tells the agent: "SageFs is an affordance-driven REPL. Check `get_fsi_status` to see what actions are available. Only invoke tools listed as available."
6. **Graceful recovery** — when a tool is invoked in the wrong state, the error response includes which tools WOULD satisfy the preconditions (LARP's "recursive goal pursuit"). E.g., invoking `send_fsharp_code` while Faulted returns: `"Session is faulted. Run reset_fsi_session or hard_reset_fsi_session first."`

**Why this matters for SageFs specifically:**

- **Token savings** — agents don't waste context reasoning about whether to reset or eval or check status. The state machine tells them.
- **Safety** — `hard_reset_fsi_session` is only offered when truly needed. Agents won't nuke sessions unnecessarily.
- **Cancellation UX** — when evaluating, `cancel_eval` is the FIRST tool listed. The agent sees it immediately instead of buried in a catalog of 8.
- **Concurrent query safety** — the affordance table explicitly shows that `check_fsharp_code` and `get_completions` are available during eval (because they route to the query actor, not the eval actor). This guides the agent to use them.

---

### HTTP Surface: The Tao of Datastar (Full CQRS + SSE + Morph)

The entire HTTP API follows one pattern: **commands in (POST), SSE streams out (GET).**

SageFs already uses Falco + Falco.Markup. We add **`Falco.Datastar`** (v1.3.0, the Datastar bindings for Falco) to get first-class Datastar SSE event generation. This is the natural fit — same Falco ecosystem, F#-native API, integrates directly with our existing Falco request/response pipeline.

**Rules:**
- **POST endpoints** are commands. They accept a request, validate it, dispatch it to the appropriate actor, and return `202 Accepted`. They never return domain data in the response body. The results flow out through SSE streams.
- **GET endpoints** are SSE subscriptions using the Datastar event format (`Falco.Datastar` generates these). Each resource has its own SSE stream. Full state on connect, then morph fragments on change.
- **MCP tools** are request/response by nature (the MCP protocol requires it), so they return results directly. They are the exception, not the rule. The HTTP surface is the primary integration point for IDE plugins.
- **Design note: two interaction patterns, one server.** MCP tools are request-response (agent submits code, gets result directly). HTTP surface uses `POST → 202 → SSE` for IDE plugins that maintain persistent subscriptions (subscribe once at startup, receive all state changes reactively). Both patterns coexist — the server is the single source of truth. Commands come in through any door (MCP tool, HTTP POST, terminal REPL). State changes flow out through SSE to all subscribers.

**New dependency (add to `Directory.Packages.props`):**
```xml
<PackageVersion Include="Falco.Datastar" Version="1.3.0" />
```

This is Datastar's philosophy applied to a dev tool: the server is the source of truth, clients subscribe and receive. No polling, no stale state, no request-response chattering.

---

### The LLM Context Window Problem

The data SageFs produces is F# code, type signatures, diagnostics, event histories, and session metadata — not HTML. Datastar's DOM morphing algorithm is designed for HTML elements. The question is: **where does morphing actually help, and what does the LLM actually need?**

**What flows to the LLM (MCP tool responses):**

| Tool | What it returns | Typical tokens | Redundancy problem |
|------|----------------|---------------|-------------------|
| `send_fsharp_code` | `Code: [echoed statement]\nResult: [output]` + diagnostics + stdout | 50–500 | Echoes back code the LLM just sent. Multi-statement evals repeat this per statement. |
| `get_recent_fsi_events` | `[ISO-timestamp] source: text` × N events | 50–300 | Full timestamps per event. Previous events re-sent on every call. Unbounded growth. |
| `get_fsi_status` | Session ID, event stats, projects, startup info, tool list, usage tips | 150–250 | Hardcoded tool list + usage tips repeated on every call (~80 tokens of boilerplate). |
| `get_startup_info` | Args, working dir, projects, assemblies, hot reload, hints | 120–180 | Overlaps heavily with `get_fsi_status`. Same hints repeated. |
| `get_available_projects` | Directory listing + 3 hardcoded usage hints | 60–150 | Hints repeated every call. |
| `reset_fsi_session` | "Session reset successfully." | 10–30 | Fine — small and focused. |
| `load_fsharp_script` | "Loaded N statements" or error list | 30–200 | Fine — proportional to actual content. |

**The real problems for LLM context are:**

1. **Echoed code** — `send_fsharp_code` returns `Code: [the code the LLM just sent]`. The LLM already knows what it sent. This is pure waste. ~20-200 tokens per eval.

2. **Repeated boilerplate** — `get_fsi_status`, `get_startup_info`, and `get_available_projects` all include static hints, tool lists, and emoji headers that never change between calls. ~80 tokens of noise per status check.

3. **Unbounded event history** — `get_recent_fsi_events` returns the last N events, but many of those events are from previous eval cycles the LLM already saw the results of. In a 50-eval session, this is hundreds of tokens of already-processed information.

4. **Overlapping tools** — `get_fsi_status` and `get_startup_info` return largely the same data. Two tools where one would do.

5. **Full diagnostics in eval results** — When diagnostics are embedded in `send_fsharp_code` results, the LLM sees them there AND potentially again via `check_fsharp_code`. Double reporting.

**What the LLM actually needs (minimal viable context):**

| After this action... | The LLM needs to see... | NOT this... |
|---------------------|------------------------|-------------|
| Eval succeeds | Result + type signature only | Echoed code, full event history, session stats |
| Eval fails | Error message + suggestion | Echoed code, previous events, tool tips |
| Status check | What changed since last check | Full static config, tool list, hints |
| Diagnostics check | Only NEW diagnostics | All accumulated diagnostics from entire session |
| Completions | Completion list | Session state, event history |
| Script load | Success/fail + errors only | Full eval trace of each statement |

**The solution is NOT Datastar morphing for LLM responses.** Datastar morph operates on HTML DOM elements — it needs element IDs, tree structure, and a persistent client-side DOM to patch into. LLM context windows are not DOMs. The LLM doesn't maintain state between tool calls — each response is consumed independently.

**The solution is a response strategy layer in SageFs:**

1. **Strip echoed code from MCP responses** — the LLM sent it, it knows. Save ~20-200 tokens per eval.
2. **Omit static boilerplate** — tool lists, usage hints, emoji headers. Move them to `ServerInstructions` (sent once at session start). Save ~80 tokens per status call.
3. **Delta events** — `get_recent_fsi_events` gains an optional `since` cursor parameter. The LLM passes the cursor from the last call, gets only new events. Previous events are not re-sent.
4. **Merge overlapping tools** — combine `get_fsi_status` and `get_startup_info` into one tool. Startup info is just status with a `verbose` flag.
5. **Separate diagnostics from eval results** — eval results don't embed diagnostics. If the LLM wants diagnostics, it calls `check_fsharp_code` explicitly. No double reporting.
6. **Structured MCP responses** — return JSON with typed fields so the LLM can focus on what matters: `{result: "...", typeSignature: "...", hasErrors: false}` instead of formatted text blocks with headers and separators.

---

### Datastar SSE for IDE Plugins (Not LLMs)

Datastar morph IS the right pattern for the Neovim plugin and future VSCode extension — they ARE persistent clients that maintain state. The plugin subscribes to SSE streams and morphs its internal state. But the data isn't HTML — it's structured JSON fragments.

**How `Falco.Datastar` applies:**

The Datastar SSE event protocol uses two event types:
- **`datastar-patch-elements`** — carries HTML fragments to morph into the DOM
- **`datastar-patch-signals`** — carries JSON signal data to patch into the reactive store

For SageFs, the data is F# domain objects (diagnostics, completions, eval results, session state). We use **`datastar-patch-signals`** as the primary mechanism — it patches JSON into the client's reactive store, which is exactly what our Neovim plugin needs:

```
event: datastar-patch-signals
data: signals {"diagnostics": {"MyModule.fs": [{"message": "...", "severity": "warning", "line": 42}]}}

event: datastar-patch-signals
data: signals {"evalResult": {"success": true, "result": "val it: int = 42", "typeSignature": "int"}}

event: datastar-patch-signals
data: signals {"status": {"evalCount": 15, "uptime": "00:23:15", "lastEvalMs": 150}}
```

The Neovim plugin maintains a Lua table that mirrors these signals. On each `datastar-patch-signals` event, it merges the incoming JSON into its local state. The plugin renders from this local state — virtual text, diagnostics, status bar, floating windows all draw from the same materialized view.

**When we DO use `datastar-patch-elements`:** If we ever serve a browser-based UI (health dashboard, rich output rendering, notebook view), Datastar morph operates natively on the HTML fragments. The same SSE endpoint serves both the Neovim plugin (which reads signals) and the browser (which morphs elements). This is the full Datastar experience.

**Neovim plugin SSE parser (~50 lines of Lua):**
```lua
-- Parse Datastar SSE events, merge signals into local state table
local function on_sse_event(event_type, data)
  if event_type == "datastar-patch-signals" then
    local signals = vim.json.decode(data.signals)
    state = vim.tbl_deep_extend("force", state, signals)
    render()  -- re-render virtual text, diagnostics, status from updated state
  end
end
```

**HTTP endpoint map:**

| Method | Path | Type | Description |
|--------|------|------|-------------|
| `POST` | `/exec` | Command | Submit code for evaluation. `202 Accepted`. Results flow via `GET /eval`. |
| `GET` | `/eval` | SSE stream | Subscribe to eval results. Full state on connect, morph fragments on change (eval started, partial stdout, eval complete with result/error). |
| `POST` | `/diagnostics` | Command | Submit code for type-checking (no eval). `202 Accepted`. Results flow via `GET /diagnostics`. |
| `GET` | `/diagnostics` | SSE stream | Subscribe to project-wide diagnostics. Full state on connect, morph fragments on change (new diagnostics, cleared on reset). |
| `POST` | `/completions` | Command | Request completions at cursor position. `202 Accepted`. Results flow via `GET /completions`. |
| `GET` | `/completions` | SSE stream | Subscribe to completion results. Morph fragment pushed after each `POST /completions`. |
| `POST` | `/cancel` | Command | Cancel the current running eval. `202 Accepted`. Cancellation reflected in `GET /eval` stream. |
| `POST` | `/reset` | Command | Soft-reset FSI session. `202 Accepted`. State changes flow via `GET /status`. |
| `POST` | `/hard-reset` | Command | Hard-reset with optional rebuild. `202 Accepted`. State changes flow via `GET /status`. |
| `GET` | `/status` | SSE stream | Subscribe to session status (eval count, uptime, defined types, loaded assemblies, performance stats). Full state on connect, morph fragments on change. |
| `GET` | `/events` | SSE stream | Subscribe to eval event history. Full history on connect, morph fragment for each new event. |
| `GET` | `/health` | Request/response | Simple health check. Exception to CQRS — this is a liveness probe, not a domain resource. Returns `200 OK`. |
| `GET` | `/sse` | MCP transport | Existing MCP SSE transport (unchanged). |
| `POST` | `/message` | MCP transport | Existing MCP message endpoint (unchanged). |

**Why this matters:** Two separate consumer patterns, one server:
- **LLM (via MCP tools)** — gets minimal, focused, non-redundant responses. No echoed code, no boilerplate, delta events only. The response strategy layer ensures the LLM's context window stays lean.
- **Neovim plugin (via SSE streams)** — subscribes to `GET /diagnostics`, `GET /eval`, `GET /status` once at startup. Receives `datastar-patch-signals` events, merges them into local Lua state. Renders from materialized view. Whether the change came from the plugin, an MCP agent, or the terminal REPL, all subscribers see the same state.

The server is the single source of truth. Commands come in through any door (MCP tool call, HTTP POST, terminal REPL input). State changes flow out through SSE to all subscribers. The LLM gets a curated summary via MCP; the plugin gets the full reactive stream via Datastar.

---

### Wire Efficiency: BARE as a Future Optimization (LARP-Aligned)

[BARE (Binary Application Record Encoding)](https://baremessages.org/) is an IETF Internet-Draft for simple binary encoding. Its type system maps naturally to F# algebraic types (DUs → unions, records → structs, options → optionals). BARE is a candidate binary encoding for machine-to-machine paths (SSE → Neovim plugin, HTTP commands from IDE plugins) if JSON SSE performance becomes a measured bottleneck.

**Key fit for SageFs:** F# domain types *are* the BARE schema — no separate `.bare` schema file needed. Status updates shrink ~89% (tool names as 1-byte enum tags vs 15-25 byte strings). Eval results shrink ~64%. BARE doesn't help the LLM/MCP path (LLMs need readable text).

**Implementation notes:** `BareNET`/`BareFs` on NuGet for F# encoding; ~100-line Lua decoder for Neovim plugin. Protocol negotiation via `Accept: application/bare` header — JSON remains the default, zero breaking changes. See [BARE spec](https://datatracker.ietf.org/doc/draft-devault-bare/) and [LARP protocol](https://speakez.tech/blog/larp-protocol/) for details.

**Priority: P2** — implement after Neovim plugin SSE integration works with JSON. Only optimize when there's a measured performance problem.

---

### Event Sourcing: Marten + PostgreSQL as the Backbone

**Status:**
- ✅ **Domain event DUs** — `SageFsEvent`, `EventSource`, `DiagnosticEvent`, `EventMetadata` in `SageFs\Features\Events.fs`
- ✅ **Marten integration tests** — 4 Testcontainers-based tests (append, multi-event, DU roundtrip, DiagnosticsChecked roundtrip)
- ✅ **EventStore module** — `configureStore`, `appendEvents`, `fetchStream`, `tryCreateFromEnv` in `SageFs.Server\EventStore.fs`. 5 tests.
- ✅ **Actor event emission** — `onEvent` callback wired into `mkAppStateActor`. Emits SessionStarted, SessionReady, EvalRequested, EvalCompleted/EvalFailed, DiagnosticsChecked, SessionReset, SessionHardReset. 5 tests.
- ✅ **CLI integration** — `CliEventLoop` creates optional Marten store from `SageFs_CONNECTION_STRING` env var
- ✅ **compose.yml** — Docker Compose for local PostgreSQL (postgres:17)
- ❌ **Session replay** — `--session <id>` not yet implemented
- ❌ **Projections** — SessionProjection, DiagnosticsProjection not yet implemented
- ❌ **SSE subscription daemon** — Marten async daemon for pushing events to SSE clients not yet wired

SageFs's current state management is ad-hoc: a mutable `EventTracker` (append-only list of tuples), a `MailboxProcessor` holding `AppState` in its loop closure, and no persistence. This works for a single-process REPL but breaks the moment you want multi-process, multi-agent, or session replay.

**The decision:** Use Marten as a full event store backed by PostgreSQL. Every meaningful action in SageFs becomes a persisted event. The actor's in-memory state becomes a projection over the event stream. SSE subscriptions become event stream consumers. Session replay becomes "start Marten, replay the stream."

#### Why Full Marten/Postgres (Not Just In-Memory)

The easy path would be an in-memory event log with optional file persistence. But SageFs aspires to be multi-process, multi-agent, multi-actor:

- **Multi-process** — Two SageFs instances (different projects, different ports) sharing one Postgres see each other's events. An orchestration agent can monitor both. `docker compose up -d` starts Postgres, run N SageFs instances against it.
- **Multi-agent** — Claude, Copilot, and a human all connected to the same SageFs session. Marten's event stream records who did what (`EventSource` metadata). Any subscriber sees the unified history.
- **Multi-actor** — The eval actor and query actor (from the actor split) both append/read from the same event stream. No in-memory coordination needed — Marten handles concurrency.
- **Session replay** — Restart SageFs, tell it a session ID. Marten replays the event stream. SageFs re-evaluates each `EvalCompleted` event's code to rebuild FSI state. True session persistence without serializing the CLR runtime.
- **Cross-session queries** — "Show me all evals across all sessions that produced errors" — a Marten LINQ query. "Which agent defined this value?" — event stream filter. This is impossible with in-memory state.
- **Lightweight infrastructure** — `docker compose up -d` for local dev, Testcontainers for tests. Connection string via `SageFs_CONNECTION_STRING`. No AppHost, no orchestrator dependency.

#### Domain Events (F# Discriminated Unions)

Every event is an immutable fact about what happened. Events are the source of truth — projections are derived views.

```fsharp
/// Identifies who caused the event
type EventSource =
  | Console
  | McpAgent of agentName: string
  | FileSync of fileName: string
  | System

/// All events that can occur in an SageFs session
type SageFsEvent =
  // Session lifecycle
  | SessionStarted of {| Config: StartupConfig; StartedAt: DateTimeOffset |}
  | SessionWarmUpStarted of {| Projects: string list |}
  | SessionWarmUpCompleted of {| Duration: TimeSpan; Errors: string list |}
  | SessionReady
  | SessionFaulted of {| Error: string; StackTrace: string option |}
  | SessionReset
  | SessionHardReset of {| Rebuild: bool |}

  // Evaluation
  | EvalRequested of {| Code: string; Source: EventSource; Background: bool |}
  | EvalCompleted of {| Code: string; Result: string; TypeSignature: string option; Duration: TimeSpan |}
  | EvalFailed of {| Code: string; Error: string; Diagnostics: DiagnosticEvent list |}
  | EvalCancelled of {| Code: string; Source: EventSource |}

  // Diagnostics
  | DiagnosticsChecked of {| Code: string; Diagnostics: DiagnosticEvent list; Source: EventSource |}
  | DiagnosticsCleared

  // Completions
  | CompletionsRequested of {| Code: string; CursorPosition: int; Source: EventSource |}
  | CompletionsReturned of {| Items: CompletionItemEvent list |}

  // Script loading
  | ScriptLoaded of {| FilePath: string; StatementCount: int; Source: EventSource |}
  | ScriptLoadFailed of {| FilePath: string; Error: string |}

  // State transitions (affordance model)
  | StateTransitioned of {| From: SessionState; To: SessionState; Reason: string |}

  // Configuration
  | McpPortUpdated of {| Port: int |}
  | MiddlewareAdded of {| Count: int |}

  // Environment changes (epistemic validity tracking)
  | AssembliesChanged of {| OldFingerprint: EnvironmentFingerprint; NewFingerprint: EnvironmentFingerprint; ChangedAssemblies: string list |}

and EnvironmentFingerprint = {
  AssemblyHash: string                    // SHA256 of all loaded assembly bytes
  AssemblyVersions: Map<string, string>   // assembly name → content hash
  ShadowDir: string option
}

and DiagnosticEvent = {
  Message: string
  Severity: string  // "error" | "warning" | "info" | "hidden"
  StartLine: int
  StartColumn: int
  EndLine: int
  EndColumn: int
}

and CompletionItemEvent = {
  Label: string
  Kind: string
  Detail: string option
}
```

**Every event carries metadata** via Marten's built-in event metadata:
- `StreamId` — the session ID (UUID)
- `Sequence` — monotonically increasing within stream (the "cursor" for delta queries)
- `Timestamp` — when it happened
- Custom metadata: `EventSource`, `CorrelationId` (links request→response events), `EnvironmentFingerprint option` (dependency snapshot for epistemic validity — initially `None`, populated when assembly change tracking is wired up)

#### Projections (Read Models)

Projections are materialized views derived from the event stream. Marten supports inline (synchronous, guaranteed consistent) and async (eventually consistent, background) projections.

**Inline projections** (consistent — updated in same transaction as event append):

```fsharp
/// Current session state — derived from events, not stored directly
type SessionProjection = {
  Id: Guid                           // = stream ID
  State: SessionState                // fold over StateTransitioned events
  EvalCount: int                     // count of EvalCompleted events
  LastEvalCode: string option        // most recent EvalCompleted.Code
  LastEvalDuration: TimeSpan option  // most recent EvalCompleted.Duration
  StartedAt: DateTimeOffset          // from SessionStarted
  AvailableTools: string list        // derived from State
  LoadedProjects: string list        // from SessionWarmUpCompleted
  TotalErrors: int                   // count of EvalFailed events
  StaleEventCount: int               // events invalidated since last AssembliesChanged
}

/// All diagnostics for the current session
type DiagnosticsProjection = {
  Id: Guid
  Diagnostics: Map<string, DiagnosticEvent list>  // keyed by code hash or file path
  LastUpdated: DateTimeOffset
}
```

**Async projections** (background, for cross-session analytics):

```fsharp
/// Agent activity across all sessions
type AgentActivityProjection = {
  AgentName: string
  TotalEvals: int
  TotalErrors: int
  SessionIds: Guid list
  LastActiveAt: DateTimeOffset
}
```

#### How the Actor Changes

Currently, the `MailboxProcessor` holds `AppState` in a recursive `loop st` closure. With Marten:

1. **Commands come in** through the MailboxProcessor (unchanged interface)
2. **Events are appended** to Marten's event stream instead of mutating in-memory state
3. **State is read** from Marten projections (or a cached in-memory projection for hot-path reads)
4. **SSE subscribers** use Marten's async daemon subscription — when new events appear, they're pushed to connected clients

```
                        ┌─────────────────────────────────┐
  MCP Tool call ──────► │         Eval Actor               │
  POST /eval ─────────► │  (serializes FSI mutations)      │
  Console input ──────► │                                  │
                        │  1. Execute FSI eval             │
                        │  2. Append EvalCompleted event   │──► Marten Event Stream
                        │     to Marten                    │         │
                        └─────────────────────────────────┘         │
                                                                     │
                        ┌─────────────────────────────────┐         │
  MCP get_status ─────► │         Query Actor              │◄────────┘
  GET /status SSE ────► │  (reads from projections)        │    (inline projection
  GET /diagnostics ───► │                                  │     updates synchronously)
                        │  Reads SessionProjection,        │
                        │  DiagnosticsProjection           │
                        └─────────────────────────────────┘

                        ┌─────────────────────────────────┐
                        │      SSE Subscription Daemon     │◄──── Marten async daemon
                        │  (pushes events to clients)      │      subscribes to stream
                        │                                  │
                        │  → Neovim plugin (Datastar)      │
                        │  → Browser UI (Datastar)         │
                        │  → Other SageFs instances          │
                        └─────────────────────────────────┘
```

#### Session Replay

The killer feature. When SageFs starts with `--session <id>`:

1. Marten loads the event stream for that session ID
2. SageFs reads `EvalCompleted` events in order
3. Each eval's `Code` is re-submitted to FSI (the same code, in the same order)
4. FSI rebuilds its internal state (bound values, open namespaces, loaded assemblies)
5. The session is restored to exactly where it left off

This is the ONLY reliable way to persist a REPL session. You can't serialize the CLR runtime, but you CAN replay the transcript.

**Smart replay optimization:** Skip evals whose results didn't change the FSI state (e.g., `42 + 1;;` returns a value but doesn't bind anything). Only replay bindings (`let x = ...`), type definitions, `#r` references, and `open` statements. The event metadata can tag "state-mutating" vs "read-only" evals.

#### Delta Events for LLM Context (Replaces Cursor Design)

The plan's section 1.0 item 3 (delta events with cursor) becomes trivial with Marten:

```fsharp
// LLM calls: get_recent_fsi_events(since_sequence: 47)
// Marten query:
let! events =
  session.Events.FetchStream(sessionId)
  |> Task.map (fun stream ->
    stream
    |> Seq.filter (fun e -> e.Sequence > 47L)
    |> Seq.map (fun e -> e.Data :?> SageFsEvent))
```

The `Sequence` is Marten's built-in monotonic event counter. No custom cursor needed.

#### Multi-Process Communication

Two SageFs instances on different ports, same Postgres:

```
SageFs:37749 (Project A)  ──► Postgres ◄── SageFs:37750 (Project B)
     │                        │                    │
     │  SessionStarted        │  SessionStarted    │
     │  EvalCompleted         │  EvalCompleted     │
     │  ...                   │  ...               │
     │                        │                    │
     └────── Orchestration Agent subscribes to both streams ──────┘
```

An orchestration agent (or the Neovim plugin showing multiple sessions) subscribes to events from both streams. "Project A's tests passed, now load its output into Project B" — a workflow that requires cross-process visibility.

#### Local Dev & Test Infrastructure (No Aspire)

SageFs is a CLI tool, not a multi-service web app. Aspire's orchestration model adds ceremony without value here. Instead:

**Local development:** SageFs auto-starts Postgres via Testcontainers on first launch (see "Operational Concerns" below). No manual steps needed — just `dotnet run`. A `compose.yml` at repo root documents the container configuration and serves as an escape hatch for manual control:

```yaml
services:
  sagefs-db:
    image: postgres:17
    ports:
      - "5432:5432"
    environment:
      POSTGRES_DB: SageFs
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: SageFs
    volumes:
      - sagefs-pgdata:/var/lib/postgresql/data
volumes:
  sagefs-pgdata:
```

Inner dev loop: `dotnet run --project SageFs.Server -- --project path/to/thing`. Postgres starts automatically on first run. Connection string auto-detected from the Testcontainers-managed container. Override with `SageFs_CONNECTION_STRING` environment variable for explicit Postgres.

**Tests: Testcontainers with container reuse and schema isolation** (inspired by [Harmony](../Harmony)):

```fsharp
/// One Postgres container, reused across all test runs.
/// Schema isolation gives each test category its own namespace.
let getPostgresContainer () =
  PostgreSqlBuilder()
    .WithDatabase("SageFs")
    .WithUsername("postgres")
    .WithPassword("SageFs")
    .WithImage("postgres:17")
    .WithReuse(true)
    .WithWaitStrategy(
      Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable 5432)
    .Build()

/// Lazy shared container — starts once per test process
let sharedContainer = lazy (
  let container = getPostgresContainer ()
  container.StartAsync().GetAwaiter().GetResult()
  container
)

/// Per-category Marten stores with schema isolation
let createStoreForSchema schemaName =
  let container = sharedContainer.Value
  DocumentStore.For(fun (o: StoreOptions) ->
    o.Connection(container.GetConnectionString())
    o.DatabaseSchemaName <- schemaName
    o.AutoCreateSchemaObjects <- AutoCreate.All
    eventStoreConfig o  // shared projection/serializer config
  ) :> IDocumentStore

let evalStore = lazy (createStoreForSchema "test_eval")
let diagnosticsStore = lazy (createStoreForSchema "test_diagnostics")
let projectionStore = lazy (createStoreForSchema "test_projections")
```

**Why this pattern works:**
- `.WithReuse(true)` — container survives across `dotnet run` invocations. First run: ~2s startup. Subsequent runs: instant (container already running).
- Schema isolation — `test_eval`, `test_diagnostics`, `test_projections` schemas are independent. Tests don't interfere. `store.Advanced.ResetAllData()` cleans per-test without affecting other schemas.
- No AppHost project, no Aspire dependencies, no dashboard. Just Postgres.
- CI: Same Testcontainers approach (GitHub Actions has Docker). Or add a `services: postgres:17` block in the workflow.

**Connection string convention:** `SageFs_CONNECTION_STRING` environment variable. Tests ignore it (they use Testcontainers). Dev/prod sets it.

#### Data Persistence

| Context | Where data lives | Lifecycle |
|---------|-----------------|-----------|
| **Local dev** | Docker named volume `sagefs-pgdata` | Survives `docker compose down`/`up`. Persists across SageFs restarts. Inspectable via `docker volume inspect sagefs-pgdata` (shows mount point). `docker volume rm sagefs-pgdata` to wipe. |
| **Tests** | Testcontainers ephemeral storage | Wiped per test via `store.Advanced.ResetAllData()`. Container itself reused for speed (`.WithReuse(true)`) but data is not preserved between test runs. |
| **Production/deployed** | Real Postgres instance | Standard database lifecycle — backups, migrations, retention policies. |

Session replay (`--session <id>`) works because events survive in the named volume across SageFs process restarts. `docker compose up -d` → run SageFs → stop SageFs → run SageFs with `--session <previous-id>` → Marten replays the event stream → FSI state rebuilt.

To start fresh: `docker compose down && docker volume rm sagefs-pgdata && docker compose up -d`.

#### Backup & Export

Two complementary mechanisms — database-level and application-level:

**1. `pg_dump` — full database backup**

```bash
# Manual backup to ~/.SageFs/backups/
docker exec sagefs-db pg_dump -U postgres SageFs > ~/.SageFs/backups/SageFs-$(date +%Y%m%d).sql

# Restore into any Postgres
psql -U postgres -d SageFs < ~/.SageFs/backups/sagefs-20260212.sql
```

Full-fidelity: captures all events, projections, schemas. Restorable to any Postgres instance. Could be wired as a CLI command (`SageFs backup`) or triggered on graceful shutdown.

**2. JSON event stream export — per-session portability**

```fsharp
/// Export a session's event stream as .jsonl (one event per line)
let exportSession (store: IDocumentStore) (sessionId: Guid) (path: string) =
  task {
    use session = store.LightweightSession()
    let! events = session.Events.FetchStreamAsync(sessionId)
    use writer = System.IO.File.CreateText(path)
    for event in events do
      let json = System.Text.Json.JsonSerializer.Serialize(event.Data)
      do! writer.WriteLineAsync(json)
  }

/// Import events from .jsonl back into a Marten store
let importSession (store: IDocumentStore) (path: string) (newSessionId: Guid) =
  task {
    use session = store.LightweightSession()
    let lines = System.IO.File.ReadAllLines(path)
    for line in lines do
      let event = deserializeEvent line  // DU-aware deserialization
      session.Events.Append(newSessionId, event) |> ignore
    do! session.SaveChangesAsync()
  }
```

- Portable: `.jsonl` files are human-readable, version-controllable, don't need Postgres to inspect
- Granular: export/import individual sessions, not the whole database
- Shareable: send a session to a colleague — `SageFs import session-2026-02-12.jsonl`
- MCP tool candidates: `export_session`, `import_session`

**Storage location:** `~/.SageFs/backups/` for `pg_dump`, `~/.SageFs/exports/` for JSON session exports. Both directories created on first use.

#### Operational Concerns

**1. First-run experience — zero-setup Postgres via Testcontainers**

SageFs auto-starts its own Postgres container on launch. No manual `docker compose up -d`, no README steps to forget. The same Testcontainers library used for tests handles runtime infrastructure:

```fsharp
/// Auto-start Postgres if no explicit connection string is provided.
/// .WithReuse(true) means the container survives SageFs process exit
/// and is reused on next launch (instant restart, no 2s startup).
let getOrStartPostgres () =
  match Environment.GetEnvironmentVariable("SageFs_CONNECTION_STRING") with
  | null | "" ->
    let container =
      PostgreSqlBuilder()
        .WithDatabase("SageFs")
        .WithUsername("postgres")
        .WithPassword("SageFs")
        .WithImage("postgres:17")
        .WithReuse(true)
        .WithVolumeMount("sagefs-pgdata", "/var/lib/postgresql/data")
        .WithWaitStrategy(
          Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable 5432)
        .Build()
    container.StartAsync().GetAwaiter().GetResult()
    printfn "✓ Event store: PostgreSQL (auto-started via Docker)"
    container.GetConnectionString()
  | connectionString ->
    printfn "✓ Event store: PostgreSQL (explicit connection)"
    connectionString
```

- **First run:** ~2s to pull/start the container. One-time cost.
- **Subsequent runs:** Container is already running. Testcontainers detects it via `.WithReuse(true)`, returns instantly.
- **Data persists** in the Docker named volume `sagefs-pgdata` across SageFs restarts and container restarts.
- **Override:** Set `SageFs_CONNECTION_STRING` for explicit Postgres (CI, prod, shared instance, remote DB).
- **No Docker installed:** Fail fast with a clear error. Docker is a prerequisite for SageFs — same as .NET SDK.
- **Storage backend note:** The event sourcing *domain model* (events, projections, replay) is naturally decoupled from Marten — pure domain functions (`availableTools`, `assessValidity`, event fold projections) never import Marten; they operate on F# DUs. The `EventStore` module is the adapter. A future SQLite backend is *possible* if Docker-free scenarios become important, but it's not a near-term priority — the current Marten+Testcontainers architecture auto-starts Postgres transparently and serves the actual user base (developers with Docker).
- **Escape hatch:** The `compose.yml` in the repo documents what SageFs auto-starts, and can be used manually by people who prefer explicit control.
- **No architectural lock-in:** Testcontainers creates a normal Docker container. `docker ps`, `docker stop`, `docker volume inspect` all work. Ripping out the auto-start is 5 lines — just require `SageFs_CONNECTION_STRING` instead.

**2. Schema migrations — event versioning**

Marten stores events as JSON in Postgres. When the `SageFsEvent` DU evolves:

- **Additive changes are safe** — adding a new DU case (e.g., `| DebuggerAttached of ...`) just means old streams don't contain that case. Deserialization works fine.
- **Field additions with defaults** — adding an optional field to an existing case (e.g., `Environment: EnvironmentFingerprint option` on `EvalCompleted`) works because `None` is the JSON-absent default.
- **Renames and removals are breaking** — renaming `EvalCompleted` to `EvalFinished` would orphan existing events. **Rule: never rename or remove DU cases. Deprecate by convention** (add `[<Obsolete>]`, stop emitting, handle in projection folds).
- **Marten's `AutoCreateSchemaObjects <- AutoCreate.All`** handles table/index creation automatically in dev. For production, use Marten's `ApplyAllConfiguredChangesToDatabaseAsync()` at startup with a version check.
- **Event upcasting** — if a structural change is unavoidable, Marten supports event transformations (upcasters) that convert old event shapes to new ones during deserialization. Design these as pure functions: `upcastV1toV2 : EvalCompletedV1 -> EvalCompletedV2`.

**3. Graceful degradation — Postgres outage mid-session**

If Postgres goes down during a running session:

- **FSI eval succeeds but event append fails** — the eval already happened in the CLR. The result is real. The design:
  - Log the event append failure as a warning
  - Return the eval result to the caller normally (MCP response, console echo)
  - Buffer the failed event in-memory for retry
  - On next successful Postgres connection, flush buffered events (with original timestamps)
  - If the buffer exceeds a threshold (e.g., 100 events), warn: `"⚠ Event store unavailable. 100 events buffered. Results are live but not persisted."`
- **Projections become stale** — the query actor reads from projections, which aren't updated if events can't append. SSE subscribers see stale state. This is acceptable — eventually consistent is the contract.
- **Session replay won't include lost events** — if Postgres never comes back and the process dies, buffered events are lost. This is the trade-off for not making Postgres a hard dependency on the eval path.

**4. Event retention — unbounded growth**

Events accumulate forever by design (event sourcing principle: events are immutable facts). But practical limits exist:

- **Short-term:** Not a problem. A power user doing 100 evals/day for a year = ~36,500 events. At ~1KB/event, that's ~36MB. Postgres handles this trivially.
- **Medium-term:** Add a `SageFs prune --older-than 90d` CLI command that archives old sessions to `.jsonl` exports (backup story above) and then deletes them from Postgres. This is explicit, not automatic.
- **Long-term:** Marten supports event archiving natively — move old streams to a separate archive table. Projections are unaffected (they're derived views, not source data).
- **Explicit non-goal:** No automatic TTL or auto-deletion. Event history is valuable. Let the user decide when to prune.

**5. Concurrent SSE clients — backpressure**

Multiple Neovim instances, browser tabs, and MCP connections can all subscribe to the same SSE streams simultaneously.

- **ASP.NET Core SSE is per-connection** — each `GET /eval` subscription is an independent HTTP response stream. The server writes to each independently. A slow client doesn't block others.
- **Backpressure:** If a client's TCP send buffer fills (client not reading), `HttpResponse.Body.WriteAsync` will eventually block or throw. ASP.NET Core's Kestrel handles this with per-connection write timeouts. A disconnected client triggers `HttpContext.RequestAborted` cancellation.
- **Practical limit:** Dozens of concurrent SSE connections are fine. Hundreds would need investigation (memory per connection, event fan-out cost). SageFs is a dev tool — dozens is the realistic ceiling.
- **Event fan-out:** The Marten async daemon subscription is a single reader. It fans out to all SSE connections via an in-memory broadcast channel (`System.Threading.Channels`). One Postgres subscription, N SSE writers.

**6. Security — credentials and access**

- **Local dev:** `compose.yml` credentials (`POSTGRES_PASSWORD: SageFs`) are intentionally simple. This is localhost-only, dev-only. The compose file is in the repo — no secrets.
- **Production:** Connection string via `SageFs_CONNECTION_STRING` environment variable. Standard secret management applies (Docker secrets, Kubernetes secrets, vault, etc.). SageFs doesn't manage secrets — it just reads the connection string.
- **MCP transport:** The MCP SSE endpoint (`/sse`, `/message`) is currently unauthenticated on `localhost:37749`. This is fine for local dev (same machine). For remote access, add a reverse proxy with auth. SageFs itself doesn't need auth — it's a local tool.
- **SSE endpoints:** Same as MCP — localhost-only by default. The `UseUrls("http://localhost:{port}")` binding ensures no external access unless explicitly reconfigured.

#### Dependencies

- **Marten 8.21.0** — latest stable, targets net10.0, includes async daemon subscriptions, inline projections, FSharp.Core dependency (F#-friendly)
- **Npgsql** — pulled in transitively by Marten
- **Testcontainers.PostgreSql** — used for both runtime auto-start (SageFs.Server) and test infrastructure (SageFs.Tests). Container reuse across runs, schema isolation for tests.
- Marten already depends on `FSharp.Core ≥ 9.0.100` — it's F#-aware

#### What Replaces What

| Current | Replaced By |
|---------|-------------|
| `EventTracker` (mutable list in Mcp.fs) | Marten event stream append |
| `AppState` in MailboxProcessor loop | Inline projection from event stream |
| `get_recent_fsi_events` polling | Marten stream query with sequence filter |
| Ring buffer (plan item 1.4) | N/A — Postgres stores all events, query with LIMIT |
| Session persistence (plan item 3.3) | Session replay from event stream |
| Custom SSE push logic | Marten async daemon subscription → SSE |
| No assembly change tracking | `AssembliesChanged` event + `EnvironmentFingerprint` metadata for epistemic validity |

#### Priority

**P0 (infrastructure)** — This is foundational. Most other features benefit from or depend on event sourcing:
- Diagnostics SSE (1.3) → events drive the SSE stream
- Actor split (2.0) → both actors read/write the same event stream
- Affordance state machine (1.0b) → `StateTransitioned` events, projections
- Delta events for LLMs (1.0) → Marten sequence numbers
- Session persistence (3.3) → replay from event stream (now trivial, subsumed)
- Multi-session (3.2) → different stream IDs in same database
- Epistemic validity (3.7) → `AssembliesChanged` events + fingerprint metadata (P2, but schema designed into Phase 0)

However, it's a significant infrastructure change. It should be implemented early but alongside the first feature (diagnostics MCP tool) — the diagnostics tool can be the first event-sourced feature, proving the pattern before applying it everywhere.

#### Assessed and Deferred: Speculative FSI Branching / Epistemic Validity

**The idea:** SageFs could spin up multiple underlying FSI instances from the same event stream, diverging like Git branches or video-game netcode prediction — exploring "possible worlds" that collapse when the correct path becomes clear.

**Assessment: The speculative branching model is premature, but the underlying concern — epistemic validity of events when dependencies change — is real and needs addressing.**

##### The Real Problem: Assembly Drift Invalidates Event History

When a DLL that SageFs loaded gets rebuilt (e.g., `dotnet build` in the user's project), every `EvalCompleted` event that referenced types/functions from that assembly is now *epistemically stale*. The eval's recorded `Result` and `TypeSignature` were true *at the time*, under *that version* of the assembly. After rebuild:

- Type signatures may have changed (a field added, a return type narrowed)
- Method behavior may differ (the function returns different values for the same inputs)
- Types may have been removed entirely
- New types/modules may exist that weren't available before

This is the dependency tree problem: events don't exist in isolation, they exist relative to an environment. The event `EvalCompleted { Code = "MyModule.calculate 42"; Result = "84" }` is only meaningful if `MyModule.calculate` still means what it meant when the event was recorded.

##### What SageFs Already Has (and What It Lacks)

SageFs already has mechanisms that touch this:

1. **Shadow copies** (`ShadowCopy.fs`) — snapshots DLLs at startup so the build system can't yank them mid-session. But there's no record of *which* snapshot version was active.
2. **Hot reload middleware** (`HotReloading.fs`) — detects new assemblies from FSI eval, uses Harmony to patch method bodies in-place. But this is method-level patching of the *live* runtime, not event-level validity tracking.
3. **Hard reset** (`HardResetSession`) — nukes everything and re-shadow-copies. The nuclear option. No selective invalidation.

What's missing: **no event carries a fingerprint of the environment it was valid in.** There's no way to answer "which of my 200 events are still trustworthy after this rebuild?"

##### The Dependency Fingerprint Model

Instead of speculative branching, the right primitive is an **environment fingerprint** on each event — a lightweight content hash of the dependency tree at the time of evaluation. The `EnvironmentFingerprint` type is defined in the domain events section above alongside `AssembliesChanged`.

Additional types for validity assessment:

```fsharp
/// Extended event metadata (adds to Marten's built-in StreamId, Sequence, Timestamp)
type EventMetadata = {
  Source: EventSource
  CorrelationId: Guid option
  Environment: EnvironmentFingerprint option  // None until assembly tracking is wired up
}
```

Now every event knows *which world it was valid in*. When assemblies change:

```fsharp
/// Given current environment and an event's environment, determine validity
type EpistemicStatus =
  | Valid           // environment unchanged for this event's dependencies
  | Stale           // dependencies changed, result may differ
  | Invalid         // dependencies removed or incompatible
  | Unknown         // can't determine (e.g., expression uses reflection)

let assessValidity
  (current: EnvironmentFingerprint)
  (eventEnv: EnvironmentFingerprint)
  (referencedAssemblies: string list)  // which assemblies this eval actually touched
  : EpistemicStatus =
  let changedAssemblies =
    referencedAssemblies
    |> List.filter (fun asm ->
      match Map.tryFind asm current.AssemblyVersions, Map.tryFind asm eventEnv.AssemblyVersions with
      | Some curr, Some orig -> curr <> orig
      | Some _, None -> true   // assembly didn't exist when event was recorded
      | None, Some _ -> true   // assembly was removed
      | None, None -> false)   // neither environment has it
  match changedAssemblies with
  | [] -> Valid
  | _ -> Stale
```

This is the **immutable data structure** insight you're reaching for: events are immutable facts, but their *interpretation* depends on the environment. The event itself never changes — but its epistemic status is a *derived property* that changes when the environment changes. This is exactly how persistent/immutable data structures work: the structure doesn't change, but the *view* over it does.

##### How This Connects to Replay

Session replay (the `--session <id>` feature) becomes more sophisticated:

1. Load event stream
2. Compute current `EnvironmentFingerprint`
3. Walk events, comparing each event's fingerprint to current
4. Events marked `Valid` → can skip re-evaluation (result is still trustworthy)
5. Events marked `Stale` → must re-evaluate (dependency changed, result may differ)
6. Events marked `Invalid` → flag to user/agent ("this eval referenced `OldModule` which no longer exists")

This is **selective replay** — not replaying everything, and not naively trusting everything. The fingerprint tells you exactly where the "tree diverges."

##### Why NOT Speculative Branching

The multi-FSI-instance branching idea was solving this same problem but with enormous overhead:

| Approach | Memory | Complexity | Granularity |
|----------|--------|------------|-------------|
| Speculative N FSI instances | N × 100-200MB | Very high (merge semantics undefined) | Coarse (whole sessions) |
| Environment fingerprints | ~1KB per event | Low (pure function over hashes) | Fine (per-event, per-assembly) |

Speculative branching also has the fundamental merge problem: two divergent CLR runtimes cannot be reconciled. But fingerprints don't need merging — they're read-only annotations that inform *which events to re-evaluate*.

##### When Fingerprinting Triggers

- **`AssembliesChanged` event** — new domain event, fired when hot reload middleware detects a rebuild or when `HardResetSession` re-shadow-copies. Carries old and new `EnvironmentFingerprint`.
- **Projection update** — `SessionProjection` gains a `StaleEventCount: int` field. Agents see: "47 events are stale since last assembly change."
- **SSE notification** — subscribers get `datastar-patch-signals` with `{ staleEvents: 47, changedAssemblies: ["MyProject"] }`.
- **Smart replay** — `get_recent_fsi_events` can optionally annotate each event with its `EpistemicStatus` relative to current environment.

##### Practical Cost

Computing `EnvironmentFingerprint` is cheap:
- SHA256 of each loaded DLL is ~1ms per assembly (cached after first computation, invalidated by file watcher)
- Store in Marten event metadata — adds ~200 bytes per event
- `assessValidity` is a pure function over two Maps — microseconds

##### Priority

**P2 (after Marten is working)** — this needs the event store to exist first. The fingerprint is event metadata, so it's a natural extension of the domain events. It should be designed into the event schema from the start (even if the `assessValidity` logic comes later), so add `EnvironmentFingerprint` to the event metadata type when defining domain events in Phase 0.

**Phase 0 action item:** Include `EnvironmentFingerprint` as an optional field in event metadata. Initially always `None`. Wire it up when assembly change detection is implemented.

---

### What upstream has that we DON'T

| Feature | Upstream | This Fork |
|---------|----------|-----------|
| **SageFs.Daemon** (JSON-RPC server) | TCP socket daemon via StreamJsonRpc; 3 methods: `eval`, `autocomplete`, `diagnostics` | MCP/HTTP but no JSON-RPC daemon |
| **VSCode Extension** (sagefs-vscode) | Published on Open VSX. Notebook + REPL modes, inline diagnostics, autocomplete, hot reload toggle per cell, `.SageFsb` file format | Only a settings.json patcher script |
| **`GetDiagnostics` Command** | Actor handles `GetDiagnostics(text, reply)` — type-checks without evaluating | Diagnostics only captured as side effect of eval |
| **Notebook format** (`.SageFsb`) | Custom notebook serializer, cell-level execution, saveable outputs, metadata per cell | None |
| **Cancellation via `thread.Interrupt()`** | `token.Register(fun () -> thread.Interrupt())` interrupts stuck FSI evals | We pass `CancellationToken.None` everywhere |
| **stdout/stderr capture middleware** | `captureStdioMiddleware` redirects Console.Out/Error per eval, returns in `metadata.stdout`/`metadata.stderr` | TextWriterRecorder captures FSI output but misses arbitrary `printfn` |
| **Separate daemon config** (`daemon.fsx`) | Separate config for daemon mode | Single config file |
| **`--daemon port addr` flag** | Embeds daemon mode in CLI | Not available |
| **"Send to REPL" / "Send to Notebook"** | `Ctrl+\` sends selection from any `.fs` file to SageFs REPL | Not available |
| **Hot reload per-cell toggle** | Each notebook cell can eval with or without hot reload via cell metadata | Hot reload is global |

### What THIS FORK has that upstream DOESN'T

| Feature | This Fork | Upstream |
|---------|-----------|----------|
| **MCP server + 8 AI tools** | SSE/HTTP MCP protocol, ServerInstructions, structured code-first responses | No AI agent integration |
| **Shadow-copy assemblies** | DLL lock prevention for loaded project assemblies | Assemblies locked during use |
| **Iterative warm-up with retry** | `openWithRetry` resolves cross-dependencies across rounds | No warm-up namespace resolution |
| **RequireQualifiedAccess tolerance** | `isBenignOpenError` skips gracefully | Would fail on these modules |
| **Console echo for MCP submissions** | All agent code visible in terminal with preserved indentation | N/A (no MCP) |
| **Code-first response format** | Always shows submitted code before result | N/A |
| **Snapshot-tested output formats** | Verify snapshots lock in formatting | No snapshot tests |
| **Aspire detection + DCP path config** | Auto-detects Aspire projects | Not available |
| **`hard_reset_fsi_session`** | Full reset with optional rebuild | Not available |
| **Event tracking** | Timestamped eval history | Not available |
| **OpenTelemetry integration** | OTLP export, structured logging | Not available |

### Key Learnings from Upstream

1. **The Daemon architecture is the right foundation for IDE integration.** The JSON-RPC daemon provides typed eval/autocomplete/diagnostics methods that the VSCode extension calls directly. Our MCP protocol is better for AI agents but the VSCode extension needs typed data — we need BOTH protocols or a typed MCP layer.

2. **`GetDiagnostics` is trivially adoptable.** The function already exists in our `Features/Diagnostics.fs` (`getDiagnostics`). We just need a Command variant in the actor and an MCP tool wrapper.

3. **Cancellation is the exact `thread.Interrupt()` pattern.** Upstream registers a callback on the CancellationToken that interrupts the FSI eval thread. This is the P0 fix for our stuck-eval problem.

4. **The notebook format is lightweight and compelling.** `.SageFsb` is just JSON with cells, metadata, and saved outputs. Cell 0 is the init cell (CLI args for the daemon). This is far simpler than Polyglot Notebooks.

5. **stdout/stderr capture per-eval is cleaner upstream.** The daemon's `captureStdioMiddleware` redirects Console.SetOut/SetError per evaluation, putting captured text in response metadata. Our TextWriterRecorder captures FSI output but doesn't capture `printfn`/`Console.WriteLine` from user code.

### Strategy: Fork the Extension, Port the Daemon Protocol

- **Contribute our unique features upstream** (shadow-copy, warm-up, RequireQualifiedAccess) via PRs
- **Fork `sagefs-vscode`** and adapt it to work with OUR enhanced daemon (same JSON-RPC protocol but with our robustness features)
- **Add MCP as a parallel protocol** — same SageFs session serves both MCP (AI agents) AND JSON-RPC (IDE) simultaneously
- **This dual-protocol approach is the ultimate differentiator** — no other F# tool lets you have an AI agent and a VSCode extension both connected to the same live session

---

## Current State (v0.2.44)

### What works well
- ✅ MCP server with 8 tools — AI agents can evaluate F# code, load scripts, inspect status
- ✅ Shadow-copied assemblies — no DLL locks on project files
- ✅ Iterative warm-up — smart namespace/module resolution with retry
- ✅ RequireQualifiedAccess tolerance — benign open errors don't cascade
- ✅ Console echo — all MCP submissions visible to user with preserved indentation
- ✅ Code-first response format — diagnostics and code always in tool responses
- ✅ Hot reload via Lib.Harmony method patching
- ✅ PrettyPrompt REPL with autocomplete
- ✅ Computation expression rewriting (`let!` at top level)
- ✅ Snapshot-tested output formats (Verify)
- ✅ MCP error guidance — tool descriptions teach transaction semantics, error messages discourage unnecessary resets
- ✅ Reset pushback — healthy-session resets include ⚠️ warning, warmup-failure resets do not
- ✅ Daemon mode — headless server (`SageFs -d`), no PrettyPrompt/TTY deps, `daemon.json` discovery
- ✅ Sub-process sessions — workers spawned via SessionManager, named pipe IPC, fault isolation
- ✅ SessionManager — Erlang-style supervisor with exponential backoff restart
- ✅ SessionMode DU — Embedded/Daemon routing for session management tools
- ✅ Unified SageFsError DU — all errors consolidated into single typed DU across all layers
- ✅ DDD type safety — SessionStatus, DiagnosticSeverity, CompletionKind, ToolUnavailable DUs replace strings
- ✅ Early MCP status — available during WarmingUp, `--bare` flag for quick sessions
- ✅ 261 tests (unit + integration + snapshot)

### What's fragile or missing
- ✅ Eval cancellation — direct CTS bypass, cancel_eval MCP tool, 2 integration tests
- ✅ MCP autocomplete tool — get_completions MCP tool with cursor position
- ✅ MCP diagnostics tool — `check_fsharp_code` pre-validates code. 9 tests.
- ✅ MCP responses stripped of echoed code and boilerplate — formatEvalResult returns only Result/Error, usage tips moved to ServerInstructions
- ❌ No Datastar SSE streams — IDE plugins have no reactive state subscription
- ✅ EventTracking persisted to Marten (when SageFs_CONNECTION_STRING set). In-memory still unbounded without Postgres.
- ✅ `QualityOfLife.fs` fully migrated — ErrorMessages to own file, EvalStats in Affordances.fs, QualityOfLife.fs deleted. Eval count/timing stats surfaced in status.
- ✅ Affordance-driven MCP — SessionState DU, availableTools, state transitions, 13 tests
- ✅ `formatStartupInfoJson` uses `JsonSerializer.Serialize` — properly escapes Windows paths
- ✅ `formatDiagnosticsStoreAsJson` uses `JsonSerializer.Serialize` — no more manual JSON
- ❌ No VSCode extension — only a settings.json patcher script
- ❌ No CI/CD pipeline for SageFs itself
- ✅ Middleware pipeline runs on dedicated thread — eval actor stays responsive during Fantomas parsing
- ❌ Custom state bag uses `Map<string, obj>` — unsafe downcasts everywhere
- ✅ `hard_reset_fsi_session` with `rebuild=true` — GC cycle after session dispose releases collectible ALC and DLL locks, retry on access denied
- ✅ Daemon mode — headless server, sub-process sessions, named pipe IPC, SessionManager supervisor
- ✅ DDD type safety — SageFsError, SessionMode, CompletionKind, SessionStatus, DiagnosticSeverity, ToolUnavailable DUs
- ❌ No per-tool session routing — eval/reset/check MCP tools always use local AppActor, can't target specific worker sessions by sessionId
- ❌ No watchdog supervisor — daemon doesn't auto-restart on crash
- ❌ No startup profile — no `~/.SageFs/init.fsx` or per-project startup scripts

---

## Tier 0 — Critical Infrastructure

### 0.1 Testcontainer Persistence Across Runs

**Problem:** The Testcontainers `PostgreSqlBuilder()` in `EventStoreTests.fs` creates an ephemeral container with no volume mount. When the test process exits, the container is destroyed and all event data is lost. This defeats the purpose of event sourcing — the whole point is that session history persists and can be replayed. The `compose.yml` has a proper `sagefs-pgdata` volume, but the test infrastructure ignores it.

**Fix:** Add a Docker volume mount to the Testcontainers builder so PostgreSQL data survives container restarts:

```fsharp
let sharedContainer = lazy(
  let container =
    Testcontainers.PostgreSql.PostgreSqlBuilder()
      .WithDatabase("SageFs_test")
      .WithUsername("postgres")
      .WithPassword("SageFs")
      .WithImage("postgres:17")
      .WithVolumeMount("sagefs-test-pgdata", "/var/lib/postgresql/data")
      .WithReuse(true)
      .Build()
  container.StartAsync().GetAwaiter().GetResult()
  container
)
```

**Key points:**
- `.WithVolumeMount()` — named Docker volume persists data across container restarts
- `.WithReuse(true)` — Testcontainers keeps the container alive across test runs instead of destroying it
- Test schemas still isolate individual test cases (existing `createStore schemaName` pattern)
- Event history accumulates across runs, enabling replay and audit scenarios
- The volume name `sagefs-test-pgdata` is distinct from the compose volume `sagefs-pgdata` to avoid cross-contamination

**Tests to add:**
- Verify events written in a previous test run are still readable after container restart
- Verify `fetchStream` returns complete history across sessions

---

## Tier 1 — Reliability & Agent Experience (High Impact, Medium Effort)

### 1.0 MCP Response Strategy — ✅ DONE
### 1.0b Affordance-Driven MCP — ✅ DONE

> See [COMPLETED_IMPROVEMENTS.md](COMPLETED_IMPROVEMENTS.md) for full details of completed items.

### 1.0c MCP Error Guidance & Reset Pushback — ✅ DONE

> See [COMPLETED_IMPROVEMENTS.md](COMPLETED_IMPROVEMENTS.md) for full details.

### 1.1 Eval Cancellation — ✅ DONE
### 1.2 MCP Autocomplete Tool — ✅ DONE
### 1.3 MCP Diagnostics Tool — ✅ DONE

> See [COMPLETED_IMPROVEMENTS.md](COMPLETED_IMPROVEMENTS.md) for full details.

### ~~1.4 Event Ring Buffer~~ — Subsumed by 0.0
### 1.5 QualityOfLife.fs — ✅ DONE

> See [COMPLETED_IMPROVEMENTS.md](COMPLETED_IMPROVEMENTS.md) for full details.

---

## Tier 2 — Developer Experience & IDE Integration (High Impact, High Effort)

### 2.0 Actor Split — ✅ DONE

> See [COMPLETED_IMPROVEMENTS.md](COMPLETED_IMPROVEMENTS.md) for full details.

### 2.1 VSCode Extension

**Problem:** The `configure-vscode.ps1` script just patches `settings.json` to point Ionide at SageFs's DLL. There's no real integration — no status bar, no inline diagnostics, no "Send to SageFs" action.

**Solution — VSCode extension with:**
- **Status bar widget** showing SageFs connection state (connected/disconnected/warm-up)
- **"Send selection to SageFs"** command (Ctrl+Enter or Alt+Enter)
- **Inline diagnostics** — run `check_fsharp_code` on save, show squiggles
- **Output panel** — SageFs evaluation results in a dedicated panel
- **CodeLens** — "Run" buttons above top-level `let` bindings and test functions
- **Snippets** — common F# REPL patterns (`testList`, `testProperty`, `open ...`)

**Architecture:**
- Extension connects to SageFs MCP server via SSE (already running)
- Uses MCP tools for all operations — no custom protocol needed
- Extension is a thin UI shell; SageFs does all the work

**This is the single biggest differentiator.** No other F# tool offers this level of REPL-IDE integration.

### 2.2 File Watching & Auto-Reload — ✅ DONE

> See [COMPLETED_IMPROVEMENTS.md](COMPLETED_IMPROVEMENTS.md) for full details.

### 2.3 Notebook Support (.dib / .fsx)

**Problem:** Jupyter-style notebooks are the standard for interactive data exploration. F# has Polyglot Notebooks but it's heavy (requires .NET Interactive kernel). SageFs could be a lightweight alternative.

**Solution:**
- Parse `.dib` (Polyglot Notebook format) or `.fsx` with `(* --- *)` cell markers
- Execute cells individually via `send_fsharp_code`
- Return structured results (text, HTML, images via `data:` URIs)
- VSCode extension renders results inline

**Lighter-weight alternative to Polyglot Notebooks** — no separate kernel, uses the same SageFs session that has your project loaded.

### 2.4 Structured Output — ✅ DONE

> See [COMPLETED_IMPROVEMENTS.md](COMPLETED_IMPROVEMENTS.md) for full details.

### 2.5 Neovim Plugin — Enhance Existing `fsi-mcp.lua` (Priority over VSCode)

**Existing foundation:** A working `fsi-mcp.lua` plugin (~776 lines) already lives in the user's dotfiles at `~/.config/nvim/lua/plugins/fsi-mcp.lua`. It provides:

| Working today | How it works |
|--------------|--------------|
| Port discovery | Checks `SageFs_MCP_PORT` env → `vim.g.SageFs_mcp_port` → scans 37749-37759 via `Get-NetTCPConnection` |
| Server lifecycle | `:SageFsStart` (hidden), `:SageFsStartVisible` (terminal split), `:SageFsStop`, `:SageFsRestart`, `:SageFsToggle` |
| Send code | HTTP POST to `/exec` with JSON `{code: text}`. Auto-appends `;;`. Uses temp files for payloads >7KB |
| Visual selection | `M.send_selection_to_fsi()` — extracts visual selection marks, sends via HTTP |
| Send line | `M.send_line_to_fsi()` — sends current line |
| Keybindings | `<leader>x*` for SageFs lifecycle, `<leader>t*` for terminal toggles, `<M-CR>` for send |
| Terminal management | toggleterm.nvim integration, bottom split with resize 15 |
| Alt-Enter dispatch | keymaps.lua checks for `_G.FsiMcp`, falls back to Ionide's `SendSelectionToFsi` |
| Global module | Exports `_G.FsiMcp` for cross-plugin access |
| Notifications | Emoji-rich `vim.notify()` for status, timing, errors |

**What's missing (the enhancement target):**

| Missing feature | Why it matters |
|----------------|----------------|
| **Inline virtual text results** | Results only appear in `vim.notify()` — no inline display next to code. Molten-nvim and Conjure both show results inline. |
| **blink.cmp completion source** | No REPL-aware completions. Ionide LSP completions don't know about REPL-defined types. |
| **Diagnostic namespace** | No SageFs-specific diagnostics. Can't pre-check code without evaluating. |
| **Tree-sitter structural sending** | Only visual selection and single-line. No "send enclosing let binding" or "send enclosing module". |
| **Floating output window** | Large results (multi-line type signatures, test output) crammed into notifications. |
| **Structured response parsing** | Current `/exec` returns JSON `{success, result, error}` synchronously. CQRS migration will replace this with `POST /exec` → 202, results via `GET /eval` SSE stream. |
| **SSE event streams** | No live connection — each eval is a one-shot HTTP POST/response. The CQRS+SSE architecture (see top-level design) replaces this with persistent subscriptions. |

**Why Neovim first (over VSCode):**

1. **Virtual text / extmarks** — Neovim can render evaluation results *inline* next to code. `type Dog = { Name: string }` → result appears as virtual text on the next line. Fundamentally different UX than VSCode's output panels.

2. **Tree-sitter F# grammar** — Already installed via LazyVim's `lazyvim.plugins.extras.lang.dotnet` extra (which includes `ensure_installed = { "fsharp" }`). Enables *structural* code sending — send the enclosing `let` binding, the enclosing `module`, the enclosing test, not just visual selection.

3. **No marketplace lock-in** — Lua plugins are just git repos. No publishing pipeline. Install with `lazy.nvim` (already configured with dev path `C:/Code/Repos` for local development).

4. **Existing integration surface** — The `fsi-mcp.lua` plugin is already wired into keymaps.lua with Alt-Enter dispatch, `_G.FsiMcp` global, toggleterm.nvim optional dependency. Enhancements slot right in.

**Architecture — enhancing `fsi-mcp.lua` → standalone `SageFs.nvim` repo:**

```
Current:  ~/.config/nvim/lua/plugins/fsi-mcp.lua  (dotfiles, single file)
Target:   C:/Code/Repos/SageFs.nvim/                (standalone repo, lazy.nvim dev plugin)
          ├── lua/
          │   ├── SageFs/
          │   │   ├── init.lua          (core: port discovery, lifecycle, send)
          │   │   ├── virtual-text.lua  (inline result display via extmarks)
          │   │   ├── treesitter.lua    (structural code extraction)
          │   │   ├── diagnostics.lua   (diagnostic namespace for check_fsharp_code)
          │   │   ├── completion.lua    (blink.cmp source for get_completions)
          │   │   └── output.lua        (floating window for large results)
          │   └── cmp_SageFs/
          │       └── init.lua          (blink.cmp custom source registration)
          └── plugin/
              └── SageFs.lua              (auto-setup, user commands, _G.FsiMcp compat)
```

The `lazy.lua` dev path `C:/Code/Repos` means `{ "WillEhrendreich/SageFs.nvim", dev = true }` will load from the local repo during development.

**Enhancement plan (builds on existing code):**

| Phase | Feature | Keybinding | What it does |
|-------|---------|------------|--------------|
| A | **Inline results** | (automatic after eval) | Subscribe to `GET /eval` SSE stream. On eval-complete event, display result as virtual text below the sent code using `nvim_buf_set_extmark` with `virt_lines` |
| A | **Floating output** | `<leader>xo` | Large results (>3 lines) from `GET /eval` SSE stream open in a floating window instead of notification. Snacks.nvim floating windows already configured. |
| A | **Send form** | `<leader>xf` | Tree-sitter query: find enclosing `value_declaration`, `type_definition`, or `module_definition` and send it |
| B | **Completion source** | (blink.cmp auto) | Register `blink_SageFs` source. POST to `/completions`, results arrive via `GET /completions` SSE stream. Returns `{name, kind, description}` |
| B | **Diagnostics** | `<leader>xd` or on-save | POST to `/diagnostics` (command). Subscribe to `GET /diagnostics` SSE stream. Set diagnostics in a `SageFs` namespace separate from Ionide's `fsautocomplete` |
| B | **Send test** | `<leader>xT` | Tree-sitter: find enclosing `testList`/`testCase`/`testProperty` and send it |
| C | **SSE streaming** | (automatic on connect) | Subscribe to `GET /eval`, `GET /diagnostics`, `GET /status` SSE streams at startup. Parse Datastar morph fragments, maintain materialized views client-side. All UI updates are driven by morph events — the plugin is a pure rendering layer. |
| C | **Which-key integration** | (automatic) | Register `<leader>x` group as "SageFs" in which-key. LazyVim already uses which-key. |

**What makes this mind-blowing vs. existing options:**

- **Conjure** sends text to a generic REPL process. SageFs sends to a *semantic* F# evaluator with type checking, diagnostics, autocomplete, project loading, and hot reload.
- **iron.nvim** is a terminal multiplexer. SageFs is an intelligent backend — it pre-checks code, offers completions, returns structured results with type signatures.
- **Molten-nvim** needs Jupyter kernels, Python, pynvim. SageFs is a single `dotnet tool` — zero Python, zero Jupyter, pure .NET.
- **Ionide-vim** provides LSP features but NO interactive evaluation. SageFs provides eval + LSP-like features (diagnostics, completions) from the REPL's perspective — including runtime state, loaded assemblies, and REPL-defined types.

**The killer feature: eval-aware diagnostics.** When you define `type Dog = { Name: string }` in the REPL, SageFs knows about `Dog`. Your next `check_fsharp_code` call can validate code that uses `Dog` — even though no `.fs` file contains it. No other Neovim F# tool can do this.

**Neovim plugin ↔ SageFs server responsibility boundary:**

The existing `fsi-mcp.lua` does some things that properly belong in SageFs itself (as HTTP endpoints), and some things that properly belong in the plugin. Assess:

| Current fsi-mcp.lua behavior | Should live in... | Why |
|------------------------------|-------------------|-----|
| Port discovery (scan 37749-37759) | ✅ **Plugin** | Client-side concern — SageFs can't tell Neovim what port it's on before Neovim connects |
| Server lifecycle (start/stop/restart) | ✅ **Plugin** | Process management is editor-side |
| `POST /exec` code sending | ✅ **Both** — plugin posts commands, subscribes to `GET /eval` SSE for results | CQRS: POST is command (202), results flow via SSE |
| Temp file for large payloads | ⚠️ **Should move to SageFs** — add streaming request body support | Plugin shouldn't need to work around HTTP payload limits |
| `is_fsi_mcp_available` (curl to `/`) | ⚠️ **Should use `/health`** — endpoint already exists | The root `/` check for "SageFs MCP Server" is fragile |
| Auto-append `;;` | ⚠️ **Could move to SageFs** — server already handles `;;` in `splitStatements` | Duplicated logic; but keeping in plugin is harmless |
| Response parsing (success/error) | ✅ **Plugin** | UI concern — renders SSE events as virtual text, notifications, floating windows |
| Diagnostics check | 🆕 **Add to SageFs** — `POST /diagnostics` (command, 202) + `GET /diagnostics` (SSE morph stream) | Plugin subscribes at startup, posts checks on save, receives morph fragments |
| Completions | 🆕 **Add to SageFs** — `POST /completions` (command, 202) + `GET /completions` (SSE morph stream) | Plugin subscribes for blink.cmp, receives morph fragments |

**Note:** The full HTTP endpoint map is defined in the "Tao of Datastar" section at the top. All new endpoints follow the same CQRS + SSE pattern — commands return `202 Accepted`, results flow via SSE streams.

**User's Neovim environment (what we integrate with):**
- **LazyVim** base distribution with extras: `lang.dotnet` (tree-sitter fsharp, fsautocomplete LSP, fantomas, netcoredbg), `ai.copilot`, `dap.core`, `test.core`
- **Ionide-nvim** (user's own fork `WillEhrendreich/Ionide-nvim`) provides LSP via `fsautocomplete`
- **toggleterm.nvim** — terminal management, vertical split default, `<F7>` toggle
- **snacks.nvim** — picker, explorer, floating windows, scratch buffers
- **harpoon** — buffer bookmarks
- **blink.cmp** (via LazyVim) — completion framework
- **nvim-dap** — debug adapter, already configured for `netcoredbg` with F# support
- **neotest** — test runner integration
- **Named pipe server** for Neovim MCP — `\\.\pipe\nvim-{1-10}` started in init.lua
- **Shell:** PowerShell Core (pwsh) — affects how we spawn processes and run commands

---

## Tier 3 — Ecosystem & Community (Medium Impact, Medium Effort)

### 3.2 Daemon Mode & Multi-Session Support — ✅ DONE (1.0 Scope)

> See [COMPLETED_IMPROVEMENTS.md](COMPLETED_IMPROVEMENTS.md) for full details. Summary of what shipped:
>
> - **Daemon extraction** — `DaemonMode.fs`, `DaemonState.fs`, `ClientMode.fs`, smart CLI routing (`SageFs`, `SageFs -d`, `SageFs stop/status`)
> - **Worker process infrastructure** — `WorkerProtocol.fs` (WorkerMessage/WorkerResponse DUs, SessionProxy abstraction), `NamedPipeTransport.fs` (length-prefixed JSON over named pipes), `WorkerMain.fs`
> - **SessionManager** — Erlang-style supervisor (spawn/monitor/restart workers), RestartPolicy with exponential backoff, SessionLifecycle exit classification
> - **SessionOperations** — pure domain routing (`resolveSession` with explicit/default-single/default-most-recent), session formatting
> - **DDD type safety** — SageFsError unified DU, SessionMode DU, CompletionKind DU, SessionStatus DU, DiagnosticSeverity DU, ToolUnavailable DU
> - **SessionMode routing** — MCP session management tools (create/list/stop) dispatch through `SessionMode.Embedded | SessionMode.Daemon`
> - **DaemonMode wiring** — real SessionManager with SessionManagementOps bridge, graceful shutdown
> - **Early MCP status** — MCP available during WarmingUp, `--bare` flag
> - **Tests:** 261 tests pass (including 3 SessionManager lifecycle integration tests)
>
> **Remaining (Post-1.0):** per-tool sessionId routing (3.2b), watchdog supervisor (Phase 3), REPL client polish (Phase 4)

**Problem:** SageFs dies when the terminal closes. PrettyPrompt crashes when there's no TTY (detached, piped, backgrounded) because it manipulates Windows console mode flags — and when PrettyPrompt crashes, the entire MCP server dies with it. Starting SageFs is manual. You can't have multiple independent FSI sessions sharing the same persistence layer. Connecting from different editors (Neovim, VSCode, terminal, web) requires knowing which port to hit and whether the process is even running.

**Root cause:** SageFs is monolithic — PrettyPrompt REPL + MCP server + FSI session all in one process. The MCP server's lifecycle is tied to the REPL's lifecycle.

**Vision:** SageFs is a **persistent headless daemon** — it starts automatically, restarts on crash, hosts multiple independent FSI sessions, and any client (terminal REPL, Neovim, VSCode, web UI, AI agent) connects to it. The daemon has NO PrettyPrompt, NO console dependencies. PrettyPrompt lives ONLY in the terminal client and can crash without affecting the engine. All sessions share the same Marten/Postgres event store. It's always there, like a database.

**Key principle:** The daemon is the *engine*. Clients (terminal REPL, editors, AI agents) are interchangeable *frontends*. PrettyPrompt is ONLY loaded in the terminal client, never in the daemon.

#### Architecture: Headless Daemon + Client REPL

```
┌─────────────────────────────────────────────────────────┐
│  SageFs daemon (headless, always-on)                      │
│  • NO PrettyPrompt, NO console dependencies             │
│  • ASP.NET Core: MCP + HTTP + SSE                       │
│  • SessionManager: multiple in-process FSI sessions     │
│  • Watchdog: auto-restart on crash                      │
│  • State in ~/.SageFs/daemon.json (PID, port)             │
│                                                         │
│  ┌────────────────────────────────────────────────────┐  │
│  │ SessionManager (MailboxProcessor)                  │  │
│  │  session-abc: { proj: Tests.fsproj, actor, state } │  │
│  │  session-def: { proj: WebApp.fsproj, actor, state }│  │
│  └────────────────────────────────────────────────────┘  │
│                                                         │
│  MCP tools: send_fsharp_code(session?, code)            │
│  MCP tools: create_session, list_sessions, stop_session │
│  HTTP: /exec, /health, /sessions, /diagnostics          │
│  Shared: Marten IDocumentStore ←→ Postgres              │
└────────────────┬────────────────────────────────────────┘
                 │ connects via HTTP/MCP
     ┌───────────┼───────────────┐
     │           │               │
  Terminal    Neovim MCP     Copilot CLI
  (PrettyPrompt  client       agent
   REPL client)
```

#### CLI Routing (Smart Default)

`SageFs` is always a client — it either connects to an existing daemon or starts one then connects. PrettyPrompt is never in the daemon process.

```bash
SageFs                              # Smart default (see below)
SageFs --proj Foo.fsproj            # Smart default + create/attach session for Foo.fsproj
SageFs -d / --daemon                # Start daemon only (headless, no REPL)
SageFs stop                         # Graceful shutdown of daemon + all sessions
SageFs status                       # Show daemon info + session list with metadata
```

**Smart default (`SageFs` with no flags):**
1. Check `~/.SageFs/daemon.json` — is a daemon already running?
2. **YES →** connect to it with PrettyPrompt REPL (attach to existing/default session or create one for `--proj`)
3. **NO →** start daemon in background, wait for it to be ready, then connect REPL to it

**`SageFs -d` / `SageFs --daemon`** starts daemon only, no REPL, returns immediately. Useful for CI, services, or MCP-only workflows.

#### In-Process Sessions (Not Sub-Processes)

Sessions are FSI actors **within the daemon process**, not separate OS processes. Each session gets its own `ActorCreation.createActor` call → own FSI session, own actor pair (eval + query). This is simpler, faster, and shares the Marten store naturally.

Each session is identified by a short readable ID (e.g., `session-a1b2c3`). Sessions are independent:
- Own `FsiEvaluationSession` (own FSI session with separate state)
- Own actor pair (eval actor + query actor)
- Own Marten stream ID
- Own loaded projects (`.fsproj` bindings)
- Own middleware stack and startup profile
- Own `SessionState` (Ready/Evaluating/Faulted/etc.)

**Session lifecycle:**
- **Created** when a client requests a new session (or on first `SageFs --proj`)
- **Active** while any client is connected or the session has pending work
- **Idle** after all clients disconnect (configurable idle timeout, default: never — sessions persist until explicitly killed)
- **Destroyed** only on explicit `SageFs stop-session <id>` or daemon shutdown

#### Rich Session Metadata

Sessions carry context so they can be displayed like GitHub Copilot's chat history:

```fsharp
type SessionId = string  // e.g. "session-a1b2c3"

type SessionInfo = {
  Id: SessionId
  Projects: string list          // loaded .fsproj files
  WorkingDirectory: string       // where the session was started
  SolutionRoot: string option    // nearest .sln/.slnx parent, auto-detected
  CreatedAt: DateTime
  LastActivity: DateTime
  State: Affordances.SessionState
}
```

> **Design note (making illegal states unrepresentable):** Consider evolving `SessionInfo` into a state-per-case DU where each state carries only the data valid for that state:
> ```fsharp
> type Session =
>   | Initializing of { Id: SessionId; Projects: string list; WorkingDirectory: string }
>   | Ready of { Info: SessionInfo; EvalHistory: EvalResult list; Diagnostics: DiagnosticsState }
>   | Evaluating of { Info: SessionInfo; CurrentEval: EvalRequest; StartedAt: DateTime }
>   | Faulted of { Info: SessionInfo; Error: SessionError }
> ```
> This prevents impossible combinations like `State = Evaluating` with no current eval, or `State = Ready` with no projects. Start with the flat `SessionInfo` record (simpler, sufficient for daemon mode v1), evolve to the DU when the domain demands it.

When listing sessions (MCP, HTTP, or REPL), each shows human-readable context:

```
session-a1b2c3  SageFs.Tests  C:\Code\Repos\SageFs  Ready
  Started: 2026-02-13 11:17  Last active: 2 min ago  Projects: SageFs.Tests.fsproj, SageFs.fsproj
session-d4e5f6  Harmony     C:\Code\Repos\Harmony  Evaluating
  Started: 2026-02-13 10:45  Last active: just now   Projects: Tests.fsproj, HarmonyServer.fsproj
```

Display name is derived from `SolutionRoot` directory name (or `WorkingDirectory` basename if no solution found). `SolutionRoot` is auto-detected by walking up from `WorkingDirectory` looking for `.sln`/`.slnx`.

Connected REPL shows metadata on attach:
```
Connected to session-a1b2c3 (SageFs.Tests)
C:\Code\Repos\SageFs  •  Started 2h ago  •  Last active just now
```

#### Session Attachment Logic

When a REPL client connects:
- If `--proj` matches an existing session's project → attach to it
- If `--proj` given but no match → ask daemon to create new session
- If no `--proj` → attach to most recently active session

#### Daemon State File (`~/.SageFs/daemon.json`)

```json
{
  "pid": 12345,
  "port": 37749,
  "startedAt": "2026-02-13T03:00:00Z"
}
```

Daemon state is minimal — just PID and port for discovery. Session state is queried live from the daemon via HTTP/MCP, not stored in the JSON file.

`DaemonState.fs` handles:
- `write: DaemonInfo -> unit` — atomically write to `~/.SageFs/daemon.json`
- `read: unit -> DaemonInfo option` — read + validate PID is still alive
- `clear: unit -> unit` — remove stale file

#### Watchdog (Phase 3)

The supervisor spawns the daemon as a child process and monitors it:
- On unexpected exit: restart with exponential backoff (1s → 2s → 4s → 8s → max 30s)
- Resets backoff after 60s of stable uptime
- Logs restarts to `~/.SageFs/logs/supervisor.log`
- Responds to `SIGTERM`/`SIGINT` by forwarding to child then exiting
- On Windows: uses `ConsoleCtrlEvent` for clean shutdown

**Implementation approach:** Same binary. `SageFs -d` starts as supervisor, which `Process.Start`s another `SageFs` with an internal `--server` flag. The `--server` flag is not user-facing.

#### MCP Session Routing

MCP tools gain an optional `sessionId` parameter:
- `send_fsharp_code { code: "let x = 42", sessionId: "session-abc" }` → routes to that session
- If `sessionId` is omitted, uses the default session (most recently active, or the one the client is attached to)
- New MCP tools: `create_session`, `list_sessions`, `stop_session`
- Existing MCP clients that don't send `sessionId` keep working (routed to default session) — backward compatible

#### SessionManager (Core Library)

SessionManager lives in `SageFs\SessionManager.fs` (not `SageFs.Server`) so it can be tested independently:

```fsharp
type SessionCommand =
  | CreateSession of args: ActorCreation.ActorArgs * AsyncReplyChannel<Result<SessionId, string>>
  | StopSession of SessionId * AsyncReplyChannel<Result<unit, string>>
  | ListSessions of AsyncReplyChannel<SessionInfo list>
  | GetSession of SessionId * AsyncReplyChannel<(SessionInfo * ActorResult) option>
  | TouchSession of SessionId  // update LastActivity
```

MailboxProcessor manages a `Map<SessionId, SessionInfo * ActorResult>`. Each session gets its own `ActorCreation.createActor` call.

#### Web UI (Falco + Datastar) — Future

A lightweight browser REPL served by the daemon itself:
- `http://localhost:37749/ui` → Falco.Markup rendered page
- Code editor sends to `POST /exec`
- Results stream back via Datastar SSE → DOM morphing
- Session picker in sidebar (create/switch/kill sessions)
- Full HATEOAS — every action is a hypermedia affordance
- Same CQRS pattern: commands → 202, results via SSE

This is NOT a full IDE — it's a browser-accessible REPL for quick interactions when you don't have a terminal or editor handy.

#### Implementation Phases

**Phase 1: Extract the Daemon (headless server)** — the critical unlock
- `SageFs.Server\DaemonMode.fs` — headless server entry point (no PrettyPrompt, no TTY deps)
- `SageFs.Server\DaemonState.fs` — daemon.json lifecycle
- `SageFs.Server\ClientMode.fs` — REPL client that connects to daemon
- `Program.fs` — smart default routing
- Test: start daemon headless, verify MCP tools work, verify no PrettyPrompt crash

**Phase 2: SessionManager (multi-session)** — the multiplier
- `SageFs\SessionManager.fs` — session registry with rich metadata
- Session-aware MCP tools (optional `sessionId` param)
- Session-aware HTTP endpoints (`GET /sessions`, `POST /sessions`, `DELETE /sessions/{id}`)
- Tests: create two sessions with different projects, eval in each, verify isolation

**Phase 3: Watchdog & Persistence** — reliability
- `SageFs.Server\Supervisor.fs` — watchdog process with exponential backoff
- Graceful shutdown (`SageFs stop` → POST /shutdown)
- Session resume on restart (read last-known sessions from Marten, offer to recreate)

**Phase 4: Terminal REPL Client Polish** — UX
- Session picker (interactive list when multiple sessions, no `--proj` match)
- Session switching (`:switch` or `:sessions` REPL command)

#### Dependencies on Earlier Work

- **Marten event store** (0.0) ✅ — shared persistence across sessions
- **Actor split** (2.0) ✅ — each session gets its own actor pair
- **Diagnostics HTTP endpoints** (1.3b) — SSE streams for clients
- **Structured output** (2.4) ✅ — JSON responses for programmatic clients

#### Design Decisions

1. **`SageFs` is always a client** — running `SageFs` either connects to an existing daemon or starts one then connects. PrettyPrompt is never in the daemon process. If the terminal dies, the daemon and all sessions survive.
2. **`SageFs -d` for daemon-only** — explicit flag to start the daemon headless without attaching a REPL. Useful for CI, services, or MCP-only workflows.
3. **Sub-process sessions with location transparency** — each session is a separate OS process (worker). The daemon communicates with workers via a transport-agnostic protocol (`WorkerMessage` DU). Default transport: named pipes (fastest local IPC on Windows, no port allocation). True fault isolation: one session crashing doesn't affect others. The `SessionProxy` abstraction means the same interface works whether the worker is local (named pipe), remote (HTTP), or even in-process (for testing).
4. **SessionManager in SageFs core, not SageFs.Server** — so it can be tested independently and reused by other hosts.
5. **Rich session metadata** — sessions carry WorkingDirectory, SolutionRoot, CreatedAt, LastActivity. Display name derived from solution/directory name. Listing sessions shows human-readable context like GitHub Copilot's chat history.
6. **daemon.json is the discovery mechanism** — clients find the daemon by reading `~/.SageFs/daemon.json`. Simple, no service registry needed.
7. **Backward compatible MCP** — existing MCP clients that don't send `sessionId` keep working (routed to default session).

#### Files (New + Modified)

| File | Action | Purpose |
|------|--------|---------|
| `SageFs\SessionManager.fs` | **NEW** | Session registry, spawns/monitors worker processes |
| `SageFs\WorkerProtocol.fs` | **NEW** | `WorkerMessage` DU, transport abstraction |
| `SageFs\Transports\NamedPipeTransport.fs` | **NEW** | Named pipe transport (default) |
| `SageFs.Server\DaemonState.fs` | **NEW** | daemon.json read/write/validate |
| `SageFs.Server\DaemonMode.fs` | **NEW** | Headless daemon entry point (no PrettyPrompt) |
| `SageFs.Server\ClientMode.fs` | **NEW** | REPL client that connects to daemon |
| `SageFs.Server\WorkerMain.fs` | **NEW** | Session worker process entry point |
| `SageFs.Server\Supervisor.fs` | **NEW** (Phase 3) | Watchdog process |
| `SageFs.Server\Program.fs` | **MODIFY** | Smart default: auto-start daemon + connect |
| `SageFs.Server\CliEventLoop.fs` | **MODIFY** | Becomes a REPL client connecting to daemon |
| `SageFs.Server\McpServer.fs` | **MODIFY** | Session-aware routing |
| `SageFs.Server\McpTools.fs` | **MODIFY** | Optional sessionId param |
| `SageFs\Mcp.fs` | **MODIFY** | McpContext gains SessionManager |
| `SageFs.Server\SageFs.Server.fsproj` | **MODIFY** | Add new files to compile order |
| `SageFs\SageFs.fsproj` | **MODIFY** | Add SessionManager.fs |
| `SageFs.Tests\SessionManagerTests.fs` | **NEW** | Unit + integration tests |
| `SageFs.Tests\DaemonIntegrationTests.fs` | **NEW** | End-to-end daemon tests |

### ~~3.3 Script Persistence & Replay~~ — Subsumed by 0.0

> See [COMPLETED_IMPROVEMENTS.md](COMPLETED_IMPROVEMENTS.md).

### 3.4 Package Explorer Integration

**Problem:** When agents need to discover what types/functions are available from loaded NuGet packages, they have to guess or ask the user.

**Solution:**
- `explore_namespace` MCP tool — lists types, functions, and signatures in a namespace
- `explore_type` MCP tool — shows members, constructors, and documentation
- Uses existing `FSharp.Compiler.Service` APIs (`ParseAndCheckInteraction`, `GetDeclarationListInfo`)
- Combined with autocomplete, this gives agents full API discovery

### 3.5 REPL Startup Profile


**Problem:** Users want custom code to run on every SageFs startup (e.g., `open System`, utility functions, test helpers). Currently `~/.SageFs/repl.fsx` exists but is limited.

**Solution:**
- Support project-local `.SageFsrc` file (auto-detected alongside `.fsproj`)
- Evaluate after warm-up but before REPL prompt
- Can define helper functions, open namespaces, configure output formatting
- MCP tool `get_startup_profile` shows what was loaded

### 3.6 BARE Wire Encoding (LARP Protocol Alignment)

**Problem:** All SageFs wire traffic is JSON text. For machine-to-machine paths (SSE streams to Neovim plugin, HTTP commands from IDE plugins), JSON carries redundant field names in every message. Over a session with hundreds of eval cycles, diagnostic updates, and status changes, this is kilobytes of repeated structure. Concrete analysis shows **64-89% size reduction** per message type (see architecture section above for byte-level encoding comparisons).

**Solution:**
- Add BARE encoding as an opt-in wire format for SSE streams and HTTP commands
- SageFs domain types (EvalResult, Diagnostic, StatusUpdate, SessionState) define the BARE schema — F# DUs map directly to BARE unions, records to structs, enums to BARE enums
- Protocol negotiation: `Accept: application/bare` header for SSE, `Content-Type: application/bare` for commands — clients that understand BARE opt in; JSON remains default
- MCP transport always stays JSON (MCP spec requires it; LLMs need readable text)
- Ideal transport for BARE clients: **WebSocket** (natively binary) rather than SSE (text-based, requires base64 encoding)
- Neovim plugin embeds a ~100-line Lua BARE decoder (no external dependency needed)

**Why BARE over alternatives:**
- **vs MessagePack** — MessagePack is self-describing (~30% savings); BARE removes field names entirely (60-89% savings for known schemas)
- **vs Protocol Buffers** — Protobuf requires `protoc` compiler, `.proto` files, generated code. BARE spec fits in 22 pages, a complete F# codec is ~200 lines
- **vs CBOR** — Same issue as MessagePack: self-describing, carries field names
- BARE aligns with LARP's architecture and the F# type system IS the schema

**Dependencies:** `BareNET` v0.3.0 (NuGet, netstandard2.0, stable) for server-side encoding. Optional: `BareFs` as F# wrapper. BAREWire is aspirational for zero-copy but under active development.

**Files:** New `BareEncoding.fs` module, `McpServer.fs` (content negotiation middleware), Neovim plugin `bare.lua` decoder (~100 lines)

**Priority:** P2 — implement after Neovim plugin SSE integration works with JSON. BARE is a performance optimization, not a prerequisite.

**See:** Full BARE spec analysis with encoding examples, type mappings, and transport architecture in "Wire Efficiency: BARE as an Ideal Target" section above.

---

## Tier 4 — Advanced Features (High Impact, Very High Effort)

### 4.1 Rich Output Rendering

**Problem:** FSI output is plain text. No charts, tables, HTML, or images.

**Solution:**
- Detect types with `ISageFsRenderable` interface or specific patterns
- Render `list<'T>` as ASCII tables
- Render `seq<float>` as sparkline charts
- Support `Html of string` for arbitrary HTML output (displayed in VSCode extension)
- Support `Chart of ...` using a lightweight charting library
- Terminal output stays text; VSCode extension renders rich content

### 4.2 Debugger Integration

**Problem:** Can't set breakpoints or step through code evaluated in SageFs.

**Solution:**
- Emit PDB information for evaluated code
- Connect to VS debugger or DAP (Debug Adapter Protocol)
- Breakpoints in `.fsx` files or inline REPL code
- Step through hot-reloaded methods

**This is extremely hard** and may require FCS changes. Lower priority but would be transformative.

### 4.3 Type Provider Live Exploration

**Problem:** Type providers (SQL, JSON, CSV, etc.) are powerful but hard to explore interactively.

**Solution:**
- Specialized MCP tools for common type providers
- `explore_sql_schema` — shows tables/columns from a SqlProvider connection
- `explore_json_schema` — shows structure from a JsonProvider sample
- Auto-detect loaded type providers and offer exploration tools

### 4.4 AI-Native Features

**Problem:** SageFs is used by AI agents but doesn't help them learn F# patterns or improve their code.

**Solution:**
- **Code review tool** — `review_fsharp_code` runs the code through FCS and returns idiomatic suggestions (e.g., "use pattern matching instead of if/else", "this can be a pipeline")
- **Test generation tool** — given a function signature, generate Expecto property-based test scaffolding
- **Refactoring tools** — rename, extract function, inline (via FCS symbol resolution)
- **Example lookup** — "how do I use List.groupBy?" returns live-evaluated examples

---

## Version 1.0 Scope

**1.0 is a shippable, useful product.** Everything else is post-1.0. The line is drawn here to prevent [second-system effect](https://en.wikipedia.org/wiki/Second-system_effect).

**In 1.0:** ✅ ALL COMPLETE
- ✅ Daemon mode: headless server, no PrettyPrompt dependency, `~/.SageFs/daemon.json` discovery
- ✅ Sub-process sessions: each session is an isolated worker process (named pipe IPC)
- ✅ MCP tools: all current tools (`send_fsharp_code`, `check_fsharp_code`, `get_completions`, `cancel_eval`, etc.) + session management (`create_session`, `list_sessions`, `stop_session`)
- ✅ Simple HTTP endpoints: `/health`, `/exec` (request-response), `/status` (SSE)
- ✅ Smart CLI default: `SageFs` auto-starts daemon + connects REPL, `SageFs -d` starts daemon only
- ✅ Affordance-driven state machine
- ✅ Event sourcing with Marten
- ✅ Actor split
- ✅ DDD type safety: SageFsError, SessionMode, CompletionKind, SessionStatus, DiagnosticSeverity DUs
- ✅ SessionManager with Erlang-style supervisor (spawn/monitor/restart with exponential backoff)

**Post-1.0:**
- Per-tool MCP session routing (optional `sessionId` param on eval/reset/check tools)
- Watchdog/supervisor process
- Neovim plugin
- VSCode extension
- BARE wire encoding
- Epistemic validity / environment fingerprints
- REPL/TUI architecture — custom Elm loop, immediate-mode rendering, multi-frontend (see Phase 6)
- Browser-based UI (Datastar web adapter)
- Notebook support
- Rich rendering, debugger, AI-native features

---

## Priority Matrix

### Completed

> See [COMPLETED_IMPROVEMENTS.md](COMPLETED_IMPROVEMENTS.md) for full details of: 0.0 (Marten), 1.0 (MCP response), 1.0b (affordances), 1.0c (error guidance + pushback mechanism), 1.1 (cancellation), 1.2 (autocomplete), 1.3 (diagnostics), 1.5 (QualityOfLife), 2.0 (actor split), 2.4 (structured output), Phase 0 eval blocking fix, 3.2 (daemon mode + multi-session + DDD type safety).

### Current Priorities

| Priority | Item | Impact | Effort | Dependency |
|----------|------|--------|--------|------------|
| 🟡 P2 | **3.2b Per-tool MCP session routing** | Very High | Medium | 3.2 ✅ |
| 🟡 P2 | **2.5 Neovim plugin enhancement** | Very High | High | 1.3 ✅, 1.2 ✅, 2.0 ✅ |
| 🟡 P2 | **3.4 Package explorer** | High | Medium | 1.2 ✅ |
| 🟡 P2 | **3.5 Startup profile** | Medium | Low | None |
| 🟡 P2 | 0.1 Testcontainer persistence | Medium | Low | 0.0 ✅ |
| 🟡 P2 | 3.6 BARE wire encoding | High | Medium | 2.5 (Neovim SSE working with JSON first) |
| 🟡 P2 | 3.7 Epistemic validity (environment fingerprints) | High | Medium | 0.0 ✅, 2.2 ✅ (file watching) |
| 🟢 P3 | 2.1 VSCode extension | Very High | High | 2.5, 1.2 ✅, 1.3 ✅ |
| 🟢 P3 | 2.3 Notebook support | High | High | 2.5 or 2.1 |
| 🔵 P4 | 4.1 Rich rendering | High | High | 2.5 |
| 🔵 P4 | 4.4 AI-native features | High | High | 1.2, 1.3 |
| 🔵 P4 | 4.2 Debugger | Very High | Very High | FCS changes |
| 🔵 P4 | 4.3 Type provider | Medium | High | 3.4 |

---

## Recommended Execution Order

### Completed Phases (0, 0b, 1)

> All completed phases are documented in [COMPLETED_IMPROVEMENTS.md](COMPLETED_IMPROVEMENTS.md).

### Phase 2: Daemon Extraction (Separation of Concerns) — ✅ DONE
10. ✅ **Daemon mode** (3.2, Phase 1) — headless daemon, `DaemonMode.fs`, `DaemonState.fs`, `ClientMode.fs`, smart CLI routing
11. ✅ **SessionManager** (3.2, Phase 2) — sub-process sessions, named pipe IPC, session metadata, SessionMode DU routing, DDD type hardening (SageFsError, CompletionKind, SessionStatus, DiagnosticSeverity DUs)

### Phase 3: Neovim Integration & Watchdog
13. Neovim plugin — MVP (2.5) — SSE subscription driven by Marten async daemon, inline results, diagnostics
14. **Watchdog** (3.2, Phase 3) — supervisor process, exponential backoff, graceful shutdown
15. Package/namespace explorer (3.4)
16. Startup profile (3.5)
17. BARE wire encoding (3.6) — after SSE works with JSON
18. Epistemic validity / environment fingerprints (3.7) — `AssembliesChanged` events, `EnvironmentFingerprint` in metadata, `assessValidity` for selective replay

### Phase 4: Live Development & VSCode
20. VSCode extension (2.1) — adapted from upstream sagefs-vscode

### Phase 5: Best-in-Class
21. **REPL client polish** (3.2, Phase 4) — interactive session picker, `:switch`/`:sessions` REPL commands
22. Notebook support (2.3)
23. Rich output rendering (4.1)
24. AI-native features (4.4)
25. Debugger integration (4.2)

### Phase 6: REPL/TUI Architecture (Immediate-Mode Elm)

Full research: [docs/repl-tui-research.md](docs/repl-tui-research.md)

**Architecture decision:** Custom Elm loop in F# (~40 lines), immediate-mode rendering (`UI = render(state)`), five known frontends consuming `RenderRegion list`.

**Core domain types:**
- `RenderRegion` — uniform render output with `RegionFlags` (Fleury-style feature flags, not widget kinds)
- `Affordance` — `{ Action; Label; KeyHint: KeyCombo option; Enabled }` — HATEOAS for all UIs
- `EditorAction` / `EditorEffect` — Elm Architecture `Msg`/`Cmd` equivalents
- `SageFsView` — bounded read model projection for render functions
- `KeyMap` — `Map<KeyCombo, EditorAction>` shared config across all frontends

**Build order:**
1. **Phase 0: Tree-Sitter Foundation** — `ionide/tree-sitter-fsharp` via `TreeSitter.DotNet`, syntax highlighting, incremental parsing
2. **Phase 1: Core Domain Types** — `RenderPipeline.fs` (`RenderRegion`, `RegionFlags`, `Affordance`), `Editor.fs` (`EditorAction`, `EditorEffect`, `SageFsView`, `update` function), `ElmLoop.fs` (~40 lines)
3. **Phase 2: Terminal Adapter** — replaces PrettyPrompt, consumes `RenderRegion list` → ANSI
4. **Phase 3: Datastar Web Adapter** — SSE-morphed HTML, validates that `RenderRegion list` abstraction works for non-terminal UIs
5. **Phase 4: Neovim/VSCode Adapters** — ext_linegrid-inspired protocol, progressive capability negotiation
6. **Phase 5: Raylib/ImGui Adapter** — GPU-rendered IMGUI via Hexa.NET.ImGui

**Key principles:**
- Push-based reactive streaming everywhere (SageFsEvent bus → all frontends subscribe)
- Affordance-driven: domain decides what's *possible*, adapters decide how to *render*
- CQRS: `EditorAction` commands in, `SageFsEvent` events out
- Animation state managed by rendering engine (not in `RenderRegion`)
- No diffing — full `render(state)` each frame (for <50 widgets, sub-millisecond)
- `FsToolkit.ErrorHandling` for `asyncResult { }` at effect handler edges

---

## Non-Goals (Explicitly Out of Scope)

- **Replacing Ionide** — SageFs complements Ionide, doesn't replace it. Ionide handles project editing, navigation, refactoring. SageFs handles interactive evaluation.
- **Competing with Polyglot Notebooks** — SageFs is a REPL-first tool with optional notebook support, not a notebook-first tool.
- **Supporting languages other than F#** — SageFs is F#-only by design. C# Interactive and Python have their own excellent tools.

---

## Architecture Notes

### Key Architectural Decisions (Summary)

These are documented in detail in the sections above. This is a quick reference:

1. **MCP IS the daemon** — no separate JSON-RPC process. MCP server on port 37749 is the integration point. SageFs runs as a persistent background service (supervisor watchdog) with multi-session support. Terminal REPL, Neovim, VSCode, and web UI are all clients connecting to the same daemon.
2. **Marten + PostgreSQL event store** — all state changes as persisted events. Projections for read models. Replaces EventTracker, enables session replay, multi-process, multi-agent.
3. **Actor split** — eval actor (serializes FSI mutations) + query actor (reads from projections concurrently). Both read/write the same Marten event stream.
4. **Full CQRS + SSE** — POST = command (202 Accepted), GET = SSE subscription via Datastar. Two consumer patterns: LLMs via MCP (curated text), IDE plugins via Datastar SSE (reactive signals).
5. **Affordance-driven MCP** — LARP-inspired state machine. Tools filtered by session state. Agents see only valid actions.
6. **Epistemic validity** — environment fingerprints on events track which assembly versions were active. `AssembliesChanged` events trigger stale-event counting. Selective replay re-evaluates only stale events.
7. **BARE wire encoding** (P2) — binary encoding for machine-to-machine paths after JSON SSE is working.
8. **Sub-process sessions with location-transparent messaging** — each session is a separate worker process spawned and supervised by the daemon. The daemon communicates with workers via a `WorkerMessage` DU (pure F# types, serialization-agnostic). Transport is pluggable:
   - **Named pipes** (default): fastest local IPC, no port allocation, OS-managed lifecycle. Pipe name: `sagefs-session-{id}`.
   - **stdin/stdout JSON-RPC**: fallback, works everywhere, simple process model.
   - **HTTP**: future — enables remote workers (different machine, container, datacenter).
   
   **Why sub-processes over in-process:** FSI runs user-submitted code. `StackOverflowException`, native interop crashes, `Environment.Exit()` in user code — these kill the host process. With sub-process sessions, one crash affects only that session. The daemon monitors worker health and can restart crashed workers automatically. This is Erlang-style supervision: let it crash, restart cleanly.
   
   **Actor model with location transparency:** The `SessionProxy` abstraction (send message, get response) works identically whether the worker is on a named pipe, an HTTP endpoint, or even in-process (useful for testing). The SessionManager doesn't know or care where the worker lives — it just sends `WorkerMessage` values and gets `WorkerResponse` values back.

### Core Functional Principles

These principles are non-negotiable for all SageFs code. They apply to every module, every refactor, every new feature.

#### Pure functions over stateful objects

```fsharp
// ❌ BAD: Stateful, hard to test
type SessionManager() =
  let mutable sessions = Map.empty
  member _.AddSession(session) =
    sessions <- Map.add session.Id session sessions
  member _.GetSession(id) = Map.tryFind id sessions

// ✅ GOOD: Pure functions, easy to test
type SessionState = Map<string, SessionInfo>

let addSession (session: SessionInfo) (state: SessionState) : SessionState =
  Map.add session.Id session state

let getSession (id: string) (state: SessionState) : SessionInfo option =
  Map.tryFind id state
```

#### Partial application over dependency injection

```fsharp
// ❌ BAD: DI container, hidden dependencies
type SessionService(logger: ILogger, config: IConfig) =
  member _.StartSession() = ...

// ✅ GOOD: Partial application, explicit dependencies
let startSession
  (logger: string -> unit)
  (createActor: ActorArgs -> ActorResult)
  (workingDir: string)
  (projects: string list)
  : Result<SessionInfo, string> =
  logger "Starting session..."
  let actor = createActor { Projects = projects; WorkingDir = workingDir }
  // ...

// Create specialized versions by partially applying dependencies:
let startSessionWithDefaults = startSession (printfn "%s") ActorCreation.createActor
```

#### Functions, not interfaces

```fsharp
// ❌ BAD: Interface-based abstraction
type IProcessSpawner =
  abstract Spawn: string -> Process

// ✅ GOOD: Function-based abstraction
type ProcessSpawner = string -> Process

// Easy to test - just pass a mock function
let testSpawner (_: string) : Process = createFakeProcess ()
```

#### Simplicity over cleverness

```fsharp
// ❌ BAD: Over-engineered
type ISessionFactory =
  abstract Create: SessionConfig -> Result<ISession, Error>
type ISessionRepository =
  abstract Save: ISession -> Task<unit>
type SessionOrchestrator(factory: ISessionFactory, repo: ISessionRepository) = ...

// ✅ GOOD: Simple, direct
let createSession (state: SessionState) (config: SessionConfig)
  : Result<SessionInfo * SessionState, string> =
  match validateConfig config with
  | Error e -> Error e
  | Ok validated ->
    let session = { Id = generateId (); Config = validated }
    let newState = Map.add session.Id session state
    Ok (session, newState)
```

#### Avoid global mutable state

**Exception**: FSI session state itself (that's the whole point — we need mutable execution context).

**Everything else should be immutable or passed explicitly.** Use MailboxProcessor (agent) for concurrent access:

```fsharp
type SessionCommand =
  | AddSession of SessionInfo * AsyncReplyChannel<ServerState>
  | GetSession of string * AsyncReplyChannel<SessionInfo option>

let sessionAgent = MailboxProcessor.Start(fun inbox ->
  let rec loop (state: ServerState) = async {
    let! msg = inbox.Receive()
    match msg with
    | AddSession (session, reply) ->
      let newState = addSession session state
      reply.Reply(newState)
      return! loop newState
    | GetSession (id, reply) ->
      reply.Reply(Map.tryFind id state.Sessions)
      return! loop state
  }
  loop { Sessions = Map.empty; Config = defaultConfig })
```

### MCP Protocol Evolution

The current MCP implementation uses text-based tool responses. As MCP matures, consider:
- **Streaming responses** — partial eval results sent incrementally (e.g., type definitions echo before test results)
- **Resource subscriptions** — clients subscribe to eval events instead of polling `get_recent_fsi_events`
- **Prompts** — pre-built prompt templates for common workflows (TDD cycle, type exploration, refactoring)

### Risk Assessment

#### High Risk
- **Process lifecycle management** — daemon crashes, zombie sessions, resource leaks
  - Mitigation: Property-based tests for crash scenarios (`killing process always cleans up state`)
  - Watchdog with exponential backoff restart (Phase 3)
  - TDD: test both happy path and failure modes for every lifecycle transition

- **FSI session isolation** — types/state leaking between sessions in the same process
  - Mitigation: each session gets a completely independent `FsiEvaluationSession`
  - TDD: property test that evaluations in one session never affect another
  - Shadow copy separation per session

#### Medium Risk
- **Breaking changes** — existing single-session MCP clients
  - Mitigation: backward compatibility tests. Omitting `sessionId` routes to default session.
  - TDD: all existing MCP tool tests must pass unchanged after refactor

- **Performance overhead** — SessionManager routing, multi-session memory
  - Mitigation: performance tests with thresholds (`latency < 50ms overhead`)
  - Memory monitoring per session

#### Low Risk
- **Documentation** — users confused by daemon vs client
  - Mitigation: tests serve as documentation. `SageFs status` shows clear state.
  - CLI help text explains the smart default clearly

### Success Criteria (Daemon Refactor)

- All existing MCP tool tests pass unchanged (backward compatible)
- Daemon runs headless for 24+ hours without degradation (stability test)
- Create/stop 10+ sessions without memory leaks (property test + profiling)
- Multiple sessions (5+) running simultaneously with full isolation (load test)
- Code execution latency < 50ms overhead vs current single-session (performance test)
- Zero data loss when sessions crash (fault injection tests)
- `SageFs` smart default works: auto-starts daemon, connects REPL, no manual steps
- PrettyPrompt crash in client does NOT affect daemon or other sessions
- Every feature has tests BEFORE code is written

### Testing Strategy: TDD Is the Implementation Method

Nothing materializes until a failing test demands it. Not a type. Not a module. Not a function signature. The test comes first, fails (RED), then the simplest code that passes is written (GREEN), then cleaned up (REFACTOR). This is non-negotiable for every non-mechanical change.

#### Test Hierarchy (in order of preference)

1. **Property-based tests (FsCheck via Expecto)** — the default. Describe invariant behavioral outcomes. Let chaotic generated input find the edge cases you didn't think of. If a function transforms data, a property test describes *what must always be true* about that transformation, not a single example.

2. **Unit tests** — only when the mapping is deterministic and specific. A known input must produce a known output, and the property would just be restating the implementation. Example: `mapSeverity FSharpDiagnosticSeverity.Error` must equal `Error` — no randomness helps here.

3. **Snapshot tests (Verify)** — for "look and feel" outputs: rendered HTML, formatted MCP responses, serialized event shapes. The snapshot captures what the output *looks like* and alerts on any drift.

4. **Integration tests** — for full workflows that cross boundaries (MCP tool → actor → FSI session → response). These use `SharedSageFsFixture` with a real FSI session.

#### Self-Verification with SageFs Diagnostics

Before running any test, use SageFs's own `check_fsharp_code` MCP tool (or `getDiagnostics` directly) to verify the code compiles and type-checks. This is why the diagnostics tool is the very first implementation priority — it becomes the inner feedback loop for building everything else. The workflow is:

1. Write a failing test
2. Verify the test code itself compiles (`check_fsharp_code`)
3. Run the test — confirm RED
4. Write minimal implementation
5. Verify implementation compiles (`check_fsharp_code`)
6. Run the test — confirm GREEN
7. Refactor, re-verify, re-run

#### What Gets Tested

| Layer | Test Type | Example |
|-------|-----------|---------|
| Pure domain functions | Property-based | `assessValidity` always returns `Valid` when fingerprints match |
| Domain event DUs | Property-based | Round-trip serialization: `serialize >> deserialize = id` for all event variants |
| State machine transitions | Property-based | `availableTools` never includes `cancel_eval` when state ≠ `Evaluating` |
| MCP response formatting | Snapshot | `formatEvalResult` output matches approved snapshot |
| Actor command handling | Integration | `PostEval "let x = 42"` → eventually `EvalCompleted` event in stream |
| HTTP endpoints | Integration | `POST /eval` returns 202, `GET /diagnostics` SSE stream emits update |
| Projections | Property-based | `SessionProjection` fold is commutative with event ordering where applicable |
| SessionManager lifecycle | Property-based | Creating N sessions always results in N entries in list |
| Session isolation | Integration | Evaluating `type Foo` in session A, session B doesn't see `Foo` |
| Daemon discovery | Unit | `DaemonState.read` returns `None` for stale PID |
| MCP backward compat | Integration | All MCP tools work without `sessionId` (routed to default) |

#### Expecto Conventions

- Use `Expecto.Flip` — actual value piped in: `actual |> Expect.equal "message" expected`
- Run tests with `dotnet run --project SageFs.Tests` (NOT `dotnet test`). **Why:** Expecto tests are a console application with its own `[<EntryPoint>]` that provides richer output, filtering (`--filter`), and integration-test lifecycle management (Testcontainers, FSI session setup). `dotnet test` via `Expecto.TestAdapter` works for discovery but loses Expecto's custom CLI args and output formatting. In development, prefer running via SageFs's own FSI session (`sagefs-send_fsharp_code`) for fastest feedback.
- Tests live in `SageFs.Tests/` alongside the code they test, named `{Feature}Tests.fs`

#### SessionManager Test Stubs (Daemon Refactor)

These test shapes drive the SessionManager implementation (RED → GREEN → REFACTOR):

```fsharp
module SessionManagerTests

open Expecto

let addSessionTests = testList "addSession" [
  testCase "adds session to empty state" <| fun () ->
    let session = { Id = "session-abc"; Projects = ["Test.fsproj"]; WorkingDirectory = "/test"; SolutionRoot = None; CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow; State = WarmingUp }
    let newState = addSession session Map.empty
    Map.count newState |> Expect.equal "should have 1 session" 1
    Map.tryFind session.Id newState |> Expect.equal "should find session" (Some session)
]

let concurrencyTests = testList "concurrent access" [
  testCase "handles concurrent create requests" <| fun () ->
    // Start 10 sessions concurrently via MailboxProcessor
    let tasks =
      [1..10]
      |> List.map (fun i ->
        agent.PostAndAsyncReply(fun reply ->
          CreateSession ({ Projects = [sprintf "Proj%d.fsproj" i]; WorkingDir = sprintf "/test%d" i }, reply)))
    let results = tasks |> Async.Parallel |> Async.RunSynchronously
    results |> Array.forall Result.isOk |> Expect.isTrue "all should succeed"
    let sessions = agent.PostAndReply(ListSessions)
    List.length sessions |> Expect.equal "should have 10 sessions" 10
]

let isolationTests = testList "session isolation" [
  testCase "types in one session don't leak to another" <| fun () ->
    // Create session A, eval "type Foo = { X: int }"
    // Create session B, eval "let f (x: Foo) = x" → should fail (Foo not defined)
    ()
]

let daemonStateTests = testList "DaemonState" [
  testCase "read returns None for non-existent file" <| fun () ->
    DaemonState.read () |> Expect.isNone "should be None"

  testCase "read returns None when PID is stale" <| fun () ->
    DaemonState.write { Pid = 999999; Port = 37749; StartedAt = DateTime.UtcNow }
    DaemonState.read () |> Expect.isNone "stale PID should return None"

  testCase "write then read round-trips" <| fun () ->
    let info = { Pid = currentPid; Port = 37749; StartedAt = DateTime.UtcNow }
    DaemonState.write info
    DaemonState.read () |> Expect.isSome "should find daemon"
]
```

#### Open Questions (Daemon Refactor)

1. **Session timeout policy?** — Proposal: no timeout by default (sessions persist until explicitly killed). Configurable via `--idle-timeout` flag.
2. **Maximum sessions per daemon?** — Proposal: 10 sessions (configurable). Each FSI session consumes ~50-100MB of memory.
3. **Version mismatch?** — When a client connects to a daemon running a different SageFs version. Proposal: version check on connection, warn if mismatch, refuse if major version differs.

---

## Appendix: Expert Review

*The following reviews simulate the perspectives of Mark Seemann (blog.ploeh.dk) and Scott Wlaschin (fsharpforfunandprofit.com), based on their published writings, talks, and known values. These are stylistic approximations, not endorsements.*

### Review by Mark Seemann (blog.ploeh.dk)

#### Overall Impression

This is an ambitious plan with genuine architectural thinking behind it, and — critically — most of the core architecture is *already implemented and tested*. The affordance-driven state machine, the CQRS actor split, the event sourcing with Marten, 11 MCP tools, diagnostics, eval cancellation, structured output — these aren't aspirational; they're production code with integration tests. That changes the nature of this review from "should you build this?" to "how should the next layer be built on top of what works?"

#### Section-by-Section

**Vision & Affordance-Driven MCP (§1.0b)**

The LARP-inspired affordance model is genuinely interesting — filtering tools by session state is a sound application of the [Principle of Least Surprise](https://blog.ploeh.dk/2015/08/03/idiomatic-or-idiosyncratic/). The state machine is small, well-defined, and the affordance table makes the invariants explicit. Good.

However, the `Evaluating of CancellationTokenSource` case concerns me. You're embedding a *capability* (the CTS) inside a *state representation*. The `SessionState` DU should be pure data — serializable, testable, comparable. A CTS is none of those things. The state machine should be `Evaluating` (with no payload), and the CTS should live *beside* the state in whatever container manages it. This is the [Functional Core, Imperative Shell](https://blog.ploeh.dk/2020/03/02/impureim-sandwich/) pattern — keep the state pure, keep the I/O concerns at the boundary.

**Recommendation:** `SessionState` should be `Evaluating` with no payload. The CTS lives in the actor's mutable context alongside the state, not inside it.

**HTTP Surface & CQRS (§Datastar)**

The CQRS architecture — EvalActor for mutations, QueryActor for reads via immutable `QuerySnapshot` — is already implemented and tested. The actor split tests prove that autocomplete and diagnostics respond during long-running evaluations. This is a genuine benefit: concurrent query access without lock contention on the FSI session.

Where I'd apply scrutiny is the *HTTP surface design*. The plan proposes `POST /exec → 202 → GET /eval SSE` for every interaction. This is appropriate for IDE plugins that maintain persistent SSE subscriptions (Neovim plugin subscribes once at startup, receives all state changes reactively). But for MCP tool invocations, the agent already gets a synchronous response — the MCP protocol handles this naturally.

**Observation:** The architecture correctly separates concerns: MCP tools are request-response (agent submits code, gets result). SSE streams are for *push notifications* to persistent subscribers (IDE plugins, browser UI). This is the right split. The danger is adding SSE ceremony to paths that don't need it. The plan already handles this — the `POST → 202 → SSE` pattern is for the HTTP surface, not for MCP tools.

**Recommendation:** Keep the CQRS actor split — it's proven. For the HTTP surface, ensure the `POST → 202 → SSE` pattern is the *plugin path*, and that the MCP path remains direct request-response. The plan already distinguishes these; make that distinction more prominent so future contributors don't accidentally add SSE complexity to MCP tool handlers.

**Event Sourcing with Marten (§0.0)**

The event sourcing infrastructure is *already built and tested*. Marten + PostgreSQL with Testcontainers, 16+ domain event types, append/fetch/query operations, integration tests with schema isolation — this isn't a speculative choice, it's a deployed foundation.

I've [written extensively](https://blog.ploeh.dk/2023/06/05/from-state-to-state/) about event sourcing as an architectural pattern versus a technology choice. Here, the technology choice is *justified by the use cases*: session replay, multi-agent history (MCP agents, terminal REPL, and IDE plugins all producing events in the same stream), cross-process visibility for the daemon architecture, and epistemic validity (knowing when results become stale). These are genuine requirements that an in-memory event list or a `.jsonl` file can't serve.

Docker as a prerequisite is a trade-off worth documenting. Testcontainers auto-starts Postgres with container reuse and a named volume — the developer experience is "run SageFs, it works." The plan correctly provides `SageFs_CONNECTION_STRING` as an override for CI and production environments.

**Recommendation:** The Marten coupling is well-placed for the EventStore module. Where the [Dependency Inversion Principle](https://blog.ploeh.dk/2017/02/02/dependency-injection-is-loose-coupling/) applies is at the *domain boundary*: pure domain functions (`availableTools`, `assessValidity`, event fold projections) should never import Marten — they operate on F# DUs. The EventStore module is the adapter. This boundary is already largely respected; formalize it as a project convention. A future SQLite backend is possible if Docker-free scenarios become important, but it's not a prerequisite — the current architecture serves the actual user base (developers with Docker).

**BARE Wire Encoding (§3.6)**

An entire section analyzing binary encoding for a tool whose primary consumer is an LLM that reads text. The analysis is thorough — I can tell someone enjoyed writing it — but it's solving a problem that doesn't exist yet. You acknowledge this: "Priority: P2 — implement after Neovim plugin SSE integration works with JSON." But the section is ~250 lines of detailed specification analysis for something you might never need.

This is [YAGNI](https://blog.ploeh.dk/2021/09/27/the-equivalence-of-hard-coding/) elevated to architecture. The Neovim plugin sends maybe a few KB/second of diagnostics. JSON is fine. If performance becomes a problem, profile first, then optimize.

**Recommendation:** Reduce this section to a 3-sentence note: "BARE is a candidate binary encoding for machine-to-machine paths if JSON SSE performance becomes a bottleneck. F# DUs map naturally to BARE unions. See the BARE spec for details." Delete the byte-level encoding examples, the Lua decoder implementation, the comparison table. None of this should exist until there's a measured performance problem.

**Daemon Architecture (§3.2)**

The separation of daemon and client is the right call. PrettyPrompt crashing a background service is an unforced error and fixing it is the correct priority.

The decision to use **sub-process sessions** with a transport-agnostic protocol is excellent. This is the Erlang supervision model applied to .NET: each session is an isolated worker process, crashes are contained, and the daemon can restart workers independently. The `WorkerMessage` DU as a pure protocol type (serialization-agnostic) is exactly right — it keeps the domain model pure while allowing transport flexibility (named pipes today, HTTP tomorrow, without changing the SessionManager logic).

Named pipes as the default transport is a pragmatic choice for Windows. The key insight — location transparency through a `SessionProxy` abstraction — means the same interface works for local named pipes, remote HTTP, or even in-process testing. This follows the [Dependency Inversion Principle](https://blog.ploeh.dk/2017/02/02/dependency-injection-is-loose-coupling/) at the architecture level: depend on the abstraction (`SessionProxy`), not the concrete transport.

**Recommendation:** Define the `WorkerMessage` and `WorkerResponse` DUs in a shared module that both daemon and worker reference. Keep serialization out of the protocol types — JSON for debugging, BARE later if needed. The transport abstraction should be a simple function signature: `WorkerMessage -> Async<WorkerResponse>`, not an interface.

**Epistemic Validity (§environment fingerprints)**

This is the most intellectually interesting section and also the one most at risk of never being built. The `EnvironmentFingerprint` + `assessValidity` pure function is well-designed. The insight that events are immutable facts with derived epistemic status is precisely right.

But the section spends ~200 lines on a P2 feature that depends on multiple unbuilt prerequisites (file watching, assembly change detection, Marten projections). In a plan that already has 25 items across 5 phases, this level of detail for a mid-priority feature creates the illusion of progress without actual progress. The design is beautiful. Ship something first.

**Recommendation:** Keep the `EnvironmentFingerprint` type definition and the `assessValidity` function (they're compact and clarify the concept). Remove the prose explaining why speculative branching was rejected — that's a solved decision, not a living design question. Reduce to ~50 lines.

**Testing Strategy**

The hierarchy (property-based → unit → snapshot → integration) is sound. The emphasis on property-based tests as the default is correct and too rare in practice.

One concern: the plan says "Run tests with `dotnet run --project SageFs.Tests` (NOT `dotnet test`)." This is unusual and the plan doesn't explain why. If Expecto is the test framework, `dotnet test` should work via `Expecto.TestAdapter`. If there's a reason it doesn't (e.g., SageFs needs to start infrastructure), that should be documented. Otherwise, you're creating a surprise for every contributor who expects `dotnet test` to work.

**Recommendation:** Document why `dotnet test` isn't used. If it's a fixable limitation, fix it. The standard tooling convention is valuable.

#### Top-Level Concerns

1. **The plan is comprehensive.** At ~2300 lines, this is a reference document as much as a plan. The Version 1.0 Scope section (added based on this feedback) clarifies what's next. Sections like BARE encoding (now trimmed to ~10 lines) and epistemic validity are future reference material, correctly prioritized as P2+.

2. **Priority Matrix is now focused.** Completed items have been moved to a "Completed" section. The "Current Priorities" table shows only unfinished work, with daemon mode (3.2) as the clear next item — all its dependencies are done.

3. **Critical path is clear.** Daemon extraction (3.2) has no blocking dependencies (0.0 ✅, 2.0 ✅). It is the next thing to implement. The Recommended Execution Order spells this out as Phase 2.

---

### Review by Scott Wlaschin (fsharpforfunandprofit.com)

#### Overall Impression

This is clearly written by someone who loves F# and understands functional design. The domain modeling is strong — the event DUs are well-structured, the state machine is explicit, and the emphasis on pure functions is genuine, not cargo-culted. I particularly like the affordance table and the `availableTools : SessionState -> ToolName list` pure function. That's exactly the right level of abstraction.

The CQRS actor split is *working code* — the EvalActor serializes FSI mutations while the QueryActor serves immutable snapshots concurrently, and integration tests prove it. The event sourcing infrastructure (16 event types, Marten + Testcontainers, append/fetch/query) is also shipped and tested. These aren't aspirational patterns; they're proven foundations that simplify everything that comes next (daemon mode, multi-session, IDE plugins). My review focuses on where the *domain modeling* can be strengthened, not on questioning the architecture.

#### Section-by-Section

**Domain Modeling — What's Missing**

The plan defines `SageFsEvent` with ~20 cases, `SessionState` with 5 states, and `EventSource` with 4 cases. These are good types. But where is the *domain* type that represents a session?

Currently, a "session" is an emergent concept — it's whatever state the MailboxProcessor happens to hold. There's no `Session` type. There's no `SessionCommand` (there's an `EvalCommand` and `QueryCommand` internal to the actor). The plan introduces `SessionInfo` for the daemon, but it's a metadata record, not a domain aggregate.

In [Domain Modeling Made Functional](https://fsharpforfunandprofit.com/ddd/), I argue that the core types should make illegal states unrepresentable. What are the illegal states for an SageFs session?

- A session can't be `Evaluating` and `Faulted` simultaneously
- A session can't receive evals while `WarmingUp`
- A session can't have diagnostics without having been initialized

The `SessionState` DU handles some of these. But consider: what does it mean for a session to have `State = Ready` but `Projects = []`? Is that valid? What about `State = Evaluating` but `LastEvalCode = None`? The plan doesn't constrain these. The types should.

**Recommendation:** Define a proper `Session` aggregate type that makes invalid combinations impossible:

```fsharp
type Session =
  | Initializing of InitializingSession
  | WarmingUp of WarmingUpSession
  | Ready of ReadySession
  | Evaluating of EvaluatingSession
  | Faulted of FaultedSession

and ReadySession = {
  Id: SessionId
  Projects: NonEmptyList<ProjectInfo>
  EvalHistory: EvalResult list
  Diagnostics: DiagnosticsState
}

and EvaluatingSession = {
  Session: ReadySession
  CurrentEval: EvalRequest
  StartedAt: DateTime
}
```

This way, you can't have an `EvaluatingSession` without a `CurrentEval`. You can't have a `ReadySession` without projects. The types enforce the domain rules.

**Railway-Oriented Error Handling**

The plan mentions `Result<'T,'E>` for operations that can fail, but the actual error types are mostly `string`. The event DUs use `Error: string` in `SessionFaulted`, `EvalFailed`, etc. This throws away information.

In [Railway Oriented Programming](https://fsharpforfunandprofit.com/rop/), I recommend domain-specific error types:

```fsharp
type SessionError =
  | ProjectNotFound of path: string
  | CompilationFailed of diagnostics: Diagnostic list
  | EvalTimeout of code: string * elapsed: TimeSpan
  | FsiCrashed of message: string * stackTrace: string option
  | DaemonNotRunning
  | SessionNotFound of id: SessionId
  | PortInUse of port: int
```

This lets you pattern match on errors, handle specific cases differently, and give better error messages. `Result<Session, SessionError>` is infinitely more useful than `Result<Session, string>`.

**Recommendation:** Replace all `string` error types with domain-specific error DUs. Every function that returns `Result` should use a typed error.

**The Onion Architecture / Ports and Adapters**

The plan has good instincts about separation (daemon vs client, eval actor vs query actor), and the boundaries are already partially clean. `SageFs\Mcp.fs` contains `McpContext` which holds the actor, event store, and session ID. This is a *composition root wiring record* — it aggregates dependencies that the MCP tool functions need. This is fine as long as the domain logic inside the tool handlers stays thin (dispatch to actor, format result).

Where this can improve is formalizing the layer separation:

- **Core domain** (`Affordances.fs`, `Events.fs`, `Diagnostics.fs`): `SessionState`, `SageFsEvent`, `availableTools`, `assessValidity`. Already pure F#, no infrastructure imports. ✅
- **Application layer** (`AppState.fs`, `ActorCreation.fs`): Actor management, session lifecycle. Depends on domain types and FSI session. ✅
- **Infrastructure** (`McpServer.fs`, `McpTools.fs`, `PostgresInfra.fs`, `EventStore.fs`): Marten store, MCP tools, HTTP endpoints, PrettyPrompt. ✅

The architecture is already layered in practice. The improvement would be making this *explicit* — either via project structure or documented convention — so the daemon refactor maintains the separation.

**Recommendation:** Document the layer boundaries as a project convention. The key rule: nothing in `Affordances.fs`, `Events.fs`, or pure domain modules should ever import `Marten`, `ModelContextProtocol`, or `Falco`. The `McpContext` record is the composition root's wiring — keep it in the infrastructure layer where it belongs.

**Workflow Patterns**

The CQRS actor split is already working — EvalActor for mutations, QueryActor for concurrent reads. This is proven by integration tests that show autocomplete responding during long evals. The architecture correctly uses two interaction patterns:

1. **MCP tools (request-response):** Agent sends `send_fsharp_code`, gets back the result directly. No SSE complexity.
2. **HTTP surface (POST → 202 → SSE):** IDE plugins subscribe once to SSE streams, receive all state changes reactively. This is the natural fit for Neovim/VSCode plugins that need live diagnostics, status, and eval results.

This split is the right design. The `POST → 202 → SSE` pattern *simplifies* the IDE plugin because the plugin doesn't need to poll — it subscribes to `/status` SSE, `/diagnostics` SSE, and `/eval` SSE at startup, and all updates flow automatically. Whether the change came from the plugin, an MCP agent, or the terminal REPL, all subscribers see the same state. That's a genuine architectural win.

Consider making the core workflows more compositional where the logic permits:

```fsharp
let evalWorkflow =
  validateCode
  >> Result.bind checkAffordances
  >> Result.bind submitToFsi
  >> Result.map emitEvents
  >> Result.mapError formatError
```

This makes the workflow a *pipeline of transformations* rather than an imperative sequence. Each step is independently testable. The composition is the wiring.

**Recommendation:** Express core workflows as composed pipelines where possible. Keep the `task { }` CE at the boundary for I/O, but the *logic* should be pure pipeline composition.

**The Big Picture**

The plan describes a *platform* built from a solid core. The core — event-sourced REPL with CQRS actors, affordance-driven MCP, 11 tools, diagnostics, cancellation, structured output — is already shipped and tested. That's not scope creep; that's a foundation.

The remaining scope is genuinely ambitious:
- Daemon mode (extract headless server from REPL)
- Multi-session (SessionManager with multiple FSI actors)
- Neovim + VSCode plugins
- Watchdog supervisor
- Browser UI, notebook support, BARE encoding

The plan wisely phases these across 5 implementation phases. The risk isn't that any individual feature is too complex — it's that the plan carries too many features at equal weight. The Version 1.0 Scope section (daemon + single session + MCP) is the right cut.

**Recommendation:** Lean into the phasing. Each phase should be independently shippable and useful. The plan already does this — Phase 2 (daemon extraction) is useful without Phase 3 (Neovim plugin). Make this explicit: each phase is a version bump with release notes, not just a milestone.

---

### Synthesis: Actionable Refinements

Based on both reviews, these concrete changes should be applied:

1. **Extract CTS from SessionState DU** — make `Evaluating` carry no payload, store CTS in actor context ✅ *applied*
2. **Add `SessionTransition` DU** — explicit, total state machine transitions ✅ *applied*
3. **Define domain-specific error types** — replace `string` errors with `SessionError` DU ✅ *applied*
4. **Add missing state transitions** — `WarmingUp → Faulted`, document `HardReset` from any state ✅ *applied*
5. **Sub-process sessions with location-transparent messaging** — replaces in-process sessions. True fault isolation via OS process boundaries. Transport-agnostic protocol (`WorkerMessage` DU). ✅ *applied*
6. **Simplify the BARE section** — reduce ~250 lines to ~20 lines (a note, not a spec) ✅ *applied*
7. **Formalize layer boundaries** — document that `Affordances.fs`, `Events.fs`, and domain modules must never import Marten, MCP, or Falco. Keep `McpContext` in infrastructure.
8. **Make SSE vs request-response distinction prominent** — MCP tools stay request-response, HTTP surface uses `POST → 202 → SSE` for IDE plugin integration ✅ *applied*
9. **Define "1.0 scope"** — daemon + single session + MCP + simple HTTP ✅ *applied*
10. **Trim completed P0 items** — move to a "Completed" section, keep Priority Matrix focused on current work ✅ *applied*
11. **Document why not `dotnet test`** — documented: Expecto CLI app with custom filtering/lifecycle ✅ *applied*
12. **Evolve Session aggregate type** — start with flat `SessionInfo`, evolve to state-per-case DU when domain demands it ✅ *applied*
13. **Express core workflows as composed pipelines** — `validateCode >> checkAffordances >> submitToFsi >> emitEvents`
14. **Phase each release** — each phase is a version bump, independently shippable and useful
