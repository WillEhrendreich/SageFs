# Lemmings

A lemming is a disposable trial. A cheap agent gets only what a new SageFs user has, does a small task, and the harness scores what happened. The point is to find where SageFs falls over, not to find out whether the agent is clever.

The lemmings here are Command Code (`cmdc`) on free models. They are clients of one shared SageFs daemon on port 37749, which the person running the harness starts and owns, so that person can watch every lemming in the dashboard at http://localhost:37750/dashboard. Nothing in this directory ever starts, stops, restarts or configures that daemon.

`run-lemming.fsx` and `score.fsx` are the older harness for Claude Code. They are still here, ported to F# with the same behavior (`LemRun/Legacy.fs`). Everything below is the cmdc harness.

Every script here is F#: a typed `.fsx` entry point run with `dotnet fsi scripts/lemmings/<name>.fsx -- <args>`, which builds the tool behind it when its sources changed (`launch.fsx`) and hands over to it. The logic is three projects: `LemScore` (the event parser, the classifier, the daemon gate, `summary.json`), `LemMatrix` (the matrix bookkeeping) and `LemRun` (the process plumbing: the bubblewrap sandbox, cmdc, Xvfb and tmux, the stores, the oracles, the prune). Nothing under this directory is a shell script.

## Prerequisites

The harness is Linux only (it needs bubblewrap). Everything must be in place before the first run:

| Need | Why | Check |
|---|---|---|
| `bwrap` (bubblewrap) | every lemming, and every oracle that runs lemming-written code, runs inside it | `command -v bwrap` |
| `git`, `ss`, `ps`, `timeout` | the lemming's working repo, the port and process checks, the time limit the sandbox applies | `command -v git ss ps timeout` |
| the .NET SDK in `global.json` (11.0.100-rc.1 or later, prerelease allowed) | runs the `.fsx` entry points, builds `LemRun`, `LemScore` and `LemMatrix` on first use, and runs the lemming's tests | `dotnet --version` |
| `cmdc` (Command Code), installed **under `~/.local/share/mise`** and logged in | the lemming itself. The sandbox binds only that toolchain, so a `cmdc` installed anywhere else is refused (exit 4) | `readlink -f "$(command -v cmdc)"`, and `~/.commandcode/auth.json` must exist |
| a built master dev dll at `<main checkout>/SageFs/bin/Release/net11.0/SageFs.dll` | the `sagefs mcp` bridge each lemming runs (or set `SAGEFS_LEMMING_BIN`, see below) | `dotnet build SageFs/SageFs.fsproj -c Release` in the main checkout |
| a running shared SageFs daemon on port 37749 | see the next section | `curl -s localhost:37749/health` |

"Main checkout" is the checkout that owns this worktree (`git rev-parse --git-common-dir`, one level up), which is where the harness looks for the dev dll.

### Start the shared daemon

The harness only checks the daemon, it never starts one. Start it yourself, in its own terminal or as a service, before running a lemming, and leave it running:

```
dotnet <main checkout>/SageFs/bin/Release/net11.0/SageFs.dll --no-resume     # the dev build
sagefs --no-resume                                                           # or the global tool
```

`--no-resume` starts it without restoring earlier sessions (`SageFs/Program.fs`, `--no-resume`). The default port is 37749. To run it as a user service instead, see "Run it as a service" in the repo `Readme.md`, `contrib/systemd/sagefs.service` (Linux) and `contrib/launchd/io.github.willehrendreich.sagefs.plist` (macOS).

Before each run the harness reads `/health` and `get_daemon_status`. The daemon must be healthy, `memoryPressure` must be `normal`, and the machine must have more than 8 GB available. It waits a bounded time and then refuses with exit 3. Those readings are of the whole machine, so other agents, other lemmings and an open editor all count against them.

### Use the same build as the daemon

The bridge each lemming runs is the dev dll (a read-only copy kept once, see "Which SageFs the lemming gets"). If the daemon was started from an older build, the two differ, and a protocol or tool-surface difference would look like a model or SageFs failure. The harness compares the two commits at the start of every run:

- it prints a `VERSION SKEW` warning on stderr,
- `summary.json` gets `versionSkew: true`, both versions (`daemonVersion`, `bridgeVersion`) and a `Preflight` finding in `fellOver`,
- `LEM_REQUIRE_SAME_VERSION=1` makes a skewed run exit 4 before anything is copied.

It cannot just use "the dll the daemon runs": that file may have been rebuilt since the daemon started, so the version printed by `get_daemon_status` is the only honest reading of what is running. To clear a skew, restart the daemon from the dll you are testing, or point `SAGEFS_LEMMING_BIN` at the build the daemon runs. `versionSkew` is `null` when either version cannot be read.

## Run one

Run from the root of a SageFs checkout, so the paths below resolve:

```
dotnet fsi scripts/lemmings/run-lemming-cmd.fsx -- <fixture> <task> <free-model> <run-id|auto> [max-turns]

dotnet fsi scripts/lemmings/run-lemming-cmd.fsx -- demoenv parse-seed stealth/space-bunny-alpha space-bunny-parse-seed-01
dotnet fsi scripts/lemmings/run-lemming-cmd.fsx -- demoenv parse-seed stealth/space-bunny-alpha auto 40
dotnet fsi scripts/lemmings/run-lemming-cmd.fsx -- "sagefs-copy:$PWD" sagefs-small-fix poolside/laguna-s-2.1-free auto
```

`auto` picks the next free `<model-short>-<task>-<nn>`. The run directory is `/tmp/lem/<run-id>` (override the root with `LEM_ROOT`), and the lemming works in `/tmp/lem/<run-id>/w`. Its sessions are keyed by that directory, so in the dashboard a lemming's sessions show a path with the run id in it, like `.../space-bunny-parse-seed-01/w`.

`/tmp` is tmpfs on this machine, which is RAM, so a run keeps almost nothing there:

- the SageFs build the bridge runs is **not** copied into the run. It is copied once per build into a store off tmpfs (`~/.local/share/sagefs-lemmings`, or `LEM_STORE`), keyed by its version and the hash of its dll, made read-only and mounted into each sandbox that uses it. The editor driver is stored the same way (`drive/`, keyed by a hash of its sources).
- when a run ends, everything except `out/` (summary.json, timeline.ndjson, shots, logs) is removed: the working copy, the run's own NuGet folder, the sandbox scratch. Set `LEM_KEEP_RUN=1` to keep the whole directory, or clear kept and older runs with `prune-runs` (below).
- while it runs, a `demoenv` run holds the fixture and a restored NuGet folder (a few MB). A `sagefs-copy` run holds a copy of the source tree and of `SageFs.Core`'s build output (`LEM_COPY_BUILD`) for as long as it runs, about 400 MB, because a session builds in place and loads that output.

An old stored build is removed by `prune-runs --builds` when nothing has used it for a week (the newest three are always kept).

### Exit codes

| Exit | Meaning |
|---|---|
| 0 | the run finished and `summary.json` was written. **This says nothing about the outcome**: read `outcome` in the summary |
| 1 | a required argument is missing |
| 2 | refused or bad usage: the model is not FREE, there is no such task or fixture, the run id already exists, or `sagefs-copy:` was not given a git checkout |
| 3 | the shared daemon is unavailable: unreachable, unhealthy, short of memory, or the sessions list is unreadable |
| 4 | the build or toolchain is missing: `LemScore` would not build, no `SageFs.dll` to copy, `cmdc` not on `PATH` or not under `~/.local/share/mise`, or a skewed bridge under `LEM_REQUIRE_SAME_VERSION=1` |

`run-matrix` stops the rest of a matrix when a runner exits 3 or 4.

### Clear old runs

```
dotnet fsi scripts/lemmings/prune-runs.fsx -- --older-than-days 7 --dry-run
dotnet fsi scripts/lemmings/prune-runs.fsx -- space-bunny-parse-seed-03
dotnet fsi scripts/lemmings/prune-runs.fsx -- --builds
```

A finished run (one with `out/summary.json`, or `out/tour.json` for a tour) already pruned itself to `out/` when it ended, so this is for runs kept with `LEM_KEEP_RUN=1` and for runs from before that. It removes everything in a named or old-enough run except `out/`; `--whole` removes the run directory too, evidence included; `--builds` also removes stored SageFs and driver builds that nothing has used for a week (the newest three are always kept). Nothing is touched unless runs are named or an age is given, a run without a summary is never touched because it may be running, and `--root` must be at least two segments deep. `--whole` is the only form that loses the summary, so read it first.

## Models

Free models only. The runner reads the live `cmdc --list-models` and refuses (exit 2) any model whose description does not start with FREE, before it touches anything or calls an API. Right now that is:

- `poolside/laguna-s-2.1-free`
- `inclusionai/ling-3.0-flash-sante:free`
- `inclusionai/ling-3.1-flash:free`
- `stealth/space-bunny-alpha`

`stealth/pixel-canary` is not in the live catalog today, so it is refused too. Free models have provider quotas. A quota error is recorded as its own outcome, `ProviderQuota`, and nothing retries.

## Which SageFs the lemming gets

The daemon is the one the harness user started, normally the master dev build. What each lemming gets of its own is the `sagefs mcp` stdio bridge in its MCP registration, written the way the README says (`command` + `args: ["mcp"]`), with `SAGEFS_MCP_PORT=37749` so it attaches to the running daemon instead of starting one.

- default: `<main checkout>/SageFs/bin/Release/net11.0/SageFs.dll`, copied once into the store (`~/.local/share/sagefs-lemmings/bridge/<version>-<dll hash>`, or under `LEM_STORE`), read-only, and mounted into each sandbox that uses it. A rebuild is a new key, so it cannot change a trial that is running, and a hundred runs of one build hold one copy
- `SAGEFS_LEMMING_BIN=published`: the global tool `sagefs`, for a baseline
- `SAGEFS_LEMMING_BIN=<path>`: a `SageFs.dll`, or a directory holding one

summary.json records both (`sagefsVersion`, for example `daemon 0.6.875+d7e11077...; bridge dev build: 0.6.875+d7e11077...`, plus `daemonVersion`, `bridgeVersion` and `versionSkew`), and flags a mismatch as described above.

## What the lemming sees

The project, the SageFs skill in `.commandcode/skills/sagefs` (where Command Code finds project skills), and `.mcp.json`. Command Code's own built-in skills are there too, the same as for a new user. The prompt is the task file, written the way a user would write it, and it never mentions the daemon's control surface.

## Outcomes

summary.json always has exactly one of these. The oracle, run by the harness outside the sandbox, decides. The model's claim never does.

| Outcome | Meaning |
|---|---|
| `Pass` | the oracle passed, no SageFs error, nothing left behind |
| `PassWithRecovery` | the oracle passed after a SageFs error, a refused tool, a session left on the daemon, or hitting the turn cap |
| `Fail` | the lemming finished cleanly and the oracle failed |
| `Blocked` | SageFs errored while setting up a session and no eval ever succeeded, and the oracle failed |
| `Incomplete` | the harness timeout ended the run, or it ended without a clean finish, and the oracle failed |
| `MaxTurns` | cmdc hit the turn cap (exit 8) and the oracle failed |
| `ProviderQuota` | the provider said no: rate limit or daily limit (exit 5), no credits (10), connection (6) or server (7) error. Not a SageFs result |
| `HarnessError` | no event stream, an auth failure inside the sandbox, or no oracle ran. A problem with the harness, not with SageFs or the model |

A failing eval of the lemming's own code is the red step of the loop the skill teaches, so it is counted (`evalFailures`) but is not a SageFs error.

## Artifacts

While it runs:

```
/tmp/lem/<run-id>/
  w/                        the lemming's working directory (a git repo, tag lem-baseline, no remote)
  nuget/                    the lemming's own NuGet packages folder (the user's cache is a read-only fallback)
  cmdchome/                 the lemming's private ~/.commandcode (credentials bound over it, read-only)
  out/
    summary.json            the verdict
    events.ndjson           cmdc's event stream
    cmdc.stderr
    prompt.md
    residue.json            sessions found under the run dir after the lemming exited, and how they were stopped
    sessions.seen.json      sessions the dashboard API showed under the run dir while it ran, with every status each passed through
    changed.txt             files changed relative to lem-baseline
    oracle.out              what the oracle printed
    sbx/ps.txt              processes still alive in the sandbox when cmdc exited
```

When the run ends, everything except `out/` is removed (unless `LEM_KEEP_RUN=1`), so `out/` is what is left: summary.json, events, the oracle's output, and for editor runs `timeline.ndjson`, `shots/`, `screens/` and the driver's logs. The SageFs build the bridge ran from is in the store, not in the run.

summary.json fields: `id`, `model`, `harness`, `sagefsVersion`, `daemonVersion`, `bridgeVersion`, `versionSkew`, `task`, `outcome`, `outcomeReason`, `providerError`, `seconds`, `turns`, `cmdcExit`, `toolCalls` (by name), `sagefsMcp` (calls, errorCount, evalFailures, inputRepairs, callSequence, and every error with its text), `oracle`, `residue` (sessions created, left behind, seen in the dashboard, processes left, lease calls), `teardown` (how cmdc ended, how the session cleanup went), `dashboardUrl`, `daemon` (memory pressure and available memory at start and end), `verification.changedFiles`, `knownLimits` (what the sandbox does not wall off, see Isolation), `fellOver`, `finalText`.

`residue.sessionsSeenInDashboard` shows each session's whole status history (`id (Starting > Ready > Busy > Ready > Disconnected)`), because the last status is almost always `Disconnected`, the status after the cleanup stopped it.

`fellOver` is a list of `{stage, symptom, evidence}`. Stages: Preflight (including a bridge/daemon version skew), Isolation (the lemming named a path outside its run directory in something the daemon runs), Registration, Adoption, SessionCreate, SessionWarmup, Eval, Reload, Tests, Lease, Cleanup, Budget, ToolSurface, Oracle, Editor, OtherTool. It is read off the stream: SageFs errors placed by the tool that failed, a faulted session, the same call three times in a row, reaching for `dotnet build` before a REPL eval, never calling SageFs at all, Command Code having to repair a tool input, sessions left behind, running out of turns, the oracle failing. An editor driver can add its own entries in `out/fellover.extra.json`.

## Isolation

### What the sandbox walls off

Each lemming's own processes run under bubblewrap: the toolchain read-only, a tmpfs home, a cleared environment, its own pid, ipc and uts namespaces (so it cannot see or signal the daemon's process, or any other process on the machine), and only its own working directory, a scratch dir, the stored bridge build (read-only) and its own NuGet packages folder writable or mounted. Nothing from the real desktop is passed in: no `DISPLAY`, no `WAYLAND_DISPLAY`, no `XDG_*`. The oracle runs lemming-written code too, so the one thing an oracle runs in a sandbox, the lemming's own test suite, runs in the same sandbox with no network and no credentials (`CmdRun.sandboxExec`); every other check an oracle makes (the diff, the regexes, the stream) is the harness's own code, outside it.

The user's NuGet cache (`~/.nuget/packages`) is mounted read-only. Restores resolve from it as a fallback folder and write anything new to the run's own `nuget/` folder, so a lemming cannot change the shared cache. Nothing else under `~/.nuget` (including any feed config with credentials) is visible.

### What it does not wall off

These are real holes. Read them before treating a run as contained. They are also listed in every `summary.json` under `knownLimits`.

1. **Code run through the daemon is outside the sandbox.** `send_fsharp_code`, `check_fsharp_code`, hot reload, and the builds that `create_project_session` starts all execute in the shared daemon's FSI host and build processes, which are children of the daemon, not of the lemming, and have the daemon owner's full file access. Through an eval a lemming can read or write anything that user can, and it can create a session on any project anywhere. The sandbox only walls the lemming's own processes.
2. **The daemon is shared with everyone.** The sessions list shows the lemming other agents' sessions and project paths. `stop_session`, `switch_session` and `release_work_lease` can reach sessions that are not the lemming's. Cleanup (below) only stops sessions under the run directory, so a session the lemming starts elsewhere is not stopped.
3. **The credential is readable by the model.** `~/.commandcode/auth.json` is bound read-only into the sandbox because `cmdc` cannot run without it, the network is open (the model API and the bridge's localhost connection need it), and `cmdc` runs with `--yolo`. The model's shell can read the credential and send it anywhere.
4. **The network is open**, including the shared daemon's HTTP API on localhost.

What the harness does about them: it states them (here and in every summary), and it keeps a tripwire. Every `send_fsharp_code`, `check_fsharp_code` and `create_*_session` call in the stream is scanned for absolute paths under `/home`, `/root`, `/etc`, `/var`, `/mnt`, `/opt`, `/run`, `/proc`, `/sys`, `/srv`, `/media` or `~/` that are outside the run directory, and each hit is an `Isolation` finding in `fellOver`. It is a tripwire read off the stream, not enforcement: code can build a path at runtime, so a quiet run is not proof.

What would actually close them is a policy in front of the daemon (a proxy on 37749 for the lemming, with a per-run allow-list of tools and project paths), and a way to keep the credential out of the model's reach. Neither exists here. Until they do, run lemmings only on a machine and a daemon whose owner accepts that a free model can act with that owner's file access, and use a throwaway account or a scoped Command Code token if one is available.

### Cleanup

Cleanup reads `GET /api/sessions` first and records what is there, then stops exactly the sessions whose working directory or loaded project is under this run's directory, by id, through `POST /api/sessions/stop`, then reads the list again. It never matches anything else, and it refuses a run directory that is not absolute and at least three segments deep. Note the sessions list is served on the MCP port, 37749. The dashboard port, 37750, answers 404 for it.

Before a lemming starts, the harness checks `/health` and `get_daemon_status` (see Prerequisites). It never starts a daemon. If the daemon dies mid-run, the bridge in the sandbox would try to start one, which lives and dies inside that sandbox.

### Noise from other agents

The daemon is shared, so a run is not alone on it. Other agents' sessions (including faulted ones) and other lemmings can be on the daemon during a run, and the memory-pressure gate sees the whole machine, not one lemming's load. Read `daemon.start` and `daemon.end` as machine readings, and `sessionsSeenInDashboard` as only this run's sessions.

## Fixtures and tasks

Fixtures live in `fixtures/<name>`. `demoenv` is a small F# library with an Expecto project, and `parse-seed` asks for a one-line fix in it.

`sagefs-copy:<path>` (`sagefs-copy:"$PWD"` copies the current checkout) copies a SageFs checkout or worktree for lemmings that work on SageFs itself: tracked files only (so no untracked notes), no `.git` (a fresh repo is made with a baseline commit and no remote), no `AGENTS.md`, `CLAUDE.md`, `.claude`, `.commandcode` or `.mcp.json`, and the build output of the projects in `LEM_COPY_BUILD` (default `SageFs.Core`) so a session loads quickly. The checkout it copies from is never touched.

Tasks live in `tasks/<name>.md`, and the file is the prompt:

- `parse-seed`: fix `DemoEnv.parseSeed` so a negative integer gives `None` (fixture `demoenv`). Oracle: the project's own Expecto suite passes in the sandbox and the tests were not edited.
- `sagefs-small-fix`: some `RingBuffer` tests fail, fix `SageFs.Core/RingBuffer.fs` and only that file. A setup step (`Workspace.setupFor`) seeds a one character off-by-one into the copy before the baseline commit (the shape of the real bug class, and the existing tests catch it). Oracle: the diff touches only that file, and `oracles/RingBufferOracle` compiles the lemming's `RingBuffer.fs` with the repo's own `RingBufferTests.fs` and runs it without network.
- `sagefs-repl-eval`: use the REPL to answer a question about `RingBuffer` at runtime and write `ANSWER.md`. No edits. Oracle: the four values are right, checked with regexes, and no source file changed.
- `smoke`: one `get_daemon_status` call. Oracle: the call completed in the stream.

### Add a task

1. Write `tasks/<name>.md`, as a user would write it. Do not explain SageFs in it.
2. Add an oracle: a case of `Oracle` in `LemRun/Oracles.fs`, mapped from the task name in `forTask`, with a function that checks what the lemming did. No oracle means `HarnessError`, never a pass. Anything that runs the lemming's code goes through the sandbox (`Context.Sandboxed`) and nothing else does. Logic bigger than a few lines goes in F# next to it (see `oracles/RingBufferOracle`). Add a test beside the others in `LemScore.Tests/OracleTests.fs`.
3. If the task needs the fixture changed first, add its case to `Workspace.setupFor` in `LemRun/Workspace.fs`. It runs before the baseline commit, and refuses out loud when what it seeds does not match.

### Add a fixture

Drop a directory in `fixtures/<name>` with the project as a new user would have it. It is copied into `w`, made a git repo, and the skill and `.mcp.json` are added.

To check a fixture or a setup step without calling a model: `LEM_PREPARE_ONLY=1 dotnet fsi scripts/lemmings/run-lemming-cmd.fsx -- <fixture> <task> <model> <id>`.

## Matrix

```
dotnet fsi scripts/lemmings/run-matrix.fsx -- <plan-file|-> [--concurrency 3] [--max-turns 60] [--quota-limit 2] [--root /tmp/lem] [--dry-run]
```

A plan is one run per line, `<fixture> <task> <model> [reps]`, with `#` comments:

```
demoenv parse-seed stealth/space-bunny-alpha 3
demoenv parse-seed poolside/laguna-s-2.1-free 3
sagefs-copy:/path/to/a/SageFs/checkout sagefs-small-fix stealth/space-bunny-alpha
```

Plan lines take a literal path (no shell expansion), so write it out.

It refuses the whole plan before running anything if a task file is missing or a model is not FREE. Run ids are unique across the plan and against what is on disk. At most `--concurrency` lemmings run at once. After a model reports `ProviderQuota` twice, its remaining runs are skipped, and no run is ever retried. If a runner exits 3 (shared daemon) or 4 (the F# tool would not build), the rest of the matrix is skipped. Each run's output goes to `<root>/.matrix-logs/<id>.log`. The end is a table of outcomes with a tally under it.

## Tests

```
dotnet run --project scripts/lemmings/LemScore.Tests -c Release
```

Expecto, 214 tests: the event parser and classifier on real captured streams (`LemScore.Tests/samples`, see its README for which are real and which are modelled), the catalog and the free-model guard, the daemon gate and cleanup scoping, summary.json, the version-skew comparison, the daemon-side path tripwire, run pruning, the matrix plan, scheduler, runner and table, and the plumbing: the sandbox arguments as data and in a real bubblewrap, the store of builds, every oracle against a scratch run, the workspace and its git repository, the editor sandbox, the VS Code window and profile, and the Claude Code harness against the output of the jq script it replaced. The F# is built on first use by `launch.fsx`, and rebuilt whenever a source file is newer than the dll. The Neovim driver has its own tests (`ui/nvim/NvimTests`), the VS Code driver too (`ui/LemDrive.Tests`).

## For the editor harnesses

`LemRun/CmdRun.fs` is the shared contract: the daemon gate (`useSharedDaemon`), `assertFreeModel`, `runCmdc`, `cleanupSessions`, `runOracle`, `score` and `finishRun`. `runCmdc` takes `Extras` (bubblewrap arguments, environment, stored builds to mount, whether to hide the global tools) appended after the core sandbox's, and an editor driver can write `out/fellover.extra.json` to add `Editor` findings to the summary. `NvimRun.fs` and `VscRun.fs` are the two editor runners built on it.

**The sandbox is the boundary, not the driver.** An editor driver (the `cmdc-nvim` and `cmdc-vscode` harnesses) typically offers the lemming a closed set of verbs, refuses absolute paths and `..` in `open`, and blocks commands such as stopping or restarting SageFs. That narrows what a lemming is steered towards. It does not bound what it can do: key presses, typed text and the command palette can still reach any other editor command, including an integrated terminal, and text and id matching is not an enforcement boundary. Whatever an editor process can do, it must only be able to do inside the same bubblewrap sandbox (`Extras.Bwrap` extends it, it does not replace it). An editor harness owner should confirm that the editor's own processes are launched with `--clearenv` and the same mounts as the cmdc process, and that its `ps` output shows it. The daemon-side holes above apply to editor lemmings unchanged.
