/// The routes of the Watch tab: add an expression, remove one, run one that is held. The page stages nothing but the text in its box (a
/// signal) and, for a row's own buttons, the expression in the query; this reads them, does what was asked through the settings layers
/// (`WatchSettings`) or the session's eval, and answers on the same connection: the box clears, or the note says why nothing changed.
/// It writes no markup of its own: the one `#main` morph is the only render path.
module SageFs.Server.LiveBindingsWatchRoute

open System
open System.Text.Json
open Falco
open Falco.Datastar
open StarFederation.Datastar.FSharp
open Microsoft.AspNetCore.Http
open SageFs
open SageFs.Features.Tweak.WatchRows
open SageFs.Server.DashboardTypes
open SageFs.Server.LiveBindingsWatch
open SageFs.Server.LiveBindingsTweakRoute

[<RequireQualifiedAccess>]
type WatchVerb =
  | Add
  | Remove
  | Run

/// What the request said: who asked, what for, and the text it named; or why it was not a request.
[<RequireQualifiedAccess>]
type WatchAsked =
  | Asked of verb: WatchVerb * sessionId: WorkerProtocol.SessionId * clientId: string * text: string
  | NotAsked of reason: string

/// Add reads the box, which is a signal; Remove and Run read the expression of the row whose button was clicked, which is in the query.
let askedOf (verb: WatchVerb) (query: string) (body: JsonElement) : WatchAsked =
  match WorkerProtocol.SessionId.validate (signalText body Signals.ViewingSessionId) with
  | Error why -> WatchAsked.NotAsked(sprintf "the page is not viewing a session: %s" why)
  | Ok sessionId ->
    let text =
      match verb with
      | WatchVerb.Add -> signalText body WatchSignals.Input
      | WatchVerb.Remove
      | WatchVerb.Run -> query
    WatchAsked.Asked(verb, sessionId, signalText body Signals.ClientId, text)

/// What to do with the saved list when the text is an expression, as a decision: the list to store, or the words for the note.
[<RequireQualifiedAccess>]
type ListChange =
  | Store of WatchExpression list
  | Refuse of words: string

/// The list after an add or a remove. An add of text that is not an expression is refused with the parser's reason; a remove of
/// something that is not on the list changes nothing, and says so.
let changeList (verb: WatchVerb) (text: string) (current: WatchExpression list) : ListChange =
  match WatchExpression.parse text with
  | Error why -> ListChange.Refuse(ExpressionError.describe why)
  | Ok expression ->
    match verb with
    | WatchVerb.Add -> ListChange.Store(WatchSettings.add expression current)
    | WatchVerb.Remove ->
      match List.contains expression current with
      | true -> ListChange.Store(WatchSettings.remove expression current)
      | false -> ListChange.Refuse(sprintf "%s is not on the list" (WatchExpression.text expression))
    | WatchVerb.Run -> ListChange.Refuse "a run does not change the list"

/// Which layer a change is saved in: the app's own, or the global one when the session has no app folder.
let private layerFor (paths: ConfigPaths) : ConfigLayer =
  match paths.Repo with
  | RepoRootAt _ -> LRepo
  | NoRepoCheckout -> LGlobal

/// Save a list in the layer the app uses.
let private save (paths: ConfigPaths) (expressions: WatchExpression list) : Result<unit, ConfigError> =
  SettingsCatalog.edit paths (layerFor paths) (WatchSettings.descriptor.Render (WatchSettings.valueOf expressions)) WatchSettings.descriptor
  |> Result.map ignore

let private say (ctx: HttpContext) (words: string) : Threading.Tasks.Task =
  Response.ssePatchSignal ctx (SignalPath.sp WatchSignals.Note) words

/// Run a held expression in the session as the Evaluate box would, and keep what the session said. The expression has to be on the saved
/// list, so a request cannot make the session run text the list does not hold.
let private runOnList
  (ctx: HttpContext)
  (evalCode: WorkerProtocol.SessionId -> string -> Threading.Tasks.Task<Result<string, string>>)
  (sessionId: WorkerProtocol.SessionId)
  (paths: ConfigPaths)
  (text: string)
  : Threading.Tasks.Task =
  task {
    match WatchExpression.parse text, expressionsAt paths with
    | Error why, _ -> do! say ctx (ExpressionError.describe why)
    | Ok _, Error why -> do! say ctx (sprintf "The saved list could not be read: %s" why)
    | Ok expression, Ok current when not (List.contains expression current) ->
      do! say ctx (sprintf "%s is not on the list" (WatchExpression.text expression))
    | Ok expression, Ok _ ->
      let! answer = evalCode sessionId (sprintf "%s;;" (WatchExpression.text expression))
      let outcome =
        match answer with
        | Ok shown -> RunOutcome.RanOk(shown.Trim())
        | Error reason -> RunOutcome.RanFailed reason
      recordRun (WorkerProtocol.SessionId.value sessionId) expression outcome
      do! say ctx ""
  }
  :> Threading.Tasks.Task

let handler
  (verb: WatchVerb)
  (q: DashboardQueries)
  (infra: DashboardInfra)
  (evalCode: WorkerProtocol.SessionId -> string -> Threading.Tasks.Task<Result<string, string>>)
  : HttpHandler =
  fun ctx ->
    task {
      try
        use! doc = readSignalsJsonSized ctx
        let query = ctx.Request.Query.["e"].ToString()
        match askedOf verb query doc.RootElement with
        | WatchAsked.NotAsked reason ->
          ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
          do! ctx.Response.WriteAsync reason
        | WatchAsked.Asked(verb, sessionId, clientId, text) ->
          let paths = pathsFor (q.GetSessionWorkingDir sessionId)
          Response.sseStartResponse ctx |> ignore
          match verb with
          | WatchVerb.Run -> do! runOnList ctx evalCode sessionId paths text
          | WatchVerb.Add
          | WatchVerb.Remove ->
            match expressionsAt paths with
            | Error why -> do! say ctx (sprintf "The saved list could not be read: %s" why)
            | Ok current ->
              match changeList verb text current with
              | ListChange.Refuse words -> do! say ctx words
              | ListChange.Store next ->
                match save paths next with
                | Error why -> do! say ctx (ConfigError.describe why)
                | Ok () ->
                  do! say ctx ""
                  match verb with
                  | WatchVerb.Add -> do! Response.ssePatchSignal ctx (SignalPath.sp WatchSignals.Input) ""
                  | WatchVerb.Remove
                  | WatchVerb.Run -> ()
          pushTo infra clientId
      with
      | :? RequestTooLargeException -> ()
      | :? IO.IOException -> ()
      | :? ObjectDisposedException -> ()
    }
    :> Threading.Tasks.Task
