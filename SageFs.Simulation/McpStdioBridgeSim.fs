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
