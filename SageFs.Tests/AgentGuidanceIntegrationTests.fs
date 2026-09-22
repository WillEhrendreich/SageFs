module SageFs.Tests.AgentGuidanceIntegrationTests

/// The agent guidance over the real wire: a spawned daemon, a real MCP client.
/// AgentGuidanceTests pins the text. This checks the server actually hands
/// it out, the instructions on connect and both prompts on prompts/list and
/// prompts/get.

open System.Threading
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open SageFs.Server.AgentGuidance
open SageFs.Tests.CohortMcpToolsIntegrationTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private promptText (result: GetPromptResult) =
  result.Messages
  |> Seq.choose (fun m ->
    match m.Content with
    | :? TextContentBlock as t -> Some t.Text
    | _ -> None)
  |> String.concat ""

[<Tests>]
let agentGuidanceIntegrationTests =
  Integration.hostList "MCP agent guidance" [

    testTask "WHY — a connecting client gets the loop instructions and can list and fetch back_to_the_repl" {
      do! withDaemon (fun port -> task {
        use! client = connect port

        client.ServerInstructions
        |> Expect.equal "the server should send AgentGuidance.serverInstructions on connect" serverInstructions

        let! prompts = client.ListPromptsAsync((null: ModelContextProtocol.RequestOptions), CancellationToken.None)
        let names = prompts |> Seq.map (fun p -> p.Name) |> Set.ofSeq
        [ BackToTheReplPromptName; SageFsLoopPromptName ]
        |> List.filter (fun n -> not (names.Contains n))
        |> Expect.isEmpty "prompts/list should carry both prompts"

        let! back = client.GetPromptAsync(BackToTheReplPromptName, null, null, CancellationToken.None)
        promptText back
        |> Expect.equal "prompts/get should return the back_to_the_repl text" backToTheRepl
      })
    }
  ]
