/// `cohort-landing` (cohort-demo-scenario-plan.md, phase C): a real,
/// checked, 8-beat cohort-coordination demo — TWO real MCP-driven agents
/// (alice, bob) join the daemon's cohort, claim disjoint files, hit a
/// genuine claim CONFLICT, configure the integration ref, and race a
/// good landing against a breaking one — never a scripted/canned
/// narration: every beat's `Expect` is proven by a REAL MCP JSON-RPC
/// response from the daemon (`Actors/Agent.fs`'s `parseCohortWire`/
/// `landAndWait` — see those doc comments for exactly how a non-input
/// `LiveActor.Observe` executes this).
///
/// THE DUAL-SESSION CORRECTION (finding #2 of the plan, verified live
/// against `SageFs/Mcp.fs`'s `memberIdFor`): cohort member identity is
/// bound to the MCP TRANSPORT SESSION, not the `agentName` argument a
/// tool call carries — two `agentName`s driven over ONE shared connection
/// collapse to the SAME member (a live dogfood run proved this: the
/// second `join_cohort` failed with "already a member"). So alice and
/// bob MUST be, and here genuinely ARE, two distinct MCP sessions:
/// `Actors/Agent.fs`'s `sessionFor` gives each `agentName` its own
/// `Rpc.Session` with its own `initialize` handshake the first time this
/// scenario's wire names that agent, and every following step for that
/// same agent reuses it. Beat 5 (bob's claim on alice's file is
/// REJECTED) and beat 6 (alice's conductor-only `set_integration_ref`
/// succeeds because SHE, not bob, is the conductor) are the two beats
/// that would be a LIE without this correction — collapsed identity
/// would make bob's own claim "conflict" with bob (never rejected) and
/// would make it ambiguous who "the conductor" even is.
///
/// HOW THE FIXTURE SHAS/CLAIM-FENCES FLOW AT RUN TIME (the plan's own
/// "beats 3-8 need runtime values" note): NONE of the runtime values
/// (claim ids/fences, `alice-good`/`bob-break` commit shas) are typed
/// into this file, because none of them exist until the daemon actually
/// answers a real MCP call at record time — this file stays 100% pure
/// data (`Scenario`/`Step` values, no IO), exactly `Domain.fs`'s own
/// "the impure edge is injected, never called from a pure planner"
/// doctrine, applied here to this actor's own local state instead of the
/// shared `DemoRuntime`.
///   - Claim id/fence (beats 3/4, consumed by beats 7/8): captured by
///     `Actors/Agent.fs`'s `recordClaim` straight off `acquire_claim`'s
///     OWN real response text (`"Acquired claim <id> over <scope>
///     (fence=<n>)."`) the moment that step succeeds, keyed by
///     `agentName` in `CohortState.claimsFor` — never carried on any
///     wire this file writes.
///   - `alice-good`/`bob-break` commit shas (beats 7/8): these are git
///     BRANCH NAMES in the fixture repo `Runtime.Cohort.prepareFixture`
///     (phase A, host-side) creates — real refs, not values this
///     pure-data scenario file could ever know ahead of time. Beats 7/8
///     wire `shaTag=alice-good`/`shaTag=bob-break` (the REF NAME, not a
///     sha) through the `land_and_wait` pseudo-tool; `Actors/Agent.fs`'s
///     `FixtureRoot.find`/`resolveGitRefSha` locate the fixture inside
///     the cell (a marker-file filesystem search, mirroring how
///     `RepoRoot.find` locates the repo checkout — see that module's own
///     doc comment) and run a real `git rev-parse <ref>` in it at CALL
///     TIME, turning the ref into the real commit sha `request_landing`
///     needs. This is the "cleanest approach" the plan asks this phase
///     to pick and document: no new wire field, no Runtime.fs/Wire.fs
///     edit, no value this file has to fabricate or guess.
///   - `join_cohort`'s own optional `working_directory` argument is left
///     OFF the wire entirely for both alice and bob (beats 1/2), rather
///     than resolved to the fixture dir at call time the way beats 7/8
///     resolve their sha: `join_cohort`'s own doc comment
///     (`SageFs/McpTools.fs`) says an omitted `working_directory` falls
///     back to "the daemon's own working directory" — which phase D's
///     `Runtime.Cohort.daemonCwd` makes the fixture dir already (the
///     daemon is launched WITH the fixture as its cwd, the plan's own
///     "genuine work" item 1). Omitting therefore reaches the exact same
///     resolved session the plan's more explicit `join_cohort(alice,
///     Implementer, fixtureDir)` sketch names, with no extra
///     fixture-discovery round trip on the two steps that don't
///     otherwise need one.
module SageFs.Demos.Scenarios.Cohort

open SageFs.Demos.Domain

/// The record separator `Actors/Agent.fs`'s `parseCohortWire` splits the
/// wire on — see that function's own doc comment for the full 4-field
/// contract (`agentName<RS>toolName<RS>argsEncoded<RS>expected`) this
/// helper packs. F#'s `` string escape produces the exact same
/// single control character `Agent.fs`'s own literal embeds directly.
[<Literal>]
let private RS = ""

/// The two repo-relative files the fixture's `alice`/`bob` claim before
/// "editing" (beats 3/4/5) — plain string literals, not `SampleFile`/
/// `Sample` (the fixture is not a `Sample` case; it is the throwaway git
/// repo `Runtime.Cohort.fs`, a phase-A host-side module this file does
/// not link against, builds and tears down per recording). Match
/// `Runtime.Cohort.fs`'s own `aliceClaimFile`/`bobClaimFile` constants
/// exactly.
[<Literal>]
let private AliceClaimFile = "src/Alice.fs"

[<Literal>]
let private BobClaimFile = "src/Bob.fs"

/// Packs one cohort MCP call onto the wire (`Actors/Agent.fs`'s widened
/// `parseCohortWire` — see that function's doc comment for the exact
/// contract this must match byte-for-byte): `agentName` is prepended onto
/// `toolName`'s arguments by the actor itself, so `args` here carries
/// only the REMAINING arguments, as `;;`-delimited `key=value` pairs
/// (`parseArgsEncoded`'s own contract). Mirrors `Scenarios.Agent.fs`'s
/// own `mcpStep` helper: `Action.Await Signal.SessionReady` — there is no
/// click/type target for a step whose real substance is an MCP tool
/// call — and `Expect = PageTextContains(wireSelector, "")`, the one
/// opaque freeform string a non-input `LiveActor.Observe` receives.
let private cohortStep
  (caption: string)
  (agentName: string)
  (toolName: string)
  (args: (string * string) list)
  (expected: string)
  (dwell: Dwell)
  : Step =
  let argsEncoded =
    args
    |> List.map (fun (k, v) -> sprintf "%s=%s" k v)
    |> String.concat ";;"

  let wireSelector = sprintf "%s%s%s%s%s%s%s" agentName RS toolName RS argsEncoded RS expected

  { Caption = Caption.mk caption
    Action = Action.Await Signal.SessionReady
    Expect = Expectation.PageTextContains(wireSelector, "")
    Dwell = dwell }

/// The 8 beats (cohort-demo-scenario-plan.md's own numbered list, each
/// `expected` substring VERIFIED against the real daemon text it checks,
/// not merely assumed from the plan's own draft wording — see below for
/// where this diverges from the plan's first guess and why):
///
///   1/8 join_cohort(alice)          — `mid`-rendered identity is
///       `"mcp:<transportSessionId>"` (`SageFs/MemberTable.fs`'s own
///       `display`), never the literal agent name, so "conductor" (from
///       `joinCohort`'s own "You are the conductor (first to join)."
///       text) is the substring that actually appears — matches the
///       plan's own beat 1 exactly.
///   2/8 join_cohort(bob)            — same identity rendering means
///       "bob"/"joined" (the plan's own first guess) is NOT reliably
///       present verbatim; `"Joined cohort"` (the literal, case-exact
///       prefix of `joinCohort`'s real success text) is what is checked
///       instead — still proves a genuine second, successful join.
///   3/8 acquire_claim(alice, Alice) — "fence" (present in the real
///       `"... (fence=<n>)."` success text) — matches the plan exactly.
///   4/8 acquire_claim(bob, Bob)     — "Acquired claim" (the plan's own
///       "expect claim id" cannot be a literal substring: the id is a
///       fresh runtime value with no fixed text to match against ahead
///       of time — this is the closest literal, still tool-specific,
///       proof of the same fact).
///   5/8 acquire_claim(bob, Alice)   — "already claimed by" — THE
///       critical proof beat: `Cohort.CohortError.ClaimConflict`'s real
///       text is `"The scope %s is already claimed by %s."` where the
///       holder is again rendered as `"mcp:<sid>"`, never literally
///       "alice" — so "naming alice" (the plan's own first-guess wording)
///       is not literally true of the rendered text either; "already
///       claimed by" is the wording `Actors/Agent.fs`'s own
///       `parseCohortWire` doc comment already settled on as the
///       MANDATORY specific check here (a generic non-failure sniff
///       would wrongly PASS — a rejection response contains no "Error"
///       text of its own).
///   6/8 set_integration_ref(alice)  — "Integration configured" —
///       matches the plan exactly (and is real proof alice, not bob, is
///       the conductor: bob would get "not the cohort conductor").
///   7/8 land_and_wait(alice, alice-good) — "Landed".
///   8/8 land_and_wait(bob, bob-break)     — "FailingTests" (the real,
///       specific `LandingBlocker` case name `Cohort.fs`'s `decide`
///       assigns a genuine test failure — `Blocked (FailingTests [...],
///       FixTests [...])` under `renderCohortFrame`'s `%A` rendering —
///       strictly more specific than the plan's own alternative "Blocked"
///       guess, which would also match a rebase conflict or a moved head
///       for the wrong reason).
let cohortLanding: Scenario =
  { Id = ScenarioId.ofRaw "cohort-landing"
    Capability = Capability.Agent
    Client = Client.Agent
    // `record` (Runtime.fs) unconditionally pre-builds `scenario.Sample`
    // on the host for EVERY scenario, cohort or not
    // (`buildSampleFromSource repoRoot (Sample.relativePath
    // scenario.Sample)`) — the lightest real sample keeps that
    // unavoidable pre-build cheap, mirroring how `agent-mcp`
    // (`Scenarios.Agent.fs`) picks ITS sample the same way, just the
    // smallest one rather than `WebappDatastar`: this scenario's own
    // real work happens entirely against the separate cohort fixture
    // (`Runtime.Cohort.fs`), never against this sample project at all.
    App = AppKind.NoApp
    Sample = Sample.ConsoleTicker
    Layout = LayoutTemplate.DashboardOnly
    Steps =
      [ cohortStep
          "1/8 · alice joins the cohort — she becomes conductor"
          "alice"
          "join_cohort"
          [ "role", "Implementer" ]
          "conductor"
          Dwell.medium
        cohortStep
          "2/8 · bob joins as a second implementer"
          "bob"
          "join_cohort"
          [ "role", "Implementer" ]
          "Joined cohort"
          Dwell.medium
        cohortStep
          (sprintf "3/8 · alice claims %s — hers to edit" AliceClaimFile)
          "alice"
          "acquire_claim"
          [ "scope", sprintf "file:%s" AliceClaimFile
            "purpose", "cohort-landing demo: alice's own change" ]
          "fence"
          Dwell.medium
        cohortStep
          (sprintf "4/8 · bob claims %s — disjoint, no conflict" BobClaimFile)
          "bob"
          "acquire_claim"
          [ "scope", sprintf "file:%s" BobClaimFile
            "purpose", "cohort-landing demo: bob's own change" ]
          "Acquired claim"
          Dwell.medium
        cohortStep
          (sprintf "5/8 · bob tries alice's %s — REJECTED (conflict)" AliceClaimFile)
          "bob"
          "acquire_claim"
          [ "scope", sprintf "file:%s" AliceClaimFile
            "purpose", "cohort-landing demo: bob tries to claim alice's file" ]
          "already claimed by"
          Dwell.medium
        cohortStep
          "6/8 · alice sets the integration ref (conductor only)"
          "alice"
          "set_integration_ref"
          [ "integrationRef", "HEAD" ]
          "Integration configured"
          Dwell.medium
        cohortStep
          "7/8 · alice's good change lands — tests green"
          "alice"
          "land_and_wait"
          [ "shaTag", "alice-good"
            "statement", "cohort-landing demo: alice's good change (extends test coverage, stays green)" ]
          "Landed"
          Dwell.long
        cohortStep
          "8/8 · bob's breaking change is BLOCKED — tests fail"
          "bob"
          "land_and_wait"
          [ "shaTag", "bob-break"
            "statement", "cohort-landing demo: bob's breaking change (expected to fail verification)" ]
          "FailingTests"
          Dwell.long ]
    Cost = CostClass.console
    Masks = [] }

let scenarios: Scenario list = [ cohortLanding ]
