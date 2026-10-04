namespace SageFs

open System.Threading.Tasks
open SageFs.Capability
open SageFs.WorkerProtocol

/// WHERE A MEMBER TOKEN MAY ROUTE: the gate step, and the registry as a bound caller sees it.
///
/// WHY THIS IS ITS OWN FILE. `Mcp.fs` carries a line budget and the decision itself is pure and
/// lives in `Capability.fs` (`Route.admit`). This is the edge: it reads the registry facts the
/// decision needs, and it narrows what a bound caller can see of the registry at all.
///
/// TWO LAYERS, because either alone has a hole.
///
///   1. THE GATE STEP (`admit`) runs once per call in `admitToolCallWithinStore`, ahead of every tool
///      body, and refuses with a NAMED reason and a next action: this token is bound to session X.
///      It reads only the two parameters that route (`session_id`, `working_directory`), so a tool
///      that routes some other way, or a body that resolves differently from the gate, escapes it.
///   2. THE CONFINEMENT (`confine`) wraps `SessionManagementOps`, the one door a tool body uses to
///      reach any session. For a bound caller a session outside the binding is simply absent:
///      `GetAllSessions` omits it, `GetSessionInfo` and `GetProxy` answer `None`, stop, restart and
///      the app-run verbs refuse. So the resolver's fallbacks (the one-session fallback, a stale
///      active-session pointer, a stale-id re-bind by directory) can only ever land on a session the
///      token is bound to, and a tool added tomorrow that reads the registry is confined without
///      being listed anywhere.
///
/// The caller is whoever `currentCapability` says it is, read at the moment of the call. It is the
/// same value `memberIdFor` reads, so the identity a call acts as and the binding it is held to
/// cannot come from two places.
///
/// WHAT THIS DOES NOT STOP. See `Capability.fs`: it is policy for the tool surface, and an
/// `Implementer` token can run code. The daemon-wide reads (`get_daemon_status`, the friction
/// reports) show counts and lease holders for the whole daemon, and the HTTP API and the MCP
/// resources do not read the token at all.
[<RequireQualifiedAccess>]
module RouteGate =

  /// The binding a call is held to: its token's, or `Unbound` for a connection with no token (the
  /// conductor on its own connection, a plain member).
  let routeOf (capability: ResolvedCapability option) : RouteBinding =
    match capability with
    | Some resolved -> resolved.Grant.Route
    | None -> RouteBinding.Unbound

  /// Whether `directory` is inside the checkout `root`: the same path, or under it, and not under a
  /// git worktree nested in it (a worktree is its own checkout, so a token bound to the main
  /// checkout does not reach it). Both are compared as the session registry compares them.
  let directoryWithin (hasCheckoutMarker: string -> bool) (root: string) (directory: string) : bool =
    let rootNormal = McpSessionRouting.normalizePath root
    let directoryNormal = McpSessionRouting.normalizePath directory
    let underRoot =
      directoryNormal = rootNormal
      || directoryNormal.StartsWith(rootNormal + "/", System.StringComparison.Ordinal)
      || directoryNormal.StartsWith(rootNormal + "\\", System.StringComparison.Ordinal)
    underRoot && not (McpSessionRouting.crossesCheckoutBoundaryWith hasCheckoutMarker rootNormal directoryNormal)

  /// `directoryWithin`, with the disk as the source of truth for what a checkout is.
  let productionWithin : string -> string -> bool = directoryWithin Checkout.hasCheckoutMarker

  let private sessionIdOf (text: string) : SessionId option =
    match SessionId.validate text with
    | Ok id -> Some id
    | Error _ -> None

  /// The working directory of the session `id` names, when the registry (as the caller sees it) serves it.
  let private directoryOfSession (ops: SessionManagementOps) (id: string) : Task<string option> =
    task {
      match sessionIdOf id with
      | None -> return None
      | Some sid ->
        let! info = ops.GetSessionInfo sid
        return info |> Option.map (fun session -> session.WorkingDirectory)
    }

  /// THE GATE STEP: may this call, from a caller bound by `binding`, go ahead? `Ok` for `Unbound` with no
  /// reads at all. For a bound caller it reads the registry facts the pure decision needs and refuses with
  /// the rule and the next action. `ops` is the context's `SessionOps`, which is already the confined view
  /// in the daemon, so a session outside the binding is absent here too and is refused, not described.
  let admit
    (ops: SessionManagementOps)
    (within: string -> string -> bool)
    (binding: RouteBinding)
    (toolName: string)
    (sessionId: string option)
    (workingDirectory: string option)
    : Task<Result<unit, string>> =
    task {
      match binding with
      | RouteBinding.Unbound -> return Ok()
      | RouteBinding.BoundToSession _
      | RouteBinding.BoundToCheckout _ ->
        let! named =
          match sessionId with
          | Some id -> directoryOfSession ops id
          | None -> Task.FromResult None
        let! boundDirectory =
          match binding with
          | RouteBinding.BoundToSession id -> directoryOfSession ops id
          | RouteBinding.BoundToCheckout _
          | RouteBinding.Unbound -> Task.FromResult None
        let facts : RouteFacts = { NamedSessionDirectory = named; BoundSessionDirectory = boundDirectory; Within = within }
        let call : RouteCall = { Tool = toolName; SessionId = sessionId; WorkingDirectory = workingDirectory }
        return
          Route.admit binding call facts
          |> Result.mapError (CohortErrorMapping.routeRefusalToSageFsError >> SageFsError.describeForAgent)
    }

  /// MCP RESOURCES. `resources/read` names a URI and no tool, and a resource takes no `working_directory`, so it
  /// cannot be held to a route by the arguments the gate reads. A bound caller may read a resource only when
  /// it is on `confined`, the list of resources that read the registry through the confined `SessionOps`
  /// (`sessions://list`). Every other one, including a resource added tomorrow, is refused to it: the
  /// cohort resource shows the DAEMON's own repository, which is outside the binding.
  let resourceAdmitted (confined: string list) (binding: RouteBinding) (uri: string) : Result<unit, string> =
    match binding with
    | RouteBinding.Unbound -> Ok()
    | RouteBinding.BoundToSession _
    | RouteBinding.BoundToCheckout _ ->
      match List.contains uri confined with
      | true -> Ok()
      | false ->
        Result.Error(
          SageFsError.describeForAgent (
            SageFsError.CohortActionFailed(
              sprintf "This member token is bound to %s, and the resource %s is not cut to a route: it shows the daemon's own repository." (RouteBinding.describe binding) uri,
              sprintf "Use the tool that takes a working_directory (get_cohort_status with the directory of your route), or read %s, which shows only the sessions inside your route." (String.concat ", " confined))))

  /// The registry as the caller sees it. `route` is read at the moment of EACH call, so one wrapped
  /// `SessionManagementOps` serves every caller of the daemon and each is held to its own binding.
  let confine (route: unit -> RouteBinding) (within: string -> string -> bool) (ops: SessionManagementOps) : SessionManagementOps =
    let permits (binding: RouteBinding) (info: SessionInfo) : bool =
      Route.permitsSession binding within (SessionId.value info.Id) info.WorkingDirectory

    /// Whether the session `sid` is one the caller may see. A session with no record is not.
    let visible (sid: SessionId) : Task<bool> =
      match route () with
      | RouteBinding.Unbound -> Task.FromResult true
      | RouteBinding.BoundToSession bound -> Task.FromResult(SessionId.value sid = bound)
      | RouteBinding.BoundToCheckout _ as binding ->
        task {
          let! info = ops.GetSessionInfo sid
          return info |> Option.exists (permits binding)
        }

    /// Run `allowed` for a visible session, or answer `denied` without asking the registry.
    let guarded (sid: SessionId) (allowed: unit -> Task<'a>) (denied: 'a) : Task<'a> =
      task {
        let! ok = visible sid
        match ok with
        | true -> return! allowed ()
        | false -> return denied
      }

    let guardedText (id: string) (allowed: SessionId -> Task<Result<string, SageFsError>>) : Task<Result<string, SageFsError>> =
      match sessionIdOf id with
      | None -> Task.FromResult(Result.Error(SageFsError.SessionNotFound id))
      | Some sid -> guarded sid (fun () -> allowed sid) (Result.Error(SageFsError.SessionNotFound id))

    let notFound (sid: SessionId) = SageFsError.SessionNotFound(SessionId.value sid)

    let allSessions () : Task<SessionInfo list> =
      task {
        let! sessions = ops.GetAllSessions()
        match route () with
        | RouteBinding.Unbound -> return sessions
        | binding -> return sessions |> List.filter (permits binding)
      }

    { ops with
        CreateSession =
          fun targets workingDirectory workflow ->
            match route () with
            | RouteBinding.Unbound -> ops.CreateSession targets workingDirectory workflow
            | RouteBinding.BoundToSession _ as binding ->
              Task.FromResult(Result.Error(CohortErrorMapping.routeRefusalToSageFsError (RouteRefusal.CannotCreateSessions binding)))
            | RouteBinding.BoundToCheckout root as binding ->
              let inside = CheckoutRoot.canonical workingDirectory |> Option.exists (within (CheckoutRoot.value root))
              match inside with
              | true -> ops.CreateSession targets workingDirectory workflow
              | false ->
                Task.FromResult(Result.Error(CohortErrorMapping.routeRefusalToSageFsError (RouteRefusal.DirectoryOutsideBinding(workingDirectory, binding))))
        ListSessions =
          fun () ->
            match route () with
            | RouteBinding.Unbound -> ops.ListSessions()
            | RouteBinding.BoundToSession _
            | RouteBinding.BoundToCheckout _ ->
              task {
                let! sessions = allSessions ()
                return McpSessionRouting.formatExistingSessionsHint sessions
              }
        StopSession = fun id -> guardedText id (fun _ -> ops.StopSession id)
        PurgeSession = fun id -> guardedText id (fun _ -> ops.PurgeSession id)
        RestartSession = fun sid plan -> guarded sid (fun () -> ops.RestartSession sid plan) (Result.Error(notFound sid))
        GetProxy = fun sid -> guarded sid (fun () -> ops.GetProxy sid) None
        GetSessionInfo =
          fun sid ->
            task {
              let! info = ops.GetSessionInfo sid
              match route () with
              | RouteBinding.Unbound -> return info
              | binding -> return info |> Option.filter (permits binding)
            }
        GetAllSessions = allSessions
        UpdateSessionStatus = fun sid status -> guarded sid (fun () -> ops.UpdateSessionStatus sid status) ()
        ClaimRun = fun sid project -> guarded sid (fun () -> ops.ClaimRun sid project) (Result.Error(notFound sid))
        ClaimStop = fun sid -> guarded sid (fun () -> ops.ClaimStop sid) (Result.Error(notFound sid))
        AdvanceRun =
          fun sid generation state -> guarded sid (fun () -> ops.AdvanceRun sid generation state) (AppRun.StepOutcome.Stale AppRun.AppRunState.NotRunning)
        EndAppRun =
          fun sid generation runId state -> guarded sid (fun () -> ops.EndAppRun sid generation runId state) AppRun.RunEnd.NotCurrent
        AwaitReady = fun sid patience -> guarded sid (fun () -> ops.AwaitReady sid patience) (Result.Error(notFound sid))
        SwitchWorkflow = fun id workflow -> guardedText id (fun _ -> ops.SwitchWorkflow id workflow)
        GetAdoptedCore = fun sid -> guarded sid (fun () -> ops.GetAdoptedCore sid) None
        GetWarmupProgress = fun sid -> guarded sid (fun () -> ops.GetWarmupProgress sid) None }
