module SageFs.Features.CohortLedgerSqlite

open Microsoft.Data.Sqlite
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Features.CohortLedger
open SageFs.Utils

/// SQLite-backed `CohortLedger.LedgerPort<MemberId>` (Slice 1,
/// cohort-integration-plan.md D1/D3; template: `FrictionSqlite.fs`). One row
/// per `LedgerEntry`. `Command`/`Events` are serialized with the same
/// `JsonFSharpConverter`-configured `JsonSerializerOptions` the daemon<->
/// worker wire protocol already uses (`WorkerProtocol.Serialization`) —
/// DU round-tripping through that codec is already proven in this codebase,
/// so this ledger reuses it rather than hand-rolling a second one.
///
/// Daemon-layer, not Core (D1): SQLite persistence lives in `SageFs/` today
/// (`FrictionSqlite.fs`), and the roast's slim-the-Core mandate keeps IO
/// adapters out of Core. `SageFs.Core/Features/CohortLedger.fs` defines the
/// port this module implements.
module Sqlite =

  let private schemaVersion = 2L

  let private openConnection (dbPath: string) =
    let connection = new SqliteConnection(sprintf "Data Source=%s" dbPath)
    connection.Open()
    connection

  /// WHY THE KEY IS `(scope, seq)` AND NOT `seq`.
  ///
  /// `seq` is cohort-LOCAL and dense — every scope's ledger starts at 0 — because that is what
  /// `replay` walks. As a bare PRIMARY KEY it was only unique by ACCIDENT of there being one scope:
  /// the moment a second scope appended its own v0, SQLite refused it
  /// (`UNIQUE constraint failed: cohort_ledger.seq`), the owner's mailbox handler swallowed the
  /// exception "continuing with previous state", and the cohort silently stopped accepting commands.
  /// A second cohort was not merely unavailable, it was quietly broken.
  ///
  /// So the scope is part of the key, which is what it always meant to be. Rows written before scope
  /// existed are all `Machine` — that is what one machine-wide cohort was — so the migration is a
  /// column with a default and no row is lost.
  let private ensureSchema (connection: SqliteConnection) =
    use command = connection.CreateCommand()
    command.CommandText <- "
CREATE TABLE IF NOT EXISTS cohort_ledger (
  scope TEXT NOT NULL DEFAULT 'machine',
  seq INTEGER NOT NULL,
  clock_ticks INTEGER NOT NULL,
  entropy BLOB NOT NULL,
  command_json TEXT NOT NULL,
  events_json TEXT NOT NULL,
  PRIMARY KEY (scope, seq)
);"
    command.ExecuteNonQuery() |> ignore
    // A table created by schema 1 has `seq` as its PRIMARY KEY, so the `CREATE TABLE IF NOT EXISTS`
    // above did nothing to it. Rebuild it: a scope column, and the composite key. Asked as a
    // scalar rather than through a reader, because a reader left open inside this binding makes
    // everything after it ambiguous.
    let hasScopeColumn =
      use columns = connection.CreateCommand()
      columns.CommandText <- "SELECT COUNT(*) FROM pragma_table_info('cohort_ledger') WHERE name = 'scope';"
      let counted = columns.ExecuteScalar()
      match counted with
      | :? int64 as n -> n > 0L
      | _ -> false
    if not hasScopeColumn then
      Log.info "[cohort-ledger] migrating cohort_ledger to a (scope, seq) key; existing rows are scope machine"
      use migrate = connection.CreateCommand()
      migrate.CommandText <-
        "ALTER TABLE cohort_ledger RENAME TO cohort_ledger_v1;
         CREATE TABLE cohort_ledger (
           scope TEXT NOT NULL DEFAULT 'machine',
           seq INTEGER NOT NULL,
           clock_ticks INTEGER NOT NULL,
           entropy BLOB NOT NULL,
           command_json TEXT NOT NULL,
           events_json TEXT NOT NULL,
           PRIMARY KEY (scope, seq)
         );
         INSERT INTO cohort_ledger (scope, seq, clock_ticks, entropy, command_json, events_json)
           SELECT 'machine', seq, clock_ticks, entropy, command_json, events_json FROM cohort_ledger_v1;
         DROP TABLE cohort_ledger_v1;"
      migrate.ExecuteNonQuery() |> ignore
    use pragma = connection.CreateCommand()
    pragma.CommandText <- sprintf "PRAGMA user_version = %d;" schemaVersion
    pragma.ExecuteNonQuery() |> ignore

  /// Open (creating if needed) the cohort ledger at `dbPath` and return the
  /// `LedgerPort` implementation over it. Each `Append`/`ReadAll` opens and
  /// closes its own connection, matching `FrictionSqlite.fs`'s discipline.
  ///
  /// The scope IS STORED, in its own column, because the primary key is `(scope, seq)`. An
  /// earlier version of this module derived the scope back out of the stored command instead of
  /// adding a column, and that was wrong: `seq` is cohort-LOCAL and dense, so every scope's first
  /// row is v0, and a bare `seq` primary key collided the moment a second scope existed. The
  /// scope had to become part of the key, and a key needs a column to hold it.
  ///
  /// Rows written before this existed are all scope `machine` — one machine-wide cohort is what
  /// they were — so the migration is a column with a default and no row is lost.
  let create (dbPath: string) : LedgerPort<MemberId> =
    use init = openConnection dbPath
    ensureSchema init

    let append (entry: LedgerEntry<MemberId>) =
      use connection = openConnection dbPath
      use command = connection.CreateCommand()
      command.CommandText <- "
INSERT INTO cohort_ledger (scope, seq, clock_ticks, entropy, command_json, events_json)
VALUES ($scope, $seq, $clock_ticks, $entropy, $command_json, $events_json);"
      command.Parameters.AddWithValue("$scope", SageFs.Scope.label (SageFs.Cohort.scopeOf entry.Command)) |> ignore
      command.Parameters.AddWithValue("$seq", int64 entry.Seq) |> ignore
      command.Parameters.AddWithValue("$clock_ticks", entry.Clock.Ticks) |> ignore
      command.Parameters.AddWithValue("$entropy", entry.Entropy) |> ignore
      command.Parameters.AddWithValue("$command_json", WorkerProtocol.Serialization.serialize<CohortCommand<MemberId>> entry.Command) |> ignore
      command.Parameters.AddWithValue("$events_json", WorkerProtocol.Serialization.serialize<CohortEvent<MemberId> list> entry.Events) |> ignore
      command.ExecuteNonQuery() |> ignore

    /// Every row of this SCOPE, oldest first, SKIPPING any row this build cannot read.
    ///
    /// WHY IT MUST NOT THROW. This table is DURABLE and survives across versions, and a row written
    /// before a `CohortCommand` case gained a trailing scope no longer deserializes — the daemon threw
    /// `JsonException` out of `startCore` on startup and never bound its port, so a wire-format change
    /// bricked every existing install rather than just losing old history. A ledger that cannot be read
    /// is a reason to START ANYWAY and say so, not a reason to refuse to boot: the cohort it held was
    /// for one machine-wide cohort that the scoped model no longer uses, so the loss is a stale seat,
    /// never live work.
    let readAll (scope: SageFs.CohortScope) : LedgerEntry<MemberId> list =
      use connection = openConnection dbPath
      use command = connection.CreateCommand()
      command.CommandText <- "SELECT seq, clock_ticks, entropy, command_json, events_json FROM cohort_ledger WHERE scope = $scope ORDER BY seq;"
      command.Parameters.AddWithValue("$scope", SageFs.Scope.label scope) |> ignore
      use reader = command.ExecuteReader()
      let entries = ResizeArray()
      let mutable skipped = 0
      while reader.Read() do
        let seq = LanguagePrimitives.Int64WithMeasure<Measures.ledgerSeq> (reader.GetInt64 0)
        let clock = System.DateTime(reader.GetInt64 1, System.DateTimeKind.Utc)
        let entropy = reader.GetFieldValue<byte[]>(2)
        let cmdJson = reader.GetString 3
        let eventsJson = reader.GetString 4
        // Two independent reads: a row written by an older build can fail on either. Both are
        // "history this build cannot use", and neither is a reason to stop reading the rest.
        match WorkerProtocol.Serialization.tryDeserialize<CohortCommand<MemberId>> cmdJson,
              WorkerProtocol.Serialization.tryDeserialize<CohortEvent<MemberId> list> eventsJson with
        | Ok cmd, Ok events ->
          entries.Add { Seq = seq; Clock = clock; Entropy = entropy; Command = cmd; Events = events }
        | Error commandErr, _ ->
          skipped <- skipped + 1
          Log.warn "[cohort-ledger] skipping ledger row v%d: this build cannot read its command (%s). The row is left in place — the daemon starts without that history rather than refusing to boot." (int64 seq) (SageFsError.describe commandErr)
        | _, Error eventsErr ->
          skipped <- skipped + 1
          Log.warn "[cohort-ledger] skipping ledger row v%d: this build cannot read its events (%s). The row is left in place — the daemon starts without that history rather than refusing to boot." (int64 seq) (SageFsError.describe eventsErr)
      if skipped > 0 then
        Log.warn "[cohort-ledger] %d ledger row(s) could not be read by this build and were skipped. Everything the daemon does now starts from an empty cohort for its scope; no landing or claim was applied from them." skipped
      List.ofSeq entries

    /// The scopes this store actually holds rows for, sorted. A caller starting a cohort for a
    /// scope with no rows gets `[]`, which is what makes a replay a real reconstruction rather
    /// than a guess. Read from the `scope` COLUMN rather than from the commands, because that is
    /// what the key is: a row's scope is the fact, not a derivation that could disagree with it.
    let scopes () =
      use connection = openConnection dbPath
      use command = connection.CreateCommand()
      command.CommandText <- "SELECT DISTINCT scope FROM cohort_ledger;"
      use reader = command.ExecuteReader()
      let labels = ResizeArray<string>()
      while reader.Read() do
        labels.Add(reader.GetString 0)
      labels |> Seq.toList |> List.map SageFs.Cohort.scopeOfLabel |> List.sortBy SageFs.Scope.label

    { Append = append; ReadAll = readAll; Scopes = scopes }

  /// The ledger's footprint for the "what's stored" view.
  type LedgerUsage = {
    Bytes: int64
    Rows: int64
    /// Clock of the oldest entry, `NoRows` when the ledger is empty.
    Oldest: SageFs.Features.FrictionSqlite.OldestRow
  }

  let usage (dbPath: string) : LedgerUsage =
    use connection = openConnection dbPath
    ensureSchema connection
    let scalar (sql: string) =
      use command = connection.CreateCommand()
      command.CommandText <- sql
      command.ExecuteScalar()
    let bytes = System.Convert.ToInt64(scalar "PRAGMA page_count;") * System.Convert.ToInt64(scalar "PRAGMA page_size;")
    let rows = System.Convert.ToInt64(scalar "SELECT count(*) FROM cohort_ledger;")
    let oldest =
      match scalar "SELECT min(clock_ticks) FROM cohort_ledger;" with
      | :? int64 as ticks ->
        SageFs.Features.FrictionSqlite.OldestRow.WrittenAt(System.DateTimeOffset(System.DateTime(ticks, System.DateTimeKind.Utc)))
      | _ -> SageFs.Features.FrictionSqlite.OldestRow.NoRows
    { Bytes = bytes; Rows = rows; Oldest = oldest }

  /// Delete every ledger row and give the space back. Callers decide whether
  /// that's allowed (`LocalDataRetention.decideLedger`); this just does it.
  ///
  /// This wipes EVERY scope, so it is the tool for "this file is finished" and
  /// nothing finer. Retention cannot use it any more — see `clearScope`.
  let clear (dbPath: string) : int64 =
    use connection = openConnection dbPath
    ensureSchema connection
    let deleted =
      use command = connection.CreateCommand()
      command.CommandText <- "DELETE FROM cohort_ledger;"
      int64 (command.ExecuteNonQuery())
    use vacuum = connection.CreateCommand()
    vacuum.CommandText <- "VACUUM;"
    vacuum.ExecuteNonQuery() |> ignore
    deleted

  /// Delete ONE scope's rows, leaving every other cohort in the file alone, and
  /// give the space back.
  ///
  /// WHY THIS EXISTS. `clear` answered a decision made from a SINGLE scope's rows by deleting every
  /// row in the table. Those two halves are the same scope only while the file holds one cohort; the
  /// moment it holds two, a `Clear` reached by replaying one scope destroyed the other scope's rows
  /// too — so the sweep could reclaim nothing, or destroy live work, and neither showed up in the
  /// verdict it returned. `DELETE ... WHERE scope = $scope` makes the delete as narrow as the
  /// decision that authorises it.
  ///
  /// VACUUM only once more than this scope's own rows went: it rewrites the WHOLE file, so calling it
  /// per scope would pay for a rewrite once per scope in the file. The freed pages are reclaimed by
  /// SQLite's allocator regardless, and the next full `clear`/`pruneFinished` sweeps them.
  let clearScope (dbPath: string) (scope: SageFs.CohortScope) : int64 =
    use connection = openConnection dbPath
    ensureSchema connection
    let deleted =
      use command = connection.CreateCommand()
      command.CommandText <- "DELETE FROM cohort_ledger WHERE scope = $scope;"
      command.Parameters.AddWithValue("$scope", SageFs.Scope.label scope) |> ignore
      int64 (command.ExecuteNonQuery())
    if deleted > 0L then
      use vacuum = connection.CreateCommand()
      vacuum.CommandText <- "VACUUM;"
      vacuum.ExecuteNonQuery() |> ignore
    deleted

  /// Run on daemon start, BEFORE the cohort owner replays the ledger: clear the
  /// rows of every scope whose cohort finished longer ago than `retention`.
  /// Doing it before the replay means the owner's state and the ledger on disk
  /// never disagree. An active cohort's rows are never touched.
  ///
  /// WHY PER SCOPE, AND WHY NOT "ONE VERDICT FOR THE WHOLE FILE". Retention is a property of ONE
  /// cohort finishing, not of the machine: `CohortScope` exists precisely because two repositories'
  /// agents share nothing, and a cohort in one of them going quiet says nothing about the other.
  /// Deciding across scopes could only have been done by folding every scope's rows into one replay
  /// — a cohort whose members happen to share an id with another's would read as active forever, and
  /// one finished scope's age would be judged against another scope's clock. Neither is a fact about
  /// either cohort. So each scope is replayed on its own, judged on its own rows, and cleared on its
  /// own rows.
  ///
  /// WHY IT WAS NOT DOING THAT. This read only `CohortScope.Machine` and then deleted the whole
  /// table. Since cohorts became per-repository, `machine` holds nothing but rows migrated from v1,
  /// so no real cohort was ever reclaimed; and had a v1 cohort finished while another repository's
  /// cohort was live, the same call would have deleted the live one.
  ///
  /// The verdict is a LIST, one entry per scope that holds rows, in `port.Scopes ()` order. It is not
  /// "the" decision for the file: with several cohorts in one file there is no single answer, and a
  /// caller that needs to know which one it is must be handed each of them. A scope with no rows is
  /// absent, which is the honest answer — a `NothingStored` for a scope nobody has rows for would be
  /// a claim about a cohort that does not exist.
  let pruneFinished (retention: System.TimeSpan) (now: System.DateTime) (dbPath: string) : SageFs.Features.LocalDataRetention.LedgerDecision list =
    let port = create dbPath
    [ for scope in port.Scopes () do
        let decision = SageFs.Features.LocalDataRetention.decideLedger retention now scope (port.ReadAll scope)
        match decision with
        | SageFs.Features.LocalDataRetention.LedgerDecision.Cleared _ -> clearScope dbPath scope |> ignore
        | SageFs.Features.LocalDataRetention.LedgerDecision.Kept _
        | SageFs.Features.LocalDataRetention.LedgerDecision.KeptRecent _ -> ()
        decision ]
