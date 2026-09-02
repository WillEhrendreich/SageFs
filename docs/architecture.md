# 🏗️ Architecture

SageFs is **daemon-first**: one server, many clients. The daemon starts bare and creates sessions on demand. Each session is an **isolated worker sub-process** with its own FSI, project, and file watcher. VS Code, Neovim, Visual Studio, the web dashboard, and MCP clients communicate with the daemon through session-scoped HTTP and SSE contracts. See the [architecture diagram](../Readme.md#-one-daemon-every-client--simultaneously) for how clients connect.

5700+ tests: Expecto unit tests, FsCheck property-based state machine tests, Verify snapshots, binary persistence property tests.

## Project Structure

```
SageFs.Core/       — Shared engine, session, testing, persistence, and protocol logic
SageFs/            — CLI tool, daemon, MCP server, dashboard, and retained legacy TUI source
SageFs.Gui/        — Deprecated Raylib product frontend retained as legacy source
SageFs.Tests/      — Expecto test project
sagefs-vscode/     — VS Code extension (Fable F#→JS)
sagefs-vs/         — Visual Studio extension (C# + F#)
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

The built-in SageTUI client, legacy TUI, and `SageFs.Gui` Raylib frontend are deprecated and are not current product interfaces. Their rendering code remains in the repository as legacy implementation history. Raylib application and game demos remain valuable, supported examples of using SageFs with game projects; they do not depend on the deprecated SageFs GUI frontend.

## Session Lifecycle

1. Daemon starts bare — no project, no session
2. A client creates a session with a project path
3. The daemon spawns a worker sub-process, loads the project, starts watching files
4. Clients send code, read diagnostics, run tests — all through the daemon
5. Multiple clients can connect to the same session simultaneously

## FSI Quirks & Rewrites

SageFs auto-rewrites `use` → `let` inside nested scopes (functions, CEs) because FSI doesn't support `use` in those positions. This means disposables aren't auto-disposed in the REPL — fine for experiments, be aware for long sessions.

Other FSI behaviors: redefinition shadows (doesn't error), `;;` boundaries are independent transactions, no `[<EntryPoint>]`, assembly loading is session-scoped.

Rewrite logic: [`SageFs.Core/FsiRewrite.fs`](../SageFs.Core/FsiRewrite.fs) (~25 lines). PRs welcome.
