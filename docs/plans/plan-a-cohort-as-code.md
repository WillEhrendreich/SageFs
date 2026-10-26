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

## What done looks like

You write a cohort plan as an F# value in a SageFs session. Building it fails, at the line that builds it, if two members' claims overlap, if a claim escapes the repository, if a member's token grant would be wider than the conductor's, or if the plan cannot be run through the real cohort reducer. The error names both members and both scopes. A valid plan renders as the exact list of cohort calls (mint, claim) an orchestrator makes, and after those calls `get_cohort_status` shows the claims the plan declared and nothing else. A second, later stage proposes a claim split from coverage data for you to edit; it ships only if the first measurement below says coverage can separate work at all.

The outcome gate is a matrix of plans {disjoint files, the same file twice, a file inside another member's project, nested projects, a duplicate member name, a path that climbs out of the repo, an empty plan, a member with a wider role than the conductor} against {build only, rehearse through `Cohort.decide`, apply to a live owner under `TokenRequired`}. Each cell asserts the text a person reads.

## What exists today

- `ClaimScope.overlaps` and `ClaimPath.tryCanonical` (`SageFs.Core/Cohort.fs`) are the overlap arithmetic. `Cohort.decide` is pure: time and entropy are parameters, so a plan can be folded through the real reducer with a fixed `Clock` and `Entropy`. `CohortState.forScope` builds a starting state.
- Claims are made by the caller's own identity (`AcquireClaim of who * scope * purpose * cohort`). The conductor cannot claim on behalf of a member. A member acts through its token, presented per call in `_meta["sagefs/memberToken"]` or per connection in `X-SageFs-Member-Token` (`Capability.CapabilityTransport`).
- `mint_member` seats the member in the cohort (`docs/mcp-tools.md`, member tokens section). A `Grant` is `{ Preset; Scope: ScopePrefix; Route: RouteBinding; NotAfter }`; `Grant.isNarrowerOrEqual` and `Grant.wideningsOf` say whether a request is wider than its minter, and `Capability.admitClaim` says whether a claim fits a prefix. `RouteBinding.ofRequest` turns a session or checkout into a binding.
- The hand-written plans this replaces live outside the tree: `demo-actors-plan.md` and the "file-disjoint background islands" briefs in `~/Work/SageFs` (untracked). The vision document sketches a `cohort { ... }` computation expression (section 6.1, untracked) and a later "auto-cohort planning" step.
- Coverage data: `TestCoverageBitmaps`, `InstrumentationMap.merge` and `InputHashCoverage.selectionFiles` give the files a test covers; `DaemonMode.fs` near line 2975 builds `coveredFilesOf` from them and `Features.LiveTesting.AffectedTests.affected` selects tests for a diff. `CompileOrderInsight.fs` knows F# compile order. `coverage_intel` is the MCP tool over failing tests only.
- Nothing builds a plan. `tools/sagefs-call.fsx` (untracked in the main checkout) is a one-off caller, and house rules say logic goes in projects with tests and scripts stay thin glue.

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
