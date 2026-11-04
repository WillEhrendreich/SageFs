namespace SageFs

open SageFs.Server
open WorkerProtocol

/// What the daemon last said about the session list: which sessions are live and which is active.
type SessionsAnnounced =
  { Live: Set<string>
    Active: ActiveSession }

/// What a delivery says about the model.
[<RequireQualifiedAccess>]
type ChangeCounts =
  /// The model changed, with this many output lines and diagnostics.
  | Counted of outputCount: int * diagCount: int
  /// Only the session list moved; subscribers have no model change to hear about.
  | SessionListOnly

/// One hand-over to the model-change subscribers: what the model changed to, and the session events that
/// came with it.
type ModelChangeDelivery =
  { Counts: ChangeCounts
    Lifecycle: SseEvent list }

module ModelChangeDelivery =
  /// Two deliveries that pile up become one. The newest counts win, because every handler reads the model
  /// when it runs, and a delivery that only moved the session list does not erase counts that are waiting.
  /// The lifecycle events all stay, in order: a session that stopped is no longer in the model, so nothing
  /// else would say it had.
  let merge (older: ModelChangeDelivery) (newer: ModelChangeDelivery) : ModelChangeDelivery =
    let counts =
      match newer.Counts with
      | ChangeCounts.Counted _ -> newer.Counts
      | ChangeCounts.SessionListOnly -> older.Counts
    { Counts = counts
      Lifecycle = older.Lifecycle @ newer.Lifecycle }

/// `SessionCreated`, `SessionStopped` and `SessionActivated` were declared, formatted and matched on, and
/// nothing triggered them, so a session going away reached a client only as a model change. A client that
/// was replayed frames for a session stopped during its gap was handed them back and never told the
/// session was gone.
///
/// They are derived from the registry the daemon already holds, so there is no second place that records
/// a session starting or stopping. The registry keeps a stopped session listed for a while, so a stop is
/// a status change as often as a removal; both are announced once.
module SessionLifecycle =

  let private isLive (session: SessionSnapshot) : bool =
    session.Status <> SessionDisplayStatus.Stopped

  let private projectNamesOf (session: SessionSnapshot) : string list =
    session.Projects |> List.map System.IO.Path.GetFileNameWithoutExtension

  let private liveOf (registry: SessionRegistryView) : Map<string, SessionSnapshot> =
    registry.Sessions
    |> List.filter isLive
    |> List.map (fun session -> SessionId.value session.Id, session)
    |> Map.ofList

  /// What changed since `before`, as events, and what to remember for the next look. The first look
  /// (`None`) announces nothing: the sessions already there are in a client's connect snapshot, and the
  /// events are for what happens after. Stops come first, then creations, then the new active session, so
  /// a client replaying them never holds more sessions than the daemon does.
  let observe (before: SessionsAnnounced option) (registry: SessionRegistryView) : SessionsAnnounced * SseEvent list =
    let now = liveOf registry
    let announced = { Live = now |> Map.keys |> Set.ofSeq; Active = registry.ActiveSessionId }
    match before with
    | None -> announced, []
    | Some previous ->
      let stopped =
        Set.difference previous.Live announced.Live
        |> Set.toList
        |> List.map SseEvent.SessionStopped
      let created =
        Set.difference announced.Live previous.Live
        |> Set.toList
        |> List.map (fun id -> SseEvent.SessionCreated (id, projectNamesOf now.[id]))
      let activated =
        match announced.Active with
        | ActiveSession.Viewing id when announced.Active <> previous.Active -> [ SseEvent.SessionActivated (SessionId.value id) ]
        | ActiveSession.Viewing _
        | ActiveSession.AwaitingSession -> []
      announced, stopped @ created @ activated
