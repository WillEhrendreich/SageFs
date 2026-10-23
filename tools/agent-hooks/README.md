# Agent hooks

## sagefs-repl-guard

A Claude Code `PreToolUse` hook for the Bash tool. When an agent reaches for
`dotnet build`, `dotnet test`, `dotnet run` or `dotnet fsi` in an F# repo while
a SageFs daemon is up, the hook denies the call and tells the agent to go back
to the REPL loop (see `skills/sagefs/SKILL.md`).

It fires only when all four are true:

- the command runs one of those four dotnet verbs (after a `cd`, `&&`, `timeout`,
  `env` and the like is fine, a mention inside quotes or after `echo` is not)
- the hook's working directory has an `.fsproj`, `.slnx` or `.sln` at or above it
- something answers `http://localhost:37749/health` within 800ms
  (`SAGEFS_MCP_PORT` overrides the port)
- that same `/health` reports a **Ready** session whose working directory
  is the command's `cwd`, or an ancestor of it

If SageFs isn't running, everything passes. There's no REPL to go back to.
The last condition matters just as much: with SageFs up but no Ready
session rooted at (or above) `cwd`, there's *still* no REPL to go back to,
that's exactly the one build `create_session` itself needs before there can
be a session at all, so it passes too (issue #144). A session that exists
for `cwd` but isn't Ready yet (still starting) doesn't count either. One that is
Faulted doesn't count as a REPL either, and gets its own treatment: see "A Faulted session".

Always allowed: `dotnet pack`, `dotnet tool ...`, `dotnet --version`,
`dotnet --list-sdks`, `dotnet restore`, `dotnet build -c Release` (see
"Release builds pass"), anything the repository lists in
`.SageFs/repl-guard.json`, and anything else that isn't one of the four verbs.

### The escape hatch

The final gate, and the one build before `create_session`, are real uses of
dotnet. Say so in the command's environment prefix:

```sh
SAGEFS_FINAL_GATE=1 dotnet test SageFs.Tests
export SAGEFS_FINAL_GATE=1 && dotnet build -c Release && dotnet test
```

The prefix covers the one call it's on. An `export` covers everything after it.

**Declaring the gate doesn't skip the memory check.** A `dotnet build` costs
real memory whether or not SageFs itself ran it, that's exactly how one
night five agents each ran their own final-gate build against one daemon and
took it to 55GB, invisibly to the daemon's own accounting. So a declared
final gate now asks the daemon for an expensive-work lease
(`POST /api/lease/request`) before it's allowed through:

- **granted**: allowed.
- **wait** or **refused**: denied with a message that says **BUSY, not
  broken**, names the reason, and (for `wait`) the retry-after. This is not
  the "use the REPL" message, it means the daemon is trying to shed load
  and a `dotnet build` right now would spend exactly the memory it's trying
  to reclaim. Wait the time named and rerun the identical command.
- the daemon not answering at all skips the lease check entirely: see
  "Always allowed" below.

The hook does not release the lease afterward (a `PreToolUse` hook can't wrap
the command's own execution), it relies on the lease's own TTL (10-15
minutes for a build/test run) to expire it. See `skills/sagefs/leases.md`'s
"Before anything expensive: ask" section for the full lease contract, and
`skills/sagefs/troubleshooting.md`'s "Busy versus broken" for why this
distinction is the whole point.

### A Faulted session

When no Ready session covers `cwd` but a Faulted one does, there is no REPL to go back to, so the build is
allowed (the hatch is the right move there). What the hook adds is the reason. The daemon already records why
each session faulted (`faultReason` in `/health`'s `sessionStates`), and the hook hands that text to the agent
as `additionalContext`, whole, with the way back: do what the reason says, then `hard_reset_fsi_session` with
`rebuild=true`. For a multi-targeted project with no build for any framework the host can load (issue #153),
the message the daemon writes names the exact command to run, `dotnet build <project> -c Release -f <tfm>`
(`FrameworkPicker.describe` in `SageFs.Core/ProjectLoading.fs`), so the agent no longer has to guess.

- It is the same in `enforce` and `advise` mode. Nothing is being denied, so there is no preference for the
  mode to bend.
- Like the advise reminder it sets no `permissionDecision`, so your normal permission prompts still apply.
- A Ready session that covers `cwd` always wins over a Faulted one, because a REPL exists. Of several Faulted
  sessions the one with the deepest working directory speaks, since that is the one the agent is standing in.
- A declared final gate gets the same note when the lease is granted. A lease the daemon refuses or asks to
  wait for is still the BUSY deny, and that outranks the note.
- If the session carries no reason (the wire value is missing or blank), the note says the daemon recorded
  none and points at the daemon log. It is never empty.

What is rough: the hook matches a session to `cwd` by working-directory ancestry, the same as for a Ready one.
A Faulted session rooted at a main checkout also covers a worktree nested under it, and `/health` does not say
which checkout a session belongs to, so the note can name a fault from the other checkout.

### Counting the escape hatch

The hook reports every use of the hatch to the daemon, so the number exists. When the hook lets a `dotnet
build`, `test`, `run` or `fsi` through while a daemon is answering in an F# workspace, it posts one report to
`POST /api/hatch/record` with two facts:

| `declaration` | meaning |
|:---|:---|
| `declared` | `SAGEFS_FINAL_GATE=1` was in front of it, or it was a Release build, which the hook treats the same |
| `undeclared` | a bare `dotnet build`, `test`, `run` or `fsi` |

| `repl` | meaning |
|:---|:---|
| `ready` | a Ready session covers `cwd`. A final gate looks like this, and so does advise mode letting a command run |
| `faulted` | a session covers `cwd` and is Faulted: the broken-REPL fallback |
| `not_ready` | no session covers `cwd`, or the one there is still starting. The one build `create_session` needs lands here, so expect about one per fresh worktree |

The daemon keeps one count for each of the six combinations, and `get_daemon_status` shows them under `hatch`
(`since`, `total`, `lastRecordedAt` and a `uses` row for each combination, zeros included; see
[`docs/mcp-tools.md`](../../docs/mcp-tools.md)). "Twenty agents all fell back today" reads as the
`undeclared`/`faulted` and `declared`/`faulted` rows going up by about that much. The number to watch is
everything not `ready`.

What is not reported: a command the hook denied (it did not run), a command with no daemon answering, a
command outside an F# workspace, `dotnet pack` and the other verbs that never needed the REPL, and anything the
repository's allowlist lists. The report is best effort with an 800 ms limit: a daemon that is gone, slow, or
older than this route (it answers 404) changes nothing about what the agent is told, and the count is simply
one short. The count lives in the daemon's memory, so it starts over when the daemon restarts.

What is rough: it counts commands, not agents. The hook has no stable id for the agent that ran a command, so
twenty builds from one agent and one build each from twenty agents read the same.

### Release builds pass

`dotnet build -c Release` (or `--configuration Release`, `--configuration=Release`, `-c:Release`) is
treated as a declared final gate, so it does not need `SAGEFS_FINAL_GATE=1`. A Release build checks
things the REPL cannot, so denying it only costs a retry. It still asks the daemon for a lease like any
final gate, so a busy daemon can still say wait. If a command has several `-c` options, the last one
counts, and anything after a bare `--` is ignored.

Only `build` gets this. `dotnet test -c Release` and `dotnet run -c Release` run code the REPL can run,
so they are still denied.

### A per-repository allowlist

Some repositories have their own build CLI, for example `dotnet run --project Build.fsproj -- <command>`,
and the hook cannot tell that from any other `dotnet run`. List it in `.SageFs/repl-guard.json` at the
root of the git checkout and commit the file:

```json
{
  "allow": [
    "dotnet run --project Build.fsproj --"
  ]
}
```

How it matches, so you can predict it:

- A pattern is the words of a dotnet command. It matches a command whose words **start with** them, whole
  words only. `dotnet run --project Build.fsproj` matches `dotnet run --project Build.fsproj -- test`
  and does not match `dotnet run --project Build.fsproj.bak`. There is no regex and no glob.
- Each command in a chain is checked on its own. `dotnet run --project Build.fsproj -- x && dotnet test`
  allows the first and still denies the second. An `env` or `timeout` in front does not matter.
- A listed command skips the lease check too. You said it always passes.
- A pattern has to be one plain dotnet command and start with `dotnet`. No quotes, pipes, `;` or `&&`.

The checkout root is the nearest directory at or above the `cwd` Claude Code sends the hook that has a
`.git` (a directory, or the file a worktree has). The hook's own working directory is not used. With no
`.git` above you, the file is not read.

If the file is wrong (not JSON, an unknown key, a bad pattern), the hook ignores all of it, stays strict,
and the deny message names the file and what is wrong. A typo in a key like `allows` is an error and not
an empty list. If the command would have passed anyway, you hear nothing.

What is rough: the patterns are word prefixes, so you list each spelling of a command you use
(`Build.fsproj` and `./Build.fsproj` are different words). Nothing checks that the file is
committed.

### Advise mode

Some repositories treat the REPL as a recommendation, not a rule. Set `SAGEFS_REPL_GUARD=advise` and the
hook lets the command run and hands the agent the same reminder as `additionalContext` instead of a deny.
`enforce` is the default. Any other value enforces, and the deny says so.

```json
{ "env": { "SAGEFS_REPL_GUARD": "advise" } }
```

That goes in `.claude/settings.json` (or `settings.local.json`) next to the hook entry, or export it in
your shell before you start Claude Code.

Advise only changes the "use the REPL" deny. A busy daemon still says wait, because that is about memory
and not about a preference. The hook does not set a permission decision, so your normal permission prompts
for the command still apply. The reminder only appears where a deny would have: with SageFs up and a Ready
session for the directory. (A Faulted session's note, above, appears in both modes.)

What is rough: it is one switch for the whole session, not a key in the repository's JSON file.

### Install

Build the hook once per machine (and again after changing `ReplGuard.fs`, `Verdict.fs`, `Hook.fs` or `SageFs.Core/HatchUse.fs`):

```sh
dotnet fsi tools/agent-hooks/publish.fsx
```

That writes `tools/agent-hooks/sagefs-repl-guard` (`.exe` on Windows), a self-contained single-file
program, about 90 MB and not committed. Then add this to `.claude/settings.json` (the project's) or `~/.claude/settings.json`
(yours), with the path pointing at your SageFs checkout:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": "/path/to/SageFs/tools/agent-hooks/sagefs-repl-guard",
            "timeout": 15
          }
        ]
      }
    ]
  }
}
```

In the SageFs repo itself, `"$CLAUDE_PROJECT_DIR/tools/agent-hooks/sagefs-repl-guard"`
works.

### How it's built

- `ReplGuard.fs` is the decision: command plus context in, `Allow`,
  `AllowWithAdvice reminder` or `Deny reason` out. It's pure, and SageFs.Tests
  compiles the same file and tests it (`ReplGuardTests.fs`,
  `ReplGuardConfigTests.fs`). `sessionProbe`/`directoryContains` in there are the
  "is there a Ready REPL for this cwd" check, also unit-tested directly. The
  allowlist, the Release rule and the mode live here too, and so does
  `hatchReportWith`, the pure decision of what (if anything) to report to the daemon.
- `SageFs.Core/HatchUse.fs` is the closed type the report is made of
  (`GateDeclaration`, `ReplAtTheTime`, their wire tokens). The daemon counts what the
  hook reports, so both compile that one file instead of spelling the tokens twice.
  It depends on nothing past FSharp.Core, for the same startup reason as the rest.
- `Verdict.fs` is the whole run minus the machine: stdin JSON in, JSON out. The
  environment, the repo config on disk, the project scan, the daemon, the lease
  and the hatch report come in as a `World`, so the tests run the real code with a fake
  daemon and a temp git checkout. It also finds the git root, reads the config file
  and reads `/health`'s `sessionStates`.
- `Hook.fs` is the compiled program's `main` and the real probes: reads stdin,
  walks up for a project file, probes `/health` once (for both daemon-liveness and
  its `sessionStates`), asks for the lease, posts the hatch report.
- `SageFs.AgentHooks.fsproj` compiles those into the `sagefs-repl-guard`
  program, and `publish.fsx` publishes it. It is a compiled program rather than
  an `.fsx` because a hook runs before every Bash call and `dotnet fsi` costs
  about 1.3 seconds of startup each time. Measured on the dev machine
  (median of 30 runs): 15 ms for a command that never mentions dotnet (it
  leaves right after reading stdin), 40 ms for a dotnet command with no daemon
  answering. The old `.fsx` plus sh wrapper was 2 ms for the first and 1.3
  seconds for the second.

If the program itself fails, it exits non-zero and Claude Code treats that as a
non-blocking hook error, so the command still runs. A broken guard never blocks
you. A missing binary does the same, so run `publish.fsx` after a fresh clone.

### Try it by hand

```sh
echo '{"hook_event_name":"PreToolUse","tool_name":"Bash","cwd":"'"$PWD"'","tool_input":{"command":"dotnet test"}}' \
  | tools/agent-hooks/sagefs-repl-guard
```

With SageFs up and a Ready session rooted at (or above) `$PWD`, that prints
the deny JSON. With SageFs down, with no Ready session covering `$PWD` yet,
or with `SAGEFS_FINAL_GATE=1 dotnet test` as the command, it prints nothing
and exits 0.
