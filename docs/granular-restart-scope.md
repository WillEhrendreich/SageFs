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

## The one thing that would make it cheaper

A boundary that can establish liveness. The signature is now in place
(`Liveness` travels with the restart), so establishing it is one value in one
place — `WorkerMain.fs`, where the live path currently says `Unknown` with its
reason.

The natural source: the running app is a process SageFs launched, so the
question "does anything hold an instance of this type" is answerable only from
inside it. That needs a probe — a tiny generated expression that reports the
live instances of a type, sent to the app's own session. That is the next
buildable piece, and it is deliberately not started here rather than
half-built and claimed.

## How to verify

```bash
# the decision, on real parsed source
dotnet fsi .roastscratch/eval.fsx file:.roastscratch/dogfood-rebuild.fsx
dotnet fsi .roastscratch/eval.fsx file:.roastscratch/dogfood-prec.fsx

# the full default tier
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --summary
```
