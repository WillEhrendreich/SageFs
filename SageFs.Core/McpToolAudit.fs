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
