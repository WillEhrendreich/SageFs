# A REPL guard for agents other than Claude

Roadmap item: `repl-guard-for-other-agents` (Exploring, Agents). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: the Claude adapter's output matches today's, byte for byte. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## Work order

Written 2026-11-11 against master 8975e898. Every `path:line` below was read on that commit; the sections under this one are the original plan, corrected where it had gone stale.

### 1. Outcome

An agent in Codex CLI, Copilot CLI, Cursor or OpenCode that types `dotnet build`, `test`, `run` or `fsi` in an F# repo with a Ready session is stopped with the text Claude Code's agents get, with the same hatch (`SAGEFS_FINAL_GATE=1`), allowlist (`.SageFs/repl-guard.json`) and advise mode (`SAGEFS_REPL_GUARD=advise`), and `sagefs hooks install --host <h>` writes that host's hook config. Today only Claude Code has the hook (`tools/agent-hooks/`); every other host has `AGENTS.md` prose.

Outcome gate. `HostAdapterTests` (default suite) pins the Claude output byte for byte and parses one recorded stdin fixture per host. The real-host run is an on-demand dedicated suite (trap 1) that drives each installed host without a terminal and reads the hook's own log after the process exits. Linux only, and each host's hook shape is a fact only after step 1 records it. All five candidate hosts are installed on this machine today as mise shims: `codex`, `copilot`, `cursor-agent`, `opencode`, `claude`, so step 1 can run now.

### 2. Where the code is today

- `tools/agent-hooks/SageFs.AgentHooks.fsproj:25-28` compiles `HatchUse.fs` (linked from Core), `ReplGuard.fs` (572 lines), `Verdict.fs` (146), `Hook.fs` (129). `publish.fsx` publishes it single-file ReadyToRun as `sagefs-repl-guard` in that folder; `settings.json` points at that path (README). `SageFs.Tests/SageFs.Tests.fsproj:1082-1083` links `ReplGuard.fs` and `Verdict.fs`, so the tests compile the real code.
- The Claude seams, all in `tools/agent-hooks/Verdict.fs`: `parseInput` reads `tool_input.command` and `cwd` (`:53`); `denyJson` writes `hookSpecificOutput.permissionDecision = "deny"` (`:103`); `adviceJson` writes `additionalContext` (`:111`); `verdict world input` (`:118`) is the whole run; `World` (`:16-28`) injects env, repo config, project scan, health, lease and the hatch report.
- Constants in `tools/agent-hooks/ReplGuard.fs`: `FinalGateVariable = "SAGEFS_FINAL_GATE"` (`:141`), `GuardMode.Variable = "SAGEFS_REPL_GUARD"` (`:155`), `ConfigDirectory = ".SageFs"` (`:184`), `ConfigFileName = "repl-guard.json"` (`:187`).
- IO in `tools/agent-hooks/Hook.fs`: `GET /health` (`:49-58`), `POST /api/lease/request` (`:77`), `POST /api/hatch/record` (`:102`), port 37749 or `SAGEFS_MCP_PORT` (`:17,43`). The daemon side is `SageFs/McpServer.fs:3731` and `HatchUse.tryParse` (`SageFs.Core/HatchUse.fs:80`). `HatchUse.all` is 2 x 3 = 6 combinations (`:67`).
- Tests: `SageFs.Tests/ReplGuardTests.fs` (the decision) and `ReplGuardConfigTests.fs`, which drives `Verdict.verdict` with the stdin JSON, a real temp checkout and a fake daemon (`:8,85`). `ReleaseRulesTests.fs:478` asserts the old `sagefs-repl-guard.fsx` is gone.
- Docs: `docs/agents.md` section 4 (`:239-272`) and `tools/agent-hooks/README.md`. `scripts/install-hooks.fsx` installs the git pre-push hook, not this one.
- Neither `sagefs.nvim` (`grep -rn hatch ~/Work/sagefs.nvim/lua`: nothing) nor `sagefs-vscode/src` (one unrelated comment, `ProjectPickPure.fs:70`) reads the hatch count, so a `Host` dimension on it is `Plugin: n/a`.
- Tiers: `Integration.dedicatedEntryPoints` (`SageFs.Tests/TestInfrastructure.fs:669`), `isBareFlagEntryPoint` (`:681`), `dedicatedCase entryPoint name body` (`:529`). `SageFs.Tests/TrustSignalTests.fs:94-101` fails the default suite when a bare-flag dedicated entry point is not invoked by `ci-pipeline.fsx`. An on-demand suite has a payload with a space in it (the VS Code example in the comment at `TestInfrastructure.fs:674-680`) and runs only under `--all`.
- Fixtures: `SageFs.Tests/fixtures/` exists; `tests/fixtures/` (named below) does not. No host payload has been recorded; the table below is from summarised docs.
- No `HostAdapter` exists.

### 3. Steps

1. **Capture, no product code.** `scripts/capture-host-hooks.fsx`, thin glue: for each host, write a logging-only hook config into a throwaway HOME, run the host once with a prompt that runs `echo` through its shell tool, copy the raw stdin JSON to `SageFs.Tests/fixtures/hosts/<host>/<case>.json`. Then by hand, per host: does `deny` block, does the agent see the reason, which cwd and env the hook gets, hook latency over ten runs, whether the host wants the hook trusted first. Replace the table below with what you saw. Commit the fixtures: `test(hooks): recorded hook payloads from codex, copilot, cursor-agent and opencode`.
2. **Pure, a refactor.** RED in `SageFs.Tests/HostAdapterTests.fs` (after `ReplGuardConfigTests.fs`, `SageFs.Tests/SageFs.Tests.fsproj:1085`): golden strings of today's `denyJson "r"` and `adviceJson "r"`, captured before any edit; `HostAdapter.render ClaudeCode (DenyWith "r")` must equal them. Then `tools/agent-hooks/HostAdapter.fs` between `ReplGuard.fs` and `Verdict.fs` in both project files: `Host`, `HostCall`, `HostCaller`, `HostReply`, `parse`, `render`, Claude branch only; `Verdict.decide : World -> HostCall -> HostReply`; `verdict` becomes parse, decide, render. Green on its own; `ReplGuardConfigTests` is untouched.
3. **Pure.** Codex and Copilot branches from the step 1 fixtures; the parse round-trip property for the Claude-shaped hosts; "every `Decision` maps to one reply per host, valid JSON of the recorded accepted shape". A source-scan test with budget zero: `ReplGuard.fs` and `Verdict.fs` name no host.
4. **Pure plus a template.** The Cursor branch; the OpenCode plugin as a template string (`HostAdapter.openCodePlugin`) that spawns `sagefs-repl-guard --host opencode`. `Hook.fs` parses `--host` into the closed `Host` DU; an unknown value fails open with one stderr line.
5. **Edge.** `sagefs hooks install --host <h> [--print]` in `SageFs/Program.fs` beside `hygiene` (`:168`): writes a file it did not create only after showing the diff, or prints with `--print`. Docs: `docs/agents.md` section 4, the README, `skills/sagefs/troubleshooting.md`.
6. **Edge.** A `Host` dimension on `HatchUse` (closed DU; `all` becomes 6 times the hosts; a third wire token with Claude as the default for an old hook). `Plugin: n/a status field no editor reads`.
7. **On-demand suite.** `SageFs.Tests/HostJourneyTests.fs` registered with `Integration.dedicatedCase "--all (on demand: codex, copilot, cursor-agent, opencode and claude on PATH, each logged in)"`: one case per host per matrix row, reading `SAGEFS_HOOK_LOG` lines after the host exits. A missing host binary fails its case with an install hint.

`DECISION:` which hosts first. Recommendation: Codex and Copilot (closest shape), then Cursor, OpenCode last. `DECISION:` the real-host run as an on-demand `--all` suite, or a bare-flag tier that `ci-pipeline.fsx` must invoke and that makes every gate need five logged-in agent CLIs. Recommendation: on demand. `DECISION:` the PATH shim. Recommendation: not until step 1's nested-call count says so. `DECISION:` `Host` on the hatch count. Recommendation: yes.

### 4. How to check it

- REPL first: `create_project_session` on `SageFs.Tests` loads `ReplGuard.fs` and `Verdict.fs` through the link; eval `HostAdapter.render` against the golden strings there before writing the file.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`. After any hook change, `dotnet fsi tools/agent-hooks/publish.fsx`, because the published binary is what Claude Code runs.
- Inner loop: `--filter-test-list "HostAdapter"`; the guard's own lists are named in `ReplGuardTests.fs` and `ReplGuardConfigTests.fs` (read them first; `--filter-test-case` on a list-name token matches nothing and exits 0).
- Acceptance: the unfiltered `--summary` run. The on-demand run by hand before a ship: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --all --filter-test-list "HostJourney"` as the loop, `--all` whole as the gate.

### 5. Traps in this repo

- A bare-flag dedicated entry point not invoked by `ci-pipeline.fsx` turns `TrustSignalTests` red (`:94-101`), and the host run needs installed, logged-in CLIs no gate machine should need. Register it on demand (payload with a space) and say so in the plan.
- Fail-open on a parse problem hides a changed host format. Count `ParseProblem`s into the hatch report so `get_daemon_status` shows them.
- SAGE-005: `parse (host: Host) (stdin: string)` is fine; `render (reason: string) (context: string)` is not. The hook project is outside the analyzer's roots (`SageFs.Core`, `SageFs`, `SageFs.Host`), but `HatchUse.fs` is in Core, so step 6 counts.
- The hook must start fast: nothing in `HostAdapter.fs` may reference Core or spawn a process (README: about 15 ms against 1.3 s for `dotnet fsi`).
- A fixture's recorded accepted output is the host's schema; a test reads it, it does not restate it.
- Child processes in steps 1 and 7: `SpawnEnvironment.apply`.
- Commit `tools/agent-hooks/SageFs.AgentHooks.fsproj` and `SageFs.Tests.fsproj` whole, from a clean tree.

### 6. Size and order

Step 1 one day. Step 2 half a day. Steps 3 to 5 three to four days. Steps 6 and 7 two days. Step 1 before everything. Steps 3 and 4 can split by host if each host is its own file and one match in `HostAdapter.fs` is merged by hand. First product commit: `test(hooks): RED the Claude adapter renders byte-for-byte what denyJson and adviceJson print today (repl-guard-for-other-agents step 2)`.

### 7. Not now

- The PATH shim.
- Confirmation gates and scope checks in other hosts (`confirmation-gates-in-every-agent-host`, `check-the-scope-before-the-write`): they reuse step 1's fixtures and step 2's adapter, so they wait for step 2.
- Aider, Zed, Cline, Gemini CLI, Windsurf.
- The operator credential on the hook's two HTTP calls (`a-token-for-every-daemon-call`).

## What done looks like

In a host other than Claude Code (the candidates are Codex CLI, GitHub Copilot CLI, Cursor, OpenCode), an agent that types `dotnet build`, `test`, `run` or `fsi` in an F# repo while a SageFs session is Ready is stopped with the same message Claude Code's agents get, and the same escape hatch (`SAGEFS_FINAL_GATE=1`), allowlist (`.SageFs/repl-guard.json`) and advise mode (`SAGEFS_REPL_GUARD=advise`) apply. Nothing about the decision is copied per host: one pure decision, one thin adapter per host. The outcome gate runs each host for real over the matrix: host by command class (slow verb with a Ready session, same with no session, declared final gate, Release build, pack, non-F# directory, daemon down, Faulted session) and asserts allowed or blocked and that the text reaches the agent. A host that cannot be run on the machine fails the tier with an instruction to install it; it does not pass silently.

For hosts with no hook system at all (anything that only reads an `AGENTS.md`), this plan states what stays advisory, and tests that the advice is present. It does not claim enforcement there.

## What exists today

- `tools/agent-hooks/` is one project, `SageFs.AgentHooks.fsproj`, assembly `sagefs-repl-guard`, published single-file ReadyToRun by `publish.fsx`. It compiles `SageFs.Core/HatchUse.fs` by link (`<Compile Include="../../SageFs.Core/HatchUse.fs" Link="HatchUse.fs" />`) because it must start fast and cannot reference Core. A PreToolUse hook runs before every Bash call; the project's comment says `dotnet fsi` start-up is over a second a call.
- `ReplGuard.fs` (572 lines) is the pure part: `classify`, `classifyWith`, `decideWith`, `needsContextWith`, `hatchReportWith`, `segments` (a shell tokenizer), `Policy.build`, `GuardMode` (`Enforcing | Advising`), `Allowlist`, `RepoConfig`, `sessionProbe`, `SlowVerb` (`Build | Test | Run | Fsi`), `Decision` (`Allow | AllowWithAdvice | Deny`).
- `Verdict.fs` (146 lines) is `verdict : World -> string -> string option`, with a `World` record of injected effects (`GetEnv`, `ReadRepoConfig`, `ProjectScope`, `Health`, `Lease`, `ReportHatch`). The two Claude-specific seams are in this file: `parseInput` reads `tool_input.command` and `cwd` from the stdin JSON, and `denyJson` and `adviceJson` write `hookSpecificOutput` with `hookEventName = "PreToolUse"`, `permissionDecision`, `permissionDecisionReason`, `additionalContext`.
- `Hook.fs` (129 lines) is the IO: stdin, `/health`, `POST /api/lease/request`, `POST /api/hatch/record`, stdout. It uses port 37749 or `SAGEFS_MCP_PORT`.
- Tests: `SageFs.Tests/ReplGuardTests.fs`, `ReplGuardConfigTests.fs`. `docs/agents.md` section 4 and `tools/agent-hooks/README.md` describe behaviour and install for Claude Code only.
- Other hosts, from their own docs read on 2026-10-26 (first pages only, and each fetch summarised by a small model, so treat the field names as leads to confirm by running the host):

| Host | Hook that sees a shell command | Input | Block | Notes |
|:--|:--|:--|:--|:--|
| Claude Code | `PreToolUse`, matcher `Bash` | `tool_name`, `tool_input.command`, `cwd`, `session_id`, `agent_id` in sub-agents | `permissionDecision: deny` | built, the baseline |
| Codex CLI | `PreToolUse`, matcher `Bash` | Claude-shaped per its docs (matchers `Bash`, `^apply_patch$`, `Edit\|Write`, `mcp__server__.*`) | `permissionDecision: deny`, or exit code 2 | hooks on by default, "ask" is parsed but unsupported and the tool call proceeds, so a guard here can only deny or advise; non-managed hooks must be reviewed and trusted before they run |
| Copilot CLI | `preToolUse` | camelCase `toolName`, `toolArgs`, `cwd`, or PascalCase `tool_name`, `tool_input` with Claude-style matchers | `permissionDecision: deny` with a required reason; `modifiedArgs` exists | command hooks fail closed on a crash or non-zero exit and fail open on a timeout |
| Cursor | `beforeShellExecution` | `command`, `cwd`, `sandbox` | `permission: allow\|deny\|ask`, and an `agent_message` | exact output field names not read |
| OpenCode | plugin `tool.execute.before` | `input.tool === "bash"`, `output.args.command` | throw an `Error` | a TypeScript plugin, not a command; the page never says it fires for every tool |
| Anything else (Aider, Zed, Cline, Gemini CLI, Windsurf) | not checked | | | `AGENTS.md` advice only |

## Design

Split `Verdict.fs` into the decision and the host edge, then add adapters. The decision does not change.

```fsharp
// tools/agent-hooks/HostAdapter.fs
[<RequireQualifiedAccess>]
type Host = ClaudeCode | Codex | CopilotCli | Cursor

type HostCall =
  { Command: string
    Cwd: string
    /// Who the host says is calling, when it says: a sub-agent id or session id. Carried for later items.
    Caller: HostCaller }

and HostCaller = NoCallerNamed | SubAgent of agentId: string * agentType: string | Session of sessionId: string

type HostReply =
  | Silent
  | DenyWith of reason: string
  | AdviseWith of reminder: string

module HostAdapter =
  let parse (host: Host) (stdin: string) : Result<HostCall, ParseProblem>
  let render (host: Host) (reply: HostReply) : string
```

`parse` and `render` are total over every `Host` case with one exhaustive match each; a new host breaks the build until both are written. A parse failure is a typed `ParseProblem` and the hook fails open with a line on stderr naming the host and the field that was missing, because blocking every command on a host whose JSON shape changed is worse than not guarding. Fail-open here is the same posture the hook already has for a daemon that does not answer. The decision step becomes `Verdict.decide : World -> HostCall -> HostReply`. `parseInput`, `denyJson` and `adviceJson` are replaced by the `ClaudeCode` branch of the adapter, and their outputs stay byte-identical, which a snapshot test pins before the move.

Host selection: the binary takes `--host claude|codex|copilot|cursor` in its command line in the host's hook config. No auto-detection from the JSON, because a wrong guess silently applies the wrong output shape. OpenCode is a TypeScript plugin and cannot run the binary through a hook field; its shell is a 30-line plugin that spawns `sagefs-repl-guard --host opencode` with a JSON line on stdin and throws on a deny reply, so the decision still lives in the one binary. That shell is the only non-F# file, and it is generated from a template in the repo, not hand-edited per user.

Cursor's `ask` is not used: the guard denies or advises, as for every host, so the behaviour is the same everywhere.

Installation: `sagefs hooks install --host codex` writes the host's hook config (a JSON or TOML file, depending on the host) and prints the path; `--print` shows it without writing. It does not edit a file it did not create without showing the diff. This is a CLI verb in `SageFs/Program.fs` next to `sagefs hygiene`, no MCP tool, no catalog cost.

PATH shim, as an alternative for hosts with no hook: a script named `dotnet` earlier on the agent's `PATH` that runs `sagefs-repl-guard --host shell` on `$*` and then execs the real `dotnet`. It sees nested calls the host hooks never see (`ship.fsx` runs `dotnet build`). It would also stop the person's own scripts, so the gate scripts must set `SAGEFS_FINAL_GATE=1`, and a shim installed in a login shell would fire for a human. Not planned for the first version; the experiment below says whether it is worth it.

Wire and client impact: none on the daemon. The hook's two HTTP calls (`/api/lease/request`, `/api/hatch/record`) stay as they are; once `a-token-for-every-daemon-call` lands, each hook sends the operator credential from the data dir file, which is a one-line change in the shared hook code. `hatchReport` gains a `Host` field so `get_daemon_status` can show which host fell back; the six combinations in `HatchUse.fs` become twelve or more with a host dimension, so add the field as a closed DU there and keep the count rows backward readable. That is a payload change to `get_daemon_status`: `Plugin: n/a, status field no editor reads` after checking `sagefs.nvim` for `hatch` (grep it first).

## Tests

RED first.

1. `SageFs.Tests/HostAdapterTests.fs` (new): `WHY - the Claude adapter renders byte-for-byte what the hook printed before the split`. Golden strings captured from the current `denyJson` and `adviceJson` before any edit. Fails to compile today.
2. Fixtures from real runs: one recorded stdin JSON per host per case (`SageFs.Tests/fixtures/hosts/<host>/<case>.json`; `SageFs.Tests/fixtures/` is the directory that exists), captured by the capture step (Steps, 1). `WHY - each host's recorded shell call parses to the command and directory the agent typed` for all hosts. Property over the Claude and Copilot adapters: for any command string and cwd, `parse (render-input command cwd)` round-trips.
3. `WHY - every Decision maps to exactly one reply per host and each reply is valid JSON of that host's shape`. The host's output schema is read from the fixture's recorded accepted output (the real run proves the host accepted it).
4. `WHY - a Deny reply for Codex and Copilot is also exit code 2 or carries a reason`: the documented block forms.
5. The decision tests in `ReplGuardTests.fs` stay as they are and run against `Verdict.decide`; a test asserts the repl-guard decision code never mentions a host name (a source scan, budget zero), so adapters stay the only host-aware code.
6. Simulation: none new. The decision already has seeded coverage in `ReplGuardTests`; the adapters are pure total functions covered by the properties above. If the shim is built, it needs a sim of the nested-call case (`ship.fsx` running `dotnet build` with and without the hatch variable) with a twin that forgets to read the variable.
7. Outcome gate, an on-demand dedicated suite (a bare-flag tier such as `--integration-hosts` would have to be invoked by `ci-pipeline.fsx` or `TrustSignalTests` goes red; see the work order's trap 1): for each host installed on the machine, a scripted non-interactive run (`codex exec`, `copilot -p`, `cursor-agent -p`, `opencode run`, `claude -p`) with a prompt that asks the agent to run `dotnet build` in a throwaway F# repo with a test daemon and a Ready session. Pass criteria are read from files after the process exits: the hook's own log line (one JSON line per verdict, written by the hook when `SAGEFS_HOOK_LOG` names a file) and the daemon's `hatch` counts. The matrix rows above are separate prompts. A missing host binary fails the tier with an install hint. This tier is local-first (run on the desktop before a ship; CI only checks that the fixtures and adapters agree), consistent with how the demo recordings work.

No wall-clock waits: each run ends when the host process exits; assertions read the log file.

## Steps

1. Capture, no product code: a script `scripts/capture-host-hooks.fsx` that installs a logging-only hook into each host and records the raw stdin JSON and the effect of each output shape (does the host honour `deny`, does the agent see the reason). Output goes to `SageFs.Tests/fixtures/hosts/`. This also settles the "none of the four hosts has been run" caveat shared with `confirmation-gates-in-every-agent-host` and `check-the-scope-before-the-write`, and its fixtures serve all three.
2. Test 1 (golden for the Claude output), then the `HostAdapter.fs` split with the Claude branch only. Owns `tools/agent-hooks/Verdict.fs`, new `HostAdapter.fs`, the fsproj, `HostAdapterTests.fs`. Behaviour unchanged, so this commit is a refactor and green on its own.
3. Codex and Copilot adapters (closest to Claude's shape). Owns the two branches and their fixtures. Parallel with 4 only if the owners add separate files (`HostAdapter.Codex.fs`, etc.) and a single match in `HostAdapter.fs` is edited by the orchestrator at merge.
4. Cursor adapter, then the OpenCode plugin template and its runner.
5. `sagefs hooks install` and docs: `docs/agents.md` section 4, `tools/agent-hooks/README.md`, `skills/sagefs/troubleshooting.md`.
6. The `--integration-hosts` tier and the `Host` dimension on the hatch count.

## Measure first

- The capture step is the measurement. For each host, record: the exact stdin JSON for a shell call; whether `deny` blocks and whether the agent sees the reason text; the working directory the hook runs in; the environment variables the hook inherits (does `SAGEFS_FINAL_GATE` in the command prefix reach the hook text; it does for Claude because the hook parses the command).
- Hook latency per host, since the guard runs before every shell call: time a trivial command with and without the hook, ten runs. The Claude baseline is the published single-file R2R binary; if a host launches hooks through a shell wrapper the cost may differ.
- Whether Copilot CLI and Cursor trust or sandbox hooks (Codex needs the hook reviewed and trusted before it runs; that is a first-run step the installer must say out loud).
- Whether the PATH shim is needed: count, in a day of work in a non-Claude host, how many slow-loop calls come from a nested script rather than the host's own shell tool.

## Risks and dependencies

- The field names above come from summarised documentation. Step 1 replaces them with recorded facts, and until then the adapters are drafts.
- Fail-open on a parse problem means a changed host format silently turns the guard off; the `--integration-hosts` tier is what notices, and it is local-only. A cheap second signal: the hook counts `ParseProblem`s into the hatch report so `get_daemon_status` shows them.
- Hooks on hosts differ in trust: a hook the user has not approved does not run, so "installed" does not mean "enforcing". The installer prints the host's approval step.
- Depends on nothing. Shares step 1 and the adapter layer with `confirmation-gates-in-every-agent-host` and `check-the-scope-before-the-write`; this item should go first because it creates both. Touches `tools/agent-hooks/` which those two also extend, so they wait for step 2.

## Size

M. The refactor and Codex and Copilot adapters are days; four hosts, the installer and the local tier make it a week or two.

## Decisions for Will

- Which hosts count? Recommendation: Codex and Copilot CLI first (their hook shapes are closest to Claude's), Cursor second, OpenCode last because it needs a TypeScript shell.
- Build the PATH shim? Recommendation: not until the nested-call count says so, since it affects your own shell.
- A host-aware line in the hatch report: acceptable change to `get_daemon_status`? Recommendation: yes.
