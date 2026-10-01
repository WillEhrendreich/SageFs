# Troubleshooting SageFs

Quick fixes for common issues. If your problem isn't listed here, check the
[GitHub Issues](https://github.com/WillEhrendreich/SageFs/issues) or run the
health check in your editor. If it's broken, file it. I'd
rather hear about it than have you quietly work around it.

## Editor Health Checks (Start Here)

| Editor | Command |
|:-------|:--------|
| **VS Code** | `Ctrl+Shift+P` → "SageFs: Check Health" |
| **Neovim** | `:checkhealth sagefs` |
| **CLI / dashboard** | `sagefs status` and `http://localhost:37750/dashboard` |

---

## Common First-Run Issues

### "SageFs daemon not found" / CLI not installed

```bash
dotnet tool install --global SageFs
```

Verify: `sagefs --version` should print the version. If the command isn't
found, make sure `~/.dotnet/tools` is on your `PATH`.

**Requires**: the .NET 10 SDK or the .NET 11 SDK to install SageFs itself (the
package carries a build for each, and `dotnet` picks the one that matches your
SDK). Check with `dotnet --version`. Sessions can be on .NET 10 or .NET 11. Each
session's host builds with whichever SDK `dotnet --version` reports in your
project's folder, and runs on that SDK's runtime. That's your `global.json`
pin if you have one, otherwise the newest SDK installed. So if an 11 preview
is installed and you want a project on 10, pin it.

### Daemon won't start / times out

1. **Check if another instance is running**: `sagefs status`. If it shows a
   running daemon, stop it with `sagefs stop` or use the existing one.
2. **Port in use**: Default is 37749 (the dashboard is the next port up). To
   pick a different port for the daemon you want to keep, set
   `SAGEFS_MCP_PORT=8080` in its environment. `--mcp-port 8080` on its own is
   refused, because a daemon off the default port has to say who owns it
   (`--owner-pid <pid>`) or when to give up (`--ttl 30m`). In VS Code, set
   `sagefs.mcpPort` in settings.
3. **Check the SageFs console window**: it logs startup errors to its
   own terminal window. Look for .NET SDK errors, missing project files, or
   compilation failures. The daemon also writes a log file
   ([Where the logs are](#where-the-logs-are)).
4. **First-time JIT warmup**: The very first launch after install takes longer
   (NuGet restore + JIT compilation). Give it 30–60 seconds.

### "No .fsproj or .sln found"

SageFs needs a project file. Either:
- Open a folder containing a `.fsproj` or `.sln` / `.slnx` file
- Set the project path explicitly:
  - **VS Code**: `sagefs.projectPath` in settings, or use "SageFs: Switch Project"
  - **Neovim**: `:SageFsSwitchProject` or set `vim.g.sagefs_project_path`
  - **CLI / dashboard**: start `sagefs`, then create or switch to a session for `path/to/MyApp.fsproj`

### Wrong project selected (multi-project workspace)

When a workspace has multiple `.fsproj` files, SageFs picks one. If it chose
the wrong one:
- **VS Code**: Run "SageFs: Switch Project" from the command palette
- **Neovim**: `:SageFsSwitchProject`
- **CLI / dashboard**: create or switch to a session for `path/to/CorrectProject.fsproj`

The active project is shown in the status bar.

---

## Runtime Issues

### Evaluation hangs / no result appears

- **Check the daemon is alive**: Look for the SageFs console window. If it
  crashed, restart via your editor's "Start Daemon" command.
- **SSE connection dropped**: The status bar shows connection state. If
  disconnected, most editors auto-reconnect. You can also trigger reconnect
  manually (VS Code: Command Palette → `SageFs: Reconnect to Daemon`, Neovim:
  `:SageFsReconnect`).
- **Long-running eval**: Some evaluations genuinely take time (large
  compilations, network calls). Check the daemon console for progress.

### Stale REPL after code changes

Use hard reset to pick up source file changes:
- **VS Code**: "SageFs: Hard Reset" from command palette
- **Neovim**: `:SageFsHardReset`
- **MCP**: `hard_reset_fsi_session` tool with `rebuild=true`

**If a hard reset doesn't fix it, read the next section.** A hard reset reloads
the session. It cannot help when the stale thing is the daemon itself.

### Stale daemon — the one that wastes the most time

A daemon that has been running since before your code changed keeps serving the
assemblies it started with. Nothing warns you, and it does not look like a
version problem. It looks like SageFs is broken.

Three ways it shows up:

- `type not found, Version=...`, or a `SageFs.Core` version mismatch.
- `Could not load file or assembly 'System.Runtime, Version=N.0.0.0'` in worker
  stderr, on a project that builds perfectly on its own. This one means the
  daemon's worker is running an older .NET than your project targets: it starts
  fine, then fails the moment it loads your DLLs.
- No error at all. Evals just quietly disagree with the code in front of you.

**Check the daemon before you believe any other diagnosis:**

```bash
sagefs status
```

It prints both `Version` and `Started`. `Started` is usually the faster tell:
if the daemon has been up since before your last build, it is serving code
older than what you're looking at, whatever the version says.

If it's behind the code you're working on, restarting the *session* won't help —
restart the *daemon*:

```bash
sagefs stop
dotnet tool update --global SageFs
sagefs
```

If `dotnet tool update` reports "already installed" when you know a newer
version is published, pass the version explicitly:

```bash
dotnet tool update --global SageFs --version X.Y.Z
```

`dotnet tool update` resolves through NuGet's search/registration index, which
lags the package store by a few minutes after a release. The package can be on
NuGet while the CLI still can't see it.

### Where the logs are

There are three places, and which one you want depends on what broke. Paths
below are what the code builds
([`DaemonState.fs`](../SageFs.Core/DaemonState.fs),
[`WorkerLogFile.fs`](../SageFs.Core/WorkerLogFile.fs)).

**The daemon log.** The daemon writes `mcp-server<yyyyMMdd>.log`, for example
`mcp-server20260930.log`, in its log directory. That is the `SageFs` folder under
your local application data directory (`~/.local/share/SageFs` on Linux,
`%LOCALAPPDATA%\SageFs` on Windows), or the data dir itself when
`SAGEFS_DATA_DIR` is set. You don't have to work the path out. The dashboard
port serves it:

```bash
curl -s http://localhost:37750/api/daemon-info
```

The `logPath` field is the file being written right now. A day that outgrows the
size cap rolls to `_001`, `_002` beside it, so the name can differ from the one
you'd guess. The daemon logs Information and above, rolls a file at 50 MB and
keeps 7 of them, so the daemon log never costs more than 350 MB. (`37750` is the
MCP port plus one. If you moved the MCP port, so did the dashboard.)

**The worker log.** Each session's worker writes
`<data dir>/workers/<sessionId>.log`, where the data dir is `~/.SageFs` unless
`SAGEFS_DATA_DIR` says otherwise. This is where project loading, warmup and hot
reload messages from inside the worker end up, one line each, as
`<UTC time> [INF|DBG|WRN|ERR] message`. The session id is in `list_sessions` and
on the dashboard. Each file is capped at 4 MiB. When the next line would pass
that, the file moves to `<sessionId>.log.1`, replacing the previous one, so a
session costs at most 8 MiB. The daemon doesn't delete a log by itself. Workspace
hygiene (`get_workspace_hygiene`, `sagefs hygiene`) lists a worker log as
expired once it has gone 14 days without a write and its session is gone
(`SAGEFS_WORKER_LOG_MAX_AGE_DAYS` changes that), and `tidy_workspace` or
`sagefs hygiene --tidy` removes it when you confirm the plan. A log for a session
that no longer exists is also just a file you can delete. If a worker can't open
its log it says so on stderr and carries on without one.

**The stderr tail.** While a worker is starting, the daemon keeps the last 200
lines of its stderr. If the worker exits before it reports its port, goes quiet
past the inactivity limit, passes the warmup ceiling, or can't be connected to,
the last 20 of those lines are added to the failure reason under a `stderr:`
heading. That reason is the session's fault reason, and the daemon log has it too
(`Worker spawn failed for session ...`). The buffer is not read after startup, so
a worker that dies later leaves its story in its worker log and nowhere else.

### When health says Degraded

"Degraded" is said in three places and means something different in each. All
three are on `/health` (the MCP port, `37749`), and the dashboard shows the
reason in full.

**A session is Degraded.** The session works, but something you'd expect to work
won't, and the reason says what to do. `/health` and `/api/sessions` carry it as
`health: { status, reason }` per session
([`SessionHealth.fs`](../SageFs.Core/SessionHealth.fs)). Today there are four
causes, and the first can have several reasons:

- A project was evaluated by MSBuild, but the host couldn't make everything
  right for it ([`HostConcern`](../SageFs.Core/ProjectLoading.fs)). The reason
  says which: the project's `runtimeconfig.json` couldn't be read so the host's
  default runtime was a guess, its target framework isn't one SageFs recognises,
  the project's FSharp.Core call sites couldn't be redirected to its own build
  (code calling a member the host's FSharp.Core lacks will throw), or the project
  carries its own FSharp.Compiler.Service, which the host can't replace with it.
  Each reason names what to do. [How SageFs opens projects plain FSI can't](how-isolation-works.md)
  has the rest.
- A project was loaded by hand-parsing the `.fsproj` instead of MSBuild
  evaluation. Code evaluates, but there is no build output, so `run_app` and hot
  reload won't work. The reason names why MSBuild failed (it threw, it returned
  nothing, or the project's SDK is newer than the .NET SageFs itself runs on).
  Run `dotnet build` on the project and read the first MSBuild error.
- A project's assembly was built with optimizations. A hot-reload patch to one of
  its functions can be bypassed by inlining, and a live value it holds can go
  untracked, and nothing else would tell you. This happens when you build Release
  by hand and SageFs finds that build up to date. Run `hard_reset_fsi_session`
  with `rebuild=true`: SageFs builds sessions with `-p:Optimize=false`
  ([`BuildOptimization.fs`](../SageFs.Core/BuildOptimization.fs)).
- The session is Ready but warmup loaded nothing: zero assemblies and zero
  namespaces opened, though a project was resolved. The project has most likely
  never been built. Build it, then `hard_reset_fsi_session` with `rebuild=true`.
  If warmup recorded failed opens, the reason quotes them instead.

A session that is Faulted or Stopped is `Failed`, with the reason. One that is
still starting, building or restarting is `Starting`.

**The daemon is Degraded.** `/health` has an `overall` field that is `Healthy`,
`Degraded` or `Unhealthy`
([`DaemonHealth.fs`](../SageFs.Core/Features/DaemonHealth.fs)). It is Degraded
when any session is Faulted, when the machine's available memory is down to the
point SageFs calls tight (it enters at 20% available and leaves at 30%, and
while tight, expensive work runs one at a time), or when the session manager is
in trouble. It is Unhealthy when memory is critical (8% available, easing back to tight at
15%) or one of the daemon's own telemetry signals has broken from its normal.
`memoryPressure` and `anomalies` in the same response say which.

**The session manager is in trouble.** The supervisor watches the loop that
handles session commands (create, stop, restart) and reports one of three
things ([`SupervisorWatchdog.fs`](../SageFs.Core/SupervisorWatchdog.fs),
[`SupervisorHealth.fs`](../SageFs.Core/SupervisorHealth.fs)):

- The loop threw and was restarted from its last good state. Look in the daemon
  log for `Mailbox loop threw unexpectedly`.
- One command's handler threw and the loop carried on without it. The reason
  names the command. Check the daemon log.
- One command has held the loop for longer than `SAGEFS_SUPERVISOR_WEDGE_SECONDS`
  (30 by default). Session commands then hang while reads, like a status check,
  still answer, which is why every session can look Ready on a daemon that can't
  act on them. Restart the daemon if it doesn't clear.

The supervisor goes back to healthy the next time a command finishes without
throwing, so a one-off failure clears itself and a stuck command clears when it
finally completes.

Separately, hot reload has its own health for the app it patches, and
`enable_hot_reload` reports it as `Degraded: <reason>`. The reasons in the code
today: a detour was applied but the native code did not change (the patch may be
ineffective); MonoMod could not patch on this runtime
(`PlatformNotSupportedException`); one accessor of a mutable binding was
re-pointed and its partner was not (restart the app); the dev-reload middleware
had to be appended instead of inserted first, so other middleware may answer
before it; and a hot-reload compile held the compiler past
`SAGEFS_HOT_RELOAD_COMPILE_QUEUE_SECONDS`.

### Hot reload not working

- Hot reload isn't on in every session. A session in the default REPL workflow
  starts with it off, and nothing is watched for patching until you opt in.
  Switch with `switch_workflow(target='live')`; see
  [Workflow Modes](workflow-modes.md).
- Check the `SAGEFS_DEVRELOAD` environment variable isn't set to `0` or `false`
- Look for `[DevReload]` messages in the session's worker log (see
  [Where the logs are](#where-the-logs-are))
- Ensure the file is part of the active project (listed in `.fsproj`)
- Check the session's health. A project built with optimizations is reported as
  Degraded (see [When health says Degraded](#when-health-says-degraded))
- A session whose directory is your home directory or a filesystem root is not
  watched at all. See [Hot reload and file watching](hot-reload.md#file-watching)

### Live testing not running

- Verify live testing is enabled: check your editor's test status indicator
- Check run policies: some test categories (integration, browser) default to
  `demand` (manual trigger only)
- Check live-test status in your editor: `:SageFsLiveTestStatus` (Neovim), the Tests pane (VS Code), or the web dashboard

### SSE connections dropping

- Set proxy/reverse-proxy timeout ≥ 60 seconds
- The dashboard's SSE stream patches a heartbeat signal at least every 5
  seconds by default (`SAGEFS_DASHBOARD_HEARTBEAT_SECONDS`), even when nothing
  else changed, and the page shows the disconnect banner if none arrives within
  `SAGEFS_DASHBOARD_STALE_AFTER_SECONDS` (15 by default)
- Corporate proxies may need explicit WebSocket/SSE passthrough configuration

### Eval watchdog — detecting daemon crash during eval

Supported editor integrations include an **eval watchdog**. If the daemon becomes unresponsive during an evaluation:

1. A timer starts when you send an eval command
2. If no response arrives within the timeout, the editor shows a notification:
   *"Evaluation interrupted — daemon may have crashed"*
3. The notification offers **Restart Daemon** and **Show Output** actions

The watchdog uses a monotonic generation ID to prevent race conditions. If you
start a new eval before the watchdog fires, the old timer is silently cancelled.

**If you see phantom "interrupted" dialogs**, update to v0.6.50+, which
included the monotonic ID fix. If you're on anything newer than that and
still seeing it, that's a regression. File it.

---

## Environment Variable Overrides

The waits and intervals can be overridden through environment variables. Set
them before starting SageFs (or in your shell profile). Every one, with its
default and what it is for, is on the
[configuration page](configuration.md), which is generated from the code. The
ones people usually reach for:

| Variable | Default | What it is for |
|:---------|:--------|:------------|
| `SAGEFS_WARMUP_INACTIVITY_SECONDS` | `30` | How long a starting worker may go without progress before it is declared stuck |
| `SAGEFS_WARMUP_MAX_MINUTES` | `10` | The hard ceiling on warmup |
| `SAGEFS_PER_TEST_TIMEOUT_SECONDS` | `5` | The starting per-test timeout for live testing |
| `SAGEFS_BUILD_TIMEOUT_MINUTES` | `10` | How long a `dotnet build` run by a session start or rebuild may take |
| `SAGEFS_BIND_HOST` | `localhost` | Loopback bind address: `localhost`, `127.0.0.1` or `::1`. Any other value stops the daemon at startup (see [Docker / Remote Containers](#docker--remote-containers)) |
| `SAGEFS_MCP_PORT` | `37749` | MCP server port |

`SAGEFS_WORKER_STARTUP_TIMEOUT_MS` used to be on this list. Nothing reads it any
more, so setting it does nothing. The two warmup variables above replaced it.

**Example**: slow CI machine with large project:

```bash
export SAGEFS_WARMUP_MAX_MINUTES=20
export SAGEFS_BUILD_TIMEOUT_MINUTES=15
export SAGEFS_PER_TEST_TIMEOUT_SECONDS=15
sagefs
```

Then create a session for `MyBigProject.Tests/MyBigProject.Tests.fsproj`.

A value that isn't a number, or is zero or negative, is silently ignored and the
default is used. There is no upper limit on these variables.

---

## Warmup Progress Phases

During session warmup, SageFs emits `warmup_progress` SSE events. Your editor's
status bar shows which phase is active:

| Phase | Status Bar Text | What's happening |
|:------|:---------------|:-----------------|
| `creating_fsi` | Creating FSI... | Spawning the F# Interactive process |
| `scanning_sources` | Scanning sources... | Reading project source files |
| `loading_assemblies` | Loading assemblies... | Loading referenced NuGet/project assemblies |
| `opening_namespaces` | Opening namespaces (N/M)... | Auto-opening project namespaces in FSI |
| `finalizing` | Finalizing... | Final validation, session ready |

Each event includes `{Step, Total, Progress, Phase, Message}`. The `Progress`
field is a 0.0–1.0 float for progress bars.

**If warmup stalls**: Check the daemon console window for compilation errors or
missing packages. Increase `SAGEFS_WARMUP_INACTIVITY_SECONDS` if your project
has slow NuGet restores.

**Large repos: name one project or solution.** The create tools have no
auto-discovery path. Use `create_project_session` for one `.fsproj`,
`create_solution_session` for one `.sln`/`.slnx`, or `create_bare_session` when
you truly want a project-free REPL. `get_available_projects` lists candidates.
If generated build state is missing, SageFs rebuilds it under a lease before it
creates the session.

**A session that never reaches Ready is bounded, not silent.** Warmup is
governed by two limits: `SAGEFS_WARMUP_INACTIVITY_SECONDS` (default 30) is how
long the worker can go with no progress before it's declared stuck;
`SAGEFS_WARMUP_MAX_MINUTES` (default 10) is the hard ceiling regardless of
progress. A session that's genuinely still working (a big repo resolving many
projects) keeps that inactivity clock reset by its own progress and can run up
to the absolute ceiling; a session that's gone quiet is faulted within the
inactivity window, with a stated reason — never left reading "Starting" with
nothing to show for it. `get_session_status` on a warming session reports the
structured `WarmingUp` state, lifecycle, target, loaded projects, and worker
facts. Use the daemon or worker logs for detailed warmup timing. `stop_session`
returns promptly in every case, including on an already-faulted or still-warming
session — it does not wait for warmup to finish or fail first.

---

## Platform-Specific Issues

### macOS: VS Code "cannot read properties of undefined"

Fixed in v0.5.414+. Update the extension. The extension now degrades gracefully
if the daemon can't start.
See [#18](https://github.com/WillEhrendreich/SageFs/issues/18).

### Docker / Remote Containers

SageFs only listens on loopback. Its HTTP ports evaluate F# as your user and
have no authentication, so binding all interfaces (`SAGEFS_BIND_HOST=0.0.0.0`)
would hand code execution to anyone on the network. The daemon refuses to
start with a non-loopback `SAGEFS_BIND_HOST`, and `sagefs check` reports it.
I'm not going to make this configurable just so someone can trade an
afternoon of convenience for handing out remote code execution. Forward the
ports instead.

To reach a daemon in a container, forward ports 37749 and 37750 to the
container's loopback instead:

- **VS Code Dev Containers**: add `"forwardPorts": [37749, 37750]` to
  `devcontainer.json`. VS Code tunnels to the container's loopback.
- **Docker on Linux**: `docker run --network host ...`
- **Anything with SSH**: `ssh -L 37749:localhost:37749 -L 37750:localhost:37750 <host>`

Browser requests must come from the dashboard itself. A page served from any
other origin, including another `localhost` port, is refused. Request bodies
must be sent as `Content-Type: application/json`.

---

## Diagnostic Tools

| Tool | What it shows |
|:-----|:-------------|
| `sagefs status` | Running daemon info, port, sessions |
| `sagefs stop` | Gracefully stop the daemon |
| Daemon console window | Real-time logs, compilation output, test results |
| Log files | The daemon log and one log per session worker. See [Where the logs are](#where-the-logs-are) |
| `GET /health` on the MCP port | Per-session `health`, the daemon's `overall` verdict, and any component failures. See [When health says Degraded](#when-health-says-degraded) |
| OpenTelemetry export | Structured traces and metrics (set `OTEL_EXPORTER_OTLP_ENDPOINT`) |
| Editor output channel | Extension-side logs (VS Code: "SageFs" in Output panel) |

---

## FSI Quirks & Rewrites

- **`;;` is required**: every FSI transaction must end with `;;`
- **"Operation could not be completed due to earlier error"**: a *previous*
  submission had a compile error. Fix that code and resubmit it. The session
  is fine, do NOT reset.
- **Type changes need hard reset**: if you change a type definition (DU, record),
  the old version is cached in FSI. Use hard reset to pick up the new types.
- **Order matters**: FSI evaluates in submission order. Define types before
  functions that use them.

---

## Still Stuck?

1. Check [GitHub Issues](https://github.com/WillEhrendreich/SageFs/issues) for
   known problems
2. Run the health check for your editor (see table at top)
3. File a new issue with: editor name + version, SageFs version (`sagefs --version`),
   OS, and the error message or behavior you're seeing. The more specific,
   the faster I can actually do something about it.
