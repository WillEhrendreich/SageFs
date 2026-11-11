/// Pure text/tooltip construction for the SageFs session status bar item.
///
/// WHY — measured on 0.6.670: the status bar read `SageFs: session [REPL]`
/// for a session that had actually loaded a project (Extension.fs re-derived
/// a project label from `SessionInfo.projects` — the DECLARED list, which
/// can be empty even when a project loaded — instead of reusing
/// `SessionsTreePure.label`, which already prefers what the worker actually
/// loaded). Separately, the tooltip was assembled as an ad hoc string
/// alongside `Text` (which legitimately carries a `$(zap)` codicon token for
/// the icon slot); keeping both assemblies textually close made it easy for
/// a codicon token to end up in `Tooltip`, the one place VS Code never
/// expands it. This module keeps `Text` (icon-bearing) and `Tooltip`
/// (plain words only) as two fields of one total function so the split
/// can't drift, mirroring the label/icon split SessionsTreePure enforces
/// for the Sessions tree.
///
/// No Fable dependency; tested under `dotnet fsi`
/// (tests/StatusBarContractTests.fsx).
module SageFs.Vscode.StatusBarPure

open SageFs.Vscode.SessionsTreePure

/// What the status bar needs to know about the active session.
/// `ProjectLabel` is expected to come from `SessionsTreePure.label` — this
/// module does not re-derive it from the session's project lists.
/// `Health` is the daemon's verdict (`SessionsTreePure.SessionHealth`), for
/// the same reason the tree takes it: the status bar is the one SageFs surface
/// a user glances at constantly, and a `$(zap)` on a session that loaded
/// nothing is the lie the verdict exists to kill.
type SessionStatusBarInput = {
  ProjectLabel: string
  /// The daemon's id for the session this item describes, so a hover answers "which one?".
  SessionId: string
  WorkflowLabel: string
  EvalCount: int
  Supervised: bool
  RestartCount: int
  SessionCount: int
  Health: SessionHealth
}

/// One status bar rendering. `Text` carries codicon tokens (they expand to glyphs there and nowhere
/// else). `AccessibleName` is what a screen reader says instead: one plain sentence, no token, no
/// sparkline glyph. `Tooltip` is the hover detail, plain words only.
type StatusBarView = {
  Text: string
  AccessibleName: string
  Tooltip: string
}

/// A sentence for a screen reader: trailing dots and spaces trimmed, then one full stop.
let sentence (words: string) : string =
  sprintf "%s." (words.Trim().TrimEnd('.', ' ', '…'))

let private evalSuffix (count: int) =
  match count with
  | 0 -> ""
  | n -> sprintf " [%d]" n

let private supervisedSuffix (supervised: bool) =
  match supervised with
  | true -> " $(shield)"
  | false -> ""

let private restartSuffix (restartCount: int) =
  match restartCount with
  | 0 -> ""
  | n -> sprintf " %d↻" n

/// The icon slot — the one place in `Text` where a codicon token expands.
/// Total over the daemon's verdict; `Unknown` (no verdict on the wire) keeps
/// the historical bolt rather than inventing a warning.
let private healthIcon (health: SessionHealth) =
  match health with
  | SessionHealth.Healthy
  | SessionHealth.Unknown -> "$(zap)"
  | SessionHealth.Degraded _ -> "$(warning)"
  | SessionHealth.Failed _ -> "$(error)"
  | SessionHealth.Starting -> "$(loading~spin)"

/// The reason the daemon attached, appended to the tooltip so the remedy is
/// one hover away instead of nowhere. Plain words only — no codicon tokens.
let private healthTooltipSuffix (health: SessionHealth) =
  match SessionHealth.reason health with
  | Some r -> sprintf " — %s: %s" (SessionHealth.label health) r
  | None -> ""

/// The status bar for the currently-selected session.
let sessionView (input: SessionStatusBarInput) : StatusBarView =
  { AccessibleName =
      sentence (
        sprintf "SageFs session %s, workflow %s, %d session(s) on this daemon%s%s%s. Click for the session menu"
          input.ProjectLabel input.WorkflowLabel input.SessionCount
          (match input.EvalCount with
           | 0 -> ""
           | n -> sprintf ", %d evaluation(s)" n)
          (match input.Supervised with
           | true -> ", supervised"
           | false -> "")
          (match input.RestartCount with
           | 0 -> ""
           | n -> sprintf ", restarted %d time(s)" n)
        + (match SessionHealth.reason input.Health with
           | Some r -> sprintf ". %s: %s" (SessionHealth.label input.Health) r
           | None -> ""))
    Text =
      sprintf "%s SageFs: %s [%s]%s%s%s"
        (healthIcon input.Health) input.ProjectLabel input.WorkflowLabel
        (evalSuffix input.EvalCount) (supervisedSuffix input.Supervised) (restartSuffix input.RestartCount)
    Tooltip =
      sprintf "SageFs: %s [%s] — session %s — %d session(s) — click for session menu%s"
        input.ProjectLabel input.WorkflowLabel input.SessionId input.SessionCount (healthTooltipSuffix input.Health) }

/// The status bar when the daemon is ready but no session is selected.
let noSessionView (supervised: bool) (restartCount: int) : StatusBarView =
  { Text = sprintf "$(zap) SageFs: ready (no session)%s%s" (supervisedSuffix supervised) (restartSuffix restartCount)
    AccessibleName = "SageFs is ready and has no active session. Click for the session menu."
    Tooltip = "SageFs — ready, no active session — click for session menu" }

/// The label for a warm-up phase the daemon reports, as the status bar words it.
let warmupPhaseLabel (phase: string) (detail: string option) : string =
  match phase with
  | "creating_fsi" -> "Creating FSI..."
  | "scanning_sources" -> "Scanning sources..."
  | "loading_assemblies" -> "Loading assemblies..."
  | "opening_namespaces" ->
    match detail with
    | Some d -> sprintf "Opening namespaces (%s)" d
    | None -> "Opening namespaces..."
  | "finalizing" -> "Finalizing..."
  | _ -> "Warming up..."

/// Every status the extension writes to the session item outside a live session's own view.
/// Each one builds (text, accessible name, tooltip) through `statusView`, so a call site cannot
/// set one of the three and forget the others.
[<RequireQualifiedAccess>]
type DaemonStatus =
  /// The daemon does not answer.
  | Offline
  /// A start was tried and the daemon is still not answering.
  | StartFailed
  /// The `sagefs` process could not be spawned at all.
  | SpawnFailed
  /// A start is under way; the count is the seconds spent waiting so far, when known.
  | Starting of waitedSeconds: int option
  /// The session is warming up; the label is `warmupPhaseLabel`'s wording of the phase.
  | WarmingUp of phaseLabel: string option
  /// The window's session faulted. `Message` is the daemon's reason; `Tooltip` is the full description.
  | SessionFault of message: string option * tooltip: string option
  /// The daemon is up and has no session.
  | NoSession
  /// The daemon is up but none of its sessions belongs to this workspace.
  | NoSessionForWorkspace of daemonSessions: int * supervised: bool * restartCount: int
  /// The event stream came back.
  | Connected
  /// The event stream dropped and is being re-established.
  | Reconnecting
  /// Connecting the event stream threw.
  | StreamError
  /// Activation itself threw.
  | ActivationFailed

let statusView (status: DaemonStatus) : StatusBarView =
  match status with
  | DaemonStatus.Offline ->
    { Text = "$(circle-slash) SageFs: offline"
      AccessibleName = "SageFs is offline. Click for the session menu."
      Tooltip = "SageFs is not running — click for session menu" }
  | DaemonStatus.StartFailed ->
    { Text = "$(error) SageFs: offline"
      AccessibleName = "SageFs is offline because the daemon did not start. Click for the session menu."
      Tooltip = "SageFs did not start — see the SageFs output for the reason, or click for session menu" }
  | DaemonStatus.SpawnFailed ->
    { Text = "$(error) SageFs: spawn failed"
      AccessibleName = "SageFs could not start the sagefs process. Click for the session menu."
      Tooltip = "SageFs could not start the sagefs process — see the SageFs output, or click for session menu" }
  | DaemonStatus.Starting None ->
    { Text = "$(loading~spin) SageFs: starting..."
      AccessibleName = "SageFs is starting."
      Tooltip = "SageFs is starting" }
  | DaemonStatus.Starting (Some seconds) ->
    { Text = sprintf "$(loading~spin) SageFs: starting... (%ds)" seconds
      AccessibleName = sprintf "SageFs is starting, waited %d second(s) so far." seconds
      Tooltip = sprintf "SageFs is starting, waited %d second(s) so far" seconds }
  | DaemonStatus.WarmingUp (Some label) ->
    { Text = sprintf "$(loading~spin) SageFs: %s" label
      AccessibleName = sentence (sprintf "SageFs is warming up the session. %s" label)
      Tooltip = sprintf "SageFs: %s" label }
  | DaemonStatus.WarmingUp None ->
    { Text = "$(loading~spin) SageFs: warming up..."
      AccessibleName = "SageFs is warming up the session."
      Tooltip = "SageFs: warming up..." }
  | DaemonStatus.SessionFault (message, tooltip) ->
    { Text = "$(error) SageFs: session error"
      AccessibleName =
        match message with
        | Some m -> sentence (sprintf "SageFs session error. %s" m)
        | None -> "SageFs session error. Click for the session menu."
      Tooltip = defaultArg tooltip "SageFs: session error" }
  | DaemonStatus.NoSession ->
    { Text = "$(circle-slash) SageFs: no session"
      AccessibleName = "SageFs has no active session. Click for the session menu."
      Tooltip = "SageFs: no session — click for session menu" }
  | DaemonStatus.NoSessionForWorkspace (daemonSessions, supervised, restartCount) ->
    let tooltip =
      sprintf "SageFs — ready, but none of the daemon's %d session(s) is for this workspace — click for session menu" daemonSessions
    { Text = (noSessionView supervised restartCount).Text
      AccessibleName =
        sprintf "SageFs is ready, but none of the daemon's %d session(s) is for this workspace. Click for the session menu." daemonSessions
      Tooltip = tooltip }
  | DaemonStatus.Connected ->
    { Text = "$(check) SageFs: connected"
      AccessibleName = "SageFs is connected."
      Tooltip = "SageFs is connected — click for session menu" }
  | DaemonStatus.Reconnecting ->
    { Text = "$(sync~spin) SageFs: reconnecting..."
      AccessibleName = "SageFs lost the event stream and is reconnecting."
      Tooltip = "SageFs lost the event stream and is reconnecting" }
  | DaemonStatus.StreamError ->
    { Text = "$(warning) SageFs: SSE error"
      AccessibleName = "SageFs could not connect the event stream. Click for the session menu."
      Tooltip = "SageFs could not connect the event stream — see the SageFs output, or click for session menu" }
  | DaemonStatus.ActivationFailed ->
    { Text = "$(error) SageFs: activation failed"
      AccessibleName = "SageFs failed to activate. See the SageFs output for the reason."
      Tooltip = "SageFs failed to activate — see the SageFs output for the reason" }

/// One sample of every `DaemonStatus` case, so a contract test covers each. Add a case above and
/// the exhaustive match in `statusView` fails to compile until it is handled; add its sample here.
let daemonStatusSamples : DaemonStatus list =
  [ DaemonStatus.Offline
    DaemonStatus.StartFailed
    DaemonStatus.SpawnFailed
    DaemonStatus.Starting None
    DaemonStatus.Starting (Some 7)
    DaemonStatus.WarmingUp (Some (warmupPhaseLabel "scanning_sources" None))
    DaemonStatus.WarmingUp None
    DaemonStatus.SessionFault (Some "Worker exited with code 1", Some "Worker exited with code 1")
    DaemonStatus.SessionFault (None, None)
    DaemonStatus.NoSession
    DaemonStatus.NoSessionForWorkspace (3, false, 0)
    DaemonStatus.Connected
    DaemonStatus.Reconnecting
    DaemonStatus.StreamError
    DaemonStatus.ActivationFailed ]

/// The accessible name for the eval-performance item, whose text is block-character sparkline a
/// screen reader reads out glyph by glyph.
let evalPerfAccessibleName (evalCount: int) (p50Ms: float option) : string =
  match p50Ms with
  | Some ms -> sentence (sprintf "SageFs eval performance, %d evaluation(s), median %.0f milliseconds. Click to open the dashboard" evalCount ms)
  | None -> sentence (sprintf "SageFs eval performance, %d evaluation(s). Click to open the dashboard" evalCount)

/// True when a string still carries a codicon token — used by the contract
/// test to pin that `Tooltip` never does, for every view this module builds.
let containsCodiconToken (s: string) : bool = s.Contains "$("

/// What clicking the eval-performance status item does. The item turns warning and error coloured
/// on slow evals and had no command at all; the eval timeline it summarises is on the dashboard.
let evalPerfCommand : string = "sagefs.openDashboard"

/// The last line of the eval-performance tooltip. Plain words, no codicon token.
let evalPerfClickHint : string = "Click to open the dashboard."
