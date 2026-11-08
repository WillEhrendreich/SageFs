/// Whether a tool call still has to ask a person.
///
/// A confirmation exists to stop an agent from throwing away something a person cares about and cannot get back.
/// Stopping, resetting and switching a session, and stopping an app, are normal steps of everyday MCP use, so they
/// never ask, for any session, whoever made it and whoever has run code in it (Will, 2026-11-07). The only tool that
/// asks is `manage_local_data`, which clears the local stores SageFs keeps on this machine.
///
/// The decision is a pure function of the tool alone: `Affordances.confirmationLosses` holds the tier, and a tool in it
/// asks with the loss it names. Nothing about the session, its creator or its activity is read, because nothing that
/// asks acts on one session. The refusal of another member's session is `SessionAccess`, a separate decision with its own
/// file; `SessionAccess.fs` also says how this prompt, the refusal and `destructiveTools` fit together.
module SageFs.ConfirmationPolicy

open SageFs.Affordances

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
