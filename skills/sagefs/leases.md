# Leases for expensive work

Read this when you are about to start a full `dotnet build`, a test suite, or an app run yourself, or a lease request comes back denied or delayed.

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

## Who a lease belongs to

A lease belongs to the connection, the `agent_name` and the `working_directory`
you asked under. Sub-agents of one Claude session share ONE MCP connection, so
the connection alone cannot tell them apart. Pass your own `agent_name` (a short,
stable name, different from every sibling's) and your own `working_directory`
(your worktree) to every `acquire_*_lease` call. Without them you are one holder
with every other sub-agent that passed none.

- Asking again under the same three returns the lease you already hold:
  `grant: already_held`, the same `leaseId`, and the expiry is not renewed.
- A different `agent_name` on the same connection is a different holder, so it
  waits its turn instead of being told it already holds one.
- A `wait` names who holds the pool (agent, connection, directory, kind of work,
  when it was granted, when it lapses), your place in line, and how long to wait.
  Ask again after that long. Asking again keeps your place. Stop asking and your
  place lapses after five minutes.
- You cannot release another holder's lease. Wait for it to be released or to
  lapse. `get_daemon_status` shows every holder and how long it has left.
- A `refused` for a different kind names the lease you hold and its id. Release
  it, then ask again. If you never took it, a sibling that passed no `agent_name`
  did: give each sibling its own.
