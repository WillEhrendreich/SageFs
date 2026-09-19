/// Phase 1 item 18a (sagefs-multiagent-vision.md §5.2): a portable JSONL
/// export/import of a cohort ledger, plus a checked-in fixture replay test —
/// the foundation of `sagefs record`/`play`. "For every checked-in
/// SageFs.Tests/cohorts/*.ledger.jsonl, replaying the recorded commands
/// reconstructs the same state."
module SageFs.Tests.CohortLedgerExportTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Features.CohortLedgerExport

// ── Generators covering the DU shapes the codec must round-trip (mirrors
//    CohortLedgerSqliteTests.fs's proven coverage — that suite already shows
//    this exact WorkerProtocol.Serialization codec round-trips every
//    CohortCommand/CohortEvent shape; this file proves the JSONL framing
//    around it is equally lossless) ─────────────────────────────────────────

let private genMember =
  Gen.oneof [
    Gen.elements [ "a"; "b"; "c" ] |> Gen.map MemberId.Minted
    Gen.elements [ "s1"; "s2" ] |> Gen.map MemberId.Browser
    Gen.elements [ "t1"; "t2" ] |> Gen.map MemberId.Mcp
  ]

let private genRole =
  Gen.elements [ JoinableRole.Implementer; JoinableRole.Verifier; JoinableRole.Observer ]

let private genSessionOpt : Gen<string option> =
  Gen.oneof [
    Gen.constant None
    Gen.elements [ "sess-1"; "sess-2" ] |> Gen.map Some
  ]

let private genScope =
  Gen.oneof [
    Gen.elements [ "A.fs"; "B.fs" ] |> Gen.map ClaimScope.File
    Gen.elements [ "X.fsproj"; "Y.fsproj" ] |> Gen.map ClaimScope.Project
  ]

let private genClaimId = Gen.elements [ "c-1"; "c-2"; "c-3" ] |> Gen.map ClaimId
let private genLandingId = Gen.elements [ "l-1"; "l-2" ] |> Gen.map LandingId
let private genTestId = Gen.elements [ "t-a"; "t-b" ] |> Gen.map TestId

let private genFence : Gen<int64<fence>> =
  Gen.choose (0, 100) |> Gen.map (fun n -> LanguagePrimitives.Int64WithMeasure<fence> (int64 n))

let private genDateTime : Gen<DateTime> =
  Gen.choose (0, 1_000_000)
  |> Gen.map (fun s -> DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(float s))

let private genShortList (g: Gen<'a>) : Gen<'a list> =
  Gen.choose (0, 2) |> Gen.bind (fun n -> Gen.listOfLength n g)

let private genCommand : Gen<CohortCommand<MemberId>> =
  Gen.oneof [
    Gen.map3 (fun m r s -> CohortCommand.Join(m, r, s)) genMember genRole genSessionOpt
    Gen.map CohortCommand.Depart genMember
    Gen.map CohortCommand.RenewLease genMember
    Gen.constant CohortCommand.Tick
    Gen.map3 (fun m s p -> CohortCommand.AcquireClaim(m, s, p)) genMember genScope (Gen.constant "purpose")
    Gen.map3 (fun m c f -> CohortCommand.ReleaseClaim(m, c, f)) genMember genClaimId genFence
    Gen.map3 (fun by c t -> CohortCommand.ReassignClaim(by, c, t)) genMember genClaimId genMember
    Gen.map2 (fun by t -> CohortCommand.DelegateConductor(by, t)) genMember genMember
    Gen.map2 (fun m p -> CohortCommand.ObserveSave(m, p)) genMember (Gen.elements [ "P.fs"; "Q.fs" ])
    (gen {
      let! r = genMember
      let! claims = genShortList (Gen.map2 (fun c f -> c, f) genClaimId genFence)
      let! commits = genShortList (Gen.elements [ "sha1"; "sha2" ])
      return CohortCommand.RequestLanding(r, claims, commits, "statement")
    })
    (gen {
      let! l = genLandingId
      let! ok = Gen.elements [ true; false ]
      if ok then
        let! sha = Gen.elements [ "sha-a"; "sha-b" ]
        return CohortCommand.RebaseCompleted(l, Ok sha)
      else
        let! files = genShortList (Gen.elements [ "conflict.fs"; "other.fs" ])
        return CohortCommand.RebaseCompleted(l, Error files)
    })
    Gen.map2 (fun l ts -> CohortCommand.AffectedComputed(l, ts)) genLandingId (genShortList genTestId)
    Gen.map2 (fun l ts -> CohortCommand.TestsCompleted(l, ts)) genLandingId (genShortList genTestId)
    Gen.map2 (fun l sha -> CohortCommand.FastForwardCompleted(l, sha)) genLandingId (Gen.elements [ "final-a"; "final-b" ])
    Gen.map2 (fun m l -> CohortCommand.WithdrawLanding(m, l)) genMember genLandingId
    Gen.map3 (fun by l reason -> CohortCommand.VetoLanding(by, l, reason)) genMember genLandingId (Gen.constant "reason")
    Gen.map2 (fun by head -> CohortCommand.SetIntegrationHead(by, head)) genMember (Gen.elements [ "head-a"; "head-b" ])
  ]

let private genLandingState : Gen<LandingState<MemberId>> =
  Gen.oneof [
    Gen.constant LandingState.Queued
    Gen.map LandingState.Rebasing (Gen.elements [ "onto-a"; "onto-b" ])
    Gen.map4 (fun base' head n r -> LandingState.Verifying(base', head, n, r)) (Gen.elements [ "onto-a"; "onto-b" ]) (Gen.elements [ "head-a"; "head-b" ]) (Gen.choose (0, 5)) (Gen.choose (0, 5))
    (gen {
      let! blocker =
        Gen.oneof [
          genShortList (Gen.elements [ "x.fs"; "y.fs" ]) |> Gen.map LandingBlocker.RebaseConflict
          genShortList genTestId |> Gen.map LandingBlocker.FailingTests
          Gen.map LandingBlocker.StaleClaimFence genClaimId
          Gen.map2 (fun f t -> LandingBlocker.HeadMoved(f, t)) (Gen.elements [ "h1" ]) (Gen.elements [ "h2" ])
          Gen.map2 (fun m r -> LandingBlocker.VetoedBy(m, r)) genMember (Gen.constant "reason")
        ]
      let! action =
        Gen.oneof [
          Gen.constant NextAction.RebaseAndResubmit
          Gen.constant NextAction.AwaitConductor
          genShortList genTestId |> Gen.map NextAction.FixTests
          Gen.constant NextAction.Withdraw
        ]
      return LandingState.Blocked(blocker, action)
    })
    Gen.map LandingState.Landed (Gen.elements [ "landed-a"; "landed-b" ])
    Gen.constant LandingState.Withdrawn
  ]

let private genEvent : Gen<CohortEvent<MemberId>> =
  Gen.oneof [
    Gen.map3 (fun m r s -> CohortEvent.MemberJoined(m, r, s)) genMember genRole genSessionOpt
    Gen.map2 (fun m d -> CohortEvent.MemberDeparted(m, d)) genMember genDateTime
    Gen.map CohortEvent.LeaseRenewed genMember
    Gen.map CohortEvent.ConductorBound genMember
    Gen.map2 (fun f t -> CohortEvent.ConductorDelegated(f, t)) genMember genMember
    (gen {
      let! cid = genClaimId
      let! scope = genScope
      let! holder = genMember
      let! f = genFence
      return CohortEvent.ClaimAcquired(cid, scope, holder, f)
    })
    (gen {
      let! cid = genClaimId
      let! by = genMember
      let! f = genFence
      return CohortEvent.ClaimReleased(cid, by, f)
    })
    (gen {
      let! cid = genClaimId
      let! prev = genMember
      let! f = genFence
      return CohortEvent.ClaimOrphaned(cid, prev, f)
    })
    (gen {
      let! cid = genClaimId
      let! toM = genMember
      let! f = genFence
      return CohortEvent.ClaimReassigned(cid, toM, f)
    })
    (gen {
      let! cid = genClaimId
      let! observer = genMember
      let! holder = genMember
      let! path = Gen.elements [ "P.fs"; "Q.fs" ]
      return CohortEvent.ClaimViolationObserved(cid, observer, holder, path)
    })
    Gen.map2 (fun l r -> CohortEvent.LandingQueued(l, r)) genLandingId genMember
    Gen.map2 (fun l s -> CohortEvent.LandingStateChanged(l, s)) genLandingId genLandingState
    Gen.map2 (fun l sha -> CohortEvent.LandingLanded(l, sha)) genLandingId (Gen.elements [ "sha-x"; "sha-y" ])
    Gen.map CohortEvent.LandingWithdrawn genLandingId
    Gen.map3 (fun l by reason -> CohortEvent.LandingVetoed(l, by, reason)) genLandingId genMember (Gen.constant "reason")
    Gen.map CohortEvent.IntegrationConfigured (Gen.elements [ "head-a"; "head-b" ])
  ]

let private genEntropy : Gen<byte[]> =
  gen {
    let! len = Gen.choose (0, 32)
    let! bytes = Gen.listOfLength len (Gen.choose (0, 255) |> Gen.map byte)
    return List.toArray bytes
  }

let rec private genAll (gens: Gen<'a> list) : Gen<'a list> =
  match gens with
  | [] -> Gen.constant []
  | g :: rest -> gen {
      let! x = g
      let! xs = genAll rest
      return x :: xs
    }

let private genEntryAt (seq: int64) : Gen<LedgerEntry<MemberId>> =
  gen {
    let! clock = genDateTime
    let! entropy = genEntropy
    let! command = genCommand
    let! events = genShortList genEvent
    return { Seq = LanguagePrimitives.Int64WithMeasure<ledgerSeq> seq; Clock = clock; Entropy = entropy; Command = command; Events = events }
  }

/// Dense, increasing Seq (0, 1, 2, ...) — matching what `CohortOwner` itself
/// assigns to a real ledger.
let private genEntries : Gen<LedgerEntry<MemberId> list> =
  Gen.choose (0, 8)
  |> Gen.bind (fun n -> genAll [ for i in 0 .. n - 1 -> genEntryAt (int64 i) ])

let private config = { FsCheckConfig.defaultConfig with maxTest = 60 }

// ── A real scenario, folded through `decide` (not just generated noise) —
//    used both by the replay-equivalence property and to produce the
//    checked-in fixture's content ─────────────────────────────────────────

type private Scenario = {
  Entries: LedgerEntry<MemberId> list
  Alice: MemberId
  Bob: MemberId
  ClaimId: ClaimId
  LandingId: LandingId
}

let private scenarioClock0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private scenarioAlice = MemberId.Minted "alice"
let private scenarioBob = MemberId.Minted "bob"

/// Joins alice (who becomes conductor, v1's implicit create_cohort) and bob,
/// has alice acquire a claim on Foo.fs, renews bob's lease, then has alice
/// request a landing presenting that claim — which immediately advances off
/// Queued into Rebasing (advanceQueue fires synchronously inside `decide`).
/// Five commands, five ledger entries, every one a real, order-dependent
/// state transition (not independent random draws).
let private buildScenario () : Scenario =
  let mutable seq = 0L
  let mutable state = CohortState.empty ()
  let entries = ResizeArray()

  let apply (clock: DateTime) (entropy: byte[]) (command: CohortCommand<MemberId>) : CohortEvent<MemberId> list =
    match decide clock entropy state command with
    | Ok(newState, events, _effects) ->
      state <- newState
      entries.Add
        { Seq = LanguagePrimitives.Int64WithMeasure<ledgerSeq> seq
          Clock = clock
          Entropy = entropy
          Command = command
          Events = events }
      seq <- seq + 1L
      events
    | Error e -> failwithf "buildScenario: command was refused: %A" e

  apply scenarioClock0 [||] (CohortCommand.Join(scenarioAlice, JoinableRole.Implementer, Some "sess-alice"))
  |> ignore

  apply (scenarioClock0.AddMinutes 1.0) [||] (CohortCommand.Join(scenarioBob, JoinableRole.Verifier, Some "sess-bob"))
  |> ignore

  let claimEvents =
    apply (scenarioClock0.AddMinutes 2.0) [| 1uy; 2uy; 3uy |]
      (CohortCommand.AcquireClaim(scenarioAlice, ClaimScope.File "Foo.fs", "implementing Foo"))
  let claimId, fence =
    claimEvents
    |> List.pick (function CohortEvent.ClaimAcquired(cid, _, _, f) -> Some(cid, f) | _ -> None)

  apply (scenarioClock0.AddMinutes 3.0) [||] (CohortCommand.RenewLease scenarioBob)
  |> ignore

  let landingEvents =
    apply (scenarioClock0.AddMinutes 4.0) [| 4uy; 5uy |]
      (CohortCommand.RequestLanding(scenarioAlice, [ claimId, fence ], [ "sha1" ], "land Foo"))
  let landingId =
    landingEvents
    |> List.pick (function CohortEvent.LandingQueued(lid, _) -> Some lid | _ -> None)

  { Entries = List.ofSeq entries
    Alice = scenarioAlice
    Bob = scenarioBob
    ClaimId = claimId
    LandingId = landingId }

let private fixturePath = Path.Combine(__SOURCE_DIRECTORY__, "cohorts", "basic.ledger.jsonl")

// ── Fixed literal expectations for the checked-in fixture — deliberately NOT
//    derived by calling `buildScenario ()` again at test time, and NOT via
//    reading `Ids`'s private hex algorithm: `buildScenario` is test-only
//    helper code that could itself change; the checked-in `.jsonl` file is
//    the frozen ground truth. If a future codec change silently breaks
//    compatibility with an already-recorded ledger, these literals are what
//    catches it. ────────────────────────────────────────────────────────────

let private expectedClaimId = ClaimId "c-010203"
let private expectedLandingId = LandingId "l-0405"

let private expectedState : CohortState<MemberId> =
  let purpose = Purpose.tryCreate "implementing Foo" |> function Ok p -> p | Error e -> failwith e
  let statement = Statement.tryCreate "land Foo" |> function Ok s -> s | Error e -> failwith e
  { NextFence = 1L<fence>
    IntegrationHead = Cohort.nullSha
    Members =
      Map.ofList [
        scenarioAlice,
        { Role = JoinableRole.Implementer
          Presence = MemberPresence.Present
          LastRenewal = scenarioClock0
          Session = Some "sess-alice" }
        scenarioBob,
        { Role = JoinableRole.Verifier
          Presence = MemberPresence.Present
          LastRenewal = scenarioClock0.AddMinutes 3.0
          Session = Some "sess-bob" }
      ]
    Claims =
      Map.ofList [
        expectedClaimId,
        { Id = expectedClaimId
          Fence = 1L<fence>
          Scope = ClaimScope.File "Foo.fs"
          Purpose = purpose
          Since = scenarioClock0.AddMinutes 2.0
          State = ClaimState.Held scenarioAlice }
      ]
    Landings =
      Map.ofList [
        expectedLandingId,
        { Id = expectedLandingId
          Requester = scenarioAlice
          Claims = [ expectedClaimId, 1L<fence> ]
          Commits = [ "sha1" ]
          BaseAtQueue = Cohort.nullSha
          Statement = statement
          State = LandingState.Rebasing Cohort.nullSha
          FastForwardAttempts = 0 }
      ]
    Queue = [ expectedLandingId ]
    Conductor = Some scenarioAlice }

[<Tests>]
let cohortLedgerExportTests =
  testList "CohortLedgerExport (Phase 1 item 18a, §5.2)" [

    testPropertyWithConfig config "fromJsonl (toJsonl entries) = Ok entries, for arbitrary generated ledgers" <|
      Prop.forAll (Arb.fromGen genEntries) (fun entries ->
        fromJsonl (toJsonl entries) = Ok entries)

    test "export -> import -> replay reconstructs the identical CohortState, and replayHead agrees" {
      let scenario = buildScenario ()
      match fromJsonl (toJsonl scenario.Entries) with
      | Error e -> failtest (sprintf "expected Ok, got Error %s" e)
      | Ok roundTripped ->
        roundTripped |> Expect.equal "round-tripped entries are the identity on what was exported" scenario.Entries
        Cohort.replay roundTripped
        |> Expect.equal "replaying the round-tripped ledger reconstructs the same state as the original" (Cohort.replay scenario.Entries)
        Cohort.replayHead roundTripped
        |> Expect.equal "replayHead agrees too (same Seq, same State)" (Cohort.replayHead scenario.Entries)
    }

    test "fromJsonl on an empty string returns Ok [] (an empty ledger is not an error)" {
      fromJsonl "" |> Expect.equal "no entries recorded yet" (Ok [])
    }

    test "fromJsonl ignores a trailing blank line" {
      let scenario = buildScenario ()
      let withTrailingNewline = toJsonl scenario.Entries + "\n\n"
      fromJsonl withTrailingNewline
      |> Expect.equal "trailing blank lines are not corrupt data" (Ok scenario.Entries)
    }

    test "fromJsonl fails closed on one corrupt line, naming its 1-based line number" {
      let scenario = buildScenario ()
      let goodLine = toJsonl [ List.head scenario.Entries ]
      let jsonl = goodLine + "\n" + "{ not valid json" + "\n" + goodLine
      match fromJsonl jsonl with
      | Ok _ -> failtest "expected Error — line 2 is corrupt"
      | Error message -> message |> Expect.stringContains "names the 1-based line number of the corrupt line" "line 2"
    }

    test "fromJsonl never throws on malformed input — it returns Error" {
      let attempts = [ "not json at all"; "{}"; "[]"; "null"; "{\"seq\":\"not-a-number\"}" ]
      for input in attempts do
        try
          match fromJsonl input with
          | Ok _ -> ()
          | Error _ -> ()
        with ex ->
          failtestf "fromJsonl threw on %s: %O" input ex
    }

    testList "the checked-in cohorts/basic.ledger.jsonl fixture (vision §5.2's replay contract)" [
      test "replaying the checked-in fixture reconstructs the exact recorded scenario" {
        File.Exists fixturePath
        |> Expect.isTrue (sprintf "fixture must exist at %s — see buildScenario/toJsonl in this file for how it was produced" fixturePath)

        let jsonl = File.ReadAllText fixturePath
        match fromJsonl jsonl with
        | Error e -> failtest (sprintf "the checked-in fixture failed to parse: %s" e)
        | Ok entries ->
          let head = Cohort.replayHead entries

          head.Seq |> Expect.equal "5 recorded commands, 0-indexed Seq — last is 4" 4L<ledgerSeq>
          head.State |> Expect.equal "the fixture replays to the exact scenario state" expectedState

          // Concrete, individually eyeball-able facts about that state:
          head.State.Conductor |> Expect.equal "alice joined first, so v1's implicit create_cohort bound her as conductor" (Some scenarioAlice)
          head.State.Members |> Map.count |> Expect.equal "alice and bob are both members" 2
          (head.State.Claims |> Map.find expectedClaimId).State
          |> Expect.equal "alice still holds the Foo.fs claim (never released)" (ClaimState.Held scenarioAlice)
          (head.State.Landings |> Map.find expectedLandingId).State
          |> Expect.equal "the landing advanced off Queued into Rebasing the moment it reached the front of an empty queue" (LandingState.Rebasing Cohort.nullSha)
          head.State.Queue |> Expect.equal "one landing in flight" [ expectedLandingId ]
      }
    ]
  ]
