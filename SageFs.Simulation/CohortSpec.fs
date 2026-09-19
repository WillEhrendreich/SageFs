namespace SageFs.Simulation

open System.Collections.Generic
open SageFs
open SageFs.Cohort

/// Phase 1 DST — the EXHAUSTIVE specification proof for the cohort coordination
/// core (the CsCheck "Exhaustive" + "Faults" modes, realized over the REAL
/// `Cohort.decide`; see `sagefs-learn-from-cscheck.md`).
///
/// Where `CohortLandingSim` SAMPLES scripted scenarios, this module PROVES the
/// named rules: it enumerates the ENTIRE reachable `CohortState` space of a
/// bounded model breadth-first, and checks every rule at every reachable state.
/// A completed BFS with no violation is a proof for that bound — not "500 random
/// cases passed" but "no reachable state violates this rule."
///
/// Why the space is finite (the three forcing functions):
///   * Chaos is DATA over a small fixed alphabet: 2 members, 2 file scopes, and
///     the whole landing lifecycle driven by exploring BOTH outcome branches of
///     each pending effect (rebase ok/conflict, tests pass/fail/inconclusive/
///     fast-forward ok/fail), plus the out-of-band commands that can fire at
///     ANY point regardless of pending effect: `ReleaseClaim` (fence
///     movement), `VetoLanding`, `ResolveVeto`, `SetIntegrationHead`.
///   * Ids are minted DETERMINISTICALLY (a claim's id from its scope, a landing's
///     from its requester), so the same logical situation always mints the same
///     id and structurally-equal states dedup. (Modelling note: because a released
///     claim keeps its id in the map, the bounded model does not RE-ACQUIRE a
///     scope after it is released — a sound restriction of the explored subspace,
///     not a claim about the unbounded system. `ReleaseClaim` itself — moving an
///     already-minted claim's fence / off `Held` — IS modeled; only the
///     subsequent re-acquire is excluded.)
///   * `Error` results (a conflicting acquire, a duplicate landing, an
///     out-of-order completion, a non-conductor's `SetIntegrationHead`/
///     `ResolveVeto`) leave the state unchanged, so they never expand the
///     frontier — the core's own guards bound the exploration.
///
/// The subject is the REAL `Cohort.decide`. The `faultRebaseConflictJam` twin
/// reintroduces exactly the pre-fix queue-jam (a rebase conflict blocks the
/// landing but never pops the queue); the `NO-TERMINAL-IN-QUEUE` rule HOLDS
/// against the real core and is VIOLATED against the twin — which is what proves
/// the rule (and the exhaustive checker) has teeth rather than passing vacuously.
///
/// COVERAGE (armfix, cmd-handoff.md item B — read this before citing "proven"
/// anywhere, and re-count against `enabled`'s actual body if this module has
/// changed since): `enabled` yields 13 of `CohortCommand`'s 20 cases — `Join`,
/// `AcquireClaim`, `ReleaseClaim`, `RequestLanding`, `VetoLanding`,
/// `ResolveVeto`, `SetIntegrationHead`, `RebaseCompleted`, `AffectedComputed`,
/// `TestsCompleted`, `VerificationInconclusive`, `FastForwardCompleted`,
/// `FastForwardFailed`. Still unmodeled: `Depart`, `RenewLease`, `Tick`,
/// `ReassignClaim`, `DelegateConductor`, `ObserveSave`, `WithdrawLanding` —
/// none of these sit on the `NO-TERMINAL-IN-QUEUE` counterexample path the
/// roasts found (a member leaving, a lease timer, an orphaned-claim handoff,
/// conductor delegation, a save-time warning, and a voluntary withdrawal are
/// none of them a way to leave a TERMINAL landing stuck in `Queue`), so their
/// absence does not undermine this proof's headline claim — but say so
/// explicitly, every time, rather than "the whole cohort core is proven."
module CohortSpec =

  /// Member identity in the bounded model is an opaque string.
  type Member = string

  /// The shape of the reducer under test (the real `Cohort.decide` and the
  /// fault twins share it).
  type Decide =
    Clock
      -> Entropy
      -> CohortState<Member>
      -> CohortCommand<Member>
      -> Result<CohortState<Member> * CohortEvent<Member> list * CohortEffect<Member> list, CohortError<Member>>

  // ── Named rules — state predicates (CsCheck `Rule`s) ──────────────────────

  /// No two `Held` claims ever cover overlapping scope.
  let claimExclusive (s: CohortState<Member>) : bool =
    let held =
      s.Claims |> Map.toList
      |> List.choose (fun (_, c) -> match c.State with ClaimState.Held _ -> Some c.Scope | _ -> None)
    let rec pairsOk =
      function
      | [] -> true
      | x :: rest -> (rest |> List.forall (fun y -> not (ClaimScope.overlaps x y))) && pairsOk rest
    pairsOk held

  /// Every claim's fence is bounded by the monotonic `NextFence`.
  let fenceMonotone (s: CohortState<Member>) : bool =
    s.Claims |> Map.forall (fun _ c -> c.Fence <= s.NextFence)

  /// Once any member exists, the conductor binding is set (v1 create-cohort).
  let conductorBound (s: CohortState<Member>) : bool =
    Map.isEmpty s.Members || Option.isSome s.Conductor

  /// The queue never retains a terminal landing (Blocked/Landed/Withdrawn) — the
  /// no-deadlock property whose sampled form caught the RebaseConflict jam.
  let noTerminalInQueue (s: CohortState<Member>) : bool =
    s.Queue
    |> List.forall (fun id ->
      match Map.tryFind id s.Landings with
      | Some req ->
        match req.State with
        | LandingState.Queued
        | LandingState.Rebasing _
        | LandingState.Verifying _ -> true
        | _ -> false
      | None -> false)

  /// Strict FIFO: only the queue head may be in flight; every other queued
  /// landing is still `Queued`.
  let queueSerial (s: CohortState<Member>) : bool =
    match s.Queue with
    | [] -> true
    | _ :: tail ->
      tail
      |> List.forall (fun id ->
        match Map.tryFind id s.Landings with
        | Some r -> r.State = LandingState.Queued
        | None -> false)

  let rules : (string * (CohortState<Member> -> bool)) list =
    [ "CLAIM-EXCLUSIVE", claimExclusive
      "FENCE-MONOTONE", fenceMonotone
      "CONDUCTOR-BOUND", conductorBound
      "NO-TERMINAL-IN-QUEUE", noTerminalInQueue
      "QUEUE-SERIAL", queueSerial ]

  // ── The bounded alphabet ──────────────────────────────────────────────────

  let private members : Member list = [ "a"; "b" ]
  let private scopes = [ ClaimScope.File "f1"; ClaimScope.File "f2" ]
  let private scopeEntropy =
    function
    | ClaimScope.File "f1" -> [| 1uy |]
    | ClaimScope.File "f2" -> [| 2uy |]
    | _ -> [| 9uy |]
  let private memberEntropy =
    function
    | "a" -> [| 10uy |]
    | "b" -> [| 11uy |]
    | _ -> [| 12uy |]

  let private clock : Clock = System.DateTime(2020, 1, 1)

  let private isPresent (s: CohortState<Member>) (m: Member) =
    match Map.tryFind m s.Members with
    | Some { Presence = MemberPresence.Present } -> true
    | _ -> false

  let private landingEffectOf =
    function
    | CohortEffect.Rebase _
    | CohortEffect.ComputeAffected _
    | CohortEffect.RunTests _
    | CohortEffect.FastForward _ as e -> Some e
    | CohortEffect.Notify _ -> None

  let private isCompletionCmd =
    function
    | CohortCommand.RebaseCompleted _
    | CohortCommand.AffectedComputed _
    | CohortCommand.TestsCompleted _
    | CohortCommand.VerificationInconclusive _
    | CohortCommand.FastForwardCompleted _
    | CohortCommand.FastForwardFailed _ -> true
    | _ -> false

  /// A second, distinct integration-head value (armfix — cmd-handoff.md item
  /// B — reaches the `HeadMoved` arms). Distinct from `nullSha` and from any
  /// sha `RebaseCompleted`/`FastForwardCompleted` mint in this model, so
  /// `SetIntegrationHead` genuinely moves `IntegrationHead` out from under an
  /// in-flight landing's `Verifying.base'`.
  let private altHead = "H2"

  /// The state-dependent enabled actions at a `(state, pending landing effect)`
  /// node. Free commands (join / acquire / request / release / veto / resolve
  /// / re-seed-head) model concurrent members acting at any time; the
  /// completions resolve the SINGLE pending landing effect, exploring EVERY
  /// outcome branch (the serial queue means at most one landing effect is
  /// ever pending).
  ///
  /// armfix (cmd-handoff.md item B): the roasts (roast-2day §1, roast-2day-cmd
  /// §1) named the earlier alphabet's blind spot precisely — `VetoLanding`,
  /// `SetIntegrationHead`, and a claim's fence moving after `RequestLanding`
  /// were ALL absent, which made `HeadMoved`/`StaleClaimFence`/the vetoed
  /// state structurally UNREACHABLE, so `NO-TERMINAL-IN-QUEUE` was "proven"
  /// only over a subspace that excluded the very arms that violated it in
  /// production. Five additions close that: `ReleaseClaim` (fence movement:
  /// a member releasing a claim that backs an IN-FLIGHT landing bumps its
  /// fence / flips it off `Held`, exactly the race `FastForwardCompleted`'s
  /// land-time re-check exists to catch), `VetoLanding` (any present member,
  /// on any non-terminal landing — matches `decide`'s own no-gate design),
  /// `ResolveVeto` (any present member attempts it; `decide` itself enforces
  /// Conductor-only, so a non-conductor's attempt is just an `Error` the
  /// explorer discards for free), `SetIntegrationHead` (re-seeds the head to
  /// `altHead`, reachable by a non-conductor too for the same reason), and a
  /// `FastForwardFailed` branch alongside `FastForwardCompleted` at every
  /// pending `FastForward` node (this one was already in `isCompletionCmd`
  /// but `enabled` never yielded it — roast-2day §1's exact finding).
  let private enabled (s: CohortState<Member>, pend: CohortEffect<Member> option) : (CohortCommand<Member> * Entropy) list =
    [ for m in members do
        if not (isPresent s m) then
          yield CohortCommand.Join(m, JoinableRole.Implementer, None), [||]
      for m in members do
        if isPresent s m then
          for sc in scopes do
            yield CohortCommand.AcquireClaim(m, sc, "p"), scopeEntropy sc
      for m in members do
        if isPresent s m then
          let mine =
            s.Claims |> Map.toList
            |> List.choose (fun (_, c) ->
              match c.State with
              | ClaimState.Held h when h = m -> Some(c.Id, c.Fence)
              | _ -> None)
          yield CohortCommand.RequestLanding(m, mine, [ "commit-" + m ], "land"), memberEntropy m
      // The five additions below are each individually cheap (measured
      // 6,427 -> 18k-53k nodes alone against a REPL probe of the real
      // alphabet), but combined naively at FULL generality (every present
      // member, every reachable claim/landing, every FastForwardFailed
      // retry up to `maxFastForwardAttempts`) they interact
      // MULTIPLICATIVELY — measured 3,000,000+ nodes and Capped=true, because
      // `VetoLanding`+`ResolveVeto` and `FastForwardFailed`'s own retry both
      // let a landing re-enter the SAME downstream subtree (rebase -> verify
      // -> fast-forward) repeatedly, and every other free command is then
      // re-offered at every one of those revisited nodes too. Three targeted
      // restrictions bring the combined proof back to a fast, CLOSED
      // exploration (measured 351,285 nodes, ~9s) without losing reachability
      // of any of the target states (HeadMoved/StaleClaimFence/Veto/
      // ResolveVeto): the invariants under proof (§ rules above) do not
      // depend on WHICH present member acted, only on whether the queue
      // stays correct — so a single fixed actor exercises the same state
      // transitions a second actor would, and WHERE in the queue an action
      // targets only matters at the front (only the front can be
      // Rebasing/Verifying, `Cohort.fs`'s own FIFO invariant, so an action on
      // a non-front landing never touches the in-flight pipeline this model
      // exists to stress).
      //   1. `ReleaseClaim`/`VetoLanding`: fixed to member "b" (never both),
      //      and only against the FRONT-of-queue landing's own claims/id.
      //   2. `ResolveVeto`/`SetIntegrationHead`: fixed to the conductor (the
      //      only actor `decide` would ever accept for either).
      //   3. `FastForwardFailed`'s transient-retry branch: only offered while
      //      `FastForwardAttempts = 0` (one retry cycle, reaching
      //      `Rebasing`+attempts=1 — enough to prove the retry transition
      //      itself is reachable and safe). The EXHAUSTION transition
      //      (3rd failure -> `Blocked(Inconclusive)`, popped) is verified
      //      separately by `CohortFastForwardFailedTests.fs`'s example test
      //      (driven step-by-step against the same real `Cohort.decide`,
      //      REPL-proven during this fix), not by this BFS — modeling the
      //      full 3-round cascade combined with every other free command
      //      here was the single largest driver of the blow-up above.
      // `VetoLanding`+`ResolveVeto` can still cycle a landing through the
      // pipeline more than once within this bound (nothing here caps a
      // repeat veto), but the fixed-actor/front-of-queue restrictions keep
      // that cheap enough for the BFS to close well within `defaultCap`.
      let frontLandingClaims =
        match s.Queue |> List.tryHead |> Option.bind (fun fid -> Map.tryFind fid s.Landings) with
        | Some req -> req.Claims
        | None -> []
      for (cid, fence) in frontLandingClaims do
        match Map.tryFind cid s.Claims with
        | Some c when c.State = ClaimState.Held "b" -> yield CohortCommand.ReleaseClaim("b", cid, fence), [||]
        | _ -> ()
      match s.Queue |> List.tryHead with
      | Some lid ->
        match Map.tryFind lid s.Landings with
        | Some { State = LandingState.Landed _ | LandingState.Withdrawn } -> ()
        | Some _ -> yield CohortCommand.VetoLanding("b", lid, "veto"), [||]
        | None -> ()
      | None -> ()
      match s.Conductor with
      | Some c ->
        let vetoed =
          s.Landings |> Map.toList
          |> List.choose (fun (lid, req) ->
            match req.State with
            | LandingState.Blocked(LandingBlocker.VetoedBy _, _) -> Some lid
            | _ -> None)
        for lid in vetoed do
          yield CohortCommand.ResolveVeto(c, lid), [||]
        // The concurrent-head-move race — only while a landing effect is
        // pending (Rebasing/Verifying is exactly when a head-move's
        // diagnosis at land time matters).
        if pend.IsSome then
          yield CohortCommand.SetIntegrationHead(c, altHead), [||]
      | None -> ()
      match pend with
      | Some(CohortEffect.Rebase(id, _, _)) ->
        yield CohortCommand.RebaseCompleted(id, Result.Ok("R-" + (let (LandingId x) = id in x))), [||]
        yield CohortCommand.RebaseCompleted(id, Result.Error [ "cf" ]), [||]
      | Some(CohortEffect.ComputeAffected(id, _, _)) -> yield CohortCommand.AffectedComputed(id, [ TestId "t" ]), [||]
      | Some(CohortEffect.RunTests(id, _)) ->
        yield CohortCommand.TestsCompleted(id, []), [||]
        yield CohortCommand.TestsCompleted(id, [ TestId "t" ]), [||]
        yield CohortCommand.VerificationInconclusive(id, "x"), [||]
      | Some(CohortEffect.FastForward(id, sha)) ->
        yield CohortCommand.FastForwardCompleted(id, sha), [||]
        // One retry cycle only — see the block comment above.
        match Map.tryFind id s.Landings with
        | Some req when req.FastForwardAttempts = 0 -> yield CohortCommand.FastForwardFailed(id, "infra-fail"), [||]
        | _ -> ()
      | _ -> () ]

  /// The result of an exhaustive exploration.
  type ExploreResult =
    { /// The number of distinct reachable `(state, pending-effect)` nodes.
      Nodes: int
      /// True iff the frontier emptied within the cap — i.e. the bounded space
      /// was fully enumerated, so an empty `Violations` is a PROOF, not a sample.
      Complete: bool
      /// True iff the cap was hit before the frontier emptied (an incomplete,
      /// therefore inconclusive, run — raise the cap).
      Capped: bool
      /// The distinct named rules that were violated at some reachable state.
      Violations: string list }

  /// Enumerate the entire reachable state space of the bounded model through the
  /// given reducer, checking every rule at every reachable node.
  let explore (decide: Decide) (cap: int) : ExploreResult =
    let visited = HashSet<CohortState<Member> * CohortEffect<Member> option>(HashIdentity.Structural)
    let frontier = Queue<CohortState<Member> * CohortEffect<Member> option>()
    let start = (CohortState.empty (), None)
    visited.Add start |> ignore
    frontier.Enqueue start
    let violations = HashSet<string>()
    let mutable capped = false
    while frontier.Count > 0 && not capped do
      let (s, pend) = frontier.Dequeue ()
      for (name, rule) in rules do
        if not (rule s) then violations.Add name |> ignore
      for (cmd, ent) in enabled (s, pend) do
        match decide clock ent s cmd with
        | Result.Ok(s', _, effs) ->
          let pend' =
            match effs |> List.tryPick landingEffectOf with
            | Some e -> Some e
            | None -> if isCompletionCmd cmd then None else pend
          let node = (s', pend')
          if visited.Add node then
            if visited.Count > cap then capped <- true else frontier.Enqueue node
        | Result.Error _ -> ()
    { Nodes = visited.Count
      Complete = (frontier.Count = 0 && not capped)
      Capped = capped
      Violations = List.ofSeq violations }

  /// A generous cap — the bounded model closes at a few thousand nodes, so this
  /// only guards against an accidental unbounded change to the alphabet.
  let defaultCap = 500_000

  /// PROVE the rules over the real `Cohort.decide` (Exhaustive mode).
  let proof () : ExploreResult = explore (fun c e s cmd -> Cohort.decide c e s cmd) defaultCap

  /// The FAULTS-mode twin: reintroduce the pre-fix RebaseConflict queue-jam — a
  /// rebase conflict blocks the front-of-queue landing but does NOT pop the queue
  /// or advance. Everything else delegates to the real core, so the ONLY
  /// difference is the missing pop. `NO-TERMINAL-IN-QUEUE` must catch it.
  let faultRebaseConflictJam : Decide =
    fun clk ent state command ->
      match command with
      | CohortCommand.RebaseCompleted(id, Result.Error conflictFiles) when (state.Queue |> List.tryHead = Some id) ->
        match Map.tryFind id state.Landings with
        | Some req ->
          match req.State with
          | LandingState.Rebasing _ ->
            let blocked = { req with State = LandingState.Blocked(LandingBlocker.RebaseConflict conflictFiles, NextAction.RebaseAndResubmit) }
            Result.Ok(
              { state with Landings = Map.add id blocked state.Landings },
              [ CohortEvent.LandingStateChanged(id, blocked.State) ],
              ([]: CohortEffect<Member> list))
          | _ -> Cohort.decide clk ent state command
        | None -> Cohort.decide clk ent state command
      | _ -> Cohort.decide clk ent state command

  /// Explore through the fault twin (Faults mode) — expected to VIOLATE
  /// `NO-TERMINAL-IN-QUEUE`.
  let faults () : ExploreResult = explore faultRebaseConflictJam defaultCap
