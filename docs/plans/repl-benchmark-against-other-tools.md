# A head-to-head benchmark for REPL servers

Roadmap item: `repl-benchmark-against-other-tools` (Next, Docs). Plan written 2026-11-07 against master 4827ab9a.

## Work order

Written 2026-11-11 against master `8975e898`. Every `path:line` below was read at that commit. The plan under this section is the design; this section is what to do on Monday morning with only the repo.

### 1. Outcome

Will can open `docs/repl-server-comparison.md` and say, with the machine, the load, both versions and the run count printed on the page, where SageFs is ahead of FsiMcp and where it is behind, and each loss links to the plan that closes it. The gate: `dotnet fsi scripts/repl-bench.fsx` on a quiet machine writes one record per scenario per tool and renders the page; `BenchRecordTests` refuses a record with no machine, load or version; a stale-page ratchet fails when the page and the records disagree; and an `--integration-host` case runs the harness end to end against SageFs plus a stub stdio server written in the test project. Linux only. The harness never runs on CI (a shared runner is not quiet). FsiMcp on Windows and macOS is unproven and stays so.

### 2. Where the code is today

- Nothing of the item exists: no `SageFs.Bench/`, no `SageFs.Tests/BenchRecordTests.fs`, no `docs/repl-server-comparison.md`, no `scripts/repl-bench.fsx` (checked).
- The model for a throwaway daemon is `scripts/machine-bench.fsx`: options at `:11-30`, `/proc/loadavg` read into the machine profile at `:211`, `systemd-run --user --scope` wrapping under `--memory-cap` at `:375`, and it already drives live testing over HTTP at `:645-677`. Its logic is in the script, which `AGENTS.md` says not to repeat: the new logic goes in a project.
- Sandboxes: `build/TierCost.fs:55` (systemd-run argv), `build/TierPlan.fs:342` `isolatedArgv` (the plan's `:350` is stale), `SageFs.Demos/Sandbox.fs:1-5` (bwrap argv as pure data).
- MCP SDK: `SageFs/SageFs.fsproj:286-287` references `ModelContextProtocol` and `.AspNetCore`; `Directory.Packages.props:78` pins `1.0.0-rc.1`. `SageFs.Tests` reaches the client through the SageFs project reference and uses `HttpClientTransport` (`SageFs.Tests/EvalReachesRunningAppTests.fs:42-45`). No stdio client anywhere in the tree (grep `StdioClientTransport`: nothing). Whether the SDK's stdio transport drains the child's stderr is `[not verified]`; the package source is in `~/.nuget/packages/modelcontextprotocol.core/1.0.0-rc.1/`.
- Tool-list figures live in `SageFs.Tests/RatchetBudgets.fs:462-464`: full catalog 38,286 characters, 74 tools; the nine-tool starter list 5,395. The measurement is `SageFs.Tests/ToolListBudgetTests.fs:50-54` (`characters`, `weight`). The table below (72 tools, 111,052 characters) is the 2026-11-07 reading and is stale by two diets.
- `SageFs/AgentGuidance.fs:34-39` `firstMinute` is four tool calls (the table below says five).
- Host crash detection is `SageFs.Core/FsiHostCrash.fs`; `cancel_eval` is a registered tool. `profile` is a registered tool too, so the table's "Profile tool: none found" row is stale.
- Sibling plan statuses, read from each `Resume here`: `eval-cost-line`, `eval-output-for-an-llm`, `session-options`, `one-command-stdio-setup`, `eval-hang-and-stack-overflow` are Done; `profile-tool` is In progress (both modes built); `small-default-tool-list` is Done as an opt-in. The page measures these rows itself.
- Test plumbing to copy: `SageFs.Tests/TestInfrastructure.fs:462-475` (`Integration.Runner`, `register`), `:508` (`hostListConcurrent`), `:774` (`Ratchet.register`); the project references at `SageFs.Tests/SageFs.Tests.fsproj:1221-1224`; a sim with a twin at `SageFs.Simulation/DeltaRouteSim.fs:36-50` and its tests `SageFs.Tests/ExpectoCasesSimTests.fs`; a stale-page ratchet at `SageFs.Tests/RoadmapDocTests.fs:51-55`.
- `dnx` is installed (`~/.local/share/dotnetup/dotnet/dnx`); `dotnet --list-sdks` ends in 11.0.100. Whether a .NET 10 SDK is also installed (FsiMcp's README asks for it) is `[not verified]`; run the full `dotnet --list-sdks` before step 7.
- `SageFs.Tests/LatencyStats.fs:27-54` already has `Summary`, `summarize` (nearest rank) and `Machine`, in the test project. The `live-testing-numbers-on-bigger-projects` plan moves them into Core as `LatencyRecord.fs`. Do not write a third copy.

### 3. Steps

Each step is one commit, RED test first. Pure steps are marked.

0. **Measurement, no code.** Can this machine be quiet, and what is the run-to-run spread? Conditions: no other agents (`sagefs call list_sessions` shows only yours), no gate (`dotnet fsi scripts/gate-tail.fsx -- --status` shows nothing running), `cat /proc/loadavg` first field under 1.0. Then `dotnet build SageFs -c Release` and run twice: `dotnet fsi scripts/machine-bench.fsx --sagefs SageFs/bin/Release/net11.0 --runs 10 --skip-lt --skip-reset --label quiet-trial --out <scratch>/trial-N.json`. Compare the p50 of each stage between the two runs. If any stage differs by more than 20 percent, write that in `Resume here` and stop until the machine is quieter; no page can be published from it. Record the load and the spread either way.
1. **RED** `SageFs.Tests/BenchRecordTests.fs`: `WHY - a result with no machine, no load average, or no tool version is refused, so no number reaches the page without them`. Compile-red. **Green**: new project `SageFs.Bench/SageFs.Bench.fsproj` (net11.0, references `SageFs.Core` for `SageFs.Json`), added to `SageFs.slnx`; `SageFs.Bench/Record.fs` with `Scenario` (closed DU, the nine scenarios, one `describe`), `ToolUnderTest { Name; Version; Commit; Author; Link; HowRun }`, `MachineReading { Machine; Load1Before; Load1During }`, `Record`, and `Record.create : ... -> Result<Record, RecordRefusal>`. Register: `<ProjectReference Include="..\SageFs.Bench\SageFs.Bench.fsproj" />` after `SageFs.Tests.fsproj:1222`, `<Compile Include="BenchRecordTests.fs" />` in the pure block. Pure. Commit the `.fsproj` files whole.
2. **RED** tests 5 and 6 (quietness refuses over the limit naming the number; median and spread of sorted values, an empty set reads "not measured"). **Green**: `SageFs.Bench/Quiet.fs` (`judge : load1 -> cores -> Result<unit, NotQuiet>`, the limit a named constant with its reason) and statistics. DECISION below on where `summarize` lives. Pure.
3. **RED** tests 2, 3, 4 (every scenario has a result or a reason; every tool names author and link; stale-page ratchet). **Green**: `SageFs.Bench/Page.fs`, `render : Record list -> string`. The ratchet is registered with `Ratchet.register` so `--ratchets` runs it. Pure.
4. **RED** the simulation `SageFs.Simulation/BenchDriverSim.fs` (test 7) with a twin that reads stderr after the call, plus `SageFs.Tests/BenchDriverSimTests.fs`. **Green**: `SageFs.Bench/StdioDriver.fs`. Before writing it, read the SDK's stdio transport in the nuget cache. If it drains stderr on its own thread and lets you read until a given id, wrap it; if not, own driver per `AGENTS.md` rule 5. Register the sim in `SageFs.Simulation/SageFs.Simulation.fsproj` next to `DeltaRouteSim.fs` and the tests in `SageFs.Tests.fsproj`. Edge code with a pure core (the protocol state machine is the pure part).
5. **RED** test 8: a stub stdio MCP server (a small console project under `SageFs.Tests/fixtures/`, answers `initialize`, `tools/list`, one `tools/call`) and an outcome case registered with `Integration.register Integration.Runner.Host` so `--integration-host` runs it: the harness drives SageFs (its own daemon, data dir and port pair, as `machine-bench.fsx` does) and the stub through every scenario and writes records that pass test 1. Edge.
6. `scripts/repl-bench.fsx`: argument parsing and calls into `SageFs.Bench`, nothing else. Writes records under `docs/benchmarks/repl/` and the page. Also run the token count (scenario 7) only when an API key is present; otherwise the cell reads "not measured: no API key".
7. **The run.** Needs the three DECISIONs below answered. Install FsiMcp v0.4.3 the way Will picks, in the sandbox the plan describes, on a quiet machine (step 0 conditions). Five runs per scenario per tool, alternating order; raise the count if step 0's spread says so. Commit the records and the page together.
8. Roadmap landmark (`SageFs.Bench/Record.fs`), re-read the FsiMcp table column at v0.4.3 and correct it, and set `Resume here` to Done.

### 4. How to check it

- REPL first: a session on `SageFs.Tests` (`create_project_session`), `send_fsharp_code` the record and page functions, run `BenchRecordTests` and the sim tests by list name with `run_tests`.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets` (the page ratchet and the tool-list budget run here).
- Inner loop: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --summary --filter-test-list "Bench"` (the list names must contain "Bench"; a filter that matches nothing exits 0, read `ran=`).
- Acceptance: the unfiltered `--summary` for steps 1 to 4 and 6, and `--integration-host --summary` for step 5. Step 7 is accepted by the page itself: load, versions, run count and the stub-free records in the commit.

### 5. Traps in this repo

- SAGE-005: `Record.create` must not take two `string`s for version and commit in a row; wrap them (`Version of string`, `Commit of string`).
- `RatchetBudgets.toolList` only goes down: this item adds no tool, so it must not move. A new ratchet (the page) is a new budget row.
- No new NuGet package without discussion: the stub server and the driver use the SDK already pinned, or plain `System.Text.Json` through `SageFs.Json` profiles.
- Child-process tests apply `SpawnEnvironment.apply psi` (`SageFs.Tests/DashboardBrowserRunner.fs:741` is the model) or the child inherits the harness's environment.
- No timing-sensitive tests: the sim and the unit tests never time anything; only the run times anything.
- `TrustSignalTests` fails the fast suite if a registered tier is not invoked by `ci-pipeline.fsx`: do not invent a `--bench` tier. Use `--integration-host` for test 8.
- Commit `SageFs.Tests.fsproj`, `SageFs.Simulation.fsproj` and `SageFs.slnx` whole, with a pathspec, never `git add -A`.
- Linux only: `/proc/loadavg`, `systemd-run`, `unshare` are Linux; `Quiet.judge` returns `NotQuiet.NoReading` elsewhere.

### 6. Size and order

Steps 1 to 3 are a day, pure. Step 4 is one to two days (the SDK read decides). Step 5 is a day. Step 6 half a day. Step 7 is an afternoon on a quiet machine plus Will's go. About a week for one person; steps 2 and 3 can run in parallel with step 4 (different files). First commit: step 0's measurement written into `Resume here`, then step 1's RED test.

### 7. Not now

- Re-measuring `docs/repl-loop-benchmarks.md` (decision 4 below) waits until the harness exists; the old page stays a dated record.
- Closing the "behind" rows is the sibling plans' job; this page only reports them.
- A second rival, or FsiMcp's next release, is a re-run of step 7.
- Windows and macOS runs: no machine.

### DECISIONs for Will (this section's additions)

- Where `summarize` lives: in `SageFs.Core/LatencyRecord.fs` from the numbers plan (shared, one copy) or in `SageFs.Bench/Stats.fs`. Recommendation: land the Core module first (its step 2, a pure day) and have Bench depend on it.
- How FsiMcp is run, your go for the timed run, and whether to send Onur Gumus the page first: unchanged from "Decisions for Will" below. Recommendation unchanged: build from the pinned commit of v0.4.3 so the source and the binary match.

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

Read from FsiMcp's source and README at commit 8e6cb8f (package 0.3.0), from one running 0.3.0 server asked for `initialize` and `tools/list` only (the tool-list rows), and from SageFs 0.6.939's daemon and tree, on 2026-11-07.

| Metric | FsiMcp | SageFs today | Where the plan for it is |
|:--|:--|:--|:--|
| Tools in `tools/list` | 7 | 72 | `small-default-tool-list` |
| Characters of tool definitions (minified) | 5,606 | 111,052 | `small-default-tool-list` |
| Estimated tokens (characters / 4) | about 1,400 | about 27,800 | `small-default-tool-list` |
| Server `instructions` | 322 characters | ceiling 4,200 | `small-default-tool-list` |
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

Update, 2026-11-10: the FsiMcp column above was read at 0.3.0 on 2026-11-07 and is a release behind. The latest release is v0.4.3 (2026-11-08; v0.4.0 came the evening of 2026-11-07, v0.4.2 the next morning), and its README lists eight tools: `load_project`, `eval`, `typecheck`, `check_project`, `reset`, `profile`, `bindings` and `measure`. The README also says `dnx FsiMcp --yes` runs it with no install. Nobody has run 0.4.3's `tools/list`, so 5,606 and 5,616 characters belong to 0.3.0 only, and the tool count is now 8. Re-read the column against the release before the next benchmark run.

Update, 2026-11-11: the SageFs column is stale too. `SageFs.Tests/RatchetBudgets.fs:462-464` records the full catalog at 38,286 characters and 74 tools (the nine-tool opt-in starter list is 5,395), so the 72 and 111,052 above are two diets old. `profile` is a registered tool (`profile-tool` plan: both modes built), so the "none found in the tree" row is wrong now. `AgentGuidance.firstMinute` (`SageFs/AgentGuidance.fs:34-39`) is four steps, not five. `eval-cost-line`, `eval-output-for-an-llm`, `session-options` and `one-command-stdio-setup` report Done in their plans. None of this has been measured head to head; the page does that.

How the tool-list figures were made, and what they leave out, is in `small-default-tool-list.md`. The FsiMcp figures come from a running 0.3.0 server (`tools/list` result 5,616 characters, 7 tools, 2026-11-07), and they match the count read from its source, 5,606 for the `tools` array. Nothing else of FsiMcp has been run: no eval, no load, no timing.

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

I read all of FsiMcp's `src/FsiMcp` sources (`Server.fs`, `Worker.fs`, `Profiler.fs`, `Text.fs`, `Program.fs`, `ProjectResolver.fs`, `ProjectChecker.fs`) and its `.fsproj`, and found a stdio server that starts a worker process of itself, runs `dotnet msbuild -restore` on the project it is given, and loads that project into an FSI session. `ProjectResolver.fs` and `ProjectChecker.fs` hold the MSBuild calls (with `-p:ArtifactsPath=` when an artifacts path is set), the choice of assemblies, a snapshot copy of the project's build output under `<temp>/fsi-mcp`, and the type-check with overlays. They start only `dotnet` and open no network connection of their own. The files they write themselves are under that temp folder (they delete folders there left by dead server processes, and no others), and the build they start writes `bin` and `obj` as any build does. That changes none of this plan's claims, and the artifacts-path call is the same `-p:ArtifactsPath=` that `session-options` proposes. I did not read the CI and release workflows, the tests, `scripts/repl.py`, or the contents of its dependencies. It is a package published hours before this plan, so nothing here vouches for it. It runs the code of whatever project it loads, by design, as every REPL server does. One difference matters for the rebuild scenario: `load_project` runs the restore and the build itself every time, so "rebuild and continue" for it includes MSBuild, and the page says so.

The way I propose, pending Will's decision: install it into a throwaway tool path under a scratch directory, never `-g` and never `~/.dotnet/tools`, recording the exact version; or build it from a pinned commit of its source (8e6cb8f is today's head) in that scratch directory, which lets the source be read before it runs and the package restore be done first. Run it with a minimal environment (`env -i`, a scratch `HOME`, only the variables `dotnet` needs, the scratch directory as its working directory), inside the same `unshare` wrapper the gate tiers use (`TierPlan.isolatedArgv`, a private `/tmp` and a mapped user) and with the network off for the timed runs, after a first restore has filled a throwaway NuGet cache. The project it loads is a tiny one the harness writes, never one of Will's. The harness deletes the tool path, the cache and the scratch home at the end and prints the paths it removed.

On 2026-11-07 Will approved running FsiMcp 0.3.0 for a read-only `initialize` and `tools/list` probe, and the orchestrator did it in a throwaway tool path with a minimal environment and removed it afterwards. That is all that has been run. The latency and behaviour benchmark still needs a quiet machine and Will's go for each run he has not approved, and the decisions below ask for the details.

## Measure first

- A trial of the harness against SageFs alone on a quiet machine, to see the spread between identical runs. If the spread is wider than the differences the page would report, the harness needs more runs or a quieter machine, and the page says so.
- Done on 2026-11-07: `tools/list` from a running FsiMcp 0.3.0 is 5,616 characters for 7 tools, the same count as the source gave.
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

- How is FsiMcp run: a throwaway tool path (what the tools/list probe used), a build from a pinned commit in the sandbox, or something else? Recommendation: build from the pinned commit, so the source and the binary match.
- Your go to run it for the timed benchmark, on a quiet machine, stated by you.
- Send Onur Gumus the harness and the page before publishing, so he can correct a misuse of his tool? Recommendation: yes. Outreach is yours; I would draft it in your voice and not send it.
- The existing page, `docs/repl-loop-benchmarks.md`, was measured on a loaded machine and says so. Re-measure it on a quiet machine with this harness, or leave it as the dated record it is?
