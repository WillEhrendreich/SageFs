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
