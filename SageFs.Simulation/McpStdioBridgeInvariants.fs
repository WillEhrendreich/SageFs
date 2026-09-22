namespace SageFs.Simulation

open SageFs.McpBridge
open SageFs.Simulation.McpStdioBridgeSim

/// Named invariants for the stdio bridge's startup-race DST — each a stable
/// ID plus a `Trace -> Outcome`, mirroring `Invariants.fs`'s shape for the
/// supervision-core sim. Every invariant here is claimed to be a TRUE
/// property of `McpBridge.decide`; a failure against `run` is a genuine bug.
module McpStdioBridgeInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  /// Did `op` ever tell this bridge to close?
  let private wasClosed (bridge: int) (ops: BridgeOp list) =
    ops
    |> List.exists (fun op ->
      match op with
      | BridgeOp.ClientClose b -> b = bridge
      | BridgeOp.ProbeTick _
      | BridgeOp.ClientSend _
      | BridgeOp.ConnectionDies _ -> false)

  /// A client that spawns the bridge always ends up either connected or
  /// given a clear error — never hung, never a silent empty tool list. The
  /// `settle` phase in `run` already drove every bridge to a terminal
  /// AwaitingDaemon-exit, so this checks the FINAL state is `Ready` or
  /// `Fatal` for every bridge that was never told to close, and `Closed` is
  /// only acceptable for one that was.
  let connectedOrClearError: Invariant =
    { Id = "connected-or-clear-error"
      Description = "Every bridge instance ends Ready or Fatal, unless its own client closed stdin."
      Check =
        fun t ->
          let violations =
            t.Bridges
            |> Map.toList
            |> List.choose (fun (b, bt) ->
              match bt.State.Transport, wasClosed b t.Scenario.Ops with
              | TransportState.Ready _, _
              | TransportState.Fatal _, _
              | TransportState.Closed, true -> None
              | TransportState.AwaitingDaemon _, _ -> Some(sprintf "bridge %d is still AwaitingDaemon after settling — it would hang forever" b)
              | TransportState.Closed, false -> Some(sprintf "bridge %d closed on its own — nothing told it to" b))
          match violations with
          | [] -> Outcome.Holds
          | vs -> Outcome.Violated(String.concat "; " vs) }

  /// The daemon is never started twice by one bridge — `StartDaemonCount`
  /// per bridge instance is at most 1, no matter how the probes/results
  /// interleave with other events.
  let daemonStartedAtMostOncePerBridge: Invariant =
    { Id = "daemon-started-at-most-once-per-bridge"
      Description = "No single bridge instance emits Action.StartDaemon more than once."
      Check =
        fun t ->
          let offenders =
            t.Bridges |> Map.toList |> List.filter (fun (_, bt) -> bt.StartDaemonCount > 1)
          match offenders with
          | [] -> Outcome.Holds
          | os -> Outcome.Violated(sprintf "bridges started the daemon more than once: %A" (os |> List.map (fun (b, bt) -> b, bt.StartDaemonCount))) }

  /// Every bridge that ever personally observed the daemon as unreachable
  /// (and the daemon was not already up) attempts to start it — this is the
  /// invariant `McpStdioBridgeSim.Twin.decideNeverStarts` must VIOLATE,
  /// since that twin reproduces the real regression where the start
  /// transition was dead code.
  ///
  /// A bridge whose client closed stdin is exempt: `UnhealthyProbeCount`
  /// counts every `Event.ProbeResult false` the sim FED to a bridge, even
  /// one already `Closed` (where `decide` correctly no-ops it) — a client
  /// that disconnected before the daemon ever answered was never going to
  /// see a start attempt, on purpose, and that is not this invariant's
  /// concern (found live: a scenario closing stdin before any probe ever
  /// ran tripped this until the exemption was added).
  let daemonStartAttempted: Invariant =
    { Id = "daemon-start-attempted"
      Description = "If the daemon wasn't already up, every bridge whose client stayed connected through an unreachable probe tried to start it."
      Check =
        fun t ->
          match t.Scenario.Daemon.AlreadyUp with
          | true -> Outcome.Holds
          | false ->
            let offenders =
              t.Bridges
              |> Map.toList
              |> List.filter (fun (_, bt) ->
                match bt.State.Transport with
                | TransportState.Closed -> false
                | TransportState.Ready _
                | TransportState.Fatal _
                | TransportState.AwaitingDaemon _ -> bt.UnhealthyProbeCount > 0 && bt.StartDaemonCount = 0)
            match offenders with
            | [] -> Outcome.Holds
            | os -> Outcome.Violated(sprintf "bridges saw an unhealthy probe but never asked the daemon to start: %A" (os |> List.map fst)) }

  /// A message is never lost or duplicated: for every `ClientSend(b, msg)`
  /// scheduled, that EXACT message appears in bridge `b`'s action trace as
  /// exactly one `ForwardToDaemon` or exactly one `RejectMessage` — never
  /// zero, never two, never both.
  let messagesNeverLostOrDuplicated: Invariant =
    { Id = "messages-never-lost-or-duplicated"
      Description = "Every sent message is forwarded exactly once or rejected exactly once — never zero, never twice, never both."
      Check =
        fun t ->
          let sent =
            t.Scenario.Ops
            |> List.choose (fun op ->
              match op with
              | BridgeOp.ClientSend(b, msg) -> Some(b, msg)
              | BridgeOp.ProbeTick _
              | BridgeOp.ClientClose _
              | BridgeOp.ConnectionDies _ -> None)
          let countIn (actions: Action list) (msg: RpcMessage) =
            actions
            |> List.filter (fun a ->
              match a with
              | Action.ForwardToDaemon m -> m = msg
              | Action.RejectMessage(m, _) -> m = msg
              | Action.Probe
              | Action.StartDaemon
              | Action.ReportFatal _
              | Action.TerminateSession _
              | Action.Shutdown -> false)
            |> List.length
          let violations =
            sent
            |> List.choose (fun (b, msg) ->
              match t.Bridges.TryFind b with
              | None -> Some(sprintf "message for unknown bridge %d: %A" b msg)
              | Some bt ->
                match countIn bt.Actions msg with
                | 1 -> None
                | 0 -> Some(sprintf "bridge %d: message %A was never forwarded or rejected — LOST" b msg)
                | n -> Some(sprintf "bridge %d: message %A was actioned %d times — DUPLICATED" b msg n))
          match violations with
          | [] -> Outcome.Holds
          | vs -> Outcome.Violated(String.concat "; " vs) }

  let all: Invariant list =
    [ connectedOrClearError
      daemonStartedAtMostOncePerBridge
      daemonStartAttempted
      messagesNeverLostOrDuplicated ]

  let violations (t: Trace) : (Invariant * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv, msg))
