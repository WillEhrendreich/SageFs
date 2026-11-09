# A conductor can clear the claims a lapsed member left behind

Roadmap item: `lapsed-members-claims-can-be-cleared` (Later, Agents). Plan written 2026-11-08 against master 213fbc10.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S for the pure command, M with the tool, the dashboard control and the docs |
| Branch and worktree | none yet |
| Last green commit | none yet. It starts from `SageFs.Core/Cohort.fs` as on master (`ReleaseClaim`, `ReassignClaim`, `Retention`). |
| Next action | Answer the decision below, then write the RED cases in "Tests". |
| Decisions made | none yet |
| Surprises | none yet |

## What done looks like

A member goes silent, or its token lapses, and the claims it held do not keep a scope shut to everyone else until a 30 minute timer runs out. The conductor can release a silent member's claim, and can release an orphaned claim without first finding a present member to hand it to. The ledger records who was released, by whom, and how long the holder had been silent, and the old holder's late `release_claim` is refused as stale.

## What exists today

Read from `SageFs.Core/Cohort.fs` and `docs/mcp-tools.md` on 2026-11-08.

- `ReleaseClaim` succeeds only for the claim's current holder with the current fence. Held by someone else, orphaned, or already released all answer `NotClaimHolder`.
- `ReassignClaim` is conductor-only and works only on an `Orphaned` claim, and only to a member who is present now (`MemberNotPresent` otherwise). A claim cannot be released by anyone but its holder, so an orphan can leave the table only by being reassigned to someone present, who then releases it, or by `Retention.sweep` after `settledRetention`, which is 30 minutes.
- An orphan does not block anyone. `AcquireClaim` conflicts only with a `Held` claim, so the cost of an orphan is clutter in `get_cohort_status` and the dashboard for up to 30 minutes.
- A `Held` claim does block. A member with no open stream and no call for 30 minutes (`Timeouts.cohortLeaseWindow`) departs, and its claims are orphaned then. Until then the scope is shut to everyone else.
- `revoke_member` cuts a member token off and orphans its claims at once. It takes a token's member id, so a member that is not under a token (a bridge or a plain connection) has no way to be cut off sooner.
- A token that lapses stays lapsed, and a new mint is a new member, so the replacement cannot release what its predecessor held (`docs/mcp-tools.md`, the call section).

## Decisions for Will

- May the conductor force-release a silent member's `Held` claim before its lease lapses? My recommendation is yes, for the conductor only, with the holder's silence recorded in the event. The fence already makes it safe: the release bumps the fence, so the old holder's late `release_claim` is refused as stale, and a landing the old holder had queued is Blocked as `StaleClaimFence` when it comes to land, which is what already happens when a claim is released, reassigned or orphaned under a queued landing, and its requester can withdraw and resubmit. The cost is that a member that is only slow, not gone, loses its claim. The question the person is shown says how long it has been silent, the same way the replace-a-conductor question does (`seat-replace-by-asking`).
- Should the person's yes be required, as it is for replacing a conductor? A conductor is one agent, and agents already `reassign_claim` on their own say-so, so I would not add a question here. Say so if you want it.

## Steps

1. RED first, in `CohortTests` and the cohort simulation: a conductor releases an orphaned claim without a present target. A conductor releases a `Held` claim of a member silent for longer than a stated window and is refused for a member that is not silent. A non-conductor is refused both. The old holder's `release_claim` after either is `StaleClaimFence`.
2. A new `CohortCommand.ClearClaim` and event `ClaimCleared of claimId * former holder * silentFor * by`, replayed by `Cohort.replay`, with one `describe` for its refusals.
3. The MCP tool: extend `reassign_claim` with a mode, or add one conductor-only tool. Decide with the tool-list work (`small-default-tool-list`), because every tool costs characters in a first session.
4. The dashboard's cohort panel names a silent holder's claims with a Clear control carrying a one-use ticket, as the seat panel does.
5. `docs/mcp-tools.md`, `skills/sagefs/agents.md` and `docs/decisions.md`.

## Tests

- Pure cases on the three refusals and the two successes in step 1, plus a property: after any sequence of commands, no scope has two `Held` claims, and a cleared claim is never `Held` again under its old fence.
- A twin that lets a non-conductor clear must fail it.
- One integration case with two real connections: the holder goes quiet, the conductor clears, the other member acquires the scope at once.

## Risks and dependencies

- A wire change: a tool or a mode on one, and a new ledger event, so the commit carries a `Plugin:` trailer and sagefs.nvim's cohort panel reads the new event name.
- It builds on the cohort's compare-and-swap on the holder, which stays.

## Size

S for the pure part, M for the rest.
