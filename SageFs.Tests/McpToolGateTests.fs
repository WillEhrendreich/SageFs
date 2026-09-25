module SageFs.Tests.McpToolGateTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FSharp.Reflection
open SageFs
open SageFs.Affordances
open SageFs.McpTools
open SageFs.WorkerProtocol

// ── Helpers ───────────────────────────────────────────────────────────

let private allStates =
  FSharpType.GetUnionCases(typeof<SessionState>)
  |> Array.map (fun c -> FSharpValue.MakeUnion(c, [||]) :?> SessionState)
  |> Array.toList

let private allModelTools =
  allStates
  |> List.collect availableTools
  |> List.distinct

/// [<McpServerTool>]-attributed public methods on SageFs.Server.McpTools.SageFsTools.
/// Referenced via `typeof` (not a runtime assembly scan) so the type is always
/// resolved and this contract can never silently skip on assembly load order —
/// the earlier scan-and-skip let switch_workflow ship registered-but-ungated.
let private registeredMcpToolNames () =
  typeof<SageFs.Server.McpTools.SageFsTools>.GetMethods()
  |> Array.filter (fun m ->
    m.GetCustomAttributes(true)
    |> Array.exists (fun attr -> attr.GetType().Name = "McpServerToolAttribute"))
  |> Array.map (fun m -> m.Name)

let private registeredToolSet () =
  registeredMcpToolNames () |> Set.ofArray

let private isRegistered () =
  registeredMcpToolNames () |> Array.isEmpty |> not

// ── Group 1: declaration completeness (structural honesty) ────────────

let declarationCompletenessTests =
  testList "gate declaration completeness" [

    testCase "every registered McpServerTool is declared in the gate domain"
    <| fun _ ->
      if not (isRegistered ()) then skiptest "SageFsTools type not found; reflection skipped"
      let registered = registeredToolSet ()
      let undeclared =
        registered
        |> Set.filter (fun name -> toolGate name |> Option.isNone)
      undeclared
      |> Set.toList
      |> Expect.equal
        "every registered tool must have a ToolGate classification (undeclared tools bypass the model)" []

    testCase "gate domain declares exactly the registered tool set (no strays)"
    <| fun _ ->
      if not (isRegistered ()) then skiptest "SageFsTools type not found; reflection skipped"
      declaredGateTools
      |> Set.ofList
      |> Expect.equal
        "declared gate tools must equal registered MCP tools"
        (registeredToolSet ())

    testCase "a tool restricted to a subset of states must be StateGated"
    <| fun _ ->
      // availableTools is the per-state policy. A tool that is absent from at
      // least one state is state-dependent — it can never be AlwaysAvailable.
      allStates
      |> List.collect availableTools
      |> List.distinct
      |> List.iter (fun tool ->
        let missingState =
          allStates
          |> List.tryFind (fun state ->
            availableTools state |> List.contains tool |> not)
        match missingState with
        | Some state ->
          match toolGate tool with
          | Some ToolGate.StateGated -> ()
          | Some ToolGate.AlwaysAvailable when tool = "get_daemon_status" -> ()
          | Some ToolGate.AlwaysAvailable ->
            failtestf
              "'%s' is missing from %A's availableTools yet declared AlwaysAvailable" tool state
          | None ->
            failtestf "'%s' appears in availableTools but has no gate classification" tool
        | None -> ())

    testCase "every StateGated tool is restricted by availableTools in at least one state"
    <| fun _ ->
      // The gate must be able to FIRE for each state-gated tool: no tool may be
      // declared state-dependent while being available in every state (that would
      // make the enforcement vacuous).
      declaredGateTools
      |> List.iter (fun tool ->
        match toolGate tool with
        | Some ToolGate.StateGated ->
          let unavailableStates =
            allStates
            |> List.filter (fun state ->
              availableTools state |> List.contains tool |> not)
          unavailableStates
          |> List.isEmpty
          |> Expect.isFalse
            (sprintf "'%s' is StateGated but is available in every state" tool)
        | _ -> ())

    testCase "all declared AlwaysAvailable tools are callable in every state"
    <| fun _ ->
      declaredGateTools
      |> List.iter (fun tool ->
        match toolGate tool with
        | Some ToolGate.AlwaysAvailable ->
          allStates
          |> List.iter (fun state ->
            checkToolCallAllowed state tool
            |> Expect.isOk
              (sprintf "%s must be allowed in %A" tool state))
        | _ -> ())
  ]

// ── Group 2: checkToolCallAllowed decision semantics ──────────────────

let gateDecisionTests =
  testList "gate decision semantics" [

    testCase "StateGated tools match checkToolAvailability exactly"
    <| fun _ ->
      allStates
      |> List.iter (fun state ->
        allModelTools
        |> List.iter (fun tool ->
          match toolGate tool with
          | Some ToolGate.StateGated ->
            checkToolCallAllowed state tool
            |> Expect.equal
              (sprintf "%s in %A must delegate to checkToolAvailability" tool state)
              (checkToolAvailability state tool)
          | _ -> ()))

    testCase "switch_workflow is declared and callable in Ready (the web-detection hint must point at a real tool)"
    <| fun _ ->
      // Regression: switch_workflow was a registered [<McpServerTool>] but absent
      // from the gate, so checkToolCallAllowed failed it closed (undeclared) and
      // the "use switch_workflow to switch to live" hint pointed at an uncallable
      // tool. It must be declared, StateGated, and offered in Ready.
      toolGate "switch_workflow"
      |> Expect.equal "switch_workflow must be gated, not undeclared (undeclared fails closed)" (Some ToolGate.StateGated)
      availableTools Ready
      |> List.contains "switch_workflow"
      |> Expect.isTrue "switch_workflow must be offered once a session is Ready"
      checkToolCallAllowed Ready "switch_workflow"
      |> Expect.isOk "switch_workflow must be allowed to run in Ready"

    testCase "switch_session is AlwaysAvailable and callable in every state (navigating away from a busy/faulted session must never be blocked)"
    <| fun _ ->
      // Regression (roast-9 §2): switch_session was StateGated, so the gate —
      // which evaluates the ACTIVE session's state — rejected it whenever the
      // active session was Evaluating or Faulted, blocking the exact recovery
      // switch_session exists for. It is navigation, not code execution: it only
      // rebinds which session the agent views, so it has no session-state
      // dependence and must be callable in every state, like stop_session.
      toolGate "switch_session"
      |> Expect.equal
        "switch_session must be AlwaysAvailable (navigation has no session-state dependence)"
        (Some ToolGate.AlwaysAvailable)
      allStates
      |> List.iter (fun state ->
        checkToolCallAllowed state "switch_session"
        |> Expect.isOk (sprintf "switch_session must be callable in %A" state))

    testCase "gate rejects an unavailable StateGated tool with ToolNotAvailable"
    <| fun _ ->
      // WarmingUp is a canonical wrong state for code execution.
      match checkToolCallAllowed SageFs.SessionState.WarmingUp "send_fsharp_code" with
      | Error (SageFsError.ToolNotAvailable (name, state, _)) ->
        name |> Expect.equal "error names the tool" "send_fsharp_code"
        state |> Expect.equal "error names the state" SageFs.SessionState.WarmingUp
      | Error other -> failtestf "expected ToolNotAvailable, got %A" other
      | Ok _ -> failtest "send_fsharp_code must be rejected in WarmingUp"

    testCase "gate allows an available StateGated tool"
    <| fun _ ->
      checkToolCallAllowed Ready "send_fsharp_code"
      |> Expect.isOk "send_fsharp_code must be allowed in Ready"

    testCase "gate allows state-free tools in every state"
    <| fun _ ->
      [ "get_daemon_status"; "get_session_status"; "list_sessions"; "get_friction_report"; "report_friction"
        "acquire_full_build_lease"; "acquire_test_suite_lease"; "acquire_run_app_lease"; "release_work_lease" ]
      |> List.iter (fun tool ->
        allStates
        |> List.iter (fun state ->
          checkToolCallAllowed state tool
          |> Expect.isOk (sprintf "%s must be allowed in %A" tool state)))

    testCase "undeclared tool fails closed"
    <| fun _ ->
      let bogus = "this_tool_is_not_declared_anywhere"
      allStates
      |> List.iter (fun state ->
        match checkToolCallAllowed state bogus with
        | Error (SageFsError.ToolNotAvailable (name, _, _)) ->
          name |> Expect.equal "bogus name preserved" bogus
        | Error other -> failtestf "expected ToolNotAvailable, got %A" other
        | Ok _ -> failtestf "%s must fail closed in %A" bogus state)
  ]

// ── Group 3: enforceToolCallGate session-aware enforcement ────────────

let private mkStatusProxy (status: SessionStatus) : SessionProxy =
  fun _msg ->
    async {
      return WorkerProtocol.WorkerResponse.StatusResult(
        "reply",
        { WorkerProtocol.WorkerStatusSnapshot.Status = status
          StatusMessage = None
          EvalCount = 0
          AvgDurationMs = 0L
          MinDurationMs = 0L
          MaxDurationMs = 0L; Projects = []; CoreVersion = "0.0.0-test" })
    }

let private mkContextForSession (status: SessionStatus) : McpContext * string =
  let workingDir = "C:\\gate-test"
  let sessionId = WorkerProtocol.SessionId.newId ()
  let info : WorkerProtocol.SessionInfo = {
    Id = sessionId
    Name = None
    Projects = []
    WorkingDirectory = workingDir
    SolutionRoot = None
    Status = SessionLifecycleStatus.ofWorkerReport (SessionLifecycleStatus.Ready { Pid = 42; Port = None }) status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning
  }
  let ops : SessionManagementOps = {
    CreateSession = fun _ _ _ -> Task.FromResult(Ok "stub")
    ListSessions = fun () -> Task.FromResult("[]")
    StopSession = fun _ -> Task.FromResult(Ok "stub")
    PurgeSession = fun _ -> Task.FromResult(Ok "stub")
    RestartSession = fun _ _ -> Task.FromResult(Ok "stub")
    GetProxy = fun _ -> Task.FromResult(Some (mkStatusProxy status))
    GetSessionInfo = fun _ -> Task.FromResult(Some info)
    GetAllSessions = fun () -> Task.FromResult([ info ])
    UpdateSessionStatus = fun _ _ -> Task.FromResult(())
    NotifyWorkerDied = fun _ -> ()
    ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
    ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
    AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
    EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
    AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
    SwitchWorkflow = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
    GetAdoptedCore = fun _ -> Task.FromResult(None)
    GetWarmupProgress = fun _ -> Task.FromResult(None)
  }
  let ctx : McpContext = {
    FrictionStore = None
    DiagnosticsChanged = (Event<SageFs.Features.DiagnosticsStore.T>()).Publish
    StateChanged = None
    SessionOps = ops
    SessionMap = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None; RecordEval = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveSnapshotSink = None
    CohortOwner = None
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None
  }
  ctx, workingDir

/// Run enforceToolCallGate through its full resolution path with a routable
/// session in the given status (routed by working_directory, as the MCP tools do).
let private enforceWithSession (status: SessionStatus) (tool: string) =
  task {
    let ctx, wd = mkContextForSession status
    let! result =
      SageFs.McpTools.enforceToolCallGate
        ctx "mcp" None (Some wd) tool
    return result
  }

/// Run enforceToolCallGate when NO session exists (Gone routing).
let private enforceNoSession (tool: string) =
  task {
    let ctx, _ = mkContextForSession WorkerProtocol.SessionStatus.Ready
    // Empty the registry so resolution yields Gone.
    let emptyOps : SessionManagementOps = {
      ctx.SessionOps with
        GetAllSessions = fun () -> Task.FromResult([])
        GetProxy = fun _ -> Task.FromResult(None)
        GetSessionInfo = fun _ -> Task.FromResult(None)
    }
    let ctx = { ctx with SessionOps = emptyOps }
    let! result =
      SageFs.McpTools.enforceToolCallGate ctx "mcp" None None tool
    return result
  }

let enforcementTests =
  testList "enforceToolCallGate session-aware enforcement" [

    testTask "send_fsharp_code rejected while session is WarmingUp" {
      let! result = enforceWithSession WorkerProtocol.SessionStatus.Starting "send_fsharp_code"
      match result with
      | Error msg ->
        Expect.stringContains "message names the tool" "send_fsharp_code" msg
      | Ok _ -> failtest "send_fsharp_code must be rejected while warming up"
    }

    testTask "send_fsharp_code allowed when session is Ready" {
      let! result = enforceWithSession WorkerProtocol.SessionStatus.Ready "send_fsharp_code"
      Expect.isOk "send_fsharp_code must pass in Ready" result
    }

    testTask "send_fsharp_code rejected while session is Evaluating" {
      let! result = enforceWithSession WorkerProtocol.SessionStatus.Evaluating "send_fsharp_code"
      match result with
      | Error msg ->
        Expect.stringContains "message names the tool" "send_fsharp_code" msg
      | Ok _ -> failtest "send_fsharp_code must be rejected while Evaluating"
    }

    testTask "cancel_eval allowed while session is Evaluating" {
      let! result = enforceWithSession WorkerProtocol.SessionStatus.Evaluating "cancel_eval"
      Expect.isOk "cancel_eval must pass in Evaluating" result
    }

    testTask "reset_fsi_session allowed when session is Faulted" {
      let! result = enforceWithSession WorkerProtocol.SessionStatus.Faulted "reset_fsi_session"
      Expect.isOk "reset_fsi_session must pass in Faulted" result
    }

    testTask "send_fsharp_code rejected when session is Faulted" {
      let! result = enforceWithSession WorkerProtocol.SessionStatus.Faulted "send_fsharp_code"
      match result with
      | Error _ -> ()
      | Ok _ -> failtest "send_fsharp_code must be rejected when Faulted"
    }

    testTask "state-free tools pass regardless of session state" {
      let! statusResult =
        enforceWithSession WorkerProtocol.SessionStatus.Starting "get_session_status"
      Expect.isOk "get_session_status must pass while warming up" statusResult
      let! daemonResult =
        enforceWithSession WorkerProtocol.SessionStatus.Starting "get_daemon_status"
      Expect.isOk "get_daemon_status must pass while warming up" daemonResult
      let! eventsResult =
        enforceWithSession WorkerProtocol.SessionStatus.Starting "get_friction_report"
      Expect.isOk "get_friction_report must pass while warming up" eventsResult
      let! listResult =
        enforceWithSession WorkerProtocol.SessionStatus.Evaluating "list_sessions"
      Expect.isOk "list_sessions must pass while Evaluating" listResult
    }

    testTask "no session: session-creation allowed, code execution rejected" {
      for tool in [ "create_project_session"; "create_solution_session"; "create_bare_session" ] do
        let! createResult = enforceNoSession tool
        Expect.isOk (sprintf "%s must pass when no session exists" tool) createResult
      let! statusResult = enforceNoSession "get_session_status"
      Expect.isOk "get_session_status must pass when no session exists" statusResult
      let! daemonResult = enforceNoSession "get_daemon_status"
      Expect.isOk "get_daemon_status must pass when no session exists" daemonResult
      let! evalResult = enforceNoSession "send_fsharp_code"
      match evalResult with
      | Error _ -> ()
      | Ok _ -> failtest "send_fsharp_code must be rejected when no session exists"
    }
  ]

[<Tests>]
let mcpToolGateTests =
  testList "MCP tool gate" [
    declarationCompletenessTests
    gateDecisionTests
    enforcementTests
  ]
