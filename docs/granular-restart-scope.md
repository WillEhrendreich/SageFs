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
docs: the perf guard's estimator was investigated, and the fix was NOT landed

The `recordEval` scaling guard red a gate at 11.0x against an 8x ceiling while
passing 11/11 in isolation, and it has now cost two gate runs. I measured
whether the harness itself is the cause rather than assuming it either way.

## The hypothesis

`PerfBudget.scalingRatio` times all `small` iterations, then all `large`. The
large batch runs ~10x longer per iteration, so under load it has a far bigger
window to catch a bad scheduling moment. min-of-15 reduces that but cannot
equalise it, so load DRIFT between the two batches biases the ratio upward —
the observed failure direction.

The candidate fix interleaves the two workloads so each pair is measured under
the same machine conditions.

## Measured, under deliberate load (8 spinners on 16 cores)

Run 1 — looked conclusive:

```
CORES 16
flat/sequential    mean=0.92 sd=0.52 max=1.98
flat/interleaved   mean=0.84 sd=0.18 max=1.01
```

A 3x variance reduction and a worst case of 1.01 against 1.98. Good enough to
have shipped on the spot.

Run 2 — same script, same load, seconds apart:

```
flat/sequential    mean=1.21 sd=0.37 min=0.94 max=1.96
flat/interleaved   mean=1.26 sd=0.39 min=0.93 max=1.92
```

**No difference. The result did not replicate.**

## So the fix is not landed

Interleaving would have replaced a gate that occasionally reads 11x with one that
reads 1.2x on the same machine state. It looks like a strict improvement on the
first sample and like a no-op on the second, which means the thing being
measured is dominated by run-to-run machine state rather than by the estimator's
structure. Changing the gate on that evidence would be replacing a real
measurement with a different real measurement and calling it a fix.

**And the near-miss is the point worth recording.** A single 6-sample run said
3x. I nearly took it. A timing claim needs enough repetitions that a repeat can
disagree with it, and this is the second time in this work that one sample
looked decisive and the next one did not — the first was the `ticks: 1 1 1`
counter, which was my test app's bug, not the product's.

## What is actually established

- The guard's own comments say the 8x ceiling is a portability margin over a
  ~1.1x baseline, and that it is intended to separate an O(n) rescan (~10x)
  from the O(n^2) it watches for (~100x). 11.0x sits in the gap, and the
  distinction the guard exists to make survives it.
- `minMs` is min-of-15 after 2 warmups with a forced full GC and
  `WaitForPendingFinalizers` before every timed block. That is already the right
  estimator for a busy runner; it was not the loose measurement it looked like.
- The real lever is not the estimator, it is WHEN this runs. A wall-clock budget
  in a tier that shares a 16-core box with four other tiers will occasionally
  read high, and the honest options are to move the perf tier out of the
  concurrent set, or to accept that it needs a quiet machine. Neither is a
  harness rewrite, and both are decisions rather than measurements.

`recordShapeOf` remains unexcluded by argument, not only by measurement: it has
zero production callers and no reference from `FeatureHooks.recordEval`.
feat(reload): read a record shape from the COMPILED type, not the text AST

Revises the answer recorded one commit ago. The text AST is a dead end — the
information is on the node and unreachable — and the compiled type carries the
same information through a supported public API the repo ALREADY uses at
SageFs.Core/Features/LiveValueTree.fs:161.

  SHAPE Order isRecord=true
  SHAPE   Id       Int32     -> IntField
  SHAPE   Name     String    -> StringField
  SHAPE   Paid     Boolean   -> BoolField
  SHAPE   Payload  Byte[]    -> Undecidable(Byte[])
  SHAPE   Meta     FSharpMap -> Undecidable(FSharpMap)
  READ live Order -> [|7; "x"; true|]

Two things this buys, both measured:

1. DECIDABLE KINDS. The text route produced `Undecidable` for every field,
   because `SynField` exposes `fieldType: SynType` but has no nameable case
   field. Here the kind is a real `System.Type`, so `int`/`string`/`bool` are
   decidable and everything else is still `Undecidable` — and refuses, which is
   the right direction for a `byte[]` or a `Map`.

2. THE OLD VALUE, not just its description. `PreComputeRecordReader` reads a
   live instance, so a migration can CARRY state rather than only describe it.
   The text AST could never do this at all; it is not a lesser route, it is a
   different capability.

The `byte[]` and `Map` rows are the point, not an aside: they show the refusal
is still discriminating rather than a blanket "everything is undecidable", and a
field that cannot be carried refuses the whole migration instead of silently
losing its value.
feat(reload): a migration is now DECIDABLE, not always a refusal

`decideRecord` has existed, tested and DST'd, and could never succeed: every
field came back `Undecidable`, because the text-AST route cannot reach a field's
declared type (`SynField` exposes `fieldType: SynType` but has no nameable case
field — measured, one commit ago). A decision that can only ever return
`Refused` is a stub wearing a decision's clothes.

`compiledShapeOf` reads the same information from the COMPILED type, through
`FSharpType.GetRecordFields` — a supported public API the repo already uses at
`LiveValueTree.fs:161`. The rule set is deliberately the same three kinds, so
the two producers cannot drift into disagreeing about what is carryable.

Measured, before writing it:

  SHAPE Order isRecord=true
  SHAPE   Id       Int32     -> IntField
  SHAPE   Name     String    -> StringField
  SHAPE   Paid     Boolean   -> BoolField
  SHAPE   Payload  Byte[]    -> Undecidable(Byte[])
  SHAPE   Meta     FSharpMap -> Undecidable(FSharpMap)
  READ live Order -> [|7; "x"; true|]

Two things this buys, and the second is the larger one:

1. DECIDABLE KINDS, so `decideRecord` over two identical shapes now returns
   `Carried` — asserted directly, because "the path can succeed" is the whole
   claim and a test that only checked the refusal would not have proven it.

2. THE OLD VALUE, not just its description. `compiledFieldsOf` reads a live
   instance, so a migration can CARRY state. The text AST could never do this
   at all: it is not a lesser route, it is a different capability, and that is
   why the AST was the wrong route to spend more cycles on.

`compiledShapeOf` returns `None` for a non-record rather than an empty shape,
because an empty field list is indistinguishable from a record with no fields
and would be read as "nothing to carry" rather than "wrong question".

The `byte[]` and `Map` rows are the load-bearing part of the test, not an
aside: they prove the refusal is DISCRIMINATING. A test asserting only that
undecidable fields refuse would also pass against a blanket
`Undecidable "unsupported"`, which is a stub and must not be able to.

Six tests. The full default tier: 9844 registered, 9844 ran, 0 failed,
verdict=Trusted.

One test was wrong on first run and the correction is the instructive part:
it compared whole `FieldKind` values for equality, which fails because
`Undecidable` carries a reason string and the two routes legitimately word it
differently. The check is over the CASE, not the value.
feat(reload): a third restart outcome — carry the live value, skip the build

`RestartAction` had two cases and neither is the one a live value deserves.
`RespawnOnly` is right when NOTHING holds the old shape. `RebuildProject` is
right when a live value cannot be carried. Neither can express "a live value
exists, and every field of it can be carried" — which needs no build AND must
not lose the value.

`MigrateAndRespawn` is that third outcome, and it is deliberately NOT folded
into `RespawnOnly`: collapsing them would make a restart that keeps live state
indistinguishable from one that discards it, which is exactly the difference a
user cannot see and would have to discover by losing state.

`decideFromLivenessAndMigration` takes BOTH facts rather than merging them,
because merging is how the cheap answer wins. A caller that reports "worth
carrying" while also reporting "nothing is live" gets the honest answer, not the
cheaper one it asked for — asserted directly, with all three verdicts paired
against an empty liveness answer.

The safety properties are the ordering of the cases:

- a live value that CANNOT be carried pays the build, and the message carries
  the refusal so the user learns WHICH field blocked it;
- a live value with NO verdict about carrying ALSO pays, because absence of
  evidence is not evidence. This is the two-sided invariant: a rule that only
  refuses when told to is satisfied by an implementation that never carries
  anything, so the un-answered case must be priced too;
- liveness that was never established, or could not answer, pays the build
  REGARDLESS of a carryable verdict.

The compiler found both non-exhaustive matches when the case was added —
`AppRunOrchestration.fs` and a pre-existing `RestartCostTests` — which is the
DU doing its job. Both are now explicit, and both say why.

In `AppRunOrchestration` the new case falls back to a REBUILD, and the reason is
worth stating: no caller supplies a `MigrationWorth` today, so the action is
never produced on the live path. Rebuilding loses the value correctly; treating
it as a respawn would drop it silently. The branch exists so that when a caller
DOES produce the action, the compiler points at that line rather than the
failure surfacing as discarded state.

`decideFromLiveCount` is untouched and asserted unchanged, so no shipped
behaviour moved — the addition is a new function, not a rewrite wearing one.

7 new tests, 7 passed. Full default tier: 9851 registered, 9851 ran, 0 failed,
verdict=Trusted.
## What is still NOT wired — the boundary is now measured, not guessed

`MigrationPlan.decide` is the producer, and it is proven reachable end to end:

```
PROBE shape  = Some { TypeName = "Order"; Fields = [Id: IntField; Name: StringField; Paid: BoolField] }
PROBE fields = Some [|7; "x"; true|]
PROBE VERDICT: worth carrying
```

and in the test tier, 9/9, including `MigrateAndRespawn` for a carryable value
and `RebuildProject` for an uncarryable one.

**The live path still cannot call it, and that is a structural fact rather than
an oversight.** `WorkerMain.fs:1030-1032` has the registry and nothing else:

```fsharp
let liveCount =
  match changedTypeName with
  | Some typeName -> holderRegistry.LiveCountOf typeName
  | None -> SageFs.LiveCount.Unconsulted "this change is not a type change"
```

and the registry cannot hand back a value:

```
LiveCell = Holds:String | Id:Int64 | Superseded:Boolean
Cell     = Value:FSharpRef`1
```

A migration needs the old VALUE. The only holder of that value is the `Cell`,
and the registry never sees a `Cell` — it records an id, a type NAME and a
superseded flag. So the value cannot be recovered from the registry by any
change confined to the decision layer; it has to be captured where the value
is held.

That is a real design decision rather than a wiring chore, and there are two
honest shapes:

1. **`HolderRegistry` gains a value-carrying entry.** `holdRegistered` already
   receives the value; storing `obj` alongside the id makes the registry able to
   answer "is this carryable?" directly. Cost: the registry becomes a strong
   reference to live state, which is exactly what a "liveness source" should
   NOT be — it would keep an object alive that the app has otherwise released,
   turning an honest `Release` into a lie.

2. **The app's own boundary declares a migration hook.** A boundary already
   exists (`RestartBoundaries.Boundary`) and already says "I hold instances of
   this type". Giving it an optional `migrate : obj -> Type -> MigrationWorth`
   puts the value where it is already in scope — the app — and keeps the
   registry a pure index. Cost: the user writes a small function, and a
   boundary that does not supply one migrates nothing.

**The second is the better shape, and the reason is the first's cost.** A
liveness source that holds strong references to live state will report
`HeldBy` for an object nothing else holds, which is the same class of bug as
the reflection probe this work already rejected: it looks live because we are
holding it, not because anything uses it. The measure has to be a by-product of
the owner, not a shadow copy the measure keeps alive.

So the next piece is: `Boundary` gains an optional migration hook, and the live
path calls `MigrationPlan.decide` with whatever the boundary supplies —
`NoValueToMigrate` when it supplies nothing, which is the honest answer for every
boundary today.
## The boundary subsystem is built, tested, and entirely unwired

Measured, not estimated:

```
restartReasonWithBoundary: 0 production refs
RestartBoundaries:         0 production refs
Registry.For:              0 production refs
declaredBoundary:          0 production refs
```

So the state of the whole granular-restart effort, honestly:

| Piece | Reachable from a save? |
|---|---|
| granular restart, attribution, subject, `RestartPlan` | **yes** |
| build-cost decision (`LiveCount`, `RestartAction`) | **yes** |
| `Holder` / `HolderRegistry` / `RegisteredHolder` | **yes** — ConsoleTicker uses it |
| `RestartBoundaries.Registry` | no — nothing constructs one |
| `restartReasonWithBoundary` | no — nothing calls it |
| `MigrateAndRespawn` / `MigrationPlan` | no — no live caller supplies a value |

`WorkerMain.fs:1020` gets a type's boundary from the HOLDER REGISTRY
(`holderRegistry.LiveCountOf typeName`), not from a declared boundary. So the
two mechanisms are parallel: the holder registry counts what the app actually
did, while a declared boundary is the opt-in a user writes to say what should
be restartable in principle.

### Why the boundary registry is not wired, and what it would take

`Registry.For` needs a `WorkerProtocol.SessionId`. The worker has one — it is
`AppRunner`'s — so the registry is constructible there. What is missing is the
DECISION to construct it and the threading of a `MigrationWorth` from a
boundary's hook into `requireRestart`, which currently takes `(first, rest,
subject, liveness)`.

That is a real seam, not a one-liner:

1. the worker constructs `RestartBoundaries.Registry.For sessionId` once, and
   the app can declare boundaries into it (that API needs surfacing through MCP
   or a startup hook — today there is no way for an APP to declare one);
2. `requireRestart` gains a `migrationWorth` parameter, alongside `liveness`
   rather than folded into it, because they answer different questions;
3. `AppRunState.RestartRequired` and `RunEnd.RebuildForChanges` carry it, the
   same way they carry `liveness` now;
4. `AppRunOrchestration.restartForChanges` uses
   `decideFromLivenessAndMigration` instead of `decideFromLiveCount`, and the
   `MigrateAndRespawn` arm it already has stops being a fallback.

Step 1 is the one with a product decision in it: **how does an app declare a
boundary?** The holder registry needs no declaration — the app opts in by
holding something. A boundary currently needs an explicit call, and there is
no path for a user's app to make one. Until there is, the honest state is what
the table says.
## The liveness registry the restart reads is not the one the app writes to

Measured, and it invalidates a claim this document has been making.

    HolderRegistry statics = [ "New" ]          <- only a constructor
    WorkerMain.fs:722   let holderRegistry = SageFs.HolderRegistry.New ()
    Program.fs:47       let registry        = SageFs.HolderRegistry.New ()
    WorkerMain.fs:1080  holderRegistry.LiveCountOf typeName

The app runs **in-process** — `SageFs.Host/AppRunner.fs:197` is
`Assembly.LoadFrom target` followed by an entry-point invoke, not a process
launch. So both registries live in the SAME process, in the SAME AppDomain,
and they are still **two unrelated objects**: `HolderRegistry` has no static or
ambient accessor, only `New`.

Which means `holderRegistry.LiveCountOf typeName` at the restart site is
reading a registry the app never touched. It reports `0` for every type, always.
So:

- `LiveCount.HeldByNothing` is produced for every live holder in a real app, and
- the restart takes `RespawnOnly` — a cheap action — on the strength of a
  registry nothing wrote to.

**That is the most expensive defect in this whole series, and it is invisible
from the outside**: it fails in the CHEAP direction, a stale live value survives
a restart that was told nothing was alive, and every test passes because the
tests construct a registry, register into it, and read it back from the SAME
instance. The isolation tests make it look deliberate — "one app's cell cannot
make another's restart pay" — while the real behaviour is "no app's cell can make
any restart pay, ever".

### Why the per-registry design was right, and what that implies

Per-registry isolation is the correct property and should not be traded away: it
is what stops one app's state from narrowing another app's restart. The bug is
not the design, it is that **the worker holds a registry nothing can write to**.

The fix is the same ambient handle pattern the app already uses for holders, and
it has to be a handle the APP obtains rather than one SageFs guesses at:

- `HolderRegistry` gains a per-process `Current`, set by the worker when it
  starts the app, and `RegisteredHolder`/`holdRegistered` register into
  `Current` when the app gives no registry of its own;
- a boundary registry follows the same rule, which is what makes a DECLARED
  boundary reachable at all — today the app has no way to declare one, and this
  is the channel that would let it.

The moment that exists, `LiveCount` stops being decorative. Until then, the
honest reading of the live path is `Unconsulted`, not `HeldByNothing` — and
that distinction is the entire reason `LiveCount` was built.
## What a migration can and cannot do at a type change

Measured from the shapes rather than assumed, because this decides whether the
`MigrateAndRespawn` action can ever fire for a real edit.

`decideRecord` takes `(oldShape, newShape)`. The restart site has:

    WorkerMain.fs:1034   ReloadChange.TypeChanged typeName
    LiveCell            = { Id; Holds: string; Superseded: bool }

So the question is what the old and new shapes can be, at the moment a type
changes.

### The new shape does not exist yet

The app is running the OLD assembly, and the new one is what a `dotnet build`
would produce. Before that build runs there is no `System.Type` for the edited
record — only the source text. So `newShape` is **not available as a compiled
type at decision time**; it is at best derivable from the edited source, and
the text-AST route was measured to be a dead end for field KINDS (the one
question a migration needs answered).

That is not a small obstacle. It inverts the assumption the whole feature rests
on.

### The old shape is only a name away from useless

`LiveCell.Holds` is a string. `compiledShapeOf` takes a `System.Type`. A
`TypeShapeMigration.Field` is `{ Name: string; Kind: FieldKind }`, and nothing
on the cell can produce a `FieldKind` — the name "Order" carries no field list,
no defaults, and no kinds. Resolving a name back to a `System.Type` would mean
searching loaded assemblies, which is reflection dressed up: it reports what
happens to be LOADED, not what was there before the edit, and it is the same
move this work already rejected in `LiveValueTree`.

### The consequence, stated plainly

A type change cannot currently be migrated, and no amount of wiring the pieces
together changes that. The honest options are:

1. **Migrate on a shape comparison the source can answer.** Keep the record's
   declared field list in the BASELINE parse — `ReloadPlanning` already keeps a
   `FileDecls` per watched file and a `Header` per type decl — and diff the
   baseline shape against the new one. Field KINDS come from the compiled type
   for fields that exist in both, and from the AST only for a field that is
   genuinely new. That splits the question: a carried field's kind is known
   from the live value, and only an ADDED field needs the AST, which is the one
   case a text parse can answer (`fieldType: SynType` is unreadable, but a new
   field's *name* and whether the new definition gives it a default are not).

2. **Narrow the claim.** A migration is provably safe for the *carried* fields
   — same name, same kind, value copied across — and provably unsafe for a field
   whose kind changed or whose value cannot be produced. So the decidable
   subset is "same fields, same kinds, everything else refused", and the
   *baseline* is where the old shape must be kept.

The second is a correction to what this document has implied. The shape
producer (`compiledShapeOf`) is real, proven and correct — it reads a *live*
value's type. What is missing is the OLD shape of a type across an edit, and no
producer of that exists. `LiveCell.Holds: string` is not it.

### What this means for the current state

`MigrateAndRespawn` is reachable and correct as a DECISION, and it fires today
for a caller that supplies a live value and a target type that already exist.
What is not reachable is the case it was built for: a watched-file type edit
with a live value. That case still takes `RebuildProject`, and that is the
correct answer until the old shape is retained.

Retaining it is a small, well-scoped piece: `RestartAttribution.KnownUnit`
carries `{ Name; DeclaresType }` and is derived from `FileDecls`, which is
already retained per watched file. Extending it with the declared shape at the
point the parse happens would give the decision the old side for free — the
parse is already running, the data is already there, and nothing in the current
design depends on the old shape not existing.
## Both shapes exist, and the baseline is frozen

Following the previous commit's "the remaining work is the new side" turned up
something better and something worse, both measured.

### The good: the old shape is genuinely there

`WorkerMain.fs:1669-1671`

    match Features.ReloadPlanning.extractDecls (IO.File.ReadAllText source) with
    | Ok decls -> reloadBaselines.TryAdd(IO.Path.GetFullPath source, decls) |> ignore

`TryAdd`, not `AddOrUpdate` — the baseline is written ONCE, at build time, from
the source the assembly was BUILT FROM. So `KnownUnit.DeclaresFields`, derived
from those baselines, is the OLD shape. Correct, and the previous commit's
capture is capturing the right thing.

### The worse: `baselineIsTrustworthy` says every save after the first is unpatchable

    SageFs.Tests/HttpApiIntegrationTests.fs:1104  (and the code it tests)
    match baselineIsTrustworthy assemblyWriteTimeUtc sourceWriteTimeUtc with
    | false -> Log.warn "was modified after the build — it will be fully re-evaluated
                        (not diff-patched) on its next save. → Rebuild the project"

Saving a watched file makes it NEWER than the assembly, so
`baselineIsTrustworthy` is false, so the baseline is never (re)written, so it
keeps describing the pre-save source forever. That is self-consistent for
patching — a file newer than its build genuinely cannot be diff-patched — but
it means `reloadBaselines` is a snapshot of the BUILD, not a running record, and
the comment at `WorkerMain.fs:758` ("every save advances") is wrong about what
the code does.

It is not a correctness bug for the decision, because the whole thing fails
safe: an untrustworthy baseline means the change is re-evaluated rather than
patched, and a type no watched file declares yields `Everything`. But it IS why
the NEW shape is unreachable, and it is a comment that will mislead the next
reader exactly as it briefly misled me.

### So what a migration has, and what it still lacks

| side | available? | from |
|---|---|---|
| OLD shape | **yes** | `KnownUnit.DeclaresFields`, from the build-time baseline |
| NEW shape | **no** | would need a parse of the edited source at decision time |
| field KINDS | partly | compiled type for a live value; not from source |

The new side is obtainable without a build: `extractDecls` runs on any file in
under a millisecond, and the restart site already has the file path. So the
missing producer is a `declaredRecordFields` over the EDITED text — which is the
same function that just proved it can read a record's fields, pointed at the
new file rather than the baseline.

That is the whole remaining seam, and it is narrow: both shapes, both parsers,
and one missing call. What it will NOT give is a field KIND for a genuinely new
field, because the AST binding cannot reach one (measured). So a new field is
either defaulted-and-added or refused, decided by the caller — which is the
honest, safe answer and is already what `decideRecord` expects.
## The last seam: nothing supplies a `MigrationWorth`

Measured: `AppRunOrchestration.fs:174` calls `decideFromLiveCount`, which
ignores migration entirely, and `MigrationPlan.decide` — which does use it —
has no production caller. So `MigrateAndRespawn` is a real, tested, reachable-by-
construction outcome that no save can produce.

What the restart site already has, in `restartOrFallBack (fileName: string)`:

- `fileName` — the edited path, so the NEW shape is one `extractDecls` away
  (sub-millisecond, and the file is already on disk in its edited state);
- `reloadBaselines` — via `knownUnits()`, which now carries `DeclaresFields`,
  so the OLD shape is already there;
- `declaredBoundaryFor typeName` — the boundary, which may also carry a
  `Migrate` hook (a boundary that knows how to migrate outranks a name-only
  comparison, because the app is the only thing that can actually move the
  value);
- `holderRegistry` — the liveness answer.

So the producer is four lines and every input is present. The precedence it
should follow, and the reason each level wins:

1. **The boundary's own hook, if it has one.** The app holds the value; nothing
   else can move it, and a hook can do things names cannot (it knows the old
   object). A hook is a stronger claim than a name comparison and must not be
   second-guessed by it.
2. **The name comparison**, when there is no hook. Safe for carried and removed
   fields, refusing anything a name cannot settle.
3. **`NoValueToMigrate`**, when the boundary is silent — which is every
   boundary today. And silence is NOT "nothing is live": that claim belongs to
   `LiveCount`, and conflating them is how a cheap restart gets granted without
   evidence, which is the defect the whole `LiveCount` refactor exists to end.

The honest consequence: with no boundary hook anywhere, the name comparison runs
and answers the ADDED-AND-UNDEFAULTED case by refusing — which lands on today's
`RebuildProject`. So wiring this changes **no** behaviour for any existing user,
and turns into a real saving the moment an app supplies a hook. That is a
deliberate property: a change that can only ever pay a build today cannot make
anyone worse, and it is exercised end to end by the tests rather than by hope.

### The thing that is still missing afterwards

Even with a hook, carrying the value needs to WRITE the new value back into the
holder's `ref`. `HolderRewrite` has the `Swap<'before,'after>` machinery for
exactly that and is still unwired. So the migration is a decision and a plan
today, and the execution is the next seam — and it must not be wired before the
decision is, because a decision with no executor is what the last three commits
have been removing.
## HolderRewrite is NOT the migration executor, and wiring it would be wrong

Chased this because `HolderRewrite` is the last module with zero production
callers, and its name reads like the thing a migration needs. It is not.

`HolderRewrite.classify` takes `BindingFacts` — `IsModuleLevel`, `Inline`,
`LiteralAttr`, `AddressTaken`, `ByRefUsage`, `InQuotation`, `PinnedStorage`,
`Visibility`, `CopySemantics` — and answers whether a BINDING can be rewritten
in place. That is the question "may SageFs redefine this `let` in the running
process at all?", and the refusals are about that: a public field, an address
taken, copy semantics unverified.

A value migration is a different question: "given a live value laid out by the
old shape, can I produce one of the new shape?" Its inputs are two SHAPES and
the value — which is `TypeShapeMigration` plus `Holder.Cell`, both of which are
wired. None of `BindingFacts` is needed for it, and asking whether a field is
`PublicAcrossAssembly` before copying its value would refuse cases that are
perfectly safe, and permit nothing that `decideRecord` had not already decided.

So the three unwired modules are unwired for three DIFFERENT and individually
correct reasons, and it is worth writing that down rather than leaving a
tempting name to invite the next reader into the same wrong wiring:

| module | its question | status |
|---|---|---|
| `TranslationValidation` | "is every USE SITE of a rewritten binding safe?" | needs the rewrite it validates; wired after a rewrite exists |
| `HolderRewrite` | "may this binding be rewritten in place at all?" | a gate for a REWRITE, not a migration; nothing rewrites yet |
| `TypeShapeMigration` | "can this value cross into the new shape?" | **wired** — the name tier answers it |

The one that is genuinely still missing is the executor for a migration: given
`MigrateAndRespawn`, write the carried value back into the holder's `ref`. That
is a small, honest piece — `Holder.Cell` already exposes `Value: 'a ref`, and a
migration produces the new value — and it belongs at the point where the
restart actually acts, which is `SessionManager.RestartSession`. Doing it before
a decision reaches it would be a decision with an executor and no producer, which
is the shape this series has been removing in the other direction.
## The migration executor has a process boundary in the way

Chasing the executor — the piece that writes a carried value back into the
holder's `ref` — turned up a constraint that changes its shape, and it is worth
recording before anyone writes it.

Measured:

    HolderRegistry.New appears in:  SageFs.Host/WorkerMain.fs   (and nowhere else)
    SessionManagementOps.RestartSession:  SessionId -> RestartPlan -> ...   (daemon)

The app runs INSIDE the worker, so the live cell and the `ref` a migration must
write are in the WORKER's process. `RestartSession` is handled by
`SessionManager`, which runs in the DAEMON. So a `RestartPlan` carrying the
carried value — or a `System.Type` — would be carrying an object across a
process boundary, and the daemon cannot even name the app's types: they are
loaded in the worker, in an AppDomain SageFs does not own.

That is why `RestartPlan` is deliberately pure data with no type on it, and why
the worker publishes a `Migrate` hook on the BOUNDARY rather than handing the
daemon a function it could never call.

So the executor has to be worker-side, and the plan has to carry a COMMAND:

    RestartPlan.Migrate boundaryId

and the worker, on receiving it, does the work it is the only process that can:
re-read the live cell, build the new value, and write it back through the same
`ref` the app holds. The boundary id is the only thing that needs to cross,
because it names a thing the worker already has.

This also explains why the boundary HOOK, added earlier, is the right shape and
not merely one option among several: it runs in the worker, with the value in
hand. A migration that had to be planned in the daemon and executed there could
never work, and no amount of correct pure-domain code would change that.

### Which is why nothing should be wired until the worker path is built

The decision chain is now complete and correct end to end — `LiveCount` ->
`migrationWorthFor` -> `decideFromLivenessAndMigration` -> `RestartAction` — and
every step is tested. What it feeds is a `RestartSession` that cannot yet carry
the instruction across the boundary that matters.

Landing that is one message the daemon sends and one branch the worker takes,
and it is the next piece rather than a detail. Its correctness property is
already pinned by the tests above: whatever the plan says, an UNANSWERED
question costs a build, and only a positive `MigrateAndRespawn` with a hook's
`Carried` may skip one.
