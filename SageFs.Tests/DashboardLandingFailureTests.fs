/// The landing page says why it fell back, and it draws the same cohort view the stream draws.
///
/// `renderLanding` caught every exception and rendered the no-session page with nothing to say a read had failed, so a
/// live session's page opened on "No session" beside a sidebar that listed the session. It also never applied the cohort
/// viewing the stream applies, so a running cohort looked different on the first paint than on the first push.
module SageFs.Tests.DashboardLandingFailureTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.Dashboard
open SageFs.Server.DashboardRequests
open SageFs.Tests.DashboardPanelVisibilityTests

let private at = DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc)

let private liveSession : WorkerProtocol.SessionInfo =
  { Id = WorkerProtocol.SessionId.validate "0a0b0c0d" |> Result.defaultWith failwith
    Name = None
    Projects = []
    WorkingDirectory = "/w"
    SolutionRoot = None
    CreatedAt = at
    LastActivity = at
    Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 4242; Port = Some 5000 }
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning; Rebuild = LastRebuild.NeverRebuilt; Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync }

let private throwingReason = "the worker did not answer"

let private queriesWhereASessionReadThrows =
  { DashboardRenderGateTests.mkQueries (ref 0) [ liveSession ] with
      GetSessionWorkflow = fun _ -> failwith throwingReason }

let private queriesThatWork = DashboardRenderGateTests.mkQueries (ref 0) [ liveSession ]

let private runningCohort = memberHoldingAClaim

let private infraWithACohort : DashboardInfra =
  { DashboardRenderGateTests.mkInfra () with
      ReadCohortFrame = fun () -> frameOf runningCohort
      ReadCohortLedger = fun () -> runningCohort }

let private landing (queries: DashboardQueries) (infra: DashboardInfra) : Task<string> =
  task {
    let! page = renderLanding queries infra ""
    return renderNode page
  }

[<Tests>]
let tests =
  testList "Dashboard landing page failure and cohort view" [
    testTask "WHY — a read that throws shows a notice that names the failure and what to do" {
      let! (html: string) = landing queriesWhereASessionReadThrows (DashboardRenderGateTests.mkInfra ())
      html |> Expect.stringContains "a notice is on the page" "action-result"
      html |> Expect.stringContains "it names the failure" throwingReason
      html |> Expect.stringContains "it says what the page shows instead" "session picker"
      html |> Expect.stringContains "it names the next action" "Reload the page"
    }

    testTask "WHY — the notice sits outside the main area, so the stream's morph cannot wipe it" {
      let! (html: string) = landing queriesWhereASessionReadThrows (DashboardRenderGateTests.mkInfra ())
      let notice = html.IndexOf("action-result", StringComparison.Ordinal)
      let picker = html.LastIndexOf("id=\"session-picker\"", StringComparison.Ordinal)
      (notice, picker) |> Expect.isGreaterThan "the notice follows the main area's content"
    }

    testTask "WHY — the fallback page is still the whole shell" {
      let! html = landing queriesWhereASessionReadThrows (DashboardRenderGateTests.mkInfra ())
      html |> Expect.stringContains "the main area" "id=\"main\""
      html |> Expect.stringContains "the picker" "id=\"session-picker\""
    }

    testTask "WHY — a page whose reads all work carries no failure notice" {
      let! (html: string) = landing queriesThatWork (DashboardRenderGateTests.mkInfra ())
      html.Contains "action-result" |> Expect.isFalse "nothing failed, so nothing is said"
    }

    testTask "WHY — a page with no session at all carries no failure notice either" {
      let! (html: string) = landing (DashboardRenderGateTests.mkQueries (ref 0) []) (DashboardRenderGateTests.mkInfra ())
      html.Contains "action-result" |> Expect.isFalse "having no session is not a failure"
    }

    testTask "WHY — with a cohort running and a session in view the first paint has the scrub control the stream's first push has" {
      let! html = landing queriesThatWork infraWithACohort
      html |> Expect.stringContains "the live scrubber" "id=\"cohort-scrubber\""
    }

    testCase "WHY — the sentence carries the reason it is given" <| fun _ ->
      LandingFailure.sentence "boom" |> Expect.stringContains "reason" "boom"
  ]
