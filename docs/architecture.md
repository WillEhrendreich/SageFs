# 🏗️ Architecture

One SageFs daemon runs per machine. It starts with no project loaded, and creates sessions on demand. I didn't
want a daemon that assumes it knows what you're working on before you've told it. Each session is a separate OS
worker process with its own FSI, loaded project assemblies, and file watcher. VS Code, Neovim, the web
dashboard, and MCP clients all talk to the daemon through session-scoped HTTP and SSE contracts. See the
[architecture diagram](../Readme.md#-one-daemon-every-client) for how clients connect.

The daemon listens on port 37749 for MCP (streamable HTTP at `/`, legacy SSE at `/sse`) and the editor state
stream (`/events`). The web dashboard runs on port 37750 at `/dashboard`. Target framework is net10.0; the
solution file is `SageFs.slnx`.

The test suite uses Expecto unit tests, FsCheck property-based state-machine tests, Verify snapshots, and
binary-persistence property tests. The README's test-count badge and property-test count are derived from
source, never hand-typed, but restamping is an explicit step (`dotnet run --project SageFs.Tests -- --update-badge`,
see `SageFs.Tests/TestCountBadge.fs`), and CI or a normal test run doesn't do it for you, so the
numbers can lag between restamps. If you spot a stale number, that's why.

## Project Structure

```
SageFs.Core/       — Shared engine, session, testing, persistence, and protocol logic
SageFs/            — CLI tool, daemon, MCP server, and dashboard
SageFs.Tests/      — Expecto test project
sagefs-vscode/     — VS Code extension (Fable F#→JS)
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

The Raylib application and game demos are there because they prove SageFs works for real projects
outside web dev too.

## Session Lifecycle

1. Daemon starts with no project and no session loaded
2. A client creates a session with a project path
3. The daemon spawns a worker sub-process, loads the project, starts watching files
4. Clients send code, read diagnostics, and run tests, all through the daemon
5. Multiple clients can connect to the same session simultaneously

## FSI Quirks & Rewrites

SageFs automatically rewrites `use` to `let` inside nested scopes (functions, computation expressions) because
FSI doesn't support `use` there. That means disposables aren't automatically disposed in the REPL. Fine for
quick experiments; worth remembering if you're leaning on a `use` binding to clean something up in a long
session.

Other FSI behaviors worth knowing: redefining a binding shadows it instead of erroring, each `;;` boundary is
its own transaction, there's no `[<EntryPoint>]`, and assembly loading is scoped to the session.

Rewrite logic: [`SageFs.Core/FsiRewrite.fs`](../SageFs.Core/FsiRewrite.fs) (26 lines, genuinely small).
PRs welcome.
