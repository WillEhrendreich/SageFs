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

A genuinely narrower restart needs a boundary SageFs can actually restart: a
DI container registration, an `IOptionsMonitor`, an agent mailbox, or a
separate process. `type-migration-direction.md` says this itself — the holder
is "hooked to boundaries that already exist (DI singletons, `IOptionsMonitor`,
agents) before asking anyone to change source".

Until such a boundary exists and is registered, `UnitScope` is a diagnosis, not
a cheaper restart. Claiming otherwise would be exactly the defect class this
work has been removing: a scope in the message with no consequence in the
action.

## How to verify

```bash
# the decision, on real parsed source
dotnet fsi .roastscratch/eval.fsx file:.roastscratch/dogfood-need.fsx

# the full default tier
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --summary
```
