/// Phase 1 item 18b (sagefs-multiagent-vision.md §6): the read side of
/// `sagefs record`/`play` — an offline CLI command that replays a portable
/// cohort ledger file and prints the reconstructed cohort state. This tests
/// the pure renderer (`CohortPlay.renderPlaySummary`) directly and the thin
/// IO wrapper (`CohortPlay.runPlay`) against real files, including the
/// checked-in `cohorts/basic.ledger.jsonl` fixture from item 18a.
module SageFs.Tests.CohortPlayTests

open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Features.CohortLedgerExport
open SageFs.CohortPlay

// ── A small, real ledger built by folding `Cohort.decide` over real
//    commands — mirrors CohortLedgerExportTests.fs's buildScenario, but is
//    kept local (and self-contained) so this file does not depend on that
//    other test module's private helpers ──────────────────────────────────

let private clock0 = System.DateTime(2026, 2, 1, 0, 0, 0, System.DateTimeKind.Utc)
let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

let private buildLedger () : LedgerEntry<MemberId> list =
  let mutable seq = 0L
  let mutable state = CohortState.empty ()
  let entries = ResizeArray()

  let apply (clock: System.DateTime) (entropy: byte[]) (command: CohortCommand<MemberId>) =
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
    | Error e -> failwithf "buildLedger: command was refused: %A" e

  apply clock0 [||] (CohortCommand.Join(alice, JoinableRole.Implementer, Some "sess-alice"))
  apply (clock0.AddMinutes 1.0) [||] (CohortCommand.Join(bob, JoinableRole.Verifier, Some "sess-bob"))
  apply (clock0.AddMinutes 2.0) [| 9uy; 9uy |] (CohortCommand.AcquireClaim(alice, ClaimScope.File "Bar.fs", "implementing Bar"))
  apply (clock0.AddMinutes 3.0) [| 7uy; 7uy |] (CohortCommand.RequestLanding(alice, [], [ "shaX" ], "land Bar"))

  List.ofSeq entries

[<Tests>]
let cohortPlayTests =
  testList "CohortPlay (Phase 1 item 18b, §6 — offline 'sagefs play')" [

    testList "renderPlaySummary — pure, over a locally-built ledger" [
      let entries = buildLedger ()
      let head = Cohort.replayHead entries
      let frame = Cohort.project head [||]
      let summary = renderPlaySummary entries.Length head frame

      test "reports the entry count and head seq" {
        summary |> Expect.stringContains "shows how many entries were replayed" (sprintf "%d entries" entries.Length)
        summary |> Expect.stringContains "shows the head seq" (sprintf "head seq=%d" (int64 head.Seq))
      }

      test "names alice as Conductor" {
        summary |> Expect.stringContains "alice joined first and is bound as conductor" "Conductor: alice"
        summary |> Expect.stringContains "alice's member row is tagged [Conductor]" "alice  role=Implementer  present [Conductor]"
      }

      test "lists bob as a present member, not conductor" {
        summary |> Expect.stringContains "bob is a present Verifier" "bob  role=Verifier  present"
      }

      test "reports the integration head" {
        summary |> Expect.stringContains "the nullSha integration head is shown" (sprintf "Integration head: %s" Cohort.nullSha)
      }

      test "reports the Bar.fs claim held by alice" {
        summary |> Expect.stringContains "Bar.fs claim is held by alice" "holder=alice  scope=file Bar.fs"
      }

      test "shows the landing queue with the requester and in-flight state" {
        summary |> Expect.stringContains "one landing in flight, requested by alice" "requester=alice"
        // An empty claims list at RequestLanding still advances Queued -> Rebasing
        // immediately (advanceQueue fires synchronously inside `decide`).
        summary |> Expect.stringContains "the landing advanced to Rebasing onto the empty-head sentinel" (sprintf "Rebasing onto %s" Cohort.nullSha)
      }
    ]

    testList "runPlay — the checked-in cohorts/basic.ledger.jsonl fixture (item 18a)" [
      let fixturePath = Path.Combine(__SOURCE_DIRECTORY__, "cohorts", "basic.ledger.jsonl")

      test "reconstructs the fixture's known facts: alice Conductor, bob Verifier, 1 claim held by alice, landing l-0405 Rebasing" {
        File.Exists fixturePath |> Expect.isTrue (sprintf "fixture must exist at %s" fixturePath)

        match runPlay fixturePath with
        | Error e -> failtest (sprintf "expected Ok, got Error %s" e)
        | Ok summary ->
          summary |> Expect.stringContains "alice is Conductor" "Conductor: alice"
          summary |> Expect.stringContains "bob is present as Verifier" "bob  role=Verifier  present"
          summary |> Expect.stringContains "exactly one claim, c-010203, held by alice" "c-010203  holder=alice  scope=file Foo.fs"
          summary |> Expect.stringContains "the landing l-0405 is in Rebasing" "l-0405  requester=alice  Rebasing onto"
          summary |> Expect.stringContains "5 entries were replayed" "Ledger: 5 entries, head seq=4"
      }
    ]

    testList "fail-closed on bad input — never throws" [
      test "runPlay on a missing file returns a clean Error" {
        let missing = Path.Combine(Path.GetTempPath(), "sagefs-play-does-not-exist-" + string (System.Guid.NewGuid()) + ".ledger.jsonl")
        match runPlay missing with
        | Ok _ -> failtest "expected Error — the file does not exist"
        | Error message -> message |> Expect.stringContains "names the missing path" missing
      }

      test "runPlay on malformed ledger content returns a clean Error, never throws" {
        let path = Path.Combine(Path.GetTempPath(), "sagefs-play-malformed-" + string (System.Guid.NewGuid()) + ".ledger.jsonl")
        File.WriteAllText(path, "{ not valid json at all")
        try
          try
            match runPlay path with
            | Ok summary -> failtest (sprintf "expected Error on malformed content, got Ok %s" summary)
            | Error _ -> ()
          with ex ->
            failtestf "runPlay threw instead of returning Error: %O" ex
        finally
          File.Delete path
      }

      test "runPlay on an empty ledger file renders an empty-cohort summary, not an error" {
        let path = Path.Combine(Path.GetTempPath(), "sagefs-play-empty-" + string (System.Guid.NewGuid()) + ".ledger.jsonl")
        File.WriteAllText(path, "")
        try
          match runPlay path with
          | Error e -> failtest (sprintf "an empty ledger is not malformed — expected Ok, got Error %s" e)
          | Ok summary ->
            summary |> Expect.stringContains "zero entries replayed" "Ledger: 0 entries, head seq=0"
            summary |> Expect.stringContains "zero members" "Members (0):"
        finally
          File.Delete path
      }
    ]
  ]
