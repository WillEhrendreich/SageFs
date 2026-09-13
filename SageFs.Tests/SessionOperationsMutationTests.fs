/// ## SessionOperations Mutation Tests
///
/// Proves the test suite catches mutations in the pure session-routing,
/// occupancy classification and multi-agent coordination logic — the shared
/// vocabulary every interface (MCP, HTTP, Neovim, CLI) routes through. Each
/// case asserts EXACT equality against the correct value so a mutant that
/// returns any other wrong value is killed too.
module SessionOperationsMutationTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.SessionOperations
open SageFs.Tests.SharedGenerators

let mkSession (id: SessionId) lastActive (status: SessionStatus) : SessionInfo = {
  Id = id
  Name = None
  Projects = ["Test.fsproj"]
  WorkingDirectory = sprintf @"C:\%s" (SessionId.value id)
  SolutionRoot = None
  CreatedAt = DateTime(2026, 1, 1)
  LastActivity = lastActive
  Status = SessionLifecycleStatus.ofWorkerReport (SessionLifecycleStatus.Ready { Pid = 100; Port = None }) status
  Workflow = WorkflowTypes.SessionWorkflow.Interactive
  ActiveProject = None
  ProjectRoles = []
  App = SageFs.AppRun.AppRunState.NotRunning
}

let sessionOperationsMutationTests = testList "SessionOperations mutations" [

  // ── OccupantRole.classify ────────────────────────────────────────────────

  testCase "WHY — classify_mcp_prefix_is_worker — a name starting with \"mcp\" must classify as Worker" <| fun () ->
    OccupantRole.classify "mcp-claude"
    |> Expect.equal "\"mcp-claude\" must classify as Worker" OccupantRole.Worker

  testCase "WHY — classify_agent_dash_prefix_is_worker — a name starting with \"agent-\" must classify as Worker" <| fun () ->
    OccupantRole.classify "agent-fork-1"
    |> Expect.equal "\"agent-fork-1\" must classify as Worker" OccupantRole.Worker

  testCase "WHY — classify_other_name_is_observer — a name matching neither prefix must classify as Observer" <| fun () ->
    OccupantRole.classify "dashboard-viewer"
    |> Expect.equal "\"dashboard-viewer\" must classify as Observer" OccupantRole.Observer

  testCase "WHY — classify_is_case_sensitive_ordinal — the prefix check must be exact-case ordinal, not case-insensitive" <| fun () ->
    OccupantRole.classify "MCP-claude"
    |> Expect.equal "\"MCP-claude\" (wrong case) must NOT match the \"mcp\" prefix — Observer, not Worker" OccupantRole.Observer

  testCase "WHY — classify_agent_without_dash_is_observer — bare \"agent\" (no trailing dash) must not match the \"agent-\" prefix" <| fun () ->
    OccupantRole.classify "agent"
    |> Expect.equal "\"agent\" alone must be Observer, not Worker" OccupantRole.Observer

  testCase "WHY — occupantRole_label_worker — label for Worker must be exactly \"worker\"" <| fun () ->
    OccupantRole.label OccupantRole.Worker |> Expect.equal "Worker labels as \"worker\"" "worker"

  testCase "WHY — occupantRole_label_observer — label for Observer must be exactly \"observer\"" <| fun () ->
    OccupantRole.label OccupantRole.Observer |> Expect.equal "Observer labels as \"observer\"" "observer"

  // ── resolveSession ───────────────────────────────────────────────────────

  testCase "WHY — resolveSession_multiple_no_id_picks_LATEST_activity — the tie-break must be most-recent, not oldest or first-in-list" <| fun () ->
    let oldId = testSessionId "bb000001"
    let midId = testSessionId "bb000002"
    let newId = testSessionId "bb000003"
    let old = mkSession oldId (DateTime(2026, 1, 1)) SessionStatus.Ready
    let mid = mkSession midId (DateTime(2026, 6, 1)) SessionStatus.Ready
    let recent = mkSession newId (DateTime(2026, 9, 1)) SessionStatus.Ready
    resolveSession None [mid; old; recent]
    |> Expect.equal "must pick the session with the LATEST LastActivity regardless of list order"
      (Result.Ok (SessionResolution.DefaultMostRecent newId))

  testCase "WHY — sessionId_extracts_from_Resolved" <| fun () ->
    let id = testSessionId "aa000001"
    sessionId (SessionResolution.Resolved id) |> Expect.equal "Resolved must unwrap to its id" id

  testCase "WHY — sessionId_extracts_from_DefaultSingle" <| fun () ->
    let id = testSessionId "aa000002"
    sessionId (SessionResolution.DefaultSingle id) |> Expect.equal "DefaultSingle must unwrap to its id" id

  testCase "WHY — sessionId_extracts_from_DefaultMostRecent" <| fun () ->
    let id = testSessionId "aa000003"
    sessionId (SessionResolution.DefaultMostRecent id) |> Expect.equal "DefaultMostRecent must unwrap to its id" id

  testCase "WHY — describeResolution_labels_each_case_distinctly — the three resolution kinds must produce visibly distinct descriptions" <| fun () ->
    let id = testSessionId "aa000004"
    (describeResolution (SessionResolution.Resolved id),
     describeResolution (SessionResolution.DefaultSingle id),
     describeResolution (SessionResolution.DefaultMostRecent id))
    |> Expect.equal "each resolution kind must describe its OWN reason, not a generic or swapped one"
      (sprintf "session %s (explicit)" (SessionId.value id),
       sprintf "session %s (only session)" (SessionId.value id),
       sprintf "session %s (most recently active)" (SessionId.value id))

  // ── SessionGuidance.compute ──────────────────────────────────────────────

  testCase "WHY — guidance_faulted_is_unhealthy — a Faulted session must guide Unhealthy, never Uncontested/OccupiedBy" <| fun () ->
    SessionGuidance.compute [] SessionStatus.Faulted
    |> Expect.equal "Faulted must be Unhealthy \"Session is faulted\"" (SessionGuidance.Unhealthy "Session is faulted")

  testCase "WHY — guidance_stopped_is_unhealthy — a Stopped session must guide Unhealthy with its own distinct reason" <| fun () ->
    SessionGuidance.compute [] SessionStatus.Stopped
    |> Expect.equal "Stopped must be Unhealthy \"Session is stopped\", not the Faulted reason" (SessionGuidance.Unhealthy "Session is stopped")

  testCase "WHY — guidance_ready_no_occupants_is_uncontested" <| fun () ->
    SessionGuidance.compute [] SessionStatus.Ready
    |> Expect.equal "Ready with no occupants must be Uncontested" SessionGuidance.Uncontested

  testCase "WHY — guidance_ready_with_worker_is_occupiedBy — a worker occupant must produce OccupiedBy, never Uncontested" <| fun () ->
    let occ = { AgentName = "mcp-x"; Role = OccupantRole.Worker }
    SessionGuidance.compute [occ] SessionStatus.Ready
    |> Expect.equal "a worker occupant must produce OccupiedBy [\"mcp-x\"]" (SessionGuidance.OccupiedBy ["mcp-x"])

  testCase "WHY — guidance_ready_with_only_observer_is_uncontested — an Observer-only occupant must NOT count as contested" <| fun () ->
    let occ = { AgentName = "dashboard"; Role = OccupantRole.Observer }
    SessionGuidance.compute [occ] SessionStatus.Ready
    |> Expect.equal "an observer-only occupant must still be Uncontested — only workers contest a session" SessionGuidance.Uncontested

  // ── SessionOccupancy.hasWorker ───────────────────────────────────────────

  testCase "WHY — hasWorker_true_when_any_worker_present" <| fun () ->
    let occs = [ { AgentName = "a"; Role = OccupantRole.Observer }; { AgentName = "b"; Role = OccupantRole.Worker } ]
    SessionOccupancy.hasWorker occs |> Expect.isTrue "a list containing one Worker among Observers must be true"

  testCase "WHY — hasWorker_false_when_all_observers" <| fun () ->
    let occs = [ { AgentName = "a"; Role = OccupantRole.Observer }; { AgentName = "b"; Role = OccupantRole.Observer } ]
    SessionOccupancy.hasWorker occs |> Expect.isFalse "an all-Observer list must be false"

  // ── AgentPresence.freshness ──────────────────────────────────────────────

  testCase "WHY — freshness_exactly_at_timeout_is_still_fresh — the boundary must use `>`, not `>=`" <| fun () ->
    let now = DateTime(2026, 1, 1, 0, 10, 0)
    let timeout = TimeSpan.FromMinutes 10.0
    let presence = { AgentName = "a"; Role = OccupantRole.Worker; SessionId = "s"; LastToolCall = DateTime(2026, 1, 1, 0, 0, 0); Intent = None; RecentFiles = []; EvalCount = 0 }
    AgentPresence.freshness now timeout presence
    |> Expect.equal "exactly at the timeout boundary, elapsed is NOT > timeout, so the agent must still be Fresh" AgentFreshness.Fresh

  testCase "WHY — freshness_one_tick_past_timeout_is_stale" <| fun () ->
    let now = DateTime(2026, 1, 1, 0, 10, 0, 0).AddTicks 1L
    let timeout = TimeSpan.FromMinutes 10.0
    let presence = { AgentName = "a"; Role = OccupantRole.Worker; SessionId = "s"; LastToolCall = DateTime(2026, 1, 1, 0, 0, 0); Intent = None; RecentFiles = []; EvalCount = 0 }
    AgentPresence.freshness now timeout presence
    |> Expect.equal "one tick past the timeout boundary, the agent must be Stale" AgentFreshness.Stale

  // ── FileOverlapAdvisory.compute ──────────────────────────────────────────

  testCase "WHY — overlap_excludes_self — the current agent's OWN presence must never generate a self-overlap advisory" <| fun () ->
    let self = { AgentName = "me"; Role = OccupantRole.Worker; SessionId = "s"; LastToolCall = DateTime.UtcNow; Intent = None; RecentFiles = ["Foo.fs"]; EvalCount = 0 }
    FileOverlapAdvisory.compute "me" ["Foo.fs"] [self]
    |> Expect.equal "the current agent's own presence must be excluded, producing no advisories at all" []

  testCase "WHY — overlap_detects_shared_files_with_other_agent" <| fun () ->
    let other = { AgentName = "other"; Role = OccupantRole.Worker; SessionId = "s"; LastToolCall = DateTime.UtcNow; Intent = None; RecentFiles = ["Foo.fs"; "Bar.fs"]; EvalCount = 0 }
    FileOverlapAdvisory.compute "me" ["Foo.fs"] [other]
    |> Expect.equal "a shared file with another agent must produce exactly one OverlappingFiles advisory naming that file"
      [ FileOverlapAdvisory.OverlappingFiles ("other", ["Foo.fs"]) ]

  testCase "WHY — overlap_empty_current_files_yields_no_advisories — nothing to overlap with means no advisories, without even inspecting others" <| fun () ->
    let other = { AgentName = "other"; Role = OccupantRole.Worker; SessionId = "s"; LastToolCall = DateTime.UtcNow; Intent = None; RecentFiles = ["Foo.fs"]; EvalCount = 0 }
    FileOverlapAdvisory.compute "me" [] [other]
    |> Expect.equal "an empty current-files set must short-circuit to no advisories" []

  testCase "WHY — overlap_no_shared_files_yields_no_advisories" <| fun () ->
    let other = { AgentName = "other"; Role = OccupantRole.Worker; SessionId = "s"; LastToolCall = DateTime.UtcNow; Intent = None; RecentFiles = ["Baz.fs"]; EvalCount = 0 }
    FileOverlapAdvisory.compute "me" ["Foo.fs"] [other]
    |> Expect.equal "disjoint file sets must produce no advisories" []

  // ── formatRelativeTime boundaries ────────────────────────────────────────

  testCase "WHY — relativeTime_under_60s_is_just_now" <| fun () ->
    let now = DateTime(2026, 1, 1, 0, 0, 59)
    formatRelativeTime now (DateTime(2026, 1, 1, 0, 0, 0))
    |> Expect.equal "59 seconds ago must be \"just now\"" "just now"

  testCase "WHY — relativeTime_at_60s_is_minutes_not_just_now — the 60-second boundary must switch buckets (`<` not `<=`)" <| fun () ->
    let now = DateTime(2026, 1, 1, 0, 1, 0)
    formatRelativeTime now (DateTime(2026, 1, 1, 0, 0, 0))
    |> Expect.equal "exactly 60 seconds ago must be \"1 min ago\", not \"just now\"" "1 min ago"

  testCase "WHY — relativeTime_under_60min_is_minutes" <| fun () ->
    let now = DateTime(2026, 1, 1, 0, 59, 0)
    formatRelativeTime now (DateTime(2026, 1, 1, 0, 0, 0))
    |> Expect.equal "59 minutes ago must be \"59 min ago\"" "59 min ago"

  testCase "WHY — relativeTime_at_60min_is_hours_not_minutes" <| fun () ->
    let now = DateTime(2026, 1, 1, 1, 0, 0)
    formatRelativeTime now (DateTime(2026, 1, 1, 0, 0, 0))
    |> Expect.equal "exactly 60 minutes ago must be \"1 hr ago\", not \"60 min ago\"" "1 hr ago"

  testCase "WHY — relativeTime_under_24hr_is_hours" <| fun () ->
    let now = DateTime(2026, 1, 1, 23, 0, 0)
    formatRelativeTime now (DateTime(2026, 1, 1, 0, 0, 0))
    |> Expect.equal "23 hours ago must be \"23 hr ago\"" "23 hr ago"

  testCase "WHY — relativeTime_at_24hr_is_days_not_hours" <| fun () ->
    let now = DateTime(2026, 1, 2, 0, 0, 0)
    formatRelativeTime now (DateTime(2026, 1, 1, 0, 0, 0))
    |> Expect.equal "exactly 24 hours ago must be \"1 days ago\", not \"24 hr ago\"" "1 days ago"

  // ── formatSessionList ────────────────────────────────────────────────────

  testCase "WHY — formatSessionList_empty_says_no_active_sessions" <| fun () ->
    formatSessionList DateTime.UtcNow None []
    |> Expect.equal "an empty session list must say exactly \"No active sessions.\"" "No active sessions."

  testCase "WHY — formatSessionList_nonEmpty_reports_exact_count" <| fun () ->
    let id = testSessionId "aa000005"
    let s = mkSession id DateTime.UtcNow SessionStatus.Ready
    formatSessionList DateTime.UtcNow None [s]
    |> Expect.stringContains "a one-session list must report count 1" "1 active session(s):"
]
