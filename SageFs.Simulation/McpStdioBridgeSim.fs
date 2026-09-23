namespace SageFs.Simulation

open SageFs.McpBridge

/// DST for the `sagefs mcp` stdio<->HTTP startup race
/// (SageFs.Core/McpBridge.fs) — the whole reason this bridge exists: a
/// client spawning it must never be able to observe ECONNREFUSED, a cached
/// failure, or a hang, no matter how the daemon's absence/appearance/death
/// interleaves with the client's own messages.
///
/// Folds the REAL `McpBridge.decide` for up to a handful of independent
/// bridge instances (modelling "two clients spawn the bridge at the same
/// time") against one shared, seeded daemon-liveness oracle. Chaos is DATA:
/// a `Scenario` is a seed, a policy, the shared daemon's behavior, and an
/// explicit ordered op schedule — same scenario, identical trace, forever.
module McpStdioBridgeSim =

  /// One scheduled operation against one bridge instance.
  [<RequireQualifiedAccess>]
  type BridgeOp =
    /// This bridge's next scheduler tick: `Event.Probe`, immediately
    /// followed by the paired `Event.ProbeResult` the shared daemon oracle
    /// produces for the current tick — exactly how the real IO edge always
    /// eventually resolves a `Probe` action into a `ProbeResult` event, just
    /// without modelling network latency as a separately-reorderable event.
    | ProbeTick of bridge: int
    /// A client sends this bridge a message.
    | ClientSend of bridge: int * msg: RpcMessage
    /// The client's stdin closes.
    | ClientClose of bridge: int
    /// The daemon drops this bridge's connection mid-session (a POST or the
    /// SSE GET failed) — meaningless unless that bridge is already `Ready`,
    /// harmless (decide no-ops it) otherwise.
    | ConnectionDies of bridge: int

  /// The shared environment every bridge instance probes against, but never
  /// sees directly — only through its own `ProbeResult` events, exactly like
  /// production. `AlreadyUp` models "the daemon was already running";
  /// `WarmupTicks` models "how long after SOME bridge asked it to start
  /// before it answers healthy" (irrelevant when already up).
  type DaemonModel = { AlreadyUp: bool; WarmupTicks: int }

  type Scenario =
    { Seed: int
      Policy: Policy
      BridgeCount: int
      Daemon: DaemonModel
      Ops: BridgeOp list }

  /// One bridge instance's full trace: its final state, everything it ever
  /// decided to do (in order), and how many times it emitted `StartDaemon` —
  /// the field `daemonStartedAtMostOncePerBridge` reads directly, instead of
  /// re-deriving it from `Actions` at every invariant check.
  type BridgeTrace =
    { State: State
      Actions: Action list
      StartDaemonCount: int
      /// How many `Event.ProbeResult false` this bridge personally received
      /// — lets an invariant ask "did this bridge ever see the daemon as
      /// unreachable" without re-deriving it from `Actions`.
      UnhealthyProbeCount: int }

  type Trace =
    { Scenario: Scenario
      Bridges: Map<int, BridgeTrace>
      /// The tick at which the FIRST `StartDaemon` action from ANY bridge
      /// fired, if one ever did.
      DaemonStartedAtTick: int option }

  let private emptyBridgeTrace =
    { State = initial; Actions = []; StartDaemonCount = 0; UnhealthyProbeCount = 0 }

  let private countStartDaemon (actions: Action list) =
    actions |> List.filter (fun a -> a = Action.StartDaemon) |> List.length

  /// True if the shared daemon is healthy at `tick`, given when (if ever)
  /// any bridge has fired `StartDaemon` so far.
  let private daemonHealthy (daemon: DaemonModel) (startedAtTick: int option) (tick: int) =
    match daemon.AlreadyUp with
    | true -> true
    | false ->
      match startedAtTick with
      | Some t -> tick >= t + daemon.WarmupTicks
      | None -> false

  let private applyOne (decideFn: Policy -> State -> Event -> State * Action list) (policy: Policy) (bt: BridgeTrace) (ev: Event) : BridgeTrace =
    let state', actions = decideFn policy bt.State ev
    { State = state'
      Actions = bt.Actions @ actions
      StartDaemonCount = bt.StartDaemonCount + countStartDaemon actions
      UnhealthyProbeCount =
        bt.UnhealthyProbeCount
        + (match ev with
           | Event.ProbeResult false -> 1
           | _ -> 0) }

  /// Fold the whole scenario through `decideFn`, once per op, in the exact
  /// order `Ops` specifies, then settle: keep probing every bridge still
  /// `AwaitingDaemon` until it reaches `Ready` or `Fatal`, so "always ends up
  /// connected or given a clear error" is checked CONCLUSIVELY — not just
  /// against whatever the scripted `Ops` happened to cover. The settle
  /// recursion is bounded by `policy.MaxProbeAttempts` (every bridge's own
  /// `decide` forces a terminal exit from `AwaitingDaemon` within that many
  /// probes), so it always terminates.
  let private runWith (decideFn: Policy -> State -> Event -> State * Action list) (scenario: Scenario) : Trace =
    let apply = applyOne decideFn scenario.Policy

    let step (bridges: Map<int, BridgeTrace>, startedAtTick: int option) (tick: int, op: BridgeOp) =
      match op with
      | BridgeOp.ProbeTick b when bridges.ContainsKey b ->
        let bt = bridges.[b]
        let afterProbe = apply bt Event.Probe
        let healthy = daemonHealthy scenario.Daemon startedAtTick tick
        let afterResult = apply afterProbe (Event.ProbeResult healthy)
        let startedAtTick' =
          match startedAtTick, afterResult.StartDaemonCount > bt.StartDaemonCount with
          | Some t, _ -> Some t
          | None, true -> Some tick
          | None, false -> startedAtTick
        bridges.Add(b, afterResult), startedAtTick'
      | BridgeOp.ClientSend(b, msg) when bridges.ContainsKey b -> bridges.Add(b, apply bridges.[b] (Event.StdinLine msg)), startedAtTick
      | BridgeOp.ClientClose b when bridges.ContainsKey b -> bridges.Add(b, apply bridges.[b] Event.StdinClosed), startedAtTick
      | BridgeOp.ConnectionDies b when bridges.ContainsKey b ->
        bridges.Add(b, apply bridges.[b] (Event.HttpFailed "simulated connection drop")), startedAtTick
      | BridgeOp.ProbeTick _
      | BridgeOp.ClientSend _
      | BridgeOp.ClientClose _
      | BridgeOp.ConnectionDies _ -> bridges, startedAtTick // op targets a bridge index outside BridgeCount — ignored

    let rec settle (tick: int) (bridges: Map<int, BridgeTrace>) (startedAtTick: int option) =
      let stillWaiting =
        bridges
        |> Map.toList
        |> List.choose (fun (b, bt) ->
          match bt.State.Transport with
          | TransportState.AwaitingDaemon _ -> Some b
          | TransportState.Ready _
          | TransportState.Closed
          | TransportState.Fatal _ -> None)
      match stillWaiting with
      | [] -> bridges, startedAtTick
      | waiting ->
        let bridges', startedAtTick' =
          waiting |> List.fold (fun (bs, sat) b -> step (bs, sat) (tick, BridgeOp.ProbeTick b)) (bridges, startedAtTick)
        settle (tick + 1) bridges' startedAtTick'

    let initBridges = [ 0 .. scenario.BridgeCount - 1 ] |> List.map (fun i -> i, emptyBridgeTrace) |> Map.ofList

    let bridgesAfterOps, startedAtTick =
      scenario.Ops |> List.mapi (fun i op -> (i, op)) |> List.fold step (initBridges, None)

    let finalBridges, finalStartedAtTick = settle (List.length scenario.Ops) bridgesAfterOps startedAtTick

    { Scenario = scenario; Bridges = finalBridges; DaemonStartedAtTick = finalStartedAtTick }

  /// Run through the REAL `McpBridge.decide` — the subject under test.
  let run (scenario: Scenario) : Trace = runWith decide scenario

  /// THE TWIN — reproduces, at the behavioral level, a real regression found
  /// by hand while building this bridge: an earlier version of `decide` keyed
  /// the "first unhealthy result starts the daemon" transition off a
  /// `NotStarted` case that `Event.Probe` always left BEFORE the paired
  /// `ProbeResult` could ever arrive (the orchestrator always posts `Probe`
  /// before the probe's own result comes back), so that branch was dead
  /// code — no bridge ever called `StartDaemon`, no matter how many
  /// unhealthy probes it saw, and it just kept probing until the attempt
  /// cap forced a clean `Fatal`. Reproduced here by skipping exactly that
  /// one transition and falling back to the real `decide` for everything
  /// else, so this twin is the smallest change that reintroduces the actual
  /// bug rather than a synthetic one.
  module Twin =
    let decideNeverStarts (policy: Policy) (state: State) (event: Event) : State * Action list =
      match state.Transport, event with
      | TransportState.AwaitingDaemon(DaemonReadiness.Probing _), Event.ProbeResult false -> state, []
      | _ -> decide policy state event

  /// Run through the twin — used to prove `daemonStartAttempted`
  /// (McpStdioBridgeInvariants.fs) has teeth: it must FAIL here. It does
  /// NOT violate "always ends up connected or given a clear error", since
  /// the twin still reaches a clean `Fatal` — that is exactly why a second,
  /// separate invariant is needed to catch it.
  let runTwinNeverStarts (scenario: Scenario) : Trace = runWith Twin.decideNeverStarts scenario

  // ── The Mcp-Session-Id capture-ordering race (issue #138) ────────────────
  //
  // `McpBridge.decide` has no bug here: given ANY order of
  // `Event.SessionIdCaptured` vs the next `Event.StdinLine`, its transitions
  // are internally consistent with whatever order it's fed. The bug lives
  // one level up, in `McpStdioBridge.fs`'s IO edge: `forward` used to write
  // the daemon's response to stdout BEFORE returning the captured
  // `Mcp-Session-Id`, so `Event.SessionIdCaptured` reached the mailbox only
  // AFTER that write — and the client, reacting to the very same write,
  // could get its own next `Event.StdinLine` into the mailbox first.
  // Whether that happens depends entirely on how fast the client replies,
  // which is exactly why the issue's own "insert a 3-second sleep" makes it
  // pass and an instant client (Claude Code) reliably loses.
  //
  // This section makes that IO-edge ordering an explicit, testable INPUT
  // instead of an unexamined assumption: `CapturePolicy` names the two
  // orderings `forward` could use, `ClientSpeed` names how fast the client
  // turns around, `raceOrder` derives the resulting mailbox arrival order
  // from them (this is the one place "today's bug" vs "the fix" is
  // encoded, and it's the ONLY place — everything downstream folds the REAL
  // `decide`), and `foldSessionIdRace` replays that order through it to see
  // what actually gets forwarded.

  /// Which order `forward` posts the session id capture relative to writing
  /// the response to stdout — the entire bug, reduced to one flag.
  [<RequireQualifiedAccess>]
  type CapturePolicy =
    /// Today's code, before the fix: write first, capture-post after.
    /// Whether the client's own next message beats that post into the
    /// mailbox depends on `ClientSpeed`.
    | CaptureAfterWrite
    /// The fix: capture-post happens before any stdout write, in the same
    /// sequential task — the client physically cannot react before a write
    /// it hasn't seen yet, so the capture is unconditionally in the mailbox
    /// first, for every `ClientSpeed`.
    | CaptureBeforeWrite

  /// How fast the client sends its next request after reading the response
  /// that carried the new session id.
  [<RequireQualifiedAccess>]
  type ClientSpeed =
    /// Reads the response and replies before the bridge's own
    /// capture-posting continuation gets scheduled — a fast, local,
    /// already-warm client. This is what Claude Code does, and the issue's
    /// own Python repro reproduces it with no sleep at all.
    | Instant
    /// Replies well after the capture would have posted either way — what
    /// the issue's own "insert a 3-second pause" workaround produces.
    | Slow

  /// One arrival at the bridge's mailbox from the daemon side
  /// (`Event.SessionIdCaptured`) or the client side (`Event.StdinLine`).
  [<RequireQualifiedAccess>]
  type RaceEvent =
    | DaemonIssuesSessionId of string
    | ClientSends of RpcMessage

  /// The mailbox arrival order a given `(CapturePolicy, ClientSpeed)`
  /// produces for "the daemon just answered with a new session id, and the
  /// client is about to send its next request in reaction to that same
  /// answer." `CaptureBeforeWrite` is unconditionally safe: the capture is
  /// posted before the write the client is reacting to has even happened,
  /// so no `ClientSpeed` can ever put the client's message first.
  let raceOrder (policy: CapturePolicy) (speed: ClientSpeed) (sid: string) (next: RpcMessage) : RaceEvent list =
    match policy, speed with
    | CapturePolicy.CaptureBeforeWrite, _
    | CapturePolicy.CaptureAfterWrite, ClientSpeed.Slow -> [ RaceEvent.DaemonIssuesSessionId sid; RaceEvent.ClientSends next ]
    | CapturePolicy.CaptureAfterWrite, ClientSpeed.Instant -> [ RaceEvent.ClientSends next; RaceEvent.DaemonIssuesSessionId sid ]

  /// Fold the REAL `decide` over `events`, starting from `Ready None` (the
  /// state right after a response was written but before this bridge
  /// necessarily knows its session id) — for every `Action.ForwardToDaemon`
  /// it produces, record the session id in effect AT THAT MOMENT, computed
  /// exactly the way `McpStdioBridge.fs`'s `run` loop computes it for
  /// `performAsync` (`state'.Transport`'s captured id right after the event
  /// that produced the action). A violation here is a violation there too.
  let foldSessionIdRace (policy: Policy) (events: RaceEvent list) : (RpcMessage * string option) list =
    let toEvent =
      function
      | RaceEvent.DaemonIssuesSessionId sid -> Event.SessionIdCaptured sid
      | RaceEvent.ClientSends msg -> Event.StdinLine msg
    let step (state: State, forwards: (RpcMessage * string option) list) (re: RaceEvent) =
      let state', actions = decide policy state (toEvent re)
      let sessionIdNow = match state'.Transport with TransportState.Ready sid -> sid | _ -> None
      let forwards' =
        actions
        |> List.fold
          (fun acc action ->
            match action with
            | Action.ForwardToDaemon msg -> (msg, sessionIdNow) :: acc
            | _ -> acc)
          forwards
      state', forwards'
    let _, forwards = events |> List.fold step ({ Transport = TransportState.Ready None; Pending = [] }, [])
    List.rev forwards

  /// True if `next` — sent in reaction to the response that carried `sid` —
  /// was forwarded WITH that session id, under the given policy and client
  /// speed. This IS the invariant issue #138 exists to restore: once the
  /// daemon has issued a session id, no subsequent request is ever
  /// forwarded without it.
  let forwardedWithCapturedSessionId (policy: Policy) (capturePolicy: CapturePolicy) (speed: ClientSpeed) (sid: string) (next: RpcMessage) : bool =
    match foldSessionIdRace policy (raceOrder capturePolicy speed sid next) with
    | [ (msg, Some capturedSid) ] -> msg = next && capturedSid = sid
    | _ -> false
