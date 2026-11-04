namespace SageFs

open SageFs.Server
open WorkerProtocol

/// What the daemon last said about the session list: which sessions are live and which is active.
type SessionsAnnounced =
  { Live: Set<string>
    Active: string option }

/// One hand-over to the model-change subscribers. `Counts` is the output and diagnostic count of a model
/// change (none when only the session list moved); `Lifecycle` is the session events that came with it.
type ModelChangeDelivery =
  { Counts: (int * int) option
    Lifecycle: SseEvent list }

module ModelChangeDelivery =
  /// Two deliveries that pile up become one. The newest counts win, because every handler reads the model
  /// when it runs. The lifecycle events all stay, in order: a session that stopped is no longer in the
  /// model, so nothing else would say it had.
  let merge (older: ModelChangeDelivery) (newer: ModelChangeDelivery) : ModelChangeDelivery =
    { Counts = newer.Counts |> Option.orElse older.Counts
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

  let private activeOf (registry: SessionRegistryView) : string option =
    ActiveSession.sessionId registry.ActiveSessionId |> Option.map SessionId.value

  /// What changed since `before`, as events, and what to remember for the next look. The first look
  /// (`None`) announces nothing: the sessions already there are in a client's connect snapshot, and the
  /// events are for what happens after. Stops come first, then creations, then the new active session, so
  /// a client replaying them never holds more sessions than the daemon does.
  let observe (before: SessionsAnnounced option) (registry: SessionRegistryView) : SessionsAnnounced * SseEvent list =
    let now = liveOf registry
    let announced = { Live = now |> Map.keys |> Set.ofSeq; Active = activeOf registry }
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
        | Some id when announced.Active <> previous.Active -> [ SseEvent.SessionActivated id ]
        | _ -> []
      announced, stopped @ created @ activated
