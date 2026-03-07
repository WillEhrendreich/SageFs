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
cd SageFs.Samples.RaylibHello   # or any demo
sagefs watch .
```

SageFs provides:
- **Hot reload** — edit functions and see changes in the running app
- **Alt+Enter** — evaluate any expression inline
- **Gutter markers** — see test results next to your code

## Requirements

- .NET 10 SDK
- For Raylib demos: a display (won't work in headless environments)
- For the web demo: a web browser
