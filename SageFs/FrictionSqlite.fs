module SageFs.Features.FrictionSqlite

open System
open Microsoft.Data.Sqlite
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionTelemetry

type SentReport = {
  ReportId: string
  SentAtUtc: DateTimeOffset
  SageFsVersion: string
  TotalEvents: int
  TotalFeedbackItems: int
  DestinationKind: string
  DestinationUrlHash: string
}

/// One table's footprint, for the "what's stored" view.
type TableUsage = {
  Table: string
  Rows: int64
  /// When the oldest row was written. `NoRows` when the table is empty.
  Oldest: OldestRow
}

and [<RequireQualifiedAccess>] OldestRow =
  | NoRows
  | WrittenAt of DateTimeOffset

type StoreUsage = {
  Bytes: int64
  Tables: TableUsage list
}

type FrictionPruneOutcome = {
  EventsDropped: int
  FeedbackDropped: int
  /// Versions whose aggregate rows were dropped to stay under the version cap.
  AggregateVersionsDropped: string list
}

type FrictionStore = {
  Initialize: unit -> Result<unit, string>
  AppendEvent: FrictionEvent -> Result<unit, string>
  AppendFeedback: ExplicitFeedback -> Result<unit, string>
  ReadEvents: unit -> Result<FrictionEvent list, string>
  ReadFeedback: unit -> Result<ExplicitFeedback list, string>
  RecordSentReport: SentReport -> Result<unit, string>
  ListSentReports: unit -> Result<SentReport list, string>
  /// Roll rows from other versions, rows past the age cap and rows over the
  /// row cap into the per-version aggregate, and delete them.
  Prune: SageFs.Features.LocalDataRetention.FrictionPolicy -> DateTimeOffset -> string -> Result<FrictionPruneOutcome, string>
  Usage: unit -> Result<StoreUsage, string>
  /// Delete every row in every table. Returns how many rows went.
  Clear: unit -> Result<int64, string>
}

/// Table names, in one place, so the prune, the usage view and the clear
/// can't drift apart.
[<RequireQualifiedAccess>]
module FrictionTables =
  let events = "friction_events"
  let feedback = "explicit_feedback"
  let sentReports = "sent_reports"
  let aggregate = "friction_version_aggregate"
  /// Every table, with the column that says when a row was written.
  let all =
    [ events, "occurred_at_utc"
      feedback, "occurred_at_utc"
      sentReports, "sent_at_utc"
      aggregate, "last_seen_utc" ]

module private Encoding =
  let outcomeParts outcome =
    match outcome with
    | FrictionOutcome.CompletedCleanly -> "CompletedCleanly", None, None, None
    | FrictionOutcome.EncounteredBlocker blocker -> "EncounteredBlocker", Some (string blocker), None, None
    | FrictionOutcome.RecoveredVia ResolutionKind.SolvedWithRetry -> "RecoveredVia", None, Some "SolvedWithRetry", None
    | FrictionOutcome.RecoveredVia (ResolutionKind.SolvedWithDifferentTool tool) -> "RecoveredVia", None, Some "SolvedWithDifferentTool", Some (ToolName.value tool)
    | FrictionOutcome.RecoveredVia ResolutionKind.SolvedAfterReset -> "RecoveredVia", None, Some "SolvedAfterReset", None
    | FrictionOutcome.RecoveredVia ResolutionKind.SolvedAfterSessionSwitch -> "RecoveredVia", None, Some "SolvedAfterSessionSwitch", None
    | FrictionOutcome.RecoveredVia ResolutionKind.Unresolved -> "RecoveredVia", None, Some "Unresolved", None
    | FrictionOutcome.AbandonedWithoutResolution -> "AbandonedWithoutResolution", None, None, None

  let followUpParts followUp =
    match followUp with
    | FollowUp.NoFollowUpYet -> "NoFollowUpYet", None
    | FollowUp.FollowedByTool tool -> "FollowedByTool", Some (ToolName.value tool)
    | FollowUp.SessionEnded -> "SessionEnded", None

  let contextCostText = function
    | ContextCost.Tiny -> "Tiny"
    | ContextCost.Focused -> "Focused"
    | ContextCost.Heavy -> "Heavy"

  let explicitFeedbackKindText = function
    | ExplicitFeedbackKind.ToolOutputWasTooLarge -> "ToolOutputWasTooLarge"
    | ExplicitFeedbackKind.ToolIntentWasUnclear -> "ToolIntentWasUnclear"
    | ExplicitFeedbackKind.ToolNameWasMisleading -> "ToolNameWasMisleading"
    | ExplicitFeedbackKind.NeededAnotherToolToFinish -> "NeededAnotherToolToFinish"
    | ExplicitFeedbackKind.ResultDidNotEstablishTrust -> "ResultDidNotEstablishTrust"

  let alternativeParts = function
    | AlternativePath.NoAlternativeRecorded -> "NoAlternativeRecorded", None
    | AlternativePath.ResolvedWithTool tool -> "ResolvedWithTool", Some (ToolName.value tool)
    | AlternativePath.ResolvedOutsideMcp -> "ResolvedOutsideMcp", None

module private Decoding =
  let private parseTool text = ToolName.create text |> Result.mapError id
  let private parseSession text = SessionRef.create text |> Result.mapError id
  let private parseDuration value = DurationMs.create value |> Result.mapError id

  let parseIntent = function
    | "VerifyChangedBehavior" -> Ok IntentKind.VerifyChangedBehavior
    | "RunExactTest" -> Ok IntentKind.RunExactTest
    | "ExploreCode" -> Ok IntentKind.ExploreCode
    | "InspectFailure" -> Ok IntentKind.InspectFailure
    | "RecoverSession" -> Ok IntentKind.RecoverSession
    | "ManageSession" -> Ok IntentKind.ManageSession
    | other -> Error (sprintf "Unknown intent kind '%s'." other)

  let private parseBlocker = function
    | "SessionAmbiguous" -> Ok BlockerKind.SessionAmbiguous
    | "SessionMissing" -> Ok BlockerKind.SessionMissing
    | "SessionWarming" -> Ok BlockerKind.SessionWarming
    | "LoadedStateUnknown" -> Ok BlockerKind.LoadedStateUnknown
    | "LoadedStateStale" -> Ok BlockerKind.LoadedStateStale
    | "TypeIdentityCompromised" -> Ok BlockerKind.TypeIdentityCompromised
    | "ExactTestNotFound" -> Ok BlockerKind.ExactTestNotFound
    | "OutputTooLarge" -> Ok BlockerKind.OutputTooLarge
    | "AffordanceMismatch" -> Ok BlockerKind.AffordanceMismatch
    | "TransportFailure" -> Ok BlockerKind.TransportFailure
    | "OperationFailed" -> Ok BlockerKind.OperationFailed
    | "InvalidRequest" -> Ok BlockerKind.InvalidRequest
    | other -> Error (sprintf "Unknown blocker kind '%s'." other)

  let private parseResolution resolutionKind resolutionTool =
    match resolutionKind, resolutionTool with
    | Some "SolvedWithRetry", _ -> Ok ResolutionKind.SolvedWithRetry
    | Some "SolvedWithDifferentTool", Some tool -> parseTool tool |> Result.map ResolutionKind.SolvedWithDifferentTool
    | Some "SolvedAfterReset", _ -> Ok ResolutionKind.SolvedAfterReset
    | Some "SolvedAfterSessionSwitch", _ -> Ok ResolutionKind.SolvedAfterSessionSwitch
    | Some "Unresolved", _ -> Ok ResolutionKind.Unresolved
    | None, _ -> Error "Missing resolution kind."
    | Some other, _ -> Error (sprintf "Unknown resolution kind '%s'." other)

  let private parseOutcome outcomeKind blockerKind resolutionKind resolutionTool =
    match outcomeKind with
    | "CompletedCleanly" -> Ok FrictionOutcome.CompletedCleanly
    | "EncounteredBlocker" ->
      match blockerKind with
      | Some blocker -> parseBlocker blocker |> Result.map FrictionOutcome.EncounteredBlocker
      | None -> Error "Blocked outcome missing blocker kind."
    | "RecoveredVia" ->
      parseResolution resolutionKind resolutionTool |> Result.map FrictionOutcome.RecoveredVia
    | "AbandonedWithoutResolution" -> Ok FrictionOutcome.AbandonedWithoutResolution
    | other -> Error (sprintf "Unknown outcome kind '%s'." other)

  let private parseFollowUp followUpKind followUpTool =
    match followUpKind with
    | "NoFollowUpYet" -> Ok FollowUp.NoFollowUpYet
    | "FollowedByTool" ->
      match followUpTool with
      | Some tool -> parseTool tool |> Result.map FollowUp.FollowedByTool
      | None -> Error "Follow-up tool missing tool name."
    | "SessionEnded" -> Ok FollowUp.SessionEnded
    | other -> Error (sprintf "Unknown follow-up kind '%s'." other)

  let private parseContextCost = function
    | "Tiny" -> Ok ContextCost.Tiny
    | "Focused" -> Ok ContextCost.Focused
    | "Heavy" -> Ok ContextCost.Heavy
    | other -> Error (sprintf "Unknown context cost '%s'." other)

  let private parseFeedbackKind = function
    | "ToolOutputWasTooLarge" -> Ok ExplicitFeedbackKind.ToolOutputWasTooLarge
    | "ToolIntentWasUnclear" -> Ok ExplicitFeedbackKind.ToolIntentWasUnclear
    | "ToolNameWasMisleading" -> Ok ExplicitFeedbackKind.ToolNameWasMisleading
    | "NeededAnotherToolToFinish" -> Ok ExplicitFeedbackKind.NeededAnotherToolToFinish
    | "ResultDidNotEstablishTrust" -> Ok ExplicitFeedbackKind.ResultDidNotEstablishTrust
    | other -> Error (sprintf "Unknown explicit feedback kind '%s'." other)

  let private parseAlternative kind toolName =
    match kind with
    | "NoAlternativeRecorded" -> Ok AlternativePath.NoAlternativeRecorded
    | "ResolvedWithTool" ->
      match toolName with
      | Some tool -> parseTool tool |> Result.map AlternativePath.ResolvedWithTool
      | None -> Error "Alternative tool missing name."
    | "ResolvedOutsideMcp" -> Ok AlternativePath.ResolvedOutsideMcp
    | other -> Error (sprintf "Unknown alternative kind '%s'." other)

  let decodeEvent occurredAt sessionId toolName intent outcome blocker resolution resolutionTool duration followUp followUpTool contextCost sageFsVersion agentKey errorSignature =
    match parseSession sessionId with
    | Error err -> Error err
    | Ok session ->
      match parseTool toolName with
      | Error err -> Error err
      | Ok tool ->
        match parseOutcome outcome blocker resolution resolutionTool with
        | Error err -> Error err
        | Ok parsedOutcome ->
          match parseDuration duration with
          | Error err -> Error err
          | Ok parsedDuration ->
            match parseFollowUp followUp followUpTool with
            | Error err -> Error err
            | Ok parsedFollowUp ->
              match parseContextCost contextCost with
              | Error err -> Error err
              | Ok parsedCost ->
                Ok {
                  OccurredAtUtc = occurredAt
                  Session = session
                  Tool = tool
                  Intent = intent
                  Outcome = parsedOutcome
                  Duration = parsedDuration
                  FollowUp = parsedFollowUp
                  ContextCost = parsedCost
                  SageFsVersion = sageFsVersion
                  AgentKey = agentKey
                  ErrorSignature = errorSignature
                }

  let decodeFeedback occurredAt sessionId toolName kind shortReason alternativeKind alternativeTool sageFsVersion =
    match parseSession sessionId with
    | Error err -> Error err
    | Ok session ->
      match parseTool toolName with
      | Error err -> Error err
      | Ok tool ->
        match parseFeedbackKind kind with
        | Error err -> Error err
        | Ok feedbackKind ->
          match parseAlternative alternativeKind alternativeTool with
          | Error err -> Error err
          | Ok alternative ->
            Ok {
              OccurredAtUtc = occurredAt
              Session = session
              Tool = tool
              Kind = feedbackKind
              ShortReason = shortReason
              AlternativeUsed = alternative
              SageFsVersion = sageFsVersion
            }

module Store =
  let create (connectionString: string) : FrictionStore =
    let openConnection () =
      let connection = new SqliteConnection(connectionString)
      connection.Open()
      connection

    let initialize () =
      try
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- "
CREATE TABLE IF NOT EXISTS friction_events (
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
  sagefs_version TEXT NOT NULL DEFAULT '',
  agent_key TEXT NOT NULL DEFAULT '',
  error_signature TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS explicit_feedback (
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
CREATE TABLE IF NOT EXISTS sent_reports (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  report_id TEXT NOT NULL,
  sent_at_utc TEXT NOT NULL,
  sagefs_version TEXT NOT NULL,
  total_events INTEGER NOT NULL,
  total_feedback_items INTEGER NOT NULL,
  destination_kind TEXT NOT NULL,
  destination_url_hash TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sent_reports_report_id ON sent_reports (report_id);
CREATE TABLE IF NOT EXISTS friction_version_aggregate (
  sagefs_version TEXT NOT NULL,
  kind TEXT NOT NULL,
  count INTEGER NOT NULL,
  last_seen_utc TEXT NOT NULL,
  PRIMARY KEY (sagefs_version, kind)
);"
        command.ExecuteNonQuery() |> ignore
        // Schema versioning (mirrors CohortLedger): read PRAGMA user_version and
        // only run the one-time migration when the DB predates it, instead of
        // re-issuing an ALTER that fails every startup on an up-to-date DB.
        let readUserVersion () =
          use cmd = connection.CreateCommand()
          cmd.CommandText <- "PRAGMA user_version"
          Convert.ToInt64(cmd.ExecuteScalar())
        // A duplicate-column error means the column is already present (fresh
        // DBs get every column from CREATE above); any OTHER error is a real
        // failure and must surface, not be swallowed.
        let addColumn tableName columnDef =
          try
            use cmd = connection.CreateCommand()
            cmd.CommandText <- sprintf "ALTER TABLE %s ADD COLUMN %s" tableName columnDef
            cmd.ExecuteNonQuery() |> ignore
          with :? SqliteException as e when e.Message.Contains "duplicate column" -> ()
        let setUserVersion version =
          use cmd = connection.CreateCommand()
          cmd.CommandText <- sprintf "PRAGMA user_version = %d" (version: int)
          cmd.ExecuteNonQuery() |> ignore
        match readUserVersion () < 1L with
        | false -> ()
        | true ->
          // Migrate pre-versioning tables that lack the sagefs_version column.
          addColumn "friction_events" "sagefs_version TEXT NOT NULL DEFAULT ''"
          addColumn "explicit_feedback" "sagefs_version TEXT NOT NULL DEFAULT ''"
          setUserVersion 1
        match readUserVersion () < 2L with
        | false -> ()
        | true ->
          // Brief B9 (observed-friction-plan.md §B9): additive attribution +
          // sanitized error-signature columns on friction_events only — an
          // old-schema DB (columns absent) still decodes cleanly because
          // ReadEvents below treats a missing column as "".
          addColumn "friction_events" "agent_key TEXT NOT NULL DEFAULT ''"
          addColumn "friction_events" "error_signature TEXT NOT NULL DEFAULT ''"
          setUserVersion 2
        Ok ()
      with ex -> Error ex.Message

    let appendEvent event =
      try
        use connection = openConnection ()
        let outcomeKind, blockerKind, resolutionKind, resolutionTool = Encoding.outcomeParts event.Outcome
        let followUpKind, followUpTool = Encoding.followUpParts event.FollowUp
        let dbNullObj = box DBNull.Value
        use command = connection.CreateCommand()
        command.CommandText <- "
INSERT INTO friction_events (
  occurred_at_utc, session_id, tool_name, intent_kind, outcome_kind, blocker_kind,
  resolution_kind, resolution_tool_name, duration_ms, follow_up_kind, follow_up_tool_name, context_cost_kind, sagefs_version,
  agent_key, error_signature)
VALUES ($occurred_at_utc, $session_id, $tool_name, $intent_kind, $outcome_kind, $blocker_kind,
  $resolution_kind, $resolution_tool_name, $duration_ms, $follow_up_kind, $follow_up_tool_name, $context_cost_kind, $sagefs_version,
  $agent_key, $error_signature);"
        command.Parameters.AddWithValue("$occurred_at_utc", event.OccurredAtUtc.ToString("O")) |> ignore
        command.Parameters.AddWithValue("$session_id", SessionRef.value event.Session) |> ignore
        command.Parameters.AddWithValue("$tool_name", ToolName.value event.Tool) |> ignore
        command.Parameters.AddWithValue("$intent_kind", string event.Intent) |> ignore
        command.Parameters.AddWithValue("$outcome_kind", outcomeKind) |> ignore
        command.Parameters.AddWithValue("$blocker_kind", blockerKind |> Option.map box |> Option.defaultValue dbNullObj) |> ignore
        command.Parameters.AddWithValue("$resolution_kind", resolutionKind |> Option.map box |> Option.defaultValue dbNullObj) |> ignore
        command.Parameters.AddWithValue("$resolution_tool_name", resolutionTool |> Option.map box |> Option.defaultValue dbNullObj) |> ignore
        command.Parameters.AddWithValue("$duration_ms", DurationMs.value event.Duration) |> ignore
        command.Parameters.AddWithValue("$follow_up_kind", followUpKind) |> ignore
        command.Parameters.AddWithValue("$follow_up_tool_name", followUpTool |> Option.map box |> Option.defaultValue dbNullObj) |> ignore
        command.Parameters.AddWithValue("$context_cost_kind", Encoding.contextCostText event.ContextCost) |> ignore
        command.Parameters.AddWithValue("$sagefs_version", event.SageFsVersion) |> ignore
        command.Parameters.AddWithValue("$agent_key", event.AgentKey) |> ignore
        command.Parameters.AddWithValue("$error_signature", event.ErrorSignature) |> ignore
        command.ExecuteNonQuery() |> ignore
        Ok ()
      with ex -> Error ex.Message

    let appendFeedback feedback =
      try
        use connection = openConnection ()
        let alternativeKind, alternativeTool = Encoding.alternativeParts feedback.AlternativeUsed
        let dbNullObj = box DBNull.Value
        use command = connection.CreateCommand()
        command.CommandText <- "
INSERT INTO explicit_feedback (
  occurred_at_utc, session_id, tool_name, feedback_kind, short_reason, alternative_kind, alternative_tool_name, sagefs_version)
VALUES ($occurred_at_utc, $session_id, $tool_name, $feedback_kind, $short_reason, $alternative_kind, $alternative_tool_name, $sagefs_version);"
        command.Parameters.AddWithValue("$occurred_at_utc", feedback.OccurredAtUtc.ToString("O")) |> ignore
        command.Parameters.AddWithValue("$session_id", SessionRef.value feedback.Session) |> ignore
        command.Parameters.AddWithValue("$tool_name", ToolName.value feedback.Tool) |> ignore
        command.Parameters.AddWithValue("$feedback_kind", Encoding.explicitFeedbackKindText feedback.Kind) |> ignore
        command.Parameters.AddWithValue("$short_reason", feedback.ShortReason) |> ignore
        command.Parameters.AddWithValue("$alternative_kind", alternativeKind) |> ignore
        command.Parameters.AddWithValue("$alternative_tool_name", alternativeTool |> Option.map box |> Option.defaultValue dbNullObj) |> ignore
        command.Parameters.AddWithValue("$sagefs_version", feedback.SageFsVersion) |> ignore
        command.ExecuteNonQuery() |> ignore
        Ok ()
      with ex -> Error ex.Message

    let readEvents () =
      try
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- "
SELECT occurred_at_utc, session_id, tool_name, intent_kind, outcome_kind, blocker_kind,
       resolution_kind, resolution_tool_name, duration_ms, follow_up_kind, follow_up_tool_name, context_cost_kind, sagefs_version,
       agent_key, error_signature
FROM friction_events
ORDER BY id;"
        use reader = command.ExecuteReader()
        let mutable events = []
        while reader.Read() do
          match Decoding.parseIntent (reader.GetString(3)) with
          | Error err -> raise (InvalidOperationException err)
          | Ok intent ->
            let sageFsVersion = if reader.IsDBNull(12) then "" else reader.GetString(12)
            // Additive B9 columns — a DB migrated up-front by Initialize()
            // always has them, but IsDBNull guards a legitimately-NULL value
            // the same way sagefs_version does above (belt-and-suspenders,
            // matching the migration's back-compat contract).
            let agentKey = if reader.IsDBNull(13) then "" else reader.GetString(13)
            let errorSignature = if reader.IsDBNull(14) then "" else reader.GetString(14)
            let decoded =
              Decoding.decodeEvent
                (DateTimeOffset.Parse(reader.GetString(0)))
                (reader.GetString(1))
                (reader.GetString(2))
                intent
                (reader.GetString(4))
                (if reader.IsDBNull(5) then None else Some (reader.GetString(5)))
                (if reader.IsDBNull(6) then None else Some (reader.GetString(6)))
                (if reader.IsDBNull(7) then None else Some (reader.GetString(7)))
                (reader.GetInt32(8))
                (reader.GetString(9))
                (if reader.IsDBNull(10) then None else Some (reader.GetString(10)))
                (reader.GetString(11))
                sageFsVersion
                agentKey
                errorSignature
            match decoded with
            | Ok event -> events <- event :: events
            | Error err -> raise (InvalidOperationException err)
        Ok (List.rev events)
      with ex -> Error ex.Message

    let readFeedback () =
      try
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- "
SELECT occurred_at_utc, session_id, tool_name, feedback_kind, short_reason, alternative_kind, alternative_tool_name, sagefs_version
FROM explicit_feedback
ORDER BY id;"
        use reader = command.ExecuteReader()
        let mutable feedback = []
        while reader.Read() do
          let sageFsVersion = if reader.IsDBNull(7) then "" else reader.GetString(7)
          let decoded =
            Decoding.decodeFeedback
              (DateTimeOffset.Parse(reader.GetString(0)))
              (reader.GetString(1))
              (reader.GetString(2))
              (reader.GetString(3))
              (reader.GetString(4))
              (reader.GetString(5))
              (if reader.IsDBNull(6) then None else Some (reader.GetString(6)))
              sageFsVersion
          match decoded with
          | Ok item -> feedback <- item :: feedback
          | Error err -> raise (InvalidOperationException err)
        Ok (List.rev feedback)
      with ex -> Error ex.Message

    let recordSentReport report =
      try
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- "
INSERT INTO sent_reports (
  report_id, sent_at_utc, sagefs_version, total_events, total_feedback_items,
  destination_kind, destination_url_hash)
VALUES ($report_id, $sent_at_utc, $sagefs_version, $total_events, $total_feedback_items,
  $destination_kind, $destination_url_hash);"
        command.Parameters.AddWithValue("$report_id", report.ReportId) |> ignore
        command.Parameters.AddWithValue("$sent_at_utc", report.SentAtUtc.ToString("O")) |> ignore
        command.Parameters.AddWithValue("$sagefs_version", report.SageFsVersion) |> ignore
        command.Parameters.AddWithValue("$total_events", report.TotalEvents) |> ignore
        command.Parameters.AddWithValue("$total_feedback_items", report.TotalFeedbackItems) |> ignore
        command.Parameters.AddWithValue("$destination_kind", report.DestinationKind) |> ignore
        command.Parameters.AddWithValue("$destination_url_hash", report.DestinationUrlHash) |> ignore
        command.ExecuteNonQuery() |> ignore
        Ok ()
      with ex -> Error ex.Message

    let listSentReports () =
      try
        use connection = openConnection ()
        use command = connection.CreateCommand()
        command.CommandText <- "
SELECT report_id, sent_at_utc, sagefs_version, total_events, total_feedback_items,
       destination_kind, destination_url_hash
FROM sent_reports
ORDER BY id DESC;"
        use reader = command.ExecuteReader()
        let mutable reports = []
        while reader.Read() do
          reports <-
            {
              ReportId = reader.GetString(0)
              SentAtUtc = DateTimeOffset.Parse(reader.GetString(1))
              SageFsVersion = reader.GetString(2)
              TotalEvents = reader.GetInt32(3)
              TotalFeedbackItems = reader.GetInt32(4)
              DestinationKind = reader.GetString(5)
              DestinationUrlHash = reader.GetString(6)
            } :: reports
        Ok (List.rev reports)
      with ex -> Error ex.Message

    let parseStamp (text: string) =
      DateTimeOffset.Parse(text, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind)

    let stamp (at: DateTimeOffset) = at.ToUniversalTime().ToString("O")

    /// The four columns retention looks at. `kindSql` is how the table's rows
    /// get counted in the aggregate.
    let readRetained (connection: SqliteConnection) (transaction: SqliteTransaction) (table: string) (kindSql: string) =
      use command = connection.CreateCommand()
      command.Transaction <- transaction
      command.CommandText <- sprintf "SELECT id, occurred_at_utc, sagefs_version, %s FROM %s;" kindSql table
      use reader = command.ExecuteReader()
      let rows = ResizeArray()
      while reader.Read() do
        rows.Add
          ({ Id = reader.GetInt64 0
             OccurredAt = parseStamp (reader.GetString 1)
             Version = (if reader.IsDBNull 2 then "" else reader.GetString 2)
             Kind = reader.GetString 3 } : SageFs.Features.LocalDataRetention.RetainedRow)
      List.ofSeq rows

    let applyDecision (connection: SqliteConnection) (transaction: SqliteTransaction) (table: string) (decision: SageFs.Features.LocalDataRetention.FrictionDecision) =
      for KeyValue(key, count) in decision.Aggregated do
        use upsert = connection.CreateCommand()
        upsert.Transaction <- transaction
        upsert.CommandText <- "
INSERT INTO friction_version_aggregate (sagefs_version, kind, count, last_seen_utc)
VALUES ($version, $kind, $count, $last_seen)
ON CONFLICT (sagefs_version, kind) DO UPDATE SET
  count = count + excluded.count,
  last_seen_utc = max(last_seen_utc, excluded.last_seen_utc);"
        upsert.Parameters.AddWithValue("$version", key.Version) |> ignore
        upsert.Parameters.AddWithValue("$kind", key.Kind) |> ignore
        upsert.Parameters.AddWithValue("$count", count) |> ignore
        upsert.Parameters.AddWithValue("$last_seen", stamp (Map.find key.Version decision.LastSeen)) |> ignore
        upsert.ExecuteNonQuery() |> ignore
      use delete = connection.CreateCommand()
      delete.Transaction <- transaction
      delete.CommandText <- sprintf "DELETE FROM %s WHERE id = $id;" table
      let idParam = delete.Parameters.Add("$id", SqliteType.Integer)
      for row, _ in decision.Dropped do
        idParam.Value <- row.Id
        delete.ExecuteNonQuery() |> ignore
      decision.Dropped.Length

    let vacuum (connection: SqliteConnection) =
      use command = connection.CreateCommand()
      command.CommandText <- "VACUUM;"
      command.ExecuteNonQuery() |> ignore

    let prune (policy: SageFs.Features.LocalDataRetention.FrictionPolicy) (now: DateTimeOffset) (currentVersion: string) =
      try
        use connection = openConnection ()
        let outcome =
          use transaction = connection.BeginTransaction()
          let decideFor table kindSql =
            readRetained connection transaction table kindSql
            |> SageFs.Features.LocalDataRetention.decide policy now currentVersion
          let eventsDropped =
            decideFor FrictionTables.events "tool_name || '/' || outcome_kind"
            |> applyDecision connection transaction FrictionTables.events
          let feedbackDropped =
            decideFor FrictionTables.feedback "'feedback/' || feedback_kind"
            |> applyDecision connection transaction FrictionTables.feedback
          let lastSeen =
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            command.CommandText <- "SELECT sagefs_version, max(last_seen_utc) FROM friction_version_aggregate GROUP BY sagefs_version;"
            use reader = command.ExecuteReader()
            let seen = ResizeArray()
            while reader.Read() do
              seen.Add(reader.GetString 0, parseStamp (reader.GetString 1))
            Map.ofSeq seen
          let versionsDropped = SageFs.Features.LocalDataRetention.aggregateVersionsToDrop policy currentVersion lastSeen
          for version in versionsDropped do
            use delete = connection.CreateCommand()
            delete.Transaction <- transaction
            delete.CommandText <- "DELETE FROM friction_version_aggregate WHERE sagefs_version = $version;"
            delete.Parameters.AddWithValue("$version", version) |> ignore
            delete.ExecuteNonQuery() |> ignore
          transaction.Commit()
          { EventsDropped = eventsDropped; FeedbackDropped = feedbackDropped; AggregateVersionsDropped = versionsDropped }
        // Deleting rows doesn't shrink the file; VACUUM does. Only worth it
        // when something actually went.
        match outcome.EventsDropped + outcome.FeedbackDropped + outcome.AggregateVersionsDropped.Length with
        | 0 -> ()
        | _ -> vacuum connection
        Ok outcome
      with ex -> Error ex.Message

    let usage () =
      try
        use connection = openConnection ()
        let scalar (sql: string) =
          use command = connection.CreateCommand()
          command.CommandText <- sql
          command.ExecuteScalar()
        let bytes = Convert.ToInt64(scalar "PRAGMA page_count;") * Convert.ToInt64(scalar "PRAGMA page_size;")
        let tables =
          [ for table, stampColumn in FrictionTables.all ->
              let rows = Convert.ToInt64(scalar (sprintf "SELECT count(*) FROM %s;" table))
              let oldest =
                match scalar (sprintf "SELECT min(%s) FROM %s;" stampColumn table) with
                | :? string as text -> OldestRow.WrittenAt (parseStamp text)
                | _ -> OldestRow.NoRows
              { Table = table; Rows = rows; Oldest = oldest } ]
        Ok { Bytes = bytes; Tables = tables }
      with ex -> Error ex.Message

    let clear () =
      try
        use connection = openConnection ()
        let deleted =
          use transaction = connection.BeginTransaction()
          let total =
            FrictionTables.all
            |> List.sumBy (fun (table, _) ->
              use command = connection.CreateCommand()
              command.Transaction <- transaction
              command.CommandText <- sprintf "DELETE FROM %s;" table
              int64 (command.ExecuteNonQuery()))
          transaction.Commit()
          total
        vacuum connection
        Ok deleted
      with ex -> Error ex.Message

    { Initialize = initialize
      Prune = prune
      Usage = usage
      Clear = clear
      AppendEvent = appendEvent
      AppendFeedback = appendFeedback
      ReadEvents = readEvents
      ReadFeedback = readFeedback
      RecordSentReport = recordSentReport
      ListSentReports = listSentReports }
  let private parseIntent = function
    | "VerifyChangedBehavior" -> Ok IntentKind.VerifyChangedBehavior
    | "RunExactTest" -> Ok IntentKind.RunExactTest
    | "ExploreCode" -> Ok IntentKind.ExploreCode
    | "InspectFailure" -> Ok IntentKind.InspectFailure
    | "RecoverSession" -> Ok IntentKind.RecoverSession
    | "ManageSession" -> Ok IntentKind.ManageSession
    | other -> Error (sprintf "Unknown intent kind '%s'." other)
