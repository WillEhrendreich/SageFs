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

let snapshotsDir = Path.Combine(__SOURCE_DIRECTORY__, "snapshots")

let verifyText name (value: string) =
  let settings = VerifySettings()
  settings.UseDirectory(snapshotsDir)
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
  ]
