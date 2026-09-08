# SageFs Cross-Client Quality Gap Closure Plan

Status: active handoff document — intentionally untracked

Last updated: 2026-09-02

## Objective

Do not describe SageFs as complete until hot reload, live testing, and friction
feedback are demonstrably usable across:

- Web Dashboard
- VS Code
- Visual Studio
- Neovim

Completion requires executable evidence for normal behavior, failures,
reconnection, daemon restart, worker recovery, stale/out-of-order events,
multi-session isolation, accessibility, and performance. Green builds or the
existence of handlers/classes do not count as user-journey proof.

## Repositories

- Main: `C:\Code\Repos\SageFs`
- Website: `C:\Code\Repos\SageTech`
- Neovim canonical checkout found by audit: `C:\Code\Repos\sagefs.nvim.git`
- A second Neovim copy also exists: `C:\Code\Repos\sagefs-nvim`

## Current Published State

- SageFs commit: `51345ff fix(dashboard): unify session output and retire legacy clients`
- SageTech commit: `e1323fc docs(sagefs): focus product docs on maintained clients`
- Published release: `v0.6.352`
- SageFs CI, smoke, correspondence, GitHub Pages, and publication succeeded.
- SageTech test, VPS deploy, and production health check succeeded.
- `https://sagetech.dev/sagefs` and `/sagefs/docs` show maintained clients and
  preserve Raylib application/game demos.

These facts prove the bounded dashboard repair and documentation deployment.
They do not prove complete cross-client product behavior.

## Verified Evidence So Far

### Dashboard session/output repair

- Main snapshot, selected session card, output panel, and evaluator use the same
  viewing-session identity.
- The stale second `sessionId` Datastar signal and empty hidden binding were
  removed.
- Long-lived SSE writes are serialized through one mailbox; adaptive bindings
  no longer write concurrently to the response.
- A dashboard action response includes the freshly committed full `#main`
  snapshot, preventing action-stream ordering loss.
- Real browser acceptance passed:
  - submit `let dashboardProbe = 8967` through the dashboard;
  - eval result appears immediately;
  - output panel contains the result immediately;
  - main/output/selected IDs match;
  - no click or reload is required;
  - browser console has no errors.
- Dashboard snapshot suite: 118 passed.

### Mutation/correspondence evidence

- Mutation suite: 89/89 planted faults caught.
- Existing Lean correspondence work remains green.
- This is strong evidence for selected pure functions, not complete UI journeys.

### Product boundary

- Built-in SageTUI client, legacy TUI, and `SageFs.Gui` are deprecated and
  disconnected from the shipped CLI project/default solution.
- Raylib application/game demos remain in the solution and docs because they
  prove game-project support independently of the deprecated GUI frontend.

## Tracking Issues

- #127 — cross-client hot-reload acceptance gaps
- #128 — cross-client live-testing acceptance gaps
- #129 — safe cross-client friction feedback

## Work In Progress — Not Yet Committed

The following fail-closed quality-gate work was started after release 0.6.352:

- `quality/definition-of-done.json`
- `SageFs.Tests/DefinitionOfDoneTests.fs`
- `SageFs.Tests/SageFs.Tests.fsproj` includes the validator file
- `SageFs.Tests/Program.fs` adds `--release-readiness`
- `.github/workflows/publish.yml` invokes the release-readiness gate

Intended semantics:

- Development validation permits explicit, owned, non-expired deferrals.
- Release validation fails if any required journey remains deferred.
- Current matrix has twelve deferred real-client journeys: three capabilities
  times four clients.

Current status:

- `DefinitionOfDoneTests` compiles and passes in SageFs.
- Development validation reports zero structural errors.
- Release validation reports twelve explicit blockers.
- Publish workflow uses a dependency-free PowerShell JSON check so publication
  cannot fail open or depend on restoring the full test project.
- The richer Expecto validator remains in the normal test suite.

## Definition of Done Rules

A matrix cell is Verified only when the relevant executable evidence ran against
the same source revision and inspected user-visible behavior.

Allowed evidence layers:

1. Pure invariant/property test
2. Wire/protocol contract test
3. Client adapter/parser/state test
4. Real client E2E
5. Failure/reconnect/recovery E2E
6. Accessibility acceptance
7. Performance budget

Forbidden shortcuts:

- build success as behavior proof;
- endpoint-name/source-text checks as wire proof;
- direct mutable assignment as file-save hot reload proof;
- parser fixtures as IDE/editor UX proof;
- pending/skipped required tests;
- tests matching zero cases;
- running E2E against a previously installed client rather than the artifact
  built from the current SHA;
- declaring all clients complete based on one dashboard flow.

## Hot Reload Audit

Strict user-action matrix currently contains no fully Verified client cells.

### Shared P0 blocker

Real file-save propagation into a running module-declared application is
documented as broken:

- `docs/internal/HOT_RELOAD_STATUS.md`
- `docs/hot-reload.md`
- `SageFs.Core/Middleware/HotReloading.fs`
- `SageFs.Host/WorkerMain.fs`
- `SageFs.Host/WorkerHttpTransport.fs`
- `SageFs/Resources/devreload.js`

Existing tests often mutate a value directly in FSI or manually replace page
content. Those are not proof of Harmony method replacement after a real file
save.

### Required first RED test

Create a real module-declared Falco/ASP.NET fixture whose route closes over a
function. Then:

1. Start the app through a SageFs Live-workflow session.
2. Watch the actual source file.
3. Request the route and record value A.
4. Edit the function body on disk to value B.
5. Observe `Compiling -> Reload` through the real worker path.
6. Request the same running process without restart.
7. Require value B.

Likely test home: `SageFs.Tests/WebAppHotReloadVerificationTests.fs`.

### Failure/recovery RED test

1. Save invalid F#.
2. Require user-visible mapped diagnostic while running app retains last valid
   behavior.
3. Correct the file.
4. Require diagnostic clear and new behavior without session recreation.

### Session isolation blocker

`DaemonStateChange.HotReloadChanged` currently loses affected session identity.
Downstream code may fetch state from a global active session. Carry the session
ID and generation through the event and reject stale/mismatched snapshots.

### Closed — hot-reload shared P0 (commits b4b7686, 29b68f7, 4f05740)

The save-driven propagation gap is fixed end-to-end and proven by real
[Integration] tests in `WebAppHotReloadVerificationTests.fs` and
`DaemonStateChangeContractTests.fs`:

- Host runs on net10 (Harmony needs it); Core multi-targets net10/net11.
- Root causes fixed: namespace files converted to nested modules so re-eval
  module identity matches #load; `_SageFsHotReload` gate finally bound via
  embedded base.fsx; getAllMethods namespace-seeding + FSI-prefix handling;
  single-assembly FSI dedup bypass; self-detour exclusion; NoInlining for
  indented module-member functions; duplicate-attribute guard; file watcher
  dirs from the manual-parse fallback.
- RED→GREEN: real file save → Compiling → Reload → running process serves the
  new value (no restart). Failure/repair journey: broken save broadcasts
  `failed` with diagnostics while the app retains last valid behavior; the
  repair save hot-reloads it.
- Session isolation: `HotReloadChanged`/`FileReloaded` now carry the session
  ID; the SSE push handler uses the event's session, never the global active
  one; `LiveTestWatcherManager` attributes file reloads to owning sessions
  (shared dirs included) and releases watchers only when no claim remains.
- Live gate passed: `dotnet pack` → install 0.6.356 → daemon → session Ready
  via the packaged net10 host closure.

### Closed — live-testing shared P0, server side (commit b01777d)

- Zero-test suppression: first-time zero-test discovery now reaches
  ReadyZeroTests (meaningful-change fix) and carries a monotonic
  DiscoveryGeneration. test_summary wire events carry DiscoveryState +
  DiscoveryGeneration on replay and live push whenever live testing is
  active — a completed zero-test discovery is observable to connected and
  late clients.
- Truthful command failures: /api/live-testing/enable, /disable, /policy
  return 503 success=false on internal failure (Cannot .../Unknown ...),
  keeping the 200 success shape otherwise.
- Model tests: zero-test discovery bumps generation; re-discovery sweeps
  absent same-session tests. Wire tests pin ready_zero_tests serialization.

### Closed — friction safety P0 (commits 87f0a4c, af704e7, b0e9e19)

- Unstarted Async in recordToolFailure fixed: pre-wrapper MCP failures are
  durably persisted (reopen test proves exactly one event). Same discard
  pattern fixed in withEchoNoAwaitRecord.
- Dashboard send path: destination validated (https only; http to loopback
  only), 15s timeout, and success is reported only after local
  RecordSentReport succeeds — no more false success on receipt-write
  failure.
- Sanitizer truncation off-by-two fixed (was maxLen+2); F# property tests
  added (paths/IP/email/session-id redaction, bounded output, totality).
- Receiver: tsx declared (npm test runs: 31 passed); payload cap enforced by
  reading the body stream, not trusting Content-Length.

### Closed — VS Code client gaps (commits ec2842e, 58d7553)

- Reconnect truth: onReconnect fires only after the replacement SSE
  connection's HTTP response arrives (verified behaviorally), never on the
  initial connection.
- Strict session isolation: untagged session-scoped events are rejected when
  a session filter is set.
- Zero-test observability: VscTestSummary carries DiscoveryState +
  DiscoveryGeneration; status bar distinguishes discovering /
  ready_zero_tests / disabled. Fable compile + esbuild clean; 5 golden
  tests pass including the new ready_zero_tests cases.

Remaining open items (client E2E, dashboard journeys, accessibility,
performance gates, VS + Neovim phases) are tracked in the matrix.

### Dashboard gaps

- Hot Reload ON/OFF is inferred from watched-file count rather than actual Live
  workflow capability.
- No real Playwright journey clicks watch controls, saves a file, and sees a
  changed running app.
- No real failed-compilation overlay/recovery E2E.
- Reconnect test does not restore and verify authoritative hot-reload state.

### VS Code gaps

- Commands/tree exist but no extension-host hot-reload journey.
- Save, failure diagnostics, restart recovery, and multi-session delayed-response
  rejection are unproved.
- `sse-helpers.js` invokes reconnect callback before the replacement connection
  actually succeeds; connected UI can be premature.

### Visual Studio defects

- Hot-reload commands use `sessions[0]`, not the document-owning/selected
  session.
- HTTP exceptions are swallowed and commands can print success after failure.
- Directory “toggle” always watches.
- No real IDE E2E.

### Neovim defects

- Hot-reload snapshot application lacks strict session filtering.
- Event generation/order protection is absent.
- Existing real-daemon E2E is disabled in CI.
- Existing save test only proves daemon survival, not changed behavior.

## Live Testing Audit

Core state-machine evidence is substantial. Client convergence and recovery are
not complete.

### P0 shared protocol requirement

Define one authoritative session-scoped live-testing snapshot containing:

- SessionId
- activation state
- discovery state (`Disabled`, `Discovering`, `ReadyZeroTests`,
  `ReadyWithTests`)
- discovery generation
- complete discovered test set
- policies
- run generation/freshness
- complete results
- source locations
- failure narratives
- coverage generation/view

Treat the snapshot as replacement state, never additive replay.

### Concrete server defect: zero tests

The domain models `ReadyZeroTests`, but SSE replay/live pushes are suppressed
when there are zero entries. Editors cannot distinguish completed-zero from no
state/disconnected.

Relevant code:

- `SageFs.Core/Features/LiveTestingTypes.fs`
- `SageFs/McpServer.fs` replay and summary emission
- `SageFs.Core/SageFsApp.fs`

First RED contract: completed zero-test discovery must emit an explicit
session-scoped authoritative snapshot to connected and late clients.

### Rediscovery defect

VS Code, Visual Studio, and Neovim merge newly discovered tests but do not remove
renamed/deleted tests. Replacement discovery must sweep absent tests by
generation.

### Truthful command failure defect

Live-testing enable/disable/policy HTTP routes report HTTP 200 and success even
when internal operations fail. Return typed non-2xx/`success=false` responses.

### Visual Studio defects

- Parser does not consume live-testing enable/disable events, so toolbar state
  can remain OFF after enable.
- Subscriber has no session filter and accepts all sessions.
- `test_run_started` is not parsed.
- Reconnect neither clears stale state nor requests a complete snapshot.
- Source jump lacks proven caret placement.
- No aggregate `coverage_view` consumer.

### VS Code defects

- Zero tests are unobservable.
- Deleted tests linger.
- Reconnect announces success before HTTP connection success.
- Reconnect does not clear all derived TestController/coverage/narrative state.
- `coverage_view` events append and can duplicate after replay.
- Untagged events are accepted, weakening strict isolation.

### Neovim defects

- Zero tests are unobservable.
- Deleted tests linger.
- Several events are not declared session-scoped: discovery, policies, source
  locations, providers, narratives, and aggregate coverage.
- Untagged events are accepted.
- Recovery helper exists but actual reconnect depends on incomplete replay.
- `coverage_view` is classified/forwarded but not stored/rendered natively.

### Dashboard gaps

- Some summary/coverage queries are session-scoped, but global status/source
  location accessors remain.
- Failure narratives do not offer source navigation.
- No complete multi-client/two-session browser acceptance harness.

## Friction Feedback Audit

Current reality: durable MCP-local telemetry with reports. It is not a usable
cross-client feature.

### P0 automatic-capture defect

`SageFs/McpServer.fs` constructs an `Async` for outer exception persistence and
discards it without starting/awaiting it. Pre-wrapper MCP failures are therefore
not durably captured.

First RED test: invoke the real capture filter with a throwing/binding-failure
handler, reopen SQLite, and require exactly one persisted friction event.

### P0 safety defects

Dashboard send handler accepts client-provided payload and arbitrary endpoint,
then forwards directly:

- sanitizer is not enforced immediately before send;
- arbitrary endpoint enables SSRF/proxy behavior;
- HTTPS is not required;
- remotely bound dashboard has no inbound authorization;
- no retry/offline queue/idempotency;
- receipt-write failure can still report success.

Relevant code:

- `SageFs.Core/Features/FrictionSanitize.fs`
- `SageFs.Core/Features/FrictionSqlite.fs`
- `SageFs/Dashboard.fs`
- `SageFs/McpTools.fs`
- `SageFs/McpServer.fs`
- `friction-receiver/src/index.ts`

Required safety order:

1. Build report from local typed data, not caller JSON.
2. Sanitize inside daemon immediately before serialization.
3. Restrict destinations to configured HTTPS allowlist.
4. Require authorization when dashboard binds beyond loopback.
5. Persist pending attempt before network send.
6. Use timeout/cancellation/idempotency.
7. Record sent receipt only after remote acceptance and local receipt success.

### Dashboard UX is missing

`DashboardSnapshot.FrictionPanel` currently receives an empty node. The send
handler exists, but no review drawer, sanitized preview, editing, endpoint
configuration, history, or retry UI is rendered.

Required journey:

1. Open discoverable friction action.
2. Review raw-local context versus sanitized outbound fields.
3. Edit explicit short reason.
4. Explicitly opt in to send.
5. See pending/success/rejected/timeout states.
6. Reopen and see local send history/pending retry.

(2026-09-02 status: CLOSED — the friction review panel is implemented in
the dashboard sidebar (expanded view): local event/feedback counts, top
tools summary, editable per-feedback reasons, endpoint + token fields, send
button with inline SSE status, and local send history. The send path is
server-authoritative — the server builds the outgoing report from the local
SQLite store and sanitizes it; the client supplies only the destination and
reason edits. The receiver additionally gained owner-only read endpoints
(GET /api/reports, GET /api/reports/{key}) gated by OWNER_TOKEN so the tool
owner can read remote reports while every user can still submit.)

### MCP data-quality defects

- Automatic events use synthetic session `mcp` rather than actual target.
- All calls use `ExploreCode` intent.
- Follow-up/retry/recovery transitions are never updated.
- Unknown feedback kinds are silently coerced.
- Blank tool names can throw instead of returning a domain error.
- Clean tools can produce meaningless remediation recommendations.
- Default reports hide previous-version data.

### Receiver defects

- TypeScript sanitizer tests use `tsx`, but `tsx` is not declared, so `npm test`
  cannot run from the checked-in dependencies.
- Payload cap trusts `Content-Length`; chunked oversized bodies bypass the
  application-level limit.
- No Worker HTTP/R2/auth integration test.
- No F#/TypeScript sanitizer parity corpus.

### Editor UX absent

No native friction submit/report action exists in VS Code, Visual Studio, or
Neovim. Each needs a discoverable action that either opens the common dashboard
review form with client context or uses the same safe local contract.

## Cross-Client Recovery Requirements

Each applicable client must prove:

- stream drop shows reconnecting within 2 seconds;
- successful replacement connection precedes “connected” UI;
- daemon restart restores full authoritative snapshot within 5 seconds;
- worker death marks in-flight work interrupted/stale, never successful;
- duplicate events are idempotent;
- older generations are rejected;
- client restart restores daemon truth rather than empty state;
- two clients viewing different sessions never cross-contaminate.

## Accessibility Requirements

- Every interactive dashboard control has accessible name/role.
- Required journeys are keyboard-completable.
- Escape closes overlays and restores focus.
- Success/failure/reconnect states are textual/live-announced, not color-only.
- Token contrast meets 4.5:1 for normal text and 3:1 for large/graphical state.
- Dashboard has no page overflow at 375x812 or 200% zoom.
- VS Code/Visual Studio actions expose labels and platform status feedback.
- Neovim actions expose commands and textual results.

## Performance Budgets

Measure release builds with at least 3 warmups and 20 samples; gate p95:

- File write to hot-reload event: <= 750 ms p95, <= 2 s max.
- File write to changed browser DOM: <= 1.5 s p95, <= 3 s max.
- Last keystroke to one trivial affected-test result: <= 1 s p95.
- Saved compiled-file change to fresh 20-test result set: <= 2 s p95.
- 200-result SSE batch to client render: <= 50 ms p95 per client.
- Health recovery to synchronized client model: <= 5 s p95.
- Local friction write + updated summary: <= 50 ms p95.
- 100 reconnects: subscriber count returns to baseline; <10 MiB attributable
  managed-memory growth.

Fix `scripts/check-benchmarks.fsx` to fail closed when reports or required
benchmarks are absent.

## Ordered Execution Plan

### Phase 1 — Fail-closed quality contract

1. Finish/type-check the matrix validator.
2. Development gate validates complete ownership and non-expired deferrals.
3. Release gate rejects any deferral.
4. Required test filters must match at least one test and may not be pending.
5. Commit matrix/gate only after SageFs tests prove both pass/fail semantics.

### Phase 2 — Shared P0 hot reload

- [x] Write real save-driven running-app RED test.
- [x] Fix captured-handler/method replacement propagation.
- [x] Add compile-error/repair journey.
- [x] Carry session ID + generation through every hot-reload event.
- [x] Add two-session isolation and stale-event tests.

### Phase 3 — Shared P0 live testing

- [x] Define authoritative replacement snapshot.
- [x] Emit zero-test completion live and on replay.
- [x] Replace, rather than merge, discovery generations.
- [x] Make every event strictly session-scoped.
- [x] Return truthful command failures.
- [x] Dispose/recreate watchers across disable/restart.

### Phase 4 — Shared P0 friction safety

- [x] Fix unstarted outer failure persistence.
- [x] Add F# sanitizer properties and shared parity corpus.
- [x] Enforce sanitizer at daemon send boundary.
- [x] Restrict/authenticate destination path.
- [x] Add pending/sent receipt lifecycle and retry semantics.
- [x] Add receiver dependency and HTTP/R2/auth/body-size tests.

### Phase 5 — Dashboard complete journeys

- [ ] Hot-reload controls + save + changed app + failure/recovery Playwright.
- [ ] Live-testing enable/discover/zero/run/pass/fail/stale/source/reconnect.
- [x] Friction review/send/history/retry UX.
- [ ] Keyboard/accessibility/viewport tests.

(2026-09-02 session evidence: the dashboard hot-reload CONTROL journey was
run live via Playwright against the installed daemon + a WebLive fixture
session — Watch All flipped the card from "Hot Reload: OFF / 0 of 3 files
watched" to "Hot Reload: ON — 3 of 3 files", Unwatch All returned it to
OFF, zero page errors. The full save→changed-app journey lives at the
worker level in `WebAppHotReloadVerificationTests` (save-driven +
compile-error/repair, both `[Integration]` and green). Live-testing
enable/disable round-trip verified in the dashboard (OFF → Enable → ON
"Discovering tests…" → Disable → OFF, zero page errors), exercising the
watcher register/dispose effects end-to-end. Watcher lifecycle gaps closed:
disable now disposes every session claim (commit 8047a1f) and a stale-event
epoch guard drops reloads from disposed watcher generations (commit b05c5f6),
both with unit tests. The remaining Phase 5 rows need the committed
Playwright harness runnable — `playwright.config.ts` is added (targets the
local daemon dashboard on 37750), but `npm install` of @playwright/test is
currently broken in this environment (npm resolves a stale empty tree), so
live journeys are run via the Python playwright until the npm issue is
resolved.)

### Phase 6 — VS Code

- [x] Fix reconnect truth and snapshot replacement.
- [x] Sweep stale tests by discovery generation (commit bb1c1ce — fold
      replaces on complete discovery from a newer generation, emits
      TestsRemoved, adapter deletes stale TestItems).
- [x] Sweep stale coverage views by generation (commit 9f6e313 —
      coverage_view payload carries the run Generation; client merges
      per (file, generation), newer gen replaces the file's views so
      renamed/deleted symbols' stale CodeLenses are swept, older-gen
      stragglers dropped).
- [x] Strict session filtering (untagged events rejected when filtered).
- [ ] Install current VSIX in isolated extension host.
- [ ] Run all three real journeys plus recovery/accessibility.

### Phase 7 — Visual Studio

- [x] Fix active/document session selection.
- [x] Stop swallowing HTTP failures.
- [x] Parse authoritative activation/run/snapshot state (commit 500cfa2 —
      TestSummary carries DiscoveryState + DiscoveryGeneration; the fold
      derives Enabled from DiscoveryState so the tool window/status
      reconcile with the server instead of optimistic-local toggles).
- [x] Add strict session filter and replacement discovery (commit 500cfa2 —
      TestsDiscovered carries (tests, isComplete, generation); complete
      newer-generation discoveries replace, emitting TestsRemoved; partial
      batches keep merge semantics).
- [ ] Add Experimental Instance UI automation on Windows.

### Phase 8 — Neovim

- [ ] Pin canonical repo/commit in matrix.
- [ ] Scope every event and reject stale generations.
- [ ] Consume aggregate coverage view.
- [ ] Re-enable real-daemon E2E in CI.
- [ ] Add friction action and all three journeys.

### Phase 9 — Release gate

1. Turn each matrix row Verified only after same-SHA evidence passes.
2. Add nightly recovery/accessibility/performance jobs.
3. Block package/extension publication unless release matrix has zero deferrals.
4. Only then describe the three capabilities as complete across maintained
   clients.

## Immediate Resume Point

1. Inspect current uncommitted diff.
2. Stop the running SageFs daemon if its DLL lock prevents `.fsproj` rebuild.
3. Build once, restart SageFs, and verify `DefinitionOfDoneTests` is loaded.
4. Run development validator: expect zero errors.
5. Run release validator: expect twelve blockers.
6. Correct any F# indentation/type errors in `SageFs.Tests/Program.fs`.
7. Add the new matrix/test files deliberately; do not add `.playwright-mcp/`,
   `.slopwatch/`, or this handoff document.
8. Commit the fail-closed release gate before beginning Phase 2.

## Known Tooling Friction

- SageFs hard reset can fail due to its own DLL locks; stop daemon, final-build,
  restart, then return to SageFs REPL verification.
- A SageTech SageFs session faulted because coverage instrumentation quarantined
  `Mono.Cecil.dll`, then `SageFs.Core` could not load it.
- Do not interpret either tooling failure as application-test failure.

### Mono.Cecil worker poisoning root cause and active fix

Fresh SageFs and SageTech sessions began failing before FSI startup because
`WorkerMain.run` physically moved same-named dependencies from the shared host
directory into `_sagefs_quarantine`. Mono.Cecil is not dashboard-only: SageFs.Core
needs it during coverage instrumentation before the later project assembly
resolver exists. A SageTech session moved the Cecil family, permanently starving
subsequent SageFs sessions until rebuild.

There is no package mismatch: requested and available assembly identity is
Mono.Cecil 0.11.6.0.

Verified uncommitted fix:

- `WorkerMain.shouldQuarantineAssembly` protects the complete Mono.Cecil family
  plus FSharp.Core and FSharp.SystemTextJson.
- `WorkerMainTests` proves Cecil remains and unrelated Falco collisions remain
  quarantinable.
- Rebuilding restored the already-poisoned host directory.
- A fresh SageFs test session reached Ready and passed WorkerMain tests.
- A fresh SageTech test session reached Ready and evaluated `1 + 1` to `2`.

## Untracked Files To Preserve/Ignore

SageFs:

- `.playwright-mcp/`
- `.slopwatch/`
- `QUALITY_GAP_CLOSURE_PLAN.md` (this handoff)

SageTech local screenshots:

- `sagefs-1024-full.png`
- `sagefs-ipadpro-1024.png`
- `sagefs-ipadpro-fixed.png`
- `sagefs-mobile-390.png`

Do not commit these unless the user explicitly requests it.
