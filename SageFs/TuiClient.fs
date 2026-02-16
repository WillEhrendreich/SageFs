module SageFs.Server.TuiClient

open System
open System.Net.Http
open System.Threading
open SageFs

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
    connError <- Some (sprintf "Cannot connect to SageFs daemon at %s\n  %s\n\nIs the daemon running? Start it with:\n  sagefs --proj <project.fsproj>" baseUrl ex.Message)

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

  // Start SSE listener in background using shared DaemonClient
  let sseTask =
    DaemonClient.runSseListener
      baseUrl
      (fun sessionState evalCount regions ->
        lastSessionState <- sessionState
        lastEvalCount <- evalCount
        lastRegions <- regions
        render ())
      (fun _ ->
        lastSessionState <- sprintf "%s (reconnecting...)" lastSessionState
        render ())
      cts.Token

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
        | Some (TerminalCommand.FocusDirection dir) ->
          state <- TerminalMode.TerminalState.focusDirection dir state
          render ()
        | Some TerminalCommand.ScrollUp ->
          state <- TerminalMode.TerminalState.scroll state -3
          render ()
        | Some TerminalCommand.ScrollDown ->
          state <- TerminalMode.TerminalState.scroll state 3
          render ()
        | Some (TerminalCommand.Action action) ->
          do! DaemonClient.dispatch client baseUrl action
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
