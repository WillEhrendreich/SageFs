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
([`ProcessEnvironment.fs`](../SageFs.Core/ProcessEnvironment.fs)). The daemon never
writes a setting into its own environment: the values the dashboard saved and the
machine tier it worked out go to each worker as a settings snapshot, one argument
([`SettingsSnapshot.fs`](../SageFs.Core/SettingsSnapshot.fs)).

The duration variables all work the same way. Unset or empty gives the default,
which is the value for a `Fast` machine (see [Machine tier](#machine-tier)):
on a slower machine a wait that is "Scaled by tier: yes" is multiplied by the
tier's factor. A variable you set is used as written and is never scaled.
A value that is not a number, or is zero or negative, is ignored and the default
is used, with no warning. One variable breaks that rule on purpose:
`SAGEFS_ABANDONED_SESSION_HOURS`, where `0` is a real answer and turns the
session reaper off (see [Abandoned sessions](#abandoned-sessions)). That is the whole rule: there is no 1 second to 10
minute clamp on these. (`ValidTimeout`'s 1s to 10min range applies to the
live-testing timeouts you set at runtime through the MCP tool, not to these
variables.) The local data retention settings follow the same rule, and the two
counts (rows and versions) must be positive whole numbers.

I made these settable because a test or a slow CI box needed one, not because I
expect anyone to live in this file. If a default is wrong for you in a way that
isn't a slow machine, tell me, because then the default is the bug.

## Settings you can change from the dashboard

`/dashboard/settings` has a row for each setting below. A row shows the value in use, which layer it came from
(default, global file, repo file, environment variable or command-line flag), the value you saved when that differs,
and a word for when a change applies: `live` takes effect on save, `restart` applies after the daemon restarts,
`read-only` is shown and never edited there.

A saved value is written to `settings.json` in the data directory (`~/.SageFs`, or `SAGEFS_DATA_DIR`). The next
`sagefs` process reads it first thing and keeps it in its settings snapshot, unless you set the variable yourself, so
the variable and the page never disagree and the workers the daemon starts get the same snapshot. A variable you set wins, and
the row says which one: "Saved 25. Ignored while SAGEFS_WARMUP_MAX_MINUTES is set." A flag on the command line wins
over both.

| Row | Variable or flag | Applies |
|:---|:---|:---|
| Warm-up inactivity limit | `SAGEFS_WARMUP_INACTIVITY_SECONDS` | restart |
| Warm-up ceiling | `SAGEFS_WARMUP_MAX_MINUTES` | restart |
| Build timeout | `SAGEFS_BUILD_TIMEOUT_MINUTES` | restart |
| FSI host build timeout | `SAGEFS_HOST_BUILD_MINUTES` | restart |
| Machine tier (`auto` or a tier) | `SAGEFS_MACHINE_TIER` | restart |
| Abandoned agent sessions (`0` is off) | `SAGEFS_ABANDONED_SESSION_HOURS` | restart |
| Stop asking NuGet for updates | `SAGEFS_UPDATE_CHECK_DISABLED` | restart |
| Metadata-delta hot reload | `SAGEFS_METADATA_DELTA` | restart (read when a worker starts) |
| Per-test timeout | `SAGEFS_PER_TEST_TIMEOUT_SECONDS` | live |
| Test-run stall window | none | live |
| MCP port | `SAGEFS_MCP_PORT`, `--mcp-port` | restart |
| Default working directory | none | live |
| Bind host | `SAGEFS_BIND_HOST` | read-only |
| Data directory | `SAGEFS_DATA_DIR` | read-only |
| Skip restoring sessions at start | `--no-resume` | read-only |
| Browser dev-reload | `SAGEFS_DEVRELOAD` | restart (read when a worker starts) |
| Editor for file links (`auto`, `vscode`, `cursor`, `jetbrains`) | `SAGEFS_EDITOR` | restart |
| The other waits you can set, in the "Waits and limits" group | the variables in [Timeouts and intervals](#timeouts-and-intervals) | restart |
| Limits on reply sizes and the compiler's memory | `SAGEFS_LIST_MEMBERS_MAX`, `SAGEFS_REFERENCES_PROJECT_CACHE`, `SAGEFS_LIST_REFERENCES_MAX`, `SAGEFS_REFERENCES_RESIDENT_CEILING_MB` | restart |
| How long local data is kept, and how many of each, in the "Local data" group | the variables in [Local data retention](#local-data-retention) | restart |
| Set by SageFs: the watchdog flag and restart count | `SAGEFS_SUPERVISED`, `SAGEFS_RESTART_COUNT` | read-only |
| Identity policy, member token, operator token | `SAGEFS_IDENTITY_POLICY`, `SAGEFS_MEMBER_TOKEN`, `SAGEFS_OPERATOR_TOKEN` | read-only |
| Starter tool list for a connection with no credential | `SAGEFS_TOOL_LIST` | read-only (read when the daemon starts) |

Every variable `Timeouts.fs` and `SageFsConfig.fs` read has a row, and a test fails when a new one does not. A row
that is read-only says why in its own words: the bind host and the identity policy stay where the daemon starts
because a loopback bind and a token policy are what stand between SageFs, which evaluates F#, and the network, and
changing them from a page the daemon serves points the trust the wrong way. A token's row says only whether it is
set, never its value.

## Variables that are not durations

| Variable | Default | What it does |
|:---|:---|:---|
| `SAGEFS_DATA_DIR` | your user data dir (`~/.SageFs`) | Moves everything the daemon persists (manifest, test cache, themes, friction store) into this directory, and the daemon's own log and the per-session worker logs with it. Unset, empty or blank means not isolated. It exists for tests and throwaway daemons that must not touch your real state ([`DaemonState.fs`](../SageFs.Core/DaemonState.fs)). |
| `SAGEFS_MACHINE_TIER` | worked out | `Fast`, `Standard`, `Constrained` or `Minimal` (any case). Forces the machine tier instead of letting SageFs probe the machine, so every wait for the machine is scaled by that tier's factor. The daemon also passes it to every worker it starts, so they agree; a variable you set yourself wins over the profile and the probe. Unset, SageFs reads the profile in the data directory, or probes. A value that is not a tier is reported in the daemon log and ignored. See [Machine tier](#machine-tier) ([`MachineTier.fs`](../SageFs.Core/MachineTier.fs)). |
| `SAGEFS_MCP_PORT` | `37749` | The MCP server port. The dashboard listens on the next port up. `--mcp-port` on the command line wins over this. The Settings panel writes the same port as `daemon.mcpPort`, which sits between this variable and the default, so whichever set it is what the daemon binds and what its clients look for ([`DaemonPort.fs`](../SageFs.Core/DaemonPort.fs)). |
| `SAGEFS_BIND_HOST` | `localhost` | `localhost`, `127.0.0.1` or `::1`. Anything else stops the daemon at startup and `sagefs check` reports it, because the ports run F# for whoever reaches them and have no authentication. See [Docker / Remote Containers](TROUBLESHOOTING.md#docker--remote-containers). |
| `SAGEFS_IDENTITY_POLICY` | `ConnectionsAllowed` | `ConnectionsAllowed` or `TokenRequired` (any case). What a connection that presents no member token is. `ConnectionsAllowed` leaves it a plain cohort member, as it was before tokens existed. `TokenRequired` lets it read `get_cohort_status` and `get_daemon_status`, lets the conductor act so it can mint, and refuses everything else with a message that says how to get a token; the first `join_cohort` still makes its caller the conductor. A value that is set but is neither fails closed, as `TokenRequired`. See [member tokens](mcp-tools.md#member-tokens-one-identity-per-agent-run) ([`Capability.fs`](../SageFs.Core/Capability.fs), [`Mcp.fs`](../SageFs/Mcp.fs)). |
| `SAGEFS_TOOL_LIST` | `Full` | `Full` or `Starter` (any case). What `tools/list` shows a connection that presents no credential, read once when the daemon starts, so it is the same for every connection. `Full` lists every tool. `Starter` lists the nine a first session needs (`create_project_session`, `get_session_status`, `list_sessions`, `send_fsharp_code`, `check_fsharp_code`, `run_tests`, `cancel_eval`, `hard_reset_fsi_session`, `discover_features`) and nothing else; every other tool is still callable by name, and `discover_features` with `tool` reads its manual. A member token keeps its role's list and the operator credential keeps the full list under either value. A value that is neither is `Full`, so a typo hides no tool ([`ToolList.fs`](../SageFs.Core/ToolList.fs)). |
| `SAGEFS_MEMBER_TOKEN` | unset | Read by `sagefs mcp`, not by the daemon: a member token from `mint_member`, sent as the `X-SageFs-Member-Token` header on every request the bridge makes, so it never appears in a JSON-RPC message ([`McpStdioBridge.fs`](../SageFs/McpStdioBridge.fs)). |
| `SAGEFS_HOT_RELOAD` | unset | `1` or `true` runs the hot-reload flow. `all` is the older spelling of "on, and watch every project file", kept so a setup that used it keeps working. Any other value leaves hot reload off until you turn it on from the dashboard ([`Args.fs`](../SageFs.Core/Args.fs)). |
| `SAGEFS_HOT_RELOAD_ALL_FILES` | unset | `1` or `true` runs the hot-reload flow and watches every project file, not only the session's own. This is the watch-all half of `SAGEFS_HOT_RELOAD`, which used to say `all` and so said two things at once: one variable read two ways meant a value the other reader could not parse, and it warned about it on every start ([`Args.fs`](../SageFs.Core/Args.fs), [`WorkerMain.fs`](../SageFs.Host/WorkerMain.fs)). |
| `SAGEFS_DEVRELOAD` | on | `0` or `false`, in any case, turns the browser dev-reload injection off, and `enable_hot_reload` then answers `disabledByEnvVar`. Any other value leaves it on. One rule reads it for the injector, the tool and the Settings row ([`DevReloadMode.fs`](../SageFs.Core/DevReloadMode.fs)). |
| `SAGEFS_UPDATE_CHECK_DISABLED` | unset | Any non-empty value other than `0` or `false` stops the daemon asking NuGet whether a newer SageFs exists. A daemon running from a local build never asks anyway ([`UpdateCheckService.fs`](../SageFs/UpdateCheckService.fs)). |
| `SAGEFS_METADATA_DELTA` | on | `on`, `1` or `true` makes a hot reload worker patch a `run_app` app's saves into the running process by metadata delta; `off`, `0` or `false` restarts the app on a save, as it did before. The daemon reads it when it starts a worker and gives that worker `DOTNET_MODIFIABLE_ASSEMBLIES=debug` and the choice, spelled out. See [how it works](how-hot-reload-works.md#a-run_app-app-the-same-save-as-a-metadata-delta) ([`MetadataDeltaFlag.fs`](../SageFs.Core/Features/MetadataDelta/MetadataDeltaFlag.fs), [`Args.fs`](../SageFs.Core/Args.fs)). |
| `SAGEFS_EDITOR` | `EDITOR`, then `code` | Picks the link scheme for clickable `file:line:column` links: a name containing `rider` or `jetbrains` gives a JetBrains link, `cursor` gives a Cursor link, anything else a VS Code link. One function reads it for the page script and the Settings row ([`EditorLink.fs`](../SageFs.Core/EditorLink.fs)). |
| `SAGEFS_FORWARD_PREFIXES` | unset | A `;` or `:` separated list of name prefixes. Environment variables starting with one of them are passed into every process SageFs spawns, so a profiler or tracer can reach a worker it did not launch. SageFs does not read or check the values ([`ProcessEnvironment.fs`](../SageFs.Core/ProcessEnvironment.fs)). |
| `SAGEFS_HOST_CACHE_DIR` | `<data dir>/hosts` | Where built FSI hosts are cached. A host is addressed by its content, so daemons can share one cache, and a test harness whose daemons each get a fresh data dir uses this to build the host once ([`IsolatedFsiSession.fs`](../SageFs.Core/IsolatedFsiSession.fs)). |
| `SAGEFS_REPO_ROOT` | unset | Where the SageFs repository is, for code that has to read a file out of it, such as the test suite's fixtures. A value wins when you set it, and SageFs looks upward from it for the checkout. Unset, it looks upward from the assembly it runs from, then from the working directory. The error it raises when it finds nothing says to set this ([`RepoPaths.fs`](../SageFs.Core/RepoPaths.fs)). |
| `SAGEFS_GATE_HOME` | `~/.local/share/sagefs-gate` | The release gate's state directory, as `scripts/local-gate.fsx` names it. The workspace hygiene scan reads it to find the gate's checkouts and pass records and to say how much space they take ([`HygieneService.fs`](../SageFs/HygieneService.fs)). |
| `SAGEFS_REFERENCES_RESIDENT_CEILING_MB` | a share of the machine's memory | Megabytes the worker may hold in all before `list_references` drops the compiler's cache. The cache is bounded in projects (`SAGEFS_REFERENCES_PROJECT_CACHE`) and a project count says nothing about bytes, so this is the bound that keeps a large session from leaving a multi-gigabyte worker behind. Unset, the ceiling is 15 percent of the machine's total memory, never less than 1280 MB (a small machine still keeps one project warm) and never more than 8192 MB. So a 16 GB machine gets about 2.4 GB, a 32 GB machine about 4.8 GB, and a 64 GB machine the 8 GB cap, enough to answer the eight-project `SageFs.Tests` session project-wide (it needs about 4.5 GB). If the machine's memory cannot be read the ceiling is 2560 MB. A value you set wins; one that is not a positive integer is reported on stderr and ignored. The worker logs the number it chose and why when the first `list_references` ask builds the cache. The rule is in [`ReferenceCacheRetention.fs`](../SageFs.Core/Features/ReferenceCacheRetention.fs) and the setting in [`SageFsConfig.fs`](../SageFs.Core/SageFsConfig.fs). The idle and pressure rules are in the Timeouts table below. |
| `SAGEFS_FINAL_GATE` | unset | Not read by the daemon. It is how you tell the `sagefs-repl-guard` agent hook that a `dotnet build` or `dotnet test` is the final gate and may run. See [Using SageFs with AI agents](agents.md) ([`ReplGuard.fs`](../tools/agent-hooks/ReplGuard.fs)). |
| `SAGEFS_REPL_GUARD` | `enforce` | Not read by the daemon. It is read by the `sagefs-repl-guard` agent hook. `advise` lets a `dotnet build`, `test`, `run` or `fsi` run and gives the agent the REPL reminder instead of refusing it. Any other value enforces and the refusal says so. A repository can also list commands that always pass in `.SageFs/repl-guard.json` ([`ReplGuard.fs`](../tools/agent-hooks/ReplGuard.fs), [hook README](../tools/agent-hooks/README.md)). |

`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL` (`grpc` by default)
and `OTEL_SERVICE_NAME` (`sagefs` by default) are the standard OpenTelemetry
variables, and setting the endpoint turns telemetry export on.

### Culture and Unicode in a session

Code in a session runs in the FSI host, and the host takes the globalization of the process that started it. On a machine with ICU that is the full
.NET behavior: `"é".Normalize(NormalizationForm.FormD)` splits the accent off and `CultureInfo "de-DE"` exists, the same as under `dotnet fsi` and
`dotnet test`. Older builds compiled the host with `InvariantGlobalization`, so both differed ([#180](https://github.com/WillEhrendreich/SageFs/issues/180)).
A machine without ICU, for instance a slim container, sets `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` in the daemon's environment and the host follows.
The daemon needs that variable too on such a machine, since .NET refuses to start without ICU or the variable.

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
| `SAGEFS_PROJECT_ASSETS` | daemon, for the isolated host | The `project.assets.json` of each project the session loaded, separated by the platform's path separator. The host reads them to find the native libraries the projects' packages carry (they sit in the NuGet cache, which the loader does not search) and to say which package ships none for this machine. |

(Sources: [`Args.fs`](../SageFs.Core/Args.fs), [`WatchdogRunner.fs`](../SageFs/WatchdogRunner.fs), [`IsolatedFsiSession.fs`](../SageFs.Core/IsolatedFsiSession.fs).)

## Machine tier

A start that takes 3 seconds on a new desktop takes 17 seconds on a 2009 four core with a spinning disk, and
more than 30 seconds when something else is using it. A wait written for the first machine is wrong for the
second, so SageFs works out which kind of machine it is on, and scales the waits that are for the machine.

| Tier | Chosen when the slowest of these says so | Waits for the machine are |
|:---|:---|:---|
| `Fast` | single thread within 1.75 times the reference, 8 or more cores, 6 GB or more available | as written below (x1) |
| `Standard` | 1.75 to 3.5 times slower, or 4 to 7 cores, or 3 to 6 GB | x2 |
| `Constrained` | 3.5 to 8 times slower, or 2 or 3 cores, or 1.5 to 3 GB, or a spinning disk | x5 |
| `Minimal` | 8 or more times slower, or 1 core, or under 1.5 GB | x12 |

The measurements the boundaries and factors come from, with the machine and the number of runs for each, are in
[Slow machines](TROUBLESHOOTING.md#slow-machines). The reference is a Ryzen 7 5800XT: the single-thread
calibration in [`MachineCalibration.fs`](../SageFs.Core/MachineCalibration.fs) reads 22 to 24 ms there.

The daemon reads the tier from `SAGEFS_MACHINE_TIER`, else from `machine-profile.json` in the data directory,
else probes (a short single-thread workload, the cores .NET can use, available memory with any cgroup limit
applied, and whether the data directory's disk spins) and writes the profile. The same file records, for each
stage of a start, a smoothed duration and its variation (the way TCP estimates a round trip, RFC 6298), so the
next start is already as patient as this machine has shown it needs to be. Delete the file to probe again.
`sagefs check` and the daemon log say which tier is in force and why.

No wait is scaled past an hour. A wait that is for a person (a debounce, a poll, a presence window, a lease),
a network budget or a protocol constant is not scaled at all: the column below says which is which, and
`TimeoutScalingTests` fails when a duration is in neither list.

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

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_WARMUP_MAX_MINUTES` | 10 | minutes | yes | The hard ceiling on a worker's warmup, which neither progress nor silence can argue past. The constant has no doc comment; these words are from the one on `awaitWorkerPort` in `SessionManager.fs`. | [L130](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L130) |
| `SAGEFS_WARMUP_INACTIVITY_SECONDS` | 30 | seconds | yes | How long a starting worker may go without reporting progress before it is declared stuck. The clock resets on every `WARMUP_PROGRESS=` line, so silence, not slowness, is what trips it. The constant has no doc comment; these words are from the one on `awaitWorkerPort` in `SessionManager.fs`. | [L131](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L131) |
| `SAGEFS_FSI_HOST_STARTUP_SECONDS` | 120 | seconds | yes | How long a started FSI host process has to finish its startup handshake (the one-shot config host in ConfigHost and the isolated session host in IsolatedFsiSession) before the start is given up as failed. No recorded reason for 120s. | [L138](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L138) |
| `SAGEFS_DOTNET_SDK_QUERY_SECONDS` | 30 | seconds | yes | How long `dotnet --version` and `dotnet --list-sdks` may each take while FsiHostBuild picks the SDK to build the FSI host with. They answer from disk, so a wait this long means a hung muxer. No recorded reason for 30s. | [L151](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L151) |
| `SAGEFS_STOP_GRACEFUL_SECONDS` | 30 | seconds | yes | How long `sagefs stop` waits for the daemon's process to exit after the shutdown request, which gives a normal graceful shutdown (manifest save, stopping workers) room before the fallback kill. | [L172](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L172) |

### HTTP / Worker Communication

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_WORKER_HTTP_READ_SECONDS` | 30 | seconds | yes | The read timeout the daemon gives the worker's streaming test proxy (`DaemonMode.fs`, `HttpWorkerClient.streamingTestProxyWithCoverage`). The constant has no doc comment; the one on `daemonSessionsProbe` says that probe is shorter than this. | [L179](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L179) |
| `SAGEFS_WORKER_HTTP_REQUEST_MINUTES` | 10 | minutes | yes | Bounded request timeout for worker HTTP calls (eval/check/typecheck/reset/etc). An eval that hangs in the worker must not hang the caller forever; 10 minutes matches the ValidTimeout max and the build cap, so legitimately long evals still complete while a wedged worker eventually surfaces as a timeout error instead of an infinite hang. | [L185](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L185) |
| `SAGEFS_CONFIRM_ANSWER_SECONDS` | 60 | seconds | no | How long a confirmation prompt waits for the person at the client to answer before the call is refused. This is a person reading a sentence, not a process starting, so it is NOT scaled by machine tier; it takes an override because a slow reader is exactly who would need to raise it. Running out is refused rather than waved through, since a prompt that gave up and let the call run would protect nobody, and the refusal says the answer never came. | [L197](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L197) |

### Live Testing (configurable at runtime via MCP, thread-safe)

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_PER_TEST_TIMEOUT_SECONDS` | 5 | seconds | yes | The per-test timeout when neither the environment nor a setting says otherwise. The constant has no doc comment; these words are from the one on `perTestTimeoutFallback`, whose default is 5 seconds. The section is headed "configurable at runtime via MCP", so this is the starting value. | [L261](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L261) |

### Live testing as you type

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_REBUILD_READY_SECONDS` | 30 | seconds | yes | How long after a rebuild restart the live-testing pipeline waits for the session manager to say the session is Ready (which is also when its streaming test proxy exists) before it reports the rebuild as failed. It is the one timer in that wait. No recorded reason for 30s. | [L345](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L345) |

### Process Management

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_BUILD_TIMEOUT_MINUTES` | 10 | minutes | yes | How long a `dotnet build` run by a session start or rebuild may take before SessionBuild kills it and the session faults with `BuildFailure.TimedOut`. Large repos need minutes; 10 matches the ValidTimeout max. | [L351](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L351) |
| `SAGEFS_HOST_BUILD_MINUTES` | 5 | minutes | yes | How long the `dotnet build` of the FSI host (a cold build, once per host version and SDK) may run before FsiHostBuild gives up. No recorded reason for 5 minutes. | [L380](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L380) |

### Hot reload

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_PATCH_CONFIRM_SECONDS` | 10 | seconds | no | How long a patched function may go without its new body running before the save reports it as never entered. A page that refreshes on the pending report usually runs the function within a second; a function nothing calls (an idle app, or a caller that kept its own copy of the old body) reads the same, so the report says "unconfirmed, exercise it". | [L555](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L555) |
| `SAGEFS_HOT_RELOAD_COMPILE_QUEUE_SECONDS` | 60 | seconds | yes | How long one save waits for the previous save's compile before it gives up and reports that it did. The wait used to be unbounded, which let one wedged eval silently disable hot reload for every other file; bounded, a stuck compiler shows up. No recorded reason for 60s. | [L588](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L588) |
| `SAGEFS_REFERENCES_CHECK_SECONDS` | 60 | seconds | yes | How long the compiler checks behind `list_references` may take, ALL the session's projects together. One budget covers the whole question, so the cost of a reply is bounded whatever the project count: a project that starts with nothing left is skipped and named as not checked, and the reply's scope line says so. Longer than `callerCheck` because a person asked for this answer and is waiting for it, where a save only wants to say who is stranded. An operator on a very large solution can raise it. | [L609](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L609) |

### Watchdog / Supervision

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_HYGIENE_SWEEP_MINUTES` | 15 | minutes | no | How often the daemon runs its workspace hygiene sweep: it saves uncommitted worktree files to a salvage branch, records what it saw in the hygiene ledger, and tidies what is provably safe. A finished agent's worktree is judged by a grace measured in hours, so a sweep every quarter of an hour finds anything new while it is still fresh and costs a few git questions per worktree. It also runs once at start and when a session stops. | [L750](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L750) |

### Dashboard / UI

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_DASHBOARD_HEARTBEAT_SECONDS` | 5 | seconds | no | Server SSE heartbeat cadence: the stream loop patches a heartbeat signal at least this often (even on no-change ticks) so the client can prove liveness. | [L769](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L769) |
| `SAGEFS_DASHBOARD_STALE_AFTER_SECONDS` | 15 | seconds | no | Client staleness budget: if no heartbeat arrives within this window the live stream is LATE, which is a suspicion and not a verdict. The page then asks the daemon directly (`dashboardProbe`) before it says anything about whether the daemon is running. | [L773](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L773) |

### Daemon / Server

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_STOP_SESSION_TIMEOUT_SECONDS` | 20 | seconds | yes | Defense-in-depth bound on a `StopSession` mailbox round-trip (`DaemonMode.createSessionOps.StopSession`). `stopWorker` itself is already bounded (workerShutdownDelay + a WaitForExit + a Kill fallback), and the SessionManager mailbox loop is supervised (`superviseStep`/`supervise` in SessionManager.fs) so an unhandled exception replies with an error instead of dropping the caller. This timeout is the last line: if the reply STILL never arrives, a future regression, not a known path today, `stop_session` fails with a stated, actionable error at 20s instead of hanging until the MCP client's own external timeout (300s in the three fcs-onboarding-trial reports, 2026-09-22) makes the caller guess why. "A stop always completes" is an invariant this bound exists to guarantee even if everything upstream of it were somehow wrong. | [L896](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L896) |
| `SAGEFS_SUPERVISOR_WEDGE_SECONDS` | 30 | seconds | yes | How long one command may hold the session manager's loop before the supervisor calls it wedged. Above the slowest honest handler (a parallel stop of every session waits seconds) and below `stopSessionMailboxTimeout` doubled, so a wedge is named before callers give up on it. | [L901](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L901) |

### Persistence

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_MANIFEST_SAVE_INTERVAL_SECONDS` | 60 | seconds | no | Cadence of the daemon's periodic manifest save (`periodicManifestSave`, `DaemonMode.fs`, `cacheSaveTimer`): both the timer's initial due time and every reschedule use this value. Default stays 60s in production; tests that need the crash/resume durability boundary to arrive sooner (rather than waiting on a fixed 60s wall-clock tick) can shrink it via the env var. Env-overridable. | [L916](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L916) |

### Session Lifecycle

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_ABANDONED_SESSION_HOURS` | 12 | hours | no | How long the connection that made an agent's session has to be gone, AND how long the session has to be idle, before the daemon stops it (`SessionReaper`). Both have to reach it. A session with no owner record, made from the dashboard, the CLI or an editor, is never stopped this way. Counted from no earlier than the daemon's own start, so a restart delays a stop and never brings one forward. Twelve hours is a night: an agent that comes back the next morning finds its session. Zero turns the reaper off. | [L931](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L931) |
| `SAGEFS_WARMUP_READY_POLL_SECONDS` | 120 | seconds | yes | Maximum time to poll a worker waiting for Ready status after spawn/restart. If exceeded, the session is faulted to prevent infinite WarmingUp states. | [L934](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L934) |

### Local data retention

| Variable | Default | Unit | Scaled by tier | What it is for | Source |
|:---|---:|:---|:---|:---|:---|
| `SAGEFS_FRICTION_MAX_AGE_DAYS` | 30 | days | no | Friction rows older than this are rolled into the per-version aggregate and deleted. | [L1013](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1013) |
| `SAGEFS_FRICTION_MAX_ROWS` | 5000 | rows | no | Raw friction rows kept per table. The newest win. | [L1015](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1015) |
| `SAGEFS_FRICTION_MAX_AGGREGATE_VERSIONS` | 20 | versions | no | How many SageFs versions keep their small aggregate (a count per kind). | [L1017](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1017) |
| `SAGEFS_COHORT_LEDGER_RETENTION_DAYS` | 7 | days | no | A finished cohort's ledger rows are cleared once its last entry is older than this. An active cohort is never touched. | [L1020](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1020) |
| `SAGEFS_HOST_CACHE_MAX_AGE_DAYS` | 30 | days | no | A built FSI host that no session has used for this long is pruned by the daemon's housekeeping. A host rebuilds in a few minutes, so a month of disuse says that SDK and source combination is gone from daily use, and 3 GB of 55 hosts is what no pruning cost one machine. | [L1043](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1043) |
| `SAGEFS_GATE_RETENTION_DAYS` | 14 | days | no | A gate checkout or tier clone whose invoking repo is still there is kept this long after its last gate, so the next gate on the same repo stays warm. Past it the checkout is recreated in a few minutes. | [L1046](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1046) |
| `SAGEFS_TEMP_RUN_MAX_AGE_DAYS` | 2 | days | no | A test runner's temp data dir (and any other `sagefs-*` temp entry nobody records an owner for) is reclaimable this long after it was last touched. A run takes minutes; two days leaves room for a debugging session that wants the leftovers of the run that just failed. | [L1050](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1050) |
| `SAGEFS_WORKER_LOG_MAX_AGE_DAYS` | 14 | days | no | A worker's log is kept this long after its last write. Logs are what a person reads after a crash, and a crash is looked at within days. | [L1053](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1053) |
| `SAGEFS_FINISHED_WORKTREE_GRACE_DAYS` | 1 | days | no | A merged agent worktree is kept this long after it was last in use, because the agent that made it may be resumed: the release gate often finds a flake in what an agent wrote, and a resume needs the worktree it was in. A day covers a merge, a gate run and the fix that follows. 0 means no grace. Work that is not merged is never removed, whatever this says. | [L1057](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1057) |
| `SAGEFS_GATE_PASS_RECORDS_KEPT` | 10 | versions | no | How many of the local gate's pass records are kept, newest first. A record holds a commit's release bundle (about 270 MB each), and the pre-push hook only ever looks up the commit being pushed, so ten covers a day of shipping; 75 of them were 20 GB. | [L1061](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1061) |
| `SAGEFS_GATE_LOGS_KEPT` | 20 | versions | no | How many of the gate's logs are kept, newest first. They are what a person reads after a red gate, and a red gate is read within the hour; twenty is a few days of runs. | [L1064](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1064) |
| `SAGEFS_DATA_PRUNE_INTERVAL_MINUTES` | 60 | minutes | no | How often a running daemon prunes friction again (it also prunes on start). | [L1022](https://github.com/WillEhrendreich/SageFs/blob/c89ae42e6f698518c9c773e180f508a6d7f02488/SageFs.Core/Timeouts.fs#L1022) |

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

`SAGEFS_MANIFEST_SAVE_INTERVAL_SECONDS` is there so a test can reach a boundary
(the save a crash resumes from) without waiting out the real default.

## Retired variables

Most waits no longer have a variable. A wait for the machine is its value on a `Fast`
machine scaled by the machine tier, so a slower machine gets more time by setting
`SAGEFS_MACHINE_TIER` (or the Machine tier row), and every other wait is one fixed number.
A daemon that finds one of these variables set says so in its log, and a saved key of one
stays in `settings.json` and is ignored with a note. Nothing is deleted, so an older build
reads the same file. The list is
[`RetiredSettings.fs`](../SageFs.Core/RetiredSettings.fs), with a reason for each.

| Variable | What decides the wait now |
|:---|:---|
| `SAGEFS_AMBIENT_SDK_PROBE_SECONDS`, `SAGEFS_TARGET_FRAMEWORK_EVALUATION_SECONDS`, `SAGEFS_GC_DUMP_CAPTURE_SECONDS`, `SAGEFS_DAEMON_SESSIONS_PROBE_SECONDS`, `SAGEFS_GIT_QUICK_SECONDS`, `SAGEFS_GIT_REBASE_MINUTES`, `SAGEFS_GIT_WORKTREE_ADD_MINUTES`, `SAGEFS_COHORT_SETTLE_SECONDS`, `SAGEFS_HOT_RELOAD_COMPILE_BUDGET_MINUTES`, `SAGEFS_APP_HOST_APPEAR_SECONDS`, `SAGEFS_APP_HOST_START_SECONDS`, `SAGEFS_WORKER_PROBE_TIMEOUT_SECONDS`, `SAGEFS_JUPYTER_WARMUP_SECONDS`, `SAGEFS_WORKER_PROXY_WAIT_SECONDS` | The machine tier: the value for a fast machine, scaled |
| `SAGEFS_FRICTION_POST_SECONDS`, `SAGEFS_UPDATE_CHECK_TIMEOUT_SECONDS`, `SAGEFS_UPDATE_CHECK_INTERVAL_MINUTES`, `SAGEFS_REFERENCES_CACHE_RETENTION_SECONDS`, `SAGEFS_REFERENCES_CACHE_SWEEP_SECONDS`, `SAGEFS_WORKER_PROBE_INTERVAL_SECONDS`, `SAGEFS_WORKER_READY_POLL_SECONDS`, `SAGEFS_OWNER_POLL_SECONDS`, `SAGEFS_AGENT_CLEANUP_SECONDS`, `SAGEFS_COHORT_REAPER_SECONDS`, `SAGEFS_ORPHAN_SWEEP_MINUTES`, `SAGEFS_WATCHER_SYNC_SECONDS`, `SAGEFS_IDLE_SESSION_THRESHOLD_MINUTES`, `SAGEFS_SESSION_MANAGER_QUEUE_CAPACITY` | One fixed number, in [`Timeouts.fs`](../SageFs.Core/Timeouts.fs) or [`SageFsConfig.fs`](../SageFs.Core/SageFsConfig.fs) |
| `SAGEFS_WORKER_STARTUP_TIMEOUT_MS` | Nothing. The warm-up inactivity limit and ceiling replaced it |
| `SAGEFS_TEST_DAEMON_READY_SECONDS`, `SAGEFS_TEST_WORKER_RESTART_SECONDS`, `SAGEFS_TEST_BROWSER_WARMUP_SECONDS`, `SAGEFS_TEST_WEBAPP_BUILD_SECONDS`, `SAGEFS_TEST_WEBAPP_PORT_SECONDS` | The test suites, which take their deadlines from `Timeouts` |

## Abandoned sessions

Agents create sessions and often never stop them. A session keeps its worker
process and its memory for days, and once the connection that made it is gone
nobody can stop it over MCP, because an MCP session is stopped only by the
connection that created it. So the daemon does it.

A session is stopped when all of these are true:

- an MCP connection made it (the daemon keeps a record of that in
  `session-owners.json` in its data directory),
- that connection has been gone for `SAGEFS_ABANDONED_SESSION_HOURS`,
- the session has been idle for the same time, and
- it is not starting, evaluating, building or restarting.

The default is 12 hours. `0` turns it off. A session with no record, one you made
from the dashboard, the CLI or an editor, is never stopped this way.

After a daemon restart every owner looks gone, because who is connected is not
saved. So both clocks start no earlier than the daemon's own start. A restart
delays a stop. It never brings one forward.

The daemon checks once a minute, and it stops the session the way the Stop button
does. It writes one log line (session id, agent, how long the owner was gone, how
long the session was idle). `list_sessions` shows an `Owner:` line under each
agent-made session, so you can see what is about to go, and it lists the sessions
the daemon stopped since it started. That list is in memory. A restart forgets it,
and the log line is the record.

What is rough: "gone since" is the last minute the daemon saw the connection, so it
can be a minute stale. A connection that comes back for one tool call counts as
here, and the clock starts again from that call.
