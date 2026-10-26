# Admit heavy work by measured cost

Roadmap item: `admit-heavy-work-by-measured-cost` (Later, Platform). Plan written 2026-10-26 against master 59a8692c.

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
- `SageFs.Core/Features/LeaseWatch.fs` is the stateful shell: one `PoolState` behind a lock; `request` reads `MemoryPressureWatch.currentLevel ()`, which is the level the last `MemorySupervisor.step` stored. That level is updated at session admission (`DaemonMode.fs` `checkMemoryAdmission`, line 440) and by a periodic sweep (`shedIdleSessionsIfNeeded`, near line 3905, described there as a steady low-frequency heartbeat). So the pressure a lease is admitted against can be as old as the last sweep.
- `SageFs/DaemonMode.fs` line 477: the daemon's own `request` for `Kind.Rebuild` around `SessionBuild.runBuildAsync` (`productionBuildRecovery`), holder `"daemon"` plus `"build for create:<projects>"` plus the lowercased directory. I found no daemon-side `request` for the other four kinds; callers ask through `POST /api/lease/request` (`McpServer.fs` line 3513), the MCP tools (`acquire_full_build_lease`, `acquire_test_suite_lease`, `acquire_run_app_lease`, `release_work_lease`), the agent hook (`tools/agent-hooks/Hook.fs`, holder `dotnet-cli:<user>:<pid>`), and `scripts/local-gate.fsx` line 307 (holder `local-gate-<pid>`, kind `full_build`, so agents back off while the gate runs).
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

`CostEstimate` reuses the `StageEstimate` arithmetic; if `Smoothed` is lifted out of `MachineProfile.fs` into a shared module, both use it (one home). A new file `SageFs.Core/WorkCost.fs` sits after `ExpensiveWorkLease.fs` in `SageFs.Core.fsproj` (that file is at line 435; `Kind` is defined there). `WorkCost.Ledger = { Forecast: Kind -> Forecast; Record: WorkRecord -> unit }` follows `StartLedger.Ledger`, with `closed`, and is persisted in its own file, `work-costs.json` in the data dir, through `SageFs.Json`. A separate file keeps a damaged cost file from taking the machine profile with it; an unreadable one reads as `NeverSeen` for every kind.

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
7. Wire: `McpLeaseWireTests` for the new fields and the unchanged granted reply.

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
