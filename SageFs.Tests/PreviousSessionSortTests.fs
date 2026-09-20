/// WHY — the "Resume Previous" list (`SageFs/DashboardFragments.fs`,
/// `renderSessionPicker`) is rendered unsorted-by-anything-explicit. That is
/// fine at three entries and useless at thirty. `sortPreviousSessions` is the
/// pure decision logic behind the dashboard's sort control — no HTTP, no DOM,
/// no IO — so the ordering rules are pinned directly rather than through
/// rendered markup or a browser.
///
/// The rule that needs pinning, beyond "each order does what it says", is
/// TOTALITY: every order must be total and stable. Two sessions that tie on
/// the chosen key fall back to `LastSeen` descending, then `Id`, so the list
/// never reshuffles between renders for no reason.
module SageFs.Tests.PreviousSessionSortTests

open System
open Expecto
open Expecto.Flip
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private mk id workingDir projects lastSeen : PreviousSession =
  { Id = id; WorkingDir = workingDir; Projects = projects; LastSeen = lastSeen }

let private ids (sessions: PreviousSession list) = sessions |> List.map (fun s -> s.Id)

let private t (hoursAgo: float) = DateTime.UtcNow.AddHours(-hoursAgo)

[<Tests>]
let tests =
  testList "sortPreviousSessions" [

    testCase "WHY — Recent is the default order: LastSeen descending" <| fun _ ->
      let sessions =
        [ mk "aaa" "/w/a" [ "a/A.fsproj" ] (t 5.0)
          mk "bbb" "/w/b" [ "b/B.fsproj" ] (t 1.0)
          mk "ccc" "/w/c" [ "c/C.fsproj" ] (t 3.0) ]
      sortPreviousSessions PreviousSessionSort.Recent sessions
      |> ids
      |> Expect.equal "most recently seen first" [ "bbb"; "ccc"; "aaa" ]

    testCase "WHY — ProjectName sorts by the first project's display name, A-Z" <| fun _ ->
      let sessions =
        [ mk "aaa" "/w/a" [ "z/Zebra.fsproj" ] (t 1.0)
          mk "bbb" "/w/b" [ "a/Alpha.fsproj" ] (t 1.0)
          mk "ccc" "/w/c" [ "m/Mid.fsproj" ] (t 1.0) ]
      sortPreviousSessions PreviousSessionSort.ProjectName sessions
      |> ids
      |> Expect.equal "alphabetical by project name" [ "bbb"; "ccc"; "aaa" ]

    testCase "WHY — ProjectName ignores case" <| fun _ ->
      let sessions =
        [ mk "aaa" "/w/a" [ "z/zebra.fsproj" ] (t 1.0)
          mk "bbb" "/w/b" [ "a/ALPHA.fsproj" ] (t 1.0) ]
      sortPreviousSessions PreviousSessionSort.ProjectName sessions
      |> ids
      |> Expect.equal "case-insensitive alphabetical" [ "bbb"; "aaa" ]

    testCase "WHY — sessions with no project sort LAST under ProjectName, never first" <| fun _ ->
      let sessions =
        [ mk "noproj" "/w/none" [] (t 1.0)
          mk "hasproj" "/w/a" [ "a/Alpha.fsproj" ] (t 5.0) ]
      sortPreviousSessions PreviousSessionSort.ProjectName sessions
      |> ids
      |> Expect.equal "named project first even though it's older" [ "hasproj"; "noproj" ]

    testCase "WHY — Directory sorts by WorkingDir, A-Z, grouping one repo's sessions together" <| fun _ ->
      let sessions =
        [ mk "aaa" "/w/zeta" [] (t 1.0)
          mk "bbb" "/w/alpha" [] (t 1.0)
          mk "ccc" "/w/alpha" [] (t 2.0) ]
      sortPreviousSessions PreviousSessionSort.Directory sessions
      |> ids
      // Both "/w/alpha" sessions land together, before "/w/zeta"; within the
      // same directory the stability fallback (LastSeen desc) applies.
      |> Expect.equal "grouped by directory, recent-first within a group" [ "bbb"; "ccc"; "aaa" ]

    testCase "WHY — Kind orders solutions before projects before no-project sessions" <| fun _ ->
      let sessions =
        [ mk "proj" "/w/p" [ "p/P.fsproj" ] (t 1.0)
          mk "none" "/w/n" [] (t 1.0)
          mk "sln" "/w/s" [ "All.slnx" ] (t 1.0)
          mk "sln2" "/w/s2" [ "Old.sln" ] (t 1.0) ]
      sortPreviousSessions PreviousSessionSort.Kind sessions
      |> ids
      |> List.take 2
      |> Set.ofList
      |> Expect.equal "both solution kinds (.sln and .slnx) rank first" (Set.ofList [ "sln"; "sln2" ])

    testCase "WHY — Kind then orders by name within the same kind" <| fun _ ->
      let sessions =
        [ mk "b" "/w/b" [ "b/Bravo.fsproj" ] (t 1.0)
          mk "a" "/w/a" [ "a/Alpha.fsproj" ] (t 1.0) ]
      sortPreviousSessions PreviousSessionSort.Kind sessions
      |> ids
      |> Expect.equal "same kind (project), alphabetical" [ "a"; "b" ]

    testCase "WHY — Kind puts no-project sessions last, matching ProjectName's rule" <| fun _ ->
      let sessions =
        [ mk "none" "/w/n" [] (t 1.0)
          mk "proj" "/w/p" [ "p/P.fsproj" ] (t 1.0) ]
      sortPreviousSessions PreviousSessionSort.Kind sessions
      |> ids
      |> Expect.equal "project before bare directory" [ "proj"; "none" ]

    testCase "WHY — a session with several projects is identified by the FIRST one" <| fun _ ->
      let sessions =
        [ mk "multi" "/w/m" [ "z/Zebra.fsproj"; "a/Alpha.fsproj" ] (t 1.0)
          mk "single" "/w/s" [ "m/Mid.fsproj" ] (t 1.0) ]
      sortPreviousSessions PreviousSessionSort.ProjectName sessions
      |> ids
      // "Zebra" (first project) sorts after "Mid", even though "Alpha" (the
      // second project) would sort before it.
      |> Expect.equal "keyed on Projects.Head, not any other member" [ "single"; "multi" ]

    testCase "WHY — identical LastSeen timestamps are stable: tie-break falls back to Id" <| fun _ ->
      let same = t 2.0
      let sessions =
        [ mk "zzz" "/w/z" [] same
          mk "aaa" "/w/a" [] same
          mk "mmm" "/w/m" [] same ]
      sortPreviousSessions PreviousSessionSort.Recent sessions
      |> ids
      |> Expect.equal "Id ascending breaks the LastSeen tie" [ "aaa"; "mmm"; "zzz" ]

    testCase "WHY — identical project names still produce a stable, deterministic order" <| fun _ ->
      let sessions =
        [ mk "later" "/w/a" [ "x/Same.fsproj" ] (t 1.0)
          mk "earlier" "/w/b" [ "y/Same.fsproj" ] (t 5.0) ]
      sortPreviousSessions PreviousSessionSort.ProjectName sessions
      |> ids
      // Same display name -> falls back to LastSeen descending.
      |> Expect.equal "tie-break by LastSeen descending" [ "later"; "earlier" ]

    testCase "WHY — an empty list sorts to an empty list, for every order" <| fun _ ->
      for order in PreviousSessionSort.all do
        sortPreviousSessions order [] |> Expect.isEmpty (sprintf "empty in, empty out for %s" (PreviousSessionSort.toKey order))

    testCase "WHY — a single-entry list is trivially sorted (and the picker renders no control for it)" <| fun _ ->
      let sessions = [ mk "only" "/w/only" [ "o/Only.fsproj" ] (t 1.0) ]
      for order in PreviousSessionSort.all do
        sortPreviousSessions order sessions
        |> ids
        |> Expect.equal (sprintf "single entry unchanged under %s" (PreviousSessionSort.toKey order)) [ "only" ]

    testCase "WHY — PreviousSessionSort.toKey/ofKey round-trip for every case" <| fun _ ->
      for order in PreviousSessionSort.all do
        order |> PreviousSessionSort.toKey |> PreviousSessionSort.ofKey
        |> Expect.equal (sprintf "round-trips through the wire key for %A" order) order

    testCase "WHY — ofKey defaults an unrecognized or empty key to Recent, never a crash" <| fun _ ->
      PreviousSessionSort.ofKey "not-a-real-key" |> Expect.equal "unknown -> default" PreviousSessionSort.Recent
      PreviousSessionSort.ofKey "" |> Expect.equal "empty -> default" PreviousSessionSort.Recent
  ]
