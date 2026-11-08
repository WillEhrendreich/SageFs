# More settings editable from the dashboard

Roadmap item: `more-settings-editable` (Next, Dashboard). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress (step 8 of 8) |
| Size | M |
| Branch and worktree | `worktree-agent-aeee5e5c5dad3fa70`, `/home/will/Work/SageFs/.claude/worktrees/agent-aeee5e5c5dad3fa70` |
| Last green commit | 1d187af6 on the branch: unfiltered default suite Trusted (15049 of 15049), ratchet lane Trusted (527 of 527), browser journey passed (filtered to its one case) |
| Next action | Ship it with the next release (the gate, then `scripts/ship.fsx`, with the maintainer's go). Every `SAGEFS_*` variable `Timeouts.fs` and `SageFsConfig.fs` read has a row, and the count of those without one is 0: a budget table (`variables without a settings row`) that `--ratchets --tighten` can lower and that a new variable fails. The cells the browser journey does not yet cover are listed under "Browser journey". |
| Decisions made | 2026-11-07, taken from this plan's recommendations by the implementing agent, Will not yet asked: (1) option A, saved values imported into the process environment at start (`SettingsBootstrap.importSaved`), because workers inherit the environment. Replaced on 2026-11-08 by option B (a settings snapshot, see "The settings snapshot and the short list" below); (2) an edit under an overriding variable or flag is accepted and the row says "Saved X. Ignored while NAME is set."; (3) bind host and identity policy stay read-only, and the bind host row now reads `SAGEFS_BIND_HOST`. |
| Surprises | See "Surprises" below. |

## What done looks like

You open `/dashboard/settings` and every setting someone plausibly has to change on a real machine is a row. Each row shows the
value in use right now, the layer it came from (default, global file, repo file, environment variable, command-line flag), the
value you saved if that differs, and whether the change applies live or after a daemon restart. A saved value that an
environment variable or flag overrides says so on the row, with the variable's name. After a restart the daemon uses what the
panel saved, and the row confirms it. The outcome gate is a Playwright journey in the Expecto suite (own daemon, fresh
`SAGEFS_DATA_DIR`, `--integration-browser` tier) over this matrix: each first-batch setting x {default, saved global, saved repo,
environment override, flag override where a flag exists} x {before restart, after restart}. Every cell asserts the visible
value, the source badge and the in-use value. No cell is skipped.

## What exists today

Checked by opening the files; the roadmap text was not taken on trust.

- The core is `SageFs.Core/Settings.fs`: `ConfigLayer = LDefault | LGlobal | LRepo | LSession`, `ConfigError`, closed value
  types (`Port`, `EnumValue`, `Toggle`, `DirectoryPath`, `ValidTimeout`, `LoopbackHost`), `SettingDescriptor`
  (Key, Name, Description, Category, Scope, Applicability, Default, Parse, Render, Apply) and the pure
  `SettingsResolver.resolve`.
- `SageFs.Core/SettingsStore.fs` persists a flat string map at `<SageFsDir>/settings.json` (global) and
  `<repo>/.SageFs/settings.json` (repo), atomic by tmp plus move. `SettingsCatalog.resolve/edit/clear` glue them to a descriptor.
- The catalog has five descriptors in `SettingsCatalog.pilots` (default working directory, per-test timeout, test-run timeout,
  MCP port, bind host) and two in `SageFs.Core/SessionAgent.fs` (`reflectionReadModeSetting`, `tieredCompilationSetting`).
  `Dashboard.fs` `settingsRows` and `descriptorForSignal` concatenate the same seven.
- `SageFs/SettingsPanel.fs` renders the page and the edit controls with `Ds.signal`, `Ds.bind`, `Ds.post`. Routes are
  `GET /dashboard/settings`, `POST /dashboard/settings/edit/{sig}`, `POST /dashboard/settings/clear/{sig}`
  (`Dashboard.fs`, near line 3488). It is a standalone page, reached by the gear link in `renderMainContent`
  (`DashboardFragments.fs` line 2311), not a pane in the one-morph shell.
- Only some saved values are ever read back. The MCP port is (`SettingsCatalog.mcpPortFromLayers`, used by
  `DaemonPort.configured`, order in `SageFsConfig.chooseMcpPort`: flag, env, saved, default). Reflection mode and tiering are read at
  session start (`SessionAgent.reflectionSettingsFor`). The default directory pre-fills the New Session form
  (`Dashboard.resolveDefaultWorkingDir`).
- `docs/configuration.md` lists 21 non-duration variables and 65 duration variables. Durations are `let` values in
  `SageFs.Core/Timeouts.fs`, frozen at module initialisation by the private `envOrDefault*` helpers. `Timeouts.fs` compiles at
  position 27 of `SageFs.Core.fsproj`, `Settings.fs` at 67, so `Timeouts` cannot call the settings store.
- Tests: `SageFs.Tests/SettingsTests.fs` (core, store, catalog, port-read-back), `SettingsPanelTests.fs` (string asserts on the
  render). I found no browser test of the panel.

Defects I found while reading, each a reason the panel can say something untrue today:

1. Saved live timeouts are lost on restart. `SettingsCatalog.edit` calls `descriptor.Apply` for `Live` rows. I found no other
   caller of `Apply`, so `Timeouts.setPerTestTimeout` is never run at daemon start from the file. The panel keeps showing the
   saved number while the daemon uses the default.
2. The bind host row reads the file layers only. The daemon takes the bind host from `SAGEFS_BIND_HOST` alone
   (`SageFsConfig.BindHost`, used in `Program.fs` `runDaemon`). With the variable set to `::1` the row says `localhost`, source
   `default`. The row is `Guarded`, so it has no control; `docs/progress.md` says read-only on purpose. The roadmap sentence that
   lists bind host among the edited settings overstates it.
3. The MCP port row ignores `SAGEFS_MCP_PORT` and `--mcp-port`, which outrank the saved value, so it can show a port the daemon
   is not on.
4. "This repo" means the daemon's current directory. `settingsPaths ()` in `Dashboard.fs` uses `Environment.CurrentDirectory`,
   while reflection settings use the session's working directory. One layer file, two meanings.
5. `SettingsStore.setKey` reads the layer, adds the key and rewrites the file with no lock. Two tabs saving different keys at the
   same moment can drop one. I have not reproduced it. The simulation below settles it.

## Design

Rank of what to move first. Evidence is how often the repo's own docs send a person to the variable
(`docs/TROUBLESHOOTING.md` mention counts) and whether the doc names it as the first thing to reach for.

| Rank | Setting (variable) | Why this place | Type | Applies |
|---|---|---|---|---|
| 1 | `SAGEFS_WARMUP_INACTIVITY_SECONDS` (5 mentions), `SAGEFS_WARMUP_MAX_MINUTES` (3), `SAGEFS_BUILD_TIMEOUT_MINUTES` (2) | `configuration.md` says these are the ones to raise on a big repo or slow CI | `ValidTimeout`-like positive span | restart |
| 2 | `SAGEFS_MACHINE_TIER` (5) | Forces every machine-scaled wait | DU `AutoDetect \| Forced of MachineTier` | restart |
| 3 | `SAGEFS_PER_TEST_TIMEOUT_SECONDS` (2, already in catalog) | Fix defect 1 first | existing | live |
| 4 | `SAGEFS_ABANDONED_SESSION_HOURS` | `0` is a real answer, a good test of the type | DU `ReaperOff \| After of hours` | restart |
| 5 | `SAGEFS_UPDATE_CHECK_DISABLED`, `SAGEFS_DEVRELOAD`, `SAGEFS_METADATA_DELTA` | on/off switches docs repeat in three pages | `Toggle` | restart (DevReload and delta are read per worker start) |
| 6 | `SAGEFS_EDITOR` | link scheme for file:line links | `EnumValue` | live for new pages |
| 7 | Local data retention (`SAGEFS_FRICTION_MAX_*`, `SAGEFS_COHORT_LEDGER_RETENTION_DAYS`, `SAGEFS_HOST_CACHE_MAX_AGE_DAYS`, `SAGEFS_WORKER_LOG_MAX_AGE_DAYS`) | rarely changed; sit beside `manage_local_data` | positive int | restart |
| 8 | Launch flags `--no-resume`, `--no-watch`, `--prune` | shown as read-only "launch flag" rows first | `Toggle` | read-only |

Shown read-only with their source, never editable here: `SAGEFS_BIND_HOST` and `SAGEFS_IDENTITY_POLICY` (security, fail closed;
changing them from a page the daemon serves is the wrong trust direction), `SAGEFS_DATA_DIR` (the settings file lives inside
it), `SAGEFS_MEMBER_TOKEN` (a secret, never rendered), every "Set by SageFs" variable, every `SAGEFS_TEST_*`.

Types, added to `Settings.fs` (the compiler then lists every match that must be updated):

```fsharp
type ConfigLayer = LDefault | LGlobal | LRepo | LSession | LEnvironment | LCommandLine   // rank 0..5

type ExternalSource =
  | NoExternalSource
  | FromEnvironment of variable: string
  | FromFlag of flag: string
  | FromEnvironmentOrFlag of variable: string * flag: string

type EnvRead = EnvUnset | EnvSet of raw: string          // injected, so tests never touch the process environment

type InUse =                                             // what the live subsystem uses right now
  | InUseSameAsSaved
  | InUseDiffers of inUse: SettingValue                  // row shows "saved X, in use Y, restart to apply"
```

`SettingDescriptor` gains `External: ExternalSource` and `Current: unit -> SettingValue`. `Current` reads the real consumer
(`Timeouts.warmupInactivityLimit`, the bound MCP port, the tier in force), never the file, so the in-use column is evidence and
not an echo of what was saved. `ConfigPaths` gains `Environment: string -> EnvRead` and `Flags: Map<string, string>`.
`SettingsCatalog.resolve` folds the two new layers into `SettingsResolver.resolve`; precedence is flag, environment, session,
repo, global, default, the order `chooseMcpPort` already uses.

Editing a setting that a higher layer overrides is allowed and says so on the row ("saved 900; ignored while
`SAGEFS_WARMUP_MAX_MINUTES=15` is set"). The write is not refused, because the person may unset the variable next.

Start-up wiring, two options, decided by the first measurement:

- A. `SettingsBootstrap.importSaved` runs first in `Program.fs main`, before anything touches `Timeouts`, and sets each saved
  duration's variable in the daemon's own environment when it is unset, recording the names it set. The resolver treats a variable
  recorded as imported is read as the global layer and never as `LEnvironment`. Workers inherit the environment, so they agree with the daemon.
- B. `Timeouts` gets a `layered` lookup the daemon installs once. Same ordering risk as A, no environment mutation.

Option A shipped first and B replaced it on 2026-11-08 (next section): A needed a module-global set of the names it had written, a
test for "is this variable a person's" and a strip of those names from every daemon a process starts.

`SettingsCatalog.applyAtStart` runs `Apply` for every `Live` descriptor with its resolved value at start. This is the fix for
defect 1.

Scope: the first batch is `Global` only. A repo-level warmup limit needs the limit passed per session at create time instead of
read from a daemon-wide static, which is a read-site change in `SessionManager`; it is a separate, later step.

The repo layer: `ConfigPaths.Repo` is resolved from the viewed session's working directory (the dashboard already tracks it as
`snap.WorkingDir`), with `NoRepoCheckout` when there is none. The edit-target selector then names the directory it will write.

Files: `Settings.fs`, `SettingsCatalog.fs`, `SettingsStore.fs` (a lock plus compare-and-write), `SessionAgent.fs` (descriptors move
out of the session module into the catalog), `Dashboard.fs`, `SettingsPanel.fs`, a new `SageFs/SettingsBootstrap.fs`,
`docs/configuration.md` (a generated "Editable in Settings" column via `scripts/gen-configuration-doc.fsx`).

Wire and clients: none. The settings routes are page-internal, not in `EndpointContracts.fs`, so `apiVersion` stays 4 and
`sagefs.nvim` and `sagefs-vscode` need nothing. No new MCP tool, so no catalog token cost. A later read endpoint for editors is
possible and is not part of this plan.

Persistence: `settings.json` stays a flat string map. New keys are additive. A key the build does not know is kept.

## Tests

RED first, in this order. Each fails today.

1. `SettingsStartupTests`: "a saved per-test timeout is the per-test timeout after a restart". Write
   `{"livetest.perTestTimeoutSeconds":"42"}` to a temp global dir, call `SettingsCatalog.applyAtStart`, assert
   `Timeouts.perTestDefault()` is 42 s. Compile-red (the function does not exist). Sequenced list that restores the default.
2. `SettingsResolveExternalTests`: with `Environment = fun "SAGEFS_MCP_PORT" -> EnvSet "5000"` and a saved 6000, the resolution
   is `LEnvironment`, effective 5000, `PerLayer` lists both. Property: the winner is always the highest-ranked present layer
   (extends the existing `SettingsResolver` property to six layers).
3. `SettingsBindHostTruthTests`: with `SAGEFS_BIND_HOST=::1` injected, the bind host row's effective value is `::1`, source
   environment, and the row has no edit control.
4. `SettingsRepoLayerTests`: the repo layer path is `<session working dir>/.SageFs/settings.json`, and with no checkout the
   repo target is refused with `LayerUnavailable`.
5. Property: `Parse (Render v) = Ok v` for every first-batch descriptor over generated legal values; and no string outside the
   legal set parses (extends `Port`/`EnumValue` properties).
6. Ratchet: every `SAGEFS_*` name in `Timeouts.fs` and `SageFsConfig.fs` is either a descriptor's `External` or in an explicit
   `NotEditable of reason` table, in the style of `EnvVarDocTests`. The table is a budget that only goes down.

Simulation (`SageFs.Simulation/SettingsStoreSim.fs` + `SettingsStoreSimInvariants.fs`, tests in
`SettingsStoreSimDstTests.fs`, modelled on `RetentionSim.fs`). Pool: three keys, two tabs, one repo layer. Ops: `Edit(tab, key,
value)`, `Clear(tab, key)`, `CrashDuringWrite`, `CorruptFile`, `Restart`. Faults from `FaultInjection.fs`: reorder, duplicate,
drop. Invariants: (a) after quiescence the file holds the last edit of each key (no lost update); (b) a crash leaves the old or
new file, never a partial one; (c) a corrupt layer resolves to an error row naming the file, never to a wrong value; (d) after
`Restart` plus `applyAtStart`, every `Live` descriptor's `Current` equals its resolved value. Twin: the current
read-modify-write without a lock, which invariant (a) must catch. No wall-clock waits: the schedule is data and the clock is
injected.

Outcome gate: `DashboardSettingsBrowserTests.fs`, `Integration.register (Integration.Dedicated "--integration-browser")`, own
daemon on reserved ports with `--no-resume` (copy the `startDaemon` shape from `HygieneBrowserTests.fs`). Matrix above. It waits on
`PlaywrightExpect.waitForSSE` and `waitForSelectorText`, never a sleep; the restart step waits for the new daemon's ready state.
Also checks at 375 px that rows wrap and nothing scrolls sideways (the `layoutProblems` helper in
`DashboardLiveBindingsDockBrowserTests.fs`) and zero console and `PatchElementsNoTargetsFound` errors.

## Steps

1. RED tests 1 and 2, then `ExternalSource`, `EnvRead`, the two new layers and `applyAtStart`. Green on the core tests.
   Owns `Settings.fs`, `SettingsCatalog.fs`, `SettingsTests.fs`.
2. Defects 3 and 4: resolve with the injected environment and flags, repo layer from the session directory. Owns
   `Dashboard.fs` (`settingsPaths`, `settingsRows`), `SettingsPanel.fs`, `SettingsPanelTests.fs`.
3. `Current` on every descriptor, the "in use" and "overridden by" row text. Owns `SettingsPanel.fs`.
4. Store lock and compare-and-write, with the simulation and its twin. Parallel with 2 and 3: owns `SettingsStore.fs`,
   `SettingsStoreSim*.fs`.
5. Measure first #1, then `SettingsBootstrap.fs` for rank 1 and 2 (warmup, build, tier). Owns `Program.fs`, `SettingsBootstrap.fs`.
6. Rank 4 and 5 descriptors (reaper DU, toggles). One commit per descriptor group.
7. The browser journey, written against ranks 1 to 3 first, extended as each rank lands.
8. `docs/configuration.md` column and the ratchet table. Commit trailer for the wire surface is not needed: no wire file changes.

Steps 2 to 4 are file-disjoint and can run in parallel after step 1.

## Measure first

- When does `Timeouts` initialise inside the `sagefs` exe? Experiment: a test child process sets a variable inside a function
  that runs first in `main`, then reads `Timeouts.warmupInactivityLimit`. If it sees the value, option A works. If the first read
  happened earlier (for example from a static in another file), the variable is ignored and option B, or moving the import
  earlier, is needed. This decides step 5.
- Do workers read `Timeouts` values the daemon sets, or recompute them? Read `SageFs.Host` for `Timeouts.` uses; if the worker
  reads `warmupInactivityLimit` itself it needs the environment (option A) and not an in-memory hook (option B).
- How long does `settings.json` take to resolve for 30 rows? Each row calls `readLayer` twice, so 60 file reads per page render.
  Expect microseconds; measure once, cache per render if not.

## Risks and dependencies

- Importing saved values into the environment (option A) changes what `sagefs check` and the daemon log say about a variable the
  person never set. The recorded-names rule above is the guard; a test must cover it.
- Persisted timeouts that were saved before this change start applying after it. The panel already promised that, so this is
  a fix, and the release note should say it.
- Depends on nothing else. `command-palette-and-one-timeline` can list "Open settings" and "Edit setting X" once rows have stable
  keys. `dashboard-follows-context` shares the "say why" vocabulary: an overridden row is the same idea as a hidden panel.

## Size

M, about two weeks for steps 1 to 8 on ranks 1 to 5, because the core types are in place and the work is wiring, one browser
journey and one simulation. Ranks 6 to 8 are S each afterwards.

## Decisions for Will

- Option A (import saved values into the process environment) or option B (a lookup seam in `Timeouts`)? Recommend A for
  rank 1 and 2 after the first measurement, because workers then agree without more plumbing.
- Should an edit be refused or accepted when an environment variable overrides it? Recommend accepted, with the row saying it is
  ignored until the variable is unset.
- Keep bind host and identity policy read-only? Recommend yes, and correct the roadmap sentence.

## Measure first: results (2026-11-07)

- When does `Timeouts` initialise in the `sagefs` exe? It does not initialise before `main`: a library file's values run the first time something in that file is used, and a top-level `do` in the last file (`Program.fs`) runs at entry, ahead of `main`. `Program.fs` already relied on this for the machine tier (its own comment records the earlier attempt inside `main`, which was too late). Option A works as long as the import touches nothing in `Timeouts.fs`, `SageFsConfig.fs` or `SettingsCatalog.fs` (the last one calls `ValidTimeout.create`, which loads `Timeouts.fs`). So the settings that are saved as environment values live in their own file, `EnvBackedSettings.fs`, which compiles before the catalog and only builds closures that read `Timeouts` when a row is drawn, and `SettingsBootstrap.fs` uses only that file and `SettingsStore`. The browser journey checks it end to end: a saved warm-up limit of 90 is the value in use after a restart (the row has no "In use now" line).
- Do workers read the values the daemon sets? Workers read their own `Timeouts` from the environment they inherit (`ProcessEnvironment.poisonedVariables` strips MSBuild variables only), so option A reaches them and an in-memory hook (option B) would not.
- How long does the page take to resolve its rows? 0.515 ms for 17 rows, each resolved against both files, the environment and the flags, with the in-use value read (mean of 200 runs in the REPL). No caching.

## Surprises

- `SettingsCatalog.resolve` treated an unreadable layer file as an empty layer, so a corrupt `settings.json` made every row read "default" with a line on stderr. It now returns an error that names the file, which the row shows. A save over a corrupt file still repairs it (a test held that contract), and the old bytes are kept beside it as `settings.json.corrupt`.
- A pasted `Infinity` or `1e999` for a timeout made `TimeSpan.FromSeconds` throw inside the page's edit handler. The parsers now bound the number first.
- `SAGEFS_DEVRELOAD` has three readers that disagree (`SageFsConfig.DevReloadEnabled` defaults to off, the worker's injector treats everything but `0` and `false` as on, the MCP tool reads it again). A row would have to pick one of them, so it has none until the readers share a rule. `SAGEFS_EDITOR` is free text that falls back to `$EDITOR`, so a closed set of choices would be untrue; it has no row either.
- The bind host row read only the layer files, so with `SAGEFS_BIND_HOST=::1` it showed `localhost` from `default`. It is now read-only, takes its value from the variable, and a bind host key in a layer file is never read. The roadmap sentence that listed bind host among the edited settings was wrong and is corrected below.
- The Settings page chose "this repo" from the daemon's own working directory. It now takes the directory of the session the gear link was clicked from (`?session=`), says the directory in the target selector, and offers no repo target when there is none.
- Tests for `Timeouts`-backed rows depend on what the test process started with, so they compare against `Timeouts.buildCompletion` instead of a literal.

## Browser journey (2026-11-07)

`SageFs.Tests/DashboardSettingsBrowserTests.fs`, registered as `--integration-browser` and added to the list in `DashboardBrowserRunner.fs`. It starts three daemons of its own on one throwaway `SAGEFS_DATA_DIR` (the second and third are restarts), drives the page in headless Chromium and asserts the value shown and the source badge in each cell:

| Row | default | saved from the page, before restart | after restart | variable overrides | flag | read-only |
|:--|:-:|:-:|:-:|:-:|:-:|:-:|
| Warm-up inactivity limit | yes | yes, with "In use now" and "applies after a restart" | yes, no "In use now" | yes, "Saved 90. Ignored while SAGEFS_WARMUP_INACTIVITY_SECONDS is set." | none | |
| Abandoned agent sessions (0 is off) | yes | yes | yes | | none | |
| Metadata-delta hot reload | yes | saved | yes | | none | |
| Build timeout | | saved | yes, no "In use now" | yes, "Saved 25. Ignored while SAGEFS_BUILD_TIMEOUT_MINUTES is set." | none | |
| Stop asking NuGet for updates | | saved | yes, no "In use now" | | none | |
| Machine tier (typed as `Fast`, shown as `fast`) | | saved | yes, no "In use now" | | none | |
| MCP port | | | | | yes, source "flag" | |
| Bind host | yes | | | yes, source "environment" | none | yes, no control |

Also at 375 px: the page does not scroll sideways, and zero console or page errors over the run. The run on this branch passed (one case, filtered with `--filter-test-case "Settings browser:"`, so it is a `NarrowedRun`, not the acceptance signal).

The second pass added the repo-layer cells (a session in a working directory, the edit target naming it, a per-test timeout saved for the repo winning over the global one) and the per-test timeout restart cell. Not in the journey, and so gated only by unit tests and the simulation: the other rows added in the second pass (they share a builder with the rows above, and a property test parses and re-renders every one), and an environment variable overriding a flag.

## Rank 5 to 8 and the rest (2026-11-07, second pass)

- A day-sized duration: `RetentionAge` (more than zero, at most ten years, written in days) next to `WaitLimit` (a second to a day). `WholeCount` carries its own least (1 for a cap, 0 for the watchdog's restart count), and `OptionalCount` is a number or "left to SageFs" for the compiler's memory ceiling. Bounds are `[<Literal>]`s in `Timeouts.fs`, so reading one runs no static initialiser.
- `EnvBackedRows.fs` holds every other variable `Timeouts.fs` and `SageFsConfig.fs` read: 44 waits, 6 ages, 8 counts, the memory ceiling, browser dev-reload and the editor, each `restart` with its own help line, and the scaled waits say they are scaled. Read-only rows say why in a closed set of reasons: the five `SAGEFS_TEST_*` deadlines (test harness), the superseded worker start-up timeout, the watchdog flag and restart count (set by SageFs), the bind host and identity policy (security boundary), the data directory, `--no-resume`, and the two tokens (a secret: the row says set or not set, and the resolver has no external source for it, so a refused parse could never quote it).
- One reader for each variable that several places read. `SAGEFS_DEVRELOAD` had three readers: the unused `SageFsConfig.DevReloadEnabled` (default off), `DevReloadInjector` and `enable_hot_reload` (both: only `0` and `false`, exact case, disable). `DevReloadMode` (`DevReloadMode.fs`) is the one rule, case-insensitive, and the injector, the tool and the row use it. `SAGEFS_EDITOR` and `EDITOR` were read in the middleware; `EditorChoice` (`EditorLink.fs`) is the one function, and the row judges text the way the links do. A test fails if another file reads either variable from the environment.
- `DevReloadMode.fs` and `EditorLink.fs` are BCL only and are also compiled by the isolated FSI host (`FsiHost.fsproj`, the embedded list in `SageFs.Core.fsproj`, and `FsiHostBuild.hostSourceNames`). The first build of this change failed in a REPL session's host build ("SageFsConfig is not defined") because the host compiles `DevReloadInjector.fs` and has no `SageFsConfig`.
- `NotEditableBudget` is gone. `variables read without a row` is a table in `SettingsExternalTests.fs`, registered with `Ratchet.table`, so `--ratchets --tighten` can rewrite it, and it is 0.
- The journey now has a session in a working directory: the edit target names it, a per-test timeout saved for the repo wins over the global one and lands in that directory's `.SageFs/settings.json`, and a per-test timeout saved from the page is in force after a restart.

## The settings snapshot and the short list (2026-11-08)

Roast 2026-11-08, item 10: the daemon wrote saved settings into its own environment (`SettingsBootstrap.importSaved`), kept a
module-global set of the names it wrote, and removed them again from every daemon it started, because `Timeouts` freezes each value
on first read and the worker took the environment as its settings. Three mechanisms (`importedNames`, `isPersonsVariable`,
`withoutImported`) existed only to undo the first one. This is option B above.

### What replaced it

- `SettingsSnapshot` (`SageFs.Core/SettingsSnapshot.fs`, the first file Core compiles) is a value: a map of variable name to text, with
  a private constructor so every name and text survives the `NAME=text` line format. It holds the saved values that no person's
  variable overrides, plus the machine tier the daemon worked out.
- `SettingsInForce.read` is the one reader. It answers from the snapshot first and the environment second. Every consumer that
  used to call `Environment.GetEnvironmentVariable` for a row's variable (`Timeouts`, `SageFsConfig`, `MachineTier`, `DevReloadMode`,
  `EditorLink`, `MetadataDeltaFlag`, `UpdateCheckService`) calls it. The snapshot is established once; a second, different one is
  reported as `Establishment.Conflicting` and the first stays.
- `SettingsBootstrap.resolveSaved` folds the saved file with the environment (a person's variable still wins) into a snapshot and
  writes nothing to the environment. `Program.fs` establishes it in its top-level `do`, before `Timeouts` is read, together with the
  machine tier (`MachineStartup.establish` no longer writes `SAGEFS_MACHINE_TIER`).
- Workers get the snapshot as an argument. The FSI host's command line is `<sessionId> <httpPort> [<snapshot>]`
  (`WorkerCommandLine`), and the worker passes the same snapshot on to the isolated FSI host as `--settings <snapshot>`
  (`FsiHostCommandLine`). Both are base 64 text, so there is no quoting. A process started with no snapshot reads the environment only.
  `SettingsSnapshot.fs` is compiled into the FSI host too (it is BCL only), so `FsiHost.fsproj`, the embedded list in
  `SageFs.Core.fsproj` and `FsiHostBuild.hostSourceNames` list it.
- The daemon's environment is the person's. A daemon that the watchdog or the stdio bridge starts inherits it and resolves its own
  snapshot from the same files, so there is nothing to strip. `isPersonsVariable` is gone because every variable in the environment is one.
- A test fails if a product file other than `AspireSetup.fs` and `AppRunner.fs` (both set variables for another program's code) calls
  `SetEnvironmentVariable`.

### Verified

- `SettingsSnapshotTests` (17 cases): the snapshot round trip, the refusals, the read order, the worker command line, the resolution
  outcomes, a resolution that leaves every `SAGEFS_` variable as it was, and the environment-write ratchet. RED commit f75fcf41.
- The FSI host project compiles with `SettingsSnapshot.fs` in it (built from a copy of its sources, with the Harmony stub from another
  host cache).
