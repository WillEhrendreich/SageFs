# A head-to-head benchmark for REPL servers

Roadmap item: `repl-benchmark-against-other-tools` (Next, Docs). Plan written 2026-11-07 against master 4827ab9a.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M for the harness and its tests, then one quiet-machine run per published page |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Get Will's answer on how the other tool is run (the decisions at the end), then write the first failing test: `BenchRecordTests`, a result with no machine, load and version is refused. |
| Decisions made | none yet |
| Surprises | none yet |

## Why this item exists

Will's rule (`feedback_never_lose_to_another_tool`, 2026-11-07): SageFs does not lose to another tool on a metric a user feels, and a measured loss gets a dated plan. FsiMcp (github.com/OnurGumus/FsiMcp, MIT, by Onur Gumus, created 2026-11-07) is the first rival. Its author built a useful tool in a day, and the plans written alongside this one say what to learn from it. This item makes the comparison repeatable, so the next rival or the next FsiMcp release is a run and not a research project, and so the numbers on the page are ours to defend.

This plan carries no timing figures. The machine that wrote it was loaded by other agents, and a benchmark taken under load is unfair to both tools. Where a figure below is FsiMcp's own claim, it is marked as the author's and was not reproduced.

## What is known now, without running the other tool

Read from FsiMcp's source and README at commit 8e6cb8f (package 0.3.0), and from SageFs 0.6.939's daemon and tree, on 2026-11-07.

| Metric | FsiMcp | SageFs today | Where the plan for it is |
|:--|:--|:--|:--|
| Tools in `tools/list` | 7 | 72 | `small-default-tool-list` |
| Characters of tool definitions (minified) | 5,606 | 111,052 | `small-default-tool-list` |
| Estimated tokens (characters / 4) | about 1,400 | about 27,800 | `small-default-tool-list` |
| Server `instructions` | 329 characters | ceiling 4,200 | `small-default-tool-list` |
| Tool calls before the first eval | 1 (`load_project`) | 5 (`AgentGuidance.firstMinute`) | `one-command-stdio-setup` |
| Shell commands to register | 2 | 3 (+1 optional) | `one-command-stdio-setup` |
| Profile tool (CPU and allocation) | yes | none found in the tree | `profile-tool` |
| Per-eval time, bytes and GC counts | yes, above 0.5 s or 100 MB | time on the daemon only | `eval-cost-line` |
| Output clipped head and tail, values under limits | yes | head only, 4,000 characters, FSI default limits | `eval-output-for-an-llm` |
| Server GC, env vars, artifacts path for a session | yes, all three | none of the three | `session-options` |
| Prelude | yes (`prelude`) | yes, as `.SageFs/init.fsx` | not a gap |
| A hang survived | yes: eval timeout (default 120 s) kills the worker | `cancel_eval`; not yet measured against it | this item |
| A stack overflow survived | yes: the worker is replaced | host crash is detected and named (`FsiHostCrash.fs`); not yet measured | this item |
| A failed build keeps the session | yes (compiler errors returned, session kept) | not yet checked | this item |
| Type-check edited files without building | `check_project` with overlays | live testing checks unsaved edits in the editors, and `check_fsharp_code` checks a snippet; no agent tool takes a set of changed files | to compare, not assumed |
| Sessions and clients | one session per server, one client | many sessions, a daemon, a dashboard, editors, cohorts | not a metric to race on |
| Author's claim, not reproduced | "in a real 9-step debugging session, 8.8 s against 27.2 s for rerunning scripts; follow-up questions 8 to 40 ms" | the published check figure is 21 to 25 ms on a loaded machine (`docs/repl-loop-benchmarks.md`) | this item |

How the tool-list figures were made, and what they leave out, is in `small-default-tool-list.md`. They are read from FsiMcp's source, not from a running server. A running server's `tools/list` adds only the JSON-RPC envelope, because the source sends the declared JSON unchanged, so I expect the same count. The benchmark run confirms it.

What the table says: SageFs is behind on size, on steps, and on four features. A second point is about what the table cannot show. FsiMcp serves one agent with one session, and SageFs serves editors, a dashboard and several agents at once, which a head-to-head on one agent's loop does not measure. The page says so in one sentence and claims no win from it.

## What done looks like

`dotnet fsi scripts/repl-bench.fsx` (glue only) drives each server over stdio through the MCP SDK's client, on the same small project, through the same scenarios, and writes a record per scenario and a docs page, `docs/repl-server-comparison.md`, from the records. The page opens with the date, the machine, the load average before and during, the version and commit of every tool, how each was installed and run, and the author of each with a link. It shows each scenario's median and spread over a stated number of alternating runs, and says "not measured" where a cell is empty. It lists where SageFs is ahead, where it is behind, and links each loss to its plan. The harness refuses to run, with a typed reason, when the machine is not quiet. It never installs or runs anything the person did not point it at.

The scenarios (a closed DU, one `describe` each):

1. Time to ready: from starting the client to the first reply that proves a session on the project exists and answers. Cold (clean build output, cold caches the tool owns) and warm.
2. First eval after ready, and the median of 30 follow-up evals of a trivial expression.
3. Rebuild and continue: edit a source file, rebuild through each tool's own route (`hard_reset_fsi_session rebuild=true` for SageFs, `load_project` again for FsiMcp), and time until an eval sees the new code.
4. A hang: `while true do ()`. Time from the moment the caller gives up (each tool's own mechanism) to the next successful eval, and what the caller was told.
5. A stack overflow: `let rec f x = 1 + f x in f 1`. Same two measures.
6. A failed build: a compile error in the project. What the caller is told, whether the earlier session still answers, and the time.
7. Tool-list tokens: the API's token counter with the tools passed in, for both servers' real `tools/list` output.
8. Steps from nothing to a first eval: the commands a stranger types and the tool calls an agent makes, counted by running them in a clean sandbox with a script that records each one, and timed.
9. Optional, not a race: resident memory at ready.

Fairness rules written into the harness and the page. The same project, built for the framework both tools support (FsiMcp wants the .NET 10 SDK and runs on the newest installed runtime). The order alternates between runs. Each tool is run the way its own README says, with the options its docs recommend, and any option either tool offers that changes the result (Server GC, a timeout, a prelude) is stated. A tool that cannot do a scenario gets "not supported", with a reason, never a blank or a zero.

## What exists today (checked in the tree)

- `scripts/machine-bench.fsx` runs a throwaway SageFs daemon (its own data dir and ports, files under `--work`) against a fixture it writes and prints stage timings with n, p50, min and max, and an optional memory cap through `systemd-run`. It is the model for how a run is isolated. Its logic lives in the script, which the repo rule says not to repeat (logic in a project, scripts as glue), so the new harness's logic goes in a project.
- `build/TierCost.fs` already runs a command in a systemd user scope and reads its cgroup's peak, and `build/TierPlan.fs:350` (`isolatedArgv`) is the sandbox the gate tiers run in: a private `/tmp` and a mapped user, in a mount namespace.
- `docs/repl-loop-benchmarks.md` publishes SageFs's own numbers and says openly that the machine was loaded (load average 4.31 to 10.13). The page this item adds follows Will's newer rule, a quiet machine, and the old page's method statement is a decision below.
- `SageFs.Demos` has a bwrap sandbox (`Sandbox.fs`) for recorded cells, a second isolation precedent.
- The MCP SDK `ModelContextProtocol` is already pinned and includes a client. I have not read its stdio transport's handling of the child's stderr, which is the trap `AGENTS.md` names (an unread stderr pipe fills and blocks the child). Step 1 reads it, and the harness drains stderr on its own thread whatever the SDK does.

## The failing test to write first

`SageFs.Tests/BenchRecordTests.fs` (the logic is in a new `SageFs.Bench` project, referenced by the tests): `WHY - a result with no machine, no load average, or no tool version is refused, so no number reaches the page without them`. It does not compile today. The model is the first test of `live-testing-numbers-on-bigger-projects`. Then:

2. `WHY - every scenario has a result or an explicit not-measured reason in the rendered page`, over the DU, so a new scenario cannot be forgotten.
3. `WHY - the page names the author and the link of every tool it compares`, a property over the tool descriptions.
4. `WHY - the rendered page is what the generator prints now` (a stale-page ratchet, like the configuration page).
5. `WHY - the harness refuses a machine whose load average is over the limit, naming the number`. Pure over a reading, so no real load is needed.
6. `WHY - the median and spread of a set of runs are those of the sorted values, and an empty set reads "not measured" and never zero`. Property.
7. Simulation, `SageFs.Simulation/BenchDriverSim.fs`: a server that answers late, never answers, dies mid-call, writes to stderr until the pipe would fill, and answers out of order. Invariants: the driver always returns, never records a time for a call that did not complete, and the stderr pipe is always drained. Twin: a driver that reads stderr after the call, which blocks and fails the invariant.
8. An outcome run of the harness against SageFs alone in the integration tier, with a stub second tool (a tiny stdio MCP server written in the test project), so the harness is proven end to end without running anyone else's software.

## Steps

1. Read the SDK client's stdio transport. Decide client library against a small driver of our own that follows the `AGENTS.md` rules (drain stderr on a thread, read until the id you asked for).
2. `SageFs.Bench` project: records, the scenario DU, quietness check, statistics, the page renderer. Tests 1 to 6.
3. The driver and its simulation (test 7).
4. Test 8 with the stub.
5. `scripts/repl-bench.fsx`, glue over the project.
6. The run, on a quiet machine, with Will's go for the other tool (below). Publish the page with the date.
7. Re-run when either tool releases, and when a plan in this group closes, so the page shows the loss it closed.

## Running the other tool safely

I read FsiMcp's `Server.fs`, `Worker.fs`, `Profiler.fs`, `Text.fs`, `Program.fs` and its `.fsproj` and found a stdio server that starts a worker process of itself, runs MSBuild on the project it is given, and loads that project into an FSI session. I did not read `ProjectResolver.fs` or `ProjectChecker.fs` (about 28 KB), the CI and release workflows, or the dependencies' contents. It is a package published hours before this plan, so nothing here vouches for it. It runs the code of whatever project it loads, by design, as every REPL server does.

The way I propose, pending Will's decision: install it into a throwaway tool path under a scratch directory, never `-g` and never `~/.dotnet/tools`, recording the exact version; or build it from a pinned commit of its source (8e6cb8f is today's head) in that scratch directory, which lets the source be read before it runs and the package restore be done first. Run it with a minimal environment (`env -i`, a scratch `HOME`, only the variables `dotnet` needs, the scratch directory as its working directory), inside the same `unshare` wrapper the gate tiers use (`TierPlan.isolatedArgv`, a private `/tmp` and a mapped user) and with the network off for the timed runs, after a first restore has filled a throwaway NuGet cache. The project it loads is a tiny one the harness writes, never one of Will's. The harness deletes the tool path, the cache and the scratch home at the end and prints the paths it removed.

On 2026-11-07 Will's approval of a throwaway install for read-only probes was relayed to the author of this plan second-hand, in a message from another agent. It is not recorded here as his decision, and nothing of FsiMcp was installed or run while writing this. The benchmark run itself, with timings, needs his go in his own words on the day, and the decisions below ask for the details.

## Measure first

- A trial of the harness against SageFs alone on a quiet machine, to see the spread between identical runs. If the spread is wider than the differences the page would report, the harness needs more runs or a quieter machine, and the page says so.
- Whether `tools/list` from a running FsiMcp equals the declared JSON, byte for byte (expected, from the source). That closes the "read from source" caveat in `small-default-tool-list`.
- The token counter's figures for both lists, which need an API key.
- Whether the machine can be made quiet at all while the release gate runs on it. If not, the run waits for a night or a second machine, and the plan says which.

## Risks and dependencies

- Fairness is the whole value. A page that reads as a dig at an author who did good work in a day would be a loss. The page credits him, links the repo, states the commit, offers a correction route, and reports the other tool's wins as plainly as ours.
- Running another party's code. See the section above. The sandbox lowers the risk and proves nothing.
- A benchmark run takes a quiet machine for a while, which collides with the release gate and the agents. Schedule it.
- Depends on the other plans only to close their rows. The harness itself depends on none.

## Size

M for the harness. Each run and page is S once it exists.

## Decisions for Will

- How is FsiMcp run: a throwaway tool path (what the coordinator relayed), a build from a pinned commit in the sandbox, or something else? Recommendation: build from the pinned commit after I read the two files I have not, so the source and the binary match.
- Your go to run it for the timed benchmark, on a quiet machine, stated by you.
- Send Onur Gumus the harness and the page before publishing, so he can correct a misuse of his tool? Recommendation: yes. Outreach is yours; I would draft it in your voice and not send it.
- The existing page, `docs/repl-loop-benchmarks.md`, was measured on a loaded machine and says so. Re-measure it on a quiet machine with this harness, or leave it as the dated record it is?
