# Eval output shaped for an LLM

Roadmap item: `eval-output-for-an-llm` (Next, Repl). Plan written 2026-11-07 against master 4827ab9a.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress (step 6 of 6) |
| Size | S for head and tail, M for the print limits in both session kinds |
| Branch and worktree | `cohort/p2-eval-output`, `/home/will/scratch/cohort/p2-eval-output` |
| Last green commit | see `git log master..cohort/p2-eval-output`; the red commit is `77e2ca01` |
| Next action | Built and committed on the branch, not landed (2026-11-07): head and tail, print limits in both session kinds, the between-calls output. The bridge-level test (plan test 4, `EvalBetweenCallsBridgeTests`) is written: a real daemon, `sagefs mcp`, a bare session and a thread in the user's code; it ran red with the watch switched off and green with it on. Land through the cohort. Then give `DirectoryConfig` a field for the limits once `session-options` has its shape, and describe a clipped reply in the skill's loop doc. |
| Decisions made | `let` keeps its value under the limits (`EvalPrintSettings.forAgent`); `TypeOnly` is built and tested as `forAgentTypeOnly` and nothing turns it on. Width is 78, not FsiMcp's 140. Between-calls output rides the next successful reply under a label. The eval's own output is bounded first and the block follows it, counted apart (`EvalResultSummary.compose`); a clipped text is bound in the session as `lastEvalOutput` (`OutputHold.bind`) and the notice names `lastEvalOutput.Substring(head, cut)`, which a session test and the bridge test RUN and compare with the cut. This replaced a notice that suggested `(sprintf "%A" it).Substring(headLength, 2000)`, an offset into text that `it` does not hold (roast 2026-11-08). |
| Surprises | The worker never forwarded `resultTruncated`, `resultOriginalLength` or `resultKeptLength` to the daemon (`WorkerMain` copies only typed DTOs from the metadata), so they exist inside the worker and nowhere else; the reply text carries the exact cut. A background thread's output between calls was dropped entirely (the worker's recorder writes to `TextWriter.Null` when no eval records). Width 140 makes FSI print a list in more characters, not fewer. |

## Why this item exists

FsiMcp 0.3.0 shapes eval output for the agent that reads it (source: `src/FsiMcp/Worker.fs`, `Text.fs`): a `let` echoes only `val x: T`, values print under limits (20 items, depth 6, width 140, size 600), long output keeps its head and its tail, output from `printfn` and from background threads is captured and cannot reach the protocol stream, and output written between calls comes back with the next call under a label. Its worker comment says a list of 24 nested records rendered as about 21,000 characters under FSI's defaults, "context the agent pays for on every call". Output is the other half of the token bill, next to the tool list, and a reply the agent has to scroll past is a reply it does worse with.

## What exists today (checked in the tree), and so what remains

Some of this is already built, so the item is smaller than the list above.

- A bound exists: `Features.EvalResultSummary.bound` with `maxResultChars = 4000` (`SageFs.Core/Features/EvalResultSummary.fs`), applied in `AppState.evalFn` (`SageFs.Core/AppState.fs:399`). It keeps the head only, then a notice that says the whole value is still bound as `it` and shows how to slice it. The reply metadata records `resultTruncated`, `resultOriginalLength` and `resultKeptLength`. The module's own header says it came from a 90,000 character result. So the size problem is bounded at 4,000 characters, which is already four times under FsiMcp's worst case. What remains here: it keeps the head and drops the tail, and the end of an output is where an error summary or a final value tends to be.
- The `let` echo is FSI's default. The module's own comment lists `val x: int = 3` as what FSI prints, so values are echoed. I found no override of `ShowDeclarationValues`, `PrintLength`, `PrintDepth`, `PrintWidth` or `PrintSize` anywhere: a search for them in `SageFs`, `SageFs.Core`, `SageFs.FsiHost` and `SageFs.Host` finds nothing, and both sessions are created from `FsiEvaluationSession.GetDefaultConfiguration()` (`AppState.fs:733` for an in-process session, `SageFs.FsiHost/Program.fs:196` for the isolated host). So printing limits are FSI's own, and a large value costs up to the 4,000 cap on a `let`, every time.
- Protocol cleanliness: the stdio bridge's stdout belongs to one writer (`StdoutWriter`, `SageFs/McpStdioBridge.fs`), and the daemon and the FSI host talk over HTTP and TCP. The host redirects `Console.Out` and `Console.Error` into events (`EventWriter`, `SageFs.FsiHost/Program.fs:188`). So `printfn` from user code or a background thread cannot write into the MCP stream by construction. That part of the item is not a gap.
- Output between calls: during an eval the actor swaps `Console.Out` for a capture (`evalFn`). Output that a background thread writes between evals goes to the recorder's underlying writer, which feeds the dashboard transcript. I did not trace whether the agent's next reply carries it. The first test answers it.
- A failed eval: SageFs keeps earlier definitions and discards the failed statement, per `;;` transaction, and says so in the tool description. FsiMcp discards the whole interaction and says so in its reply. These are different semantics, and neither is a gap.

## What done looks like

A reply to `send_fsharp_code` that is longer than the bound keeps its head and its tail with a line between them that says how many characters were cut and how to read the rest (`it` is still bound, as today). A `let` of a large value echoes its type and a bounded value, because the session prints under limits (list length, depth, width, total size) set once for both session kinds. Output a background thread writes between calls comes back with the next reply, under a label, or the plan says why it cannot and where it goes instead. The dashboard's transcript is not made worse for a human: the limits apply to what FSI prints, the dashboard shows that, and a person who wants more evaluates the value. A measured baseline and after, in characters and tokens, on the same inputs, is in this plan.

## Design

Pure, in `EvalResultSummary.fs`: `bound` becomes head-and-tail (`maxResultChars` split, two thirds to the head as FsiMcp does, which is a proposal; the split is a named constant), keeping the notice and the metadata. Total, never throws, never returns more than the bound plus the notice.

Print limits: a small record `PrintLimits` (length, depth, width, size) in a file with no dependency past FSharp.Core, compiled into both `SageFs.Core` and `SageFs.FsiHost` (as `DirectoryConfigTypes.fs` already is), and one `FsiEvaluationSessionHostConfig` subclass that applies it, used at both creation sites. The numbers are named, and FsiMcp's (20, 6, 140, 600) are the first guess. Whether a `let` echoes only its type is a separate setting, off by default until measured, because it removes information some agents use and the dashboard shows the same text. A `DirectoryConfig` field can override the limits per directory (see `session-options`).

Between-call output: decided by the first test. If it is dropped from the agent's reply today, a bounded buffer in the eval actor (a `TailBuffer`, which `HostCrash` already uses) is flushed into the next reply under a label.

## The failing test to write first

`SageFs.Tests/EvalOutputShapeTests.fs`: `WHY - a clipped result keeps the start and the end of what was printed`. A property over generated text longer than the bound: the result begins with the text's first characters, ends with its last, is within the bound plus the notice, and says the cut length exactly. It runs against `EvalResultSummary.bound` today and fails, because that keeps only the head. Then:

2. `WHY - text at or under the bound comes back untouched` (already holds, kept as the guard).
3. `WHY - the limits are applied by both session kinds`: a record-list value of known size, printed through an in-process session and through a real isolated host, comes back no longer than a bound derived from `PrintLimits`, for both. Twin: a host that ignores the config, which fails it.
4. `WHY - a background thread's output written between two evals is in the second reply under a label, and the stdio stream stays valid JSON-RPC`. Drive the real bridge over stdio with the SDK's client, reading until the response id (`AGENTS.md`: never a hand-rolled client). Settles the open question above.
5. `WHY - the structured result says whether the reply was cut and by how much` (the metadata exists; this pins it for head-and-tail).

## Steps

1. Baseline: measure what a nested record list, a 10,000 item list and a long string cost in characters today, from the reply metadata `resultOriginalLength` (the bound hides the size, the metadata keeps it). Fill "Measure first".
2. Test 1 and head-and-tail. Commit red, then green.
3. `PrintLimits` and the config subclass at both creation sites (test 3).
4. Between-call output (test 4).
5. The type-only echo, only if the baseline says the echo is the bulk of the bytes. Otherwise leave it and write the reason here.
6. Update `send_fsharp_code`'s description and the skill's loop doc for what a clipped reply looks like.

Wire impact: `AppState.fs`, `EvalResultSummary.fs`, `SageFs.FsiHost/Program.fs`, maybe `McpTools.fs`. If the metadata fields change, `Plugin: done` or `Plugin: n/a` after reading how `sagefs.nvim` and `sagefs-vscode` read the reply.

## Measure first

- Characters and tokens for three fixed evals (24 nested records, a 10,000 element list, a 50,000 character string), today and after, from `resultOriginalLength` and the kept length. The tokens by the method named in `small-default-tool-list` (the API counter for exactness, characters over four for an estimate).
- What the dashboard transcript shows for the same evals under the new limits. Compare with today's by eye, and record the screenshot headless.
- Whether the same session's other clients (editors, `sagefs call`) read the reply text for values. Check `sagefs.nvim` and the VS Code extension before touching `let` echo.

### Measured 2026-11-07

Daemon 0.6.940-local, a bare isolated session, the machine under load (no timings taken). Characters of what FSI printed, from a session and from `EvalOutputShapeTests`; tokens are characters over four.

| Eval | FSI's defaults (before) | Agent limits, value kept (now) | Agent limits, type only |
|:--|--:|--:|--:|
| `let` of 24 nested records | 4,521 (about 1,130 tokens) | 781 (about 195) | 25 |
| `let` of a 10,000 item list | 436 | 100 | 19 |
| `let` of a 50,000 character string | 110 | 110 | 28 |
| bare expression, 24 nested records | 4,517 | 3,770 (20 of 24 items) | not applicable |
| bare expression, 50,000 character string | 50,023, clipped to its first 4,000 | clipped to the first 2,666 and the last 1,334 | not applicable |

- The reply bound already held a reply to 4,000 characters plus a notice, so what shrinks is the cost of a `let`, and what changes for a clipped reply is that its end survives.
- Width 78 (FSI's own), not 140. Twenty records took 3,770 characters at width 78, 5,690 at 100 and 7,436 at 140: the layout puts several records on a line and pads each continuation with spaces. Size 300, 600 and 1,200 changed the length by 5 characters, so the item count is what bounds a list.
- Type-only echo: it would save 756 more characters on the 24-record `let`, and it takes `= 5` out of the editors' ghost text and the dashboard. `sagefs.nvim`'s `parse_bindings` reads both shapes. The VS Code ghost text shows the first line of the reply, so it would lose the value. So the default keeps the value, and `forAgentTypeOnly` waits for a per-directory field.
- Between calls: before this change a background thread's `printfn` three seconds after a call did not reach the next reply. The worker's recorder writes to `TextWriter.Null` when no eval is recording. Now the isolated host's output between evals is kept (the last 2,000 characters) and the next successful reply ends with it under `[output written since your last call]`. Watching starts at the first eval, so warm-up's own output is not carried. The stdio-bridge test is not written.

## Risks and dependencies

- Print limits change what a human sees in the dashboard output. So the limits come first, and the type-only echo waits for evidence.
- Two creation sites must agree. Test 3 is the guard.
- Depends on nothing. The cost line (`eval-cost-line`) appends after the shaped output.

## Size

S for head and tail, M for the limits in both kinds.

## Decisions for Will

- Print limits on by default for every session, or per directory only? Recommendation: by default, with a `DirectoryConfig` override, because the default agent is the reader that pays.
- Type-only `let` echo: yes, no, or only after the baseline? Recommendation: after the baseline.
