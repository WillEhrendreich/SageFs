module SageFs.Features.DaemonManifest

open System

/// A daemon session as recorded in the daemon.sagefm manifest.
type DaemonSessionRecord = {
  SessionId: string
  Projects: string list
  WorkingDir: string
  CreatedAt: DateTimeOffset
  StoppedAt: DateTimeOffset option
}

/// Daemon-level session state loaded from / saved to daemon.sagefm.
///
/// This is the durable, replayable record of daemon sessions — the binary
/// manifest is the sole source of truth for session resume. It is NOT an
/// event-sourced projection: per-session event sourcing was removed and the
/// daemon manifest replaced it. Type names are kept intentionally close to
/// the old "replay state" vocabulary so the migration is mechanical, but the
/// module name states the real story: a manifest, not an event replay.
type DaemonManifestState = {
  Sessions: Map<string, DaemonSessionRecord>
  ActiveSessionId: string option
}

/// What daemon startup does with a session the manifest says was alive.
[<RequireQualifiedAccess>]
type ResumeDecision =
  | Resume of projects: string list
  /// Gone for good — forgotten instead of retried (and warned about) on every start.
  | Forget of reason: string

module ResumeDecision =
  /// A deleted directory, or every project deleted, can never start again; one
  /// deleted project among several drops just that project.
  let decide
    (directoryExists: string -> bool)
    (fileExists: string -> bool)
    (record: DaemonSessionRecord)
    : ResumeDecision =
    match directoryExists record.WorkingDir with
    | false -> ResumeDecision.Forget (sprintf "its directory %s no longer exists" record.WorkingDir)
    | true ->
      let resolve (project: string) =
        match IO.Path.IsPathRooted project with
        | true -> project
        | false -> IO.Path.Combine(record.WorkingDir, project)
      let present, gone = record.Projects |> List.partition (resolve >> fileExists)
      match present, gone with
      | [], [] -> ResumeDecision.Resume []
      | [], _ -> ResumeDecision.Forget (sprintf "its project(s) no longer exist: %s" (String.concat ", " gone))
      | _ -> ResumeDecision.Resume present

module DaemonManifestState =
  let empty : DaemonManifestState = {
    Sessions = Map.empty
    ActiveSessionId = None
  }

  /// Sessions that are still alive (StoppedAt = None).
  let aliveSessions (state: DaemonManifestState) : DaemonSessionRecord list =
    state.Sessions
    |> Map.values
    |> Seq.filter (fun r -> r.StoppedAt.IsNone)
    |> Seq.toList

/// How a live-session sync treats the sessions running right now.
[<RequireQualifiedAccess>]
type LiveSync =
  /// The daemon keeps running: live sessions stay alive (StoppedAt = None).
  | Running
  /// The daemon is shutting down: live sessions are stamped stopped at the sync time.
  | ShuttingDown

/// One change to the daemon manifest. The manifest owner applies these one at
/// a time, in arrival order, to the only in-memory copy of the manifest, so
/// two writers can never interleave a read-merge-write and lose an update.
[<RequireQualifiedAccess>]
type ManifestMutation =
  /// Reconcile with the sessions live right now (periodic save, shutdown save).
  | SyncLive of live: DaemonSessionRecord list * activeSessionId: string option * at: DateTimeOffset * mode: LiveSync
  /// Purge, or forget a session that can never resume: delete its entry entirely.
  | Remove of sessionId: string
  /// `--prune`: stamp every still-alive entry stopped.
  | StampAllStopped of at: DateTimeOffset

module ManifestMutation =
  let private stopAt (at: DateTimeOffset) (record: DaemonSessionRecord) =
    match record.StoppedAt with
    | Some _ -> record
    | None -> { record with StoppedAt = Some at }

  let private syncLive
    (live: DaemonSessionRecord list)
    (activeSessionId: string option)
    (at: DateTimeOffset)
    (mode: LiveSync)
    (state: DaemonManifestState)
    : DaemonManifestState =
    let stampLive (record: DaemonSessionRecord) =
      match mode with
      | LiveSync.Running -> { record with StoppedAt = None }
      | LiveSync.ShuttingDown -> { record with StoppedAt = Some at }
    let liveById = live |> List.map (fun r -> r.SessionId, r) |> Map.ofList
    let reconciled =
      state.Sessions
      |> Map.map (fun sid record ->
        match liveById.ContainsKey sid with
        | true -> stampLive record
        // Alive in the manifest but not running: it crashed or vanished without
        // a stop. Stamp it so resume does not treat it as forever-alive; an
        // entry that was already stopped keeps its original timestamp.
        | false -> stopAt at record)
    let sessions =
      liveById
      |> Map.fold (fun acc sid record ->
        match Map.containsKey sid acc with
        | true -> acc
        | false -> Map.add sid (stampLive record) acc) reconciled
    { Sessions = sessions; ActiveSessionId = activeSessionId }

  let private remove (sessionId: string) (state: DaemonManifestState) : DaemonManifestState =
    match Map.containsKey sessionId state.Sessions with
    | false -> state
    | true ->
      let remaining = Map.remove sessionId state.Sessions
      { Sessions = remaining
        ActiveSessionId =
          match state.ActiveSessionId = Some sessionId with
          | true -> remaining |> Map.keys |> Seq.tryHead
          | false -> state.ActiveSessionId }

  /// The manifest after one mutation. Pure: the owner's only decision logic.
  let apply (mutation: ManifestMutation) (state: DaemonManifestState) : DaemonManifestState =
    match mutation with
    | ManifestMutation.SyncLive (live, activeSessionId, at, mode) -> syncLive live activeSessionId at mode state
    | ManifestMutation.Remove sessionId -> remove sessionId state
    | ManifestMutation.StampAllStopped at -> { state with Sessions = state.Sessions |> Map.map (fun _ r -> stopAt at r) }
