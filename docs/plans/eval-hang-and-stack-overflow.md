# An eval that hangs or overflows the stack answers in bounded time and says what it cost

Roadmap item: `eval-hang-and-stack-overflow` (Repl, Built). Plan written 2026-11-07 against master 27163dfa. It closes a gap against FsiMcp 0.3.0, which cuts an eval at a timeout and gives each eval a 256 MB thread.

## Resume here

| Field | Now |
|:--|:--|
| Status | Done |
| Size | M |
| Branch and worktree | `cohort/p5-hang`, `/home/will/scratch/cohort/p5-hang` |
| Last green commit | the newest commit that touches this file (`git log -1 -- docs/plans/eval-hang-and-stack-overflow.md`) |
| Next action | Land through the cohort. Then run the packaged tool against the measurements below on a quiet machine and write the timings next to them. |
| Decisions made | 2026-11-07: the default `timeout_seconds` is 120 (FsiMcp's), and a call at the limit cancels the eval instead of leaving it running; the `/exec` route (`sagefs eval`) keeps the worker's 10 minutes because it has no way to ask yet. |
| Surprises | The host crash was already reported (`HostCrash`, `HostCrashed`); what was missing was the word "stack" and the first lines of the runtime's report. The bigger defect was not the overflow: cancelling an eval ended the CALL while the host went on running it. |

## What was measured (2026-11-07, daemon 0.6.940, a bare session on this checkout, machine load average about 50: no timings are recorded, only behaviour and counts)

1. **Eval stack.** The host's eval thread had the process's 8 MB (`ulimit -s 8192`). A plain `let rec f n = if n = 0 then 0 else 1 + f (n-1)` survived 521,875 frames and died at 550,000. FsiMcp gives each eval a 256 MB thread. The runtime ends the whole process on a stack overflow, so the host died with exit 134.
2. **What the caller read after the overflow.** The session went `HostCrashed` (the daemon survived and the session id survived: `hard_reset_fsi_session` started a fresh host under the same id). The message was `the FSI host crashed (exit 134) ... Last output from the host: ... <40 lines of frames>`. The runtime's own lines (`Stack overflow.`, `Repeated 261771 times: at FSI_0004.f(Int32)`) come first in the output, so the 40-line tail had cut them off. Nothing said "stack".
3. **Waiting.** `send_fsharp_code` had no timeout parameter. The only bound was the worker call's 10 minutes (`Timeouts.workerHttpRequest`).
4. **Cancel.** `cancel_eval` had no `session_id` parameter (it routes by `working_directory` only, so two sessions in one directory could not be cancelled by name). Against `while true do ()` it returned in about a second and the session read Ready. Against a native `sleep(600)` it also returned and the session read Ready. In both only the CALL had ended: `AppState.evalFn` registered `Thread.Interrupt` on the worker thread, which was waiting for the host's answer, so the interrupt abandoned the wait. The host's eval thread was still running the loop. The next eval queued behind it and never answered, the session read `Evaluating`, and `hard_reset_fsi_session` was refused because of that state. Only `stop_session` got out. A deep unwind (an exception thrown 500,000 frames down, which is what a stack check that throws does) did the same: the host stayed busy for over two minutes after the cancel and every later call waited behind it.

## What changed

- `Timeouts.evalCall` (120 s), `evalCallCap` (the worker's 10 minutes), `evalCancelGrace` (10 s), `evalCancelAnswer`.
- `SageFs.Core/EvalBound.fs`: `EvalLimit.ofRequest` (what `timeout_seconds` means), `EvalWait.next` (the one decision), `EvalWait.run` (the caller's loop, which uses `next`), `EvalBoundMessage` (what the caller reads). Proven in a SageFs session before it was written to the file.
- `send_fsharp_code` takes `timeout_seconds` (default 120, capped at 600, covers the whole call). At the limit the eval is cancelled and the answer says so. If the eval then does not answer within `evalCancelAnswer` the caller is answered without it and told how to recover.
- `cancel_eval` takes `session_id`.
- A cancel of a session whose FSI is in a host no longer interrupts the worker thread (`AppState.workerThreadMayBeInterrupted`); the cancel reaches the host through the token, and the call ends when the host says its eval stopped.
- `FsiHostSession`: an eval that is cancelled and not confirmed stopped within `evalCancelGrace` has its host killed. The session ends as `HostExit.StoppedAfterCancel` and says what was lost; it no longer sits in `Cancelling` or `Evaluating`.
- `HostExit.OverflowedStack`: `HostOutputTail` keeps the first four lines of the runtime's report apart from the tail, and the session says "stack overflow", names the repeating frame and how often, says the state is gone, and says to make the recursion tail-recursive or loop.
- The host's eval thread has a 256 MB stack (`HostLimits.EvalStackBytes`), the same as FsiMcp. So does the eval thread of an in-process session.

## Several callers, one host (found by the roast of 2026-11-08, fixed on top of the first two commits)

The first version cancelled "whatever eval the host is running". Agent B's eval waiting in the host's queue behind agent A's long, legitimate eval timed out at B's 120 seconds, B's cancel interrupted A's eval, and ten seconds later the host was killed with every client's definitions. A reply that landed as the grace ended was killed too. Now:

- `WorkerMessage.CancelEvalRequest replyId` (`POST /cancel-request`) names the request. The worker keeps the cancellation source of every `EvalCode` in flight by its id (`WorkerMain.inFlightEvals`); the timeout's cancel in `Mcp.fs` carries the id its own `EvalCode` carried.
- `Request.Interrupt` carries the id of the eval request. The host acts through `EvalInterrupts.planInterrupt`: the running eval is interrupted only if the id is its own; a queued one is answered `EvalNeverRan` at once and skipped when its turn comes; an unknown one does nothing (an interrupt that names an eval which just left the queue is remembered and answers `EvalNeverRan` when that eval starts).
- The client kills the host only through `EvalInterrupts.graceExpired`, and the question "is the request still waiting" is answered by taking it off the pending table in one step: a reply that took it first has answered, so there is no window in which a reply and the kill both happen.
- A call that times out while queued is answered with the text the worker gives for `EvalNeverRan` ("queued behind another eval ... never ran and nothing was evaluated") under the timeout note; the other eval is not touched.
- A cancel that itself fails is logged with its exception (`EvalWait.logCancelFailure`), not dropped.
- `cancel_eval` (the tool and the dashboard) still cancels the newest eval the worker received, as before; it is a person's or agent's explicit act on a session, not a timeout.

`SageFs.Simulation/EvalQueueSim.fs` runs several callers against one host with a queue and folds the real decisions; its twins (an interrupt with no request id, a grace the reply does not disarm) are caught. `EvalHangHostTests` has the two-caller case against a real host.

## Tests

- `SageFs.Tests/EvalBoundTests.fs`: the limit, the decision, the wait against a real clock, the messages, the output tail.
- `SageFs.Simulation/EvalBoundSim.fs` with `EvalBoundSimTests.fs`: an eval that finishes, stops when cancelled, or never answers, under the real `EvalWait.next`. Three twins (never cancels, cancels early, waits forever after the cancel) each trip an invariant.
- `SageFs.Tests/EvalHangHostTests.fs` (`--integration-host`, a real host): 4 million frames return; an infinite recursion ends as `OverflowedStack` with the right text and a hard reset recovers; a cancelled wait (an event nobody sets) leaves the session Ready and the next eval runs; a cancelled empty loop ends as `StoppedAfterCancel` within the bound and a hard reset recovers. A native `sleep` is not a fixture: the interrupt's signal ends it early on some runs. Measured: waits stop on cancel and an empty loop does not. A loop calling `Thread.Yield()` was killed on one run and stopped on another, so it is not a fixture.

## Not changed, and why

- A thread the eval starts that never returns is not touched: the eval returns, the thread keeps running in the host, and only a reset ends it. I did not measure what FsiMcp does with one.
- An exception thrown deep in a very deep stack still unwinds slowly (the runtime's cost). A cancel during that unwind now ends in the host being stopped after the grace, not in a hang.
