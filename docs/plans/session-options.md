# Session options a user can set

Roadmap item: `session-options` (Later, Repl). Plan written 2026-11-07 against master 4827ab9a.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Done, 2026-11-10 (all six steps). The integration-tier gates have run on this machine only, and "Next action" lists what is unproven. |
| Size | M |
| Branch and worktree | master, the main checkout |
| Last green commit | `git log --oneline -- SageFs.Core/SessionOptionSettings.fs` names the commit that added the rows |
| Next action | Nothing is open in the item. What is still unproven: the full gate has not run these cases on a second machine or under both SDKs; the artifacts conflict check asks MSBuild about the first project of a session only, and not at all for a solution target; the settings rows show what the last session started in the directory resolved, and a session's own status does not yet print its options (it would be a wire change). A second row can also be edited from the page while `config.fsx` sets the same option, and the row says that the file wins but the page does not refuse the save. |
| Decisions made | `InitScript` is deleted, in favour of the file-name discovery that works (`.SageFs/init.fsx`). `IsRoot` and `SessionName` are deleted with it: the Readme said they were parsed and stored and "not wired to anything", and a grep finds no reader. `DefaultArgs` is wired. `Gc` and `SessionEnv` are config-only, as recommended. The GC cases are `WorkstationGc` and `ServerGc`: a case named `Server` makes the compiler resolve `SageFs.Server.HttpOriginGuard...` in patterns as the case, and `SageFs.Host/WorkerHttpTransport.fs` stopped compiling. |
| Surprises | All the options travel one way, and a roast (2026-11-08) said the first version had three: the daemon resolves a directory's config once, when a session is created (`DirectoryConfig.prepareForCreate`, the only writer of `SessionOptionsRegistry`, counted by a ratchet), the daemon's own builds read that entry, the worker is spawned with the whole options value in one variable (`SAGEFS_SESSION_OPTIONS`), and the worker, its rebuilds and its project loader read that variable and nothing else (the worker no longer evaluates `config.fsx`, which also removed one FSI host start per session). `DirectoryConfig.load` returns the config and writes nothing, so a dashboard render cannot change what a build uses. The artifacts path must be absolute: a relative one is refused with its reason instead of guessed against a directory. The same wire lesson as before: the FSI host's protocol has no tuples, so anything in `DirectoryConfig` is a record, a union or a list of them. |

### The 2026-11-10 pass: rows, outcome gates, per-session options, the project's own ArtifactsPath

What was built, and what the work found.

- **Rows.** `SageFs.Core/SessionOptionSettings.fs` declares `session.gc`, `session.environment` and `session.artifactsPath` in the Session group. They are repo-overridable and apply to the next session started in the directory, which is a new applicability, `NextSession` (chip "next session"), because "restart" said the daemon and these rows do not need one. Text becomes a value in one place, the descriptor's parser, which uses the checks `config.fsx` uses, so a refused entry says the same thing from either file. A refused save writes nothing and shows its reason and next step in the panel's banner.
- **Who wins.** Option by option, a `config.fsx` field that is not its default beats the row; the repo layer beats the global layer. `config.fsx` cannot ask for the default over a saved row, because "left at the default" is how the file says nothing. The row says under its value when the file supplied the option at the last create.
- **Open question (c), two sessions in one directory.** Decided: each session carries its own options from its first spawn. `SessionOptionsRegistry` now has a pending book (by directory, written by create) and a held book (by session id, fixed at the first spawn, released on stop). A restart, rebuild or reset reads the held book, and a build finds its session by directory and projects, which the manager makes unique. The red case: `remember dir A`, spawn, `remember dir B`, and a read for the first session returned B. The outcome gate restarts the first session's worker after a second create and reads `IsServerGC` back.
- **Open question (d), a project that sets `ArtifactsPath` in its own props.** Tested with real MSBuild: an environment variable loses to the project's props, a `-p:` global property wins over them. SageFs passed the artifacts path to builds as an environment variable and to the project loader as a global property, so the build wrote to the project's folder and the loader looked in the session's. Decision: the project's choice stays and the session's request is refused with a message naming both folders and what to do. Builds now pass `-p:ArtifactsPath=` (escaped for `%`, `;` and `,`), so the build and the load always read one folder, and `ProjectArtifactsProbe` asks MSBuild before the session starts, with no `ArtifactsPath` in its environment, so imports and `UseArtifactsOutput` count. The probe asks about the first project only.
- **Found on the way, all with the artifacts path.** The daemon resolved options after the recovery build and the preflight, so a first create built without the path; it resolves them first now. `BuildPreflight` looked for `obj/project.assets.json` under the project, which an artifacts path moves, so a create would have refused with `NeedsRebuild` after a good build; `checkUnder` looks under the artifacts path. The host's `AppContext.BaseDirectory` (issue 142's way back to the project's output) and the native-library restore record were found only under the project's own `bin` and `obj`; both follow the artifacts path now.
- **Gates.** `SageFs.Tests/SessionOptionsOutcomeTests.fs` (integration-host tier) starts real workers and reads back: Server GC from `config.fsx`, a variable from `config.fsx`, both from rows, precedence of file over row, the artifacts folder holding `Small.dll` with the project's `bin` and `obj` absent and the host's output folder under it, and the restart case. `SessionOptionSettingsTests.fs` has the pure cases and three real-MSBuild cases (the probe, the props conflict end to end, an odd folder name).

### The probe, 2026-11-07

Question: do `InitScript` and `DefaultArgs` reach a session? Answer from the tree: no. A grep of every `.fs` and `.fsx` file for both names finds the type, the default, the equality check in `DirectoryConfig.ensureAutoOpenNamespacesOptIn` and tests that set them and read them back. The only place the FSI host's arguments are made is `AppState.createFsiSession` (`solutionToFsiArgs`), and it never read a config. The Readme already said `InitScript`, `DefaultArgs`, `IsRoot` and `SessionName` "are parsed and stored but not wired to anything yet". That is a defect by the plan's own rule (accepted and ignored is worse than absent). The guard is `SessionOptionsTests`: every field of `DirectoryConfig` has a row in `SessionOptions.fieldReaders` naming who reads it, and a field that the session host reads must change what the host gets.

## Why this item exists

FsiMcp 0.3.0's `load_project` takes four options that stay in force across loads, resets and timeout restarts until changed: `prelude` (a script loaded into every new session, its module and top-level opens opened), `gc_server` (the session runs under Server GC, so code is timed under the GC it will really run with; the author says allocation-heavy code can run about twice as fast under it), `artifacts_path` (a directory for `bin` and `obj`, so nothing is written under the project), and `env` (environment variables for the session process). Read from `Server.fs` and `docs/how-it-works.md`. A person who benchmarks in the REPL, or whose project builds into an artifacts folder, reaches for each of them. The question for each is whether SageFs already has it.

## What exists today (checked in the tree), option by option

- Prelude: it exists, under another shape. `StartupProfile.tryFindInitScript` finds `.SageFs/init.fsx` or `.SageFsrc` in the primary project's folder and then the session's folder, and the script is evaluated in every new session (`SageFs.Core/StartupProfile.fs`, `RespawnAfterReap.fs`). The text is evaluated as written, so what it defines is in scope unqualified, which is the effect FsiMcp gets by `#load`ing the file and opening its module. I did not trace whether a reset re-runs it; a test in step 1 should say. A hot-reload level swap wipes the definitions, including the init script's, as `level-the-repl-without-losing-it` says. So this option is not a gap, and the item does not rebuild it. What differs: FsiMcp sets it in a tool call and remembers it, and SageFs finds a file by name.
- A real question about the config fields: `DirectoryConfig` has `InitScript` and `DefaultArgs` (`SageFs.Core/DirectoryConfigTypes.fs`), and a test shows `config.fsx` can set them (`DirectoryConfigTests.fs:36` and `:40`). I found no reader of either field outside those tests and an equality check in `SageFs/DirectoryConfig.fs:129`. If that holds they are accepted and ignored, which is worse than absent. The first probe settles it.
- Server GC: absent. I searched for `DOTNET_gcServer`, `ServerGarbageCollector` and `gcserver` in the sources and the project files and found nothing. A session's FSI host runs under whatever the tool's runtime config and the daemon's environment give it.
- Environment variables for the session process: the seam exists and has no user. `FsiHostClient` takes `Environment: (string * string) list` for the host process (`SageFs.Core/FsiHostClient.fs:58`), and `IsolatedFsiSession` fills it with the project output, native paths, roll-forward and value-read settings (`IsolatedFsiSession.fs:774`). Nothing a user wrote reaches it. Workers inherit the daemon's environment minus a list of MSBuild poisoners (`ProcessEnvironment.poisonedVariables`), so a variable set before starting the daemon reaches every session, but not one session.
- Artifacts path: absent as a setting. SageFs asks MSBuild for the project's `TargetPath` through Ionide, so a project that already builds into an artifacts folder works as an input, and the watcher skips a directory named `artifacts` (`FileWatcher.excludedDirNames`). I found no way to tell SageFs's own session build to put its output somewhere else, such as `-p:ArtifactsPath=<dir>`. A person who does not want a session's build to dirty the project's `bin` and `obj` has no switch.

## What done looks like

A user sets, per directory in `.SageFs/config.fsx` and at session creation in the dashboard and the MCP tools: the GC mode (Workstation or Server), an artifacts path for the session's own builds, environment variables for the session process, and the prelude. Each is one named field with a closed type, shown in the session's status so the person can see what the session runs under (the runtime line already says the .NET version). A setting that cannot be honored says why: an environment variable that is on the poisoned list is refused, naming the reason, and an artifacts path the build cannot write is refused before the build. The options stay in force across resets and restarts of that session, and a second session in another directory is not affected. The fields that are accepted and ignored today either work or are removed. The outcome gate runs a real session for each option and reads the effect back from inside the host: `GCSettings.IsServerGC`, an environment variable's value, the path of the built assembly.

## Design

Pure, in the file `DirectoryConfigTypes.fs` (dependency-free, compiled into the daemon and the host): `type GcMode = Workstation | Server` (no `bool`), `type SessionEnv = SessionEnv of (string * string) list` built only by `SessionEnv.tryCreate`, which refuses a poisoned name and an empty name with a typed reason, and `ArtifactsPath` built only by a smart constructor that makes the path absolute and refuses one under `/proc` or empty. `DirectoryConfig` gains `Gc`, `SessionEnv` and `ArtifactsPath`, and `DirectoryConfigDefaults.empty` leaves them at today's behavior. The fields `InitScript` and `DefaultArgs` get their first probe's verdict: wired, or deleted in the same commit as the wiring of the others.

Edge: the three options meet `IsolatedFsiSession.options.Environment` (`DOTNET_gcServer=1` for `Server`, the user's list after the SageFs ones, with a SageFs name winning a clash and the clash reported) and the session build's argument list (`-p:ArtifactsPath=`, in `SessionBuild.fs` beside `-p:Optimize=false`). The output assembly lookup must follow the artifacts path (it reads `TargetPath` from the evaluation, so it should; a test proves it). The settings catalog and the dashboard panel list the options (`SettingsCatalog.fs`), and an MCP creation parameter is added only if the benchmark and first-run work say it earns its place in the tool list (see `small-default-tool-list`).

## The failing test to write first

`SageFs.Tests/SessionOptionsTests.fs`: `WHY - a Server GC choice puts DOTNET_gcServer=1 in the host's environment and Workstation does not`. A pure function from `DirectoryConfig` to the host's environment list. Fails to compile today. Then:

2. `WHY - a session environment entry on the poisoned list is refused with its reason, and the rest are kept`. Property over generated names.
3. `WHY - a user variable that clashes with one SageFs sets is reported and SageFs's value wins`.
4. `WHY - the session build's arguments carry the artifacts path and the output assembly is found under it` (a real build of a tiny project in a temp directory, in the integration tier).
5. `WHY - an option set for one session does not reach another` (two sessions, two directories).
6. Outcome gate: a real session with `Server` reports `GCSettings.IsServerGC = true` from an eval; with an environment entry reports its value from `Environment.GetEnvironmentVariable`; with an artifacts path leaves the project's `bin` and `obj` untouched, checked by a file listing before and after.
7. `WHY - a config field that a config file can set reaches the session` for each field of `DirectoryConfig`, a property over the record's fields by reflection, so a future dead field fails the build. This is the guard against what `InitScript` and `DefaultArgs` may be today.

## Steps

1. Probe: a throwaway daemon (port 38300 to 38399, `--owner-pid`), a config that sets `DefaultArgs = ["--no-warn:1182"]` and an `InitScript`, and a session that shows whether either took effect. Record the result here. Test 7 follows from it.
2. Test 1 and the pure types.
3. The environment path (tests 2, 3, 5, 6), with Server GC as its first user.
4. The artifacts path (test 4).
5. Settings catalog and dashboard rows, docs in `docs/configuration.md` and the session docs.
6. Fix or remove the dead fields (test 7).

Wire impact: `DirectoryConfigTypes.fs` is compiled into the FSI host, so the host and the daemon move together. Settings payloads gain fields: check `sagefs.nvim` and `sagefs-vscode` for readers of the settings list before choosing `Plugin: done` or `Plugin: n/a`.

## Measure first

- The probe above: do `DefaultArgs` and the config's `InitScript` do anything? It decides whether the item removes two fields.
- Server GC on a real session: does `DOTNET_gcServer=1` in the host's environment take effect on the isolated host (the host is started through `dotnet`, so the variable should apply), and what does it do to the host's memory on this machine (it commits a heap per core)? Check the host's resident size with and without, on the quiet machine. Not written here.
- Does a project with `UseArtifactsOutput` or `ArtifactsPath` set in `Directory.Build.props` load, build and reload correctly today? If it does, the setting only matters for a project that does not set it itself.
- How much of FsiMcp's claim, allocation-heavy code about twice as fast under Server GC, holds in the host. That is for the benchmark harness, on a quiet machine, and the figure is not written here.

## Risks and dependencies

- Server GC raises memory use. It stays opt-in and the status line names it. The daemon already supervises memory (`MemorySupervisor`), so the admission code should see a larger host.
- An environment variable can change how the user's own code behaves, which is its purpose. The refusal list is the guard for SageFs's own needs.
- `DirectoryConfigTypes.fs` is compiled into two projects. A field added there changes the host's wire (`ConfigResult`), so the change is made in one commit across both.

## Size

M. The types and the environment path are small. The artifacts path and the outcome gates are the cost.

## Decisions for Will

- Delete `InitScript` and `DefaultArgs` if the probe shows they do nothing, or wire them? Recommendation: wire `DefaultArgs` (a person may want `--no-warn`), and delete `InitScript` in favor of the file-name discovery that works, saying so in the docs.
- Should Server GC be settable per session from the MCP creation tools, or only in `config.fsx` and the dashboard? Recommendation: config and dashboard only, so the tool list does not grow.
