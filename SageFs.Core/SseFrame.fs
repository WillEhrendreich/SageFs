namespace SageFs

/// Whose news a pushed SSE frame is.
[<RequireQualifiedAccess>]
type FrameScope =
  /// True for the whole daemon (a cohort moved, an alarm rang, a keepalive), so every
  /// connection gets it, however narrowly it asked.
  | Daemon
  /// About one session, by the id the frame's own payload carries.
  | Session of sessionId: string

/// What one `/events` connection asked to see.
[<RequireQualifiedAccess>]
type StreamScope =
  /// No `sessionId` query parameter: the firehose the editors connect to today.
  | EverySession
  /// `?sessionId=<id>`: this session's frames, and the daemon's.
  | OnlySession of sessionId: string

/// Why an `/events` request was refused. Named cases, not a string, so the status code and
/// the words come from one place and every refusal says what to do next.
[<RequireQualifiedAccess>]
type StreamScopeRefusal =
  /// `sessionId` is not an id at all (the reason is the validator's own).
  | NotASessionId of reason: string
  /// A well-formed id the daemon does not hold.
  | UnknownSession of sessionId: string

module StreamScopeRefusal =
  let status (refusal: StreamScopeRefusal) : int =
    match refusal with
    | StreamScopeRefusal.NotASessionId _ -> 400
    | StreamScopeRefusal.UnknownSession _ -> 404

  let message (refusal: StreamScopeRefusal) : string =
    match refusal with
    | StreamScopeRefusal.NotASessionId reason ->
      sprintf "sessionId is not a session id (%s). Pass the 8-character id from list_sessions, or leave sessionId off to stream every session." reason
    | StreamScopeRefusal.UnknownSession id ->
      sprintf "No session '%s' is running. Call list_sessions for the ids that are, or leave sessionId off to stream every session." id

/// A frame ready to write, with whose news it is beside it. The wire text is a pre-formatted
/// string, so the scope cannot be recovered from it afterwards: it has to travel with it.
type SseFrame = { Scope: FrameScope; Wire: string }

module SseFrame =
  let daemon (wire: string) : SseFrame = { Scope = FrameScope.Daemon; Wire = wire }

  let session (sessionId: string) (wire: string) : SseFrame =
    { Scope = FrameScope.Session sessionId; Wire = wire }

  /// For a payload built with an optional session id. No id (or a blank one) is not any
  /// session's news, so it is the daemon's.
  let ofSession (sessionId: string option) (wire: string) : SseFrame =
    match sessionId with
    | Some id when not (System.String.IsNullOrWhiteSpace id) -> session id wire
    | Some _
    | None -> daemon wire

  /// The one place that decides who gets a frame. Exhaustive on purpose: a new scope has to
  /// be answered here before anything compiles.
  let visibleTo (stream: StreamScope) (frame: SseFrame) : bool =
    match stream, frame.Scope with
    | StreamScope.EverySession, _ -> true
    | StreamScope.OnlySession _, FrameScope.Daemon -> true
    | StreamScope.OnlySession wanted, FrameScope.Session owner ->
      System.String.Equals(wanted, owner, System.StringComparison.Ordinal)
