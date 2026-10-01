# Warmup Design: Next Steps

Two design proposals for reducing SageFs warmup latency: extending the replay cache, and measuring ReadyToRun impact. There used to be a third, eager prewarming of a standby worker. It is gone, and the end of this page says why.

I checked this page against the tree on 2026-10-01. It names functions and types instead of line numbers, because the line numbers in the first version of this page had all moved. Where I say a thing exists, I looked at it that day.

**Baseline architecture** (read [`fsi warmup.md`](fsi%20warmup.md) for the full analysis):

| Phase | Function | Cost Profile |
|---|---|---|
| `creating_fsi` | `FsiEvaluationSession.Create(...)` in `SageFs.Core/AppState.fs` | JIT-dominated, 5 to 15 s |
| `scanning_sources` | `discoverWarmupReplayPlan` in `SageFs.Core/AppState.fs` | I/O-bound, about 100 ms with parallel scan |
| `loading_assemblies` | Reflection over the project's assemblies, same function | I/O and reflection, variable |
| `opening_namespaces` | `WarmUp.openWithRetryRichBatched` in `SageFs.Core/WarmUp.fs` | FSI eval-bound, 60 to 90% of total |

The per-phase timing is a `WarmupPhaseTiming` record in `SageFs.Core/WarmUp.fs` (scan sources, scan assemblies, open namespaces, total), and `AppState.fs` logs the total when warmup finishes. The 5 to 15 s and 60 to 90% figures in the table are the first version's estimates. I haven't re-measured them for this edit.

---

## 1. Replay Cache Design

### What Exists Today

The warmup replay cache is already implemented in `SageFs.Core/WarmupReplayCache.fs`. It caches the discovery result, the list of namespaces and modules to open, so a later startup skips the source-file scanning and assembly reflection phases.

**Current flow** (`resolveWarmupReplayPlan` in `AppState.fs`):

```
buildFingerprintForSolution -> fingerprint
resolveWarmupReplayPlan(fingerprint):
  cache hit?  -> return the cached ReplayPlan
  cache miss? -> discoverWarmupReplayPlan() -> save -> return
```

**What's cached** (`WarmupReplayCache.ReplayPlan`):
- `Fingerprint`: the schema version (5 today), whether namespaces are auto-opened, the FSI args, file stamps (path, size, mtime) for startup files, source files and assembly files, and a content hash for each project-definition file
- `SourceFilesScanned`: a count
- `AssembliesLoaded`: the assemblies with their namespace and module counts
- `NamesToOpen`: each name with whether it is a module
- `ProjectFileNames` and `DiscoveryWarnings`

**Cache key**: structural equality on `Fingerprint`. A change to any stamped file, or to the content of a project file (a package version bump leaves path, size and mtime alone and still changes what a build exposes), makes the fingerprint differ and discovery runs again.

**Storage**: JSON at `{projectDir}/.SageFs/warmup-replay-cache.json`, from `tryGetCachePath`.

### What's NOT Cached

The cache saves which namespaces to open. The `open X;;` evaluations themselves still happen on every startup, so the opening phase, the biggest one, isn't helped.

### Proposed Extension: Pre-Compiled Warmup Assembly

Nothing like this exists in the tree: I searched for it on 2026-10-01 and found no precompiled-warmup code.

**Concept**: after a successful warmup, compile the whole `open` sequence into a DLL. On the next startup, replace the N individual `open X;;` evals with one `#r "warmup-precompiled.dll"`.

**Cache key**: the same fingerprint as the replay cache. The DLL would sit next to the JSON plan, at `{projectDir}/.SageFs/warmup-precompiled.dll`.

**Invalidation**: any file in the fingerprint changes, the .NET SDK changes (visible as assembly stamps changing after a rebuild), or `WarmupReplayCache.SchemaVersion` is bumped.

**Compilation step**: after warmup completes, emit a script like:

```fsharp
namespace WarmupPrecompiled
open System
open System.IO
open MyProject.Domain
// ... all opened namespaces
```

and compile it with `fsc --target:library --out:warmup-precompiled.dll warmup-script.fsx --reference:...` in the background, so it doesn't slow the first start.

**On the next startup**: if the DLL exists and the fingerprint matches, do `#r "{path}/warmup-precompiled.dll"` and `open WarmupPrecompiled` in place of the individual opens.

### Risks

1. **Assembly version mismatches.** If project DLLs are rebuilt and the fingerprint somehow misses it, the precompiled DLL references stale types. File stamps and project-file hashes make that unlikely.

2. **`open` side effects.** Some modules have `do` bindings that run on open, and a precompiled assembly won't run them the way FSI does. The plan would be to cache only the `open` list and never init scripts. The startup profile (`StartupProfile.fs`) runs after warmup whatever the cache does.

3. **Compilation latency.** Running `fsc` in the background adds CPU load after warmup, and on a low-core machine that could slow your first interaction. Lower its priority and cancel it if the session restarts first.

### Recommendation

Worth pursuing, as a second step. The replay cache already removes the scanning phase and the precompiled assembly would go after the opening phase, which is the real bottleneck. The work is not small, though: a background `fsc`, DLL management, and a plan for stale DLLs.

**Concrete next step**: measure how much of the opening phase is `EvalInteractionNonThrowing` against FSI's own overhead. If the per-open cost is FSI compilation, a precompiled DLL won't help much. If it's type resolution, the DLL could cut opening time a lot. I haven't measured it.

A histogram per open batch and one for the whole phase, next to the other meters in `SageFs.Core/Instrumentation.fs`, would give that data:

```fsharp
let warmupOpenPhaseMs =
  sessionMeter.CreateHistogram<float>(
    "sagefs.warmup.open_phase_ms", "ms",
    "Warmup namespace-opening phase duration")
let warmupOpenBatchMs =
  sessionMeter.CreateHistogram<float>(
    "sagefs.warmup.open_batch_ms", "ms",
    "Per-batch open duration during warmup")
```

They'd be recorded around the batch opener and around the whole opening phase in `AppState.fs`. Neither metric exists yet.

---

## 2. Eager Prewarm: superseded

The first version of this page described a standby worker pool as implemented and proposed warming standbys at daemon boot. The pool was removed (see "No standby worker pool" in [decisions.md](decisions.md#no-standby-worker-pool)): the spares cost memory and held ports, the boot contention cost startup time, and the restart path they were meant for was never wired to use them. A restart is now spawn-first: the replacement starts, the old worker keeps serving until the new one is ready, and then they swap. There is no `StandbyPool.fs` and nothing to prewarm into, so I removed the design instead of leaving it to describe code that isn't there.

If cold-start time becomes the main complaint, `decisions.md` says to reopen it from measurements, and the pool would be a design to write again, not a page to revive.

---

## 3. ReadyToRun Measurement Plan

### What ReadyToRun Does

`PublishReadyToRun` (R2R) pre-JITs IL to native code at publish time. The assemblies carry both IL and native code. The JIT still runs for methods the R2R image doesn't cover, but the hot startup path is precompiled.

SageFs ships as a global dotnet tool (`PackAsTool` in `SageFs/SageFs.fsproj`). R2R works with tools, and the NuGet package would include platform-specific native images.

### Current State

No R2R configuration exists. I grepped every `.fsproj` and `.props` in the repo for `ReadyToRun` on 2026-10-01 and found nothing, so the tool runs with full JIT on every invocation.

### What to Measure

**Milestone 1: process startup to FSI session creation.** From the entry point in `Program.fs` to `FsiEvaluationSession.Create` returning. That captures runtime init, SageFs bootstrap and the F# compiler's JIT.

**Milestone 2: JIT time during warmup.** `System.Runtime.JitInfo.GetCompiledMethodCount()` and `GetCompiledILBytes()` before and after warmup. The delta is the JIT work in the opening phase, and R2R should shrink it.

**Milestone 3: total warmup wall clock.** From `warmupStartedAt` to `warmupCtx.PhaseTiming.TotalMs`, both in `AppState.fs`. `Instrumentation.startupDurationMs` is a different thing, the daemon's startup to ready, so don't use it for this.

**Milestone 4: binary size.** The nupkg size after `dotnet pack`, with and without R2R. R2R typically makes the affected assemblies 2 to 3 times larger.

### Test Procedure

**Baseline (no R2R)**:

```bash
# Build and pack without R2R
dotnet pack SageFs -o nupkg -c Release
ls -l nupkg/*.nupkg
# Install it
dotnet tool install --global SageFs --add-source nupkg --no-cache
```

Then time a cold start three times and take the median. There is no `--headless --quit-after-warmup` flag (I checked the CLI on 2026-10-01), so the experiment needs a temporary flag that runs the full warmup and exits with the timing, or it can read the total that `WarmupPhaseTiming` logs.

**With R2R**: add this to `SageFs/SageFs.fsproj` and repeat the measurement. R2R applies to Release builds, so Debug is unaffected.

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <PublishReadyToRun>true</PublishReadyToRun>
</PropertyGroup>
```

**Platforms**: CI runs on Linux only, so a Windows number would need a Windows machine. JIT behaviour and tiered compilation defaults differ between platforms, so I wouldn't carry a Linux figure over.

### Expected Impact

These are guesses I wrote before measuring anything:

| Metric | Expected Change | Confidence |
|---|---|---|
| Process startup to FSI create | 20 to 40% faster | High. R2R removes first-invocation JIT for SageFs code |
| JIT bytes during warmup | 10 to 30% less | Medium. FSI's own JIT isn't covered by R2R |
| Total warmup wall clock | 5 to 15% faster | Low to medium. Most of the time is FSI eval, not SageFs JIT |
| Binary size | 2 to 3 times larger nupkg | High. Standard R2R overhead |

### Why This Might NOT Help Much

The F# compiler (`FSharp.Compiler.Service.dll`) is the biggest JIT consumer during warmup, and it's a NuGet dependency. R2R only precompiles assemblies in the SageFs tool package, so FCS would need its own R2R treatment, which the F# team hasn't shipped.

### Recommendation

Run the experiment before committing R2R to the build. If process startup improves by more than 20% but total warmup by under 5%, it may not be worth a nupkg 2 to 3 times the size for a tool that's installed once and run often. If the data is good, enable it for Release only. A stretch goal would be `crossgen2` in composite mode to precompile FCS as well, which needs more build work and goes at the real JIT bottleneck.

---

## Summary: Priority Order

| Design | Effort | Expected Gain | Existing Foundation |
|---|---|---|---|
| **R2R measurement** | Low: add one property, run benchmarks | 5 to 15% of total warmup (a guess) | None, a clean experiment |
| **Replay cache extension** | Medium: background `fsc`, DLL management | 50 to 80% of the opening phase (a guess) | The replay cache JSON exists |

Run the R2R experiment first, since it's cheap and touches nothing else. Do the replay cache extension only if the instrumentation above shows the opening phase is the bottleneck and FSI eval overhead is the cause.
