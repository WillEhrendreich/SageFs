/// Phase 1 item 7 (sagefs-multiagent-vision.md §5.2, §7.1, §7.3, §10): the pure
/// cohort core's model-based properties (numbered 1 to 28 in the test names; 17 is
/// the corpus replay and carries no number), run over generated command
/// sequences, generated strategies on generated schedules, and a replayed
/// regression corpus.
///
/// Properties 8 and 9 run: `Affordances.cohortTools` landed (Phase 1 item 11), so
/// they are the "cohort affordances" list at the bottom of this file, adapted to
/// the cohort-tool scope that v1 gates (see the comment above that list).
module SageFs.Tests.CohortPropertyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open System.Threading.Tasks
open SageFs.Features
open SageFs.Features.CohortLedger
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

/// How an `IAdvance` pushes the landing at the FRONT of the queue. An index into "every landing ever minted" lands the
/// right one at the right stage about once in a hundred histories (measured: 1.9 percent reached a single Landed), so
/// the pipeline gets an intent that names the front landing by position and chooses the command its stage calls for.
[<RequireQualifiedAccess>]
type FrontStep =
  | Succeed
  | Conflict
  | ComputeAffected
  | PassTests
  | FailTests
  | Inconclusive
  | FastForwardFails

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
  | IAdvance of FrontStep
  /// `OpenCohort`: the cohort's own scope (an idempotent no-op that is still a ledger entry) or another one (refused).
  | IOpen of scopeIdx: int
  /// A `Join` addressed to a cohort that is not this one. `decide` refuses it before anything else, and the state
  /// and the ledger must not move.
  | IWrongScope of agent: int * scopeIdx: int
  | IInconclusive of landingIdx: int
  | IFastForwardFailed of landingIdx: int
  | ISetHead of by: int * head: int

let genFrontStep =
  Gen.frequency [
    8, Gen.constant FrontStep.Succeed
    1, Gen.constant FrontStep.Conflict
    2, Gen.constant FrontStep.ComputeAffected
    2, Gen.constant FrontStep.PassTests
    1, Gen.constant FrontStep.FailTests
    1, Gen.constant FrontStep.Inconclusive
    1, Gen.constant FrontStep.FastForwardFails
  ]

let private genIntent =
  Gen.frequency [
    4, Gen.map2 (fun a r -> IJoin(a, r)) (Gen.choose (1, 4)) genRole
    2, Gen.map IDepart (Gen.choose (1, 4))
    2, Gen.map IRenewLease (Gen.choose (1, 4))
    2, Gen.map ITick (Gen.choose (0, 60))
    5, Gen.map2 (fun a s -> IAcquire(a, s)) (Gen.choose (1, 4)) (Gen.choose (0, 3))
    3, Gen.map2 (fun a c -> IReleaseOwn(a, c)) (Gen.choose (1, 4)) (Gen.choose (0, 10))
    2, Gen.map3 (fun b c t -> IReassign(b, c, t)) (Gen.choose (1, 4)) (Gen.choose (0, 10)) (Gen.choose (1, 4))
    2, Gen.map2 (fun a p -> IObserveSave(a, p)) (Gen.choose (1, 4)) (Gen.choose (0, 3))
    4, Gen.map2 (fun a c -> IRequestLanding(a, c)) (Gen.choose (1, 4)) (Gen.choose (0, 1000))
    7, Gen.map IAdvance genFrontStep
    1, Gen.map IOpen (Gen.choose (0, 5))
    1, Gen.map2 (fun a s -> IWrongScope(a, s)) (Gen.choose (1, 4)) (Gen.choose (0, 3))
    1, Gen.map IInconclusive (Gen.choose (0, 10))
    1, Gen.map IFastForwardFailed (Gen.choose (0, 10))
    1, Gen.map2 (fun b h -> ISetHead(b, h)) (Gen.choose (1, 4)) (Gen.choose (0, 5))
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

/// The ONE history that hands an ORPHANED claim on, written out because a random corpus reaches
/// it only by luck. `ReassignClaim` is accepted only for a claim whose holder has LEFT (that is
/// what orphans it) and only for the conductor who is still here. Measured on this file's own
/// corpus it went missing in 4 of 40 draws on net11, so the case the coverage claim in test 31
/// rests on was a coin toss. A coverage claim made by luck is a flake wearing a green coat, so
/// the path is in the corpus by construction instead: the next rare case gets a history here
/// too, and the claim stops depending on the draw.
let reassignHistory =
  seating
  @ [ IAcquire(3, 0)       // the holder takes a claim
      IDepart 3            // the holder leaves, and the claim is orphaned
      IReassign(1, 0, 2) ] // the conductor hands it to a member who is still here

/// The ordinary intents, leaning hard on the landing pipeline: claims by the two implementers, landings requested by
/// them, and the front landing pushed along. Seated first, as the delegating histories are. This is the history the
/// properties that need a landing to HAPPEN (5, 6, 7, 13) run over; the plain `genIntent` histories reach a landing in
/// about one in twenty, which is right for the properties about members and claims and far too few for these.
let genPipelineIntent =
  Gen.frequency [
    6, genIntent
    5, Gen.map2 (fun a s -> IAcquire(a, s)) (Gen.choose (1, 2)) (Gen.choose (0, 3))
    5, Gen.map2 (fun a c -> IRequestLanding(a, c)) (Gen.choose (1, 2)) (Gen.choose (0, 1000))
    12, Gen.map IAdvance genFrontStep
  ]

type PipelineHistory = PipelineHistory of Intent list

type PipelineGenerators =
  static member History() =
    Arb.fromGen (Gen.choose (10, 60) |> Gen.bind (fun n -> Gen.listOfLength n genPipelineIntent) |> Gen.map (fun intents -> PipelineHistory(seating @ intents)))

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

let pipelineConfig = { cohortConfig with arbitrary = [ typeof<PipelineGenerators> ] }

// ── The harness: fold Intents through `decide`, keeping a full step log ────

/// `StepClock` and `StepEntropy` are what `decide` was handed for this step: a faithful `LedgerEntry` needs both,
/// and they are named apart from `LedgerEntry.Clock`/`Entropy` so a record literal can never be read as the wrong type.
type Step = {
  Before: CohortState<Agent>
  After: CohortState<Agent>
  Command: CohortCommand<Agent>
  Events: CohortEvent<Agent> list
  StepClock: DateTime
  StepEntropy: byte[]
}

/// Every command handed to `decide`, with what it said. `Steps` holds only the accepted ones; this is what lets a test
/// ask whether a generator ever TRIED a command, and whether `decide` refused it for the reason it should.
type Attempt = { Tried: CohortCommand<Agent>; Verdict: Result<unit, CohortError<Agent>> }

type Harness = {
  Clock: DateTime
  NextEntropy: int
  State: CohortState<Agent>
  ClaimHistory: ClaimId list
  LandingHistory: LandingId list
  Steps: Step list
  Attempts: Attempt list
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

/// Cohorts that are NOT the one every state in this file is opened as. A command addressed to one of these is refused
/// `WrongCohortScope`, which is the guard the scope model rests on.
let otherScopes = [| CohortScope.Named "elsewhere"; CohortScope.Repository "/repo-b" |]

let private initHarness () : Harness = {
  Clock = epoch
  NextEntropy = 0
  State = CohortState.empty ()
  ClaimHistory = []
  LandingHistory = []
  Steps = []
  Attempts = []
}

let private applyCommand (h: Harness) (cmd: CohortCommand<Agent>) : Harness =
  let entropy = BitConverter.GetBytes h.NextEntropy
  let h1 = { h with NextEntropy = h.NextEntropy + 1 }
  match decide h1.Clock entropy h1.State cmd with
  | Error refusal -> { h1 with Attempts = h1.Attempts @ [ { Tried = cmd; Verdict = Result.Error refusal } ] }
  | Ok(newState, events, _effects) ->
    let newClaims = newState.Claims |> Map.toList |> List.map fst |> List.filter (fun c -> not (List.contains c h1.ClaimHistory))
    let newLandings = newState.Landings |> Map.toList |> List.map fst |> List.filter (fun l -> not (List.contains l h1.LandingHistory))
    { h1 with
        State = newState
        ClaimHistory = h1.ClaimHistory @ newClaims
        LandingHistory = h1.LandingHistory @ newLandings
        Steps = h1.Steps @ [ { Before = h1.State; After = newState; Command = cmd; Events = events; StepClock = h1.Clock; StepEntropy = entropy } ]
        Attempts = h1.Attempts @ [ { Tried = cmd; Verdict = Ok() } ] }

let private testIdsOf (n: int) (prefix: string) = [ for i in 1 .. (abs n % 5) -> TestId(sprintf "%s%d" prefix i) ]

let pick (pool: 'a list) (idx: int) : 'a option =
  match pool with
  | [] -> None
  | _ -> Some pool.[abs idx % pool.Length]

/// The ONE place an `Intent` becomes a `CohortCommand`, generic in the member type so the pure harness (`Agent`) and
/// the live owner (`MemberId`) are driven by the very same resolution. Two earlier copies of this table disagreed
/// about where claim and landing indices point, and a copy that drifts is a test of the copy.
///
/// `claimPool` and `landingPool` are the ids an index may point at; the caller says which (every id ever minted for
/// the harness, the ids currently in state for the owner). The advance is how far the clock moves before the command
/// runs, which only `ITick` uses. `None` is an intent that points at nothing yet, so it is skipped.
let resolveIntent<'m when 'm: comparison>
  (who: int -> 'm)
  (absent: 'm)
  (claimPool: ClaimId list)
  (landingPool: LandingId list)
  (state: CohortState<'m>)
  (entropyN: int)
  (intent: Intent)
  : (TimeSpan * CohortCommand<'m>) option =
  let now cmd = Some(TimeSpan.Zero, cmd)
  let onLanding l build = pick landingPool l |> Option.map (fun lid -> TimeSpan.Zero, build lid)
  match intent with
  | IOpen i -> now (CohortCommand.OpenCohort(if abs i % 3 = 0 then machine else otherScopes.[abs i % otherScopes.Length]))
  | IWrongScope(a, i) -> now (CohortCommand.Join(who a, JoinableRole.Implementer, None, otherScopes.[abs i % otherScopes.Length]))
  | IInconclusive l -> onLanding l (fun lid -> CohortCommand.VerificationInconclusive(lid, "session still warming", machine))
  | IFastForwardFailed l -> onLanding l (fun lid -> CohortCommand.FastForwardFailed(lid, "ref moved under us", machine))
  | ISetHead(by, h) -> now (CohortCommand.SetIntegrationHead(who by, sprintf "head-%d" (abs h % 3), machine))
  | IJoin(a, role) -> now (CohortCommand.Join(who a, role, None, machine))
  | IDepart a -> now (CohortCommand.Depart(who a, machine))
  | IRenewLease a -> now (CohortCommand.RenewLease(who a, machine))
  | ITick minutes -> Some(TimeSpan.FromMinutes(float (abs minutes % 90)), CohortCommand.Tick machine)
  | IAcquire(a, s) -> now (CohortCommand.AcquireClaim(who a, scopeOf s, "purpose", machine))
  | IReleaseOwn(a, c) ->
    pick claimPool c
    |> Option.bind (fun cid ->
      Map.tryFind cid state.Claims
      |> Option.map (fun claim -> TimeSpan.Zero, CohortCommand.ReleaseClaim(who a, cid, claim.Fence, machine)))
  | IReassign(by, c, toA) -> pick claimPool c |> Option.map (fun cid -> TimeSpan.Zero, CohortCommand.ReassignClaim(who by, cid, who toA, machine))
  | IObserveSave(a, p) -> now (CohortCommand.ObserveSave(who a, pathOf p, machine))
  | IRequestLanding(a, commit) ->
    let agent = who a
    let backing =
      state.Claims
      |> Map.toList
      |> List.choose (fun (cid, c) ->
        match c.State with
        | ClaimState.Held holder when holder = agent -> Some(cid, c.Fence)
        | _ -> None)
    now (CohortCommand.RequestLanding(agent, backing, [ sprintf "commit-%d" commit ], "landing statement", machine))
  | IRebaseOk l -> onLanding l (fun lid -> CohortCommand.RebaseCompleted(lid, Ok(sprintf "sha-%d" entropyN), machine))
  | IRebaseConflict(l, f) -> onLanding l (fun lid -> CohortCommand.RebaseCompleted(lid, Error [ sprintf "conflict-%d.fs" (abs f) ], machine))
  | IAffected(l, t) -> onLanding l (fun lid -> CohortCommand.AffectedComputed(lid, testIdsOf t "t", machine))
  | ITestsPass l -> onLanding l (fun lid -> CohortCommand.TestsCompleted(lid, [], machine))
  | ITestsFail(l, f) -> onLanding l (fun lid -> CohortCommand.TestsCompleted(lid, testIdsOf (1 + abs f) "f", machine))
  | IFastForward l -> onLanding l (fun lid -> CohortCommand.FastForwardCompleted(lid, sprintf "landed-%d" entropyN, machine))
  | IWithdraw(a, l) -> onLanding l (fun lid -> CohortCommand.WithdrawLanding(who a, lid, machine))
  | IVeto(by, l) -> onLanding l (fun lid -> CohortCommand.VetoLanding(who by, lid, "veto reason", machine))
  // `toAgent` 5 is an agent that never joins (`agentOf` maps 1..4), so absent targets are generated too.
  | IDelegate(by, toA) -> now (CohortCommand.DelegateConductor(who by, (if toA = 5 then absent else who toA), machine))
  | IResolveVeto(by, l) -> onLanding l (fun lid -> CohortCommand.ResolveVeto(who by, lid, machine))
  | IAdvance step ->
    // The landing at the front of the queue, and the command ITS stage calls for. A landing that is not at the front
    // is never advanced by `decide` (`LandingNotInExpectedState`), so naming the front is what lets a history finish one.
    match state.Queue with
    | [] -> None
    | front :: _ ->
      match Map.tryFind front state.Landings with
      | None -> None
      | Some request ->
        match request.State, step with
        | LandingState.Rebasing _, FrontStep.Conflict -> now (CohortCommand.RebaseCompleted(front, Error [ "conflict.fs" ], machine))
        | LandingState.Rebasing _,
          (FrontStep.Succeed | FrontStep.ComputeAffected | FrontStep.PassTests | FrontStep.FailTests | FrontStep.Inconclusive | FrontStep.FastForwardFails) ->
          now (CohortCommand.RebaseCompleted(front, Ok(sprintf "sha-%d" entropyN), machine))
        | LandingState.Verifying _, FrontStep.Succeed -> now (CohortCommand.FastForwardCompleted(front, sprintf "landed-%d" entropyN, machine))
        | LandingState.Verifying _, FrontStep.Conflict -> now (CohortCommand.AffectedComputed(front, testIdsOf 2 "t", machine))
        | LandingState.Verifying _, FrontStep.ComputeAffected -> now (CohortCommand.AffectedComputed(front, [], machine))
        | LandingState.Verifying _, FrontStep.PassTests -> now (CohortCommand.TestsCompleted(front, [], machine))
        | LandingState.Verifying _, FrontStep.FailTests -> now (CohortCommand.TestsCompleted(front, [ TestId "f1" ], machine))
        | LandingState.Verifying _, FrontStep.Inconclusive -> now (CohortCommand.VerificationInconclusive(front, "session still warming", machine))
        | LandingState.Verifying _, FrontStep.FastForwardFails -> now (CohortCommand.FastForwardFailed(front, "ref moved under us", machine))
        | (LandingState.Queued | LandingState.Blocked _ | LandingState.Landed _ | LandingState.Withdrawn), _ -> None

let absentAgent : Agent = { Id = 5; Display = "agent-5" }

let private applyIntent (h: Harness) (intent: Intent) : Harness =
  match resolveIntent agentOf absentAgent h.ClaimHistory h.LandingHistory h.State h.NextEntropy intent with
  | None -> h
  | Some(advance, cmd) -> applyCommand { h with Clock = h.Clock + advance } cmd

let private run (intents: Intent list) : Harness = intents |> List.fold applyIntent (initHarness ())

let private allStates (h: Harness) : CohortState<Agent> list =
  CohortState.empty () :: (h.Steps |> List.map (fun s -> s.After))

/// The ledger a run wrote: every ACCEPTED step as the `LedgerEntry` that records it (`Seq` dense from 0, the clock and
/// entropy `decide` was handed, the command and the events it returned) — what the ledger properties 10 and 17 replay.
/// Built from the run's own steps, so there is one fold behind it and not a second copy of the intent table.
let private ledgerOf (h: Harness) : LedgerEntry<Agent> list =
  h.Steps
  |> List.mapi (fun i s ->
    { Seq = LanguagePrimitives.Int64WithMeasure(int64 i)
      Clock = s.StepClock
      Entropy = s.StepEntropy
      Command = s.Command
      Events = s.Events })

let private toLedger (intents: Intent list) : LedgerEntry<Agent> list = intents |> run |> ledgerOf

// ── The live owner, driven by the same intents ───────────────────────────
//
// `run` folds `decide` and nothing else, so a property over it can only ever say something about `decide`. The owner
// (`CohortOwner`) is the second, independent path: it holds the state in memory, applies commands in arrival order and
// WRITES the ledger as it goes. Comparing what it holds with `replay` of what it wrote is the property that can fail,
// because the two paths share `decide` but not the clock, the entropy or the order the ledger recorded.

let silentLogger =
  { new SageFs.Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let memberOf (i: int) : MemberId = MemberId.Minted(sprintf "agent-%d" (1 + abs i % 4))
let absentMember : MemberId = MemberId.Minted "agent-5"

/// Drive a real `CohortOwner` over `port` with `intents`, resolving each against the state the owner holds NOW, and
/// return the state it holds at the end. The owner's performer is the default (it completes nothing), so a landing
/// moves only when an intent says so, and the ledger order is the intent order.
let driveOwner (port: LedgerPort<MemberId>) (intents: Intent list) : Task<CohortState<MemberId>> =
  task {
    let clock = ref epoch
    let drawn = ref 0
    let entropy () =
      let n = drawn.Value
      drawn.Value <- n + 1
      BitConverter.GetBytes n
    use owner = CohortOwner.start silentLogger machine port (fun () -> clock.Value) entropy (fun _ -> ([], [], [], 0L))
    for intent in intents do
      let state = owner.ReadCohortState()
      let claimIds = state.Claims |> Map.toList |> List.map fst
      let landingIds = state.Landings |> Map.toList |> List.map fst
      match resolveIntent memberOf absentMember claimIds landingIds state drawn.Value intent with
      | None -> ()
      | Some(advance, cmd) ->
        clock.Value <- clock.Value + advance
        let! _ = owner.Commit cmd
        ()
    return owner.ReadCohortState()
  }

/// Does the state the owner holds equal `replay` of the ledger it wrote?
let ownerAgreesWithReplay (port: LedgerPort<MemberId>) (intents: Intent list) : Task<bool> =
  task {
    let! live = driveOwner port intents
    return replay (port.ReadAll machine) = live
  }

/// Twins of the in-memory ledger that each record one thing wrongly. A comparison that cannot tell these from the
/// honest ledger is not comparing anything.
let ledgerThatRecordsTheWrongClock (inner: LedgerPort<MemberId>) : LedgerPort<MemberId> =
  { inner with Append = fun e -> inner.Append { e with Clock = epoch } }

let ledgerThatDropsEntropy (inner: LedgerPort<MemberId>) : LedgerPort<MemberId> =
  { inner with Append = fun e -> inner.Append { e with Entropy = [||] } }

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
  // A fast-forward that failed for an infrastructure reason sends the landing back through the pipeline (bounded by
  // `maxFastForwardAttempts`). The generator never produced `FastForwardFailed` before the pipeline histories did, so
  // this edge was missing from the declared graph and nothing said so. It is the documented retry, not a defect.
  | "Verifying", "Rebasing" -> true
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
  // TOTAL: a landed id that was never queued is a failure, not a row to drop. (`List.choose id` used to discard it,
  // so a landing the queue never saw could land and the property would not notice.)
  let positions = landedOrder |> List.map posOf
  positions |> List.forall Option.isSome
  && positions |> List.choose id |> List.pairwise |> List.forall (fun (a, b) -> a < b)

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

// ── Property 11: a landing verified against H1 never lands when the head is H2 <> H1 ──
//
// The state is REACHED through `decide` (join, set the head to H1, claim, request the landing, rebase, affected, tests),
// then the conductor moves the head to H2, then the landing is fast-forwarded. It used to be a record literal with
// "H1-" and "H2-" prefixed onto the two generated strings, so the heads could never be equal: the half of the
// property that says a landing LANDS when the head did not move was dead, and a `decide` that never landed anything
// passed it. Equal heads are now generated half the time.

/// What a fast-forward after the head moved did. A case per outcome, so a landing that did something else is not
/// confused with either of the two the property is about.
type FastForwardReached =
  | SetupRefused of why: string
  | Landed of integrationCommit: string
  | HeadMoved of verified: string * now: string
  | SomethingElse of what: string

type DecideFn =
  DateTime
    -> byte[]
    -> CohortState<Agent>
    -> CohortCommand<Agent>
    -> Result<CohortState<Agent> * CohortEvent<Agent> list * CohortEffect<Agent> list, CohortError<Agent>>

/// The head the landing was verified against, and the head at fast-forward time.
type HeadPair = HeadPair of verifiedAgainst: string * atFastForward: string

let fastForwardAfterHeadMoved (decideFn: DecideFn) (scope: ClaimScope) (HeadPair(h1, h2)) : FastForwardReached =
  let conductor = agentOf 1
  let step (state: Result<CohortState<Agent>, string>, n: int) (cmd: CohortCommand<Agent>) =
    match state with
    | Result.Error _ -> state, n
    | Ok st ->
      match decideFn epoch (BitConverter.GetBytes n) st cmd with
      | Ok(next, _, _) -> Ok next, n + 1
      | Error refusal -> Result.Error(sprintf "%A was refused: %A" cmd refusal), n + 1
  let after (build: CohortState<Agent> -> CohortCommand<Agent>) (state: Result<CohortState<Agent>, string>, n: int) =
    match state with
    | Result.Error _ -> state, n
    | Ok st -> step (state, n) (build st)
  let claimOf (st: CohortState<Agent>) = st.Claims |> Map.toList |> List.head
  let landingOf (st: CohortState<Agent>) = st.Landings |> Map.toList |> List.head |> fst
  let verified =
    (Ok(CohortState.empty ()), 0)
    |> fun s -> step s (CohortCommand.Join(conductor, JoinableRole.Implementer, None, machine))
    |> fun s -> step s (CohortCommand.SetIntegrationHead(conductor, h1, machine))
    |> fun s -> step s (CohortCommand.AcquireClaim(conductor, scope, "purpose", machine))
    |> after (fun st ->
      let cid, claim = claimOf st
      CohortCommand.RequestLanding(conductor, [ cid, claim.Fence ], [ "c1" ], "statement", machine))
    |> after (fun st -> CohortCommand.RebaseCompleted(landingOf st, Ok "rebased-sha", machine))
    |> after (fun st -> CohortCommand.AffectedComputed(landingOf st, [], machine))
    |> after (fun st -> CohortCommand.TestsCompleted(landingOf st, [], machine))
    |> fun s -> step s (CohortCommand.SetIntegrationHead(conductor, h2, machine))
  match fst verified with
  | Result.Error why -> SetupRefused why
  | Ok st ->
    let lid = landingOf st
    match decideFn epoch [| 99uy |] st (CohortCommand.FastForwardCompleted(lid, "landed-sha", machine)) with
    | Error refusal -> SomethingElse(sprintf "the fast-forward was refused: %A" refusal)
    | Ok(next, events, _) ->
      match next.Landings.[lid].State with
      | LandingState.Landed sha when events |> List.exists (function CohortEvent.LandingLanded _ -> true | _ -> false) -> Landed sha
      | LandingState.Blocked(LandingBlocker.HeadMoved(f, t), _) -> HeadMoved(f, t)
      | other -> SomethingElse(sprintf "%A" other)

/// What a fast-forward after the head moved MUST do: land when the heads agree, block naming both when they do not.
let fastForwardDidTheRightThing (HeadPair(h1, h2)) (reached: FastForwardReached) : bool =
  if h1 = h2 then
    (match reached with Landed "landed-sha" -> true | _ -> false)
  else
    (match reached with HeadMoved(f, t) -> f = h1 && t = h2 | _ -> false)

let genHead =
  Gen.choose (0, 6)
  |> Gen.bind (fun n -> Gen.listOfLength n (Gen.elements [ 'a' .. 'f' ]))
  |> Gen.map (fun cs -> "head-" + String(Array.ofList cs))

/// Equal heads about half the time, so neither branch of the property is a rarity.
type HeadGenerators =
  static member Pair() =
    Gen.oneof [
      genHead |> Gen.map (fun h -> HeadPair(h, h))
      Gen.zip genHead genHead |> Gen.where (fun (a, b) -> a <> b) |> Gen.map HeadPair
    ]
    |> Arb.fromGen

  static member Scope() = Gen.elements (List.ofArray scopePool) |> Arb.fromGen

let headConfig ={ cohortConfig with arbitrary = [ typeof<HeadGenerators> ] }

/// Two ways `decide` could be wrong that the property must notice. `neverLands` reports HeadMoved where the real one
/// lands, with the payload the blocked path would carry; `ignoresTheHead` lands whatever the head is.
let neverLands : DecideFn =
  fun clock entropy state cmd ->
    match cmd, decide clock entropy state cmd with
    | CohortCommand.FastForwardCompleted(lid, _, _), Ok(next, events, effects) ->
      match next.Landings.[lid].State, state.Landings.[lid].State with
      | LandingState.Landed _, LandingState.Verifying(verified, _, _, _) ->
        let blocked = { next.Landings.[lid] with State = LandingState.Blocked(LandingBlocker.HeadMoved(verified, state.IntegrationHead), NextAction.RebaseAndResubmit) }
        Ok({ next with Landings = Map.add lid blocked next.Landings }, [ CohortEvent.LandingStateChanged(lid, blocked.State) ], effects)
      | _ -> Ok(next, events, effects)
    | _, result -> result

let ignoresTheHead : DecideFn =
  fun clock entropy state cmd ->
    match cmd with
    | CohortCommand.FastForwardCompleted(lid, _, _) ->
      match state.Landings.[lid].State with
      | LandingState.Verifying(verified, _, _, _) -> decide clock entropy { state with IntegrationHead = verified } cmd
      | _ -> decide clock entropy state cmd
    | _ -> decide clock entropy state cmd

// ── Generators for the targeted races (4, 12, 15, 16, 19) ──────────────────
//
// These properties took an `int` and used it modulo four, so each was a four-case example test run two hundred times.
// They take real generated values now: a claim scope drawn from the pool, how many acquire/release round trips move
// the fence counter first, how far behind a presented fence is, an arbitrary display name, and a generated plan.

/// How many acquire/release round trips happen before the claim under test.
type Cycles = Cycles of int

/// How many fences behind the current one a stale command presents.
type Staleness = Staleness of int

/// A display name, including ones that collide with nothing and ones made of awkward characters.
type DisplayName = DisplayName of string

[<RequireQualifiedAccess>]
type Reconnection =
  | Steady
  | DropsFirst

type PlanStep = { PlanScope: ClaimScope; Reconnect: Reconnection }

type ReconnectPlan = ReconnectPlan of PlanStep list

type RaceGenerators =
  static member Scope() = Gen.elements (List.ofArray scopePool) |> Arb.fromGen
  static member Cycles() = Gen.choose (0, 3) |> Gen.map Cycles |> Arb.fromGen
  static member Staleness() = Gen.choose (1, 5) |> Gen.map Staleness |> Arb.fromGen

  static member DisplayName() =
    Gen.choose (1, 12)
    |> Gen.bind (fun n -> Gen.listOfLength n (Gen.elements [ 'a'; 'Z'; '0'; ' '; '-'; '_'; 'é'; '/' ]))
    |> Gen.map (fun cs -> DisplayName(String(Array.ofList cs)))
    |> Arb.fromGen

  static member Plan() =
    let reconnection = Gen.elements [ Reconnection.Steady; Reconnection.DropsFirst ]
    let step = Gen.map2 (fun scope reconnect -> { PlanScope = scope; Reconnect = reconnect }) (Gen.elements (List.ofArray scopePool)) reconnection
    Gen.choose (1, 6) |> Gen.bind (fun n -> Gen.listOfLength n step) |> Gen.map ReconnectPlan |> Arb.fromGen

/// How many of a sample of histories reached each landing outcome. Named `...In` so a field is never read as the
/// event or state case of the same name.
type Reach = { LandedIn: int; TwoLandedIn: int; BlockedIn: int; VetoedIn: int }

let raceConfig ={ cohortConfig with arbitrary = [ typeof<RaceGenerators> ] }

/// Every checked-in cohort ledger, parsed: file name and entries. A file that does not parse fails the test that asks.
let corpusLedgers () : (string * LedgerEntry<MemberId> list) list =
  let corpusDir = System.IO.Path.Combine(repoRoot, "SageFs.Tests", "cohorts")
  System.IO.Directory.CreateDirectory corpusDir |> ignore
  System.IO.Directory.GetFiles(corpusDir, "*.ledger.jsonl")
  |> Array.sort
  |> Array.map (fun file ->
    match SageFs.Features.CohortLedgerExport.fromJsonl (System.IO.File.ReadAllText file) with
    | Error e -> failwithf "%s failed to parse: %s" file e
    | Ok(entries: LedgerEntry<MemberId> list) -> System.IO.Path.GetFileName file, entries)
  |> List.ofArray

/// The claim `who` currently HOLDS, if any. A released or orphaned claim stays in the map until it is swept, so
/// "the one claim" is not `Claims |> Map.toList |> List.head`.
let heldClaimOf (who: Agent) (h: Harness) : (ClaimId * Claim<Agent>) option =
  h.State.Claims
  |> Map.toList
  |> List.tryFind (fun (_, c) -> match c.State with ClaimState.Held holder -> holder = who | _ -> false)

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

      testPropertyWithConfig pipelineConfig "5 (pipeline histories): LandingState transitions follow the declared graph" <| fun (PipelineHistory intents) ->
        landingTransitionsFollowTheGraph (run intents)

      testPropertyWithConfig cohortConfig "6: Landed implies every claim was Held by the requester at land time" <| fun (intents: Intent list) ->
        landedImpliesClaimsHeldAtLandTime (run intents)

      testPropertyWithConfig pipelineConfig "6 (pipeline histories): Landed implies every claim was Held by the requester at land time" <| fun (PipelineHistory intents) ->
        landedImpliesClaimsHeldAtLandTime (run intents)

      // Two INDEPENDENT paths: the state a live `CohortOwner` holds after being driven through its command path, and
      // `replay` of the ledger that same owner wrote. (This used to compare `List.fold decide` with `replay`, which is
      // the same `List.fold decide`: it proved `decide` deterministic and nothing about the ledger.)
      testPropertyWithConfig cohortConfig "7: the state the live owner holds equals replay of the ledger it wrote" <| fun (intents: Intent list) ->
        ownerAgreesWithReplay (InMemory.create<MemberId> ()) intents

      testPropertyWithConfig pipelineConfig "7 (pipeline histories): the state the live owner holds equals replay of the ledger it wrote, through landings" <| fun (PipelineHistory intents) ->
        ownerAgreesWithReplay (InMemory.create<MemberId> ()) intents

      // The teeth: the same comparison must FAIL for a ledger that records one thing wrongly. If it cannot, property 7
      // above is comparing a value with itself again.
      testTask "7 (teeth): a ledger that records the wrong clock, or drops the entropy, is caught by property 7's comparison" {
        // Seated first, so the history spends its steps on claims, landings and lapses and not on getting anyone in.
        let histories = Gen.sample 300 genIntents |> Array.map (fun intents -> seating @ intents)
        let disagreements (wrap: LedgerPort<MemberId> -> LedgerPort<MemberId>) =
          task {
            let mutable count = 0
            for intents in histories do
              let! agrees = ownerAgreesWithReplay (wrap (InMemory.create<MemberId> ())) intents
              if not agrees then count <- count + 1
            return count
          }
        let! honest = disagreements id
        honest |> Expect.equal "an honest ledger always agrees with the owner" 0
        let! wrongClock = disagreements ledgerThatRecordsTheWrongClock
        Expect.isGreaterThan "a ledger with the wrong clock disagrees on some history" (wrongClock, 0)
        let! noEntropy = disagreements ledgerThatDropsEntropy
        Expect.isGreaterThan "a ledger without the entropy disagrees on some history" (noEntropy, 0)
      }

      // A ledger this build wrote must replay without a single skipped row and without one event it cannot reproduce.
      // This is what makes a row in `Dropped` or `Diverged` mean "the ledger is older than the code", not noise.
      testPropertyWithConfig cohortConfig "7 (audit): replaying a ledger this build wrote drops no row and finds no event it cannot reproduce" <| fun (intents: Intent list) ->
        let audit = replayAuditIn machine (toLedger intents)
        audit.Dropped.IsEmpty && audit.Diverged.IsEmpty && audit.Head.State = replay (toLedger intents)

      test "7 (audit): a row decide now refuses comes back in Dropped with its Seq and the refusal, and the state is what replayIn returns" {
        let honest = toLedger [ IJoin(1, JoinableRole.Implementer) ]
        let ghost = ClaimId "ghost"
        let refused : LedgerEntry<Agent> =
          { Seq = LanguagePrimitives.Int64WithMeasure 1L
            Clock = epoch.AddMinutes 1.
            Entropy = [||]
            Command = CohortCommand.ReleaseClaim(agentOf 1, ghost, 0L<Measures.fence>, machine)
            Events = [] }
        let audit = replayAuditIn machine (honest @ [ refused ])
        audit.Dropped
        |> List.map (fun d -> int64 d.DroppedSeq, d.Refusal)
        |> Expect.equal "the refused row is reported with its sequence number and the refusal" [ 1L, CohortError.UnknownClaim ghost ]
        audit.Head.State |> Expect.equal "the dropped row changes nothing" (replayIn machine honest)
        audit.Head.Seq |> Expect.equal "the head still names the last row read" refused.Seq
      }

      test "7 (audit): a row decide still accepts but answers with other events than were recorded comes back in Diverged" {
        let honest = toLedger [ IJoin(1, JoinableRole.Implementer) ]
        let tickAt = epoch.AddMinutes 20.
        // 20 minutes of silence is inside today's 30 minute lease window, so decide returns no events for this Tick.
        // The row records a departure, which is what a shorter window would have written.
        let recorded : LedgerEntry<Agent> =
          { Seq = LanguagePrimitives.Int64WithMeasure 1L
            Clock = tickAt
            Entropy = [||]
            Command = CohortCommand.Tick machine
            Events = [ CohortEvent.MemberDeparted(agentOf 1, tickAt) ] }
        let audit = replayAuditIn machine (honest @ [ recorded ])
        audit.Dropped |> Expect.isEmpty "the row still applies"
        audit.Diverged
        |> List.map (fun d -> int64 d.DivergedSeq, d.Recorded.Length, d.Replayed.Length)
        |> Expect.equal "the row is reported with what was recorded and what replay produced" [ 1L, 1, 0 ]
      }

      testPropertyWithConfig cohortConfig "13: landed order never precedes an earlier landing's request order" <| fun (intents: Intent list) ->
        landedOrderRespectsRequestOrder (run intents)

      // Two landings in one history is what this property is ABOUT, and the plain histories reach it in about one in
      // four hundred. The pipeline histories reach it in most runs (see 29), and 29's seeded history guarantees it.
      testPropertyWithConfig pipelineConfig "13 (pipeline histories): landed order never precedes an earlier landing's request order" <| fun (PipelineHistory intents) ->
        landedOrderRespectsRequestOrder (run intents)

      testPropertyWithConfig cohortConfig "14: a departing member's Held claims are all Orphaned in the same step" <| fun (intents: Intent list) ->
        departureOrphansAllHeldClaimsImmediately (run intents)
    ]

    testList "targeted races (4, 11, 12, 15)" [

      testPropertyWithConfig raceConfig "4: a stale fence is refused whatever else the command carries" <| fun (scope: ClaimScope) (Cycles roundTrips) (Staleness behind) ->
        let a = agentOf 1
        let h0 = initHarness ()
        let h1 = applyCommand h0 (CohortCommand.Join(a, JoinableRole.Implementer, None, machine))
        // Acquire/release round trips move the fence counter before the claim under test is taken, so the stale
        // fence is not always "one below the first fence there ever was".
        let h2 =
          [ 1 .. roundTrips ]
          |> List.fold
            (fun h _ ->
              let acquired = applyCommand h (CohortCommand.AcquireClaim(a, scope, "round trip", machine))
              match heldClaimOf a acquired with
              | Some(cid, claim) -> applyCommand acquired (CohortCommand.ReleaseClaim(a, cid, claim.Fence, machine))
              | None -> acquired)
            h1
        let h3 = applyCommand h2 (CohortCommand.AcquireClaim(a, scope, "purpose", machine))
        match heldClaimOf a h3 with
        | Some(cid, claim) ->
          let staleFence = claim.Fence - (int64 behind) * 1L<Measures.fence>
          match decide h3.Clock [| 99uy |] h3.State (CohortCommand.ReleaseClaim(a, cid, staleFence, machine)) with
          | Error(CohortError.StaleClaimFence(errCid, presented, current)) ->
            errCid = cid && presented = staleFence && current = claim.Fence
          | _ -> false
        | None -> false

      testPropertyWithConfig headConfig "11: a landing verified against H1 lands when the head is still H1 and is blocked HeadMoved(H1, H2) when it is H2 <> H1" <| fun (scope: ClaimScope) (pair: HeadPair) ->
        fastForwardAfterHeadMoved decide scope pair |> fastForwardDidTheRightThing pair

      // The teeth. Property 11 is only worth having if it fails for a `decide` that is wrong in either direction, and
      // if both of its branches are reached: the earlier version could never reach the equal-heads branch at all.
      testCase "11 (teeth): the property reaches both branches, and fails a decide that never lands and one that ignores the head" <| fun _ ->
        let pairs = Gen.sample 400 (Arb.toGen (HeadGenerators.Pair()))
        let satisfying (decideFn: DecideFn) =
          pairs |> Array.filter (fun pair -> fastForwardAfterHeadMoved decideFn scopePool.[0] pair |> fastForwardDidTheRightThing pair) |> Array.length
        let equalHeads = pairs |> Array.filter (fun (HeadPair(a, b)) -> a = b) |> Array.length
        Expect.isGreaterThan "the equal-heads branch is reached" (equalHeads, 100)
        Expect.isLessThan "the different-heads branch is reached" (equalHeads, pairs.Length - 100)
        satisfying decide |> Expect.equal "the real decide satisfies every pair" pairs.Length
        Expect.isLessThan "a decide that never lands fails the equal-heads pairs" (satisfying neverLands, pairs.Length - 100)
        Expect.isLessThan "a decide that ignores the head fails the different-heads pairs" (satisfying ignoresTheHead, pairs.Length - 100)

      testPropertyWithConfig raceConfig "12: a departed member's stale-fence command never changes the claim it no longer holds" <| fun (scope: ClaimScope) (Cycles bystanders) ->
        let a = agentOf 1
        let h0 = initHarness ()
        let h1 = applyCommand h0 (CohortCommand.Join(a, JoinableRole.Implementer, None, machine))
        // Other members are present around the one that departs; none of them may change what happens to its claim.
        let h2 =
          [ 2 .. 1 + bystanders ]
          |> List.fold (fun h i -> applyCommand h (CohortCommand.Join(agentOf i, JoinableRole.Implementer, None, machine))) h1
        let h3 = applyCommand h2 (CohortCommand.AcquireClaim(a, scope, "purpose", machine))
        match heldClaimOf a h3 with
        | Some(cid, claimBeforeDepart) ->
          let h4 = applyCommand h3 (CohortCommand.Depart(a, machine))
          // The member's late command, presenting the fence from BEFORE departure.
          match decide h4.Clock [| 7uy |] h4.State (CohortCommand.ReleaseClaim(a, cid, claimBeforeDepart.Fence, machine)) with
          | Error _ ->
            match h4.State.Claims.[cid].State with
            | ClaimState.Orphaned(prev, _) -> prev = a
            | _ -> false
          | Ok _ -> false
        | None -> false

      testPropertyWithConfig raceConfig "15: a hostile display-name collision never lets one member touch another's claim" <| fun (scope: ClaimScope) (DisplayName name) ->
        let a = { Id = 1; Display = name }
        let hostile = { Id = 2; Display = name }
        let h0 = initHarness ()
        let h1 = applyCommand h0 (CohortCommand.Join(a, JoinableRole.Implementer, None, machine))
        let h2 = applyCommand h1 (CohortCommand.Join(hostile, JoinableRole.Implementer, None, machine))
        let h3 = applyCommand h2 (CohortCommand.AcquireClaim(a, scope, "purpose", machine))
        match heldClaimOf a h3 with
        | Some(cid, claim) ->
          match decide h3.Clock [| 3uy |] h3.State (CohortCommand.ReleaseClaim(hostile, cid, claim.Fence, machine)) with
          | Error(CohortError.NotClaimHolder(errCid, requester)) ->
            errCid = cid
            && requester = hostile
            && (match h3.State.Claims.[cid].State with ClaimState.Held h -> h = a | _ -> false)
          | _ -> false
        | None -> false
    ]

    testList "generated schedule with reconnection (16)" [

      // A generated plan: how many landings, which scope each claims, and before which of them the member drops its
      // connection (departs and rejoins). `Reconnection` is a case per behaviour, so a step is never "flaky: true".
      testPropertyWithConfig raceConfig "16: intermittent reconnection between landings never changes the set of landed commits" <| fun (ReconnectPlan steps) ->
        let a = agentOf 1

        let runPlan (reconnection: PlanStep -> Reconnection) : Set<string> =
          let joined = applyCommand (initHarness ()) (CohortCommand.Join(a, JoinableRole.Implementer, None, machine))
          let _, landed =
            steps
            |> List.indexed
            |> List.fold
              (fun (h, landed) (i, step) ->
                let reconnected =
                  match reconnection step with
                  | Reconnection.Steady -> h
                  | Reconnection.DropsFirst ->
                    applyCommand (applyCommand h (CohortCommand.Depart(a, machine))) (CohortCommand.Join(a, JoinableRole.Implementer, None, machine))
                let acquired = applyCommand reconnected (CohortCommand.AcquireClaim(a, step.PlanScope, "purpose", machine))
                match heldClaimOf a acquired with
                | None -> acquired, landed
                | Some(cid, claim) ->
                  let requested =
                    applyCommand acquired (CohortCommand.RequestLanding(a, [ (cid, claim.Fence) ], [ sprintf "commit-%d" i ], "statement", machine))
                  match requested.State.Queue with
                  | [] -> requested, landed
                  | lid :: _ ->
                    let finished =
                      [ CohortCommand.RebaseCompleted(lid, Ok "head", machine)
                        CohortCommand.AffectedComputed(lid, [], machine)
                        CohortCommand.TestsCompleted(lid, [], machine)
                        CohortCommand.FastForwardCompleted(lid, sprintf "landed-%d" i, machine) ]
                      |> List.fold applyCommand requested
                    match finished.State.Landings.[lid].State with
                    | LandingState.Landed sha -> finished, Set.add sha landed
                    | _ -> finished, landed)
              (joined, Set.empty)
          landed

        let honestLanded = runPlan (fun _ -> Reconnection.Steady)
        let flakyLanded = runPlan (fun step -> step.Reconnect)
        // Every step landed in the honest run (a lone member with a free scope), so the comparison is not two empty sets.
        honestLanded.Count = List.length steps && honestLanded = flakyLanded
    ]

    testList "the histories reach what the properties are about (29, 30)" [

      // Reachability, measured on the REAL generators (4000 histories each, run through the real `decide`) when these
      // floors were set, as a percentage of histories:
      //
      //                  Landed   two Landed   Blocked   Vetoed
      //   before (old)     1.9       0.03         6.1      1.4    (the weights this suite shipped with)
      //   plain            5.7       0.25         7.6      1.4
      //   pipeline        87.3      66.8         70.9      3.9
      //
      // The floors sit well under the measurements, so a run of bad luck cannot trip them and a generator that stops
      // reaching a landing does. A property over "every Landed event" is empty when no history lands.
      testCase "29: the plain histories reach Landed and Blocked, and the pipeline histories reach Landed, two Landed, Blocked and Vetoed" <| fun _ ->
        let sampled = 1000
        let reach (gen: Gen<Intent list>) =
          let histories = Gen.sample sampled gen |> Array.map run
          let events (h: Harness) = h.Steps |> List.collect (fun s -> s.Events)
          let landedCount (h: Harness) = events h |> List.filter (function CohortEvent.LandingLanded _ -> true | _ -> false) |> List.length
          let blocked (h: Harness) = events h |> List.exists (function CohortEvent.LandingStateChanged(_, LandingState.Blocked _) -> true | _ -> false)
          let vetoed (h: Harness) =
            events h |> List.exists (function CohortEvent.LandingStateChanged(_, LandingState.Blocked(LandingBlocker.VetoedBy _, _)) -> true | _ -> false)
          { LandedIn = histories |> Array.filter (fun h -> landedCount h >= 1) |> Array.length
            TwoLandedIn = histories |> Array.filter (fun h -> landedCount h >= 2) |> Array.length
            BlockedIn = histories |> Array.filter blocked |> Array.length
            VetoedIn = histories |> Array.filter vetoed |> Array.length }
        // A floor of N percent of 1000 histories is N * 10.
        let percent (n: int) = n * sampled / 100
        let plain = reach genIntents
        Expect.isGreaterThan "plain histories reach a Landed landing in at least 3 percent" (plain.LandedIn, percent 3)
        Expect.isGreaterThan "plain histories reach a Blocked landing in at least 3 percent" (plain.BlockedIn, percent 3)
        let pipeline = reach (Arb.toGen (PipelineGenerators.History()) |> Gen.map (fun (PipelineHistory intents) -> intents))
        Expect.isGreaterThan "pipeline histories reach a Landed landing in at least 60 percent" (pipeline.LandedIn, percent 60)
        Expect.isGreaterThan "pipeline histories land TWO landings in at least 40 percent" (pipeline.TwoLandedIn, percent 40)
        Expect.isGreaterThan "pipeline histories reach a Blocked landing in at least 40 percent" (pipeline.BlockedIn, percent 40)
        Expect.isGreaterThan "pipeline histories reach a vetoed landing in at least 1 percent" (pipeline.VetoedIn, percent 1)

      // The one history that lands two, written out, so property 13 and property 6 have a case that cannot be missed
      // by a run of bad luck. Two implementers' worth of work from one member: claim, request, rebase, fast-forward, twice.
      let twoLandings =
        seating
        @ [ IAcquire(1, 0); IRequestLanding(1, 1); IAdvance FrontStep.Succeed; IAdvance FrontStep.Succeed
            IAcquire(1, 1); IRequestLanding(1, 2); IAdvance FrontStep.Succeed; IAdvance FrontStep.Succeed ]

      testTask "30: a seeded history lands two landings, in request order, each with its claims held, and the owner agrees with replay" {
        let h = run twoLandings
        let events = h.Steps |> List.collect (fun s -> s.Events)
        events
        |> List.filter (function CohortEvent.LandingLanded _ -> true | _ -> false)
        |> List.length
        |> Expect.equal "two landings landed" 2
        landedOrderRespectsRequestOrder h |> Expect.isTrue "they landed in the order they were requested"
        landedImpliesClaimsHeldAtLandTime h |> Expect.isTrue "each landed with its claims held by the requester"
        landingTransitionsFollowTheGraph h |> Expect.isTrue "each followed the declared graph"
        let! agrees = ownerAgreesWithReplay (InMemory.create<MemberId> ()) twoLandings
        agrees |> Expect.isTrue "the live owner holds what replay of its ledger holds"
      }

      testCase "30 (teeth): property 13 fails a landing order that runs backwards and a landing the queue never saw" <| fun _ ->
        let first = LandingId "l-first"
        let second = LandingId "l-second"
        let history (events: CohortEvent<Agent> list) =
          let step : Step =
            { Before = CohortState.empty (); After = CohortState.empty (); Command = CohortCommand.Tick machine
              Events = events; StepClock = epoch; StepEntropy = [||] }
          { initHarness () with Steps = [ step ] }
        let queued = [ CohortEvent.LandingQueued(first, agentOf 1); CohortEvent.LandingQueued(second, agentOf 1) ]
        history (queued @ [ CohortEvent.LandingLanded(first, "a"); CohortEvent.LandingLanded(second, "b") ])
        |> landedOrderRespectsRequestOrder
        |> Expect.isTrue "in request order is accepted"
        history (queued @ [ CohortEvent.LandingLanded(second, "b"); CohortEvent.LandingLanded(first, "a") ])
        |> landedOrderRespectsRequestOrder
        |> Expect.isFalse "the second landing landing first is refused"
        history (queued @ [ CohortEvent.LandingLanded(LandingId "l-never-queued", "c") ])
        |> landedOrderRespectsRequestOrder
        |> Expect.isFalse "a landing that was never queued is refused, not dropped"

      // `CohortCommand` has 21 cases and this suite's generator used to emit 17 of them; the four it did not
      // (VerificationInconclusive, FastForwardFailed, SetIntegrationHead, OpenCohort) were never exercised by a
      // property, and no test would have said so. Reflecting over the DU makes a new case fail here until a generator
      // issues it, and "issues it" means `decide` ACCEPTED it, not that an intent was merely written.
      testCase "31: every CohortCommand case is accepted by decide in some generated history, and a command for another cohort is refused WrongCohortScope" <| fun _ ->
        let caseOf (cmd: CohortCommand<Agent>) = (fst (FSharpValue.GetUnionFields(cmd, typeof<CohortCommand<Agent>>))).Name
        let allCases = FSharpType.GetUnionCases(typeof<CohortCommand<Agent>>) |> Array.map (fun c -> c.Name) |> Set.ofArray
        let pipeline = Gen.sample 600 (Arb.toGen (PipelineGenerators.History())) |> Array.map (fun (PipelineHistory intents) -> run intents)
        let plain = Gen.sample 600 genIntents |> Array.map (fun intents -> run (seating @ intents))
        let delegating = Gen.sample 300 (Arb.toGen (DelegatingGenerators.History())) |> Array.map (fun (DelegatingHistory intents) -> run intents)
        // The deterministic history LAST, so the corpus is the generated one plus the one path a
        // random draw cannot be relied on to reach (`reassignHistory`).
        let histories = Array.concat [ pipeline; plain; delegating; [| run reassignHistory |] ]
        let accepted = histories |> Array.collect (fun h -> h.Steps |> List.map (fun s -> caseOf s.Command) |> Array.ofList) |> Set.ofArray
        // The deterministic history earns its place or it is not there. If `decide` ever stops
        // accepting this path, the corpus falls back on the draw and the claim below becomes a
        // coin toss again, so this says so NOW, every run, instead of leaving it to a flake.
        run reassignHistory
        |> fun h -> h.Steps |> List.map (fun s -> caseOf s.Command)
        |> List.contains "ReassignClaim"
        |> Expect.isTrue "the deterministic history really reassigns a claim, so the coverage does not rest on the draw"
        Set.difference allCases accepted
        |> Set.toList
        |> Expect.equal "every CohortCommand case is accepted in some generated history; the ones listed never were" []
        histories
        |> Array.exists (fun h ->
          h.Attempts |> List.exists (fun a -> match a.Verdict with Result.Error(CohortError.WrongCohortScope _) -> true | _ -> false))
        |> Expect.isTrue "a command addressed to another cohort was generated and refused WrongCohortScope"
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

      testPropertyWithConfig raceConfig "19: ReassignClaim is refused for a non-conductor and succeeds for the conductor" <| fun (scope: ClaimScope) ->
        let conductor = agentOf 0
        let other = agentOf 1
        let target = agentOf 2
        let h0 = initHarness ()
        let h1 = applyCommand h0 (CohortCommand.Join(conductor, JoinableRole.Implementer, None, machine)) // first joiner: becomes conductor
        let h2 = applyCommand h1 (CohortCommand.Join(other, JoinableRole.Implementer, None, machine))
        let h3 = applyCommand h2 (CohortCommand.Join(target, JoinableRole.Implementer, None, machine))
        let h4 = applyCommand h3 (CohortCommand.AcquireClaim(other, scope, "purpose", machine))
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
        let ledgers = corpusLedgers ()

        ledgers.Length > 0
        |> Expect.isTrue "at least one recorded ledger corpus file is checked in (item 18a's fixture)"

        ledgers
        |> List.iter (fun (file, entries) ->
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
          |> ignore

          // The same ledger through the audited replay: nothing dropped, and no event it cannot reproduce. A corpus
          // ledger is the one place a change to `leaseWindow` or `settledRetention` must be noticed by name.
          let audit = replayAuditIn CohortScope.Machine entries
          audit.Dropped |> List.map (fun d -> int64 d.DroppedSeq) |> Expect.equal (sprintf "%s: no row is dropped" file) []
          audit.Diverged |> List.map (fun d -> int64 d.DivergedSeq) |> Expect.equal (sprintf "%s: no row diverges" file) [])
      }

      // The corpus used to be four lines in one file and exercised 4 of the 21 `CohortCommand` cases. This fails the
      // moment a case is added to the DU without a recorded ledger that issues it, so the corpus cannot quietly
      // fall behind the type again. The ledgers are generated by folding commands through the real `decide`.
      test "every CohortCommand case is issued by at least one recorded ledger" {
        let caseOf (cmd: CohortCommand<MemberId>) = (fst (FSharpValue.GetUnionFields(cmd, typeof<CohortCommand<MemberId>>))).Name
        let allCases = FSharpType.GetUnionCases(typeof<CohortCommand<MemberId>>) |> Array.map (fun c -> c.Name) |> Set.ofArray
        let recorded =
          corpusLedgers ()
          |> List.collect (fun (_, entries) -> entries |> List.map (fun e -> caseOf e.Command))
          |> Set.ofList
        Set.difference allCases recorded
        |> Set.toList
        |> Expect.equal "every CohortCommand case appears in a corpus ledger; the ones listed do not" []
      }

      test "the corpus pins the policy constants: it records a lease lapse and a retention sweep" {
        // `leaseWindow` and `settledRetention` are not stored in the ledger, so the events a `Tick` recorded are the
        // only witness. If the corpus stopped containing a departure and a purge, changing either constant would
        // pass this directory silently.
        let events = corpusLedgers () |> List.collect (fun (_, entries) -> entries |> List.collect (fun e -> e.Events))
        events
        |> List.exists (function CohortEvent.MemberDeparted _ -> true | _ -> false)
        |> Expect.isTrue "a recorded Tick departed a member whose lease lapsed"
        events
        |> List.exists (function CohortEvent.MemberPurged _ -> true | _ -> false)
        |> Expect.isTrue "a recorded Tick purged settled history after the retention window"
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
