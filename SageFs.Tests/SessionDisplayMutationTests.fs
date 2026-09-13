/// ## SessionDisplay Mutation Tests
///
/// Proves the test suite catches mutations in the single-definition
/// SessionDisplayStatus derivation — the "what the user sees" mapping that
/// used to be declared (and could disagree) in two places. A swapped case
/// or a flipped staleness boundary here shows the wrong status on every
/// dashboard/MCP/TUI surface at once. Each case asserts EXACT equality
/// against the correct value so a mutant that returns any other wrong value
/// is killed too. (Confirmed stable with the agent that just finished
/// unifying this type — SessionDisplayStatus's case list and
/// displayStatus/snapshot logic are not in flux.)
module SessionDisplayMutationTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

let private mkInfo (status: SessionLifecycleStatus) (lastActivity: DateTime) : SessionInfo = {
  Id = SageFs.Tests.SharedGenerators.testSessionId "aa000001"
  Name = None
  Projects = ["Test.fsproj"]
  WorkingDirectory = @"C:\test"
  SolutionRoot = None
  CreatedAt = DateTime(2026, 1, 1)
  LastActivity = lastActivity
  Status = status
  Workflow = WorkflowTypes.SessionWorkflow.Interactive
  ActiveProject = None
  ProjectRoles = []
  App = SageFs.AppRun.AppRunState.NotRunning
}

let private handle = { Pid = 100; Port = Some 5000 }
let private now = DateTime(2026, 1, 1, 12, 0, 0)

let sessionDisplayMutationTests = testList "SessionDisplay mutations" [

  // ── displayStatus: per-case mapping ──────────────────────────────────────

  testCase "WHY — ready_fresh_is_Running_not_Stale" <| fun () ->
    let info = mkInfo (SessionLifecycleStatus.Ready handle) now
    SessionDisplay.displayStatus now info
    |> Expect.equal "a fresh Ready session must display Running" SessionDisplayStatus.Running

  testCase "WHY — evaluating_fresh_is_Running — Evaluating must display the same as Ready when fresh, not Starting" <| fun () ->
    let info = mkInfo (SessionLifecycleStatus.Evaluating handle) now
    SessionDisplay.displayStatus now info
    |> Expect.equal "a fresh Evaluating session must display Running" SessionDisplayStatus.Running

  testCase "WHY — building_fresh_is_Running — Building must display the same as Ready when fresh" <| fun () ->
    let info = mkInfo (SessionLifecycleStatus.Building ("dotnet build", handle)) now
    SessionDisplay.displayStatus now info
    |> Expect.equal "a fresh Building session must display Running" SessionDisplayStatus.Running

  testCase "WHY — starting_is_Starting_regardless_of_activity_age — Starting must never be reclassified as Stale" <| fun () ->
    let info = mkInfo (SessionLifecycleStatus.Starting handle) (now - TimeSpan.FromDays 1.0)
    SessionDisplay.displayStatus now info
    |> Expect.equal "Starting must display Starting even with old LastActivity" SessionDisplayStatus.Starting

  testCase "WHY — faulted_with_reason_carries_the_exact_reason_through — the reason must not be dropped or replaced with a generic string" <| fun () ->
    let info = mkInfo (SessionLifecycleStatus.Faulted (Some "OutOfMemoryException")) now
    SessionDisplay.displayStatus now info
    |> Expect.equal "Faulted(Some reason) must carry that EXACT reason into the display status" (SessionDisplayStatus.Faulted "OutOfMemoryException")

  testCase "WHY — faulted_with_no_reason_uses_the_fallback_text — a missing reason must use \"Session faulted\", not an empty string" <| fun () ->
    let info = mkInfo (SessionLifecycleStatus.Faulted None) now
    SessionDisplay.displayStatus now info
    |> Expect.equal "Faulted(None) must fall back to exactly \"Session faulted\"" (SessionDisplayStatus.Faulted "Session faulted")

  testCase "WHY — restarting_is_Restarting_not_Starting — these are visually and semantically distinct states" <| fun () ->
    let info = mkInfo (SessionLifecycleStatus.Restarting (Some 42)) now
    SessionDisplay.displayStatus now info
    |> Expect.equal "Restarting must display Restarting, never collapse into Starting" SessionDisplayStatus.Restarting

  testCase "WHY — stopped_is_Stopped_not_Faulted — a deliberate stop must not be shown as an error" <| fun () ->
    let info = mkInfo SessionLifecycleStatus.Stopped now
    SessionDisplay.displayStatus now info
    |> Expect.equal "Stopped must display Stopped, not Faulted" SessionDisplayStatus.Stopped

  // ── displayStatus: staleness boundary on the busy statuses ──────────────

  testCase "WHY — ready_exactly_at_stale_threshold_is_still_running — `>` not `>=`" <| fun () ->
    let checkAt = now + Timeouts.staleSessionThreshold
    let info = mkInfo (SessionLifecycleStatus.Ready handle) now
    SessionDisplay.displayStatus checkAt info
    |> Expect.equal "exactly at the stale threshold, elapsed is NOT > threshold, so it must still be Running" SessionDisplayStatus.Running

  testCase "WHY — ready_one_tick_past_stale_threshold_is_Stale" <| fun () ->
    let checkAt = now + Timeouts.staleSessionThreshold + TimeSpan.FromTicks 1L
    let info = mkInfo (SessionLifecycleStatus.Ready handle) now
    SessionDisplay.displayStatus checkAt info
    |> Expect.equal "one tick past the stale threshold, Ready must become Stale" SessionDisplayStatus.Stale

  // ── label / cssClass: total, distinct mappings ──────────────────────────

  testCase "WHY — label_is_distinct_and_lowercase_per_case" <| fun () ->
    [ SessionDisplayStatus.Running; SessionDisplayStatus.Starting; SessionDisplayStatus.Restarting
      SessionDisplayStatus.Faulted "x"; SessionDisplayStatus.Lost; SessionDisplayStatus.Stopped; SessionDisplayStatus.Stale ]
    |> List.map SessionDisplayStatus.label
    |> Expect.equal "labels must be exactly running,starting,restarting,faulted,lost,stopped,stale in that order"
      [ "running"; "starting"; "restarting"; "faulted"; "lost"; "stopped"; "stale" ]

  testCase "WHY — cssClass_running_is_status_ready_others_are_not — only Running gets the ready class" <| fun () ->
    [ SessionDisplayStatus.Running; SessionDisplayStatus.Starting; SessionDisplayStatus.Faulted "x" ]
    |> List.map SessionDisplayStatus.cssClass
    |> Expect.equal "Running -> status-ready, Starting -> status-warming, Faulted -> status-faulted"
      [ "status-ready"; "status-warming"; "status-faulted" ]

  testCase "WHY — cssClass_restarting_shares_warming_class_with_starting" <| fun () ->
    SessionDisplayStatus.cssClass SessionDisplayStatus.Restarting
    |> Expect.equal "Restarting must share the \"status-warming\" class with Starting" "status-warming"

  // ── ofSessionState ────────────────────────────────────────────────────────

  testCase "WHY — ofSessionState_maps_every_case_without_swapping" <| fun () ->
    [ Ready; Evaluating; WarmingUp; Faulted; Uninitialized ]
    |> List.map SessionDisplayStatus.ofSessionState
    |> Expect.equal "Ready/Evaluating -> Running, WarmingUp -> Starting, Faulted -> Faulted \"session faulted\", Uninitialized -> Lost"
      [ SessionDisplayStatus.Running; SessionDisplayStatus.Running; SessionDisplayStatus.Starting
        SessionDisplayStatus.Faulted "session faulted"; SessionDisplayStatus.Lost ]

  // ── ActiveSession ────────────────────────────────────────────────────────

  testCase "WHY — activeSession_sessionId_extracts_only_from_Viewing" <| fun () ->
    let id = SageFs.Tests.SharedGenerators.testSessionId "aa000002"
    (ActiveSession.sessionId (ActiveSession.Viewing id), ActiveSession.sessionId ActiveSession.AwaitingSession)
    |> Expect.equal "Viewing must yield Some id; AwaitingSession must yield None" (Some id, None)

  testCase "WHY — activeSession_isViewing_true_only_for_matching_id" <| fun () ->
    let id1 = SageFs.Tests.SharedGenerators.testSessionId "aa000003"
    let id2 = SageFs.Tests.SharedGenerators.testSessionId "aa000004"
    (ActiveSession.isViewing id1 (ActiveSession.Viewing id1),
     ActiveSession.isViewing id1 (ActiveSession.Viewing id2),
     ActiveSession.isViewing id1 ActiveSession.AwaitingSession)
    |> Expect.equal "isViewing must be true only for the SAME id being actively viewed" (true, false, false)

  // ── snapshot ─────────────────────────────────────────────────────────────

  testCase "WHY — snapshot_carries_fields_without_swapping — Id/Name/Projects/LastActivity/UpSince/WorkingDirectory must land in their OWN fields" <| fun () ->
    let info = mkInfo (SessionLifecycleStatus.Ready handle) now
    let snap = SessionDisplay.snapshot now info
    (snap.Id, snap.Projects, snap.LastActivity, snap.UpSince, snap.WorkingDirectory)
    |> Expect.equal "CreatedAt must land in UpSince (not LastActivity), and LastActivity must stay LastActivity"
      (info.Id, info.Projects, info.LastActivity, info.CreatedAt, info.WorkingDirectory)

  testCase "WHY — snapshot_evalCount_defaults_to_zero — the display snapshot does not compute EvalCount itself" <| fun () ->
    let info = mkInfo (SessionLifecycleStatus.Ready handle) now
    (SessionDisplay.snapshot now info).EvalCount |> Expect.equal "EvalCount must default to 0 in a bare snapshot" 0
]
