# SSE failure_narratives Integration Guide

## 1. DAEMON SSE FORMAT (C# SageFs.Core/SseWriter.fs)

### Payload Shape (lines 73-87)

\\\sharp
/// Format failure narratives as an SSE event string
let formatFailureNarrativesEvent (opts: JsonSerializerOptions) (sessionId: string option) (narratives: Map<Features.LiveTesting.TestId, Features.LiveTesting.FailureNarrative>) : string =
  let payload =
    narratives
    |> Map.toArray
    |> Array.map (fun (tid, n) ->
      {| TestId = Features.LiveTesting.TestId.value tid; LastPassedAt = n.LastPassedAt; TimeSinceLastPass = n.TimeSinceLastPass
         CausalChanges = n.CausalChanges |> List.map (fun c ->
           match c with
           | Features.LiveTesting.CausalChange.SymbolChanged s -> {| Kind = "symbol"; Name = s |}
           | Features.LiveTesting.CausalChange.FileChanged f -> {| Kind = "file"; Name = f |}
           | Features.LiveTesting.CausalChange.Unknown -> {| Kind = "unknown"; Name = "" |})
         PropertyViolation = n.PropertyViolation; Summary = n.Summary |})
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent \"failure_narratives\" json
\\\

### JSON Payload Example

\\\json
{
  \"event\": \"failure_narratives\",
  \"data\": [
    {
      \"TestId\": \"MyTest.test_should_add\",
      \"LastPassedAt\": \"2024-01-15T10:30:00Z\",
      \"TimeSinceLastPass\": \"PT2H45M\",
      \"CausalChanges\": [
        { \"Kind\": \"symbol\", \"Name\": \"myFunction\" },
        { \"Kind\": \"file\", \"Name\": \"src/Math.fs\" }
      ],
      \"PropertyViolation\": \"Expected 1, got 2\",
      \"Summary\": \"Test started failing after myFunction was changed\"
    }
  ]
}
\\\

### Related Event Formats Nearby (lines 58-94)

- **test_summary** (line 59-61): {Total, Passed, Failed, Running, Stale, Disabled}
- **test_results_batch** (line 64-66): {Entries[], Summary, Freshness, Generation, Completion}
- **file_annotations** (line 69-71): {FileAnnotations[]}

All use the same pattern: serialize domain object → injectSessionId → formatSseEvent

---

## 2. VS CODE EXTENSION (sagefs-vscode/src/)

### LiveTestingListener.fs (lines 1-257)

**Event Processing (lines 177-232)**

\\\sharp
let processEvent (eventType: string) (data: obj) =
  tryHandleEvent eventType (fun () ->
    match eventType with
    | \"test_summary\" ->
      let summary = parseSummary data
      callbacks.OnSummaryUpdate summary
    | \"test_results_batch\" ->
      let events = parseResultsBatch data
      let mutable allChanges = []
      for evt in events do
        let newState, changes = VscLiveTestState.update evt state
        state <- newState
        allChanges <- allChanges @ changes
      if not allChanges.IsEmpty then
        callbacks.OnStateChange allChanges
    | \"state\" ->
      callbacks.OnStatusRefresh ()
    | \"session\" ->
      ()
    | \"bindings_snapshot\" ->
      fieldArray \"Bindings\" data
      |> Option.iter (fun arr ->
        bindings <- arr
        callbacks.OnBindingsUpdate bindings)
    | \"test_trace\" ->
      TestTrace <- Some data
      callbacks.OnTestTraceUpdate data
    | \"eval_diff\"
    | \"cell_dependencies\"
    | \"binding_scope_map\"
    | \"eval_timeline\" ->
      let merged = ... (feature event handler merge)
      processFeatureEvent eventType data merged
    | \"eval_started\" ->
      let filePath = fieldString \"filePath\" data |> Option.defaultValue \"\"
      let bsl = fieldInt \"blockStartLine\" data |> Option.defaultValue 0
      match filePath with
      | \"\" -> ()
      | fp -> callbacks.OnEvalStarted fp bsl
    | \"eval_result\" ->
      let filePath = fieldString \"filePath\" data |> Option.defaultValue \"\"
      let bsl = fieldInt \"blockStartLine\" data |> Option.defaultValue 0
      let output = fieldString \"output\" data |> Option.defaultValue \"\"
      let durationMs = fieldFloat \"durationMs\" data |> Option.defaultValue 0.0
      match filePath with
      | \"\" -> ()
      | fp -> callbacks.OnEvalResult fp bsl output durationMs
    | _ ->
      ())
\\\

**Callbacks Type Definition (lines 138-147)**

\\\sharp
type LiveTestingCallbacks = {
  OnStateChange: VscStateChange list -> unit
  OnSummaryUpdate: VscTestSummary -> unit
  OnStatusRefresh: unit -> unit
  OnBindingsUpdate: obj array -> unit
  OnTestTraceUpdate: obj -> unit
  OnFeatureEvent: FeatureCallbacks option
  OnEvalResult: string -> int -> string -> float -> unit
  OnEvalStarted: string -> int -> unit
}
\\\

**Pattern for Adding failure_narratives**

Add to the match expression:
\\\sharp
| \"failure_narratives\" ->
  let narratives = parseFailureNarratives data
  callbacks.OnFailureNarratives narratives
\\\

Add to callbacks:
\\\sharp
type LiveTestingCallbacks = {
  ...existing fields...
  OnFailureNarratives: (VscTestId * FailureNarrative) array -> unit
}
\\\

### SafeInterop.fs (Helper Functions, lines 1-142)

\\\sharp
// Field accessors (runtime-checked type casts)
let fieldString (name: string) (obj: obj) : string option      // Line 97
let fieldInt (name: string) (obj: obj) : int option            // Line 98
let fieldFloat (name: string) (obj: obj) : float option        // Line 99
let fieldArray (name: string) (obj: obj) : obj array option    // Line 101
let fieldObj : string -> obj -> obj option                     // Line 102

// DU parsing helpers
let duCase (du: obj) : string option                           // Line 112
let duFieldsArray (du: obj) : obj array option                 // Line 117
let duFirstFieldString (du: obj) : string option               // Line 120
\\\

### Extension.fs (Callback Wiring, lines 1-150+)

**Mutable State (lines 26-38)**

\\\sharp
let mutable client: Client.Client option = None
let mutable outputChannel: OutputChannel option = None
let mutable statusBarItem: StatusBarItem option = None
let mutable testStatusBarItem: StatusBarItem option = None
let mutable diagnosticsDisposable: Disposable option = None
let mutable sseDisposable: Disposable option = None
let mutable diagnosticCollection: DiagnosticCollection option = None
let mutable activeSessionId: string option = None
let mutable liveTestListener: LiveTest.LiveTestingListener option = None
let mutable testAdapter: TestCtrl.TestAdapter option = None
let mutable dashboardPanel: WebviewPanel option = None
let mutable typeExplorer: TypeExpl.TypeExplorer option = None
\\\

**Callback Registration Pattern**

Find where LiveTestingCallbacks is wired:
\\\sharp
let callbacks = {
  OnStateChange = fun changes -> ...render changes...
  OnSummaryUpdate = fun s -> testStatusBarItem |> Option.iter (updateText ...)
  OnStatusRefresh = fun () -> ...
  OnBindingsUpdate = fun arr -> ...
  OnTestTraceUpdate = fun obj -> ...
  OnFeatureEvent = Some {...}
  OnEvalResult = fun fp bsl out ms -> ...
  OnEvalStarted = fun fp bsl -> ...
  // ADD HERE:
  OnFailureNarratives = fun narratives -> 
    // Display narratives in test details panel or hover
}
\\\

### TestControllerAdapter.fs (Test Result Display, lines 1-148)

The adapter applies test results to VS Code's built-in Test Controller:

\\\sharp
let applyResults (results: VscTestResult array) =
  let run = getOrCreateRun ()
  for result in results do
    let id = VscTestId.value result.Id
    match testItemMap.TryGetValue(id) with
    | true, item ->
      let durationMs = result.DurationMs |> Option.defaultValue 0.0
      match result.Outcome with
      | VscTestOutcome.Passed ->
        run.passed(item, durationMs)
      | VscTestOutcome.Failed msg ->
        let message = newTestMessage msg
        run.failed(item, message, durationMs)          // <-- Failure message display
      | VscTestOutcome.Skipped _ ->
        run.skipped item
      | VscTestOutcome.Running ->
        run.started item
      | ... other outcomes
\\\

**For failure_narratives**: Enhance the Failed case to include narrative in the message:
\\\sharp
| VscTestOutcome.Failed msg ->
  let narrative = failureNarratives |> Array.tryFind (fun (id, _) -> id = result.Id)
  let enrichedMsg = match narrative with
    | Some (_, n) -> sprintf \"%s\\n\\nLast passed: %s\\nChanged: %s\" msg n.LastPassedAt (formatChanges n.CausalChanges)
    | None -> msg
  let message = newTestMessage enrichedMsg
  run.failed(item, message, durationMs)
\\\

---

## 3. VISUAL STUDIO EXTENSION (sagefs-vs/)

### LiveTestingParser.fs (lines 1-277)

**Parsing Helpers**

\\\sharp
namespace SageFs.VisualStudio.Core

[<RequireQualifiedAccess>]
module LiveTestingParser =
  let tryStr (el: JsonElement) (prop: string) (fb: string) =        // Line 9
    // Try-get string property with fallback
  
  let tryInt (el: JsonElement) (prop: string) (fb: int) =           // Line 13
    // Try-get int property with fallback
  
  let getProp (el: JsonElement) (prop: string) =                    // Line 17
    // Get optional property
  
  let parseTestId (el: JsonElement) =                               // Line 30
    // Extract TestId from DU-serialized element
  
  let parseTestResult (entry: JsonElement) =                        // Line 69
    // Parse {TestId, Status, DurationMs, Output}
  
  let parseSummary (root: JsonElement) =                            // Line 131
    // Parse {Total, Passed, Failed, Running, Stale, Disabled}
  
  let parseResultsBatch (root: JsonElement) : LiveTestEvent list =  // Line 153
    // Parse and emit TestsDiscovered + TestResultBatch events
  
  let parseFeatureSseEvent (eventType: string) (json: string) : FeatureEvent option = // Line 265
    // Parse feature events (eval_diff, cell_dependencies, etc.)
\\\

**For failure_narratives**, add:

\\\sharp
let parseFailureNarrative (el: JsonElement) : FailureNarrative =
  { TestId = parseTestId el
    LastPassedAt = tryStr el "LastPassedAt" ""
    TimeSinceLastPass = tryStr el "TimeSinceLastPass" ""
    CausalChanges = parseCausalChanges (getProp el "CausalChanges")
    PropertyViolation = tryStr el "PropertyViolation" ""
    Summary = tryStr el "Summary" "" }

let parseFailureNarratives (root: JsonElement) : FailureNarrative array =
  match getProp root \"<root>\" with
  | Some arr when arr.ValueKind = JsonValueKind.Array ->
    [| for e in arr.EnumerateArray() -> parseFailureNarrative e |]
  | _ -> [||]
\\\

### LiveTestingSubscriber.fs (lines 1-185)

**SSE Event Dispatch (lines 47-91)**

\\\sharp
let rec loop () = task {
  try
    let! resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, newCts.Token)
    use! stream = resp.Content.ReadAsStreamAsync(newCts.Token)
    use reader = new IO.StreamReader(stream)
    retryDelay <- 1000
    let mutable currentEvent = \"message\"
    while not (reader.EndOfStream || newCts.Token.IsCancellationRequested) do
      let! line = reader.ReadLineAsync(newCts.Token)
      if line <> null then
        if line.StartsWith(\"event: \") then
          currentEvent <- line.Substring(7).Trim()
        elif line.StartsWith(\"data: \") then
          let json = line.Substring(6)
          let events = LiveTestingParser.parseSseEvent currentEvent json      // <-- Main dispatch
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
          match LiveTestingParser.parseFeatureSseEvent currentEvent json with  // <-- Feature event dispatch
          | Some fe -> featureReceived.Trigger(fe)
          | None -> ()
          currentEvent <- \"message\"
\\\

**Events Published (lines 21-34)**

\\\sharp
[<CLIEvent>]
member _.StateChanged = stateChanged.Publish              // Main state updates

[<CLIEvent>]
member _.ChangeEmitted = changeEmitted.Publish            // Discrete changes (for UI adapters)

[<CLIEvent>]
member _.SummaryChanged = summaryChanged.Publish           // test_summary SSE event

[<CLIEvent>]
member _.FeatureReceived = featureReceived.Publish         // Feature events (eval_diff, etc.)
\\\

**For failure_narratives**, add a new event:

\\\sharp
let failureNarrativesReceived = Event<FailureNarrative array>()

[<CLIEvent>]
member _.FailureNarrativesReceived = failureNarrativesReceived.Publish

// Then in the SSE loop:
match LiveTestingParser.parseFeatureSseEvent currentEvent json with
| Some fe -> featureReceived.Trigger(fe)
| None -> 
  if currentEvent = \"failure_narratives\" then
    let narratives = LiveTestingParser.parseFailureNarratives json
    if not (Array.isEmpty narratives) then
      failureNarrativesReceived.Trigger(narratives)
\\\

### InlineFailureAdornment.cs (Inline Display Pattern, lines 1-252)

\\\csharp
// Adornment manager structure
internal sealed class InlineFailureAdornmentManager : IDisposable
{
    private readonly IWpfTextView _view;
    private readonly IAdornmentLayer _layer;
    private readonly FileAnnotationTracker _tracker;
    
    private void RenderAdornments()
    {
        _layer.RemoveAllAdornments();
        var filePath = TryGetFilePath();
        if (filePath == null) return;
        
        foreach (var line in _view.TextViewLines)
        {
            var lineNum = snapshot.GetLineNumberFromPosition(line.Start) + 1;
            var failures = _tracker.GetFailuresForLine(filePath, lineNum);
            if (failures.Count == 0) continue;
            
            var displayText = string.Join(\"  |  \", failures.Select(f => f.ToInlineText()));
            
            var block = new TextBlock
            {
                Text = displayText,
                Foreground = FailureBrush,
                FontFamily = fontFamily,
                FontSize = fontSize,
                Padding = new Thickness(8, 0, 4, 0),
            };
            
            Canvas.SetLeft(block, line.Right);
            Canvas.SetTop(block, top);
            
            _layer.AddAdornment(
                AdornmentPositioningBehavior.TextRelative,
                new SnapshotSpan(line.Start, 0),
                tag: null,
                adornment: block,
                removedCallback: null);
        }
    }
}
\\\

**For failure_narratives**, create FailureNarrativeAdornmentManager:

\\\csharp
internal sealed class FailureNarrativeAdornmentManager : IDisposable
{
    private readonly IWpfTextView _view;
    private readonly IAdornmentLayer _layer;
    private FailureNarrative[] _narratives = Array.Empty<FailureNarrative>();
    
    private void OnNarrativesUpdated(FailureNarrative[] narratives)
    {
        _narratives = narratives;
        Application.Current?.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            (Action)RenderNarratives);
    }
    
    private void RenderNarratives()
    {
        _layer.RemoveAllAdornments();
        var snapshot = _view.TextSnapshot;
        
        foreach (var line in _view.TextViewLines)
        {
            var lineNum = snapshot.GetLineNumberFromPosition(line.Start) + 1;
            
            // Find narratives for tests on this line
            var relevantNarratives = _narratives
                .Where(n => n.SourceLine == lineNum)
                .ToArray();
            
            if (relevantNarratives.Length == 0) continue;
            
            var narrative = relevantNarratives[0];
            var details = new StringBuilder();
            details.Append(\"Last: \").Append(narrative.LastPassedAt);
            details.Append(\" | Changed: \");
            details.Append(string.Join(\", \", narrative.CausalChanges.Select(c => c.Name)));
            
            var block = new TextBlock
            {
                Text = details.ToString(),
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xB3, 0xBA)),
                FontSize = 11,
                FontStyle = FontStyles.Italic,
            };
            
            _layer.AddAdornment(...);
        }
    }
}
\\\

### LiveTestingWindow.cs (Tool Window Structure, lines 1-55)

\\\csharp
namespace SageFs.VisualStudio.ToolWindows;

[VisualStudioContribution]
internal class LiveTestingWindow : ToolWindow
{
  private readonly Core.SageFsClient client;
  private readonly Core.LiveTestingSubscriber subscriber;
  private LiveTestingData? dataContext;

  public LiveTestingWindow(Core.SageFsClient client, Core.LiveTestingSubscriber subscriber)
  {
    this.client = client;
    this.subscriber = subscriber;
    this.Title = \"SageFs Live Testing\";
  }

  public override ToolWindowConfiguration ToolWindowConfiguration => new()
  {
    Placement = ToolWindowPlacement.DocumentWell,
  };

  public override Task InitializeAsync(CancellationToken ct)
  {
    dataContext = new LiveTestingData(Extensibility, client, subscriber);
    return Task.CompletedTask;
  }

  public override Task<IRemoteUserControl> GetContentAsync(CancellationToken ct)
  {
    return Task.FromResult<IRemoteUserControl>(new LiveTestingControl(dataContext));
  }
}
\\\

**LiveTestingData** is the data context that would be bound to the LiveTestingControl view.

**For failure_narratives**, subscribe to the subscriber event in LiveTestingData:

\\\csharp
subscriber.FailureNarrativesReceived += (narratives) =>
{
    // Update UI with failure narrative details
    // Could populate a detail pane, expand failure info in the tree, etc.
};
\\\

---

## 4. NEOVIM PLUGIN (C:\\Code\\Repos\\sagefs.nvim)

### sse.lua (Event Classification, lines 1-189)

**Type-to-Action Mapping (lines 85-143)**

\\\lua
function M.classify_event(event)
  if not event then return nil end

  local type_to_action = {
    -- Eval & session
    EvalCompleted = \"eval_completed\",
    SessionCreated = \"session_created\",
    SessionStopped = \"session_stopped\",
    DiagnosticsUpdated = \"diagnostics_updated\",
    state = \"state_update\",
    -- Testing cycle (PascalCase — internal Elm events)
    TestLocationsDetected = \"test_locations_detected\",
    TestsDiscovered = \"tests_discovered\",
    TestRunStarted = \"test_run_started\",
    TestRunCompleted = \"test_run_completed\",
    TestResultsBatch = \"test_results_batch\",
    LiveTestingEnabled = \"live_testing_enabled\",
    LiveTestingDisabled = \"live_testing_disabled\",
    AffectedTestsComputed = \"affected_tests_computed\",
    RunPolicyChanged = \"run_policy_changed\",
    ProvidersDetected = \"providers_detected\",
    TestCycleTimingRecorded = \"test_cycle_timing_recorded\",
    RunTestsRequested = \"run_tests_requested\",
    -- Testing cycle (snake_case — typed SSE events from SageFs)
    test_results_batch = \"test_results_batch\",
    test_summary = \"test_summary\",
    test_run_started = \"test_run_started\",
    test_run_completed = \"test_run_completed\",
    tests_discovered = \"tests_discovered\",
    live_testing_enabled = \"live_testing_enabled\",
    live_testing_disabled = \"live_testing_disabled\",
    providers_detected = \"providers_detected\",
    test_cycle_timing_recorded = \"test_cycle_timing_recorded\",
    -- Coverage
    CoverageUpdated = \"coverage_updated\",
    CoverageCleared = \"coverage_cleared\",
    -- File annotations (inline feedback: CodeLens, failures, coverage detail)
    file_annotations = \"file_annotations\",
    FileAnnotationsUpdated = \"file_annotations\",
    -- File watching
    HotReloadTriggered = \"hot_reload_triggered\",
    FileChanged = \"file_changed\",
    -- Session events (typed envelope from SageFs daemon)
    session = \"session_event\",
    -- CQRS: server-pushed bindings and test trace state
    bindings_snapshot = \"bindings_snapshot\",
    test_trace = \"test_trace\",
    -- Feature hooks (server-computed, push-only)
    eval_diff = \"eval_diff\",
    cell_dependencies = \"cell_dependencies\",
    binding_scope_map = \"binding_scope_map\",
    eval_timeline = \"eval_timeline\",
    -- Inline eval result decorations (Sprint 7+ daemon)
    eval_result = \"eval_result\",
  }

  local action = type_to_action[event.type] or \"unknown\"
  return { action = action, data = event.data }
end
\\\

**ADD TO type_to_action:**
\\\lua
failure_narratives = \"failure_narratives\",
\\\

### testing.lua (State Model, lines 1-800+)

**State Initialization (lines 71-90)**

\\\lua
function M.new()
  return {
    enabled = false,
    tests = {},      -- testId → {displayName, fullName, file, line, framework, category, policy, status, output}
    policies = {},   -- category → policy string
    summary = { total = 0, passed = 0, failed = 0, stale = 0, running = 0, disabled = 0 },
    locations = {},  -- file → [{testId, file, line}]
    providers = {},  -- [string]
    run_phase = \"Idle\",  -- \"Idle\" | \"Running\" | \"RunningButEdited\"
    generation = 0,      -- current RunGeneration int
    freshness = nil,     -- \"Fresh\" | \"StaleCodeEdited\" | \"StaleWrongGeneration\" | nil
    completion = nil,    -- \"Complete\" | \"Partial\" | \"Superseded\" | nil
    _file_index = {},    -- file → { testId → true } (O(1) file lookup, maintained incrementally)
    _version = 0,        -- mutation counter for render skip (FDA short-circuit / Nu ViewVersion)
  }
end
\\\

**Example Handler: handle_results_batch (lines 661-694)**

\\\lua
function M.handle_results_batch(state, data)
  if not data then return state end

  -- Receiving test results implies live testing is active
  state.enabled = true

  -- Enriched payload: Entries/entries (PascalCase or camelCase)
  local entries = data.Entries or data.entries
  if entries then
    for _, entry in ipairs(entries) do
      M.update_test(state, M.normalize_entry(entry))
    end
    local summary = data.Summary or data.summary
    if summary then
      state.summary = M.normalize_summary(summary)
    end
    state.generation = M.parse_generation(data.Generation or data.generation) or state.generation
    state.freshness = M.parse_freshness(data.Freshness or data.freshness)
    state.completion = M.parse_completion(data.Completion or data.completion)
    -- Bump version so schedule_render() version-skip check fires the render.
    state._version = state._version + 1
    return state
  end

  -- Legacy format: results array with {testId, status, output}
  if data.results then
    for _, r in ipairs(data.results) do
      M.update_result(state, r.testId, r.status, r.output)
    end
  end
  return state
end
\\\

**ADD TO testing.lua (around line 800+):**

\\\lua
--- Handle a failure_narratives SSE event
--- Stores rich failure context (when last passed, what changed) for display
---@param state table testing state
---@param data table {[testId]: {LastPassedAt, TimeSinceLastPass, CausalChanges[], PropertyViolation, Summary}}
---@return table state
function M.handle_failure_narratives(state, data)
  if not data then return state end
  
  -- Normalize PascalCase keys to camelCase
  state.failure_narratives = state.failure_narratives or {}
  
  -- If data is an array (from the SSE payload which is an array of objects),
  -- index by TestId for O(1) lookup
  if type(data) == \"table\" and data[1] then
    -- Array format: data is the direct array from SSE
    for _, narrative in ipairs(data) do
      if narrative.TestId then
        state.failure_narratives[narrative.TestId] = {
          testId = narrative.TestId,
          lastPassedAt = narrative.LastPassedAt,
          timeSinceLastPass = narrative.TimeSinceLastPass,
          causalChanges = narrative.CausalChanges or {},
          propertyViolation = narrative.PropertyViolation,
          summary = narrative.Summary,
        }
      end
    end
  else
    -- Map format: {testId → narrative}
    state.failure_narratives = data
  end
  
  state._version = state._version + 1
  return state
end

--- Get failure narrative for a specific test
---@param state table testing state
---@param testId string
---@return table|nil narrative
function M.get_failure_narrative(state, testId)
  if not state.failure_narratives then return nil end
  return state.failure_narratives[testId]
end

--- Format failure narrative for display
---@param narrative table
---@return string formatted text
function M.format_failure_narrative(narrative)
  if not narrative then return \"\" end
  
  local lines = {}
  if narrative.lastPassedAt then
    table.insert(lines, \"Last passed: \" .. narrative.lastPassedAt)
  end
  if narrative.timeSinceLastPass then
    table.insert(lines, \"Time since pass: \" .. narrative.timeSinceLastPass)
  end
  if narrative.causalChanges and #narrative.causalChanges > 0 then
    local changes = {}
    for _, change in ipairs(narrative.causalChanges) do
      table.insert(changes, string.format(\"%s: %s\", change.Kind, change.Name))
    end
    table.insert(lines, \"Changed: \" .. table.concat(changes, \", \"))
  end
  if narrative.propertyViolation then
    table.insert(lines, \"Violation: \" .. narrative.propertyViolation)
  end
  if narrative.summary then
    table.insert(lines, narrative.summary)
  end
  
  return table.concat(lines, \"\\n\")
end
\\\

### init.lua (Handler Wiring, lines 100-225)

**SSE_HANDLER_DEFS (Data-Driven Definition, lines 121-148)**

\\\lua
local SSE_HANDLER_DEFS = {
  -- Testing cycle (decode + state update ± session check ± event)
  { action = \"tests_discovered\", fn = \"handle_tests_discovered\", target = \"testing\" },
  { action = \"test_results_batch\", fn = \"handle_results_batch\", target = \"testing\", session_scoped = true, event = \"test_results_batch\" },
  { action = \"test_run_started\", fn = \"handle_test_run_started\", target = \"testing\", session_scoped = true, event = \"test_run_started\" },
  { action = \"test_run_completed\", fn = \"handle_test_run_completed\", target = \"testing\", session_scoped = true, event = \"test_run_completed\" },
  { action = \"run_policy_changed\", fn = \"handle_run_policy_changed\", target = \"testing\" },
  { action = \"test_locations_detected\", fn = \"handle_test_locations\", target = \"testing\" },
  { action = \"providers_detected\", fn = \"handle_providers_detected\", target = \"testing\", event = \"providers_detected\" },
  { action = \"test_summary\", fn = \"handle_test_summary\", target = \"testing\", session_scoped = true, event = \"test_summary\" },
  -- Coverage
  { action = \"coverage_updated\", fn = \"apply_coverage_response\", target = \"coverage\", event = \"coverage_updated\" },
  -- Annotations
  { action = \"file_annotations\", fn = \"handle_file_annotations\", target = \"annotations\", session_scoped = true, event = \"file_annotations\" },
  -- Fire-event-only (decode + fire, no state update)
  { action = \"affected_tests_computed\", event = \"affected_tests_computed\" },
  { action = \"test_cycle_timing_recorded\", event = \"test_cycle_timing_recorded\" },
  { action = \"run_tests_requested\", event = \"run_tests_requested\" },
  { action = \"eval_completed\", event = \"eval_completed\" },
  { action = \"hot_reload_triggered\", event = \"hot_reload_triggered\" },
  -- Feature hooks (server-computed, push-only)
  { action = \"eval_diff\", event = \"eval_diff\" },
  { action = \"cell_dependencies\", event = \"cell_dependencies\" },
  { action = \"binding_scope_map\", event = \"binding_scope_map\" },
  { action = \"eval_timeline\", event = \"eval_timeline\" },
  -- Inline eval result decorations — fire event so plugins can display ghost text
  { action = \"eval_result\", event = \"eval_result\" },
}
\\\

**ADD TO SSE_HANDLER_DEFS:**
\\\lua
{ action = \"failure_narratives\", fn = \"handle_failure_narratives\", target = \"testing\", session_scoped = true, event = \"failure_narratives\" },
\\\

**Dispatch Handler Builder (lines 157-225)**

\\\lua
local function build_handlers()
  local handlers = {}

  -- Validate definitions at build time (Wlaschin: cheap defense in depth)
  format.validate_handler_defs(SSE_HANDLER_DEFS)

  -- Generate handlers from data-driven definitions
  for _, def in ipairs(SSE_HANDLER_DEFS) do
    handlers[def.action] = function(raw)
      local data = decode_event_data(raw)
      if not data then return end
      if def.session_scoped and not session_matches(data) then return end
      if def.fn and def.target then
        local t = TARGET_MAP[def.target]
        M[t.key] = t.mod()[def.fn](M[t.key], data)
      end
      if def.event then fire_user_event(def.event, data) end
    end
  end

  -- Custom handlers that don't fit the pattern
  handlers.state_update = function(_raw)
    M.state = model.set_status(M.state, \"connected\")
  end
  handlers.live_testing_enabled = function(raw)
    local data = decode_event_data(raw)
    if data then M.testing_state = testing.set_enabled(M.testing_state, true) end
  end
  handlers.live_testing_disabled = function(raw)
    local data = decode_event_data(raw)
    if data then M.testing_state = testing.set_enabled(M.testing_state, false) end
  end
  -- ... other custom handlers
  
  return sse_parser.build_dispatch_table(handlers)
end
\\\

### commands.lua (UI Keymaps, lines 1-400+)

**Example: Test Panel with Detail Display (lines 347-600+)**

The test panel shows tests grouped by file with filtering. The pattern for adding a detail/info keymap:

\\\lua
  -- Persistent Test Panel Implementation
  local test_panel_buf = nil
  local test_panel_win = nil
  local test_panel_entries = {}
  
  -- ... entry formatting and rendering functions ...
  
  -- Keymap for showing test details (including failure narratives)
  vim.api.nvim_buf_set_keymap(test_panel_buf, \"n\", \"<CR>\", \":\", {
    callback = function()
      local lnum = vim.api.nvim_win_get_cursor(test_panel_win)[1]
      local entry = test_panel_entries[lnum]
      if entry and entry.testId then
        local narrative = testing.get_failure_narrative(plugin.testing_state, entry.testId)
        local lines = { \"═══ Test Details ═══\", \"\" }
        table.insert(lines, \"Test: \" .. entry.displayName)
        table.insert(lines, \"Status: \" .. (entry.status or \"Unknown\"))
        if narrative then
          table.insert(lines, \"\")
          table.insert(lines, \"═══ Failure Narrative ═══\")
          for line in testing.format_failure_narrative(narrative):gmatch(\"([^\\n]+)\") do
            table.insert(lines, line)
          end
        end
        render.show_float(lines, { title = \"Test Details\" })
      end
    end,
    noremap = true,
  })
\\\

### Floating Window Patterns

**Example from commands.lua (lines 86-104):**

\\\lua
vim.api.nvim_open_win(buf, true, {
  relative = \"editor\",
  width = width,
  height = #lines,
  row = math.floor((vim.o.lines - #lines) / 2),
  col = math.floor((vim.o.columns - width) / 2),
  style = \"minimal\",
  border = \"rounded\",
})
vim.keymap.set(\"n\", \"q\", \"<cmd>close<CR>\", { buffer = buf, silent = true })
\\\

**For failure_narratives floating window:**

\\\lua
local function show_failure_narrative(testId)
  local narrative = testing.get_failure_narrative(M.testing_state, testId)
  if not narrative then
    vim.notify(\"No failure narrative available\", vim.log.levels.WARN)
    return
  end
  
  local lines = { \"═══ Failure Narrative ═══\", \"\" }
  local text = testing.format_failure_narrative(narrative)
  for line in text:gmatch(\"([^\\n]+)\") do
    table.insert(lines, line)
  end
  
  render.show_float(lines, {
    title = narrative.testId or \"Failure Details\",
    width = 60,
    height = #lines + 2,
  })
end
\\\

---

## INTEGRATION CHECKLIST

### Daemon (SageFs.Core/SseWriter.fs)
- [x] ✓ formatFailureNarrativesEvent exists (lines 73-87)
- [x] ✓ Payload shape: {TestId, LastPassedAt, TimeSinceLastPass, CausalChanges[], PropertyViolation, Summary}
- [x] ✓ injectSessionId applied for multi-session support

### VS Code Extension
- [ ] Parse failure_narratives in LiveTestingListener.fs (add to processEvent match)
- [ ] Add OnFailureNarratives callback to LiveTestingCallbacks type
- [ ] Wire callback in Extension.fs
- [ ] Display narratives in TestControllerAdapter.Failed message or hover

### Visual Studio Extension  
- [ ] parseFailureNarrative helper in LiveTestingParser.fs
- [ ] Add failureNarrativesReceived event in LiveTestingSubscriber.fs
- [ ] Create FailureNarrativeAdornmentManager (similar to InlineFailureAdornment.cs)
- [ ] Subscribe in LiveTestingData/LiveTestingControl

### Neovim Plugin
- [ ] Add failure_narratives to type_to_action in sse.lua (line 88+)
- [ ] Add handle_failure_narratives to testing.lua (around line 800+)
- [ ] Add get_failure_narrative + format_failure_narrative to testing.lua
- [ ] Add SSE_HANDLER_DEFS entry in init.lua (line 121+)
- [ ] Add show_failure_narrative command in commands.lua (test panel detail keymap)

