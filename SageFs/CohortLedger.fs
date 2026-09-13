module SageFs.Features.CohortLedgerSqlite

open Microsoft.Data.Sqlite
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Features.CohortLedger

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

    let readAll () : LedgerEntry<MemberId> list =
      use connection = openConnection dbPath
      use command = connection.CreateCommand()
      command.CommandText <- "SELECT seq, clock_ticks, entropy, command_json, events_json FROM cohort_ledger ORDER BY seq;"
      use reader = command.ExecuteReader()
      let entries = ResizeArray()
      while reader.Read() do
        let seq = LanguagePrimitives.Int64WithMeasure<Measures.ledgerSeq> (reader.GetInt64 0)
        let clock = System.DateTime(reader.GetInt64 1, System.DateTimeKind.Utc)
        let entropy = reader.GetFieldValue<byte[]>(2)
        let cmd = WorkerProtocol.Serialization.deserialize<CohortCommand<MemberId>> (reader.GetString 3)
        let events = WorkerProtocol.Serialization.deserialize<CohortEvent<MemberId> list> (reader.GetString 4)
        entries.Add { Seq = seq; Clock = clock; Entropy = entropy; Command = cmd; Events = events }
      List.ofSeq entries

    { Append = append; ReadAll = readAll }
