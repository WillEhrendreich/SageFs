/// Proves the Wave-1 domain model actually models the plan (demo-gif-plan.md
/// §5, §6, §6.1): the flagship scenario constructs, the derived id matches
/// the plan's own worked example, the caption smart constructor is total,
/// and the matrix comprehension shape from §6 type-checks. None of these
/// tests call a planner stub (`Motion`, `Cadence`, …) — those are Wave 2.
module SageFs.Demos.Tests.DomainTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain

/// `hr-dashboard-vscode-web`: edit a Falco/Datastar web app in VS Code, save,
/// watch the running site repaint — narrated from the dashboard. Copied from
/// demo-gif-plan.md §6.1 so a change to that worked example is felt here.
let hrDashboardVscodeWeb : Scenario =
  { Id = ScenarioId.derive Capability.HotReload Client.VsCode AppKind.Web
    Capability = Capability.HotReload
    Client = Client.VsCode
    App = AppKind.Web
    Sample = Sample.WebappDatastar
    Layout = LayoutTemplate.EditorLeft
    Cost = CostClass.web
    Masks = [ Region.clock ]
    Steps =
      [ { Caption = Caption.mk "Run the web app"
          Action = Action.Setup (ClientCommand.OpenFile SampleFile.homePage)
          Expect = Expectation.EditorSaved SampleFile.homePage
          Dwell = Dwell.short }
        { Caption = Caption.mk "1/4 · Press Run on the session card"
          Action = Action.Click (Target.DashboardElement DashboardId.runApp)
          Expect = Expectation.AppState AppRunStateCase.Running
          Dwell = Dwell.medium }
        { Caption = Caption.mk "2/4 · Change the heading"
          Action =
            Action.Type (
              Target.EditorPosition (SampleFile.homePage, 12, 20),
              Text.mk "SageFs is live",
              CadenceSeed.ofId "hr-dashboard-vscode-web"
            )
          Expect = Expectation.EditorSaved SampleFile.homePage
          Dwell = Dwell.short }
        { Caption = Caption.mk "3/4 · Save"
          Action = Action.Chord [ Key.Ctrl; Key.S ]
          Expect = Expectation.EditorSaved SampleFile.homePage
          Dwell = Dwell.short }
        { Caption = Caption.mk "4/4 · The running site repaints — no reload"
          Action = Action.Await Signal.appOutputChanged
          Expect = Expectation.AppOutputChanged Region.appHeading
          Dwell = Dwell.long } ] }

[<Tests>]
let tests =
  testList "Domain" [

    testCase "the worked hero scenario constructs with 5 steps" <| fun _ ->
      hrDashboardVscodeWeb.Steps.Length |> Expect.equal "should have 5 steps" 5

    testCase "ScenarioId.derive matches the plan's worked example" <| fun _ ->
      ScenarioId.derive Capability.HotReload Client.VsCode AppKind.Web
      |> ScenarioId.value
      |> Expect.equal "hr-dashboard-vscode-web is derived from HotReload/VsCode/Web" "hr-dashboard-vscode-web"

    testCase "ScenarioId.derive matches every hero-six id in the plan" <| fun _ ->
      let cases =
        [ (Capability.HotReload, Client.VsCode, AppKind.Web), "hr-dashboard-vscode-web"
          (Capability.HotReload, Client.Neovim, AppKind.Raylib), "hr-neovim-neovim-raylib"
          (Capability.HotReload, Client.VsCode, AppKind.Console), "hr-dashboard-vscode-console"
          (Capability.LiveTesting, Client.VsCode, AppKind.NoApp), "lt-vscode"
          (Capability.Repl, Client.Neovim, AppKind.NoApp), "repl-neovim"
          (Capability.Sessions, Client.Dashboard, AppKind.NoApp), "sessions-dashboard" ]
      for (capability, client, appKind), expected in cases do
        ScenarioId.derive capability client appKind
        |> ScenarioId.value
        |> Expect.equal (sprintf "%A/%A/%A should derive %s" capability client appKind expected) expected

    testCase "Caption.mk is total: a caption at the 70-char limit is unchanged" <| fun _ ->
      let exactly70 = String.replicate 70 "x"
      exactly70
      |> Caption.mk
      |> Caption.value
      |> Expect.equal "70-char caption round-trips unchanged" exactly70

    testCase "Caption.mk truncates a caption over the 70-char limit (§9)" <| fun _ ->
      let tooLong = String.replicate 100 "x"
      let result = tooLong |> Caption.mk |> Caption.value
      result.Length |> Expect.equal "truncated caption is exactly 70 chars" 70
      result |> Expect.equal "truncated caption is the first 70 chars of the input" (tooLong.Substring(0, 70))

    testCase "the §6 matrix comprehension shape type-checks over Client.all × Sample.runnable" <| fun _ ->
      Client.all |> Expect.equal "three clients" [ Client.Dashboard; Client.VsCode; Client.Neovim ]
      Sample.runnable
      |> Expect.equal
        "three runnable samples (FromCSharp is live-testing-only)"
        [ Sample.WebappDatastar; Sample.RaylibGame; Sample.ConsoleTicker ]
      let hotReloadPairs = [ for client in Client.all do for sample in Sample.runnable -> client, sample ]
      hotReloadPairs.Length
      |> Expect.equal "3 clients × 3 runnable samples = 9 hot-reload scenarios (§6)" 9

    testCase "DashboardId.testId is exhaustive and stable" <| fun _ ->
      DashboardId.runApp |> DashboardId.testId |> Expect.equal "run-app data-testid" "run-app"
  ]
