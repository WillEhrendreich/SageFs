# Plan a cohort as F# code

Roadmap item: `plan-a-cohort-as-code` (Exploring, Agents). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S for the builder, M with the applier |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: two members claiming one file give `ClaimsOverlap`. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## Work order

Written 2026-11-11 against master 8975e898. Every `path:line` below was read on that commit; the sections under this one are the original plan, corrected where it had gone stale.

### 1. Outcome

You write a cohort plan as an F# value in a SageFs session. `CohortPlan.tryBuild` refuses it, naming both members and both canonical scopes, when two claims overlap, when a claim escapes the repository, when a member name repeats, or when a member's grant is wider than the conductor's. An accepted plan becomes the ordered list of `mint_member` and `acquire_claim` calls an orchestrator makes, and `CohortPlan.rehearse` proves the real `Cohort.decide` accepts each one. Today the orchestrator writes that list in a brief by hand (`demo-actors-plan.md`, untracked at the repo root) and learns about an overlap when the second agent's `acquire_claim` answers `ClaimConflict`.

Outcome gate. `CohortPlanTests` (default suite, pure) covers the matrix in "What done looks like" against `tryBuild` and `rehearse`. `CohortPlanOutcomeTests` (`--integration-host`) applies an accepted plan to a real daemon under `SAGEFS_IDENTITY_POLICY=TokenRequired` and reads `get_cohort_status`: the held claims equal the plan's claims and nothing else. Linux is the whole proven matrix. The pure half has no platform. The daemon half has nothing platform-specific in it and no run on Windows or macOS.

### 2. Where the code is today

- `ClaimScope` is `File | Project` (`SageFs.Core/Cohort.fs:97`); `PathRefusal` is `Absolute | EscapesRoot` (`:101`). `ClaimPath.tryCanonical` on a string is `:121`, `ClaimScope.tryCanonical` is `:155`, `ClaimScope.overlaps` is `:175`. `Purpose.tryCreate` bounds a purpose to one line of 200 characters (`:193`).
- `Cohort.decide clock entropy state command` is `Cohort.fs:2119`. `CohortState.forScope` is `:562`. `AcquireClaim of who * scope * purpose * cohort` is a case of `CohortCommand` (`:644`, 25 cases today). Empty entropy is accepted (module doc, `:69`).
- Grants (`SageFs.Core/Capability.fs`): `Grant = { Preset; Scope: ScopePrefix; Route; NotAfter }` (`:619`), `Widening` (`:630`), `Grant.isNarrowerOrEqual` (`:649`), `Grant.wideningsOf requested minter` (`:656`), `RolePreset` (`:213`), `RolePreset.toolClasses` (`:263`), `ScopePrefix` with a private constructor and `tryParse`, `isWithin`, `covers` (`:46-95`), `RouteBinding.ofRequest sessionId workingDirectory` (`:362`), `Capability.admitClaim grant scope` (`:898`). `ScopePrefix.commonOf` does not exist; step 2 adds it.
- Token transport: `CapabilityTransport.headerName`, `metaKey`, `bridgeEnvVar`, `policyEnvVar` (`Capability.fs:1004-1018`). `SageFs.Tests/CapabilityHttpIntegrationTests.fs:35-59` connects with the SDK's `McpClient.CreateAsync` and calls tools with `_meta`; `CohortMcpToolsIntegrationTests.fs:185` has `withDaemon`.
- Coverage input for `propose`: `SageFs/DaemonMode.fs:3027` builds `coveredFilesOf` from `InputHashCoverage.selectionFiles` (the plan said line 2975; it moved).
- Nothing named `CohortPlan` exists in `SageFs*`, `tools` or `scripts`. `tools/sagefs-call.fsx` does not exist, tracked or untracked; the reference below is wrong and is left as history.
- Compile order: `Capability.fs` is `SageFs.Core/SageFs.Core.fsproj:73`; the new slice goes after it. `CapabilityTests.fs` is `SageFs.Tests/SageFs.Tests.fsproj:1105`; `Program.fs` is `:1194` and every test file must come before it.
- Tests that must stay green: `CapabilityTests.fs`, `ClaimPathTests.fs`, `CohortPropertyTests.fs`. This item does not touch `Cohort.fs`.

### 3. Steps

Each step: RED test committed red with the step number, then the smallest change, committed green.

1. **Pure.** `SageFs.Tests/CohortPlanTests.fs`, list `"CohortPlan: overlap and escape"`: two members claiming `File "src/A.fs"` and `File "src/../src/A.fs"` give `ClaimsOverlap` naming both members and both canonical scopes; a `File` under another member's `Project` directory is refused; `../x.fs` is `ClaimEscapesRepo`; a repeated name is `DuplicateMember`; a plan with three mistakes returns three refusals. Add `<Compile Include="CohortPlanTests.fs" />` after line 1105 of `SageFs.Tests.fsproj`. Then `SageFs.Core/Features/CohortPlan.fs` (`PlanMember`, `PlanRefusal`, `CohortPlan` with a private constructor, `tryBuild` for overlap, escape and duplicates only), added to `SageFs.Core.fsproj` after line 73.
2. **Pure, `Capability.fs`.** `ScopePrefix.commonOf : ClaimScope list -> Result<ScopePrefix, PathRefusal>` (longest common directory; a `Project` claim contributes its directory). `GrantWiderThanConductor` in `tryBuild` through `Grant.wideningsOf`. RED: an `Implementer` member under an `Analysis` conductor is refused with `Widening.Role`; a member whose claims span `src/` and `tests/` gets `ScopePrefix.repoRoot`. Land this step alone; `Capability.fs` is shared.
3. **Pure.** `rehearse` and the two properties (builder accepts implies `decide` accepts; `decide` answers `ClaimConflict` implies builder refuses) with a generator over about six path names so collisions are common. Fixed `Clock`, `Entropy = [||]`.
4. **Pure.** `calls : CohortPlan -> PlanCall list`, and the fold test: `calls` through `decide` from `CohortState.forScope` ends with held claims equal to the plan's. A paragraph in `docs/agents.md` section 6 (`:282`).
5. **Simulation, pure.** `SageFs.Simulation/CohortPlanSim.fs` (after `CohortVacancySpec.fs`, `SageFs.Simulation.fsproj:66`) and `SageFs.Tests/CohortPlanSimTests.fs`: random plans applied with `FaultInjection.drop` and `FaultInjection.duplicate` (`SageFs.Simulation/FaultInjection.fs:61,71`) over the call list. Invariants: the cohort never holds a claim the plan did not declare; never two overlapping `Held` claims. Twin: a builder that skips `tryCanonical`. The retried-`AcquireClaim` finding stands: the repeat answers `ClaimConflict(scope, holder = self)` with no claim id, so an applier reads status before calling that a failure.
6. **Edge.** `SageFs/CohortPlanApply.fs` over `McpClient` (pattern: `CapabilityHttpIntegrationTests.fs:35-59`): join as conductor, `mint_member` per member, `acquire_claim` per claim with that member's token in `_meta["sagefs/memberToken"]`. `CohortPlanOutcomeTests.fs` registered with `Integration.hostList` (`SageFs.Tests/TestInfrastructure.fs:482`), daemon from `withDaemon`, `SAGEFS_IDENTITY_POLICY=TokenRequired` in the child's environment through `SpawnEnvironment.apply`.
7. `DECISION:` build `propose`? Run "Measure first" first. Options: (a) build it over `coveredFilesOf`-shaped input, (b) stop at the builder. Recommendation: expect (b); `SageFs.Tests` has hub files (`TestInfrastructure.fs`) that most tests touch, so coverage will not separate work there.

### 4. How to check it

- REPL first: `create_project_session` on `SageFs.Core`, `send_fsharp_code` each test body against `SageFs.Features.CohortPlan` before writing it, `check_fsharp_code` on the new file. Never `#load` `CohortPlan.fs` into a session that has Core loaded.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Inner loop: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --filter-test-list "CohortPlan"`. The token is in the list name, so `--filter-test-case` would match nothing and still exit 0.
- Acceptance: the unfiltered `--summary` run names `CohortPlanTests`; for step 6, `--integration-host --summary --filter-test-list "CohortPlanOutcome"` as the loop and the unfiltered `--integration-host` tier as the gate.

### 5. Traps in this repo

- SAGE-005. `tryBuild conductor members` is fine. A helper such as `overlapOf (a: string) (b: string)` is a new finding, and `RatchetBudgets.analyzerFindings` (`SageFs.Tests/RatchetBudgets.fs:431`) holds `SageFs.Core/SAGE-005` at exactly 179. Pass `PlanMember` values, not two names.
- No new `CohortCommand` case belongs in this item, so no ledger fixture and no deterministic history are needed. If you find you need one, stop: the whole point is that `decide` already accepts what the builder emits.
- Step 6 starts a daemon: `SpawnEnvironment.apply` on the `ProcessStartInfo`. A ratchet counts the spawns that skip it.
- No wall-clock waits in step 6. A call returns when the ledger has the row (`Handle.Commit`, `SageFs.Core/Features/CohortOwner.fs:425`).
- Commit `SageFs.Core.fsproj`, `SageFs.Tests.fsproj` and `SageFs.Simulation.fsproj` whole, from a clean tree.
- No new NuGet package. `McpClient` is already referenced by the tests.

### 6. Size and order

Steps 1 to 4: two to three days for one person. Steps 5 and 6: two to three more. Step 2 lands alone. Step 5 can run beside 4. First commit: `test(cohort): RED two members claiming one file is refused as ClaimsOverlap (plan-a-cohort-as-code step 1)`, which adds `CohortPlanTests.fs` and its fsproj line and does not compile.

### 7. Not now

- `cohort { }` builder sugar: wait until a real plan reads badly as a pipeline.
- Worktree creation from a plan: `tidy_workspace` territory.
- `propose`: gated on the measurement.
- `Contract` claims (`claim-an-interface`): `overlaps` takes the new case when it lands, and `PlanMember.Claims` carries it for free.
- Re-applying a plan after a daemon restart: `tokens-survive-a-restart`.

## What done looks like

You write a cohort plan as an F# value in a SageFs session. Building it fails, at the line that builds it, if two members' claims overlap, if a claim escapes the repository, if a member's token grant would be wider than the conductor's, or if the plan cannot be run through the real cohort reducer. The error names both members and both scopes. A valid plan renders as the exact list of cohort calls (mint, claim) an orchestrator makes, and after those calls `get_cohort_status` shows the claims the plan declared and nothing else. A second, later stage proposes a claim split from coverage data for you to edit; it ships only if the first measurement below says coverage can separate work at all.

The outcome gate is a matrix of plans {disjoint files, the same file twice, a file inside another member's project, nested projects, a duplicate member name, a path that climbs out of the repo, an empty plan, a member with a wider role than the conductor} against {build only, rehearse through `Cohort.decide`, apply to a live owner under `TokenRequired`}. Each cell asserts the text a person reads.

## What exists today

- `ClaimScope.overlaps` and `ClaimPath.tryCanonical` (`SageFs.Core/Cohort.fs`) are the overlap arithmetic. `Cohort.decide` is pure: time and entropy are parameters, so a plan can be folded through the real reducer with a fixed `Clock` and `Entropy`. `CohortState.forScope` builds a starting state.
- Claims are made by the caller's own identity (`AcquireClaim of who * scope * purpose * cohort`). The conductor cannot claim on behalf of a member. A member acts through its token, presented per call in `_meta["sagefs/memberToken"]` or per connection in `X-SageFs-Member-Token` (`Capability.CapabilityTransport`).
- `mint_member` seats the member in the cohort (`docs/mcp-tools.md`, member tokens section). A `Grant` is `{ Preset; Scope: ScopePrefix; Route: RouteBinding; NotAfter }`; `Grant.isNarrowerOrEqual` and `Grant.wideningsOf` say whether a request is wider than its minter, and `Capability.admitClaim` says whether a claim fits a prefix. `RouteBinding.ofRequest` turns a session or checkout into a binding.
- The hand-written plans this replaces live outside the tree: `demo-actors-plan.md` and the "file-disjoint background islands" briefs in `~/Work/SageFs` (untracked). The vision document sketches a `cohort { ... }` computation expression (section 6.1, untracked) and a later "auto-cohort planning" step.
- Coverage data: `TestCoverageBitmaps`, `InstrumentationMap.merge` and `InputHashCoverage.selectionFiles` give the files a test covers; `DaemonMode.fs` near line 3027 (2026-11-11; it was 2975) builds `coveredFilesOf` from them and `Features.LiveTesting.AffectedTests.affected` selects tests for a diff. `CompileOrderInsight.fs` knows F# compile order. `coverage_intel` is the MCP tool over failing tests only.
- Nothing builds a plan. (The plan once named `tools/sagefs-call.fsx` as a one-off caller; on 2026-11-11 no such file exists, tracked or untracked.) House rules say logic goes in projects with tests and scripts stay thin glue.

## Design

A pure slice, `SageFs.Core/Features/CohortPlan.fs`, compiled after `Capability.fs`:

```fsharp
type PlanMember = { Name: string; Role: RolePreset; Claims: ClaimScope list; Worktree: RouteBinding; Ttl: TimeSpan }

type PlanRefusal =
  | DuplicateMember of name: string
  | ClaimsOverlap of a: string * ClaimScope * b: string * ClaimScope
  | ClaimEscapesRepo of member': string * PathRefusal
  | GrantWiderThanConductor of member': string * Widening list
  | NoClaims of member': string                    // an Implementer with nothing to do
  | RehearsalRefused of step: PlanStep * CohortError<string>

type CohortPlan = private CohortPlan of PlanMember list   // only CohortPlan.tryBuild makes one

CohortPlan.tryBuild : conductor: Grant -> PlanMember list -> Result<CohortPlan, PlanRefusal list>
CohortPlan.rehearse : CohortPlan -> Result<CohortState<string>, PlanRefusal>
CohortPlan.calls    : CohortPlan -> PlanCall list     // Mint of ... | Claim of memberName * ClaimScope * purpose
```

`tryBuild` returns every refusal, not the first, because a plan with three mistakes should say so once. Overlap checks use `ClaimScope.overlaps` after `tryCanonical`, so `a/../b.fs` and `b.fs` collide. The token scope for a member is the longest common directory of its claims (a new `ScopePrefix.commonOf`), and `tryBuild` checks it with `Grant.wideningsOf`. A shared prefix between two members is allowed because claims are exclusive and the plan makes all of them before work starts.

`rehearse` runs `Join` for a stand-in conductor and each member and then every `AcquireClaim` through the real `Cohort.decide` from `CohortState.forScope`. This proves the plan is accepted by the reducer that will run it, so the builder cannot drift from the rule. The pairwise check exists for the better message.

Surface: a plain Result pipeline first (`CohortPlan.empty |> addMember ... |> tryBuild`). A `cohort { }` builder is sugar over the same functions and waits until the pipeline reads badly in a real plan. The vision's rendezvous steps (land L1 then L2) are out of scope here; the first version covers membership and claims only, which is where the construction-time guarantee has value.

Execution has three stages, in order of risk:

1. `CohortPlan.calls` returns data. A person or an orchestrator issues the calls. This alone replaces the hand-written island brief.
2. An applier in `SageFs/CohortPlanApply.fs` using the SDK's `McpClient` (already a dependency; see `CapabilityHttpIntegrationTests.fs` for header presentation). It joins as conductor, mints, then claims per member with that member's token. It does not hand-roll JSON-RPC; `AGENTS.md` rule 5 forbids that.
3. A `sagefs cohort-plan check <plan.fsx>` command only if people ask for it outside a session.

Proposing a split (stage two of the item) is `CohortPlan.propose : CoverageCoupling -> WorkItem list -> Proposal`, where a work item names seed files and the proposal groups items whose covering tests do not overlap, listing hub files (covered by many tests) as "serialize these". Its input type is plain data so it is testable without a session. It returns a `Proposal`, never a `CohortPlan`; the human edits and builds.

No wire change in stages one and two. No new MCP tool. Stage three adds a CLI verb only. Persistence is untouched.

## Tests

RED first, `SageFs.Tests/CohortPlanTests.fs`:

1. Two members claiming `File "src/A.fs"` and `File "src/../src/A.fs"` give `ClaimsOverlap` naming both. (Fails to compile until the module exists, then fails on the assertion.)
2. A `File` under another member's `Project` is refused; two nested projects are refused.
3. A member whose preset is wider than the conductor's gives `GrantWiderThanConductor` with the `Widening` list.
4. Properties: for any plan `tryBuild` accepts, `rehearse` is `Ok` (the builder never accepts what the reducer refuses); for any plan the reducer refuses with `ClaimConflict`, `tryBuild` also refuses. A generator of members and scopes drawn from a small path alphabet makes collisions common.
5. `calls` of an accepted plan, folded through `decide`, reaches a state whose held claims equal the plan's claims exactly.

Simulation: add a plan-driven scenario to `SageFs.Simulation` using `Scenario.fs` and `FaultInjection.fs`: random plans, apply with dropped and duplicated calls (a retried `AcquireClaim` after a lost reply), invariant that the cohort never holds a claim the plan did not declare and never holds two overlapping claims. The twin is a builder that skips canonicalisation, which the first test must catch. Finding to design around: a retried `AcquireClaim` that already succeeded returns `ClaimConflict(scope, holder)` with the caller as holder and no claim id or fence, so the applier must read `get_cohort_status` before treating that as failure.

Outcome gate: `SageFs.Tests/CohortPlanOutcomeTests.fs`, `--integration-host` tier, the matrix above applied through the real MCP surface with `SAGEFS_IDENTITY_POLICY=TokenRequired`. No waits: every call returns when it is applied.

## Steps

1. Tests 1 and 2 and the smallest `CohortPlan.tryBuild` (overlap and escape only). Owns the new `CohortPlan.fs` and test file, plus the two fsproj entries.
2. Grant checks and `ScopePrefix.commonOf` (touches `Capability.fs`, so run alone or after any other change to it).
3. `rehearse` and properties 4 and 5.
4. `calls`, renderer, docs section in `docs/agents.md`.
5. Simulation (new file only; parallel with 4).
6. Applier and outcome gate.
7. Only if the measurement passes: `propose`, in its own file and tests, parallel with 6.

## Measure first

Run this before any code for `propose`. In a SageFs session on `SageFs.Tests`, with live testing on, dump the files each test covers (`selectionFiles` per test) and compute: the share of tests that cover each file, and for the 20 files agents touch most often (from `git log --since='30 days ago' --name-only`) the pairwise overlap of covering test sets. Then compare with history: for the last 30 days of agent merges, did pairs of branches with overlapping covering tests actually conflict or break each other after merge?

- If most touched files are covered by more than half the tests, coverage cannot separate work here (hub files), and `propose` is not built; the builder stands alone.
- If covering sets cluster and the clusters predict conflicts better than directory prefixes do, build `propose`.
- The thresholds are my guess. The measurement decides them; write the numbers into this plan when you have them.

A second small check: whether `Grant.Scope` as the common directory of a member's claims is usable in practice. If members' claims are spread over four directories the prefix is the repo root and the confinement means nothing; then the plan should mint one token per claim cluster, which changes `PlanMember`.

## Risks and dependencies

- A plan that constructs cleanly can still be wrong semantically (two members editing different files that call each other). Say this in the docs; claims are policy and `Capability.fs` says so about itself.
- Depends on nothing for stages one and two. `claim-an-interface` adds `Contract` claims; `overlaps` already covers a new case once it lands, and `PlanMember.Claims` takes it for free. `drive-real-agents-as-members` consumes `calls`. `tokens-survive-a-restart` (roadmap, Later) changes whether a plan can be re-applied after a daemon restart.
- The conductor-seat gap: a plan applier that crashes after the seat lapses leaves a vacant seat that no tool fills (see the `drive-real-agents-as-members` plan).

## Size

S for the builder, rehearsal and calls; M with the applier and the outcome gate; `propose` is a separate L that may not be built.

## Decisions for Will

1. Result pipeline first, `cohort { }` later? Recommendation: yes.
2. Should the plan also own worktree creation, or only claims and tokens? Recommendation: claims and tokens only; worktrees are `tidy_workspace` territory and creating them from a plan couples the plan to git.
3. Is stage three (a CLI check) wanted at all, given that a session is the dogfood loop? Recommendation: skip it until someone asks.
