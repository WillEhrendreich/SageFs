# Sessions learn the right load order

Roadmap item: `sessions-learn-the-load-order` (Later, REPL). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Stopped: the premise was not borne out. The measurement under "Measure first" found no project whose opens need a second pass, so a learned order would change nothing today. |
| Size | S to M, if it were ever picked up again |
| Branch and worktree | Not merged. The code (steps 1 to 6, green, with its tests) is kept on the branch `archive/2026-11-07/rescue-load-order`, cut from `rescue/load-order` at 255bbe94 on top of master 1f4daeee. |
| Last green commit | 255bbe94 on that branch (steps 1 to 6 green; step 7, the sim, and step 8, the host-tier gate, were never started). |
| Next action | None. Pick the branch up only if a project turns up whose opens need a second pass, and run the measurement on it first. |
| Decisions made | 2026-11-07, Will: the code does not land. The measurement found nothing for it to fix, and it would add a store file, a schema bump and a wired call in `AppState.fs` for no effect. The measurement and this note land; the branch stays. |
| Surprises | The one project whose retry fires, `SageFs.Simulation`, fails on a discovery gap that learning cannot fix (see the run below). |

## What done looks like

A project whose warmup needs more than one pass to open its namespaces opens them in one pass on the second start. The user sees the same Ready session sooner, and `warmup-context` reports every name opened on the first pass with no single-name fallback evals. The outcome gate is a host-tier test that starts a real worker twice on a fixture with a hidden open-order edge and compares the count of FSI evals warmup made (not the wall time) across a matrix: first start, second start unchanged, second start after a source edit that adds a name, second start with the memory file deleted, second start with the memory file corrupt, each at batch size 8 and batch size 1. Every cell must end Ready with the same set of names opened.

Step 1 is a measurement that can kill the item. If no project we have ever needs a second pass, the premise is false and the plan stops after step 2.

## What exists today

The roadmap sentence says projects "load in the wrong order and retry". I found two orderings in the code, and only one of them retries.

Reference order (done). `ProjectLoading.topoSortByProjectReferences` (`SageFs.Core/ProjectLoading.fs:1293`, private) sorts a solution's projects so each comes after its `ReferencedProjects`, and `solutionToFsiArgs` (same file, line 1365) uses it to order the `-r:` flags. The doc comment records why: a dependent DLL referenced before its dependency made FCS resolve `SageFs.Tests.EvalTimelineTests` wrongly. `ReferencedProjects` comes from Ionide's load of the MSBuild graph, so "order by MSBuild's project graph first" already holds for DLL references.

Open order (not ordered by the graph, and the only retry). `discoverWarmupReplayPlan` (`SageFs.Core/AppState.fs:447`) builds the names to open in this sequence: `open X` lines scanned from source files in the order `sourceFilesForSolution` returns them (`WarmupReplayCache.fs:131`), then root namespaces and public top-level modules reflected from each project in `for project in sln.Projects` (Ionide order, not topologically sorted), all deduplicated first-wins through one `HashSet`. `AppState.fs:918` then calls `WarmUp.openWithRetryRichBatched 5 WarmUp.DefaultOpenBatchSize` (8). Inside `openWithRetryRichCore` (`SageFs.Core/WarmUp.fs:212`) a round tries every remaining name; a name that fails is retried in the next round; the loop stops when a round opens nothing or after 5 rounds. `openWithRetryRichBatched` (`WarmUp.fs:325`) evals each chunk of 8 as one script, and when the chunk fails it evals each name of the chunk on its own. A chunk with one bad name therefore costs 1 failed batch eval plus 8 single evals, and the bad name costs one more eval per later round.

What the retry records. A failure keeps `RetryCount` (`WarmupOpenFailure`, `WarmUp.fs:101`). A success keeps no round: `OpenedBinding` is `Name, Kind, Source, DurationMs`. So the daemon cannot tell today which names needed a second pass.

What the cache keeps. `WarmupReplayCache.ReplayPlan.NamesToOpen` stores the order discovery produced (`WarmupReplayCache.fs:57`). It never learns from the retry. Its key is `Fingerprint` (schema 5, stamps of every source file, hashes of project files), so any source edit discards the whole plan, and anything learned would be lost with it unless stored apart.

What I could not find. A retry at the level of loading whole projects into a session, other than the open retry above. If the roadmap author means a different retry, this plan does not cover it (see Decisions).

Neighbours worth knowing. `StartLedger` / `MachineProfile` (`SageFs.Core/StartLedger.fs`, `MachineProfile.fs`) already show the house pattern for "remember what this machine taught us, between runs, fail open": a pure fold plus a small file in the data directory. `SageFs.Simulation/StartLearningSim.fs` is the sim with the same shape (real code as subject, a twin that never learns). `WarmupCacheGitIgnoreTests.fs` pins what SageFs may write into a project's `.SageFs/` and lists the generated files, so a new file there must be added to it.

## Design

The unit of learning is a rank per name: how many passes the name needed. Absence means rank 1.

```fsharp
/// 1 is the first pass. OpenRound.ofInt refuses zero and below.
type OpenRound = OpenRound of int

[<RequireQualifiedAccess>]
type OrderMemory =
  | NothingRemembered
  | Remembered of ranks: Map<string, OpenRound>
  | Unreadable of reason: string

module OpenOrder =
  /// Stable sort by (rank, original index). A name with no rank is rank 1.
  /// Unreadable behaves as NothingRemembered and the reason is logged, never hidden.
  val arrange : OrderMemory -> (string * OpenableKind) list -> (string * OpenableKind) list
  /// rank' = rank + (observedRound - 1). Ranks never fall, so a name that only
  /// opened on the first pass because it was placed late stays placed late.
  /// Only names that opened are kept; a name that no longer exists is dropped.
  val learn : OrderMemory -> opened: Map<string, OpenRound> -> Map<string, OpenRound>
```

Why the rank never falls: if the rule were "rank = the round observed", a name placed late succeeds on round 1, its rank drops to 1, the next start places it early, it fails again, and the order flip-flops forever. The sim twin below reproduces exactly that, so the invariant has teeth.

Why this is the right edge model: a name that fails on pass 1 failed because a name later in the list had not been opened yet (a relative `open Inner` written inside `namespace Outer`, which resolves in the source file and not at FSI's top level until `open Outer` has run). No project graph contains that edge. Observed rounds are the edge, compressed to a topological level. A dependency chain of any depth converges in one learning start, because the observed round of the deepest name already carries the depth.

Pure core and edge.
- `WarmUp.openWithRetryRichBatched` stays as it is and becomes a thin wrapper over a new `openWithRetryRichBatchedTraced` that also returns `Rounds: Map<string, OpenRound>` and `OpenEvals: int` (every `EvalInteractionNonThrowing` call, batch or single). `OpenedBinding` is not touched, because it is on the wire (`PluginContractTests`, `SseEvent.fs`, `sagefs-vscode`'s `SageFsClient.fs`) and has about ten construction sites in tests.
- `SageFs.Core/OpenOrder.fs` (new, compiled before `AppState.fs`): the types above, `arrange`, `learn`, and `OpenOrderStore.load / trySave` using a `SageFs.Json` profile (JSON goes through `SageFs.Json`, never a hand-built `JsonSerializerOptions`). The file is `<project>/.SageFs/warmup-open-order.json`, next to the replay cache, written with the same temp-file-then-move as `WarmupReplayCache.save`. A missing file is `NothingRemembered`, a file that does not parse is `Unreadable reason`.
- `AppState.fs` is line-budgeted (its own comment says so), so the edge is one call: `OpenOrder.arrange memory (namePairs replayPlan)` before line 918 and `OpenOrder.learn` plus `trySave` after. Put the closure that wires them in `OpenOrder.fs`, not in `AppState.fs`, and run the ratchet lane to see the budget.
- Discovery order (the "MSBuild graph first" half). Make `topoSortByProjectReferences` public as `ProjectLoading.orderByProjectReferences` and iterate it in the `for project in sln.Projects` loop of `discoverWarmupReplayPlan`. A project's namespaces then come after the namespaces of the projects it references. This changes what discovery puts in a plan, so `WarmupReplayCache.SchemaVersion` goes 5 to 6 (its own comment says to bump on exactly this kind of change).
- `.gitignore` handling: `WarmupCacheGitIgnoreTests` lists what SageFs writes in `.SageFs/`; add the new file there and wherever the generated `.gitignore` text is built.

Wire and clients. None. The ranks stay in the project's `.SageFs/` and the logs; `warmup-context`, SSE and MCP are untouched, so `apiVersion` stays 4 and no commit touches a wire file. If step 7 reports rounds through `warmup-context`, that is an additive field and then the `Plugin:` trailer is needed (`n/a`: clients ignore unknown fields; sagefs-vscode parses `warmup-context` leniently, which I did not verify field by field).

Persistence. One new small JSON file per project. No binary format, no schema migration of existing files. Token cost: no MCP tool added.

## Tests

RED tests, in order, all in the default suite unless marked.

1. `WarmupBatchedOpenTests.fs`: "traced opener reports the round each name opened in". An opener that fails `B` until `A` has opened, list `[B; A; C]`. Expect `Rounds`: `B = 2`, `A = 1`, `C = 1`; `OpenEvals` equals the number of batch and single calls made. Compile-red until the function exists.
2. `OpenOrderTests.fs` (new): "arrange puts a rank 2 name after every rank 1 name and keeps the rest in order" (example), then properties with FsCheck: arrange is a permutation of its input; arrange with `NothingRemembered` or `Unreadable` is the identity; arrange is stable within a rank.
3. `OpenOrderTests.fs`: "learn never lowers a rank" (property over any memory and any observed rounds); "learn drops names that did not open"; "a name observed on round 1 at rank 2 keeps rank 2" (the flip-flop guard).
4. `OpenOrderTests.fs`: store round trip through a temp directory (path built with `Path.Combine`), missing file gives `NothingRemembered`, garbage gives `Unreadable`, a save over a read-only directory returns an error and does not throw.
5. `ProjectLoadingTests.fs`: "discovery orders namespaces dependency-first". Three `ProjectOptions` whose Ionide order lists the dependent first; expect the dependency's names first. Needs the public `orderByProjectReferences`.
6. `WarmupReplayCacheTests.fs`: schema 6 invalidates a plan written at schema 5 (extend the existing schema test).

Simulation (`SageFs.Simulation/OpenOrderSim.fs`, `OpenOrderGenerators.fs`, `OpenOrderInvariants.fs`, registered in `SageFs.Simulation.fsproj` near `WarmupSim.fs`; the test list in `SageFs.Tests/OpenOrderSimTests.fs`, same shape as `WarmupSimTests.fs`).
- Pool: names 1..40; a hidden DAG of "needs" edges over them (the generator builds a DAG, so it never emits a cycle).
- Fake FSI: a name opens when its needs are open; a chunk eval succeeds only if every name in it can open in order; the model counts evals.
- Ops, as seeded data: Start, EditAddName (with edges), EditRemoveName, EditAddEdge, DeleteMemory, CorruptMemory, Restart.
- Invariants: (I1) after a Start on unchanged edges, the next Start takes 1 round and 0 fallback evals; (I2) every Start opens exactly the set of names that are openable, none lost, none duplicated; (I3) a surviving name's rank never falls while its edges are unchanged; (I4) a Start after EditAddName costs at most 2 rounds and the one after it costs 1; (I5) `Unreadable` and `NothingRemembered` produce the same opens; (I6) rounds stay within the configured maximum.
- Twins that must be caught: `learnByObservedRound` (rank := observed round, violates I1 on alternating starts) and `neverLearn` (violates I1). The test asserts each twin produces a violation for some seed, so a weakened invariant fails the suite.
- Faults from `FaultInjection.fs`: drop and duplicate the Learn step (a crash between opens and save), reorder Start and Edit.

Outcome gate (host tier, `Integration.hostList`, so it joins `--integration-host` by registration; new `SageFs.Tests/OpenOrderOutcomeTests.fs` modelled on `WarmupOutcomeIntegrationTests.fs`). Fixture `SageFs.Tests/fixtures/OpenOrderFixture/` with `namespace Outer` in one file and, in a second file listed first, `namespace Outer.Sub` that opens `Inner` relative to `Outer` (the edge nothing but the retry sees). Step 0 of the work is to confirm in a REPL session that FSI top level rejects `open Inner` before `open Outer` and accepts it after; if it does not, find a fixture that does before writing the test. Matrix as above; the assertion is on the eval count and the opened set read from `warmup-context`, never on elapsed time.

No wall-clock waits. The host test waits on the session reaching Ready through the existing ready-wait path (`ReadyWait.fs`, the `Integration` helpers), which ends on the state transition. The sim has no clock at all.

## Steps

1. Measure first (see below). Output: a table in the PR description. Decide go or stop. Files: temporary logging only, not committed.
2. RED test 1, then `openWithRetryRichBatchedTraced` in `WarmUp.fs` with the old function as a wrapper. Green on its own. Also fix the cumulative `sw.ElapsedMilliseconds` in the "Opened %d/%d" log line (`AppState.fs`, it prints time since the start of `createFsiSession`, not the opens phase) only if `faster-time-to-ready` has not already.
3. `OpenOrder.fs` with `arrange` and `learn`, RED tests 2 and 3 first. File-disjoint from step 5.
4. `OpenOrderStore`, RED test 4, gitignore list and `WarmupCacheGitIgnoreTests`.
5. Public `orderByProjectReferences`, discovery loop, schema 6, RED tests 5 and 6. Owns `ProjectLoading.fs`, `WarmupReplayCache.fs`, and the discovery function in `AppState.fs`.
6. Wire `arrange`, `learn` and save into the open phase (`AppState.fs` plus the closure in `OpenOrder.fs`). After 3, 4 and 5 land, because it touches `AppState.fs` too.
7. Sim files and `OpenOrderSimTests.fs`. Can start after step 3 fixes the types; owns only `SageFs.Simulation/OpenOrder*.fs` and the one test file.
8. Host-tier fixture and outcome gate. Needs 6.
9. Docs: `docs/fsi warmup.md` and `docs/warmup-design-next.md` (their status notes), and the roadmap line. Do not edit `scripts/roadmap/RoadmapItems.fs` except through the roadmap's own process.

Parallel: 3 with 5; 7 with 4 and 5; 8 with 7. Serial: 2, then 6.

## Measure first

Question: how often does a real project need a second pass, and what share of the opens phase do the fallback evals take?

Experiment. After step 2's traced function, log one line per warmup: names, rounds used, `OpenEvals`, evals that were single fallbacks, and the wall time of the opens phase (a `Stopwatch` around the call, not the cumulative `sw`). Create a session, once cold and once with the replay cache hit, on: `samples/counter`, `SageFs.Simulation`, `SageFs.Core`, `SageFs.Tests`, and `~/Work/SageTech`. Read the lines from the worker log (`<data dir>/workers/<session>.log`) and from `warmup-context`.

Reading it. If rounds is 1 and fallback evals is 0 on all five, stop: the item solves nothing we have. If fallback evals plus later rounds are under 5 percent of the opens phase, keep steps 2 and 3 as instrumentation and drop the rest. If they are over 15 percent on any real project, go. These cut-offs are my judgment, not a measured threshold. The 5.5 s and 12 s cold numbers in `docs/repl-loop-benchmarks.md` carry no open breakdown, so I have no basis today to say which side we are on.

### Run 2026-10-26, agent-load-order

Ran in the item worktree at master 1cbf8fc3, with one session per project and the worker logs and replay caches those sessions wrote. The traced function from step 2 did not exist yet and `WarmUp.fs` was outside this agent's file set, so rounds were read from the worker log's batch-failure lines and from checking every plan name against what FSI top level can resolve.

| Project | Plan names | Rounds | Eval calls | Fallback singles | Names that never open | Relative-open edges in sources |
|:--|--:|--:|--:|--:|--:|--:|
| samples/counter | 5 | 1 | 1 | 0 | 0 | 0 of 1 file |
| SageFs.Core | 123 | 1 | 16 | 0 | 0 | 0 of 333 files |
| SageFs.Simulation | 198 | 2 | 44 | 16 | 2 | 0 of 142 files |
| SageFs.Tests | not sessioned, no build in this worktree | | | | | 0 of 978 files |
| ~/Work/SageTech | not measured, outside this agent's workspace | | | | | |

The only project whose retry fires is SageFs.Simulation, and its cause is a discovery gap, not an order problem. Two names, `ConfirmationGateSim` and `ReflectionReadSim`, are bare opens of modules that live directly inside `namespace SageFs.Simulation`, scraped from indented `open` lines inside sibling nested modules. Discovery adds only first-segment root namespaces (`SageFs`), so `SageFs.Simulation` never enters the plan and the bare names can never resolve at FSI's top level. They fail every start, each poisons one chunk of 8, and the measured cost is 19 wasted eval calls out of 44 (about 43 percent of the eval calls, about 23 percent of the opens phase by the log's own durations) plus two warning lines on every start. Learning the order cannot fix them: a name that never opens never earns a rank, and the plan never contains the namespace that would make it resolvable.

No project in this tree has this plan's true second-pass edge (a bare relative namespace open, `open Inner` in a file under `namespace Outer`, resolvable only after `open Outer` has run). A static scan found zero such edges in 1,454 source files across the four in-repo projects, and the live check showed every plan name in SageFs.Core and samples/counter resolved on its first pass.

Reading, against this plan's own cut-offs: no project needs a second pass that learning could fix, so wiring the learning into the open phase would change nothing measurable today. The waste that does exist has two candidate fixes, both in files outside that agent's set: `OpenReplay.resolveWarmupOpens` could drop bare names that can never resolve at top level (one more `DroppedOpenReason` case), or discovery could add full namespace paths to the plan (which needs the schema 6 bump from step 5). Either removes the Simulation waste outright; the second also turns relative namespace opens into round 2 successes that learning could then rank.

### Outcome

Stopped here. The premise (a second pass that a learned order would remove) was not borne out on any project I could measure, so the item stays unbuilt and the code stays on `archive/2026-11-07/rescue-load-order`. The Simulation waste above is real and small (two names, two warning lines per start) and has its own two fixes, which belong to whoever next works on `faster-time-to-ready`, not to this item.

## Risks and dependencies

- The premise may be false on our projects (step 1 exists for that).
- A stale rank is harmless: opening a name later never breaks an open that succeeded earlier. The file could grow, and `learn` bounds it by keeping only names present in the current plan.
- `AppState.fs` budget ratchet: the wiring must fit. Check with `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Shares the opens loop, `WarmUp.fs` and the AppState warmup region with `faster-time-to-ready`. Land that plan's timing fix and `WarmupStageTimes` first, then this one, or the two collide in the same 100 lines.
- Depends on nothing else. Nothing depends on it, though a precompiled-opens experiment in `faster-time-to-ready` would reuse the learned order.

## Size

S to M, about a week if step 1 says go, because the pure core is small and the cost is in the sim, the fixture and the schema bump. A stop at step 1 costs a day.

## Decisions for Will

1. Is the retry you mean the open retry in `WarmUp.openWithRetryRichCore`? I found no project-level retry. Recommendation: treat it as the open retry, and if there is another one, name the log line.
2. Where should the memory live: the project's `.SageFs/` next to the replay cache, or the data directory? Recommendation: `.SageFs/`. Both describe one project, and the gitignore test already guards that directory.
3. May step 1 end the item? Recommendation: yes, with the 5 and 15 percent cut-offs above. Answered 2026-11-07: yes. The measurement ended it.
