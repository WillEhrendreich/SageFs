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

  /// What the daemon recorded about a landing's run: every test that did not pass with its name, why each one that could not
  /// run did not (by id: "timed out after 5s"), the tests the landing gate left to the release gate, and the file that holds
  /// all of it (absent when it could not be written).
  type Report = {
    Failures: Failure list
    /// A test in `Failures` that could not run, with why. A failure with no entry here failed its assertions.
    Reasons: Map<string, string>
    /// The `[Integration]` tests the gate did not ask the session to run, so nothing is hidden (`CohortLandingScope`).
    NotRun: Failure list
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
    match ids with
    | [] -> "no failing tests"
    | _ ->
      let count = List.length ids
      let named = namedByIds report ids
      let shown = named |> List.truncate (max 1 limit)
      let hidden = count - List.length shown
      let first = shown |> List.map fst |> String.concat "; "
      let unnamed = named |> List.filter (fun (_, known) -> not known) |> List.length
      let headline =
        match hidden with
        | 0 -> sprintf "%s: %s" (countText count) first
        | more -> sprintf "%s, first %d by name: %s; and %d more" (countText count) (List.length shown) first more
      let where =
        match report |> Option.bind (fun recorded -> recorded.File) with
        | Some file -> sprintf " All %d with their names: %s" count file
        | None when hidden > 0 -> " The whole list is not on disk."
        | None -> ""
      let caveat =
        match unnamed with
        | 0 -> ""
        | n when n = count -> " The names were not recorded (the daemon restarted since the run); run the landing's tests in your own session to see them."
        | n -> sprintf " %d of them show an id because their names were not recorded." n
      headline + "." + where + caveat

  /// The failures of a run with the names the session knows them by: an id the session cannot name is left out, so the status
  /// marks it as unnamed instead of inventing a name.
  let failuresOf (nameOf: string -> string option) (ids: string list) : Failure list =
    ids |> List.choose (fun id -> nameOf id |> Option.map (fun name -> { Id = id; Name = name }))

  /// RED STUB: the sentence that says which tests the landing gate left to the release gate.
  let notRunSentence (_report: Report option) : string = ""

  /// RED STUB: the file's whole text, the failures with why and the tests not run.
  let fullListing (landingId: string) (report: Report) : string = listing landingId report.Failures
