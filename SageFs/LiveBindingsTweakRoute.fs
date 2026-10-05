/// The route the live-bindings knob posts to. The page stages one row's request in signals and posts them (as every dashboard action
/// does); this reads them, does what the row asked through `LiveBindingsTweakService`, and answers on the same connection: the
/// row's step count goes back to zero, and the page's own stream is told to draw the new state. It writes no markup of its own:
/// the one `#main` morph is the only render path, so the row shows what the server's state says, whatever the click did.
module SageFs.Server.LiveBindingsTweakRoute

open System
open System.Text.Json
open Falco
open Falco.Datastar
open StarFederation.Datastar.FSharp
open Microsoft.AspNetCore.Http
open SageFs
open SageFs.Server.DashboardTypes
open SageFs.Server.LiveBindingsTweakView
open SageFs.Server.LiveBindingsTweakService

/// What the signals body said, or why it was not a request.
[<RequireQualifiedAccess>]
type Asked =
  | Request of sessionId: WorkerProtocol.SessionId * clientId: string * request: TweakRequest
  | NotARequest of reason: string

let askedOf (body: JsonElement) : Asked =
  let text (name: string) : string =
    match body.ValueKind, body.TryGetProperty name with
    | JsonValueKind.Object, (true, property) when property.ValueKind = JsonValueKind.String -> property.GetString()
    | _ -> ""
  let sessionText = text Signals.ViewingSessionId
  match WorkerProtocol.SessionId.validate sessionText with
  | Error why -> Asked.NotARequest(sprintf "the page is not viewing a session: %s" why)
  | Ok sessionId ->
    match
      TweakRequest.parse
        (text TweakSignals.Row)
        (text TweakSignals.File)
        (text TweakSignals.Address)
        (text TweakSignals.Seen)
        (text TweakSignals.SeenText)
        (text TweakSignals.Verb)
        (text TweakSignals.Value)
    with
    | Error fault -> Asked.NotARequest(TweakRequest.describe fault)
    | Ok request -> Asked.Request(sessionId, text Signals.ClientId, request)

/// Tell one page's own stream to draw: the same push the live-bindings subscription uses, so it is serialized with every other write
/// to that stream and the whole-page morph stays the only render path.
let pushTo (infra: DashboardInfra) (clientId: string) : unit =
  match infra.ConnectionChannels.TryGetValue clientId with
  | true, channel ->
    try channel.Post(DashboardStreamCommand.StateChange(SseEvent.ModelChanged(0, 0)))
    with :? ObjectDisposedException -> ()
  | false, _ -> ()

let handler (q: DashboardQueries) (infra: DashboardInfra) (service: Service) : HttpHandler =
  fun ctx ->
    task {
      try
        use! doc = readSignalsJsonSized ctx
        match askedOf doc.RootElement with
        | Asked.NotARequest reason ->
          ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
          do! ctx.Response.WriteAsync reason
        | Asked.Request(sessionId, clientId, request) ->
          let sessionText = WorkerProtocol.SessionId.value sessionId
          let! hotReload = q.GetHotReloadState sessionId
          let! sessions = q.GetAllSessions()
          let reloadNow =
            sessions
            |> List.tryFind (fun s -> s.Id = sessionId)
            |> Option.map (fun s -> s.Reload)
            |> Option.defaultValue SessionReload.NoReloadYet
          let files =
            match hotReload with
            | Some state -> state.files |> List.map (fun f -> f.path, HotReloadWatch.ofFlag f.watched)
            | None -> []
          let owned = Service.ownedOf sessionText (q.GetSessionWorkingDir sessionId) files
          do! Service.act service sessionText owned (fun () -> reloadNow) (fun () -> pushTo infra clientId) request
          Response.sseStartResponse ctx |> ignore
          // The count goes back to zero whatever the door said: a landed write moved the value, and a refused one must not leave
          // the drag handle where the person left it as if it had worked.
          do! Response.ssePatchSignal ctx (SignalPath.sp (TweakSignals.stepsOf request.Row)) 0
      with
      | :? RequestTooLargeException -> ()
      | :? IO.IOException -> ()
      | :? ObjectDisposedException -> ()
    }
    :> Threading.Tasks.Task
