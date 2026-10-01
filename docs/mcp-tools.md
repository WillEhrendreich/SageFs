# MCP Tools Reference

SageFs runs a Model Context Protocol server on port 37749. Any MCP client
(GitHub Copilot, Claude Code, Claude Desktop, Cursor, Windsurf, OpenCode)
can connect and drive an F# session: run code, type-check it, list and verify
tests, and read live status. This is the surface I actually use every day to
work on SageFs itself, so if it's clunky, I feel it first.

SageFs gates tools when you call them. `tools/list` always advertises the
full catalog below, and SageFs doesn't filter what an MCP client sees there.
Calling one is different: a call that doesn't apply to the current session
state gets rejected with a structured error (`enforceToolCallGate` in
`SageFs/Mcp.fs`), instead of a raw failure. Call `get_daemon_status`
for daemon health, then `get_session_status` to see which tools currently
apply. In a warming-up session, for example, it
reports `send_fsharp_code` as not yet available, even though the tool is
still listed. I went back and forth on filtering the list itself. For now
the call-time gate is what's actually wired up, so that's what this doc
promises.

The full advertised set is 63 tools, grouped below. This is separate
from the daemon's HTTP API (`/api/...`), which the editors and dashboard use
for completions, coverage bitmaps, run policies, and event history. Those
HTTP endpoints are not MCP tools.

## Connect

**stdio (recommended)**: your client spawns `sagefs mcp`, so there's no
ordering to get wrong — no port to be missing, no cached connection
failure, no race between your client starting and the daemon starting.
`sagefs mcp` checks whether the daemon is up, starts it if it isn't, waits
for it, then bridges your client's stdin/stdout to it.

```
claude mcp add sagefs -- sagefs mcp
```

### How long the daemon lives

If `sagefs mcp` has to start the daemon, it starts it with
`--owner-pid <the pid of that bridge>` (`startDaemonProcess` in
`SageFs/McpStdioBridge.fs`). The daemon polls that pid every 2 seconds
(`OwnerMonitor`, `SageFs.Core/OwnerMonitor.fs`) and shuts down when the owner
is gone. So a daemon that an agent started lives exactly as long as that agent
session: when the client exits, the daemon goes within about ten seconds, and
the dashboard and every session go with it. Two agents sharing that daemon
share its fate, too. The one that started it leaving takes it down for the
other.

Good for a throwaway session, bad if you want the REPL, the dashboard and the
warm sessions to still be there tomorrow. To get a daemon that outlives the
agent, start it yourself before the agent does: run `sagefs` in a terminal, or
run it as a service (see "Run it as a service (Linux)" in the
[README](../Readme.md)). `sagefs mcp` checks for a running daemon first, so
it just bridges to yours and never becomes its owner.

For clients that take raw JSON config:
```json
{ "mcpServers": { "sagefs": { "command": "sagefs", "args": [ "mcp" ] } } }
```

**Streamable HTTP** (for a client that only speaks HTTP, or that's already
started before you get to configure it — the daemon has to be running
first):
```json
{ "mcpServers": { "sagefs": { "type": "streamable-http", "url": "http://localhost:37749/" } } }
```

**SSE** (for clients that don't support Streamable HTTP yet):
```json
{ "mcpServers": { "sagefs": { "type": "sse", "url": "http://localhost:37749/sse" } } }
```

### No SageFs tools in your client

If your client shows no SageFs tools, or it shows them but every call
fails, you almost certainly configured HTTP and started your client before
the daemon. An HTTP-based MCP client that fails to connect on its first try
usually caches that failure and never retries, even once the daemon comes
up seconds later — restarting the client is the only fix, and nothing
tells you that's what you need to do. Switch to stdio (`claude mcp add
sagefs -- sagefs mcp`, above) and this stops happening: your client spawns
the bridge itself, and the bridge makes sure the daemon is there before
your client ever sees an empty tool list. If you want to stay on HTTP,
check `sagefs status` — if it says no daemon is running, start one with
`sagefs` and restart your client.

## Execution and status

| Tool | What it does |
|:---|:---|
| `send_fsharp_code` | Evaluate F# code in the session. Each `;;` is a transaction boundary: a failure discards that one statement and keeps everything before it. |
| `check_fsharp_code` | Type-check a snippet without running it, in the current FSI context. Earlier `send_fsharp_code` definitions are in scope, but namespaces still need an explicit `open`. A "not defined" error here almost always just means you forgot the `open`, nothing more sinister. |
| `cancel_eval` | Cancel a running evaluation. |
| `get_daemon_status` | Daemon version, health, memory, process telemetry, and session counts, including with no active session. |
| `get_session_status` | The selected session's lifecycle, loaded projects, progress, and tools available in the current state. Pass `wait_seconds` (default 0, capped at 60) to wait for a warming session to become Ready instead of polling; the `wait` field in the reply says how it ended (`NotNeeded`, `BecameReady`, `Faulted`, `TimedOut`). `lastReload` says what the last save did: a patch reads `PatchPending` until the new code has been seen running, then `Patched`, or `NeverEntered` if the bound passed first. See [Hot Reload](hot-reload.md#what-patched-means). `replFreshness` says whether the REPL and live tests run the build the app runs: `InSync`, or `BehindApp` with the number of saves and what was patched, after an app started with `run_app` was patched in place. `send_fsharp_code`, `check_fsharp_code`, `run_tests` and `list_sessions` say it too, after their results. `sourceState` is a different fact: whether the build the session runs is behind the files on disk. It is `InSync`, `Stale` (with the files that were written after the build, and when), `Rebuilding` (a rebuild is in progress and the old worker is still serving), or `Unknown` (with why: no project is loaded, the worker did not say when it loaded its build, a file could not be read). It is in every shape of the reply, including the warming and faulted ones. `replFreshness` is the REPL behind the app, and `sourceState` is the disk ahead of the build, so they can be true together, apart, or not at all. |
| `get_recent_fsi_events` | Recent evals, errors, and loads with timestamps. |

## Sessions and lifecycle

| Tool | What it does |
|:---|:---|
| `create_project_session` | Create an isolated session for one explicit `.fsproj`. Missing generated build state is rebuilt before the session is registered. |
| `create_solution_session` | Create an isolated session for one explicit `.sln` or `.slnx`. |
| `create_bare_session` | Create an isolated project-free REPL. It never auto-discovers. |
| `list_sessions` | List active sessions. Every entry ends with a `Source:` line: in sync with the build, no project loaded, or the same warning `get_session_status` carries as `sourceState` when files changed after the build, a rebuild is running, or it could not be told. |
| `switch_session` | Change which session your calls route to. |
| `stop_session` | Stop a session by id. MCP-bound sessions can only be stopped by the connection that created them. |
| `reset_fsi_session` | Soft reset: clears definitions, keeps loaded DLLs. |
| `hard_reset_fsi_session` | Full reset: rebuilds the project, reloads, starts fresh. Needed after `.fsproj` or package changes. |
| `get_available_projects` | Discover `.fsproj` / `.sln` / `.slnx` files under a directory. |
| `list_runnable_projects` | List the session's projects and which ones `run_app` can run (`OutputType=Exe`). |
| `switch_workflow` | Switch the session's workflow: `repl` (Interactive), `livetesting` (Live Testing), or `live` (Hot Reload, aliases `hotreload`/`weblive`/`web`, kept for backward compatibility). Creates a new session in the target workflow and stops the old one; VS Code and the dashboard's own `POST /api/sessions/{sid}/workflow` route restart the same session id in place instead. |

## Hot reload and running apps

| Tool | What it does |
|:---|:---|
| `enable_hot_reload` | Turn on file watching and hot reload for the session. |
| `disable_hot_reload` | Turn it off. |
| `reset_hot_reload_state` | List the live state a save kept when you edited its initializer (binding, kept value, waiting initializer), or pass a binding to run only that initializer in the running app. |
| `set_reflection_read_mode` | Show how hot reload watches values read through reflection (the mode, whether the watch is on, and any question a hot reflective loop raised), or pass `exact-every-read`, `mark-on-reflect` or `probe-callers` to switch the running app. No restart. See [Hot Reload](hot-reload.md#reflection-reads). |
| `run_app` | Run the session's executable project the way `dotnet run` would, with hot reload. Applies `launchSettings.json` (first "Project" profile) and picks a free loopback port when the project sets no URL. Restarts an Interactive session into the Hot Reload workflow first, so REPL bindings are lost. Saving source then hot-patches the running app, including a route table built once at startup. |
| `stop_app` | Stop the app started by `run_app`. Its web host stops and frees its port; the session keeps running. |

## Testing and verification

| Tool | What it does |
|:---|:---|
| `list_tests` | List discovered tests, grouped by file with source locations. A compiled-project session's tests are ReflectionOnly and carry no file/line, so those come back under a separate `WithoutSourceLocation` field instead of being dropped. Optional pattern or file filter. |
| `run_tests` | Run the session's discovered tests through the live-testing engine and get a receipt. Filters: `pattern`, `file_path`, `category`. Waits up to `wait_seconds` (default 30, at most 60); a run still going hands back a `receipt_id` to call again with. |
| `targeted_verify` | Plan a trustworthy verification pass for one changed behavior. Refuses to claim green when session trust is ambiguous or loaded code is stale. It doesn't run tests itself. It returns the next trustworthy move. |
| `explain_test_failure` | Enriched failure context for a test that recently went from passing to failing. |

`run_tests` is a door into the live-testing engine, not a second test runner. It
sends the same request the dashboard and the editors send, and what comes back is
the engine's own record of that run. So `list_tests`, the dashboard and the
receipt can't disagree: they're reading one place.

The receipt never calls a test passed unless this run passed it. A pass left over
from an earlier run doesn't count, and neither does a skipped test, a test the run
was cut off before, or one that never reported. A run where every requested test
passed is `AllPassed`, a run where any failed is `SomeFailed`, and everything else
is `Incomplete`, which is not green. Each line says what happened to that test in
this run and why. If the session is still warming up, has nothing discovered, or
your filters match nothing, `run_tests` says so and runs nothing.

The tests run in the build the session loaded, and the files on disk can be newer
than that build. So a finished receipt also carries `source`, the same
`sourceState` that `get_session_status` has, read when the run was dispatched and
again when it finished (the worse of the two counts, so an edit during the run
shows up), and fixed once the run settles. `AllPassed` means every test passed and
`source` is `InSync`. A pass over anything else gets its own verdict:
`PassedOnStaleSource` (files changed on disk after the build; `source` names them),
`PassedWhileRebuilding`, or `PassedOnUnknownSource` (a file could not be read, the
worker did not say when it loaded, or no project is loaded). None of those is
green. The text says it too, as `passed, but on STALE source`, and ends with a
`Source:` line. The run still happens and the receipt says what it ran against.
`hard_reset_fsi_session` with `rebuild=true` brings the build level with the files. The mtime check can read a file as changed that only
got touched (a branch switch does that), and it cannot see an edit made inside
the window of a build that was already running.

Don't run `dotnet test` from an agent to do the same job. That's a second engine,
and its answers are the ones that drift. `dotnet test` and `dotnet run` stay as
the final gate before you push.

## Analysis and diagnostics

| Tool | What it does |
|:---|:---|
| `diagnose` | Full diagnostic report: test failures, cell staleness, ripple plan, suggestions. |
| `coverage_intel` | Coverage-quality analysis: blind spots, correlated failures. |
| `impact_forecast` | Forecast the performance impact and downstream blast radius of cells. |
| `suggest_next_action` | Prioritized "what next" queue combining coverage, impact, and staleness. |
| `suggest_next_cell` | Type-directed suggestions for what to evaluate next, from the bindings in scope. |
| `suggest_repair` | Given a failing test, trace causal changes and suggest the symbol to fix. |
| `plan_ripple` | Plan cascade re-evaluation for changed cells using the live dependency graph. |
| `preview_what_if` | Preview what would change if a binding had a different value, without executing. |
| `decompose_pipeline` | Break an F# pipeline into stages, each classified pure / effectful / unknown. |
| `get_cell_dependencies` | The cell dependency graph with staleness annotations. |
| `discover_features` | Context-aware feature discovery, ranked by relevance to the session state. |

## Export and history

| Tool | What it does |
|:---|:---|
| `export_notebook` | Export the session as a notebook-style `.fsx` with cell metadata. |
| `export_session_transcript` | Export the session as a clean, topologically sorted `.fsx` transcript. |
| `get_session_filmstrip` | Visual history of evaluations: each a frame with code, bindings, and duration. |
| `get_eval_timeline` | Eval-duration sparkline and P50/P95/P99 statistics. |
| `get_eval_diff` | Before/after diff of recent evaluation outputs. |
| `get_message_journal` | Audit log of eval events, filterable by severity and source. |
| `manage_scratch_pad` | View, export, or promote ephemeral snippets from session history. |

## Friction telemetry (local only)

These read and write a local log. They don't phone home. I have zero
interest in your code or your keystrokes, and building a telemetry pipeline
sounds like a chore anyway.

| Tool | What it does |
|:---|:---|
| `get_friction_summary` | Compact summary of recorded MCP friction. |
| `get_friction_report` | Structured JSON report of MCP pain points. |
| `report_friction` | Record structured feedback about a confusing tool call. |
| `manage_local_data` | See what SageFs stores under its data dir (rows, bytes, oldest row, retention rules), or clear it. |

## Leases for expensive work

A full build, a test suite and an app run each cost real memory. One night five
agents did one each against a single daemon and nothing coordinated them, so
the daemon's memory was already high by the time it could tell. An agent that
starts one of these itself asks for a lease first and gets back Granted, Wait
with a retry time, or Refused. SageFs's own session creation and
`hard_reset_fsi_session rebuild=true` take their own leases, so an agent doesn't
lease those. The full rules, for an agent, are in
[`skills/sagefs/leases.md`](../skills/sagefs/leases.md).

| Tool | What it does |
|:---|:---|
| `acquire_full_build_lease` | A lease for a full `dotnet build` you start yourself. |
| `acquire_test_suite_lease` | A lease for a test-suite process you start yourself (`dotnet run --project <tests>` or `dotnet test`). SageFs has no tool that runs your suite for you. It runs its own tests through the live-testing engine, and you read them with `list_tests` or `run_tests`. |
| `acquire_run_app_lease` | A lease for a run-app process you start yourself. SageFs's own `run_app` is a different thing and needs no lease from you. |
| `release_work_lease` | Release a lease by the id a granted acquisition returned. A lease held by another connection is never released. |

A lease that is never released is reclaimed when it expires, so an agent that
crashes can't hold a slot forever.

## Workspace hygiene

Agents and orchestrators leave things behind: worktrees, merged branches, the
gate's checkouts, built FSI hosts, test temp dirs, orphaned processes. These
two tools show it and tidy the part that is safe. They are about the
repository and the disk, not a session, so they work before one exists.
`sagefs hygiene` prints the same plan from a shell, and `sagefs hygiene --tidy`
runs the safe part of it. Both take `--repo PATH` for a repository you are not
standing in.

| Tool | What it does |
|:---|:---|
| `get_workspace_hygiene` | A dry run. Lists each leftover with its size, age, who made it and a standing that says why it is or is not safe to reclaim, then the plan: safe to reclaim, needs a look (with the command that saves the work first), and left alone with the reason. Ends with a plan id. |
| `tidy_workspace` | Runs the safe part of that plan. Needs `confirm=true` and the plan id you were shown. Each step looks at its target again first and skips anything that became busy. Never touches unmerged commits, uncommitted work, anything in use, or anything it could not judge. |

`create_*_session`, `get_session_status` and `get_daemon_status` add one line
when a repo has more than a handful of leftover worktrees or the gate dir has
grown past a threshold: `workspace: N leftover worktrees (X GB), M safe to
reclaim: call get_workspace_hygiene`.

## Cohort and multi-agent coordination

For running several agents against one repo at once. One implicit cohort per
daemon; the first agent to join becomes its conductor. Every tool resolves
the caller's identity from the MCP connection, not the `agentName` argument.
Two connections that pass the same name are still two different members.

| Tool | What it does |
|:---|:---|
| `join_cohort` | Join the daemon's shared coordination session. The first joiner becomes conductor. |
| `leave_cohort` | Leave. Any claims you still hold are orphaned (the conductor must reassign them). |
| `get_cohort_status` | Members, claims and fences, the test matrix, the landing queue, and what the trunk did with each landing (see below). Wait-free: reads a published snapshot. The `cohort://status` MCP resource is the frame as JSON for subscription, and doesn't carry the trunk lines. |
| `acquire_claim` | Take an exclusive claim over a file or project (`file:<path>` or `project:<path>`) so others know it's yours to edit. |
| `release_claim` | Release a claim you hold. The presented fence must match the current one. |
| `reassign_claim` | Conductor-only: reassign an orphaned claim to a present member. |
| `request_landing` | Queue a landing: your commits are rebased onto the integration head, verified against affected tests, and fast-forwarded in. Landings are strictly serial (one FIFO queue). |
| `set_integration_ref` | Conductor-only: configure the git ref that landings rebase onto, in a dedicated integration worktree, and the trunk checkout the landings are carried to. The reply names both (`worktree=` and `trunk=`). |

### The trunk

A landing that lands is carried into the app the trunk runs. The **trunk checkout** is a daemon-owned git worktree
(`cohort-trunk` under the data dir, detached) that `set_integration_ref` creates beside the integration worktree. After a
landing has landed, and only then, the daemon moves the trunk checkout to the landing's commit. A **trunk session** is a
session whose working directory is that checkout: start one with `create_project_session` (`workflow` `hotreload`) and
`run_app`. When it runs an app, the daemon tells its worker which files the landing changed and the worker runs the same save
pipeline a person's save takes. The app serves the landing without a restart and keeps its in-memory state, or restarts and
says why.

`get_cohort_status` ends with one line per landing:

```
Trunk: checkout=/data/cohort-trunk landings (2):
  trunk l-4f2a...: session c5121ff3: no running app to update (the trunk checkout holds the landing; rebuild the session before run_app, so the app starts from it)
  trunk l-9be1...: session c5121ff3: Alice.fs PatchPending by metadata-delta
```

The case (`PatchPending`, `Patched`, `Restarted`, `RestartRequired`, `NoEffect`, `CompileFailed`) and the mechanism (`detour`,
`metadata-delta`) are the ones `get_session_status` reports in `lastReload`. A patch is `PatchPending` until its new body has
run, and the line changes to `Patched` when the worker says it has seen that. A restart names its cause on the line. A
landing that was blocked, withdrawn or is still being verified never reaches the trunk, and a trunk session that runs no app
only records the landing. A worker in a trunk session takes its saves from landings only, so a save a person makes in the
trunk checkout is not hot reloaded there.

## Per-client config

Prefer stdio (`command`/`args`, spawns `sagefs mcp`) over HTTP (`url`,
requires the daemon already running) for the reason above.

**Claude Code**:
```
claude mcp add sagefs -- sagefs mcp
```
That lands in local scope by default: it goes into the entry for your current
project inside `~/.claude.json`, and only loads in that project. Add `-s user`
(`--scope user`) and it's written to `~/.claude.json` for every project on the
machine. For a team, `-s project` writes a `.mcp.json` at the project root,
which you can commit:
```json
{ "mcpServers": { "sagefs": { "command": "sagefs", "args": [ "mcp" ] } } }
```
(Claude Desktop is a different app with its own `claude_desktop_config.json`.
Claude Code doesn't read it.)

**GitHub Copilot (CLI)**, `~/.copilot/github-copilot/mcp.json`:
```json
{ "servers": { "sagefs": { "type": "stdio", "command": "sagefs", "args": [ "mcp" ] } } }
```

**Cursor / Windsurf**, `.cursor/mcp.json` or the Windsurf MCP settings:
```json
{ "mcpServers": { "sagefs": { "command": "sagefs", "args": [ "mcp" ] } } }
```

**OpenCode**, `~/.opencode.json`:
```json
{ "mcp": { "sagefs": { "type": "local", "command": [ "sagefs", "mcp" ], "enabled": true } } }
```

If your client only takes a `url` (HTTP), the daemon has to be running
first — see the troubleshooting note above. The `url` is
`http://localhost:37749/` for Streamable HTTP, or `http://localhost:37749/sse`
for SSE:

**GitHub Copilot (CLI)** over HTTP:
```json
{ "servers": { "sagefs": { "type": "http", "url": "http://localhost:37749/" } } }
```

**Claude Code / Claude Desktop** over HTTP:
```json
{ "mcpServers": { "sagefs": { "url": "http://localhost:37749/" } } }
```

**Cursor / Windsurf** over HTTP:
```json
{ "mcpServers": { "sagefs": { "url": "http://localhost:37749/" } } }
```

**OpenCode** over SSE:
```json
{ "mcp": { "sagefs": { "type": "remote", "url": "http://localhost:37749/sse", "enabled": true } } }
```

Works with GitHub Copilot (CLI and VS Code), Claude Code, Claude Desktop,
OpenCode, Windsurf, Cursor, and any MCP-compatible tool. With live testing
on, agents can edit files, let SageFs re-run the affected tests, and read the
result through `list_tests` and `diagnose`, no eval round-trip needed. This
is the whole point of building an MCP server instead of just a REPL: the
agent gets the same fast feedback loop I do.
