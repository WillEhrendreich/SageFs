# SageFs: Coding Agent Guidelines

## STOP: Read This Before Anything Else

**The SageFs daemon is a long-running process that never exits on its own.** It hosts the FSI session, MCP server, and dashboard on ports 37749/37750. You will be tempted to wait for it. DO NOT.

**The cardinal rule, stated three times because it is the only thing you keep getting wrong:**

1. **NEVER narrate a wait to the user.** After starting a daemon, do not write "verifying" or "checking" or "started" and then stop. The next thing you produce must be a tool result: specifically a screenshot, an HTTP probe with a hard timeout, or the next concrete step. A chat message that is not a result or a question is the failure mode.

2. **NEVER call a command that blocks on the daemon's lifetime.** `Wait-Process`, `Start-Process -Wait`, waiting for a process to exit, `taskkill /T /F` on the daemon while also awaiting the result, all of these will hang forever because the daemon is not supposed to exit.

3. **NEVER treat `Start-Sleep` as "wait for the daemon to be ready" without a follow-up tool call in the same turn.** `Start-Sleep 3` followed by a chat message is the same hang, just shorter. `Start-Sleep 3` followed by a screenshot is fine. The sleep is not the problem. The text after the sleep is the problem.

4. **NEVER re-derive an MCP connection from scratch. Run `dotnet fsi scripts/reconnect-mcp.fsx`.** If every MCP tool answers `404 Session not found`, the connection is live and points at a session the daemon no longer has (installing a build restarts the daemon, so the client's id dies). The script re-initializes and proves it; `OK` means you are live. The durable fix is the stdio bridge, `claude mcp add sagefs -- sagefs mcp`. Details: "404 Session not found" in `docs/mcp-tools.md` and `skills/sagefs/troubleshooting.md` (which also says to check the daemon's version first).

5. **NEVER hand-roll an MCP client over HTTP, and never script an edit with `python3 -c`.** A hand-rolled client initializes fine, then hangs on `tools/call`, because the bridge writes diagnostics to stderr and an unread pipe fills and blocks it. Drain stderr on its own thread and read until the response ID you asked for, since responses are SSE-framed and a fixed line count deadlocks. For edits use the exact editor tool (`skills/sagefs/design.md`).

6. **A tool that never answers is a BUG in the product, not a timeout to work around.** A scope-colliding `join_cohort` leaves the bridge alive and streaming notifications while producing NO response at all, so the caller's only signal is a hang. An agent that reads that concludes "hard limit, no workaround", which is a wrong conclusion produced by a missing answer. If a tool call you made produced no response, say so and treat it as a defect; do not re-derive the answer from the absence.

7. **A lazy registry's "on create" hook misses everything seeded before it.** `CohortOwners.SetOnOwnerStarted` fires when an owner is created. `DaemonMode.fs` seeded the daemon's own owner about 100 lines before registering the hook, so that cohort had no subscriber and the dashboard's cohort panel froze after its first render. Later scopes worked, which made it read as a stale-count bug. Subscribe anything created before the hook by hand, and if the hook body is more than a line, name it and call it from both places.

8. **A count can measure a different collection than readers assume.** `CohortFrame.MemberIds` holds every member a cohort has ever seen (departed members stay so their claims resolve); `MemberSeat` says who is present. Both renderers counted `MemberIds` and showed "1 member" beside a VACANT conductor. Fixing that broke two other things, pinned in `CohortPanelTests`: `ClaimHolderIndex` indexes `MemberIds`, and the claims list sits inside the "has members" branch. Before changing a count, grep every use of the old collection's length: it is also a loop bound and an index bound.

9. **A failing "the fixture assembly loads" assertion describes the fixture.** Read the assertion's subject before concluding the feature under test is broken. The inlined-callee hot-reload case died there because an `AssemblyBuilder` assembly reports an empty `Location`; its fixture is now a real `.dll` written with `Mono.Cecil`.

## Concrete patterns:

- Starting the daemon: one `Start-Process ... -WindowStyle Hidden` (no `-Wait`), one `Start-Sleep -Seconds 3` for warmup, then the next tool call is the screenshot. Nothing in between.
- Verifying the daemon is up: `Invoke-WebRequest -TimeoutSec 3` with a hard timeout. If it returns, great. If it throws a timeout exception, kill the request and report the state. Do not retry indefinitely.
- Killing the daemon: `Get-Process -Name "SageFs" | Stop-Process -Force` returns immediately. Never combine that with a `Wait-Process`.
- The "is it up" check is the screenshot. Not a chat message, not a sleep, not a status probe. The screenshot.

**If you catch yourself writing a sentence that contains "waiting", "let me check", "verifying", "starting up", or "should be ready"** between starting a process and your next tool call, stop. Skip the sentence. Make the tool call.

**You have failed this rule on the very first turn of this session, and on the turn immediately after being told about it, and on multiple turns after that. The next failure is a refusal to do the work, not a sentence of acknowledgment.**

## The inner loop: the SageFs REPL, always

Load and follow [`skills/sagefs/SKILL.md`](skills/sagefs/SKILL.md) before
touching F#. The short version: the SageFs REPL (MCP) is the inner loop, and
`dotnet build` / `dotnet test` is the final gate only. If you brief a
sub-agent, put the loop in the brief. Sub-agents don't inherit it.

Working on SageFs itself has two extra catches:
- **Build before `create_session`.** A session loads compiled output. If it
  still fails with "Not all DLLs are found" after a build, treat it as a SageFs
  bug and report the paths it names. SageFs.Core, SageFs.Host and SageFs
  multi-target net10.0;net11.0, and 0.6.782 resolved those references wrong,
  so a session on SageFs.Tests faulted even when everything was built.
  **In this repo that bug is yours to fix, not to route around.** On 2026-10-18
  about twenty agents hit it in fresh worktrees (#153), took the `dotnet build`
  escape hatch, and reported it, each correctly by the letter of the skill, and
  the hatch quietly became the normal path. So if a session on this repo will
  not load, stop and fix the cause (or file it with the exact paths) before you
  carry on with the task; do not build around it.
- **The daemon runs the build you are working against, always.** A lagging daemon costs signal: sessions
  refuse on a version mismatch, `hard_reset_fsi_session rebuild=true` fails on bugs already fixed, and a
  merged fix that is not running looks broken. Run `dotnet fsi scripts/install-local.fsx -- --status` at the start of
  work. If it says master is ahead, run `dotnet fsi scripts/install-local.fsx -- --build --force` (or pass a
  `<sha>` with `--force` to install the nupkg a gate built). It installs the package as the global tool, restarts the
  daemon and reads the version back. Open sessions are dropped on purpose, and nobody asks first:
  upgrading the daemon always wins. `scripts/ship.fsx` does this itself after the gate and before the push.
- **Self-hosting skew.** A worktree's `SageFs.Core` can be newer than the
  installed daemon (the daemon is whatever was last published). A
  session that loads Core can then refuse with a version mismatch, or a
  "type not found, Version=..." error. That's a known SageFs bug. Report the
  exact error, work in a session that doesn't load Core if you can (pure files
  can be `#load`ed into any session), and only then fall back to `dotnet` for
  that step. Never silently.
- **A project's own `bin` can hold a STALE `SageFs.Core.dll`, and the symptom
  hides.** A project that references SageFs.Core carries a copy of it in
  `bin/<config>/<tfm>/`, and MSBuild only refreshes that copy when it thinks
  it must. Change SageFs.Core, build the daemon, and the project's copy can
  still be the old build: same version, different code. The daemon adopted it
  (the adoption check compared the version, which matches) and the worker died
  with a `MissingMethodException` for a method the daemon's own code calls. The
  session then never reached Ready, and the only trace was a stack trace in a
  worker log: a 120-second timeout with nothing to read. The daemon now
  refuses that candidate with a message that says which file to rebuild, and it
  refuses it in seconds. If a session on this repo hangs in warmup, check the
  timestamp on the project's `bin/**/SageFs.Core.dll` against the tree's before
  you read anything else.

## Editing: exact calls, and never a bulk rewrite

The loop above is for CODE. This is for CHANGING it, and the rule is narrow on
purpose: a scripted edit that might land wrong is worse than no edit, because a
silent no-op reads exactly like a successful one.

- **No `sed -i`, no `python3 -c` file rewrites, no regex bulk edits.** They
  rewrite what they did not read, they swallow the one file that differed, and
  they cannot tell you which of thirty call sites they actually changed.
- **Use the editor tool for a specific change**: read the region, then replace
  that exact text. A call that did not match is a bug to investigate, not
  something to widen until something sticks.
- **Scripting is F#, and it is dogfooded.** A repeated mechanical change goes
  in an `.fsx` run through SageFs, because the script is then F# you can
  evaluate, and it reports its own result. It MUST assert that each
  replacement matched, and say so out loud, rather than exiting quietly.
- **Verify the diff after any scripted change.** A pattern that matched in
  4 files and silently missed a 5th is the failure mode this rule exists to
  prevent.
- **Gates run in a background agent you do not block on.** The release gate is
  a decision, not an iteration tool; re-rolling it to escape a flake costs
  minutes and proves nothing. Launch it, keep working, read the result when you
  need it. Never poll it in a loop, that is the same waste as blocking.
- **Never `#load` an already-loaded project.** `#load`ing a Core file into a
  session that has Core loaded produces two copies of every type, and the
  symptom is a misleading incompatibility error
  (`The type 'FSI_0035.SageFs.H' is not compatible with the type 'SageFs.H'`),
  not a load failure. Use the loaded types directly; `#load` only a PURE file
  with no dependency on the loaded project.
- **Prove the change in the REPL before you write it to a file.** An eval is
  under a second; a build that catches your misread type is two minutes, and it
  catches it *after* you have edited. The full sequence, prove, persist, then
  `hard_reset_fsi_session rebuild=true` to re-verify, is in
  [`skills/sagefs/editing.md`](skills/sagefs/editing.md) under "Editing: prove
  it, then write it". Read that section before your first edit in a session.
- **"I can't verify that in the session" is a conclusion you have not earned
  yet.** A session that loads `SageFs.Core` has Core's whole dependency closure
  loaded with it. A Fantomas/FCS type that Core walks is in the live session, and
  `AppDomain.CurrentDomain.GetAssemblies()` will name it:

  ```
  PROBE Fantomas SynField = Range:Range | ... | fieldType:SynType | idOpt:...
  ```

  That answer took **seven `dotnet build` cycles** to get by fighting the
  compiler about it, and **one `send_fsharp_code` eval** to get by asking. If
  you catch yourself reaching for a build to find out what a type looks like,
  stop: reflect over the loaded assemblies instead. Use `dotnet build` to check
  that an edit COMPILES, never to discover what a type IS.
- **`Editing.fs` is the executable form of the rules above.** If you are about
  to do a repeated mechanical change, `SageFs.Editing.applyInOrder` is the
  total-or-reported version: a replacement either applies or says why it did
  not, an ambiguous find refuses rather than guessing, and the find text
  travels with the refusal. Prefer it to a hand-rolled loop.

## Instructions for agents in this repo

- **`AGENTS.md` is the single source of truth.** `CLAUDE.md` is `@AGENTS.md`
  plus an auto-synced taste mirror; the `@` import means the live instructions
  always arrive. Do not duplicate rules between the two, that is how they
  drift. The skill, not this file, is what loads automatically on an F# task,
  so anything an agent must obey *before its first edit* belongs there.


## Project Overview

SageFs is F# Interactive that already has your project loaded, re-runs the affected tests on unsaved edits, and patches the running app on save. VS Code, Neovim, a web dashboard and an MCP server for agents all share its sessions. A daemon hosts persistent, isolated F# Interactive sessions, one worker process each.

The built-in SageTUI client, legacy TUI, and `SageFs.Gui` Raylib frontend are deprecated. Do not treat them as current product surfaces or add new product documentation for them. Preserve Raylib application and game demos because they demonstrate SageFs support for game projects and are independent of the deprecated GUI frontend.

The Visual Studio extension (`sagefs-vs/`) is deprecated and no longer built, tested, or published, do not treat it as a current product surface, do not add new product documentation for it, and do not route new engineering effort into it.

## Language & Stack

- **Primary language**: F# (functional programming)
- **Target framework**: `net11.0` (the shipped tool closure. SageFs, SageFs.Core, SageFs.Host, multi-targets `net10.0;net11.0`; see `Directory.Build.props`)
- **Solution format**: `.slnx` (not `.sln`)
- **Web framework**: Falco (functional web framework for ASP.NET Core)
- **HTML rendering**: Falco.Markup
- **Real-time UI**: Falco.Datastar (SSE-based)
- **Testing**: Expecto (behavior-driven, property-based with FsCheck)
- **Snapshot testing**: Verify
- **Persistence**: Binary manifest format (.sagefm) for session and test state
- **Package management**: Central package management via `Directory.Packages.props`

## Critical Coding Standards

### Indentation
- **ALWAYS use 2 spaces**, never 4 spaces: this is non-negotiable across the entire codebase.

### One home for each kind of decision (ratcheted, never magic)

A value that several places must agree on has one named home, and a test counts the places
that still decide for themselves. Each count only goes down. A file that is not in a
budget table has a budget of zero, and a budget above the real count is itself a failure.

- **Durations.** `Timeouts` (`SageFs.Core/Timeouts.fs`) is the product's. A constant says
  what the wait is FOR and why that long, is named for that purpose and never for its
  value, and takes an `envOrDefault` override only if an operator could need to tune it.
  A test takes a production duration from `Timeouts`, never from a copy. A duration a test
  picks on purpose lives in `SageFs.Tests/TestTimeouts.fs` (`TestTimeouts`, and
  `FixtureDurations` for made-up result durations that nothing reads back). No bare
  `TimeSpan.FromSeconds 20.`, `Task.Delay 250`, `60_000` or `timeout = 5000` at a call site.
  `TimeoutLiteralsTests` is the ratchet.
- **JSON.** `SageFs.Json` (`SageFs.Core/Json.fs`) is the one place that serializes and
  deserializes. A call site names a profile (naming, layout, nulls, union encoding) and never
  builds a `JsonSerializerOptions` or calls `JsonSerializer` itself. Every profile carries the
  F# converter, so a value is the same text on .NET 10 and .NET 11 (.NET 11 writes an F#
  union and .NET 10 throws). Read a record that clients may send with optional fields left
  out with the `omitNulls` profile. `JsonCentralizationTests` is the ratchet.
- **Closed sets of strings** are discriminated unions with one exhaustive to-string function.

### Package References
- **NEVER** include `Version` attributes in `<PackageReference>` elements in `.fsproj` files.
- All versions are defined centrally in `Directory.Packages.props` at the repo root.

### Commit Messages
- Use **Conventional Commits** format: `type(scope): description`
- Types: `feat`, `fix`, `refactor`, `docs`, `test`, `chore`, `perf`, `style`, `ci`, `build`

### Writing: no slop
Will finds every Claude-ism gross, and none of them outranks another: negative parallelisms,
staged openers, staged emphasis, closing beats, weasel attribution, fake-insight tails, marketing
register, stock phrases, transition crutches, filler grammar, hyphenated clichés, business jargon,
template residue, and the landing beat. The full rules with examples are in `~/.claude/CLAUDE.md`
under "Writing: no slop", and the catalogue with replacements is the `plain-english` skill
(`~/.agents/skills/plain-english/SKILL.md`). They apply to every kind of prose an agent writes: docs,
release notes, commit messages, code comments, issue text, PR descriptions, chat replies.


### F# Style
- Favor immutable types, discriminated unions, pattern matching, and pipeline operators (`|>`)
- Use `Result<'T, 'TError>` for operations that can fail
- Keep domain logic pure: side effects only at system edges
- Small, composable functions with clear intent

### Testing
- Tests use Expecto with `Expecto.Flip`, **message is always the first argument**:
  ```fsharp
  actual |> Expect.equal "should be 42" 42
  actual |> Expect.isTrue "should be true"
  ```
- Run tests via the SageFs REPL, not `dotnet test`
- Run the ratchet lane after any edit and before committing: `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`. It runs only the ratchets (the budget, literal-count, stale-generated-page and CI-wiring tests, registered by reference with `Ratchet.register` in `TestInfrastructure.fs`) and takes seconds after the build. The release gate and `scripts/ship.fsx` run the same lane first and stop on red, so a red ratchet costs minutes, not twenty. A budget that went down is lowered by `--ratchets --tighten`, never by hand.
- Property-based tests (FsCheck) are preferred over example-based tests

#### Filters: `--filter-test-list` matches LISTS, `--filter-test-case` matches LEAVES

Expecto exposes three filter flags and they do **not** mean the same thing:

| Flag | Matches against |
|---|---|
| `--filter <path>` | a slash-separated hierarchy prefix |
| `--filter-test-list <substring>` | **`testList` names only** |
| `--filter-test-case <substring>` | **leaf case names only** |

If the token you are filtering on lives in the `testList` name and not in any leaf
case name, `--filter-test-case` matches **nothing**, and the run still prints
`Failed: 0, Errored: 0` and **exits 0**. This has already happened here: an agent ran
`--filter-test-case "roast-8"` against a list literally named for `roast-8` whose eight
cases all begin `"WHY — "`, read the green result, and concluded the behaviour was
covered. Nothing had executed. Exit 0 means *nothing that ran failed*; it says nothing
about what was excluded.

**Now enforced, not just documented.** Every tier (default, `--integration-host`,
each dedicated entry point, `--mutation-score`) runs through
`TestInfrastructure.TrustSignal.run`, which reads Expecto's own summary and prints
one `TRUST tier=… registered=N ran=N … verdict=…` line:

| Verdict | Meaning | Exit |
|---|---|---|
| `Trusted` | unfiltered, everything registered ran, nothing failed | 0 |
| `NarrowedRun` | passed, but a filter/`--run`/`--stress` narrowed it, inner loop only | 0 |
| `TestsFailed` | something failed or errored | 1/2 |
| `NothingRan` | zero tests executed (the trap above): filtered or not | **3** |
| `CountMismatch` | unfiltered, but ran ≠ registered | **3** |

CI runs every test stage even after one goes red, collects each tier's row in a
ledger (`SAGEFS_TRUST_LEDGER`), and its final `trust report` stage prints one
table and fails on any tier that is not Trusted, including a tier whose process
died before reporting (`NoReport`). `TrustSignalTests` fails the fast suite if a
registered tier is not invoked by `ci-pipeline.fsx`, or if a test run there
bypasses the ledgered `testTier` step. Read the table, not a stage colour.

The packed tool is smoked too, past `--version`: the `packaged tool smoke` stage (the `release` leg) installs the nupkg it packed under each SDK and runs `scripts/smoke-test.fsx` against that install (its own daemon on a free port pair, a session on the C# sample, completions, live testing), and any red step fails the pipeline. It is a stage and not a ledgered tier because the ledger's rows come from the Expecto process and the trust report is judged before the nupkg exists; `PackagedToolSmokeWiringTests` fails the fast suite if the stage stops invoking the script.

The default suite runs on every framework the tool ships for, because the net10
tool asset is the one most users install and a bug that exists on .NET 10 only
(a raw F# union reaching System.Text.Json, which .NET 11 writes and .NET 10
throws on) is invisible to a net11 run. Each framework is its own tier and its
own trust row: `default` on net11, `default-net10` on net10. The `build other
frameworks` stage (after `ratchets`) builds the net10 test assembly with
`TierPlan.testBuildCommands`, which leaves the
tracked lock files and the net11 `obj/` alone. It compiles only the test
assembly, against the shipped closure that `build/GateProducts.proj` built for
both frameworks, once `TierPlan.seedScript` has copied that closure's
intermediate outputs into the private obj directory. To run it by hand, first
build net11 with `dotnet build SageFs.Tests -c Release`, then run these five
commands in order from the repo root. The first one writes the net10
ReadyToRun report under `SageFs/obj/Release/net10.0/`, which the net10
`HostManifestTests` case reads. `BuildPlanTests` compares this block with what
`TierPlan` produces and fails on any difference, so it cannot drift:

```
dotnet build build/GateProducts.proj -c Release
dotnet restore SageFs.Tests -p:TargetFramework=net10.0 -p:NuGetLockFilePath=obj/tier-net10.0/packages.lock.json -p:RestoreLockedMode=false -p:BaseIntermediateOutputPath=obj/tier-net10.0/
sh -c 'test -d SageFs.Core/obj/Release/net10.0 && rm -rf SageFs.Core/obj/tier-net10.0/Release/net10.0 && mkdir -p SageFs.Core/obj/tier-net10.0/Release && cp -a --reflink=auto SageFs.Core/obj/Release/net10.0 SageFs.Core/obj/tier-net10.0/Release/net10.0 && test -d SageFs.Simulation/obj/Release/net10.0 && rm -rf SageFs.Simulation/obj/tier-net10.0/Release/net10.0 && mkdir -p SageFs.Simulation/obj/tier-net10.0/Release && cp -a --reflink=auto SageFs.Simulation/obj/Release/net10.0 SageFs.Simulation/obj/tier-net10.0/Release/net10.0 && test -d SageFs/obj/Release/net10.0 && rm -rf SageFs/obj/tier-net10.0/Release/net10.0 && mkdir -p SageFs/obj/tier-net10.0/Release && cp -a --reflink=auto SageFs/obj/Release/net10.0 SageFs/obj/tier-net10.0/Release/net10.0 && test -d SageFs.Host/obj/Release/net10.0 && rm -rf SageFs.Host/obj/tier-net10.0/Release/net10.0 && mkdir -p SageFs.Host/obj/tier-net10.0/Release && cp -a --reflink=auto SageFs.Host/obj/Release/net10.0 SageFs.Host/obj/tier-net10.0/Release/net10.0'
dotnet build SageFs.Tests -c Release --no-restore --no-dependencies -p:TargetFramework=net10.0 -p:NuGetLockFilePath=obj/tier-net10.0/packages.lock.json -p:RestoreLockedMode=false -p:BaseIntermediateOutputPath=obj/tier-net10.0/
dotnet SageFs.Tests/bin/Release/net10.0/SageFs.Tests.dll --summary
```

Do not run `dotnet restore -p:TargetFramework=net10.0` in a working tree: it
rewrites `packages.lock.json` with only the net10.0 sections. Only the default
suite runs on net10; the integration, mutation and browser tiers stay on net11.

Consequences, in order of importance:

1. **A filtered run is never the acceptance check.** A gate is done when its own test
   name appears in the output of a real, **unfiltered** run, `dotnet {testDll} --summary`,
   or the relevant whole-suite entry point. Filters are for the inner loop only.
2. **Never add a gate that is only reachable by a name filter.** Put it in the default
   suite as a plain `[<Tests>]` value, or select it structurally through the
   `Integration` registry in `TestInfrastructure.fs` (reference-based exclusion, which a
   typo cannot defeat). CI itself uses no filter expressions, every stage runs a whole
   suite. Keep it that way.
3. **Do not put tracking tokens (`roast-N`, bug ids) in `testList` names.** Put them on
   the leaf cases where a case filter can see them, or leave them out entirely.

## Project Structure

```
SageFs.Core/       — Shared engine, session, testing, persistence, and protocol logic
SageFs/            — CLI tool, daemon, MCP server, dashboard, and retained deprecated TUI source
SageFs.Gui/        — Deprecated Raylib product frontend retained as legacy source
SageFs.Tests/      — Expecto test project
sagefs-vscode/     — VS Code extension (Fable F#→JS)
sagefs-vs/         — Deprecated Visual Studio extension (C# + F#), retained as legacy source
docs/              — Markdown docs, linked from Readme.md and shown on sagetech.dev/sagefs (that site is the SageTech repo; GitHub Pages is retired)
```

The Neovim plugin lives in a separate repo, `WillEhrendreich/sagefs.nvim` (checked out at `~/Work/sagefs.nvim`), only so Neovim distribution works. It is a first-class part of SageFs: when a need shows up there, fix and improve it like any other project (busted spec first, verified in real Neovim against the daemon), push it once it is verified, and keep it from falling behind. Its version always equals the SageFs release: `scripts/ship.fsx` runs `scripts/sync-nvim-version.fsx` after each release push, which bumps, tests and pushes the plugin. The version is only a marker, because the plugin reaches users commit by commit. What decides compatibility is the daemon's `apiVersion` against the `api_range` in the plugin's `lua/sagefs/compat.lua`, and `scripts/ship.fsx` refuses to release (`scripts/sync-nvim-version.fsx -- --compat`) when the daemon speaks a version the plugin does not declare. A wire change lands in the plugin first.

The plugin stays its own repo (so it can be distributed to Neovim users), but it moves with SageFs: when the daemon gains something a Neovim user would want (a new tool, endpoint, resource, field, status or event), the plugin takes advantage of it in the same stretch of work, not later. Every commit that touches the wire surface (`SageFs.Core/EndpointContracts.fs`, `SageFs/McpTools.fs`, `McpServer.fs`, `McpLeaseWire.fs`, `DashboardTypes.fs`, `SageFs.Core/SessionOperations.fs`, `SessionStatusPayload.fs`, `docs/mcp-tools.md`) carries a trailer, `Plugin: done <what changed in sagefs.nvim>` or `Plugin: n/a <why it needs nothing>`, and `scripts/ship.fsx` refuses a release with one missing (`scripts/sync-nvim-version.fsx -- --impact`). Commits at or before `scripts/plugin-impact-baseline` are exempt. Sub-agent briefs that change the wire surface say so.

## Build & Test

```bash
dotnet build           # Build all projects
dotnet test            # Run tests (CI only — prefer SageFs REPL locally)
dotnet pack SageFs -o nupkg  # Package the CLI tool
```

The release gate runs detached, so a shell list shows only the command and the output is in
files. `dotnet fsi scripts/gate-tail.fsx -- --status` prints each tier, running or finished,
with its size and its last line; with no arguments it follows every tier live; a tier name
follows one. It only reads, so Ctrl-C ends the viewer and never the gate.

## Multi-agent / worktree sessions

- **Sessions are checkout-aware.** A session's working directory is classified against the filesystem (`SageFs.Checkout.classify`, no `git` subprocess): a plain repository, a git **worktree** (its own root and branch, worktrees have a `.git` FILE, not a directory, pointing at the main checkout's `.git/worktrees/<name>` admin dir), or not a git checkout at all. `list_sessions` and the dashboard show a worktree session's branch.
- **A git worktree is a routing boundary.** If you are working inside a worktree (e.g. `.claude/worktrees/agent-x`) and no session exists for it yet, tool calls resolve to `Gone` with a create hint, they never silently fall back to a session rooted at the main checkout, even though your directory is textually nested under it. Create a session for the worktree; do not assume the main checkout's session is yours to use.
- **For a project the running daemon already serves, create a session in it.** Only spawn a second daemon when you are testing daemon code itself (changes to `SageFs.Core`/`SageFs`/`SageFs.Host`) that the running daemon cannot execute because it predates your change, and then give that daemon an explicit owner/TTL rather than leaving it to leak.
- **Identity is bound to your MCP connection, not to the `agentName` you pass.** Two different connections that happen to declare the same `agentName` are tracked as two separate members, you cannot see or clear another connection's active session by reusing its name.

## Orchestrator hygiene

Agents leave things behind: worktrees, merged branches, the gate's checkouts and pass records, built FSI hosts,
test temp dirs, orphaned processes. One machine ended up with a hundred worktrees (150 GB) and a 172 GB gate
dir that nothing ever removed. SageFs sees the mess and tells you what it is and what is safe to remove. Keeping
the workspace tidy is the orchestrator's job, and these are the calls that do it.

- **Before you spawn,** look: call `get_workspace_hygiene`, or read the `workspace:` line that
  `create_*_session`, `get_session_status` and `get_daemon_status` add once a repo has a pile (more than a
  handful of leftover worktrees, or a gate dir past 40 GB). If there is already a pile, tidy it first.
- **After an agent's work merges,** call `get_workspace_hygiene`, read the dry-run plan, then call
  `tidy_workspace` with `confirm=true` and the plan id. That removes its merged worktree and branch. From a
  shell it is `sagefs hygiene` for the plan and `sagefs hygiene --tidy` for the safe part.
- **In every sub-agent brief,** say: finish by removing nothing you do not own, and report your worktree path
  and branch so the orchestrator can reap them. Sub-agents do not tidy up after themselves, and they do not
  tidy up after each other.
- **What tidy will and will not do.** It removes what is merged (by ancestry, by rebase or squash, or by an
  identical tree), what only differs by build output, what an owner that is gone left, and what has expired.
  It never touches a worktree with uncommitted work, a branch with commits the base lacks, anything a session,
  lease or process is using, anything it could not judge, or any path outside the directories SageFs manages.
  Those are listed with the command that saves the work first (a patch, or a branch), and you run it
  yourself. A plan that changed since you looked is refused, and every step looks at its target again right
  before it acts.
- **Who made it.** A session records which MCP connection created it and where, so a plan can say "made by
  agent X, which is gone". The connection is the identity; the name an agent gives itself is only a label.

## Architecture Principles

- **Current clients**: VS Code, Neovim, the web dashboard, and MCP use session-scoped daemon contracts
- **Web dashboard**: Falco.Datastar and SSE provide browser-based session control and observability
- **Binary persistence**: Session/test state via CRC-validated binary manifest (.sagefm)
- **CQRS**: Separate read/write models
- **Vertical slices**: Features as single files for locality of behavior
- **Daemon architecture**: Long-running FSI session with MCP server for editor communication

## Things to Avoid

- Do not introduce new NuGet dependencies without discussion
- Do not change the indentation style (2 spaces)
- Do not use `dotnet test` for local development, use the SageFs REPL
- Do not modify `Directory.Build.props` version numbers. Nothing bumps on commit: `scripts/ship.fsx` bumps once per push, and the pre-push hook (`scripts/pre-push.fsx`) refuses a master push that doesn't raise the version
- One push to master is one release. The pipeline publishes the version at the tip, so a push carrying two release bumps loses the first one silently: 0.6.920, 0.6.921 and 0.6.922 were committed and pushed this way and never reached NuGet. The pre-push hook refuses a range that carries more than one release commit
- Do not add Version attributes to PackageReference elements
