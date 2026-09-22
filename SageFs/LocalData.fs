/// What SageFs keeps under its data dir, how big it is, how long it's kept,
/// and a way to clear it. The MCP tool `manage_local_data` and the daemon's
/// `/api/local-data` endpoints both go through here so they can't disagree.
module SageFs.LocalData

open System
open System.IO
open SageFs.Features
open SageFs.Features.FrictionSqlite

let frictionFileName = "friction.db"
let cohortLedgerFileName = "cohort.ledger.db"

let frictionPath (dataDir: string) = Path.Combine(dataDir, frictionFileName)
let cohortLedgerPath (dataDir: string) = Path.Combine(dataDir, cohortLedgerFileName)

[<RequireQualifiedAccess>]
type FrictionReport =
  /// The daemon started without a friction store (init failed or it's off).
  | NotConfigured
  | ReadFailed of reason: string
  | Stored of StoreUsage

type Report = {
  DataDir: string
  Friction: FrictionReport
  CohortLedger: Result<CohortLedgerSqlite.Sqlite.LedgerUsage, string>
  /// The live cohort, as the running owner sees it right now.
  Cohort: LocalDataRetention.CohortActivity
  Policy: LocalDataRetention.FrictionPolicy
  CohortLedgerRetention: TimeSpan
  PruneInterval: TimeSpan
}

let report (frictionStore: FrictionStore option) (cohort: LocalDataRetention.CohortActivity) (dataDir: string) : Report =
  let friction =
    match frictionStore with
    | None -> FrictionReport.NotConfigured
    | Some store ->
      match store.Usage () with
      | Ok usage -> FrictionReport.Stored usage
      | Error reason -> FrictionReport.ReadFailed reason
  let ledger =
    try Ok (CohortLedgerSqlite.Sqlite.usage (cohortLedgerPath dataDir))
    with ex -> Error ex.Message
  { DataDir = dataDir
    Friction = friction
    CohortLedger = ledger
    Cohort = cohort
    Policy = LocalDataRetention.defaultPolicy ()
    CohortLedgerRetention = DataRetention.cohortLedgerRetention
    PruneInterval = DataRetention.pruneInterval }

let private oldestText (oldest: OldestRow) =
  match oldest with
  | OldestRow.NoRows -> "empty"
  | OldestRow.WrittenAt at -> sprintf "oldest %s" (at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'"))

let private kib (bytes: int64) = sprintf "%.1f KiB" (float bytes / 1024.0)

let private cohortText (activity: LocalDataRetention.CohortActivity) =
  match activity with
  | LocalDataRetention.CohortActivity.Finished -> "no cohort running"
  | LocalDataRetention.CohortActivity.Active(present, held, landings) ->
    sprintf "cohort running: %d present, %d claims held, %d landings in flight" present held landings

/// The plain-text view the MCP tool returns.
let render (r: Report) : string =
  let lines = ResizeArray<string>()
  lines.Add(sprintf "Local data in %s" r.DataDir)
  lines.Add ""
  match r.Friction with
  | FrictionReport.NotConfigured -> lines.Add(sprintf "%s: not configured (this daemon isn't recording friction)" frictionFileName)
  | FrictionReport.ReadFailed reason -> lines.Add(sprintf "%s: couldn't read it: %s" frictionFileName reason)
  | FrictionReport.Stored usage ->
    lines.Add(sprintf "%s: %s" frictionFileName (kib usage.Bytes))
    for t in usage.Tables do
      lines.Add(sprintf "  %s: %d rows, %s" t.Table t.Rows (oldestText t.Oldest))
  match r.CohortLedger with
  | Ok usage -> lines.Add(sprintf "%s: %s, %d rows, %s (%s)" cohortLedgerFileName (kib usage.Bytes) usage.Rows (oldestText usage.Oldest) (cohortText r.Cohort))
  | Error reason -> lines.Add(sprintf "%s: couldn't read it: %s" cohortLedgerFileName reason)
  lines.Add ""
  lines.Add "Kept for:"
  lines.Add(sprintf "  friction: only the running SageFs version's rows (%s). Other versions are rolled into a count per kind." (FrictionTelemetryTypes.SageFsVersion.current ()))
  lines.Add(sprintf "  friction: rows older than %g days go (%s)" r.Policy.MaxAge.TotalDays DataRetention.frictionMaxAgeEnvVar)
  lines.Add(sprintf "  friction: at most %d rows per table, newest win (%s)" r.Policy.MaxRows DataRetention.frictionMaxRowsEnvVar)
  lines.Add(sprintf "  friction: counts kept for the %d most recent versions (%s)" r.Policy.MaxAggregateVersions DataRetention.frictionMaxAggregateVersionsEnvVar)
  lines.Add(sprintf "  friction: pruned on daemon start and every %g minutes (%s)" r.PruneInterval.TotalMinutes DataRetention.pruneIntervalEnvVar)
  lines.Add(sprintf "  cohort ledger: cleared on daemon start once the cohort finished more than %g days ago; a running cohort is never touched (%s)" r.CohortLedgerRetention.TotalDays DataRetention.cohortLedgerRetentionEnvVar)
  lines.Add ""
  lines.Add "Clear it with manage_local_data action=clear store=friction|cohort|all."
  String.Join("\n", lines)

/// The JSON shape `/api/local-data` returns.
let toJson (r: Report) : obj =
  let oldestJson (oldest: OldestRow) : obj =
    match oldest with
    | OldestRow.NoRows -> null
    | OldestRow.WrittenAt at -> box (at.ToUniversalTime().ToString("O"))
  box
    {| dataDir = r.DataDir
       friction =
         match r.Friction with
         | FrictionReport.NotConfigured -> box {| state = "notConfigured" |}
         | FrictionReport.ReadFailed reason -> box {| state = "readFailed"; reason = reason |}
         | FrictionReport.Stored usage ->
           box
             {| state = "stored"
                path = frictionPath r.DataDir
                bytes = usage.Bytes
                tables = usage.Tables |> List.map (fun t -> {| table = t.Table; rows = t.Rows; oldest = oldestJson t.Oldest |}) |}
       cohortLedger =
         match r.CohortLedger with
         | Ok usage ->
           box
             {| state = "stored"
                path = cohortLedgerPath r.DataDir
                bytes = usage.Bytes
                rows = usage.Rows
                oldest = oldestJson usage.Oldest
                cohort = cohortText r.Cohort |}
         | Error reason -> box {| state = "readFailed"; reason = reason |}
       retention =
         {| frictionCurrentVersionOnly = FrictionTelemetryTypes.SageFsVersion.current ()
            frictionMaxAgeDays = r.Policy.MaxAge.TotalDays
            frictionMaxRows = r.Policy.MaxRows
            frictionMaxAggregateVersions = r.Policy.MaxAggregateVersions
            pruneIntervalMinutes = r.PruneInterval.TotalMinutes
            cohortLedgerRetentionDays = r.CohortLedgerRetention.TotalDays |} |}

[<RequireQualifiedAccess>]
type ClearTarget =
  | Friction
  | CohortLedger
  | All

module ClearTarget =
  let parse (text: string) : Result<ClearTarget, string> =
    match (if isNull text then "" else text.Trim().ToLowerInvariant()) with
    | "" | "all" -> Ok ClearTarget.All
    | "friction" -> Ok ClearTarget.Friction
    | "cohort" | "cohort-ledger" -> Ok ClearTarget.CohortLedger
    | other -> Error (sprintf "Unknown store '%s'. Use friction, cohort or all." other)

[<RequireQualifiedAccess>]
type ClearOutcome =
  | Cleared of rows: int64
  | NotConfigured
  /// The cohort is still running; its ledger is never cleared out from under it.
  | RefusedActiveCohort of LocalDataRetention.CohortActivity
  | Failed of reason: string

[<RequireQualifiedAccess>]
type LocalStore =
  | Friction
  | CohortLedger

module LocalStore =
  let fileName (store: LocalStore) =
    match store with
    | LocalStore.Friction -> frictionFileName
    | LocalStore.CohortLedger -> cohortLedgerFileName

/// One line per store the clear touched.
type ClearReport = (LocalStore * ClearOutcome) list

let clear (target: ClearTarget) (frictionStore: FrictionStore option) (cohort: LocalDataRetention.CohortActivity) (dataDir: string) : ClearReport =
  let clearFriction () =
    match frictionStore with
    | None -> ClearOutcome.NotConfigured
    | Some store ->
      match store.Clear () with
      | Ok rows -> ClearOutcome.Cleared rows
      | Error reason -> ClearOutcome.Failed reason
  let clearLedger () =
    match cohort with
    | LocalDataRetention.CohortActivity.Active _ -> ClearOutcome.RefusedActiveCohort cohort
    | LocalDataRetention.CohortActivity.Finished ->
      // The running owner keeps its in-memory state; only what's left of a
      // finished cohort (departed seats, settled claims and landings, the
      // conductor binding) goes from disk. The next daemon start replays
      // from whatever gets appended after this, which is a fresh cohort.
      try ClearOutcome.Cleared (CohortLedgerSqlite.Sqlite.clear (cohortLedgerPath dataDir))
      with ex -> ClearOutcome.Failed ex.Message
  match target with
  | ClearTarget.Friction -> [ LocalStore.Friction, clearFriction () ]
  | ClearTarget.CohortLedger -> [ LocalStore.CohortLedger, clearLedger () ]
  | ClearTarget.All -> [ LocalStore.Friction, clearFriction (); LocalStore.CohortLedger, clearLedger () ]

let private outcomeText (store: LocalStore) (outcome: ClearOutcome) =
  let name = LocalStore.fileName store
  match outcome with
  | ClearOutcome.Cleared rows -> sprintf "%s: cleared %d rows" name rows
  | ClearOutcome.NotConfigured -> sprintf "%s: not configured, nothing to clear" name
  | ClearOutcome.RefusedActiveCohort activity -> sprintf "%s: left alone, %s" name (cohortText activity)
  | ClearOutcome.Failed reason -> sprintf "%s: clear failed: %s" name reason

let renderClear (r: ClearReport) : string =
  r |> List.map (fun (store, outcome) -> outcomeText store outcome) |> String.concat "\n"

/// The live cohort's activity, from the running owner. No owner (a daemon
/// without cohorts wired) means nothing is running.
let liveCohort (owner: CohortOwner.Handle option) : LocalDataRetention.CohortActivity =
  match owner with
  | Some handle -> LocalDataRetention.cohortActivity (handle.ReadCohortState ())
  | None -> LocalDataRetention.CohortActivity.Finished

[<RequireQualifiedAccess>]
type LocalDataAction =
  | Status
  | Clear

module LocalDataAction =
  let parse (text: string) : Result<LocalDataAction, string> =
    match (if isNull text then "" else text.Trim().ToLowerInvariant()) with
    | "" | "status" -> Ok LocalDataAction.Status
    | "clear" -> Ok LocalDataAction.Clear
    | other -> Error (sprintf "Unknown action '%s'. Use status or clear." other)
