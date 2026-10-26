# Breakpoints in patched code

Roadmap item: `breakpoints-in-patched-code` (Exploring, hot reload). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | A is S, C is M, B is M to L |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a detour save reports `NoSymbolsInPatch`. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

With a debugger attached to the process that runs a hot-reloaded app, a user sets a breakpoint on a line of a function they just saved, makes a request, and the debugger stops in the new body on the new source line with the locals of that frame. A breakpoint set before the save keeps working after it. If SageFs cannot make that true for a given save, the save's report says so before the user sets a breakpoint (as `SymbolSupport` already does for a held test). A save that arrives while the debugger has the process stopped is not lost and is not reported `Patched` until the process has resumed and the new body has run. The outcome gate is a netcoredbg journey over the four shapes `HotReloadDebuggerTests` already attaches for (`inlineLambda`, `instanceState`, `taskNamed`, `signature`), on .NET 10 and .NET 11, across the matrix breakpoint-set-before-save and breakpoint-set-after-save, each cell asserting a stop at the new line. Nothing in it is a skip.

## What exists today

- `SageFs.Tests/HotReloadDebuggerTests.fs` attaches a pinned netcoredbg (3.2.0-1092, SHA-256 in the file) to the FSI host (`fsiHostPid`, `attach`, `waitUntilAttached`), asks the process `Debugger.IsAttached`, then saves the four rows and expects `Patched`. Its header says what it does not do: no breakpoint is set, nothing steps, nothing edits a method the debugger is stopped in. The harness starts netcoredbg with `--interpreter=cli` and writes commands to its stdin (`continue`).
- `docs/decisions.md` ("The debugger row downloads a pinned debugger...") names this item as the reason to reopen the row: a detour rewrites the first bytes of the code a breakpoint may sit in.
- The patch body lives in an FSI assembly that SageFs forces to single-assembly mode (`--multiemit-`, `SageFs.Core/ProjectLoading.fs` line 1462; `SageFs.Core/WorkflowTypes.fs` header explains the constraint). The compiler's own doc (`~/Work/fsharp-compiler-services/docs/fsi-emit.md`) says Reflection.Emit on .NET Core cannot write debug symbols for dynamic assemblies, which is why FSI moved to `--multiemit+` (separate in-memory assemblies loaded with `Assembly.Load(bytes, pdbBytes)`, `Interactive/fsi.fs` around line 1976). So a patched body has no PDB on today's route.
- SageFs already says this for tests: `HostAgent.TestDebug.SymbolSupport` has `CompiledWithSymbols | DefinedByEval`, with the comment that evaluated code "lives in a dynamic assembly, which has no PDB: the debugger runs it but breakpoints in it do not bind" (`SageFs.Core/HostAgent.fs` line 300 to 306), pinned by `TestDebugTests.fs` ("a test an eval defined is held with DefinedByEval"). Nothing carries that fact on a reload report.
- `SageFs.Core/DebugTestRequest.fs` and the `HostAgent.TestDebug.step` transition (Hold, HoldEvent, HoldEffect) are the house pattern for a pure state machine around an attached debugger; `DebugAttachJourneyTests.fs` (`--integration-lt`) runs a real attach end to end.
- The delta route refuses under a debugger. `DeltaApply.moduleGaps` yields `CapabilityGap.DebuggerAttached` and `DeltaApply.apply` returns `ApplyOutcome.DebuggerAttached`; the save restarts and names it (`docs/hot-reload.md`, "A debugger on the app's process..."). The delta is applied with an empty PDB delta (`DeltaApply.fs` line 272).
- Detours are MonoMod native detours through `HotReloadCore.detourMethodObserved`, with a canary that compares machine code before and after. The host runs with tiering off by default (`ValueReadTracking.processEnvironment`), and SageFs builds with `Optimize=false`, which stamps `DebuggableAttribute` (`SageFs.Core/SessionBuild.fs`, `BuildOptimization.fs`), so JIT output for project code is debuggable.

What I could not find: any test or doc that binds a breakpoint in a hot-reloaded method, and any signal from an editor to the daemon saying the debuggee is stopped.

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

Waiting: the journey waits on the debugger's stopped event line and on the daemon's reload report, never a sleep. `waitUntilAttached` already polls the process and is the pattern; replace its `Task.Delay TestTimeouts.pollService` loop for new code with a read of netcoredbg's output stream for the `stopped` marker.

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
