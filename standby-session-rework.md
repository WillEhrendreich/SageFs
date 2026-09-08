# Standby-session rework: dissolve the standby pool, add spawn-first restart

Status: handoff spec for a fresh agent. Read this file completely before changing anything.
Scope: `C:\Code\Repos\SageFs` (monorepo, `master`). Line numbers verified against `0.6.412` on 2026-09-03 — **re-verify every line number on your checkout before relying on it.**

This document is intentionally untracked. Do NOT commit it, and do NOT `git add -A` anything during this work — stage files by explicit path only.

---

## 1. Objective

Remove the standby worker pool entirely and replace it with **spawn-first restart** for the no-rebuild hard-reset path: spawn the replacement worker **before** stopping the old one, then swap. Result must be *smaller* (pool code, dashboard badge, metrics, and tests deleted), *simpler* (one restart path + one fallback, no pre-warmed processes), and *provably safer* (the stale-code swap hazard becomes structurally impossible; spawn failure leaves the session untouched).

The daemon currently spawns one `SageFs.Host.exe` per session **plus** one resident "standby" worker per session config (~250–500 MB each, held forever). The pool is consumed by exactly one code path. We are deleting that mechanism and its surface.

## 2. Why (evidence for the decision)

- The pool's only consumer is `RestartSession` (hard reset) with `rebuild=false` AND a `Ready` standby with a valid proxy: `StandbyPool.decideRestart` (`SageFs.Core/StandbyPool.fs:118-127`), used from the `RestartSession` handler (`SageFs.Core/SessionManager.fs:853-900`).
- `rebuild=true` hard resets always cold-restart and **kill** the standby (`SessionManager.fs:909-914`). Hard resets skew `rebuild=true` (that is why users reset), so the pool's payoff path is rare.
- Session **create** never consumes standbys. `PoolState.tryConsumeStandby` (`StandbyPool.fs:163-168`) is referenced only by tests and the design doc — not by the live create path.
- **Crash recovery** (`ScheduleRestart`, `SessionManager.fs:1228-1260`) cold-spawns; the pool is never consulted.
- `SessionCommand.InvalidateStandbys` has **no sender anywhere in the repo** — dead command. Invalidation never fires, and `decideRestart` has no staleness check, so a swapped-in standby can serve code from before the last file edits. In this product's own hot-reload workflow that is a silent stale-code hazard.
- Boot-time resume already delivers "warm sessions" (the daemon re-spawns persisted sessions at startup), which is the warmth users actually experience — the pool is a second, redundant warmth mechanism with a narrower payoff.
- Measured cost on this machine: one standby ≈ 245 MB resident (older builds up to ~486 MB), a held loopback port, and double warmup CPU contention at boot.

## 3. Target design: spawn-first restart

### 3.1 Behavioral contract (these are the properties to preserve/establish)

- **P1 — Spawn-before-stop**: for `RestartSession(rebuild=false)`, the new worker's `StartWorkerProcess` is invoked *before* `StopWorker` on the old worker.
- **P2 — Old-worker exit is inert**: the old worker's `WorkerExited` event must never be interpreted as a real exit. The stale-pid guard at `SessionManager.fs:1155-1175` already ignores exits whose pid ≠ `Info.WorkerPid` (and `WorkerPid = None` with `pid > 0`). Therefore the registry must point at the **new** pid *before* `StopWorker` is called on the old worker.
- **P3 — Spawn failure is free**: if `StartWorkerProcess` returns `Error`, the session must be left untouched — still `Ready`, old worker still serving — and the error returned to the caller. (Strictly better than today: the current code stops the old worker first, so a failed spawn leaves nothing.)
- **P4 — Post-spawn warmup failure faults (unchanged envelope)**: if the new worker spawns but never reports ready (port/warmup timeout), the session faults exactly like today's cold path. Reuse the existing bounded timeout logic (`AwaitWorkerPort`, `SageFsConfig.WorkerStartupTimeoutMs`).
- **P5 — rebuild=true unchanged**: keep the existing cold path (stop, mark `Restarting`, spawn, `RebuildCompleted` respawn point) exactly as-is.
- **P6 — Exactly one worker per session, always**: after a session reaches `Ready`, exactly one worker process exists for it; a restart replaces the pid but never increases the count; stopping a session leaves no worker. No pooled/idle processes exist at any time.
- **P7 — Registry continuity**: the session stays registered for the whole restart (no missing-session window) and transitions `Restarting → Ready`, mirroring the invariant documented at `SessionManager.fs:915-921`.
- **P8 — Concurrent-reset guard stays**: keep the rejection of a second hard reset while a cold rebuild is in flight (`SessionManager.fs:836-843`).

### 3.2 Recommended shape (adapt to the codebase, keep the properties)

Replace the `SwapStandby` branch of `RestartSession` (`SessionManager.fs:854-900`) with a spawn-first branch for `rebuild=false`:

1. Call `runtime.StartWorkerProcess id session.Projects session.WorkingDir session.AutoOpenNamespaces session.Workflow onExited` (same session id; `onExited` posts `SessionCommand.WorkerExited`).
   - `Error` → reply error to the caller; return state unchanged (P3).
   - `Ok newProc` → continue.
2. Record the swap: add a small map to `ManagerState`, e.g. `PendingSwap: Map<SessionId, Process>` holding the **old** process to retire; mark the session `Status = Restarting` with `Proxy = pendingProxy`, `WorkerBaseUrl = ""` (registry continuity, P7). Do **not** clear `Info.WorkerPid` yet and do **not** change it to the new pid yet.
3. Start `runtime.AwaitWorkerPort id newProc inbox ct`. It will post `SessionCommand.WorkerReady(id, newPid, baseUrl, proxy)` when the new worker is up (same machinery as normal create).
4. **Commit point — the shared `WorkerReady` handler** (`SessionManager.fs:1026-1058`): when a `WorkerReady` arrives for a session that has a `PendingSwap` entry,
   - set `Info.WorkerPid = newPid` **before** anything else (this is what makes the old worker's eventual exit stale, P2);
   - install `proxy` / `baseUrl` / `WorkerPort` as the handler already does;
   - then `do! runtime.StopWorker <oldSession>` using the old session record (its exit event is now stale vs. the new pid) and remove the `PendingSwap` entry.
   - The existing async ready-poll (lines 1064-1096) already flips the status `Restarting → Ready`.
5. Reply to the `RestartSession` caller immediately after step 1-3 succeed (before the swap completes), e.g. `Ok "Hard reset accepted — replacement worker spawning."` — do not block the mailbox on worker startup (the mailbox loop must never await the worker's lifetime; everything async goes through `Async.Start`, exactly like the existing `awaitWorkerPort` / poll code).

Notes:
- Do **not** reuse the dummy-session-id trick (`SessionId.newId()` used for standbys) — spawn with the real session id so the shared `WorkerReady`/`WorkerExited` machinery keys on it.
- The `ColdRestart` branch of `RestartSession` (rebuild=true) plus the whole `ScheduleRestart` crash-recovery path stay untouched.
- If you find a cleaner in-mailbox shape that still satisfies P1–P8, use it — but the tests in §5 pin the properties, not the implementation shape.

## 4. What to delete (complete inventory)

Work from this list top-down; the compiler will surface stragglers. Grep for `Standby|standby|standb` after each step to confirm zero leftovers.

### 4.1 Core (`SageFs.Core`)
- **Delete file** `SageFs.Core/StandbyPool.fs` and its entry in `SageFs.Core/SageFs.Core.fsproj` (`<Compile Include="StandbyPool.fs" />`, line 105).
- `SageFs.Core/SessionManager.fs`:
  - Command cases (lines 74-83): `WarmStandby`, `StandbyReady`, `StandbySpawnFailed`, `StandbyExited`, `StandbyProgress`, `InvalidateStandbys`, `GetStandbyInfo`.
  - `ManagerState` fields `StandbyInfo` / `PerSessionStandby` (155, 158); `QuerySnapshot.StandbyInfo` field (208, 219) and `empty` (231); `computeStandbyInfo` (166-189), `computePerSessionStandby` (191-205); the `fromState` standby parameter (208-219, 223).
  - `SessionManagerRuntime` fields `AwaitStandbyPort`, `StopStandbyWorker` (236, 238) and their implementations `awaitStandbyPort` (541-606), `stopStandbyWorker` (609-646).
  - Handlers: `WarmStandby` (1333-1362), `StandbyReady` (1364-1388), `StandbySpawnFailed` (1390-1398), `StandbyExited` (1400-1408), `StandbyProgress` (1410-…), `InvalidateStandbys` (1443-…).
  - `WarmStandby` posts: after the old swap (899, dies with §3.2) and in `WorkerReady` (1054-1058, delete — no standby warmup on ready).
  - The standby-kill block inside `ColdRestart` (909-914) — delete (no pool).
  - Keep (still used by the live create path): `hasValidReadyTransport`, `describeInvalidReadyTransport`, `AwaitWorkerPort`, `StopWorker`, `pendingProxy`, the `WorkerReady` and `WorkerExited` handlers.
- `SageFs.Core/Instrumentation.fs`: delete `standbySwaps` (34-35), `standbyPoolSize` (64-65), `standbyWarmupMs` (66-67), `standbyInvalidations` (68-69), `standbyAgeAtSwapMs` (70-71). Keep `sessionsRestarted` (32-33) and `coldRestarts` (36-37); update `coldRestarts` description to "restarts using stop-then-spawn (rebuild=true or spawn-first failure fallback)". Optionally add a `spawnFirstRestarts` counter if you want the two-path visibility — keep it if and only if you also emit it from the new branch.
- `SageFs.Core/SessionDisplay.fs`: remove the `Standby: StandbyInfo` field (58) and the standby parameter/population (104 and surrounding). Grep for `SessionDisplay` consumers and clean them.
- `SageFs.Core/SageFsApp.fs`: remove `Standby = StandbyInfo.NoPool` (228) and the `Standby` field it fills.

### 4.2 Daemon / dashboard (`SageFs`)
- `SageFs/DashboardTypes.fs`: remove `StandbyLabel` from the session card type (434) and its default (`479`); remove `GetStandbyInfo` / `GetSessionStandbyInfo` from the query abstraction (597-598).
- `SageFs/DaemonMode.fs`: remove the `GetStandbyInfo` implementation (319-320) and the `GetStandbyInfo`/`GetSessionStandbyInfo` wiring (1684-1686).
- `SageFs/SessionMode.fs`: remove `GetStandbyInfo` from the ops record (28) and its default (46).
- `SageFs/Dashboard.fs`: remove `StandbyLabel` population (286-292, 975-976, 1015-1016) and the panel standby render (1396-1412).
- `SageFs/DashboardFragments.fs`: remove the `getSessionStandbyInfo` parameter (1323, 1344-1347) and the badge population (1333-1334).
- Check `SageFs/Mcp.fs` — no standby references found on 2026-09-03; do not add any.

### 4.3 Tests (`SageFs.Tests`)
- **Delete file** `SageFs.Tests/StandbyPoolTests.fs` (if the test fsproj lists files explicitly, remove its `<Compile Include>` too — check).
- `SageFs.Tests/SessionManagerRestartTombstoneTests.fs`:
  - Delete the standby-notification test list `sessionManagerStandbyNotificationTests` (490-531).
  - Rewrite the test "standby ready without a proxy is discarded and restart falls back to a cold respawn" (146-183) to the new contract (spawn-first; see §5 T2/T3/T5). `GetStartCalls()` math and the "worker respawning" message expectation will change.
  - The harness itself (30-99): remove `AwaitStandbyPort` / `StopStandbyWorker` from `mkRuntime` (44, 46), the `GetStandbyProgressNotifications` harness field (15, 60, 65, 76), and its callback argument.
- `SageFs.Tests/LifecyclePropertyTests.fs`: delete `warmupDecisionTests` / `shouldWarmStandby` property tests (201-242).
- `SageFs.Tests/Round4HardeningTests.fs`: delete the `invalidateForDirTests` list (67-108) and its header comment (45).
- `SageFs.Tests/DashboardSnapshotTests.fs`: delete `standbyBadgeSseTests` (541-583); remove `StandbyLabel` from all fixtures (86, 100, 273, 410, 415) and the `GetStandbyInfo`/`GetSessionStandbyInfo` stubs (164-165).
- `SageFs.Tests/DashboardParsingTests.fs`: remove `StandbyLabel` from fixtures (224, 509, 534, 567, 592).
- `SageFs.Tests/CompletionsContractTests.fs`: remove `GetStandbyInfo` from the stub (41).

### 4.4 Docs
- `docs/warmup-design-next.md`: Section 2 ("Eager Prewarm Design") documents the pool as existing state (107-170) and references the standby metrics (159-170). Rewrite section 2 to state the pool was removed in favor of spawn-first restart and that eager prewarm is off the table until there is real create-latency evidence; drop the metric references. Keep sections 1 and 3+ intact.
- `docs/internal/type-algebra-summary.txt`: line 340 (`Standby: StandbyInfo` in a view record). If this file is generated, regenerate it via its generator; otherwise hand-edit to drop the standby member and re-run any generation check.

## 5. RED → GREEN → REFACTOR

Follow the repo's TDD skill (`dotnet-fsharp-tdd-workflow`) and its phase discipline: RED tests land first (compiled, run through the real test path, committed as the proof-of-broken), then GREEN, then REFACTOR. Test conventions (non-negotiable): Expecto, `Expecto.Flip` message-**first**, `testCase "name" <| fun _ -> ...`, plain records (no anonymous-record literals), no hardcoded Windows paths (`Path.Combine`; derive dirs from the test assembly location), 2-space indent.

Drive the mailbox tests through the existing harness in `SessionManagerRestartTombstoneTests.fs` (`mkRuntime` + `withHarness`, fake `StartWorkerProcess`/`RunBuildAsync` with counters). That harness already gives you `GetStartCalls()`; extend the fake runtime record with a shared `ResizeArray<string>` verb log if you need ordering (both `StartWorkerProcess` and `StopWorker` append `"start"` / `"stop"`).

### RED tests (write first; must fail against current code)
- **T1** "a session that becomes ready spawns exactly one worker (no standby)": `createSession` + `WorkerReady(id, pid, url, proxy)` → `GetStartCalls() = 1`. Today this fails (WorkerReady posts `WarmStandby` → second spawn).
- **T2** "non-rebuild hard reset spawns the replacement before stopping the old worker": create + ready, then `RestartSession(id, false, reply)` → verb log ends with `[...; "start"; "stop"]` and contains exactly one `"start"`. Today this fails (stop-first / standby swap).
- **T3** "non-rebuild hard reset with a spawn failure leaves the session Ready and serving": fake `StartWorkerProcess` returns `Error (WorkerSpawnFailed "...")` → restart reply is `Error`; session status remains `Ready`; `WorkerPid` unchanged; `GetStartCalls()` unchanged after the attempt. Today this fails (cold path stops the worker first).
- **T4** "rebuild hard reset keeps stop-then-spawn order": `RestartSession(id, true, reply)` → verb log contains `[...; "stop"; "start"]`. Must keep passing after the rework (guards P5).
- **T5** "the retired worker's exit during a swap is ignored": after a non-rebuild restart is accepted (spawn recorded) and *before* `WorkerReady` for the new process, post `WorkerExited(id, oldPid, 0)` → the session must NOT schedule a `RestartAfter`/crash-recovery restart; it stays `Restarting` and completes when `WorkerReady` arrives. Today this fails (no swap state exists; behavior differs).
- **T6** (optional, slow — land in `ptestList` per repo convention) "one worker process per session after create and after hard reset": integration test that creates a session over the daemon HTTP API with `SAGEFS_DATA_DIR` pointed at a fresh temp dir per daemon spawn (never touch `~/.SageFs`; sweep `SageFs*` processes between runs), then asserts the count of `SageFs.Host` processes == session count and that a no-rebuild hard reset keeps the count at session count while changing the pid.

### GREEN
Implement §3.2. Keep P1–P8. Do not commit GREEN until T1–T5 pass through the real test runner.

### REFACTOR
Execute §4 deletions, then run the full default suite; fix only what your change broke. Grep `Standby|standby|standb` across `SageFs.Core`, `SageFs`, `SageFs.Tests` — expect zero hits (doc-whitelisted: `docs/warmup-design-next.md` may retain a historical mention).

## 6. Verification (unit + live)

1. `dotnet build -c Release` — zero warnings (Release is the safety gate; FS3511-class warnings must not appear).
2. Default suite green via the repo's expected path (per AGENTS.md: via the SageFs REPL locally; `dotnet test` in CI). Note `StandbyPoolTests.fs` deletion removes its perf tests (tryConsumeStandby < 5µs etc.) — that's fine, they test deleted code.
3. Verify snapshots: if any `.verified.*` snapshot fails because the dashboard card/JSON lost `StandbyLabel`/the badge, that is an *intentional contract change* — regenerate the `.verified` file from the same render call and confirm the diff shows only the standby removal (repo-wide CRLF scrub already exists in the harness root; do not per-file patch line endings).
4. Live end-to-end (mandatory, per project norms — verify on the running tool, not just tests):
   - Fresh `net10.0` Release build; kill any running daemon/`SageFs*` processes first (ask the user before killing their daemon; the daemon holds the port and must be restarted to run the new code).
   - Start the daemon from the **new** binary; confirm `sagefs --version`/`/api/status` version matches the build.
   - Open a session; while idle, list processes: exactly **one** `SageFs.Host` per session, no extra standby (before the rework there was one extra per config).
   - Hard reset without rebuild: the session's `SageFs.Host` pid changes, count stays at session count, session returns to `Ready`, dashboard shows no standby badge and never did.
   - Hard reset with rebuild=true: same count invariant, cold path works.
   - Stop all sessions: zero `SageFs.Host` processes remain.
   - Confirm reclaimed memory: no ~245 MB resident worker after a session stops.

## 7. Definition of Done

- [ ] `StandbyPool.fs`, `StandbyPoolTests.fs`, and every standby symbol/field/handler/metric removed; grep for `Standby|standby|standb` in code returns zero hits.
- [ ] `RestartSession(rebuild=false)` uses spawn-first (T2 passes), spawn failure leaves the session serving (T3 passes), old-worker exits are inert during swap (T5 passes), rebuild path untouched (T4 passes), exactly one worker per session (T1, T6).
- [ ] `dotnet build -c Release` clean; full default suite green (GREEN committed with all T-tests passing through the real runner — never commit a RED).
- [ ] Dashboard no longer renders standby badges; contract tests/snapshots updated to the new card shape.
- [ ] Live verification on the running tool completed per §6.4 with the process-count and pid-swap evidence.
- [ ] Commits follow Conventional Commits (`refactor(session): remove standby pool`, `feat(restart): spawn-first hard reset` — one logical commit per phase; each leaves the suite green). Co-author trailer required on every commit.
- [ ] `git status` shows only intended changes plus this untracked doc — nothing staged accidentally.

## 8. Operational rules (from repo memory)

- The daemon never exits on its own and the launcher keeps a handle open: start daemons detached (`Start-Process -WindowStyle Hidden`), never `-Wait`/`Wait-Process` on them, and verify with a hard-timeout HTTP probe or process list — never a bare sleep-and-narrate.
- Integration tests must isolate daemon state: `SAGEFS_DATA_DIR` = fresh temp dir per spawned daemon; never create/delete anything in the user's real `~/.SageFs`. Stale `SageFs*` processes from aborted runs hold ports — sweep them before re-running.
- Don't create a second session for the same `workingDirectory` the shared/test session uses — duplicate sessions break `/exec` routing.
- The installed global tool (`~/.dotnet/tools` store) can be stale; live verification must run the fresh Release binary, and confirm the serving version before trusting results.