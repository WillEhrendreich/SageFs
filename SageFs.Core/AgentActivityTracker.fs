namespace SageFs

open System
open System.Collections.Concurrent
open SageFs.SessionOperations

/// In-memory, thread-safe agent activity tracker.
/// Replaces the noop EventPersistence for coordination purposes.
/// Records tool calls with timestamps and file paths, enabling
/// occupancy cleanup, file-overlap detection, and session guidance.
///
/// NOT a persistence layer — this data lives only in the daemon's
/// memory and is lost on restart. That's by design: coordination
/// is about the current moment, not history.
module AgentActivityTracker =

  /// Maximum number of recent files tracked per agent.
  /// Prevents unbounded growth from long-running agents.
  [<Literal>]
  let MaxRecentFiles = 50

  /// Internal snapshot stored per agent.
  /// The ConcurrentDictionary handles thread safety for the map;
  /// individual field updates are atomic reference swaps.
  type internal AgentSnapshot = {
    AgentName: string
    Role: OccupantRole
    SessionId: string
    LastToolCall: DateTime
    Intent: string option
    RecentFiles: string list
    EvalCount: int
  }

  /// The tracker instance. Create one per daemon lifetime.
  type Tracker internal (agents: ConcurrentDictionary<string, AgentSnapshot>) =
    member internal _.Agents = agents

  /// Create a fresh tracker (empty).
  let create () : Tracker =
    Tracker(ConcurrentDictionary<string, AgentSnapshot>(StringComparer.Ordinal))

  /// Record a tool call from an agent.
  /// Updates LastToolCall, accumulates file path, increments eval count.
  /// Intent is updated only when a non-None value is provided.
  let recordToolCall
    (tracker: Tracker)
    (agentName: string)
    (sessionId: string)
    (filePath: string option)
    (intent: string option)
    (now: DateTime)
    : unit =
    let role = OccupantRole.classify agentName
    tracker.Agents.AddOrUpdate(
      agentName,
      // Factory: first time we see this agent
      (fun _key ->
        let files =
          match filePath with
          | Some f -> [f]
          | None -> []
        {
          AgentName = agentName
          Role = role
          SessionId = sessionId
          LastToolCall = now
          Intent = intent
          RecentFiles = files
          EvalCount = 1
        }),
      // Update: agent already exists
      (fun _key existing ->
        let newFiles =
          match filePath with
          | Some f ->
            let updated = f :: (existing.RecentFiles |> List.filter (fun x -> x <> f))
            match updated.Length > MaxRecentFiles with
            | true -> updated |> List.take MaxRecentFiles
            | false -> updated
          | None -> existing.RecentFiles
        let newIntent =
          match intent with
          | Some _ -> intent
          | None -> existing.Intent
        {
          existing with
            SessionId = sessionId
            LastToolCall = now
            Intent = newIntent
            RecentFiles = newFiles
            EvalCount = existing.EvalCount + 1
        })
    ) |> ignore

  /// Get an agent's current presence as an immutable snapshot.
  /// Returns None if the agent has never been seen.
  let getPresence (tracker: Tracker) (agentName: string) : AgentPresence option =
    match tracker.Agents.TryGetValue(agentName) with
    | true, snap ->
      Some {
        AgentName = snap.AgentName
        Role = snap.Role
        SessionId = snap.SessionId
        LastToolCall = snap.LastToolCall
        Intent = snap.Intent
        RecentFiles = snap.RecentFiles
        EvalCount = snap.EvalCount
      }
    | false, _ -> None

  /// Get all agent presences, optionally filtered by session.
  let getAllPresences (tracker: Tracker) (sessionId: string option) : AgentPresence list =
    tracker.Agents.Values
    |> Seq.filter (fun snap ->
      match sessionId with
      | Some sid -> snap.SessionId = sid
      | None -> true)
    |> Seq.map (fun snap ->
      { AgentPresence.AgentName = snap.AgentName
        Role = snap.Role
        SessionId = snap.SessionId
        LastToolCall = snap.LastToolCall
        Intent = snap.Intent
        RecentFiles = snap.RecentFiles
        EvalCount = snap.EvalCount } : AgentPresence)
    |> Seq.toList

  /// Get presences filtered to only those within the activity time window.
  /// Files outside the window are excluded from each presence's RecentFiles.
  let getActivePresences
    (tracker: Tracker)
    (sessionId: string option)
    (activityWindow: TimeSpan)
    (now: DateTime)
    : AgentPresence list =
    getAllPresences tracker sessionId
    |> List.filter (fun p -> AgentPresence.isFresh now activityWindow p)

  /// Evict agents whose last tool call exceeds the timeout.
  /// Returns the cleanup outcome describing what happened.
  let cleanup
    (tracker: Tracker)
    (timeout: TimeSpan)
    (now: DateTime)
    : OccupancyCleanupOutcome =
    let staleAgents =
      tracker.Agents
      |> Seq.filter (fun kv -> (now - kv.Value.LastToolCall) > timeout)
      |> Seq.map (fun kv -> kv.Key)
      |> Seq.toList
    match staleAgents with
    | [] -> OccupancyCleanupOutcome.NothingToClean
    | agents ->
      agents |> List.iter (fun name ->
        tracker.Agents.TryRemove(name) |> ignore)
      OccupancyCleanupOutcome.EvictedStale agents
