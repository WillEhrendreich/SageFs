module SageFs.Tests.SessionProjectPickerTests

/// WHY — the session card's project picker answers "which project is this
/// session on?" and lets the user change it. Before it existed, a card showed a
/// working directory and nothing else, so a root holding several projects gave
/// no way to tell which one was loaded, let alone switch.
///
/// The rule that needs pinning is the SELECTION rule: a session that loads a
/// project also loads its project references, so several candidates look
/// "loaded" — and a single <select> with two `selected` options shows whichever
/// the browser picked last, which is not the project the user chose. Exactly one
/// option may be selected.

open Expecto
open Expecto.Flip
open SageFs.Server.DashboardFragments

let private discovered solutions projects : SageFs.Server.DashboardTypes.DiscoveredProjects =
  { WorkingDir = "/w"; Solutions = solutions; Projects = projects }

let private selectedOf choices =
  choices |> List.filter (fun (_, _, isSelected) -> isSelected) |> List.map (fun (v, _, _) -> v)

[<Tests>]
let tests =
  testList "Session card project picker" [

    testCase "WHY — the loaded project is pre-selected, so the card states which project the session is on" <| fun _ ->
      projectChoices (discovered [] [ "a/A.fsproj"; "b/B.fsproj" ]) [ "/w/b/B.fsproj" ]
      |> selectedOf
      |> Expect.equal "the loaded one" [ "b/B.fsproj" ]

    testCase "WHY — EXACTLY one option is selected even when project references make several look loaded" <| fun _ ->
      // A test project loads itself AND the project it references.
      projectChoices
        (discovered [] [ "T/T.Tests.fsproj"; "C/C.fsproj" ])
        [ "/w/T/T.Tests.fsproj"; "/w/C/C.fsproj" ]
      |> selectedOf
      |> Expect.equal "first match only, never two" [ "T/T.Tests.fsproj" ]

    testCase "WHY — a session that loaded nothing selects nothing, rather than implying a project it does not have" <| fun _ ->
      projectChoices (discovered [] [ "a/A.fsproj" ]) []
      |> selectedOf
      |> Expect.isEmpty "no phantom selection"

    testCase "WHY — the only project, already loaded, renders no picker: there is nothing to choose" <| fun _ ->
      projectChoices (discovered [] [ "a/A.fsproj" ]) [ "/w/a/A.fsproj" ]
      |> Expect.isEmpty "no pointless control"

    testCase "WHY — the only project NOT loaded still renders, because switching to it is a real action" <| fun _ ->
      projectChoices (discovered [] [ "a/A.fsproj" ]) []
      |> List.length
      |> Expect.equal "offered" 1

    testCase "WHY — a solution is offered and labelled as loading everything, because that is what picking it does" <| fun _ ->
      let choices = projectChoices (discovered [ "All.slnx" ] [ "a/A.fsproj" ]) []
      choices |> List.head |> (fun (value, label, _) ->
        value |> Expect.equal "value is the path" "All.slnx"
        label |> Expect.stringContains "says it loads everything" "whole solution")

    testCase "WHY — matching is by file name, so a relative candidate still matches an absolute loaded path" <| fun _ ->
      // Two candidates, so the "only project and it's loaded" suppression above
      // does not apply and the selection rule is what is under test.
      projectChoices (discovered [] [ "nested/deep/A.fsproj"; "other/B.fsproj" ]) [ "/abs/elsewhere/A.fsproj" ]
      |> selectedOf
      |> Expect.equal "name match" [ "nested/deep/A.fsproj" ]
  ]
