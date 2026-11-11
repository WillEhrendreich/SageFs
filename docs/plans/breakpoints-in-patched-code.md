# Breakpoints in patched code

Roadmap item: `breakpoints-in-patched-code` (Exploring, hot reload). Plan written 2026-10-26 against master 59a8692c. Work order added 2026-11-11 against master `8975e898`; every `path:line` below was read on that tree.

## Work order

### 1. Outcome

Piece A, which ships first and alone: after a hot reload save with a debugger attached, the reload report says whether a breakpoint in the new body will bind. Today it says nothing, and on the detour route the honest answer is no (the patch lives in a Reflection.Emit assembly with no PDB). Piece C: a save that lands while the debugger has the process stopped is held, reported as held with the stop location, and applied in order on continue; today the eval cannot run and the save is reported as whatever the timeout produces. Piece B, only if experiment E3 says a debugger can bind in an in-memory PDB: the new body is bindable. Outcome gate for A: `PatchSymbolsTests` on the wire plus a `HotReloadDebuggerTests` assertion that each of its four rows reports `NoSymbolsInPatch`. Gate for C: `DebugSaveSim` invariants plus a host-tier row that saves while stopped. Gate for B: `DebuggerBreakpointJourneyTests`, netcoredbg, the four rows times (breakpoint before save, after save) on net10 and net11, every cell stopping at the new line. Linux x64 only: the harness downloads `netcoredbg-linux-amd64` (`SageFs.Tests/HotReloadDebuggerTests.fs:44-59`) and refuses other platforms by name.

### 2. Where the code is today

- The attached-debugger journey: `SageFs.Tests/HotReloadDebuggerTests.fs:43-45` (netcoredbg 3.2.0-1092 pinned), `:50` (`rowNames`), `:86` (`fsiHostPid`), `:112` (`--interpreter=cli --attach`), `:139` (`Debugger.IsAttached` asked through `WorkerMessage.EvalCode`), `:151` (`waitUntilAttached`, still a `Task.Delay` poll), `:268` (`Integration.hostList`). It sets no breakpoint.
- **A harness that sets breakpoints already exists** (the old plan said none did): `SageFs.Tests/DebugAttachJourneyTests.fs:95` (`DebuggerOutput`), `:325` (`send debugger "break <file>:<line>"`), `:329` (waits for the bind line), `:339` (`Output.Await ... "stopped, reason: breakpoint"`). It drives netcoredbg against a held TEST in the FSI host, not against a hot-reloaded method. Reuse `DebuggerOutput` and `Await` for E1 to E4; do not write a new poll.
- What SageFs already says about symbols: `SageFs.Core/HostAgent.fs:313-318` (`SymbolSupport = CompiledWithSymbols | DefinedByEval`), pinned by `SageFs.Tests/TestDebugTests.fs`. Nothing on the reload report carries it.
- The delta route refuses under a debugger: `SageFs.Core/Features/MetadataDelta/DeltaApply.fs:51` and `:76` (`DebuggerAttached` in `CapabilityGap` and `ApplyOutcome`), `:134` (the message), `:159`, `:269`. Empty PDB delta at `:272`.
- FSI arguments: `SageFs.Core/ProjectLoading.fs:1520-1556` pass `--multiemit-` (`:1542`) and `--optimize-` (`:1550`) and no `--debug` flag of any kind, so E3's expectation is that the host emits no PDB for evaluated code today. The single-assembly constraint is `SageFs.Core/WorkflowTypes.fs:5`.
- Detours: `SageFs.Core/Middleware/HotReloadCore.fs:310` (`detourMethodObserved`, with canary), `:1689` (`handleNewAsmFromRepl`, the pairing Piece B would break). Tiering off: `SageFs.Core/Middleware/ValueReadTracking.fs:1644` (`processEnvironment`).
- Wire: `mechanism` read at `SageFs.Core/SessionReload.fs:177`, written `:212`; `PatchMechanism.wireName` `SageFs.Core/Features/ReloadOutcome.fs:256`. `apiVersion = 4`, `SageFs.Core/EndpointContracts.fs:38`.
- Pure state-machine pattern to copy: `SageFs.Core/DebugTestRequest.fs` and `HostAgent.TestDebug.step`.
- Budgets: `SageFs.Host/WorkerMain.fs` is at its `RatchetBudgets.fileSize` budget (2503 lines) and so is `HotReloadCore.fs` (1963). New logic goes in new files.
- The `--integration-lt` tier is the list at `SageFs.Tests/LiveTestingShards.fs:28-36`; a journey joins it by being added to that list, so "registered by reference" there means editing the list.
- `docs/hot-reload.md:228-235` is the debugger paragraph to rewrite from results.
- `[not verified]`: whether netcoredbg can read an in-memory portable PDB (E3), whether it implements edit-and-continue (E5), what VS Code's C# extension uses on this machine.

### 3. Steps

1. **RED test 1, Piece A.** New `SageFs.Tests/PatchSymbolsTests.fs` (module `SageFs.Tests.PatchSymbolsTests`, `<Compile Include=.../>` in `SageFs.Tests/SageFs.Tests.fsproj` before `Program.fs`): the reload payload for a detour save carries `symbols` with the Reflection.Emit cause, and it round-trips through `SageFs.Json`. Compile-red. Then new `SageFs.Core/PatchSymbols.fs` with the `PatchSymbols` DU and one `wireName`; the worker puts it on the payload (one line in `WorkerMain.fs`, the building in the new file), the daemon reads it in `SessionReload.fs` beside `mechanism` (`:177`, `:212`). Additive field, `apiVersion` stays 4, trailer `Plugin: n/a, additive field; the plugin may show it later`. Pure plus two edge lines.
2. **Assertion on the existing rows.** `HotReloadDebuggerTests.fs:268` list: each row's report says `NoSymbolsInPatch`. Edge; host tier.
3. **Experiments E1 to E5** as `SageFs.Tests/DebuggerBreakpointExperiments.fs`, registered with `Integration.hostList` but printing, not asserting, built on `DebugAttachJourneyTests.DebuggerOutput` and the `HotReloadDebuggerTests` attach. Delete the file when the results are in the plan. Parallel with 1 and 2 (file-disjoint). Results into "Measure first" with the date, and `docs/hot-reload.md:228-235`.
4. **RED test 2, Piece C pure.** `SageFs.Tests/DebugSaveTests.fs` and `SageFs.Core/Features/DebugSave.fs` (`DebuggerStand`, `HeldSave`, `step`). `SageFs.Simulation/DebugSaveSim.fs` with the invariants and the twin in Tests; `DebugSaveSimTests.fs`. Pure. Parallel with 1 to 3.
5. **Piece C wire.** `DECISION:` 2 first. `POST /api/sessions/{id}/debugger-stand` in `SageFs.Core/EndpointContracts.fs`; the stand on `get_session_status` (`SageFs/McpTools.fs`) as one field; the fold per session in a new daemon file; the held-save gate in the worker's save path (new file, one call from `WorkerMain.fs`). Wire change: plugin first (`sagefs.nvim` nvim-dap `event_stopped`/`event_continued` listeners), then `sagefs-vscode` (a debug adapter tracker), `Plugin: done ...` on the commit. After 1 and 4.
6. **Piece B or B2**, by E2, E3 and E5. RED test 3 (`DebuggerBreakpointJourneyTests.fs`, host tier). B compiles the patch with `--multiemit+ --debug+` only while a debugger is attached, behind one setting, with `HotReloadParityTests` run with the setting forced on. Do not start before step 3 reports.
7. **Docs** from the measured results, dated.

### 4. How to check it

- Inner loop: `dotnet build SageFs.Tests -c Release`; `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --filter-test-list "PatchSymbols" --summary`, `--filter-test-list "DebugSave"`; host tier `--integration-host --summary --filter-test-list "managed debugger"` (read `ran=`).
- `--ratchets` after each edit (file sizes, timeout literals, skip sites).
- Acceptance: unfiltered `--summary`, `--integration-host --summary` (`SAGEFS_HOST_SHARDS=5`); for the stopped-save row, `--integration-lt --summary` if it joins `LiveTestingShards.tree`. Needs netcoredbg on this machine; `SAGEFS_REQUIRE_DEBUGGER=1` makes a machine without one fail instead of pass.

### 5. Traps in this repo

- `WorkerMain.fs` and `HotReloadCore.fs` are at their line budgets: new files, one call each.
- No timing: wait on netcoredbg's output lines (`DebuggerOutput.Await`) and on the reload report; bounds from `TestTimeouts` (`debugAttach`, `patience`), never a literal. `waitUntilAttached`'s poll is the pattern not to copy.
- SAGE-005: `StoppedAt of file: string * line: int` is fine; a function taking two strings (file and route) is not.
- `StandUnknown` fails closed: the save proceeds as today and the report says it could not tell.
- Wire change in step 5: plugin first, `Plugin:` trailer, dashboard and both editors in the same commit; `apiVersion` stays 4 (additive).
- A new test file has a `skipSites` budget of zero; a platform refusal is a named failure, not a skip. Module names unique.
- Commit `SageFs.Tests.fsproj` and `SageFs.Core.fsproj` whole, by pathspec. No new NuGet package. Linux-only machine.

### 6. Size and order

Piece A (steps 1, 2): two days. Experiments (step 3): two days, parallel. Piece C (steps 4, 5): a week here plus client work in two repos. Piece B: unsized until E3. First commit: the RED `PatchSymbolsTests.fs` with its fsproj line.

### 7. Not now

- Stepping through a frame that was running the old body when the save landed: a detour cannot move a running frame; only edit-and-continue proper (B2) could, and that waits on `use-the-fsharp-compilers-deltas` S3.
- vsdbg: not licensed outside Microsoft products; netcoredbg is the only debugger tested.
- Fixing `waitUntilAttached`'s poll in `HotReloadDebuggerTests.fs:151`: worth doing, separate commit, not this item's outcome.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | A is S, C is M, B is M to L |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Work order step 1: RED `PatchSymbolsTests.fs`, a detour save reports `NoSymbolsInPatch`. Step 3 (experiments E1 to E5 on the `DebugAttachJourneyTests` harness) in parallel. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

With a debugger attached to the process that runs a hot-reloaded app, a user sets a breakpoint on a line of a function they just saved, makes a request, and the debugger stops in the new body on the new source line with the locals of that frame. A breakpoint set before the save keeps working after it. If SageFs cannot make that true for a given save, the save's report says so before the user sets a breakpoint (as `SymbolSupport` already does for a held test). A save that arrives while the debugger has the process stopped is not lost and is not reported `Patched` until the process has resumed and the new body has run. The outcome gate is a netcoredbg journey over the four shapes `HotReloadDebuggerTests` already attaches for (`inlineLambda`, `instanceState`, `taskNamed`, `signature`), on .NET 10 and .NET 11, across the matrix breakpoint-set-before-save and breakpoint-set-after-save, each cell asserting a stop at the new line. Nothing in it is a skip.

## What exists today

- `SageFs.Tests/HotReloadDebuggerTests.fs` attaches a pinned netcoredbg (3.2.0-1092, SHA-256 in the file) to the FSI host (`fsiHostPid`, `attach`, `waitUntilAttached`), asks the process `Debugger.IsAttached`, then saves the four rows and expects `Patched`. Its header says what it does not do: no breakpoint is set, nothing steps, nothing edits a method the debugger is stopped in. The harness starts netcoredbg with `--interpreter=cli` and writes commands to its stdin (`continue`).
- `docs/decisions.md` ("The debugger row downloads a pinned debugger...") names this item as the reason to reopen the row: a detour rewrites the first bytes of the code a breakpoint may sit in.
- The patch body lives in an FSI assembly that SageFs forces to single-assembly mode (`--multiemit-`, `SageFs.Core/ProjectLoading.fs:1542` on 2026-11-11; `SageFs.Core/WorkflowTypes.fs:5` explains the constraint). The compiler's own doc (`~/Work/fsharp-compiler-services/docs/fsi-emit.md`) says Reflection.Emit on .NET Core cannot write debug symbols for dynamic assemblies, which is why FSI moved to `--multiemit+` (separate in-memory assemblies loaded with `Assembly.Load(bytes, pdbBytes)`, `Interactive/fsi.fs` around line 1976). So a patched body has no PDB on today's route.
- SageFs already says this for tests: `HostAgent.TestDebug.SymbolSupport` has `CompiledWithSymbols | DefinedByEval`, with the comment that evaluated code "lives in a dynamic assembly, which has no PDB: the debugger runs it but breakpoints in it do not bind" (`SageFs.Core/HostAgent.fs:313-318` on 2026-11-11), pinned by `TestDebugTests.fs` ("a test an eval defined is held with DefinedByEval"). Nothing carries that fact on a reload report.
- `SageFs.Core/DebugTestRequest.fs` and the `HostAgent.TestDebug.step` transition (Hold, HoldEvent, HoldEffect) are the house pattern for a pure state machine around an attached debugger; `DebugAttachJourneyTests.fs` (`--integration-lt`) runs a real attach end to end.
- The delta route refuses under a debugger. `DeltaApply.moduleGaps` yields `CapabilityGap.DebuggerAttached` and `DeltaApply.apply` returns `ApplyOutcome.DebuggerAttached`; the save restarts and names it (`docs/hot-reload.md`, "A debugger on the app's process..."). The delta is applied with an empty PDB delta (`DeltaApply.fs` line 272).
- Detours are MonoMod native detours through `HotReloadCore.detourMethodObserved`, with a canary that compares machine code before and after. The host runs with tiering off by default (`ValueReadTracking.processEnvironment`), and SageFs builds with `Optimize=false`, which stamps `DebuggableAttribute` (`SageFs.Core/SessionBuild.fs`, `BuildOptimization.fs`), so JIT output for project code is debuggable.

What I could not find: any test or doc that binds a breakpoint in a hot-reloaded method, and any signal from an editor to the daemon saying the debuggee is stopped. (Update 2026-11-11: `SageFs.Tests/DebugAttachJourneyTests.fs:325-339` does bind a breakpoint with netcoredbg and waits on its `stopped, reason: breakpoint` line, against a test held in the FSI host. That is the harness to reuse; it still binds nothing in patched code.)

## Design

Three separable pieces. Each can land and be useful alone.

Piece A, say what binds (honesty first). A closed set on the reload report:

```fsharp
[<RequireQualifiedAccess>]
type PatchSymbols =
  /// The patched body is in an assembly with a PDB the debugger can read.
  | BindsInPatch
  /// Breakpoints in the new body will not bind; the old body is bypassed. `why` names the cause.
  | NoSymbolsInPatch of why: string
```

The worker puts it on the reload payload next to `mechanism`. One function spells it for the wire. This is true today on the detour route (`NoSymbolsInPatch "the patch is in a Reflection.Emit assembly, which has no PDB"`) and costs one additive field.

Piece B, make the new body bindable. Candidate: when a debugger is attached, compile the patch with `--multiemit+ --debug+` so FSI loads the patch as a real in-memory assembly with portable PDB bytes. This breaks the single-assembly assumption in `HotReloadCore.handleNewAsmFromRepl` (pairing by `FSI_NNNN`-stripped names across assemblies) and the `WorkflowTypes` constraint. Whether it is even needed depends on experiments E2 and E3 below. If the debugger cannot read an in-memory PDB, Piece B is dead and Piece A is the whole deliverable on this route.

Alternative B2: the delta route keeps the edited method in the original file-backed module, whose PDB the debugger already has. It needs the debugger to drive the edit (the runtime refuses `ApplyUpdate` under a debugger), a PDB delta from the producer, and a debugger that implements edit-and-continue. SageFs's emitter writes no PDB delta. The F# compiler work in `use-the-fsharp-compilers-deltas` writes portable PDB deltas, so B2 waits on that item's signal S3 and on E5.

Piece C, a save while the process is stopped. A debugger stop suspends managed threads in the process (to confirm, E4), and FCS compiles inside the FSI host, so the eval cannot run. The save must be held and reported honestly. New pure module `SageFs.Core/Features/DebugSave.fs`:

```fsharp
[<RequireQualifiedAccess>]
type DebuggerStand =
  | NoDebugger
  | Running
  | StoppedAt of file: string * line: int
  | StandUnknown of why: string

[<RequireQualifiedAccess>]
type HeldSave =
  | Nothing
  | Held of files: string list      // saved while stopped, applied in order on resume

val step : DebuggerStand -> HeldSave -> DebugSaveEvent -> (HeldSave * DebugSaveEffect)
```

Events: `SaveArrived of file`, `StandChanged of DebuggerStand`, `SaveFinished of verdict`. Effects: `ApplyNow`, `HoldAndSay`, `DrainHeld`. A save arriving at `StoppedAt` becomes `HoldAndSay`; the report says "held: the debugger has the process stopped at Pages.fs:42; the save applies when you continue". `StandUnknown` is fail-closed: the save proceeds as today and the report says it could not tell. The stand comes from the editor, because the daemon cannot see a DAP `stopped` event any other way: `sagefs-vscode` forwards it from a debug adapter tracker, `sagefs.nvim` from nvim-dap's `event_stopped` and `event_continued` listeners. New route `POST /api/sessions/{id}/debugger-stand` in `SageFs.Core/EndpointContracts.fs`. No new MCP tool: the stand rides on `get_session_status` as one field, so the catalog (already about 28,000 tokens) grows by one short line.

A frame that is already running the old body when a save lands keeps running it to the end; the next call takes the new body. `docs/how-hot-reload-works.md` says the same of a delta ("a method that is running when the update lands keeps its old frames"), untested there too. The report says "frames already inside X finish on the old body" while a stop exists in a function the save patched. Stepping through a patched frame is not promised.

Wire and clients: additive fields on `lastReload` (`symbols`) and a new route, so `apiVersion` does not move. Plugin trailer on every wire commit: `Plugin: done` with what each client sends, once those exist. Persistence: none.

## Tests

RED tests, in order:

1. `PatchSymbolsTests`: "WHY - a detour save on a hot reload session reports NoSymbolsInPatch with the Reflection.Emit cause". Asserts the worker payload for a detour save carries `symbols = none` and the cause text; wire round-trip through `SageFs.Json`. RED: the field does not exist.
2. `DebugSaveTests`: "WHY - a save that arrives while the debugger has the process stopped is held, not applied, and not lost". Asserts `step (StoppedAt ..) Nothing (SaveArrived f)` is `Held [f]` with `HoldAndSay`, and `StandChanged Running` drains it in arrival order.
3. `DebuggerBreakpointJourneyTests` (host tier, `--integration-lt` or a new registered tier, by reference): for each row, attach netcoredbg, set `break <file>:<line>` on the edited line before the save, save, request, assert the stop line and file. Variant with the break set after. Compile-green, behavior-red until Piece B or its negative result is in.

Simulation `DebugSaveSim` in `SageFs.Simulation`: seeded events (saves on several files, stand changes at any point, a save finishing, the editor disconnecting so the stand goes `StandUnknown`). Invariants: no save is ever lost; a held save applies exactly once and after the stand left `StoppedAt`; `Patched` is reported only after the new body ran (reuse `PatchConfirmation`); order of application equals order of arrival. Twin: a step function that applies the save at arrival regardless of the stand (the wrong behaviour); the sim must catch it.

Property test (FsCheck): any sequence of events leaves `HeldSave.Held []` empty after `StandChanged Running` followed by enough `SaveFinished`.

Waiting: the journey waits on the debugger's stopped event line and on the daemon's reload report, never a sleep. `DebugAttachJourneyTests.DebuggerOutput.Await` already reads netcoredbg's output stream for a marker line; use it. `waitUntilAttached` in `HotReloadDebuggerTests.fs:151` still polls with `Task.Delay` and is the pattern not to copy.

## Steps

1. RED test 1 and the `PatchSymbols` field. Files: new `PatchSymbols.fs` in `SageFs.Core`, the worker payload in `SageFs.Host/WorkerMain.fs`, the daemon's `lastReload` in `SageFs.Core/SessionReload.fs`. Green when the field round-trips and the detour save carries `none`.
2. Experiments E1 to E5 below, as one throwaway journey test file `DebuggerBreakpointExperiments.fs` that prints, not asserts. Parallel with step 1 (file-disjoint: only the new test file and its fsproj line). No product change.
3. RED test 2, `DebugSave.fs` pure module, `DebugSaveSim`. File-disjoint from 1 and 2.
4. Route and field: `EndpointContracts.fs`, `McpTools.fs` status field, daemon fold of the stand per session, held-save plumbing in the worker's save pipeline (`WorkerMain.fs`; collides with step 1, so after it).
5. Client senders: `sagefs-vscode` tracker, `sagefs.nvim` listeners. Parallel after 4; separate repos, separate owners. Plugin first, per the wire rule.
6. Piece B or B2, chosen by E2, E3 and E5, with RED test 3. Do not start before step 2 reports.
7. Docs: `docs/hot-reload.md` debugger paragraph and the comparison-table row in `docs/how-hot-reload-works.md` rewritten from the measured results, with dates.

## Measure first

All five run against the pinned netcoredbg through the existing harness, on Linux x64, net10 and net11. They decide which pieces get built.

| Experiment | Do | If yes | If no |
|---|---|---|---|
| E1: breakpoint in the original body, then save | Bind a bp on a compiled method (it has a PDB), save an edit to that method, request it. Record: hit or not, any crash, whether the debugger still lists the bp as bound. | Hit means the old body still runs somewhere, which would contradict the detour; investigate. | Expected: the detour bypasses it. The bp is dead after the save. Then Piece A must tell the user the old bp is bypassed. |
| E2: bp bound after the save on the same source line | After the save, `break <file>:<line>`. Where does the debugger bind it (original module or patch)? | Binds in the original module only: it never hits. Piece A message stands. | Binds in the patch: Piece B is already partly true; re-run E3 for the PDB source. |
| E3: does netcoredbg read an in-memory PDB | In an Interactive-workflow session (`--multiemit+`) attach netcoredbg to the FSI host, eval a function from a file with `#line`, bind a bp on it. I expect a PDB only if FSI is started with `--debug+`; check the host's `FsiArgs` first. | A bind means Piece B is feasible. Next measure the cost of `--multiemit+` on a hot reload save. | No bind means Piece B is dead on netcoredbg; the VS Code C# debugger (vsdbg) is not licensed for use outside Microsoft products, so it is not a substitute. Ship Piece A and C only. |
| E4: stop-the-world | Stop at a bp in a request thread, post a save, watch whether the host evaluates it before `continue`. | The eval completes: the process is not fully stopped; Piece C's premise is wrong and the held save is unnecessary. | The eval waits: Piece C is needed as designed. |
| E5: does netcoredbg implement edit-and-continue | Read its `--help` and strings in the pinned release; try its hot reload request if it has one. I could not verify this from the repository and have not run it. | B2 becomes plannable against netcoredbg. | B2 is limited to debuggers that implement the protocol. |

Numbers I need and do not have: the save-latency cost of `--multiemit+ --debug+` against today's p50 of about 260 ms; the cost of the PDB write per save.

## Risks and dependencies

- Piece B changes the pairing of old and new methods, the most fragile part of `HotReloadCore`. Gate it behind "a debugger is attached" so the normal path is untouched, and run the whole parity matrix (`HotReloadParityTests`) with the setting forced on as a regression.
- A debugger can attach after the app started. The stand and the compile mode can then disagree with the already-loaded patch assemblies; the report must say "attached after the last save; your first save from now on is bindable".
- Depends on `use-the-fsharp-compilers-deltas` (S3) for B2. Shares the `HotReloadDebuggerTests` harness with nothing else in this group.
- Only netcoredbg is tested. VS Code's C# extension ships the same Samsung debugger or vsdbg depending on setup; I have not checked which the extension uses here.

## Size

Piece A: S (days). Piece C with its sim and the wire: M (a week or two), mostly client work in two other repos. Piece B: M to L, and decided by E3; B2: L and blocked.

## Decisions for Will

1. Ship Piece A alone first, even if E3 is negative? Recommend yes: it is the cheapest true thing and it removes the surprise.
2. Is an editor-sent stand signal acceptable as the only source for "the debugger has the process stopped"? Recommend yes, with `StandUnknown` failing closed, because the daemon has no other event to wait on and a heartbeat timeout would be a wall-clock guess.
3. Accept "a frame already running the old body finishes on it"? Recommend yes; a detour cannot move a running frame, so a promise of stepping through a patched frame is not buildable on it. E-and-C proper (B2) is the only route to that.
