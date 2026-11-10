# A file descriptor closed under a read in the default test tier

Written 2026-11-10 against master 75f8a696. Status: the cause is not found. This page holds the evidence, what each experiment
ruled out, the tool that will name the culprit on the next occurrence, and how to run it.

## The symptom

The default tier is one `dotnet SageFs.Tests.dll` process that runs about 16,650 Expecto cases in parallel, many of which start
child processes. About one gate pass in five fails a case in it with an operation on a file descriptor that was valid a moment
before. .NET maps `EBADF` to `UnauthorizedAccessException` ("Access to the path ... is denied") with an inner `IOException: Bad
file descriptor`, so the real errno is `EBADF`. A descriptor the parent holds, or one it opened microseconds earlier, was closed
(or replaced) by something else.

The failing operation is always the first use of a fresh descriptor: `read` after `open` (`File.ReadAllText`), `lseek` after
`open` (the `CanSeek` probe that makes Cecil throw "Stream does not support seeking"), `read` on a facts file the child wrote.
The runtime computes `CanSeek` and `SupportsRandomAccess` lazily with `lseek` on the first use, so an `EBADF` there reads as an
unseekable stream, and the following `read` fails the same way.

## Evidence: every occurrence on record

| When (CST) | Gate pass | Tier | Case and seed | Message |
|---|---|---|---|---|
| 2026-11-07 about 10:50 | the pass that wrote `ship4.log` | default | `metadata delta emitter.WHY - a body that tells the probe registry it started ...` seeds 3000 and 3001, 76 s into the tier | seed 3000: "the child printed 0 evaluations, wanted 5", exit 0, empty stderr. Seed 3001: `NotSupportedException: Stream does not support seeking` at `DeltaProgram.fs:524` (Cecil `assembly.Write path`). Both in one case run. |
| 2026-11-08 | a pass in the 97252b11 work (`docs/plans/sandboxed-getters-keep-the-pool-clean.md`) | default-net10 | the same list | `UnauthorizedAccessException` on `facts.txt`, inner "Bad file descriptor". The same list then passed 6 of 6 reruns on net10. |
| 2026-11-09 23:00 | 75f8a696 (`logs/75f8a696.failure.txt`, tier log `checkout-41322401.tiers/default.log`, search `4012`) | default | `metadata delta emitter.WHY - the baseline is the Cecil-instrumented module ...` seed 4012, after 12.5 s | `UnauthorizedAccessException ... stdout.txt` inner "Bad file descriptor" from `File.ReadAllText` at `DeltaChild.fs:499`, on a descriptor opened a moment earlier. The parent's `StreamWriter` on the same file had been open for 12 s. Passed 3 of 3 reruns alone. |

Fifteen gate logs from 2026-11-07 to 2026-11-09 are on disk, with seven failure reports. One of the seven is this symptom. The
first two rows come from a scratch copy of a gate log and from the commit messages of `cd4a1f6a`, `1f4daeee` and `97252b11`,
because the gate logs of those passes are gone. That is three occurrences in about fifteen to twenty passes of the tier.

Every occurrence is in the `metadata delta emitter` list. That list is the one place where four cases at a time each hold a
`StreamWriter` open for the whole life of a child process (4 to 12 s), start that child with both output streams redirected, write
assemblies with Cecil, and read the child's files back. It is also the list that keeps a descriptor open the longest, so it may be
the most exposed victim and not the cause.

The first occurrence is 2026-11-07, one day after the machine rebooted (2026-11-06 11:51) following a `systemd 262.1` and
`linux-omarchy 7.2.15-1` upgrade. The running kernel is still 7.2.8-9-omarchy, because the boot entry did not pick up the new
image. The delta tests have run in the gate since 2026-10-01.

## Upstream history of the same signature

- dotnet/runtime#54589 (2021): "Sporadic failure in file enumeration tests on Unix", `EBADF` right after `open`. jkotas gave the
  sequence that fits: thread A closes N, thread B opens and gets N, thread A closes N a second time (the bug) and takes B's
  descriptor, thread B then gets `EBADF`. Root cause: a test wrapped a raw pipe descriptor in a `SafeFileHandle` and also disposed
  the pipe, so two owners closed one descriptor.
- dotnet/runtime#107933 (open, last touched 2026-11-10): `Process.Start` with redirected output fails `EBADF` inside
  `AnonymousPipeClientStream.Init` on the descriptor `ForkAndExecProcess` just returned, on osx x64 and linux arm32. Same
  signature in the runtime's own test suite, root cause not stated.

## What the product and test code can do to a descriptor

The repository has no raw `close`, `dup2` or `close_range` call, no `SafeFileHandle`, `SafePipeHandle`, named or anonymous pipe,
and no `FileStream` over a foreign handle (grepped over `SageFs.Core`, `SageFs`, `SageFs.Host`, `SageFs.Simulation`,
`SageFs.Tests`). The P/Invokes are `clock_gettime`, `link`, `prctl` and the seccomp installer in `ThreadSandbox.fs`, whose filter
answers `EPERM` or `ENOSYS` only. The only `Console` handle code is `Console.OpenStandardOutput()` and `OpenStandardError()`, which
wrap a duplicate of the descriptor.

In-process machinery that touches shared runtime state: Harmony patches of `MethodBase.Invoke` and the other reflection entry points
(`ValueReadTracking.fs`), `ThreadSandbox.run` and its `PoolHold` (process-wide thread pool limits), and `ThreadPool` use of every
test. None of them calls into descriptor code.

## Experiments, all under the canary (see "The canary")

Every run below used `LD_PRELOAD=fdshim.so`, which logs a `close` that fails with `EBADF`, any read, write, `lseek`, `fstat`
or `ftruncate` told its descriptor is not open, any close of descriptor 0, 1 or 2, and any `dup2`, `dup3` or `close_range`, with a
stack and the thread that last opened and last closed that number.

| Experiment | Size | Events in a dotnet process |
|---|---|---|
| Default tier, quiet machine, `TMPDIR` on tmpfs | 1 run, 16,651 of 16,651 cases ran | 0 |
| Default tier under 14 busy loops, `TMPDIR` on tmpfs | 3 runs | 0 |
| Default tier under 14 busy loops, `TMPDIR` on btrfs (the gate's filesystem) | 1 run | 0 |
| Default tier started the way the gate starts a tier (`unshare --user --map-root-user --mount`, private `/tmp` bind mount, then a nested `unshare --user --map-user`, as `TierPlan.isolatedArgv` builds it) | 1 run | 0 |
| Same, two tiers at once, twice | 4 runs | 0 |
| Same, three tiers at once, twice, under 6 busy loops | 6 runs | 0 |
| Same, three tiers at once, with `DOTNET_TieredPGO=0` and `SAGEFS_DATA_DIR` set as `ci-pipeline.fsx` sets them | 3 runs | 0 |
| `metadata delta emitter` list alone under 12 busy loops | 8 runs of 23 cases | 0 |
| The shape of `DeltaChild.runReading` (held `StreamWriter`, redirected streams drained by events, `WaitForExitAsync` then `WaitForExit`, then reads of the written file and of a child-written file), 16 lanes | 6,916 runs in 90 s | 0, no failed read |
| .NET `Process.Start` of `/bin/echo` from 8 threads while 8 threads open, write, `SetLength` and close files | 98,003 spawns, 921,582 opens in 40 s | 0, no empty output |
| Pure C: 12 threads doing `pipe2` x3, `vfork`, `dup2`, `execve`, drain, `waitpid` while 12 threads open, `pwrite`, `pread`, `fstat`, `close` | 448,503 spawns, 49,270,841 file cycles on tmpfs; 847,269 and 134,867 on btrfs | 0 errors, 0 data mismatches |
| Same with 6,000 ballast descriptors and 500-descriptor bursts every 16 opens, so the table grows, shrinks and is copied by `vfork` | 642,117 spawns, 5,020,004 file cycles | 0 |

The `EBADF` closes the canary did report in these runs (12 per tier run) all came from the tier's own shell helpers (`sh -c` with a
`sha256sum | cut` pipeline in `sagefs-npm-guard`), none from a dotnet process. They are an ordinary double close of a pipe end
inside bash and they hit a number nobody else was using.

Of the failing cases in these runs (8 to 15 per run), none touched a descriptor. They are other agents' edits that were in the tree
at the time (tool counts in `docs/mcp-tools.md`).

## What is ruled out

- A double close inside `Process.Start`, `Process` disposal, `FileStream` or `SafeFileHandle` as the runtime ships in `11.0.0-rc.2`
  on this machine, under the call pattern of the delta harness at far above the gate's rate (6,916 harness-shaped runs in 90 s).
  The canary reports any `EBADF` on a close, and none came.
- The kernel's descriptor table under concurrent `vfork` and `open`, at 450,000 spawns and tens of millions of file cycles,
  including a table that grows and shrinks under the copy.
- The seccomp sandbox as the closer: its filter returns `EPERM` or `ENOSYS`, and no case reaches it with `EBADF`.
- The native fork path read in source (`pal_process.c` at v10.0.13 and v11.0.0-rc.1): each pipe end is closed once, on every
  path, and `RestrictHandleInheritance` only sets `FD_CLOEXEC`.
- The gate's namespaces, private `/tmp`, tmpfs against btrfs, and busy-loop contention, each alone and together, at the rate this
  machine could produce.

## What is not ruled out, in order

1. A close that does not go through libc `close`: a raw `syscall(SYS_close, ...)` from native code. The canary sees `close`,
   `closedir`, `fclose` and `close_range`, not a raw syscall.
2. A race that needs the gate's full load: eighteen tiers at once, with the daemon, browser and FSI host tiers beside the default
   tier. Here the canary saw nothing in 19 full tier runs and 8 delta-list runs, against roughly one in five tier runs in the gate.
3. A native library loaded in the test process that closes a descriptor it does not own (SQLite, tree-sitter). Neither was seen
   closing anything with `EBADF`.
4. The Harmony patches of `MethodBase.Invoke` and the other reflection entry points (`ValueReadTracking.fs`). Every run logged
   "the runtime recompiled MethodBase.Invoke and SageFs's watch on it stopped seeing reads for a while", so a detour does lose its
   place when the runtime re-JITs a patched method. No run showed a descriptor error beside that warning, including the three
   with `DOTNET_TieredPGO=0`. A detour that writes into a stale code address would corrupt whatever sits there, which would look
   like a random double close, but nothing here links the two.

Item 2 needs the gate: run it with the canary below. Items 3 and 4 get their answer from the same run, because a hit names the
frame that closed the descriptor.

## The canary

`docs/fd-canary/fdshim.c` is an `LD_PRELOAD` library, and `docs/fd-canary/resolve.sh` names the JIT frames in its log from the
runtime's perf map. It was checked on a forced double close (two `SafeFileHandle`s over one descriptor, the 2021 bug):

```
EBADF-CLOSE pid=705847 fd=102
   fdshim.so+0x1d47(close)
   libSystem.Native.so+0xda8b(SystemNative_Close)
   int32 [System.Private.CoreLib] Interop+Sys::Close(native int)
   instance bool [System.Private.CoreLib] Microsoft.Win32.SafeHandles.SafeFileHandle::ReleaseHandle()
   instance void [System.Private.CoreLib] System.Runtime.InteropServices.SafeHandle::InternalRelease(bool)
   void [FSI-ASSEMBLY-MULTI] <StartupCode$FSI_0001>.$FSI_0001::main@()
```

Run it under the gate by exporting these before `scripts/ship.fsx` (the library and the log must live outside `/tmp`, because every
tier gets a private `/tmp`):

```
gcc -O1 -g -shared -fPIC -fno-omit-frame-pointer -o ~/.cache/fdshim/fdshim.so docs/fd-canary/fdshim.c -ldl
export LD_PRELOAD=$HOME/.cache/fdshim/fdshim.so FDSHIM_LOG=$HOME/.cache/fdshim/gate.log
export DOTNET_ReadyToRun=0 DOTNET_PerfMapEnabled=3 DOTNET_PerfMapJitDumpPath=$HOME/.cache/fdshim/perf
mkdir -p $HOME/.cache/fdshim/perf
```

`DOTNET_ReadyToRun=0` makes the runtime JIT its own library code, so the perf map names every frame. It slows startup by about a
minute per process. After the gate, `docs/fd-canary/resolve.sh $FDSHIM_LOG $HOME/.cache/fdshim/perf` prints each hit with its
stack. The log is shared by every process the gate starts, so the `PROC` lines map a pid to an executable. A hit from a dotnet pid
is the one that matters.

How to read a hit: an `EBADF-CLOSE` stack is the second closer. The `last close` line says which call closed that number just
before. If the number was reopened in between, the second closer's own close succeeds, and the evidence is a `VICTIM` line (the new
owner's failing call) followed later by that owner's `EBADF-CLOSE`. In both cases the closer's stack names the managed frame that
owns the stale descriptor.

## What this page does not change

No test or product code changed. The harness could stop holding `stdout.txt` open for the child's life and use the lines it already
has in memory, and that would remove two of the victims in the table. It would not remove the facts-file read or the Cecil writes,
which open fresh descriptors, so it would hide the symptom in the most-hit list without touching the cause. It stays undone until a
hit names the closer.
