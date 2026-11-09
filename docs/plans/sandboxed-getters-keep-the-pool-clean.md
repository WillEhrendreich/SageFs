# A sandboxed getter leaves no filtered thread behind

Roadmap item: `sandboxed-getters-keep-the-pool-clean` (Built, Isolation). Plan written 2026-11-08 against master 213fbc10, closed by 97252b11 the same day.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Done (97252b11). Four things are unverified, listed under "What is still unproven". |
| Size | M |
| Branch and worktree | none; on master |
| Last green commit | 97252b11. The ratchet lane read 554/554 on net11 and net10, and the `ThreadSandbox` list ran 27 of 27 in six quiet runs, four runs under 64 busy loops, and three on net10. |
| Next action | Nothing is required. If a getter ever reads as hung in a real host, look at `Timeouts.sandboxPoolHoldCeiling` first: it frees the pool's limits after at most 15 s. |
| Decisions made | In process, not a supervisor and not out of process. The filter refuses `clone(CLONE_THREAD)` under every policy, the pool is made ready on the caller's unfiltered thread before the call, and the pool's limits are held while the call runs. Details in `docs/decisions.md`. |
| Surprises | Refusing thread creation alone corrupts the pool: `MaybeAddWorkingWorker` raises its counts before it starts the thread, a refused start throws with the counts left raised, and the pool then believes in a thread that does not exist. A child that blocked every worker and queued one more hung to its ceiling in 9 runs of 40. |

## What done looks like

A live-bindings getter that queues work to the thread pool leaves no thread in the process carrying the seccomp filter after the call returns, and its own I/O is still stopped while it runs. The kernel is the witness: `/proc/self/task/<tid>/status` says `Seccomp: 0` for every thread that is not the sandbox thread.

## What shipped

- `Seccomp.denyProgramFor` builds the filter from `ThreadCreation.Refused | Allowed`. `Refused` is what runs. `Allowed` is the old program and stays as the twin that proves the case has teeth.
- `SandboxPool.prepare` makes a worker, the gate thread and the timer thread on the caller's thread before the filter goes on. A call it cannot prepare is refused as `PoolNotReady`, with the reason on the row.
- `PoolHold` (pure, applied by `SandboxPool.Guard` under one lock) pins the pool's maximum at the threads that exist, so the pool wakes an idle worker or leaves work queued and never starts a thread. Overlapping calls share one hold, and a late leave from an earlier epoch does nothing. A hold that outlives the ceiling is released by a timer, so an abandoned getter cannot freeze the host's pool.
- A getter loses two things: `Thread.Start` throws inside it (the call still completes), and the pool cannot grow while it runs.

## Evidence

- Before: after one call the kernel named 5 threads with `Seccomp: 2` on net11. On net10, 12 rounds each followed by 64 pool file writes left 17 threads filtered and failed 768 of 768 writes with "Operation not permitted", 5 runs of 5.
- After: 0 threads and 0 failures.
- The twin without the hold: a busy pool throws `OutOfMemoryException` from `Thread.StartCore`, 12 of 12 on net11 and 6 of 6 on net10. With the hold the same scenario completes and the limits are restored.
- Every real-filter case runs in a fresh child process (`SandboxChild`) under one named ceiling.

## What is still unproven

- The runtime's tiering compiler thread is refused too and nothing here controls it. A getter that got a method promoted completed in every run.
- One earlier build threw an `InvalidCastException` out of a getter, once in about four runs. It has not come back in over 200 child runs and about 30 list runs. It is unexplained and not shown fixed.
- The hold's ceiling timer is proven by the child scenario and the pure epoch cases, not by a long getter in a real host.
- The net10 `facts.txt` error ("Bad file descriptor") was not this leak, since a leak shows as EPERM on a write. The real list passed 6 of 6 on net10 and the cause is unknown.
- aarch64 stays unverified, which is the item [the getter sandbox beyond Linux x86-64](getter-sandbox-beyond-linux.md). No full unfiltered tier run of the new cases has happened outside the gate.
