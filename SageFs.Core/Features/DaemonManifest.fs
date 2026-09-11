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
