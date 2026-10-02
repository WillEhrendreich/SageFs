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
   (NuGet restore + JIT compilation). Give it 30–60 seconds. On an older machine the
   first start also builds the FSI host once, and can take a few minutes. See
   [Slow machines](#slow-machines).

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
  watched at all. See [Hot reload and file watching](hot-reload.md#file-watching).
  A daemon started with your home directory as its working directory hits this
  for every request that names no directory, so start it from a project or a
  neutral directory. The systemd unit in `contrib/systemd` runs it from
  `~/.local/state/sagefs` for that reason

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

## Slow machines

SageFs used to give every start the same fixed waits, and the numbers in them were tuned on a fast desktop. On an
older machine a session could not start at all: the daemon's 30 second inactivity limit killed the worker while it
was still building the FSI host (the first start on any machine builds it once, and that took 40 seconds on a
2009 four core), started it again with the same 30 seconds, five times, and then said
`Worker process exited with code 137 (abandoned after max retries)`. It named neither what it was waiting for nor
how long. Raising the limit by hand fixed it, and finding that out took reading logs.

What it does now, in the order it happens:

1. The daemon works out what kind of machine it is on (a **tier**, below), before anything reads a timeout. It
   uses `SAGEFS_MACHINE_TIER` if you set it, else the profile it saved last time, else a probe of about a fifth of
   a second. It writes `machine-profile.json` in the data directory and logs one line saying which tier and why.
2. Every wait that is for the machine (a start, a build, a warm-up, an exit) is multiplied by the tier's factor.
   Waits that are for a person, a network or a protocol are not. [Configuration](configuration.md#machine-tier)
   lists which is which.
3. A worker that goes silent for longer than its allowance is not killed and started again with the same
   allowance. The next attempt gets twice the silence, up to four attempts and never past the absolute bound. The
   first attempt is as patient as the tier allows or the machine's own recorded starts say it needs, whichever is
   longer.
4. When it does give up it says what it waited for, how long, how many times, what tier this machine counts as, and
   what a start here usually takes, and it says what to do. A slow tier is told up front that the first start can
   take a minute or more and that this is normal there.

### What you will see

`sagefs check` and the daemon log both say the tier and where it came from. `get_session_status` carries
`machineTier`, and a starting session's progress text carries the slow-start notice. A start that could not finish
reads like this (a real one, with the allowance forced down to 1 second to make it fail quickly):

```
The session could not start: it gave up after 4 attempts (19 s in all) waiting for the worker to load the project
and start the FSI host. Each attempt was allowed more silence than the one before (1 s, 2 s, 4 s, 8 s). Its last
report was '2/2 Instrumenting assemblies for IL coverage'. This machine counts as Fast and has not finished a start
yet, so SageFs has no history to compare with. What to do: close whatever is using the CPU or the disk and run
hard_reset_fsi_session with rebuild=true. To wait longer, set SAGEFS_WARMUP_INACTIVITY_SECONDS, or tell SageFs this
is a slower machine with SAGEFS_MACHINE_TIER=Standard. The worker's log (daemon log directory,
workers/<session id>.log) says where it stopped.
```

### Tiers

A machine counts as the slowest tier any one of these puts it in. "Reference" is a Ryzen 7 5800XT, where the
calibration in `MachineCalibration.fs` (a fixed single-thread workload, timed in the thread's own CPU time) reads
22 to 24 ms.

| Tier | Chosen when | Waits for the machine | A first start there, measured (ready, p50) |
|:---|:---|:---|:---|
| `Fast` | calibration under 1.75 times the reference (under 38 ms); 4 or more cores; 2 GB or more available; the disk does not spin | x1 | Ryzen 7 5800XT: 5.3 s warm and 14.4 s cold on 16 threads, 5.3 s and 13.9 s on 8, 5.9 s and 15.8 s on 4 |
| `Standard` | 1.75 to 3.5 times slower; 1 to 4 cores; 1.5 to 2 GB | x2 | 2 cores: 6.6 s and 22.5 s. One core: 7.9 s and 24.3 s (both emulated with a cgroup quota on the 5800XT) |
| `Constrained` | 3.5 to 8 times slower; half a core to one; 1 to 1.5 GB; a spinning disk | x5 | AMD Phenom II X4 B50, 4 cores, 5400 rpm disk: 16.8 s and 56.9 s (0.6.875 with its waits raised so it could finish; this change at its defaults: 17.1 s and 65.3 s). Half a core (quota 50% of one core, this change): 12.6 s and 83.4 s |
| `Minimal` | 8 or more times slower; under half a core; under 1 GB | x12 | not measured, see below |

"Warm" is a start with the FSI host already built. "Cold" is the first start on a machine: it builds the host once
per SDK and SageFs version. Every number is the median of 5 warm and 3 cold runs, except the Phenom's (3 and 2).

Where the factors come from. The wait that fails first is the inactivity limit, 30 s on `Fast`, and what it times is the
silent stretch between spawning a worker and its first progress report: boot, the project load, the host build on a
cold start, and the host start. Measured cold, that stretch is 12.6 s on the 5800XT (n=3), 52.5 s on the Phenom
(n=2) and 80.2 s on half a core (n=3). For the two and one core runs I did not take that figure out, but a whole cold
start to Ready took 22.5 s and 24.3 s, and the stretch is part of that. The allowance on `Constrained` is 150 s: 2.9
times the Phenom's stretch and 1.9 times the half core's. On `Standard` it is 60 s, 2.5 times the longest whole cold
start measured there. Escalation covers what a factor misjudges: a start that needs more is retried with twice the
allowance, up to four attempts.

What I could not measure, so the numbers are a guess: `Minimal`. Nothing I had access to is slower than half a core
or the Phenom, so x12 is an extrapolation (a 360 s allowance, about 4.5 times the half core's silent stretch), not a fitted number. The
boundaries between tiers other than the ones above come from the same data and should be treated as the best fit to
four points, not as a law.

### What was measured

Two machines, and one of them pretending to be several.

- **The fast one:** AMD Ryzen 7 5800XT, 8 cores and 16 threads, 64 GB, NVMe, .NET SDK 11 running the net10 tool. Single
  thread PassMark 3533. The calibration reads 22 to 24 ms on it.
- **The old one ("orvus"):** AMD Phenom II X4 B50, 4 cores at 3.2 GHz, 7.9 GB, 5400 rpm disk, Arch, .NET SDK 10.0.401.
  Single thread PassMark 1289, 2.7 times slower than the 5800XT. The calibration reads 122 to 133 ms (wall clock; it
  reads thread CPU time now, which is lower on a busy machine and the same on a quiet one). Quiet when measured, one run
  at a time, `nice 19`, its own port pair and data directory.
- **The pretend ones:** the 5800XT confined with `systemd-run --user --scope` (`taskset` to a core count, `CPUQuota`
  to a share of a core, `MemoryMax` with swap off). `t4` is 4 cores, `t2q50` is 2 cores at half a quota, so one
  core's worth, `t1q50` is half a core. What it cannot pretend is a slower clock or a slower disk (that needs root).

Every row is from a throwaway daemon in its own data directory on its own port, driving a two project fixture
(a library and an Expecto test project), 5 warm runs and 3 cold runs, medians, seconds, in the shipped 0.6.875 with the
shipped defaults. The Phenom's numbers are 3 warm and 2 cold runs, with the waits raised so a slow start could finish
and be measured. The last column is the build in this change, which could finish where 0.6.875 could not.
Reproduce with `dotnet fsi scripts/machine-bench.fsx --sagefs <dir>`.

**Warm** (the FSI host is already built):

| stage (s) | 5800XT, 16 threads | 4 cores | 1 core | Phenom II X4 | half a core, this change |
|:---|---:|---:|---:|---:|---:|
| daemon up to /health | 0.60 | 0.72 | 0.70 | 4.78 | 1.90 |
| project load | 1.16 | 1.18 | 1.55 | 4.46 | 3.10 |
| host prepare | 0.60 | 0.67 | 0.63 | 2.03 | 1.38 |
| FSI session create | 1.53 | 1.87 | 2.51 | 5.56 | 4.49 |
| **ready (create to Ready)** | **5.3** | **5.9** | **7.9** | **16.8** | **12.6** |
| first eval | 0.40 | 0.53 | 0.41 | 1.88 | 0.90 |
| hard reset | 4.4 | 5.1 | 7.1 | 16.4 | 12.2 |
| hard reset, rebuild | 7.2 | 9.4 | 12.1 | 28.9 (15.0 to 30.1) | 28.3 |
| stop session | 5.1 | 5.2 | 5.2 | 5.2 | 5.5 |

**Cold** (the first start on a machine, which builds the FSI host):

| stage (s) | 5800XT, 16 threads | 4 cores | 1 core | Phenom II X4 | half a core, this change |
|:---|---:|---:|---:|---:|---:|
| daemon up to /health | 0.60 | 0.62 | 0.72 | 4.82 | 2.01 |
| project load | 1.09 | 1.08 | 1.61 | 4.95 | 3.19 |
| host prepare (the build) | 9.80 | 10.80 | 16.66 | 40.62 | 71.58 |
| FSI session create | 1.54 | 1.57 | 2.79 | 6.22 | 4.60 |
| **ready (create to Ready)** | **14.4** | **15.8** | **24.3** | **56.9** | **83.4** |

What the numbers say:

- **Speed of one thread decides nearly everything.** The Phenom is 2.7 times slower per thread by PassMark (5.5 times
  by the calibration) and 3 to 5 times slower in most stages (8 times for the daemon starting). Cores barely matter: 16 threads against 4 is 0.6 s warm and 1.4 s cold. They matter
  when work is parallel (the host build: 9.8 s on 16 threads, 15.7 s on 2) and when there are fewer than one
  (half a core never reached Ready under 0.6.875; every one of its 8 runs failed).
- **Memory is not the limit it looks like.** 8, 4 and 2 GB caps behaved like 32 GB (ready 5.7 to 6.1 s warm, 15.0 to
  15.7 s cold), and a worker, FSI host and daemon together peak at about 720 MB (173, 317 and 231 MB). Below 2 GB see
  "Where memory runs out" below.
- **The disk is the suspected rest.** The Phenom's daemon takes 4.8 s to answer /health against 0.6 s, 8 times for a
  2.7 times slower thread. A cold page cache and a spinning disk are the likely difference, and I could not
  emulate either, so the disk rule (a spinning disk counts as `Constrained`) rests on one machine.
- **A stop costs 5.1 to 5.7 s everywhere.** The worker never answers the shutdown request inside its 2 s and
  never leaves inside the 3 s after, so the full 5 s is always spent. That is a separate bug, and it is why those
  waits are fixed and do not scale (they are bounds before a kill, and scaling them would make every stop on a slow
  machine take a minute).
- **First start probe.** Taking the probe costs 0.2 to 0.27 s once per data directory, which shows in "daemon up" between the 0.6.875 rows and this change's on the same machine.

#### Which waits bind where

The default (a `Fast` machine) waits, against the longest stretch each one has to cover:

| wait (default) | what it times | binds on |
|:---|:---|:---|
| warm-up inactivity, 30 s | the silent stretch from spawning a worker to its first progress report | cold start on the Phenom (52.5 s) and on half a core (80.2 s). It killed the worker and started it again with the same 30 s, to the end of the attempts. Never binds on the 5800XT (12.6 s) |
| rebuild ready, 30 s | a hard reset with rebuild, to Ready | the Phenom's rebuild reset reaches 30.1 s, at the edge, and the half core's 28 s. Not hit on the 5800XT (7 s) |
| everything else I measured | | nothing, on any machine here. The host start, the SDK query, the HTTP read, a stop and the supervisor's wedge detection all finished well inside their limits |

Every wait for the machine scales together, so a tier covers the ones I did not see bind as well.

#### What it did on the 2009 machine

The same Phenom II X4 B50, shared with whatever else was running on it (its load average was 4 to 5 during all of
these), one run at a time at `nice 19`, in its own data directory on its own port. Seconds, to Ready.

| build and setting | cold start | warm start | what happened |
|:---|---:|---:|:---|
| 0.6.875, shipped defaults | never | never | 233 s (cold) and 237 s (warm), then `Worker process exited with code 137 (abandoned after max retries)`, both runs |
| this change, defaults | 65.3 (63.1 to 67.5, n=2) | 17.1 (17.1 to 18.0, n=3) | Ready every time, one FSI creation each, no retry. The log's first line says `Machine tier Constrained, from a probe just taken (4.0 cores, 7.7 GB memory (4.2 GB available), calibration 154 ms, 7.0 times the reference, a spinning disk)`, with no warning that a wait was read early |
| this change, defaults, one more cold start | 82.4 (n=1) | | the same, a minute and a half with the machine busy |
| this change, forced `SAGEFS_MACHINE_TIER=Fast` | 176.4 (n=1) | | the old 30 s allowance, so it was silent 30 s and was started again with 60 s, silent 60 s and started again with 120 s, and the third attempt finished. Escalation alone rescued a start the tier would have covered |
| this change, forced `Fast` and `SAGEFS_WARMUP_INACTIVITY_SECONDS=2` | fails after 42 s | | 2 s, 4 s, 8 s, 16 s, then the message under "What you will see" with these numbers in it |

The first build that ran there had the tier settled too late (the daemon's own log said so, and the fast desktop
could not show it, because `Fast` was right there either way). It ran with `Fast` waits on a machine it knew was
`Constrained`, and escalation alone took it to Ready in 75.9 s cold (58.2 to 93.5, n=2) and 17.3 s warm (n=3). That is
what the retry is for, and also why the tier is settled before a wait is read now (`DataDirChoice.fs`).

The profile that run left in the data directory, which the next daemon starts from:

```
"Tier": "Constrained", "Storage": "Rotational", "Calibration": 154.5 ms
"Stages": [ WorkerReady: smoothed 82.1 s, deviation 41.0 s, samples 1 ;  WorkerPort: smoothed 80.9 s, ... ]
```

#### What it does on a fast machine

Nothing it did not do before, within what a shared machine can show. The 5800XT was not quiet while this was
measured (other test runs on it, load average 6 to 14), so the old build (0.6.875) and this one were run
interleaved, old, new, old, new, each with 3 warm and 2 cold runs, shipped defaults, and the profile carried from run
to run. Medians, seconds, to Ready:

| build | cold, first pair | cold, second pair | warm, first pair | warm, second pair |
|:---|---:|---:|---:|---:|
| 0.6.875 | 16.9 | 15.5 | 7.1 | 5.6 |
| this change | 18.7 | 16.3 | 5.8 | 5.5 |

No failures and one FSI creation each. The tier was `Fast` and the waits the ones it had. The only cost is the probe the
first time a data directory is used (about 0.1 to 0.27 s, in "daemon up"); after that the profile is read.

#### Where memory runs out

Capped on the daemon and everything it starts (`MemoryMax`, swap off, 0.6.875, shipped defaults, 5800XT, 4 cores):

| cap | cold ready | warm ready | rebuild reset |
|:---|---:|---:|:---|
| 8, 4, 2 GB | 15.0 to 15.7 s | 5.7 to 6.1 s | 7.9 to 10.0 s |
| 1.5 GB | 14.2 s (n=1) | 6.3 s (n=2) | 8.3 s |
| 1 GB | 15.5 s | 5.8 s (n=2) | never returned: everything was killed |
| 768 MB | did not finish in 300 s | not run | not run |

Down to 1.5 GB nothing is slower. At 1 GB a start finishes and a plain hard reset too, but a hard reset with a rebuild
never came back: the build beside a live worker and FSI host went over the cap and the kernel killed all of it.
No wait helps with that, so the tier table puts the lines at 2 GB (`Fast`), 1.5 GB (`Standard`) and 1 GB
(`Constrained`), and a machine under 1 GB is `Minimal`.

### What to do

If a first start takes long and then works, that is the host build: it happens once per SDK and SageFs version, and
the second start on the same machine does not pay it. If it fails, read the message, then:

- Force a slower tier for this machine: `export SAGEFS_MACHINE_TIER=Constrained`, then restart the daemon.
- Or raise the one wait the message names (it names the variable).
- Delete `machine-profile.json` in the data directory to make SageFs probe again, for instance after a hardware
  change.
- To get your own numbers, run `dotnet fsi scripts/machine-bench.fsx --sagefs <dir that holds SageFs.dll>`. It runs
  a throwaway daemon in its own directory on its own ports, writes a small two project fixture, and prints the
  machine profile and one row per stage. `scripts/machine-bench-tiers.fsx` wraps it in systemd scopes to emulate
  smaller machines on one box. If you send me a table from a machine that is slower than anything in the tables
  above, I will fit the tiers to it.

### Where the numbers come from

- The estimator that learns how long a start takes on a machine is TCP's retransmission timer: a smoothed mean plus
  four deviations, with the first deviation half the first observation. RFC 6298,
  [rfc-editor.org/rfc/rfc6298](https://www.rfc-editor.org/rfc/rfc6298).
- Why the first start is the slow one and a later one is not: .NET compiles code to machine code as it first runs it,
  and ReadyToRun images and tiered compilation trade start-up time for steady speed.
  [learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation).
- Single thread scores that put the two machines in order:
  [Ryzen 7 5800XT, 3533](https://www.cpubenchmark.net/cpu.php?cpu=AMD+Ryzen+7+5800XT),
  [Phenom II X4 B50, 1289](https://www.cpubenchmark.net/cpu.php?cpu=AMD+Phenom+II+X4+B50).
- Other tools: Gradle's daemon documentation
  ([docs.gradle.org](https://docs.gradle.org/current/userguide/gradle_daemon.html)) and OmniSharp's configuration
  ([OmniSharp wiki](https://github.com/OmniSharp/omnisharp-roslyn/wiki/Configuration-Options), a fixed 30 s
  `documentAnalysisTimeoutMs`) document fixed limits and no adaptation to the machine, which is the gap this closes.

---

## Environment Variable Overrides

The waits and intervals can be overridden through environment variables. Set
them before starting SageFs (or in your shell profile). Every one, with its
default and what it is for, is on the
[configuration page](configuration.md), which is generated from the code. The
ones people usually reach for:

| Variable | Default | What it is for |
|:---------|:--------|:------------|
| `SAGEFS_MACHINE_TIER` | worked out | Forces the machine tier (`Fast`, `Standard`, `Constrained`, `Minimal`), which scales every wait for the machine. See [Slow machines](#slow-machines) |
| `SAGEFS_WARMUP_INACTIVITY_SECONDS` | `30` | How long a starting worker may go without progress before it is declared stuck. 30 is the `Fast` value; a slower tier multiplies it, and a value you set is used as written |
| `SAGEFS_WARMUP_MAX_MINUTES` | `10` | The hard ceiling on warmup (the `Fast` value) |
| `SAGEFS_PER_TEST_TIMEOUT_SECONDS` | `5` | The starting per-test timeout for live testing (the `Fast` value) |
| `SAGEFS_BUILD_TIMEOUT_MINUTES` | `10` | How long a `dotnet build` run by a session start or rebuild may take (the `Fast` value) |
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
governed by two limits: `SAGEFS_WARMUP_INACTIVITY_SECONDS` (30 on a `Fast`
machine, scaled on a slower one) is how long the worker can go with no progress
before it's declared stuck, and a worker that goes silent is started again with
more patience before the session is given up on ([Slow machines](#slow-machines));
`SAGEFS_WARMUP_MAX_MINUTES` (10 on a `Fast` machine) is the hard ceiling
regardless of progress. A session that's genuinely still working (a big repo resolving many
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
