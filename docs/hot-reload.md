# 🔥 Hot Reload — How It Works

> **Prerequisite:** Hot reload requires **Live mode**. If you're in REPL mode (the default), browser hot reload is not active. See [Workflow Modes](workflow-modes.md) for how to switch.

1. File watcher detects `.fs`/`.fsx` changes (~500ms debounce)
2. `#load` sends the file to FSI (~100ms)
3. [Harmony](https://github.com/pardeike/Harmony) patches method pointers at runtime — no restart
4. SSE pushes a reload signal to connected browsers

**Zero config for web apps.** SageFs auto-injects DevReload middleware into your ASP.NET pipeline via [Harmony](https://github.com/pardeike/Harmony) — no code changes needed. Your Falco/ASP.NET app gets browser auto-refresh the moment SageFs is running. If something breaks, an accessible error overlay appears in the browser with source context, editor links, and smart auto-reload when the error is fixed.

Set `SAGEFS_DEVRELOAD=0` to disable auto-injection if needed.

The VS Code extension gives per-file and per-directory hot reload toggles.

See [HOT_RELOAD_STATUS.md](internal/HOT_RELOAD_STATUS.md) for the full technical deep-dive.
