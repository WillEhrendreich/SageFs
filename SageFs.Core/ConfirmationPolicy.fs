/// Whether a confirm-tier tool still has to ask a person.
///
/// A confirmation exists to stop an agent from throwing away work a person cares about. A session the
/// calling agent made itself is not that: nobody else put anything in it. Every agent in this repo hit the
/// one-minute timeout on `hard_reset_fsi_session` and `stop_session` for a session it had created seconds
/// earlier, and then went around the prompt by HTTP, so the prompt cost time and protected nothing.
///
/// The decision is a pure function of four facts the daemon already has: the tool, the session the call
/// targets, who the daemon recorded as that session's creator, and the identity the call is bound to. It
/// fails closed. `NotRequired` is reachable on exactly one path (a tool that acts on one session, a session
/// the call names, a creator on record, and that creator being the caller), and every other input, however
/// odd, is `Required` with the reason it asks.
module SageFs.ConfirmationPolicy

open SageFs.Affordances
open SageFs.MemberTable

/// Who the daemon recorded as the maker of a session. The record is `McpTools.sessionOwners`, written when a
/// connection (or a member token, which outranks the connection) creates a session over MCP. A session a
/// person made, from the dashboard, the CLI or an editor, has no record.
[<RequireQualifiedAccess>]
type SessionCreator =
  /// No record: a person made it, or the daemon no longer remembers (a restart drops the record).
  | Unrecorded
  /// An MCP connection or a member token made it.
  | Recorded of MemberId

module SessionCreator =
  /// Read a recorded key back into the identity it was written from. `MemberId.display` is the written form,
  /// so this is its inverse; a key that carries none of the known prefixes is a `Minted` name, which `display`
  /// writes verbatim.
  let ofRecordedKey (key: string) : SessionCreator =
    match System.String.IsNullOrWhiteSpace key with
    | true -> SessionCreator.Unrecorded
    | false ->
      match key with
      | k when k.StartsWith("mcp:", System.StringComparison.Ordinal) ->
        SessionCreator.Recorded(MemberId.Mcp(k.Substring 4))
      | k when k.StartsWith("cap:", System.StringComparison.Ordinal) ->
        SessionCreator.Recorded(MemberId.Capability(k.Substring 4))
      | k when k.StartsWith("browser:", System.StringComparison.Ordinal) ->
        SessionCreator.Recorded(MemberId.Browser(k.Substring 8))
      | k -> SessionCreator.Recorded(MemberId.Minted k)

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
    | ConfirmationReason.CallerNotBound -> "the call is not bound to an MCP connection or a member token"

/// The decision. `caller` is the identity the call is bound to (a member token outranks the connection), not
/// the `agentName` the call passed.
let decide (tool: ToolName) (target: TargetSession) (creator: SessionCreator) (caller: MemberId) : Confirmation =
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
      | MemberId.Capability _ ->
        match creator with
        | SessionCreator.Unrecorded -> Confirmation.Required ConfirmationReason.CreatorNotRecorded
        | SessionCreator.Recorded maker when maker = caller ->
          Confirmation.NotRequired(OwnSession.CreatedByThisCaller sessionId)
        | SessionCreator.Recorded _ -> Confirmation.Required ConfirmationReason.CreatedBySomeoneElse
