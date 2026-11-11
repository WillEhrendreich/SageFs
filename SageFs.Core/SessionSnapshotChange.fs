namespace SageFs

open SageFs.WorkerProtocol

/// What a newly published session list says against the one before it, for `SessionManagerCallbacks.OnSessionsChanged`.
/// The manager publishes one snapshot per command, on its loop thread, and calls this once per publish after the snapshot is
/// readable, so whoever the callback wakes reads the new list. The callback carries no payload: the reader asks the snapshot.
[<RequireQualifiedAccess>]
type SnapshotChange =
  | Unchanged
  /// Some session differs: one came or went, or a status or another field of one moved.
  | SessionsChanged

module SnapshotChange =
  /// `LastActivity` is stamped by every call a caller makes to a worker (`TouchSession`), several a second while live
  /// tests run as you type, so a change in it alone is not a change worth a refresh. Every other field is compared.
  let withoutActivity (sessions: Map<SessionId, SessionInfo>) : Map<SessionId, SessionInfo> =
    sessions |> Map.map (fun _ info -> { info with LastActivity = System.DateTime.MinValue })

  /// Warmup progress, worker URLs and health are not the session list: progress has its own callback, a URL only changes
  /// with a status, and health is the supervisor's, so only the sessions are compared.
  let between (before: Map<SessionId, SessionInfo>) (after: Map<SessionId, SessionInfo>) : SnapshotChange =
    match withoutActivity before = withoutActivity after with
    | true -> SnapshotChange.Unchanged
    | false -> SnapshotChange.SessionsChanged

  /// Tell `onChanged` once when `after` differs from `before`. A subscriber that throws is logged and the loop carries on.
  let announce (onChanged: unit -> unit) (before: Map<SessionId, SessionInfo>) (after: Map<SessionId, SessionInfo>) : unit =
    match between before after with
    | SnapshotChange.Unchanged -> ()
    | SnapshotChange.SessionsChanged ->
      onChanged |> Guarded.call (fun ex -> Utils.Log.warn "[SessionManager] OnSessionsChanged threw (continuing): %s" ex.Message)
