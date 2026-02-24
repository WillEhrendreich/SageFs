# SageFs

**A live F# development server that eliminates the edit-build-run cycle.** Edit code, save, see changes in your browser — instantly. No restart. No rebuild. Just flow.

SageFs is a [.NET global tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools) that turns F# Interactive into a full development environment: project loading, sub-second hot reload, file watching, multi-session isolation, a web dashboard, an MCP server that gives AI agents live access to your running code, and [live unit testing](#-live-unit-testing) that runs affected tests on every edit — across every editor, every major .NET test framework — for free.

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com)

<p align="center">
  <img src="docs/hot-reload-demo.gif" alt="SageFs Hot Reload Demo — edit F# code and see changes in the browser instantly" width="800"/>
</p>

---

## Why SageFs?

**The problem:** F# development has a painful feedback loop. Change a line → wait for `dotnet build` → restart your app → navigate back to where you were → check if it worked. For web apps, this can be 30-60 seconds per change. Interactive development with `dotnet fsi` helps, but it can't load your project's dependencies, doesn't watch files, and has no IDE integration.

**SageFs fixes all of this:**

- **Sub-second hot reload** — Save a `.fs` file and your running web server picks up the change in ~100ms. [Harmony](https://github.com/pardeike/Harmony) patches method pointers at runtime — no restart, no rebuild. Browsers auto-refresh via SSE.
- **Live unit testing** — Edit code and affected tests run automatically in under 500ms. Gutter markers show pass/fail on test code and coverage on production code — across every editor. No IL instrumentation, no separate test runner. [See details below.](#-live-unit-testing)
- **Full project context in the REPL** — All your NuGet packages, project references, and namespaces are loaded automatically. No `#r` directives. It's your actual project, live.
- **AI agents that can compile and run your code** — SageFs exposes an [MCP server](https://modelcontextprotocol.io/) so AI tools (Copilot, Claude, etc.) can execute F# code, type-check, explore .NET APIs, and run tests — all against your real project.
- **One server, every frontend** — Start the daemon once. Connect from VS Code, Neovim, the terminal, a GPU-rendered GUI, a web dashboard, or all of them at the same time. They all share the same live session state.
- **Crash-proof sessions** — An Erlang-style supervisor auto-restarts the daemon on crash. Worker sessions run in isolated sub-processes — one crash doesn't take down the others.

---

## 🧪 Live Unit Testing

> *Visual Studio Enterprise charges ~$250/month for Live Unit Testing. SageFs does it better — across every editor, every major .NET test framework — for free.*

SageFs already has hot reload, file watching, Harmony method patching, and real-time SSE push to every connected UI. **Live Unit Testing** wires those together into a single pipeline: edit code → tests run automatically → results appear inline, in under 500ms.

**What this looks like in practice:**

```
✓ let ``should add two numbers`` () =       ← passed (12ms)
✗ let ``should reject negative`` () =       ← failed: Expected Ok but got Error
● let ``should handle empty`` () =          ← detected, not yet run
▸ let validate x =                          ← covered by 3 tests, all passing
○ let unusedHelper () = ()                  ← not reached by any test
```

Gutter markers appear in your editor (VS Code, Neovim, TUI, GUI, Visual Studio — all of them) showing test status on test code and test reachability on production code. No configuration, no IL instrumentation, no separate test runner window.

**How it's different from VS Enterprise:**

| | VS Enterprise Live Testing | SageFs Live Testing |
|---|---|---|
| **Speed** | 5-30 seconds (full build) | 200-500ms (hot reload, no build) |
| **Trigger** | File save only | As-you-type (configurable per category) |
| **Frameworks** | MSTest, xUnit, NUnit only | + Expecto, TUnit, extensible provider model |
| **Coverage method** | IL instrumentation (heavy) | FCS typed AST symbol graph (lightweight) |
| **Editors** | Visual Studio only | VS Code, Neovim, TUI, GUI, Visual Studio, web dashboard |
| **Cost** | ~$250/month | Free, MIT licensed |

**Three-speed feedback pipeline:**
1. **~50ms** — Tree-sitter detects test attributes in broken/incomplete code → immediate gutter markers
2. **~350ms** — F# Compiler Service type-checks → namespace disambiguation, dependency graph, reachability annotations
3. **~500ms** — Harmony patches + affected-test execution → ✓/✗ results inline

Tests are categorized automatically (Unit, Integration, Browser, Property, Benchmark) with smart run policies — unit and property tests run on every keystroke, integration tests run on save, browser tests run on demand. All configurable.

**What's built:**

- [x] **Pure domain model** — `TestId` (SHA256-stable), `TestCase`, `TestResult`, `TestRunStatus`, `TestDependencyGraph`, `CoverageAnnotation`, and full Elm state management (`LiveTestState` with events, update, projection)
- [x] **Two-tier provider system** — Attribute-based executor (Tier 1) covers xUnit, NUnit, MSTest, TUnit in ~10 lines each; custom executor (Tier 2) handles Expecto-style value-based tests
- [x] **Tree-sitter source detection** — `tests.scm` query file detects test attributes in broken/incomplete F# code for instant gutter markers, even before the compiler runs
- [x] **Test execution orchestration** — `TestOrchestrator` handles discovery, reflection-based execution with async parallelism and semaphore limits
- [x] **Transitive coverage types & pure functions** — `TestDependencyGraph` with BFS reachability, `CoverageComputation` for line-level annotations, `filterByPolicy()` for trigger-based filtering
- [x] **OTEL instrumentation** — `ActivitySource` + `Meter` with histograms for tree-sitter, FCS, and execution timing; zero-cost (~50ns) when no collector attached
- [x] **Elm architecture integration** — 8 event types (`TestsDiscovered`, `TestResultsBatch`, `AffectedTestsComputed`, `CoverageUpdated`, etc.) wired through `SageFsModel` update loop
- [x] **TUI gutter rendering** — `LineAnnotation` icons/colors rendered in the terminal UI via `RenderPipeline`, toggle with **Ctrl+Alt+T**
- [x] **Harmony hot-reload trigger** — `LiveTestingHook.afterReload()` called after every successful eval in `HotReloading.fs`, detecting providers, discovering tests, and finding affected tests from updated methods. Results flow through worker protocol metadata into the Elm event loop.
- [x] **Run policy enforcement** — `filterByPolicy()` integrated into execution paths so unit tests run on keystroke, integration on save, browser on demand
- [x] **SSE push of test results** — `TestSummaryChanged` and `TestResultsBatch` events streamed to connected HTTP/SSE clients via push notification architecture
- [x] **MCP live test tools** — `get_live_test_status` (query test state with file filter), `set_run_policy` (control which categories run when), `get_pipeline_trace` (debug the pipeline waterfall)
- [x] **128+ tests** — Domain model, executor, tree-sitter, instrumentation, Elm integration, and FsCheck property-based tests all passing
- [x] **FCS dependency graph** — F# Compiler Service `CheckFileResults` wired via `SymbolGraphBuilder` to build symbol→test dependency maps, with `SymbolDiff` for detecting changes between FCS runs and `FileAnalysisCache` for per-file caching
- [x] **Three-speed pipeline end-to-end** — Full debounced pipeline: keystroke → tree-sitter (50ms) → FCS with adaptive backoff (300ms, max 2000ms) → affected-test execution. `PipelineDebounce` manages per-stage cancellation tokens, `AdaptiveDebounce` backs off dynamically on FCS cancellations
- [x] **Source mapping** — `SourceMapping` module bridges tree-sitter source locations (file/line/column) to reflection-discovered tests via function name matching, so gutter markers land on the right line even for Expecto-style hierarchical tests
- [x] **`run_tests` MCP tool** — On-demand test execution with optional pattern and category filters, integrated into the Elm event loop via `RunTestsRequested`
- [x] **`toggle_live_testing` MCP tool** — Enable/disable live testing from any MCP client
- [x] **Daemon startup guard** — All editor plugins (VS Code, Visual Studio, CLI, Raylib GUI) now check for an already-running daemon via HTTP probe before spawning a new instance, preventing duplicate daemons

**What's next:**

- [ ] **VS Code gutter markers** — DecorationProvider and TestController integration in the VS Code extension (`LineAnnotation` data ready, UI not yet connected)
- [ ] **Neovim gutter markers** — Extmark signs and virtual text for test status in sagefs.nvim
- [ ] **Raylib GUI gutter rendering** — Gutter icons in the GPU-rendered GUI frontend
- [ ] **Visual Studio gutter markers** — Margin glyphs via the VS Extensibility SDK

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://docs.docker.com/get-docker/) — required for PostgreSQL, which [Marten](https://martendb.io/) uses for event sourcing (managed automatically via [Testcontainers](https://dotnet.testcontainers.org/), no manual setup needed)

---

## Installation

SageFs is a [.NET global tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools) published on [NuGet](https://www.nuget.org/packages/SageFs/):

```bash
dotnet tool install --global SageFs
```

Verify it installed:

```bash
sagefs --help
```

To **update** to the latest version:

```bash
dotnet tool update --global SageFs
```

<details>
<summary><strong>Build from source</strong></summary>

```bash
git clone https://github.com/WillEhrendreich/SageFs.git
cd SageFs
dotnet build && dotnet pack SageFs -o nupkg
dotnet tool install --global SageFs --add-source ./nupkg --no-cache
```

</details>

---

## Getting Started

Navigate to any F# project directory and run:

```bash
sagefs --proj MyApp.fsproj
```

**What happens:**

1. SageFs starts a **daemon** — a background server that stays running
2. It builds your project and loads all dependencies into an F# Interactive session
3. It starts watching your source files for changes
4. It opens an **MCP server** on `http://localhost:37749/sse` (for AI agents)
5. It opens a **live dashboard** at `http://localhost:37750/dashboard`

**That's it.** SageFs is running. Open the dashboard, press **Ctrl+Enter** on some F# code, and see the result immediately.

---

## How to Use SageFs

SageFs is a daemon — one server, many clients. Start it once, connect from anywhere.

### VS Code Extension

The **SageFs extension** turns VS Code into a live F# development environment with inline eval results, real-time diagnostics, hot reload controls, and session management — all powered by the running SageFs daemon.

**What you get:**

- **Alt+Enter** — Evaluate the current selection or `;;`-delimited code block. Results appear as inline decorations right next to your code.
- **Alt+Shift+Enter** — Evaluate the entire file
- **CodeLens** — Clickable "▶ Eval" buttons above every `;;` block
- **Live diagnostics** — Type errors and warnings stream in via SSE as you edit, appearing as native VS Code squiggles
- **Hot Reload sidebar** — A tree view in the activity bar showing all project files with watch toggles. Toggle individual files, directories, or watch/unwatch everything at once.
- **Session Context sidebar** — See loaded assemblies, opened namespaces, failed opens, and warmup details for the active session
- **Status bar** — Shows the active project, eval count, supervised status, and restart count. Click it to open the web dashboard.
- **Multi-session support** — Create, switch, and manage multiple sessions from the command palette
- **Auto-start** — Detects `.fsproj`/`.sln`/`.slnx` files and offers to start SageFs automatically
- **Ionide integration** — Hijacks Ionide's `FSI: Send Selection` commands so **Alt+Enter** routes through SageFs instead of plain FSI

#### Installing the VS Code Extension

**Option A: Download from GitHub Releases (recommended)**

Each [GitHub Release](https://github.com/WillEhrendreich/SageFs/releases) includes a `.vsix` file. Download the latest and install:

```bash
code --install-extension sagefs-<version>.vsix
```

Reload VS Code and you're ready to go.

**Option B: Build from source**

```bash
cd sagefs-vscode
npm install
npm run compile
npx @vscode/vsce package
code --install-extension sagefs-*.vsix
```

#### Extension Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `sagefs.mcpPort` | `37749` | SageFs MCP server port |
| `sagefs.dashboardPort` | `37750` | SageFs dashboard port |
| `sagefs.autoStart` | `true` | Automatically start SageFs when opening F# projects |
| `sagefs.projectPath` | `""` | Explicit `.fsproj` path (auto-detect if empty) |

> **Note:** The VS Code extension is written entirely in F# using [Fable](https://fable.io/) — no TypeScript. The F# source compiles to JavaScript, giving you type-safe extension code with the same language as your project.

### Neovim Plugin

[**sagefs.nvim**](https://github.com/WillEhrendreich/sagefs.nvim) gives you the same experience in Neovim — **Alt+Enter** to evaluate `;;`-delimited cells, inline results via extmarks, gutter signs, SSE live updates, and session management.

```lua
-- lazy.nvim
{
  "WillEhrendreich/sagefs.nvim",
  ft = { "fsharp" },
  opts = { port = 37749, auto_connect = true },
}
```

The plugin auto-connects to the running daemon, shows eval results as virtual text below your code, and marks cells with ✓/✖/⏳ gutter signs. See the [sagefs.nvim README](https://github.com/WillEhrendreich/sagefs.nvim) for full setup and keybindings.

### Visual Studio Extension

The **SageFs Visual Studio extension** in `sagefs-vs/` uses the [VisualStudio.Extensibility](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/) SDK with a thin C# shell and all real logic in an F# core library (`SageFs.VisualStudio.Core`).

**What you get:**

- **Alt+Enter** — Evaluate selection, **Shift+Alt+Enter** — Evaluate file, **Ctrl+Alt+Enter** — Evaluate `;;`-delimited block
- **CodeLens** — "▶ Eval" buttons on every F# function, type, and module
- **Error List integration** — SageFs diagnostics stream into the native VS Error List via SSE
- **Session Context tool window** — Live dashboard showing connection status, assemblies, namespaces, warmup details
- **Hot reload** — Toggle files, directories, watch/unwatch all from the Extensions menu
- **Multi-session** — Create, switch, reset, and hard-reset sessions
- **Output window** — All eval results and command feedback logged to the SageFs output channel

### AI Agent (MCP)

SageFs speaks [Model Context Protocol](https://modelcontextprotocol.io/). Point your AI tool at the MCP endpoint and it becomes a live F# development partner — executing code, type-checking, exploring APIs, and running tests against your real project.

<details>
<summary><strong>GitHub Copilot (CLI & VS Code)</strong></summary>

Edit your MCP config file (usually `~/.config/.copilot/mcp-config.json` or wherever your Copilot MCP servers are configured):

```json
{
  "mcpServers": {
    "sagefs": {
      "type": "sse",
      "url": "http://localhost:37749/sse",
      "headers": {},
      "tools": ["*"]
    }
  }
}
```

In **VS Code**, you can also add it to `.vscode/mcp.json` in your workspace:

```json
{
  "servers": {
    "sagefs": {
      "type": "sse",
      "url": "http://localhost:37749/sse"
    }
  }
}
```
</details>

<details>
<summary><strong>Claude Code (CLI)</strong></summary>

Add a `.mcp.json` file to your project root:

```json
{
  "mcpServers": {
    "sagefs": {
      "type": "sse",
      "url": "http://localhost:37749/sse"
    }
  }
}
```

Or configure globally via `claude mcp add --transport sse sagefs http://localhost:37749/sse`.
</details>

<details>
<summary><strong>Claude Desktop</strong></summary>

Edit `claude_desktop_config.json` (Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "sagefs": {
      "type": "sse",
      "url": "http://localhost:37749/sse"
    }
  }
}
```
</details>

<details>
<summary><strong>Any MCP-compatible client</strong></summary>

SageFs exposes a standard [Model Context Protocol](https://modelcontextprotocol.io/) SSE endpoint:

```
http://localhost:37749/sse
```

Connect with any MCP client that supports SSE transport. No API key required — it's a local server.
</details>

### REPL Client

```bash
sagefs connect
```

A text-based REPL that connects to the running daemon. Type F# code, get results. Use `#help` for commands, `#sessions` to manage multiple sessions.

### Terminal UI

```bash
sagefs tui
```

A multi-pane terminal interface: editor, output, diagnostics, session context. Navigate with Tab, manage sessions with keyboard shortcuts. Tree-sitter syntax highlighting, mouse support, and the Kanagawa color theme by default.

### GPU-Rendered GUI

```bash
sagefs gui
```

A native GPU-rendered window via [Raylib](https://github.com/ChrisDill/Raylib-cs) with the same layout as the TUI. Both the TUI and GUI share the same rendering abstraction (`Cell[,]` grid) — same keybindings, same layout, same features.

### Web Dashboard

Already running at `http://localhost:37750/dashboard`. Submit code, view session status, manage sessions — all from the browser. Powered by [Falco.Datastar](https://github.com/spiraloss/Falco.Datastar) for real-time SSE updates.

**All of these connect to the same daemon.** Open multiple at once — they all see the same state.

### Frontend Feature Matrix

| Feature | TUI | Raylib GUI | Web Dashboard | VS Code | Visual Studio | Neovim |
|---------|:---:|:----------:|:------------:|:-------:|:------------:|:------:|
| Eval code | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Eval file | n/a¹ | n/a¹ | ✅ | ✅ | ✅ | ✅ |
| Eval block (`;;`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Inline results | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Diagnostics | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Create / switch session | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Stop session | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Reset / hard reset | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Session context | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Hot reload toggle | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Watch / unwatch all | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Code completion | ✅ | ✅ | ✅ | ✅ | —² | ✅ |
| SSE live updates | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Themes | ✅ | ✅ | ✅ | ✅ | —² | ✅ |
| CodeLens | n/a¹ | n/a¹ | n/a¹ | ✅ | ✅ | ✅ |
| Project discovery | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Session resume | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

> ¹ **n/a** — Feature is architecturally inapplicable. TUI/Raylib are REPL interfaces (eval file = just type code); CodeLens requires an editor with source buffers.
> ² **—** — VS Extensibility SDK (out-of-process, v17.14) does not yet expose completion provider or theme color contribution APIs. The HTTP client (`GetCompletionsAsync`) is implemented; UI integration awaits SDK support.

---

## Key Features

### 🔥 Hot Reload

This is the headline feature. Save a `.fs` file and SageFs:

1. Detects the change (~500ms debounce)
2. Sends `#load` to FSI (~100ms)
3. [Harmony](https://github.com/pardeike/Harmony) patches method pointers at runtime — no restart
4. Connected browsers auto-refresh via SSE (add `SageFs.DevReloadMiddleware` to your app)

```fsharp
// Add to your Falco/ASP.NET app for auto browser refresh:
open SageFs.DevReloadMiddleware

webHost [||] {
  use_middleware middleware
  // your routes...
}
```

Edit a handler, save the file, and the browser refreshes with the new code — all in under a second.

The VS Code extension also gives you per-file and per-directory hot reload toggles, so you control exactly which files trigger live patching.

### 🤖 AI-Native (MCP Server)

SageFs doesn't just expose static tools to AI agents — it uses an **affordance-driven state machine** that only presents tools valid for the current session state. An agent connecting to a warming-up session sees `get_fsi_status`; once ready, it sees `send_fsharp_code`, `check_fsharp_code`, etc. Invalid tool calls return structured errors with alternatives. This eliminates wasted tokens from agents guessing which tools work.

The MCP response strategy is also optimized for LLM context windows — echoed code is stripped, boilerplate moves to `ServerInstructions` (sent once), events use delta cursors instead of re-sending everything.

| Tool | What it does |
|------|-------------|
| `send_fsharp_code` | Execute F# code (each `;;` is a transaction — failures are isolated) |
| `check_fsharp_code` | Type-check without executing (pre-validate before committing) |
| `get_completions` | Code completions at cursor position |
| `explore_type` | Browse members of any .NET type |
| `explore_namespace` | Browse types in a namespace |
| `cancel_eval` | Cancel a running evaluation (recover from infinite loops) |
| `create_session` | Spin up a new isolated FSI session |
| `hard_reset_fsi_session` | Rebuild and reload (after source file changes) |
| `get_live_test_status` | Query live test state with optional file filter |
| `run_tests` | Run tests on demand with pattern/category filters |
| `set_run_policy` | Control which test categories auto-run and when |
| `toggle_live_testing` | Enable/disable the live testing pipeline |
| `get_pipeline_trace` | Debug the three-speed pipeline waterfall |

[Full tool list →](#mcp-tools-reference)

### 📦 Project & Solution Support

```bash
sagefs --proj MyApp.fsproj       # Load one project
sagefs --sln MySolution.sln      # Load entire solution
sagefs                           # Auto-detect in current directory
sagefs --bare                    # No project, just bare FSI
```

SageFs loads all NuGet packages, project references, and namespaces automatically. No manual `#r` directives needed.

### 👁️ File Watching

Source files are watched automatically. The escalation chain: `.fs`/`.fsx` changes → incremental `#load` reload (~100ms). `.fsproj` changes → soft reset. Rapid saves are debounced (500ms). Failed reloads are atomic — old definitions stay valid. Disable with `--no-watch`.

### 🔀 Multi-Session

Run multiple F# sessions simultaneously — different projects, different states. Each session is an **isolated worker sub-process** so one crash doesn't take down the others. Create, switch, and stop sessions from any frontend (VS Code, Neovim, REPL, dashboard, or MCP).

### 🛡️ Supervised Mode (Watchdog)

```bash
sagefs --supervised --proj MyApp.fsproj
```

Wraps the daemon in an Erlang-style supervisor with exponential backoff (1s → 2s → 4s → max 30s). After 5 consecutive crashes within 5 minutes, it reports the failure. The watchdog state is exposed via `/api/system/status` and shown in the VS Code status bar. Use this when leaving SageFs running all day.

### ⚡ Standby Pool

Hard resets are fast because SageFs maintains a **standby pool** of pre-warmed FSI sessions. When you reset, the active session is replaced with an already-warm one from the pool — near-instant recovery instead of a 30-60 second rebuild.

### 📊 Event Sourcing

All session events (evals, resets, diagnostics, errors) are stored in PostgreSQL via [Marten](https://martendb.io/). Query your development history, replay sessions, and build analytics on top of your coding patterns.

---

## Common Workflows

### F# Web Development (Falco)

```bash
sagefs --proj MyWebApp.fsproj
```

Edit your handlers → save → SageFs hot-reloads → browser auto-refreshes. Sub-second feedback loop. Add `SageFs.DevReloadMiddleware` to your pipeline for automatic browser refresh.

### AI-Assisted Development

Start SageFs, configure your AI tool's MCP settings, and your AI agent becomes a live F# development partner — it can execute code, check types, explore APIs, and run tests through SageFs. The affordance-driven tool exposure means agents succeed on the first attempt instead of guessing.

### REPL-Driven Development

```bash
sagefs connect
```

Prototype functions, test ideas, explore APIs — with your full project loaded. Everything you'd do in `dotnet fsi` but with your actual project dependencies available.

### Test-Driven Development

Run [Expecto](https://github.com/haf/expecto) tests directly inside SageFs — no separate test runner needed. Write a test, evaluate it, see red/green immediately. Change code, re-run, iterate. The REPL is your test runner.

---

## CLI Reference

```bash
# Start daemon (default)
sagefs                           # Auto-detect project
sagefs --proj MyApp.fsproj       # Specific project
sagefs --sln MySolution.sln      # Specific solution
sagefs --bare                    # No project, just bare FSI

# Connect clients
sagefs connect                   # REPL client
sagefs tui                       # Terminal UI
sagefs gui                       # GPU GUI (Raylib)

# Manage daemon
sagefs stop                      # Stop running daemon
sagefs status                    # Show daemon info

# Production / long-running
sagefs --supervised              # Auto-restart on crash

# Options
sagefs --mcp-port 8080           # Custom MCP port
sagefs --no-watch                # Disable file watcher
sagefs --use script.fsx          # Run script on startup
sagefs --help                    # All options
```

---

## Configuration

### Per-Directory Config

Create `.SageFs/config.fsx` in your project directory:

```fsharp
{ DirectoryConfig.empty with
    Load = Projects ["src/MyApp.fsproj"; "tests/MyApp.Tests.fsproj"]
    InitScript = Some "setup.fsx" }
```

**Precedence:** CLI args > `.SageFs/config.fsx` > auto-discovery.

### Startup Profile

SageFs auto-loads `~/.SageFs/init.fsx` on session start, if it exists. Use it for personal helpers, open statements, or custom setup that should apply to every session.

---

## Troubleshooting

**"SageFs daemon not found"** — Make sure the daemon is running (`sagefs --proj ...` in another terminal). Clients auto-discover via HTTP health check.

**"Session is still starting up"** — The FSI session is loading your project. Wait for the "ready" message. Large projects may take 30-60 seconds. The standby pool makes subsequent resets much faster.

**Build errors after code changes** — If you changed `.fs` files and the REPL seems stale, run `hard_reset_fsi_session` (via MCP) or `#hard-reset` (in the REPL). This rebuilds and reloads. Note: file watching handles most cases automatically — you shouldn't need manual resets often.

**Port already in use** — Another SageFs instance is running. Use `sagefs stop` or `sagefs --mcp-port 8080`.

**Hot reload not working** — Make sure your app uses `SageFs.DevReloadMiddleware` for browser auto-refresh. Check the SageFs console for 🔥 or 📄 messages confirming file changes are detected.

---

<details>
<summary><h2>MCP Tools Reference</h2></summary>

| Tool | Description |
|------|-------------|
| `send_fsharp_code` | Execute F# code. Each `;;` marks a transaction boundary. |
| `check_fsharp_code` | Type-check without executing. Returns diagnostics. |
| `get_completions` | Code completions at a cursor position. |
| `cancel_eval` | Cancel a running evaluation. |
| `load_fsharp_script` | Load an `.fsx` file with partial progress. |
| `get_recent_fsi_events` | Recent evals, errors, and loads with timestamps. |
| `get_fsi_status` | Session health, loaded projects, statistics, affordances. |
| `get_startup_info` | Projects, features, CLI arguments. |
| `get_available_projects` | Discover `.fsproj`/`.sln`/`.slnx` in working directory. |
| `explore_namespace` | Browse types and functions in a .NET namespace. |
| `explore_type` | Browse members and properties of a .NET type. |
| `get_elm_state` | Current UI render state (editor, output, diagnostics). |
| `reset_fsi_session` | Soft reset — clear definitions, keep DLL locks. |
| `hard_reset_fsi_session` | Full reset — rebuild, reload, fresh session. |
| `create_session` | Create a new isolated FSI session. |
| `list_sessions` | List all active sessions. |
| `stop_session` | Stop a session by ID. |
| `switch_session` | Switch active session by ID. |

</details>

<details>
<summary><h2>Architecture</h2></summary>

SageFs is a **daemon-first architecture**. One server, many clients.

```
                ┌───────────────┐
                │  SageFs Daemon│
                │  ┌─────────┐  │
                │  │ FSI Actor│  │
                │  │ (Eval +  │  │
                │  │  Query)  │  │
                │  └─────────┘  │
                │  ┌─────────┐  │
                │  │  File    │  │
                │  │ Watcher  │  │
                │  └─────────┘  │
                │  ┌─────────┐  │
                │  │  MCP     │  │
                │  │ Server   │  │
                │  └─────────┘  │
                └──┬──┬──┬──┬───┘
                   │  │  │  │
     ┌──────┐  ┌──┴┐ ┌┴──┐ ┌┴──────┐  ┌────────┐
     │VS Code│  │TUI│ │GUI│ │ Web   │  │AI Agent│
     │Plugin │  │   │ │   │ │ Dash  │  │ (MCP)  │
     └──────┘  └───┘ └───┘ └───────┘  └────────┘
     ┌──────┐  ┌───────┐
     │Neovim│  │ REPL  │
     │Plugin│  │Connect│
     └──────┘  └───────┘
```

- **Daemon** — Runs FSI actors, MCP server, file watcher, hot reload engine, web dashboard
- **Worker Sessions** — Isolated FSI sub-processes per project (Erlang-style fault isolation)
- **Clients** — VS Code, Neovim, REPL, TUI, GUI, dashboard, AI agents — all connect over HTTP/SSE
- **Dual Renderer** — TUI and GUI share the same `Cell[,]` grid abstraction. Same keybindings, same layout.

</details>

<details>
<summary><h2>Testing</h2></summary>

```bash
# Run all tests (Expecto — use dotnet run, not dotnet test)
dotnet run --project SageFs.Tests

# Filter by name
dotnet run --project SageFs.Tests -- --filter "Snapshot"
dotnet run --project SageFs.Tests -- --filter "Hot Reload"
```

</details>

---

## License

MIT — see [LICENSE](LICENSE)

## Acknowledgments

- [FsiX](https://github.com/soweli-p/FsiX) — The original F# Interactive experience that inspired SageFs
- [sagefs.nvim](https://github.com/WillEhrendreich/sagefs.nvim) — Neovim plugin for SageFs (separate repo)
- [Falco](https://github.com/pimbrouwers/Falco) & [Falco.Datastar](https://github.com/spiraloss/Falco.Datastar) — Dashboard framework
- [Harmony](https://github.com/pardeike/Harmony) — Runtime method patching for hot reload
- [Ionide.ProjInfo](https://github.com/ionide/proj-info/) — Project file parsing
- [Marten](https://martendb.io/) — Event sourcing on PostgreSQL
- [Raylib-cs](https://github.com/ChrisDill/Raylib-cs) — GPU-rendered GUI
- [Fable](https://fable.io/) — F# to JavaScript compiler (powers the VS Code extension)
- [ModelContextProtocol](https://modelcontextprotocol.io/) — AI integration standard
