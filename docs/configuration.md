# Configuration: environment variables

Apart from the dashboard's settings panel and a repository's
`.SageFs/config.fsx`, SageFs is tuned through environment variables. Almost all
of them are waits and intervals that live in one place,
[`SageFs.Core/Timeouts.fs`](../SageFs.Core/Timeouts.fs). This page lists every
one of them, plus the handful that are not durations.

Set a variable in the environment that starts the daemon (your shell profile, a
service unit, `export` before running `sagefs`). The daemon reads it when it
starts, so a change needs a restart. Workers the daemon spawns inherit the
daemon's environment, apart from a short list of MSBuild variables SageFs strips
([`ProcessEnvironment.fs`](../SageFs.Core/ProcessEnvironment.fs)).

The duration variables all work the same way. Unset or empty gives the default.
A value that is not a number, or is zero or negative, is ignored and the default
is used, with no warning. That is the whole rule: there is no 1 second to 10
minute clamp on these. (`ValidTimeout`'s 1s to 10min range applies to the
live-testing timeouts you set at runtime through the MCP tool, not to these
variables.) The local data retention settings follow the same rule, and the two
counts (rows and versions) must be positive whole numbers.

I made these settable because a test or a slow CI box needed one, not because I
expect anyone to live in this file. If a default is wrong for you in a way that
isn't a slow machine, tell me, because then the default is the bug.

## Variables that are not durations

| Variable | Default | What it does |
|:---|:---|:---|
| `SAGEFS_DATA_DIR` | your user data dir (`~/.SageFs`) | Moves everything the daemon persists (manifest, test cache, themes, friction store) into this directory, and the daemon's own log and the per-session worker logs with it. Unset, empty or blank means not isolated. It exists for tests and throwaway daemons that must not touch your real state ([`DaemonState.fs`](../SageFs.Core/DaemonState.fs)). |
| `SAGEFS_MCP_PORT` | `37749` | The MCP server port. The dashboard listens on the next port up. `--mcp-port` on the command line wins over this ([`SageFsConfig.fs`](../SageFs.Core/SageFsConfig.fs)). |
| `SAGEFS_BIND_HOST` | `localhost` | `localhost`, `127.0.0.1` or `::1`. Anything else stops the daemon at startup and `sagefs check` reports it, because the ports run F# for whoever reaches them and have no authentication. See [Docker / Remote Containers](TROUBLESHOOTING.md#docker--remote-containers). |
| `SAGEFS_HOT_RELOAD` | unset | The daemon sets this to `1` for a worker whose workflow is hot reload ([`Args.fs`](../SageFs.Core/Args.fs)). A worker that finds `all` in it starts with every project file watched for hot reload; any other value leaves hot reload off until you turn it on from the dashboard ([`WorkerMain.fs`](../SageFs.Host/WorkerMain.fs)). |
| `SAGEFS_DEVRELOAD` | on | `0` or `false` turns the browser dev-reload injection off, and `enable_hot_reload` then answers `disabledByEnvVar` ([`DevReloadInjector.fs`](../SageFs.Host/DevReloadInjector.fs), [`McpTools.fs`](../SageFs/McpTools.fs)). Any other value leaves it on. |
| `SAGEFS_UPDATE_CHECK_DISABLED` | unset | Any non-empty value other than `0` or `false` stops the daemon asking NuGet whether a newer SageFs exists. A daemon running from a local build never asks anyway ([`UpdateCheckService.fs`](../SageFs/UpdateCheckService.fs)). |
| `SAGEFS_METADATA_DELTA` | on | `on`, `1` or `true` makes a hot reload worker patch a `run_app` app's saves into the running process by metadata delta; `off`, `0` or `false` restarts the app on a save, as it did before. The daemon reads it when it starts a worker and gives that worker `DOTNET_MODIFIABLE_ASSEMBLIES=debug` and the choice, spelled out. See [how it works](how-hot-reload-works.md#a-run_app-app-the-same-save-as-a-metadata-delta) ([`MetadataDeltaFlag.fs`](../SageFs.Core/Features/MetadataDelta/MetadataDeltaFlag.fs), [`Args.fs`](../SageFs.Core/Args.fs)). |
| `SAGEFS_EDITOR` | `EDITOR`, then `code` | Picks the link scheme for clickable `file:line:column` links: a name containing `rider` or `jetbrains` gives a JetBrains link, `cursor` gives a Cursor link, anything else a VS Code link ([`DevReloadMiddleware.fs`](../SageFs.Host/DevReloadMiddleware.fs)). |
| `SAGEFS_FORWARD_PREFIXES` | unset | A `;` or `:` separated list of name prefixes. Environment variables starting with one of them are passed into every process SageFs spawns, so a profiler or tracer can reach a worker it did not launch. SageFs does not read or check the values ([`ProcessEnvironment.fs`](../SageFs.Core/ProcessEnvironment.fs)). |
| `SAGEFS_HOST_CACHE_DIR` | `<data dir>/hosts` | Where built FSI hosts are cached. A host is addressed by its content, so daemons can share one cache, and a test harness whose daemons each get a fresh data dir uses this to build the host once ([`IsolatedFsiSession.fs`](../SageFs.Core/IsolatedFsiSession.fs)). |
| `SAGEFS_SESSION_MANAGER_QUEUE_CAPACITY` | `256` | How many commands may be waiting for the session manager before create, stop and restart are refused with `SupervisorBusy` instead of queueing. Sized so it never engages in normal use ([`SageFsConfig.fs`](../SageFs.Core/SageFsConfig.fs)). |
| `SAGEFS_FINAL_GATE` | unset | Not read by the daemon. It is how you tell the `sagefs-repl-guard` agent hook that a `dotnet build` or `dotnet test` is the final gate and may run. See [Using SageFs with AI agents](agents.md) ([`ReplGuard.fs`](../tools/agent-hooks/ReplGuard.fs)). |
| `SAGEFS_WORKER_STARTUP_TIMEOUT_MS` | `120000` | Does nothing. It is still parsed so an old value keeps parsing, but nothing waits on it any more: `SAGEFS_WARMUP_INACTIVITY_SECONDS` and `SAGEFS_WARMUP_MAX_MINUTES` replaced it ([`SageFsConfig.fs`](../SageFs.Core/SageFsConfig.fs)). |

`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL` (`grpc` by default)
and `OTEL_SERVICE_NAME` (`sagefs` by default) are the standard OpenTelemetry
variables, and setting the endpoint turns telemetry export on.

### Set by SageFs, not by you

SageFs writes these into the processes it starts. Setting one yourself in the
daemon's environment is not something I've tested, so leave them alone.

| Variable | Who sets it | What it carries |
|:---|:---|:---|
| `SAGEFS_SESSION_PROJECTS` | daemon, for each worker | The project or solution paths the session was created for, `;` separated. |
| `SAGEFS_BARE_SESSION` | daemon, for each worker | `1` for a session with no project. |
| `SAGEFS_NO_WATCH` | daemon, for each worker | `1` when the worker should not start a file watcher. |
| `SAGEFS_AUTO_OPEN_NAMESPACES` | daemon, for each worker | `0` when the session should not auto-open namespaces. |
| `SAGEFS_DAEMON_PID`, `SAGEFS_DAEMON_START_TICKS` | daemon, for each worker | Who owns the worker. The worker watches that process and exits when it is gone, which is how a hard-killed daemon does not leave orphans. |
| `SAGEFS_SUPERVISED`, `SAGEFS_RESTART_COUNT` | the watchdog runner | Whether the daemon runs under the auto-restart supervisor, and how many times it has been restarted. |
| `SAGEFS_METADATA_DELTA`, `DOTNET_MODIFIABLE_ASSEMBLIES` | daemon, for a hot reload worker | The first says whether the worker takes `run_app` saves as metadata deltas. When it is `on` the second is `debug`, which the runtime only reads at process start and which lets it edit the app's assembly. Nothing the worker spawns inherits the second, so the save's own build and the FSI host don't pay for it. |
| `SAGEFS_PROJECT_OUTPUT` | daemon, for the isolated host | The primary project's own build output directory. Code in a session can read it to find files next to the project's assembly. |

(Sources: [`Args.fs`](../SageFs.Core/Args.fs), [`WatchdogRunner.fs`](../SageFs/WatchdogRunner.fs), [`IsolatedFsiSession.fs`](../SageFs.Core/IsolatedFsiSession.fs).)

## Timeouts and intervals

Everything below is generated from `Timeouts.fs` by
[`scripts/gen-configuration-doc.fsx`](../scripts/gen-configuration-doc.fsx), so a
default here is the default in the code. Each description is the constant's own
doc comment, with its line breaks joined and em dashes written as commas. Four
constants have no doc comment of their own, and their rows say where the words
come from. The Source column links to the line, pinned to a commit.
`SageFs.Tests/EnvVarDocTests.fs` fails if a variable is in `Timeouts.fs` and not
on this page, or the other way round.

<!-- BEGIN GENERATED: timeouts (scripts/gen-configuration-doc.fsx) -->

### Build & Warmup

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_WARMUP_MAX_MINUTES` | 10 | minutes | The hard ceiling on a worker's warmup, which neither progress nor silence can argue past. The constant has no doc comment; these words are from the one on `awaitWorkerPort` in `SessionManager.fs`. | [L46](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L46) |
| `SAGEFS_WARMUP_INACTIVITY_SECONDS` | 30 | seconds | How long a starting worker may go without reporting progress before it is declared stuck. The clock resets on every `WARMUP_PROGRESS=` line, so silence, not slowness, is what trips it. The constant has no doc comment; these words are from the one on `awaitWorkerPort` in `SessionManager.fs`. | [L47](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L47) |
| `SAGEFS_FSI_HOST_STARTUP_SECONDS` | 120 | seconds | How long a started FSI host process has to finish its startup handshake (the one-shot config host in ConfigHost and the isolated session host in IsolatedFsiSession) before the start is given up as failed. No recorded reason for 120s. | [L54](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L54) |
| `SAGEFS_DOTNET_SDK_QUERY_SECONDS` | 30 | seconds | How long `dotnet --version` and `dotnet --list-sdks` may each take while FsiHostBuild picks the SDK to build the FSI host with. They answer from disk, so a wait this long means a hung muxer. No recorded reason for 30s. | [L67](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L67) |
| `SAGEFS_AMBIENT_SDK_PROBE_SECONDS` | 15 | seconds | How long ProjectLoading's `dotnet --version` pre-check may take before it is killed. A hung probe must never hang a warmup, so this is shorter than `dotnetSdkQuery`; a failure just means the normal in-process load runs as before. No recorded reason for 15s. | [L72](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L72) |
| `SAGEFS_TARGET_FRAMEWORK_EVALUATION_SECONDS` | 60 | seconds | How long `dotnet msbuild -getProperty:TargetFramework` may take when a project file does not name its target framework (a Directory.Build.props sets it), before the daemon gives up asking and lets the worker's own evaluation decide. It loads MSBuild once, which takes seconds on a cold machine and nothing more. No recorded reason for 60s. | [L77](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L77) |
| `SAGEFS_GC_DUMP_CAPTURE_SECONDS` | 120 | seconds | How long one `dotnet-gcdump collect` may run. On a process this large it can take tens of seconds; this is generous without being an unbounded hang on a machine that is already struggling. No recorded reason for 120s. | [L81](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L81) |
| `SAGEFS_STOP_GRACEFUL_SECONDS` | 30 | seconds | How long `sagefs stop` waits for the daemon's process to exit after the shutdown request, which gives a normal graceful shutdown (manifest save, stopping workers) room before the fallback kill. | [L88](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L88) |

### HTTP / Worker Communication

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_WORKER_HTTP_READ_SECONDS` | 30 | seconds | The read timeout the daemon gives the worker's streaming test proxy (`DaemonMode.fs`, `HttpWorkerClient.streamingTestProxyWithCoverage`). The constant has no doc comment; the one on `daemonSessionsProbe` says that probe is shorter than this. | [L95](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L95) |
| `SAGEFS_WORKER_HTTP_REQUEST_MINUTES` | 10 | minutes | Bounded request timeout for worker HTTP calls (eval/check/typecheck/reset/etc). An eval that hangs in the worker must not hang the caller forever; 10 minutes matches the ValidTimeout max and the build cap, so legitimately long evals still complete while a wedged worker eventually surfaces as a timeout error instead of an infinite hang. | [L101](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L101) |
| `SAGEFS_DAEMON_SESSIONS_PROBE_SECONDS` | 3 | seconds | Hard timeout on a short GET to the local daemon's `/api/sessions` from the CLI (`sagefs status` and the environment check). The daemon is on this machine, so a probe that takes this long means it is wedged, and the caller reports that instead of waiting. Shorter than `workerHttpRead`. | [L118](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L118) |
| `SAGEFS_FRICTION_POST_SECONDS` | 15 | seconds | Request timeout for posting a friction report to the configured endpoint. The post is user-initiated and the dashboard shows the failure, so this is a network budget with no recorded reason for 15s. | [L122](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L122) |

### Update / staleness check (issue #136)

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_UPDATE_CHECK_TIMEOUT_SECONDS` | 3 | seconds | Hard timeout on the NuGet flat-container GET. Any failure (including a timeout) must be swallowed by the caller into `CheckFailed`, never block daemon startup, and never slow a session. | [L128](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L128) |
| `SAGEFS_UPDATE_CHECK_INTERVAL_MINUTES` | 360 | minutes | How often the daemon re-asks NuGet whether a newer version exists. Checked once on daemon start (if the on-disk cache is older than this) and re-checked on this cadence for the lifetime of a long-running daemon, the whole point being to catch a daemon that has been up long enough to go stale without anyone restarting it. | [L134](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L134) |

### Live Testing (configurable at runtime via MCP, thread-safe)

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_PER_TEST_TIMEOUT_SECONDS` | 5 | seconds | The per-test timeout when neither the environment nor a setting says otherwise. The constant has no doc comment; these words are from the one on `perTestTimeoutFallback`, whose default is 5 seconds. The section is headed "configurable at runtime via MCP", so this is the starting value. | [L143](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L143) |

### Live testing as you type

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_REBUILD_READY_SECONDS` | 30 | seconds | How long after a rebuild restart the live-testing pipeline waits for the session manager to say the session is Ready (which is also when its streaming test proxy exists) before it reports the rebuild as failed. It is the one timer in that wait. No recorded reason for 30s. | [L227](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L227) |

### Process Management

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_BUILD_TIMEOUT_MINUTES` | 10 | minutes | How long a `dotnet build` run by a session start or rebuild may take before SessionBuild kills it and the session faults with `BuildFailure.TimedOut`. Large repos need minutes; 10 matches the ValidTimeout max. | [L233](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L233) |
| `SAGEFS_HOST_BUILD_MINUTES` | 5 | minutes | How long the `dotnet build` of the FSI host (a cold build, once per host version and SDK) may run before FsiHostBuild gives up. No recorded reason for 5 minutes. | [L257](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L257) |
| `SAGEFS_GIT_QUICK_SECONDS` | 30 | seconds | Plumbing commands (rev-parse, diff, update-ref, worktree remove). | [L274](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L274) |
| `SAGEFS_GIT_REBASE_MINUTES` | 5 | minutes | A rebase, which may run hooks and touch many commits. | [L276](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L276) |
| `SAGEFS_GIT_WORKTREE_ADD_MINUTES` | 2 | minutes | `git worktree add`, which checks out a whole tree. | [L278](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L278) |

### Cohort landing gate

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_COHORT_SETTLE_SECONDS` | 120 | seconds | How long the landing gate waits for the integration session to settle to a trustworthy state after a rebase before giving up (a terminal/dead session fails fast via SessionTrust.settleDecision; this bounds only a genuinely warming session). Env-overridable. | [L285](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L285) |

### Hot reload

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_PATCH_CONFIRM_SECONDS` | 10 | seconds | How long a patched function may go without its new body running before the save reports it as never entered. A page that refreshes on the pending report usually runs the function within a second; a function nothing calls (an idle app, or a caller that kept its own copy of the old body) reads the same, so the report says "unconfirmed, exercise it". | [L390](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L390) |
| `SAGEFS_HOT_RELOAD_COMPILE_QUEUE_SECONDS` | 60 | seconds | How long one save waits for the previous save's compile before it gives up and reports that it did. The wait used to be unbounded, which let one wedged eval silently disable hot reload for every other file; bounded, a stuck compiler shows up. No recorded reason for 60s. | [L423](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L423) |
| `SAGEFS_HOT_RELOAD_COMPILE_BUDGET_MINUTES` | 5 | minutes | Ceiling on one save's re-evaluation. The eval is posted with a token nothing else cancels, so without a deadline one wedged submission owns the compiler forever; with one the compiler is always handed back and the next save goes through. No recorded reason for 5 minutes. | [L428](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L428) |

### Running an app (run_app)

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_APP_HOST_APPEAR_SECONDS` | 10 | seconds | How long an app's entry point may run before it builds a host. If it has not by then, the app is treated as a console app with no server. The wait ends the moment a host appears, so a generous value costs a web app nothing; it is the delay a console app pays before it is recognised as one. A cold ASP.NET host build on a loaded machine takes seconds, which is why this is not sub-second. | [L440](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L440) |
| `SAGEFS_APP_HOST_START_SECONDS` | 90 | seconds | How long a host that has been built may take to start listening. | [L442](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L442) |

### Watchdog / Supervision

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_WORKER_PROBE_INTERVAL_SECONDS` | 5 | seconds | How often an established (Ready) worker is probed for health. | [L496](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L496) |
| `SAGEFS_WORKER_PROBE_TIMEOUT_SECONDS` | 3 | seconds | How long one health probe round-trip gets to answer before it counts as missed. Three misses in a row restart the worker (`WorkerHealthProbe`). | [L499](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L499) |
| `SAGEFS_WORKER_READY_POLL_SECONDS` | 1 | seconds | How often the watchdog asks a starting worker for its status until it is Ready. A poll: the worker reports Ready only by answering this question. | [L502](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L502) |
| `SAGEFS_OWNER_POLL_SECONDS` | 2 | seconds | How often a worker checks that the process that owns it is still alive, so a hard-killed owner cannot orphan it. A poll; a process exit has no portable event to wait on from a non-parent. | [L506](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L506) |
| `SAGEFS_AGENT_CLEANUP_SECONDS` | 60 | seconds | How often the daemon evicts agents that have gone silent from the activity tracker. A timer, one run after the last finishes. | [L509](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L509) |
| `SAGEFS_COHORT_REAPER_SECONDS` | 60 | seconds | How often the cohort reaper renews the lease of each active member and ticks the cohort so silent members depart. No recorded reason for 60s. | [L512](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L512) |
| `SAGEFS_ORPHAN_SWEEP_MINUTES` | 15 | minutes | How often the daemon sweeps the OS temp directory for orphaned `sagefs-host-adopt-*` and `sagefs-test` directories left by processes that died. Generous, because the walk touches the temp directory and nearly every sweep finds nothing. | [L517](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L517) |
| `SAGEFS_WATCHER_SYNC_SECONDS` | 5 | seconds | How often the daemon makes sure every session has a file watcher and sweeps stale ones, in case a state change event was missed. A poll that the state change events already cover. No recorded reason for 5s. | [L521](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L521) |

### Dashboard / UI

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_DASHBOARD_HEARTBEAT_SECONDS` | 5 | seconds | Server SSE heartbeat cadence: the stream loop patches a heartbeat signal at least this often (even on no-change ticks) so the client can prove liveness. | [L536](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L536) |
| `SAGEFS_DASHBOARD_STALE_AFTER_SECONDS` | 15 | seconds | Client staleness budget: if no heartbeat arrives within this window the dashboard flips Signals.Connected=false and shows the disconnect banner. | [L539](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L539) |

### Daemon / Server

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_STOP_SESSION_TIMEOUT_SECONDS` | 20 | seconds | Defense-in-depth bound on a `StopSession` mailbox round-trip (`DaemonMode.createSessionOps.StopSession`). `stopWorker` itself is already bounded (workerShutdownDelay + a WaitForExit + a Kill fallback), and the SessionManager mailbox loop is supervised (`superviseStep`/`supervise` in SessionManager.fs) so an unhandled exception replies with an error instead of dropping the caller. This timeout is the last line: if the reply STILL never arrives, a future regression, not a known path today, `stop_session` fails with a stated, actionable error at 20s instead of hanging until the MCP client's own external timeout (300s in the three fcs-onboarding-trial reports, 2026-09-22) makes the caller guess why. "A stop always completes" is an invariant this bound exists to guarantee even if everything upstream of it were somehow wrong. | [L626](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L626) |
| `SAGEFS_SUPERVISOR_WEDGE_SECONDS` | 30 | seconds | How long one command may hold the session manager's loop before the supervisor calls it wedged. Above the slowest honest handler (a parallel stop of every session waits seconds) and below `stopSessionMailboxTimeout` doubled, so a wedge is named before callers give up on it. | [L631](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L631) |

### Persistence

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_MANIFEST_SAVE_INTERVAL_SECONDS` | 60 | seconds | Cadence of the daemon's periodic manifest save (`periodicManifestSave`, `DaemonMode.fs`, `cacheSaveTimer`): both the timer's initial due time and every reschedule use this value. Default stays 60s in production; tests that need the crash/resume durability boundary to arrive sooner (rather than waiting on a fixed 60s wall-clock tick) can shrink it via the env var. Env-overridable. | [L646](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L646) |

### Session Lifecycle

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_IDLE_SESSION_THRESHOLD_MINUTES` | 10 | minutes | How long a Ready session can go untouched before the dashboard calls it idle instead of running. Only Ready is time-gated this way, Evaluating and Building are never idle regardless of age (see SessionDisplay.displayStatus). Env-overridable so an integration test can prove the boundary without waiting ten real minutes for it. | [L655](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L655) |
| `SAGEFS_WARMUP_READY_POLL_SECONDS` | 120 | seconds | Maximum time to poll a worker waiting for Ready status after spawn/restart. If exceeded, the session is faulted to prevent infinite WarmingUp states. | [L658](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L658) |

### Waiting for a worker's test proxy (WorkerProxyWait)

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_WORKER_PROXY_WAIT_SECONDS` | 15 | seconds | The ceiling on how long a live-testing run waits for a worker to register its test proxy before it dispatches with none (every test then reports NotRun). Generous on purpose: the wait is only paid when the worker is absent, and cutting it short strands a real run: an earlier 750ms schedule failed every run under the load of the integration tier. | [L666](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L666) |

### Test harness / integration

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_TEST_DAEMON_READY_SECONDS` | 120 | seconds | Deadline for a spawned test daemon to reach a readable Ready state. Replaces the bare 120_000ms literals in the daemon integration tests. | [L680](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L680) |
| `SAGEFS_TEST_WORKER_RESTART_SECONDS` | 60 | seconds | Deadline for a killed worker to be restarted on a new pid in the crash/restart integration smoke. Replaces the bare 60_000L literal. | [L683](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L683) |
| `SAGEFS_TEST_BROWSER_WARMUP_SECONDS` | 300 | seconds | Warmup deadline for a browser-journey dashboard session (cold Chromium + Release sample). Replaces the 300.0s literals in DashboardBrowserRunner. | [L686](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L686) |
| `SAGEFS_TEST_WEBAPP_BUILD_SECONDS` | 180 | seconds | Build deadline for the web-app hot-reload verification sample. Replaces the 180000ms literal. | [L689](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L689) |
| `SAGEFS_TEST_WEBAPP_PORT_SECONDS` | 120 | seconds | Deadline for the hot-reload web-app sample to bind its port and report ready. Replaces the 120.0s literal. | [L692](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L692) |

### Local data retention

| Variable | Default | Unit | What it is for | Source |
|:---|---:|:---|:---|:---|
| `SAGEFS_FRICTION_MAX_AGE_DAYS` | 30 | days | Friction rows older than this are rolled into the per-version aggregate and deleted. | [L725](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L725) |
| `SAGEFS_FRICTION_MAX_ROWS` | 5000 | rows | Raw friction rows kept per table. The newest win. | [L727](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L727) |
| `SAGEFS_FRICTION_MAX_AGGREGATE_VERSIONS` | 20 | versions | How many SageFs versions keep their small aggregate (a count per kind). | [L729](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L729) |
| `SAGEFS_COHORT_LEDGER_RETENTION_DAYS` | 7 | days | A finished cohort's ledger rows are cleared once its last entry is older than this. An active cohort is never touched. | [L732](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L732) |
| `SAGEFS_HOST_CACHE_MAX_AGE_DAYS` | 30 | days | A built FSI host that no session has used for this long is pruned by the daemon's housekeeping. A host rebuilds in a few minutes, so a month of disuse says that SDK and source combination is gone from daily use, and 3 GB of 55 hosts is what no pruning cost one machine. | [L754](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L754) |
| `SAGEFS_GATE_RETENTION_DAYS` | 14 | days | A gate checkout or tier clone whose invoking repo is still there is kept this long after its last gate, so the next gate on the same repo stays warm. Past it the checkout is recreated in a few minutes. | [L757](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L757) |
| `SAGEFS_TEMP_RUN_MAX_AGE_DAYS` | 2 | days | A test runner's temp data dir (and any other `sagefs-*` temp entry nobody records an owner for) is reclaimable this long after it was last touched. A run takes minutes; two days leaves room for a debugging session that wants the leftovers of the run that just failed. | [L761](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L761) |
| `SAGEFS_WORKER_LOG_MAX_AGE_DAYS` | 14 | days | A worker's log is kept this long after its last write. Logs are what a person reads after a crash, and a crash is looked at within days. | [L764](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L764) |
| `SAGEFS_GATE_PASS_RECORDS_KEPT` | 10 | versions | How many of the local gate's pass records are kept, newest first. A record holds a commit's release bundle (about 270 MB each), and the pre-push hook only ever looks up the commit being pushed, so ten covers a day of shipping; 75 of them were 20 GB. | [L768](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L768) |
| `SAGEFS_GATE_LOGS_KEPT` | 20 | versions | How many of the gate's logs are kept, newest first. They are what a person reads after a red gate, and a red gate is read within the hour; twenty is a few days of runs. | [L771](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L771) |
| `SAGEFS_DATA_PRUNE_INTERVAL_MINUTES` | 60 | minutes | How often a running daemon prunes friction again (it also prunes on start). | [L734](https://github.com/WillEhrendreich/SageFs/blob/0cd13277a532cd310f4c69b842d2caaec8af3900/SageFs.Core/Timeouts.fs#L734) |

<!-- END GENERATED: timeouts -->

## When you would change one

Most of the comments above give a reason in the sentence itself, and I haven't
added reasons they don't give. The cases where there is one:

A large repository can need more than the defaults to get a session up. A
session that is still making progress keeps its inactivity clock reset by that
progress, so a big repo resolving many projects mostly needs
`SAGEFS_WARMUP_MAX_MINUTES` and `SAGEFS_BUILD_TIMEOUT_MINUTES`. A session that
has gone quiet is faulted after `SAGEFS_WARMUP_INACTIVITY_SECONDS`, and that
message names the variable; raise it if your NuGet restores are slow. The
[troubleshooting page](TROUBLESHOOTING.md#warmup-progress-phases) walks through
what each phase is doing.

A slow CI machine is the other one. The troubleshooting page's example raises
the warmup ceiling, the build timeout and the per-test timeout together, and
those three are the ones I'd reach for first.

`SAGEFS_MANIFEST_SAVE_INTERVAL_SECONDS` and
`SAGEFS_IDLE_SESSION_THRESHOLD_MINUTES` are there so a test can reach a boundary
(the save a crash resumes from, the point where a Ready session reads as idle)
without waiting out the real default. The `SAGEFS_TEST_*` ones are only for the
test harness and have no use outside it.
