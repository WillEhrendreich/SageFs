# SageFs Feature Matrix

Current product surfaces are the web dashboard, editor integrations (VS Code and Neovim), and MCP. The built-in SageTUI client, legacy TUI, `SageFs.Gui` Raylib frontend, and the Visual Studio extension are deprecated and excluded from this matrix.

Raylib application and game projects remain supported. The demos in `samples/demos/` show SageFs providing live development for Raylib projects, separate from the deprecated SageFs GUI frontend.

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

Live testing works but is still being stabilized, with rough edges around session switching and test discovery timing. Expecto has the best coverage; xUnit (including v3), NUnit, MSTest, and TUnit are also detected.

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

Completions and CodeLens are editor features backed by FSharp.Compiler.Service; they are not MCP tools.

## Hot Reload and Health

Hot reload watches `.fs` files and runs the full pipeline (watch, `#load`/FSI eval, Harmony patch, SSE refresh). Browser auto-refresh works. Propagating a change into a running module-declared app is still being completed.

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
| Evaluate selection/cell | `Alt+Enter` | `<leader>se` |
| Evaluate file | `Alt+Shift+Enter` | `<leader>sf` |
| Cancel evaluation | `Ctrl+Shift+C` | `<leader>sc` |
| Reset session | Command palette | `:SageFsResetSession` |
| Hard reset | Command palette | `:SageFsHardReset` |
| Switch project | Command palette | `:SageFsSwitchProject` |
| Toggle live testing | Command palette | `:SageFsToggleLiveTesting` |
| Open dashboard | Command palette | `:SageFsDashboard` |

## MCP

MCP gives programmatic access for session-aware F# evaluation, test discovery and execution, failure explanation, targeted verification, and diagnostics. The daemon advertises all ~50 tools regardless of session state; calling one that doesn't apply yet is rejected with a structured error instead of the tool being hidden. Call `get_fsi_status` to see which tools currently apply.
