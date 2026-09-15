module SageFs.Tests.SessionCardActionsTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private mkCardSession (id: string) (projects: (string * ProjectLoading.ProjectRole) list) : ParsedSession =
  let sid = WorkerProtocol.SessionId.validate id |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
  { Id = sid; Status = SessionDisplayStatus.Running; StatusMessage = None
    ProjectsText = "(A.fsproj)"; EvalCount = 1
    Uptime = "1m"; WorkingDir = "/a"; LastActivity = "A"
    TestSummary = None; CoverageSummary = None; TestTreemapEntries = [||]; CoverageTreemap = None
    BindingEntries = [||]; AgentBadges = []; GuidanceCssClass = ""
    ActiveProject = None
    ProjectRoles =
      projects
      |> List.map (fun (path, role) -> { ProjectLoading.ClassifiedProject.Path = path; Role = role; PackageRefs = [] })
    App = AppRun.AppRunState.NotRunning
    WorkerRssBytes = None; SelfHostStaleness = None }

let private render viewing sessions =
  renderSessionsForSession viewing sessions false |> renderNode

[<Tests>]
let tests = testList "Session card actions" [
  test "WHY — run buttons — each executable's name renders inside a truncating label pill, never inside a fixed 28px icon box, because the text overflowed onto the neighbouring buttons" {
    let s =
      mkCardSession "0a2b3c4d" [
        "/a/SageFs.Tests.fsproj", ProjectLoading.ProjectRole.Executable
        "/a/SageFsWebAppFixture.fsproj", ProjectLoading.ProjectRole.Executable ]
    let html = render "0a2b3c4e" [ s ]
    html |> Expect.stringContains "labeled run button" "session-btn session-btn-primary session-btn-labeled"
    html |> Expect.stringContains "first name in a truncating label" "<span class=\"session-btn-label\">SageFs.Tests</span>"
    html |> Expect.stringContains "second name in a truncating label" "<span class=\"session-btn-label\">SageFsWebAppFixture</span>"
  }

  test "WHY — run buttons — a project name is HTML-encoded in its label because a hostile .fsproj name must not reach the DOM as markup" {
    let s =
      mkCardSession "0a2b3c4d" [
        "/a/<img src=x onerror=alert(1)>.fsproj", ProjectLoading.ProjectRole.Executable
        "/a/B.fsproj", ProjectLoading.ProjectRole.Executable ]
    let html = render "0a2b3c4e" [ s ]
    html.Contains "<img src=x" |> Expect.isFalse "raw markup from a project name must be encoded"
  }

  test "WHY — run buttons — a single executable keeps the square glyph button because one ▶ needs no label" {
    let s = mkCardSession "0a2b3c4d" [ "/a/App.fsproj", ProjectLoading.ProjectRole.Executable ]
    let html = render "0a2b3c4e" [ s ]
    html.Contains "session-btn-labeled" |> Expect.isFalse "a single project uses the square button"
  }

  test "WHY — session switching — cards and the switch button POST to /dashboard/session/switch because GET /dashboard ignores ?session= and every card reloaded onto the first session" {
    let html = render "0a2b3c4e" [ mkCardSession "0a2b3c4d" []; mkCardSession "0a2b3c4e" [] ]
    html.Contains "?session=" |> Expect.isFalse "no URL-driven navigation remains"
    html |> Expect.stringContains "a non-viewed card switches via the signal-synced POST" "@post('/dashboard/session/switch/0a2b3c4d')"
  }

  test "WHY — session switching — the viewed card has no switch handler because clicking the session you are already viewing must do nothing" {
    let html = render "0a2b3c4e" [ mkCardSession "0a2b3c4e" [] ]
    html.Contains "/dashboard/session/switch/0a2b3c4e" |> Expect.isFalse "the viewed card must not switch to itself"
  }

  test "WHY — session switching — the card ignores clicks that land on its own buttons and links because Stop/Run clicks bubbled into a switch" {
    let html = render "0a2b3c4e" [ mkCardSession "0a2b3c4d" [] ]
    html |> Expect.stringContains "card click guard" "evt.target.closest('button, a') ||"
  }
]
