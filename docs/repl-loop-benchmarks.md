# The REPL loop, measured

What an edit costs here, next to what the same edit costs the old way. Three projects,
from a sample you can read in one sitting to the engine underneath everything else,
measured on one machine with other work already running. A benchmark taken on an idle
box is a number about a machine nobody actually owns.

## The numbers

| Project | F# lines | Check in a warm session | `dotnet build` | Filtered tests | Build + tests |
|---|---:|---:|---:|---:|---:|
| `samples/counter` | 306 | **0.025 s** | 2.085 s | 6.292 s | 8.377 s |
| `SageFs.Simulation` | 22,601 | **0.024 s** | 52.106 s | 6.719 s | 58.825 s |
| `SageFs.Core` | 86,019 | **0.021 s** | 48.824 s | 5.469 s | 54.293 s |

The check column is `check_fsharp_code` answered by a session that has already loaded
the project. Note what it does *not* do: it does not care how big the project is. Three
hundred lines and eighty-six thousand lines land in the same twenty milliseconds,
because the compiler service is already warm and the work is one snippet, not a
project-wide rebuild. That is the whole trick. The build, meanwhile, does care, and it
cares linearly with how much you have.

## What it costs to start

Nothing above is free of a beginning. Creating a session and waiting for it to reach
Ready:

| Project | Cold warmup, create to Ready |
|---|---:|
| `samples/counter` | 5.479 s |
| `SageFs.Simulation` | 12.546 s |
| `SageFs.Core` | 12.293 s |

You pay that once per project, then every subsequent check is the twenty milliseconds
above. Against that, a build is not a one-off either: it is the same fifty seconds
every time something you touched changes.

## The check really checks

A fast number that comes from a function which quietly answers "fine, whatever" is a
speedometer disconnected from the wheels. So each measurement pair ran the negative
control too. Submitting a snippet that cannot type-check:

```fsharp
let bad = 1 + "x"
```

```text
[error] (1,14) The type 'string' does not match the type 'int'
[error] (1,12) The type 'string' does not match the type 'int'
```

returned in **0.081 s** and **0.066 s** on two different sessions, against 0.021 to
0.085 s for the valid snippet. Same speed, and it caught the error.

## The machine and the load

- **CPU**: AMD Ryzen 7 5800XT, 8 cores / 16 threads
- **RAM**: 62 GiB total, 22 to 37 GiB available during the run
- **Load average during the run**: 4.31 to 10.13

Three `linuxvm` processes held 60 to 100% CPU throughout, the SageFs daemon sat near
123%, and the desktop compositor and browser were running. The numbers above were
measured with all of that going, which is closer to how the loop actually gets used
than a quiescent machine would be.

The first call after a session warms is 3 to 4 times the steady figure (JIT and
connection setup), so the table reports the steady value with the first-call figure
available in the raw run. Re-running a build that is already up to date lands at
0.430 s for `SageFs.Core` and 0.620 s for `SageFs.Simulation`, so the build column
spans 0.5 s when nothing moved to 52 s when a target went stale across two
frameworks. Both ends are real.

## Measuring it yourself

The check side is one MCP call:

```bash
curl -sS -X POST http://localhost:37749/ \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2025-06-18' \
  -H "Mcp-Session-Id: $SID" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"check_fsharp_code","arguments":{"code":"let probe = 1 + 1","session_id":"<session>"}}}'
```

bracketed by two `date +%s.%N` calls. The build side needs no trick at all, which is
rather the point.

Note that `check_fsharp_code` reports no duration of its own: `evalCount`,
`get_eval_timeline` and `get_message_journal` stay empty for it, so the clock has to
come from outside the daemon.
