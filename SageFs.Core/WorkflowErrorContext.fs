namespace SageFs

/// Workflow-aware error enhancement — adds context when Live mode restricts the REPL.
///
/// Follows the same pure-function composition pattern as ErrorMessages.fs:
///   detect pattern → compose enhancement → return enhanced suggestion.
///
/// WHY: When a user in Live mode tries to redefine a type, FSI says
///   "FS0037: Duplicate definition of type 'Foo'" — which is cryptic.
///   This module intercepts that specific pattern and explains the actual cause:
///   single-assembly FSI mode (required for Harmony patching) prevents type redefinition.
///
/// DESIGN RULE: Enhancement ADDS context, never removes existing guidance.
///   The original suggestion is always preserved in the output.
module WorkflowErrorContext =

  /// Detect whether an error is a type redefinition error.
  /// These only become workflow-relevant in HotReload (single-assembly) mode.
  let isTypeRedefinitionError (errorText: string) =
    errorText.Contains("Duplicate definition of type")
    || errorText.Contains("FS0037")
    || errorText.Contains("has been defined")

  /// Enhance an error suggestion with workflow context.
  ///
  /// - In Interactive mode: identity — REPL has no restrictions, never inject misleading hints.
  /// - In HotReload mode + type redef error: append switch hint explaining why and how to fix.
  /// - All other combinations: identity — don't add noise.
  let enhance
    (workflow: WorkflowTypes.SessionWorkflow)
    (errorText: string)
    (suggestion: string) =
    match WorkflowTypes.SessionWorkflow.replCapability workflow with
    | WorkflowTypes.ReplCapability.ExpressionOnly when isTypeRedefinitionError errorText ->
      // WHY the wording is specific: this is the last user-facing reference to the
      // deprecated TUI, and it named a keystroke (Ctrl+W) in a client that no longer
      // ships — so the one actionable half of the hint pointed at nothing. It also
      // said "Live mode", which is the old name; the workflow is HotReload, and
      // "live" is an alias trap that means Hot Reload rather than live testing.
      // Only the two clients that can actually perform the switch are named: the
      // dashboard has no switch route yet and Neovim's :SageFsWorkflow is
      // status-only, so promising either would be a remedy the user cannot follow.
      sprintf
        "%s\n\n🔄 Type redefinition is not available in the Hot Reload workflow (single-assembly FSI).\n   Switch to the REPL workflow for full type redefinition: the switch_workflow MCP tool, or 'SageFs: Switch Workflow' in VS Code."
        suggestion
    | _ -> suggestion
