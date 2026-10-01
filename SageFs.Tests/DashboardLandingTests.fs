/// The page a browser gets for `/dashboard`, and what it asked for. A page
/// records its friction opt-in (`?panels=friction`) under a client id it makes up,
/// and its stream reads that choice back under the same id. The landing page has
/// a fallback for when building its snapshot throws, and the fallback used to make
/// a NEW client id and record nothing, so a page that had asked for the friction
/// panel came back without it, for good, because its stream then read the choice
/// under an id that never had one. It failed one full browser-tier run in three
/// under load, on a machine where a worker call briefly threw.
module SageFs.Tests.DashboardLandingTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.Dashboard

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

/// The queries the landing page makes for a session it is about to show: a live
/// session exists, and reading its workflow throws, as a busy worker can.
let private queriesWhereASessionReadThrows =
  { DashboardRenderGateTests.mkQueries (ref 0) [ liveSession ] with
      GetSessionWorkflow = fun _ -> failwith "the worker did not answer" }

let private queriesThatWork = DashboardRenderGateTests.mkQueries (ref 0) [ liveSession ]

let private landingHtml (queries: DashboardQueries) (panels: string) : Task<string> =
  task {
    let! page = renderLanding queries (DashboardRenderGateTests.mkInfra ()) panels
    return renderNode page
  }

[<Tests>]
let tests =
  testList "Dashboard landing page" [
    testTask "WHY — a page that asked for the friction panel has it when everything answers" {
      let! html = landingHtml queriesThatWork "friction"
      html |> Expect.stringContains "the opt-in is honored" "id=\"friction-panel\""
    }

    testTask "WHY — a page that did not ask for the friction panel does not get it" {
      let! (html: string) = landingHtml queriesThatWork ""
      html.Contains("id=\"friction-panel\"") |> Expect.isFalse "the default layout has no friction panel"
    }

    testTask "WHY — a page that asked for the friction panel still has it when building its snapshot throws" {
      let! html = landingHtml queriesWhereASessionReadThrows "friction"
      html |> Expect.stringContains "the fallback page keeps the opt-in it was asked for" "id=\"friction-panel\""
    }

    testTask "WHY — the fallback page is still the whole shell, so the stream has somewhere to morph into" {
      let! html = landingHtml queriesWhereASessionReadThrows ""
      html |> Expect.stringContains "the main area is there" "id=\"main\""
      html |> Expect.stringContains "the session picker is there" "id=\"session-picker\""
    }
  ]
