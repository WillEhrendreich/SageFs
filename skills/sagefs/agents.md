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
8. `dotnet` only as the final gate, run in the BACKGROUND — do not block on it
9. report REPL friction instead of silently falling back
10. edit with exact editor calls, never `sed`/python/bulk regex; prove the
    change in the REPL before persisting it

The easiest way is to tell it to load this skill.
