/// `switch_workflow` used to create a NEW session in the same working directory and
/// then stop the old one. The duplicate-session guard refused the create, so the tool
/// failed for every session whose directory the old one still held, which is every
/// session it was ever called on. The daemon's own `SwitchWorkflow` command restarts the
/// SAME session id spawn-first into the target workflow, and the HTTP route and the
/// dashboard already use it. The tool goes through that command too, so there is one
/// way to switch and never a second session.
module SageFs.Tests.McpSwitchWorkflowTests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.WorkerProtocol

let private sid = "a1b2c3d4"
let private workingDirectory = "/switch-workflow"
let private proxy : SessionProxy = fun _ -> async { return failwith "switch_workflow never calls the worker proxy" }

type private Calls =
  { Switched: List<string * WorkflowTypes.SessionWorkflow>
    Created: List<string>
    Stopped: List<string> }

let private ctxWith (switchResult: Result<string, SageFsError>) : McpContext * Calls =
  let calls = { Switched = List(); Created = List(); Stopped = List() }
  let info : SessionInfo =
    { Id = SageFs.McpSessionRouting.toSessionId sid
      Name = None
      Projects = []
      WorkingDirectory = workingDirectory
      SolutionRoot = None
      Status = SessionLifecycleStatus.Ready { Pid = 42; Port = None }
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      ActiveProject = None
      ProjectRoles = []
      App = AppRun.AppRunState.NotRunning
      Rebuild = LastRebuild.NeverRebuilt
      Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync }
  let ops : SessionManagementOps =
    { SessionManagementOps.stub with
        GetProxy = fun _ -> Task.FromResult (Some proxy)
        GetSessionInfo = fun _ -> Task.FromResult (Some info)
        GetAllSessions = fun () -> Task.FromResult [ info ]
        SwitchWorkflow = fun id workflow -> calls.Switched.Add (id, workflow); Task.FromResult switchResult
        CreateSession = fun _ dir _ -> calls.Created.Add dir; Task.FromResult (Error (SageFsError.DuplicateSession (sid, dir)))
        StopSession = fun id -> calls.Stopped.Add id; Task.FromResult (Ok "stopped") }
  let ctx : McpContext =
    { FrictionStore = None
      DiagnosticsChanged = (Event<SageFs.Features.DiagnosticsStore.T>()).Publish
      StateChanged = None
      SessionOps = ops
      SessionMap = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
      McpPort = 0
      Dispatch = None
      GetElmModel = None
      GetElmRegions = None
      GetWarmupContext = None
      GetFeatureState = None
      RecordEval = None
      ActivityTracker = AgentActivityTracker.create ()
      LiveBindings = None
      CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
      GetDaemonHealth = fun () -> None
      GetProcessTelemetry = fun () -> None }
  ctx, calls

[<Tests>]
let tests =
  testList "switch_workflow goes through the daemon's own switch" [

    testTask "WHY — the switch restarts the SAME session into the target workflow and never creates a second session in its directory" {
      let ctx, calls = ctxWith (Ok "Hard reset accepted — replacement worker spawning.")
      let! (text: string) = switchWorkflow ctx "agent" (Some workingDirectory) "livetesting" false
      calls.Switched |> Seq.toList |> List.map fst |> Expect.equal "asked the daemon to switch that session" [ sid ]
      calls.Created.Count |> Expect.equal "no second session was created" 0
      calls.Stopped.Count |> Expect.equal "the session was not stopped: it is the one being restarted" 0
      let doc = JsonDocument.Parse text
      doc.RootElement.GetProperty("Outcome").GetString() |> Expect.equal "outcome token" "executed"
      doc.RootElement.GetProperty("Switched").GetBoolean() |> Expect.isTrue "switched"
      doc.RootElement.GetProperty("NewSessionId").GetString() |> Expect.equal "same session id" sid
    }

    testTask "WHY — a refused switch says so with the daemon's own reason, and still creates nothing" {
      let ctx, calls = ctxWith (Error (SageFsError.HardResetFailed "A rebuild is in progress for this session."))
      let! (text: string) = switchWorkflow ctx "agent" (Some workingDirectory) "livetesting" false
      text |> Expect.stringContains "says the switch failed" "Error switching workflow"
      text |> Expect.stringContains "carries the daemon's reason" "rebuild is in progress"
      calls.Created.Count |> Expect.equal "no second session" 0
    }

    testTask "WHY — a dry run only previews: nothing is switched, created or stopped" {
      let ctx, calls = ctxWith (Ok "unused")
      let! _ = switchWorkflow ctx "agent" (Some workingDirectory) "livetesting" true
      calls.Switched.Count |> Expect.equal "no switch" 0
      calls.Created.Count |> Expect.equal "no create" 0
      calls.Stopped.Count |> Expect.equal "no stop" 0
    }
  ]
