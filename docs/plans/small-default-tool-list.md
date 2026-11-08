# A small tool list for a first session

Roadmap item: `small-default-tool-list` (Next, Agents). Plan written 2026-11-07 against master 4827ab9a.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Done (the opt-in starter list and the diet; the default flip is Will's call and waits on the pass-rate measurement, see Next action) |
| Size | M for the ratchet and the shorter descriptions, M for the authorization-keyed starter list |
| Branch and worktree | cohort member `p1-toollist`, `/home/will/scratch/cohort/p1-toollist`, branch `cohort/p1-toollist`, until it lands |
| Last green commit | the commit that carries this block (red commits: `test(tools): the tool list has a weight budget...` and `test(tools): a starter tool list...`) |
| Next action | Two things need a person. (1) Run the API's token counter over both lists (needs an API key from Will) and write the figures under "Measured" in place of characters / 4. (2) Run the pass-rate guard from the FS Advent experiment (a fixed task set, at least five runs per task) with `SAGEFS_TOOL_LIST=Starter` against the full list, and flip `ToolListProfile.defaultProfile` to `Starter` only if the starter pass rate is not lower, and Will agrees. |
| Decisions made | 2026-11-08, by the `p1-toollist` agent under the cohort brief, for Will to confirm: the starter list is nine tools (`create_project_session`, `get_session_status`, `list_sessions`, `send_fsharp_code`, `check_fsharp_code`, `run_tests`, `cancel_eval`, `hard_reset_fsi_session`, `discover_features`); a stranger gets it through a daemon setting, `SAGEFS_TOOL_LIST` (option A below), and it is opt-in; the bar is the whole catalog under 55,000 characters and the starter list under FsiMcp's 5,606 (both met, see "Measured"); the manual of a tool is the description it is registered with, listed one sentence per tool by `ToolSummaries`. |
| Surprises | `discover_features`, the doorway to every hidden tool, was state-gated: with no session it answered "No active session", so the first real-process run of the starter list found the doorway shut for exactly the client that needs it. It is `AlwaysAvailable` now (`Affordances.fs`), with the Uninitialized and WarmingUp peer sizes in `McpAffordanceHardeningTests` moved from 15 and 14 to 16 and 15. The plan counted `working_directory` described six ways; it is 18 ways across 50 tools, with `session_id` six ways across 19 and the agent name six ways across 17. The weight was 112,840 characters on this checkout, not 111,052 (the plan's figure was one tool and a few days older). `get_available_projects` and `stop_session` are not in the starter list: the plan listed them, and with them the starter list weighed 6,397 characters, over FsiMcp's 5,606. |

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

| | FsiMcp 0.3.0 (from a running server) | SageFs 0.6.939 (read from the daemon) |
|:--|--:|--:|
| Tools | 7 | 72 |
| Characters of the tool definitions | 5,606 | 111,052 |
| of which descriptions | 2,230 | 67,774 |
| of which input schemas | 3,009 | 34,676 |
| Estimated tokens (characters / 4) | about 1,400 | about 27,800 |
| Longest single definition | `eval`, about 1,050 | `send_fsharp_code`, 6,354 (description 4,030) |
| Server `instructions` | 322 characters | ceiling 4,200 (test), actual not measured here |

How the figures were made. FsiMcp: from a running 0.3.0 server on 2026-11-07, started in a throwaway tool path with a minimal environment for an `initialize` and a `tools/list` and nothing else (done by the orchestrator, with Will's approval for that read-only probe). The `tools/list` result is 5,616 characters, 7 tools, and its `tools` array alone is 5,606, which is the count I had first read from the `toolsJson` array in `src/FsiMcp/Server.fs` at commit 8e6cb8f, so the source and the running server agree. The `instructions` string is 322 characters. The raw output is kept in the orchestrator's scratchpad as `fsimcp-0.3.0-tools-list.jsonl`. SageFs: `tools/list` through `tools/sagefs-call.fsx list` against the daemon, the `result.tools` array minified and counted the same way (the raw response line was 115,098 bytes, because it carries `\uXXXX` escapes and the envelope). The SageFs definitions also carry `annotations` (`destructiveHint` on 18 tools), which FsiMcp's do not.

The token figure is characters divided by four, a rule of thumb. JSON and code usually tokenize worse than prose, so the real figure is probably higher for both, and the ratio (about 20 to 1) is the number to trust. Exact tokens need the API's token counter with the tools passed in. Step 1 does that.

Other numbers this item needs before code:

- The weight of the candidate starter set today. Ten tools (`create_project_session`, `send_fsharp_code`, `check_fsharp_code`, `get_session_status`, `run_tests`, `hard_reset_fsi_session`, `list_sessions`, `get_available_projects`, `discover_features`, `stop_session`) come to 22,365 characters, about four times all of FsiMcp. Choosing fewer tools is not enough. The descriptions have to shrink too, and `send_fsharp_code` alone (6,354) is bigger than FsiMcp's whole list.
- The bar. I propose the starter list at no more than one and a half times FsiMcp's declared characters, 8,400, so about 2,100 tokens, and the full catalog under half of today's 111,052. These are proposals; Will sets them. The honest cost of the bar is that guidance now in descriptions moves to the skill, `instructions` and `discover_features`.
- Whether shorter descriptions hurt. The Haiku-on-FCS onboarding trial (`project_onboarding_haiku_bar`) is the check: a small model with only the public docs still has to succeed at a first session.
- Which harnesses already defer tools. Claude Code loads names and instructions first by default (research doc, section 3), so the saving there is smaller than the character count says. The lever there is the `instructions` text. The clients that do not defer pay in full.

## Measured after the work (2026-11-08)

Counted by `ToolListBudgetTests`: each registered tool built in process the way `.WithTools<SageFsTools>()` builds it, given the
hints the list filter gives it, serialized as the wire serializer writes a tool with escaping relaxed (so an apostrophe is one
character), and the characters of the JSON array summed. Master 4285a037 plus this branch, no timing involved, so the machine's
load does not matter. Tokens are characters divided by four, a rule of thumb. No API key was available, so the API's token counter
has not been run over either list.

| | Before | After | FsiMcp 0.3.0 |
|:--|--:|--:|--:|
| Full catalog (characters) | 112,840 (72 tools) | 38,379 (73 tools; 37,937 for the same 72) | 5,606 (7 tools) |
| Full catalog (tokens, characters / 4) | about 28,200 | about 9,600 | about 1,400 |
| Starter list, 9 tools (characters) | 22,365 for the plan's ten tools | 5,521 | |
| Starter list (tokens, characters / 4) | | about 1,380 | |

Where the weight went. 68,356 characters were descriptions and 36,902 were input schemas. Descriptions are now one sentence each
(`ToolSummaries`, 72 of them, none over 160 characters). The schemas lost most of their weight to `working_directory` (50 tools,
12,486 characters of schema before), `session_id` and the agent name, which now carry one of a few named sentences each
(`SharedParameters`).

How it is held. `RatchetBudgets.toolList` records both figures and `--ratchets --tighten` is the only thing that lowers them.
A budget above the real weight fails, so a description cannot grow back into room that was won. The cases are in
`ToolListBudgetTests` and `ToolListProfileTests`; the real-process case is in `CapabilityHttpIntegrationTests`
(`The starter tool list over HTTP`).

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
