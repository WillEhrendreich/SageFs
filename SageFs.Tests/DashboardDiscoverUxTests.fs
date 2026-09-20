/// Pure decision-logic tests for the New Session / Discover UX fixes
/// (sagefs-roast.md addendum, "session-creation flow" pass):
///   1. Create with an empty directory must give visible feedback.
///   2. The Discover preview must say exactly what Create will do.
///   3. The preview must name the real button ("Create").
///   4. Discover results must be selectable, not inert text.
///   6. The working-directory placeholder must match the running platform.
/// These are pure functions in DashboardFragments.fs — no HTTP, no DOM, no
/// IO — so the decisions are tested directly rather than through rendered
/// markup or a browser.
module SageFs.Tests.DashboardDiscoverUxTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private discoveredWith (solutions: string list) (projects: string list) : DiscoveredProjects =
  { WorkingDir = "/repo"; Solutions = solutions; Projects = projects }

let placeholderTests = testList "workingDirPlaceholderFor" [
  testCase "Windows gets a backslash path" <| fun _ ->
    workingDirPlaceholderFor true
    |> Expect.equal "should be the Windows convention" @"C:\path\to\project"

  testCase "non-Windows gets a forward-slash path" <| fun _ ->
    workingDirPlaceholderFor false
    |> Expect.equal "should be the POSIX convention" "/path/to/project"
]

let describeSessionLoadPlanTests = testList "describeSessionLoadPlan" [
  testCase "an escaping/unsafe project surfaces the SageFsError, not a generic message" <| fun _ ->
    let err = SageFsError.UnsafeSessionPath("/outside/X.fsproj", "project path escapes the session working directory")
    describeSessionLoadPlan (discoveredWith [] []) (Error err)
    |> Expect.equal "should be the error's own description" (SageFsError.describe err)

  testCase "no projects resolved says so plainly" <| fun _ ->
    describeSessionLoadPlan (discoveredWith [] []) (Ok [])
    |> Expect.equal "should explain there is nothing to load"
      "No projects found. Enter paths manually or check the directory."

  // roast-9 #2: Discover found 30+ projects in the repo and advertised "Will
  // load all projects", while Create actually resolved through
  // resolveSessionProjects, which prefers the first solution and discards
  // every standalone project — loading exactly ["SageFs.slnx"]. The preview
  // must describe THAT resolution, including how many of the discovered
  // projects the solution actually accounts for.
  testCase "a resolved solution reports the solution AND how many discovered projects it covers" <| fun _ ->
    let discovered =
      discoveredWith [ "SageFs.slnx" ] (List.init 30 (fun i -> sprintf "Proj%d/Proj%d.fsproj" i i))
    describeSessionLoadPlan discovered (Ok [ "/repo/SageFs.slnx" ])
    |> Expect.equal "should name the real button and the real project count"
      "Will load SageFs.slnx (the whole solution — 30 projects found are part of it). Click 'Create' to proceed."

  testCase "a resolved solution with exactly one other discovered project uses the singular" <| fun _ ->
    let discovered = discoveredWith [ "App.sln" ] [ "App/App.fsproj" ]
    describeSessionLoadPlan discovered (Ok [ "/repo/App.sln" ])
    |> Expect.equal "should say '1 project ... is', not '1 projects ... are'"
      "Will load App.sln (the whole solution — 1 project found is part of it). Click 'Create' to proceed."

  testCase "a resolved solution with no other discovered projects omits the count clause" <| fun _ ->
    let discovered = discoveredWith [ "App.sln" ] []
    describeSessionLoadPlan discovered (Ok [ "/repo/App.sln" ])
    |> Expect.equal "should not claim projects were found when none were"
      "Will load App.sln (the whole solution). Click 'Create' to proceed."

  testCase "a single resolved project (not a solution) names it, not 'the whole solution'" <| fun _ ->
    let discovered = discoveredWith [] [ "MyProject.fsproj" ]
    describeSessionLoadPlan discovered (Ok [ "/repo/MyProject.fsproj" ])
    |> Expect.equal "should describe loading exactly one project"
      "Will load 1 project: MyProject. Click 'Create' to proceed."

  testCase "several manually-picked projects are listed by name" <| fun _ ->
    let discovered = discoveredWith [] [ "A/A.fsproj"; "B/B.fsproj"; "C/C.fsproj" ]
    describeSessionLoadPlan discovered (Ok [ "/repo/A/A.fsproj"; "/repo/B/B.fsproj"; "/repo/C/C.fsproj" ])
    |> Expect.equal "should name the real button and list every project"
      "Will load 3 projects: A, B, C. Click 'Create' to proceed."

  testCase "the preview never says 'Create Session' — that button does not exist" <| fun _ ->
    let discovered = discoveredWith [ "X.sln" ] []
    describeSessionLoadPlan discovered (Ok [ "/repo/X.sln" ])
    |> (fun s -> s.Contains "Create Session")
    |> Expect.isFalse "the button is labelled 'Create', not 'Create Session' (roast-9 #3)"
]

let toggleManualProjectTests = testList "toggleManualProject" [
  testCase "selecting into an empty field sets it" <| fun _ ->
    toggleManualProject "" "A.fsproj"
    |> Expect.equal "should just be the selected path" "A.fsproj"

  testCase "selecting a second project appends it" <| fun _ ->
    toggleManualProject "A.fsproj" "B.fsproj"
    |> Expect.equal "should append with a comma" "A.fsproj, B.fsproj"

  testCase "selecting an already-selected project deselects it (toggle)" <| fun _ ->
    toggleManualProject "A.fsproj, B.fsproj" "A.fsproj"
    |> Expect.equal "should remove the toggled entry" "B.fsproj"

  testCase "whitespace around existing entries is tolerated" <| fun _ ->
    toggleManualProject "  A.fsproj ,B.fsproj " "B.fsproj"
    |> Expect.equal "should trim before comparing" "A.fsproj"

  testCase "matching is case-sensitive (Linux/macOS paths are case-sensitive)" <| fun _ ->
    toggleManualProject "A.fsproj" "a.fsproj"
    |> Expect.equal "different casing must not be treated as the same file" "A.fsproj, a.fsproj"

  testCase "deselecting the only entry empties the field" <| fun _ ->
    toggleManualProject "A.fsproj" "A.fsproj"
    |> Expect.equal "should be empty" ""
]

[<Tests>]
let tests =
  testList "Dashboard discover UX (pure decisions)" [
    placeholderTests
    describeSessionLoadPlanTests
    toggleManualProjectTests
  ]
