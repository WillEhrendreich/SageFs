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
| `let handler : HttpHandler = fun ctx -> ...` — a value bound **directly** to a lambda | yes — F# compiles it to a method, exactly like the line above |
| a handler whose parameter type is declared in the same file | yes |
| the **body** of a `static member Render x = ...` on a type | yes |
| a small function with no `[<MethodImpl(NoInlining)>]` | yes |

Anything that takes effect at **startup** cannot be patched into a process that
already started, and SageFs restarts the app instead (or, if SageFs is not the
one running your app, tells you a restart is needed):

| Shape | Why not |
|---|---|
| `let getHome : HttpHandler = Response.ofHtml (pageLayout [])` | the HTML is computed once at module initialisation and captured by the route; nothing is called per request |
| a value whose closure is built inside a `let` — `let h = let x = compute () in fun () -> x` | `compute ()` ran at startup and the closure captured its result |
| `let mutable state = ...` whose value you changed | its value is your app's live data. SageFs will neither carry the old value forward (that ignores your edit) nor reset it (that destroys live state), so it leaves the running app exactly as it was and says a restart is needed |
| a changed function or member **signature**, a new/removed declaration, a type whose fields, cases or members were added, removed or re-typed | the compiled assembly's shape no longer matches, and live instances were laid out by the old definition |

If a handler is not picking up edits, check whether its value is **computed**
rather than **a function**: `let getHome : HttpHandler = fun ctx -> ...` and
`let getHome (ctx: HttpContext) = ...` both reload;
`let getHome : HttpHandler = Response.ofHtml (...)` is built once.

### Build without optimizations

Hot reload re-points **methods**. The F# compiler's Release optimizer inlines
small functions into their callers — including into the closures a route table
captures at startup — so in an optimized build there is often no call left to
re-point: the patch lands on a method nothing calls any more. SageFs builds your
project with `-p:Optimize=false` for exactly this reason, so a session SageFs
built is covered. **If you build Release by hand after starting the session**,
that optimized assembly is what gets loaded and edits to inlined functions will
not reach the running app — rebuild through SageFs (`hard_reset` with
`rebuild: true`, or the dashboard's HARD_RESET).

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
