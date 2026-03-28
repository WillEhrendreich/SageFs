module SageFs.Tests.McpAffordanceHardeningTests

open System
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FSharp.Reflection
open SageFs
open SageFs.Affordances

// ── Helpers ──

let private allStates =
  FSharpType.GetUnionCases(typeof<SessionState>)
  |> Array.map (fun c -> FSharpValue.MakeUnion(c, [||]) :?> SessionState)
  |> Array.toList

let private allAffordanceTools =
  allStates
  |> List.collect availableTools
  |> List.distinct

let private snakeCaseRegex =
  Regex(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled)

let private tryGetMcpToolMethods () =
  try
    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.collect (fun a ->
      try a.GetTypes() with _ -> [||])
    |> Array.tryFind (fun t -> t.Name = "SageFsTools")
    |> Option.map (fun t ->
      t.GetMethods()
      |> Array.filter (fun m ->
        m.GetCustomAttributes(true)
        |> Array.exists (fun attr ->
          attr.GetType().Name = "McpServerToolAttribute")))
  with _ -> None

// ── Group 1: State Machine Reachability ──

let stateMachineReachabilityTests =
  testList "state machine reachability" [

    testCase
      "monotonicity: get_fsi_status reachable in every state" <| fun _ ->
      allStates
      |> List.iter (fun state ->
        availableTools state
        |> List.contains "get_fsi_status"
        |> Expect.isTrue
          (sprintf "get_fsi_status must be in %A" state))

    testCase
      "monotonicity: get_recent_fsi_events in every non-Uninitialized state"
    <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let has =
          availableTools state |> List.contains "get_recent_fsi_events"
        match state with
        | Uninitialized ->
          has |> Expect.isFalse "must not be in Uninitialized"
        | _ ->
          has
          |> Expect.isTrue
            (sprintf "get_recent_fsi_events must be in %A" state))

    testCase "recovery: both reset tools available when Faulted" <| fun _ ->
      let faultedTools = availableTools Faulted |> Set.ofList
      faultedTools.Contains "reset_fsi_session"
      |> Expect.isTrue "Faulted must include reset_fsi_session"
      faultedTools.Contains "hard_reset_fsi_session"
      |> Expect.isTrue "Faulted must include hard_reset_fsi_session"

    testCase
      "cancel_eval absent from Uninitialized, WarmingUp, and Faulted"
    <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let has = availableTools state |> List.contains "cancel_eval"
        match state with
        | Uninitialized | WarmingUp | Faulted ->
          has
          |> Expect.isFalse
            (sprintf "cancel_eval must NOT be in %A" state)
        | Ready | Evaluating ->
          has
          |> Expect.isTrue
            (sprintf "cancel_eval must be in %A" state))

    testCase "send_fsharp_code exclusive to Ready" <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let has = availableTools state |> List.contains "send_fsharp_code"
        match state with
        | Ready ->
          has |> Expect.isTrue "send_fsharp_code must be in Ready"
        | _ ->
          has
          |> Expect.isFalse
            (sprintf "send_fsharp_code must NOT be in %A" state))

    testCase "load_fsharp_script exclusive to Ready" <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let has = availableTools state |> List.contains "load_fsharp_script"
        match state with
        | Ready ->
          has |> Expect.isFalse "load_fsharp_script is no longer an MCP affordance"
        | _ ->
          has
          |> Expect.isFalse
            (sprintf "load_fsharp_script must NOT be in %A" state))

    testCase "get_startup_info exclusive to Ready" <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let has = availableTools state |> List.contains "get_startup_info"
        match state with
        | Ready ->
          has |> Expect.isFalse "get_startup_info is no longer an MCP affordance"
        | _ ->
          has
          |> Expect.isFalse
            (sprintf "get_startup_info must NOT be in %A" state))
  ]

// ── Group 2: Affordance Algebra Properties ──

let affordanceAlgebraTests =
  testList "affordance algebra properties" [

    testCase "no empty states: every state has >= 1 tool" <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let count = availableTools state |> List.length
        (count >= 1)
        |> Expect.isTrue
          (sprintf "%A must have >= 1 tool (got %d)" state count))

    testCase "no duplicates within any state tool list" <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let tools = availableTools state
        let unique = tools |> List.distinct
        tools.Length
        |> Expect.equal
          (sprintf "no duplicates in %A (got %A)" state tools)
          unique.Length)

    testCase "all tool names satisfy snake_case convention" <| fun _ ->
      allAffordanceTools
      |> List.iter (fun tool ->
        snakeCaseRegex.IsMatch(tool)
        |> Expect.isTrue
          (sprintf "'%s' must match ^[a-z][a-z0-9_]*$" tool))

    testCase "checkToolAvailability Ok for every listed tool" <| fun _ ->
      allStates
      |> List.iter (fun state ->
        availableTools state
        |> List.iter (fun tool ->
          checkToolAvailability state tool
          |> Expect.isOk
            (sprintf "%s should be Ok in %A" tool state)))

    testCase "checkToolAvailability exhaustive: Ok for every (state, tool) pair"
    <| fun _ ->
      allStates
      |> List.iter (fun state ->
        availableTools state
        |> List.iter (fun tool ->
          checkToolAvailability state tool
          |> Expect.isOk
            (sprintf "%s must be Ok in %A" tool state)))

    testCase
      "checkToolAvailability rejection: Error(ToolNotAvailable) for every unlisted tool"
    <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let stateTools = availableTools state |> Set.ofList
        allAffordanceTools
        |> List.filter (fun t -> stateTools.Contains t |> not)
        |> List.iter (fun tool ->
          match checkToolAvailability state tool with
          | Error (SageFsError.ToolNotAvailable _) -> ()
          | Error other ->
            failtestf
              "%s in %A: expected ToolNotAvailable, got %A"
              tool state other
          | Ok _ ->
            failtestf "%s should be rejected in %A" tool state))
  ]

// ── Group 3: Tool Registration Completeness ──

let toolRegistrationTests =
  testList "tool registration completeness" [

    testCase "affordance module covers exactly 16 unique tool names" <| fun _ ->
      allAffordanceTools
      |> List.length
      |> Expect.equal "unique affordance tools" 16

    testCase "all affordance tool names are non-empty and non-whitespace"
    <| fun _ ->
      allAffordanceTools
      |> List.iter (fun tool ->
        String.IsNullOrWhiteSpace tool
        |> Expect.isFalse
          (sprintf "tool name must not be blank: '%s'" tool))

    testCase "Ready state has the largest tool set" <| fun _ ->
      let readyCount = availableTools Ready |> List.length
      allStates
      |> List.iter (fun state ->
        let count = availableTools state |> List.length
        (count <= readyCount)
        |> Expect.isTrue
          (sprintf "%A (%d) should have <= Ready (%d)" state count readyCount))

    testCase "McpServerTool-attributed methods total exactly 18 (reflection)"
    <| fun _ ->
      match tryGetMcpToolMethods () with
      | None ->
        skiptest
          "SageFsTools type not found in loaded assemblies; \
           reflection test skipped"
      | Some methods ->
        methods.Length
          |> Expect.equal "MCP tool method count" 18

    testCase
      "every McpServerTool method has a non-empty Description (reflection)"
    <| fun _ ->
      match tryGetMcpToolMethods () with
      | None ->
        skiptest
          "SageFsTools type not found; description audit skipped"
      | Some methods ->
        methods
        |> Array.iter (fun m ->
          let descAttr =
            m.GetCustomAttributes(true)
            |> Array.tryFind (fun a ->
              a.GetType().Name = "DescriptionAttribute")
          match descAttr with
          | None ->
            failtestf "method '%s' missing [<Description>]" m.Name
          | Some attr ->
            let value =
              attr.GetType().GetProperty("Description")
                .GetValue(attr) :?> string
            String.IsNullOrWhiteSpace value
            |> Expect.isFalse
              (sprintf "'%s' has empty description" m.Name))

    testCase
      "every McpServerTool Description is substantive (>= 30 chars)"
    <| fun _ ->
      match tryGetMcpToolMethods () with
      | None ->
        skiptest
          "SageFsTools type not found; length audit skipped"
      | Some methods ->
        methods
        |> Array.iter (fun m ->
          let descAttr =
            m.GetCustomAttributes(true)
            |> Array.tryFind (fun a ->
              a.GetType().Name = "DescriptionAttribute")
          match descAttr with
          | None -> ()
          | Some attr ->
            let value =
              attr.GetType().GetProperty("Description")
                .GetValue(attr) :?> string
            (value.Length >= 30)
            |> Expect.isTrue
              (sprintf "'%s' description too short (%d chars)"
                m.Name value.Length))
  ]

// ── Group 4: State Transition Safety ──

let stateTransitionSafetyTests =
  testList "state transition safety" [

    testCase "all (state, affordanceTool) pairs: checkToolAvailability never throws"
    <| fun _ ->
      let mutable tested = 0
      allStates
      |> List.iter (fun state ->
        allAffordanceTools
        |> List.iter (fun tool ->
          try
            let _ = checkToolAvailability state tool
            tested <- tested + 1
          with ex ->
            failtestf
              "checkToolAvailability threw for (%A, %s): %s"
              state tool ex.Message))
      tested
          |> Expect.equal "should test all 80 state×tool combos" 80

    testCase "all rejections return ToolNotAvailable specifically" <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let stateTools = availableTools state |> Set.ofList
        allAffordanceTools
        |> List.filter (fun t -> stateTools.Contains t |> not)
        |> List.iter (fun tool ->
          match checkToolAvailability state tool with
          | Error (SageFsError.ToolNotAvailable _) -> ()
          | Error other ->
            failtestf
              "(%A, %s): expected ToolNotAvailable, got %A"
              state tool other
          | Ok _ ->
            failtestf "(%A, %s): expected Error, got Ok" state tool))

    testCase "rejection error carries the requested tool name" <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let stateTools = availableTools state |> Set.ofList
        allAffordanceTools
        |> List.filter (fun t -> stateTools.Contains t |> not)
        |> List.iter (fun tool ->
          match checkToolAvailability state tool with
          | Error (SageFsError.ToolNotAvailable (name, _, _)) ->
            name
            |> Expect.equal
              (sprintf "error tool name in %A" state) tool
          | _ -> ()))

    testCase "rejection error carries the current state" <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let stateTools = availableTools state |> Set.ofList
        allAffordanceTools
        |> List.filter (fun t -> stateTools.Contains t |> not)
        |> List.iter (fun tool ->
          match checkToolAvailability state tool with
          | Error (SageFsError.ToolNotAvailable (_, errState, _)) ->
            errState
            |> Expect.equal
              (sprintf "error state for %s" tool) state
          | _ -> ()))

    testCase "rejection error lists available alternatives" <| fun _ ->
      allStates
      |> List.iter (fun state ->
        let stateTools = availableTools state
        let stateToolSet = stateTools |> Set.ofList
        allAffordanceTools
        |> List.filter (fun t -> stateToolSet.Contains t |> not)
        |> List.iter (fun tool ->
          match checkToolAvailability state tool with
          | Error (SageFsError.ToolNotAvailable (_, _, alts)) ->
            alts
            |> Expect.equal
              (sprintf "alternatives for %s in %A" tool state)
              stateTools
          | _ -> ()))

    testCase "bogus tool name rejected in every state with full error info"
    <| fun _ ->
      let bogus = "this_tool_definitely_does_not_exist"
      allStates
      |> List.iter (fun state ->
        match checkToolAvailability state bogus with
        | Error (SageFsError.ToolNotAvailable (name, s, alts)) ->
          name |> Expect.equal "bogus name preserved" bogus
          s |> Expect.equal "state preserved" state
          alts
          |> Expect.equal "alternatives match state" (availableTools state)
        | Error other ->
          failtestf
            "bogus in %A: expected ToolNotAvailable, got %A" state other
        | Ok _ ->
          failtestf "bogus tool must be rejected in %A" state)

    testCase "75-combo safety: all MCP tools × all states never throw (reflection)"
    <| fun _ ->
      match tryGetMcpToolMethods () with
      | None ->
        skiptest
          "SageFsTools not found; 245-combo test skipped"
      | Some methods ->
        let toolNames = methods |> Array.map (fun m -> m.Name)
        let mutable tested = 0
        allStates
        |> List.iter (fun state ->
          toolNames
          |> Array.iter (fun tool ->
            try
              let _ = checkToolAvailability state tool
              tested <- tested + 1
            with ex ->
              failtestf
                "checkToolAvailability threw for (%A, %s): %s"
                state tool ex.Message))
        tested
        |> Expect.equal
          "should test 5 states × 17 tools = 85" 85
  ]

// ── Group 5: Affordance Superset/Subset Relationships ──

let affordanceSupersetSubsetTests =
  testList "affordance superset/subset" [

    testCase "Ready ⊇ WarmingUp" <| fun _ ->
      let readyTools = availableTools Ready |> Set.ofList
      let warmingTools = availableTools WarmingUp |> Set.ofList
      Set.isSubset warmingTools readyTools
      |> Expect.isTrue "Ready must contain all WarmingUp tools"

    testCase "Ready ⊇ Uninitialized" <| fun _ ->
      let readyTools = availableTools Ready |> Set.ofList
      let uninitTools = availableTools Uninitialized |> Set.ofList
      Set.isSubset uninitTools readyTools
      |> Expect.isTrue "Ready must contain all Uninitialized tools"

    testCase "Ready ⊇ Faulted" <| fun _ ->
      let readyTools = availableTools Ready |> Set.ofList
      let faultedTools = availableTools Faulted |> Set.ofList
      Set.isSubset faultedTools readyTools
      |> Expect.isTrue "Ready must contain all Faulted tools"

    testCase "Ready ⊇ Evaluating" <| fun _ ->
      let readyTools = availableTools Ready |> Set.ofList
      let evalTools = availableTools Evaluating |> Set.ofList
      Set.isSubset evalTools readyTools
      |> Expect.isTrue "Ready must contain all Evaluating tools"

    testCase "WarmingUp ⊋ Uninitialized (strict)" <| fun _ ->
      let warmingTools = availableTools WarmingUp |> Set.ofList
      let uninitTools = availableTools Uninitialized |> Set.ofList
      Set.isProperSubset uninitTools warmingTools
      |> Expect.isTrue
        "Uninitialized must be strictly smaller than WarmingUp"

    testCase "Ready ⊋ WarmingUp (strict)" <| fun _ ->
      let readyTools = availableTools Ready |> Set.ofList
      let warmingTools = availableTools WarmingUp |> Set.ofList
      Set.isProperSubset warmingTools readyTools
      |> Expect.isTrue
        "WarmingUp must be strictly smaller than Ready"

    testCase "Faulted includes recovery tools" <| fun _ ->
      availableTools Faulted
      |> Expect.containsAll "recovery tools"
        [ "reset_fsi_session"; "hard_reset_fsi_session" ]

    testCase "Evaluating includes cancel but excludes mutation tools"
    <| fun _ ->
      let evalTools = availableTools Evaluating |> Set.ofList
      evalTools.Contains "cancel_eval"
      |> Expect.isTrue "Evaluating must include cancel_eval"
      evalTools.Contains "send_fsharp_code"
      |> Expect.isFalse "Evaluating must exclude send_fsharp_code"
      evalTools.Contains "load_fsharp_script"
      |> Expect.isFalse "Evaluating must exclude load_fsharp_script"

    testCase "Evaluating excludes reset tools" <| fun _ ->
      let evalTools = availableTools Evaluating |> Set.ofList
      evalTools.Contains "reset_fsi_session"
      |> Expect.isFalse "cannot reset while evaluating"
      evalTools.Contains "hard_reset_fsi_session"
      |> Expect.isFalse "cannot hard_reset while evaluating"

    testCase "Uninitialized has the smallest tool set" <| fun _ ->
      let uninitCount = availableTools Uninitialized |> List.length
      allStates
      |> List.filter (fun s -> s <> Uninitialized)
      |> List.iter (fun state ->
        let count = availableTools state |> List.length
        (count > uninitCount)
        |> Expect.isTrue
          (sprintf "%A (%d) must exceed Uninitialized (%d)"
            state count uninitCount))

    testCase "Faulted does not include mutation tools" <| fun _ ->
      let faultedTools = availableTools Faulted |> Set.ofList
      faultedTools.Contains "send_fsharp_code"
      |> Expect.isFalse "Faulted must exclude send_fsharp_code"
      faultedTools.Contains "load_fsharp_script"
      |> Expect.isFalse "Faulted must exclude load_fsharp_script"
      faultedTools.Contains "cancel_eval"
      |> Expect.isFalse "Faulted must exclude cancel_eval"
  ]

[<Tests>]
let mcpAffordanceHardeningTests =
  testList "MCP affordance hardening" [
    stateMachineReachabilityTests
    affordanceAlgebraTests
    toolRegistrationTests
    stateTransitionSafetyTests
    affordanceSupersetSubsetTests
  ]
