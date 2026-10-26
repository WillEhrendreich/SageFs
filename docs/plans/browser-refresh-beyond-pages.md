# Refresh CSS and static files too

Roadmap item: `browser-refresh-beyond-pages` (Later, hot reload). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S to M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: the two copies of `devreload.js` are byte-identical. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

You edit `wwwroot/site.css` while the page is open. The styles change in the open page within a fraction of a second, with no
navigation: a half-typed form input, the scroll position and the page's JavaScript state stay. You edit a script, an
image, a font or a static HTML file and the page reloads once. You edit a Razor or Blazor file and the overlay says what SageFs
does and does not do for it, where today it says nothing. An edit in `bin/`, `obj/`, `node_modules/` or `.git/` does nothing.
A burst of an F# save and a CSS save reloads the page at most once, and not while the app is restarting.

The outcome gate is a Playwright.NET journey in the F# suite (house rule: browser tests live in Expecto, never in a separate
JS project), in a new `SageFs.Tests/AssetReloadBrowserTests.fs` beside `HotReloadBrowserTests.fs`. Matrix: app started by
(`run_app`, an FSI-started WebLive app) by file edited (css, js, png, html, razor, a file under `node_modules`) by page state
(an input with typed text and a JS marker variable). Expected per row: css keeps the marker and the text and changes the
computed style; js, png, html reload once and lose the marker; razor shows the named message; the ignored file changes
nothing. The browser tier runs on net11 only, as the repo does for every browser test, so the runtime axis is not repeated.

## What exists today

- The file watcher watches `.fs`, `.fsx` and `.fsproj` only: `FileWatcher.defaultWatchConfig` sets `Extensions`, and
  `fileChangeAction` answers `Ignore` for any other extension with a debug line ("extension ... not watched").
  `FileWatcher.shouldTriggerRebuild` filters `bin/`, `obj/` and temp files; `excludedDirNames` prunes `bin obj .git .vs .idea
  node_modules .runs artifacts`.
- The consumers of `FileChangeAction` are the worker's `beginChange` and the landing path (`ApplySaves`) in
  `SageFs.Host/WorkerMain.fs` (the two matches near lines 1904 and 2271), plus `SageFs.Simulation/WatcherLifecycleSim.fs` and
  `WatcherLifecycleInvariants.fs`. A new DU case breaks all four at compile time, which is the safety net.
- The browser is told through `SageFs.Core/DevReload.fs`: `DevReloadEvent` is `Compiling | Applied | Patched | NeverEntered |
  Restarted | NotApplied | CompilationFailed`. `DevReloadEvent.refreshes` answers true for `Applied` and `Restarted` only.
  `broadcast` calls `LastReload.record`, which keeps the last terminal event for `/hotreload/last-outcome`.
- The page script is `devreload.js`, injected by `SageFs.Host/DevReloadMiddleware.fs` into HTML responses. It reconnects to the
  worker's `/__sagefs__/reload` stream, switches on `msg.type`, and its only refresh is `safeReload`, which counts reloads
  against a guard (`RELOAD_GUARD_THRESHOLD`, 3) and saves and restores scroll and form state.
- Two byte-identical copies of the script are committed: `SageFs.Host/Resources/devreload.js` and
  `SageFs/Resources/devreload.js` (same blob hash). A third path embeds the Host copy into the FSI host build
  (`SageFs.Core.fsproj`, `FsiHostBuild.fs`). No test pins the two copies together.
- The daemon also reads that stream: `SageFs/WorkerReloadRelay.fs` feeds `SessionReload.ofPayloadJson`, which answers
  `UnknownEventType` for a type it does not know, and `DaemonMode.fs` logs a warning per event. A new event type would flood
  the log unless the daemon learns it.
- A `run_app` app has its content root at the project directory (`AppRun.planLaunch`, `ASPNETCORE_CONTENTROOT`), so
  `wwwroot` files are served from the source tree and an edit is visible on disk at once. The only missing piece is the page
  finding out.
- `DevReloadMiddleware` sets `Cache-Control: no-store` on the HTML it rewrites. It sets nothing on a static file response.
- Razor and Blazor. F# projects cannot compile `.cshtml` or `.razor` files themselves (the Razor SDK is C# only), so such views
  live in a referenced C# project. I did not check how SageFs loads such a reference. No code in this tree touches either
  extension.

## Design

A static asset change is not a save. It never takes the compile lock, never reaches the planner, and never changes the last
save verdict. It is a separate event.

Pure core, in two places because `DevReload.fs` is also compiled into the FSI host and must stay free of Core dependencies:

```fsharp
// SageFs.Core/DevReload.fs (embedded in the FSI host: no dependencies)
type AssetKind =
  | Stylesheet
  | Script
  | Image
  | Font
  | StaticDocument
  | CompiledView          // .cshtml, .razor: compiled into an assembly, never served from disk

type DevReloadEvent =     // one new case
  | ...
  | AssetChanged of kind: AssetKind * urlPath: string * sequence: int64

// SageFs.Core/FileWatcher.fs
type FileChangeAction =   // one new case
  | ...
  | Asset of path: string * kind: AssetKind
```

`AssetKind.ofExtension` is one total function with one exhaustive `describe`. `FileWatcher.defaultWatchConfig.Extensions`
grows by the asset set. `urlPath` is the file's path relative to the project's `wwwroot`, with forward slashes, or empty when
the file is not under one.

Coalescing, pure, in `SageFs.Core/AssetReload.fs`: `AssetReload.decide : Standing -> Input -> Standing * Action list` over
`Standing = Quiet | SaveInFlight | ReloadOwed`, with inputs `AssetEdited of AssetKind`, `SaveStarted`, `SaveFinished of
refreshes: Refresh`, and actions `SwapStylesheet`, `ReloadPage`, `SayCompiledView`. `Refresh` is a two-case DU
(`PageRefreshed | PageUntouched`; no bool). Rules: a stylesheet edit always swaps and never reloads; any other kind during a
save in flight sets `ReloadOwed`; when the save finishes, `ReloadOwed` with `PageRefreshed` emits nothing, and with
`PageUntouched` emits one `ReloadPage`. That gives "at most one reload per burst" and "no reload while the app restarts" by
construction, with the wait ending on the save's own terminal event, never on a timer.

Worker: `beginChange` handles `FileChangeAction.Asset` before the compile lock (so a stuck compile never blocks a CSS swap),
folds it through `AssetReload.decide`, and calls a new `DevReload.broadcastAssetChanged`. `LastReload.record` ignores the new
case like it ignores `Compiling`. In the landing path (`ApplySaves`), an asset file answers a new
`TrunkFollow.FileOutcome.AssetSignalled`, so a landed CSS file refreshes the pages of the trunk app and is reported.

Browser: a new `msg.type === 'asset'` branch in `devreload.js`. For `stylesheet`, find each `link[rel=stylesheet]` whose
pathname equals `urlPath` (or ends with its last two segments when `urlPath` is empty), insert a clone with `?sagefs=<sequence>`
after it, remove the old link on the clone's `load`, keep the old one on `error`. If no link matches, call `safeReload`. All
other kinds call `safeReload`. Both script copies change in the same commit. A new test pins them byte-equal.

`CompiledView`: the broadcast sends the event with kind `compiledview`, and the overlay says the file compiles into an
assembly SageFs does not patch, and what to do (save the C# project or restart the session). The daemon's session state does
not change. Real support needs Roslyn's edit-and-continue service for C#, which is out of scope here (decision below).

Cache. If measurement 1 shows the browser serves a stale script after `location.reload()`, `DevReloadMiddleware` gets a second
branch that sets `Cache-Control: no-cache` on static-content responses in development. Otherwise nothing changes.

Daemon. `SessionReload.ofPayloadJson` gets a `ReloadPayloadError.NotASaveEvent of eventType` for `asset`, and
`DaemonMode.ensureReloadRelay` logs it at debug level. The session's `Reload` field stays as it was.

Wire and clients. The browser SSE payload gains `{"type":"asset","kind":..., "path":..., "sequence":...}` (additive, a new
`type`). It is not on the MCP or `/events` surface. `sagefs.nvim` reads `/__sagefs__/reload` payloads in `reload_state.lua` for
outcomes only; I did not read how it treats an unknown `type`, so the first commit checks it and carries a `Plugin:` trailer
(`done` if it needs a branch, `n/a` otherwise). `sagefs-vscode` takes reload reports through the daemon's session field,
which does not change. `apiVersion` does not move. Token cost: none.

Config. A debounce for assets is `Timeouts.fileWatchDebounce` (200 ms), the same as saves. An asset editor writes a CSS file in
one event, so a shorter value is plausible, and the existing roadmap note about the 200 ms debounce being longer than needed
applies. Do not add a new constant until that is measured.

## Tests

RED first, in this order:

1. `FileWatcherTests` (the existing home of `fileChangeAction` tests): "a css file under wwwroot is an Asset of kind Stylesheet,
   a cshtml is a CompiledView, a css file under node_modules is Ignore". Compile-red today.
2. `SageFs.Tests/DevReloadScriptTests.fs` (new): "the two committed copies of devreload.js are byte-identical". Green today,
   so it is a guard committed first, before any edit to either copy.
3. `AssetReloadTests`: table and property tests of `AssetReload.decide`. Property: for any sequence of inputs, the number of
   `ReloadPage` actions is at most one per `SaveFinished`/quiet period, and none is emitted while `SaveInFlight`.
4. `SageFs.Tests/DevReloadTests.fs`: "`payloadJson` of `AssetChanged` is valid JSON with the type, kind, path and sequence, and
   `LastReload.record` leaves the last save verdict alone".
5. `SessionReloadTests.fs`: "`ofPayloadJson` on an asset payload answers `NotASaveEvent`, which is a different case from `UnknownEventType`".
6. The browser gate above.

Simulation. New `SageFs.Simulation/AssetReloadSim.fs` with generators and invariants. Pool of ops: `AssetEdit kind` over the six
kinds and an ignored path, `SaveStarts`, `SaveEnds Applied|Restarted|NoEffect|Failed`, `ClientConnects`, `ClientDrops`. It folds
the real `FileWatcher.fileChangeAction` and the real `AssetReload.decide`. Invariants:

- A1: a stylesheet edit never causes a page reload while a matching link exists in the model page.
- A2: at most one reload per burst between two quiet moments.
- A3: no reload is sent between `SaveStarts` and its terminal event.
- A4: an ignored path produces no event.
- A5: a client that connects after an event does not receive it (events are not replayed).

Twins: `runReloadOnEveryAsset` breaks A1 and A2; `runReloadWhileCompiling` breaks A3; `runWatchEverything` (no pruning) breaks
A4. `WatcherLifecycleSim.fs` gets the new `Asset` case in its match so its existing twins stay meaningful.

Tiers: 1 to 5 and the sim in the default suite. The journey in `--integration-browser`, which already provisions Chromium.
No sleeps: the journey waits on a Playwright condition (computed style, `page.WaitForFunction` on the marker, a navigation
event), and the sim has no clock.

## Steps

1. Guard test 2 and RED test 1. Files: `SageFs.Tests/DevReloadScriptTests.fs` (new), `FileWatcherTests.fs`.
2. `AssetKind`, `DevReloadEvent.AssetChanged`, `payloadJson`, `LastReload.record` rule; tests 4. Files: `DevReload.fs`,
   `DevReloadTests.fs`.
3. `FileWatcher.fs` classification and the new `FileChangeAction.Asset`; fix the four consumers so they compile
   (worker, landing path, two sim files), each with a no-op arm. Test 1 green. Files: `FileWatcher.fs`, `WorkerMain.fs`,
   `SageFs.Simulation/WatcherLifecycle*.fs`.
4. `AssetReload.fs`, its tests and `AssetReloadSim.fs` with twins. File-disjoint from step 5, so they run in parallel.
5. `devreload.js` in both copies, browser journey for css first. Files: the two scripts, `AssetReloadBrowserTests.fs`, a
   fixture with a `wwwroot` (none is tracked today; `SageFs.Tests/fixtures/` gets one).
6. Worker wiring of `AssetReload` into `beginChange` and `ApplySaves`, the broadcast, daemon `NotASaveEvent`. After 3 and 4.
7. Journey rows for js, png, html, razor, ignored. Cache measurement result applied.
8. Docs: `hot-reload.md` and `how-hot-reload-works.md` (the comparison table says Microsoft covers CSS and static files; the row
   changes), and the dashboard hot-reload panel if it lists watched extensions.

First week: steps 1 to 5, ending with the CSS row green on `run_app`. The first commit is step 1.

## Measure first

1. Does a plain `location.reload()` pick up a changed script or image? Experiment in the browser tier: serve `/app.js` from
   `wwwroot` through the fixture, edit it, reload, read the script's marker. Repeat in Chromium and Firefox if the tier runs
   both. A stale result adds the `no-cache` branch to the middleware.
2. Do the watcher's per-extension filters (`watcher.Filters.Add "*.css"` in `startPrunedWatcher`) add noticeable event noise on a
   project with a large `wwwroot/lib`? Count events while `npm install` writes into a watched `wwwroot` on the fixture. If it is
   large, ignore `wwwroot/lib` by default.
3. Where Falco and plain ASP.NET apps started from FSI get their web root. Start the WebLive sample and print
   `app.Environment.WebRootPath`. If it is not the project's `wwwroot`, `urlPath` mapping needs the app's own value, which the
   worker can ask the agent for.
4. How `sagefs.nvim` treats an unknown `type` on the reload stream (read `lua/sagefs/reload_state.lua`).

## Risks and dependencies

- Link matching is a heuristic: a stylesheet reached through `@import`, a CSS-in-JS bundle or a hashed filename will not match
  and falls back to a full reload. That is the correct fail-closed direction, and the overlay says why.
- Two copies of the script is a standing hazard; step 1's guard test is the mitigation.
- Depends on nothing. Nothing depends on it. It shares the watcher with `hot-reload-on-windows-and-macos`, whose
  `FileSystemWatcher` behavior differs by OS; the asset extensions inherit whatever that item finds.
- Razor and Blazor are named in the roadmap and are not delivered here. The item's own title, "CSS and static files", is what
  this plan delivers; the Razor sentence is answered with an honest message.

## Size

S to M. The pure core, the event and the script are days, and the browser journey fixture is the longest part. Razor support
would be a separate L item.

## Decisions for Will

- Razor and Blazor: message only (this plan), or a real route through a C# edit-and-continue service? Recommend message only.
  F# projects cannot hold the views, so the audience is small. Revisit when someone asks with a real project.
- Full reload for images and fonts, or swap them in place? Recommend full reload now, swap later if the flicker annoys.
- Should a CSS change from another tool (a Tailwind watcher writing `wwwroot/css/out.css`) be treated the same? Recommend yes,
  since the watcher cannot tell and the swap is cheap. The cost is more events during a Tailwind rebuild, which the
  200 ms debounce already folds.
