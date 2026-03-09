# SageFs — Visual Studio Extension

A Visual Studio extension for [SageFs](../Readme.md) — the live F# development server. Evaluate F# code, see inline results, stream diagnostics into the Error List, get completions, manage sessions, control hot reload, and monitor live test status — all from within Visual Studio.

## Requirements

- Visual Studio 2022 17.14+ (the extension uses the [new out-of-process Extensibility SDK](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/))
- .NET SDK 10.0+
- SageFs CLI: `dotnet tool install --global SageFs`
- Windows only (amd64 or arm64)

## Quick Start

1. Install the VSIX (build from source or download from Releases)
2. Install the SageFs CLI:
   ```bash
   dotnet tool install --global SageFs
   ```
3. Open a `.fsx` or `.fs` file — if the daemon isn't running, a notification will appear in the **SageFs Output** channel
4. Start the daemon: **Extensions → SageFs → Start Daemon**
5. Completions, gutter markers, and TypeExplorer activate automatically

## Installing from Source

```bash
cd sagefs-vs/SageFs.VisualStudio
dotnet build
```

Then load the extension in Visual Studio's experimental instance, or install the generated VSIX.

## Features

| Feature | Status |
|---------|--------|
| Code evaluation (selection, file, block) | ✅ |
| Inline eval adornments | ✅ |
| CodeLens ("▶ Eval" per function/type/module) | ✅ |
| Error/warning squiggles (Error List integration) | ✅ |
| F# completions (working_directory context, 14 kind mappings) | ✅ |
| Gutter test status markers (populated on startup) | ✅ |
| TypeExplorer (auto-refreshes on session warmup) | ✅ |
| Live test status panel with Run Policy picker | ✅ |
| Daemon health check on startup (Output channel notification) | ✅ |
| Session management (create, switch, reset, hard reset) | ✅ |
| Hot reload file watching | ✅ |
| Coverage gutter signs | ❌ Not yet implemented |
| Themes | ❌ VS SDK doesn't expose color contribution API |
| Call graph viewer | ❌ Available in VS Code and Neovim |
| History browser | ❌ Available in VS Code and Neovim |

### Code Evaluation

| Command | Keybinding | Description |
|---------|-----------|-------------|
| SageFs: Evaluate Selection | `Alt+Enter` | Evaluate selected text, or the block around the cursor if nothing is selected |
| SageFs: Evaluate File | `Shift+Alt+Enter` | Evaluate the entire file |
| SageFs: Evaluate Code Block | *(no keybinding)* | Evaluate the block around the cursor (accessible via Extensions menu) |

Results appear inline as adornments and in the **SageFs Output** window.

### CodeLens

"▶ Eval" buttons appear above every F# function, type, and module. Click to evaluate. Live test CodeLens shows test status when live testing is enabled.

### Completions

F# completions are powered by SageFs's FSI session. The extension passes `working_directory` context and maps all 14 completion kind variants to their native VS equivalents. Completions have a 3-second timeout to keep the IDE responsive.

### Error List Integration

SageFs diagnostics (type errors, warnings) stream into the native VS Error List via SSE — real-time feedback as you code.

### Gutter Test Status Markers

Pass/fail/stale icons appear in the editor margin next to each test. Markers are seeded from daemon state on extension load (`InitialStatePoll`) so the gutter is populated immediately, even before the next test run.

### Session Management

| Command | Description |
|---------|-------------|
| SageFs: Create Session | Create a new isolated FSI session |
| SageFs: Configure Warmup Auto-Open | Create or open `.SageFs/config.fsx` and disable warmup namespace auto-open |
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

On extension load, a startup health check writes the daemon status to the **SageFs Output** channel so you know immediately whether the daemon is reachable.

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
| SageFs: Set Run Policy | Configure which test categories auto-run |
| SageFs: Live Testing Dashboard | Open a tool window with test summary and results |
| SageFs: Show Recent Events | Display recent pipeline events |

The **Set Run Policy** command opens an interactive picker. Select a category and policy, then click **Apply** to update the daemon.

### Tool Windows

- **Session Context** — Connection status, loaded assemblies, opened namespaces, warmup details
- **Hot Reload Files** — Project files with watch status
- **Live Testing** — Test summary, toggle, run-all, per-category run policy, and results text
- **Type Explorer** — Browse .NET types and namespaces; auto-refreshes when a new FSI session warms up

## Architecture

The extension uses a two-layer architecture:

- **`SageFs.VisualStudio`** (C#) — Thin shim using the VS Extensibility SDK. Defines commands, CodeLens providers, tool windows, and menu items. Targets `net8.0-windows8.0`.
- **`SageFs.VisualStudio.Core`** (F#) — All HTTP client logic, daemon management, SSE subscriptions, and domain types. This is where the real work happens.

Communication with SageFs uses the same HTTP + SSE protocol as all other frontends. The daemon runs on port 37749 (MCP) and 37750 (dashboard/API) by default.

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
