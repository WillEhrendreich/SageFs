# Use the F# compiler's own deltas

Roadmap item: `use-the-fsharp-compilers-deltas` (Exploring, hot reload). Plan written 2026-10-26 against master 59a8692c. Work order added 2026-11-11 against master `8975e898`; every `path:line` below was read on that tree, and the upstream states were read with `gh` the same day.

## Work order

### 1. Outcome

Nothing here changes what a user can do until the F# compiler ships its delta emitter in an SDK. What ships now is a tripwire that turns red the day the pinned SDK's `fsc` accepts `--test:HotReloadDeltas`, so the signal cannot be missed, plus a doc line that names the signals. Stage 2, after the signal: a save to a `run_app` app can take its delta from the compiler instead of SageFs's emitter, and the outcome gate is the existing `RunAppDeltaTests` rows (`SageFs.Tests/RunAppDeltaTests.fs:285`, `Integration.hostListConcurrent` per runtime) run once per delta source on net10 and net11, every row ending `Patched` with the new body served or in a restart naming the same cause. Linux x64 is where the host tier runs; the delta route on Windows and macOS is the `hot-reload-on-windows-and-macos` item's business.

### 2. Where the code is today

- Upstream, read 2026-11-11 with `gh pr view`: dotnet/fsharp #19941, #20025, #20027, #20030 and #20031 are all OPEN, last updated 2026-10-30/31; dotnet/sdk#55464 is OPEN. So S1, S2 and S3 below are all false today and stage 2 stays gated. `docs/how-hot-reload-works.md:19` and `:133` already carry the 2026-11-10 note on the `fsharp-watch` preview (`0.2.0-preview.20261027.*`), which needs an SDK that carries the compiler API and so is a fifth signal: a stock SDK it works on.
- Our emitter: `SageFs.Core/Features/MetadataDelta/` (`PeImage.fs`, `IlCanon.fs`, `MethodDiff.fs`, `DeltaWriter.fs`, `DeltaApply.fs`, `DeltaSession.fs`, `RudeCause.fs`, `MetadataDeltaFlag.fs`). `DeltaPayload` is `DeltaWriter.fs:49`, `PrepareOutcome` `:417`, `DeltaChain.PrepareProbing` `:455`, `Commit` `:477`. `MetadataUpdater.ApplyUpdate` with an empty PDB delta: `DeltaApply.fs:272`. `Preparation`, `Refusal` (with `Unavailable of why`), `SaveDecision`: `DeltaSession.fs:28`, `:38`, `:46`. `RudeCause` is `RudeCause.fs:44`.
- The caller: `SageFs.Host/RunAppDelta.fs:79` (`Session.Start`), `:97` (`Save`), `:109` (`SessionBuild.runBuildAsync`), `:125` (`current.PrepareProbing`).
- The baseline is the Cecil-instrumented shadow copy: `CoverageInstrumenter.instrumentShadowSolution` (`SageFs.Core/Features/CoverageInstrumenter.fs:383`) called from `SageFs.Core/ActorCreation.fs:130` and `SageFs.Core/AppState.fs:1567`.
- FSI runs `--multiemit-`: `SageFs.Core/ProjectLoading.fs:1542` (the old plan said 1462).
- Wire: `ReloadOutcome.ByMetadataDelta` `SageFs.Core/Features/ReloadOutcome.fs:392`, `PatchMechanism` `:246`, `wireName` `:256`; the daemon reads `mechanism` at `SageFs.Core/SessionReload.fs:177` and writes it `:212`.
- Simulation: `SageFs.Simulation/DeltaRouteSim.fs`; the twin "the chain advances before the runtime has answered" is `:142`, reproduced by `SageFs.Tests/DeltaRouteSimTests.fs:114`.
- Fixture for the tripwire: `SageFs.Tests/fixtures/RunAppDeltaFixture`. Pinned SDK: `global.json` `11.0.100-rc.1.26425.128`, `rollForward: latestFeature`.
- The compiler checkout `~/Work/fsharp-compiler-services` exists only on Will's machine; a clone has to fetch dotnet/fsharp itself.
- `[not verified]`: that stock `fsc` warns FS1063 on `--test:HotReloadDeltas`. The plan takes that from dotnet/sdk#55464's text; the tripwire's first run proves or corrects it.

### 3. Steps

1. **Check the signals (ten minutes, repeat monthly and on every SDK bump).** `gh pr view 20030 --repo dotnet/fsharp --json state,mergedAt` and the same for 20031 and 19941; `gh issue view 55464 --repo dotnet/sdk --json state`. Write the date and states into "Measure first". All open: do steps 2 and 3 only.
2. **RED `CompilerDeltaTripwireTests.fs`** (new file, module `SageFs.Tests.CompilerDeltaTripwireTests`, `<Compile Include=...>` in `SageFs.Tests/SageFs.Tests.fsproj` before `Program.fs`), registered with `Integration.hostList` so `--integration-host` runs it with no name filter. It runs `dotnet build SageFs.Tests/fixtures/RunAppDeltaFixture -p:OtherFlags=--test:HotReloadDeltas` under the pinned SDK (child process through `SpawnEnvironment.apply`) and asserts the build output carries the flag's warning (FS1063 is the expected id; the first run tells you the real one, and the assertion names it). Its failure message says "the pinned SDK accepts --test:HotReloadDeltas; read docs/plans/use-the-fsharp-compilers-deltas.md stage 2". No skip cell: a machine without the SDK fails and says so. Edge code; green on day one.
3. **Doc commit.** In `docs/how-hot-reload-works.md`, after the #19941 paragraph (`:19`), one sentence that names the tripwire test and this plan's signal table. Parallel with 2.
4. **The seam, gated on `DECISION:` 1.** RED `DeltaSourceTests.fs`: `OurEmitterSource.Prepare` on a Cecil-built before/after pair (generators in `SageFs.Tests/DeltaProgram.fs`) equals the mapped `PrepareOutcome` for every case. New `SageFs.Core/Features/MetadataDelta/DeltaSource.fs` compiled before `DeltaSession.fs`, `IDeltaSource` as in Design, `RunAppDelta.Session.Save` calls it at `:125`. Green when every `RunAppDeltaTests` and `DeltaRouteSimTests` case is unchanged. Pure type plus one edge call.
5. **Sim extension.** `DeltaRouteSim` takes a modelled source (unavailable, stale generation, discards after prepare) with the invariants and the twin in Tests; cases in `DeltaRouteSimTests.fs`. After 4.
6. **Stage 2 (gated on S3).** `CompilerSessionSource`, `deltaOrigin` additive on `lastReload` and `ReloadReported` with `DeltaOriginWireTests` golden on net10 and net11, the parameterised `RunAppDeltaTests` matrix, experiments 2 to 4. `Plugin: n/a, additive field neither client reads`.

### 4. How to check it

- Inner loop: `dotnet build SageFs.Tests -c Release`; `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary --filter-test-list "tripwire"` (read `ran=`); for the seam, `--filter-test-list "DeltaSource"` on the default run and `--filter-test-list "run_app metadata delta"` on the host tier.
- `--ratchets` after each edit.
- Acceptance: unfiltered `--summary` and `--integration-host --summary` (`SAGEFS_HOST_SHARDS=5`).

### 5. Traps in this repo

- A child-process test uses `SpawnEnvironment.apply`, or the daemon's environment leaks into the build.
- No timing: the tripwire waits on process exit, nothing else; its bound is a `Timeouts` or `TestTimeouts` value, never a literal.
- A seam with one implementation is speculative; budgets only go down, so do not add the seam unless decision 1 says so.
- `RudeCause` is a closed set with one describe; a compiler diagnostic with no case becomes a new case, never a string.
- Pinning `FSharp.Compiler.Service` to an SDK version would be a new dependency: discussion first.
- Commit `SageFs.Tests.fsproj` whole, by pathspec. Linux-only machine.

### 6. Size and order

Steps 1 to 3: one day. Step 4 and 5: two to three days, only on decision 1. Stage 2: unsized until #20030 can be read. First commit: the tripwire test file with its fsproj line (step 2). Steps 2 and 3 are file-disjoint and parallel.

### 7. Not now

- Building the compiler checkout to try the session API before #20030 merges: the signatures are still moving.
- The REPL host as a delta target: it reads coverage from the instrumented module and `--multiemit-` assemblies are reflection-emit (`DeltaApply.moduleGaps`, `CapabilityGap.ModuleIsDynamic`).
- Rewriting the comparison table for `fsharp-watch`: that happens when a stock SDK carries the API, which is S3.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S for the seam, M to L for the adapter |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Work order step 1 (check the five signals with `gh`; all were open on 2026-11-11), then step 2, the tripwire test, and step 3, the doc line. The seam waits on decision 1. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

Nothing ships on a date for this item. It is done in two stages. Stage 1 (now): SageFs has a tripwire that goes red the day the pinned SDK's `fsc` accepts the compiler's delta flag, a written list of signals that say go, and a seam in the delta route so the emitter is one of two sources behind one interface. Stage 2 (only after the signals): a save to a `run_app` app can take its delta from the compiler, and the outcome gate is the existing `RunAppDeltaTests` rows run once per source (ours, the compiler's) on .NET 10 and .NET 11, every row ending in the same process, the new body served, and `Patched`, or in a restart naming the same cause. A row that one source carries and the other refuses is listed in the doc, not skipped.

## What exists today

Verified in this tree (master 59a8692c):

- The emitter is ours. `SageFs.Core/Features/MetadataDelta/` holds `PeImage.fs`, `IlCanon.fs`, `MethodDiff.fs`, `DeltaWriter.fs`, `DeltaApply.fs`, `DeltaSession.fs`, `RudeCause.fs`, `MetadataDeltaFlag.fs`. It reads two finished assemblies and writes the delta. No code was taken from #19941.
- The seam candidates already exist. `DeltaChain.PrepareProbing(encId, next: PeImage, probing)` returns `PrepareOutcome` (`NothingChanged | Ready of PreparedDelta | Refused of RudeCause list`); `DeltaChain.Commit(prepared)` advances the chain; `PreparedDelta.Payload : DeltaPayload` carries `Metadata`, `Il`, `Updated`, `AddedMethods`, `MethodTokens`, `Probes`, `Requires`. `DeltaApply.check` and `DeltaApply.apply` hand a payload to `MetadataUpdater.ApplyUpdate` (with an empty PDB delta, `DeltaApply.fs` line 272). `DeltaSession` (pure) decides `Unchanged | Apply | Restart`, and `DeltaRouteSim` folds it.
- The caller is `SageFs.Host/RunAppDelta.fs` (`Session.Start`, `Session.Save`). `Save` builds with `SageFs.SessionBuild.runBuildAsync`, wraps the new DLL as a `PeImage`, calls `PrepareProbing`, decides, applies, commits.
- The baseline is the Cecil-instrumented shadow copy, not the compiler's output (`docs/decisions.md`, "A metadata delta is computed against the module the worker actually loaded"). `CoverageInstrumenter.instrumentShadowSolution` runs in `ActorCreation.fs` (line 141) and `AppState.fs` (line 1601). This matters below.
- The REPL host cannot take a delta today, for the reason in `docs/decisions.md` ("The REPL is behind the app after a delta"): the host reads coverage and a patched method runs without its probes; FSI's own `--multiemit-` assemblies are reflection-emit and `DeltaApply.moduleGaps` refuses them (`CapabilityGap.ModuleIsDynamic`).

What I read in the F# compiler checkout at `~/Work/fsharp-compiler-services` (main at cdb9dc5e5, 2026-09-19):

- Foundations of #19941 have merged as internal infrastructure with no in-tree caller: #20017 (`ResetCompilerGeneratedNameState`), #20018 (Roslyn-format EnC custom debug info, `AbstractIL/EncMethodDebugInformation.fs`), #20019 (ECMA-335 delta writer, `AbstractIL/FSharpDeltaMetadataWriter.fs`, 992 lines), #20024 (synthesized-name replay), #20026 (`CodeGen/HotReloadBaseline.fs`, `FSharpEmitBaseline`). The merged files are only referenced from `FSharp.Compiler.Service.fsproj`.
- I found no `HotReloadDeltas` switch and no `CreateHotReloadSession` or `FSharpHotReloadSession` anywhere in `src`. The user-visible part is not in main.
- FSI multiemit: `docs/fsi-emit.md` says Reflection.Emit on .NET Core cannot write debug symbols, so FSI moved to multiple in-memory assemblies (`--multiemit+`, default on .NET Core). `fsi.fs` loads each with `Assembly.Load(bytes, pdbBytes)`. SageFs runs `--multiemit-` for hot reload (`SageFs.Core/ProjectLoading.fs:1542` on 2026-11-11).

What GitHub says (read 2026-10-26): #19941 is open, `mergeable_state` dirty, now an umbrella for four open slices: #20025 (typed-tree differ and edit classification), #20027 (delta emitter and symbol matcher), #20030 (session and FCS service surface), #20031 (experimental flag-gated in-process compile). Its description: opt-in `--test:HotReloadDeltas`, metadata, IL and portable PDB deltas, `FSharpChecker.CreateHotReloadSession`, unsupported edits require a rebuild. dotnet/sdk#55464 (open, assigned) says the session API (`AddProject`, `EmitDelta`, `Commit`, `Discard`, `UpdateCapabilities`, `SetActiveStatements`, `Dispose`) is the cross-repo contract, that watch waits for its new architecture, and that stock `fsc` warns FS1063 on the flag today. I did not read that API's source; it is not in the checkout.

## Design

The seam is one value-level interface in a new file `SageFs.Core/Features/MetadataDelta/DeltaSource.fs`, compiled before `DeltaSession.fs`. Both sources answer the same question: given the build the worker holds and the build on disk now, what is the next delta.

```fsharp
[<RequireQualifiedAccess>]
type DeltaOrigin =
  | OurEmitter
  | CompilerSession of compilerVersion: string

/// What a source needs from the caller to prepare one save.
type DeltaRequest =
  { Next: PeImage
    EncId: Guid
    Probing: EntryProbing }

[<RequireQualifiedAccess>]
type SourceOutcome =
  | NothingChanged
  | Ready of origin: DeltaOrigin * payload: DeltaPayload * fromGeneration: int
  | Refused of origin: DeltaOrigin * causes: RudeCause list
  /// The source cannot answer for this module at all, and says why (no session API, baseline from another compile).
  | SourceUnavailable of origin: DeltaOrigin * why: string

type IDeltaSource =
  abstract Prepare: DeltaRequest -> SourceOutcome
  abstract Commit: fromGeneration: int -> unit
  abstract Discard: unit -> unit
```

Stage 1 wraps `DeltaChain` as `OurEmitterSource` and changes nothing observable. `Preparation` in `DeltaSession.fs` gains no new case; `SourceUnavailable` maps to `Refusal.Unavailable`. `Session.Save` in `RunAppDelta.fs` calls the interface where it now calls `current.PrepareProbing`. `ReloadOutcome.ByMetadataDelta` and the wire `mechanism` stay `metadata-delta`. If both sources ship, `mechanism` stays one value and the report gains `deltaOrigin` (additive), because a client has to know which emitter refused an edit.

Stage 2 adapter (`CompilerSessionSource`) is written only after the signals. It maps the compiler's `RudeEdit` diagnostics (FSHRDL ids, per the SDK issue) onto `RudeCause`; a diagnostic with no matching case becomes a new `RudeCause` case, never a string. `RudeCause` is a closed set with one describe function, so it grows by case.

The unsolved design problem is the baseline. A delta from the compiler is numbered against the compiler's own output. The worker loads a Cecil-rewritten copy whose rows, heaps and method bodies differ (that is why our emitter re-expresses every token). A compiler delta applied to a Cecil-written module will not line up. Three ways out, in order of preference:

| Option | Cost |
|---|---|
| Do not instrument the module of a `run_app` app that runs the delta route. Coverage is read only in the FSI host (`HostAgent.TakeCoverage`), so nothing in the worker loses a number. | Needs a check that nothing in the worker reads coverage, and a change to the instrumenter's target list. Cheapest. |
| Keep our emitter as the applier and take the compiler only as the differ and refuser. | Keeps our token remapping. Gains the compiler's closure and state-machine matching. |
| Instrument after applying. | Not possible; probe slots are fixed at load. |

The REPL host stays out of scope for stage 2. It reads coverage, so the first row's answer does not carry over.

Wire and client impact: stage 1 none. Stage 2 adds `deltaOrigin` to `lastReload` and `ReloadReported` (additive, no `apiVersion` move). `sagefs.nvim` and `sagefs-vscode` need nothing unless they choose to show it; the commit carries `Plugin: n/a, additive field neither client reads`. Persistence: none. Token cost: none (no tool added).

## Tests

RED tests first (stage 1):

1. `DeltaSourceTests`: "WHY - the emitter wrapped as a source gives the answer `DeltaChain` gave, for every `PrepareOutcome` case". Asserts `OurEmitterSource.Prepare` on a Cecil-built before/after pair (reuse `SageFs.Tests/DeltaProgram.fs` generators) equals the mapped `PrepareOutcome`. RED because the type does not exist (compile-red, per the repo's proof-of-broken habit).
2. `CompilerDeltaTripwireTests` in the host tier (`--integration-host`, registered by reference in the `Integration` registry, not by name): builds `SageFs.Tests/fixtures/RunAppDeltaFixture` with `--test:HotReloadDeltas` under the pinned SDK (`global.json`, 11.0.100-rc.1.26425.128) and asserts the build reports FS1063 for the flag. Green today. It goes red the day an SDK accepts the flag, and its failure message names this plan. No skip cell: on a machine without the SDK it fails and says so.
3. `DeltaOriginWireTests` (stage 2 only): `lastReload` round-trips `deltaOrigin` through `SageFs.Json`, golden text on net10 and net11.

Outcome gate (stage 2): `RunAppDeltaTests` parameterised by `DeltaOrigin` x runtime (net10, net11) x row (every existing row). The matrix is generated from `DeltaOrigin` cases so a third source adds cells. Each cell must end `Patched` or in a restart with the expected `RudeCause`; a cell where the sources differ is asserted explicitly in a table, not skipped.

Simulation: `DeltaRouteSim` already folds `DeltaSession` over seeded events. Add the source as a modelled input: a source that answers `SourceUnavailable` at any generation, a source whose `Prepare` returns a generation other than the chain's (stale), a source that `Discard`s after a prepare. Invariants: a refused or unavailable source never advances the chain; a `Ready` from generation g applies only when the process is at g; after `Discard` the next prepare starts at the same generation. Twin: a source adapter that commits on `Prepare` instead of on landing; the existing "advance the chain before the runtime answers" twin shows the pattern, and the new one must be caught the same way.

All tests wait on events already in use (`AppRunState`, the reload report), none on a deadline.

## Steps

1. (Waits on decision 1; the default is to wait for S1.) RED `DeltaSourceTests` (compile-red). Add `DeltaSource.fs`, `OurEmitterSource`, route `RunAppDelta.Session.Save` through it. Files: new `DeltaSource.fs`, `SageFs.Core.fsproj`, `RunAppDelta.fs`. Green when the whole `RunAppDeltaTests` and `DeltaRouteSimTests` rows are unchanged.
2. Add `CompilerDeltaTripwireTests` (host tier). Parallel with 1; owns only the new test file and its fsproj line.
3. Link the tripwire and the signal table below from the #19941 paragraph of `docs/how-hot-reload-works.md`, and say in it that the foundations (#20017, #20018, #20019, #20024, #20026) have merged and the user-facing slices have not. Doc-only commit; parallel with 1 and 2.
4. Extend `DeltaRouteSim` with the source inputs and the new twin. After step 1.
5. (Stage 2, gated) `CompilerSessionSource`, `deltaOrigin` on the wire, the parameterised outcome gate. Not started until the go signals in "Measure first" hold.

## Measure first

Signals, checked on the first of each month and on every SDK bump (the tripwire does the second):

| Signal | Source | Meaning |
|---|---|---|
| S1 | #20030 merged into dotnet/fsharp main | The session API exists to read. Read its source, then write down the real signatures here. |
| S2 | #20031 merged | A session can produce baseline and deltas from one compile, which removes the on-disk build as the floor. |
| S3 | Tripwire red under a newer pinned SDK | A shipped `fsc` takes the flag. Go for stage 2. |
| S4 | dotnet/sdk watch ships an F# provider | Microsoft's tools hot reload F#. The doc comparison table must be rewritten that day. |
| S5 | `fsharp-watch` (NuGet, Nat Elkins) works on a stock SDK | Same meaning as S3 from the other side; `docs/how-hot-reload-works.md:19` tracks it. |

Checked 2026-11-11 with `gh`: #19941, #20025, #20027, #20030, #20031 all OPEN (updated 2026-10-30/31); dotnet/sdk#55464 OPEN. S1 to S5 false.

Experiments, cheapest first, none needing a shipped SDK:

1. Build the compiler checkout from `~/Work/fsharp-compiler-services` only when S1 is true. Not before: no build now (a release gate is running).
2. Does a compiler delta apply to an uninstrumented module in the worker? With S3 true, take the `RunAppDeltaFixture` rows, skip instrumentation for the worker's shadow copy, apply the compiler's delta with `DeltaApply.apply`. Applies: stage 2 uses option 1 in the table. Refused: keep our emitter as the applier (option 2).
3. Does the compiler carry the rows ours restarts on (a field added, a new closure class, a lambda that starts capturing)? The PR text says it supports closures and member additions. Run the same three edits through both sources and compare. This is the gain; if it is small, stage 2 is not worth its adapter.
4. Build time. A compiler session removes the `dotnet build` floor (1.6 to 2.4 s of a 1.8 to 2.6 s save). Measure save-to-served with S2's in-process path on the same fixture and the same machine as the existing `DELTA-COST` numbers.

## Risks and dependencies

- The session API is experimental and the SDK issue says exact identity at runtime is required. Pinning `FSharp.Compiler.Service` to the SDK's version is a dependency SageFs does not take today.
- A second emitter doubles the places a delta bug can live. The parameterised gate is the guard.
- Debugger edits (`breakpoints-in-patched-code`) need the compiler's PDB delta; this item's S3 is that item's precondition for its delta track.
- Nothing depends on this item shipping. `redefine-a-value-a-handler-renders` and `hot-reload-without-tiering-off` run on the detour route.

## Size

Stage 1 is S (days): a seam, a wrapper, a tripwire, one sim extension. Stage 2 is M to L and cannot be sized until S1 is read; the baseline problem decides it.

## Decisions for Will

1. Stage 1 now, or only the tripwire? I recommend steps 2 and 3 now and step 1 (the seam) when S1 flips, because an interface with one implementation is speculative. The cost of waiting is one refactor day.
2. May the worker's shadow copy skip coverage instrumentation for delta-route apps? It decides stage 2's cheapest path. I recommend yes after experiment 2.
