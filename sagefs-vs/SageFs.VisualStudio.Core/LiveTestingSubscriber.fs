namespace SageFs.VisualStudio.Core

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks

/// SSE subscriber for the /events endpoint.
/// Parses event:/data: lines, maintains LiveTestState, dispatches callbacks.
type LiveTestingSubscriber(port: int) =
  let http = new HttpClient()
  let mutable cts: CancellationTokenSource option = None
  let mutable state = LiveTestState.empty
  let stateChanged = Event<LiveTestState>()
  let summaryChanged = Event<TestSummary>()
  let changeEmitted = Event<LiveTestChange>()

  /// Fires when test state changes (discovery, results, toggle)
  [<CLIEvent>]
  member _.StateChanged = stateChanged.Publish

  /// Fires when a discrete change occurs (for UI adapters)
  [<CLIEvent>]
  member _.ChangeEmitted = changeEmitted.Publish

  /// Fires when a test_summary SSE event arrives
  [<CLIEvent>]
  member _.SummaryChanged = summaryChanged.Publish

  member _.State = state

  /// C#-friendly property for current state
  member _.CurrentState = state

  member _.Start() =
    let newCts = new CancellationTokenSource()
    cts <- Some newCts
    let url = sprintf "http://localhost:%d/events" port
    let mutable retryDelay = 1000

    let rec loop () = task {
      try
        let! resp =
          http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, newCts.Token)
        use! stream = resp.Content.ReadAsStreamAsync(newCts.Token)
        use reader = new IO.StreamReader(stream)
        retryDelay <- 1000
        let mutable currentEvent = "message"
        while not (reader.EndOfStream || newCts.Token.IsCancellationRequested) do
          let! line = reader.ReadLineAsync(newCts.Token)
          if line <> null then
            if line.StartsWith("event: ") then
              currentEvent <- line.Substring(7).Trim()
            elif line.StartsWith("data: ") then
              let json = line.Substring(6)
              let events = LiveTestingParser.parseSseEvent currentEvent json
              for evt in events do
                let newState, changes = LiveTestState.update evt state
                state <- newState
                for change in changes do
                  changeEmitted.Trigger(change)
                match evt with
                | LiveTestEvent.SummaryUpdated s -> summaryChanged.Trigger(s)
                | _ -> ()
              if not events.IsEmpty then
                stateChanged.Trigger(state)
              currentEvent <- "message"
            elif line.Trim() = "" then
              currentEvent <- "message"
      with
      | :? OperationCanceledException -> ()
      | _ ->
        retryDelay <- min (retryDelay * 2) 30000
        do! Task.Delay(retryDelay, newCts.Token)
        if not newCts.Token.IsCancellationRequested then
          do! loop ()
    }
    Task.Run(fun () -> loop () :> Task) |> ignore

  member _.Summary () = LiveTestState.summary state

  member _.TestsForFile (filePath: string) =
    LiveTestState.testsForFile filePath state

  member _.ResultFor (testId: TestId) =
    LiveTestState.resultFor testId state

  member _.Stop() =
    match cts with
    | Some c -> c.Cancel(); c.Dispose(); cts <- None
    | None -> ()

  interface IDisposable with
    member this.Dispose() =
      this.Stop()
      http.Dispose()

  /// Find a test result at a specific file/line. Returns None if no test there.
  static member findTestAtLine
    (state: LiveTestState, filePath: string, line: int) : (TestInfo * TestResult option) voption =
    let tests = LiveTestState.testsForFile filePath state
    tests
    |> List.tryFind (fun t ->
      match t.Line with
      | Some l -> l = line
      | None -> false)
    |> function
      | Some t ->
        let result = LiveTestState.resultFor t.Id state
        ValueSome (t, result)
      | None -> ValueNone

  /// Find a test by name (display name or full name contains the query).
  /// Returns null if no match (C#-friendly).
  static member findTestByName
    (state: LiveTestState, name: string) : struct (TestInfo * TestResult option) option =
    if System.String.IsNullOrWhiteSpace name then None
    else
      state.Tests
      |> Map.tryPick (fun _ t ->
        if t.DisplayName.Contains(name, System.StringComparison.OrdinalIgnoreCase)
           || t.FullName.Contains(name, System.StringComparison.OrdinalIgnoreCase)
        then Some t
        else None)
      |> Option.map (fun t ->
        let result = LiveTestState.resultFor t.Id state
        struct (t, result))

  /// Format a CodeLens label for a test at a given line.
  static member formatTestLabel(info: TestInfo, result: TestResult option) : string =
    match result with
    | Some r ->
      match r.Outcome with
      | TestOutcome.Passed durationMs -> sprintf "✓ Passed (%0.0fms)" durationMs
      | TestOutcome.Failed (msg, _) -> sprintf "✗ Failed: %s" (if msg.Length > 50 then msg.[..49] + "…" else msg)
      | TestOutcome.Skipped reason -> sprintf "⊘ Skipped: %s" reason
      | TestOutcome.Errored msg -> sprintf "✗ Error: %s" (if msg.Length > 50 then msg.[..49] + "…" else msg)
      | TestOutcome.Running -> "◆ Running…"
      | TestOutcome.Detected -> "● Detected"
      | TestOutcome.Stale -> "◌ Stale"
      | TestOutcome.PolicyDisabled -> "⊘ Disabled"
    | None -> "● Not Run"

  /// Format a tooltip with full test details.
  static member formatTestTooltip(info: TestInfo, result: TestResult option) : string =
    match result with
    | Some r ->
      let status =
        match r.Outcome with
        | TestOutcome.Passed _ -> "Passed"
        | TestOutcome.Failed _ -> "Failed"
        | TestOutcome.Skipped _ -> "Skipped"
        | TestOutcome.Errored _ -> "Errored"
        | TestOutcome.Running -> "Running"
        | TestOutcome.Detected -> "Detected"
        | TestOutcome.Stale -> "Stale"
        | TestOutcome.PolicyDisabled -> "Disabled"
      let duration =
        match r.DurationMs with
        | Some ms -> sprintf " (%0.1fms)" ms
        | None -> ""
      let output =
        match r.Output with
        | Some o when o.Length > 0 -> sprintf "\n%s" o
        | _ -> ""
      sprintf "%s — %s%s%s" info.DisplayName status duration output
    | None -> sprintf "%s — Not Run" info.DisplayName
