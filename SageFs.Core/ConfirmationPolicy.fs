/// Whether a tool call still has to ask a person.
///
/// A confirmation exists to stop an agent from throwing away something a person cares about and cannot get back.
/// Stopping, resetting and switching a session, and stopping an app, are normal steps of everyday MCP use, so they
/// never ask, for any session, whoever made it and whoever has run code in it (Will, 2026-11-07). The only tool that
/// asks is `manage_local_data`, which clears the local stores SageFs keeps on this machine.
///
/// The decision is a pure function of the tool alone: `Affordances.confirmationLosses` holds the tier, and a tool in it
/// asks with the loss it names. Nothing about the session, its creator or its activity is read, because nothing that
/// asks acts on one session. This module also holds `SessionOwnership`, which is a refusal and not a prompt (#184).
module SageFs.ConfirmationPolicy

open SageFs.Affordances
open SageFs.MemberTable

/// Whose a session is to stop, as far as the daemon remembers (#184). Only `RecordedToAnother` is a refusal: a session with no
/// record (made from the dashboard or the CLI, or by a connection of a daemon that has since restarted, which empties the
/// record) has no other owner to wait for, and can be stopped from any connection.
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

/// What a call owes a person before its body runs.
[<RequireQualifiedAccess>]
type Confirmation =
  /// Nobody is asked. Every tool outside the confirm tier.
  | NotRequired
  /// A person is asked, and the prompt names `loss`, what the tool is about to take away.
  | Required of loss: string

/// The decision. A tool asks exactly when `Affordances.confirmationLosses` names it.
let decide (tool: ToolName) : Confirmation =
  match Map.tryFind tool confirmationLosses with
  | Some loss -> Confirmation.Required loss
  | None -> Confirmation.NotRequired
