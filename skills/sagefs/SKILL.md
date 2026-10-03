---
name: sagefs
description: "How to work in any F# repo when SageFs is available: the SageFs REPL (MCP) is the inner loop, dotnet build/test is only the final gate, and every change is proven in the REPL before it is written to a file. Also the house code-design standards: exhaustive DUs over option/bool, Result with a named reason over string refusals, parse-don't-validate, pure functional core with effects at the edge, hexagonal architecture, vertical slices, immutability, small composable functions, TDD red-green-refactor, and no `private` anywhere except a smart constructor. Use at the start of every F# task, when designing a type or a signature, whenever you are about to run dotnet build, dotnet test, dotnet run or dotnet fsi, whenever you are about to edit a source file with sed/python/bulk-regex instead of an exact editor call, when a SageFs tool errors, and when writing a brief for a sub-agent that will touch F#."
license: MIT
---

# Working in F# with SageFs

SageFs gives you a live F# REPL with the project already loaded. An eval takes
milliseconds, a `dotnet build` minutes. So the REPL is the inner loop, and
`dotnet build` / `test` / `run` is the final gate, run once when you are done,
never to probe what an API looks like.

## Before you design anything

**Read `design.md`** before designing a type, signature or module: the house
standard for what the code should LOOK like, where a mechanically correct change
can still be the wrong code. The rest of this skill is mechanics — how to DRIVE
the tool.

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
   says `TimedOut`, call again; on `Faulted`, act on the reason it names.

## The loop

1. RED: `send_fsharp_code` with the smallest thing that shows the problem, and
   watch it fail.
2. GREEN: redefine it in the session until it passes. Small blocks, each
   statement ending in `;;`.
3. Persist the working code to the `.fs` file.
4. `hard_reset_fsi_session` with `rebuild=true`, so the session runs the real
   file. Only after persisting or an `.fsproj` change, not after every eval.
5. Re-verify in the session, then commit.
6. Final gate, once, at the end, in the background: the full build and the
   unfiltered suite, `dotnet run --project <tests>` (Expecto `Exe`), never
   `dotnet test`. From the session, `run_tests` returns a receipt; `Incomplete`
   is not green.

## Rules that bite before your first edit

- Prove a change in the REPL before you write it to a file. Eval the new logic
  on real input and read the output first.
- Edit with exact editor calls: read the region, replace that exact text. Never
  `sed -i`, `python3 -c` or bulk regex edits: a silent no-op looks like success.
- Never `#load` or `#r` anything the session already loaded — two copies of
  every type, and the lock blocks rebuilds. `#load` only a pure file.
- To learn an API's or AST's shape, ask the session (reflect over the type, or
  parse a sample and print it). Never guess or start a `dotnet fsi` script.
- "Operation could not be completed due to earlier error" means an earlier
  statement failed. Fix that one. Do not reset the session.
- A bare `Error`/`Ok` resolving to the wrong type is a shadowing union case:
  write `Result.Error` / `Result.Ok`.
- A filtered test run is never the acceptance check. A filter matching nothing
  still prints `0 failed` and exits 0; only `ran=` proves coverage.
- Acquire the matching lease before a build/test/app run you start (leases.md).
- Clean up: `stop_session` on every session you created; kill only processes
  you started, by exact PID.

## Orchestrator hygiene

Tidy up after sub-agents: `get_workspace_hygiene` before you spawn, `tidy_workspace` (`confirm=true` plus its
plan id) once their work merges. Brief them: remove nothing you do not own, report your worktree path and branch.

## Read more only when you need it

| Read | When |
|---|---|
| design.md | **before designing a type, signature or module** |
| testing-standards.md | **before writing the tests for a change** |
| dst-capabilities.md | before writing a simulation or mutation test |
| sessions.md | choosing a session, a stale daemon, or one stuck warming |
| loop.md | the loop is unclear, or you are tempted to run `dotnet build` mid-task |
| editing.md | before your first edit, or on a `#load` error |
| testing.md | running tests from the session, or judging a green run |
| leases.md | starting a build/test/app run yourself, or a lease was denied |
| troubleshooting.md | a tool errors, or SageFs seems broken |
| claude-code.md | shell commands hit permission prompts |
| agents.md | writing a brief for a sub-agent |
