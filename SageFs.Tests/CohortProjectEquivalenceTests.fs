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
/// because `project` only ever reads `Members`/`Claims`/`Conductor` off the
/// state — generating them straight lets the property probe shapes `decide`
/// itself would never construct (a claim held by a member who isn't present
/// in `Members`, overlapping test outcomes across Pass/Fail/Stale for the same
/// session, an empty test pool) that `project` still has to render without
/// throwing or diverging from the oracle.
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
  let project (head: LedgerHead<'m>) (snapshots: SessionSnapshot<'m>[]) : CohortFrame<'m> =
    let members = head.State.Members |> Map.toArray
    let memberIds = members |> Array.map fst
    let memberIndexOf who = memberIds |> Array.tryFindIndex ((=) who) |> Option.defaultValue -1

    let claims = head.State.Claims |> Map.toArray

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
      Dirty = FrameRegions.Members ||| FrameRegions.Claims ||| FrameRegions.Matrix
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

let private genState =
  gen {
    let! members = genMembers
    let! claims = genClaims
    let! conductor = genConductor
    return { CohortState.empty () with Members = members; Claims = claims; Conductor = conductor }
  }

let private genLedgerSeq = Gen.choose (0, 100_000) |> Gen.map (fun n -> int64 n * 1L<ledgerSeq>)

let private genHead : Gen<LedgerHead<int>> =
  gen {
    let! state = genState
    let! sq = genLedgerSeq
    return { Seq = sq; State = state }
  }

/// A small, overlap-heavy test-id pool — the same names deliberately recur
/// across Pass/Fail/Stale and across sessions, exercising `Array.distinct`'s
/// dedup and "a test appears in more than one category for the same session"
/// (which both the oracle's `Set.ofList` and the dictionary-indexed rewrite
/// must resolve identically: the bit simply ends up set in both bitplanes).
let private testPool = [| "t1"; "t2"; "t3"; "t4"; "t5" |] |> Array.map TestId

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
  ]
