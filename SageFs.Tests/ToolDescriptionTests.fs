module SageFs.Tests.ToolDescriptionTests

open Expecto
open Expecto.Flip
open VerifyExpecto
open VerifyTests
open System
open System.IO
open System.ComponentModel
open System.Reflection
open SageFs.Server.McpTools

do try VerifierSettings.DisableRequireUniquePrefix() with _ -> ()

let snapshotsDir =Path.Combine(__SOURCE_DIRECTORY__, "snapshots")

let verifyText name (value: string) =
  let settings = VerifySettings()
  settings.UseDirectory(snapshotsDir)
  settings.DisableDiff()
  let normalized = value.Replace("\r\n", "\n")
  Verifier.Verify(name, normalized, "txt", settings).ToTask()

/// Extract all [<Description>] attributes from MCP tool methods
let toolDescriptions =
  typeof<SageFsTools>.GetMethods(BindingFlags.Instance ||| BindingFlags.Public)
  |> Array.choose (fun m ->
    m.GetCustomAttribute<DescriptionAttribute>()
    |> Option.ofObj
    |> Option.map (fun attr -> m.Name, attr.Description))
  |> Array.toList

let registeredToolDescriptions =
  typeof<SageFsTools>.GetMethods(BindingFlags.Instance ||| BindingFlags.Public)
  |> Array.filter (fun m ->
    m.GetCustomAttributes(true)
    |> Array.exists (fun attr -> attr.GetType().Name = "McpServerToolAttribute"))
  |> Array.choose (fun m ->
    m.GetCustomAttribute<DescriptionAttribute>()
    |> Option.ofObj
    |> Option.map (fun attr -> m.Name, attr.Description))
  |> Array.toList

/// Registered MCP tool methods (those carrying [<McpServerTool>])
let registeredToolMethods =
  typeof<SageFsTools>.GetMethods(BindingFlags.Instance ||| BindingFlags.Public)
  |> Array.filter (fun m ->
    m.GetCustomAttributes(true)
    |> Array.exists (fun attr -> attr.GetType().Name = "McpServerToolAttribute"))

/// WHY — MCP SDK reflection marks every parameter WITHOUT a default value as
/// REQUIRED in the tool schema. A parameter whose handler tolerates absence but
/// whose signature lacks a default makes the schema lie to agents: omitting it
/// throws ArgumentException inside the marshaller instead of reaching the handler
/// (observed 2026-08: send_fsharp_code without block_start_line crashed the tool call).
/// Because — every parameter must either be genuinely required or carry a default,
/// so the reflected schema matches what handlers actually accept.
let requiredParamsByTool =
  [ "send_fsharp_code", set ["agentName"; "code"]
    "check_fsharp_code", set ["code"]
    "create_session", set ["projects"; "working_directory"]
    "stop_session", set ["session_id"]
    "switch_session", set ["session_id"]
    "targeted_verify", set ["behavior"]
    "report_friction", set ["tool_name"; "feedback_kind"; "short_reason"]
    "explain_test_failure", set ["test_name"] ]
  |> Map.ofList

[<Tests>]
let toolSchemaHonestyTests =
  testList "MCP tool schema honesty" [

    testCase "WHY — registered MCP tools — every non-defaulted parameter is intentionally required because reflection turns missing defaults into hard schema requirements that crash agent calls"
    <| fun _ ->
      let failures =
        registeredToolMethods
        |> Array.collect (fun m ->
          let expected =
            requiredParamsByTool
            |> Map.tryFind m.Name
            |> Option.defaultWith (fun _ -> Set.empty)
          let actualRequired =
            m.GetParameters()
            |> Array.filter (fun p -> not p.HasDefaultValue)
            |> Array.map (fun p -> p.Name)
            |> set
          if actualRequired = expected then [||]
          else [| sprintf "%s: schema-required [%s] but design-required [%s]"
                    m.Name
                    (actualRequired |> Set.toList |> String.concat ", ")
                    (expected |> Set.toList |> String.concat ", ") |])
      failures
      |> Array.toList
      |> Expect.equal "every registered tool's required-parameter set must match its design" []

    testCase "WHY — send_fsharp_code — optional args (eval_mode, block_start_line, intent, working_directory, file_path) are omitted by well-behaved agents because descriptions say so, so their absence must not throw"
    <| fun _ ->
      let m = registeredToolMethods |> Array.find (fun m -> m.Name = "send_fsharp_code")
      for p in m.GetParameters() do
        if p.Name <> "agentName" && p.Name <> "code" then
          p.HasDefaultValue
          |> Expect.isTrue (sprintf "parameter '%s' should have a default value" p.Name)
  ]

[<Tests>]
let descriptionSnapshotTests =
  testSequenced <| testList "Tool description snapshots" [

    testTask "send_fsharp_code description" {
      let desc =
        toolDescriptions
        |> List.find (fun (name, _) -> name = "send_fsharp_code")
        |> snd
      do! verifyText "send_fsharp_code_description" desc
    }

    testTask "load_fsharp_script description" {
      let desc =
        toolDescriptions
        |> List.find (fun (name, _) -> name = "load_fsharp_script")
        |> snd
      do! verifyText "load_fsharp_script_description" desc
    }

    testTask "get_fsi_status description" {
      let desc =
        toolDescriptions
        |> List.find (fun (name, _) -> name = "get_fsi_status")
        |> snd
      do! verifyText "get_fsi_status_description" desc
    }
  ]

[<Tests>]
let descriptionPropertyTests =
  testList "Tool description properties" [

    testCase "all MCP tools have substantive descriptions (>= 30 chars)"
    <| fun _ ->
      for (name, desc) in toolDescriptions do
        Expect.isTrue
          (sprintf "Tool '%s' description should be >= 30 chars but was %d" name desc.Length)
          (desc.Length >= 30)

    testCase "send_fsharp_code description teaches incremental usage"
    <| fun _ ->
      let desc =
        toolDescriptions
        |> List.find (fun (name, _) -> name = "send_fsharp_code")
        |> snd
      desc
      |> Expect.stringContains
        "Should mention ;; as statement separator"
        ";;"

    testCase "send_fsharp_code description warns about large blocks"
    <| fun _ ->
      let desc =
        toolDescriptions
        |> List.find (fun (name, _) -> name = "send_fsharp_code")
        |> snd
      let mentionsIncremental =
        desc.Contains("incremental", StringComparison.OrdinalIgnoreCase)
        || desc.Contains("small", StringComparison.OrdinalIgnoreCase)
      mentionsIncremental
      |> Expect.isTrue
        "Should teach agents to submit small/incremental blocks"

    testCase "send_fsharp_code description explains error recovery"
    <| fun _ ->
      let desc =
        toolDescriptions
        |> List.find (fun (name, _) -> name = "send_fsharp_code")
        |> snd
      let mentionsRecovery =
        desc.Contains("previous", StringComparison.OrdinalIgnoreCase)
        || desc.Contains("session", StringComparison.OrdinalIgnoreCase)
      mentionsRecovery
      |> Expect.isTrue
        "Should explain that errors don't corrupt session state"

    testCase "get_fsi_status description stays focused on worker readiness"
    <| fun _ ->
      let desc =
        toolDescriptions
        |> List.find (fun (name, _) -> name = "get_fsi_status")
        |> snd
      desc.Contains("get_live_test_status", StringComparison.OrdinalIgnoreCase)
      |> Expect.isFalse "get_fsi_status should not redirect MCP agents into live-testing tooling"

    testCase "targeted_verify description teaches trust-first workflow"
    <| fun _ ->
      let desc =
        toolDescriptions
        |> List.find (fun (name, _) -> name = "targeted_verify")
        |> snd
      desc |> Expect.stringContains "should mention snippet-first trust" "local snippet-first proof"

    testCase "reduced MCP surface keeps the tool count surgical"
    <| fun _ ->
      registeredToolDescriptions.Length |> Expect.equal "tool count should stay intentionally small" 18
  ]
