# SageFs — Visual Studio Extension

A Visual Studio extension for [SageFs](../Readme.md) — the live F# development server. Evaluate F# code, see inline results via CodeLens, stream diagnostics into the Error List, manage sessions, and control hot reload — all from within Visual Studio.

> **⚠️ Early Development.** Core evaluation, CodeLens, session management, and hot reload work. Live testing gutter markers, coverage, and several advanced features are not yet implemented. See [Current Limitations](#current-limitations) below.

## Requirements

- Visual Studio 2022 17.14+ (the extension uses the [new out-of-process Extensibility SDK](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/))
- [SageFs](../Readme.md) installed as a .NET global tool (`dotnet tool install --global SageFs`)
- An F# project in your solution

## Installing

The VS extension is **not published on the VS Marketplace** yet. Build and install from source:

```bash
cd sagefs-vs/SageFs.VisualStudio
dotnet build
```

Then load the extension in Visual Studio's experimental instance, or install the generated VSIX.

## Features

### Code Evaluation

| Command | Keybinding | Description |
|---------|-----------|-------------|
| SageFs: Evaluate Selection | `Alt+Enter` | Evaluate selected code |
| SageFs: Evaluate File | `Shift+Alt+Enter` | Evaluate the entire file |
| SageFs: Evaluate Code Block | `Ctrl+Alt+Enter` | Evaluate the `;;`-delimited block at cursor |

Results appear in the **SageFs Output** window.

### CodeLens

"▶ Eval" buttons appear above every F# function, type, and module. Click to evaluate. Live test CodeLens shows test status when live testing is enabled.

### Error List Integration

SageFs diagnostics (type errors, warnings) stream into the native VS Error List via SSE — real-time feedback as you code.

### Session Management

| Command | Description |
|---------|-------------|
| SageFs: Create Session | Create a new isolated FSI session |
| SageFs: Switch Session | Switch to a different session |
| SageFs: Stop Session | Stop the active session |
| SageFs: Reset Session | Soft reset (clear definitions, keep DLLs) |
| SageFs: Hard Reset | Full rebuild and reload |
| SageFs: Session Context | Show loaded assemblies, namespaces, warmup details |

### Daemon Lifecycle

| Command | Description |
|---------|-------------|
| SageFs: Start Daemon | Start the SageFs daemon (auto-detects project/solution) |
| SageFs: Stop Daemon | Stop the running daemon |
| SageFs: Open Dashboard | Open the web dashboard in your browser |

### Hot Reload

| Command | Description |
|---------|-------------|
| SageFs: Toggle Hot Reload for File | Toggle watching for the active file |
| SageFs: Toggle Hot Reload for Directory | Toggle watching for a directory |
| SageFs: Watch All Files | Enable watching for all project files |
| SageFs: Unwatch All Files | Disable all file watching |
| SageFs: Refresh Hot Reload | Refresh the hot reload file list |

### Live Testing

| Command | Description |
|---------|-------------|
| SageFs: Enable/Disable Live Testing | Toggle the live test pipeline |
| SageFs: Run All Tests | Execute all tests now |
| SageFs: Set Run Policy | Configure which test categories auto-run (currently sets all to "every change") |
| SageFs: Live Testing Dashboard | Open a tool window with test summary and results |
| SageFs: Show Recent Events | Display recent pipeline events |

### Tool Windows

- **Session Context** — Connection status, loaded assemblies, opened namespaces, warmup details
- **Hot Reload Files** — Project files with watch status
- **Live Testing** — Test summary, toggle, run-all, and results text
- **Type Explorer** — Browse .NET types and namespaces

## Architecture

The extension uses a two-layer architecture:

- **`SageFs.VisualStudio`** (C#) — Thin shim using the VS Extensibility SDK. Defines commands, CodeLens providers, tool windows, and menu items. Targets `net8.0-windows8.0`.
- **`SageFs.VisualStudio.Core`** (F#) — All HTTP client logic, daemon management, SSE subscriptions, and domain types. This is where the real work happens.

Communication with SageFs uses the same HTTP + SSE protocol as all other frontends. The daemon runs on port 37749 (MCP) and 37750 (dashboard/API) by default.

## Current Limitations

These features exist in VS Code and/or Neovim but are **not yet implemented** in the VS extension:

| Feature | Status | Notes |
|---------|--------|-------|
| Live test gutter markers | ❌ Not implemented | Server data ready, VS margin glyphs pending |
| Coverage gutter signs | ❌ Not implemented | Server data ready, VS margin glyphs pending |
| Test panel with per-test results | ⚠️ Basic | Shows text summary, no per-test navigation |
| Test policy picker | ⚠️ Hardcoded | Sets all categories to "every change", no interactive picker |
| Code completion | ❌ Not implemented | HTTP client ready, VS SDK doesn't expose completion API yet |
| Themes | ❌ Not implemented | VS SDK doesn't expose color contribution API yet |
| Inline result decorations | ❌ Not implemented | Results go to Output window, not inline adornments |
| Status dashboard | ❌ Not implemented | Use the web dashboard at `localhost:37750/dashboard` |
| Call graph viewer | ❌ Not implemented | Available via VS Code and Neovim |
| History browser | ❌ Not implemented | Available via VS Code and Neovim |

## Troubleshooting

**"SageFs not found on PATH"** — Install SageFs: `dotnet tool install --global SageFs`. Make sure the .NET tools directory is on your PATH.

**Commands do nothing / no feedback** — Check the SageFs Output window (`View → Output → SageFs`). The extension logs errors there. If the daemon isn't running, start it with "SageFs: Start Daemon".

**Error List not updating** — Verify the SSE connection is active. The extension subscribes to `/events` on the dashboard port (37750). Firewall or proxy issues can block this.

## Development

```bash
cd sagefs-vs/SageFs.VisualStudio
dotnet build
```

Use Visual Studio's experimental instance (F5) to test. The C# shim project references the F# core via project reference.
