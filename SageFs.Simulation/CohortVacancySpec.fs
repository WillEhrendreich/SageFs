namespace SageFs.Simulation

open System.Collections.Generic
open SageFs
open SageFs.Cohort

/// A SECOND, deliberately SMALL exhaustive proof — over MEMBERSHIP ONLY.
///
/// `CohortSpec.fs`'s exhaustive BFS does not model membership at all: `Depart`,
/// `RenewLease`, `Tick` and `DelegateConductor` are absent from its alphabet, and
/// it runs on ONE fixed clock, so no lease ever expires inside it. Adding them
/// there would multiply its 351k-node exploration out of reach. So membership —
/// the axis the conductor-vacancy defect actually lives on — gets its own
/// exploration instead, sized to close instantly (measured: 149 distinct states,
/// ~0.2s).
///
/// The alphabet is members x presence x conductor x claim-holder:
///   * `Join m` / `Depart m` / `RenewLease m` for each of two members,
///   * `AcquireClaim`/`ReleaseClaim` for one scope (so a claim can keep a
///     departed member's seat alive and exercise the purge exemption),
///   * `DelegateConductor` from whoever holds the seat to the other member,
///     and
///   * `ClearClaim` by every present member over every claim in every state, at the claim's current fence: the
///     conductor's release of a claim whose holder is silent or gone, which needs the clocks below because "silent"
///     is a lease that went unrenewed for `Timeouts.cohortHolderSilentAfter`, and
///   * `Tick` as a SINGLE "everything silent lapses" action — the reaper's
///     whole job in one edge, which is what keeps this from exploding into
///     every intermediate clock.
///
/// Three clocks are explored from each node (now, +1 leaseWindow, +2 lease
/// windows). That is the only thing a lease can do, so it covers expiry and
/// purge without modelling a timeline.
///
/// ONE modelling decision is forced by measurement rather than taste, and it is
/// recorded here because a silently-wrong choice would make this proof look
/// stronger than it is. The dedup KEY is the PROJECTION the rules read —
/// presence per member, the conductor binding, and who HOLDS a claim per scope
/// — not the whole `CohortState`. Keying on the whole state does not close:
/// `CohortState.NextFence` is a monotone GLOBAL counter and claim ids are minted
/// from the caller's entropy, so every release/re-acquire cycle is a genuinely
/// new state and the frontier never empties (measured: 1,000,000+ nodes and
/// still climbing, with the full-state key). Since both rules below read nothing
/// but the projection, two states sharing a key are interchangeable FOR THESE
/// RULES, and claiming a proof over states we did not enumerate would be the
/// overclaim this repo's roast doctrine exists to prevent.
module CohortVacancySpec =

  /// Member identity in the bounded model is an opaque string.
  type Member = string

  type Decide =
    Clock
      -> Entropy
      -> CohortState<Member>
      -> CohortCommand<Member>
      -> Result<CohortState<Member> * CohortEvent<Member> list * CohortEffect<Member> list, CohortError<Member>>

  // ── Named rules — state predicates (CsCheck `Rule`s) ──────────────────────

  /// An id deliberately outside the model's member alphabet, used to check that
  /// the door fails closed for someone who never joined at all.
  let notAMember : Member = "\u0000not-a-member"

  /// THE ROOT INVARIANT. `ConductorBinding`'s own doc makes `Bound m` mean
  /// "m is Present"; this is the check that it stayed true. Before the typed
  /// binding it had no expression at all — an `'m option` said nothing about
  /// presence — which is precisely how a departed conductor came to hold the
  /// highest authority.
  let boundIsPresent (s: CohortState<Member>) : bool =
    match s.Conductor with
    | ConductorBinding.Bound m ->
      match Map.tryFind m s.Members with
      | Some { Presence = MemberPresence.Present } -> true
      | _ -> false
    | ConductorBinding.NeverBound
    | ConductorBinding.Vacant _ -> true

  /// FAIL CLOSED, stated over the real door (`Authority.present`) rather than
  /// over the binding: presenting as `Conductor` IMPLIES the seat is `Bound` to
  /// that same member.
  ///
  /// The direction of this implication is the whole point, and it is easy to
  /// write backwards. `ConductorOnlyWhenSeatEmpty` — "no member may be Conductor
  /// unless the seat is bound" — is FALSE on the one state that is supposed to
  /// work: `a` joins a never-bound cohort, binds the seat, and legitimately IS
  /// the conductor. This rule therefore asserts the one-way implication that
  /// actually closes the fail-open (no other direction admits a stale binding),
  /// and the complement that makes it a two-way statement about the DOOR is the
  /// first rule plus `Bound m`'s own invariant.
  let conductorOnlyWhenBoundToThem (s: CohortState<Member>) : bool =
    let membersOk =
      s.Members
      |> Map.toList
      |> List.forall (fun (m, _) ->
        match Authority.present m s with
        | Authority.Conductor _ -> s.Conductor = ConductorBinding.Bound m
        | Authority.Member _
        | Authority.Anonymous -> true)
    // A member who never joined is checked too, because the old bug's shape was
    // "this id resolves to Conductor regardless of whether there is a seat".
    let strangerOk =
      match Authority.present notAMember s with
      | Authority.Conductor _ -> false
      | Authority.Member _
      | Authority.Anonymous -> true
    membersOk && strangerOk

  /// No two `Held` claims cover overlapping scope, whatever mix of acquires, departures, reaper ticks and clears
  /// got the cohort here: clearing frees a scope and never double-books one.
  let claimsExclusive (s: CohortState<Member>) : bool =
    let held =
      s.Claims
      |> Map.toList
      |> List.choose (fun (_, c) -> match c.State with ClaimState.Held _ -> Some c.Scope | _ -> None)
    let rec pairsOk =
      function
      | [] -> true
      | x :: rest -> (rest |> List.forall (fun y -> not (ClaimScope.overlaps x y))) && pairsOk rest
    pairsOk held

  let rules : (string * (CohortState<Member> -> bool)) list =
    [ "BOUND-IS-PRESENT", boundIsPresent
      "CONDUCTOR-ONLY-WHEN-BOUND-TO-THEM", conductorOnlyWhenBoundToThem
      "CLAIMS-EXCLUSIVE", claimsExclusive ]

  // ── The bounded alphabet ──────────────────────────────────────────────────

  let private members : Member list = [ "a"; "b" ]
  /// The CLAIM scope the single modelled claim is taken on — NOT the cohort scope.
  let private scope = ClaimScope.File "f1"
  /// The cohort scope every command names, matching the `CohortState.empty` the
  /// exploration starts from: one machine-wide cohort, as in v1. The spec drives
  /// the reducer with no session and no working directory, so what it proves is
  /// membership and the conductor seat, not scope isolation.
  let private cohortScope = CohortScope.Machine
  let private baseClock : Clock = System.DateTime(2020, 1, 1)

  /// `now`, one window, two windows. Far enough for `Retention.sweep` to have
  /// run as well as the reaper, since `settledRetention` and `leaseWindow` are
  /// the same size in the product's `Timeouts` today — if they ever diverge, the
  /// "+2 windows" clock is the one that has to move.
  let private clocks : Clock list =
    [ baseClock
      baseClock.Add leaseWindow
      baseClock.Add(2.0 * leaseWindow) ]

  /// One fixed entropy per claim so the same logical acquire mints the same id
  /// at every node — the modelled determinism `CohortSpec.fs` also relies on.
  let private claimEntropy : Entropy = [| 1uy |]

  let private isPresent (s: CohortState<Member>) (m: Member) =
    match Map.tryFind m s.Members with
    | Some { Presence = MemberPresence.Present } -> true
    | _ -> false

  let private heldBy (s: CohortState<Member>) (m: Member) =
    s.Claims
    |> Map.toList
    |> List.choose (fun (cid, c) -> match c.State with ClaimState.Held h when h = m -> Some(cid, c.Fence) | _ -> None)

  // ── Named rules over ONE accepted step (before, command, after) ───────────

  /// A person fills an EMPTY seat. `SeatConductor` over a seat that is bound would move a held seat, which is the
  /// conductor's `DelegateConductor`, so an accepted seating over a `Bound` binding is the rule broken.
  let seatingFillsOnlyAnEmptySeat (before: CohortState<Member>) (command: CohortCommand<Member>) (_after: CohortState<Member>) : bool =
    match command, before.Conductor with
    | CohortCommand.SeatConductor _, ConductorBinding.Bound _ -> false
    | _ -> true

  /// A seating names a member who was present, and the seat is bound to exactly that member afterwards.
  let seatingNamesAPresentMember (before: CohortState<Member>) (command: CohortCommand<Member>) (after: CohortState<Member>) : bool =
    match command with
    | CohortCommand.SeatConductor(target, _) -> isPresent before target && after.Conductor = ConductorBinding.Bound target
    | _ -> true

  /// A person's replacement names the holder it replaces: it is accepted only over a seat bound to exactly that
  /// member, so a person who looked at the seat cannot move one that changed under them, and never over a holder
  /// that is also the replacement.
  let replaceNamesTheHolder (before: CohortState<Member>) (command: CohortCommand<Member>) (_after: CohortState<Member>) : bool =
    match command with
    | CohortCommand.ReplaceConductor(holder, target, _) -> before.Conductor = ConductorBinding.Bound holder && holder <> target
    | _ -> true

  /// A replacement seats a member who was present and moves the seat and nothing else: every member keeps its
  /// presence (the replaced holder becomes an ordinary member, neither departed nor purged) and every claim keeps its
  /// holder.
  let replaceMovesOnlyTheSeat (before: CohortState<Member>) (command: CohortCommand<Member>) (after: CohortState<Member>) : bool =
    match command with
    | CohortCommand.ReplaceConductor(_, target, _) ->
      isPresent before target
      && after.Conductor = ConductorBinding.Bound target
      && after.Members = before.Members
      && after.Claims = before.Claims
    | _ -> true

  /// A clear is the conductor's: it is accepted only over a seat bound to the member that issued it.
  let clearIsTheConductors (before: CohortState<Member>) (command: CohortCommand<Member>) (_after: CohortState<Member>) : bool =
    match command with
    | CohortCommand.ClearClaim(by, _, _, _) -> before.Conductor = ConductorBinding.Bound by
    | _ -> true

  /// A claim of a member that is here is never cleared. A `Held` claim goes only when its holder had been silent for the
  /// window at the clock of the clear (read back from the `Released(_, at)` the clear stamps), and an `Orphaned` claim
  /// has no holder to protect. A clear accepted over a claim that was already released broke the rule too.
  let clearNeverTakesAPresentHoldersClaim (before: CohortState<Member>) (command: CohortCommand<Member>) (after: CohortState<Member>) : bool =
    match command with
    | CohortCommand.ClearClaim(_, claimId, _, _) ->
      match Map.tryFind claimId before.Claims, Map.tryFind claimId after.Claims with
      | Some { State = ClaimState.Held holder }, Some { State = ClaimState.Released(_, at) } ->
        match Map.tryFind holder before.Members with
        | Some record -> at - record.LastRenewal >= Timeouts.cohortHolderSilentAfter
        | None -> true
      | Some { State = ClaimState.Orphaned _ }, Some { State = ClaimState.Released _ } -> true
      | _ -> false
    | _ -> true

  /// A clear moves one claim and nothing else: that claim ends released at a later fence (so the old holder's release
  /// is stale and the claim is never held again under its old fence), and every other claim, every member, the seat,
  /// the landings and the queue are exactly as they were. So a clear never loses another member's claim.
  let clearTouchesOnlyThatClaim (before: CohortState<Member>) (command: CohortCommand<Member>) (after: CohortState<Member>) : bool =
    match command with
    | CohortCommand.ClearClaim(_, claimId, fence, _) ->
      match Map.tryFind claimId after.Claims with
      | Some { State = ClaimState.Released _; Fence = newFence } ->
        newFence > fence
        && Map.remove claimId after.Claims = Map.remove claimId before.Claims
        && after.Members = before.Members
        && after.Conductor = before.Conductor
        && after.Landings = before.Landings
        && after.Queue = before.Queue
      | _ -> false
    | _ -> true

  let stepRules : (string * (CohortState<Member> -> CohortCommand<Member> -> CohortState<Member> -> bool)) list =
    [ "SEATING-FILLS-ONLY-AN-EMPTY-SEAT", seatingFillsOnlyAnEmptySeat
      "SEATING-NAMES-A-PRESENT-MEMBER", seatingNamesAPresentMember
      "REPLACE-NAMES-THE-HOLDER", replaceNamesTheHolder
      "REPLACE-MOVES-ONLY-THE-SEAT", replaceMovesOnlyTheSeat
      "CLEAR-IS-THE-CONDUCTORS", clearIsTheConductors
      "CLEAR-NEVER-TAKES-A-PRESENT-HOLDERS-CLAIM", clearNeverTakesAPresentHoldersClaim
      "CLEAR-TOUCHES-ONLY-THAT-CLAIM", clearTouchesOnlyThatClaim ]

  /// The dedup key: exactly the projection the two rules above read. See the
  /// module doc for why this is the key and not the whole state.
  let private keyOf (s: CohortState<Member>) : string =
    let presence =
      s.Members
      |> Map.toList
      |> List.map (fun (m, r) ->
        sprintf
          "%s:%s"
          m
          (match r.Presence with
           | MemberPresence.Present -> "P"
           | MemberPresence.Departed _ -> "D"))
      |> String.concat ","
    let holders =
      s.Claims
      |> Map.toList
      |> List.choose (fun (cid, c) -> match c.State with ClaimState.Held h -> Some(sprintf "%A=%s" cid h) | _ -> None)
      |> List.sort
      |> String.concat ","
    sprintf "%s | %A | %s" presence s.Conductor holders

  let private enabled (s: CohortState<Member>) : (CohortCommand<Member> * Entropy) list =
    let acc = ResizeArray<CohortCommand<Member> * Entropy>()
    for m in members do
      if not (isPresent s m) then
        acc.Add(CohortCommand.Join(m, JoinableRole.Implementer, None, cohortScope), [||])
    for m in members do
      if isPresent s m then
        acc.Add(CohortCommand.Depart(m, cohortScope), [||])
        acc.Add(CohortCommand.RenewLease(m, cohortScope), [||])
        acc.Add(CohortCommand.AcquireClaim(m, scope, "p", cohortScope), claimEntropy)
        for (cid, fence) in heldBy s m do
          acc.Add(CohortCommand.ReleaseClaim(m, cid, fence, cohortScope), [||])
        match s.Conductor with
        | ConductorBinding.Bound c when c <> m ->
          acc.Add(CohortCommand.DelegateConductor(c, m, cohortScope), [||])
        // default policy: `NeverBound` has no holder to delegate from and
        // `Vacant` has nobody to delegate — the vacancy is filled by a person,
        // never by the holder of a vacancy delegating one.
        | ConductorBinding.NeverBound
        | ConductorBinding.Bound _
        | ConductorBinding.Vacant _ -> ()
    // A person's seating, offered for EVERY member in EVERY state (a bound seat, an absent member, a vacancy), so the
    // refusals are explored as well as the acceptances.
    for m in members do
      acc.Add(CohortCommand.SeatConductor(m, cohortScope), [||])
    // A person's replacement, for EVERY holder name and EVERY target in every state: the right holder, a stale one,
    // the holder as its own replacement, an absent target, and a seat that is empty.
    for holder in members do
      for m in members do
        acc.Add(CohortCommand.ReplaceConductor(holder, m, cohortScope), [||])
    // A conductor's clear, offered for EVERY present member (so the conductor and a non-conductor both try) and EVERY claim
    // in whatever state it is in (held by a silent or an active member, orphaned, released), at the claim's current fence.
    for m in members do
      if isPresent s m then
        for KeyValue(cid, c) in s.Claims do
          acc.Add(CohortCommand.ClearClaim(m, cid, c.Fence, cohortScope), [||])
    // "everything silent lapses", in one edge
    acc.Add(CohortCommand.Tick cohortScope, [||])
    List.ofSeq acc

  // ── The exploration ───────────────────────────────────────────────────────

  type ExploreResult =
    { /// Distinct projection-keys reached — the enumerated space.
      Nodes: int
      /// True iff the frontier emptied within the cap — an empty `Violations`
      /// is then a PROOF over that space, not a sample.
      Complete: bool
      Capped: bool
      Violations: string list }

  let explore (decide: Decide) (cap: int) : ExploreResult =
    let visited = HashSet<string>(HashIdentity.Structural)
    let frontier = Queue<CohortState<Member>>()
    let start = CohortState.empty ()
    visited.Add(keyOf start) |> ignore
    frontier.Enqueue start
    let violations = HashSet<string>()
    let mutable capped = false
    while frontier.Count > 0 && not capped do
      let s = frontier.Dequeue ()
      for name, rule in rules do
        if not (rule s) then violations.Add name |> ignore
      for clock in clocks do
        for cmd, ent in enabled s do
          match decide clock ent s cmd with
          | Result.Ok(s', _, _) ->
            for name, rule in stepRules do
              if not (rule s cmd s') then violations.Add name |> ignore
            if visited.Add(keyOf s') then
              if visited.Count > cap then capped <- true else frontier.Enqueue s'
          | Result.Error _ -> ()
    { Nodes = visited.Count
      Complete = (frontier.Count = 0 && not capped)
      Capped = capped
      Violations = List.ofSeq violations }

  /// A generous cap: this space is deliberately tiny (see the module doc).
  let defaultCap = 100_000

  /// PROVE the rules over the real `Cohort.decide`.
  let proof () : ExploreResult = explore (fun c e s cmd -> Cohort.decide c e s cmd) defaultCap

  // ── Faults mode: the checker must have teeth ──────────────────────────────

  /// The PRE-FIX world, re-expressed in the new type: `departMember` marked the
  /// seat Departed and never touched the binding, so the binding went on naming
  /// a member who had left. This reducer is the real `decide` with that one
  /// defect re-imposed on every result — exactly the state the old code
  /// produced and the old `Authority.present` honoured.
  let faultDepartedConductorKeepsSeat : Decide =
    fun clk ent state command ->
      match Cohort.decide clk ent state command with
      | Error e -> Error e
      | Ok(s, evs, effs) ->
        let faulted =
          match s.Conductor with
          | ConductorBinding.Vacant (former, _, _) -> { s with Conductor = ConductorBinding.Bound former }
          | ConductorBinding.NeverBound
          | ConductorBinding.Bound _ -> s
        Ok(faulted, evs, effs)

  /// Explore through that twin — expected to VIOLATE `BOUND-IS-PRESENT`. A rule
  /// that no broken world can violate is not proving anything, so this is the
  /// half of the module that says the other half is real.
  let faults () : ExploreResult = explore faultDepartedConductorKeepsSeat defaultCap

  /// A careless seating: whoever is named gets the empty seat, present or not. The world where `SeatConductor` skips
  /// the presence check, which is the `Bound m` invariant ("m is Present") broken by the one command a person types.
  let faultSeatsAbsentMember : Decide =
    fun clk ent state command ->
      match command, state.Conductor with
      | CohortCommand.SeatConductor(target, _), (ConductorBinding.Vacant _ | ConductorBinding.NeverBound) when not (isPresent state target) ->
        Ok({ state with Conductor = ConductorBinding.Bound target }, [ CohortEvent.ConductorSeated(target, state.Conductor) ], [])
      | _ -> Cohort.decide clk ent state command

  /// A careless seating the other way: it takes a seat that is held. The person's command would then move a conductor
  /// who never agreed to go, which only the conductor may do (`DelegateConductor`).
  let faultSeatsOverBoundConductor : Decide =
    fun clk ent state command ->
      match command, state.Conductor with
      | CohortCommand.SeatConductor(target, _), ConductorBinding.Bound _ when isPresent state target ->
        Ok({ state with Conductor = ConductorBinding.Bound target }, [ CohortEvent.ConductorSeated(target, state.Conductor) ], [])
      | _ -> Cohort.decide clk ent state command

  let seatFaultsAbsent () : ExploreResult = explore faultSeatsAbsentMember defaultCap
  let seatFaultsOverBound () : ExploreResult = explore faultSeatsOverBoundConductor defaultCap

  /// A replacement that does not check who it replaces: whoever the person names, the seat moves. The person who
  /// looked at the seat then moves a conductor that changed under them.
  let faultReplacesWithoutNamingTheHolder : Decide =
    fun clk ent state command ->
      match command, state.Conductor with
      | CohortCommand.ReplaceConductor(holder, target, _), ConductorBinding.Bound sitting when holder <> sitting && target <> sitting && isPresent state target ->
        Ok(
          { state with Conductor = ConductorBinding.Bound target },
          [ CohortEvent.ConductorReplaced(target, ConductorBinding.Vacant(sitting, clk, VacancyReason.Replaced)) ],
          []
        )
      | _ -> Cohort.decide clk ent state command

  /// A replacement that seats a member who is not here: the `Bound m` invariant, broken by the person's command.
  let faultReplaceSeatsAbsentMember : Decide =
    fun clk ent state command ->
      match command, state.Conductor with
      | CohortCommand.ReplaceConductor(holder, target, _), ConductorBinding.Bound sitting when holder = sitting && target <> sitting && not (isPresent state target) ->
        Ok(
          { state with Conductor = ConductorBinding.Bound target },
          [ CohortEvent.ConductorReplaced(target, ConductorBinding.Vacant(sitting, clk, VacancyReason.Replaced)) ],
          []
        )
      | _ -> Cohort.decide clk ent state command

  /// A replacement that also sends the replaced holder away: it is departed on the spot, so it is not the ordinary
  /// member the rules promise (its claims would be orphaned by a command that only meant to move the seat).
  let faultReplaceDepartsTheHolder : Decide =
    fun clk ent state command ->
      match Cohort.decide clk ent state command, command with
      | Ok(after, events, effects), CohortCommand.ReplaceConductor(holder, _, _) ->
        let departed = { after with Members = Map.add holder { after.Members.[holder] with Presence = MemberPresence.Departed clk } after.Members }
        Ok(departed, events, effects)
      | result, _ -> result

  /// The state and event of a clear that asks nothing: the claim is released by `by` at `clk`, at the next fence.
  let forcedClear (clk: Clock) (state: CohortState<Member>) (by: Member) (claim: Claim<Member>) (former: Member) : CohortState<Member> * CohortEvent<Member> list =
    let fence = state.NextFence + 1L<Measures.fence>
    let released = { claim with Fence = fence; State = ClaimState.Released(by, clk) }
    { state with NextFence = fence; Claims = Map.add claim.Id released state.Claims },
    [ CohortEvent.ClaimCleared(claim.Id, former, System.TimeSpan.Zero, by, fence) ]

  /// A clear that never asks whether the holder is here: the conductor clears any `Held` claim. The claim of a member that
  /// renewed a moment ago goes, which is the loss the silence rule exists to prevent.
  let faultClearsAnyClaim : Decide =
    fun clk ent state command ->
      match command with
      | CohortCommand.ClearClaim(by, claimId, fence, _) when Authority.present by state = Authority.Conductor by ->
        match Map.tryFind claimId state.Claims with
        | Some({ State = ClaimState.Held holder } as claim) when claim.Fence = fence ->
          let after, events = forcedClear clk state by claim holder
          Ok(after, events, [])
        | _ -> Cohort.decide clk ent state command
      | _ -> Cohort.decide clk ent state command

  /// A clear any present member may make: a member that is not the conductor is treated as if it were. The seat's holder
  /// stands in for the authority check and the clear is recorded under the name of the member that asked.
  let faultAnyMemberClears : Decide =
    fun clk ent state command ->
      match command, state.Conductor with
      | CohortCommand.ClearClaim(by, claimId, fence, scope), ConductorBinding.Bound conductor when by <> conductor && isPresent state by ->
        match Cohort.decide clk ent state (CohortCommand.ClearClaim(conductor, claimId, fence, scope)) with
        | Ok(after, events, effects) ->
          let relabelled =
            events
            |> List.map (function
              | CohortEvent.ClaimCleared(id, former, silentFor, _, f) -> CohortEvent.ClaimCleared(id, former, silentFor, by, f)
              | other -> other)
          Ok(after, relabelled, effects)
        | Error e -> Error e
      | _ -> Cohort.decide clk ent state command

  /// A clear that forgets to move the fence on: the claim is released at the fence it had, so the old holder's release
  /// presents a fence that still matches, and the compare-and-swap that guards the claim no longer guards it.
  let faultClearKeepsFence : Decide =
    fun clk ent state command ->
      match Cohort.decide clk ent state command, command with
      | Ok(after, events, effects), CohortCommand.ClearClaim(_, claimId, fence, _) ->
        let kept = { after.Claims.[claimId] with Fence = fence }
        Ok({ after with Claims = Map.add claimId kept after.Claims }, events, effects)
      | result, _ -> result

  let clearFaultsAny () : ExploreResult = explore faultClearsAnyClaim defaultCap
  let clearFaultsNonConductor () : ExploreResult = explore faultAnyMemberClears defaultCap
  let clearFaultsKeepFence () : ExploreResult = explore faultClearKeepsFence defaultCap

  let replaceFaultsStale () : ExploreResult = explore faultReplacesWithoutNamingTheHolder defaultCap
  let replaceFaultsAbsent () : ExploreResult = explore faultReplaceSeatsAbsentMember defaultCap
  let replaceFaultsDeparts () : ExploreResult = explore faultReplaceDepartsTheHolder defaultCap

  /// The OLD lookup, frozen as a regression witness: the conductor binding
  /// checked BEFORE presence, which is the whole defect in four lines. NOT wired
  /// into any product path — `CohortVacancyTests.fs` drives it to show the
  /// difference rather than merely assert it.
  let presentIgnoringPresence (who: Member) (s: CohortState<Member>) : Authority<Member> =
    match s.Conductor with
    | ConductorBinding.Bound c when c = who -> Authority.Conductor who
    | ConductorBinding.NeverBound
    | ConductorBinding.Bound _
    | ConductorBinding.Vacant _ ->
      match Map.tryFind who s.Members with
      | Some { Presence = MemberPresence.Present; Role = role } -> Authority.Member(who, role)
      | Some { Presence = MemberPresence.Departed _ } -> Authority.Anonymous
      | None -> Authority.Anonymous
