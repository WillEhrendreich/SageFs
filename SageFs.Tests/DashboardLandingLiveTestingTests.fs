module SageFs.Tests.DashboardLandingLiveTestingTests

/// The dashboard's no-session landing shows the live-testing activity of every live session. It used to ask
/// for the activity of the empty session id, which names the primary session's cycle alone, so a daemon whose
/// primary session had live testing off showed "Live Testing: OFF" and an Enable button while another session
/// was running tests.

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.Dashboard
open SageFs.Tests.DashboardRenderGateTests

type Activity =SageFs.Features.LiveTestActivity.LiveTestActivity

let liveSession(id: WorkerProtocol.SessionId) (workingDirectory: string) : WorkerProtocol.SessionInfo =
  { Id = id
    Name = None
    Projects = []
    WorkingDirectory = workingDirectory
    SolutionRoot = None
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = Some 5000 }
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet
    Freshness = ReplFreshness.InSync }

let sessionId(hex: string) : WorkerProtocol.SessionId =
  WorkerProtocol.SessionId.validate hex |> Result.defaultValue (WorkerProtocol.SessionId.newId ())

/// The activity each session id answers, and Off for any id the table does not name (the empty id included).
let queriesAnswering(sessions: WorkerProtocol.SessionInfo list) (answers: Map<string, Activity>) : DashboardQueries =
  { mkQueries (ref 0) sessions with
      GetLiveTestActivity = fun id -> answers |> Map.tryFind id |> Option.defaultValue Activity.Off
      GetSinceGreen = fun _ -> SageFs.Features.Tweak.SinceGreen.Green.NoGreenYet }

let landingPanel(sessions: WorkerProtocol.SessionInfo list) (answers: Map<string, Activity>) =
  task {
    let! snap = buildNoSessionSnapshotWithSessions (queriesAnswering sessions answers) (mkInfra ()) sessions
    return renderNode snap.LiveTestingPanel
  }

[<Tests>]
let tests =
  testList "Dashboard no-session landing: live testing activity" [

    testTask "WHY — a session running tests shows on the landing while the primary session has live testing off" {
      let primary = sessionId "0a000001"
      let other = sessionId "0b000002"
      let sessions = [ liveSession primary "/w/primary"; liveSession other "/w/other" ]
      let answers =
        Map.ofList
          [ WorkerProtocol.SessionId.value other,
            Activity.Settled { Features.LiveTestActivity.TestTally.empty with Passed = 3 } ]
      let! html = landingPanel sessions answers
      html |> Expect.stringContains "the other session's three tests are shown" "All 3 tests passed"
      html.Contains "Live Testing: OFF" |> Expect.isFalse "the panel does not say off"
      html |> Expect.stringContains "and offers to turn it off, because it is on" "/dashboard/live-testing/disable"
    }

    testTask "WHY — the landing adds up the tests of every session that has live testing on" {
      let first = sessionId "0a000001"
      let second = sessionId "0b000002"
      let sessions = [ liveSession first "/w/first"; liveSession second "/w/second" ]
      let answers =
        Map.ofList
          [ WorkerProtocol.SessionId.value first, Activity.Settled { Features.LiveTestActivity.TestTally.empty with Passed = 2 }
            WorkerProtocol.SessionId.value second, Activity.Settled { Features.LiveTestActivity.TestTally.empty with Passed = 3 } ]
      let! html = landingPanel sessions answers
      html |> Expect.stringContains "2 + 3 tests" "All 5 tests passed"
    }

    testTask "WHY — with no session running live testing the landing says it is off and offers to enable it" {
      let sessions = [ liveSession (sessionId "0a000001") "/w/primary" ]
      let! html = landingPanel sessions Map.empty
      html |> Expect.stringContains "off" "Live Testing: OFF"
      html |> Expect.stringContains "and offers to enable it" "/dashboard/live-testing/enable"
    }

    testTask "WHY — with no sessions at all the landing says live testing is off" {
      let! html = landingPanel [] Map.empty
      html |> Expect.stringContains "off" "Live Testing: OFF"
    }
  ]
