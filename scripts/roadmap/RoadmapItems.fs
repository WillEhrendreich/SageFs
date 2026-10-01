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
      "After a save is patched into your running app, the REPL, the live bindings and the live tests can still be looking at the old code, and nothing tells you. I'm making that a named state every surface shows, and then I'll see whether refreshing the REPL host for you is cheap enough to do."

    item "agent-landings-reach-the-running-app" "An agent's landed work reaches your running app" Agents Now
      NoLandmarkYet
      [ "docs/how-hot-reload-works.md"; "docs/mcp-tools.md" ]
      "When agents land work in the shared trunk, the app running there should pick it up live with its state kept, and the reload row should say how it got there. Today a landing is verified and merged but the trunk app doesn't hear about it, so a landing is going to count as a save."

    item "slow-machines-get-fitting-timeouts" "Slow machines get timeouts that fit them" Platform Now
      (landmark "SageFs.Core/MachineTier.fs" "MachineTier")
      [ "docs/configuration.md" ]
      "On an old quad core the isolated FSI host takes about 38 seconds to start, and the fixed 30 second budget gave up and then retried with the same 30. I'm measuring how different machines really behave, scaling the waits to what each one can do, and making retries wait longer instead of repeating themselves."

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

    item "edits-during-worker-swap-judged" "Edits during a worker swap are judged" LiveTesting Now
      (landmark "SageFs/LiveCheckRelay.fs" "LiveCheckRelay")
      [ "docs/how-live-testing-works.md"; "docs/decisions.md" ]
      "An edit typed while the confirming build replaces the worker used to be lost. A type-check or an eval now waits for a Ready worker and believes an answer only from the worker it asked."

    // ---- Next: weeks to a couple of months ----

    item "repl-eval-reaches-the-running-app" "A REPL eval changes the running app" HotReload Next
      NoLandmarkYet
      [ "docs/how-hot-reload-works.md" ]
      "An eval reaches an app you started from FSI, but not one started with run_app, where only saved files get through. There are two ways in. One writes the evaluated declaration to source and lets save, build and delta carry it, which costs the build. The other grafts FSI's own IL into a delta, and in a spike that worked and was served in milliseconds, but it isn't wired through a real worker and it has sharp edges. I'm building the write-to-source route first because it also keeps your change, then the faster one behind a flag."

    item "live-tweak-front-door" "Nudge a value in the running app" HotReload Next
      NoLandmarkYet
      [ "docs/decisions.md" ]
      "The engine for dragging a value in a running app and writing the result back to the source file is built and tested, with addressing that survives a rename and an undoable history. Nothing in the dashboard or the editors calls it yet, so today you can't use it."

    item "neovim-debug-a-failing-test" "Debug a failing test from Neovim" Editors Next
      NoLandmarkYet
      [ "docs/LIVE_TESTING_GUIDE.md" ]
      "The daemon side is done: one route holds the test and hands back a process id to attach to, and another releases it. The Neovim plugin has to wire nvim-dap to those routes, and that work lives in the sagefs.nvim repo."

    item "neovim-catches-up" "The Neovim plugin catches up with the daemon" Editors Next
      NoLandmarkYet
      [ "docs/sse-events.md"; "docs/mcp-tools.md" ]
      "The daemon's wire moved under the plugin. Hot reload now reports pending and never-entered states, and the live-values pane has a Safe mode with a click to run one getter. The hand-offs are written and the work is in the plugin's own repo."

    item "debug-failing-tests-in-vscode" "Debug failing tests properly in VS Code" LiveTesting Next
      NoLandmarkYet
      [ "docs/LIVE_TESTING_GUIDE.md"; "docs/how-live-testing-works.md" ]
      "Debugging a failing test from the VS Code lens works for one test at a time. Still open is a full attach end to end in our own tests, and debug runs updating the gutter. Several tests at once and breakpoints in evaluated code are further out."

    item "live-requests-not-dropped" "No live request dropped during a swap" LiveTesting Next
      NoLandmarkYet
      [ "docs/how-live-testing-works.md" ]
      "A check that waits out the 30 second swap bound is dropped instead of retried, and running affected tests or discovery still drops a request that finds no worker. I want every one of those to wait for the replacement the way the type-check now does."

    item "each-agent-its-own-member" "Each agent counts as its own member" Agents Next
      NoLandmarkYet
      [ "docs/agents.md"; "docs/mcp-tools.md" ]
      "Sub-agents of one Claude Code session share one MCP connection, so SageFs sees them as a single member and the build and test leases collide. Identity moves off the connection and onto a handle SageFs mints, so each agent gets its own seat and a dropped connection doesn't lose it."

    item "cohort-veto-and-delegation" "Veto and delegate in a cohort" Agents Next
      NoLandmarkYet
      [ "docs/mcp-tools.md" ]
      "The conductor can't hand off its role and nobody can veto or withdraw a landing, because four commands exist in the core with no tool or button that issues them. I'll wire them or delete them, and wiring starts with deciding who is allowed to veto."

    item "more-settings-editable" "More settings editable from the dashboard" Dashboard Next
      NoLandmarkYet
      [ "docs/configuration.md" ]
      "The Settings panel edits a handful of settings today (test timeouts, the MCP port, bind host, default working directory, reflection mode and tiering), each resolved across layers with its source shown. Most environment variables and CLI flags still have to be routed through it before you can edit them at runtime."

    item "dashboard-follows-context" "Dashboard panels that follow your context" Dashboard Next
      NoLandmarkYet
      []
      "Live bindings become a real pane in REPL mode, and cohort shows only while one is running. A pane hidden for being irrelevant says why and can be pinned open."

    item "app-output-pane" "App output in its own pane" Dashboard Next
      NoLandmarkYet
      []
      "stdout and stderr from a running app get their own pane with follow, pause and search, so a chatty app stops burying your evals. The daemon already receives the app's output lines, and the pane is what's missing."

    item "new-session-dialog" "A guided new-session dialog" Dashboard Next
      NoLandmarkYet
      []
      "A plus button on the Sessions list opens a dialog that finds projects, says in a line what each workflow means, and warns before you make a second session in the same directory. Today New Session is a collapsible panel."

    item "publish-the-loop-timings" "Published numbers for the REPL loop" Docs Next
      NoLandmarkYet
      []
      "I timed an edit plus a check in a warm session against a build plus a filtered test on three projects, from a few hundred lines to a very large one. The session answered in a fraction of a second where the build took seconds, and you pay the warmup once. None of it is in the docs yet, and I want the table there with the machine and the load stated."

    item "native-nuget-in-sessions" "Check native NuGet packages in sessions" Isolation Next
      NoLandmarkYet
      [ "docs/how-isolation-works.md" ]
      "Someone reported a `#r` NuGet package with a native library failing in plain FSI. I haven't checked whether the isolated host fixes it, so I'll run that repro and write down what I find, good or bad."

    item "cross-file-signature-callers" "Callers in other files follow a signature change" HotReload Next
      NoLandmarkYet
      [ "docs/hot-reload.md"; "docs/decisions.md" ]
      "When a save re-signs a function, a caller in another file keeps calling the old method until you save that file too. The build wouldn't pass until you did, so the window is short, but the old behavior runs in it. A cross-file check of who calls what would close it."

    // ---- Later: months to a year ----

    item "type-change-keeps-state" "Type changes keep your running state" HotReload Later
      NoLandmarkYet
      [ "docs/granular-restart-scope.md"; "docs/decisions.md" ]
      "A record that gains a field can keep its live value today, but only if your app registers a boundary and only for simple fields such as int and string. I want that to need nothing from you and to cover more field kinds, because a type change is the biggest restart left."

    item "delta-adds-fields-and-types" "Adding a field without a restart" HotReload Later
      NoLandmarkYet
      [ "docs/how-hot-reload-works.md"; "docs/decisions.md" ]
      "A run_app save that adds a field to a type, or adds a lambda that captures something, still restarts the app. The runtime can take those as deltas. My emitter writes rows only for method bodies and added methods so far."

    item "browser-refresh-beyond-pages" "Refresh CSS and static files too" HotReload Later
      NoLandmarkYet
      [ "docs/how-hot-reload-works.md" ]
      "Browser refresh covers your pages when a save lands. CSS, static assets and Razor or Blazor views aren't part of it, and .NET Hot Reload covers them."

    item "hot-reload-on-windows-and-macos" "Hot reload checked on Windows and macOS" Platform Later
      NoLandmarkYet
      [ "docs/how-hot-reload-works.md"; "Readme.md" ]
      "The README says SageFs runs on Windows and macOS, but my CI is Linux only, so the detour has no test evidence on either and none on arm64. I need machines to close that."

    item "start-on-login-windows-macos" "Start on login for Windows and macOS" Platform Later
      NoLandmarkYet
      [ "docs/progress.md" ]
      "Linux gets a systemd user unit that starts the daemon for you. Nothing starts it at login on Windows or macOS yet."

    item "getter-sandbox-beyond-linux" "The getter sandbox beyond Linux x86-64" Platform Later
      NoLandmarkYet
      [ "docs/decisions.md" ]
      "Clicking a getter in the live-bindings pane runs it under a syscall filter that stops network use and file writes, on Linux x86-64 only. Elsewhere the getter runs under its deadline alone, and the pane says so."

    item "a-token-for-every-daemon-call" "A token for every daemon call" Platform Later
      NoLandmarkYet
      [ "docs/mcp-tools.md" ]
      "The daemon binds loopback only and checks Origin so a web page can't drive it, but nothing authenticates a local caller. A minted token the daemon requires would, and it's the same handle agents need for their own identity."

    item "admit-heavy-work-by-measured-cost" "Admit heavy work by measured cost" Platform Later
      NoLandmarkYet
      []
      "Heavy work already takes a lease, and the daemon sheds under memory pressure with a retry hint. It doesn't yet know what a rebuild of your project usually costs, so it reacts instead of predicting. I'd record peak memory and duration per kind of work and admit against the headroom."

    item "sessions-learn-the-load-order" "Sessions learn the right load order" Repl Later
      NoLandmarkYet
      []
      "A session whose projects load in the wrong order retries and usually recovers. I'd order loads by MSBuild's project graph first, and remember the edges it missed so the second start loads clean on the first pass."

    item "jupyter-kernel-that-works" "A Jupyter kernel that installs and works" Repl Later
      NoLandmarkYet
      [ "docs/coming-from-jupyter.md"; "Readme.md" ]
      "`sagefs --jupyter` is marked experimental because nothing in my suite opens a ZMQ socket against it. I'd add a one-command install and a test that drives it end to end, then richer output than plain text."

    item "move-and-dock-dashboard-panes" "Move and dock dashboard panes" Dashboard Later
      NoLandmarkYet
      []
      "Panes you can move, dock, hide and reopen like Visual Studio's tool windows, with a layout per workflow and a reset button for when you lose one."

    item "friction-reports-you-can-read-first" "Friction reports you can read first" Dashboard Later
      NoLandmarkYet
      [ "docs/mcp-tools.md" ]
      "Friction events stay local and bounded now, and the panel stays hidden until reporting has a real endpoint. A report you choose to send would show its exact payload first, then go to that endpoint or start as a prefilled GitHub issue."

    item "markers-on-expecto-cases" "Markers on individual Expecto cases" LiveTesting Later
      NoLandmarkYet
      [ "docs/how-live-testing-works.md" ]
      "Expecto test cases come from reflection and have no source line, so the gutter marks the `[<Tests>]` binding but not each testCase, and a category comes from the test's name alone. I'd read cases from source and take categories from where you put them."

    item "confirm-builds-without-restarting-the-worker" "Confirm builds without restarting the worker" LiveTesting Later
      NoLandmarkYet
      [ "docs/decisions.md"; "docs/how-live-testing-works.md" ]
      "The confirming build restarts the worker, so FSI state and unsaved edits in other files don't survive it. A second worker for the build would avoid that at the price of a second FSI host's memory."

    item "live-testing-numbers-on-bigger-projects" "Live testing numbers on bigger projects" LiveTesting Later
      NoLandmarkYet
      [ "docs/how-live-testing-works.md" ]
      "Keystroke to verdict is p50 712 ms and save to green p50 542 ms, measured on an 11-test sample. I have no figure for a large solution, for slow tests or for the confirming build."

    item "run-tests-reuses-proven-results" "run_tests reuses proven results" LiveTesting Later
      NoLandmarkYet
      [ "docs/decisions.md" ]
      "The content-addressed cache that stops a landing being proven twice works only for the cohort landing gate. I'd feed it into run_tests receipts too, and run tests inside the live session once I know a redefinition changes what your compiled tests call."

    item "watch-your-agents-from-your-phone" "Watch your agents from your phone" Agents Later
      NoLandmarkYet
      []
      "A second listener on your Tailscale address would serve the dashboard read-only to a phone with a short-lived token. A conductor could then approve or veto a landing from a link, but only once the minted handles above exist."

    // ---- Exploring: ideas, no promise ----

    item "use-the-fsharp-compilers-deltas" "Use the F# compiler's own deltas" HotReload Exploring
      NoLandmarkYet
      [ "docs/how-hot-reload-works.md"; "docs/decisions.md" ]
      "dotnet/fsharp#19941 would have the compiler write metadata deltas itself. If it ships in an SDK, what I wrote becomes the part that applies and confirms them, and it could reach the REPL host too. I'm tracking it and not waiting for it."

    item "breakpoints-in-patched-code" "Breakpoints in patched code" HotReload Exploring
      NoLandmarkYet
      [ "docs/hot-reload.md"; "docs/decisions.md" ]
      "Saves land while a debugger is attached, and I've tested that with netcoredbg on four shapes. Nothing sets a breakpoint, steps, or edits a method the debugger is stopped in, because a detour rewrites the first bytes a breakpoint may sit in."

    item "redefine-a-value-a-handler-renders" "Redefine a value a handler renders" HotReload Exploring
      NoLandmarkYet
      [ "docs/hot-reload.md" ]
      "Edit a value that a page handler hands on and the next save is a restart, because I can't tell whether the response kept it. Following the value past the handler would let those patch, and I'd rather prove that than assume it."

    item "hot-reload-without-tiering-off" "Hot reload without turning tiering off" HotReload Exploring
      NoLandmarkYet
      [ "docs/hot-reload.md" ]
      "Hot reload sessions run with tiered compilation off so a watch on a value can't silently lapse, and that costs warmup time. A cheaper way to keep the watch reliable would let me leave JIT tiering alone."

    item "mutants-in-the-live-session" "Mutation testing in the live session" LiveTesting Exploring
      NoLandmarkYet
      []
      "Hot-patching a mutant into the running image might cost milliseconds where Stryker.NET pays a build each. Nobody has timed it, and a day against Stryker on a fixture would tell me."

    item "test-selection-that-learns" "Test selection that learns from history" LiveTesting Exploring
      NoLandmarkYet
      []
      "Small local models trained on your own run history could rank which tests to run first and flag the flaky ones. It's a parked design with no code, and selection by coverage works without it."

    item "claim-an-interface" "Claim an interface, not just a file" Agents Exploring
      NoLandmarkYet
      []
      "An agent would claim a .fsi signature file, every member's session would check its code against it, and a change to it would need a recorded decision. Claims cover only files and projects today."

    item "plan-a-cohort-as-code" "Plan a cohort as F# code" Agents Exploring
      NoLandmarkYet
      []
      "A plan builder would fail at construction if two members claim the same file, and later could propose a claim split from coverage data for you to edit."

    item "fork-and-rewind" "Fork a cohort or a session at a point" Agents Exploring
      NoLandmarkYet
      []
      "Replay a session's cells up to one point into a new worktree, or fork a whole cohort at a ledger sequence number, the way git branches a commit. A cell with side effects replays differently, and the result would say so."

    item "a-cohort-across-machines" "A cohort across machines" Agents Exploring
      NoLandmarkYet
      []
      "Once members are handles instead of sockets, one could join from another machine. The coordination core is already pure and message-passing, so it could cross a network."

    item "ask-the-live-codebase-in-fsharp" "Ask the live codebase in F#" Agents Exploring
      NoLandmarkYet
      []
      "A type provider over the repo and the cohort would make a misspelled symbol or test name a compile error at the agent's prompt, and active patterns could filter the cohort in code. This is research."

    item "repl-guard-for-other-agents" "A REPL guard for agents other than Claude" Agents Exploring
      NoLandmarkYet
      [ "docs/agents.md" ]
      "The hook that sends an agent back to the REPL instead of running dotnet build is a Claude Code hook. Other agent tools have nothing like it yet."

    item "editors-beyond-vscode-and-neovim" "Rider, Zed, Helix and Emacs" Editors Exploring
      NoLandmarkYet
      [ "docs/agents.md" ]
      "SageFs speaks HTTP and SSE, so any editor could connect, but only VS Code and Neovim have clients. When people ask for more, Rider comes up most."

    item "a-notebook-over-a-live-session" "A notebook that is a window onto a live session" Repl Exploring
      NoLandmarkYet
      [ "docs/coming-from-jupyter.md" ]
      "Cells that update when you edit source, and a value tree as the default output, over the same session your editor and your agent already share."

    item "faster-time-to-ready" "Faster time to Ready" Repl Exploring
      NoLandmarkYet
      []
      "Warmup is long enough to notice on a bigger solution. Opening namespaces is 60 to 90 percent of it and the replay cache doesn't touch that part, so a precompiled warmup assembly might, once I've measured whether it would help."

    item "command-palette-and-one-timeline" "A command palette and one timeline" Dashboard Exploring
      NoLandmarkYet
      []
      "Ctrl+K for every dashboard action, and one timeline of evals, hot reloads, test runs and tweaks that you can scrub." ]
