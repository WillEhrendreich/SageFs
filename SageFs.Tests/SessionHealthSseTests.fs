module SageFs.Tests.SessionHealthSseTests

// Gap 1 (roast-8 §1): `SessionHealth.classify` was called from exactly two
// HTTP GET handlers (`/health`, `/api/sessions`) and referenced NOWHERE in
// SseWriter.fs/SseEvent.fs — a session going Healthy -> Degraded mid-session
// was invisible to every connected client until an unrelated refetch. These
// tests prove:
//   1. `SseEvent.SessionHealthChanged` is a real, correctly-shaped session-
//      channel event (pure serialization tests).
//   2. `wireSessionHealthSubscription` pushes ONLY on a genuine transition —
//      a deliberately-broken dedup gate (always push, or never push) makes
//      these fail.
//   3. `replayHealthSnapshot` gives a freshly-connecting client the CURRENT
//      health without waiting for the next transition.

open System
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.ProjectLoading
open SageFs.Server
open SageFs.Server.McpServer

// ── Fixtures ─────────────────────────────────────────────────────────────

let private handle : WorkerHandle = { Pid = 1; Port = Some 5000 }

let private project : ClassifiedProject =
  { Path = "/repo/MyApp/MyApp.fsproj"; Role = ProjectRole.Executable; PackageRefs = [] }

let private loadedAssembly : LoadedAssembly =
  { Name = "MyApp"; Path = "/bin/MyApp.dll"; NamespaceCount = 1; ModuleCount = 1 }

let private openedNamespace : WarmUp.OpenedBinding =
  { Name = "MyApp"; Kind = WarmUp.OpenableKind.Namespace; Source = "reflection"; DurationMs = 0.0 }

let private healthyWarmup : WarmupContext =
  { SourceFilesScanned = 1
    AssembliesLoaded = [ loadedAssembly ]
    NamespacesOpened = [ openedNamespace ]
    FailedOpens = []
    PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 10L }
    StartedAt = DateTimeOffset.UtcNow }

let private nothingLoadedWarmup : WarmupContext =
  { healthyWarmup with AssembliesLoaded = []; NamespacesOpened = [] }

let private at = DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc)

let private mkSessionId (hex: string) : SessionId =
  match SessionId.validate hex with
  | Ok id -> id
  | Error e -> failwith e

let private sessId = mkSessionId "0a0b0c0d"

let private mkSessionInfo () : SessionInfo =
  { Id = sessId
    Name = None
    Projects = [ project.Path ]
    WorkingDirectory = "/repo/MyApp"
    SolutionRoot = None
    Status = SessionLifecycleStatus.Ready handle
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    CreatedAt = at
    LastActivity = at
    ActiveProject = None
    ProjectRoles = [ project ]
    App = AppRun.AppRunState.NotRunning }

let private mkSseContext (getWarmup: string -> Task<WarmupContext option>) (broadcast: Event<string>) : SseContext =
  { GetElmModel = None
    GetWarmupContext = Some getWarmup
    GetHotReloadState = None
    SseJsonOpts = JsonSerializerOptions()
    TestEventBroadcast = Event<string>()
    SessionEventBroadcast = broadcast
    ServerTracker = McpServerTracker()
    CohortOwner = None }

// ── Group 1: pure SseEvent shape ────────────────────────────────────────

[<Tests>]
let sessionHealthEventShapeTests = testList "SseEvent.SessionHealthChanged" [

  testCase "rides the session channel" <| fun _ ->
    SseEvent.SessionHealthChanged("s1", SessionHealth.Healthy)
    |> SseEvent.channel
    |> Expect.equal "session channel" SseChannel.Session

  testCase "format starts with 'event: session' and ends with a blank line" <| fun _ ->
    let frame = SseEvent.format (SseEvent.SessionHealthChanged("s1", SessionHealth.Healthy))
    frame |> Expect.stringStarts "event prefix" "event: session\n"
    frame |> Expect.stringEnds "double newline" "\n\n"

  testCase "toJson carries the session id, type tag, and Healthy status with no reason" <| fun _ ->
    let json = SseEvent.toJson (SseEvent.SessionHealthChanged("abc123", SessionHealth.Healthy))
    use doc = JsonDocument.Parse(json)
    doc.RootElement.GetProperty("type").GetString() |> Expect.equal "type tag" "session_health_changed"
    doc.RootElement.GetProperty("sessionId").GetString() |> Expect.equal "session id" "abc123"
    let health = doc.RootElement.GetProperty("health")
    health.GetProperty("status").GetString() |> Expect.equal "status" "Healthy"
    health.TryGetProperty("reason") |> fst |> Expect.isFalse "Healthy carries no reason property (WhenWritingNull)"

  testCase "toJson carries the Degraded reason" <| fun _ ->
    let json = SseEvent.toJson (SseEvent.SessionHealthChanged("abc123", SessionHealth.Degraded "nothing loaded"))
    use doc = JsonDocument.Parse(json)
    let health = doc.RootElement.GetProperty("health")
    health.GetProperty("status").GetString() |> Expect.equal "status" "Degraded"
    health.GetProperty("reason").GetString() |> Expect.equal "reason" "nothing loaded"

  testCase "toJson carries the Failed reason" <| fun _ ->
    let json = SseEvent.toJson (SseEvent.SessionHealthChanged("abc123", SessionHealth.Failed "worker crashed"))
    use doc = JsonDocument.Parse(json)
    let health = doc.RootElement.GetProperty("health")
    health.GetProperty("status").GetString() |> Expect.equal "status" "Failed"
    health.GetProperty("reason").GetString() |> Expect.equal "reason" "worker crashed"
]

// ── Group 2: wireSessionHealthSubscription — transition-only push ───────

[<Tests>]
let wireSessionHealthSubscriptionTests = testList "wireSessionHealthSubscription" [

  testCase "pushes on the first classification, stays silent on repeats, pushes again only on a real transition" <| fun _ ->
    let stateChanged = Event<SseEvent>()
    let broadcast = Event<string>()
    let received = ResizeArray<string>()
    broadcast.Publish.Add(received.Add)

    // Every session starts Healthy; a mutable ref lets the fake flip it to
    // "nothing loaded" (Degraded) mid-test, simulating a live transition.
    let warmupRef = ref (Some healthyWarmup)
    let getWarmup (_: string) = Task.FromResult warmupRef.Value
    let getAllSessions () = Task.FromResult [ mkSessionInfo () ]
    let ctx = mkSseContext getWarmup broadcast

    use _sub = wireSessionHealthSubscription stateChanged.Publish ctx getAllSessions

    // Tick 1: Healthy, first time seen -> establishes the baseline, pushes once.
    stateChanged.Trigger SseEvent.SessionProgress
    received.Count |> Expect.equal "first tick pushes the baseline verdict" 1
    received.[0] |> Expect.stringContains "baseline is Healthy" "\"status\":\"Healthy\""

    // Tick 2: still Healthy -> the exact "never per tick" requirement. A
    // dedup gate that always pushes would fail this assertion.
    stateChanged.Trigger SseEvent.SessionProgress
    received.Count |> Expect.equal "unchanged health pushes nothing" 1

    // Tick 3: the session degrades (warmup now reports nothing loaded) -> a
    // dedup gate that never re-evaluates (or that never actually pushes at
    // all) would fail this assertion.
    warmupRef.Value <- Some nothingLoadedWarmup
    stateChanged.Trigger SseEvent.SessionProgress
    received.Count |> Expect.equal "a real transition pushes again" 2
    received.[1] |> Expect.stringContains "transitioned to Degraded" "\"status\":\"Degraded\""

    // Tick 4: stays Degraded -> still no further push.
    stateChanged.Trigger SseEvent.SessionProgress
    received.Count |> Expect.equal "repeated Degraded pushes nothing further" 2

  testCase "reacts to session-list changes too, not just one hardcoded event case" <| fun _ ->
    // The subscription recomputes on EVERY stateChanged occurrence (any
    // case can flip a session's usability), so a HotReloadChanged tick must
    // trigger the same dedup-gated recompute as a ModelChanged tick.
    let stateChanged = Event<SseEvent>()
    let broadcast = Event<string>()
    let received = ResizeArray<string>()
    broadcast.Publish.Add(received.Add)
    let getWarmup (_: string) = Task.FromResult (Some healthyWarmup)
    let getAllSessions () = Task.FromResult [ mkSessionInfo () ]
    let ctx = mkSseContext getWarmup broadcast
    use _sub = wireSessionHealthSubscription stateChanged.Publish ctx getAllSessions

    stateChanged.Trigger (SseEvent.HotReloadChanged sessId)
    received.Count |> Expect.equal "any stateChanged case triggers a recompute" 1
]

// ── Group 3: replayHealthSnapshot — fresh connections learn current health ──

[<Tests>]
let replayHealthSnapshotTests = testList "replayHealthSnapshot" [

  // WHY testTask rather than testCase plus a blocking await: blocking on an
  // awaitable inside a test body is banned here — it starves the thread pool and
  // is ratcheted by "Architecture — blocking-call budgets". `do!` is the whole fix.
  // (The blocking call is deliberately not spelled out above: the ratchet counts
  // any line containing that literal, so naming it would count as its own debt.)
  testTask "writes the CURRENT (already-Degraded) verdict without waiting for a transition" {
    let getWarmup (_: string) = Task.FromResult (Some nothingLoadedWarmup)
    let getAllSessions () = Task.FromResult [ mkSessionInfo () ]
    let ctx = mkSseContext getWarmup (Event<string>())
    use stream = new IO.MemoryStream()

    do! replayHealthSnapshot ctx getAllSessions stream

    stream.Position <- 0L
    let written = (new IO.StreamReader(stream)).ReadToEnd()
    written |> Expect.stringContains "replay carries the session channel event" "event: session"
    written |> Expect.stringContains "replay carries the session id" (WorkerProtocol.SessionId.value sessId)
    written |> Expect.stringContains "replay carries the CURRENT Degraded verdict" "\"status\":\"Degraded\""
  }

  testTask "writes Healthy for a session with no warmup data yet (quiet common case)" {
    let getWarmup (_: string) = Task.FromResult (None: WarmupContext option)
    let getAllSessions () = Task.FromResult [ mkSessionInfo () ]
    let ctx = mkSseContext getWarmup (Event<string>())
    use stream = new IO.MemoryStream()

    do! replayHealthSnapshot ctx getAllSessions stream

    stream.Position <- 0L
    let written = (new IO.StreamReader(stream)).ReadToEnd()
    written |> Expect.stringContains "replay carries Healthy" "\"status\":\"Healthy\""
  }
]
