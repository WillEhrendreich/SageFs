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
  WorkflowLabel: string
  EvalCount: int
  Supervised: bool
  RestartCount: int
  SessionCount: int
  Health: SessionHealth
}

type StatusBarView = {
  Text: string
  Tooltip: string
}

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
  { Text =
      sprintf "%s SageFs: %s [%s]%s%s%s"
        (healthIcon input.Health) input.ProjectLabel input.WorkflowLabel
        (evalSuffix input.EvalCount) (supervisedSuffix input.Supervised) (restartSuffix input.RestartCount)
    Tooltip =
      sprintf "SageFs: %s — %d session(s) — click for session menu%s"
        input.ProjectLabel input.SessionCount (healthTooltipSuffix input.Health) }

/// The status bar when the daemon is ready but no session is selected.
let noSessionView (supervised: bool) (restartCount: int) : StatusBarView =
  { Text = sprintf "$(zap) SageFs: ready (no session)%s%s" (supervisedSuffix supervised) (restartSuffix restartCount)
    Tooltip = "SageFs — ready, no active session — click for session menu" }

/// True when a string still carries a codicon token — used by the contract
/// test to pin that `Tooltip` never does, for every view this module builds.
let containsCodiconToken (s: string) : bool = s.Contains "$("
