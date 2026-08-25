module SageFs.Vscode.LiveTestingListener

open Fable.Core.JsInterop
open Vscode

open SageFs.Vscode.LiveTestingTypes
open SageFs.Vscode.JsHelpers
open SageFs.Vscode.FeatureTypes
open SageFs.Vscode.SafeInterop

// ── Server JSON → VscLiveTestEvent mappers ───────────────────

/// Extract DU Case string from a Fable-serialized DU object
let parseDuCase (du: obj) : string option =
  tryOfObj du
  |> Option.bind (fun du ->
    duCase du)

/// Extract the first string field from a Fable-serialized DU's Fields array
let duFirstFieldStr (du: obj) : string option =
  tryOfObj du
  |> Option.bind duFirstFieldString

/// Extract DU Fields array from a Fable-serialized DU
let duFieldsArr (du: obj) : obj array option =
  tryOfObj du
  |> Option.bind duFieldsArray

/// Parse HH:MM:SS duration string to milliseconds
let parseDuration (dur: string) : float option =
  tryOfObj dur
  |> Option.bind (fun dur ->
    let parts = dur.Split(':')
    match parts.Length with
    | 3 ->
      let h = float parts.[0]
      let m = float parts.[1]
      let s = float parts.[2]
      Some ((h * 3600.0 + m * 60.0 + s) * 1000.0)
    | _ -> None)

/// Extract TestId string from a server TestId DU object
let parseTestId (testIdObj: obj) : string =
  tryOfObj testIdObj
  |> Option.bind duFirstFieldString
  |> Option.defaultValue (
    tryOfObj testIdObj
    |> Option.map string
    |> Option.defaultValue "")

let parseSelectionPrecision (value: string) =
  match value with
  | "exact_dependency_match" -> Some VscSelectionPrecision.ExactDependencyMatch
  | "coverage_approximation" -> Some VscSelectionPrecision.CoverageApproximation
  | "conservative_fallback" -> Some VscSelectionPrecision.ConservativeFallback
  | "no_impacted_tests" -> Some VscSelectionPrecision.NoImpactedTests
  | "suppressed_by_policy" -> Some VscSelectionPrecision.SuppressedByPolicy
  | _ -> None

let parseFreshnessTrust (value: string) =
  match value with
  | "fresh_exact" -> Some VscFreshnessTrust.FreshExact
  | "fresh_approximate" -> Some VscFreshnessTrust.FreshApproximate
  | "stale_awaiting_rerun" -> Some VscFreshnessTrust.StaleAwaitingRerun
  | "suppressed" -> Some VscFreshnessTrust.Suppressed
  | _ -> None

let parseRerunCause (value: string) =
  match value with
  | "keystroke_buffered" -> Some VscRerunCause.KeystrokeBuffered
  | "file_saved" -> Some VscRerunCause.FileSaved
  | "explicit_run_requested" -> Some VscRerunCause.ExplicitRunRequested
  | _ -> None

let parseStringArrayField (fieldName: string) (data: obj) =
  fieldArray fieldName data
  |> Option.defaultValue [||]
  |> Array.choose tryCastString

let parseLastDecision (data: obj) : VscLiveTestingDecision option =
  let precision =
    fieldString "Precision" data
    |> Option.bind parseSelectionPrecision
  let trust =
    fieldString "Trust" data
    |> Option.bind parseFreshnessTrust
  let cause =
    fieldString "Cause" data
    |> Option.bind parseRerunCause
  match precision, trust, cause with
  | Some precision, Some trust, Some cause ->
    Some {
      Cause = cause
      FilePath = fieldString "FilePath" data |> Option.defaultValue ""
      Precision = precision
      Trust = trust
      ChangedSymbols = parseStringArrayField "ChangedSymbols" data
      SelectedTests = parseStringArrayField "SelectedTests" data
      DeferredTests = parseStringArrayField "DeferredTests" data
      Reason = fieldString "Reason" data |> Option.defaultValue "" }
  | _ -> None

/// Map server TestSummary JSON to VscTestSummary
let parseSummary (data: obj) : VscTestSummary =
  { Total = fieldInt "Total" data |> Option.defaultValue 0
    Passed = fieldInt "Passed" data |> Option.defaultValue 0
    Failed = fieldInt "Failed" data |> Option.defaultValue 0
    Running = fieldInt "Running" data |> Option.defaultValue 0
    Stale = fieldInt "Stale" data |> Option.defaultValue 0
    Disabled = fieldInt "Disabled" data |> Option.defaultValue 0
    LastDecision = fieldObj "LastDecision" data |> Option.bind parseLastDecision }

/// Map a server TestStatusEntry to VscTestResult
let parseTestResult (entry: obj) : VscTestResult =
  let id = fieldObj "TestId" entry |> Option.map parseTestId |> Option.defaultValue "" |> VscTestId.create
  let status = fieldObj "Status" entry |> Option.defaultValue (obj())
  let statusCase = parseDuCase status |> Option.defaultValue "Detected"
  let fields = duFieldsArr status
  let outcome =
    match statusCase with
    | "Passed" -> VscTestOutcome.Passed
    | "Failed" ->
      let msg =
        fields
        |> Option.bind (fun f ->
          match f.Length with
          | 0 -> None
          | _ -> Some f.[0])
        |> Option.bind duFirstFieldString
        |> Option.defaultValue "test failed"
      VscTestOutcome.Failed msg
    | "Skipped" ->
      let reason = fields |> Option.bind Array.tryHead |> Option.bind tryCastString |> Option.defaultValue "skipped"
      VscTestOutcome.Skipped reason
    | "Running" -> VscTestOutcome.Running
    | "Stale" -> VscTestOutcome.Stale
    | "PolicyDisabled" -> VscTestOutcome.PolicyDisabled
    | _ -> VscTestOutcome.Skipped "unknown status"
  let durationMs =
    match statusCase, fields with
    | "Passed", Some f ->
      f |> Array.tryHead |> Option.bind tryCastString |> Option.bind parseDuration
    | "Failed", Some f when f.Length >= 2 ->
      f |> Array.tryItem 1 |> Option.bind tryCastString |> Option.bind parseDuration
    | _ -> None
  { Id = id; Outcome = outcome; DurationMs = durationMs; Output = None }

/// Map a server TestStatusEntry to VscTestInfo
let parseTestInfo (entry: obj) : VscTestInfo =
  let testIdStr = fieldObj "TestId" entry |> Option.map parseTestId |> Option.defaultValue ""
  let origin = fieldObj "Origin" entry |> Option.defaultValue (obj())
  let filePath, line =
    match parseDuCase origin with
    | Some "SourceMapped" ->
      let fields = duFieldsArr origin |> Option.defaultValue [||]
      match fields.Length >= 2 with
      | true ->
        let fp = fields |> Array.tryItem 0 |> Option.bind tryCastString
        let ln = fields |> Array.tryItem 1 |> Option.bind tryCastInt
        fp, ln
      | false -> None, None
    | _ -> None, None
  { Id = VscTestId.create testIdStr
    DisplayName = fieldString "DisplayName" entry |> Option.defaultValue ""
    FullName = fieldString "FullName" entry |> Option.defaultValue ""
    FilePath = filePath
    Line = line }

/// Parse Freshness DU from server JSON (Case/Fields or plain string)
let parseFreshness (data: obj) : VscResultFreshness =
  match fieldObj "Freshness" data |> Option.bind parseDuCase with
  | Some "StaleCodeEdited" -> VscResultFreshness.StaleCodeEdited
  | Some "StaleWrongGeneration" -> VscResultFreshness.StaleWrongGeneration
  | _ -> VscResultFreshness.Fresh

/// Parse test_results_batch → VscLiveTestEvent pair (discovery + results)
let parseResultsBatch (data: obj) : VscLiveTestEvent list =
  fieldObj "Entries" data
  |> Option.bind tryOfObj
  |> Option.map (fun entries ->
    let freshness = parseFreshness data
    let entryArray = tryCastArray entries |> Option.defaultValue [||]
    let testInfos = entryArray |> Array.map parseTestInfo
    let testResults = entryArray |> Array.map parseTestResult
    [ VscLiveTestEvent.TestsDiscovered testInfos
      VscLiveTestEvent.TestResultBatch (testResults, freshness) ])
  |> Option.defaultValue []

/// Parse a single CausalChange object from server JSON
let parseCausalChange (obj: obj) : VscCausalChange =
  { Kind = fieldString "Kind" obj |> Option.orElse (fieldString "kind" obj) |> Option.defaultValue ""
    Name = fieldString "Name" obj |> Option.orElse (fieldString "name" obj) |> Option.defaultValue "" }

/// Parse a single FailureNarrative from server JSON
let parseFailureNarrative (obj: obj) : VscFailureNarrative =
  let testId =
    fieldString "TestId" obj
    |> Option.orElse (fieldString "testId" obj)
    |> Option.defaultValue ""
  let changes =
    fieldArray "CausalChanges" obj
    |> Option.orElse (fieldArray "causalChanges" obj)
    |> Option.defaultValue [||]
    |> Array.map parseCausalChange
  { TestId = testId
    Summary = fieldString "Summary" obj |> Option.orElse (fieldString "summary" obj) |> Option.defaultValue ""
    TimeSinceLastPass = fieldString "TimeSinceLastPass" obj |> Option.orElse (fieldString "timeSinceLastPass" obj) |> Option.defaultValue ""
    CausalChanges = changes }

/// Parse failure_narratives SSE event (JSON array of narratives)
let parseFailureNarratives (data: obj) : VscFailureNarrative array =
  tryCastArray data
  |> Option.defaultValue [||]
  |> Array.map parseFailureNarrative

// ── Listener lifecycle ───────────────────────────────────────

type LiveTestingCallbacks = {
  OnStateChange: VscStateChange list -> unit
  OnSummaryUpdate: VscTestSummary -> unit
  OnStatusRefresh: unit -> unit
  OnBindingsUpdate: obj array -> unit
  OnTestTraceUpdate: obj -> unit
  OnFeatureEvent: FeatureCallbacks option
  OnEvalResult: string -> int -> string -> float -> unit
  OnEvalStarted: string -> int -> unit
  OnEvalHeartbeat: string -> int -> int64 -> unit
  OnSourceLocationsUpdate: obj array -> unit
  OnFileAnnotations: obj -> unit
  OnFailureNarratives: VscFailureNarrative array -> unit
  OnWarmupProgress: int -> int -> string -> float -> string -> unit
  OnWarmupCompleted: string -> unit
  OnFileReloaded: string -> unit
  OnSessionFaulted: string -> unit
  OnDomainModel: obj -> unit
  OnDiagnosisReady: obj -> unit
  OnBindingValuesUpdate: int -> ClientBindingValue list -> unit
  OnWorkflowChanged: string -> unit
}

type LiveTestingListener = {
  State: unit -> VscLiveTestState
  Summary: unit -> VscTestSummary
  Bindings: unit -> obj array
  TestTrace: unit -> obj option
  EvalDiff: unit -> VscEvalDiff option
  CellGraph: unit -> VscCellGraph option
  BindingScope: unit -> VscBindingScopeSnapshot option
  Timeline: unit -> VscTimelineStats option
  /// Update the session filter — only events tagged with this session ID will be processed.
  /// Pass None to disable filtering (accept all sessions, e.g. before first warmup).
  SetSessionFilter: string option -> unit
  Dispose: unit -> unit
}

let start (port: int) (callbacks: LiveTestingCallbacks) (onReconnect: (unit -> unit) option) (onDisconnect: (unit -> unit) option) (log: (string -> unit) option) : LiveTestingListener =
  let mutable state = VscLiveTestState.empty
  let mutable bindings: obj array = [||]
  let mutable TestTrace: obj option = None
  let mutable evalDiff: VscEvalDiff option = None
  let mutable cellGraph: VscCellGraph option = None
  let mutable bindingScope: VscBindingScopeSnapshot option = None
  let mutable timeline: VscTimelineStats option = None
  // Track last known (filePath, blockStartLine) from eval_result so bindings_snapshot
  // can fall back to it when the server doesn't yet emit blockStartLine in the snapshot.
  let mutable lastKnownBsl: (string * int) option = None
  // Session filter: only process events tagged with this session ID.
  // None = no filter (pass all events — used before first warmup completes).
  let mutable sessionFilter: string option = None
  let url = sprintf "http://localhost:%d/events" port

  let featureCallbacks =
    { OnEvalDiff = fun d -> evalDiff <- Some d
      OnCellGraph = fun g -> cellGraph <- Some g
      OnBindingScope = fun s -> bindingScope <- Some s
      OnTimeline = fun t -> timeline <- Some t }

  /// Session-scoped events carry a SessionId field injected by the server.
  /// When a filter is set, skip events whose SessionId doesn't match.
  let passesSessionFilter (data: obj) =
    match sessionFilter with
    | None -> true
    | Some expected ->
      match fieldString "SessionId" data with
      | Some sid -> sid = expected
      | None -> true  // No SessionId in event — backward compat: always pass through

  // Events that are NOT session-scoped — always process regardless of sessionFilter.
  let isSessionAgnosticEvent (eventType: string) =
    match eventType with
    | "state" | "session" | "domain_model" | "warmup_completed" -> true
    | _ -> false

  let processEvent (eventType: string) (data: obj) =
    tryHandleEvent eventType (fun () ->
      // Skip session-scoped events that don't match the active session filter.
      if not (isSessionAgnosticEvent eventType) && not (passesSessionFilter data) then () else

      match eventType with
      | "test_summary" ->
        let summary = parseSummary data
        callbacks.OnSummaryUpdate summary
      | "test_results_batch" ->
        let events = parseResultsBatch data
        let mutable allChanges = []
        for evt in events do
          let newState, changes = VscLiveTestState.update evt state
          state <- newState
          allChanges <- allChanges @ changes
        if not allChanges.IsEmpty then
          callbacks.OnStateChange allChanges
      | "state" ->
        callbacks.OnStatusRefresh ()
      | "session" ->
        // Auto-detect the active session from warmup/activation events.
        // The server injects sessionId (lowercase) inside "session" type events.
        let subtype = fieldString "type" data |> Option.defaultValue ""
        match subtype with
        | "warmup_context_snapshot" | "session_activated" ->
          match fieldString "sessionId" data with
          | Some sid when sid <> "" ->
            sessionFilter <- Some sid
          | _ -> ()
        | "workflow_switched" ->
          match fieldString "workflowLabel" data with
          | Some label -> callbacks.OnWorkflowChanged label
          | None -> ()
        | _ -> ()
      | "bindings_snapshot" ->
        fieldArray "Bindings" data
        |> Option.iter (fun arr ->
          bindings <- arr
          callbacks.OnBindingsUpdate bindings)
        let bslFromEvent = fieldInt "blockStartLine" data |> Option.defaultValue 0
        // Fall back to last eval_result's blockStartLine if the server doesn't include
        // one in the snapshot (pre-Phase-2 server compatibility).
        let bsl =
          match bslFromEvent > 0 with
          | true  -> bslFromEvent
          | false -> lastKnownBsl |> Option.map snd |> Option.defaultValue 0
        let bindingValues =
          fieldArray "BindingValues" data
          |> Option.map (Array.choose parseClientBindingValue >> Array.toList)
          |> Option.defaultValue []
        callbacks.OnBindingValuesUpdate bsl bindingValues
      | "test_trace" ->
        TestTrace <- Some data
        callbacks.OnTestTraceUpdate data
      | "eval_diff"
      | "cell_dependencies"
      | "binding_scope_map"
      | "eval_timeline" ->
        let merged =
          match callbacks.OnFeatureEvent with
          | Some custom ->
            { OnEvalDiff = fun d -> featureCallbacks.OnEvalDiff d; custom.OnEvalDiff d
              OnCellGraph = fun g -> featureCallbacks.OnCellGraph g; custom.OnCellGraph g
              OnBindingScope = fun s -> featureCallbacks.OnBindingScope s; custom.OnBindingScope s
              OnTimeline = fun t -> featureCallbacks.OnTimeline t; custom.OnTimeline t }
          | None -> featureCallbacks
        processFeatureEvent eventType data merged
      | "eval_started" ->
        let filePath = fieldString "filePath" data |> Option.defaultValue ""
        let bsl = fieldInt "blockStartLine" data |> Option.defaultValue 0
        match filePath with
        | "" -> ()
        | fp -> callbacks.OnEvalStarted fp bsl
      | "eval_heartbeat" ->
        let filePath = fieldString "FilePath" data |> Option.defaultValue ""
        let bsl = fieldInt "BlockStartLine" data |> Option.defaultValue 0
        let elapsedMs = fieldFloat "ElapsedMs" data |> Option.map int64 |> Option.defaultValue 0L
        callbacks.OnEvalHeartbeat filePath bsl elapsedMs
      | "eval_result" ->
        let filePath = fieldString "filePath" data |> Option.defaultValue ""
        let bsl = fieldInt "blockStartLine" data |> Option.defaultValue 0
        let output = fieldString "output" data |> Option.defaultValue ""
        let durationMs = fieldFloat "durationMs" data |> Option.defaultValue 0.0
        match filePath with
        | "" -> ()
        | fp ->
          if bsl > 0 then lastKnownBsl <- Some (fp, bsl)
          callbacks.OnEvalResult fp bsl output durationMs
      | "test_source_locations" ->
        let locations = fieldArray "Locations" data |> Option.defaultValue [||]
        callbacks.OnSourceLocationsUpdate locations
      | "file_annotations" ->
        callbacks.OnFileAnnotations data
      | "failure_narratives" ->
        let narratives = parseFailureNarratives data
        let narrativeMap =
          narratives |> Array.fold (fun m n -> Map.add n.TestId n m) state.FailureNarratives
        state <- { state with FailureNarratives = narrativeMap }
        callbacks.OnFailureNarratives narratives
      | "warmup_progress" ->
        let step = fieldInt "Step" data |> Option.defaultValue 0
        let total = fieldInt "Total" data |> Option.defaultValue 0
        let message = fieldString "Message" data |> Option.defaultValue ""
        let progress = fieldFloat "Progress" data |> Option.defaultValue 0.0
        let phase = fieldString "Phase" data |> Option.defaultValue ""
        callbacks.OnWarmupProgress step total message progress phase
      | "warmup_completed" ->
        let projectName = fieldString "ProjectName" data |> Option.defaultValue "project"
        callbacks.OnWarmupCompleted projectName
      | "file_reloaded" ->
        let filePath = fieldString "FilePath" data |> Option.orElse (fieldString "filePath" data) |> Option.defaultValue ""
        callbacks.OnFileReloaded filePath
      | "session_faulted" ->
        let reason = fieldString "Reason" data |> Option.orElse (fieldString "reason" data) |> Option.defaultValue "unknown error"
        callbacks.OnSessionFaulted reason
      | "domain_model" ->
        callbacks.OnDomainModel data
      | "diagnosis_ready" ->
        callbacks.OnDiagnosisReady data
      | "live_bindings" ->
        // Live bound-value watch window — intentionally a no-op for now;
        // the dashboard consumes this event. Handled explicitly for parity.
        ()
      | _ ->
        ())

  let disconnectFn =
    match onDisconnect with
    | Some fn -> fun () -> fn ()
    | None -> fun () -> ()

  let disposable =
    match onReconnect, log with
    | Some reconnectFn, Some logger ->
      subscribeTypedSseWithReconnect url processEvent (fun () ->
        state <- VscLiveTestState.empty
        sessionFilter <- None  // Re-open filter; will be re-set by next warmup_context_snapshot
        reconnectFn ()
      ) disconnectFn logger
    | Some reconnectFn, None ->
      subscribeTypedSseWithReconnect url processEvent (fun () ->
        state <- VscLiveTestState.empty
        sessionFilter <- None  // Re-open filter; will be re-set by next warmup_context_snapshot
        reconnectFn ()
      ) disconnectFn (fun msg -> try printfn "[SageFs SSE] %s" msg with _ -> ())
    | _ -> subscribeTypedSse url processEvent

  { State = fun () -> state
    Summary = fun () -> VscLiveTestState.summary state
    Bindings = fun () -> bindings
    TestTrace = fun () -> TestTrace
    EvalDiff = fun () -> evalDiff
    CellGraph = fun () -> cellGraph
    BindingScope = fun () -> bindingScope
    Timeline = fun () -> timeline
    SetSessionFilter = fun sid -> sessionFilter <- sid
    Dispose = fun () -> disposable.dispose () |> ignore }
