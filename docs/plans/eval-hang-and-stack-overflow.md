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

## Tests

- `SageFs.Tests/EvalBoundTests.fs`: the limit, the decision, the wait against a real clock, the messages, the output tail.
- `SageFs.Simulation/EvalBoundSim.fs` with `EvalBoundSimTests.fs`: an eval that finishes, stops when cancelled, or never answers, under the real `EvalWait.next`. Three twins (never cancels, cancels early, waits forever after the cancel) each trip an invariant.
- `SageFs.Tests/EvalHangHostTests.fs` (`--integration-host`, a real host): 4 million frames return; an infinite recursion ends as `OverflowedStack` with the right text and a hard reset recovers; a cancelled wait (an event nobody sets) leaves the session Ready and the next eval runs; a cancelled empty loop ends as `StoppedAfterCancel` within the bound and a hard reset recovers. A native `sleep` is not a fixture: the interrupt's signal ends it early on some runs. Measured: waits stop on cancel and an empty loop does not. A loop calling `Thread.Yield()` was killed on one run and stopped on another, so it is not a fixture.

## Not changed, and why

- A thread the eval starts that never returns is not touched: the eval returns, the thread keeps running in the host, and only a reset ends it. I did not measure what FsiMcp does with one.
- An exception thrown deep in a very deep stack still unwinds slowly (the runtime's cost). A cancel during that unwind now ends in the host being stopped after the grace, not in a hang.
