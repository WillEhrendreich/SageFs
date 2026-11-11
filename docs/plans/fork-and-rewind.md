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

## Work order

Written 2026-11-11 against master 8975e898. Every `path:line` below was read on that commit; the sections under this one are the original plan, corrected where it had gone stale.

### 1. Outcome

`sagefs cohort-fork <in.jsonl> --at <seq> --out <fork.jsonl>` takes a ledger that `sagefs cohort-export` wrote, keeps its first N entries, and appends one `ForkCohort` entry that departs every member, orphans every claim, blocks any landing still in flight with a stated reason, and seats the person who forked as conductor. The output replays with `Cohort.replayIn` like any other ledger and `sagefs play` renders it. Today there is no way to branch a cohort; `Cohort.fs:60` says so in its header. The session half (`fork_session`) ships only if the measurement in "Measure first" passes; it is step 7.

Outcome gate. `CohortForkTests` folds every fixture under `SageFs.Tests/cohorts/` (eight files today) at every sequence number and checks the fork's frame before its `ForkCohort` entry equals `CohortScrubber.frameAtSeq` of the parent; the corpus gates in `CohortPropertyTests` accept the new fixture; `CohortForkCommandTests` runs the CLI over a temp file. Pure and offline, so Linux is the whole matrix and nothing is unproven elsewhere.

### 2. Where the code is today

- `CohortCommand<'m>` has 25 cases (`SageFs.Core/Cohort.fs:644`). `scopeOf` (`:942`) is the one exhaustive match over commands outside `decide`. `decide` is `:2119`; the `decideRaw` arms start at `:1323`; `Join` (`:1329`) binds the conductor only from `NeverBound`; `Depart` is `:1363`. `departMember why who now state` (`:1033`) and `orphanClaimsOf` (`:1017`) are the departure path; `blockAndPop` (`:1132`, private) blocks a landing and pops the queue. `ConductorBinding` is `Bound | Vacant | NeverBound` (`:266-320`); `VacancyReason` is `:269`.
- `LedgerEntry` `:2126`, `LedgerHead` `:2136`, `DroppedEntry` `:2143`, `ReplayAudit` `:2162`, `replayAuditIn` `:2187`, `replayIn` `:2207`, `replayHeadIn` `:2219` (the plan said near 1929). `CohortState` `:470` carries `IntegrationHead` (`:472`) and `Queue` (`:479`).
- Events DU `:774`. Exhaustive matches over events outside `Cohort.fs`: `SageFs.Core/Features/CohortLanes.fs:143-184` and `SageFs.Core/Features/TrunkFollow.fs:175-185`. `SageFs.Core/SseWriter.fs:556-805` maps cohort events to `claim_changed` and `landing_changed`. The plan below names `SseWriter`, the inspector and `CohortPlay` as exhaustive over commands. They are not; only `Cohort.fs` matches every command. `CohortLanes` and `TrunkFollow` match every event, so a new event goes there.
- `SageFs.Simulation/CohortVacancySpec.fs` enumerates commands by BFS and issues `ClearClaim` for every member (`:300`) with per-command rules (`:196-232`). `SageFs.Simulation/Linearizability.fs` lists commands too (touched when `ClearClaim` landed).
- Scrubber (`SageFs.Core/Features/CohortScrubber.fs`): `ledgerThroughSeq` `:50`, `latestSeq` `:55`, `frameAtSeq` `:65`, `tryParseSeq` `:76`; its header (`:28`) says `fork_cohort` is not built there.
- Export (`SageFs.Core/Features/CohortLedgerExport.fs`): `toJsonl` `:38`, `fromJsonl` `:49`, `exportToFile` `:69`, `importFromFile` `:73`. CLI (`SageFs/CohortExportCommand.fs`): `commandName` `:24`, `parseArgs` `:102`, `renderExport` `:142`, `describe` `:174`, `run` `:206`. `SageFs/Program.fs:184` dispatches it, `:713` prints its help, `:904-910` runs it; `play` is `:182-183`. `CohortPlay.scopeOfLedger` `SageFs/CohortPlay.fs:135`, `runPlay` `:166`.
- The store keys rows by `Scope.label (Cohort.scopeOf entry.Command)` (`SageFs/CohortLedger.fs:119`) under `PRIMARY KEY (scope, seq)` (`:53`). `cohortLedgerRetention` is `SageFs.Core/Timeouts.fs:1070`, 7 days (the plan said near 907).
- The integration binding is one process-wide ref: `McpCohortIntegration.cohortIntegrationRef` (`SageFs/McpCohortIntegration.fs:41`).
- The corpus gates a new command must pass, all in `SageFs.Tests/CohortPropertyTests.fs`: "corpus replay (17)" (`:1659`) replays every `cohorts/*.ledger.jsonl` against `CohortScope.Machine` and requires each row's events to equal the recorded ones; "every CohortCommand case is issued by at least one recorded ledger" (`:1703`); test 31 "every CohortCommand case is accepted by decide in some generated history" (`:1160`) over the `Intent` DU (`:95-131`) plus the deterministic histories `reassignHistory`, `seatHistory`, `replaceHistory`, `clearHistory` (`:208-240`). The last new command, `ClearClaim` (commit 41adfbe9), touched 23 files; its fixture `claim-cleared.ledger.jsonl` (commit 0f785746) is four rows folded through `decide` and written with `exportToFile`. No script in the tree writes a fixture; it is done from a session.
- Sessions: `EvalHistoryEntry = { CellIndex; Code; Result: string; DurationMs; Timestamp }` (`SageFs.Core/Features/EvalStore.fs:45`), `HistoryCap.StandardCells = 10000` (`:70`), `ByteBudget.StandardBytes` 64 MiB (`:96`). `FeatureHooks.recordEvalWithCost` stamps every cell `EvalTimeline.Succeeded` (`SageFs.Core/Features/FeatureHooks.fs:74`). Recording doors: `send_fsharp_code` (`SageFs/Mcp.fs:1183`) and the `/exec` door (`SageFs/McpServer.fs:4941`). Whether a dashboard eval records: `[not verified]`.
- Nothing named `ForkCohort`, `CohortFork` or `SessionFork` exists in the tree.
- `RatchetBudgets.fileSize` (`SageFs.Tests/RatchetBudgets.fs:18`) holds `SageFs.Core/Cohort.fs` at exactly 2475 lines (`:348`), and a budget only goes down. The `decide` arm adds lines to that file. See trap 1.

### 3. Steps

1. **Pure, `Cohort.fs`.** RED in `SageFs.Tests/CohortForkTests.fs` (new; `<Compile Include="CohortForkTests.fs" />` after `CohortClearClaimTests.fs`, `SageFs.Tests/SageFs.Tests.fsproj:1172`), list `"CohortFork: the command"`: `decide` on `ForkCohort(by, parent, atSeq, scope)` over a state with two present members, one held claim and a `Verifying` landing yields every member Departed, the claim Orphaned, the landing `Blocked(Inconclusive _, RebaseAndResubmit)`, an empty queue, the conductor `Bound by`, and events ending in `CohortForked(parent, atSeq)`. Then: the case in `CohortCommand` (`:644`), `CohortForked` in `CohortEvent` (`:774`), the arm in `decideRaw` (reuse `departMember` and `blockAndPop`), the `scopeOf` line (`:942`), the event arms in `CohortLanes.fs:143-184`, `TrunkFollow.fs:175-185`, and `SseWriter.fs`. The build names every match you missed; that is what no wildcards buys.
2. **Corpus, same commit or the next.** (a) An `Intent` case `IFork of by: int` (`CohortPropertyTests.fs:95`) with its arm in the intent-to-command function (`:375-430`), and a deterministic `forkHistory = seating @ [ IAcquire(2, 0); IFork 1 ]` added to test 31's `histories` and asserted the way `clearHistory` is (`:1180-1190`). (b) The fixture `SageFs.Tests/cohorts/forked.ledger.jsonl`: in a session on `SageFs.Tests`, fold `Join alice; Join bob; AcquireClaim bob; ForkCohort alice` through `decide` with `Entropy = [||]`, fixed clocks and `CohortScope.Machine`, build the `LedgerEntry` rows from the events `decide` returned, `exportToFile`. The replay gate compares events row by row, so hand-written events fail. (c) `CohortVacancySpec.fs`: a rule `forkDepartsEveryoneAndSeatsTheForker` and `ForkCohort` issued for each present member in the enumeration at `:300`, or a written reason it is outside the bounded model. Write the rule; a command outside the proof is the kind that rots.
3. **Pure, new slice.** `SageFs.Core/Features/CohortFork.fs` after `CohortScrubber.fs` (`SageFs.Core/SageFs.Core.fsproj:362`): `rescope`, `at`, `ForkRefusal`. RED: `rescope` is total, `scopeOf (rescope s c) = s`, over a generator built by reflecting the DU the way test 31 does; `at` past the head is `SeqBeyondHead`; `at` on a prefix that `replayAuditIn` drops a row from is `PrefixDropsEntries`; the frame-equality property against `frameAtSeq` for every fixture and every seq.
4. **Simulation.** `SageFs.Simulation/CohortForkSim.fs` (after `CohortVacancySpec.fs`, `SageFs.Simulation/SageFs.Simulation.fsproj:66`) and `SageFs.Tests/CohortForkSimTests.fs`: a seeded parent from the `Intent` generators, a fork at a random seq, independent tails under `FaultInjection.withClockSkew` (`SageFs.Simulation/FaultInjection.fs:93`). Invariants: the parent's fold is unchanged; no fork claim is `Held` by a parent-only member; ids from the shared entropy do not collide inside either. Twin: a `rescope` that forgets one case.
5. **Edge.** `SageFs/CohortForkCommand.fs` after `CohortExportCommand.fs` (`SageFs/SageFs.fsproj:257`), modelled on it: `commandName`, `Flag`, `parseArgs`, `renderFork`, `describe`, `run`. `Program.fs` dispatch beside `:184`, help beside `:713`, run beside `:904`. `CohortForkCommandTests.fs` modelled on `CohortExportCommandTests.fs:135-175` (a temp dir, a built ledger, the TWIN that drops entropy). A Readme line beside `cohort-export` (`Readme.md:639`) and a `docs/progress.md` line.
6. **Measure, session half.** "Measure first" below. `SessionFork` is built only on a pass.
7. **Later.** `fork_session` (`SageFs.Core/Features/SessionFork.fs`) and a live `fork_cohort` once `cohortIntegrationRef` is per scope.

`DECISION:` the forker is seated as conductor inside `ForkCohort`. Recommendation: yes; a fork nobody can run is not a fork. `DECISION:` `CohortForked` as a new SSE event name or folded into `cohort_matrix`. Recommendation: fold; `docs/sse-events.md` then changes one line, and no editor learns a new event. `DECISION:` persist eval history for session forks. Recommendation: decide after measurement 1.

### 4. How to check it

- REPL first: a session on `SageFs.Core`; `send_fsharp_code` the `decide` call of step 1 before touching the file. After editing `Cohort.fs`, `hard_reset_fsi_session rebuild=true`.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Inner loop: `--filter-test-list "CohortFork"`; after step 2 also `--filter-test-list "corpus replay"` and `--filter-test-list "Cohort properties"` (the list at `CohortPropertyTests.fs:842`).
- Acceptance: the unfiltered `--summary` run, which carries `CohortPropertyTests`, `CohortSpecTests`, `CohortForkTests` and the command tests. The offline fork needs no integration tier.

### 5. Traps in this repo

- `Cohort.fs` is at its line budget (2475, exact). The DU case, the `scopeOf` line and the `decide` arm all add lines there. Pay for them: keep the arm to the calls into `departMember` and `blockAndPop`, put every helper in `CohortFork.fs`, and if it is still over, move `releaseOrClear` (`:974`) and `scopeOfLabel` (`:989`) to a file after `Cohort.fs` (their callers reference them by module path; `[not verified]` that no caller is inside `Cohort.fs`). Then `--ratchets --tighten`, never a hand edit of the table.
- A new `CohortCommand` case needs all three: a fixture under `SageFs.Tests/cohorts/` whose events came from `decide`, an `Intent` plus a deterministic history in `CohortPropertyTests.fs`, and a place in `CohortVacancySpec.fs`. The gate goes red on the first two by name; the third is the house rule (DST for every reducer change).
- The corpus replay seeds `CohortScope.Machine` (`CohortPropertyTests.fs:1696`). A fork fixture whose `ForkCohort` names a `Repository` scope is refused `WrongCohortScope` there. Fork the fixture to `Machine`, or give the fixture an `OpenCohort` first row.
- Exhaustive matches: `scopeOf` and `decideRaw` for commands; `CohortLanes`, `TrunkFollow`, `SseWriter` for events. No wildcard arms.
- SAGE-005: `at atSeq forkScope forker clock entropy entries` has one `int64<ledgerSeq>` and no two parameters of one primitive type. Keep it that way; `SageFs.Core/SAGE-005` is 179, exact.
- Wire: the offline command has no daemon surface, `Plugin: n/a offline CLI`. A new SSE event name is a wire change (`docs/sse-events.md` is on the trailer list): `Plugin:` trailer, plugin first.
- Commit `SageFs.Core.fsproj`, `SageFs.fsproj`, `SageFs.Tests.fsproj` and `SageFs.Simulation.fsproj` whole, from a clean tree.

### 6. Size and order

Steps 1 to 3: three to four days. Steps 4 and 5: two to three. Steps 1 and 2 land together or back to back (the corpus gate is red between them). Step 3 starts once the command case exists. Step 5 is file-disjoint from 3 and 4. First commit: `test(cohort): RED a fork departs every member, orphans their claims and seats the forker (fork-and-rewind step 1)`.

### 7. Not now

- Live `fork_cohort` (a worktree at `IntegrationHead`, an integration session): waits on a per-scope integration binding.
- `fork_session`: waits on measurement 1 and on a success flag per cell (`FeatureHooks.fs:74` stamps everything `Succeeded`).
- A fork key on the dashboard scrubber: after the CLI has been used.
- An MCP `fork_cohort` tool: the catalog weighs 38,286 characters and only goes down (`SageFs.Tests/RatchetBudgets.fs:462`).

## What done looks like

Two verbs, built in this order.

`fork_cohort`: pick a ledger sequence number from a cohort (live, or from a file made by `sagefs cohort-export`), and get a new cohort whose history is the first N entries. Every member of the fork is departed, every claim they held is orphaned, any landing that was in flight is blocked with a stated reason, and the person who forked holds the conductor seat. From there new commands run through the same `decide`. The fork never touches the parent.

`fork_session`: replay one session's recorded cells up to a cell index into a new session in a new worktree. The result lists every cell as replayed, replayed with a different output, failed, or possibly side-effecting, and says so up front. It never reports a clean fork it cannot support.

The outcome gate runs both against real state. For cohorts it forks every checked-in fixture in `SageFs.Tests/cohorts/` at every sequence number and checks the fork's frame equals `CohortScrubber.frameAtSeq` of the parent at that number, then continues the fork with a different tail. For sessions it replays a recorded session and checks the report against the cells that were deliberately made effectful. Matrix: {fork at 0, at a middle seq, at the head, past the head} x {ledger with a landing in flight at the fork point, without} x {conductor present, vacant}; and for sessions {pure cells, a cell reading the clock, a cell writing a file, a cell that failed originally, a cell evicted from history}.

## What exists today

- `Cohort.replayHeadIn`, `replayAuditIn` (`SageFs.Core/Cohort.fs`, lines 2219 and 2187 on 2026-11-11; the plan first said 1929) fold a ledger; `replayAuditIn` also reports `Dropped` and `Diverged` entries. The `Cohort.fs` header says plainly that `fork` is not built.
- `CohortScrubber.ledgerThroughSeq` and `frameAtSeq` (`SageFs.Core/Features/CohortScrubber.fs`) already take the prefix of a ledger and project it. A fork is that prefix plus one more entry.
- The ledger is portable: `CohortLedgerExport.toJsonl`/`fromJsonl` (`SageFs.Core/Features/CohortLedgerExport.fs`) write whole rows with entropy, `sagefs cohort-export` (`SageFs/CohortExportCommand.fs`) writes them offline, `sagefs play` (`SageFs/CohortPlay.fs`, `Program.fs` near line 136) reads and renders them, and `CohortPlay.scopeOfLedger` refuses a file that mixes cohorts. Eight fixtures sit in `SageFs.Tests/cohorts/` (four when this was written).
- Rows are pruned after `DataRetention.cohortLedgerRetention`, 7 days by default (`SageFs.Core/Timeouts.fs:1070`), so a fork of an old cohort needs an export taken in time.
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

1. Tests 1 and 5, `ForkCohort` command, event, `decide` arm, `CohortFork.at`. Owns `Cohort.fs` plus the new files; fix the exhaustive matches: `scopeOf` and `decideRaw` name every command, and `CohortLanes.fs`, `TrunkFollow.fs` and `SseWriter.fs` name every event (checked 2026-11-11: `SseWriter`, the inspector and `CohortPlay` do not match on commands).
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
