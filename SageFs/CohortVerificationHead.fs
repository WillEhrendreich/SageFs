/// Bringing a cohort's verification session to the head a landing must be verified against.
///
/// The cheap way respawns the worker and keeps the build. That cannot work for a landing that changed a
/// signature: the sources no longer type-check against the build bound over them, the eval answers FSI's
/// "earlier error", and retrying it changes nothing. Left there, the session stays wedged and every later
/// landing verifies against a session that holds nothing — the cohort is blocked for good.
///
/// The effects are ports, so the order and the escalation are readable here and provable without a daemon.
module SageFs.CohortVerificationHead

open SageFs.Features

/// What bringing the session to the head needs to do, supplied by the daemon.
type Ports =
  { /// The verification session.
    Session: string
    /// The head's sources as `(path, blob)`: what the session must end up holding.
    Snapshot: (string * string) list
    /// Respawn the worker, KEEPING the build it already has.
    Reset: string -> Async<Result<unit, string>>
    /// Park until the session reaches a trustworthy status.
    AwaitTrusted: string -> Async<Result<Features.Verification.SessionTrust.SessionObservation, string>>
    /// Did the respawned worker load the worktree's build, with its files bound as compiled values?
    BindBuild: string -> Async<bool>
    /// Bind the head's own text over the build, one file at a time.
    EvalSources: string -> string list -> Async<Result<unit, string>>
    /// Record that a file is bound to a blob, so the next landing can tell whether the head moved on.
    RecordBound: string -> string -> unit
    /// Rebuild the session's project and restart on what the build made.
    Rebuild: string -> Async<Result<string, SageFs.SageFsError>> }

/// Bring `ports.Snapshot`'s text into the session: rebind over the build it has, and if that cannot
/// work, pay for one rebuild. Answers which way it happened so the caller can say so.
let bring (ports: Ports) : Async<Result<CohortRebuildGate.Recovery, string>> =
  let record () =
    for (full, blob) in ports.Snapshot do
      ports.RecordBound full blob
  let rebind () =
    async {
      match! ports.Reset ports.Session with
      | Error reason -> return Error reason
      | Ok () ->
        match! ports.AwaitTrusted ports.Session with
        | Error reason -> return Error reason
        | Ok _ ->
          match! ports.BindBuild ports.Session with
          | false ->
            // Without the build's compiled values behind them, a binding of the head's text cannot be
            // typed against what the code it replaces is typed against, so the session would hold
            // neither the head nor anything to check the head against.
            return Error "the integration session could not be brought to the head — resubmit"
          | true ->
            match! ports.EvalSources ports.Session (ports.Snapshot |> List.map fst) with
            | Error reason -> return Error reason
            | Ok () ->
              record ()
              return Ok ()
    }
  let rebuild () =
    async {
      match! ports.Rebuild ports.Session with
      | Error err -> return Error(sprintf "the rebuild of the integration session failed: %s" (SageFsError.describeForAgent err))
      | Ok _ ->
        match! ports.AwaitTrusted ports.Session with
        | Error reason -> return Error reason
        | Ok _ ->
          // A rebuild leaves nothing evaluated over it: what the session holds is the worktree's own text.
          SageFs.McpCohortIntegration.forgetFilesEvaluatedOverBuild ()
          record ()
          return Ok ()
    }
  CohortRebuildGate.rebindOrRebuild rebind rebuild (fun cheap missed ->
    sprintf "%s; the rebuild that followed also failed: %s" cheap missed)
