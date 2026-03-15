# 🏗️ Architecture

SageFs is **daemon-first** — one server, many clients. The daemon starts bare and creates sessions on demand. Each session is an **isolated worker sub-process** (Erlang-style fault isolation) with its own FSI, project, and file watcher. The TUI uses SageTUI's Elm Architecture (`Program<Model,Msg>` with SIMD cell diff), while the Raylib GUI uses the `Cell[,]` grid abstraction — both share the same keybindings via `KeyMap` and connect to the daemon via SSE. See the [architecture diagram](../Readme.md#-one-daemon-every-editor--simultaneously) for how clients connect.

6500+ tests: Expecto unit tests, FsCheck property-based state machine tests, Verify snapshots, binary persistence property tests.

## Project Structure

```
SageFs.Core/       — Shared types, rendering abstraction, KeyMap, Theme
SageFs/            — CLI tool, daemon, SageTUI client + legacy TUI
SageFs.Gui/        — Raylib GUI client (Cell[,] grid renderer)
SageFs.Tests/      — Expecto test project
sagefs-vscode/     — VS Code extension (Fable F#→JS)
sagefs-vs/         — Visual Studio extension (C# + F#)
docs/              — GitHub Pages site
```

The Neovim plugin lives in a separate repo: [sagefs.nvim](https://github.com/WillEhrendreich/sagefs.nvim).

## Rendering Pipeline

Both TUI and Raylib GUI consume the same pipeline:

```
ElmModel → SageFsRender.render → RenderRegion list
  → Screen.draw(grid, state, regions)  ← writes to Cell[,]
    → Backend.emit(grid)               ← TUI: ANSI string, Raylib: draw calls
```

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
