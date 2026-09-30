---
name: sagefs
description: "How to work in any F# repo when SageFs is available: the SageFs REPL (MCP) is the inner loop, dotnet build/test is only the final gate, and every change is proven in the REPL before it is written to a file. Use at the start of every F# task, whenever you're about to run dotnet build, dotnet test, dotnet run or dotnet fsi, whenever you're about to edit a source file with sed/python/bulk-regex instead of an exact editor call, when a SageFs tool errors, and when writing a brief for a sub-agent that will touch F#."
license: MIT
---

# Working in F# with SageFs

SageFs gives you a live F# REPL with the project already loaded. An eval takes
milliseconds. A `dotnet build` takes minutes. So the REPL is the inner loop, and
`dotnet build` / `dotnet test` / `dotnet run` is the final gate, run once when
you are done. Do not use them to probe what an API looks like.

## First minute

1. Call `get_daemon_status`. If the tools are missing, SageFs is not connected,
   so tell the user. Some clients (Claude Code) load MCP tools on demand: fetch
   the core ones in ONE call (get_daemon_status, list_sessions,
   get_available_projects, create_project_session, get_session_status,
   send_fsharp_code, hard_reset_fsi_session, stop_session). If the daemon's
   version is behind the code you are working on, tell the user. Do not restart
   it yourself: it is theirs and other agents may be on it.
2. Call `list_sessions`. A session belongs to a working directory (a git
   worktree is its own boundary). Use yours, never create a duplicate.
3. Call `get_available_projects`, then create one session with
   `create_project_session` (one `.fsproj`), `create_solution_session` (a
   `.sln` or `.slnx`) or `create_bare_session` (no project). SageFs builds
   missing generated state itself, so do not run a shell build first.
4. Warmup takes 15-30s. Call `get_session_status` with `wait_seconds=60`: it
   returns once the session is `Ready`, so no sleeping or polling. If `wait`
   says `TimedOut`, call again. If it says `Faulted`, act on the reason it names.

## The loop

1. RED: `send_fsharp_code` with the smallest thing that shows the problem, and
   watch it fail.
2. GREEN: redefine it in the session until it passes. Small blocks, each
   statement ending in `;;`.
3. Persist the working code to the `.fs` file.
4. `hard_reset_fsi_session` with `rebuild=true`, so the session runs the real
   file. Only after persisting or an `.fsproj` change, not after every eval.
5. Re-verify in the session, then commit.
6. Final gate: the full build and the unfiltered test suite, once, at the end.
   Run it in the background and keep working. To run tests, call
   `run_tests`. It asks the engine `list_tests` reads and returns a receipt:
   `Incomplete` is not green. Still running? Call again with the
   `receipt_id`. Never `dotnet test` for that. Final gate: `dotnet run --project
   <tests>` (Expecto `Exe`).

## Rules that bite before your first edit

- Prove a change in the REPL before you write it to a file. Eval the new logic
  on real input and read the output first.
- Edit with exact editor calls: read the region, replace that exact text. Never
  `sed -i`, `python3 -c` rewrites or bulk regex edits: a silent no-op looks like
  success. A repeated mechanical change is an `.fsx` run through SageFs that
  asserts every replacement matched.
- Never `#load` a file from a project the session already loaded. You get two
  copies of every type and a misleading "type is not compatible" error. `#load`
  only a pure file with no dependency on the loaded project.
- Never `#r` a DLL the session already loaded from the project. Same two-copies
  trap, and the lock blocks rebuilds.
- To learn an API's or AST's shape, ask the session (reflect over the type, or
  parse a sample and print it). Never guess or start a `dotnet fsi` script.
- "Operation could not be completed due to earlier error" means an earlier
  statement failed. Fix that statement. Do not reset the session.
- A bare `Error` or `Ok` that resolves to the wrong type is shadowed by a union
  case in scope. Write `Result.Error` / `Result.Ok`.
- A filtered test run is never the acceptance check. A filter that matches
  nothing still prints `0 failed` and exits 0. Only an unfiltered run counts,
  and its `TRUST` line must say `ran=` the number you expect.
- Before a full build, test suite or app run that you start yourself, acquire
  the matching lease (see leases.md).
- If the REPL fights you, do not fall back silently. Note the tool, input and
  full error; see troubleshooting.md.
- Clean up. `stop_session` on every session you created. Kill only processes
  you started, by exact PID, never by name.

## Read more only when you need it

| Read | When |
|---|---|
| sessions.md | choosing or creating a session, checking the daemon version, a session stuck warming or Faulted |
| loop.md | the loop is unclear, or you are tempted to run `dotnet build` mid-task |
| editing.md | before your first edit, on a `#load` error, or on F# syntax that costs a build |
| testing.md | running tests from the session, waiting on a slow gate, judging whether a green run covered anything |
| leases.md | starting a full build, test suite or app run yourself, or a lease came back denied |
| troubleshooting.md | a tool errors, an eval disagrees with your code, or SageFs seems busy or broken (a stale daemon is the usual cause) |
| claude-code.md | shell commands hit permission prompts in Claude Code |
| agents.md | writing a brief for a sub-agent, or the user says you have drifted off the REPL |
