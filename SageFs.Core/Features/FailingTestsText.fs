namespace SageFs.Features

open System.IO

/// What a landing Blocked on failing tests says about them (findings C-names and C-failing of the cohort run).
///
/// The blocker carried the failing tests as `Cohort.TestId`, which is a 16-hex-character hash of the test's full name, and the
/// status printed all of them: a hundred hashes and nothing else. A member cannot map a hash back to a name (get_cohort_status
/// takes only a working directory, and list_tests and explain_test_failure need a session and a name), and the integration
/// session lists no tests of its own. The names are known in one place, the integration session's live-testing state at the
/// moment the run failed, so the daemon records them there and this module words them.
///
/// A test that did not pass may not have failed: a per-test timeout ends as "Skipped, timed out after 5s", and the verdict is
/// fail-closed, so it blocks all the same. The text keeps the two apart ("3 failed, 97 could not run") and gives the reason beside
/// each name that could not run, so a member reads a timeout as a timeout and not as broken code.
///
/// Pure. The daemon reads the names and writes the file; this module decides what the status says and what the file holds.
module FailingTestsText =

  /// One failing test: the id the cohort blocker carries and the full name the test project knows it by.
  type Failure = {
    Id: string
    Name: string
  }

  /// Whether the report's file is on disk, and where.
  [<RequireQualifiedAccess>]
  type ReportFile =
    | NotWritten
    | Written of path: string

  /// What the daemon recorded about a landing's run: every test that did not pass with its name, why each one that could not
  /// run did not (by id: "timed out after 5s"), the tests the landing gate left to the release gate, and the file that holds
  /// all of it (absent when it could not be written).
  type Report = {
    Failures: Failure list
    /// A test in `Failures` that could not run, with why. A failure with no entry here failed its assertions.
    Reasons: Map<string, string>
    /// The `[Integration]` tests the gate did not ask the session to run, so nothing is hidden (`CohortLandingScope`).
    NotRun: Failure list
    File: ReportFile
  }

  /// How many names the status line shows before it points at the file. Ten fit one status line; the file has the rest.
  [<Literal>]
  let namesShownInStatus = 10

  /// Where a landing's whole list goes: the integration worktree's test output, one file per landing. `test-results/` is
  /// git-ignored, so a rebase or a reset of the worktree leaves it alone.
  let reportPath (worktree: string) (landingId: string) : string =
    Path.Combine(worktree, "test-results", sprintf "cohort-landing-%s-failing-tests.txt" landingId)

  /// The failing tests' text: the count, then one line per test, by name, each with its id.
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

  /// How many tests the landing gate left to the release gate, as a clause that agrees with the count.
  let leftOutText (count: int) : string =
    match count with
    | 1 -> "1 [Integration] test was"
    | n -> sprintf "%d [Integration] tests were" n

  /// What each test in a run did, for the status: its name (or its bare id when the name was not recorded), whether the name is
  /// known, and why it could not run if it could not. Sorted by name, so the first N are the same ones every time the status
  /// is read.
  let itemsOf (report: Report option) (ids: string list) : (string * bool * string option) list =
    let names =
      match report with
      | Some recorded -> recorded.Failures |> List.map (fun failure -> failure.Id, failure.Name) |> Map.ofList
      | None -> Map.empty
    let reasons =
      match report with
      | Some recorded -> recorded.Reasons
      | None -> Map.empty
    ids
    |> List.map (fun id ->
      let name, known =
        match Map.tryFind id names with
        | Some recordedName -> recordedName, true
        | None -> id, false
      (name, known, Map.tryFind id reasons))
    |> List.sortBy (fun (name, _, _) -> name)

  /// The sentence that says which tests the landing gate left to the release gate, why, and where their names are. Empty when it
  /// left none out, so a landing that ran everything says nothing extra.
  let notRunSentence (report: Report option) : string =
    match report with
    | Some { NotRun = (_ :: _) as left; File = file } ->
      let where =
        match file with
        | ReportFile.Written path -> sprintf " Their names: %s" path
        | ReportFile.NotWritten -> ""
      sprintf
        "%s not run by the landing gate: they start real processes, daemons and ports, which the integration session cannot, and the release gate's --integration-host tier runs them.%s"
        (leftOutText (List.length left))
        where
    | Some _
    | None -> ""

  /// The reason a landing is Blocked on failing tests, in words the member can act on: how many did not pass and how many of
  /// those could not run, the first `limit` by name with the reason beside each that could not run, how many more there are, and
  /// where the whole list is.
  let render (limit: int) (report: Report option) (ids: string list) : string =
    match ids with
    | [] -> "no failing tests"
    | _ ->
      let count = List.length ids
      let items = itemsOf report ids
      let shown = items |> List.truncate (max 1 limit)
      let hidden = count - List.length shown
      let label (name, _, reason) =
        match reason with
        | Some why -> sprintf "%s (could not run: %s)" name why
        | None -> name
      let first = shown |> List.map label |> String.concat "; "
      let couldNotRun = items |> List.filter (fun (_, _, reason) -> Option.isSome reason) |> List.length
      let lead =
        match couldNotRun with
        | 0 -> countText count
        | some -> sprintf "%d tests did not pass: %d failed, %d could not run" count (count - some) some
      let headline =
        match hidden with
        | 0 -> sprintf "%s: %s" lead first
        | more -> sprintf "%s, first %d by name: %s; and %d more" lead (List.length shown) first more
      let file =
        match report with
        | Some recorded -> recorded.File
        | None -> ReportFile.NotWritten
      let where =
        match file with
        | ReportFile.Written path -> sprintf " All %d with their names: %s" count path
        | ReportFile.NotWritten when hidden > 0 -> " The whole list is not on disk."
        | ReportFile.NotWritten -> ""
      let unnamed = items |> List.filter (fun (_, known, _) -> not known) |> List.length
      let caveat =
        match unnamed with
        | 0 -> ""
        | n when n = count -> " The names were not recorded (the daemon restarted since the run); run the landing's tests in your own session to see them."
        | n -> sprintf " %d of them show an id because their names were not recorded." n
      let left =
        match notRunSentence report with
        | "" -> ""
        | sentence -> " " + sentence
      headline + "." + where + caveat + left

  /// The failures of a run with the names the session knows them by: an id the session cannot name is left out, so the status
  /// marks it as unnamed instead of inventing a name.
  let failuresOf (nameOf: string -> string option) (ids: string list) : Failure list =
    ids |> List.choose (fun id -> nameOf id |> Option.map (fun name -> { Id = id; Name = name }))

  /// The file's whole text: every test that did not pass, by name, with its id and why it could not run when it could not, then
  /// every test the landing gate left to the release gate, by name, with why.
  let fullListing (landingId: string) (report: Report) : string =
    let failureLine (failure: Failure) =
      match Map.tryFind failure.Id report.Reasons with
      | Some why -> sprintf "%s  [%s]  could not run: %s" failure.Name failure.Id why
      | None -> sprintf "%s  [%s]" failure.Name failure.Id
    let failed =
      match report.Failures with
      | [] -> []
      | some ->
        sprintf "%d failing tests in landing %s" (List.length some) landingId
        :: (some |> List.sortBy (fun failure -> failure.Name, failure.Id) |> List.map failureLine)
    let left =
      match report.NotRun with
      | [] -> []
      | some ->
        let heading =
          sprintf "%d [Integration] tests not run by the landing gate in landing %s: %s" (List.length some) landingId CohortLandingScope.notRunReason
        let spacer = match failed with [] -> [] | _ -> [ "" ]
        spacer @ (heading :: (some |> List.sortBy (fun test -> test.Name, test.Id) |> List.map (fun test -> sprintf "%s  [%s]" test.Name test.Id)))
    String.concat "\n" (failed @ left) + "\n"
