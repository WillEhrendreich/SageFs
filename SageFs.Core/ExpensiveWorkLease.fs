namespace SageFs

open System
open System.IO

/// The daemon's coordination point for expensive, memory-costly work:
/// session create/warmup, a hard-reset rebuild, a full `dotnet build`, a
/// test-suite run, starting an app. One night, five agents each did one of
/// these against a single daemon, all at once — nobody was misbehaving,
/// nothing coordinated, and the daemon had no way to know five were
/// happening until its RSS was already at 55GB. This module is that missing
/// coordination point: ask before you spend the memory, and get back
/// Granted / Queued / Refused instead of finding out the hard way.
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
/// WHO HOLDS A LEASE: a lease is attributable to a `Holder`, the triple
/// (connection, agent name, working directory). Claude sub-agents of one
/// session share ONE MCP connection id, so a lease keyed by the connection
/// alone made every sibling look like the same holder: three agents were
/// refused for 17 to 35 minutes with "you already hold 1/1 leases" while a
/// sibling held it, and nothing said which one. A refusal now names the
/// leases that really hold the pool (who, what kind of work, when granted,
/// when it lapses), the caller's place in line, and what to do. Connection
/// identity itself is a larger design (the cohort member identity); this
/// module only makes the refusal truthful and the holder visible.
///
/// FAIRNESS: requests queue by arrival (a monotonic sequence number, not
/// wall-clock, so replay is exact) and are granted strictly in that order
/// once capacity exists — nobody who asked first waits behind somebody who
/// asked later. `maxPerHolder` caps how many leases any ONE holder may hold
/// at once, regardless of pressure, so a single busy agent cannot monopolize
/// an otherwise-quiet machine. An agent that never releases loses its lease
/// at `ExpiresAt` — reclaimed on the very next `request` call, not on a
/// timer of its own, so a crashed agent can never hold a slot forever. A
/// waiter that stops asking loses its place the same way, so a crashed agent
/// at the front of the line cannot hold up everyone behind it.
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
    /// `Rebuild`/`FullBuild` are `Timeouts.buildCompletion`, the build kill
    /// timer; `RunApp` is long-running by nature.
    let defaultTtl =
      function
      | Kind.SessionCreateOrWarmup -> Timeouts.leaseTtlSessionCreate
      | Kind.Rebuild -> Timeouts.leaseTtlRebuild
      | Kind.FullBuild -> Timeouts.leaseTtlFullBuild
      | Kind.TestSuiteRun -> Timeouts.leaseTtlTestSuite
      | Kind.RunApp -> Timeouts.leaseTtlRunApp

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

  /// The name an agent gave itself. Self-declared and spoofable, which is
  /// fine here: it labels a lease and separates siblings, it grants nothing.
  [<RequireQualifiedAccess>]
  type AgentName =
    | Named of string
    | Anonymous

  /// Where the holder says it is working. Part of the identity so two
  /// sub-agents in two worktrees are two holders even before they name
  /// themselves.
  [<RequireQualifiedAccess>]
  type WorkingDirectory =
    | In of string
    | Unspecified

  /// Who a lease is attributable to. Equality is the identity: the same
  /// connection with a different agent name, or the same agent name in
  /// another working directory, is a DIFFERENT holder.
  type Holder = { Connection: string; Agent: AgentName; Directory: WorkingDirectory }

  module Holder =
    /// Build a holder from the raw strings an MCP tool or HTTP caller gave.
    /// A blank name or directory is the absence of one, never a name.
    let make (connection: string) (agentName: string) (workingDirectory: string) : Holder =
      { Connection = connection
        Agent =
          (match String.IsNullOrWhiteSpace agentName with
           | true -> AgentName.Anonymous
           | false -> AgentName.Named(agentName.Trim()))
        Directory =
          (match String.IsNullOrWhiteSpace workingDirectory with
           | true -> WorkingDirectory.Unspecified
           | false -> WorkingDirectory.In(Path.TrimEndingDirectorySeparator(workingDirectory.Trim()))) }

    /// A caller that knows only one name for itself (the HTTP lease
    /// endpoint, the guard hook, the daemon's own create/rebuild asks).
    let ofConnection (connection: string) : Holder = make connection "" ""

    let agentLabel (holder: Holder) : string =
      match holder.Agent with
      | AgentName.Named name -> name
      | AgentName.Anonymous -> "(unnamed)"

    let directoryLabel (holder: Holder) : string =
      match holder.Directory with
      | WorkingDirectory.In directory -> directory
      | WorkingDirectory.Unspecified -> "(unspecified)"

    /// One phrase for a refusal or a log line.
    let describe (holder: Holder) : string =
      let agent =
        match holder.Agent with
        | AgentName.Named name -> sprintf "agent '%s'" name
        | AgentName.Anonymous -> "an unnamed agent"
      match holder.Directory with
      | WorkingDirectory.In directory -> sprintf "%s on connection %s in %s" agent holder.Connection directory
      | WorkingDirectory.Unspecified -> sprintf "%s on connection %s" agent holder.Connection

  type ActiveLease = {
    Id: LeaseId
    Kind: Kind
    Holder: Holder
    GrantedAt: DateTimeOffset
    ExpiresAt: DateTimeOffset
  }

  /// One pending ask, keyed by (Holder, Kind) — a caller that calls
  /// `request` again for the SAME (holder, kind) before getting `Granted`
  /// is treated as a retry of the SAME queued ask, not a second one, so
  /// polling never creates duplicate queue entries or lets one holder cut
  /// the line by asking twice.
  type QueuedRequest = {
    Holder: Holder
    Kind: Kind
    /// Monotonic arrival order — FIFO by this, not by wall-clock, so replay
    /// from a seed is exact regardless of clock resolution.
    Seq: int64
    FirstAskedAt: DateTimeOffset
    /// The last time the holder asked. An ask that is not repeated within
    /// `Timeouts.leaseAskStaleAfter` is abandoned and loses its place.
    LastAskedAt: DateTimeOffset
  }

  /// Why a queued caller is not running yet.
  [<RequireQualifiedAccess>]
  type WaitReason =
    /// The pool is at its cap for the current pressure (a cap of 0 at Critical).
    | AtCapacity
    /// There is room, but earlier asks are served first.
    | NotYourTurn

  /// Everything a queued caller needs to know, as data. `Holding` is the
  /// set of leases that really occupy the pool at the moment of the answer.
  type Waiting = {
    Position: int
    Kind: Kind
    Ahead: QueuedRequest list
    Holding: ActiveLease list
    Cap: int
    Pressure: MemoryPressure
    Why: WaitReason
    RetryAfter: TimeSpan
  }

  /// Why a request will not be queued: waiting does not help, something the
  /// caller itself holds or has asked for is in the way.
  [<RequireQualifiedAccess>]
  type Refusal =
    /// The holder already holds a lease of ANOTHER kind. Releasing it is what
    /// unblocks this, not time.
    | HoldsOtherKind of held: ActiveLease * asked: Kind
    /// The holder already has an ask queued for another kind.
    | OtherKindQueued of queued: Kind * asked: Kind

  [<RequireQualifiedAccess>]
  type Decision =
    /// The work may proceed now. `expiresAt` is when the lease is reclaimed
    /// if `release` is never called.
    | Granted of LeaseId * expiresAt: DateTimeOffset
    /// This holder already holds a lease of this kind: here it is, unchanged.
    /// Asking again never renews it and never takes a second one.
    | AlreadyHeld of ActiveLease
    /// Not now. See `Waiting` for the place in line and who holds the pool.
    | Queued of Waiting
    | Refused of Refusal

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
    | MemoryPressure.Normal -> Timeouts.leaseRetryAfterNormal
    | MemoryPressure.Tight -> Timeouts.leaseRetryAfterTight
    | MemoryPressure.Critical -> Timeouts.leaseRetryAfterCritical

  /// Full jitter, derived from the request's own identity so two different
  /// requesters land at two different points in the window — a fleet
  /// refused at the same instant fans out across it instead of every one of
  /// them retrying on the identical tick (the classic thundering-herd
  /// failure of backoff without jitter; see AWS's "Exponential Backoff and
  /// Jitter"). The draw depends only on `holder`/`kind`/`seq`, never on a
  /// random source, so one request always draws the same point.
  let private minRetryAfter = Timeouts.leaseMinRetryAfter

  let private withJitter (holder: Holder) (kind: Kind) (seq: int64) (computed: TimeSpan) : TimeSpan =
    let h = HashCode.Combine(holder, kind, seq)
    let frac = float (uint32 h) / float UInt32.MaxValue
    // Floored so "jittered down to (near) zero" can never read as "retry
    // immediately" — a Wait always costs the caller at least a beat.
    List.max [ minRetryAfter; TimeSpan.FromTicks(int64 (frac * float computed.Ticks)) ]

  /// How many of the longest retry windows (`Timeouts.leaseRetryAfterCritical`) a queued ask may miss before the
  /// pool stops holding a slot for it.
  let graceInRetryWindows = 2.0

  /// How far past the retry-after it was handed a queued ask may be before the pool stops holding a slot for it.
  /// A caller that has not come back this long after it was told to is treated as gone for the purpose of
  /// granting: the asker behind it is served, and the ask keeps its place in line until it ages out at
  /// `Timeouts.leaseAskStaleAfter`, so a caller that was only slow is served first when it returns. Derived from
  /// the longest retry-after the pool hands out, not a new duration: an agent that spends a turn thinking between
  /// two asks is not skipped, and a ghost costs this much instead of the 5 minutes it used to.
  let askGrace : TimeSpan = Timeouts.leaseRetryAfterCritical * graceInRetryWindows

  /// The real reclaim: drop every lease whose `ExpiresAt` has passed, and
  /// every queued ask that has not been repeated within
  /// `Timeouts.leaseAskStaleAfter`.
  let private reapExpired (now: DateTimeOffset) (state: PoolState) : PoolState =
    { state with
        Active = state.Active |> List.filter (fun l -> l.ExpiresAt > now)
        Queue = state.Queue |> List.filter (fun q -> q.LastAskedAt + Timeouts.leaseAskStaleAfter > now) }

  /// What a repeat ask for a kind the holder already holds does.
  [<RequireQualifiedAccess>]
  type private ReAsk =
    /// Hand back the lease it already has.
    | ReturnExisting
    /// Take another one (the twin that breaks idempotence).
    | GrantAnother

  /// The three places the real pool and its twins differ, injected so every
  /// twin shares every other line of the real decision logic.
  type private Policy = {
    /// Whether leases and stale asks are reclaimed. Without it a crashed
    /// agent deadlocks the pool forever.
    Reap: DateTimeOffset -> PoolState -> PoolState
    /// How a caller is identified. The real pool uses the holder as given;
    /// the collapsed twin keeps only the connection, as the pool did before.
    Identify: Holder -> Holder
    ReAsk: ReAsk
  }

  let private realPolicy : Policy = { Reap = reapExpired; Identify = id; ReAsk = ReAsk.ReturnExisting }

  let private requestCore (policy: Policy) (now: DateTimeOffset) (pressure: MemoryPressure) (state: PoolState) (asker: Holder) (kind: Kind) : PoolState * Decision =
    let state = policy.Reap now state
    let holder = policy.Identify asker
    let mine = state.Active |> List.filter (fun l -> l.Holder = holder)
    let holdsThisKind = mine |> List.tryFind (fun l -> l.Kind = kind)
    match holdsThisKind, policy.ReAsk with
    | Some lease, ReAsk.ReturnExisting -> state, Decision.AlreadyHeld lease
    | _ ->
      // A holder may have AT MOST ONE outstanding claim on the pool at a
      // time — active OR queued, and for ANY kind. Counting only active
      // leases here would let a holder queue several DIFFERENT kinds at
      // once (ask for a build, then — before that resolves — ask for a test
      // run too): each ask alone looks fine, but the holder now has two
      // outstanding claims, which is hold-and-wait in every way that
      // matters even though neither claim is "active" yet. One claim per
      // holder, full stop, is what actually keeps "one lease per member at a
      // time" true.
      let otherKindHeld =
        match policy.ReAsk with
        | ReAsk.ReturnExisting -> mine
        | ReAsk.GrantAnother -> mine |> List.filter (fun l -> l.Kind <> kind)
      let otherKindQueued = state.Queue |> List.tryFind (fun q -> q.Holder = holder && q.Kind <> kind)
      // Refused, not queued: what unblocks this is THIS holder releasing its
      // active lease or resolving its other outstanding ask, not time
      // passing or pressure easing — so drop any stale queue entry of
      // theirs for THIS kind rather than let it linger (their other
      // pending kind, if any, is untouched: that is the ask actually still
      // outstanding).
      let withoutMyEntry = { state with Queue = state.Queue |> List.filter (fun q -> not (q.Holder = holder && q.Kind = kind)) }
      match otherKindHeld, otherKindQueued with
      | lease :: _, _ when List.length otherKindHeld >= maxPerHolder ->
        withoutMyEntry, Decision.Refused(Refusal.HoldsOtherKind(lease, kind))
      | _, Some other -> withoutMyEntry, Decision.Refused(Refusal.OtherKindQueued(other.Kind, kind))
      | _ ->
        let existing = state.Queue |> List.tryFind (fun q -> q.Holder = holder && q.Kind = kind)
        let mySeq, queueWithMine, nextSeq =
          match existing with
          | Some q ->
            q.Seq,
            state.Queue |> List.map (fun e -> if e.Seq = q.Seq then { e with LastAskedAt = now } else e),
            state.NextSeq
          | None ->
            let seq = state.NextSeq
            seq,
            state.Queue @ [ { Holder = holder; Kind = kind; Seq = seq; FirstAskedAt = now; LastAskedAt = now } ],
            state.NextSeq + 1L
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
          let why =
            match capacityAvailable with
            | true -> WaitReason.NotYourTurn
            | false -> WaitReason.AtCapacity
          let waiting : Waiting =
            { Position = position + 1
              Kind = kind
              Ahead = sortedQueue |> List.filter (fun q -> q.Seq < mySeq)
              Holding = state.Active |> List.sortBy (fun l -> l.GrantedAt, l.ExpiresAt)
              Cap = cap
              Pressure = pressure
              Why = why
              RetryAfter = withJitter holder kind mySeq computed }
          { state with Queue = sortedQueue; NextSeq = nextSeq }, Decision.Queued waiting

  /// The one decision function. Pure: same `now`/`pressure`/`state`/
  /// `holder`/`kind` in, same `(state', Decision)` out, every time (a granted
  /// lease's id is the one fresh GUID) — replayable and DST-able exactly
  /// like `HealthAnomaly.step`/`MemorySupervisor.step`. `holder` identifies
  /// the AGENT (not a fresh id per call), so a queued-then-retry sequence is
  /// recognized as the SAME ask.
  let request = requestCore realPolicy

  /// TWIN: identical decision logic, but NEVER reclaims an expired lease or a
  /// stale ask. An agent that crashes without releasing holds its slot
  /// FOREVER under this twin — which is exactly the failure `request`'s
  /// reaping step exists to prevent. Exists so "every lease expires" can be
  /// shown to have teeth: it must FAIL against this.
  let requestNeverExpiresTwin = requestCore { realPolicy with Reap = fun _ state -> state }

  /// TWIN: identifies a caller by its connection alone, as the pool did
  /// before. Two sub-agents on one connection become one holder, so one is
  /// told it holds the other's lease. Exists so "a refusal names the real
  /// holder" can be shown to have teeth.
  let requestCollapsedIdentityTwin =
    requestCore { realPolicy with Identify = fun h -> { h with Agent = AgentName.Anonymous; Directory = WorkingDirectory.Unspecified } }

  /// TWIN: a repeat ask for a kind the holder already holds takes a second
  /// lease instead of returning the first. Exists so "the same holder asking
  /// again is idempotent" and "one holder never holds two" can be shown to
  /// have teeth.
  let requestDuplicatesOnReAskTwin = requestCore { realPolicy with ReAsk = ReAsk.GrantAnother }

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

  /// Release only when the lease belongs to the supplied CONNECTION. The
  /// connection is the ownership boundary for MCP callers: a guessed or
  /// stolen lease id must not let one connection release another
  /// connection's work. Sub-agents that share a connection can release each
  /// other's leases by id, because from the daemon they are one caller; the
  /// id is the capability, and only the holder is told it.
  let releaseOwned (connection: string) (leaseId: LeaseId) (state: PoolState) : PoolState * ReleaseOutcome =
    match state.Active |> List.tryFind (fun l -> l.Id = leaseId) with
    | Some lease when lease.Holder.Connection = connection -> release leaseId state
    | _ -> state, ReleaseOutcome.AlreadyGone

  // ---------------------------------------------------------------------
  // Words. One place turns a decision into what an agent reads.
  // ---------------------------------------------------------------------

  let private clock (at: DateTimeOffset) : string = at.ToUniversalTime().ToString "HH:mm:ss'Z'"

  /// How long until `at`, in the two largest units that matter.
  let private span (remaining: TimeSpan) : string =
    match remaining <= TimeSpan.Zero with
    | true -> "now"
    | false ->
      match int remaining.TotalHours, remaining.Minutes, remaining.Seconds with
      | 0, 0, seconds -> sprintf "%ds" seconds
      | 0, minutes, seconds -> sprintf "%dm %ds" minutes seconds
      | hours, minutes, _ -> sprintf "%dh %dm" hours minutes

  let private describeLease (now: DateTimeOffset) (lease: ActiveLease) : string =
    sprintf
      "%s held by %s, granted %s, expires %s (in %s)"
      (Kind.describe lease.Kind)
      (Holder.describe lease.Holder)
      (clock lease.GrantedAt)
      (clock lease.ExpiresAt)
      (span (lease.ExpiresAt - now))

  /// A lease described to the one who holds it: no need to name the holder.
  let private describeOwn (now: DateTimeOffset) (lease: ActiveLease) : string =
    sprintf
      "%s lease %s, granted %s, expires %s (in %s)"
      (Kind.describe lease.Kind)
      (LeaseId.value lease.Id)
      (clock lease.GrantedAt)
      (clock lease.ExpiresAt)
      (span (lease.ExpiresAt - now))

  /// What an agent reads for a decision, at `now`. Names the holder, the
  /// kind of work, when it was granted and when it lapses, the caller's
  /// place in line, and what to do about it.
  let explain (now: DateTimeOffset) (decision: Decision) : string =
    match decision with
    | Decision.Granted(leaseId, expiresAt) ->
      sprintf
        "Granted: lease %s, expires %s (in %s). Release it with release_work_lease when the work finishes."
        (LeaseId.value leaseId)
        (clock expiresAt)
        (span (expiresAt - now))
    | Decision.AlreadyHeld lease ->
      sprintf
        "You already hold this lease: %s. Asking again does not renew it, and you were handed the same id. It is held as %s: if another agent shares that connection, agent_name and working_directory, it shares this lease, so pass a distinct agent_name to get your own."
        (describeOwn now lease)
        (Holder.describe lease.Holder)
    | Decision.Queued waiting ->
      let holding =
        match waiting.Holding, waiting.Why with
        | [], _ -> "No lease is out: the wait is the memory pressure alone."
        | held, WaitReason.AtCapacity -> sprintf "Held by: %s." (held |> List.map (describeLease now) |> String.concat "; ")
        | held, WaitReason.NotYourTurn -> sprintf "Running now: %s." (held |> List.map (describeLease now) |> String.concat "; ")
      let ahead =
        match waiting.Ahead with
        | [] -> ""
        | asks ->
          sprintf " %d ask(s) are queued before yours: %s." (List.length asks) (asks |> List.map (fun q -> Holder.describe q.Holder) |> String.concat ", ")
      sprintf
        "Queued: %d lease(s) active (cap %d at %s pressure: %s). You are #%d in line for %s.%s %s Ask again in about %.0fs; asking again keeps your place. You cannot release another agent's lease: wait for it to finish or expire. If one of these is yours, release it with release_work_lease and its lease id."
        (List.length waiting.Holding)
        waiting.Cap
        (MemoryPressure.describe waiting.Pressure)
        (MemoryPressure.explain waiting.Pressure)
        waiting.Position
        (Kind.describe waiting.Kind)
        ahead
        holding
        (Math.Ceiling waiting.RetryAfter.TotalSeconds)
    | Decision.Refused(Refusal.HoldsOtherKind(held, asked)) ->
      sprintf
        "Refused: you are %s, and that holder already holds a %s, so you cannot also take a %s lease. One lease per holder at a time: release it with release_work_lease (lease_id=%s) first. If you never took it, another agent sharing your connection, agent_name and working_directory did: pass a distinct agent_name (and your working_directory) so each of you is its own holder."
        (Holder.describe held.Holder)
        (describeOwn now held)
        (Kind.describe asked)
        (LeaseId.value held.Id)
    | Decision.Refused(Refusal.OtherKindQueued(queued, asked)) ->
      sprintf
        "Refused: you already have a queued ask for %s, so you cannot also ask for %s. One outstanding ask per holder at a time: keep asking for the queued one, or stop asking and it lapses after %.0f minutes."
        (Kind.describe queued)
        (Kind.describe asked)
        Timeouts.leaseAskStaleAfter.TotalMinutes

  /// For observability — `get_daemon_status`'s view of the pool: how many
  /// leases are out and how deep the queue is, after reclaiming anything
  /// expired. One row per holder, so two sub-agents on one connection are two
  /// rows, each with its agent, directory, kind and how long it has left.
  let snapshot (now: DateTimeOffset) (state: PoolState) =
    let live = reapExpired now state
    {|
      ActiveCount = List.length live.Active
      QueueDepth = List.length live.Queue
      Active =
        live.Active
        |> List.sortBy (fun lease -> lease.GrantedAt, lease.Holder.Connection, Kind.toToken lease.Kind)
        |> List.map (fun lease ->
          {|
            Connection = lease.Holder.Connection
            AgentName = Holder.agentLabel lease.Holder
            WorkingDirectory = Holder.directoryLabel lease.Holder
            Kind = Kind.toToken lease.Kind
            GrantedAt = lease.GrantedAt
            ExpiresAt = lease.ExpiresAt
            ExpiresInSeconds = int (Math.Ceiling (lease.ExpiresAt - now).TotalSeconds)
          |})
      Queue =
        live.Queue
        |> List.sortBy (fun request -> request.Seq)
        |> List.mapi (fun index request ->
          {|
            Position = index + 1
            Connection = request.Holder.Connection
            AgentName = Holder.agentLabel request.Holder
            WorkingDirectory = Holder.directoryLabel request.Holder
            Kind = Kind.toToken request.Kind
            RequestedAt = request.FirstAskedAt
            LastAskedAt = request.LastAskedAt
          |})
    |}
