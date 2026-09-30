# Troubleshooting: things that bite, busy versus broken

Read this when a SageFs tool errors, an eval gives a result that disagrees with the code in front of you, a lease or build is refused or delayed, or the REPL feels like it is fighting you.

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

## Busy versus broken — the distinction that matters most

When SageFs refuses or delays something, figure out which of these you're in
before you do anything else. Getting this backwards is exactly how the
incident in leases.md happened: every agent read "the REPL is fighting me" and
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
   taking a lease for it if SageFs is up (see leases.md) — and say so in your
   report with the error from step 1. That report is how SageFs gets fixed.
   Silent fallbacks are how it stays broken.
4. **Never spin up your own daemon to get around a busy one.** A second
   daemon spends the same machine memory the first one is trying to protect,
   completely outside anyone's accounting. Only spawn a second daemon when
   you are testing daemon code itself that the running daemon predates — see
   AGENTS.md's multi-agent section — and give it an explicit owner/TTL.
