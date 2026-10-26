# Hot reload checked on Windows and macOS

Roadmap item: `hot-reload-on-windows-and-macos` (Later, Platform). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a platform nobody listed is Unproven. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

A person on macOS (Apple Silicon or Intel), Windows or Linux arm64 can ask SageFs what is known about hot reload on their machine and get an answer that comes from a CI run on that platform. `sagefs check` prints one line per platform fact (`hot reload detours: unproven on macOS arm64, see #149`, or `proven by leg windows-x64, since 0.6.9xx`), and the Readme and `docs/how-hot-reload-works.md` say the same thing because a test ties their sentences to the same table. The table moves a row to `Proven` only in a commit that also names a CI leg that ran green on that platform.

The outcome gate is a CI leg per platform that runs three things on a GitHub-hosted runner: the in-process detour proof, the packaged-tool session smoke, and one real save that the same running app serves with the new value. The matrix is five platforms (macOS arm64, macOS x64, Windows x64, Linux arm64, and Linux x64 as the control that already passes) against five cells (plain static detour, value-type generic body, shared generic body, instance member, real app save). No cell is skipped silently: a cell a platform cannot run is a `KnownBroken` or `Unproven` row, and the leg's exit code says so.

The plan stops short of fixing the five #149 failures. It gets their exact text from CI with no Mac in the loop, and it replaces the guess behind "does hot reload work here" with a table row.

## What exists today

CI shape, all read this session:

- `.github/workflows/main.yml` has two jobs. `local` runs on `[self-hosted, sagefs-local]` for push and dispatch and promotes the bundle the local gate built. `pr` runs `dotnet fsi ci-pipeline.fsx -- ci` on `ubuntu-latest`. There is no Windows or macOS job. Commit `5358bcfd` ("ci: collapse to a single Linux leg, drop the windows job") removed the last one, and the header comment in `ci-pipeline.fsx` says "If a real Windows runtime issue ever surfaces, add a scheduled windows smoke back."
- `.github/workflows/publish.yml` publishes only when the `main build` workflow run concluded `success`. A failing extra job in `main.yml` therefore blocks a release. This shapes the whole design below.
- `ci-pipeline.fsx` is one `pipeline "sagefs"` with stages: `source ratchets`, `restore mcp sdk fork`, `restore harmony fork`, `build`, `ratchets`, `build other frameworks`, `format`, `build samples for integration suites`, three `vscode ...` stages, `test tiers`, `classify failures`, `trust report`, `package vscode extension`, `pack release bundle`, `packaged tool smoke`. The tokens `ci` and `release` are read with `hasTier` and stripped by `tierArgs`; stages gate themselves with `when'`.
- Linux assumptions inside it: each tier runs in a `systemd-run --user --scope`, Chromium installs from `.playwright/node/linux-x64/node`, tiers run in reflink clones. A Windows or macOS leg cannot reuse the `test tiers` stage as it stands.
- The two Linux jobs each check out `sagefs.nvim` and set `SAGEFS_NVIM_DIR`. The reason is written in `main.yml`: `SseParityTests` reads that repo's Lua from a module-level `let`, and when the folder is missing the type initializer throws and aborts the process (exit 134). Any new lane that forces test discovery hits the same crash.

Hot reload code and its known platform assumptions:

- The five tests of issue #149 are the five `testCase`s in `testList "the real detours"` in `SageFs.Tests/GenericReloadTests.fs` (lines 252, 279, 296, 328, 345). The last one asserts a refusal, so on macOS arm64 even a refusal case fails. I could read the issue with `gh issue view 149`; it has no comments and no failure text.
- `SageFs.Core/Middleware/HotReloadCore.fs` (`module private NativeDetours`, near line 764) reaches MonoMod's `PlatformTriple` by reflection and calls `GetNativeMethodBody` and `CreateNativeDetour`.
- `SageFs.Core/Middleware/GenericReload.fs` `sharedStub` (line 504) builds a `DynamicMethod` whose signature is `self; hidden; visible`, then `calli`s the new body. Its comment on `returnsThroughBuffer` (line 497) says a struct return buffer comes "before the hidden argument". That is the x64 calling convention. I did not check the arm64 one.
- `HotReloadCore.fs` line 202 (`resolveJitCodeAddress`) decodes `FF 25` (an x64 `jmp [rip+disp]`) to find the real code. The comment says "On x64 .NET". An arm64 method has a different precode stub, so the canary that confirms a detour changed bytes is x64-shaped.
- `docs/decisions.md` line 396 lists "Windows and macOS" under "Not tried". `HotReloadDebuggerTests.fs` line 57 fails on anything but Linux x64.
- The Harmony stack is a fork: `restore harmony fork` clones LibHarmony at a pinned commit and builds `SageFs.Harmony 2.4.2-sagefs.1`. `Directory.Build.props` explains the MonoMod patch for .NET 11.

Install and first session on each platform:

- `runtimes/` carries tree-sitter natives for `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` and `win-x64`.
- `Args.resolveHostLaunch` (`SageFs.Core/Args.fs` line 287) prefers `SageFs.Host.exe` on Windows and falls back to running `SageFs.Host.dll` through the dotnet muxer. The `ci-pipeline.fsx` header says a Windows user gets the muxer path. I did not unpack a nupkg to confirm that `SageFs.Host.exe` is absent.
- `scripts/smoke-test.fsx` (602 lines, plain F#: `Process`, `HttpClient`) installs nothing itself; the `packaged tool smoke` stage installs the nupkg to a throwaway `--tool-path` and runs the script against that install. Nothing ever ran it on Windows or macOS.
- `Features.MachineMemory.current` reads `/proc/meminfo` on Linux and approximates elsewhere (its own doc comment says so).
- Many tests are already guarded for Windows (`4609ff06`: they are registered only where they mean something), so someone has thought about Windows. Nobody has run the suite there.
- `sagefs check` (`SageFs/EnvCheck.fs`, `runAll` line 672) has no platform line. `checkMachineTier` (line 664) is the precedent for an informational one.
- Today a user learns the status only from one paragraph in `Readme.md` (line 73) and one bullet in `docs/how-hot-reload-works.md` (line 243).

## Design

### The evidence table

A new vertical slice `SageFs.Core/PlatformSupport.fs`, pure, no Option or bool state:

```fsharp
type OsName = Linux | MacOS | Windows
type CpuArch = X64 | Arm64
type Platform = { Os: OsName; Arch: CpuArch }

type PlatformCapability =
  | PlainDetour | GenericOwnBody | GenericSharedBody | InstanceMember | RealAppSave
  | PackagedToolSession | ThreadSandbox   // ThreadSandbox rows belong to getter-sandbox-beyond-linux

type Evidence =
  | Proven of leg: string * since: string
  | KnownBroken of issue: string * symptom: string
  | Unproven of why: string

module PlatformSupport =
  val table : (Platform * PlatformCapability * Evidence) list
  val evidenceFor : Platform -> PlatformCapability -> Evidence   // total: a missing row is Unproven "no leg runs here"
  val describe : Platform -> PlatformCapability -> string        // one line for `sagefs check`
  val ofRuntime : unit -> Platform                               // edge, from RuntimeInformation
```

`evidenceFor` is total on purpose. A platform nobody listed answers `Unproven`. The table lives in code because an installed tool cannot read `quality/`. `quality/definition-of-done.json` already carries run ids for client journeys; platform rows can cite the same run id and commit in `since`, and a ratchet checks that each `Proven` leg name exists in `main.yml` (see Tests).

`sagefs check` gets `checkPlatform ()` beside `checkMachineTier`, `Status.Pass` for `Proven`, `Status.Warn` with a hint for `Unproven` and `KnownBroken`.

### The platform lane

A new tier flag `--platform-proof` in `SageFs.Tests`, built the way the ratchet lane is built (`TestInfrastructure.Ratchet`, `Program.fs` line 242): a registry `PlatformProof.register` that selects existing tests by the identity of their bodies, so nothing moves out of the default suite and a typo cannot drop a test. It runs through `TrustSignal.run`, so zero ran is `NothingRan` (exit 3). AGENTS.md forbids a gate reachable only by a name filter; this is the sanctioned shape.

The lane registers, by reference:

| Cell | Existing test | Needs |
|---|---|---|
| plain static detour | `HarmonyCanaryTests` "detourMethod with canary confirms successful Harmony detour" | nothing |
| value-type generic body, shared generic body, instance member | the five cases of "the real detours" in `GenericReloadTests.fs` | nothing |
| entry point | `DetourEntryPointTests` | nothing |
| real app save | `WebAppHotReloadVerificationTests` "real file save hot-reloads a running module-declared app (save-driven, no restart)" (line 508, a `hostCaseTask`) | built FSI host, free ports, about a minute |

The first four rows run in seconds in one process. The last row boots a daemon, so it is a second lane flag, `--platform-proof --with-app`, and the leg runs the cheap lane first so a macOS failure shows in the first minutes.

### The pipeline

`ci-pipeline.fsx` gets one more token, `platform` (add it to `tierArgs`, read it with `hasTier`). New stages `platform build`, `platform proof`, `platform app save` and `platform tool smoke` carry `when' platformRequested`. Every Linux-only stage gets `when' (not platformRequested)`. A wiring test in the style of `PackagedToolSmokeWiringTests` fails if a stage declares neither, so a new stage cannot forget which world it belongs to.

`platform build` is: `restore harmony fork` (its `dotnet build` and `dotnet pack` are portable), then `dotnet build SageFs.Tests -c Release`. It skips samples, VS Code and the second framework. `platform tool smoke` packs, installs the nupkg to a throwaway `--tool-path`, and runs `scripts/smoke-test.fsx` against it, the same code path `packaged tool smoke` uses on Linux. The pack on a non-Linux runner is not the release artifact; the smoke exists to prove the install-and-first-session path on that OS, which is what Advent readers on Windows and macOS will do first.

### The workflow job

One new job `platform` inside the existing `main.yml` workflow:

```yaml
platform:
  strategy:
    fail-fast: false
    matrix:
      include:
        - { leg: macos-arm64,   runs-on: macos-15,          gating: false }
        - { leg: macos-x64,     runs-on: macos-15-intel,    gating: false }
        - { leg: windows-x64,   runs-on: windows-latest,    gating: false }
        - { leg: linux-arm64,   runs-on: ubuntu-24.04-arm,  gating: false }
  runs-on: ${{ matrix.runs-on }}
  continue-on-error: ${{ !matrix.gating }}
```

Hosted runners only: the self-hosted label is never used for a job a pull request can reach (a rule `TrustSignalTests` already enforces). The labels above are what I know of GitHub's public-repo runner set; I could not check GitHub's current list from here, so step 3 confirms each label before it is merged. `continue-on-error` is the answer to the publish coupling: while a leg is `Unproven`, a red leg shows a red mark on the job and does not stop `publish.yml`. The flip to `gating: true` happens in the same commit that sets the row to `Proven`, and a wiring test fails if a row is `Proven` while its leg is not gating, or the reverse.

### Wire and client impact

None on the daemon wire. `sagefs-vscode` and `sagefs.nvim` are untouched. The Readme and `docs/how-hot-reload-works.md` sentences change. A test (`PlatformDocsTests`) asserts that every `KnownBroken` issue id appears in the Readme and that the Readme does not use the word "proven" for a platform whose row is not `Proven`. None of these files is on the wire-surface list in AGENTS.md, so no commit here needs a `Plugin:` trailer.

Token cost: no new MCP tool.

## Tests

RED first, in this order:

1. `PlatformSupportTests`: "WHY - a platform nobody listed is Unproven, never supported". Asserts `evidenceFor { Os = MacOS; Arch = Arm64 } GenericSharedBody` equals `KnownBroken("#149", ...)` and an invented `{ Os = Windows; Arch = Arm64 }` equals `Unproven`. Fails to compile today (module missing), which is the proof of broken.
2. `EnvCheckTests`: `checkPlatform` returns `Status.Warn` with a hint naming the issue for macOS arm64 and `Status.Pass` for Linux x64.
3. Wiring: every stage of `sagefsPipeline` is declared Linux-only or all-platform, and every `Proven` row names a matrix leg that is `gating: true`.
4. The lane itself, run on the hosted macOS arm64 leg. Its first run is expected to be red with the #149 failures. That red run is the evidence the plan is after, and its log is the artifact.

Property tests: `evidenceFor` is total over `OsName x CpuArch x PlatformCapability` (enumerate the DU cases, no generator needed) and `describe` never returns an empty string.

Simulation: nothing in this item is a schedule or IO race, so no `SageFs.Simulation` entry. The probes of "did the new body run" already wait on the probe registry, and none of them waits on a clock. The lane waits on nothing it measures with a deadline: the real-app row uses the existing case, which awaits the app's own response.

Outcome gate and tiers: the matrix above, one row per `(platform, cell)`, in the `--platform-proof` tier. The default and `--integration-host` tiers stay as they are on Linux. The trust report prints one `TRUST tier=platform-proof leg=<leg>` row per leg.

## Steps

1. `PlatformSupport.fs` plus tests 1 and 2, table seeded with the facts above (Linux x64 `Proven`, macOS arm64 `KnownBroken "#149"`, everything else `Unproven`). Owns `SageFs.Core/PlatformSupport.fs`, `SageFs.Core.fsproj` entry, new test file. Green on Linux.
2. `checkPlatform` in `EnvCheck.fs` and `runAll`. Owns `SageFs/EnvCheck.fs`. Parallel with 1 after the types exist.
3. `PlatformProof` registry and the `--platform-proof` entry point, registering the four existing cases by reference. Owns `SageFs.Tests/TestInfrastructure.fs` (new module at the end), `Program.fs` dispatch, one registration line in each of `GenericReloadTests.fs`, `HarmonyCanaryTests.fs`, `DetourEntryPointTests.fs`. Run on Linux first: it must pass here before it is allowed to fail anywhere else.
4. `ci-pipeline.fsx`: the `platform` token, `platform build`, `platform proof`, `when'` guards, the wiring test. Owns `ci-pipeline.fsx` and its wiring test. Not parallel with 3.
5. `main.yml` job with the matrix, all legs `gating: false`, the nvim checkout and `SAGEFS_NVIM_DIR` steps copied from the `pr` job. First push is expected red on macOS arm64. Capture the five failure texts into the issue.
6. `--platform-proof --with-app` and `platform tool smoke` stages.
7. Diagnose #149 from the captured text (see Measure first). The fix is its own plan once the cause is known.
8. Flip each row to `Proven` and its leg to `gating: true` as it goes green. Update the Readme, `docs/how-hot-reload-works.md` and `docs/decisions.md` line 396. Steps 1 and 2 can be done by one sub-agent, step 3 by another, since their files are disjoint. Steps 4 and 5 are sequential.

## Measure first

- The exact failure text of the five tests on macOS arm64. Experiment: step 5's first run. If the hosted runner is green where tw0po1nt's Mac was red, the cause is the OS version (his is macOS 27 on SDK rc.1) or the machine, and the leg needs a second macOS image.
- Whether the failure is in detouring or in the stub. Experiment: two throwaway tests in a scratch branch, run on the leg: (a) `GetNativeMethodBody` of a plain static method returns a non-zero address and `CreateNativeDetour` between two plain static methods redirects a call; (b) a native detour of the shared body of a generic method over a reference type to a hand-written stub with the shared body's signature (hidden argument included) that only records the argument it receives and returns a constant. If (a) fails, the problem is the Harmony fork on that CPU or macOS's write-protected code pages. If (a) passes and (b) shows a wrong or zero argument, the stub's `self; hidden; visible` order is wrong for arm64. If both pass, look at `GenericReload.sharedStub`'s `calli` and then at `resolveJitCodeAddress` and the canary.
- Whether `macos-15` hosted runners match a real M-series Mac for this purpose. Experiment: the same lane on tw0po1nt's machine, once, from the same commit.
- Wall time of the leg on a 3 core macOS runner. If `platform build` passes 40 minutes, drop the smoke into its own matrix cell.

## Risks and dependencies

- Publish coupling (above). If `continue-on-error` turns out to hide a real regression, make `gating: true` the default as each row proves.
- Harmony fork build on macOS or Windows may fail before any test runs. That result answers the question for that platform and goes in the table.
- The Windows leg shares the pipeline with `start-on-login-windows` (its install test runs there) and `getter-sandbox-beyond-linux` (it needs the `ubuntu-24.04-arm` leg to verify the arm64 filter). Both depend on step 5.
- `admit-heavy-work-by-measured-cost` needs the platform legs to learn whether machine memory is readable off Linux.
- What would change my mind: if the lane on macOS arm64 is green on a hosted runner, #149 may be specific to macOS 27 or SDK rc.1, and the plan shrinks to proving the matrix and closing the issue as "not reproduced on macOS 15".

## Size

M. The table, the check line and the lane are days; the pipeline split and a first useful macOS result are another week or two, and the #149 fix is not counted.

## Decisions for Will

- Run the platform legs on every push and pull request, or only on push to master and manual dispatch? I recommend push and dispatch, plus pull requests that touch `SageFs.Core/Middleware/**`; public-repo hosted minutes are free, but macOS queues can be long.
- `continue-on-error` while a row is unproven, or a separate scheduled workflow that cannot affect `publish.yml`? The house rule is that new coverage is a stage of the one pipeline, so I recommend `continue-on-error` with the `gating` flip tied to `Proven`.
- Table in F# (read by `sagefs check`) or rows in `quality/definition-of-done.json`? I recommend the F# table, citing the same run ids.
- Is a macOS 27 beta host worth a self-hosted runner? I recommend no. One run on tw0po1nt's machine is enough.
