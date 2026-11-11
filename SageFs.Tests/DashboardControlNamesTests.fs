/// A control is named by the word on it, is big enough to hit, and a label never stands over an empty value.
///
/// Three adjacent buttons on a session card read Stop, Close and Delete on screen and Stop, Dispose and Purge to a
/// screen reader. The "timing" toggle was a 42 by 15 pixel text button, the smallest target on the page. The header
/// printed "Session:" with nothing after it when no session was in view.
module SageFs.Tests.DashboardControlNamesTests

open System
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Tests.DashboardPanelVisibilityTests

let private now = DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc)

let private info : WorkerProtocol.SessionInfo =
  { Id = WorkerProtocol.SessionId.validate "0a0b0c0d" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    Name = None
    Projects = [ "/repo/app/app.fsproj" ]
    WorkingDirectory = "/repo/app"
    SolutionRoot = None
    CreatedAt = now.AddMinutes -5.0
    LastActivity = now.AddMinutes -3.0
    Status = WorkerProtocol.SessionLifecycleStatus.ofWorkerReport (WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = None }) WorkerProtocol.SessionStatus.Ready
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet
    Freshness = ReplFreshness.InSync }

let private cardHtml : string =
  renderSessionsForSession "" [ sessionCardOf now None 0 SessionHealth.Healthy info ] false |> renderNode

/// Every `<button ...>...</button>` of the card whose class carries `session-btn`, as (aria-label, visible text).
let private cardButtons (html: string) : (string * string) list =
  [ for m in Regex.Matches(html, "<button[^>]*aria-label=\"([^\"]*)\"[^>]*>(.*?)</button>", RegexOptions.Singleline) ->
      m.Groups.[1].Value, Regex.Replace(m.Groups.[2].Value, "<[^>]*>", "").Trim() ]

let private allTeardown = [ TeardownButton.Stop; TeardownButton.Close; TeardownButton.Delete ]

[<Tests>]
let tests =
  testList "control names" [
    testCase "WHY — each teardown button's accessible name opens with the word drawn on it" <| fun _ ->
      for button in allTeardown do
        (TeardownButton.name button).StartsWith(TeardownButton.word button)
        |> Expect.isTrue (sprintf "%A: %s" button (TeardownButton.name button))

    testCase "WHY — the card's Stop, Close and Delete buttons are named Stop, Close and Delete, and Dispose and Purge are gone" <| fun _ ->
      let buttons = cardButtons cardHtml
      for button in allTeardown do
        let word = TeardownButton.word button
        match buttons |> List.tryFind (fun (_, visible) -> visible.EndsWith word) with
        | Some(name, _) -> name |> Expect.equal (sprintf "%s button's name" word) (TeardownButton.name button)
        | None -> failtestf "no button reads %s in %A" word buttons
      cardHtml.Contains "Dispose" |> Expect.isFalse "no second vocabulary for Close"
      cardHtml.Contains "Purge —" |> Expect.isFalse "no second vocabulary for Delete"

    testCase "WHY — the timing toggle is named by its word and says whether the detail is open" <| fun _ ->
      let html =
        renderEvalStats { Count = 3; AvgMs = 1.0; MinMs = 1.0; MaxMs = 2.0; Sparkline = ""; P50Ms = None; P95Ms = None }
        |> renderNode
      html |> Expect.stringContains "the name opens with the word on the button" (sprintf "aria-label=\"%s\"" timingButtonName)
      timingButtonName.StartsWith "Timing" |> Expect.isTrue "the name starts with the visible word"
      html |> Expect.stringContains "it reports its expanded state" "aria-expanded"

    testCase "WHY — the timing toggle is at least 1.5rem tall and its size is in rem" <| fun _ ->
      let rule = Regex.Matches(Dashboard.dashboardCss, @"\.perf-toggle-btn\s*\{[^}]*\}") |> Seq.map (fun m -> m.Value) |> String.concat "\n"
      rule |> Expect.stringContains "a min-height in rem" "min-height: 1.5rem"
      rule |> Expect.stringContains "a min-width in rem" "min-width: 2.75rem"
      rule.Contains "min-height: 1.5px" |> Expect.isFalse "the target size is not in pixels"

    testCase "WHY — with no session in view the header does not print a label with no value" <| fun _ ->
      let page snapshot = renderMainContent snapshot |> renderNode
      let none = page { snapshotWith off [] with SessionId = "" }
      none.Contains "tabline-session-label" |> Expect.isFalse "no Session: label with nothing after it"
      none |> Expect.stringContains "the empty switcher target is still on the page" DomIds.WorkflowSwitcher
      let some = page (snapshotWith off [])
      some |> Expect.stringContains "with a session the label and id are there" "tabline-session-label"
      some |> Expect.stringContains "and so is the id" "abcd1234"
  ]
