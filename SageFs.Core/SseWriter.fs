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

/// Pure: format an SSE event string with a sequence ID for Last-Event-Id replay support.
/// The `id:` field is prepended per the EventSource spec, enabling reconnection.
let formatSseEventWithId (seqId: int64) (eventType: string) (data: string) : string =
  let frame = formatSseEvent eventType data
  sprintf "id: %d\n%s" seqId frame

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
let formatTestSummaryEvent
  (opts: JsonSerializerOptions)
  (sessionId: string option)
  (summary: Features.LiveTesting.TestSummary)
  (lastDecision: Features.LiveTesting.LiveTestingDecision option)
  : string =
  let payload =
    {| Total = summary.Total
       Passed = summary.Passed
       Failed = summary.Failed
       Stale = summary.Stale
       Running = summary.Running
       Disabled = summary.Disabled
       Enabled = summary.Enabled
       LastDecision = lastDecision |> Option.map Features.LiveTesting.LiveTestingDecision.toWireModel |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "test_summary" json

/// Format a TestSummary as an SSE event string, carrying the authoritative
/// per-session discovery state + generation. Discovery is REPLACEMENT state:
/// clients must reject summaries whose DiscoveryGeneration is older than the
/// last one they applied, and ReadyZeroTests/ready_zero_tests is how a
/// completed discovery with zero tests becomes observable (the zero-test
/// suppression defect).
let formatTestSummaryEventWithDiscovery
  (opts: JsonSerializerOptions)
  (sessionId: string option)
  (summary: Features.LiveTesting.TestSummary)
  (lastDecision: Features.LiveTesting.LiveTestingDecision option)
  (discoveryState: Features.LiveTesting.LiveTestDiscoveryState)
  (discoveryGeneration: int64)
  (activity: Features.LiveTestActivity.LiveTestActivity)
  : string =
  // The activity is the session's one state; clients render its words
  // instead of rebuilding a state from the counts.
  let payload =
    {| Total = summary.Total
       Passed = summary.Passed
       Failed = summary.Failed
       Stale = summary.Stale
       Running = summary.Running
       Disabled = summary.Disabled
       Enabled = summary.Enabled
       NotYetRun = (Features.LiveTestActivity.LiveTestActivity.tallyOf activity).NotYetRun
       Activity = Features.LiveTestActivity.LiveTestActivity.wireKind activity
       ActivityText = Features.LiveTestActivity.LiveTestActivity.describe activity
       ActivityShort = Features.LiveTestActivity.LiveTestActivity.shortLabel activity
       DiscoveryState = Features.LiveTesting.LiveTestDiscoveryState.toWireValue discoveryState
       DiscoveryGeneration = discoveryGeneration
       LastDecision = lastDecision |> Option.map Features.LiveTesting.LiveTestingDecision.toWireModel |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "test_summary" json

/// Format a TestResultsBatchPayload as an SSE event string
let formatTestResultsBatchEvent (opts: JsonSerializerOptions) (sessionId: string option) (payload: Features.LiveTesting.TestResultsBatchPayload) : string =
  let wirePayload =
    {| Generation = payload.Generation
       Freshness = payload.Freshness
       Completion = payload.Completion
       Entries = payload.Entries
       Summary = payload.Summary
       LastDecision = payload.LastDecision |> Option.map Features.LiveTesting.LiveTestingDecision.toWireModel |}
  let json = JsonSerializer.Serialize(wirePayload, opts) |> injectSessionId sessionId
  formatSseEvent "test_results_batch" json

/// Format a FileAnnotations as an SSE event string
let formatFileAnnotationsEvent (opts: JsonSerializerOptions) (sessionId: string option) (annotations: Features.LiveTesting.FileAnnotations) : string =
  let json = JsonSerializer.Serialize(annotations, opts) |> injectSessionId sessionId
  formatSseEvent "file_annotations" json

/// Format a CoverageView as an SSE event string.
/// WHY — this is the shared "per-function aggregate" event that all three
/// editors subscribe to. Each editor renders it natively: VSCode shows it
/// as one CodeLens, Neovim shows it as one virt_text line, VS shows it
/// in the navigation bar. The editor never sees per-test inline text for
/// a heavily-tested function — only the aggregate badge.
/// Performance: a single JsonSerializer.Serialize call per view. The
/// caller (SsePublisher) is expected to throttle / batch across files.
let formatCoverageViewEvent
  (opts: JsonSerializerOptions)
  (sessionId: string option)
  (generation: int)
  (view: Features.LiveTesting.CoverageView)
  : string =
  let payload =
    {| Generation = generation
       Symbol = view.Symbol
       FilePath = view.FilePath
       DefinitionLine = view.DefinitionLine
       TotalCount = view.TotalCount
       Overflow = view.Overflow
       InlineBadgeText = view.InlineBadgeText
       Health = view.Health |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "coverage_view" json

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

/// Format a bindings snapshot as an SSE event string.
/// Includes both the legacy FsiBinding array (for backward compat) and rich BindingValue records.
/// blockStartLine (1-based) lets clients position per-binding ghost text decorations correctly.
let formatBindingsSnapshotEvent
  (opts: JsonSerializerOptions)
  (sessionId: string option)
  (bindingValues: Features.FsiOutputParser.BindingValue list)
  (blockStartLine: int)
  (filePath: string option)
  (bindings: FsiBinding array)
  : string =
  let json =
    JsonSerializer.Serialize(
      {| Bindings = bindings
         BindingValues = bindingValues
         blockStartLine = blockStartLine
         filePath = filePath |> Option.defaultValue "" |},
      opts)
    |> injectSessionId sessionId
  formatSseEvent "bindings_snapshot" json

/// Format a live bindings snapshot (the reflection-walked watch-window tree)
/// as an SSE event string.
let formatLiveBindingsEvent
  (opts: JsonSerializerOptions)
  (sessionId: string option)
  (snapshot: Features.LiveValueTree.LiveValueSnapshot)
  : string =
  let json =
    JsonSerializer.Serialize(snapshot, opts)
    |> injectSessionId sessionId
  formatSseEvent "live_bindings" json

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

/// Format an eval heartbeat as an SSE event.
/// Emitted every ~500ms by an independent timer while an eval is running.
/// Lets editors show elapsed time and confirm the SSE connection is alive.
let formatEvalHeartbeatEvent (opts: JsonSerializerOptions) (sessionId: string option) (filePath: string) (blockStartLine: int) (elapsedMs: int64) : string =
  let json =
    JsonSerializer.Serialize(
      {| FilePath = filePath
         BlockStartLine = blockStartLine
         ElapsedMs = elapsedMs |}, opts)
    |> injectSessionId sessionId
  formatSseEvent "eval_heartbeat" json

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
/// Includes per-failure testName + causalSymbols so clients can render repair CodeLens.
let formatDiagnosisReadyEvent (opts: JsonSerializerOptions) (sessionId: string option) (report: Features.Diagnostician.DiagnosticReport) : string =
  let extractCausalSymbols (changes: Features.LiveTesting.CausalChange list) =
    changes
    |> List.choose (function
      | Features.LiveTesting.CausalChange.SymbolChanged s -> Some s
      | _ -> None)
    |> List.toArray
  let payload =
    {| Severity = sprintf "%A" report.Severity
       FailureCount = report.Failures.Length
       AffectedCells = report.AffectedCells
       SuggestionCount = report.SuggestedFixes.Length
       TopSuggestions =
         report.SuggestedFixes
         |> List.truncate 3
         |> List.map (fun s -> {| Code = s.Code; Explanation = s.Explanation; Confidence = s.Confidence |})
       Failures =
         report.Failures
         |> List.map (fun f ->
           {| TestName = f.TestName
              CausalSymbols = extractCausalSymbols f.Narrative.CausalChanges |})
         |> List.toArray
       Performance =
         report.PerformanceContext
         |> Option.map (fun s -> {| Sparkline = s.Sparkline; P50Ms = s.P50Ms; P95Ms = s.P95Ms |})
       Summary = report.Summary |}
  let json = JsonSerializer.Serialize(payload, opts) |> injectSessionId sessionId
  formatSseEvent "diagnosis_ready" json

// ── Cohort SSE events (item 15a: cohort_matrix / claim_changed / landing_changed) ──
// Multi-agent cohort coordination (sagefs-multiagent-vision.md). Unlike every
// formatter above, these three rows are NOT session-scoped — one cohort spans
// every session/agent connected to this daemon — so they carry no SessionId.
// `Features.CohortOwner` (SageFs.Core/Features/CohortOwner.fs) is the single
// per-daemon owner; `SageFs/McpServer.fs` subscribes to its `Events` stream
// and reads its published `CohortFrame`/`CohortState` to build these payloads.

let private displayMember (m: MemberTable.MemberId) = MemberTable.MemberId.display m

let private claimScopeToWire (scope: Cohort.ClaimScope) =
  match scope with
  | Cohort.ClaimScope.File p -> {| Kind = "file"; Path = p |}
  | Cohort.ClaimScope.Project p -> {| Kind = "project"; Path = p |}

/// Uniform shape across Held/Orphaned/Released so `Array.map` produces one
/// anonymous-record type — F# gives each distinct field set its own
/// structural type, so the match arms must agree on fields.
let private claimStateToWire (state: Cohort.ClaimState<MemberTable.MemberId>) =
  match state with
  | Cohort.ClaimState.Held holder -> {| Kind = "held"; Holder = displayMember holder; Since = None |}
  | Cohort.ClaimState.Orphaned (prev, since) -> {| Kind = "orphaned"; Holder = displayMember prev; Since = Some since |}
  | Cohort.ClaimState.Released (by, at) -> {| Kind = "released"; Holder = displayMember by; Since = Some at |}

/// Uniform shape across every `LandingBlocker` case, for the same reason as
/// `claimStateToWire` — fields not meaningful for a given case are left at
/// their zero value ("" / []) rather than becoming a per-case record type.
let private landingBlockerToWire (blocker: Cohort.LandingBlocker<MemberTable.MemberId>) =
  match blocker with
  | Cohort.LandingBlocker.RebaseConflict files ->
    {| Kind = "rebase_conflict"; Files = files; Tests = []; ClaimId = ""; From = ""; To = ""; By = ""; Reason = "" |}
  | Cohort.LandingBlocker.FailingTests tests ->
    {| Kind = "failing_tests"; Files = []; Tests = tests |> List.map (fun (Cohort.TestId t) -> t); ClaimId = ""; From = ""; To = ""; By = ""; Reason = "" |}
  | Cohort.LandingBlocker.StaleClaimFence (Cohort.ClaimId cid) ->
    {| Kind = "stale_claim_fence"; Files = []; Tests = []; ClaimId = cid; From = ""; To = ""; By = ""; Reason = "" |}
  | Cohort.LandingBlocker.HeadMoved (from, to') ->
    {| Kind = "head_moved"; Files = []; Tests = []; ClaimId = ""; From = from; To = to'; By = ""; Reason = "" |}
  | Cohort.LandingBlocker.VetoedBy (by, reason) ->
    {| Kind = "vetoed_by"; Files = []; Tests = []; ClaimId = ""; From = ""; To = ""; By = displayMember by; Reason = reason |}

let private nextActionToWire (action: Cohort.NextAction) =
  match action with
  | Cohort.NextAction.RebaseAndResubmit -> {| Kind = "rebase_and_resubmit"; Tests = [] |}
  | Cohort.NextAction.AwaitConductor -> {| Kind = "await_conductor"; Tests = [] |}
  | Cohort.NextAction.FixTests tests -> {| Kind = "fix_tests"; Tests = tests |> List.map (fun (Cohort.TestId t) -> t) |}
  | Cohort.NextAction.Withdraw -> {| Kind = "withdraw"; Tests = [] |}

let private landingStateKind (state: Cohort.LandingState<MemberTable.MemberId>) =
  match state with
  | Cohort.LandingState.Queued -> "queued"
  | Cohort.LandingState.Rebasing _ -> "rebasing"
  | Cohort.LandingState.Verifying _ -> "verifying"
  | Cohort.LandingState.Blocked _ -> "blocked"
  | Cohort.LandingState.Landed _ -> "landed"
  | Cohort.LandingState.Withdrawn -> "withdrawn"

/// Format the daemon's single per-daemon `CohortFrame` (Cohort.fs's `project`)
/// as an SSE event string: members (id/role/seat/conductor), claims
/// (id/scope/holder/fence/state), and the flat, index-aligned test matrix
/// (Tests + one Pass/Fail/Stale row per session, `Rows.[i]` aligned with
/// `frame.SessionGens.[i]`). Version-gate at the call site (compare
/// `frame.Version` to the last one sent, like `coverage_view`'s generation
/// gate) — this formatter itself always formats whatever frame it is given.
let formatCohortMatrixEvent (opts: JsonSerializerOptions) (frame: Cohort.CohortFrame<MemberTable.MemberId>) : string =
  let members =
    Array.init frame.MemberIds.Length (fun i ->
      {| Id = displayMember frame.MemberIds.[i]
         Role = sprintf "%A" frame.MemberRole.[i]
         Seat =
           match frame.MemberSeat.[i] with
           | Cohort.SeatState.Present -> "present"
           | Cohort.SeatState.Departed _ -> "departed"
         Conductor = frame.Conductor = Some frame.MemberIds.[i] |})
  let claims =
    Array.init frame.ClaimIds.Length (fun i ->
      let (Cohort.ClaimId cid) = frame.ClaimIds.[i]
      let holder =
        match frame.ClaimHolderIndex.[i] with
        | -1 -> None
        | idx -> Some (displayMember frame.MemberIds.[idx])
      {| Id = cid
         Scope = claimScopeToWire frame.ClaimScope.[i]
         Holder = holder
         Fence = frame.ClaimFence.[i]
         State = claimStateToWire frame.ClaimState.[i] |})
  let tests = frame.TestIds |> Array.map (fun (Cohort.TestId t) -> t)
  let rows =
    Array.init frame.SessionGens.Length (fun i ->
      {| Generation = frame.SessionGens.[i]
         Pass = frame.Pass.[i]
         Fail = frame.Fail.[i]
         Stale = frame.Stale.[i] |})
  let payload =
    {| Version = frame.Version
       Members = members
       Claims = claims
       Tests = tests
       Rows = rows |}
  let json = JsonSerializer.Serialize(payload, opts)
  formatSseEvent "cohort_matrix" json

/// Format a single claim change (from a `ClaimAcquired`/`ClaimReleased`/
/// `ClaimOrphaned`/`ClaimReassigned` `Cohort.CohortEvent`) as an SSE event
/// string. `claim` is the CURRENT `Cohort.Claim` — read from
/// `CohortOwner.Handle.ReadCohortState()` after the triggering event was
/// applied — because the event DUs themselves don't all carry `Scope`
/// (`ClaimReleased`/`ClaimOrphaned`/`ClaimReassigned` don't), so the caller
/// looks the claim up by id instead of projecting the event's own fields.
/// `kind` names which event fired: "acquired" | "released" | "orphaned" | "reassigned".
let formatClaimChangedEvent (opts: JsonSerializerOptions) (kind: string) (claim: Cohort.Claim<MemberTable.MemberId>) : string =
  let (Cohort.ClaimId cid) = claim.Id
  let holder =
    match claim.State with
    | Cohort.ClaimState.Held h -> Some (displayMember h)
    | _ -> None
  let payload =
    {| ClaimId = cid
       Scope = claimScopeToWire claim.Scope
       Holder = holder
       Fence = claim.Fence
       Kind = kind |}
  let json = JsonSerializer.Serialize(payload, opts)
  formatSseEvent "claim_changed" json

/// Format a single landing state change (from a `LandingStateChanged`
/// `Cohort.CohortEvent`) as an SSE event string. `landing` is the CURRENT
/// `Cohort.LandingRequest` — read from `ReadCohortState()` after the
/// triggering event was applied, same reasoning as `formatClaimChangedEvent`.
/// `Blocker`/`NextAction` are populated only when `State = "blocked"`.
let formatLandingChangedEvent (opts: JsonSerializerOptions) (landing: Cohort.LandingRequest<MemberTable.MemberId>) : string =
  let (Cohort.LandingId lid) = landing.Id
  let blocker, nextAction =
    match landing.State with
    | Cohort.LandingState.Blocked (b, na) -> Some (landingBlockerToWire b), Some (nextActionToWire na)
    | _ -> None, None
  let payload =
    {| LandingId = lid
       Requester = displayMember landing.Requester
       State = landingStateKind landing.State
       Blocker = blocker
       NextAction = nextAction |}
  let json = JsonSerializer.Serialize(payload, opts)
  formatSseEvent "landing_changed" json

/// Format a claim early-warning (multi-agent vision §5.1: a cohort member's
/// watcher observed a save landing inside a DIFFERENT member's held claim —
/// advisory, never blocking, `Cohort.decide`'s `ObserveSave` case never
/// refuses). `claim` is the CURRENT `Cohort.Claim` — read from
/// `CohortOwner.Handle.ReadCohortState()` by the `ClaimViolationObserved`
/// event's `ClaimId` — because that event doesn't carry `Scope` either,
/// same reasoning as `formatClaimChangedEvent`/`formatLandingChangedEvent`.
/// `observer`/`holder`/`path` come straight off the event itself.
let formatSaveObservedEvent
  (opts: JsonSerializerOptions)
  (claim: Cohort.Claim<MemberTable.MemberId>)
  (observer: MemberTable.MemberId)
  (holder: MemberTable.MemberId)
  (path: string) : string =
  let (Cohort.ClaimId cid) = claim.Id
  let payload =
    {| ClaimId = cid
       Observer = displayMember observer
       Holder = displayMember holder
       Scope = claimScopeToWire claim.Scope
       Path = path |}
  let json = JsonSerializer.Serialize(payload, opts)
  formatSseEvent "save_observed" json

// ── Authoritative SSE event type registry ──────────────────────────────────────────

/// Authoritative list of all SSE event type names emitted by SseWriter formatters.
/// Every event type emitted by the daemon that originates from SseWriter must appear here.
/// The `"state"` and `"session"` events are the two channels of the unified
/// SageFs.Server.SseEvent vocabulary (roast-5 §1) — one DU, one serializer,
/// classified by SseEvent.channel; their channel names are
/// SseEvent.sseEventTypeState / SseEvent.sseEventTypeSession.
let allSseEventTypes : string list = [
  "warmup_progress"
  "test_summary"
  "test_results_batch"
  "file_annotations"
  "failure_narratives"
  "test_source_locations"
  "bindings_snapshot"
  "live_bindings"
  "test_trace"
  "eval_diff"
  "eval_started"
  "eval_heartbeat"
  "eval_result"
  "cell_dependencies"
  "binding_scope_map"
  "eval_timeline"
  "domain_model"
  "diagnosis_ready"
  "coverage_view"
  "cohort_matrix"
  "claim_changed"
  "landing_changed"
  "save_observed"
]
