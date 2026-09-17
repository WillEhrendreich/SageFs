# MCP Tools Reference

SageFs runs a Model Context Protocol server on port 37749. Any MCP client — GitHub Copilot, Claude Code, Claude Desktop, Cursor, Windsurf, OpenCode — can connect and drive an F# session: run code, type-check it, list and verify tests, and read live status.

The tool surface is **affordance-gated**. An agent only sees the tools that are valid for the current session state, so it never has to guess which call will work. In a warming-up session, for example, `send_fsharp_code` is not offered yet. Call `get_fsi_status` to see what is available right now.

The full advertised set is about 50 tools, grouped below. This is separate from the daemon's HTTP API (`/api/...`), which the editors and dashboard use for completions, coverage bitmaps, run policies, and event history. Those HTTP endpoints are not MCP tools.

## Connect

**Streamable HTTP** (recommended — it auto-reconnects and doesn't drop sessions):
```json
{ "mcpServers": { "sagefs": { "type": "streamable-http", "url": "http://localhost:37749/" } } }
```

**SSE** (for clients that don't support Streamable HTTP yet):
```json
{ "mcpServers": { "sagefs": { "type": "sse", "url": "http://localhost:37749/sse" } } }
```

## Execution and status

| Tool | What it does |
|:---|:---|
| `send_fsharp_code` | Evaluate F# code in the session. Each `;;` is a transaction boundary: a failure discards that one statement and keeps everything before it. |
| `check_fsharp_code` | Type-check a snippet without running it, in the current FSI context. Earlier `send_fsharp_code` definitions are in scope, but namespaces still need an explicit `open` — a "not defined" error usually means a missing `open`, not a real bug. |
| `cancel_eval` | Cancel a running evaluation. |
| `get_fsi_status` | Session health, loaded projects, and the tools available in the current state. |
| `get_recent_fsi_events` | Recent evals, errors, and loads with timestamps. |

## Sessions and lifecycle

| Tool | What it does |
|:---|:---|
| `create_session` | Create an isolated FSI session for a project or working directory. |
| `list_sessions` | List active sessions. |
| `switch_session` | Change which session your calls route to. |
| `stop_session` | Stop a session by id. |
| `reset_fsi_session` | Soft reset — clear definitions, keep loaded DLLs. |
| `hard_reset_fsi_session` | Full reset — rebuild the project, reload, start fresh. Needed after `.fsproj` or package changes. |
| `get_available_projects` | Discover `.fsproj` / `.sln` / `.slnx` files under a directory. |
| `list_runnable_projects` | List the session's projects and which ones `run_app` can run (`OutputType=Exe`). |
| `switch_workflow` | Switch the session between REPL and Live (WebLive) workflows. |

## Hot reload and running apps

| Tool | What it does |
|:---|:---|
| `enable_hot_reload` | Turn on file watching and hot reload for the session. |
| `disable_hot_reload` | Turn it off. |
| `run_app` | Run the session's executable project the way `dotnet run` would, with hot reload. Applies `launchSettings.json` (first "Project" profile) and picks a free loopback port when the project sets no URL. Restarts an Interactive session into WebLive first, so REPL bindings are lost. Saving source then hot-patches the running app. |
| `stop_app` | Stop the app started by `run_app`. Its web host stops and frees its port; the session keeps running. |

## Testing and verification

| Tool | What it does |
|:---|:---|
| `list_tests` | List discovered tests, grouped by file with source locations. Optional pattern or file filter. |
| `targeted_verify` | Plan a trustworthy verification pass for one changed behavior. Refuses to claim green when session trust is ambiguous or loaded code is stale. It does not run tests itself — it returns the next trustworthy move. |
| `explain_test_failure` | Enriched failure context for a test that recently went from passing to failing. |

There is no `run_tests` MCP tool. Test runs are driven by the live-testing engine (save a file, or use the editor/dashboard run controls); agents read results through `list_tests`, `explain_test_failure`, and `diagnose`.

## Analysis and diagnostics

| Tool | What it does |
|:---|:---|
| `diagnose` | Full diagnostic report: test failures, cell staleness, ripple plan, suggestions. |
| `coverage_intel` | Coverage-quality analysis — blind spots, correlated failures. |
| `impact_forecast` | Forecast the performance impact and downstream blast radius of cells. |
| `suggest_next_action` | Prioritized "what next" queue combining coverage, impact, and staleness. |
| `suggest_next_cell` | Type-directed suggestions for what to evaluate next, from the bindings in scope. |
| `suggest_repair` | Given a failing test, trace causal changes and suggest the symbol to fix. |
| `plan_ripple` | Plan cascade re-evaluation for changed cells using the live dependency graph. |
| `preview_what_if` | Preview what would change if a binding had a different value, without executing. |
| `decompose_pipeline` | Break an F# pipeline into stages, each classified pure / effectful / unknown. |
| `get_cell_dependencies` | The cell dependency graph with staleness annotations. |
| `discover_features` | Context-aware feature discovery, ranked by relevance to the session state. |

## Export and history

| Tool | What it does |
|:---|:---|
| `export_notebook` | Export the session as a notebook-style `.fsx` with cell metadata. |
| `export_session_transcript` | Export the session as a clean, topologically sorted `.fsx` transcript. |
| `get_session_filmstrip` | Visual history of evaluations — each a frame with code, bindings, and duration. |
| `get_eval_timeline` | Eval-duration sparkline and P50/P95/P99 statistics. |
| `get_eval_diff` | Before/after diff of recent evaluation outputs. |
| `get_message_journal` | Audit log of eval events, filterable by severity and source. |
| `manage_scratch_pad` | View, export, or promote ephemeral snippets from session history. |

## Friction telemetry (local only)

These read and write a local log. They do not phone home.

| Tool | What it does |
|:---|:---|
| `get_friction_summary` | Compact summary of recorded MCP friction. |
| `get_friction_report` | Structured JSON report of MCP pain points. |
| `report_friction` | Record structured feedback about a confusing tool call. |

## Cohort and multi-agent coordination

For running several agents against one repo at once. One implicit cohort per daemon; the first agent to join becomes its conductor. Every tool resolves the caller's identity from the MCP connection, not from the `agentName` argument.

| Tool | What it does |
|:---|:---|
| `join_cohort` | Join the daemon's shared coordination session. The first joiner becomes conductor. |
| `leave_cohort` | Leave. Any claims you still hold are orphaned (the conductor must reassign them). |
| `get_cohort_status` | Members, claims and fences, the test matrix, and the landing queue. Wait-free — reads a published snapshot. Also available on the `cohort://status` MCP resource for subscription. |
| `acquire_claim` | Take an exclusive claim over a file or project (`file:<path>` or `project:<path>`) so others know it's yours to edit. |
| `release_claim` | Release a claim you hold. The presented fence must match the current one. |
| `reassign_claim` | Conductor-only: reassign an orphaned claim to a present member. |
| `request_landing` | Queue a landing: your commits are rebased onto the integration head, verified against affected tests, and fast-forwarded in. Landings are strictly serial (one FIFO queue). |
| `set_integration_ref` | Conductor-only: configure the git ref that landings rebase onto, in a dedicated integration worktree. |

## Per-client config

The `url` is `http://localhost:37749/` for Streamable HTTP, or `http://localhost:37749/sse` for SSE.

**GitHub Copilot (CLI)** — `~/.copilot/github-copilot/mcp.json`:
```json
{ "servers": { "sagefs": { "type": "http", "url": "http://localhost:37749/" } } }
```

**Claude Code / Claude Desktop** — `~/.claude/claude_desktop_config.json`:
```json
{ "mcpServers": { "sagefs": { "url": "http://localhost:37749/" } } }
```

**Cursor / Windsurf** — `.cursor/mcp.json` or the Windsurf MCP settings:
```json
{ "mcpServers": { "sagefs": { "url": "http://localhost:37749/" } } }
```

**OpenCode** — `~/.opencode.json`:
```json
{ "mcp": { "sagefs": { "type": "remote", "url": "http://localhost:37749/sse", "enabled": true } } }
```

Works with GitHub Copilot (CLI and VS Code), Claude Code, Claude Desktop, OpenCode, Windsurf, Cursor, and any MCP-compatible tool. With live testing on, agents can edit files, let SageFs re-run the affected tests, and read the result through `list_tests` and `diagnose` — no eval round-trip needed.
