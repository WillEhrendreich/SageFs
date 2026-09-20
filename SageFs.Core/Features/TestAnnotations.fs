/// The inline-feedback READ MODEL (CQRS): editor annotations, code lenses,
/// the coverage view, the run explainer and the session invariants — all
/// projections of `LiveTestState` for presentation.
///
/// Split out of `LiveTestingTypes.fs`, which had grown to 5,232 lines and
/// mixed these presentation projections in with the live-testing domain and
/// its decision logic. Nothing here is referenced anywhere in the FSI host's
/// embedded source closure, so the host no longer compiles any of it.
namespace SageFs.Features.LiveTesting

open System
open System.IO
open System.Numerics
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open SageFs.Measures

// ============================================================
// Inline Feedback Read Model (CQRS)
// Universal FileAnnotations projected from LiveTestState
// ============================================================

[<RequireQualifiedAccess>]
type AnnotationLayer =
  | TestStatus
  | Coverage
  | CodeLens
  | InlineFailure

[<RequireQualifiedAccess>]
type FailurePresentation =
  | AssertionDiff of expected: string * actual: string
  | ExceptionMessage of message: string * relevantFrame: string
  | Timeout of after: TimeSpan
  | RawMessage of message: string

[<RequireQualifiedAccess>]
type CodeLensCommand =
  | RunTest
  | DebugTest
  | ShowHistory

[<RequireQualifiedAccess>]
type AnnotationFreshness =
  | Current
  | Stale
  | Running

type TestLineAnnotation = {
  Line: int
  TestId: TestId
  DisplayName: string
  Status: TestRunStatus
  Freshness: AnnotationFreshness
}

type CoverageLineAnnotation = {
  Line: int
  EndLine: int
  EndColumn: int
  Detail: CoverageStatus
  CoveringTestIds: TestId array
  BranchCoverage: LineCoverage option
}

type InlineFailure = {
  Line: int
  TestId: TestId
  TestName: string
  Failure: FailurePresentation
  Duration: TimeSpan
}

type TestCodeLens = {
  Line: int
  Label: string
  TestId: TestId
  Command: CodeLensCommand
}

/// Per-cell eval performance annotation for editor gutter sparklines.
type PerformanceAnnotation = {
  /// Line in the source file where the ;; boundary begins.
  Line: int
  /// Cell index in the eval session.
  CellIndex: int
  /// Recent eval durations in ms (most recent last), up to 10 entries.
  DurationsMs: float list
  /// Sparkline string (Unicode block chars) for gutter display.
  Sparkline: string
  /// P50 of recent durations.
  P50Ms: float
  /// P95 of recent durations.
  P95Ms: float
}

type FileAnnotations = {
  FilePath: string
  TestAnnotations: TestLineAnnotation array
  CoverageAnnotations: CoverageLineAnnotation array
  InlineFailures: InlineFailure array
  CodeLenses: TestCodeLens array
  PerformanceAnnotations: PerformanceAnnotation array
}

module FailurePresentation =
  let private cleanValue (s: string) = s.TrimEnd('.', ' ')

  /// Parse "Expected: X / Actual: Y" or NUnit's "Expected: X / But was: Y"
  let private tryParseLabeledPair (msg: string) =
    let lines =
      msg.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    let findPrefixed (prefixes: string list) =
      lines
      |> Array.tryPick (fun l ->
        let t = l.TrimStart()
        prefixes
        |> List.tryPick (fun prefix ->
          match t.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith(prefix + " :", StringComparison.OrdinalIgnoreCase) with
          | true ->
            let idx = t.IndexOf(':')
            match idx >= 0 with
            | true -> Some (t.Substring(idx + 1).Trim())
            | false -> None
          | false -> None))
    match findPrefixed ["expected"], findPrefixed ["actual"; "but was"] with
    | Some e, Some a -> Some (cleanValue e, cleanValue a)
    | _ -> None

  let private comparisonPattern =
    Regex(
      @"Expected (?:actual to be |string to contain )(.+?),?\s+(?:but was|was)\s+(.+?)\.?\s*$",
      RegexOptions.Compiled ||| RegexOptions.Singleline)

  let private lengthPattern =
    Regex(@"Expected length\s+(\S+),\s*was\s+(\S+?)\.?\s*$", RegexOptions.Compiled)

  let private fsCheckPattern =
    Regex(
      @"Falsifiable.*?Original:\s*\n(.+?)(?:\nShrunk:\s*\n(.+?))?$",
      RegexOptions.Compiled ||| RegexOptions.Singleline)

  /// Parse Expecto comparison prose: "Expected actual to be greater than X, but was Y"
  let private tryParseComparisonProse (msg: string) =
    let lines = msg.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    lines
    |> Array.tryPick (fun line ->
      let m = comparisonPattern.Match(line.Trim())
      match m.Success with
      | true -> Some (cleanValue m.Groups.[1].Value, cleanValue m.Groups.[2].Value)
      | false ->
        let ml = lengthPattern.Match(line.Trim())
        match ml.Success with
        | true -> Some (sprintf "length %s" (ml.Groups.[1].Value), cleanValue ml.Groups.[2].Value)
        | false -> None)

  /// Parse FsCheck "Falsifiable" messages with counterexamples
  let private tryParseFsCheck (msg: string) =
    let m = fsCheckPattern.Match(msg)
    match m.Success with
    | true ->
      let original = m.Groups.[1].Value.Trim()
      let shrunk =
        match m.Groups.[2].Success with
        | true -> m.Groups.[2].Value.Trim()
        | false -> ""
      match String.IsNullOrEmpty shrunk with
      | true -> Some (original, "(falsified)")
      | false -> Some (original, sprintf "(shrunk to %s)" shrunk)
    | false -> None

  /// Parse assertion diffs from all major .NET test frameworks:
  /// Expecto, xUnit, NUnit (Expected/But was), FsCheck (Falsifiable)
  let tryParseAssertionDiff (msg: string) =
    match String.IsNullOrWhiteSpace msg with
    | true -> None
    | false ->
      match tryParseLabeledPair msg with
      | Some pair -> Some pair
      | None ->
        match tryParseComparisonProse msg with
        | Some pair -> Some pair
        | None -> tryParseFsCheck msg

  let firstUserFrame (trace: string) =
    let lines =
      trace.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    lines
    |> Array.tryFind (fun l ->
      l.Contains(".fs:line") || l.Contains(".cs:line"))
    |> Option.defaultWith (fun () ->
      lines |> Array.tryHead |> Option.defaultValue "")
    |> fun s -> s.Trim()

  let fromTestFailure (f: TestFailure) =
    match f with
    | TestFailure.AssertionFailed msg ->
      match tryParseAssertionDiff msg with
      | Some(e, a) -> FailurePresentation.AssertionDiff(e, a)
      | None -> FailurePresentation.RawMessage msg
    | TestFailure.ExceptionThrown(msg, st) ->
      FailurePresentation.ExceptionMessage(msg, firstUserFrame st)
    | TestFailure.TimedOut after ->
      FailurePresentation.Timeout after

  let inlineLabel maxLen (fp: FailurePresentation) =
    let raw =
      match fp with
      | FailurePresentation.AssertionDiff(e, a) ->
        sprintf "expected %s, got %s" e a
      | FailurePresentation.ExceptionMessage(m, _) -> m
      | FailurePresentation.Timeout t ->
        sprintf "timed out after %ds" (int t.TotalSeconds)
      | FailurePresentation.RawMessage m -> m
    match raw.Length <= maxLen with
    | true -> raw
    | false -> raw.Substring(0, maxLen - 1) + "…"

module AnnotationFreshness =
  let fromPhaseAndResult
    (phase: TestRunPhase)
    (status: TestRunStatus)
    =
    match status with
    | TestRunStatus.Running
    | TestRunStatus.Queued -> AnnotationFreshness.Running
    | TestRunStatus.Stale -> AnnotationFreshness.Stale
    | _ ->
      match phase with
      | TestRunPhase.RunningButEdited _ -> AnnotationFreshness.Stale
      | _ -> AnnotationFreshness.Current

module TestCodeLens =
  let label (status: TestRunStatus) (name: string) =
    match status with
    | TestRunStatus.Passed d ->
      sprintf "✓ %s (%.0fms)" name d.TotalMilliseconds
    | TestRunStatus.Failed(f, d) ->
      let msg =
        match f with
        | TestFailure.AssertionFailed m -> m
        | TestFailure.ExceptionThrown(m, _) -> m
        | TestFailure.TimedOut t ->
          sprintf "timed out after %ds" (int t.TotalSeconds)
      let t =
        match msg.Length > 60 with
        | true -> msg.Substring(0, 59) + "…"
        | false -> msg
      sprintf "✗ %s: %s (%.0fms)" name t d.TotalMilliseconds
    | TestRunStatus.Running ->
      sprintf "● %s running…" name
    | TestRunStatus.Detected -> sprintf "◆ %s" name
    | TestRunStatus.Queued -> sprintf "◆ %s (queued)" name
    | TestRunStatus.Skipped r -> sprintf "○ %s: %s" name r
    | TestRunStatus.Stale -> sprintf "~ %s (stale)" name
    | TestRunStatus.PolicyDisabled ->
      sprintf "○ %s (disabled)" name

  let defaultCommand = function
    | TestRunStatus.Failed _ -> CodeLensCommand.DebugTest
    | _ -> CodeLensCommand.RunTest

module FileAnnotations =
  let empty path =
    { FilePath = path
      TestAnnotations = [||]
      CoverageAnnotations = [||]
      InlineFailures = [||]
      CodeLenses = [||]
      PerformanceAnnotations = [||] }

  // NOTE: coverageViewsForFile is defined after the CoverageView types
  // below to avoid a forward reference. See FileAnnotations.coverageViewsForFile
  // at the end of the CoverageView section.

  let statusPriority = function
    | TestRunStatus.Failed _ -> 0
    | TestRunStatus.Running -> 1
    | TestRunStatus.Stale -> 2
    | TestRunStatus.Skipped _ -> 3
    | TestRunStatus.Detected -> 4
    | TestRunStatus.Queued -> 5
    | TestRunStatus.Passed _ -> 6
    | TestRunStatus.PolicyDisabled -> 7

  /// Build a lookup from (file, line) → (endLine, endColumn) from sequence points.
  /// When multiple SPs exist for the same line, picks the widest range.
  let private buildRangeLookup (maps: InstrumentationMap seq) : Map<string * int, int * int> =
    maps
    |> Seq.collect (fun m -> m.Slots)
    |> Seq.filter SequencePoint.hasRange
    |> Seq.groupBy (fun sp -> sp.File, sp.Line)
    |> Seq.map (fun (key, sps) ->
      let best = sps |> Seq.maxBy (fun sp -> sp.EndLine, sp.EndColumn)
      key, (best.EndLine, best.EndColumn))
    |> Map.ofSeq

  let project
    (filePath: string)
    (depGraph: TestDependencyGraph option)
    (state: LiveTestState)
    =
    let entries = LiveTestState.orderedStatusEntries state
    // `state` belongs wholly to one session (see `LiveTestState.ownerSessionId`).
    let owningPhase =
      match LiveTestState.ownerSessionId state with
      | Some sid -> state.RunPhases |> Map.tryFind sid |> Option.defaultValue Idle
      | None -> if TestRunPhase.isAnyRunning state.RunPhases then RunningButEdited RunGeneration.zero else Idle
    let fileEntries =
      entries
      |> Array.choose (fun e ->
        match e.Origin with
        | TestOrigin.SourceMapped(f, line) when f = filePath ->
          Some(e, line)
        | _ -> None)
    let testAnnotations =
      fileEntries
      |> Array.groupBy (fun (_, line) -> line)
      |> Array.map (fun (line, entries) ->
        let worst =
          entries
          |> Array.sortBy (fun (e, _) -> statusPriority e.Status)
          |> Array.head
          |> fst
        { Line = line
          TestId = worst.TestId
          DisplayName = worst.DisplayName
          Status = worst.Status
          Freshness = AnnotationFreshness.fromPhaseAndResult owningPhase worst.Status })
      |> Array.sortBy (fun a -> a.Line)
    let codeLenses =
      fileEntries
      |> Array.map (fun (e, line) ->
        { Line = line
          Label = TestCodeLens.label e.Status e.DisplayName
          TestId = e.TestId
          Command = TestCodeLens.defaultCommand e.Status })
      |> Array.sortBy (fun c -> c.Line)
    let inlineFailures =
      fileEntries
      |> Array.choose (fun (e, line) ->
        match e.Status with
        | TestRunStatus.Failed(failure, duration) ->
          Some
            { Line = line
              TestId = e.TestId
              TestName = e.DisplayName
              Failure =
                FailurePresentation.fromTestFailure failure
              Duration = duration }
        | _ -> None)
      |> Array.sortBy (fun f -> f.Line)
    let coverageAnnotations =
      state.CoverageAnnotations
      |> Array.filter (fun ca -> ca.FilePath = filePath)
      |> Array.map (fun ca ->
        { Line = ca.DefinitionLine
          EndLine = 0
          EndColumn = 0
          Detail = ca.Status
          CoveringTestIds =
            match depGraph with
            | None -> [||]
            | Some g ->
              match Map.tryFind ca.Symbol g.SymbolToTests with
              | Some ids -> ids
              | None -> [||]
          BranchCoverage = None })
      |> Array.sortBy (fun c -> c.Line)
    { FilePath = filePath
      TestAnnotations = testAnnotations
      CoverageAnnotations = coverageAnnotations
      InlineFailures = inlineFailures
      CodeLenses = codeLenses
      PerformanceAnnotations = [||] }

  /// Synthesize coverage annotations from dep graph + analysis cache when
  /// no explicit CoverageUpdated events have been dispatched.
  let private synthesizeCoverage
    (filePath: string)
    (analysisCache: FileAnalysisCache)
    (depGraph: TestDependencyGraph)
    (lastResults: Map<TestId, TestRunResult>)
    : CoverageAnnotation array =
    match Map.tryFind filePath analysisCache.FileSymbols with
    | None -> [||]
    | Some refs ->
      refs
      |> List.choose (fun ref ->
        match Map.tryFind ref.SymbolFullName depGraph.SymbolToTests with
        | None -> None
        | Some [||] -> None
        | Some testIds ->
          let passCount = testIds |> Array.filter (fun tid -> match Map.tryFind tid lastResults with | Some { Result = TestResult.Passed _ } -> true | _ -> false) |> Array.length
          let failCount = testIds |> Array.filter (fun tid -> match Map.tryFind tid lastResults with | Some { Result = TestResult.Failed _ } -> true | _ -> false) |> Array.length
          let health = if failCount > 0 then CoverageHealth.SomeFailing else CoverageHealth.AllPassing
          let status = if passCount + failCount > 0 then CoverageStatus.Covered(passCount + failCount, health) else CoverageStatus.Pending
          Some { Symbol = ref.SymbolFullName; FilePath = filePath; DefinitionLine = ref.Line; Status = status; BranchCoverage = BranchCoverage.Unknown })
      |> Array.ofList

  /// Project file annotations with coverage synthesized from the dependency graph.
  /// Use this instead of `project` when the full cycle state is available.
  let projectWithCoverage (filePath: string) (cycleState: LiveTestCycleState) : FileAnnotations =
    let depGraph = Some cycleState.DepGraph
    let base' = project filePath depGraph cycleState.TestState
    let allMaps = cycleState.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
    let lineCovMap = CoverageBitmap.computeLineCoverageForFile filePath allMaps cycleState.TestState.TestCoverageBitmaps
    let rangeLookup = buildRangeLookup allMaps
    let enrichWithRange (ca: CoverageLineAnnotation) =
      match Map.tryFind (filePath, ca.Line) rangeLookup with
      | Some (endL, endC) -> { ca with EndLine = endL; EndColumn = endC }
      | None -> ca
    let enrichWithBranch (anns: CoverageLineAnnotation array) =
      anns |> Array.map (fun ca ->
        let ca' = enrichWithRange ca
        match Map.tryFind ca'.Line lineCovMap with
        | Some lc -> { ca' with BranchCoverage = Some lc }
        | None -> ca')
    match base'.CoverageAnnotations.Length > 0 with
    | true ->
      { base' with CoverageAnnotations = enrichWithBranch base'.CoverageAnnotations }
    | false ->
      let synthesized = synthesizeCoverage filePath cycleState.AnalysisCache cycleState.DepGraph cycleState.TestState.LastResults
      let coverageLineAnnotations =
        synthesized
        |> Array.map (fun ca ->
          let endLine, endColumn =
            match Map.tryFind (filePath, ca.DefinitionLine) rangeLookup with
            | Some (el, ec) -> el, ec
            | None -> 0, 0
          { Line = ca.DefinitionLine
            EndLine = endLine
            EndColumn = endColumn
            Detail = ca.Status
            CoveringTestIds =
              match Map.tryFind ca.Symbol cycleState.DepGraph.SymbolToTests with
              | Some ids -> ids
              | None -> [||]
            BranchCoverage = Map.tryFind ca.DefinitionLine lineCovMap })
        |> Array.sortBy (fun c -> c.Line)
      { base' with CoverageAnnotations = coverageLineAnnotations }

  /// Resolve a file path query to a full path by checking test sources first,
  /// then production code files from instrumentation maps.
  let resolveFilePath (fileParam: string) (statusEntries: TestStatusEntry array) (instrMaps: Map<string, InstrumentationMap array>) : string option =
    let matchesParam (f: string) =
      f = fileParam
      || f.EndsWith(fileParam, System.StringComparison.OrdinalIgnoreCase)
      || f.EndsWith(
           System.IO.Path.DirectorySeparatorChar.ToString() + fileParam,
           System.StringComparison.OrdinalIgnoreCase)
    let testFile =
      statusEntries
      |> Array.choose (fun e ->
        match e.Origin with
        | TestOrigin.SourceMapped (f, _) -> Some f
        | _ -> None)
      |> Array.distinct
      |> Array.tryFind matchesParam
    match testFile with
    | Some _ -> testFile
    | None ->
      instrMaps
      |> Map.values |> Seq.collect id
      |> Seq.collect (fun m -> m.Slots |> Array.map (fun s -> s.File))
      |> Seq.distinct
      |> Seq.tryFind matchesParam

// --- CoverageView: render-budget-aware projection of coverage data ---
// WHY — exists because the existing pipeline hands every editor a raw
// TestId array per symbol and each editor invents its own (different) inline
// rendering policy. The three editors currently render 20-100 test names as
// inline text on the function definition, which is unusable. This module is the
// shared constraining layer: callers ask for a "view" of one symbol's
// coverage, and we hand back a budget-bounded projection that all three
// editors render uniformly. The single user-tunable knob is the collapse
// threshold; defaults are F#-friendly: many tests per function is the norm,
// so the default collapse threshold is Int32.MaxValue (no auto-collapse).
// Performance: every operation is a single fold over the covering set; no
// Seq, no LINQ, no mutable state. The hot path (toInlineBadge) uses
// pre-computed constant fragments. The intent is that this projection runs
// on hover and CodeLens request, not on the keystroke path.

/// The overflow state of a CoverageView. NOT a bool — the renderer needs
/// the exact "hidden" count to render "▾ +N more", not just a flag.
[<RequireQualifiedAccess>]
type Overflow =
  | Within
  | Overflow of hidden: int

module Overflow =
  /// Compute Overflow from the total covering count and the collapse
  /// threshold. Pure; no state.
  let fromTotal (collapseAt: int) (total: int) : Overflow =
    let hidden = total - collapseAt
    if hidden > 0 then Overflow.Overflow hidden
    else Overflow.Within

/// Bounded status-count tuple. Rendered inline as a single line of text.
/// DU with exactly 5 cases — the renderer is allowed to assume a strict
/// upper bound of 5 (one per status kind).
[<RequireQualifiedAccess>]
type CoverageBadge =
  | Pass of count: int
  | Fail of count: int
  | Running of count: int
  | Stale of count: int
  | Skipped of count: int

/// Honest health indicator. NOT a bool — preserves the 5 status kinds
/// so the renderer can show the exact problem (e.g. a Stale test is
/// not the same as a Passing test, and the user must see the
/// difference).
[<RequireQualifiedAccess>]
type CoverageViewState =
  | Passing
  | Failing
  | Running
  | Stale
  | Skipped
  | Absent

module CoverageViewState =
  /// Derive State from the dominant status among covering tests.
  /// Priority: Failing > Running > Stale > Skipped > Passing.
  /// WHY — priority order matches the user's "navigate to failing
  /// tests" intent: any failing test makes the whole function Failing.
  let fromCounts
    (passing: int)
    (failing: int)
    (running: int)
    (stale: int)
    (skipped: int)
    : CoverageViewState =
    match passing + failing + running + stale + skipped with
    | 0 -> CoverageViewState.Absent
    | _ ->
      if failing > 0 then CoverageViewState.Failing
      else if running > 0 then CoverageViewState.Running
      else if stale > 0 then CoverageViewState.Stale
      else if skipped > 0 then CoverageViewState.Skipped
      else CoverageViewState.Passing

/// User-tunable view configuration. Only the one field that matters for
/// the F#-heavy-function case lives here. Picker concerns (group, sort,
/// filter) belong in the picker, not in the data model — YAGNI.
type CoverageViewMode = {
  /// Inline names only when total covering tests < this number.
  /// Default Int32.MaxValue (always inline) because F# users have many
  /// tests per function; auto-collapsing would punish their style.
  InlineCollapseAt: int
}

module CoverageViewMode =
  /// F#-friendly default: no auto-collapse. Users tune via editor
  /// config (settings.json / vim.g / Options page) without code changes.
  let defaults = { InlineCollapseAt = Int32.MaxValue }

/// Per-symbol aggregate. The single thing an editor renders by default.
type CoverageView = {
  /// Symbol fully-qualified name.
  Symbol: string
  /// Source file containing the symbol.
  FilePath: string
  /// 1-based line of the symbol's definition.
  DefinitionLine: int
  /// Total covering tests. 0 means "absent" (no decoration).
  TotalCount: int
  /// Overflow indicator — NOT a bool. The renderer gets the exact
  /// hidden count to render "▾ +N more".
  Overflow: Overflow
  /// Pre-formatted one-line badge text (e.g. "✓ 97 ✗ 3"). Computed
  /// once at projection; the editor renders this as one line of
  /// virtual text or one CodeLens. The render budget is HARD: one
  /// line, always.
  InlineBadgeText: string
  /// Honest health indicator — preserves the 5 status kinds.
  Health: CoverageViewState
}

module CoverageView =
  /// Format a single badge kind as a short string fragment. Pure
  /// function — JIT folds the bounded 5-case DU into a switch.
  let formatBadge (badge: CoverageBadge) : string =
    match badge with
    | CoverageBadge.Pass n -> "✓ " + string n
    | CoverageBadge.Fail n -> "✗ " + string n
    | CoverageBadge.Running n -> "⟳ " + string n
    | CoverageBadge.Stale n -> "~ " + string n
    | CoverageBadge.Skipped n -> "⊘ " + string n

  /// Format the inline badge as a single short line. PURE: no
  /// StringBuilder, no mutable, no concat-inside-loop. One fold over
  /// the bounded list. Even at the worst case of 5 badges the output
  /// is < 16 chars.
  let toInlineBadge (badges: CoverageBadge list) : string =
    badges
    |> List.fold (fun (acc, first) b ->
      let prefix = if first then "" else " "
      (acc + prefix + formatBadge b, false)) ("", true)
    |> fst

  /// Project one symbol's coverage into a typed CoverageView. PURE:
  /// no I/O, no mutable, no closures over state. The coveringIds
  /// argument is the pre-resolved set of TestIds that cover this
  /// symbol — caller looks it up so this function stays a pure
  /// projection. Returns TotalCount=0 (not None/Option) when nothing
  /// covers the symbol.
  let project
    (mode: CoverageViewMode)
    (coveringIds: TestId array)
    (depGraph: TestDependencyGraph)
    (state: LiveTestState)
    (file: string)
    (line: int)
    (symbol: string)
    : CoverageView =
    let total = Array.length coveringIds
    if total = 0 then
      { Symbol = symbol
        FilePath = file
        DefinitionLine = line
        TotalCount = 0
        Overflow = Overflow.Within
        InlineBadgeText = ""
        Health = CoverageViewState.Absent }
    else
      // Single fold over the covering set, partitioning into status
      // counts. No mutable state; the fold accumulates a record.
      let init : int * int * int * int * int = (0, 0, 0, 0, 0)
      let (passing, failing, running, stale, skipped) =
        coveringIds
        |> Array.fold (fun (p, f, r, s, k) tid ->
          match Map.tryFind tid state.LastResults with
          | Some result ->
            match result.Result with
            | TestResult.Passed _ -> (p + 1, f, r, s, k)
            | TestResult.Failed _ -> (p, f + 1, r, s, k)
            | TestResult.Skipped _ -> (p, f, r, s, k + 1)
            | TestResult.NotRun -> (p, f, r, s + 1, k) // NotRun = stale: result not current
            | TestResult.NoResult _ -> (p, f, r, s + 1, k) // never reported = no current result
          | None -> (p, f, r, s + 1, k)) // No result yet = stale: not yet run
          init
      // Build the badge list. Stable order: Pass, Fail, Running, Stale, Skipped.
      // Empty entries are omitted so the inline text stays short.
      let badges =
        [ if passing > 0 then CoverageBadge.Pass passing
          if failing > 0 then CoverageBadge.Fail failing
          if running > 0 then CoverageBadge.Running running
          if stale > 0 then CoverageBadge.Stale stale
          if skipped > 0 then CoverageBadge.Skipped skipped ]
      { Symbol = symbol
        FilePath = file
        DefinitionLine = line
        TotalCount = total
        Overflow = Overflow.fromTotal mode.InlineCollapseAt total
        InlineBadgeText = toInlineBadge badges
        Health = CoverageViewState.fromCounts passing failing running stale skipped }

// Forward-declared helper: produces one CoverageView per
// CoverageAnnotation for the file. Used by the SSE publisher to emit
// one coverage_view event per symbol. Pure function.
module FileAnnotationsInternals =
  let projectViewsForFile
    (mode: CoverageViewMode)
    (file: string)
    (depGraph: TestDependencyGraph)
    (state: LiveTestState)
    : CoverageView array =
    let annotations =
      state.CoverageAnnotations
      |> Array.filter (fun ca -> ca.FilePath = file)
    if Array.isEmpty annotations then [||]
    else
      annotations
      |> Array.map (fun ca ->
        let coveringIds =
          match Map.tryFind ca.Symbol depGraph.SymbolToTests with
          | Some arr -> arr
          | None -> [||]
        CoverageView.project mode coveringIds depGraph state file ca.DefinitionLine ca.Symbol)

// --- Test Run Explainer (MCP "explain_test_run" / "query_test_coverage") ---

[<RequireQualifiedAccess>]
type TestTriggerReason =
  | SymbolCoverage of symbols: string list
  | NewTest
  | ExplicitRun
  | UnknownCoverage

type TestRunExplanation = {
  TestId: TestId
  DisplayName: string
  Reason: TestTriggerReason
  CoveringSymbols: string list
  Trigger: RunTrigger
  DurationMs: float option
  FlakyClassification: FlakyClassification
}

type RunCycleExplanation = {
  ChangedSymbols: string list
  Trigger: RunTrigger
  AffectedTests: TestRunExplanation array
  FilteredOutByPolicy: TestId array
  TotalDiscovered: int
}

module TestRunExplainer =

  let private durationMs (r: TestResult) =
    match r with
    | TestResult.Passed d -> Some d.TotalMilliseconds
    | TestResult.Failed (_, d) -> Some d.TotalMilliseconds
    | TestResult.Skipped _ | TestResult.NotRun | TestResult.NoResult _ -> None

  let explainTest
    (graph: TestDependencyGraph)
    (lastResults: Map<TestId, TestRunResult>)
    (flakyHistory: Map<TestId, ResultWindow>)
    (changedSymbols: string list)
    (trigger: RunTrigger)
    (test: TestCase)
    : TestRunExplanation =
    let coveringSymbols =
      changedSymbols
      |> List.filter (fun sym ->
        match Map.tryFind sym graph.TransitiveCoverage with
        | Some testIds -> testIds |> Array.contains test.Id
        | None -> false)
    let reason =
      match coveringSymbols with
      | _ :: _ -> TestTriggerReason.SymbolCoverage coveringSymbols
      | [] ->
        match Map.tryFind test.Id lastResults with
        | None -> TestTriggerReason.NewTest
        | Some _ -> TestTriggerReason.UnknownCoverage
    let dur =
      Map.tryFind test.Id lastResults
      |> Option.bind (fun r -> durationMs r.Result)
    let classification =
      FlakyDetection.classifyFlakiness test.Id flakyHistory lastResults
    { TestId = test.Id
      DisplayName = test.DisplayName
      Reason = reason
      CoveringSymbols = coveringSymbols
      Trigger = trigger
      DurationMs = dur
      FlakyClassification = classification }

  let explainSymbolChange
    (graph: TestDependencyGraph)
    (discoveredTests: TestCase array)
    (lastResults: Map<TestId, TestRunResult>)
    (flakyHistory: Map<TestId, ResultWindow>)
    (policies: Map<TestCategory, RunPolicy>)
    (changedSymbols: string list)
    (trigger: RunTrigger)
    : RunCycleExplanation =
    let affectedIds =
      TestDependencyGraph.findAffected changedSymbols graph |> Set.ofArray
    let affectedTests =
      discoveredTests
      |> Array.filter (fun tc -> affectedIds.Contains tc.Id)
    let filtered =
      PolicyFilter.filterTests policies trigger affectedTests
    let filteredSet = filtered |> Array.map (fun tc -> tc.Id) |> Set.ofArray
    let filteredOut =
      affectedTests
      |> Array.filter (fun tc -> not (filteredSet.Contains tc.Id))
      |> Array.map (fun tc -> tc.Id)
    let explanations =
      filtered
      |> Array.map (explainTest graph lastResults flakyHistory changedSymbols trigger)
    { ChangedSymbols = changedSymbols
      Trigger = trigger
      AffectedTests = explanations
      FilteredOutByPolicy = filteredOut
      TotalDiscovered = discoveredTests.Length }

  let queryTestCoverage
    (graph: TestDependencyGraph)
    (discoveredTests: TestCase array)
    (lastResults: Map<TestId, TestRunResult>)
    (symbol: string)
    : CoveringTestInfo array =
    match Map.tryFind symbol graph.TransitiveCoverage with
    | None -> [||]
    | Some testIds ->
      let testMap = discoveredTests |> Array.map (fun tc -> tc.Id, tc) |> Map.ofArray
      testIds
      |> Array.choose (fun tid ->
        match Map.tryFind tid testMap with
        | Some tc ->
          Some { TestId = tid
                 DisplayName = tc.DisplayName
                 Result = Map.tryFind tid lastResults |> Option.map (fun r -> r.Result) }
        | None -> None)

type SessionInvariantViolation = {
  Message: string
  RunPhaseKeys: Set<string>
  InstrumentationMapKeys: Set<string>
}

module SessionInvariant =
  /// Validates steady-state consistency of per-session maps.
  /// Returns None during initialization (when either map is empty).
  let validate (state: LiveTestState) (instrMaps: Map<string, InstrumentationMap array>) : SessionInvariantViolation option =
    let runPhaseKeys = state.RunPhases |> Map.keys |> Set.ofSeq
    let instrMapKeys = instrMaps |> Map.keys |> Set.ofSeq
    match Set.isEmpty runPhaseKeys || Set.isEmpty instrMapKeys with
    | true -> None
    | false ->
      match runPhaseKeys <> instrMapKeys with
      | true ->
        Some {
          Message = sprintf "Session key mismatch: RunPhases has %A, InstrumentationMaps has %A" runPhaseKeys instrMapKeys
          RunPhaseKeys = runPhaseKeys
          InstrumentationMapKeys = instrMapKeys
        }
      | false -> None
