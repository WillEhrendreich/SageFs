/// The new-session dialog, wired to the page: the per-page state, the three POST routes, and the one function
/// that puts the dialog into every snapshot the page renders.
///
/// The dialog stays inside the dashboard's one render path. Its state is kept per page (by the page's client
/// id, like the "Resume Previous" sort), every route moves it with `NewSessionDialog.step`, and then asks THAT
/// page's stream for a push. The stream renders the whole page, dialog and all, through the one morph. A route
/// never draws markup of its own: it changes the state and, where the browser has to move (reopen after a
/// refusal, view the new session), patches a signal.
module SageFs.Server.DashboardNewSession

open System
open System.IO
open System.Text.Json
open Falco
open Falco.Datastar
open StarFederation.Datastar.FSharp
open Microsoft.AspNetCore.Http
open SageFs
open SageFs.Server.DashboardTypes

/// How many pages' dialogs are held before the oldest is evicted (see `PageChoices`).
[<Literal>]
let dialogPageCapacity = 4096

/// Each page's dialog. Never cleared when a stream closes: Datastar reconnects to the same URL, and the new
/// connection needs what the old one had.
let dialogs = PageChoices<NewSessionDialog.NewSessionDialog>(dialogPageCapacity)

let stateOf (clientId: string) : NewSessionDialog.NewSessionDialog =
  dialogs.Find(clientId, NewSessionDialog.NewSessionDialog.Closed)

/// Put this page's dialog into the snapshot: the Sessions panel's slot holds the starting card, the list and the
/// dialog. Applied wherever a snapshot is built for a page, so the GET render, the stream and an action's own
/// morph can never disagree about it.
let apply (clientId: string) (snap: DashboardSnapshot) : DashboardSnapshot =
  { snap with SessionsPanel = NewSessionDialogView.renderRegion (stateOf clientId) snap.SessionsPanel }

let postToStream (infra: DashboardInfra) (clientId: string) (command: DashboardStreamCommand) : unit =
  match infra.ConnectionChannels.TryGetValue clientId with
  | true, channel ->
    try channel.Post command
    with :? ObjectDisposedException -> ()
  | false, _ -> ()

/// Move this page's dialog and ask its stream to draw the result.
let transition (infra: DashboardInfra) (clientId: string) (event: NewSessionDialog.Event) : NewSessionDialog.NewSessionDialog =
  let next = dialogs.Update(clientId, NewSessionDialog.NewSessionDialog.Closed, fun state -> NewSessionDialog.NewSessionDialog.step state event)
  postToStream infra clientId (DashboardStreamCommand.StateChange SseEvent.SessionProgress)
  next

// ── Reading what a click sent ────────────────────────────────────────────

let readString (doc: JsonDocument) (name: string) : string =
  match doc.RootElement.TryGetProperty name with
  | true, property when property.ValueKind = JsonValueKind.String -> property.GetString()
  | _ -> ""

let readStrings (doc: JsonDocument) (name: string) : string list =
  match doc.RootElement.TryGetProperty name with
  | true, property when property.ValueKind = JsonValueKind.Array ->
    [ for item in property.EnumerateArray() do
        match item.ValueKind with
        | JsonValueKind.String -> yield item.GetString()
        | _ -> () ]
  | _ -> []

// ── Routes ───────────────────────────────────────────────────────────────

/// Look in a directory: the dialog goes to Discovering at once, then to what was found. The ticks and the target
/// follow the discovery, so the person starts from a sensible choice they can change.
let discover (q: DashboardQueries) (infra: DashboardInfra) (clientId: string) (ctx: HttpContext) (typed: string) = task {
  let directory = typed.Trim()
  transition infra clientId (NewSessionDialog.Event.Open directory) |> ignore
  let! sessions = q.GetAllSessions ()
  let outcome = NewSessionDiscovery.discover (NewSessionDiscovery.liveSessionsOf sessions) directory
  let starting (found: NewSessionDialog.Found) = task {
    let kind, picked = NewSessionDialog.DefaultChoice.ofFound found
    // One patch with the ticks as a real array: patching a single signal with a list reached the page as text.
    let signals = Collections.Generic.Dictionary<string, obj>()
    signals.[NewSessionDialog.NewSessionNames.TargetSignal] <- box (NewSessionDialog.TargetKind.key kind)
    signals.[NewSessionDialog.NewSessionNames.ProjectsSignal] <- box (List.toArray (NewSessionDialog.DefaultChoice.aligned found picked))
    do! Response.ssePatchSignals ctx signals
  }
  // The choices are set BEFORE the list is drawn, so the list never shows with the previous directory's ticks.
  match outcome with
  | Ok (found, _) -> do! starting found
  | Error NewSessionDialog.Refusal.NoDirectory -> do! starting (NewSessionDialog.Found.nothing directory)
  | Error (NewSessionDialog.Refusal.DirectoryMissing _)
  | Error NewSessionDialog.Refusal.NothingPicked
  | Error (NewSessionDialog.Refusal.Daemon _) -> ()
  transition infra clientId (NewSessionDiscovery.eventOf directory outcome) |> ignore
}

let discoverHandler (q: DashboardQueries) (infra: DashboardInfra) : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
      Response.sseStartResponse ctx |> ignore
      do! discover q infra (readString doc Signals.ClientId) ctx (readString doc Signals.NewSessionDir)
    with
    | :? RequestTooLargeException -> ()
    | :? IOException -> ()
    | :? ObjectDisposedException -> ()
  }

let closeHandler (infra: DashboardInfra) : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
      Response.sseStartResponse ctx |> ignore
      transition infra (readString doc Signals.ClientId) NewSessionDialog.Event.Dismiss |> ignore
    with
    | :? RequestTooLargeException -> ()
    | :? IOException -> ()
    | :? ObjectDisposedException -> ()
  }

/// Create what the dialog asked for. The dialog is already closed in the browser (the click closed it), so the
/// state moves to Creating and the page's stream draws the starting card in the Sessions list at once. On
/// success the page views the new session and the state closes; on a refusal the state becomes Refused and the
/// dialog is reopened by patching its open signal, with the reason in it.
let createHandler (q: DashboardQueries) (infra: DashboardInfra) (actions: DashboardActions) : HttpHandler =
  fun ctx -> task {
    try
      use! doc = readSignalsJsonSized ctx
      let clientId = readString doc Signals.ClientId
      let directory = (readString doc Signals.NewSessionDir).Trim()
      let kind =
        NewSessionDialog.TargetKind.tryOfKey (readString doc NewSessionDialog.NewSessionNames.TargetSignal)
        |> Option.defaultValue NewSessionDialog.TargetKind.LoadProjects
      let picked = NewSessionDialog.DefaultChoice.ticked (readStrings doc NewSessionDialog.NewSessionNames.ProjectsSignal)
      let workflowKey = readString doc NewSessionDialog.NewSessionNames.WorkflowSignal
      Response.sseStartResponse ctx |> ignore
      let reject (refusal: NewSessionDialog.Refusal) = task {
        transition infra clientId (NewSessionDialog.Event.Rejected(directory, refusal)) |> ignore
        do! Response.ssePatchSignal ctx (SignalPath.sp Signals.NewSessionOpen) true
      }
      match NewSessionDialog.Request.parse directory kind picked workflowKey with
      | Error refusal -> do! reject refusal
      | Ok request ->
        match Directory.Exists request.Directory, NewSessionDiscovery.resolveTargets request with
        | false, _ -> do! reject (NewSessionDialog.Refusal.DirectoryMissing request.Directory)
        | true, Error refusal -> do! reject refusal
        | true, Ok targets ->
          transition infra clientId (NewSessionDialog.Event.Submit request) |> ignore
          let! created = actions.CreateSession targets request.Directory request.Workflow
          match created with
          | Ok sessionId ->
            let! _ = actions.SwitchSession sessionId
            postToStream infra clientId (DashboardStreamCommand.RetargetView (Some sessionId))
            do! Response.ssePatchSignal ctx (SignalPath.sp Signals.ViewingSessionId) (WorkerProtocol.SessionId.value sessionId)
            transition infra clientId NewSessionDialog.Event.Created |> ignore
          | Error error ->
            transition infra clientId (NewSessionDialog.Event.Failed (NewSessionDialog.Refusal.Daemon error)) |> ignore
            do! Response.ssePatchSignal ctx (SignalPath.sp Signals.NewSessionOpen) true
    with
    | :? RequestTooLargeException -> ()
    | :? IOException -> ()
    | :? ObjectDisposedException -> ()
  }

let routes (q: DashboardQueries) (infra: DashboardInfra) (actions: DashboardActions) : Falco.HttpEndpoint list =
  [ Falco.Routing.post NewSessionDialog.NewSessionNames.DiscoverRoute (discoverHandler q infra)
    Falco.Routing.post NewSessionDialog.NewSessionNames.CreateRoute (createHandler q infra actions)
    Falco.Routing.post NewSessionDialog.NewSessionNames.CloseRoute (closeHandler infra) ]
