/// Phase 2 item 16 of sagefs-multiagent-vision.md (§6.5 "the lane view from
/// `LaneEvents` with span flames from `CohortFrame.Spans`"): pure unit tests
/// for `CohortLanes` — no daemon, no FSI, no I/O, driven entirely through
/// `Cohort.decide` to build a real ledger, the same "pure core, no IO"
/// discipline `CohortTerritoryTests.fs`/`CohortOwnerTests.fs` use.
///
/// `Cohort.CohortFrame<'m>` has no `Spans`/`LaneEvents` field (confirmed by
/// reading `Cohort.fs`'s own module-level scope note before writing this
/// island) — these tests exercise `CohortLanes.project`, which builds the
/// lane/span model from the ledger's `LedgerEntry.Clock`/`Events` instead.
module SageFs.Tests.CohortLanesTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.Features
open SageFs.Features.CohortLanes

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private atSec (n: int) : DateTime = epoch.AddSeconds(float n)
let private alice = "alice"
let private bob = "bob"

/// Folds `decide` over a fixed (clock, command) list from an empty state,
/// recording one dense `LedgerEntry` per accepted command — exactly what
/// `CohortOwner`/`CohortLedgerSqlite` do in production, just without the
/// actor/SQLite plumbing. Distinct per-command entropy (`[| byte i |]`)
/// mirrors `CohortTerritoryTests.fs`'s `frameAfter` so claims minted in the
/// same ledger get distinct `ClaimId`s (id minting collapses to `"c-0"` for
/// every call when given empty entropy — `Cohort.fs`'s `Ids.mintClaimId`).
let private ledgerFrom (commandsWithClocks: (DateTime * CohortCommand<string>) list) : LedgerEntry<string> list =
  let _, _, entriesRev =
    commandsWithClocks
    |> List.mapi (fun i (clock, cmd) -> i, clock, cmd)
    |> List.fold
      (fun (state, seq, entriesRev) (i, clock, cmd) ->
        match decide clock [| byte i |] state cmd with
        | Ok(newState, events, _) ->
          let seq' = seq + 1L<ledgerSeq>
          newState, seq', { Seq = seq'; Clock = clock; Entropy = [| byte i |]; Command = cmd; Events = events } :: entriesRev
        | Error err -> failwithf "unexpected refusal building test ledger: %A" err)
      (CohortState.empty (), 0L<ledgerSeq>, [])
  entriesRev |> List.rev

let private mintedClaimId (ledger: LedgerEntry<string> list) : ClaimId * int64<fence> =
  ledger
  |> List.pick (fun e ->
    e.Events
    |> List.tryPick (function
      | CohortEvent.ClaimAcquired(cid, _, _, fence) -> Some(cid, fence)
      | _ -> None))

let private mintedLandingId (ledger: LedgerEntry<string> list) : LandingId =
  ledger
  |> List.pick (fun e ->
    e.Events
    |> List.tryPick (function
      | CohortEvent.LandingQueued(lid, _) -> Some lid
      | _ -> None))

let private labelMember (m: string option) : string = m |> Option.defaultValue "integration"

let private laneFor (member_: string option) (model: LaneModel<string>) : Lane<string> =
  model.Lanes
  |> List.tryFind (fun l -> l.Member = member_)
  |> Option.defaultWith (fun () -> failwithf "no lane for %A in %A" member_ (model.Lanes |> List.map (fun l -> l.Member)))

[<Tests>]
let cohortLanesTests =
  testList "CohortLanes" [

    testList "project" [

      testCase "an empty ledger projects a stable, empty lane model" <| fun _ ->
        let model = project []
        model.Lanes |> Expect.equal "no ledger, no lanes" []
        model.RangeStart |> Expect.equal "stable zero range start" DateTime.MinValue
        model.RangeEnd |> Expect.equal "stable zero range end" DateTime.MinValue

      testCase "a held-then-released claim is one Succeeded span on the holder's lane" <| fun _ ->
        let probe =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
          ]
        let claimId, fence = mintedClaimId probe
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
            atSec 5, CohortCommand.ReleaseClaim(alice, claimId, fence)
          ]
        let model = project ledger
        model.Lanes |> List.length |> Expect.equal "the integration lane plus alice's lane" 2
        let aliceLane = model |> laneFor (Some alice)
        aliceLane.Spans |> List.length |> Expect.equal "one claim span" 1
        let span = aliceLane.Spans.[0]
        span.Start |> Expect.equal "starts at acquire" (atSec 1)
        span.End |> Expect.equal "ends at release" (Some(atSec 5))
        span.Outcome |> Expect.equal "a release is a success" SpanOutcome.Succeeded
        span.Track |> Expect.equal "a single span always lands on track 0" 0
        span.Kind |> Expect.equal "carries the claimed scope" (SpanKind.Claim(ClaimScope.File "src/Foo.fs"))
        span.Label |> Expect.equal "labeled with the claimed path" "src/Foo.fs"

      testCase "a claim never released stays Open with no End" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
          ]
        let model = project ledger
        let aliceLane = model |> laneFor (Some alice)
        aliceLane.Spans |> List.length |> Expect.equal "one open claim" 1
        let span = aliceLane.Spans.[0]
        span.Outcome |> Expect.equal "still held == open" SpanOutcome.Open
        span.End |> Expect.equal "no end recorded — never a synthesized wall-clock end" None

      testCase "an orphaned claim closes as a Failed span" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
            atSec 9, CohortCommand.Depart alice
          ]
        let model = project ledger
        let aliceLane = model |> laneFor (Some alice)
        let span = aliceLane.Spans.[0]
        span.Outcome |> Expect.equal "orphaned (departed while holding) is a failure" SpanOutcome.Failed
        span.End |> Expect.equal "closes at the departure clock" (Some(atSec 9))

      testCase "reassigning an orphaned claim opens a fresh Open span for the new holder, on top of the old holder's Failed span" <| fun _ ->
        // `decide` only ever reassigns an ORPHANED claim (Cohort.fs:627,
        // `ClaimNotOrphaned` otherwise) — alice must join as the cohort's
        // first joiner (so she is Conductor, the only one who may issue
        // ReassignClaim) and depart before bob can be reassigned her claim.
        // The probe replays the SAME command list, at the SAME positional
        // indices, as the full ledger below — `ledgerFrom` mints ids from
        // the command's own index (`[| byte i |]`), so the claim id must be
        // read off a ledger where `AcquireClaim` sits at the identical index
        // or the mint disagrees with what the full ledger actually produced.
        let prefix =
          [ atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.Join(bob, JoinableRole.Implementer, None)
            atSec 2, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing") ]
        let claimId, _fence = mintedClaimId (ledgerFrom prefix)
        let ledger =
          ledgerFrom (
            prefix
            @ [ atSec 3, CohortCommand.Depart alice
                atSec 4, CohortCommand.ReassignClaim(alice, claimId, bob) ]
          )
        let model = project ledger
        let aliceLane = model |> laneFor (Some alice)
        aliceLane.Spans |> List.length |> Expect.equal "alice's span was already closed by the orphan" 1
        aliceLane.Spans.[0].Outcome |> Expect.equal "orphaning closes it as Failed" SpanOutcome.Failed
        aliceLane.Spans.[0].End |> Expect.equal "closes at the departure clock" (Some(atSec 3))
        let bobLane = model |> laneFor (Some bob)
        bobLane.Spans |> List.length |> Expect.equal "bob gets a fresh span from the reassignment" 1
        let bobSpan = bobLane.Spans.[0]
        bobSpan.Start |> Expect.equal "starts at the reassignment clock, not the original acquire" (atSec 4)
        bobSpan.Outcome |> Expect.equal "still held — open" SpanOutcome.Open
        bobSpan.Kind |> Expect.equal "the same claimed scope, carried across the reassignment" (SpanKind.Claim(ClaimScope.File "src/Foo.fs"))

      testCase "two concurrently held claims by the same member stack onto different tracks" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
            atSec 2, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Bar.fs", "editing")
          ]
        let model = project ledger
        let aliceLane = model |> laneFor (Some alice)
        aliceLane.Spans |> List.length |> Expect.equal "two open claims" 2
        aliceLane.Spans
        |> List.map (fun s -> s.Track)
        |> List.distinct
        |> List.length
        |> Expect.equal "overlapping-in-time spans never share a track" 2

      testCase "spans on the same track never overlap in time (non-overlapping-in-time claims reuse a track)" <| fun _ ->
        let probe =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
          ]
        let claimId, fence = mintedClaimId probe
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
            atSec 5, CohortCommand.ReleaseClaim(alice, claimId, fence)
            atSec 6, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Bar.fs", "editing")
          ]
        let model = project ledger
        let aliceLane = model |> laneFor (Some alice)
        aliceLane.Spans |> List.length |> Expect.equal "two sequential (non-overlapping) claims" 2
        aliceLane.Spans
        |> List.forall (fun s -> s.Track = 0)
        |> Expect.isTrue "a released-before-the-next-starts claim can reuse track 0"

      testCase "a landing spans the integration lane from queued to landed" <| fun _ ->
        let probe =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.RequestLanding(alice, [], [ "sha1" ], "land it")
          ]
        let landingId = mintedLandingId probe
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.RequestLanding(alice, [], [ "sha1" ], "land it")
            atSec 2, CohortCommand.RebaseCompleted(landingId, Ok "rebased-sha")
            atSec 3, CohortCommand.AffectedComputed(landingId, [])
            atSec 4, CohortCommand.TestsCompleted(landingId, [])
            atSec 8, CohortCommand.FastForwardCompleted(landingId, "committed-sha")
          ]
        let model = project ledger
        model.Lanes |> List.length |> Expect.equal "integration lane + alice's lane (she never held a claim, but she joined)" 2
        let integrationLane = model |> laneFor None
        integrationLane.Spans |> List.length |> Expect.equal "one landing span" 1
        let span = integrationLane.Spans.[0]
        span.Kind |> Expect.equal "a landing, not a claim" SpanKind.Landing
        span.Start |> Expect.equal "starts at queue time" (atSec 1)
        span.End |> Expect.equal "ends when it lands" (Some(atSec 8))
        span.Outcome |> Expect.equal "landed is a success" SpanOutcome.Succeeded

      testCase "a withdrawn landing closes as a Failed integration-lane span" <| fun _ ->
        let probe =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.RequestLanding(alice, [], [ "sha1" ], "land it")
          ]
        let landingId = mintedLandingId probe
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.RequestLanding(alice, [], [ "sha1" ], "land it")
            atSec 3, CohortCommand.WithdrawLanding(alice, landingId)
          ]
        let model = project ledger
        let integrationLane = model |> laneFor None
        integrationLane.Spans.[0].Outcome |> Expect.equal "withdrawn is a failure, not silently dropped" SpanOutcome.Failed

      testCase "member lanes appear in first-join order, integration lane always first" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(bob, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.Join(alice, JoinableRole.Implementer, None)
          ]
        let model = project ledger
        model.Lanes |> List.map (fun l -> l.Member)
        |> Expect.equal "integration first, then join order (bob before alice)" [ None; Some bob; Some alice ]

      testCase "projecting the same ledger twice is structurally identical (determinism)" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
            atSec 2, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Bar.fs", "editing")
          ]
        project ledger |> Expect.equal "same ledger, same model, every time" (project ledger)
    ]

    testList "toSvg" [

      testCase "an empty lane model still renders a valid, well-formed svg" <| fun _ ->
        let svg = toSvg labelMember 320.0 16.0 (project [])
        svg.StartsWith "<svg" |> Expect.isTrue "opens with an svg tag"
        svg.EndsWith "</svg>" |> Expect.isTrue "closes the svg tag"

      testCase "a claim path containing XSS-shaped characters is escaped, never raw, in the rendered markup" <| fun _ ->
        let dangerous = "src/<script>alert('x')&\".fs"
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File dangerous, "editing")
          ]
        let svg = toSvg labelMember 320.0 16.0 (project ledger)
        (svg.Contains "<script>") |> Expect.isFalse "the raw tag never appears unescaped"
        (svg.Contains "&lt;script&gt;") |> Expect.isTrue "it is escaped instead"
        (svg.Contains "&#39;") |> Expect.isTrue "single quotes are escaped"
        (svg.Contains "&amp;") |> Expect.isTrue "ampersands are escaped"
        (svg.Contains "&quot;") |> Expect.isTrue "double quotes are escaped"

      testCase "a member label containing XSS-shaped text is escaped too" <| fun _ ->
        let ledger =
          ledgerFrom [ atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None) ]
        let dangerousLabel (m: string option) =
          match m with
          | Some _ -> "<img src=x onerror=alert(1)>"
          | None -> "integration"
        let svg = toSvg dangerousLabel 320.0 16.0 (project ledger)
        (svg.Contains "<img") |> Expect.isFalse "the raw tag never appears unescaped"
        (svg.Contains "&lt;img") |> Expect.isTrue "it is escaped instead"

      testCase "rendering the same model twice produces byte-identical markup" <| fun _ ->
        let ledger =
          ledgerFrom [
            atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
            atSec 1, CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing")
          ]
        let model = project ledger
        toSvg labelMember 320.0 16.0 model
        |> Expect.equal "deterministic rendering" (toSvg labelMember 320.0 16.0 model)
    ]
  ]
