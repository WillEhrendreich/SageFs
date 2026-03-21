namespace SageFs.Features

open System

/// Session health status for health snapshots.
[<RequireQualifiedAccess>]
type SessionHealthStatus =
  | Ready
  | Evaluating
  | WarmingUp
  | Faulted
  | Stopped

/// Overall daemon health.
[<RequireQualifiedAccess>]
type OverallHealth =
  | Healthy
  | Degraded
  | Unhealthy

/// Per-session summary for health snapshots.
type SessionHealthSummary = {
  SessionId: string
  ProjectName: string
  Status: SessionHealthStatus
  EvalCount: int
  LastActivity: DateTimeOffset
}

/// Live testing aggregate for health snapshots.
type LiveTestHealthSummary = {
  TotalTests: int
  Passed: int
  Failed: int
  Running: int
}

/// Complete daemon health snapshot — pure data, no IO.
type HealthSnapshot = {
  DaemonPid: int
  DaemonPort: int
  Uptime: TimeSpan
  Version: string
  SessionSummaries: SessionHealthSummary list
  LiveTestingSummary: LiveTestHealthSummary option
  MemoryMB: int
}

module DaemonHealth =

  let primarySessionStatus (sessions: SessionHealthSummary list) : SessionHealthStatus option =
    let has status =
      sessions |> List.exists (fun session -> session.Status = status)

    match sessions with
    | [] -> None
    | _ when has SessionHealthStatus.Ready -> Some SessionHealthStatus.Ready
    | _ when has SessionHealthStatus.Evaluating -> Some SessionHealthStatus.Evaluating
    | _ when has SessionHealthStatus.WarmingUp -> Some SessionHealthStatus.WarmingUp
    | _ when has SessionHealthStatus.Faulted -> Some SessionHealthStatus.Faulted
    | _ when has SessionHealthStatus.Stopped -> Some SessionHealthStatus.Stopped
    | _ -> None

  let primarySessionStatusLabel (sessions: SessionHealthSummary list) : string =
    primarySessionStatus sessions
    |> Option.map (function
      | SessionHealthStatus.Ready -> "Ready"
      | SessionHealthStatus.Evaluating -> "Evaluating"
      | SessionHealthStatus.WarmingUp -> "Warming Up"
      | SessionHealthStatus.Faulted -> "Faulted"
      | SessionHealthStatus.Stopped -> "Stopped")
    |> Option.defaultValue "no session"

  /// Determine overall health from snapshot.
  let overallStatus (snap: HealthSnapshot) : OverallHealth =
    match snap.SessionSummaries with
    | [] -> OverallHealth.Unhealthy
    | sessions ->
      let hasFaulted =
        sessions |> List.exists (fun s -> s.Status = SessionHealthStatus.Faulted)
      match hasFaulted with
      | true -> OverallHealth.Degraded
      | false -> OverallHealth.Healthy

  let healthEmoji = function
    | OverallHealth.Healthy -> "🟢"
    | OverallHealth.Degraded -> "🟡"
    | OverallHealth.Unhealthy -> "🔴"

  let healthLabel = function
    | OverallHealth.Healthy -> "Healthy"
    | OverallHealth.Degraded -> "Degraded"
    | OverallHealth.Unhealthy -> "Unhealthy"

  let sessionStatusLabel = function
    | SessionHealthStatus.Ready -> "Ready"
    | SessionHealthStatus.Evaluating -> "Evaluating"
    | SessionHealthStatus.WarmingUp -> "Warming Up"
    | SessionHealthStatus.Faulted -> "Faulted"
    | SessionHealthStatus.Stopped -> "Stopped"

  let sessionStatusEmoji = function
    | SessionHealthStatus.Ready -> "✅"
    | SessionHealthStatus.Evaluating -> "⚡"
    | SessionHealthStatus.WarmingUp -> "⏳"
    | SessionHealthStatus.Faulted -> "❌"
    | SessionHealthStatus.Stopped -> "⏹️"

  /// Format uptime as human-readable string.
  let formatUptime (ts: TimeSpan) : string =
    match ts.TotalDays >= 1.0 with
    | true -> sprintf "%dd %dh" (int ts.TotalDays) ts.Hours
    | false ->
      match ts.TotalHours >= 1.0 with
      | true -> sprintf "%dh %dm" (int ts.TotalHours) ts.Minutes
      | false -> sprintf "%dm" (int ts.TotalMinutes)

  /// Format a complete health summary as multi-line text.
  let formatSummary (snap: HealthSnapshot) : string =
    let status = overallStatus snap
    let header =
      sprintf "%s SageFs %s (PID %d, port %d) — %s, up %s, %dMB"
        (healthEmoji status)
        snap.Version
        snap.DaemonPid
        snap.DaemonPort
        (healthLabel status)
        (formatUptime snap.Uptime)
        snap.MemoryMB

    let sessionLines =
      snap.SessionSummaries
      |> List.map (fun s ->
        sprintf "  %s %s [%s] — %d evals"
          (sessionStatusEmoji s.Status)
          s.ProjectName
          (sessionStatusLabel s.Status)
          s.EvalCount)

    let testLine =
      match snap.LiveTestingSummary with
      | Some t ->
        [ sprintf "  🧪 Tests: %d total, %d passed, %d failed, %d running"
            t.TotalTests t.Passed t.Failed t.Running ]
      | None -> []

    [ [header]; sessionLines; testLine ]
    |> List.concat
    |> String.concat "\n"

  /// Explain the daemon's current session mix in one line for diagnostics surfaces.
  let diagnosticSummary (snap: HealthSnapshot) : string =
    let breakdown =
      snap.SessionSummaries
      |> List.countBy (fun session -> sessionStatusLabel session.Status)
      |> List.sortBy fst
      |> List.map (fun (label, count) -> sprintf "%s=%d" label count)
      |> String.concat ", "

    match snap.SessionSummaries with
    | [] -> "No sessions registered with the daemon."
    | sessions ->
      let faultedProjects =
        sessions
        |> List.filter (fun session -> session.Status = SessionHealthStatus.Faulted)
        |> List.map (fun session -> session.ProjectName)
        |> List.distinct

      match faultedProjects with
      | [] -> sprintf "%d session(s): %s" sessions.Length breakdown
      | projects ->
        sprintf
          "Faulted session(s): %s. %d session(s): %s"
          (String.concat ", " projects)
          sessions.Length
          breakdown
