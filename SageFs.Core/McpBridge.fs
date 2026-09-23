namespace SageFs

open System.Text.Json

/// Pure decision core for the `sagefs mcp` stdio<->HTTP bridge — see
/// SageFs/McpStdioBridge.fs for the IO edges (probing the daemon, spawning
/// it, POSTing/GETting the streamable-HTTP endpoint, reading/writing
/// stdio) that drive this. Everything here is deterministic and side-effect
/// free: given a state and an event, it says what to do next. It never
/// touches the network, a process, or a clock — that is the whole point.
/// SageFs.Simulation folds THIS module (not a reimplementation of it)
/// through a seeded schedule of daemon-liveness events to prove the startup
/// race — the daemon absent, appearing late, slow, dying mid-session, two
/// clients racing to start it — can't hang the bridge or lose a message.
module McpBridge =

  /// JSON-RPC 2.0 message id. The spec allows a string or a number; a
  /// notification carries neither (see `RpcMessage.Notification`).
  [<RequireQualifiedAccess>]
  type RpcId =
    | S of string
    | N of int64

  /// What one line of stdio JSON-RPC is. Carries the raw text so the bridge
  /// forwards it byte-for-byte instead of re-serializing it — re-encoding a
  /// payload risks reformatting something a strict client or the daemon
  /// cares about (numeric precision, key order some SDKs still sniff).
  [<RequireQualifiedAccess>]
  type RpcMessage =
    | Request of id: RpcId * method: string * raw: string
    | Notification of method: string * raw: string
    | Response of id: RpcId * raw: string
    /// Not valid JSON, or valid JSON missing the fields JSON-RPC 2.0
    /// requires. Never thrown as an exception — the bridge has to keep
    /// running and tell the caller, not crash on one bad line.
    | Unparseable of raw: string * reason: string

  let rawOf (m: RpcMessage) : string =
    match m with
    | RpcMessage.Request(_, _, raw)
    | RpcMessage.Notification(_, raw)
    | RpcMessage.Response(_, raw)
    | RpcMessage.Unparseable(raw, _) -> raw

  /// Parse one line of stdio JSON-RPC. Pure and total: a parse failure
  /// becomes `Unparseable`, never an exception the caller has to guard.
  let parseRpcMessage (raw: string) : RpcMessage =
    try
      use doc = JsonDocument.Parse(raw)
      let root = doc.RootElement
      let idOpt =
        match root.TryGetProperty("id") with
        | true, idEl ->
          match idEl.ValueKind with
          | JsonValueKind.String -> Some(RpcId.S(idEl.GetString()))
          | JsonValueKind.Number -> Some(RpcId.N(idEl.GetInt64()))
          | _ -> None
        | false, _ -> None
      let methodOpt =
        match root.TryGetProperty("method") with
        | true, mEl when mEl.ValueKind = JsonValueKind.String -> Some(mEl.GetString())
        | _ -> None
      match idOpt, methodOpt with
      | Some id, Some m -> RpcMessage.Request(id, m, raw)
      | None, Some m -> RpcMessage.Notification(m, raw)
      | Some id, None -> RpcMessage.Response(id, raw)
      | None, None ->
        RpcMessage.Unparseable(
          raw,
          "no 'id' and no 'method' — not a JSON-RPC request, notification, or response"
        )
    with ex ->
      RpcMessage.Unparseable(raw, ex.Message)

  /// Where the bridge is in getting the daemon up and answering. One DU,
  /// not a bool "already started" flag next to an attempt counter that
  /// could drift out of sync with it — that drift is exactly how "started
  /// twice" bugs happen.
  [<RequireQualifiedAccess>]
  type DaemonReadiness =
    /// Probed `attempts` times; every result so far has been unreachable,
    /// and we have not yet asked anyone to start the daemon.
    | Probing of attempts: int
    /// We ourselves issued the daemon start; still probing for it to
    /// answer. `StartDaemon` is only ever emitted on the transition OUT of
    /// `Probing` into this case, and that transition happens at most once
    /// per bridge — `Probing` is never re-entered — so this case existing
    /// at all is proof the start happened at most once.
    | Starting of attempts: int

  /// The bridge's overall transport state.
  [<RequireQualifiedAccess>]
  type TransportState =
    | AwaitingDaemon of DaemonReadiness
    /// The daemon answered healthy. `sessionId` is `None` until the
    /// `initialize` response's `Mcp-Session-Id` header is captured.
    | Ready of sessionId: string option
    | Closed
    | Fatal of reason: string

  /// One bridge-state snapshot: the transport state, plus every stdin
  /// message that arrived before the daemon was reachable, held in arrival
  /// order. Reaching `Ready` for the first time drains this queue — each
  /// message forwarded exactly once, in order — so a message that shows up
  /// during the startup race is never lost and never sent twice.
  type State =
    { Transport: TransportState
      Pending: RpcMessage list }

  let initial: State =
    { Transport = TransportState.AwaitingDaemon(DaemonReadiness.Probing 0)
      Pending = [] }

  [<RequireQualifiedAccess>]
  type Event =
    /// The scheduler's clock tick: "probe the daemon now".
    | Probe
    | ProbeResult of healthy: bool
    | DaemonStartFailed of reason: string
    | StdinLine of RpcMessage
    | SessionIdCaptured of string
    /// The daemon answered a specific request with a protocol-level error
    /// (HTTP 4xx) — it is alive and reachable, only this ONE message was
    /// rejected. Distinct from `HttpFailed`: this must never move the
    /// bridge to `Fatal`, only answer the offending request.
    | RequestRejected of msg: RpcMessage * reason: string
    /// A POST or the SSE GET failed once the bridge was `Ready` — no usable
    /// response came back at all (connection refused, timed out, stream
    /// torn down). This IS "the daemon is gone."
    | HttpFailed of reason: string
    | StdinClosed

  /// What the bridge should DO. A DU, never a tuple of independent flags —
  /// "probe and also start the daemon" can't be represented, so `decide`
  /// choosing one IS choosing only one.
  [<RequireQualifiedAccess>]
  type Action =
    | Probe
    | StartDaemon
    | ForwardToDaemon of RpcMessage
    /// A message arrived while the bridge was closed or fatally stopped —
    /// never silently dropped, always reported so the IO edge can log it or
    /// answer the client with an explicit JSON-RPC error.
    | RejectMessage of RpcMessage * reason: string
    | ReportFatal of reason: string
    | TerminateSession of sessionId: string
    | Shutdown

  type Policy = { MaxProbeAttempts: int }

  /// 30 attempts — matches `Program.fs`'s existing `waitForDaemonReady`
  /// (30 x 500ms = 15s), which this bridge replaces the client-side half of.
  let defaultPolicy = { MaxProbeAttempts = 30 }

  let private giveUpReason (policy: Policy) =
    sprintf
      "the daemon did not answer healthy after %d probes — run 'sagefs check' to see what's wrong, or 'sagefs stop' if a stale one is holding the port"
      policy.MaxProbeAttempts

  let private drain (state: State) : State * Action list =
    let drained = state.Pending |> List.map Action.ForwardToDaemon
    { Transport = TransportState.Ready None; Pending = [] }, drained

  /// Move to `Fatal` and, in the SAME transition, reject every message that
  /// queued up during the race — a message still sitting in `Pending` when
  /// the bridge gives up would otherwise never be actioned again (no future
  /// event ever looks at it once we're Fatal): rot, not loss by a different
  /// name, but still a violation of "never lost." `ReportFatal` always comes
  /// first so the IO edge's top-level "why did this fail" line isn't buried
  /// under per-message rejection lines, and `Shutdown` always comes last:
  /// `run`'s doc comment promises "1 if the bridge ended Fatal" as something
  /// the caller actually GETS BACK, not something it has to wait for stdin
  /// to close to observe. Emitting it here — not only from `StdinClosed` —
  /// is what makes that true; without it a Fatal bridge just sat there doing
  /// nothing until an external kill (found live while verifying issue #138's
  /// second defect).
  let private giveUp (state: State) (reason: string) : State * Action list =
    let rejections = state.Pending |> List.map (fun m -> Action.RejectMessage(m, reason))
    { Transport = TransportState.Fatal reason; Pending = [] }, (Action.ReportFatal reason :: rejections) @ [ Action.Shutdown ]

  /// The one pure transition. `Action list` (not a single `Action`) because
  /// reaching `Ready` for the first time atomically drains everything
  /// queued during the race — draining is part of the SAME transition that
  /// reaches `Ready`, not a second event the IO edge could reorder against
  /// a fresh `StdinLine`.
  let decide (policy: Policy) (state: State) (event: Event) : State * Action list =
    match state.Transport, event with

    // ---- Probing / starting the daemon ----
    | TransportState.AwaitingDaemon(DaemonReadiness.Probing n), Event.Probe ->
      match n >= policy.MaxProbeAttempts with
      | true -> giveUp state (giveUpReason policy)
      | false ->
        { state with Transport = TransportState.AwaitingDaemon(DaemonReadiness.Probing(n + 1)) }, [ Action.Probe ]

    | TransportState.AwaitingDaemon(DaemonReadiness.Starting n), Event.Probe ->
      match n >= policy.MaxProbeAttempts with
      | true -> giveUp state (giveUpReason policy)
      | false ->
        { state with Transport = TransportState.AwaitingDaemon(DaemonReadiness.Starting(n + 1)) }, [ Action.Probe ]

    | TransportState.AwaitingDaemon _, Event.ProbeResult true -> drain state

    | TransportState.AwaitingDaemon(DaemonReadiness.Probing _), Event.ProbeResult false ->
      // First unhealthy answer we've ever gotten: start the daemon
      // ourselves. This is the ONLY place `Action.StartDaemon` is emitted,
      // and `Probing` is never re-entered once left, so one bridge instance
      // can never emit it twice.
      { state with Transport = TransportState.AwaitingDaemon(DaemonReadiness.Starting 0) }, [ Action.StartDaemon ]

    | TransportState.AwaitingDaemon(DaemonReadiness.Starting _), Event.ProbeResult false ->
      // Already asked for a start; never ask again. Keep waiting for the
      // next scheduled probe.
      state, []

    | TransportState.AwaitingDaemon(DaemonReadiness.Starting _), Event.DaemonStartFailed reason ->
      giveUp state (sprintf "could not start the daemon: %s" reason)

    | TransportState.AwaitingDaemon(DaemonReadiness.Probing _), Event.DaemonStartFailed _
    | TransportState.Ready _, Event.DaemonStartFailed _
    | TransportState.Closed, Event.DaemonStartFailed _
    | TransportState.Fatal _, Event.DaemonStartFailed _ ->
      // We never asked for a start in these states — a stray/late report,
      // no-op.
      state, []

    // ---- Message flow ----
    | TransportState.AwaitingDaemon _, Event.StdinLine msg ->
      // Never lost: queued for the drain the moment the daemon is Ready.
      { state with Pending = state.Pending @ [ msg ] }, []

    | TransportState.Ready _, Event.StdinLine msg -> state, [ Action.ForwardToDaemon msg ]

    | TransportState.Closed, Event.StdinLine msg -> state, [ Action.RejectMessage(msg, "stdin already closed") ]

    | TransportState.Fatal reason, Event.StdinLine msg ->
      state, [ Action.RejectMessage(msg, sprintf "bridge is fatally stopped: %s" reason) ]

    // ---- Session id capture (idempotent: captured exactly once) ----
    | TransportState.Ready None, Event.SessionIdCaptured sid -> { state with Transport = TransportState.Ready(Some sid) }, []

    | TransportState.Ready(Some _), Event.SessionIdCaptured _ ->
      // Already have one — ignored, not reapplied. Keeps "captured exactly
      // once" true even if the IO edge reads the header off more than one
      // response.
      state, []

    | TransportState.AwaitingDaemon _, Event.SessionIdCaptured _
    | TransportState.Closed, Event.SessionIdCaptured _
    | TransportState.Fatal _, Event.SessionIdCaptured _ -> state, []

    // ---- A protocol-level rejection of ONE request (never fatal) ----
    // Reuses `Action.RejectMessage` — the same shape already used for a
    // message the bridge can't forward for a state reason (queued-at-give-up,
    // Closed, Fatal) — rather than inventing a second "here's what's wrong
    // with your request" shape. The bridge stays exactly where it was.
    | TransportState.Ready _, Event.RequestRejected(msg, reason) -> state, [ Action.RejectMessage(msg, reason) ]

    | TransportState.AwaitingDaemon _, Event.RequestRejected _
    | TransportState.Closed, Event.RequestRejected _
    | TransportState.Fatal _, Event.RequestRejected _ ->
      // Can't happen — nothing is ever forwarded before Ready — but a stray
      // report here is a no-op, same posture as a stray HttpFailed.
      state, []

    // ---- Daemon dies mid-session ----
    | TransportState.Ready _, Event.HttpFailed reason ->
      let msg = sprintf "the daemon became unreachable: %s" reason
      // Shutdown here too — same reasoning as `giveUp`'s doc comment: Fatal
      // is a terminal state the process should actually leave on its own,
      // not one it sits in until stdin happens to close.
      { state with Transport = TransportState.Fatal msg }, [ Action.ReportFatal msg; Action.Shutdown ]

    | TransportState.AwaitingDaemon _, Event.HttpFailed _ ->
      // A probe's own failure arrives as `ProbeResult false`, not
      // `HttpFailed` — a stray report here is a no-op.
      state, []

    | TransportState.Closed, Event.HttpFailed _
    | TransportState.Fatal _, Event.HttpFailed _ -> state, []

    // ---- Shutdown ----
    | TransportState.Ready(Some sid), Event.StdinClosed ->
      { state with Transport = TransportState.Closed }, [ Action.TerminateSession sid; Action.Shutdown ]

    | TransportState.Ready None, Event.StdinClosed -> { state with Transport = TransportState.Closed }, [ Action.Shutdown ]

    | TransportState.AwaitingDaemon _, Event.StdinClosed ->
      // The client is gone before the daemon ever answered — nothing will
      // ever read a response again, but anything still queued must still be
      // ACCOUNTED for (rejected), not left to rot the same way an
      // un-drained `Pending` would on give-up.
      let rejections = state.Pending |> List.map (fun m -> Action.RejectMessage(m, "stdin closed before the daemon became reachable"))
      { Transport = TransportState.Closed; Pending = [] }, Action.Shutdown :: rejections

    | TransportState.Closed, Event.StdinClosed
    | TransportState.Fatal _, Event.StdinClosed -> state, []

    // ---- Stale scheduler ticks / probe results once past the race ----
    | TransportState.Ready _, Event.Probe
    | TransportState.Closed, Event.Probe
    | TransportState.Fatal _, Event.Probe -> state, []

    | TransportState.Ready _, Event.ProbeResult _
    | TransportState.Closed, Event.ProbeResult _
    | TransportState.Fatal _, Event.ProbeResult _ -> state, []

  /// True once the bridge will never move again on its own — the caller
  /// (real or simulated) has either connected or been told why not.
  let isTerminal (s: State) : bool =
    match s.Transport with
    | TransportState.Closed
    | TransportState.Fatal _ -> true
    | TransportState.AwaitingDaemon _
    | TransportState.Ready _ -> false

  /// True once messages can flow — used by both the real IO edge (to know
  /// when to start forwarding stdin directly) and the invariants (to know
  /// when "connected" holds).
  let isConnected (s: State) : bool =
    match s.Transport with
    | TransportState.Ready _ -> true
    | TransportState.AwaitingDaemon _
    | TransportState.Closed
    | TransportState.Fatal _ -> false
