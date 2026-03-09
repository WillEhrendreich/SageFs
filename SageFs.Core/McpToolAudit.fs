namespace SageFs

open System

/// Pure, immutable MCP tool usage audit tracker.
/// Records per-tool call counts, durations, outcomes, and affordance violations.
/// Thread-safe via immutable snapshots — callers swap references atomically.
module McpToolAudit =

  /// Outcome of a single tool invocation.
  [<Struct>]
  type ToolOutcome =
    | Success
    | Failure
    | AffordanceViolation

  /// Per-tool aggregated statistics (immutable).
  type ToolStats = {
    ToolName: string
    CallCount: int
    SuccessCount: int
    FailureCount: int
    AffordanceViolations: int
    TotalDurationMs: float
    MinDurationMs: float
    MaxDurationMs: float
  }

  module ToolStats =
    let empty name = {
      ToolName = name
      CallCount = 0
      SuccessCount = 0
      FailureCount = 0
      AffordanceViolations = 0
      TotalDurationMs = 0.0
      MinDurationMs = Double.MaxValue
      MaxDurationMs = 0.0
    }

    let record (durationMs: float) (outcome: ToolOutcome) (stats: ToolStats) =
      { stats with
          CallCount = stats.CallCount + 1
          SuccessCount =
            match outcome with
            | Success -> stats.SuccessCount + 1
            | _ -> stats.SuccessCount
          FailureCount =
            match outcome with
            | Failure -> stats.FailureCount + 1
            | _ -> stats.FailureCount
          AffordanceViolations =
            match outcome with
            | AffordanceViolation -> stats.AffordanceViolations + 1
            | _ -> stats.AffordanceViolations
          TotalDurationMs = stats.TotalDurationMs + durationMs
          MinDurationMs = min stats.MinDurationMs durationMs
          MaxDurationMs = max stats.MaxDurationMs durationMs }

    let averageDurationMs (stats: ToolStats) =
      match stats.CallCount with
      | 0 -> 0.0
      | n -> stats.TotalDurationMs / float n

    let successRate (stats: ToolStats) =
      match stats.CallCount with
      | 0 -> 1.0
      | n -> float stats.SuccessCount / float n

  /// Snapshot of all tool audit data (immutable).
  type AuditSnapshot = {
    Tools: Map<string, ToolStats>
    TotalCalls: int
    StartedAt: DateTimeOffset
  }

  module AuditSnapshot =
    let empty () = {
      Tools = Map.empty
      TotalCalls = 0
      StartedAt = DateTimeOffset.UtcNow
    }

    /// Record a tool invocation in the audit snapshot.
    let record (toolName: string) (durationMs: float) (outcome: ToolOutcome) (snap: AuditSnapshot) =
      let stats =
        snap.Tools
        |> Map.tryFind toolName
        |> Option.defaultValue (ToolStats.empty toolName)
        |> ToolStats.record durationMs outcome
      { snap with
          Tools = snap.Tools |> Map.add toolName stats
          TotalCalls = snap.TotalCalls + 1 }

    /// Get tools sorted by call count descending (most-used first).
    let topTools (snap: AuditSnapshot) =
      snap.Tools
      |> Map.toList
      |> List.map snd
      |> List.sortByDescending (fun s -> s.CallCount)

    /// Get tools with zero calls (never used).
    let unusedTools (allToolNames: string list) (snap: AuditSnapshot) =
      allToolNames
      |> List.filter (fun name ->
        snap.Tools
        |> Map.tryFind name
        |> Option.map (fun s -> s.CallCount = 0)
        |> Option.defaultValue true)

    /// Get tools where >5% of calls are affordance violations.
    let problematicTools (snap: AuditSnapshot) =
      snap.Tools
      |> Map.toList
      |> List.map snd
      |> List.filter (fun s ->
        s.CallCount > 0 && float s.AffordanceViolations / float s.CallCount > 0.05)

    /// Summary statistics for the audit decision document.
    type AuditSummary = {
      TotalCalls: int
      UniqueToolsUsed: int
      TopToolsByUsage: (string * int) list
      UnusedTools: string list
      ToolsWithHighFailRate: (string * float) list
      ToolsWithAffordanceViolations: (string * int) list
      AverageDurationMs: float
      UptimeMinutes: float
    }

    let summarize (allToolNames: string list) (snap: AuditSnapshot) : AuditSummary =
      let tools = topTools snap
      let uptime = (DateTimeOffset.UtcNow - snap.StartedAt).TotalMinutes
      { TotalCalls = snap.TotalCalls
        UniqueToolsUsed = snap.Tools.Count
        TopToolsByUsage =
          tools
          |> List.take (min 10 tools.Length)
          |> List.map (fun s -> s.ToolName, s.CallCount)
        UnusedTools = unusedTools allToolNames snap
        ToolsWithHighFailRate =
          tools
          |> List.filter (fun s -> s.CallCount > 0 && ToolStats.successRate s < 0.9)
          |> List.map (fun s -> s.ToolName, ToolStats.successRate s)
        ToolsWithAffordanceViolations =
          tools
          |> List.filter (fun s -> s.AffordanceViolations > 0)
          |> List.map (fun s -> s.ToolName, s.AffordanceViolations)
        AverageDurationMs =
          match snap.TotalCalls with
          | 0 -> 0.0
          | _ ->
            let total = tools |> List.sumBy (fun s -> s.TotalDurationMs)
            total / float snap.TotalCalls
        UptimeMinutes = uptime }

  /// Thread-safe mutable audit tracker wrapping immutable snapshots.
  /// Uses Interlocked.Exchange for lock-free updates.
  type AuditTracker() =
    let mutable snapshot = AuditSnapshot.empty ()

    member _.Record(toolName: string, durationMs: float, outcome: ToolOutcome) =
      let rec tryUpdate () =
        let current = snapshot
        let next = AuditSnapshot.record toolName durationMs outcome current
        let exchanged = System.Threading.Interlocked.CompareExchange(&snapshot, next, current)
        match Object.ReferenceEquals(exchanged, current) with
        | true -> ()
        | false -> tryUpdate ()
      tryUpdate ()

    member _.Snapshot = snapshot

    member _.Reset() =
      snapshot <- AuditSnapshot.empty ()
