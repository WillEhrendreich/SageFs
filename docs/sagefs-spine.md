# The cohort spine, as code

A one-page map of the code that lets several agents work in one repository at once: who may touch what, how their work lands on a
shared branch, and what survives a daemon restart. Line numbers were read on 2026-11-11; they drift, the names do not.

The strategy behind it (why this is the bet) is in `sagefs-spine.md` at the repository root, which is an untracked working note. This
page only describes what the code does.

## The five parts

```
 MCP tool / dashboard / CLI
        |  (who is calling: MemberTable.ConnectionIdentity.resolve)
        v
 CohortOwners.OwnerFor scope ----> one CohortOwner per repository
        |                               |  mailbox: one command at a time
        |                               v
        |                         Cohort.decide  (pure)  -> state, events, effects
        |                               |
        |                 ledger.Append (SQLite row)  -> reply to the caller
        |                               |
        |                         effects -> LandingPerformer (git, sessions)
        |                               |
        |                completion command posted back to the same mailbox
        v
 get_cohort_status <- CohortFrame (Cohort.project) <- published state
```

### 1. The pure core: `SageFs.Core/Cohort.fs`

- `CohortCommand` (`Cohort.fs:644`) is every thing that can happen: join, depart, renew, tick, claims, the conductor seat, landings and
  their completions. Every command carries the scope of the cohort it is for.
- `decide clock entropy state command` (`Cohort.fs:2134`, body `decideRaw` at `:1300`) returns the new `CohortState` (`:470`), the
  events to record, and the effects to perform (`CohortEffect`, `:825`: `Rebase`, `ComputeAffected`, `RunTests`, `FastForward`,
  `Notify`). It reads no clock and no random source; both are parameters, so any interleaving a test builds is reproducible.
- The scope gate is the first thing `decideRaw` checks: a command for another cohort is refused `WrongCohortScope`.
- `Authority.present` (`:584`) says whether a member is the conductor, a member or nobody. The conductor seat is a `ConductorBinding`
  (`NeverBound`, `Bound`, `Vacant`); the first joiner of a never-bound cohort takes it, and a vacant seat is filled only by a person
  (`SeatConductor`, see `docs/decisions.md`, "A conductor seat is filled by a person's yes").
- Claims are fenced: every change to a claim moves its fence, and a release or a landing that presents an old fence is refused
  `StaleClaimFence`.
- Landings form one serial queue. `advanceQueue` (`:1096`) starts the head; `blockAndPop` (`:1132`) is the one terminal move, so a
  blocked landing never holds up the ones behind it.
- `Retention` (`:1162`) sweeps settled landings and released claims on `Tick` after `cohortSettledRetention`.
- `replayAuditIn` (`:2202`) folds `decide` over recorded rows and reports rows it now refuses (`Dropped`) and rows whose events it no
  longer reproduces (`Diverged`). `project` (`:2377`) turns a ledger head into the flat `CohortFrame` that the status text and the
  dashboard read.

### 2. The owner actor: `SageFs.Core/Features/CohortOwner.fs`

- `startCoreWithCeiling` (`CohortOwner.fs:516`) reads the scope's rows, replays them, takes the next row number from the store
  (`LedgerPort.NextSeq`, which counts rows this build cannot read), and starts a `MailboxProcessor`. Before the first command it
  settles or re-drives a head landing that was in flight when the last owner stopped (`restartRecovery`).
- `handle` (`:341`) runs one command: `decide`, project the frame, append the row, publish, answer the caller, then hand the effects
  to `dispatchLandingEffects` (`:242`). A step that throws answers `OwnerStepFailed` and the owner keeps its previous state
  (`ResilientActor.wrapLoopAnswering`).
- `LandingPerformer` (`:84`) is the port for effects. Each one runs off the mailbox and posts its result back as a completion command
  (`RebaseCompleted`, `AffectedComputed`, `TestsCompleted`, `FastForwardCompleted`, and the failure cases).
- `CohortOwners.OwnerFor` (`CohortOwners.fs:214`) keeps one owner per scope and starts it once (a lock around creation). A scope is one repository (`CohortScope.Repository`), so
  agents in two repositories share nothing.

### 3. The ledger: `SageFs.Core/Features/CohortLedger.fs` and `SageFs/CohortLedger.fs`

- `LedgerPort` is `Append`, `ReadAll scope`, `NextSeq scope` and `Scopes`. The ledger is append-only and is the only source of truth:
  `CohortState` is never stored, it is always a replay.
- The SQLite port keys rows by `(scope, seq)`. A row this build cannot decode is skipped on read and left on disk; its number stays
  taken, so the next append goes after it.
- Rows are serialized with the worker wire profile today, so a change to a `CohortCommand` case is a ledger format change. Every new
  case needs a recorded fixture in `SageFs.Tests/cohorts/*.ledger.jsonl` and a history in `CohortPropertyTests.fs`.

### 4. The landing gate

- `Rebase` runs `CohortGit.rebaseCommitsOnto` (`SageFs/CohortGit.fs:483`) in the integration worktree.
- `ComputeAffected` and `RunTests` use the integration session; the set of tests is `AffectedTests.verificationTestSet`
  (`SageFs.Core/Features/AffectedTests.fs:72`), which never returns an empty set (a selection of nothing runs everything).
- `FastForward` runs `CohortGit.fastForwardBranch` (`CohortGit.fs:494`), which refuses anything but a fast-forward.
- The daemon's performer is `cohortLandingPerformer` (`SageFs/DaemonMode.fs:3377`).
- An owner that starts over a ledger whose head landing was cut off re-runs a `Rebasing` head's rebase and records a `Verifying` head
  inconclusive, so the queue moves (`CohortOwner.restartRecovery`).
- Known gap, with a work order: the claim and head checks run at `FastForwardCompleted`, after git has moved
  (`docs/plans/cohort-landing-checks-before-git-moves.md`).

### 5. Identity

- `MemberTable.MemberId` (`SageFs.Core/MemberTable.fs`) is `Browser`, `Mcp`, `Minted`, `Capability` or `Operator`. An MCP member id is a
  fingerprint of the connection's handle, never the handle, and `MemberId.display` fingerprints a raw handle a legacy row carries.
- `ConnectionIdentity.resolve` (`MemberTable.fs:161`) picks the caller: a member token, then the bridge's client key, then the
  transport session, then the name the caller gave.
- Sub-agents on one MCP connection are one member. A sub-agent that calls `leave_cohort` departs its orchestrator. A member token per
  sub-agent (`SageFs.Core/Capability.fs`, `mint_member`) is the way to tell them apart.
- The reaper (`cohortReaperCallback`, `SageFs/DaemonMode.fs:4422`) renews a member that evaluated, called or holds a stream, and
  posts `Tick` to every scope's owner, which departs members whose lease lapsed.

## Where it is tested

- `SageFs.Tests/CohortPropertyTests.fs`: properties over generated histories, the corpus ledgers, and the live-owner-equals-replay check.
- `SageFs.Simulation/CohortSpec.fs`, `CohortVacancySpec.fs`, `CohortVetoSpec.fs`, `CohortLandingSim.fs`: exhaustive and seeded models.
- `SageFs.Tests/CohortOwnerTests.fs`, `CohortLedgerSqliteTests.fs`, `CohortLinearizabilityTests.fs`: the owner, the store and concurrency.
- The decisions behind the rules are in `docs/decisions.md` (member tokens, the bridge client key, seating, clearing a silent member's
  claims, and the open decision on how long a quiet holder keeps a claim).
