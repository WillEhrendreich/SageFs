module SageFs.Tests.SessionLifecycleTests

// `SessionCreated`, `SessionStopped` and `SessionActivated` were declared, formatted and matched on in
// five places, and nothing ever triggered them. A stop reached a client only as a model change, and a
// replay that held frames for a session stopped during the gap handed them back to the client, which
// then had nothing that said the session was gone.
//
// They are derived from the registry the daemon already holds, by one pure function: what changed
// since the last time the daemon looked.

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server
open SageFs.WorkerProtocol

let private idOf (hex: string) : SessionId =
  match SessionId.validate hex with
  | Ok id -> id
  | Error e -> failwith e

let private a = idOf "0a0a0a0a"
let private b = idOf "0b0b0b0b"

let private snapshot (id: SessionId) (status: SessionDisplayStatus) : SessionSnapshot =
  { Id = id
    Name = None
    Projects = [ "/repo/Core/Core.fsproj"; "/repo/App/App.fsproj" ]
    Status = status
    LastActivity = DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)
    EvalCount = 0
    UpSince = DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)
    WorkingDirectory = "/repo" }

let private registry (sessions: SessionSnapshot list) (active: ActiveSession) : SessionRegistryView =
  { Sessions = sessions; ActiveSessionId = active; TotalEvals = 0; WatchStatus = None }

let private running (id: SessionId) = snapshot id SessionDisplayStatus.Running
let private stopped (id: SessionId) = snapshot id SessionDisplayStatus.Stopped

let private observeTwice (first: SessionRegistryView) (second: SessionRegistryView) : SseEvent list =
  let announced, _ = SessionLifecycle.observe None first
  SessionLifecycle.observe (Some announced) second |> snd

// ── Every registry a walk can pass through ──

/// What a session can be in a registry: not listed, listed and live, listed and stopped (the registry keeps
/// a stopped session for a while, so a stop is a status change as often as a removal).
type private Presence = Absent | Live | Stopped

let private presences = [ Absent; Live; Stopped ]

let private registryOf (presenceA: Presence) (presenceB: Presence) (active: ActiveSession) : SessionRegistryView =
  let listed (id: SessionId) (presence: Presence) =
    match presence with
    | Absent -> []
    | Live -> [ running id ]
    | Stopped -> [ stopped id ]
  registry (listed a presenceA @ listed b presenceB) active

let private actives = [ ActiveSession.AwaitingSession; ActiveSession.Viewing a; ActiveSession.Viewing b ]

let private everyRegistry : SessionRegistryView list =
  [ for pa in presences do
      for pb in presences do
        for active in actives -> registryOf pa pb active ]

let private liveIds (r: SessionRegistryView) : Set<string> =
  r.Sessions
  |> List.filter (fun s -> s.Status <> SessionDisplayStatus.Stopped)
  |> List.map (fun s -> SessionId.value s.Id)
  |> Set.ofList

let private replay (live: Set<string>) (events: SseEvent list) : Set<string> =
  events
  |> List.fold (fun set event ->
    match event with
    | SseEvent.SessionCreated (id, _) -> Set.add id set
    | SseEvent.SessionStopped id -> Set.remove id set
    | _ -> set) live

// ── What reaches /events ──

let private sseContext () : SageFs.Server.McpServer.SseContext =
  { // The subscription is made only when the daemon has both, as it always does.
    GetElmModel = Some (fun () -> SageFsModel.initial ())
    GetWarmupContext = Some (fun _ -> System.Threading.Tasks.Task.FromResult None)
    GetHotReloadState = None
    SseJsonOpts = Json.optionsOf Json.standard
    TestEventBroadcast = Event<SseFrame>()
    SessionEventBroadcast = Event<SseFrame>()
    Log = SseLog.startNew ()
    ServerTracker = SageFs.Server.McpServer.McpServerTracker()
    CohortOwner = None }

[<Tests>]
let wireTests =
  testList "session lifecycle events on the stream" [

    test "WHY — a lifecycle event becomes a session frame scoped to its session, so it is numbered and replayed" {
      let ctx = sseContext ()
      let frames = ResizeArray<SseFrame>()
      ctx.SessionEventBroadcast.Publish.Add frames.Add
      let changed = Event<SseEvent>()
      SageFs.Server.McpServer.wireSessionEventSubscription changed.Publish ctx |> ignore
      changed.Trigger (SseEvent.SessionStopped (SessionId.value a))
      changed.Trigger (SseEvent.SessionCreated (SessionId.value b, [ "App" ]))
      changed.Trigger (SseEvent.SessionActivated (SessionId.value b))
      frames |> Seq.map (fun f -> f.Scope) |> List.ofSeq
      |> Expect.equal "each is that session's news"
           [ FrameScope.Session (SessionId.value a); FrameScope.Session (SessionId.value b); FrameScope.Session (SessionId.value b) ]
      frames |> Seq.forall (fun f -> f.Wire.StartsWith "event: session\n")
      |> Expect.isTrue "on the session channel, not the state channel"
      frames.[0].Wire |> Expect.stringContains "names what happened" "session_stopped"
    }

    test "a lifecycle event reaches the replay log once, as a session frame and not also as a state frame" {
      let ctx = sseContext ()
      let changed = Event<SseEvent>()
      let held = ResizeArray<SseFrame>()
      // The two feeds the log is wired to, as `wireSseLog` wires them.
      ctx.SessionEventBroadcast.Publish.Add(fun frame -> ctx.Log.Publish frame |> ignore; held.Add frame)
      changed.Publish
      |> Observable.filter (fun change -> SseEvent.channel change = SseChannel.State)
      |> Observable.map SageFs.Server.McpServer.stateFrameOf
      |> Observable.add (fun frame -> ctx.Log.Publish frame |> ignore; held.Add frame)
      SageFs.Server.McpServer.wireSessionEventSubscription changed.Publish ctx |> ignore
      changed.Trigger (SseEvent.SessionStopped (SessionId.value a))
      changed.Trigger (SseEvent.ModelChanged (1, 0))
      held |> Seq.map (fun f -> f.Wire.Split('\n').[0]) |> List.ofSeq
      |> Expect.equal "one session frame for the stop, one state frame for the model change" [ "event: session"; "event: state" ]
    }
  ]

[<Tests>]
let tests =
  testList "session lifecycle events" [

    test "the first look announces nothing: sessions already there are in the connect snapshot" {
      let announced, events = SessionLifecycle.observe None (registry [ running a ] (ActiveSession.Viewing a))
      events |> Expect.isEmpty "a baseline is not news"
      announced.Live |> Expect.equal "but it is remembered" (Set.ofList [ SessionId.value a ])
    }

    test "a session that appears is announced as created, with its project names" {
      observeTwice (registry [] ActiveSession.AwaitingSession) (registry [ running a ] ActiveSession.AwaitingSession)
      |> Expect.equal "created, naming the projects without their path or extension" [ SseEvent.SessionCreated (SessionId.value a, [ "Core"; "App" ]) ]
    }

    test "a session that disappears is announced as stopped" {
      observeTwice (registry [ running a ] ActiveSession.AwaitingSession) (registry [] ActiveSession.AwaitingSession)
      |> Expect.equal "stopped" [ SseEvent.SessionStopped (SessionId.value a) ]
    }

    test "a session that stays listed but becomes Stopped is announced as stopped, once" {
      let r1 = registry [ running a ] ActiveSession.AwaitingSession
      let r2 = registry [ stopped a ] ActiveSession.AwaitingSession
      let announced1, _ = SessionLifecycle.observe None r1
      let announced2, events2 = SessionLifecycle.observe (Some announced1) r2
      events2 |> Expect.equal "stopped" [ SseEvent.SessionStopped (SessionId.value a) ]
      SessionLifecycle.observe (Some announced2) r2 |> snd |> Expect.isEmpty "still stopped is not news"
      SessionLifecycle.observe (Some announced2) (registry [] ActiveSession.AwaitingSession)
      |> snd
      |> Expect.isEmpty "and neither is its removal, which was announced when it stopped"
    }

    test "a change of active session is announced, and awaiting a session is not" {
      observeTwice (registry [ running a; running b ] (ActiveSession.Viewing a)) (registry [ running a; running b ] (ActiveSession.Viewing b))
      |> Expect.equal "activated b" [ SseEvent.SessionActivated (SessionId.value b) ]
      observeTwice (registry [ running a ] (ActiveSession.Viewing a)) (registry [] ActiveSession.AwaitingSession)
      |> Expect.equal "only the stop; nothing is active, so nothing is activated" [ SseEvent.SessionStopped (SessionId.value a) ]
    }

    test "nothing changed, nothing announced" {
      let r = registry [ running a; running b ] (ActiveSession.Viewing a)
      observeTwice r r |> Expect.isEmpty "a model change that left the session list alone"
    }

    test "a stop comes before the creation that follows it, so a client never holds more sessions than exist" {
      observeTwice (registry [ running a ] (ActiveSession.Viewing a)) (registry [ running b ] (ActiveSession.Viewing b))
      |> Expect.equal "stopped, created, activated"
           [ SseEvent.SessionStopped (SessionId.value a)
             SseEvent.SessionCreated (SessionId.value b, [ "Core"; "App" ])
             SseEvent.SessionActivated (SessionId.value b) ]
    }

    test "WHY — whatever sequence of registries the daemon passes through, replaying the events rebuilds the live sessions" {
      // A client that applies the events to the sessions it held at the first look ends with the sessions
      // the registry has, for every walk of three registries drawn from every combination.
      for first in everyRegistry do
        for second in everyRegistry do
          for third in everyRegistry do
            let announced1, _ = SessionLifecycle.observe None first
            let announced2, events2 = SessionLifecycle.observe (Some announced1) second
            let _, events3 = SessionLifecycle.observe (Some announced2) third
            let rebuilt = replay (liveIds first) (events2 @ events3)
            rebuilt
            |> Expect.equal (sprintf "after %A then %A then %A" (liveIds first) (liveIds second) (liveIds third)) (liveIds third)
    }

    test "WHY — deliveries that pile up keep the newest counts and every lifecycle event, in order" {
      let stop = SseEvent.SessionStopped (SessionId.value a)
      let create = SseEvent.SessionCreated (SessionId.value b, [ "App" ])
      let older = { Counts = Some (1, 1); Lifecycle = [ stop ] }
      let newer = { Counts = Some (2, 3); Lifecycle = [ create ] }
      ModelChangeDelivery.merge older newer
      |> Expect.equal "newest counts, both events" { Counts = Some (2, 3); Lifecycle = [ stop; create ] }
      ModelChangeDelivery.merge older { Counts = None; Lifecycle = [] }
      |> Expect.equal "a lifecycle-only delivery does not erase the counts waiting" { Counts = Some (1, 1); Lifecycle = [ stop ] }
    }

    test "WHY — the active session a client is told of is the registry's, whenever the registry has one" {
      for first in everyRegistry do
        for second in everyRegistry do
          let announced1, _ = SessionLifecycle.observe None first
          let announced2, _ = SessionLifecycle.observe (Some announced1) second
          match ActiveSession.sessionId second.ActiveSessionId with
          | Some active -> announced2.Active |> Expect.equal "remembered as active" (Some (SessionId.value active))
          | None -> ()
    }
  ]
