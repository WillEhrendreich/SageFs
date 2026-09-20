# SageFs Hot Reload Status

> ## ✅ Status: Working for function-body changes
>
> A save propagates into the running app for **module-declared, route-captured
> apps** — the Falco/Giraffe/Saturn/Oxpecker pattern (`module App.Program` +
> `let routes = [...]` captured by value at startup). Verified live against
> `samples/demos/SageFs.Samples.WebappDatastar`: the same running process served
> the new text after a save, with no restart, including for a handler whose
> parameter type is declared in the same file.
>
> **What changed.** The worker used to route a save to the in-place patch path
> only when the app had been started by `run_app`. Everything else fell back to
> re-evaluating the WHOLE file. A whole-file re-evaluation re-declares the types
> the file itself defines, so a handler like `todoListView (items: TodoItem
> list)` ended up with a parameter type from the FSI assembly while the compiled
> method's came from the project assembly. `HotReloadCore.compatibleForDetour`
> compares parameter types for equality, rejected the pair, and applied no
> detour — while still reporting the handlers that happened to have BCL-only
> signatures as "hot reloaded" and refreshing the browser. That is why the page
> reloaded with the old code.
>
> The route now depends on whether the file has a **baseline** (the source its
> loaded assembly was built from), not on how the app was started:
> `ReloadPlanning.routeFor`. With a baseline, only the CHANGED functions are
> emitted against the compiled module's identity
> (`CompilationContext.emitStableIdentity`), so every parameter type is
> identical and the pairing succeeds.
>
> **Genuine remaining limitations** (each pinned by a matrix cell, see below):
> value bindings, eagerly-computed handlers, mutable fields, and signature/type
> changes take effect at startup and cannot be patched into a process that
> already started. SageFs restarts the app when it is the one running it;
> otherwise it logs that a restart is needed and re-evaluates the file.

## The shape matrix (the gate)

`SageFs.Tests/WebAppHotReloadVerificationTests.fs` holds `ShapeMatrix.cells`.
Every cell starts a real app whose handler table is captured at startup, saves a
real source file, and asserts what the SAME process serves afterwards — never
"the pipeline ran", never "a detour was planned".

The fixture's call shape is itself a matrix dimension. It has to be: the earlier
gate (`fixtures/WebAppFixture/Greeting.fs`) was a `namespace`-declared
`[<MethodImpl(NoInlining)>]` function that the route CALLED at request time —
the one shape a detour has always been able to rewire. It was genuinely green,
ran in CI, and was the exact complement of the bug users hit. The matrix fixture
is `fixtures/WebAppFixture/Shapes.fs`: a dotted module-declared file, handlers
captured by value, no `NoInlining` anywhere, and the un-patchable shapes present
as explicit `RestartOnly` cells with their reason.

## ✅ What Works

### 6. Browser Auto-Refresh via DevReload
- **`SageFs.DevReload`** — pure broadcaster in `SageFs.Core` with zero ASP.NET dependency
- **`SageFs.DevReloadMiddleware`** — ASP.NET Core middleware in `SageFs` project
- **`SageFs.DevReloadInjector`** — Harmony auto-injection into `WebApplication.Run/RunAsync`
- Injects a tiny `<script>` before `</body>` in all `text/html` responses
- Script opens SSE connection to `/__sagefs__/reload`
- **Zero configuration** — works automatically for any ASP.NET Core app loaded in SageFs

#### DevReload Event Lifecycle

`DevReloadEvent` (`SageFs.Core/DevReload.fs`) has one non-terminal case and four
terminal outcomes — deliberately no bare "Reload" case, because a save can do more
than one thing to a running app:

```
Compiling(fileName)  → Patched(report)            (one or more functions re-pointed; browser refreshes)
Compiling(fileName)  → Restarted(report)           (SageFs itself restarted the app; browser refreshes once it's back up)
Compiling(fileName)  → NotApplied(report)          (no-op save, or a change that needs a restart SageFs isn't driving; browser does NOT refresh)
Compiling(fileName)  → CompilationFailed(error, report, diagnostics)  (browser shows the error overlay, no refresh)
```

`report` is the `ReloadOutcome` translated into `{ Outcome, Patched, Considered, Message,
SuggestedAction, Reasons }` (`SageFs.Core/Features/ReloadBroadcast.fs`) — the same values
every client (browser overlay, VS Code, an editor extension) reads, so they cannot disagree
about what a save did. `DevReloadEvent.refreshes` is the single place that decides whether
the browser refreshes; only `Patched` and `Restarted` return `true` — a save that patched
nothing, or a save that needs a restart SageFs isn't driving, does not send a refresh.

#### Safety Features
- **Infinite-reload guard**: sessionStorage counter — if >3 reloads in 5s, pauses with red warning
- **Error overlay**: compilation errors shown directly in the browser (red panel, `#dc2626`)
- **Idempotent injection**: `data-sagefs-injected` attribute prevents double script injection
- **Kill switch**: Set `SAGEFS_DEVRELOAD=false` or `0` to disable entirely
- **SSE retry**: `retry: 1000` header ensures automatic reconnection after network hiccups

### 1. Automatic File Watching
- **Worker processes automatically watch project directories** for `.fs`, `.fsx`, `.fsproj` changes
- On `.fs`/`.fsx` change: `ReloadPlanning.routeFor` picks patch-in-place (diff against the
  build-time baseline, emit only the changed functions) or whole-file re-evaluation, then
  Harmony re-points methods for the patch path
- On `.fsproj` change: triggers soft reset to pick up new references
- 500ms debounce prevents thrashing on rapid saves
- **`--no-watch` does not disable this.** It is parsed but not wired to anything —
  `SessionManager.startWorkerProcess` always spawns workers with watching on — and
  `sagefs`'s CLI refuses the flag with a message saying so (`SageFs.Core/Args.fs`,
  `SageFs/Program.fs`). There is currently no daemon-startup or session-creation
  control to turn file watching off.

### 2. Hot Reload with Harmony Method Detouring
- Handlers can be updated in real-time — no restart needed, proven by
  `SageFs.Tests/WebAppHotReloadVerificationTests.fs`'s shape matrix against a real
  running process (see the banner at the top of this file)
- This works for both:
  - REPL-typed code (interactive)
  - File-change-triggered reloads (automatic)
- Changes appear live in the browser via DevReload once the detour succeeds

### 3. FSI Compatibility Middleware  
- Automatically rewrites `use` → `let` for indented use statements
- Applies to interactively-sent code via MCP
- Handles FSI incompatibilities transparently
- Located in `SageFs/FsiRewrite.fs` and `SageFs/Middleware/FsiCompatibility.fs`

### 4. Multi-line Code Submission
- Fixed in `SageFs/Mcp.fs` sendFsharpCode 
- Splits code by `;;` delimiter
- Executes each statement sequentially
- Returns all results concatenated

### 5. Enhanced Error Reporting
- Shows full exception details including:
  - Exception type
  - Message
  - Stack trace
  - Inner exceptions (recursively)
- Located in `SageFs/Mcp.fs` formatEvalResult

## 🔥 How Hot Reload Works End-to-End

1. **File change detected** → FileWatcher debounces (500ms)
2. **Action decided** → `.fs`/`.fsx` → `ReloadPlanning.routeFor` picks `PatchInPlace baseline`
   (there's a build-time baseline for this file) or `ReevaluateWholeFile` (there isn't, or the
   source was edited after the build — the whole file is re-evaluated instead of diff-patched);
   `.fsproj` → SoftReset
3. **DevReload broadcasts `Compiling`** → browser shows the recompiling overlay
4. **On the patch path**: only the changed functions are emitted against the compiled
   module's own identity (`CompilationContext.emitStableIdentity`), so their parameter types
   match the compiled method's exactly
5. **On success**: Harmony re-points the changed methods' native entry points
   (`HotReloadCore.detourMethod`, exact parameter-type matching, not fuzzy) →
   `ReloadOutcome.Patched(patched, considered)` → `DevReload.broadcastPatched` → browser
   refreshes. A save that changed nothing patchable reports `NoEffect` with a reason and a
   remedy, and does **not** refresh the browser.
6. **On a startup-only change** (a value binding, `let mutable` state, a changed signature or
   type): SageFs restarts the app itself when it is the one running it (`Restarted`), or falls
   back to re-evaluating the whole file and tells you a restart is needed (`RestartRequired`)
7. **On failure**: `CompilationFailed` → browser shows the error overlay (red), no refresh
8. **No restart needed for a patch** → the next HTTP request uses the new code automatically

## 🏗️ Architecture Decisions (Chesterton's Fences)

These design decisions exist for specific reasons. Before changing them, understand why they're there.

### AppDomain.CurrentDomain for shared state
**Why**: `DevReload.getChannels()` stores the ConcurrentDictionary in `AppDomain.CurrentDomain.GetData()` instead of a static field. This is because Harmony's auto-injection causes SageFs.Core.dll to be loaded multiple times in the same process (host copy + FSI shadow copy). A static field would create two separate dictionaries — the browser's SSE client registers against the shadow-copy DLL, while the broadcast functions (`DevReload.broadcastPatched`, `broadcastRestarted`, `broadcastNotApplied`, `broadcastCompilationFailed`) run in the host DLL. AppDomain storage is shared across all assembly loads, solving this mismatch.

### Channel-per-client (not shared Channel)
**Why**: Each SSE client gets its own `Channel<DevReloadEvent>`. A shared channel with multiple readers would require fan-out logic and risk one slow reader blocking others. Per-client channels provide natural backpressure isolation — if one browser tab is slow, others aren't affected. The ConcurrentDictionary keyed by connection ID supports this cleanly.

### Harmony auto-injection (not manual middleware registration)
**Why**: `DevReloadInjector.install()` patches `WebApplication.Run/RunAsync` via Harmony prefix. This means DevReload works **without any code changes** to the user's app — they just load their project in SageFs and it works. Manual `app.Use(middleware)` requires users to modify their code, which is worse DX and breaks the "zero-config" principle.

### Pre-allocated SSE byte arrays
**Why**: The SSE endpoint in `WorkerHttpTransport.fs` pre-allocates `heartbeatBytes`, `connectedBytes`, `compilingBytes`, and `reloadBytes` as module-level `ReadOnlyMemory<byte>` values. SSE heartbeats fire every 15s per client — allocating fresh byte arrays each time creates unnecessary GC pressure. Only `Compiling(Some file)` and `CompilationFailed(error)` allocate dynamically because their payloads vary.

### Single cleanup via RequestAborted.Register (not IDisposable)
**Why**: The SSE endpoint previously had both a `use cleanup = { new IDisposable ... }` and a `RequestAborted.Register(fun _ -> unregisterClient id)`. This caused double-unregister on normal disconnect (scope exit fires IDisposable, then abort fires the callback). Since `unregisterClient` decrements a counter, double-calling would make the counter go negative. Now only `RequestAborted.Register` handles cleanup. `unregisterClient` is idempotent via `TryRemove` — second call is a no-op.

### Infinite-reload guard (sessionStorage, not server-side)
**Why**: The guard runs in the browser, not on the server. Server-side rate limiting would need per-connection state management and doesn't protect against the actual failure mode (JavaScript `catch(ex) { reload() }` in a loop). The browser guard uses `sessionStorage` so it persists across reloads but not across tab close — a fresh tab always starts clean.

### Direct ConcurrentDictionary iteration (no snapshot)
**Why**: `broadcast()` iterates `for kvp in channels do ...` directly rather than `channels.Values |> Seq.toList`. ConcurrentDictionary's enumerator is lock-free and supports concurrent modification. The previous `Seq.toList` snapshot allocated a new list on every broadcast — unnecessary since we only call `TryWrite` (which never throws even if the channel is full or the client was just removed).

## ⚠️ Known Limitations

### Changes that take effect at startup

A running process cannot be given a new module initialisation. These shapes are
`RestartOnly` cells in the matrix, each with its reason:

| Shape | Why a detour cannot reach it |
|---|---|
| `let h : HttpHandler = Response.ofHtml (pageLayout [])` | the value was computed once at module init and captured by the route |
| `let h : HttpHandler = fun ctx -> ...` | `ReloadPlanning` classifies a parameterless binding as a value, so the whole file is treated as a startup change. The IL would allow it (F# emits a static method plus a closure that calls it) — reclassifying syntactic-lambda value bindings as functions is an open improvement |
| `let mutable state = ...` | the field was assigned at startup; a reader compiles to a direct field load (`ldfld`) |
| a changed signature, a new/removed declaration, a changed type | the compiled assembly's shape no longer matches |

Note what is NOT a limitation: `[<MethodImpl(MethodImplOptions.NoInlining)>]`
is **not** required on the user's own source. The `tiny` matrix cell is a
one-line function with no attribute and it reloads. SageFs injects `NoInlining`
on the code it emits (`HotReloading.injectNoInlining`); the compiled side does
not need it.

### The baseline must match the build

Patching in place is only offered for a file whose source was not touched after
the build that produced the loaded assembly
(`ReloadPlanning.baselineIsTrustworthy`). A file edited after its last build is
re-evaluated whole instead, and the worker logs that it is doing so. Build
before starting the session.

### Content Security Policy (CSP)

DevReload injects an inline `<script>` tag into HTML responses. If your app uses a
strict Content-Security-Policy header, the injected script may be blocked.

**Automatic handling (v0.5.618+):** When DevReload detects a CSP header on the response,
it automatically:
1. Generates a cryptographic nonce (`RandomNumberGenerator.GetBytes(16)`)
2. Adds `nonce="..."` to the injected script tag
3. Appends `'nonce-...'` to the CSP header's `script-src` directive

This works for most CSP configurations. However, it does **not** work when:
- CSP is delivered via `<meta http-equiv="Content-Security-Policy">` (only response headers are patched)
- CSP uses `'strict-dynamic'` without a nonce source (the nonce is added but `strict-dynamic` requires
  scripts to be loaded by trusted scripts, not injected)
- A reverse proxy strips or overrides the modified CSP header after middleware runs

**Workarounds for edge cases:**
```bash
# Option 1: Disable DevReload entirely
SAGEFS_DEVRELOAD=0 SageFs

# Option 2: Add SageFs's script hash to your CSP
# (hash changes each release — not recommended for long-term use)

# Option 3: Use 'unsafe-inline' in development CSP only
# Most frameworks have env-conditional CSP configuration
```

**Diagnostic logging:** Set log level to Debug to see `[DevReload] Injected CSP nonce for /path`
messages confirming nonce injection is working.

### Project Loading via sessions
When the daemon is running bare and a client creates a session for `MyProject.fsproj`:
- SageFs loads **compiled DLLs**, not source code
- The FSI compatibility rewrite only affects:
  - Files loaded with `--use` flag (`.fsx` scripts)
  - Code sent interactively via MCP
  - Files reloaded via file watcher `#load`
- Already-compiled DLL code is NOT rewritten

### Console I/O — Resolved
- PrettyPrompt has been **removed** from SageFs. The daemon-first architecture runs headless; `SageFs connect` provides the REPL client.

## 🎯 How to Use Hot Reload

The daemon is headless — there is no `--use <script>` script-launch mode and no
`--no-watch` (see the note under "Automatic File Watching" above). File watching turns on
automatically once a session is created:

```bash
sagefs                       # start the daemon bare, waits for clients
```

Create a session for your project from an editor, MCP, or the dashboard
(`http://localhost:37750/dashboard`), start your app in it — either you run it yourself
in the REPL, or `run_app` runs it for you — then just edit `.fs` files and save. Look for
`[DevReload]`/`[HotReload]` log lines in the daemon console.

Set `SAGEFS_DEVRELOAD=0` (or `false`) to disable the browser-refresh injection specifically;
there is currently no way to disable file watching itself.

## 📁 Key Files

| File | Purpose |
|------|---------|
| `SageFs.Core/DevReload.fs` | Pure broadcaster: `Compiling` + 4 terminal outcomes (`Patched`/`Restarted`/`NotApplied`/`CompilationFailed`), Channel-per-client, AppDomain shared state, diagnostic logging |
| `SageFs.Core/Features/ReloadOutcome.fs` | `ReloadOutcome` (`Patched`/`NoEffect`/`Restarted`/`RestartRequired`/`CompileFailed`) and `RestartReason`, each with a `describe` and a `remedy` |
| `SageFs.Core/Features/ReloadBroadcast.fs` | Translates a `ReloadOutcome` into the one `DevReloadEvent`/`ReloadReport` every client reads |
| `SageFs.Host/Resources/devreload.js` | Browser-side JS: WCAG AA error panel, smart auto-reload, editor links, ARIA |
| `SageFs.Host/DevReloadMiddleware.fs` | ASP.NET middleware: body-swap injection, CSP nonce, template placeholders |
| `SageFs.Host/DevReloadInjector.fs` | Harmony auto-injection: patches WebApplication.Run/RunAsync |
| `SageFs.Host/WorkerHttpTransport.fs` | SSE endpoint: pre-allocated bytes, diagnostic logging, hardened exception handling |
| `SageFs.Host/WorkerMain.fs` | Starts file watcher, routes changes through `ReloadPlanning` → FSI, wires error path |
| `SageFs.Core/FileWatcher.fs` | Pure file watching with debounce, diagnostic logging |
| `SageFs.Core/Middleware/HotReloading.fs` | Harmony method detouring |
| `SageFs.Core/Middleware/CompilationContext.fs` | File preprocessing, module detection, line offset mapping |
| `SageFs.Core/ActorCreation.fs` | Registers middleware pipeline |
| `SageFs.Core/Features/ReloadPlanning.fs` | `routeFor` (patch in place vs re-evaluate whole file), `planReload`, `confirmPatch`, `baselineIsTrustworthy` |
| `SageFs.Tests/fixtures/WebAppFixture/Shapes.fs` | The shape-matrix fixture: handlers captured by value at startup, no `NoInlining`, un-patchable shapes included |
| `SageFs.Tests/WebAppHotReloadVerificationTests.fs` | `ShapeMatrix.cells` + the outcome gates (real app, real save, real HTTP) |
| `SageFs.Tests/DevReloadMiddlewareTests.fs` | 40 tests: CSP nonce, encoding, embedded JS, 13 UX features |
| `SageFs.Tests/DevReloadTests.fs` | 31 tests: 6 FsCheck property + 25 unit (lifecycle, middleware, SSE) |
| `SageFs.Tests/HotReloadingPropertyTests.fs` | Property-based tests for HotReloading pipeline |
| `SageFs.Tests/HotReloadTests.fs` | 21 integration tests |
| `SageFs.Tests/FileWatcherTests.fs` | Pure function tests |

## ✨ Summary

The system:
- Watches project directories for `.fs`/`.fsx`/`.fsproj` changes
- Debounces (500ms) to avoid thrashing
- Diffs the saved file against the source its loaded assembly was built from
- Emits only the changed functions, against the compiled module's own identity
- Harmony re-points those methods at runtime
- No restart, no manual intervention — just edit and save

Changes that take effect at startup (values, mutable fields, types, signatures)
restart the app instead; see the banner at the top of this file.
