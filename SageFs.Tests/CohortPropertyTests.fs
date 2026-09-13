/// Phase 1 item 7 (sagefs-multiagent-vision.md §5.2, §7.1, §7.3, §10): the pure
/// cohort core's seventeen model-based properties, run over generated command
/// sequences (1-12), generated strategies on generated schedules (13-16), and a
/// replayed regression corpus (17).
///
/// Properties 8 and 9 are DEFERRED, not silently dropped — they need `cohortTools`
/// and `Authority.tryPresent` (Phase 1 items 8 and 11, §8.1), which this island
/// does not own; see the `Tests.skiptest` cases below for exactly why.
module SageFs.Tests.CohortPropertyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Cohort
open SageFs.Tests.SharedGenerators

// ── A concrete member identity for these tests only ───────────────────────
//
// Cohort.fs keeps identity opaque ('m). Here we instantiate it with a minimal
// record whose equality/ordering is by Id alone, and whose Display can
// deliberately collide between distinct agents — exactly what property 15
// needs to prove bound identity can't be spoofed by a shared display name.

[<CustomEquality; CustomComparison>]
type Agent =
  { Id: int; Display: string }

  override this.Equals(o) =
    match o with
    | :? Agent as a -> a.Id = this.Id
    | _ -> false

  override this.GetHashCode() = this.Id

  interface IComparable with
    member this.CompareTo(o) =
      match o with
      | :? Agent as a -> compare this.Id a.Id
      | _ -> invalidArg "o" "not an Agent"

let agentOf (i: int) : Agent =
  { Id = 1 + (abs i % 4); Display = sprintf "agent-%d" (1 + abs i % 4) }

// ── Generators ──────────────────────────────────────────────────────────

let private genRole =
  Gen.elements [ JoinableRole.Implementer; JoinableRole.Verifier; JoinableRole.Observer ]

let private scopePool =
  [| ClaimScope.File "SageFs.Core/A.fs"
     ClaimScope.File "SageFs.Core/B.fs"
     ClaimScope.Project "SageFs.Core/SageFs.Core.fsproj"
     ClaimScope.Project "SageFs/SageFs.fsproj" |]

let private scopeOf (i: int) = scopePool.[abs i % scopePool.Length]

let private pathPool = [| "SageFs.Core/A.fs"; "SageFs.Core/inner/C.fs"; "SageFs/D.fs" |]
let private pathOf (i: int) = pathPool.[abs i % pathPool.Length]

/// An intent is resolved against the CURRENT harness state at fold time (a
/// model-based command, not a literal `CohortCommand` — claim/landing ids do
/// not exist until something has minted them). An intent that does not resolve
/// (e.g. "release claim #3" when nothing has been acquired yet) is a no-op,
/// which is what lets pure random generation exercise real transitions without
/// needing a hand-written well-formedness filter.
type Intent =
  | IJoin of agent: int * role: JoinableRole
  | IDepart of agent: int
  | IRenewLease of agent: int
  | ITick of minutesLater: int
  | IAcquire of agent: int * scopeIdx: int
  | IReleaseOwn of agent: int * claimIdx: int
  | IReassign of by: int * claimIdx: int * toAgent: int
  | IObserveSave of agent: int * pathIdx: int
  | IRequestLanding of agent: int * commit: int
  | IRebaseOk of landingIdx: int
  | IRebaseConflict of landingIdx: int * file: int
  | IAffected of landingIdx: int * testCount: int
  | ITestsPass of landingIdx: int
  | ITestsFail of landingIdx: int * failCount: int
  | IFastForward of landingIdx: int
  | IWithdraw of agent: int * landingIdx: int
  | IVeto of by: int * landingIdx: int

let private genIntent =
  Gen.frequency [
    3, Gen.map2 (fun a r -> IJoin(a, r)) (Gen.choose (1, 4)) genRole
    2, Gen.map IDepart (Gen.choose (1, 4))
    2, Gen.map IRenewLease (Gen.choose (1, 4))
    2, Gen.map ITick (Gen.choose (0, 60))
    4, Gen.map2 (fun a s -> IAcquire(a, s)) (Gen.choose (1, 4)) (Gen.choose (0, 3))
    3, Gen.map2 (fun a c -> IReleaseOwn(a, c)) (Gen.choose (1, 4)) (Gen.choose (0, 10))
    2, Gen.map3 (fun b c t -> IReassign(b, c, t)) (Gen.choose (1, 4)) (Gen.choose (0, 10)) (Gen.choose (1, 4))
    2, Gen.map2 (fun a p -> IObserveSave(a, p)) (Gen.choose (1, 4)) (Gen.choose (0, 3))
    3, Gen.map2 (fun a c -> IRequestLanding(a, c)) (Gen.choose (1, 4)) (Gen.choose (0, 1000))
    2, Gen.map IRebaseOk (Gen.choose (0, 10))
    1, Gen.map2 (fun l f -> IRebaseConflict(l, f)) (Gen.choose (0, 10)) (Gen.choose (0, 5))
    2, Gen.map2 (fun l t -> IAffected(l, t)) (Gen.choose (0, 10)) (Gen.choose (0, 5))
    2, Gen.map ITestsPass (Gen.choose (0, 10))
    1, Gen.map2 (fun l f -> ITestsFail(l, f)) (Gen.choose (0, 10)) (Gen.choose (0, 3))
    2, Gen.map IFastForward (Gen.choose (0, 10))
    1, Gen.map2 (fun a l -> IWithdraw(a, l)) (Gen.choose (1, 4)) (Gen.choose (0, 10))
    1, Gen.map2 (fun b l -> IVeto(b, l)) (Gen.choose (1, 4)) (Gen.choose (0, 10))
  ]

let private genIntents =
  Gen.choose (5, 40) |> Gen.bind (fun n -> Gen.listOfLength n genIntent)

type private CohortGenerators =
  static member Intent() = Arb.fromGen genIntent
  static member Agent() = Arb.fromGen (Gen.choose (1, 4) |> Gen.map agentOf)

let private cohortConfig = {
  propConfig with
    arbitrary = [ typeof<CohortGenerators> ]
}

// ── The harness: fold Intents through `decide`, keeping a full step log ────

type Step = {
  Before: CohortState<Agent>
  After: CohortState<Agent>
  Command: CohortCommand<Agent>
  Events: CohortEvent<Agent> list
}

type Harness = {
  Clock: DateTime
  NextEntropy: int
  State: CohortState<Agent>
  ClaimHistory: ClaimId list
  LandingHistory: LandingId list
  Steps: Step list
}

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

let private initHarness () : Harness = {
  Clock = epoch
  NextEntropy = 0
  State = CohortState.empty ()
  ClaimHistory = []
  LandingHistory = []
  Steps = []
}

let private applyCommand (h: Harness) (cmd: CohortCommand<Agent>) : Harness =
  let entropy = BitConverter.GetBytes h.NextEntropy
  let h1 = { h with NextEntropy = h.NextEntropy + 1 }
  match decide h1.Clock entropy h1.State cmd with
  | Error _ -> h1
  | Ok(newState, events, _effects) ->
    let newClaims = newState.Claims |> Map.toList |> List.map fst |> List.filter (fun c -> not (List.contains c h1.ClaimHistory))
    let newLandings = newState.Landings |> Map.toList |> List.map fst |> List.filter (fun l -> not (List.contains l h1.LandingHistory))
    { h1 with
        State = newState
        ClaimHistory = h1.ClaimHistory @ newClaims
        LandingHistory = h1.LandingHistory @ newLandings
        Steps = h1.Steps @ [ { Before = h1.State; After = newState; Command = cmd; Events = events } ] }

let private resolveClaim (h: Harness) (idx: int) : ClaimId option =
  if h.ClaimHistory.IsEmpty then None else Some h.ClaimHistory.[abs idx % h.ClaimHistory.Length]

let private resolveLanding (h: Harness) (idx: int) : LandingId option =
  if h.LandingHistory.IsEmpty then None else Some h.LandingHistory.[abs idx % h.LandingHistory.Length]

let private testIdsOf (n: int) (prefix: string) = [ for i in 1 .. (abs n % 5) -> TestId(sprintf "%s%d" prefix i) ]

let private applyIntent (h: Harness) (intent: Intent) : Harness =
  match intent with
  | IJoin(a, role) -> applyCommand h (CohortCommand.Join(agentOf a, role, None))
  | IDepart a -> applyCommand h (CohortCommand.Depart(agentOf a))
  | IRenewLease a -> applyCommand h (CohortCommand.RenewLease(agentOf a))
  | ITick minutes -> applyCommand { h with Clock = h.Clock.AddMinutes(float (abs minutes % 90)) } CohortCommand.Tick
  | IAcquire(a, s) -> applyCommand h (CohortCommand.AcquireClaim(agentOf a, scopeOf s, "purpose"))
  | IReleaseOwn(a, c) ->
    match resolveClaim h c with
    | None -> h
    | Some cid ->
      match Map.tryFind cid h.State.Claims with
      | None -> h
      | Some claim -> applyCommand h (CohortCommand.ReleaseClaim(agentOf a, cid, claim.Fence))
  | IReassign(by, c, toA) ->
    match resolveClaim h c with
    | None -> h
    | Some cid -> applyCommand h (CohortCommand.ReassignClaim(agentOf by, cid, agentOf toA))
  | IObserveSave(a, p) -> applyCommand h (CohortCommand.ObserveSave(agentOf a, pathOf p))
  | IRequestLanding(a, commit) ->
    let agent = agentOf a
    let backing =
      h.State.Claims
      |> Map.toList
      |> List.choose (fun (cid, c) ->
        match c.State with
        | ClaimState.Held holder when holder = agent -> Some(cid, c.Fence)
        | _ -> None)
    applyCommand h (CohortCommand.RequestLanding(agent, backing, [ sprintf "commit-%d" commit ], "landing statement"))
  | IRebaseOk l ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.RebaseCompleted(lid, Ok(sprintf "sha-%d" h.NextEntropy)))
  | IRebaseConflict(l, f) ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.RebaseCompleted(lid, Error [ sprintf "conflict-%d.fs" (abs f) ]))
  | IAffected(l, t) ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.AffectedComputed(lid, testIdsOf t "t"))
  | ITestsPass l ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.TestsCompleted(lid, []))
  | ITestsFail(l, f) ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.TestsCompleted(lid, testIdsOf (1 + abs f) "f"))
  | IFastForward l ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.FastForwardCompleted(lid, sprintf "landed-%d" h.NextEntropy))
  | IWithdraw(a, l) ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.WithdrawLanding(agentOf a, lid))
  | IVeto(by, l) ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.VetoLanding(agentOf by, lid, "veto reason"))

let private run (intents: Intent list) : Harness = intents |> List.fold applyIntent (initHarness ())

let private allStates (h: Harness) : CohortState<Agent> list =
  CohortState.empty () :: (h.Steps |> List.map (fun s -> s.After))

/// `Harness.Steps` (built by `run`/`applyIntent`) does not carry each accepted
/// step's own (Clock, Entropy) — only the running counters that produced them.
/// A faithful `LedgerEntry` needs both, so this re-runs the intents itself,
/// this time recording every accepted (Seq, Clock, Entropy, Command, Events) —
/// the ledger properties 7/10/17 replay.
let private toLedger (intents: Intent list) : LedgerEntry<Agent> list =
  let mutable clock = epoch
  let mutable nextEntropy = 0
  let mutable state = CohortState.empty ()
  let entries = ResizeArray()
  let mutable seq = 0L
  for intent in intents do
    let cmdOpt =
      match intent with
      | IJoin(a, role) -> Some(CohortCommand.Join(agentOf a, role, None))
      | IDepart a -> Some(CohortCommand.Depart(agentOf a))
      | IRenewLease a -> Some(CohortCommand.RenewLease(agentOf a))
      | ITick minutes ->
        clock <- clock.AddMinutes(float (abs minutes % 90))
        Some CohortCommand.Tick
      | IAcquire(a, s) -> Some(CohortCommand.AcquireClaim(agentOf a, scopeOf s, "purpose"))
      | IReleaseOwn(a, c) ->
        if state.Claims.IsEmpty then None
        else
          let cid = state.Claims |> Map.toList |> List.map fst |> fun ids -> ids.[abs c % ids.Length]
          Some(CohortCommand.ReleaseClaim(agentOf a, cid, state.Claims.[cid].Fence))
      | IReassign(by, c, toA) ->
        if state.Claims.IsEmpty then None
        else
          let cid = state.Claims |> Map.toList |> List.map fst |> fun ids -> ids.[abs c % ids.Length]
          Some(CohortCommand.ReassignClaim(agentOf by, cid, agentOf toA))
      | IObserveSave(a, p) -> Some(CohortCommand.ObserveSave(agentOf a, pathOf p))
      | IRequestLanding(a, commit) ->
        let agent = agentOf a
        let backing =
          state.Claims
          |> Map.toList
          |> List.choose (fun (cid, c) ->
            match c.State with
            | ClaimState.Held holder when holder = agent -> Some(cid, c.Fence)
            | _ -> None)
        Some(CohortCommand.RequestLanding(agent, backing, [ sprintf "commit-%d" commit ], "landing statement"))
      | IRebaseOk l ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.RebaseCompleted(lid, Ok(sprintf "sha-%d" nextEntropy)))
      | IRebaseConflict(l, f) ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.RebaseCompleted(lid, Error [ sprintf "conflict-%d.fs" (abs f) ]))
      | IAffected(l, t) ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.AffectedComputed(lid, testIdsOf t "t"))
      | ITestsPass l ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.TestsCompleted(lid, []))
      | ITestsFail(l, f) ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.TestsCompleted(lid, testIdsOf (1 + abs f) "f"))
      | IFastForward l ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.FastForwardCompleted(lid, sprintf "landed-%d" nextEntropy))
      | IWithdraw(a, l) ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.WithdrawLanding(agentOf a, lid))
      | IVeto(by, l) ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.VetoLanding(agentOf by, lid, "veto reason"))
    match cmdOpt with
    | None -> ()
    | Some cmd ->
      let entropy = BitConverter.GetBytes nextEntropy
      nextEntropy <- nextEntropy + 1
      match decide clock entropy state cmd with
      | Error _ -> ()
      | Ok(newState, events, _effects) ->
        entries.Add { Seq = LanguagePrimitives.Int64WithMeasure seq; Clock = clock; Entropy = entropy; Command = cmd; Events = events }
        seq <- seq + 1L
        state <- newState
  List.ofSeq entries

// ── Property 1: at most one exclusive claim per overlapping scope ─────────

let private noOverlappingHeldClaims (state: CohortState<Agent>) =
  let held = state.Claims |> Map.toList |> List.choose (fun (cid, c) -> match c.State with ClaimState.Held _ -> Some(cid, c.Scope) | _ -> None)
  held
  |> List.forall (fun (cid1, s1) -> held |> List.forall (fun (cid2, s2) -> cid1 = cid2 || not (ClaimScope.overlaps s1 s2)))

// ── Property 2: every Held claim's holder is a Present member ─────────────

let private heldClaimsHaveByPresentHolders (state: CohortState<Agent>) =
  state.Claims
  |> Map.toList
  |> List.forall (fun (_, c) ->
    match c.State with
    | ClaimState.Held holder ->
      match Map.tryFind holder state.Members with
      | Some m -> m.Presence = MemberPresence.Present
      | None -> false
    | _ -> true)

// ── Property 3: ClaimFence strictly increasing per ClaimId ────────────────

let private claimFenceOf =
  function
  | CohortEvent.ClaimAcquired(cid, _, _, f) -> Some(cid, f)
  | CohortEvent.ClaimReleased(cid, _, f) -> Some(cid, f)
  | CohortEvent.ClaimOrphaned(cid, _, f) -> Some(cid, f)
  | CohortEvent.ClaimReassigned(cid, _, f) -> Some(cid, f)
  | _ -> None

let private fencesStrictlyIncreasePerClaim (h: Harness) =
  h.Steps
  |> List.collect (fun s -> s.Events)
  |> List.choose claimFenceOf
  |> List.groupBy fst
  |> List.forall (fun (_, xs) -> xs |> List.map snd |> List.pairwise |> List.forall (fun (a, b) -> b > a))

// ── Property 5: LandingState transitions follow the declared graph ────────

let private kindOf =
  function
  | LandingState.Queued -> "Queued"
  | LandingState.Rebasing _ -> "Rebasing"
  | LandingState.Verifying _ -> "Verifying"
  | LandingState.Blocked _ -> "Blocked"
  | LandingState.Landed _ -> "Landed"
  | LandingState.Withdrawn -> "Withdrawn"

let private allowedTransition fromK toK =
  match fromK, toK with
  | "Queued", "Rebasing" -> true
  | "Queued", "Blocked" -> true // veto_landing can strike before rebasing even starts (§5.5)
  | "Rebasing", "Verifying" -> true
  | "Rebasing", "Blocked" -> true
  | "Verifying", "Verifying" -> true
  | "Verifying", "Blocked" -> true
  | "Verifying", "Landed" -> true
  | _, "Withdrawn" -> true
  | _ -> false

let private landingTransitionsFollowTheGraph (h: Harness) =
  let byLanding =
    h.Steps
    |> List.collect (fun s -> s.Events)
    |> List.choose (function
      | CohortEvent.LandingQueued(id, _) -> Some(id, "Queued")
      | CohortEvent.LandingStateChanged(id, st) -> Some(id, kindOf st)
      | _ -> None)
    |> List.groupBy fst
    |> List.map (fun (id, xs) -> id, xs |> List.map snd)
  byLanding
  |> List.forall (fun (_, kinds) -> kinds |> List.pairwise |> List.forall (fun (a, b) -> a = b || allowedTransition a b))

// ── Property 6: Landed implies every claim was Held by the requester ──────

let private landedImpliesClaimsHeldAtLandTime (h: Harness) =
  h.Steps
  |> List.forall (fun step ->
    step.Events
    |> List.forall (function
      | CohortEvent.LandingLanded(id, _) ->
        match Map.tryFind id step.Before.Landings with
        | None -> false
        | Some req ->
          req.Claims
          |> List.forall (fun (cid, fence) ->
            match Map.tryFind cid step.Before.Claims with
            | Some c -> (match c.State with ClaimState.Held h -> h = req.Requester | _ -> false) && c.Fence = fence
            | None -> false)
      | _ -> true))

// ── Property 13: overlapping-claim landings never land out of request order ─
// (Strict serial in this model makes it true of EVERY landing pair, not only
// overlapping ones — the stronger, structurally-guaranteed invariant.)

let private landedOrderRespectsRequestOrder (h: Harness) =
  let events = h.Steps |> List.collect (fun s -> s.Events)
  let requestOrder = events |> List.choose (function CohortEvent.LandingQueued(id, _) -> Some id | _ -> None)
  let landedOrder = events |> List.choose (function CohortEvent.LandingLanded(id, _) -> Some id | _ -> None)
  let posOf id = requestOrder |> List.tryFindIndex ((=) id)
  landedOrder
  |> List.map posOf
  |> List.choose id
  |> List.pairwise
  |> List.forall (fun (a, b) -> a < b)

// ── Property 14: a Departed member's claims are Orphaned in the same step ──

let private departureOrphansAllHeldClaimsImmediately (h: Harness) =
  h.Steps
  |> List.forall (fun step ->
    step.Events
    |> List.forall (function
      | CohortEvent.MemberDeparted(who, _) ->
        let heldBefore =
          step.Before.Claims
          |> Map.toList
          |> List.choose (fun (cid, c) -> match c.State with ClaimState.Held h when h = who -> Some cid | _ -> None)
          |> Set.ofList
        let orphanedThisStep =
          step.Events
          |> List.choose (function CohortEvent.ClaimOrphaned(cid, prev, _) when prev = who -> Some cid | _ -> None)
          |> Set.ofList
        heldBefore = orphanedThisStep
      | _ -> true))

[<Tests>]
let cohortPropertyTests =
  testList "Cohort properties (Phase 1 item 7, §7.3)" [

    testList "generated command sequences (1-7, 10, 12-14)" [

      testPropertyWithConfig cohortConfig "1: no two overlapping claims are Held at once, in any reachable state" <| fun (intents: Intent list) ->
        let h = run intents
        allStates h |> List.forall noOverlappingHeldClaims

      testPropertyWithConfig cohortConfig "2: every Held claim's holder is a Present member" <| fun (intents: Intent list) ->
        let h = run intents
        allStates h |> List.forall heldClaimsHaveByPresentHolders

      testPropertyWithConfig cohortConfig "3: ClaimFence strictly increases per ClaimId" <| fun (intents: Intent list) ->
        fencesStrictlyIncreasePerClaim (run intents)

      testPropertyWithConfig cohortConfig "5: LandingState transitions follow the declared graph" <| fun (intents: Intent list) ->
        landingTransitionsFollowTheGraph (run intents)

      testPropertyWithConfig cohortConfig "6: Landed implies every claim was Held by the requester at land time" <| fun (intents: Intent list) ->
        landedImpliesClaimsHeldAtLandTime (run intents)

      testPropertyWithConfig cohortConfig "7: replay of the accepted ledger equals the live fold" <| fun (intents: Intent list) ->
        let ledger = toLedger intents
        let live = ledger |> List.fold (fun st e -> match decide e.Clock e.Entropy st e.Command with Ok(s, _, _) -> s | Error _ -> st) (CohortState.empty ())
        replay ledger = live

      testPropertyWithConfig cohortConfig "13: landed order never precedes an earlier landing's request order" <| fun (intents: Intent list) ->
        landedOrderRespectsRequestOrder (run intents)

      testPropertyWithConfig cohortConfig "14: a departing member's Held claims are all Orphaned in the same step" <| fun (intents: Intent list) ->
        departureOrphansAllHeldClaimsImmediately (run intents)
    ]

    testList "targeted races (4, 11, 12, 15)" [

      testPropertyWithConfig cohortConfig "4: a stale fence is refused whatever else the command carries" <| fun (scopeIdx: int) ->
        let a = agentOf 1
        let h0 = initHarness ()
        let h1 = applyCommand h0 (CohortCommand.Join(a, JoinableRole.Implementer, None))
        let h2 = applyCommand h1 (CohortCommand.AcquireClaim(a, scopeOf scopeIdx, "purpose"))
        match h2.State.Claims |> Map.toList with
        | [ (cid, claim) ] ->
          let staleFence = claim.Fence - 1L<Measures.fence>
          match decide h2.Clock [| 99uy |] h2.State (CohortCommand.ReleaseClaim(a, cid, staleFence)) with
          | Error(CohortError.StaleClaimFence(errCid, presented, current)) ->
            errCid = cid && presented = staleFence && current = claim.Fence
          | _ -> false
        | _ -> false

      testPropertyWithConfig cohortConfig "11: a landing verified against H1 never lands when the head is H2 <> H1" <| fun (h1: string) (h2: string) ->
        let requester = agentOf 1
        let h1' = "H1-" + h1
        let h2' = "H2-" + h2
        match Purpose.tryCreate "purpose", Statement.tryCreate "statement" with
        | Ok purpose, Ok statement ->
          let claim = { Id = ClaimId "c-0"; Fence = 1L<Measures.fence>; Scope = ClaimScope.File "X.fs"; Purpose = purpose; Since = epoch; State = ClaimState.Held requester }
          let req =
            { Id = LandingId "l-0"; Requester = requester; Claims = [ (claim.Id, claim.Fence) ]; Commits = [ "c1" ]
              BaseAtQueue = h1'; Statement = statement; State = LandingState.Verifying(h1', "rebased-" + h1', 1, 0) }
          let state =
            { CohortState.empty () with
                IntegrationHead = h2'
                Members = Map.ofList [ requester, { Role = JoinableRole.Implementer; Presence = MemberPresence.Present; LastRenewal = epoch; Session = None } ]
                Claims = Map.ofList [ claim.Id, claim ]
                Landings = Map.ofList [ req.Id, req ]
                Queue = [ req.Id ] }
          match decide epoch [||] state (CohortCommand.FastForwardCompleted(req.Id, "landed-sha")) with
          | Ok(newState, events, _) when h1' = h2' ->
            (match newState.Landings.[req.Id].State with LandingState.Landed sha -> sha = "landed-sha" | _ -> false)
            && events |> List.exists (function CohortEvent.LandingLanded _ -> true | _ -> false)
          | Ok(newState, _, _) ->
            match newState.Landings.[req.Id].State with
            | LandingState.Blocked(LandingBlocker.HeadMoved(f, t), _) -> f = h1' && t = h2'
            | _ -> false
          | Error _ -> false
        | _ -> false

      testPropertyWithConfig cohortConfig "12: a departed member's stale-fence command never changes the claim it no longer holds" <| fun (scopeIdx: int) ->
        let a = agentOf 1
        let h0 = initHarness ()
        let h1 = applyCommand h0 (CohortCommand.Join(a, JoinableRole.Implementer, None))
        let h2 = applyCommand h1 (CohortCommand.AcquireClaim(a, scopeOf scopeIdx, "purpose"))
        match h2.State.Claims |> Map.toList with
        | [ (cid, claimBeforeDepart) ] ->
          let h3 = applyCommand h2 (CohortCommand.Depart a)
          // The member's late command, presenting the fence from BEFORE departure.
          match decide h3.Clock [| 7uy |] h3.State (CohortCommand.ReleaseClaim(a, cid, claimBeforeDepart.Fence)) with
          | Error _ ->
            match h3.State.Claims.[cid].State with
            | ClaimState.Orphaned(prev, _) -> prev = a
            | _ -> false
          | Ok _ -> false
        | _ -> false

      testPropertyWithConfig cohortConfig "15: a hostile display-name collision never lets one member touch another's claim" <| fun (scopeIdx: int) ->
        let a = { Id = 1; Display = "shared-name" }
        let hostile = { Id = 2; Display = "shared-name" }
        let h0 = initHarness ()
        let h1 = applyCommand h0 (CohortCommand.Join(a, JoinableRole.Implementer, None))
        let h2 = applyCommand h1 (CohortCommand.Join(hostile, JoinableRole.Implementer, None))
        let h3 = applyCommand h2 (CohortCommand.AcquireClaim(a, scopeOf scopeIdx, "purpose"))
        match h3.State.Claims |> Map.toList with
        | [ (cid, claim) ] ->
          match decide h3.Clock [| 3uy |] h3.State (CohortCommand.ReleaseClaim(hostile, cid, claim.Fence)) with
          | Error(CohortError.NotClaimHolder(errCid, requester)) ->
            errCid = cid
            && requester = hostile
            && (match h3.State.Claims.[cid].State with ClaimState.Held h -> h = a | _ -> false)
          | _ -> false
        | _ -> false
    ]

    testList "generated schedule with reconnection (16)" [

      testPropertyWithConfig cohortConfig "16: intermittent reconnection between landings never changes the set of landed commits" <| fun (n: int) ->
        let planLength = 1 + (abs n % 4)
        let a = agentOf 1

        let runPlan (flaky: bool) =
          let h0 = initHarness ()
          let h1 = applyCommand h0 (CohortCommand.Join(a, JoinableRole.Implementer, None))
          let landed = ResizeArray<string>()
          let mutable h = h1
          for i in 1 .. planLength do
            if flaky then
              h <- applyCommand h (CohortCommand.Depart a)
              h <- applyCommand h (CohortCommand.Join(a, JoinableRole.Implementer, None))
            h <- applyCommand h (CohortCommand.AcquireClaim(a, scopeOf i, "purpose"))
            let cid = h.State.Claims |> Map.toList |> List.map fst |> List.last
            let fence = h.State.Claims.[cid].Fence
            h <- applyCommand h (CohortCommand.RequestLanding(a, [ (cid, fence) ], [ sprintf "commit-%d" i ], "statement"))
            let lid = h.State.Landings |> Map.toList |> List.map fst |> List.last
            h <- applyCommand h (CohortCommand.RebaseCompleted(lid, Ok "head"))
            h <- applyCommand h (CohortCommand.AffectedComputed(lid, []))
            h <- applyCommand h (CohortCommand.TestsCompleted(lid, []))
            h <- applyCommand h (CohortCommand.FastForwardCompleted(lid, sprintf "landed-%d" i))
            match h.State.Landings.[lid].State with
            | LandingState.Landed sha -> landed.Add sha
            | _ -> ()
          h, Set.ofSeq landed

        let _, honestLanded = runPlan false
        let _, flakyLanded = runPlan true
        honestLanded = flakyLanded
    ]

    testList "authority (Phase 1 item 8, §4.2)" [

      testPropertyWithConfig cohortConfig "18: present is Anonymous for a non-member, Conductor for the bound member, Member for every other Present member" <| fun (intents: Intent list) ->
        let h = run intents
        let outsider = { Id = 999; Display = "outsider" }
        let anonymousOk = Authority.present outsider h.State = Authority.Anonymous
        let conductorOk =
          match h.State.Conductor with
          | Some c -> Authority.present c h.State = Authority.Conductor c
          | None -> true
        let membersOk =
          h.State.Members
          |> Map.toList
          |> List.forall (fun (m, r) ->
            match r.Presence with
            | MemberPresence.Present when Some m <> h.State.Conductor ->
              Authority.present m h.State = Authority.Member(m, r.Role)
            | _ -> true)
        anonymousOk && conductorOk && membersOk

      testPropertyWithConfig cohortConfig "19: ReassignClaim is refused for a non-conductor and succeeds for the conductor" <| fun (scopeIdx: int) ->
        let conductor = agentOf 0
        let other = agentOf 1
        let target = agentOf 2
        let h0 = initHarness ()
        let h1 = applyCommand h0 (CohortCommand.Join(conductor, JoinableRole.Implementer, None)) // first joiner: becomes conductor
        let h2 = applyCommand h1 (CohortCommand.Join(other, JoinableRole.Implementer, None))
        let h3 = applyCommand h2 (CohortCommand.Join(target, JoinableRole.Implementer, None))
        let h4 = applyCommand h3 (CohortCommand.AcquireClaim(other, scopeOf scopeIdx, "purpose"))
        match h4.State.Claims |> Map.toList with
        | [ (cid, _) ] ->
          let h5 = applyCommand h4 (CohortCommand.Depart other) // orphans the claim
          match h5.State.Claims.[cid].State with
          | ClaimState.Orphaned _ ->
            let refusedForNonConductor =
              match decide h5.Clock [| 1uy |] h5.State (CohortCommand.ReassignClaim(target, cid, target)) with
              | Error(CohortError.NotConductor by) -> by = target
              | _ -> false
            let succeedsForConductor =
              match decide h5.Clock [| 2uy |] h5.State (CohortCommand.ReassignClaim(conductor, cid, target)) with
              | Ok(newState, events, _) ->
                (match newState.Claims.[cid].State with ClaimState.Held holder -> holder = target | _ -> false)
                && events |> List.exists (function CohortEvent.ClaimReassigned(c, _, _) -> c = cid | _ -> false)
              | _ -> false
            refusedForNonConductor && succeedsForConductor
          | _ -> false
        | _ -> false

      test "20: DelegateConductor is refused for a non-conductor and to a non-member, and moves the binding when the conductor delegates to a Present member" {
        let conductor = agentOf 0
        let other = agentOf 1
        let stranger = agentOf 2 // never joins
        let h0 = initHarness ()
        let h1 = applyCommand h0 (CohortCommand.Join(conductor, JoinableRole.Implementer, None))
        let h2 = applyCommand h1 (CohortCommand.Join(other, JoinableRole.Implementer, None))
        let refusedByNonConductor =
          match decide h2.Clock [| 1uy |] h2.State (CohortCommand.DelegateConductor(other, other)) with
          | Error(CohortError.NotConductor by) -> by = other
          | _ -> false
        let refusedToNonMember =
          match decide h2.Clock [| 2uy |] h2.State (CohortCommand.DelegateConductor(conductor, stranger)) with
          | Error(CohortError.MemberNotPresent m) -> m = stranger
          | _ -> false
        let delegationOk =
          match decide h2.Clock [| 3uy |] h2.State (CohortCommand.DelegateConductor(conductor, other)) with
          | Ok(newState, events, _) ->
            let bindingMoved = newState.Conductor = Some other
            let oldIsMember = Authority.present conductor newState = Authority.Member(conductor, JoinableRole.Implementer)
            let newIsConductor = Authority.present other newState = Authority.Conductor other
            let eventOk = events |> List.exists (function CohortEvent.ConductorDelegated(f, t) -> f = conductor && t = other | _ -> false)
            bindingMoved && oldIsMember && newIsConductor && eventOk
          | _ -> false
        (refusedByNonConductor && refusedToNonMember && delegationOk)
        |> Expect.isTrue "non-conductor and non-member delegation refused; conductor delegation to a Present member moves the binding"
      }

      // Property 4 (§7.3's authority-unforgeability): the generated Intent type has
      // no DelegateConductor case, so no generated sequence ever issues one — this
      // proves the Conductor binding is unforgeable by ordinary membership commands:
      // it is set exactly once (ConductorBound, on the first join) and never again.
      testPropertyWithConfig cohortConfig "21: without DelegateConductor, ConductorBound fires at most once and Conductor always equals that first joiner" <| fun (intents: Intent list) ->
        let h = run intents
        let conductorBoundEvents =
          h.Steps
          |> List.collect (fun s -> s.Events)
          |> List.choose (function CohortEvent.ConductorBound w -> Some w | _ -> None)
        match conductorBoundEvents with
        | [] -> h.State.Conductor = None
        | [ only ] -> h.State.Conductor = Some only
        | _ -> false
    ]

    testList "read model (10)" [

      testPropertyWithConfig cohortConfig "10: project is a function of (ledger head, session generations) alone" <| fun (intents: Intent list) (gens: int64 list) ->
        let ledger = toLedger intents
        let head = replayHead ledger
        let snapshotsOf () : SessionSnapshot<Agent>[] =
          gens
          |> List.truncate 3
          |> List.mapi (fun i g ->
            { Member = Some(agentOf i); SessionId = sprintf "s%d" i; Generation = g
              PassingTests = [ TestId "t1" ]; FailingTests = []; StaleTests = [] })
          |> List.toArray
        let frame1 = project head (snapshotsOf ())
        let frame2 = project head (snapshotsOf ())
        frame1 = frame2 && frame1.Version = head.Seq
    ]

    testList "corpus replay (17)" [
      test "every recorded cohort ledger in SageFs.Tests/cohorts replays to its recorded events" {
        let corpusDir = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "cohorts")
        System.IO.Directory.CreateDirectory corpusDir |> ignore
        let files = System.IO.Directory.GetFiles(corpusDir, "*.ledger.jsonl")
        // §7.3 #17: "initially over an empty directory." No recorded cohort has
        // been exported yet (that export mechanism is a later phase item), so
        // this is vacuously true today and starts failing the moment a real
        // ledger drifts from what current `decide` replays to.
        files
        |> Array.length
        |> Expect.equal "no recorded ledgers yet — this is the empty-corpus baseline, not a stub" 0
      }
    ]

    // Properties 8 and 9, un-skipped for Phase 1 item 11 / cohort-integration-
    // plan.md Slice 3. `Affordances.cohortTools` is a SCOPED v1 of the
    // vision's full `SessionState * Authority * CohortPhase -> Set<ToolName>`
    // (§8.1): keyed on `Authority` alone (no `CohortPhase` — v1's implicit
    // cohort has no phase lifecycle to match on — see Affordances.fs's
    // module doc for the full rationale), and scoped to the 7 COHORT tools
    // only rather than the whole MCP tool surface. Both properties below are
    // adapted to that scope, not the vision's literal phrasing.
    testList "cohort affordances (8, 9 — Phase 1 item 11, §8.1, Slice 3 v1 scope)" [

      test "8: cohortTools Anonymous is the solo/no-cohort status-only surface" {
        // Adaptation of the vision's solo-user invariant (`cohortTools state
        // Authority.Anonymous phase = Affordances.availableTools state`):
        // Slice 3's `cohortTools` only covers cohort tools, so its
        // Anonymous-authority analogue is "a caller with no cohort
        // membership sees only the read-only status tool from `cohortTools`
        // itself." `join_cohort` is deliberately NOT folded into the
        // Anonymous case (see `cohortTools`'/`alwaysReachableCohortTools`'
        // doc) — it is reachable via the separate always-reachable set
        // instead, so a fresh caller can still always join. Both halves of
        // that invariant are asserted here.
        Affordances.cohortTools Authority.Anonymous
        |> Expect.equal "Anonymous sees only get_cohort_status from cohortTools itself" (set [ Affordances.CohortTool.GetStatus ])
        Affordances.checkCohortToolAllowed Authority.Anonymous Affordances.CohortTool.GetStatus
        |> Expect.isTrue "get_cohort_status must stay reachable to an unjoined caller"
        Affordances.checkCohortToolAllowed Authority.Anonymous Affordances.CohortTool.Join
        |> Expect.isTrue "join_cohort must stay reachable to an unjoined caller, or nobody could ever join a cohort"
        Affordances.checkCohortToolAllowed Authority.Anonymous Affordances.CohortTool.AcquireClaim
        |> Expect.isFalse "an unjoined caller may not acquire a claim"
        Affordances.checkCohortToolAllowed Authority.Anonymous Affordances.CohortTool.ReassignClaim
        |> Expect.isFalse "an unjoined caller may not reassign a claim"
      }

      testPropertyWithConfig cohortConfig "9: cohortTools is total over Authority<Agent> and never empty" <| fun (agentIdx: int) (variant: int) ->
        let agent = agentOf agentIdx
        let authority =
          match abs variant % 5 with
          | 0 -> Authority.Anonymous
          | 1 -> Authority.Member(agent, JoinableRole.Implementer)
          | 2 -> Authority.Member(agent, JoinableRole.Verifier)
          | 3 -> Authority.Member(agent, JoinableRole.Observer)
          | _ -> Authority.Conductor agent
        // Totality over `Authority<'m>` is itself compiler-checked — the
        // `match` in `cohortTools` is exhaustive with no wildcard arm, so an
        // unhandled case is a build error, not a runtime gap (no
        // registration-integrity test needed, unlike the string-keyed
        // `gatingDomain` table). What this property adds is the runtime
        // guarantee that totality: every reachable `Authority` value yields
        // a genuinely usable (non-empty) tool set — an authority silently
        // resolving to the empty set would be a real lockout bug the
        // compiler's exhaustiveness check alone cannot catch.
        not (Set.isEmpty (Affordances.cohortTools authority))
        && Set.contains Affordances.CohortTool.GetStatus (Affordances.cohortTools authority)
    ]
  ]
