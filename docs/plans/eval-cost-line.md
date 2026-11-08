# A cost line on every slow or allocation-heavy eval

Roadmap item: `eval-cost-line` (Later, Repl). Plan written 2026-11-07 against master 4827ab9a.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress (step 5 of 5) |
| Size | S |
| Branch and worktree | `cohort/p2-eval-output`, `/home/will/scratch/cohort/p2-eval-output` |
| Last green commit | see `git log master..cohort/p2-eval-output`; the red commit is `77e2ca01` |
| Next action | Built and committed on the branch, not landed (2026-11-07): the host measures every eval, the wire carries it, the reply ends with the line past a threshold, a failed eval carries it too. Land through the cohort. The session's timeline now holds each eval's cost and `get_eval_timeline` shows bytes beside duration (a real-bridge case checks both). Left: the cost on the global store the dashboard's push reads (`McpContext.RecordEval` still takes three arguments), the failed-eval status (the timeline records every eval as Succeeded, which predates this item), and the thresholds from a day of history once a daemon has kept one. |
| Decisions made | Bytes are the whole process's (`GC.GetTotalAllocatedBytes false`). The line is written by the daemon from a typed `EvalCost` the host measured, carried in the reply metadata as JSON under `evalCost`. `tracedFsiEval` is left as it is; deleting it is still Will's call. |
| Surprises | The eval reply never carried the host's time: the daemon's stopwatch includes the queue and the wire. The worker forwarded nothing but typed live-testing DTOs in the reply metadata, so adding a typed value meant a new case there. Adding a field to `FsiProtocol.EvalResult` shifted every sample in `SageFs.Tests/fixtures/FsiProtocol/wire-golden.tsv`, which is regenerated. |

## Why this item exists

FsiMcp 0.3.0 ends a slow or allocation-heavy eval with one line, `[0.84 s · 312.4 MB allocated · GCs gen0 14 / gen1 3 / gen2 1]`, shown when the eval took at least 0.5 s or allocated at least 100 MB (read from `src/FsiMcp/Worker.fs`, `Session.Eval`). Its author writes in the docs that allocation volume is a far steadier A/B signal than time, which matches what I see when I time things under load. An agent trying two versions of a function reads the line and knows which one did less, with no extra tool call. SageFs gives the agent no such line.

## What exists today (checked in the tree)

- `SageFs.Core/Instrumentation.fs` collects time and nothing per-eval about memory. The OpenTelemetry histogram `sagefs.fsi.eval_duration_ms` and the counters for evals and statements are in `tracedFsiEval` (line 539), and nothing calls that function: I searched `SageFs` and `SageFs.Core` for a caller and found none. The GC data it exposes is two observable gauges (`sagefs.gc.heap_bytes`, `sagefs.gc.collections_gen2`, lines 241 to 250) of the process that registers them, which is the daemon, not the host that runs the user's code. Allocated bytes per eval and per-generation counts are not collected anywhere.
- The eval actor times every eval (`EvalFinished` carries the `Stopwatch`, `SageFs.Core/AppState.fs:1264`) and uses it for `EvalStats`, the `EvalCompleted` event (`Duration`), the timeline (`DurationMs`) and the P50 and P95 in `get_eval_timeline`. The reply of `send_fsharp_code` does not carry it. The daemon-side time includes the queue wait and the wire, so it is not the host's time.
- User code runs in the isolated FSI host. Its protocol has `EvalResult of id * outcome * diagnostics` (`SageFs.FsiHost/FsiProtocol.fs`), with no field for cost. The host runs every eval on one thread named `fsihost-eval` (`SageFs.FsiHost/Program.fs:346`).

So the gap is real, and it is a smaller one than "nothing is measured": time exists on the daemon and is wrong for this purpose, and bytes and GC counts do not exist.

## What done looks like

An eval that takes at least the slow threshold, or allocates at least the heavy threshold, ends its reply with one line carrying the time in the host, the bytes allocated and the GC counts per generation. A fast, light eval carries no line, so the common reply is no longer. The numbers come from the host process, around the eval only, and the structured result carries the same figures as typed fields. The thresholds have names in `Timeouts` (the time) and a named constant for the bytes. The line is also on a failed eval, because a failure that ran for a minute is where it is most wanted. For the same code, the line's bytes agree with the bytes `profile` reports in allocation mode, within the tolerance that Measure first sets.

## Design

Pure, `SageFs.Core/EvalCost.fs` (no dependency past FSharp.Core): `type EvalCost = { Elapsed: TimeSpan; Allocated: ByteCount; Gen0: int; Gen1: int; Gen2: int }`, `EvalCost.line : Thresholds -> EvalCost -> string option` (`None` below both thresholds), and `describeBytes`. `Thresholds` is a record read from `Timeouts` and one constant, so a test passes its own. One function writes the line, and the dashboard and the tool use it.

Host: `FsiProtocol.EvalResult` gains `cost: EvalCost`. The host reads `GC.GetTotalAllocatedBytes(true)` and `GC.CollectionCount` for generations 0 to 2 before and after `EvalInteractionNonThrowing`, and a `Stopwatch` around the call. The wire is ours and not stable, so the field is added and every in-repo reader updated in the same commit.

Daemon: the eval reply appends the line after the output and before the diagnostics footer. `EvalTimeline` entries carry the cost, so `get_eval_timeline` can show bytes beside duration. The dead `tracedFsiEval` either gets its caller (so the histogram means something) or goes, which is a separate small commit and a decision for Will.

A caveat to measure. `GC.GetTotalAllocatedBytes` counts every thread in the host. The host also runs the live-testing agent, the hot-reload agent and a value walk after an eval, so an eval that overlaps one of those would be charged for it. `GC.GetAllocatedBytesForCurrentThread` counts only the eval thread, and misses what the eval's own tasks and threads allocate. FsiMcp's host runs only the eval, so the problem does not arise there.

## The failing test to write first

`SageFs.Tests/EvalCostLineTests.fs`: `WHY - an eval under both thresholds carries no cost line`, a property over costs below the thresholds. It does not compile today. Then:

2. `WHY - an eval at or over either threshold carries one line with all five figures`, property over costs at and above each threshold, checking the line parses back to the same figures (a round trip, so the test does not compare a hard-coded string).
3. `WHY - the structured result and the text line agree`.
4. `WHY - a failed eval carries its cost`.
5. Outcome gate, `--integration-host` tier: evaluate `Array.zeroCreate<byte> 200_000_000 |> ignore` on a real session and assert the reported bytes are at least 200 MB and the GC counts are non-negative; evaluate `1 + 1` and assert no line. Then evaluate the same allocation while a hot-reload save is landing and assert the line does not charge the save's allocations (this decides process-wide against thread-local).
6. `WHY - the eval timeline stores the cost`, so `get_eval_timeline` and the line cannot disagree.

## Steps

1. Test 1 and `EvalCost.fs`. Names the thresholds in `Timeouts` (a constant says what the wait is for and why that long) and the byte threshold beside it.
2. Host field, protocol change, every reader updated (tests 3, 4, 6).
3. Reply text and the timeline.
4. Outcome gate (test 5), which also settles the caveat above.
5. Docs: `docs/mcp-tools.md` (the line and what it means), the `send_fsharp_code` description gains one sentence (see `small-default-tool-list` for the budget, the sentence is under 100 characters).

Wire impact: `FsiProtocol.fs`, `McpTools.fs`, `SessionOperations.fs` if the structured result changes, `docs/mcp-tools.md`. Commits carry `Plugin: n/a` if no editor reads the eval reply text for a pattern; check `sagefs.nvim` and `sagefs-vscode` for parsers of the reply before choosing.

## Measure first

- Process-wide against thread-local bytes: run the same three evals (a 100 MB array, a list of ten million boxed ints, a string builder loop) under both counters in a host with the live-testing agent idle and then active, and write both figures here. Pick the counter that is stable and honest, or report both.
- Measured 2026-11-07, in an isolated host with nothing else running, the machine under load (bytes do not depend on load, times are not recorded). Process-wide / eval thread, in bytes:

  | Eval | Process-wide | Thread-local |
  |:--|--:|--:|
  | `Array.zeroCreate<byte> 100000000` | 100,000,024 | 100,000,368 |
  | `List.init 10000000 box` | 559,987,384 (35 / 33 / 1 collections) | 560,001,256 |
  | `StringBuilder` loop of 2,000,000 appends | 71,710,040 | 59,774,464 |
  | `Parallel.For`, four tasks of 25 MB | 100,008,296 | 2,296 |
  | `1 + 1` | 0 | 368 |

  For code on the eval thread the two agree. The thread-local counter misses what an eval's own tasks allocate (100 MB read as 2 KB), and the process-wide one charges the eval for whatever else allocates meanwhile (12 MB of 72 MB in the string-builder loop, with the host idle). Process-wide is the one in use, because an eval that fans out is the case where an agent most wants the number. Not measured: the live-testing agent active.
- What overlaps an eval in the host (read from `SageFs.FsiHost/Program.fs`, 2026-11-07): the eval, a hot-reload save (`RunAgentAfterEval`) and the live-values walk all run on the one `fsihost-eval` thread, so they queue behind the eval and are never charged to it. A live-testing test run (`AgentRunTest`) and a debug begin or continue run on the pool and can overlap. A bare isolated session, an eval allocating 10 x 10 MB while a pool task allocated the stated amount at the same time, MB charged to the eval, process-wide / eval thread:

  | Allocated meanwhile by another task | Process-wide | Thread-local |
  |--:|--:|--:|
  | 0 | 99 | 100 |
  | 100 | 189 | 100 |
  | 300 | 200 | 100 |
  | 1,000 | 219 | 100 |

  The overcharge is what the other task allocated while the eval ran, up to the eval's own length, so an eval that overlaps a test run reads high. The line cannot tell the eval's own tasks from someone else's, and neither counter can. It stays process-wide, and this is the case to say when a figure looks too high. A way to remove it is to have the host hold off test runs while an eval is in flight, which is a change to the live-testing agent and not to this item.
- The overhead of the two counters. Expected negligible; check it with the benchmark harness on a quiet machine rather than here, because the machine is loaded.
- The thresholds. FsiMcp uses 0.5 s and 100 MB. Check what share of real evals they would annotate in `get_eval_timeline` history from a day of agent work, so the line stays rare. Too many lines is the cost of this feature.
  - Checked 2026-11-07: there is no history to check against. The timeline is in memory, 1,000 entries per session, and a daemon restart empties it; `~/.SageFs` holds a friction database, the cohort ledger and the session manifest, and none of them keeps eval durations. `get_eval_timeline` on the running daemon answered "No active session" for an anonymous caller. So the thresholds stay at FsiMcp's 0.5 s and 100 MB as a first guess, and the bytes now in the timeline are what a later read of a day's history can use.

## Risks and dependencies

- A line on every reply would cost tokens on all of them. The thresholds are the guard, and test 1 pins that a light eval is untouched.
- Depends on nothing. `profile-tool` reads the same bytes and should agree with it.

## Size

S.

## Decisions for Will

- Wire up `tracedFsiEval` or delete it? Recommendation: delete it, because the timeline already holds what it would record, and a histogram nobody reads is cost.
- Thresholds: take FsiMcp's 0.5 s and 100 MB, or set ours from the timeline history? Recommendation: ours, from the history, with FsiMcp's as the first guess.
