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
let private registeredMcpToolNames () =
  AppDomain.CurrentDomain.GetAssemblies()
  |> Array.collect (fun a ->
    try a.GetTypes() with _ -> [||])
  |> Array.tryFind (fun t -> t.Name = "SageFsTools")
  |> Option.map (fun t ->
    t.GetMethods()
    |> Array.filter (fun m ->
      m.GetCustomAttributes(true)
      |> Array.exists (fun attr -> attr.GetType().Name = "McpServerToolAttribute"))
    |> Array.map (fun m -> m.Name))
  |> Option.defaultValue [||]

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
      [ "get_fsi_status"; "list_sessions"; "get_friction_report"; "report_friction" ]
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
          MaxDurationMs = 0L })
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
    Status = status
    FaultReason = None
    WorkerPid = Some 42
    WorkerPort = None
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
  }
  let ops : SessionManagementOps = {
    CreateSession = fun _ _ _ -> Task.FromResult(Ok "stub")
    ListSessions = fun () -> Task.FromResult("[]")
    StopSession = fun _ -> Task.FromResult(Ok "stub")
    DisposeSession = fun _ -> Task.FromResult(Ok "stub")
    PurgeSession = fun _ -> Task.FromResult(Ok "stub")
    RestartSession = fun _ _ -> Task.FromResult(Ok "stub")
    GetProxy = fun _ -> Task.FromResult(Some (mkStatusProxy status))
    GetSessionInfo = fun _ -> Task.FromResult(Some info)
    GetAllSessions = fun () -> Task.FromResult([ info ])
    UpdateSessionStatus = fun _ _ -> Task.FromResult(())
    GetStandbyInfo = fun () -> Task.FromResult(StandbyInfo.NoPool)
    NotifyWorkerDied = fun _ -> ()
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
    GetFeatureState = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveSnapshotSink = None
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
        enforceWithSession WorkerProtocol.SessionStatus.Starting "get_fsi_status"
      Expect.isOk "get_fsi_status must pass while warming up" statusResult
      let! eventsResult =
        enforceWithSession WorkerProtocol.SessionStatus.Starting "get_friction_report"
      Expect.isOk "get_friction_report must pass while warming up" eventsResult
      let! listResult =
        enforceWithSession WorkerProtocol.SessionStatus.Evaluating "list_sessions"
      Expect.isOk "list_sessions must pass while Evaluating" listResult
    }

    testTask "no session: session-creation allowed, code execution rejected" {
      let! createResult = enforceNoSession "create_session"
      Expect.isOk "create_session must pass when no session exists" createResult
      let! statusResult = enforceNoSession "get_fsi_status"
      Expect.isOk "get_fsi_status must pass when no session exists" statusResult
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
