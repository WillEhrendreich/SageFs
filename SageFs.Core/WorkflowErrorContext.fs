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

  /// The places a user can switch a session's workflow from today. A closed set, so the hint below is built from
  /// it and cannot name a client that cannot switch, or leave one out.
  /// Neovim is not here: `:SageFsWorkflow` (sagefs.nvim, `lua/sagefs/commands.lua`) takes no argument and only
  /// shows the current workflow.
  [<RequireQualifiedAccess>]
  type SwitchSurface =
    /// `switch_workflow`, which creates a new session in the target workflow and stops the old one.
    | McpTool
    /// The workflow dropdown (`#workflow-switcher`), which posts `/dashboard/switch-workflow`; the daemon then calls
    /// `POST /api/sessions/{sid}/workflow` and the same session id restarts in the target workflow.
    | WebDashboard
    /// `SageFs: Switch Workflow`, which posts `/api/sessions/{sid}/workflow` directly.
    | VsCode

  module SwitchSurface =
    let all : SwitchSurface list = [ SwitchSurface.McpTool; SwitchSurface.WebDashboard; SwitchSurface.VsCode ]

    /// How the hint says to use it.
    let describe (surface: SwitchSurface) : string =
      match surface with
      | SwitchSurface.McpTool -> "the switch_workflow MCP tool"
      | SwitchSurface.WebDashboard -> "the workflow dropdown in the web dashboard"
      | SwitchSurface.VsCode -> "'SageFs: Switch Workflow' in VS Code"

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
      // Only the clients that can actually perform the switch are named, and they come
      // from `SwitchSurface.all`: the MCP tool, the dashboard's workflow dropdown and
      // VS Code. Neovim's :SageFsWorkflow is status-only, so promising it would be a
      // remedy the user cannot follow.
      let surfaces = SwitchSurface.all |> List.map SwitchSurface.describe
      let switchWith =
        match List.rev surfaces with
        | [] -> ""
        | [ only ] -> only
        | last :: restReversed -> sprintf "%s, or %s" (String.concat ", " (List.rev restReversed)) last
      sprintf
        "%s\n\n🔄 Type redefinition is not available in the Hot Reload workflow (single-assembly FSI).\n   Switch to the REPL workflow for full type redefinition: %s."
        suggestion
        switchWith
    | _ -> suggestion
