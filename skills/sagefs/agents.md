# Sub-agents and getting back on track

Read this when you are writing a brief for a sub-agent that will touch F#, or the user says you have drifted off the REPL.

## Getting back on track

If the user says "back to the REPL", "mandate 1", or invokes the SageFs
`back_to_the_repl` prompt, you've drifted. Stop what you're doing, name the
step where you left the loop, and pick the loop back up from the REPL. Don't
argue it, and don't finish the slow way first.

## Briefing another agent

Sub-agents don't inherit any of this. Every brief for F# work must include the
loop explicitly:

1. `get_daemon_status`, then `get_available_projects` and the matching
   `create_project_session` / `create_solution_session` /
   `create_bare_session` for the agent's own worktree
2. Wait for that session's `get_session_status` to say Ready
3. show the failure with `send_fsharp_code`
4. make it pass with `send_fsharp_code`
5. persist to the file
6. `hard_reset_fsi_session` with `rebuild=true`
7. re-verify, then commit
8. `dotnet` only as the final gate, run in the BACKGROUND, do not block on it
9. if the REPL will not load a session, say so in the report with the exact error. In
   the SageFs repo itself that is a bug in the thing being worked on: fix it or file
   it with the paths it names before going on, do not build around it. Anywhere else
   report it and use `dotnet` for that one step only. Never write "use the REPL where
   it works, otherwise fall back" into a brief: it is a permission slip and agents
   take it. Ask for the actual `send_fsharp_code` submissions in the report as the
   evidence the loop was used; "I used dotnet build" is not that evidence
10. edit with exact editor calls, never `sed`/python/bulk regex; prove the
    change in the REPL before persisting it
11. finish by removing nothing you do not own, and report your worktree path
    and branch so the orchestrator can reap them (`tidy_workspace`)
12. no worktree unless the work must not touch the trunk. An agent that reads or
    edits a file or two works in the shared checkout under a claim (`acquire_claim`).
    A worktree is the exception that gets a ledger entry: the daemon records who
    made it, saves its uncommitted files to `refs/sagefs/salvage/<name>`, and removes it only
    after its work has reached the base branch and it has been quiet for a day. Work
    that exists nowhere else is listed first by `sagefs hygiene`, and
    `sagefs hygiene --rescue <name>` puts it on a branch

The easiest way is to tell it to load this skill.

## When the agent's client has no SageFs tools

A client configured with an HTTP URL loses its SageFs tools when the daemon restarts, because the restart ends the one
session id the client holds (`claude mcp add sagefs -- sagefs mcp` is the durable fix on the client's side). A
sub-agent in that state still has the loop: `sagefs call` reaches every tool from a shell.

- `sagefs call --list` shows the tools. `sagefs call <tool> --<parameter> <value>` calls one; `--json '{...}'` passes
  the arguments as an object. A name the tool does not have is refused with the names it does.
- Put `eval "$(sagefs member mint --role Implementer --dir "$PWD" --print-env SAGEFS_MEMBER_TOKEN)"` in the brief's
  setup, or pass `--token-file <path>`. Every call is a new connection, so with no token a session one call made cannot be
  stopped by the next, and `stop_session` is refused. With the token every call is the same member. Do not pass
  `--token <token>`: it prints a warning, because the token lands in the process list and the shell history.
- The answer is on stdout. A tool error is on stderr with exit code 1. Read the exit code.
