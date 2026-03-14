# Warmup Design: Next Steps

Three design proposals for reducing SageFs warmup latency. Each is scoped to a specific concern: caching the replay plan, eagerly prewarming sessions, and measuring ReadyToRun impact.

**Baseline architecture** (read `docs/fsi warmup.md` for full analysis):

| Phase | Function | Cost Profile |
|---|---|---|
| `creating_fsi` | `FsiEvaluationSession.Create(...)` in `AppState.fs:591–603` | JIT-dominated, 5–15s |
| `scanning_sources` | `discoverWarmupReplayPlan` in `AppState.fs:401–575` | I/O-bound, ~100ms with parallel scan |
| `loading_assemblies` | Reflection via `AssemblyLoadContext` in `AppState.fs:469–561` | I/O + reflection, variable |
| `opening_namespaces` | `openWithRetryRichBatched` in `WarmUp.fs:199–231` | FSI eval-bound, 60–90% of total |

Current warmup timing is captured in `WarmupPhaseTiming` (`WarmUp.fs:36–41`) and logged at `AppState.fs:781–785`.

---

## 1. Replay Cache Design

### What Exists Today

The warmup replay cache is **already implemented** in `WarmupReplayCache.fs`. It caches the *discovery* result — the list of namespaces/modules to open — so subsequent startups skip the source-file scanning and assembly reflection phases entirely.

**Current flow** (`AppState.fs:629–653`):

```
buildFingerprintForSolution → fingerprint
resolveWarmupReplayPlan(fingerprint):
  cache hit?  → return cached ReplayPlan
  cache miss? → discoverWarmupReplayPlan() → save → return
```

**What's cached** (`WarmupReplayCache.ReplayPlan`):
- `Fingerprint`: schema version, FSI args, file stamps (path + size + mtime) for startup files, source files, and assembly files
- `SourceFilesScanned`: count
- `AssembliesLoaded`: list of `LoadedAssembly` (name, path, namespace/module counts)
- `NamesToOpen`: list of `(name, isModule)` pairs

**Cache key**: structural equality on `Fingerprint`. If any file stamp changes (size or mtime), the fingerprint mismatches and the cache is invalidated.

**Storage**: JSON at `{projectDir}/.SageFs/warmup-replay-cache.json`, created by `tryGetCachePath` (`WarmupReplayCache.fs:132–143`).

### What's NOT Cached

The replay cache saves which namespaces to open, but the actual `EvalInteractionNonThrowing("open X;;")` calls still happen every startup. The opening phase (60–90% of warmup) is unaffected by the current cache.

### Proposed Extension: Pre-Compiled Warmup Assembly

**Concept**: After a successful warmup, compile the entire `open` sequence into a DLL. On next startup, replace N individual `open X;;` FSI evals with a single `#r "warmup-precompiled.dll"`.

**Cache key**: Same fingerprint as current replay cache — hash of FSI args + file stamps. Store the DLL alongside the JSON plan.

**Invalidation triggers**:
- Any file in the fingerprint changes (source files, assembly files, startup files)
- .NET SDK version change (detected via assembly file stamps changing after rebuild)
- Schema version bump in `WarmupReplayCache.SchemaVersion`

**Storage format**: The compiled DLL at `{projectDir}/.SageFs/warmup-precompiled.dll`, keyed by the same fingerprint.

**Compilation step**: After warmup completes, emit a script like:

```fsharp
namespace WarmupPrecompiled
open System
open System.IO
open MyProject.Domain
// ... all opened namespaces
```

Compile with `fsc --target:library --out:warmup-precompiled.dll warmup-script.fsx --reference:...`. This runs in the background after the first successful warmup, so it doesn't slow down the initial start.

**On next startup**: If `warmup-precompiled.dll` exists and fingerprint matches, do `#r "{path}/warmup-precompiled.dll"` and `open WarmupPrecompiled` instead of the N individual opens.

### Risks

1. **Assembly version mismatches**: If project DLLs are rebuilt but the fingerprint somehow doesn't catch it (unlikely given mtime stamps), the precompiled DLL references stale types. Mitigation: file stamps are high-fidelity — size + mtime changes on any rebuild.

2. **`open` side effects**: Some modules have `do` bindings that run on open. A precompiled assembly referencing these modules doesn't execute those side effects the same way FSI does. Mitigation: only cache the `open` list, not init scripts. `StartupProfile` (`StartupProfile.fs:41–48`) always runs after warmup regardless of cache.

3. **Compilation latency**: Running `fsc` in the background adds CPU load after warmup. On low-core machines this could slow down the user's first interaction. Mitigation: lower priority, cancel if the session is restarted before compilation finishes.

### Recommendation

**Worth pursuing, but as a Phase 2 optimization.** The current replay cache already eliminates the scanning phase. The precompiled assembly would attack the opening phase, which is the real bottleneck. However, the implementation complexity (background `fsc` invocation, DLL management, error handling for stale DLLs) is non-trivial.

**Concrete next step**: Instrument the opening phase to measure how much time is spent in `EvalInteractionNonThrowing` vs. FSI internal overhead. If the per-open cost is dominated by FSI compilation (not the `open` resolution itself), a precompiled DLL won't help much — the bottleneck is FSI's incremental compiler. If it's dominated by type resolution, the DLL approach could cut opening time by 80%+.

Add a histogram metric in `Instrumentation.fs`:

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

Record at `AppState.fs:726` (after `batchOpener`) and `AppState.fs:750` (total open phase). This gives real data to decide whether the precompiled approach is worthwhile.

---

## 2. Eager Prewarm Design

### What Exists Today

The **standby pool** is already implemented in `StandbyPool.fs` and wired into `SessionManager.fs:997–1060`. Key types:

- `StandbyState`: `Warming | Ready | Invalidated` (`StandbyPool.fs:9–12`)
- `StandbySession`: pre-warmed worker process with optional `SessionProxy` (`StandbyPool.fs:15–24`)
- `StandbyKey`: config identifier — projects + workingDir + autoOpenNamespaces (`StandbyPool.fs:28–32`)
- `PoolState`: `Map<StandbyKey, StandbySession>` (`StandbyPool.fs:138–141`)

**Current trigger**: `StandbyPool.shouldWarmStandby` (`StandbyPool.fs:77–90`) returns true when the primary session is healthy (`Ready | Evaluating | Building`) and no standby exists for that key. The `SessionManager` posts `WarmStandby` after session creation succeeds (`SessionManager.fs:644`) and after restart completes (`SessionManager.fs:767`).

**Current swap logic**: On `RestartSession`, `StandbyPool.decideRestart` (`StandbyPool.fs:111–120`) checks if a ready standby exists. If `rebuild=false` and standby is `Ready` with a valid `Proxy`, it swaps instantly. Otherwise, cold restart.

### What's Missing: Daemon-Side Eager Prewarming

The standby pool only warms *after* a primary session exists. There is no prewarming at daemon startup — the first session is always cold.

**Proposed addition**: On daemon startup, after loading the binary manifest (`DaemonMode.fs` resume flow), immediately start warming a standby for each session configuration that was active in the previous daemon run.

### Integration Points

1. **Daemon boot** (`DaemonMode.fs:938–960`): After `loadManifest` returns previous session records, extract the `(projects, workingDir, autoOpenNamespaces)` tuples and post `WarmStandby` for each unique `StandbyKey` — *before* any client connects.

2. **First client request**: When `CreateSession` arrives, check if a ready standby exists for that key via `PoolState.tryConsumeStandby`. If yes, swap it in as the primary. If no (still warming), fall through to normal cold creation — the standby continues warming and becomes available for the first *restart*.

3. **MRU priority**: If the manifest contains multiple session configs, prewarm them in most-recently-used order. The manifest stores `CreatedAt` timestamps per session (`ManifestPersistence.fs:47–62`), so sort by recency.

### Resource Constraints

- **Memory**: Each FSI worker process consumes 200–500MB. On a 16GB machine, 2–3 standby workers is a reasonable ceiling. Add a `--max-standby` CLI flag (default: 1) to cap the pool.

- **CPU**: Warmup is CPU-intensive (JIT + compilation). Running N warmups concurrently on an M-core machine causes contention. Limit concurrent warmup spawns to `max(1, ProcessorCount / 4)`.

- **Stale standbys**: If the daemon boots and the user opens a different project than last time, the prewarmed standby is wasted. Mitigation: set a TTL (e.g., 5 minutes). If a standby isn't consumed within the TTL, kill the worker and reclaim memory.

### Cancellation

If an explicit `CreateSession` request arrives while a standby is still in `Warming` state, two options:

1. **Let it finish**: The standby continues warming and becomes available for swap on next restart. The client gets a cold start this time but faster restarts later.
2. **Cancel and redirect resources**: Kill the warming standby, redirect CPU to the primary session's warmup. This avoids the scenario where two sessions are warming simultaneously on a low-core machine.

**Recommendation**: Option 1 (let it finish). The standby pool already handles this — if the standby isn't `Ready` when `decideRestart` runs, it returns `ColdRestart`. The standby keeps warming in the background and will be consumed on the next restart.

### Concrete Next Steps

1. **Add `--eager-prewarm` CLI flag** (default: off initially). When enabled, the daemon posts `WarmStandby` for manifest sessions on boot.

2. **Extract prewarm configs from manifest**: In the daemon resume flow, after deduplicating session records, collect unique `StandbyKey` values and post them to `SessionManager`.

3. **Add TTL eviction**: In the `SessionManager` mailbox loop, add a periodic timer (e.g., every 60s) that checks standby ages and kills workers older than `--standby-ttl` (default: 300s).

4. **Metrics**: Record `Instrumentation.standbyWarmupMs` (already exists at line 67) when standbys complete. Add:

   ```fsharp
   let eagerPrewarmAttempts =
     sessionMeter.CreateCounter<int64>(
       "sagefs.standby.eager_prewarm_attempts_total",
       description = "Total eager prewarm attempts at daemon boot")
   let eagerPrewarmHits =
     sessionMeter.CreateCounter<int64>(
       "sagefs.standby.eager_prewarm_hits_total",
       description = "Eager prewarmed standbys consumed by CreateSession")
   ```

---

## 3. ReadyToRun Measurement Plan

### What ReadyToRun Does

`PublishReadyToRun` (R2R) pre-JITs IL to native code at publish time. The produced assemblies contain both IL (for portability) and native code (for fast startup). The JIT still runs for methods not covered by the R2R image, but the hot startup path is pre-compiled.

SageFs is published as a global dotnet tool (`PackAsTool=true` in `SageFs.fsproj:5`). R2R is compatible with tools — the NuGet package includes platform-specific native images.

### Current State

No R2R configuration exists. `SageFs.fsproj` has no `PublishReadyToRun` property. The tool runs with full JIT on every invocation.

### What to Measure

**Milestone 1: Process startup to FSI session creation**
- Start: process entry point (`Program.fs` main)
- End: `FsiEvaluationSession.Create` returns (`AppState.fs:593`)
- This captures .NET runtime init + SageFs bootstrap + F# compiler JIT

**Milestone 2: JIT time during warmup**
- Use `System.Runtime.JitInfo.GetCompiledMethodCount()` and `GetCompiledILBytes()` before and after warmup
- Delta shows how much JIT work happens during the opening phase
- R2R should reduce this delta significantly

**Milestone 3: Total warmup wall clock**
- Start: `warmupStartedAt` (`AppState.fs:582`)
- End: `warmupCtx.PhaseTiming.TotalMs` (`AppState.fs:781`)
- Already instrumented via `Instrumentation.startupDurationMs`

**Milestone 4: Binary size**
- Before: current nupkg size (check `nupkg/*.nupkg` after `dotnet pack`)
- After: nupkg size with R2R enabled
- R2R typically increases binary size 2–3x for the affected assemblies

### Test Procedure

**Baseline (no R2R)**:

```powershell
# Build and pack without R2R
dotnet pack SageFs -o nupkg -c Release
# Record nupkg size
Get-ChildItem nupkg/*.nupkg | Select-Object Name, Length
# Install and run
dotnet tool install --global SageFs --add-source nupkg --no-cache
# Measure cold start (3 runs, take median)
Measure-Command { SageFs --proj SageFs.Tests/SageFs.Tests.fsproj --headless --quit-after-warmup }
```

Note: `--headless --quit-after-warmup` doesn't exist yet. For the experiment, add a temporary `--benchmark-warmup` flag that runs the full warmup pipeline and exits with timing on stdout. Or, use the existing `WarmupPhaseTiming` logged to the SageFs console.

**With R2R**:

Add to `SageFs.fsproj`:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <PublishReadyToRun>true</PublishReadyToRun>
</PropertyGroup>
```

Repeat the same measurement. R2R only applies to Release builds, so Debug is unaffected.

**Cross-platform**: Run on both Windows (where R2R is well-tested) and Linux (WSL2 or CI). JIT behavior differs — Linux uses RyuJIT with different tiered compilation defaults.

### Expected Impact

| Metric | Expected Change | Confidence |
|---|---|---|
| Process startup → FSI create | 20–40% faster | High — R2R eliminates first-invocation JIT for SageFs code |
| JIT bytes during warmup | 10–30% reduction | Medium — FSI's internal JIT is not covered by R2R |
| Total warmup wall clock | 5–15% faster | Low-medium — most time is in FSI eval, not SageFs JIT |
| Binary size | 2–3x larger nupkg | High — standard R2R overhead |

### Why This Might NOT Help Much

The F# compiler (`FSharp.Compiler.Service.dll`) is the biggest JIT consumer during warmup, and it's a NuGet dependency — not part of SageFs's own assemblies. R2R only pre-compiles assemblies in the SageFs tool package. FCS would need its own R2R treatment (which the F# team has not shipped).

### Recommendation

**Run the experiment before committing to R2R in the build.** The measurement is cheap (a few hours of benchmarking). If process startup improves by >20% but total warmup only improves by <5%, R2R may not be worth the 2–3x nupkg size increase for a global tool that's installed once and run frequently.

If the data shows meaningful improvement, enable it conditionally in Release only:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <PublishReadyToRun>true</PublishReadyToRun>
</PropertyGroup>
```

This keeps Debug fast for development while optimizing the published tool.

**Stretch goal**: If R2R shows promise, investigate `crossgen2` with composite mode to also pre-compile FCS and other large dependencies. This requires more build infrastructure but could address the real JIT bottleneck.

---

## Summary: Priority Order

| Design | Effort | Expected Gain | Existing Foundation |
|---|---|---|---|
| **Eager prewarm** | Low — wire manifest configs to `WarmStandby` on boot | 5–15s perceived latency (clock starts earlier) | StandbyPool fully implemented |
| **R2R measurement** | Low — add one .fsproj property, run benchmarks | 5–15% total warmup (speculative) | None, clean experiment |
| **Replay cache extension** | Medium — background `fsc`, DLL management | 50–80% of opening phase (speculative) | Replay cache JSON exists |

Start with eager prewarm (lowest risk, uses existing code). Run R2R experiment in parallel (independent, no code conflict). Tackle replay cache extension only if instrumentation data shows the opening phase is the dominant bottleneck and FSI eval overhead is the cause.
