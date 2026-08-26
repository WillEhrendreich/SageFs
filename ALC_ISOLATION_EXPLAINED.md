# ALC Isolation, Explained

*What was broken, why it was broken, and how the quarantine fix makes it work.*

---

## 1. The architecture in one picture

SageFS runs two kinds of processes, but they are the **same binary**:

```
┌─────────────────────┐        ┌──────────────────────────────┐
│       daemon        │ spawns │            worker            │
│                     │───────▶│                              │
│  • dashboard UI     │        │  • hosts the FSI session     │
│  • MCP server       │        │  • evaluates your code       │
│  • session manager  │        │  • serves warmup, eval, etc. │
└─────────────────────┘        └──────────────────────────────┘
        │                                    │
        │   ONE tool package: everything ships together
        ▼                                    ▼
   uses Falco 5.2.0                  never uses Falco
   (renders the dashboard)           (only the daemon does)
```

Because it's one package, the **worker's directory also contains `Falco.dll`
(5.2.0), `OpenTelemetry.*`, `StarFederation.Datastar`**, etc. — even though the
worker code path never touches them. They're just *there*, because the daemon
needs them and they share a folder.

That "just being there" is the whole problem.

---

## 2. The problem: .NET resolves assemblies by *name*, and the worker's own copy wins

When your F# code says `open Falco`, the .NET runtime asks:

> "Who provides the assembly named `Falco`?"

and searches locations **in order**. The first hit wins, and once an assembly
with that name is loaded, it can **never** be replaced — .NET binds by simple
name forever after.

The worker's own directory is searched **before** the project's references:

```
FSI needs "Falco"
        │
        ▼
  ┌────────────────────────────────────────────────────┐
  │ 1. worker base dir     → SageFS's Falco 5.2.0      │ ◀── WINS (wrong one)
  │ 2. project bin (via -r:) → SageTech's Falco 6.0.0  │     never gets a chance
  └────────────────────────────────────────────────────┘
```

So for a project like SageTech (which uses **Falco 6.0.0-beta1**), this happened:

1. FSI starts, references the project's Falco 6.0.0-beta1 via `-r:`.
2. The first `open Falco` makes the runtime *probe* → finds SageFS's 5.2.0 in
   the worker's folder → **binds "Falco" to 5.2.0**.
3. Now `#load`-ing the project's source files (or referencing its built DLL)
   needs 6.0.0-beta1 → the runtime says:

```
Could not load file or assembly '...bin\Debug\Falco.dll'.
The located assembly's manifest definition does not match the
assembly reference. (0x80131040)
```

The session was **permanently unable to load any project that used a different
version of a library SageFS also ships** (Falco, OpenTelemetry, Datastar, ...).

```
worker dir            project bin            result
─────────             ────────────           ──────
Falco 5.2.0     +     Falco 6.0.0      =     💥 0x80131040
```

---

## 3. Why not just use a real AssemblyLoadContext?

That was the obvious first idea: run the FSI session in an isolated load
context that probes the project's bin *first*, so SageFS's copies never
interfere.

**It's not possible with the current FCS API.** `FsiEvaluationSession` (from
`FSharp.Compiler.Service`) always resolves assemblies through the process's
**default** load context. There is no public way to hand it a custom
`AssemblyLoadContext`.

We also tried every available knob to make the project's copy win:

| Attempt | Why it failed |
|---|---|
| `-r:` pointing at the project's Falco | compile-time reference only; runtime probe still hit the worker's copy first |
| `--lib:<project bin>` | affects `#r`-less resolution, not the runtime probe order |
| Pre-loading project assemblies before FSI starts | once `Falco` was already bound to 5.2.0, `LoadFrom` of 6.0.0-beta1 is a no-op |
| Explicit `#r @"...\Falco.dll"` | same binding problem — the name is already taken |

All of them failed for the same reason: **the worker's copy was already in the
probing path, and .NET binds names, not versions.**

---

## 4. The insight that fixed it

The worker **never uses** those assemblies. This isn't two parts of one process
genuinely fighting over a library — it's a library that *happens to sit in the
directory* shadowing the project's copy.

> We don't need to isolate the load context. We just need to **remove the
> competing copies from the probing path**.

If the worker's folder doesn't contain `Falco.dll`, the probe skips straight to
the project's bin, and the project's version wins *naturally* — the same end
result as a separate ALC, achieved by clearing the collision instead of
containing it.

---

## 5. The fix: quarantine

At **worker startup, before the FSI session is created**:

```
project bin DLL names
        │
        ▼
┌───────────────────────────────┐
│ for each name:                │
│   does the worker's folder    │
│   have a DLL with that name?  │
└───────────────────────────────┘
        │ yes
        ▼
  move it to  _sagefs_quarantine/
        │
        ▼
  EXCEPT: never move runtime-critical
  assemblies (FSharp.Core,
  FSharp.SystemTextJson) — the
  worker needs those itself
```

Before / after, from FSI's point of view:

```
BEFORE
  worker dir:  [Falco 5.2.0] [OpenTelemetry.*] [FSharp.Core]
  project bin: [Falco 6.0.0] [OpenTelemetry.*]
  probe → finds worker's copies first → 💥

AFTER
  worker dir:  [FSharp.Core]              ← FSharp.Core stays
  quarantine:  [Falco 5.2.0] [OpenTelemetry.*]
  project bin: [Falco 6.0.0] [OpenTelemetry.*]
  probe → worker dir has nothing matching → project's copies win → ✅
```

Key details:

- **Per-worker, so effectively per-project.** Each session gets its own worker
  process, so quarantining inside the worker isolates that session's project —
  the same effect a per-project ALC would give.
- **The daemon is unaffected.** It's a separate process with its own loaded
  copies (already in memory from rendering the dashboard).
- **Idempotent.** If the file is already quarantined (moved by an earlier
  worker), the next worker just skips it.

---

## 6. What it takes to actually open a project now

The full flow that works end-to-end:

```
1. daemon spawns worker for the project
2. worker quarantines its own copies of any DLL the project's bin also provides
3. FSI session starts with the project's references (bin DLLs + ASP.NET shared
   framework, deduped to one config, native/satellite DLLs filtered)
4. warmup opens namespaces from the PROJECT's assemblies → 74/74, 0 failures
5. startup profile references the project's built DLL
6. the app's code starts executing inside the session
```

For SageTech, step 6 now runs the app until it hits the *app's own*
configuration (its Postgres connection string) — an application concern, not an
assembly collision anymore:

```
BEFORE:  Could not load ...Falco.dll (0x80131040)     ← SageFS bug
AFTER:   Error building connection string: Missing
         required configuration value: ConnectionStrings:marten   ← app's own config
```

---

## 7. Supporting fixes that were part of the same journey

Getting to "the app runs in the session" took a few other fixes, all in the
project-loading path:

| Fix | What it solved |
|---|---|
| **Manual `.fsproj` parse fallback** | Ionide's `WorkspaceLoader` silently returns *0 projects* in compiled processes (works under FSI, not the worker). The fallback parses the project XML directly to get real source files. |
| **Bin reference collection** | The fallback gathers the project's built DLLs (single newest config, no satellite/native DLLs) plus ASP.NET shared-framework DLLs (deduped so the bin version wins), so FSI actually has the project's references. |
| **Shadow-copy change** | Dependency DLLs were being copied into a shadow dir, which broke FSI's `#load` transitive resolution. Now only the project's *own* assemblies are shadowed; references stay in place. |
| **`--langversion:preview`** | FSI defaults to an older language version than the SDK, which rejects modern constructs (`'T: not struct` errors). The fallback now passes the flag explicitly. |

---

## 8. Edge case: what if the worker itself needs a colliding assembly?

The quarantine has one hand-maintained list — `criticalNames` — protecting
`FSharp.Core.dll` and `FSharp.SystemTextJson.dll` from being moved. The
question: is that list the right mechanism, and what happens when the worker
genuinely depends on something a project also ships?

### The worker's directory contains two *kinds* of assemblies

They look identical (DLLs in the same folder) but behave differently:

```
worker's folder
├── USED by the worker's executed code
│     FSharp.Core, FSharp.SystemTextJson, FSharp.Compiler.Service,
│     Mono.Cecil, SQLite, ...
│     → loaded into the process when the worker starts
│     → quarantine breaks the worker        ← must protect
│
└── In the dependency closure but NEVER touched by worker code
      Falco, OpenTelemetry.*, StarFederation.Datastar, FsToolkit.ErrorHandling,
      NetMQ, SageTUI, ...
      → present because the DAEMON uses them (shared package)
      → the worker process never loads them
      → quarantine is exactly right          ← safe to move
```

The subtle part: **both kinds are in the worker's `SageFs.deps.json` closure**
(55 libraries — Falco 5.2.0, OpenTelemetry 1.15, SageTUI, NetMQ, ...). So "is it
in the dependency closure?" is NOT the discriminator. Falco is in the closure
and still safe to quarantine, because the worker's executed code never loads it.

### The rule that actually works

The right test is **"is it loaded in the worker's process at startup?"** —
not the static list, not the closure:

```
colliding = workerDir DLLs ∩ projectBin DLLs

for each dll in colliding:
    workerLoaded = AppDomain.GetAssemblies() contains dll's name?

    if workerLoaded:
        → KEEP worker's copy (project's version loses — log a clear warning)
    else:
        → quarantine (the worker provably hasn't needed it through startup)
```

This makes the protection *automatic* instead of listed:

| Assembly | Loaded at worker startup? | Verdict |
|---|---|---|
| FSharp.Core | ✅ yes | keep (protected automatically) |
| FSharp.SystemTextJson | ✅ yes | keep (protected automatically) |
| FSharp.Compiler.Service | ✅ yes | keep (protected automatically) |
| Falco 5.2.0 | ❌ no | quarantine ✅ (the fix) |
| OpenTelemetry.* | ❌ no | quarantine ✅ |
| StarFederation.Datastar | ❌ no | quarantine ✅ |
| FsToolkit.ErrorHandling | ❌ no | quarantine ✅ |
| *future dependency* | depends | handled correctly, no list to update |

The static `criticalNames` list in the current code is a *snapshot* of this
rule. It's correct today but has two failure modes:

- **False positive** — if the worker's own code starts *using* FsToolkit (or
  NetMQ, SageTUI...) at FSI-relevant times, it's not on the list, gets
  quarantined, and the worker breaks.
- **False negative** — a project pinning a different version of some
  worker-bundled-but-unused lib that ISN'T on the list hits `0x80131040` again.

### The honest residual limit

"Loaded at startup" runs *before* the actor + HTTP server start, so assemblies
the worker loads *later* (NetMQ when the transport spins up) aren't loaded yet
and would be wrongly quarantined if a project collided with them.

For that case there is **no clean in-process answer**: if the worker's own code
and the user's project both need different versions of the same assembly, .NET
binds one name to one version per process. The definitive fixes are:

1. **Ship the worker's own deps in a private subdirectory** (a real
   `AssemblyLoadContext`-style layout — the worker probes its own folder, FSI
   probes the project's) — the true long-term architecture.
2. **Run FSI in a separate process** with the project's deps as its world.

Both are bigger structural changes than the quarantine. The quarantine +
"loaded at startup" rule is the right *pragmatic* layer: it fixes the real
collisions (dashboard-only deps) with zero maintenance, and the residual case
(worker genuinely needs a different version than the project) is rare and
already *impossible to satisfy in-process* — so failing with a clear warning is
the honest behavior.

---

## 9. Design: thin F# supervisor + minimal FSI host

Settled direction: **F#-only**, a thin supervisor (borrowing Akka's *ideas* —
backoff, child restart — implemented directly, no Akka dependency), and the FSI
host extracted as its own process with the smallest possible closure. The
assembly guarantee comes from **process boundaries + enforced closure**, not
from an actor framework.

```
daemon (unchanged: dashboard, MCP, session manager)
  │
  └─ thin supervisor (F#)                 ← new: spawn/watch/restart/broker
       └─ FSI host process (F#)           ← new: the ONLY .NET probing world
            dir: minimal vetted closure
            project bin is the only other probe path
```

The supervisor owns the FSI host's lifecycle (spawn, file watch, port broker,
restart with backoff). The daemon keeps talking the existing protocol — to the
supervisor, which proxies it to the host (byte-for-byte, no protocol rewrite).

### Keeping the closure small

The FSI host's mandatory set is driven by the eval pipeline:

| Host-side (mandatory) | Why |
|---|---|
| FSharp.Core | the runtime's — FSI compiles against it |
| FSharp.Compiler.Service | the FSI session itself |
| FSharp.SystemTextJson | DU serialization for the protocol |
| FSharp.Data.Adaptive | live bindings |
| Fantomas | eval preprocessing (CompilationContext, ComputationExpression, Directives) |
| Mono.Cecil | IL coverage instrumentation |
| Harmony | hot-reload detours |

| Can move daemon-side | Why |
|---|---|
| FuzzySharp | autocomplete — a feature, not eval hot path |
| TreeSitter | syntax highlighting — ditto |
| SQLite (friction store) | persistence — daemon-side concern |

Moving those three shrinks the host's closure to the mandatory set above.

### Making each part safe — automatic adaptation, not refusal

"Fail-loud" is a *diagnosis*, not a fix. People pin versions — the host must
**adapt to the project's pins automatically**. The rule at FSI host startup:

```
for each name in hostDir ∩ projectBin:
    if the host's own code calls into this API:
        → API-coupled: swap in a version-matched VARIANT (below)
    elif API-compatible (FSharp.Core, FCS, SystemTextJson):
        → load the PROJECT's pinned version — host code doesn't call it
          directly, so the project's version is safe to run
    else:
        → move it daemon-side so it isn't in the host at all
```

**API-compatible → load the project's version.** FSharp.Core, FCS, and
FSharp.SystemTextJson are either runtime-shared (FSharp.Core — the host and
project run the same runtime, so the host's IS the project's) or consumed
through stable APIs (FCS — the host *is* the FSI consumer). When the project
pins one of these, the host loads the project's version at startup. No
refusal, no workaround — it just *is* the project's version.

**API-coupled → version-matched variants.** The host's own preprocessing
(Fantomas), coverage (Cecil), and hot-reload (Harmony) layers are *compiled
against* their libraries' APIs. Loading a different version into the host and
calling those APIs throws MissingMethodException — no load order fixes that.
So each API-coupled layer becomes its own **variant assembly**:

```
SageFs.Host.Preprocess.Fantomas8.dll     (built against Fantomas 8)
SageFs.Host.Preprocess.Fantomas6.dll     (built against Fantomas 6)
SageFs.Host.Coverage.Cecil0.11.dll       (built against Cecil 0.11)
...
```

At session start, the supervisor inspects the project's pinned versions and
loads the matching variant. "Project pins Fantomas 6" → the host runs the
Fantomas-6 preprocessing variant. The host *core* never calls Fantomas/Cecil/
Harmony APIs directly — only the variants do, and each variant is compiled
against the exact version it runs. If no variant exists for a pin, that's a
**build-time gap** (a variant needs to be added), detected at session *start*
with a clear message — never a mangled mid-eval failure.

**Feature deps → moved out.** FuzzySharp, TreeSitter, SQLite leave the host
entirely (daemon-side), so the question can't arise.

### The safety net (still enforced)

The version gate stays, but its job changes from "primary mechanism" to
"detect the unexpected":

```
at startup, for every collision:
    if the project's version has a matching variant or is API-compatible:
        → automatic adaptation (above)
    else:
        → refuse to start with an explicit, actionable message:
          "project pins X v{project} — no host variant for that version yet"
```

This is a *guarantee* in the strict sense: there is no code path where a
project's code silently runs against the wrong version of a host library —
either the host loaded the project's version, swapped in a variant built for
it, or refused before any eval.

### The FSharp.Core special case (per-TFM)

The one genuinely hard case: a project targeting a *different* TFM than the
host (net8 project in a net10 host). Its compiled assemblies reference a
different FSharp.Core line. The correct answer is structural, not in-process:
**the FSI host runs at the project's TFM** (spawn a net8 host for a net8
project, a net10 host for a net10 project). This is the same per-TFM decision
`dotnet run` makes — the host is the project's runtime, not SageFS's.

### Why this is provable

- The host's probing world is exactly: its own vetted dir + the project bin.
  Nothing else exists in it.
- The vetted dir is **enforced at build time**: a CI test asserts the packaged
  host dir contains exactly the manifest's allowed set, and a startup check
  verifies it before the session begins. A new dependency must be added to the
  manifest explicitly — it cannot sneak in.
- Every collision is handled automatically by construction:
  API-compatible libs load the project's version; API-coupled layers swap in a
  version-matched variant; feature deps are not in the host at all.
- The version gate + fail-loud load remain as the safety net for the
  unexpected (a pin with no variant yet → explicit refusal before any eval).
- There is no code path in which a project's code can silently run against the
  wrong version of a host library.

### The thin supervisor (borrowed Akka ideas, no Akka)

- **Spawn**: start the host process (correct TFM for the project).
- **Watch**: file events → forward to host.
- **Broker**: proxy the existing daemon↔host protocol byte-for-byte.
- **Restart**: on host death, restart with exponential backoff (the Akka
  BackoffSupervisor pattern, ~50 lines of F#).
- The host itself keeps the MailboxProcessor eval actor (single-owner FSI
  session) — that stays as-is; the supervisor sits *above* the host, not inside
  it.

---

## TL;DR

- One package → the worker's folder contains the daemon's libraries.
- .NET binds assemblies by name, first probe wins → the worker's own copy
  always shadowed the project's different version.
- FCS can't run FSI in a custom load context, and no flag overrides name
  binding.
- But the worker never *uses* those libraries — so the fix is to **move them
  aside at startup** (quarantine), leaving the project's versions as the only
  candidates. Same result as ALC isolation, without needing an ALC.
- The protection list should really be dynamic: **"quarantine anything not
  loaded in the worker at startup"** — not a hand-maintained list. The static
  list works today but is a snapshot of that rule.
