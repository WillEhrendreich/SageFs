# One rule decides what a worker's report writes into the registry

Roadmap item: `session-status-reads-write-the-registry` (Next, Repl). Plan written 2026-11-08 against master 213fbc10.

## Work order

Written 2026-11-11 against master 8975e898. `SageFs/Mcp.fs`, `SageFs/DaemonMode.fs` and `SageFs.Core/SessionManager.fs` had uncommitted edits in the working tree, so their line numbers below are approximate; grep the symbol.

### 1. Outcome

`get_session_status` and the health probe decide what a worker's report writes into the registry with one pure function, so a status read can never store a transient (`Starting` over a worker that is Ready while its FSI host is replaced in place). Gate: a pure, hand-enumerated property over every (`SessionLifecycleStatus`, `SessionStatus`) pair, in the default suite, saying the read's decision equals the probe's; `ReplLevelSim` with the transient write turned into a twin that still breaks L7 or L8; one `--integration-host` case through the real route. Nothing platform-specific.

### 2. Where the code is today

- The read's rule: `SageFs/Mcp.fs`, `get_session_status`, the `Routable sid` branch (about `:1480-1493`): `ProjectResolution.reconcile targets sessionInfo.ProjectRoles.Length sessionInfo.Status snapshot.Status`, then `ctx.SessionOps.UpdateSessionStatus` when `Reconciled status` differs from the registry. `SageFs.Core/ProjectResolution.fs:54` `earnedReady`, `:68` `reconcile`, which hands everything earned to `SessionLifecycleStatus.ofWorkerReport` (`SageFs.Core/WorkerProtocol.fs:317`). That function maps every report, `Starting` included, and is sticky only on `Faulted` and `Stopped`.
- The probe's rule: `SageFs.Core/WorkerHealthProbe.fs:66` `RegistryUpdate = NoChange | Replace`, `:78` `registryUpdate` (writes a `HostCrashed` in, and `Ready` or `Evaluating` over `HostCrashed` or `Starting`; everything else `NoChange`), `:89` `syncRegistry`. Wired at `SageFs.Core/WorkerPostReady.fs:234`, started from `SessionManager.fs` `startHealthProbe` (about `:1359-1366`).
- The other writers, each a lifecycle event that keeps its own rule: `Mcp.fs` transport failure to `Faulted` (about `:309`), `setSnapshotStatus` (about `:575`), `DaemonMode.fs:721` (the ops record), `SessionManager.fs:1288` (the handler; keeps a known fault reason over an `Unexplained` one), `:552` (worker died).
- Types: `WorkerProtocol.fs:55` `SessionStatus` (8 cases), `:195` `SessionLifecycleStatus` (8 cases).
- Tests: `SageFs.Tests/HostCrashTests.fs:137-168` already pins `registryUpdate` on seven pairs (`Building` and `Restarting` are `NoChange`, `:168`). `WorkerHealthProbeTests.fs` covers `decide` and `run`, not the rule. `ReplLevelSimTests.fs:115,126` are the two cases where a status read left `Starting` in the registry (19 `testCase`/`testProperty` lines in the file today; the plan's 29 was the runner's count at 80b8cc4b).
- Sim: `SageFs.Simulation/ReplLevelSim.fs:70` `Op.StatusReadSaysStarting`, `:108` `RegistryStanding.ReadsStarting`, `:202` `Policy` (a twin is a `Policy` value, `real` at `:464`), `:446` the op's effect; `ReplLevelInvariants.fs:103` L7, `:113` L8; scenarios `ReplLevelGenerators.fs:121,126,131`.
- `docs/decisions.md:497` records that the probe carries status to the registry. Nothing records that a read writes.

### 3. Steps

0. DECISION (blocks step 2). (1) Keep the write, one shared rule. (2) Make the read read-only; `list_sessions` can lag a read by up to 5 s. (3) Leave it. Recommendation: (1). The comment at the read says agents compare `list_sessions` with the status before trusting the REPL, so a lagging registry costs trust; (3) keeps two rules; (1) is a small change plus a property.
1. (pure, RED) `SageFs.Tests/RegistryWriteTests.fs` (new; `<Compile Include="RegistryWriteTests.fs" />` after `WorkerHealthProbeTests.fs`, fsproj `:206`): for every `SessionLifecycleStatus` case (each with a handle where it takes one) by every `SessionStatus` case, the read's decision equals `WorkerHealthProbe.registryUpdate current reported`. Enumerate both unions by hand in the test so a new case is a compile error. Today `reconcile (Ready h) Starting` is `Reconciled (Starting h)` and the probe says `NoChange`.
2. (pure) Give the rule one name both can call (`registryUpdate` already lives in Core; keep it there or move it to a `SageFs.Core/RegistryWrite.fs` listed before `WorkerHealthProbe.fs` in the fsproj). `ProjectResolution.reconcile` keeps its signature and `earnedReady` gate, and when earned returns `Reconciled` only for `RegistryUpdate.Replace`, otherwise the current status. Green for step 1.
3. (edge) The read branch in `Mcp.fs` changes nothing if step 2 is done inside `reconcile`; confirm by reading it and leave it.
4. (sim) `ReplLevelSim`: `Policy` gains `WritesTransientOnRead: bool`, false in `real`; `Op.StatusReadSaysStarting` moves the registry only when it is true; a twin `readWritesTransient` sets it and must break L7 or L8 on the `:121` or `:131` scenarios. `ReplLevelSimTests.fs:151` ("every twin is caught at least once") covers the new twin by construction.
5. (test, integration) One `--integration-host` case: `reset_fsi_session`, `get_session_status` while the host comes back, then `list_sessions`; the registry says what the earned report says and never `Starting` after `Ready` was earned. Wait with `get_session_status wait_seconds` or `/events`, no sleep.
6. (docs) `docs/decisions.md`: "one rule writes a worker's report into the registry", dated, naming `registryUpdate` and the option chosen.

### 4. How to check it

- REPL: a session on `SageFs.Tests`; evaluate `WorkerHealthProbe.registryUpdate (Ready h) SessionStatus.Starting` beside `ProjectResolution.reconcile [...] 1 (Ready h) SessionStatus.Starting` to see the two rules disagree before editing.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Inner loop: `--summary --filter-test-list "registry"` and `--filter-test-list "ReplLevel"`.
- Acceptance: unfiltered `--summary` (CI runs it on net11 and net10), then `--integration-host --summary` for step 5.

### 5. Traps in this repo

- `ofWorkerReport` is sticky on `Faulted` and `Stopped` on purpose (its comment: polling could resurrect a faulted session). Keep that.
- `HostCrashTests.fs:168` asserts `Building` and `Restarting` reports are `NoChange`: the shared rule keeps it.
- No wire change: `Plugin: n/a` unless a status label changes.
- `Mcp.fs` is being edited by other work; rebase before committing step 3 and keep the diff to the one branch.
- Commit `SageFs.Tests.fsproj` whole with the new file.

### 6. Size and order

S: half a day for steps 1 to 4, half a day for 5 and 6. First commit: step 1's RED, once Will has picked option 1.

### 7. Not now

- Making `list_sessions` ask each worker live: a round trip per session per call.
- Removing `earnedReady`: it answers a different question (have the project's roles resolved).

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S to M |
| Branch and worktree | none yet |
| Last green commit | none yet. It starts from `SageFs/Mcp.fs` (the `get_session_status` path), `SageFs.Core/ProjectResolution.fs` (`reconcile`) and `SageFs.Core/WorkerHealthProbe.fs` (`registryUpdate`) as on master. |
| Next action | Answer the decision below, then write the RED case in "Tests": a status read for a worker that reports Starting while the registry holds Ready writes nothing. |
| Decisions made | none yet |
| Surprises | none yet |

## What done looks like

There is one pure function that takes the registry's status and a worker's report and says what, if anything, the registry should hold. The `get_session_status` read and the 5 second health probe both ask it, so they cannot disagree, and a transient the worker reports while its FSI host is being replaced in place (a level or a reset) never replaces a settled status.

## What exists today

Read from the tree on 2026-11-08.

- `get_session_status` writes. After it fetches the worker's report it runs `ProjectResolution.reconcile` and, when the result differs from what the registry holds, calls `UpdateSessionStatus` (`SageFs/Mcp.fs`, near line 1480). The comment there says why: agents compare `list_sessions` with the status before they trust the REPL, so the registry follows the live worker.
- The health probe writes too, through `WorkerHealthProbe.registryUpdate`: a crashed host, a host back after a reset, and (since f382c678) a `Starting` on a worker the registry already declared Ready. Its comment says the probe "is not a second status writer" and names the cause it retires: a status read wrote the worker's own report while the FSI host was being replaced in place.
- The two decide by different rules. `reconcile` writes what the worker reports once Ready is earned (`earnedReady`), so it can write a transient. `registryUpdate` writes only toward settled and leaves a rebuild's `Building` and a restart's `Restarting` to the paths that own them.
- The effect was real. Before f382c678, a status read could leave `Starting` in the registry for a worker that was Ready, and the level's state said Leveled while the daemon held the old host's tests. `RefetchGate.judgeRefetch` and the probe's retirement fixed the symptom. The cause, a read that writes by its own rule, is still there. `ReplLevelSim` carries it as the op `StatusReadSaysStarting`, and the invariants L7 and L8 hold because the probe cleans up after it within 5 seconds.

## Decisions for Will

A read that writes is a design choice and I would rather you make it than find out it was made.

1. Keep the write, with one rule. `get_session_status` asks `registryUpdate` (or a function it shares with it) instead of `reconcile`, so a read only moves the registry toward settled. `list_sessions` stays current the moment an agent reads a status.
2. Make the read read-only. The probe is then the only writer besides lifecycle events, and `list_sessions` can be up to 5 seconds behind a status read. An agent that reads Ready from one and Starting from the other has a reason to doubt the tool.
3. Leave it. The probe cleans up in 5 seconds and the sim says the invariants hold.

My recommendation is 1. It keeps what the comment in `Mcp.fs` wants, and it removes the second rule, which was the cause. `earnedReady` is a different question (whether the project's roles have resolved) and stays where it is, as a gate before the shared decision.

## Steps

1. RED: `HostCrashTests` or a new `RegistryWriteTests` (pure): for each pair of (registry status, worker report), the read's decision equals the probe's. A read for a Starting report over a Ready registry is `NoChange`. Today it is `Replace Starting`.
2. `ReplLevelSim`: `StatusReadSaysStarting` becomes a twin. The shipped path never writes the transient, and the twin that does still breaks L7 and L8, which keeps the invariants honest.
3. Route the read's write through the shared function. Keep `earnedReady` in front of it.
4. Read every other `UpdateSessionStatus` call site (`Mcp.fs` lines 321, 575 and 1489; `DaemonMode.fs` line 715) for a rule of its own, and list what each owns. A caller that is a lifecycle event (a rebuild's `Building`, a reset's status) keeps writing, since the probe's comment already leaves those to the paths that own them.
5. `docs/decisions.md` records the answer.

## Tests

- The pair-wise property in step 1 over every `SessionLifecycleStatus` and `SessionStatus` case, so a new case cannot be added without deciding what a read does with it.
- The existing `ReplLevelSim` cases (29 of 29 at 80b8cc4b) stay green with the twin changed.
- An integration case through the real route: a worker mid-level, a status read, then `list_sessions`, and the registry still says what it said.

## Risks and dependencies

- If the answer is 2, a client that reads `list_sessions` right after `get_session_status` will see the registry lag. The dashboard and sagefs.nvim read `list_sessions`, so check both. No wire shape changes, so `Plugin: n/a` unless the status text does.

## Size

S to M. The pair-wise property is most of it.
