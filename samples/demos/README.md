# SageFs Demo Applications

Working applications demonstrating SageFs with different frameworks.

## Projects

### 🎨 Raylib Hello — Animated Shapes

A simple Raylib window with animated circle and pulsing ring.
Great for learning how SageFs hot-reloads graphics code.

```bash
cd SageFs.Samples.RaylibHello
dotnet run
```

Change colors, shapes, or animation speeds — SageFs patches function
pointers via Harmony, so your changes appear instantly in the running window.

### 🎮 Raylib Game — Star Catcher

A simple game: catch falling stars with arrow keys. Demonstrates game loops,
scoring, and collision detection.

```bash
cd SageFs.Samples.RaylibGame
dotnet run
```

### 🌐 Webapp Datastar — Reactive Todo List

A real-time web application using Falco (F# web framework) and Datastar
(SSE-based reactivity). Add, toggle, and delete todos with instant updates.

```bash
cd SageFs.Samples.WebappDatastar
dotnet run
```

Then open `http://localhost:5000` in your browser.

## Using with SageFs

For the best development experience:

```bash
sagefs   # start the daemon
```

Then create a session for the demo's `.fsproj` in the Hot Reload workflow from your
editor, the dashboard or an MCP client, and start the app with `run_app` (VS Code:
**SageFs: Run App**). There is no `sagefs watch` command.

SageFs provides:
- **Hot reload** — edit functions and see changes in the running app. How a save reaches
  an app that `run_app` started (a restart or a patch, and what that does to its state) is
  in [docs/hot-reload.md](../../docs/hot-reload.md)
- **Alt+Enter** — evaluate any expression inline
- **Gutter markers** — see test results next to your code

`SageFs.Samples.ConsoleTicker` (and its `.Tests` project) is a console demo that is not
described above.

## Requirements

- The .NET SDK that `global.json` pins (a .NET 11 release candidate as of 2026-10-01)
- For Raylib demos: a display (won't work in headless environments)
- For the web demo: a web browser
