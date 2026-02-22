# SageFs

**A live F# development server.** Load your project, evaluate code, hot-reload changes, and connect AI agents — all from one running daemon.

SageFs is a [.NET global tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools) that wraps F# Interactive with project support, file watching, hot reloading, and an MCP server for AI-assisted development.

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com)

<p align="center">
  <img src="docs/hot-reload-demo.gif" alt="SageFs Hot Reload Demo — edit F# code and see changes in the browser instantly" width="800"/>
</p>

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git (to clone the repo)

---

## Installation

SageFs is a [.NET global tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools). It will be published to NuGet once stable — for now, clone the repo and install locally:

```bash
git clone https://github.com/WillEhrendworb/SageFs.git
cd SageFs
dotnet build && dotnet pack SageFs -o nupkg
dotnet tool install --global SageFs --add-source ./nupkg
```

Verify it installed:

```bash
sagefs --help
```

To **update** after pulling new changes:

```bash
cd SageFs
git pull
dotnet tool uninstall --global SageFs
dotnet build && dotnet pack SageFs -o nupkg
dotnet tool install --global SageFs --add-source ./nupkg --no-cache
```

### Connect Your AI Agent (MCP)

Once SageFs is running (`sagefs --proj YourApp.fsproj`), point your AI tool at it.

**GitHub Copilot CLI** — add to your [MCP config](https://docs.github.com/en/copilot/customizing-copilot/extending-copilot-for-your-organization/managing-mcp-servers):

```json
{
  "mcpServers": {
    "SageFs": {
      "type": "sse",
      "url": "http://localhost:37749/sse"
    }
  }
}
```

**Claude Desktop** — add to your MCP settings:

```json
{
  "mcpServers": {
    "SageFs": {
      "url": "http://localhost:37749/sse"
    }
  }
}
```

Your AI agent now has live access to your F# project — it can execute code, check types, explore APIs, and manage sessions through SageFs.

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

You'll see output like:

```
SageFs daemon v0.5.35 starting on port 37749...
√ Event store: PostgreSQL (auto-started via Docker)
MCP SSE endpoint: http://localhost:37749/sse
Dashboard available at http://localhost:37750/dashboard
SageFs daemon ready (PID 12345, MCP port 37749)
```

**That's it.** SageFs is running. Now you can interact with it.

### Try It Out

Open the **dashboard** in your browser:

```
http://localhost:37750/dashboard
```

Type some F# in the eval input and press **Ctrl+Enter**:

```fsharp
let greeting = "Hello from SageFs!"
printfn "%s" greeting
```

You'll see the result appear immediately.

---

## How to Use SageFs

SageFs is a daemon — one server, many clients. Start it once, connect from anywhere.

### Option 1: AI Agent (MCP)

If you configured MCP during installation (see above), your AI agent already has access. It can execute F# code, check types, explore APIs, and manage sessions — all through SageFs.

### Option 2: REPL Client

```bash
sagefs connect
```

A text-based REPL that connects to the running daemon. Type F# code, get results. Use `#help` for commands, `#sessions` to manage multiple sessions.

### Option 3: Terminal UI

```bash
sagefs tui
```

A four-pane terminal interface: editor, output, diagnostics, sessions. Navigate with Tab, manage sessions with keyboard shortcuts.

### Option 4: GUI

```bash
sagefs gui
```

A native GPU-rendered window (Raylib) with the same layout as the TUI.

### Option 5: Web Dashboard

Already running at `http://localhost:{port+1}/dashboard`. Submit code, view session status, manage sessions — all from the browser.

All of these connect to the **same daemon**. Open multiple at once — they all see the same state.

---

## Key Features

### 🔄 Hot Reloading

Save a `.fs` file → SageFs reloads it in ~100ms → running code is patched via [Harmony](https://github.com/pardeike/Harmony) → refresh your browser to see changes. No restart needed.

```fsharp
// Your web handler (in a Falco app):
let mutable handleHome (ctx: HttpContext) =
    task { do! ctx.Response.WriteAsync("<h1>Hello!</h1>") }

// Edit the file, save. SageFs auto-reloads.
// Refresh browser — updated response appears instantly.
```

### 📦 Project & Solution Support

```bash
sagefs --proj MyApp.fsproj       # Load one project
sagefs --sln MySolution.sln      # Load entire solution
sagefs                           # Auto-detect in current directory
```

SageFs loads all NuGet packages, project references, and namespaces automatically. No manual `#r` directives needed.

### 🤖 AI-Native (MCP Server)

SageFs speaks [Model Context Protocol](https://modelcontextprotocol.io/). AI agents get tools for:

| Tool | What it does |
|------|-------------|
| `send_fsharp_code` | Execute F# code (each `;;` is a transaction) |
| `check_fsharp_code` | Type-check without executing |
| `get_completions` | Code completions at cursor position |
| `explore_type` | Browse members of any .NET type |
| `explore_namespace` | Browse types in a namespace |
| `create_session` | Spin up a new isolated FSI session |
| `hard_reset_fsi_session` | Rebuild and reload (after code changes) |

[Full tool list →](#mcp-tools-reference)

### 👁️ File Watching

Source files are watched automatically. `.fs`/`.fsx` changes reload in ~100ms. Failed reloads are atomic — old definitions stay valid. Disable with `--no-watch`.

### 🔀 Multi-Session

Run multiple F# sessions simultaneously — different projects, different states. Create, switch, and stop sessions from any frontend.

---

## Common Workflows

### F# Web Development (Falco)

```bash
sagefs --proj MyWebApp.fsproj
```

Edit your handlers → save → SageFs hot-reloads → refresh browser. Sub-second feedback loop.

### AI-Assisted Development

Start SageFs, configure your AI tool's MCP settings, and your AI agent becomes a live F# development partner — it can execute code, check types, explore APIs, and run tests through SageFs.

### REPL-Driven Development

```bash
sagefs connect
```

Prototype functions, test ideas, explore APIs — with your full project loaded. Everything you'd do in `dotnet fsi` but with your actual project dependencies available.

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

# Options
sagefs --mcp-port 8080           # Custom MCP port
sagefs --no-watch                # Disable file watcher
sagefs --supervised              # Auto-restart on crash
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

### AI Agent Config

See [AI Agent Configuration](#option-1-ai-agent-mcp) above for Copilot CLI and Claude Desktop setup.

---

## Troubleshooting

**"SageFs daemon not found"** — Make sure the daemon is running (`sagefs --proj ...` in another terminal). Clients auto-discover via HTTP health check.

**"Session is still starting up"** — The FSI session is loading your project. Wait for the "ready" message. Large projects may take 30-60 seconds.

**Build errors after code changes** — If you changed `.fs` files and the REPL seems stale, run `hard_reset_fsi_session` (via MCP) or `#hard-reset` (in the REPL). This rebuilds and reloads.

**Port already in use** — Another SageFs instance is running. Use `sagefs stop` or `sagefs --mcp-port 8080`.

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
| `get_fsi_status` | Session health, loaded projects, statistics. |
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
              ┌───────────┐
              │  SageFs   │
              │  Daemon   │
              └──┬──┬──┬──┘
                 │  │  │
    ┌────────┐ ┌┴──┐ ┌┴────┐ ┌─────────┐
    │ REPL   │ │TUI│ │ GUI │ │Dashboard│
    │Connect │ │   │ │     │ │  (Web)  │
    └────────┘ └───┘ └─────┘ └─────────┘
                 │
            ┌────┴─────┐
            │ AI Agents│
            │  (MCP)   │
            └──────────┘
```

- **Daemon** — Runs FSI, MCP server, file watcher, hot reload, dashboard
- **Worker Sessions** — Isolated FSI processes per project
- **Clients** — REPL, TUI, GUI, dashboard, AI agents all connect over HTTP/SSE

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
- [Falco](https://github.com/pimbrouwers/Falco) & [Falco.Datastar](https://github.com/spiraloss/Falco.Datastar) — Dashboard framework
- [Harmony](https://github.com/pardeike/Harmony) — Runtime method patching for hot reload
- [Ionide.ProjInfo](https://github.com/ionide/proj-info/) — Project file parsing
- [Raylib-cs](https://github.com/ChrisDill/Raylib-cs) — GPU-rendered GUI
- [ModelContextProtocol](https://modelcontextprotocol.io/) — AI integration standard
