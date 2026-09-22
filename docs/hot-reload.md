# Hot Reload: How It Works

Save a `.fs` file and the change lands in the process that's already running your
app, including apps whose route table was built once at startup, the Falco /
Giraffe / Saturn pattern (`module App.Program` + `let routes = [...]`). No
restart. And the app's live state stays where it is: the counter you bumped,
the cache you filled. This is the thing I most wanted from a REPL and couldn't
get anywhere else, so I built it. The gaps I know about are further down,
under [Where it falls short right now](#where-it-falls-short-right-now).

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
4. SSE pushes the outcome to connected browsers and editors.

Step 2 is the part that makes a startup-captured route table work. The route
list holds function values created at startup, but each of those still
dispatches to the handler's method entry point, so re-pointing the method
changes what the captured route serves. Harmony doesn't care that the
delegate was created six minutes ago. It cares where the call ends up.

## What reloads, and what needs a restart

The rule I'm going for: code changes take effect, state stays, and SageFs
never does either one quietly. Every row below names the test that pins it.
The "real app" ones start a real host on .NET 10 and on .NET 11, save a real
file and read what the same process serves afterwards. The planner ones are
unit tests on the decision, and I've said which is which.

### Code

A change reaches the running app when it's a change to a **function body**:

| Shape | What happens | Pinned by (real app) |
|---|---|---|
| `let handler (ctx: HttpContext) = ...` | reloads | shape matrix `plain` |
| `let handler : HttpHandler = fun ctx -> ...`, a value bound **directly** to a lambda | reloads, because F# compiles it to a method just like the line above | shape matrix `lambda` |
| a handler whose parameter type is declared in the same file | reloads | shape matrix `localType` |
| the **body** of a `static member Render x = ...` on a type | reloads | shape matrix `member` |
| a small function with no `[<MethodImpl(NoInlining)>]` | reloads | shape matrix `tiny` |
| a function that reads or writes a `let mutable private` in its file | reloads, and it uses the app's OWN field, so reads and writes agree with the rest of the app | state tests, rule 1 `let mutable private` |

The shape matrix is `SageFs.Tests/WebAppHotReloadVerificationTests.fs` (it runs
on .NET 11). The state tests are `SageFs.Tests/HotReloadStateOutcomeTests.fs`
and run once per runtime.

### State

Module-level `let mutable`s are your app's live data, and a save treats them
that way:

| You... | What happens | Pinned by (real app, net10 + net11) |
|---|---|---|
| edit a function, and the file has a `let mutable` you didn't touch | its live value stays. Its initializer never runs again | rule 1, public `let mutable` |
| edit a function that uses a `let mutable private` | same, the value stays. The patch can't name a private member from FSI, so it gets a stand-in with the same name that reads and writes the app's own field. Nothing gets re-declared | rule 1, `let mutable private` |
| edit a `let mutable`'s **initializer**, same type (`= 10` to `= 25`) | the app keeps its live value, and the save says so: `kept 'App.State.tuned' = 13 (your new initializer 25 applies when you reset it)`. The page doesn't refresh, because nothing it would fetch changed | rule 3, keep |
| reset it (the **Reset** button in the dashboard's Hot Reload panel, or the `reset_hot_reload_state` MCP tool) | ONLY that initializer runs, and its value goes into the app's field. Nothing else in the file runs | rule 3, reset. The button itself is pinned by a browser journey: an open dashboard shows the kept notice without a reload, Reset is clicked, and the app serves the new value (`HotReloadBrowserTests`, `--integration-hr`, .NET 11) |
| change a `let mutable`'s **type** (`= 1` to `= "one"`) | restart needed. The live value has the old type, so there's nothing safe to keep, and the running app is left exactly as it was until you restart | rule 4, retype |

I check the type against the running app, not just your annotation. The save
asks the app what it's holding (it defines your new initializer as a function
and compares types, it never calls it), so `= 1` to `= "one"` gets caught
without a `: int` anywhere. If that check can't get an answer, it's a restart.
Nothing gets kept on a guess.

### Restarts

Anything that takes effect at **startup** can't be patched into a process
that's already started, and SageFs restarts the app instead (or, if SageFs
isn't the one running your app, tells you a restart is needed):

| Shape | Why not | Pinned by |
|---|---|---|
| a value computed once at startup and closed over, like `let getHome : HttpHandler = Response.ofHtml (pageLayout [])` or `let h = let x = compute () in fun () -> x` | it ran at module initialisation and the route captured the result, so nothing is called per request | shape matrix `eager` (real app) |
| an immutable value that startup copied, like a route that does `let atStartup = banner in fun () -> atStartup` | the route holds the copy, so no patch of `banner` reaches it. It's never reported as patched | rule 2 guard, `banner` (real app, net10 + net11) |
| a `let mutable` whose type changed | see the state table | rule 4 (real app, net10 + net11) |
| a function that uses a **private function, value or type** in its file | FSI would need that member's code, not just a field, and a patch can't see private members. Private `let mutable`s are fine (see above) | planner: `ReloadPlanningTests` "carried live state" |
| a changed function or member **signature**, a new/removed declaration, a type whose fields, cases or members were added, removed or re-typed | the compiled assembly's shape no longer matches, and live instances were laid out by the old definition | planner: `ReloadPlanningTests`, `ReloadPlanningDecisionMutationTests` |

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

I don't trust a claim in these tables that isn't backed by a test, and for
anything about what the app actually does I want one that starts a real
process. It's too easy to convince yourself something works when it doesn't.

One requirement: the baseline is the source your loaded assembly was
built from, so build the project before starting the session. A source file
edited after its last build gets re-evaluated whole instead of patched, and
SageFs logs that it's doing so.

## Where it falls short right now

I'd rather you hear this from me than find it at 11pm.

- **Redefining an immutable value** (`let greeting = "hello"` to `"howdy"`)
  still needs a restart, even when nothing captured it. It's reported as a
  restart, never as a patch. See below for why.
- **When SageFs isn't the one running your app and a save needs a restart**
  (a signature or type change, say), SageFs re-evaluates the whole file
  instead, and that re-declares every `let mutable` in it. Your live state in
  that file is reset. The outcome says restart-required, which is true, but it
  doesn't say your state went with it. Start the app with `run_app` and SageFs
  restarts it properly instead.
- **The reset button runs just the initializer.** If the initializer uses
  something private in its file, the save can't check it and you get a restart
  instead of a kept value.

## What I'm working on

In the order I'm doing them. None of them is done until a real-app test proves
it on both .NET 10 and .NET 11.

Done already: an app started by `.SageFs/init.fsx` `#load`ing your sources
used to restart on an edit (.NET 10) or get patched on the wrong copy while
still saying "Patched" (.NET 11). The init script's `#load` skipped the
hot-reload middleware, so SageFs never learned which copy of a function the
app was holding. Now it tracks that, patches the copy the app holds, and never
reports "Patched" when it couldn't find it.

1. **A redefined value gets its new value**, as long as nothing captured it at
   startup. Patching the value's getter is the easy part. Knowing that nothing
   copied the old value while the app started is the hard part, and I thought
   the capture tracking above would answer it. It doesn't. It records which
   copy of each *function* the app holds, which is what a function patch
   needs, and says nothing about where a *value* went. The compiled code
   doesn't settle it either: for `let greeting = "hello"` the getter is just
   the string, the file's own startup code calls that getter too, and a route
   reaches `greet` through a function value that anything could have called
   once at startup and kept the answer. Telling "reads it per request" from
   "copied it at startup" needs evidence about where copies of the value
   went, and I don't have a way to get that without guessing. Guessing here
   means a fake patch, so it stays a restart until I do. The test for it is
   written and red on purpose (`HotReloadStateOutcomeTests`, rule 2, run with
   `--all`).

It's the same place Flutter, React Fast Refresh and Clojure's `defonce` ended
up, and I think they got it right. The one thing people complain about in
Flutter is that it keeps state without telling you, which is why a kept value
always comes with a notice.

## Browser auto-refresh (DevReload)

Web apps need no extra configuration. SageFs auto-injects DevReload
middleware into your ASP.NET pipeline via
[Harmony](https://github.com/pardeike/Harmony), with no code changes on your
part. Your Falco/ASP.NET app gets browser auto-refresh once SageFs is
running. When a compile fails, an accessible error overlay appears in the
browser with source context and editor links, and the page reloads
automatically once the error is fixed. A save that only kept live state doesn't
refresh the page, since nothing it would fetch changed.

Set `SAGEFS_DEVRELOAD=0` (or `false`) to disable auto-injection.

The VS Code extension gives per-file and per-directory hot reload toggles.

See [HOT_RELOAD_STATUS.md](internal/HOT_RELOAD_STATUS.md) for the full
technical details, if you want to go spelunking.
