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

1. **Is SageFs up?** Call `get_daemon_status` (or `list_sessions`). If the
   tools aren't there at all, SageFs isn't connected. Tell the user. Don't work
   around it silently.
2. **Is the daemon current?** Do this before you trust a single result. A stale
   daemon serves old code and gives you wrong answers that look right, and it
   is the most expensive failure in this whole document. See "a stale daemon"
   under "Things that will bite you" for the disguises it wears.

   Read its version from `get_daemon_status`, `sagefs status`, or the dashboard's
   `/api/daemon-info`, and compare it against the code you're about to work on.
   In the SageFs repo itself that's `Directory.Build.props`; anywhere else it's
   "was this daemon started after the last build of this project?" If you can't
   tell, the cheap tell is whether a symbol you just added is visible in the
   session.

   If it's behind, **tell the user and ask them to restart it**. Don't stop,
   restart or reinstall it yourself. It's theirs, and other agents may be on
   it. If they ask you how: `dotnet tool update -g sagefs`, then restart. If
   that reports "already installed" while a newer version is on NuGet, pass
   `--version X.Y.Z` explicitly. `dotnet tool update` resolves through NuGet's
   search index, which lags the package store by a few minutes.
3. **Do you have a session for where you're working?** Sessions are tied to a
   working directory, and **a git worktree is its own routing boundary**. A
   session for the main checkout is not yours if you're in
   `.claude/worktrees/whatever`. Check `list_sessions` before creating one.
4. **Choose the session target explicitly.** Call `get_available_projects`,
   then use exactly one of these:
   - `create_project_session` for one `.fsproj`
   - `create_solution_session` for one `.sln` or `.slnx`
   - `create_bare_session` for a project-free REPL

   There is no auto-discovery on these tools. If generated build state is
   missing, SageFs takes a rebuild lease, runs the build itself, rechecks the
   generated files, and only then creates the session. You don't need a shell
   build before session creation, and you shouldn't race one against it.
5. **The create tool returns before warmup finishes.** Poll
   `get_session_status` for that exact session until it says `Ready`. Don't
   sleep in a loop. Warming responses carry elapsed time, work time, and the
   worker's last progress line. If it says `Faulted`, act on the reason rather
   than polling hoping it changes.

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
- **A bare `Error` or `Ok` that resolves to the wrong type.** If a match on a
  `Result` fails with "This union case does not take arguments" or names some
  other type, a union case in scope is shadowing `Result.Error` / `Result.Ok`.
  Write `Result.Error` / `Result.Ok`. SageFs's own types can't do this anymore:
  no SageFs union has a case named `Ok`, `Error`, `Some` or `None`, and a test
  enforces that. A library you open still might.
- **Never `#r` a DLL the session already loaded from the project.** It creates a
  second copy of every type ("type X is not compatible with type X"). `#r` also
  locks the DLL, so a later rebuild can't overwrite it.
- **A stale daemon is the most expensive failure here, because it looks like
  every other failure.** A daemon that has been running since before your code
  changed keeps serving the assemblies it started with. Nothing warns you. It
  wears at least three disguises:
  - `"type not found, Version=..."` or a Core version mismatch.
  - `Could not load file or assembly 'System.Runtime, Version=N.0.0.0'` in
    worker stderr, on a project that builds fine on its own. That one means the
    daemon's worker is on an older .NET than your project targets — it starts
    fine and then chokes the moment it loads your DLLs.
  - No error at all: evals that quietly disagree with the code in front of you.

  **Check the daemon's version before you believe anything else.**
  `get_daemon_status` or `get_session_status`, `sagefs status`, or
  `/health`. If the daemon is behind the code
  you're working on, say so and ask the user to restart it — that is the fix,
  and no amount of `hard_reset_fsi_session` will substitute for it, because the
  daemon process itself is the stale thing. Don't restart it yourself; it's
  theirs and other agents may be on it.

  An agent lost an entire session to this: it read the load error as "SageFs is
  broken", spent hours working around it with `dotnet`, and the daemon was
  simply old. Checking the version first would have cost one tool call.
- **A filtered test run is never the acceptance check.** A filter that matches
  nothing prints `0 failed` and exits 0 in plain Expecto. SageFs's trust line
  says `NothingRan` or `NarrowedRun`. Only an unfiltered run counts.
- **Clean up.** `stop_session` on every session you created. Kill only
  processes you started, by exact PID, never by name.

## Before anything expensive: ask

Session create/warmup, `hard_reset_fsi_session rebuild=true`, a full `dotnet
build`, a test-suite run, starting an app — these cost real machine memory.
One night, five agents each did one of these against a single daemon, all at
once. Nobody was misbehaving; nothing coordinated. The daemon had no way to
know until its RSS was already at 55GB of a 62GB box.

SageFs-managed session creation and `hard_reset_fsi_session rebuild=true`
acquire their own coordination leases. Do not manually lease those operations.

Before a caller-owned full `dotnet build`, unfiltered test suite, or app run,
use the matching MCP tool:

- `acquire_full_build_lease` for a build you start yourself;
- `acquire_test_suite_lease` for a test-suite process you start yourself;
- `acquire_run_app_lease` for a run-app process you start yourself.

A granted tool returns an opaque `leaseId`. Call `release_work_lease` with that
exact id on the normal exit path. If a lease tool denies or delays the work,
follow the decision it returns; do not shell around the daemon and spend the
same memory outside its accounting.

## Busy versus broken — the distinction that matters most

When SageFs refuses or delays something, figure out which of these you're in
before you do anything else. Getting this backwards is exactly how the
incident above happened: every agent read "the REPL is fighting me" and
reached for `dotnet`, when the REPL wasn't broken — the daemon was busy, and
`dotnet` spent the same memory anyway, outside its accounting, at the exact
moment it was trying to shed load.

- **BROKEN**: a real bug, a version skew, a tool erroring for reasons that
  aren't your code, the REPL genuinely not doing what it says. The escape
  hatch below is correct: use `dotnet` for that one step, and report it,
  because the report is how it gets fixed.
- **BUSY**: SageFs (or the `sagefs-repl-guard` hook, if it's installed) tells
  you pressure is `tight` or `critical`, a lease request came back `wait` or
  `refused`, or a declared final-gate `dotnet build`/`test`/`run` gets denied
  with a message that says "BUSY, not broken." The escape hatch is exactly
  the WRONG move here. **Wait the time it names, then retry the identical
  command or lease request.** Shelling out anyway, or spinning up your own
  daemon to get around a busy one, spends the exact memory SageFs is trying
  to reclaim — invisibly to it. That is the whole mechanism of the incident
  this section exists to prevent.

If you genuinely cannot tell which one you're in, that itself is a bug: report
it exactly like a BROKEN case (tool, input, full error) rather than guessing.

## When the REPL fights you (this means BROKEN, not busy)

Sometimes it will. A version skew, a load error, a tool that errors for reasons
that aren't your code — this is the BROKEN case above, not the BUSY one. When
that happens:

1. **Don't fall back silently.** Write down exactly what broke: the tool, the
   input, the full error.
2. Try the obvious fix once: build first, qualify `Result.Ok`, create the
   session in the right worktree, check the daemon version.
3. If it still fights you, use `dotnet` for **that one step only** — after
   taking a lease for it if SageFs is up (see above) — and say so in your
   report with the error from step 1. That report is how SageFs gets fixed.
   Silent fallbacks are how it stays broken.
4. **Never spin up your own daemon to get around a busy one.** A second
   daemon spends the same machine memory the first one is trying to protect,
   completely outside anyone's accounting. Only spawn a second daemon when
   you are testing daemon code itself that the running daemon predates — see
   AGENTS.md's multi-agent section — and give it an explicit owner/TTL.

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
8. `dotnet` only as the final gate
9. report REPL friction instead of silently falling back

The easiest way is to tell it to load this skill.
