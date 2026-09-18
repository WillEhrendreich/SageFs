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
