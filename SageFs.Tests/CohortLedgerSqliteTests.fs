/// Slice 1 of cohort-integration-plan.md: `CohortLedgerSqlite.Sqlite` is the
/// SQLite implementation of `CohortLedger.LedgerPort<MemberId>`. This suite
/// proves the codec round-trips — `ReadAll` after `Append` is the identity
/// on what was appended, including entropy bytes and every DU shape
/// `CohortCommand`/`CohortEvent` can take. It touches a temp SQLite file, so
/// it is registered `[Integration]` (runs under `--integration-host`) rather
/// than joining the default suite.
module SageFs.Tests.CohortLedgerSqliteTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Microsoft.Data.Sqlite
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Features.CohortLedger
open SageFs.Features.CohortLedgerSqlite
open SageFs.Tests.TestInfrastructure

let private tempDbPath () =
  Path.Combine(Path.GetTempPath(), sprintf "sagefs-cohort-ledger-%s.db" (Guid.NewGuid().ToString("N")))

let private cleanup (path: string) =
  try File.Delete path with _ -> ()

// ── Generators covering the DU shapes the codec must round-trip ───────────

let private genMember =
  Gen.oneof [
    Gen.elements [ "a"; "b"; "c" ] |> Gen.map MemberId.Minted
    Gen.elements [ "s1"; "s2" ] |> Gen.map MemberId.Browser
    Gen.elements [ "t1"; "t2" ] |> Gen.map MemberId.Mcp
  ]

let private genRole =
  Gen.elements [ JoinableRole.Implementer; JoinableRole.Verifier; JoinableRole.Observer ]

/// Covers both shapes of the new (item 13c) `session` field so the codec
/// round-trip actually exercises `Some`/`None`, not just one of them.
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

/// The COHORT scope carried by every generated command. This suite is about the
/// ledger CODEC round-tripping a command faithfully, not about cohort scoping: the
/// commands never reach `decide`, they are only serialized and compared back. So
/// the scope is a single fixed value, and a mixed-scope generator would test the
/// codec twice over without adding a single extra round-trip case. Every
/// `CohortCommand` case now carries one, so this is what makes the DU total.
let private cohortScope = CohortScope.Machine

let private genClaimId = Gen.elements [ "c-1"; "c-2"; "c-3" ] |> Gen.map ClaimId
let private genLandingId = Gen.elements [ "l-1"; "l-2" ] |> Gen.map LandingId
let private genTestId = Gen.elements [ "t-a"; "t-b" ] |> Gen.map TestId

let private genFence : Gen<int64<fence>> =
  Gen.choose (0, 100) |> Gen.map (fun n -> LanguagePrimitives.Int64WithMeasure<fence> (int64 n))

let private genDateTime : Gen<DateTime> =
  Gen.choose (0, 1000000)
  |> Gen.map (fun s -> DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(float s))

let private genShortList (g: Gen<'a>) : Gen<'a list> =
  Gen.choose (0, 2) |> Gen.bind (fun n -> Gen.listOfLength n g)

let private genCommand : Gen<CohortCommand<MemberId>> =
  Gen.oneof [
    Gen.map3 (fun m r s -> CohortCommand.Join(m, r, s, cohortScope)) genMember genRole genSessionOpt
    Gen.map (fun m -> CohortCommand.Depart(m, cohortScope)) genMember
    Gen.map (fun m -> CohortCommand.RenewLease(m, cohortScope)) genMember
    Gen.constant (CohortCommand.Tick cohortScope)
    Gen.map3 (fun m s p -> CohortCommand.AcquireClaim(m, s, p, cohortScope)) genMember genScope (Gen.constant "purpose")
    Gen.map3 (fun m c f -> CohortCommand.ReleaseClaim(m, c, f, cohortScope)) genMember genClaimId genFence
    Gen.map3 (fun by c t -> CohortCommand.ReassignClaim(by, c, t, cohortScope)) genMember genClaimId genMember
    Gen.map2 (fun by t -> CohortCommand.DelegateConductor(by, t, cohortScope)) genMember genMember
    Gen.map2 (fun m p -> CohortCommand.ObserveSave(m, p, cohortScope)) genMember (Gen.elements [ "P.fs"; "Q.fs" ])
    (gen {
      let! r = genMember
      let! claims = genShortList (Gen.map2 (fun c f -> c, f) genClaimId genFence)
      let! commits = genShortList (Gen.elements [ "sha1"; "sha2" ])
      return CohortCommand.RequestLanding(r, claims, commits, "statement", cohortScope)
    })
    (gen {
      let! l = genLandingId
      let! ok = Gen.elements [ true; false ]
      if ok then
        let! sha = Gen.elements [ "sha-a"; "sha-b" ]
        return CohortCommand.RebaseCompleted(l, Ok sha, cohortScope)
      else
        let! files = genShortList (Gen.elements [ "conflict.fs"; "other.fs" ])
        return CohortCommand.RebaseCompleted(l, Error files, cohortScope)
    })
    Gen.map2 (fun l ts -> CohortCommand.AffectedComputed(l, ts, cohortScope)) genLandingId (genShortList genTestId)
    Gen.map2 (fun l ts -> CohortCommand.TestsCompleted(l, ts, cohortScope)) genLandingId (genShortList genTestId)
    Gen.map2 (fun l sha -> CohortCommand.FastForwardCompleted(l, sha, cohortScope)) genLandingId (Gen.elements [ "final-a"; "final-b" ])
    Gen.map2 (fun m l -> CohortCommand.WithdrawLanding(m, l, cohortScope)) genMember genLandingId
    Gen.map3 (fun by l reason -> CohortCommand.VetoLanding(by, l, reason, cohortScope)) genMember genLandingId (Gen.constant "reason")
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
/// assigns, and required by the table's `seq INTEGER PRIMARY KEY`.
let private genEntries : Gen<LedgerEntry<MemberId> list> =
  Gen.choose (0, 8)
  |> Gen.bind (fun n -> genAll [ for i in 0 .. n - 1 -> genEntryAt (int64 i) ])

let private config = { FsCheckConfig.defaultConfig with maxTest = 60 }

[<Tests>]
let cohortLedgerSqliteTests =
  Integration.hostList "CohortLedgerSqlite" [

    test "creating a ledger records the schema version via PRAGMA user_version" {
      let path = tempDbPath ()
      try
        Sqlite.create path |> ignore
        use connection = new SqliteConnection(sprintf "Data Source=%s" path)
        connection.Open()
        use cmd = connection.CreateCommand()
        cmd.CommandText <- "PRAGMA user_version;"
        let version = cmd.ExecuteScalar() :?> int64
        // 2 is the `(scope, seq)` key. It was 1 while `seq` alone was the PRIMARY KEY, which only
        // held because there was one cohort; a second scope's own v0 collided on it.
        version |> Expect.equal "the ledger's schema version is recorded" 2L
      finally
        cleanup path
    }

    test "a fresh ledger reads back as empty" {
      let path = tempDbPath ()
      try
        let port = Sqlite.create path
        port.ReadAll cohortScope |> Expect.isEmpty "nothing has been appended yet"
      finally
        cleanup path
    }

    testPropertyWithConfig config "append then readAll is the identity on the appended entries, including entropy bytes and every DU command/event shape" <|
      Prop.forAll (Arb.fromGen genEntries) (fun entries ->
        let path = tempDbPath ()
        try
          let port = Sqlite.create path
          entries |> List.iter port.Append
          port.ReadAll cohortScope = entries
        finally
          cleanup path)

    test "a row this build CANNOT read is skipped, not thrown — the daemon must still start" {
      // The regression this pins. The ledger is DURABLE and survives across versions, so a row
      // written before a command gained a field no longer deserializes. `readAll` used to throw
      // `JsonException` straight out of `startCore`, and the daemon died BEFORE BINDING ITS PORT —
      // so a wire-format change bricked every existing install rather than losing old history.
      //
      // The loss is a stale seat and never live work: an unreadable row cannot have produced a
      // landing or a claim this build could apply, and it belonged to one machine-wide cohort the
      // scoped model no longer uses.
      let path = tempDbPath ()
      try
        let port = Sqlite.create path
        // One row this build CAN read, then one it cannot — the shape a version bump leaves behind.
        port.Append
          { Seq = 0L<ledgerSeq>
            Clock = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            Entropy = [| 1uy |]
            Command = CohortCommand.Tick cohortScope
            Events = [] }
        use connection = new SqliteConnection(sprintf "Data Source=%s" path)
        connection.Open()
        use corrupt = connection.CreateCommand()
        corrupt.CommandText <-
          "INSERT INTO cohort_ledger (scope, seq, clock_ticks, entropy, command_json, events_json) VALUES ('machine', 1, 0, X'01', '{ broken', '[]');"
        corrupt.ExecuteNonQuery() |> ignore

        let read =
          try Sqlite.create path |> fun p -> p.ReadAll cohortScope
          with ex -> failtestf "readAll THREW instead of skipping an unreadable row: %O" ex

        read.Length
        |> Expect.equal "the row this build can read still comes back, and the unreadable one is dropped" 1

        // And the row is LEFT IN PLACE, so a build that can read it is not destroyed by ours.
        use check = connection.CreateCommand()
        check.CommandText <- "SELECT COUNT(*) FROM cohort_ledger;"
        let rows = check.ExecuteScalar() :?> int64
        rows |> Expect.equal "both rows are still on disk — nothing was deleted" 2L
      finally
        cleanup path
    }

    test "TWO scopes each append their own v0: neither insert throws, and each readAll sees only its own row" {
      // The regression this pins. `seq` is COHORT-LOCAL and dense — every scope's ledger starts at
      // v0 — so with `seq` alone as the PRIMARY KEY the two appends below were the SAME key. The
      // second raised `SQLite Error 19: UNIQUE constraint failed: cohort_ledger.seq`, and because the
      // owner's mailbox handler swallows a handler exception and continues with the previous state,
      // that was not a failed command: it was a cohort that had silently stopped accepting any.
      let path = tempDbPath ()
      try
        let port = Sqlite.create path
        let machine = CohortScope.Machine
        // Two scopes, deliberately the shape a SECOND repository brings: not machine-wide, so the
        // two really are two cohorts rather than one with two rows.
        let repo = CohortScope.Repository @"/tmp/sagefs-two-scope-probe"
        let atSeq0 scope =
          { Seq = 0L<ledgerSeq>
            Clock = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            Entropy = [| 1uy |]
            Command = CohortCommand.Tick scope
            Events = [] }

        // Both appends are guarded: the bug is that the second one THROWS, so a bare call would
        // fail this test with a SQLite error instead of saying which half of the fix is missing.
        let firstAppend =
          try
            port.Append (atSeq0 machine)
            None
          with ex -> Some ex
        firstAppend |> Expect.equal "the first scope's v0 appends under the composite key" None

        let secondAppend =
          try
            port.Append (atSeq0 repo)
            None
          with ex -> Some ex
        match secondAppend with
        | Some ex -> failtestf "the SECOND scope's v0 threw — the key is still not (scope, seq): %O" ex
        | None -> ()

        let machineRows = port.ReadAll machine
        let repoRows = port.ReadAll repo
        machineRows.Length |> Expect.equal "the machine scope reads back exactly its own v0" 1
        repoRows.Length |> Expect.equal "the repository scope reads back exactly its own v0" 1
        machineRows.Head.Seq |> Expect.equal "the machine row is the v0 it was appended as" 0L<ledgerSeq>
        repoRows.Head.Seq |> Expect.equal "the repository row is the v0 it was appended as" 0L<ledgerSeq>
        // The scopes are separate buckets, so a read must not answer with the other's history —
        // the failure mode of filtering by COMMAND rather than by the stored scope column.
        machineRows |> List.map (fun e -> Cohort.scopeOf e.Command) |> List.distinct |> Expect.equal "the machine row's command is machine-scoped" [ machine ]
        repoRows |> List.map (fun e -> Cohort.scopeOf e.Command) |> List.distinct |> Expect.equal "the repository row's command is repository-scoped" [ repo ]
        // And a third scope with no rows gets its own empty answer rather than the whole table.
        port.ReadAll (CohortScope.Named "never-used")
        |> Expect.equal "a scope with no rows reads back empty, not every scope's rows" []
        port.Scopes () |> Expect.equal "both scopes are discoverable from the stored column" [ machine; repo ]
      finally
        cleanup path
    }
  ]
