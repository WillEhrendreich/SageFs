namespace SageFs

open SageFs.Affordances
open SageFs.MemberTable

// ── Who may do a harmful thing: three lists, one decision each ───────────────────────────────────────────
//
// This comment is the one place that says how the three fit together.
//
//   `Affordances.destructiveTools`    Information. Published on `tools/list` as MCP's `destructiveHint`, so a client
//                                     knows what it is holding and may keep a prompt of its own. It decides nothing here.
//   `Affordances.confirmationLosses`  The one prompt. A tool in it asks a person, naming what it takes away. Only
//                                     `manage_local_data` is in it, because it acts on no session and an agent has no
//                                     reason to call it mid-task (`ConfirmationPolicy.decide`).
//   `SessionAccess` (this file)       The one refusal. The five calls that stop or restart a session (`SessionAction`)
//                                     are refused to a bound member that did not make the session, unless it is the
//                                     conductor of the cohort the session sits in or the operator. Nobody is asked.
//
// A tool that is in `destructiveTools` and in neither of the other two is only labelled. `SessionAccessTests` holds
// every `SessionAction`'s tool to `destructiveTools`, so the refusal never guards a tool the client was not told about.

/// Who is asking to act on a session, as far as one decision needs to know.
[<RequireQualifiedAccess>]
type SessionCaller =
  /// The daemon's operator credential is on the request.
  | Operator
  /// The conductor of the cohort the session sits in.
  | Conductor of MemberId
  /// A bound member (a token, a client key or a connection) that holds no seat of authority over the session.
  | Member of MemberId
  /// The call is on no bound identity: the dashboard, an editor over HTTP, the CLI, a test.
  | Unbound

module SessionCaller =
  /// Pure: who is calling, from whether the operator credential is on the request, the identity the call is bound to
  /// (None when it is bound to none), and that identity's authority in the cohort the session sits in. Only the
  /// conductor's own id makes a caller the conductor: a bound member that is not in the cohort is an ordinary member.
  let resolve (operator: bool) (bound: MemberId option) (authority: Cohort.Authority<MemberId>) : SessionCaller =
    match operator, bound with
    | true, _ -> SessionCaller.Operator
    | false, None -> SessionCaller.Unbound
    | false, Some who ->
      match authority with
      | Cohort.Authority.Conductor conductor when conductor = who -> SessionCaller.Conductor who
      | Cohort.Authority.Conductor _
      | Cohort.Authority.Member _
      | Cohort.Authority.Anonymous -> SessionCaller.Member who

/// Who made a session, as far as the daemon remembers. The record lives in memory, so a session made from the dashboard
/// or the CLI has none, and neither has one a restarted daemon resumed.
[<RequireQualifiedAccess>]
type SessionMaker =
  | MadeBy of MemberId
  | NoRecord

/// Why a call may act, so a test (and a log line) can tell the reasons apart.
[<RequireQualifiedAccess>]
type AccessReason =
  | ByOperator
  | ByConductor
  | ByMaker
  /// The daemon has no record of a maker, so no other member's session is at stake.
  | NoMakerOnRecord
  /// The call is on no bound identity, so there is no member to compare.
  | NotOnAConnection

[<RequireQualifiedAccess>]
type SessionAccess =
  | Allowed of AccessReason
  /// A bound member that is not the maker, the conductor or the operator. Carries the maker, so the refusal can name it.
  | Refused of maker: MemberId

/// The calls the rule guards: the ones that stop or restart a session someone else may be working in.
[<RequireQualifiedAccess>]
type SessionAction =
  | Stop
  | Reset
  | HardReset
  | SwitchWorkflow
  | StopApp

module SessionAction =
  let all : SessionAction list =
    [ SessionAction.Stop; SessionAction.Reset; SessionAction.HardReset; SessionAction.SwitchWorkflow; SessionAction.StopApp ]

  /// The MCP tool each action is. One exhaustive function, so a new action cannot be added without naming its tool.
  let toolName (action: SessionAction) : ToolName =
    match action with
    | SessionAction.Stop -> ToolName.StopSession
    | SessionAction.Reset -> ToolName.ResetFsiSession
    | SessionAction.HardReset -> ToolName.HardResetFsiSession
    | SessionAction.SwitchWorkflow -> ToolName.SwitchWorkflow
    | SessionAction.StopApp -> ToolName.StopApp

  /// The guarded action a tool is, or None for every other tool.
  let ofTool (tool: ToolName) : SessionAction option = all |> List.tryFind (fun action -> toolName action = tool)

  /// What the caller was trying to do, in a verb phrase that ends in the session.
  let verb (action: SessionAction) : string =
    match action with
    | SessionAction.Stop -> "stop"
    | SessionAction.Reset -> "reset"
    | SessionAction.HardReset -> "hard-reset"
    | SessionAction.SwitchWorkflow -> "switch the workflow of"
    | SessionAction.StopApp -> "stop the app of"

module SessionAccess =
  /// The status a refused HTTP call answers with: the caller is known and not allowed.
  [<Literal>]
  let ForbiddenStatus = 403

  /// The rule, in one place. The operator may always act, and so may a call on no bound identity (the dashboard and the
  /// CLI have no member to compare). A session with no maker on record is nobody else's. Past those, the conductor of
  /// the cohort and the maker may act, and a different bound member may not.
  let decide (maker: SessionMaker) (caller: SessionCaller) : SessionAccess =
    match caller, maker with
    | SessionCaller.Operator, _ -> SessionAccess.Allowed AccessReason.ByOperator
    | SessionCaller.Unbound, _ -> SessionAccess.Allowed AccessReason.NotOnAConnection
    | _, SessionMaker.NoRecord -> SessionAccess.Allowed AccessReason.NoMakerOnRecord
    | SessionCaller.Conductor _, SessionMaker.MadeBy _ -> SessionAccess.Allowed AccessReason.ByConductor
    | SessionCaller.Member who, SessionMaker.MadeBy owner when who = owner -> SessionAccess.Allowed AccessReason.ByMaker
    | SessionCaller.Member _, SessionMaker.MadeBy owner -> SessionAccess.Refused owner

  /// What a refusal says happened, and what the caller can do next: the three people who may act, and a session of the
  /// caller's own. It is the same error `RouteGate` and `ToolAuthorityGate` refuse with (an authority refusal: a reason
  /// and a next action), so every client that reads those reads this one.
  let refusal (action: SessionAction) (sessionId: string) (maker: MemberId) : SageFsError =
    let reason =
      sprintf
        "session '%s' was made by another member (%s). Only its maker, the conductor of its cohort or the operator may %s it."
        sessionId
        (MemberId.display maker)
        (SessionAction.verb action)
    let next =
      sprintf
        "Ask the maker (%s) to do it, ask the cohort conductor to, or have the operator call it with the operator credential (SAGEFS_OPERATOR_TOKEN). To work on your own, create a session with create_project_session and use that one."
        (MemberId.display maker)
    SageFsError.CohortActionFailed(reason, next)
