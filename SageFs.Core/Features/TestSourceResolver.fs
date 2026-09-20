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
// appear as substrings of a test's FullName. DisplayName is intentionally excluded
// because it is a user-facing label, not a stable source identity. When no cell match
// is found, CellId is -1 (sentinel for "unknown cell").
//
// TODO: When CellInfo is extended with FilePath + StartLine (e.g., from FCS symbol
// tables), use graph.Cells directly for location data and remove the TestOrigin fallback.

/// Attempt to resolve file/line positions for a list of tests.
/// Uses TestCase.Origin for source location when available (SourceMapped tests only).
/// CellId is resolved by matching test FullName values against cell Produces lists; -1 if unresolvable.
/// Returns only tests for which location data is available — silently skips ReflectionOnly tests.
let resolveTestLocations
    (graph: CellGraph)
    (tests: TestCase list)
    : TestSourceLocation list =
  let findCellId (tc: TestCase) =
    graph.Cells
    |> Map.tryPick (fun cellId info ->
      match info.Produces |> List.exists (fun binding -> tc.FullName.Contains binding) with
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

/// A discovered test as a caller that must not lose tests should see it: either
/// with a source position, or explicitly without one.
///
/// WHY this exists: `resolveTestLocations` drops every `ReflectionOnly` test on
/// the floor. That is correct for a caller that needs a file and line (a gutter
/// marker has nowhere to go without one), but catastrophic for a caller that
/// needs the TEST LIST — and `list_tests` is exactly that caller. A session
/// whose tests were discovered by reflection (every compiled-project session)
/// has ReflectionOnly for ALL of them, so `list_tests` reported
/// `TotalCount: 0` while `/api/live-testing/status` simultaneously reported
/// three passing tests for the same session. `docs/mcp-tools.md` names
/// `list_tests` as the way an agent reads test results, so the headline promise
/// was returning an empty list for the common case.
///
/// The absence is a DU case rather than a placeholder path/line: an empty
/// FilePath and a 0 line are a plausible-looking lie that a caller would render
/// as a real location.
[<RequireQualifiedAccess>]
type ResolvedTest =
  | Located of TestSourceLocation
  | Unlocated of testName: string * cellId: int

/// Every test, never fewer — tests with a source position keep it, tests
/// without one are carried as `Unlocated` instead of being discarded.
let resolveAllTests (graph: CellGraph) (tests: TestCase list) : ResolvedTest list =
  let located = resolveTestLocations graph tests
  let locatedNames = located |> List.map (fun l -> l.TestName) |> Set.ofList
  let findCellId (tc: TestCase) =
    graph.Cells
    |> Map.tryPick (fun cellId info ->
      match info.Produces |> List.exists (fun binding -> tc.FullName.Contains binding) with
      | true -> Some cellId
      | false -> None)
    |> Option.defaultValue -1
  let unlocated =
    tests
    |> List.filter (fun tc -> not (locatedNames.Contains tc.FullName))
    |> List.map (fun tc -> ResolvedTest.Unlocated(tc.FullName, findCellId tc))
  (located |> List.map ResolvedTest.Located) @ unlocated

/// The located/unlocated split a test LISTING needs, with the listing's own
/// filters applied to both halves. Pure, and here rather than at the MCP
/// boundary because it is a decision about this module's own types — and
/// because Mcp.fs is the accretion hub the file-size ratchet exists to shrink.
///
/// A test with no source position cannot satisfy a file filter, so it is
/// excluded when one is given rather than being handed a fabricated path.
let partitionForListing
    (graph: CellGraph)
    (tests: TestCase list)
    (pattern: string option)
    (filePath: string option)
    : TestSourceLocation list * (string * int) list =
  let nonEmpty = Option.filter (fun (s: string) -> s.Length > 0)
  let resolved = resolveAllTests graph tests
  let located =
    resolved
    |> List.choose (function
      | ResolvedTest.Located l -> Some l
      | ResolvedTest.Unlocated _ -> None)
  let unlocated =
    match nonEmpty filePath with
    | Some _ -> []
    | None ->
      resolved
      |> List.choose (function
        | ResolvedTest.Unlocated(name, cellId) -> Some(name, cellId)
        | ResolvedTest.Located _ -> None)
      |> List.filter (fun (name, _) ->
        match nonEmpty pattern with
        | Some p -> name.Contains(p, System.StringComparison.OrdinalIgnoreCase)
        | None -> true)
  located, unlocated
