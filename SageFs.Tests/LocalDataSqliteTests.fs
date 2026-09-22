/// Retention against real SQLite files in a temp dir: the friction prune,
/// usage and clear (`FrictionSqlite.Store`), and the cohort ledger's
/// startup prune (`CohortLedgerSqlite.Sqlite.pruneFinished`). Never touches
/// the user's ~/.SageFs.
module SageFs.Tests.LocalDataSqliteTests

open System
open System.IO
open Expecto
open Expecto.Flip
open Microsoft.Data.Sqlite
open SageFs
open SageFs.Cohort
open SageFs.Features
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionSqlite

let private ok = function
  | Ok value -> value
  | Error err -> failtestf "expected success, got error: %s" err

let private now = DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero)

let private event (version: string) (daysAgo: float) (toolName: string) : FrictionEvent =
  { OccurredAtUtc = now.AddDays(-daysAgo)
    Session = SessionRef.create "session-1" |> ok
    Tool = ToolName.create toolName |> ok
    Intent = IntentKind.VerifyChangedBehavior
    Outcome = FrictionOutcome.CompletedCleanly
    Duration = DurationMs.create 4 |> ok
    FollowUp = FollowUp.SessionEnded
    ContextCost = ContextCost.Tiny
    SageFsVersion = version; AgentKey = ""; ErrorSignature = "" }

let private feedback (version: string) (daysAgo: float) : ExplicitFeedback =
  { OccurredAtUtc = now.AddDays(-daysAgo)
    Session = SessionRef.create "session-1" |> ok
    Tool = ToolName.create "create_session" |> ok
    Kind = ExplicitFeedbackKind.ToolIntentWasUnclear
    ShortReason = "which session?"
    AlternativeUsed = AlternativePath.NoAlternativeRecorded
    SageFsVersion = version }

let private withTempDir (f: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-local-data-%s" (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory dir |> ignore
  try f dir
  finally
    SqliteConnection.ClearAllPools()
    try Directory.Delete(dir, true) with _ -> ()

let private aggregateRows (dbPath: string) =
  use connection = new SqliteConnection(sprintf "Data Source=%s" dbPath)
  connection.Open()
  use command = connection.CreateCommand()
  command.CommandText <- "SELECT sagefs_version, kind, count FROM friction_version_aggregate ORDER BY sagefs_version, kind;"
  use reader = command.ExecuteReader()
  [ while reader.Read() do yield reader.GetString 0, reader.GetString 1, reader.GetInt64 2 ]

let private policy : LocalDataRetention.FrictionPolicy =
  { MaxAge = TimeSpan.FromDays 5.0; MaxRows = 3; MaxAggregateVersions = 2 }

let private rowsIn (usage: StoreUsage) (table: string) =
  usage.Tables |> List.find (fun t -> t.Table = table) |> fun t -> t.Rows

let private ledgerEntry (seq: int64) (clock: DateTime) (state: CohortState<MemberTable.MemberId>) (command: CohortCommand<MemberTable.MemberId>) =
  match SageFs.Cohort.decide clock [| byte seq |] state command with
  | Ok(next, events, _) ->
    next, ({ Seq = LanguagePrimitives.Int64WithMeasure seq; Clock = clock; Entropy = [| byte seq |]; Command = command; Events = events } : LedgerEntry<MemberTable.MemberId>)
  | Error e -> failtestf "cohort command refused: %A" e

[<Tests>]
let localDataSqliteTests =
  testList "Local data retention on real SQLite" [

    testCase "a prune keeps only the running version's newest rows and counts the rest" <| fun _ ->
      withTempDir (fun dir ->
        let dbPath = Path.Combine(dir, "friction.db")
        let store = Store.create (sprintf "Data Source=%s" dbPath)
        store.Initialize() |> ok
        // old version: 2 fresh rows; current: 1 aged, 5 in the window
        for e in [ event "0.1" 0.5 "a"; event "0.1" 0.2 "a"; event "0.2" 10.0 "b" ] do store.AppendEvent e |> ok
        for d in [ 4.0; 3.0; 2.0; 1.0; 0.1 ] do store.AppendEvent (event "0.2" d "c") |> ok
        store.AppendFeedback (feedback "0.1" 1.0) |> ok
        let outcome = store.Prune policy now "0.2" |> ok
        (outcome.EventsDropped, outcome.FeedbackDropped) |> Expect.equal "3 over-cap/aged/old events and 1 old feedback go" (5, 1)
        let kept = store.ReadEvents() |> ok
        kept |> List.map (fun e -> e.SageFsVersion, Math.Round((now - e.OccurredAtUtc).TotalDays, 3))
        |> Expect.equal "the three newest running-version rows are left" [ "0.2", 2.0; "0.2", 1.0; "0.2", 0.1 ]
        aggregateRows dbPath
        |> Expect.equal "every dropped row is counted by version and kind"
          [ "0.1", "a/CompletedCleanly", 2L
            "0.1", "feedback/ToolIntentWasUnclear", 1L
            "0.2", "b/CompletedCleanly", 1L
            "0.2", "c/CompletedCleanly", 2L ]
        let again = store.Prune policy now "0.2" |> ok
        (again.EventsDropped, again.FeedbackDropped) |> Expect.equal "a second prune finds nothing to do" (0, 0))

    testCase "the aggregate keeps only the most recently seen versions" <| fun _ ->
      withTempDir (fun dir ->
        let store = Store.create (sprintf "Data Source=%s" (Path.Combine(dir, "friction.db")))
        store.Initialize() |> ok
        for v, age in [ "0.1", 4.0; "0.2", 3.0; "0.3", 2.0 ] do store.AppendEvent (event v age "a") |> ok
        let outcome = store.Prune policy now "0.4" |> ok
        outcome.AggregateVersionsDropped |> Expect.equal "the oldest-seen version's counts go" [ "0.1" ])

    testCase "usage reports rows, bytes and the oldest row, and clear empties every table" <| fun _ ->
      withTempDir (fun dir ->
        let store = Store.create (sprintf "Data Source=%s" (Path.Combine(dir, "friction.db")))
        store.Initialize() |> ok
        store.AppendEvent (event "0.2" 2.0 "a") |> ok
        store.AppendEvent (event "0.2" 1.0 "a") |> ok
        let before = store.Usage() |> ok
        rowsIn before FrictionTables.events |> Expect.equal "two events stored" 2L
        (before.Tables |> List.find (fun t -> t.Table = FrictionTables.events)).Oldest
        |> Expect.equal "oldest is the two-day-old row" (OldestRow.WrittenAt(now.AddDays -2.0))
        before.Bytes > 0L |> Expect.isTrue "the file has a size"
        store.Clear() |> ok |> Expect.equal "both rows cleared" 2L
        let after = store.Usage() |> ok
        after.Tables |> List.forall (fun t -> t.Rows = 0L && t.Oldest = OldestRow.NoRows)
        |> Expect.isTrue "every table is empty after a clear")

    testCase "the cohort ledger is cleared on start once a finished cohort is old enough" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "cohort.ledger.db")
        let port = CohortLedgerSqlite.Sqlite.create path
        let t0 = now.UtcDateTime.AddDays -30.0
        let ada = MemberTable.MemberId.Mcp "ada"
        let joined, join = ledgerEntry 0L t0 (CohortState.empty ()) (CohortCommand.Join(ada, JoinableRole.Implementer, None))
        let _, depart = ledgerEntry 1L (t0.AddHours 1.0) joined (CohortCommand.Depart ada)
        port.Append join
        port.Append depart
        CohortLedgerSqlite.Sqlite.pruneFinished (TimeSpan.FromDays 7.0) now.UtcDateTime path
        |> Expect.equal "the finished cohort is cleared" (LocalDataRetention.LedgerDecision.Clear(depart.Clock, 2))
        port.ReadAll() |> Expect.isEmpty "nothing left on disk"
        (CohortLedgerSqlite.Sqlite.usage path).Rows |> Expect.equal "usage agrees" 0L)

    testCase "a running cohort's ledger survives the start prune, however old" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "cohort.ledger.db")
        let port = CohortLedgerSqlite.Sqlite.create path
        let ada = MemberTable.MemberId.Mcp "ada"
        let _, join = ledgerEntry 0L (now.UtcDateTime.AddDays -300.0) (CohortState.empty ()) (CohortCommand.Join(ada, JoinableRole.Implementer, None))
        port.Append join
        CohortLedgerSqlite.Sqlite.pruneFinished (TimeSpan.FromDays 7.0) now.UtcDateTime path
        |> Expect.equal "kept" (LocalDataRetention.LedgerDecision.KeepActive(LocalDataRetention.CohortActivity.Active(1, 0, 0)))
        port.ReadAll() |> List.length |> Expect.equal "the row is still there" 1)
  ]
