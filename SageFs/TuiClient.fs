module SageFs.Server.TuiClient

open System
open System.Net.Http
open System.Threading
open SageFs
open SageFs.Measures
open SageFs.Utils

/// Convert an InputEvent (from VT parser) to a TerminalCommand via KeyMap lookup
let mapInputEvent (keyMap: KeyMap) (ev: InputEvent) : TerminalCommand option =
  match ev with
  | KeyEvent (key, ch, mods) ->
    let ki = ConsoleKeyInfo(ch, key, mods.HasFlag(ConsoleModifiers.Shift), mods.HasFlag(ConsoleModifiers.Alt), mods.HasFlag(ConsoleModifiers.Control))
    TerminalInput.mapKeyWith keyMap ki
  | InputEvent.MouseEvent _ -> None // Mouse handled separately

/// Run the TUI client, connecting to a running daemon.
let run (daemonInfo: DaemonInfo) = task {
  let dashboardPort = daemonInfo.DashboardPort
  let baseUrl = sprintf "http://localhost:%d" dashboardPort
  use handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
  use client = new HttpClient(handler)
  client.Timeout <- Timeouts.sseKeepAlive

  // Verify daemon is reachable
  let mutable connError = None
  try
    let! resp = client.GetAsync(sprintf "%s/dashboard" baseUrl)
    resp.EnsureSuccessStatusCode() |> ignore
  with ex ->
    connError <- Some (sprintf "Cannot connect to SageFs daemon at %s\n  %s\n\nIs the daemon running? Start it with:\n  sagefs --proj <project.fsproj>" baseUrl ex.Message)

  match connError with
  | Some msg ->
    Log.error "%s" msg
    return 1
  | None ->

  use cts = new CancellationTokenSource()

  let rows = Console.WindowHeight
  let cols = Console.WindowWidth
  let mutable gridRows = rows
  let mutable gridCols = cols
  let mutable grid = CellGrid.rent gridRows gridCols
  let mutable focusedPane = PaneId.Output // Fix: start on visible pane
  let mutable scrollOffsets = Map.empty<PaneId, int>
  let mutable prevGrid : CellGrid option = None
  let mutable lastRegions : RenderRegion list = []
  let mutable testSourceLocations = Map.empty<string, string * int>
  let mutable lastSessionState = "Connecting..."
  let mutable lastSessionId = ""
  let mutable lastWorkingDir = ""
  let mutable lastEvalCount = 0
  let mutable lastAvgMs = 0.0
  let mutable lastStandbyLabel = ""
  let mutable lastLiveTestingStatus = ""
  let mutable lastWatchedCount = 0
  let mutable lastDensity = UiDensity.Normal
  // Eval watchdog: tracks whether the daemon was evaluating when SSE disconnected.
  // If so, the reconnecting message is enhanced to mention the interrupted eval.
  let mutable evalInterruptedOnDisconnect = false
  let mutable lastEvalStartTime : DateTime option = None
  let mutable layoutConfig = LayoutConfig.defaults
  let mutable currentTheme =
    match ThemePresets.tryFind "Kanagawa" with
    | Some t -> t
    | None -> Theme.defaults
  let mutable currentThemeName = "Kanagawa"
  let sessionThemes = System.Collections.Generic.Dictionary<string, string>()

  // Load keybindings from config, merge with defaults
  let keyMap =
    let cwd = IO.Directory.GetCurrentDirectory()
    match DirectoryConfig.load cwd with
    | Some cfg when not cfg.Keybindings.IsEmpty ->
      KeyMap.merge cfg.Keybindings KeyMap.defaults
    | _ -> KeyMap.defaults

  // Set up raw terminal mode (includes mouse tracking + VT input)
  TerminalMode.setupRawMode ()
  ConsoleInput.RawInput.start ()

  let mutable lastFrameMs = 0.0

  // Time-travel: buffer last N region snapshots for keyboard navigation
  let mutable timeTravelState =
    TimeTravel.create { ModelSnapshot.Capacity = 200; ModelSnapshot.Enabled = true }

  // Failing test navigation
  let mutable failingTestIdx = -1
  let mutable failingNavHint = ""
  // Test source locations for jump-to-source (F12)
  let mutable testSourceLocations = Map.empty<string, string * int>

  let render () =
    lock TerminalUIState.consoleLock (fun () ->
      try
        let frameSw = System.Diagnostics.Stopwatch.StartNew()
        let ttStatus = TimeTravel.formatStatus timeTravelState
        let statusLeft =
          let sid = match lastSessionId.Length > 8 with | true -> lastSessionId.[..7] | false -> lastSessionId
          let standby = match lastStandbyLabel.Length > 0 with | true -> sprintf " | %s" lastStandbyLabel | false -> ""
          let liveTesting = match lastLiveTestingStatus.Length > 0 with | true -> sprintf " | %s" lastLiveTestingStatus | false -> ""
          let ttPart = match ttStatus with | Some s -> sprintf " | %s" s | None -> ""
          let failNav = match failingNavHint.Length > 0 with | true -> sprintf " | %s" failingNavHint | false -> ""
          let stateIcon, stateLabel =
            match lastSessionState with
            | "Ready" -> "⬤", "Ready"
            | s when s.StartsWith("Evaluating") ->
              let elapsed =
                match lastEvalStartTime with
                | Some t -> sprintf " (%dms)" (int (DateTime.UtcNow - t).TotalMilliseconds)
                | None -> ""
              "⟳", sprintf "Evaluating%s" elapsed
            | "WarmingUp" -> "⟳", "WarmingUp"
            | s when s.StartsWith("Faulted") -> "✗", "FAULTED — Ctrl+R to hard reset"
            | "Uninitialized" | "Connecting..." -> "·", lastSessionState
            | _ -> "⟳", lastSessionState
          let stateStr = sprintf "%s %s" stateIcon stateLabel
          match lastEvalCount > 0 with
          | true ->
            sprintf " %s %s | evals: %d (avg %.0fms)%s%s%s%s | %s" sid stateStr lastEvalCount lastAvgMs standby liveTesting ttPart failNav (PaneId.displayName focusedPane)
          | false ->
            sprintf " %s %s | evals: %d%s%s%s%s | %s" sid stateStr lastEvalCount standby liveTesting ttPart failNav (PaneId.displayName focusedPane)
        let statusRight = sprintf " %s | %.1fms |%s" currentThemeName lastFrameMs (StatusHints.build keyMap focusedPane layoutConfig.VisiblePanes lastWatchedCount lastDensity)

        // When viewing history, use historical regions; otherwise use live
        let displayRegions =
          match TimeTravel.isLive timeTravelState with
          | true -> lastRegions
          | false ->
            match TimeTravel.currentModel timeTravelState with
            | Some regions -> regions
            | None -> lastRegions

        let drawSw = System.Diagnostics.Stopwatch.StartNew()
        let statusTheme =
          match lastSessionState with
          | s when s.StartsWith("Faulted") -> { currentTheme with BgStatus = "#4a1515" }
          | s when s.StartsWith("Evaluating") -> { currentTheme with BgStatus = "#2d2000" }
          | "WarmingUp" -> { currentTheme with BgStatus = "#002430" }
          | _ -> currentTheme
        let cursorPos = Screen.drawWith layoutConfig statusTheme grid displayRegions focusedPane scrollOffsets statusLeft statusRight
        drawSw.Stop()
        Instrumentation.renderScreenDrawMs.Record(drawSw.Elapsed.TotalMilliseconds)

        let cursorRow, cursorCol =
          match cursorPos with
          | Some (r, c) -> r, c
          | None -> 0, 0

        let emitSw = System.Diagnostics.Stopwatch.StartNew()
        let output =
          match prevGrid with
          | None ->
            Instrumentation.renderFullEmitCount.Add(1L)
            AnsiEmitter.emit grid cursorRow cursorCol
          | Some prev ->
            Instrumentation.renderDiffEmitCount.Add(1L)
            AnsiEmitter.emitDiff prev grid cursorRow cursorCol
        emitSw.Stop()
        Instrumentation.renderEmitMs.Record(emitSw.Elapsed.TotalMilliseconds)

        let writeSw = System.Diagnostics.Stopwatch.StartNew()
        match output.Length > 0 with
        | true -> Console.Write(output)
        | false -> ()
        writeSw.Stop()
        Instrumentation.renderConsoleWriteMs.Record(writeSw.Elapsed.TotalMilliseconds)

        prevGrid <- Some (CellGrid.clone grid)
        frameSw.Stop()
        let frameMs = frameSw.Elapsed.TotalMilliseconds
        Instrumentation.renderFrameTotalMs.Record(frameMs)
        lastFrameMs <- frameMs
      with _ -> ())

  // Initial render
  render ()

  // Periodic refresh while evaluating — keeps the elapsed-ms counter live
  let _evalTimerLoop =
    System.Threading.Tasks.Task.Run(fun () ->
      task {
        while not cts.IsCancellationRequested do
          try do! System.Threading.Tasks.Task.Delay(100, cts.Token) with _ -> ()
          match not cts.IsCancellationRequested && lastSessionState.StartsWith("Evaluating") with
          | true -> render ()
          | false -> ()
      } :> System.Threading.Tasks.Task)

  let navigateFailingTests (delta: int) =
    let failing =
      lastRegions
      |> List.toArray
      |> Array.collect (fun r ->
        r.LineAnnotations
        |> Array.filter (fun a -> a.Icon = Features.LiveTesting.GutterIcon.TestFailed)
        |> Array.map (fun a -> a.Line))
      |> Array.sort
      |> Array.distinct
    match failing.Length with
    | 0 -> failingNavHint <- ""
    | total ->
      let newIdx =
        match failingTestIdx < 0 with
        | true -> match delta > 0 with | true -> 0 | false -> total - 1
        | false -> (failingTestIdx + delta + total) % total
      failingTestIdx <- newIdx
      let targetLine = failing.[newIdx]
      failingNavHint <- sprintf "↯%d/%d" (newIdx + 1) total
      let totalLines =
        lastRegions
        |> List.tryFind (fun r -> r.Id = "editor")
        |> Option.map (fun r -> r.Content.Split('\n').Length)
        |> Option.defaultValue 100
      let approxHeight = max 1 (gridRows - 4)
      scrollOffsets <- scrollOffsets |> Map.add PaneId.Editor (max 0 (totalLines - approxHeight - targetLine + 3))

  // Start SSE listener in background using shared DaemonClient
  let sseTask =
    DaemonClient.runSseListener
      baseUrl
      (fun event regions ->
        evalInterruptedOnDisconnect <- false
        match event.ActiveWorkingDir.Length > 0 && event.ActiveWorkingDir <> lastWorkingDir && lastWorkingDir.Length > 0 with
        | true ->
          sessionThemes.[lastWorkingDir] <- currentThemeName
          match sessionThemes.TryGetValue(event.ActiveWorkingDir) with
          | true, themeName ->
            match ThemePresets.tryFind themeName with
            | Some theme ->
              currentTheme <- theme
              currentThemeName <- themeName
            | None -> ()
          | false, _ -> ()
        | false -> ()
        match event.ActiveWorkingDir.Length > 0 with
        | true -> lastWorkingDir <- event.ActiveWorkingDir
        | false -> ()
        lastSessionId <- event.SessionId
        lastSessionState <- event.SessionState
        match event.SessionState with
        | s when s.StartsWith("Evaluating") ->
          match lastEvalStartTime with
          | None -> lastEvalStartTime <- Some DateTime.UtcNow
          | Some _ -> ()
        | _ -> lastEvalStartTime <- None
        lastEvalCount <- event.EvalCount
        lastAvgMs <- event.AvgMs
        lastStandbyLabel <- event.StandbyLabel
        lastLiveTestingStatus <- event.LiveTestingStatus
        lastWatchedCount <- event.WatchedCount
        lastRegions <- regions
        match event.TestSourceLocations.IsEmpty with
        | true -> ()
        | false -> testSourceLocations <- event.TestSourceLocations
        // Record region snapshot for time-travel (only in live mode)
        timeTravelState <-
          TimeTravel.record event.SessionState 0.0<SageFs.Measures.ms> regions timeTravelState
        render ())
      (fun _ ->
        let wasEvaluating =
          lastSessionState.Contains("Evaluating", StringComparison.OrdinalIgnoreCase)
          || lastSessionState.Contains("evaluating", StringComparison.OrdinalIgnoreCase)
        match wasEvaluating with
        | true ->
          evalInterruptedOnDisconnect <- true
          lastEvalStartTime <- None
          lastSessionState <- "⚠ Eval interrupted — daemon disconnected (reconnecting...)"
        | false ->
          lastSessionState <- sprintf "%s (reconnecting...)" lastSessionState
        render ())
      cts.Token

  let mutable exitCode = 0
  try
    // Input event loop
    while not cts.Token.IsCancellationRequested do
      // Check for terminal resize
      let newRows = Console.WindowHeight
      let newCols = Console.WindowWidth
      match newRows <> gridRows || newCols <> gridCols with
      | true ->
        gridRows <- newRows
        gridCols <- newCols
        CellGrid.release grid
        grid <- CellGrid.rent gridRows gridCols
        prevGrid <- None
        lock TerminalUIState.consoleLock (fun () ->
          Console.Write(AnsiCodes.clearScreen))
        render ()
      | false -> ()

      // Process raw stdin chars through VT parser
      ConsoleInput.RawInput.processChars ()

      // Drain parsed input events
      let mutable hadInput = false
      let mutable ev = ConsoleInput.RawInput.tryRead ()
      while ev.IsSome && not cts.Token.IsCancellationRequested do
        hadInput <- true
        match ev with
        | Some (InputEvent.MouseEvent me) ->
          match me.Action with
          | MouseAction.Press when me.Button = MouseButton.Left ->
            // Click-to-focus pane + editor cursor / session click
            let paneRects = Screen.computeLayoutWith layoutConfig gridRows gridCols |> fst
            let clicked =
              paneRects
              |> List.tryFind (fun (_, r) ->
                me.Row >= r.Row && me.Row < r.Row + r.Height &&
                me.Col >= r.Col && me.Col < r.Col + r.Width)
            match clicked with
            | Some (id, r) ->
              focusedPane <- id
              match id with
              | PaneId.Editor ->
                let contentRow = me.Row - r.Row - 1
                let contentCol = me.Col - r.Col - 1
                match contentRow >= 0 && contentCol >= 0 with
                | true ->
                  let scrollOff = scrollOffsets |> Map.tryFind PaneId.Editor |> Option.defaultValue 0
                  let line = contentRow + scrollOff
                  do! DaemonClient.dispatch client baseUrl (EditorAction.SetCursorPosition (line, contentCol))
                | false -> ()
              | PaneId.Sessions ->
                let contentRow = me.Row - r.Row - 1
                let scrollOff = scrollOffsets |> Map.tryFind PaneId.Sessions |> Option.defaultValue 0
                let sessionIdx = contentRow + scrollOff
                match contentRow >= 0 with
                | true ->
                  do! DaemonClient.dispatch client baseUrl (EditorAction.SessionSetIndex sessionIdx)
                | false -> ()
              | _ -> ()
            | None -> ()
            render ()
          | MouseAction.WheelUp ->
            let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
            scrollOffsets <- scrollOffsets |> Map.add focusedPane (cur + 3)
            render ()
          | MouseAction.WheelDown ->
            let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
            scrollOffsets <- scrollOffsets |> Map.add focusedPane (max 0 (cur - 3))
            render ()
          | _ -> () // Ignore release/move for now

        | Some (KeyEvent (key, ch, mods)) ->
          let ki = ConsoleKeyInfo(ch, key, mods.HasFlag(ConsoleModifiers.Shift), mods.HasFlag(ConsoleModifiers.Alt), mods.HasFlag(ConsoleModifiers.Control))
          match TerminalInput.mapKeyWith keyMap ki with
          | Some TerminalCommand.Quit ->
            cts.Cancel()
          | Some TerminalCommand.Redraw ->
            prevGrid <- None
            lock TerminalUIState.consoleLock (fun () ->
              Console.Write(AnsiCodes.clearScreen))
            render ()
          | Some TerminalCommand.CycleFocus ->
            focusedPane <- PaneId.nextVisible layoutConfig.VisiblePanes focusedPane
            render ()
          | Some (TerminalCommand.FocusDirection dir) ->
            let paneRects = Screen.computeLayoutWith layoutConfig gridRows gridCols |> fst
            focusedPane <- PaneId.navigate dir focusedPane paneRects
            render ()
          | Some TerminalCommand.ScrollUp ->
            let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
            scrollOffsets <- scrollOffsets |> Map.add focusedPane (cur + 3)
            render ()
          | Some TerminalCommand.ScrollDown ->
            let cur = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
            scrollOffsets <- scrollOffsets |> Map.add focusedPane (max 0 (cur - 3))
            render ()
          | Some (TerminalCommand.TogglePane pid) ->
            layoutConfig <- LayoutConfig.togglePane pid layoutConfig
            match layoutConfig.VisiblePanes.Contains focusedPane with
            | false -> focusedPane <- PaneId.firstVisible layoutConfig.VisiblePanes
            | true -> ()
            render ()
          | Some (TerminalCommand.LayoutPreset presetName) ->
            layoutConfig <-
              match presetName with
              | "focus" -> LayoutConfig.focus
              | "minimal" -> LayoutConfig.minimal
              | _ -> LayoutConfig.defaults
            match layoutConfig.VisiblePanes.Contains focusedPane with
            | false -> focusedPane <- PaneId.firstVisible layoutConfig.VisiblePanes
            | true -> ()
            render ()
          | Some (TerminalCommand.ResizeH d) ->
            layoutConfig <- LayoutConfig.resizeH d layoutConfig
            render ()
          | Some (TerminalCommand.ResizeV d) ->
            layoutConfig <- LayoutConfig.resizeV d layoutConfig
            render ()
          | Some (TerminalCommand.ResizeR d) ->
            layoutConfig <- LayoutConfig.resizeR d layoutConfig
            render ()
          | Some TerminalCommand.CycleTheme ->
            let name, theme = ThemePresets.cycleNext currentTheme
            currentTheme <- theme
            currentThemeName <- name
            match lastWorkingDir.Length > 0 with
            | true -> sessionThemes.[lastWorkingDir] <- name
            | false -> ()
            render ()
          | Some TerminalCommand.HotReloadWatchAll ->
            match lastSessionId.Length > 0 with
            | true ->
              try
                let! _ = client.PostAsync(sprintf "%s/api/sessions/%s/hotreload/watch-all" baseUrl lastSessionId, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))
                ()
              with _ -> ()
            | false -> ()
          | Some TerminalCommand.HotReloadUnwatchAll ->
            match lastSessionId.Length > 0 with
            | true ->
              try
                let! _ = client.PostAsync(sprintf "%s/api/sessions/%s/hotreload/unwatch-all" baseUrl lastSessionId, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))
                ()
              with _ -> ()
            | false -> ()
          | Some TerminalCommand.EnableLiveTesting ->
            do! DaemonClient.dispatchAction client baseUrl "enableLiveTesting" None |> Async.AwaitTask
            render ()
          | Some TerminalCommand.DisableLiveTesting ->
            do! DaemonClient.dispatchAction client baseUrl "disableLiveTesting" None |> Async.AwaitTask
            render ()
          | Some TerminalCommand.CycleRunPolicy ->
            do! DaemonClient.dispatchAction client baseUrl "cycleRunPolicy" None |> Async.AwaitTask
            render ()
          | Some TerminalCommand.ToggleCoverage ->
            do! DaemonClient.dispatchAction client baseUrl "toggleCoverage" None |> Async.AwaitTask
            render ()
          | Some TerminalCommand.TimeTravelBack ->
            timeTravelState <- TimeTravel.stepBack timeTravelState
            render ()
          | Some TerminalCommand.TimeTravelForward ->
            timeTravelState <- TimeTravel.stepForward timeTravelState
            render ()
          | Some TerminalCommand.TimeTravelGoLive ->
            timeTravelState <- TimeTravel.goLive timeTravelState
            render ()
          | Some TerminalCommand.CycleDensity ->
            lastDensity <- UiDensity.cycle lastDensity
            render ()
          | Some TerminalCommand.NextFailingTest ->
            navigateFailingTests 1
            render ()
          | Some TerminalCommand.PrevFailingTest ->
            navigateFailingTests (-1)
            render ()
          | Some TerminalCommand.JumpToTest ->
            let scrollOff = scrollOffsets |> Map.tryFind focusedPane |> Option.defaultValue 0
            match JumpToTest.getSelectedTestLocation lastRegions focusedPane scrollOff testSourceLocations with
            | Some (file, line) -> JumpToTest.openInEditor file line
            | None -> ()
          | Some TerminalCommand.MarkAllStale ->
            try
              let! _ = client.PostAsync(sprintf "%s/api/live-testing/mark-all-stale" baseUrl, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))
              ()
            with _ -> ()
            render ()
          | Some (TerminalCommand.Action action) ->
            let remappedAction =
              match focusedPane = PaneId.Sessions with
              | true ->
                match action with
                | EditorAction.MoveCursor Direction.Up -> EditorAction.SessionNavUp
                | EditorAction.MoveCursor Direction.Down -> EditorAction.SessionNavDown
                | EditorAction.NewLine -> EditorAction.SessionSelect
                | EditorAction.DeleteBackward -> EditorAction.SessionDelete
                | EditorAction.DeleteForward -> EditorAction.SessionDelete
                | other -> other
              | false -> action
            do! DaemonClient.dispatch client baseUrl remappedAction
          | None ->
            match mods.HasFlag(ConsoleModifiers.Control), key with
            | true, ConsoleKey.R ->
              do! DaemonClient.dispatch client baseUrl EditorAction.HardResetSession
            | _ ->
              match ch with
              | c when c >= ' ' && c <= '~' ->
                do! DaemonClient.dispatch client baseUrl (EditorAction.InsertChar ch)
              | c when c > '\x7f' ->
                do! DaemonClient.dispatch client baseUrl (EditorAction.InsertChar ch)
              | _ -> ()

        | None -> ()

        ev <- ConsoleInput.RawInput.tryRead ()

      match hadInput with
      | false ->
        try
          do! Threading.Tasks.Task.Delay(8, cts.Token)
        with :? OperationCanceledException -> ()
      | true -> ()
  finally
    ConsoleInput.RawInput.stop ()
    TerminalMode.restoreConsole ()
    try cts.Cancel() with _ -> ()

  return exitCode
}
