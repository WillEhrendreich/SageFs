# One rule decides what a worker's report writes into the registry

Roadmap item: `session-status-reads-write-the-registry` (Next, Repl). Plan written 2026-11-08 against master 213fbc10.

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
