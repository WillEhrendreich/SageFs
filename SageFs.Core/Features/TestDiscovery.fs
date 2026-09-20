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

/// One test in a listing that has no source position — discovered by
/// reflection, so there is genuinely no file or line to report.
type UnlocatedTest = { TestName: string; CellId: int }

/// A file's tests in a listing.
type ListedFile = { File: string; Tests: TestSourceLocation list }

/// The whole answer to "what tests does this session have?", shaped for a
/// client. `WithoutSourceLocation` is its own field rather than a group under
/// a fabricated path, so a caller can tell "no location" from "line 0".
type TestListing = {
  TotalCount: int
  Returned: int
  /// Human-readable description of applied filters, or None if unfiltered —
  /// same shape the query result carries, passed through unchanged.
  FilterApplied: string option
  Summary: string
  GroupedByFile: ListedFile list
  WithoutSourceLocation: UnlocatedTest list
}

/// Build the listing from an already-partitioned set of tests. Pure, and here
/// rather than at the MCP boundary because it is presentation over this
/// module's own result types — and because Mcp.fs is the accretion hub the
/// file-size ratchet exists to shrink.
let buildListing
    (query: TestDiscoveryQuery)
    (located: TestSourceLocation list)
    (unlocated: (string * int) list)
    : TestListing =
  let result = TestDiscovery.applyQuery query located
  { TotalCount = result.TotalCount + unlocated.Length
    Returned = result.Tests.Length + unlocated.Length
    FilterApplied = result.FilterApplied
    Summary = TestDiscovery.summarize result
    GroupedByFile = result.GroupedByFile |> List.map (fun (file, tests) -> { File = file; Tests = tests })
    WithoutSourceLocation = unlocated |> List.map (fun (name, cellId) -> { TestName = name; CellId = cellId }) }
