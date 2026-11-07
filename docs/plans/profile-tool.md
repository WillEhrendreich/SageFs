# A profile tool: where the time and the allocations went

Roadmap item: `profile-tool` (Later, Repl). Plan written 2026-11-07 against master 4827ab9a.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M for the CPU report, S for the allocation report once the recorder exists |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run "Measure first" (the dependency size and whether eval-defined code shows in a trace), then write the first failing test: `ProfileReportTests`, rows of a CPU report sum to the sampled total. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

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

Eval-defined code needs thought. Code typed into the session lives in FSI's dynamic assemblies (`FSI_0003` and so on), which may have no module file in a trace. FsiMcp treats an empty module name as not the user's, so a function defined in the eval itself may not appear in its "your code" table. I have not run it, so I do not claim it. For SageFs the rule is that a frame whose type name starts with `FSI_` or whose module is a project assembly the session owns is the user's. That is a test and a Measure-first item.

## The failing test to write first

`SageFs.Tests/ProfileReportTests.fs`: `WHY - the rows of a CPU report plus its other row sum to the sampled total, whatever top is`. A property over generated samples. Fails to compile today. Then:

2. `WHY - a frame from an FSI_ type is the user's` (an example per owner case, plus a property that the rule is total).
3. `WHY - allocation rows sum to the sampled bytes, and the nearest user frame is the first user frame walking from the leaf`.
4. `WHY - a trace that ends mid-record gives TraceTruncated and still names what it read`. Twin: a reader that returns an empty list, which the test fails.
5. Simulation, `SageFs.Simulation/ProfileRecorderSim.fs` with `SageFs.Tests/ProfileRecorderSimTests.fs`: seeded events (attach, eval ends, host exits mid-recording, stream stalls, stop times out, disk full when writing the trace). Invariants: the call always returns, the trace file never outlives it, no outcome is both `Ok` and a failure. Twin: a recorder that leaves the file when the host dies, which fails the "never outlives" invariant. Durations come from `Timeouts`.
6. Outcome gate in the `--integration-host` tier: profile `List.init 200000 string |> List.length` on a real session and assert the report names a method from `FSharp.Core` or the runtime above zero, and that allocation mode reports `String` as the top type. Then a real host killed mid-profile returns the typed error.

## Steps

1. Measure first (below). Decide the dependency.
2. `ProfileReport.fs` and tests 1 to 3, RED then GREEN.
3. `ProfileRecorder.fs` behind the port, with the simulation (tests 4 and 5).
4. The `profile` tool in `SageFs/McpTools.fs`, its class in `Capability.ToolClass.toolsOf` (a test already holds the two tables together), the description kept under 400 characters (see `small-default-tool-list`), `docs/mcp-tools.md`.
5. Test 6, the outcome gate.
6. A dashboard row is out of scope for this item.

Wire impact: `McpTools.fs`, `Capability.fs`, `docs/mcp-tools.md`, so `Plugin: n/a, an additive tool the plugin does not call`.

## Measure first

- Package weight: the `.nupkg` size of SageFs with and without the two Microsoft.Diagnostics packages, and their licences (both MIT as far as I know; confirm). If the weight is large, the recorder can live in a separate optional package and the tool answers `ProfilingUnavailable` when it is absent.
- Does a recording of a real FsiHost process work in the sandbox the gate tiers use (`build/TierPlan.fs:351`, which rebinds `/tmp`)? EventPipe's socket lives in the temp directory.
- Do frames of eval-defined code carry a usable name in the trace? Record `let rec f n = if n < 2 then n else f (n-1) + f (n-2) in f 32` and read the stacks.
- Cost of attaching: the extra time on a short eval, measured on a quiet machine by the benchmark harness, not here.
- Does allocation sampling at one per 100 KB agree with `GC.GetTotalAllocatedBytes` for the same eval (the cost line in `eval-cost-line`) within a stated tolerance?

## Risks and dependencies

- A profile runs the user's code, so it carries the confirmation and role rules of `send_fsharp_code`.
- Hot reload and `run_app` hosts have `DOTNET_MODIFIABLE_ASSEMBLIES` set. Record one to see whether the trace still reads.
- Depends on the decision about a new dependency. Pairs with `eval-cost-line` (the same host process, the same bytes).

## Size

M. The pure report is small. The recorder and its failure cases are the work.

## Decisions for Will

- Add the two Microsoft.Diagnostics packages to `Directory.Packages.props`, or write a minimal IPC client and nettrace reader of my own? Recommendation: the packages, in one project, since the format is Microsoft's and moves.
- In-process sessions: refuse with a reason (my recommendation), or record the worker from the daemon?
