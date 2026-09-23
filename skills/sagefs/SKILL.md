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
2. **Is the daemon current?** Do this before you trust a single result. A stale
   daemon serves old code and gives you wrong answers that look right, and it
   is the most expensive failure in this whole document — see "a stale daemon"
   under "Things that will bite you" for the disguises it wears.

   Read its version from `get_fsi_status`, `sagefs status`, or the dashboard's
   `/api/daemon-info`, and compare it against the code you're about to work on.
   In the SageFs repo itself that's `Directory.Build.props`; anywhere else it's
   "was this daemon started after the last build of this project?" If you can't
   tell, the cheap tell is whether a symbol you just added is visible in the
   session.

   If it's behind, **tell the user and ask them to restart it**. Don't stop,
   restart or reinstall it yourself — it's theirs, and other agents may be on
   it. If they ask you how: `dotnet tool update -g sagefs`, then restart. If
   that reports "already installed" while a newer version is on NuGet, pass
   `--version X.Y.Z` explicitly — `dotnet tool update` resolves through NuGet's
   search index, which lags the package store by a few minutes.
3. **Do you have a session for where you're working?** Sessions are tied to a
   working directory, and **a git worktree is its own routing boundary**. A
   session for the main checkout is not yours if you're in
   `.claude/worktrees/whatever`. Don't create a duplicate either: check
   `list_sessions` first.
4. **Build once, and wait for it to actually finish, before you create the
   session.** A session loads your project's compiled output, so build
   first — and "started a build" is not "built". A background build that
   hasn't finished yet is the single most common cause of a session that
   faults immediately with "Not all DLLs are found": SageFs looked for the
   output before your build wrote it. If you see that message right after
   creating a session, check whether a build is still running before
   assuming anything else is wrong — then recover with `hard_reset_fsi_session
   rebuild=true`, which builds and reloads in one step. If the message
   still appears after a build you've confirmed finished, that's a SageFs
   bug: report it with the paths the message names.
5. **Name a project. Don't pass `projects: []` on anything but a small,
   one-project directory.** `create_session` with `projects: []` doesn't
   give you an empty REPL — it auto-discovers and loads whatever project or
   solution sits in that directory, which on a large repo (a solution with
   dozens of projects) can take minutes and used to give you nothing to
   look at while it did. Call `get_available_projects` first, pick the one
   project you actually need, and pass it explicitly:
   `create_session(projects=["path/to/One.fsproj"], working_directory=...)`.
   That is what actually gets you to `Ready` fast — auto-discovery is the
   exception, for small directories, not the default.
6. `create_session` returns immediately; the session then warms up. Call
   `get_fsi_status` until it says `Ready`. Don't sleep in a loop — a
   `WarmingUp`/"Rebuilding" response now carries `elapsedSeconds`,
   `boundSeconds`, and the worker's own last-reported progress line, so you
   can tell "large repo, still working" from "actually stuck" without
   guessing. If it reports `Faulted` with a reason, that reason is real —
   don't keep polling hoping it changes; act on it (usually
   `hard_reset_fsi_session rebuild=true`).

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
  `get_fsi_status`, `sagefs status`, or `/health`. If it's behind the code
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

**SageFs is the coordination point for this.** Before you run any of the five
things above, ask `POST /api/lease/request` (`{ holder, kind }`, where `kind`
is one of `session_create_or_warmup`, `rebuild`, `full_build`,
`test_suite_run`, `run_app`, and `holder` identifies YOU — the same value
every retry, never a fresh id per call) on the daemon's MCP port. You get back
one of three things:

- **`granted`** — go ahead. You get a `leaseId` and an `expiresAt`; call
  `POST /api/lease/release` with the `leaseId` when you're done (a crashed or
  forgetful caller just loses it at `expiresAt` — no need to release on every
  exit path, but do release the normal one).
- **`wait`** — a `retryAfterSeconds` and a `reason`. Sleep that long and ask
  again with the SAME `holder`+`kind`. This is not a rejection of your
  request, it's your place in a fair, first-come queue.
- **`refused`** — you already hold a lease. Finish and release it before
  asking for another; waiting won't help here, releasing will.

This applies even to a `dotnet build` you run yourself outside the REPL (see
below) — the whole point is that the daemon's accounting has to include
memory it never spent a single byte of itself, or the accounting lies.

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
  session, poll `get_fsi_status`. For a long command, run it in the background
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

1. `create_session` for the agent's own worktree (after one build)
2. RED and GREEN via `send_fsharp_code`
3. persist to the file
4. `hard_reset_fsi_session` with `rebuild=true`
5. re-verify, then commit
6. `dotnet` only as the final gate
7. report REPL friction instead of silently falling back

The easiest way is to tell it to load this skill.
