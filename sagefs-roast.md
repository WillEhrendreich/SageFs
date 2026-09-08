# The SageFs Roast

**Taste rating:** Acceptable — the instincts are right, the follow-through is not.
**Single most important observation:** SageFs has the architecture of a demo that grew into a product: a genuinely well-factored core of pure decision modules and a typed worker protocol, buried under a feature accretion of ~60 telemetry modules, three parallel session-state models, and an error algebra that the production boundaries never use.

Every claim below carries `path:line` evidence. No friendship here: the ambition is to be the best F# codebase the world has seen, and it is measured against that bar. Where something genuinely holds to world-class, it is said plainly in the Strengths section. Everything else is a defect you need to see. (Two judgments you may share or reject, stated up front so the rest is not colored by them: the fat-morph dashboard render is treated as correct — payload economy belongs to Brotli and protocol-level diffing, not renderer surgery — and per-session event sourcing is treated as legitimately replaced by the daemon manifest, so the real defect is the leftover half-wired machinery, not the decision.)

---

## 1. Architecture — the hexagon is drawn on top of a monolith

**What world-class looks like:** the domain is a pure, dependency-free center; HTTP/dashboard/MCP/SQLite/process/FSI-host are adapters depending inward; features are vertical slices owning types+logic+persistence; the process boundary between daemon and worker is a typed port, not a namespace coincidence.

### [CRITICAL] SageFs.Core is not a domain core — it is the entire application, in one class library, referenced by two different processes.

- `SageFs.Core/SageFs.Core.fsproj:166` compiles `Mcp.fs` — a 3,581-line MCP tool-and-formatting surface — into the "core."
- `SageFs.Core/SageFs.Core.fsproj:136-141` compiles the deprecated terminal UI (`ElmLoop.fs`, `Editor.fs`, `Screen.fs`, `AnsiEmitter.fs`, `TerminalUI.fs`, `SageFsApp.fs`) into the same library that both the daemon and the FSI host load.
- `SageFs.Core/SageFs.Core.fsproj:221,230-231` references `Microsoft.Data.Sqlite` and `Mono.Cecil`; the host process (net10, isolated for Harmony) therefore links a closure that includes the Elm render loop, the deprecated TUI, and the MCP surface.

The codebase's own files carve out the seams it *wants* to have: `SessionOperations.fs:6-8` ("Pure, deterministic session routing... no side effects, no IO"), `Watchdog.fs:5-7`, `VariantSelector.fs:16-17` — and `WorkerProtocol.fs:212` defines a clean `SessionProxy = WorkerMessage -> Async<WorkerResponse>` function port. So the hexagonal instinct is real; the follow-through is not. The worker host even namespaces itself into the daemon (`SageFs.Host/WorkerMain.fs:1 module SageFs.Server.WorkerMain`), so the "port" is an internal seam between two closures of one bloated library. The daemon and the host can never evolve different cores, and the FSI process ships the TUI it cannot run.

**The fix direction:** split `SageFs.Core` into a true domain library (session lifecycle, workflow, eval pipeline, worker protocol, error algebra, persistence codecs — packages: FSharp.Compiler.Service and Fantomas belong here because FSI *is* the product) plus adapters (process supervision, SQLite, HTTP/MCP, editor wire types) that reference the domain. Delete the retained TUI from the closure rather than forever compiling it into the process that hosts your MCP server.

### [CRITICAL] The five largest files are god files: 15,450 lines of type+logic+formatting+IO in five places.

| File | Lines | What it actually is |
|---|---|---|
| `Features/LiveTestingTypes.fs` | ~4,960 | Live-testing **types AND** the debounce/state-machine reducer (`LiveTestingTypes.fs:3797,3920,3945` — `Succeeded... s.Debounce`, tick effects, type-check requests) |
| `Mcp.fs` | ~3,581 | MCP formatting + tool implementations + session routing + per-session caches |
| `SageFsApp.fs` | ~2,637 | Deprecated TUI Elm model/update/effects |
| `AppState.fs` | ~1,743 | FSI-eval actor + warmup engine + stdout cleaning + ANSI regexes (`AppState.fs:266`) |
| `SessionManager.fs` | ~1,329 | Worker spawn/kill/build + mailbox loop + standby pool |

A file named `LiveTestingTypes.fs` should contain types. It contains the behavior. A file named `AppState.fs` should contain state; it contains an actor, a warmup pipeline, output sanitization, and regexes. These are not style complaints — they are the reason every change to live-testing behavior touches a 5,000-line file and every eval path threads five responsibilities.

### [CRITICAL] `DaemonState.fs` is duplicated across the two projects and the copies already mean different things.

- `SageFs.Core/DaemonState.fs:18` — `module DaemonState` = HTTP daemon probe/state: `SageFsDir`, `httpClient`, `probeDaemonHttpAsync`, `requestShutdownAsync`.
- `SageFs/DaemonState.fs:7` — defines `type DaemonStateChange` (a DU of SSE state events) and then at `:54` a *second* module literally named `DaemonState` that "re-exports functions so existing code using SageFs.Server.DaemonState compiles."

Two independent "state" abstractions with the same name, held together only by a re-export shim. `SageFs/DaemonState.fs:52-53` admits the shim exists "so existing code compiles" — which means the architecture is held together by compatibility, not by design.

### [CRITICAL] There is no single event vocabulary — at least three typed SSE families describe the same worker state, with different serializers.

- `SageFs/DaemonState.fs:7` — `DaemonStateChange`
- `SageFs.Core/SessionEvents.fs:9` — `SessionEvent` (Warmup/HotReload/Workflow), which hand-rolls JSON via `Utf8JsonWriter` at `:34`
- `SageFs.Core/Features/Events.fs` — `SageFsEvent`
- `SseWriter.fs:457-458` acknowledges the split by tracking two "event" types separately.

A workflow switch and a hot-reload change describing the *same* worker state live in different modules with different serializers. One event type, one port, one serializer — this is the "squash the confusion" demand, unmet.

### [CRITICAL] `Features/` is a shared kernel of ~60 accretion modules, not vertical slices — and several are write-only telemetry with a heavy test tax and no product consumer.

`Features/Diagnostician.fs:8,15,22` opens `EvalTimeline`, `EvalRipple`, `Ghostwriter`; `Features/ActionPrioritizer.fs:2-3` opens `CoverageIntel` and `ImpactForecast`. Features cross-wire into each other and are invoked from a mega-hub (`Mcp.fs:652`) rather than owning a bounded vertical. Many exist only in their own file plus unit tests: `Ghostwriter.fs`, `EvalLens.fs`, `WhatIf.fs`, `ImpactForecast.fs`, `ActionPrioritizer.fs`, `Diagnostician.fs`, `CellDependenciesReport.fs`. The friction subsystem is the clearest example: writes flow through `recordToolResult` (`SageFs/McpTools.fs:60`), summaries surface via `get_friction_report` (`McpTools.fs:965`), the dashboard renders a panel (`DashboardFragments.fs:1492`) — but there is no demonstrated decision loop anywhere that consumes friction to change behavior. That is a 6+ test-file subsystem reporting to a store no product surface acts on.

The README itself carries the scar: `Readme.md:1-7` says the author has "extreme cognitive/comprehension debt" from agentic development. The `Features/` directory is the evidence — accretionware with unit tests as its only consumer. Every new capability lands as a new parallel feature tree cross-wired into `Mcp.fs`, growing the hub instead of the product.

### [IMPROVEMENT] The eval pipeline is composable middleware — but its order is a hard-coded literal duplicated in two places, and the "extension point" is not data-driven.

- `SageFs.Core/ActorCreation.fs:9-16` — `commonMiddleware` order: FsiCompatibility, ViBind, OpenDirective, CompExpr, NonBlockingRun, HotReload.
- `SageFs.Core/Middleware/Tracing.fs:95-102` — `namedCommonMiddleware` re-lists the same six with names.
- `AppState.fs:255-256` — `buildPipeline = List.foldBack (fun m next -> m next) middleware evalFn` (order = reverse of the list), and `ActorCreation.fs:110-124` reconstructs names by reference-equality lookup into a Map.

Each middleware is a small composable function (`AppState.fs:182-183`) — good shape. But the stage list is a source literal with a duplicate naming registry, the core `evalFn` (`AppState.fs:337`) is monolithic, and choices inside `NonBlockingRun` are string-matching `if`-chains over code text (`Middleware/NonBlockingRun.fs:26-43`: `code.Contains(".Run()")`, `.EndsWith(".Build().Run()")`). Hot-reload logic decided by grepping the user's source string is exactly the kind of behavior that needs a compiler-model decision, not a `Contains`.

### [IMPROVEMENT] Wire types are re-encoded per client instead of shared.

`HotReloadState` exists in `SageFs.Core/HotReloadState.fs:5`, `sagefs-vscode/src/SageFsClient.fs:50`, and `sagefs-vs/SageFs.VisualStudio.Core/Types.fs:54` — three copies of one concept, each with its own serializer. `sagefs-vscode/src/LiveTestingTypes.fs:1` declares a parallel type-space whose own comment at `:428` admits it. The daemon↔worker codec is excellent and shared (`WorkerProtocol.fs`, `HttpWorkerClient`, `WorkerHttpTransport.fs:35`); the editor wire vocabulary should follow the same pattern: generate or share the contract, never hand-maintain four copies that drift.

---

## 2. Domain modeling — excellent state machines drowned in duplicated state

**What world-class looks like:** precise DUs where wrong states do not compile; total, exhaustive transitions; no null escapes; no `bool` duplicating a DU; structured identities; serialization that does not flatten the model into lossy strings.

### The genuine strength first

`SessionWorkflow` (`WorkflowTypes.fs:58-63`), `WorkflowSwitchOutcome` (`WorkflowTypes.fs:151-163`), `SessionPhase` (`AppState.fs:163-166`), `SessionStatus` (`WorkerProtocol.fs:56-65`), `ExitOutcome`/`SessionLifecycle`, `StandbyState`/`StandbyDecision`, `ActiveSession` — these are real DUs with exhaustive, total transitions. `SessionManager`'s mailbox `loop` (`SessionManager.fs:643-1302`) matches each command exhaustively; `SessionLifecycle.onWorkerExited` is total. I found no `(state, action)` handler relying on wildcard fall-through in the session/worker machines. This is the best part of the codebase, and it is not accidental — it is the pattern the whole codebase needs more of.

### [CRITICAL] The session has three parallel state models that can disagree, plus a bool that duplicates a DU.

- `AppState.fs:163-166` — `SessionPhase` (the rich DU introduced to kill the desync).
- `SessionState.fs:4-9` — the legacy `SessionState` (`Uninitialized | Starting | Ready | Faulted...`), still threaded as a separate loop argument alongside `st` and `SessionPhase` in the same actor handlers.
- `AppState.fs:1129` sets `SessionState.Ready` while publishing `Active(st, Idle)` — the two can disagree by construction.
- `SessionDisplay.fs:17-27` — `SessionSnapshot` carries a rich `Status: SessionDisplayStatus` AND a redundant `IsActive: bool`, plus a `member Activation` that maps the bool back into an `ActiveSession` DU (`SessionDisplay.fs:56-59`). The bool and the DU can contradict; the code has to define an accessor to paper over which one wins.

"Stopped-but-registered," "faulted-with-live-AppState," "stale-and-active" — every illegal combination the `SessionPhase` DU was meant to kill is still representable *somewhere*, kept honest only by convention. This is the single largest modeling debt: one lifecycle, one type, total transitions, and the compiler enforces it.

### [CRITICAL] `AppState.fs` uses `Unchecked.defaultof<_>` tombstones and `isNull` checks to represent a "faulted" session — a null escape in a codebase with `CheckNulls=true`.

- `AppState.fs:1606-1620` — the faulted tombstone allocates `Session = Unchecked.defaultof<_>` and `OutStream = Unchecked.defaultof<_>` and relies on `isNull (box st.Session)` guards (`AppState.fs:328-335`) to avoid touching them.
- `AppState.fs:147` — `EvalResponse`'s error channel is `Result<string, Exception>`, leaking .NET exceptions as domain outcomes; consumers parse `.Message`.

`Directory.Build.props:8` turns on `CheckNulls` and `TreatWarningsAsErrors` — and the code then smuggles nulls through `Unchecked.defaultof` and guards with `isNull (box ...)`. A "Faulted session" should be a `SessionPhase` case with no `Session` field at all — that is what makes the state unrepresentable. Instead the actor allocates garbage objects and prays no code path touches them.

### [CRITICAL] Session identity and ports revert to primitives exactly at the boundaries that matter: persistence, SSE events, and the live-testing wire.

- `Features/LiveTestingTypes.fs:3234` — `TypeCheckRequest.SessionId: string option`; the type-check request that drives the whole live-testing pipeline is string-typed.
- `WorkerProtocol.fs:15` defines a single-case `SessionId` DU honored only in the supervisor core; `Features/ManifestPersistence.fs:11,20` persists `SessionId: string` / `ActiveSessionId: string option` — with a header that hard-assumes IDs are ≤20 chars (`ManifestPersistence.fs:98`) while `SessionId` is exactly 8 hex chars.
- `WorkerPort: int option` + `Status` DU + `WorkerBaseUrl: string` + a `hasValidReadyTransport` predicate (`SessionManager.fs:210-219`) — the URL/port/status relation is three nullable fields, not a state machine.

The precise DUs get flattened to `string sessionId`, `string label`, `bool hotReloadActive`, raw `int64` timestamps at the persistence/SSE/dashboard edges — and the daemon re-derives meaning from raw string prefixes on the way back in.

### [IMPROVEMENT] `failwith` and exceptions as domain outcomes in Core.

- `AppState.fs:867` — `failwith msg` when expected project assemblies did not load (a user-facing state transition, thrown as an exception).
- `AppState.fs:1095` — `Error (InvalidOperationException message)`: an exception instantiated as a *value* in the error type.
- `Features/LiveTestingTypes.fs:573-590` — three `failwithf` on size mismatch inside pure bitmap operations (should be `Result`).
- `ModelSnapshot.fs:89` — `Option.get` in a snapshot mapper; non-total access in a pure fold.

The infrastructure rethrows (`Mcp.fs:874/903/1269`, `FrictionSqlite.fs`, `NamedPipeTransport.fs`) are defensible — those are genuine IO/actor boundaries. The domain-level `failwith`/`Option.get`/exception-as-value are not.

### [IMPROVEMENT] Primitive obsession is systemic at the edges.

`Measures.fs` defines `float<ms>` and is nearly dead code — durations are raw `int64` (`WorkerStatusSnapshot.AvgDurationMs`), timestamps raw everywhere, ports raw `int`. `DashboardTypes.fs:404` parses unknown status strings to `Running` (`| _ -> SessionDisplayStatus.Running`) — a silent stringly-typed fallback that turns a contract violation into a wrong-but-plausible state. The units module is a good idea that was abandoned after one file.

### [CRITICAL] The binary readers trust header counts and strides instead of section sizes.

- `Features/SessionPersistence.fs:264-271,332-347` — the `.sagefs` INPT parse reads `count`/`stride` from the header and computes `tocStart + i*stride` and `tocStart + count*stride` with no check that they stay inside the section payload. The CRC protects against corruption but not against a *consistent* malicious rewrite; `parseInpt` ignores the section's declared size (`e.Size`) entirely.
- `BinaryFormat.fs:42-79` — `readLpString`'s bounds check is against the full stream, not the section slice.
- `Features/TestCachePersistence.fs:174-187,302-324` — `.sagetc` `parseImap` bounds-checks `wc*8` (good), but the codec is lossy: `Outcome.Error` round-trips to `NotRun` (`:362`), `NotRun` to `Skip`, `Fail` to `AssertionFailed`. A distinct state is silently destroyed on disk.
- `Features/ManifestPersistence.fs:151,169-170` — `_totalSize` is read and ignored; declared header counts are never cross-checked against actual data length.

The format has the right bones — versioned envelopes, CRC, section directory, 176 property tests — but the readers are not uniform: some bounds-check, some trust. "OOM/corruption-safe" is only as true as the weakest reader, and the weakest readers trust `stride`.

### [IMPROVEMENT] The writer re-derives what the model already knew, lossily.

`Features/SessionPersistence.fs:404-434` reconstructs entry kinds by string-guessing: `e.Code.StartsWith("#r ") → "Directive"`, `EndsWith(".dll") → DllPath`, `Contains("nuget:") → NuGet`; and a `Status` restoration that maps any non-empty interaction list to `ReplayStatus.Ready` (`:452-455`). Writing back should serialize the actual DU the session held — not infer it from substrings that a user's `#r`-shaped variable name would misclassify.

---

## 3. CQRS and the Tao of Datastar — server authority yes, transport-level incremental delivery

**What world-class looks like:** the server owns the DOM and renders authoritatively; controls reflect real state; the browser never holds a second source of truth; and payload economy is the *protocol's* job, not the renderer's — Brotli-compressed SSE plus wire-level diffing make a full fat-morph push cheap, so the application keeps one simple whole-page render instead of fragment plumbing. SageFs got the hard half right (server authority, one render path, no divergence). What it got wrong is pushing full morphs on timers when nothing changed, over an uncompressed stream.

### [IMPROVEMENT] The whole-`#main` fat morph is the right call — keep it. The defects are no-change timer pushes and an uncompressed SSE stream.

- `SageFs/Dashboard.fs:12-30` — the header doctrine is correct: one render function, one morph, server-authoritative, "DO NOT DIVERGE." Splitting `renderMainContent` into per-fragment patch functions would trade a simple, provably-consistent renderer for codebase complexity — reject that.
- `Dashboard.fs:522` — one `ssePatchNode (renderMainContent snap)` per state change is fine; making the payload cheap belongs to the transport layer, not the renderer.
- `Dashboard.fs:615-622` — the SSE fallback poll (`while not ctx.RequestAborted do Task.Delay(sseEventInterval); pushState()`, `sseEventInterval = 1s` at `Timeouts.fs:93`) pushes a full morph every second even when nothing changed.
- `DaemonMode.fs:2194-2208` — the daemon dispatches `ListSessions` every 10 seconds into the event stream; `DaemonMode.fs:1596-1600` — a 200ms test-cycle timer feeds `ModelChanged`. Both fire when the snapshot is unchanged, and each triggers a full render + morph.

The fix, in order of leverage — all protocol/transport work, zero renderer surgery:
1. **Suppress no-change pushes.** A change counter/version guard before `pushState` means timers never emit a morph for a byte-identical snapshot. This is the only defect that actually costs CPU.
2. **Enable Brotli compression on the SSE/HTTP response.** Repeated dashboard markup compresses extremely well; this is server configuration, not codebase complexity.
3. **If wire size still matters, diff at the protocol level** — delta-encode consecutive morph payloads in the SSE writer — never by splitting the fat morph into per-panel fragment functions in application code.

### [CRITICAL] The MCP tool list is never filtered by session state — and the gate that would do it (`requireTool`) is dead code.

- `Affordances.fs:41-81` enumerates per-state tool names; the gate exists at `Mcp.fs:1091-1098` (`requireTool` → `SageFsError.ToolNotAvailable`).
- Grep shows `requireTool` is **never called** by any tool handler — the enforcement is dead code. The README's headline claim ("AI agents only see tools valid for the current session state") is not what ships: an agent in `WarmingUp` sees `send_fsharp_code` in the tool list and must learn from prose or error text that it is unavailable.
- Affordance names are returned inside *status output* (`Mcp.fs:336-359`), not applied to the tool listing.

The affordance-driven state machine is a genuinely good idea that was half-implemented: the list is computed, the error case is defined, and the wiring that filters the actual MCP tool table was never connected.

### [IMPROVEMENT] Several controls are fire-and-forget with no press feedback, and RESET/HARD_RESET are state-agnostic.

- `DashboardFragments.fs:1418-1429,1447-1464` — Watch All / Unwatch All / per-file toggles are raw `fetch()` calls (`Attr.create "onclick"`) with no `Ds.indicator`, no inline status, response dropped; they bypass Datastar entirely and the only correction is a later unrelated SSE push.
- `DashboardFragments.fs:1186-1195` — RESET and HARD_RESET have no `Ds.indicator` and are not disabled during eval; only EVAL binds to the loading signal (`:1181`).
- Reset (`Dashboard.fs:824-850`) awaits the whole soft-reset before writing "Reset: ..." — no "resetting…" affordance appears pre-op. Session create (`Dashboard.fs:1206-1230`) shows nothing until a 15-30s warmup completes.

The teardown path is the model to copy everywhere: `Dashboard.fs:880-889` swaps the card for `renderStoppingCard` ("⏳ Stopping session id:...") *before* the await. Create and reset do not follow their own best pattern.

### [IMPROVEMENT] The page shell has no true single-column flow at narrow widths.

The session cards inside the sidebar are exemplary (`dashboard.css:594-652`: `grid-template-columns:minmax(0,1fr)`, actions `flex-wrap: wrap`, `overflow-wrap:anywhere`) — the doctrine's responsive demands are met *inside the card*. But the app itself is a fixed horizontal shell: `body { display:flex; overflow:hidden }` (`dashboard.css:64-75`), a fixed 320px sidebar with `flex-shrink:0` (`:138-165`), fixed header/statusline/cmdline heights, and a single `@media (max-width:768px)` that only overlays the sidebar instead of restacking (`:1096-1108`). At half width the content pane is squeezed, not wrapped — the exact "cramped with dead zones" failure mode the card design was built to avoid. And the layout lives in ~150 inline `Attr.style` calls with only a few semantic classes; `dashboard.css:1110-1113` itself ends with the note "Semantic Utility Classes — Replace repeated inline Attr.style patterns" — the stylesheet admits the debt.

### [IMPROVEMENT] Dashboard controls mix three idioms and inconsistent sizes.

`◎/◌/■/⌫/✖/⇄` glyph buttons (uniform 28px `.session-btn`, good), emoji (`🧙⏱📁🤖🔴⚠️🚨⚡`), and text-in-brackets `[EVAL] [RESET] [HARD_RESET] [CLEAR]` — three control idioms on one page, with `session-btn` at 28px but `eval-btn`/`panel-header-btn` auto-sized. Rem-based font sizes in fragments (`0.6rem/0.65rem/0.7rem`) do not scale with the Ctrl+/- zoom that sets `--font-size` on `:root` (only base `body` consumes it) — zoom resizes the page unevenly by construction.

### [IMPROVEMENT] The viewed session can dangle when it dies outside the button-initiated teardown path.

`createStreamHandler` resolves one immutable `currentSessionOpt` per connection (`Dashboard.fs:455-486`); only the session's own stop/dispose path re-routes. If the daemon crashes, a worker dies, or another client disposes the session, the stream keeps pushing snapshots for the dead id — rendered as `SessionDisplayStatus.Lost` placeholders (`DashboardTypes.fs:545-557`) with no auto-advance and no picker. The button path auto-selects next-or-picker correctly (`Dashboard.fs:890-958`); every *other* death path dangles.

---

## 4. Actors and ownership — one supervised resource, and it is the wrong one

**What world-class looks like:** every owned resource has a single owner actor with supervision/restart; messages are the API; lifecycle is explicit and tested. SageFs has exactly one genuinely supervised, single-owner resource: the worker *process* tree (SessionManager mailbox + `RestartPolicy` backoff). Everything else is advisory coordination.

### [CRITICAL] The supervisor itself is not supervised — one unexpected exception kills the SessionManager mailbox permanently, orphaning every session.

`SessionManager.fs:1304-1309`:
```fsharp
async {
  try
    return! loop ManagerState.empty
  with ex ->
    Log.error "[SessionManager] Mailbox died unexpectedly: ..."
}
```
There is no `return! loop` after the log and no supervisor above the supervisor. Per-worker process restart has full backoff (`RestartPolicy.fs:55-62`); the actor that *owns* that restart has none. `ResilientActor.wrapLoop` only catches and logs — it never restarts. The eval/query/router actors (`AppState.fs:1070-1624`) are fire-and-forget `MailboxProcessor.Start` with no bounded queue and no restart-on-death.

### [CRITICAL] `WorkerReady` and `WorkerSpawnFailed` ignore `workerPid` — a dead worker's late events are applied to its replacement.

`SessionManager.fs:889,995` — both handlers bind `_workerPid` and never compare it to the session's current `WorkerPid`; only `WorkerExited` has the stale-pid guard (`:1015-1027`). A hard reset kills the old process (`:776`) while its `awaitWorkerPort` task is still reading stdout; on EOF it posts `WorkerSpawnFailed`, which the handler applies to the *fresh* worker — tombstoning it to `Faulted` with a `pendingProxy`. The pid is on the message precisely so this race can be closed, and two of the three handlers throw it away.

### [CRITICAL] The cold-restart `dotnet build` runs inside the mailbox loop, blocking every session operation for up to 10 minutes.

`SessionManager.fs:801-803` does `do! runtime.RunBuildAsync ...` inline, and `runBuildAsync` has a 600,000ms kill timer (`:477`). The file header at `:434` claims "Async so we don't block the MailboxProcessor during build" — but a 10-minute `do!` inside the single mailbox serializes CreateSession/StopSession/WorkerExited for *every* session behind the build.

### [CRITICAL] Duplicate-session creation is a check-then-act TOCTOU: nothing enforces one session per (projects, workingDir).

`Mcp.fs:1881-1900` reads the CQRS snapshot and *warns* if a duplicate exists, then calls `SessionOps.CreateSession`; the mailbox `CreateSession` has no dedupe. Two concurrent `create_session` calls (two agents, same directory) both pass the advisory guard and both spawn — reproducing the "Multiple sessions match workingDirectory" ambiguity the guard was built to prevent. Uniqueness must be enforced by the single owner (the mailbox), not advised by a snapshot read.

### [CRITICAL] `daemon.sagefm` has three unsynchronized writers.

`periodicManifestSave` (DaemonMode.fs:796-828, timer thread), the shutdown save (DaemonMode.fs:654-668), and `PurgeSession → removeManifestEntry` (DaemonPersistence.fs:88-104, MCP tool threads) all do read-merge-write to the same `.tmp` + `File.Move` (ManifestPersistence.fs:245-262) with no lock and no single owner. A purge concurrent with a periodic save can interleave tmp writes or lose an update. The codebase *knows* single-owner discipline (the worker process tree is proof); the manifest is written like a shared `ConcurrentDictionary`.

### [CRITICAL] `WorkerLivenessMonitor.fs` — written, tested, and never wired in: hung-but-alive workers are never detected.

The module is pure and fully tested, but its only consumers are tests. Production relies solely on `proc.Exited` and the warmup poll (`SessionManager.fs:928-958`). A worker that stops answering HTTP while its process stays alive is never reaped and never restarted — the exact case `MissThreshold=3`/`HealthCheckTimeout` was built for. Dead code that would catch the failure mode the daemon is blind to.

### [CRITICAL] The eval actor is the one actor NOT wrapped by `ResilientActor.wrapLoop` — one unhandled exception bricks eval and reset for that session.

`AppState.fs:1070-1624` — the per-session FSI owner runs with a big internal try/with but its message loop has no per-message guard, and several handlers mutate `st.OutStream`/session directly (`AppState.fs:1088`) with no catch. The query actor (`:1026`) and router (`:1701`) get `wrapLoop`; the actor that owns the FSI session — the most failure-prone resource in the system — does not.

### [IMPROVEMENT] A late `EvalFinished` after reset/hard-reset republishes pre-reset state wrapping a disposed FSI session.

Eval runs on a dedicated thread (`AppState.fs:1111-1121`) that posts back `EvalFinished(Ok(res,newSt))` with the *old* `st`; reset joins for only 2s (`:1212-1218`) and, on timeout, disposes the session and publishes new state. The straggler `EvalFinished` then runs `publishSnapshot newSt` (`:1131`) — resurrecting a snapshot of a disposed session. Generation counters exist in this codebase for SSE staleness; the eval actor needs the same generation discipline against its own resets.

### [IMPROVEMENT] `SessionMap` (agent→session) grows forever.

`Mcp.fs:691-692,997-1008` — `setActiveSessionId` overwrites entries with `""` but nothing ever removes them; the 60-second cleanup timer (DaemonMode.fs:1639-1655) evicts only `AgentActivityTracker` presences. Every agent name that has ever connected remains a key for the daemon's lifetime. `AgentActivityTracker` itself is bounded and reaped correctly — this leak is SessionMap-specific and trivially fixable with the same discipline.

### [IMPROVEMENT] The live-test watcher is a lock-based multi-writer object, not an actor.

`DaemonMode.fs:850-1050` — five `ConcurrentDictionary`s plus `pendingLock`, mutated from FileSystemWatcher threads, a shared debounce Timer, and a 5-second sync timer. The entire `LiveTestWatcherStaleGuard` epoch machinery is a *mitigation* for the lack of a single owner. Stop/dispose vs. queued-event races have to be defended by hand instead of eliminated by construction.

---

## 5. Event sourcing — it was dropped, and the replacement is honest about it

**What world-class looks like (if present):** events are the source of truth or a faithful replayable audit; projections are derived; the filmstrip does not diverge from persisted truth.

### [CRITICAL] Per-session event sourcing was removed and replaced with a write-only trail; only the daemon manifest is durable and replayable.

- `DaemonMode.fs:1478` — `let sessionOps = createSessionOps sessionManager readSnapshot (fun _ -> ())  // Event tracking removed — no callback needed`
- `Features/Replay.fs` (a genuine pure fold, `applyEvent`/`replayStream`) and the `.sagefs` binary persistence are faithful but unused: `saveSession` has **no production caller** — only tests. Disposal even *deletes* the never-written file (`DaemonMode.fs:299`).
- The actual replay path that works is `daemon.sagefm` (`DaemonReplayState`), loaded at startup to rebuild previous sessions (`DaemonMode.fs:1130-1178`).

So: one replay path is real (manifest), one is a vestige (Replay/.sagefs), and the "filmstrip" and "eval timeline" features are formatting over in-memory `EvalHistory` (`Mcp.fs:2728-2736`), consistent with persisted truth only to the extent the in-memory history is. `TimeTravel.fs` is a TUI *viewer* over client-side `RenderRegion` lists (`TuiClient.fs:262`), not a restore mechanism — nothing re-applies snapshots to daemon or session state.

The audit's honest verdict: dropping per-session event sourcing for the manifest is a defensible simplification *if* the manifest is the single durable source and the lossy filmstrip/timeline features are clearly labeled as telemetry. Today the label is a comment at one call site. Decide: either the manifest is the source of truth and the Replay/.sagefs machinery is deleted (it is misleading dead weight), or per-session events are restored for auditability. Keeping both, with one unwired, is the worst option — and it is the current one.

---

## 6. Performance — the O(n²) is real, and it runs on every eval

**What world-class looks like:** allocation-conscious hot paths, documented complexity, no sync-over-async in daemon code, no per-keystroke/per-eval quadratic work, bounded queues. The good news: bounded ring buffers (500), the LRU cache, and capped lists exist and are mostly respected. The bad news is what sits on top of them.

### [CRITICAL] Per-eval O(n²): every eval rebuilds the whole binding scope by re-parsing all up-to-10,000 retained results and running one regex per binding against every prior cell.

- `Features/FeatureHooks.fs:71-89` — `recordEval` (called on every eval, daemon and MCP paths) does `cappedHistory |> List.rev |> List.map ...` then `BindingExplorer.buildScopeSnapshot allCellInputs` — parsing every retained result, then compiling and running one `Regex(@"\b<name>\b")` per binding against every prior cell (`BindingExplorer.fs:82-88`).
- `MaxEvalHistory = 10_000` caps the *count*; each entry carries full `Result` + `Code` strings, so *bytes* are unbounded.
- `EvalTimeline`/`SessionFilmstrip` then do `List.rev |> List.truncate 20` over that 10k list on every filmstrip render (`Dashboard.fs:1880-1898`) — a full `List.rev` of 10k entries per SSE push.

The code comments claim this O(n²) was removed ("W5/R8"); the per-bindings × per-cells cross product is still intact. For a product whose headline is "sub-500ms feedback on every save," the eval hot path rebuilding a 10,000-entry scope with regex-per-binding-per-cell is the dominant algorithmic sin in the codebase.

### [IMPROVEMENT] The dashboard SSE path re-reads the entire SQLite friction store synchronously on every push.

`Dashboard.fs:419-427` — `reportDirect` → `Async.RunSynchronously` inside the friction panel render, which runs on the SSE push path (`buildDashboardSnapshot → frictionPanel`). Every dashboard SSE morph (10s tick, 200ms test tick) performs a synchronous SQLite read. This is the "live" view blocking on disk per push.

### [IMPROVEMENT] Sync-over-async clusters at warmup, per-session startup, and per-result streaming.

- `AppState.fs:665` — `Async.RunSynchronously` + `Async.AwaitTask` inside the eval actor's warmup path (holds the actor that must stay responsive to CancelEval).
- `HttpWorkerClient.fs:118,172` — per-test-result-line `readTask.Result` in a while-loop, allocating a `Task.Delay` + `Task.WhenAny` race per line.
- `SessionManager.fs:928-958` — after every `WorkerReady`, a 1s `Async.Sleep` poll of `/status` over HTTP until Ready or a 120s timeout. N warming sessions = N workers × one GET/second. Startup-only, but it is HTTP churn at the exact moment the session is trying to warm up.
- `Dashboard.fs:427,874`, `Program.fs:251-254`, `EnvCheck.fs:154-157`, `DaemonState.fs:138,164` — startup/one-off `RunSynchronously`, each individually defensible, collectively a pattern of convenience.

### [IMPROVEMENT] Per-eval reflection walk over every bound value, on the eval thread.

`AppState.fs:1135-1157 → LiveValueTree.fs:196-262` — every successful eval walks `newSt.Session.GetBoundValues()`, running `FSharpValue.GetRecordFields`/`GetUnionFields`/`t.GetProperties()` + `GetValue` on each, serializes to JSON, and ships it for the adaptive store — bounded by depth 6/50 children/500 chars, but it runs on the eval thread between eval completion and the `EvalCompleted` publish, and it runs per eval.

### [IMPROVEMENT] The `SyntaxHighlight` cache key is the entire code text.

`Features/SyntaxHighlight.fs:103-159` — key = whole code + theme color; a one-character edit is a full tree-sitter re-parse miss. The 512-entry LRU will thrash on large buffers. (This is on the deprecated TUI render path — `Screen.fs:313-314` — so it is legacy-adjacent, but the cache design is the lesson.)

### [IMPROVEMENT] Memory growth without eviction.

`SessionOutputStore` (500-slot ring per session) never evicts buffers on session stop (`SageFsEvent.fs:25-146` — `Clear` is never called from stop paths); buffers grow with the count of distinct session IDs ever seen in one daemon run. `BatchFlusher` is count/time-bounded but not byte-bounded.

---

## 7. Data access and persistence

**What world-class looks like:** parameterized queries, versioned schema with a migration story, a single owner per persisted file, stable binary formats with uniform bounds checks. Verdict: SQLite is parameterized and fine; the file-based persistence has real single-owner and bounds gaps.

### [IMPROVEMENT] The single strongest data-access finding is the unsynchronized `daemon.sagefm` writers (see Actors §4) — read-merge-write with no owner.

The schema side is genuinely good: `FrictionSqlite.fs:273-291,302-315,387-400` uses `$named` parameters throughout; the only interpolation is DDL/table-name (`:258`), and those are module constants, not user input. Schema versioning is ad hoc — "try ALTER, swallow error if column exists" (`:255-262`) with no `PRAGMA user_version` — acceptable for a local telemetry table, not a pattern to copy.

### [CRITICAL] The binary readers are not uniformly bounds-checked (see Domain §2) — the CRC gate protects against corruption, not against a consistent malicious rewrite, and `parseInpt` ignores the section's declared size.

### [IMPROVEMENT] Session persistence is written but never used by production.

The `.sagefs` per-session format has no production caller (`saveSession` is test-only; dispose deletes the file at `DaemonMode.fs:299`). The only durable per-daemon state is `daemon.sagefm`. Either the per-session format becomes the session journal (restoring output/timelines on resume) or it is deleted — today it is 300+ lines of tested code guarding a file that production never writes.

---

## 8. Security — unauthenticated loopback with no origin checks and a `@develop` CDN script

**What world-class looks like for a local dev daemon:** bind loopback; treat the browser as an untrusted peer — validate Origin/Host on every mutating route; sandbox or token-gate anything that executes code or spawns processes at arbitrary paths; never fetch unversioned third-party scripts; justify every alpha/preview pin.

### [CRITICAL] The dashboard and MCP servers bind localhost but have no authentication, no CORS config, and no Origin/Host check anywhere — while exposing fully mutating endpoints.

- `McpServer.fs:2018-2022` binds MCP to `SAGEFS_BIND_HOST` or `"localhost"`; `Dashboard.fs`/`DaemonMode.fs:1104-1108` binds the dashboard the same way.
- Grep for `UseCors|WithOrigins|Origin` across the repo: no matches. No `UseAuthentication`, no CSRF token, no `Sec-Fetch-Site` check, no required custom header.
- The mutating surface is large and unguarded: `POST /dashboard/eval`, `/dashboard/session/create`, `/dashboard/session/stop`, `/api/sessions/create` (`McpServer.fs:1700-1746`) accepting **arbitrary `workingDirectory` and project paths**, `/exec`, `/load-script`, `/api/shutdown` (`Dashboard.fs:1691-1694`), hot-reload proxy endpoints, `/api/dispatch`.

A malicious webpage can POST to `http://localhost:37749/` or `:37750/` cross-origin (DNS-rebinding defeats same-origin assumptions; `Host` is never validated). It can create a session rooted at an attacker-chosen path, evaluate arbitrary F# in it — code that executes with the logged-in user's full privilege — and call `/api/shutdown` to kill the daemon. This is the single highest-severity finding in the audit. VS Code solved this exact problem years ago with the port-plus-token model; SageFs has the token machinery for outbound friction (`X-SageFs-Token`, `Dashboard.fs:1121`) but no inbound requirement.

### [CRITICAL] The dashboard loads Datastar from an unversioned `@develop` CDN branch.

`Dashboard.fs:215`:
```fsharp
Elem.script [ Attr.type' "module"; Attr.src "https://cdn.jsdelivr.net/gh/starfederation/datastar@develop/bundles/datastar.js" ] []
```
`@develop` is a moving, unversioned branch — a supply-chain and XSS surface. Combined with the unauthenticated mutation endpoints above, a compromise of that CDN path (or a version skew) means full control of every open dashboard tab and arbitrary code execution in the daemon through `eval`. The comment explains Falco.Datastar 1.3.0's `cdnScript` points at an older RC lacking the `class:` plugin — the answer is pinning a known-good Datastar build and serving it from the daemon (it is a localhost app; the bytes cost nothing), not fetching a branch at runtime.

### [CRITICAL] Worker HTTP servers are unauthenticated loopback, and the dashboard proxies arbitrary worker paths without validation.

`WorkerHttpTransport.fs:67` binds `http://127.0.0.1:%d` and exposes `/eval`, `/check`, `/load-script`, `/shutdown`, `/run-tests`, `/hotreload/watch-directory` with no auth. `DaemonMode.fs:598-609` (`createHotReloadProxyEndpoints`, `proxyGet`/`proxyPost`) is an HTTP passthrough that forwards any path to the worker — and forwards `ex.Message` verbatim into the 502 body (`DaemonMode.fs:565`). Any local process, and any cross-origin page via the dashboard port, can eval code in the user's session or call `/shutdown` on the worker.

### [IMPROVEMENT] Path validation exists only on the two file-read endpoints — and was clearly added reactively.

`createEvalFileHandler` (Dashboard.fs:714-737) canonicalizes + `ResolveLinkTarget` + containment-checks; MCP `/load-script` (McpServer.fs:1237-1258) does the same. But `/api/sessions/create` (McpServer.fs:1700-1734) accepts arbitrary `workingDirectory`/`projects` and feeds them straight into `ProcessStartInfo.WorkingDirectory` (`SessionManager.fs:240`) and `dotnet build` `ArgumentList` (`:447-456`) with no canonicalization, no `..` containment, no UNC check (`resolveSessionProjects`, DashboardTypes.fs:844-877). Combined with the missing origin checks, a page can point a session at any readable directory. Process args themselves are structured (`ArgumentList`, validated 8-hex session ids) — that part is done right.

### [IMPROVEMENT] Secrets and internals leak into logs and error bodies.

Worker stderr — which can contain user code output including pasted secrets — is forwarded into daemon logs at spawn-failure summaries (`SessionManager.fs:300-312,333-351` include full stderr). The global error middleware writes `ex.Message` verbatim as the 500 body (`McpServer.fs:304-317`); the hot-reload proxy forwards worker `ex.Message` (`DaemonMode.fs:565`); the dashboard 500 path does the same (`Dashboard.fs:754-758`). Exception messages can contain absolute paths. `/api/status`, `/health`, `/api/daemon-info` expose `workingDirectory`/project paths of all sessions to any caller (`McpServer.fs:1573,1352`, `Dashboard.fs:1673-1687`). `~/.SageFs` and its manifests/friction DB are created with the default umask and no ACL hardening (`DaemonMode.fs:154-158`) — world-readable on multi-user Linux/macOS by default.

### [IMPROVEMENT] Supply-chain posture is inconsistent: one excellent justified CVE pin and a dozen silent alpha/preview pins.

The SQLitePCLRaw pin is a model of what a pin comment should be (`Directory.Packages.props:50-55`, CVE-2025-6965, first patched release, transitive resolution explained). But FSharp.Compiler.Service `43.13.101-preview7`, FSharp.Core `11.0.101-preview7`, Expecto `11.0.0-alpha8`, Fantomas.Core `8.0.0-alpha-002`, ModelContextProtocol `1.0.0-rc.1`, Serilog.Extensions.Logging.File `9.0.0-dev-02303`, StreamJsonRpc `2.23.32-alpha` carry no justifying comment at all. The tree-sitter native binaries ship per-RID with no hash verification (`SyntaxHighlight.fs:44-60` is just `File.Exists` + path probing), and there is no `packages.lock.json`/RestoreLockedMode.

### [IMPROVEMENT] Debug and undocumented endpoints ship unconditionally.

`/diag/threadpool` (McpServer.fs:1389, WorkerHttpTransport.fs:101), two overlapping cancel endpoints (`/cancel` + `/api/cancel-eval`, McpServer.fs:1202-1217), and overlapping route families across MCP (37749) and dashboard (37750) ports with no API reference (`EndpointContracts.fs` declares an `apiVersion` nothing enforces). `POST /api/shutdown` has no body, no auth, no header check.

---

## 9. Testing doctrine — strong core discipline buried under a flat 300-file pile with a dead default-suite filter

**What world-class looks like:** tests mirror the feature map; properties dominate logic tests; message-first Expecto.Flip everywhere; fast default suite with genuinely slow integration gated behind an explicit opt-in; no `Async.RunSynchronously`/`Thread.Sleep` polling in test bodies; global snapshot scrubbers at the harness root; every commit green.

**The strengths:** property discipline in the core is genuinely strong — 527 `testProperty` occurrences across 83 files; `BinaryFormatTests` (49 properties incl. corruption), `CompilationContextPropertyTests`, `WorkflowProperty/TransitionProperty`, `EventFoldProperty`, `Lifecycle` — this is the doctrine working. Integration tests isolate the real daemon from the user's `~/.SageFs` correctly via `SAGEFS_DATA_DIR` temp overrides (`HttpApiIntegrationTests.fs:122`, `DaemonIntegrationTests.fs:233`). And the total line count (88k across ~290 compiled files) is a real investment. The problems are structural, not effort.

### [CRITICAL] All ~290 test files sit flat in one directory — no unit/integration/snapshot split, no mirror of `SageFs.Core/Features/`.

The only subfolders are `fixtures/` and `snapshots/`. `Program.fs:142-151` discovers everything via `Impl.testFromThisAssembly()`. A 290-file flat pile cannot be navigated, cannot be reasoned about at a glance, and cannot enforce a fast/slow split by location.

### [CRITICAL] The default-suite filter is a name-convention string check and the genuinely slow suites leak into it.

`Program.fs:140-150` filters by `not (name.Contains "[Integration]")`. NamedPipeTransportTests (real pipe round-trips with `.Result` at `:124/165/198`), ElmLoopResilienceTests (`releaseGate.Wait()` 2s timeouts at `:473/558/600`), JupyterKernelTests (846 lines of blocking handler evals), InstrumentationTests, and the RoundNN "hardening" suites all run in the default tree with no `[Integration]` marker. And because the filter is a name check, a suite that forgets the tag silently joins the default run — the mechanism cannot be trusted.

### [CRITICAL] Banned blocking patterns pervade test bodies — ~70 `Async.RunSynchronously`, ~35 `.Result`/`.Wait(`/`GetAwaiter().GetResult()`, ~25 `Thread.Sleep` poll sites — including in default-suite tests.

Representative violations: `JupyterKernelTests.fs:398/421/434/...` (25 `RunSynchronously`), `NamedPipeTransportTests.fs:31-62`, `ElmLoopResilienceTests.fs:410/473/486/558/600/612`, `FalcoTests.fs:174/273`, `EvalCancellationTests.fs:22/44/50`, `McpServerIntegrationTests.fs:35-295` (15 `RunSynchronously`), plus fixed sleeps (`Thread.Sleep(500/1000)` in `DaemonStateChangeContractTests.fs:177`, `WebAppHotReloadVerificationTests.fs:347`, `ParentMonitorTests.fs:67`). The justified exceptions are few and documented (`TestInfrastructure.fs:99` deliberate lazy async bridge, `HttpApiIntegrationTests.fs:338` one-time daemon startup). The rest are `task { }` bodies that could simply `do!`. This is exactly the pattern the repo's own policy bans — and the suite's timeouts are the bill.

### [CRITICAL] Snapshot/Verify scaffolding is fragmented: four files re-declare the snapshots dir and per-file scrubbers, on top of the global root scrubber.

`SnapshotTests.fs:13-19`, `DashboardSnapshotTests.fs:16-23`, `ThemePersistenceTests.fs:22`, `ToolDescriptionTests.fs:15-22` each hardcode `Path.Combine(__SOURCE_DIRECTORY__, "snapshots")` and do their own `.Replace("\r\n","\n")` — Verify is configured twice per file, and `SnapshotTests.fs:11` even re-runs `DisableRequireUniquePrefix()` behind a swallowed `try/with`. The AGENTS.md-required "line-ending scrub built into the harness root so it applies to every snapshot test" exists at `Program.fs:95-97` — and is then overridden per-file instead of trusted.

### [CRITICAL] Tautological and self-contradicting assertions would not catch the regressions they name.

- `DaemonHealthTests.fs:54` — test name says "unhealthy when no sessions exist," assertion expects `OverallHealth.Healthy`. A regression from Healthy→Unhealthy would *pass*.
- `McpLlmInteropTests.fs:468` — `Expect.isTrue true "Should not throw"`: asserts nothing.
- `DaemonHealthTests.fs:84-85` — asserts emoji/labels equal themselves.
- `DemoRecording.fs:1105-1107` — permanently-ignored `ptestCase` no-ops that silently "pass" when VS Code is absent.
- `DevReloadCanaryTests.fs:89-91/133` — all three match arms pass.

A test whose name and assertion disagree is worse than no test: it is a green lie in the exact place the suite claims coverage.

### [CRITICAL] 30+ test files are orphaned (not in the .fsproj compile list) or compiled-but-empty, inflating the suite count and the maintenance tax.

`VtInputParserTests.fs`, `TuiClientTests.fs`, `SageTuiAllocationBenchmarks.fs`, and both `CoverageViewTests.v1.fs`/`CoverageViewSseEventTests.v1.fs` are dead weight never compiled. `Tier0CorrectnessTests.fs` is compiled but contains only a comment ("These tests tested MCP-side result collection logic that no longer exists"). `SharedSageFsFixture.fs` (fixed `McpPort=54321`, a 300-attempt poll harness) is compiled and referenced nowhere. Stale `.v1` files coexist with current files with overlapping list names — the classic stale-copy smell.

### [IMPROVEMENT] Message-first Flip discipline is mostly good but leaks — worst in the files that matter most for readability.

258 files open `Expecto.Flip`, but `AppStateCustomTests.fs` (no Flip; `Expect.isNone ... "absent key should be None"` message-last), `CleanStdoutTests.fs`, `CellGridTests.fs` (~70 message-last sites at `:12-243`), and `DaemonHealthTests.fs` (Flip open but message-last half the time) violate the convention AGENTS.md makes binding.

### [IMPROVEMENT] The README's headline test claims do not survive contact with the code.

README claims "176 property-based tests including concurrent write safety" — the actual count is 527+ properties (the README *understates* the good news), but "concurrent write safety" maps to exactly one `testProperty` (`BinaryFormatTests.fs:1695`) that races writes to two **distinct** files — not concurrent writes to a shared session file. And the mutation-score gate in CI is effectively fabricated: `Program.fs:61-66` hardcodes `expectedMutations = 89` and infers "survived" from a single exit-code bit regardless of actual survivors; the CI comment says "No threshold gate yet... just report" and exits 0 unconditionally.

### [IMPROVEMENT] Parallel-safety is partial: fixed ports, a shared `globalActorResult`, and Harmony patches in the default suite.

`SharedSageFsFixture.fs:18` (`McpPort=54321`), `HotReloadToolTests.fs:76` (`port=40000`), `DevReloadTests.fs:417/423` (hardcoded `127.0.0.1:5000`/`8080`). `TestInfrastructure.fs:18-23` adds an `envLock` but only `withEnvVar` uses it — `InstrumentationTests.fs:297/304` mutates `OTEL_EXPORTER_OTLP_ENDPOINT` directly. `DevReloadCanaryTests` prefix-patches a real Harmony instance in default-suite tests while `DevReloadTests` also patches — cross-test interference by construction. And "session isolation" tests share one in-proc actor and assert only that context maps switch — they do not prove real session-file isolation (that lives in the in-memory `BinaryFormatTests` property instead).

### [IMPROVEMENT] `dotnet test` via the TestSdk would bypass Program.fs's filter entirely.

`YoloDev.Expecto.TestSdk` is referenced; CI runs `dotnet run --project SageFs.Tests -- --summary` (which honors Program.fs — good), but `dotnet test` on the fsproj runs the `[<Tests>]` assembly unfiltered, contradicting the fast-default design. The AGENTS.md rule "run tests via the SageFs REPL, not `dotnet test`" is not just a workflow preference — it is the only thing keeping the ptest filter alive.

### [STYLE] Giant files and naming sprawl.

`PureModulesComprehensiveTests.fs` (2,727 lines), `SageFsAppTests.fs` (2,637), `LiveTestingGraphTests.fs` (2,154) — and test names mix `WHY —` prefixes, bug-tracker IDs, `RoundNN`/`W35`-style synthesis names, and outcome-descriptions, making `--filter-test-case` navigation erratic.

---

## 10. Error messages — the deepest wound: a world-class error algebra that production never calls

**What world-class looks like (and what this product explicitly demands):** every failure tells the user/agent what failed, why, and exactly what to do next; MCP errors are structured and actionable; error identifiers are stable enough to grep; the log path is in the message.

SageFs *built* this. `SageFsError` is a single DU with `describe` (what), `suggestedAction` (what to do), `describeForAgent` (composed, "→ Next:"), `toJson` (case/fields/message/suggestedAction), `toHttpStatus`, `toLogLevel`, and four classifiers. Then every production boundary ignored it.

### [CRITICAL] The SageFsError algebra is exercised only by unit tests.

`toJson`, `toHttpStatus`, `toLogLevel`, `isClientError/isServerError/isGatewayError/isInfraError` are referenced only from `ErrorAlgebraHardeningTests.fs`, `ArchitectureTests.fs`, `SageFsErrorClassificationTests.fs`. Every production surface hand-rolls an ad hoc shape: `{| success=false; error = msg |}` (McpServer.fs:276/297/316/1258/1672/1744), `{ error }` (Dashboard.fs:753), `{ message }` (McpServer.fs:1914). The careful status/log/JSON projections and the "→ Next:" convention never reach a user or agent.

### [CRITICAL] The eval/exec HTTP surface cannot represent failure at all.

`McpServer.fs:1179-1206`:
```fsharp
do! jsonResponse ctx 200 {| success = true; result = result |}        // /exec — always 200
do! jsonResponse ctx 200 {| success = not (result.Contains("Error")); message = result |}  // /reset — substring sniffing
match result.StartsWith("Error") with ...                              // /cancel — prefix sniffing
```
A compile error, a dead worker, and a type-load failure are indistinguishable at the HTTP contract. A legitimate output containing the word "Error" flips `/reset` to failure. The clients that consume this are then forced into the same substring sniffing.

### [CRITICAL] MCP tool errors are flattened to plain strings; no tool emits structured `isError` content; `check_fsharp_code` returns bare lines with no location and no remediation.

`McpServer.fs:175-211` — only *thrown exceptions* reach `isError`, as one text block. `Mcp.fs:1572-1590` — `check_fsharp_code` returns `sprintf "[%s] %s" (DiagnosticSeverity.label d.Severity) d.Message` with no file/line and no fix. The proof that structure never crosses the boundary: `classifyFrictionOutcome` (`McpTools.fs:24-58`) re-infers failure *kind* from `Contains("session") && Contains("warm")`-style text matching on those flattened strings.

### [CRITICAL] Worker eval failures become `ex.ToString()` stack dumps wrapped in "SageFsInternal error occured," then re-classified by substring matching.

`AppState.fs:243` (`Error <| new Exception("SageFsInternal error occured", e)`), `WorkerMain.fs:150` (`Result.mapError (fun ex -> ex.ToString())`), then `Mcp.fs:1223-1232` runs `ErrorMessages.categorize` over the *entire* dump. The user sees a raw stack trace; the classifier, running over stack frames, mis-derives the tip (see the classification ordering hazard below). The `Unexpected` case's `describe` is `"Unexpected error: %s"` and its suggestedAction is `"Check the SageFs log for details"` — and the log path appears nowhere in the message.

### [CRITICAL] `/health` always sends `error = null` — the VS Code client built to consume structured health errors never receives one.

`McpServer.fs:1377-1380` — `{ error = (null :> obj) ... }` unconditionally. `Extension.fs:780-796` — on a Faulted/Stopped session the client branches on `status.error`; with `None` it renders a bare `$(error) SageFs: session error` status-bar icon — no message, no action. The client was written for the structured contract; the server never sends it.

### [IMPROVEMENT] FSI error classification is fragile substring matching with a real ordering hazard.

`ErrorMessages.fs:17-24`:
```fsharp
| _ when errorText.Contains("not defined") || errorText.Contains("not found") -> NameError
| _ when errorText.Contains("syntax") || errorText.Contains("unexpected") -> SyntaxError
| _ when errorText.Contains("type") -> TypeError
```
"not found" is checked before "type", so a missing-file message lands in NameError ("did you open the right namespace?") with the wrong remedy; then any residual message containing "type" — including type names in a stack trace — becomes TypeError. And the classifier runs over the *entire composed string* including `ex.ToString()` stack frames (`Mcp.fs:1127-1133`), so stack frames containing "type"/"syntax" skew the category and its appended advice.

### [IMPROVEMENT] `sagefs check` cannot fail its headline check.

`EnvCheck.fs:62-67` — the ".NET SDK" check reads `RuntimeInformation.FrameworkDescription`, which is the CLI's *own running runtime*: it always succeeds, and the `fail` branch is dead. There is no `dotnet --list-sdks`, no global.json/TFM-vs-SDK check, no "does the target build" check. The FSI check is real (`dotnet fsi --nologo --exec` with a 3s kill — and note it *passes* even when the process is killed on timeout, `EnvCheck.fs:82`). The port and daemon checks have genuinely good hints (`--mcp-port`, `sagefs stop`).

### [IMPROVEMENT] Swallow-all `with _ ->` erases "nothing there" vs "couldn't look."

`Mcp.fs:1490-1502,1873` — `getAvailableProjects` returns "(none found)" on exception; `readFsprojPackageRefs` returns `[]` on parse errors; `EnvCheck.fs:49-60` — `isPortFree`/`findFsproj` treat exceptions as false/empty. A permission error reads as success.

### [IMPROVEMENT] `sagefs stop` reports failure as success.

`Program.fs:229-235` — a stale PID prints "Daemon was not running (stale PID N)" to stdout and returns exit code 0; stopping with no daemon prints "No daemon running" and returns 0. An automation script cannot distinguish a successful stop from a no-op.

### [IMPROVEMENT] Error strings are prose, not stable tokens.

Logs are message-string-only; `SageFsError.toLogLevel` and case names never reach the logging path, so a failure is logged as `"Evaluation failed: ..."` with no stable case token to grep (`SageFsError.fs:78`). Friction strings are recorded as `sprintf "Error: %s" ex.Message` (`McpTools.fs:201`), losing type identity. And 19 of the 30 DU cases carry bare unstructured `reason: string` — `EvalFailed`/`ResetFailed`/`CheckFailed` are indistinguishable at the data level; automation must parse prose.

### [IMPROVEMENT] `targeted_verify`'s report swallows what it actually checked.

`Mcp.fs:2661-2723` calls `createReport request None None` — no snippet, no exact-test observation — so the response is a canned "Plan: ..." sentence (`Verification.fs:316-317`), and the "no evidence collected" blocker is swallowed by the `Perform ..., _` arm. The agent learns nothing about session trust, loaded-file staleness, or what to run next — the exact information the plumbing just computed.

---

## 11. What genuinely holds to world-class (unpadded)

- **The daemon↔worker typed port.** `WorkerProtocol.fs` (typed wire DU + `SessionProxy` function port + JSON codec), `HttpWorkerClient`, and `WorkerHttpTransport.fs:35` — the client and server literally share one route table. This is the hexagonal pattern done right.
- **The pure decision cores.** `SessionOperations.fs:6-8`, `Watchdog.fs:5-7`, `VariantSelector.fs:16-17`, `SessionLifecycle`, `RestartPolicy` — pure, deterministic, exhaustively matched, with the process-IO seam injected (`SessionManagerRuntime`, `SessionManager.fs:192-199`). The `getProcessById` injection discipline is exactly right.
- **The state-machine DUs.** `SessionWorkflow`, `WorkflowSwitchOutcome`, `SessionPhase`, `SessionStatus`, `StandbyState`, `ExitOutcome` — total and exhaustive; the compiler enforces the transitions that matter most.
- **Worker-process supervision.** Backoff restart (1s→2s→4s→max 30s, 5-crash report), parent-death watchdog, fail-closed liveness, standby pool — this is production-grade lifecycle engineering and clearly the codebase's hardest-won knowledge.
- **Property-testing core.** 527 FsCheck properties across 83 files; BinaryFormat corruption/isolation properties, transition and fold properties. When this codebase tests a pure module, it tests it properly.
- **Integration-test isolation.** `SAGEFS_DATA_DIR` temp overrides keep the real `~/.SageFs` untouched — the tests that spawn the real daemon do it correctly.
- **The error algebra as designed.** `SageFsError` with `describe`/`suggestedAction`/`describeForAgent`/`toJson` is the right shape. The tragedy is that production ignores it.
- **Optimistic teardown + auto-advance + picker fallback** (`Dashboard.fs:880-958`) and the responsive session-card grid (`dashboard.css:594-652`) — the UX doctrine executed well in the one path that was thought through.
- **VS Code error prompts with action buttons** (`Extension.fs:663-673,899,2320-2330`) — genuinely actionable editor error handling.
- **The CVE pin comment** (`Directory.Packages.props:50-55`) — the model for every other pin in the file.

---

## 12. VERDICT

**Needs rework.** Not because the foundation is wrong — the foundation (typed worker port, pure cores, supervised worker processes, real property tests, a designed error algebra) is unusually good for a solo project. Because the foundation is *buried*: the domain core carries the TUI and the MCP hub; three session-state models coexist; the error algebra every boundary should use is called by no boundary; the eval hot path is O(n²); and the dashboard's own header comment forbids the incremental updates its doctrine demands. The bones are world-class. The accretion is not. **The single highest-leverage act is not adding a feature — it is deleting:** remove the retained TUI from the Core closure, delete the dead `WorkerLivenessMonitor`/`Replay`/`.sagefs` wiring or wire it, unify the session lifecycle into one type, and route every production error through the algebra that already exists.

## 13. RISK ASSESSMENT

**HIGH.** The dominant risk is not a crash — it is the unauthenticated, origin-unchecked loopback HTTP surface (dashboard + MCP + worker proxy + worker) combined with the `@develop` CDN script. Any webpage can reach mutating endpoints that create sessions at arbitrary paths and evaluate arbitrary F# as the logged-in user; DNS rebinding defeats the same-origin assumption. The second-order risk is the check-then-act TOCTOU on session creation and the unsynchronized `daemon.sagefm` writers corrupting durable state under exactly the multi-client concurrency the product advertises. Security work must precede feature work.

## 14. PRIORITY FIX QUEUE (ordered by leverage)

1. **Gate every HTTP surface against the browser:** require a per-daemon token (or validated Origin/Host + `Sec-Fetch-Site`) on every mutating route of ports 37749 and 37750, and drop the `@develop` CDN script for a pinned, self-hosted Datastar build (`Dashboard.fs:215`, `McpServer.fs:2018`, `DaemonMode.fs:598`).
2. **Route every production error through `SageFsError`:** replace the ad hoc `{success=false; error=msg}` shapes and the `/exec`-always-200 contract with the existing `toJson`/`toHttpStatus` projections; make `/health` send the structured `error` its own client already consumes (`McpServer.fs:1179-1206,1377`, `Dashboard.fs:753`).
3. **Make MCP errors structured:** emit `case/message/suggestedAction` content blocks from every tool instead of flattened strings, and stop classifying by substring (`McpTools.fs:24-58`, `Mcp.fs:1123-1232`).
4. **Unify the session lifecycle into one type:** delete the parallel `SessionState` loop-variable and the `IsActive` bool; make `Faulted` a `SessionPhase` case with no `Session` field so `Unchecked.defaultof` tombstones become unrepresentable (`AppState.fs:146-151,1606-1620`, `SessionDisplay.fs:17-27`).
5. **Close the pid-blind restart race and supervise the supervisors:** compare `workerPid` in `WorkerReady`/`WorkerSpawnFailed`, move the cold build off the mailbox loop, wrap the eval actor's loop in `ResilientActor`, and make the SessionManager mailbox restart-on-death (`SessionManager.fs:801-803,889,995,1304`).
6. **Enforce one-session-per-directory at the owner:** dedupe inside the mailbox `CreateSession`, and serialize `daemon.sagefm` writes through a single owner (`Mcp.fs:1881`, `DaemonMode.fs:796`, `DaemonPersistence.fs:88`).
7. **Fix the per-eval O(n²):** make the binding-scope rebuild incremental — only the new eval's bindings enter the scope; stop re-parsing 10k retained results and stop the regex-per-binding × per-cell cross product (`FeatureHooks.fs:71-89`, `BindingExplorer.fs:82-88`).
8. **Slim the Core closure:** delete the retained TUI and the MCP hub from `SageFs.Core` so daemon and host link a domain library, not the whole application (`SageFs.Core.fsproj:136-141,166`).
9. **Put payload economy in the transport, not the renderer:** keep the single whole-`#main` fat morph, suppress timer pushes when the snapshot is unchanged, enable Brotli on the SSE stream, and if wire size still matters, delta-encode at the protocol level — never split the render into per-panel fragments (`Dashboard.fs:522,615`, `DaemonMode.fs:1596,2194`).
10. **Wire the affordance gate:** call `requireTool` in every tool handler so the advertised state-filtered MCP surface is real (`Mcp.fs:1091-1098`).
11. **Reclaim the test suite:** delete the orphaned `.v1`/never-compiled files, move blocking `RunSynchronously`/`.Result`/`Thread.Sleep` tests to `do!`, gate every slow suite behind the ptest mechanism with a structural (not name-string) filter, and centralize snapshot settings at the root (`Program.fs:140`, `SnapshotTests.fs:11-19`).
12. **Fix the lying tests first:** `DaemonHealthTests.fs:54` (name says unhealthy, asserts Healthy) and `McpLlmInteropTests.fs:468` (`Expect.isTrue true`) — a green lie is worse than no test.
13. **Make `sagefs check` real:** replace the always-passing runtime check with `dotnet --list-sdks` + global.json/TFM validation, and make `sagefs stop` return non-zero on no-op (`EnvCheck.fs:62-67`, `Program.fs:229-235`).
14. **Bound the binary readers uniformly:** every TOC entry and string read checks section size, not stream length; and make the codecs non-lossy or version-bump them (`SessionPersistence.fs:264-271,332`, `TestCachePersistence.fs:302-362`).
15. **Give the dashboard create/reset paths the same optimistic feedback as teardown**, and give every control a real state-reflecting affordance and consistent sizing (`Dashboard.fs:824-850,1206-1230`, `DashboardFragments.fs:1186-1195`).
16. **Pick the event-sourcing story and delete the other:** wire per-session persistence to production resume, or delete `Replay`/`.sagefs` and label the filmstrip honestly as telemetry (`DaemonMode.fs:1478,299`).
