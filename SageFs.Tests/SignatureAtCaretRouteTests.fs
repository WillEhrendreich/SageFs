/// The signature-at-caret route must carry the FCS tooltip across the worker wire. The pure planner
/// (SignatureQueryTests) injects a tooltip, so it cannot catch a wire that drops it; this test drives
/// the real MCP route with a fake worker and asserts the caller sees the compiler's signature.
module SageFs.Tests.SignatureAtCaretRouteTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Extensions.Logging.Abstractions
open SageFs
open SageFs.McpTools
open SageFs.Features.AutoCompletion

let private tmpFile =
  let path = Path.Combine(Path.GetTempPath(), "sagefs-signature-route-" + Guid.NewGuid().ToString("N") + ".fs")
  File.WriteAllText(path, "let answer = 42\nlet double x = x * 2\nlet used = double answer\n")
  path

/// A context whose worker answers completion questions. `reply` decides what each worker message
/// returns, so the test can say what the worker knows about a candidate without a real FSI session.
let private ctxWith (reply: WorkerProtocol.WorkerMessage -> WorkerProtocol.WorkerResponse) (sessionId: string) =
  let result = SageFs.Tests.TestInfrastructure.globalActorResult.Value
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["mcp"] <- sessionId
  let ops : SessionManagementOps = {
    CreateSession = fun _ _ _ -> Task.FromResult(Ok "test-session")
    ListSessions = fun () -> Task.FromResult("No sessions")
    StopSession = fun _ -> Task.FromResult(Ok "stopped")
    PurgeSession = fun _ -> Task.FromResult(Ok "purged")
    RestartSession = fun _ _ -> Task.FromResult(Ok "restarted")
    GetProxy = fun _ -> Task.FromResult(Some (fun msg -> async { return reply msg }))
    GetSessionInfo = fun id ->
      Task.FromResult(
        Some { WorkerProtocol.SessionInfo.Id = id
               Name = None
               Projects = []
               WorkingDirectory = ""
               SolutionRoot = None
               Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 4242; Port = None }
               Workflow = WorkflowTypes.SessionWorkflow.Interactive
               CreatedAt = DateTime.UtcNow
               LastActivity = DateTime.UtcNow
               ActiveProject = None
               ProjectRoles = []
               App = SageFs.AppRun.AppRunState.NotRunning
               Rebuild = LastRebuild.NeverRebuilt
               Reload = SessionReload.NoReloadYet
               Freshness = SageFs.ReplFreshness.InSync })
    GetAllSessions = fun () -> Task.FromResult([])
    UpdateSessionStatus = fun _ _ -> Task.FromResult(())
    NotifyWorkerDied = fun _ -> ()
    ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
    ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
    AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
    EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
    AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
    SwitchWorkflow = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
    GetAdoptedCore = fun _ -> Task.FromResult(None)
    RefetchLiveTestingState = fun _ -> Task.FromResult(())
    GetWarmupProgress = fun _ -> Task.FromResult(None) }
  { FrictionStore = None
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = ops
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    RecordEval = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveBindings = None
    CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None } : McpContext

let private completion =
  WorkerProtocol.WorkerResponse.CompletionResult(
    "rid",
    CompletionAnswer.Answered(
      [ { DisplayText = "double"; ReplacementText = "double"; Kind = CompletionKind.Method } ],
      CompletionTotal.Exact 1))

/// The worker is the only process holding FCS, so the describe reply carries the tooltip the completion
/// wire cannot. The fake worker answers it the way WorkerMain does.
let private reply (msg: WorkerProtocol.WorkerMessage) =
  match msg with
  | WorkerProtocol.WorkerMessage.GetCompletions _ -> completion
  | WorkerProtocol.WorkerMessage.DescribeCompletion _ ->
    WorkerProtocol.WorkerResponse.DescriptionResult("rid", "val double: int -> int")
  | other -> failtestf "unexpected worker message %A" other

[<Tests>]
let signatureAtCaretRouteTests =
  testList "signature-at-caret route" [
    testTask "WHY — the caller sees the compiler's signature, not a refusal, when the worker knows the candidate's tooltip" {
      let ctx = ctxWith reply "aaa00002"
      let! answer =
        McpSignatureRoute.signatureAt
          ctx
          (McpSignatureRoute.Agent "mcp")
          (Some "aaa00002")
          None
          (McpSignatureRoute.SourcePath tmpFile)
          (McpSignatureRoute.CaretLine 2)
          (McpSignatureRoute.CaretColumn 11)
      answer
      |> Expect.stringContains "the FCS signature reaches the caller" "val double: int -> int"
    }
  ]
