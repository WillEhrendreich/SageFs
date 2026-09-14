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

  /// Shared implementation behind both `recordToolCall` (legacy, string-keyed
  /// — Role derived by re-classifying the key text) and `recordMemberActivity`
  /// (MemberTable.MemberId-keyed — Role derived from the connection kind
  /// itself). One table, one merge algorithm; the two public entry points
  /// differ ONLY in how `key`/`role` are computed, per
  /// sagefs-multiagent-vision.md §4.1 / §10 Phase 0 item 4 ("SessionMap, the
  /// name-keyed AgentActivityTracker ... collapse into one table").
  let private recordActivity
    (tracker: Tracker)
    (key: string)
    (role: OccupantRole)
    (sessionId: string)
    (filePath: string option)
    (intent: string option)
    (now: DateTime)
    : unit =
    tracker.Agents.AddOrUpdate(
      key,
      // Factory: first time we see this agent
      (fun _key ->
        let files =
          match filePath with
          | Some f -> [f]
          | None -> []
        {
          AgentName = key
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

  /// Record a tool call from an agent, keyed by its caller-supplied name.
  /// Role is derived by re-classifying that name's text (`OccupantRole.classify`).
  /// PRESERVED for existing callers (the dashboard's browser-tab presence,
  /// and every test written against this signature) — behavior-identical to
  /// before `recordMemberActivity` existed, since for any string `s`,
  /// `OccupantRole.ofMemberId (MemberId.Minted s) = OccupantRole.classify s`
  /// and `MemberId.display (MemberId.Minted s) = s` (SageFs.MemberTable).
  /// Prefer `recordMemberActivity` wherever a real `MemberTable.MemberId` is
  /// already in hand (SageFs/Mcp.fs's bound MCP calls) — it derives Role from
  /// the connection kind itself, never from re-parsed string text.
  let recordToolCall
    (tracker: Tracker)
    (agentName: string)
    (sessionId: string)
    (filePath: string option)
    (intent: string option)
    (now: DateTime)
    : unit =
    recordActivity tracker agentName (OccupantRole.classify agentName) sessionId filePath intent now

  /// Record a tool call from a BOUND member identity
  /// (sagefs-multiagent-vision.md §4.1: "identity is bound to the
  /// connection, not declared"). Keys the SAME shared table as `recordToolCall`
  /// (`MemberTable.MemberId.display id`) but computes Role from the `MemberId`
  /// CASE itself (`OccupantRole.ofMemberId`) — never by re-classifying
  /// whatever text happens to be inside the id. This is what closes
  /// "OccupantRole.classify by name prefix deleted" for the bound-connection
  /// path: an `Mcp`/`Browser` member's role can never silently misclassify
  /// due to a coincidental prefix match, because it is not derived from a
  /// string at all.
  let recordMemberActivity
    (tracker: Tracker)
    (id: MemberTable.MemberId)
    (sessionId: string)
    (filePath: string option)
    (intent: string option)
    (now: DateTime)
    : unit =
    recordActivity tracker (MemberTable.MemberId.display id) (OccupantRole.ofMemberId id) sessionId filePath intent now

  /// Forget one member outright (its key — see SageFs.MemberTable.MemberId.display
  /// for how MCP/dashboard callers derive it — not a display name). Used on a
  /// graceful disconnect (e.g. a dashboard tab's SSE stream closing) where the
  /// presence should disappear immediately rather than wait out the staleness
  /// window `cleanup` enforces.
  let forget (tracker: Tracker) (key: string) : unit =
    tracker.Agents.TryRemove(key) |> ignore

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
