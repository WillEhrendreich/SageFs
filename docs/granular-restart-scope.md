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

Nothing adds a SageFs reference, so a sample cannot simply use the holder API
as it stands. **But the API is reachable** — proven by a real external project
outside this repo, compiled and run against the built `SageFs.Core.dll`:

```xml
<Reference Include="SageFs.Core">
  <HintPath>.../SageFs.Core/bin/Release/net10.0/SageFs.Core.dll</HintPath>
</Reference>
```

```fsharp
open SageFs
let registry = HolderRegistry.New ()
let ticks = RegisteredHolder.holdRegistered registry "TickerState" 0
let cell = RegisteredHolder.cellOf ticks
let next () =
  let current = Holder.read cell
  cell.Value.Value <- current + 1
  current + 1
printfn "ticks: %d %d %d" (next ()) (next ()) (next ())
printfn "the restart sees: %A" (registry.LiveCountOf "TickerState")
```

```
ticks: 1 2 3
read back: 3
the restart sees: HeldBy ["TickerState"]
```

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
