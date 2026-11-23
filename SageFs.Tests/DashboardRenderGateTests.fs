module SageFs.Tests.DashboardRenderGateTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.Dashboard

/// Roast-5 item #3: the SSE push loop (`pushState` in Dashboard.fs) already
/// fetches `GetAllSessions` once per tick to reconcile the viewed session
/// before it decides what to render. Before this fix, both
/// `buildDashboardSnapshot`/`buildNoSessionSnapshot` (called next, to build
/// the actual page) fetched the SAME session list again internally via
/// `buildSessionCards` — a second `GetAllSessions` read on every single SSE
/// tick, for every open dashboard connection, even when nothing changed.
///
/// The fix: `buildDashboardSnapshotWithSessions` / `buildNoSessionSnapshotWithSessions`
/// / `buildSessionCardsFrom` take the session list as a parameter instead of
/// fetching it, and `pushState` passes the list it already has. The
/// standalone `buildDashboardSnapshot` / `buildNoSessionSnapshot` entry
/// points (used by tests and one-off callers with no list in hand) still
/// fetch exactly once.

let mkQueries (getAllSessionsCount: int ref) (sessions: WorkerProtocol.SessionInfo list) : DashboardQueries =
  { GetSessionState = fun _ -> SessionState.Ready
    GetStatusMsg = fun _ -> None
    GetEvalStats = fun _ -> System.Threading.Tasks.Task.FromResult(SageFs.Affordances.EvalStats.empty)
    GetFrictionStore = fun () -> System.Threading.Tasks.Task.FromResult None
    GetSessionWorkingDir = fun _ -> "/w"
    GetElmRegionsForSession = fun _ -> None
    GetPreviousSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
    GetAllSessions = fun () ->
      getAllSessionsCount.Value <- getAllSessionsCount.Value + 1
      System.Threading.Tasks.Task.FromResult sessions
    GetHotReloadState = fun _ -> System.Threading.Tasks.Task.FromResult None
    GetWarmupContext = fun _ -> System.Threading.Tasks.Task.FromResult None
    GetWarmupProgress = fun _ -> ""
    GetSessionTestSummary = fun _ -> None
    GetSessionCoverageSummary = fun _ -> None
    GetSessionTestTreemap = fun _ -> [||]
    GetSessionCoverageTreemap = fun _ -> None
    GetSessionBindings = fun _ -> [||]
    GetLiveBindings = fun _ -> None
    GetAppOutput = fun _ -> None
    SetAppOutput = fun _ _ -> ()
    GetTweakView = fun _ _ _ _ _ -> System.Threading.Tasks.Task.FromResult SageFs.Features.Tweak.BindingTweak.TweakView.none
    GetBindingScopeSnapshot = fun () -> None
    GetLiveTestingStatus = fun _ -> ""
    GetLiveTestActivity = fun _ -> SageFs.Features.LiveTestActivity.LiveTestActivity.Off
    GetSinceGreen = fun _ -> SageFs.Features.Tweak.SinceGreen.Green.NoGreenYet
    GetEvalTimeline =
      fun () -> SageFs.Features.EvalTimeline.TimelineState.empty |> SageFs.Features.EvalTimeline.timelineStats 20
    GetDaemonHealth = fun () -> None
    GetFailureNarratives = fun _ -> []
    GetCurrentDiagnostics = fun () -> []
    GetFilmstripEntries = fun () -> []
    GetTestSourceLocations = fun () -> []
    GetSessionAgentBadges = fun _ -> []
    GetSessionGuidanceCss = fun _ -> ""
    GetSessionSelfHostStaleness = fun _ -> None
    GetSessionWorkflow = fun _ -> WorkflowTypes.SessionWorkflow.Interactive
    GetSessionActiveProject = fun _ -> None
    GetSessionProjectRoles = fun _ -> []
    GetSessionApp = fun _ -> SageFs.AppRun.AppRunState.NotRunning
    GetSessionEvalCounts = fun () -> Map.empty
    IsCreatingSession = fun () -> false }

let mkInfra () : DashboardInfra =
  { Version = "0.0.0"
    McpPort = 37749
    DaemonStartedAt = DateTimeOffset.UnixEpoch
    // Non-optional (roast-6 Phase 0 item 1): these tests never exercise the
    // SSE stream, so a never-firing event is a faithful "no push happens" stand-in.
    StateChanged = (Event<SseEvent>()).Publish
    ConnectionTracker = None
    SessionThemes = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
    GetCompletions = fun _ _ _ -> System.Threading.Tasks.Task.FromResult []
    GetSessionCount = fun () -> System.Threading.Tasks.Task.FromResult 0
    SystemAlarmBuffer = ref []
    TriggerStateChange = fun () -> ()
    ConnectionChannels = System.Collections.Concurrent.ConcurrentDictionary<string, MailboxProcessor<DashboardStreamCommand>>()
    Teardowns = SageFs.Server.TeardownRegistry.Registry()
    ActivityTracker = None
    LiveBindingsAdaptive = None
    ReadCohortFrame = fun () -> SageFs.Cohort.project (SageFs.Cohort.replayHead []) [||]
    ReadHolderSilence = fun () -> SageFs.Features.CohortStatusText.HolderSilence.NotSilent
    ReadCohortLedger = fun () -> []
    ReadTrunk = fun () -> SageFs.Features.TrunkFollow.initial
    SeatConductor = fun _ -> System.Threading.Tasks.Task.FromResult(Result.Error "no cohort owner in this fixture")
    ReplaceConductor = fun _ -> System.Threading.Tasks.Task.FromResult(Result.Error "no cohort owner in this fixture") }

[<Tests>]
let tests = testList "Dashboard render gate — no redundant GetAllSessions per push" [
  testTask "WHY — buildDashboardSnapshotWithSessions reuses a pre-fetched session list because pushState already fetched one this tick to reconcile the viewed session" {
    let counter = ref 0
    let sid = WorkerProtocol.SessionId.validate "0a000001" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let queries = mkQueries counter []
    let! _ =
      buildDashboardSnapshotWithSessions (SageFs.Tests.DashboardSnapshotTests.snapshotInput queries (mkInfra ()) sid (WorkerProtocol.SessionId.newId ()) None [] SageFs.Server.DashboardPanelContext.FrictionPanelOptIn.NotOptedIn)
    counter.Value |> Expect.equal "must not re-fetch sessions — the caller already has them" 0
  }

  testTask "WHY — buildNoSessionSnapshotWithSessions reuses a pre-fetched session list for the same reason" {
    let counter = ref 0
    let queries = mkQueries counter []
    let! _ = buildNoSessionSnapshotWithSessions queries (mkInfra ()) []
    counter.Value |> Expect.equal "must not re-fetch sessions — the caller already has them" 0
  }

  testTask "WHY — buildSessionCardsFrom takes the session list directly and never calls GetAllSessions" {
    let counter = ref 0
    let queries = mkQueries counter []
    buildSessionCardsFrom queries Map.empty None [] |> ignore
    counter.Value |> Expect.equal "must not fetch sessions — they were passed in" 0
  }

  // WHY — the dashboard's warmup fetch has a shorter timeout than MCP's. When
  // it times out, no warmup context reaches the card. Saying "the worker did
  // not report its warmup" is a false claim about the worker (it always serves
  // one) and contradicts get_session_status on a Ready session. The honest
  // state is that this receipt was read without a source check.
  testTask "WHY — a failed source fetch says the receipt was not checked, not that the worker stayed silent" {
    let info : WorkerProtocol.SessionInfo =
      { Id = WorkerProtocol.SessionId.newId ()
        Name = None
        Projects = [ "Test.fsproj" ]
        WorkingDirectory = "."
        SolutionRoot = None
        CreatedAt = System.DateTime.UtcNow
        LastActivity = System.DateTime.UtcNow
        Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 999; Port = None }
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        ActiveProject = None
        ProjectRoles = [ { Path = "/w/Test.fsproj"; Role = SageFs.ProjectLoading.ProjectRole.Test; PackageRefs = []; LoadMode = SageFs.ProjectLoading.LoadMode.Evaluated; Build = SageFs.BuildOptimization.Unoptimized } ]
        App = SageFs.AppRun.AppRunState.NotRunning
        Rebuild = LastRebuild.NeverRebuilt
        Reload = SessionReload.NoReloadYet
        Freshness = SageFs.ReplFreshness.InSync }
    let state = SourceStateProbe.ofSession info None
    let note = SourceState.cardNote "." state
    match note with
    | CardNote.Note (_, details) ->
      details |> List.exists (fun d -> d.Contains "could not be read") |> Expect.isTrue "the note says the check failed"
      details |> List.exists (fun d -> d.Contains "worker did not report") |> Expect.isFalse "the worker is not blamed"
    | _ -> failtest "expected a note on the card"
  }

  testTask "WHY — a viewed session whose warmup fetch failed is not read, not blamed on the worker" {
    let counter = ref 0
    let queries = mkQueries counter []
    let sid = WorkerProtocol.SessionId.validate "0a000002" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let info : WorkerProtocol.SessionInfo =
      { Id = sid
        Name = None
        Projects = [ "Test.fsproj" ]
        WorkingDirectory = "."
        SolutionRoot = None
        CreatedAt = System.DateTime.UtcNow
        LastActivity = System.DateTime.UtcNow
        Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 999; Port = None }
        Workflow = WorkflowTypes.SessionWorkflow.Interactive
        ActiveProject = None
        ProjectRoles = [ { Path = "/w/Test.fsproj"; Role = SageFs.ProjectLoading.ProjectRole.Test; PackageRefs = []; LoadMode = SageFs.ProjectLoading.LoadMode.Evaluated; Build = SageFs.BuildOptimization.Unoptimized } ]
        App = SageFs.AppRun.AppRunState.NotRunning
        Rebuild = LastRebuild.NeverRebuilt
        Reload = SessionReload.NoReloadYet
        Freshness = SageFs.ReplFreshness.InSync }
    let cards : SageFs.Server.DashboardTypes.ParsedSession list =
      buildSessionCardsFrom queries Map.empty (Some (sid, None)) [ info ]
    let card = cards |> List.find (fun c -> c.Id = sid)
    match card.Source with
    | CardSource.Read (SourceState.Unknown (UnknownReason.LoadTimeNotReported detail)) ->
      detail |> Expect.stringContains "the check failed, the worker is not blamed" "could not be read"
    | other -> failtestf "expected an honest source-read failure, got %A" other
  }

  testTask "WHY — the standalone buildDashboardSnapshot still fetches sessions exactly once for callers with no list in hand" {
    let counter = ref 0
    let sid = WorkerProtocol.SessionId.validate "0a000001" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let queries = mkQueries counter []
    let! _ = buildDashboardSnapshot (SageFs.Tests.DashboardSnapshotTests.snapshotInput queries (mkInfra ()) sid (WorkerProtocol.SessionId.newId ()) None [] SageFs.Server.DashboardPanelContext.FrictionPanelOptIn.NotOptedIn)
    counter.Value |> Expect.equal "the standalone entry point fetches sessions exactly once" 1
  }

  testTask "WHY — the standalone buildNoSessionSnapshot still fetches sessions exactly once for callers with no list in hand" {
    let counter = ref 0
    let queries = mkQueries counter []
    let! _ = buildNoSessionSnapshot queries (mkInfra ())
    counter.Value |> Expect.equal "the standalone entry point fetches sessions exactly once" 1
  }

  testTask "WHY — buildNoSessionSnapshotWithSessions renders an empty cohort panel because cohort data is irrelevant without a selected session" {
    let! snap = buildNoSessionSnapshotWithSessions (mkQueries (ref 0) []) (mkInfra ()) []
    let html = renderNode snap.CohortPanel
    html.Contains("Cohort") |> Expect.isFalse "must not contain cohort panel heading"
    html.Contains("Lanes") |> Expect.isFalse "must not contain lanes panel"
    html.Contains("cohort-scrubber") |> Expect.isFalse "must not contain scrub control"
  }
]
