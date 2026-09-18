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
///     each pending effect (rebase ok/conflict, tests pass/fail/inconclusive).
///   * Ids are minted DETERMINISTICALLY (a claim's id from its scope, a landing's
///     from its requester), so the same logical situation always mints the same
///     id and structurally-equal states dedup. (Modelling note: because a released
///     claim keeps its id in the map, the bounded model does not re-acquire a
///     scope after it is released — a sound restriction of the explored subspace,
///     not a claim about the unbounded system.)
///   * `Error` results (a conflicting acquire, a duplicate landing, an
///     out-of-order completion) leave the state unchanged, so they never expand
///     the frontier — the core's own guards bound the exploration.
///
/// The subject is the REAL `Cohort.decide`. The `faultRebaseConflictJam` twin
/// reintroduces exactly the pre-fix queue-jam (a rebase conflict blocks the
/// landing but never pops the queue); the `NO-TERMINAL-IN-QUEUE` rule HOLDS
/// against the real core and is VIOLATED against the twin — which is what proves
/// the rule (and the exhaustive checker) has teeth rather than passing vacuously.
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

  /// The state-dependent enabled actions at a `(state, pending landing effect)`
  /// node. Free commands (join / acquire / request) model concurrent members
  /// acting at any time; the completions resolve the SINGLE pending landing
  /// effect, exploring EVERY outcome branch (the serial queue means at most one
  /// landing effect is ever pending).
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
      match pend with
      | Some(CohortEffect.Rebase(id, _, _)) ->
        yield CohortCommand.RebaseCompleted(id, Result.Ok("R-" + (let (LandingId x) = id in x))), [||]
        yield CohortCommand.RebaseCompleted(id, Result.Error [ "cf" ]), [||]
      | Some(CohortEffect.ComputeAffected(id, _, _)) -> yield CohortCommand.AffectedComputed(id, [ TestId "t" ]), [||]
      | Some(CohortEffect.RunTests(id, _)) ->
        yield CohortCommand.TestsCompleted(id, []), [||]
        yield CohortCommand.TestsCompleted(id, [ TestId "t" ]), [||]
        yield CohortCommand.VerificationInconclusive(id, "x"), [||]
      | Some(CohortEffect.FastForward(id, sha)) -> yield CohortCommand.FastForwardCompleted(id, sha), [||]
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
