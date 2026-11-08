namespace SageFs.Simulation

open System
open SageFs.McpBridge

/// DST for the stdio bridge's reconnect after a daemon upgrade, with the re-handshake NOT atomic.
///
/// `McpStdioBridgeSim` folds `McpBridge.decide` over events that arrive one at a time and treats every
/// action's effect as instant, so `Action.Rehandshake` was one step and "a stdin line arrives while the new
/// session is being minted" could not be written down. The real edge runs each batch of actions in a detached
/// task, so the line DOES arrive in that gap (roast 2026-11-08, finding 3): it was forwarded under the stale
/// id, the daemon answered 404, the heal minted a second session, two captures followed and two push streams
/// stayed open.
///
/// Here every effect is a scheduled completion with a latency, and a seeded schedule interleaves the
/// completions with stdin lines, the daemon dying and returning, push-stream ends and notifications. The world
/// is a model of the daemon: its incarnation, the sessions it knows (a new incarnation knows none), what the
/// bridge minted on each, which push streams are open. The subject is the REAL `McpBridge.decide`; the IO
/// model follows `McpStdioBridge.fs` (capture before the retry, one heal gate per bridge).
///
/// Chaos is data: a `Scenario` is a seed, a policy, latencies and a script. The same scenario replays to the
/// same trace.
module McpStdioBridgeReconnectSim =

  /// Something that happens outside the bridge, at a scripted tick.
  [<RequireQualifiedAccess>]
  type Outside =
    /// A stdin line.
    | ClientSends of RpcMessage
    /// The daemon process ends: its sessions and push streams go with it.
    | DaemonDies
    /// A new daemon answers on the port. It knows no session.
    | DaemonReturns
    /// The daemon pushes notification `tag` into every live session's open stream.
    | DaemonNotifies of tag: int

  /// What `forward` does when the daemon answers 404 for the id it holds.
  [<RequireQualifiedAccess>]
  type Heal =
    /// The bridge's heal gate: a heal that finds a session already minted since the id it tried reuses it.
    | Gated
    /// Every stale request mints its own.
    | Ungated

  /// How many ticks each effect takes, plus a seeded extra of up to `Jitter` ticks on each.
  type Latencies =
    { Probe: int
      Forward: int
      Rehandshake: int
      StreamEnd: int
      Jitter: int }

  type Scenario =
    { Seed: int
      Policy: Policy
      Heal: Heal
      Latencies: Latencies
      /// Ticks between a probe that found no daemon and the next one.
      ProbeEvery: int
      Script: (int * Outside) list }

  /// The daemon as the bridge cannot see it.
  type Daemon =
    { Incarnation: int
      Up: bool
      Live: Set<string>
      /// Sessions minted per incarnation, whoever asked.
      Minted: Map<int, int>
      Counter: int }

  /// One `ForwardToDaemon` the bridge decided, with what the world looked like at that moment.
  type ForwardRecord =
    { Msg: RpcMessage
      Sid: string option
      /// The bridge had seen a probe answer healthy since this daemon incarnation began.
      BridgeKnewDaemonWasBack: bool
      /// The daemon knew `Sid` when the request was dispatched.
      DaemonKnewSession: bool }

  type Trace =
    { Scenario: Scenario
      Final: State
      Daemon: Daemon
      Sent: RpcMessage list
      Forwards: ForwardRecord list
      /// One entry per answer the client got: delivered, or rejected with an error.
      Answered: RpcMessage list
      Captures: string list
      Actions: Action list
      /// Push streams open at the end, by session id.
      Streams: Set<string>
      MostStreamsOpen: int
      MostQueued: int
      /// Notification tag and how many open streams delivered it to the client.
      Notifications: (int * int) list }

  type Due = { At: int; Seq: int; Run: World -> World * Event list }

  and World =
    { Tick: int
      NextSeq: int
      Dues: Due list
      Bridge: State
      Daemon: Daemon
      /// The last incarnation in which a probe the bridge received answered healthy.
      ProbedUp: int option
      /// The id minted by the last heal or re-handshake: what the heal gate reuses.
      GateLatest: string option
      Streams: Set<string>
      Sent: RpcMessage list
      Forwards: ForwardRecord list
      Answered: RpcMessage list
      Captures: string list
      Actions: Action list
      MostStreamsOpen: int
      MostQueued: int
      Notifications: (int * int) list }

  let runWith (decideFn: Policy -> State -> Event -> State * Action list) (scenario: Scenario) : Trace =
    let rnd = Random(scenario.Seed)
    let lat (basis: int) : int =
      match scenario.Latencies.Jitter with
      | 0 -> basis
      | jitter -> basis + rnd.Next(0, jitter + 1)

    let schedule (delay: int) (run: World -> World * Event list) (w: World) : World =
      { w with
          Dues = { At = w.Tick + delay; Seq = w.NextSeq; Run = run } :: w.Dues
          NextSeq = w.NextSeq + 1 }

    let mint (w: World) : World * string =
      let sid = sprintf "s%d" w.Daemon.Counter
      let d = w.Daemon
      let mintedHere = 1 + (Map.tryFind d.Incarnation d.Minted |> Option.defaultValue 0)
      { w with
          Daemon =
            { d with
                Counter = d.Counter + 1
                Live = Set.add sid d.Live
                Minted = Map.add d.Incarnation mintedHere d.Minted } },
      sid

    // What the heal gate hands a caller that was refused under `stale`: the session the bridge already holds, if
    // it is newer than the refused one and the daemon still knows it.
    let reusable (w: World) (stale: string option) : string option =
      match scenario.Heal, w.GateLatest with
      | Heal.Gated, Some latest when Some latest <> stale && Set.contains latest w.Daemon.Live -> Some latest
      | _ -> None

    let completeForward (msg: RpcMessage) (sid: string option) (w: World) : World * Event list =
      match w.Daemon.Up, sid with
      | false, _ -> w, [ Event.RequestRejected(msg, "the daemon became unreachable"); Event.HttpFailed "unreachable" ]
      | true, Some s when Set.contains s w.Daemon.Live -> { w with Answered = msg :: w.Answered }, []
      | true, _ ->
        // The daemon answered 404 for the id the request carried: the heal.
        match reusable w sid with
        | Some existing -> { w with Answered = msg :: w.Answered }, [ Event.SessionIdCaptured existing ]
        | None ->
          let minted, fresh = mint w
          { minted with GateLatest = Some fresh; Answered = msg :: minted.Answered }, [ Event.SessionIdCaptured fresh ]

    let dispatch (sidNow: string option) (w: World) (action: Action) : World =
      match action with
      | Action.Probe -> schedule (lat scenario.Latencies.Probe) (fun w -> w, [ Event.ProbeResult w.Daemon.Up ]) w
      | Action.ForwardToDaemon msg ->
        let record =
          { Msg = msg
            Sid = sidNow
            BridgeKnewDaemonWasBack = (w.ProbedUp = Some w.Daemon.Incarnation && w.Daemon.Up)
            DaemonKnewSession =
              (match sidNow with
               | Some s -> Set.contains s w.Daemon.Live
               | None -> false) }
        schedule (lat scenario.Latencies.Forward) (completeForward msg sidNow) { w with Forwards = record :: w.Forwards }
      | Action.Rehandshake stale ->
        schedule
          (lat scenario.Latencies.Rehandshake)
          (fun w ->
            match w.Daemon.Up, reusable w (Some stale) with
            | false, _ -> w, [ Event.RehandshakeFailed "the daemon is down" ]
            | true, Some existing -> w, [ Event.SessionIdCaptured existing ]
            | true, None ->
              let minted, fresh = mint w
              { minted with GateLatest = Some fresh }, [ Event.SessionIdCaptured fresh ])
          w
      | Action.OpenPushStream(sid, succession) ->
        let withoutReplaced =
          match succession with
          | StreamSuccession.Replaces replaced -> Set.remove replaced w.Streams
          | StreamSuccession.First -> w.Streams
        match w.Daemon.Up && Set.contains sid w.Daemon.Live with
        | true ->
          let streams = Set.add sid withoutReplaced
          { w with Streams = streams; MostStreamsOpen = max w.MostStreamsOpen (Set.count streams) }
        | false ->
          schedule (lat scenario.Latencies.StreamEnd) (fun w -> w, [ Event.ServerStreamClosed(sid, "stream closed") ]) { w with Streams = withoutReplaced }
      | Action.RejectMessage(msg, _) -> { w with Answered = msg :: w.Answered }
      | Action.TerminateSession sid -> { w with Daemon = { w.Daemon with Live = Set.remove sid w.Daemon.Live } }
      | Action.StartDaemon
      | Action.ReportFatal _
      | Action.Shutdown -> w

    let feed (ev: Event) (w0: World) : World =
      let w1 =
        match ev with
        | Event.ProbeResult true -> { w0 with ProbedUp = Some w0.Daemon.Incarnation }
        | Event.SessionIdCaptured sid -> { w0 with Captures = sid :: w0.Captures }
        | _ -> w0
      let state', actions = decideFn scenario.Policy w1.Bridge ev
      let sidNow =
        match state'.Transport with
        | TransportState.Ready sid -> sid
        | _ -> None
      let w2 =
        { w1 with
            Bridge = state'
            Actions = List.rev actions @ w1.Actions
            MostQueued = max w1.MostQueued (Backlog.count state'.Pending) }
      // As the IO edge does: an unhealthy answer while waiting schedules the next probe.
      let w3 =
        match ev, state'.Transport with
        | Event.ProbeResult false, TransportState.AwaitingDaemon _ -> schedule scenario.ProbeEvery (fun w -> w, [ Event.Probe ]) w2
        | _ -> w2
      List.fold (dispatch sidNow) w3 actions

    let outside (what: Outside) : World -> World * Event list =
      match what with
      | Outside.ClientSends msg -> fun w -> { w with Sent = msg :: w.Sent }, [ Event.StdinLine msg ]
      | Outside.DaemonDies ->
        fun w ->
          let ending = w.Streams
          let dead =
            { w with
                Daemon = { w.Daemon with Up = false; Live = Set.empty }
                Streams = Set.empty }
          ending
          |> Set.fold
            (fun acc sid -> schedule (lat scenario.Latencies.StreamEnd) (fun w -> w, [ Event.ServerStreamClosed(sid, "stream closed") ]) acc)
            dead,
          []
      | Outside.DaemonReturns ->
        fun w ->
          { w with Daemon = { w.Daemon with Up = true; Incarnation = w.Daemon.Incarnation + 1; Live = Set.empty } }, []
      | Outside.DaemonNotifies tag ->
        fun w ->
          let copies =
            match w.Daemon.Up with
            | true -> w.Streams |> Set.filter (fun s -> Set.contains s w.Daemon.Live) |> Set.count
            | false -> 0
          { w with Notifications = (tag, copies) :: w.Notifications }, []

    let firstSession = "s0"
    let initialWorld =
      { Tick = 0
        NextSeq = 0
        Dues = []
        Bridge = { Transport = TransportState.Ready(Some firstSession); Pending = Backlog.empty }
        Daemon =
          { Incarnation = 1
            Up = true
            Live = Set.singleton firstSession
            Minted = Map.ofList [ 1, 1 ]
            Counter = 1 }
        ProbedUp = None
        GateLatest = None
        Streams = Set.singleton firstSession
        Sent = []
        Forwards = []
        Answered = []
        Captures = []
        Actions = []
        MostStreamsOpen = 1
        MostQueued = 0
        Notifications = [] }

    let withScript =
      scenario.Script
      |> List.fold (fun w (at, what) -> { w with Dues = { At = at; Seq = w.NextSeq; Run = outside what } :: w.Dues; NextSeq = w.NextSeq + 1 }) initialWorld

    let lastScripted = scenario.Script |> List.map fst |> List.fold max 0
    // A bridge that never settles would run forever on its own probe timer; this is the bound.
    let horizon = lastScripted + 200

    let shuffled (xs: Due list) : Due list =
      match scenario.Latencies.Jitter with
      | 0 -> xs
      | _ -> xs |> List.map (fun x -> rnd.Next(), x) |> List.sortBy fst |> List.map snd

    let rec loop (w: World) : World =
      let due, later = w.Dues |> List.partition (fun d -> d.At <= w.Tick)
      // The script is the world's own timeline and keeps its order; only the completions of the bridge's effects,
      // which really do race one another, are shuffled.
      let scripted, completions = due |> List.sortBy (fun d -> d.Seq) |> List.partition (fun d -> d.Seq < List.length scenario.Script)
      let ordered = scripted @ shuffled completions
      let ran =
        ordered
        |> List.fold
          (fun acc d ->
            let after, events = d.Run acc
            List.fold (fun a ev -> feed ev a) after events)
          { w with Dues = later }
      match ran.Tick >= horizon || (ran.Tick > lastScripted && List.isEmpty ran.Dues) with
      | true -> ran
      | false -> loop { ran with Tick = ran.Tick + 1 }

    let final = loop withScript
    { Scenario = scenario
      Final = final.Bridge
      Daemon = final.Daemon
      Sent = List.rev final.Sent
      Forwards = List.rev final.Forwards
      Answered = List.rev final.Answered
      Captures = List.rev final.Captures
      Actions = List.rev final.Actions
      Streams = final.Streams
      MostStreamsOpen = final.MostStreamsOpen
      MostQueued = final.MostQueued
      Notifications = List.rev final.Notifications }

  /// Run through the REAL `McpBridge.decide`.
  let run (scenario: Scenario) : Trace = runWith decide scenario

  /// Named invariants. Each is claimed to be a true property of the real bridge.
  type Violation = { Id: string; Message: string }

  module Invariants =
    let oneSessionPerIncarnation (t: Trace) : Violation list =
      t.Daemon.Minted
      |> Map.toList
      |> List.filter (fun (_, minted) -> minted > 1)
      |> List.map (fun (incarnation, minted) ->
        { Id = "one-session-per-daemon-incarnation"
          Message = sprintf "daemon incarnation %d minted %d sessions for one bridge (captures: %A)" incarnation minted t.Captures })

    let neverTwoPushStreams (t: Trace) : Violation list =
      match t.MostStreamsOpen > 1 with
      | true ->
        [ { Id = "never-two-push-streams"
            Message = sprintf "%d push streams were open at once, so every notification reached the client twice" t.MostStreamsOpen } ]
      | false -> []

    let eachNotificationOnce (t: Trace) : Violation list =
      t.Notifications
      |> List.filter (fun (_, copies) -> copies > 1)
      |> List.map (fun (tag, copies) ->
        { Id = "each-notification-at-most-once"
          Message = sprintf "notification %d reached the client %d times" tag copies })

    let noRequestUnderUnknownSession (t: Trace) : Violation list =
      t.Forwards
      |> List.filter (fun f -> f.BridgeKnewDaemonWasBack && not f.DaemonKnewSession)
      |> List.map (fun f ->
        { Id = "no-request-under-a-session-the-new-daemon-does-not-know"
          Message =
            sprintf
              "%s was forwarded under %A after the bridge had seen the new daemon answer healthy, and the daemon knew no such session"
              (rawOf f.Msg)
              f.Sid })

    let everyLineAnsweredOnce (t: Trace) : Violation list =
      t.Sent
      |> List.choose (fun msg ->
        match t.Answered |> List.filter ((=) msg) |> List.length with
        | 1 -> None
        | n ->
          Some
            { Id = "every-line-answered-exactly-once"
              Message = sprintf "%s was answered %d times (delivered or rejected)" (rawOf msg) n })

    let queueWithinCap (t: Trace) : Violation list =
      match t.MostQueued > t.Scenario.Policy.QueueCap with
      | true ->
        [ { Id = "queue-within-cap"
            Message = sprintf "the bridge held %d messages, the cap is %d" t.MostQueued t.Scenario.Policy.QueueCap } ]
      | false -> []

    /// With the daemon up at the end of the script and every effect settled, the bridge is Ready on a session
    /// the daemon knows, and exactly that session's push stream is open.
    let settledOnOneStream (t: Trace) : Violation list =
      match t.Final.Transport, t.Daemon.Up with
      | TransportState.Ready(Some sid), true when Set.contains sid t.Daemon.Live && t.Streams = Set.singleton sid -> []
      | other, _ ->
        [ { Id = "settled-on-one-session-and-one-stream"
            Message = sprintf "ended in %A with streams %A against live sessions %A" other t.Streams t.Daemon.Live } ]

    let all: (Trace -> Violation list) list =
      [ oneSessionPerIncarnation
        neverTwoPushStreams
        eachNotificationOnce
        noRequestUnderUnknownSession
        everyLineAnsweredOnce
        queueWithinCap
        settledOnOneStream ]

  let violations (t: Trace) : Violation list = Invariants.all |> List.collect (fun check -> check t)

  /// Twins: the bridge as it was before the fix, reproduced by changing the smallest thing. Each must violate
  /// the invariant named beside it, or the invariant has no teeth.
  module Twin =
    /// Before P17: the daemon answering moved the bridge straight to `Ready` on the stale id and queued the
    /// re-handshake in front of the drained messages, and a new session never cancelled the old push stream.
    let decideAtomicRehandshake (policy: Policy) (state: State) (event: Event) : State * Action list =
      match state.Transport, event with
      | TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(_, HeldSession.Id stale)), Event.ProbeResult true ->
        { Transport = TransportState.Ready(Some stale); Pending = Backlog.empty },
        Action.Rehandshake stale :: (state.Pending |> Backlog.toList |> List.map Action.ForwardToDaemon)
      | _ ->
        let next, actions = decide policy state event
        next,
        actions
        |> List.map (function
          | Action.OpenPushStream(sid, _) -> Action.OpenPushStream(sid, StreamSuccession.First)
          | other -> other)

    /// The queue as it was: no cap.
    let decideUnbounded (policy: Policy) (state: State) (event: Event) : State * Action list =
      decide { policy with QueueCap = Int32.MaxValue } state event

  let runTwinAtomicRehandshake (scenario: Scenario) : Trace = runWith Twin.decideAtomicRehandshake scenario
  let runTwinUnbounded (scenario: Scenario) : Trace = runWith Twin.decideUnbounded scenario

  // ── Scenarios ────────────────────────────────────────────────────────────

  let line (tag: int) : RpcMessage =
    RpcMessage.Request(RpcId.N(int64 tag), "tools/call", sprintf """{"jsonrpc":"2.0","id":%d,"method":"tools/call","tag":%d}""" tag tag)

  let quietLatencies = { Probe = 1; Forward = 1; Rehandshake = 4; StreamEnd = 1; Jitter = 0 }

  let bridgePolicy = { defaultPolicy with MaxProbeAttempts = 50; MaxReconnectProbes = 50 }

  /// The roast's case. The daemon dies and a new one answers at tick 2. The bridge's probe finds it at tick 2,
  /// the re-handshake takes four ticks, and a client line arrives at tick 3, in the gap.
  let lineDuringRehandshake: Scenario =
    { Seed = -1
      Policy = bridgePolicy
      Heal = Heal.Gated
      Latencies = quietLatencies
      ProbeEvery = 1
      Script =
        [ 0, Outside.DaemonDies
          2, Outside.DaemonReturns
          3, Outside.ClientSends(line 1)
          4, Outside.ClientSends(line 2)
          12, Outside.DaemonNotifies 1 ] }

  /// Two requests are in flight under an id the daemon forgot before the bridge heard the stream end: the
  /// daemon is back one tick after it died, and the stream's end takes ten ticks to arrive.
  let twoRequestsUnderAStaleId: Scenario =
    { Seed = -2
      Policy = bridgePolicy
      Heal = Heal.Gated
      Latencies = { quietLatencies with StreamEnd = 10; Forward = 2 }
      ProbeEvery = 1
      Script =
        [ 0, Outside.DaemonDies
          1, Outside.DaemonReturns
          2, Outside.ClientSends(line 1)
          2, Outside.ClientSends(line 2)
          30, Outside.DaemonNotifies 1 ] }

  /// Found by the seeded sweep (seed 218752) once the re-handshake held its queue: a request dispatched just before
  /// the daemon went away is still in flight when the new daemon answers, so its 404 heal and the bridge's own
  /// re-handshake both want a session. They have to share one.
  let healRacesRehandshake: Scenario =
    { Seed = -4
      Policy = bridgePolicy
      Heal = Heal.Gated
      Latencies = { Probe = 1; Forward = 3; Rehandshake = 3; StreamEnd = 2; Jitter = 0 }
      ProbeEvery = 1
      Script =
        [ 4, Outside.DaemonDies
          6, Outside.ClientSends(line 3)
          7, Outside.DaemonReturns
          9, Outside.ClientSends(line 2)
          25, Outside.DaemonNotifies 1 ] }

  /// A client that sends far more than the cap while the daemon is down.
  let floodWhileDown: Scenario =
    { Seed = -3
      Policy = { bridgePolicy with QueueCap = 8 }
      Heal = Heal.Gated
      Latencies = quietLatencies
      ProbeEvery = 1
      Script =
        [ yield 0, Outside.DaemonDies
          for i in 1..40 -> 3, Outside.ClientSends(line i)
          yield 6, Outside.DaemonReturns
          yield 40, Outside.DaemonNotifies 1 ] }

  /// A seeded scenario: one or two upgrades with the daemon back after 0 to 4 ticks, stdin lines scattered
  /// around them, notifications after.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let outages = 1 + rnd.Next(0, 2)
    // Each outage is (when the daemon dies, when a new one answers); the next starts well after.
    let spans =
      [ 1..outages ]
      |> List.fold
        (fun acc _ ->
          let died =
            match acc with
            | [] -> 2 + rnd.Next(0, 4)
            | (_, back) :: _ -> back + 15 + rnd.Next(0, 10)
          (died, died + rnd.Next(0, 5)) :: acc)
        []
      |> List.rev
    let lastBack = spans |> List.map snd |> List.fold max 0
    let script =
      [ for index, (died, back) in List.indexed spans do
          yield died, Outside.DaemonDies
          yield back, Outside.DaemonReturns
          for n in 1 .. rnd.Next(1, 8) do
            yield died + rnd.Next(0, 12), Outside.ClientSends(line (index * 100 + n))
        for i in 1 .. rnd.Next(1, 4) do
          yield lastBack + 10 + rnd.Next(0, 10), Outside.DaemonNotifies i ]
      |> List.sortBy fst
    { Seed = seed
      Policy = { bridgePolicy with QueueCap = 2 + rnd.Next(0, 10) }
      Heal = Heal.Gated
      Latencies =
        { Probe = 1
          Forward = 1 + rnd.Next(0, 3)
          Rehandshake = 1 + rnd.Next(0, 6)
          StreamEnd = 1 + rnd.Next(0, 6)
          Jitter = rnd.Next(0, 3) }
      ProbeEvery = 1 + rnd.Next(0, 3)
      Script = script }
