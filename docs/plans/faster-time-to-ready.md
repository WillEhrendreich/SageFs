# Faster time to Ready

Roadmap item: `faster-time-to-ready` (Exploring, REPL). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S to measure, S to M for the first change |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: the plan segment excludes FSI creation. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

Create to Ready on a large solution takes measurably less, and nothing a user can observe about the ready session differs: the same names are in scope, the same opens fail with the same diagnostics, and "Ready" still means the namespaces are open. The first deliverable is not a speedup. It is a stage table that says where the time goes, so the speedup is aimed at a measured stage. The published cold-start numbers (5.5 s on `samples/counter`, 12.5 s on `SageFs.Simulation`, 12.3 s on `SageFs.Core`, `docs/repl-loop-benchmarks.md`) get a second table with the stage breakdown and a larger solution.

Outcome gate for any optimization that changes how names are brought into scope: a host-tier test that creates a session the old way and the new way on each fixture and compares the names in scope across the matrix {namespace, top-level module, `open type`, a library that shadows core operators, a name that fails to open, a name that opens only after another} x {cold plan, replay cache hit} x {opens as evals (control), the new mechanism}. Every cell must give the same resolved identifiers and the same `FailedOpens`. Speed is checked by the benchmark, recorded in the doc with the commit, and never asserted as a wall-clock threshold in a test.

## What exists today

Verified in the tree on master.

The path. `createFsiSession` (`SageFs.Core/AppState.fs:719`) starts one `Stopwatch` `sw` on line 720 and never restarts it. In order: build args, create the FSI session in the isolated host (`SessionKinds.Isolated` is how every session runs), evaluate the embedded `base.fsx` (line 776) and any startup files, resolve the replay plan (`resolveWarmupReplayPlan`, line 818: cache hit or `discoverWarmupReplayPlan`, line 447), open the names (`WarmUp.openWithRetryRichBatched 5 WarmUp.DefaultOpenBatchSize`, line 918, batch size 8), verify assemblies loaded, then two more evals that restore `Microsoft.FSharp.Core.Operators` and `ExtraTopLevelOperators` (line 993; they exist because libraries such as FSharpPlus shadow `min`, `max` and the async builder).

The phase numbers are mislabelled. `scanPhaseMs = sw.ElapsedMilliseconds` (line 833) is time since `createFsiSession` began, so `WarmupPhaseTiming.ScanSourceFilesMs` includes FSI session creation, `base.fsx` and the startup files. `assemblyPhaseMs` (line 836) is read a few lines later, so `ScanAssembliesMs` is about zero (the assembly scan runs inside `discoverWarmupReplayPlan`, inside the first number). `OpenNamespacesMs` is correct. `TotalMs` is the open mark, which excludes the post-open work. The log line "Opened %d/%d namespaces/modules in %dms" (line 925) prints the cumulative `sw`, not the opens phase. The dashboard shows these as "Scan source files" and "Open namespaces" (`SageFs/DashboardFragments.fs:3536`), `McpAdapter.fs:247` prints them, `SseEvent.fs:151` sends them. So "opening namespaces is 60 to 90 percent" in `docs/fsi warmup.md` and `docs/warmup-design-next.md` is a share of `createFsiSession`'s own time with FSI creation hidden in another phase, written before anything was measured (both pages say so). It says nothing about create to Ready.

Create to Ready has more in it. `scripts/machine-bench.fsx` (816 lines) already measures stage by stage on a throwaway daemon (own data dir and ports): `daemon_up`, `create_request`, `worker_boot`, `project_load` (MSBuild load, shadow copies), `host_prep` (a cold start builds the FSI host here, 7 to 11 s per `FsiHostBuild.fs`'s header), `fsi_session_create`, `warmup_after_session`, `ready_total`, then evals, hard reset, stop, and peak RSS per process. Options: `--sagefs`, `--runs`, `--cold-runs`, `--env K=V` (a daemon environment override, so a runtime switch is measurable), `--out FILE` (raw samples), `--label`. Its fixture is a small two-project solution it writes itself, so it cannot answer "a bigger solution" today. `MachineProfile.fs:181` documents that the `WorkerReady` stage also includes test discovery; I have not seen that time measured on its own.

The replay cache (`SageFs.Core/WarmupReplayCache.fs`) removes the scan and assembly reflection only. It stores the list of names, never the result of opening them.

ReadyToRun is partly done, and both warmup pages say it is not. `SageFs.Host/ReadyToRun.targets` (commit 17241280, 2026-10-04) compiles nine assemblies (FCS, FSharp.Core, Fantomas, SageFs.Core and others) with the SDK's crossgen2 pack into the daemon's `host/` directory on Release builds. Measured by that commit: median -0.6 s wall of about 5 s on one hot reload fixture, 10 pairs. It does not reach users of the published tool: the package keeps plain IL on purpose, because one payload serves every OS and CPU and a foreign R2R image throws `BadImageFormatException` (measured; `HostManifestTests` pins it). `warmup-design-next.md` says "I grepped every .fsproj and .props ... found nothing"; it missed the `.targets` file. Which assemblies the FSI host process loads is a different question: `FsiHostBuild` compiles `SageFs.FsiHost` with the project's own SDK against that SDK's FCS, so the FCS in the process that runs the opens may or may not be R2R already. Unknown.

`System.Reflection.Emit`-style dependencies for a generated assembly exist: `Mono.Cecil` is a Core reference (`SageFs.Core.fsproj:612`), used by `FsiHostBuild.fs`, `IsolatedFsiSession.fs` and `CoverageInstrumenter.fs`.

The proposal in `warmup-design-next.md` ("a script `namespace WarmupPrecompiled; open System; ...` compiled to a DLL, then `#r` it and `open WarmupPrecompiled`") cannot work as written. An `open` is a scoping directive that applies to the file containing it. An assembly exports no opens, so `open WarmupPrecompiled` would bring in nothing. The candidate that could work is the assembly-level `AutoOpen("Some.Namespace")` attribute (it exists in FSharp.Core for assemblies). Whether FSI honours it on a `#r`'d assembly, and what that costs, is untested here (experiment M4).

Supervision interacts. `WarmupSupervision.fs` and `WorkerStartup.fs` bound startup by silence: the inactivity timer resets on every `WARMUP_PROGRESS=` line. Opens report progress per chunk today. One giant batch is silent until it finishes. `SageFs.Simulation/WarmupSim.fs` models this guard.

## Design

Phase 0, make the numbers true. Replace the four-field `WarmupPhaseTiming` meaning with separately measured stages, additive on the wire:

```fsharp
type WarmupStageTimes =
  { FsiCreateMs: int64        // host process start to session created
    BaseAndStartupMs: int64   // base.fsx and startup files
    PlanMs: int64             // replay plan; PlanSource says hit or discovery
    PlanSource: PlanSource    // CacheHit | Discovered of PlanMiss
    OpensMs: int64
    OpenEvals: int            // every EvalInteractionNonThrowing call made for opens
    OpenRounds: int
    PostOpenMs: int64 }       // assembly verification and the two operator restores
```

`ScanSourceFilesMs` keeps its field name and takes the plan segment (so the dashboard row means what it says); the new fields ride beside it in `warmup-context`, in `SseEvent.fs`'s warmup payload and in the MCP text. A clock function is passed in so the pure builder is tested with fake marks. Clients: `sagefs-vscode` and `sagefs.nvim` do not read the phase fields (I grepped both trees for `ScanSourceFilesMs` and `OpenNamespacesMs`: only SageFs, SageFs.Core and tests). `apiVersion` stays 4; `docs/sse-events.md` documents the added fields.

Phase 1, the harness. Extend `scripts/machine-bench.fsx` (it stays thin glue; the logic is already in the product) with `--fixture-dir <path>` to load a real solution instead of the generated one, and with rows read from `GET /api/sessions/{sid}/warmup-context` instead of scraping log lines: `warmup_fsi_create`, `warmup_base`, `warmup_plan_cached`, `warmup_plan_miss`, `warmup_opens`, `warmup_open_evals`, `warmup_post_open`, plus `discovery` for test discovery inside Ready.

Phase 2, one or more of these, chosen by the measurements and not before. Each is a separate commit series.

| Option | What changes | Wins when | Risk |
|---|---|---|---|
| A. Fewer, bigger evals | batch size follows the measured per-interaction cost; on failure bisect to the bad name instead of falling to singles; progress still emitted per chunk | per-interaction overhead dominates the opens | a bigger chunk widens the silent gap for the inactivity guard; failure attribution must stay per name |
| B. Overlap | start `resolveWarmupReplayPlan` while the FSI host is being created (the plan does not need FSI) | plan on a cache miss is seconds on a big solution | none for semantics; only scheduling |
| C. Assembly `AutoOpen` | write `.SageFs/warmup-opens.dll` with Cecil (`[<assembly: AutoOpen "X">]` per namespace or module), `#r` it, drop the per-name evals; keep `open type` entries and the two operator restores as evals | the cost is per interaction and FSI honours the attribute | shadowing depends on open order, which must be preserved in attribute order; a failing name fails differently (no per-name diagnostic); stale DLL under a changed fingerprint |
| D. R2R for the published tool | on first run of a new tool version, crossgen the daemon's `host/` into the user cache with the same keying as `ReadyToRun.targets` | JIT time is a large share and the FSI host's FCS is IL | needs the crossgen2 pack on the user's machine (a download); fail-open path must leave IL; a new first-run cost |
| E. Cut work | none planned. Opening in the background after Ready is rejected: Ready must mean ready | | |

Wire and client impact beyond Phase 0: none for A, B, C, D. Token cost: no MCP tool added.

## Tests

RED first.
1. `WarmupContextTests.fs`: "the plan segment excludes FSI creation": call the new builder with marks (created 4000 ms, base 4100, plan 4200, opens 9000, post 9100) and expect `FsiCreateMs = 4000`, `PlanMs = 100`, `OpensMs = 4800`, `ScanSourceFilesMs = PlanMs`. It fails today because `completeWarmup` has no such inputs.
2. Same file, property: for any non-decreasing marks, every stage is non-negative and the stages sum to the last mark minus the first.
3. `WarmupBatchedOpenTests.fs`: "OpenEvals counts batch and single evals" (extends the traced result from the sibling plan `sessions-learn-the-load-order`; if that has not landed, add the counter here and let it rebase).
4. Phase 2, per option, RED before code. A: "a failing name inside a chunk of 32 costs at most ceil(log2 32) + 1 extra evals and is reported with its own diagnostics". B: "plan resolution starts before the FSI create completes" asserted on an event order log from fakes. C: scope-equivalence cells of the outcome gate; "an AutoOpen DLL with a missing namespace reports the name that failed".
5. Property tests over a pure `OpenBatching.plan` (names, per-interaction cost model in, chunk list out): chunks are a partition of the names in order; chunk count never exceeds `ceil(N / minChunk)`; progress points between chunks never leave a silent gap larger than the tier's allowance.

Simulation (`SageFs.Simulation/OpenBatchSim.fs` and its Generators and Invariants, shaped like `WarmupSim.fs`, listed in `SageFs.Simulation.fsproj`). Pool: 1..400 names, each with a cost, a set of names it needs opened first, some that always fail. Fake FSI: per-eval overhead `a` plus per-name cost `b`. Ops (seeded data): Open with batch policy P, tier change (slow machine), a worker stall mid-chunk, a failing name. Invariants: (B1) the opened set and the failed set (with each failing name's own diagnostic) equal the one-eval-per-name control for every policy; (B2) the silent gap between progress lines never exceeds the inactivity allowance of the `MachineTier` in force, so `WarmupSupervision` cannot time out a progressing worker; (B3) evals made never exceed `ceil(N/chunk) + failures x (log2 chunk + 1)`; (B4) result order is deterministic for a seed. Twin: a policy of one chunk for everything must violate B2 on a slow tier, and a bisect that merges diagnostics must violate B1, so each invariant is shown to bite. Faults: stall, duplicate progress line, drop a progress line.

Tier placement. Pure tests and the sim: default suite on net11 and net10. The scope-equivalence gate: host tier (`Integration.hostList`), fixtures under `SageFs.Tests/fixtures/` (there are `FSharpCoreIdentityFixture` and others to model on). Benchmarks are not tests: they run on the desktop before a release, results are recorded in `docs/repl-loop-benchmarks.md` with the commit and the machine, CI only checks the doc's freshness against its source data if a freshness check is added (decide below).

No wall-clock waits in tests: host cases wait for the session's Ready transition through the existing ready-wait path; the sim has no clock.

## Steps

1. RED tests 1 and 2, then `WarmupStageTimes` and the call-site changes in `AppState.fs` (stopwatch marks around create, base, plan, opens, post) and the cumulative-time log line fix. Owns `WarmupContext.fs`, `WarmUp.fs` (the record), the warmup region of `AppState.fs`, `SseEvent.fs`, `McpAdapter.fs`, `DashboardFragments.fs` (labels). Lands first; `sessions-learn-the-load-order` waits for it.
2. Update `docs/fsi warmup.md` and `docs/warmup-design-next.md` status notes: R2R partly done, opens-share claim retracted until M2, the `open WarmupPrecompiled` design cannot work as written. Docs only; parallel with 3.
3. `machine-bench.fsx`: `--fixture-dir`, rows from `warmup-context`. Owns only that script. Parallel with 2.
4. Run the experiments M1 to M7 below. Output: a results table in the PR and a short "where the time goes" section in `docs/repl-loop-benchmarks.md`.
5. Choose options with Will (Decisions). Then, per chosen option, a series of its own RED test, sim invariant, change, gate. B and A can run in parallel (different files: B in `AppState.fs`'s create region, A in `WarmUp.fs` and a new `OpenBatching.fs`). C is serial after A because it replaces the opener. D touches `SageFs.Host/ReadyToRun.targets`, a new first-run module and `HostManifest`; independent of A to C.

## Measure first

All runs use `machine-bench.fsx` on the desktop, with the daemon at normal priority and other agents idle, 3 cold and 5 warm runs, medians reported, raw samples saved with `--out`. Fixtures: `samples/counter`, `SageFs.Simulation`, `SageFs.Core`, and one solution large enough that you notice the wait (`~/Work/SageTech` is the candidate; the 86,000-line number in the doc is `SageFs.Core`).

M1. The stage table after step 1 and 3. Question: what fraction of create to Ready is `project_load`, `host_prep`, `fsi_session_create`, plan, opens, post-open and test discovery? If opens are under 40 percent of Ready on the large solution, stop optimizing opens and aim at the largest stage; the item's premise changes.

M2. Plan cost on a cache miss versus a hit (delete `.SageFs/warmup-replay-cache.json`). If a miss costs more than 1 s on the large solution, option B is worth doing first because it is free of semantic risk.

M3. Opens anatomy by batch size. Temporarily make the chunk size a local environment read (not committed) and run sizes 1, 8, 32 and all-in-one on the large solution; record interactions and opens ms. Fit `T = a x interactions + b x names`. If total time falls close to linearly with interaction count, per-interaction overhead dominates and option A is the cheap answer; if it stays flat, names dominate and A will not help.

M4. Does `AutoOpen` work, and does the DLL idea's control fail as predicted. In a SageFs session on `samples/counter` (REPL, no build of SageFs itself): with Cecil in an `.fsx` write two DLLs, one with `[<assembly: AutoOpen "System.IO">]` and one containing a source-compiled `namespace W` that opens `System.IO`. `#r` each and evaluate `File.Exists "x"` without an `open`. Expect the first to resolve if FSI honours the attribute and the second not to. Then time `#r` of an AutoOpen DLL naming all N names against the opens phase. If the first does not resolve, option C is dead and the page that proposes it gets corrected.

M5. JIT share. In the FSI host process read `System.Runtime.JitInfo.GetCompiledMethodCount(false)`, `GetCompiledILBytes(false)` and `GetCompilationTime(false)` before and after the opens. No product change is needed: create the session with auto-open disabled (`autoOpenNamespaces` false, the "Auto-open disabled" branch at `AppState.fs:920`; the setting is `AutoOpenNamespaces` in `DirectoryConfigTypes.fs:80`), read the counters with one eval (an eval runs in the FSI host process, where FCS lives), paste the opens as a single script, read the counters again. Repeat with `--env DOTNET_ReadyToRun=0` to see how much the existing R2R already buys the worker. Also check whether `FSharp.Compiler.Service.dll` in the FSI host's directory is a ReadyToRun image (read the PE header's managed native header). If JIT time is over half of the opens phase and FCS is IL, option D has a target; if FCS is already R2R, D is not worth its first-run cost.

M6. The floor. Evaluate `1+1;;` twenty times right after Ready and record the first and the median of the rest. That is `a` for M3's model.

M7. Test discovery inside Ready. With live testing off versus on, measure `ready_total`. If discovery is a large slice, it becomes its own candidate (overlap it with the opens).

## Risks and dependencies

- The 12 s on `SageFs.Core` may be mostly `project_load` and `host_prep`, in which case this whole plan shrinks to Phase 0 and Option B. That is a good outcome, found cheaply.
- Machine noise. The bench numbers in the doc were taken on a loaded machine on purpose; compare only paired runs on one box, as commit 17241280 did.
- Option C changes shadowing order risk and diagnostics; it is the only option with a user-visible failure mode, which is why it carries the scope-equivalence gate.
- `ReadyToRun.targets` is Release-only and fail-open; do not let option D change that.
- Shares the opens loop with `sessions-learn-the-load-order`. Land step 1 here first. Learned open order also shrinks A's failure cost, so the two compound.
- Nothing else depends on this item.

## Size

S for steps 1 to 4 (a few days, mostly the corrected timings and running the experiments). The first optimization after that is S to M (B or A). C or D are M to L each and only if M3/M4/M5 point at them.

## Decisions for Will

1. Which solution is "a bigger solution" for the published table? Recommendation: `SageTech`, plus the `SageFs.Core` row already in the doc, and say which commit each was measured at.
2. Is a first-run crossgen2 step (download of the pack on a user's machine) acceptable for option D? Recommendation: decide only after M5; I would not ship a download for a 0.6 s gain.
3. Should raw benchmark samples be committed so CI can run a freshness check on the doc table? Recommendation: yes, a small JSON beside the doc, because the local-first rule says heavy generation happens on the desktop and CI only compares.
4. Confirm that "Ready" keeps meaning "namespaces are open". Recommendation: yes; background opens after a Ready are off the table.
