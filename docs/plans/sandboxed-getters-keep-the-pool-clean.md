# A sandboxed getter leaves no filtered thread behind

Roadmap item: `sandboxed-getters-keep-the-pool-clean` (Next, Isolation). Plan written 2026-11-08 against master 213fbc10.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M, once the design is chosen. The choice is Will's. |
| Branch and worktree | none yet |
| Last green commit | none yet. It starts from `SageFs.Core/ThreadSandbox.fs` as it stands on master, with the whole-process cases in a child process (de827187). |
| Next action | Get Will's answer to the decision below, then write the RED case in "Tests": a child process where the pool has no idle worker, a getter that queues pool work, and a read of every thread's `Seccomp:` line afterwards. It fails today with four filtered threads. |
| Decisions made | none yet |
| Surprises | none yet |

## What done looks like

A live-bindings getter that queues work to the thread pool leaves no thread in the daemon's process (or the FSI host's) carrying the seccomp filter after the call returns, and the getter's own I/O is still stopped while it runs. The kernel is the witness: `/proc/self/task/<tid>/status` says `Seccomp: 0` for every thread that is not the sandbox thread.

## What exists today

Read from the tree and from `docs/decisions.md` ("A getter is evaluated on a thread with a syscall filter, and the filter only stops I/O"), measured 2026-11-08.

- `ThreadSandbox.run` puts a getter on a fresh dedicated thread and installs a seccomp filter on that thread first. A thread the work starts inherits the filter. The filter dies with the thread.
- A task the work hands to a pool thread does not inherit it when an idle worker already exists. In a process whose pool has not grown, the pool makes the worker (and its gate thread) from the thread that queued the work, so the new threads are born filtered. A getter that queued pool work left four threads carrying the filter (`Seccomp: 2`, with the sandbox thread's name). On those threads the host's own writes, connections and spawns fail for the life of the process, and a thread cannot be un-filtered.
- Refusing thread creation under the filter is not the fix. The pool does not undo a request whose worker it could not make, never asks again, and in a process with no worker yet every later `Task.Run` waits forever.
- The existing case "the filter does not leak to the calling thread, a pool thread or a later run's thread" (`ThreadSandboxTests.fs`) uses a pool that has already grown, so it cannot see this.
- Until this is decided the evaluator's guards (a deadline and a cap on abandoned threads) are the protection, and the filter stops what it names only on the thread that carries it.

## Decisions for Will

`docs/decisions.md` names three ways out and none of them is free:

1. A supervisor that answers the denied calls per thread, using seccomp user notification. The filter stays on the leaked threads, but a notification for a thread that is not inside a sandbox call can be answered "allow", so a leaked worker behaves like an ordinary one. This keeps the getter in the process with the live objects it reads. I have not tried it: whether `SECCOMP_RET_USER_NOTIF` composes with the filters the host already runs under, and what each notified syscall costs, are the two things to measure first.
2. Run the getter's I/O-reaching work out of process. A getter reads live objects of the process it runs in, so this needs the value moved across a boundary first. I do not see how that works for an arbitrary property.
3. Refuse a getter that queues pool work. Nothing can tell before it runs, so this means running it and then refusing, which is too late.

My lean is the first, because it is the only one that keeps the getter beside its objects. It is a lean and not a finding.

## Steps

1. RED: `SandboxChild` gains a scenario (the `--sandbox-child <scenario> <directory>` shape) that starts with a pool that has made no worker, runs a getter that does `Task.Run(...).Wait()`, and writes every thread's `Seccomp:` line. The parent asserts none is `2` except a thread still inside a call. Commit it red.
2. Measure option 1 in the child: install the notify filter, answer one denied call and one allowed call from a thread outside the sandbox, and time a thousand notified syscalls.
3. Build the chosen design behind the existing `ThreadSandbox.run` signature, so `LiveBindings` and the pane do not change.
4. Rewrite the decisions entry: the "Open" paragraph becomes the decision and its evidence. Grep `docs` for any other line that says the filter does not leak to pool threads.

## Tests

- The child-process case above, in the default tier, since the child runs under one named ceiling (`TestTimeouts.childScenarioCeiling`) and never waits on a pool.
- A twin that carries the old behaviour must fail it, so the case has teeth.
- The existing seccomp interpreter cases stay: the BPF is built by a pure function and run through a small interpreter against a separately written model, and a notify filter needs the same.

## Risks and dependencies

- Linux x86-64 is the only verified platform. Aarch64 is reported unverified and other systems report `NotLinux`, and none of this changes that.
- A supervisor thread is one more thing that can wedge. It owns no state a getter's thread needs, and the getter's caller keeps owning the deadline.

## Size

M once chosen. The measurement in step 2 can move it.
