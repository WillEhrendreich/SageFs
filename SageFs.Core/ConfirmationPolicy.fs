/// Whether a confirm-tier tool still has to ask a person.
///
/// A confirmation exists to stop an agent from throwing away work a person cares about. A session the
/// calling agent made itself, and that nobody else has worked in, is not that. Every agent in this repo hit the
/// one-minute timeout on `hard_reset_fsi_session` and `stop_session` for a session it had created seconds
/// earlier, and then went around the prompt by HTTP, so the prompt cost time and protected nothing (#176).
///
/// The decision is a pure function of five facts the daemon already has: the tool, the session the call
/// targets, who the daemon recorded as that session's creator, who else has been in it (`SessionActivity`),
/// and the identity the call is bound to. It fails closed. `NotRequired` is reachable on exactly one path (a
/// tool that acts on one session, a session the call names, a creator on record, that creator being the
/// caller, nobody else having run code in it, and no other agent in it now), and every other input, however
/// odd, is `Required` with the reason it asks.
module SageFs.ConfirmationPolicy

open SageFs.Affordances
open SageFs.MemberTable

/// Who the daemon recorded as the maker of a session. The record is `SessionOwners`, written when a
/// connection (or a member token, which outranks the connection) creates a session over MCP. It holds the
/// `MemberId` itself, so the decision compares identities and never the text `MemberId.display` prints. A
/// session a person made, from the dashboard, the CLI or an editor, has no record.
[<RequireQualifiedAccess>]
type SessionCreator =
  /// No record: a person made it, or the daemon no longer remembers (a restart drops the record).
  | Unrecorded
  /// An MCP connection or a member token made it.
  | Recorded of MemberId

/// Whose a session is to stop, as far as the daemon remembers (#184). Only `RecordedToAnother` is a refusal: a session with no
/// record (made from the dashboard or the CLI, or by a connection of a daemon that has since restarted, which empties the
/// record) has no other owner to wait for, and the confirmation gate still asks a person about it (`SessionCreator.Unrecorded`).
[<RequireQualifiedAccess>]
type SessionOwnership =
  /// The call is not on a bound MCP connection, so there is no connection to compare.
  | NotOnAConnection
  | RecordedToCaller
  | RecordedToAnother
  | NoRecord

module SessionOwnership =
  /// Pure: how a session stands to the caller, from whether the call is on a bound connection, who the daemon recorded as its
  /// maker, and who is calling.
  let decide (onAConnection: bool) (recorded: MemberId option) (caller: MemberId) : SessionOwnership =
    match onAConnection, recorded with
    | false, _ -> SessionOwnership.NotOnAConnection
    | true, None -> SessionOwnership.NoRecord
    | true, Some owner when owner = caller -> SessionOwnership.RecordedToCaller
    | true, Some _ -> SessionOwnership.RecordedToAnother

/// Where code that ran in a session came from. Making a session is not the only way to have work in it: a person can
/// type into the dashboard's REPL box or send a block from an editor into a session an agent made, and a reset or a
/// stop throws that away too.
[<RequireQualifiedAccess>]
type EvalSource =
  /// An MCP call, or an HTTP `/exec` call (VS Code, Neovim, Jupyter), under the identity the call is bound to
  /// (`McpTools.memberIdFor`). An unbound `/exec` caller is `MemberId.Minted`, never a connection or a token.
  | Member of MemberId
  /// The dashboard's REPL box, from any tab. A tab is an Observer in the occupancy table even while it runs code, so
  /// this is the only record that a person worked there.
  | Dashboard
  /// The deprecated TUI's editor buffer, submitted through `POST /api/dispatch` (`EditorEffect.RequestEval`). That
  /// route names no caller, so it counts as someone other than any agent.
  | Tui

/// Who is working in a session now, as the occupancy table has it.
[<RequireQualifiedAccess>]
type Presence =
  /// No Worker but the caller. A dashboard tab that only watches is an Observer and does not count; one that ran code
  /// is in `SessionActivity.RanCode`.
  | CallerOnly
  /// A Worker other than the caller is in the session.
  | AnotherAgentIsIn

/// Who else has been in a session besides the agent that made it: everyone who ran code in it, and whether another
/// agent is working in it now. Read only for a call the creator record already says is the caller's own.
type SessionActivity =
  { /// Every source that has run code in the session, for the life of the session (`SessionEvaluators`).
    RanCode: Set<EvalSource>
    Presence: Presence }

module SessionActivity =
  /// Nobody but the caller has been there.
  let quiet : SessionActivity = { RanCode = Set.empty; Presence = Presence.CallerOnly }

/// The session a call acts on, as the gate resolved it.
[<RequireQualifiedAccess>]
type TargetSession =
  /// The call names no session, or the one it names could not be found.
  | NoSession
  | Session of sessionId: string

/// Why a call still asks. One case per way the answer can be "ask", so a test can tell them apart and the
/// message can say which.
[<RequireQualifiedAccess>]
type ConfirmationReason =
  /// The tool acts on more than one session, or on something that is not a session (the local stores, the
  /// workspace, member tokens). Whose session it is does not matter.
  | ToolSpansMoreThanOneSession
  /// The call names no session, or the id does not resolve to one.
  | NoSessionToJudge
  /// The daemon has no record of who made the session: a person did, or the record is gone.
  | CreatorNotRecorded
  /// A different connection or member token made the session.
  | CreatedBySomeoneElse
  /// The caller made the session, but someone else has run code in it: another agent (which may since have moved to
  /// another session), the dashboard, an editor over `/exec`, or the TUI. Their definitions would go too.
  | SomeoneElseRanCode
  /// The caller made the session, but another agent is working in it now.
  | AnotherAgentIsWorkingInIt
  /// The call is not bound to an MCP connection or a member token (a browser, or an unbound direct call), so
  /// "the same agent" has no meaning for it.
  | CallerNotBound

/// The one case where nobody is asked.
[<RequireQualifiedAccess>]
type OwnSession =
  /// The caller made this session itself.
  | CreatedByThisCaller of sessionId: string

[<RequireQualifiedAccess>]
type Confirmation =
  | Required of ConfirmationReason
  | NotRequired of OwnSession

module ConfirmationReason =
  /// One sentence per reason, for logs and the dashboard. Exhaustive: a new reason cannot be added without
  /// saying what it means.
  let describe (reason: ConfirmationReason) : string =
    match reason with
    | ConfirmationReason.ToolSpansMoreThanOneSession -> "the tool acts on more than one session, or on something that is not a session"
    | ConfirmationReason.NoSessionToJudge -> "the call names no session that can be found"
    | ConfirmationReason.CreatorNotRecorded -> "the daemon has no record of this agent creating the session"
    | ConfirmationReason.CreatedBySomeoneElse -> "a different connection or member token made the session"
    | ConfirmationReason.SomeoneElseRanCode -> "someone other than the agent that made the session has run code in it"
    | ConfirmationReason.AnotherAgentIsWorkingInIt -> "another agent is working in the session now"
    | ConfirmationReason.CallerNotBound -> "the call is not bound to an MCP connection or a member token"

/// Whether a call on a session the caller made can skip the prompt, given who else has been there. Only the
/// caller's own code and nobody else working in it now leaves nobody else to lose anything.
let private aloneIn (activity: SessionActivity) (caller: MemberId) (sessionId: string) : Confirmation =
  match activity.RanCode |> Set.forall (fun source -> source = EvalSource.Member caller), activity.Presence with
  | false, _ -> Confirmation.Required ConfirmationReason.SomeoneElseRanCode
  | true, Presence.AnotherAgentIsIn -> Confirmation.Required ConfirmationReason.AnotherAgentIsWorkingInIt
  | true, Presence.CallerOnly -> Confirmation.NotRequired(OwnSession.CreatedByThisCaller sessionId)

/// The decision. `caller` is the identity the call is bound to (a member token outranks the connection), not
/// the `agentName` the call passed. `activity` matters only once the session is known to be the caller's own.
let decide (tool: ToolName) (target: TargetSession) (creator: SessionCreator) (activity: SessionActivity) (caller: MemberId) : Confirmation =
  match Set.contains tool singleSessionConfirmTools with
  | false -> Confirmation.Required ConfirmationReason.ToolSpansMoreThanOneSession
  | true ->
    match target with
    | TargetSession.NoSession -> Confirmation.Required ConfirmationReason.NoSessionToJudge
    | TargetSession.Session sessionId when System.String.IsNullOrWhiteSpace sessionId ->
      Confirmation.Required ConfirmationReason.NoSessionToJudge
    | TargetSession.Session sessionId ->
      match caller with
      | MemberId.Browser _
      | MemberId.Minted _ -> Confirmation.Required ConfirmationReason.CallerNotBound
      | MemberId.Mcp _
      | MemberId.Capability _
      | MemberId.Operator ->
        match creator with
        | SessionCreator.Unrecorded -> Confirmation.Required ConfirmationReason.CreatorNotRecorded
        | SessionCreator.Recorded maker when maker = caller -> aloneIn activity caller sessionId
        | SessionCreator.Recorded _ -> Confirmation.Required ConfirmationReason.CreatedBySomeoneElse

/// Whether `decide` would read `activity` for these facts: only when every other fact already says the session is the
/// caller's own, since activity can only turn a skip into an ask. The gate gathers the activity (a scan of the
/// occupancy table) only when this is true, so an eval or a call on someone else's session never pays for it.
let readsActivity (tool: ToolName) (target: TargetSession) (creator: SessionCreator) (caller: MemberId) : bool =
  match decide tool target creator SessionActivity.quiet caller with
  | Confirmation.NotRequired _ -> true
  | Confirmation.Required _ -> false
