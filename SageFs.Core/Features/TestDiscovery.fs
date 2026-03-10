module SageFs.Features.TestDiscovery

open SageFs.Features.LiveTesting

/// Query parameters for filtering and paginating discovered tests.
type TestDiscoveryQuery = {
  /// Optional substring to match against test name or file path (case-insensitive).
  Pattern: string option
  /// Optional file path substring to restrict results.
  FilePath: string option
  /// Maximum results to return.
  MaxResults: int
}

/// Result of a test discovery query.
type TestDiscoveryResult = {
  /// Tests matching the query, sorted by file then line.
  Tests: TestSourceLocation list
  /// Total matching tests before MaxResults truncation.
  TotalCount: int
  /// Human-readable description of applied filters, or None if unfiltered.
  FilterApplied: string option
  /// Tests grouped by source file path.
  GroupedByFile: (string * TestSourceLocation list) list
}

module TestDiscovery =

  let defaultQuery = { Pattern = None; FilePath = None; MaxResults = 100 }

  let private caseInsensitiveContains (needle: string) (haystack: string) =
    haystack.Contains(needle, System.StringComparison.OrdinalIgnoreCase)

  let private matchesPattern (pattern: string) (loc: TestSourceLocation) =
    caseInsensitiveContains pattern loc.TestName
    || caseInsensitiveContains pattern loc.FilePath

  /// Apply a query to a flat list of test locations. Returns a filtered,
  /// sorted, truncated result with grouping metadata.
  let applyQuery (query: TestDiscoveryQuery) (locations: TestSourceLocation list) : TestDiscoveryResult =
    let filtered =
      locations
      |> List.filter (fun loc ->
        match query.Pattern with
        | Some p -> matchesPattern p loc
        | None -> true)
      |> List.filter (fun loc ->
        match query.FilePath with
        | Some f -> caseInsensitiveContains f loc.FilePath
        | None -> true)
      |> List.sortBy (fun loc -> loc.FilePath, loc.StartLine)
    let total = filtered.Length
    let trimmed = filtered |> List.truncate query.MaxResults
    let grouped = trimmed |> List.groupBy (fun l -> l.FilePath) |> List.sortBy fst
    let filterDesc =
      [ match query.Pattern with Some p -> yield $"pattern:'{p}'" | None -> ()
        match query.FilePath with Some f -> yield $"file:'{f}'" | None -> () ]
      |> function
         | [] -> None
         | parts -> Some (System.String.Join(", ", parts))
    { Tests = trimmed
      TotalCount = total
      FilterApplied = filterDesc
      GroupedByFile = grouped }

  /// Human-readable one-line summary of a discovery result.
  let summarize (result: TestDiscoveryResult) =
    let filterPart =
      match result.FilterApplied with
      | Some f -> $" [filter: {f}]"
      | None -> ""
    let fileCount = result.GroupedByFile.Length
    $"🔍 {result.Tests.Length} of {result.TotalCount} test(s){filterPart} across {fileCount} file(s)"
