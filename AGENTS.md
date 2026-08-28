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

SageFs is an F# live development environment — a REPL-powered tool with editor integrations for VS Code, Visual Studio, Neovim, plus a built-in TUI and Raylib GUI. It uses an MCP server for editor communication and a daemon architecture for persistent F# Interactive sessions.

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
SageFs.Core/       — Shared types, rendering abstraction, KeyMap, Theme
SageFs/            — CLI tool, daemon, SageTUI client (SageTuiClient.fs) + legacy TUI (TuiClient.fs)
SageFs.Gui/        — Raylib GUI client (Cell[,] grid renderer)
SageFs.Tests/      — Expecto test project
sagefs-vscode/     — VS Code extension (Fable F#→JS)
sagefs-vs/         — Visual Studio extension (C# + F#)
docs/              — GitHub Pages site
```

The Neovim plugin lives in a separate repo: `WillEhrendreich/sagefs.nvim`.

## Build & Test

```bash
dotnet build           # Build all projects
dotnet test            # Run tests (CI only — prefer SageFs REPL locally)
dotnet pack SageFs -o nupkg  # Package the CLI tool
```

## Architecture Principles

- **TUI via SageTUI**: Terminal UI uses [SageTUI](https://github.com/WillEhrendreich/sagetui) Elm Architecture (`Program<Model,Msg>` with `init/update/view/subscribe`), SIMD cell diff, zero-GC rendering. Classic `CellGrid` imperative renderer available as `--legacy-tui` fallback.
- **Raylib GUI**: GPU-rendered client uses `Cell[,]` grid abstraction with `RaylibEmitter`
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
