/// How a structured server error is turned into something a person reads.
///
/// WHY — measured (sagefs-ux-roast.md §6.4): the daemon sends
/// `{case, message, suggestedAction}` on every structured error, and
/// `suggestedAction` is a SENTENCE of remedy ("Check the SageFs log for
/// details", "Run `dotnet build` on it, then hard_reset_fsi_session"). Two
/// sites got it exactly backwards in opposite directions:
///
///   * the session-error dialog passed `suggestedAction` as a BUTTON CAPTION —
///     VS Code truncates those — and pressing it appended the same sentence to
///     an output channel the user was not looking at. The remedy text was on
///     the wire, rendered on screen, and still never reached anyone.
///   * the app-run failure dialog did the mirror image: `message` in the modal
///     with NO buttons, and `suggestedAction` in a status-bar TOOLTIP. The
///     actionable half went to a hover; the dead end went to the modal.
///
/// One rule, borrowed from this repo's own best-designed user-facing type
/// (`SageFs.Core/Features/ReloadOutcome.fs:186-189`, `describeForUser`):
/// **what happened, then `→` the remedy, both in the message body.** Buttons
/// carry verbs.
///
/// No Fable dependency; tested under `dotnet fsi`
/// (tests/ErrorPresentationContractTests.fsx).
module SageFs.Vscode.ErrorPresentationPure

/// The server's structured error shape, mirroring `SageFsClient.HealthError`
/// (`/health`'s `error` object and the run-app/stop-app non-200 body) without
/// the Fable interop.
type StructuredError = {
  Case: string
  Message: string
  SuggestedAction: string
}

let private isBlank (s: string) = System.String.IsNullOrWhiteSpace s

/// The message body for a dialog, notification or tooltip.
///
/// Total, and never empty: a structured error with nothing usable in it still
/// produces a sentence, because an empty dialog is worse than a vague one.
let describe (e: StructuredError) : string =
  let headline =
    match isBlank e.Message, isBlank e.Case with
    | false, _ -> e.Message.Trim()
    | true, false -> sprintf "SageFs reported %s." (e.Case.Trim())
    | true, true -> "SageFs reported an error with no detail."
  match isBlank e.SuggestedAction with
  | true -> headline
  | false -> sprintf "%s\n→ %s" headline (e.SuggestedAction.Trim())

/// The accessible/status-bar one-liner. Same facts, no newline — a status bar
/// collapses them anyway, and a screen reader reads the arrow as noise.
let describeInline (e: StructuredError) : string =
  (describe e).Replace("\n→ ", " — ")

/// True when a string is prose rather than a control label — the test this
/// module exists to make possible. A remedy sentence is long and/or ends in a
/// full stop; a button caption is a short verb phrase. Used by the contract
/// test to pin that no dialog ever ships a remedy as a button again.
let looksLikeProseNotAButton (s: string) : bool =
  let t = (s |> Option.ofObj |> Option.defaultValue "").Trim()
  t.Length > 30 || t.EndsWith "." || t.Contains ". "
