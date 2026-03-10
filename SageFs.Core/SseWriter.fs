module SageFs.SseWriter

open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Pure: format an SSE retry hint per the EventSource spec.
/// Tells the client how many milliseconds to wait before reconnecting.
let formatRetryHint (retryMs: int) : string =
  sprintf "retry: %d\n\n" retryMs

/// Pure: format an SSE event string.
/// Handles data containing newlines per SSE spec (each line as separate data: field).
let formatSseEvent (eventType: string) (data: string) : string =
  match data.Contains("\n") with
  | true ->
    let dataLines = data.Split('\n') |> Array.map (sprintf "data: %s") |> String.concat "\n"
    sprintf "event: %s\n%s\n\n" eventType dataLines
  | false ->
    sprintf "event: %s\ndata: %s\n\n" eventType data

/// Pure: format SSE event with multiline data
let formatSseEventMultiline (eventType: string) (lines: string list) : string =
  match lines with
  | [] -> sprintf "event: %s\n\n" eventType
  | _ ->
    let dataLines = lines |> List.map (sprintf "data: %s") |> String.concat "\n"
    sprintf "event: %s\n%s\n\n" eventType dataLines

/// Safely write bytes to a stream, returning Result instead of throwing
let trySendBytes (stream: Stream) (bytes: byte[]) : Task<Result<unit, string>> =
  task {
    try
      do! stream.WriteAsync(bytes)
      do! stream.FlushAsync()
      return Ok ()
    with ex ->
      return Error (sprintf "SSE write failed: %s" ex.Message)
  }

/// Format + send an SSE event, returning Result instead of throwing
let trySendSseEvent (stream: Stream) (eventType: string) (data: string) : Task<Result<unit, string>> =
  let text = formatSseEvent eventType data
  let bytes = Encoding.UTF8.GetBytes(text)
  trySendBytes stream bytes

/// Inject a SessionId field into a JSON object string. None = no change (backward compat).
let injectSessionId (sessionId: string option) (json: string) : string =
  match sessionId with
  | None -> json
  | Some sid ->
    match json.StartsWith("{") with
    | true ->
      sprintf """{"SessionId":"%s",%s""" sid (json.Substring(1))
    | false -> json

// ── Warmup progress SSE event ──

/// Derive a human-readable warmup phase from step/total context.
/// Total <= 5 means main warmup phases (FSI creation, scanning, assembly load, finalize).
/// Total > 5 means per-namespace open phase.
let private deriveWarmupPhase (step: int) (total: int) =
  match total <= 5 with
  | true ->
    match step with
    | 1 -> "creating_fsi"
    | 2 -> "scanning_sources"
    | 3 -> "loading_assemblies"
    | _ -> "finalizing"
  | false -> "opening_namespaces"

/// Format a warmup progress event as an SSE event string.
/// Emitted during session warmup so editor plugins can show phase-by-phase progress.
let formatWarmupProgressEvent (opts: JsonSerializerOptions) (sessionId: string option) (step: int) (total: int) (message: string) : string =
  let progress =
    match total with
    | 0 -> 0.0
    | t -> System.Math.Round(float step / float t, 3)
  let phase = deriveWarmupPhase step total
  let json =
    JsonSerializer.Serialize(
      {| Step = step; Total = total; Message = message; Progress = progress; Phase = phase |}, opts)
    |> injectSessionId sessionId
  formatSseEvent "warmup_progress" json

/// Format a TestSummary as an SSE event string
let formatTestSummaryEvent (opts: JsonSerializerOptions) (sessionId: string option) (summary: Features.LiveTesting.TestSummary) : string =
  let json = JsonSerializer.Serialize(summary, opts) |> injectSessionId sessionId
  formatSseEvent "test_summary" json

/// Format a TestResultsBatchPayload as an SSE event string
let formatTestResultsBatchEvent (opts: JsonSerializerOptions) (sessionId: string option) (payload: Features.LiveTesting.TestResultsBatchPayload) : string =
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "test_results_batch" json

/// Format a FileAnnotations as an SSE event string
let formatFileAnnotationsEvent (opts: JsonSerializerOptions) (sessionId: string option) (annotations: Features.LiveTesting.FileAnnotations) : string =
  let json = JsonSerializer.Serialize(annotations, opts) |> injectSessionId sessionId
  formatSseEvent "file_annotations" json

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
  formatSseEvent "failure_narratives" json

/// Format TestSourceLocations as an SSE event string
let formatTestSourceLocationsEvent (opts: JsonSerializerOptions) (sessionId: string option) (locations: Features.LiveTesting.TestSourceLocation list) : string =
  let payload = {| Locations = locations |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "test_source_locations" json

// ── Bindings snapshot (CQRS: server-side parsing, push via SSE) ──

/// A single FSI binding tracked server-side
type FsiBinding = {
  Name: string
  TypeSig: string
  Value: string option
  ShadowCount: int
}

// ── Binding parser: Option.bind pipeline (ROP) ──

/// Try to strip a prefix, returning the rest or None
let private tryStripPrefix (prefix: string) (s: string) =
  match s.Trim() with
  | t when t.StartsWith(prefix) -> Some (t.Substring(prefix.Length))
  | _ -> None

/// Strip "mutable " prefix if present (total function, always succeeds)
let private stripMutablePrefix (s: string) =
  match s with
  | t when t.StartsWith("mutable ") -> t.Substring(8)
  | t -> t

/// Split at first colon into (name, typeSig), or None
let private splitAtColon (s: string) =
  match s.IndexOf(':') with
  | i when i > 0 -> Some (s.Substring(0, i).Trim(), s.Substring(i + 1).Trim())
  | _ -> None

/// Split trailing "= value" from a type signature, returning (typeSig, valueOpt)
let private splitTypeSigAndValue (typeSig: string) =
  match typeSig.LastIndexOf('=') with
  | i when i > 0 ->
    let ts = typeSig.Substring(0, i).Trim()
    let v = typeSig.Substring(i + 1).Trim()
    match v with
    | "" -> (ts, None)
    | _ -> (ts, Some v)
  | _ -> (typeSig, None)

/// Validate a binding name: skip "it" (expression results) and tuple patterns
let private validateBindingName (name: string, typeSig: string, value: string option) =
  match name with
  | "it" -> None
  | n when n.Contains("(") -> None
  | _ -> Some (name, typeSig, value)

/// Parse a single FSI output line into (name, typeSig, value) via Option.bind pipeline
let private tryParseBinding (line: string) =
  line
  |> tryStripPrefix "val "
  |> Option.map stripMutablePrefix
  |> Option.bind splitAtColon
  |> Option.map (fun (name, ts) ->
    let (typeSig, value) = splitTypeSigAndValue ts
    (name, typeSig, value))
  |> Option.bind validateBindingName

/// Parse `val name : type = value` lines from FSI output.
/// Skips `val it` (expression results) and tuple patterns.
let parseBindingsFromOutput (output: string) : (string * string * string option) array =
  output.Split('\n') |> Array.choose tryParseBinding

/// Accumulate parsed bindings into a running map, tracking shadow counts.
let accumulateBindings
  (existing: Map<string, FsiBinding>)
  (parsed: (string * string * string option) array)
  : Map<string, FsiBinding> =
  parsed
  |> Array.fold (fun acc (name, typeSig, value) ->
    let count =
      match Map.tryFind name acc with
      | Some b -> b.ShadowCount + 1
      | None -> 1
    Map.add name { Name = name; TypeSig = typeSig; Value = value; ShadowCount = count } acc
  ) existing

/// Format a bindings snapshot as an SSE event string
let formatBindingsSnapshotEvent (opts: JsonSerializerOptions) (sessionId: string option) (bindings: FsiBinding array) : string =
  let json = JsonSerializer.Serialize({| Bindings = bindings |}, opts) |> injectSessionId sessionId
  formatSseEvent "bindings_snapshot" json

/// Format a test trace as an SSE event string
let formatTestTraceEvent (sessionId: string option) (traceJson: string) : string =
  let json = injectSessionId sessionId traceJson
  formatSseEvent "test_trace" json

// ── Feature SSE formatters (CQRS: push-only, no GET endpoints) ──

/// Format an eval diff as an SSE event string
let formatEvalDiffEvent (opts: JsonSerializerOptions) (sessionId: string option) (summary: Features.EvalDiff.DiffSummary) : string =
  let payload =
    {| Lines = summary.Lines |> List.map (fun l ->
         match l with
         | Features.EvalDiff.Added s -> {| Kind = "added"; Text = s; OldText = "" |}
         | Features.EvalDiff.Removed s -> {| Kind = "removed"; Text = ""; OldText = s |}
         | Features.EvalDiff.Modified (o, n) -> {| Kind = "modified"; Text = n; OldText = o |}
         | Features.EvalDiff.Unchanged s -> {| Kind = "unchanged"; Text = s; OldText = "" |})
       Added = summary.AddedCount
       Removed = summary.RemovedCount
       Modified = summary.ModifiedCount
       Unchanged = summary.UnchangedCount |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "eval_diff" json

/// Format an eval started notification as an SSE event.
/// Emitted at the start of each /exec call so editor plugins can mark decorations stale.
let formatEvalStartedEvent (opts: JsonSerializerOptions) (sessionId: string option) (filePath: string) (blockStartLine: int) : string =
  let json =
    JsonSerializer.Serialize(
      {| filePath = filePath
         blockStartLine = blockStartLine |}, opts)
    |> injectSessionId sessionId
  formatSseEvent "eval_started" json

/// Format an eval result as an SSE event for inline decorations.
/// Emitted after each /exec call with filePath, blockStartLine, and durationMs populated.
let formatEvalResultEvent (opts: JsonSerializerOptions) (sessionId: string option) (filePath: string) (blockStartLine: int) (output: string) (success: bool) (durationMs: float) : string =
  let json =
    JsonSerializer.Serialize(
      {| filePath = filePath
         blockStartLine = blockStartLine
         output = output
         success = success
         durationMs = durationMs |}, opts)
    |> injectSessionId sessionId
  formatSseEvent "eval_result" json

/// Format a cell dependency graph as an SSE event string
let formatCellDependenciesEvent (opts: JsonSerializerOptions) (sessionId: string option) (graph: Features.CellDependencyGraph.CellGraph) : string =
  let payload =
    {| Nodes = graph.Cells |> Map.values |> Seq.map (fun c ->
         {| Id = c.Id; Produces = c.Produces; Consumes = c.Consumes |})
         |> Array.ofSeq
       Edges = graph.Edges |> List.map (fun (f, t) -> {| From = f; To = t |}) |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "cell_dependencies" json

/// Format a binding scope map as an SSE event string
let formatBindingScopeMapEvent (opts: JsonSerializerOptions) (sessionId: string option) (snapshot: Features.BindingExplorer.BindingScopeSnapshot) : string =
  let payload =
    {| Bindings = snapshot.Bindings |> List.map (fun b ->
         {| Name = b.Name; TypeSig = b.TypeSig; CellIndex = b.CellIndex
            ShadowedBy = b.ShadowedBy; ReferencedIn = b.ReferencedIn |})
       ActiveCount = snapshot.ActiveBindings.Count
       ShadowedCount = snapshot.ShadowedBindings.Length |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "binding_scope_map" json

/// Format eval timeline stats as an SSE event string
let formatEvalTimelineEvent (opts: JsonSerializerOptions) (sessionId: string option) (stats: Features.EvalTimeline.TimelineStats) : string =
  let payload =
    {| Count = stats.Count
       P50Ms = stats.P50Ms
       P95Ms = stats.P95Ms
       P99Ms = stats.P99Ms
       MeanMs = stats.MeanMs
       Sparkline = stats.Sparkline |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "eval_timeline" json

/// Format annotated domain model transitions as an SSE event string.
/// Each transition carries health status (Passing/Failing/Stale/Untested/NotImplemented).
let formatDomainModelEvent (opts: JsonSerializerOptions) (sessionId: string option) (annotations: Features.DomainModelViz.AnnotatedTransition list) : string =
  let payload =
    {| Transitions =
         annotations |> List.map (fun a ->
           {| FromState = a.FromState
              ToState = a.ToState
              FunctionName = a.FunctionName
              IsErrorBranch = a.IsErrorBranch
              Health = sprintf "%A" a.Health |})
         |> List.toArray |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "domain_model" json

/// Format a DiagnosticReport as an SSE event string.
let formatDiagnosisReadyEvent (opts: JsonSerializerOptions) (sessionId: string option) (report: Features.Diagnostician.DiagnosticReport) : string =
  let payload =
    {| Severity = sprintf "%A" report.Severity
       FailureCount = report.Failures.Length
       AffectedCells = report.AffectedCells
       SuggestionCount = report.SuggestedFixes.Length
       TopSuggestions =
         report.SuggestedFixes
         |> List.truncate 3
         |> List.map (fun s -> {| Code = s.Code; Explanation = s.Explanation; Confidence = s.Confidence |})
       Performance =
         report.PerformanceContext
         |> Option.map (fun s -> {| Sparkline = s.Sparkline; P50Ms = s.P50Ms; P95Ms = s.P95Ms |})
       Summary = report.Summary |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "diagnosis_ready" json

// ── Authoritative SSE event type registry ──────────────────────────────────────────

/// Authoritative list of all SSE event type names emitted by SseWriter formatters.
/// Every event type emitted by the daemon that originates from SseWriter must appear here.
/// The `"state"` event (DaemonStateChange) and `"session"` event (SessionEvents) are
/// tracked separately in DaemonStateChange.sseEventType and SessionEvents.sessionEventType.
let allSseEventTypes : string list = [
  "warmup_progress"
  "test_summary"
  "test_results_batch"
  "file_annotations"
  "failure_narratives"
  "test_source_locations"
  "bindings_snapshot"
  "test_trace"
  "eval_diff"
  "eval_started"
  "eval_result"
  "cell_dependencies"
  "binding_scope_map"
  "eval_timeline"
  "domain_model"
  "diagnosis_ready"
]
