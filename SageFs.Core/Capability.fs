namespace SageFs

open System
open SageFs.Cohort
open SageFs.MemberTable

/// Per-run member capabilities: tokens the conductor mints so an orchestrator
/// (Nehemiah) can run N agents as N distinct cohort members, scoped, expiring
/// and revocable. Phase 2 of cohort-member-identity-as-capability.md, but not
/// built the way that doc sketched it.
///
/// The doc minted the token inside `Cohort.decide` from its entropy. Every
/// ledger row stores the entropy that command used, so that design would write
/// the token in plaintext to the persisted ledger, its exports and the replay
/// files. Here the token is drawn at the edge, shown once, and only its SHA-256
/// reaches this module. The capability table is its own pure reducer, outside
/// the event ledger, and the cohort sees a token holder as an ordinary member
/// whose id is `MemberId.Capability <fingerprint>`.
///
/// What this module decides, all of it pure (time is a parameter, randomness
/// never enters):
///   - a grant is a closed role preset, a canonical scope prefix, a route binding (the session or
///     checkout it may act on, never "anything") and an expiry;
///   - a grant can never be wider than its minter's (`Grant.isNarrowerOrEqual`,
///     a partial order the property tests check), and a request that is wider
///     is refused with the widenings named, never clamped;
///   - a revoked token stays revoked, whatever happens to it afterwards;
///   - a token that is not used for a cohort lease window lapses, the way a
///     silent member's seat does, and no token outlives its own expiry;
///   - the tool allow-list is the preset's set of tool CLASSES, a closed DU.
///     There is no free-form allow-list, and a tool nobody classified is refused.
///
/// A scope prefix is policy, not containment: it refuses a claim, an eval
/// routed by the daemon and a landing outside it, but a process that can write
/// the files can still write them. Nehemiah's sandbox is the containment. And
/// any grant that includes `Eval` can run arbitrary code as the daemon's OS
/// user, so the narrow presets are the ones that mean something against a
/// hostile agent.
module Capability =

  // ── Scope prefix ──────────────────────────────────────────────────────────

  /// A canonical repo-relative directory a grant is confined to. The empty
  /// prefix is the whole repo. Constructed only by `ScopePrefix.tryParse`, so
  /// a prefix is always canonical and `src/Foo/../Bar` cannot be one.
  type ScopePrefix = private ScopePrefix of string

  module ScopePrefix =
    let repoRoot : ScopePrefix = ScopePrefix ""

    let tryParse (raw: string) : Result<ScopePrefix, PathRefusal> =
      ClaimPath.tryCanonical raw |> Result.map ScopePrefix

    let value (ScopePrefix canonical) : string = canonical

    let private pathWithin (path: string) (prefix: string) =
      prefix = "" || path = prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal)

    /// `inner` is `outer` or a directory under it. A partial order.
    let isWithin (ScopePrefix inner) (ScopePrefix outer) : bool = pathWithin inner outer

    let private directoryOf (canonicalFsproj: string) : string =
      match canonicalFsproj.LastIndexOf '/' with
      | -1 -> ""
      | i -> canonicalFsproj.Substring(0, i)

    /// Whether a claim over `scope` stays inside the prefix. A file is covered
    /// when its path is under the prefix; a project claim covers its whole
    /// directory, so the project's directory must be. A path that cannot be
    /// canonicalized (it escapes the repo, or is rooted) is covered by nothing.
    let covers (ScopePrefix prefix) (scope: ClaimScope) : bool =
      match ClaimScope.tryCanonical scope with
      | Error _ -> false
      | Ok(ClaimScope.File path) -> pathWithin path prefix
      | Ok(ClaimScope.Project path) -> pathWithin (directoryOf path) prefix

  /// A name that is not one of a closed set, with the names that are.
  type UnknownName = { Given: string; Expected: string list }

  // ── Roles: closed presets over closed tool classes ────────────────────────

  /// What a tool does, coarsely. A role is a set of these, never a list of tool
  /// names, so a new tool forces one decision (its class) and every role then
  /// has an answer for it.
  [<RequireQualifiedAccess>]
  type ToolClass =
    /// Read the cohort: members, claims, landings.
    | CohortRead
    /// Join or leave the cohort.
    | CohortMembership
    /// Claim a scope, release it, queue a landing.
    | CohortWork
    /// Reassign claims, configure the integration, mint and revoke members.
    /// Conductor only: no preset holds it.
    | CohortAdmin
    /// Read daemon, session and project status; point at a session.
    | SessionRead
    /// Tell SageFs what was confusing. Writes only the local friction store.
    | Feedback
    /// Read analysis of code and of the session's history. Runs nothing.
    | CodeAnalysis
    /// Run the project's tests. That runs user code.
    | TestRun
    /// Ask for build, test and run-app leases.
    | Leases
    /// Evaluate F# in the session: arbitrary code as the daemon's OS user.
    | Eval
    /// Create, reset, switch and stop sessions, and toggle hot reload.
    | SessionLifecycle
    /// Start and stop the session's application.
    | AppControl
    /// SageFs's own local data and the machine's leftover worktrees. Conductor only.
    | Maintenance

  module ToolClass =
    let all : ToolClass list =
      [ ToolClass.CohortRead
        ToolClass.CohortMembership
        ToolClass.CohortWork
        ToolClass.CohortAdmin
        ToolClass.SessionRead
        ToolClass.Feedback
        ToolClass.CodeAnalysis
        ToolClass.TestRun
        ToolClass.Leases
        ToolClass.Eval
        ToolClass.SessionLifecycle
        ToolClass.AppControl
        ToolClass.Maintenance ]

    let toToken =
      function
      | ToolClass.CohortRead -> "CohortRead"
      | ToolClass.CohortMembership -> "CohortMembership"
      | ToolClass.CohortWork -> "CohortWork"
      | ToolClass.CohortAdmin -> "CohortAdmin"
      | ToolClass.SessionRead -> "SessionRead"
      | ToolClass.Feedback -> "Feedback"
      | ToolClass.CodeAnalysis -> "CodeAnalysis"
      | ToolClass.TestRun -> "TestRun"
      | ToolClass.Leases -> "Leases"
      | ToolClass.Eval -> "Eval"
      | ToolClass.SessionLifecycle -> "SessionLifecycle"
      | ToolClass.AppControl -> "AppControl"
      | ToolClass.Maintenance -> "Maintenance"

    /// The registered tools of a class. Total over the DU, so a class cannot be
    /// added without saying which tools it holds. The tool names are the MCP
    /// registration names (`Affordances.gatingDomain` declares the same set, and
    /// a test holds the two together).
    let toolsOf =
      function
      | ToolClass.CohortRead -> [ "get_cohort_status" ]
      | ToolClass.CohortMembership -> [ "join_cohort"; "leave_cohort" ]
      | ToolClass.CohortWork -> [ "acquire_claim"; "release_claim"; "request_landing" ]
      | ToolClass.CohortAdmin -> [ "reassign_claim"; "set_integration_ref"; "mint_member"; "revoke_member" ]
      | ToolClass.SessionRead ->
        [ "get_daemon_status"; "get_session_status"; "list_sessions"; "get_available_projects"
          "list_runnable_projects"; "get_friction_report"; "get_friction_summary"; "discover_features"
          "get_recent_fsi_events"; "switch_session" ]
      | ToolClass.Feedback -> [ "report_friction" ]
      | ToolClass.CodeAnalysis ->
        [ "check_fsharp_code"; "diagnose"; "coverage_intel"; "impact_forecast"; "suggest_next_action"; "plan_ripple"
          "preview_what_if"; "suggest_next_cell"; "get_cell_dependencies"; "decompose_pipeline"; "explain_test_failure"
          "list_tests"; "suggest_repair"; "get_session_filmstrip"; "get_eval_timeline"; "get_eval_diff"
          "get_message_journal"; "export_notebook"; "export_session_transcript" ]
      | ToolClass.TestRun -> [ "run_tests"; "targeted_verify" ]
      | ToolClass.Leases ->
        [ "acquire_full_build_lease"; "acquire_test_suite_lease"; "acquire_run_app_lease"; "release_work_lease" ]
      | ToolClass.Eval -> [ "send_fsharp_code"; "cancel_eval"; "manage_scratch_pad" ]
      | ToolClass.SessionLifecycle ->
        [ "create_project_session"; "create_solution_session"; "create_bare_session"; "reset_fsi_session"
          "hard_reset_fsi_session"; "switch_workflow"; "stop_session"; "enable_hot_reload"; "disable_hot_reload"
          "reset_hot_reload_state"; "set_reflection_read_mode"; "nudge_value" ]
      | ToolClass.AppControl -> [ "run_app"; "stop_app" ]
      | ToolClass.Maintenance -> [ "manage_local_data"; "get_workspace_hygiene"; "tidy_workspace" ]

    let private classByTool : Map<string, ToolClass> =
      all |> List.collect (fun c -> toolsOf c |> List.map (fun tool -> tool, c)) |> Map.ofList

    /// The class of a registered tool, or `None` for a name nobody classified.
    let ofTool (toolName: string) : ToolClass option = Map.tryFind toolName classByTool

  /// The roles the conductor can mint, narrowest first. A closed set: there is
  /// no way to ask for an arbitrary list of tools.
  [<RequireQualifiedAccess>]
  type RolePreset =
    /// Reads the cohort and the session's status. Calls nothing that analyses or runs.
    | Observer
    /// Observer plus read-only analysis of code and history. No eval, no tests.
    | Analysis
    /// Analysis plus running tests and taking build and test leases. No eval.
    | Verifier
    /// Everything a working member does: eval, sessions, apps, claims, landings.
    | Implementer

  module RolePreset =
    let all : RolePreset list = [ RolePreset.Observer; RolePreset.Analysis; RolePreset.Verifier; RolePreset.Implementer ]

    let toToken =
      function
      | RolePreset.Observer -> "Observer"
      | RolePreset.Analysis -> "Analysis"
      | RolePreset.Verifier -> "Verifier"
      | RolePreset.Implementer -> "Implementer"

    let tryParse (raw: string) : Result<RolePreset, UnknownName> =
      let wanted = if isNull raw then "" else raw.Trim()
      match all |> List.tryFind (fun p -> String.Equals(toToken p, wanted, StringComparison.OrdinalIgnoreCase)) with
      | Some preset -> Ok preset
      | None -> Error { Given = wanted; Expected = all |> List.map toToken }

    /// The cohort role the seat is created with.
    let joinableRole =
      function
      | RolePreset.Observer -> JoinableRole.Observer
      | RolePreset.Analysis -> JoinableRole.Observer
      | RolePreset.Verifier -> JoinableRole.Verifier
      | RolePreset.Implementer -> JoinableRole.Implementer

    let private observerClasses =
      set [ ToolClass.CohortRead; ToolClass.CohortMembership; ToolClass.SessionRead; ToolClass.Feedback ]

    let private analysisClasses = Set.add ToolClass.CodeAnalysis observerClasses

    let private verifierClasses =
      analysisClasses |> Set.add ToolClass.TestRun |> Set.add ToolClass.Leases

    let private implementerClasses =
      verifierClasses
      |> Set.add ToolClass.Eval
      |> Set.add ToolClass.SessionLifecycle
      |> Set.add ToolClass.AppControl
      |> Set.add ToolClass.CohortWork

    /// Every class a preset may call. `CohortAdmin` and `Maintenance` are in none.
    let toolClasses =
      function
      | RolePreset.Observer -> observerClasses
      | RolePreset.Analysis -> analysisClasses
      | RolePreset.Verifier -> verifierClasses
      | RolePreset.Implementer -> implementerClasses

    /// The order on roles is inclusion of tool classes, so it can never disagree with what a role may call.
    let isNarrowerOrEqual (a: RolePreset) (b: RolePreset) : bool =
      Set.isSubset (toolClasses a) (toolClasses b)

  // ── Route binding: where a token may route ────────────────────────────────
  //
  // The role says WHICH tools a token may call and the scope says which FILES it may claim.
  // Neither said which SESSION or CHECKOUT the call may act on, so any tool that takes a
  // `session_id` or a `working_directory` honored whichever the caller named: an Analysis token
  // minted for one session could read another's. A grant now names exactly one of three things,
  // a closed DU and not a bool or an option, so "bound to nothing" is not a state a minted
  // token can be in.
  //
  // WHAT THIS IS NOT. It decides what the MCP tool surface will do for a caller holding the
  // token. It does not stop a process the token's holder runs (an `Implementer` token can eval
  // arbitrary F# as the daemon's OS user, and that code can call the daemon's loopback with any
  // session id). Like the scope prefix and the role, it is policy that makes the wrong call
  // refused and named, and it is not containment. The long form is in `ToolAuthorityGate.fs`.

  /// Why a directory is not a checkout root.
  [<RequireQualifiedAccess>]
  type RootRefusal =
    | Blank
    /// Not an absolute path, so it names no directory on this machine.
    | NotAbsolute of raw: string
    /// The root of the file system names everything, which is not a checkout.
    | FilesystemRoot of raw: string

  /// A canonical absolute directory a token is confined to. Constructed only by
  /// `CheckoutRoot.tryParse`, so `/work/x/../a` and `/work/a/` are the same root.
  type CheckoutRoot = private CheckoutRoot of string

  module CheckoutRoot =
    /// The canonical spelling of an absolute directory: separators normalized, `.` and `..`
    /// resolved, no trailing separator. `None` for text that is not an absolute path. Pure string
    /// work: it never touches the disk, so a directory that does not exist has a spelling too.
    let canonical (raw: string) : string option =
      match String.IsNullOrWhiteSpace raw with
      | true -> None
      | false ->
        let text = raw.Trim()
        match IO.Path.IsPathRooted text with
        | false -> None
        | true ->
          let full = IO.Path.GetFullPath text
          match IO.Path.GetPathRoot full = full with
          | true -> Some full
          | false -> Some(full.TrimEnd('/', '\\'))

    let tryParse (raw: string) : Result<CheckoutRoot, RootRefusal> =
      match String.IsNullOrWhiteSpace raw with
      | true -> Error RootRefusal.Blank
      | false ->
        match canonical raw with
        | None -> Error(RootRefusal.NotAbsolute raw)
        | Some path when IO.Path.GetPathRoot path = path -> Error(RootRefusal.FilesystemRoot raw)
        | Some path -> Ok(CheckoutRoot path)

    let value (CheckoutRoot canonical) : string = canonical

  /// Where a grant may route.
  [<RequireQualifiedAccess>]
  type RouteBinding =
    /// Every session the daemon serves. The conductor's authority and the identity of a connection
    /// with no token. NO MINT CAN HAND THIS OUT (`MintRefusal.UnboundNotMintable`), so a token is
    /// always bound to something.
    | Unbound
    /// One session, by id.
    | BoundToSession of sessionId: string
    /// Every session whose working directory is this checkout, a git worktree nested under it excluded.
    | BoundToCheckout of CheckoutRoot

  /// Why a mint request names no usable binding.
  [<RequireQualifiedAccess>]
  type BindRefusal =
    | NothingNamed
    | BothNamed
    | NotAbsolute of raw: string
    | FilesystemRoot of raw: string

  module RouteBinding =
    /// `a` routes nowhere `b` does not. `Unbound` is the top; two bound grants are related only when
    /// they are the same binding, because whether a session is inside a checkout is a fact about the
    /// registry, not about the grants, and an order that guessed would be a widening waiting to happen.
    let isNarrowerOrEqual (a: RouteBinding) (b: RouteBinding) : bool =
      match b with
      | RouteBinding.Unbound -> true
      | RouteBinding.BoundToSession _
      | RouteBinding.BoundToCheckout _ -> a = b

    /// The binding a mint request asks for: a session OR a working directory, never both, never neither.
    /// The result is never `Unbound`.
    let ofRequest (sessionId: string option) (workingDirectory: string option) : Result<RouteBinding, BindRefusal> =
      let present (text: string option) = text |> Option.map (fun s -> s.Trim()) |> Option.filter (fun s -> s <> "")
      match present sessionId, present workingDirectory with
      | None, None -> Error BindRefusal.NothingNamed
      | Some _, Some _ -> Error BindRefusal.BothNamed
      | Some id, None -> Ok(RouteBinding.BoundToSession id)
      | None, Some directory ->
        match CheckoutRoot.tryParse directory with
        | Ok root -> Ok(RouteBinding.BoundToCheckout root)
        | Error RootRefusal.Blank -> Error BindRefusal.NothingNamed
        | Error(RootRefusal.NotAbsolute raw) -> Error(BindRefusal.NotAbsolute raw)
        | Error(RootRefusal.FilesystemRoot raw) -> Error(BindRefusal.FilesystemRoot raw)

    /// In words, for a refusal or the mint reply.
    let describe (binding: RouteBinding) : string =
      match binding with
      | RouteBinding.Unbound -> "any session the daemon serves"
      | RouteBinding.BoundToSession id -> sprintf "session %s" id
      | RouteBinding.BoundToCheckout root -> sprintf "the checkout %s" (CheckoutRoot.value root)

  /// How a tool uses the session or directory it is given, closed. A binding means something
  /// different for each: a tool that acts on a session is held to the sessions the binding allows, a
  /// tool that picks a cohort by directory must be told which directory, and so on. A tool with no
  /// kind is refused to a bound token, so a tool added tomorrow cannot be forgotten into the gate.
  [<RequireQualifiedAccess>]
  type RouteKind =
    /// Acts on one session: `session_id`, else `working_directory`, else the caller's own active session.
    | OnSession
    /// Lists the sessions the daemon serves. What it shows is cut to the binding underneath it.
    | ListsSessions
    /// Starts a new session rooted at `working_directory`.
    | CreatesSession
    /// `working_directory` picks the repository's cohort. Omitted, it means the DAEMON's own repository.
    | InCohort
    /// Lists files under `working_directory` on disk.
    | ReadsDirectory
    /// Acts on the daemon or on the caller's own state, and names no session. Leases, friction and the
    /// daemon's own status. A directory or session id it is given anyway is still held to the binding.
    | DaemonWide

  module RouteKind =
    let all : RouteKind list =
      [ RouteKind.OnSession
        RouteKind.ListsSessions
        RouteKind.CreatesSession
        RouteKind.InCohort
        RouteKind.ReadsDirectory
        RouteKind.DaemonWide ]

    let toToken =
      function
      | RouteKind.OnSession -> "OnSession"
      | RouteKind.ListsSessions -> "ListsSessions"
      | RouteKind.CreatesSession -> "CreatesSession"
      | RouteKind.InCohort -> "InCohort"
      | RouteKind.ReadsDirectory -> "ReadsDirectory"
      | RouteKind.DaemonWide -> "DaemonWide"

    /// One entry per registered tool. The same names `ToolClass.toolsOf` lists, held to them by a test.
    let private tagged (kind: RouteKind) (tools: string list) : (string * RouteKind) list =
      tools |> List.map (fun tool -> tool, kind)

    let assignments : (string * RouteKind) list =
      tagged
        RouteKind.InCohort
        [ "get_cohort_status"; "join_cohort"; "leave_cohort"; "acquire_claim"; "release_claim"
          "request_landing"; "reassign_claim"; "set_integration_ref"; "mint_member"; "revoke_member" ]
      @ tagged
          RouteKind.OnSession
          [ "get_session_status"; "list_runnable_projects"; "discover_features"; "get_recent_fsi_events"
            "switch_session"; "check_fsharp_code"; "diagnose"; "coverage_intel"; "impact_forecast"
            "suggest_next_action"; "plan_ripple"; "preview_what_if"; "suggest_next_cell"
            "get_cell_dependencies"; "explain_test_failure"; "list_tests"; "suggest_repair"
            "get_session_filmstrip"; "get_eval_timeline"; "get_eval_diff"; "get_message_journal"
            "export_notebook"; "export_session_transcript"; "run_tests"; "targeted_verify"
            "send_fsharp_code"; "cancel_eval"; "manage_scratch_pad"; "reset_fsi_session"
            "hard_reset_fsi_session"; "switch_workflow"; "stop_session"; "enable_hot_reload"
            "disable_hot_reload"; "reset_hot_reload_state"; "set_reflection_read_mode"; "run_app"
            "stop_app"; "nudge_value" ]
      @ tagged RouteKind.CreatesSession [ "create_project_session"; "create_solution_session"; "create_bare_session" ]
      @ tagged RouteKind.ListsSessions [ "list_sessions" ]
      @ tagged RouteKind.ReadsDirectory [ "get_available_projects" ]
      @ tagged
          RouteKind.DaemonWide
          [ "get_daemon_status"; "get_friction_report"; "get_friction_summary"; "report_friction"
            "decompose_pipeline"; "acquire_full_build_lease"; "acquire_test_suite_lease"
            "acquire_run_app_lease"; "release_work_lease"; "manage_local_data"
            "get_workspace_hygiene"; "tidy_workspace" ]

    let private kindByTool : Map<string, RouteKind> = Map.ofList assignments

    /// The kind of a registered tool, or `None` for a name nobody classified.
    let ofTool (toolName: string) : RouteKind option = Map.tryFind toolName kindByTool

  /// What the gate knows of a call before its body runs: the tool, and the two parameters that route.
  type RouteCall = {
    Tool: string
    SessionId: string option
    WorkingDirectory: string option
  }

  /// What the registry says about the call, read by the edge and handed in, so the decision is pure.
  type RouteFacts = {
    /// The working directory of the session `session_id` names, when the daemon serves it.
    NamedSessionDirectory: string option
    /// The working directory of the bound session, when the binding is `BoundToSession` and it is served.
    BoundSessionDirectory: string option
    /// Whether `directory` lies inside checkout `root`. A git worktree nested under `root` is its own
    /// checkout, so it is NOT inside. Both arguments are canonical.
    Within: string -> string -> bool
  }

  [<RequireQualifiedAccess>]
  type RouteRefusal =
    /// The call names a session the binding does not allow (or one the daemon does not serve, which
    /// is not told apart from one outside the binding, so the refusal is no oracle for other sessions).
    | SessionOutsideBinding of requested: string * binding: RouteBinding
    /// The call names a directory that is not inside the binding.
    | DirectoryOutsideBinding of requested: string * binding: RouteBinding
    /// The binding names a session the daemon no longer serves, so no directory can be shown to be inside it.
    | BoundSessionNotServed of sessionId: string
    /// The tool picks a cohort or reads a directory and the call named none. Defaulting it would act in
    /// the daemon's own repository, which is outside the binding.
    | DirectoryRequired of tool: string * binding: RouteBinding
    /// A token bound to one session creates none: a new session is one it is not bound to.
    | CannotCreateSessions of binding: RouteBinding
    /// The tool has no routing kind, so the gate cannot say where it acts.
    | UnclassifiedRouting of tool: string

  module RouteRefusal =
    /// One sentence: what was refused and by what rule.
    let describe (refusal: RouteRefusal) : string =
      match refusal with
      | RouteRefusal.SessionOutsideBinding(requested, binding) ->
        sprintf "This member token is bound to %s, and session %s is not inside that." (RouteBinding.describe binding) requested
      | RouteRefusal.DirectoryOutsideBinding(requested, binding) ->
        sprintf "This member token is bound to %s, and the directory '%s' is not inside that." (RouteBinding.describe binding) requested
      | RouteRefusal.BoundSessionNotServed id ->
        sprintf "This member token is bound to session %s, which the daemon no longer serves, so no directory can be shown to be inside it." id
      | RouteRefusal.DirectoryRequired(tool, binding) ->
        sprintf "%s acts in the repository of the directory it is given, and this call named none, which would mean the daemon's own repository. This member token is bound to %s." tool (RouteBinding.describe binding)
      | RouteRefusal.CannotCreateSessions binding ->
        sprintf "This member token is bound to %s, and creating a session routes to one it is not bound to." (RouteBinding.describe binding)
      | RouteRefusal.UnclassifiedRouting tool ->
        sprintf "SageFs has no routing rule for the tool %s, so a bound member token may not call it." tool

    /// The next action, so a refusal says how to proceed and not only that it stopped.
    let nextAction (refusal: RouteRefusal) : string =
      let useInstead (binding: RouteBinding) =
        match binding with
        | RouteBinding.BoundToSession id -> sprintf "Pass session_id %s, or no session at all to act on it." id
        | RouteBinding.BoundToCheckout root -> sprintf "Pass a working_directory inside %s, or none to act on a session there." (CheckoutRoot.value root)
        | RouteBinding.Unbound -> "This token is not bound, so nothing is outside it."
      match refusal with
      | RouteRefusal.SessionOutsideBinding(_, binding)
      | RouteRefusal.DirectoryOutsideBinding(_, binding) ->
        sprintf "%s To reach another session, ask the conductor to mint a token bound to it (mint_member)." (useInstead binding)
      | RouteRefusal.BoundSessionNotServed id ->
        sprintf "Session %s is gone. Ask the conductor to mint a token for the session or checkout you are working in (mint_member)." id
      | RouteRefusal.DirectoryRequired(_, binding) ->
        match binding with
        | RouteBinding.BoundToSession id -> sprintf "Pass working_directory: the directory of session %s, which list_sessions shows." id
        | RouteBinding.BoundToCheckout root -> sprintf "Pass working_directory %s (or a directory inside it)." (CheckoutRoot.value root)
        | RouteBinding.Unbound -> "This token is not bound, so a directory is not required."
      | RouteRefusal.CannotCreateSessions binding ->
        sprintf "Use the session you are bound to (%s). A token bound to a checkout can create sessions inside it: ask the conductor for one (mint_member with working_directory)." (RouteBinding.describe binding)
      | RouteRefusal.UnclassifiedRouting tool ->
        sprintf "Use the conductor's own connection, or report this: every registered tool must have a routing kind (%s has none)." tool

  module Route =
    /// May a caller bound by `binding` act on the session `sessionId`, rooted at `sessionDirectory`?
    /// The one rule the confinement of the registry and the gate share.
    let permitsSession (binding: RouteBinding) (within: string -> string -> bool) (sessionId: string) (sessionDirectory: string) : bool =
      match binding with
      | RouteBinding.Unbound -> true
      | RouteBinding.BoundToSession bound -> sessionId = bound
      | RouteBinding.BoundToCheckout root -> within (CheckoutRoot.value root) sessionDirectory

    /// The directory a binding confines directories to. `Unbound` has none: every directory is inside it.
    let private regionOf (binding: RouteBinding) (facts: RouteFacts) : Result<string option, RouteRefusal> =
      match binding with
      | RouteBinding.Unbound -> Ok None
      | RouteBinding.BoundToCheckout root -> Ok(Some(CheckoutRoot.value root))
      | RouteBinding.BoundToSession id ->
        match facts.BoundSessionDirectory |> Option.bind CheckoutRoot.canonical with
        | Some directory -> Ok(Some directory)
        | None -> Error(RouteRefusal.BoundSessionNotServed id)

    let private checkSession (binding: RouteBinding) (call: RouteCall) (facts: RouteFacts) : Result<unit, RouteRefusal> =
      match call.SessionId with
      | None -> Ok()
      | Some id ->
        let inside =
          match binding with
          | RouteBinding.Unbound -> true
          | RouteBinding.BoundToSession bound -> id = bound
          | RouteBinding.BoundToCheckout root -> facts.NamedSessionDirectory |> Option.exists (facts.Within(CheckoutRoot.value root))
        match inside with
        | true -> Ok()
        | false -> Error(RouteRefusal.SessionOutsideBinding(id, binding))

    let private checkDirectory (binding: RouteBinding) (call: RouteCall) (facts: RouteFacts) : Result<unit, RouteRefusal> =
      match call.WorkingDirectory with
      | None -> Ok()
      | Some raw ->
        match regionOf binding facts with
        | Error refusal -> Error refusal
        | Ok None -> Ok()
        | Ok(Some region) ->
          // Resolved before it is compared, so `/work/a/../b` is `/work/b`. A directory that is not
          // absolute cannot be shown to be inside anything.
          match CheckoutRoot.canonical raw |> Option.exists (facts.Within region) with
          | true -> Ok()
          | false -> Error(RouteRefusal.DirectoryOutsideBinding(raw, binding))

    let private checkKind (binding: RouteBinding) (kind: RouteKind) (call: RouteCall) : Result<unit, RouteRefusal> =
      let directoryRequired () =
        match call.WorkingDirectory with
        | Some _ -> Ok()
        | None -> Error(RouteRefusal.DirectoryRequired(call.Tool, binding))
      match kind with
      | RouteKind.OnSession
      | RouteKind.ListsSessions
      | RouteKind.DaemonWide -> Ok()
      | RouteKind.InCohort
      | RouteKind.ReadsDirectory -> directoryRequired ()
      | RouteKind.CreatesSession ->
        match binding with
        | RouteBinding.BoundToSession _ -> Error(RouteRefusal.CannotCreateSessions binding)
        | RouteBinding.BoundToCheckout _
        | RouteBinding.Unbound -> directoryRequired ()

    /// THE decision every call from a bound token passes through. Pure. `Unbound` admits everything,
    /// because it is the conductor's authority and a connection with no token. For a bound token a
    /// session id or directory the call names is held to the binding on EVERY tool, a tool with no
    /// routing kind is refused, and the kind says what a call that names nothing may do.
    let admit (binding: RouteBinding) (call: RouteCall) (facts: RouteFacts) : Result<unit, RouteRefusal> =
      match binding with
      | RouteBinding.Unbound -> Ok()
      | RouteBinding.BoundToSession _
      | RouteBinding.BoundToCheckout _ ->
        match RouteKind.ofTool call.Tool with
        | None -> Error(RouteRefusal.UnclassifiedRouting call.Tool)
        | Some kind ->
          match checkSession binding call facts with
          | Error refusal -> Error refusal
          | Ok() ->
            match checkDirectory binding call facts with
            | Error refusal -> Error refusal
            | Ok() -> checkKind binding kind call

  // ── Grant: what a token may do ────────────────────────────────────────────

  type Grant = {
    Preset: RolePreset
    Scope: ScopePrefix
    /// Where the token may route. A minted token is never `Unbound`.
    Route: RouteBinding
    /// The token never works at or after this instant.
    NotAfter: DateTime
  }

  /// One way a requested grant is wider than its minter's.
  [<RequireQualifiedAccess>]
  type Widening =
    | Role of requested: RolePreset * minter: RolePreset
    | Scope of requested: ScopePrefix * minter: ScopePrefix
    | Route of requested: RouteBinding * minter: RouteBinding
    | Expiry of requested: DateTime * minter: DateTime

  module Grant =
    /// What the conductor may hand out: everything, everywhere, for ever. A
    /// minted token is always narrower than this, because mint caps a lifetime
    /// and refuses to hand out `Unbound`.
    let conductorAuthority : Grant =
      { Preset = RolePreset.Implementer
        Scope = ScopePrefix.repoRoot
        Route = RouteBinding.Unbound
        NotAfter = DateTime.MaxValue }

    /// `a` grants nothing `b` does not: a role that calls no more tools, a scope
    /// that is the same directory or under it, a route that is no wider, an expiry no later. A partial
    /// order, so "narrower than the minter" means something the tests can check.
    let isNarrowerOrEqual (a: Grant) (b: Grant) : bool =
      RolePreset.isNarrowerOrEqual a.Preset b.Preset
      && ScopePrefix.isWithin a.Scope b.Scope
      && RouteBinding.isNarrowerOrEqual a.Route b.Route
      && a.NotAfter <= b.NotAfter

    /// Each way `requested` is wider than `minter`. Empty exactly when it is narrower or equal.
    let wideningsOf (requested: Grant) (minter: Grant) : Widening list =
      [ if not (RolePreset.isNarrowerOrEqual requested.Preset minter.Preset) then
          Widening.Role(requested.Preset, minter.Preset)
        if not (ScopePrefix.isWithin requested.Scope minter.Scope) then
          Widening.Scope(requested.Scope, minter.Scope)
        if not (RouteBinding.isNarrowerOrEqual requested.Route minter.Route) then
          Widening.Route(requested.Route, minter.Route)
        if requested.NotAfter > minter.NotAfter then
          Widening.Expiry(requested.NotAfter, minter.NotAfter) ]

  // ── Token: shown once, only its hash is kept ──────────────────────────────

  /// SHA-256 of a token, lowercase hex. The only form of a token the daemon
  /// keeps, and the only form a command carries. The token is 256 bits of
  /// entropy, so a plain hash is enough: there is nothing to brute-force.
  type TokenHash = private TokenHash of string

  module TokenHash =
    let ofToken (rawToken: string) : TokenHash =
      let digest = Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes rawToken)
      TokenHash(digest |> Array.map (fun b -> b.ToString "x2") |> String.concat "")

    let value (TokenHash hex) : string = hex

    /// A hash read back from its hex form (the HTTP edge hands it over as a claim). Only a
    /// well-formed SHA-256 hex string is a hash, so nothing else can reach the table.
    let tryOfHex (hex: string) : TokenHash option =
      match isNull hex || hex.Length <> 64 with
      | true -> None
      | false ->
        match hex |> Seq.forall (fun c -> Uri.IsHexDigit c && not (Char.IsUpper c)) with
        | true -> Some(TokenHash hex)
        | false -> None

  module Token =
    /// How many random bytes a token is made of.
    let entropyBytes : int = 32

    /// Every token starts with this, so a leaked one is recognizable in a log or a scanner.
    let prefix = "sfm_"

    /// The token text for `entropy`. Pure: the edge draws the bytes from a
    /// cryptographic source, shows the result once, and keeps only its hash.
    let ofEntropy (entropy: byte[]) : string =
      prefix + Convert.ToBase64String(entropy).TrimEnd('=').Replace('+', '-').Replace('/', '_')

  /// The public, non-secret name of a token: a prefix of its hash. It is what
  /// the cohort shows as the member (`cap:<id>`) and what revoke takes.
  type CapabilityId = CapabilityId of string

  module CapabilityId =
    let private fingerprintLength = 16

    let ofHash (hash: TokenHash) : CapabilityId = CapabilityId((TokenHash.value hash).Substring(0, fingerprintLength))

    let value (CapabilityId id) : string = id

    let memberId (CapabilityId id) : MemberId = MemberId.Capability id

    let tryOfMemberId (who: MemberId) : CapabilityId option =
      match who with
      | MemberId.Capability fingerprint -> Some(CapabilityId fingerprint)
      | MemberId.Browser _
      | MemberId.Mcp _
      | MemberId.Minted _ -> None

  /// The member a capability is, and the text of its public id. Plain functions beside the module
  /// because `CapabilityId` is also a union case, and a caller outside this file that writes
  /// `Capability.CapabilityId.memberId` gets the case, not the module.
  let memberIdOf (id: CapabilityId) : MemberId = CapabilityId.memberId id

  let idText (id: CapabilityId) : string = CapabilityId.value id

  // ── State, commands, the pure reducer ─────────────────────────────────────

  [<RequireQualifiedAccess>]
  type CapabilityStatus =
    | Active
    /// Terminal. A revoked token is never active again, and its hash cannot be minted again.
    | Revoked of at: DateTime

  type CapabilityRecord = {
    Id: CapabilityId
    MintedBy: MemberId
    MintedAt: DateTime
    /// The last time the token was presented and accepted. A token unused for a cohort lease window lapses.
    LastSeen: DateTime
    Grant: Grant
    Status: CapabilityStatus
  }

  /// The capability table, keyed by token hash. In memory only: a daemon restart
  /// drops every token, and the orchestrator mints new ones. Not the cohort
  /// ledger, so a token's existence is never in a ledger row.
  type CapabilityState = { Records: Map<TokenHash, CapabilityRecord> }

  module CapabilityState =
    let empty : CapabilityState = { Records = Map.empty }

  /// Who is minting and the grant they hold themselves. A conductor with no
  /// token holds `Grant.conductorAuthority`; one acting under a token holds that token's grant.
  type Minter = { Authority: Authority<MemberId>; Grant: Grant }

  [<RequireQualifiedAccess>]
  type CapabilityCommand =
    /// The command carries the HASH. The raw token never enters this module.
    | Mint of minter: Minter * requested: Grant * hash: TokenHash
    /// The token was presented and accepted: keep it alive.
    | Touch of hash: TokenHash
    | Revoke of by: Authority<MemberId> * target: CapabilityId

  [<RequireQualifiedAccess>]
  type CapabilityEvent =
    | Minted of CapabilityId * Grant
    | Touched of CapabilityId
    | Revoked of CapabilityId

  [<RequireQualifiedAccess>]
  type MintRefusal =
    | NotConductor
    | DuplicateToken
    | NotInTheFuture of notAfter: DateTime
    /// The request asks for `RouteBinding.Unbound`. No token routes anywhere: even the conductor's
    /// own authority is not something a mint can hand out, so a token always names where it routes.
    | UnboundNotMintable
    | WouldWiden of Widening list
    | LifetimeTooLong of requested: TimeSpan * max: TimeSpan

  [<RequireQualifiedAccess>]
  type RevokeRefusal =
    | NotConductor
    | UnknownCapability of CapabilityId
    | AlreadyRevoked of at: DateTime

  [<RequireQualifiedAccess>]
  type CapabilityRefusal =
    | Mint of MintRefusal
    | Revoke of RevokeRefusal

  /// Why a presented token does not resolve. There is no case that falls back
  /// to the connection's own identity: a bad token is a refusal.
  [<RequireQualifiedAccess>]
  type PresentRefusal =
    | Unknown
    | Expired of notAfter: DateTime
    | LeaseLapsed of lastSeen: DateTime
    | Revoked of at: DateTime

  /// Whether the token behind `hash` is good at `now`. Revocation outranks
  /// expiry, which outranks idleness, so the reason named is the most final one.
  let resolve (now: DateTime) (state: CapabilityState) (hash: TokenHash) : Result<CapabilityRecord, PresentRefusal> =
    match Map.tryFind hash state.Records with
    | None -> Error PresentRefusal.Unknown
    | Some record ->
      match record.Status with
      | CapabilityStatus.Revoked at -> Error(PresentRefusal.Revoked at)
      | CapabilityStatus.Active ->
        if now >= record.Grant.NotAfter then Error(PresentRefusal.Expired record.Grant.NotAfter)
        elif now - record.LastSeen >= Cohort.leaseWindow then Error(PresentRefusal.LeaseLapsed record.LastSeen)
        else Ok record

  /// A mint is allowed only for the conductor, only for a new hash, only into
  /// the future and no further than the maximum lifetime, and only for a grant
  /// that widens nothing the minter holds. `Ok` names who is minting.
  let private checkMint (now: DateTime) (state: CapabilityState) (minter: Minter) (requested: Grant) (hash: TokenHash) : Result<MemberId, MintRefusal> =
    match minter.Authority with
    | Authority.Member _
    | Authority.Anonymous -> Error MintRefusal.NotConductor
    | Authority.Conductor by ->
      if Map.containsKey hash state.Records then Error MintRefusal.DuplicateToken
      elif requested.NotAfter <= now then Error(MintRefusal.NotInTheFuture requested.NotAfter)
      elif requested.Route = RouteBinding.Unbound then Error MintRefusal.UnboundNotMintable
      else
        match Grant.wideningsOf requested minter.Grant with
        | _ :: _ as widenings -> Error(MintRefusal.WouldWiden widenings)
        | [] ->
          let lifetime = requested.NotAfter - now
          if lifetime > Timeouts.capabilityMaxLifetime then
            Error(MintRefusal.LifetimeTooLong(lifetime, Timeouts.capabilityMaxLifetime))
          else Ok by

  let decide (now: DateTime) (state: CapabilityState) (command: CapabilityCommand)
      : Result<CapabilityState * CapabilityEvent list, CapabilityRefusal> =
    match command with
    | CapabilityCommand.Mint(minter, requested, hash) ->
      match checkMint now state minter requested hash with
      | Error refusal -> Error(CapabilityRefusal.Mint refusal)
      | Ok by ->
        let id = CapabilityId.ofHash hash
        let record =
          { Id = id
            MintedBy = by
            MintedAt = now
            LastSeen = now
            Grant = requested
            Status = CapabilityStatus.Active }
        Ok({ state with Records = Map.add hash record state.Records }, [ CapabilityEvent.Minted(id, requested) ])
    | CapabilityCommand.Touch hash ->
      match resolve now state hash with
      | Ok record ->
        let touched = { record with LastSeen = now }
        Ok({ state with Records = Map.add hash touched state.Records }, [ CapabilityEvent.Touched record.Id ])
      // A token that does not resolve is not kept alive: Touch never revives a lapsed, expired or revoked one.
      | Error _ -> Ok(state, [])
    | CapabilityCommand.Revoke(by, target) ->
      match by with
      | Authority.Conductor _ ->
        match state.Records |> Map.tryFindKey (fun _ r -> r.Id = target) with
        | None -> Error(CapabilityRefusal.Revoke(RevokeRefusal.UnknownCapability target))
        | Some hash ->
          let record = state.Records.[hash]
          match record.Status with
          | CapabilityStatus.Revoked at -> Error(CapabilityRefusal.Revoke(RevokeRefusal.AlreadyRevoked at))
          | CapabilityStatus.Active ->
            let revoked = { record with Status = CapabilityStatus.Revoked now }
            Ok({ state with Records = Map.add hash revoked state.Records }, [ CapabilityEvent.Revoked target ])
      | Authority.Member _
      | Authority.Anonymous -> Error(CapabilityRefusal.Revoke RevokeRefusal.NotConductor)

  // ── What a grant lets a call do ───────────────────────────────────────────

  [<RequireQualifiedAccess>]
  type ToolRefusal =
    | NotInRole of tool: string * toolClass: ToolClass * preset: RolePreset
    | Unclassified of tool: string

  [<RequireQualifiedAccess>]
  type ScopeRefusal =
    | OutsideScope of requested: ClaimScope * granted: ScopePrefix
    | MalformedPath of PathRefusal

  /// May a call to `toolName` run under this grant? Fails closed: a tool with no class is refused.
  let admitTool (grant: Grant) (toolName: string) : Result<unit, ToolRefusal> =
    match ToolClass.ofTool toolName with
    | None -> Error(ToolRefusal.Unclassified toolName)
    | Some toolClass ->
      match Set.contains toolClass (RolePreset.toolClasses grant.Preset) with
      | true -> Ok()
      | false -> Error(ToolRefusal.NotInRole(toolName, toolClass, grant.Preset))

  /// May this grant claim `scope`? The scope is canonicalized first, so a `..` detour cannot step outside the prefix.
  let admitClaim (grant: Grant) (scope: ClaimScope) : Result<unit, ScopeRefusal> =
    match ClaimScope.tryCanonical scope with
    | Error refusal -> Error(ScopeRefusal.MalformedPath refusal)
    | Ok canonical ->
      match ScopePrefix.covers grant.Scope canonical with
      | true -> Ok()
      | false -> Error(ScopeRefusal.OutsideScope(canonical, grant.Scope))

  /// `tools/list` for a token: the registered tools it could call.
  let visibleTools (grant: Grant) (registered: string list) : string list =
    registered |> List.filter (fun tool -> Result.isOk (admitTool grant tool))

  // ── Identity policy: what a connection with no token is ───────────────────

  [<RequireQualifiedAccess>]
  type IdentityPolicy =
    /// A connection without a token is a plain member, as before tokens existed. The default.
    | ConnectionsAllowed
    /// A connection without a token may read status, and the conductor may act.
    /// Everyone else needs a token. Without this, the narrow tokens would sit
    /// beside an unrestricted door and the allow-list would be advice.
    | TokenRequired

  module IdentityPolicy =
    let all : IdentityPolicy list = [ IdentityPolicy.ConnectionsAllowed; IdentityPolicy.TokenRequired ]

    let toToken =
      function
      | IdentityPolicy.ConnectionsAllowed -> "ConnectionsAllowed"
      | IdentityPolicy.TokenRequired -> "TokenRequired"

    let tryParse (raw: string) : Result<IdentityPolicy, UnknownName> =
      let wanted = if isNull raw then "" else raw.Trim()
      match all |> List.tryFind (fun p -> String.Equals(toToken p, wanted, StringComparison.OrdinalIgnoreCase)) with
      | Some policy -> Ok policy
      | None -> Error { Given = wanted; Expected = all |> List.map toToken }

    /// Nothing existing breaks: a token-less connection stays a plain member.
    let defaultPolicy : IdentityPolicy = IdentityPolicy.ConnectionsAllowed

    /// The only tools a token-less caller may use under `TokenRequired` unless it is the conductor.
    let tokenlessReadable : string list = [ "get_cohort_status"; "get_daemon_status" ]

    /// The one other tool a token-less caller may use, and only while the cohort has no conductor: the
    /// first member to join is the conductor, and the conductor is who mints.
    let bootstrapTool : string = "join_cohort" 

  [<RequireQualifiedAccess>]
  type PolicyRefusal = TokenRequired of tool: string

  /// Whether the cohort has a conductor yet. Under `TokenRequired` the first joiner still becomes the
  /// conductor, because without one nobody can mint a token, so joining is admitted until a seat is bound.
  [<RequireQualifiedAccess>]
  type ConductorSeat =
    | NotBoundYet
    | Bound

  /// May a call that presented no token run? `authority` is the connection's own.
  let admitTokenless (policy: IdentityPolicy) (seat: ConductorSeat) (authority: Authority<MemberId>) (toolName: string) : Result<unit, PolicyRefusal> =
    match policy with
    | IdentityPolicy.ConnectionsAllowed -> Ok()
    | IdentityPolicy.TokenRequired ->
      match authority with
      | Authority.Conductor _ -> Ok()
      | Authority.Member _
      | Authority.Anonymous ->
        match seat, List.contains toolName IdentityPolicy.tokenlessReadable with
        | _, true -> Ok()
        | ConductorSeat.NotBoundYet, false when toolName = IdentityPolicy.bootstrapTool -> Ok()
        | ConductorSeat.NotBoundYet, false
        | ConductorSeat.Bound, false -> Error(PolicyRefusal.TokenRequired toolName)

  // ── Transport: how a token reaches the daemon ─────────────────────────────

  /// A token travels in an HTTP header or in MCP `_meta`, set by whatever sits
  /// between the model and the daemon, and never in a tool argument: an agent
  /// transcript stores every argument, and so would the bridge's warning log.
  /// The daemon hashes it at the edge, so the raw value goes no further than
  /// the request that carried it.
  module CapabilityTransport =
    /// The HTTP request header. Per connection.
    let headerName = "X-SageFs-Member-Token"
    /// The key in a request's `_meta`. Per call, so one connection can carry many members.
    let metaKey = "sagefs/memberToken"
    /// The environment variable `sagefs mcp` (the stdio bridge) reads and sends as `headerName`.
    let bridgeEnvVar = "SAGEFS_MEMBER_TOKEN"
    /// The environment variable that sets the daemon's `IdentityPolicy`.
    let policyEnvVar = "SAGEFS_IDENTITY_POLICY"
    /// The claim the daemon's HTTP edge puts the token's HASH in, for the MCP request filter to read.
    let hashClaimType = "urn:sagefs:member-token-hash"

    /// How a call presented a token. There is no case that carries a raw token.
    [<RequireQualifiedAccess>]
    type Presentation =
      | NoToken
      | Presented of TokenHash
      /// A token was presented but is not a token at all (blank, or absurdly long).
      | Unreadable of reason: string

    /// The longest a token may be. A real one is `Token.prefix` plus 43 characters; anything far past
    /// that is not a token, and is refused before it is hashed.
    let maxTokenLength = 512

    let private hasText (value: string) = not (String.IsNullOrWhiteSpace value)

    /// The header the stdio bridge sends, when its environment names a token.
    let headerFromEnvironment (getEnv: string -> string) : (string * string) option =
      match getEnv bridgeEnvVar with
      | value when hasText value -> Some(headerName, value.Trim())
      | _ -> None

    /// Which token a call presents. `_meta` is per call and wins over the per-connection header, so
    /// a gateway holding one connection can speak for many members. A blank or absurd value is a
    /// token that cannot be one, not the absence of a token: it must be refused, never ignored.
    let presentationOf (headerHash: string option) (metaToken: string option) : Presentation =
      match metaToken, headerHash with
      | Some meta, _ ->
        match hasText meta, meta.Length <= maxTokenLength with
        | true, true -> Presentation.Presented(TokenHash.ofToken (meta.Trim()))
        | false, _ -> Presentation.Unreadable "the token in _meta is blank"
        | true, false -> Presentation.Unreadable "the token in _meta is far longer than any token"
      | None, Some hex ->
        match TokenHash.tryOfHex hex with
        | Some hash -> Presentation.Presented hash
        | None -> Presentation.Unreadable "the token header could not be read"
      | None, None -> Presentation.NoToken

    /// The daemon's identity policy for `getEnv`. Unset is the default, so nothing existing changes.
    /// Set but unreadable fails closed: a typo must not open the door the setting was meant to shut.
    let policyFromEnvironment (getEnv: string -> string) : IdentityPolicy =
      match getEnv policyEnvVar with
      | value when hasText value ->
        match IdentityPolicy.tryParse value with
        | Ok policy -> policy
        | Error _ -> IdentityPolicy.TokenRequired
      | _ -> IdentityPolicy.defaultPolicy
