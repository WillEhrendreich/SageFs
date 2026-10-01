namespace SageFs.Features.LiveTesting

open SageFs
open System
open System.IO
open System.Numerics
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open SageFs.Measures

// The live-testing cycle: what a type-check decides, the debounce, the effects it asks for and the
// per-session cycle state. Daemon-only. The isolated FSI host compiles LiveTestingTypes.fs and never
// this file, so what lives here costs the host nothing.

type AfterTypeCheckOutcome = {
  Decision: LiveTestingDecision option
  Effects: TestCycleEffect list
}

/// What a type-check learned about the file that was edited.
///
/// These are two different questions and conflating them shipped a bug that
/// reported green on a real regression. `Changed` is the symbol-NAME delta —
/// `SymbolDiff.computeChanges`, a `Set<string>` difference. Rewrite
/// `let add a b = a + b` to `a - b` and the name set is identical, so
/// `Changed` is EMPTY: a set difference cannot see inside a body. `InFile` is
/// every symbol the type-check resolved in that file, which does not move when
/// a body does, and is therefore what makes a body-only edit selectable at all.
///
/// They are one record rather than two `string list` parameters on purpose: as
/// adjacent parameters of the same type, a transposed call site would compile
/// silently and select the wrong tests.
type FileSymbolDelta = {
  /// Symbols whose NAME appeared or disappeared. Empty for a body-only edit.
  Changed: string list
  /// Every symbol the type-check resolved in this file, changed or not.
  InFile: string list
  /// Which lines the edit changed against the text the assembly was compiled from.
  /// `NoBaseline` when that text is not known, which keeps selection as wide as before.
  Lines: ChangedLines
}

module FileSymbolDelta =
  /// For call sites that genuinely have no file-scope information. Selection
  /// then degrades to the old name-only behavior with the conservative floor
  /// still in force, rather than pretending the file declares nothing.
  let ofChangedOnly (changed: string list) : FileSymbolDelta =
    { Changed = changed; InFile = []; Lines = ChangedLines.NoBaseline }

module TestCycleEffects =
  let fromTick
    (tsPayload: string option)
    (fcsPayload: string option)
    (latestContent: string option)
    (latestAnalysisIdentity: AnalysisIdentity option)
    (filePath: string)
    (lastTiming: TestCycleTiming option)
    : TestCycleEffect list =
    [ match tsPayload with
      | Some content -> TestCycleEffect.ParseTreeSitter(content, filePath)
      | None -> ()
      match fcsPayload with
      | Some fp ->
        let tsElapsed = TestCycleTiming.accumulatedTsElapsed lastTiming
        TestCycleEffect.RequestFcsTypeCheck {
          SessionId = None
          FilePath = fp
          Content = latestContent
          AnalysisIdentity = latestAnalysisIdentity
          TreeSitterElapsed = tsElapsed
        }
      | None -> () ]

  let decideAfterTypeCheck
    (symbols: FileSymbolDelta)
    (changedFilePath: string)
    (trigger: RunTrigger)
    (depGraph: TestDependencyGraph)
    (state: LiveTestState)
    (lastTiming: TestCycleTiming option)
    (instrumentationMaps: Map<string, InstrumentationMap array>)
    : AfterTypeCheckOutcome =
    match state.Activation = LiveTestingActivation.Inactive with
    | true -> { Decision = None; Effects = [] }
    | false ->
      let changedSymbols = symbols.Changed
      let cause = TestCycleOrchestrator.causeFromTrigger changedFilePath trigger
      let symbolAffected = TestDependencyGraph.findAffected changedSymbols depGraph
      // Compute coverage-affected across all sessions' maps
      let hasMaps = not (Map.isEmpty instrumentationMaps)
      let hasBitmaps = not (Map.isEmpty state.TestCoverageBitmaps)
      let coverageAffected =
        match hasMaps && hasBitmaps with
        | true ->
          instrumentationMaps
          |> Map.toArray
          |> Array.collect (fun (_, maps) ->
            CoverageBitmap.findCoverageAffected changedFilePath maps state.TestCoverageBitmaps)
        | false -> [||]
      let symbolAffectedSet = Set.ofArray symbolAffected
      let nameDeltaAffected =
        Array.append symbolAffected coverageAffected |> Array.distinct
      // A body-only edit (`a + b` -> `a - b`) moves no symbol NAMES, so
      // `symbols.Changed` is empty and nothing above selects anything. Select
      // on what the file CONTAINS instead: every test reaching a symbol this
      // type-check resolved in the file. Every trigger: a half-typed buffer
      // takes the `Failed` branch, and `PolicyFilter` below keeps a save-only
      // category quiet per keystroke. If nothing is found, the floor below.
      let fileScopeAffected =
        match
          Array.isEmpty nameDeltaAffected
          && List.isEmpty changedSymbols
          && not (List.isEmpty symbols.InFile)
        with
        | true -> TestDependencyGraph.findAffected symbols.InFile depGraph
        | false -> [||]
      let usedFileScope = not (Array.isEmpty fileScopeAffected)
      let affected =
        Array.append nameDeltaAffected fileScopeAffected |> Array.distinct
      let isCompiledFile =
        changedFilePath.EndsWith(".fs", System.StringComparison.OrdinalIgnoreCase)
        && not (changedFilePath.EndsWith(".fsx", System.StringComparison.OrdinalIgnoreCase))
      // NO-EMPTY-ESCAPE floor, shared with the landing gate
      // (`AffectedTests.verificationTestSet`): if every narrow above found
      // nothing for a compiled file, widen to ALL discovered tests on any
      // trigger. Nothing found is not "nothing affected": the name delta cannot
      // see a body rewrite, and the graph may not have seen the covering test
      // yet. Run policy still decides whether a keystroke runs. Scripts (.fsx)
      // are evaluated, not compiled: no floor.
      let symbolsChanged = not (List.isEmpty changedSymbols)
      let shouldFallback = Array.isEmpty affected && isCompiledFile
      let effectiveAffected,
          precision,
          reason =
        match shouldFallback with
        | true ->
          state.DiscoveredTests |> Array.map (fun tc -> tc.Id),
          SelectionPrecision.ConservativeFallback,
          // Mechanism-neutral (Brief 4): redirectToEvalBuffer may retarget
          // this into an FSI eval, not always a rebuild.
          match symbolsChanged with
          | true -> "The dependency graph could not narrow this compiled-file change, so SageFs conservatively queued all discovered tests for a fresh run."
          | false -> "No test the dependency graph knows reaches this compiled file, and it may not have seen the test that does, so SageFs queued all discovered tests rather than read an empty selection as green."
        | false when usedFileScope ->
          affected,
          SelectionPrecision.ConservativeFallback,
          "This edit changed no symbol names, which a name-only comparison cannot tell apart from a rewritten function body, so SageFs selected every test that reaches a symbol this file declares."
        | false when Array.isEmpty affected ->
          [||],
          SelectionPrecision.NoImpactedTests,
          "No semantically affected tests were identified for this change."
        | false when Array.isEmpty coverageAffected ->
          affected,
          SelectionPrecision.ExactDependencyMatch,
          "Changed symbols mapped directly to impacted tests in the dependency graph."
        | false when Array.forall (fun testId -> Set.contains testId symbolAffectedSet) coverageAffected ->
          affected,
          SelectionPrecision.ExactDependencyMatch,
          "Changed symbols mapped directly to impacted tests in the dependency graph."
        | false when Array.isEmpty symbolAffected ->
          affected,
          SelectionPrecision.CoverageApproximation,
          "Coverage evidence widened the impacted set because the symbol graph alone could not explain this file change."
        | false ->
          affected,
          SelectionPrecision.CoverageApproximation,
          "Changed symbols found impacted tests and coverage evidence conservatively widened that set."
      match Array.isEmpty effectiveAffected with
      | true ->
        { Decision =
            Some (
              LiveTestingDecision.fromSelection
                cause
                precision
                changedSymbols
                [||]
                [||]
                reason)
          Effects = [] }
      | false ->
        let affectedSet = Set.ofArray effectiveAffected
        let affectedTests =
          state.DiscoveredTests
          |> Array.filter (fun tc -> affectedSet.Contains tc.Id)
        let filtered =
          let allMaps =
            instrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
          let coverageWeights =
            CoverageBitmap.computeCoverageWeights [changedFilePath] allMaps state.TestCoverageBitmaps
          let flakyClassifications =
            affectedTests
            |> Array.choose (fun tc ->
              let c = FlakyDetection.classifyFlakiness tc.Id state.FlakyHistory state.LastResults
              match c with
              | FlakyClassification.Insufficient | FlakyClassification.Stable -> None
              | _ -> Some (tc.Id, c))
            |> Map.ofArray
          let ctx : PrioritizationContext = {
            LastResults = state.LastResults
            CoverageWeights = coverageWeights
            FlakyClassifications = flakyClassifications
          }
          PolicyFilter.filterTests state.RunPolicies trigger affectedTests
          |> TestPrioritization.prioritizeWithContext ctx
        let deferred =
          let selectedSet = filtered |> Array.map (fun tc -> tc.Id) |> Set.ofArray
          affectedTests
          |> Array.filter (fun tc -> not (Set.contains tc.Id selectedSet))
          |> Array.map (fun tc -> tc.FullName)
        let decision =
          match Array.isEmpty filtered with
          | true ->
            LiveTestingDecision.fromSelection
              cause
              SelectionPrecision.SuppressedByPolicy
              changedSymbols
              [||]
              deferred
              "Affected tests were intentionally deferred by the current run policy, so ambient live testing stayed quiet on purpose."
          | false ->
            LiveTestingDecision.fromSelection
              cause
              precision
              changedSymbols
              (filtered |> Array.map (fun tc -> tc.FullName))
              deferred
              reason
        match Array.isEmpty filtered with
        | true ->
          { Decision = Some decision
            Effects = [] }
        | false ->
          let tsElapsed = TestCycleTiming.accumulatedTsElapsed lastTiming
          let fcsElapsed = TestCycleTiming.accumulatedFcsElapsed lastTiming
          // `state` belongs wholly to one session (see `LiveTestState.ownerSessionId`),
          // so every affected test in `filtered` targets that one session — no grouping needed.
          let targetSession = LiveTestState.ownerSessionId state
          let effects =
            match TestRunPhase.isSessionRunning targetSession state.RunPhases with
            | true -> []
            | false ->
              let sessionMaps =
                match targetSession |> Option.bind (fun s -> Map.tryFind s instrumentationMaps) with
                | Some maps -> maps
                | None -> instrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
              let isCompiled =
                changedFilePath.EndsWith(".fs", System.StringComparison.OrdinalIgnoreCase)
                && not (changedFilePath.EndsWith(".fsx", System.StringComparison.OrdinalIgnoreCase))
              let req = {
                Tests = filtered
                Trigger = trigger
                TreeSitterElapsed = tsElapsed
                FcsElapsed = fcsElapsed
                SessionId = targetSession
                InstrumentationMaps = sessionMaps
              }
              match isCompiled && trigger <> RunTrigger.Keystroke with
              | true -> [ TestCycleEffect.RequestRebuild(0L, req) ]
              | false -> [ TestCycleEffect.RunAffectedTests req ]
          { Decision = Some decision
            Effects = effects }

  /// Name-delta-only entry point: selection sees which symbol NAMES moved and
  /// nothing about what the file contains, so a body-only edit selects nothing
  /// through here. That is the pre-`FileSymbolDelta` behavior, kept because
  /// every caller is a test pinning a name-delta scenario. Production goes
  /// through `decideAfterTypeCheck` with the file's own symbol set.
  let afterTypeCheck
    (changedSymbols: string list)
    (changedFilePath: string)
    (trigger: RunTrigger)
    (depGraph: TestDependencyGraph)
    (state: LiveTestState)
    (lastTiming: TestCycleTiming option)
    (instrumentationMaps: Map<string, InstrumentationMap array>)
    : TestCycleEffect list =
    decideAfterTypeCheck
      (FileSymbolDelta.ofChangedOnly changedSymbols)
      changedFilePath trigger depGraph state lastTiming instrumentationMaps
    |> fun outcome -> outcome.Effects

  /// Redirects the compiled-DLL decision — `RunAffectedTests` (Keystroke) or
  /// `RequestRebuild` (FileSave/ExplicitRun) — into `EvalBufferThenRunAffected`
  /// for a compiled `.fs` file whose buffer content is known, so the edited
  /// buffer is eval'd into FSI instead of running/rebuilding the stale
  /// compiled DLL (live-testing-asyoutype-plan.md §2/Brief 4). Total, pure:
  /// a `.fsx`/expression file, or a compiled file with no known content
  /// (`content = None`), passes every effect through UNCHANGED — this never
  /// narrows or drops an effect, only retargets which mechanism runs it.
  let redirectToEvalBuffer
    (content: string option)
    (filePath: string)
    (effects: TestCycleEffect list)
    : TestCycleEffect list =
    let isCompiledFile =
      filePath.EndsWith(".fs", System.StringComparison.OrdinalIgnoreCase)
      && not (filePath.EndsWith(".fsx", System.StringComparison.OrdinalIgnoreCase))
    match isCompiledFile, content with
    | true, Some c ->
      effects
      |> List.map (fun effect ->
        match effect with
        | TestCycleEffect.RunAffectedTests req
        | TestCycleEffect.RequestRebuild (_, req) ->
          TestCycleEffect.EvalBufferThenRunAffected { FilePath = filePath; Content = c; Run = req }
        | other -> other)
    | _ -> effects

  let fallbackRebuildAfterFailedTypeCheck
    (filePath: string)
    (trigger: RunTrigger)
    (state: LiveTestState)
    (lastTiming: TestCycleTiming option)
    (instrumentationMaps: Map<string, InstrumentationMap array>)
    : TestCycleEffect list =
    let isCompiledFile =
      filePath.EndsWith(".fs", System.StringComparison.OrdinalIgnoreCase)
      && not (filePath.EndsWith(".fsx", System.StringComparison.OrdinalIgnoreCase))
    let isSaveOrExplicit =
      trigger = RunTrigger.FileSave || trigger = RunTrigger.ExplicitRun

    match isCompiledFile && isSaveOrExplicit with
    | false -> []
    | true ->
      let filtered =
        PolicyFilter.filterTests state.RunPolicies trigger state.DiscoveredTests
        |> TestPrioritization.prioritize state.LastResults
      match Array.isEmpty filtered with
      | true -> []
      | false ->
        let tsElapsed = TestCycleTiming.accumulatedTsElapsed lastTiming
        let fcsElapsed = TestCycleTiming.accumulatedFcsElapsed lastTiming
        // `state` belongs wholly to one session (see `LiveTestState.ownerSessionId`).
        let targetSession = LiveTestState.ownerSessionId state
        match TestRunPhase.isSessionRunning targetSession state.RunPhases with
        | true -> []
        | false ->
          let sessionMaps =
            match targetSession |> Option.bind (fun s -> Map.tryFind s instrumentationMaps) with
            | Some maps -> maps
            | None -> instrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
          [ TestCycleEffect.RequestRebuild(0L, {
              Tests = filtered
              Trigger = trigger
              TreeSitterElapsed = tsElapsed
              FcsElapsed = fcsElapsed
              SessionId = targetSession
              InstrumentationMaps = sessionMaps
            }) ]

/// Adaptive debounce configuration.
type AdaptiveDebounceConfig = {
  BaseTreeSitterMs: float<ms>
  BaseFcsMs: float<ms>
  MaxFcsMs: float<ms>
  BackoffMultiplier: float
  ResetAfterSuccessCount: int
}

module AdaptiveDebounceConfig =
  let defaults = {
    BaseTreeSitterMs = Timeouts.liveTestTreeSitterDebounce.TotalMilliseconds * 1.0<ms>
    BaseFcsMs = Timeouts.liveTestFcsDebounce.TotalMilliseconds * 1.0<ms>
    MaxFcsMs = Timeouts.liveTestFcsDebounceMax.TotalMilliseconds * 1.0<ms>
    BackoffMultiplier = 1.5
    ResetAfterSuccessCount = 3
  }

/// Tracks FCS cancellation history for adaptive backoff.
type AdaptiveDebounce = {
  Config: AdaptiveDebounceConfig
  ConsecutiveFcsCancels: int
  ConsecutiveFcsSuccesses: int
  CurrentFcsDelayMs: float<ms>
}

module AdaptiveDebounce =
  let create (config: AdaptiveDebounceConfig) = {
    Config = config
    ConsecutiveFcsCancels = 0
    ConsecutiveFcsSuccesses = 0
    CurrentFcsDelayMs = config.BaseFcsMs
  }

  let createDefault () = create AdaptiveDebounceConfig.defaults

  let onFcsCanceled (ad: AdaptiveDebounce) =
    let newCancels = ad.ConsecutiveFcsCancels + 1
    let newDelay =
      ad.CurrentFcsDelayMs * ad.Config.BackoffMultiplier
      |> min ad.Config.MaxFcsMs
    { ad with
        ConsecutiveFcsCancels = newCancels
        ConsecutiveFcsSuccesses = 0
        CurrentFcsDelayMs = newDelay }

  let onFcsCompleted (ad: AdaptiveDebounce) =
    let newSuccesses = ad.ConsecutiveFcsSuccesses + 1
    match newSuccesses >= ad.Config.ResetAfterSuccessCount with
    | true ->
      { ad with
          ConsecutiveFcsCancels = 0
          ConsecutiveFcsSuccesses = 0
          CurrentFcsDelayMs = ad.Config.BaseFcsMs }
    | false ->
      { ad with
          ConsecutiveFcsCancels = 0
          ConsecutiveFcsSuccesses = newSuccesses }

  let currentFcsDelay (ad: AdaptiveDebounce) = ad.CurrentFcsDelayMs

/// Represents a resolved symbol reference from FCS.
type SymbolReference = {
  SymbolFullName: string
  UseKind: SymbolUseKind
  UsedInTestId: TestId option
  FilePath: string
  Line: int
}

/// Builds SymbolToTests from a list of symbol references using the line-range heuristic
/// in buildFromSymbolUses. Converts SymbolReference to ExtractedSymbolUse first.
module SymbolGraphBuilder =
  let toExtractedSymbolUse (sr: SymbolReference) : ExtractedSymbolUse =
    let parts = sr.SymbolFullName.Split('.')
    let displayName = if parts.Length > 0 then parts[parts.Length - 1] else sr.SymbolFullName
    { FullName = sr.SymbolFullName
      DisplayName = displayName
      UseKind = sr.UseKind
      StartLine = sr.Line
      EndLine = sr.Line }

  let buildIndex (testModuleIdentifier: string) (framework: TestFramework) (refs: SymbolReference list) : Map<string, TestId array> =
    let extracted = refs |> List.map toExtractedSymbolUse |> Array.ofList
    let graph = TestDependencyGraph.buildFromSymbolUses testModuleIdentifier framework extracted
    graph.SymbolToTests

  let updateGraph (testModuleIdentifier: string) (framework: TestFramework) (newRefs: SymbolReference list) (filePath: string) (graph: TestDependencyGraph) : TestDependencyGraph =
    let extracted = newRefs |> List.map toExtractedSymbolUse |> Array.ofList
    let newFileIndex = buildIndex testModuleIdentifier framework newRefs
    let updatedPerFile = Map.add filePath newFileIndex graph.PerFileIndex
    let merged = TestDependencyGraph.mergePerFileIndexes updatedPerFile
    let callGraph = TestDependencyGraph.extractCallGraph testModuleIdentifier extracted
    { graph with
        SymbolToTests = merged
        TransitiveCoverage = TestDependencyGraph.computeTransitiveCoverage callGraph merged
        PerFileIndex = updatedPerFile
        SourceVersion = graph.SourceVersion + 1 }

/// Result of comparing two sets of symbols — separates added from removed.
type SymbolChanges = {
  Added: string list
  Removed: string list
}

module SymbolChanges =
  let empty = { Added = []; Removed = [] }
  let allChanged (sc: SymbolChanges) = sc.Added @ sc.Removed
  let isEmpty (sc: SymbolChanges) = List.isEmpty sc.Added && List.isEmpty sc.Removed

/// Detects which symbols changed between two FCS analysis runs.
module SymbolDiff =
  let computeChanges (previousSymbols: Set<string>) (currentSymbols: Set<string>) : SymbolChanges =
    { Added = Set.difference currentSymbols previousSymbols |> Set.toList
      Removed = Set.difference previousSymbols currentSymbols |> Set.toList }

  let fromRefs (previousRefs: SymbolReference list) (currentRefs: SymbolReference list) : SymbolChanges =
    let prevSyms = previousRefs |> List.map (fun r -> r.SymbolFullName) |> Set.ofList
    let currSyms = currentRefs |> List.map (fun r -> r.SymbolFullName) |> Set.ofList
    computeChanges prevSyms currSyms

/// Caches per-file symbol analysis for efficient diff computation.
type FileAnalysisCache = {
  FileSymbols: Map<string, SymbolReference list>
}

module FileAnalysisCache =
  let empty = { FileSymbols = Map.empty }

  let symbolNames (refs: SymbolReference list) =
    refs |> List.map (fun r -> r.SymbolFullName) |> Set.ofList

  let update (filePath: string) (newRefs: SymbolReference list) (cache: FileAnalysisCache) : SymbolChanges * FileAnalysisCache =
    let prevNames =
      cache.FileSymbols
      |> Map.tryFind filePath
      |> Option.defaultValue []
      |> symbolNames
    let newNames = symbolNames newRefs
    let changes = SymbolDiff.computeChanges prevNames newNames
    let newCache = { FileSymbols = Map.add filePath newRefs cache.FileSymbols }
    changes, newCache

  let getFileSymbols (filePath: string) (cache: FileAnalysisCache) =
    cache.FileSymbols |> Map.tryFind filePath |> Option.defaultValue []

/// Result of an FCS type-check request.
/// Success carries the file and symbol references for dependency graph updates.
/// Failed carries diagnostics. Cancelled means the check was superseded.
[<RequireQualifiedAccess>]
type FcsTypeCheckResult =
  | Success of filePath: string * refs: SymbolReference list
  | Failed of filePath: string * errors: string list
  | Cancelled of filePath: string

type PendingRebuildState = {
  Generation: int64
  Tests: TestCase array
  Trigger: RunTrigger
  FilePath: string
  AnalysisIdentity: AnalysisIdentity option
  TreeSitterElapsed: System.TimeSpan
  FcsElapsed: System.TimeSpan
  SessionId: string option
  InstrumentationMaps: InstrumentationMap array
}

type QueuedRebuildState = {
  Tests: TestCase array
  Trigger: RunTrigger
  FilePath: string
  AnalysisIdentity: AnalysisIdentity option
  TreeSitterElapsed: System.TimeSpan
  FcsElapsed: System.TimeSpan
  SessionId: string option
  InstrumentationMaps: InstrumentationMap array
}

/// Wraps LiveTestState + TestCycleDebounce + TestDependencyGraph + adaptive
/// debounce + file analysis cache into a single state record for the Elm loop.
type LiveTestCycleState = {
  TestState: LiveTestState
  Debounce: TestCycleDebounce
  DepGraph: TestDependencyGraph
  ActiveFile: string option
  LatestContent: string option
  LatestAnalysisIdentity: AnalysisIdentity option
  ChangedSymbols: string list
  LastTreeSitterResult: SourceTestLocation array option
  AnalysisCache: FileAnalysisCache
  AdaptiveDebounce: AdaptiveDebounce
  LastTrigger: RunTrigger
  LastTiming: TestCycleTiming option
  /// IL coverage instrumentation maps per session (sessionId → maps for that session's assemblies).
  InstrumentationMaps: Map<string, InstrumentationMap array>
  NextRebuildGeneration: int64
  PendingRebuild: PendingRebuildState option
  QueuedRebuild: QueuedRebuildState option
  /// What is holding this session's next test run back.
  Compile: CompileBlock
  /// Where this session's confirmation of an evaluated run against a real build stands.
  Confirmation: ConfirmationMachine
}

module LiveTestCycleState =
  let empty = {
    TestState = LiveTestState.empty
    Debounce = TestCycleDebounce.empty
    DepGraph = TestDependencyGraph.empty
    ActiveFile = None
    LatestContent = None
    LatestAnalysisIdentity = None
    ChangedSymbols = []
    LastTreeSitterResult = None
    AnalysisCache = FileAnalysisCache.empty
    AdaptiveDebounce = AdaptiveDebounce.createDefault()
    LastTrigger = RunTrigger.Keystroke
    LastTiming = None
    InstrumentationMaps = Map.empty
    NextRebuildGeneration = 0L
    PendingRebuild = None
    QueuedRebuild = None
    Compile = CompileBlock.NoCompileErrors
    Confirmation = BuildConfirmation.initial
  }

  let liveTestingStatusBarForSession (activeSessionId: string) (state: LiveTestCycleState) : string =
    let rebuilding =
      match state.PendingRebuild with
      | Some pending ->
        match pending.Tests.Length with
        | 1 -> "🔨 Rebuilding 1 test"
        | n when n > 1 -> sprintf "🔨 Rebuilding %d tests" n
        | _ -> "🔨 Rebuilding"
      | None -> ""
    let timing =
      match state.LastTiming with
      | None -> ""
      | Some t -> TestCycleTiming.toStatusBar t
    let entries = LiveTestState.statusEntriesForSession activeSessionId state.TestState
    let statuses = entries |> Array.map (fun e -> e.Status)
    let summary = TestSummary.fromStatuses state.TestState.Activation statuses |> TestSummary.toStatusBar
    let explanation =
      state.TestState.LastDecision
      |> Option.map LiveTestingDecision.statusBarHint
      |> Option.defaultValue ""
    [ rebuilding; timing; summary; explanation ]
    |> List.filter (fun segment ->
      not (System.String.IsNullOrWhiteSpace segment) && segment <> "Tests: none")
    |> String.concat " | "

  let liveTestingStatusBar (state: LiveTestCycleState) : string =
    liveTestingStatusBarForSession "" state

  let currentFcsDelay (s: LiveTestCycleState) =
    AdaptiveDebounce.currentFcsDelay s.AdaptiveDebounce

  let private isEquivalentKeystroke (content: string) (filePath: string) (s: LiveTestCycleState) =
    s.LastTrigger = RunTrigger.Keystroke
    && s.ActiveFile = Some filePath
    && s.LatestContent = Some content

  let onKeystroke (content: string) (filePath: string) (now: DateTimeOffset) (s: LiveTestCycleState) =
    match isEquivalentKeystroke content filePath s with
    | true -> s
    | false ->
        let triviaOnlyChange =
          match s.ActiveFile = Some filePath, s.LatestContent with
          | true, Some previous -> TriviaNormalization.equivalent previous content
          | _ -> false
        let fcsDelay = int (currentFcsDelay s / 1.0<ms>) * 1<ms>
        let db =
          match triviaOnlyChange with
          | true ->
              // No new check, but a pending one (for the edit BEFORE this
              // keystroke, reading `LatestContent` when it fires) stays.
              { s.Debounce with
                  TreeSitter = s.Debounce.TreeSitter |> DebounceChannel.submit content TestCycleDebounce.treeSitterDelayMs now }
          | false ->
              s.Debounce |> TestCycleDebounce.onKeystroke content filePath fcsDelay now
        let analysisIdentity = AnalysisIdentity.ofContent content
        // When semantically meaningful edits arrive while tests are running, mark phase as edited so in-flight results are stale.
        let ts =
          match triviaOnlyChange with
          | true -> s.TestState
          | false -> { s.TestState with RunPhases = s.TestState.RunPhases |> Map.map (fun _ p -> TestRunPhase.onEdit p) }
        { s with
            Debounce = db
            TestState = ts
            ActiveFile = Some filePath
            LatestContent = Some content
            LatestAnalysisIdentity = Some analysisIdentity
            LastTrigger = RunTrigger.Keystroke
            PendingRebuild = if triviaOnlyChange then s.PendingRebuild else None
            QueuedRebuild = if triviaOnlyChange then s.QueuedRebuild else None }

  let onFileSave (filePath: string) (now: DateTimeOffset) (s: LiveTestCycleState) =
    let db = s.Debounce |> TestCycleDebounce.onFileSave filePath now
    let ts = { s.TestState with RunPhases = s.TestState.RunPhases |> Map.map (fun _ p -> TestRunPhase.onEdit p) }
    { s with
        Debounce = db
        TestState = ts
        ActiveFile = Some filePath
        LatestContent = None
        LatestAnalysisIdentity = None
        LastTrigger = RunTrigger.FileSave
        PendingRebuild = None
        QueuedRebuild = None }

  let private isEquivalentSavedContent (content: string) (filePath: string) (s: LiveTestCycleState) =
    s.LastTrigger = RunTrigger.FileSave
    && s.ActiveFile = Some filePath
    && s.LatestContent = Some content

  let onFileSaveWithContent (content: string) (filePath: string) (now: DateTimeOffset) (s: LiveTestCycleState) =
    match isEquivalentSavedContent content filePath s with
    | true -> s
    | false ->
        let db =
          s.Debounce
          |> TestCycleDebounce.onFileSave filePath now
          |> fun debounce ->
            { debounce with
                TreeSitter =
                  debounce.TreeSitter
                  |> DebounceChannel.submit content TestCycleDebounce.treeSitterDelayMs now }
        let analysisIdentity = AnalysisIdentity.ofContent content
        let ts = { s.TestState with RunPhases = s.TestState.RunPhases |> Map.map (fun _ p -> TestRunPhase.onEdit p) }
        { s with
            Debounce = db
            TestState = ts
            ActiveFile = Some filePath
            LatestContent = Some content
            LatestAnalysisIdentity = Some analysisIdentity
            LastTrigger = RunTrigger.FileSave
            PendingRebuild = None
            QueuedRebuild = None }

  let onFcsComplete (filePath: string) (refs: SymbolReference list) (s: LiveTestCycleState) =
    let changes, newCache = FileAnalysisCache.update filePath refs s.AnalysisCache
    let newDepGraph =
      SymbolGraphBuilder.updateGraph
        LiveTestingDefaults.TestModuleIdentifier
        LiveTestingDefaults.Framework
        refs filePath s.DepGraph
    let newDebounce = AdaptiveDebounce.onFcsCompleted s.AdaptiveDebounce
    { s with
        DepGraph = newDepGraph
        ChangedSymbols = SymbolChanges.allChanged changes
        AnalysisCache = newCache
        AdaptiveDebounce = newDebounce }

  let onFcsCanceled (s: LiveTestCycleState) =
    { s with AdaptiveDebounce = AdaptiveDebounce.onFcsCanceled s.AdaptiveDebounce }

  let pendingRebuildCancellationEffect (s: LiveTestCycleState) =
    s.PendingRebuild
    |> Option.map (fun pending ->
      TestCycleEffect.CancelRebuild(pending.SessionId, pending.Generation))

  let promoteQueuedRebuild (targetSession: string option) (s: LiveTestCycleState) =
    match s.QueuedRebuild with
    | Some queued
        when queued.SessionId = targetSession
             && queued.AnalysisIdentity = s.LatestAnalysisIdentity ->
        let generation = s.NextRebuildGeneration + 1L
        let pending = {
          Generation = generation
          Tests = queued.Tests
          Trigger = queued.Trigger
          FilePath = queued.FilePath
          AnalysisIdentity = queued.AnalysisIdentity
          TreeSitterElapsed = queued.TreeSitterElapsed
          FcsElapsed = queued.FcsElapsed
          SessionId = queued.SessionId
          InstrumentationMaps = queued.InstrumentationMaps
        }
        [ TestCycleEffect.RequestRebuild(
            generation,
            { Tests = pending.Tests
              Trigger = pending.Trigger
              TreeSitterElapsed = pending.TreeSitterElapsed
              FcsElapsed = pending.FcsElapsed
              SessionId = pending.SessionId
              InstrumentationMaps = pending.InstrumentationMaps }) ],
        { s with
            NextRebuildGeneration = generation
            PendingRebuild = Some pending
            QueuedRebuild = None }
    | Some queued when queued.SessionId = targetSession ->
        [], { s with QueuedRebuild = None }
    | _ ->
        [], s

  let acceptsFcsResult (analysisIdentity: AnalysisIdentity option) (s: LiveTestCycleState) =
    match analysisIdentity, s.LatestAnalysisIdentity with
    | Some completed, Some current -> completed = current
    | _ -> true

  /// Ticks the debounce channels and produces test cycle effects.
  /// Returns (effects, updatedState).
  /// Note: afterTypeCheck is NOT called here — it runs after FCS completes,
  /// not when the FCS request is emitted (avoids stale symbol data).
  let tick (now: DateTimeOffset) (s: LiveTestCycleState) =
    match s.ActiveFile with
    | None -> [], s
    | Some filePath ->
      let (tsPayload, fcsPayload), db = s.Debounce |> TestCycleDebounce.tick now
      let effects =
        TestCycleEffects.fromTick
          tsPayload
          fcsPayload
          s.LatestContent
          s.LatestAnalysisIdentity
          filePath
          s.LastTiming
      // Return same reference when debounce didn't change (no-op tick)
      let s' =
        match obj.ReferenceEquals(db, s.Debounce) with
        | true -> s
        | false -> { s with Debounce = db }
      effects, s'

  /// Handles an FCS type-check result: updates state and produces effects.
  /// Success: updates symbol graph, analysis cache, adaptive debounce, then
  /// calls afterTypeCheck to emit RunAffectedTests if symbols changed.
  /// Failed: records the file as blocking the run, then falls back to a rebuild.
  /// Cancelled: updates adaptive debounce backoff.
  let handleFcsResult
    (result: FcsTypeCheckResult)
    (s: LiveTestCycleState)
    : TestCycleEffect list * LiveTestCycleState =
    let equivalentPendingRebuild
      (tests: TestCase array)
      (trigger: RunTrigger)
      (sessionId: string option)
      (filePath: string)
      (analysisIdentity: AnalysisIdentity option)
      (pending: PendingRebuildState)
      =
      pending.Trigger = trigger
      && pending.SessionId = sessionId
      && pending.FilePath = filePath
      && pending.AnalysisIdentity = analysisIdentity
      && pending.Tests.Length = tests.Length
      && Array.forall2 (fun (pendingTest: TestCase) (nextTest: TestCase) -> pendingTest.Id = nextTest.Id) pending.Tests tests

    let storePendingRebuild filePath analysisIdentity effects state =
      match
        effects
        |> List.tryPick (fun e ->
          match e with
          | TestCycleEffect.RequestRebuild (_generation, req) ->
            Some req
           | _ -> None)
      with
      | Some req ->
        match state.PendingRebuild with
        | Some pending when equivalentPendingRebuild req.Tests req.Trigger req.SessionId filePath analysisIdentity pending ->
            let effects' =
              effects
              |> List.filter (fun effect ->
                match effect with
                | TestCycleEffect.RequestRebuild _ -> false
                | _ -> true)
            effects', { state with QueuedRebuild = None }
        | _ ->
            let generation = state.NextRebuildGeneration + 1L
            let pending = {
              Generation = generation
              Tests = req.Tests
              Trigger = req.Trigger
              FilePath = filePath
              AnalysisIdentity = analysisIdentity
              TreeSitterElapsed = req.TreeSitterElapsed
              FcsElapsed = req.FcsElapsed
              SessionId = req.SessionId
              InstrumentationMaps = req.InstrumentationMaps
            }
            let effects' =
              effects
              |> List.map (fun effect ->
                match effect with
                | TestCycleEffect.RequestRebuild (_, req) ->
                  TestCycleEffect.RequestRebuild (generation, req)
                | other -> other)
            effects',
            { state with
                NextRebuildGeneration = generation
                PendingRebuild = Some pending
                QueuedRebuild = None }
      | None ->
        effects, state

    let storeQueuedRebuild filePath analysisIdentity candidateEffects state =
      match
        candidateEffects
        |> List.tryPick (fun effect ->
          match effect with
          | TestCycleEffect.RequestRebuild (_, req)
              when TestRunPhase.isSessionRunning req.SessionId state.TestState.RunPhases ->
                Some {
                  Tests = req.Tests
                  Trigger = req.Trigger
                  FilePath = filePath
                  AnalysisIdentity = analysisIdentity
                  TreeSitterElapsed = req.TreeSitterElapsed
                  FcsElapsed = req.FcsElapsed
                  SessionId = req.SessionId
                  InstrumentationMaps = req.InstrumentationMaps
                }
          | _ ->
              None)
      with
      | Some queued ->
          { state with QueuedRebuild = Some queued }
      | None ->
          state

    /// A clean type-check lifts only its own file's block; another file may still be broken.
    let liftedBy (filePath: string) (block: CompileBlock) =
      match block with
      | CompileBlock.CompileErrors (blocked, _) when blocked = filePath -> CompileBlock.NoCompileErrors
      | other -> other

    match result with
    | FcsTypeCheckResult.Success (filePath, refs) ->
      let s1 = { onFcsComplete filePath refs s with Compile = liftedBy filePath s.Compile }
      let trigger = s.LastTrigger
      // `refs` is every symbol this type-check resolved in the file. The name
      // delta alone cannot see a body rewrite, so the file's own symbol set
      // travels with it — see `FileSymbolDelta`.
      let symbols : FileSymbolDelta =
        { Changed = s1.ChangedSymbols
          InFile = refs |> List.map (fun r -> r.SymbolFullName) |> List.distinct
          Lines = ChangedLines.NoBaseline }
      let outcome =
        TestCycleEffects.decideAfterTypeCheck
          symbols
          filePath
          trigger
          s1.DepGraph
          s1.TestState
          s1.LastTiming
          s1.InstrumentationMaps
      let queuedOutcome =
        TestCycleEffects.decideAfterTypeCheck
          symbols
          filePath
          trigger
          s1.DepGraph
          { s1.TestState with RunPhases = Map.empty }
          s1.LastTiming
          s1.InstrumentationMaps
      let s1' = { s1 with TestState = { s1.TestState with LastDecision = outcome.Decision } }
      // Brief 4: redirect the compiled-DLL decision into an FSI eval of the
      // now-known buffer content, for both Keystroke and FileSave. Applied
      // AFTER decideAfterTypeCheck so the pending/queued-rebuild bookkeeping
      // below — which only recognizes RequestRebuild — correctly becomes a
      // no-op once RequestRebuild has been redirected (the eval path has no
      // rebuild to cancel/queue; it is not the slow multi-second rebuild the
      // bookkeeping exists to serialize).
      let outcomeEffects = TestCycleEffects.redirectToEvalBuffer s1'.LatestContent filePath outcome.Effects
      let queuedOutcomeEffects = TestCycleEffects.redirectToEvalBuffer s1'.LatestContent filePath queuedOutcome.Effects
      let effects', s2 = storePendingRebuild filePath s1'.LatestAnalysisIdentity outcomeEffects s1'
      effects', storeQueuedRebuild filePath s1'.LatestAnalysisIdentity queuedOutcomeEffects s2
    | FcsTypeCheckResult.Failed (filePath, errors) ->
      let effects =
        TestCycleEffects.fallbackRebuildAfterFailedTypeCheck
          filePath
          s.LastTrigger
          s.TestState
          s.LastTiming
          s.InstrumentationMaps
      let queuedEffects =
        TestCycleEffects.fallbackRebuildAfterFailedTypeCheck
          filePath
          s.LastTrigger
          { s.TestState with RunPhases = Map.empty }
          s.LastTiming
          s.InstrumentationMaps
      // A failed check has at least one error, even when none came back as text.
      let blocked = { s with Compile = CompileBlock.CompileErrors (filePath, max 1 errors.Length) }
      let effects', s' = storePendingRebuild filePath s.LatestAnalysisIdentity effects blocked
      effects', storeQueuedRebuild filePath s.LatestAnalysisIdentity queuedEffects s'
    | FcsTypeCheckResult.Cancelled _ ->
      [], onFcsCanceled s

  /// When the hot-reload hook identifies affected tests (via method name matching),
  /// look up full TestCase objects, filter by RunPolicy, and produce a RunAffectedTests
  /// effect if any tests should run. This bridges the gap between discovery and execution
  /// for MCP-triggered evals (which don't go through the FCS type-check cycle).
  let triggerExecutionForAffected
    (affectedIds: TestId array)
    (trigger: RunTrigger)
    (targetSession: string option)
    (s: LiveTestCycleState)
    : TestCycleEffect list =
    match Array.isEmpty affectedIds
          || s.TestState.Activation = LiveTestingActivation.Inactive
          || TestRunPhase.isSessionRunning targetSession s.TestState.RunPhases with
    | true -> []
    | false ->
      let affectedIdSet = Set.ofArray affectedIds
      let affectedTests =
        s.TestState.DiscoveredTests
        |> Array.filter (fun tc -> affectedIdSet.Contains tc.Id)
      let filtered =
        PolicyFilter.filterTests s.TestState.RunPolicies trigger affectedTests
        |> TestPrioritization.prioritize s.TestState.LastResults
      match Array.isEmpty filtered with
      | true -> []
      | false ->
        let sessionMaps =
          match targetSession |> Option.bind (fun sid -> Map.tryFind sid s.InstrumentationMaps) with
          | Some maps -> maps
          | None -> s.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq
        [ TestCycleEffect.RunAffectedTests {
            Tests = filtered
            Trigger = trigger
            TreeSitterElapsed = System.TimeSpan.Zero
            FcsElapsed = System.TimeSpan.Zero
            SessionId = targetSession
            InstrumentationMaps = sessionMaps
          } ]

  /// Filter tests by optional criteria for explicit MCP-triggered runs.
  /// All filters are AND'd. None = no filter = match all.
  let filterTestsForExplicitRun
    (discoveredTests: TestCase array)
    (fileFilter: string option)
    (patternFilter: string option)
    (categoryFilter: TestCategory option)
    : TestCase array =
    discoveredTests
    |> Array.filter (fun tc ->
      let matchesFile =
        match fileFilter with
        | None -> true
        | Some f ->
          match tc.Origin with
          | TestOrigin.SourceMapped (file, _) -> file = f
          | TestOrigin.ReflectionOnly -> false
      let matchesPattern =
        match patternFilter with
        | None -> true
        | Some p -> tc.FullName.Contains p || tc.DisplayName.Contains p
      let matchesCategory =
        match categoryFilter with
        | None -> true
        | Some c -> tc.Category = c
      matchesFile && matchesPattern && matchesCategory)

