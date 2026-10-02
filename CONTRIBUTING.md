# Contributing to SageFs

Welcome. SageFs is open source and I'm glad of any help, whether that's a typo, a doc fix, a bug report or a whole feature. If you're from the F# community and want to pitch in, you're in the right place.

## Quick Links

| What | Where |
|:---|:---|
| Report a bug | [GitHub Issues](https://github.com/WillEhrendreich/SageFs/issues/new?labels=bug) |
| Suggest a feature | [GitHub Issues](https://github.com/WillEhrendreich/SageFs/issues/new?labels=enhancement) |
| Ask a question | [GitHub Discussions](https://github.com/WillEhrendreich/SageFs/discussions) or open an issue |
| Code standards | [AGENTS.md](AGENTS.md) |

## Getting Started

### Prerequisites

- The .NET SDK that `global.json` pins, which is a .NET 11 release candidate (`11.0.100-rc.1.26425.128`, with `allowPrerelease` on) as I write this. The repo builds for `net11.0` by default, and the shipped tool (`SageFs`, `SageFs.Core`, `SageFs.Host`) multi-targets `net10.0;net11.0` so a .NET 10 user can install it. Get the SDK from [dotnet.microsoft.com/download/dotnet/11.0](https://dotnet.microsoft.com/download/dotnet/11.0)
- Git
- An editor — VS Code with Ionide, Neovim, Rider, or your preference

### Clone and Build

```bash
git clone https://github.com/WillEhrendreich/SageFs.git
cd SageFs
dotnet fsi build.fsx
```

The build script (one step) clones the [forked MCP SDK](https://github.com/WillEhrendreich/ModelContextProtocolSdk), packs it into a local `mcp-sdk-nupkg/` directory, and builds the solution. This is necessary because `nuget.config` points at that local directory as a package source. It also lists `harmony-nupkg/` as a source. That one holds the packed Harmony fork (`SageFs.Harmony`, built from [LibHarmony](https://github.com/WillEhrendreich/LibHarmony) with a patched MonoMod so detours work on .NET 11), and the package is checked in, so there is nothing to fetch for it.

> **Why a forked MCP SDK?** SageFs depends on a fork of `ModelContextProtocol` with additional features not yet upstream. The fork is public. The build script clones and packs it automatically, so there are no manual steps.

After the first run, `dotnet build` works normally (the `mcp-sdk-nupkg/` directory persists). If you get `NU1301: The local source 'mcp-sdk-nupkg' doesn't exist`, re-run `dotnet fsi build.fsx` to regenerate it.

### Build Script

```bash
dotnet fsi build.fsx              # fetch MCP SDK + build (first-time setup)
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
# Start the daemon. It starts bare and waits for a client to create a session
sagefs

# Create a session for any F# project from an editor integration, an MCP client or the dashboard
# Dashboard: http://localhost:37750/dashboard
```

## Project Structure

```
SageFs.Core/       — Shared engine, session, testing, persistence, and protocol logic (start here!)
SageFs/            — CLI tool, daemon, MCP server, dashboard, plus retained deprecated TUI source
SageFs.Gui/        — Deprecated Raylib product frontend retained as legacy source
SageFs.Host/       — The worker process the daemon spawns per session
SageFs.FsiHost/    — The isolated FSI host: the FSI session, your running code, the hot reload and live testing agent
SageFs.Simulation/ — Deterministic simulation (DST) models that fold the real cores
SageFs.Tests/      — Expecto test project (thousands of tests; the README badge is auto-derived)
sagefs-vscode/     — VS Code extension (F# via Fable → JavaScript)
sagefs-vs/         — Deprecated Visual Studio extension (C# + F#), retained as legacy source; not built or published
docs/              — User docs, design decisions, troubleshooting and feature references
```

The Neovim plugin lives in a separate repo: [sagefs.nvim](https://github.com/WillEhrendreich/sagefs.nvim).

The built-in SageTUI client, legacy TUI, and `SageFs.Gui` Raylib frontend are deprecated. Do not extend them as current product surfaces. Raylib application and game demos remain valuable examples of SageFs game-project support and should be preserved.

**Good starting points for reading code:**
- `SageFs/DaemonMode.fs` — daemon composition and client routing
- `SageFs/Dashboard.fs` — current browser dashboard
- `SageFs/McpServer.fs` and `SageFs/McpTools.fs` — MCP transport and tools
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
sagefs
```

Then create a session for `SageFs.Tests/SageFs.Tests.fsproj` from your editor, MCP client, or the dashboard. That session loads the project into a live F# Interactive session with hot reload.

**2. Connect your editor.** SageFs serves MCP at `http://localhost:37749/` (streamable HTTP) and `http://localhost:37749/sse` (legacy SSE). If you're using VS Code with the SageFs extension, it auto-connects. For other editors and for an agent (`claude mcp add sagefs -- sagefs mcp`), see the [README](Readme.md) and [docs/agents.md](docs/agents.md).

**3. Edit a `.fs` file and save.** SageFs notices the change (the watcher waits 200 ms for a burst of write events to settle), brings the session up to date, and if you have live testing enabled, the affected tests re-run. In the Hot Reload workflow the changed functions are patched into the running process instead. [docs/hot-reload.md](docs/hot-reload.md) says what patches and what restarts.

**4. Run tests from the SageFs session** (not `dotnet test`). The `run_tests` MCP tool, the dashboard and your editor all ask the same live-testing engine. To run one module from the REPL:

```fsharp
Expecto.Tests.runTestsWithCLIArgs [] [||] SageFs.Tests.SomeModule.tests;;
```

> **Signature note:** `runTestsWithCLIArgs` takes `(cliArguments: string list, argv: string[], test: Test)`, and the **third argument is a single `Test` value**, not an array. A `[<Tests>]` module binding like `SomeModule.tests` is already a single combined `Test`; do NOT wrap it in `[| ... |]`. Passing an array lands it in the `argv` slot and produces the confusing error `expected string but got Test`.

**5. Check the verdict, not the colour.** The direct runner ends with a `TRUST` line: `Trusted` (unfiltered, everything registered ran, nothing failed), `NarrowedRun` (it passed, but a filter narrowed it), `TestsFailed`, `NothingRan`, or `CountMismatch`. The last two exit with 3, because a run that executed nothing is not a green run. A filtered run is never the acceptance check. The table and the filter traps are in [AGENTS.md](AGENTS.md#filters---filter-test-list-matches-lists---filter-test-case-matches-leaves).

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

### Performance guards

Performance is guarded locally, in the normal test run — not by a separate CI
job on noisy shared runners. The heavyweight BenchmarkDotNet suite was removed:
it gated releases on microbenchmark variance for paths no user feels (the
deprecated TUI, features with no product consumer), while the one benchmark that
measured a real hot path — the per-eval binding-scope rebuild — was never in the
threshold gate at all.

In its place, `PerfTests.fs` carries lightweight guards that run every time you
run the suite. They assert **algorithmic scaling ratios** (`PerfBudget.fs`)
rather than absolute wall-clock budgets: the ratio of a large-workload cost to a
small one isolates the algorithm's growth and is independent of how fast or busy
the machine is, so the check is meaningful locally and never flakes. The current
guard proves `recordEval` stays sub-linear as the eval history grows (the O(n^2)
regression the roast flagged). Add a new guard the same way when you touch a
genuine hot path; do heavier one-off profiling ad hoc in the REPL.

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
- **Log files** — the daemon writes `mcp-server<yyyyMMdd>.log` and each session's worker writes `<data dir>/workers/<sessionId>.log`. `GET http://localhost:37750/api/daemon-info` returns the daemon log's path as `logPath`. See [Where the logs are](docs/TROUBLESHOOTING.md#where-the-logs-are).
- **OpenTelemetry** — start with `start-sagefs-otel.bat` (a Windows batch file) for structured traces, or set `OTEL_EXPORTER_OTLP_ENDPOINT` yourself on any platform

## Running Tests

SageFs uses [Expecto](https://github.com/haf/expecto) for testing, with [FsCheck](https://github.com/fscheck/FsCheck) for property-based tests and [Verify](https://github.com/VerifyTests/Verify) for snapshot tests.

```bash
# Quick: run all tests via build script
dotnet fsi build.fsx -- test

# Direct: run the test project
dotnet run --project SageFs.Tests -- --summary

# Filter: run specific tests (the inner loop only, see the TRUST note above)
dotnet run --project SageFs.Tests -- --filter "CellGrid"
```

For local development, prefer running tests inside SageFs's own REPL for instant feedback.

`--summary` runs the default suite. The integration, browser and mutation tiers each have their own entry point (`--integration-host`, `--integration-browser` and so on), and `ci-pipeline.fsx` runs every one of them and prints a trust report. A pull request runs the whole pipeline on a Linux runner.

### Test Categories

Tests are auto-categorized:
- **Unit** — pure logic, runs on every change
- **Integration** — needs external resources, runs on demand by default
- **Browser** — Playwright .NET tests, runs on demand
- **Property** — FsCheck generative tests
- **Benchmark** — performance tests

## Coding Standards

The full coding standards are in [AGENTS.md](AGENTS.md). Here are the essentials:

### The Non-Negotiables

- **2 spaces for indentation** — not 4, not tabs. The entire codebase uses 2 spaces.
- **Conventional Commits** — `feat(core): add session routing`, `fix(dashboard): handle reconnect`, `docs: update contributing guide`
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
6. Push and open a PR against `master`

### What Makes a Great PR

- **Tests included** — new features need tests, bug fixes need a regression test
- **Small and focused** — one logical change per PR
- **Clear description** — what changed, why, and how to verify
- **Passes CI** — green build, no new warnings

### What We'll Review

- Does it follow F# idioms? (pattern matching, immutability, composition)
- Does it have tests?
- Does it use 2-space indentation?
- Does it affect multiple current clients? (VS Code, Neovim, web dashboard, MCP)
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
     ┌───────┐ ┌────┴──┐ ┌┴──────┐  ┌──────────┐
     │VS Code│ │Neovim │ │ Web   │  │MCP Client│
     └───────┘ └───────┘ │ Dash  │  └──────────┘
                          └───────┘
```

Key architectural concepts:
- **Thin clients** — editors, dashboard tabs, and MCP clients use the same session-scoped daemon contracts
- **Web dashboard** — browser operations and state updates use Falco.Datastar and SSE
- **Worker isolation** — each FSI session runs in an isolated sub-process (Erlang-style)
- **SSE for reads** — all state changes push to clients via Server-Sent Events
- **POST for commands** — write operations are POST-only, return acknowledgment only

## Questions?

- Open a [Discussion](https://github.com/WillEhrendreich/SageFs/discussions) for general questions
- Open an [Issue](https://github.com/WillEhrendreich/SageFs/issues) for bugs or feature requests
- PRs are always welcome — even small ones

Thank you for contributing! 🎉
