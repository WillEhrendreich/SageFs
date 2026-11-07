/// The roadmap's items, in the order they read on the page. Edit here, then run scripts/gen-roadmap.fsx.
/// Status is never written here. An item that names a landmark becomes Built when the landmark is in the tree.
module SageFs.RoadmapItems

open SageFs.Roadmap

let private item
  (id: string)
  (title: string)
  (area: Area)
  (horizon: Horizon)
  (arrival: Arrival)
  (links: string list)
  (summary: string)
  : Item =
  { Id = id
    Title = title
    Area = area
    Horizon = horizon
    Summary = summary
    Arrival = arrival
    Links = links }

let private landmark (path: string) (symbol: string) : Arrival = Marked { Path = path; Symbol = symbol }

let items : Item list =
  [ // ---- Now: being built, days to a few weeks ----

    item "repl-says-when-behind" "The REPL says when it is behind" HotReload Now
      (landmark "SageFs.Core/SessionManager.fs" "ReplFreshness")
      [ "docs/how-hot-reload-works.md" ]
      "After a save is patched into your running app, the REPL and the live tests still run the build from before it. Nothing used to say so. Now the session carries a named state, and `get_session_status`, `list_sessions`, `send_fsharp_code`, `run_tests` and the dashboard card all say the REPL is behind, which declarations changed, and what bringing it level costs."

    item "agent-landings-reach-the-running-app" "An agent's landed work reaches your running app" Agents Now
      (landmark "SageFs.Core/Features/TrunkFollow.fs" "step")
      [ "docs/how-hot-reload-works.md"; "docs/mcp-tools.md" ]
      "When agents land work in the shared trunk, the app running in the trunk session picks it up live with its state kept. `get_cohort_status` and the dashboard show each landing's result per file, including the mechanism, and a landing that needs a restart says why. A landing that fails verification never reaches the app."

    item "daemon-stops-sessions-an-agent-forgot" "The daemon stops a session an agent forgot" Agents Now
      (landmark "SageFs.Core/SessionReaper.fs" "SessionReaper")
      [ "docs/configuration.md" ]
      "An agent makes a session, wanders off, and the worker sits there for days. Now the daemon stops it once the connection that made it has been gone and the session has been idle for 12 hours, and says why. A session you made from the dashboard, the CLI or an editor is never stopped this way. `SAGEFS_ABANDONED_SESSION_HOURS` sets the time, and 0 turns it off."

    item "cohort-export-for-play" "Keep a cohort run and play it back" Agents Now
      (landmark "SageFs/CohortExportCommand.fs" "CohortExportCommand")
      [ "docs/progress.md"; "Readme.md" ]
      "`sagefs cohort-export` writes a cohort's ledger from the local database to a file, with no daemon running, and `sagefs play` reads that file back. A finished cohort's rows get pruned after a while, so the export is the only way to keep a run. A file that mixes cohorts from different repositories is refused, with the scopes listed."

    item "slow-machines-get-fitting-timeouts" "Slow machines get timeouts that fit them" Platform Now
      (landmark "SageFs.Core/MachineTier.fs" "MachineTier")
      [ "docs/TROUBLESHOOTING.md"; "docs/configuration.md" ]
      "On an old quad core with a spinning disk a session never reached Ready, because the 30 second silence limit killed the worker mid-build and the retries repeated the same 30. SageFs now probes the machine, files it as Fast, Standard, Constrained or Minimal, and scales the waits that are for the machine. A start that runs out of patience retries with a longer one, and a give-up names the wait, the attempts and what to set. On that machine it now reaches Ready in about a minute cold and 17 seconds warm."

    item "workspace-hygiene" "SageFs tidies what agents leave behind" Platform Now
      (landmark "SageFs.Core/WorkspaceHygiene.fs" "classify")
      [ "docs/agents.md"; "docs/configuration.md" ]
      "Agents leave worktrees, branches and gate checkouts behind, and on my machine 65 old gate records alone came to 17.6 GB. A dashboard panel, two MCP tools and `sagefs hygiene` list the leftovers and tidy only the safe ones, after you confirm the exact plan you were shown."

    item "run-app-saves-patch-in-place" "Saves to run_app apps patch in place" HotReload Now
      (landmark "SageFs.Host/RunAppDelta.fs" "SaveResult")
      [ "docs/hot-reload.md"; "docs/how-hot-reload-works.md"; "docs/decisions.md" ]
      "A save to an app you started with run_app is handed to the runtime as a metadata delta, so the process keeps its state. On the test fixture a save was served in 1.8 to 2.6 seconds against 6 to 8.5 for the restart it replaced, and `SAGEFS_METADATA_DELTA=off` puts the old behavior back."

    item "generic-functions-patch-in-place" "Generic functions patch in every instantiation" HotReload Now
      (landmark "SageFs.Core/Middleware/HotReloadCore.fs" "prepareGenericUnit")
      [ "docs/hot-reload.md"; "docs/decisions.md" ]
      "A save to a generic function reaches every instantiation the runtime compiled, including a float or struct first used after the save. If your code calls MakeGenericMethod anywhere, a save to a generic function still restarts and names why."

    item "hot-reload-latency-measured" "Hot reload save times are measured" HotReload Now
      (landmark "SageFs.Tests/HotReloadLatency.fs" "SaveStamps")
      [ "docs/how-hot-reload-works.md" ]
      "A test tier times saves against a real running app and fails if the p95 drifts. It's one machine and one small app, and I have no Microsoft figure to set it against."

    item "self-hosting-builds-its-own-core" "A session builds against the Core its project brings" Isolation Now
      (landmark "SageFs.Core/CoreEvidence.fs" "CoreEvidence")
      [ "docs/decisions.md"; "docs/how-isolation-works.md" ]
      "A session on a project in a repo that builds its own SageFs.Core now compiles against that Core, not the daemon's older one. Before, building SageFs.Tests in a worktree failed against the old Core and printed a wrong compile-order hint. A project that can't be read is refused with the path and the reason. The daemon and its FSI host still match Cores by version number, so a worktree Core and an older daemon can disagree at load time until they're built together."

    item "waiting-on-a-rebuild" "A rebuild is waited on, not skipped" Repl Now
      (landmark "SageFs.Core/ReadyWait.fs" "ReadyWait")
      [ "docs/mcp-tools.md" ]
      "`get_session_status` with `wait_seconds` now waits for a rebuild in progress and answers when the new worker is ready, or Faulted with the build's own error. It used to answer \"not needed\" while the rebuild was still running, so a caller had to poll."

    item "receipts-say-what-source-they-ran-on" "A test receipt says what source it ran on" LiveTesting Now
      (landmark "SageFs.Core/Features/SourceState.fs" "SourceState")
      [ "docs/mcp-tools.md"; "docs/how-live-testing-works.md" ]
      "If you edit a test file and don't rebuild, `run_tests` used to say \"3 passed\" with no warning. Now every receipt carries whether the source is in sync, stale (naming the files), being rebuilt, or unknown and why. A pass over stale source reads \"passed, but on STALE source\" and is never plain AllPassed. It's also on `get_session_status` and `list_sessions`, next to the REPL-behind state."

    item "pending-tests-are-skipped" "A pending test is skipped, not passed" LiveTesting Now
      (landmark "SageFs.Core/Features/LiveTestingExecutors.fs" "ExpectoDisposition")
      [ "docs/how-live-testing-works.md" ]
      "An Expecto `ptest` used to run its body and count as passed. It's now reported as skipped and never run, and a run with one is Incomplete, not AllPassed. If anything is focused with `ftest`, the unfocused tests are reported as skipped too, matching what Expecto itself does."

    item "edits-during-worker-swap-judged" "Edits during a worker swap are judged" LiveTesting Now
      (landmark "SageFs/LiveCheckRelay.fs" "LiveCheckRelay")
      [ "docs/how-live-testing-works.md"; "docs/decisions.md" ]
      "An edit typed while the confirming build replaces the worker used to be lost. A type-check or an eval now waits for a Ready worker and believes an answer only from the worker it asked."

    // ---- Next: weeks to a couple of months ----

    item "hygiene-that-runs-itself" "The daemon tidies the workspace by itself" Platform Next
      NoLandmarkYet
      [ "docs/agents.md"; "docs/configuration.md"; "docs/plans/hygiene-that-runs-itself.md" ]
      "Workspace hygiene lists what agents leave behind, but it only tidies when a person or an agent asks, so on my machine about 25 worktrees and 49 GB piled up over five weeks, some holding commits or uncommitted files that exist nowhere else. The daemon will run it on a schedule. It commits uncommitted files to a salvage branch first, removes only what is provably safe once it has been quiet for a day, and writes every action and its tip sha to a ledger. Work that exists nowhere else is named in the status line, the dashboard card and `sagefs hygiene`, with how old the oldest is."

    item "repl-eval-reaches-the-running-app" "A REPL eval changes the running app" HotReload Next
      NoLandmarkYet
      [ "docs/how-hot-reload-works.md"; "docs/plans/repl-eval-reaches-the-running-app.md" ]
      "An eval reaches an app you started from FSI, but not one started with run_app, where only saved files get through. There are two ways in. One writes the evaluated declaration to source and lets save, build and delta carry it, which costs the build. That one is in: an eval that names its file and line writes the declaration back, journaled so it undoes, and it is now driven end to end against a running run_app app in our own tests. The eval writes the declaration back, the file change reaches the worker, and the running app serves the new code. Measured here, the whole route takes about 5.6 seconds, most of it the build. The other grafts FSI's own IL into a delta. In a spike that worked and was served in milliseconds, but the IL lives in the FSI host and the delta chain lives in the worker, and nobody has worked out how it crosses. That one isn't built."

    item "level-the-repl-without-losing-it" "Level the REPL after a patch without losing it" HotReload Next
      NoLandmarkYet
      [ "docs/decisions.md"; "docs/how-hot-reload-works.md"; "docs/plans/level-the-repl-without-losing-it.md" ]
      "Bringing the REPL level with a patched app means a fresh FSI host, which takes about 2 seconds and keeps the app's process and state, but it wipes your definitions and an init script's, and it would break live testing's coverage maps and kill a test run in flight. The only remedy today is a rebuild reset that stops the app. Half of the fix is already in: the daemon re-fetches maps and discovery after every in-worker host swap, refused through a gate rather than half-refreshed when the worker is unsettled. What is still missing is the idle-and-empty check inside the eval actor, which cancels a running eval before a hard reset so the check cannot live outside it, and the host's own in-flight test count, because a test run runs in the host and the actor's idle phase proves nothing about it. With those, the refresh becomes a background step with the loud state held until it finishes."

    item "live-tweak-front-door" "Nudge a value in the running app" HotReload Now
      (landmark "SageFs/McpNudge.fs" "nudgeValue")
      [ "docs/hot-reload.md"; "docs/mcp-tools.md" ]
      "The `nudge_value` tool lists the literals and expressions in a file the session owns, writes one of them back as just that range, journals the write before it lands, and undoes it exactly. A stale address is refused with what moved, and a write that does not type-check shows up in the reload verdict and rolls back. Agents and scripts can use it today."

    // Split in two, because one item was carrying three surfaces and a stage, and a landmark can only speak for one of
    // them. The dashboard knob is in the tree; the editor controls and the pre-save apply are not, so they stay open
    // and say so.
    item "live-tweak-knob" "A knob for the value you are nudging" HotReload Now
      (landmark "SageFs.Core/Features/Tweak/BindingTweak.fs" "stateAndControl")
      [ "docs/hot-reload.md" ]
      "The dashboard's Live Bindings pane has a knob on every value a project file holds. A row says where its value lives (`in source`, `not in a file`, `ambiguous` when two files declare the name, and the reasons a write cannot be offered), and the control follows what the file actually spells: a real gets a drag handle, step buttons and the arrow keys, a bool a switch, a string or a character a field, a formula (`gravity * 2.0`) an expression field, and a value made of parts becomes rows of its own. The step count is turned into the literal by the daemon, so `1.0` stays `1.0`, `0x1F` stays hex and units stay. Every write goes through the same nudge door the tool uses, with the hash the row last showed, its own undo and redo, and a refusal that says the rule and what to do. What it does not do yet: apply a value to the running app before you save it, and say when the app copied a value at startup."

    item "live-tweak-in-the-editors" "A knob in the editors too" HotReload Next
      (landmark "sagefs-vscode/src/NudgeKnob.fs" "let register")
      [ "docs/hot-reload.md" ]
      "The dashboard has the knob, and now both editors do too. Neovim scrubs with Alt-k and Alt-j: each press runs the same inspect-locate-bump-set flow `:SageFsNudge` runs, so the rules and the undo are the daemon's, and a count multiplies the steps. VS Code scrubs by dragging across a number with the multi-cursor modifier held, because its API never reports that a modifier is down; what it reports is the second cursor an Alt+click adds. Both write through `nudge_value`, so the journal, the seen hash, the refusal and the type-check rollback all apply."

    // Cross-repo landmarks DO resolve: a landmark may name `../sagefs.nvim/...`, and `codeUrl` in
    // Roadmap.fs renders the link against that project's own repo rather than this one. So living in the
    // plugin is no longer a reason to stay open, and the item below is no longer open for that
    // reason either. The full attach inside our own tests is now in the tree, so the item is built.
    item "neovim-debug-a-failing-test" "Debug a failing test from Neovim" Editors Next
      (landmark "SageFs.Tests/DebugAttachJourneyTests.fs" "journeyTests")
      [ "docs/LIVE_TESTING_GUIDE.md" ]
      "The daemon side is done: one route holds the test and hands back a process id to attach to, and another releases it. The Neovim side is done too, in the sagefs.nvim repo: `:SageFsDebugTest` puts a failing test on the board and wires nvim-dap to those routes, on `<leader>rtg` and `D` on a panel row. The release goes out on `configurationDone`, when the adapter has really attached, so a slow attach is the daemon's to wait out rather than a grace period on this side. And the attach is now proven in our own tests: a real debugger attaches to the process the daemon names, lists that process's threads, and the held test runs when the debugger lets it go."

    item "neovim-catches-up" "The Neovim plugin catches up with the daemon" Editors Next
      (landmark "../sagefs.nvim/lua/sagefs/dashboard/state.lua" "handlers.eval_heartbeat")
      [ "docs/sse-events.md"; "docs/mcp-tools.md" ]
      "The plugin shows hot reload's pending and never-entered states, the live-values pane with its Safe mode click, workflow switching and the session's own app state, and it names the session on every session-scoped call. It reads a tool's answer as its first text block, finds a nudged value by the range the daemon reports, sends the session's id with every nudge, shows a vetoed landing with who vetoed it and why, and lists the files a re-sign left on the old method with how sure each match is. The slow-eval heartbeat is wired too: while an eval runs the output section ticks its elapsed time, a beat aimed at another file cannot corrupt it, and the line stops the moment eval_result lands."

    item "debug-failing-tests-in-vscode" "Debug failing tests properly in VS Code" LiveTesting Next
      NoLandmarkYet
      [ "docs/LIVE_TESTING_GUIDE.md"; "docs/how-live-testing-works.md"; "docs/plans/debug-failing-tests-in-vscode.md" ]
      "Debugging a failing test from the VS Code lens works for one test at a time. The attach is now proven in our own tests, the same case the Neovim item leans on. Still open is debug runs updating the gutter. Several tests at once and breakpoints in evaluated code are further out."

    item "bound-the-connect-catch-up" "Bound what a connection is sent on connect" LiveTesting Next
      (landmark "SageFs.Core/SseWriter.fs" "formatAnnotationsAvailableEvent")
      [ "docs/sse-events.md"; "docs/how-live-testing-works.md"; "docs/plans/bound-the-connect-catch-up.md" ]
      "A connection is sent the list of files that have annotations in one frame, and an editor asks for the ones it has open. Before, a connect sent the annotations and coverage badges of every file, one frame each, and on a project of 1,629 files that had sent 2.7 MB after 100 seconds without finishing. Now a connect to a session on SageFs.Tests, with 17,973 tests discovered, is 14 frames and 116 KB. While a suite runs, only the files whose annotations changed are pushed."

    item "live-requests-not-dropped" "No live request dropped during a swap" LiveTesting Next
      (landmark "SageFs/Mcp.fs" "requireToolFor")
      [ "docs/how-live-testing-works.md" ]
      "A check that waited out the 30 second swap bound was dropped instead of retried, and running affected tests or discovery dropped a request that found no worker. The gate now parks on the manager's own AwaitReady for the bound a rebuild's own wait uses, then judges the state that wait released, so a code tool waits for the replacement instead of being refused the moment a swap begins."

    item "each-agent-its-own-member" "Each agent counts as its own member" Agents Next
      (landmark "SageFs.Core/Capability.fs" "CapabilityState")
      [ "docs/agents.md"; "docs/mcp-tools.md"; "docs/decisions.md" ]
      "Sub-agents of one Claude Code session share one MCP connection, so SageFs saw them as a single member and the build and test leases collided. The conductor now mints a token per run with `mint_member`, each token is its own member with its own claims and leases, and a token outranks the connection it arrives on. A platform sets it in a header or in MCP `_meta`, never as a tool argument. Tokens live in memory, so a daemon restart means minting new ones."

    item "member-roles-and-tool-lists" "A member's role says which tools it can call" Agents Now
      (landmark "SageFs.Core/Capability.fs" "listedTools")
      [ "docs/mcp-tools.md"; "docs/decisions.md" ]
      "An Observer used to be able to call send_fsharp_code, because authority only gated the cohort's own tools. A member token now has one of four roles (Observer, Analysis, Verifier, Implementer), each a set of tool classes, and a call outside the role is refused with the role, the tool and what to do. `tools/list` shows a token only what it can call, and `SAGEFS_IDENTITY_POLICY=TokenRequired` makes a connection with no token read-only."

    item "member-id-is-not-a-credential" "A member's id is no longer the bearer handle" Agents Now
      (landmark "SageFs.Core/MemberTable.fs" "ofConnectionHandle")
      [ "docs/mcp-tools.md"; "docs/decisions.md" ]
      "A member's id in `get_cohort_status`, the cohort frame and the ledger was the MCP session id, which is the credential a request is bound to, so anyone who read the status could act as the conductor. The id is a fingerprint of it now, and a test fails if any cohort output carries a handle."

    item "bridge-keeps-its-member-id" "A stdio bridge stays the same cohort member across a daemon restart" Agents Now
      (landmark "SageFs.Core/MemberTable.fs" "ClientKey")
      [ "docs/agents.md"; "docs/mcp-tools.md"; "docs/decisions.md" ]
      "After a daemon upgrade the stdio bridge re-initialized with a new MCP session, and a member's id was a fingerprint of the session id, so the conductor came back as a stranger while its old self held the seat for up to 30 minutes. The bridge now makes a random key when it starts and sends it on every request, the daemon names the member after a fingerprint of that key, and the same client is the same member through a restart or a reconnect. The reaper also counts an open push stream and any admitted call as presence, where it used to count only an eval, so a conductor that waits on its agents keeps its seat."

    item "a-claim-path-means-one-thing" "A claim path means one thing" Agents Now
      (landmark "SageFs.Core/Cohort.fs" "ClaimPath")
      [ "docs/mcp-tools.md" ]
      "`src/Foo/../Bar/x.fs` did not overlap `src/Bar/x.fs`, so two members could hold the same file and a prefix check could be walked around. Claim paths are canonical at the boundary now, and a path that leaves the repo is refused."

    item "cohort-veto-and-delegation" "Veto and delegate in a cohort" Agents Now
      (landmark "SageFs/McpCohortTools.fs" "delegateConductor")
      [ "docs/mcp-tools.md" ]
      "The conductor can hand the seat to a present member with `delegate_conductor`. A seated Implementer, Verifier or the conductor can `veto_landing` with a reason, the conductor clears it with `resolve_veto`, and a landing's own requester can `withdraw_landing`. A veto on a landing that already landed is refused, not ignored, and `get_cohort_status` and the dashboard panel show who vetoed and why. A vacant seat still can't be filled from the tool surface."

    item "cohort-seat-a-conductor" "A person can seat a conductor" Agents Now
      (landmark "SageFs/CohortSeatCommand.fs" "CohortSeatCommand")
      [ "docs/mcp-tools.md" ]
      "When a cohort's conductor leaves or goes silent the seat stays empty, and no tool can fill it, so a cohort whose conductor left could only be reset by wiping its ledger. `sagefs cohort seat <member id>` fills it: the person who runs the daemon names a present member, the operator credential is checked, and the ledger records `ConductorSeated` with the vacancy it ended. A held seat, an absent member and a member under a token are refused with the reason, and no MCP tool or token can build the command, so an agent cannot take the seat. With no member id the command lists who is present and where the seat stands."

    item "more-settings-editable" "More settings editable from the dashboard" Dashboard Next
      NoLandmarkYet
      [ "docs/configuration.md"; "docs/plans/more-settings-editable.md" ]
      "The Settings panel edits the warm-up, build and abandoned-session limits, the machine tier, two on-off switches, the test timeouts, the MCP port, the default working directory, reflection mode and tiering, each resolved across layers (default, global file, repo file, environment variable, flag) with its source and the value in use shown. The bind host and data directory are shown and read-only. Most of the other environment variables, the local data retention settings among them, still have no row."

    item "live-bindings-watch-view" "A live bindings view you can switch and keep" Dashboard Next
      NoLandmarkYet
      [ "docs/plans/live-bindings-watch-view.md"; "docs/plans/more-settings-editable.md" ]
      "The Live Bindings pane draws a badge, a type, a wide pin and the same sentence on every row, so at the default dock two of ten bindings show, and it looks the same in every workflow. It is becoming one configurable watch view with presets, a recommended preset for each workflow, and a choice kept for each app and workflow through the settings layers. A value's relation to its source is one word, and Save says what it writes: a tweak is saved to its literal, a value the program has changed is reset or taken as the new initial value after a confirmation, and a value built by a call points at the literal inside the function that builds it. Watch tabs with expression rows and per-row views follow, and so does what the pane shows while the test loop runs."

    item "dashboard-follows-context" "Dashboard panels that follow your context" Dashboard Next
      (landmark "SageFs/DashboardPanelContext.fs" "PanelContext")
      [ "docs/plans/dashboard-follows-context.md" ]
      "One pure function decides, for every context panel, whether it is shown, hidden with a reason, or quiet because it has nothing to report: hot reload, live testing, cohort, friction, diagnostics, failing tests and alarms. A hidden panel leaves a one-line reason where it would have been, and offers the next step when there is one: live testing off carries a Turn on button, and hot reload names the workflow switcher. A panel with nothing to report draws nothing, diagnostics that were never checked say so instead of reading as clean, and the cohort panel shows while a cohort is running or still holds an orphaned claim or a landing. A browser matrix checks eleven states at 375, 768 and 1400 px. The live bindings becoming a real pane in REPL mode and a pane moving between regions are not built; they belong to the move and dock item."

    item "app-output-pane" "App output in its own pane" Dashboard Next
      (landmark "SageFs/AppOutputView.fs" "render")
      []
      "stdout and stderr from a running app get their own pane with follow, pause and search, so a chatty app stops burying your evals. The daemon holds one AppOutputPane per session, fed the same lines the transcript is fed from; the dock decides from that buffer and draws the answer, so the two cannot disagree about what the app wrote."

    item "new-session-dialog" "A guided new-session dialog" Dashboard Now
      (landmark "SageFs/NewSessionDialog.fs" "NewSessionDialog")
      []
      "A plus button on the Sessions list opens a dialog that finds projects under a directory you can edit, says in a line what each workflow means, and warns before you make a second session in the same directory, naming the one that exists. A refusal from the daemon shows in the dialog with its next action, and Create closes it into a starting card at once. The no-session picker's Open Directory card still has its own form."

    item "publish-the-loop-timings" "Published numbers for the REPL loop" Docs Next
      (landmark "docs/repl-loop-benchmarks.md" "The REPL loop, measured")
      [ "docs/repl-loop-benchmarks.md" ]
      "I timed an edit plus a check in a warm session against a build plus a filtered test on three projects, from 306 lines to 86,019. The check answered in 21 to 25 milliseconds on all three, because it does not care how big the project is, while the build took 2 to 52 seconds and cares a great deal. The table is in docs with the machine and the load averages it was measured under, and a snippet that cannot type-check returns its errors in 81 milliseconds, so the fast number is a real check rather than one that says fine."

    item "sse-events-stay-small" "A discovery event fits the stream it rides" Dashboard Next
      (landmark "SageFs/SseEvent.fs" "SseEvent")
      [ "docs/LIVE_TESTING_GUIDE.md" ]
      "A suite of 2,604 tests reached the editor as one SSE event of about 990 KB: the whole discovery list in a single `data:` line, replayed to every connection that catches up on /events. Size was not what broke it. The reader has no size limit and the daemon writes whole frames; what broke it was a buffer that outlived its connection, so a reconnect inherited the dead connection's partial line, and that is fixed and proven, zero parse errors with the reset and one without. What is left is cost: the daemon already fetches discovery over the worker wire, so a summary on the stream with the list behind a fetch takes a catch-up from 990 KB to a few dozen bytes. I would send the summary first and let a client that wants the list ask for it, and whichever shape I pick the editor, the plugin and the dashboard all have to read it."

    item "native-nuget-in-sessions" "Native NuGet packages load in sessions" Isolation Now
      (landmark "SageFs.Core/NativeResolution.fs" "NativeAssets")
      [ "docs/how-isolation-works.md" ]
      "Someone reported a `#r` NuGet package with a native library failing in plain FSI. I couldn't reproduce that on Linux: SQLite, SkiaSharp and LibGit2Sharp all load in plain FSI and in a bare SageFs session. What did fail was a project session, because the package's native library sits in the NuGet cache and the isolated host never looked there. The host now finds it from the project's restore, and a library that won't load is named with its package, this runtime and what to do. Windows, macOS and arm64 are untested."

    item "cross-file-signature-callers" "Callers in other files follow a signature change" HotReload Now
      (landmark "SageFs.Core/Features/CallerState.fs" "CallerState")
      [ "docs/hot-reload.md"; "docs/decisions.md" ]
      "When a save re-signs or removes a function, the reload report now lists every caller in another file that still calls the old one, with file, line and the next action, and it clears when that file is saved and patched. A caller saved on its own lands against the new definition. If the other files can't be searched it says so instead of staying quiet. A caller that needs no edit stays listed until a restart."

    item "tokens-bound-to-a-session" "A member token bound to one session" Agents Now
      (landmark "SageFs.Core/Capability.fs" "RouteBinding")
      [ "docs/mcp-tools.md" ]
      "A minted token now names the one session or checkout it may route to. A tool that takes a session id or a working directory is held to that on every call, a session list and the session resources are cut to it, and a refusal says which rule it hit and what to do. `mint_member` takes a `session_id`, and its `working_directory` has to be an absolute path."

    item "one-typed-list-of-destructive-tools" "One typed list of what counts as destructive" Agents Next
      (landmark "SageFs.Core/Affordances.fs" "destructiveTools")
      [ "docs/mcp-tools.md" ]
      "SageFs could say who may call a tool, but nothing said what calling it does to your work. There is now one `Set<ToolName>` on the same DU the authority gate reads, held to the registered tools by a test so a renamed tool cannot leave a stale name behind, and `tools/list` publishes it as MCP's own `destructiveHint`, so a client asks the server instead of keeping a copy of the list itself."

    // ---- Opened 2026-11-07 after reading FsiMcp (github.com/OnurGumus/FsiMcp, by Onur Gumus), a small F# REPL server for
    // agents. Will's rule is that SageFs does not lose to another tool on something a user feels. The first four are in
    // Next because a stranger meets them in the first minutes, and the benchmark is how the rest get checked.
    item "small-default-tool-list" "A small tool list for a first session" Agents Next
      NoLandmarkYet
      [ "docs/mcp-tools.md"; "docs/plans/small-default-tool-list.md" ]
      "A client that connects to SageFs is shown 72 tools, 111,052 characters of definitions, about 27,800 tokens by the usual rough count, before its first message. FsiMcp, a seven-tool F# REPL server, lists 5,606 characters, about 1,400, measured on a running 0.3.0 server. Even the ten tools a first session needs weigh 22,365 today, so cutting tools is not enough and the descriptions have to shrink too. I want a starter list for someone who has done nothing else, chosen through the authorization on the request so it stays inside the MCP rule that a list never varies by connection, with every other tool still callable by name and found through `discover_features`. A size ratchet that can only go down holds the whole catalog. It ships opt-in, and only becomes the default once a measured pass rate on a fixed task set is no lower than with today's list."

    item "one-command-stdio-setup" "One command to set SageFs up in your agent" Platform Next
      NoLandmarkYet
      [ "docs/client-setup.md"; "Readme.md"; "docs/plans/one-command-stdio-setup.md" ]
      "The durable way to register SageFs is `claude mcp add sagefs -- sagefs mcp`, and the docs already say why an HTTP registration loses its tools when the daemon restarts: the session id lives in the daemon's memory and the client never asks for a new one. `sagefs setup` now prints, or with `--write` merges, the right snippet for Claude Code, VS Code, Cursor and Claude Desktop, keeps every other server in the file, leaves a file it cannot parse alone, and installs the skill. The page is generated from the same code, and a test fails when it is stale. I have not yet read each client's own documentation for the file and key, so the page says the locations come from FsiMcp's README and what I know. From nothing to a first eval takes two shell commands now, and still five tool calls against FsiMcp's one; the seconds come from the benchmark, not from me guessing."

    item "eval-output-for-an-llm" "Eval output shaped for the agent that reads it" Repl Next
      NoLandmarkYet
      [ "docs/mcp-tools.md"; "docs/plans/eval-output-for-an-llm.md" ]
      "Part of this is already here. A result over 4,000 characters is cut, with a note that the whole value is still bound as `it`, and `printfn` from any thread cannot reach the MCP stream because the bridge and the host never share it. What is left: the cut keeps the start and drops the end, where an error or the last value usually sits; both kinds of session print with FSI's own limits, so a `let` of a large value echoes up to the cap every time; and I have not checked whether output a background thread writes between calls reaches the agent's next reply. I plan to keep head and tail, set print limits once for both session kinds, and pin the between-calls case with a test that drives the real stdio bridge. Echoing only the type of a `let` waits for a measurement."

    item "repl-benchmark-against-other-tools" "A head-to-head benchmark for REPL servers" Docs Next
      NoLandmarkYet
      [ "docs/repl-loop-benchmarks.md"; "docs/plans/repl-benchmark-against-other-tools.md" ]
      "I want to be able to say where SageFs is ahead of another REPL server and where it is behind, with numbers I can defend. The harness drives each server over stdio on the same small project: time to ready, first and follow-up eval, rebuild and continue, a hang, a stack overflow, a failed build, tool-list tokens, and steps from nothing to a first eval. It runs only on a quiet machine, refuses to record a number without the machine, load and versions, and publishes a dated page that credits each tool's author and links each loss to its plan. FsiMcp is the first comparison. Running someone else's package needs my go and a sandbox, so the plan says how, and no timing is written until then."

    item "eval-cost-line" "A cost line on a slow or allocation-heavy eval" Repl Later
      NoLandmarkYet
      [ "docs/plans/eval-cost-line.md" ]
      "FsiMcp ends an eval that took half a second or allocated 100 MB with one line of time, bytes allocated and GC counts, which tells an agent comparing two versions of a function which one did less. SageFs times every eval on the daemon, but that time includes the queue and the wire, and nothing records bytes or collections, so an agent has no such line. The wrapper in `Instrumentation.fs` that would have recorded eval durations has no caller. I plan to read the counters in the host around the eval, return them on the wire as typed fields, and print a line only above a threshold so a light eval costs no extra tokens. Whether to count the whole process or only the eval thread is something I will measure first."

    item "profile-tool" "A profile tool: where the time and allocations went" Repl Later
      NoLandmarkYet
      [ "docs/plans/profile-tool.md" ]
      "FsiMcp has a `profile` tool that runs your F# like an eval and reports where CPU time or allocations went, recorded over EventPipe from outside the process that runs your code. SageFs has nothing like it, and an agent that wonders why an eval is slow has to guess. I plan the same shape: methods by self time, your own code by total time, bytes by type and by the nearest frame of yours, recorded from the worker against the FSI host's process id, with the diagnostics libraries never loaded into the host. It adds two Microsoft packages, which I want to talk about first, and it is classed as an eval tool so a role without eval never sees it."

    item "session-options" "Session options you can set" Repl Later
      NoLandmarkYet
      [ "docs/configuration.md"; "docs/plans/session-options.md" ]
      "FsiMcp lets you set Server GC, an artifacts path that keeps build output out of the project, environment variables for the session process, and a prelude. SageFs already has the prelude, as `.SageFs/init.fsx`. It has no Server GC setting and no artifacts path for its own builds, and the host's environment list has a slot that nothing a user writes can reach. `config.fsx` accepted `InitScript`, `DefaultArgs`, `IsRoot` and `SessionName` and nothing read them; those three are removed and `DefaultArgs` now reaches the FSI host. `Gc = ServerGc` and `SessionEnv` are in: one named field each with a closed type, a poisoned variable refused with its reason. The artifacts path for the session's own builds is still to do, and the outcome gates that read the effect back from inside a real session."

    item "project-check-with-overlays" "Check the whole project with a file replaced, without building" Repl Next
      NoLandmarkYet
      [ "docs/mcp-tools.md"; "docs/plans/project-check-with-overlays.md" ]
      "FsiMcp has `check_project`: it type-checks a whole project with some files replaced by text you send, without a build and without touching the REPL session, so an agent can ask \"does this still compile\" before it lands an edit. SageFs has `check_fsharp_code` for a snippet and a build for the project, which costs minutes on a loaded machine. The compiler call that would answer it is in the tree twice, for the caller check and for `list_references`, and both need a loaded session. I plan a project check that gets the compiler arguments from a design-time MSBuild run, keeps them until the project file, `Directory.Build.props`, `Directory.Build.targets` or `Directory.Packages.props` changes, builds a stale referenced project once, stops when the client cancels, and returns diagnostics with path, line, column, severity, code and message. The pure core, its tests and a simulation come first, and the tool registration waits for the tool-list work."

    // ---- Later: months to a year ----

    item "type-change-keeps-state" "Type changes keep your running state" HotReload Later
      NoLandmarkYet
      [ "docs/granular-restart-scope.md"; "docs/decisions.md"; "docs/plans/type-change-keeps-state.md" ]
      "A record that gains a field can keep its live value today, but only if your app registers a boundary and only for simple fields such as int and string. I want that to need nothing from you and to cover more field kinds, because a type change is the biggest restart left."

    item "delta-adds-fields-and-types" "Adding a field without a restart" HotReload Later
      NoLandmarkYet
      [ "docs/how-hot-reload-works.md"; "docs/decisions.md"; "docs/plans/delta-adds-fields-and-types.md" ]
      "A run_app save that adds a field to a type, or adds a lambda that captures something, still restarts the app. The runtime can take those as deltas. My emitter writes rows only for method bodies and added methods so far."

    item "browser-refresh-beyond-pages" "Refresh CSS and static files too" HotReload Later
      NoLandmarkYet
      [ "docs/how-hot-reload-works.md"; "docs/plans/browser-refresh-beyond-pages.md" ]
      "Browser refresh covers your pages when a save lands. CSS, static assets and Razor or Blazor views aren't part of it, and .NET Hot Reload covers them."

    item "hot-reload-on-windows-and-macos" "Hot reload checked on Windows and macOS" Platform Later
      NoLandmarkYet
      [ "docs/how-hot-reload-works.md"; "Readme.md"; "docs/plans/hot-reload-on-windows-and-macos.md" ]
      "My CI is Linux only, so the hot reload detour has no passing evidence on Windows, macOS or arm64, and the docs say so. The one data point so far is bad: on an Apple Silicon Mac the five real-detour tests for generic functions fail, and I don't know why (issue #149). Until someone with a Mac captures the failures, hot reload on macOS is unproven."

    item "start-on-login-windows" "Start on login for Windows" Platform Later
      NoLandmarkYet
      [ "docs/progress.md"; "Readme.md"; "docs/plans/start-on-login-windows.md" ]
      "Linux gets a systemd user unit and macOS a launchd agent, and each starts the daemon for you. Nothing starts it at login on Windows yet."

    item "dotnet-framework-host" "A .NET Framework host for Framework projects" Platform Later
      NoLandmarkYet
      [ "docs/ecosystem-compatibility.md"; "docs/how-isolation-works.md"; "docs/plans/dotnet-framework-host.md" ]
      "SageFs refuses a project whose only target is .NET Framework, because its FSI host is a modern .NET process that cannot load Framework assemblies. The host is already built by the project's own SDK behind a process boundary, so a Framework variant is another build of an existing artifact. It would run on Windows only. I want the session to run on the Framework CLR, since driving fsianycpu.exe gives a bare REPL with no hot reload, live testing or coverage. Requested in issue #135."

    item "start-on-login-macos" "The daemon starts at login on macOS" Platform Later
      (landmark "contrib/launchd/io.github.willehrendreich.sagefs.plist" "io.github.willehrendreich.sagefs")
      [ "Readme.md" ]
      "A launchd user agent starts the daemon at login and restarts its supervisor after a crash, the same job the systemd unit does on Linux. The README install fills in your home directory and your .NET folder, because launchd expands no variables. tw0po1nt wrote it and tested it on macOS 27 on an Apple Silicon Mac. An Intel Mac and the Homebrew dotnet formula are untested."

    item "getter-sandbox-beyond-linux" "The getter sandbox beyond Linux x86-64" Platform Later
      NoLandmarkYet
      [ "docs/decisions.md"; "docs/plans/getter-sandbox-beyond-linux.md" ]
      "Clicking a getter in the live-bindings pane runs it under a syscall filter that stops network use and file writes, on Linux x86-64 only. Elsewhere the getter runs under its deadline alone, and the pane says so."

    item "a-token-for-every-daemon-call" "A token for every daemon call" Platform Later
      NoLandmarkYet
      [ "docs/mcp-tools.md"; "docs/plans/a-token-for-every-daemon-call.md" ]
      "The daemon binds loopback only and checks Origin so a web page can't drive it, but nothing authenticates a local caller. A minted token the daemon requires would, and it's the same handle agents need for their own identity."

    item "admit-heavy-work-by-measured-cost" "Admit heavy work by measured cost" Platform Later
      NoLandmarkYet
      [ "docs/plans/admit-heavy-work-by-measured-cost.md" ]
      "Heavy work already takes a lease, and the daemon sheds under memory pressure with a retry hint. It doesn't yet know what a rebuild of your project usually costs, so it reacts instead of predicting. I'd record peak memory and duration per kind of work and admit against the headroom."

    item "sessions-learn-the-load-order" "Sessions learn the right load order" Repl Later
      NoLandmarkYet
      [ "docs/plans/sessions-learn-the-load-order.md" ]
      "I stopped this one. I measured four projects, 1,454 source files in all, looking for a project whose opens need a second pass, and found none. The one retry that fires, in SageFs.Simulation, comes from two names discovery can never resolve, which a learned order cannot fix. Nothing needs the learned order today, so I did not merge the code. It is kept on the branch `archive/2026-11-07/rescue-load-order` in case a project ever does."

    item "jupyter-kernel-that-works" "A Jupyter kernel that installs and works" Repl Later
      NoLandmarkYet
      [ "docs/coming-from-jupyter.md"; "Readme.md"; "docs/plans/jupyter-kernel-that-works.md" ]
      "`sagefs --jupyter` is marked experimental because nothing in my suite opens a ZMQ socket against it. I'd add a one-command install and a test that drives it end to end, then richer output than plain text."

    item "move-and-dock-dashboard-panes" "Move and dock dashboard panes" Dashboard Later
      NoLandmarkYet
      [ "docs/plans/move-and-dock-dashboard-panes.md" ]
      "Panes you can move, dock, hide and reopen like Visual Studio's tool windows, with a layout per workflow and a reset button for when you lose one. Two pieces of that are in: the live bindings fold away and open again with one click, because the tree can be taller than the dock's ceiling and the drag handle that changes the ceiling is a thin line to find, and the fold is the user's, so a push does not take it back; and the dock's own ceiling remembers itself between visits. Moving a pane between regions is not built."

    item "friction-reports-you-can-read-first" "Friction reports you can read first" Dashboard Later
      NoLandmarkYet
      [ "docs/mcp-tools.md"; "docs/plans/friction-reports-you-can-read-first.md" ]
      "Friction events stay local and bounded now, and the panel stays hidden until reporting has a real endpoint. A report you choose to send would show its exact payload first, then go to that endpoint or start as a prefilled GitHub issue."

    item "markers-on-expecto-cases" "Markers on individual Expecto cases" LiveTesting Later
      NoLandmarkYet
      [ "docs/how-live-testing-works.md"; "docs/plans/markers-on-expecto-cases.md" ]
      "Expecto test cases come from reflection and have no source line, so the gutter marks the `[<Tests>]` binding but not each testCase, and a category comes from the test's name alone. I'd read cases from source and take categories from where you put them."

    item "confirm-builds-without-restarting-the-worker" "Confirm builds without restarting the worker" LiveTesting Later
      NoLandmarkYet
      [ "docs/decisions.md"; "docs/how-live-testing-works.md"; "docs/plans/confirm-builds-without-restarting-the-worker.md" ]
      "The confirming build restarts the worker, so FSI state and unsaved edits in other files don't survive it. A second worker for the build would avoid that at the price of a second FSI host's memory."

    item "live-testing-numbers-on-bigger-projects" "Live testing numbers on bigger projects" LiveTesting Later
      NoLandmarkYet
      [ "docs/how-live-testing-works.md"; "docs/plans/live-testing-numbers-on-bigger-projects.md" ]
      "Keystroke to verdict is p50 712 ms and save to green p50 542 ms, measured on an 11-test sample. I have no figure for a large solution, for slow tests or for the confirming build."

    item "run-tests-reuses-proven-results" "run_tests reuses proven results" LiveTesting Later
      NoLandmarkYet
      [ "docs/decisions.md"; "docs/plans/run-tests-reuses-proven-results.md" ]
      "The content-addressed cache that stops a landing being proven twice works only for the cohort landing gate. I'd feed it into run_tests receipts too, and run tests inside the live session once I know a redefinition changes what your compiled tests call."

    item "watch-your-agents-from-your-phone" "Watch your agents from your phone" Agents Later
      NoLandmarkYet
      [ "docs/plans/watch-your-agents-from-your-phone.md" ]
      "A second listener on your Tailscale address would serve the dashboard read-only to a phone with a short-lived token. A conductor could then approve or veto a landing from a link, but only once the minted handles above exist."

    item "tokens-survive-a-restart" "Member tokens survive a daemon restart" Agents Later
      NoLandmarkYet
      [ "docs/mcp-tools.md"; "docs/plans/tokens-survive-a-restart.md" ]
      "Tokens live in the daemon's memory, so a restart drops every one and the orchestrator mints new ones. Keeping their hashes would let a run survive a restart, but only in a store that is not the cohort ledger, and I'd rather see someone need it first."

    item "ask-before-a-destructive-tool-runs" "Ask you before a tool clears your local data" Agents Later
      (landmark "SageFs/McpServer.fs" "confirmationStep")
      [ "docs/mcp-tools.md" ]
      "The server asks you itself, over MCP's own elicitation request, for exactly one tool: `manage_local_data`, which clears the local stores SageFs keeps on your machine. Those belong to no session and an agent has no reason to clear them mid-task. The call stops after the four gates already there and before its body, names what it is about to take away, and a decline refuses it the way a gate rejection does. The tier sits on the same `ToolName` the authority gate reads rather than in another string list, and it says out loud that a client which never offers elicitation gets no prompt at all, which is the case the stdio bridge re-handshakes into. `stop_session`, `reset_fsi_session`, `hard_reset_fsi_session`, `switch_workflow` and `stop_app` never ask, for any session: an agent calls them as normal steps of its loop, and a prompt on a routine step gets clicked through. They stay marked destructive on `tools/list`, which is information and not a prompt."

    // ---- Exploring: ideas, no promise ----

    item "use-the-fsharp-compilers-deltas" "Use the F# compiler's own deltas" HotReload Exploring
      NoLandmarkYet
      [ "docs/how-hot-reload-works.md"; "docs/decisions.md"; "docs/plans/use-the-fsharp-compilers-deltas.md" ]
      "dotnet/fsharp#19941 would have the compiler write metadata deltas itself. If it ships in an SDK, what I wrote becomes the part that applies and confirms them, and it could reach the REPL host too. I'm tracking it and not waiting for it."

    item "breakpoints-in-patched-code" "Breakpoints in patched code" HotReload Exploring
      NoLandmarkYet
      [ "docs/hot-reload.md"; "docs/decisions.md"; "docs/plans/breakpoints-in-patched-code.md" ]
      "Saves land while a debugger is attached, and I've tested that with netcoredbg on four shapes. Nothing sets a breakpoint, steps, or edits a method the debugger is stopped in, because a detour rewrites the first bytes a breakpoint may sit in."

    item "redefine-a-value-a-handler-renders" "Redefine a value a handler renders" HotReload Exploring
      NoLandmarkYet
      [ "docs/hot-reload.md"; "docs/plans/redefine-a-value-a-handler-renders.md" ]
      "Edit a value that a page handler hands on and the next save is a restart, because I can't tell whether the response kept it. Following the value past the handler would let those patch, and I'd rather prove that than assume it."

    item "hot-reload-without-tiering-off" "Hot reload without turning tiering off" HotReload Exploring
      NoLandmarkYet
      [ "docs/hot-reload.md"; "docs/plans/hot-reload-without-tiering-off.md" ]
      "Hot reload sessions run with tiered compilation off so a watch on a value can't silently lapse, and that costs warmup time. A cheaper way to keep the watch reliable would let me leave JIT tiering alone."

    item "mutants-in-the-live-session" "Mutation testing in the live session" LiveTesting Exploring
      NoLandmarkYet
      [ "docs/plans/mutants-in-the-live-session.md" ]
      "Hot-patching a mutant into the running image might cost milliseconds where Stryker.NET pays a build each. Nobody has timed it, and a day against Stryker on a fixture would tell me."

    item "test-selection-that-learns" "Test selection that learns from history" LiveTesting Exploring
      NoLandmarkYet
      [ "docs/plans/test-selection-that-learns.md" ]
      "Small local models trained on your own run history could rank which tests to run first and flag the flaky ones. It's a parked design with no code, and selection by coverage works without it."

    item "a-token-per-subagent-in-claude-code" "A token per sub-agent in Claude Code" Agents Exploring
      NoLandmarkYet
      [ "docs/plans/a-token-per-subagent-in-claude-code.md" ]
      "A header is per connection, so sub-agents that share one connection share a token. Only a per-call `_meta` tells them apart, and Claude Code gives the model no per-call header, so a harness hook that sets it would be the way in."

    item "claim-an-interface" "Claim an interface, not just a file" Agents Exploring
      NoLandmarkYet
      [ "docs/plans/claim-an-interface.md" ]
      "An agent would claim a .fsi signature file, every member's session would check its code against it, and a change to it would need a recorded decision. Claims cover only files and projects today."

    item "plan-a-cohort-as-code" "Plan a cohort as F# code" Agents Exploring
      NoLandmarkYet
      [ "docs/plans/plan-a-cohort-as-code.md" ]
      "A plan builder would fail at construction if two members claim the same file, and later could propose a claim split from coverage data for you to edit."

    item "fork-and-rewind" "Fork a cohort or a session at a point" Agents Exploring
      NoLandmarkYet
      [ "docs/plans/fork-and-rewind.md" ]
      "Replay a session's cells up to one point into a new worktree, or fork a whole cohort at a ledger sequence number, the way git branches a commit. A cell with side effects replays differently, and the result would say so."

    item "a-cohort-across-machines" "A cohort across machines" Agents Exploring
      NoLandmarkYet
      [ "docs/plans/a-cohort-across-machines.md" ]
      "Once members are handles instead of sockets, one could join from another machine. The coordination core is already pure and message-passing, so it could cross a network."

    item "ask-the-live-codebase-in-fsharp" "Ask the live codebase in F#" Agents Exploring
      NoLandmarkYet
      [ "docs/plans/ask-the-live-codebase-in-fsharp.md" ]
      "A type provider over the repo and the cohort would make a misspelled symbol or test name a compile error at the agent's prompt, and active patterns could filter the cohort in code. This is research."

    item "repl-guard-for-other-agents" "A REPL guard for agents other than Claude" Agents Exploring
      NoLandmarkYet
      [ "docs/agents.md"; "docs/plans/repl-guard-for-other-agents.md" ]
      "The hook that sends an agent back to the REPL instead of running dotnet build is a Claude Code hook. Other agent tools have nothing like it yet."

    item "editors-beyond-vscode-and-neovim" "Rider, Zed, Helix and Emacs" Editors Exploring
      NoLandmarkYet
      [ "docs/agents.md"; "docs/plans/editors-beyond-vscode-and-neovim.md" ]
      "SageFs speaks HTTP and SSE, so any editor could connect, but only VS Code and Neovim have clients. When people ask for more, Rider comes up most."

    item "a-notebook-over-a-live-session" "A notebook that is a window onto a live session" Repl Exploring
      NoLandmarkYet
      [ "docs/coming-from-jupyter.md"; "docs/plans/a-notebook-over-a-live-session.md" ]
      "Cells that update when you edit source, and a value tree as the default output, over the same session your editor and your agent already share."

    item "faster-time-to-ready" "Faster time to Ready" Repl Exploring
      NoLandmarkYet
      [ "docs/plans/faster-time-to-ready.md" ]
      "Warmup is long enough to notice on a bigger solution. Opening namespaces is 60 to 90 percent of it and the replay cache doesn't touch that part, so a precompiled warmup assembly might, once I've measured whether it would help."

    item "command-palette-and-one-timeline" "A command palette and one timeline" Dashboard Exploring
      NoLandmarkYet
      [ "docs/plans/command-palette-and-one-timeline.md" ]
      "Ctrl+K for every dashboard action, and one timeline of evals, hot reloads, test runs and tweaks that you can scrub."

    item "check-the-scope-before-the-write" "Check the scope before the write, not after" Agents Exploring
      NoLandmarkYet
      [ "docs/agents.md"; "docs/plans/check-the-scope-before-the-write.md" ]
      "A claim is policy today: it refuses another member's claim over the same file, but nothing stops a process writing outside the checkout, and the daemon says that about itself. The one component sitting between an agent and its own file tools could ask where a write is going before it happens, which is the difference between a claim and a boundary."

    item "drive-real-agents-as-members" "Drive real coding agents as cohort members" Agents Exploring
      NoLandmarkYet
      [ "docs/agents.md"; "docs/plans/drive-real-agents-as-members.md" ]
      "One process would hold the seat, mint a token per run and drive Claude, Copilot, OpenCode or Pi over the Agent Client Protocol as a member with its own claims and landings, answering that member's permission requests from the scope it holds. It has to join the cohort before it mints anything, because a fresh connection is Anonymous and the very first mint would be refused."

    item "confirmation-gates-in-every-agent-host" "Confirmation gates in every agent host" Agents Exploring
      NoLandmarkYet
      [ "docs/agents.md"; "docs/plans/confirmation-gates-in-every-agent-host.md" ]
      "Where a host can prompt before a tool call, a small plugin could ask there, which covers the clients a server-side prompt can never reach. Since 2026-11-07 the server asks about one tool only, `manage_local_data`, because stopping and resetting sessions are normal steps of the loop and no longer prompt anywhere. That shrank this item to one tool, so the plan needs re-scoping before anyone builds it. None of the hosts has been run yet."

    item "bake-the-build-env-into-the-tooling" "Builds SageFs starts no longer die on a leftover MSBuild node" Platform Next
      (landmark "SageFs.Core/ProcessEnvironment.fs" "buildEnvironment")
      [ "docs/agents.md" ]
      "Session rebuilds and install-local's pack died with MSB4223 three times in one session under load, and they only proceeded once an agent exported MSBUILDDISABLENODEREUSE=1 by hand. The daemon's session rebuilds and its FSI host build now take the variable from one named list, and ship.fsx and install-local.fsx set it for every build they start. The skill's troubleshooting page names the error and the one-line fix for an older daemon."

    item "give-the-gate-a-clean-checkout" "An interrupted gate cannot fail the run after it" Platform Next
      (landmark "SageFs.Core/GateCheckout.fs" "decide")
      [ "docs/agents.md" ]
      "The gate reuses one checkout directory, and a killed run's versioned outputs crashed the next run in zero seconds with a FileNotFoundException naming a SageFs.Core version the tree no longer was. The gate now writes what it is doing beside the checkout before the pipeline starts and what it left once the pipeline ends on its own. A run that finds the first and no second clears the checkout's build output before it builds, and a checkout with no record is cleared too. A run that finished, even a red one, keeps its warm build."

    item "make-a-failed-gate-retry-one-command" "A failed gate's retry is one command" Platform Next
      (landmark "scripts/ReleaseRules.fs" "planBump")
      [ "docs/agents.md" ]
      "A failed gate used to leave its bump commit behind, and a rerun added a second one that the pre-push hook refused only after a whole new gate. Now a plain `ship.fsx` looks at the release commits in origin/master..HEAD first. With none it bumps. With one that is above the published version it keeps that bump and gates HEAD. With two or more it stops before any gate, names the commits and prints the `git rebase --onto` line that drops each extra one. The hook counts release commits with the same reader, and the marker text lives in one place. The retry path is written up in docs/agents.md."

    item "write-down-what-a-new-tool-declares" "What a new tool must declare is written down" Docs Next
      (landmark "docs/mcp-tools.md" "## Adding a tool")
      [ "docs/progressive-map-research-2026-10-24.md" ]
      "Adding list_members took six declarations, and it missed four hand-kept test lists as well, which left the default suite red on master. The tools page now lists all seven places in the order that keeps the build green, names the test that goes red for each, and says what else a tool commit carries: the Plugin trailer, and a name that is not a retired one. The slice plan in the progressive map research points there."

    item "fix-the-readme-capability-mcp-marks" "The Readme capability table says which rows are HTTP routes" Docs Next
      (landmark "Readme.md" "Code completion | ✅ | ✅ | no | HTTP")
      [ "docs/mcp-tools.md" ]
      "The ticks under MCP for completion and the type explorer were gone in March, but the cells that replaced them were a bare colon, which reads as a typo. The rows that are HTTP routes and no tool now say HTTP, every other empty cell says no, and the note under the table says what each word means."

    item "list-members-counts-by-kind" "Ask what is inside a name, a page at a time" Agents Next
      (landmark "SageFs/McpMemberPage.fs" "pageMembers")
      [ "docs/progressive-map-research-2026-10-24.md"; "docs/mcp-tools.md" ]
      "`list_members` takes a qualified name and answers with its members a page at a time. The reply gives a total that is either exact or marked as a lower bound, a count for each real kind (classes, modules, methods and so on) with the kinds you can step into first, and a footer naming the offset of the next page. A name that lists nothing says why: it is a leaf, an empty container, or it does not resolve under its parent. The kind comes from the compiler through the worker. Before, the daemon guessed it from the shape of the name, and every reply stopped at 50 members whatever the real size."

    item "walk-the-rest-of-the-symbol-ladder" "Walk the rest of the symbol ladder" Agents Next
      (landmark "SageFs/McpNamePath.fs" "resolve")
      [ "docs/progressive-map-research-2026-10-24.md"; "docs/plans/walk-the-rest-of-the-symbol-ladder.md" ]
      "The references rung is built: `list_references` takes a qualified name and lists every definition and use the compiler resolves to it across the session's projects, a page at a time. Its first line is the scope (project-wide, partial with the projects it did not reach, or unavailable with the reason), so a count never travels without how far the compiler looked, and one budget covers all the projects together. What remains of the progressive map plan: the name-path fallback for a stale reference, and per-tool result-size ratchet rows so a ladder reply can never become the map again." ]
