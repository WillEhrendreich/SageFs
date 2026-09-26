# Where type-migration stands, and what each piece buys

## The chain, and where it is live

| Step | Commit | Reachable from a save? |
|---|---|---|
| 1 granular restart | `4195ca99` | yes — `RestartScope` names the unit |
| 2 holder | — | no caller yet |
| 3 derived migration | — | no caller yet |
| 4 rewrite side conditions | `45b75eb1` | no caller yet |
| 5 translation validation | `4465eae0` | no caller yet |
| attribution | `c5d5f5b9` | yes — from the planner's own parse |
| subject decision | `fa6c0060` | yes — every reason classified |
| subject → action | `07735851`, `5bb2d7f6` | yes — through `RestartPlan` |
| boundaries | `6ad97294`, `70519d6c` | user-declared, wins over inference |
| build cost | `bab682b7`, `b2f46465` | yes — decides respawn vs rebuild |

Steps 2–5 are built, tested and DST'd, and still have no caller. That is the
honest state, not a detail.

## What a type change now actually does

Dogfooded live, on a real file, a real type-shape edit:

```
change      -> [TypeShapeChanged ("Order", Scoped "order-store")]
subject     -> OneUnit "order-store"
build needed-> yes (Liveness.Unknown pays it)
```

And the safety direction, dogfooded:

```
mutable + scoped -> WholeWorker ["'counter' IS the app's live state;
                                a scoped restart would not reset it"]
undeclared type  -> Undeclared
two boundaries  -> Ambiguous ["store-a"; "store-b"]
```

## What is NOT claimed

- **A narrower BUILD is impossible.** `SessionBuild.runBuildAsync` is one
  `dotnet build` on one project, and F# is whole-assembly. A scoped restart
  that claimed a cheaper build would be refuted by the build system.
- **A scoped restart is not yet cheaper.** Width and cost are different
  questions. Today `Liveness.Unknown` pays the build, so behaviour is
  unchanged — and correctly so.
- **The holder is not in anyone's app yet.** Nothing is a `Cell<'a>` because
  nothing has opted in.

## The one thing that would make it cheaper — and why it is blocked

A boundary that can establish LIVENESS. The signature is in place
(`Liveness` travels with the restart), so establishing it is one value in one
place — `WorkerMain.fs`, where the live path currently says `Unknown`.

**And the obvious probe does not work, measured.** .NET exposes no per-type
instance count. Checked rather than assumed:

- `GC.GetGCMemoryInfo()` — total bytes, no per-type count
- `GC.GetGeneration()` — per-thread, no per-type count
- `AppDomain.GetAssemblies()` — types **LOADED**, not instances
- `GC.GetTotalMemory()` — total bytes

And the decisive check:

```fsharp
type NeverInstantiated = class end
// LOADED as a type : true
// instances ever made: 0
```

So any reflection-based probe reports "live" for a type nobody holds. That is
a lie, and it fails in the EXPENSIVE-LOOKING direction: it would report
liveness, pay a rebuild every time, and look like a working probe while
answering nothing. `Liveness.Unknown` is therefore the CORRECT answer today,
not a placeholder waiting to be filled in.

**What would actually answer it.** Not reflection — instrumentation. SageFs
already knows where values live for a *kept binding*: `HotReloadCore.AppHolds`
records which `MethodInfo` the app captured, and `LiveStateEmit.probeCode` asks
the app directly about a specific field. A liveness probe for a TYPE change
needs the same machinery pointed at a type rather than a binding: either the
holder owning the instances (steps 2–5, which have no caller yet), or a
runtime hook SageFs installs that counts constructions of a named type.

That is a real build, and it is the honest next piece. It is not started here
rather than half-built and claimed — and the note records WHY, so the next
person does not spend a day on the reflection probe that cannot work.

## The reachability question, settled by measurement

A user's `Run App` project references **only** `FSharp.Core`:

```
$ grep -E 'PackageReference|ProjectReference' samples/demos/SageFs.Samples.ConsoleTicker/*.fsproj
    <PackageReference Include="FSharp.Core" />
```

**A user now needs to write nothing at all.** `SessionBuild.runBuildAsync`
locates the running `SageFs.Core.dll`, writes a `.targets` that adds the
`Reference`, and passes it to `dotnet build` as
`-p:CustomAfterMicrosoftCommonTargets=<temp>.targets`.

Proven end to end through the real product path — a project with **zero**
`Reference` elements, no `SageFsCoreAssembly` property, using the holder API:

```
BUILD OK: Build succeeded
read=0 live=HeldBy ["Order"]
bin/Debug/net11.0/SageFs.Core.dll    <- 7,495,168 bytes, copied
/tmp/sagefs-inject-*.targets         <- gone; cleanup ran
```

And again on the **real ConsoleTicker** with both fallbacks removed — the
fsproj `Reference` block and the `SageFsCoreAssembly` property — through
`SessionBuild.runBuildAsync` itself:

```
BUILD OK: Build succeeded
12:57:35.4  #1  SageFs keeps ticking
12:57:35.5  #2  SageFs keeps ticking
```

So neither fallback is load-bearing for the Run App path. "It compiled" and
"it runs" are separate claims, and only the second proves `Private=true` —
without it the app dies at RUN time with a missing assembly, long after a green
build.

## What is still NOT wired — measured, not estimated

Three modules are built, tested and DST'd, and have **zero production callers**:

```
$ for m in HolderRewrite TypeShapeMigration TranslationValidation; do
    echo "$m: $(grep -rl "$m" --include='*.fs' SageFs/ SageFs.Host/ | grep -v obj | wc -l) production files"
  done
HolderRewrite: 0 production files
TypeShapeMigration: 0 production files
TranslationValidation: 0 production files
```

A decision function with no live caller is a lookup nobody performs. They are
not dead code to be deleted — they are the next seam.

### The seam, precisely

`RestartAction` has two cases today:

```fsharp
| RespawnOnly of because: string
| RebuildProject of because: string
```

The missing third is **migrate the value**: `TypeShapeMigration.decideRecord`
already answers "can this old value be carried into the new shape?", and it
answers it as a DU with a refusal per field rather than a partial migration. A
`Migration<_, _>` that is not a migration is an error, not a degraded success.

So the shape of the next piece is:

1. When liveness says a live value of the old shape EXISTS, build the old and
   new `RecordShape` from the type's own definition.
2. Ask `decideRecord`. If it migrates, take a **third** action — rewrite the
   value in place and respawn, with no `dotnet build` at all.
3. If it refuses, fall to today's `RebuildProject`, carrying the refusal as the
   `because` so the user learns *which field* was undecidable rather than that
   something was.
4. Run every use site of the rewritten binding through `TranslationValidation`
   before believing the result — that is what `Undecidable` is for, and it is
   the check that makes a migration safe rather than merely possible.

The hard part is step 1, and it is honest to say so: SageFs does not currently
retain the old shape of a type across an edit, so the old `RecordShape` has to
come from the last known parse. That is a real build, not a wiring exercise.

### The AST, measured — and where the cycle stopped

Reaching a field's KIND from source took several build cycles, so the findings
are recorded rather than left for the next person to rediscover. All of it
measured with a temporary reflection probe inside the test tier, which is the
only context where `Fantomas.FCS` is in scope (`check_fsharp_code` and a bare
`dotnet fsi` both lack it):

| Question | Measured answer |
|---|---|
| namespace | `Fantomas.FCS.Syntax`, NOT `FSharp.Compiler.Ast` |
| `SynField` members | `idOpt: FSharpOption<FSharpOption<Ident>>`, `fieldType: SynType`, plus `isMutable`, `isStatic`, `attributes` |
| `SynField` destructuring | takes **ONE** binding; naming two members in the pattern is a syntax error |
| `SynField` case field | **no public constructor and no nameable case field** — `GetConstructors()` returns empty |
| `idOpt` element | a bare `Ident`, NOT a `SynIdent` (the compiler says so) |
| `SynType` | only `Is*` predicates; a type's NAME lives on `typeInfo` |

So the declared type *is* on the node as `fieldType: SynType` — the information
is present — but it cannot be reached from a pattern that names it, because the
DU's single case field has no discoverable name. That is why every field is
`Undecidable`.

**This is the correct place to stop, and stopping is safe.** An undecidable
field refuses the migration, so the answer falls back to the rebuild we already
do — no wrong-typed value can reach a live object. The alternative was a guess,
and the guess is what this whole refactor exists to remove.

The next step is a decision, not more archaeology: either Fantomas gains a
readable accessor (a small change in Fantomas's own AST, not a private-field
hack from here), or the shape comes from a *compiled* symbol rather than the
text AST. Both are worth doing deliberately. Neither is worth a
reflection-into-private-fields hack in the middle of a hot-reload path.

## Gate parallel 2 is better, not yet sufficient

At `SAGEFS_TIER_PARALLEL=5` seven host tests errored as timeouts. At 2 the first
run was all-green. At 2 on a *busier* run, three tiers went red again — and that
second run is the more informative one, because it separates the two classes:

| Tier | Symptom | Isolated re-run |
|---|---|---|
| `--integration-host[1/5]` | 1 errored, hot-reload keep-tiering | a *different* test errors in isolation |
| `--integration-browser` | 1 errored | **variable: 1 run 1 failed + 1 errored, next run clean** |
| `default` | 1 **failed** — `recordEval` scaled 11.0x for 10x history (budget 8x) | **5 of 5 passed** |

`--integration-host` naming a *different* failing test on each run is the
signature of contention, and the repo already documents it for the
live-testing tests. `--integration-browser` is variable, so a single verdict
there is not evidence of anything — only the rate is.

`recordEval` is a **wall-clock budget**, and it passed 5 of 5 in isolation
against a gate run that failed it. That is not proof of innocence, so the
isolation is being pushed to 6+ runs and its source read; a timing budget on a
loaded box is exactly the thing that produces a false red, and equally exactly
the thing that hides a real regression.

What the budget test is actually guarding is worth stating plainly: an
`O(n²)` rescan of eval scope on every eval, which is invisible at small history
and catastrophic at 10x. It is a real invariant with a real failure mode.

## The red gate at parallel 2, measured rather than excused

Three tiers went red while verification ran in parallel. Isolating each on the
machine as it actually stood:

| Tier | Isolated result | Rate |
|---|---|---|
| `default` perf budget (`recordEval` scaled 11.0x, budget 8x) | **11 passed, 0 failed** | 11/11 |
| `--integration-browser` | **3 clean, 36/36 each** | 0/3 red |
| `--integration-host[1/5]` keep-tiering | not reproduced; a *different* test errors in isolation | **unresolved** |

Load average during all of it: **18.6 / 24.6 / 23.4, peaking at 43.0** on 16
logical cores — 1.4x to 2.7x oversubscribed for the entire window, because the
verification was competing with the gate. The runner was loaded *by
construction*, which is the condition a wall-clock budget is least able to
survive.

### What the perf budget really measures

`SageFs.Tests/PerfTests.fs:62-71` — ceiling 8x against a stated baseline of
~1.1x. Wall-clock via `Stopwatch.GetTimestamp`, but deliberately hardened:
min-of-15 after 2 warmups, with a forced `GC.Collect` and
`WaitForPendingFinalizers` before each timed block, so GC pauses and a loaded
machine only ever ADD time and the fastest observed run is closest to real cost.

So the margin is **7.3x**, and an observed 11.0x is close enough to 8x to trip
it and close enough to 1.1x to be noise — the same test being marginal on a
loaded box, twice over. It is not a quadratic signal: the guard separates an
O(n) rescan (~10x) from the O(n^2) it watches for (~100x).

`recordShapeOf` cannot be implicated on the merits either: **zero production
callers**, and no reference from `FeatureHooks.recordEval` or anything it
reaches, so `recordEval` cannot execute the new code at all.

### The one left unresolved, and why

`--integration-host[1/5]`'s keep-tiering errored in **25.0s**, and that duration
is arithmetically inconsistent with the test. Its only failure path is
`saveWithinBudget (TimeSpan.FromSeconds 150.0)`, which cannot expire before
150s; its other guards are 180s and 120s. So 25.0s is not its assertion timing
out — it is harness or host-spawn startup. Called **unresolved** rather than
exempted, because it did not reproduce. The test also skips itself when the
watch never lapses, on documented grounds that some runs never get there.

Recorded unresolved deliberately: a measurement that exonerates a change and a
measurement that never happened are different, and only the first is evidence.

## Dogfooded on the 0.6.835 daemon, not in a test fixture

The decision evaluated in a live session on the installed daemon, over its real
MCP transport:

```
PROBE held      rebuild=true     evidence: something holds it
PROBE empty     rebuild=false    evidence: nothing does  -> SKIP the build
PROBE unchecked rebuild=true     not evidence: pay
```

Line two is the entire point of `LiveCount`. Before it, "a registry was asked
and found nothing" and "no registry was consulted" were both `None`, and
`rebuilds` collapsed the answer to a bool — so the two claims that must differ
most were the two that could not.

### Three mistakes that were the DRIVER's, not the product's

Worth recording, because the cheapest explanation of each was a product bug and
none of them was:

1. **`Uninitialized` was a real state, not an error.** The tool refused and
   named exactly what *was* available. That is the recovery-from-a-state fix
   working: escaping it never required leaving it first.
2. **Tool arguments are snake_case.** `working_directory`, not
   `workingDirectory`. The tool answered with the exact missing parameter name.
3. **The transport replies as SSE.** A JSON-RPC result arrives on a `data:`
   line, so searching the raw body finds nothing *even on success* — a driver
   that reports that as a failure is reporting a failure that is not one.

Each was found by reading what the daemon actually said rather than inferring
from a boolean. A driver that prints "not ready" without saying which state it
saw cannot tell warmup from breakage.

### The mechanism, and what does NOT work

| Approach | Result |
|---|---|
| `-p:CustomAfterMicrosoftCommonTargets=<file>` | **works** — the file can ADD a `Reference` |
| `-p:ReferencePath=<dir>` | **does not work** — it only tells MSBuild where to SEARCH for references that already exist, so it can never introduce one |
| `<ProjectReference>` to `SageFs.Core` | fails with NU1605 — pulls a newer `FSharp.Core` transitively, read as a downgrade, and the repo treats warnings as errors |

The `ReferencePath` row is the load-bearing one. I assumed the property form
would work before measuring, and it produced a clean FS0039 on `HolderRegistry`
that looked exactly like a missing assembly. That is why the injection is a
`.targets` file and not a property.

### Where the assembly is found, and where it must not be

`typeof<HolderRegistry>.Assembly.Location` — **not** `AppContext.BaseDirectory`.
The latter is deliberately overridden to the *user project's* build output
inside the FSI host, so it points at the app rather than at the tool: a lookup
that would have found the wrong DLL and looked correct.

### The one case that still needs a fallback

CI builds the samples with a raw `dotnet build`
(`ci-pipeline.fsx`, "build samples for integration suites"), which is not a
SageFs build and therefore gets no injection. The ConsoleTicker keeps a
`Reference` for that one path, resolved from a `SageFsCoreAssembly` property in
the repo-root `Directory.Build.props` and guarded by `Exists` so a machine that
has never built Core simply gets no `Reference`. That fallback is a CI
convenience, not something a user needs.

## Gate parallelism, and the one tier that is not concurrency-tolerant

`SAGEFS_TIER_PARALLEL` overrides the default. The default is derived, not
constant: `build/TierPlan.fs` returns `max 1 (min 6 (cores / 3))`, which is **5
on a 16-core machine**.

At parallelism 5 a gate run produced 7 errored host-integration tests. All were
timeouts, 0 were failures, and the evidence that they were contention rather
than defects:

- every affected test's net11.0 twin passed in the SAME run, while the net10.0
  twin took 75–79s against a 60s budget;
- the same tests pass serially and at a clean baseline commit;
- the repo already documents it — `SageFs.Tests/HttpApiIntegrationTests.fs`
  says the worker's test proxy "does not become available inside the wait" under
  contention and "passes alone in ~10s".

**At `SAGEFS_TIER_PARALLEL=2` all 11 tiers were `Trusted`**, 0 failed, 0
errored, and the previously-erroring test ran in 12.3s against its 60s budget.

One caveat measured rather than assumed: `--integration-browser` is the
sensitive tier. It errored once at parallelism 2 while ten other tiers passed,
on a 15s UI-wait assertion, then passed on 4 consecutive runs on an idle
machine. A single green run of one tier is not evidence that the tier is
sound — only the repetition is. So: **run the gate at `SAGEFS_TIER_PARALLEL=2`
or lower**, and treat a lone browser-tier error as contention until a repeat
run says otherwise.

So the earlier claim in this file — that the opt-in design is unreachable from
user code — **was wrong**, and is corrected here rather than quietly dropped.
`SageFs.Core` is a `Library`, so a user project can reference it; what is
missing is only the ergonomic step of the reference being added for them.

**A correction worth recording.** The first version of that app printed
`ticks: 1 1 1` and it looked like a holder defect. It was not: the app *read*
the cell and never *wrote* it. Proved by asking the product in a live SageFs
session before touching the code — writes then reads through the same cell give
`[1; 2; 3]`. SageFs was right and the test app was wrong.

### What still needs doing

- Add the `SageFs.Core` reference to a Run App project automatically, or ship
  a small `SageFs.Runtime` package carrying just `Holder`, `HolderRegistry`
  and `RegisteredHolder`. Until then a user must hand-write a `HintPath`.

## What each piece is worth, and its reach

| Piece | Reachable from a save? |
|---|---|
| granular restart, attribution, subject, `RestartPlan` | yes |
| build-cost decision (`LiveCount`) | yes — answers `Unconsulted` unless an app registers a holder |
| `Holder` / `HolderRegistry` / `RegisteredHolder` | yes, by reference; ergonomics still missing |

## How to verify

```bash
# the decision, proven in a live SageFs session
dotnet fsi .roastscratch/eval.fsx file:.roastscratch/proof2.fsx
#   held by a boundary    rebuild=true    evidence: something holds it
#   asked, found nothing  rebuild=false   evidence: nothing does -> SKIP
#   never consulted       rebuild=true    not evidence: pay
#   source failed         rebuild=true    not evidence: pay

# a real external app
cd /tmp/holdertest && dotnet run

# the full default tier
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --summary
```
