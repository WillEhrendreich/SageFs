module SageFs.Features.TestSourceResolver

open SageFs.Features.LiveTesting
open SageFs.Features.CellDependencyGraph

// CellInfo only carries Produces/Consumes binding names and source text — it does
// NOT carry file paths or line numbers. File and line data is available on TestCase
// via TestOrigin.SourceMapped when the test was discovered through tree-sitter. For
// tests discovered via reflection only (TestOrigin.ReflectionOnly), no source position
// is available and those tests are silently skipped.
//
// CellId resolution is best-effort: a cell "produces" short F# val names, which may
// appear as substrings of a test's FullName or DisplayName. When no cell match is
// found, CellId is -1 (sentinel for "unknown cell").
//
// TODO: When CellInfo is extended with FilePath + StartLine (e.g., from FCS symbol
// tables), use graph.Cells directly for location data and remove the TestOrigin fallback.

/// Attempt to resolve file/line positions for a list of tests.
/// Uses TestCase.Origin for source location when available (SourceMapped tests only).
/// CellId is resolved by matching test names against cell Produces lists; -1 if unresolvable.
/// Returns only tests for which location data is available — silently skips ReflectionOnly tests.
let resolveTestLocations
    (graph: CellGraph)
    (tests: TestCase list)
    : TestSourceLocation list =
  let findCellId (tc: TestCase) =
    graph.Cells
    |> Map.tryPick (fun cellId info ->
      match info.Produces |> List.exists (fun binding ->
        tc.FullName.Contains(binding) || tc.DisplayName.Contains(binding)) with
      | true -> Some cellId
      | false -> None)
    |> Option.defaultValue -1
  tests
  |> List.choose (fun tc ->
    match tc.Origin with
    | TestOrigin.SourceMapped (file, line) ->
      Some {
        CellId    = findCellId tc
        TestName  = tc.FullName
        FilePath  = file
        StartLine = line
        EndLine   = line
      }
    | TestOrigin.ReflectionOnly -> None)
