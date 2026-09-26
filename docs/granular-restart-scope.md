# What `UnitScope` buys, and what it cannot buy yet

## Where the subject goes today

`RestartSubjectDecision` → `restartReasonAttributed` → `RestartAttribution`
(from the planner's own parse) → `AppRunState.RestartRequired` →
`RunEnd.RebuildForChanges` → `restartForChanges` → `RestartSession`. The
subject reaches the code that acts, not just the code that reports.

Dogfooded live in a real SageFs session, on a real file, a real type-shape edit:

```
type change -> [TypeShapeChanged ("Order", Scoped "Shop.Orders")]
subject      = OneUnit "Shop.Orders"
```

and for a live mutable alongside it:

```
mutable + scoped -> WholeWorker ["'counter' IS the app's live state;
                                a scoped restart would not reset it"]
```

## Why a scoped restart still rebuilds the whole project

Measured from the code, not assumed. `restartForChanges` calls
`ops.RestartSession sessionId (RestartPlan.Rebuild subject)`, and `Rebuild`
reaches `SessionManager`, which runs `runtime.RunBuildAsync` over
`SessionProjectTarget.paths session.Targets` — the whole project.

A type's shape is baked into the compiled assembly, so a respawn alone would
reconstruct the old bytes. There is no narrower build to ask for: there is one
project, not one registration per unit. So for a `TypeChanged`, rebuild is not
optional and `UnitScope` cannot make it cheaper.

What `UnitScope` *does* buy today:

1. The restart is attributed. "restart unit 'Shop.Orders'; everything else
   keeps running" instead of a message that said the same thing while the code
   restarted everything.
2. The safety direction is explicit and tested: one unscopable reason — an
   unattributable type, a live mutable, a value the app copied, a signature
   change — forces a whole-worker restart. Always.
3. The signature no longer blocks a narrower action. `RestartPlan` and
   `AppRunState.RestartRequired` carry the subject to the place that would
   implement one, so that work is no longer a signature change rippling out.

## The remaining step, stated precisely

A genuinely narrower restart needs a boundary SageFs can actually restart. Two
were considered and one is ruled out by the code:

- **A DI container or `IOptionsMonitor` per unit.** `Run App` launches the
  user's app as a PROCESS (`AppRunner`'s `ThreadOnly` / `HostHandle` are for
  SageFs's own dashboard apps, not the user's). There is no in-process
  per-unit object for SageFs to restart, because there is no in-process user
  app at all.
- **The holder (`SageFs.Core/Holder.fs`).** It provides `Cell<'a>` and a typed
  `Swap<'before, 'after>`, which is exactly the right SHAPE for a per-unit
  restart — but nothing in a user's app is a holder yet, because the holder
  requires the source rewrite (`HolderRewrite`) that step 4 gates and no
  boundary has opted in.

So the missing piece is a **registration** step: a user (or a framework
adapter) declaring "this unit is a holder boundary", after which
`UnitScope unit` has something to act on. `type-migration-direction.md` says
the same thing — the holder is "hooked to boundaries that already exist (DI
singletons, `IOptionsMonitor`, agents) before asking anyone to change source".

That is a design step, not a wiring fix, and it needs a decision about which
boundary is first. Claiming `UnitScope` is already a narrower restart would be
exactly the defect class this release has been removing: a scope in the
message with no consequence in the action.

## How to verify

```bash
# the decision, on real parsed source
dotnet fsi .roastscratch/eval.fsx file:.roastscratch/dogfood-need.fsx

# the full default tier
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --summary
```
