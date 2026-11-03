namespace SageFs

open SageFs.WorkerProtocol

/// Which sessions a session-manager command took out of the registry, for `SessionManagerCallbacks.OnSessionRemoved`.
module SessionRemoval =

  /// Tell `onRemoved` about each session `before` lists and `after` does not. The loop calls this after every command,
  /// so a new way to remove a session needs nothing here. Most commands leave the map itself untouched, and those cost
  /// one reference check. A hook that throws is logged and the loop carries on.
  let report (onRemoved: SessionId -> unit) (before: Map<SessionId, 'session>) (after: Map<SessionId, 'session>) =
    match obj.ReferenceEquals(before, after) with
    | true -> ()
    | false ->
      for id in before.Keys do
        match Map.containsKey id after with
        | true -> ()
        | false ->
          (fun () -> onRemoved id)
          |> Guarded.call (fun ex ->
            Utils.Log.warn "[SessionManager] OnSessionRemoved threw for %s (continuing): %s" (SessionId.value id) ex.Message)
