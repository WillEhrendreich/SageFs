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

  let rules : (string * (CohortState<Member> -> bool)) list =
    [ "BOUND-IS-PRESENT", boundIsPresent
      "CONDUCTOR-ONLY-WHEN-BOUND-TO-THEM", conductorOnlyWhenBoundToThem ]

  // ── The bounded alphabet ──────────────────────────────────────────────────

  let private members : Member list = [ "a"; "b" ]
  let private scope = ClaimScope.File "f1"
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
        acc.Add(CohortCommand.Join(m, JoinableRole.Implementer, None), [||])
    for m in members do
      if isPresent s m then
        acc.Add(CohortCommand.Depart m, [||])
        acc.Add(CohortCommand.RenewLease m, [||])
        acc.Add(CohortCommand.AcquireClaim(m, scope, "p"), claimEntropy)
        for (cid, fence) in heldBy s m do
          acc.Add(CohortCommand.ReleaseClaim(m, cid, fence), [||])
        match s.Conductor with
        | ConductorBinding.Bound c when c <> m ->
          acc.Add(CohortCommand.DelegateConductor(c, m), [||])
        // default policy: `NeverBound` has no holder to delegate from and
        // `Vacant` has nobody to delegate — the vacancy is filled by a person,
        // never by the holder of a vacancy delegating one.
        | ConductorBinding.NeverBound
        | ConductorBinding.Bound _
        | ConductorBinding.Vacant _ -> ()
    // "everything silent lapses", in one edge
    acc.Add(CohortCommand.Tick, [||])
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
