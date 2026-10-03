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

  let private schemaVersion = 1L

  let private openConnection (dbPath: string) =
    let connection = new SqliteConnection(sprintf "Data Source=%s" dbPath)
    connection.Open()
    connection

  let private ensureSchema (connection: SqliteConnection) =
    use command = connection.CreateCommand()
    command.CommandText <- "
CREATE TABLE IF NOT EXISTS cohort_ledger (
  seq INTEGER PRIMARY KEY,
  clock_ticks INTEGER NOT NULL,
  entropy BLOB NOT NULL,
  command_json TEXT NOT NULL,
  events_json TEXT NOT NULL
);"
    command.ExecuteNonQuery() |> ignore
    use pragma = connection.CreateCommand()
    pragma.CommandText <- sprintf "PRAGMA user_version = %d;" schemaVersion
    pragma.ExecuteNonQuery() |> ignore

  /// Open (creating if needed) the cohort ledger at `dbPath` and return the
  /// `LedgerPort` implementation over it. Each `Append`/`ReadAll` opens and
  /// closes its own connection, matching `FrictionSqlite.fs`'s discipline.
  ///
  /// SCOPES COME FROM THE COMMAND, not from a new column. Every `CohortCommand`
  /// carries the scope it was issued against (`Join`, `Depart`, `AffectedComputed`,
  /// `TestsCompleted` and the rest all do), and that command is already serialized
  /// whole into `command_json`. So one cohort per scope is a FILTER on the stored
  /// rows rather than a schema migration, which matters because this table is
  /// durable and already holds rows written before scope existed.
  let create (dbPath: string) : LedgerPort<MemberId> =
    use init = openConnection dbPath
    ensureSchema init

    let append (entry: LedgerEntry<MemberId>) =
      use connection = openConnection dbPath
      use command = connection.CreateCommand()
      command.CommandText <- "
INSERT INTO cohort_ledger (seq, clock_ticks, entropy, command_json, events_json)
VALUES ($seq, $clock_ticks, $entropy, $command_json, $events_json);"
      command.Parameters.AddWithValue("$seq", int64 entry.Seq) |> ignore
      command.Parameters.AddWithValue("$clock_ticks", entry.Clock.Ticks) |> ignore
      command.Parameters.AddWithValue("$entropy", entry.Entropy) |> ignore
      command.Parameters.AddWithValue("$command_json", WorkerProtocol.Serialization.serialize<CohortCommand<MemberId>> entry.Command) |> ignore
      command.Parameters.AddWithValue("$events_json", WorkerProtocol.Serialization.serialize<CohortEvent<MemberId> list> entry.Events) |> ignore
      command.ExecuteNonQuery() |> ignore

    /// Every row, oldest first, SKIPPING any row this build cannot read.
///
/// WHY IT MUST NOT THROW. This table is DURABLE and survives across versions, and a row written
/// before a `CohortCommand` case gained a trailing scope no longer deserializes — the daemon threw
/// `JsonException` out of `startCore` on startup and never bound its port, so a wire-format change
/// bricked every existing install rather than just losing old history. A ledger that cannot be read
/// is a reason to START ANYWAY and say so, not a reason to refuse to boot: the cohort it held was
/// for one machine-wide cohort that the scoped model no longer uses, so the loss is a stale seat,
/// never live work.
    let readAll () : LedgerEntry<MemberId> list =
      use connection = openConnection dbPath
      use command = connection.CreateCommand()
      command.CommandText <- "SELECT seq, clock_ticks, entropy, command_json, events_json FROM cohort_ledger ORDER BY seq;"
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
    /// than a guess. Rows written before scope existed carry a v1 command whose scope was
    /// machine-wide by construction, so they read as `Machine` rather than becoming invisible.
    let scopes () =
      readAll ()
      |> List.map (fun entry -> Cohort.scopeOf entry.Command)
      |> List.distinct
      |> List.sortBy SageFs.Scope.label

    let readAllIn (scope: SageFs.CohortScope) : LedgerEntry<MemberId> list =
      readAll ()
      |> List.filter (fun entry -> SageFs.Scope.equal (Cohort.scopeOf entry.Command) scope)

    { Append = append; ReadAll = readAllIn; Scopes = scopes }

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

  /// Run on daemon start, BEFORE the cohort owner replays the ledger: clear it
  /// when the cohort it holds finished longer ago than `retention`. Doing it
  /// before the replay means the owner's state and the ledger on disk never
  /// disagree. An active cohort's rows are never touched.
  let pruneFinished (retention: System.TimeSpan) (now: System.DateTime) (dbPath: string) : SageFs.Features.LocalDataRetention.LedgerDecision =
    let port = create dbPath
    let decision =
      SageFs.Features.LocalDataRetention.decideLedger retention now (port.ReadAll SageFs.CohortScope.Machine)
    match decision with
    | SageFs.Features.LocalDataRetention.LedgerDecision.Clear _ -> clear dbPath |> ignore
    | SageFs.Features.LocalDataRetention.LedgerDecision.NothingStored
    | SageFs.Features.LocalDataRetention.LedgerDecision.KeepActive _
    | SageFs.Features.LocalDataRetention.LedgerDecision.KeepRecent _ -> ()
    decision
