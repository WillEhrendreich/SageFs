# The delta route takes the edits the FSI planner refuses for FSI's own reasons

Written 2026-11-11 against master `c80f8d49` plus the hot reload roast fixes of the same day (roast 2.1, finding C1). Status: not
started. Nothing here is built; the routing decision stays as it is until this lands with a hot-reload gate pass.

## Work order

### 1. Outcome

A `run_app` app whose handler reads an unedited `let mutable private hits` keeps `hits` across a save of that handler: the edit
lands by metadata delta in the same process, the save says `Patched` with mechanism `metadataDelta`, and `hits` reads what it read
before. The same holds for a function that calls a private function of its own file. Today both restart the app on the default route
(`SAGEFS_METADATA_DELTA` unset is on) and lose every live value.

The outcome gate is a `RunAppDeltaTests` row per shape, on both runtimes (`HostRuntime.all`), that asks the same pid for the new body
AND the carried counter after the save. Linux x64 only, as every hot reload proof is today (`decisions.md`, "Linux x64 only");
Windows and macOS stay unproven.

### 2. Where the code is today (read on 2026-11-11)

- `SageFs.Core/Features/PatchRoute.fs:28-37` `PatchRoute.choose`: only `ReloadPlan.PatchFunctions (_ :: _)` with
  `AppPlacement.InWorkerProcess` and `MetadataDeltaMode.On` goes to `SaveRoute.ByMetadataDelta`. A `PatchKeepingState` there
  becomes a restart naming each state (`ReloadChange.LiveStateOffDeltaRoute`, `:35-36`). Everything else goes through
  `AppPlacement.adjust`.
- `SageFs.Core/Features/ReloadPlanning.fs:1619` `AppPlacement.adjust` turns a patch plan in the worker into
  `RestartRequired (RunsOutsideAgent name, ...)`, which is now reached only with the route off. Until 2026-11-11 it was also the
  route-on reason for live state, and said the app "runs outside the process SageFs patches", which was false.
- `ReloadPlanning.fs:1307` `planReloadThen`: an unedited non-public `let mutable` the patch uses becomes `LiveState.Carried`
  (`isCarryable` at `:1300`); a non-public function or value the patch uses becomes `ReloadChange.UsesNonPublicMember`, a restart
  (`:1334-1339`); an edited `let mutable` initializer is `LiveState.Kept`; a redefined public value is `LiveState.Redefined`. All three
  `LiveState` cases ride in `PatchKeepingState` (`:1351`). Both refusals exist because the FSI patch is compiled outside the app's
  assembly and cannot name its private members. A metadata delta is the app's own assembly rebuilt, so neither reason applies to it.
- `[<RequireQualifiedAccess>]`: the roast listed it as a third refusal. It is not one on the delta route. `RequiresQualifiedAccess`
  is raised only inside the FSI emit (`SageFs.Host/WorkerMain.fs:~1593`, `emitPatchWithDefinitions` answering
  `PatchRefusal.QualifiedAccess`), and `patchInPlace` is reached in a `run_app` worker only with the route off, because
  `PatchRoute.choose` already sent `PatchFunctions` to the delta. `QualifiedAccessRoute.RestartApp` (`ReloadPlanning.fs:1649`) is
  therefore the route-off path. Nothing to do for it here.
- The emitter already refuses what a delta cannot do with state: a changed static initializer or startup class body is
  `RudeCause.StaticInitializerChanged` (`SageFs.Core/Features/MetadataDelta/MethodDiff.fs:173` `isStaticInitializer`, `:285-287`).
  So `Kept` and `Redefined` sent to the delta should come back as a named restart, the same outcome they have today, because F#
  compiles a module-level initializer into the startup class. `[not verified by a run]`: step 4 starts by checking it with a row.
- `SageFs.Tests/DeltaRouteTests.fs`, the case "an edit the planner sends to a restart stays one ...", pins the restart for
  `PatchKeepingState` on both modes, and the reason on each (`LiveStateOffDeltaRoute` with the route on, `RunsOutsideAgent` with it
  off). That pin is the decision this work changes.
- The real-app rows: `SageFs.Tests/RunAppDeltaTests.fs:285` (`hostListConcurrent`, in `--integration-host`) over
  `SageFs.Tests/fixtures/RunAppDeltaFixture/Handlers.fs`. The fixture has no module-level `let mutable private` today
  (`Handlers.fs:45` and `:83` are locals of a type and a function).
- Latency tiers that time a `run_app` save: `--integration-hr-restart`, `--integration-hr-delta`, `--integration-hr-growth`
  (`ci-pipeline.fsx:2004-2017`), plus the journeys in `--integration-hr` (`ci-pipeline.fsx:2003`).

### 3. Steps

1. RED, edge (fixture plus test): add a module-level `let mutable private hits = 0` and a `/hits` handler that increments and returns
   it to `RunAppDeltaFixture/Handlers.fs`, and a row in `RunAppDeltaTests.rows` named `carried` that hits the route twice, edits the
   handler's text, and expects `Ending.PatchedByDelta` with the counter continuing (`AlsoAfter`). Add a second row `nonPublicCall`: a
   public handler that calls a `let private helper`, editing the handler. Both rows go in the `patching` list. Run
   `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary --filter-test-list "run_app metadata delta"`
   and watch both fail with a restart.
2. RED, domain: in `DeltaRouteTests.fs` replace the `keeping` half of that case with a property over the `LiveState` cases:
   with `InWorkerProcess` and `On`, a `PatchKeepingState` whose state is all `Carried` goes to `SaveRoute.ByMetadataDelta`; with `Off`
   it stays the restart it is. Add the same for a plan the planner refused only for `UsesNonPublicMember`.
3. GREEN, domain: `PatchRoute.choose` routes `PatchKeepingState (functions, state)` to `ByMetadataDelta functions` when every state is
   `LiveState.Carried`. For `UsesNonPublicMember` the planner has to stop deciding a restart before routing: give `planReloadThen` the
   placement and mode, or (simpler, recommended) have the planner keep `UsesNonPublicMember` as data on a patch plan
   (`ReloadPlan.PatchFunctions` plus the unreachable list) and let `PatchRoute.choose` decide: the delta takes it, FSI restarts it.
   That changes `ReloadPlan`'s shape, so do it in its own commit and let the compiler list every match.
4. DECISION (Will): `Kept` and `Redefined` on the delta route.
   - (a) Leave them on the restart they have today. Smallest change.
   - (b) Send them to the delta and let `StaticInitializerChanged` refuse. Same outcome as (a), one less special case in `choose`.
   - (c) Teach the emitter rule 3: drop the `.cctor` / startup-class body change from the delta, patch the readers, and report
     `KeptLiveState` the way the detour does. This is the real parity with the detour route and a separate plan.
   Recommendation: (b) now, (c) as its own plan, because (b) removes the special case and costs nothing a user can see.
5. DECISION (Will): ship behind a setting first, or not.
   - (a) Behind `SAGEFS_DELTA_CARRIES_STATE=on` for one release, default off, flipped after a week of saves on Will's own apps.
   - (b) Default on, with `SAGEFS_METADATA_DELTA=off` as the existing escape hatch.
   Recommendation: (a). The failure this risks is the worst one hot reload has: a save that says `Patched` while the app runs something
   else, or a counter that silently resets. A flag keeps that off every user until the rows and Will's own apps agree. The flag is a
   closed DU parsed next to `MetadataDeltaMode` (`MetadataDeltaMode.parse` is the pattern), never a bool.
6. Re-pin that `DeltaRouteTests.fs` case to the new decision, and update the "Every refusal is a named restart" and run_app sections of
   `docs/how-hot-reload-works.md` and `docs/decisions.md` to say which edits the delta now takes.

### 4. How to check it

- REPL first: a session on `SageFs.Tests/SageFs.Tests.fsproj`, `send_fsharp_code` the new `PatchRoute.choose` over every
  `LiveState` combination before writing it.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Inner loop: `--filter-test-list "delta route"` (default suite) and the `--integration-host` filter in step 1.
- Acceptance, all unfiltered, each `Trusted` in the TRUST table: the default suite (net11 and net10), `--integration-host`,
  `--integration-hr`, `--integration-hr-restart`, `--integration-hr-delta`, `--integration-hr-growth`. The hr tiers matter because a
  save that used to restart now patches, which moves the `hr-delta` and `hr-restart` series; a restart row whose saves now patch fails
  by design (`docs/how-hot-reload-works.md`, the paragraph on rows checking their route) and has to be given an edit that still
  restarts.

### 5. Traps in this repo

- A `run_app` row starts a real host: it calls `SpawnEnvironment.apply` through the harness; a new harness entry point must too.
- No timing-sensitive asserts in the rows: wait on the probe (`settle`), never a sleep.
- `ReloadPlan` and `ReloadChange` are matched in `WorkerMain.fs`, `ReloadPlanning.fs` and the tests: change the DU and let the compiler
  list the sites. Do not add a wildcard arm to make it compile.
- The setting in step 5 must be read where `MetadataDeltaMode` is read for the worker (`Args.buildWorkerSpawnConfigWith`), and the
  variable must not leak into spawned builds (`ProcessEnvironment.poisonedVariables` is the pattern for that).
- SAGE-005: a new `choose` overload with two `string`s or two `bool`s fails the analyzer stage.

### 6. Size and order

About two days for one person: half a day for the fixture rows (step 1), half a day for `choose` and the property (steps 2, 3 for
`Carried`), a day for moving `UsesNonPublicMember` out of the planner's restart list, which touches every `ReloadPlan` match. Step 1
lands first and alone (red rows are fine on master only if they are in a tier the gate runs, so land step 1 and step 3 together if the
gate would go red). The first commit is the fixture's `hits` route and the `carried` row.

### 7. Not now

- Rule 3 for `Kept` on the delta (step 4 option c): its own plan.
- Fields and types the delta adds: `docs/plans/delta-adds-fields-and-types.md`.
- The FSI route's own `UsesNonPublicMember` restart (InternalsVisibleTo, re-emitting members): unrelated to the delta.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Step 1: the `hits` route in `RunAppDeltaFixture/Handlers.fs` and the `carried` row in `RunAppDeltaTests.rows`, run red. |
| Decisions made | none yet (steps 4 and 5 are Will's) |
| Surprises | The `[<RequireQualifiedAccess>]` refusal the roast listed never reaches the delta route; see "Where the code is today". |

## The neighbouring measurement

The roast that found this also proposed moving the child-spawning `metadata delta emitter` cases out of the default tier, to see whether
the `EBADF` flake follows them. That experiment is written up, with steps, in `docs/fd-loss-in-the-default-tier.md` under "Experiment:
move the delta children out of the default tier". It is independent of this plan and can run first.
