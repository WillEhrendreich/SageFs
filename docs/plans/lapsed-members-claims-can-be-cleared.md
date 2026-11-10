# A conductor can clear the claims a lapsed member left behind

Roadmap item: `lapsed-members-claims-can-be-cleared` (Later, Agents). Plan written 2026-11-08 against master 213fbc10.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Done on master, 2026-11-09, awaiting the release gate. Pure command, simulation, tool, status text, docs and the Neovim view are done. The dashboard Clear control and a `sagefs cohort` clear command are not built. |
| Size | S for the pure command, M with the tool, the dashboard control and the docs |
| Branch and worktree | none: committed on master in the shared checkout. sagefs.nvim commit `dddec14` is local and not pushed. |
| Last green commit | `41adfbe9` (built with HEAD plus its files in a private copy, because the shared tree did not compile with other agents' in-flight edits: 12 pure cases, 2 properties, 3 status cases, 3 refusal cases, 14 membership cases with the three twins, the `--ratchets` lane green except the VS Code docs-title case, analyzer counts within budget, and the real-daemon case below). The REPL proofs are in "What was proved". |
| Next action | Run the release gate. Then the dashboard control (see "Not built"), if a person needs to clear without a conductor. |
| Decisions made | The conductor may clear a `Held` claim of a silent member before its lease lapses, with the silence recorded in the event. No person's yes is asked. "Silent" is `Timeouts.cohortHolderSilentAfter` (two minutes), the measure the status text already uses for a seat's holder, not the 30 minute lease. The tool is `release_claim`: the conductor naming a claim that is not its own is a clear (`Cohort.releaseOrClear` picks the command), so there is no new tool and no new parameter. A call under a member token never clears. The command carries the fence and compares it as `ReleaseClaim` does. |
| Surprises | The status text could not say a claim is clearable from a frame, because a frame carries no lease; `Cohort.ClaimStanding.clearable state now` is read off the state at the edge, as `HolderSilence.ofState` is. `reassign_claim` has no fence, so a clear through it would have skipped the compare-and-swap; that is why the clear rides on `release_claim`. The `ClaimConflict` and `NotClaimHolder` refusals sent a caller to "ask the holder" with no word about a silent one; both say the conductor can clear it now. |

### What was proved

Against the real `Cohort.decide` in a SageFs REPL session (bare session, the pure files `#load`ed), before the build: the conductor's plain `release_claim` of bob's claim was `NotClaimHolder` (RED); a clear at one minute was `ClearRefused HolderActive(bob, 1 min, 2 min)`; at two minutes it released the claim, bob's late release was `StaleClaimFence`, carol took the scope, carol's own clear was `NotConductor`, a stale fence was `StaleClaimFence`, a second clear was `AlreadyReleased`, and a claim orphaned by bob's departure was cleared with no present target. A landing bob had queued was `Blocked(StaleClaimFence)` at `FastForwardCompleted`. `CohortVacancySpec.proof ()` closed over 149 nodes with no violation, and each of the three twins (`clearFaultsAny`, `clearFaultsNonConductor`, `clearFaultsKeepFence`) closed and violated exactly its rule.

After the build, against a real daemon with three MCP connections (`--integration-host`, the case "the conductor clears the claim a member left behind with release_claim, and another member can take the scope"; one case, `ran=1`, a filtered run): a second member's `acquire_claim` was refused while the holder held the scope, the holder left, the status marked the orphaned claim `| clearable:`, a member that is not the conductor was refused, the conductor's `release_claim` answered `Cleared claim ...`, and the other member then acquired the scope.

### Not built

- The dashboard cohort panel lists claims with no actions today. A Clear button needs a person's authority (the daemon operator route and a one-use ticket, as the Seat button has) and a command with no `by`, which would make a `Released` claim's "by" the former holder or a new case. I left it: a person replaces a silent conductor (`sagefs cohort seat <id> --replace <holder>`), and the new conductor clears.
- `sagefs cohort` has only `seat`; there is no sibling command for claims, so none was added.
- The silent-holder path through a real daemon is not tested end to end: a connection cannot be made silent for two minutes inside a test. The model and the pure cases hold it.

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

Both were built on the recommendations below (2026-11-09: yes to the conductor's clear, no person's yes). Either can be overruled; a person's yes would be a `seat_conductor`-style question in front of `release_claim`'s clear branch.

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
