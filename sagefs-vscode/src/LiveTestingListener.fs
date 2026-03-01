module SageFs.Vscode.LiveTestingListener

open Fable.Core.JsInterop
open Vscode

open SageFs.Vscode.LiveTestingTypes
open SageFs.Vscode.JsHelpers
open SageFs.Vscode.FeatureTypes

// ── Server JSON → VscLiveTestEvent mappers ───────────────────

/// Extract DU Case string from a Fable-serialized DU object
let parseDuCase (du: obj) : string option =
  tryOfObj du
  |> Option.map (fun du ->
    du?Case
    |> tryOfObj
    |> Option.map unbox<string>
    |> Option.defaultValue (string du))

/// Extract the first field from a Fable-serialized DU's Fields array
let duFirstField<'T> (du: obj) : 'T option =
  tryOfObj du
  |> Option.bind (fun du ->
    tryOfObj du?Fields
    |> Option.bind (fun fields ->
      let arr = unbox<obj array> fields
      match arr.Length with 0 -> None | _ -> Some (unbox<'T> arr.[0])))

/// Extract DU Fields array from a Fable-serialized DU
let duFields (du: obj) : obj array option =
  tryOfObj du
  |> Option.bind (fun du ->
    tryOfObj du?Fields
    |> Option.map unbox<obj array>)

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
  |> Option.bind (duFirstField<string>)
  |> Option.defaultValue (
    tryOfObj testIdObj
    |> Option.map string
    |> Option.defaultValue "")

/// Map server TestSummary JSON to VscTestSummary
let parseSummary (data: obj) : VscTestSummary =
  { Total = tryField<int> "Total" data |> Option.defaultValue 0
    Passed = tryField<int> "Passed" data |> Option.defaultValue 0
    Failed = tryField<int> "Failed" data |> Option.defaultValue 0
    Running = tryField<int> "Running" data |> Option.defaultValue 0
    Stale = tryField<int> "Stale" data |> Option.defaultValue 0
    Disabled = tryField<int> "Disabled" data |> Option.defaultValue 0 }

/// Map a server TestStatusEntry to VscTestResult
let parseTestResult (entry: obj) : VscTestResult =
  let id = tryField<obj> "TestId" entry |> Option.map parseTestId |> Option.defaultValue "" |> VscTestId.create
  let status = tryField<obj> "Status" entry |> Option.defaultValue (obj())
  let statusCase = parseDuCase status |> Option.defaultValue "Detected"
  let fields = duFields status
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
        |> Option.bind (fun failObj -> duFirstField<string> failObj)
        |> Option.defaultValue "test failed"
      VscTestOutcome.Failed msg
    | "Skipped" ->
      let reason = fields |> Option.bind Array.tryHead |> Option.bind (fun x -> tryOfObj (unbox<string> x)) |> Option.defaultValue "skipped"
      VscTestOutcome.Skipped reason
    | "Running" -> VscTestOutcome.Running
    | "Stale" -> VscTestOutcome.Stale
    | "PolicyDisabled" -> VscTestOutcome.PolicyDisabled
    | _ -> VscTestOutcome.Skipped "unknown status"
  let durationMs =
    match statusCase, fields with
    | "Passed", Some f ->
      f |> Array.tryHead |> Option.bind (fun x -> tryOfObj (unbox<string> x)) |> Option.bind parseDuration
    | "Failed", Some f when f.Length >= 2 ->
      f |> Array.tryItem 1 |> Option.bind (fun x -> tryOfObj (unbox<string> x)) |> Option.bind parseDuration
    | _ -> None
  { Id = id; Outcome = outcome; DurationMs = durationMs; Output = None }

/// Map a server TestStatusEntry to VscTestInfo
let parseTestInfo (entry: obj) : VscTestInfo =
  let testIdStr = tryField<obj> "TestId" entry |> Option.map parseTestId |> Option.defaultValue ""
  let origin = tryField<obj> "Origin" entry |> Option.defaultValue (obj())
  let filePath, line =
    match parseDuCase origin with
    | Some "SourceMapped" ->
      let fields = duFields origin |> Option.defaultValue [||]
      match fields.Length >= 2 with
      | true ->
        let fp = fields |> Array.tryItem 0 |> Option.bind (fun x -> tryOfObj (unbox<string> x))
        let ln = fields |> Array.tryItem 1 |> Option.bind (fun x -> tryOfObj (unbox<int> x))
        fp, ln
      | false -> None, None
    | _ -> None, None
  { Id = VscTestId.create testIdStr
    DisplayName = tryField<string> "DisplayName" entry |> Option.defaultValue ""
    FullName = tryField<string> "FullName" entry |> Option.defaultValue ""
    FilePath = filePath
    Line = line }

/// Parse Freshness DU from server JSON (Case/Fields or plain string)
let parseFreshness (data: obj) : VscResultFreshness =
  match tryField<obj> "Freshness" data |> Option.bind parseDuCase with
  | Some "StaleCodeEdited" -> VscResultFreshness.StaleCodeEdited
  | Some "StaleWrongGeneration" -> VscResultFreshness.StaleWrongGeneration
  | _ -> VscResultFreshness.Fresh

/// Parse test_results_batch → VscLiveTestEvent pair (discovery + results)
let parseResultsBatch (data: obj) : VscLiveTestEvent list =
  tryField<obj> "Entries" data
  |> Option.bind tryOfObj
  |> Option.map (fun entries ->
    let freshness = parseFreshness data
    let entryArray = tryOfObj entries |> Option.map unbox<obj array> |> Option.defaultValue [||]
    let testInfos = entryArray |> Array.map parseTestInfo
    let testResults = entryArray |> Array.map parseTestResult
    [ VscLiveTestEvent.TestsDiscovered testInfos
      VscLiveTestEvent.TestResultBatch (testResults, freshness) ])
  |> Option.defaultValue []

// ── Listener lifecycle ───────────────────────────────────────

type LiveTestingCallbacks = {
  OnStateChange: VscStateChange list -> unit
  OnSummaryUpdate: VscTestSummary -> unit
  OnStatusRefresh: unit -> unit
  OnBindingsUpdate: obj array -> unit
  OnPipelineTraceUpdate: obj -> unit
  OnFeatureEvent: FeatureCallbacks option
}

type LiveTestingListener = {
  State: unit -> VscLiveTestState
  Summary: unit -> VscTestSummary
  Bindings: unit -> obj array
  PipelineTrace: unit -> obj option
  EvalDiff: unit -> VscEvalDiff option
  CellGraph: unit -> VscCellGraph option
  BindingScope: unit -> VscBindingScopeSnapshot option
  Timeline: unit -> VscTimelineStats option
  Dispose: unit -> unit
}

let start (port: int) (callbacks: LiveTestingCallbacks) : LiveTestingListener =
  let mutable state = VscLiveTestState.empty
  let mutable bindings: obj array = [||]
  let mutable pipelineTrace: obj option = None
  let mutable evalDiff: VscEvalDiff option = None
  let mutable cellGraph: VscCellGraph option = None
  let mutable bindingScope: VscBindingScopeSnapshot option = None
  let mutable timeline: VscTimelineStats option = None
  let url = sprintf "http://localhost:%d/events" port

  let featureCallbacks =
    { OnEvalDiff = fun d -> evalDiff <- Some d
      OnCellGraph = fun g -> cellGraph <- Some g
      OnBindingScope = fun s -> bindingScope <- Some s
      OnTimeline = fun t -> timeline <- Some t }

  let processEvent (eventType: string) (data: obj) =
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
      ()
    | "bindings_snapshot" ->
      tryField<obj array> "Bindings" data
      |> Option.iter (fun arr ->
        bindings <- arr
        callbacks.OnBindingsUpdate bindings)
    | "pipeline_trace" ->
      pipelineTrace <- Some data
      callbacks.OnPipelineTraceUpdate data
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
    | _ ->
      ()

  let disposable = subscribeTypedSse url processEvent

  { State = fun () -> state
    Summary = fun () -> VscLiveTestState.summary state
    Bindings = fun () -> bindings
    PipelineTrace = fun () -> pipelineTrace
    EvalDiff = fun () -> evalDiff
    CellGraph = fun () -> cellGraph
    BindingScope = fun () -> bindingScope
    Timeline = fun () -> timeline
    Dispose = fun () -> disposable.dispose () |> ignore }
