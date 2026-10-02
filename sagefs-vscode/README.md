# SageFs — VS Code Extension

> **Live eval, live testing, coverage and hot reload all run today, each with limits that are written down.** [The main README](../Readme.md) and [docs/](../docs/README.md) say what is solid and what isn't.

SageFs brings live evaluation, instant test feedback, and coverage visualization to VS Code. No configuration needed. Just press `Alt+Enter`.

## ✨ What You Get

| Feature | What it does |
|---------|-------------|
| **Inline Results** | Expression values appear next to your code as you evaluate |
| **Live Testing** | When live testing is enabled, the affected tests re-run as you edit (saved or not) and feed green ✓ / red ✗ gutter state |
| **Debug a failing test** | A failing test gets a Debug lens, a Debug link in its hover and Debug Test in the Test Explorer. It attaches a .NET debugger to the process that runs your tests. Breakpoints bind in your compiled project code, and not in code you evaluated in the session, which has no debug symbols |
| **Coverage Gutters** | Colored bars show which lines are covered by tests |
| **Failure Details** | Inline `⊘` markers show Expected vs Actual diffs |
| **Failure Narratives** | Rich context: what changed, when it last passed, causal analysis |
| **Test Source Jump** | Test Explorer items link to their source location automatically |
| **Hot Reload** | Save a `.fs` file and SageFs re-points the changed methods in the running app (Harmony), route tables built once at startup included, and your app's state stays. Browser refresh over SSE works. What patches and what restarts the app is in [docs/hot-reload.md](../docs/hot-reload.md). |
| **Eval Performance** | Status bar sparkline with P50/P95/P99 eval latencies |

> **Available on the [VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=willehrendreich.sagefs) and [Open VSX](https://open-vsx.org/extension/willehrendreich/sagefs).**

---

## 🚀 Quick Start

1. **Install SageFs CLI**: `dotnet tool install --global SageFs`
2. **Install this extension**: Search **SageFs** in the Extensions sidebar, or `code --install-extension willehrendreich.sagefs`
3. **Open an F# project** in VS Code
4. **Press `Alt+Enter`** on any expression — the daemon starts automatically and results appear inline

Live testing is a per-session toggle. If you pick the Live Testing workflow (`SageFs: Switch Workflow`) it is turned on for you once the session is ready. In the other workflows, run `SageFs: Enable Live Testing` once your test session is loaded.

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
| Next failing test | `Alt+Shift+]` | SageFs: Next Failing Test |
| Previous failing test | `Alt+Shift+[` | SageFs: Previous Failing Test |
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
| **Hot reload** | `Hot reload: patched 3/3` | What the last save did to the running app, see below. Hidden until the first save. |

Click the daemon status item to open the session menu.

The daemon item's tooltip is also its accessible name. It reads `SageFs: MyProject [Hot Reload] - session 7c8bec06 - 4 session(s)`, so it names the workflow and which session this window is talking to. The test summary item's accessible name is its headline only, and the tooltip adds what the last run was and why, each on its own line.

#### The hot reload item

After a save in a Hot Reload session, the item shows the daemon's verdict for that save, and I show one message when the save is settled:

| Item says | Meaning |
|-----------|---------|
| `compiling Program.fs` | The save is being built. No message yet. |
| `applied 2/2, not run yet` | The patch landed and nothing has been seen running the new body. No message yet, `patched` or `never ran` follows. |
| `patched 2/2` | The new body ran. The message says how many definitions and whether it was a metadata delta or a detour. |
| `patched 1/2, never ran` (yellow) | The patch is in place and nothing called it within the daemon's bound. Exercise that code path, or restart the app. |
| `restarted`, `no effect`, `kept live state` | What it says. The message carries the daemon's cause and next step. |
| `restart required`, `compile failed` (red) | The change cannot be applied, or did not build. A failed build leaves the app serving the last code that compiled. |

The tooltip has the daemon's message, the mechanism, and, when a patch left the REPL behind the running app, that it is behind by N saves. Hard Reset (Rebuild) brings the REPL level, and it stops the running app, so the message offers it as a button and does not do it for you. Clicking the item opens the Output channel, where the same text is written.

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

#### Which session a window uses

One daemon serves every window and every agent on the machine, so a window has to know which session is its own. I decide it like this:

1. The session you picked with Switch Session, or that Create Session just made, for as long as it exists.
2. Otherwise the first session whose working directory is inside one of the folders you have open.
3. Otherwise none. The status bar says none of the daemon's sessions is for this workspace, and the views offer Create Session. I do not borrow a session because it happens to be first in the daemon's list.

Create Session finds the session it made, selects it, and tells you. If it cannot tell which one is new, it says so and asks you to pick it, and does not guess. Every command that acts on the session carries its id: live testing on, off and run, run one test, load script, and the Test Explorer run button. Reset, hard reset, cancel and run policy have no session id on the daemon's routes, so for those I make the window's session the daemon's active one first, and the command does not run if that switch fails.

Each row in the Sessions sidebar reads `<project names> <id> · active · this workspace · Ready · 3 evals · Work/molina`. The short id comes first because the sidebar cuts the text from the right. `active` is the session this window uses, `this workspace` is any session started in a folder you have open, and the last two folders of the working directory follow. More than two projects show as the first two and `+N more`. The tooltip has everything.

#### Other agents' events

The daemon sends every session's events to every window. By default I show only the ones about this window's session, a session started in this workspace, or a file under this workspace: a session fault, and the `File reloaded` lines in the Output channel. Another agent's fault does not pop up in your window. The Output channel notes the ones I left out at the `debug` log level. Turn on `sagefs.showEventsFromAllSessions` to see everything.

A session fault message is one sentence and a remedy. The daemon's full diagnosis goes to the Output channel, and the message's Show Output button opens it.
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
- **11 custom theme colors** — Inline result and gutter colors respect your VS Code theme (the list is under `contributes.colors` in `package.json`)

---

## Requirements

- [SageFs](../Readme.md) installed as a .NET global tool (`dotnet tool install --global SageFs`)
- An F# project (`.fsproj` or `.sln`) in your workspace

## Installing

**Option A: VS Code Marketplace (recommended)**

Search for **SageFs** in the Extensions sidebar, or install from the command line:

```bash
code --install-extension willehrendreich.sagefs
```

Also available on [Open VSX](https://open-vsx.org/extension/willehrendreich/sagefs) for VSCodium and other compatible editors.

**Option B: Download from GitHub Releases**

Each [GitHub Release](https://github.com/WillEhrendreich/SageFs/releases) includes a `.vsix` file:

```bash
code --install-extension sagefs-<version>.vsix
```

Or: open the Extensions sidebar → click `...` (top-right) → "Install from VSIX..." → select the downloaded file.

**Option C: Build from source**

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
| `sagefs.dashboardPort` | `37750` | Legacy fallback dashboard port. SageFs normally discovers the daemon and derives the dashboard port from the MCP port. |
| `sagefs.autoStart` | `true` | Automatically start SageFs when opening F# projects |
| `sagefs.projectPath` | `""` | Explicit `.fsproj` path (auto-detect if empty) |
| `sagefs.logLevel` | `"info"` | Output channel verbosity (`debug`, `info`, `error` — no `warn` level) |
| `sagefs.showEventsFromAllSessions` | `false` | Show session faults and file reloads from every session on the daemon. Off by default, so a window shows its own session's and its workspace's. |
| `sagefs.inlineResultTimeout` | `30000` | How long (ms) inline eval results stay visible before auto-clearing. `0` keeps them forever. |
| `sagefs.cellHighlight` | `true` | Highlight the current code cell (block) the cursor is in |
| `sagefs.density` | `"full"` | Visual annotation level: `full` (all decorations), `normal` (inline results and test signs), `minimal` (inline results only) |
| `sagefs.typeExplorerRoot` | `""` | Root namespace for the Type Explorer tree (empty shows all top-level namespaces) |

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
| SageFs: Cycle Density (Full → Normal → Minimal) | — | Toggle Full → Normal → Minimal inline display |
| SageFs: Load Current Script | — | Load the active `.fsx` file into the session |

### Daemon & Session

| Command | Keybinding | Description |
|---------|-----------|-------------|
| SageFs: Start Daemon | — | Start the SageFs daemon |
| SageFs: Stop Daemon | — | Stop the SageFs daemon |
| SageFs: Restart Daemon | — | Restart the SageFs daemon |
| SageFs: Open Dashboard | — | Open web dashboard in VS Code |
| SageFs: Show Output | none | Open the SageFs Output channel |
| SageFs: Check Health | — | Run the extension's health check |
| SageFs: Create Session | — | Create a new FSI session |
| SageFs: Switch Session | — | Switch to a different session |
| SageFs: Switch Project | — | Change which `.fsproj`/`.sln` the session loads |
| SageFs: Browse for Project | — | Pick a project file from a file dialog |
| SageFs: Reconnect to Daemon | — | Re-open the connection to the daemon |
| SageFs: Switch Workflow | — | Switch the session between REPL, Live Testing and Hot Reload |
| SageFs: Stop Session | — | Stop the active session |
| SageFs: Reset Session | — | Soft reset (clear definitions, keep session) |
| SageFs: Hard Reset (Rebuild) | — | Full rebuild and reload |
| SageFs: Session Menu | — | Quick-access menu for all session operations |
| SageFs: Export Session as .fsx | — | Save session state to a script file |
| SageFs: Show FSI Bindings | — | Browse current FSI bindings |
| SageFs: Configure Warmup Auto-Open | — | Create or open `.SageFs/config.fsx` |
| SageFs: Refresh Sessions | — | Refresh the Sessions sidebar |
| SageFs: Refresh Session Context | — | Refresh the Session Context sidebar |
| SageFs: Run App | — | Run the session's executable project, with hot reload |
| SageFs: Stop App | — | Stop the app started by Run App |
| SageFs: Open Getting Started Sample | — | Open the bundled getting-started `.fsx` |

The Sessions sidebar also has inline per-row actions (Switch To, Stop, Reset) — click the icons on a session row instead of going through the command palette.

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
| SageFs: Run This Test | — | Run one test (from the CodeLens above it) |
| SageFs: Debug This Test | — | Hold one test, attach a .NET debugger to the process that runs it, release it |
| SageFs: Show Covering Tests | — | The tests that cover a symbol (from a coverage CodeLens) |
| SageFs: Set Test Run Policy | — | Configure per-category run policies |
| SageFs: Show Test Call Graph | — | Visualize test dependency graph |
| SageFs: Show Test Trace | — | Browse test cycle events |
| SageFs: Show Recent Events | — | Browse pipeline event history |
| SageFs: Explain Test Failure | — | Enriched failure context for the test at cursor |
| SageFs: Suggest Repair for Failed Test | — | Trace causal changes and suggest a fix |
| SageFs: Next Failing Test | `Alt+Shift+]` | Jump to the next failing test |
| SageFs: Previous Failing Test | `Alt+Shift+[` | Jump to the previous failing test |

### Sidebar Views

| View | Location | Description |
|------|----------|-------------|
| Hot Reload Files | Activity Bar | File tree with watch toggles |
| Session Context | Activity Bar | Assemblies, namespaces, warmup details |
| Sessions | Activity Bar | All sessions with inline switch/stop/reset |
| API Browser | Activity Bar | Browse .NET types and namespaces (the Type Explorer) |

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
1. SageFs discovers [Expecto](https://github.com/haf/expecto), xUnit (including v3), NUnit, MSTest, and TUnit tests. Expecto is the best-covered path today, so start there if you can.
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
2. Try starting SageFs manually from a terminal: `SageFs`
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
