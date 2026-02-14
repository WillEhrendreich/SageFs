namespace SageFs

open System
open SageFs.WorkerProtocol

/// Pure, deterministic session routing and error types.
/// No side effects, no IO, no dependencies on transport or actor.
/// Shared vocabulary for ALL interfaces: MCP, HTTP, Neovim, CLI.
module SessionOperations =

  /// How a session was identified for an operation.
  [<RequireQualifiedAccess>]
  type SessionResolution =
    /// Caller explicitly specified a session ID and it was found.
    | Resolved of SessionId
    /// Only one session exists, so it's the obvious target.
    | DefaultSingle of SessionId
    /// Multiple sessions exist; the most recently active one is chosen.
    | DefaultMostRecent of SessionId

  /// Resolve which session to target for an operation.
  /// Pure function — takes the caller's optional sessionId and the current session list.
  ///
  /// Rules:
  /// 1. Explicit sessionId > single-session default > most-recently-active
  /// 2. If explicit sessionId is not found, error
  /// 3. If no sessions exist, error
  /// 4. If exactly one session, use it (no ambiguity)
  /// 5. If multiple sessions and no sessionId, pick most recently active
  let resolveSession
    (requestedId: SessionId option)
    (sessions: SessionInfo list)
    : Result<SessionResolution, SageFsError> =
    match requestedId with
    | Some id ->
      if sessions |> List.exists (fun s -> s.Id = id) then
        Result.Ok (SessionResolution.Resolved id)
      else
        Result.Error (SageFsError.SessionNotFound id)
    | None ->
      match sessions with
      | [] ->
        Result.Error SageFsError.NoActiveSessions
      | [ single ] ->
        Result.Ok (SessionResolution.DefaultSingle single.Id)
      | multiple ->
        let mostRecent =
          multiple
          |> List.sortByDescending (fun s -> s.LastActivity)
          |> List.head
        Result.Ok (SessionResolution.DefaultMostRecent mostRecent.Id)

  /// Extract the resolved session ID from any resolution variant.
  let sessionId (resolution: SessionResolution) : SessionId =
    match resolution with
    | SessionResolution.Resolved id
    | SessionResolution.DefaultSingle id
    | SessionResolution.DefaultMostRecent id -> id

  /// Format a resolution for display/logging.
  let describeResolution (resolution: SessionResolution) : string =
    match resolution with
    | SessionResolution.Resolved id ->
      sprintf "session %s (explicit)" id
    | SessionResolution.DefaultSingle id ->
      sprintf "session %s (only session)" id
    | SessionResolution.DefaultMostRecent id ->
      sprintf "session %s (most recently active)" id

  // ── Session display formatting ──────────────────────────────────

  /// What the caller wants when creating a session — pure data, no IO.
  type SessionCreateRequest = {
    Projects: string list
    WorkingDirectory: string
  }

  /// Human-readable relative time (e.g. "5 min ago", "just now").
  let private formatRelativeTime (now: DateTime) (past: DateTime) : string =
    let diff = now - past
    if diff.TotalSeconds < 60.0 then "just now"
    elif diff.TotalMinutes < 60.0 then sprintf "%d min ago" (int diff.TotalMinutes)
    elif diff.TotalHours < 24.0 then sprintf "%d hr ago" (int diff.TotalHours)
    else sprintf "%d days ago" (int diff.TotalDays)

  /// Format a single session for display.
  let formatSessionInfo (now: DateTime) (info: SessionInfo) : string =
    let name = SessionInfo.displayName info
    let projects = info.Projects |> String.concat ", "
    let lastActive = formatRelativeTime now info.LastActivity
    let pid =
      match info.WorkerPid with
      | Some p -> sprintf "(PID %d)" p
      | None -> "(no PID)"
    sprintf "%s  %s  %s  %s  %s\n  Started: %s  Last active: %s  Projects: %s"
      info.Id name info.WorkingDirectory (SessionStatus.label info.Status) pid
      (info.CreatedAt.ToString("yyyy-MM-dd HH:mm"))
      lastActive
      projects

  /// Format a list of sessions for display.
  let formatSessionList (now: DateTime) (sessions: SessionInfo list) : string =
    match sessions with
    | [] -> "No active sessions."
    | sessions ->
      sessions
      |> List.map (formatSessionInfo now)
      |> String.concat "\n\n"
      |> sprintf "%d active session(s):\n\n%s" sessions.Length
