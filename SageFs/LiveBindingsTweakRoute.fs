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

/// One signal's text, or nothing when the body has no such string.
let signalText (body: JsonElement) (name: string) : string =
  match body.ValueKind, body.TryGetProperty name with
  | JsonValueKind.Object, (true, property) when property.ValueKind = JsonValueKind.String -> property.GetString()
  | _ -> ""

let askedOf (body: JsonElement) : Asked =
  let text (name: string) : string = signalText body name
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

/// What an apply request said: the session, the page that asked, and the statement to evaluate, or why it was not a request.
[<RequireQualifiedAccess>]
type ApplyAsked =
  | Apply of sessionId: WorkerProtocol.SessionId * clientId: string * code: string
  | NotAnApply of reason: string

/// The row the button staged, read the way the knob's route reads it: the same signals, the file's text as the row last drew it.
let applyAskedOf (body: JsonElement) : ApplyAsked =
  let text (name: string) : string = signalText body name
  match WorkerProtocol.SessionId.validate (text Signals.ViewingSessionId), TweakRequest.rowOfText (text TweakSignals.Row) with
  | Error why, _ -> ApplyAsked.NotAnApply(sprintf "the page is not viewing a session: %s" why)
  | Ok _, Error fault -> ApplyAsked.NotAnApply(TweakRequest.describe fault)
  | Ok sessionId, Ok key ->
    match ApplyToSession.codeFor key (text TweakSignals.SeenText) with
    | Error refusal -> ApplyAsked.NotAnApply(ApplyRefusal.text refusal)
    | Ok code -> ApplyAsked.Apply(sessionId, text Signals.ClientId, code)

/// What a range-and-step request said: the session, the page that asked, the row, and the three texts, or why it was not a request.
[<RequireQualifiedAccess>]
type LimitsAsked =
  | Limits of sessionId: WorkerProtocol.SessionId * clientId: string * row: SageFs.Features.Tweak.BindingTweak.RowKey * texts: SageFs.Features.Tweak.LimitsTexts
  | NotLimits of reason: string

let limitsAskedOf (body: JsonElement) : LimitsAsked =
  let text (name: string) : string = signalText body name
  match WorkerProtocol.SessionId.validate (text Signals.ViewingSessionId), TweakRequest.rowOfText (text TweakSignals.Row) with
  | Error why, _ -> LimitsAsked.NotLimits(sprintf "the page is not viewing a session: %s" why)
  | Ok _, Error fault -> LimitsAsked.NotLimits(TweakRequest.describe fault)
  | Ok sessionId, Ok key ->
    LimitsAsked.Limits(
      sessionId,
      text Signals.ClientId,
      key,
      { LowestText = text TweakSignals.LimLow; HighestText = text TweakSignals.LimHigh; StepText = text TweakSignals.LimStep }
    )

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

/// Apply a row's file value to the session: evaluate `let name = <the file's expression>;;` the way the Evaluate box would. The eval's
/// own answer, a value or an error, is written to the Output pane by `evalCode` itself, so this has no second place to report it; the
/// page's stream is told to draw either way, so the row and the live values show what the session holds now.
let applyHandler
  (infra: DashboardInfra)
  (evalCode: WorkerProtocol.SessionId -> string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx ->
    task {
      try
        use! doc = readSignalsJsonSized ctx
        match applyAskedOf doc.RootElement with
        | ApplyAsked.NotAnApply reason ->
          ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
          do! ctx.Response.WriteAsync reason
        | ApplyAsked.Apply(sessionId, clientId, code) ->
          Response.sseStartResponse ctx |> ignore
          let! answer = evalCode sessionId code
          match answer with
          | Ok _
          | Error _ -> pushTo infra clientId
      with
      | :? RequestTooLargeException -> ()
      | :? IO.IOException -> ()
      | :? ObjectDisposedException -> ()
    }
    :> Threading.Tasks.Task

/// What a push request said: the row, the expression it showed, how the value gets into the session, whether this is a try or a
/// revert, and the typed text.
type PushRequest =
  { Row: SageFs.Features.Tweak.BindingTweak.RowKey
    File: string
    Address: string
    Seen: string
    SeenText: string
    Via: SageFs.Features.Tweak.BindingTweak.PushVia
    Verb: PushVerb
    Typed: string }

[<RequireQualifiedAccess>]
type PushAsked =
  | Push of sessionId: WorkerProtocol.SessionId * clientId: string * request: PushRequest
  | NotAPush of reason: string

let pushAskedOf (body: JsonElement) : PushAsked =
  let text (name: string) : string = signalText body name
  match WorkerProtocol.SessionId.validate (text Signals.ViewingSessionId), TweakRequest.rowOfText (text TweakSignals.Row) with
  | Error why, _ -> PushAsked.NotAPush(sprintf "the page is not viewing a session: %s" why)
  | Ok _, Error fault -> PushAsked.NotAPush(TweakRequest.describe fault)
  | Ok sessionId, Ok row ->
    match PushVerb.ofToken (text TweakSignals.Verb), SageFs.Features.Tweak.BindingTweak.PushVia.ofToken (text TweakSignals.Via) with
    | Error why, _
    | _, Error why -> PushAsked.NotAPush(ConfigError.describe why)
    | Ok verb, Ok via ->
      PushAsked.Push(
        sessionId,
        text Signals.ClientId,
        { Row = row
          File = text TweakSignals.File
          Address = text TweakSignals.Address
          Seen = text TweakSignals.Seen
          SeenText = text TweakSignals.SeenText
          Via = via
          Verb = verb
          Typed = text TweakSignals.Value }
      )

/// Try a typed value in the session, or put the file's value back. Nothing is written to the file; the session's own answer goes to
/// the Output pane, and the page's stream is told to draw either way.
let pushHandler
  (infra: DashboardInfra)
  (service: Service)
  (evalCode: WorkerProtocol.SessionId -> string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx ->
    task {
      try
        use! doc = readSignalsJsonSized ctx
        match pushAskedOf doc.RootElement with
        | PushAsked.NotAPush reason ->
          ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
          do! ctx.Response.WriteAsync reason
        | PushAsked.Push(sessionId, clientId, request) ->
          Response.sseStartResponse ctx |> ignore
          let attempted = Service.attemptedFrom service request.Row request.File request.Address request.Seen request.SeenText
          do!
            Service.push
              service
              (WorkerProtocol.SessionId.value sessionId)
              (evalCode sessionId)
              (fun () -> pushTo infra clientId)
              request.Row
              attempted
              request.Via
              request.Verb
              request.Typed
      with
      | :? RequestTooLargeException -> ()
      | :? IO.IOException -> ()
      | :? ObjectDisposedException -> ()
    }
    :> Threading.Tasks.Task

/// What a batch said: the session, the page that asked, and the requests, or why it was not a batch.
[<RequireQualifiedAccess>]
type BatchAsked =
  | Batch of sessionId: WorkerProtocol.SessionId * clientId: string * items: SageFs.Features.Tweak.ChangesStrip.BatchItem list
  | NotABatch of reason: string

let batchAskedOf (body: JsonElement) : BatchAsked =
  let text (name: string) : string = signalText body name
  match WorkerProtocol.SessionId.validate (text Signals.ViewingSessionId) with
  | Error why -> BatchAsked.NotABatch(sprintf "the page is not viewing a session: %s" why)
  | Ok sessionId ->
    match Json.deserialize<SageFs.Features.Tweak.ChangesStrip.BatchItem list> Json.camelCase (text TweakSignals.Batch) with
    | Error fault -> BatchAsked.NotABatch(sprintf "the batch is not a list of requests: %s" (JsonError.describe fault))
    | Ok items -> BatchAsked.Batch(sessionId, text Signals.ClientId, items)

/// Save every tweak, or put the file's value back for every tweak, one request after another. Each is the same act a row's own button
/// does, through the same door or the same evaluation, so the strip has no way to reach a file or a session that a row does not.
let batchHandler
  (q: DashboardQueries)
  (infra: DashboardInfra)
  (service: Service)
  (evalCode: WorkerProtocol.SessionId -> string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx ->
    task {
      try
        use! doc = readSignalsJsonSized ctx
        match batchAskedOf doc.RootElement with
        | BatchAsked.NotABatch reason ->
          ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
          do! ctx.Response.WriteAsync reason
        | BatchAsked.Batch(sessionId, clientId, items) ->
          Response.sseStartResponse ctx |> ignore
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
          let changed () = pushTo infra clientId
          // The saves go through the door one after another; the reverts are one evaluation in the session (`Service.revertMany`).
          let mutable reverts = []
          for item in items do
            match SageFs.Features.Tweak.ChangesStrip.BatchKind.ofToken item.Kind, TweakRequest.rowOfText item.Row with
            | Error _, _
            | _, Error _ -> ()
            | Ok SageFs.Features.Tweak.ChangesStrip.BatchKind.Save, Ok _ ->
              match TweakRequest.parse item.Row item.File item.Address item.Seen item.SeenText TweakVerb.LiteralToken item.Value with
              | Ok request -> do! Service.act service sessionText owned (fun () -> reloadNow) changed request
              | Error _ -> ()
            | Ok SageFs.Features.Tweak.ChangesStrip.BatchKind.Revert, Ok row ->
              match SageFs.Features.Tweak.BindingTweak.PushVia.ofToken item.Via with
              | Ok via -> reverts <- reverts @ [ row, Service.attemptedFrom service row item.File item.Address item.Seen item.SeenText, via ]
              | Error _ -> ()
          match reverts with
          | [] -> ()
          | _ -> do! Service.revertMany service sessionText (evalCode sessionId) changed reverts
      with
      | :? RequestTooLargeException -> ()
      | :? IO.IOException -> ()
      | :? ObjectDisposedException -> ()
    }
    :> Threading.Tasks.Task

/// Keep a number's range and step. The choice is written beside the project; the page's stream is told to draw (the slider's ends
/// move), and the row's own note says why when the texts could not be used.
let limitsHandler (q: DashboardQueries) (infra: DashboardInfra) (service: Service) : HttpHandler =
  fun ctx ->
    task {
      try
        use! doc = readSignalsJsonSized ctx
        match limitsAskedOf doc.RootElement with
        | LimitsAsked.NotLimits reason ->
          ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
          do! ctx.Response.WriteAsync reason
        | LimitsAsked.Limits(sessionId, clientId, row, texts) ->
          let note = Service.setLimits service (q.GetSessionWorkingDir sessionId) row texts
          pushTo infra clientId
          Response.sseStartResponse ctx |> ignore
          do! Response.ssePatchSignal ctx (SignalPath.sp (TweakSignals.limNoteOf row)) note
      with
      | :? RequestTooLargeException -> ()
      | :? IO.IOException -> ()
      | :? ObjectDisposedException -> ()
    }
    :> Threading.Tasks.Task
