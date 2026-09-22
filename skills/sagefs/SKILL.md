---
name: sagefs
description: "How to work in any F# repo when SageFs is available: the SageFs REPL (MCP) is the inner loop, and dotnet build/test is only the final gate. Use at the start of every F# task, whenever you're about to run dotnet build, dotnet test, dotnet run or dotnet fsi, when a SageFs tool errors, and when writing a brief for a sub-agent that will touch F#."
license: MIT
---

# Working in F# with SageFs

SageFs gives you a live F# REPL that already has the project loaded. An eval
takes milliseconds. A `dotnet build` takes tens of seconds to minutes, and a
test run takes longer. If you iterate by rebuilding, you spend almost all your
time waiting. So:

**The REPL is the inner loop. `dotnet build` / `dotnet test` / `dotnet run` is
the final gate, run once, when you're done.** Not a fallback, not "just to
check quickly", not for probing what an API looks like.

Agents drift back to the slow loop the moment the REPL feels awkward. That drift
is the failure this skill exists to stop. If you catch yourself typing
`dotnet build` in the middle of a task, stop and read "When the REPL fights
you" below.

## The first minute

1. **Is SageFs up?** Call `get_fsi_status` (or `list_sessions`). If the tools
   aren't there at all, SageFs isn't connected. Tell the user; don't work
   around it silently.
2. **Is the daemon current?** A stale daemon serves old code and gives you wrong
   answers that look right. Compare the daemon's version (`get_fsi_status`, or
   the dashboard's `/api/daemon-info`) with what the repo expects. If it's
   behind, tell the user. **Never stop, restart or reinstall the user's daemon
   without asking.** It's theirs, and other agents may be using it.
3. **Do you have a session for where you're working?** Sessions are tied to a
   working directory, and **a git worktree is its own routing boundary**. A
   session for the main checkout is not yours if you're in
   `.claude/worktrees/whatever`. Don't create a duplicate either: check
   `list_sessions` first.
4. **Build once, then create the session.** A session loads your project's
   compiled output. Create it before the first build and warmup fails with
   "Not all DLLs are found". That's not a daemon bug; build first. This one
   build is allowed. Then `create_session` with your working directory and
   project, and call `get_fsi_status` until it says `Ready`. Don't sleep in a
   loop.

## The loop

1. **RED in the REPL.** `send_fsharp_code` with the smallest thing that shows
   the problem: the failing case, the wrong value, the bad parse. Watch it fail.
2. **GREEN in the REPL.** Redefine the function in the session until the case
   passes. Small blocks, each statement ending in `;;`.
3. **Persist.** Write the working code into the `.fs` file.
4. **Reload what you persisted.** `hard_reset_fsi_session` with `rebuild=true`
   rebuilds and reloads, so the session runs the real file. Only do this after
   persisting, or when an `.fsproj` changed (new files, new packages). Don't do
   it after every eval.
5. **Re-verify in the session**, then commit.
6. **Final gate:** the full build and the unfiltered test suite, once, at the
   end.

Useful tools while you're in the loop:
- `check_fsharp_code` type-checks without running anything. It's great for "does
  this compile" questions.
- `cancel_eval` stops a runaway eval. Don't reset the session for it.
- `explain_test_failure` and `targeted_verify` exercise real tests without
  building.
- **Want to know an API's or an AST's shape?** Ask the session: reflect over the
  type, parse a sample and print it. Never guess, and never spin up a
  throwaway `dotnet fsi` script to find out.

Running tests from the session: evaluate
`Expecto.Tests.runTestsWithCLIArgs [] [| "--filter-test-case"; "name" |] MyTests.tests`.
It returns the exit code. The runner's console output goes to the worker, so if
you need the details, use `explain_test_failure`.

## Things that will bite you

- **"Operation could not be completed due to earlier error"** means a previous
  statement failed. Read the diagnostics and fix that statement. The session is
  fine, so don't reset it.
- **`Ok` / `Error` may be shadowed** by something the session opens. If
  `match r with Ok x -> ...` fails with a type mismatch about `EvalStatus`,
  write `Result.Ok` / `Result.Error`.
- **Never `#r` a DLL the session already loaded from the project.** It creates a
  second copy of every type ("type X is not compatible with type X"). `#r` also
  locks the DLL, so a later rebuild can't overwrite it.
- **"type not found, Version=..."** or a Core version mismatch usually means the
  daemon is older than the code you're loading (or SageFs is hosting its own
  Core). That's a SageFs problem to report, not a reason to quietly switch to
  `dotnet`.
- **A filtered test run is never the acceptance check.** A filter that matches
  nothing prints `0 failed` and exits 0 in plain Expecto. SageFs's trust line
  says `NothingRan` or `NarrowedRun`. Only an unfiltered run counts.
- **Clean up.** `stop_session` on every session you created. Kill only
  processes you started, by exact PID, never by name.

## When the REPL fights you

Sometimes it will. A version skew, a load error, a tool that errors for reasons
that aren't your code. When that happens:

1. **Don't fall back silently.** Write down exactly what broke: the tool, the
   input, the full error.
2. Try the obvious fix once: build first, qualify `Result.Ok`, create the
   session in the right worktree, check the daemon version.
3. If it still fights you, use `dotnet` for **that one step only**, and say so
   in your report with the error from step 1. That report is how SageFs gets
   fixed. Silent fallbacks are how it stays broken.

## Getting back on track

If the user says "back to the REPL", "mandate 1", or invokes the SageFs
`back_to_the_repl` prompt, you've drifted. Stop what you're doing, name the
step where you left the loop, and pick the loop back up from the REPL. Don't
argue it, and don't finish the slow way first.

## Briefing another agent

Sub-agents don't inherit any of this. Every brief for F# work must include the
loop explicitly:

1. `create_session` for the agent's own worktree (after one build)
2. RED and GREEN via `send_fsharp_code`
3. persist to the file
4. `hard_reset_fsi_session` with `rebuild=true`
5. re-verify, then commit
6. `dotnet` only as the final gate
7. report REPL friction instead of silently falling back

The easiest way is to tell it to load this skill.
