/// Pure decision logic for VS Code test/coverage gutter decorations.
///
/// WHY — `outcome-gate-sweep.md` Gap G: `TestDecorations.fs` computed the
/// `passedRanges`/`failedRanges`/`runningRanges` split, the hover text, and
/// the one-based→zero-based line conversion (`newRange (line - 1) ...`)
/// inline, next to the Fable-only `vscode.window` calls — with zero test
/// references. An off-by-one in the line math, or an `Errored` result
/// silently landing in `passedRanges`, would ship green: nothing calls
/// `applyToEditor` outside a real Electron host, and `VscLiveTestStateTests`
/// only exercises the reducer that feeds it, never the decoration mapping
/// itself.
///
/// This module extracts that mapping so it is testable under plain
/// `dotnet fsi`, mirroring `SessionsTreePure.fs` / `StatusBarPure.fs` /
/// `ContextKeysPure.fs` / `CoverageViewPure.fs`. No Fable dependency.
/// Tested by `tests/TestDecorationsContractTests.fsx`.
module SageFs.Vscode.TestDecorationsPure

open SageFs.Vscode.LiveTestingTypes

// ── Line numbering ───────────────────────────────────────────────

/// Every line SageFs hands this extension (`VscTestInfo.Line`, coverage
/// line-map keys) is one-based, matching F# compiler diagnostics. VS Code's
/// `Range`/`Position` API is zero-based. This is the ONE place that
/// conversion happens — both decoration paths and diagnostics route through
/// it, so a fix here fixes every gutter/underline at once.
let toZeroBasedLine (oneBasedLine: int) : int = oneBasedLine - 1

/// Inverse of `toZeroBasedLine` — used only by the contract test to pin the
/// round-trip (a fresh one-based line always recovers itself through the
/// pair of conversions).
let toOneBasedLine (zeroBasedLine: int) : int = zeroBasedLine + 1

// ── Test-outcome decorations ─────────────────────────────────────

/// The three `TextEditorDecorationType`s a test outcome can render into.
/// A DU, not a bool pair — an outcome routes to exactly one of these, never
/// zero and never more than one, and the match below is exhaustive so a new
/// `VscTestOutcome` case cannot silently fail to pick one.
[<RequireQualifiedAccess>]
type DecorationBucket =
  | Passed
  | Failed
  | Running

/// One rendered decoration: the source line (one-based, as reported), the
/// VS Code line it actually maps to, the hover text, and which bucket it
/// belongs in.
type DecorationEntry = {
  Line: int
  ZeroBasedLine: int
  HoverText: string
  Bucket: DecorationBucket
}

let private mkEntry (line: int) (hoverText: string) (bucket: DecorationBucket) : DecorationEntry =
  { Line = line
    ZeroBasedLine = toZeroBasedLine line
    HoverText = hoverText
    Bucket = bucket }

let private notYetRunText (displayName: string) : string =
  sprintf "◆ %s (not yet run)" displayName

/// Which bucket a single `VscTestOutcome` belongs in, and the hover text for
/// it. Total over `VscTestOutcome` — every case is matched explicitly, no
/// wildcard, so the compiler forces a decision whenever a new case is added.
/// `Failed` and `Errored` are two distinct outcomes but both are failures:
/// both — and *only* these two — route to `Failed`. Nothing else does.
let bucketForOutcome
  (displayName: string)
  (freshness: VscResultFreshness)
  (narrativeText: string)
  (durationMs: float option)
  (outcome: VscTestOutcome)
  : DecorationBucket * string =
  match outcome with
  | VscTestOutcome.Passed ->
    let text =
      match durationMs with
      | Some ms -> sprintf "✓ %s (%.0fms)" displayName ms
      | None -> sprintf "✓ %s" displayName
    DecorationBucket.Passed, text
  | VscTestOutcome.Failed msg ->
    DecorationBucket.Failed, sprintf "✗ %s: %s%s" displayName msg narrativeText
  | VscTestOutcome.Errored msg ->
    DecorationBucket.Failed, sprintf "✗ %s: %s%s" displayName msg narrativeText
  | VscTestOutcome.Running ->
    DecorationBucket.Running, sprintf "● Running: %s" displayName
  | VscTestOutcome.Skipped reason ->
    DecorationBucket.Passed, sprintf "⊘ Skipped: %s — %s" displayName reason
  | VscTestOutcome.Stale ->
    let reasonText =
      match freshness with
      | VscResultFreshness.StaleCodeEdited -> "code edited since last run"
      | VscResultFreshness.StaleWrongGeneration -> "generation mismatch"
      | VscResultFreshness.Fresh -> "needs re-run"
    DecorationBucket.Running, sprintf "◌ %s (stale — %s)" displayName reasonText
  | VscTestOutcome.PolicyDisabled ->
    DecorationBucket.Passed, sprintf "⊘ %s (disabled by policy)" displayName
  | VscTestOutcome.NotYetRun ->
    DecorationBucket.Running, notYetRunText displayName

/// The decoration for a single discovered test, or `None` when the test has
/// no known source line (nothing can be decorated). `result = None` (known
/// but never run at all — distinct from the `NotYetRun` outcome, which is a
/// result that has actually been recorded as such) renders identically to
/// `NotYetRun`, matching the pre-extraction behavior.
let decorationForTest
  (narrativeText: VscTestId -> string)
  (freshness: VscResultFreshness)
  (result: VscTestResult option)
  (test: VscTestInfo)
  : DecorationEntry option =
  match test.Line with
  | None -> None
  | Some line ->
    match result with
    | None -> Some (mkEntry line (notYetRunText test.DisplayName) DecorationBucket.Running)
    | Some r ->
      let bucket, text =
        bucketForOutcome test.DisplayName freshness (narrativeText test.Id) r.DurationMs r.Outcome
      Some (mkEntry line text bucket)

/// Every decoration for one file, already split into the three buckets
/// `TestDecorations.applyToEditor` hands to `setDecorations`. Order within
/// each bucket matches discovery order.
type FileDecorations = {
  Passed: DecorationEntry list
  Failed: DecorationEntry list
  Running: DecorationEntry list
}

let empty : FileDecorations = { Passed = []; Failed = []; Running = [] }

let private addToBucket (entry: DecorationEntry) (acc: FileDecorations) : FileDecorations =
  match entry.Bucket with
  | DecorationBucket.Passed -> { acc with Passed = entry :: acc.Passed }
  | DecorationBucket.Failed -> { acc with Failed = entry :: acc.Failed }
  | DecorationBucket.Running -> { acc with Running = entry :: acc.Running }

/// All test decorations for `filePath`, computed purely from state.
let decorationsForFile (state: VscLiveTestState) (filePath: string) : FileDecorations =
  let narrativeText (id: VscTestId) : string =
    VscLiveTestState.narrativeFor (VscTestId.value id) state
    |> Option.map renderNarrativeText
    |> Option.defaultValue ""
  VscLiveTestState.testsForFile filePath state
  |> List.choose (fun test ->
    decorationForTest narrativeText state.Freshness (VscLiveTestState.resultFor test.Id state) test)
  |> List.fold (fun acc entry -> addToBucket entry acc) empty
  |> fun d -> { Passed = List.rev d.Passed; Failed = List.rev d.Failed; Running = List.rev d.Running }

// ── Coverage decorations ─────────────────────────────────────────

/// The three `TextEditorDecorationType`s a per-line coverage status can
/// render into. `Pending` has none — it renders nothing, matching the
/// pre-extraction `applyCoverageToEditor`.
[<RequireQualifiedAccess>]
type CoverageBucket =
  | Passing
  | Failing
  | NotCovered

/// Which bucket, if any, a line's coverage status belongs in, and its hover
/// text. Total over `VscLineCoverage`.
let coverageBucketFor (coverage: VscLineCoverage) : (CoverageBucket * string) option =
  match coverage with
  | VscLineCoverage.Covered (testCount, VscLineCoverageHealth.AllPassing) ->
    Some (CoverageBucket.Passing, sprintf "▸ Covered by %d test(s), all passing" testCount)
  | VscLineCoverage.Covered (testCount, VscLineCoverageHealth.SomeFailing) ->
    Some (CoverageBucket.Failing, sprintf "▸ Covered by %d test(s), some failing" testCount)
  | VscLineCoverage.NotCovered ->
    Some (CoverageBucket.NotCovered, "○ Not covered by any test")
  | VscLineCoverage.Pending -> None

/// A rendered coverage decoration. Deliberately separate from
/// `DecorationEntry` — coverage has no `DecorationBucket` (that DU belongs
/// to test outcomes only), and giving this entry a field that would always
/// hold a meaningless placeholder value is exactly the kind of
/// illegal-state-representable modeling this repo bans.
type CoverageEntry = {
  Line: int
  ZeroBasedLine: int
  HoverText: string
}

let private mkCoverageEntry (line: int) (hoverText: string) : CoverageEntry =
  { Line = line; ZeroBasedLine = toZeroBasedLine line; HoverText = hoverText }

type FileCoverageDecorations = {
  Passing: CoverageEntry list
  Failing: CoverageEntry list
  NotCovered: CoverageEntry list
}

let emptyCoverage : FileCoverageDecorations = { Passing = []; Failing = []; NotCovered = [] }

let private addToCoverageBucket (entry: CoverageEntry) (bucket: CoverageBucket) (acc: FileCoverageDecorations) =
  match bucket with
  | CoverageBucket.Passing -> { acc with Passing = entry :: acc.Passing }
  | CoverageBucket.Failing -> { acc with Failing = entry :: acc.Failing }
  | CoverageBucket.NotCovered -> { acc with NotCovered = entry :: acc.NotCovered }

/// All coverage decorations for `filePath`. `Map.tryFind filePath` returning
/// `None` (no coverage data at all) is `emptyCoverage`, matching the
/// pre-extraction "clear everything" branch.
let coverageDecorationsForFile (state: VscLiveTestState) (filePath: string) : FileCoverageDecorations =
  match Map.tryFind filePath state.Coverage with
  | None -> emptyCoverage
  | Some fileCov ->
    fileCov.LineCoverage
    |> Map.toList
    |> List.choose (fun (line, coverage) ->
      coverageBucketFor coverage
      |> Option.map (fun (bucket, text) -> mkCoverageEntry line text, bucket))
    |> List.fold (fun acc (entry, bucket) -> addToCoverageBucket entry bucket acc) emptyCoverage
    |> fun d ->
      { Passing = List.rev d.Passing
        Failing = List.rev d.Failing
        NotCovered = List.rev d.NotCovered }
