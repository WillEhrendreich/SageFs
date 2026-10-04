# Troubleshooting: things that bite, busy versus broken

Read this when a SageFs tool errors, an eval gives a result that disagrees with the code in front of you, a lease or build is refused or delayed, or the REPL feels like it is fighting you.

## "404 Session not found" — read this FIRST, it is not what it looks like

**Your client is not broken. The connection is LIVE and pointing at a session that
no longer exists.**

The MCP session lives in the DAEMON'S MEMORY. The SDK mints an
`Mcp-Session-Id` when a client sends `initialize`, and the daemon holds it in
its process. When the daemon is RESTARTED — which SageFs does on purpose every
time a new build is installed, so you get the new code — the new daemon has never
heard of that id, and every request the client makes is refused. Measured, same id
and same port either side of a restart:

```
held session id, before restart:  200
held session id, after  restart:   404
```

This is not a stale daemon, not a broken config and not your fault, so do NOT
spend this session rebuilding your MCP setup. Check the version first (the next
section) and then do this:

```
dotnet fsi scripts/reconnect-mcp.fsx
```

It re-runs `initialize` and PROVES the result: it first sends a deliberately
never-minted id and requires it to be REJECTED, so a later success cannot mean
merely "the endpoint answers". Exit 0 with `OK` and the session is live. Run it
from the SageFs repo (it takes an optional port, default 37749).

**Why you will keep hitting this.** A client on Streamable HTTP sends
`initialize` ONCE, at connect, and keeps the id. Nothing tells it to initialize
again after the daemon changes, and the protocol cannot recover an id in place.
So the same failure returns every time a build is installed, until someone
reloads the client by hand.

**The durable answer, and what to tell your user.** SageFs ships a stdio bridge,
`sagefs mcp`, which owns the handshake itself: it captures `Mcp-Session-Id` from
the response headers so YOUR CLIENT NEVER SEES ONE. Register the bridge instead of
a URL:

```
claude mcp add sagefs -- sagefs mcp
```

or in raw JSON:

```json
{ "mcpServers": { "sagefs": { "command": "sagefs", "args": [ "mcp" ] } } }
```

That works for any client that spawns a command, not just Claude Code. See
"Connect" in `docs/mcp-tools.md`, which covers this in full for a human.

**Say which side the fix is on.** Reconnecting is something to RUN. Whether the
client heals ITSELF is a change to that client's MCP transport, not to this
repo — so if the user wants it automatic, tell them it is their side rather than
implying the command closes it.

## Things that will bite you

- **A cohort tool (`acquire_claim`, `release_claim`, `request_landing`) says you have not
  joined, or refuses you after a successful `join_cohort`.** Cohorts are per repository.
  Pass the SAME `working_directory` (the repository you are working in) to `join_cohort`
  and to every later cohort tool. A call that names no directory acts in the cohort of the
  repository the daemon was started in, and there you are a stranger. Check with
  `get_cohort_status` and that directory: you should be listed as present. If a daemon up
  to 0.6.891 told you "your role is Working: that needs the Working role" after you joined
  as Implementer in a repository that is not the daemon's own, that was a daemon bug, fixed
  after 0.6.891 (docs/mcp-tools.md, "If a cohort tool says you have not joined").
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
