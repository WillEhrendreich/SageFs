/// What a person sees and has to do when a confirm-tier tool (`stop_session`, `hard_reset_fsi_session`) asks. The
/// prompt used to carry one boolean field, `confirmed`, marked REQUIRED and not ticked. A form with a required field
/// cannot be submitted until the field is set, so pressing Accept did nothing until the person found Space to tick
/// the box first; and the server never read the box at all, only whether Accept was pressed. A person who "could
/// not confirm" was looking at a checkbox that changed nothing. These cases drive the real `confirmWithin` with a
/// fake client, and pin what the prompt asks for and what each answer means.
module SageFs.Tests.ConfirmationPromptTests

open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Protocol
open SageFs.Server.McpServer

let private canAsk = ClientCapabilities(Elicitation = ElicitationCapability())

/// A client that records what it was asked and answers with `answer`.
let private recording (answer: ElicitResult) : (ElicitRequestParams -> CancellationToken -> Task<ElicitResult>) * (unit -> ElicitRequestParams option) =
  let mutable asked : ElicitRequestParams option = None
  (fun parameters _ ->
    asked <- Some parameters
    Task.FromResult answer),
  (fun () -> asked)

let private accept = ElicitResult(Action = "accept")

let private acceptWith (confirmed: bool) : ElicitResult =
  let value = JsonDocument.Parse(if confirmed then "true" else "false").RootElement
  ElicitResult(Action = "accept", Content = Dictionary<string, JsonElement>(dict [ "confirmed", value ]))

[<Tests>]
let tests =
  testList "The confirmation prompt" [

    testTask "WHY - pressing Accept is enough, because the box is not required and starts ticked, so a form that cannot take an Accept never stands between a person and the call" {
      let ask, asked = recording accept
      let! _ = confirmWithin TestTimeouts.confirmationBudget canAsk ask "stop_session" CancellationToken.None
      match asked () with
      | None -> failtest "the client was never asked"
      | Some parameters ->
        let required = match parameters.RequestedSchema.Required with | null -> [] | names -> List.ofSeq names
        required |> Expect.isEmpty "no field has to be set before Accept works"
        match parameters.RequestedSchema.Properties.["confirmed"] with
        | :? ElicitRequestParams.BooleanSchema as flag -> flag.Default |> Expect.equal "the box starts ticked" (System.Nullable true)
        | other -> failtestf "the confirmed field is a boolean, got %A" other
    }

    testTask "WHY - Accept with the box left alone runs the call, because Accept is the answer" {
      let ask, _ = recording accept
      let! outcome = confirmWithin TestTimeouts.confirmationBudget canAsk ask "stop_session" CancellationToken.None
      outcome |> Expect.equal "accepted" (Ok())
    }

    testTask "WHY - Accept with the box ticked runs the call" {
      let ask, _ = recording (acceptWith true)
      let! outcome = confirmWithin TestTimeouts.confirmationBudget canAsk ask "stop_session" CancellationToken.None
      outcome |> Expect.equal "accepted" (Ok())
    }

    testTask "WHY - Accept with the box deliberately unticked does NOT run the call, because a person who removed the tick did not say yes" {
      let ask, _ = recording (acceptWith false)
      let! outcome = confirmWithin TestTimeouts.confirmationBudget canAsk ask "stop_session" CancellationToken.None
      outcome |> Expect.equal "refused as a decline, and nothing is done" (Result.Error Declined)
    }
  ]
