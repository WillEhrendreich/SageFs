# Workspace hygiene that runs itself

Roadmap item: `hygiene-that-runs-itself` (Next, Platform). Plan written 2026-11-07 against master `1f4daeee`. The reasons, with the
numbers measured that day, are in the findings the plan came from (summarised under "Why the pile grew").

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Done on branch `cohort/p7-hygiene` (2026-11-07 night, phase A of the cohort fan-out): steps 1 to 8, 9 (the CLI half) and 10, and the three found since (a grace of 0, stale `*.tiers` directories, checkouts a cohort holds). The one piece left is the MCP tool for the rescue, which waits for the tool-list work. |
| Size | L |
| Branch and worktree | `cohort/p7-hygiene` in `/home/will/scratch/cohort/p7-hygiene` |
| Last green commit | See `git log` on `cohort/p7-hygiene`. Filtered runs only, on a machine at load average 50: the `Workspace hygiene` lists (the sweep, rescue, ownership, grace, ledger gate and both DSTs) and `--ratchets`. The unfiltered default suite was not run. |
| Next action | Land through the cohort. After the tool-list work, add an MCP tool for `--rescue` (the logic is `HygieneRescue.rescue`, which the CLI already calls). Nothing else is open. |
| Decisions made | 2026-11-07, agent on Will's brief: the daemon is the owner of hygiene, not the orchestrator. A merged worktree waits `Timeouts.finishedWorktreeGrace` (24 hours; the variable is `SAGEFS_FINISHED_WORKTREE_GRACE_DAYS`, and 0 means no grace) after it was last in use before the automatic tidy takes it, and only once the ledger has a record of it quiet for that long. A `*.tiers` directory beside a repository is listed, never removed by SageFs. A restore ref that already exists under another commit gets the commit's first eight characters added. A rescue branch that already exists is never moved. |
| Surprises | Built in this pass: `HygieneEdge.effectsWith` (tip, merge state and a line per removal, `refs/sagefs/removed/<name>` before a commit the base only has by content is let go, and no removal if that ref cannot be made), `Ledger.clearance` and `Ledger.holdBack` (agent worktrees need a ledger entry), `HygieneSweep` (observe, salvage, tidy), the `hygiene.autoTidy` setting (default on, live), the daemon timer and `CohortHolds` (the daemon registers them; `GET /api/sessions` carries them as `cohortHeld`, which is how `sagefs hygiene` in a shell learns them), git evidence for worktrees outside the managed directory (named when they hold work, never removed, never salvaged), `HygieneRescue`, the shared `heldLine`, the agent-worktree note in session replies, and `HygieneSweepSim` with three twins. Earlier: the grace rule (`standingOf`, `Retention.KeepFor` on an agent worktree, `LastTouched` from git's admin files and the ledger, `Locations.WorktreeGrace`, `DataRetention.finishedWorktreeGrace`), `Summary.Held` and the `nudge` that leads with held work, `HygieneLedger` (pure), `HygieneLedgerStore`, `HygieneSalvage` (real git, worktree untouched), `HygieneAuto` (the closed list). Before this pass nothing called salvage, the ledger store or `HygieneAuto.plan`, so the product behaved as before apart from the grace and the status line. The domain-budget ratchet forced unions in place of options and bools (`Tip`, `MergeState`, `RecordedActivity`, `Presence`, `HeldWork`); do not undo that. `git status` rewrites a worktree's index, so every hygiene git call now sets `GIT_OPTIONAL_LOCKS=0` (the index mtime is one of the activity signals). Will, 2026-11-07: agent worktrees are the exception and should be cheap to avoid (see Design). The plan id is a hash of every step, so it changes between two dry runs minutes apart. The `workspace:` line is shown today and ignored: it quotes 2084 "safe" items (logs and registry files) and no count of work that exists nowhere else. |

## Why the pile grew

1. Nothing runs the tidy. `HygieneService.tidy` has three callers (the `tidy_workspace` tool, `sagefs hygiene --tidy`, the dashboard
   button), each a person or an agent asking. The daemon prunes only the host cache by itself.
2. A merged worktree is Reclaimable the second `git cherry` shows no `+`. There is no age and no record of whether the agent that
   made it may be resumed (memory `feedback_dont_prune_worktrees_of_agents_you_may_resume`). So the safe class is both too eager
   (a 58-minute-old worktree is on it) and never run.
3. The plan id hashes ~3400 steps (logs, registry files, temp runs) and a tidy is refused when it moved. Two dry runs a few minutes
   apart gave `3ad46ed0bdde` and `382366a435db`.
4. Work that exists nowhere else is `PresentedOnly`: a line of text with a command. Nothing commits uncommitted files, nothing says
   how old the oldest held work is, and the `workspace:` line counts "safe" items (2084, nearly all log and registry noise) rather
   than worktrees that hold work.
5. Ownership is "mcp": 450 of 633 owner ledger records carry that name. The harness's own identity (`agent-<id>` in the directory
   name and in the git lock reason) is never read.
6. Worktrees outside `.claude/worktrees` (13 on the measuring machine, 15 GB) are listed `Unknown` and never checked for commits or
   uncommitted files.

## What done looks like

- The daemon runs a sweep at start and every `Timeouts.hygieneSweepInterval`, for every repository it has a record of (a live
  session, or an owner ledger record whose directory still exists). A setting `hygiene.autoTidy` (default on, `Live`) turns the
  tidy half off; the salvage half and the surfaces stay on.
- The sweep first salvages: every worktree whose tree has uncommitted real files gets a commit on `salvage/<name>`, made with a
  temporary index so the worktree's own files, index and HEAD are not touched. A second sweep with nothing changed makes no commit.
- The sweep then tidies the provably safe class and only that: merged (ancestor, patch-equivalent or tree-equal) clean worktrees
  that have been quiet for the grace, expired worker logs, orphaned or expired temp runs, orphaned spawned-daemon registry entries,
  and merged agent branches. Each step looks at its target again right before acting (`Executor.afterLook`), the path gate and the
  `Confirmation` still apply, and a plan that changed is refused for the manual tidy exactly as before.
- Every action is a line in `<data dir>/hygiene-ledger.jsonl` (append-only): what, where, tip sha, merged how, why. A worktree is
  also recorded when first seen, when it stops being in use (a "finished" line) and when it is resumed. `resumable` is read from
  that record.
- Work that exists nowhere else is named the same way on every surface: "N worktrees hold work that is nowhere else, oldest 38d". It
  leads the `workspace:` line of `get_daemon_status` and the `create_*_session` replies, the first line of the dashboard hygiene
  card, and the top of `sagefs hygiene`.
- `sagefs hygiene --rescue <name>` makes `rescue/<name>`, the worktree's branch (and its salvage commit) rebased on the base, and
  reports whether the base already has the change, by patch id and by content.
- Never touched, ever: a worktree a session, lease, process or live harness lock is using; anything outside the managed directories;
  a worktree whose branch has commits the base lacks; a worktree with uncommitted real files (salvaged, but still held).
- Outcome gates: a `SageFs.Simulation` case over seeded worlds of worktrees in every state with the invariant "no commit and no
  uncommitted byte is ever lost", and a twin that tidies something unsafe so the invariant is shown to bite; real-git tests on
  throwaway repositories for salvage, the ledger, the sweep and rescue.

## Measure first

Done on 2026-11-07, before any code (read-only, nothing tidied): the numbers under "Why the pile grew", plus `git cherry` and
`git merge-tree --write-tree master <branch>` over every unmerged branch: 3 of 24 are patch-equivalent or content-contained,
21 hold commits master has no equivalent for. So a tidy that waits for "merged" would still leave most of the pile, which is why
the surfaces and salvage matter as much as the sweep. These numbers move; re-derive them rather than quoting them.

## Design

Pure core, edge at the sides, the same shape as the rest of hygiene.

- `WorkspaceHygiene.fs` (pure) gets the grace rule (`standingOf`: a merged worktree whose `LastTouched` is inside its
  `Retention.KeepFor` is `WithinRetention`), the "holds work" reading of a standing (`Standing.holdsWork`), the `Summary` fields
  (`HoldsWork`, `HoldsWorkOldest`) and the `nudge` that leads with them.
- `HygieneLedger.fs` (pure, Core) is the event type, the JSON lines codec, the fold into per-worktree records, and `resumable`.
  The file store and the clock are at the edge (`SageFs/HygieneLedgerStore.fs`).
- `AutoTidy` (pure, Core) is the one function that says which plan steps the daemon may run by itself: a closed list of kinds,
  `Risk.Safe` only, never a process, never a gate checkout or host cache entry (those have their own reapers).
- `HygieneSalvage.fs` (edge) is git plumbing: temporary `GIT_INDEX_FILE`, `read-tree HEAD`, `add -A -- <real paths>`, `write-tree`,
  `commit-tree` with the worktree's HEAD (and the previous salvage tip) as parents, `update-ref`. Idempotent by tree id.
- `HygieneSweep.fs` (edge) is the sweep: observe (ledger), salvage, gather, auto plan, run through `Executor.runAsync` with a ledger
  recording `HygieneEdge` context, refresh the cache. It takes the same `Locations` and `LiveFacts` the manual path takes.
- `DaemonMode.fs` owns one timer, rooted like the others and disposed at shutdown, plus a run at start and on `SessionStopped`.

Agent worktrees are the exception, not the default (Will, 2026-11-07: they are why the pile exists). The design therefore treats a
worktree as something that needs a ledger entry: the sweep writes `FirstSeen` for each one with its maker, and a worktree the
ledger has no record of is not eligible for the automatic tidy until the ledger has seen it quiet for the grace. Avoiding them is
made cheap in the same plan: an agent that only reads or edits one file can work in the shared checkout under a claim (the cohort
scope claims already exist), and the briefs (`skills/sagefs/agents.md`) should say "no worktree unless the work must not touch the
trunk". That brief change and a `create_*_session` hint ("this directory is an agent worktree; the daemon will salvage and tidy it,
and `sagefs hygiene` lists it") are step 10.

Decisions:

1. Grace is 24 hours after the worktree was last in use, as the record or the git admin files say. Named `Timeouts.finishedWorktreeGrace`,
   `SAGEFS_FINISHED_WORKTREE_GRACE_DAYS` overrides (a count of days, 0.5 is twelve hours), 0 means no grace.
2. The restore point of a removed branch that was merged by patch or tree equivalence is `refs/sagefs/removed/<branch>`, so its commits
   stay reachable. A branch that is an ancestor of the base needs none.
3. The salvage commit holds Real files only. Generated files (build output, lock files) are regenerated by definition and are what the
   existing forced removal already drops.

## Steps

Each step is RED first, committed on its own, green on its own.

1. The grace rule in `standingOf`, and `LastTouched` for a worktree read from the git admin files.
2. `Standing.holdsWork`, `Summary.HoldsWork` and `HoldsWorkOldest`, and the `nudge` that leads with them.
3. `HygieneLedger`: events, codec, fold, `resumable`.
4. The ledger store and recording in the edge: first sight, finished, resumed, and each removal with its tip sha.
5. `HygieneSalvage` on real throwaway repositories: a modified file, a deleted file, an untracked file, no commit when nothing
   changed, the worktree's own files and index untouched.
6. `AutoTidy` and `HygieneSweep`, with the `hygiene.autoTidy` setting, and the daemon timer.
7. The `SageFs.Simulation` case: seeded worlds, the invariant, a twin.
8. The surfaces: the dashboard card and `sagefs hygiene` say the same thing the `workspace:` line does; worktrees outside the
   managed root get git evidence (still never removed).
9. `sagefs hygiene --rescue <name>` and the MCP tool for it.
10. Make a worktree the exception: the brief rule in `skills/sagefs/agents.md` ("no worktree unless the work must not touch the
    trunk"), and the `create_*_session` hint for an agent worktree.

## Decisions for Will

- Whether 24 hours is the right grace. The memory says "after the release containing its work shipped"; the daemon cannot see a
  release, so a clock stands in for it.
- Whether the automatic tidy should also take merged branches that no worktree holds (it does in this plan: nothing is lost, the
  tip is in the ledger).
