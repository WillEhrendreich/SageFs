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
