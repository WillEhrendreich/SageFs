# SageFs — VS Code Extension

> **Save your F# file → tests run → results appear inline — all in < 500ms.**

SageFs brings live evaluation, instant test feedback, and coverage visualization to VS Code. No configuration needed — just press `Alt+Enter`.

## ✨ What You Get

| Feature | What it does |
|---------|-------------|
| **Inline Results** | Expression values appear next to your code as you evaluate |
| **Live Testing** | Tests run automatically on save — green ✓ / red ✗ in the gutter |
| **Coverage Gutters** | Colored bars show which lines are covered by tests |
| **Failure Details** | Inline `⊘` markers show Expected vs Actual diffs |
| **Failure Narratives** | Rich context: what changed, when it last passed, causal analysis |
| **Test Source Jump** | Test Explorer items link to their source location automatically |
| **Hot Reload** | Method-level patching — change code, see results without restart |
| **Eval Performance** | Status bar sparkline with P50/P95/P99 eval latencies |

> **Note:** This extension is not yet published on the VS Marketplace. See [Installing](#installing) below.

---

## 🚀 Quick Start

1. **Install SageFs CLI**: `dotnet tool install --global SageFs`
2. **Install this extension**: Download `.vsix` from [Releases](https://github.com/WillEhrendreich/SageFs/releases) → Extensions sidebar → `...` → Install from VSIX
3. **Open an F# project** in VS Code
4. **Press `Alt+Enter`** on any expression — the daemon starts automatically and results appear inline

That's it. Live testing enables automatically when Expecto tests are detected.

---

## ⌨️ Keybindings

| Action | Keybinding | Command Palette |
|--------|-----------|-----------------|
| Evaluate selection / `;;` block | `Alt+Enter` | SageFs: Evaluate Selection / Line |
| Evaluate entire file | `Alt+Shift+Enter` | SageFs: Evaluate Entire File |
| Evaluate & advance to next block | `Shift+Enter` | SageFs: Evaluate & Advance |
| Evaluate all `;;` blocks | `Ctrl+Alt+Enter` | SageFs: Evaluate All Blocks |
| Cancel running evaluation | `Ctrl+Shift+C` | SageFs: Cancel Evaluation |
| Next `;;` code block | `Ctrl+Down` | SageFs: Next Code Block |
| Previous `;;` code block | `Ctrl+Up` | SageFs: Previous Code Block |
| Clear inline results | — | SageFs: Clear Inline Results |
| Run all tests | — | SageFs: Run All Tests |
| Toggle live testing | — | SageFs: Enable / Disable Live Testing |
| Session picker | — | SageFs: Switch Session |
| Reset session | — | SageFs: Hard Reset (Rebuild) |
| Load current script | — | SageFs: Load Current Script |

> 💡 All keybindings are scoped to F# files only. `Shift+Enter` is deactivated when a notebook is focused to avoid conflicts with Jupyter.

---

## 🎨 Understanding What You See

### Gutter Icons

| Icon | Meaning |
|------|---------|
| ✓ (green) | All tests passing for this line |
| ✗ (red) | A test covering this line has failed |
| ⊘ (gray) | Test skipped or disabled by policy |
| ● | Test status marker on test definition lines |

### Coverage Bars (left gutter)

| Marker | Meaning |
|--------|---------|
| ▸ (green) | Line is covered — all covering tests pass |
| ▸ (red) | Line is covered — some covering tests are failing |
| ○ (gray) | Line has no test coverage |

### Inline Decorations

| Decoration | Meaning |
|------------|---------|
| `= 42` (gray text) | Evaluation result from `Alt+Enter` |
| `⊘ testName — Expected: 5  Actual: 3` | Test failure with Expected/Actual diff |
| `⊘ testName — exception message` | Test failure from unhandled exception |
| `⊘ testName — Timed out` | Test exceeded its timeout |
| `ℹ️ narrative summary` | Failure narrative: what changed, when it last passed |

> 💡 **Hover** over any decoration for more details, including failure narratives with causal analysis.

### Status Bar

The status bar (bottom of VS Code) shows three items:

| Item | Example | Meaning |
|------|---------|---------|
| **Daemon status** | `⚡ SageFs: MyProject [3]` | Connected, project name, eval count |
| **Test summary** | `🧪 42/42 ✓` | All tests passing |
| | `🧪 40/42 ✗ 2` | 40 passing, 2 failing (red background) |
| | `🧪 ⟳ Running 5/42` | Tests currently executing (spinner) |
| | `🧪 ⚠ 10/42 stale` | Tests need re-run — code changed (yellow background) |
| | `🧪 No tests` | No tests discovered yet |
| **Eval performance** | `P50: 12ms P95: 45ms` | Eval latency percentiles with sparkline |

Click the daemon status item to open the dashboard.

---

## Features

### Code Evaluation
- **Alt+Enter** — Evaluate the current selection or `;;`-delimited code block. Results appear as inline decorations.
- **Alt+Shift+Enter** — Evaluate the entire file
- **Shift+Enter** — Evaluate current block and advance cursor to the next
- **Ctrl+Alt+Enter** — Evaluate all `;;` blocks in the file
- **CodeLens** — Clickable "▶ Eval" buttons above every `;;` block
- **Density cycling** — Toggle between Full / Normal / Minimal inline result display

### Live Unit Testing
- **Inline test decorations** — ✓/✗/● markers on test lines, updated in real-time via SSE
- **Native Test Explorer** — Tests appear in VS Code's built-in Test Explorer via a `TestController` adapter
- **Test result CodeLens** — "✓ Passed" / "✗ Failed" above every test function
- **Failure diagnostics** — Failed tests appear as native VS Code squiggles
- **Coverage gutter bars** — Per-line coverage health (AllPassing / SomeFailing / NoCoverage) shown in the gutter
- **Inline failure details** — `⊘` markers with Expected/Actual diffs, exception messages, and timeouts
- **Failure narratives** — Enriched failure context: summary, time since last pass, causal changes
- **Test source locations** — Test Explorer items automatically link to their source file and line
- **Test policy controls** — Enable/disable live testing, run all tests, or configure run policies from the command palette
- **Call graph viewer** — Visualize test dependency graphs
- **Test trace** — Browse test cycle events

### Live Diagnostics
- F# type errors and warnings stream in via SSE as you edit, appearing as native VS Code squiggles

### Hot Reload
- **Hot Reload sidebar** — Tree view in the activity bar showing all project files with per-file and per-directory watch toggles
- Toggle individual files, directories, or watch/unwatch everything at once

### Session Management
- **Session Context sidebar** — Loaded assemblies, opened namespaces, failed opens, warmup details
- **Sessions sidebar** — View all sessions with inline switch/stop/reset actions
- **Multi-session** — Create, switch, and manage multiple sessions from the command palette
- **Export session** — Save current session state as a `.fsx` script
- **Session menu** — Quick-access menu for all session operations

### More
- **Type Explorer sidebar** — Browse .NET types and namespaces interactively from the activity bar
- **Event history** — Browse recent pipeline events via QuickPick
- **FSI bindings browser** — View all current FSI bindings
- **Dashboard webview** — Open the SageFs dashboard directly inside VS Code
- **Status bar** — Active project, eval count, test summary, eval performance sparkline. Click to open dashboard.
- **Auto-start** — Detects `.fsproj`/`.sln`/`.slnx` files and offers to start SageFs automatically
- **Ionide integration** — Hijacks Ionide's `FSI: Send Selection` commands so Alt+Enter routes through SageFs
- **7 custom theme colors** — Inline result colors respect your VS Code theme

---

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

Or: open the Extensions sidebar → click `...` (top-right) → "Install from VSIX..." → select the downloaded file.

**Option B: Build from source**

```bash
cd sagefs-vscode
npm install
npm run compile
npx @vscode/vsce package
code --install-extension sagefs-*.vsix
```

---

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `sagefs.mcpPort` | `37749` | SageFs MCP server port |
| `sagefs.dashboardPort` | `37750` | SageFs dashboard port |
| `sagefs.autoStart` | `true` | Automatically start SageFs when opening F# projects |
| `sagefs.projectPath` | `""` | Explicit `.fsproj` path (auto-detect if empty) |
| `sagefs.logLevel` | `"info"` | Output channel verbosity (`debug`, `info`, `warn`, `error`) |

---

## Commands

### Evaluation

| Command | Keybinding | Description |
|---------|-----------|-------------|
| SageFs: Evaluate Selection / Line | `Alt+Enter` | Evaluate selection or `;;` block |
| SageFs: Evaluate Entire File | `Alt+Shift+Enter` | Evaluate full file |
| SageFs: Evaluate & Advance | `Shift+Enter` | Evaluate current block, move cursor to next |
| SageFs: Evaluate All Blocks | `Ctrl+Alt+Enter` | Evaluate every `;;` block in the file |
| SageFs: Evaluate Code Block | — | Evaluate the `;;`-delimited block at cursor |
| SageFs: Cancel Evaluation | `Ctrl+Shift+C` | Cancel a running evaluation |
| SageFs: Next Code Block | `Ctrl+Down` | Jump cursor to next `;;` block |
| SageFs: Previous Code Block | `Ctrl+Up` | Jump cursor to previous `;;` block |
| SageFs: Clear Inline Results | — | Remove all inline result decorations |
| SageFs: Cycle Density | — | Toggle Full → Normal → Minimal inline display |
| SageFs: Load Current Script | — | Load the active `.fsx` file into the session |

### Daemon & Session

| Command | Keybinding | Description |
|---------|-----------|-------------|
| SageFs: Start Daemon | — | Start the SageFs daemon |
| SageFs: Stop Daemon | — | Stop the SageFs daemon |
| SageFs: Restart Daemon | — | Restart the SageFs daemon |
| SageFs: Open Dashboard | — | Open web dashboard in VS Code |
| SageFs: Create Session | — | Create a new FSI session |
| SageFs: Switch Session | — | Switch to a different session |
| SageFs: Stop Session | — | Stop the active session |
| SageFs: Reset Session | — | Soft reset (clear definitions, keep session) |
| SageFs: Hard Reset (Rebuild) | — | Full rebuild and reload |
| SageFs: Session Menu | — | Quick-access menu for all session operations |
| SageFs: Export Session as .fsx | — | Save session state to a script file |
| SageFs: Show FSI Bindings | — | Browse current FSI bindings |
| SageFs: Configure Warmup Auto-Open | — | Create or open `.SageFs/config.fsx` |

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
| SageFs: Show Test Trace | — | Browse test cycle events |
| SageFs: Show Recent Events | — | Browse pipeline event history |

### Sidebar Views

| View | Location | Description |
|------|----------|-------------|
| Hot Reload Files | Activity Bar | File tree with watch toggles |
| Session Context | Activity Bar | Assemblies, namespaces, warmup details |
| Sessions | Activity Bar | All sessions with inline switch/stop/reset |

---

## 🔧 Troubleshooting

### "SageFs: offline" in status bar
The daemon isn't running. Fix:
1. Run `dotnet tool install --global SageFs` if you haven't installed the CLI
2. Click the status bar item, or run Command Palette → "SageFs: Start Daemon"
3. The extension auto-starts the daemon when it detects F# projects — if this isn't happening, check `sagefs.autoStart` is `true`

### No inline results after Alt+Enter
1. Check the Output panel (View → Output → select "SageFs" from the dropdown) for errors
2. Verify the daemon is running: the status bar should show `⚡ SageFs: YourProject`
3. Try restarting: Command Palette → "SageFs: Hard Reset (Rebuild)"
4. Make sure your cursor is in an F# file (keybindings only activate for `fsharp` language)

### Tests not appearing in gutter
1. Your project needs [Expecto](https://github.com/haf/expecto) tests (not xUnit/NUnit)
2. Enable live testing: Command Palette → "SageFs: Enable Live Testing"
3. Check that the daemon discovered tests: status bar should show a test count (e.g., `🧪 42/42 ✓`)
4. Check the Output panel for errors

### Status bar shows no connection
The SSE connection to the daemon dropped. It will reconnect automatically. If it persists:
1. Check if the daemon process is still running (look for `SageFs` in your task manager)
2. Restart the daemon: Command Palette → "SageFs: Restart Daemon"
3. Check Output panel (select "SageFs") for connection errors

### "No .fsproj or .sln found"
Open a folder containing an F# project. The extension auto-detects projects. For non-standard layouts, set `sagefs.projectPath` in VS Code settings to the explicit path.

### Daemon crashes or restarts unexpectedly
The extension detects daemon crashes and shows a restart prompt. If it keeps crashing:
1. Check the SageFs output in your terminal for stack traces
2. Try starting SageFs manually from a terminal: `SageFs --proj YourProject.fsproj`
3. File an issue at [github.com/WillEhrendreich/SageFs/issues](https://github.com/WillEhrendreich/SageFs/issues)

---

## Architecture

This extension is written entirely in F# using [Fable](https://fable.io/) — no TypeScript. The F# source compiles to JavaScript, giving you type-safe extension code with the same language as your project.

Key architectural decisions:
- **SSE (Server-Sent Events)** for all real-time data: test results, diagnostics, coverage, eval results
- **POST commands** for all actions: eval, reset, start/stop — responses are acknowledgments only
- **Native VS Code APIs** via Fable bindings: TestController, CodeLens, TreeView, Diagnostics, Decorations

## Development

```bash
cd sagefs-vscode
npm install
npm run compile
```

Press **F5** in VS Code to launch the Extension Development Host.
