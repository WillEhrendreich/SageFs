# Hot Reload — How It Works

> ## 🚧 Status: In Progress
>
> The pipeline is wired and runs live, but one part is still being finished.
>
> **Works today:**
> - File watch → `#load` → FSI eval → Harmony patch → SSE broadcast runs end to end.
> - Browser auto-refresh (DevReload) works: SageFs injects a reload script into
>   your app's HTML and refreshes connected tabs on save, no manual F5.
>
> **Still being completed:**
> - Propagating code changes into a *running* app for module-declared,
>   route-captured apps (the common Falco/ASP.NET pattern: `module App.Program`
>   + `let routes = [...]` captured by value at startup). Re-evaluating the file
>   creates new FSI functions, but the running route table still points at the
>   old closures. See [internal status](internal/HOT_RELOAD_STATUS.md).
>
> Treat hot reload as experimental until the status banner there is removed.

> **Prerequisite:** Hot reload requires **Live mode**. In REPL mode (the default),
> browser hot reload is off. See [Workflow Modes](workflow-modes.md) for how to switch.

## The pipeline

1. The file watcher detects `.fs`/`.fsx` changes (~500ms debounce).
2. `#load` sends the file to FSI (~100ms).
3. [Harmony](https://github.com/pardeike/Harmony) patches method pointers at runtime, no restart.
4. SSE pushes a reload signal to connected browsers.

## Browser auto-refresh (DevReload)

Web apps need no extra configuration. SageFs auto-injects DevReload middleware
into your ASP.NET pipeline via [Harmony](https://github.com/pardeike/Harmony),
with no code changes. Your Falco/ASP.NET app gets browser auto-refresh once
SageFs is running. When a compile fails, an accessible error overlay appears in
the browser with source context and editor links, and the page reloads
automatically once the error is fixed.

Set `SAGEFS_DEVRELOAD=0` (or `false`) to disable auto-injection.

The VS Code extension gives per-file and per-directory hot reload toggles.

See [HOT_RELOAD_STATUS.md](internal/HOT_RELOAD_STATUS.md) for the full technical details.
