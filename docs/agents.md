# Using SageFs with AI agents

If you give an agent SageFs and don't tell it how to use it, it'll do what
agents do in every .NET repo: edit, `dotnet build`, wait, `dotnet test`, wait,
repeat. That's the slow loop SageFs exists to get rid of. An eval in a warm
SageFs session takes milliseconds. A build takes tens of seconds, and a test run
takes longer. The difference isn't small. It's most of the day.

So this page is about making the agent actually use the REPL, and pulling it
back when it drifts. I've watched my own agents drift more than once, so none
of this is hypothetical.

## The rule

**The SageFs REPL is the inner loop. `dotnet build` / `dotnet test` is the final
gate, run once when the work is done.**

The loop:
1. Show the bug in the REPL.
2. Fix it in the REPL.
3. Write the fix into the file.
4. Reload with `hard_reset_fsi_session rebuild=true`.
5. Check it again in the REPL.
6. Commit.
7. Run the full build and tests once, at the end.

## 1. Install the skill

The skill is a folder, [`skills/sagefs/`](../skills/sagefs/). The agent loads
[`SKILL.md`](../skills/sagefs/SKILL.md) on every F# task. It is short: the
first-minute checklist, the loop, the rules that bite before a first edit, and
a few lines for an agent that spawns other agents (see
[section 6](#6-after-a-day-of-sub-agents)).
The other files in the folder are read only when their trigger comes up:
- `design.md`: before designing a type, signature or module
- `testing-standards.md`: before writing the tests for a change
- `dst-capabilities.md`: before writing a simulation or mutation test
- `sessions.md`: choosing a session, checking the daemon version
- `loop.md`: why the REPL is the loop, and which tool answers what
- `editing.md`: proving a change before writing it, and the `#load` trap
- `testing.md`: running tests and slow gates
- `leases.md`: leases before a full build, test run or app run
- `troubleshooting.md`: stale daemons, busy versus broken, when the REPL fights you,
  and the "needs a person to confirm" wait (only `manage_local_data` asks; stopping and resetting sessions never does)
- `claude-code.md`: permission prompts and auto mode
- `agents.md`: briefing a sub-agent

**Claude Code:**
```bash
sagefs install-skill
```
The skill ships inside the tool, so the copy always matches the daemon you have. It writes
`~/.claude/skills/sagefs/` and replaces an older copy of itself whole, so a file that a later
build drops does not linger. A different skill that already uses the name `sagefs` is left
alone until you pass `--force`. A folder that is a link, for instance into a checkout of this
repo, is also left alone and follows its target. `--dir <folder>` installs into another skills
folder, for an agent that reads skills from somewhere else, or for one repo only
(`.claude/skills/` in that repo).

`sagefs check` has a row for it. It says whether the skill is installed, whether it is this
build's, and the one command that fixes it. The skill's version is a hash of its files, so
there is no number to forget to bump, and two copies have the same version exactly when
they hold the same files. Run `sagefs install-skill` again after you update SageFs.

**Other agents (Codex, Copilot, Cursor, OpenCode and so on):** most of them read
an `AGENTS.md` at the repo root. Add this to yours:

```markdown
## F# work: use SageFs

This repo is developed with SageFs. The SageFs REPL (MCP tools) is the inner
loop. `dotnet build` / `dotnet test` is only the final gate.
Before any F# change, read and follow
https://github.com/WillEhrendreich/SageFs/blob/master/skills/sagefs/SKILL.md

- Read `get_daemon_status`, use `get_available_projects`, then create the
  agent's own worktree session with `create_project_session`,
  `create_solution_session`, or `create_bare_session`. A worktree is its own
  routing boundary. Wait for that session's `get_session_status` to say Ready.
  If generated build state is missing, SageFs builds it before creating the
  session.
- Show the problem with `send_fsharp_code`, fix it there, write the fix to the
  file, run `hard_reset_fsi_session rebuild=true`, check it again, commit.
- If the REPL fights you, report the exact error. Don't quietly switch to
  dotnet.
- Never stop, restart or reinstall the SageFs daemon without asking.
- Run the slow gate in a background agent and keep working. Don't await it,
  don't poll it, and don't re-roll a full run to escape a flake.
- Never `#load` a file belonging to a project the session already has loaded.
  You get two copies of every type, and the error points at a type
  incompatibility rather than at the real cause. `#load` only a *pure* file.

**Changing code: prove it, then write it.** This is the rule that saves the
most time, and the easiest one to break under pressure.

- Prove the change in the REPL before you write it to a file. An eval takes
  under a second; a build that catches your misread of a type takes two
  minutes, and catches it *after* you have already edited.
- Edit with exact, targeted calls — read the region, replace that exact text.
  Not `sed -i`, not a `python3 -c` file rewrite, not a broad regex bulk edit. A
  scripted edit that silently matches nothing looks exactly like one that
  worked, and you find out at the worst possible moment.
- For a repeated mechanical change, write an `.fsx` and run it through SageFs,
  so the script is F# that reports its own result — and have it assert that
  each replacement matched, out loud, rather than exiting quietly.
  `SageFs.Editing.applyInOrder` is the built-in version: each replacement
  either applies or tells you why it didn't, and an ambiguous match refuses
  rather than guessing.
```

## 2. Let the agent call SageFs without asking every time

In Claude Code, allow the SageFs tools once, in `.claude/settings.json` (for one
repo) or `~/.claude/settings.json` (everywhere):

```json
{
  "permissions": {
    "allow": ["mcp__sagefs__*"]
  }
}
```

This matters more in auto mode. An allowed tool runs without going past the
classifier at all, while most `dotnet build`/`test`/`run` shell commands get
reviewed one at a time. So the REPL route is faster in two ways: the eval is
milliseconds instead of a build, and there's no approval step in between.
Details are in Claude Code's
[permissions](https://code.claude.com/docs/en/permissions.md) and
[permission modes](https://code.claude.com/docs/en/permission-modes.md) docs.

## 3. Connect the MCP server

Register the stdio bridge. In Claude Code that is one command, for every project:

```bash
claude mcp add --scope user sagefs -- sagefs mcp
```

`sagefs setup` prints the registration for each client it finds (Claude Code, VS Code, Cursor, Claude
Desktop), and `sagefs setup --client vscode --write` merges it into that client's config file, keeping
every other server in it and leaving a file it cannot parse alone. The snippet for each client, and why
the stdio form keeps its tools across a daemon restart while an HTTP one does not, are on
[client-setup.md](client-setup.md).

A client that can only speak HTTP can point at `http://localhost:37749/` (streamable HTTP), or
`http://localhost:37749/sse` for older clients. When an agent connects, SageFs
sends it a short version of the rules, so even an agent without the skill gets
the core of it. The skill is the full version, and you want both.

### When the client has no SageFs tools

A client configured with a URL asks for its tools once, when it connects, and holds one session id. A
daemon restart ends that session, and installing a build restarts the daemon, so the client is left with
no SageFs tools or a `404 Session not found` on every call until someone reloads it. A client that
started while the daemon was down never got the tools at all. The durable fix is on the client's side:
`claude mcp add sagefs -- sagefs mcp` registers the stdio bridge, which owns the handshake, so the client
never holds an id that can go stale.

Until the client is reconnected, `sagefs call` reaches the same tools from a shell, a script, a hook or
CI:

```bash
sagefs call --list                                     # each tool, and who you are calling as
sagefs call get_session_status --session_id 1a2b3c4d
sagefs call run_tests --pattern Foo --timeout_seconds 60
sagefs call stop_session --json '{"session_id":"1a2b3c4d"}'
```

Flags are typed from the tool's own input schema, and a name the tool does not have is refused with the
names it does. The answer is on stdout. A tool that answers with an error goes to stderr with exit code 1.

Each `sagefs call` is a new process, and the daemon ties a session to the connection that made it, so
with no token the next call cannot stop what the last one made. Mint a member token once and every call is
the same member:

```bash
eval "$(sagefs member mint --role Implementer --dir "$PWD" --print-env SAGEFS_MEMBER_TOKEN)"
```

`--scope` takes ONE directory prefix (`--scope src/Foo/`), and leaving it out means the whole repo. A
comma list is refused with the reason, because a token's scope is a single directory.

`--token-file <path>` (or `--token-file -` for standard input) does the same for one call without putting the token on
the command line. `--token <token>` still works and prints a warning. The full description is in
[mcp-tools.md](mcp-tools.md#reaching-the-tools-without-a-client-sagefs-call).

### What a client sees when the daemon is upgraded

A client registered with `sagefs mcp` keeps its connection across a daemon stop and start, which is what
`scripts/install-local.fsx` and a release do. Before 2026-11-07 the bridge ended with exit code 1 and
`sagefs mcp: the daemon became unreachable: server stream closed` on stderr the moment the daemon's push
stream closed, so the client showed the server as disconnected until someone reloaded it. Now:

- A request in flight when the daemon stops gets a JSON-RPC error that says the daemon became unreachable
  and to ask again in a few seconds. It is not left without an answer.
- A request that arrives while the daemon is down waits in the bridge and goes out, in order, when the daemon
  answers again. It is answered, or rejected with the reason if the bridge gives up (see the last point).
- When the daemon answers again the bridge opens a new MCP session on it with the `initialize` parameters
  your client sent, then writes `notifications/tools/list_changed`, `notifications/prompts/list_changed` and
  `notifications/resources/list_changed` to the client, because the daemon behind it is a new session and
  may be a new build. The client asks for the lists again. Sessions of the old daemon are gone, as they
  always were; `list_sessions` shows what the new daemon has.
- You are the same cohort member afterwards. The bridge sends the same random client key on every request, so the new
  MCP session is the same member as the old one, and a conductor stays the conductor through an upgrade. Before
  2026-11-07 each upgrade turned the conductor into a new member and the old one held the seat for up to 30 minutes.
  While the bridge is connected, its open push stream also keeps your seat's lease renewed, so a conductor that spends
  an hour waiting on its agents does not lose the seat. See "Which name the id is a fingerprint of" in
  [mcp-tools.md](mcp-tools.md#cohort-and-multi-agent-coordination).
- The bridge waits for the old daemon's replacement for up to a minute on a fast machine (`Timeouts.stdioBridgeReconnectWait`,
  longer on a slower one). After that it starts a daemon itself, the way it does when it finds none at
  startup, and gives up only by the rules of a cold start.
- A request that arrives while the bridge is minting that new session waits for it and goes out under the new
  session id, in order, after the push stream is open. The bridge forwards nothing under the old id once it has seen the
  daemon answer. Before 2026-11-08 such a request went out under the old id, got a 404, and the bridge's repair
  minted a second session: the client then received every notification twice, because both push streams stayed open. Now
  a new session replaces the old one's stream, and requests that are refused for the same old id share one repair.
- If the daemon cannot give the bridge a session (it answered without an id, or went away again), the requests
  that were waiting are answered with an error that names the reason, and the bridge goes back to waiting for a
  daemon. It does not hold them.
- A request the daemon has not answered within the bridge's own deadline (`Timeouts.workerHttpRequest`, ten minutes
  by default, so a long test run is fine) is answered with an error that says it timed out and may still be running.
  The bridge stays on its session. Before 2026-11-08 a timeout was treated as the daemon being gone, which started a
  needless re-handshake and left a live session behind.
- While the daemon is down or a session is being minted the bridge holds at most `Timeouts.stdioBridgeQueueCap` (1000)
  messages. The one past that is answered with an error that says the queue is full.

The installer replaces the tool's files under a running bridge. That does not end the bridge: a process
that already loaded its assemblies keeps them. Measured on 2026-11-07 against a throwaway daemon: the
bridge's version directory removed, then `tools/list` and `tools/call` still answered.

## 4. When the agent drifts

It'll happen. Usually the agent hits something awkward (a version mismatch, a
session that isn't ready yet) and quietly goes back to building. Three ways to
pull it back, from least to most enforced:

> If it was a version mismatch, check your own daemon before you blame the
> agent. `sagefs status` prints when it started, a daemon that's been up since
> before your last build is serving old code, and the agent was right that
> something was wrong, just not about what. See
> [stale daemon](TROUBLESHOOTING.md#stale-daemon--the-one-that-wastes-the-most-time).

- **Tell it.** "Back to the REPL" or "mandate 1". The skill tells the agent
  what those mean: stop, say where it left the loop, and resume from the REPL.
- **Use the prompt.** SageFs ships an MCP prompt, `back_to_the_repl`. In Claude
  Code that's the slash command `/mcp__sagefs__back_to_the_repl`. It puts the
  loop back in front of the agent in one keystroke.
- **Make drift impossible to miss.** [`tools/agent-hooks/`](../tools/agent-hooks/)
  has a Claude Code `PreToolUse` hook, `sagefs-repl-guard`. It stops `dotnet
  build`, `dotnet test`, `dotnet run` and `dotnet fsi` mid-task in an F# repo
  while SageFs is running, and tells the agent why. The final gate goes through
  with `SAGEFS_FINAL_GATE=1` in front of the command. Packaging and tool
  commands are never blocked, and neither is anything when SageFs isn't
  running. `dotnet build -c Release` is never blocked either, because a
  Release build checks things the REPL can't. A repository with its own build
  CLI (say `dotnet run --project Build.fsproj -- <command>`) lists it in
  `.SageFs/repl-guard.json`. If the REPL is a recommendation in your repository
  and not a rule, set `SAGEFS_REPL_GUARD=advise`: the command runs and the
  agent gets the reminder instead of a refusal. When the directory's session
  is Faulted there is no REPL to go back to, so the build runs and the agent
  is handed the session's own fault reason. The hook also reports each
  `dotnet build`, `test`, `run` or `fsi` it lets through to the daemon, and
  `get_daemon_status` shows the count under `hatch`. Details are in the
  [hook's README](../tools/agent-hooks/README.md).

## 5. When the REPL really is broken

Sometimes it is, and that's worth hearing about. The skill tells agents to
write down the exact error, try the obvious fix once, and only then fall back
for that one step, saying so. If your agent reports something like that,
please [open an issue](https://github.com/WillEhrendreich/SageFs/issues) with
the error. Every silent fallback is a bug that never gets fixed.

## 6. After a day of sub-agents

An orchestrator that hands work to sub-agents in their own git worktrees
collects leftovers: the worktrees, the merged branches, the gate's checkouts,
built FSI hosts, temp dirs, the odd orphaned process. They add up in disk
and memory.

Two MCP tools show it and tidy the part that is safe, and `sagefs hygiene`
does the same from a shell. The skill tells an orchestrator to look before it
spawns and tidy once the work has merged:

- `get_workspace_hygiene` is a dry run. It lists each leftover with its size,
  age and a standing that says why it is or isn't safe to reclaim, then a plan
  with an id.
- `tidy_workspace` runs only the safe part of that plan, and only with
  `confirm=true` and the plan id you were shown. Each step looks at its target
  again first. It never touches unmerged commits, uncommitted work, anything in
  use, or anything it couldn't judge.

The daemon does not wait for you to look. It sweeps each repository it knows at
start, every quarter of an hour and whenever a session stops: it records every
agent worktree in a ledger (who made it, when it went quiet), saves the
uncommitted files of a worktree nobody is using (a local `.env` included) to
`refs/sagefs/salvage/<name>`, a ref `git push --all` does not send, without
touching the worktree, and, with `hygiene.autoTidy` on (the default), removes
what is provably safe. A worktree it has never seen waits a full day from its
first sweep, and one that holds commits the base lacks, or changes, is never
removed. A checkout a cohort holds reads as in use, and a `<repo>.tiers`
directory that nothing uses is listed as needing a look, never removed.
`sagefs hygiene --rescue <name>` puts a worktree's work on `rescue/<name>`,
rebased on the base, and says whether the base already has it.

[`mcp-tools.md`](mcp-tools.md#workspace-hygiene) has the details. The brief you
give a sub-agent should say to remove nothing it doesn't own, to use no
worktree unless the work must not touch the trunk, and to report its worktree
path and branch, so the orchestrator knows what to reap.

## 7. When a ship fails its gate and you run it again

This one is for working on SageFs itself. `dotnet fsi scripts/ship.fsx` bumps
the version, commits it as `chore: release v<version>`, gates that commit
and pushes it. A failed gate leaves the bump commit on your local master. One
push is one release, so the pre-push hook refuses a second bump on top of the
first, and an old ship only found that out after another gate of about ten
minutes.

Fix what failed, commit the fix, and run the same command again. Before it
touches anything, plain `ship.fsx` counts the release commits in
`origin/master..HEAD`:

- **None.** It bumps, as always.
- **One.** It does not bump again. It prints which commit that is and its
  version, then gates and pushes HEAD with that bump in place. It reuses the
  bump only when its version is above the one on origin/master. Otherwise it
  stops and names the two versions it compared.
- **Two or more.** It stops before any ratchet or gate, names the commits, and
  prints the `git rebase --onto <sha>^ <sha>` line for each extra one. Run
  them as printed (the oldest stays, the rest go newest first), then run
  `ship.fsx` again.

The rule is `ReleaseRules.planBump` in
[`scripts/ReleaseRules.fs`](../scripts/ReleaseRules.fs). The pre-push hook
counts release commits with the same reader, so the two cannot disagree about
what a release commit is. `ship.fsx -- <commit>` still gates and pushes a
given commit without bumping.
