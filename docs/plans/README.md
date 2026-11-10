# Implementation plans

One plan for each roadmap item that is not built yet. The roadmap links each item to its plan, so whoever picks an item up opens
the plan first. A plan names the real files and symbols the work starts from, the first failing test, the commits in order, what to
measure before building where the answer changes the design, and the questions only the maintainer can answer.

These were written on 26 October 2026 against master `59a8692c`. A plan goes stale as the code moves. Before starting one, check
that the symbols it cites still exist, and if the plan has a "Measure first" section, run that first.

## Picking up an item in any harness

Every plan carries its own handoff in its "Resume here" block, and this section is the one place the rules that apply to every item
are written. Nothing else has to be passed along. A test fails if an open roadmap item has no plan, if a plan names no item, or if a
plan has no block or no valid status, so this cannot quietly rot.

### Start

1. Open the item's plan and read "Resume here" first. If Status is "In progress" or "Blocked", the last green commit (and the branch, if one was taken) and
   the next action are in it. Start from those, not from step 1.
2. Check the plan against the tree. The date and the master commit it was written against are under the title. Confirm the symbols it
   cites still exist before relying on them. "I could not find X" in a plan means exactly that.
3. Read `AGENTS.md`, then load the SageFs skill (`skills/sagefs/SKILL.md`). The REPL is the inner loop and `dotnet build` and the test
   suite are the final gate. A harness that cannot reach the SageFs MCP server says so and uses the narrowest build it can, never a
   build after every edit.
4. Check the daemon runs the build you are working against: `dotnet fsi scripts/install-local.fsx -- --status`. Make your own session in
   your own worktree. A git worktree is a routing boundary, so do not use a session rooted somewhere else.
5. Work on master and commit to it as each step goes green. Take a temporary branch and worktree (`plan/<item-id>`) only when a cohort or a
   parallel agent needs its own checkout, merge it back to master as soon as it is green, and delete it. If you took one, write it into
   "Resume here" straight away.
6. Read "Measure first" and "Decisions for Will". A measurement that can end the item runs before any code. A decision that blocks a
   step gets an answer from the maintainer before that step.

### While working

- Failing test first. Commit it red with the plan's step number, then the change that turns it green. Every other commit is green on its own.
- Take a lease (`acquire_full_build_lease`, `acquire_test_suite_lease`) before a build or test run you start, and release it.
- Tests and features do not wait on a wall-clock deadline for work that is making progress. Wait on an event or a state change, and
  remove the cause of a race instead of tolerating it.
- Conventional Commits, with the attribution lines the harness or `AGENTS.md` asks for. A commit that touches the wire surface carries
  `Plugin: done ...` or `Plugin: n/a ...` (the file list is in `AGENTS.md`), and the plugin lands first.
- Writing (docs, comments, commit messages) is plain English: no em dashes, no "not X, but Y" pairs, no marketing words.
- Update "Resume here" in the same commit as the work:
  - **Status** is `Not started`, `In progress (step N of M)`, `Blocked (on X)`, `Stopped: <why>` (the work was measured or tried and ended on purpose; the item stays on the roadmap with the reason) or `Done`.
  - **Branch and worktree** (only when you took one) and **Last green commit** say where the work is.
  - **Next action** is stated exactly enough that a stranger could do it.
  - **Decisions made** records each decision with its date and who made it.
  - **Surprises** records what the plan got wrong or missed. A surprise that changes the plan changes the plan, in the same commit.

### Stopping part-way (a new tool, person or session)

Commit what works to master (or to your temporary branch, if you took one), even if the item is unfinished. Set Status, Last green commit and Next action so someone with no
other context can carry on. Leave nothing running: release leases and stop the sessions you created. Do not write a separate handoff
file. The block in the plan is the handoff.

### Finishing

1. Run the unfiltered suite (`dotnet <tests dll> --summary`) and the ratchet lane (`--ratchets`). A filtered run is never the
   acceptance check.
2. Add the item's landmark (a file and a symbol) in `scripts/roadmap/RoadmapItems.fs`, run `dotnet fsi scripts/gen-roadmap.fsx`, and
   set Status to `Done`. The item moves to Built by itself once the landmark is in the tree, and the guard test requires Status `Done`
   exactly when the item is Built.
3. Shipping is `dotnet fsi scripts/ship.fsx`: one push to master is one release, and only with the maintainer's go. Run it as
   `SAGEFS_KEEP_GOING=1 dotnet fsi scripts/ship.fsx` so one pass reports every failing tier. The retry of a failed gate is the same
   command on the same commit, because pass records belong to the commit.
4. After the work merges, tidy up: `get_workspace_hygiene`, then `tidy_workspace` with the plan id it returns.

## The plans

Sizes are against recent pace: S is days, M a week or two, L more. Where a plan has phases the size of each is given.

### Live testing

| Item | Size | First failing test |
|:--|:--|:--|
| [Debug failing tests in VS Code](debug-failing-tests-in-vscode.md) | M for the Next scope, L overall | `holdBody` carries the session id |
| [The debug-row journeys run green](debug-row-journeys-run-green.md) | S to M | none new: `oneTestRow` and `threeTestsOneAttach` pass from the tier after one hand run against a real attach |
| [Bound what a connection is sent on connect](bound-the-connect-catch-up.md) | S to M | a catch-up for a 10,000-file session writes a fixed number of frames |
| [Markers on individual Expecto cases](markers-on-expecto-cases.md) | M | two cases under one `testList` get two lines |
| [Confirm builds without restarting the worker](confirm-builds-without-restarting-the-worker.md) | L | a verifying session is always refused a venue |
| [Live testing numbers on bigger projects](live-testing-numbers-on-bigger-projects.md) | M | a latency record with no machine is refused |
| [run_tests reuses proven results](run-tests-reuses-proven-results.md) | M | a stored pass under the current key is reused |
| [Mutation testing in the live session](mutants-in-the-live-session.md) | S for the experiment, L for the feature | apply a mutant, the covering test fails, revert, it passes |
| [Test selection that learns](test-selection-that-learns.md) | S for the recorder, the model unsized | ordering is a permutation of its input |

### Hot reload

| Item | Size | First failing test |
|:--|:--|:--|
| [A REPL eval changes the running app](repl-eval-reaches-the-running-app.md) | M | a grafted method is sent again by the next save |
| [Level the REPL after a patch](level-the-repl-without-losing-it.md) | M | an idle REPL with nothing to lose levels |
| [Type changes keep your running state](type-change-keeps-state.md) | L | `FieldZero` for every kind (property) |
| [Adding a field without a restart](delta-adds-fields-and-types.md) | L | a capability probe, then `addLambdaCapturing` |
| [Refresh CSS and static files](browser-refresh-beyond-pages.md) | S to M | the two copies of `devreload.js` are byte-identical |
| [Use the F# compiler's own deltas](use-the-fsharp-compilers-deltas.md) | S for the seam, M to L for the adapter | the emitter wrapped as a source gives the answer `DeltaChain` gave |
| [Breakpoints in patched code](breakpoints-in-patched-code.md) | A is S, C is M, B is M to L | a detour save reports `NoSymbolsInPatch` |
| [Redefine a value a handler renders](redefine-a-value-a-handler-renders.md) | M to L | a concatenated read returned ends in the response |
| [Hot reload without turning tiering off](hot-reload-without-tiering-off.md) | S if the first measurement closes it, M if not | an entry point is never patched before it is promoted |

### The REPL and editors

| Item | Size | First failing test |
|:--|:--|:--|
| [Sessions learn the load order](sessions-learn-the-load-order.md) | stopped: the premise was not borne out | none, the code is on `archive/2026-11-07/rescue-load-order` |
| [A Jupyter kernel that installs and works](jupyter-kernel-that-works.md) | M, then M for the value tree | an `execute_request` is answered with `status` ok |
| [A notebook over a live session](a-notebook-over-a-live-session.md) | L, M for the first slice | `CellSplitter.split` on `;;` and blank-line files |
| [Faster time to Ready](faster-time-to-ready.md) | S to measure, S to M for the first change | the plan segment excludes FSI creation |
| [Rider, Zed, Helix and Emacs](editors-beyond-vscode-and-neovim.md) | S for the contract, M for Emacs, L for Rider | the thin-editor endpoints exist and are mapped |
| [Eval output shaped for the agent that reads it](eval-output-for-an-llm.md) | S for head and tail, M for the print limits | a clipped result keeps its head and its tail |
| [A cost line on a slow or allocation-heavy eval](eval-cost-line.md) | S | a fast small eval carries no line |
| [A profile tool](profile-tool.md) | M | the rows of a CPU report sum to the sampled total |
| [Session options you can set](session-options.md) | M | a Server GC choice becomes `DOTNET_gcServer=1` in the host's environment |
| [One rule decides what a worker's report writes into the registry](session-status-reads-write-the-registry.md) | S to M | a status read for a worker that reports Starting over a Ready registry writes nothing |
| [Check the whole project with a file replaced](project-check-with-overlays.md) | M | an overlay for a file the project does not compile is refused with the project's files |
| [A head-to-head benchmark for REPL servers](repl-benchmark-against-other-tools.md) | M, then one run per page | a result with no machine, load or version is refused |

### Agents and cohorts

| Item | Size | First failing test |
|:--|:--|:--|
| [Walk the rest of the symbol ladder](walk-the-rest-of-the-symbol-ladder.md) | S, M with the shared contract | a name that stops at its parent names the nearest candidates |
| [A token for every daemon call](a-token-for-every-daemon-call.md) | L | a route with no declared access cannot be added |
| [Member tokens survive a restart](tokens-survive-a-restart.md) | M | a revoked token is still revoked after restore |
| [A token per sub-agent in Claude Code](a-token-per-subagent-in-claude-code.md) | S for phase 0, M for phase 2 | a string-referenced server is reported as sharing the parent |
| [Confirmation gates in every agent host](confirmation-gates-in-every-agent-host.md) | M | each destructive tool is `HostMustAsk` or `ServerWillAsk` |
| [A REPL guard for other agents](repl-guard-for-other-agents.md) | M | the Claude adapter's output matches today's, byte for byte |
| [Check the scope before the write](check-the-scope-before-the-write.md) | M, L with four hosts | a write onto another member's claim is refused naming the holder |
| [Watch your agents from your phone](watch-your-agents-from-your-phone.md) | L, M for the read-only view | the viewer listener maps no route that mutates |
| [Claim an interface](claim-an-interface.md) | M | claim overlap is symmetric and total over every scope pair |
| [Plan a cohort as code](plan-a-cohort-as-code.md) | S for the builder, M with the applier | two members claiming one file give `ClaimsOverlap` |
| [Fork a cohort or a session](fork-and-rewind.md) | M for the offline cohort fork | a fork departs every member and seats the forker |
| [A cohort across machines](a-cohort-across-machines.md) | L | a duplicated `AcquireClaim` envelope returns the first outcome |
| [Ask the live codebase in F#](ask-the-live-codebase-in-fsharp.md) | M to L, then S | every role's verdict equals `admitTool` for the matching tool |
| [Drive real agents as cohort members](drive-real-agents-as-members.md) | L | an edit at `../outside.fs` is rejected `OutsideCheckout` |
| [A small tool list for a first session](small-default-tool-list.md) | M, M | the registered tools' definitions weigh no more than the recorded budget |
| [A big landing waits on the run's progress](big-landings-pass-the-verifier.md) | M for the stop, S to M for the smaller set | a run that is still reporting past the old budget is `Advancing`, and one that stopped reporting for a window is `Stalled` |
| [A conductor can clear a lapsed member's claims](lapsed-members-claims-can-be-cleared.md) | S for the command, M with the tool and the panel | a conductor releases an orphaned claim with no present member to take it |

### Dashboard

| Item | Size | First failing test |
|:--|:--|:--|
| [More settings editable](more-settings-editable.md) | M | a saved per-test timeout is in force after a restart |
| [Panels that follow your context](dashboard-follows-context.md) | M | the hidden live-testing panel's reason links to enable it |
| [Move and dock panes](move-and-dock-dashboard-panes.md) | L | moving a pane leaves every pane placed exactly once |
| [Friction reports you can read first](friction-reports-you-can-read-first.md) | M | the bytes the transport gets are the bytes the preview showed |
| [A command palette and one timeline](command-palette-and-one-timeline.md) | palette M, timeline L | every keyboard action is a palette command |

### Platform and install

| Item | Size | First failing test |
|:--|:--|:--|
| [Hot reload on Windows and macOS](hot-reload-on-windows-and-macos.md) | M | a platform nobody listed is Unproven |
| [Start on login for Windows](start-on-login-windows.md) | S for files and tests, M with a Windows check | the task definition parses |
| [The getter sandbox beyond Linux x86-64](getter-sandbox-beyond-linux.md) | M | an aarch64 program refuses socket and connect |
| [A sandboxed getter leaves no filtered thread behind](sandboxed-getters-keep-the-pool-clean.md) | M | after a getter that queues pool work in a child process, no thread but the sandbox's reads `Seccomp: 2` (done in 97252b11) |
| [Admit heavy work by measured cost](admit-heavy-work-by-measured-cost.md) | M | a 6 GiB build is queued with numbers when 5 GiB is free |
| [One command to set SageFs up in your agent](one-command-stdio-setup.md) | S for snippets, M with the command | every client's snippet is valid JSON and names `sagefs mcp` |
| [Help for one tool in sagefs call](call-help-lists-parameters.md) | S | `--help` after a tool name parses to a request to describe that tool (done) |
| [The daemon tidies the workspace by itself](hygiene-that-runs-itself.md) | L | a merged worktree used an hour ago is kept, one quiet for a day is not |

## Where to start for hot reload and live testing

Items whose first step is a measurement or a guard that decides the rest come first, because they cost days and can change the order.

1. `hot-reload-without-tiering-off`, step M1 (re-measure the cost; it may close the item).
2. `faster-time-to-ready`, the corrected stage timing (the cumulative scan clock mislabels the phases).
3. `live-testing-numbers-on-bigger-projects`, the count and load run (it feeds the confirm-build, reuse and ordering plans).
4. `browser-refresh-beyond-pages` (S to M, depends on nothing).
5. `delta-adds-fields-and-types`, phase A (unlocks type changes keeping state, and some REPL grafts).
6. `repl-eval-reaches-the-running-app` and `level-the-repl-without-losing-it` (the pair that makes the REPL and the running app agree).
7. `debug-failing-tests-in-vscode` (the VS Code session id fix and the debug verdict record are file-disjoint).
8. `markers-on-expecto-cases` (the tree-sitter dump and the `categorize` token fix are file-disjoint).
9. `breakpoints-in-patched-code`, piece A (tell the user whether a breakpoint will bind).

`redefine-a-value-a-handler-renders` and `hot-reload-without-tiering-off` share files (`ValueReads.fs` and the reflection-read simulations), so do not land them in the same week.

## Defects found while planning

Small and real, each with a step in the plan that fixes it. None is fixed yet.

- `mint_member`'s reply tells the caller to run `revoke_member member=...`, but the parameter is `member_id`.
- Nothing clears the "REPL is behind" state after an in-worker FSI reset; only a worker swap does.
- Saved live timeouts from the Settings panel are not applied when the daemon starts, and two rows ignore their environment variables.
- The eval timeline stamps every cell `Succeeded`, so stored history cannot tell a failed cell from a good one.
- No command restores a vacant conductor seat in a cohort.
- `categorize` matches `ui` and `arch` as substrings, so "building" reads as Browser and "search" as Architecture.
- `list_references` spends its whole 60 second budget to say "none found" for a name that does not exist.
- The Readme and the roadmap say nothing opens a ZMQ socket against the Jupyter kernel; `JupyterKernelSocketTests.fs` does.
- The "opens are 60 to 90 percent of warmup" figure rests on a cumulative clock, not a phase duration.
- `samples/from-jupyter/notebook.fsx` still describes a pre-warmed standby session that no longer exists.
