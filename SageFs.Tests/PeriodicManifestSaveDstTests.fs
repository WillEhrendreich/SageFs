/// Deterministic simulation of the daemon's periodic-manifest-save crash/resume
/// guarantee: what `Timeouts.manifestSaveInterval` + `periodicManifestSave`
/// (DaemonMode.fs) + `ResumeDecision.decide` (SageFs.Core
/// Features/DaemonManifest.fs) together promise across a crash — without
/// booting a real daemon, spawning a process, or touching disk.
///
/// `DaemonResumeOutcomeTests` proved the real, cross-process claim once, at
/// real wall-clock cost (two daemons, one crash/restart cycle). This file
/// closes the gap that gate cannot afford to pay for on every seed: what
/// happens across MANY session lifecycles, MANY tick cadences, and crashes
/// landing at every possible offset from the last completed save — in
/// milliseconds, replayable by seed.
///
/// A virtual clock, not wall time: a scenario is a seeded schedule of session
/// Create/Stop/DirDeleted events plus a crash time, all as plain floats
/// ("virtual seconds"). Periodic-save ticks are derived deterministically at
/// `interval, 2*interval, 3*interval, ...` — the real `cacheSaveTimer`'s
/// one-shot, self-rescheduling shape (DaemonMode.fs ~2480-2506), with the
/// save callback itself treated as instantaneous (true in production: a
/// manifest write is milliseconds against a multi-second interval).
///
/// The REAL code is the subject at every step that matters, never a
/// reimplementation:
///   * `SageFs.Server.DaemonMode.liveSyncMutation` — the exact snapshot ->
///     mutation translation `periodicManifestSave` posts to the manifest
///     owner on every tick (DaemonMode.fs:520-529, 940-960).
///   * `SageFs.Features.DaemonManifest.ManifestMutation.apply` — the SAME
///     pure reducer `ManifestOwner.commit` applies (ManifestOwner.fs:128).
///     This DST never re-derives what a sync does to the manifest, only
///     WHEN a tick fires and WHAT is live at that moment.
///   * `SageFs.Features.DaemonManifest.DaemonManifestState.aliveSessions` and
///     `ResumeDecision.decide` — the exact functions the daemon's own startup
///     resume path calls (DaemonMode.fs:1370, 1411), fed the exact structural
///     shape (a `DaemonSessionRecord` list from a real `DaemonManifestState`).
///
/// Chaos is DATA: `fromSeed n` is a pure function of the seed, so `run` on
/// the same seed replays the identical trace forever (proven below).
///
/// TWIN: a scheduler that stops rescheduling once a save attempt raises —
/// the exact bug the real `cacheSaveCallback`'s `try ... finally
/// (reschedule)` (DaemonMode.fs ~2483-2500, comments W18/W27(R11-12)) exists
/// to prevent. Reintroducing it (reschedule only on the happy path, inside
/// the `try` rather than the `finally`) freezes the manifest at whatever it
/// held at the last successful tick, forever — an unbounded loss window
/// where the real scheduler guarantees a bounded one.
module SageFs.Tests.PeriodicManifestSaveDstTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.DaemonManifest

// ── Building the real inputs: minimal, valid, otherwise-irrelevant fields ──

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private handle : WorkerHandle = { Pid = 4242; Port = Some 5000 }

let private workingDirOf (sid: string) = sprintf "/work/%s" sid
let private projectsOf (sid: string) = [ sprintf "%s.fsproj" sid ]

/// A `SessionInfo` for `sid`, created at virtual second `createdAt`. Only
/// `Id`/`Projects`/`WorkingDirectory`/`CreatedAt` matter to
/// `DaemonMode.buildManifestState` — the rest are filled with fixed,
/// otherwise-inert values (mirrors `SessionDisplayMutationTests.mkInfo` /
/// `DaemonIntegrationTests`' own `SessionInfo` builders).
let private mkSessionInfo (sid: string) (createdAt: float) : SessionInfo =
  { Id = SageFs.Tests.SharedGenerators.testSessionId sid
    Name = None
    Projects = projectsOf sid
    WorkingDirectory = workingDirOf sid
    SolutionRoot = None
    CreatedAt = epoch.AddSeconds createdAt
    LastActivity = epoch.AddSeconds createdAt
    Status = SessionLifecycleStatus.Ready handle
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning }

/// The `QuerySnapshot` a periodic-save tick would read at this instant, for
/// exactly the sessions currently live (`sid -> createdAt`).
let private tickSnapshot (live: Map<string, float>) : SessionManager.QuerySnapshot =
  let sessions =
    live
    |> Map.toList
    |> List.map (fun (sid, createdAt) ->
      let info = mkSessionInfo sid createdAt
      info.Id, info)
    |> Map.ofList
  { SessionManager.QuerySnapshot.empty with Sessions = sessions }

// ── Chaos is data: events, a scenario, a seeded generator ─────────────────

[<RequireQualifiedAccess>]
type Event =
  | Create of sid: string
  | Stop of sid: string
  /// The session's working directory vanishes (deleted, moved) before crash —
  /// `ResumeDecision.decide` must forget it at restart even though the
  /// manifest still names it alive.
  | DirDeleted of sid: string

/// A fully-specified, replayable scenario. `Timeline` is ascending virtual
/// seconds; `PoisonTicks` names which 1-based tick indices have their save
/// attempt raise (modeling a transient write/IO failure inside the periodic
/// save callback).
type Scenario =
  { Seed: int
    IntervalSeconds: float
    Timeline: (float * Event) list
    CrashAt: float
    PoisonTicks: Set<int> }

/// Which scheduler behavior a trace was folded under.
[<RequireQualifiedAccess>]
type SchedulerBehavior =
  /// Production: `cacheSaveCallback`'s `try ... finally (reschedule)` —
  /// rescheduling is unconditional, so a poisoned tick only skips THAT
  /// cycle's save; the next tick still fires on schedule.
  | RealFinallyReschedule
  /// The bug the `finally` exists to prevent: reschedule lives on the happy
  /// path only, so a poisoned tick permanently stops every future tick.
  | TwinStopsOnFailure

let private describeBehavior = function
  | SchedulerBehavior.RealFinallyReschedule -> "real-finally-reschedule"
  | SchedulerBehavior.TwinStopsOnFailure -> "twin-stops-on-failure"

/// Seeded, deterministic schedule of Create/Stop/DirDeleted events across a
/// small session pool, followed by a crash. `fromSeed n` is a pure function
/// of `n`, so `run` on it replays identically forever. Occasionally poisons
/// one tick and pushes the crash several intervals past it, so the twin
/// sweep below has real teeth (not just a single hand-picked scenario).
let fromSeed (seed: int) : Scenario =
  let rnd = Random(seed)
  let interval = 2.0 + float (rnd.Next 5) // 2..6 virtual seconds
  let sessionIds = [ for i in 1 .. 4 -> sprintf "%08x" (i + 1) ]
  let mutable t = 0.0
  let bump () = t <- t + float (1 + rnd.Next 3)
  let events = ResizeArray<float * Event>()
  for sid in sessionIds do
    // Not every pool id is created this run — the "never showed up" case.
    match rnd.Next 10 with
    | 0 -> ()
    | _ ->
      bump ()
      events.Add(t, Event.Create sid)
      match rnd.Next 3 with
      | 0 ->
        bump ()
        events.Add(t, Event.Stop sid)
      | 1 ->
        bump ()
        events.Add(t, Event.DirDeleted sid)
      | _ -> ()
  bump ()
  let lastEventAt = t
  let ticksSoFar = max 1 (int (lastEventAt / interval) + 1)
  let hasPoison = rnd.Next 2 = 0
  let poisonTickIndex = 1 + rnd.Next ticksSoFar
  // When poisoned, push the crash several intervals further out: only then
  // does "the scheduler never ticks again" diverge from "the scheduler skips
  // one cycle and catches up next time" — the exact difference the twin
  // exists to expose.
  let extraIntervals = match hasPoison with true -> 2 + rnd.Next 3 | false -> rnd.Next 3
  let crashAt = lastEventAt + float (rnd.Next 4) + interval * float extraIntervals
  { Seed = seed
    IntervalSeconds = interval
    Timeline = events |> List.ofSeq |> List.sortBy fst
    CrashAt = crashAt
    PoisonTicks = match hasPoison with true -> Set.singleton poisonTickIndex | false -> Set.empty }

// ── Folding the REAL functions over the merged event/tick timeline ────────

[<RequireQualifiedAccess>]
type private Step =
  | Lifecycle of Event
  | Tick of index: int

/// Every event plus every derived tick up to (and a little past) `CrashAt`,
/// time-ordered. At a tied timestamp a lifecycle event lands before the tick
/// sharing its instant — the tick's snapshot read sees anything already in
/// flight at that moment, never something scheduled at the same tick but
/// logically after it.
let private mergedSteps (scenario: Scenario) : (float * Step) list =
  let maxTickCount = int (scenario.CrashAt / scenario.IntervalSeconds) + 2
  let ticks = [ for i in 1 .. maxTickCount -> float i * scenario.IntervalSeconds, Step.Tick i ]
  let lifecycle = scenario.Timeline |> List.map (fun (t, e) -> t, Step.Lifecycle e)
  (ticks @ lifecycle)
  |> List.sortBy (fun (t, step) -> t, (match step with Step.Lifecycle _ -> 0 | Step.Tick _ -> 1))

type private FoldState =
  { Live: Map<string, float>
    DirDeleted: Set<string>
    /// The manifest as of the LAST COMPLETED (non-poisoned) tick. This is
    /// exactly what a real crash leaves on disk: `ManifestOwner` only ever
    /// holds what a successful `Apply` produced.
    Durable: DaemonManifestState
    LastSaveAt: float option
    /// TwinStopsOnFailure only: once true, no further tick ever fires.
    Stopped: bool }

let private foldStep (behavior: SchedulerBehavior) (scenario: Scenario) (fs: FoldState) ((t, step): float * Step) : FoldState =
  match t > scenario.CrashAt with
  | true -> fs // the daemon is dead — nothing happens after the crash instant
  | false ->
    match step with
    // Session lifecycle is driven by a completely different subsystem (the
    // SessionManager mailbox) than the manifest-save timer — a wedged
    // scheduler (Stopped) never stops sessions from starting or stopping,
    // only from being SAVED. Only `Step.Tick` below respects `Stopped`.
    | Step.Lifecycle (Event.Create sid) -> { fs with Live = Map.add sid t fs.Live }
    | Step.Lifecycle (Event.Stop sid) -> { fs with Live = Map.remove sid fs.Live }
    | Step.Lifecycle (Event.DirDeleted sid) -> { fs with DirDeleted = Set.add sid fs.DirDeleted }
    | Step.Tick index ->
      match fs.Stopped with
      | true -> fs // TwinStopsOnFailure only: the scheduler wedged — no tick ever fires again
      | false ->
        let poisoned = Set.contains index scenario.PoisonTicks
        let fs' =
          match poisoned with
          | true -> fs // this cycle's save attempt raised — Durable/LastSaveAt untouched, exactly
                       // like the real callback's inner `with ex -> log ...` swallowing it.
          | false ->
            // periodicManifestSave, faithfully: build the snapshot of what is
            // live right now, translate it through the REAL liveSyncMutation,
            // apply it through the REAL, pure ManifestMutation.apply.
            let snapshot = tickSnapshot fs.Live
            let mutation = SageFs.Server.DaemonMode.liveSyncMutation snapshot None None
            { fs with
                Durable = ManifestMutation.apply mutation fs.Durable
                LastSaveAt = Some t }
        match behavior, poisoned with
        | SchedulerBehavior.TwinStopsOnFailure, true -> { fs' with Stopped = true }
        | _ -> fs'

let private runFold (behavior: SchedulerBehavior) (scenario: Scenario) : FoldState =
  mergedSteps scenario
  |> List.fold
    (foldStep behavior scenario)
    { Live = Map.empty; DirDeleted = Set.empty; Durable = DaemonManifestState.empty; LastSaveAt = None; Stopped = false }

/// The daemon startup resume path, faithfully: `aliveSessions` on the
/// manifest that survived the crash, then the REAL `ResumeDecision.decide`
/// per record — the exact two functions `DaemonMode`'s startup resume calls
/// (DaemonMode.fs:1370, 1411). Session ids are unique per working directory
/// in every generated scenario, so the daemon's (dir, projects) dedup step
/// is always a no-op and is not modeled here.
let private restart (durable: DaemonManifestState) (dirDeleted: Set<string>) : Set<string> =
  let directoryExists (dir: string) = dirDeleted |> Set.forall (fun sid -> workingDirOf sid <> dir)
  let fileExists (_: string) = true
  DaemonManifestState.aliveSessions durable
  |> List.choose (fun r ->
    match ResumeDecision.decide directoryExists fileExists r with
    | ResumeDecision.Resume _ -> Some r.SessionId
    | ResumeDecision.Forget _ -> None)
  |> Set.ofList

type Trace =
  { Scenario: Scenario
    Reducer: string
    Durable: DaemonManifestState
    LastSaveAt: float option
    DirDeleted: Set<string>
    Resumed: Set<string> }

let run (behavior: SchedulerBehavior) (scenario: Scenario) : Trace =
  let fs = runFold behavior scenario
  { Scenario = scenario
    Reducer = describeBehavior behavior
    Durable = fs.Durable
    LastSaveAt = fs.LastSaveAt
    DirDeleted = fs.DirDeleted
    Resumed = restart fs.Durable fs.DirDeleted }

// ── Invariants (a)/(b)/(c) ──────────────────────────────────────────────

let private aliveInDurable (t: Trace) : Set<string> =
  DaemonManifestState.aliveSessions t.Durable |> List.map (fun r -> r.SessionId) |> Set.ofList

/// (a) SAFE-RESUME: every session live at the last COMPLETED save, whose
/// project directory still exists at restart, is resumed — and nothing else
/// is (an exact match, not merely a superset).
let safeResume (t: Trace) : bool =
  let expected = aliveInDurable t |> Set.filter (fun sid -> not (Set.contains sid t.DirDeleted))
  t.Resumed = expected

/// (b) BOUNDED LOSS: the gap between the crash and the last completed save
/// never reaches a full save interval — so no change is EVER lost for
/// longer than `Timeouts.manifestSaveInterval`. (No completed save yet means
/// the crash landed inside the very first interval window.)
let boundedLoss (t: Trace) : bool =
  match t.LastSaveAt with
  | Some savedAt -> t.Scenario.CrashAt - savedAt < t.Scenario.IntervalSeconds
  | None -> t.Scenario.CrashAt < t.Scenario.IntervalSeconds

/// (c) NOTHING RESURRECTED: never resumes a session that is absent from, or
/// already stopped in, the manifest as of the last completed save.
let nothingStale (t: Trace) : bool =
  Set.isSubset t.Resumed (aliveInDurable t)

// ── Seeded sweep plumbing ──────────────────────────────────────────────

let private seeds = [ 1 .. 300 ]

let private describe (t: Trace) =
  sprintf
    "seed=%d reducer=%s interval=%.1f crashAt=%.2f poison=%A lastSaveAt=%A resumed=%A timeline=%A"
    t.Scenario.Seed t.Reducer t.Scenario.IntervalSeconds t.Scenario.CrashAt t.Scenario.PoisonTicks
    t.LastSaveAt t.Resumed t.Scenario.Timeline

/// Fail with the first few counterexamples (each fully replayable by seed)
/// and the total violation count, rather than a bare "should be empty".
let private expectNone (label: string) (bad: Trace list) =
  match bad with
  | [] -> ()
  | _ ->
    failtestf "%s: %d of %d seeds.\n%s" label bad.Length seeds.Length
      (bad |> List.truncate 5 |> List.map describe |> String.concat "\n")

[<Tests>]
let tests =
  testList "DST periodic manifest save / crash / resume" [

    testList "the real scheduler (try/finally reschedule) holds every invariant" [

      testCase "(a) SAFE-RESUME across seeded schedules" <| fun _ ->
        seeds
        |> List.map (fun s -> run SchedulerBehavior.RealFinallyReschedule (fromSeed s))
        |> List.filter (safeResume >> not)
        |> expectNone "a session was not resumed exactly as expected"

      testCase "(b) BOUNDED-LOSS across seeded schedules" <| fun _ ->
        seeds
        |> List.map (fun s -> run SchedulerBehavior.RealFinallyReschedule (fromSeed s))
        |> List.filter (boundedLoss >> not)
        |> expectNone "the loss window exceeded one save interval"

      testCase "(c) NOTHING-STALE across seeded schedules" <| fun _ ->
        seeds
        |> List.map (fun s -> run SchedulerBehavior.RealFinallyReschedule (fromSeed s))
        |> List.filter (nothingStale >> not)
        |> expectNone "a stopped/absent session was resumed"

      testCase "a poisoned tick (one cycle's save throws) still holds BOUNDED-LOSS — the next tick catches up" <| fun _ ->
        // Named, hand-picked reproduction: one session, tick 1 (t=5) poisoned,
        // crash at t=12 — between tick 2 (t=10, the recovery) and tick 3
        // (t=15). The real scheduler's unconditional reschedule means tick 2
        // still lands on schedule despite tick 1's failure.
        let scenario =
          { Seed = -1
            IntervalSeconds = 5.0
            Timeline = [ 1.0, Event.Create "00000002" ]
            CrashAt = 12.0
            PoisonTicks = Set.singleton 1 }
        let t = run SchedulerBehavior.RealFinallyReschedule scenario
        t.LastSaveAt |> Expect.equal "tick 2 (t=10) recovered after tick 1 (t=5) was poisoned" (Some 10.0)
        boundedLoss t |> Expect.isTrue "the gap from the crash (t=12) to the recovered save (t=10) is 2s — under the 5s interval"
    ]

    testList "TWIN — a scheduler that stops rescheduling after a poisoned save breaks BOUNDED-LOSS" [

      testCase "REPRODUCED — a poisoned tick freezes the manifest forever under the twin" <| fun _ ->
        // Same poisoned-tick-1 setup, but the crash lands at t=27 — more than
        // four intervals later. The real scheduler keeps ticking every 5s
        // and lands its last completed save at t=25 (2s before crash, well
        // inside the 5s budget). The twin never reschedules past the t=5
        // failure, so it never saves ANYTHING, ever.
        let scenario =
          { Seed = -2
            IntervalSeconds = 5.0
            Timeline = [ 1.0, Event.Create "00000002" ]
            CrashAt = 27.0
            PoisonTicks = Set.singleton 1 }
        let real = run SchedulerBehavior.RealFinallyReschedule scenario
        let twin = run SchedulerBehavior.TwinStopsOnFailure scenario
        real.LastSaveAt |> Expect.equal "the real scheduler kept ticking on schedule after the failure" (Some 25.0)
        twin.LastSaveAt |> Expect.equal "the twin never saved again after the poisoned first tick" None
        boundedLoss real |> Expect.isTrue "the real scheduler's loss window (2s) stays under the 5s interval"
        boundedLoss twin |> Expect.isFalse "the twin's loss window is the whole 27s run — nowhere near bounded by the 5s interval"

      testCase "the invariant has teeth: some seeded scenario violates BOUNDED-LOSS under the twin" <| fun _ ->
        let violating =
          seeds
          |> List.map (fun s -> run SchedulerBehavior.TwinStopsOnFailure (fromSeed s))
          |> List.filter (boundedLoss >> not)
        violating
        |> Expect.isNonEmpty "BOUNDED-LOSS must catch a scheduler that stops rescheduling after a failure"

      testCase "the twin still gets SAFE-RESUME / NOTHING-STALE right for whatever it DID manage to save" <| fun _ ->
        // The twin's bug is staleness, not corruption: whatever the last
        // (frozen) Durable snapshot says is still applied consistently —
        // this is what makes the bug insidious (nothing looks obviously
        // broken at restart; sessions just silently vanish).
        seeds
        |> List.map (fun s -> run SchedulerBehavior.TwinStopsOnFailure (fromSeed s))
        |> List.filter (fun t -> not (safeResume t) || not (nothingStale t))
        |> expectNone "even a frozen manifest must be applied consistently"
    ]

    testList "determinism / replay" [
      // `liveSyncMutation` (the REAL function this DST folds) stamps a
      // periodic (non-shutdown) sync's `StoppedAt` with `DateTimeOffset.UtcNow`
      // (DaemonMode.fs:527-528) — a genuinely wall-clock-dependent field, by
      // design: it is a real timestamp, not a logical one, and two
      // back-to-back runs of the identical scenario legitimately stamp
      // different instants. That is correct production behavior, not
      // nondeterminism in the scenario or the fold. `canonical` compares
      // everything the SEED actually controls — the scenario, the reducer,
      // virtual `LastSaveAt`, `Resumed`, and each record's `StoppedAt.IsSome`
      // (the only thing any invariant or the real `aliveSessions` reads) —
      // without asserting on the exact wall-clock instant, which was never
      // part of the seed's contract.
      let canonical (t: Trace) =
        t.Scenario, t.Reducer, t.LastSaveAt, t.DirDeleted, t.Resumed,
        t.Durable.ActiveSessionId,
        t.Durable.Sessions
        |> Map.map (fun _ r -> r.SessionId, r.Projects, r.WorkingDir, r.CreatedAt, r.StoppedAt.IsSome)

      testProperty "same seed => identical trace (real scheduler)" <| fun (seed: int) ->
        let a = run SchedulerBehavior.RealFinallyReschedule (fromSeed seed)
        let b = run SchedulerBehavior.RealFinallyReschedule (fromSeed seed)
        canonical a |> Expect.equal "replaying the same seed yields the identical trace" (canonical b)

      testProperty "same seed => identical trace (twin scheduler)" <| fun (seed: int) ->
        let a = run SchedulerBehavior.TwinStopsOnFailure (fromSeed seed)
        let b = run SchedulerBehavior.TwinStopsOnFailure (fromSeed seed)
        canonical a |> Expect.equal "replaying the same seed yields the identical trace" (canonical b)
    ]
  ]
