namespace SageFs.Features.LiveTesting

/// Narrow a landing's verification to the tests a `base..head` diff can affect
/// (sagefs-multiagent-vision.md §5.4: run only what the change touches, not the
/// whole suite). Pure: the caller injects each test's covered files (from its
/// coverage bitmap via `InputHashCoverage.coveredFiles`) and the diff's changed
/// files (from `CohortGit.diffNames`). Correctness over speed — a test whose
/// coverage we cannot trust is ALWAYS considered affected, so narrowing can
/// never wrongly skip a test that the change actually broke.
module AffectedTests =

  let private norm (p: string) = p.Replace('\\', '/')

  /// True when a covered file path (often ABSOLUTE — coverage records
  /// `sp.Document.Url` from the PDB) corresponds to a repo-RELATIVE changed
  /// path. Matched on a path boundary so `.../aFoo.fs` never matches `Foo.fs`,
  /// and robust to the absolute root differing from the repo dir (a worktree /
  /// a different build machine): the covered path just has to END WITH the
  /// changed repo-relative path at a `/` boundary (or equal it outright).
  let fileMatches (coveredPath: string) (changedRel: string) : bool =
    let c, r = norm coveredPath, norm changedRel
    c = r || c.EndsWith("/" + r)

  /// The subset of `tests` a change to `changedFiles` (repo-relative) can
  /// affect. `coveredFilesOf t` returns `Some files` when `t` has trustworthy
  /// coverage and `None` when it does not (no bitmap, or a stale/size-mismatched
  /// one). A `None` test is ALWAYS affected (conservative); a `Some` test is
  /// affected iff one of its covered files matches a changed file. Order
  /// preserved. (The caller falls back to the whole suite when the diff itself
  /// cannot be computed — this function assumes `changedFiles` is authoritative.)
  let affected
    (changedFiles: string list)
    (coveredFilesOf: TestId -> string list option)
    (tests: TestId list)
    : TestId list =
    tests
    |> List.filter (fun t ->
      match coveredFilesOf t with
      | None -> true
      // Empty covered set = coverage we cannot trust (every executed test touches
      // at least its own source file), so treat it as affected — same
      // conservative rule as `None`. Trusting `Some []` as "covers nothing, so
      // unaffected" is a fail-OPEN that would skip a test the change may have broken.
      | Some [] -> true
      | Some covered ->
        covered |> List.exists (fun cf -> changedFiles |> List.exists (fileMatches cf)))

  /// F17 attributable-settle close: the set of tests a cohort landing gate
  /// MUST verify for a rebase touching `changedFiles` — `affected`'s
  /// coverage-based narrow, with a NO-EMPTY-ESCAPE fail-closed floor.
  ///
  /// Why `affected` alone is not enough here: `affected`'s per-test
  /// "untrustworthy coverage -> always affected" rule only rescues tests
  /// whose OWN coverage is untrustworthy. It says nothing about the case
  /// where every candidate test looks CLEANLY unaffected because the
  /// narrow itself is being asked the wrong question — e.g. a same-
  /// signature BODY edit to an existing function (`add a b = a+b` ->
  /// `a-b`) that a purely symbol-NAME-based change-detector (the live-
  /// testing FCS pipeline's `changedSymbols` diff — see
  /// `TestCycleEffects.decideAfterTypeCheck`) sees as no change at all,
  /// while coverage bitmaps are cold right after a rebase (a separate,
  /// not-yet-collected signal). Both signals can independently, honestly,
  /// say "nothing affected" for a real, breaking, content change — and a
  /// gate that trusts that verifies nothing and lands the regression.
  ///
  /// So: on a REAL diff (`changedFiles` non-empty) against a REAL
  /// discovered suite (`allTests` non-empty), this function NEVER returns
  /// an empty set — an empty coverage-based narrow falls back to the
  /// WHOLE discovered suite rather than being trusted as "nothing to
  /// verify". Conservative-over-precise: a gate is not a per-keystroke
  /// path, so correctness outranks the cost of an occasional full run.
  let verificationTestSet
    (changedFiles: string list)
    (coveredFilesOf: TestId -> string list option)
    (allTests: TestId list)
    : TestId list =
    match changedFiles, allTests with
    | [], _ | _, [] -> []
    | _ ->
      match affected changedFiles coveredFilesOf allTests with
      | [] -> allTests
      | narrowed -> narrowed

  /// TWIN — the pre-fix decision shape, frozen as a regression witness.
  /// Mirrors the live-testing FCS pipeline's ACTUAL current behavior for
  /// the F17 scenario: narrow by symbol-NAME delta only (`changedSymbols`
  /// empty for a body-only edit => nothing selected), with NO coverage
  /// fallback and NO empty-escape floor. This is deliberately NOT wired
  /// into any product path — it exists only so `NO-EMPTY-ESCAPE` can be
  /// proven to fail under it, pinning the exact bug `verificationTestSet`
  /// closes (see `AffectedTestsTests.fs`'s twin properties).
  let nameOnlyTwin
    (changedSymbols: string list)
    (symbolToTests: Map<string, TestId array>)
    : TestId list =
    match changedSymbols with
    | [] -> []
    | syms ->
      syms
      |> List.choose (fun s -> Map.tryFind s symbolToTests)
      |> Array.concat
      |> Array.distinct
      |> Array.toList
