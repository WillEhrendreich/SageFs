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

The whole playbook is one file: [`skills/sagefs/SKILL.md`](../skills/sagefs/SKILL.md).
It covers:
- the first-minute checklist
- the loop
- the gotchas
- what to do when the REPL fights you
- how to brief a sub-agent

**Claude Code:**
```bash
mkdir -p ~/.claude/skills/sagefs
curl -fsSL https://raw.githubusercontent.com/WillEhrendreich/SageFs/master/skills/sagefs/SKILL.md \
  -o ~/.claude/skills/sagefs/SKILL.md
```
For one repo only, put it in that repo's `.claude/skills/sagefs/SKILL.md`
instead.

**Other agents (Codex, Copilot, Cursor, OpenCode and so on):** most of them read
an `AGENTS.md` at the repo root. Add this to yours:

```markdown
## F# work: use SageFs

This repo is developed with SageFs. The SageFs REPL (MCP tools) is the inner
loop. `dotnet build` / `dotnet test` is only the final gate.
Before any F# change, read and follow
https://github.com/WillEhrendreich/SageFs/blob/master/skills/sagefs/SKILL.md

- Build once, then `create_session` for your working directory (a git worktree
  is its own boundary), then wait for `get_fsi_status` to say Ready.
- Show the problem with `send_fsharp_code`, fix it there, write the fix to the
  file, run `hard_reset_fsi_session rebuild=true`, check it again, commit.
- If the REPL fights you, report the exact error. Don't quietly switch to
  dotnet.
- Never stop, restart or reinstall the SageFs daemon without asking.
```

## 2. Connect the MCP server

Point your agent at `http://localhost:37749/` (streamable HTTP), or
`http://localhost:37749/sse` for older clients. When an agent connects, SageFs
sends it a short version of the rules, so even an agent without the skill gets
the core of it. The skill is the full version, and you want both.

## 3. When the agent drifts

It'll happen. Usually the agent hits something awkward (a version mismatch, a
session that isn't ready yet) and quietly goes back to building. Three ways to
pull it back, from least to most enforced:

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

## 4. When the REPL really is broken

Sometimes it is, and that's worth hearing about. The skill tells agents to
write down the exact error, try the obvious fix once, and only then fall back
for that one step, saying so. If your agent reports something like that,
please [open an issue](https://github.com/WillEhrendreich/SageFs/issues) with
the error. Every silent fallback is a bug that never gets fixed.
