namespace SageFs

/// Pure decision core for "which sessions' live testing does a file change reach".
///
/// A change to a file belongs to the sessions whose working directory contains it, and it reaches
/// a session's live testing exactly when that session has live testing on. Nothing else decides
/// it: not which session is primary, not which one the dashboard is viewing, not what any other
/// session has switched on. The earlier shape asked the primary cycle's activation first, so a
/// daemon with live testing off on the primary session and on for another one silently dropped
/// every save and every unsaved-buffer change for the other one.
///
/// Ownership reuses `LiveTestWatcherCore.sessionsForPath`, the same longest-matching-directory
/// resolution the file watcher uses to attribute `FileReloaded`, so the two cannot disagree about
/// who owns a path.
///
/// Top-level module so the SageFs.Simulation harness and FSI consumers resolve it by name.
module LiveTestFileRouting =

  open WorkerProtocol
  open SageFs.Features.LiveTesting

  /// Why a session exists, as far as the live loop is concerned.
  [<RequireQualifiedAccess>]
  type SessionPurpose =
    /// A session a person or an agent works in: a save in its directory and a keystroke in its buffer drive its live loop.
    | Working
    /// A session the cohort made to verify landings. The landing gate evaluates the rebased files into it and runs its
    /// tests itself, so a save in its worktree must not also drive its live loop: that loop confirms with a real build,
    /// the build swaps the worker, and a swap in the middle of a verification is a race the gate never asked for.
    | Verifying

  /// A session as the router sees it: where it lives on disk, and why it exists.
  type SessionRoot =
    { SessionId: SessionId
      WorkingDirectory: string
      Purpose: SessionPurpose }

  /// Where a file change goes.
  [<RequireQualifiedAccess>]
  type FileRoute =
    /// No session's working directory contains the file (it came from the daemon's fallback
    /// watch directory). The primary cycle takes it, which is the session-less behaviour.
    | SessionLess
    /// The sessions that own the file AND have live testing on. May be empty: the owners have
    /// live testing off, so the change reaches nobody.
    | ToSessions of SessionId list

  /// Every session with live testing on, in the order given. A session that has it off is dropped.
  let withLiveTestingOn
    (activationOf: SessionId -> LiveTestingActivation)
    (sessions: SessionId list)
    : SessionId list =
    sessions
    |> List.filter (fun sid ->
      match activationOf sid with
      | LiveTestingActivation.Active -> true
      | LiveTestingActivation.Inactive -> false)

  /// On when either is on. A file watcher exists for a session when the primary cycle is on (the
  /// global enable, which watches every session) or the session's own cycle is on.
  let eitherOn (first: LiveTestingActivation) (second: LiveTestingActivation) : LiveTestingActivation =
    match first, second with
    | LiveTestingActivation.Inactive, LiveTestingActivation.Inactive -> LiveTestingActivation.Inactive
    | LiveTestingActivation.Active, LiveTestingActivation.Active
    | LiveTestingActivation.Active, LiveTestingActivation.Inactive
    | LiveTestingActivation.Inactive, LiveTestingActivation.Active -> LiveTestingActivation.Active

  /// The sessions whose working directory is the most specific one containing `path`. Sessions
  /// that share that directory all own the path. A session with no working directory owns nothing.
  let owners (roots: SessionRoot list) (path: string) : SessionId list =
    let dirSessions =
      roots
      |> List.filter (fun root -> not (System.String.IsNullOrEmpty root.WorkingDirectory))
      |> List.fold
        (fun acc root ->
          let existing = acc |> Map.tryFind root.WorkingDirectory |> Option.defaultValue []
          acc |> Map.add root.WorkingDirectory (existing @ [ root.SessionId ]))
        Map.empty
    LiveTestWatcherCore.sessionsForPath dirSessions path

  /// The sessions among `sessions` whose live loop a save or a keystroke may drive: every one except a session known to be
  /// verifying. A verifying session still OWNS the files in its worktree (so they are never mistaken for session-less ones and
  /// handed to the primary), it just is not driven by them. A session the roots do not mention is a working one.
  let drivenByEdits (roots: SessionRoot list) (sessions: SessionId list) : SessionId list =
    sessions
    |> List.filter (fun sid ->
      roots
      |> List.exists (fun root ->
        root.SessionId = sid
        && (match root.Purpose with
            | SessionPurpose.Verifying -> true
            | SessionPurpose.Working -> false))
      |> not)

  /// Route one changed file. `activationOf` answers for a session's OWN live-testing cycle.
  let route
    (activationOf: SessionId -> LiveTestingActivation)
    (roots: SessionRoot list)
    (path: string)
    : FileRoute =
    match owners roots path with
    | [] -> FileRoute.SessionLess
    | owning -> FileRoute.ToSessions (withLiveTestingOn activationOf (drivenByEdits roots owning))
