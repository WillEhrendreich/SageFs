namespace SageFs.Simulation

open SageFs
open SageFs.Cohort

/// Cohort keystone P2 (cohort-member-identity-as-capability.md H1): a pure
/// linearizability CHECKER for `Cohort.decide` under real concurrency.
///
/// The model: a set of ops is issued CONCURRENTLY to the real `CohortOwner`
/// actor. Because that actor serializes every `CohortCommand` through its
/// mailbox (`CohortOwner.fs`'s `handle`/`MailboxProcessor.Start`), the real
/// execution corresponds to SOME sequential order — whichever order the
/// mailbox actually applied them in. The observed outcome is LINEARIZABLE
/// iff there EXISTS a permutation of the concurrent ops such that folding
/// `Cohort.decide` from the known initial state in that order reproduces
/// both the observed per-op outcome (accepted, or refused with the SAME
/// error case) and the observed final state (structural equality). A
/// correctly-serializing actor is always linearizable — its real arrival
/// order IS one such permutation. A shell that mutates state OUTSIDE the
/// mailbox (a lost-update race) can produce an outcome no sequential
/// permutation of `decide` could ever produce.
///
/// This module is pure — no IO, no mailbox, no threads. `SageFs.Tests`'
/// `CohortLinearizabilityTests.fs` is the concurrency DRIVER that spins the
/// real `CohortOwner`, fires a concurrent burst, and hands this checker the
/// observed outcome.
module Linearizability =

  /// An op's identity in a concurrently-issued burst. Kept a plain `int`
  /// (0-based index, caller's choice) rather than a fully generic type
  /// parameter — the search space this checker explores is deliberately
  /// small (K ≤ 4, so at most 24 permutations), and an int index is all a
  /// driver needs to correlate a posted command with its captured result.
  type OpId = int

  /// What the REAL run actually produced for a set of concurrently-issued
  /// ops: each op's own accept/refuse outcome, trimmed to `Result<unit, _>`
  /// (never the state delta — two different valid *orderings* can accept the
  /// SAME op while producing different intermediate states; only the FINAL
  /// state is compared, and only once, structurally) plus the state
  /// everything settled into.
  type Observed<'m when 'm: comparison> = {
    PerOp: Map<OpId, Result<unit, CohortError<'m>>>
    Final: CohortState<'m>
  }

  /// Modelled as a DU, never a bool — "yes" carries the witness order that
  /// proves it; "no" carries a diagnosis, not just a flag.
  [<RequireQualifiedAccess>]
  type LinearizationResult =
    | Linearizable of witnessOrder: OpId list
    | NotLinearizable of reason: string

  // ── Error-kind comparison (§ "Ok↔Ok, Error kind ↔ same Error kind") ──────

  /// One marker per `CohortError` case, ignoring payload. Exhaustive by
  /// construction (`Cohort.fs`'s own "default policy" discipline): if
  /// `CohortError<'m>` ever grows a case, this match breaks the build rather
  /// than silently treating the new case as "no error" or crashing the
  /// checker at runtime.
  [<RequireQualifiedAccess>]
  type private ErrorKind =
    | DuplicateJoin
    | MemberNotPresent
    | ClaimConflict
    | NotClaimHolder
    | UnknownClaim
    | ClaimNotOrphaned
    | DuplicateClaimId
    | StaleClaimFence
    | InvalidPurpose
    | InvalidStatement
    | UnknownLanding
    | DuplicateLandingId
    | NotLandingRequester
    | LandingNotAtFrontOfQueue
    | LandingNotInExpectedState
    | NotConductor

  let private kindOf (err: CohortError<'m>) : ErrorKind =
    match err with
    | CohortError.DuplicateJoin _ -> ErrorKind.DuplicateJoin
    | CohortError.MemberNotPresent _ -> ErrorKind.MemberNotPresent
    | CohortError.ClaimConflict _ -> ErrorKind.ClaimConflict
    | CohortError.NotClaimHolder _ -> ErrorKind.NotClaimHolder
    | CohortError.UnknownClaim _ -> ErrorKind.UnknownClaim
    | CohortError.ClaimNotOrphaned _ -> ErrorKind.ClaimNotOrphaned
    | CohortError.DuplicateClaimId _ -> ErrorKind.DuplicateClaimId
    | CohortError.StaleClaimFence _ -> ErrorKind.StaleClaimFence
    | CohortError.InvalidPurpose _ -> ErrorKind.InvalidPurpose
    | CohortError.InvalidStatement _ -> ErrorKind.InvalidStatement
    | CohortError.UnknownLanding _ -> ErrorKind.UnknownLanding
    | CohortError.DuplicateLandingId _ -> ErrorKind.DuplicateLandingId
    | CohortError.NotLandingRequester _ -> ErrorKind.NotLandingRequester
    | CohortError.LandingNotAtFrontOfQueue _ -> ErrorKind.LandingNotAtFrontOfQueue
    | CohortError.LandingNotInExpectedState _ -> ErrorKind.LandingNotInExpectedState
    | CohortError.NotConductor _ -> ErrorKind.NotConductor

  /// `Ok () ↔ Ok ()`; `Error e1 ↔ Error e2` iff same `ErrorKind` (payload —
  /// e.g. WHICH member holds a conflicting claim — is allowed to differ
  /// between permutations, since an errored command never mutates state, so
  /// its payload is a function of whichever claim/landing happened to exist
  /// at that point in THIS candidate order).
  let private agrees (expected: Result<unit, CohortError<'m>>) (actual: Result<unit, CohortError<'m>>) : bool =
    match expected, actual with
    | Ok(), Ok() -> true
    | Error e1, Error e2 -> kindOf e1 = kindOf e2
    | Ok(), Error _
    | Error _, Ok() -> false

  // ── Permutations (n! — caller keeps n small, K ≤ 4) ───────────────────────

  let rec private permutationsOf (xs: OpId list) : OpId list list =
    match xs with
    | [] -> [ [] ]
    | [ x ] -> [ [ x ] ]
    | _ ->
      xs
      |> List.collect (fun x ->
        let rest = xs |> List.filter (fun y -> y <> x)
        permutationsOf rest |> List.map (fun p -> x :: p))

  // ── Folding one candidate order, with early pruning ───────────────────────

  /// The outcome of folding `decide` over one candidate permutation, checked
  /// against `observed.PerOp` as it goes (early pruning: the fold stops the
  /// instant one op's outcome disagrees — it never bothers computing the
  /// rest of that permutation).
  [<RequireQualifiedAccess>]
  type private FoldOutcome<'m when 'm: comparison> =
    /// Every op's outcome agreed, in this order, all the way to the end —
    /// carries the state the fold actually landed on.
    | FullAgreement of CohortState<'m>
    /// The fold disagreed with `observed.PerOp` at this op (0-based position
    /// in the candidate order) — `expected` is what was observed, `actual`
    /// is what folding `decide` in THIS order produced instead.
    | Disagreement of atPosition: int * opId: OpId * expected: Result<unit, CohortError<'m>> * actual: Result<unit, CohortError<'m>>

  let rec private foldChecked
    (clock: Clock)
    (entropyOf: OpId -> Entropy)
    (opById: Map<OpId, CohortCommand<'m>>)
    (observedPerOp: Map<OpId, Result<unit, CohortError<'m>>>)
    (state: CohortState<'m>)
    (position: int)
    (remaining: OpId list)
    : FoldOutcome<'m> =
    match remaining with
    | [] -> FoldOutcome.FullAgreement state
    | opId :: rest ->
      let cmd = opById.[opId]
      let newState, actual =
        match decide clock (entropyOf opId) state cmd with
        | Ok(s, _events, _effects) -> s, Ok()
        | Error err -> state, Error err
      let expected = observedPerOp.[opId]
      if agrees expected actual then
        foldChecked clock entropyOf opById observedPerOp newState (position + 1) rest
      else
        FoldOutcome.Disagreement(position, opId, expected, actual)

  // ── The checker ────────────────────────────────────────────────────────

  /// `isLinearizable initial clock entropyOf ops observed` searches every
  /// permutation of `ops` (n!, keep n ≤ 4): a permutation MATCHES iff
  /// folding `Cohort.decide` in that order from `initial` (a) agrees with
  /// `observed.PerOp` for every op (`Ok`↔`Ok`, same `Error` case — pruned
  /// early, the first disagreement abandons that permutation) AND (b) the
  /// resulting final state is structurally equal to `observed.Final`.
  /// `entropyOf` binds entropy to the OP's own identity, never to its
  /// position in a candidate order — modelling "each op carries its own
  /// nonce, chosen by its caller before racing to be applied", not "the Nth
  /// op folded gets the Nth entropy call". Without this, replaying the same
  /// op at a different position in a different candidate order could mint a
  /// DIFFERENT id purely from trying it in a different slot — a checker
  /// artifact, not a real disagreement about whether the outcome is
  /// explainable by some sequential order.
  let isLinearizable
    (initial: CohortState<'m>)
    (clock: Clock)
    (entropyOf: OpId -> Entropy)
    (ops: (OpId * CohortCommand<'m>) list)
    (observed: Observed<'m>)
    : LinearizationResult =
    let opById = ops |> List.map (fun (id, cmd) -> id, cmd) |> Map.ofList
    let opIds = ops |> List.map fst
    let candidates = permutationsOf opIds

    let witness =
      candidates
      |> List.tryPick (fun order ->
        match foldChecked clock entropyOf opById observed.PerOp initial 0 order with
        | FoldOutcome.FullAgreement finalState when finalState = observed.Final -> Some order
        | FoldOutcome.FullAgreement _
        | FoldOutcome.Disagreement _ -> None)

    match witness with
    | Some order -> LinearizationResult.Linearizable order
    | None ->
      // Diagnostics: report the permutation that got furthest (either the
      // longest per-op-agreeing prefix before a disagreement, or — the more
      // interesting case — a permutation whose per-op outcomes ALL agreed
      // but whose final state still differs from `observed.Final`, which
      // means the concrete state itself is not explainable by ANY
      // sequential order even though the accept/refuse pattern is).
      let diagnose order =
        match foldChecked clock entropyOf opById observed.PerOp initial 0 order with
        | FoldOutcome.FullAgreement finalState ->
          sprintf
            "order %A: every op's outcome agreed, but the resulting state differs from the observed final state (a sequential replay in this order can't reach the state the real run ended in)"
            order
        | FoldOutcome.Disagreement(pos, opId, expected, actual) ->
          sprintf "order %A: op %d (position %d) expected %A but folding decide in this order produced %A" order opId pos expected actual

      let best =
        candidates
        |> List.map (fun order ->
          order,
          match foldChecked clock entropyOf opById observed.PerOp initial 0 order with
          | FoldOutcome.FullAgreement _ -> System.Int32.MaxValue
          | FoldOutcome.Disagreement(pos, _, _, _) -> pos)
        |> List.sortByDescending snd
        |> List.tryHead

      let reason =
        match best with
        | Some(order, _) ->
          sprintf
            "no permutation of %d ops (out of %d checked) reproduces the observed per-op outcomes and final state. Closest: %s"
            (List.length opIds) (List.length candidates) (diagnose order)
        | None ->
          sprintf "no permutation of %d ops (out of %d checked) reproduces the observed per-op outcomes and final state" (List.length opIds) (List.length candidates)

      LinearizationResult.NotLinearizable reason
