/// The route behind a row's picker: a press posts the row and the format it asks for, the server records the choice for the page that
/// pressed and tells that page's own stream to draw, so the one `#main` morph is the only render path. A token that is not a format
/// is refused with the set it should have been.
module SageFs.Server.LiveBindingsRowFormatRoute

open System
open System.Text.Json
open Falco
open Falco.Datastar
open StarFederation.Datastar.FSharp
open Microsoft.AspNetCore.Http
open SageFs
open SageFs.Features.Tweak.RowFormatting
open SageFs.Server.DashboardTypes
open SageFs.Server.LiveBindingsTweakRoute

/// What the request said: who pressed, which row, and the format it asked for; or why it was not a request.
[<RequireQualifiedAccess>]
type FormatAsked =
  | Asked of clientId: string * row: string * choice: FormatChoice
  | NotAsked of reason: string

/// The page's id is a signal; the row and the format are in the query.
let askedOf (rowText: string) (formatToken: string) (body: JsonElement) : FormatAsked =
  match signalText body Signals.ClientId, rowText with
  | "", _ -> FormatAsked.NotAsked "the page has no client id, so there is no page to keep the choice for"
  | _, "" -> FormatAsked.NotAsked "the request names no row"
  | clientId, row ->
    match FormatChoice.ofToken formatToken with
    | Ok choice -> FormatAsked.Asked(clientId, row, choice)
    | Error why -> FormatAsked.NotAsked(ConfigError.describe why)

let handler (infra: DashboardInfra) : HttpHandler =
  fun ctx ->
    task {
      try
        use! doc = readSignalsJsonSized ctx
        match askedOf (ctx.Request.Query.["r"].ToString()) (ctx.Request.Query.["f"].ToString()) doc.RootElement with
        | FormatAsked.NotAsked reason ->
          ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
          do! ctx.Response.WriteAsync(sprintf "The format was not changed: %s" reason)
        | FormatAsked.Asked(clientId, row, choice) ->
          LiveBindingsRowFormat.choose clientId row choice
          Response.sseStartResponse ctx |> ignore
          pushTo infra clientId
      with
      | :? RequestTooLargeException -> ()
      | :? IO.IOException -> ()
      | :? ObjectDisposedException -> ()
    }
    :> Threading.Tasks.Task
