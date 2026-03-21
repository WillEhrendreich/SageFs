namespace SageFs.VisualStudio.Core

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks

/// SSE subscriber for the /events endpoint.
/// Parses event:/data: lines, maintains LiveTestState, dispatches callbacks.
type LiveTestingSubscriber(port: int) =
  let handler = new HttpClientHandler(AutomaticDecompression = System.Net.DecompressionMethods.All)
  let http = new HttpClient(handler)
  let mutable cts: CancellationTokenSource option = None
  let mutable state = LiveTestState.empty
  let mutable connected = false
  let stateChanged = Event<LiveTestState>()
  let summaryChanged = Event<TestSummary>()
  let changeEmitted = Event<LiveTestChange>()
  let featureReceived = Event<FeatureEvent>()
  let narrativesReceived = Event<FailureNarrative array>()
  let connectionLost = Event<unit>()
  let connectionRestored = Event<unit>()

  /// Fires when test state changes (discovery, results, toggle)
  [<CLIEvent>]
  member _.StateChanged = stateChanged.Publish

  /// Fires when a discrete change occurs (for UI adapters)
  [<CLIEvent>]
  member _.ChangeEmitted = changeEmitted.Publish

  /// Fires when a test_summary SSE event arrives
  [<CLIEvent>]
  member _.SummaryChanged = summaryChanged.Publish

  /// Fires when a feature event (eval_diff, cell_dependencies, etc.) arrives
  [<CLIEvent>]
  member _.FeatureReceived = featureReceived.Publish

  /// Fires when failure narratives arrive
  [<CLIEvent>]
  member _.NarrativesReceived = narrativesReceived.Publish

  /// Fires when the SSE connection drops (for eval watchdog)
  [<CLIEvent>]
  member _.ConnectionLost = connectionLost.Publish

  /// Fires when the SSE connection is restored after a drop
  [<CLIEvent>]
  member _.ConnectionRestored = connectionRestored.Publish

  /// Whether the SSE stream is currently connected
  member _.IsConnected = connected

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
        match connected with
        | false ->
          connected <- true
          connectionRestored.Trigger()
        | true -> ()
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
              match LiveTestingParser.parseFeatureSseEvent currentEvent json with
              | Some fe -> featureReceived.Trigger(fe)
              | None -> ()
              if currentEvent = "failure_narratives" then
                let narratives = LiveTestingParser.parseFailureNarratives json
                if narratives.Length > 0 then
                  let newMap =
                    narratives |> Array.fold (fun m n -> Map.add n.TestId n m) state.FailureNarratives
                  state <- { state with FailureNarratives = newMap }
                  narrativesReceived.Trigger(narratives)
                  stateChanged.Trigger(state)
              currentEvent <- "message"
            elif line.Trim() = "" then
              currentEvent <- "message"
      with
      | :? OperationCanceledException -> ()
      | _ ->
        match connected with
        | true ->
          connected <- false
          connectionLost.Trigger()
        | false -> ()
        retryDelay <- min (retryDelay * 2) 30000
        do! Task.Delay(retryDelay, newCts.Token)
        if not newCts.Token.IsCancellationRequested then
          do! loop ()
    }
    Task.Run(fun () ->
      task {
        try do! loop ()
        with :? OperationCanceledException -> ()
      } :> Task) |> ignore

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

  /// Format a tooltip with full test details. Optionally enriched with freshness and narrative context.
  static member formatTestTooltip(info: TestInfo, result: TestResult option, ?freshness: ResultFreshness, ?narrative: FailureNarrative, ?lastDecision: LiveTestingDecision) : string =
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
        | TestOutcome.Stale ->
          match freshness with
          | Some ResultFreshness.StaleCodeEdited -> "Stale — code edited since last run"
          | Some ResultFreshness.StaleWrongGeneration -> "Stale — generation mismatch (re-run needed)"
          | _ -> "Stale"
        | TestOutcome.PolicyDisabled -> "Disabled by policy"
      let duration =
        match r.DurationMs with
        | Some ms -> sprintf " (%0.1fms)" ms
        | None -> ""
      let output =
        match r.Output with
        | Some o when o.Length > 0 -> sprintf "\n%s" o
        | _ -> ""
      let narrativeText =
        match narrative with
        | Some n when n.Summary <> "" -> sprintf "\nℹ️ %s" n.Summary
        | _ -> ""
      let explanationText =
        match lastDecision with
        | Some decision -> sprintf "\n%s" (TestSummary.formatDecisionHint decision)
        | None -> ""
      sprintf "%s — %s%s%s%s%s" info.DisplayName status duration output narrativeText explanationText
    | None -> sprintf "%s — Not Run" info.DisplayName
