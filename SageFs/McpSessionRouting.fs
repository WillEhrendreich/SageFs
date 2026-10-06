namespace SageFs

open System

/// Pure session working-directory routing/matching helpers, extracted from the
/// Mcp.fs accretion hub (roast-8 §2/§14 item 2). No `McpContext`, no IO — these
/// operate on a `SessionInfo list` plus path strings, so they are trivially
/// testable (see SessionIsolationTests) and shrink the MCP tool file. McpTools
/// re-exposes them by `open`ing this module, so its call sites are unchanged.
module McpSessionRouting =

  /// Normalize a path for comparison: trim trailing separators, lowercase on Windows.
  let normalizePath (p: string) =
    let trimmed = p.TrimEnd('/', '\\')
    match Environment.OSVersion.Platform = PlatformID.Win32NT with
    | true -> trimmed.Replace('/', '\\').ToLowerInvariant()
    | false -> trimmed

  /// Sessions whose registered WorkingDirectory matches `workingDir` exactly.
  /// Public because a McpTools caller outside the deep-match helpers uses it.
  let sessionsMatchingWorkingDir (sessions: WorkerProtocol.SessionInfo list) (workingDir: string) =
    let target = normalizePath workingDir
    sessions
    |> List.filter (fun s -> normalizePath s.WorkingDirectory = target)

  /// Parent segment of a path, tolerant of either separator style (tests and
  /// some callers use Windows-style paths regardless of host OS).
  let private parentSegment (path: string) : string option =
    match path.LastIndexOfAny([| '\\'; '/' |]) with
    | i when i > 0 -> Some(path.Substring(0, i))
    | _ -> None

  /// Pure: true when some directory between `root` (exclusive) and `target`
  /// (inclusive) carries its OWN checkout marker — `target` sits inside a
  /// NESTED checkout (a git worktree under `root`, e.g.
  /// `.claude/worktrees/agent-x`), so it is a routing boundary, not part of
  /// `root`'s session even though the path is textually nested under it.
  /// `hasCheckoutMarker` is injected so this stays pure/testable without disk
  /// I/O — mirrors SageFs.FileWatcher.isInNestedCheckout's semantics
  /// (root's own marker never counts) but is kept independent since Mcp.fs
  /// does not depend on FileWatcher.
  let crossesCheckoutBoundaryWith (hasCheckoutMarker: string -> bool) (root: string) (target: string) : bool =
    let rootNorm = normalizePath root
    let rec walk (dirOpt: string option) =
      match dirOpt with
      | None -> false
      | Some dir ->
        match String.Equals(normalizePath dir, rootNorm, StringComparison.OrdinalIgnoreCase) with
        | true -> false
        | false ->
          match hasCheckoutMarker dir with
          | true -> true
          | false -> walk (parentSegment dir)
    walk (Some target)

  /// WHY — agents call tools with the directory they are WORKING IN, which is
  /// often a subdirectory of the registered session root (e.g. repo\tests while
  /// the session is rooted at repo). Exact-only matching turned that into
  /// "No sessions match" while list_sessions showed the session plainly present;
  /// status and list disagreed and only an explicit switch_session recovered
  /// (friction report 2026-08). Because — matching falls back to sessions whose
  /// registered directory is a path-boundary ancestor of the requested one, so a
  /// request from inside a session's tree routes to that session instead of vanishing.
  /// EXCEPT across a checkout boundary (sagefs-multiagent-vision.md §3.2,
  /// Phase 0 item 3): a request from `.claude/worktrees/x` under a session
  /// rooted at the main checkout must NOT silently route to that session —
  /// the worktree is a DIFFERENT checkout even though the path is nested.
  let sessionsMatchingWorkingDirDeepWith
    (hasCheckoutMarker: string -> bool)
    (sessions: WorkerProtocol.SessionInfo list)
    (workingDir: string) =
    let target = normalizePath workingDir
    match sessionsMatchingWorkingDir sessions workingDir with
    | [] ->
      let isPathAncestorOf (ancestor: string) (candidate: string) =
        candidate.StartsWith(ancestor + "\\", StringComparison.Ordinal)
        || candidate.StartsWith(ancestor + "/", StringComparison.Ordinal)
      sessions
      |> List.filter (fun s ->
        let baseDir = normalizePath s.WorkingDirectory
        not (String.IsNullOrWhiteSpace baseDir)
        && isPathAncestorOf baseDir target
        && not (crossesCheckoutBoundaryWith hasCheckoutMarker baseDir target))
    | matched -> matched

  let sessionsMatchingWorkingDirDeep (sessions: WorkerProtocol.SessionInfo list) (workingDir: string) =
    sessionsMatchingWorkingDirDeepWith Checkout.hasCheckoutMarker sessions workingDir

  /// Honest failure: when working-directory routing finds nothing, say what DOES
  /// exist so the agent can reconcile the disagreement without a second tool call.
  let formatExistingSessionsHint (sessions: WorkerProtocol.SessionInfo list) =
    match sessions with
    | [] -> "(none running)"
    | _ ->
      sessions
      |> List.map (fun s ->
        sprintf "%s (%s, dir: %s)"
          (WorkerProtocol.SessionId.value s.Id)
          (WorkerProtocol.SessionLifecycleStatus.label s.Status)
          s.WorkingDirectory)
      |> String.concat "; "

  /// Find a session whose WorkingDirectory matches the given path.
  /// Convenience helper only — authoritative callers must detect ambiguity
  /// before selecting a single session.
  let resolveSessionByWorkingDir (sessions: WorkerProtocol.SessionInfo list) (workingDir: string) : WorkerProtocol.SessionInfo option =
    sessionsMatchingWorkingDir sessions workingDir
    |> List.tryHead

  let private formatSessionRoutingChoice (session: WorkerProtocol.SessionInfo) =
    sprintf "  %s  %s  %s"
      (WorkerProtocol.SessionId.value session.Id)
      (WorkerProtocol.SessionLifecycleStatus.label session.Status)
      session.WorkingDirectory

  /// Public because McpTools' resolveSessionId formats this ambiguity error.
  let formatWorkingDirectoryAmbiguity (prefix: string) (workingDir: string) (sessions: WorkerProtocol.SessionInfo list) =
    let matches =
      sessions
      |> List.map formatSessionRoutingChoice
      |> String.concat "\n"
    sprintf "%s '%s'.\n\nUse switch_session to select one before calling other tools, or pass sessionId explicitly on every call.\n\nMatching sessions:\n%s"
      prefix workingDir matches

  /// Why a session can't take work. Carried on FaultedSession so every tool
  /// that refuses a faulted session can say WHY. Before this, get_fsi_status
  /// and every other tool said "Session is faulted. Run reset..." and the real
  /// reason ("Not all DLLs are found ...") only showed up in /api/sessions.
  [<RequireQualifiedAccess>]
  type FaultCause =
    /// What the daemon recorded when the session faulted.
    | Recorded of reason: string
    /// Faulted, but whatever faulted it didn't say why.
    | NoReasonRecorded
    | Stopped

  module FaultCause =
    let ofStatus (status: WorkerProtocol.SessionLifecycleStatus) : FaultCause =
      match status with
      | WorkerProtocol.SessionLifecycleStatus.Faulted (WorkerProtocol.FaultReason.Reported reason) when not (System.String.IsNullOrWhiteSpace reason) ->
        FaultCause.Recorded reason
      // A start that ran out of patience on every attempt: the whole explanation, not "no reason recorded".
      | WorkerProtocol.SessionLifecycleStatus.Faulted (WorkerProtocol.FaultReason.StartTimedOut failure) ->
        FaultCause.Recorded (StartEscalation.describe failure)
      | WorkerProtocol.SessionLifecycleStatus.Stopped -> FaultCause.Stopped
      // default policy: this is only called for a status that's already been
      // classified as faulted-or-stopped; anything else has no reason to give.
      | _ -> FaultCause.NoReasonRecorded

    /// The reason, in words an agent can act on.
    let describe = function
      | FaultCause.Recorded reason -> reason
      | FaultCause.NoReasonRecorded -> "No reason was recorded for this fault. The daemon log has the details."
      | FaultCause.Stopped -> "The session was stopped."

  /// Convert a resolved session ID string to SessionId for SessionOps calls.
  /// Pre-condition: sid came from resolveSessionId or session lookup (already valid format).
  /// Moved here from Mcp.fs to keep that file under its line budget.
  let toSessionId (sid: string) =
    match WorkerProtocol.SessionId.validate sid with
    | Ok id -> id
    | Error e -> failwithf "Invalid resolved session ID '%s': %s" sid e

  /// Typed outcome of resolving which session a tool call should target.
  /// Guidance text is a pure function of this union: a session that exists in
  /// the registry is never reported as gone. `Gone` is produced only when the
  /// session is genuinely absent (never created, or explicitly stopped).
  ///
  /// Moved here from Mcp.fs, with its two pure companions below, for the reason
  /// this file's own header gives: no `McpContext`, no IO, and Mcp.fs sits at
  /// its line budget where every line it does not carry is headroom for a fix.
  type SessionResolution =
    | Routable of sessionId: string
    | WarmingUp of sessionId: string * status: WorkerProtocol.SessionLifecycleStatus
    | Unroutable of sessionId: string * status: WorkerProtocol.SessionLifecycleStatus
    | FaultedSession of sessionId: string * cause: FaultCause
    | Gone of message: string

  /// Pure classification: decide the resolution from registry knowledge.
  /// INVARIANT: `Gone` is produced only when the session is absent from the
  /// registry; an existing session is always Routable, WarmingUp, Unroutable,
  /// or FaultedSession — never Gone.
  let classifySessionAvailability
    (info: WorkerProtocol.SessionInfo option)
    (proxyAvailable: bool)
    : SessionResolution =
    match info with
    | Some i when proxyAvailable -> Routable (WorkerProtocol.SessionId.value i.Id)
    | Some i ->
      match i.Status with
      | WorkerProtocol.SessionLifecycleStatus.Starting _
      | WorkerProtocol.SessionLifecycleStatus.Restarting _ ->
        WarmingUp (WorkerProtocol.SessionId.value i.Id, i.Status)
      | WorkerProtocol.SessionLifecycleStatus.Faulted _
      | WorkerProtocol.SessionLifecycleStatus.Stopped ->
        FaultedSession (WorkerProtocol.SessionId.value i.Id, FaultCause.ofStatus i.Status)
      | _ ->
        Unroutable (WorkerProtocol.SessionId.value i.Id, i.Status)
    | None ->
      Gone "Session is no longer running. Use get_available_projects, then create_project_session, create_solution_session, or create_bare_session to start a new one."

  /// Pure guidance: the agent-facing message for a resolution.
  /// INVARIANT: "create_session" and "no longer running" appear only in the
  /// Gone case — an existing session is never presented as missing.
  let formatSessionResolution = function
    | Routable _ -> ""
    | WarmingUp (sid, status) ->
      sprintf "Session '%s' is still warming up (%s). This typically takes 15-30s for test projects. Call get_session_status with wait_seconds=60 to wait for readiness; do not sleep or poll. Do NOT create a new session — it will compete for resources and make warmup slower." sid (WorkerProtocol.SessionLifecycleStatus.label status)
    | Unroutable (sid, status) ->
      sprintf "Session '%s' exists (status: %s) but its worker is not routable yet — it may be mid-restart. Check get_session_status or list_sessions and re-check shortly. Do NOT create a duplicate session." sid (WorkerProtocol.SessionLifecycleStatus.label status)
    | FaultedSession (sid, cause) ->
      sprintf "Session '%s' is faulted. Why: %s\nRun reset_fsi_session or hard_reset_fsi_session to recover." sid (FaultCause.describe cause)
    | Gone msg -> msg
