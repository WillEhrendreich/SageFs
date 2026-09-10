# The SageFs Roast — Round 2

**Taste rating:** Good instincts, incomplete follow-through — the round-1 fixes are real, but several were applied at the wrong layer (suppression after render, incremental cache bypassed by its own consumers, gating that makes suites *never run*), and the round-1 audit's own logic was not applied to the sibling surfaces (worker HTTP server, eval-result render path, McpPort, the second SessionMap, the second SessionDisplayStatus).
**Single most important observation:** Round 1 deleted the *named* defects and left the *structural* ones standing — the domain core still ships the TUI render stack to the FSI host, ~17 MCP feature tools are unreachable because they were never registered, the eval actor is still the one unwrapped actor, and the F#-executing worker HTTP server is the least-protected HTTP surface in the product.

The round-1 priority queue (all 16 items) is closed and the tree is green (6,759 tests / 0 failed at f22f069; the tree has since advanced to merge `0cfec9a`, 59 commits pushed). This round re-audits what the cleanup left behind — and what the cleanup itself broke. Every claim carries `path:line` evidence gathered fresh against the current tree.

Two handoff documents are merged into this round and prioritized below: `release-blockers.md` (why 0.6.412 cannot ship — the Definition-of-Done gate) and `standby-session-rework.md` (dissolve the standby pool into spawn-first restart). They are referenced by name rather than duplicated in full; both remain untracked working docs.

---

## 1. Security — the round-1 gate is real, but the actual code-executing process is wide open

**What world-class looks like:** every HTTP surface that can execute code or mutate state validates Origin/Host/token; no user- or agent-controlled text ever reaches the DOM unescaped; the threat model treats the browser as an untrusted peer on every port the daemon family opens.

### [CRITICAL] The worker HTTP server — the process that actually evaluates arbitrary F# — has no origin guard, no token, no Host check, and ships a deliberately `*`-CORS endpoint
- `SageFs.Host/WorkerHttpTransport.fs:67` binds `http://127.0.0.1:%d`; `:125` `/eval`, `:309` `/shutdown`, `:185` `/run-tests`, `:162` `/load-script`, `:170/:177` `/reset`,`/hard-reset`, `:329-386` hot-reload mutators. The pipeline is `UseResponseCompression()` then `Map*` (`:96`) — none of these reference `HttpOriginGuard` (which exists only at `McpServer.fs:2163` and `DaemonMode.fs:1075`).
- `WorkerHttpTransport.fs:400-405` `/__sagefs__/reload` is an SSE endpoint with `Access-Control-Allow-Origin: *`, documented as "Cross-origin (user's app port → worker port)" — the design *intends* arbitrary browser origins to reach this server.
- Port is OS-ephemeral (`Args.fs:154` `sprintf "%s 0"`), printed to stdout; a local webpage can learn it (the dashboard teaches the browser the worker base URL), and DNS-rebinding or a local process makes it reachable.
- why it matters: The round-1 fix hardened the daemon's two servers and then stopped. The worker is the one process whose `/eval` executes arbitrary F# as the logged-in user — a malicious local webpage can issue a "simple" cross-origin POST (`text/plain`/form, no preflight) with a JSON body (parsed at `:127`) to `/eval`, `/load-script`, or `/shutdown`. Even where CORS blocks reading the response, the code execution and session teardown happen. The guard's own rationale (`HttpOriginGuard.fs:5-8` — "DNS rebinding … POSTing to http://localhost:<port> … evaluates arbitrary F#") applies verbatim to the one server that actually does that.

### [CRITICAL] Dashboard eval-result XSS — F# output reaches the DOM unescaped through `Text.raw`
- `SageFs/Dashboard.fs:707-712` renders the eval **success/error** result with `Text.raw displayResult` (no `WebUtility.HtmlEncode`), pushed via `ssePatchNode` into `#eval-result` → Datastar morphs it into the DOM. Same at `:859-865`/`:873-879` (reset), `:1037-1043` (session action msg), `:1259-1264` (create result); `DashboardFragments.fs:2032-2036` `evalResultError msg = Text.raw msg`.
- The correct pattern exists all around it: `DashboardFragments.fs:464-466` `renderOutputForSession` wraps `line.Text` in `HtmlEncode`, as do `:407,418,432,439,493,1067,1542,1725`. Only the EvalResult/error fragment paths bypass it.
- Secondary channel: binding `TypeSig`/`Value` rendered raw — `DashboardFragments.fs:1836` `Text.raw b.TypeSig`, `:1851` `Text.raw (sprintf "= %s" v)`, `:721`/`:727` in the explorer.
- why it matters: FSI output is fully agent-controlled (the agent is instructed to run arbitrary F#). `printfn "<img src=x onerror=…>"` — or an exception message, or `%A` of a hostile string — becomes stored XSS in the dashboard with the same origin as `/dashboard/*` mutating endpoints, giving any future eval a pivot to the daemon's full surface. The escaping convention exists; the eval-result sink is the hole in it.

### [IMPROVEMENT] `/dashboard/discover-projects` + session-create accept arbitrary directories with no containment
- `Dashboard.fs:1207-1223` (`createDiscoverHandler`): if `Directory.Exists dir` → `EnumerateFiles(dir, "*.fsproj", AllDirectories)` — any readable path. `:1238-1267` create-session uses the same arbitrary dir + `resolveSessionProjects` (`DashboardTypes.fs:845-878`, rooted manual paths accepted). `get_available_projects` (`Mcp.fs:1548-1583`) same shape.
- Contrast: `/dashboard/eval-file` and `/load-script` DO canonicalize + confine (`Dashboard.fs:743-766`; `McpServer.fs:1342-1363`) — this surface was left out of that hardening.
- why it matters: the origin guard only rejects non-loopback *browser* requests; curl/CLI/local processes pass. Directory-structure disclosure plus an FSI session rooted anywhere — the sibling endpoints prove the team knows the containment pattern.

### [IMPROVEMENT — LOW] Both daemon servers bind `localhost`, not `127.0.0.1`
- `DaemonMode.fs:1065-1069`, `McpServer.fs:2132-2136` default `bindHost = "localhost"` (worker binds 127.0.0.1). Small residual surface past the guard.

### Verified clean (round-1 fixes hold)
- Origin guard wraps every daemon mutating route on both ports (`DaemonMode.fs:1074-1076`, `McpServer.fs:2162-2163`); no route escapes it.
- Hot-reload proxy is now a fixed 7-path allow-list with `{sid}` validation + 1 MB body cap (`DaemonMode.fs:561-571`) — no arbitrary passthrough.
- Friction read-side is owner-gated at the receiver (`friction-receiver/src/index.ts:305-351`); the daemon never reads remote reports (dashboard renders local SQLite only) — the privacy model holds.
- Output *lines* and most fragments are escaped.

---

## 2. Architecture — the split moved the hub and left the kernel

**What world-class looks like:** the FSI host links a domain library; the daemon owns application + adapters; nothing the host cannot run ships in its closure; every feature has a reachable consumer or is deleted.

### [CRITICAL] Core is still the daemon application + deprecated-TUI kernel; the FSI host closure still ships the render stack
- `SageFs.Core/SageFs.Core.fsproj:120-139` still compiles `RenderPipeline.fs`, `CellGrid.fs`, `Theme.fs`, `Draw.fs`, `AnsiEmitter.fs`, `Editor.fs`, `ElmLoop.fs`, `SessionDisplay.fs`, `SageFsApp.fs`, `ElmDaemon.fs`, `TerminalUI.fs`, `SyntaxHighlight.fs` into Core.
- The daemon's entire state machine runs on Core's Elm model: `SageFsApp.fs:220` (`SageFsModel`), `:414` (`SageFsUpdate`), `:1775` (`SageFsRender`), `:1957` (`SageFsEffectHandler`); `DaemonMode.fs:1304-1331` runs `ElmDaemon.startHeadless`.
- The HOST references all of Core (`SageFs.Host/SageFs.Host.fsproj:23`) and its manifest ships `SageFs.Core.dll` into the worker dir — so the FSI process that only runs `WorkerMain.fs` loads CellGrid/AnsiEmitter/Draw/TerminalUI and the 2,623-line `SageFsApp.fs` it can never run.
- Only `Mcp.fs` moved out. The roast's fix #8 ("delete the retained TUI from the closure") is otherwise unaddressed; the seam tests added in round 1 (`ArchitectureTests.fs` "Closure seams") assert Host↛daemon and Core∌McpTools — they do not assert Core∌render-stack, so the accretion is still invisible to the gate.

### [CRITICAL] ~17 MCP feature tools are never registered — the write-only feature stack is unreachable, not merely under-consumed
- `SageFs/McpTools.fs:1132-1467`: `decompose_pipeline` (1141), `diagnose` (1164), `coverage_intel` (1181), `impact_forecast` (1201), `suggest_next_action` (1222), `plan_ripple` (1235), `preview_what_if` (1251), `suggest_next_cell` (1269), `get_session_filmstrip` (1281), `export_notebook` (1299), `get_message_journal` (1331), `get_eval_timeline` (1350), `manage_scratch_pad` (1370), `get_eval_diff` (1385), `get_cell_dependencies` (1425), `discover_features` (1439), `suggest_repair` (1462) carry `[<Description>]` only. The `[<McpServerTool>]` attribute appears at 232…1105, then not again until 1393 — the whole block is unregistered.
- Registration is `.WithTools<SageFsTools>()` (`McpServer.fs:1179`), which reflects only `[<McpServerTool>]` members.
- why it matters: the round-1 roast called Ghostwriter/EvalLens/WhatIf/ImpactForecast/ActionPrioritizer/Diagnostician/CellDependenciesReport "write-only with a heavy test tax." The truth is stronger: their entire call graph hangs off MCP members agents can never invoke. The `[<Description>]` text advertises tools that do not exist on the wire. Either register them (if they earn their keep) or delete the ~1,000 lines + their test tax. EvalTimeline and Diagnostician (via `explain_test_failure`) are the only two with live consumers.

### [IMPROVEMENT] God files grew on the daemon side — 10 files over 1,000 lines across two projects
- `SageFs/Mcp.fs` 3,668; `McpServer.fs` 2,217; `DaemonMode.fs` 2,212; `DashboardFragments.fs` 2,039; `Dashboard.fs` 1,742; `McpTools.fs` 1,468; `SageFs.Core/Features/LiveTestingTypes.fs` 4,960; `SageFsApp.fs` 2,623; `AppState.fs` 1,820; `SessionManager.fs` 1,540. The project split moved the hub but did not decompose it.

### [IMPROVEMENT] Duplications survive verbatim — DaemonState.fs and SessionDisplayStatus
- `SageFs.Core/DaemonState.fs:18` (`module DaemonState`, HTTP probe) vs `SageFs/DaemonState.fs:54-59` (re-export shim, comment "so existing code using SageFs.Server.DaemonState compiles") — architecture held together by compatibility.
- Two `SessionDisplayStatus` types: Core 6-case (`SessionDisplay.fs:8-14`) consumed by `DaemonMode.fs:1788-1793`; daemon 5-case (`DashboardTypes.fs:385-390`) consumed throughout Dashboard. Two display vocabularies, mapped back and forth, in two assemblies.

### [IMPROVEMENT] The Core TUI render stack's only production consumer is SageFs.Gui — which is not in the solution
- `SageFs.Gui/RaylibMode.fs` uses `Screen.drawWith` (`:192`), `CellGrid` (`:285,382-383`), `JumpToTest` (`:468-469`), `Theme` (`RaylibPalette.fs:10-14`); but `SageFs.slnx` does not include SageFs.Gui. Screen/CellGrid/AnsiEmitter/Draw/PipelineFlame/TerminalUI/DaemonClient have no production consumer except Gui + tests.
- `TerminalUIState.IsActive` (`TerminalUI.fs:284-286`) is never set anywhere — the daemon gate `not TerminalUIState.IsActive` (`DaemonMode.fs:1345`) is a dead constant-true condition.

### [STYLE] MCP-named modules still in Core; duplicate InternalsVisibleTo
- `SageFs.Core/McpPushNotifications.fs`, `McpStateHandlers.fs`, `McpToolAudit.fs` — the boundary drawn in round 1 stops at exactly these names. `SageFs.Core.fsproj:11-13` + `:15-17` duplicate the same IVT ItemGroup.

---

## 3. Actors & process lifecycle — one unwrapped actor, one unwired monitor, one unguarded message

**What world-class looks like:** every owned resource has a single supervised owner; every late/stale event carries a generation that is checked; no actor owns a live FSI session without a restart guard.

### [HIGH] The eval actor — the mailbox that owns the only live `FsiEvaluationSession` — is still the one actor NOT wrapped in `ResilientActor`
- `SageFs.Core/AppState.fs:1073` `MailboxProcessor.Start` with a bare `let rec loop phase middleware evalStats` (1077-1581); no wrapLoop, no loop-level catch. Query (`:1029`) and router (`:1778`) are wrapped.
- Unwrapped throwable handlers: `EvalEnableStdout` → `st.OutStream.Enable()` (`:1089`); `EvalRun` gate-fail → `emit` + `reply.Reply` (`:1101-1102`); `EvalFinished` → `publishSnapshot` → `queryActor.Post` + `Volatile.Write` (`:1136`), `reply.Reply` (`:1190`); `EvalAddMiddleware` (`:1217-1219`).
- why it matters: one exception escaping any of these terminates the mailbox — every future `Eval`/`Reset`/`HardResetSession` queues forever, `PostAndAsyncReply` hangs the worker, and (because this actor publishes the phase snapshots) no `Faulted` is ever emitted. The per-branch try/with (reset/hard-reset bodies, eval thread, live-value capture) gives false confidence; the round-1 pattern is defined three lines above the eval actor and applied to a low-risk forwarder instead of the highest-risk stateful actor.

### [HIGH] `WorkerReady` ignores its pid — the one remaining unguarded member of the stale-event family
- `SessionManager.fs:1026` `WorkerReady(id, _workerPid, baseUrl, proxy)` binds `_workerPid` and never compares it to `session.Info.WorkerPid`. `WorkerExited` (`:1170`) and `WorkerSpawnFailed` (`:1141-1144`) both pid-guard; the cold-restart path clears `WorkerPid` so the old worker's exit is ignored (`:920`).
- why it matters: the off-mailbox `awaitWorkerPort` task's late `WorkerReady(oldPid,…)` can sit in the mailbox behind `RestartSession` and then install the OLD proxy/baseUrl over the fresh process (`:1046-1050`), pointing every subsequent eval at a dead port; the warmup poll started from that stale Ready (`:1065-1096`) then pokes the old proxy and can fault the fresh session. The pid is on the message precisely so this race is closed — it is closed for two of the three handlers.

### [MEDIUM] WorkerLivenessMonitor remains production-dead (module + tests only)
- `SageFs.Core/WorkerLivenessMonitor.fs:41`; zero production consumers (grep). Worker death is detected only via `proc.Exited`/`WorkerExited` (`SessionManager.fs:694,765,1249`) — a hung-but-alive worker is never reaped. The module encodes the exact verdict policy (healthy→unhealthy→timeout, cooldowns) that would close this, and nothing calls it.

### [MEDIUM] SessionMap (agent→session) grows forever and stale agents claim occupancy
- `SageFs/Mcp.fs:664` `SessionMap: ConcurrentDictionary<string,string>`; the only write is `setActiveSessionId` (`:691-692` unconditional), and there is NO `TryRemove` anywhere. `stopSession` (`:2017-2024`) never prunes; `DaemonMode.fs:1604-1621` cleanup evicts only `AgentActivityTracker`. Empty-string writes (`:997,1001,1004,1007`) leave the key present.
- why it matters: `SessionOccupancy.forSession` (`SessionOperations.fs:92-96`) reverse-looks-up this map to feed `SessionGuidance` ("OccupiedBy: X") and `listSessions` occupancy — a dead agent's key makes the session look permanently occupied to every other agent.

### [MEDIUM] McpPort "preservation across reset" is inert — no sender exists and the mutation targets the wrong actor
- `AppState.fs:1245-1253`/`:1324-1331` carefully read the live `StartupConfig` "so a reset preserves McpPort" — but `StartupConfig.McpPort` is initialized to 0 (`:1668`), `UpdateMcpPort` (`:198,227`) routes only to the query actor (`:1727-1728` → `:1019-1027`) where it mutates the query snapshot, and **no code anywhere posts `UpdateMcpPort`**. The eval actor (the state resets rebuild from) never sees it.
- why it matters: worker status always reports MCP Port 0 after both Active and Faulted resets — consistent only because the value is never set. The comments describe a mechanism disconnected from the state it claims to preserve; if someone wires a real sender later, reset-from-Active silently drops it.

### [LOW] Dead-defensive `isNull` guards remain in the reset path
- `AppState.fs:1238` and `:1359`: `match phase with Active (st, _) when not (isNull (box st.Session))` — inside `Active`, `Session` is live by construction, so the guard is the residue of the tombstone mindset (untestable dead branch that softens the phase invariant).

---

## 4. Performance — the round-1 fixes were applied at the wrong layer

**What world-class looks like:** the no-change guard is a cheap version check *before* any work; the incremental cache is what the consumers read; nothing on the eval thread does reflection over unbounded state.

### [CRITICAL] The round-1 O(n²) fix is bypassed by its own consumers — every feature push still rebuilds the full cell graph + binding scope from ≤10k history cells
- `SageFs/McpServer.fs:935-962` `handleFeaturePush` (fires on EVERY `ModelChanged` with output movement, `:1025-1037`) → `computeCellDepsPush` (`FeatureHooks.fs:129-140`) maps `CellDependencyGraph.analyzeCell` over the entire `state.EvalHistory` (cap 10,000, `:44`); then `McpServer.fs:955` `Volatile.Write(&sharedBindingScope, buildScopeFromState state)` — a FULL `buildScopeSnapshot` re-parse + word-boundary cross-reference over every retained cell (`FeatureHooks.fs:142-148`/`BindingExplorer.fs:51-96`).
- why it matters: round 1 made `recordEval`'s cache update incremental (`appendCell`, `BindingExplorer.fs:130`) — then the per-push read side throws the cache away and redoes the whole thing, twice per push, per subscribed MCP connection. The dominant algorithmic sin survives in the daemon push path.

### [CRITICAL] Dashboard pushState suppresses *after* building the full snapshot — every tick pays HTTP + SQLite + disk + double render even when nothing is sent
- `Dashboard.fs:505-552`: the no-change check (`mainHtml = lastPushedMain`, `:546-548`) runs after `buildDashboardSnapshot` (`:518`) and after `renderNode (renderMainContent snap)` (`:546`). Per snapshot: 2-3 worker HTTP round-trips (`GetEvalStats`/`GetHotReloadState`/`GetWarmupContext`), a synchronous SQLite friction read (`:429-445` — `RunSynchronously` full-table queries, the round-1 item that was "re-checked" and left), a disk manifest read (`:253` → `DaemonPersistence.loadManifest`), and a double full-page render when changed.
- The 1s fallback poll (`:646-652`) and the SSE agent coalesce only ~100 ms (`:573-621`), so a busy run costs ~10 full snapshot builds/sec. The comment at `:499-502` claims "state reads are cheap" — they are network I/O.
- why it matters: the round-1 fix ("suppress no-change pushes") suppresses the *send*, not the *cost*. The version guard belongs before `buildDashboardSnapshot`.

### [IMPROVEMENT] Live-value reflection walk + JSON serialize still on the eval thread, per eval, unbounded in binding count
- `AppState.fs:1140-1165` (EvalFinished): `Session.GetBoundValues()` → `LiveValueTree.buildSnapshot` walks EVERY bound value via reflection (`LiveValueTree.fs:196-262`, per-node depth 6/children 50 but no cap on binding count) → `WorkerProtocol.Serialization.serialize` the whole tree into `Metadata`. Daemon re-deserializes per eval (`DaemonMode.fs:1920-1927`).
- why it matters: eval latency as observed by callers includes the full walk + stringify on the actor thread, before `reply.Reply`. Property getters are arbitrary user code.

### [IMPROVEMENT] Streaming test proxy allocates a fresh 30s `Task.Delay` + `WhenAny` per line, never cancelled
- `SageFs.Core/HttpWorkerClient.fs:110-121` and `:164-175`: per-line `ReadLineAsync` raced against a new `Task.Delay(workerHttpRead)` (30s); abandoned timers accumulate for the run's duration.
- When the read times out, the loop silently stops (`keepReading <- false`) — no error, no `NotRun` for unfinished tests — and the caller (`SageFsApp.fs:2517-2550`) dispatches `TestRunCompleted` anyway, leaving tests permanently `Running` in the UI.

### [IMPROVEMENT] Session stop leaks per-session state
- `SageFsApp.fs:1048-1085` (`SessionStopped`) never calls `RecentOutput.Clear(sessionId)` (`SageFsEvent.fs:210-215`); `LiveBindingsAdaptive.remove` (`LiveBindingsAdaptive.fs:70-72`) has zero callers; `FeaturePushState` is daemon-global, never session-scoped or cleared. Each create/stop cycle retains the output ring + adaptive snapshot tree for daemon lifetime.

### [IMPROVEMENT] Remaining per-push full-list scans
- Filmstrip: `DaemonMode.fs:1846-1864` `List.rev` over ≤10k history per push, re-reversed in `DashboardFragments.fs:1009-1012`. Timeline: `EvalTimeline.timelineStats` does three full `List.sort`s of ≤1,000 entries per push (`EvalTimeline.fs:44-56`).
- Warmup poll: per-session HTTP GET /status every 1s for up to 120s (`SessionManager.fs:1065-1096`) — N warming sessions = N GETs/sec at the moment workers are busiest.

### Verified: Brotli is deliberately excluded for `text/event-stream` (`DaemonMode.fs:1053-1060`, `McpServer.fs:1140-1147`) — SSE frames ship uncompressed by design; acceptable once the no-change guard precedes the build, otherwise the fat morphs stay fat.

---

## 5. Test suite — the gating mechanism silently un-wrote the integration suites

**What world-class looks like:** slow suites are gated behind an explicit opt-in that actually runs them somewhere; CI exercises the full suite; the default run is fast AND the integration run is real.

### [CRITICAL] `ptest` = permanently pending: every `ptestList "[Integration]"` suite is dead, not merely gated
- Expecto semantics: `ptest` builds a test that is **ignored** (FocusState.Pending) — it never runs, even under `--all`.
- Suite after suite double-gates with ptest AND `[Integration]`: `DaemonIntegrationTests.fs:163,221,318`, `McpServerIntegrationTests.fs:19`, `FalcoTests.fs:107`, `ActorSplitTests.fs:16`, `EvalCancellationTests.fs:16`, `SessionIsolationTests.fs:76`, `SessionResetTests.fs:101-134`, `ExplorationTests.fs:9`, `HotReloadBrowserTests.fs:146`, `McpLlmInteropTests.fs:28,74,138,162`, `VscodeExtensionTests.fs:253,267`, `DemoRecording.fs:1105-1107`, `StandbyPoolTests.fs:348`.
- why it matters: the round-1 "flip the really slow stuff back to ptestList" directive was executed as *permanently ignored*. The daemon-lifecycle, real-server, real-Harmony, session-reset regression suites cannot run through ANY path. Their coverage is fictional — and CI is green, which is exactly what a broken hot-reload journey or daemon CLI would look like.

### [CRITICAL] CI never runs `[Integration]` or `[Benchmark]` tests at all
- `.github/workflows/main.yml:44-49` runs only `dotnet run -- --summary` (the default filter, `Program.fs:140-151`); no job passes `--all`/`--integration`/`--compliance`.
- Non-ptest `[Integration]` tests that would run under `--all` but never in CI: `WebAppHotReloadVerificationTests.fs:224,291`, `DaemonStateChangeContractTests.fs:128,159`, `HttpApiIntegrationTests.fs:780,946,1091`, `VscodeExtensionTests.fs:255,269`, `HarmonyCanaryTests.fs:80-144`, `DashboardBrowserTests.fs:107,122`.
- Expecto `[Benchmark]` timing-threshold lists (`StandbyPoolTests.fs:259`, `LiveTestingCycleTests.fs:1425`) never run anywhere (CI's `--benchmark` runs BenchmarkDotNet only).

### [CRITICAL] Real-process and real-socket suites sit in the DEFAULT run with no tag
- `ShutdownLifecycleTests.fs:15-30,66-106` (spawns + kills `cmd /c ping` children), `ParentMonitorTests.fs:28-70` (spawns the test process, sleeps poll intervals), `DaemonStateTests.fs:60-111` (real HttpListener), `WorkerHttpTransportTests.fs:137-231` (real Kestrel + HTTP round-trips), `EnvCheckTests.fs` (real TcpListeners), `HarmonyCanaryTests.fs:80-144` + `MethodPatcherTests.fs:44-60` (real Harmony patches — global side effects).
- why it matters: these mutate process state and bind ports in the default parallel run; the filter's name-Contains mechanism cannot be trusted (a list named "HarmonyCanary integration" without the bracketed `[Integration]` tag slips through).

### [IMPROVEMENT] YoloDev `dotnet test` bypasses the Program.fs filter entirely
- `SageFs.Tests.fsproj:338-339` references `YoloDev.Expecto.TestSdk`; `dotnet test` uses adapter discovery, not `Program.main` — a developer running `dotnet test` gets the full unfiltered assembly with no sequencing context. Two different suites exist depending on invocation.

### [IMPROVEMENT] ~105 blocking sites remain in the default run
- `JupyterKernelTests.fs` 25 (`Async.RunSynchronously` in pure-async handlers), `SageFsAppTests.fs` 15, `SessionManagerMailboxSupervisionTests.fs` 16, `LiveTestingExecutorTests.fs` 12, `Round14HardeningTests.fs` 12 (incl. `Thread.Sleep(50)` + `ManualResetEventSlim.Wait`), `InstrumentationTests.fs` 11 (`.Result` on `Task.FromResult`). Justified exceptions: `TestInfrastructure.fs:97-100` single bridge, daemon-startup waits inside `[Integration]`.

### [IMPROVEMENT] `--mutation-score` is theater
- `Program.fs:61` `expectedMutations = 89`; score inferred from exit code (exit 0 **or 2** → 89), never from actual caught/survived counts; `Program.fs:80-84` exits 1 only if score < threshold (default `0.0` — never fails); `main.yml` mutation step `exit 0` always. A mutation-score collapse can never fail CI.

### Style: message-order discipline drifted (piped `|> Expect.isTrue "msg"` inside Flip files + non-Flip message-last files); no tautologies remain; property surface thin (~43 files, capped at 200 samples globally).

---

## 6. Error handling — the algebra is used at the new boundaries and abandoned at the legacy ones

**What world-class looks like:** every boundary emits the typed error with case + suggestedAction; no boundary re-derives failure kind from display strings.

### [MEDIUM] The legacy HTTP shim still string-sniffs `result.StartsWith("Error")`
- `McpServer.fs:1289-1290` (`/reset`), `1303-1304` (`/hard-reset`), `1310-1312` (`/cancel`), `1318-1320` (`/api/cancel-eval`), `1366-1368` (`/load-script`): `{| success = not failed; message = result |}` / `{| received; error |}` over the flattened string — no `SageFsError`, no `errorDetails`. Leaf `{| success = false; error = "…" |}` bodies remain at `:309,342,407,1363,1786,1800,1808,1888,1936-1975`. Newer `/api/*` routes use `structuredErrorBody` correctly.

### [MEDIUM] MCP tool errors are still plain strings; friction classification still substring-sniffs
- `McpTools.fs:24-58` `classifyFrictionOutcome` sniffs `Contains("session") && Contains("warm")` (`:31`), `Contains("TypeLoadException")`, `Contains("output") && Contains("large")` over flattened display strings; tools return `Task<string>` wrapped in a single `TextContentBlock` (`McpServer.fs:209-212,242-246`) — no structured `case`/`message`/`suggestedAction` content block on the wire; suggestedAction survives only as prose in `describeForAgent` (`Mcp.fs:1097-1150`).
- why it matters: telemetry classification depends on substring luck (a success message containing "session"+"warm" misrecords), and an agent that wants to branch on error case must regex prose.

### [LOW] `/health` still sends `error = null` for Faulted/Stopped sessions without a recorded faultReason
- `McpServer.fs:1487-1500` + `DaemonHealth.fs:182-196`: structured error iff Faulted/Stopped **and** `faultReason` non-empty. Worker-death tombstones set FaultReason; dashboard `StopSession` removes the session; some fault paths preserve None. The VS Code client's structured-error branch still gets null sometimes.

### [LOW] ErrorMessages ordering is correct and regression-locked; residual risk is the untested "first line is the error" contract
- `ErrorMessages.fs:23-35` classifies only the first line, specifics-before-generic, regression-tested (`FsiRegressionTests.fs:163`). Callers classify `SageFsError.describeForAgent` text (which carries a `→ Next:` suffix) at `Mcp.fs:1182,1281-1285` — a first line that isn't the error silently reclassifies.

### Verified fixed: Manifest reader now cross-checks declared total size vs actual length, session count vs parsed count, per-entry budgets (`ManifestPersistence.fs:174-176,240-258,273-279`) — `_totalSize` is no longer read-and-ignored.

---

## 7. Persistence & cache correctness

### [MEDIUM] TestCachePersistence is wire-lossless but domain-lossy — failure kind is erased across restarts
- `SageFs.Core/Features/TestCachePersistence.fs:336-358`: `Failed(AssertionFailed)`, `Failed(ExceptionThrown)`, and `Failed(TimedOut)` ALL encode to enum `Fail` (byte 1); decode (`:370-407`) always rehydrates as `AssertionFailed` — ExceptionThrown/TimedOut discrimination unrecoverable. `Outcome.Error` (byte 3) decodes to `NotRun` ("legacy code 3; never written by current writers").
- why it matters: failure *kind* — which drives narratives and flaky classification reading `LastResults` — is destroyed on disk. A distinct state is silently lost, the exact round-1 complaint re-verified against the "fixed" codec.

### [LOW/MEDIUM] Warmup replay-cache fingerprint excludes dependency/project content — a version change can serve a stale warmup plan
- `WarmupReplayCache.fs:22-29,98-119`: fingerprint stamps `Path/Length/LastWriteTimeTicks` of startup/source/assembly files only. A package-version change that keeps path + mtime (project-to-project refs, SDK/channel bumps) leaves the fingerprint equal and replays the old namespace/module open plan against a different dependency set.

---

## 8. Live-testing & hot-reload

### [MEDIUM] Live test result application bypasses the generation guard that was built for it
- `SageFsApp.fs:1274-1275` `TestResultsBatch` → `applyBufferedTestResults` (`:751-801`) → merge (`LiveTestingTypes.fs:2471-2510`) — no `RunGeneration` comparison, no session filter. The guard exists (`LiveTestingTypes.fs:889-913` `StaleWrongGeneration`) but is never invoked on this path. The daemon run-dispatch captures sid/proxy at start (`SageFsApp.fs:2478-2493`) with no re-validation.
- why it matters: a batch in flight when the session was stopped/hard-reset (the `with ex` path at `SageFsApp.fs:2562-2580` synthesizes "transport closed" failures) marks fresh tests failed and corrupts flaky history.

### [LOW] Hot-reload module-strip gap: no TODO, no unskipped E2E test
- The only test that starts a real app, re-evals, and asserts the HTTP response changed is `HotReloadBrowserTests.fs:148-341` — a `ptestCase` (permanently skipped). The re-eval'd-method-suffix-match contract (`HotReloading.fs:450-511`) is defended only by reflection-name heuristics with no end-to-end pin.

---

## 9. Release status — 0.6.412 is blocked from shipping by the Definition-of-Done gate

**Source:** `release-blockers.md` (separate agent handoff, 2026-09-03, untracked). Merged in full context; this section is the roast's working summary.

### [BLOCKER — release-critical] The publish gate fails while any of 12 real-client E2E rows are `deferred`; all 12 are deferred
- `.github/workflows/publish.yml` step "Enforce release Definition of Done" (added by commit `4413211`) fail-closes: any row with `status == 'deferred'` → `Write-Error "Release blocked by <id> (issue #<n>)"` → exit 1. Publish run `33778022860` failed exactly this way. **No NuGet push, no GitHub Release, no version tag exists; the global tool update (`dotnet tool update sagefs --global`) is pending.**
- The matrix lives at `quality/definition-of-done.json`: 12 rows = 3 capabilities (hot-reload #127, live-testing #128, friction #129) × 4 clients (dashboard, vscode, visualstudio, neovim). All 12 are `deferred`, all expire 2026-10-01.
- ⚠️ **Gotcha:** `SageFs.Tests/DefinitionOfDoneTests.fs` second test asserts the current all-deferred matrix *produces* "blocks release readiness" errors — the moment every row is `verified`, that test **fails** and the default suite goes red. Closing the matrix requires updating that test in the same commit.
- why it matters: this is the highest-leverage finding in the entire roast — nothing ships until 12 journeys are real, user-visible, same-SHA, and CI-executed. The evidence doctrine forbids green builds/handler existence/parser fixtures/endpoint-name checks as proof; today **none** of the 12 rows has compliant evidence, and several rows require **new product code** (no friction command exists in VS Code, VS, or Neovim; VS has no UI automation; nvim E2E is CI-disabled).

### [BLOCKER] Per-client evidence gaps — none of the 12 rows is close to verified
- **HR-DASH-E2E**: save→changed-app proven at worker/HTTP level only (`WebAppHotReloadVerificationTests`, two `[Integration]` tests — never run in CI); **no browser journey**; reconnect-restores-authoritative-state has **no coverage anywhere**; `HotReloadBrowserTests` is a permanently-skipped `ptestList` video demo injecting static HTML.
- **HR-VSC-E2E**: no extension-host journey exists; `VscodeExtensionTests` (named evidence) only asserts activation/status text, never saves a file, and has **no VSIX install step** — a broken `dist/Extension.js` coexists with green tests.
- **HR-VS-E2E**: no Experimental Instance / UI automation; only pure unit tests of client routing.
- **HR-NVIM-E2E**: `e2e_hotreload_spec.lua` only proves "daemon survived," not changed behavior; E2E is CI-disabled in the nvim repo.
- **LT-DASH-E2E / LT-VSC-E2E / LT-VS-E2E / LT-NVIM-E2E**: no browser/extension-host/tool-window journey anywhere; nvim `handle_tests_discovered`/`handle_results_batch` **merge unconditionally** (no older-generation rejection, no complete-discovery replacement sweep — must mirror the VS Code/VS fold semantics).
- **FR-DASH-E2E**: panel UX exists but **no test of any kind** exercises the send handler or the panel journey (grep for `friction/send`/`createFrictionSendHandler` in `SageFs.Tests` returns zero).
- **FR-VSC-E2E / FR-VS-E2E / FR-NVIM-E2E**: **no friction/report command exists** in any of the three extensions/plugins — must be *built*, then journeyed.
- Also required per the plan: cross-client recovery (reconnect ≤2s, daemon-restart snapshot ≤5s, worker-death marks stale, duplicate events idempotent, older generations rejected, two clients never cross-contaminate), accessibility (accessible names, keyboard-completable, textual/live-announced states, contrast, no overflow at 375×812/200% zoom), and performance budgets (file-write→reload ≤750ms p95, etc.).

### [IMPROVEMENT] Stale/misleading tests that must be fixed or deleted while closing rows (hygiene)
- `HotReloadBrowserTests.fs` — entire list is `ptestList` (can never run), a video choreography over `demoShell` injecting static HTML — cited as evidence and misleading.
- `DashboardBrowserTests.fs` — "two tabs maintain independent sessions" admits it can't switch (tautology); "switch does not dispatch SessionSwitched" never clicks a switch; "responsive mobile viewport" navigates **before** `SetViewportSizeAsync` so 375×812 never applies.
- `DemoRecording.fs` hot-reload hero — `printfn`-only assertions, external unversioned workspace, waits for `apiVersion:1` while the current contract is `apiVersion:2`.
- TS `tests/seed.spec.ts` — empty placeholder; no hot-reload/live-testing/friction spec exists; nothing in CI runs `npx playwright test`.
- Coverage-view generation sweep + reconnect-clearing in VS Code are implemented but **unproven by any test**.

### The path to green (from the handoff, condensed)
Phase 0: delete/rewrite the misleading tests; get one dashboard journey runnable locally on the fresh net10 Release build with a fresh `SAGEFS_DATA_DIR`; wire the first CI job. Phase 1 (dashboard, closest): FR send-handler tests + Playwright journey → LT browser journey → HR browser journey. Phase 2 (VS Code): build the FR command, add an extension-host harness (VSIX from current SHA → isolated profile → CDP journeys). Phase 3 (VS): build the FR command, add Experimental Instance UI automation. Phase 4 (nvim, external repo): generation sweep + coverage consumption + friction action + real HR assertion, re-enable E2E in its CI, pin the repo+commit. Phase 5: flip rows `verified` with executable evidence; **update the inverted `DefinitionOfDoneTests` test in the same commit**; push → watch main → publish → confirm NuGet + Release. Phase 6: verify 0.6.412 on nuget.org, `dotnet tool update sagefs --global`, restart the user's daemon (ask first).

---

## 10. Standby-session rework — the standby pool is not pulling its weight; dissolve it into spawn-first restart

**Source:** `standby-session-rework.md` (handoff spec, 2026-09-03, untracked). The user's directive: the mechanism needs to change and the extra worker dissolved. This section is the roast's working summary of that spec; the spec has the full deletion inventory and RED tests.

### [IMPROVEMENT — architectural] The standby pool is a second, redundant warmth mechanism whose payoff path is rare and whose stale-code hazard is real
- **One consumer only:** `StandbyPool.decideRestart` (`SageFs.Core/StandbyPool.fs:118-127`) is used only from `RestartSession` (`SessionManager.fs:853-900`) with `rebuild=false` AND a Ready standby with a valid proxy. `rebuild=true` hard resets always cold-restart and **kill** the standby (`SessionManager.fs:909-914`) — and hard resets skew `rebuild=true`, so the pool's payoff is rare.
- **Create never consumes it:** `PoolState.tryConsumeStandby` (`StandbyPool.fs:163-168`) is referenced only by tests and the design doc, not the live create path.
- **Crash recovery cold-spawns:** `ScheduleRestart` (`SessionManager.fs:1228-1260`) never consults the pool.
- **`InvalidateStandbys` has no sender anywhere** — dead command; invalidation never fires and `decideRestart` has no staleness check, so a swapped-in standby can serve code from before the last file edits — a silent stale-code hazard in this product's own hot-reload workflow.
- **Measured cost:** ~245 MB resident per standby (older builds to ~486 MB), a held loopback port, double warmup CPU contention at boot. Boot-time resume already delivers "warm sessions" — the pool is redundant.
- why it matters: this is the "are the standby workers really holding their weight?" verdict, answered honestly: no. ~250-500 MB held forever per session config for one rare path with a stale-code bug.

### [IMPROVEMENT] Target design — spawn-first restart (P1-P8 in the spec)
Spawn the replacement worker *before* stopping the old one for `rebuild=false`; the shared `WorkerReady` handler is the commit point (set the new pid **before** stopping the old worker, making its exit inert); spawn failure leaves the session untouched and serving (strictly better than today's stop-first); rebuild=true and crash recovery stay as-is; exactly one worker per session always. Full deletion inventory (pool file, tests, metrics, dashboard badge, command cases, harness fields) and T1-T6 RED tests are in the spec.

---

## 11. What genuinely holds (round-2 verified)

- **The origin guard is real and complete on both daemon servers** — every mutating route on 37749/37750 is wrapped; the proxy is a fixed allow-list; friction read-side is owner-gated and the daemon never reads remote reports.
- **The phase refactor is genuinely complete** — the eval loop is phase-only, `Faulted` carries no state, reset-from-Faulted rebuilds from closure captures correctly, and the query/router actors are wrapped (the eval actor is the one gap).
- **Manifest reader hardening** — declared sizes/counts cross-checked against actual payload with per-entry budgets.
- **ErrorMessages ordering** is correct, regression-locked, first-line-scoped.
- **No tautological tests remain**; the fsproj compile list matches the on-disk file set (no orphans); the phase/tombstone work deleted ~1,650 lines of dead code and the Mcp move slimmed the host closure by the MCP surface.
- **Commit discipline is genuinely good** — last 60 commits ~100% conventional; props↔package.json in sync with a drift gate.

---

## 12. VERDICT

**Needs the second half — and it is currently unshippable.** Round 1 fixed the named defects with real skill — and left the structural ones, plus a few it created: the gating mechanism that makes the integration suites *never run*, the no-change suppression that suppresses the *send* but not the *cost*, the incremental cache that its own consumers bypass, the Mcp.fs move that stopped at the MCP-named modules still in Core, and a worker HTTP server that executes arbitrary F# with no guard while the daemon's two servers got one. The security posture is now inverted from round 1: the most-protected surfaces are the daemon's, and the least-protected is the one that runs the code. And two independent handoffs make the *next* work unambiguous: **nothing ships** until the 12 real-client journeys are executable same-SHA evidence (release-blockers.md), and the standby pool — ~245-500 MB held per session for one rare path with a stale-code bug — should be dissolved into spawn-first restart (standby-session-rework.md). The pool verdict the user asked for is honest: it is not pulling its weight; delete it.

## 13. RISK ASSESSMENT

**HIGH — and now release-shaped.** Round 1's dominant risk (unauthenticated daemon + `@develop` CDN) is closed. The new dominant risks: (1) the **worker HTTP server** — unguarded `/eval`/`/shutdown`/`/load-script` on an ephemeral 127.0.0.1 port with an intentional `*`-CORS SSE endpoint, reachable by cross-origin simple POSTs and DNS rebinding; (2) the **eval-result XSS sink** — agent-controlled F# output morphed into the dashboard DOM unescaped, a stored-XSS foothold with same-origin access to every mutating endpoint; (3) the **release gate** — 0.6.412 (and everything after) is blocked until 12 real-client E2E journeys are real, CI-executed evidence; the ptest/`[Integration]` gating that never runs in CI compounds it by making the regressions invisible; (4) the **eval actor still unwrapped** — one exception bricks the session owner permanently; (5) the **standby pool** — dead-command staleness hazard plus ~250-500 MB held per session for a rare payoff path.

## 14. PRIORITY FIX QUEUE (ordered by leverage)

1. **Guard the worker HTTP server** — apply the same origin/Host/token check the daemon servers got to `WorkerHttpTransport.fs` (or bind + require a per-session token the daemon proxies), and remove the blanket `ACAO:*` on `/__sagefs__/reload` in favor of an explicit origin list (`WorkerHttpTransport.fs:67,125,309,400-405`).
2. **Close the eval-result XSS sinks** — `HtmlEncode` the eval result/error fragment paths + binding TypeSig/Value, matching the convention already used for output lines (`Dashboard.fs:707-712,859-879,1037-1043,1259-1264`; `DashboardFragments.fs:2032-2036,721,727,1836,1851`).
3. **Make the integration suites runnable** — replace `ptestList "[Integration]"` with `testList` gated behind a structural filter that a `--all`/`--integration` CI job actually invokes; add the missing CI job; tag the untagged real-process/socket/Harmony suites (`Program.fs:140-151`; the ~15 ptest suites; `ShutdownLifecycleTests`, `ParentMonitorTests`, `DaemonStateTests`, `WorkerHttpTransportTests`, `HarmonyCanaryTests`, `MethodPatcherTests`).
4. **Move the no-change guard before the work** — version-check `buildDashboardSnapshot` on a cheap counter before any HTTP/SQLite/render; move the friction SQLite read off the push path (`Dashboard.fs:505-552,429-445`).
5. **Wrap the eval actor** in `ResilientActor.wrapLoop` like query/router (`AppState.fs:1073-1581`).
6. **Close the `WorkerReady` pid race** — compare `workerPid` like `WorkerExited`/`WorkerSpawnFailed` do (`SessionManager.fs:1026`).
7. **Register or delete the ~17 unregistered feature tools** (`McpTools.fs:1132-1467`) — the write-only stack is unreachable; keep EvalTimeline + Diagnostician/explain_test_failure.
8. **Route the per-push feature rebuild through the incremental cache** — make `handleFeaturePush` consume `appendCell`/delta scope instead of re-parsing ≤10k cells (`McpServer.fs:955`; `FeatureHooks.fs:129-148`).
9. **Dissolve the standby pool into spawn-first restart** — P1-P8 + T1-T6 in `standby-session-rework.md`; delete `StandbyPool.fs`/tests/metrics/dashboard badge; spawn failure leaves the session serving; exactly one worker per session.
10. **Slim the Core closure for real** — move SageFsApp/ElmDaemon/SessionDisplay (the daemon's Elm kernel) + the render stack into the daemon, or delete what only SageFs.Gui (not in the solution) consumes; extend the seam tests to assert Core∌render-stack.
11. **Give the eval worker a real per-eval timeout** (bounded CTS in `WorkerMain.fs:143-175` + bounded daemon proxy calls — retire the `InfiniteTimeSpan` `HttpWorkerClient`), and apply the pid/generation discipline to live-test result batches (`SageFsApp.fs:751-801` must call `onResultsArrived`/`StaleWrongGeneration`).
12. **Delete the dead** — WorkerLivenessMonitor (wire it to the /health probe or delete it), SessionMap leak (TryRemove on stop + inactivity), McpPort "preservation" (route to the eval actor or delete the claim), the inert `TerminalUIState.IsActive` gate, the duplicate IVT, the two surviving duplications (DaemonState shim, second SessionDisplayStatus).
13. **Make the test-cache codec non-lossy or version-bump it** (`TestCachePersistence.fs:336-407`), and include project/dependency content in the warmup fingerprint (`WarmupReplayCache.fs`).
14. **Replace the `--mutation-score` theater** with a real measured survivor count + a gate that can fail, or delete the flag.
15. **Contain session-create/discover** to the session root like eval-file/load-script already are (`Dashboard.fs:1207-1267`).
16. **Execute the release-blocker path** (per `release-blockers.md` phases): fix/delete the stale misleading tests; build the FR commands + journeys per client; close HR/LT/FR rows with executable same-SHA evidence run by CI; flip the matrix; update the inverted `DefinitionOfDoneTests` in the same commit; confirm NuGet + Release + tool update.
