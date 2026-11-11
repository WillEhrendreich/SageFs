# A profile tool: where the time and the allocations went

Roadmap item: `profile-tool` (Later, Repl). Plan written 2026-11-07 against master 4827ab9a.

## Work order

Written 2026-11-11 against master 8975e898 (version 0.6.945). The tool shipped in 0.6.944 (`18e39403`). This section says what
shipped, what the gate has since proven, and what is left. The body under "Resume here" is the build record.

### 1. Outcome

Shipped: an agent calls `profile` with `code` and `mode` (`cpu` or `alloc`); the code runs in its session like
`send_fsharp_code` while the daemon records the FSI host from outside with an installed `dotnet-trace`; the reply is the eval's
text and a bounded table (15 rows, 6,000 characters). Refusals are named (`ToolMissing` with the install command,
`NotRecordable`, `CouldNotAttach`, `SocketPathTooLong`, `HostGone`, `TraceUnreadable`, `NoSamples`).

Outcome gate: `SageFs.Tests/ProfileIntegrationTests.fs:85`, `Integration.hostList "profile tool over a real daemon, host and
recorder"`, five cases (`:87` hot function named first, `:103` long TMPDIR still attaches, `:123` alloc names the type and the
function, `:137` unknown mode refused with the code not run, `:148` host ends gives `HostGone`). The plan's "Next action" says
these had not run on the shipping machine. That is stale: the release gate for 0.6.945 ran them in `--integration-host[3/5]`
and the tier was `Trusted` (69 of 69 passed; `~/.local/share/sagefs-gate/checkout-8431b56a.tiers/integration-host-3of5.log:2351-2355`
and `:2413`). Linux is proven. macOS is claimed and unproven. Windows is refused by design (`ProfileRecorder.fs:283`).

### 2. Where the code is today

- `SageFs.Core/Features/ProfileReport.fs` (303 lines): `ProfileFailure:15` with `ToolMissing:21`, `SocketPathTooLong:26`,
  `HostGone:28`, `TraceUnreadable:32`, `describe:64`; `isGcWait:100` matches only `PollGC` (the one shape a real trace showed).
- `SageFs.Core/Features/ProfileTrace.fs` (146): `SpeedscopeTrace.parse`, `cpuReport`. Pure.
- `SageFs.Core/Features/ProfileNetTrace.fs` (268): `NetTrace.parse`, `allocReport`, our own nettrace reader, no package. Pure.
- `SageFs.Core/Features/ProfileRecorder.fs` (292): `DotnetTrace.installCommand:54`, providers `:59` (cpu) and `:65` (alloc),
  `ToolSearch:92`, `DotnetTraceCollector.locate:139`, `beginRecording:280` (Windows refused `:283`, tool missing `:284`,
  `DiagnosticsSocket.refusalFor` before starting the tool), SIGTERM stop (doc `:134-136`). `Timeouts.profileRecorderWait` at
  `Timeouts.fs:209` (60 s).
- `SageFs.Core/Features/DiagnosticsSocket.fs` (126): `maxPathLength = 107:23`, `longestName:27`, `fits:33`, `TempPlan:36`
  (`KeepTemp | UseShortLink`). Its header says the check is Linux-only and macOS's limit is 103.
- `SageFs/McpProfile.fs` (118): `ProfileCall:16`, `hostPidOf:29` (worker's `GetHostProcess` answer, `Isolated | InWorker |
  NotRunning`), `projectModulesOf:42`, `answerOf:46`, `traceAfterEval:66`, `profileIn:93`, `profile:117`. `top` is not a parameter.
- Classing: `SageFs.Core/Capability.fs:196` (`ToolClass.Eval`), `Affordances.fs:76`, `:263` (`StateGated`), `:758`
  (`ToolName.Profile`). Manual: `docs/mcp-tools.md:350`.
- Tests: `McpProfileTests.fs:26`, `ProfileReportTests.fs`, `ProfileTraceTests.fs`, `ProfileNetTraceTests.fs`,
  `ProfileRecorderSimTests.fs`, `ProfileCollectorProcessTests.fs:55` (stand-in tool), `ProfileIntegrationTests.fs`.
  `ToolListProfileTests.fs` and `HardResetStartupProfileIntegrationTests.fs` are about tool-list profiles and startup, not this tool.
- `scripts/roadmap/RoadmapItems.fs:306` still says `NoLandmarkYet` and the text still says the integration cases "have not yet
  run on the machine that ships". Both are stale.
- `[not verified]`: a run on macOS; a recording of a `run_app` host (which has `DOTNET_MODIFIABLE_ASSEMBLIES=debug`); the
  attach cost on a quiet machine; the GC label against a trace with a gen2 collection.

### 3. Steps (what remains)

1. Close the item through the roadmap process: landmark `SageFs/McpProfile.fs` (`profile`), status Done, and drop the
   "have not yet run" sentence from the item text. `DECISION:` Will owns `RoadmapItems.fs`; recommend close now, with 2 to 5
   as follow-ups under `eval-cost-line` or a new S item.
2. Measure the attach cost on a quiet machine: `dotnet fsi scripts/machine-bench.fsx` style runs of the same eval with and
   without `profile`, medians of five, written into "Measure first" below. Edge, no code. If the overhead on a one-second
   eval is under 10 percent, nothing follows; otherwise a `Timeouts` note and a line in the manual.
3. RED, domain: `ProfileReportTests.fs`, "a gen2 collection's wait frames count as GC wait": record one real trace of an eval
   that forces `GC.Collect(2)` under pressure, read the frame names on the waiting threads, and extend `isGcWait:100` to the
   set seen. Keep the set closed (a list of names, not a substring).
4. RED, domain: concurrent-eval attribution. `McpProfile.answerOf` says nothing when another agent's eval ran in the same
   session during the recording. Add a line to the reply when the session's eval count moved by more than one during the
   recording (the daemon knows its own evals). Test in `McpProfileTests.fs`.
5. macOS: `DiagnosticsSocket.maxPathLength` is 107 on Linux and 103 on macOS. Make the limit a function of
   `OperatingSystem.IsMacOS()` with a test per platform value. Unprovable here; mark the test by platform.
6. `DECISION:` Windows. Stopping `dotnet-trace` needs a signal; a console-control-event stop (`GenerateConsoleCtrlEvent`)
   would lift the refusal. Recommend leave refused until a Windows user asks; the refusal names the reason.
7. `DECISION:` keep our nettrace reader or depend on `Microsoft.Diagnostics.Tracing.TraceEvent`. Recommend keep ours: zero
   packages, `TraceUnreadable` refuses by name when the format moves, and the integration tier notices.

### 4. How to check it

- Inner loop: a SageFs session on `SageFs.Tests`, `send_fsharp_code` against `ProfileReport.cpu`, `NetTrace.parse` with the
  builders in `ProfileNetTraceTests.fs`; `run_tests` filtered to `profile`.
- Lane: `dotnet build SageFs.Tests -c Release`; `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Integration inner loop: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary --filter-test-list "profile tool over a real daemon"`
  (needs `dotnet tool install -g dotnet-trace`).
- Acceptance: unfiltered `--integration-host --summary`, `TRUST ... verdict=Trusted`.

### 5. Traps in this repo

- Tool-list weight: `profile` is already counted in `RatchetBudgets.toolList` (`SageFs.Tests/RatchetBudgets.fs:462`); a longer
  description or a new parameter (`top`) moves the budget, and budgets only go down.
- A new reply line (step 4) is tool output, not a wire shape change: `Plugin: n/a`.
- No new NuGet package without discussion (step 7 is that discussion).
- The TMPDIR trap: the gate's tier directories are 85 characters; the short-link plan in `DiagnosticsSocket.fs` is what makes
  `:103` pass. Do not shorten the gate's paths to make a test pass.
- Linux-only machine: steps 5 and 6 cannot be run here.

### 6. Size and order

Step 1 is an hour. Steps 2 to 4 are S, a day or two together, in any order. Steps 5 to 7 wait on a decision. First commit:
step 1 (roadmap close), then step 3's red test.

### 7. Not now

- A dashboard panel for profiles (the plan said out of scope; still is).
- `top` as a parameter (adds tool-list weight for a bound nobody asked to move).
- Profiling the worker process for in-process sessions (refused with the reason today).
- Beating FsiMcp's numbers on the same code: unmeasured; needs a quiet machine and FsiMcp installed, and nothing depends on it.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress. Shipped in 0.6.944 (`18e39403`). The outcome gate ran green in the 0.6.945 release gate (`--integration-host[3/5]`, Trusted, 2026-11-11). What is left is in "Work order" above: close the roadmap item, measure attach cost, widen the GC-wait label, say when another eval shared the recording, macOS limit, Windows stop. |
| Size | M for the CPU report, S for the allocation report once a trace reader exists (both done) |
| Branch and worktree | none: work is on master |
| Last green commit | the commit that carries this block (2026-11-10, on top of master 75f8a696) |
| Next action | Run the outcome gate (`dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary --filter-test-list "profile tool over a real daemon"`) on the machine that ships and read `ran=`; it needs `dotnet-trace` installed (`dotnet tool install -g dotnet-trace`) on the runner, and a CI runner without it fails the case with the refusal that names the command. Then: the same on Windows (the recorder is stopped with SIGTERM, which Windows lacks, so a Windows daemon refuses today: a console-control-event stop would lift that); the garbage collection label in the CPU table counts only a thread seen in the runtime's GC poll (`PollGC`), which is the one shape a real trace showed, so check it against a run with a gen2 collection; and the cost of attaching on a quiet machine, measured by the benchmark harness. Then set Status to Done and give the item its landmark (`SageFs/McpProfile.fs`, `profile`) in `scripts/roadmap/RoadmapItems.fs`. |
| Decisions made | 2026-11-10 (the agent doing the item, within the owner's brief: no new package without the owner): the recorder is an installed `dotnet-trace` driven from the daemon as an external tool, so no NuGet package is added. A CPU recording is converted to Speedscope by the tool and read by `SpeedscopeTrace`; an allocation recording is kept as the nettrace file and read by `NetTrace`, a reader of our own, because the allocation events are not in the Speedscope conversion and the package the plan named is not needed. A missing tool is refused with its install command. In-process sessions are refused with the reason (the plan's recommendation). The tool is classed `Eval` and gated like `send_fsharp_code`. `mode` is a closed union (`cpu`, `alloc`), and an unknown one is refused by name before any session is looked up, so the code never runs under it. |
| Surprises | The Speedscope file `dotnet-trace convert` writes does name frames of code typed into the session (`FSI-ASSEMBLY!FSI_0008.hotFib(int32)`), so the plan's worry about eval-defined code having no name did not hold on this machine. Each thread has a marker frame at the leaf (`CPU_TIME`, `UNMANAGED_CODE_TIME`, `BLOCKED_TIME`), which is what separates managed CPU from idle threads. SIGINT does not stop `dotnet-trace` when the daemon itself was started with SIGINT ignored (a shell's `&` does that), and a newline on stdin does nothing when stdin is a pipe; SIGTERM stops it, writes the trace and converts it. Sampled time runs well under the wall clock (about 10 percent here: 20 runs of a 90 ms function showed 207 ms), so the report says to read percentages. Attaching and stopping took under a second and about two seconds for a 16 MB trace. A trace is about 1 MB a second of recording, idle threads included, so the recording lasts the eval and nothing longer. The nettrace of an allocation recording is plain enough to read without TraceEvent: blocks of compressed event headers, a stack block of code addresses, and method and module load events (143 and 152 at run time, 144 and 154 in the rundown the tool writes when it stops) that name every address a stack holds, the code typed into the session included. Read from a real trace, 1,319 allocation ticks for about 160 MB, 64,008,288 bytes sampled for a type of which the eval had allocated 64,000,000. The runtime's diagnostics socket is `<TMPDIR>/dotnet-diagnostic-<pid>-<key>-socket` and a Unix socket path holds 107 characters, so a TMPDIR longer than 63 makes the runtime cut the socket's name short and `dotnet-trace` cannot attach (it says "The total length of the diagnostic socket path may exceed 108 characters"). The release gate's tier directories are 85 characters, so every profile case failed there while the same cases passed in a 59 character directory. A host is now started with TMPDIR spelled through a short link (`/tmp/sf-<hash>`) to the same directory when the real spelling is too long, and a host that still cannot be attached to is refused by name with the directory and the most it may be (`SocketPathTooLong`). macOS is not covered: its limit is 103 and the check runs on Linux only. |

## Why this item exists

FsiMcp 0.3.0 has a `profile` tool (read from its source on 2026-11-07, `src/FsiMcp/Profiler.fs`). It runs F# like `eval` while the server records the worker over EventPipe, and reports CPU time by method (self time, and the user's own frames by total time) or allocated bytes by type and by the nearest frame in the user's code. SageFs has nothing like it: I searched the tree for EventPipe, `DiagnosticsClient`, `nettrace` and `TraceEvent` and found no use in code. An agent that wants to know why an eval is slow has to guess, or write a Stopwatch loop. A tool that answers it is a feature a user reaches for, so SageFs loses on it today.

What FsiMcp's author found that I should keep: record only the sample profiler plus the JIT and loader events, because dotnet-trace's default provider set stretched one real 164 second run past 11 minutes (comment in `Profiler.fs`); count only threads running managed code, so idle pool threads do not drown the table; label time spent waiting for a garbage collection, because a large share of it means allocation is the cost; sample allocations about once per 100 KB; keep the diagnostics libraries out of the process that hosts the user's project.

## What done looks like

An agent calls `profile` with F# code and a mode (`cpu` or `alloc`). The code runs in the session like `send_fsharp_code`, so its bindings persist, and the reply is the eval's own output followed by a table. For `cpu`: methods by self time, the user's code by total time, and the share of time spent waiting for a garbage collection. For `alloc`: bytes by type, and by type at the nearest frame in the user's code. The recording is made from outside the process that runs the user's code, the diagnostics libraries never load into that process, and the trace file is deleted after it is read. A session that cannot be recorded says why in a typed reason, and a host that dies mid-recording gives an error with the crash text, never a hang or an empty table. The numbers beat or match FsiMcp's on the same code, which the benchmark harness checks.

## What exists today (checked in the tree)

- User code runs in an isolated FSI host process by default (`SageFs.FsiHost`, started by `FsiHostClient`). The client already holds the host's process id (`member _.ProcessId = proc.Id`, `SageFs.Core/FsiHostClient.fs:238`), so recording by pid is possible from the worker that owns the host, and the host loads nothing new.
- An in-process session has no separate process to record. It answers like `EvaluateMember` does for an in-process session: unavailable, with the reason (`IFsiSession.EvaluateMember`, `SageFs.Core/FsiSession.fs`).
- `Directory.Packages.props` has no `Microsoft.Diagnostics.NETCore.Client` or `Microsoft.Diagnostics.Tracing.TraceEvent`. FsiMcp uses both (`Microsoft.Diagnostics.NETCore.Client` 0.2.621003 and `Microsoft.Diagnostics.Tracing.TraceEvent` 3.1.19, from its `.fsproj`, referenced by the server process only). Adding them is a new NuGet dependency, which `AGENTS.md` says needs a discussion.
- Nothing in the host spawn sets `DOTNET_EnableDiagnostic`, so EventPipe's IPC socket should be available. I did not run a recording, so that is unconfirmed.

## Design

Pure part, `SageFs.Core/ProfileReport.fs`, no dependency past FSharp.Core: the report from a list of `Sample` values (a stack of frames, each with a method name and a module name, and a weight) for CPU, and from `AllocationTick` values (type name, bytes, stack) for allocation. `Mode` is a DU (`Cpu | Alloc`). Rows are sorted by weight, cut to `top`, and each row's share is of the total, so rows plus an "other" row sum to 100. A sample with no stack is counted and named, never dropped. Whether a frame is the user's is a function of a `FrameOwner` DU (`UserCode | RuntimeCode | Unknown`), so the rule is one tested function.

Edge, `SageFs.Core/ProfileRecorder.fs`: start a session on the host's pid with the two provider sets, run the eval, stop, convert the trace, read it, delete it. Every failure is a DU case with one `describe` (`HostGone`, `CouldNotAttach of reason`, `TraceTruncated`, `NoSamples`, `ProfilingUnavailable of SessionKind`). The reader is behind a port (`ReadTrace: path -> Result<Sample list, ProfileFailure>`), so the dependency sits in one project and the pure code needs none of it.

The tool, `profile`, takes `code`, `mode`, `top`, `session_id`. It is classed `Eval` (it runs arbitrary code as the daemon's user), so a role without eval cannot see or call it. It is not in the starter list of `small-default-tool-list`.

Eval-defined code needs thought. Code typed into the session lives in FSI's dynamic assemblies (`FSI_0003` and so on). For SageFs the rule is that a frame in the `FSI-ASSEMBLY` module, a frame whose type name starts with `FSI_` (or holds `$FSI_`), or a frame in a project assembly the session owns is the user's (`Frame.ownerOf`). Measured 2026-11-10: the Speedscope file names these frames, so the rule has something to read.

### As built (2026-11-10)

- `SageFs.Core/Features/ProfileReport.fs`: `ProfileMode`, `ProfileFailure` with `describe`, `Frame` and `Frame.ownerOf`, `StackSample`, the CPU report and the allocation report (`ProfileReport.cpu`, `ProfileReport.alloc`), bounded rendering. Pure.
- `SageFs.Core/Features/ProfileTrace.fs`: `SpeedscopeTrace.parse` and `cpuReport`, reading what `dotnet-trace convert --format Speedscope` writes. Pure.
- `SageFs.Core/Features/ProfileRecorder.fs`: `ProfileRecorder.around` over the `TraceCollector` port, and `DotnetTraceCollector`, the real collector that runs an installed `dotnet-trace` against the host's process id, stops it with SIGTERM and removes its files. `Timeouts.profileRecorderWait` bounds the attach and the stop.
- The worker answers a new `GetHostProcess` message with the host's pid (`SessionAgent.HostProcess`), and `SageFs/McpProfile.fs` is the tool's body. `profile` takes `agentName`, `code`, `mode`, `working_directory` and `session_id`; `top` is not a parameter (15 rows a table, 6,000 characters).
- `SageFs.Core/Features/ProfileNetTrace.fs`: `NetTrace.parse` and `allocReport`, reading the allocation ticks, stacks, methods and modules of a `.nettrace` file into `AllocSample` values. Pure, with no package. A CPU recording is Speedscope, an allocation recording is `<base>.nettrace` (`RecordedTrace`), and `McpProfile.answerOf` reads the one that its mode asks for.

## The failing test to write first

`SageFs.Tests/ProfileReportTests.fs`: `WHY - the rows of a CPU report plus its other row sum to the sampled total, whatever top is`. A property over generated samples. Fails to compile today. Then:

2. `WHY - a frame from an FSI_ type is the user's` (an example per owner case, plus a property that the rule is total).
3. `WHY - allocation rows sum to the sampled bytes, and the nearest user frame is the first user frame walking from the leaf`.
4. `WHY - a trace that ends mid-record gives TraceTruncated and still names what it read`. Twin: a reader that returns an empty list, which the test fails.
5. Simulation, `SageFs.Simulation/ProfileRecorderSim.fs` with `SageFs.Tests/ProfileRecorderSimTests.fs`: seeded events (attach, eval ends, host exits mid-recording, stream stalls, stop times out, disk full when writing the trace). Invariants: the call always returns, the trace file never outlives it, no outcome is both `Ok` and a failure. Twin: a recorder that leaves the file when the host dies, which fails the "never outlives" invariant. Durations come from `Timeouts`.
6. Outcome gate in the `--integration-host` tier: profile `List.init 200000 string |> List.length` on a real session and assert the report names a method from `FSharp.Core` or the runtime above zero, and that allocation mode reports `String` as the top type. Then a real host killed mid-profile returns the typed error.

## Steps

1. Measure first (below). Decide the dependency. Done: no package for either mode.
2. `ProfileReport.fs` and tests 1 to 3, RED then GREEN. Done (tests 1 to 3 are in `ProfileReportTests.fs`; the trace reader's are in `ProfileTraceTests.fs`).
3. `ProfileRecorder.fs` behind the port, with the simulation (tests 4 and 5). Done (`ProfileRecorderSimTests.fs`).
4. The `profile` tool in `SageFs/McpTools.fs`, its class in `Capability.ToolClass.toolsOf` (a test already holds the two tables together), the description kept under 400 characters (see `small-default-tool-list`), `docs/mcp-tools.md`. Done; the manual is longer than 400 characters and the list carries one sentence, as for every tool.
5. Test 6, the outcome gate. Written (`ProfileIntegrationTests.fs`: the hot function is named first as the user's own code, the allocating type leads the bytes by type and is placed at the function that allocated it, an unknown mode is refused with the code not run, a host that ends gives HostGone). Each half was proved by hand against a real host on 2026-11-10; the cases themselves run in the `--integration-host` tier.
6. A dashboard row is out of scope for this item.

Wire impact: `McpTools.fs`, `Capability.fs`, `docs/mcp-tools.md`, so `Plugin: n/a, an additive tool the plugin does not call`.

## Measure first

Results of 2026-11-10 on this machine (a daemon 0.6.941 with a bare session, `dotnet-trace` 10.0.745401, under a release gate and a loaded machine, so the cost figures are rough):

- Recording an FsiHost process from outside works: `dotnet-trace collect -p <host pid>` attaches in under a second with the daemon's own temp directory. Not measured: inside the sandbox the gate tiers use.
- Frames of eval-defined code carry usable names (`FSI-ASSEMBLY!FSI_0008.hotFib(int32)`). A recursive `hotFib` run for 20 calls was 90.7 percent of the managed CPU sampled.
- Attaching and stopping: under a second to attach, about two seconds to stop, write and convert a 16 MB trace.
- Allocation sampling against the program's own count: a record type the eval allocated 64,000,000 bytes of was reported as 64,008,288 sampled bytes, and the 1,319 ticks of a 160 MB run summed to 165,455,896.
- Not measured: the cost of attaching on a quiet machine, a recording inside the sandbox the gate tiers use, and a long recording (a trace is about 1 MB a second, and 128 MB is the most an allocation report reads).

The original list, for what stays open:

- Package weight (not needed: no package was added): the `.nupkg` size of SageFs with and without the two Microsoft.Diagnostics packages, and their licences (both MIT as far as I know; confirm). If the weight is large, the recorder can live in a separate optional package and the tool answers `ProfilingUnavailable` when it is absent.
- Does a recording of a real FsiHost process work in the sandbox the gate tiers use (`build/TierPlan.fs:351`, which rebinds `/tmp`)? EventPipe's socket lives in the temp directory.
- Do frames of eval-defined code carry a usable name in the trace? Record `let rec f n = if n < 2 then n else f (n-1) + f (n-2) in f 32` and read the stacks.
- Cost of attaching: the extra time on a short eval, measured on a quiet machine by the benchmark harness, not here.
- Does allocation sampling at one per 100 KB agree with `GC.GetTotalAllocatedBytes` for the same eval (the cost line in `eval-cost-line`) within a stated tolerance?

## Risks and dependencies

- A profile runs the user's code, so it carries the confirmation and role rules of `send_fsharp_code`.
- Hot reload and `run_app` hosts have `DOTNET_MODIFIABLE_ASSEMBLIES` set. Record one to see whether the trace still reads.
- Depends on the decision about a new dependency (none was needed). Pairs with `eval-cost-line` (the same host process, the same bytes).
- The recorder attaches to the host before the eval is handed to it, so a profile that queues behind another agent's eval in the same session records that eval's time and allocations too. The table does not say so yet. A session an agent shares with others should not be profiled while another eval is running.
- Two profiles of one session at once each start their own recording of the same host, and each sees the other's eval.

## Size

M. The pure report is small. The recorder and its failure cases are the work.

## Decisions for Will

- The allocation reader is our own (`NetTrace`, about 250 lines, tested against a builder in the tests and a real trace by hand), and no package was added. The format is Microsoft's and moves: if a runtime changes the compressed headers or the payload of event 10, the reader refuses by name (`TraceUnreadable`), and the integration tier is what notices. Say if you would rather depend on `Microsoft.Diagnostics.Tracing.TraceEvent` for that reason, which would replace `ProfileNetTrace.fs`.
- In-process sessions: refused with the reason (done, as recommended). Say if the worker should be recorded from the daemon instead.
- Windows: stopping `dotnet-trace` needs a signal this build sends on Linux and macOS only, and a Windows daemon refuses with that reason. Say if Windows matters enough to build a console-control-event stop.
