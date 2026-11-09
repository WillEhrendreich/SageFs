# The getter sandbox beyond Linux x86-64

Roadmap item: `getter-sandbox-beyond-linux` (Later, Platform). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: an aarch64 program refuses socket and connect. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

Clicking a "not evaluated" getter in the live-bindings pane does what the pane says it does on every platform, and the pane says it before the click, per capability. On Linux x86-64 and Linux arm64 the getter runs under a syscall filter that blocks network use, file writes and new processes, and the arm64 claim is backed by a CI leg on an arm64 runner. On macOS the pane says the getter runs under its deadline alone and what that means. On Windows it says the same unless a spike shows that a restricted thread token blocks file writes, in which case it says exactly that and no more.

The outcome gate is a host-tier case per platform leg that clicks a getter which tries a loopback connect, a file create and a process start, and asserts the pane's line and the result per cell. Matrix: Linux x64, Linux arm64, macOS arm64, Windows x64, against three getters (network, file write, spawn) and two walk modes where a click is possible. Each cell has a stated expectation (blocked, or open with a stated reason). A platform that cannot run a cell has a row saying so; nothing skips.

## What exists today

- `SageFs.Core/ThreadSandbox.fs` (371 lines), dependency free on purpose because it is also compiled into the isolated FSI host (`FsiHostBuild.fs` line 31 lists it; `SageFs.Core.fsproj` and `FsiHost.fsproj` carry the entries). It holds:
  - `SandboxArchitecture = X86_64 | Aarch64` (line 14), `SandboxPolicy = NoNetwork | NoNetworkNoWritesNoSpawn`, `SandboxUnavailable = NotLinux | UnverifiedArchitecture of string | InstallFailed of step * errno | LibraryNotFound of string`, `SandboxOutcome<'T> = Completed | Threw | Unavailable`, and `SandboxUnavailable.describe`, the one-line reason the pane shows.
  - `Seccomp.denyProgram arch policy` builds the classic-BPF bytes. For `X86_64` it assembles a deny list (network syscalls, file mutations, spawn calls, `open`/`openat`/`clone` decided on the flags argument, the x32 range, `clone3` answering ENOSYS). For `Aarch64` it returns `assemble []`, one ALLOW, with a comment: "no unchecked list of numbers exists to be installed by mistake".
  - `Seccomp.support` (line 239) answers `Error (UnverifiedArchitecture "Aarch64")`, and `Seccomp.architectureOf` maps `Architecture.Arm64` to `Aarch64`.
  - `ThreadSandbox.availability ()` (line 329) answers `Error NotLinux` off Linux, otherwise `architectureOf |> bind support`. `ThreadSandbox.run policy work` starts a dedicated thread, installs the filter with `prctl(PR_SET_NO_NEW_PRIVS)` then `prctl(PR_SET_SECCOMP)` through a `DllImport` of `libc.so.6`, runs the work there, and returns `Unavailable` (work not run) if the install fails.
- `SageFs.FsiHost/Program.fs` line 224 picks, once at host start, `ContainedBy NoNetworkNoWritesNoSpawn` with a filtered evaluator, or `NotContained why` with `MemberEvaluation.unfiltered` (the getter runs on its own thread under the deadline and guards only). `Containment` is the two-case DU at `SageFs.FsiHost/FsiProtocol.fs` line 106 and travels in `MemberShown`.
- `SageFs.Core/Features/LiveBindingsPane.fs` `containmentLine` (line 60) turns a click's outcome into one sentence: "ran under a syscall filter (no network, no file writes, no new processes)" or "no I/O containment here: <reason>". Before any click the line is `NothingClickedYet`; `LiveBindingsPanelTests.fs` line 179 asserts nothing about the filter shows before a click. The same sentence goes out through `GET /api/sessions/{sid}/live-values/mode` as `containment` (`McpServer.fs` near line 3863).
- Safe mode, the default, runs a getter only when its compiled body provably does nothing; the click is the only path that runs a getter that might do I/O (`docs/decisions.md`, "The live bindings pane starts in Safe mode").
- `SageFs.Tests/ThreadSandboxTests.fs` (636 lines). The pure program tests run on every OS against a classic-BPF interpreter and a separately written model of x86-64 (`networkNrs`, `deniedOutrightNrs`, the flag masks, `auditArchAarch64` is already declared there). The real-filter cases are gated by `isLinuxX64` (line 245) and, elsewhere, assert `Unavailable` and that the work never ran (`expectNoContainmentHere`). Line 404 asserts `Seccomp.support Aarch64` is an error; that case flips.
- `docs/decisions.md` (about line 313): "Linux x86-64 is the only verified platform... Reopen it if: arm64 gets a machine to verify on." The `ubuntu-24.04-arm` hosted runner from `hot-reload-on-windows-and-macos` is that machine.
- `main.yml` has no arm64 job today.
- The host is compiled at run time from embedded copies of its sources. `ThreadSandbox.fs` is listed in `SageFs.Core/FsiHostBuild.fs` (line 31), embedded by `SageFs.Core.fsproj` (line 118) and compiled by `FsiHost.fsproj` (line 52). A new file the host needs would have to be added in all three places.
- A note in Will's memory index (`reference_molina_fork_and_platform_findings`) says `fork(2)` hung on CoreCLR three times out of three and that seccomp needs no root. I did not open that file. If it is right, running a getter in a forked, sandboxed child (the usual macOS answer) is not available.

## Design

### Why the Windows and macOS answers differ from Linux

A getter reads a live object in the FSI host's own heap, so it has to run in that process. A sandbox that applies to a whole process would also confine the host's builds, file writes and the user's app. That rules out the per-process tools:

| Platform | Per-process tools | Fits a getter in the host? |
|---|---|---|
| macOS | Seatbelt (`sandbox_init`, `sandbox-exec`), App Sandbox | No. They apply to the whole process, irreversibly. A child process would need the object, and fork is out. |
| Windows | Job objects, AppContainer, a restricted primary token | No. They are set when a process is created and cover the whole process. |
| Linux | seccomp-bpf with a filter installed on one thread | Yes. Per thread, and threads the getter starts inherit it. |

So the honest scope is: Linux arm64 gets the same mechanism, macOS gets deadline only, and Windows may get one facet through a thread-level mechanism that I believe exists but have not run: an impersonation token on the getter's thread (`WindowsIdentity.RunImpersonated`) made with `CreateRestrictedToken` using `WRITE_RESTRICTED`. File writes are access-checked against the thread's effective token, so writes should fail with `UnauthorizedAccessException` on that thread. A socket is not access-checked by a token, so network stays open; `CreateProcess` uses the process token, so spawn stays open. This is reasoning from how Windows access checks work. Nobody has run it.

A fourth route works everywhere Harmony works: a managed fence, Harmony prefixes on a few .NET entry points (file open for write, delete, move, `Socket` connect and bind, `Process.Start`) that throw while a thread-static flag is set. It stops the .NET APIs and nothing native (a P/Invoke to `connect` walks past it), and it patches `System.Private.CoreLib`, which is the riskiest place to detour on a platform where #149 says detours already fail on one case. I list it as an option and recommend against building it now.

### Types

Replace the single sentence with a statement per capability. `Containment` moves from `FsiProtocol.fs` into `ThreadSandbox.fs` (dependency free) so Core and the host share it:

```fsharp
type Mechanism =
  | SyscallFilter of SandboxArchitecture
  | RestrictedThreadToken      // Windows, only if the spike passes
  | ManagedFence               // optional, see above

type Facet =
  | Blocked of Mechanism
  | Open of why: SandboxUnavailable   // reuse the reasons, add NoThreadLevelMechanism of os: string

type Containment = { Network: Facet; FileWrites: Facet; NewProcesses: Facet }

type SandboxHost =
  | LinuxOn of Architecture
  | MacOS of Architecture
  | WindowsOn of Architecture
  | OtherOs of name: string

module Containment =
  val forPlatform : SandboxHost -> Containment
  val describe : Containment -> string
```

`forPlatform` takes a closed DU for the host, so there is no `Option` for "unknown": `OtherOs` carries the name. It is pure, so every OS tests every row. `describe : Containment -> string` is the pane's sentence; it names each blocked facet and each open one with its reason, never a bare "contained". `ThreadSandbox.run` takes the `Containment` it will enforce, installs the mechanism of each blocked facet, and reports `Unavailable` (work not run) if any promised facet cannot be installed. The fail-closed rule is unchanged: a promise that cannot be kept means the getter does not run in the filtered path.

### Linux arm64

In `Seccomp`, add an arm64 table next to the x86-64 one and make `denyProgram Aarch64` build from it. The differences that matter, from the kernel's generic syscall table (`include/uapi/asm-generic/unistd.h`), which I have not opened here. The work transcribes the numbers from the header on an arm64 machine:

- Numbers differ, and `AUDIT_ARCH_AARCH64` is `0xC00000B7` (already in the tests).
- Legacy calls do not exist: no `open`, `creat`, `mkdir`, `rmdir`, `unlink`, `rename`, `link`, `symlink`, `chmod`, `chown`, `fork`, `vfork`. Only the `*at` forms, `openat`, `openat2`, `clone`, `clone3` and `execve` do. The deny lists are shorter and the open-flags check applies to `openat` alone.
- No x32 range. The AArch32 compat arch would be allowed, as the x86 filter allows `int 0x80` today, and the `ThreadSandbox` doc comment already lists that limit.
- `clone`'s flags are still argument 0.

Verification is the point. The BPF is built by a pure function, so the existing interpreter plus a second, separately written aarch64 model run on every OS. The real-filter cases then run on an actual arm64 machine, the `linux-arm64` leg. `Seccomp.support Aarch64` becomes `Ok ()` in the same commit that has a green arm64 leg, and the existing comment "a platform that cannot be verified is absent, never assumed" stays true because the leg is the verification.

### The pane

`LiveBindingsPane.containmentLine` gets a standing case, `Standing of text`, shown when the snapshot has at least one clickable row (`notEvaluatedCount snapshot > 0`, an existing function). The host sends its `Containment` once in its ready message (`FsiProtocol.Ready` at line 188 gains the field; I have not read the daemon-side handler that receives it, step 4 reads it first). Statements, as the text the tests assert:

| Platform | Statement before a click |
|---|---|
| Linux x86-64, Linux arm64 | Getters you click run on their own thread under a syscall filter: no network, no file writes, no new processes. |
| macOS | Getters you click run with a time limit only. macOS has no per-thread sandbox, so a getter can use the network, write files and start processes. The default Safe mode runs only getters that provably do none of that. |
| Windows, spike fails | Same as macOS with "Windows". |
| Windows, spike passes | File writes are blocked on the getter's thread by a restricted token. Network use and new processes are not. |
| Other Linux architecture, or musl | The syscall filter is not verified for this CPU or libc: time limit only. (The reason comes from `SandboxUnavailable.describe`.) |

### Wire, clients, token cost

`GET .../live-values/mode` already returns the containment as a text line, so no field is added and `apiVersion` does not move. The line's wording changes, and `McpServer.fs` is on the wire-surface list, so the commit needs a `Plugin:` trailer. `sagefs.nvim` reads this line: `lua/sagefs/live_bindings.lua` stores `data.containment` from both the evaluate and the mode replies (lines 263 and 276) and keeps it in the view. It shows whatever string it is given, so the standing line should appear there with no plugin change, but that is a claim to check in real Neovim, so the trailer is `Plugin: done (confirmed the standing line renders in the pane)` or the real finding. `sagefs-vscode` has no live-values client that I found (`rg -i contain` over `sagefs-vscode/src` hits only unrelated words). `FsiProtocol.fs` is internal to the daemon and the host, which ship in one package. No new MCP tool.

## Tests

RED first:

1. `ThreadSandboxTests`: "WHY - an aarch64 NoNetwork program refuses socket and connect and allows read, as the model says". Today `denyProgram Aarch64` is one ALLOW, so `socket` is allowed and the case fails. The model lists the aarch64 numbers written again by hand from the header, as the x86 model does.
2. Property tests mirroring the two x86 ones ("decides every ... syscall the way the model says"): a generator over syscall numbers (including every denied number, numbers just either side of each, the missing legacy numbers) and flag words, for both policies. Plus "never KILL or TRAP", "ends in ALLOW or ERRNO", "an unknown arch is ALLOWed", reused over both architectures.
3. "WHY - `Seccomp.support Aarch64` is Ok only with a verified leg": the flip. It stays red-by-design until the arm64 leg is green, so it lands last.
4. `ContainmentTests`: `forPlatform` for each `SandboxHost` returns the table above, and `describe` names every facet. Pure, runs everywhere.
5. `LiveBindingsPaneTests`: the standing line appears when a row is clickable and is absent when none is. This replaces the assertion at `LiveBindingsPanelTests.fs` line 179 on purpose; say so in the commit.

Real-filter cases: `realFilterCase` is gated by `isLinuxX64` today. Change the guard to "Linux on X64 or Arm64" written out in the test (not read from `Seccomp.support`, which would let the product vouch for itself). Same cases, same assertions, run on the arm64 leg. Every cell on a platform without a filter asserts the same shape `expectNoContainmentHere` uses now: the getter ran on its own thread under the deadline, the pane line says what is open.

Outcome gate: one `Integration.hostCaseTask` that creates a session on a fixture with three getters (loopback connect, create a file in a temp dir, `Process.Start` of `dotnet --version`), clicks each, and compares the pane text and the getter's result to the platform's row. Windows and macOS legs run it with the expectation "open", so the row proves the pane tells the truth about an open platform.

Simulation: nothing here is a schedule. The click lifecycle is already simulated (`SageFs.Simulation/GuardSim.fs`, `ClickLifecycle.step`); the sandbox adds no state to it. The twin that matters is for the filter: a mutated aarch64 table that omits one denied number must make property test 2 fail, which is what property test 2 is for. I found no mutation suite and no `SageFs.Simulation` file that mentions `ThreadSandbox` or `Seccomp` (`rg` over both directories hits only the test files `ThreadSandboxTests.fs`, `MemberEvaluationTests.fs`, `MemberEvaluationGuardTests.fs`, `FsiHostClientTests.fs` and `TestTimeouts.fs`), so the twin is a test-local mutation: build the table with one number removed and assert the property catches it.

No wall-clock waits: the real-filter cases already run on a pool thread with `TestTimeouts.patienceInProcess` as a ceiling and end on the outcome.

## Steps

1. Aarch64 table and `denyProgram`, tests 1 and 2, interpreter and model for aarch64. Owns `SageFs.Core/ThreadSandbox.fs` (the `Seccomp` module) and `SageFs.Tests/ThreadSandboxTests.fs`. Green on Linux x64 without touching `support`.
2. `Containment`, `SandboxHost`, `forPlatform`, `describe`, test 4. Moves the type out of `FsiProtocol.fs`. Owns `ThreadSandbox.fs` (new module at the end), `FsiProtocol.fs`, a new test file. Sequential after 1 (same file).
3. The pane's standing line and the ready-message field, tests 5. Owns `LiveBindingsPane.fs`, `FsiHost/Program.fs`, `FsiProtocol.fs`, `LiveBindingsPaneTests.fs`, `LiveBindingsPanelTests.fs`, and the `McpServer.fs` text. Parallel with 1, file-disjoint, but it depends on step 2's types, so start it after 2.
4. The arm64 leg: needs `hot-reload-on-windows-and-macos` step 5. Run the real-filter cases there, then flip `support` and test 3 in one commit, and update `docs/decisions.md` (the "Reopen it if" line).
5. Windows spike (see Measure first), as a scratch test on the Windows leg. If it passes, add `RestrictedThreadToken` and its case; if not, record the result in `docs/decisions.md`.
6. Readme or docs line per platform, from `describe`, with a test tying the docs sentence to it.

## Measure first

- Does an arm64 Linux runner accept the filter at all, and does the .NET runtime run under it? The x86 test "the filtered thread still allocates, collects garbage, JITs fresh code and gets a hot method promoted" is the probe. If the runtime needs a syscall the arm64 deny list refuses, that shows up here.
- Windows: on a thread under `WindowsIdentity.RunImpersonated` with a `WRITE_RESTRICTED` token, does `File.WriteAllText` to a temp file fail, does a read succeed, does a managed thread started inside inherit the token (a .NET thread does not inherit an impersonation token by default; the x86 filter is inherited), and does `File.WriteAllText` from a pool thread the getter hands work to escape it? The second and third answers decide whether the facet is worth stating at all. If a thread started from the getter escapes, the statement must say so.
- macOS: nothing to measure; the claim is that no thread-level tool exists. I could be wrong about a newer API. One hour of reading Apple's sandbox documentation before step 5 is enough to confirm or find one.
- musl: `libc.so.6` is glibc's name. Alpine fails with `LibraryNotFound`. Measure on an Alpine container whether the right name is `libc` and whether the filter then works.

## Risks and dependencies

- A wrong aarch64 number silently allows what it should block, or blocks something the runtime needs. The model written separately plus a real arm64 run are the two defenses; the plan ships nothing for arm64 on the model alone.
- Dependency: `hot-reload-on-windows-and-macos` for the arm64 and Windows legs. Its `PlatformSupport` table can carry a `ThreadSandbox` row per platform; this item works without it, using `SandboxUnavailable` as today.
- Anything else that calls `ThreadSandbox.run` besides `MemberEvaluation.filtered` would see the new signature. I found no other caller in `SageFs`, `SageFs.Core`, `SageFs.Host` or `SageFs.FsiHost`; test code is the other user.
- What would change my mind: if the Windows spike shows a pool thread escapes the token, drop the Windows facet and state macOS's sentence for Windows.

## Size

M. Linux arm64 plus the pane statement is a week once the arm64 leg exists; the Windows spike adds days and may end in "no". The managed fence, if ever built, is a separate L item.

## Decisions for Will

- Show the standing containment line before any click, whenever a clickable row exists? It adds one line to the pane and reverses the assertion at `LiveBindingsPanelTests.fs` line 179. I recommend yes: the user should know before clicking what the click can do.
- Build the managed fence for macOS and Windows? I recommend no. It stops only .NET APIs, detours CoreLib on a platform where detours are under suspicion, and a statement that says "partly" is easy to read as "safe".
- Should Safe mode (the default) be described next to the platform statement, as in the macOS row? I recommend yes, since it is what makes "deadline only" acceptable as a default.
