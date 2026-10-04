module SageFs.Tests.EventsStreamScopeTests

// /events took a `sessionId` query parameter nobody read. Ask for session B and you got
// session A's last eval diff replayed, and every session's frames after that, because the
// broadcasts were pre-formatted strings and the subscriber had nothing to filter on.
//
// A frame now says whose news it is (`SseFrame.Scope`), a connection says whose news it
// wants (`StreamScope`), and one function decides (`SseFrame.visibleTo`). These tests cover
// the decision on its own, and then the real /events route end to end: live broadcasts,
// the state stream, the replay on connect, and the 400/404 for a bad id.
//
// A connection with no `sessionId` is the firehose the editors use today. It keeps working.

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.ProjectLoading
open SageFs.Server
open SageFs.WorkerProtocol

// ── Fixtures ─────────────────────────────────────────────────────────────

let mkSessionId (hex: string) : SessionId =
  match SessionId.validate hex with
  | Ok id -> id
  | Error e -> failwith e

let sidA = mkSessionId "0a0b0c0d"
let sidB = mkSessionId "deadbeef"
let idA = SessionId.value sidA
let idB = SessionId.value sidB

let project : ClassifiedProject =
  { Path = "/repo/MyApp/MyApp.fsproj"; Role = ProjectRole.Executable; PackageRefs = []; LoadMode = LoadMode.Evaluated; Build = SageFs.BuildOptimization.Unoptimized }

let createdAt = DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc)

let mkSessionInfo (id: SessionId) : SessionInfo =
  { Id = id
    Name = None
    Projects = [ project.Path ]
    WorkingDirectory = "/repo/MyApp"
    SolutionRoot = None
    Status = SessionLifecycleStatus.Ready { Pid = 1; Port = Some 5000 }
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    CreatedAt = createdAt
    LastActivity = createdAt
    ActiveProject = None
    ProjectRoles = [ project ]
    App = AppRun.AppRunState.NotRunning; Rebuild = LastRebuild.NeverRebuilt; Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync }

/// Two sessions the daemon knows about. Anything else is unknown.
let fakeOps : SessionManagementOps =
  let known = [ mkSessionInfo sidA; mkSessionInfo sidB ]
  { SessionManagementOps.stub with
      GetAllSessions = fun () -> Task.FromResult known
      GetSessionInfo = fun sid -> Task.FromResult(known |> List.tryFind (fun s -> s.Id = sid)) }

/// A frame whose payload is a marker the assertions can search for.
let wireOf (marker: string) = sprintf "event: test\ndata: {\"marker\":\"%s\"}\n\n" marker

// ── The decision, on its own ─────────────────────────────────────────────

/// Small enough to enumerate in full, so the cases below are a proof over it, not a sample.
let sessionIdPool = [ "0a0b0c0d"; "deadbeef"; "11223344"; "abcdef01" ]

[<Tests>]
let visibilityTests = testList "SseFrame.visibleTo" [

  testCase "a connection with no sessionId sees every frame" <| fun _ ->
    for owner in sessionIdPool do
      SseFrame.session owner "x" |> SseFrame.visibleTo StreamScope.EverySession
      |> Expect.isTrue (sprintf "the firehose sees %s's frame" owner)
    SseFrame.daemon "x" |> SseFrame.visibleTo StreamScope.EverySession
    |> Expect.isTrue "the firehose sees the daemon's frame"

  testCase "a connection for one session sees that session's frames and the daemon's, never another session's (every pair)" <| fun _ ->
    for wanted in sessionIdPool do
      for owner in sessionIdPool do
        let stream = StreamScope.OnlySession wanted
        SseFrame.session owner "x" |> SseFrame.visibleTo stream
        |> Expect.equal (sprintf "a stream for %s, a frame of %s's" wanted owner) (wanted = owner)
      SseFrame.daemon "x" |> SseFrame.visibleTo (StreamScope.OnlySession wanted)
      |> Expect.isTrue (sprintf "a stream for %s still gets the daemon's frame" wanted)

  testCase "a frame about no session (None, empty, blank) is the daemon's, so a scoped stream still gets it" <| fun _ ->
    for id in [ None; Some ""; Some "   " ] do
      (SseFrame.ofSession id "x").Scope
      |> Expect.equal (sprintf "%A names no session" id) FrameScope.Daemon
]

[<Tests>]
let queryTests = testList "SseEvent.streamScopeOf" [

  testCase "no parameter, or an empty one, is the firehose" <| fun _ ->
    for raw in [ ""; null ] do
      SseEvent.streamScopeOf raw
      |> Expect.equal (sprintf "%A is every session" raw) (Ok StreamScope.EverySession)

  testCase "a well-formed id asks for that session" <| fun _ ->
    SseEvent.streamScopeOf idA
    |> Expect.equal "one session" (Ok (StreamScope.OnlySession idA))

  testCase "a malformed id is refused, never read as the firehose, and the refusal says what to do" <| fun _ ->
    match SseEvent.streamScopeOf "not-a-session" with
    | Ok scope -> failtestf "a bad id was accepted as %A" scope
    | Error refusal ->
      StreamScopeRefusal.status refusal |> Expect.equal "a malformed id is the caller's mistake" 400
      StreamScopeRefusal.message refusal |> Expect.stringContains "points at where the ids are" "list_sessions"

  testCase "an unknown session is a 404 that also says what to do" <| fun _ ->
    let refusal = StreamScopeRefusal.UnknownSession idB
    StreamScopeRefusal.status refusal |> Expect.equal "not found" 404
    StreamScopeRefusal.message refusal |> Expect.stringContains "names the session" idB
    StreamScopeRefusal.message refusal |> Expect.stringContains "points at where the ids are" "list_sessions"
]

[<Tests>]
let sseEventScopeTests = testList "SseEvent.scope" [

  testCase "events about one session are that session's, the global ones are the daemon's" <| fun _ ->
    let cases : (SseEvent * FrameScope) list =
      [ SseEvent.SessionReady sidB, FrameScope.Session idB
        SseEvent.HotReloadChanged sidB, FrameScope.Session idB
        SseEvent.WarmupProgress(sidB, 1, 3, "x"), FrameScope.Session idB
        SseEvent.SessionHealthChanged(idB, SessionHealth.Healthy), FrameScope.Session idB
        SseEvent.HotReloadSnapshot(idB, []), FrameScope.Session idB
        SseEvent.ModelChanged(1, 2), FrameScope.Daemon
        SseEvent.SystemAlarm("phase", "message"), FrameScope.Daemon
        SseEvent.SessionProgress, FrameScope.Daemon ]
    for evt, expected in cases do
      SseEvent.scope evt |> Expect.equal (sprintf "%A" evt) expected

  testCase "SseEvent.frame carries the same wire text as SseEvent.format, with the scope beside it" <| fun _ ->
    let evt = SseEvent.SessionHealthChanged(idB, SessionHealth.Healthy)
    let frame = SseEvent.frame evt
    frame.Wire |> Expect.equal "same wire" (SseEvent.format evt)
    frame.Scope |> Expect.equal "scoped to the session" (FrameScope.Session idB)
]

// ── The real route ───────────────────────────────────────────────────────

type Harness =
  { App: WebApplication
    BaseUrl: string
    State: Event<SseEvent>
    Session: Event<SseFrame>
    Test: Event<SseFrame> }

/// Stand up only `mapEventsRoute` on a bare Kestrel host. `staleSlot` is what the daemon's
/// single "last eval diff" replay slot happens to hold when a client connects.
let startServer (staleSlot: SseFrame option) = task {
  let builder = WebApplication.CreateBuilder([||])
  builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
  builder.Logging.ClearProviders() |> ignore

  let state = Event<SseEvent>()
  let session = Event<SseFrame>()
  let test = Event<SseFrame>()

  let config : McpServer.McpServerConfig =
    { DiagnosticsChanged = (Event<SageFs.Features.DiagnosticsStore.T>()).Publish
      StateChanged = Some state.Publish
      FrictionStore = None
      Port = 0
      BindHost = SageFs.SageFsConfig.LoopbackHost.Localhost
      OwnOrigins = SageFs.Server.HttpOriginGuard.OwnOrigins.ofPorts [ 0 ]
      SessionOps = fakeOps
      ElmRuntime = None
      GetWarmupContext = None
      GetHotReloadState = None
      SharedBindingScope = ref None
      SharedFeatureState = None
      ActivityTracker = SageFs.AgentActivityTracker.create ()
      LiveBindings = None
      CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
      GetDaemonHealth = fun () -> None }

  let mcpContext : McpContext =
    { FrictionStore = None
      DiagnosticsChanged = config.DiagnosticsChanged
      StateChanged = None
      SessionOps = fakeOps
      SessionMap = Collections.Concurrent.ConcurrentDictionary<string, string>()
      McpPort = 0
      Dispatch = None
      GetElmModel = None
      GetElmRegions = None
      GetWarmupContext = None
      GetFeatureState = None; RecordEval = None
      ActivityTracker = config.ActivityTracker
      LiveBindings = None
      CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
      GetDaemonHealth = fun () -> None
      GetProcessTelemetry = fun () -> None }

  let sseContext : McpServer.SseContext =
    { GetElmModel = None
      GetWarmupContext = None
      GetHotReloadState = None
      SseJsonOpts = JsonSerializerOptions()
      TestEventBroadcast = test
      SessionEventBroadcast = session
      ServerTracker = McpServer.McpServerTracker()
      CohortOwner = None }

  let rctx : McpServer.RouteContext =
    { Config = config
      McpContext = mcpContext
      SseContext = sseContext
      Dispatch = None
      GetElmRegions = None
      FsiBindings = ref Map.empty
      FeaturePushState =
        ref { SageFs.Features.FeatureHooks.FeaturePushState.empty with LastEvalDiffSse = staleSlot }
      LastFeatureOutputCount = ref 0
      LastEvalContext = ref None }

  let app = builder.Build()
  McpServer.mapEventsRoute app rctx
  do! app.StartAsync()
  let baseUrl =
    app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
    |> Seq.head
  return { App = app; BaseUrl = baseUrl; State = state; Session = session; Test = test }
}

let client = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)

/// Read lines until one contains `marker`, with a hard budget, and hand back everything seen.
let readUntil (reader: StreamReader) (marker: string) : Task<string> = task {
  use cts = new CancellationTokenSource(TestTimeouts.patienceInProcess)
  let seen = StringBuilder()
  let mutable found = false
  try
    while not found do
      let! line = reader.ReadLineAsync(cts.Token)
      match line with
      | null -> failwithf "the stream ended before '%s'. Saw:\n%s" marker (seen.ToString())
      | l ->
        seen.AppendLine l |> ignore
        found <- l.Contains marker
  with :? OperationCanceledException ->
    failwithf "timed out waiting for '%s'. Saw:\n%s" marker (seen.ToString())
  return seen.ToString()
}

/// Connect, and read through the replay up to the retry hint. The hint is written after every
/// live source is subscribed, so a frame triggered once this returns cannot be missed.
let connect (h: Harness) (query: string) = task {
  let! resp = client.GetAsync(h.BaseUrl + "/events" + query, HttpCompletionOption.ResponseHeadersRead)
  match int resp.StatusCode with
  | 200 ->
    let! body = resp.Content.ReadAsStreamAsync()
    let reader = new StreamReader(body)
    let! replay = readUntil reader "retry:"
    return Choice1Of2 (reader, replay)
  | status ->
    let! text = resp.Content.ReadAsStringAsync()
    return Choice2Of2 (status, text)
}

let open200 (h: Harness) (query: string) = task {
  match! connect h query with
  | Choice1Of2 opened -> return opened
  | Choice2Of2 (status, text) -> return failwithf "expected a stream, got %d: %s" status text
}

let withServer (staleSlot: SseFrame option) (body: Harness -> Task) = task {
  let! h = startServer staleSlot
  try do! body h
  finally (h.App :> IDisposable).Dispose()
}

let markerA = "MARKER-ALPHA"
let markerB = "MARKER-BRAVO"
let daemonMarker = "MARKER-DAEMON"
let sentinel = "MARKER-SENTINEL"

[<Tests>]
let routeTests = testList "GET /events?sessionId=" [

  testTask "a connection for A is sent A's broadcasts and the daemon's, and none of B's" {
    do! withServer None (fun h -> task {
      let! (reader: StreamReader), _ = open200 h (sprintf "?sessionId=%s" idA)
      // B first, so a filter that only dropped the LAST frame could not pass.
      h.Session.Trigger(SseFrame.session idB (wireOf markerB))
      h.Test.Trigger(SseFrame.session idB (wireOf (markerB + "-test")))
      h.Session.Trigger(SseFrame.session idA (wireOf markerA))
      h.Test.Trigger(SseFrame.session idA (wireOf (markerA + "-test")))
      h.Session.Trigger(SseFrame.daemon (wireOf daemonMarker))
      h.Session.Trigger(SseFrame.daemon (wireOf sentinel))
      let! seen = readUntil reader sentinel
      seen |> Expect.stringContains "A's session frame" markerA
      seen |> Expect.stringContains "A's test frame" (markerA + "-test")
      seen |> Expect.stringContains "a daemon-wide frame" daemonMarker
      Expect.isFalse "B's session frame leaked" (seen.Contains markerB)
      Expect.isFalse "B's test frame leaked" (seen.Contains (markerB + "-test"))
    })
  }

  testTask "a connection with no sessionId is still the firehose" {
    do! withServer None (fun h -> task {
      let! (reader: StreamReader), _ = open200 h ""
      h.Session.Trigger(SseFrame.session idA (wireOf markerA))
      h.Test.Trigger(SseFrame.session idB (wireOf markerB))
      h.Session.Trigger(SseFrame.daemon (wireOf sentinel))
      let! seen = readUntil reader sentinel
      seen |> Expect.stringContains "A's frame" markerA
      seen |> Expect.stringContains "B's frame" markerB
    })
  }

  testTask "the state stream is scoped too: B's hot reload is not A's news, a model change is" {
    do! withServer None (fun h -> task {
      let! (reader: StreamReader), _ = open200 h (sprintf "?sessionId=%s" idA)
      h.State.Trigger(SseEvent.HotReloadChanged sidB)
      h.State.Trigger(SseEvent.SystemAlarm("alarm-phase", "alarm-message"))
      h.Session.Trigger(SseFrame.daemon (wireOf sentinel))
      let! seen = readUntil reader sentinel
      seen |> Expect.stringContains "a daemon-wide state event reaches a scoped stream" "alarm-phase"
      Expect.isFalse "B's state event leaked" (seen.Contains idB)
    })
  }

  testTask "the replay on connect is A's: the stale last-eval-diff slot belongs to B, so it is not sent" {
    let staleForB = SseFrame.session idB (wireOf "STALE-DIFF-OF-B")
    do! withServer (Some staleForB) (fun h -> task {
      let! _, replay = open200 h (sprintf "?sessionId=%s" idA)
      Expect.isFalse "B's replayed eval diff leaked into A's stream" (replay.Contains "STALE-DIFF-OF-B")
    })
  }

  testTask "the replay slot is still sent to the session it belongs to, and to the firehose" {
    let forB = SseFrame.session idB (wireOf "DIFF-OF-B")
    do! withServer (Some forB) (fun h -> task {
      let! _, scoped = open200 h (sprintf "?sessionId=%s" idB)
      scoped |> Expect.stringContains "B's own stream gets B's slot" "DIFF-OF-B"
      let! _, everything = open200 h ""
      everything |> Expect.stringContains "the firehose gets it too" "DIFF-OF-B"
    })
  }

  testTask "the health replay is one verdict for A on a scoped stream, both on the firehose" {
    do! withServer None (fun h -> task {
      let! _, scoped = open200 h (sprintf "?sessionId=%s" idA)
      scoped |> Expect.stringContains "A's health" idA
      Expect.isFalse "B's health leaked into A's replay" (scoped.Contains idB)
      let! _, everything = open200 h ""
      everything |> Expect.stringContains "firehose has A" idA
      everything |> Expect.stringContains "firehose has B" idB
    })
  }

  testTask "a malformed sessionId is a 400 that says so, not a silent firehose" {
    do! withServer None (fun h -> task {
      match! connect h "?sessionId=not-a-session" with
      | Choice2Of2 (status, text) ->
        status |> Expect.equal "malformed id" 400
        text |> Expect.stringContains "names the problem" "sessionId"
      | Choice1Of2 _ -> failtest "a malformed id opened a stream"
    })
  }

  testTask "a well-formed id the daemon does not hold is a 404" {
    do! withServer None (fun h -> task {
      match! connect h "?sessionId=cafef00d" with
      | Choice2Of2 (status, text) ->
        status |> Expect.equal "unknown session" 404
        text |> Expect.stringContains "says what to do next" "list_sessions"
      | Choice1Of2 _ -> failtest "an unknown session opened a stream"
    })
  }
]
