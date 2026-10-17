namespace SageFs

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open SageFs.WorkerProtocol

/// Agents create sessions and often never stop them. A session keeps its worker process and its memory for days, and once
/// the connection that made it is gone nobody can stop it over MCP, because an MCP-bound session is stopped only by the
/// connection that created it. This module decides when the DAEMON stops one instead.
///
/// The rule is narrow on purpose. A session is stopped only when an MCP connection made it (the owner ledger has a record
/// for it), that connection has been gone for the bound, AND the session has been idle for the bound. A session with no
/// record was made by a person, from the dashboard, the CLI or an editor, and is never touched.
///
/// Every time decision is a pure function of the `Now` it is given (`decide`), so a failing seed replays. The stateful
/// part (`Reaper`) only remembers when each owner connection was last seen and what it has stopped.
///
/// Fail closed around restarts. Presence is not persisted, so after a daemon restart every owner looks gone. "Gone since"
/// and "idle since" therefore start no earlier than the daemon's own start (`goneSince`, `idleSince`): a restart can delay
/// a reap, never bring one forward.
module SessionReaper =

  /// How long an owner has to be gone, and a session idle, before the daemon stops it. Built from the configured span at the
  /// edge: zero or less means reaping is off.
  [<RequireQualifiedAccess>]
  type Bound =
    | Disabled
    | After of TimeSpan

  module Bound =
    let ofTimeSpan (span: TimeSpan) : Bound =
      match span > TimeSpan.Zero with
      | true -> Bound.After span
      | false -> Bound.Disabled

  /// The last time THIS daemon saw an owner's connection. A daemon that has not seen it yet knows nothing earlier than its
  /// own start.
  [<RequireQualifiedAccess>]
  type LastSeen =
    | NeverSeen
    | SeenAt of DateTime

  /// What the owner ledger and the presence tracker say about who made a session.
  [<RequireQualifiedAccess>]
  type OwnerFact =
    /// No ledger record: a person made it.
    | NoOwnerRecord
    | Connected of agentName: string
    | NotConnected of agentName: string * lastSeen: LastSeen

  /// What the session is doing, as far as the reaper cares.
  [<RequireQualifiedAccess>]
  type SessionPhase =
    /// Starting, evaluating, building or restarting: never idle.
    | Working
    | AtRest
    | Stopped

  type Facts =
    { Owner: OwnerFact
      Work: SessionPhase
      LastActivity: DateTime
      DaemonStartedAt: DateTime
      Now: DateTime
      Bound: Bound }

  [<RequireQualifiedAccess>]
  type KeepWhy =
    | Disabled
    /// A person made it.
    | NoOwnerRecord
    | OwnerConnected
    | WorkInFlight
    | AlreadyStopped
    | OwnerGoneButNotLongEnough of goneFor: TimeSpan * bound: TimeSpan
    | ActiveRecently of idleFor: TimeSpan * bound: TimeSpan

  type ReapWhy =
    { AgentName: string
      OwnerGoneFor: TimeSpan
      IdleFor: TimeSpan
      Bound: TimeSpan }

  [<RequireQualifiedAccess>]
  type Decision =
    | Keep of KeepWhy
    | Reap of ReapWhy

  /// When an owner started being gone. Never earlier than the daemon's own start: presence is not persisted, so before
  /// that this daemon knows nothing.
  let goneSince (daemonStartedAt: DateTime) (lastSeen: LastSeen) : DateTime =
    match lastSeen with
    | LastSeen.NeverSeen -> daemonStartedAt
    | LastSeen.SeenAt seen -> max seen daemonStartedAt

  /// When a session started being idle. Never earlier than the daemon's own start, for the same reason.
  let idleSince (daemonStartedAt: DateTime) (lastActivity: DateTime) : DateTime = max lastActivity daemonStartedAt

  /// A negative age (a clock that ran backwards) is no age at all.
  let private age (now: DateTime) (since: DateTime) : TimeSpan = max TimeSpan.Zero (now - since)

  /// How long the owner has been gone, by the rule above. Zero for a session nobody owns or whose owner is here.
  let goneFor (facts: Facts) : TimeSpan =
    match facts.Owner with
    | OwnerFact.NoOwnerRecord
    | OwnerFact.Connected _ -> TimeSpan.Zero
    | OwnerFact.NotConnected(_, lastSeen) -> age facts.Now (goneSince facts.DaemonStartedAt lastSeen)

  /// How long the session has been idle, by the rule above.
  let idleFor (facts: Facts) : TimeSpan = age facts.Now (idleSince facts.DaemonStartedAt facts.LastActivity)

  let decide (facts: Facts) : Decision =
    match facts.Bound with
    | Bound.Disabled -> Decision.Keep KeepWhy.Disabled
    | Bound.After bound ->
      match facts.Owner with
      | OwnerFact.NoOwnerRecord -> Decision.Keep KeepWhy.NoOwnerRecord
      | OwnerFact.Connected _ -> Decision.Keep KeepWhy.OwnerConnected
      | OwnerFact.NotConnected(agentName, _) ->
        match facts.Work with
        | SessionPhase.Stopped -> Decision.Keep KeepWhy.AlreadyStopped
        | SessionPhase.Working -> Decision.Keep KeepWhy.WorkInFlight
        | SessionPhase.AtRest ->
          let gone = goneFor facts
          let idle = idleFor facts
          match gone < bound, idle < bound with
          | true, _ -> Decision.Keep(KeepWhy.OwnerGoneButNotLongEnough(gone, bound))
          | false, true -> Decision.Keep(KeepWhy.ActiveRecently(idle, bound))
          | false, false -> Decision.Reap { AgentName = agentName; OwnerGoneFor = gone; IdleFor = idle; Bound = bound }

  /// A span as a person reads it: "42m", "13h 20m", "1d 1h". Never finer than a minute.
  let describeSpan (span: TimeSpan) : string =
    let minutes = int (max TimeSpan.Zero span).TotalMinutes
    match minutes with
    | 0 -> "under a minute"
    | m when m < 60 -> sprintf "%dm" m
    | m when m < 24 * 60 ->
      match m % 60 with
      | 0 -> sprintf "%dh" (m / 60)
      | rest -> sprintf "%dh %dm" (m / 60) rest
    | m ->
      let days = m / (24 * 60)
      match (m / 60) % 24 with
      | 0 -> sprintf "%dd" days
      | hours -> sprintf "%dd %dh" days hours

  module KeepWhy =
    /// One exhaustive to-string: why the daemon is not stopping this session.
    let describe (why: KeepWhy) : string =
      match why with
      | KeepWhy.Disabled -> "the reaper is off (SAGEFS_ABANDONED_SESSION_HOURS=0)"
      | KeepWhy.NoOwnerRecord -> "no agent connection made it, so a person did and the daemon leaves it alone"
      | KeepWhy.OwnerConnected -> "the connection that made it is still here"
      | KeepWhy.WorkInFlight -> "it is starting, evaluating, building or restarting"
      | KeepWhy.AlreadyStopped -> "it is already stopped"
      | KeepWhy.OwnerGoneButNotLongEnough(goneFor, bound) ->
        sprintf "the connection that made it has been gone for %s, and the bound is %s" (describeSpan goneFor) (describeSpan bound)
      | KeepWhy.ActiveRecently(idleFor, bound) ->
        sprintf "it was used %s ago, and the bound is %s" (describeSpan idleFor) (describeSpan bound)

  module ReapWhy =
    let describe (why: ReapWhy) : string =
      sprintf
        "the connection of agent '%s' that made it has been gone for %s and the session has been idle for %s, both past the bound of %s"
        why.AgentName (describeSpan why.OwnerGoneFor) (describeSpan why.IdleFor) (describeSpan why.Bound)

  /// One ledger record, reduced to what the reaper needs. The connection id is the identity; the agent name is a label.
  type Maker =
    { SessionId: string
      AgentName: string
      ConnectionId: string }

  /// What `phaseOf` makes of a session's lifecycle status.
  let phaseOf (status: SessionLifecycleStatus) : SessionPhase =
    match status with
    | SessionLifecycleStatus.Stopped -> SessionPhase.Stopped
    | SessionLifecycleStatus.Starting _
    | SessionLifecycleStatus.Evaluating _
    | SessionLifecycleStatus.Building _
    | SessionLifecycleStatus.Restarting _ -> SessionPhase.Working
    | SessionLifecycleStatus.Ready _
    | SessionLifecycleStatus.Faulted _
    | SessionLifecycleStatus.HostCrashed _ -> SessionPhase.AtRest

  /// One live session and what the reaper makes of it.
  type Assessment =
    { SessionId: string
      WorkingDirectory: string
      Facts: Facts
      Decision: Decision }

  module Assessment =
    /// The `Owner:` line `list_sessions` shows under an agent-made session. Empty for a session a person made.
    let listLine (assessment: Assessment) : string =
      let facts = assessment.Facts
      let idle = describeSpan (idleFor facts)
      let tail =
        match assessment.Decision with
        | Decision.Reap _ -> "Past the bound: the daemon stops it on its next sweep."
        | Decision.Keep why -> sprintf "Not stopped: %s." (KeepWhy.describe why)
      match facts.Owner with
      | OwnerFact.NoOwnerRecord -> ""
      | OwnerFact.Connected agent -> sprintf "Owner: agent '%s' made it and its connection is still here. Idle %s. %s" agent idle tail
      | OwnerFact.NotConnected(agent, _) ->
        sprintf "Owner: agent '%s' made it and its connection has been gone for %s. Idle %s. %s" agent (describeSpan (goneFor facts)) idle tail

  /// A session the daemon stopped as abandoned, and why.
  type ReapedSession =
    { SessionId: string
      AgentName: string
      WorkingDirectory: string
      At: DateTime
      Why: ReapWhy }

  module ReapedSession =
    let describe (reaped: ReapedSession) : string =
      sprintf "Stopped by the daemon at %s: session '%s' (%s): %s." (reaped.At.ToString "yyyy-MM-dd HH:mm 'UTC'") reaped.SessionId reaped.WorkingDirectory (ReapWhy.describe reaped.Why)

  type ReapAttempt =
    { Session: ReapedSession
      Outcome: Result<string, SageFsError> }

  [<RequireQualifiedAccess>]
  type SweepOutcome =
    /// Another sweep is still stopping sessions. This one stood aside, so a session is never stopped twice at once.
    | AlreadySweeping
    | Swept of ReapAttempt list

  /// How many stopped sessions the reaper remembers for `list_sessions`. A few days of a busy machine; the log line is the
  /// permanent record.
  [<Literal>]
  let RememberedReaps = 20

  /// The reaper's memory: when each owner connection was last seen, and what it has stopped. Everything it decides goes
  /// through `decide` with the `now` the caller passes in.
  type Reaper(daemonStartedAt: DateTime, bound: Bound) =
    let lastSeen = ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal)
    let gate = obj ()
    let mutable reaped : ReapedSession list = []
    let sweeping = ref 0

    let lastSeenOf (connectionId: string) : LastSeen =
      match lastSeen.TryGetValue connectionId with
      | true, seen -> LastSeen.SeenAt seen
      | false, _ -> LastSeen.NeverSeen

    member _.DaemonStartedAt : DateTime = daemonStartedAt
    member _.Bound : Bound = bound

    /// The sessions it has stopped, newest first.
    member _.Reaped : ReapedSession list = lock gate (fun () -> reaped)

    /// What the reaper makes of each live session. Notes which owners are connected on the way: seeing an owner is a fact
    /// whoever asked.
    member _.Assess(now: DateTime, makers: Maker list, isConnected: string -> bool, sessions: SessionInfo list) : Assessment list =
      let makerOf = makers |> List.map (fun m -> m.SessionId, m) |> Map.ofList
      sessions
      |> List.map (fun session ->
        let sid = SessionId.value session.Id
        let owner =
          match Map.tryFind sid makerOf with
          | None -> OwnerFact.NoOwnerRecord
          | Some maker ->
            match isConnected maker.ConnectionId with
            | true ->
              lastSeen.AddOrUpdate(maker.ConnectionId, now, (fun _ previous -> max previous now)) |> ignore
              OwnerFact.Connected maker.AgentName
            | false -> OwnerFact.NotConnected(maker.AgentName, lastSeenOf maker.ConnectionId)
        let facts =
          { Owner = owner
            Work = phaseOf session.Status
            LastActivity = session.LastActivity
            DaemonStartedAt = daemonStartedAt
            Now = now
            Bound = bound }
        { SessionId = sid
          WorkingDirectory = session.WorkingDirectory
          Facts = facts
          Decision = decide facts })

    /// Stop every session that is past the bound, one at a time, through `stop` (the normal stop path). A stop that fails
    /// is carried in the outcome and tried again on the next sweep. A sweep that starts while another is running stands
    /// aside.
    member this.SweepAsync
      (
        now: DateTime,
        makers: Maker list,
        isConnected: string -> bool,
        stop: string -> Task<Result<string, SageFsError>>,
        sessions: SessionInfo list
      ) : Task<SweepOutcome> =
      match Interlocked.CompareExchange(&sweeping.contents, 1, 0) with
      | 0 ->
        task {
          try
            let due =
              this.Assess(now, makers, isConnected, sessions)
              |> List.choose (fun a ->
                match a.Decision with
                | Decision.Reap why ->
                  Some
                    { SessionId = a.SessionId
                      AgentName = why.AgentName
                      WorkingDirectory = a.WorkingDirectory
                      At = now
                      Why = why }
                | Decision.Keep _ -> None)
            let attempts = ResizeArray<ReapAttempt>()
            for session in due do
              let! outcome =
                task {
                  try
                    return! stop session.SessionId
                  with ex ->
                    return Result.Error(SageFsError.SessionStopFailed(session.SessionId, ex.Message))
                }
              match outcome with
              | Result.Ok _ -> lock gate (fun () -> reaped <- (session :: reaped) |> List.truncate RememberedReaps)
              | Result.Error _ -> ()
              attempts.Add { Session = session; Outcome = outcome }
            return SweepOutcome.Swept(List.ofSeq attempts)
          finally
            Interlocked.Exchange(&sweeping.contents, 0) |> ignore
        }
      | _ -> Task.FromResult SweepOutcome.AlreadySweeping
