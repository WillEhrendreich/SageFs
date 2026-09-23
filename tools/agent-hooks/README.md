# Agent hooks

## sagefs-repl-guard

A Claude Code `PreToolUse` hook for the Bash tool. When an agent reaches for
`dotnet build`, `dotnet test`, `dotnet run` or `dotnet fsi` in an F# repo while
a SageFs daemon is up, the hook denies the call and tells the agent to go back
to the REPL loop (see `skills/sagefs/SKILL.md`).

It fires only when all three are true:

- the command runs one of those four dotnet verbs (after a `cd`, `&&`, `timeout`,
  `env` and the like is fine, a mention inside quotes or after `echo` is not)
- the hook's working directory has an `.fsproj`, `.slnx` or `.sln` at or above it
- something answers `http://localhost:37749/health` within 800ms
  (`SAGEFS_MCP_PORT` overrides the port)

If SageFs isn't running, everything passes. There's no REPL to go back to.

Always allowed: `dotnet pack`, `dotnet tool ...`, `dotnet --version`,
`dotnet --list-sdks`, `dotnet restore`, and anything else that isn't one of the
four verbs.

### The escape hatch

The final gate, and the one build before `create_session`, are real uses of
dotnet. Say so in the command's environment prefix:

```sh
SAGEFS_FINAL_GATE=1 dotnet test SageFs.Tests
export SAGEFS_FINAL_GATE=1 && dotnet build -c Release && dotnet test
```

The prefix covers the one call it's on. An `export` covers everything after it.

**Declaring the gate doesn't skip the memory check.** A `dotnet build` costs
real memory whether or not SageFs itself ran it — that's exactly how one
night five agents each ran their own final-gate build against one daemon and
took it to 55GB, invisibly to the daemon's own accounting. So a declared
final gate now asks the daemon for an expensive-work lease
(`POST /api/lease/request`) before it's allowed through:

- **granted** — allowed.
- **wait** or **refused** — denied with a message that says **BUSY, not
  broken**, names the reason, and (for `wait`) the retry-after. This is not
  the "use the REPL" message — it means the daemon is trying to shed load
  and a `dotnet build` right now would spend exactly the memory it's trying
  to reclaim. Wait the time named and rerun the identical command.
- the daemon not answering at all skips the lease check entirely — see
  "Always allowed" below.

The hook does not release the lease afterward (a `PreToolUse` hook can't wrap
the command's own execution) — it relies on the lease's own TTL (10-15
minutes for a build/test run) to expire it. See `skills/sagefs/SKILL.md`'s
"Before anything expensive: ask" section for the full lease contract, and
"Busy versus broken" for why this distinction is the whole point.

### Install

Add this to `.claude/settings.json` (the project's) or `~/.claude/settings.json`
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

- `ReplGuard.fs` is the decision: command plus context in, `Allow` or
  `Deny reason` out. It's pure, and SageFs.Tests compiles the same file and tests
  it (`ReplGuardTests.fs`).
- `sagefs-repl-guard.fsx` is the IO around it: reads the hook JSON from stdin,
  walks up for a project file, probes `/health`, and writes the deny JSON.
- `sagefs-repl-guard` is a small POSIX sh wrapper. `dotnet fsi` takes about a
  second and a half to start, and a hook runs before every Bash call, so the
  wrapper exits straight away for commands that never mention dotnet. Only the
  ones that do pay for the F# script.

If the script itself fails (no dotnet on the PATH, say), it exits non-zero and
Claude Code treats that as a non-blocking hook error, so the command still runs.
A broken guard never blocks you.

### Try it by hand

```sh
echo '{"hook_event_name":"PreToolUse","tool_name":"Bash","cwd":"'"$PWD"'","tool_input":{"command":"dotnet test"}}' \
  | tools/agent-hooks/sagefs-repl-guard
```

With SageFs up, that prints the deny JSON. With it down, or with
`SAGEFS_FINAL_GATE=1 dotnet test` as the command, it prints nothing and exits 0.
