# Contributing to SageFs

Welcome! SageFs is an open-source project and we genuinely appreciate contributions — whether it's fixing a typo, improving docs, filing a bug, or building a whole new feature. If you're from the F# community and want to help, you're in the right place.

## Quick Links

| What | Where |
|:---|:---|
| Report a bug | [GitHub Issues](https://github.com/WillEhrendreich/SageFs/issues/new?labels=bug) |
| Suggest a feature | [GitHub Issues](https://github.com/WillEhrendreich/SageFs/issues/new?labels=enhancement) |
| Ask a question | [GitHub Discussions](https://github.com/WillEhrendreich/SageFs/discussions) or open an issue |
| Code standards | [AGENTS.md](AGENTS.md) |

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (see `global.json` for exact version)
- Git
- An editor — VS Code with Ionide, Neovim, Visual Studio, or your preference

### Clone and Build

```bash
git clone https://github.com/WillEhrendreich/SageFs.git
cd SageFs
dotnet build
```

That's it. No Docker required, no external databases, no npm install for the core project. SageFs is a single `dotnet build` away from running.

### Build Script (Alternative)

For a full build including the MCP SDK dependency, tests, packaging, and editor extensions:

```bash
dotnet fsi build.fsx              # fetch MCP SDK + build
dotnet fsi build.fsx -- test      # build + run tests
dotnet fsi build.fsx -- install   # build + pack + install as global tool
dotnet fsi build.fsx -- ext       # build + package + install editor extensions
dotnet fsi build.fsx -- all       # everything
```

### Install Your Local Build

```bash
dotnet pack SageFs -o nupkg
dotnet tool install --global SageFs --add-source ./nupkg --no-cache
```

Now `sagefs` on your PATH is your locally-built version.

### Run It

```bash
# Point SageFs at any F# project
sagefs --proj SageFs.Tests/SageFs.Tests.fsproj

# Or use the TUI, GUI, or dashboard
sagefs tui
sagefs gui
# Dashboard auto-opens at http://localhost:37750/dashboard
```

## Project Structure

```
SageFs.Core/       — Shared types, rendering abstraction, Elm model (start here!)
SageFs/            — CLI tool, daemon, TUI client
SageFs.Gui/        — Raylib GPU-rendered GUI client
SageFs.Tests/      — Expecto test project (2900+ tests)
sagefs-vscode/     — VS Code extension (F# via Fable → JavaScript)
sagefs-vs/         — Visual Studio extension (C# shim + F# core)
docs/              — GitHub Pages documentation site
```

The Neovim plugin lives in a separate repo: [sagefs.nvim](https://github.com/WillEhrendreich/sagefs.nvim).

**Good starting points for reading code:**
- `SageFs.Core/TerminalUI.fs` — the `Cell`, `CellGrid`, `Draw`, and `Theme` types that both TUI and GUI share
- `SageFs.Core/RenderPipeline.fs` — how the Elm model becomes screen output
- `SageFs.Tests/` — the test project shows how every module is exercised

## Debugging SageFs

This is the section your friend probably wants. Here's how to actually debug and develop SageFs day-to-day.

### The Development Loop

SageFs is its own development environment. The recommended workflow is:

```
1. Run SageFs against the test project
2. Use the live FSI session to iterate on code
3. Write tests in the REPL, see them fail, make them pass
4. Save proven code to .fs files
5. Rebuild and verify
```

### Step-by-Step: Your First Debugging Session

**1. Start SageFs against its own test project:**

```bash
sagefs --proj SageFs.Tests/SageFs.Tests.fsproj
```

This loads all project dependencies into a live F# Interactive session with hot reload.

**2. Connect your editor.** SageFs exposes an MCP server at `http://localhost:37749/sse`. If you're using VS Code with the SageFs extension, it auto-connects. For other editors, see the [README](Readme.md) for setup.

**3. Edit a `.fs` file and save.** SageFs detects the change (~500ms debounce), reloads the file via `#load` (~100ms), and if you have live testing enabled, affected tests re-run automatically.

**4. Run tests from the SageFs REPL** (not `dotnet test`):

```fsharp
// Run a specific test module
Expecto.Tests.runTestsWithCLIArgs [] [||] SageFs.Tests.SomeModule.tests;;

// Run all tests
Expecto.Tests.runTestsWithCLIArgs [] [||] SageFs.Tests.AllTests.tests;;
```

**5. Check test output** in the SageFs console window. Exit code 0 = all passed. Exit code 2 = passed but no TTY detected (cosmetic, ignore it). Exit code 1 = actual failures.

### Debugging with Breakpoints

For traditional breakpoint debugging:

```bash
# Build in Debug configuration (default)
dotnet build

# Attach your debugger to the SageFs process, or:
# Run the test project directly with a debugger attached
dotnet run --project SageFs.Tests -- --filter "test name"
```

VS Code: Use the built-in .NET debugger. Create a `launch.json` that targets `SageFs.Tests.dll`.

Visual Studio / Rider: Open `SageFs.slnx`, set `SageFs.Tests` as the startup project, and hit F5.

### The Pack/Reinstall Cycle

When you change SageFs's own source code (anything in `SageFs/` or `SageFs.Core/`), you need to rebuild and reinstall:

```bash
# Stop the running instance, rebuild, repackage, reinstall
dotnet build && dotnet pack SageFs -o nupkg
dotnet tool update --global SageFs --add-source ./nupkg --no-cache
```

Then restart SageFs. If you only changed test code, a simpler rebuild is enough — no reinstall needed.

### Viewing Logs

- **Daemon console** — real-time output in the terminal where SageFs is running
- **Dashboard** — `http://localhost:37750/dashboard` shows session state, events, test results
- **Log files** — `sagefs-stderr.log`, `sagefs-trace.log` in the working directory
- **OpenTelemetry** — start with `start-sagefs-otel.bat` for structured traces

## Running Tests

SageFs uses [Expecto](https://github.com/haf/expecto) for testing, with [FsCheck](https://github.com/fscheck/FsCheck) for property-based tests and [Verify](https://github.com/VerifyTests/Verify) for snapshot tests.

```bash
# Quick: run all tests via build script
dotnet fsi build.fsx -- test

# Direct: run the test project
dotnet run --project SageFs.Tests -- --summary

# Filter: run specific tests
dotnet run --project SageFs.Tests -- --filter "CellGrid"
```

For local development, prefer running tests inside SageFs's own REPL for instant feedback.

### Test Categories

Tests are auto-categorized:
- **Unit** — pure logic, runs on every change
- **Integration** — needs external resources, runs on save
- **Browser** — Playwright .NET tests, runs on demand
- **Property** — FsCheck generative tests
- **Benchmark** — performance tests

## Coding Standards

The full coding standards are in [AGENTS.md](AGENTS.md). Here are the essentials:

### The Non-Negotiables

- **2 spaces for indentation** — not 4, not tabs. The entire codebase uses 2 spaces.
- **Conventional Commits** — `feat(core): add cell merging`, `fix(tui): handle resize crash`, `docs: update contributing guide`
- **No `Version` in PackageReference** — all NuGet versions live in `Directory.Packages.props`

### F# Style

```fsharp
// ✅ Pattern matching, not if/else
match user.Role with
| Admin -> doAdmin()
| Regular -> doRegular()

// ✅ Result for errors, not Option or bool
let validate input : Result<ValidInput, ValidationError> = ...

// ✅ Immutable records with { with } for updates
let updated = { user with Name = newName }

// ✅ Pipeline operators
input |> validate |> Result.map transform |> Result.mapError formatError

// ✅ Small, composable functions in modules
module User =
  let create name email = { Name = name; Email = email }
  let rename newName user = { user with Name = newName }
```

### Testing Style (Expecto.Flip)

Message is **always the first argument**:

```fsharp
actual |> Expect.equal "should be 42" 42
actual |> Expect.isTrue "should be true"
list |> Expect.hasLength "should have 3 items" 3
```

## Making a Pull Request

### Before You Start

1. **Check existing issues** — someone may already be working on it
2. **Open an issue first** for large changes — let's discuss the approach before you invest time
3. **Small PRs are better** — easier to review, faster to merge

### PR Workflow

1. Fork the repo and create a branch: `git checkout -b feat/my-feature`
2. Make your changes with tests
3. Ensure `dotnet build` succeeds with no warnings (warnings are errors)
4. Run `dotnet fsi build.fsx -- test` to verify tests pass
5. Commit with conventional commit messages
6. Push and open a PR against `main`

### What Makes a Great PR

- **Tests included** — new features need tests, bug fixes need a regression test
- **Small and focused** — one logical change per PR
- **Clear description** — what changed, why, and how to verify
- **Passes CI** — green build, no new warnings

### What We'll Review

- Does it follow F# idioms? (pattern matching, immutability, composition)
- Does it have tests?
- Does it use 2-space indentation?
- Does it affect multiple editor plugins? (VS Code, Neovim, Visual Studio, TUI, GUI)
- Are commit messages conventional?

## Good First Contributions

Not sure where to start? Here are some areas where help is especially welcome:

- **Documentation** — improve docs, add examples, fix typos
- **Test coverage** — add property-based tests, improve edge case coverage
- **Snapshot tests** — add Verify snapshot tests for rendered output
- **Bug fixes** — check the issue tracker for bugs labeled `good-first-issue`
- **Error messages** — make diagnostics clearer and more helpful
- **FSI quirks** — `SageFs.Core/FsiRewrite.fs` is ~25 lines and handles FSI edge cases — PRs welcome

## Architecture Overview

SageFs is **daemon-first** — one long-running server, many clients:

```
                ┌───────────────┐
                │  SageFs Daemon│
                │  ┌─────────┐  │
                │  │ FSI Actor│  │  ← F# Interactive session
                │  └─────────┘  │
                │  ┌─────────┐  │
                │  │  File    │  │  ← watches .fs/.fsx changes
                │  │ Watcher  │  │
                │  └─────────┘  │
                │  ┌─────────┐  │
                │  │  MCP     │  │  ← AI + editor communication
                │  │ Server   │  │
                │  └─────────┘  │
                └──┬──┬──┬──┬───┘
                   │  │  │  │
     ┌──────┐  ┌──┴┐ ┌┴──┐ ┌┴──────┐  ┌────────┐
     │VS Code│  │TUI│ │GUI│ │ Web   │  │AI Agent│
     └──────┘  └───┘ └───┘ │ Dash  │  │ (MCP)  │
     ┌──────┐  ┌───────┐   └───────┘  └────────┘
     │Neovim│  │ REPL  │
     └──────┘  └───────┘
```

Key architectural concepts:
- **Dual-renderer** — TUI and Raylib GUI share the same `Cell[,]` grid abstraction
- **Elm architecture** — unidirectional data flow: Model → View → Update
- **Worker isolation** — each FSI session runs in an isolated sub-process (Erlang-style)
- **SSE for reads** — all state changes push to clients via Server-Sent Events
- **POST for commands** — write operations are POST-only, return acknowledgment only

## Questions?

- Open a [Discussion](https://github.com/WillEhrendreich/SageFs/discussions) for general questions
- Open an [Issue](https://github.com/WillEhrendreich/SageFs/issues) for bugs or feature requests
- PRs are always welcome — even small ones

Thank you for contributing! 🎉
