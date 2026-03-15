# 🤖 MCP Tools Reference

SageFs exposes dozens of MCP tools — from `send_fsharp_code` to `run_tests` to `discover_features`. Any MCP client can connect.

## Connection

**Streamable HTTP** (recommended — auto-reconnects, no session drops):
```json
{ "mcpServers": { "sagefs": { "type": "streamable-http", "url": "http://localhost:37749/" } } }
```

**SSE** (legacy clients that don't support Streamable HTTP yet):
```json
{ "mcpServers": { "sagefs": { "type": "sse", "url": "http://localhost:37749/sse" } } }
```

## Full Tool List

| Tool | Description |
|:---|:---|
| `send_fsharp_code` | Execute F# code. Each `;;` is a transaction boundary. |
| `check_fsharp_code` | Type-check a snippet without executing. Code is checked in the current FSI session context — prior `send_fsharp_code` definitions are in scope, but namespaces must be explicitly opened. "Not defined" errors usually mean a missing `open`, not a real bug. |
| `get_completions` | Code completions at cursor position. |
| `cancel_eval` | Cancel a running evaluation. |
| `load_fsharp_script` | Load `.fsx` with partial progress. |
| `get_recent_fsi_events` | Recent evals, errors, loads with timestamps. |
| `get_fsi_status` | Session health, loaded projects, affordances. |
| `get_startup_info` | Projects, features, CLI arguments. |
| `get_available_projects` | Discover `.fsproj`/`.sln`/`.slnx` files. |
| `explore_namespace` | Browse types in a .NET namespace. |
| `explore_type` | Browse members of a .NET type. |
| `get_elm_state` | Current UI render state. |
| `reset_fsi_session` | Soft reset — clear definitions, keep DLLs. |
| `hard_reset_fsi_session` | Full reset — rebuild, reload, fresh session. |
| `create_session` | Create an isolated FSI session. |
| `list_sessions` | List all active sessions. |
| `stop_session` | Stop a session by ID. |
| `switch_session` | Switch active session. |
| `enable_live_testing` | Turn on live unit testing. |
| `disable_live_testing` | Turn off live unit testing. |
| `get_live_test_status` | Test state with optional file filter. |
| `run_tests` | Run tests by pattern or category. Waits up to 15s for hot reload to complete first. |
| `set_run_policy` | Auto-run policy per category (every/save/demand/disabled). |
| `set_test_timeouts` | Configure per-test and global run timeouts. |
| `get_test_trace` | Test cycle timing waterfall. |
| `explain_test_run` | Why a test was selected to run — trigger reason, changed symbols, flaky status. |
| `explain_test_failure` | Enriched failure context for a test that recently went Passed→Failed. |
| `query_test_coverage` | Which tests transitively cover a given symbol via the dependency graph. |
| `get_file_coverage` | Per-line coverage data for a file — bitmap + dependency graph synthesis. |
| `visualize_domain_model` | Visualize a discriminated union type as a state machine diagram. |
| `list_tests` | List all discovered tests, optionally filtered by pattern or file path. Returns grouped-by-file results with source locations. |
| `get_cell_dependencies` | Expose the cell dependency graph with staleness annotations. Shows which cells are stale and why. |
| `discover_features` | Context-aware feature discovery. Ranks available SageFs features by relevance to current session state. |
| **Analysis & Diagnostics** | |
| `diagnose` | Full diagnostic report combining test failures, cell staleness, ripple plan, suggestions, and performance. |
| `coverage_intel` | Analyze test coverage quality — find blind spots, correlate failures, assess diagnostic power. |
| `impact_forecast` | Forecast performance impact for cells — detect regressions, measure downstream blast radius. |
| `suggest_next_action` | Prioritized "what should I do next?" queue combining coverage, impact, and staleness data. |
| `suggest_repair` | Given a failing test, trace causal changes and suggest which symbol to fix. |
| `suggest_next_cell` | Type-directed suggestions for what to evaluate next based on current bindings in scope. |
| `plan_ripple` | Plan cascade re-evaluation for changed cells using the live dependency graph. |
| `preview_what_if` | Preview what would change if a binding had a different value — without executing. |
| `decompose_pipeline` | Decompose an F# pipeline into stages with purity classification (pure/effectful/unknown). |
| **Export & Session History** | |
| `export_notebook` | Export session as a notebook-style `.fsx` file with cell metadata. |
| `export_session_transcript` | Export session as a clean, topologically-sorted `.fsx` transcript. |
| `get_session_filmstrip` | Visual history of all evaluations — each as a "frame" with code, bindings, and duration. |
| `get_eval_timeline` | Performance sparkline and percentile statistics (P50/P95/P99) for eval durations. |
| `get_eval_diff` | Before/after diff comparison of recent evaluation outputs. |
| `get_message_journal` | Structured audit log of eval events, filterable by severity and source. |
| `manage_scratch_pad` | View, export, or promote ephemeral code snippets from the session history. |

## Per-Client Config Examples

**GitHub Copilot (CLI)** — `~/.copilot/github-copilot/mcp.json`:
```json
{ "servers": { "sagefs": { "type": "sse", "url": "http://localhost:37749/sse" } } }
```

**Claude Code** — `~/.claude/claude_desktop_config.json`:
```json
{ "mcpServers": { "sagefs": { "type": "sse", "url": "http://localhost:37749/sse" } } }
```

**Windsurf / Cursor** — `.cursor/mcp.json` or Windsurf MCP settings:
```json
{ "mcpServers": { "sagefs": { "type": "sse", "url": "http://localhost:37749/sse" } } }
```

**OpenCode** — `mcp.json`:
```json
{ "mcpServers": { "sagefs": { "url": "http://localhost:37749/sse" } } }
```

Works with GitHub Copilot (CLI & VS Code), Claude Code, Claude Desktop, OpenCode, Windsurf, Cursor, and any MCP-compatible tool. The **edit → auto-test → poll** workflow means agents don't even need to call eval — just edit files and check `get_live_test_status`.
