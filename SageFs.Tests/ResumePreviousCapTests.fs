/// "Resume Previous" is bounded: the newest few of each project are drawn, the rest sit behind a native
/// disclosure, and the page says how many are hidden.
///
/// The list was every stopped session of the last seven days, with no cap: 266 rows were measured on a working
/// machine, behind a 50vh scroller, on the first screen a new user sees. `planPreviousSessions` is the pure choice
/// of which rows are drawn; these cases pin it and the markup that carries it.
module SageFs.Tests.ResumePreviousCapTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private baseTime = DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc)

/// One stopped session: a project number (its own directory and project file) and how many minutes ago it was last seen.
let private session (project: int) (minutesAgo: int) (n: int) : PreviousSession =
  { Id = sprintf "%08x" (project * 100000 + n)
    WorkingDir = sprintf "/work/project-%02d" project
    Projects = [ sprintf "/work/project-%02d/Project%02d.fsproj" project project ]
    LastSeen = baseTime.AddMinutes(float -minutesAgo) }

/// The measured shape: many projects, most with a dozen or more stopped sessions.
let private manySessions : PreviousSession list =
  [ for project in 1 .. 25 do
      for n in 1 .. 10 + (project % 3) do
        yield session project (n * 7 + project) n ]

let private small = { PerProject = 2; Total = 5 }

let private pickerHtml (sessions: PreviousSession list) : string =
  renderSessionPickerSorted PreviousSessionSort.Recent sessions |> renderNode

let private count (needle: string) (haystack: string) : int =
  (haystack.Length - haystack.Replace(needle, "").Length) / needle.Length

[<Tests>]
let planTests =
  testList "planPreviousSessions" [
    testCase "WHY — every session is either drawn open or behind show all, never lost and never both" <| fun _ ->
      let plan = planPreviousSessions PreviousListCaps.standard PreviousSessionSort.Recent manySessions
      (plan.Shown @ plan.Hidden |> List.map (fun s -> s.Id) |> List.sort)
      |> Expect.equal "same sessions" (manySessions |> List.map (fun s -> s.Id) |> List.sort)

    testCase "WHY — the open rows respect both caps" <| fun _ ->
      let plan = planPreviousSessions small PreviousSessionSort.Recent manySessions
      plan.Shown.Length |> Expect.equal "at most Total rows" small.Total
      plan.Shown
      |> List.countBy (fun s -> s.WorkingDir)
      |> List.iter (fun (dir, shown) -> (shown, small.PerProject) |> Expect.isLessThanOrEqual (sprintf "%s at most PerProject" dir))

    testCase "WHY — the rows kept are the newest, whatever sort order is chosen" <| fun _ ->
      let kept order =
        (planPreviousSessions small order manySessions).Shown |> List.map (fun s -> s.Id) |> Set.ofList
      let byRecent = kept PreviousSessionSort.Recent
      for order in PreviousSessionSort.all do
        kept order |> Expect.equal (sprintf "%A keeps the same rows" order) byRecent
      let newestOverall =
        manySessions |> List.sortByDescending (fun s -> s.LastSeen) |> List.head
      byRecent |> Expect.contains "the newest session of all is open" newestOverall.Id

    testCase "WHY — the open rows follow the chosen order" <| fun _ ->
      let plan = planPreviousSessions small PreviousSessionSort.Directory manySessions
      plan.Shown
      |> List.map (fun s -> s.WorkingDir)
      |> Expect.equal "directory order" (plan.Shown |> List.map (fun s -> s.WorkingDir) |> List.sortWith (fun a b -> String.Compare(a, b, StringComparison.OrdinalIgnoreCase)))

    testCase "WHY — a list inside the caps hides nothing" <| fun _ ->
      let few = [ session 1 5 1; session 1 9 2; session 2 3 1 ]
      let plan = planPreviousSessions PreviousListCaps.standard PreviousSessionSort.Recent few
      plan.Hidden |> Expect.isEmpty "nothing hidden"
      plan.Shown.Length |> Expect.equal "all three" 3

    testProperty "WHY — for any list the caps hold and no row is lost" <| fun (items: (byte * byte) list) ->
      let sessions =
        items
        |> List.truncate 60
        |> List.mapi (fun i (project, age) -> session (int project % 8) (int age) i)
      let plan = planPreviousSessions small PreviousSessionSort.Recent sessions
      let noLoss = (plan.Shown @ plan.Hidden |> List.map (fun s -> s.Id) |> List.sort) = (sessions |> List.map (fun s -> s.Id) |> List.sort)
      let capped = plan.Shown.Length <= small.Total
      let perProject = plan.Shown |> List.countBy (fun s -> s.WorkingDir) |> List.forall (fun (_, n) -> n <= small.PerProject)
      noLoss && capped && perProject
  ]

[<Tests>]
let markupTests =
  testList "the Resume Previous markup" [
    testCase "WHY — 266 stopped sessions draw at most the cap open, and the rest are behind one details" <| fun _ ->
      let page = pickerHtml manySessions
      let detailsAt = page.IndexOf("<details", StringComparison.Ordinal)
      (detailsAt, 0) |> Expect.isGreaterThan "the disclosure is present"
      let open' = page.Substring(0, detailsAt)
      let behind = page.Substring detailsAt
      (count "picker-session-row" open', PreviousListCaps.standard.Total) |> Expect.isLessThanOrEqual "open rows fit the total cap"
      (count "picker-session-row" open' + count "picker-session-row" behind)
      |> Expect.equal "every session still has a row" manySessions.Length

    testCase "WHY — the sentence says how many are hidden and by what rule" <| fun _ ->
      let plan = planPreviousSessions PreviousListCaps.standard PreviousSessionSort.Recent manySessions
      let page = pickerHtml manySessions
      page |> Expect.stringContains "the sentence" (previousListHiddenSentence PreviousListCaps.standard plan)
      page |> Expect.stringContains "the count is the hidden number" (string plan.Hidden.Length)
      page |> Expect.stringContains "the control says how many it holds" (sprintf "Show all %d hidden sessions" plan.Hidden.Length)

    testCase "WHY — show all is a native disclosure, reachable by keyboard, kept open across a morph" <| fun _ ->
      let page = pickerHtml manySessions
      page |> Expect.stringContains "a details element" "<details"
      page |> Expect.stringContains "with its own summary, which is focusable" "<summary>Show all"
      page.Contains "tabindex=\"-1\"" |> Expect.isFalse "nothing takes the summary out of the tab order"
      page |> Expect.stringContains "an open disclosure is not snapped shut by the next push" "data-preserve-attr=\"open\""

    testCase "WHY — a short list has no disclosure and no sentence about hidden rows" <| fun _ ->
      let page = pickerHtml [ session 1 5 1; session 2 6 1 ]
      page.Contains "<details id=\"previous-show-all\"" |> Expect.isFalse "no disclosure"
      page.Contains "resume-hidden-sentence" |> Expect.isFalse "no sentence"
  ]
