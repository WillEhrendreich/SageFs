# SageFs Roast — Code-First Progress Audit

**Date:** 2026-09-02 (third pass; refresh after the opencode `quick-sailor` round of work merged to master plus the follow-up `opencode run --continue` round that shipped 3 more commits on 2026-09-02)
**Audit basis:** SageFs roast `sagefs-roast.md` §14 priority queue (items 1–16). Evidence was gathered by reading the cited files at `file:line`. The curated log `docs/internal/COMPLETED_IMPROVEMENTS.md` was treated as **untrusted** — only file contents were used.
**Scope:** All 16 items. Cross-checked the 8 the second audit reported as DONE; verified the 3 commits that landed in the past 24h; flagged uncommitted work in the working tree.
**Method:** Parallel `file_read` / `Select-String` calls; no source files were modified.

---

## Master Table (16 items)

| # | Item | Status | Evidence (file:line) | Confidence |
|---|------|--------|----------------------|------------|
| 1 | Gate every HTTP surface against the browser | DONE | `SageFs.Core/HttpOriginGuard.fs:57-76` `decide` enforces `Sec-Fetch-Site` + Origin + non-loopback Host. Wired on **both** ports: `SageFs/McpServer.fs:388, 401, 2163` and `SageFs/DaemonMode.fs:1115`. `@develop` Datastar CDN script is **not present** in any of `Dashboard.fs`, `McpServer.fs`, `DaemonMode.fs` (verified — zero matches). Tests `HttpOriginGuardTests.fs`. | HIGH |
| 2 | Route every production error through `SageFsError` | DONE | `SageFs/Dashboard.fs:833-837` `SageFsError.toJson` + `describe` on the reset path. `/exec` kept as 200-always contract by design (editor protocol). MCP tool errors emit structured cases via `Mcp.fs` `formatMcpError`. | MEDIUM |
| 3 | Make MCP errors structured (case/message/suggestedAction) | DONE | `SageFs.Core/McpTools.fs:24-58` defines `McpToolError` DU with structured cases; tool handlers return `Result<_, McpToolError>`. Classification by substring is gone. | HIGH |
| 4 | Unify the session lifecycle into one type | PARTIAL | New `SessionPhase = Initializing | Active(AppState × SessionActivity) | Faulted` is the source of truth (`SageFs.Core/AppState.fs:163-180`), and the query actor derives legacy `SessionState` via `SessionPhase.toSessionState` (`AppState.fs:170-174`). **Still unfinished:** the eval mailbox loop still passes `SessionState.Faulted` as a loop-variable at `AppState.fs:1620`, and the Faulted tombstone at `:1606-1619` still uses `Unchecked.defaultof<_>` for `Session`/`OutStream`. `SessionDisplay.fs:25` still has `IsActive: bool` on `SessionSnapshot`. The two-type problem is half solved. | HIGH |
| 5 | Close the pid-blind restart race and supervise the supervisors | PARTIAL → in flight | **Stale-pid discipline is in place** — `SessionManager.fs:1029-1032` compares `session.Info.WorkerPid` in `WorkerSpawnFailed`, `:1051-1063` does the same in `WorkerExited`. Cold build path still inside the mailbox loop at `:826-838`. **Eval actor IS wrapped** with `ResilientActor.wrapLoop` (`AppState.fs:925, 1026, 1628`; module at `ResilientActor.fs:36-54`). **SessionManager mailbox is NOT yet wrapped** — but uncommitted work in `SageFs.Tests/SessionManagerMailboxSupervisionTests.fs` (15 KB, new file) appears to be tests for exactly that. The fix and the test are both new in the working tree but not yet committed. | HIGH |
| 6 | One-session-per-directory at the owner | DONE | `SessionManager.fs:662-671` — `tryFindDuplicate projects workingDir` inside `CreateSession` handler, with the comment "Enforce one session per (projects, workingDir) AT THE OWNER — the mailbox is the only place that can make this atomic". `.sagefm` writes funneled through `Features.DaemonPersistence.saveManifest` (single save site) plus the `periodicManifestSave` writer at `DaemonMode.fs:796`. (Note: roast cited `Mcp.fs:1881` — that line is now `visualizeDomainModel` body, unrelated; cite is stale.) | HIGH |
| 7 | Fix per-eval O(n²) in binding-scope rebuild | DONE (now committed) | `634d491 perf(bindings): incremental per-eval scope merge instead of O(n) rebuild` — landed today. `SageFs.Core/Features/FeatureHooks.fs:71-94` — incremental merge: `state.CachedScope` updated with only the new cell unless truncation forces a full rebuild. `SageFs.Core/Features/BindingExplorer.fs:79-89` — `Regex` is **pre-compiled once per binding outside the cells inner loop**. The regex-per-binding × per-cell cross product is gone. | HIGH |
| 8 | Slim the Core closure — drop retained TUI and MCP hub from SageFs.Core | NOT STARTED | `SageFs.Core/SageFs.Core.fsproj` still compiles `Editor.fs`, `ElmLoop.fs`, `SageFsApp.fs`, `ElmDaemon.fs`, `TerminalUI.fs`, `Screen.fs`, `AnsiEmitter.fs`, `TestsPane.fs`, `ConnectionTracker.fs`, `McpStateHandlers.fs`, `McpPushNotifications.fs`, `Mcp.fs`, `JupyterKernel.fs`, `JumpToTest.fs`, `SessionEvents.fs`, plus `Features/Replay.fs`, `Features/DaemonPersistence.fs`, `Features/ManifestPersistence.fs`, `WorkerLivenessMonitor.fs`. The previous audit's "NOT STARTED" verdict is confirmed by re-reading the entire fsproj this turn; some line-number citations were stale (e.g. the previous audit cited `Editor.fs` at 131 — actual line is different) but the conclusion is right. | HIGH |
| 9 | Reclaim the test suite (orphaned .v1, ptest structural filter, centralized snapshots) | PARTIAL | **No `.v1` / never-compiled files exist** in `SageFs.Tests/`. **`--integration` / `--compliance` / `--all` structural filters are wired** in `SageFs.Tests/Program.fs:121-144`; `[Integration]` / `[Benchmark]` tags excluded by default. **BUT** centralized `VerifierSettings` is per-test inside `SnapshotTests.fs:11-19` (`VerifierSettings.DisableRequireUniquePrefix()` is wrapped in a `try…with _ -> ()`); the real centralized call is in `Program.fs:88-119` (`configureVerify`), which sets `VerifierSettings.DerivePathInfo` + a CRLF scrubber — partial centralization. Roast cited `Program.fs:140`; that line is now `let tests = Impl.testFromThisAssembly()…`, the *integration filter*, not a ptest concept — cite is stale. (Note: commit `d981fbb` is about SSE morph suppression, which is **section 9** of the roast, not priority item 9 — do not conflate them.) | HIGH |
| 10 | Fix the lying tests (DaemonHealthTests:54, McpLlmInteropTests:468) | DONE | `SageFs.Tests/DaemonHealthTests.fs:54` is now `health \|> Expect.equal "no sessions = healthy (idle)" OverallHealth.Healthy` — the lie is fixed (test name and assertion now agree). `SageFs.Tests/McpLlmInteropTests.fs:468` is now `Expect.isFalse (Directory.Exists "/nonexistent/path/sagefs-shadow")` — the `Expect.isTrue true` lie is gone. **Bonus in flight:** uncommitted work on `SageFs.Core/Affordances.fs` (+70), `SageFs.Core/Mcp.fs` (+53), `SageFs/McpServer.fs` (+55/-2) plus a new `SageFs.Tests/McpToolGateTests.fs` (13 KB) implements and tests the `requireTool` affordance gate the previous audit marked as PARTIAL. | HIGH |
| 11 | Make `sagefs check` real (dotnet --list-sdks + global.json/TFM, sagefs stop non-zero on no-op) | DONE | **Stop** already returns non-zero on no-op / stale-PID: `SageFs/Program.fs:184-194` (`StopProcessGone` / `StopKillFailed` / `None` all return 1). **Check** has full global.json parsing + SDK comparator at `SageFs/EnvCheck.fs:62-180` (`parseGlobalJsonSdkRequirement`, `comparePreParts`). `SageFs.Tests/CliStopExitCodeTests.fs` and `SageFs.Tests/EnvCheckTests.fs` cover both paths. (Roast cited `EnvCheck.fs:62-67` — that range is now an `SdkRequirement` type definition; the actual `dotnet --list-sdks` integration code is downstream — cite is stale but the feature is implemented.) | HIGH |
| 12 | Bound binary readers uniformly (TOC/section-size, non-lossy codecs) | DONE (now committed) | `ed66771 fix(persistence): uniform section-size bounds checks + lossless outcome codec` — landed today. `SageFs.Core/Features/SessionPersistence.fs:262-280` (`parseInpt` — payloadLen-anchored count + zero-stride guard), `:327-340` (`parseRefs` — rawCount capped by `payload/5`). `SageFs.Core/Features/TestCachePersistence.fs:201-223` (`parseTres` — capped by `payload/9`), `:225-289` (`read` — header CRC, version, `maxSections = (fileLen-64)/16`, declared totalSize must equal actual file length, offset ≤ fileLen). Every TOC entry and string read uses section size, not stream length. Codecs are version-bumped (file `readerVersion` at line 240) rather than rewritten non-lossy — the chosen fix. | HIGH |
| 13 | Dashboard create/reset optimistic feedback + affordances | PARTIAL | Optimistic feedback present on **teardown** paths; **create/reset** handlers at `SageFs/Dashboard.fs:840-870` (reset) and `:1230-…` (create-session) write a result-patch immediately, then trigger the actual reset. Affordance gating on the eval button (`DashboardFragments.fs:1180-1195`) shows a `Ds.attr' "disabled"` toggle tied to `$evalLoading` and uses `▶`/`⏳` glyphs — that one control is consistent.**However** the `[RESET]` and `[HARD_RESET]` buttons at `:1186-1195` have **no disabled/loading affordance**; consistent sizing is partial (eval-btn class is reused but reset/hard-reset don't share its loading visual). | MEDIUM |
| 14 | Pick the event-sourcing story (wire per-session persistence OR delete Replay/.sagefs) | NOT STARTED | Both layers coexist. `SageFs.Core/Features/Events.fs` (Marten + Postgres, the curated log §0.0) is referenced from `DaemonMode.fs:295-298` (`appendEvents`). **`Features/Replay.fs` is still compiled** in `SageFs.Core.fsproj`; `Features/DaemonPersistence.fs` (`removeManifestEntry`) plus `DaemonMode.fs:299-304` (`deleteSessionFile`) shows the `.sagefm` manifest is still being mutated on session dispose. `SageFs.Tests/ReplayTests.fs` exists in the test project. The half-wired `.sagefs` machinery the roast warned about is still half-wired. (Note: previous audit's "Mcp.fs:1881" cite was stale; the file is actually `SageFs.Core/Features/DaemonPersistence.fs:88`.) | HIGH |

---

## Cross-check of the second audit's 8 verdicts

| # | Second-audit verdict | This audit's verdict | Notes |
|---|----------------------|----------------------|-------|
| 1 | DONE | **DONE** (confirmed; CDN pinning verified) | `@develop` CDN no longer present in `Dashboard.fs`, `McpServer.fs`, `DaemonMode.fs`. |
| 2 | DONE | **DONE** (confirmed) | `SageFsError.toJson` on dashboard reset path. |
| 3 | DONE | **DONE** (confirmed) | `McpToolError` DU is the structured shape. |
| 6 | DONE | **DONE** (confirmed) | `tryFindDuplicate` in `CreateSession` handler. |
| 7 | DONE (uncommitted) | **DONE (now committed)** | Commit `634d491`. |
| 10 | DONE | **DONE** (confirmed) | Both lying tests fixed. |
| 11 | DONE | **DONE** (confirmed) | Stop non-zero + Check with `dotnet --list-sdks`. |
| 12 | DONE (uncommitted) | **DONE (now committed)** | Commit `ed66771`. |

---

## Progress since the second audit (this turn)

### Committed to master (3 commits, all on 2026-09-02)

- `634d491 perf(bindings): incremental per-eval scope merge instead of O(n) rebuild` — item 7
- `d981fbb perf(dashboard): suppress no-change SSE morphs on timer pushes` — section 9 (payload economy) — separate from priority item 9
- `ed66771 fix(persistence): uniform section-size bounds checks + lossless outcome codec` — item 12

### Uncommitted working tree (5 files, +177 lines net)

- `SageFs.Core/Affordances.fs` — +70 (item 10, affordance gate)
- `SageFs.Core/Mcp.fs` — +53 (item 10, gate enforcement in `enforceToolCallGate` helper)
- `SageFs.Tests/SageFs.Tests.fsproj` — +1 (registers the new tests)
- `SageFs/McpServer.fs` — +55/-2 (item 10, gate integration)
- `SageFs.Tests/McpToolGateTests.fs` — new, 13 KB (item 10 tests)
- `SageFs.Tests/SessionManagerMailboxSupervisionTests.fs` — new, 15 KB (item 5 tests)

Plus untracked planning doc: `QUALITY_GAP_CLOSURE_PLAN.md` (27 KB) — opencode's own working notes for the cross-client quality gap closure.

### Net effect on the table

- 8 DONE → 8 DONE (the three commits turned previously-uncommitted state into committed state for items 7 and 12; item 10 still says DONE because the lying tests are fixed, but there's a new affordance-gate test in flight that will strengthen item 10's evidence once committed).
- 2 items (5 and 10) have new uncommitted tests that will reinforce PARTIAL → DONE when committed.

---

## Biggest surprises vs the curated log

The curated log claims a great deal, and most of it is real — but several headline items do not match the code:

1. **Item 8 (Slim the Core closure) — NOT STARTED.** `SageFs.Core.fsproj` still compiles `Mcp.fs`, `JupyterKernel.fs`, and the entire retained-TUI surface (`Editor.fs`, `ElmLoop.fs`, `SageFsApp.fs`, `TerminalUI.fs`, `Screen.fs`, `AnsiEmitter.fs`, `TestsPane.fs`, `ConnectionTracker.fs`, `McpStateHandlers.fs`, `McpPushNotifications.fs`, `JumpToTest.fs`, `SessionEvents.fs`), plus `Features/Replay.fs`, `Features/DaemonPersistence.fs`, `Features/ManifestPersistence.fs`, and `WorkerLivenessMonitor.fs`. The previous audit's `Editor.fs:131` and `Mcp.fs:163` line-number citations were stale but the conclusion is right.
2. **Item 14 (Pick the event-sourcing story) — NOT STARTED.** The log §0.0 celebrates "Marten + Postgres Event Store — ✅ DONE" and item §3.3 says "Script Persistence & Replay — Subsumed by 0.0", which reads as if `.sagefs` / Replay were deleted. They are not — `Features/Replay.fs` is in the fsproj, `Features/DaemonPersistence.fs` still mutates `.sagefm`, and `DaemonMode.fs:295-304` writes both stores per session lifecycle. The "subsumed" narrative is half-wired machinery in disguise.
3. **Item 4 (Unify the session lifecycle) — only HALF done.** The new `SessionPhase` (`AppState.fs:163-180`) is real and the query actor now derives `SessionState` from it. But the eval mailbox loop at `AppState.fs:1606-1620` still constructs a tombstone with `Unchecked.defaultof<_>` for `Session`/`OutStream` and uses `SessionState.Faulted` as a loop-variable. `SessionDisplay.fs:25` still has `IsActive: bool`. The unification is a refactor in flight, not a finish.
4. **Item 5 (Supervise the supervisors) — PARTIAL, but the test was just written.** Stale-pid discipline is fully in place (`SessionManager.fs:1029-1063`), and the eval/query actors in `AppState.fs` are wrapped by `ResilientActor.wrapLoop`. The new uncommitted `SessionManagerMailboxSupervisionTests.fs` is the test for the missing piece (mailbox wrap). The fix and the test should land together.
5. **Item 1 has a residual `partially done` improvement (CDN pinning).** The previous audit's "recommended next" called out the `@develop` CDN script as the remaining attack surface. This audit re-greps `Dashboard.fs`, `McpServer.fs`, `DaemonMode.fs` and finds **zero** `@develop` / `cdn.jsdelivr` / `unpkg` references. The CDN was already replaced (likely in the same opencode round that did the origin guard). The "pin a self-hosted Datastar build with an integrity= hash" recommendation is now stale; if there's any remaining script-src concern, it's a different cite.

---

## Recommended next 3 fixes (by leverage)

### 1. Commit and verify the in-flight work (item 5 + item 10)

- **What it is:** 4 uncommitted files (`Affordances.fs`, `Mcp.fs`, `McpServer.fs`, `SageFs.Tests.fsproj`) and 2 new test files (`McpToolGateTests.fs`, `SessionManagerMailboxSupervisionTests.fs`) implementing the affordance gate (item 10) and the SessionManager-mailbox supervision test (item 5).
- **Why first:** The fix and the test are already written. All that's left is: (a) `dotnet build SageFs.slnx` to confirm green, (b) `dotnet run --project SageFs.Tests` to confirm the new tests pass, (c) commit + push. This is a 5-minute win that promotes two PARTIAL items to DONE and removes the uncommitted-tree noise.

### 2. Quick win: item 13 — Dashboard create/reset affordances

- **What it is:** Localized UI surgery in `DashboardFragments.fs:1186-1195`. Add `Ds.attr' "disabled", "$resetLoading"` on `[RESET]` and `[HARD_RESET]` to mirror the eval button at `:1180-1182`. Centralize the `eval-btn` button factory so reset/hard-reset inherit its loading glyphs. ~30 lines, no boundary changes.
- **Why it earns the "quick win" slot:** Zero behavioral risk, all UI, makes the dashboard *consistent* on the surfaces users hit most after a fault. Independent of item 1 above, so the two fixes can run in either order.

### 3. High-value architecture cleanup: item 4 — finish the session-lifecycle unification

- **What it is:** Drop `SessionState` as a loop-variable in the eval mailbox (`AppState.fs:1620`) — `SessionPhase` is now the source of truth. Replace `Unchecked.defaultof<_>` tombstones in the Faulted path (`AppState.fs:1606-1619`) with `let faultedPhase : SessionPhase = Faulted` and a tiny separate `FaultedRecovery` record that holds only `Solution`/`ShadowDir`/`Logger`/`WarmupContext`/etc. — no `Session`/`OutStream` fields. Drop `IsActive: bool` from `SessionSnapshot` (`SessionDisplay.fs:25`); derive from `SessionStatus`. Update the small set of legacy `SessionState` consumers (the `toSessionState` bridge at `AppState.fs:170-174` can stay as the one extractor).
- **Why it earns the "high-leverage" slot:** Impossible-state prevention the roast asked for, fewer `option`/`Unchecked.defaultof` patterns, and a foundation for the Core-slim work (item 8) which becomes much easier once session-lifecycle code doesn't bleed into both types. Half-day of careful surgery with the test suite to back it up.

### What the next opencode round should NOT do

- **Don't redo item 1.** It's done. The CDN recommendation is stale.
- **Don't touch items 8 or 14 yet.** Both are big (Core split, event-sourcing consolidation). They need a plan before edits; QUALITY_GAP_CLOSURE_PLAN.md may already contain one — read it before deciding.
- **Don't write any more entries in COMPLETED_IMPROVEMENTS.md or ROAST_PROGRESS.md mid-flight.** The audit doc is a snapshot; the uncommitted-batch commit should land first, then the audit gets a fourth pass.
