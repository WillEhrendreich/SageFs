# SageFs — VS Code Extension

A full-featured VS Code frontend for [SageFs](../Readme.md) — the live F# development server. Inline eval results, real-time diagnostics, live unit testing with pass/fail markers, hot reload controls, session management, and a built-in dashboard — all powered by the running SageFs daemon.

> **Note:** This extension is not yet published on the VS Marketplace. See [Installing](#installing) below.

## Features

### Code Evaluation
- **Alt+Enter** — Evaluate the current selection or `;;`-delimited code block. Results appear as inline decorations.
- **Alt+Shift+Enter** — Evaluate the entire file
- **CodeLens** — Clickable "▶ Eval" buttons above every `;;` block

### Live Unit Testing
- **Inline test decorations** — ✓/✗/● markers on test lines, updated in real-time via SSE
- **Native Test Explorer** — Tests appear in VS Code's built-in Test Explorer via a `TestController` adapter
- **Test result CodeLens** — "✓ Passed" / "✗ Failed" above every test function
- **Failure diagnostics** — Failed tests appear as native VS Code squiggles
- **Test policy controls** — Enable/disable live testing, run all tests, or configure run policies from the command palette
- **Call graph viewer** — Visualize test dependency graphs
- **Pipeline trace** — Browse three-speed pipeline events

### Live Diagnostics
- F# type errors and warnings stream in via SSE as you edit, appearing as native VS Code squiggles

### Hot Reload
- **Hot Reload sidebar** — Tree view in the activity bar showing all project files with per-file and per-directory watch toggles
- Toggle individual files, directories, or watch/unwatch everything at once

### Session Management
- **Session Context sidebar** — Loaded assemblies, opened namespaces, failed opens, warmup details
- **Multi-session** — Create, switch, and manage multiple sessions from the command palette

### More
- **Type Explorer sidebar** — Browse .NET types and namespaces interactively from the activity bar
- **Event history** — Browse recent pipeline events via QuickPick
- **Dashboard webview** — Open the SageFs dashboard directly inside VS Code
- **Status bar** — Active project, eval count, supervised status, restart count. Click to open dashboard.
- **Auto-start** — Detects `.fsproj`/`.sln`/`.slnx` files and offers to start SageFs automatically
- **Ionide integration** — Hijacks Ionide's `FSI: Send Selection` commands so Alt+Enter routes through SageFs
- **7 custom theme colors** — Inline result colors respect your VS Code theme

## Requirements

- [SageFs](../Readme.md) installed as a .NET global tool (`dotnet tool install --global SageFs`)
- An F# project (`.fsproj` or `.sln`) in your workspace

## Installing

> The SageFs VS Code extension is **not published on the VS Marketplace** yet. Install manually:

**Option A: Download from GitHub Releases (recommended)**

Each [GitHub Release](https://github.com/WillEhrendreich/SageFs/releases) includes a `.vsix` file:

```bash
code --install-extension sagefs-<version>.vsix
```

**Option B: Build from source**

```bash
cd sagefs-vscode
npm install
npm run compile
npx @vscode/vsce package
code --install-extension sagefs-*.vsix
```

## Getting Started

1. Install SageFs: `dotnet tool install --global SageFs`
2. Open an F# project in VS Code
3. The extension will offer to start SageFs if it's not running
4. Press **Alt+Enter** on any F# code to evaluate it
5. Open the command palette (`Ctrl+Shift+P`) and type "SageFs" to discover all commands

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `sagefs.mcpPort` | `37749` | SageFs MCP server port |
| `sagefs.dashboardPort` | `37750` | SageFs dashboard port |
| `sagefs.autoStart` | `true` | Automatically start SageFs when opening F# projects |
| `sagefs.projectPath` | `""` | Explicit `.fsproj` path (auto-detect if empty) |

## Commands

### Evaluation

| Command | Keybinding | Description |
|---------|-----------|-------------|
| SageFs: Evaluate Selection / Line | `Alt+Enter` | Evaluate selection or `;;` block |
| SageFs: Evaluate Entire File | `Alt+Shift+Enter` | Evaluate full file |
| SageFs: Evaluate Code Block | — | Evaluate the `;;`-delimited block at cursor |
| SageFs: Clear Inline Results | — | Remove all inline result decorations |

### Daemon & Session

| Command | Keybinding | Description |
|---------|-----------|-------------|
| SageFs: Start Daemon | — | Start the SageFs daemon |
| SageFs: Stop Daemon | — | Stop the SageFs daemon |
| SageFs: Open Dashboard | — | Open web dashboard in VS Code |
| SageFs: Create Session | — | Create a new FSI session |
| SageFs: Switch Session | — | Switch to a different session |
| SageFs: Stop Session | — | Stop the active session |
| SageFs: Reset Session | — | Soft reset (clear definitions) |
| SageFs: Hard Reset (Rebuild) | — | Full rebuild and reload |

### Hot Reload

| Command | Keybinding | Description |
|---------|-----------|-------------|
| SageFs: Toggle Hot Reload for File | — | Toggle file watching for current file |
| SageFs: Toggle Directory Hot Reload | — | Toggle watching for a directory |
| SageFs: Watch All Files | — | Enable watching for all project files |
| SageFs: Unwatch All Files | — | Disable all file watching |
| SageFs: Refresh Hot Reload | — | Refresh the hot reload file list |

### Live Testing

| Command | Keybinding | Description |
|---------|-----------|-------------|
| SageFs: Enable Live Testing | — | Turn on live test execution |
| SageFs: Disable Live Testing | — | Turn off live test execution |
| SageFs: Run All Tests | — | Execute all tests now |
| SageFs: Set Test Run Policy | — | Configure per-category run policies |
| SageFs: Show Test Call Graph | — | Visualize test dependency graph |
| SageFs: Show Recent Events | — | Browse pipeline event history |

### Sidebar Views

| View | Location | Description |
|------|----------|-------------|
| Hot Reload Files | Activity Bar | File tree with watch toggles |
| Session Context | Activity Bar | Assemblies, namespaces, warmup details |
| Refresh Session Context | Activity Bar | Refresh the session context panel |

## Troubleshooting

**"SageFs: offline" in status bar** — The daemon isn't running. Click the status bar or run "SageFs: Start Daemon" from the command palette.

**"No .fsproj or .sln found"** — Open a folder containing an F# project. The extension auto-detects projects; set `sagefs.projectPath` for non-standard layouts.

**Inline results not appearing** — Make sure the daemon is running (status bar shows ⚡). Try "SageFs: Clear Inline Results" then re-evaluate.

**Test decorations not showing** — Enable live testing via "SageFs: Enable Live Testing". Check the Output panel (select "SageFs") for errors.

## Architecture

This extension is written entirely in F# using [Fable](https://fable.io/) — no TypeScript. The F# source compiles to JavaScript, giving you type-safe extension code with the same language as your project.

## Development

```bash
cd sagefs-vscode
npm install
npm run compile
```

Press **F5** in VS Code to launch the Extension Development Host.
