# SageFs — VSCode Extension

Send F# code to [SageFs](../Readme.md) for instant evaluation, with inline results, session management, and auto-start.

## Features

- **Alt+Enter** — Evaluate the current selection (or code block) in SageFs
- **Alt+Shift+Enter** — Evaluate the entire file
- **Status bar** — Shows SageFs session status, click to open dashboard
- **Auto-start** — Detects `.fsproj` files and offers to start SageFs
- **Inline results** — Evaluation results appear as inline decorations
- **Session management** — Reset, hard reset, open dashboard from command palette

## Requirements

- [SageFs](../Readme.md) installed as a .NET global tool
- An F# project (`.fsproj`) in your workspace

## Getting Started

1. Install SageFs (see [main README](../Readme.md#installation))
2. Open an F# project in VS Code
3. The extension will offer to start SageFs if it's not running
4. Press **Alt+Enter** on any F# code to evaluate it

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `sagefs.mcpPort` | 37749 | SageFs MCP server port |
| `sagefs.dashboardPort` | 37750 | SageFs dashboard port |
| `sagefs.autoStart` | true | Auto-start SageFs when opening F# projects |
| `sagefs.projectPath` | "" | Explicit .fsproj path (auto-detect if empty) |

## Commands

| Command | Keybinding | Description |
|---------|-----------|-------------|
| SageFs: Evaluate Selection / Line | Alt+Enter | Evaluate code in SageFs |
| SageFs: Evaluate Entire File | Alt+Shift+Enter | Evaluate full file |
| SageFs: Start Daemon | — | Start the SageFs daemon |
| SageFs: Stop Daemon | — | Stop the SageFs daemon |
| SageFs: Open Dashboard | — | Open web dashboard |
| SageFs: Reset Session | — | Soft reset FSI session |
| SageFs: Hard Reset (Rebuild) | — | Rebuild and reload session |

## Development

```bash
cd sagefs-vscode
npm install
npm run compile
```

Press **F5** in VS Code to launch the Extension Development Host.
