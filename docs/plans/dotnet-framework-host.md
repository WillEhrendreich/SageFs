# A .NET Framework host for Framework projects

Roadmap item: `dotnet-framework-host` (Later, Platform). Plan written 2026-11-01 against master bf73d159. Requested in [issue #135](https://github.com/WillEhrendreich/SageFs/issues/135). Work order added 2026-11-11 against master 8975e898; line numbers below are from that tree.

## Work order

### 1. Outcome

On Windows, a session on a `net48`-only project reaches Ready, and in it `System.Environment.Version` and `RuntimeInformation.FrameworkDescription` say .NET Framework, a `#r` of a Framework-only assembly loads, and the project's own Framework-API code evaluates. On Linux and macOS the same project is still refused, and the refusal says the Framework host runs on Windows only (today it says only that the host "cannot load .NET Framework assemblies", `SageFs.Core/ProjectCompatibility.fs:39-40`). Gate: a Windows CI leg that opens a `net48` fixture, evaluates a Framework API, saves an edit and sees it land; there is no Windows leg today (`.github/workflows/main.yml:32-38`), no `net48` fixture in the tree (`rg -l "net48|net472" --glob '*.fsproj' --glob '*.csproj'` finds nothing), and this machine is Linux. Everything about running on the Framework CLR is unproven; the first measurement below can end the item.

### 2. Where the code is today

- Classification is pure and tested. `UnsupportedTfmReason.NetFramework` (`ProjectCompatibility.fs:35`), `TfmVerdict` (`:51`), `ProjectHostability = Hostable | NotHostable | Indeterminate` (`:84-93`), `classifyTfm` (`:117`, `net\d{2,3}` is Framework, `:101`), `classifyTfms` (`:165`), `classify` (`:198`), `describeUnhostable` (`:286-292`), `trackingIssue` (`:45-47`), `findUnhostableEvaluated` (`:368`, asks MSBuild when the file names no TFM). Tests: `SageFs.Tests/ProjectCompatibilityTests.fs` (`SageFs.Tests.fsproj:706`); cases `:49`, `:53`, `:66` assert `net48`, `net472` and `net47;net48` are NOT hostable, and they have no OS parameter.
- The refusal: `SageFs/DaemonMode.fs:553` calls `findUnhostableEvaluated` before any build and returns `SageFsError.ProjectFrameworkNotHostable` (`SageFs.Core/SageFsError.fs:208`; text `:347`, HTTP 400 `:487`, suggested action `:614` "Point SageFs at a .NET (Core) project"). Two more readers of `classifyTfm`: `SageFs.Core/SdkChoice.fs:439` (which SDK major to build the host with; `Unsupported` contributes nothing) and `SageFs.Core/HostAdaptation.fs:205` (`Refusal.FrameworkNotHostable`). A third verdict has to be decided at all three.
- The host is one project built at run time with the target project's SDK. `SageFs.FsiHost/FsiHost.fsproj:9` `TargetFramework = net$(BundledNETCoreAppTargetFrameworkVersion)`; `:11` `DisableImplicitFSharpCoreReference`; `:32` `FrameworkReference Microsoft.AspNetCore.App` (does not exist for `net48`); `:49-51` `FSharp.Core`, `FSharp.Compiler.Service` and `FSharp.DependencyManager.Nuget` referenced by `HintPath` out of the SDK's `FSharp/` folder (`:27`), no `PackageReference` at all; `:53` `SageFs.HostHarmony.dll` by `HintPath`. `SageFs.Core/FsiHostBuild.fs`: `hostSourceNames` (`:20-69`, 49 entries, mirrored by `FsiHost.fsproj` and the `EmbeddedResource` list in `SageFs.Core.fsproj`), `HostHarmonyName` (`:140`), `renameAssembly` with Mono.Cecil (`:144`), `hostHarmony` (`:154`, reads `typeof<HarmonyLib.Harmony>.Assembly.Location`, so it renames the asset the DAEMON loaded, a `net10.0`/`net11.0` one), `SdkSelection = { Version; DotnetRoot }` (`:227`), `ensureBuiltWith` (`:330-375`: writes sources, the renamed Harmony and a `global.json`, runs `dotnet build FsiHost.fsproj -c Release -o bin`, caches under `cacheKey`). Callers: `SageFs.Core/IsolatedFsiSession.fs:619`, `:721`; `SageFs.Core/ConfigHost.fs:43`. Tests: `SageFs.Tests/FsiHostBuildTests.fs` (`fsproj:424`; `cacheKey`, `globalJson`, `hostBuildRuntimeEnvironment`).
- The session side: `IFsiSession` implemented by `InProcessFsiSession` (`SageFs.Core/FsiSession.fs`) and `RemoteFsiSession` (`SageFs.Core/RemoteFsiSession.fs`), driven from `IsolatedFsiSession.fs` over `FsiHostClient.fs`; `FsiProtocol.fs` is compiled into Core (`SageFs.Core.fsproj:628`) and the host.
- Package assets, read from the local caches on 2026-11-11: `SageFs.Harmony 2.4.2-sagefs.1` (`harmony-nupkg/`, `Directory.Packages.props:54`) packs `lib/netstandard2.0`, `lib/net8.0`, `lib/net10.0`; `FSharp.Compiler.Service 43.13.101-preview7.26381.103` (`:39`) packs `lib/netstandard2.0` only; `FSharp.Core 11.0.101-preview7.26359.118` (`:127`) packs `lib/netstandard2.0` and `netstandard2.1`. So the closure the issue names has a Framework-loadable asset for each, at the asset level. Whether they RUN on the Framework CLR is the measurement. `Microsoft.NETFramework.ReferenceAssemblies` is not pinned anywhere (`grep` of `Directory.Packages.props`), and `System.Text.Json` and `System.Memory` (`FsiProtocol.fs:509` uses `ReadOnlySpan<byte>`) are packages on `net48`, not framework assemblies.
- Docs that say Framework is unsupported: `docs/ecosystem-compatibility.md:56` (table row) and `:235-243` (section); `docs/how-isolation-works.md:64`, `:85`.
- `[not verified]`: that the SDK's `FSharp/` folder FCS is the same `netstandard2.0` build as the package's; the Harmony fork's behaviour on the Framework CLR; what in `SageFs.FsiHost/Program.fs` and the 49 compiled files reaches a modern-only API (compiles for `netstandard2.0`, throws on `net48`).

### 3. Steps

Step 0 is the measurement and runs before any code. It needs a Windows machine with the .NET Framework 4.8 runtime and a .NET SDK. There is no useful Linux-only first commit; a Linux build of a `net48` host through reference assemblies proves nothing about loading.

0. Measure (hand run, not committed as code). Copy `SageFs.FsiHost/` to a scratch folder; change `:9` to `<TargetFramework>net48</TargetFramework>`; drop `:32`; replace the three `HintPath` references with `PackageReference`s to `FSharp.Core`, `FSharp.Compiler.Service` (the pins above), plus `System.Text.Json` and `System.Memory`; add `Microsoft.NETFramework.ReferenceAssemblies` if the machine has no targeting pack; take the Harmony `netstandard2.0` asset out of `harmony-nupkg/SageFs.Harmony.2.4.2-sagefs.1.nupkg` and rename it with `FsiHostBuild.renameAssembly` in a SageFs session (`send_fsharp_code`, it is pure over bytes); build; start the exe; drive one handshake (`FsiProtocol.Request` on stdin, expect `Response.Ready`, `FsiProtocol.fs:192`); evaluate `System.Environment.Version`; `#r` a Framework assembly; apply one Harmony patch through `HostAgent`. Record every answer, including compile errors, in "Surprises". If FCS or FSharp.Core will not bind, the item ends: the only route left is driving `fsianycpu.exe`, which the plan rejects.
1. RED, pure: `ProjectCompatibilityTests` gains "WHY - net48 on Windows is hostable by the Framework host, and on any other OS is refused with the Windows-only reason". Give `classifyTfms` and `classify` an explicit host-OS argument (a closed DU, `HostOs = Windows | Linux | MacOS | Other of string`, in `ProjectCompatibility.fs`; never `OperatingSystem.IsWindows()` inside the pure function) and a third `ProjectHostability` case, `HostableByFrameworkHost of tfm`. The three readers (`DaemonMode.fs:553`, `SdkChoice.fs:439`, `HostAdaptation.fs:205`) break and decide. The existing cases at `:49`, `:53`, `:66` pass the non-Windows OS. `describeUnsupportedReason` (`:39`) says "runs on Windows only" for the refusal. Commit red, then green. REPL-provable, so prove it in a session first.
2. `FsiHostBuild` gains a `HostFlavour = Modern | Framework` on `SdkSelection` (or beside it) that selects a second embedded fsproj text, `FsiHost.net48.fsproj`, carrying the step-0 differences, and a Harmony asset path per flavour (the `netstandard2.0` asset has to be carried in the daemon, since `hostHarmony` today renames the loaded one). `cacheKey` takes the flavour. RED in `FsiHostBuildTests`: "the cache key differs by flavour" and "the Framework fsproj names no AspNetCore framework reference and no SDK HintPath". Register nothing new; the test file exists. Edge code: `ensureBuiltWith`.
3. Session start picks the flavour from step 1's verdict (`IsolatedFsiSession.fs:619`, `:721`) and the Framework host is run directly (`FsiHost.exe`, no `dotnet` muxer). A simulation twin for a host that dies during the handshake belongs in the existing host-start sims (`SageFs.Simulation/StartEscalationSim.fs`) if the handshake path changes; if it does not, say so in the commit.
4. Windows CI leg, after `hot-reload-on-windows-and-macos` step 5: a `net48` fixture under `SageFs.Tests/fixtures/` (new), a host-tier case (`Integration.hostCaseTask`, `TestInfrastructure.fs:520`) that creates a session on it, evaluates `RuntimeInformation.FrameworkDescription`, saves an edit, sees it land.
5. Docs: `ecosystem-compatibility.md:56`, `:235-243`; `how-isolation-works.md:64`, `:85`; `SageFsError.fs:614` suggested action. Commit trailer `Plugin: n/a` unless the verdict reaches the wire (a new `targetFramework`-style field on session status would need `Plugin: done` and the plugin first).

DECISION: FCS pinned by SageFs in the Framework host (differs from the SDK's FSI) or none at all. Recommendation: pinned, and `get_session_status` says which FCS the session runs.
DECISION: who runs step 0 and on what machine. Nothing starts without it.

### 4. How to check it

- REPL first (`Skill(skill: "sagefs")`): step 1 is pure, prove `classifyTfms` in a session on `SageFs.Core` before editing.
- `dotnet build SageFs.Tests -c Release`
- Inner loop: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --filter-test-list "ProjectCompatibility" --summary` and `--filter-test-list "FsiHostBuild"` (both tokens are `testList` names).
- `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`
- Acceptance: the unfiltered default suite on Linux for steps 1-3; the Windows leg for step 4, which does not exist yet.

### 5. Traps in this repo

- New NuGet packages (`Microsoft.NETFramework.ReferenceAssemblies`, `System.Text.Json`, `System.Memory` for the host build) need discussion before they go in `Directory.Packages.props`; the host's own restore happens at run time against the project's SDK, so decide where those versions are pinned.
- `hostSourceNames` (`FsiHostBuild.fs:20-69`), `FsiHost.fsproj` and the `EmbeddedResource` list in `SageFs.Core.fsproj` must agree; a second fsproj text is a new embedded resource in all three places.
- SAGE-005: `classifyTfms (tfms: string list) (hostOs: HostOs)` is fine; do not add a second `string` for the OS.
- Budgets only go down: `RatchetBudgets.fs:341` `DaemonMode.fs` is at 5528 of 5529 lines, so the verdict match at `:553` gains at most one line there; put any new logic in `ProjectCompatibility.fs`.
- No timing-sensitive tests: the handshake wait in step 3 is on `Response.Ready`, with a `TestTimeouts` ceiling.
- Linux-only machine: steps 0, 2's run, 3's run and 4 are unverifiable here.

### 6. Size and order

L. Step 0 is a day on Windows and can end it. Steps 1 (half a day, Linux) and 2 (two days) are sequential; 3 is a day; 4 waits on the Windows leg; 5 is an hour. One owner. First commit: none before step 0's answers are in "Surprises"; then the RED test of step 1.

### 7. Not now

- Driving `fsianycpu.exe` over stdin/stdout: a session in name only, rejected in Design.
- Hot reload, live testing and coverage on the Framework CLR: each is its own measurement after the host answers; the first slice ships eval only and says so in `get_session_status`.
- A Windows leg as a scheduled job: that is `hot-reload-on-windows-and-macos` step 5.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | L, and the first measurement can end it |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run "Measure first" on a Windows machine. Nothing else starts before the host answers its handshake on the Framework CLR. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | The issue says `publish.yml` has a `windows-latest` runner. It does not: the Windows leg was removed, see the comment at `.github/workflows/main.yml` line 32. |

## What done looks like

A Windows user opens a `net48` library, creates a session for it, and the FSI behind that session runs on the .NET Framework CLR. `System.Environment.Version` and `RuntimeInformation.FrameworkDescription` in the session say so, a `#r` of a Framework-only assembly works, and the project's own code that calls Framework APIs evaluates. Hot reload and live testing work on it the way they do on the modern host, or the page says which of them do not.

On Linux and macOS a Framework project is still refused, and the message says the Framework host runs on Windows only. The refusal today says the host cannot load Framework code and does not say that is a gap.

The outcome gate is a Windows CI leg that opens a `net48` fixture, evaluates a Framework API, saves an edit and sees it land. That leg does not exist yet (see Measure first).

## What exists today

I read these on 2026-11-01.

- `ProjectCompatibility.classify` and `findUnhostableEvaluated` (`SageFs.Core/ProjectCompatibility.fs`) return `NotHostable` for a project whose every target is Framework. `SageFs/DaemonMode.fs` calls `findUnhostableEvaluated` before any build and answers `SageFsError.ProjectFrameworkNotHostable`, so the refusal happens at session creation, with the project's real target framework asked of MSBuild when the project file does not name one.
- The FSI session sits behind a port. `IFsiSession` is used by `SageFs.Core/SessionAgent.fs` and `IsolatedFsiSession.fs`, with `InProcessFsiSession` (`FsiSession.fs`) and `RemoteFsiSession` (`RemoteFsiSession.fs`) as implementations.
- The remote host is the `SageFs.FsiHost/` project (`FsiHost.fsproj`, `Program.fs`, `FsiProtocol.fs`), generated and built by `FsiHostBuild` (`SageFs.Core/ConfigHost.fs`, `IsolatedFsiSession.fs`, `FsiHostClient.fs`) with the target project's own SDK.
- Docs that say Framework is unsupported: `docs/ecosystem-compatibility.md` (the table row and the ".NET Framework" section) and `docs/how-isolation-works.md`.
- CI has no Windows leg. `.github/workflows/main.yml` says the former one is gone and to add a scheduled Windows smoke back if a real Windows runtime issue surfaces.

The issue also claims the host's dependency closure (FSharp.Compiler.Service, FSharp.Core, Lib.Harmony 2.4.2, System.Text.Json) has Framework-compatible targets. Checked on 2026-11-11 against the local package caches (see the Work order): the SageFs Harmony fork, FCS and FSharp.Core at the current pins each pack a `netstandard2.0` asset. Whether they run on the Framework CLR is still the measurement.

## Design

### The two ways to get "FSI on the Framework CLR"

| | A Framework variant of the SageFs host | Drive `fsianycpu.exe` over stdin and stdout |
|:--|:--|:--|
| What runs | the same host program, built for `net48`, speaking the same protocol | the FSI that ships with Visual Studio or the SDK |
| Hot reload, live testing, coverage, value tree | the host's own agent can carry them | none: FSI's console protocol has no structured diagnostics, no completions, no inspection and no place to host an agent |
| FCS version | the SageFs pin, from NuGet (`netstandard2.0`) | whatever the installed FSI is |
| Effort | a second build of one project, plus selection logic | small, and a session in name only |
| Verdict | the plan | only if someone needs the exact `fsianycpu` binary, for a toolchain with no SDK or for documented parity |

### The shape

The host is a variant of an existing artifact, so the work is three small things and one large check.

1. Classification gains a third answer. A Framework-only project on Windows is hostable by the Framework host. On any other OS it stays refused, with the Windows-only message.
2. `FsiHostBuild` builds the host for `net48` when that answer comes back. The known differences from the modern host: no `Microsoft.AspNetCore.App` framework reference (`FsiHost.fsproj:32`), FCS and FSharp.Core come from NuGet instead of the SDK's `FSharp/` folder `HintPath`s the modern host uses (`FsiHost.fsproj:27`, `:49-51`), and the Cecil rename of Harmony to `SageFs.HostHarmony` has to work against the package's `netstandard2.0` asset, where today `hostHarmony` (`FsiHostBuild.fs:154`) renames the assembly the daemon itself loaded.
3. Session start picks the host by the verdict, and `RemoteFsiSession` talks to it unchanged.
4. A Windows run proves it. Building on Linux works through `Microsoft.NETFramework.ReferenceAssemblies`, and running does not.

### Risks I can name

- Harmony on the Framework CLR is its oldest path, but the rename and the shipped patches have only ever run on modern .NET here. A hot reload that fails on Framework should refuse with a reason and not degrade silently.
- A different FCS from the one the SDK ships can change what the REPL accepts. The session status should say which FCS it runs.
- Anything in the host that reaches for a modern-only API will compile for `net48` and fail at run time. The measurement below finds those before any design depends on them.

## Measure first

These can end the item, so they run before any code. They need a Windows machine with the .NET Framework 4.8 runtime.

1. Hand-build `SageFs.FsiHost` for `net48` and start it. Does it answer the protocol handshake? If FCS or FSharp.Core will not bind on the Framework CLR, the host variant is dead and the fallback in the table is the only route.
2. With the host up, evaluate `System.Environment.Version`, a `#r` of a Framework-only assembly, and one Framework API call. Record the FCS version it reports.
3. Apply one Harmony patch under it, through the renamed assembly. Record whether it takes.
4. Re-check the closure: for each package the host needs, list the Framework-compatible target it resolves to under the current pins.

## Steps

1. RED: `ProjectCompatibility.classify` for `net48` returns a verdict that names the Framework host on Windows and the Windows-only refusal elsewhere. Pure, so it runs in the REPL.
2. The `net48` build of the host project, with the differences above, proven by the Measure first handshake.
3. Session start chooses the host from the verdict. A simulation twin covers a host that dies during the handshake.
4. The Windows CI leg: open the `net48` fixture, evaluate a Framework API, save an edit, see it land. It shares the Windows runner that `hot-reload-on-windows-and-macos` also needs.
5. Docs: the table row and section in `docs/ecosystem-compatibility.md`, and the refusal message.
6. Plugin: `Plugin: n/a` unless the verdict reaches the wire as a new field, in which case `Plugin: done` with the change.

## Decisions for Will

1. Is a SageFs-pinned FCS in the Framework host acceptable, given the REPL then differs from the SDK's FSI?
2. Is `fsianycpu.exe` worth building at all, or does the fallback stay a sentence in the docs until someone asks for the exact binary?
3. Which machine runs the Windows measurement, and does the Windows CI leg come back as a scheduled job, as the comment in `main.yml` suggests?
