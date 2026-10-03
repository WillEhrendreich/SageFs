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

/// Every cohort row below is written into the cohort `CohortState.empty` opens,
/// which is Machine-scoped — the v1 shape. No session, no working directory.
let private machine = CohortScope.Machine

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

/// A friction retention policy small enough that a handful of rows exercises every bound: a
/// short age window, three rows, two aggregate versions. Not the product's defaults.
let private policy : LocalDataRetention.FrictionPolicy =
  { MaxAge = TestTimeouts.smallRetentionWindow; MaxRows = 3; MaxAggregateVersions = 2 }

let private rowsIn (usage: StoreUsage) (table: string) =
  usage.Tables |> List.find (fun t -> t.Table = table) |> fun t -> t.Rows

let private ledgerEntry (seq: int64) (clock: DateTime) (state: CohortState<MemberTable.MemberId>) (command: CohortCommand<MemberTable.MemberId>) =
  match SageFs.Cohort.decide clock [| byte seq |] state command with
  | Ok(next, events, _) ->
    next, ({ Seq = LanguagePrimitives.Int64WithMeasure seq; Clock = clock; Entropy = [| byte seq |]; Command = command; Events = events } : LedgerEntry<MemberTable.MemberId>)
  | Error e -> failtestf "cohort command refused: %A" e

/// The whole sweep as `scope -> one-line verdict`, so the ORDER it walks scopes in is pinned as well
/// as each verdict. `Scopes()` sorts by LABEL, so the two `repo:` scopes in the scene below come back
/// `…-active` before `…-finished` — which is what makes this assertion worth making: the sweep must
/// reach a verdict on one scope before it moves on to another, deciding and deleting together,
/// rather than deciding everything first and deleting afterwards.
let private decisionsFor (retention: TimeSpan) (at: DateTime) (path: string) =
  CohortLedgerSqlite.Sqlite.pruneFinished retention at path
  |> List.map (fun d ->
    match d with
    | LocalDataRetention.LedgerDecision.Cleared(scope, finishedAt, rows) -> scope, sprintf "cleared %d rows, finished %O" rows finishedAt
    | LocalDataRetention.LedgerDecision.Kept(scope, _) -> scope, "kept: the cohort is still running"
    | LocalDataRetention.LedgerDecision.KeptRecent(scope, finishedAt) -> scope, sprintf "kept: finished %O, inside the window" finishedAt)

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
        let joined, join = ledgerEntry 0L t0 (CohortState.empty ()) (CohortCommand.Join(ada, JoinableRole.Implementer, None, machine))
        let _, depart = ledgerEntry 1L (t0.AddHours 1.0) joined (CohortCommand.Depart(ada, machine))
        port.Append join
        port.Append depart
        CohortLedgerSqlite.Sqlite.pruneFinished DataRetention.cohortLedgerRetention now.UtcDateTime path
        |> Expect.equal "the finished cohort is cleared" [ LocalDataRetention.LedgerDecision.Cleared(machine, depart.Clock, 2) ]
        port.ReadAll machine |> Expect.isEmpty "nothing left on disk"
        (CohortLedgerSqlite.Sqlite.usage path).Rows |> Expect.equal "usage agrees" 0L)

    testCase "a running cohort's ledger survives the start prune, however old" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "cohort.ledger.db")
        let port = CohortLedgerSqlite.Sqlite.create path
        let ada = MemberTable.MemberId.Mcp "ada"
        let _, join = ledgerEntry 0L (now.UtcDateTime.AddDays -300.0) (CohortState.empty ()) (CohortCommand.Join(ada, JoinableRole.Implementer, None, machine))
        port.Append join
        CohortLedgerSqlite.Sqlite.pruneFinished DataRetention.cohortLedgerRetention now.UtcDateTime path
        |> Expect.equal "kept" [ LocalDataRetention.LedgerDecision.Kept(machine, LocalDataRetention.CohortActivity.Active(1, 0, 0)) ]
        port.ReadAll machine |> List.length |> Expect.equal "the row is still there" 1)

    // ── PER SCOPE, NOT PER MACHINE: the regression this pins ──────────────
    //
    // `pruneFinished` used to read ONLY `CohortScope.Machine` and then decide, and clear, the WHOLE
    // table. Two things were wrong with that and neither is visible in a one-scope fixture:
    //
    //   * Since cohorts became per-repository, every real cohort's rows live under a `repo:...`
    //     scope, so the sweep replayed a scope that in practice holds nothing but rows migrated from
    //     v1. A finished cohort's rows were therefore never reclaimed — which is the bug the sweep
    //     exists to prevent — and the daemon logged a verdict about a ledger it was not keeping.
    //   * Worse, the two halves disagreed: it decided the `machine` scope and then cleared ALL
    //     scopes. A file holding an active cohort in one repo and a finished one in another would have
    //     its live rows deleted, while replaying only the scope that said "keep".
    //
    // So the decision is now made PER SCOPE and the delete is scoped with it, which is the shape the
    // test below can only be written against.

    /// Two cohorts in two scopes, each a real `repo:` scope, each aged past the retention window.
    /// `finished` joined and departed (`CohortActivity.Finished`); `active` joined and still holds a
    /// claim (`CohortActivity.Active`). `seq` restarts at 0 in each, because it is cohort-local.
    let twoScopes (dir: string) =
      let path = Path.Combine(dir, "cohort.ledger.db")
      let port = CohortLedgerSqlite.Sqlite.create path
      let retention = DataRetention.cohortLedgerRetention
      let stale = now.UtcDateTime.AddDays(-30.0)
      let ada = MemberTable.MemberId.Mcp "ada"
      let bo = MemberTable.MemberId.Mcp "bo"
      let finished = CohortScope.Repository "/tmp/sagefs-retention-finished"
      let active = CohortScope.Repository "/tmp/sagefs-retention-active"
      let aged = now.UtcDateTime - retention - TestTimeouts.clockMargin

      // The FINISHED cohort: joined, then departed, so its replayed state has nobody present.
      // `CohortState.forScope`, not `CohortState.empty`: `decide` refuses a command naming a scope
      // the state is not for (`WrongCohortScope`), so a repo cohort's history has to be built in a
      // repo cohort — which is also what the daemon's owner does.
      let joined, joinEntry = ledgerEntry 0L stale (CohortState.forScope finished) (CohortCommand.Join(ada, JoinableRole.Implementer, None, finished))
      let _, departEntry = ledgerEntry 1L aged joined (CohortCommand.Depart(ada, finished))
      port.Append joinEntry
      port.Append departEntry

      // The ACTIVE cohort: joined and holding a claim, so nothing about it may be touched.
      let joined2, joinEntry2 = ledgerEntry 0L stale (CohortState.forScope active) (CohortCommand.Join(bo, JoinableRole.Implementer, None, active))
      let _, claimEntry = ledgerEntry 1L aged joined2 (CohortCommand.AcquireClaim(bo, ClaimScope.File "/r/bo.fs", "editing", active))
      port.Append joinEntry2
      port.Append claimEntry
      path, port, finished, departEntry, active

    testCase "THE START SWEEP IS PER SCOPE: two cohorts, only the finished one is reclaimed" <| fun _ ->
      withTempDir (fun dir ->
        let path, port, finished, departEntry, active = twoScopes dir

        decisionsFor DataRetention.cohortLedgerRetention now.UtcDateTime path
        |> Expect.equal "a verdict for EVERY scope, each naming the scope it is about, in scope-label order"
             [ active, "kept: the cohort is still running"
               finished, sprintf "cleared 2 rows, finished %O" departEntry.Clock ]

        port.ReadAll finished |> Expect.isEmpty "the FINISHED cohort's rows are reclaimed — the case a machine-only reading cannot express"
        port.ReadAll active |> List.length |> Expect.equal "the ACTIVE cohort's rows survive untouched" 2
        (CohortLedgerSqlite.Sqlite.usage path).Rows |> Expect.equal "only the finished cohort's two rows are gone" 2L)

    testCase "a cleared machine-scope cohort never takes a live repo cohort's rows with it" <| fun _ ->
      withTempDir (fun dir ->
        // The destructive half of the same bug, and the reason the delete had to become
        // scope-aware rather than the read merely becoming multi-scope. A v1-shaped machine cohort
        // finished long ago, sitting next to a repository's LIVE cohort. The machine-only sweep
        // answered `Clear` from the machine rows and then ran `DELETE FROM cohort_ledger`, which
        // took the live cohort's rows with it — replaying a scope that said "go" and deleting scopes
        // it had never read.
        let path = Path.Combine(dir, "cohort.ledger.db")
        let port = CohortLedgerSqlite.Sqlite.create path
        let retention = DataRetention.cohortLedgerRetention
        let aged = now.UtcDateTime - retention - TestTimeouts.clockMargin
        let ada = MemberTable.MemberId.Mcp "ada"
        let bo = MemberTable.MemberId.Mcp "bo"
        let live = CohortScope.Repository "/tmp/sagefs-retention-live-repo"

        let joined, joinEntry = ledgerEntry 0L aged (CohortState.empty ()) (CohortCommand.Join(ada, JoinableRole.Implementer, None, machine))
        let _, departEntry = ledgerEntry 1L aged joined (CohortCommand.Depart(ada, machine))
        port.Append joinEntry
        port.Append departEntry
        let joinedLive, joinLive = ledgerEntry 0L aged (CohortState.forScope live) (CohortCommand.Join(bo, JoinableRole.Implementer, None, live))
        let _, claimLive = ledgerEntry 1L aged joinedLive (CohortCommand.AcquireClaim(bo, ClaimScope.File "/r/bo.fs", "editing", live))
        port.Append joinLive
        port.Append claimLive

        decisionsFor retention now.UtcDateTime path
        |> Expect.equal "the finished machine cohort is cleared and the live repo cohort is kept"
             [ machine, sprintf "cleared 2 rows, finished %O" departEntry.Clock
               live, "kept: the cohort is still running" ]

        port.ReadAll machine |> Expect.isEmpty "the machine cohort's rows go"
        port.ReadAll live |> List.length |> Expect.equal "and the live repo cohort's rows are still there" 2)

    testCase "the start sweep decides a machine-scope cohort the same way it decides any other scope" <| fun _ ->
      withTempDir (fun dir ->
        let path = Path.Combine(dir, "cohort.ledger.db")
        let port = CohortLedgerSqlite.Sqlite.create path
        let t0 = now.UtcDateTime.AddDays(-30.0)
        let ada = MemberTable.MemberId.Mcp "ada"
        let joined, joinEntry = ledgerEntry 0L t0 (CohortState.empty ()) (CohortCommand.Join(ada, JoinableRole.Implementer, None, machine))
        let _, departEntry = ledgerEntry 1L (t0.AddHours 1.0) joined (CohortCommand.Depart(ada, machine))
        port.Append joinEntry
        port.Append departEntry

        // The machine scope is NOT dead code: rows written before `scope` existed are all `machine`,
        // so on every real install the sweep now REACHES those rows instead of ignoring them. Under
        // the machine-only reading the sweep happened to read them too — but decided them as though
        // they were the whole file, which is the same verdict for one scope and the wrong one for two.
        CohortLedgerSqlite.Sqlite.pruneFinished DataRetention.cohortLedgerRetention now.UtcDateTime path
        |> Expect.equal "a v1-shaped machine cohort is judged as itself, by its own rows"
             [ LocalDataRetention.LedgerDecision.Cleared(machine, departEntry.Clock, 2) ]
        port.ReadAll machine |> Expect.isEmpty "and its rows go")
  ]
