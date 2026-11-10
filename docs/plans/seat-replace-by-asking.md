# A seated member can ask to replace a silent conductor, and a person answers

Roadmap item: `seat-replace-by-asking` (Built, Agents). Plan written 2026-11-08 against master 2f82c792 and the p12-seat branch, built 2026-11-09 on master d4a66a21.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Done, on master and unreleased until the next ship. What is still unproven is under "What is still unproven". |
| Size | M |
| Branch and worktree | none; on master |
| Last green commit | the commit that carries this block. The `SeatingFlow`, `SeatingSim`, `McpSeating`, `SeatNonce` and `CohortSeatDashboard` lists and the ratchet lane were run on it; the counts are in that commit's report. |
| Next action | Nothing is required. If a real client shows the question badly, `McpSeating.questionText` is the one place its words live. |
| Decisions made | 2026-11-08, Will: the first landing of `seat_conductor` fills an EMPTY seat only, so a release could go out. 2026-11-09: replacing is the same tool with `replaces`, not a second tool, so one list entry and one question carry both. The plan does not refuse a holder that is not silent, because the person decides and the question says how recently the holder renewed. |
| Surprises | `SeatingFlow.fs` compiles before `Features/CohortStatusText.fs`, so the plan cannot hold a `HolderSilence`. It carries the holder and when it last renewed (`Mode.Replace(holder, lastRenewal)`), and the adapter reads the silence off that with the new `HolderSilence.ofRenewal`, which `ofState` now calls too. |

## What done looks like

A conductor's connection is gone and its lease has not lapsed, so the seat is bound to a holder nobody can reach. A seated member calls `seat_conductor` with `replaces` set to that holder's member id, and the daemon asks the person at its client, naming the repository, who would sit, the holder being replaced and how long it has been silent. Only an accept with the box ticked replaces it: the command is `ReplaceConductor`, built from a `SeatApproval` that carries the holder and that only `SeatingFlow.ask` makes. The cohort's own compare-and-swap still applies (a holder that is no longer the holder is refused, and says who is). The dashboard's cohort panel, which already names the replace command for a silent holder, gets a Replace button per present member with its own one-use ticket. A decline, silence, a client that cannot be asked and a call under a token change nothing.

## What shipped

- `SeatingFlow.planReplace now state scope caller target holder` and `Plan.Mode` (`Fill`, or `Replace(holder, lastRenewal)`). `plan` and `planReplace` share `planFor`: a token caller, a token target, a caller with no seat, then `Cohort.decide` on the command against a copy of the state. `SeatApproval` carries `Replacing`, and `commandOf` returns `ReplaceConductor(holder, target, scope)` for a replacement and `SeatConductor` for a filling. `ask` is still the only place an approval is made.
- `McpSeating.run` takes `replaces` (blank is the filling it always was), and `questionText now plan` names the holder and either how long it has been silent and when its lease lapses, or that it renewed its lease a few minutes ago. `seat_conductor` has the optional `replaces` parameter.
- The cohort's compare-and-swap is the second check. It runs again when the command commits, so a seat that changed hands while the person read the question refuses with the new holder named.
- The dashboard: a Replace button per present member who is neither the holder nor under a token, shown only while the holder is silent. The click asks first, naming the holder and its silence, and posts to `/dashboard/cohort/seat` with `replace=<holder>` and a ticket issued for that pair (`SeatNonces.replaceSubject`). `Dashboard.replaceRequestVerdict` checks the pair's ticket and the browser header, and `CohortSeatRoute.replaceFromDashboard` builds the command with `commandForReplace`. `DashboardInfra` gained `ReplaceConductor`.
- Docs: `docs/mcp-tools.md` (the tool row, the replace section), `docs/agents.md`, `skills/sagefs/agents.md`, `docs/decisions.md` (the seating decision gains the replace case).

## Tests

- `SeatingFlowTests`: `planReplace` refuses a holder that is not the holder (and carries who is), an empty seat, a holder replaced by itself, a token caller, a token target, an absent target and a caller with no seat. A yes gives an approval whose command is `ReplaceConductor` of the holder shown, every other answer refuses, and a holder that changed between the ask and the answer is refused by the cohort with the new holder named.
- `SeatingSimTests` over `SeatingSim`: events gain `AskToReplace`, with a holder that hands the seat on while the question is open. The new invariant is `A-BOUND-SEAT-CHANGES-HANDS-ONLY-FROM-THE-HOLDER-SHOWN`. The twins that seat on the plan alone and that ask and ignore the answer still break the yes invariant, and a third twin, `ReplacesWhoeverHoldsNowTwin`, breaks the new one only when the race happens.
- `McpSeatingTests`: through a real owner, a yes replaces and every other answer does not, a stale `replaces` is refused with the actual holder named and nobody asked, and a holder that changed during the question is refused with the new holder named.
- `CohortSeatDashboardTests` and `SeatNonceTests`: the Replace button appears only for a silent holder, carries a ticket for the pair, and a ticket for one pair does not replace another, nor does a Seat ticket.
- `DashboardPanelContextBrowserTests`: a headless Chromium on the real shell shows the button, asks naming the holder and how long it has been silent, and posts the holder and the pair's ticket.
- `SeatConductorIntegrationTests`: a real daemon where the holder's client disappears and its lease has not lapsed; a client that ticks the box replaces it.

## What is still unproven

- The browser check serves the page from memory with the panel rendered for a silent holder. A silent holder on a live daemon's dashboard, with the posted request answered by the real route, has not been looked at.
- The person at a real client has not read the question. The tests read its words, and a client's own elicitation form may wrap them differently.
- A holder that is only slow can be replaced if the person ticks the box. The question says how recently it renewed, and nothing stops the yes.

## Decisions for Will

None.
