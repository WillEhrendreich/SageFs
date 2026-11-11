# The landing's claim and head checks run before git moves

Found by the cohort roast (2026-11-10, finding C2). Left as a work order by the fix pass on 2026-11-11: the change moves a check that
property 11, the exhaustive spec and the landing sim all pin at `FastForwardCompleted`, and it was not finished with a proof in that pass.
The owner-restart half of the same roast (C3) did land; see `CohortLandingLoopTests.fs`, `cohortOwnerRestartTests`.

## Work order

### 1. Outcome

A landing whose claim is cleared, released or reassigned, or whose integration head is moved by the conductor, while it is being verified
is blocked before git is touched. Today `refs/heads/<integration>` moves first and the ledger then says the landing did not land, so git
is ahead of the cohort's `IntegrationHead` and every later landing fails its fast-forward three times and ends
`Blocked(Inconclusive "fast-forward failed 3 times")` until the conductor runs `set_integration_ref`. Afterwards git and the ledger agree
about what landed in every history. The gate: a property over histories with a clear, a lease lapse or a `SetIntegrationHead` between
`TestsCompleted` and `FastForwardCompleted`, plus a git-world twin in `CohortLandingSim` whose branch the `FastForward` effect moves.
Linux only today; nothing here is platform-specific.

### 2. Where the code is today

- `SageFs.Core/Cohort.fs`, `TestsCompleted` arm (search `| CohortCommand.TestsCompleted(id, failing, scope) ->`): with no failing tests it
  sets `Verifying(..., 0)` and emits `CohortEffect.FastForward(id, rebasedHead)` with no claim check and no head check.
- Same file, `FastForwardCompleted` arm: compares `base'` with `state.IntegrationHead` (`HeadMoved`), then `claimHeldOk` over
  `req.Claims` (`StaleClaimFence`), then lands. Both checks run after the performer has moved the branch.
- `CohortEffect.FastForward of LandingId * toSha: string` (`Cohort.fs`, `type CohortEffect`) carries no expected old value.
- `SageFs/CohortGit.fs:494` `fastForwardBranch` reads the branch, checks ancestry, and runs `update-ref <branch> <toSha> <currentSha>` with
  the sha it read one subprocess earlier. That guards a sub-second race only; it never compares with the cohort's head.
- The performer: `SageFs.Core/Features/CohortOwner.fs:307` dispatches `FastForward`; `SageFs/DaemonMode.fs:3377` `cohortLandingPerformer`.
- Models that reach the arm: `SageFs.Simulation/CohortSpec.fs:344` (maps a pending `FastForward` straight to `FastForwardCompleted`),
  `SageFs.Simulation/CohortLandingSim.fs:123` (same, no git world), `SageFs.Tests/CohortPropertyTests.fs:695`
  `fastForwardAfterHeadMoved` and property 11 at `:986`, property 6 (`Landed implies every claim was Held by the requester at land time`).
- `genIntent` now includes `IClear` (2026-11-11), so property 6 meets a clear under a live landing; it reads the pure state, so it cannot
  see the git half.

### 3. Steps

1. RED, sim (domain + sim). In `SageFs.Simulation/CohortLandingSim.fs` add `BranchAt: string` to the world; the `FastForward` effect sets it
   to `toSha` before the completion is posted. New invariant `GIT-AGREES-WITH-LEDGER`: after every step, `BranchAt` equals
   `state.IntegrationHead` or a landing is mid fast-forward. Add `ClearClaim` and `Tick` to the landing alphabet. Run it: it must find
   the C2 sequence (clear between `TestsCompleted` and `FastForwardCompleted`) and shrink it.
2. RED, property. In `CohortPropertyTests.fs` add a fixed history next to `clearHistory`: seat, acquire, request, rebase ok, affected,
   tests pass, `IClear` (after the silence window), fast-forward. Assert the effect list of the `TestsCompleted` step contains no
   `FastForward` when the clear came first, and that a `FastForward` once emitted is always followed by `Landed`.
3. GREEN (domain). In the `TestsCompleted` arm, before emitting `FastForward`, run the same two checks `FastForwardCompleted` runs, with
   `blockAndPop` and the same blockers. Extract them into one private function so the two arms cannot disagree.
4. DECISION: what `FastForwardCompleted` does once git has moved. Options: (a) keep the checks there too (the roast's shape; leaves the
   in-flight window of one `update-ref`); (b) land unconditionally, since the completion reports what git did, and release whatever claims
   still back it; (c) make the effect `FastForward(id, toSha, expectedOld = base')` so git's own compare-and-swap refuses when the
   cohort's head moved, and keep the claim check only at `TestsCompleted`. Recommendation: (c) plus (b): git refuses a stale head by
   itself, and a completion that reports success is a fact the ledger records.
5. GREEN (edge). Add the expected old value to `CohortEffect.FastForward` and `LandingPerformer.FastForward`, pass it to
   `fastForwardBranch` as the `update-ref` old value. Every pattern on `CohortEffect.FastForward` changes: `CohortOwner.fs`,
   `CohortSpec.fs`, `CohortLandingSim.fs`, `DaemonMode.fs` (daemon-tail's file when this was written: coordinate).
6. Update property 11 and `fastForwardAfterHeadMoved` to put the head move before `TestsCompleted`, and its teeth test.

### 4. How to check it

REPL session on `SageFs.Tests` first (eval the new `TestsCompleted` arm against the fixed history). Then
`dotnet build SageFs.Tests -c Release`, `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`, the inner loop
`--filter-test-list "cohort"`, and the acceptance run unfiltered `--summary` (the sims run in the default suite).

### 5. Traps in this repo

- No new `CohortCommand` case is needed; if one appears, it needs a `SageFs.Tests/cohorts/*.ledger.jsonl` fixture and a property history.
- Changing `CohortEffect` is not a ledger change (effects are not stored), but the corpus replay test must still drop nothing.
- `DaemonMode.fs` is shared and large; keep the edge change to the one performer field.

### 6. Size and order

About a day. Steps 1 and 2 first and together (they are the RED), then 3, then the decision, then 5 and 6.

### 7. Not now

The roast's I9 (a ledger JSON profile apart from the worker wire) is related to durability, not to this ordering.
