# SageFs Release Blockers — Full Handoff

Status: **active handoff document — intentionally untracked (do not commit)**

Created: 2026-09-03 · Repo: `C:\Code\Repos\SageFs` · Branch: `master` @ `0cfec9a`

---

## 1. TL;DR

A push of 59 commits to `origin/master` (merge `0cfec9a`, including `f9e4aeb Update Readme.md (#130)`)
passed the **main build** CI run green (`33776759095`), but the **publish release** run
(`33778022860`) was **blocked** by the repo's own Definition of Done gate:

```
Release blocked by HR-DASH-E2E (issue #127, expires 2026-10-01)
Process completed with exit code 1.
```

The gate fails while **any** of the **12 real-client E2E journeys** in
`quality/definition-of-done.json` are `deferred`. All 12 are deferred (issues #127–129, all
expire 2026-10-01). **No NuGet package shipped, no GitHub Release was created, no version tag
exists.** The global tool update (`dotnet tool update sagefs --global`) is still pending and
cannot happen until the gate passes and NuGet receives `0.6.412`.

Closing all 12 rows requires **executable, user-visible, same-SHA journey evidence per client**
(dashboard, VS Code, Visual Studio, Neovim) — the plan explicitly forbids green builds,
handler existence, parser fixtures, or endpoint-name checks as proof. Today, **none of the 12
rows has such evidence**; several rows also require **new product code** (a friction action
does not exist in VS Code, Visual Studio, or Neovim; VS has no UI automation; the nvim E2E is
CI-disabled).

---

## 2. Current repository state

| Fact | Value |
|---|---|
| HEAD | `0cfec9a` (merge of `f22f069` local + `f9e4aeb` remote readme #130) |
| Pushed range since last release state `51345ff` | 59 commits (`51345ff..0cfec9a`) |
| Version (`Directory.Build.props`) | **0.6.412** (matches `sagefs-vscode/package.json` — release-artifacts job verified alignment) |
| Target framework | **net10.0** repo-wide (see §6.4 — do NOT bump to net11) |
| main build CI run | `33776759095` — **success** (all jobs incl. release-artifacts) |
| publish CI run | `33778022860` — **failed** at "Enforce release Definition of Done" |
| Installed global tool | `sagefs` **0.6.356** (stale) |
| Fresh local Release build | `SageFs\bin\Release\net10.0\SageFs.exe` → version **0.6.412.0**, 0 warnings |

Untracked files present (preserve, never stage): `QUALITY_GAP_CLOSURE_PLAN.md`,
`sagefs-roast.md`, `docs/internal/ROAST_PROGRESS.md`, `.playwright-mcp/`, `.slopwatch/`,
and this file.

---

## 3. The release gate — exactly how it works

### 3.1 Files involved

- `quality/definition-of-done.json` — the matrix. `schemaVersion: 1`; statuses
  `verified | deferred | not-applicable`. 12 rows = 3 capabilities × 4 clients, all
  currently `deferred`, all with `issue` (127/128/129) and `expires: 2026-10-01`.
- `.github/workflows/publish.yml` step **"Enforce release Definition of Done"** (added by
  commit `4413211 test(quality): block releases on deferred client journeys`): dependency-free
  PowerShell reads the JSON, and if any row has `status == 'deferred'`, writes one
  `Write-Error "Release blocked by <id> (issue #<n>, expires <date>)"` per row and throws —
  **fail closed**.
- `SageFs.Tests/DefinitionOfDoneTests.fs` — Expecto validator `validateMatrix
  (releaseReady: bool) (today: DateOnly) (json: string)` used by two **default-run** tests
  (no `[Integration]` tag, so they run in the normal suite and in CI's `--summary` run).
- `SageFs.Tests/Program.fs` — `--release-readiness` flag: exits 0 only when
  `validateMatrix true` yields no errors; used nowhere in CI today (the publish step is the
  standalone PowerShell check).

### 3.2 Validator semantics (what makes the gate pass/fail structurally)

For every row it checks:

- **`verified`** → requires non-empty `evidence` string, else error.
- **`deferred`** → requires a numeric `issue > 0` and an unexpired `expires` date; and when
  `releaseReady = true` → error `"<id> blocks release readiness"`.
- **`not-applicable`** → requires a `reason` string.
- The 3×4 (capability × client) combination set must be complete — every capability must have
  a row for every one of `dashboard, vscode, visualstudio, neovim` (rows may not be deleted;
  they can only become `verified` or `not-applicable` with a reason).
- No duplicate or blank ids; unknown statuses are errors.

### 3.3 ⚠️ Gotcha — a test inverts when the matrix is closed

`DefinitionOfDoneTests.fs` second test ("release readiness fails while any obligation is
deferred") **asserts the current all-deferred matrix produces "blocks release readiness"
errors**. The moment every row is `verified`, that test **fails** (no such errors exist) and
the whole default suite goes red — which also fails CI. Closing the matrix therefore requires
updating that test in the same commit (e.g. assert release-readiness passes when all rows are
verified, or delete the now-obsolete assertion). Do not forget this or the follow-up CI run
will be red for a reason unrelated to the journeys.

### 3.4 What "verified" means (doctrine, from `QUALITY_GAP_CLOSURE_PLAN.md`)

A matrix cell is **Verified only when the relevant executable evidence ran against the same
source revision and inspected user-visible behavior.**

Allowed evidence layers: (1) pure invariant/property test, (2) wire/protocol contract test,
(3) client adapter/parser/state test, (4) **real client E2E**, (5) failure/reconnect/recovery
E2E, (6) accessibility acceptance, (7) performance budget.

**Forbidden shortcuts** (each is explicitly listed in the plan):

- build success as behavior proof;
- endpoint-name/source-text checks as wire proof;
- direct mutable assignment as file-save hot-reload proof;
- parser fixtures as IDE/editor UX proof;
- pending/skipped required tests;
- tests matching zero cases;
- running E2E against a previously installed client rather than the artifact built from the
  current SHA;
- declaring all clients complete based on one dashboard flow.

The `evidence` strings in the matrix currently name aspirational artifacts
(`DashboardBrowserTests`, `VscodeExtensionTests`, `"Dashboard friction Playwright journey"`,
`"VS Code extension-host E2E"`, `"Visual Studio experimental instance"`, `"sagefs.nvim
real-daemon E2E"`). The audits in §5 show several of those named artifacts either do not
exercise the named journey or never run. When flipping a row, the `evidence` field must name
**actual executable artifacts that ran and passed** against the current SHA (file/test filter,
and for nvim: pinned external repo commit), not aspirations.

---

## 4. The 12 rows and what each requires

Cross-client recovery requirements every applicable journey must prove (plan §"Cross-Client
Recovery Requirements"): stream drop shows reconnecting ≤ 2 s; successful replacement
connection precedes "connected" UI; daemon restart restores full authoritative snapshot ≤ 5 s;
worker death marks in-flight work interrupted/stale (never successful); duplicate events
idempotent; older generations rejected; client restart restores daemon truth; two clients on
different sessions never cross-contaminate.

Accessibility requirements (dashboard journeys): every interactive control has accessible
name/role; journeys keyboard-completable; Escape closes overlays and restores focus;
success/failure/reconnect states textual/live-announced (not color-only); contrast 4.5:1
(normal) / 3:1 (large); no page overflow at 375×812 or 200% zoom; VS Code/VS actions expose
labels + platform status feedback; Neovim actions expose commands + textual results.

Performance budgets (measure on release builds, gate p95; see plan for exact numbers):
file-write→hot-reload event ≤ 750 ms p95 / ≤ 2 s max; file-write→changed browser DOM ≤ 1.5 s
p95; keystroke→trivial affected-test result ≤ 1 s p95; saved change→fresh 20-test result set
≤ 2 s p95; 200-result SSE batch→client render ≤ 50 ms p95; health recovery→synchronized client
model ≤ 5 s p95; local friction write+summary ≤ 50 ms p95; 100 reconnects → subscriber count
baseline, < 10 MiB managed-memory growth. (These belong to the plan's layer list; fold
realistic assertions into the journeys rather than treating the budgets as a separate gate
unless a row is marked with that evidence layer.)

### 4.1 Hot reload rows (issue #127)

Required journey (shared shape): start a real WebLive app through a SageFs session; watch the
actual source file; request a route and record value A; **edit the file on disk** to value B;
observe `Compiling → Reload` through the real worker path; request the same running process
**without restart**; require value B. Failure/recovery: save invalid F# → user-visible mapped
diagnostic while the running app retains last valid behavior; correct the file → diagnostic
clears and new behavior appears without session recreation. Isolation: two sessions never
cross-contaminate; stale events from disposed watcher generations are rejected.

| Row | Client | Current evidence gap |
|---|---|---|
| HR-DASH-E2E | Dashboard | Save→changed-app proven at worker/HTTP level only (`WebAppHotReloadVerificationTests`), **no browser journey**; reconnect-restores-authoritative-state has **no coverage anywhere**; `HotReloadBrowserTests` is a permanently-skipped `ptestList` video demo |
| HR-VSC-E2E | VS Code | **No extension-host journey exists**; existing extension tests never save a file or observe a reload; `VscodeExtensionTests` (the named evidence) only asserts activation/status text |
| HR-VS-E2E | Visual Studio | **No Experimental Instance / UI automation exists**; only pure unit tests of client routing (`BufferChangeRequestTests`) |
| HR-NVIM-E2E | Neovim | E2E suite exists in the nvim repo but is **CI-disabled**, and `e2e_hotreload_spec.lua` only proves "daemon survived," not changed behavior |

### 4.2 Live-testing rows (issue #128)

Required journey (shared shape): enable live testing; observe discovery (`Discovering` →
`ReadyZeroTests` or `ReadyWithTests`) with monotonic DiscoveryGeneration; run tests; see
pass/fail/stale results; renamed/deleted tests **swept** on complete newer-generation
discovery; strict session scoping; reconnect clears stale state then restores a complete
authoritative snapshot; source-location navigation; zero-test discovery observable
(never conflated with "no state").

| Row | Client | Current evidence gap |
|---|---|---|
| LT-DASH-E2E | Dashboard | No browser journey; only rendered-HTML snapshot tests (`DashboardSnapshotTests`) and engine unit tests |
| LT-VSC-E2E | VS Code | Fold/sweep logic unit-tested (`VscLiveTestStateTests` default-run; `.mjs` golden parser tests), but **no extension-host journey** drives enable/run/observe in the real Test Explorer; reconnect-truth closure untested |
| LT-VS-E2E | Visual Studio | Extensive pure unit coverage (`LiveTestStateTests` replacement sweep, `LiveTestingParserTests` on real SSE fixtures), but no command executed in a real VS tool window |
| LT-NVIM-E2E | Neovim | `e2e_testing_spec.lua` exists (enable/disable/run/policy + SSE) but is CI-disabled; plugin **never sweeps removed tests** (`handle_tests_discovered`/`handle_results_batch` merge unconditionally — no older-generation rejection, no complete-discovery replacement) |

### 4.3 Friction rows (issue #129)

Required journey (shared shape): a discoverable friction/report action in the client; review
raw-local context vs sanitized outbound fields; edit an explicit short reason; explicit
opt-in to send; see pending/success/rejected/timeout states; reopen to see local send
history/pending retry. Server-authoritative send path already exists (daemon builds the
outgoing report from local SQLite and sanitizes; client supplies only destination + reason
edits) — the client actions do not.

| Row | Client | Current evidence gap |
|---|---|---|
| FR-DASH-E2E | Dashboard | **Panel UX exists** (`DashboardFragments.fs renderFrictionPanel`, wired in `Dashboard.fs`), but **no test of any kind** (unit, HTTP, or browser) exercises the send handler (`Dashboard.fs` send path) or the panel journey; only pure view-model tests (`FrictionReviewViewTests`) |
| FR-VSC-E2E | VS Code | **No friction/report command exists in the extension** (checked: no command in `package.json` contributes.commands, no source hits). Must be **built**, then journeyed |
| FR-VS-E2E | Visual Studio | **No friction command exists** in the extension (checked across `sagefs-vs`). Must be **built**, then journeyed |
| FR-NVIM-E2E | Neovim | **No friction action exists** in the plugin (zero hits in `lua/` or `spec/`). Must be **built**, then journeyed |

---

## 5. Per-client evidence inventory (audited 2026-09-03)

### 5.1 Dashboard

**Existing executable evidence**

- `SageFs.Tests/WebAppHotReloadVerificationTests.fs` — two `[Integration]` tests
  ("real file save hot-reloads a running module-declared app (save-driven, no restart)",
  "compile-error save keeps last valid behavior and repair hot-reloads it"): spawns a real
  `SageFs.Host` worker, drives a WebLive session, `POST /hotreload/watch-all`, writes fixture
  files on disk, reads the SSE lifecycle (`GET /__sagefs__/reload`, `Compiling → Reload`),
  and asserts the **same running process** serves value B; the broken-save path asserts a
  `"type":"failed"` event with diagnostics, app keeps last valid, repair → `reload` → value B.
  This is the closest thing to HR proof that exists — but it is worker/HTTP level, no browser,
  and **never runs in CI** (`[Integration]`-excluded; no CI passes `--all/--integration`).
- `SageFs.Tests/DashboardBrowserTests.fs` — 21 Playwright cases (Microsoft.Playwright .NET)
  against the live dashboard on 37750: page loads, panels, eval journeys, keyboard help,
  session info, banner hidden/visible, SSE-blocked reconnect polling, two-tab test. All
  `[Integration]`-named → excluded from default run; never run in CI. Several are
  **misleading** (see §5.5).
- `tests/` TypeScript Playwright specs (10 files: `tests/dashboard/page-structure.spec.ts`,
  `tests/code-evaluation/*.spec.ts`, connection-status, keyboard-shortcuts,
  session-management, empty `tests/seed.spec.ts`) — plain `test()`, config
  `playwright.config.ts` targets `http://127.0.0.1:37750`, workers 1. Root `package.json`
  declares `@playwright/test ^1.58.2`. **No hot-reload/live-testing/friction spec exists.**
  **Nothing in CI runs them** (no `npx playwright test` / `npm test` step in any workflow).
- Non-browser default-run evidence: `DashboardSnapshotTests.fs` (rendered HTML: live-testing
  panel OFF/ON, pass/fail colors, empty discovery), `FrictionReviewViewTests.fs` (pure
  view-model: sanitized review build, edits, history), `FrictionSendSafetyTests.fs` (endpoint
  allow-list), friction durability/SQLite tests, large `LiveTesting*` engine test family,
  `HotReloadEndpointTests.fs` (mock-server endpoint contract).

**What's missing (in order of importance)**

1. FR-DASH-E2E: **no test exercises the friction send handler or the panel journey at any
   level** (grep for `friction/send`, `createFrictionSendHandler`, `FrictionSendStatus`,
   `renderFrictionPanel` in `SageFs.Tests` returns zero). Add: an HTTP/unit test of the send
   path (pending → sent receipt only after local record success; rejected/timeout) plus a
   Playwright journey (open panel → raw vs sanitized → edit reason → opt-in send → status →
   reopen history). Panel DOM lives in `DashboardFragments.fs` (~lines 1491–1577), wiring in
   `Dashboard.fs` (~429–445, send handler ~1107–1199).
2. LT-DASH-E2E browser journey: enable live testing in the page → Discover state → zero-tests
   vs tests → run → pass/fail/stale rendered → source navigation → daemon restart + reconnect
   restoring snapshot. Today only rendered-HTML + engine tests exist.
3. HR-DASH-E2E browser journey: click Watch All/Unwatch All → watched-count UI; save a file in
   a watched project → observe the change in the **browser-displayed running app** (the demo
   shell trick in `HotReloadBrowserTests` is not this — it injects static HTML); compile-error
   state + recovery in the page; reconnect restores authoritative hot-reload state (nothing
   exists anywhere for this last one).
4. Accessibility/viewport evidence: no axe/a11y tests, no 200%-zoom test; the one 375×812
   mobile test never applies its viewport (see §5.5).

**Harness notes**: F# browser tests need `Microsoft.Playwright` browsers installed
(`npx playwright install chromium`). The TS harness needs a working `npm ci` (a prior plan
note records npm resolving a stale empty tree locally — may need clearing the npm cache or
using the .NET Playwright path). Chromium is not installed on CI runners unless a job installs
it.

### 5.2 VS Code

**Existing executable evidence**

- `sagefs-vscode` is **F# → Fable → esbuild** (no TS source; `compile` = `dotnet fable src
  --outDir fable-out && node esbuild.mjs` → `dist/Extension.js`).
- `sagefs-vscode/src/LiveTestingListener.golden-tests.mjs` — hand-rolled `vm` harness over the
  **compiled** Fable output; asserts parse functions: fallback-decision summary, results-batch
  with coverage, policy suppression, `ready_zero_tests` surfacing, old-server default. Runs via
  `node` (`package.json` script `test:golden`). Unit-level only.
- `sagefs-vscode/tests/*.fsx` (7 FSI Expecto scripts) — pure module contracts (port discovery,
  buffer routing, coverage store). **Not wired into npm or CI.** Note:
  `CoverageViewStoreContractTests.fsx` tests the **pre-generation** store shape — the
  generation-keyed coverage sweep (newer-gen replaces, older dropped) implemented in
  `LiveTestingListener.fs` (~448–467) and `CoverageViewCodeLensProvider.fs` (~34–51) has
  **no committed test**.
- `SageFs.Tests/VscLiveTestStateTests.fs` — default-run Expecto: partial discovery merges;
  complete newer-generation discovery **sweeps removed tests/results and emits TestsRemoved**;
  same-generation complete does not sweep. Solid unit coverage of the fold.
- `SageFs.Tests/VscodeExtensionTests.fs` — Playwright-over-CDP against a real VS Code
  (`VscodeFixture`: `--remote-debugging-port=9222 --user-data-dir=C:\temp\sagefs-vscode-test`,
  preset settings disabling trust/updates/telemetry, orphan kill, CDP poll). Smoke tests
  (extensions disabled): title/status bar/command palette/screenshot. Extension tests:
  workspace open, "extension activates with SageFs status" (status-bar text), output channel,
  `.fsproj` quick-open. **All 8 are `[Integration]`-prefixed (never in default/CI runs) and
  become `ptestCase` when Code.exe is absent.** CI never runs them. **None exercise hot reload,
  live testing, saves, diagnostics, or friction.** The extension tests also silently depend on
  the extension being pre-installed in the isolated profile — there is no install step, so a
  broken `dist/Extension.js` can coexist with green tests (stale by construction).
- `scripts/reinstall-vscode-ext.ps1` — compiles, `vsce package`s, and installs the VSIX into
  the **user's default profile** (not the isolated test profile) — a mismatch with the fixture.
- Worker-level HR proof exists in `WebAppHotReloadVerificationTests` (see §5.1) but the client
  there is a raw HTTP proxy, not the extension.

**What's missing**

1. FR-VSC-E2E: **build a friction/report command** in the extension (contributes.commands +
   handler) that opens the shared dashboard review form with client context or uses the same
   safe local contract, then journey it. Nothing exists today.
2. An **extension-host E2E harness**: no `--extensionDevelopmentPath`/`--extensionTestsPath`
   run, no `@vscode/test-electron` dependency, no `.vscode/launch.json`, no CI step launches an
   Extension Development Host. Recommended shape: extend `VscodeFixture` to install the VSIX
   built from the current SHA into the isolated profile (`code --install-extension <vsix>
   --user-data-dir ... --force`) before launching with `--disable-extensions` false; then drive
   real journeys over CDP: open the `TestWorkspace` fixture, enable live testing, watch
   zero→discovered→pass/fail in the Test Explorer DOM, save a source file and observe
   hot-reload diagnostics/reload state, kill+restart the daemon and assert reconnect truth
   (status bar "reconnecting…" → snapshot restored), assert stale sweep after deleting a test.
   A daemon must be running (spawn `SageFs.exe` current build with a fresh `SAGEFS_DATA_DIR`).
3. Tests for already-implemented-but-unproven paths: reconnect state-clearing closure
   (`LiveTestingListener.fs` ~480–495), coverage-view generation sweep, status-bar tooltip
   builder (`Extension.fs` ~581–628).
4. A CI job (windows) that runs the above; VS Code exists at
   `C:\Program Files\Microsoft VS Code\Code.exe` on this machine (fixture auto-detects it).

### 5.3 Visual Studio

**Existing executable evidence** (all unit-level; CI job `vs-extension` in `main.yml` runs
`dotnet test` on both projects — no VS instance, no VSIX deploy, no .runsettings):

- `sagefs-vs/SageFs.VisualStudio.Core.Tests` (F#, xUnit + FsUnit): `LiveTestStateTests`
  (authoritative fold: complete newer-generation discovery replaces + emits TestsRemoved;
  `Enabled` derived from server DiscoveryState), `LiveTestingParserTests` (real SSE fixture
  payloads from `SageFs.Tests/fixtures/LiveTesting/`), `LiveTestingFormatTests`,
  `TestTreeViewModelTests`, `GlyphProjectionTests`, `BufferChangeRequestTests` (session
  ownership resolution + fail-closed HTTP semantics), `DaemonManagerTests`,
  `DaemonTargetFinderTests`, `CompletionRequestTests`, `SageFsClientVersionTests` etc. All
  HTTP stubbed via mock handlers; none need a real VS or daemon.
- `sagefs-vs/SageFs.VisualStudio.Editor.Tests` (C#, xUnit, net472): MEF export/attribute
  reflection sweep, state trackers, glyphs, block detection — deliberately runnable without an
  IDE ("swallow VS-host exceptions when run outside VS").

**What's missing**

1. FR-VS-E2E: **no friction command exists** in the extension (checked: command surface in
   `sagefs-vs/.../Commands/*.cs` has hot-reload/live-testing/eval/session commands only).
   Must be built (same safe local contract as the dashboard), then journeyed.
2. HR/LT-VS-E2E: **no Experimental Instance UI automation exists anywhere** (no
   FlaUI/WinAppDriver/EnvDTE/vstest-hosted-IDE code; only README mentions of the experimental
   hive). Phase 7 open item: "Add Experimental Instance UI automation on Windows." Required:
   a Windows job that launches VS with `/RootSuffix Exp` (or installs the VSIX into an isolated
   hive), loads a solution/workspace, executes the real commands (hot-reload watch/save,
   live-testing enable/run), and asserts tool-window/status state. The net8.0 shell project
   (`SageFs.VisualStudio`) where the real Commands live has **zero direct test coverage** —
   its logic is only spec-mirrored by net472 tests.

### 5.4 Neovim (external repo — `C:\Code\Repos\sagefs.nvim.git`)

Canonical checkout: `C:\Code\Repos\sagefs.nvim.git` (git clone, HEAD `a6eb9808`, branch
master, plugin version 0.5.543). Second non-git copy: `C:\Code\Repos\sagefs-nvim` (identical
content). The SageFs repo never checks out or tests sagefs.nvim; the only cross-repo step is
publish.yml's "Sync sagefs.nvim version tag" (rewrites `lua/sagefs/version.lua` and force-tags
`v<version>`, needs `SAGEFS_NVIM_PAT`).

**Existing evidence** (in the nvim repo): ~40 lua modules under `lua/sagefs/`; busted specs
(~1100+) + `spec/nvim_harness.lua` + `spec/e2e/` (custom harness booting a real
`sagefs --supervised` daemon; 27 E2E specs: eval, sse, sessions, testing, hotreload,
completions; launcher `test_e2e.cmd`). CI (`.github/workflows/test.yml`) runs busted +
nvim_harness on ubuntu but **E2E is explicitly disabled**:

> `# E2E tests disabled — SageFs session routing API is still stabilizing.`
> `# Re-enable when SageFs exposes working_directory on all HTTP endpoints.`

(SageFs has since moved to session-scoped routing with one-session-per-project — the
stabilization may now be satisfied; verify the endpoints the harness uses.)

**Plugin-state gaps vs Phase 8** (all unchecked in the plan):

- Session scoping: **implemented** (`testing.session_matches` three-way filter; specs exist).
- Generation handling: **partial** — parses/stores RunGeneration, freshness, completion,
  reconnect-gen clearing; but `handle_tests_discovered`/`handle_results_batch`
  (`testing.lua` ~662–708) **merge unconditionally**: no older-generation rejection, no
  complete-discovery replacement sweep of removed tests. Must mirror the VS Code/VS fold
  semantics (`TestsRemoved` equivalent).
- Coverage view: `coverage_view` is classified + re-fired as a User autocmd only; **no
  aggregate per-(file,generation) consumption** exists.
- Friction action: **none** (zero matches in `lua/` or `spec/`).
- `e2e_hotreload_spec.lua` proves only daemon survival after a file modification — must become
  a real save→changed-running-app (and failure/recovery) assertion.
- Re-enable `spec/e2e/` in CI (against a SageFs daemon built from the current SageFs SHA).

**Closing nvim rows requires work in the external repo** (new commits there), then pinning the
canonical repo + commit in the matrix evidence field.

### 5.5 Stale / misleading tests to fix or delete (hygiene — do this before/while closing rows)

- `SageFs.Tests/HotReloadBrowserTests.fs` — the entire list is `ptestList "[Integration] Hot
  reload browser tests"` (line ~146): **can never run**. Its one test is a video-recording
  choreography demo over a hand-written `demoShell` (injects static HTML via `setPage` — it
  never observes a real app change in a browser). Delete or rewrite as a real journey; it is
  currently cited as evidence and is misleading.
- `DashboardBrowserTests.fs` "two browser tabs maintain independent sessions" (~428–460):
  admits in its own comments it can't perform a session switch; final assertion is a tautology
  (no action on page1). Rewrite to actually create two sessions and switch in one tab, or cut.
- `DashboardBrowserTests.fs` "dashboard switch does not dispatch SessionSwitched to Elm"
  (~404–426): never clicks a switch — only fetches `/api/daemon-info`. Misleading name.
- `DashboardBrowserTests.fs` "responsive layout on mobile viewport" (~225–234): the wrapper
  navigates **before** `SetViewportSizeAsync`, so the 375×812 viewport never applies.
- `DemoRecording.fs` hot-reload hero: GIF recorder with `printfn`-only "assertions", targets an
  external unversioned workspace, and waits for `apiVersion:1` while the current client
  contract is `apiVersion:2` — cannot reach a connected state on a current daemon.
- `VscodeExtensionTests.fs`: named as evidence for HR-VSC-E2E but cannot detect its regression
  (never saves a file; extension-under-test may be stale/pre-installed — no install step).
- Coverage-view generation sweep and reconnect-clearing in the VS Code extension: implemented,
  **unproven by any test**.
- TS `tests/seed.spec.ts` is an empty placeholder.

---

## 6. Environment & operational facts (for whoever runs the journeys)

### 6.1 Daemons / ports — current state on this machine

- Running daemon: **PID 12244**, `C:\Code\Repos\SageFs\SageFs\bin\Release\net11.0\SageFs.exe`,
  version **0.6.393.0** (daemon-info), started 2026-09-02 20:39, dashboard 37750, MCP 37749,
  2 sessions, apiVersion 2. **This is STALE**: it predates the net10 rollback and the 59 pushed
  commits. Evidence against it is not same-SHA evidence. It is a user-run process with real
  sessions — **ask before killing it**, and restart the daemon from the fresh build
  (`SageFs\bin\Release\net10.0\SageFs.exe`, 0.6.412) before running any journey.
- Also running: several `SageFs.Host` worker processes (9696, 22468, 30352, 46168).
- Installed global tool `sagefs` 0.6.356 also exists and may hold ports if started — verify
  which binary serves a port before trusting results (stale-tool trap).
- CLI: `SageFs stop` stops the running daemon; `SageFs status` reports daemon info; daemon is
  the default mode. `SageFs --supervised` runs under a watchdog.

### 6.2 Test-daemon operational rules (hard-won, repo doctrine)

- Never let test harnesses touch the user's real `~/.SageFs` state: the daemon honors a
  **`SAGEFS_DATA_DIR`** env override (fresh temp dir per spawned daemon). Always set it.
- One session per project dir is enforced (mailbox owner); duplicate sessions for one
  workingDirectory break `/exec` routing with "Multiple sessions match workingDirectory".
  Integration tests that create sessions should target small standalone sample projects, not
  `SageFs.Tests` itself. Existing fixture: `SageFs.Tests/fixtures/TestWorkspace/`
  (`TestWorkspace.fsproj`, `SampleTests.fs`, `Simple.fs`, `WithError.fs` — includes a
  deliberate compile-error file). There are also Falco/WebLive fixture helpers in
  `WebAppHotReloadVerificationTests` (`writeFixtureFile`, `watchAllFiles`, `readSseUntil`).
- Spawned workers: drain **both** stdout and stderr (redirect to files); undrained pipes
  deadlock children. Budget 120–180 s readiness on cold CI runners.
- Stale `SageFs*` processes from aborted runs hold ports and produce spurious
  `success=false` — sweep them (and confirm port listeners clear) before each suite run.
- Init scripts/app ports: never hard-code a fixed app port; pick a free port dynamically
  (bind TcpListener on 0, read assigned port, stop) — fixed ports make hard-reset restarts
  fail when the old worker still holds the port.
- Worker/HTTP session creation: `POST /api/sessions/create` with
  `{ workingDirectory, projects, workflow = "WebLive" }`; WebLive does NOT auto-start the app —
  the host discovers and evals `.SageFs/init.fsx` during warmup.
- `/eval` requires a `replyId` in the request body (omitting it yields a bare 500 that mimics a
  worker bug but is a client error).

### 6.3 Tooling available on this machine

- `gh` 2.69.0 authenticated as WillEhrendreich. Node v23.10.0, npm 10.9.2.
- `@playwright/test` **not installed** in node_modules (root package.json declares ^1.58.2).
  A prior plan note: `npm install` resolved a stale empty tree locally — may need
  `npm cache clean`/fresh install before the TS harness works; the F# Microsoft.Playwright path
  is an alternative (needs `npx playwright install chromium`).
- VS Code installed at `C:\Program Files\Microsoft VS Code\Code.exe` (fixture auto-detects).
- dotnet SDKs include 10.0.303 and 11.0.100-preview.7 (global.json pins the preview SDK; the
  repo **targets net10.0**).

### 6.4 TFM note — do not bump net11

`Directory.Build.props` targets **net10.0** repo-wide with an annotated gate: do not bump to
net11 until Harmony/MonoMod support CoreCLR 11 AND the full hot-reload verification suite
passes with no skips. The stale net11 daemon directory (`bin\Release\net11.0`) is leftover
from the pre-rollback era — current builds land in `bin\Release\net10.0`.

### 6.5 CI wiring facts

- `main.yml` "Test" step runs `dotnet run --no-build --project SageFs.Tests -- --summary`
  (Expecto exit 2 = no TTY, tolerated). Default run excludes `[Integration]`-named tests;
  **no workflow passes `--all`/`--integration`**, so every existing browser/worker journey is
  dead in CI today. No workflow runs `npx playwright test` / `npm test` for the dashboard
  specs. `main.yml` also has build/extensions/benchmarks/vs-extension/release-artifacts jobs;
  release-artifacts (windows) packs `SageFs`, downloads the VSIX artifacts, writes
  `release-manifest.json`, uploads `release-assets` (retention 1 day). The publish workflow
  auto-triggers on main-build success for master pushes (`workflow_run`) and enforces the DoD
  gate, marketplace-publishes VS Code (VSCE_PAT) + Open VSX (optional, continue-on-error),
  patches + publishes the VS VSIX (VsixPublisher.exe on the runner), **pushes SageFs nupkg to
  NuGet** (`--skip-duplicate`), generates release notes from conventional commits, creates the
  GitHub Release, and syncs the sagefs.nvim version tag.
- CI runners have no VS Code, no Chromium, no daemon, and no nvim by default — every new E2E
  job must provision its own (and install the forked MCP SDK prerequisites the other jobs
  already handle: checkout `WillEhrendreich/ModelContextProtocolSdk` to `mcp-sdk`, pack its
  three projects to `mcp-sdk-nupkg`).

---

## 7. Recommended execution order

Phase 0 — hygiene + harness readiness (small, unblocks everything):

1. Delete/rewrite the misleading tests in §5.5 (at minimum un-`ptest` or remove
   `HotReloadBrowserTests`; fix the viewport/tautology dashboard tests).
2. Get one dashboard journey runnable end-to-end locally on the current build: restart daemon
   from `bin\Release\net10.0` (ask user first), fresh `SAGEFS_DATA_DIR`, verify
   `WebAppHotReloadVerificationTests` passes under `--integration`, verify
   `DashboardBrowserTests` against the current daemon, and get either the TS specs or the F#
   Playwright suite executing cleanly.
3. Wire the first CI job for dashboard journeys (provision daemon + Chromium) and confirm the
   gate's `--release-readiness` flag semantics.

Phase 1 — Dashboard (all three rows closable inside this repo; closest to done):

1. FR-DASH-E2E first (smallest): add send-handler tests + Playwright panel journey.
2. LT-DASH-E2E: full browser journey incl. zero-tests, stale sweep, source navigation,
   reconnect-after-daemon-restart snapshot restore.
3. HR-DASH-E2E: real browser save→changed-app journey (reuse the
   `WebAppHotReloadVerificationTests` WebLive fixture pattern but assert in the browser),
   compile-error + recovery in the page, reconnect-restores-hot-reload-state.
4. Accessibility/viewport assertions folded into the journeys (or a dedicated a11y spec).

Phase 2 — VS Code (all three rows; two journeys + one new product feature):

1. Build FR-VSC friction command (contributes.commands + handler; safe local contract).
2. Extension-host harness: VSIX built from current SHA → install into the isolated profile →
   CDP journeys (HR: save→reload+diagnostics; LT: enable→discover→run→sweep→reconnect;
   FR: open action → review → send → history).
3. Add unit tests for the unproven implemented paths (§5.2.3). Windows CI job.

Phase 3 — Visual Studio (hardest infrastructure):

1. Build FR-VS friction command.
2. Experimental Instance UI automation on Windows (Phase 7): launch VS `/RootSuffix Exp` with
   the current VSIX, load a solution, execute real commands, assert tool-window state; wire a
   windows CI job (runner needs VS 2022 + workload; the publish job already locates
   `VsixPublisher.exe` under `C:\Program Files\Microsoft Visual Studio\2022\*`).

Phase 4 — Neovim (external repo, separate commits + pin):

1. In `sagefs.nvim.git`: implement generation sweep/rejection mirroring the VS fold semantics;
   consume aggregate coverage_view per (file,generation); add a friction action + safe send;
   strengthen `e2e_hotreload_spec.lua` to a real save→changed-behavior assertion; re-enable
   `spec/e2e/` in the nvim repo's CI (against a SageFs daemon built from the current SageFs
   SHA).
2. Pin the canonical repo URL + commit in the matrix evidence fields.

Phase 5 — Close the gate:

1. Flip each row to `verified` with an `evidence` string naming the executable artifact(s)
   that passed against the current SHA (file + test filter; for nvim: external repo + commit).
2. **Update the inverted `DefinitionOfDoneTests` test (§3.3) in the same commit.**
3. Commit (conventional message, e.g. `test(quality): verify real-client journeys ...`) →
   push → watch main build (all jobs incl. release-artifacts) → publish auto-runs → confirm
   "Push to NuGet" succeeded and the release notes/GitHub Release were created.

Phase 6 — Post-release (the originally requested outcome):

1. Verify `0.6.412` is visible on nuget.org (registration API
   `https://api.nuget.org/v3/registration5-gz-semver2/sagefs/index.json`, or the package
   page; treat flat-container index lag as expected; confirm the push log says "Your package
   was pushed").
2. `dotnet tool update sagefs --global` → confirm `sagefs --version` = 0.6.412.
3. Note: the user's running daemon is still 0.6.393 and will need a restart to the new tool —
   ask before killing it.

---

## 8. Definition of Done for this handoff ("perfect")

- All 12 matrix rows `verified`, each `evidence` naming an executable artifact that ran and
  passed against the current SageFs SHA (nvim rows pin the external repo commit), with no
  forbidden shortcuts (§3.4) and no `[Integration]`-only evidence that never runs in CI.
- Every journey's evidence is **executed by CI** (new or updated workflows), not just runnable
  locally.
- `DefinitionOfDoneTests` updated so the default suite is green with the closed matrix.
- Stale/misleading tests from §5.5 removed or rewritten; no `ptest*` evidence remains.
- `--release-readiness` exits 0 locally.
- Publish run green end-to-end: NuGet push of `SageFs 0.6.412` confirmed on nuget.org, GitHub
  Release v0.6.412 created with notes, version tags consistent.
- Global tool updated to 0.6.412 and verified (`sagefs --version`), daemon restarted onto the
  new build (with the user's OK).
- Working tree clean of scratch artifacts (this file and the other untracked docs stay
  untracked unless the user says otherwise).

---

## Appendix A — key files map

| Purpose | Path |
|---|---|
| Matrix | `quality/definition-of-done.json` |
| Validator tests | `SageFs.Tests/DefinitionOfDoneTests.fs` |
| Gate flag | `SageFs.Tests/Program.fs` (`--release-readiness`) |
| Publish gate step | `.github/workflows/publish.yml` ("Enforce release Definition of Done") |
| Master plan (authoritative doctrine) | `QUALITY_GAP_CLOSURE_PLAN.md` (untracked; phases 5–9 are the roadmap; §"Definition of Done Rules", §"Cross-Client Recovery Requirements", §"Accessibility Requirements", §"Performance Budgets") |
| Issue trackers | #127 hot-reload, #128 live-testing, #129 friction (all open) |
| Worker-level HR proof | `SageFs.Tests/WebAppHotReloadVerificationTests.fs` |
| Dashboard browser tests | `SageFs.Tests/DashboardBrowserTests.fs`; TS specs under `tests/`; `playwright.config.ts` |
| HR browser (broken) | `SageFs.Tests/HotReloadBrowserTests.fs` (ptestList video demo) |
| VS Code ext tests | `sagefs-vscode/src/LiveTestingListener.golden-tests.mjs`; `sagefs-vscode/tests/*.fsx`; `SageFs.Tests/VscLiveTestStateTests.fs`; `SageFs.Tests/VscodeExtensionTests.fs`; `scripts/reinstall-vscode-ext.ps1` |
| VS ext tests | `sagefs-vs/SageFs.VisualStudio.Core.Tests/`; `sagefs-vs/SageFs.VisualStudio.Editor.Tests/` |
| nvim plugin | `C:\Code\Repos\sagefs.nvim.git` (canonical); `C:\Code\Repos\sagefs-nvim` (copy); E2E at `spec/e2e/`, CI at `.github/workflows/test.yml` (E2E disabled) |
| Fixtures | `SageFs.Tests/fixtures/TestWorkspace/`; `SageFs.Tests/fixtures/LiveTesting/*.json` |
| Gate commit | `4413211 test(quality): block releases on deferred client journeys` |

## Appendix B — exact gate-failure log (for reference)

```
##[error]Write-Error: Release blocked by HR-DASH-E2E (issue #127, expires 2026-10-01)
##[error]Process completed with exit code 1.
```
(The step enumerates every deferred row; the first Write-Error shown is HR-DASH-E2E. All 12
rows are deferred, so all 12 appear.)
