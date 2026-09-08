# The SageFs Roast — Round 3 (usability · performance · correctness first)

**Taste rating:** The product surfaces are finally ahead of the theory. The round-2 queue is ~10/16 closed and the release gate is green; what's left is concentrated exactly where the user waits: the eval hot path, the live-testing stream, the dashboard push loop, and session memory growth. Per the owner's explicit framing — long files don't matter under ~10k lines; lightning-fast, correct, and usable do — the god-file complaint is retired to a single footnote, and this round leads with the three things a human or agent actually feels: how fast an eval/tick/test-run lands, whether the UI's state is true, and whether a session that runs for days stays fast.

**Single most important observation:** the two worst remaining defects are the same defect twice — the eval actor thread and the SSE push path both do expensive, blocking, O(n)-per-eval work *before* they decide there is nothing to send or compute: the live-value reflection walk (with a JSON serialize→deserialize round trip) runs on the actor for every eval, and the dashboard rebuilds + renders the entire page before its no-change guard fires. Meanwhile the streaming test proxy's silent timeout leaves tests stuck as `Running` forever, and session teardown never releases the adaptive snapshots, feature state, or output buffers — so a long-lived daemon gets slower and heavier exactly the way users notice.

Every claim carries `path:line` evidence gathered fresh against HEAD `9430543` (gate green). Resolved round-2 items are stated as resolved; the surviving line is cited for the rest.

---

## 1. Usability — what the user actually sees

**What world-class looks like:** every long action shows progress *before* it starts and resolves to a truthful terminal state; the same event can never hang a spinner or fabricate a failure; memory is released on teardown so days of sessions don't cost days of RAM; an idle dashboard costs ~nothing.

### [HIGH] A timed-out test stream silently ends and the UI shows tests stuck `Running` forever — or fabricates failures
- `HttpWorkerClient.fs:111-121` and `:165-175`: the SSE read races each `ReadLineAsync` against a fresh `Task.Delay(readTimeout)` via `WhenAny`; on timeout the loop just sets `keepReading <- false` and the `Async<unit>` **returns successfully**. No line tells the caller which tests never finished. The caller (`SageFsApp.fs:2516`) then unconditionally dispatches `TestRunCompleted` (`:2549`), which clears the run phase — but tests that never received a result event stay `Running`/spinner in the UI forever.
- The *only* alternative is the `with ex` path (`SageFsApp.fs:2561-2577`) which marks **every** test `Failed(ExceptionThrown)` — synthesizing failures for tests that never executed. So on a worker stall the user gets either eternal spinners or a wall of fabricated red — never "2 of 5 tests never completed."
- This is the single most user-visible defect in the product: it's the live-testing headline feature lying about its own results.
- World-class: on timeout, explicitly mark each not-yet-`done` test `TimedOut` and emit a real completion with the partial picture; cancel the in-flight read with one CTS instead of leaking a timer per line (see §2).

### [HIGH] Session teardown leaks the adaptive store, feature state, and output buffers — the dashboard degrades day over day
- `LiveBindingsAdaptive.remove` (`LiveBindingsAdaptive.fs:70-72`) has **zero production callers**: every evaluated session's `cval` snapshot cells (each a reflected value tree) accumulate for the daemon's lifetime. The dashboard's per-session live-bindings subscription `IDisposable` is disposed only on *re-subscribe* (`Dashboard.fs:832-846`), not on session stop.
- `sharedFeatureState` is one daemon-global `FeaturePushState ref` (`DaemonMode.fs:1502`) holding up to 10,000 history rows plus cached scope/graph/timeline, keyed to the *last* session, and never reset when a session stops. Bindings and history from dead sessions bloat forever.
- `RecentOutput.Clear` runs on user `ClearOutput` (`SageFsApp.fs:850-856`) but nothing clears the output ring on session stop — the ring grows with every distinct session id.
- Why it matters: an agent or user who opens/creates/closes sessions all day (the advertised workflow) leaves the daemon permanently fatter; the live values panel and filmstrip slow down proportionally. Nobody notices on day one; everyone notices on day ten.
- World-class: teardown (`StopSession`/`PurgeSession`) calls `LiveBindingsAdaptive.remove`, disposes the subscription, clears the session's buffer, and session-keys or resets `FeaturePushState` — the "delete the obj/bin of a session" doctrine the owner already articulated.

### [IMPROVEMENT] Every eval POST rebuilds the entire dashboard snapshot — the eval response pays a full dashboard render
- `createEvalHandler` (`Dashboard.fs:898-899`) calls `buildDashboardSnapshot` (worker HTTP fetches + SQLite + all panels) on *top of* the SSE stream's own build, just to render the action response. The comment at `:895-897` explains the Datastar-overlap reason, but the cost is a full extra snapshot per eval — and eval throughput is the product's headline latency.
- World-class: reuse the already-built snapshot (same tick/generation) for the action response, or render the reply from the freshest cached snapshot rather than rebuilding.

### [IMPROVEMENT] Idle dashboard ticks still do full SSR + full HTML compare; the friction SQLite read blocks a thread on the push path
- `pushState` (`Dashboard.fs:683-731`) builds the complete snapshot (worker fetches — TTL-cached at `:657-666`, good — plus the friction `Async.RunSynchronously` SQLite read at `:464`) and renders the whole `#main` *before* the `mainHtml = lastPushedMain` guard (`:726-728`) suppresses the send. A connection with nothing changing still renders the whole page every second; the blocking SQLite read fires every ~2s (TTL=2000ms vs tick=1000ms).
- The code comments still claim "state reads are cheap" (`:644-646`) — they are two HTTP round-trips plus a synchronous disk read. Real worker changes arrive as events; the 1s fallback poll exists only for the disconnected/initial case, yet it pays the full cost per tick.
- World-class: a cheap version counter (the infra already carries `outputCount` in `DaemonStateChange.ModelChanged`) before `buildDashboardSnapshot` makes an idle tick an int compare; the friction read becomes `task`-based and off the push path.

### [IMPROVEMENT] Warmup/ready verification polls each session's `/status` every second for up to 120s
- After spawn, the manager polls the worker over HTTP at 1 Hz until Ready or timeout (SessionManager.fs warmup-poll loop). N warming sessions = N GETs/sec at the exact moment workers are busiest compiling. Startup-only, so lower severity — but it is the first thing every new session's user waits on.
- World-class: event the ready signal out of band (the worker already knows when it is ready internally) and poll only as a fallback with backoff.

### Verified good usability (rounds 1-2 fixes all hold)
- **Optimistic teardown** — the card morphs to "⏳ Stopping…" before the await and auto-advances to the next session or the picker; create/reset now write an immediate result patch and gate on a shared `$actionLoading` disabled signal (`DashboardFragments.fs:1176-1198`).
- **No-session state renders the full shell** — header, sidebar Sessions, New Session, statusline all visible; the 0.6.470/471 blank-screen and missing-panel regressions are closed (`Dashboard.fs:513-617`).
- **Responsive single-column session cards** that wrap instead of cramp (`dashboard.css:615-651`), and per-connection session discipline so tabs don't clobber each other (`DashboardTypes.fs:573-581`).

---

## 2. Performance — the hot paths the user can feel

**What world-class looks like:** the eval actor replies and moves on; the debugger-watch tree is built lazily by whoever subscribed; a stalled stream fails the batch fast with a deadlined single CTS; per-eval work is O(1) amortized; timers idle down to zero when nothing is running.

### [CRITICAL] The binding-scope append is still O(n) per eval — O(n²) across a session
- `recordEval` (`FeatureHooks.fs:50-141`) did adopt the incremental cache and the daemon push path consumes it (`McpServer.fs:948-960`) — the round-2 "re-parse 10k results" complaint is closed. But on every non-truncating eval it still rebuilds `priorCellInputs` by mapping the *entire* `EvalHistory` (up to 10,000 entries) at `FeatureHooks.fs:103-109`, and `appendCell` then walks all of them for each new binding name to compute `ReferencedIn` (`BindingExplorer.fs:130-180`, `cellsReferencingName` at `:140`). The cost moved from "re-parse every source" to "walk every source per eval" — same asymptote.
- Why it matters: a long interactive session (the product's core scenario) does 10k-element scans on every single eval, for a feature (cell dependency/filmstrip intel) whose consumer may not even be subscribed.
- World-class: keep the cell list between evals and append (O(1)), and index names→cells so `cellsReferencingName` is a lookup, not a scan. First step is trivial: reuse the previous eval's `priorCellInputs` list instead of re-mapping 10k entries.

### [CRITICAL] The live-value reflection walk + JSON round-trip runs on the eval actor, per eval, before the reply
- `EvalFinished` (`AppState.fs:1130-1192`) walks every bound value via reflection (`:1145-1157`), builds the tree (`LiveValueTree.fs`, depth 6 × 50 children — node-bounded, but top-level binding count unbounded, so worst case 50⁶ nodes incl. arbitrary property getters), serializes the whole tree to JSON on the actor thread (`:1162`), and only then `reply.Reply` (`:1191`). The daemon then **deserializes** that JSON back (`DaemonMode.fs:1918-1922`) to feed `LiveBindingsAdaptive`.
- The round-trip is doubly wasteful: `LiveSnapshotSink` already exists (`DaemonMode.fs:1551`, consumed at `Mcp.fs:1310`) and could carry the snapshot in memory, but the eval path still goes through the JSON metadata channel. And the work happens even when zero SSE/dashboard/MCP consumers are attached.
- Why it matters: eval latency as observed by callers includes a reflection walk over arbitrary user types + a full JSON stringify — on the thread that owns the FSI session. This is the "sub-500ms on every save" promise's biggest fixed tax.
- World-class: post snapshot building to a bounded background queue after `reply.Reply` (or push raw `LiveValueSnapshot` via `LiveSnapshotSink`); build the tree lazily for the consumers that actually subscribed.

### [HIGH] Streaming test proxy leaks a timer + in-flight read per line and can't distinguish "done" from "stalled"
- `HttpWorkerClient.fs:111-121` / `:165-175`: one `Task.Delay(readTimeout)` and one unconsumed `ReadLineAsync` per line, never cancelled; a stalled worker leaves every timed-out read dangling until the run's end. No bounded-test-run timeout exists anywhere in the module.
- See §1 for the user-visible consequence. Together: unbounded timer accumulation during a run + permanent-spinner/fabricated-failure UI after a stall.
- World-class: one `CancellationTokenSource` per run cancelled on timeout to kill the in-flight read (`HttpClient.Timeout` + a request CTS already bound the operation); on timeout, terminate the loop *with* an explicit failure listing the not-yet-finished tests.

### [HIGH] The 25 ms test-cycle timer scans the full live-testing map at 40 Hz — even when no session is running
- `testCycleTimer` (`DaemonMode.fs:1562-1565`) dispatches a `TestCycleTick` every 25 ms unconditionally; the handler runs `LiveTestCycleState.tick` on the primary state **and** maps over every entry in `PerSessionLiveTesting` (`SageFsApp.fs:1551-1573`) regardless of whether any session is live-testing. Coalescing (`:319-322`) and the same-model-ref no-op (`:1575-1576`) keep it cheap when idle, but the 40 Hz map iteration is forever tax on a laptop battery even with zero sessions.
- World-class: gate the timer on at least one active live-testing session; idle down to a slow heartbeat (or stop) when none exist.

### [IMPROVEMENT] Sync-over-async on the eval and push paths
- Per-eval middleware: `ComputationExpression.fs:265` (`rewriteCompExpr |> Async.RunSynchronously`) and `Directives.fs:33` (`CodeFormatter.ParseAsync |> Async.RunSynchronously`) block on the evaluation pipeline; warmup/hard-reset block on `AppState.fs:673` and `:1508`; the MCP failure-capture path blocks on `McpServer.fs:129`; the dashboard blocks on the friction read (`Dashboard.fs:464`) and the signals read (`:1113`). Each individually defensible; the eval-path two run on the application's most time-sensitive thread.

### [IMPROVEMENT] `daemon.sagefm` has four unsynchronized read-merge-write writers
- `saveManifest` from prune (`DaemonMode.fs:204`), shutdown (`:615`), the 60s periodic save (`:769`), and purge (`DaemonPersistence.fs:87`). Atomic tmp+move prevents torn writes (`ManifestPersistence.fs:300-317`) but there is no lock or single owner — overlapping writers can lose each other's committed session changes. Under multi-client concurrency (advertised), this is a lost-update risk.
- World-class: one manifest-writer actor owns the only read-merge-write; callers hand it state to persist.

### [IMPROVEMENT] Warmup fingerprint: projects content-hashed, sources still mtime-stamped
- The round-2 dependency-content gap is closed — `.fsproj`/`.sln` files are SHA-256 content-hashed (`WarmupReplayCache.fs:29-33,173`). Residual: `.fs` sources are path/length/mtime stamped (`:15-20,95-109`), so a source edit preserving length+mtime serves a stale warmup plan.

### Verified good performance (rounds 1-2 fixes hold)
- **Incremental scope/graph/timeline caching with a cache-consuming daemon push path** (`FeatureHooks.fs:50-141`, `McpServer.fs:948-960`) and a genuinely O(n) map-indexed shadow pass (`BindingExplorer.fs:65-75`).
- **TTL-cached worker fetches** in the dashboard (`Dashboard.fs:657-666`) and the no-change morph suppression — the wire send is correctly gated, even if the build isn't.
- **Lossless v2 test-cache codec, backward-compatible, uniformly bounds-checked** (`TestCachePersistence.fs:125-126,344-459`); periodic saves are atomic and one-shot (reschedule-after-completion, `DaemonMode.fs:1585-1598`).
- **Spawn-first restart** with stale-pid guards replacing the standby pool: no idling workers, no held ports, exactly one live worker per session (`SessionManager.fs:690-733,895-907`).

---

## 3. Correctness — where the product can be wrong

**What world-class looks like:** every claimed capability is exercised by a run CI actually performs; evidence is executable, same-SHA, and cross-checked — never a "green locally" string; every code-execution surface has the strongest guard; error signals are typed, not sniffed; the UI can never render a state the state machine forbids.

### [HIGH] Six DoD rows are "verified" on evidence CI never executed; three share one 13-test file in another repo
- `definition-of-done.json`: 10 `verified` + 2 legitimate N/A (FR-VSC/FR-VS, documented `reason`). But `HR-VSC-E2E`/`LT-VSC-E2E` cite "green locally"; `HR/LT/FR-NVIM-E2E` all cite the *same* `sagefs.nvim spec/e2e/e2e_realdaemon_spec.lua — 13/13 green locally` — one small file vouching for three different capabilities — and the CI-cited SHAs (`d3647cf`, `79f1a3b`, `c26220c`) predate HEAD `9430543`. The gate (`publish.yml:71-82`) and test (`DefinitionOfDoneTests.fs:83-89`) correctly enforce "no deferred" but trust the `status` string; nothing resolves the cited runs.
- World-class: evidence rows carry a workflow run ID / pinned external commit that the gate resolves; "green locally" is a TODO, not a verification.

### [HIGH] A tier of `[Integration]` suites and the entire VS Code journey tier never run in CI — and VS Code journeys pend instead of failing when absent
- `--integration-vsc` (`Program.fs:171-177`) is invoked by **no workflow**; `VscodeExtensionTests.fs:333-394` registers `ptestCase … ignore` whenever `Code.exe` isn't found (`:64`), so on a machine without VS Code the journeys are silent-green — the VSC DoD rows are untestable-in-CI by construction.
- Ordinary (non-ptest) `[Integration]` suites with **zero CI coverage** remain: `ActorSplitTests.fs:16`, `DaemonIntegrationTests.fs:163,221,318`, `HttpApiIntegrationTests.fs:353,780,946,1091`, `McpServerIntegrationTests.fs:19`, `McpLlmInteropTests.fs:28,70,134,158`, `SessionIsolationTests.fs:75,704`, `SessionResetTests.fs:32,101`, `FalcoTests.fs:107`, `ExplorationTests.fs:9`, `DiagnosticsToolTests.fs:88,139`. These are real-process/daemon suites; CI green says nothing about them. (`--all`/`--integration` exist at `Program.fs:212-229` and no workflow calls them.)
- World-class: one `--integration` entry point runs the full tagged set in CI (VS Code included, with a real installed editor), and an absent prerequisite is an explicit counted skip or a failure — never a pending pass.

### [HIGH] The worker's mutating-route classifier misses four code-executing endpoints
- `WorkerHttpTransport.fs:57-65` classifies `/eval`, `/check`, `/typecheck-symbols`, `/shutdown`, `/reset`, `/cancel`, `/load-script`, `/hotreload/*` as mutating (Sec-Fetch-Site + Origin). **`/hard-reset` (`:261-267`), `/run-tests` (`:269-277`), `/run-tests-stream` (`:279-381`), and `/completions` (`:234-241`)** fall through to the read-only branch (`:127-132`) which checks only `Origin`. These are the arbitrary-code-execution endpoints — a cross-site POST with a loopback-formatted Origin (or a non-browser client) reaches them through the weaker gate.
- World-class: classify each route from the route table/attribute so a future `MapPost` can never silently land on the weak branch; reject `Sec-Fetch-Site: cross-site` for all non-GET globally.

### [HIGH] One unescaped XSS sink and raw path echoes remain
- `evalResultError` still renders `Text.raw msg` (`DashboardFragments.fs:2023`), fed by caller strings (`Dashboard.fs:1441,1462,1499,1512,1539,1839,1860,1863`); `switchedDir`/`dir` echoes are raw too (`Dashboard.fs:1127-1128,1188,1227,1416,1441`). Everything else on the page HtmlEncodes (`Dashboard.fs:912,1064,1078,1086,1236`). One hostile directory name reaches the DOM unescaped.

### [MEDIUM] The editor-protocol HTTP family still sniffs strings; the round-2 typing only reached `/exec`
- `/reset` (`McpServer.fs:1292-1293`), `/hard-reset` (`:1306-1307`), `/cancel` (`:1313-1314`), `/api/cancel-eval` (`:1321-1322`), `/load-script` (`:1369-1371`): `result.StartsWith("Error")` + `{success;message}`. A legit output containing "Error" flips `/reset` to failure by construction. `/exec` uses the typed outcome (`:1262,1279-1282`) — the siblings should follow it. MCP tool errors remain flat `Task<string>` text; `case/message/suggestedAction` exists only as "→ Next:" prose in `describeForAgent` (`Mcp.fs:1147-1200`), so agents branch on regexes over English.

### [MEDIUM] The mutation-score gate reports a number that is binary
- `Program.fs:66-97`: `killed = match exitCode with 0 -> totalMutations | _ -> 0` — 100% or 0%, inferred from whether the group passed, not measured per mutant; CI passes no `--threshold`, so the threshold is inert. It can fail (real safety), but "the reported score is measured" (`:63-65`) is not true.

### [MEDIUM] VS Code journeys are machine-dependent and timing-fragile
- Hardcoded `C:\temp\sagefs-vscode-test-%s.png` (`VscodeExtensionTests.fs:323`) and workspace `C:\Code\Repos\SageFs` (`:342,358,373`); fixed `Task.Delay(8000/2000/1500)` sleeps (`:569,583,594,605`) — the least deterministic waits in the suite. The dashboard journeys' Stopwatch-deadline polls are the pattern to copy.

### [MEDIUM] Two `SessionDisplayStatus` types still define the same concept differently
- `SessionDisplay.fs:8-14` (6 cases, `Errored of string`, `Stale`) vs `DashboardTypes.fs:385-391` (5 cases, `Faulted`, `Lost`) — parallel vocabularies mapped back and forth in one app; a state the one can express the other cannot.

### Verified correct (rounds 1-2 fixes hold)
- **All three servers** are behind fail-closed origin/Host middleware (`McpServer.fs:2165-2166`, `DaemonMode.fs:1072-1073`, `WorkerHttpTransport.fs:179-180`); the SSE CORS reflects one loopback origin, never `*`; the `@develop` CDN is a pinned self-hosted bundle (`Dashboard.fs:63-75`); friction send is SSRF-blocked, server-assembled, hash-stored, and owner-gated (`Dashboard.fs:1281-1396`; `friction-receiver/src/index.ts:293-351`).
- **Worker lifecycle races closed**: stale-pid guards in Ready/SpawnFailed/Exited (`SessionManager.fs:837-861,980-1024,1028-1061`), dedupe at the owner (`tryFindDuplicate` in `CreateSession`), spawn-first swap, `Faulted` is a clean zero-field `SessionPhase` with no tombstones (`AppState.fs:162-209`), eval/query/router wrapped in `ResilientActor` (`AppState.fs:1703`).
- **The binary/persistence layer is defensible**: uniform section-size bounds checks, lossless v2 codec with min-reader-version, content-hashed project fingerprints.

---

## 4. Everything else (one line each — not the point of this round)

- God-file sizes: `LiveTestingTypes.fs` at 4,960 lines is under the 10k bar the owner set; it is a maintainability smell, not a blocker, and doesn't rank here. `DemoRecording.fs` (1,111 lines of video tooling in the test assembly) is the same class of "ships the render stack" as round 1, on the test side.
- Verify snapshot config is fragmented (five files re-call `DisableRequireUniquePrefix`; the CRLF scrubber lives only in `Program.fs:179-210`); `bypass_dod` is a reachable, unrecorded escape hatch (`publish.yml:14-18,72,84-88`); the Google Fonts CSS is the last unversioned external load (`Dashboard.fs:228-230`); `McpPort` is display-only (`Mcp.fs:663,1510,1597`).

---

## 5. What genuinely holds — the good decisions (verified, unpadded)

- **The eval actor is now the best-pattern actor**: `ResilientActor.wrapLoop` (`AppState.fs:1703`), CQRS split with immutable snapshots served via `Volatile.Read` with zero mailbox round-trip (`AppState.fs:1041-1060,1800-1825`), and the expensive cold build moved off the SessionManager mailbox (`SessionManager.fs:765-768`).
- **Spawn-first restart with parked old worker and stale-pid guards** — race-free worker swap, strictly better than stop-then-start, and the standby RAM/port hoard is deleted (`SessionManager.fs:690-733,895-907`).
- **Cache-consuming incremental analysis** — scope/graph/timeline caches appended incrementally and *consumed* by the daemon push path, O(n) shadow pass; the runtime behavior matches the theory.
- **Lossless, versioned, uniformly bounded binary persistence** — the codec the round-1 audit said was lossy is now provably not.
- **Origin guards on all three servers, self-hosted pinned Datastar, hashed friction secrets, SSRF-blocked endpoints** — the security posture inverted from round 1 to exactly right.
- **Hermetic browser journeys** — fresh `SAGEFS_DATA_DIR` per run, dynamic ports, drain-to-file child output (the undrained-pipe deadlock avoided), process-tree kill (`DashboardBrowserRunner.fs:55-97,358-372,578-601,802-822`).
- **The DoD gate and its tests tell the truth** — 0 deferred, `DefinitionOfDoneTests.fs:83-89` un-inverted, matrix N/A rows carry reasons; the remaining defect is evidence quality, not gate honesty.
- **The UI doctrine held where it matters** — optimistic teardown + auto-advance, no-session full-shell render, disabled/in-flight controls, responsive single-column cards.

---

## 6. VERDICT

**Fix the eval thread and the delivery apparatus, and this product is honest.** The round-2 queue is mostly closed and verified at the right layer; the remaining defects are not scattered — they are the eval hot path (reflection walk on the actor, O(n²) reference scan, per-line timers, 40 Hz idle scan, blocking middleware) and the evidence apparatus (CI-orphaned suites, pending-not-failing VS Code journeys, "green locally" DoD rows, binary mutation score). One fix each for streaming timeouts (deadline-based CTS + explicit `TimedOut` results), session teardown (release adaptive state), and no-change ticks (version-gate before the build) would resolve the three things a user can actually feel: stuck/fabricated test results, a daemon that fattens over time, and an idle dashboard that burns CPU. The long-files complaint is retracted per the owner's bar; the codebase's best machinery — typed port, pure cores, supervised processes, provable closures — is institutionalized. What remains is concentrated, visible, and fixable.

## 7. RISK ASSESSMENT

**MEDIUM-HIGH, and now user-shaped.** (1) **Live-testing result integrity** — a worker stall yields eternal spinners or fabricated failures, and the stream leaks timers per line; this is the headline feature's worst failure mode. (2) **Unbounded growth** — adaptive snapshots, daemon-global feature state, and output rings survive teardown; long-lived daemons get slower and heavier. (3) **Evidence gaps** — VS Code journeys and a full `[Integration]` tier never run in CI, and six DoD rows rest on local runs, so regressions in exactly the shipped release-gate capabilities are invisible. (4) **Worker route classification** — four code-executing endpoints sit behind the weaker gate. (5) **The unescaped `evalResultError` sink** — stored-XSS foothold with same-origin access to mutating endpoints.

## 8. PRIORITY FIX QUEUE (usability and perf first, per the owner's framing)

1. **Fix the streaming test proxy** — one CTS per run cancelled on timeout; on stall, mark not-yet-finished tests `TimedOut` and emit a truthful completion instead of silent success; stop leaking a per-line timer/read (`HttpWorkerClient.fs:111-121,165-175`; `SageFsApp.fs:2516-2549`).
2. **Release session state on teardown** — call `LiveBindingsAdaptive.remove`, dispose the dashboard subscription, clear the session output buffer, and session-key or reset `FeaturePushState` on stop (`LiveBindingsAdaptive.fs:70-72`; `DaemonMode.fs:1502`; `Dashboard.fs:832-846`; `SageFsApp.fs:850-856`).
3. **Get the reflection walk off the eval actor** — post snapshot building to a background queue after `reply.Reply`, or ship `LiveValueSnapshot` through the existing `LiveSnapshotSink` and drop the JSON serialize→deserialize round trip (`AppState.fs:1141-1163`; `DaemonMode.fs:1918-1922`; `DaemonMode.fs:1551`).
4. **Make the reference scan incremental** — append to a persistent cell list instead of re-mapping 10k history per eval; index names→cells to make `cellsReferencingName` a lookup (`FeatureHooks.fs:103-112`; `BindingExplorer.fs:140`).
5. **Hoist the no-change guard** — version-check before `buildDashboardSnapshot`; make the friction SQLite read async and off the push path; reuse the fresh snapshot for eval-POST responses (`Dashboard.fs:464,657-666,685-731,898-899`).
6. **Idle down the 40 Hz timer** — gate `testCycleTimer` on active live-testing sessions (`DaemonMode.fs:1562-1565`; `SageFsApp.fs:1551-1573`).
7. **Run the full integration tier in CI, including VS Code** — wire `--integration-vsc` with a real installed editor (explicit counted skip, never a pending pass) and fold the orphaned `[Integration]` suites into a single `--integration` job (`Program.fs:108-177,212-229`; `VscodeExtensionTests.fs:333-394`).
8. **Make DoD evidence executable and resolved** — replace "green locally" with CI run IDs / pinned external commits the gate checks; don't let three capabilities share one 13-test file (`definition-of-done.json:6,8,10,12,16`).
9. **Close the worker gate's route list** — move `/hard-reset`, `/run-tests`, `/run-tests-stream`, `/completions` onto the strong branch, ideally via route-table-driven classification (`WorkerHttpTransport.fs:57-65,234,261,269,279`).
10. **HtmlEncode the last sinks** — `evalResultError` and the `switchedDir`/`dir` echoes (`DashboardFragments.fs:2023`; `Dashboard.fs:1127-1128,1188,1227,1416,1441`).
11. **Serialize the manifest writers** — one owner for `daemon.sagefm` read-merge-write (`DaemonMode.fs:204,615,769`; `DaemonPersistence.fs:87`).
12. **Extend the typed-outcome pattern from `/exec` to its siblings**, and emit structured `McpToolError` content blocks instead of prose (`McpServer.fs:1292-1371,211,245`).
13. **De-flake the VS Code journeys** — temporary directories and deadline polls instead of `C:\temp`/`C:\Code\Repos\SageFs` hardcodes and fixed 8 s sleeps (`VscodeExtensionTests.fs:323,342,358,373,569`).
14. **Give the mutation gate a real measurement** — per-mutant kill accounting and a meaningful threshold, or rename it a kill-smoke (`Program.fs:66-97`).
15. **Unify the display vocabulary** — one `SessionDisplayStatus` derived from `SessionPhase` (`SessionDisplay.fs:8` vs `DashboardTypes.fs:385`).
16. **Move `DemoRecording` out of the test assembly** (1,111 lines, video tooling, Playwright, no CI consumer), consolidate the five Verify-config call sites, and self-host the font (`DemoRecording.fs`; `SnapshotTests.fs:11` et al.; `Dashboard.fs:228-230`).