# SageFs Feature Matrix

Current product surfaces are the web dashboard, editor integrations, and MCP. The built-in SageTUI client, legacy TUI, and `SageFs.Gui` Raylib frontend are deprecated and are intentionally excluded from this current-support matrix.

Raylib application and game projects remain supported. The demos in `samples/demos/` prove SageFs can provide live development for Raylib projects; they are separate from the deprecated SageFs GUI frontend.

> **Legend**: Supported = dedicated client experience | Shared = available through the daemon or MCP | Partial = client support is incomplete | N/A = not meaningful for that client

## Core Evaluation

| Feature | VS Code | Neovim | Visual Studio | Web Dashboard | MCP |
|:--------|:-------:|:------:|:-------------:|:-------------:|:---:|
| Evaluate code, blocks, or files | Supported | Supported | Supported | Supported | Supported |
| Display evaluation results | Supported | Supported | Supported | Supported | Supported |
| Cancel a running evaluation | Supported | Supported | Supported | Supported | Supported |
| Evaluation history | Supported | Supported | Partial | Supported | Supported |
| Session-scoped diagnostics | Supported | Supported | Supported | Supported | Supported |

## Session Management

| Feature | VS Code | Neovim | Visual Studio | Web Dashboard | MCP |
|:--------|:-------:|:------:|:-------------:|:-------------:|:---:|
| Create and switch sessions | Supported | Supported | Supported | Supported | Supported |
| Soft reset | Supported | Supported | Supported | Supported | Supported |
| Hard reset and rebuild | Supported | Supported | Supported | Supported | Supported |
| Multi-session selection | Supported | Supported | Supported | Supported | Supported |
| Per-client active session | Supported | Supported | Supported | Supported | Supported |

## Live Testing

| Feature | VS Code | Neovim | Visual Studio | Web Dashboard | MCP |
|:--------|:-------:|:------:|:-------------:|:-------------:|:---:|
| Test discovery and execution | Supported | Supported | Supported | Supported | Supported |
| Run affected tests on save | Supported | Supported | Supported | Shared | Shared |
| Test result panel | Supported | Supported | Supported | Supported | Supported |
| Test gutter markers | Supported | Supported | Supported | N/A | N/A |
| Coverage gutters | Supported | Supported | Supported | N/A | N/A |
| Failure narratives | Supported | Supported | Supported | Supported | Supported |
| Test policy and timeout controls | Supported | Supported | Partial | Supported | Supported |
| Explain test failures and causal changes | Shared | Shared | Shared | Supported | Supported |

## Code Intelligence

| Feature | VS Code | Neovim | Visual Studio | Web Dashboard | MCP |
|:--------|:-------:|:------:|:-------------:|:-------------:|:---:|
| Completions | Supported | Supported | Supported | N/A | Supported |
| CodeLens | Supported | Supported | Supported | N/A | N/A |
| Type and namespace exploration | Supported | Supported | Partial | Partial | Supported |
| Dependency and coverage queries | Shared | Shared | Shared | Supported | Supported |
| Domain model and pipeline analysis | Shared | Shared | Shared | Partial | Supported |

## Hot Reload and Health

| Feature | VS Code | Neovim | Visual Studio | Web Dashboard | MCP |
|:--------|:-------:|:------:|:-------------:|:-------------:|:---:|
| File watching and reload state | Supported | Supported | Supported | Supported | Supported |
| Browser refresh status | Supported | Supported | Supported | Supported | Supported |
| Health and connection state | Supported | Supported | Supported | Supported | Supported |
| Warmup progress | Supported | Supported | Supported | Supported | Supported |
| Typed errors and recovery guidance | Supported | Supported | Supported | Supported | Supported |

## Client Roles

| Client | Primary Role | Transport |
|:-------|:-------------|:----------|
| **VS Code** | Full editor workflow, inline results, testing, coverage, and navigation | HTTP commands + SSE |
| **Neovim** | Full editor workflow, inline results, testing, coverage, and navigation | HTTP commands + SSE |
| **Visual Studio** | Native editor workflow, diagnostics, testing, and project integration | HTTP commands + SSE |
| **Web Dashboard** | Browser-based session operations, output, test state, diagnostics, and observability | Falco.Datastar + SSE |
| **MCP** | Agent and programmatic access to FSI, sessions, tests, and diagnostics | Streamable HTTP; legacy SSE where required |

## Common Editor Commands

| Action | VS Code | Neovim |
|:-------|:--------|:-------|
| Evaluate selection/cell | `Alt+Enter` | `<leader>se` |
| Evaluate file | `Ctrl+Alt+Enter` | `<leader>sf` |
| Cancel evaluation | `Ctrl+Alt+C` | `<leader>sc` |
| Reset session | Command palette | `:SageFsResetSession` |
| Hard reset | Command palette | `:SageFsHardReset` |
| Switch project | Command palette | `:SageFsSwitchProject` |
| Toggle live testing | Command palette | `:SageFsToggleLiveTesting` |
| Open dashboard | Command palette | `:SageFsDashboard` |

## MCP

MCP is the structured automation surface for session-aware F# evaluation, test discovery and execution, failure explanation, targeted verification, and diagnostics. The set of tools shown to a client depends on session state, so consult the MCP tools exposed by the running daemon rather than relying on a fixed count.
