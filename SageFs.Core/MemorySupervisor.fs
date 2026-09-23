namespace SageFs

open System

/// A pure policy for shedding load before the daemon starves the machine.
/// Twice in one night the daemon's own RSS grew past 50GB of a 62GB box,
/// pinned a core, stopped answering HTTP, and starved every other process on
/// the machine until someone killed it by hand — while every session read
/// Ready and nothing in SageFs noticed. `HealthWatch`/`HealthAnomaly` are the
/// half that notices. This is the half that acts.
///
/// Everything here is a pure function of a snapshot: the daemon's own RSS,
/// the machine's available memory, and what each session is doing right now.
/// The daemon (or a Simulation harness) supplies the snapshot and carries the
/// returned `MemoryPressure` forward as the next call's `current` — the only
/// state this module owns is that one enum, threaded by the caller exactly
/// like `HealthAnomaly.State` is. `MemoryPressure` is shared with job 5's
/// expensive-work lease (`ExpensiveWorkLease.fs`) — this module and that one
/// react to the identical reading, never two competing ones.
///
/// THREE LEVELS, ESCALATING, WITH HYSTERESIS:
///   - `Normal` — nothing to shed. Dead sessions are still reaped even here:
///     freeing a session that is already gone has no downside at any
///     pressure level, so reaping is not gated on `MemoryPressure` at all.
///   - `Tight` — on top of reaping, idle (never active-viewed) sessions are
///     stopped, oldest-idle first, until the level clears.
///   - `Critical` — on top of both, new sessions are refused with a reason a
///     caller can show a user or an agent.
/// Each tier's enter/exit thresholds are DIFFERENT (`ShedEnterFrac` <
/// `ShedExitFrac`, `RefuseEnterFrac` < `RefuseExitFrac`) — the same
/// breach/clear-with-a-dead-zone hysteresis `HealthAnomaly.fs` uses for its
/// CUSUM verdicts, for the same reason: a single threshold that available
/// memory happens to hover around would flap the level (and the actions it
/// drives) every sample. Two thresholds per tier mean a value has to
/// recover PAST the entry point, not just twitch above it, before the level
/// steps back down.
///
/// WHAT THIS MODULE NEVER DOES: it never proposes stopping a session the
/// caller marks `IsUserActive` — the session currently being viewed or
/// evaluated. If every session is active, `Tight`/`Critical` still refuse
/// new work and still reap the dead, but idle-shedding contributes nothing;
/// the daemon degrades honestly (refusing new sessions, saying why) rather
/// than silently killing the one thing the user is looking at.
module MemorySupervisor =

  /// Every fraction here is `available / total` machine memory — scale-free,
  /// so the same thresholds apply whether the box has 16GB or 512GB.
  type Thresholds = {
    /// At or below this fraction, start shedding idle sessions.
    ShedEnterFrac: float
    /// At or above this fraction, stop shedding — must exceed `ShedEnterFrac`.
    ShedExitFrac: float
    /// At or below this fraction, refuse new sessions outright.
    RefuseEnterFrac: float
    /// At or above this fraction, resume admitting — must exceed `RefuseEnterFrac`.
    RefuseExitFrac: float
    /// A session counts as idle-shedding-eligible once it has gone this long
    /// without activity.
    IdleAfter: TimeSpan
  }

  /// 20%/30%/8%/15% available, 30 minutes idle. Conservative on the refusal
  /// side deliberately: refusing a session is the most user-visible action
  /// this policy takes, so it only fires once memory is genuinely tight
  /// (under 8% available, and not resumed until back above 15%) — well
  /// before a machine with real headroom would ever see it, but early
  /// enough that the daemon degrades on its own terms instead of the OOM
  /// killer choosing for it.
  let defaultThresholds : Thresholds = {
    ShedEnterFrac = 0.20
    ShedExitFrac = 0.30
    RefuseEnterFrac = 0.08
    RefuseExitFrac = 0.15
    IdleAfter = TimeSpan.FromMinutes 30.0
  }

  /// Delegates to the shared `MemoryPressure.nextFromAvailableFrac` — the
  /// SAME hysteresis job 5's expensive-work lease reads, just addressed by
  /// this module's own field names (`ShedEnterFrac`/`RefuseEnterFrac`
  /// instead of `TightEnterFrac`/`CriticalEnterFrac`) so this module's
  /// already-tuned, already-tested `Thresholds` record didn't need to change
  /// shape for the shared type underneath it to change name.
  let nextLevel (t: Thresholds) (current: MemoryPressure) (availableFrac: float) : MemoryPressure =
    MemoryPressure.nextFromAvailableFrac
      { TightEnterFrac = t.ShedEnterFrac
        TightExitFrac = t.ShedExitFrac
        CriticalEnterFrac = t.RefuseEnterFrac
        CriticalExitFrac = t.RefuseExitFrac }
      current
      availableFrac

  /// What a session is doing, for shedding purposes only — NOT the full
  /// `SessionLifecycleStatus`. `Dead` covers both `Faulted` and `Stopped`
  /// (see `WorkerProtocol.SessionLifecycleStatus.isDead`): no live worker,
  /// nothing coming back on its own, safe to reap regardless of pressure.
  [<RequireQualifiedAccess>]
  type SessionMemoryStatus =
    | Active
    | Idle of idleFor: TimeSpan
    | Dead

  type SessionSnapshot = {
    Id: string
    Status: SessionMemoryStatus
    /// True for the session currently being viewed or evaluated — never a
    /// shedding target, at any pressure level. See the module doc comment.
    IsUserActive: bool
  }

  type MachineStats = {
    DaemonRssBytes: int64
    MachineAvailableBytes: int64
    MachineTotalBytes: int64
  }

  /// One action this policy can prescribe. The caller executes these; this
  /// module only ever decides, never touches a process or a session itself.
  [<RequireQualifiedAccess>]
  type ShedAction =
    /// Release retained state for sessions that are already gone — always
    /// safe, at any pressure level.
    | ReapDeadSessions of ids: string list
    /// Stop these sessions, oldest-idle first — only ever non-active ones.
    | StopIdleSessions of ids: string list
    /// Refuse a new session outright, with a reason fit to show a user or an
    /// agent (this IS the "clear reason" every refusal must carry).
    | RefuseNewSessions of reason: string

  type Decision = {
    Level: MemoryPressure
    /// In application order: reap, then stop, then refuse. Empty when there
    /// is nothing to do at all (no dead sessions, `Normal` level).
    Actions: ShedAction list
    /// Present exactly when `Actions` is non-empty — the evidence a health
    /// payload or a log line should carry alongside any of them.
    Reason: string option
  }

  let private availableFracOf (m: MachineStats) : float =
    match m.MachineTotalBytes with
    | 0L -> 1.0
    | total -> float m.MachineAvailableBytes / float total

  let private idleDurationOf =
    function
    | SessionMemoryStatus.Idle d -> Some d
    | SessionMemoryStatus.Active
    | SessionMemoryStatus.Dead -> None

  /// The one decision function. Pure: same `current`, `m`, `sessions` in,
  /// same `Decision` out, every time — replayable and DST-able exactly like
  /// `HealthAnomaly.step`.
  let step (t: Thresholds) (current: MemoryPressure) (m: MachineStats) (sessions: SessionSnapshot list) : Decision =
    let availableFrac = availableFracOf m
    let level = nextLevel t current availableFrac

    // Reaping is NOT gated on level: a dead session's retained state is
    // never worth keeping, whether the box is starving or has 90% free.
    let deadIds =
      sessions
      |> List.filter (fun s -> s.Status = SessionMemoryStatus.Dead)
      |> List.map (fun s -> s.Id)
    let reap =
      match deadIds with
      | [] -> []
      | ids -> [ ShedAction.ReapDeadSessions ids ]

    // Oldest-idle-first, and never a session the caller marked active.
    let idleOldestFirst =
      sessions
      |> List.filter (fun s -> not s.IsUserActive)
      |> List.choose (fun s ->
        idleDurationOf s.Status
        |> Option.filter (fun idleFor -> idleFor >= t.IdleAfter)
        |> Option.map (fun idleFor -> s.Id, idleFor))
      |> List.sortByDescending snd
      |> List.map fst

    let stop =
      match level with
      | MemoryPressure.Tight
      | MemoryPressure.Critical when not idleOldestFirst.IsEmpty -> [ ShedAction.StopIdleSessions idleOldestFirst ]
      | _ -> []

    let refuse =
      match level with
      | MemoryPressure.Critical ->
        [ ShedAction.RefuseNewSessions(
            sprintf
              "%.1f%% of machine memory available (at or below the %.0f%% floor) — wait for memory to free up or stop unused sessions and retry"
              (availableFrac * 100.0)
              (t.RefuseEnterFrac * 100.0)
          ) ]
      | _ -> []

    let actions = reap @ stop @ refuse
    let reason =
      match actions with
      | [] -> None
      | _ -> Some(sprintf "memory pressure level=%A, %.1f%% of machine memory available" level (availableFrac * 100.0))
    { Level = level; Actions = actions; Reason = reason }
