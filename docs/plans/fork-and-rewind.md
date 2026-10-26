# Fork a cohort or a session at a point

Roadmap item: `fork-and-rewind` (Exploring, Agents). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M for the offline cohort fork |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a fork departs every member and seats the forker. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

Two verbs, built in this order.

`fork_cohort`: pick a ledger sequence number from a cohort (live, or from a file made by `sagefs cohort-export`), and get a new cohort whose history is the first N entries. Every member of the fork is departed, every claim they held is orphaned, any landing that was in flight is blocked with a stated reason, and the person who forked holds the conductor seat. From there new commands run through the same `decide`. The fork never touches the parent.

`fork_session`: replay one session's recorded cells up to a cell index into a new session in a new worktree. The result lists every cell as replayed, replayed with a different output, failed, or possibly side-effecting, and says so up front. It never reports a clean fork it cannot support.

The outcome gate runs both against real state. For cohorts it forks every checked-in fixture in `SageFs.Tests/cohorts/` at every sequence number and checks the fork's frame equals `CohortScrubber.frameAtSeq` of the parent at that number, then continues the fork with a different tail. For sessions it replays a recorded session and checks the report against the cells that were deliberately made effectful. Matrix: {fork at 0, at a middle seq, at the head, past the head} x {ledger with a landing in flight at the fork point, without} x {conductor present, vacant}; and for sessions {pure cells, a cell reading the clock, a cell writing a file, a cell that failed originally, a cell evicted from history}.

## What exists today

- `Cohort.replayHeadIn`, `replayAuditIn` (`SageFs.Core/Cohort.fs`, near line 1929) fold a ledger; `replayAuditIn` also reports `Dropped` and `Diverged` entries. The `Cohort.fs` header says plainly that `fork` is not built.
- `CohortScrubber.ledgerThroughSeq` and `frameAtSeq` (`SageFs.Core/Features/CohortScrubber.fs`) already take the prefix of a ledger and project it. A fork is that prefix plus one more entry.
- The ledger is portable: `CohortLedgerExport.toJsonl`/`fromJsonl` (`SageFs.Core/Features/CohortLedgerExport.fs`) write whole rows with entropy, `sagefs cohort-export` (`SageFs/CohortExportCommand.fs`) writes them offline, `sagefs play` (`SageFs/CohortPlay.fs`, `Program.fs` near line 136) reads and renders them, and `CohortPlay.scopeOfLedger` refuses a file that mixes cohorts. Four fixtures sit in `SageFs.Tests/cohorts/`.
- Rows are pruned after `DataRetention.cohortLedgerRetention`, 7 days by default (`SageFs.Core/Timeouts.fs` near line 907), so a fork of an old cohort needs an export taken in time.
- Every command carries its cohort scope and `decide` refuses a mismatch (`checkScope`, `CohortError.WrongCohortScope`). The ledger store keys rows by `Cohort.scopeOf entry.Command`, with `PRIMARY KEY (scope, seq)` (`SageFs/CohortLedger.fs`). So a fork's rows must be rewritten to the fork's scope. `scopeOf` is an exhaustive match; the inverse does not exist.
- `Join` binds the conductor only from `NeverBound`. A `Vacant` seat is never refilled by a join or a delegation, and `docs/mcp-tools.md` says "nothing in the tool surface fills one". A fork that departs everyone would be vacant with nobody able to run it.
- The integration binding is one process-wide ref: `McpCohortIntegration.cohortIntegrationRef : CohortIntegrationBinding option ref`, with fixed paths `cohort-integration` and `cohort-trunk` under the data dir. A second live cohort with an integration setup would overwrite it.
- Sessions: eval history is `EvalStore` (`SageFs.Core/Features/EvalStore.fs`), per session, in memory, capped at 10,000 cells and 64 MB (`HistoryCap.StandardCells`, `ByteBudget.StandardBytes`). An entry is `{ CellIndex; Code; Result: string; DurationMs; Timestamp }`. `Result` is text, and `FeatureHooks.recordEval` stamps every timeline entry `EvalTimeline.Succeeded`, so a failed cell cannot be told from a good one by the stored data. Evals sent through MCP `send_fsharp_code` and the `/exec` bridge are recorded (`Mcp.fs` near line 1205, `McpServer.fs` near line 4406); I could not confirm that dashboard or editor evals are. `NotebookExport` writes cells with `@sagefs-cell[i] deps:` markers; `EvalRipple.planRipple` orders a re-evaluation by the dependency graph and has a `RippleStatus` with `Failed`.
- Nothing classifies a cell's effects that I could find. A case-insensitive grep for "side effect" and "impure" in `SageFs.Core` and `SageFs` hits ten files (`FileWatcher.fs`, `HttpOriginGuard.fs`, `SessionOperations.fs` and others); I did not open each, and the names suggest comments about other things.

## Design

Cohort fork, pure core in `SageFs.Core/Features/CohortFork.fs` (new slice):

```fsharp
CohortFork.rescope : CohortScope -> CohortCommand<'m> -> CohortCommand<'m>   // exhaustive, mirrors Cohort.scopeOf
CohortFork.at : atSeq: int64<ledgerSeq> -> forkScope: CohortScope -> forker: 'm -> clock -> entropy
                -> LedgerEntry<'m> list -> Result<LedgerEntry<'m> list, ForkRefusal>

type ForkRefusal =
  | SeqBeyondHead of requested: int64<ledgerSeq> * head: int64<ledgerSeq>
  | PrefixDropsEntries of Cohort.DroppedEntry<'m> list    // replayAudit said the prefix is not clean
  | ForkScopeInUse of CohortScope
```

`at` takes `ledgerThroughSeq`, rescopes each command, and appends one new entry whose command is a new `CohortCommand.ForkCohort of by * parent: CohortScope * atSeq * scope`. Its arm in `decide` (the one place that may do this) marks every Present member Departed through the existing departure path (claims become Orphaned), blocks each live landing with `Blocked(Inconclusive "the cohort was forked while this landing was in flight", RebaseAndResubmit)` via the existing `blockAndPop`, and binds the conductor to `by` after seating `by` as Present. It emits `CohortForked of parent * atSeq`. A fork is therefore an ordinary ledger that replays with `replayIn`, and the corpus test that replays every fixture covers it. Past the head is `SeqBeyondHead`, never a silent whole-ledger fork (the scrubber clamps; a fork should refuse).

The integration head at the fork point is `state.IntegrationHead` after the prefix, already in the state. Making a fork run live (a worktree at that sha, an integration session) needs the integration binding to be per cohort scope, which is a separate change. Until then `fork_cohort` is offline: a file in, a file out, viewed with `sagefs play`.

Surfaces, in order: `sagefs cohort-fork <in.jsonl> --at <seq> --out <fork.jsonl>` in `SageFs/CohortForkCommand.fs`, modelled on `CohortExportCommand` (pure `parseArgs`, `renderFork`, thin IO, every failure a DU case with one `describe`); then the MCP/dashboard verb `fork_cohort`. The dashboard scrubber already shows frames by sequence number; the vision puts a fork key on a viewed frame. No new MCP tool until the offline command has been used; if it becomes a tool, fold it into an existing cohort tool's parameters or a resource to save catalog tokens.

Session fork, in `SageFs.Core/Features/SessionFork.fs`:

```fsharp
type CellEffect = | NoEffectSeen | MaySideEffect of reasons: string list       // never "Pure": that cannot be proven

type CellReplay =
  | Replayed of cell: int
  | OutputDiffered of cell: int * recorded: string * replayed: string
  | ReplayFailed of cell: int * error: string
  | Unreplayable of cell: int * UnreplayableReason     // evicted, code over a size bound, ...

SessionFork.plan : EvalHistoryEntry list -> atCell: int -> ForkPlan   // cells to send, in order, each with its CellEffect
```

Effect classification is a conservative token scan over the cell's text for known effectful namespaces and members (`System.IO`, `File.`, `Http`, `DateTime.Now`, `Random`, `Environment`, `Process`), producing reasons. `NoEffectSeen` means the scan found nothing, and the report prints it that way. The honest detector is the comparison of the replayed output with the recorded `Result`: any difference is `OutputDiffered`. The replay itself sends each cell through the same entry point as `send_fsharp_code` into a session created in the new worktree, one at a time, waiting on each cell's own completion.

Prerequisites this exposes: the store needs a success flag per cell (a closed DU, not a string), and it is in memory only, so it dies with the daemon. Persisting eval history is a separate decision; this plan forks what the live session holds and says so in the result.

## Tests

RED first, `SageFs.Tests/CohortForkTests.fs` (default suite):

1. `at` with `atSeq` equal to a fixture's head then `replayIn` of the result: members all Departed, claims none `Held`, conductor `Bound` to the forker.
2. For every fixture and every `atSeq` in `0 .. head`, the fork's `project` frame before the final `ForkCohort` entry equals `CohortScrubber.frameAtSeq` of the parent (property over seq).
3. `rescope` is total and `scopeOf (rescope s c) = s` for every command shape. Written as a property over a generator that covers every `CohortCommand` case, so adding a case without extending `rescope` fails compilation first and this test second.
4. A fork at a seq where a landing is `Verifying` ends with that landing `Blocked(Inconclusive ...)` and an empty queue.
5. `SeqBeyondHead` and `PrefixDropsEntries` refusals carry the head and the dropped entries.

Simulation, `SageFs.Simulation/CohortForkSim.fs`: seeded random cohorts from the existing cohort generators, fork at a random seq, continue both parent and fork with independent seeded tails under injected clock skew. Invariants: the parent's state is unchanged by forking (fold equality); no fork claim is `Held` by a parent-only member; ids minted from the same entropy in both cohorts do not collide inside either. The twin is a `rescope` that forgets one command case, caught by the first invariant on a ledger containing that case.

Session fork tests, `SessionForkTests.fs`: plan from a history is ordered and bounded; a cell mentioning `DateTime.Now` is `MaySideEffect`; an evicted prefix gives `Unreplayable`. The outcome gate: a real session, a recorded history with a clock-reading cell and a file-writing cell, forked into a second worktree session; the report must flag both and the pure cells must replay equal. Tier: `--integration-host`. No sleeps; each cell is awaited by its completion.

## Steps

1. Tests 1 and 5, `ForkCohort` command, event, `decide` arm, `CohortFork.at`. Owns `Cohort.fs` plus the new files; fix the exhaustive matches that name every command (`scopeOf`, `SseWriter`, the inspector, `CohortPlay`).
2. `rescope` with test 3 (new file, parallel with step 1 once the command exists).
3. Tests 2 and 4, then the simulation.
4. `cohort-fork` command (`CohortForkCommand.fs`, `Program.fs` entry, help text) and its tests, modelled on `CohortExportCommandTests`. A new fixture under `SageFs.Tests/cohorts/` for a fork.
5. Docs: `docs/mcp-tools.md` only if a tool is added; `Readme.md` and `docs/progress.md` line next to `cohort-export`.
6. Measure the session half (below). Build `SessionFork` only on a pass.
7. Live `fork_cohort` after the integration binding is per scope.

## Measure first

1. Can a session's recorded cells replay at all? Take 10 real session histories (the `export_session_transcript` or `export_notebook` output of recent agent sessions), replay each into a fresh session, and count cells whose output equals the recorded `Result`. If under about 80 percent match, the report would be mostly warnings and `fork_session` is not worth building. The 80 is my guess; record the real number.
2. Replay time against the original `DurationMs` total. A fork that takes ten times as long as the original run is a different feature.
3. How many histories are truncated. `HistoryCap` is 10,000 cells; check what share of long sessions hit it, and which eval paths (dashboard, editor) skip `recordEval`.
4. For the cohort half, nothing needs measuring beyond running tests 1 and 2.

## Risks and dependencies

- The conductor-seat gap decides whether a fork is usable: the design seats the forker as conductor inside `ForkCohort`, which side-steps it, but a plain departed conductor elsewhere stays stuck.
- Tests, claims and landings are recorded as completions, so a fork's different tail cannot re-run real tests. It explores the reducer's policy, not the product. Say this in the docs.
- Entropy is reused across parent and fork, so ids in the shared prefix are identical and new ids come from new entropy. Intended, and tested in the simulation.
- Depends on `cohort-export-for-play` (built). `drive-real-agents-as-members` and `a-cohort-across-machines` do not depend on it. The dashboard's fork key depends on the scrubber (built).

## Size

M for the offline cohort fork and command. Session fork is a separate M to L that may not pass its measurement. Live fork waits on per-scope integration and is L.

## Decisions for Will

1. Offline-first for `fork_cohort`: ship `sagefs cohort-fork` and `play` before any live fork. Recommendation: yes.
2. The forker becomes conductor inside `ForkCohort`. Recommendation: yes, because a fork nobody can run is not a fork.
3. Persist eval history so session forks survive a daemon restart? Recommendation: decide after measurement 1; it is a larger change than the fork.
