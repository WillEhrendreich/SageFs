# A seated member can ask to replace a silent conductor, and a person answers

Roadmap item: `seat-replace-by-asking` (Next, Agents). Plan written 2026-11-08 against master 2f82c792 and the p12-seat branch.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet. It starts from `seat_conductor` as landed (`SageFs.Core/SeatingFlow.fs`, `SageFs/McpSeating.fs`) on top of p15's `ReplaceConductor` (`SageFs.Core/Cohort.fs`, `commandForReplace` in `SageFs/CohortSeatCommand.fs`). |
| Next action | Write the RED cases of "Tests" below, then take the steps in order. The first is `SeatingFlow.planReplace` and a `SeatApproval` that carries the holder. |
| Decisions made | 2026-11-08, Will: the first landing of `seat_conductor` fills an EMPTY seat only, so a release could go out. Replacing a silent holder by asking is this follow-up. |
| Surprises | none yet |

## What done looks like

A conductor's connection is gone and its lease has not lapsed, so the seat is bound to a holder nobody can reach. A seated member calls `seat_conductor` with `replaces` set to that holder's member id, and the daemon asks the person at its client, naming the repository, who would sit, the holder being replaced and how long it has been silent. Only an accept with the box ticked replaces it: the command is `ReplaceConductor`, built from a `SeatApproval` that carries the holder and that only `SeatingFlow.ask` makes. The cohort's own compare-and-swap still applies (a holder that is no longer the holder is refused, and says who is). The dashboard's cohort panel, which already names the replace command for a silent holder, gets a Replace button per present member with its own one-use ticket. A decline, silence, a client that cannot be asked and a call under a token change nothing.

## What exists today

- `seat_conductor` fills an empty seat (`SeatingFlow.plan`, `ask`, `commandOf`; `McpSeating.run`). It takes `agentName`, `toMember` and `working_directory`.
- p15 added `ReplaceConductor` to the cohort, `sagefs cohort seat <member> --replace <holder>` and `POST /api/cohort/seat` with a `replace` field, and a panel line that names the command for a silent holder (`HolderSilence`, `renderCohortPanelFor`). No tool builds `ReplaceConductor`, and the docs say so. That stays true until this item lands.
- The Seat button posts to `/dashboard/cohort/seat` with a one-use ticket (`SeatNonces`, `seatRequestVerdict`).

## Steps

1. RED cases first (see "Tests"), committed red.
2. `SeatingFlow.planReplace now state scope caller target holder`: the caller is present and not a token, the target is present and not a token, `holder` is the member the seat is bound to, and `holder` is not the target; it dry-runs `Cohort.decide` on `ReplaceConductor`. `Plan` gains the holder and the silence (`Features.CohortStatusText.HolderSilence.ofState`), `SeatApproval` gains `Replacing: MemberId option`, and `commandOf` returns `ReplaceConductor(holder, target, scope)` or `SeatConductor(target, scope)`. `ask` stays the only place that makes an approval.
3. `McpSeating.run` takes `replaces` (blank is the existing behaviour) and `questionText` names the holder and its silence. `McpTools.seat_conductor` gets the optional `replaces` parameter; the description stays under 600 characters and `ToolSummaries` (once on master) takes the extra parameter.
4. The dashboard: when `ReadHolderSilence` says `SilentSince`, a Replace button per present member who is neither the holder nor under a token, with a confirm that names the holder. The ticket is issued for the pair (member and holder) and the route reads a `replace` query value; `seatFromDashboard` builds the command with `commandForReplace`.
5. Docs: `docs/mcp-tools.md` (the tool row, the three routes, the sentence that no tool builds `ReplaceConductor`), `skills/sagefs/agents.md`, `docs/decisions.md` (the seating decision gains the replace case), the roadmap item's landmark.

## Tests

RED first, then green, then the ratchet lane.

- `SeatingFlowTests`: `planReplace` refuses a holder that is not the holder (and carries who is), a holder replaced by itself, a token caller, a token target, an absent target; a yes gives an approval whose command is `ReplaceConductor`; every other answer refuses; the approval still has no public constructor.
- `SeatingSim`: events gain `AskToReplace`; the invariants gain "a bound seat changes hands only on a yes, and only from the holder the person was shown"; the twins (seat on the plan alone, ask and ignore the answer) must still break them, and a third twin replaces whoever holds the seat now.
- `McpSeatingTests`: through a real owner, a silent conductor is replaced on a yes and not on a no; a stale `replaces` is refused with the actual holder named.
- `CohortSeatDashboardTests` and `SeatNonceTests`: the Replace button appears only for a silent holder, carries a ticket for the pair, and a ticket for one pair does not replace another.
- `SeatConductorIntegrationTests`: a real daemon where the holder's client disappears and its lease has not lapsed; a client that ticks the box replaces it.

## Risks and dependencies

- p1-toollist's `ToolSummaries` matches exhaustively over `ToolName`. It carries `seat_conductor` without `replaces` until this lands, and takes the parameter in this item.
- Replacing a holder that is only slow, not gone, takes the seat from a live conductor. The question says how long the holder has been silent, and `Cohort.decide` leaves the replaced holder an ordinary member with its claims untouched.

## Size

M: one pure function and one record field in the flow, one parameter and one question, a dashboard button with its ticket, and tests at each layer.

## Decisions for Will

None.
