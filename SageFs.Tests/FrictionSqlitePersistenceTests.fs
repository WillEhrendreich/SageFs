module SageFs.Tests.FrictionSqlitePersistenceTests

open System
open System.IO
open Expecto
open Expecto.Flip
open Microsoft.Data.Sqlite
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionSqlite

let private ok = function
  | Ok value -> value
  | Error err -> failtestf "expected success, got error: %s" err

let private tool text = ToolName.create text |> ok
let private session text = SessionRef.create text |> ok
let private duration value = DurationMs.create value |> ok

let private mkEvent toolName outcome =
  { OccurredAtUtc = DateTimeOffset.UtcNow
    Session = session "session-1"
    Tool = tool toolName
    Intent = IntentKind.VerifyChangedBehavior
    Outcome = outcome
    Duration = duration 4
    FollowUp = FollowUp.SessionEnded
    ContextCost = ContextCost.Tiny
    SageFsVersion = ""; AgentKey = ""; ErrorSignature = "" }

/// Hand-builds a DB in the pre-B9 shape (`friction_events` WITHOUT
/// `agent_key`/`error_signature`, user_version=1 — i.e. exactly what
/// `Store.create`+`Initialize()` produced before this brief) and inserts one
/// row directly via raw SQL, bypassing `Store.appendEvent` entirely so no
/// B9 code path can accidentally "help" by writing the new columns anyway.
let private seedPreB9Database (dbPath: string) =
  let connStr = sprintf "Data Source=%s" dbPath
  use connection = new SqliteConnection(connStr)
  connection.Open()
  use create = connection.CreateCommand()
  create.CommandText <- "
CREATE TABLE friction_events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  occurred_at_utc TEXT NOT NULL,
  session_id TEXT NOT NULL,
  tool_name TEXT NOT NULL,
  intent_kind TEXT NOT NULL,
  outcome_kind TEXT NOT NULL,
  blocker_kind TEXT NULL,
  resolution_kind TEXT NULL,
  resolution_tool_name TEXT NULL,
  duration_ms INTEGER NOT NULL,
  follow_up_kind TEXT NOT NULL,
  follow_up_tool_name TEXT NULL,
  context_cost_kind TEXT NOT NULL,
  sagefs_version TEXT NOT NULL DEFAULT ''
);
CREATE TABLE explicit_feedback (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  occurred_at_utc TEXT NOT NULL,
  session_id TEXT NOT NULL,
  tool_name TEXT NOT NULL,
  feedback_kind TEXT NOT NULL,
  short_reason TEXT NOT NULL,
  alternative_kind TEXT NOT NULL,
  alternative_tool_name TEXT NULL,
  sagefs_version TEXT NOT NULL DEFAULT ''
);
CREATE TABLE sent_reports (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  report_id TEXT NOT NULL,
  sent_at_utc TEXT NOT NULL,
  sagefs_version TEXT NOT NULL,
  total_events INTEGER NOT NULL,
  total_feedback_items INTEGER NOT NULL,
  destination_kind TEXT NOT NULL,
  destination_url_hash TEXT NOT NULL
);
PRAGMA user_version = 1;"
  create.ExecuteNonQuery() |> ignore
  use insert = connection.CreateCommand()
  insert.CommandText <- "
INSERT INTO friction_events (
  occurred_at_utc, session_id, tool_name, intent_kind, outcome_kind, blocker_kind,
  resolution_kind, resolution_tool_name, duration_ms, follow_up_kind, follow_up_tool_name, context_cost_kind, sagefs_version)
VALUES ('2026-01-01T00:00:00.0000000+00:00', 'session-pre-b9', 'run_tests', 'VerifyChangedBehavior', 'CompletedCleanly', NULL,
  NULL, NULL, 4, 'SessionEnded', NULL, 'Tiny', '0.6.300');"
  insert.ExecuteNonQuery() |> ignore

[<Tests>]
let tests =
  testSequenced (testList "Friction SQLite persistence" [
    testCase "append and readback preserves friction outcome semantics" <| fun _ ->
      let dbPath = Path.Combine(Path.GetTempPath(), sprintf "sagefs-friction-roundtrip-%s.db" (Guid.NewGuid().ToString("N")))
      let conn = sprintf "Data Source=%s" dbPath
      let store = Store.create conn
      store.Initialize() |> ok |> ignore
      let event = mkEvent "run_tests" (FrictionOutcome.RecoveredVia ResolutionKind.SolvedAfterReset)
      store.AppendEvent event |> ok |> ignore
      store.ReadEvents()
      |> ok
      |> Expect.equal "event should round-trip with recovery semantics" [ event ]
      try File.Delete dbPath with _ -> ()

    // Brief B9 (observed-friction-plan.md §B9) — the load-bearing back-compat
    // test: a DB written before the agent_key/error_signature columns
    // existed must still ReadEvents cleanly once Initialize() runs its
    // migration, decoding the old row with AgentKey="" / ErrorSignature=""
    // (the additive fields' safe defaults) rather than failing.
    testCase "an old-schema DB without agent_key/error_signature columns still ReadEvents cleanly" <| fun _ ->
      let dbPath = Path.Combine(Path.GetTempPath(), sprintf "sagefs-friction-preb9-%s.db" (Guid.NewGuid().ToString("N")))
      seedPreB9Database dbPath
      let conn = sprintf "Data Source=%s" dbPath
      let store = Store.create conn
      store.Initialize() |> ok |> ignore  // must migrate the pre-B9 schema, not throw
      let events = store.ReadEvents() |> ok
      events |> Expect.hasLength "the pre-B9 row must still be readable" 1
      let migrated = events.Head
      migrated.Session |> SessionRef.value |> Expect.equal "the old session id should be preserved" "session-pre-b9"
      migrated.SageFsVersion |> Expect.equal "the old sagefs_version should be preserved" "0.6.300"
      migrated.AgentKey |> Expect.equal "a pre-B9 row must decode to the additive field's safe default" ""
      migrated.ErrorSignature |> Expect.equal "a pre-B9 row must decode to the additive field's safe default" ""
      // A subsequent append (post-migration) must also round-trip the new
      // columns normally, proving the migrated schema is fully writable.
      let freshEvent = mkEvent "get_fsi_status" FrictionOutcome.CompletedCleanly
      store.AppendEvent freshEvent |> ok |> ignore
      store.ReadEvents() |> ok |> List.last |> Expect.equal "a fresh post-migration append should round-trip exactly" freshEvent
      try File.Delete dbPath with _ -> ()
  ])
