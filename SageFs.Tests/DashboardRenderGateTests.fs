module SageFs.Tests.DashboardRenderGateTests

open System
open Expecto
open Expecto.Flip
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

let private mkQueries (getAllSessionsCount: int ref) (sessions: WorkerProtocol.SessionInfo list) : DashboardQueries =
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
    GetBindingScopeSnapshot = fun () -> None
    GetLiveTestingStatus = fun () -> ""
    GetLiveTestingActive = fun () -> false
    GetLiveTestActivity = fun _ -> SageFs.Features.LiveTestActivity.LiveTestActivity.Off
    GetEvalTimeline =
      fun () -> SageFs.Features.EvalTimeline.TimelineState.empty |> SageFs.Features.EvalTimeline.timelineStats 20
    GetDaemonHealth = fun () -> None
    GetFailureNarratives = fun () -> []
    GetCurrentDiagnostics = fun () -> []
    GetFilmstripEntries = fun () -> []
    GetTestSourceLocations = fun () -> []
    GetSessionAgentBadges = fun _ -> []
    GetSessionGuidanceCss = fun _ -> ""
    GetSessionWorkflow = fun _ -> WorkflowTypes.SessionWorkflow.Interactive
    GetSessionActiveProject = fun _ -> None
    GetSessionProjectRoles = fun _ -> []
    GetSessionApp = fun _ -> SageFs.AppRun.AppRunState.NotRunning
    GetSessionEvalCounts = fun () -> Map.empty
    IsCreatingSession = fun () -> false }

let private mkInfra () : DashboardInfra =
  { Version = "0.0.0"
    McpPort = 37749
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
    ActivityTracker = None
    LiveBindingsAdaptive = None }

[<Tests>]
let tests = testList "Dashboard render gate — no redundant GetAllSessions per push" [
  testTask "WHY — buildDashboardSnapshotWithSessions reuses a pre-fetched session list because pushState already fetched one this tick to reconcile the viewed session" {
    let counter = ref 0
    let sid = WorkerProtocol.SessionId.validate "0a000001" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let queries = mkQueries counter []
    let! _ =
      buildDashboardSnapshotWithSessions queries (mkInfra ()) sid (WorkerProtocol.SessionId.newId ()) "" "default" None []
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
    buildSessionCardsFrom queries [] |> ignore
    counter.Value |> Expect.equal "must not fetch sessions — they were passed in" 0
  }

  testTask "WHY — the standalone buildDashboardSnapshot still fetches sessions exactly once for callers with no list in hand" {
    let counter = ref 0
    let sid = WorkerProtocol.SessionId.validate "0a000001" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    let queries = mkQueries counter []
    let! _ = buildDashboardSnapshot queries (mkInfra ()) sid (WorkerProtocol.SessionId.newId ()) "" "default" None
    counter.Value |> Expect.equal "the standalone entry point fetches sessions exactly once" 1
  }

  testTask "WHY — the standalone buildNoSessionSnapshot still fetches sessions exactly once for callers with no list in hand" {
    let counter = ref 0
    let queries = mkQueries counter []
    let! _ = buildNoSessionSnapshot queries (mkInfra ())
    counter.Value |> Expect.equal "the standalone entry point fetches sessions exactly once" 1
  }
]
