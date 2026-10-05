/// A member token and WHERE it may route.
///
/// A token is confined to a scope of files and a role's tool classes, but nothing named the session
/// or checkout it may reach: any tool that takes a `session_id` or a `working_directory` honored
/// whichever one the caller named, so an Analysis token minted for session A could read session B.
/// These stand up a registry that serves TWO checkouts and go through the real gate
/// (`admitToolCallWithinStore`), the way `ToolAuthorityGateTests` does for the cohort, because the
/// pure decision (`RouteBindingTests`) cannot show that the gate asks it.
///
/// What is covered, by name: the hole itself (a token for A admitted to B), every routing parameter
/// (`session_id`, `working_directory`, and the call that names neither), every registered tool, the
/// conductor and an untokened connection (Unbound by design), a git worktree as its own boundary, the
/// structural confinement under the gate (a bound caller cannot SEE another session, so a path the
/// gate does not know about cannot reach it either), the wiring in the daemon's one context
/// constructor, and the mint tool's new rule that a token names where it routes.
module SageFs.Tests.MemberRouteGateTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Capability
open SageFs.Features
open SageFs.McpTools
open SageFs.Server
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
              AvgDurationMs = FixtureDurations.notRunMsInt64
              MinDurationMs = FixtureDurations.notRunMsInt64
              MaxDurationMs = FixtureDurations.notRunMsInt64
              Projects = []
              CoreVersion = "0.0.0-test" })
      | other -> return failwithf "the gate sent the worker something other than a status probe: %A" other
    }

let registry : SessionInfo list = [ infoOf sidA dirA; infoOf sidB dirB ]

/// What the daemon's registry does, over `sessions`. Stopping and restarting record who was asked, so a
/// test can see whether a call got through to the registry at all.
let opsOf (sessions: SessionInfo list) (reached: ConcurrentBag<string>) : SessionManagementOps =
  let find (sid: SessionId) = sessions |> List.tryFind (fun s -> s.Id = sid)
  { SessionManagementOps.stub with
      GetProxy = fun sid -> Task.FromResult(find sid |> Option.map (fun _ -> readyProxy))
      GetSessionInfo = fun sid -> Task.FromResult(find sid)
      GetAllSessions = fun () -> Task.FromResult sessions
      GetAdoptedCore = fun sid -> Task.FromResult(find sid |> Option.map (fun _ -> ("0.0.0", DateTime.MinValue)))
      StopSession = fun sid -> reached.Add("stop:" + sid); Task.FromResult(Result.Ok "stopped")
      PurgeSession = fun sid -> reached.Add("purge:" + sid); Task.FromResult(Result.Ok "purged")
      RestartSession = fun sid _ -> reached.Add("restart:" + SessionId.value sid); Task.FromResult(Result.Ok "restarted")
      CreateSession = fun _ dir _ -> reached.Add("create:" + dir); Task.FromResult(Result.Ok "99999999") }

let silentLogger =
  { new Utils.ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

let scopeOfPath (path: string) = Scope.ofWorkingDirectory Scope.defaultStrategy path

/// A daemon that started in checkout A and serves `sessions`, with a cohort per repository.
let mkCtxWith (sessions: SessionInfo list) (reached: ConcurrentBag<string>) : McpContext =
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
    SessionOps = opsOf sessions reached
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

/// The context as the daemon builds it: the registry seen through the caller's binding.
let confineOps (ctx: McpContext) : McpContext =
  { ctx with SessionOps = RouteGate.confine (fun () -> RouteGate.routeOf currentCapability.Value) RouteGate.productionWithin ctx.SessionOps }

let mkCtx () : McpContext = confineOps (mkCtxWith registry (ConcurrentBag<string>()))

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

let rootOf (dir: string) : CheckoutRoot =
  CheckoutRoot.tryParse dir |> Expect.wantOk (sprintf "'%s' is a checkout root" dir)

let grantFor (preset: RolePreset) (route: RouteBinding) : Grant =
  { Preset = preset; Scope = ScopePrefix.repoRoot; Route = route; NotAfter = now + TestTimeouts.tokenRun }

let conductorMinter =
  { Authority = Cohort.Authority.Conductor(MemberTable.MemberId.Minted "conductor"); Grant = Grant.conductorAuthority }

let mutable private entropyCounter = 0

/// Mint straight into the table (the cohort is not the subject here) and present the token.
let tokenFor (store: CapabilityStore) (preset: RolePreset) (route: RouteBinding) : ResolvedCapability =
  // Cases of one list run in parallel and all mint through here, so the counter is bumped atomically: a plain
  // read-increment-write hands two cases the same entropy, and two tokens with one hash are one token.
  let n = System.Threading.Interlocked.Increment(&entropyCounter)
  let hash = TokenHash.ofToken (Token.ofEntropy (Array.create Token.entropyBytes (byte n)))
  store.Mint(now, conductorMinter, grantFor preset route, hash)
  |> Result.mapError (sprintf "%A")
  |> Expect.wantOk "the conductor mints a bound token"
  |> ignore
  store.Present(now, hash) |> Result.mapError (sprintf "%A") |> Expect.wantOk "the minted token resolves"

let newStore () = CapabilityStore(IdentityPolicy.ConnectionsAllowed)

let boundToA = RouteBinding.BoundToSession(SessionId.value sidA)
let checkoutOfA () = RouteBinding.BoundToCheckout(rootOf dirA)

/// One call through the real gate, as the MCP filter makes it, holding `token` (or none).
let gateAs (ctx: McpContext) (store: CapabilityStore) (token: ResolvedCapability option) (sessionId: SessionId option) (workingDirectory: string option) (tool: string) =
  asCaller "token-conn" token (fun () ->
    admitToolCallWithinStore store Timeouts.gateStatusProbe ctx "mcp" (sessionId |> Option.map SessionId.value) workingDirectory tool)

let gate (ctx: McpContext) (store: CapabilityStore) (token: ResolvedCapability) = gateAs ctx store (Some token)

let isAdmitted (verdict: Result<GateAdmission, string>) = Result.isOk verdict

let describeVerdict (verdict: Result<GateAdmission, string>) =
  match verdict with
  | Ok admission -> sprintf "ADMITTED (%A)" admission.Resolution
  | Error message -> sprintf "REFUSED (%s)" message

let refusedText (verdict: Result<GateAdmission, string>) : string =
  match verdict with
  | Error message -> message
  | Ok admission -> failtestf "was admitted: %A" admission.Resolution

let registeredTools () : string list =
  typeof<SageFs.Server.McpTools.SageFsTools>.GetMethods()
  |> Array.filter (fun m -> m.GetCustomAttributes(true) |> Array.exists (fun attr -> attr.GetType().Name = "McpServerToolAttribute"))
  |> Array.map (fun m -> m.Name)
  |> List.ofArray

// ── The hole ────────────────────────────────────────────────────────────

/// The conductor of checkout A joins its cohort first, then mints one token for A.
let conductorMintsForA (ctx: McpContext) (store: CapabilityStore) (role: string) : Task<ResolvedCapability> =
  task {
    let! joined = asCaller "conductor-conn" None (fun () -> McpCohortTools.joinCohort ctx "gateway" "Implementer" (Some dirA))
    joined |> Result.mapError SageFsError.describeForAgent |> Expect.wantOk "the conductor joins checkout A's cohort" |> ignore
    let! minted = asCaller "conductor-conn" None (fun () -> McpCapability.mintMember ctx store now "gateway" role "" 60 (Some dirA) None)
    let minted = minted |> Result.mapError SageFsError.describeForAgent |> Expect.wantOk "the conductor mints a token for checkout A"
    return store.Present(now, TokenHash.ofToken minted.Token) |> Result.mapError (sprintf "%A") |> Expect.wantOk "the minted token resolves"
  }

[<Tests>]
let theHoleTests =
  testList "a member token is bound to where it was minted for" [

    testTask "a token minted for session A is refused session B named by its id" {
      let ctx = mkCtx ()
      let store = newStore ()
      let! token = conductorMintsForA ctx store "Analysis"
      let! verdict = gate ctx store token (Some sidB) None "check_fsharp_code"
      verdict |> isAdmitted |> Expect.isFalse (sprintf "a token minted for A reached B: %s" (describeVerdict verdict))
    }

    testTask "a token minted for session A is refused session B named by its working directory" {
      let ctx = mkCtx ()
      let store = newStore ()
      let! token = conductorMintsForA ctx store "Analysis"
      let! verdict = gate ctx store token None (Some dirB) "check_fsharp_code"
      verdict |> isAdmitted |> Expect.isFalse (sprintf "a token minted for A reached B's directory: %s" (describeVerdict verdict))
    }

    testTask "a token minted for session A is refused session B on a tool the gate resolves no session for" {
      // get_session_status is AlwaysAvailable, so the session-state gate never looks at the id at all.
      let ctx = mkCtx ()
      let store = newStore ()
      let! token = conductorMintsForA ctx store "Analysis"
      let! verdict = gate ctx store token (Some sidB) None "get_session_status"
      verdict |> isAdmitted |> Expect.isFalse (sprintf "get_session_status read B for a token minted for A: %s" (describeVerdict verdict))
    }

    testTask "NEGATIVE CONTROL: the token still reaches the session it was minted for, by id and by directory" {
      let ctx = mkCtx ()
      let store = newStore ()
      let! token = conductorMintsForA ctx store "Analysis"
      let! byId = gate ctx store token (Some sidA) None "check_fsharp_code"
      let! byDirectory = gate ctx store token None (Some dirA) "check_fsharp_code"
      byId |> isAdmitted |> Expect.isTrue (sprintf "A by id: %s" (describeVerdict byId))
      byDirectory |> isAdmitted |> Expect.isTrue (sprintf "A by directory: %s" (describeVerdict byDirectory))
    }
  ]

// ── Every routing parameter, on every registered tool ───────────────────

[<Tests>]
let everyToolTests =
  testList "no registered tool admits a bound token for what it was not bound to" [

    testTask "every registered tool has a routing kind, so none can be skipped by the gate" {
      let unclassified = registeredTools () |> List.filter (fun tool -> RouteKind.ofTool tool |> Option.isNone)
      unclassified |> Expect.equal "a tool with no routing kind is refused to a bound token and unreachable to it" []
    }

    testTask "a token bound to session A is refused session B by id and by directory on EVERY registered tool" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      for tool in registeredTools () do
        let! byId = gate ctx store token (Some sidB) None tool
        let! byDirectory = gate ctx store token None (Some dirB) tool
        byId |> isAdmitted |> Expect.isFalse (sprintf "%s admitted a token bound to A for session B by id: %s" tool (describeVerdict byId))
        byDirectory |> isAdmitted |> Expect.isFalse (sprintf "%s admitted a token bound to A for B's directory: %s" tool (describeVerdict byDirectory))
    }

    testTask "a token bound to checkout A is refused session B by id and by directory on EVERY registered tool" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer (checkoutOfA ())
      for tool in registeredTools () do
        let! byId = gate ctx store token (Some sidB) None tool
        let! byDirectory = gate ctx store token None (Some dirB) tool
        byId |> isAdmitted |> Expect.isFalse (sprintf "%s admitted a token bound to checkout A for session B by id: %s" tool (describeVerdict byId))
        byDirectory |> isAdmitted |> Expect.isFalse (sprintf "%s admitted a token bound to checkout A for B's directory: %s" tool (describeVerdict byDirectory))
    }

    testTask "NEGATIVE CONTROL: every session tool the role allows still admits the token for the session it is bound to" {
      // The gate did not become "refuse everything".
      let ctx = mkCtx ()
      let store = newStore ()
      let onSession =
        RouteKind.assignments
        |> List.filter (fun (_, kind) -> kind = RouteKind.OnSession)
        |> List.map fst
        |> List.filter (fun tool -> Result.isOk (Capability.admitTool (grantFor RolePreset.Implementer boundToA) tool))
      onSession |> List.isEmpty |> Expect.isFalse "the control has tools to try"
      for route in [ boundToA; checkoutOfA () ] do
        let token = tokenFor store RolePreset.Implementer route
        for tool in onSession do
          let! byId = gate ctx store token (Some sidA) None tool
          let! byDirectory = gate ctx store token None (Some dirA) tool
          byId |> isAdmitted |> Expect.isTrue (sprintf "%s refused its own session by id (%A): %s" tool route (describeVerdict byId))
          byDirectory |> isAdmitted |> Expect.isTrue (sprintf "%s refused its own directory (%A): %s" tool route (describeVerdict byDirectory))
    }
  ]

// ── The refusal says the rule and the next action ───────────────────────

[<Tests>]
let refusalTests =
  testList "a refused route says what the token is bound to" [

    testTask "session B named by a token bound to A: the refusal names A, so the agent can proceed" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Analysis boundToA
      let! verdict = gate ctx store token (Some sidB) None "get_session_status"
      refusedText verdict |> Expect.stringContains "names the session the token is bound to" (SessionId.value sidA)
    }

    testTask "a cohort verb with no directory: the refusal says to pass working_directory and which one" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Analysis (checkoutOfA ())
      let! verdict = gate ctx store token None None "join_cohort"
      let text = refusedText verdict
      text |> Expect.stringContains "names the parameter" "working_directory"
      text |> Expect.stringContains "names the directory to pass" dirA
    }

    testTask "creating a session from a token bound to one session: refused, and says a token for a checkout can create" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      let! verdict = gate ctx store token None (Some dirA) "create_project_session"
      let text = refusedText verdict
      text |> Expect.stringContains "points at the way to get a token that can" "mint_member"
    }
  ]

// ── The call that names nothing, and the structure under the gate ───────

[<Tests>]
let implicitTests =
  testList "a call that names no session still cannot leave the binding" [

    testTask "bound to A with two sessions served, naming nothing routes to A, not to 'no active session'" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      let! verdict = gate ctx store token None None "check_fsharp_code"
      match verdict with
      | Ok admission -> admission.Resolution |> Expect.equal "resolved to its own session" (GateResolution.Resolved(Routable(SessionId.value sidA)))
      | Error message -> failtestf "refused its own implicit session: %s" message
    }

    testTask "bound to A when A is gone and only B is served: naming nothing is refused, never routed to B" {
      // The 'only one session' fallback is what a stranger's call would otherwise fall into.
      let ctx = confineOps (mkCtxWith [ infoOf sidB dirB ] (ConcurrentBag<string>()))
      let store = newStore ()
      for route in [ boundToA; checkoutOfA () ] do
        let token = tokenFor store RolePreset.Implementer route
        let! verdict = gate ctx store token None None "check_fsharp_code"
        verdict |> isAdmitted |> Expect.isFalse (sprintf "routed to the only other session (%A): %s" route (describeVerdict verdict))
    }

    testTask "an active-session pointer that points at B does not carry a token bound to A there" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      do! asCaller "token-conn" (Some token) (fun () -> task { setActiveSessionId ctx "mcp" (SessionId.value sidB) })
      let! verdict = gate ctx store token None None "check_fsharp_code"
      verdict |> isAdmitted |> Expect.isFalse (sprintf "followed a stale pointer to B: %s" (describeVerdict verdict))
    }

    testTask "a refused call does not leave the token's pointer on the session it was refused" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      let! _ = gate ctx store token None (Some dirB) "check_fsharp_code"
      let! pointer = asCaller "token-conn" (Some token) (fun () -> task { return activeSessionId ctx "mcp" })
      pointer |> Expect.notEqual "a later call that names nothing must not land on B" (SessionId.value sidB)
    }
  ]

[<Tests>]
let confinementTests =
  testList "the registry as a bound caller sees it" [

    testTask "a caller bound to session A sees session A and no other, in every registry read" {
      let reached = ConcurrentBag<string>()
      let ctx = confineOps (mkCtxWith registry reached)
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      do!
        asCaller "token-conn" (Some token) (fun () ->
          task {
            let! all = ctx.SessionOps.GetAllSessions()
            all |> List.map (fun s -> s.Id) |> Expect.equal "only A is listed" [ sidA ]
            let! infoB = ctx.SessionOps.GetSessionInfo sidB
            infoB |> Expect.isNone "B's record is not readable"
            let! proxyB = ctx.SessionOps.GetProxy sidB
            proxyB |> Option.isNone |> Expect.isTrue "B has no worker to talk to"
            let! coreB = ctx.SessionOps.GetAdoptedCore sidB
            coreB |> Expect.isNone "B's adopted build is not readable"
            let! infoA = ctx.SessionOps.GetSessionInfo sidA
            infoA |> Option.isSome |> Expect.isTrue "A is"
          })
    }

    testTask "a caller bound to A cannot stop, purge or restart session B, and the registry is never asked to" {
      let reached = ConcurrentBag<string>()
      let ctx = confineOps (mkCtxWith registry reached)
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      do!
        asCaller "token-conn" (Some token) (fun () ->
          task {
            let! stopped = ctx.SessionOps.StopSession(SessionId.value sidB)
            stopped |> Result.isError |> Expect.isTrue "stop B is refused"
            let! purged = ctx.SessionOps.PurgeSession(SessionId.value sidB)
            purged |> Result.isError |> Expect.isTrue "purge B is refused"
            let! restarted = ctx.SessionOps.RestartSession sidB RestartPlan.RespawnOnly
            restarted |> Result.isError |> Expect.isTrue "restart B is refused"
          })
      reached |> Seq.toList |> Expect.equal "nothing reached the registry" []
    }

    testTask "a caller bound to A can stop A: the confinement is not 'refuse everything'" {
      let reached = ConcurrentBag<string>()
      let ctx = confineOps (mkCtxWith registry reached)
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      let! stopped = asCaller "token-conn" (Some token) (fun () -> ctx.SessionOps.StopSession(SessionId.value sidA))
      stopped |> Result.isOk |> Expect.isTrue "stop A is allowed"
      reached |> Seq.toList |> Expect.equal "the registry was asked about A" [ "stop:" + SessionId.value sidA ]
    }

    testTask "a caller bound to a checkout can create a session inside it and not outside it" {
      let reached = ConcurrentBag<string>()
      let ctx = confineOps (mkCtxWith registry reached)
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer (checkoutOfA ())
      do!
        asCaller "token-conn" (Some token) (fun () ->
          task {
            let! inside = ctx.SessionOps.CreateSession [ SessionProjectTarget.Bare ] (dirA + "/sub") WorkflowTypes.SessionWorkflow.Interactive
            inside |> Result.isOk |> Expect.isTrue "inside"
            let! outside = ctx.SessionOps.CreateSession [ SessionProjectTarget.Bare ] dirB WorkflowTypes.SessionWorkflow.Interactive
            outside |> Result.isError |> Expect.isTrue "outside"
          })
      reached |> Seq.toList |> Expect.equal "only the inside creation reached the registry" [ "create:" + dirA + "/sub" ]
    }

    testTask "a caller bound to a session cannot create one at all" {
      let reached = ConcurrentBag<string>()
      let ctx = confineOps (mkCtxWith registry reached)
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      let! created = asCaller "token-conn" (Some token) (fun () -> ctx.SessionOps.CreateSession [ SessionProjectTarget.Bare ] dirA WorkflowTypes.SessionWorkflow.Interactive)
      created |> Result.isError |> Expect.isTrue "refused"
      reached |> Seq.toList |> Expect.equal "nothing reached the registry" []
    }

    testTask "list_sessions, as a bound caller runs it, shows no other session's id or directory" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Observer boundToA
      let! text = asCaller "token-conn" (Some token) (fun () -> listSessions ctx)
      text |> Expect.stringContains "its own session is listed" (SessionId.value sidA)
      text.Contains(SessionId.value sidB) |> Expect.isFalse "B's id is not listed"
      text.Contains dirB |> Expect.isFalse "B's directory is not listed"
    }

    testTask "an untokened caller (the conductor, a plain member) sees every session, as before" {
      let ctx = mkCtx ()
      let! all = asCaller "plain-conn" None (fun () -> ctx.SessionOps.GetAllSessions())
      all |> List.length |> Expect.equal "both" 2
    }
  ]

// ── Unbound by design: the conductor and an untokened connection ────────

[<Tests>]
let unboundTests =
  testList "the conductor and an untokened connection route anywhere, as before" [

    testTask "an untokened connection reaches either session by id and by directory under ConnectionsAllowed" {
      let ctx = mkCtx ()
      let store = newStore ()
      for target in [ sidA; sidB ] do
        let! verdict = gateAs ctx store None (Some target) None "check_fsharp_code"
        verdict |> isAdmitted |> Expect.isTrue (sprintf "untokened by id %A: %s" target (describeVerdict verdict))
      for dir in [ dirA; dirB ] do
        let! verdict = gateAs ctx store None None (Some dir) "check_fsharp_code"
        verdict |> isAdmitted |> Expect.isTrue (sprintf "untokened by directory %s: %s" dir (describeVerdict verdict))
    }

    testTask "under TokenRequired the conductor on its own connection reaches either session, because it has to mint for any" {
      let ctx = mkCtx ()
      let store = CapabilityStore(IdentityPolicy.TokenRequired)
      let! joined = asCaller "conductor-conn" None (fun () -> McpCohortTools.joinCohort ctx "gateway" "Implementer" (Some dirA))
      joined |> Result.isOk |> Expect.isTrue "the first joiner is the conductor"
      let gateAsConductor (sessionId: SessionId) = asCaller "conductor-conn" None (fun () -> admitToolCallWithinStore store Timeouts.gateStatusProbe ctx "mcp" (Some(SessionId.value sessionId)) (Some dirA) "get_session_status")
      let! onA = gateAsConductor sidA
      let! onB = gateAsConductor sidB
      onA |> isAdmitted |> Expect.isTrue (sprintf "conductor on A: %s" (describeVerdict onA))
      onB |> isAdmitted |> Expect.isTrue (sprintf "conductor on B: %s" (describeVerdict onB))
    }
  ]

// ── A git worktree is its own routing boundary ──────────────────────────

/// A repository on disk with a linked worktree under it, the way `.claude/worktrees/<agent>` is.
let withRepoAndWorktree (body: string -> string -> Task<unit>) : Task<unit> =
  task {
    let root = Directory.CreateTempSubdirectory("route-binding-").FullName
    try
      let repo = Path.Combine(root, "repo")
      let worktree = Path.Combine(repo, ".claude", "worktrees", "agent-x")
      Directory.CreateDirectory(Path.Combine(repo, ".git")) |> ignore
      Directory.CreateDirectory worktree |> ignore
      File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere")
      do! body repo worktree
    finally
      Directory.Delete(root, true)
  }

[<Tests>]
let worktreeTests =
  testList "a git worktree is its own boundary" [

    testTask "a token bound to the main checkout does not reach a session rooted in a worktree nested under it" {
      do!
        withRepoAndWorktree (fun repo worktree ->
          task {
            let mainSession = SessionId.newId ()
            let worktreeSession = SessionId.newId ()
            let ctx = confineOps (mkCtxWith [ infoOf mainSession repo; infoOf worktreeSession worktree ] (ConcurrentBag<string>()))
            let store = newStore ()
            let token = tokenFor store RolePreset.Implementer (RouteBinding.BoundToCheckout(rootOf repo))
            let! own = gate ctx store token (Some mainSession) None "check_fsharp_code"
            own |> isAdmitted |> Expect.isTrue (sprintf "its own session: %s" (describeVerdict own))
            let! nestedById = gate ctx store token (Some worktreeSession) None "check_fsharp_code"
            nestedById |> isAdmitted |> Expect.isFalse (sprintf "the nested worktree's session by id: %s" (describeVerdict nestedById))
            let! nestedByDirectory = gate ctx store token None (Some worktree) "check_fsharp_code"
            nestedByDirectory |> isAdmitted |> Expect.isFalse (sprintf "the nested worktree's directory: %s" (describeVerdict nestedByDirectory))
          })
    }

    testTask "a token bound to the worktree reaches its own session and not the main checkout's" {
      do!
        withRepoAndWorktree (fun repo worktree ->
          task {
            let mainSession = SessionId.newId ()
            let worktreeSession = SessionId.newId ()
            let ctx = confineOps (mkCtxWith [ infoOf mainSession repo; infoOf worktreeSession worktree ] (ConcurrentBag<string>()))
            let store = newStore ()
            let token = tokenFor store RolePreset.Implementer (RouteBinding.BoundToCheckout(rootOf worktree))
            let! own = gate ctx store token (Some worktreeSession) None "check_fsharp_code"
            own |> isAdmitted |> Expect.isTrue (sprintf "the worktree's own session: %s" (describeVerdict own))
            let! parent = gate ctx store token (Some mainSession) None "check_fsharp_code"
            parent |> isAdmitted |> Expect.isFalse (sprintf "the main checkout's session: %s" (describeVerdict parent))
            let! parentDirectory = gate ctx store token None (Some repo) "check_fsharp_code"
            parentDirectory |> isAdmitted |> Expect.isFalse (sprintf "the main checkout's directory: %s" (describeVerdict parentDirectory))
          })
    }
  ]

// ── mint_member: a token names where it routes ──────────────────────────

let mintBy (ctx: McpContext) (store: CapabilityStore) (workingDirectory: string option) (sessionId: string option) : Task<Result<McpCapability.MintedMember, SageFsError>> =
  asCaller "conductor-conn" None (fun () -> McpCapability.mintMember ctx store now "gateway" "Analysis" "" 60 workingDirectory sessionId)

/// The minted member, or the refusal as the agent would read it. Annotated, so a test body can read its fields.
let mintedOrFail (message: string) (result: Result<McpCapability.MintedMember, SageFsError>) : McpCapability.MintedMember =
  result |> Result.mapError SageFsError.describeForAgent |> Expect.wantOk message

let conductorOf (ctx: McpContext) (dir: string) =
  task {
    let! joined = asCaller "conductor-conn" None (fun () -> McpCohortTools.joinCohort ctx "gateway" "Implementer" (Some dir))
    joined |> Result.isOk |> Expect.isTrue (sprintf "joined %s as its conductor" dir)
  }

[<Tests>]
let mintToolTests =
  testList "mint_member binds the token" [

    testTask "naming a working_directory binds the token to that checkout" {
      let ctx = mkCtx ()
      let store = newStore ()
      do! conductorOf ctx dirA
      let! minted = mintBy ctx store (Some dirA) None
      let minted = mintedOrFail "minted" minted
      minted.Record.Grant.Route |> Expect.equal "bound to the checkout" (RouteBinding.BoundToCheckout(rootOf dirA))
    }

    testTask "naming a session_id binds the token to that session, and seats it in that session's repository" {
      let ctx = mkCtx ()
      let store = newStore ()
      do! conductorOf ctx dirB
      let! minted = mintBy ctx store None (Some(SessionId.value sidB))
      let minted = mintedOrFail "minted for B's session, from B's cohort" minted
      minted.Record.Grant.Route |> Expect.equal "bound to the session" (RouteBinding.BoundToSession(SessionId.value sidB))
      minted.Session |> Expect.equal "seated on that session" (Some(SessionId.value sidB))
    }

    testTask "naming neither is refused, and the refusal names both parameters" {
      let ctx = mkCtx ()
      let store = newStore ()
      do! conductorOf ctx dirA
      let! minted = mintBy ctx store None None
      let text =
        match minted with
        | Error err -> SageFsError.describeForAgent err
        | Ok _ -> failtest "a token was minted that names no binding"
      text |> Expect.stringContains "names working_directory" "working_directory"
      text |> Expect.stringContains "names session_id" "session_id"
      store.Records |> Expect.isEmpty "no token was minted"
    }

    testTask "naming both is refused, because a grant has one binding" {
      let ctx = mkCtx ()
      let store = newStore ()
      do! conductorOf ctx dirA
      let! minted = mintBy ctx store (Some dirA) (Some(SessionId.value sidA))
      minted |> Result.isError |> Expect.isTrue "refused"
      store.Records |> Expect.isEmpty "no token was minted"
    }

    testTask "a session the daemon does not serve is refused, and nothing is minted" {
      let ctx = mkCtx ()
      let store = newStore ()
      do! conductorOf ctx dirA
      let! minted = mintBy ctx store None (Some "00000000")
      minted |> Result.isError |> Expect.isTrue "refused"
      store.Records |> Expect.isEmpty "no token was minted"
    }

    testTask "a relative working_directory is refused, because it names no checkout" {
      let ctx = mkCtx ()
      let store = newStore ()
      do! conductorOf ctx dirA
      let! minted = mintBy ctx store (Some "src/Foo") None
      minted |> Result.isError |> Expect.isTrue "refused"
      store.Records |> Expect.isEmpty "no token was minted"
    }

    testTask "the reply the conductor reads says where the token routes" {
      let ctx = mkCtx ()
      let store = newStore ()
      do! conductorOf ctx dirA
      let! minted = mintBy ctx store (Some dirA) None
      let minted = mintedOrFail "minted" minted
      McpCapability.describeMinted minted |> Expect.stringContains "names the checkout" dirA
      McpCapability.describeMintedForLog minted |> Expect.stringContains "the log text names it too" dirA
    }

    testTask "only the conductor mints, bound or not: a member cannot mint a token for a session of its own" {
      let ctx = mkCtx ()
      let store = newStore ()
      do! conductorOf ctx dirA
      let! minted = asCaller "other-conn" None (fun () -> McpCapability.mintMember ctx store now "other" "Analysis" "" 60 (Some dirA) None)
      minted |> Result.isError |> Expect.isTrue "refused"
      store.Records |> Expect.isEmpty "no token was minted"
    }
  ]

// ── The daemon's one context constructor wires the confinement ──────────

let mcpConfig (ops: SessionManagementOps) : McpServer.McpServerConfig =
  { DiagnosticsChanged = (Microsoft.FSharp.Control.Event<DiagnosticsStore.T>()).Publish
    StateChanged = None
    FrictionStore = None
    Port = 0
    BindHost = SageFsConfig.LoopbackHost.Localhost
    OwnOrigins = Server.HttpOriginGuard.OwnOrigins.ofPorts [ 0 ]
    SessionOps = ops
    ElmRuntime = None
    GetWarmupContext = None
    GetHotReloadState = None
    SharedBindingScope = ref None
    SharedFeatureState = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveBindings = None
    CohortSupport = CohortOwners.Wiring.Unwired
    GetDaemonHealth = fun () -> None }

[<Tests>]
let wiringTests =
  testList "the daemon's context constructor confines the registry" [

    testTask "a context built by McpServer.mkContext shows a bound caller only its own session" {
      let ctx = McpServer.mkContext (mcpConfig (opsOf registry (ConcurrentBag<string>()))) None None None
      let store = newStore ()
      let token = tokenFor store RolePreset.Observer boundToA
      let! bound = asCaller "token-conn" (Some token) (fun () -> ctx.SessionOps.GetAllSessions())
      bound |> List.map (fun s -> s.Id) |> Expect.equal "a bound caller sees A only" [ sidA ]
      let! unbound = asCaller "plain-conn" None (fun () -> ctx.SessionOps.GetAllSessions())
      unbound |> List.length |> Expect.equal "an untokened caller sees both" 2
    }
  ]

// ── The daemon's own background work is not held to a caller's route ────

/// The working directories a scan was told are in use.
let sessionDirectories (facts: HygieneGather.LiveFacts) : string list =
  facts.Sessions |> List.map snd |> List.sort

[<Tests>]
let backgroundTests =
  testList "the daemon's own background work is not held to a caller's route" [

    testTask "a machine scan started by a bound caller still sees every session as in use" {
      // Cache.refresh runs the scan on a pool thread, which inherits the member token of the call that started it.
      // Seen through a token bound to A, B's worktree would look unused and the cached plan would call it reclaimable.
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      let! scanned = asCaller "token-conn" (Some token) (fun () -> Task.Run(fun () -> McpHygiene.daemonLiveFacts ctx))
      scanned |> sessionDirectories |> Expect.equal "both directories are in use" [ dirA; dirB ]
    }

    testTask "CONTRAST: the same gathering through the caller's own view sees only the route" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      let! seen = asCaller "token-conn" (Some token) (fun () -> Task.Run<HygieneGather.LiveFacts>(fun () -> McpHygiene.liveFactsOf ctx))
      seen |> sessionDirectories |> Expect.equal "only A, which is why the scan must not use it" [ dirA ]
    }

    testTask "the scan's thread dropping the binding does not drop the caller's" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Implementer boundToA
      let! stillBound =
        asCaller "token-conn" (Some token) (fun () ->
          task {
            let! _ = Task.Run(fun () -> McpHygiene.daemonLiveFacts ctx)
            return currentCapability.Value |> Option.isSome
          })
      stillBound |> Expect.isTrue "the call that started the scan is still held to its route"
    }
  ]

// ── MCP resources ───────────────────────────────────────────────────────

let confinedResources = [ McpResources.SessionsListUri ]

[<Tests>]
let resourceTests =
  testList "MCP resources are held to the route too" [

    testTask "sessions://list, read by a caller bound to A, lists A and not B" {
      let ctx = mkCtx ()
      let store = newStore ()
      let token = tokenFor store RolePreset.Observer boundToA
      let! json = asCaller "token-conn" (Some token) (fun () -> McpResources.SageFsResources(ctx).SessionsList())
      json |> Expect.stringContains "its own session" (SessionId.value sidA)
      json.Contains(SessionId.value sidB) |> Expect.isFalse "B is not listed"
      json.Contains dirB |> Expect.isFalse "B's directory is not listed"
    }

    testTask "NEGATIVE CONTROL: sessions://list, read with no token, lists both" {
      let ctx = mkCtx ()
      let! json = asCaller "plain-conn" None (fun () -> McpResources.SageFsResources(ctx).SessionsList())
      json |> Expect.stringContains "A" (SessionId.value sidA)
      json |> Expect.stringContains "B" (SessionId.value sidB)
    }

    testCase "a bound caller reads only the resources cut to its route, and a resource added tomorrow is refused to it" <| fun () ->
      for binding in [ boundToA; checkoutOfA () ] do
        RouteGate.resourceAdmitted confinedResources binding McpResources.SessionsListUri |> Expect.equal "the confined resource" (Ok())
        RouteGate.resourceAdmitted confinedResources binding McpResources.CohortStatusUri |> Result.isError |> Expect.isTrue "the cohort resource shows the daemon's own repository"
        RouteGate.resourceAdmitted confinedResources binding "a-resource://added-tomorrow" |> Result.isError |> Expect.isTrue "an unclassified resource"

    testCase "a refused resource says what the token is bound to and what to read instead" <| fun () ->
      match RouteGate.resourceAdmitted confinedResources boundToA McpResources.CohortStatusUri with
      | Error text ->
        text |> Expect.stringContains "names the binding" (SessionId.value sidA)
        text |> Expect.stringContains "names the tool that takes a directory" "get_cohort_status"
      | Ok() -> failtest "the cohort resource was admitted to a bound token"

    testCase "an untokened caller reads every resource, as before" <| fun () ->
      for uri in [ McpResources.SessionsListUri; McpResources.CohortStatusUri; "a-resource://added-tomorrow" ] do
        RouteGate.resourceAdmitted confinedResources RouteBinding.Unbound uri |> Expect.equal uri (Ok())
  ]

// ── Mutants of the resource decision ────────────────────────────────────

type ResourceInput = RouteBinding * string

let resourceAdmittedOf ((binding, uri): ResourceInput) : Result<unit, string> = RouteGate.resourceAdmitted confinedResources binding uri

let resourcesAdmitEverything : MutationTestingFramework.Mutant<ResourceInput -> Result<unit, string>> =
  { Name = "resources_admit_everything_to_a_bound_token"
    Description = "the cohort resource shows the daemon's own repository, so a bound token must not read it"
    Apply = fun _ _ -> Ok() }

let resourcesRefuseTheConfinedOne : MutationTestingFramework.Mutant<ResourceInput -> Result<unit, string>> =
  { Name = "resources_refuse_the_confined_resource"
    Description = "sessions://list is cut to the route underneath, so a bound token may read it"
    Apply = fun real (binding, uri) -> (match binding with RouteBinding.Unbound -> real (binding, uri) | _ -> Result.Error "refused") }

[<Tests>]
let resourceMutationTests =
  testList "resourceAdmitted mutants are caught" [
    MutationTestingFramework.detectsOutputMutant resourcesAdmitEverything (boundToA, McpResources.CohortStatusUri) resourceAdmittedOf Result.isError
    MutationTestingFramework.detectsOutputMutant resourcesRefuseTheConfinedOne (boundToA, McpResources.SessionsListUri) resourceAdmittedOf Result.isOk
  ]
