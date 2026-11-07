# Check a whole project with some files replaced, without building

Roadmap item: `project-check-with-overlays` (Next, Repl). Plan written 2026-11-07 against master 27163dfa.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress (phase A of C: the pure core, its tests and a simulation) |
| Size | M |
| Branch and worktree | `cohort/p4-projectcheck` in `/home/will/scratch/cohort/p4-projectcheck` |
| Last green commit | none yet |
| Next action | Phase A is the pure `ProjectCheckCore` (arguments, overlays, cache decision, staleness, diagnostics, report), its Expecto tests and `ProjectCheckSim`. Phase B is the effect module `ProjectCheck` (MSBuild process, held checker, overlay files) and one real-compiler case. Phase C is the `check_project` registration, ready as a patch in `docs/plans/project-check-with-overlays-tool-patch.md`, applied after the tool-list work lands. |
| Decisions made | 2026-11-07, the packet agent: overlays are written as stable files under a per-project cache directory and the compiler is given those paths, instead of the transparent compiler's in-memory snapshot (see "Overlays"). Arguments come from a child `dotnet msbuild` process, never from MSBuild loaded into the daemon. |
| Surprises | The design-time MSBuild run writes `obj/<config>/<tfm>/<Project>.AssemblyInfo.fs` and the `AssemblyAttributes.fs` file when they are missing. It builds nothing else. The plan keeps those two files as the cost of the run. |

## Why this item exists

FsiMcp 0.3.0 has a `check_project` tool (read from its source and `docs/fsimcp-comparison.md` on 2026-11-07). It type-checks a whole F# project with some files replaced by text the caller sends, without building, and without touching the REPL session. The question it answers is "does the project still compile with this file changed". An agent about to land an edit asks it instead of running `dotnet build`, which is minutes on a loaded machine and writes to `bin` and `obj`.

SageFs cannot answer that question today. `check_fsharp_code` checks a snippet against the session's context and says nothing about the files of the project. A build answers it and costs a build. The compiler call that would answer it exists twice in the tree, and both callers are bound to a loaded session (below).

## What exists today (checked in the tree)

- `SageFs.Core/Features/CallerCheck.fs`, `compilerAnswerAsync` (line 197) calls `projectChecker.Value.ParseAndCheckProject options` (line 200) under `Timeouts.callerCheck`, with a checker of its own made with `FSharpChecker.Create(projectCacheSize = 3)`. It reads `GetAllUsesOfAllSymbols` and the error lines of `results.Diagnostics`. It takes `FSharpProjectOptions` from the caller.
- `SageFs.Core/Features/SymbolReferenceCheck.fs`, `checkProject` (line 112) calls `checker.ParseAndCheckProject options` through a held checker, `SymbolReferenceCheck.cache`, which is a `CacheHolder<FSharpChecker>` with a resident-bytes ceiling, an idle retention (`Timeouts.referencesCacheRetention`) and a sweep timer. It also takes `FSharpProjectOptions` from the caller.
- Both get their options from the worker's loaded session (`ActorCreation.CheckOptions`, produced by Ionide.ProjInfo, with `ManualProjectParse` as the fallback in `SageFs.Core/ProjectLoading.fs`). A project with no session has no options, and a session's options are the ones the session was started with, so they do not follow an edit to the `.fsproj`.
- Nothing in the tree runs MSBuild's design-time path to get compiler arguments. I searched `SageFs`, `SageFs.Core`, `SageFs.Host` and `scripts` for `ProvideCommandLineArgs`, `SkipCompilerExecution` and `GetProjectOptionsFromCommandLineArgs` and found no use in code.
- `Timeouts` has `callerCheck` (three single-file checks) and `referencesCheck` (60 s, all projects). Neither names this wait.

## What was probed (2026-11-07, throwaway two-project lab under the scratchpad, SDK 11.0.100-rc)

`dotnet msbuild App.fsproj -t:ResolveReferences,FindReferenceAssembliesForReferences,BeforeBuild,BeforeCompile,CoreCompile -p:DesignTimeBuild=true -p:SkipCompilerExecution=true -p:ProvideCommandLineArgs=true -p:BuildProjectReferences=false -getTargetResult:CoreCompile -getItem:ProjectReference -getProperty:MSBuildAllProjects,TargetPath,Configuration` run from the project directory:

- `CoreCompile` succeeds without compiling and returns the Fsc command line as items. Each item's `Identity` is one argument: `--define:...`, `--langversion:11`, `-r:<path>` (169 of them, framework reference assemblies, FSharp.Core and the referenced project's reference assembly under its `obj/<config>/<tfm>/ref/`), and the source files last, relative to the project directory (`A.fs`, `Program.fs`) plus the generated `obj/.../App.AssemblyInfo.fs` and `.NETCoreApp,Version=v10.0.AssemblyAttributes.fs`.
- `FullPath` on the items resolves against the process's current directory, so it is wrong for options and cannot be used for sources. The run starts in the project directory and the reader resolves each source from `Identity` itself.
- Without `FindReferenceAssembliesForReferences` the run returned no `-r:` at all (it answered Success with an empty reference list). That failure is silent, so the reader refuses an argument list with no `-r:` for FSharp.Core.
- `MSBuildAllProjects` lists every project, props and targets file the evaluation read (the `.fsproj`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `obj/*.nuget.g.targets` and the SDK's own). That list is the cache's fingerprint input, so a change to any file that shaped the arguments refreshes them with no hand-kept list of file names.
- With `BuildProjectReferences=false` the run does not build the referenced project. Its `-r:` path was a reference assembly that did not exist yet: that is how a stale or never-built reference shows.

A timing figure taken on this machine while its load average was above 40 is not written down. The command to measure is under "Measure first".

## What done looks like

An agent calls `check_project` with a project path and zero or more overlays (path and text). The reply is the diagnostics of the project's compile as the compiler sees it with those files replaced: for each one the path, line, column, end line, end column, severity, code and message, plus a verdict (`Clean`, `HasErrors`, or a typed refusal). Nothing is built into `bin`, no session is used, and the REPL session of the caller is untouched.

- A second call on the same project with a different overlay does not run MSBuild again and does not recheck the files before the overlay.
- A change to the project file, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props` or a restore's `nuget.g.targets` makes the next call run MSBuild again. Any other change does not.
- A referenced project whose reference assembly is missing or older than its sources is built once before the check, and the reply says so. A referenced project that is current is not built.
- `notifications/cancelled` for the call stops the MSBuild process (the whole process tree) or the compiler check, and the reply says which it stopped.
- An overlay for a file the project does not compile is refused with the project's file list, and so is the same path twice. A wrong overlay never reaches the compiler.
- The diagnostics are typed. The check carries a bound from `Timeouts`, and a check that ran out of time returns `TimedOut` with the files it had finished, not a hang and not an empty `Clean`.

## Design

Pure, `SageFs.Core/Features/ProjectCheckCore.fs`, no dependency past FSharp.Core and `SageFs.Json`:

- `Severity` (closed DU, one `Severity.toWord`), `CheckDiagnostic` (path, line, column, end line, end column, severity, code, message), `Overlay`, `OverlayRefusal`.
- `CompilerArguments.ofIdentities`: splits the identities of `CoreCompile` into options and sources, resolves sources against the project directory, and refuses an empty source list or a list with no FSharp.Core reference, each with its own reason.
- `MsBuildFacts.read`: reads the JSON of the design-time run (through `SageFs.Json`) into the arguments, the project references, the fingerprint inputs and the configuration, or a typed refusal when the target failed.
- `Fingerprint`: the (path, last write, length) of every file in `MSBuildAllProjects`. `ArgumentCache.decide` takes the stored entry and the current fingerprint and answers `Reuse` or `Refresh of reason` (`NotCached`, `FileChanged of path`, `FileGone of path`). The key is project path plus configuration.
- `Staleness.decide`: a referenced project's output time against the newest of its inputs, answering `Current`, `Missing` or `OlderThan of path`.
- `Overlays.resolve`: validates overlays against the project's sources and answers the source list the compiler gets, with the stable overlay path for each replaced file, or an `OverlayRefusal`. `Overlays.mapBack` turns a diagnostic's overlay path into the project path the caller sent.
- `Report.verdict` and `Report.render`: diagnostics sorted by path, line, column; capped lines with a count of the rest; the one function that writes the text.

Edge, `SageFs.Core/Features/ProjectCheck.fs`, built on the `ParseAndCheckProject` call `CallerCheck` and `SymbolReferenceCheck` already make:

- `MsBuildRunner`: starts `dotnet msbuild` as a child process in the project directory with the command above, reads stdout and drains stderr on its own thread (an unread stderr pipe fills and blocks the child), and on cancellation kills the whole process tree. Named waits in `Timeouts`: `projectArgumentsRun`, `projectCheck`, `projectReferenceBuild`.
- `ProjectCheck.run`: reads the cached or fresh arguments, builds a stale referenced project once (`dotnet build <ref> --no-dependencies`, same cancellation), writes the overlays, builds `FSharpProjectOptions` with `GetProjectOptionsFromCommandLineArgs`, and calls `ParseAndCheckProject` on a `CacheHolder<FSharpChecker>` with the same resident-bytes ceiling and idle retention `SymbolReferenceCheck` uses. Two concurrent calls for the same key share one MSBuild run.

### Overlays

The compiler reads source files from disk. The classic checker has no in-memory source for a project file. The transparent compiler has `FSharpProjectSnapshot`, which does, but it is a different backend with a different cache, and `SageFs` does not run it anywhere today. The first version writes each overlay to `<temp>/sagefs-project-check/<project hash>/<index>/<file name>` and gives the compiler that path. The path is stable for a project, so the compiler's per-file cache sees only the overlay's own timestamp change and keeps the checks of every file before it. The write is skipped when the bytes are the same, so an unchanged overlay keeps its timestamp. Two consequences, both stated in the tool's description: `__SOURCE_DIRECTORY__` and `__SOURCE_FILE__` inside an overlaid file name the overlay directory, and a relative `#load` inside an overlaid file resolves from there. If the transparent compiler becomes the default backend, this becomes a snapshot and the files go away.

## The failing test to write first

`SageFs.Tests/ProjectCheckTests.fs`, in this order, each RED before its code:

1. `WHY - an overlay for a file the project does not compile is refused with the project's files`, a property over random project file sets and a path outside them.
2. `WHY - the same overlay path twice is refused`.
3. `WHY - the identities of CoreCompile split into options and sources, and sources resolve against the project directory, not the process directory`.
4. `WHY - an argument list with no reference to FSharp.Core is refused, not checked` (the silent failure in the probe).
5. `WHY - the cache reuses when every file of the fingerprint is the same, and refreshes naming the file that changed or went` (property over random fingerprints and one random mutation).
6. `WHY - a change to Directory.Packages.props refreshes the arguments`, `WHY - a change to a file outside the fingerprint does not`.
7. `WHY - a referenced project with no output, or an output older than an input, is stale; a newer output is current`.
8. `WHY - diagnostics from an overlay file report the path the caller sent`.
9. `WHY - the report lists errors before warnings, in path, line, column order, and counts what it cut`.
10. Real compiler (`--integration-host` is not needed, this is the compiler in process, like `CallerCheckTests`): a fixture project of two files on disk checks clean; the overlay that breaks the second file's use of the first reports the error at the right line and column in the project's path; the overlay that fixes it reports `Clean`; the disk files are unchanged afterwards.

`SageFs.Simulation/ProjectCheckSim.fs` and `SageFs.Tests/ProjectCheckSimTests.fs`: a seeded history of project-file edits, props edits, source edits, calls with overlays, calls that are cancelled mid-run and a concurrent pair of calls, over the real `ArgumentCache.decide` and `Staleness.decide`. Invariants: the arguments a call used are never older than a file in its fingerprint; a cancelled run leaves no entry; two concurrent calls for one key run MSBuild once; a referenced project is built at most once between two edits of its inputs. The twins: a cache that ignores `Directory.Packages.props`, a cache that stores an entry from a cancelled run, a coalescer that starts one run per call, and a staleness rule that always says Current. Each must violate its invariant, so the test has teeth.

## Steps

1. Plan and roadmap item (this commit).
2. Tests 1 to 9 and `ProjectCheckCore.fs`, one RED commit then one GREEN.
3. `ProjectCheckSim` and its tests with the twins.
4. `Timeouts` entries, the effect module, and test 10 against a fixture project.
5. The tool patch file for the conductor (`docs/plans/project-check-with-overlays-tool-patch.md`).
6. After the tool lands: the roadmap landmark, `docs/mcp-tools.md`, and Status `Done`.

Wire impact: none in phase A. The tool patch touches `SageFs/McpTools.fs`, `SageFs.Core/Affordances.fs`, `SageFs.Core/Capability.fs`, `docs/mcp-tools.md` and two test tables, so its commit carries a `Plugin:` trailer.

## Measure first

- Cold and warm time of the design-time MSBuild run on `SageFs.Tests.fsproj`, and of the check with one overlay, on a quiet machine. Command, until the `repl-benchmark-against-other-tools` harness exists: `time dotnet msbuild SageFs.Tests/SageFs.Tests.fsproj -nodeReuse:false -t:ResolveReferences,FindReferenceAssembliesForReferences,BeforeBuild,BeforeCompile,CoreCompile -p:DesignTimeBuild=true -p:SkipCompilerExecution=true -p:ProvideCommandLineArgs=true -p:BuildProjectReferences=false -getTargetResult:CoreCompile`. No figure is written here from a loaded machine.
- Resident bytes of the held checker after a check of `SageFs.Tests`, to confirm the ceiling `SymbolReferenceCheck` uses is right for a daemon-side holder.
- FsiMcp's own figures for the same project, for the comparison page. Running their package needs Will's go and a sandbox.

## Risks and dependencies

- The overlay directory approach changes `__SOURCE_DIRECTORY__` inside an overlaid file. Documented, and the snapshot route removes it.
- A design-time run writes the two generated `obj` files when they are missing. It is the same write a build makes first.
- The tool registration waits for the small-default-tool-list work (`docs/plans/small-default-tool-list.md`), because the tool's description has to fit the budget that work sets.
- Depends on nothing else. `list_references` and the caller check keep their own checkers.

## Size

M.

## Decisions for Will

- Whether the check runs in the daemon (this plan, one held checker for every caller) or in a worker. A worker means a session, which the item is meant to avoid.
- Whether a referenced project that is stale is built by the tool (this plan, once, and the reply says so) or refused with the command to run.
