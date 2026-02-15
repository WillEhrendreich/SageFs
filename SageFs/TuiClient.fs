module SageFs.Server.TuiClient

open System
open System.Net.Http
open System.Text.Json
open System.Threading
open SageFs

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

/// Parse the JSON payload from the /api/state SSE stream.
let private parseStateEvent (json: string) : (string * int * DaemonRegionData list) option =
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

/// Send an EditorAction to the daemon via POST /api/dispatch.
let private dispatchAction (client: HttpClient) (baseUrl: string) (actionName: string) (value: string option) = task {
  let payload =
    match value with
    | Some v -> sprintf """{"action":"%s","value":"%s"}""" actionName v
    | None -> sprintf """{"action":"%s"}""" actionName
  let content = new StringContent(payload, Text.Encoding.UTF8, "application/json")
  try
    let! resp = client.PostAsync(sprintf "%s/api/dispatch" baseUrl, content)
    resp.EnsureSuccessStatusCode() |> ignore
  with _ -> ()
}

/// Map an EditorAction to a (name, value) pair for the API.
let private actionToApi (action: EditorAction) : (string * string option) option =
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
  | EditorAction.TriggerCompletion -> Some ("triggerCompletion", None)
  | EditorAction.DismissCompletion -> Some ("dismissCompletion", None)
  | EditorAction.HistoryPrevious -> Some ("historyPrevious", None)
  | EditorAction.HistoryNext -> Some ("historyNext", None)
  | _ -> None

/// Run the TUI client, connecting to a running daemon.
let run (daemonInfo: DaemonInfo) = task {
  let dashboardPort = daemonInfo.Port + 1
  let baseUrl = sprintf "http://localhost:%d" dashboardPort
  use client = new HttpClient()
  client.Timeout <- TimeSpan.FromHours(24.0)

  // Verify daemon is reachable
  let mutable connError = None
  try
    let! resp = client.GetAsync(sprintf "%s/dashboard" baseUrl)
    resp.EnsureSuccessStatusCode() |> ignore
  with ex ->
    connError <- Some (sprintf "Cannot connect to daemon at %s: %s" baseUrl ex.Message)

  match connError with
  | Some msg ->
    eprintfn "%s" msg
    return 1
  | None ->

  use cts = new CancellationTokenSource()

  let rows = Console.WindowHeight
  let cols = Console.WindowWidth
  let mutable state = TerminalMode.TerminalState.create rows cols
  let mutable prevFrame = ""
  let mutable lastRegions : RenderRegion list = []
  let mutable lastSessionState = "Connecting..."
  let mutable lastEvalCount = 0

  // Set up raw terminal mode
  TerminalMode.setupRawMode ()

  let render () =
    lock TerminalUIState.consoleLock (fun () ->
      try
        let frame =
          TerminalRender.renderFrame state.Layout lastRegions lastSessionState lastEvalCount
        let output =
          if prevFrame.Length = 0 then frame
          else
            let d = FrameDiff.diff prevFrame frame
            if d.Length = 0 then ""
            else d
        if output.Length > 0 then
          Console.Write(output)
        prevFrame <- frame
      with _ -> ())

  // Initial render
  render ()

  // Start SSE listener in background
  let sseTask = task {
    try
      use sseClient = new HttpClient()
      sseClient.Timeout <- TimeSpan.FromHours(24.0)
      let! stream = sseClient.GetStreamAsync(sprintf "%s/api/state" baseUrl)
      use reader = new IO.StreamReader(stream)
      while not cts.Token.IsCancellationRequested do
        let! line = reader.ReadLineAsync()
        if not (isNull line) then
          if line.StartsWith("data: ") then
            let json = line.Substring(6)
            match parseStateEvent json with
            | Some (sessionState, evalCount, regionData) ->
              lastSessionState <- sessionState
              lastEvalCount <- evalCount
              lastRegions <- regionData |> List.map DaemonRegionData.toRenderRegion
              render ()
            | None -> ()
    with
    | :? OperationCanceledException -> ()
    | ex -> eprintfn "SSE connection lost: %s" ex.Message
  }

  let mutable exitCode = 0
  try
    // Key reading loop
    while not cts.Token.IsCancellationRequested do
      // Check for terminal resize
      let newRows = Console.WindowHeight
      let newCols = Console.WindowWidth
      if newRows <> state.Layout.Rows || newCols <> state.Layout.Cols then
        state <- TerminalMode.TerminalState.resize state newRows newCols
        prevFrame <- ""
        lock TerminalUIState.consoleLock (fun () ->
          Console.Write(AnsiCodes.clearScreen))
        render ()

      if Console.KeyAvailable then
        let keyInfo = Console.ReadKey(true)
        match TerminalInput.mapKey keyInfo with
        | Some TerminalCommand.Quit ->
          cts.Cancel()
        | Some TerminalCommand.Redraw ->
          prevFrame <- ""
          lock TerminalUIState.consoleLock (fun () ->
            Console.Write(AnsiCodes.clearScreen))
          render ()
        | Some TerminalCommand.CycleFocus ->
          state <- TerminalMode.TerminalState.cycleFocus state
          render ()
        | Some TerminalCommand.ScrollUp ->
          state <- TerminalMode.TerminalState.scroll state -3
          render ()
        | Some TerminalCommand.ScrollDown ->
          state <- TerminalMode.TerminalState.scroll state 3
          render ()
        | Some (TerminalCommand.Action action) ->
          match actionToApi action with
          | Some (name, value) ->
            do! dispatchAction client baseUrl name value
          | None -> ()
        | None -> ()
      else
        try
          do! Threading.Tasks.Task.Delay(16, cts.Token)
        with :? OperationCanceledException -> ()
  finally
    TerminalMode.restoreConsole ()
    try cts.Cancel() with _ -> ()

  return exitCode
}
