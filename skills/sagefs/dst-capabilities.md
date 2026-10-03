# What the DST and mutation harness can already do

Read this before writing a test, so you reach for what exists instead of
re-inventing it or settling for a few examples. The point of the list is that
**the watchers are watched**: every area below already has deterministic
simulation or mutation coverage, so a change to one of them without new evidence
is a regression in the evidence, not just in the code.

Everything here is in `SageFs.Simulation/` and `SageFs.Tests/`. The counts are
from 2026-10-03: **42 simulations, 39 invariant modules, 21 generator modules,
18 mutation suites.**

## The shape every simulation shares

`SageFs.Simulation/Scenario.fs` sets the rules, and they are the reason this
harness finds things:

- **Chaos is DATA.** A `Scenario` carries an explicit, ordered, SEEDED schedule
  of events and faults — not ambient randomness. The same seed produces the
  identical run, so any failure replays exactly.
- **Time is injected.** The run threads a simulated clock (`StartTime` plus
  accumulated `ClockAdvance` spans). Nothing calls `DateTime.Now`.
- **The REAL code is the subject.** The sim folds events through the actual
  production function — `RestartPolicy.decide`, `SessionLifecycle.onWorkerExited`,
  `Cohort.decide` — and there is no reimplemented "candidate" to compare against.
- **The oracle is a set of named invariants**, each a property the real code is
  claimed to satisfy, each in its own `*Invariants.fs`.

## The three pieces you compose

| Piece | File | What it gives you |
|---|---|---|
| Scenarios | `Scenario.fs` | seeded event schedules, an injected clock, the fold over real code |
| Generators | `Generators.fs` + 20 per-area modules | valid-by-construction inputs; never emit a case the validator would reject |
| Invariants | 39 `*Invariants.fs` | one named property per rule, pure over a single reachable state |
| Faults | `FaultInjection.fs` | PURE combinators that rewrite a schedule: clock skew, reorder, DROP, DUPLICATE |
| Shrinking | `Shrink.fs` | ddmin-style delta debugging — a failing 40-event run shrinks to a MINIMAL repro |

`FaultInjection.fs` matters more than its size suggests: because a fault is
data, you can compose it with a generator and assert the twin catches it. That
is how "the watchers are watched" actually works — the fault injection is not
decorative chaos, it is the mutant.

## Where the coverage already is

**This list is derived from the filesystem, not written by hand** — an earlier
draft of this file invented nine module names that do not exist, which is the
exact rot this document exists to prevent. If you extend it, derive the names
(`ls SageFs.Simulation/*.fs`) rather than recalling them.

Anything you touch in these areas needs new evidence, because the existing
evidence is what you are changing.

**Sessions, workers and restarts**
`WorkerLifecycleSim` (with `SupervisorSim`, `SupervisorWatchdogSim`,
`GranularRestartSim`, `ReadyWaitSim`, `StartEscalationSim`, `WatcherLifecycleSim`,
`WorkflowSwitchSim`, `MemoryShedSim`)

**Reload, hot reload and the file watcher**
`FileReloadRoutingSim`, `DeltaRouteSim`, `LiveCheckPumpSim`, `HolderSim`,
`HolderRewriteSim`, `FsiEmitSim`, `FsiDiscoveryIdentitySim`, `RewriteValidationSim`,
`TrunkFollowSim`, `StartLearningSim`

**Builds, confirmation and warmup**
`BuildConfirmationSim`, `PatchConfirmationSim`, `WarmupSim`, `SourceStateSim`,
`SessionStatusReconciliationSim`

**Testing, coverage and results**
`Coverage` (its own project-level sim), `ManifestSim`, `RetentionSim`,
`TestDebugSim`, `TypeShapeMigrationSim`

**Cohort and landings**
`CohortLandingSim`, plus the two **exhaustive** specs — `CohortSpec` enumerates
every reachable state over a bounded model, so it is a proof rather than a
sample, and `CohortVacancySpec` carries the vacancy invariants with a twin that
genuinely breaks them.

**Capability tokens, gates and authority**
`CapabilitySim`, `GuardSim`, `ValueReadSim`, `ReflectionReadSim`, `TweakSim`

**Health, hygiene and leases**
`HealthAnomalySim`, `HygieneSim`, `LeaseSim`

**The stdio bridge**
`McpStdioBridgeSim` — worth knowing about when a bridge change is yours, since
the startup race it models is a real class of bug.

**Shared, not per-area**: `Scenario.fs`, `Generators.fs`, `Shrink.fs`,
`FaultInjection.fs`, `Invariant.fs`, `Oracle.fs`, `Runner.fs`, `SeedCorpus.fs`,
`Linearizability.fs`, `ReferenceModel.fs`.

## Mutation testing, and what it is for

`SageFs.Tests/MutationTestingFramework.fs` defines a `Mutant<'a>`: a named
transformation that breaks exactly one thing, applied to real code, where the
requirement is that an existing assertion then FAILS.

There are **18 mutation suites**, for: `Affordances`, `BinaryPrimitives`,
`CohortGit`, `CoverageView`, `CoverageViewProject`, `EvalStore`,
`HotReloadState`, `ReloadPlanningDecision`, `RestartPolicyBoundary`, `ResultEx`,
`SageFsError`, `SessionDisplay`, `SessionLifecycle`, `SessionOperations`,
`SseWriterCohort`, `TestCachePersistence`, `Watchdog`, `WorkflowTypes`.

**The three layers the framework's own header names**, which is a useful way to
think about where a gap actually is:

1. **Lean theorems** (`formal-verification/lean/`) — model correctness.
2. **Correspondence tests** — model ↔ implementation alignment.
3. **Mutation tests** — whether the TEST SUITE would notice a wrong answer.

A module with examples but no mutants is at layer 3 blind, however green it is.

## What this means for your change

1. Touching one of these areas? The existing sim or mutant is your BASELINE and
   it is about to become wrong. Update it, and say how.
2. Adding a new decision function? It wants both: a sim with named invariants
   over the real function, and mutants for each branch.
3. Adding a new area? Follow `Scenario.fs`'s contract rather than inventing
   randomness, or the failure you find at 3am will not replay.

A claim like "tests pass" means very little on its own here. What means
something is: the invariant is named, the twin breaks it, the mutant is killed,
and the run replays from its seed.