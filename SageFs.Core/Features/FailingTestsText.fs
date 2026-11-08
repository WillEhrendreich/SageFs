namespace SageFs.Features

open System.IO

/// What a landing Blocked on failing tests says about them (finding C-names of the cohort run).
///
/// The blocker carried the failing tests as `Cohort.TestId`, which is a 16-hex-character hash of the test's full name, and the
/// status printed all of them: a hundred hashes and nothing else. A member cannot map a hash back to a name (get_cohort_status
/// takes only a working directory, and list_tests and explain_test_failure need a session and a name), and the integration
/// session lists no tests of its own. The names are known in one place, the integration session's live-testing state at the
/// moment the run failed, so the daemon records them there and this module words them.
///
/// Pure. The daemon reads the names and writes the file; this module decides what the status says and what the file holds.
module FailingTestsText =

  /// One failing test: the id the cohort blocker carries and the full name the test project knows it by.
  type Failure = {
    Id: string
    Name: string
  }

  /// What the daemon recorded when a landing's run failed: every failing test with its name, and the file that holds the whole
  /// list (absent when it could not be written).
  type Report = {
    Failures: Failure list
    File: string option
  }

  /// How many names the status line shows before it points at the file. Ten fit one status line; the file has the rest.
  [<Literal>]
  let namesShownInStatus = 10

  /// Where a landing's whole list goes: the integration worktree's test output, one file per landing. `test-results/` is
  /// git-ignored, so a rebase or a reset of the worktree leaves it alone.
  let reportPath (worktree: string) (landingId: string) : string =
    Path.Combine(worktree, "test-results", sprintf "cohort-landing-%s-failing-tests.txt" landingId)

  /// The file's text: the count, then one line per failing test, by name, each with its id.
  let listing (landingId: string) (failures: Failure list) : string =
    let lines =
      failures
      |> List.sortBy (fun failure -> failure.Name, failure.Id)
      |> List.map (fun failure -> sprintf "%s  [%s]" failure.Name failure.Id)
    String.concat "\n" (sprintf "%d failing tests in landing %s" (List.length failures) landingId :: lines) + "\n"

  let countText (count: int) : string =
    match count with
    | 1 -> "1 failing test"
    | n -> sprintf "%d failing tests" n

  /// The names to show for `ids`: the report's name when it has one, else the bare id marked as unnamed. Sorted by name, so the
  /// first N are the same ones every time the status is read.
  let namedByIds (report: Report option) (ids: string list) : (string * bool) list =
    let names =
      match report with
      | Some recorded -> recorded.Failures |> List.map (fun failure -> failure.Id, failure.Name) |> Map.ofList
      | None -> Map.empty
    ids
    |> List.map (fun id ->
      match Map.tryFind id names with
      | Some name -> (name, true)
      | None -> (id, false))
    |> List.sortBy fst

  /// The reason a landing is Blocked on failing tests, in words the member can act on: how many, the first `limit` by name, how
  /// many more there are, and where the whole list is.
  let render (limit: int) (report: Report option) (ids: string list) : string =
    // RED STUB: what the status said before, every id and nothing else.
    match ids with
    | [] -> "no failing tests"
    | _ -> sprintf "Failing tests: %s" (String.concat ", " ids)
