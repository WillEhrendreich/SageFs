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
open SageFs.MemberTable
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

// The checked-in ledger corpus lives under SageFs.Tests/cohorts. Located at
// RUNTIME (RepoPaths walks up from this assembly's own location), never through
// a build-time constant that would measure where the assembly was compiled.
let private repoRoot = RepoPaths.repoPathFull [||]

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
  | IDelegate of by: int * toAgent: int
  | IResolveVeto of by: int * landingIdx: int

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

/// The ordinary intents plus the four commands tools issue now. Kept out of `genIntent` on purpose: property 21
/// pins that WITHOUT a delegation the seat never moves, and mixing delegations into that history would end it.
let genDelegatingIntent =
  Gen.frequency [
    4, genIntent
    4, Gen.map2 (fun b t -> IDelegate(b, t)) (Gen.choose (1, 4)) (Gen.choose (1, 5))
    4, Gen.map2 (fun a c -> IRequestLanding(a, c)) (Gen.choose (1, 4)) (Gen.choose (0, 1000))
    3, Gen.map2 (fun b l -> IVeto(b, l)) (Gen.choose (1, 4)) (Gen.choose (0, 10))
    3, Gen.map2 (fun b l -> IResolveVeto(b, l)) (Gen.choose (1, 4)) (Gen.choose (0, 10))
    2, Gen.map2 (fun a l -> IWithdraw(a, l)) (Gen.choose (1, 4)) (Gen.choose (0, 10))
  ]

/// Four agents seated first (1 joins first, so it is the conductor; 2 and 1 implement, 3 verifies, 4 observes), so a
/// generated history spends its steps on the commands under test and not on getting anyone into the cohort.
let seating = [ IJoin(1, JoinableRole.Implementer); IJoin(2, JoinableRole.Implementer); IJoin(3, JoinableRole.Verifier); IJoin(4, JoinableRole.Observer) ]

/// A history that includes delegations, vetoes, clears and withdrawals.
type DelegatingHistory = DelegatingHistory of Intent list

type DelegatingGenerators =
  static member History() =
    Arb.fromGen (Gen.choose (10, 60) |> Gen.bind (fun n -> Gen.listOfLength n genDelegatingIntent) |> Gen.map (fun intents -> DelegatingHistory(seating @ intents)))

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

/// Every cohort state in this file is opened by `CohortState.empty`, which is
/// Machine-scoped — the v1 shape these properties were written against. These
/// tests drive the pure core with no session and no working directory, so they
/// talk about exactly one machine-wide cohort: a `Repository` or `Named` scope
/// would turn each of them into a test of scoping instead of a test of the
/// behaviour it was written for. Note the contrast with `scopeOf` above, which
/// is a CLAIM scope and pools several on purpose — two axes, not one.
let private machine = CohortScope.Machine

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
  | IJoin(a, role) -> applyCommand h (CohortCommand.Join(agentOf a, role, None, machine))
  | IDepart a -> applyCommand h (CohortCommand.Depart(agentOf a, machine))
  | IRenewLease a -> applyCommand h (CohortCommand.RenewLease(agentOf a, machine))
  | ITick minutes -> applyCommand { h with Clock = h.Clock.AddMinutes(float (abs minutes % 90)) } (CohortCommand.Tick machine)
  | IAcquire(a, s) -> applyCommand h (CohortCommand.AcquireClaim(agentOf a, scopeOf s, "purpose", machine))
  | IReleaseOwn(a, c) ->
    match resolveClaim h c with
    | None -> h
    | Some cid ->
      match Map.tryFind cid h.State.Claims with
      | None -> h
      | Some claim -> applyCommand h (CohortCommand.ReleaseClaim(agentOf a, cid, claim.Fence, machine))
  | IReassign(by, c, toA) ->
    match resolveClaim h c with
    | None -> h
    | Some cid -> applyCommand h (CohortCommand.ReassignClaim(agentOf by, cid, agentOf toA, machine))
  | IObserveSave(a, p) -> applyCommand h (CohortCommand.ObserveSave(agentOf a, pathOf p, machine))
  | IRequestLanding(a, commit) ->
    let agent = agentOf a
    let backing =
      h.State.Claims
      |> Map.toList
      |> List.choose (fun (cid, c) ->
        match c.State with
        | ClaimState.Held holder when holder = agent -> Some(cid, c.Fence)
        | _ -> None)
    applyCommand h (CohortCommand.RequestLanding(agent, backing, [ sprintf "commit-%d" commit ], "landing statement", machine))
  | IRebaseOk l ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.RebaseCompleted(lid, Ok(sprintf "sha-%d" h.NextEntropy), machine))
  | IRebaseConflict(l, f) ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.RebaseCompleted(lid, Error [ sprintf "conflict-%d.fs" (abs f) ], machine))
  | IAffected(l, t) ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.AffectedComputed(lid, testIdsOf t "t", machine))
  | ITestsPass l ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.TestsCompleted(lid, [], machine))
  | ITestsFail(l, f) ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.TestsCompleted(lid, testIdsOf (1 + abs f) "f", machine))
  | IFastForward l ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.FastForwardCompleted(lid, sprintf "landed-%d" h.NextEntropy, machine))
  | IWithdraw(a, l) ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.WithdrawLanding(agentOf a, lid, machine))
  | IVeto(by, l) ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.VetoLanding(agentOf by, lid, "veto reason", machine))
  // `toAgent` 5 is an agent that never joins (`agentOf` maps 1..4), so absent targets are generated too.
  | IDelegate(by, toA) -> applyCommand h (CohortCommand.DelegateConductor(agentOf by, (if toA = 5 then { Id = 5; Display = "agent-5" } else agentOf toA), machine))
  | IResolveVeto(by, l) ->
    match resolveLanding h l with
    | None -> h
    | Some lid -> applyCommand h (CohortCommand.ResolveVeto(agentOf by, lid, machine))

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
      | IJoin(a, role) -> Some(CohortCommand.Join(agentOf a, role, None, machine))
      | IDepart a -> Some(CohortCommand.Depart(agentOf a, machine))
      | IRenewLease a -> Some(CohortCommand.RenewLease(agentOf a, machine))
      | ITick minutes ->
        clock <- clock.AddMinutes(float (abs minutes % 90))
        Some(CohortCommand.Tick machine)
      | IAcquire(a, s) -> Some(CohortCommand.AcquireClaim(agentOf a, scopeOf s, "purpose", machine))
      | IReleaseOwn(a, c) ->
        if state.Claims.IsEmpty then None
        else
          let cid = state.Claims |> Map.toList |> List.map fst |> fun ids -> ids.[abs c % ids.Length]
          Some(CohortCommand.ReleaseClaim(agentOf a, cid, state.Claims.[cid].Fence, machine))
      | IReassign(by, c, toA) ->
        if state.Claims.IsEmpty then None
        else
          let cid = state.Claims |> Map.toList |> List.map fst |> fun ids -> ids.[abs c % ids.Length]
          Some(CohortCommand.ReassignClaim(agentOf by, cid, agentOf toA, machine))
      | IObserveSave(a, p) -> Some(CohortCommand.ObserveSave(agentOf a, pathOf p, machine))
      | IRequestLanding(a, commit) ->
        let agent = agentOf a
        let backing =
          state.Claims
          |> Map.toList
          |> List.choose (fun (cid, c) ->
            match c.State with
            | ClaimState.Held holder when holder = agent -> Some(cid, c.Fence)
            | _ -> None)
        Some(CohortCommand.RequestLanding(agent, backing, [ sprintf "commit-%d" commit ], "landing statement", machine))
      | IRebaseOk l ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.RebaseCompleted(lid, Ok(sprintf "sha-%d" nextEntropy), machine))
      | IRebaseConflict(l, f) ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.RebaseCompleted(lid, Error [ sprintf "conflict-%d.fs" (abs f) ], machine))
      | IAffected(l, t) ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.AffectedComputed(lid, testIdsOf t "t", machine))
      | ITestsPass l ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.TestsCompleted(lid, [], machine))
      | ITestsFail(l, f) ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.TestsCompleted(lid, testIdsOf (1 + abs f) "f", machine))
      | IFastForward l ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.FastForwardCompleted(lid, sprintf "landed-%d" nextEntropy, machine))
      | IWithdraw(a, l) ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.WithdrawLanding(agentOf a, lid, machine))
      | IVeto(by, l) ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.VetoLanding(agentOf by, lid, "veto reason", machine))
      | IDelegate(by, toA) -> Some(CohortCommand.DelegateConductor(agentOf by, (if toA = 5 then { Id = 5; Display = "agent-5" } else agentOf toA), machine))
      | IResolveVeto(by, l) ->
        if state.Landings.IsEmpty then None
        else
          let lid = state.Landings |> Map.toList |> List.map fst |> fun ids -> ids.[abs l % ids.Length]
          Some(CohortCommand.ResolveVeto(agentOf by, lid, machine))
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
        let h1 = applyCommand h0 (CohortCommand.Join(a, JoinableRole.Implementer, None, machine))
        let h2 = applyCommand h1 (CohortCommand.AcquireClaim(a, scopeOf scopeIdx, "purpose", machine))
        match h2.State.Claims |> Map.toList with
        | [ (cid, claim) ] ->
          let staleFence = claim.Fence - 1L<Measures.fence>
          match decide h2.Clock [| 99uy |] h2.State (CohortCommand.ReleaseClaim(a, cid, staleFence, machine)) with
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
              BaseAtQueue = h1'; Statement = statement; State = LandingState.Verifying(h1', "rebased-" + h1', 1, 0)
              FastForwardAttempts = 0; Settlement = LandingSettlement.Unsettled }
          let state =
            { CohortState.empty () with
                IntegrationHead = h2'
                Members = Map.ofList [ requester, { Role = JoinableRole.Implementer; Presence = MemberPresence.Present; LastRenewal = epoch; Session = None } ]
                Claims = Map.ofList [ claim.Id, claim ]
                Landings = Map.ofList [ req.Id, req ]
                Queue = [ req.Id ] }
          match decide epoch [||] state (CohortCommand.FastForwardCompleted(req.Id, "landed-sha", machine)) with
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
        let h1 = applyCommand h0 (CohortCommand.Join(a, JoinableRole.Implementer, None, machine))
        let h2 = applyCommand h1 (CohortCommand.AcquireClaim(a, scopeOf scopeIdx, "purpose", machine))
        match h2.State.Claims |> Map.toList with
        | [ (cid, claimBeforeDepart) ] ->
          let h3 = applyCommand h2 (CohortCommand.Depart(a, machine))
          // The member's late command, presenting the fence from BEFORE departure.
          match decide h3.Clock [| 7uy |] h3.State (CohortCommand.ReleaseClaim(a, cid, claimBeforeDepart.Fence, machine)) with
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
        let h1 = applyCommand h0 (CohortCommand.Join(a, JoinableRole.Implementer, None, machine))
        let h2 = applyCommand h1 (CohortCommand.Join(hostile, JoinableRole.Implementer, None, machine))
        let h3 = applyCommand h2 (CohortCommand.AcquireClaim(a, scopeOf scopeIdx, "purpose", machine))
        match h3.State.Claims |> Map.toList with
        | [ (cid, claim) ] ->
          match decide h3.Clock [| 3uy |] h3.State (CohortCommand.ReleaseClaim(hostile, cid, claim.Fence, machine)) with
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
          let h1 = applyCommand h0 (CohortCommand.Join(a, JoinableRole.Implementer, None, machine))
          let landed = ResizeArray<string>()
          let mutable h = h1
          for i in 1 .. planLength do
            if flaky then
              h <- applyCommand h (CohortCommand.Depart(a, machine))
              h <- applyCommand h (CohortCommand.Join(a, JoinableRole.Implementer, None, machine))
            h <- applyCommand h (CohortCommand.AcquireClaim(a, scopeOf i, "purpose", machine))
            let cid = h.State.Claims |> Map.toList |> List.map fst |> List.last
            let fence = h.State.Claims.[cid].Fence
            h <- applyCommand h (CohortCommand.RequestLanding(a, [ (cid, fence) ], [ sprintf "commit-%d" i ], "statement", machine))
            let lid = h.State.Landings |> Map.toList |> List.map fst |> List.last
            h <- applyCommand h (CohortCommand.RebaseCompleted(lid, Ok "head", machine))
            h <- applyCommand h (CohortCommand.AffectedComputed(lid, [], machine))
            h <- applyCommand h (CohortCommand.TestsCompleted(lid, [], machine))
            h <- applyCommand h (CohortCommand.FastForwardCompleted(lid, sprintf "landed-%d" i, machine))
            match h.State.Landings.[lid].State with
            | LandingState.Landed sha -> landed.Add sha
            | _ -> ()
          h, Set.ofSeq landed

        let _, honestLanded = runPlan false
        let _, flakyLanded = runPlan true
        honestLanded = flakyLanded
    ]

    testList "authority (Phase 1 item 8, §4.2)" [

      testPropertyWithConfig cohortConfig "18: present is Anonymous for a non-member, Conductor only for a Present seat BOUND to the caller, Member for every other Present member" <| fun (intents: Intent list) ->
        // CHANGED, deliberately. This used to read `Authority.present c h.State =
        // Authority.Conductor c` for whatever `Conductor` held — a statement
        // that the conductor binding confers authority unconditionally, which
        // is the fail-open itself: it asserted that a DEPARTED conductor is
        // still Conductor, because `present` checked the binding before
        // presence. The membership loop below had the same shape
        // (`Some m <> h.State.Conductor` skipped the conductor from the
        // Present-member case).
        //
        // The invariant that replaces it is the one the typed `ConductorBinding`
        // exists to make writable-once: Conductor iff the seat is Bound to a
        // member who is still Present. Anything else is an ordinary Member or
        // Anonymous — so a departed conductor who rejoins is a Member, and a
        // departed conductor is Anonymous, and never a lingering Conductor.
        let h = run intents
        let outsider = { Id = 999; Display = "outsider" }
        let anonymousOk = Authority.present outsider h.State = Authority.Anonymous
        let conductorOk =
          match h.State.Conductor with
          | ConductorBinding.Bound c ->
            let isPresent =
              match Map.tryFind c h.State.Members with
              | Some { Presence = MemberPresence.Present } -> true
              | _ -> false
            isPresent && Authority.present c h.State = Authority.Conductor c
          // default policy: nobody may present as Conductor over an empty seat
          | ConductorBinding.NeverBound
          | ConductorBinding.Vacant _ ->
            h.State.Members
            |> Map.toList
            |> List.forall (fun (m, _) -> Authority.present m h.State <> Authority.Conductor m)
        let membersOk =
          h.State.Members
          |> Map.toList
          |> List.forall (fun (m, r) ->
            match r.Presence with
            | MemberPresence.Present when h.State.Conductor <> ConductorBinding.Bound m ->
              Authority.present m h.State = Authority.Member(m, r.Role)
            | _ -> true)
        // and the invariant the DU promises, checked directly on the state
        let bindingInvariantOk =
          match h.State.Conductor with
          | ConductorBinding.Bound c ->
            match Map.tryFind c h.State.Members with
            | Some { Presence = MemberPresence.Present } -> true
            | _ -> false
          | ConductorBinding.NeverBound
          | ConductorBinding.Vacant _ -> true
        anonymousOk && conductorOk && membersOk && bindingInvariantOk

      testPropertyWithConfig cohortConfig "19: ReassignClaim is refused for a non-conductor and succeeds for the conductor" <| fun (scopeIdx: int) ->
        let conductor = agentOf 0
        let other = agentOf 1
        let target = agentOf 2
        let h0 = initHarness ()
        let h1 = applyCommand h0 (CohortCommand.Join(conductor, JoinableRole.Implementer, None, machine)) // first joiner: becomes conductor
        let h2 = applyCommand h1 (CohortCommand.Join(other, JoinableRole.Implementer, None, machine))
        let h3 = applyCommand h2 (CohortCommand.Join(target, JoinableRole.Implementer, None, machine))
        let h4 = applyCommand h3 (CohortCommand.AcquireClaim(other, scopeOf scopeIdx, "purpose", machine))
        match h4.State.Claims |> Map.toList with
        | [ (cid, _) ] ->
          let h5 = applyCommand h4 (CohortCommand.Depart(other, machine)) // orphans the claim
          match h5.State.Claims.[cid].State with
          | ClaimState.Orphaned _ ->
            let refusedForNonConductor =
              match decide h5.Clock [| 1uy |] h5.State (CohortCommand.ReassignClaim(target, cid, target, machine)) with
              | Error(CohortError.NotConductor by) -> by = target
              | _ -> false
            let succeedsForConductor =
              match decide h5.Clock [| 2uy |] h5.State (CohortCommand.ReassignClaim(conductor, cid, target, machine)) with
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
        let h1 = applyCommand h0 (CohortCommand.Join(conductor, JoinableRole.Implementer, None, machine))
        let h2 = applyCommand h1 (CohortCommand.Join(other, JoinableRole.Implementer, None, machine))
        let refusedByNonConductor =
          match decide h2.Clock [| 1uy |] h2.State (CohortCommand.DelegateConductor(other, other, machine)) with
          | Error(CohortError.NotConductor by) -> by = other
          | _ -> false
        let refusedToNonMember =
          match decide h2.Clock [| 2uy |] h2.State (CohortCommand.DelegateConductor(conductor, stranger, machine)) with
          // The refusal names the target and carries the roster of present members, so the caller can pick one.
          | Error(CohortError.DelegateTargetAbsent(m, roster)) -> m = stranger && List.sort roster = List.sort [ conductor; other ]
          | _ -> false
        let delegationOk =
          match decide h2.Clock [| 3uy |] h2.State (CohortCommand.DelegateConductor(conductor, other, machine)) with
          | Ok(newState, events, _) ->
            let bindingMoved = newState.Conductor = ConductorBinding.Bound other
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
      testPropertyWithConfig cohortConfig "21: without DelegateConductor, ConductorBound fires at most once and the seat is NEVER auto-refilled — it goes Vacant, not back to unbound" <| fun (intents: Intent list) ->
        // CHANGED, deliberately. The original was "Conductor always equals that
        // first joiner", i.e. `Conductor = Some only`. Two problems with it as
        // an invariant: it was false the moment the conductor departed or its
        // lease lapsed (the binding kept naming it, which is the defect), and
        // as written it FORBADE the seat ever emptying — so keeping it green
        // meant keeping the departed conductor bound forever.
        //
        // What is actually invariant is what "unforgeable" means once vacancy
        // is a state: `ConductorBound` still fires AT MOST ONCE (nobody but a
        // joiner can make it, and only on a seat that was never held), and
        // after that first joiner the seat can only ever be whatever the
        // conductor binding says — `Bound` to that same first joiner, or
        // `Vacant` naming them. It must NEVER silently revert to `NeverBound`
        // or quietly become someone else, and it must never be Bound to a
        // member who has left.
        let h = run intents
        let conductorBoundEvents =
          h.Steps
          |> List.collect (fun s -> s.Events)
          |> List.choose (function CohortEvent.ConductorBound w -> Some w | _ -> None)
        match conductorBoundEvents with
        | [] ->
          // nothing ever bound the seat
          h.State.Conductor = ConductorBinding.NeverBound
        | [ only ] ->
          match h.State.Conductor with
          | ConductorBinding.Bound c -> c = only
          | ConductorBinding.Vacant(former, _, _) -> former = only
          // default policy: the seat lost both its holder AND its record of
          // one, which would mean something unbound it without ever saying so
          | ConductorBinding.NeverBound -> false
        | _ -> false
    ]

    testList "delegation, veto and withdrawal — the commands tools issue (22-27)" [

      // Seeded: a failing run reproduces from the seed the runner prints, and CI reruns the same histories.
      // Histories mix every ordinary intent with delegations (including to an agent that never joined),
      // vetoes, clears and withdrawals.
      let delegatingConfig = { cohortConfig with arbitrary = [ typeof<DelegatingGenerators> ]; replay = Some(0x5EEDUL, 0xC0407UL, None) }

      let isPresentIn (state: CohortState<Agent>) (who: Agent) =
        match Map.tryFind who state.Members with
        | Some { Presence = MemberPresence.Present } -> true
        | _ -> false

      let presentRoster (state: CohortState<Agent>) : Agent list =
        state.Members
        |> Map.toList
        |> List.choose (fun (m, r) -> match r.Presence with MemberPresence.Present -> Some m | MemberPresence.Departed _ -> None)

      let conductorsIn (state: CohortState<Agent>) : Agent list =
        state.Members |> Map.toList |> List.map fst |> List.filter (fun m -> Authority.present m state = Authority.Conductor m)

      testCase "28: the generated histories actually reach each command (accepted, not only attempted), so 22-27 are not vacuous" <| fun _ ->
        let histories =
          Gen.sample 300 (DelegatingGenerators.History() |> Arb.toGen)
          |> Array.map (fun (DelegatingHistory intents) -> run intents)
        let accepted (matches: CohortCommand<Agent> -> bool) =
          histories |> Array.sumBy (fun h -> h.Steps |> List.filter (fun s -> matches s.Command) |> List.length)
        let delegated = accepted (function CohortCommand.DelegateConductor _ -> true | _ -> false)
        let vetoed = accepted (function CohortCommand.VetoLanding _ -> true | _ -> false)
        let cleared = accepted (function CohortCommand.ResolveVeto _ -> true | _ -> false)
        let withdrawn = accepted (function CohortCommand.WithdrawLanding _ -> true | _ -> false)
        Expect.isGreaterThan "delegations are accepted in generated histories" (delegated, 10)
        Expect.isGreaterThan "vetoes are accepted" (vetoed, 10)
        Expect.isGreaterThan "vetoes are cleared" (cleared, 0)
        Expect.isGreaterThan "withdrawals are accepted" (withdrawn, 10)

      testPropertyWithConfig delegatingConfig "22: a delegation leaves exactly one conductor, the named present member, and the old holder is an ordinary member" <| fun (DelegatingHistory intents) ->
        let h = run intents
        h.Steps
        |> List.forall (fun step ->
          step.Events
          |> List.forall (function
            | CohortEvent.ConductorDelegated(from, target) ->
              Authority.present from step.Before = Authority.Conductor from
              && Authority.present target step.After = Authority.Conductor target
              && Authority.present from step.After <> Authority.Conductor from
              && List.length (conductorsIn step.After) = 1
            | _ -> true))

      testPropertyWithConfig delegatingConfig "23: the seat moves from one bound member to another by a delegation by the holder, and by nothing else" <| fun (DelegatingHistory intents) ->
        let h = run intents
        h.Steps
        |> List.forall (fun step ->
          match step.Before.Conductor, step.After.Conductor with
          | ConductorBinding.Bound before, ConductorBinding.Bound now when before <> now ->
            (match step.Command with
             | CohortCommand.DelegateConductor(by, target, _) -> by = before && target = now
             | _ -> false)
          | _ -> true)

      testPropertyWithConfig delegatingConfig "24: an accepted veto came from a seated non-observer, with a reason, on a live landing; and the first veto stands" <| fun (DelegatingHistory intents) ->
        let h = run intents
        h.Steps
        |> List.forall (fun step ->
          match step.Command with
          | CohortCommand.VetoLanding(by, id, reason, _) ->
            let standing =
              match Authority.present by step.Before with
              | Authority.Conductor _
              | Authority.Member(_, JoinableRole.Implementer)
              | Authority.Member(_, JoinableRole.Verifier) -> true
              | Authority.Member(_, JoinableRole.Observer)
              | Authority.Anonymous -> false
            let live =
              match Map.tryFind id step.Before.Landings with
              | Some { State = LandingState.Queued }
              | Some { State = LandingState.Rebasing _ }
              | Some { State = LandingState.Verifying _ } -> true
              | _ -> false
            let recorded =
              match step.After.Landings.[id].State with
              | LandingState.Blocked(LandingBlocker.VetoedBy(vetoer, text), NextAction.AwaitConductor) -> vetoer = by && text = reason.Trim()
              | _ -> false
            standing && reason.Trim() <> "" && live && recorded && not (List.contains id step.After.Queue)
          | _ -> true)

      testPropertyWithConfig delegatingConfig "25: after any history, a veto, a clear or a withdrawal of a landing that landed or was withdrawn is REFUSED for everyone, never accepted as a no-op" <| fun (DelegatingHistory intents) ->
        let h = run intents
        let settled =
          h.State.Landings
          |> Map.toList
          |> List.filter (fun (_, l) -> match l.State with LandingState.Landed _ | LandingState.Withdrawn -> true | _ -> false)
          |> List.map fst
        [ for lid in settled do
            for who in [ agentOf 1; agentOf 2; agentOf 3; agentOf 4 ] do
              yield CohortCommand.VetoLanding(who, lid, "late", machine)
              yield CohortCommand.ResolveVeto(who, lid, machine)
              yield CohortCommand.WithdrawLanding(who, lid, machine) ]
        |> List.forall (fun cmd ->
          match decide h.Clock [| 9uy |] h.State cmd with
          | Error _ -> true
          | Ok _ -> false)

      testPropertyWithConfig delegatingConfig "26: an accepted withdrawal is by the landing's own requester, and an accepted clear is by the conductor of a vetoed landing" <| fun (DelegatingHistory intents) ->
        let h = run intents
        h.Steps
        |> List.forall (fun step ->
          match step.Command with
          | CohortCommand.WithdrawLanding(who, id, _) -> step.Before.Landings.[id].Requester = who
          | CohortCommand.ResolveVeto(by, id, _) ->
            Authority.present by step.Before = Authority.Conductor by
            && (match step.Before.Landings.[id].State with LandingState.Blocked(LandingBlocker.VetoedBy _, _) -> true | _ -> false)
          | _ -> true)

      testPropertyWithConfig delegatingConfig "27: a delegation to someone who is not present is refused with the roster of those who are; a delegation by anyone but the sitting conductor is refused" <| fun (DelegatingHistory intents) ->
        let h = run intents
        let roster = presentRoster h.State |> List.sort
        let everyone = [ for i in 1 .. 5 -> if i = 5 then { Id = 5; Display = "agent-5" } else agentOf i ]
        match h.State.Conductor with
        | ConductorBinding.Bound conductor ->
          everyone
          |> List.forall (fun target ->
            match decide h.Clock [| 9uy |] h.State (CohortCommand.DelegateConductor(conductor, target, machine)) with
            | Ok _ -> isPresentIn h.State target && target <> conductor
            | Error(CohortError.DelegateToSelf who) -> who = conductor && target = conductor
            | Error(CohortError.DelegateTargetAbsent(refused, listed)) -> refused = target && not (isPresentIn h.State target) && List.sort listed = roster
            | Error _ -> false)
          && (everyone
              |> List.filter (fun by -> by <> conductor)
              |> List.forall (fun by ->
                match decide h.Clock [| 9uy |] h.State (CohortCommand.DelegateConductor(by, conductor, machine)) with
                | Error(CohortError.NotConductor who) -> who = by
                | _ -> false))
        | ConductorBinding.NeverBound
        | ConductorBinding.Vacant _ ->
          everyone
          |> List.forall (fun by ->
            match decide h.Clock [| 9uy |] h.State (CohortCommand.DelegateConductor(by, by, machine)) with
            | Error(CohortError.ConductorVacant _) -> true
            | _ -> false)
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
        // §7.3 #17: "initially over an empty directory." Phase 1 item 18a
        // (sagefs-multiagent-vision.md §5.2) landed the export mechanism this
        // comment used to defer — `SageFs.Features.CohortLedgerExport` — plus
        // the repo's first checked-in ledger, `cohorts/basic.ledger.jsonl`.
        // This is no longer the vacuous empty-corpus baseline: every
        // `*.ledger.jsonl` file found here is parsed and REPLAYED command by
        // command, and each step's freshly-produced events must equal the
        // events that were recorded alongside that command — proving a
        // checked-in ledger stays replayable across code changes, not merely
        // that its final state happens to match.
        let corpusDir = System.IO.Path.Combine(repoRoot, "SageFs.Tests", "cohorts")
        System.IO.Directory.CreateDirectory corpusDir |> ignore
        let files = System.IO.Directory.GetFiles(corpusDir, "*.ledger.jsonl")

        files.Length > 0
        |> Expect.isTrue "at least one recorded ledger corpus file is checked in (item 18a's fixture)"

        files
        |> Array.iter (fun file ->
          let jsonl = System.IO.File.ReadAllText file
          match SageFs.Features.CohortLedgerExport.fromJsonl jsonl with
          | Error e -> failtestf "%s failed to parse: %s" file e
          | Ok(entries: LedgerEntry<MemberId> list) ->
            entries
            |> List.fold
              (fun state (entry: LedgerEntry<MemberId>) ->
                match decide entry.Clock entry.Entropy state entry.Command with
                | Ok(newState, events, _effects) ->
                  events
                  |> Expect.equal
                    (sprintf "%s seq %d replays to its recorded events" file (int64 entry.Seq))
                    entry.Events
                  newState
                | Error e ->
                  failtestf "%s seq %d: recorded command no longer applies cleanly: %A" file (int64 entry.Seq) e)
              (CohortState.empty ())
            |> ignore)
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
