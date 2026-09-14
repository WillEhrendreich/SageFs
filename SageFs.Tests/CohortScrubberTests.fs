/// Phase 2 item 16 of sagefs-multiagent-vision.md (§6.5 "the time-scrubber"):
/// pure unit + property tests for `CohortScrubber` — no daemon, no FSI, no
/// I/O, the same "pure core, no IO" discipline `CohortInspectorTests.fs`/
/// `CohortLanesTests.fs` use. A separate file from those so this island's
/// tests never collide with edits another agent makes to the matrix/
/// territory/lanes/inspector sections.
module SageFs.Tests.CohortScrubberTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Features.CohortScrubber

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private atSec (n: int) : DateTime = epoch.AddSeconds(float n)
let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

/// Folds `decide` over a fixed (clock, command) list from an empty state,
/// recording one dense `LedgerEntry` per accepted command — mirrors
/// `CohortInspectorTests.fs`'s `ledgerFrom`, instantiated at `'m = MemberId`
/// (the type `DashboardInfra.ReadCohortLedger` actually carries).
let private ledgerFrom (commandsWithClocks: (DateTime * CohortCommand<MemberId>) list) : LedgerEntry<MemberId> list =
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

let private threeEntryLedger : LedgerEntry<MemberId> list =
  ledgerFrom [
    atSec 0, CohortCommand.Join(alice, JoinableRole.Implementer, None)
    atSec 1, CohortCommand.Join(bob, JoinableRole.Implementer, None)
    atSec 2, CohortCommand.AcquireClaim(alice, ClaimScope.File "Foo.fs", "working on foo")
  ]

/// The direct, unabbreviated way to compute "the frame as of seq N" —
/// `frameAtSeq` must always agree with this, by the module's own brief
/// ("entries + targetSeq -> CohortFrame" via replay-then-project).
let private replayThenProject (entries: LedgerEntry<MemberId> list) (targetSeq: int64<ledgerSeq>) (snapshots: SessionSnapshot<MemberId>[]) : CohortFrame<MemberId> =
  let prefix = entries |> List.filter (fun e -> e.Seq <= targetSeq)
  project (replayHead prefix) snapshots

[<Tests>]
let cohortScrubberTests =
  testList "CohortScrubber" [

    testList "ledgerThroughSeq" [
      testCase "the ledger's own head (seq 3) keeps every entry" <| fun _ ->
        ledgerThroughSeq threeEntryLedger 3L<ledgerSeq>
        |> Expect.equal "seq<=3 keeps every entry" threeEntryLedger

      testCase "a target before the first entry (seq 1) keeps nothing" <| fun _ ->
        ledgerThroughSeq threeEntryLedger 0L<ledgerSeq>
        |> Expect.equal "empty prefix" []

      testCase "a target between two entries keeps exactly the earlier ones" <| fun _ ->
        ledgerThroughSeq threeEntryLedger 2L<ledgerSeq>
        |> List.map (fun e -> e.Seq)
        |> Expect.equal "seqs 1 and 2 (the first two entries)" [ 1L<ledgerSeq>; 2L<ledgerSeq> ]

      testCase "a target beyond the last entry keeps everything" <| fun _ ->
        ledgerThroughSeq threeEntryLedger 999L<ledgerSeq>
        |> Expect.equal "whole ledger" threeEntryLedger
    ]

    testList "latestSeq" [
      testCase "an empty ledger has no latest seq" <| fun _ ->
        latestSeq ([] : LedgerEntry<MemberId> list)
        |> Expect.equal "None" None

      testCase "a non-empty ledger's latest seq is its last entry's seq" <| fun _ ->
        latestSeq threeEntryLedger
        |> Expect.equal "last entry's seq" (Some 3L<ledgerSeq>)
    ]

    testList "frameAtSeq — WHY: entries + targetSeq -> CohortFrame via replay-then-project (module brief)" [
      testCase "at an early seq equals the direct replay-then-project of the same prefix" <| fun _ ->
        frameAtSeq threeEntryLedger 1L<ledgerSeq> [||]
        |> Expect.equal "matches manual replay(takeUpTo 1) |> project" (replayThenProject threeEntryLedger 1L<ledgerSeq> [||])

      testCase "at the ledger's own head equals the direct replay-then-project of the whole ledger" <| fun _ ->
        frameAtSeq threeEntryLedger 3L<ledgerSeq> [||]
        |> Expect.equal "matches manual replay(whole ledger) |> project" (replayThenProject threeEntryLedger 3L<ledgerSeq> [||])

      testCase "at the ledger's own head equals the LIVE frame (no clamping happening at the boundary)" <| fun _ ->
        frameAtSeq threeEntryLedger 3L<ledgerSeq> [||]
        |> Expect.equal "same as projecting the full ledger directly" (project (replayHead threeEntryLedger) [||])

      testCase "seq 0 — before this ledger's first entry (seq 1) — clamps to the pristine empty-cohort frame" <| fun _ ->
        frameAtSeq threeEntryLedger 0L<ledgerSeq> [||]
        |> Expect.equal "no members, no claims — nothing has happened yet" (project (replayHead []) [||])

      testCase "beyond-latest clamps sanely to the live (full-ledger) frame, not an error or a truncated view" <| fun _ ->
        frameAtSeq threeEntryLedger 999L<ledgerSeq> [||]
        |> Expect.equal "same as the live frame" (project (replayHead threeEntryLedger) [||])

      testCase "an empty ledger at any seq is the pristine empty-cohort frame" <| fun _ ->
        frameAtSeq ([]: LedgerEntry<MemberId> list) 5L<ledgerSeq> [||]
        |> Expect.equal "pristine frame" (project (replayHead []) [||])

      testCase "a scrubbed frame reports the seq it was scrubbed to as its own Version" <| fun _ ->
        (frameAtSeq threeEntryLedger 2L<ledgerSeq> [||]).Version
        |> Expect.equal "Version = the replayed prefix's own last seq (2), not the ledger's latest (3)" 2L<ledgerSeq>

      testCase "deterministic — the same inputs called twice produce structurally-equal frames" <| fun _ ->
        let a = frameAtSeq threeEntryLedger 2L<ledgerSeq> [||]
        let b = frameAtSeq threeEntryLedger 2L<ledgerSeq> [||]
        a |> Expect.equal "same frame both times" b

      testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
        "for ANY target seq (in range, before the start, or past the end) frameAtSeq always equals the direct replay-then-project of the equivalently-filtered prefix" <| fun (target: int64) ->
          let targetSeq = LanguagePrimitives.Int64WithMeasure<ledgerSeq> target
          frameAtSeq threeEntryLedger targetSeq [||] = replayThenProject threeEntryLedger targetSeq [||]
    ]

    testList "tryParseSeq" [
      testCase "empty string parses to None (not scrubbing / live)" <| fun _ ->
        tryParseSeq "" |> Expect.equal "None" None

      testCase "non-numeric text parses to None" <| fun _ ->
        tryParseSeq "live" |> Expect.equal "None" None

      testCase "a numeric string parses to the matching seq" <| fun _ ->
        tryParseSeq "5" |> Expect.equal "Some 5" (Some 5L<ledgerSeq>)

      testCase "a negative numeric string still parses (clamping is frameAtSeq's job, not the parser's)" <| fun _ ->
        tryParseSeq "-1" |> Expect.equal "Some -1" (Some -1L<ledgerSeq>)
    ]

    testList "resolveViewedFrame — the pure form of the push-gate contract" [
      testCase "with no viewing seq, resolves to Live wrapping exactly the caller's live frame" <| fun _ ->
        let live = project (replayHead threeEntryLedger) [||]
        resolveViewedFrame threeEntryLedger [||] live None
        |> Expect.equal "Live live" (ViewedFrame.Live live)

      testCase "WHY — a viewing seq set means the live frame is never shown, no matter what it is" <| fun _ ->
        // Even when the live frame and the historical one happen to render
        // the same underlying state (seq 3 is the ledger's own head), the
        // RESULT must still be tagged Scrubbed, not Live — the push gate's
        // contract is about which branch fires, not whether the bytes
        // happen to match on any one tick.
        let live = project (replayHead threeEntryLedger) [||]
        match resolveViewedFrame threeEntryLedger [||] live (Some 3L<ledgerSeq>) with
        | ViewedFrame.Scrubbed(frame, seq) ->
          frame |> Expect.equal "the scrubbed frame is still the correct historical (here: full-ledger) frame" live
          seq |> Expect.equal "viewed seq echoed back" 3L<ledgerSeq>
        | ViewedFrame.Live _ -> failtest "a set viewing seq must never resolve to Live"

      testCase "a scrubbed view to an earlier seq differs from the live frame's member/claim state" <| fun _ ->
        let live = project (replayHead threeEntryLedger) [||]
        match resolveViewedFrame threeEntryLedger [||] live (Some 1L<ledgerSeq>) with
        | ViewedFrame.Scrubbed(frame, seq) ->
          seq |> Expect.equal "viewed seq echoed back" 1L<ledgerSeq>
          frame.MemberIds.Length |> Expect.equal "only alice has joined by seq 1" 1
          live.MemberIds.Length |> Expect.equal "the live frame already has both members" 2
        | ViewedFrame.Live _ -> failtest "a set viewing seq must never resolve to Live"

      testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
        "resolveViewedFrame is Live iff the caller passed None, and Scrubbed iff it passed Some" <| fun (targetOpt: int64 option) ->
          let live = project (replayHead threeEntryLedger) [||]
          let viewingSeq = targetOpt |> Option.map (LanguagePrimitives.Int64WithMeasure<ledgerSeq>)
          match resolveViewedFrame threeEntryLedger [||] live viewingSeq, viewingSeq with
          | ViewedFrame.Live _, None -> true
          | ViewedFrame.Scrubbed(_, seq), Some expected -> seq = expected
          | _ -> false
    ]
  ]
