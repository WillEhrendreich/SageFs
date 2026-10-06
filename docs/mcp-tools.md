# MCP Tools Reference

SageFs runs a Model Context Protocol server on port 37749. Any MCP client
(GitHub Copilot, Claude Code, Claude Desktop, Cursor, Windsurf, OpenCode)
can connect and drive an F# session: run code, type-check it, list and verify
tests, and read live status. This is the surface I actually use every day to
work on SageFs itself, so if it's clunky, I feel it first.

SageFs gates tools when you call them. A connection that presents no member
token sees the full catalog below in `tools/list`, whatever state its session
is in. Calling one is different: a call that doesn't apply to the current session
state gets rejected with a structured error (`enforceToolCallGate` in
`SageFs/Mcp.fs`), instead of a raw failure. Call `get_daemon_status`
for daemon health, then `get_session_status` to see which tools currently
apply. In a warming-up session, for example, it
reports `send_fsharp_code` as not yet available, even though the tool is
still listed. I went back and forth on filtering the list by session state.
For now the call-time gate is what's wired up for that.

There are two exceptions, and both are about who is calling, not about the
session. A caller that presents a [member token](#member-tokens-one-identity-per-agent-run)
sees only the tools its role allows. And a daemon started with
`SAGEFS_IDENTITY_POLICY=TokenRequired` shows a connection with no token only
the status tools, unless that connection is the conductor.

The full advertised set is 70 tools, grouped below. This is separate
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
run it as a service (see "Run it as a service (Linux)" or "Run it as a
service (macOS)" in the [README](../Readme.md)). `sagefs mcp` checks for a
running daemon first, so it just bridges to yours and never becomes its owner.

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

## "404 Session not found": the daemon was replaced under a live client

The single most confusing failure here, and it is not what it looks like. Read
this before you assume your client is broken.

**What happened.** The MCP session lives in the DAEMON'S MEMORY. The SDK mints an
`Mcp-Session-Id` when a client sends `initialize`, and the daemon holds that
session in its process. So a client that connected over **Streamable HTTP**
is holding an id the daemon only knows about. If the daemon is then
**restarted** — which SageFs does on purpose whenever a new build is installed,
so you get the new code — the new daemon has never heard of that id, and every
request the client makes is refused.

Measured, same id and same port either side of a restart:

```
held session id, before restart:  200
held session id, after  restart:   404
```

Nothing was wrong with your client, your code, or your configuration. The
connection was LIVE and pointed at a session that no longer existed.

**Why HTTP makes this permanent.** An HTTP client sends `initialize` once, at
connect, and keeps the id it got. When the daemon changes, nothing tells the
client to run `initialize` again, so it keeps presenting a dead id forever, and
the only fix is a manual client reload. The protocol has no way to recover an
id in place; `initialize` is the only thing that mints one.

**Why stdio does not have this problem.** With `sagefs mcp` the session id is
captured by the BRIDGE (`SageFs/McpStdioBridge.fs`, `forward`), not by your
client — the client never sees one. The bridge is a process that lives for the
length of your session, so it is the thing that would have to recover, and
that is what the bridge's own docs are about.

**If you are on HTTP and you hit it, do this.** One command re-establishes and
PROVES the session (it first checks a deliberately stale id is rejected, so a
pass cannot mean "the endpoint merely answers"):

```
dotnet fsi scripts/reconnect-mcp.fsx
```

If it prints `OK`, the session is live again — no client reload needed. That
repairs the connection; it cannot change what your client does on its own, which
is why the durable fix is stdio.

**Registering the bridge, for any client** (Claude Code, Open Code, Cursor,
Windsurf, or anything that spawns a command):

```
claude mcp add sagefs -- sagefs mcp
```

or in raw JSON:

```json
{ "mcpServers": { "sagefs": { "command": "sagefs", "args": [ "mcp" ] } } }
```

The daemon must be reachable on 37749 (the bridge's default). Point it
somewhere else with `SAGEFS_MCP_PORT`. If the daemon is not running, the
bridge starts one.

## Reading a tool reply

A tool's answer is the FIRST text content block of its result. When the daemon saw events since your
last call (a warmup finished, a test run landed), it adds one more text block after it, which starts with
`📡 SageFs events since last call:`. The echo is its own block, never part of the answer's text, so a tool
whose answer is JSON (`nudge_value` is one) is valid JSON on its own. A client that joins every text block into one string will see the echo glued on
the end: read the first block (or `structuredContent`, where a tool has it) and treat any later block as
the daemon talking. Agents still get the echo, because their client shows every block.

Positions in a reply (`line`, `column`, `endLine`, `endColumn`) are the parser's own: lines count from 1,
columns from 0 (counted in characters, not bytes), and `endColumn` is one past the last character.

## Execution and status

| Tool | What it does |
|:---|:---|
| `send_fsharp_code` | Evaluate F# code in the session. Each `;;` is a transaction boundary: a failure discards that one statement and keeps everything before it. |
| `check_fsharp_code` | Type-check a snippet without running it, in the current FSI context. Earlier `send_fsharp_code` definitions are in scope, but namespaces still need an explicit `open`. A "not defined" error here almost always just means you forgot the `open`, nothing more sinister. |
| `cancel_eval` | Cancel a running evaluation. |
| `get_daemon_status` | Daemon version, health, memory, process telemetry, and session counts, including with no active session. |
| `get_session_status` | The selected session's lifecycle, loaded projects, progress, and tools available in the current state. Pass `wait_seconds` (default 0, capped at 60) to wait for a warming session to become Ready, or for a rebuild to finish, instead of polling; the `wait` field in the reply says how it ended (`NotNeeded`, `BecameReady`, `Faulted`, `TimedOut`). A rebuild that is still running is waited on, because the old worker keeps serving and reads Ready the whole time: `BecameReady` means the new build's worker is up, `Faulted` means the rebuild failed (the old build is still serving), and `wait.lastRebuild` says what the rebuild did (`Succeeded`, `FailedStillServing`, `FailedNotServing`, `InProgress` when the wait ran out, `NoneRecorded`). `lastReload` says what the last save did: a patch reads `PatchPending` until the new code has been seen running, then `Patched`, or `NeverEntered` if the bound passed first. See [Hot Reload](hot-reload.md#what-patched-means). `replFreshness` says whether the REPL and live tests run the build the app runs: `InSync`, or `BehindApp` with the number of saves and what was patched, after an app started with `run_app` was patched in place. `send_fsharp_code`, `check_fsharp_code`, `run_tests` and `list_sessions` say it too, after their results. `sourceState` is a different fact: whether the build the session runs is behind the files on disk. It is `InSync`, `Stale` (with the files that were written after the build, and when), `Rebuilding` (a rebuild is in progress and the old worker is still serving), or `Unknown` (with why: no project is loaded, the worker did not say when it loaded its build, a file could not be read). It is in every shape of the reply, including the warming and faulted ones. `replFreshness` is the REPL behind the app, and `sourceState` is the disk ahead of the build, so they can be true together, apart, or not at all. |
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
| `hard_reset_fsi_session` | Full reset: rebuilds the project, reloads, starts fresh. Needed after `.fsproj` or package changes. With `rebuild=true` it answers at once and builds in the background; call `get_session_status` with `wait_seconds=60` to wait for the outcome. In a repo that builds its own SageFs.Core, the build compiles against that Core, not the daemon's. |
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
| `nudge_value` | Nudge one value in a source file the session owns and write it back as just that expression. `action=inspect` lists what can be nudged (each address, its text, its hash, where it sits as `line`, `column`, `endLine` and `endColumn` and, for a literal, its kind and its `value` typed by that kind). `action=set` takes a literal or an expression plus the hash you inspected. `action=undo` and `action=redo` step through what the tool wrote. Pass `session_id` when several sessions share a directory. Only the session's project files are touched (a file outside them is refused as `NotOwned`; a project file hot reload is not watching is written, and the reply carries the `FileNotWatched` note), a stale address is refused with the reason and never guessed, the file is replaced by a rename so a failed write leaves it byte-identical, and every write is journaled first. A member token needs the Implementer role. See [Hot Reload](hot-reload.md#nudging-a-value). |
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
| `diagnose` | Diagnostic report for one session: its test failures with causal changes, the cells they touch, a ripple plan, suggestions, eval timing. Says which side it could not read (`Unmeasured`). |
| `coverage_intel` | Coverage-quality analysis for one session's failing tests: blind spots, correlated failures. Needs live testing's instrumented runs. |
| `impact_forecast` | How many REPL cells sit downstream of a cell, and how long the session's evals take. About cells, not source code. |
| `suggest_next_action` | Prioritized "what next" queue combining coverage, impact, and staleness. |
| `suggest_next_cell` | Type-directed suggestions for what to evaluate next, from the bindings the session's evals made. |
| `suggest_repair` | Given a failing test, trace causal changes and suggest the symbol to fix. |
| `plan_ripple` | Plan cascade re-evaluation for changed cells using the session's eval dependency graph. |
| `preview_what_if` | Preview what would change if a binding had a different value, without executing. |
| `decompose_pipeline` | Break an F# pipeline into stages, each classified pure / effectful / unknown. |
| `get_cell_dependencies` | The cell dependency graph of one session: what each eval produced and consumed, and the edges between them. Reports staleness as `NotMeasured`. |
| `discover_features` | Ranks the tools this daemon registers by the session's state. The list is read from the registered set, so it cannot name a tool that is not there. |

### Measured, or not available

Seven of these (`diagnose`, `coverage_intel`, `impact_forecast`, `plan_ripple`, `preview_what_if`, `suggest_next_cell`,
`get_cell_dependencies`) used to answer an empty list, `0 downstream` or `No issues detected` when they had nothing to read. A
policy that reads that as "measured, nothing wrong" is acting on a number nobody counted. So each of them answers one of two
things, in `structuredContent.answer`:

- `Measured`: it read the data. The text block is the measurement as before. A `Measured` empty list or a `0` is a real zero.
  `unmeasured` lists any side it could not read (`diagnose` says `Tests` when the session has evals and no recorded test result).
- `NotAvailable`: the data was not there. `reason` is a closed token, `message` says what is missing, `whatToDo` says what to do,
  and the text block is that same sentence, starting `Not available (<reason>).`

| Reason | Means | What to do |
|:---|:---|:---|
| `NoSessionResolved` | No session could be found for the call. | Pass `session_id` or `working_directory`. |
| `SessionNotReady` | The session is warming up, faulted or not routable. | `get_session_status` with `wait_seconds=60`. |
| `NoEvalsYet` | The session recorded no evals. | `send_fsharp_code` in that session. |
| `NoTestRunYet` | The session has no recorded test result. | `run_tests` for that session. |
| `NothingObservedYet` | `diagnose` has neither evals nor test results. | Either of the two above. |
| `CellsNotInHistory` | The cell ids are not in that session's history. | Take ids from `get_cell_dependencies`. |
| `NoUsableCellIds` | `changed_cells` was not comma-separated integers. | Pass `'0,2'`. |
| `BindingNotInScope` | `preview_what_if` was given a name nothing bound. | Evaluate it in that session first. |
| `NeedsAWorkflow` | Coverage comes from the LiveTesting workflow and the session is not in it. | `switch_workflow` with `target='livetesting'`, then `run_tests`. |
| `NeedsLiveTesting` | LiveTesting workflow, but live testing is switched off. | Switch it on in the dashboard or an editor, then `run_tests`. |
| `NoCoverageRecorded` | Live testing is on, but no instrumented run has recorded coverage yet. | `run_tests`. |

Only `coverage_intel` fundamentally needs live testing: coverage bitmaps come from its instrumented runs and nowhere else. The
others need the session's own evals or test results and nothing more.

**One session per call.** These tools take the same `session_id` and `working_directory` routing as `run_tests`, and with neither
they answer for the session the connection is working in. They read that session's own eval history (what `send_fsharp_code`
recorded for it) and the live-testing state that owns that session's tests, which is where `run_tests` records its results. They
never read another session's data, and they never fall back to the primary session's.

**`impact_forecast` is about REPL cells.** A cell is one `send_fsharp_code` eval. Downstream means later evals that use a name this
one bound. It does not measure the blast radius of a change to your source files and it does not say which tests a change
affects, so do not gate a change on it. `run_tests` is the tool that answers about tests.

Completions, the type explorer, per-file coverage, the symbol-to-tests lookup and the domain-model diagram are not MCP tools.
The editors reach the first, second and third over the daemon's HTTP API (`/api/completions`, `/api/explore`,
`/api/live-testing/file-annotations`), and `discover_features` does not list them.

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
starts one of these itself asks for a lease first and gets back `granted`, `wait`
with a retry time, or `refused`. SageFs's own session creation and
`hard_reset_fsi_session rebuild=true` take their own leases, so an agent doesn't
lease those. The full rules, for an agent, are in
[`skills/sagefs/leases.md`](../skills/sagefs/leases.md).

A lease belongs to the connection, the `agent_name` and the `working_directory` it
was asked for under. Claude sub-agents of one session share one MCP connection, so
each of them passes its own `agent_name` (and its own `working_directory`) to be a
holder of its own. Asking again under the same three returns the lease you already
hold (`grant: already_held`, same `leaseId`, expiry not renewed). A `wait` names who
holds the pool (agent, connection, directory, kind of work, when it was granted and
when it lapses), your place in line and when to ask again, and asking again keeps your
place. `get_daemon_status` shows the same rows under `leases`, one per holder.

| Tool | What it does |
|:---|:---|
| `acquire_full_build_lease` | A lease for a full `dotnet build` you start yourself. Takes `agent_name` and `working_directory`. |
| `acquire_test_suite_lease` | A lease for a test-suite process you start yourself (`dotnet run --project <tests>` or `dotnet test`). SageFs has no tool that runs your suite for you. It runs its own tests through the live-testing engine, and you read them with `list_tests` or `run_tests`. Takes `agent_name` and `working_directory`. |
| `acquire_run_app_lease` | A lease for a run-app process you start yourself. SageFs's own `run_app` is a different thing and needs no lease from you. Takes `agent_name` and `working_directory`. |
| `release_work_lease` | Release a lease by the id a granted acquisition returned. A lease held by another connection is never released. Sub-agents that share your connection can release it by its id, so keep the id to yourself. |

A lease that is never released is reclaimed when it expires, and a queued ask
that is not repeated within five minutes loses its place, so an agent that
crashes can't hold a slot, or a place in line, forever.

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

A member's id in every cohort output (`get_cohort_status`, the `cohort://status`
frame, the SSE rows, the ledger export, the lease listing) is `mcp:m-<16 hex>`,
a one-way fingerprint of the connection's session id. It used to be the session
id itself, which is the SDK's bearer handle: anyone who could read the status
could present it as `Mcp-Session-Id` and act as that member, the conductor
included. The id names a member now and cannot be used to be one.

**Every cohort tool below takes `working_directory`.** Cohorts are scoped per repository,
so a tool needs to know WHICH repository's cohort you mean: `join_cohort` in Nehemiah and
`join_cohort` in Molina are different cohorts with different conductor seats, and one global
cohort would have made the second collide with a cohort it never asked for. It must be the
exact MCP wire name `working_directory` — the registered tool surface enforces that spelling
on every directory parameter, so one name works everywhere a client needs it. The rows
mention it only where the behaviour is worth calling out; assume the rest take it too.

**If a cohort tool says you have not joined.** The refusal reads `<tool> needs a seat in the cohort this
call acts in, and you have not joined it`. Either you never called `join_cohort`, or you joined a
different repository's cohort than the one this call names. Call `join_cohort` with the same
`working_directory` you pass to the failing tool, then retry. A call that names no directory acts in the
cohort of the repository the daemon was started in, so an agent in any other repository has to pass its
directory on every cohort call, not just the first. `get_cohort_status` with that directory shows whether
you are seated there.

Daemons up to 0.6.891 got this wrong in the other direction: an Implementer who had joined a repository
other than the daemon's own (and was its conductor) was refused `acquire_claim`, `release_claim` and
`request_landing` with `your role is Working: that needs the Working role`. The gate was reading the
daemon's own cohort instead of the one the call acts in. If you see that text, the daemon predates the fix.

| Tool | What it does |
|:---|:---|
| `join_cohort` | Join your repository's coordination session. The first joiner **of that repository** becomes its conductor. Takes `working_directory`: cohorts are per repository, so an agent in a second repository joins a second cohort with its own conductor seat instead of colliding with a cohort it never asked for. The reply always says which seat you hold: that you are the conductor, who the conductor is, or that the seat is vacant and why (the conductor left, its lease lapsed, or it was revoked). After a daemon restart your connection is a new identity, so a reply naming a conductor that is not you means your old self still holds the seat until its lease lapses. |
| `leave_cohort` | Leave. Any claims you still hold are orphaned (the conductor must reassign them). |
| `get_cohort_status` | Members, claims and fences, the test matrix, the landing queue, and what the trunk did with each landing (see below). Wait-free: reads a published snapshot. The `cohort://status` MCP resource is the frame as JSON for subscription, and doesn't carry the trunk lines. Takes an optional `working_directory`: one daemon holds one cohort **per repository**, so pass the directory you are working in to read that repository's cohort. Omit it and you read the cohort of the directory the daemon itself started in. |
| `acquire_claim` | Take an exclusive claim over a file or project (`file:<path>` or `project:<path>`) so others know it's yours to edit. |
| `release_claim` | Release a claim you hold. The presented fence must match the current one. |
| `reassign_claim` | Conductor-only: reassign an orphaned claim to a present member. |
| `delegate_conductor` | Conductor-only: hand the conductor seat to another **present** member. Only a sitting conductor can issue it, so it cannot fill an empty seat. Refused with the roster of present members when the target is not here, when it is yourself, and when it acts under a member token (no token may call the conductor's tools, so the seat would be stranded). |
| `request_landing` | Queue a landing: your commits are rebased onto the integration head, verified against affected tests, and fast-forwarded in. Landings are strictly serial (one FIFO queue). |
| `withdraw_landing` | Take back a landing **you** requested, in any state short of landed or already withdrawn (a vetoed one included). Nobody else can withdraw it, the conductor included. |
| `veto_landing` | Object to a **live** landing (queued, rebasing or verifying) with a reason. It is blocked, awaiting the conductor, and leaves the queue so it holds nobody behind it. Allowed for a seated Implementer, Verifier or the conductor; refused for an Observer, for someone who never joined and for a member who left. A landing that landed, was withdrawn, is blocked for another reason or is already vetoed is refused, and the first veto and its reason stand. |
| `resolve_veto` | Conductor-only: clear a veto. The landing queues again as it was, and its claims are checked again when it reaches the front. |
| `set_integration_ref` | Conductor-only: configure the git ref that landings rebase onto, in a dedicated integration worktree, and the trunk checkout the landings are carried to. The reply names both (`worktree=` and `trunk=`). |
| `mint_member` | Conductor-only: mint a per-run member token bound to a role, a scope prefix, the one session or checkout it may act on, and an expiry. The token comes back once. See [member tokens](#member-tokens-one-identity-per-agent-run). |
| `revoke_member` | Conductor-only: cut a member token off. It is refused from the next call, its seat departs and its claims are orphaned. |

### Veto, withdraw and delegate

Four commands sat in the cohort core for a long time with nothing that could issue them: handing the
conductor seat on, withdrawing a landing, vetoing one, and clearing a veto. So a conductor couldn't leave
cleanly, nobody could object to a landing, and a vetoed landing said it was waiting for the conductor when the
conductor had no verb to answer with. They're tools now (`delegate_conductor`, `withdraw_landing`,
`veto_landing`, `resolve_veto`), and the rules are the cohort's own. `SageFs.Simulation/CohortVetoSpec.fs`
explores every command against every landing and target, with a twin for each rule that the proof has to catch.

- **Who may veto.** A seated Implementer, a Verifier, or the conductor. The old design said any present member
  and checked nothing, so someone who never joined, or who had left, could block a landing, and so could an
  Observer, whose whole job is reading. A veto needs a reason (1 to 1000 characters) and a landing that's still
  live (queued, rebasing or verifying). Vetoing one that already landed or was withdrawn is refused, not
  quietly accepted. So is vetoing one that's blocked for another reason (a veto would relabel a rebase conflict
  as "awaiting the conductor") or already vetoed (the first vetoer and reason stand).
- **Who may withdraw.** Only the landing's requester.
- **Who may clear a veto or delegate.** Only the sitting conductor. An empty seat is refused as `VACANT`, and
  nothing in the tool surface fills one: delegating needs a conductor to do it, and nobody gets promoted by a
  timer. A person has to restore a conductor.
- **One conductor at a time.** The seat is a single binding. A delegation moves it from the caller to a present
  member in one step and the caller is an ordinary member after. A target who isn't present gets refused with
  the list of members who are. A member acting under a token can't take the seat, because no token may call
  the conductor's tools and the seat would be stuck with them.
- **Where it shows.** `get_cohort_status` prints a veto as `Blocked(vetoed by <member>: "<reason>")` plus the
  two ways out, and the dashboard's cohort panel lists each landing the same way.

A Verifier token may `veto_landing` (that's the role that reads the tests). Observer and Analysis tokens may
not. `delegate_conductor` and `resolve_veto` are conductor-only and in no token role, like `reassign_claim`.

Claim paths are canonical. `file:src/Foo/../Bar/x.fs` is `file:src/Bar/x.fs`, so
the two overlap, and a path that climbs out of the repo (`file:../x.fs`) or is
rooted (`file:/etc/x`) is refused.

### Member tokens: one identity per agent run

An orchestrator that runs N agents behind one connection used to get one member,
because identity was the connection. `mint_member` fixes that: the conductor
mints a token per run, each token is its own cohort member (`cap:<16 hex>`), and
a token outranks the connection it arrives on.

**Mint.** `mint_member role=Analysis scope=src/Foo/ ttl_minutes=60 working_directory=/abs/path/to/checkout`.
The reply has the member id, the grant and the token (`sfm_...`), once. SageFs
keeps only the token's SHA-256, so it is in no ledger row, log line or status
text, and it cannot be shown again. Tokens live in the daemon's memory and do
not survive a restart: the orchestrator mints new ones. Because no token
outlives a restart, there are no tokens from before the route binding existed
and nothing to migrate. Only the conductor mints, and a token can only be
narrower than whoever mints it: a wider role, a wider scope, another route or a
later expiry is refused with the widenings named, never clamped.

**Route.** Every token names the one place it may act, and you say where with
exactly one of two parameters:

- `working_directory`: an absolute path to a checkout. The token may act on
  sessions rooted inside that directory. A git worktree nested under it is its
  own checkout, so a token for `/repo` does not reach a session in
  `/repo/.claude/worktrees/agent-x`, and a token for the worktree does not reach
  `/repo`. The member is seated in that repository's cohort.
- `session_id`: one session. The member is seated in the repository that
  session lives in.

Naming neither is refused, naming both is refused, and a relative path or the
file system root is refused. No mint can produce a token that routes anywhere:
that is the conductor's own authority, which a connection with no token has, and
it is not something a grant can carry. A token can hand out only its own route,
because whether one session is inside a checkout is a fact about the registry and
not about two grants.

How a call is held to the route depends on what the tool does with a session or
a directory. Every tool has exactly one of these kinds, and a tool with no kind
is refused to a bound token, so a tool added tomorrow cannot be forgotten:

| Kind | Tools | What a bound token may do |
|:---|:---|:---|
| Acts on a session | everything that evaluates, analyses, tests, resets, runs the app or reads a session's status or history | Name its own session or a directory inside its route. Name nothing and the call acts on its session. Name another and it is refused. |
| Lists sessions | `list_sessions` | Allowed, and it shows only the sessions inside the route. |
| Creates a session | `create_project_session`, `create_solution_session`, `create_bare_session` | A checkout token creates sessions inside its checkout and nowhere else. A session token creates none. |
| Picks a cohort | the cohort tools, `mint_member`, `revoke_member` | Must pass `working_directory` inside its route. Left out, the call would act in the daemon's own repository, so it is refused with the directory to pass. |
| Reads a directory | `get_available_projects` | Must pass a `working_directory` inside its route. |
| Daemon-wide | `get_daemon_status`, friction, leases, `decompose_pipeline` | Allowed. A session or directory it names is still held to the route. |

Two things back that up. A `session_id` or `working_directory` that a call names
is held to the route on every tool, whatever the tool does with it, and a
directory is resolved before it is compared, so `/repo/a/../b` is `/repo/b`. And
underneath every tool body the daemon shows a bound caller only the sessions
inside its route: the others are absent from `list_sessions`, from a status
lookup and from the registry a tool reads, and a stop, restart or app command on
one is refused. So a call that names nothing cannot fall through to "the only
other session", and an active-session pointer that points elsewhere finds
nothing.

**Present.** In the `X-SageFs-Member-Token` HTTP header (one value per
connection), or in the request's `_meta["sagefs/memberToken"]` (per call, so one
connection can speak for many members: the gateway sets it, the model never sees
it). `sagefs mcp` reads `SAGEFS_MEMBER_TOKEN` from its environment and sends it as
the header on every request. There is no tool argument for a token on purpose:
an agent's transcript keeps every argument. A token that is presented and bad
(unknown, expired, revoked, lapsed, unreadable) is refused. It is never quietly
downgraded to the connection's own identity.

**Roles.** A closed set. Each role is a set of tool classes, and a tool with no
class is refused, so a new tool cannot be reachable by accident.

| Role | Adds to the one above | Cohort role |
|:---|:---|:---|
| `Observer` | `get_cohort_status`, `join_cohort`, `leave_cohort`, `get_daemon_status`, `get_session_status`, `list_sessions`, `get_available_projects`, `list_runnable_projects`, `get_friction_report`, `get_friction_summary`, `discover_features`, `get_recent_fsi_events`, `switch_session`, `report_friction` | Observer |
| `Analysis` | `check_fsharp_code`, `diagnose`, `coverage_intel`, `impact_forecast`, `suggest_next_action`, `plan_ripple`, `preview_what_if`, `suggest_next_cell`, `get_cell_dependencies`, `decompose_pipeline`, `explain_test_failure`, `list_tests`, `suggest_repair`, `get_session_filmstrip`, `get_eval_timeline`, `get_eval_diff`, `get_message_journal`, `export_notebook`, `export_session_transcript`. No eval, no tests. | Observer |
| `Verifier` | `run_tests`, `targeted_verify`, `acquire_full_build_lease`, `acquire_test_suite_lease`, `acquire_run_app_lease`, `release_work_lease`, `veto_landing`. No eval. | Verifier |
| `Implementer` | `send_fsharp_code`, `cancel_eval`, `manage_scratch_pad`, the session tools (`create_*_session`, `reset_fsi_session`, `hard_reset_fsi_session`, `switch_workflow`, `stop_session`, the hot reload tools), `run_app`, `stop_app`, `acquire_claim`, `release_claim`, `request_landing`, `withdraw_landing` | Implementer |

No role can call `mint_member`, `revoke_member`, `reassign_claim`, `delegate_conductor`,
`resolve_veto`, `set_integration_ref`, `manage_local_data`, `get_workspace_hygiene` or
`tidy_workspace`. A token call also passes the cohort's own role check, so an
Observer, Analysis or Verifier token can read the cohort but not claim a scope or
queue a landing. `tools/list` shows a token only the tools its role allows. The
role says which tools a token may call and the route (below) says where it may call
them, and a call must pass both.

**Scope.** A prefix such as `src/Foo/`. The token can claim only inside it, and
the path is canonicalized first, so `src/Foo/../Bar` counts as `src/Bar`. This is
policy, not a sandbox: it refuses claims, and it does not stop a process from
writing a file. Containment is the sandbox's job. And `Implementer` includes
`send_fsharp_code`, which runs arbitrary F# as the daemon's OS user, so against a
hostile agent only the roles without eval mean something.

**Expiry.** `ttl_minutes` from now (0 means 120, at most 480). A token also lapses
after 30 minutes with no call, the way a silent member's seat does, and every
call it makes keeps its seat renewed.

**Revoke.** `revoke_member member_id=cap:<id>`. The token is refused from the
next call, for ever: a revoked token stays revoked and its hash cannot be minted
again. The seat departs and its claims are orphaned, so the conductor can
`reassign_claim` them. A build or test lease the member already holds runs out on
its own.

**Identity policy.** `SAGEFS_IDENTITY_POLICY` on the daemon decides what a
connection with no token is. `ConnectionsAllowed` (the default, and what happens
when it is unset) leaves it a plain member, exactly as before tokens existed.
`TokenRequired` lets it read `get_cohort_status` and `get_daemon_status`, and lets
the conductor act (so it can mint); everything else is refused with a message that
says how to get a token. The first member to `join_cohort` is still the conductor,
and a connection with no token may call `join_cohort` until there is one, so the
orchestrator starts the daemon, joins first, then mints. A value that is set but not one of the two fails closed,
as `TokenRequired`. Without `TokenRequired` the narrow roles sit beside an
unrestricted door, so the allow-list is advice.

**What a refusal looks like.**

```
Error: This member token has the Analysis role, which cannot call send_fsharp_code:
that is evaluating F#. → Next: send_fsharp_code needs the Implementer role. Ask the
conductor to mint a token with it (mint_member), or use a tool your role allows
(tools/list shows them).
```

```
Error: This member token is confined to 'src/Foo' and cannot claim file:src/Bar/a.fs.
→ Next: Claim a path inside 'src/Foo', or ask the conductor to mint a token for a
wider scope.
```

```
Error: This member token is bound to session a1b2c3d4, and session d7b45c0e is not inside that.
→ Next: Pass session_id a1b2c3d4, or no session at all to act on it. To reach another
session, ask the conductor to mint a token bound to it (mint_member).
```

```
Error: join_cohort acts in the repository of the directory it is given, and this call named
none, which would mean the daemon's own repository. This member token is bound to the
checkout /work/checkout-a.
→ Next: Pass working_directory /work/checkout-a (or a directory inside it).
```

**What the route does not stop.** It decides what the tool surface will do for a
caller holding the token, and like the role and the scope it is policy and not a
sandbox. These are specific to the route:

- An `Implementer` token can run code (`send_fsharp_code`), and that code runs as
  the daemon's OS user. It can call the daemon's HTTP API on loopback with any
  session id, read the data directory and read `/proc`. The route does not
  change that. Against a hostile agent only the roles without eval mean
  something.
- The HTTP API and the MCP resources (`sessions://list`, `cohort://status`) do
  not read the token, so they are not confined to the route.
- The daemon-wide reads show the whole daemon: `get_daemon_status` reports
  session counts, worker processes by session id prefix and which agents hold
  build and test leases for which directories, and the friction reports name
  sessions. They are allowed to every role that may call them.
- A directory is compared as a path and not through the file system, so a
  symbolic link inside a checkout that points at another checkout is inside.
- A token bound to a checkout acts on every session rooted in that checkout, not
  one of them in particular. Bind to a `session_id` when one is what you mean.
- `switch_session` by a bound token moves the daemon's active-session pointer to
  that token's own session, like any caller's, and nothing else.
- A token dies with the daemon. The route, the role and the expiry all go with it,
  and the orchestrator mints again.

**Limits.** A token is held by whatever sends it, so a leaked one is
impersonation until it expires or is revoked. A connection-wide header cannot tell
apart sub-agents that share a connection (Claude Code's `Agent` tool): only a
per-call `_meta` can, and Claude Code gives the model no per-call header. The
conductor on a `ConnectionsAllowed` daemon is still identified by its connection,
and a connection with no token routes anywhere, which is what lets the conductor
mint for any session.

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
run, and the line changes to `Patched` when the worker says it has seen that. A restart names its cause on the line.
`lastReload` also carries `callers`: when a save re-signs or removes a function, it lists, per declaration, the file, line
and calling declaration of every caller in another file that still calls the old one (`CallersPending`), with the next
action ("Save Pages.fs"); it reads `CallersCurrent` once those files have landed, and `CallersNotChecked` with the reason
when the project's other files could not be searched. A
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
