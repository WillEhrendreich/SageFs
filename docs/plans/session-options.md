# Session options a user can set

Roadmap item: `session-options` (Later, Repl). Plan written 2026-11-07 against master 4827ab9a.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run the first "Measure first" probe (do `DefaultArgs` and the config's `InitScript` reach a session at all?), then write the first failing test: `SessionOptionsTests`, a Server GC choice becomes `DOTNET_gcServer=1` in the host's environment. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

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
