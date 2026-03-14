# SageFs Feature Matrix

Cross-editor comparison of SageFs features across all seven frontends plus AI agents.
Use this to understand what's available in your editor and discover features you might not know about.

> **Legend**: ✅ Supported | ⚠️ Partial | ❌ Not applicable | ➖ N/A for this client type
>
> All features are also accessible via **49 MCP tools** for AI agents and programmatic clients.

---

## Core Evaluation

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI | Web | AI (MCP) |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|:---:|:--------:|
| Evaluate selection (`Alt+Enter`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Evaluate current cell | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Evaluate file | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Load .fsx script | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Inline results | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Eval history / timeline | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Cancel running eval | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

## Session Management

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI | Web | AI (MCP) |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|:---:|:--------:|
| Create session | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Switch session | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Soft reset (clear definitions) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Hard reset (rebuild + reload) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Export session as .fsx | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Session picker (multi-session) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

## Live Testing

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI | Web | AI (MCP) |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|:---:|:--------:|
| Auto-discover tests | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Run affected tests on save | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Test gutter markers (pass/fail) | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ |
| Test results panel | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ |
| Failure narratives | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Causal change tracking | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Run policy configuration | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Property-based test detail | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Coverage gutter signs | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ |
| Test timeout configuration | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Explain why a test ran | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |

## Code Intelligence

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI | Web | AI (MCP) |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|:---:|:--------:|
| Type Explorer tree | ✅ | ✅ | ✅ | ✅ | ⚠️¹ | ➖ | ✅ |
| Namespace exploration | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Code completions | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Dependency graph | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Test coverage query | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Domain model visualization | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Pipeline decomposition | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |

## Analysis & Diagnostics (AI-First)

These tools are designed primarily for AI agents and programmatic clients via MCP. Editor UIs surface the *results* of these analyses through gutter markers, panels, and status bars — but the full analysis tools are MCP-native.

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI | Web | AI (MCP) |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|:---:|:--------:|
| Full diagnostic report (`diagnose`) | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Coverage intelligence | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Impact forecast (regression risk) | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Suggested next action (priority queue) | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Suggest repair for failing test | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| What-if preview | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Ripple plan (cascade re-eval) | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Type-directed next-cell suggestions | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Feature discovery (context-aware) | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |

## Export & Session History

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI | Web | AI (MCP) |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|:---:|:--------:|
| Export as notebook (.fsx) | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ✅ |
| Export session transcript | ✅ | ✅ | ➖ | ✅ | ✅ | ➖ | ✅ |
| Session filmstrip (eval history) | ➖ | ➖ | ➖ | ✅ | ✅ | ➖ | ✅ |
| Eval timeline (performance stats) | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Eval diff (before/after) | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Message journal (audit log) | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| Scratch pad management | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |

## Hot Reload & File Watching

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI | Web | AI (MCP) |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|:---:|:--------:|
| Auto-reload on .fs save | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| Hot reload status indicator | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| DevReload browser refresh | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| Per-file/directory toggle | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |

## Diagnostics & Health

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI | Web | AI (MCP) |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|:---:|:--------:|
| Health check command | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Environment pre-flight (`sagefs check`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Daemon status in status bar | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| Session count in status bar | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| Actionable error notifications | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Output channel / logs | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| OpenTelemetry export | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Warmup progress display | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Typed error display (`SageFsError`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Eval watchdog (crash detection) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Version gate (API compat check) | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| Daemon stderr capture | ✅ | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |

## Onboarding

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI | Web | AI (MCP) |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|:---:|:--------:|
| Getting Started walkthrough | ✅ | ➖ | 🔜 | ➖ | ➖ | ➖ | ➖ |
| Welcome message (first run) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| Empty-state guidance | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| Snippets | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |

## Editor Integration

| Feature | VS Code | Neovim | Visual Studio | TUI | Raylib GUI | Web | AI (MCP) |
|:--------|:-------:|:------:|:-------------:|:---:|:----------:|:---:|:--------:|
| CodeLens (test results above functions) | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Inline failure annotations | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ |
| Telescope/fuzzy finder integration | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Statusline component | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ |
| Syntax highlighting (Tree-sitter) | ❌ | ✅ | ❌ | ✅ | ✅ | ❌ | ❌ |
| Time-travel (history snapshots) | ❌ | ❌ | ❌ | ✅ | ✅ | ❌ | ❌ |
| Failing test navigation | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ➖ |
| Mouse support (click/drag/scroll) | ❌ | ✅ | ❌ | ✅ | ✅ | ✅ | ❌ |
| Text selection + copy | ❌ | ✅ | ❌ | ❌ | ✅ | ✅ | ❌ |
| Layout presets | ❌ | ❌ | ❌ | ✅ | ✅ | ❌ | ❌ |
| Display density modes | ❌ | ❌ | ❌ | ✅ | ✅ | ❌ | ❌ |
| Font size zoom | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |

> ¹ Completion popup available; dedicated Type Explorer pane not yet implemented in Raylib GUI.

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

## MCP Tools (Available to All Clients)

All 49 MCP tools are accessible from any editor or AI agent connected to the daemon.
The tools fall into these categories:

**Code Evaluation (10):** `send_fsharp_code`, `check_fsharp_code`, `load_fsharp_script`, `cancel_eval`, `get_completions`, `get_recent_fsi_events`, `get_fsi_status`, `get_startup_info`, `get_available_projects`, `get_elm_state`

**Session Management (6):** `create_session`, `list_sessions`, `switch_session`, `stop_session`, `reset_fsi_session`, `hard_reset_fsi_session`

**Code Intelligence (4):** `explore_namespace`, `explore_type`, `visualize_domain_model`, `decompose_pipeline`

**Live Testing (12):** `enable_live_testing`, `disable_live_testing`, `run_tests`, `get_live_test_status`, `set_run_policy`, `set_test_timeouts`, `get_test_trace`, `list_tests`, `explain_test_run`, `explain_test_failure`, `query_test_coverage`, `get_file_coverage`

**Analysis & Diagnostics (9):** `diagnose`, `coverage_intel`, `impact_forecast`, `suggest_next_action`, `suggest_repair`, `suggest_next_cell`, `plan_ripple`, `preview_what_if`, `discover_features`

**Export & History (8):** `export_notebook`, `export_session_transcript`, `get_session_filmstrip`, `get_eval_timeline`, `get_eval_diff`, `get_message_journal`, `manage_scratch_pad`, `get_cell_dependencies`

---

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
