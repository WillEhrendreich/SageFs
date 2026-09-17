# 🏗️ Architecture

One SageFs daemon runs per machine. It starts with no project loaded and creates sessions on demand. Each session is a separate OS worker process with its own FSI, loaded project assemblies, and file watcher. VS Code, Neovim, the web dashboard, and MCP clients all talk to the daemon through session-scoped HTTP and SSE contracts. See the [architecture diagram](../Readme.md#-one-daemon-every-client--simultaneously) for how clients connect.

The daemon listens on port 37749 for MCP (streamable HTTP at `/`, legacy SSE at `/sse`) and the editor state stream (`/events`). The web dashboard runs on port 37750 at `/dashboard`. Target framework is net10.0; the solution file is `SageFs.slnx`.

The test suite uses Expecto unit tests, FsCheck property-based state-machine tests, Verify snapshots, and binary-persistence property tests. The current test count is auto-derived into the README badge.

## Project Structure

```
SageFs.Core/       — Shared engine, session, testing, persistence, and protocol logic
SageFs/            — CLI tool, daemon, MCP server, dashboard, and retained legacy TUI source
SageFs.Gui/        — Deprecated Raylib product frontend retained as legacy source
SageFs.Tests/      — Expecto test project
sagefs-vscode/     — VS Code extension (Fable F#→JS)
sagefs-vs/         — Deprecated Visual Studio extension (C# + F#), retained as legacy source
docs/              — GitHub Pages site
```

The Neovim plugin lives in a separate repo: [sagefs.nvim](https://github.com/WillEhrendreich/sagefs.nvim).

## Client Pipeline

Current clients use the daemon as the source of truth:

```
Editor / Dashboard / MCP command
  → session-scoped daemon endpoint
    → isolated FSI worker
      → structured result and SSE state updates
```

The built-in SageTUI client, legacy TUI, and `SageFs.Gui` Raylib frontend are deprecated and are not current product interfaces; their rendering code stays in the repository as legacy history. Raylib application and game demos are still supported examples of using SageFs with game projects, and they don't depend on the deprecated SageFs GUI frontend.

## Session Lifecycle

1. Daemon starts with no project and no session loaded
2. A client creates a session with a project path
3. The daemon spawns a worker sub-process, loads the project, starts watching files
4. Clients send code, read diagnostics, and run tests, all through the daemon
5. Multiple clients can connect to the same session simultaneously

## FSI Quirks & Rewrites

SageFs automatically rewrites `use` to `let` inside nested scopes (functions, computation expressions) because FSI doesn't support `use` there. This means disposables aren't automatically disposed in the REPL. That's fine for quick experiments, but keep it in mind for long sessions.

Other FSI behaviors: redefining a binding shadows it instead of erroring, each `;;` boundary is its own transaction, there's no `[<EntryPoint>]`, and assembly loading is scoped to the session.

Rewrite logic: [`SageFs.Core/FsiRewrite.fs`](../SageFs.Core/FsiRewrite.fs) (~25 lines). PRs welcome.
