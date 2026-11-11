/// The dashboard's read of the cohort ledger costs a lookup while nothing is appended.
///
/// Every dashboard push on every connection read the whole ledger of the scope from SQLite while it built the snapshot,
/// before any no-change guard could skip the render. `ReadCache.wrap` answers from the last read of a scope until an
/// entry is appended to that scope, so N pushes with no append read the store once.
module SageFs.Tests.CohortLedgerReadCacheTests

open System
open System.IO
open Expecto
open Expecto.Flip
open Microsoft.Data.Sqlite
open SageFs
open SageFs.Cohort
open SageFs.Features.CohortLedger
open SageFs.MemberTable
open SageFs.Features.CohortLedgerSqlite
open SageFs.Tests.TestInfrastructure

let private clock = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private machine = CohortScope.Machine
let private other = CohortScope.Named "other-cohort"

let private entry (scope: CohortScope) (seq: int64) : LedgerEntry<MemberId> =
  { Seq = LanguagePrimitives.Int64WithMeasure seq
    Clock = clock.AddMinutes(float seq)
    Entropy = [| byte seq |]
    Command = CohortCommand.OpenCohort scope
    Events = [] }

/// A port over a fresh in-memory store that counts the reads the store really answers.
let private counted () : LedgerPort<MemberId> * (unit -> int) =
  let raw = InMemory.create<MemberId> ()
  let reads = ref 0
  { raw with
      ReadAll =
        fun scope ->
          reads.Value <- reads.Value + 1
          raw.ReadAll scope },
  (fun () -> reads.Value)

[<Tests>]
let tests =
  testList "ledger read cache" [
    testCase "WHY — fifty pushes with no append read the store once" <| fun _ ->
      let inner, reads = counted ()
      let port = ReadCache.wrap inner
      for _ in 1 .. 50 do
        port.ReadAll machine |> ignore
      reads () |> Expect.equal "one read of the store" 1

    testCase "WHY — an append makes the next read see the new row, and the reads after that are free again" <| fun _ ->
      let inner, reads = counted ()
      let port = ReadCache.wrap inner
      port.ReadAll machine |> Expect.isEmpty "empty to begin with"
      port.Append(entry machine 0L)
      (port.ReadAll machine).Length |> Expect.equal "the appended row is there" 1
      for _ in 1 .. 20 do
        port.ReadAll machine |> ignore
      reads () |> Expect.equal "one read before the append and one after" 2

    testCase "WHY — an append to one scope leaves another scope's cached read alone" <| fun _ ->
      let inner, reads = counted ()
      let port = ReadCache.wrap inner
      port.ReadAll machine |> ignore
      port.ReadAll other |> ignore
      port.Append(entry other 0L)
      port.ReadAll machine |> ignore
      reads () |> Expect.equal "the machine scope was not re-read" 2
      (port.ReadAll other).Length |> Expect.equal "the other scope sees its row" 1

    testCase "WHY — scopes and the durable append still go to the store underneath" <| fun _ ->
      let inner, _ = counted ()
      let port = ReadCache.wrap inner
      port.Append(entry machine 0L)
      (inner.ReadAll machine).Length |> Expect.equal "the store holds the row" 1
      port.Scopes () |> Expect.equal "scopes come from the store" [ machine ]

    testProperty "WHY — for any run of appends and reads the cached port answers what an uncached one does" <| fun (steps: (bool * bool) list) ->
      let raw = InMemory.create<MemberId> ()
      let port = ReadCache.wrap (InMemory.create<MemberId> ())
      let nextSeq = Collections.Generic.Dictionary<string, int64>()
      let scopeOf useOther = if useOther then other else machine
      steps
      |> List.truncate 80
      |> List.forall (fun (isAppend, useOther) ->
        let scope = scopeOf useOther
        let key = Scope.label scope
        match isAppend with
        | true ->
          let seq = match nextSeq.TryGetValue key with | true, n -> n | false, _ -> 0L
          nextSeq.[key] <- seq + 1L
          raw.Append(entry scope seq)
          port.Append(entry scope seq)
          true
        | false -> (port.ReadAll scope |> List.map (fun e -> int64 e.Seq)) = (raw.ReadAll scope |> List.map (fun e -> int64 e.Seq)))

    testCase "WHY — the daemon puts the cache over the ledger its owner and dashboard share" <| fun _ ->
      let source = File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs", "DaemonMode.fs"))
      source |> Expect.stringContains "DaemonMode wraps the SQLite ledger" "CohortLedger.ReadCache.wrap"
  ]

/// Measured on a scratch ledger of 2,000 rows with a thread scanning it continuously: in the default rollback journal one
/// append waited 30 seconds for the reader and one failed with SQLITE_BUSY (`database is locked`), and in WAL 40 paced appends
/// took 3 ms at the 99th percentile with none failing. The owner appends on its mailbox thread, so an append that waits on a
/// dashboard read stalls every cohort command.
[<Tests>]
let walTests =
  Integration.hostCase "CohortLedgerSqlite is in WAL, so an append never waits on a reader" (fun () ->
    let path = Path.Combine(Path.GetTempPath(), sprintf "sagefs-cohort-ledger-wal-%s.db" (Guid.NewGuid().ToString "N"))
    try
      Sqlite.create path |> ignore
      use connection = new SqliteConnection(sprintf "Data Source=%s" path)
      connection.Open()
      use cmd = connection.CreateCommand()
      cmd.CommandText <- "PRAGMA journal_mode;"
      (cmd.ExecuteScalar() :?> string) |> Expect.equal "the ledger file is in write-ahead-log mode" "wal"
    finally
      for suffix in [ ""; "-wal"; "-shm" ] do
        try File.Delete(path + suffix) with _ -> ())
