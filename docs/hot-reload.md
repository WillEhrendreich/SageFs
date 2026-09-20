# Hot Reload — How It Works

Save a `.fs` file and the change lands in the process that is already running
your app — including apps whose route table was built once at startup, the
Falco / Giraffe / Saturn pattern (`module App.Program` + `let routes = [...]`).
No restart.

> **Prerequisite:** Hot reload requires the **Hot Reload workflow** —
> `SessionWorkflow.HotReload`, which `switch_workflow` still spells `live` / `weblive` /
> `web` for historical reasons. It is off in the other two workflows, REPL
> (`Interactive`, the default) and Live Testing (`LiveTesting`), both of which keep full
> type redefinition instead. See [Workflow Modes](workflow-modes.md) for the full set and
> how to switch.

## The pipeline

1. The file watcher detects `.fs`/`.fsx` changes (~500ms debounce).
2. SageFs compares the file with the source your loaded assembly was built from
   and emits only the **functions that changed**, against the compiled module's
   own identity — so their parameter types stay the compiled types.
3. [Harmony](https://github.com/pardeike/Harmony) re-points those methods at
   their new bodies at runtime, no restart.
4. SSE pushes a reload signal to connected browsers.

Step 2 is what makes a startup-captured route table work. The route list holds
function values created at startup, but each of those still dispatches to the
handler's method entry point, so re-pointing the method changes what the
captured route serves.

## What reloads, and what needs a restart

A change reaches the running app when it is a change to a **function body**:

| Shape | Reloads |
|---|---|
| `let handler (ctx: HttpContext) = ...` | yes |
| a handler whose parameter type is declared in the same file | yes |
| `static member Render x = ...` on a type | yes |
| a small function with no `[<MethodImpl(NoInlining)>]` | yes |

Anything that takes effect at **startup** cannot be patched into a process that
already started, and SageFs restarts the app instead (or, if SageFs is not the
one running your app, says so and re-evaluates the file):

| Shape | Why not |
|---|---|
| `let getHome : HttpHandler = Response.ofHtml (pageLayout [])` | the HTML is computed once at module initialisation and captured by the route; nothing is called per request |
| `let handler : HttpHandler = fun ctx -> ...` (value binding) | module initialisation already ran; the route captured that run's closure |
| `let mutable state = ...` | the field was assigned at startup, and a reader compiles to a direct field load |
| a changed function signature, a new/removed declaration, a changed type | the compiled assembly's shape no longer matches |

If you want a handler to pick up edits, give it parameters —
`let getHome (ctx: HttpContext) = ...` rather than
`let getHome : HttpHandler = ...`.

Every row above is pinned by an executable cell in the hot-reload shape matrix
(`SageFs.Tests/WebAppHotReloadVerificationTests.fs`), which starts a real app,
saves a real file, and asserts what the same process serves afterwards.

**One requirement:** the baseline is the source your loaded assembly was built
from, so build the project before starting the session. A source file edited
after its last build is re-evaluated whole instead of patched, and SageFs logs
that it is doing so.

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
