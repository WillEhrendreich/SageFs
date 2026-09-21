# Can I use SageFs with…?

The short answer for most of the F# ecosystem is **yes**. This page says exactly where the
edges are, and why they're there, so you can tell in advance which half of your project
SageFs is the right tool for. I'd rather tell you the edge exists than have you find it
yourself at 11pm.

Every claim here was checked against a real project that was actually built and run. Where
something doesn't work, the page quotes the error you'll actually see, because I hate docs
that describe a limitation in the abstract and then leave you guessing whether the error
you're staring at is the one they meant.

---

## The one rule that explains every row

SageFs runs your code in a live .NET process. Its REPL is F# Interactive; its hot reload
patches **.NET method bodies in that running process** (via Harmony method detours); its live
testing and coverage read .NET assemblies.

So there is exactly one question behind every row below:

> **Is the thing you want SageFs to change a .NET method, running under the JIT, in a process
> SageFs controls?**

- **Yes** → everything works: REPL, hot reload, live testing, coverage, `run_app`.
- **No, it's JavaScript in a browser** (Fable client code) → SageFs can't patch it, because
  there's no .NET method there to patch. Vite's HMR already does that job well, and better
  than I could.
- **No, it's an already-AOT-compiled native binary** → nothing can patch it. There's no JIT
  and no reflection-emit in that process, so F# Interactive can't exist inside it.

Those last two aren't missing features. They're different machines.

---

## Compatibility at a glance

| You're building | REPL | Hot reload | Live testing + coverage | Notes |
|---|---|---|---|---|
| **Falco** | ✅ | ✅ browser refresh | ✅ | Fully supported; used by SageFs's own dashboard |
| **Giraffe** | ✅ | ✅ browser refresh | ✅ | Fully supported |
| **Saturn** | ✅ | ✅ browser refresh | ✅ | Fully supported |
| **Oxpecker** | ✅ | ✅ browser refresh | ✅ | Supported since the detection fix below |
| **Plain ASP.NET Core / Minimal API** | ✅ | ✅ browser refresh | ✅ | Supported since the detection fix below |
| **Shared / domain projects** (SAFE `Shared`) | ✅ | ✅ | ✅ | Ordinary .NET. The best-supported thing in the list |
| **Fable / Elmish / Feliz client** | ⚠️ partial | ❌ for browser code | ⚠️ partial | Builds and loads fine; browser bindings throw when evaluated. Use Vite HMR |
| **SAFE-stack full-stack app** | ✅ server + shared | ✅ server + shared | ✅ server + shared | Point SageFs at Server and Shared; let Vite handle Client |
| **React / Vue / Angular front end + F# API** | ✅ | ✅ | ✅ | Your front end is a separate process SageFs never touches — nothing to support |
| **Native AOT** (developing one) | ✅ | ✅ | ✅ | Dev-time is JIT. See the caveat below — it's real |
| **Native AOT** (an already-published binary) | ❌ | ❌ | ❌ | Impossible on any tool. CLR limit |
| **.NET Framework** (`net48` etc.) | ❌ | ❌ | ❌ | Refused up front with a clear message — [issue #135](https://github.com/WillEhrendreich/SageFs/issues/135) |

---

## Web frameworks: Falco, Giraffe, Saturn, Oxpecker, plain ASP.NET

All four frameworks, and plain ASP.NET Core / Minimal APIs, are fully supported: full REPL,
hot reload with browser refresh, live testing, coverage, and `run_app`.

**Oxpecker** specifically:

| Package | Supported |
|---|---|
| `Oxpecker` | ✅ server |
| `Oxpecker.ViewEngine` | ✅ server |
| `Oxpecker.Htmx` | ✅ server |
| `Oxpecker.OpenApi` | ✅ server |
| `Oxpecker.Solid`, `Oxpecker.Solid.FablePlugin` | ⚠️ these are **Fable/Solid.js client** packages — see the Fable section |

Both Oxpecker project shapes work. You don't need the Web SDK: the `Oxpecker` package
declares its own `Microsoft.AspNetCore.App` framework reference, so a plain
`Sdk="Microsoft.NET.Sdk"` project builds and runs.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup><Compile Include="Program.fs" /></ItemGroup>
  <ItemGroup><PackageReference Include="Oxpecker" Version="2.1.1" /></ItemGroup>
</Project>
```

### What was actually broken until recently

SageFs decided "is this a web app?" by looking only at `<PackageReference>` names. A modern
ASP.NET Core or Minimal API project gets ASP.NET from `Sdk="Microsoft.NET.Sdk.Web"` plus a
`<FrameworkReference Include="Microsoft.AspNetCore.App" />` — **not** from any package
reference. So those projects got classified as console apps and were never offered the
hot-reload workflow. An Oxpecker project hit the same wall twice: Oxpecker wasn't in the list,
and the project shape didn't help either. Embarrassing bug, easy fix once I actually looked.

SageFs now reads the SDK attribute and framework references out of the `.fsproj` itself, so a
web project with zero package references is recognised as one. It also reads
`paket.references`, because Paket-managed projects — the SAFE template among them — carry no
`<PackageReference>` at all.

---

## Fable, Elmish, Feliz and SPA clients

This is the interesting row, and the common assumptions about it are wrong in both directions.

### What is true

**A Fable client project builds fine.** `Fable.Core`, `Feliz`, `Fable.Browser.Dom`,
`Fable.Elmish` and `Fable.Elmish.React` all restore and `dotnet build` with **zero warnings**
on a `net10.0` project. Every one of them ships a real `lib/netstandard2.0/*.dll`, and none
injects MSBuild targets. There's no compile error to report.

**A Fable project doesn't poison your session.** A SAFE-shaped
`Server` / `Shared` / `Client` solution builds clean, and the Client produces a real
`Client.dll`. Building the Server alone works. Building the Client alone works. A SageFs
session over that solution warms up without a single build error.

**`Fable.Elmish` genuinely runs on .NET.** Its MVU core is a portable library —
`Program.mkSimple ... |> Program.run` really does execute its update/view loop in a plain
console app. "Fable packages are JavaScript-only" is not true as a blanket claim, and I was
mildly surprised by that too.

### What does not work

Evaluating the **browser bindings** in the REPL. They're stubs on .NET, and each family fails
differently:

| You evaluate | You get |
|---|---|
| `Fable.Core.JS.*`, `JsInterop.importDefault` | `System.Exception: You've hit dummy code used for Fable bindings. This probably means you're compiling Fable code to .NET by mistake, please check.` |
| `Browser.Dom.document`, `window` | `System.TypeInitializationException` → inner `System.Exception: JS only` |
| `Feliz` view builders (`Html.div [ ... ]`) | `System.InvalidCastException: Unable to cast object of type 'System.Tuple`2[System.String,System.Object]' to type 'Feliz.IReactProperty'.` |

And hot reload of client code doesn't apply at all. Fable's output is JavaScript; there's no
.NET method for Harmony to detour. This is a category difference, not an unfinished feature —
I'm not going to build a fake progress bar toward a thing that's structurally impossible.

### So what should you actually do?

For a SAFE-stack or any Fable + F#-server app, the work splits cleanly:

| Part of your app | Tool | What you get |
|---|---|---|
| **Server** (Falco / Giraffe / Saturn / Oxpecker / ASP.NET) | **SageFs** | Full REPL, hot reload, browser refresh, live testing, coverage |
| **Shared / domain `.fs`** | **SageFs** | Full REPL, hot reload, live testing, coverage — this is plain .NET |
| **Client** (Fable → JS) | **Vite HMR** | Fable's own toolchain already does sub-second client hot reload well |

Point SageFs at `Server.fsproj` and `Shared.fsproj`. Run `dotnet fable watch` / Vite alongside,
as you already would. You lose nothing, because the client half was never SageFs's job.

Since your `Shared` project is usually where the domain types, validation and business rules
live, this isn't a consolation prize — it's the part live testing and the REPL help with most.

SageFs will tell you this itself: create a session on a Fable client project and the reply
names the project, the references that identified it, what still works, and what to use
instead.

### Other SPA front ends: React, Vue, Angular, Svelte, HTMX

If your front end is TypeScript or JavaScript talking to an F# API over HTTP, **there's nothing
to support**. Your front end runs in its own dev server, in its own process, and SageFs never
sees it. SageFs hot-reloads your F# API; your front end's own dev server hot-reloads itself.
That combination works today and always has, because neither side needs the other to change.

HTMX and Datastar go further: because the server renders the markup, hot-reloading the server
*is* hot-reloading the UI. SageFs pushes a browser refresh over SSE on save. `Falco.Htmx`,
`Oxpecker.Htmx` and `Falco.Datastar` / `StarFederation.Datastar.FSharp` all work — this
combination is basically my daily driver.

---

## Native AOT

### Developing an app you will later AOT-publish: fully supported

Add `<PublishAot>true</PublishAot>` and keep using SageFs normally. Warmup, eval, hot reload,
live testing and `run_app` all behave exactly as they do for any other project.

The reason is that SageFs's FSI host is a **separate process with its own runtime
configuration**. Your project's DLL is loaded into it as a library, not as the entry assembly,
so your project's AOT settings don't govern that process. I verified this directly: loading
an AOT-flagged assembly into an FSI host leaves `RuntimeFeature.IsDynamicCodeSupported = true`,
and reflection-emit, `System.Text.Json` and eval all work.

`run_app` is the same story — SageFs invokes your project's entry point **in-process** via
`Assembly.LoadFrom`, so your app's own `runtimeconfig.json` is never read.

Two smaller worries that turn out to be unfounded:

- **`PublishAot` does not enable `InvariantGlobalization`.** Culture-sensitive string comparison
  behaves normally; `tr-TR` `ToUpper('i')` still gives `İ`.
- **`<IsAotCompatible>true</IsAotCompatible>` cannot fail your F# build.** The trim/AOT analyzers
  are Roslyn analyzers and don't run on F#, so you get zero `IL2xxx`/`IL3xxx` warnings at build
  time even with `TreatWarningsAsErrors`.

### The caveat that is real, and is not about SageFs

`<PublishAot>true</PublishAot>` **does** change your `bin/Debug` output, not just `dotnet publish`.
The SDK writes AOT feature switches into your app's `runtimeconfig.json` at **build** time:

```json
"System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported": false,
"System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault": false,
"System.Linq.Expressions.CanEmitObjectArrayDelegate": false
```

So if you run your app **standalone** with `dotnet run`, reflection-based `System.Text.Json`
and `Reflection.Emit` throw:

```
InvalidOperationException: Reflection-based serialization has been disabled for this application.
PlatformNotSupportedException: Dynamic code generation is not supported on this platform.
```

**Practical consequence, stated plainly: SageFs is more permissive than your own app.** Because
`run_app` hosts your entry point inside the FSI worker, those switches never apply, so code that
works under SageFs can still fail under `dotnet run` and fail again at `dotnet publish`.
**SageFs cannot tell you your app is AOT-safe.** Since F# gets no AOT analyzer coverage either,
`dotnet publish -r <rid>` is the only thing that will, and it remains a required step in your CI.
I'd rather say that plainly than let a green SageFs session give you false confidence.

### Hosting an already-AOT-compiled binary: impossible

Once your app is published with Native AOT it's a native executable with no JIT, no
reflection-emit and no runtime code generation. F# Interactive can't exist in that process, and
Harmony can't patch it. No tool can do this — it's what AOT *means*. Develop under JIT, publish
to AOT.

---

## .NET Framework

Not supported. SageFs's FSI host runs on modern .NET (Core) and can't load .NET Framework
assemblies. SageFs refuses the session up front with a message naming the project, its target
framework, and why — rather than letting warmup fail with a misleading "project has not been
built".

Follow or push on it at [issue #135](https://github.com/WillEhrendreich/SageFs/issues/135) if
you need it. I'm not against it, it just needs a second host and I haven't built one.

---

## What we think is worth supporting, and why

The ask behind this page was "what's worth supporting and why". Here's the reasoning, not just
a verdict.

**1. Oxpecker — worth it, and done.** It's a real, growing ASP.NET Core framework in the
Giraffe lineage with roughly 87k downloads on the core package. Crucially, the cost was near
zero: it's an ordinary ASP.NET app, so everything already worked except *recognising* it. The
fix was one entry in a marker list. When supporting a framework costs one list entry and the
framework has thousands of users, the question answers itself.

**2. Plain ASP.NET / Minimal API — worth it, and was a bug, not a feature.** This wasn't a
"should we support X" question at all. Every Minimal API project in F# was silently misclassified
as a console app. That's the single highest-value fix on this page, because it affects a shape
far more common than any named framework.

**3. Shared/domain projects in a SAFE app — the thing actually worth emphasising.** This is the
one I think is under-sold. The `Shared` project is where the domain types and business rules
live, and it's 100% ordinary .NET. Live testing on save, full REPL, coverage — all of it applies.
When someone asks "does SageFs work with SAFE?", the honest and *useful* answer is "yes, for the
two thirds of your code where it helps most."

**4. Fable client hot reload — not worth building, and I should say so loudly.** I couldn't
build it if I wanted to: there's no .NET method to patch. But even if a JS-side reload
mechanism were bolted on, it would duplicate Vite HMR, which is already excellent, already
what Fable users run, and already faster than anything I'd ship. Building a worse copy of a
tool you already have is the wrong use of my time. **What is worth doing is the
truthfulness work** — detecting the case and explaining it — which is what shipped.

**5. Native AOT — worth a warning, not a feature.** Dev-time already works, and post-publish
hosting is impossible. The genuinely useful thing is the caveat above: SageFs is *more*
permissive than your own app's `dotnet run`, so a green SageFs session is not evidence of AOT
safety. That gap is worth stating clearly and isn't worth trying to close, because closing it
would mean reproducing AOT's restrictions inside the REPL and making the REPL worse for the 99%
of users who don't publish AOT.

**6. .NET Framework — the one genuine gap.** Unlike the Fable and AOT rows, this isn't a
category difference; it's a real limitation with real users behind it, and it would require a
second host. It's tracked, not dismissed.

The pattern: I support what is .NET-and-JIT, because that's where SageFs has something no
other tool has. Where another tool already owns the job (Vite for browser code) or where the
platform forbids it (AOT, .NET Framework's assembly format), the valuable work is telling you
the truth quickly instead of failing in a confusing way.

---

## How these answers were verified

Not from documentation or memory. For each claim:

- **Detection**: checked against this repo's own
  `SageFs.Tests/fixtures/WebAppFixture/WebAppFixture.fsproj` — an `Sdk="Microsoft.NET.Sdk.Web"`
  project — which the running daemon reported with `PackageRefs: []`, confirming that package
  references alone can't see an ASP.NET project. Covered by tests in
  `SageFs.Tests/ProjectClassificationTests.fs`.
- **Fable**: five packages restored and built individually and together on `net10.0`; their
  nupkg layouts inspected; a program written that calls the browser bindings and run, capturing
  the verbatim exceptions quoted above; and a full `Server`/`Shared`/`Client` solution built as a
  solution, server-only and client-only.
- **Oxpecker**: package ids and their nuspecs read from nuget.org — which is how
  `Oxpecker.Solid` was identified as a Fable/Solid.js client ("F# web framework built on top of
  Solid.js", depending on `Fable.Core` and `Fable.Browser.Dom`) rather than a server package.
  Hello-world projects built in both the Web SDK and plain SDK shapes.
- **Native AOT**: MSBuild properties evaluated at build and at publish; the generated
  `runtimeconfig.json` diffed against a control project; dynamic-code, `System.Text.Json` and
  `Reflection.Emit` probes run side by side under `dotnet run`; and the AOT-flagged assembly
  loaded into an FSI host to confirm the host's own configuration governs.

---

## See also

- [Workflow Modes](workflow-modes.md) — REPL vs Live, and which to pick
- [Hot Reload](hot-reload.md) — how the reload pipeline works and its current limits
- [Troubleshooting](TROUBLESHOOTING.md)
