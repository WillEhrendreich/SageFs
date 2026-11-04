namespace SageFs.Simulation

open System
open SageFs

/// Deterministic Simulation Testing for `SseReplay`: editors that connect to `/events`, lose the
/// connection, and come back with the last id they saw, while the daemon keeps publishing,
/// drops old frames to stay inside its limits, and sometimes restarts. Same rules as the other
/// DST harnesses here: chaos is data (a seeded event list), the real `SseReplay.catchUp` is the
/// subject, and each twin breaks exactly one guarantee so its invariant is shown to have teeth.
///
/// A client answers a `Resync` the way the daemon does, with a snapshot of everything up to the
/// moment it attached. What a snapshot cannot carry (a transient eval event, say) is outside this
/// model, and `docs/` lists those frames.
module SseReplaySim =

  [<RequireQualifiedAccess>]
  type ReplayBehavior =
    | Real
    /// Reads only the seq of the id and takes the epoch to be this run's, so an id from before a
    /// restart replays as if it were from now.
    | IgnoresEpochTwin
    /// Starts the replay at the frame the client already has.
    | ReplaysTheLastSeenTwin
    /// When the frame the client wanted was dropped, replays whatever is left and says nothing.
    | ForgetsEvictionTwin
    /// Replays every session's frames to a stream that asked for one.
    | IgnoresScopeTwin
    /// Works out what the client missed in one step and registers it for live frames in a second, with
    /// nothing holding the daemon still between them. `SseLog.Attach` does both under its lock, which is
    /// the whole reason for the lock: a frame published in the gap is in neither the replay nor the live
    /// feed, so the client never knows it missed it.
    | TwoStepAttachTwin

  [<RequireQualifiedAccess>]
  type SimEvent =
    /// The daemon sends a frame of `payloadBytes` of wire text, whose news is `scope`'s.
    | Publish of scope: FrameScope * payloadBytes: int
    /// The client's connection drops. It remembers the last id it saw.
    | Disconnect of client: int
    /// The client (re)attaches with the last id it saw. A reset of a live connection too.
    | Reconnect of client: int
    /// The daemon process restarts: a new epoch, an empty buffer, and every connection gone.
    | RestartDaemon
    /// The first half of an attach: what the client missed is worked out. Only a behavior whose attach is
    /// not atomic treats it as half; for every other it is a whole `Reconnect` and `AttachEnd` does nothing.
    | AttachBegin of client: int
    /// The second half: the client is registered for live frames and given what `AttachBegin` found.
    | AttachEnd of client: int

  type Scenario = { Seed: int; Limits: ReplayLimits; Events: SimEvent list }

  /// An attach that has worked out what the client missed and not yet registered it.
  type AttachInFlight = { CatchUp: SseReplay.CatchUp; Epoch: string; Latest: int64 }

  /// What a client holds of one run: everything from `Epoch` up to `Through` arrived as state.
  type Snapshot = { Epoch: string; Through: int64 }

  type Client =
    { Stream: StreamScope
      Connected: bool
      LastId: EventId option
      /// Every frame delivered, live or replayed, in the order it arrived.
      Received: Replayable list
      Snapshots: Snapshot list
      /// Set between `AttachBegin` and `AttachEnd` of a behavior whose attach is two steps.
      Attaching: AttachInFlight option }

  type State =
    { Step: int
      Run: int
      Buffer: SseReplay.Buffer
      Clients: Map<int, Client>
      /// Everything the daemon ever sent, in order, kept or not. The ground truth.
      Published: Replayable list }

  let streams = [| StreamScope.EverySession; StreamScope.OnlySession "aaaaaaaa"; StreamScope.OnlySession "bbbbbbbb" |]
  let scopes = [| FrameScope.Daemon; FrameScope.Session "aaaaaaaa"; FrameScope.Session "bbbbbbbb" |]

  let epochOf (run: int) : string = sprintf "run-%d" run

  let initialOf (limits: ReplayLimits) : State =
    { Step = 0
      Run = 1
      Buffer = SseReplay.create (epochOf 1) limits
      Clients =
        streams
        |> Array.mapi (fun i stream ->
          i, { Stream = stream; Connected = false; LastId = None; Received = []; Snapshots = []; Attaching = None })
        |> Map.ofArray
      Published = [] }

  let withEpoch (epoch: string) (raw: string) : string =
    match EventId.tryParse raw with
    | Ok id -> EventId.toWire { id with Epoch = epoch }
    | Result.Error _ -> raw

  let oneSeqEarlier (raw: string) : string =
    match EventId.tryParse raw with
    | Ok id when id.Seq >= 2L -> EventId.toWire { id with Seq = id.Seq - 1L }
    | Ok _
    | Result.Error _ -> raw

  let catchUpOf (behavior: ReplayBehavior) : StreamScope -> string option -> SseReplay.Buffer -> SseReplay.CatchUp =
    match behavior with
    | ReplayBehavior.Real -> SseReplay.catchUp
    | ReplayBehavior.IgnoresEpochTwin ->
      fun stream last buffer -> SseReplay.catchUp stream (last |> Option.map (withEpoch buffer.Epoch)) buffer
    | ReplayBehavior.ReplaysTheLastSeenTwin ->
      fun stream last buffer -> SseReplay.catchUp stream (last |> Option.map oneSeqEarlier) buffer
    | ReplayBehavior.ForgetsEvictionTwin ->
      fun stream last buffer ->
        match SseReplay.catchUp stream last buffer with
        | SseReplay.CatchUp.Resync (SseReplay.ResyncReason.Evicted (_, first)) ->
          SseReplay.catchUp stream (Some (EventId.toWire { Epoch = buffer.Epoch; Seq = first - 1L })) buffer
        | other -> other
    | ReplayBehavior.IgnoresScopeTwin ->
      fun _ last buffer -> SseReplay.catchUp StreamScope.EverySession last buffer
    | ReplayBehavior.TwoStepAttachTwin -> SseReplay.catchUp

  /// Register the client for live frames and hand it what its catch-up found, as of `latest` in `epoch`.
  let complete (s: State) (clientId: int) (catchUp: SseReplay.CatchUp) (epoch: string) (latest: int64) : State =
    let client = s.Clients.[clientId]
    let client' =
      match catchUp with
      | SseReplay.CatchUp.Replay items ->
        { client with
            Connected = true
            Attaching = None
            LastId = items |> List.tryLast |> Option.map (fun r -> r.Id) |> Option.orElse client.LastId
            Received = client.Received @ items }
      | SseReplay.CatchUp.Resync _ ->
        { client with
            Connected = true
            Attaching = None
            LastId =
              (match latest with
               | 0L -> None
               | latest -> Some { Epoch = epoch; Seq = latest })
            Snapshots = client.Snapshots @ [ { Epoch = epoch; Through = latest } ] }
    { s with Clients = Map.add clientId client' s.Clients }

  let attach (behavior: ReplayBehavior) (s: State) (clientId: int) : State =
    let client = s.Clients.[clientId]
    let catchUp = catchUpOf behavior client.Stream (client.LastId |> Option.map EventId.toWire) s.Buffer
    complete s clientId catchUp s.Buffer.Epoch s.Buffer.Latest

  /// What the first half of a two-step attach finds, kept until the second half.
  let beginAttach (behavior: ReplayBehavior) (s: State) (clientId: int) : State =
    let client = s.Clients.[clientId]
    let inFlight =
      { CatchUp = catchUpOf behavior client.Stream (client.LastId |> Option.map EventId.toWire) s.Buffer
        Epoch = s.Buffer.Epoch
        Latest = s.Buffer.Latest }
    { s with Clients = Map.add clientId { client with Attaching = Some inFlight } s.Clients }

  /// A daemon restart drops every connection, so an attach that began in an earlier run is abandoned.
  let endAttach (s: State) (clientId: int) : State =
    match s.Clients.[clientId].Attaching with
    | Some inFlight when inFlight.Epoch = s.Buffer.Epoch -> complete s clientId inFlight.CatchUp inFlight.Epoch inFlight.Latest
    | Some _ -> { s with Clients = Map.add clientId { s.Clients.[clientId] with Attaching = None } s.Clients }
    | None -> s

  let step (behavior: ReplayBehavior) (s: State) (ev: SimEvent) : State =
    let s = { s with Step = s.Step + 1 }
    match ev with
    | SimEvent.Publish (scope, payloadBytes) ->
      let frame = { Scope = scope; Wire = String.replicate payloadBytes "z" }
      let item, buffer' = SseReplay.record frame s.Buffer
      let deliver (client: Client) =
        match client.Connected && SseFrame.visibleTo client.Stream frame with
        | true -> { client with LastId = Some item.Id; Received = client.Received @ [ item ] }
        | false -> client
      { s with Buffer = buffer'; Clients = s.Clients |> Map.map (fun _ c -> deliver c); Published = s.Published @ [ item ] }
    | SimEvent.Disconnect clientId ->
      { s with Clients = Map.add clientId { s.Clients.[clientId] with Connected = false } s.Clients }
    | SimEvent.Reconnect clientId -> attach behavior s clientId
    | SimEvent.AttachBegin clientId ->
      match behavior with
      | ReplayBehavior.TwoStepAttachTwin -> beginAttach behavior s clientId
      | _ -> attach behavior s clientId
    | SimEvent.AttachEnd clientId ->
      match behavior with
      | ReplayBehavior.TwoStepAttachTwin -> endAttach s clientId
      | _ -> s
    | SimEvent.RestartDaemon ->
      { s with
          Run = s.Run + 1
          Buffer = SseReplay.create (epochOf (s.Run + 1)) s.Buffer.Limits
          Clients = s.Clients |> Map.map (fun _ c -> { c with Connected = false }) }

  /// Pulls each `Reconnect` apart so the event that follows it lands between the two halves, which is
  /// the interleaving a single lock rules out. A `Reconnect` that is the last event is split with nothing
  /// between its halves.
  let rec splitAttaches (events: SimEvent list) : SimEvent list =
    match events with
    | SimEvent.Reconnect clientId :: next :: rest ->
      SimEvent.AttachBegin clientId :: next :: SimEvent.AttachEnd clientId :: splitAttaches rest
    | SimEvent.Reconnect clientId :: [] -> [ SimEvent.AttachBegin clientId; SimEvent.AttachEnd clientId ]
    | event :: rest -> event :: splitAttaches rest
    | [] -> []

  let trace (behavior: ReplayBehavior) (scenario: Scenario) : State list =
    let events =
      match behavior with
      | ReplayBehavior.TwoStepAttachTwin -> splitAttaches scenario.Events
      | _ -> scenario.Events
    events |> List.scan (step behavior) (initialOf scenario.Limits)

  let everyClient : int list = [ 0 .. streams.Length - 1 ]

  /// A pure function of `seed`. Every client attaches first and again at the end, so a scenario
  /// finishes with every client having asked for what it missed. Limits are small on purpose, so
  /// frames are dropped and some are bigger than the byte bound outright.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let limits = { MaxFrames = 1 + rng.Next 6; MaxBytes = 30 + rng.Next 250 }
    let n = 20 + rng.Next 40
    let events =
      [ for _ in 1 .. n ->
          match rng.Next 12 with
          | 0 | 1 | 2 | 3 | 4 | 5 -> SimEvent.Publish (scopes.[rng.Next scopes.Length], rng.Next 120)
          | 6 | 7 -> SimEvent.Disconnect (rng.Next streams.Length)
          | 8 | 9 | 10 -> SimEvent.Reconnect (rng.Next streams.Length)
          | _ -> SimEvent.RestartDaemon ]
    { Seed = seed
      Limits = limits
      Events = (everyClient |> List.map SimEvent.Reconnect) @ events @ (everyClient |> List.map SimEvent.Reconnect) }
