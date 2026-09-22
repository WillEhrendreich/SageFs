# SageFs Feature Matrix

Current product surfaces are the web dashboard, editor integrations (VS Code and Neovim), and MCP. The
built-in SageTUI client, legacy TUI, `SageFs.Gui` Raylib frontend, and the Visual Studio extension are
deprecated and excluded from this matrix. I'm not going to keep grading a frontend I've already stopped
building.

Raylib application and game projects remain supported. The demos in `samples/demos/` show SageFs providing
live development for Raylib projects, separate from the deprecated SageFs GUI frontend.

> **Legend**: Supported = dedicated client experience | Shared = available through the daemon or MCP | Partial = client support is incomplete | N/A = not meaningful for that client

## Core Evaluation

| Feature | VS Code | Neovim | Web Dashboard | MCP |
|:--------|:-------:|:------:|:-------------:|:---:|
| Evaluate code, blocks, or files | Supported | Supported | Supported | Supported |
| Display evaluation results | Supported | Supported | Supported | Supported |
| Cancel a running evaluation | Supported | Supported | Supported | Supported |
| Evaluation history | Supported | Supported | Supported | Supported |
| Session-scoped diagnostics | Supported | Supported | Supported | Supported |

## Session Management

| Feature | VS Code | Neovim | Web Dashboard | MCP |
|:--------|:-------:|:------:|:-------------:|:---:|
| Create and switch sessions | Supported | Supported | Supported | Supported |
| Soft reset | Supported | Supported | Supported | Supported |
| Hard reset and rebuild | Supported | Supported | Supported | Supported |
| Multi-session selection | Supported | Supported | Supported | Supported |
| Per-client active session | Supported | Supported | Supported | Supported |

## Live Testing

Live testing is the capability I've put the most proof behind: a CI integration test boots a real daemon and a
real session, edits a source file on disk, and asserts the affected test flips to failing without anyone asking
for a rerun. That's not a claim in a README, it's a test that runs on every push. Rough edges remain around
session switching and test discovery timing. I'm not going to pretend those are solved yet. Expecto has the
best coverage; xUnit (including v3), NUnit, MSTest, and TUnit are also detected.

Live testing is also a workflow, not only a toggle. See [Workflow Modes](workflow-modes.md).

| Feature | VS Code | Neovim | Web Dashboard | MCP |
|:--------|:-------:|:------:|:-------------:|:---:|
| Test discovery and execution | Supported | Supported | Supported | Supported |
| Run affected tests on save | Supported | Supported | Shared | Shared |
| Test result panel | Supported | Supported | Supported | N/A |
| Test gutter markers | Supported | Supported | N/A | N/A |
| Coverage gutters | Supported | Supported | N/A | N/A |
| Failure narratives | Supported | Supported | Supported | Supported |
| Explain test failures and causal changes | Shared | Shared | Supported | Supported |

## Code Intelligence

| Feature | VS Code | Neovim | Web Dashboard | MCP |
|:--------|:-------:|:------:|:-------------:|:---:|
| Completions | Supported | Supported | N/A | N/A |
| CodeLens | Supported | Supported | N/A | N/A |
| Dependency and coverage queries | Shared | Shared | Supported | Supported |
| Domain model and pipeline analysis | Shared | Shared | Partial | Supported |

Completions and CodeLens are editor features backed by FSharp.Compiler.Service; they are not MCP tools. Neither
is the type explorer, the call graph, the test-run policy control, or the test trace. Those are HTTP endpoints
the editors and the dashboard call (`GET /api/dependency-graph`, `POST /api/live-testing/policy`,
`GET /api/live-testing/test-trace`). The `get_completions` and `explore_type` members in `SageFs/McpTools.fs`
carry a `[<Description>]` but no `[<McpServerTool>]` attribute, so they aren't part of the advertised tool
surface at all. I checked, this table isn't guessing. [`LIVE_TESTING_GUIDE.md`](LIVE_TESTING_GUIDE.md) is the
authoritative list of what is and isn't an MCP tool.

## Hot Reload and Health

Hot reload watches `.fs` files, emits the functions that changed, and uses Harmony to re-point those methods in
the already-running process, including apps whose route table was built once at startup (the
`module App.Program` + `let routes = [...]` pattern). Browser auto-refresh works.

Because it re-points **methods**, a handler that is *called* per request reloads, and a handler whose output
was *computed once* at startup cannot. Prefer `let getHome (ctx: HttpContext) = ...` over
`let getHome : HttpHandler = Response.ofHtml (pageLayout [])`. `let mutable` state, changed signatures, and
changed types restart the app instead of patching it, and SageFs tells you that's what's happening rather than
silently serving stale code. [Hot Reload](hot-reload.md) carries the full what-reloads / what-restarts table,
each row pinned by an executable test; this page deliberately doesn't duplicate it.

| Feature | VS Code | Neovim | Web Dashboard | MCP |
|:--------|:-------:|:------:|:-------------:|:---:|
| File watching and reload state | Supported | Supported | Supported | Supported |
| Browser refresh status | Supported | Supported | Supported | Supported |
| Health and connection state | Supported | Supported | Supported | Supported |
| Warmup progress | Supported | Supported | Supported | Supported |
| Typed errors and recovery guidance | Supported | Supported | Supported | Supported |

## Client Roles

| Client | Primary Role | Transport |
|:-------|:-------------|:----------|
| **VS Code** | Full editor workflow: inline results, testing, coverage, and navigation | HTTP commands + SSE |
| **Neovim** | Full editor workflow: inline results, testing, coverage, and navigation | HTTP commands + SSE |
| **Web Dashboard** | Browser-based session operations, output, test state, diagnostics, and observability | Falco.Datastar + SSE |
| **MCP** | Agent and programmatic access to FSI, sessions, tests, and diagnostics | Streamable HTTP at `/`; legacy SSE at `/sse` |

## Common Editor Commands

| Action | VS Code | Neovim |
|:-------|:--------|:-------|
| Evaluate selection/cell | `Alt+Enter` | `<Alt-Enter>` (or `<leader>re`) |
| Evaluate file | `Alt+Shift+Enter` | `<leader>rf` |
| Cancel evaluation | `Ctrl+Shift+C` | `<leader>rx` |
| Reset session | Command palette | `:SageFsReset` |
| Hard reset | Command palette | `:SageFsHardReset` |
| Switch project | Command palette | `:SageFsSwitchProject` |
| Enable / disable live testing | Command palette | `:SageFsEnableTesting` / `:SageFsDisableTesting` |
| Switch workflow | Command palette | `:SageFsWorkflow live\|repl` |
| Open dashboard | Command palette | `:SageFsDashboard` |

> The Neovim column is a copy of the plugin's own keymaps, which live in the separate [`sagefs.nvim`](https://github.com/WillEhrendreich/sagefs.nvim#keymaps) repository. That README is authoritative, and nothing in this repo verifies it. This table used to list `<leader>se` / `<leader>sf` / `<leader>sc`, which the plugin doesn't bind: it puts everything under `<leader>r` precisely because LazyVim reserves `<leader>s` for Search. It also listed `:SageFsResetSession` and `:SageFsToggleLiveTesting`, neither of which exists. Both were my mistakes, now fixed.

## MCP

MCP gives programmatic access for session-aware F# evaluation, test discovery and execution, failure
explanation, targeted verification, and diagnostics. The daemon advertises all ~50 tools regardless of session
state; calling one that doesn't apply yet is rejected with a structured error instead of the tool being hidden.
Call `get_fsi_status` to see which tools currently apply.
