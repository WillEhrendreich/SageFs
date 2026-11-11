# Admit heavy work by measured cost

Roadmap item: `admit-heavy-work-by-measured-cost` (Later, Platform). Plan written 2026-10-26 against master 59a8692c. Work order added 2026-11-11 against master 8975e898; line numbers below are from that tree.

## Work order

### 1. Outcome

An agent that asks for a lease while the machine cannot take the work hears so before it starts, with numbers: the expected peak for its kind of work, what is available after the leases already out, how long the holder usually takes, and when to ask again. A machine with room grants at once, as today. A kind of work the daemon has never seen is judged by the count cap alone, and the reply says so. Gate: (a) the pure differential property "with the machine not measured, `requestWith` makes exactly the decision `request` makes" in the default suite; (b) `LeaseCostSim` with its four twins; (c) a host-tier case with a seeded `work-costs.json` in an isolated data dir asking as two holders and checking the wire reply per cell (kind by history state by contention by reading state). Slice 1 (durations in the wait reply) ships on its own. Linux proves everything; off Linux `MachineMemory.current ()` answers an approximation (`SageFs.Core/Features/MachineMemory.fs:113-135`) and the reply must say "not measured", which the Windows and macOS legs of `hot-reload-on-windows-and-macos` would prove.

### 2. Where the code is today

- The pool: `SageFs.Core/ExpensiveWorkLease.fs` (653 lines; `SageFs.Core.fsproj:553`). `Kind` (`:61-66`, five cases), `Kind.all` (`:68`), `toToken` (`:72`), `tryParse` (`:80`), `describe` (`:82`), `defaultTtl` (`:94`, from `Timeouts.leaseTtl*`, `Timeouts.fs:553-565`). `WaitReason = AtCapacity | NotYourTurn` (`:208-212`), `Waiting` (`:216-225`, has `RetryAfter`, no expected-free time), `Decision` (`:242-251`), `PoolState` (`:253`), `maxConcurrentFor` (`:256-260`: Normal 4, Tight 1, Critical 0), `maxPerHolder = 1` (`:270`), retry bases (`:282-284`, `Timeouts.leaseRetryAfter*`), `requestCore` ending at `:446` (grant at `:430`, queued at `:434-446`), `request = requestCore realPolicy` (`:457`), five twins (`:464-489`), `release` (`:508`), `explain` (`:569-614`), `snapshot` (`:622-653`). `ActiveLease` carries `GrantedAt` and `ExpiresAt` (`:430`).
- The shell: `SageFs.Core/Features/LeaseWatch.fs` (56 lines; `fsproj:554`). `request` (`:24-29`) reads `(MemoryPressureWatch.observe MemoryPressureWatch.live).Level`. That observation reads `MachineMemory.current ()` fresh on every ask (`MemoryPressureWatch.fs:29-30`, `:67-72`) and takes the worse of the stored level and the footprint floor. So the ask already pays for a fresh machine reading and then uses only its pressure level; the plan's "as old as the last sweep" is true of the level, false of the reading. `release` (`:39`), `releaseOwned` (`:46`), `snapshot` (`:53`), `reset` (`:56`).
- Callers: the daemon's own `Rebuild` ask in `productionBuildRecovery` (`SageFs/DaemonMode.fs:460-503`; `request` `:473`, `release` in `finally` `:493`, `withdraw` `:500`, holder `"daemon"` plus `"build for create:<projects>"` plus the lowercased directory). HTTP: `POST /api/lease/request` (`SageFs/McpServer.fs:4039`), `/api/lease/release` (`:4066`), contracts `EndpointContracts.fs:70-71`; the reply is `McpLeaseWire.decisionJson` (`SageFs/McpLeaseWire.fs:61`). The MCP tools (`acquire_full_build_lease`, `acquire_test_suite_lease`, `acquire_run_app_lease`, `release_work_lease`). The hook (`tools/agent-hooks/Hook.fs:62-80`, holder `dotnet-cli:<user>:<pid>`, kinds `full_build`, `test_suite_run`, `run_app`). The gate (`scripts/local-gate.fsx:66-72`, `leaseRequestTimeout`, `leaseAttempts = 12`, `leaseRetryPause`; the pure reply reading is in `scripts/ReleaseRules.fs`). `get_daemon_status` reads `LeaseWatch.snapshot ()` at `SageFs/Mcp.fs:1335`.
- A second cap: `SessionBuild.buildSemaphore` (`SageFs.Core/SessionBuild.fs:355`, `availableBuildSlots` `:431`, awaited `:490`).
- The measurement style to copy: `SageFs.Core/MachineProfile.fs` (`StageEstimate` `:205-237`: `SmoothingGain 0.125`, `DeviationGain 0.25`, `DeviationMultiplier 4.0`, `first`, `observe`, `timeout`; `StageHistory = Seen | NeverSeen` `:240`; `FileName = "machine-profile.json"` `:254`; `MachineProbeReader.writeProfile` `:537`). `SageFs.Core/StartLedger.fs` (79 lines): `Ledger = { History; Record }` (`:12`), `closed` (`:21`), `openAt` (`:50`), `install` (`:72`), `shared` (`:77`); installed at `DaemonMode.fs:2223`. `fsproj:85-86`.
- The idea already built for tiers: `build/TierPlan.fs` `Candidate.PeakBytes` (`:391`), `Limits` with `MemoryReserveBytes`, `RampSeconds`, `PatienceSeconds` (`:403-412`), `unknownPeakBytes = 4 GiB` (`:420`), `StarvationGuard` (`:462`), `unramped` (`:509-513`), `decide` (`:523`; `needed = reserve + head.PeakBytes + unramped` `:570`; patience at `:574`). `build/TierCost.fs:47` reads cgroup `memory.peak`. Sim `SageFs.Simulation/TierSched.fs` (`Simulation.fsproj:342-343`), tests `TierPlanTests.fs`, `TierSchedSimTests.fs`.
- Sims and tests: `SageFs.Simulation/LeaseSim.fs` (events `:34-46`: `Request`, `Release`, `Abandon`, `Withdraw`, `PressureChange`, `PassSeconds`; `PoolBehavior` twins `:58`), `LeaseSimInvariants.fs` (`all` `:305`, `violations` `:316`), `Simulation.fsproj:332-333`; `SageFs.Tests/LeaseSimTests.fs` (`fsproj:895`), `ExpensiveWorkLeaseTests.fs` (`:894`), `LeaseWireTests.fs` (`:917`; the plan's `McpLeaseWireTests` is this file), `StartLedgerTests.fs` (`:201`).
- Clients: `rg -w lease` finds nothing in `sagefs.nvim/lua` or `sagefs-vscode/src` (re-checked 2026-11-11).
- `[not verified]`: how close a 1 s `VmRSS` process-tree sample comes to cgroup `memory.peak`; the spread of held times per kind; `GC.GetGCMemoryInfo().MemoryLoadBytes` off Linux.

### 3. Steps

1. Pure. New `SageFs.Core/WorkCost.fs` after `ExpensiveWorkLease.fs` in `SageFs.Core.fsproj` (insert after line 553; before `Features\LeaseWatch.fs`): `PeakMemory`, `WorkRecord`, `CostEstimate` (reusing `StageEstimate`'s arithmetic; lift `SmoothingGain`/`DeviationGain`/`DeviationMultiplier` into one shared module only if both files then read it), `Forecast = Seen | NeverSeen`, `Headroom = AvailableMb | NotMeasured`, `WorkCost.Ledger = { Forecast; Record }` with `closed` and `openAt dataDir` writing `work-costs.json` through a `SageFs.Json` profile (never a `JsonSerializerOptions` at the call site; `JsonCentralizationTests` ratchets it). RED in a new `SageFs.Tests/WorkCostTests.fs` (register after `fsproj:894`): "WHY - the first sighting is the forecast, and a later one moves it by the smoothing gain" (mirror `StartLedgerTests.fs:42-66`), "an unreadable file reads as NeverSeen for every kind", "a lease that expired unreleased records nothing". Commit red, then green.
2. Pure. `Fit.fit reserveMb headroom forecast active` in `WorkCost.fs` and `requestWith (view: MachineView)` in `ExpensiveWorkLease.fs`, with `request` unchanged (so every existing test and `LeaseSim` run holds). `WaitReason` gains `WouldNotFit of neededMb * availableMb`; `Waiting` gains `ExpectedFreeIn: TimeSpan option`; `explain` names both numbers and the holder's usual time. RED in `ExpensiveWorkLeaseTests.fs`: "WHY - a full build expected to peak at 6 GiB is queued with the numbers when 5 GiB is available, though the count cap would have granted it"; properties "with the machine not measured `requestWith` equals `request`" (the differential), "an ask that waited the patience window with nothing active is granted", "reservation for an active lease shrinks as it ramps and is zero after". Every exhaustive match on `WaitReason` breaks (`McpLeaseWire.fs`, `explain`) and decides.
3. Pure, DST. `SageFs.Simulation/LeaseCostSim.fs` and `LeaseCostInvariants.fs` (register after `Simulation.fsproj:333`), events extending `LeaseSim.SimEvent` with `NeighbourHolds`, `ReadingLost`, `Release of observedPeak`; invariants and the four twins under "Tests" below; `SageFs.Tests/LeaseCostSimTests.fs` after `fsproj:895`. Every twin must break its invariant.
4. Edge. `LeaseWatch.request` builds the `MachineView` from the reading it already has (`observe live` returns `Reading.Machine`, use it; no second read), calls `requestWith`; `release` records held time; the ledger is installed once at daemon start next to `StartLedger.install` (`DaemonMode.fs:2223`). Durations only. This is the shippable slice: waits now say how long the holder usually takes. `Timeouts.leaseFitPatience` is a new named entry beside `leaseAskStaleAfter` (`Timeouts.fs:583`); `TimeoutLiteralsTests` fails on a bare number.
5. Wire. `McpLeaseWire.decisionJson` adds `expectedPeakMb`, `availableMb`, `holderUsuallyTakesSeconds`; the granted reply unchanged; `snapshot` rows gain `expectedPeakMb`. RED in `LeaseWireTests.fs`. `docs/mcp-tools.md`, `skills/sagefs/leases.md`. Additive, `apiVersion` stays 4, trailer `Plugin: n/a (no client reads leases)`.
6. Host tier. The outcome case (`Integration.hostCaseTask`, `TestInfrastructure.fs:520`) with `SAGEFS_DATA_DIR` on a temp dir holding a seeded `work-costs.json` whose `full_build` forecast exceeds total RAM; cells that need no waiting only (the lone-ask patience cell lives in the property and the sim).
7. Peaks, Linux: sample the build's process tree `VmRSS` for the daemon's own `Rebuild`; optional `peakBytes` on `POST /api/lease/release`; `Kind.ReleaseGate` with `Timeouts.leaseTtlReleaseGate` and token `release_gate`, asked for by `local-gate.fsx`, which sends its cgroup `memory.peak` on release. `Kind.all` feeds the 400 text at `McpServer.fs:4052`, so the new token appears there by itself.
8. Optional, last: one dependency-free file for the ramped reservation, compiled into Core and `#load`ed by `build/TierPlan.fs`.

DECISION: add `Kind.ReleaseGate`. Recommendation: yes; otherwise the gate's 90-minute holds average into agents' builds.
DECISION: a never-seen kind is judged by the count cap alone. Recommendation: yes; there is no measured prior to seed.
DECISION: the gate reports `peakBytes` on release. Recommendation: yes, after step 4.
DECISION: the shared ramp file (step 8). Recommendation: yes, last.

### 4. How to check it

- REPL first (`Skill(skill: "sagefs")`): `requestCore`, `fit` and the ledger arithmetic are pure; prove the 6 GiB versus 5 GiB case in a session on `SageFs.Core` before writing it.
- `dotnet build SageFs.Tests -c Release`
- Inner loop: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --filter-test-list "ExpensiveWorkLease" --summary`; `--filter-test-list "WorkCost"`; `--filter-test-list "LeaseCostSim"`; `--filter-test-list "McpLeaseWire"`.
- `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets` (timeout literals, JSON centralization, file sizes).
- Acceptance: the unfiltered default suite for steps 1-3 and 5; `--integration-host --summary` unfiltered for step 6, the case name in its output.

### 5. Traps in this repo

- Durations: every new span is a named `Timeouts` entry (`leaseFitPatience`, `leaseTtlReleaseGate`); `TimeoutLiteralsTests` is the ratchet. Test-chosen values go in `SageFs.Tests/TestTimeouts.fs`.
- JSON: `work-costs.json` goes through a `SageFs.Json` profile; `JsonCentralizationTests` counts call sites that do not.
- SAGE-005: `fit reserveMb headroom forecast active` carries one `int64`; `WouldNotFit of neededMb: int64 * availableMb: int64` is two of one primitive, so make `Mb` a single-case type or carry `Headroom` and `Forecast` themselves.
- Budgets only go down: `RatchetBudgets.fs:341-342` gives `DaemonMode.fs` 5529 lines and it has 5528; `McpServer.fs` gets 5056 and it has 5056. The install line fits in `DaemonMode.fs`; the route change at `McpServer.fs:4039-4064` must not grow that file, so the reading of `peakBytes` goes in `McpLeaseWire.fs`.
- Wire: `McpLeaseWire.fs` and `McpServer.fs` are on the trailer list; `Plugin: n/a (no client reads leases)` on every commit that touches them.
- No wall-clock tests: the pool takes `now`; the sim injects it; the host case runs only cells that answer at once.
- `LeaseSim` and `LeaseSimInvariants.all` must still hold over `requestWith` (the differential property is the proof).
- Linux-only: peaks off Linux are `NotAttributed`, headroom is `NotMeasured`, and the reply must say so.

### 6. Size and order

M. Steps 1-3 are a week (1 and 2 in parallel once the types in 1 exist, 3 after 2); step 4 a day and shippable alone; 5 and 6 two days; 7 two to three days on Linux; 8 a day. First commit: the RED `WorkCostTests.fs` case for the first sighting, red because `WorkCost.fs` does not exist.

### 7. Not now

- Merging `SessionBuild.buildSemaphore` with the lease pool: two caps stay; note it in `docs/decisions.md`.
- Keying history by repository (`Checkout.classify`): only after the week of logging shows the spread within a kind is too wide.
- Changing the three MCP acquire tools or `release_work_lease`: the tool catalog's token weight (`RatchetBudgets.toolList`) stays as is; `peakBytes` is HTTP-only.
- Off-Linux headroom: needs the platform legs.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a 6 GiB build is queued with numbers when 5 GiB is free. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

An agent that asks for a rebuild, a full build, a test run or an app run while the machine cannot take it hears so before the work starts, with numbers: "your full build is expected to peak near 5.1 GiB (seen 14 times); 3.2 GiB is available after reserving for the test run already in progress; that run usually takes 9 minutes and started 4 minutes ago; ask again in about 5 minutes". A machine with room grants at once, as today. A kind of work the daemon has never seen is judged by the old count cap alone, and the reply says so. Nothing waits forever on a wrong guess.

The outcome gate is a host-tier case that starts a real daemon with an isolated data dir and a pre-seeded cost file, asks for leases as two holders, and checks the wire reply per cell. Matrix: five kinds of work, history in three states (never seen, seen small, seen larger than the machine), contention in two states (alone, one lease active), and the machine reading in two states (measured, not measured). In every cell the expected decision is written down; an unmeasured reading must reproduce today's decision exactly.

## What exists today

- `SageFs.Core/ExpensiveWorkLease.fs` (653 lines) is the pool. `Kind = SessionCreateOrWarmup | Rebuild | FullBuild | TestSuiteRun | RunApp` with `toToken`, `tryParse`, `describe`, `defaultTtl`. `maxConcurrentFor` caps grants by memory pressure: `Normal` 4, `Tight` 1, `Critical` 0. `maxPerHolder` is 1. `request` is `requestCore realPolicy`, pure, with the injected `Policy` record (`Admit`, `Reap`, `Identify`, `ReAsk`) that lets five twins (`requestNeverExpiresTwin`, `requestCollapsedIdentityTwin`, `requestDuplicatesOnReAskTwin`, `requestFrontOnlyTwin`, `requestJumpsTheQueueTwin`) share every other line. A queued ask gets `Waiting { Position; Ahead; Holding; Cap; Pressure; Why; RetryAfter }`, `WaitReason = AtCapacity | NotYourTurn`. The retry-after is a base by pressure plus 3 s per place in line, jittered from the holder's identity.
- It knows nothing about how much memory or time a kind of work takes. The cap is a count.
- `SageFs.Core/Features/LeaseWatch.fs` is the stateful shell: one `PoolState` behind a lock; `request` reads `(MemoryPressureWatch.observe MemoryPressureWatch.live).Level` (`LeaseWatch.fs:26` on 2026-11-11). `observe live` reads `/proc/meminfo` and the daemon's footprint fresh on every ask (`MemoryPressureWatch.fs:29-30`, `:67-72`) and answers the worse of the stored level and the footprint floor. The stored level is what the last `MemorySupervisor` evaluation set, at session admission (`DaemonMode.fs` `checkMemoryAdmission`, line 444) or in the periodic sweep (`shedIdleSessionsIfNeeded`, line 4573). So the ask already has a fresh machine reading and throws away everything in it but the level; the fix is to use the reading it has, not to add a read.
- `SageFs/DaemonMode.fs` line 473 (477 when written): the daemon's own `request` for `Kind.Rebuild` around `SessionBuild.runBuildAsync` (`productionBuildRecovery`), holder `"daemon"` plus `"build for create:<projects>"` plus the lowercased directory. I found no daemon-side `request` for the other four kinds; callers ask through `POST /api/lease/request` (`McpServer.fs` line 4039), the MCP tools (`acquire_full_build_lease`, `acquire_test_suite_lease`, `acquire_run_app_lease`, `release_work_lease`), the agent hook (`tools/agent-hooks/Hook.fs:62-80`, holder `dotnet-cli:<user>:<pid>`), and `scripts/local-gate.fsx` (lease constants at lines 66-72, kind `full_build`, so agents back off while the gate runs).
- A second, unrelated cap exists inside the build itself: `SessionBuild.buildSemaphore` (cores / 4, at least 1) bounds concurrent daemon `dotnet build`s. Two caps decide one thing.
- `SageFs.Core/MachineProfile.fs` and `StartLedger.fs` are the measurement style to follow. `StageEstimate` keeps a smoothed mean and a smoothed deviation (RFC 6298: gain 0.125, deviation gain 0.25, timeout = mean + 4 deviations), `StageHistory = Seen | NeverSeen` (absence is a case of the DU), and `StartLedger.Ledger = { History; Record }` is a record of two functions persisted to `machine-profile.json` in the data dir, with `closed` for workers and tests. The comments in `MachineProfile.fs` hold the only memory figures I found for the daemon's own work: the daemon, worker and FSI host peak near 720 MB together on a start; a hard reset with a rebuild did not return under a 1 GB cap; 1.5 GB available lost nothing (`scripts/machine-bench.fsx --memory-cap`, 2026-10-01).
- `build/TierPlan.fs` already does what this item asks for tiers. `Candidate.PeakBytes` is the last recorded peak (`TierSchedule.peakBytesOf`, from `TierCost`, which reads cgroup `memory.peak` for each tier's systemd scope), `unramped` reserves the part of a running unit's peak it has not reached yet over `RampSeconds`, `decide` admits when `available >= MemoryReserveBytes + head.PeakBytes + unramped`, and `StarvationGuard` admits a head after `PatienceSeconds` when nothing of ours runs. `SageFs.Simulation/TierSched.fs` runs it against a fake machine with neighbours and units that die. This is a second copy of the idea outside the daemon.
- `Features/MachineMemory.fs`: `current ()` reads `/proc/meminfo` on Linux; elsewhere it answers total from the GC and available as total minus this process's working set, which its own comment calls approximate. `docs/progress.md` says the same.
- `SageFs.Core/WorkspaceHygiene.fs` sets the style for gather-then-decide: pure decisions over facts, "standing says why", `Unknown` is never acted on, edge interpreters in `HygieneGather.fs`.
- Simulations to extend: `SageFs.Simulation/LeaseSim.fs` and `LeaseSimInvariants.fs` (events `Request`, `Release`, `Abandon`, `Withdraw`, `PressureChange`, `PassSeconds`; invariants `mutualExclusion`, `everyLeaseExpires`, `grantNeverJumpsALiveWaiter`, `noRoomWithheld`, ...) and `StartLearningSim.fs`.
- No client reads leases. `rg -w lease` finds nothing in `sagefs.nvim/lua` or `sagefs-vscode/src`.

## Design

### What gets recorded

Per lease: kind, how long it was held, and the peak memory, when known. A lease that expired without a release says nothing about its duration (nobody knows when the work ended), so it is dropped.

```fsharp
type PeakMemory =
  | Measured of megabytes: int64
  | NotAttributed of reason: string     // "other leases were active", "no process tree walk off Linux"

type WorkRecord = { Kind: Kind; Held: TimeSpan; Peak: PeakMemory }

type CostEstimate = { Kind: Kind; HeldMs: Smoothed; PeakMb: Smoothed; Samples: int }   // Smoothed = mean + deviation, as StageEstimate

type Forecast =
  | Seen of peakMb: int64 * held: TimeSpan * samples: int     // mean + 4 deviations for the peak, mean for the time
  | NeverSeen

type Headroom =
  | AvailableMb of int64
  | NotMeasured of reason: string
```

`CostEstimate` reuses the `StageEstimate` arithmetic; if `Smoothed` is lifted out of `MachineProfile.fs` into a shared module, both use it (one home). A new file `SageFs.Core/WorkCost.fs` sits after `ExpensiveWorkLease.fs` in `SageFs.Core.fsproj` (that file is at line 553 on 2026-11-11; `Kind` is defined there). `WorkCost.Ledger = { Forecast: Kind -> Forecast; Record: WorkRecord -> unit }` follows `StartLedger.Ledger`, with `closed`, and is persisted in its own file, `work-costs.json` in the data dir, through `SageFs.Json`. A separate file keeps a damaged cost file from taking the machine profile with it; an unreadable one reads as `NeverSeen` for every kind.

### The admission rule

A pure function in `WorkCost.fs`, the same shape as `TierPlan.decide`:

```fsharp
type Fit =
  | Fits
  | WouldNotFit of neededMb: int64 * availableMb: int64
  | CannotJudge of reason: string       // NeverSeen, or Headroom NotMeasured

val fit : reserveMb:int64 -> Headroom -> Forecast -> active:(Forecast * elapsed:TimeSpan) list -> Fit
```

`needed = reserve + asked peak + sum over active leases of (their forecast peak * max(0, 1 - elapsed / ramp))`. The ramp for a kind is a fraction of its forecast hold time, with a floor. `Fits` when `available >= needed`. `CannotJudge` falls back to the count cap, which is exactly today's decision. A starvation guard admits an ask that has waited past a patience window when nothing is active, so a forecast can delay a lone ask, never strand it.

`ExpensiveWorkLease` takes this as an added argument. `request` keeps its current signature and means "no forecast", so every existing test and `LeaseSim` run is unchanged; a new `requestWith` takes a `MachineView = { Headroom; ForecastOf: Kind -> Forecast; ReserveMb: int64; Patience: TimeSpan }` and is the one `LeaseWatch.request` calls. The count cap stays as an upper bound: a grant needs both. `WaitReason` gains `WouldNotFit of needed * available`, `Waiting` gains `ExpectedFreeIn: TimeSpan`, computed from the oldest active lease's forecast hold time minus its elapsed time, and `RetryAfter` uses it when known. `explain` names the forecast, the available memory, the holder's usual time and what to do.

### Where observations come from

| Kind | Held | Peak |
|---|---|---|
| `Rebuild` (daemon's own build) | exact, grant to release | Linux: highest sum of `VmRSS` over the build's process tree, sampled; elsewhere `NotAttributed "no process tree walk off Linux"` |
| `FullBuild`, `TestSuiteRun`, `RunApp` asked by a caller | exact when released; dropped when expired | `NotAttributed` unless the releaser reports it, or the lease was alone in the pool and available memory is sampled: then the dip below the level at grant |
| `SessionCreateOrWarmup` | same | same |

The first slice records durations only. That already improves the wait reply (the holder's usual time) and needs no new measurement. Peaks come in slice 2. A caller that can read its own cost reports it: `POST /api/lease/release` accepts an optional `peakBytes` (additive; `scripts/local-gate.fsx` can send the cgroup `memory.peak` it already collects through `build/TierCost.fs`). The MCP `release_work_lease` tool is left alone in this item, so the tool catalog's token cost is unchanged.

`LeaseWatch.request` reads `MachineMemory.current ()` itself at ask time (a cheap file read) instead of the stored pressure. `LeaseWatch.release` looks up the lease's `GrantedAt` under the same gate lock before releasing, so `ExpensiveWorkLease.release` and `ReleaseOutcome` keep their shapes.

### Key by kind, with one proposed new kind

History is keyed by `Kind` alone at first. That mixes two things with the same kind that differ by an order of magnitude: the release gate holds a `full_build` lease while it runs every test tier, and an agent's `dotnet build` also asks for `full_build`. Their forecasts would average into nonsense. The fix that needs no heuristic is a sixth case, `Kind.ReleaseGate` (token `release_gate`, `Timeouts.leaseTtlReleaseGate` of about the pipeline's 90-minute ceiling), which `local-gate.fsx` asks for. `Kind.all` feeds the HTTP error text that lists valid kinds, so the new token appears there by itself; the three MCP acquire tools keep their three kinds. If, after measuring, one kind still has a wide spread across projects, add a second key, the repository identity from `Checkout.classify` (`MainCheckout root` or `Worktree (root, branch)`; a worktree maps to its main checkout), so ephemeral agent worktrees of one repository share a history.

### What this does for a gate and a build on one box

The gate holds one lease for the whole run and agents back off. With a `ReleaseGate` forecast (its peak is what the gate reports on release) the pool can say "the gate usually peaks near 38 GiB; 12 GiB is available; your build waits" before an agent's build starts, and the gate can ask first on a busy box and learn it should wait. Today it asks fail-open and gates anyway (its comment explains why a release must never block on a wedged daemon), and that stays.

### Wire, clients, formats

- `McpLeaseWire.fs` is on the wire-surface list. The wait reply gains `expectedPeakMb`, `availableMb`, `holderUsuallyTakesSeconds` and `reason` text with them; the granted reply is unchanged. `get_daemon_status`'s lease rows (`ExpensiveWorkLease.snapshot`, read at `Mcp.fs` line 1357) gain `expectedPeakMb`. All additive, so `apiVersion` does not move. Update `docs/mcp-tools.md` and `skills/sagefs/leases.md`. Commits carry `Plugin: n/a (no client reads leases)`.
- No new MCP tool. `McpServer.fs` line 3513 accepts `release_gate` through `Kind.tryParse`.
- New persisted file `work-costs.json`; no change to `.sagefm` or `machine-profile.json`.

## Tests

RED first, in `ExpensiveWorkLeaseTests.fs`, a new `WorkCostTests.fs`, and `LeaseSim`:

1. `WorkCostTests`: "WHY - the first sighting is the forecast, and a later one moves it by the smoothing gain", mirroring the `StageEstimate` cases in `StartLedgerTests.fs`. Fails to compile today.
2. `ExpensiveWorkLeaseTests`: "WHY - a full build expected to peak at 6 GiB is queued with the numbers when 5 GiB is available, though the count cap would have granted it". Asserts `Decision.Queued` with `Why = WouldNotFit(6144, 5120)` and that `explain` contains both figures and the words for what to do.
3. Property: "WHY - with the machine not measured, `requestWith` makes exactly the decision `request` makes", over random pools, kinds and pressures. The differential against today's behavior is the guard that nothing regressed off Linux.
4. Property: "WHY - an ask that has waited the patience window with nothing active is granted whatever the forecast says".
5. Property: "WHY - memory reserved for an active lease only shrinks as it ramps, and is zero after the ramp".
6. "WHY - a lease that expired unreleased records nothing", and "a released lease records its held time and, when attributed, its peak".
7. Wire: `LeaseWireTests.fs` (the file that tests `McpLeaseWire.decisionJson`) for the new fields and the unchanged granted reply.

Simulation: `SageFs.Simulation/LeaseCostSim.fs` with `LeaseCostInvariants.fs` and generators, shaped like `TierSched.fs` (a fake machine with neighbours holding memory between two moments, a machine reading that is sometimes unreadable, leases that complete or are abandoned, seeded, replayable). Events extend `LeaseSim.SimEvent` with `NeighbourHolds of mb * until`, `ReadingLost`, `Release of observedPeak`. Invariants: a grant when the forecast is `Seen` and the reading measured implies `available >= needed` unless the starvation guard fired; the unmeasured run equals `request`; an abandoned lease records nothing; every existing `LeaseSimInvariants.all` still holds over `requestWith`. Twins, each making one invariant fail: `IgnoresForecastTwin` (count cap only), `NoPatienceTwin` (a lone ask waits forever on an over-large forecast), `CountsAbandonedTwin` (records an expired lease as complete, poisoning the forecast), `ReservesNothingForRampTwin` (a burst of grants overcommits).

Outcome gate: the host-tier case above (`Integration.hostCaseTask`), with `SAGEFS_DATA_DIR` pointing at a temp dir that holds a seeded `work-costs.json`. It uses the seeded forecast to make the machine "too small" (a `full_build` forecast larger than total RAM) instead of faking memory. The cells that need no waiting are the ones it runs: a second ask beside an active lease whose forecast does not fit is `wait` with `WouldNotFit` at once; a small forecast beside an active lease is `granted`; a never-seen kind is `granted` by the count cap with a reply that says it could not judge; an unmeasured reading is `granted` as today. The lone-ask-with-a-huge-forecast cell would sit out the patience window, so it lives in the pure property (test 4) and the simulation, where time is injected, and not in the host tier. The patience value is a new named `Timeouts` entry (`leaseFitPatience`), passed to the pool in `MachineView`.

No test waits on wall-clock time: the pool takes `now` as an argument and the sim injects it.

## Steps

1. `WorkCost.fs` types, `Ledger` and persistence, test 1, test 6. Owns `SageFs.Core/WorkCost.fs`, `SageFs.Core.fsproj`, a new test file. Parallel with 2.
2. `Fit.fit` and the `requestWith` path in `ExpensiveWorkLease.fs`, tests 2 to 5, then the sim and twins. Owns `ExpensiveWorkLease.fs`, `SageFs.Simulation/LeaseCostSim.fs`, `LeaseCostInvariants.fs`, `LeaseSimTests.fs` additions. File-disjoint from 1 apart from the shared types, so write the types in step 1 first.
3. `LeaseWatch` reads machine memory at ask time, records durations on release, the ledger is installed once at daemon start next to `StartLedger.install`. Owns `Features/LeaseWatch.fs` and the install line in `DaemonMode.fs`. Durations only. This is a shippable slice: waits now say how long the holder usually takes.
4. Wire text: `McpLeaseWire.fs`, `docs/mcp-tools.md`, `skills/sagefs/leases.md`, test 7.
5. The host-tier outcome gate.
6. Peaks: process-tree sampling for the daemon's own build on Linux; optional `peakBytes` on release; `local-gate.fsx` sends its scope's `memory.peak`. Adds `Kind.ReleaseGate` with its TTL and token.
7. Optional: lift the ramped-reservation formula into one dependency-free file compiled into Core and `#load`ed by `build/TierPlan.fs`, so tiers and leases share one `unramped`. Only after 1 to 6 are stable; the gate is mid-release and `TierPlan` is its scheduler.

## Measure first

- How wide is the spread within a kind? Experiment: step 3 logs `Kind`, held time and (where attributed) peak for a week of normal use on this machine and on the gate. If the coefficient of variation of `Rebuild` held time across projects is under about 0.5, key by kind. If not, add the repository key. Without data the plan keeps kind-only.
- Is process-tree `VmRSS` sampling close enough to the true peak? Experiment: run the same `dotnet build` inside `systemd-run --user --scope` (as `TierCost.accountedArgv` does) and read cgroup `memory.peak`, then compare with the sampled sum at 1 s. If they differ by more than about 15 percent, sample faster or run the daemon's builds in a scope.
- How stale is the stored pressure level at ask time? Experiment: log `MemoryPressureWatch.currentLevel ()` and a fresh `MachineMemory.current ()` at each ask for a few days. If they rarely disagree, the freshness change in step 3 buys little and is still free.
- How often does memory, rather than the count cap, say wait? Run the sim over logged asks before turning the rule on.
- Off Linux: does `GC.GetGCMemoryInfo ().MemoryLoadBytes` give a machine-wide figure on macOS and Windows? If it does, `MachineMemory.current` can answer `AvailableMb` there instead of `NotMeasured`. Needs the platform legs of `hot-reload-on-windows-and-macos`.

## Risks and dependencies

- A forecast that is too high queues work that would have run. The starvation guard and the fallback to the count cap bound the cost to one patience window per ask, and the reply says why.
- The pool is cooperative: a caller that never asks is invisible. The forecast improves honest callers' waits; it does not stop a rogue build.
- Two caps (the lease pool and `SessionBuild.buildSemaphore`) remain. Merging them is a separate change; note in `docs/decisions.md`.
- A daemon restart loses the in-memory pool but not the ledger, which is the point.
- Depends on nothing to start. Off-Linux headroom depends on `hot-reload-on-windows-and-macos`. The release gate's use of the forecast depends on step 6 and on Will agreeing to the `ReleaseGate` kind.
- What would change my mind: if the week of logging shows held times with no stable mean even within one repository, durations are not forecastable here and the plan reduces to the fresh headroom read plus the wait text.

## Size

M. The pure rule, ledger and sim are about a week; wire text, the host case and Linux peak sampling are another. Slice 3 (durations in the wait reply) can ship alone.

## Decisions for Will

- Add `Kind.ReleaseGate` so the gate's history stays apart from agents' builds? I recommend yes; the alternative is to guess from the holder's name.
- A never-seen kind is judged by the count cap alone (no invented priors)? I recommend yes. The measurements I found are not per-kind peaks, and a made-up prior would be a number with no source.
- Should the gate's release report `peakBytes`, using the cgroup counters it already collects? I recommend yes, after step 3.
- Do you want the shared ramped-reservation file (step 7) so the tier scheduler and the lease pool cannot drift? I recommend yes, last.
