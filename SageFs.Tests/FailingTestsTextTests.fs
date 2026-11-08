module SageFs.Tests.FailingTestsTextTests

/// What a landing Blocked on failing tests says about them (finding C-names of the cohort run): the status carried a hundred
/// 16-character hashes and nothing a member could look up. It now says how many, the first ten by name, how many more, and
/// the file in the integration worktree's test output that holds all of them with their names.
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Cohort
open SageFs.Features
open SageFs.Features.FailingTestsText

let private failure (n: int) : Failure =
  { Id = sprintf "%016X" (n * 7919); Name = sprintf "SageFs.Tests.Suite.case %03d" n }

let private failures (count: int) : Failure list = [ for n in 1 .. count -> failure n ]

let private reportFor (count: int) : Report =
  { Failures = failures count; File = Some "/data/cohort-integration/test-results/cohort-landing-l-1-failing-tests.txt" }

let private idsOf (items: Failure list) : string list = items |> List.map (fun f -> f.Id)

let private occurrences (needle: string) (haystack: string) : int =
  haystack.Split([| needle |], System.StringSplitOptions.None).Length - 1

[<Tests>]
let renderTests =
  testList "FailingTestsText.render: the reason a landing is Blocked on failing tests" [

    testCase "WHY — a hundred failures show the first ten by name, say how many more, and point at the file (finding C-names)" <| fun _ ->
      let text = render namesShownInStatus (Some (reportFor 100)) (idsOf (failures 100))
      text |> Expect.stringContains "the count" "100 failing tests"
      text |> Expect.stringContains "the first by name" "SageFs.Tests.Suite.case 001"
      text |> Expect.stringContains "the tenth by name" "SageFs.Tests.Suite.case 010"
      text |> Expect.stringContains "how many more" "90 more"
      text |> Expect.stringContains "where the whole list is" "/data/cohort-integration/test-results/cohort-landing-l-1-failing-tests.txt"
      (text.Contains "SageFs.Tests.Suite.case 011") |> Expect.isFalse "the eleventh is in the file, not the status"

    testCase "WHY — the status shows names and never the hashes of tests it can name, because a hash is what the member could not use" <| fun _ ->
      let text = render namesShownInStatus (Some (reportFor 100)) (idsOf (failures 100))
      for f in failures 100 do
        (text.Contains f.Id) |> Expect.isFalse (sprintf "the id of %s is not in the status" f.Name)

    testCase "WHY — the same ten come first however the failures arrive, so reading the status twice shows the same tests" <| fun _ ->
      let ids = idsOf (failures 100)
      let a = render namesShownInStatus (Some (reportFor 100)) ids
      let b = render namesShownInStatus (Some (reportFor 100)) (List.rev ids)
      b |> Expect.equal "order of arrival does not change the text" a

    testCase "WHY — a few failures are all named, with no 'more' and the file still named" <| fun _ ->
      let text = render namesShownInStatus (Some (reportFor 3)) (idsOf (failures 3))
      text |> Expect.stringContains "the count" "3 failing tests"
      for n in 1 .. 3 do text |> Expect.stringContains "each name" (sprintf "case %03d" n)
      (text.Contains "more") |> Expect.isFalse "nothing is hidden"

    testCase "WHY — one failure says '1 failing test', not '1 failing tests'" <| fun _ ->
      render namesShownInStatus (Some (reportFor 1)) (idsOf (failures 1))
      |> Expect.stringContains "singular" "1 failing test:"

    testCase "WHY — without a recorded report the status says so and shows ids, instead of pretending to name them" <| fun _ ->
      let text = render namesShownInStatus None (idsOf (failures 100))
      text |> Expect.stringContains "the count" "100 failing tests"
      text |> Expect.stringContains "says names are missing" "names were not recorded"
      text |> Expect.stringContains "still shows an id" (failure 1).Id

    testCase "WHY — a report that names only some of the failures marks the rest, so a partial record is not read as complete" <| fun _ ->
      let partial = { reportFor 5 with Failures = failures 3 }
      let text = render namesShownInStatus (Some partial) (idsOf (failures 5))
      text |> Expect.stringContains "two are unnamed" "2 of them show an id"

    testCase "WHY — no failures is said as that, because an empty list is not a count of zero hashes" <| fun _ ->
      render namesShownInStatus None [] |> Expect.equal "none" "no failing tests"

    testProperty "WHY — however many tests fail, the status names at most the limit and the count it prints is the real count" <| fun (count: int) ->
      let n = 1 + abs (count % 300)
      let text = render namesShownInStatus (Some (reportFor n)) (idsOf (failures n))
      let named = occurrences "SageFs.Tests.Suite.case" text
      (named = min n namesShownInStatus) && (text.Contains (sprintf "%d failing test" n))
  ]

[<Tests>]
let listingTests =
  testList "FailingTestsText.listing and reportPath: the whole list on disk" [

    testCase "WHY — the file has every failing test with its name and id, by name, so the member can grep it" <| fun _ ->
      let text = listing "l-1" (failures 100 |> List.rev)
      let lines = text.TrimEnd('\n').Split('\n')
      lines.Length |> Expect.equal "a header and one line per test" 101
      lines.[0] |> Expect.equal "the header" "100 failing tests in landing l-1"
      lines.[1] |> Expect.stringContains "the first by name" "SageFs.Tests.Suite.case 001  ["
      lines.[100] |> Expect.stringContains "the last by name" "SageFs.Tests.Suite.case 100  ["

    testCase "WHY — the file goes in the integration worktree's git-ignored test-results, one per landing, so a reset does not remove it" <| fun _ ->
      let path = reportPath "/data/cohort-integration" "l-abc"
      path |> Expect.equal "under test-results" "/data/cohort-integration/test-results/cohort-landing-l-abc-failing-tests.txt"
      (reportPath "/data/cohort-integration" "l-abc" <> reportPath "/data/cohort-integration" "l-def") |> Expect.isTrue "one file per landing"
  ]

let private frameBlocked (ids: string list) : LandingState<MemberTable.MemberId> =
  let tests = ids |> List.map TestId
  LandingState.Blocked (LandingBlocker.FailingTests tests, NextAction.FixTests tests)

[<Tests>]
let statusTests =
  testList "CohortStatusText: a Blocked landing's status line carries the names" [

    testCase "WHY — the status line of a landing Blocked on failing tests names the tests and does not print the hundred ids twice" <| fun _ ->
      let ids = idsOf (failures 100)
      let line = CohortStatusText.landingStateTextWith (fun landingId -> if landingId = "l-1" then Some (reportFor 100) else None) "l-1" (frameBlocked ids)
      line |> Expect.stringContains "a name" "SageFs.Tests.Suite.case 001"
      line |> Expect.stringContains "the file" "cohort-landing-l-1-failing-tests.txt"
      line |> Expect.stringContains "what the member does next" "withdraw_landing"
      (line.Contains (failure 1).Id) |> Expect.isFalse "no hash in the line"
      occurrences "SageFs.Tests.Suite.case" line |> Expect.equal "ten names, once each" namesShownInStatus

    testCase "WHY — a landing the daemon has no report for still reads sensibly, with the count and ids" <| fun _ ->
      let line = CohortStatusText.landingStateTextWith (fun _ -> None) "l-9" (frameBlocked (idsOf (failures 12)))
      line |> Expect.stringContains "the count" "12 failing tests"
      line |> Expect.stringContains "says the names are missing" "names were not recorded"

    testCase "WHY — other states keep the wording they had, so only the failing-tests line changed" <| fun _ ->
      CohortStatusText.landingStateTextWith (fun _ -> None) "l-1" (LandingState.Landed "abc123")
      |> Expect.equal "landed" (CohortStatusText.landingStateText (LandingState.Landed "abc123"))
  ]
