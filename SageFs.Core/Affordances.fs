module SageFs.Affordances

open System

/// Tracks eval count and timing statistics (immutable, pure updates).
type EvalStats = {
  EvalCount: int
  TotalDuration: TimeSpan
  MinDuration: TimeSpan
  MaxDuration: TimeSpan
}

module EvalStats =
  let empty = {
    EvalCount = 0
    TotalDuration = TimeSpan.Zero
    MinDuration = TimeSpan.Zero
    MaxDuration = TimeSpan.Zero
  }

  let record (duration: TimeSpan) (stats: EvalStats) =
    match stats.EvalCount = 0 with
    | true ->
      { EvalCount = 1
        TotalDuration = duration
        MinDuration = duration
        MaxDuration = duration }
    | false ->
      { EvalCount = stats.EvalCount + 1
        TotalDuration = stats.TotalDuration + duration
        MinDuration = min stats.MinDuration duration
        MaxDuration = max stats.MaxDuration duration }

  let averageDuration (stats: EvalStats) =
    match stats.EvalCount = 0 with
    | true -> TimeSpan.Zero
    | false -> TimeSpan.FromTicks(stats.TotalDuration.Ticks / int64 stats.EvalCount)

/// Pure function: given a session state, returns the list of tool names
/// that are valid to invoke. Agents should only call listed tools.
let availableTools (state: SessionState) : string list =
  match state with
  | Uninitialized ->
    // switch_session must be available here: when the ACTIVE session is
    // uninitialized/absent but OTHER sessions exist and are Ready, selecting one
    // is exactly the recovery the "N sessions exist... switch_session to select
    // one" hint points at — omitting it was a catch-22 (roast UX / affordances).
    [ "get_daemon_status"; "get_session_status"; "get_friction_report"; "get_available_projects"
      "list_sessions"; "switch_session"; "create_project_session"; "create_solution_session"; "create_bare_session"
      "acquire_full_build_lease"; "acquire_test_suite_lease"; "acquire_run_app_lease"; "release_work_lease"; "decompose_pipeline" ]
  | WarmingUp ->
    [ "get_session_status"; "get_recent_fsi_events"; "get_friction_report"
      "get_available_projects"; "list_sessions"; "switch_session"
      "create_project_session"; "create_solution_session"; "create_bare_session"
      "acquire_full_build_lease"; "acquire_test_suite_lease"; "acquire_run_app_lease"; "release_work_lease"
      "decompose_pipeline" ]
  | Ready ->
    [ "send_fsharp_code"
      "get_session_status"
      "get_recent_fsi_events"
      "get_friction_report"
      "check_fsharp_code"
      "targeted_verify"
      "list_tests"
      "explain_test_failure"
      "list_sessions"
      "switch_session"
      "create_project_session"
      "create_solution_session"
      "create_bare_session"
      "acquire_full_build_lease"; "acquire_test_suite_lease"; "acquire_run_app_lease"; "release_work_lease"
      "get_available_projects"
      "reset_fsi_session"
      "hard_reset_fsi_session"
      // Switching REPL <-> Live (hot reload) is a Ready-session action — the
      // web-package detection hint points agents here, so it must be callable.
      "switch_workflow"
      "cancel_eval"
      // Feature-analysis surface (P15–P19 + orphaned modules): all session
      // read-only, available once a session is Ready.
      "decompose_pipeline"
      "diagnose"
      "coverage_intel"
      "impact_forecast"
      "suggest_next_action"
      "plan_ripple"
      "preview_what_if"
      "suggest_next_cell"
      "get_session_filmstrip"
      "export_notebook"
      "export_session_transcript"
      "get_message_journal"
      "get_eval_timeline"
      "manage_scratch_pad"
      "get_eval_diff"
      "get_cell_dependencies"
      "discover_features"
      "suggest_repair"
      "list_runnable_projects"
      "run_app"
      "stop_app" ]
  | Evaluating ->
    [ "cancel_eval"
      "get_session_status"
      "get_recent_fsi_events"
      "get_friction_report"
      "get_available_projects"
      "acquire_full_build_lease"; "acquire_test_suite_lease"; "acquire_run_app_lease"; "release_work_lease"
      "list_sessions"
      // switch_session is always callable (navigation, not execution) — advertise
      // it so an agent whose active session is mid-eval knows it can switch away.
      "switch_session"
      "check_fsharp_code"
      "decompose_pipeline" ]
  | Faulted ->
    [ "get_session_status"
      "get_recent_fsi_events"
      "get_friction_report"
      "get_available_projects"
      "list_sessions"
      // switch_session is always callable — advertise it so an agent on a
      // faulted session knows it can switch to a healthy one.
      "switch_session"
      "create_project_session"
      "create_solution_session"
      "create_bare_session"
      "acquire_full_build_lease"; "acquire_test_suite_lease"; "acquire_run_app_lease"; "release_work_lease"
      "reset_fsi_session"
      "hard_reset_fsi_session"
      "decompose_pipeline" ]

/// Check if a tool is available in the current state.
/// Returns Ok () if available, Error with SageFsError.ToolNotAvailable.
let checkToolAvailability (state: SessionState) (toolName: string) : Result<unit, SageFsError> =
  let tools = availableTools state
  match tools |> List.contains toolName with
  | true ->
    Ok ()
  | false ->
    Error (SageFsError.ToolNotAvailable(toolName, state, tools))

// ── Tool-call gating domain ──────────────────────────────────────────────
//
// The MCP server gates every `tools/call` invocation against THIS model. A
// tool is classified either:
//   - `StateGated`     — its availability is derived from `availableTools` for
//                        the session's CURRENT lifecycle state (Ready/Eval…).
//   - `AlwaysAvailable`— it has no session-state dependence and must remain
//                        callable in every state (and before any session
//                        exists). Monitoring/session-listing tools plus the
//                        non-session surface (friction telemetry, hot-reload
//                        toggles, stop_session) belong here.
// The gate logic itself never special-cases a tool name — it consults only
// this declaration table, so a newly registered tool is either declared or it
// fails closed (and the registration-integrity tests force the declaration).

[<RequireQualifiedAccess>]
type ToolGate =
  | AlwaysAvailable
  | StateGated

/// Every MCP-registered tool must be declared here (one entry per tool name).
let private gatingDomain : Map<string, ToolGate> =
  Map.ofList [
    // Tools with no session-state dependence.
    "get_daemon_status", ToolGate.AlwaysAvailable
    "get_session_status", ToolGate.AlwaysAvailable
    "get_friction_report", ToolGate.AlwaysAvailable
    "get_available_projects", ToolGate.AlwaysAvailable
    "acquire_full_build_lease", ToolGate.AlwaysAvailable
    "acquire_test_suite_lease", ToolGate.AlwaysAvailable
    "acquire_run_app_lease", ToolGate.AlwaysAvailable
    "release_work_lease", ToolGate.AlwaysAvailable
    "list_sessions", ToolGate.AlwaysAvailable
    "get_friction_summary", ToolGate.AlwaysAvailable
    // Reads and clears SageFs's own files under its data dir. No session involved.
    "manage_local_data", ToolGate.AlwaysAvailable
    "report_friction", ToolGate.AlwaysAvailable
    "enable_hot_reload", ToolGate.AlwaysAvailable
    "disable_hot_reload", ToolGate.AlwaysAvailable
    // Lists or resets live state a hot-reload save kept. It goes straight to
    // the worker, which answers for itself when it can't (no worker, nothing
    // kept), so it doesn't need the session-state gate either.
    "reset_hot_reload_state", ToolGate.AlwaysAvailable
    // Lists or switches rule 2's reflection read mode. Same shape: it goes
    // straight to the worker, which answers for itself.
    "set_reflection_read_mode", ToolGate.AlwaysAvailable
    "stop_session", ToolGate.AlwaysAvailable
    // switch_session is navigation, not code execution: it only rebinds which
    // session the agent views and moves the daemon-global active pointer. It
    // never touches an FSI session or worker, so it cannot depend on any
    // session's execution state — and gating it on the ACTIVE session's state
    // wrongly rejected "switch away from a busy/faulted session," the exact
    // recovery it exists for (roast-9 §2). It belongs here with stop_session.
    "switch_session", ToolGate.AlwaysAvailable
    // Stateless code analysis — no session required (decomposes the passed
    // pipeline expression directly).
    "decompose_pipeline", ToolGate.AlwaysAvailable
    // Cohort tools v1 (cohort-integration-plan.md Slice 2): membership,
    // claims, and landings belong to the daemon's single implicit cohort, not
    // to any one FSI session's lifecycle — they are meaningful (and must be
    // callable) whether or not a session yet exists. Cohort-phase/authority-
    // aware gating (a member vs. the conductor) is Slice 3's `cohortTools`,
    // not this per-session-state model.
    "join_cohort", ToolGate.AlwaysAvailable
    "leave_cohort", ToolGate.AlwaysAvailable
    "acquire_claim", ToolGate.AlwaysAvailable
    "release_claim", ToolGate.AlwaysAvailable
    "reassign_claim", ToolGate.AlwaysAvailable
    "request_landing", ToolGate.AlwaysAvailable
    "get_cohort_status", ToolGate.AlwaysAvailable
    // Item 14c: configuring the integration ref/worktree is conductor-only
    // (enforced by `cohortTools` below) but, like every other cohort tool,
    // has no per-SESSION-state dependence — it is meaningful before any FSI
    // session exists.
    "set_integration_ref", ToolGate.AlwaysAvailable
    // State-gated tools — availability derives from availableTools for the
    // session's current lifecycle state.
    "send_fsharp_code", ToolGate.StateGated
    "get_recent_fsi_events", ToolGate.StateGated
    "check_fsharp_code", ToolGate.StateGated
    "targeted_verify", ToolGate.StateGated
    "list_tests", ToolGate.StateGated
    "explain_test_failure", ToolGate.StateGated
    "create_project_session", ToolGate.StateGated
    "create_solution_session", ToolGate.StateGated
    "create_bare_session", ToolGate.StateGated
    "reset_fsi_session", ToolGate.StateGated
    "hard_reset_fsi_session", ToolGate.StateGated
    "switch_workflow", ToolGate.StateGated
    "cancel_eval", ToolGate.StateGated
    // Feature-analysis surface (P15–P19 + orphaned modules): session
    // read-only, gated on a Ready session like the other analysis tools.
    "diagnose", ToolGate.StateGated
    "coverage_intel", ToolGate.StateGated
    "impact_forecast", ToolGate.StateGated
    "suggest_next_action", ToolGate.StateGated
    "plan_ripple", ToolGate.StateGated
    "preview_what_if", ToolGate.StateGated
    "suggest_next_cell", ToolGate.StateGated
    "get_session_filmstrip", ToolGate.StateGated
    "export_notebook", ToolGate.StateGated
    "export_session_transcript", ToolGate.StateGated
    "get_message_journal", ToolGate.StateGated
    "get_eval_timeline", ToolGate.StateGated
    "manage_scratch_pad", ToolGate.StateGated
    "get_eval_diff", ToolGate.StateGated
    "get_cell_dependencies", ToolGate.StateGated
    "discover_features", ToolGate.StateGated
    "suggest_repair", ToolGate.StateGated
    // Running the session's executable project needs a Ready worker.
    "list_runnable_projects", ToolGate.StateGated
    "run_app", ToolGate.StateGated
    "stop_app", ToolGate.StateGated
  ]

/// Look up a tool's gating classification. `None` means the tool is not
/// declared — the call fails closed whenever a session state is known.
let toolGate (toolName: string) : ToolGate option =
  Map.tryFind toolName gatingDomain

/// All declared tool names (must equal the registered MCP tool set).
let declaredGateTools : string list =
  gatingDomain |> Map.toList |> List.map fst

/// Decide whether a `tools/call` for `toolName` may proceed when the targeted
/// session is in `state`. Pure — no I/O, no session resolution.
let checkToolCallAllowed (state: SessionState) (toolName: string) : Result<unit, SageFsError> =
  match toolGate toolName with
  | Some ToolGate.AlwaysAvailable ->
    Ok ()
  | Some ToolGate.StateGated ->
    checkToolAvailability state toolName
  | None ->
    // Fail closed: a tool that is not declared in the gating domain has no
    // availability policy, so it must never bypass the model.
    Error (SageFsError.ToolNotAvailable(toolName, state, availableTools state))

// ── Cohort affordances (Phase 1 item 11, §8.1; cohort-integration-plan.md
//    Slice 3) ─────────────────────────────────────────────────────────────
//
// The vision's full affordance function is `SessionState * Authority *
// CohortPhase -> Set<ToolName>` (§8.1). This is a SCOPED v1: `CohortPhase` is
// DEFERRED — v1's implicit per-daemon cohort (Slice 2, `Cohort.CohortState`)
// has no phase field, so there is no Forming/Active/Landing/Closed lifecycle
// to match on yet, and `Cohort.decide` already enforces authority at the
// core (`NotConductor`/`NotClaimHolder`). So `cohortTools` here is keyed on
// `Authority` alone, and its job is narrower than the vision's: filter the
// cohort MCP TOOL LIST by the caller's role, as an earlier, friendlier
// signal than the core's own refusal — not a full state/phase matrix.
//
// Repo doctrine ("no magic strings anywhere"): the closed set of cohort MCP
// tool names becomes a DU with one exhaustive to-string function, same as
// `ToolGate` above.

[<RequireQualifiedAccess>]
type CohortTool =
  | Join
  | Leave
  | AcquireClaim
  | ReleaseClaim
  | ReassignClaim
  | RequestLanding
  | GetStatus
  /// Item 14c: configure the cohort's integration ref/worktree/branch.
  /// Conductor-only (`cohortTools` below) — the same treatment as
  /// `ReassignClaim`.
  | SetIntegrationRef

module CohortTool =
  /// The exact MCP tool names the 8 cohort tools are registered under
  /// (`Affordances.fs`'s own `gatingDomain` "Cohort tools v1" entries above —
  /// all `AlwaysAvailable` there at the per-SESSION-state layer; this module
  /// is the authority-aware refinement layered on top for Slice 3/item 14c).
  let toToolName =
    function
    | CohortTool.Join -> "join_cohort"
    | CohortTool.Leave -> "leave_cohort"
    | CohortTool.AcquireClaim -> "acquire_claim"
    | CohortTool.ReleaseClaim -> "release_claim"
    | CohortTool.ReassignClaim -> "reassign_claim"
    | CohortTool.RequestLanding -> "request_landing"
    | CohortTool.GetStatus -> "get_cohort_status"
    | CohortTool.SetIntegrationRef -> "set_integration_ref"

  let all: CohortTool list =
    [ CohortTool.Join
      CohortTool.Leave
      CohortTool.AcquireClaim
      CohortTool.ReleaseClaim
      CohortTool.ReassignClaim
      CohortTool.RequestLanding
      CohortTool.GetStatus
      CohortTool.SetIntegrationRef ]

/// MCP tool names SageFs has RETIRED. Nothing may call these, and — the reason
/// this type exists at all — no LIVE agent-facing string may tell an agent to.
/// A retired name surviving in a daemon response is worse than a stale doc: the
/// product itself sends the agent to a tool that no longer exists. The
/// Lemmings roast proved it: every successful run was told to poll
/// `get_fsi_status`, which was no longer registered.
///
/// Modelled as a DU with one exhaustive to-string function so the forbidden set
/// is DATA — a future rename cannot quietly satisfy a test that only asserts a
/// literal is absent, because the literal lives here and is checked against
/// the real registered catalog. Historical prose (CHANGELOG, trial reports,
/// archived internal notes) may still name these; that is history, not
/// guidance.
[<RequireQualifiedAccess>]
type RetiredTool =
  | CreateSession
  | GetFsiStatus
  | GetStartupInfo
  | LoadFsharpScript

module RetiredTool =
  /// The exact `tools/list` name each tool was registered under, before it
  /// was retired.
  let toToolName =
    function
    | RetiredTool.CreateSession -> "create_session"
    | RetiredTool.GetFsiStatus -> "get_fsi_status"
    | RetiredTool.GetStartupInfo -> "get_startup_info"
    | RetiredTool.LoadFsharpScript -> "load_fsharp_script"

  let all: RetiredTool list =
    [ RetiredTool.CreateSession
      RetiredTool.GetFsiStatus
      RetiredTool.GetStartupInfo
      RetiredTool.LoadFsharpScript ]

  let toolNames: string list = all |> List.map toToolName

  /// The live name that replaced a retired one, where a direct replacement
  /// exists. A retired tool with no single successor is absent on purpose, so
  /// a caller cannot invent a mapping the product never defined.
  let replacement =
    function
    | RetiredTool.GetFsiStatus -> Some "get_session_status"
    | RetiredTool.GetStartupInfo -> Some "get_daemon_status"
    | RetiredTool.LoadFsharpScript -> None
    | RetiredTool.CreateSession -> None

/// Total over `Cohort.Authority<'m>` (property 9, cohort-integration-plan.md
/// Slice 3) — the compiler checks the `match` is exhaustive, so no
/// registration-integrity test is needed for this one (unlike the
/// string-keyed `gatingDomain` above). The v1 role table (CohortPhase
/// collapsed, since v1 has none — see the module-doc above):
///
///   Anonymous                    -> status only
///   Member(_, Observer)          -> status only
///   Member(_, Verifier)          -> status only
///   Member(_, Implementer)       -> status, join, leave, claim/release, request_landing
///   Conductor _                  -> every tool, including reassign_claim
///                                    and set_integration_ref (item 14c)
///
/// `join_cohort` is deliberately NOT threaded into the `Anonymous` arm here
/// — see `alwaysReachableCohortTools`.
let cohortTools (authority: Cohort.Authority<'m>) : Set<CohortTool> =
  match authority with
  | Cohort.Authority.Anonymous ->
    set [ CohortTool.GetStatus ]
  | Cohort.Authority.Member(_, Cohort.JoinableRole.Observer) ->
    set [ CohortTool.GetStatus ]
  | Cohort.Authority.Member(_, Cohort.JoinableRole.Verifier) ->
    set [ CohortTool.GetStatus ]
  | Cohort.Authority.Member(_, Cohort.JoinableRole.Implementer) ->
    set [ CohortTool.GetStatus
          CohortTool.Join
          CohortTool.Leave
          CohortTool.AcquireClaim
          CohortTool.ReleaseClaim
          CohortTool.RequestLanding ]
  | Cohort.Authority.Conductor _ ->
    Set.ofList CohortTool.all

/// Tools that must stay reachable to EVERY caller regardless of authority —
/// most importantly a not-yet-a-member `Anonymous` caller, who could
/// otherwise never reach `join_cohort` at all (nobody could ever join a
/// cohort). Mirrors the `gatingDomain` table above, which already makes
/// `join_cohort`/`get_cohort_status` `AlwaysAvailable` regardless of SESSION
/// state; this is the same treatment at the authority layer. The invariant
/// this preserves: a fresh caller can always `join_cohort` and always
/// `get_cohort_status`.
let alwaysReachableCohortTools: Set<CohortTool> =
  set [ CohortTool.Join; CohortTool.GetStatus ]

/// Resolve a caller's `Authority` from a published `CohortFrame` — the
/// frame-based mirror of `Cohort.Authority.present` (which reads
/// `CohortState` directly; the MCP gate only ever holds the owner's
/// published `CohortFrame`, D4, never the state itself). `Conductor` is read
/// from the frame's own `Conductor` field first; otherwise the caller is
/// looked up by identity in the frame's parallel `MemberIds`/`MemberRole`/
/// `MemberSeat` arrays — a `Departed` seat resolves to `Anonymous`, the same
/// as `Authority.present`'s `MemberPresence.Present` guard — and a caller
/// found in neither is `Anonymous`, never an error (total, like
/// `Authority.present`).
let authorityOfMember (who: 'm) (frame: Cohort.CohortFrame<'m>) : Cohort.Authority<'m> =
  match frame.Conductor with
  | Some c when c = who ->
    Cohort.Authority.Conductor who
  | _ ->
    match frame.MemberIds |> Array.tryFindIndex ((=) who) with
    | Some i ->
      match frame.MemberSeat.[i] with
      | Cohort.SeatState.Present -> Cohort.Authority.Member(who, frame.MemberRole.[i])
      | Cohort.SeatState.Departed _ -> Cohort.Authority.Anonymous
    | None ->
      Cohort.Authority.Anonymous

/// Whether `tool` may be invoked by `authority`: `cohortTools authority`
/// widened by the always-reachable set (join/status — see
/// `alwaysReachableCohortTools`). Pure; the MCP gate (`Mcp.fs`) resolves
/// `authority` via `authorityOfMember` and calls this before a cohort tool
/// body runs.
let checkCohortToolAllowed (authority: Cohort.Authority<'m>) (tool: CohortTool) : bool =
  Set.contains tool alwaysReachableCohortTools
  || Set.contains tool (cohortTools authority)
