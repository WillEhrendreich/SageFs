namespace SageFs

open System.Text.Json
open System.Text.RegularExpressions

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

  /// The id and the method of a line that does not parse, recovered leniently.
  ///
  /// JSON forbids a literal newline inside a string, and a client that writes one anyway (a
  /// tool call whose argument string ends in a newline, say) sends a message the bridge cannot
  /// parse. The client is still waiting for THAT request's id, and without it the bridge can only
  /// log to stderr, and the call hangs for good (issue #164). So the id is dug out of the raw text
  /// and the request keeps its identity: the daemon is asked about it, the daemon's own refusal is
  /// the answer the client needs, and it names what it choked on.
  let private recoverId (raw: string) : RpcId option =
    let number = Regex("\"id\"\\s*:\\s*(-?\\d+)").Match(raw)
    let text = Regex("\"id\"\\s*:\\s*\"([^\"]*)\"").Match(raw)
    if number.Success then Some(RpcId.N(int64 number.Groups.[1].Value))
    elif text.Success then Some(RpcId.S text.Groups.[1].Value)
    else None

  /// The method of a line that does not parse, recovered leniently (see `recoverId`).
  let private recoverMethod (raw: string) : string option =
    let m = Regex("\"method\"\\s*:\\s*\"([^\"]*)\"").Match(raw)
    if m.Success then Some m.Groups.[1].Value else None

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
      // The line did not parse. It may still be a request the client is waiting for: a message
      // whose arguments carry a LITERAL newline is exactly this. The id is recovered so the
      // bridge can answer THAT request, and the message is forwarded so the daemon's own refusal
      // (which names what it choked on) is what the client hears.
      match recoverId raw, recoverMethod raw with
      | Some id, Some m -> RpcMessage.Request(id, m, raw)
      | _ -> RpcMessage.Unparseable(raw, ex.Message)

  /// The most text a run of unfinished lines may hold before the bridge gives up on them as one message. Large
  /// enough for any tool call a client sends (a whole source file as a `code` argument), small enough that a
  /// stream of lines that never finish cannot grow without limit.
  let maxHeldChars = 4 * 1024 * 1024

  /// Where the stdin reader is between lines. A line that is not JSON may be the first part of a message whose
  /// string carried a literal newline, so it is held and the next line decides what it was.
  [<RequireQualifiedAccess>]
  type LineState =
    | Idle
    /// Lines that did not parse, joined with the newlines they were split at.
    | Holding of text: string

  let private isValidJson (text: string) : bool =
    try
      use _doc = JsonDocument.Parse text
      true
    with :? JsonException -> false

  /// Take one stdin line (blank ones included) and say what messages it completed.
  ///
  /// A client that writes a literal newline inside a JSON string splits ONE message over several stdin lines.
  /// `parseRpcMessage` can recover the id from a single line only when the id and the method sit on the same
  /// line, and a client that serializes `id` after `params` puts them on different ones, so every fragment came
  /// out `Unparseable`, nothing carried an id, and the call hung for good (issue #164). So a fragment that is
  /// not JSON is held, and each following line that is not a complete message of its own is appended to it.
  /// Recovery is tried again on the joined text, and the first time it finds an id and a method the message is
  /// handed on as a `Request`, which the daemon refuses with the reason and the bridge answers by id.
  ///
  /// A line that IS valid JSON ends the hold at once and is handled as itself, so a good message is never
  /// swallowed by an unfinished line in front of it. The held fragment comes out `Unparseable` (logged, since it
  /// has no id to answer against).
  let acceptLine (state: LineState) (line: string) : LineState * RpcMessage list =
    let first (line: string) : LineState * RpcMessage list =
      match line with
      | "" -> LineState.Idle, []
      | _ ->
        match parseRpcMessage line with
        | RpcMessage.Unparseable(raw, _) when not (isValidJson raw) -> LineState.Holding raw, []
        | message -> LineState.Idle, [ message ]
    match state with
    | LineState.Idle -> first line
    | LineState.Holding head ->
      match line <> "" && isValidJson line with
      | true ->
        let next, messages = first line
        next, RpcMessage.Unparseable(head, "an unfinished line, followed by a message of its own") :: messages
      | false ->
        let joined = head + "\n" + line
        match parseRpcMessage joined with
        | RpcMessage.Unparseable _ when joined.Length > maxHeldChars ->
          LineState.Idle, [ RpcMessage.Unparseable(joined, "lines that never became a message") ]
        | RpcMessage.Unparseable _ -> LineState.Holding joined, []
        | message -> LineState.Idle, [ message ]

  /// What stdin closing leaves behind: a fragment still being held, which has no id to answer against.
  let closeInput (state: LineState) : RpcMessage list =
    match state with
    | LineState.Idle -> []
    | LineState.Holding head -> [ RpcMessage.Unparseable(head, "stdin closed inside an unfinished line") ]

  /// The `params` object an `initialize` declared, verbatim.
  ///
  /// The daemon reads a client's capabilities out of `initialize`, and they
  /// decide what that session may ever be asked: MCP elicitation, and
  /// anything else the server only does for a client that said it can take
  /// it. When the bridge has to mint a fresh session because the id it holds
  /// has gone stale, it must re-declare WHAT THE CLIENT declared, or the new
  /// session quietly starts with fewer rights than the client actually has
  /// and nobody is told anything changed.
  ///
  /// Nothing else in the protocol is a handshake, so nothing else yields
  /// params: a later heal can never pick up a payload that was never one.
  /// Unreadable input yields nothing rather than throwing, because the caller
  /// falls back to claiming nothing, which is the safe direction to be wrong
  /// in — a heal must never fail to mint a session over a capability we could
  /// not read.
  let initializeParamsOf (msg: RpcMessage) : string option =
    match msg with
    | RpcMessage.Request(_, "initialize", raw) ->
      try
        use doc = JsonDocument.Parse(raw)
        match doc.RootElement.TryGetProperty("params") with
        | true, p when p.ValueKind = JsonValueKind.Object -> Some(p.GetRawText())
        | _ -> None
      with _ -> None
    | _ -> None

  /// What is held AFTER seeing `msg`: an `initialize` replaces whatever was
  /// held, and everything else leaves it alone.
  ///
  /// The second half is the half that matters. A bridge forwards hundreds of
  /// messages between handshakes, and if any of them could clear the held
  /// declaration then the first `tools/list` after a client connected would
  /// quietly strip the capabilities the re-handshake needs — the very bug
  /// this whole path exists to avoid, arriving by a route nobody wrote.
  let heldAfter (held: string option) (msg: RpcMessage) : string option =
    match initializeParamsOf msg with
    | Some declared -> Some declared
    | None -> held

  /// The body the bridge posts to re-mint a session after the daemon no
  /// longer knows the id we hold.
  ///
  /// Same shape it has always had, except `params` is the client's own when
  /// we have seen one. With none held it falls back to the empty
  /// capabilities it has always sent: a working handshake that claims
  /// nothing, which is what a bridge that never saw a client should say.
  let reHandshakeBodyFor (heldParams: string option) : string =
    let paramsJson =
      match heldParams with
      | Some p -> p
      | None ->
        """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"sagefs-mcp-bridge","version":"1"}}"""
    sprintf """{"jsonrpc":"2.0","id":"sagefs-mcp-bridge-rehandshake","method":"initialize","params":%s}""" paramsJson

  /// What a bridge that lost its daemon still holds of the MCP session it had: an id (stale now, the new daemon never
  /// minted it) or nothing, because the client had not finished its handshake.
  [<RequireQualifiedAccess>]
  type HeldSession =
    | Never
    | Id of sessionId: string

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
    /// The daemon this bridge was talking to went away (an upgrade stops it, installs the new build and starts
    /// it again). The bridge stays up and keeps probing, holding the MCP session id it had so the first request
    /// after the daemon is back re-runs the handshake. It asks for a daemon itself only once
    /// `Policy.MaxReconnectProbes` probes have found none, which moves it to `Starting`.
    | Reconnecting of attempts: int * held: HeldSession

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
    /// The server-push stream opened for `sessionId` ended. Only the stream of the session the bridge holds now
    /// means the daemon is gone: a stream of an older session ending after the bridge moved on says nothing.
    | ServerStreamClosed of sessionId: string * reason: string
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
    /// The daemon is back and the bridge holds the id of a session it no longer has: mint a fresh one with the
    /// client's own `initialize` parameters, open the push stream on it and tell the client its lists may have
    /// changed. Runs before the messages queued while the daemon was away, so they go out under the new id.
    | Rehandshake
    | Shutdown

  type Policy =
    { MaxProbeAttempts: int
      /// Probes a bridge makes after it lost its daemon before it starts one itself.
      MaxReconnectProbes: int }

  /// As many probes as fit in `Timeouts.daemonStartWait` at `Timeouts.stdioBridgeProbeInterval` apart: 30 on a
  /// fast machine (15 s), more on a slow one. Matches `Program.fs`'s `waitForDaemonReady`, which this bridge
  /// replaces the client-side half of. A lost daemon gets `Timeouts.stdioBridgeReconnectWait` of probes.
  let defaultPolicy =
    { MaxProbeAttempts = int (System.Math.Ceiling(Timeouts.daemonStartWait / Timeouts.stdioBridgeProbeInterval))
      MaxReconnectProbes = int (System.Math.Ceiling(Timeouts.stdioBridgeReconnectWait / Timeouts.stdioBridgeProbeInterval)) }

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

    // ---- The daemon went away under a connected bridge: keep serving, wait for it ----
    | TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(n, held)), Event.Probe ->
      { state with Transport = TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(n + 1, held)) }, [ Action.Probe ]

    | TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(_, held)), Event.ProbeResult true ->
      // A held id is the id of a session the new daemon never minted. `Rehandshake` replaces it before the queue
      // drains, so the queued messages go out under the new one.
      let next, drained = drain state
      let transport, first =
        match held with
        | HeldSession.Id sid -> TransportState.Ready(Some sid), [ Action.Rehandshake ]
        | HeldSession.Never -> next.Transport, []
      { next with Transport = transport }, first @ drained

    | TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(n, _)), Event.ProbeResult false ->
      match n >= policy.MaxReconnectProbes with
      | true -> { state with Transport = TransportState.AwaitingDaemon(DaemonReadiness.Starting 0) }, [ Action.StartDaemon ]
      | false -> state, []

    | TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting _), Event.DaemonStartFailed _ -> state, []

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

    | TransportState.Ready(Some held), Event.SessionIdCaptured sid ->
      // The same id again (the IO edge reads the header off more than one response) changes nothing. A DIFFERENT
      // id is the daemon's answer to a re-handshake: the held session is gone, and every later request must
      // carry the new one.
      match sid = held with
      | true -> state, []
      | false -> { state with Transport = TransportState.Ready(Some sid) }, []

    | TransportState.AwaitingDaemon _, Event.SessionIdCaptured _
    | TransportState.Closed, Event.SessionIdCaptured _
    | TransportState.Fatal _, Event.SessionIdCaptured _ -> state, []

    // ---- A protocol-level rejection of ONE request (never fatal) ----
    // Reuses `Action.RejectMessage` — the same shape already used for a
    // message the bridge can't forward for a state reason (queued-at-give-up,
    // Closed, Fatal) — rather than inventing a second "here's what's wrong
    // with your request" shape. The bridge stays exactly where it was.
    | TransportState.Ready _, Event.RequestRejected(msg, reason) -> state, [ Action.RejectMessage(msg, reason) ]

    | TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting _), Event.RequestRejected(msg, reason) ->
      // A request that was in flight when the daemon went away: its failure can arrive after the push stream's
      // end already moved the bridge here, and the client is still owed an answer.
      state, [ Action.RejectMessage(msg, reason) ]

    | TransportState.AwaitingDaemon _, Event.RequestRejected _
    | TransportState.Closed, Event.RequestRejected _
    | TransportState.Fatal _, Event.RequestRejected _ ->
      // Can't happen — nothing is ever forwarded before Ready — but a stray
      // report here is a no-op, same posture as a stray HttpFailed.
      state, []

    // ---- Daemon dies mid-session ----
    | TransportState.Ready sessionId, Event.HttpFailed _ ->
      // The daemon is gone, and an upgrade makes it gone on purpose: it stops the daemon, installs the new build
      // and starts the daemon again. The client's process is still here and owns this bridge, so the bridge waits
      // for the daemon (`Reconnecting`) and gives up only by the rules of a cold start. Ending here made every
      // upgrade end every client's SageFs connection (measured: exit code 1, "server stream closed").
      let held =
        match sessionId with
        | Some sid -> HeldSession.Id sid
        | None -> HeldSession.Never
      { state with Transport = TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(0, held)) }, [ Action.Probe ]

    | TransportState.Ready(Some held), Event.ServerStreamClosed(sid, _) when sid = held ->
      { state with Transport = TransportState.AwaitingDaemon(DaemonReadiness.Reconnecting(0, HeldSession.Id held)) }, [ Action.Probe ]

    | TransportState.Ready _, Event.ServerStreamClosed _
    | TransportState.AwaitingDaemon _, Event.ServerStreamClosed _
    | TransportState.Closed, Event.ServerStreamClosed _
    | TransportState.Fatal _, Event.ServerStreamClosed _ -> state, []

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
