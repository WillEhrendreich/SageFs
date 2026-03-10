module SageFs.Features.Diagnostician

open System
open SageFs.Features.CellDependencyGraph
open SageFs.Features.EvalProvenance
open SageFs.Features.EvalTimeline
open SageFs.Features.LiveTesting

/// Severity of a diagnostic report — drives urgency of push notifications.
type DiagnosticSeverity =
  | Info
  | Warning
  | Critical

/// A single diagnosed failure with full causal context.
type DiagnosedFailure = {
  TestId: TestId
  TestName: string
  Narrative: FailureNarrative
  CausalCells: CellId list
  Staleness: Map<CellId, Staleness>
}

/// Composed diagnostic report joining failure narratives, ripple plans,
/// provenance, suggestions, and performance context into one coherent view.
type DiagnosticReport = {
  Failures: DiagnosedFailure list
  AffectedCells: (CellId * Staleness) list
  RipplePlan: RipplePlan option
  SuggestedFixes: Suggestion list
  PerformanceContext: TimelineStats option
  Severity: DiagnosticSeverity
  Summary: string
}

module DiagnosticReport =
  let empty = {
    Failures = []
    AffectedCells = []
    RipplePlan = None
    SuggestedFixes = []
    PerformanceContext = None
    Severity = Info
    Summary = "No issues detected."
  }

/// Pure composition: joins 5 feature modules into a single diagnostic report.
module Diagnostician =

  /// Resolve which cells caused each test failure by joining CausalChange symbols
  /// back to CellGraph producers.
  let private resolveCausalCells
    (graph: CellGraph)
    (narrative: FailureNarrative)
    : CellId list =
    narrative.CausalChanges
    |> List.choose (fun change ->
      match change with
      | CausalChange.SymbolChanged symbol ->
        graph.Cells
        |> Map.tryPick (fun cellId info ->
          match info.Produces |> List.contains symbol with
          | true -> Some cellId
          | false -> None)
      | _ -> None)
    |> List.distinct

  /// Compute staleness for all affected cells.
  let private computeStalenessMap
    (graph: CellGraph)
    (affectedCells: CellId list)
    (changedCells: Set<CellId>)
    : Map<CellId, Staleness> =
    affectedCells
    |> List.map (fun cellId ->
      let prov = EvalProvenance.compute graph cellId changedCells
      cellId, prov.Staleness)
    |> Map.ofList

  /// Classify severity from failure count and performance anomaly.
  let private classifySeverity
    (failureCount: int)
    (p95Spike: bool)
    : DiagnosticSeverity =
    match failureCount, p95Spike with
    | 0, false -> Info
    | 0, true -> Warning
    | _, _ -> Critical

  /// Detect P95 spike: true when P95 > 3× P50 (a meaningful regression signal).
  let private detectP95Spike (stats: TimelineStats option) : bool =
    match stats with
    | Some s ->
      match s.P50Ms, s.P95Ms with
      | Some p50, Some p95 when p50 > 0.0 -> p95 / p50 > 3.0
      | _ -> false
    | None -> false

  /// Generate human-readable summary, ≤10 lines.
  let summarize (report: DiagnosticReport) : string =
    match report.Failures.Length with
    | 0 ->
      match report.Severity with
      | Warning -> "⚠️ Performance anomaly detected — no test failures."
      | _ -> "✅ No issues detected."
    | failCount ->
      let lines = ResizeArray<string>()
      lines.Add(sprintf "🔴 %d test%s failed" failCount (match failCount with 1 -> "" | _ -> "s"))

      report.Failures
      |> List.truncate 3
      |> List.iter (fun f ->
        let causalHint =
          match f.Narrative.CausalChanges with
          | [] -> ""
          | changes ->
            let symbols =
              changes
              |> List.choose (fun c ->
                match c with
                | CausalChange.SymbolChanged s -> Some s
                | CausalChange.FileChanged p -> Some (IO.Path.GetFileName p)
                | CausalChange.Unknown -> None)
            match symbols with
            | [] -> ""
            | syms -> sprintf " — likely caused by: %s" (syms |> String.concat ", ")
        lines.Add(sprintf "  • %s%s" f.TestName causalHint))

      match report.Failures.Length > 3 with
      | true -> lines.Add(sprintf "  … and %d more" (report.Failures.Length - 3))
      | false -> ()

      match report.RipplePlan with
      | Some plan when plan.Steps.Length > 0 ->
        lines.Add(sprintf "Ripple: %d cell%s affected" plan.Steps.Length (match plan.Steps.Length with 1 -> "" | _ -> "s"))
      | _ -> ()

      match report.SuggestedFixes with
      | [] -> ()
      | fixes ->
        let top = fixes |> List.truncate 2
        top |> List.iter (fun s ->
          lines.Add(sprintf "💡 %s (%.0f%%)" s.Explanation (s.Confidence * 100.0)))

      match report.PerformanceContext with
      | Some stats ->
        lines.Add(sprintf "Perf: %s" stats.Sparkline)
      | None -> ()

      lines |> Seq.truncate 10 |> String.concat "\n"

  /// Compose a full diagnostic report from the 5 feature module outputs.
  let compose
    (graph: CellGraph)
    (testFailures: (TestId * string * FailureNarrative) list)
    (scope: ScopeBinding list)
    (timeline: TimelineState)
    : DiagnosticReport =

    let timelineStats =
      match timeline.Entries with
      | [] -> None
      | _ -> Some (EvalTimeline.timelineStats 20 timeline)

    let p95Spike = detectP95Spike timelineStats

    // Join each failure to its causal cells
    let diagFailures =
      testFailures
      |> List.map (fun (testId, testName, narrative) ->
        let causalCells = resolveCausalCells graph narrative
        let changedSet = causalCells |> Set.ofList
        let stalenessMap = computeStalenessMap graph causalCells changedSet
        { TestId = testId
          TestName = testName
          Narrative = narrative
          CausalCells = causalCells
          Staleness = stalenessMap })

    // Collect all affected cells across all failures
    let allCausalCells =
      diagFailures
      |> List.collect (fun f -> f.CausalCells)
      |> List.distinct

    let changedSet = allCausalCells |> Set.ofList

    // Compute ripple plan if there are causal cells
    let ripplePlan =
      match changedSet.IsEmpty with
      | true -> None
      | false -> Some (EvalRipple.planRipple graph changedSet)

    // Compute staleness for all transitively affected cells
    let allAffected =
      allCausalCells
      |> List.collect (fun cid -> CellDependencyGraph.transitiveStale graph cid)
      |> List.append allCausalCells
      |> List.distinct

    let affectedWithStaleness =
      allAffected
      |> List.map (fun cid ->
        let prov = EvalProvenance.compute graph cid changedSet
        cid, prov.Staleness)

    // Generate suggestions from current scope bindings
    let suggestions = Ghostwriter.suggest scope

    let severity = classifySeverity diagFailures.Length p95Spike

    let report = {
      Failures = diagFailures
      AffectedCells = affectedWithStaleness
      RipplePlan = ripplePlan
      SuggestedFixes = suggestions
      PerformanceContext = timelineStats
      Severity = severity
      Summary = ""
    }

    { report with Summary = summarize report }
