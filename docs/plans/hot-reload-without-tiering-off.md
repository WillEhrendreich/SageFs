# Hot reload without turning tiering off

Roadmap item: `hot-reload-without-tiering-off` (Exploring, hot reload). Plan written 2026-10-26 against master 59a8692c.

## Work order

Written 2026-11-11 against master 8975e898. Every `path:line` below was read at that commit; the older sections keep their own numbers, some of which have moved.

### 1. Outcome

A hot reload session starts its FSI host with the runtime's tiered compilation left on, and the reflection watch behind rule 2 still cannot lapse without the save failing closed. Or the item closes because a fresh measurement shows tiering-off costs too little to chase. Either is done. The gate for the build case is a copy of the keep-tiering row at `SageFs.Tests/HotReloadStateOutcomeTests.fs:353` for the new choice, hammering the entry points well past `hammerCalls = 80` (line 342) on net10 and net11, asserting the watch never reports `Lapsed`. The file registers through `Integration.hostListConcurrent` (line 471), so the acceptance run is `--integration-host`. Linux only on this machine; Windows and macOS are the `hot-reload-on-windows-and-macos` item.

### 2. Where the code is today

- `TieringChoice` is `SageFs.Core/Middleware/ValueReads.fs:576-625`: cases at 580 and 584, `all` 587, `standard` 595, `name` 598, `parse` 609, `cost` 616, `consequence` 622. It sits in `ReflectionSettings` at 934 with `standard` at 937.
- The setting is `tieredCompilationSetting` at `SageFs.Core/SessionAgent.fs:44`, read by `reflectionSettingsAt` at 85-92 (a bad value falls back to `standard`). The dashboard lists it at `SageFs/Dashboard.fs:2517`.
- `processEnvironment` is `SageFs.Core/Middleware/ValueReadTracking.fs:1644-1650` (the comment above it, 1625-1643, is the reasoning). Its one call site is `SageFs.Core/IsolatedFsiSession.fs:800`, the FSI host's environment. That comment names `read-tracking-costs.md`, which is not in the tree.
- The watch: `EntryPoint` cases `ValueReadTracking.fs:746-752`, `ReflectionCanary` 776, `canaryHits` 793, `lapsedPoints` 903, `patchPoint` 911, `checkEntryWatch` 923, `entryWatch` (a `Lazy`) 950, `Tracker.Checkpoint` 1610.
- PGO is off in `SageFs.Host/SageFs.Host.fsproj:26` and `SageFs.FsiHost/FsiHost.fsproj:23`. The R2R list is `SageFs.Host/ReadyToRun.targets:39`; `HostManifestTests.fs:100` (`HostReadyToRun`) pins it.
- `DOTNET_TC_CallCountingDelayMs=0` is already set for one test child at `SageFs.Tests/SandboxChild.fs:580`, with a comment that says why. M3 can copy that.
- The published numbers are `docs/hot-reload.md:474-478`. `docs/decisions.md:649-650` records that a generic detour is lost after tier-up and relies on tiering being off.
- Tests: `ReflectionReadSurfaceTests.fs:92-105` (`processEnvironment` per choice), `ReflectionReadTrackingTests.fs:135` and `322`, `HotReloadStateOutcomeTests.fs:342-378`, `SageFs.Simulation/ReflectionReadSim.fs:74` (`Tiering` input), `313` (`LoseEntryWatch`), `429-433` (lapse only under `KeepTiering`), `476` (`neverPatchedWhileLapsed`). `TieredCompilationCostBenchmark.fs` is in the fsproj (line 1011) and is not a registered test; its header says how to run it from a REPL session.
- Nothing in the repo uses `System.Diagnostics.Tracing.EventListener` or `System.Runtime.JitInfo` (grep over `*.fs` and `*.fsx`). M4 starts from zero.

### 3. Steps

0. Measurement M1, no code. In a SageFs session on `SageFs.Tests`: `SageFs.Tests.TieredCompilationCostBenchmark.run HostRuntime.Net11;;` (await the Task as the file header says), then the same on `HostRuntime.Net10`. Add the save-to-served p50 from `SageFs.Tests/HotReloadLatency.fs` under each choice (the setting is the repo key `tieredCompilationSetting.Key`; `TieredCompilationCostBenchmark.configure` shows how to set it). Write the numbers into `docs/hot-reload.md` beside the 2026 table, dated. If the start cost is under about 0.5 s and the save cost under 10 percent, go to step 7 and close. Commit: `docs(hot-reload): re-measure tiering-off with PGO off and R2R images`.
1. Spike M4, scratch only. An `.fsx` in the scratchpad that starts a `System.Diagnostics.Tracing.EventListener` for the `Microsoft-Windows-DotNETRuntime` provider with the JIT keyword, calls `MethodBase.Invoke` 500 times, and prints every event name and payload it sees. The event and field names are not verified here; printing them is the spike's job. If no event names a method and a tier, candidate C1 is dead: skip to C3 or close.
2. Pure core, RED first. `SageFs.Tests/EntryTieringTests.fs` with the two cases in "Tests" 1 and 2, listed in `SageFs.Tests/SageFs.Tests.fsproj` right after `ReflectionReadSurfaceTests.fs` (line 639). Then `SageFs.Core/Middleware/EntryTiering.fs`, listed in `SageFs.Core/SageFs.Core.fsproj` right after `ValueReads.fs`. BCL only, the state machine from "Design". Domain.
3. The choice. Add `TierEntryPointsFirst` to `ValueReads.fs:576`; the compiler lists every match to fix (`name`, `parse`, `cost`, `consequence`, `all`, `processEnvironment`). RED: extend `ReflectionReadSurfaceTests.fs:98` with the new choice and the value M3 picked. `SessionAgent.fs:44` reads `TieringChoice.all`, so the setting text follows. Domain plus one edge line.
4. Wire the phase into `entryWatch` (`ValueReadTracking.fs:950`): run the `EntryTiering` machine with the listener from step 1 before `patchPoint`, dispose the listener when all six are `Patched`. Edge. Same file as step 3's `processEnvironment`, so after it.
5. Sim: `ReflectionReadSim.fs:429` picks from `TieringChoice.all`, so the new case is generated at once. Add the `RuntimeSaysRejitted` step and the two invariants from "Tests"; add the twin. `SageFs.Tests/ReflectionReadSimTests.fs` (fsproj line 371) runs it. Domain.
6. Host row: copy `HotReloadStateOutcomeTests.fs:353-378` for the new choice with a much higher call ceiling, both runtimes, asserting never `Lapsed`. Edge, `--integration-host`.
7. Default and docs: `TieringChoice.standard` (`ValueReads.fs:595`), `docs/hot-reload.md`, `docs/how-hot-reload-works.md`. DECISION: see 1 to 3 under "Decisions for Will"; my recommendations stand.

### 4. How to check it

- Inner loop: a SageFs session on `SageFs.Tests`, `send_fsharp_code` of the new tests, then `hard_reset_fsi_session rebuild=true` after persisting.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Filtered inner loop: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --summary --filter-test-list "EntryTiering"` (list name, so `--filter-test-list`).
- Acceptance: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --summary` unfiltered (sim and pure), then `--integration-host --summary` for step 6. Run the host tier alone; it spawns real apps.

### 5. Traps in this repo

- Budgets only go down: a new `DOTNET_TC_CallCountingDelayMs` literal is a duration; if it is committed it lives in `Timeouts` with a name, or `TimeoutLiteralsTests` fails.
- No timing-sensitive tests: step 6 waits on the report changing, the way line 373 does, never on a sleep.
- The new choice is a new value of an existing setting: trailer `Plugin: n/a, a new value of an existing setting`. No tool-list change.
- No new NuGet package: `EventListener` is BCL.
- `redefine-a-value-a-handler-renders` edits the same files; do not run both in the same week.

### 6. Size and order

S (a day) if step 0 closes it. M (three to five days) for steps 1 to 7. Step 0 and step 1 are independent and can run on the same day; step 2 can start before either finishes. First commit: the step 0 numbers in `docs/hot-reload.md`.

### 7. Not now

- Pre-tiering generic detours (M5). It only matters if the new choice becomes the default; measure after step 6.
- A runtime tier-up listener kept alive for the whole session (candidate C2). Only if H1 fails.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S if the first measurement closes it, M if not |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: an entry point is never patched before it is promoted. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

A hot reload session starts the FSI host with the runtime's tiered compilation left on, and the reflection watch behind rule 2 still cannot lapse unnoticed: the same promise `tiering-off-while-watching` makes today, kept a cheaper way. Either a measured candidate beats tiering-off on session start, first eval and save latency without adding a lapse, and becomes the default choice; or the measurements show tiering-off costs too little to matter, and the item closes with the new numbers in the docs. Both endings are a result. The outcome gate is the existing lapse rows run across choice (`tiering-off-while-watching`, the new choice, `keep-tiering`) by reflection mode (`probe-callers`, `mark-on-reflect`, `exact-every-read`) by runtime (net10, net11) by load (idle, hammering the entry points), each cell stating whether a lapse may occur and what the save says if it does. A cell where a lapse is allowed still has to fail closed.

## What exists today

- The choice is a closed type, `TieringChoice = TieringOffWhileWatching | KeepTiering` in `SageFs.Core/Middleware/ValueReads.fs` (around line 576), with `name`, `parse`, `cost`, `consequence` and `standard = TieringOffWhileWatching`. The setting is `hotreload.tieredCompilation`, `RestartRequired`, in `SageFs.Core/SessionAgent.fs` (`tieredCompilationSetting`, read by `reflectionSettingsAt`).
- The only call site that turns it into a process setting is `Middleware.ValueReadTracking.processEnvironment`, used once, in `SageFs.Core/IsolatedFsiSession.fs` (line 800 at 8975e898), in the environment of the FSI host. So the cost lands on the process that also runs the F# compiler for every eval, not only on the user's app. For `IgnoreValueReads` sessions it is empty.
- The watch is six Harmony prefixes on runtime methods: `EntryPoint = Invoke | FieldGetValue | CreateDelegate | CreateDelegateFor | DelegateCreateDelegate | DelegateCreateDelegateFor` (`ValueReadTracking.fs` around line 746), patched through `patchPoint` in `entryWatch`. A canary per point (`ReflectionCanary`, `canaryHits`, `exercise`) is read at every checkpoint by `lapsedPoints`. `checkEntryWatch` re-patches a lapsed point and records a lapse; the comment there says a patch on the recompiled code stays. `Tracker.Checkpoint` takes the getters' own watch back and, via `ReflectionWatchStatus.Lapsed`, every tracked value restarts on its next edit until the app restarts. A lapse fails closed.
- The numbers in `docs/hot-reload.md`: tiering off costs about 0.7 s of app start (8.5 s against 7.8 s, five runs, median) and warm requests were faster off (340 against 563 microseconds). They were measured by `SageFs.Tests/TieredCompilationCostBenchmark.fs` on the hot reload test app on net11. That benchmark is not a gate and is run from a SageFs session.
- Those numbers predate two other settings that change the picture, both verified in this tree: the host's `runtimeconfig` turns tiered PGO off (`<TieredPGO>false</TieredPGO>` in `SageFs.Host/SageFs.Host.fsproj` line 26 and `SageFs.FsiHost/FsiHost.fsproj` line 23; `HostManifestTests.fs` pins it), and the daemon's host directory carries ReadyToRun images of FCS, FSharp.Core, SageFs.Core and the other big assemblies (`SageFs.Host/ReadyToRun.targets`, `SageFsReadyToRunAssemblies`). With PGO off, a hot method promotes once (to tier 1) instead of twice. With R2R, most FCS code is precompiled and, with tiering off, it is used as is and never rejitted. I could not find a measurement of the tiering-off cost taken after those two changes.
- `decisions.md` ("A generic function is re-pointed...") records another consequence of the same fact: a detour of a body is lost after tier-up when tiering is on, and "the host runs with tiering off". So tiering-off protects the generic-function detours too, not only the reflection watch. Any cheaper way must keep that, or `GenericReload` bodies need the same treatment.
- Tests that pin the area: `ReflectionReadSurfaceTests.fs` (lines 95 to 104: `processEnvironment` per choice), `ReflectionReadTrackingTests.fs` (the checkpoint row at line 312), `HotReloadStateOutcomeTests.fs` (line 353: the keep-tiering row that hammers `reflectDrop` up to `hammerCalls = 80` times and expects either `Watching` or `Lapsed` and a fail-closed save), `SageFs.Simulation/ReflectionReadSim.fs` (`Intent.Lapse`, only generated under `KeepTiering`; `Step.LoseEntryWatch`).

What I could not find: any measurement of a lapse in a real host with PGO off and R2R on, and any use of a runtime tier-up event anywhere in the repo.

## Design

Nothing is built until Measure first answers. The design below is the leading candidate, conditioned on those answers.

Candidate C1, tier the six entry points first, then patch. Tiering stays on. During watch start, before `entryWatch` patches anything, the tracker calls each entry path past the runtime's call-count threshold and waits until the runtime reports each of the six methods promoted to its final tier. Only then does it patch. After a method has reached final tier, nothing rejits it again (hypothesis H1, to be measured), so the patch cannot lapse. The existing canary stays as the proof and `checkEntryWatch` stays as the repair.

New choice, one case and no bool:

```fsharp
type TieringChoice =
  | TieringOffWhileWatching
  /// Leave tiering on; promote the six reflection entry points to their final tier before patching them.
  | TierEntryPointsFirst
  | KeepTiering
```

`name` gives `tier-entry-points-first`; `cost` and `consequence` say what it costs in the words the measurements justify; the setting description and the dashboard text read them, so nothing else is edited. `processEnvironment` returns `[]` for it, or `DOTNET_TC_CallCountingDelayMs=0` if M3 shows the delay stalls promotion during startup.

The phase is a pure state machine so it can be simulated. New file `SageFs.Core/Middleware/EntryTiering.fs`, BCL-only:

```fsharp
[<RequireQualifiedAccess>]
type EntryTier =
  | Counting of calls: int
  | Promoted
  | Patched
  /// A promotion seen after the patch: the patch is gone.
  | LapsedAfterPatch

val step : EntryTier -> EntryTierEvent -> EntryTier * EntryTierEffect
// events: Called | RuntimeSaysPromoted | PatchApplied | RuntimeSaysRejitted
// effects: CallAgain | PatchNow | MarkLapsed | NoEffect
```

Invariants: `PatchNow` is only emitted from `Promoted`; `RuntimeSaysRejitted` after `Patched` always emits `MarkLapsed` (fail closed, same as today); a `Patched` entry never returns to `Counting`.

How "the runtime says promoted" is observed is the open question M4. The candidates are the runtime's tiered-compilation events through an in-process `EventListener` held only for the length of the phase, or `System.Runtime.JitInfo` counters used as a coarse "no tier-up in flight". I have not verified the event names or fields on .NET 10 and .NET 11. If no per-method signal exists, C1 is dead, because the only other way to know is a timer, and a timer is a guess about work that is still progressing.

Candidate C2, keep the listener on and re-patch the moment a rejit event for one of the six arrives. The window between the rejit and the re-patch is a window of missed reads, so every rejit would count as a lapse; it removes the dependency on the canary's polling but keeps the restarts. Only if C1 fails H1.

Candidate C3, make tiering-off cheaper instead of avoiding it: widen the R2R list to the assemblies a hot reload session JITs most (found by M1), keep `TieringOffWhileWatching`. Needs no new type. Taken if M1 shows the cost is JIT of non-R2R code.

Wire and clients: the setting's `Description` text changes (it enumerates the choices); the dashboard and VS Code read the setting catalog, so no client code changes. The new value is a new enum member of an existing setting, additive. No MCP tool, no `apiVersion` move. Commit trailer `Plugin: n/a, a new value of an existing setting`. Persistence: configs that hold the old names still parse.

## Tests

RED tests first:

1. `EntryTieringTests`: "WHY - an entry point is never patched before the runtime reports it promoted", asserting `step (Counting 40) Called` does not emit `PatchNow` and `step Promoted PatchApplied` goes to `Patched`. RED: module missing (compile-red).
2. "WHY - a rejit after the patch marks the watch lapsed", asserting `step Patched RuntimeSaysRejitted` is `LapsedAfterPatch` with `MarkLapsed`.
3. `ReflectionReadSurfaceTests` extension: `processEnvironment` for `TierEntryPointsFirst` equals the value M3 chose, and `TieringChoice.all` lists three names with a `cost` and `consequence` each (the existing enumeration test fails until the case has both).
4. Host tier, `HotReloadStateOutcomeTests`: a `tier-entry-points-first` copy of the line-353 row. Hammer the entry points on a real host for many more calls than 80, on both runtimes, and assert the watch never reports `Lapsed`. Today's row tolerates `Lapsed` under `KeepTiering`; this one may not. Uses the existing "wait for the report" loop, which ends on the report changing and not on a sleep.

Simulation: extend `ReflectionReadSim` with a `Tiering` input of the three choices. `Intent.Lapse` is generated under `KeepTiering` and, for the new choice, only by a seeded `RuntimeSaysRejitted` that follows a modelled `Patched` entry (the H1 failure case). New invariants: under `TierEntryPointsFirst` no read is served by an unpatched entry after `Watching` is reported; a seeded rejit fails closed exactly like `KeepTiering`'s lapse (the existing "never Patched while lapsed"). Twin: patch at once without waiting for `Promoted` (what `KeepTiering` does today); the sim must catch it with a lapse during startup.

Property test: any interleaving of `Called`, `RuntimeSaysPromoted` and `PatchApplied` that reaches `Patched` has `RuntimeSaysPromoted` before `PatchApplied`.

The cost gate is a measurement and stays one: `TieredCompilationCostBenchmark.run` gains the new choice and extra columns, results go in `docs/hot-reload.md` with dates. It is not a pass/fail test.

## Steps

1. RED tests 1 and 2, `EntryTiering.fs`. Owns the new file and its test file. Green on the pure machine.
2. Measurements M1 to M5 below, taken on a quiet machine, results appended to this plan's section in `docs/hot-reload.md`. No product change. Parallel with step 1 (file-disjoint: the benchmark file only).
3. Decide from the measurements (the table in Measure first). If closing, the work ends with a docs commit.
4. If building C1: `TieringChoice` case, `processEnvironment`, setting text (`ValueReads.fs`, `ValueReadTracking.fs`, `SessionAgent.fs`). RED test 3. After 1.
5. Wire the phase into `entryWatch`: `ValueReadTracking.fs` (collides with step 4 on that file, so after it). RED test 4.
6. Simulation extension and twin. After 1; file-disjoint from 4 and 5 (`SageFs.Simulation/ReflectionReadSim.fs` only).
7. Make it the default (`TieringChoice.standard`), update `docs/hot-reload.md` and `docs/how-hot-reload-works.md` ("No JIT tax from me turning tiering off" in the where-Microsoft-is-ahead list), and run the whole `--integration-hr` tier before and after.

## Measure first

Run on the machine the other numbers came from (Ryzen 7 5800XT, 16 threads, Linux, .NET 11.0.0-rc.1), idle, taking turns between settings so load drift hits both, with the release-gate not running.

| # | Question | Experiment | What it decides |
|---|---|---|---|
| M1 | What does tiering-off cost the FSI host today, with R2R images and PGO off? | Using `TieredCompilationCostBenchmark`'s alternating pattern, time for each choice: spawn to session Ready; first `send_fsharp_code` round trip; save-to-served p50 from `HotReloadLatency.fs` (today 258 to 306 ms); app start; warm request cost. Five reps each. | If the total difference is under about 0.5 s on session start and under 10 percent on save latency, close the item and update the stale 0.7 s figure. If it is larger, which stage owns it: JIT of non-R2R code (take C3) or something else (take C1). |
| M2 | With PGO off, how many times does each of the six methods compile, and at which tiers? | A hot reload host with `keep-tiering`, `DOTNET_JitStdOutFile` and `DOTNET_JitDisasmSummary=1` (the method `decisions.md` used for generics), 200 calls through each entry path, then 30 s of normal traffic. Count compiles per method and their tiers. | Confirms or kills H1 (at most one promotion, then stable). More than one promotion kills C1. |
| M3 | Does call-counting delay stall promotion during a busy startup? | Time from first call to promotion for the six methods under default settings and under `DOTNET_TC_CallCountingDelayMs=0`, while the host is loading a real project. | If promotion waits on startup quiet for seconds, C1 adds that to session start. Then either the delay env var (and what it costs) or C1 is not cheaper than tiering-off. |
| M4 | Is there a per-method promotion signal? | On net10 and net11, attach an `EventListener` for the runtime provider with the JIT and tiered-compilation keywords for the length of the phase; log what a named method's promotion looks like. Count events per second during startup to price the listener. | If a per-method event exists, C1 is buildable with no timer. If not, C1 is dead and C3 or closing are the only outcomes. |
| M5 | Does a detour of a generic body survive when only the reflection entry points are pre-tiered? | `GenericReloadTests` fixture under `tier-entry-points-first`, hammered. | Generic detours also need the pre-tier treatment, or the new choice cannot be the default. |

Kill criteria, written before the numbers: C1 is abandoned if M2 shows two promotions, or M4 finds no per-method signal, or M3 shows promotion regularly takes longer than the cost it saves.

## Risks and dependencies

- H1 is a belief about runtime internals. M2 is the check, per runtime. A runtime update can change it; the canary and the repair stay for that reason.
- The new choice must not be the default until the hammer row passes on both runtimes at a call count well above the 80 that today's row uses.
- `redefine-a-value-a-handler-renders` makes more patches rest on the reflection watch being exact. Do not land both in the same week; both edit `ValueReads.fs`, `ValueReadSim` and `WorkerMain` neighbours.
- Depends on nothing else in the roadmap. Nothing depends on it.

## Size

S if M1 closes it (a docs commit and fresh numbers). M if C1 survives M2 to M4: a pure machine, a listener phase, one choice case, a sim extension and a host row. L only if the signal does not exist and a different approach has to be found.

## Decisions for Will

1. Run M1 before anything else, and accept closing the item if the cost is small? Recommend yes; the 0.7 s figure predates R2R images and PGO off, and it may already be wrong.
2. If C1 works, make it the default and keep `tiering-off-while-watching` as a setting? Recommend yes, with the three-choice text on the settings panel.
3. Is an in-process `EventListener` during session start acceptable on the host? It adds a runtime event stream to a process that compiles F# for every eval. Recommend yes for the phase only, disposed when all six are `Patched`.
