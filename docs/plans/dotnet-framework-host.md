# A .NET Framework host for Framework projects

Roadmap item: `dotnet-framework-host` (Later, Platform). Plan written 2026-11-01 against master bf73d159. Requested in [issue #135](https://github.com/WillEhrendreich/SageFs/issues/135).

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

The issue also claims the host's dependency closure (FSharp.Compiler.Service, FSharp.Core, Lib.Harmony 2.4.2, System.Text.Json) has Framework-compatible targets. I did not re-check it against the current pins in `Directory.Packages.props`. Measure first covers it.

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
2. `FsiHostBuild` builds the host for `net48` when that answer comes back. The known differences from the modern host: no `Microsoft.AspNetCore.App` framework reference, FCS and FSharp.Core come from NuGet and not the SDK's `FSharp/` folder, and the Cecil rename of Harmony to `SageFs.HostHarmony` has to work against the `net48` asset.
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
