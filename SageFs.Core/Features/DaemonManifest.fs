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
