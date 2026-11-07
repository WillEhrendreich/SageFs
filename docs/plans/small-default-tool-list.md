# A small tool list for a first session

Roadmap item: `small-default-tool-list` (Next, Agents). Plan written 2026-11-07 against master 4827ab9a.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M for the ratchet and the shorter descriptions, M for the authorization-keyed starter list |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Step 1: count tokens for both lists with the API's counter (needs an API key from Will), then write the first failing test (the tool-list size ratchet). |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## Why this item exists

FsiMcp (github.com/OnurGumus/FsiMcp, MIT, Onur Gumus, created 2026-11-07) is a stdio MCP server that gives an agent a persistent, project-aware F# REPL. Its whole tool list is seven tools. Mine is 72. Will's rule is that SageFs does not lose to another tool on a metric a user feels (`feedback_never_lose_to_another_tool`), and an agent's context filling with tool definitions before the first message is something a user feels: the first reply is slower and dearer, and on a client that does not defer tools it also crowds the window. A stranger who connects both tools sees this on the first turn.

## What done looks like

A stranger who registers `sagefs mcp` and does nothing else sees a starter list: the tools the first session needs (list projects, create a session, evaluate, check without running, session status, run tests, reset, stop, and `discover_features` for the rest), and its definitions weigh within the bar set under "Measure first". Every tool outside the starter list stays callable by name, `discover_features` names each of them with its one-line purpose, and a member token or the operator credential shows what it always did. The starter list is the same for every caller who presents the same thing, before and after any session is created or any member joins, so it is inside the MCP 2026-07-28 rule. The full catalog also weighs less than it does today, held down by a ratchet that can only go lower. A measured pass rate on a fixed task set with the new lists is not below the pass rate with today's list, which is the guard Will set on 2026-10-26.

## What exists today (checked in the tree)

- `tools/list` is a function of what the request presents, and of nothing else (`createToolListFilter`, `SageFs/McpServer.fs:844`, calling `Capability.listedTools`, `SageFs.Core/Capability.fs:964`). A member token lists its role's tools (`visibleTools`, `:885`), the operator credential lists all of them, and a caller who presents nothing lists all of them under the default `ConnectionsAllowed` policy. `TokenRequired` cuts the anonymous list to `get_cohort_status`, `get_daemon_status` and `join_cohort`. So the roles already cut the list, and a stranger has no token, so a stranger gets the full list. That is the gap.
- The four role presets are sets of tool classes (`RolePreset.toolClasses`, `Capability.fs:244`). There is no preset sized for a first session: Observer lists 16 tools and cannot evaluate, and Implementer lists 63.
- I read the list from the running daemon (0.6.939) over the stdio bridge on 2026-11-07, with the bridge carrying no credential: 72 tools. `docs/mcp-tools.md` says the same count.
- Nothing bounds the size of the list. The only size test I found is `AgentGuidanceTests.fs:111`, which holds `serverInstructions` under 4,200 characters. No test counts tool-definition characters (`run-tests-reuses-proven-results.md` says the same).
- The catalog diet is described in two untracked documents at the repo root, `fsadvent-2026-12-12-video-plan.md` (row "A catalog diet" in section 2, and section 3 on tokens) and `mcp-tool-list-by-harness-2026-10-26.md` (layers 1 to 3 in section 4, the guard in section 7). There is no roadmap item or plan for it in `docs/plans`.

## Fit to the catalog-diet plan: merge

I merge. This plan becomes the plan of record for the diet, so there is one place with a Resume block, and the two untracked documents stay as the research behind it. I take their layer 1 (a smaller catalog under a ratchet) and layer 2 (profiles keyed by what the caller presents) and the guard from section 7. I leave out their layer 3, a list driven by session state, for a reason in the research itself: the 2026-07-28 spec forbids a list that varies per connection, Cursor handles a changing list unreliably, and a changing list invalidates the prompt cache. The FS Advent token experiment (three arms, at least five runs per task) is where this plan's pass-rate guard is run, so the experiment is consumed here and not rebuilt.

## Measure first

Read on 2026-11-07. Nothing below was timed.

| | FsiMcp (read from source) | SageFs 0.6.939 (read from the daemon) |
|:--|--:|--:|
| Tools | 7 | 72 |
| Characters of the tool definitions | 5,606 | 111,052 |
| of which descriptions | 2,230 | 67,774 |
| of which input schemas | 3,009 | 34,676 |
| Estimated tokens (characters / 4) | about 1,400 | about 27,800 |
| Longest single definition | `eval`, about 1,050 | `send_fsharp_code`, 6,354 (description 4,030) |
| Server `instructions` | 329 characters | ceiling 4,200 (test), actual not measured here |

How the figures were made. FsiMcp: the JSON array `toolsJson` declared in `src/FsiMcp/Server.fs` at commit 8e6cb8f (the package is 0.3.0 by that commit's message), minified with `jq -c` and counted. This is read from source, not from a running server. FsiMcp sends `toolsJson` unchanged in `tools/list`, so a running server adds only the JSON-RPC envelope (about 35 characters), and I expect the same count; the benchmark plan confirms it by running the server. SageFs: `tools/list` through `tools/sagefs-call.fsx list` against the daemon, the `result.tools` array minified and counted the same way (the raw response line was 115,098 bytes, because it carries `\uXXXX` escapes and the envelope). The SageFs definitions also carry `annotations` (`destructiveHint` on 18 tools), which FsiMcp's do not.

The token figure is characters divided by four, a rule of thumb. JSON and code usually tokenize worse than prose, so the real figure is probably higher for both, and the ratio (about 20 to 1) is the number to trust. Exact tokens need the API's token counter with the tools passed in. Step 1 does that.

Other numbers this item needs before code:

- The weight of the candidate starter set today. Ten tools (`create_project_session`, `send_fsharp_code`, `check_fsharp_code`, `get_session_status`, `run_tests`, `hard_reset_fsi_session`, `list_sessions`, `get_available_projects`, `discover_features`, `stop_session`) come to 22,365 characters, about four times all of FsiMcp. Choosing fewer tools is not enough. The descriptions have to shrink too, and `send_fsharp_code` alone (6,354) is bigger than FsiMcp's whole list.
- The bar. I propose the starter list at no more than one and a half times FsiMcp's declared characters, 8,400, so about 2,100 tokens, and the full catalog under half of today's 111,052. These are proposals; Will sets them. The honest cost of the bar is that guidance now in descriptions moves to the skill, `instructions` and `discover_features`.
- Whether shorter descriptions hurt. The Haiku-on-FCS onboarding trial (`project_onboarding_haiku_bar`) is the check: a small model with only the public docs still has to succeed at a first session.
- Which harnesses already defer tools. Claude Code loads names and instructions first by default (research doc, section 3), so the saving there is smaller than the character count says. The lever there is the `instructions` text. The clients that do not defer pay in full.

## The failing test to write first

`SageFs.Tests/ToolListBudgetTests.fs`: `WHY - the registered tools' definitions weigh no more than the recorded budget, and a budget above the real weight is itself a failure`. It builds the list from the registered tools in process, sums name, description and schema characters, and compares with a budget in `RatchetBudgets` like the others (lowered by `--ratchets --tighten`, never by hand). It fails to compile today because neither the weight function nor the budget exists. The first green commit records today's 111,052 as the ceiling, so every later description change can only go down.

Then, in this order, each RED before its change:

1. `WHY - the starter list is identical for two anonymous connections, before and after a session is created and after a member joins`. A property over a sequence of daemon events. Twin: a filter that reads the conductor seat, which fails it.
2. `WHY - every tool outside the starter list is still admitted when called by name by a caller who presented nothing`, over every registered tool.
3. `WHY - the starter list weighs no more than its budget`, and a second ratchet case for it.
4. `WHY - discover_features names every tool the starter list leaves out`, so a hidden tool can be found.
5. `WHY - a member token's list is unchanged by the starter profile` (property over the four presets).

## Steps

1. Token counts. Run both lists through the token counter for the three target models and put the figures in this plan. Needs Will's API key. Until then the 4-character estimate stands and is labelled.
2. The ratchet, test above, at today's weight. Commit red, then green.
3. Shrink the descriptions, biggest first. `send_fsharp_code` loses its RULES block to the skill, `instructions` and the refusal text, but anything a wrong call could damage stays in the tool and its refusal, as the research doc decided. Each tool keeps what it does, when to call it and the one gotcha. Lower the budget with `--tighten` after each batch.
4. The starter list through authorization. The mechanism needs a decision (below). The pure part is a new case in `Capability.ListedFor` or a daemon-level setting read by `listedTools`; tests 1 to 5 drive it.
5. Opt-in first. The starter list ships behind a setting. The default changes only after the pass-rate guard in the FS Advent experiment shows no loss and after Will agrees.
6. Document it in `docs/mcp-tools.md` (the count, the two lists, how to ask for the full one) and `Readme.md`.

Wire impact: `McpServer.fs`, `Capability.fs`, `docs/mcp-tools.md`. Commits carry `Plugin: n/a` unless the plugin lists tools by name, which I did not check; check `sagefs.nvim` for a hard-coded tool list first.

## Risks and dependencies

- A tool list that changes mid-conversation invalidates the prompt cache. The starter list never changes inside a connection, so this item adds no churn. A user who moves from starter to full changes the list once, on reconnect.
- Hiding a tool can make an agent do worse (the research doc cites 49 to 74 percent for the reverse case). The guard is the measured pass rate, and `discover_features` is in the starter list for that reason.
- Depends on `a-token-for-every-daemon-call` only if the full list is to require a credential. It does not have to.
- The SDK upgrade to 2.x (research doc, section 6) makes the order of the list stable by the SDK's own guarantee. Until then the registered order is what the ratchet counts.

## Decisions for Will

- How does a stranger get the starter list when the spec lets the list vary only by authorization? Option A: a daemon setting for what an anonymous caller is shown (`full` today, `starter` when set). It is the same for every connection, so it is inside the rule, and `sagefs mcp` presents the operator credential only when the person asks for the full list. Option B: a fifth role preset, `Starter`, and a token that carries it. It fits the existing tables but needs a token, which a stranger lacks. Recommendation: A, with B available for orchestrators.
- The bar: 8,400 characters for the starter list and under 55,000 for the whole catalog? Or lower?
- Does the default flip for everyone after the guard passes, or does the starter list stay opt-in until `a-token-for-every-daemon-call` lands?
