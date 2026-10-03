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
    //
    // hard_reset_fsi_session belongs here for the same reason, and was the same
    // bug: a session that never finished warmup is Uninitialized, and without
    // it there is no way back — every other tool is refused, and the state
    // tells you to use a tool the gate will not let anyone call. Recovering
    // FROM a state must never require leaving that state first.
    //
    // It is NOT paid for by dropping a diagnostic: get_friction_report is
    // declared AlwaysAvailable, and a state must not hide a tool it is
    // contractually required to offer. So Uninitialized grows by one and the
    // peer-size contract moves with it — the two states are still peers, and
    // the difference between them is now "one can reset itself and the other
    // is already starting up", which is the honest difference.
    [ "get_daemon_status"; "get_session_status"; "get_friction_report"; "get_available_projects"
      "list_sessions"; "switch_session"; "create_project_session"; "create_solution_session"; "create_bare_session"
      "hard_reset_fsi_session"
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
      "run_tests"
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
    // What agents left behind on the machine (worktrees, gate checkouts, caches, temp dirs) and the plan to tidy it.
    // About the repository and the disk, not about any session, so callable before one exists.
    "get_workspace_hygiene", ToolGate.AlwaysAvailable
    "tidy_workspace", ToolGate.AlwaysAvailable
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
    // Per-run member tokens (Capability.fs): minting and revoking are conductor-only
    // (`cohortTools` below) and, like every cohort tool, have no per-SESSION-state
    // dependence.
    "mint_member", ToolGate.AlwaysAvailable
    "revoke_member", ToolGate.AlwaysAvailable
    // State-gated tools — availability derives from availableTools for the
    // session's current lifecycle state.
    "send_fsharp_code", ToolGate.StateGated
    "get_recent_fsi_events", ToolGate.StateGated
    "check_fsharp_code", ToolGate.StateGated
    "targeted_verify", ToolGate.StateGated
    "list_tests", ToolGate.StateGated
    "run_tests", ToolGate.StateGated
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
  /// Mint a per-run member token (Capability.fs). Conductor-only.
  | MintMember
  /// Revoke a member token. Conductor-only.
  | RevokeMember

module CohortTool =
  /// The exact MCP tool names the 10 cohort tools are registered under
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
    | CohortTool.MintMember -> "mint_member"
    | CohortTool.RevokeMember -> "revoke_member"

  let all: CohortTool list =
    [ CohortTool.Join
      CohortTool.Leave
      CohortTool.AcquireClaim
      CohortTool.ReleaseClaim
      CohortTool.ReassignClaim
      CohortTool.RequestLanding
      CohortTool.GetStatus
      CohortTool.SetIntegrationRef
      CohortTool.MintMember
      CohortTool.RevokeMember ]

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
  // Code intelligence and coverage lookups that were never `[<McpServerTool>]`, or that left the
  // MCP surface in the "reduce agent tool surface" change. The editors reach the same data over
  // the daemon's HTTP API (completions, type explorer, coverage), so a client that wants them
  // calls that, and an agent is not told to look for them in `tools/list`.
  | ExploreNamespace
  | ExploreType
  | GetCompletions
  | GetFileCoverage
  | QueryTestCoverage
  | VisualizeDomainModel

module RetiredTool =
  /// The exact `tools/list` name each tool was registered under, before it
  /// was retired.
  let toToolName =
    function
    | RetiredTool.CreateSession -> "create_session"
    | RetiredTool.GetFsiStatus -> "get_fsi_status"
    | RetiredTool.GetStartupInfo -> "get_startup_info"
    | RetiredTool.LoadFsharpScript -> "load_fsharp_script"
    | RetiredTool.ExploreNamespace -> "explore_namespace"
    | RetiredTool.ExploreType -> "explore_type"
    | RetiredTool.GetCompletions -> "get_completions"
    | RetiredTool.GetFileCoverage -> "get_file_coverage"
    | RetiredTool.QueryTestCoverage -> "query_test_coverage"
    | RetiredTool.VisualizeDomainModel -> "visualize_domain_model"

  let all: RetiredTool list =
    [ RetiredTool.CreateSession
      RetiredTool.GetFsiStatus
      RetiredTool.GetStartupInfo
      RetiredTool.LoadFsharpScript
      RetiredTool.ExploreNamespace
      RetiredTool.ExploreType
      RetiredTool.GetCompletions
      RetiredTool.GetFileCoverage
      RetiredTool.QueryTestCoverage
      RetiredTool.VisualizeDomainModel ]

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
    | RetiredTool.ExploreNamespace -> None
    | RetiredTool.ExploreType -> None
    | RetiredTool.GetCompletions -> None
    | RetiredTool.GetFileCoverage -> None
    | RetiredTool.QueryTestCoverage -> None
    | RetiredTool.VisualizeDomainModel -> None

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
/// published `CohortFrame`, D4, never the state itself).
///
/// PRESENCE FIRST, conductor second — the same ordering discipline as
/// `Cohort.Authority.present`, and for the same reason. This used to read
/// `frame.Conductor` before it ever looked at the seat, so a caller whose seat
/// had gone `Departed` (they left, or their lease lapsed) still resolved to
/// `Conductor` and every conductor-only tool stayed listed and allowed for them
/// while their connection lived: the frame-side twin of the core's fail-open.
/// Now the seat is consulted first, and only a `Bound` binding naming a
/// `Present` seat grants `Conductor` — which the frame's own
/// `ConductorBinding` invariant already guarantees, so the lookup stays total
/// and needs no second source of truth. A `Vacant` seat (including to its
/// FORMER holder, who may have rejoined) resolves to an ordinary `Member`, and a
/// caller found in neither is `Anonymous`, never an error (total, like
/// `Authority.present`).
let authorityOfMember (who: 'm) (frame: Cohort.CohortFrame<'m>) : Cohort.Authority<'m> =
  match frame.MemberIds |> Array.tryFindIndex ((=) who) with
  | Some i ->
    match frame.MemberSeat.[i] with
    | Cohort.SeatState.Present ->
      match frame.Conductor with
      | Cohort.ConductorBinding.Bound c when c = who -> Cohort.Authority.Conductor who
      // default policy: a Present seat that is not (or no longer) the
      // conductor's is an ordinary member — `NeverBound`, `Vacant`, or bound to
      // somebody else. One arm, whatever `ConductorBinding` ever grows to.
      | Cohort.ConductorBinding.NeverBound
      | Cohort.ConductorBinding.Bound _
      | Cohort.ConductorBinding.Vacant _ -> Cohort.Authority.Member(who, frame.MemberRole.[i])
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

// ── Authority over the WHOLE tool surface, not the cohort verbs ───────────
//
// WHY THIS SECTION EXISTS. `cohortTools` above is keyed on the ten COHORT
// verbs. Every other tool name fell through it (`checkCohortAuthorityGate`
// returned `None`) and landed on the session-state gate, which knows nothing
// about roles. So a member whose role is `Observer` — a name that reads like
// a boundary — could call `send_fsharp_code`, `hard_reset_fsi_session` and
// `run_app` in perfect good health. A role name that does not mean anything is
// worse than no role at all, because it is read as a guarantee.
//
// A second layer already existed and was too narrow to help: `Capability`'s
// TOOL-CLASS gate runs only for a call that PRESENTED A TOKEN, and a token is
// optional under the default `IdentityPolicy.ConnectionsAllowed`. The cohort
// `Authority` of a connection is always resolvable — from the published
// `CohortFrame`, with no token — so THAT is the dimension to gate the whole
// surface on. This section does exactly that; `Mcp.fs` composes the two by
// INTERSECTION (a call needs the session-state gate AND this one), never union.
//
// ── WHAT A ROLE IS NOT. Read this before trusting the table below ─────────
//
// `send_fsharp_code` runs ARBITRARY F# IN THE FSI SESSION, as the same OS user
// as the daemon. Code run that way can read the daemon's data directory, call
// the daemon's own loopback with any session id, and read `/proc`. `run_tests`
// is the same hole by a different door: it RUNS USER CODE (the project's test
// binary). So:
//
//   ANY allow-list that includes `send_fsharp_code` or `run_tests` is ADVISORY
//   AGAINST A HOSTILE AGENT, NOT A BOUNDARY.
//
// A role is a well-mannered-agent convention plus a refusal a careless agent
// will hit. It is not a sandbox. Containment is a process/credential boundary
// — a different OS user, a container, a VM. `Capability.fs`'s own module doc
// already says this ("Nehemiah's sandbox is the containment"); this is the
// same sentence at the cohort layer.
//
// THE DIRECTION OF THE LIST, therefore, is the whole safety property: a
// narrow grant is a POSITIVE list of tools that were individually classified
// as read-only. It is NEVER "everything except eval". A deny-list is one
// forgotten tool away from being a union, and `run_app`/`hard_reset_fsi_session`
// were exactly the kind of tool that gets forgotten.

// ── ToolName: the closed set of registered MCP tool names ────────────────
//
// Repo doctrine: a closed string set is a DU with ONE exhaustive to-string, so
// a new tool forces a decision at compile time instead of drifting. Two string
// tables already existed and neither was the answer:
//
//   * `gatingDomain` above — the per-SESSION-STATE classification, string-keyed,
//     kept honest against `[<McpServerTool>]` reflection by McpToolGateTests;
//   * `Capability.ToolClass.toolsOf` — the per-TOKEN-CLASS classification,
//     held to `gatingDomain` by CapabilityWireTests.
//
// `ToolName` is the KEY both of those are looked up by, so they are now one
// table plus its classification rather than two parallel name lists. It is the
// parameter of the authority gate, so a grant can only ever be written as
// `Set<ToolName>` — there is no way to spell an allow-list in strings.
//
// The three cannot disagree: `McpToolGateTests` already proves
// `ToolName.all` ≡ `[<McpServerTool>]` ≡ `gatingDomain` ≡ `ToolClass.toolsOf`,
// and F# proves `toToolName` total over the DU.
[<RequireQualifiedAccess>]
type ToolName =
  // Monitoring and machine facts — no session involved.
  | GetDaemonStatus
  | GetSessionStatus
  | ListSessions
  | SwitchSession
  | GetAvailableProjects
  | ListRunnableProjects
  | GetFrictionReport
  | GetFrictionSummary
  | GetRecentFsiEvents
  // Telling SageFs what was confusing. Writes only the local friction store.
  | ReportFriction
  // Session lifecycle: create, reset, switch workflow, stop.
  | CreateProjectSession
  | CreateSolutionSession
  | CreateBareSession
  | ResetFsiSession
  | HardResetFsiSession
  | SwitchWorkflow
  | StopSession
  | CancelEval
  // Evaluation. `send_fsharp_code` is arbitrary code as the daemon's OS user.
  | SendFsharpCode
  | ManageScratchPad
  // Static analysis — the code is read, never run.
  | CheckFsharpCode
  | DecomposePipeline
  | Diagnose
  | CoverageIntel
  | ImpactForecast
  | SuggestNextAction
  | PlanRipple
  | PreviewWhatIf
  | SuggestNextCell
  | GetCellDependencies
  | DiscoverFeatures
  | GetSessionFilmstrip
  | GetEvalTimeline
  | GetEvalDiff
  | GetMessageJournal
  | ExportNotebook
  | ExportSessionTranscript
  | ExplainTestFailure
  | SuggestRepair
  // Tests. `run_tests` RUNS USER CODE, so it is its own hazard, not a read.
  | ListTests
  | RunTests
  | TargetedVerify
  // Running and stopping the session's application.
  | RunApp
  | StopApp
  // Hot reload's own switches.
  | EnableHotReload
  | DisableHotReload
  | ResetHotReloadState
  | SetReflectionReadMode
  // Leases.
  | AcquireFullBuildLease
  | AcquireTestSuiteLease
  | AcquireRunAppLease
  | ReleaseWorkLease
  // Cohort verbs — the ten `CohortTool`s, spelled the same way here so the two
  // sets are comparable (`cohortToolByName` below holds them together).
  | JoinCohort
  | LeaveCohort
  | AcquireClaim
  | ReleaseClaim
  | ReassignClaim
  | RequestLanding
  | GetCohortStatus
  | SetIntegrationRef
  | MintMember
  | RevokeMember
  // SageFs's own files and the machine's leftover worktrees.
  | ManageLocalData
  | GetWorkspaceHygiene
  | TidyWorkspace

module ToolName =
  /// The one exhaustive to-string. Adding a case without adding an arm here is
  /// a compile error, which is the point of the DU.
  let toToolName =
    function
    | ToolName.GetDaemonStatus -> "get_daemon_status"
    | ToolName.GetSessionStatus -> "get_session_status"
    | ToolName.ListSessions -> "list_sessions"
    | ToolName.SwitchSession -> "switch_session"
    | ToolName.GetAvailableProjects -> "get_available_projects"
    | ToolName.ListRunnableProjects -> "list_runnable_projects"
    | ToolName.GetFrictionReport -> "get_friction_report"
    | ToolName.GetFrictionSummary -> "get_friction_summary"
    | ToolName.GetRecentFsiEvents -> "get_recent_fsi_events"
    | ToolName.ReportFriction -> "report_friction"
    | ToolName.CreateProjectSession -> "create_project_session"
    | ToolName.CreateSolutionSession -> "create_solution_session"
    | ToolName.CreateBareSession -> "create_bare_session"
    | ToolName.ResetFsiSession -> "reset_fsi_session"
    | ToolName.HardResetFsiSession -> "hard_reset_fsi_session"
    | ToolName.SwitchWorkflow -> "switch_workflow"
    | ToolName.StopSession -> "stop_session"
    | ToolName.CancelEval -> "cancel_eval"
    | ToolName.SendFsharpCode -> "send_fsharp_code"
    | ToolName.ManageScratchPad -> "manage_scratch_pad"
    | ToolName.CheckFsharpCode -> "check_fsharp_code"
    | ToolName.DecomposePipeline -> "decompose_pipeline"
    | ToolName.Diagnose -> "diagnose"
    | ToolName.CoverageIntel -> "coverage_intel"
    | ToolName.ImpactForecast -> "impact_forecast"
    | ToolName.SuggestNextAction -> "suggest_next_action"
    | ToolName.PlanRipple -> "plan_ripple"
    | ToolName.PreviewWhatIf -> "preview_what_if"
    | ToolName.SuggestNextCell -> "suggest_next_cell"
    | ToolName.GetCellDependencies -> "get_cell_dependencies"
    | ToolName.DiscoverFeatures -> "discover_features"
    | ToolName.GetSessionFilmstrip -> "get_session_filmstrip"
    | ToolName.GetEvalTimeline -> "get_eval_timeline"
    | ToolName.GetEvalDiff -> "get_eval_diff"
    | ToolName.GetMessageJournal -> "get_message_journal"
    | ToolName.ExportNotebook -> "export_notebook"
    | ToolName.ExportSessionTranscript -> "export_session_transcript"
    | ToolName.ExplainTestFailure -> "explain_test_failure"
    | ToolName.SuggestRepair -> "suggest_repair"
    | ToolName.ListTests -> "list_tests"
    | ToolName.RunTests -> "run_tests"
    | ToolName.TargetedVerify -> "targeted_verify"
    | ToolName.RunApp -> "run_app"
    | ToolName.StopApp -> "stop_app"
    | ToolName.EnableHotReload -> "enable_hot_reload"
    | ToolName.DisableHotReload -> "disable_hot_reload"
    | ToolName.ResetHotReloadState -> "reset_hot_reload_state"
    | ToolName.SetReflectionReadMode -> "set_reflection_read_mode"
    | ToolName.AcquireFullBuildLease -> "acquire_full_build_lease"
    | ToolName.AcquireTestSuiteLease -> "acquire_test_suite_lease"
    | ToolName.AcquireRunAppLease -> "acquire_run_app_lease"
    | ToolName.ReleaseWorkLease -> "release_work_lease"
    | ToolName.JoinCohort -> "join_cohort"
    | ToolName.LeaveCohort -> "leave_cohort"
    | ToolName.AcquireClaim -> "acquire_claim"
    | ToolName.ReleaseClaim -> "release_claim"
    | ToolName.ReassignClaim -> "reassign_claim"
    | ToolName.RequestLanding -> "request_landing"
    | ToolName.GetCohortStatus -> "get_cohort_status"
    | ToolName.SetIntegrationRef -> "set_integration_ref"
    | ToolName.MintMember -> "mint_member"
    | ToolName.RevokeMember -> "revoke_member"
    | ToolName.ManageLocalData -> "manage_local_data"
    | ToolName.GetWorkspaceHygiene -> "get_workspace_hygiene"
    | ToolName.TidyWorkspace -> "tidy_workspace"

  let all : ToolName list =
    [ ToolName.GetDaemonStatus
      ToolName.GetSessionStatus
      ToolName.ListSessions
      ToolName.SwitchSession
      ToolName.GetAvailableProjects
      ToolName.ListRunnableProjects
      ToolName.GetFrictionReport
      ToolName.GetFrictionSummary
      ToolName.GetRecentFsiEvents
      ToolName.ReportFriction
      ToolName.CreateProjectSession
      ToolName.CreateSolutionSession
      ToolName.CreateBareSession
      ToolName.ResetFsiSession
      ToolName.HardResetFsiSession
      ToolName.SwitchWorkflow
      ToolName.StopSession
      ToolName.CancelEval
      ToolName.SendFsharpCode
      ToolName.ManageScratchPad
      ToolName.CheckFsharpCode
      ToolName.DecomposePipeline
      ToolName.Diagnose
      ToolName.CoverageIntel
      ToolName.ImpactForecast
      ToolName.SuggestNextAction
      ToolName.PlanRipple
      ToolName.PreviewWhatIf
      ToolName.SuggestNextCell
      ToolName.GetCellDependencies
      ToolName.DiscoverFeatures
      ToolName.GetSessionFilmstrip
      ToolName.GetEvalTimeline
      ToolName.GetEvalDiff
      ToolName.GetMessageJournal
      ToolName.ExportNotebook
      ToolName.ExportSessionTranscript
      ToolName.ExplainTestFailure
      ToolName.SuggestRepair
      ToolName.ListTests
      ToolName.RunTests
      ToolName.TargetedVerify
      ToolName.RunApp
      ToolName.StopApp
      ToolName.EnableHotReload
      ToolName.DisableHotReload
      ToolName.ResetHotReloadState
      ToolName.SetReflectionReadMode
      ToolName.AcquireFullBuildLease
      ToolName.AcquireTestSuiteLease
      ToolName.AcquireRunAppLease
      ToolName.ReleaseWorkLease
      ToolName.JoinCohort
      ToolName.LeaveCohort
      ToolName.AcquireClaim
      ToolName.ReleaseClaim
      ToolName.ReassignClaim
      ToolName.RequestLanding
      ToolName.GetCohortStatus
      ToolName.SetIntegrationRef
      ToolName.MintMember
      ToolName.RevokeMember
      ToolName.ManageLocalData
      ToolName.GetWorkspaceHygiene
      ToolName.TidyWorkspace ]

  let private byName : Map<string, ToolName> = all |> List.map (fun t -> toToolName t, t) |> Map.ofList

  /// The DU case for a registered MCP tool name. `None` for a name nobody
  /// registered — and that is what `checkToolCallAllowed` refuses closed on,
  /// so an undeclared tool can never reach the authority gate as `Some`.
  let tryParse (toolName: string) : ToolName option = Map.tryFind toolName byName

  /// The registered tool name for a case. Total, by the compiler.
  let toString (tool: ToolName) : string = toToolName tool

  let allToolNames : string list = all |> List.map toToolName

  /// The ten cohort verbs as `ToolName`s, so `cohortTools` and `authorityTools`
  /// are comparable and the old cohort gate is provably a SUBSET of the new
  /// one (there is a test for exactly that, and the compiler keeps
  /// `toCohortTool` total — a new cohort verb forces a case here).
  let cohortTools : ToolName list =
    [ ToolName.JoinCohort
      ToolName.LeaveCohort
      ToolName.AcquireClaim
      ToolName.ReleaseClaim
      ToolName.ReassignClaim
      ToolName.RequestLanding
      ToolName.GetCohortStatus
      ToolName.SetIntegrationRef
      ToolName.MintMember
      ToolName.RevokeMember ]

  /// The cohort verb `tool` names, or `None` for the other tools — which is
  /// how a caller knows a non-cohort tool must not be looked up in
  /// `CohortTool`. There is a wildcard arm because `ToolName` covers all 65
  /// registered tools and only 10 of them ARE cohort verbs, but the ten
  /// mapping arms above are explicit, so a RENAMED cohort verb cannot quietly
  /// fall through the wildcard into `GetStatus`.
  let tryCohortTool (tool: ToolName) : CohortTool option =
    match tool with
    | ToolName.JoinCohort -> Some CohortTool.Join
    | ToolName.LeaveCohort -> Some CohortTool.Leave
    | ToolName.AcquireClaim -> Some CohortTool.AcquireClaim
    | ToolName.ReleaseClaim -> Some CohortTool.ReleaseClaim
    | ToolName.ReassignClaim -> Some CohortTool.ReassignClaim
    | ToolName.RequestLanding -> Some CohortTool.RequestLanding
    | ToolName.GetCohortStatus -> Some CohortTool.GetStatus
    | ToolName.SetIntegrationRef -> Some CohortTool.SetIntegrationRef
    | ToolName.MintMember -> Some CohortTool.MintMember
    | ToolName.RevokeMember -> Some CohortTool.RevokeMember
    | _ -> None

  /// Whether `tool` is one of the ten cohort verbs. Derived from the mapping
  /// above rather than from the name list, so the two cannot disagree.
  let isCohortTool (tool: ToolName) : bool = Option.isSome (tryCohortTool tool)

// ── ToolRole: what this project means by `Observer` ──────────────────────
//
// THE SEMANTIC DECISION, stated once so every test can quote it.
//
//   Observer  = MAY NOT EVALUATE, and may not do anything that runs, builds,
//               writes or resets. Its whole surface is a positive list of
//               tools that READ: cohort status, daemon/session/session-list
//               status, code and history ANALYSIS, and the friction
//               report/summary. `list_tests` — which READS the discovered
//               test list — is in it. `report_friction` is in it, because it
//               writes only the local telemetry store and refusing it would
//               make an Observer unable to tell SageFs what confused it,
//               which is the one thing a read-only witness is for.
//
//   Verifier  = Observer, PLUS reading, building and RUNNING TESTS, and taking
//               a build/test/run-app lease. It still does not evaluate: a
//               Verifier reads the code and runs the project's own tests, and
//               never injects its own F#. `run_tests` is the deliberate sharp
//               edge here and it is documented on the case below.
//
//   Implementer / the conductor = the working member. Everything a member may
//               do, including `send_fsharp_code`.
//
// WHY OBSERVER IS NOT "EVERYTHING EXCEPT EVAL": because the tools that are
// not "eval" are exactly where the rest of the danger lives. `run_tests` runs
// user code. `run_app` starts the project's executable. `hard_reset_fsi_session`
// respawns a worker process. `tidy_workspace` DELETES worktrees from disk. An
// Observer refused `send_fsharp_code` but granted all of those would be more
// dangerous than one granted nothing. So the list is spelled out positively,
// tool by tool, and the comment on every block says why each entry earned it.
//
// WHY VERIFIER ≠ OBSERVER ANYMORE (it used to): they were the same table
// (`[GetStatus]`) differing only in name, which is precisely the defect this
// change exists to remove — a role that distinguishes nothing. `Capability.fs`
// already had the honest ladder (Observer < Analysis < Verifier < Implementer)
// over tool classes; this brings the cohort's own `JoinableRole` in line with
// it instead of leaving a second, emptier notion of the same word.
//
// WHAT THIS IS NOT: see the section header. A role is not a sandbox. An
// Observer cannot eval through `send_fsharp_code`, but a Verifier can still
// execute the project's own test binary, and nothing here stops a process that
// already has the daemon's OS credentials.
[<RequireQualifiedAccess>]
type ToolRole =
  | Observer
  | Verifier
  | Working
  /// The conductor. It is NOT `Working`: `Working` deliberately excludes the
  /// two conductor-only families (`CohortAdmin`, `Maintenance`), because an
  /// ordinary Implementer must not be able to reassign a claim, configure the
  /// integration, mint a token, or delete worktrees off the disk. Folding the
  /// conductor into `Working` would hand those to every implementer, which is
  /// the exact authority hole this table exists to close — so it is a separate
  /// case, and it is what makes the ladder monotone.
  | Conductor

module ToolRole =
  let all : ToolRole list = [ ToolRole.Observer; ToolRole.Verifier; ToolRole.Working; ToolRole.Conductor ]

  let toToken =
    function
    | ToolRole.Observer -> "Observer"
    | ToolRole.Verifier -> "Verifier"
    | ToolRole.Working -> "Working"
    | ToolRole.Conductor -> "Conductor"

  // ── the positive lists ──────────────────────────────────────────────────

  /// Reads the cohort, reads machine and session status, reads the friction
  /// store, and says what was confusing. Every one of these is a read or a
  /// write to the local telemetry store — none of them runs, builds, resets
  /// or deletes.
  let observerTools : Set<ToolName> =
    set
      [ // cohort: status only, plus join so a fresh caller is never stuck
        ToolName.GetCohortStatus
        ToolName.JoinCohort
        // machine and session status
        ToolName.GetDaemonStatus
        ToolName.GetSessionStatus
        ToolName.ListSessions
        ToolName.SwitchSession
        ToolName.GetAvailableProjects
        ToolName.ListRunnableProjects
        ToolName.GetFrictionReport
        ToolName.GetFrictionSummary
        ToolName.GetRecentFsiEvents
        ToolName.DiscoverFeatures
        ToolName.ReportFriction
        // code and history ANALYSIS — the code is read, never run
        ToolName.CheckFsharpCode
        ToolName.DecomposePipeline
        ToolName.Diagnose
        ToolName.CoverageIntel
        ToolName.ImpactForecast
        ToolName.SuggestNextAction
        ToolName.PlanRipple
        ToolName.PreviewWhatIf
        ToolName.SuggestNextCell
        ToolName.GetCellDependencies
        ToolName.GetSessionFilmstrip
        ToolName.GetEvalTimeline
        ToolName.GetEvalDiff
        ToolName.GetMessageJournal
        ToolName.ExportNotebook
        ToolName.ExportSessionTranscript
        ToolName.ExplainTestFailure
        ToolName.SuggestRepair
        // DISCOVERING tests is a read. See `verifierOnlyTools` for the sharp
        // edge this draws against: `list_tests` lists, `run_tests` executes.
        ToolName.ListTests ]

  /// What a Verifier adds. Only three entries, and each one is here on
  /// purpose:
  ///
  ///  * `run_tests` / `targeted_verify` — a Verifier's entire reason to exist.
  ///    This EXECUTES the project's test binary, which is user code running as
  ///    the daemon's OS user — the same hazard as `send_fsharp_code`, reached
  ///    by a different door. `Capability.RolePreset` says exactly this
  ///    ("`TestRun`: Run the project's tests. That runs user code.") and
  ///    `verifierOnlyTools` is pinned by a test so this cannot widen quietly.
  ///    A grant containing it is advisory, never a boundary.
  ///  * the build/test/run-app leases — taking one only RESERVES capacity;
  ///    it runs nothing. Without them a Verifier would starve a session of
  ///    the build budget its own tests need.
  let verifierOnlyTools : Set<ToolName> =
    set [ ToolName.RunTests
          ToolName.TargetedVerify
          ToolName.AcquireFullBuildLease
          ToolName.AcquireTestSuiteLease
          ToolName.AcquireRunAppLease
          ToolName.ReleaseWorkLease ]

  let verifierTools : Set<ToolName> = Set.union observerTools verifierOnlyTools

  /// The tools no grant but the conductor's own may call, across all roles.
  ///
  /// `GetWorkspaceHygiene` is deliberately NOT here even though it used to be. It REPORTS — it
  /// lists leftover worktrees and a dry-run plan and changes nothing — so requiring the conductor
  /// merely to LOOK at what agents left behind is a visibility rule dressed as a power rule, and it
  /// produced two dead ends: the refusal's own next action is "mint you a token", which only the
  /// CONDUCTOR can do, so a solo user who was the conductor and held no `cap:<id>` could never get
  /// an answer; and the daemon itself appends "call get_workspace_hygiene" to replies that hit a
  /// pile of leftovers — so the product told agents to call a tool the gate then refused them.
  /// Measured against the live daemon before this change:
  ///
  ///     cannot call get_workspace_hygiene (your role is Working): ... is not in your grant
  ///
  /// `TidyWorkspace` STAYS, because it deletes, and that is exactly the power worth gating.
  let conductorOnlyTools : Set<ToolName> =
    set [ ToolName.ReassignClaim
          ToolName.SetIntegrationRef
          ToolName.MintMember
          ToolName.RevokeMember
          ToolName.ManageLocalData
          ToolName.TidyWorkspace ]

  /// The working member's surface. Everything `ToolName.all` holds, except the
  /// two conductor-only families (`CohortAdmin`, `Maintenance`) — which is
  /// exactly `Capability.RolePreset.Implementer`'s rule ("`CohortAdmin` and
  /// `Maintenance` are in none"), restated against the DU rather than against
  /// strings. Total over `ToolName`, so a new tool is in a working member's
  /// surface from the moment it is declared.
  let workingTools : Set<ToolName> =
    ToolName.all
    |> List.filter (fun tool ->
      Set.contains tool verifierTools || not (Set.contains tool conductorOnlyTools))
    |> Set.ofList

  /// The conductor's surface: everything, including the two conductor-only
  /// families. Derived from `ToolName.all` so a tool registered tomorrow is in
  /// the conductor's surface without anyone remembering to add it.
  let conductorTools : Set<ToolName> = Set.ofList ToolName.all

  /// A role's whole surface. Positive and total over the DU: there is no
  /// default arm, so a new `ToolRole` case is a compile error rather than an
  /// accidental union of everything.
  let toolsOf =
    function
    | ToolRole.Observer -> observerTools
    | ToolRole.Verifier -> verifierTools
    | ToolRole.Working -> workingTools
    | ToolRole.Conductor -> conductorTools

  /// The order on roles is inclusion, so a role that calls more tools can
  /// never be called narrower than one that calls fewer.
  let isNarrowerOrEqual (a: ToolRole) (b: ToolRole) : bool = Set.isSubset (toolsOf a) (toolsOf b)

  /// The role a cohort seat's joinable role grants, and what the conductor
  /// holds. `JoinableRole.Observer` and `.Verifier` keep their names; what
  /// they MEANT is what changed, and it is what the tables above say.
  ///
  /// WHY `Anonymous` IS `Working` AND NOT `Observer`. A caller that never joined the cohort has
  /// no seat, so it has no ROLE to narrow it — and mapping it to Observer was measured to brick
  /// the product: on an empty frame, `send_fsharp_code`, `run_app` and `hard_reset_fsi_session`
  /// were all refused ("your role is Observer"), so the moment ANY agent formed a cohort every
  /// other MCP client — the user in their editor, a dashboard tab, a second agent — was locked out
  /// of every code tool. Worse, the refusal's own next action ("mint you a token whose role
  /// includes it") is only reachable BY THE CONDUCTOR, so a solo user who was the conductor and
  /// held no `cap:<id>` could never mint their way out. That is a dead end for the common case,
  /// not a safety property.
  ///
  /// What this does NOT do is hand out authority over OTHER members' work. An unjoined caller is
  /// still `Anonymous` in `Cohort.Authority`, so `Authority.present` refuses it every COHORT verb
  /// that needs a seat — it cannot join someone else's claim, reassign a landing or mint a token.
  /// The role gate governs what a caller may do to ITS OWN sessions, and the cohort gate governs
  /// what it may do to everyone else's. Mapping `Anonymous` to `Working` keeps the second without
  /// letting the first become a way to bypass the second.
  ///
  /// A caller who HAS joined is unaffected: `Member(_, role)` and `Conductor _` both still map to
  /// their own role, so an Observer that joined deliberately is still read-only.
  let ofAuthority (authority: Cohort.Authority<'m>) : ToolRole =
    match authority with
    | Cohort.Authority.Anonymous -> ToolRole.Working
    | Cohort.Authority.Member(_, Cohort.JoinableRole.Observer) -> ToolRole.Observer
    | Cohort.Authority.Member(_, Cohort.JoinableRole.Verifier) -> ToolRole.Verifier
    | Cohort.Authority.Member(_, Cohort.JoinableRole.Implementer) -> ToolRole.Working
    | Cohort.Authority.Conductor _ -> ToolRole.Conductor

  /// What `role` may call. This is the authority gate's whole decision: a tool
  /// in the set, or `RoleForbids`.
  let admits (role: ToolRole) (tool: ToolName) : bool = Set.contains tool (toolsOf role)

// ── AuthorityRefusal: why the gate said no ───────────────────────────────
//
// A named DU, not an untyped string result. The repo counts that shape
// refusals DOWN with a ratchet (and a refusal that can only be a string cannot
// be matched on, logged by category, or turned into a status code). Every case
// carries the TOOL and the NEXT ACTION, so the agent that hit the wall is told
// what to do instead of only what happened.
[<RequireQualifiedAccess>]
type AuthorityRefusal =
  /// The caller's role may not call this tool. Carries the tool, the role it
  /// would have needed, and that role's own name.
  | RoleForbids of tool: ToolName * required: ToolRole
  /// The caller's grant does not name this tool. Distinct from `RoleForbids`
  /// so a caller can tell "the wrong role" from "this grant was never meant to
  /// include that tool".
  | NotInGrant of tool: ToolName * granted: ToolRole
  /// The tool is one this gate knows nothing about. Unreachable in practice —
  /// `Affordances.checkToolCallAllowed` refuses an undeclared tool first — and
  /// present so the gate still fails CLOSED rather than admitting a name
  /// nobody classified.
  | CapabilityRequired of tool: string

module AuthorityRefusal =
  /// One sentence: what was refused and by what rule.
  let describe (refusal: AuthorityRefusal) : string =
    match refusal with
    | AuthorityRefusal.RoleForbids(tool, required) ->
      sprintf "your role may not call %s: that needs the %s role." (ToolName.toString tool) (ToolRole.toToken required)
    | AuthorityRefusal.NotInGrant(tool, granted) ->
      sprintf "%s is not in your grant, which is the %s role." (ToolName.toString tool) (ToolRole.toToken granted)
    | AuthorityRefusal.CapabilityRequired tool ->
      sprintf "%s is not a tool this gate knows; it cannot be admitted on anyone's authority." tool

  /// The next action, so a refusal tells the caller how to proceed. The `Conductor`
  /// arms are unreachable in practice — the conductor holds every tool — and are
  /// written anyway so a new `ToolRole` case cannot fall through this match as a
  /// runtime `MatchFailureException`.
  let nextAction (refusal: AuthorityRefusal) : string =
    match refusal with
    | AuthorityRefusal.RoleForbids(_, ToolRole.Working) ->
      "Ask the cohort conductor to run it, or to mint you a token whose role includes it."
    | AuthorityRefusal.RoleForbids(_, (ToolRole.Observer | ToolRole.Verifier)) ->
      "Ask the cohort conductor to run it, or to mint you a token whose role includes it. Read-only tools are the ones this role may call."
    | AuthorityRefusal.RoleForbids(_, ToolRole.Conductor) ->
      "Only the cohort conductor may call this, and the conductor already holds it."
    | AuthorityRefusal.NotInGrant(_, ToolRole.Working) ->
      "Ask the cohort conductor to run it, or to mint you a token whose role includes it."
    | AuthorityRefusal.NotInGrant(_, (ToolRole.Observer | ToolRole.Verifier)) ->
      "Ask the cohort conductor to run it, or to mint you a token whose role includes it. This role's tools are the read-only ones."
    | AuthorityRefusal.NotInGrant(_, ToolRole.Conductor) ->
      "Only the cohort conductor holds this, and the conductor already holds it."
    | AuthorityRefusal.CapabilityRequired _ ->
      "This tool name is not one SageFs registers, so no role can call it. Check the tools/list response for a name that exists."

/// THE AUTHORITY GATE over the whole tool surface: `Authority -> ToolName ->
/// Result<unit, AuthorityRefusal>`. Pure; `Mcp.fs` resolves the caller's
/// `Authority` from the published `CohortFrame` and composes this with the
/// session-state gate by INTERSECTION — a call must pass both, and this gate
/// runs FIRST so a refused call never reaches session resolution.
///
/// It does not widen anything `cohortTools` decided: the ten cohort verbs are
/// intersected with `checkCohortToolAllowed` too, so the old cohort gate
/// remains exactly as strict and the two can only ever agree.
///
/// ANONYMOUS is `ToolRole.Working`, not "no role". Before tokens existed
/// every connection was a plain member, and the alternative — treating an
/// unjoined caller as having no authority at all — would make `join_cohort`
/// unreachable, and a cohort nobody can join is not a cohort.
/// `join_cohort` and `get_cohort_status` stay in Observer's list for that
/// reason (the same reason `alwaysReachableCohortTools` exists above).
let checkAuthorityAllowed (authority: Cohort.Authority<'m>) (tool: ToolName) : Result<unit, AuthorityRefusal> =
  let role = ToolRole.ofAuthority authority
  let required = ToolRole.toolsOf role
  let verdict =
    if ToolRole.admits role tool then Ok ()
    elif ToolRole.admits ToolRole.Working tool then Error(AuthorityRefusal.RoleForbids(tool, ToolRole.Working))
    else Error(AuthorityRefusal.NotInGrant(tool, role))
  match verdict, ToolName.tryCohortTool tool with
  | Ok (), Some cohortTool ->
    // Never a widening: a cohort verb the OLD gate refused stays refused,
    // whatever the role tables say.
    if checkCohortToolAllowed authority cohortTool then Ok ()
    else Error(AuthorityRefusal.RoleForbids(tool, ToolRole.Working))
  | Ok (), None -> Ok ()
  | Error refusal, _ -> Error refusal

/// The role a caller with `authority` holds, for display. The gate's own
/// output; a UI that wants the caller's role reads this rather than
/// re-deriving it from `Authority`.
let authorityRoleOf (authority: Cohort.Authority<'m>) : ToolRole = ToolRole.ofAuthority authority
