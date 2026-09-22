# Hot Reload — How It Works

Save a `.fs` file and the change lands in the process that's already running your
app, including apps whose route table was built once at startup, the Falco /
Giraffe / Saturn pattern (`module App.Program` + `let routes = [...]`). No
restart. This is the thing I most wanted from a REPL and couldn't get anywhere
else, so I built it. There's one real gap right now, and a plan for values
and state. Both are further down, under
[Where it falls short right now](#where-it-falls-short-right-now).

> **Prerequisite:** Hot reload requires the **Hot Reload workflow**:
> `SessionWorkflow.HotReload`, which `switch_workflow` still spells `live` /
> `weblive` / `web` for historical reasons (I renamed the workflow late and
> didn't want to break anyone's muscle memory). It's off in the other two
> workflows, REPL (`Interactive`, the default) and Live Testing
> (`LiveTesting`), both of which keep full type redefinition instead. See
> [Workflow Modes](workflow-modes.md) for the full set and how to switch.

## The pipeline

1. The file watcher detects `.fs`/`.fsx` changes (~500ms debounce).
2. SageFs diffs the file against the source your loaded assembly was actually
   built from and emits only the **functions that changed**, against the
   compiled module's own identity. That keeps their parameter types as the
   compiled types.
3. [Harmony](https://github.com/pardeike/Harmony) re-points those methods at
   their new bodies at runtime. No restart.
4. SSE pushes a reload signal to connected browsers.

Step 2 is the part that makes a startup-captured route table work. The route
list holds function values created at startup, but each of those still
dispatches to the handler's method entry point, so re-pointing the method
changes what the captured route serves. Harmony doesn't care that the
delegate was created six minutes ago. It cares where the call ends up.

## What reloads, and what needs a restart

A change reaches the running app when it's a change to a **function body**:

| Shape | Reloads |
|---|---|
| `let handler (ctx: HttpContext) = ...` | yes |
| `let handler : HttpHandler = fun ctx -> ...`, a value bound **directly** to a lambda | yes, F# compiles it to a method, exactly like the line above |
| a handler whose parameter type is declared in the same file | yes |
| the **body** of a `static member Render x = ...` on a type | yes |
| a small function with no `[<MethodImpl(NoInlining)>]` | yes |

Anything that takes effect at **startup** can't be patched into a process
that's already started, and SageFs restarts the app instead (or, if SageFs
isn't the one running your app, tells you a restart is needed):

| Shape | Why not |
|---|---|
| `let getHome : HttpHandler = Response.ofHtml (pageLayout [])` | the HTML is computed once at module initialisation and captured by the route; nothing is called per request |
| a value whose closure is built inside a `let`, like `let h = let x = compute () in fun () -> x` | `compute ()` ran at startup and the closure captured its result |
| `let mutable state = ...` whose value you changed | its value is your app's live data. I'm not going to carry the old value forward (that ignores your edit), and I'm not going to reset it either (that destroys live state). So the running app is left exactly as it was and you get told a restart is needed |
| a changed function or member **signature**, a new/removed declaration, a type whose fields, cases or members were added, removed or re-typed | the compiled assembly's shape no longer matches, and live instances were laid out by the old definition |

If a handler isn't picking up edits, check whether its value is **computed**
rather than **a function**: `let getHome : HttpHandler = fun ctx -> ...` and
`let getHome (ctx: HttpContext) = ...` both reload;
`let getHome : HttpHandler = Response.ofHtml (...)` is built once and won't.
This trips people up constantly and it's almost always this.

### Build without optimizations

Hot reload re-points **methods**. The F# compiler's Release optimizer inlines
small functions into their callers (including into the closures a route
table captures at startup), so in an optimized build there's often no call
left to re-point: the patch lands on a method nothing calls anymore. SageFs
builds your project with `-p:Optimize=false` for exactly this reason, so a
session SageFs built is covered. **If you build Release by hand after
starting the session**, that optimized assembly is what gets loaded and
edits to inlined functions won't reach the running app. Rebuild through
SageFs (`hard_reset` with `rebuild: true`, or the dashboard's HARD_RESET).

Every row above is pinned by an executable cell in the hot-reload shape
matrix (`SageFs.Tests/WebAppHotReloadVerificationTests.fs`), which starts a
real app, saves a real file, and asserts what the same process actually
serves afterwards. I don't trust a claim in this table that isn't backed by
a test that starts a real process. It's too easy to convince yourself something
works when it doesn't.

One requirement: the baseline is the source your loaded assembly was
built from, so build the project before starting the session. A source file
edited after its last build gets re-evaluated whole instead of patched, and
SageFs logs that it's doing so.

## Where it falls short right now

I'd rather you hear this from me than find it at 11pm.

- **Apps started by an init script that `#load`s your sources.** If your
  `.SageFs/init.fsx` does `#load "Greeting.fs"` then `#load "App.fs"` and starts
  the app during warmup, a body edit doesn't patch in place on .NET 10. It comes
  out as a restart. You get the new code, but your live state is gone. On .NET
  11 it's worse, and it's the reason .NET 11 isn't the default yet: the patch
  lands on the compiled copy of the function, the app is calling the copy the
  init script loaded, and the outcome still says "Patched". It works fine if
  the app runs from your compiled project, or if you `#load` your sources
  into the session yourself and then start the app. The broken path is only the
  one where the init script loads the sources during warmup. There's a test
  that pins this as broken (`SageFs.Tests/FsiEmitSimTests.fs`), and I'm fixing
  it now.
- **Mutable state you didn't touch.** Editing one function in a file shouldn't
  touch a `let mutable` next to it, since only the changed functions get
  re-evaluated, so its value should survive. No test proves that yet, so don't
  treat it as a promise until one does. A **private** mutable that the edited
  function reads is worse: that forces a restart, because the patch can't see
  private members.
- **Editing a value or a mutable's initializer** restarts the app (see the
  table above).

## What I'm working on

These are in the order I'm doing them. None of them is done until a real-app
test proves it on both .NET 10 and .NET 11.

1. **Patch the copy the app actually calls.** SageFs will track which copy of a
   function the app captured when it built its handler table, and aim the
   patch there. If it can't find that copy it'll say restart-required, with the
   reason, and never "Patched". That fixes the init-script case above on both
   runtimes, and it's what unblocks .NET 11 as the default.
2. **State survives a reload.** An untouched `let mutable`, ref cell or mutable
   object keeps its live value, private ones included, and there'll be a test
   pinning it.
3. **Editing a live mutable's initializer keeps the live value and tells you.**
   Change `let mutable count = 0` to `= 10` and the running counter keeps
   counting. SageFs says something like "kept `count` = 37, your new
   initializer applies on reset", and gives you a reset button in the dashboard
   plus an MCP call. It only restarts if the type changed.
4. **A redefined value gets its new value**, as long as nothing captured it at
   startup. If something did, you get a restart and the reason, not a fake
   patch.

The rule behind all of it: code changes take effect, state stays, and SageFs
never does either one quietly. It's the same place Flutter, React Fast Refresh
and Clojure's `defonce` ended up, and I think they got it right.

## Browser auto-refresh (DevReload)

Web apps need no extra configuration. SageFs auto-injects DevReload
middleware into your ASP.NET pipeline via
[Harmony](https://github.com/pardeike/Harmony), with no code changes on your
part. Your Falco/ASP.NET app gets browser auto-refresh once SageFs is
running. When a compile fails, an accessible error overlay appears in the
browser with source context and editor links, and the page reloads
automatically once the error is fixed.

Set `SAGEFS_DEVRELOAD=0` (or `false`) to disable auto-injection.

The VS Code extension gives per-file and per-directory hot reload toggles.

See [HOT_RELOAD_STATUS.md](internal/HOT_RELOAD_STATUS.md) for the full
technical details, if you want to go spelunking.
