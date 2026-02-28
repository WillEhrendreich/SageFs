module SageFs.Vscode.LiveTestingListener

open Fable.Core
open Fable.Core.JsInterop
open Vscode

open SageFs.Vscode.LiveTestingTypes

// ── Event-type-aware SSE subscriber ──────────────────────────

/// SSE subscriber that tracks both `event:` type and `data:` payload.
/// Calls onEvent(eventType, parsedData) for each complete SSE message.
[<Emit("""(() => {
  const http = require('http');
  let req;
  let buffer = '';
  let currentEvent = 'message';
  let retryDelay = 1000;
  const maxDelay = 30000;
  const startListening = () => {
    req = http.get($0, { timeout: 0 }, (res) => {
      retryDelay = 1000;
      res.on('data', (chunk) => {
        buffer += chunk.toString();
        let lines = buffer.split('\n');
        buffer = lines.pop() || '';
        for (const line of lines) {
          if (line.startsWith('event: ')) {
            currentEvent = line.slice(7).trim();
          } else if (line.startsWith('data: ')) {
            try {
              const data = JSON.parse(line.slice(6));
              $1(currentEvent, data);
            } catch (_) {}
            currentEvent = 'message';
          } else if (line.trim() === '') {
            currentEvent = 'message';
          }
        }
      });
      res.on('end', () => {
        retryDelay = Math.min(retryDelay * 2, maxDelay);
        setTimeout(startListening, retryDelay);
      });
      res.on('error', () => {
        retryDelay = Math.min(retryDelay * 2, maxDelay);
        setTimeout(startListening, retryDelay);
      });
    });
    req.on('error', () => {
      retryDelay = Math.min(retryDelay * 2, maxDelay);
      setTimeout(startListening, retryDelay);
    });
  };
  startListening();
  return { dispose: () => { if (req) req.destroy(); } };
})()""")>]
let subscribeTypedSse (url: string) (onEvent: string -> obj -> unit) : Disposable = jsNative

// ── Server JSON → VscLiveTestEvent mappers ───────────────────

/// Map server TestSummary JSON to VscTestSummary
let parseSummary (data: obj) : VscTestSummary =
  { Total = data?Total |> unbox<int>
    Passed = data?Passed |> unbox<int>
    Failed = data?Failed |> unbox<int>
    Running = data?Running |> unbox<int>
    Stale = data?Stale |> unbox<int>
    Disabled = data?Disabled |> unbox<int> }

/// Map a server TestStatusEntry to VscTestResult
let parseTestResult (entry: obj) : VscTestResult =
  let testIdObj = entry?TestId
  let testIdStr : string =
    if isNull testIdObj then ""
    else
      let fields = testIdObj?Fields
      if isNull fields then string testIdObj
      else (fields :> obj array).[0] |> unbox<string>
  let id = VscTestId.create testIdStr
  let status = entry?Status
  let statusCase : string =
    if isNull status then "Detected"
    else status?Case |> unbox<string>
  let outcome =
    match statusCase with
    | "Passed" ->
      VscTestOutcome.Passed
    | "Failed" ->
      let failure = status?Fields |> unbox<obj array>
      let failObj = failure.[0]
      let msg =
        if isNull failObj then "test failed"
        else
          let failFields = failObj?Fields
          if isNull failFields then string failObj
          else (failFields :> obj array).[0] |> unbox<string>
      VscTestOutcome.Failed msg
    | "Skipped" ->
      let fields = status?Fields |> unbox<obj array>
      VscTestOutcome.Skipped(fields.[0] |> unbox<string>)
    | "Running" -> VscTestOutcome.Running
    | "Stale" -> VscTestOutcome.Stale
    | "PolicyDisabled" -> VscTestOutcome.PolicyDisabled
    | _ -> VscTestOutcome.Skipped "unknown status"
  let durationMs =
    match statusCase with
    | "Passed" ->
      let fields = status?Fields |> unbox<obj array>
      let dur : string = fields.[0] |> unbox<string>
      let parts = dur.Split(':')
      if parts.Length = 3 then
        let h = float parts.[0]
        let m = float parts.[1]
        let s = float parts.[2]
        Some ((h * 3600.0 + m * 60.0 + s) * 1000.0)
      else None
    | "Failed" ->
      let fields = status?Fields |> unbox<obj array>
      if fields.Length >= 2 then
        let dur : string = fields.[1] |> unbox<string>
        let parts = dur.Split(':')
        if parts.Length = 3 then
          let h = float parts.[0]
          let m = float parts.[1]
          let s = float parts.[2]
          Some ((h * 3600.0 + m * 60.0 + s) * 1000.0)
        else None
      else None
    | _ -> None
  { Id = id; Outcome = outcome; DurationMs = durationMs; Output = None }

/// Map a server TestStatusEntry to VscTestInfo
let parseTestInfo (entry: obj) : VscTestInfo =
  let testIdObj = entry?TestId
  let testIdStr : string =
    if isNull testIdObj then ""
    else
      let fields = testIdObj?Fields
      if isNull fields then string testIdObj
      else (fields :> obj array).[0] |> unbox<string>
  let origin = entry?Origin
  let filePath, line =
    if isNull origin then None, None
    else
      let originCase : string = origin?Case |> unbox<string>
      match originCase with
      | "SourceMapped" ->
        let fields = origin?Fields |> unbox<obj array>
        Some(fields.[0] |> unbox<string>), Some(fields.[1] |> unbox<int>)
      | _ -> None, None
  { Id = VscTestId.create testIdStr
    DisplayName = entry?DisplayName |> unbox<string>
    FullName = entry?FullName |> unbox<string>
    FilePath = filePath
    Line = line }

/// Parse Freshness DU from server JSON (Case/Fields or plain string)
let parseFreshness (data: obj) : VscResultFreshness =
  let freshnessObj = data?Freshness
  if isNull freshnessObj then VscResultFreshness.Fresh
  else
    let caseStr : string =
      let c = freshnessObj?Case
      if isNull c then string freshnessObj
      else c |> unbox<string>
    match caseStr with
    | "StaleCodeEdited" -> VscResultFreshness.StaleCodeEdited
    | "StaleWrongGeneration" -> VscResultFreshness.StaleWrongGeneration
    | _ -> VscResultFreshness.Fresh

/// Parse test_results_batch → VscLiveTestEvent pair (discovery + results)
let parseResultsBatch (data: obj) : VscLiveTestEvent list =
  let entries = data?Entries
  if isNull entries then []
  else
    let freshness = parseFreshness data
    let entryArray : obj array = entries |> unbox
    let testInfos = entryArray |> Array.map parseTestInfo
    let testResults = entryArray |> Array.map parseTestResult
    [ VscLiveTestEvent.TestsDiscovered testInfos
      VscLiveTestEvent.TestResultBatch (testResults, freshness) ]

// ── Listener lifecycle ───────────────────────────────────────

type LiveTestingCallbacks = {
  OnStateChange: VscStateChange list -> unit
  OnSummaryUpdate: VscTestSummary -> unit
  OnStatusRefresh: unit -> unit
}

type LiveTestingListener = {
  State: unit -> VscLiveTestState
  Summary: unit -> VscTestSummary
  Dispose: unit -> unit
}

let start (port: int) (callbacks: LiveTestingCallbacks) : LiveTestingListener =
  let mutable state = VscLiveTestState.empty
  let url = sprintf "http://localhost:%d/events" port

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
      // Session events (warmup_context_snapshot, hotreload_snapshot)
      // Currently logged for forward compatibility; handlers added as needed
      ()
    | _ ->
      ()

  let disposable = subscribeTypedSse url processEvent

  { State = fun () -> state
    Summary = fun () -> VscLiveTestState.summary state
    Dispose = fun () -> disposable.dispose () |> ignore }
