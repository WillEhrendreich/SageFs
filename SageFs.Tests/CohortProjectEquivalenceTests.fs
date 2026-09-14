/// Output-identical proof for the `Cohort.project` perf optimization
/// (CohortPerfBudgetTests.fs's Phase-0 #1 finding: `decide`+`project` measured
/// p50≈77ms/p99≈84ms at 10 members / 7,000 tests, ~80x over the <1ms p99
/// budget — see that file's header for the profiling notes pointing at
/// `bitmapFor`'s per-session `Set.ofList` tree builds).
///
/// `Reference.project` below is a frozen, verbatim copy of `Cohort.project`
/// exactly as it read before the optimization (the `Set.ofList` /
/// `Array.tryFindIndex` version) — the ORACLE. It is never replaced by a call
/// into `Cohort.project` itself, so a future accidental rewrite of the real
/// implementation cannot silently become its own oracle. This property
/// asserts `Cohort.project` produces a `CohortFrame` STRUCTURALLY EQUAL to
/// `Reference.project` for every generated `(CohortState-shaped LedgerHead,
/// SessionSnapshot[])` pair — the hard constraint the perf task states:
/// `project`'s output feeds the dashboard cockpit (PNG matrix, territory,
/// lanes), the MCP `cohort://status` resource, and SSE rows, so the optimized
/// internals (dictionary-indexed bitmaps instead of balanced-tree `Set`s) must
/// never drift from what every consumer actually observes.
///
/// States/snapshots here are built DIRECTLY (never through `decide`/`replay`),
/// because `project` only ever reads `Members`/`Claims`/`Conductor`/
/// `Landings`/`Queue`/`IntegrationHead` off the state — generating them
/// straight lets the property probe shapes `decide` itself would never
/// construct (a claim held by a member who isn't present in `Members`,
/// overlapping test outcomes across Pass/Fail/Stale for the same session, an
/// empty test pool, a landing whose requester isn't a current member) that
/// `project` still has to render without throwing or diverging from the
/// oracle.
module SageFs.Tests.CohortProjectEquivalenceTests

open System
open System.Collections.Generic
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.Tests.SharedGenerators

// ── The oracle ──────────────────────────────────────────────────────────

module private Reference =
  /// Verbatim copy of `Cohort.project` as it existed before the perf
  /// optimization — do not "fix" this to match a future `Cohort.project`;
  /// its entire job is to stay the pre-optimization behavior.
  ///
  /// The landing/`IntegrationHead` projection (added alongside
  /// `CohortFrame.Landings` to close the dogfood-surfaced gap) was NEVER
  /// part of the perf-optimized bitmap rewrite this oracle guards — there is
  /// no "pre-optimization" landing behavior to freeze. It is duplicated here
  /// verbatim (same code as `Cohort.project`, not re-derived) so the property
  /// below stays a real equivalence proof over the WHOLE frame, including the
  /// new fields, rather than silently going blind to them.
  let project (head: LedgerHead<'m>) (snapshots: SessionSnapshot<'m>[]) : CohortFrame<'m> =
    let members = head.State.Members |> Map.toArray
    let memberIds = members |> Array.map fst
    let memberIndexOf who = memberIds |> Array.tryFindIndex ((=) who) |> Option.defaultValue -1

    let claims = head.State.Claims |> Map.toArray

    let landings =
      head.State.Landings
      |> Map.toArray
      |> Array.sortBy (fun (LandingId lid, _) -> lid)
    let queuePosition =
      let positions = Dictionary<LandingId, int>(head.State.Queue.Length)
      head.State.Queue
      |> List.iteri (fun i lid -> if not (positions.ContainsKey lid) then positions.[lid] <- i)
      landings |> Array.map (fun (lid, _) -> match positions.TryGetValue lid with true, i -> i | false, _ -> -1)

    let testIds =
      snapshots
      |> Array.collect (fun s -> (s.PassingTests @ s.FailingTests @ s.StaleTests) |> List.toArray)
      |> Array.distinct
      |> Array.sortBy (fun (TestId t) -> t)

    let bitmapFor (pick: SessionSnapshot<'m> -> TestId list) =
      snapshots
      |> Array.map (fun s ->
        let set = pick s |> Set.ofList
        testIds |> Array.map set.Contains)

    {
      Version = head.Seq
      SessionGens = snapshots |> Array.map (fun s -> s.Generation)
      Dirty = FrameRegions.Members ||| FrameRegions.Claims ||| FrameRegions.Matrix ||| FrameRegions.Landings
      Conductor = head.State.Conductor
      MemberIds = memberIds
      MemberRole = members |> Array.map (fun (_, r) -> r.Role)
      MemberSeat =
        members
        |> Array.map (fun (_, r) ->
          match r.Presence with
          | MemberPresence.Present -> SeatState.Present
          | MemberPresence.Departed since -> SeatState.Departed since)
      ClaimIds = claims |> Array.map fst
      ClaimScope = claims |> Array.map (fun (_, c) -> c.Scope)
      ClaimHolderIndex =
        claims
        |> Array.map (fun (_, c) ->
          match c.State with
          | ClaimState.Held holder -> memberIndexOf holder
          | _ -> -1)
      ClaimFence = claims |> Array.map (fun (_, c) -> c.Fence)
      ClaimState = claims |> Array.map (fun (_, c) -> c.State)
      TestIds = testIds
      Pass = bitmapFor (fun s -> s.PassingTests)
      Fail = bitmapFor (fun s -> s.FailingTests)
      Stale = bitmapFor (fun s -> s.StaleTests)
      IntegrationHead = head.State.IntegrationHead
      LandingIds = landings |> Array.map fst
      LandingRequesterIndex = landings |> Array.map (fun (_, l) -> memberIndexOf l.Requester)
      LandingStatement = landings |> Array.map (fun (_, l) -> l.Statement)
      LandingCommits = landings |> Array.map (fun (_, l) -> l.Commits |> List.toArray)
      LandingState = landings |> Array.map (fun (_, l) -> l.State)
      LandingQueuePosition = queuePosition
    }

// ── Generators — `'m = int`, built directly (never via `decide`) ──────────

let private genRole =
  Gen.elements [ JoinableRole.Implementer; JoinableRole.Verifier; JoinableRole.Observer ]

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

let private genPresence =
  Gen.oneof [ Gen.constant MemberPresence.Present; Gen.constant (MemberPresence.Departed epoch) ]

let private genMemberRecord =
  gen {
    let! role = genRole
    let! presence = genPresence
    let! session = Gen.oneof [ Gen.constant None; Gen.constant (Some "sess-1") ]
    return { Role = role; Presence = presence; LastRenewal = epoch; Session = session }
  }

let private genMembers =
  gen {
    let! ids = Gen.listOf (Gen.choose (1, 12)) |> Gen.map List.distinct
    let! records = Gen.listOfLength ids.Length genMemberRecord
    return List.zip ids records |> Map.ofList
  }

let private scopePool =
  [| ClaimScope.File "SageFs.Core/A.fs"
     ClaimScope.File "SageFs.Core/B.fs"
     ClaimScope.Project "SageFs.Core/SageFs.Core.fsproj" |]

let private genClaimScope = Gen.elements scopePool

let private genClaimState =
  gen {
    let! holder = Gen.choose (1, 12)
    return! Gen.elements [ ClaimState.Held holder; ClaimState.Orphaned(holder, epoch); ClaimState.Released(holder, epoch) ]
  }

let private genPurpose =
  match Purpose.tryCreate "working on it" with
  | Ok p -> Gen.constant p
  | Error e -> failwithf "test bug: %s" e

let private genClaim =
  gen {
    let! idNum = Gen.choose (0, 20)
    let! fenceN = Gen.choose (0, 1000)
    let! scope = genClaimScope
    let! state = genClaimState
    let! purpose = genPurpose
    let cid = ClaimId(sprintf "c-%d" idNum)
    return
      cid,
      { Id = cid
        Fence = int64 fenceN * 1L<fence>
        Scope = scope
        Purpose = purpose
        Since = epoch
        State = state }
  }

let private genClaims =
  gen {
    let! entries = Gen.listOf genClaim
    return entries |> List.distinctBy fst |> Map.ofList
  }

let private genConductor = Gen.oneof [ Gen.constant None; Gen.choose (1, 12) |> Gen.map Some ]

let private genIntegrationHead = Gen.elements [ nullSha; "sha-onto"; "sha-rebased"; "sha-other" ]

/// A small, overlap-heavy test-id pool — the same names deliberately recur
/// across Pass/Fail/Stale and across sessions, exercising `Array.distinct`'s
/// dedup and "a test appears in more than one category for the same session"
/// (which both the oracle's `Set.ofList` and the dictionary-indexed rewrite
/// must resolve identically: the bit simply ends up set in both bitplanes).
/// Shared with the landing generators below (`FailingTests`) so a generated
/// `LandingBlocker.FailingTests` can reference the same ids a session
/// snapshot might report as failing.
let private testPool = [| "t1"; "t2"; "t3"; "t4"; "t5" |] |> Array.map TestId

// ── Landing generators (exercises the ADDITIVE Landings/IntegrationHead
//    projection — never part of the frozen pre-optimization oracle, so this
//    is the only place that shape gets test coverage in this file) ────────

let private genLandingBlocker =
  gen {
    let! kind = Gen.choose (0, 4)
    match kind with
    | 0 ->
      let! files = Gen.listOf (Gen.elements [ "a.fs"; "b.fs" ])
      return LandingBlocker.RebaseConflict files
    | 1 ->
      let! tests = Gen.listOf (Gen.elements testPool)
      return LandingBlocker.FailingTests tests
    | 2 ->
      let! n = Gen.choose (0, 20)
      return LandingBlocker.StaleClaimFence(ClaimId(sprintf "c-%d" n))
    | 3 -> return LandingBlocker.HeadMoved("sha-a", "sha-b")
    | _ ->
      let! who = Gen.choose (1, 12)
      return LandingBlocker.VetoedBy(who, "reason")
  }

let private genNextAction =
  Gen.elements [
    NextAction.RebaseAndResubmit
    NextAction.AwaitConductor
    NextAction.FixTests [ TestId "t1" ]
    NextAction.Withdraw
  ]

let private genLandingState =
  gen {
    let! kind = Gen.choose (0, 5)
    match kind with
    | 0 -> return LandingState.Queued
    | 1 -> return LandingState.Rebasing "sha-onto"
    | 2 ->
      let! affected = Gen.choose (0, 10)
      let! running = Gen.choose (0, 10)
      return LandingState.Verifying("sha-base", "sha-rebased", affected, running)
    | 3 ->
      let! blocker = genLandingBlocker
      let! nextAction = genNextAction
      return LandingState.Blocked(blocker, nextAction)
    | 4 -> return LandingState.Landed "sha-landed"
    | _ -> return LandingState.Withdrawn
  }

let private genStatement =
  match Statement.tryCreate "land this please" with
  | Ok s -> Gen.constant s
  | Error e -> failwithf "test bug: %s" e

let private genLandingClaimRef =
  gen {
    let! n = Gen.choose (0, 20)
    let! fenceN = Gen.choose (0, 1000)
    return ClaimId(sprintf "c-%d" n), int64 fenceN * 1L<fence>
  }

let private genLandingRequest : Gen<LandingId * LandingRequest<int>> =
  gen {
    let! idNum = Gen.choose (0, 20)
    let! requester = Gen.choose (1, 12)
    let! claimRefs = Gen.listOf genLandingClaimRef
    let! commits = Gen.listOf (Gen.elements [ "sha-c1"; "sha-c2"; "sha-c3" ])
    let! statement = genStatement
    let! state = genLandingState
    let lid = LandingId(sprintf "l-%d" idNum)
    return
      lid,
      { Id = lid
        Requester = requester
        Claims = claimRefs
        Commits = commits
        BaseAtQueue = "sha-base-at-queue"
        Statement = statement
        State = state }
  }

let private genLandings : Gen<Map<LandingId, LandingRequest<int>>> =
  gen {
    let! entries = Gen.listOf genLandingRequest
    return entries |> List.distinctBy fst |> Map.ofList
  }

/// A queue that is a (possibly partial, possibly reordered) permutation of
/// the generated landings' ids — including ids repeated is impossible
/// (`Array.distinct`), and the queue may be shorter than the full landing
/// set (the FIFO discipline `advanceQueue` maintains never requires every
/// landing to be queued — Landed/Withdrawn ones are popped).
let private genQueueFor (landingIds: LandingId[]) : Gen<LandingId list> =
  gen {
    let! shuffled = Gen.shuffle landingIds
    let! takeN = Gen.choose (0, landingIds.Length)
    return shuffled |> Array.toList |> List.truncate takeN
  }

let private genState =
  gen {
    let! members = genMembers
    let! claims = genClaims
    let! conductor = genConductor
    let! integrationHead = genIntegrationHead
    let! landings = genLandings
    let! queue = genQueueFor (landings |> Map.toArray |> Array.map fst)
    return
      { CohortState.empty () with
          Members = members
          Claims = claims
          Conductor = conductor
          IntegrationHead = integrationHead
          Landings = landings
          Queue = queue }
  }

let private genLedgerSeq = Gen.choose (0, 100_000) |> Gen.map (fun n -> int64 n * 1L<ledgerSeq>)

let private genHead : Gen<LedgerHead<int>> =
  gen {
    let! state = genState
    let! sq = genLedgerSeq
    return { Seq = sq; State = state }
  }

let private genTestIdList = Gen.listOf (Gen.elements testPool)

let private genSnapshot : Gen<SessionSnapshot<int>> =
  gen {
    let! memberOpt = Gen.oneof [ Gen.constant None; Gen.choose (1, 12) |> Gen.map Some ]
    let! sid = Gen.elements [ "s1"; "s2"; "s3" ]
    let! genr = Gen.choose (0, 100) |> Gen.map int64
    let! pass = genTestIdList
    let! fail = genTestIdList
    let! stale = genTestIdList
    return
      { Member = memberOpt
        SessionId = sid
        Generation = genr
        PassingTests = pass
        FailingTests = fail
        StaleTests = stale }
  }

let private genSnapshots : Gen<SessionSnapshot<int>[]> = Gen.listOf genSnapshot |> Gen.map List.toArray

type private ProjectGenerators =
  static member LedgerHead() : Arbitrary<LedgerHead<int>> = Arb.fromGen genHead
  static member Snapshots() : Arbitrary<SessionSnapshot<int>[]> = Arb.fromGen genSnapshots

let private config = { propConfig with arbitrary = [ typeof<ProjectGenerators> ] }

/// Regression guard for the additive Landings/IntegrationHead projection
/// (dogfood gap closure): adding landing state to a `CohortState` must never
/// change any of the pre-existing frame fields. Compares `project` over the
/// generated (possibly landing-bearing) head against `project` over the same
/// head with `Landings`/`Queue` stripped back to empty — every field except
/// the landing/IntegrationHead ones must agree. (`IntegrationHead` is
/// deliberately excluded from this comparison: it is real `CohortState`
/// data, generated independently of `Landings`/`Queue` by `genIntegrationHead`,
/// so it is expected — and correct — to differ from the empty-landings
/// baseline exactly when the generator picked two different values, not a
/// hidden interaction with landings.)
let private stripLandings (head: LedgerHead<'m>) : LedgerHead<'m> =
  { head with State = { head.State with Landings = Map.empty; Queue = [] } }

[<Tests>]
let cohortProjectEquivalenceTests =
  testList "Cohort.project output-identical proof" [
    testPropertyWithConfig config "project matches the pre-optimization reference implementation" <|
      fun (head: LedgerHead<int>) (snapshots: SessionSnapshot<int>[]) ->
        Cohort.project head snapshots = Reference.project head snapshots

    testCase "WHY — a test appearing in more than one category for one session sets the bit in every matching bitplane, matching the oracle" <| fun _ ->
      let state = { CohortState.empty () with Members = Map.ofList [ 1, { Role = JoinableRole.Implementer; Presence = MemberPresence.Present; LastRenewal = epoch; Session = None } ] }
      let head = { Seq = 3L<ledgerSeq>; State = state }
      let snapshots =
        [| { Member = Some 1; SessionId = "s1"; Generation = 0L
             PassingTests = [ TestId "dup" ]; FailingTests = [ TestId "dup" ]; StaleTests = [] } |]
      Cohort.project head snapshots
      |> Expect.equal "should match the frozen oracle byte-for-byte" (Reference.project head snapshots)

    testPropertyWithConfig config "adding landings never changes the pre-existing (non-landing) frame fields" <|
      fun (head: LedgerHead<int>) (snapshots: SessionSnapshot<int>[]) ->
        let withLandings = Cohort.project head snapshots
        let withoutLandings = Cohort.project (stripLandings head) snapshots
        withLandings.Version = withoutLandings.Version
        && withLandings.SessionGens = withoutLandings.SessionGens
        && withLandings.Conductor = withoutLandings.Conductor
        && withLandings.MemberIds = withoutLandings.MemberIds
        && withLandings.MemberRole = withoutLandings.MemberRole
        && withLandings.MemberSeat = withoutLandings.MemberSeat
        && withLandings.ClaimIds = withoutLandings.ClaimIds
        && withLandings.ClaimScope = withoutLandings.ClaimScope
        && withLandings.ClaimHolderIndex = withoutLandings.ClaimHolderIndex
        && withLandings.ClaimFence = withoutLandings.ClaimFence
        && withLandings.ClaimState = withoutLandings.ClaimState
        && withLandings.TestIds = withoutLandings.TestIds
        && withLandings.Pass = withoutLandings.Pass
        && withLandings.Fail = withoutLandings.Fail
        && withLandings.Stale = withoutLandings.Stale

    testCase "empty landings/queue project to empty landing columns, stably" <| fun _ ->
      let state = { CohortState.empty () with Members = Map.ofList [ 1, { Role = JoinableRole.Implementer; Presence = MemberPresence.Present; LastRenewal = epoch; Session = None } ] }
      let head = { Seq = 0L<ledgerSeq>; State = state }
      let frame = Cohort.project head [||]
      frame.LandingIds |> Expect.isEmpty "no landings means an empty LandingIds column"
      frame.LandingRequesterIndex |> Expect.isEmpty "no landings means an empty requester-index column"
      frame.LandingState |> Expect.isEmpty "no landings means an empty state column"
      frame.LandingQueuePosition |> Expect.isEmpty "no landings means an empty queue-position column"
      frame.IntegrationHead |> Expect.equal "an empty state's IntegrationHead is the null sha sentinel" Cohort.nullSha
  ]
