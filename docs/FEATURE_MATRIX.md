# SageFs Feature Matrix

Cross-editor comparison of SageFs features across all six frontends. Use this to understand what's
available in your editor and discover features you might not know about.

> **Legend**: ✅ Supported | 🔜 Planned | ❌ Not applicable | ➖ N/A for this editor

---

## Core Evaluation

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|
| Evaluate selection (`Alt+Enter`) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Evaluate current cell | ✅ | ✅ | ✅ | ✅ | ✅ |
| Evaluate file | ✅ | ✅ | ✅ | ✅ | ✅ |
| Load .fsx script | ✅ | ✅ | ✅ | ✅ | ✅ |
| Inline results | ✅ | ✅ | ✅ | ✅ | ✅ |
| Eval history / timeline | ✅ | ✅ | 🔜 | ✅ | ✅ |
| Cancel running eval | ✅ | ✅ | ✅ | ✅ | ✅ |

## Session Management

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|
| Create session | ✅ | ✅ | ✅ | ✅ | ✅ |
| Switch session | ✅ | ✅ | ✅ | ✅ | ✅ |
| Soft reset (clear definitions) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Hard reset (rebuild + reload) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Export session as .fsx | ✅ | ✅ | 🔜 | ✅ | ✅ |
| Session picker (multi-session) | ✅ | ✅ | ✅ | ✅ | ✅ |

## Live Testing

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|
| Auto-discover tests | ✅ | ✅ | ✅ | ✅ | ✅ |
| Run affected tests on save | ✅ | ✅ | ✅ | ✅ | ✅ |
| Test gutter markers (pass/fail) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Test results panel | ✅ | ✅ | ✅ | ✅ | ✅ |
| Failure narratives | ✅ | ✅ | ✅ | ✅ | ✅ |
| Causal change tracking | ✅ | ✅ | ✅ | ✅ | ✅ |
| Run policy configuration | ✅ | ✅ | 🔜 | ✅ | ✅ |
| Property-based test detail | ✅ | ✅ | ✅ | ✅ | ✅ |
| Coverage gutter signs | ✅ | ✅ | 🔜 | ✅ | ✅ |

## Code Intelligence

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|
| Type Explorer tree | ✅ | ✅ | ✅ | ✅ | ¹ |
| Namespace exploration | ✅ | ✅ | ✅ | ✅ | ✅ |
| Code completions | ✅ | ✅ | 🔜 | ✅ | ✅ |
| Dependency graph | ✅ | ✅ | 🔜 | ✅ | ✅ |
| Test coverage query | ✅ | ✅ | ✅ | ✅ | ✅ |

## Hot Reload & File Watching

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|
| Auto-reload on .fs save | ✅ | ✅ | ✅ | ✅ | ✅ |
| Hot reload status indicator | ✅ | ✅ | ✅ | ✅ | ✅ |
| DevReload browser refresh | ✅ | ✅ | ✅ | ✅ | ✅ |

## Diagnostics & Health

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|
| Health check command | ✅ | ✅ | ✅ | ✅ | ✅ |
| Daemon status in status bar | ✅ | ✅ | ✅ | ✅ | ✅ |
| Session count in status bar | ✅ | ✅ | ✅ | ✅ | ✅ |
| Actionable error notifications | ✅ | ✅ | ✅ | ✅ | ✅ |
| Output channel / logs | ✅ | ✅ | ✅ | ✅ | ✅ |
| OpenTelemetry export | ✅ | ✅ | ✅ | ✅ | ✅ |
| Warmup progress display | ✅ | ✅ | ✅ | ✅ | ✅ |
| Typed error display (`SageFsError`) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Eval watchdog (crash detection) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Version gate (API compat check) | ✅ | ➖ | 🔜 | ➖ | ➖ |
| Daemon stderr capture | ✅ | ➖ | ✅ | ➖ | ➖ |

## Onboarding

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|
| Getting Started walkthrough | ✅ | ➖ | 🔜 | ➖ | ➖ |
| Welcome message (first run) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Empty-state guidance | ✅ | ✅ | ✅ | ✅ | ✅ |
| Snippets | ✅ | 🔜 | ❌ | ❌ | ❌ |

## Editor Integration

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|
| CodeLens (test results above functions) | ✅ | ❌ | ✅ | ❌ | ❌ |
| Inline failure annotations | ✅ | ✅ | ✅ | ✅ | ✅ |
| Telescope/fuzzy finder integration | ❌ | ✅ | ❌ | ❌ | ❌ |
| Statusline component | ✅ | ✅ | ✅ | ✅ | ✅ |
| Syntax highlighting (Tree-sitter) | ❌ | ✅ | ❌ | ✅ | ✅ |
| Time-travel (history snapshots) | ❌ | ❌ | ❌ | ✅ | ✅ |
| Failing test navigation | ✅ | ✅ | ❌ | ✅ | ✅ |
| Mouse support (click/drag/scroll) | ❌ | ✅ | ❌ | ✅ | ✅ |
| Text selection + copy | ❌ | ✅ | ❌ | ❌ | ✅ |
| Layout presets | ❌ | ❌ | ❌ | ✅ | ✅ |
| Display density modes | ❌ | ❌ | ❌ | ✅ | ✅ |
| Font size zoom | ❌ | ❌ | ❌ | ❌ | ✅ |

> ¹ Completion popup available; dedicated Type Explorer pane not yet implemented.

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

## Architecture Notes

| Client | Rendering Engine | Terminal Setup | Frame Loop |
|:-------|:----------------|:---------------|:-----------|
| **TUI** (default) | SageTUI Elm Architecture (`Program<Model,Msg>`) | SageTUI handles alt screen, raw mode, mouse | `App.run` with SIMD cell diff |
| **TUI** (`--legacy-tui`) | Imperative `CellGrid` → `AnsiEmitter` | `TerminalMode.setupRawMode()` | Manual `CellGrid.rent` → `Screen.drawWith` → `Console.Write` |
| **Raylib GUI** | `Cell[,]` grid → `RaylibEmitter` draw calls | N/A (GPU window) | Raylib frame loop with `BeginDrawing`/`EndDrawing` |
| **Web Dashboard** | Falco.Datastar SSE → HTML | N/A (browser) | Server-push via SSE |

All clients share the same daemon SSE subscription for real-time state updates,
the same `KeyMap` for keybinding configuration, and the same `ThemeConfig` for
color theming.
