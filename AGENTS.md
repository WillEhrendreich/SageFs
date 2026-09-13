# SageFs — Coding Agent Guidelines

## STOP — Read This Before Anything Else

**The SageFs daemon is a long-running process that never exits on its own.** It hosts the FSI session, MCP server, and dashboard on ports 37749/37750. You will be tempted to wait for it. DO NOT.

**The cardinal rule, stated three times because it is the only thing you keep getting wrong:**

1. **NEVER narrate a wait to the user.** After starting a daemon, do not write "verifying" or "checking" or "started" and then stop. The next thing you produce must be a tool result — specifically a screenshot, an HTTP probe with a hard timeout, or the next concrete step. A chat message that is not a result or a question is the failure mode.

2. **NEVER call a command that blocks on the daemon's lifetime.** `Wait-Process`, `Start-Process -Wait`, waiting for a process to exit, `taskkill /T /F` on the daemon while also awaiting the result — all of these will hang forever because the daemon is not supposed to exit.

3. **NEVER treat `Start-Sleep` as "wait for the daemon to be ready" without a follow-up tool call in the same turn.** `Start-Sleep 3` followed by a chat message is the same hang, just shorter. `Start-Sleep 3` followed by a screenshot is fine. The sleep is not the problem. The text after the sleep is the problem.

**Concrete patterns:**

- Starting the daemon: one `Start-Process ... -WindowStyle Hidden` (no `-Wait`), one `Start-Sleep -Seconds 3` for warmup, then the next tool call is the screenshot. Nothing in between.
- Verifying the daemon is up: `Invoke-WebRequest -TimeoutSec 3` with a hard timeout. If it returns, great. If it throws a timeout exception, kill the request and report the state. Do not retry indefinitely.
- Killing the daemon: `Get-Process -Name "SageFs" | Stop-Process -Force` returns immediately. Never combine that with a `Wait-Process`.
- The "is it up" check is the screenshot. Not a chat message, not a sleep, not a status probe. The screenshot.

**If you catch yourself writing a sentence that contains "waiting", "let me check", "verifying", "starting up", or "should be ready"** between starting a process and your next tool call, stop. Skip the sentence. Make the tool call.

**You have failed this rule on the very first turn of this session, and on the turn immediately after being told about it, and on multiple turns after that. The next failure is a refusal to do the work, not a sentence of acknowledgment.**

## Project Overview

SageFs is an F# live development environment with editor integrations for VS Code and Neovim, a web dashboard, and an MCP server for agent and programmatic access. Its daemon architecture hosts persistent, isolated F# Interactive sessions.

The built-in SageTUI client, legacy TUI, and `SageFs.Gui` Raylib frontend are deprecated. Do not treat them as current product surfaces or add new product documentation for them. Preserve Raylib application and game demos because they demonstrate SageFs support for game projects and are independent of the deprecated GUI frontend.

The Visual Studio extension (`sagefs-vs/`) is deprecated and no longer built, tested, or published — do not treat it as a current product surface, do not add new product documentation for it, and do not route new engineering effort into it.

## Language & Stack

- **Primary language**: F# (functional programming)
- **Target framework**: `net10.0`
- **Solution format**: `.slnx` (not `.sln`)
- **Web framework**: Falco (functional web framework for ASP.NET Core)
- **HTML rendering**: Falco.Markup
- **Real-time UI**: Falco.Datastar (SSE-based)
- **Testing**: Expecto (behavior-driven, property-based with FsCheck)
- **Snapshot testing**: Verify
- **Persistence**: Binary manifest format (.sagefm) for session and test state
- **Package management**: Central package management via `Directory.Packages.props`

## Critical Coding Standards

### Indentation
- **ALWAYS use 2 spaces**, never 4 spaces — this is non-negotiable across the entire codebase.

### Package References
- **NEVER** include `Version` attributes in `<PackageReference>` elements in `.fsproj` files.
- All versions are defined centrally in `Directory.Packages.props` at the repo root.

### Commit Messages
- Use **Conventional Commits** format: `type(scope): description`
- Types: `feat`, `fix`, `refactor`, `docs`, `test`, `chore`, `perf`, `style`, `ci`, `build`

### F# Style
- Favor immutable types, discriminated unions, pattern matching, and pipeline operators (`|>`)
- Use `Result<'T, 'TError>` for operations that can fail
- Keep domain logic pure — side effects only at system edges
- Small, composable functions with clear intent

### Testing
- Tests use Expecto with `Expecto.Flip` — **message is always the first argument**:
  ```fsharp
  actual |> Expect.equal "should be 42" 42
  actual |> Expect.isTrue "should be true"
  ```
- Run tests via the SageFs REPL, not `dotnet test`
- Property-based tests (FsCheck) are preferred over example-based tests

## Project Structure

```
SageFs.Core/       — Shared engine, session, testing, persistence, and protocol logic
SageFs/            — CLI tool, daemon, MCP server, dashboard, and retained deprecated TUI source
SageFs.Gui/        — Deprecated Raylib product frontend retained as legacy source
SageFs.Tests/      — Expecto test project
sagefs-vscode/     — VS Code extension (Fable F#→JS)
sagefs-vs/         — Deprecated Visual Studio extension (C# + F#), retained as legacy source
docs/              — GitHub Pages site
```

The Neovim plugin lives in a separate repo: `WillEhrendreich/sagefs.nvim`.

## Build & Test

```bash
dotnet build           # Build all projects
dotnet test            # Run tests (CI only — prefer SageFs REPL locally)
dotnet pack SageFs -o nupkg  # Package the CLI tool
```

## Multi-agent / worktree sessions

- **Sessions are checkout-aware.** A session's working directory is classified against the filesystem (`SageFs.Checkout.classify`, no `git` subprocess): a plain repository, a git **worktree** (its own root and branch — worktrees have a `.git` FILE, not a directory, pointing at the main checkout's `.git/worktrees/<name>` admin dir), or not a git checkout at all. `list_sessions` and the dashboard show a worktree session's branch.
- **A git worktree is a routing boundary.** If you are working inside a worktree (e.g. `.claude/worktrees/agent-x`) and no session exists for it yet, tool calls resolve to `Gone` with a create hint — they never silently fall back to a session rooted at the main checkout, even though your directory is textually nested under it. Create a session for the worktree; do not assume the main checkout's session is yours to use.
- **For a project the running daemon already serves, create a session in it.** Only spawn a second daemon when you are testing daemon code itself (changes to `SageFs.Core`/`SageFs`/`SageFs.Host`) that the running daemon cannot execute because it predates your change — and then give that daemon an explicit owner/TTL rather than leaving it to leak.
- **Identity is bound to your MCP connection, not to the `agentName` you pass.** Two different connections that happen to declare the same `agentName` are tracked as two separate members — you cannot see or clear another connection's active session by reusing its name.

## Architecture Principles

- **Current clients**: VS Code, Neovim, the web dashboard, and MCP use session-scoped daemon contracts
- **Web dashboard**: Falco.Datastar and SSE provide browser-based session control and observability
- **Binary persistence**: Session/test state via CRC-validated binary manifest (.sagefm)
- **CQRS**: Separate read/write models
- **Vertical slices**: Features as single files for locality of behavior
- **Daemon architecture**: Long-running FSI session with MCP server for editor communication

## Things to Avoid

- Do not introduce new NuGet dependencies without discussion
- Do not change the indentation style (2 spaces)
- Do not use `dotnet test` for local development — use the SageFs REPL
- Do not modify `Directory.Build.props` version numbers — the pre-commit hook handles versioning
- Do not add Version attributes to PackageReference elements
