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
- `sessions.md`: choosing a session, checking the daemon version
- `loop.md`: why the REPL is the loop, and which tool answers what
- `editing.md`: proving a change before writing it, and the `#load` trap
- `testing.md`: running tests and slow gates
- `leases.md`: leases before a full build, test run or app run
- `troubleshooting.md`: stale daemons, busy versus broken, when the REPL fights you
- `claude-code.md`: permission prompts and auto mode
- `agents.md`: briefing a sub-agent

**Claude Code:**
```bash
mkdir -p ~/.claude/skills/sagefs
for f in SKILL sessions loop editing testing leases troubleshooting claude-code agents; do
  curl -fsSL "https://raw.githubusercontent.com/WillEhrendreich/SageFs/master/skills/sagefs/$f.md" \
    -o ~/.claude/skills/sagefs/$f.md
done
```
For one repo only, put the folder in that repo's `.claude/skills/sagefs/`
instead.

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

Point your agent at `http://localhost:37749/` (streamable HTTP), or
`http://localhost:37749/sse` for older clients. When an agent connects, SageFs
sends it a short version of the rules, so even an agent without the skill gets
the core of it. The skill is the full version, and you want both.

## 4. When the agent drifts

It'll happen. Usually the agent hits something awkward (a version mismatch, a
session that isn't ready yet) and quietly goes back to building. Three ways to
pull it back, from least to most enforced:

> If it was a version mismatch, check your own daemon before you blame the
> agent. `sagefs status` prints when it started — a daemon that's been up since
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
  running.

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

[`mcp-tools.md`](mcp-tools.md#workspace-hygiene) has the details. The brief you
give a sub-agent should say to remove nothing it doesn't own and to report its
worktree path and branch, so the orchestrator knows what to reap.
