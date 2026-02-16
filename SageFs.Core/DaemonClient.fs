namespace SageFs

open System
open System.Net.Http
open System.Text.Json
open System.Threading

/// State received from the daemon's /api/state SSE stream.
type DaemonRegionData = {
  Id: string
  Content: string
  Cursor: RegionCursor option
}

module DaemonRegionData =
  let toRenderRegion (d: DaemonRegionData) : RenderRegion =
    { Id = d.Id
      Flags = RegionFlags.None
      Content = d.Content
      Affordances = []
      Cursor = d.Cursor }

/// Shared daemon client logic for both TUI and GUI.
module DaemonClient =

  /// Parse the JSON payload from the /api/state SSE stream.
  let parseStateEvent (json: string) : (string * int * DaemonRegionData list) option =
    try
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      let sessionState = root.GetProperty("sessionState").GetString()
      let evalCount = root.GetProperty("evalCount").GetInt32()
      let regions =
        root.GetProperty("regions").EnumerateArray()
        |> Seq.map (fun el ->
          let id = el.GetProperty("id").GetString()
          let content = el.GetProperty("content").GetString()
          let cursor =
            match el.TryGetProperty("cursor") with
            | true, cursorEl when cursorEl.ValueKind <> JsonValueKind.Null ->
              Some { Line = cursorEl.GetProperty("line").GetInt32()
                     Col = cursorEl.GetProperty("col").GetInt32() }
            | _ -> None
          { Id = id; Content = content; Cursor = cursor })
        |> Seq.toList
      Some (sessionState, evalCount, regions)
    with _ -> None

  /// Map an EditorAction to a (name, value) pair for the dispatch API.
  let actionToApi (action: EditorAction) : (string * string option) option =
    match action with
    | EditorAction.InsertChar c -> Some ("insertChar", Some (string c))
    | EditorAction.NewLine -> Some ("newLine", None)
    | EditorAction.Submit -> Some ("submit", None)
    | EditorAction.Cancel -> Some ("cancel", None)
    | EditorAction.DeleteBackward -> Some ("deleteBackward", None)
    | EditorAction.DeleteForward -> Some ("deleteForward", None)
    | EditorAction.DeleteWord -> Some ("deleteWord", None)
    | EditorAction.MoveCursor Direction.Up -> Some ("moveUp", None)
    | EditorAction.MoveCursor Direction.Down -> Some ("moveDown", None)
    | EditorAction.MoveCursor Direction.Left -> Some ("moveLeft", None)
    | EditorAction.MoveCursor Direction.Right -> Some ("moveRight", None)
    | EditorAction.MoveWordForward -> Some ("moveWordForward", None)
    | EditorAction.MoveWordBackward -> Some ("moveWordBackward", None)
    | EditorAction.MoveToLineStart -> Some ("moveToLineStart", None)
    | EditorAction.MoveToLineEnd -> Some ("moveToLineEnd", None)
    | EditorAction.Undo -> Some ("undo", None)
    | EditorAction.SelectAll -> Some ("selectAll", None)
    | EditorAction.SelectWord -> Some ("selectWord", None)
    | EditorAction.TriggerCompletion -> Some ("triggerCompletion", None)
    | EditorAction.AcceptCompletion -> Some ("acceptCompletion", None)
    | EditorAction.DismissCompletion -> Some ("dismissCompletion", None)
    | EditorAction.NextCompletion -> Some ("nextCompletion", None)
    | EditorAction.PreviousCompletion -> Some ("previousCompletion", None)
    | EditorAction.HistoryPrevious -> Some ("historyPrevious", None)
    | EditorAction.HistoryNext -> Some ("historyNext", None)
    | EditorAction.DeleteToEndOfLine -> Some ("deleteToEndOfLine", None)
    | EditorAction.Redo -> Some ("redo", None)
    | EditorAction.ToggleSessionPanel -> Some ("toggleSessionPanel", None)
    | EditorAction.ListSessions -> Some ("listSessions", None)
    | EditorAction.SwitchSession id -> Some ("switchSession", Some id)
    | EditorAction.CreateSession projects ->
      Some ("createSession", Some (String.concat "," projects))
    | EditorAction.StopSession id -> Some ("stopSession", Some id)
    | EditorAction.HistorySearch s -> Some ("historySearch", Some s)
    | EditorAction.SwitchMode _ -> None

  /// Send an EditorAction to the daemon via POST /api/dispatch.
  let dispatchAction (client: HttpClient) (baseUrl: string) (actionName: string) (value: string option) = task {
    let payload =
      match value with
      | Some v ->
        JsonSerializer.Serialize({| action = actionName; value = v |})
      | None ->
        JsonSerializer.Serialize({| action = actionName |})
    let content = new StringContent(payload, Text.Encoding.UTF8, "application/json")
    try
      let! resp = client.PostAsync(sprintf "%s/api/dispatch" baseUrl, content)
      resp.EnsureSuccessStatusCode() |> ignore
    with _ -> ()
  }

  /// Dispatch an EditorAction to the daemon (convenience wrapper).
  let dispatch (client: HttpClient) (baseUrl: string) (action: EditorAction) = task {
    match actionToApi action with
    | Some (name, value) -> do! dispatchAction client baseUrl name value
    | None -> ()
  }

  /// Verify daemon is reachable. Returns Ok baseUrl or Error message.
  let verifyConnection (daemonInfo: DaemonInfo) = task {
    let dashboardPort = daemonInfo.Port + 1
    let baseUrl = sprintf "http://localhost:%d" dashboardPort
    use client = new HttpClient()
    try
      let! resp = client.GetAsync(sprintf "%s/dashboard" baseUrl)
      resp.EnsureSuccessStatusCode() |> ignore
      return Ok baseUrl
    with ex ->
      return Error (sprintf "Cannot connect to SageFs daemon at %s\n  %s" baseUrl ex.Message)
  }

  /// Callback invoked when new state arrives from the SSE stream.
  type StateCallback = string -> int -> RenderRegion list -> unit

  /// Run SSE listener with auto-reconnect. Calls onState for each update.
  /// Calls onReconnecting when connection drops. Blocks until cancelled.
  let runSseListener
    (baseUrl: string)
    (onState: StateCallback)
    (onReconnecting: string -> unit)
    (ct: CancellationToken) = task {
    let mutable retryDelay = 1000
    let maxRetryDelay = 30000
    while not ct.IsCancellationRequested do
      try
        use sseClient = new HttpClient()
        sseClient.Timeout <- TimeSpan.FromHours(24.0)
        let! stream = sseClient.GetStreamAsync(sprintf "%s/api/state" baseUrl, ct)
        use reader = new IO.StreamReader(stream)
        retryDelay <- 1000
        while not ct.IsCancellationRequested do
          let! line = reader.ReadLineAsync(ct)
          if isNull line then
            raise (IO.IOException("SSE stream ended"))
          if line.StartsWith("data: ") then
            let json = line.Substring(6)
            match parseStateEvent json with
            | Some (sessionState, evalCount, regionData) ->
              let regions = regionData |> List.map DaemonRegionData.toRenderRegion
              onState sessionState evalCount regions
            | None -> ()
      with
      | :? OperationCanceledException -> ()
      | _ when not ct.IsCancellationRequested ->
        onReconnecting "reconnecting..."
        try
          do! Threading.Tasks.Task.Delay(retryDelay, ct)
        with :? OperationCanceledException -> ()
        retryDelay <- min (retryDelay * 2) maxRetryDelay
  }
