/// A member token and WHERE it may route.
///
/// A token is confined to a scope of files and a role's tool classes, but nothing named the session
/// or checkout it may reach: any tool that takes a `session_id` or a `working_directory` honored
/// whichever one the caller named, so an Analysis token minted for session A could read session B.
/// These stand up a registry that serves TWO checkouts and go through the real gate
/// (`admitToolCallWithinStore`), the way `ToolAuthorityGateTests` does for the cohort, because the
/// pure decision cannot show that the gate asks it.
module SageFs.Tests.MemberRouteGateTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Capability
open SageFs.Features
open SageFs.McpTools
open SageFs.WorkerProtocol

// ── Two checkouts, one daemon ───────────────────────────────────────────

let dirA = "/tmp/route-binding/checkout-a"
let dirB = "/tmp/route-binding/checkout-b"
let sidA = SessionId.newId ()
let sidB = SessionId.newId ()

let infoOf (sid: SessionId) (dir: string) : SessionInfo =
  { Id = sid
    Name = None
    Projects = []
    WorkingDirectory = dir
    SolutionRoot = None
    CreatedAt = DateTime(2026, 10, 4)
    LastActivity = DateTime(2026, 10, 4)
    Status = SessionLifecycleStatus.Stopped
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet
    Freshness = ReplFreshness.InSync }

/// A worker that is Ready and answers the gate's status probe. Anything else it is asked is a test bug.
let readyProxy : SessionProxy =
  fun msg ->
    async {
      match msg with
      | WorkerMessage.GetStatus replyId ->
        return
          WorkerResponse.StatusResult(
            replyId,
            { Status = SessionStatus.Ready
              StatusMessage = None
              EvalCount = 0
              AvgDurationMs = 0L
              MinDurationMs = 0L
              MaxDurationMs = 0L
              Projects = []
              CoreVersion = "0.0.0-test" })
      | other -> return failwithf "the gate sent the worker something other than a status probe: %A" other
    }

let registry : SessionInfo list = [ infoOf sidA dirA; infoOf sidB dirB ]

let opsOf (sessions: SessionInfo list) : SessionManagementOps =
  let find (sid: SessionId) = sessions |> List.tryFind (fun s -> s.Id = sid)
  { SessionManagementOps.stub with
      GetProxy = fun sid -> Task.FromResult(find sid |> Option.map (fun _ -> readyProxy))
      GetSessionInfo = fun sid -> Task.FromResult(find sid)
      GetAllSessions = fun () -> Task.FromResult sessions }

let silentLogger =
  { new Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let scopeOfPath (path: string) = Scope.ofWorkingDirectory Scope.defaultStrategy path

/// A daemon that started in checkout A and serves both checkouts, with a cohort per repository.
let mkCtx () : McpContext =
  let deps : CohortOwners.Deps =
    { Ledger = CohortLedger.InMemory.create ()
      Clock = fun () -> DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc)
      Entropy = CohortOwner.productionEntropy
      GetSessionTestOutcomes = fun _ -> ([], [], [], 0L)
      Performer = CohortOwner.LandingPerformer.stub
      Logger = silentLogger }
  let owners = CohortOwners.create (CohortOwners.factoryOf deps)
  let diagnostics = Microsoft.FSharp.Control.Event<DiagnosticsStore.T>()
  { FrictionStore = None
    DiagnosticsChanged = diagnostics.Publish
    StateChanged = None
    SessionOps = opsOf registry
    SessionMap = ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    RecordEval = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveBindings = None
    CohortSupport = CohortOwners.Wiring.Wired(owners, scopeOfPath dirA)
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None }

let now = DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc)

/// Run `body` as the connection `handle`, presenting `capability` the way the call filter binds both.
let asCaller (handle: string) (capability: ResolvedCapability option) (body: unit -> Task<'a>) : Task<'a> =
  task {
    let previousHandle = currentTransportSessionId.Value
    let previousCapability = currentCapability.Value
    currentTransportSessionId.Value <- Some handle
    currentCapability.Value <- capability
    try
      return! body ()
    finally
      currentTransportSessionId.Value <- previousHandle
      currentCapability.Value <- previousCapability
  }

/// The conductor of checkout A joins its cohort first, then mints one token for A's session.
let mintForA (ctx: McpContext) (store: CapabilityStore) (role: string) : Task<ResolvedCapability> =
  task {
    let! joined = asCaller "conductor-conn" None (fun () -> McpCohortTools.joinCohort ctx "gateway" "Implementer" (Some dirA))
    joined
    |> Result.mapError SageFsError.describeForAgent
    |> Expect.wantOk "the conductor joins checkout A's cohort" |> ignore
    let! minted = asCaller "conductor-conn" None (fun () -> McpCapability.mintMember ctx store now "gateway" role "" 60 (Some dirA))
    let minted =
      minted
      |> Result.mapError SageFsError.describeForAgent
      |> Expect.wantOk "the conductor mints a token for checkout A"
    return
      store.Present(now, TokenHash.ofToken minted.Token)
      |> Result.mapError (sprintf "%A")
      |> Expect.wantOk "the minted token resolves"
  }

/// One call through the real gate, as the MCP filter makes it, holding `token`.
let gate (ctx: McpContext) (store: CapabilityStore) (token: ResolvedCapability) (sessionId: SessionId option) (workingDirectory: string option) (tool: string) =
  asCaller "token-conn" (Some token) (fun () ->
    admitToolCallWithinStore store Timeouts.gateStatusProbe ctx "mcp" (sessionId |> Option.map SessionId.value) workingDirectory tool)

let isAdmitted (verdict: Result<GateAdmission, string>) = Result.isOk verdict

let describeVerdict (verdict: Result<GateAdmission, string>) =
  match verdict with
  | Ok admission -> sprintf "ADMITTED (%A)" admission.Resolution
  | Error message -> sprintf "REFUSED (%s)" message

// ── The hole ────────────────────────────────────────────────────────────

[<Tests>]
let theHoleTests =
  testList "a member token is bound to where it was minted for" [

    testTask "a token minted for session A is refused session B named by its id" {
      let ctx = mkCtx ()
      let store = CapabilityStore(IdentityPolicy.ConnectionsAllowed)
      let! token = mintForA ctx store "Analysis"
      let! verdict = gate ctx store token (Some sidB) None "check_fsharp_code"
      verdict |> isAdmitted |> Expect.isFalse (sprintf "a token minted for A reached B: %s" (describeVerdict verdict))
    }

    testTask "a token minted for session A is refused session B named by its working directory" {
      let ctx = mkCtx ()
      let store = CapabilityStore(IdentityPolicy.ConnectionsAllowed)
      let! token = mintForA ctx store "Analysis"
      let! verdict = gate ctx store token None (Some dirB) "check_fsharp_code"
      verdict |> isAdmitted |> Expect.isFalse (sprintf "a token minted for A reached B's directory: %s" (describeVerdict verdict))
    }

    testTask "a token minted for session A is refused session B on a tool the gate resolves no session for" {
      // get_session_status is AlwaysAvailable, so the session-state gate never looks at the id at all.
      let ctx = mkCtx ()
      let store = CapabilityStore(IdentityPolicy.ConnectionsAllowed)
      let! token = mintForA ctx store "Analysis"
      let! verdict = gate ctx store token (Some sidB) None "get_session_status"
      verdict |> isAdmitted |> Expect.isFalse (sprintf "get_session_status read B for a token minted for A: %s" (describeVerdict verdict))
    }

    testTask "NEGATIVE CONTROL: the token still reaches the session it was minted for, by id and by directory" {
      let ctx = mkCtx ()
      let store = CapabilityStore(IdentityPolicy.ConnectionsAllowed)
      let! token = mintForA ctx store "Analysis"
      let! byId = gate ctx store token (Some sidA) None "check_fsharp_code"
      let! byDirectory = gate ctx store token None (Some dirA) "check_fsharp_code"
      byId |> isAdmitted |> Expect.isTrue (sprintf "A by id: %s" (describeVerdict byId))
      byDirectory |> isAdmitted |> Expect.isTrue (sprintf "A by directory: %s" (describeVerdict byDirectory))
    }
  ]
