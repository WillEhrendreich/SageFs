# SageFs — Coding Agent Guidelines

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
- **Database**: PostgreSQL with Marten (event sourcing)
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
SageFs.Core/       — Shared types, rendering abstraction, Elm model
SageFs/            — CLI tool (TUI client + daemon)
SageFs.Gui/        — Raylib GUI client
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

- **Dual-renderer**: TUI and Raylib GUI share the same `Cell[,]` grid abstraction
- **Event sourcing**: State changes modeled as events (Marten)
- **CQRS**: Separate read/write models
- **Vertical slices**: Features as single files for locality of behavior
- **Daemon architecture**: Long-running FSI session with MCP server for editor communication

## Things to Avoid

- Do not introduce new NuGet dependencies without discussion
- Do not change the indentation style (2 spaces)
- Do not use `dotnet test` for local development — use the SageFs REPL
- Do not modify `Directory.Build.props` version numbers — the pre-commit hook handles versioning
- Do not add Version attributes to PackageReference elements
