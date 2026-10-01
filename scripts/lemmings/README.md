# Lemmings

A lemming is a disposable trial. A cheap agent gets only what a new SageFs user has, does a small task, and I score what happened. The point is to find where SageFs falls over, not to find out whether the agent is clever.

The lemmings here are Command Code (`cmdc`) on free models. They are clients of the one shared SageFs daemon on 37749, the dev daemon I already have running, so I can watch every one of them in the dashboard at http://localhost:37750/dashboard. Nothing in this directory ever starts, stops, restarts or configures that daemon.

`run-lemming` and `score` are the older harness for Claude Code. They are still here and untouched. Everything below is the cmdc harness.

## Run one

```
scripts/lemmings/run-lemming-cmd <fixture> <task> <free-model> <run-id> [max-turns]

scripts/lemmings/run-lemming-cmd demoenv parse-seed stealth/space-bunny-alpha space-bunny-parse-seed-01
scripts/lemmings/run-lemming-cmd demoenv parse-seed stealth/space-bunny-alpha auto 40
scripts/lemmings/run-lemming-cmd sagefs-copy:/home/will/Work/SageFs sagefs-small-fix poolside/laguna-s-2.1-free auto
```

`auto` picks the next free `<model-short>-<task>-<nn>`. The run directory is `/tmp/lem/<run-id>` (override the root with `LEM_ROOT`), and the lemming works in `/tmp/lem/<run-id>/w`. Its sessions are keyed by that directory, so in the dashboard a lemming's sessions show a path with the run id in it, like `.../space-bunny-parse-seed-01/w`.

`/tmp` here is tmpfs, which has no reflink, so each run holds a real copy of the SageFs build (about 380 MB) and, for `sagefs-copy`, of the source tree. Point `LEM_ROOT` at a btrfs path if that matters.

## Models

Free models only. The runner reads the live `cmdc --list-models` and refuses (exit 2) any model whose description does not start with FREE, before it touches anything or calls an API. Right now that is:

- `poolside/laguna-s-2.1-free`
- `inclusionai/ling-3.0-flash-sante:free`
- `inclusionai/ling-3.1-flash:free`
- `stealth/space-bunny-alpha`

`stealth/pixel-canary` is not in the live catalog today, so it is refused too. Free models have provider quotas. A quota error is recorded as its own outcome, `ProviderQuota`, and nothing retries.

## Which SageFs the lemming gets

The daemon is mine and it is the master dev build. What each lemming gets of its own is the `sagefs mcp` stdio bridge in its MCP registration, written the way the README says (`command` + `args: ["mcp"]`), with `SAGEFS_MCP_PORT=37749` so it attaches to the running daemon instead of starting one.

- default: `<main checkout>/SageFs/bin/Release/net11.0/SageFs.dll`, copied into the run dir, so a rebuild cannot change a trial that is running
- `SAGEFS_LEMMING_BIN=published`: the global tool `sagefs`, for a baseline
- `SAGEFS_LEMMING_BIN=<path>`: a `SageFs.dll`, or a directory holding one

summary.json records both, for example `daemon 0.6.875+d7e11077...; bridge dev build: 0.6.875+d7e11077...`.

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

```
/tmp/lem/<run-id>/
  w/                        the lemming's working directory (a git repo, tag lem-baseline, no remote)
  bin/sagefs/               the copy of the SageFs build the bridge runs from
  cmdchome/                 the lemming's private ~/.commandcode (credentials bound over it, read-only)
  out/
    summary.json            the verdict
    events.ndjson           cmdc's event stream
    cmdc.stderr
    prompt.md
    residue.json            sessions found under the run dir after the lemming exited, and how they were stopped
    sessions.seen.json      sessions the dashboard API showed under the run dir while it ran
    changed.txt             files changed relative to lem-baseline
    oracle.out              what the oracle printed
    sbx/ps.txt              processes still alive in the sandbox when cmdc exited
```

summary.json fields: `id`, `model`, `harness`, `sagefsVersion`, `task`, `outcome`, `outcomeReason`, `providerError`, `seconds`, `turns`, `cmdcExit`, `toolCalls` (by name), `sagefsMcp` (calls, errorCount, evalFailures, inputRepairs, callSequence, and every error with its text), `oracle`, `residue` (sessions created, left behind, seen in the dashboard, processes left, lease calls), `teardown` (how cmdc ended, how the session cleanup went), `dashboardUrl`, `daemon` (memory pressure and available memory at start and end), `verification.changedFiles`, `fellOver`, `finalText`.

`fellOver` is a list of `{stage, symptom, evidence}`. Stages: Preflight, Registration, Adoption, SessionCreate, SessionWarmup, Eval, Reload, Tests, Lease, Cleanup, Budget, ToolSurface, Oracle, Editor, OtherTool. It is read off the stream: SageFs errors placed by the tool that failed, a faulted session, the same call three times in a row, reaching for `dotnet build` before a REPL eval, never calling SageFs at all, Command Code having to repair a tool input, sessions left behind, running out of turns, the oracle failing. An editor driver can add its own entries in `out/fellover.extra.json`.

## Isolation

Each lemming runs under bubblewrap: the toolchain read-only, a tmpfs home, a cleared environment, its own pid, ipc and uts namespaces (so it cannot see or signal the daemon's process, or any other process on the machine), and only its own working directory, a scratch dir and the bridge copy mounted. The credentials file is bound read-only over `~/.commandcode/auth.json` in a private config dir. Nothing from the real desktop is passed in: no `DISPLAY`, no `WAYLAND_DISPLAY`, no `XDG_*`.

The network is not isolated, because the model API and the bridge's localhost connection both need it. That means a lemming could in principle reach the daemon's HTTP API. The prompt never mentions it and the process is out of reach, but the HTTP surface is not walled off. The oracle runs lemming-written code too, so oracles run it through `lem_sandbox_exec`: the same sandbox with no network and no credentials.

Cleanup reads `GET /api/sessions` first and records what is there, then stops exactly the sessions whose working directory or loaded project is under this run's directory, by id, through `POST /api/sessions/stop`, then reads the list again. It never matches anything else, and it refuses a run directory that is not absolute and at least three segments deep. Note the sessions list is served on the MCP port, 37749. The dashboard port, 37750, answers 404 for it.

Before a lemming starts, the harness checks `/health` and `get_daemon_status`: the daemon must be healthy, `memoryPressure` must be `normal` and the machine must have more than 8 GB available. It waits, bounded, and then refuses. It never starts a daemon. If the daemon dies mid-run, the bridge in the sandbox would try to start one, which lives and dies inside that sandbox.

## Fixtures and tasks

Fixtures live in `fixtures/<name>`. `demoenv` is a small F# library with an Expecto project, and `parse-seed` asks for a one-line fix in it.

`sagefs-copy:<path>` copies a SageFs checkout or worktree for lemmings that work on SageFs itself: tracked files only (so none of my untracked notes), no `.git` (a fresh repo is made with a baseline commit and no remote), no `AGENTS.md`, `CLAUDE.md`, `.claude`, `.commandcode` or `.mcp.json`, and the build output of the projects in `LEM_COPY_BUILD` (default `SageFs.Core`) so a session loads quickly. The checkout it copies from is never touched.

Tasks live in `tasks/<name>.md`, and the file is the prompt:

- `parse-seed`: fix `DemoEnv.parseSeed` so a negative integer gives `None` (fixture `demoenv`). Oracle: the project's own Expecto suite passes in the sandbox and the tests were not edited.
- `sagefs-small-fix`: some `RingBuffer` tests fail, fix `SageFs.Core/RingBuffer.fs` and only that file. A `tasks/sagefs-small-fix.setup.sh` seeds a one character off-by-one into the copy before the baseline commit (the shape of the real bug class, and the existing tests catch it). Oracle: the diff touches only that file, and `oracles/RingBufferOracle` compiles the lemming's `RingBuffer.fs` with the repo's own `RingBufferTests.fs` and runs it without network.
- `sagefs-repl-eval`: use the REPL to answer a question about `RingBuffer` at runtime and write `ANSWER.md`. No edits. Oracle: the four values are right, checked with regexes, and no source file changed.
- `smoke`: one `get_daemon_status` call. Oracle: the call completed in the stream.

### Add a task

1. Write `tasks/<name>.md`, as a user would write it. Do not explain SageFs in it.
2. Write `oracles/<name>.sh` from `oracles/_template.sh` and `chmod +x` it. No executable oracle means `HarnessError`, never a pass. Anything that runs the lemming's code goes through `lem_sandbox_exec`. Logic bigger than a few lines goes in F# next to it.
3. If the task needs the fixture changed first, add `tasks/<name>.setup.sh <workdir>`. It runs before the baseline commit.

### Add a fixture

Drop a directory in `fixtures/<name>` with the project as a new user would have it. It is copied into `w`, made a git repo, and the skill and `.mcp.json` are added.

To check a fixture or a setup script without calling a model: `LEM_PREPARE_ONLY=1 scripts/lemmings/run-lemming-cmd <fixture> <task> <model> <id>`.

## Matrix

```
scripts/lemmings/run-matrix <plan-file|-> [--concurrency 3] [--max-turns 60] [--quota-limit 2] [--root /tmp/lem] [--dry-run]
```

A plan is one run per line, `<fixture> <task> <model> [reps]`, with `#` comments:

```
demoenv parse-seed stealth/space-bunny-alpha 3
demoenv parse-seed poolside/laguna-s-2.1-free 3
sagefs-copy:/home/will/Work/SageFs sagefs-small-fix stealth/space-bunny-alpha
```

It refuses the whole plan before running anything if a task file is missing or a model is not FREE. Run ids are unique across the plan and against what is on disk. At most `--concurrency` lemmings run at once. After a model reports `ProviderQuota` twice, its remaining runs are skipped, and no run is ever retried. If a runner exits 3 (shared daemon) or 4 (the F# tool would not build), the rest of the matrix is skipped. Each run's output goes to `<root>/.matrix-logs/<id>.log`. The end is a table of outcomes with a tally under it.

## Tests

```
dotnet run --project scripts/lemmings/LemScore.Tests -c Release
```

Expecto, 97 tests: the event parser and classifier on real captured streams (`LemScore.Tests/samples`, see its README for which are real and which are modelled), the catalog and the free-model guard, the daemon gate and cleanup scoping, summary.json, and the matrix plan, scheduler and table. The F# is built on first use by `lib-cmd.sh`, and rebuilt whenever a source file is newer than the dll.

## For the editor harnesses

`lib-cmd.sh` is the shared contract, and its header lists every function. `lem_run_cmdc` honours `LEM_EXTRA_BWRAP` and `LEM_EXTRA_ENV`, and an editor driver can write `out/fellover.extra.json` to add `Editor` findings to the summary.
