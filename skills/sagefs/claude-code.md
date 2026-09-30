# Permissions and auto mode in Claude Code

Read this when you are in Claude Code and shell commands keep hitting permission prompts, or the auto mode classifier blocks or times out on a command.

## Permissions and auto mode (Claude Code)

The REPL also gets you out of most permission friction, which is one more
reason to stay on it.

- **Allow SageFs once.** Add `"mcp__sagefs__*"` (or `"mcp__sagefs"`) to
  `permissions.allow` in settings.json. An action that matches an allow rule
  resolves right away, so SageFs calls never wait on a prompt or on auto mode's
  classifier. See the
  [permissions docs](https://code.claude.com/docs/en/permissions.md).
- **Shell commands mostly don't get that.** In auto mode, broad Bash allow rules
  (`Bash(*)`, wildcarded interpreters, package-manager run commands) are
  dropped, so most `dotnet` commands go through the classifier one at a time.
  That's slower, and every one is another chance of a block or a "cannot
  determine the safety" denial. See the
  [permission modes docs](https://code.claude.com/docs/en/permission-modes.md).
- **Keep shell commands narrow and single-purpose.** A compound command is
  checked piece by piece, so `cd x && dotnet build && ...` needs every piece
  approved. Use absolute paths instead of `cd`.
- **Don't wait with sleep.** A `sleep N; check` pattern gets blocked. For a
  session, poll `get_session_status`. For a long command, run it in the background
  and let it tell you when it's done.
- **Kill only by exact PID, never by name.** Mass kills look destructive, and
  they can take down the user's daemon.
- **If the classifier times out** ("cannot determine the safety of ... right
  now"), it isn't a verdict on your command. Do the read-only work you can,
  then retry. Don't rewrite the command to sneak past it.
