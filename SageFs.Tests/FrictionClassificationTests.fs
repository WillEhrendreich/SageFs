module SageFs.Tests.FrictionClassificationTests

/// Roast-5 item #4: friction classification must be driven by the typed
/// `SageFsError` a tool body actually produced, never by re-parsing its
/// rendered "Blocked:"/"Error:" text for keywords. `classifyFrictionOutcome`
/// (the old string-sniffing function, with a precedence bug in its
/// affordance/tool-availability branch) is gone; `SageFs.Server.McpTools.blockerKindOf`
/// is its exhaustive, compiler-checked replacement, and `sessionRoutingError`
/// / `targetedVerifyResult` (SageFs.McpTools, in Mcp.fs) are the typed
/// sources that feed it for session-routing and stale-loaded-state failures.
///
/// Every test below is deliberately adversarial: the SageFsError reason
/// strings say the OPPOSITE of what the correct classification is (e.g. a
/// SessionNotRoutable reason that mentions "stale" and "Multiple sessions
/// match", or a HotReloadStateError reason that denies being stale). A
/// substring-based classifier would get these wrong; a type-driven one
/// cannot, because it never looks at the string at all.
open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.McpSessionRouting
open SageFs.Server.McpTools
open SageFs.WorkerProtocol
open SageFs.Features.FrictionTelemetryTypes

let private dummyProxy (_: WorkerMessage) =
  async { return WorkerResponse.StatusResult("reply", { Status = SessionStatus.Ready; StatusMessage = None; EvalCount = 0; AvgDurationMs = 0L; MinDurationMs = 0L; MaxDurationMs = 0L; Projects = [] }) }

let private mkSessionInfo (id: SessionId) (workingDir: string) (status: SessionLifecycleStatus) : SessionInfo =
  { Id = id
    Name = Some "tests"
    Projects = [ "Fake.fsproj" ]
    WorkingDirectory = workingDir
    SolutionRoot = Some workingDir
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    Status = status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning }

/// Minimal McpContext — only GetAllSessions/GetProxy/GetSessionInfo/GetElmModel
/// are exercised by the functions under test here.
let private mkCtx (sessions: SessionInfo list) (elmModel: (unit -> SageFsModel) option) : McpContext =
  let diagEvent = Event<Features.DiagnosticsStore.T>()
  { FrictionStore = None
    DiagnosticsChanged = diagEvent.Publish
    StateChanged = None
    SessionOps =
      { SessionManagementOps.stub with
          GetAllSessions = fun () -> Task.FromResult(sessions)
          GetProxy = fun _ -> Task.FromResult(Some dummyProxy)
          GetSessionInfo = fun sid -> Task.FromResult(sessions |> List.tryFind (fun s -> s.Id = sid)) }
    SessionMap = ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = None
    GetElmModel = elmModel
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None; RecordEval = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveSnapshotSink = None
    CohortOwner = None }

let private noSessions = mkCtx [] None

[<Tests>]
let blockerKindOfTests =
  testList "blockerKindOf — typed dispatch, not string-sniffing" [

    testCase "WHY — SessionNotRoutable classifies as SessionWarming purely by its DU case, even when its own reason text says 'stale' and 'Multiple sessions match'" <| fun _ ->
      let adversarial =
        SageFsError.SessionNotRoutable
          "this reason deliberately mentions stale and Multiple sessions match and exact test not found"
      blockerKindOf adversarial
      |> Expect.equal "classification must ignore the reason text" BlockerKind.SessionWarming

    testCase "WHY — AmbiguousSessions classifies as SessionAmbiguous even when its descriptions mention warm-up and staleness" <| fun _ ->
      let adversarial =
        SageFsError.AmbiguousSessions [ "session is warming up"; "loaded definition is stale" ]
      blockerKindOf adversarial
      |> Expect.equal "classification must ignore the description text" BlockerKind.SessionAmbiguous

    testCase "WHY — HotReloadStateError classifies as LoadedStateStale even when the reason text explicitly denies being stale" <| fun _ ->
      let adversarial =
        SageFsError.HotReloadStateError ("sid1", "everything is fine here, definitely not stale")
      blockerKindOf adversarial
      |> Expect.equal "classification must come from the case, not the denial in the text" BlockerKind.LoadedStateStale

    testCase "WHY — NoActiveSessions and SessionNotFound both mean SessionMissing" <| fun _ ->
      blockerKindOf SageFsError.NoActiveSessions
      |> Expect.equal "no sessions at all is SessionMissing" BlockerKind.SessionMissing
      blockerKindOf (SageFsError.SessionNotFound "deadbeef")
      |> Expect.equal "an explicit unknown session id is SessionMissing" BlockerKind.SessionMissing

    testCase "WHY — WorkerCommunicationFailed classifies as TransportFailure regardless of reason text" <| fun _ ->
      blockerKindOf (SageFsError.WorkerCommunicationFailed ("sid1", "unrelated text mentioning ambiguous multiple sessions"))
      |> Expect.equal "classification is by case, not text" BlockerKind.TransportFailure

    testCase "WHY — Unexpected exceptions classify as OperationFailed, not the old arbitrary InvalidRequest catch-all" <| fun _ ->
      blockerKindOf (SageFsError.Unexpected (System.InvalidOperationException "boom"))
      |> Expect.equal "an unclassified exception is an honest OperationFailed" BlockerKind.OperationFailed
  ]

[<Tests>]
let frictionOutcomeOfTests =
  testList "frictionOutcomeOf" [
    testCase "WHY — a successful tool result is always a clean completion" <| fun _ ->
      frictionOutcomeOf (Ok "some tool output")
      |> Expect.equal "Ok is always clean" FrictionOutcome.CompletedCleanly

    testCase "WHY — a failed tool result carries the classified blocker" <| fun _ ->
      frictionOutcomeOf (Error (SageFsError.AmbiguousSessions [ "a"; "b" ]))
      |> Expect.equal "Error is classified via blockerKindOf" (FrictionOutcome.EncounteredBlocker BlockerKind.SessionAmbiguous)
  ]

[<Tests>]
let sessionRoutingErrorTests =
  testList "sessionRoutingError — structural resolution classification (representative: warmup, multiple-sessions, stale-adjacent)" [

    testCaseTask "WHY — a warming-up session is classified as SessionWarming, distinctly from an ambiguous or missing session" <| fun () -> task {
      let resolution = SessionResolution.WarmingUp ("sid1", SessionLifecycleStatus.Starting { Pid = 123; Port = None })
      let! blocker = sessionRoutingError noSessions None (Some @"C:\Repos\Proj") resolution
      match blocker with
      | Some err ->
        blockerKindOf err |> Expect.equal "warming up must classify as SessionWarming" BlockerKind.SessionWarming
      | None -> failtest "expected a routing blocker for a warming-up session"
    }

    testCaseTask "WHY — multiple sessions matching the working directory classify as SessionAmbiguous, computed from the registry (not from a Gone message string)" <| fun () -> task {
      let s1 = mkSessionInfo (SessionId.newId ()) @"C:\Repos\Proj" (SessionLifecycleStatus.Ready { Pid = 1; Port = Some 1 })
      let s2 = mkSessionInfo (SessionId.newId ()) @"C:\Repos\Proj" (SessionLifecycleStatus.Ready { Pid = 2; Port = Some 2 })
      let ctx = mkCtx [ s1; s2 ] None
      // The Gone message deliberately says nothing about ambiguity — proves
      // the classification is NOT derived by re-parsing this text.
      let resolution = SessionResolution.Gone "totally unrelated placeholder text"
      let! blocker = sessionRoutingError ctx None (Some @"C:\Repos\Proj") resolution
      match blocker with
      | Some err ->
        blockerKindOf err |> Expect.equal "two matching sessions must classify as SessionAmbiguous" BlockerKind.SessionAmbiguous
      | None -> failtest "expected a routing blocker for an ambiguous working directory"
    }

    testCaseTask "WHY — no sessions matching the working directory classify as SessionMissing, distinctly from the ambiguous case above" <| fun () -> task {
      let ctx = mkCtx [] None
      let resolution = SessionResolution.Gone "totally unrelated placeholder text"
      let! blocker = sessionRoutingError ctx None (Some @"C:\Repos\Proj") resolution
      match blocker with
      | Some err ->
        blockerKindOf err |> Expect.equal "zero matching sessions must classify as SessionMissing" BlockerKind.SessionMissing
      | None -> failtest "expected a routing blocker for a missing working directory match"
    }

    testCaseTask "WHY — an explicit unknown session id classifies as SessionMissing via SessionNotFound" <| fun () -> task {
      let resolution = SessionResolution.Gone "session is no longer running"
      let! blocker = sessionRoutingError noSessions (Some "deadbeef") None resolution
      match blocker with
      | Some (SageFsError.SessionNotFound sid) ->
        sid |> Expect.equal "the explicit session id is carried through" "deadbeef"
      | other -> failtestf "expected SessionNotFound, got %A" other
    }

    testCaseTask "WHY — a faulted session classifies as TransportFailure" <| fun () -> task {
      let resolution = SessionResolution.FaultedSession ("sid9", FaultCause.Recorded "warmup failed")
      let! blocker = sessionRoutingError noSessions None (Some @"C:\Repos\Proj") resolution
      match blocker with
      | Some err -> blockerKindOf err |> Expect.equal "a faulted session is a transport-level blocker" BlockerKind.TransportFailure
      | None -> failtest "expected a routing blocker for a faulted session"
    }

    testCaseTask "WHY — a routable session is never a blocker" <| fun () -> task {
      let! blocker = sessionRoutingError noSessions None (Some @"C:\Repos\Proj") (SessionResolution.Routable "sid1")
      blocker |> Expect.isNone "a routable session has nothing to classify"
    }
  ]

[<Tests>]
let targetedVerifyStaleTests =
  testList "targetedVerifyResult — stale loaded state" [
    testCaseTask "WHY — a confirmed-stale loaded file is reported as a real SageFsError classifying to LoadedStateStale, with the SAME presentation text targetedVerify already produces" <| fun () -> task {
      let sessionInfo = mkSessionInfo (SessionId.newId ()) @"C:\Repos\Proj" (SessionLifecycleStatus.Ready { Pid = 1; Port = Some 1 })
      let sid = SessionId.value sessionInfo.Id
      let staleFile : FileStatus =
        { Path = "UserPreferences.fs"
          Readiness = Stale
          LastLoadedAt = Some DateTimeOffset.UtcNow
          IsWatched = true }
      let sessionContext : SessionContext =
        { SessionId = sid
          ProjectNames = [ "Fake" ]
          WorkingDir = @"C:\Repos\Proj"
          Status = "Ready"
          Warmup = WarmupContext.empty
          FileStatuses = [ staleFile ]
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
          AutoOpenNamespaces = true }
      let ctx =
        mkCtx [ sessionInfo ] (Some (fun () -> { SageFsModel.initial() with SessionContext = Some sessionContext }))

      let! expectedText = targetedVerify ctx "mcp" (Some @"C:\Repos\Proj") "UserPreferences.loadFromFile" None
      let! actualText, blocker = targetedVerifyResult ctx "mcp" (Some @"C:\Repos\Proj") "UserPreferences.loadFromFile" None

      actualText |> Expect.equal "targetedVerifyResult must present the identical text targetedVerify does" expectedText
      match blocker with
      | Some err -> blockerKindOf err |> Expect.equal "a confirmed-stale file must classify as LoadedStateStale" BlockerKind.LoadedStateStale
      | None -> failtest "expected a stale-loaded-state blocker"
    }

    testCaseTask "WHY — a current (non-stale) loaded file has no blocker" <| fun () -> task {
      let sessionInfo = mkSessionInfo (SessionId.newId ()) @"C:\Repos\Proj" (SessionLifecycleStatus.Ready { Pid = 1; Port = Some 1 })
      let sid = SessionId.value sessionInfo.Id
      let currentFile : FileStatus =
        { Path = "UserPreferences.fs"
          Readiness = Loaded
          LastLoadedAt = Some DateTimeOffset.UtcNow
          IsWatched = true }
      let sessionContext : SessionContext =
        { SessionId = sid
          ProjectNames = [ "Fake" ]
          WorkingDir = @"C:\Repos\Proj"
          Status = "Ready"
          Warmup = WarmupContext.empty
          FileStatuses = [ currentFile ]
          Workflow = WorkflowTypes.SessionWorkflow.Interactive
          AutoOpenNamespaces = true }
      let ctx =
        mkCtx [ sessionInfo ] (Some (fun () -> { SageFsModel.initial() with SessionContext = Some sessionContext }))

      let! _, blocker = targetedVerifyResult ctx "mcp" (Some @"C:\Repos\Proj") "UserPreferences.loadFromFile" None
      blocker |> Expect.isNone "a current loaded file must not be classified as a blocker"
    }
  ]
