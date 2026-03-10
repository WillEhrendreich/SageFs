# SageFs Feature Matrix

Cross-editor comparison of SageFs features. Use this to understand what's
available in your editor and discover features you might not know about.

> **Legend**: ✅ Supported | 🔜 Planned | ❌ Not applicable | ➖ N/A for this editor

---

## Core Evaluation

| Feature | VS Code | Neovim | Visual Studio | TUI |
|:--------|:-------:|:------:|:-------------:|:---:|
| Evaluate selection (`Alt+Enter`) | ✅ | ✅ | ✅ | ✅ |
| Evaluate current cell | ✅ | ✅ | ✅ | ✅ |
| Evaluate file | ✅ | ✅ | ✅ | ✅ |
| Load .fsx script | ✅ | ✅ | ✅ | ✅ |
| Inline results | ✅ | ✅ | ✅ | ✅ |
| Eval history / timeline | ✅ | ✅ | 🔜 | ✅ |
| Cancel running eval | ✅ | ✅ | ✅ | ✅ |

## Session Management

| Feature | VS Code | Neovim | Visual Studio | TUI |
|:--------|:-------:|:------:|:-------------:|:---:|
| Create session | ✅ | ✅ | ✅ | ✅ |
| Switch session | ✅ | ✅ | ✅ | ✅ |
| Soft reset (clear definitions) | ✅ | ✅ | ✅ | ✅ |
| Hard reset (rebuild + reload) | ✅ | ✅ | ✅ | ✅ |
| Export session as .fsx | ✅ | ✅ | 🔜 | ✅ |
| Session picker (multi-session) | ✅ | ✅ | ✅ | ✅ |

## Live Testing

| Feature | VS Code | Neovim | Visual Studio | TUI |
|:--------|:-------:|:------:|:-------------:|:---:|
| Auto-discover tests | ✅ | ✅ | ✅ | ✅ |
| Run affected tests on save | ✅ | ✅ | ✅ | ✅ |
| Test gutter markers (pass/fail) | ✅ | ✅ | ✅ | ✅ |
| Test results panel | ✅ | ✅ | ✅ | ✅ |
| Failure narratives | ✅ | ✅ | ✅ | ✅ |
| Causal change tracking | ✅ | ✅ | ✅ | ✅ |
| Run policy configuration | ✅ | ✅ | 🔜 | ✅ |
| Property-based test detail | ✅ | ✅ | ✅ | ✅ |
| Coverage gutter signs | ✅ | ✅ | 🔜 | ➖ |

## Code Intelligence

| Feature | VS Code | Neovim | Visual Studio | TUI |
|:--------|:-------:|:------:|:-------------:|:---:|
| Type Explorer tree | ✅ | ✅ | ✅ | ✅ |
| Namespace exploration | ✅ | ✅ | ✅ | ✅ |
| Code completions | ✅ | ✅ | 🔜 | ✅ |
| Dependency graph | ✅ | ✅ | 🔜 | ✅ |
| Test coverage query | ✅ | ✅ | ✅ | ✅ |

## Hot Reload & File Watching

| Feature | VS Code | Neovim | Visual Studio | TUI |
|:--------|:-------:|:------:|:-------------:|:---:|
| Auto-reload on .fs save | ✅ | ✅ | ✅ | ✅ |
| Hot reload status indicator | ✅ | ✅ | ✅ | ✅ |
| DevReload browser refresh | ✅ | ✅ | ✅ | ✅ |

## Diagnostics & Health

| Feature | VS Code | Neovim | Visual Studio | TUI |
|:--------|:-------:|:------:|:-------------:|:---:|
| Health check command | ✅ | ✅ | ✅ | ✅ |
| Daemon status in status bar | ✅ | ✅ | ✅ | ✅ |
| Session count in status bar | ✅ | ✅ | ✅ | ✅ |
| Actionable error notifications | ✅ | ✅ | ✅ | ✅ |
| Output channel / logs | ✅ | ✅ | ✅ | ✅ |
| OpenTelemetry export | ✅ | ✅ | ✅ | ✅ |

## Onboarding

| Feature | VS Code | Neovim | Visual Studio | TUI |
|:--------|:-------:|:------:|:-------------:|:---:|
| Getting Started walkthrough | ✅ | ➖ | 🔜 | ➖ |
| Welcome message (first run) | ✅ | ✅ | ✅ | ✅ |
| Empty-state guidance | ✅ | ✅ | ✅ | ✅ |
| Snippets | ✅ | 🔜 | ❌ | ❌ |

## Editor Integration

| Feature | VS Code | Neovim | Visual Studio | TUI |
|:--------|:-------:|:------:|:-------------:|:---:|
| CodeLens (test results above functions) | ✅ | ❌ | ✅ | ❌ |
| Inline failure annotations | ✅ | ✅ | ✅ | ✅ |
| Telescope/fuzzy finder integration | ❌ | ✅ | ❌ | ❌ |
| Statusline component | ✅ | ✅ | ✅ | ✅ |
| Syntax highlighting (Tree-sitter) | ❌ | ✅ | ❌ | ✅ |

---

## Keybindings

| Action | VS Code | Neovim |
|:-------|:--------|:-------|
| Evaluate selection/cell | `Alt+Enter` | `<leader>se` |
| Evaluate file | `Ctrl+Alt+Enter` | `<leader>sf` |
| Cancel eval | `Ctrl+Alt+C` | `<leader>sc` |
| Reset session | Command palette | `:SageFsResetSession` |
| Hard reset | Command palette | `:SageFsHardReset` |
| Switch project | Command palette | `:SageFsSwitchProject` |
| Toggle live testing | Command palette | `:SageFsToggleLiveTesting` |
| Open dashboard | Command palette | `:SageFsDashboard` |

---

## MCP Tools (Available in All Editors via MCP)

All 33+ MCP tools are available regardless of editor choice:

- `send_fsharp_code` — Evaluate F# in the REPL
- `check_fsharp_code` — Type-check without executing
- `run_tests` — Run tests by pattern/category
- `get_live_test_status` — Current test results
- `explain_test_failure` — Why did this test break?
- `hard_reset_fsi_session` — Rebuild and reload
- `explore_namespace` / `explore_type` — Browse .NET APIs
- `get_completions` — Code completion at cursor
- `query_test_coverage` — Which tests cover this symbol?
- `get_file_coverage` — Line-level coverage data
- `visualize_domain_model` — DU as state machine diagram
- ...and more (see MCP tool documentation)
