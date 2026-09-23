namespace SageFs

open System

/// The daemon's coordination point for expensive, memory-costly work:
/// session create/warmup, a hard-reset rebuild, a full `dotnet build`, a
/// test-suite run, starting an app. One night, five agents each did one of
/// these against a single daemon, all at once — nobody was misbehaving,
/// nothing coordinated, and the daemon had no way to know five were
/// happening until its RSS was already at 55GB. This module is that missing
/// coordination point: ask before you spend the memory, and get back
/// Granted / Wait / Refused instead of finding out the hard way.
///
/// WHY A LEASE BEATS A PRESSURE SIGNAL: a plain "here is the current
/// pressure" reading is something a caller can silently ignore — which is
/// exactly what happened. A LEASE covers work the daemon does NOT run
/// itself: an agent shelling out to its own `dotnet build` for a final gate
/// still has to ask for a lease first, so the daemon's accounting includes
/// memory it never spent a single byte of itself. That is the gap a
/// pressure signal alone cannot close.
///
/// `MemoryPressure` (shared with `MemorySupervisor`, job 4's session
/// shedding) drives the pool's admission ceiling: MORE concurrent expensive
/// work is allowed at `Normal`, only one at a time at `Tight`, and none at
/// all admitted fresh at `Critical` — existing work still runs to
/// completion or expiry, but nothing new starts until pressure eases.
///
/// FAIRNESS: requests queue by arrival (a monotonic sequence number, not
/// wall-clock, so replay is exact) and are granted strictly in that order
/// once capacity exists — nobody who asked first waits behind somebody who
/// asked later. `maxPerHolder` caps how many leases any ONE holder may hold
/// at once, regardless of pressure, so a single busy agent cannot monopolize
/// an otherwise-quiet machine. An agent that never releases loses its lease
/// at `ExpiresAt` — reclaimed on the very next `request` call, not on a
/// timer of its own, so a crashed agent can never hold a slot forever.
module ExpensiveWorkLease =

  /// The kinds of work worth leasing: the ones that actually cost real
  /// memory. Deliberately NOT `StopSession`/list/read operations — refusing
  /// those would block the very things that relieve pressure.
  [<RequireQualifiedAccess>]
  type Kind =
    | SessionCreateOrWarmup
    | Rebuild
    | FullBuild
    | TestSuiteRun
    | RunApp

  module Kind =
    let all = [ Kind.SessionCreateOrWarmup; Kind.Rebuild; Kind.FullBuild; Kind.TestSuiteRun; Kind.RunApp ]

    /// The exhaustive wire token for each kind — the one place a `Kind`
    /// becomes/comes from text (the HTTP lease endpoints, the guard hook).
    let toToken =
      function
      | Kind.SessionCreateOrWarmup -> "session_create_or_warmup"
      | Kind.Rebuild -> "rebuild"
      | Kind.FullBuild -> "full_build"
      | Kind.TestSuiteRun -> "test_suite_run"
      | Kind.RunApp -> "run_app"

    let tryParse (token: string) : Kind option = all |> List.tryFind (fun k -> toToken k = token)

    let describe =
      function
      | Kind.SessionCreateOrWarmup -> "session create/warmup"
      | Kind.Rebuild -> "hard-reset rebuild"
      | Kind.FullBuild -> "full dotnet build"
      | Kind.TestSuiteRun -> "test-suite run"
      | Kind.RunApp -> "run app"

    /// How long a granted lease is good for before it is reclaimed as
    /// abandoned. Real durations for a large repo, not a guess —
    /// `Rebuild`/`FullBuild` mirror `SessionBuild.fs`'s own 600s build kill
    /// timer; `RunApp` is long-running by nature.
    let defaultTtl =
      function
      | Kind.SessionCreateOrWarmup -> TimeSpan.FromMinutes 5.0
      | Kind.Rebuild -> TimeSpan.FromMinutes 10.0
      | Kind.FullBuild -> TimeSpan.FromMinutes 10.0
      | Kind.TestSuiteRun -> TimeSpan.FromMinutes 15.0
      | Kind.RunApp -> TimeSpan.FromHours 4.0

  /// Opaque lease identity — a caller can compare/release by it, never
  /// construct one, so "I made up my own lease id" cannot happen.
  type LeaseId = private LeaseId of string

  module LeaseId =
    let create () : LeaseId = LeaseId(Guid.NewGuid().ToString "N")
    let value (LeaseId s) = s

    /// Reconstruct a `LeaseId` from its wire form — the ONE legitimate use
    /// of the private constructor from outside this module: a `release`
    /// call arrives over HTTP carrying only the string a `Granted` decision
    /// handed out earlier, never a freshly-invented one. `release` itself
    /// is the actual safety net — a string that doesn't match any active
    /// lease (fabricated, stale, or someone else's) resolves to
    /// `ReleaseOutcome.AlreadyGone`, never to releasing the wrong lease.
    let ofWire (raw: string) : LeaseId = LeaseId raw

  type ActiveLease = {
    Id: LeaseId
    Kind: Kind
    Holder: string
    GrantedAt: DateTimeOffset
    ExpiresAt: DateTimeOffset
  }

  /// One pending ask, keyed by (Holder, Kind) — a caller that calls
  /// `request` again for the SAME (holder, kind) before getting `Granted`
  /// is treated as a retry of the SAME queued ask, not a second one, so
  /// polling never creates duplicate queue entries or lets one holder cut
  /// the line by asking twice.
  type QueuedRequest = {
    Holder: string
    Kind: Kind
    /// Monotonic arrival order — FIFO by this, not by wall-clock, so replay
    /// from a seed is exact regardless of clock resolution.
    Seq: int64
    FirstAskedAt: DateTimeOffset
  }

  [<RequireQualifiedAccess>]
  type Decision =
    /// The work may proceed now. `expiresAt` is when the lease is reclaimed
    /// if `release` is never called.
    | Granted of LeaseId * expiresAt: DateTimeOffset
    /// Not now — try again after `retryAfter`. `reason` says how many
    /// leases are out and at what pressure, so a caller can log something
    /// useful rather than just sleeping blindly.
    | Wait of retryAfter: TimeSpan * reason: string
    /// Waiting will not help: the holder already holds its per-holder cap.
    /// Releasing one of ITS OWN leases is what unblocks this, not time.
    | Refused of reason: string

  type PoolState = { Active: ActiveLease list; Queue: QueuedRequest list; NextSeq: int64 }

  let empty : PoolState = { Active = []; Queue = []; NextSeq = 0L }

  /// Max concurrently-GRANTED leases, by pressure — escalates DOWN as
  /// pressure rises. Zero at `Critical`: nothing new starts, but everything
  /// already granted keeps running to completion or expiry.
  let maxConcurrentFor =
    function
    | MemoryPressure.Normal -> 4
    | MemoryPressure.Tight -> 1
    | MemoryPressure.Critical -> 0

  /// Max leases any ONE holder may hold at once, at ANY pressure. Fixed at
  /// 1, deliberately: a holder that already holds a lease and asks for a
  /// SECOND one is refused outright rather than queued — hold-and-wait is
  /// exactly how a wait-for graph gets a cycle (a classic deadlock
  /// precondition, Coffman et al. 1971). One lease per member at a time
  /// makes that structurally impossible, the same "make illegal states
  /// unrepresentable" instinct this codebase already applies to domain
  /// types, applied here to a concurrency resource instead.
  let maxPerHolder = 1

  /// Retry-after base by pressure, before queue-position scaling and
  /// jitter. Jitter matters here specifically: several agents refused at
  /// the same instant (the exact shape of the incident this module exists
  /// for) would otherwise all wake up and retry in the same instant too —
  /// a synchronized retry stampede is what capped exponential backoff
  /// WITHOUT jitter is well known to produce (see AWS's "Exponential
  /// Backoff and Jitter"). `jitterFrac` spreads retries over a window
  /// instead of a single point in time.
  let private baseBackoff =
    function
    | MemoryPressure.Normal -> TimeSpan.FromSeconds 3.0
    | MemoryPressure.Tight -> TimeSpan.FromSeconds 10.0
    | MemoryPressure.Critical -> TimeSpan.FromSeconds 30.0

  /// Full jitter, DETERMINISTICALLY derived from the request's own identity
  /// (never `System.Random`, which would make `request` impure and break
  /// exact seed replay in the DST harness): retryAfter is drawn from
  /// `[0, computed]` at a point that depends only on `holder`/`kind`/`seq`,
  /// so two different requesters land at two different points in the
  /// window — a fleet refused at the same instant fans out across it
  /// instead of every one of them retrying on the identical tick (the
  /// classic thundering-herd failure of backoff without jitter; see AWS's
  /// "Exponential Backoff and Jitter").
  let private minRetryAfter = TimeSpan.FromMilliseconds 200.0

  let private withJitter (holder: string) (kind: Kind) (seq: int64) (computed: TimeSpan) : TimeSpan =
    let h = HashCode.Combine(holder, kind, seq)
    let frac = float (uint32 h) / float UInt32.MaxValue
    // Floored so "jittered down to (near) zero" can never read as "retry
    // immediately" — a Wait always costs the caller at least a beat.
    List.max [ minRetryAfter; TimeSpan.FromTicks(int64 (frac * float computed.Ticks)) ]

  /// The real reclaim: drop every lease whose `ExpiresAt` has passed.
  let private reapExpired (now: DateTimeOffset) (state: PoolState) : PoolState =
    { state with Active = state.Active |> List.filter (fun l -> l.ExpiresAt > now) }

  /// `reap` is injected so the never-expiring TWIN below can share every
  /// other line of the real decision logic — the only difference between
  /// "leases are reclaimed" and "a crashed agent deadlocks the pool
  /// forever" is whether this one step runs.
  let private requestCore
    (reap: DateTimeOffset -> PoolState -> PoolState)
    (now: DateTimeOffset)
    (pressure: MemoryPressure)
    (state: PoolState)
    (holder: string)
    (kind: Kind)
    : PoolState * Decision =
    let state = reap now state
    let holderActiveCount = state.Active |> List.filter (fun l -> l.Holder = holder) |> List.length
    // A holder may have AT MOST ONE outstanding claim on the pool at a
    // time — active OR queued, and for ANY kind. Counting only active
    // leases here would let a holder queue several DIFFERENT kinds at
    // once (ask for a build, then — before that resolves — ask for a test
    // run too): each ask alone looks fine, but the holder now has two
    // outstanding claims, which is hold-and-wait in every way that
    // matters even though neither claim is "active" yet. One claim per
    // holder, full stop, is what actually keeps "one lease per member at a
    // time" true.
    let holderQueuedForOtherKind = state.Queue |> List.exists (fun q -> q.Holder = holder && q.Kind <> kind)
    match holderActiveCount >= maxPerHolder || holderQueuedForOtherKind with
    | true ->
      // Refused, not Wait: what unblocks this is THIS holder releasing its
      // active lease or resolving its other outstanding ask, not time
      // passing or pressure easing — so drop any stale queue entry of
      // theirs for THIS kind rather than let it linger (their other
      // pending kind, if any, is untouched: that is the ask actually still
      // outstanding).
      let state' = { state with Queue = state.Queue |> List.filter (fun q -> not (q.Holder = holder && q.Kind = kind)) }
      let reason =
        match holderActiveCount >= maxPerHolder with
        | true -> sprintf "you already hold %d/%d leases — release one before requesting another" holderActiveCount maxPerHolder
        | false -> sprintf "you already have an outstanding request queued for a different kind — resolve that one first"
      state', Decision.Refused reason
    | false ->
      let existing = state.Queue |> List.tryFind (fun q -> q.Holder = holder && q.Kind = kind)
      let mySeq, queueWithMine, nextSeq =
        match existing with
        | Some q -> q.Seq, state.Queue, state.NextSeq
        | None ->
          let seq = state.NextSeq
          seq, state.Queue @ [ { Holder = holder; Kind = kind; Seq = seq; FirstAskedAt = now } ], state.NextSeq + 1L
      let sortedQueue = queueWithMine |> List.sortBy (fun q -> q.Seq)
      let cap = maxConcurrentFor pressure
      let capacityAvailable = List.length state.Active < cap
      let isFront =
        match List.tryHead sortedQueue with
        | Some f -> f.Seq = mySeq
        | None -> true
      match capacityAvailable && isFront with
      | true ->
        let leaseId = LeaseId.create ()
        let expiresAt = now + Kind.defaultTtl kind
        let lease = { Id = leaseId; Kind = kind; Holder = holder; GrantedAt = now; ExpiresAt = expiresAt }
        let queue' = sortedQueue |> List.filter (fun q -> q.Seq <> mySeq)
        { state with Active = lease :: state.Active; Queue = queue'; NextSeq = nextSeq }, Decision.Granted(leaseId, expiresAt)
      | false ->
        let position = sortedQueue |> List.findIndex (fun q -> q.Seq = mySeq)
        let computed = baseBackoff pressure + TimeSpan.FromSeconds(float position * 3.0)
        let retryAfter = withJitter holder kind mySeq computed
        let reason =
          sprintf
            "%d lease(s) active (cap %d at %s pressure) — you are #%d in queue for %s"
            (List.length state.Active)
            cap
            (MemoryPressure.describe pressure)
            (position + 1)
            (Kind.describe kind)
        { state with Queue = sortedQueue; NextSeq = nextSeq }, Decision.Wait(retryAfter, reason)

  /// The one decision function. Pure: same `now`/`pressure`/`state`/
  /// `holder`/`kind` in, same `(state', Decision)` out, every time —
  /// replayable and DST-able exactly like `HealthAnomaly.step`/
  /// `MemorySupervisor.step`. `holder` must identify the AGENT/COHORT
  /// MEMBER (not a fresh id per call), so a `Wait`-then-retry sequence is
  /// recognized as the SAME queued ask.
  let request = requestCore reapExpired

  /// TWIN: identical decision logic, but NEVER reclaims an expired lease.
  /// An agent that crashes without releasing holds its slot FOREVER under
  /// this twin — which is exactly the failure `request`'s reaping step
  /// exists to prevent. Exists so "an expired lease is always reclaimed"
  /// can be shown to have teeth: it must FAIL against this.
  let requestNeverExpiresTwin = requestCore (fun _ state -> state)

  /// Whether a release found a still-live lease. `AlreadyGone` is the
  /// fencing signal a pure coordination pool CAN actually give (it cannot
  /// stop a process mid-build the way a real fencing token can, per
  /// Kleppmann's "How to do distributed locking"): a caller that gets
  /// `AlreadyGone` back knows its lease had already expired and been
  /// reclaimed BEFORE it finished, which is grounds to treat whatever it
  /// just did as suspect rather than trustingly assuming it still held
  /// exclusive rights to do it.
  [<RequireQualifiedAccess>]
  type ReleaseOutcome =
    | Released
    | AlreadyGone

  /// Release a held lease early — the normal path once the work finishes.
  /// `AlreadyGone` is not an error: the lease may simply have expired and
  /// been reclaimed already (see `ReleaseOutcome`'s own doc comment for why
  /// a caller should still pay attention to which one it got back).
  let release (leaseId: LeaseId) (state: PoolState) : PoolState * ReleaseOutcome =
    match state.Active |> List.exists (fun l -> l.Id = leaseId) with
    | false -> state, ReleaseOutcome.AlreadyGone
    | true -> { state with Active = state.Active |> List.filter (fun l -> l.Id <> leaseId) }, ReleaseOutcome.Released

  /// For observability — `get_fsi_status`/a health payload's own view of
  /// the pool: how many leases are out and how deep the queue is, after
  /// reclaiming anything expired.
  let snapshot (now: DateTimeOffset) (state: PoolState) =
    let live = reapExpired now state
    {| ActiveCount = List.length live.Active; QueueDepth = List.length live.Queue |}
