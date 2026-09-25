namespace SageFs

/// Pure decision/formatting functions for the `create_session` MCP tool's
/// UX (sagefs-roast.md Findings #3/#6/#7) — workflow parsing, the exact-
/// duplicate-session rule, and the success reply text. Its own small module
/// (not more mass on `Mcp.fs`, which sits close to its own file-size
/// budget) so these stay independently unit-testable and `Mcp.fs`'s
/// `McpTools.createSession`/`switchWorkflow` can call them directly — it
/// compiles before `Mcp.fs` (see SageFs.fsproj compile order).
module CreateSessionUx =

  /// The single "unknown workflow" rejection message, shared by
  /// `switch_workflow` (which already rejected an unrecognized target) and
  /// `create_session` (which used to silently default to Interactive on a
  /// typo instead — Finding #6) so the two tools can never drift on
  /// wording for the same mistake.
  let formatUnknownWorkflowError (raw: string) : string =
    sprintf "Error: unknown workflow '%s'. Valid values: 'interactive' (REPL), 'livetesting' (Live Testing), 'hotreload' (Hot Reload)" raw

  /// Parse `create_session`'s `workflow` argument, REJECTING an
  /// unrecognized value instead of silently defaulting to Interactive.
  /// `switch_workflow` already rejects a typo loudly via `tryOfString`
  /// (`SessionWorkflow.ofString` — what `create_session` used before this —
  /// exists specifically to default unrecognized input, so a typo like
  /// "hotreoad" silently became a plain REPL and the agent believed it got
  /// hot reload; Finding #6). A BLANK value means "not specified" and
  /// legitimately defaults to Interactive — that is the documented default
  /// for an omitted argument, not a typo, and `tryOfString ""` already
  /// returns `None` so it must be special-cased here rather than rejected.
  let parseCreateSessionWorkflow (raw: string) : Result<WorkflowTypes.SessionWorkflow, string> =
    match System.String.IsNullOrWhiteSpace raw with
    | true -> Ok WorkflowTypes.SessionWorkflow.Interactive
    | false ->
      match WorkflowTypes.SessionWorkflow.tryOfString raw with
      | Some w -> Ok w
      | None -> Error (formatUnknownWorkflowError raw)

  /// Whether `(requestedProjects, requestedWorkingDir)` would be treated as
  /// a duplicate of an already-registered session by the single owner
  /// (`SessionManager.ManagerState.tryFindDuplicate`): the SAME working
  /// directory (ordinal, case-insensitive) and the SAME project list once
  /// each side is sorted — compared as the RAW strings the manager stores,
  /// with no path normalization, because that is exactly what the manager
  /// compares. Before this, the MCP-side pre-check warned (and refused to
  /// even attempt creation) on ANY project overlap between the request and
  /// an existing session — so `[A;B]` against an existing `[A]`-only
  /// session was blocked here even though the manager would have created it
  /// without complaint (Finding #7: two disagreeing rules). This makes the
  /// MCP pre-check agree with the authority instead of inventing a third,
  /// stricter rule.
  let isExactDuplicateSession
    (requestedProjects: string list) (requestedWorkingDir: string)
    (existingProjects: string list) (existingWorkingDir: string)
    : bool =
    System.String.Equals(existingWorkingDir, requestedWorkingDir, System.StringComparison.OrdinalIgnoreCase)
    && List.sort existingProjects = List.sort requestedProjects

  let tryCreateTarget (paths: string list) : Result<SessionProjectTarget list, string> =
    SessionProjectTarget.tryCreateMany paths

  /// The `create_session` success reply. At CREATION time the worker has not
  /// finished resolving projects yet, so this states exactly what was requested
  /// and points at get_fsi_status to confirm what actually loaded.
  let formatCreateSessionReply
    (sid: string)
    (targets: SessionProjectTarget list)
    (detectionHint: string option) : string =
    let requestedLine = sprintf "Requested target: %s" (SessionProjectTarget.describe targets)
    let hintLine = detectionHint |> Option.map (sprintf "\n\n%s") |> Option.defaultValue ""
    sprintf "%s\n%s\nSession is warming up (typically 15-30s). Call get_fsi_status once it reports State='Ready' to confirm what actually loaded — the 'Loaded:' field there reflects the worker's own resolution.%s"
      sid requestedLine hintLine
