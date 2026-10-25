# SageFs Samples

Learn F# interactively with SageFs! Each sample is a proper .NET project
you can open, build, and run, or use SageFs for instant inline feedback.

## Quick Start

```bash
# Run the F# Koans:
cd samples/from-koans/SageFs.Samples.Koans
dotnet run

# Or use SageFs for live feedback: start the daemon, then create a session
# for the project from your editor, the dashboard or an MCP client.
sagefs
```

## Sample Projects

### 🧘 [F# Koans](from-koans/): Learn F# from Scratch

Progressive exercises teaching F# fundamentals, from `let` bindings
to discriminated unions and data pipelines. Adapted from
[FSharpKoans](https://github.com/ChrisMarinos/FSharpKoans) for SageFs.
The project is `from-koans/SageFs.Samples.Koans/`, and its README lists the exercises.

### 🌉 Language Bridges: Coming from Another Language?

Each bridge shows F# equivalents for patterns you already know, as tests
you can run. Pick the one that matches your background:

```
from-csharp/SageFs.Samples.FromCSharp/          records, unions, option, result, pipelines
from-python/SageFs.Samples.FromPython/          immutable bindings, functions, pipelines, pattern matching
from-java/SageFs.Samples.FromJava/              records, unions, option, generics, pipelines
from-javascript/SageFs.Samples.FromJavaScript/  bindings, functions, records, unions, modules
from-rust/SageFs.Samples.FromRust/              enums as unions, option, result, records
from-jupyter/SageFs.Samples.FromJupyter/        statistics, fibonacci, data exploration
```

### 🎮 [Demos](demos/): Real Applications

Working applications demonstrating SageFs capabilities:

```
demos/SageFs.Samples.RaylibHello/      Raylib graphics — animated shapes
demos/SageFs.Samples.RaylibGame/       Raylib game — star catcher with scoring
demos/SageFs.Samples.WebappDatastar/   Falco web app — reactive todo list
```

## Running the Projects

### Option 1: dotnet run (simplest)

```bash
cd samples/from-koans/SageFs.Samples.Koans
dotnet run
```

Test projects show Expecto results in the terminal.
Demo projects launch their application (Raylib window or web server).

### Option 2: SageFs (best experience)

```bash
sagefs
# then, from your editor, the dashboard or an MCP client, create a session for
# samples/from-koans/SageFs.Samples.Koans/SageFs.Samples.Koans.fsproj
```

With SageFs, you get:
- ✓/✗ marks on each test's line
- Alt+Enter on any expression for inline results
- Live feedback as you edit. On my machine, with the small FromCSharp sample, the verdict on the edited function's test arrives about 0.7 s after the edit (see [how live testing works](../docs/how-live-testing-works.md))
- No terminal scrolling: results appear in your editor

### Option 3: dotnet test (CI integration)

```bash
cd samples/from-koans/SageFs.Samples.Koans
dotnet test
```

All test projects include `YoloDev.Expecto.TestSdk` for `dotnet test`
integration.

## Using the .fsx Scripts

The original `.fsx` scripts are still available alongside the projects.
They work as standalone SageFs exercises:

1. Open any `.fsx` file in VS Code or Neovim
2. Alt+Enter expressions to see results inline
3. Fill in `__` blanks to make tests pass
4. Save: SageFs shows ✓/✗ marks

The `.fsx` files have `__` placeholders (the exercise).
The `.fs` project files have solved answers (for verification).

## Project Structure

All projects use:
- The target framework inherited from `Directory.Build.props` (net11.0 as of 2026-10-01)
- **Central package management** (versions in `Directory.Packages.props`)
- **Expecto** with `Expecto.Flip` for testing
- **2-space indentation** (SageFs convention)
