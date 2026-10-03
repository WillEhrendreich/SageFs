module SageFs.Server.McpStdioBridge

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open SageFs
open SageFs.McpBridge
open SageFs.Utils

/// `sagefs mcp` — bridges a client's stdio to the daemon's streamable-HTTP
/// MCP endpoint, so the client never has to win the race of starting after
/// the daemon does. Every DECISION here is `SageFs.McpBridge.decide`
/// (SageFs.Core/McpBridge.fs); everything in this file is the IO edge that
/// decision drives: probing, spawning the daemon, POSTing/GETting the wire
/// protocol, and reading/writing stdio.
///
/// stdout is the protocol. Nothing in this file calls `printfn`/
/// `Console.Out` — those go through `Program.fs`'s `NewlineNormalizingWriter`
/// (CRLF on Windows), which would corrupt LF-delimited JSON-RPC framing.
/// `StdoutWriter` below opens the RAW stdout stream instead, once, and is
/// the only thing in the process allowed to write to it. Diagnostics always
/// go to stderr via `eprintfn`/`SageFs.Utils.Log`.

/// Writes exactly what stdio MCP framing requires: one JSON-RPC message per
/// line, LF-terminated, nothing else. Takes its own lock because stdout is
/// written from several concurrent sources — a forwarded response, a pushed
/// server notification, a rejection — that must never interleave mid-line.
type StdoutWriter(stream: Stream) =
  let writer = new StreamWriter(stream, UTF8Encoding(false), NewLine = "\n", AutoFlush = false)
  let gate = obj ()
  member _.WriteLine(line: string) =
    lock gate (fun () ->
      writer.Write(line)
      writer.Write('\n')
      writer.Flush())

/// The raw stdout handle — bypasses `Console.Out` and any wrapper installed
/// on it.
let openStdout () : StdoutWriter = StdoutWriter(Console.OpenStandardOutput())

/// The two ways forwarding one message to the daemon can fail — kept apart
/// on purpose (issue #138): a `Rejected` message means the daemon is alive
/// and reachable and said "no" to THIS request; an `Unreachable` message
/// means no usable response came back at all. Conflating them used to turn
/// an ordinary protocol error (e.g. a missing session header) into "the
/// daemon is gone," which killed the bridge over a single bad request.
[<RequireQualifiedAccess>]
type ForwardError =
  /// The daemon answered with a client/protocol-level error (HTTP 4xx) for
  /// this one request. Stays connected; only this request gets an error.
  | Rejected of reason: string
  /// The daemon answered "I have never heard of that `Mcp-Session-Id`" —
  /// HTTP 404 carrying JSON-RPC error `-32001 "Session not found"`, which is
  /// what the streamable-HTTP transport answers after a daemon restart has
  /// destroyed the in-memory session (`install-local --force` restarts the
  /// daemon on purpose; measured 200 before, 404 after). The held session id
  /// is STALE, not the request: nothing about this request is wrong, so it is
  /// the ONE rejection that is recoverable by re-running the handshake and
  /// re-sending. Distinct from `Rejected`, which is the daemon's real answer
  /// about THIS request and must reach the client untouched.
  | SessionInvalid of reason: string
  /// No usable response — connection refused, timed out, the stream tore
  /// down mid-read. This is "the daemon is gone."
  | Unreachable of reason: string

/// The exact daemon answer that means "the session id you are holding is one
/// I have never minted", and therefore the only rejection this bridge heals.
///
/// Both halves must hold, and neither alone would do. The status is 404 with
/// no `Mcp-Session-Id` on the response (nothing was minted), and the body is
/// the JSON-RPC error `-32001 "Session not found"` that
/// `StreamableHttpHandler.GetSessionAsync` writes on exactly that path
/// (mcp-sdk/src/ModelContextProtocol.AspNetCore/StreamableHttpHandler.cs).
/// Matching on the status alone would misread any other 404, and matching on
/// the phrase alone would misread a tool error that merely mentions it — so
/// this is deliberately narrow, and everything else stays a `Rejected` that
/// reaches the client.
let internal isSessionInvalid (statusCode: int) (body: string) : bool =
  statusCode = 404 && body.Contains("-32001") && body.Contains("Session not found")

/// The real-world effects the bridge performs, injected so the pure
/// orchestration loop (`run`, below) can be driven by fakes in tests without
/// a real process or a real daemon. Each field maps to exactly one
/// `McpBridge.Action`.
type Io =
  { /// A daemon health probe (SageFs.DaemonState.probeDaemonHttpAsync).
    Probe: unit -> Task<bool>
    /// Start the daemon the way `sagefs` normally does: detached, not tied
    /// to this process's lifetime, since other clients share it.
    StartDaemon: unit -> Task<Result<unit, string>>
    /// POST `msg` to the daemon (with the current session id header, if
    /// any), and write every JSON-RPC message the daemon answers with —
    /// single JSON or SSE, one message or several — to stdout. Forwarding
    /// IS the write: they are not two actions that could be reordered
    /// against each other.
    ///
    /// Takes an explicit capture callback instead of returning the new
    /// session id, and the implementation MUST call it the instant the
    /// `Mcp-Session-Id` response header is read — strictly BEFORE writing
    /// anything to stdout (issue #138). That ordering is the entire fix: the
    /// client physically cannot send its next request until it has read the
    /// stdout write, so a capture posted ahead of that write is guaranteed
    /// to reach the bridge's mailbox ahead of the `StdinLine` it provokes,
    /// no matter how fast the client turns around. Returning the id instead
    /// and posting it only after this Task completes (as a prior version of
    /// this bridge did) reopens exactly that race — see
    /// SageFs.Simulation/McpStdioBridgeSim.fs's capture-ordering-race
    /// section for the DST that proves it.
    ///
    /// A `SessionInvalid` answer is the ONE case this heals in place rather
    /// than reporting: the held session id is stale, not the request, so the
    /// implementation must re-run the handshake to mint a fresh id and then
    /// RE-SEND this same message under it, and report `SessionInvalid` only
    /// when that second attempt also fails. The client asked one question
    /// and is owed one answer; clearing the id without re-sending would leave
    /// that question unanswered until the client thought to ask again, which
    /// is exactly the "a human has to reload" failure this replaces.
    Forward: (string -> unit) -> string option -> RpcMessage -> Task<Result<unit, ForwardError>>
    /// Open the server-push SSE stream (GET) once a session id exists, and
    /// write every message it carries to stdout until it closes or errors.
    OpenServerStream: string -> Task<Result<unit, string>>
    TerminateSession: string -> Task<unit>
    /// Read stdin until EOF, calling `post` with each `Event.StdinLine` and
    /// finally `Event.StdinClosed`. Takes the poster as a parameter instead
    /// of `run` exposing its mailbox, so stdin is just one more event
    /// source feeding the same queue as probes and HTTP results — exactly
    /// how SageFs.Simulation models it.
    PumpStdin: (Event -> unit) -> Task<unit>
    ProbeInterval: TimeSpan }

/// Write one JSON-RPC error response for a rejected `Request` to stdout —
/// the client sent it and is owed an answer, even though the bridge can't
/// forward it. A `Notification`/`Response`/`Unparseable` has no id to
/// answer against, so it's only logged to stderr — never silently dropped.
let internal writeRejection (stdout: StdoutWriter) (msg: RpcMessage) (reason: string) =
  match msg with
  | RpcMessage.Request(id, _, _) ->
    let idJson =
      match id with
      | RpcId.S s -> Json.serialize Json.standard s
      | RpcId.N n -> string n
    let errMsg = Json.serialize Json.standard reason
    stdout.WriteLine(sprintf """{"jsonrpc":"2.0","id":%s,"error":{"code":-32000,"message":%s}}""" idJson errMsg)
    // Loud on both channels: the client gets a proper JSON-RPC error, and
    // stderr gets the same reason so a human watching the process (or its
    // log) never has to infer a rejection from a missing tool alone.
    Log.warn "[mcp-stdio] rejected a request (%s): %s" reason (rawOf msg)
  | RpcMessage.Notification _
  | RpcMessage.Response _
  | RpcMessage.Unparseable _ -> Log.warn "[mcp-stdio] dropped an unanswerable message (%s): %s" reason (rawOf msg)

[<RequireQualifiedAccess>]
type private Cmd =
  | Ev of Event
  | Stop

/// Drive `McpBridge.decide` from a mailbox that serializes every event onto
/// one thread — the pure fold never has to worry about concurrent callers,
/// and every async IO result comes back in through the SAME queue as a new
/// `Event`, never by mutating shared state from another thread. Returns the
/// process exit code: 0 once connected and cleanly shut down, 1 if the
/// bridge ended Fatal.
let run (policy: Policy) (io: Io) (stdout: StdoutWriter) : Task<int> =
  let exitCode = TaskCompletionSource<int>()

  let post (mailbox: MailboxProcessor<Cmd>) (ev: Event) = mailbox.Post(Cmd.Ev ev)

  // `onSessionId` is a SEPARATE parameter from "post to the mailbox" (rather
  // than performAsync hardcoding that) so a batch of `ForwardToDaemon`
  // actions produced by ONE `decide` call — e.g. `drain`, when several
  // messages queued up before the daemon was even reachable and are
  // released together — can update a LOCAL running session id that the
  // REST of that same batch uses immediately, not just the mailbox's own
  // state (which a later event would see, but which this synchronous batch
  // can't wait around for). See the dispatch loop below for why this
  // matters: it's issue #138's race again, just one level up — several
  // requests queued together instead of one client reacting to a response.
  let performAsync (mailbox: MailboxProcessor<Cmd>) (sessionId: string option) (onSessionId: string -> unit) (action: Action) : Task<unit> =
    task {
      // Every one of these runs from a batch that's awaited SEQUENTIALLY
      // but never blocks the mailbox's own receive loop (the whole batch is
      // one fire-and-forget `Task.Run`, see below) — so an exception that
      // escaped here would vanish silently — exactly the kind of hang this
      // bridge exists to never produce. Catch and report instead: a
      // probe/forward that throws becomes an HttpFailed/DaemonStartFailed
      // event, same as an IO function that returns `Error` cleanly.
      try
        match action with
        | Action.Probe ->
          let! healthy = io.Probe()
          post mailbox (Event.ProbeResult healthy)
        | Action.StartDaemon ->
          match! io.StartDaemon() with
          | Ok() -> () // readiness is observed through later probes, not this call
          | Error reason -> post mailbox (Event.DaemonStartFailed reason)
        | Action.ForwardToDaemon msg ->
          // The capture callback runs BEFORE `io.Forward` writes anything to
          // stdout — see `Io.Forward`'s doc comment for why that ordering is
          // the whole fix.
          match! io.Forward onSessionId sessionId msg with
          | Ok() -> ()
          | Error(ForwardError.Rejected reason) -> post mailbox (Event.RequestRejected(msg, reason))
          | Error(ForwardError.SessionInvalid reason) -> post mailbox (Event.RequestRejected(msg, reason))
          | Error(ForwardError.Unreachable reason) -> post mailbox (Event.HttpFailed reason)
        | Action.RejectMessage(msg, reason) -> writeRejection stdout msg reason
        | Action.ReportFatal reason -> eprintfn "sagefs mcp: %s" reason
        | Action.TerminateSession sid -> do! io.TerminateSession sid
        | Action.Shutdown -> mailbox.Post Cmd.Stop
      with ex ->
        match action with
        | Action.StartDaemon -> post mailbox (Event.DaemonStartFailed ex.Message)
        | Action.Probe
        | Action.ForwardToDaemon _ -> post mailbox (Event.HttpFailed ex.Message)
        | Action.RejectMessage _
        | Action.ReportFatal _
        | Action.TerminateSession _
        | Action.Shutdown -> eprintfn "sagefs mcp: internal error: %s" ex.Message
    }

  let mailbox =
    MailboxProcessor<Cmd>.Start(fun inbox ->
      let rec loop (state: State) = async {
        let! cmd = inbox.Receive()
        match cmd with
        | Cmd.Stop ->
          let code = match state.Transport with TransportState.Fatal _ -> 1 | _ -> 0
          exitCode.TrySetResult(code) |> ignore
        | Cmd.Ev ev ->
          let state', actions = decide policy state ev
          // The moment a session id is first captured, open the
          // server-push stream — not on bare "daemon reachable", since the
          // streamable-HTTP GET endpoint is scoped to a session.
          match state.Transport, state'.Transport with
          | TransportState.Ready None, TransportState.Ready(Some sid) ->
            Task.Run<unit>(fun () ->
              task {
                match! io.OpenServerStream sid with
                | Ok() -> ()
                | Error reason -> post inbox (Event.HttpFailed reason)
              })
            |> ignore
          | _ -> ()
          // `decide` deliberately answers a "still waiting, nothing to do
          // yet" probe result with an EMPTY action list — it's the IO
          // edge's job to keep the clock ticking. Schedule the next probe
          // ourselves, exactly once per unhealthy result, only while still
          // in the race.
          match ev, state'.Transport with
          | Event.ProbeResult false, TransportState.AwaitingDaemon _ ->
            Task.Run<unit>(fun () ->
              task {
                do! Task.Delay(io.ProbeInterval)
                post inbox Event.Probe
              })
            |> ignore
          | _ -> ()
          let sessionId =
            match state'.Transport with
            | TransportState.Ready sid -> sid
            | TransportState.AwaitingDaemon _
            | TransportState.Closed
            | TransportState.Fatal _ -> None
          // One `Task.Run` for the WHOLE batch, actions awaited in order —
          // never blocks the mailbox's own `inbox.Receive()` loop (this
          // Task.Run isn't awaited here), but WITHIN the batch, action N+1
          // now starts only once action N has actually finished, threading
          // a live-updated `currentSessionId` through every
          // `ForwardToDaemon` in it. Without this, `drain` releasing several
          // queued messages at once (initialize + notifications/initialized
          // + tools/list, all queued before the daemon answered — exactly
          // what Will's own pipe-everything-in-at-once repro produces) fired
          // them via independent, unordered `Task.Run` calls that ALL
          // captured the SAME stale (None) sessionId snapshot from this one
          // `decide` call, so every request after `initialize` still went
          // out with no session header — the session-id race one level up
          // from the single-message case `Io.Forward`'s doc comment covers.
          match actions with
          | [] -> ()
          | _ ->
            Task.Run<unit>(fun () ->
              task {
                let mutable currentSessionId = sessionId
                for action in actions do
                  do! performAsync inbox currentSessionId (fun sid -> currentSessionId <- Some sid; post inbox (Event.SessionIdCaptured sid)) action
              })
            |> ignore
          return! loop state'
      }
      loop McpBridge.initial)

  Task.Run<unit>(fun () -> io.PumpStdin(post mailbox)) |> ignore
  post mailbox Event.Probe
  exitCode.Task

/// The arguments that make a bridge the owner of the daemon it starts. The
/// daemon's home port is shared: other agents connect to the same daemon, so
/// its life must not hang on whichever client happened to start it. A daemon on
/// any other port is refused unless it names an owner (or a ttl), so there the
/// bridge is the owner and the daemon goes when its client does. This is the
/// same split `decideCustomPortOwnership` makes on the daemon's side.
let ownerArguments (homePort: int) (mcpPort: int) (bridgePid: int) : string list =
  match mcpPort = homePort with
  | true -> []
  | false -> [ "--owner-pid"; string bridgePid ]

/// The daemon-spawn IO edge: start it exactly the way plain `sagefs` does —
/// same executable, same args, detached. We never wait on it and never tie
/// its lifetime to ours unless we were asked for a port of our own (see
/// `ownerArguments`). Its own stdout/stderr are redirected and drained to OUR
/// stderr so nothing from it ever reaches our stdout, which is the protocol
/// stream.
let startDaemonProcess (mcpPort: int) : Task<Result<unit, string>> =
  task {
    try
      let exePath = Process.GetCurrentProcess().MainModule.FileName
      let psi =
        ProcessStartInfo(
          exePath,
          WorkingDirectory = Environment.CurrentDirectory,
          UseShellExecute = false,
          RedirectStandardOutput = true,
          RedirectStandardError = true
        )
      // This process worked its own tier out and published it; the daemon works out its own, from the same profile.
      MachineStartup.withoutDerivedTier psi.Environment
      psi.ArgumentList.Add("--mcp-port")
      psi.ArgumentList.Add(string mcpPort)
      ownerArguments SageFsConfig.McpPortFromEnv mcpPort (Process.GetCurrentProcess().Id)
      |> List.iter psi.ArgumentList.Add
      let proc = Process.Start(psi)
      proc.OutputDataReceived.Add(fun e ->
        match e.Data with
        | null -> ()
        | line -> Log.info "[daemon] %s" line)
      proc.ErrorDataReceived.Add(fun e ->
        match e.Data with
        | null -> ()
        | line -> Log.warn "[daemon] %s" line)
      proc.BeginOutputReadLine()
      proc.BeginErrorReadLine()
      return Ok()
    with ex ->
      return Error ex.Message
  }

let private mcpAccept = "application/json, text/event-stream"
let private sessionIdHeader = "Mcp-Session-Id"

/// Split an SSE byte stream into the JSON payload of each event ("data:"
/// lines joined by "\n", per the SSE spec, flushed on the blank line that
/// terminates an event) and hand each one to `onMessage`.
let rec private drainSse (reader: StreamReader) (onMessage: string -> unit) : Task<unit> =
  task {
    let dataLines = ResizeArray<string>()
    let flush () =
      match dataLines.Count with
      | 0 -> ()
      | _ ->
        onMessage (String.Join("\n", dataLines))
        dataLines.Clear()
    let mutable finished = false
    while not finished do
      let! line = reader.ReadLineAsync()
      match line with
      | null ->
        flush ()
        finished <- true
      | "" -> flush ()
      | l when l.StartsWith("data:") -> dataLines.Add(l.Substring(5).TrimStart(' '))
      | _ -> () // event:/id:/retry:/comment lines carry nothing this bridge needs
  }

/// One POST's worth of work, without the healing policy: the raw text the
/// daemon answered with, whether it was a success, and (on a success) whether
/// it was already written to stdout. Kept separate from `forward` so the
/// re-handshake retry below re-enters the IDENTICAL wire path rather than a
/// hand-rolled second copy of it that could drift.
type private PostOutcome =
  /// The daemon answered 2xx; the response has already been written to stdout.
  | Written
  /// The daemon answered non-2xx with this status and body, nothing written.
  | Answered of statusCode: int * body: string

/// POST one JSON-RPC message to the daemon's streamable-HTTP endpoint and
/// write whatever it answers with to stdout — a single JSON object, or an
/// SSE stream carrying one message or several before the daemon closes it.
///
/// `captureSessionId` is called the instant a new `Mcp-Session-Id` response
/// header is read — BEFORE anything is written to stdout. That ordering is
/// load-bearing (issue #138): the caller posts it straight to the bridge's
/// mailbox, and the client cannot possibly produce its next stdin line
/// before it has read the write that follows, so capturing first guarantees
/// the mailbox sees the session id ahead of the request it provokes. Capture
/// after the write (a prior version of this function) makes that ordering a
/// coin flip instead of a guarantee.
let private postOnce
  (client: HttpClient)
  (port: int)
  (stdout: StdoutWriter)
  (captureSessionId: string -> unit)
  (sessionId: string option)
  (msg: RpcMessage)
  : Task<PostOutcome> =
  task {
    use content = new StringContent(rawOf msg, Encoding.UTF8, "application/json")
    use req = new HttpRequestMessage(HttpMethod.Post, sprintf "http://localhost:%d/" port, Content = content)
    req.Headers.Accept.ParseAdd("application/json")
    req.Headers.Accept.ParseAdd("text/event-stream")
    match sessionId with
    | Some sid -> req.Headers.Add(sessionIdHeader, sid)
    | None -> ()
    use! resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
    // Capture BEFORE any stdout write below — see this function's doc
    // comment and `Io.Forward`'s.
    match resp.Headers.TryGetValues(sessionIdHeader) with
    | true, values ->
      match Seq.tryHead values with
      | Some sid when Some sid <> sessionId -> captureSessionId sid
      | _ -> ()
    | false, _ -> ()
    match resp.IsSuccessStatusCode with
    | false ->
      let! body = resp.Content.ReadAsStringAsync()
      return Answered(int resp.StatusCode, body)
    | true ->
      let mediaType = resp.Content.Headers.ContentType |> Option.ofObj |> Option.map (fun ct -> ct.MediaType) |> Option.defaultValue ""
      match mediaType with
      | "text/event-stream" ->
        use! stream = resp.Content.ReadAsStreamAsync()
        use reader = new StreamReader(stream)
        do! drainSse reader stdout.WriteLine
        return Written
      | _ ->
        let! body = resp.Content.ReadAsStringAsync()
        match String.IsNullOrWhiteSpace body with
        | true -> () // 202 Accepted for a notification/response the client sent — nothing to write
        | false -> stdout.WriteLine(body.Trim())
        return Written
  }

/// The `initialize` this bridge sends ITSELF to re-mint a session id it lost.
/// It is never written to stdout: the client already completed a handshake and
/// must not be handed a second `initialize` result it did not ask for (it
/// would see an unexpected message in its stream). The id is taken from the
/// RESPONSE HEADER, which is where the client got its own.
let private reHandshakeBody =
  """{"jsonrpc":"2.0","id":"sagefs-mcp-bridge-rehandshake","method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"sagefs-mcp-bridge","version":"1"}}}"""

/// POST one JSON-RPC message to the daemon's streamable-HTTP endpoint and
/// write whatever it answers with to stdout — a single JSON object, or an
/// SSE stream carrying one message or several before the daemon closes it.
///
/// `captureSessionId` is called the instant a new `Mcp-Session-Id` response
/// header is read — BEFORE anything is written to stdout. That ordering is
/// load-bearing (issue #138): the caller posts it straight to the bridge's
/// mailbox, and the client cannot possibly produce its next stdin line
/// before it has read the write that follows, so capturing first guarantees
/// the mailbox sees the session id ahead of the request it provokes. Capture
/// after the write (a prior version of this function) makes that ordering a
/// coin flip instead of a guarantee.
///
/// SELF-HEAL. The MCP session lives in the DAEMON'S MEMORY, so any daemon
/// restart (`install-local --force` does it on purpose) destroys it and turns
/// the id the client is still holding into a 404 "Session not found". Before
/// this, that 404 became a `Rejected` and the held id was NEVER cleared, so
/// every later request failed identically for the life of the client process
/// and only a human reloading the MCP server could fix it. Here, exactly that
/// one answer clears the id, re-mints it with a handshake the daemon accepts
/// (never surfaced to the client — it has already handshook), and RE-SENDS
/// this same message under the new id. The client asked one question and is
/// owed one answer, so the re-send is what makes this "one call pays the
/// cost" rather than "the next call, if the client is feeling lucky".
///
/// Nothing else retries. A 400 is the daemon's real answer about THIS request
/// and reaches the client verbatim; a transport failure is not this
/// function's business at all (it is `Unreachable`, which `decide` turns into
/// a probe/`StartDaemon` cycle — see `Io.StartDaemon`). Only the narrow
/// `isSessionInvalid` answer is healed, and only once.
let forward
  (client: HttpClient)
  (port: int)
  (stdout: StdoutWriter)
  (captureSessionId: string -> unit)
  (sessionId: string option)
  (msg: RpcMessage)
  : Task<Result<unit, ForwardError>> =
  task {
    try
      let! outcome = postOnce client port stdout captureSessionId sessionId msg
      match outcome with
      | Written -> return Ok()
      | Answered(statusCode, body) ->
        match sessionId, isSessionInvalid statusCode body with
        | Some _, true ->
          // The held id is stale. Clear it, mint a replacement, and re-send.
          Log.warn "[mcp-stdio] daemon no longer knows session %A (it restarted); re-running the handshake and re-sending this request" sessionId
          use content = new StringContent(reHandshakeBody, Encoding.UTF8, "application/json")
          use req = new HttpRequestMessage(HttpMethod.Post, sprintf "http://localhost:%d/" port, Content = content)
          req.Headers.Accept.ParseAdd("application/json")
          req.Headers.Accept.ParseAdd("text/event-stream")
          use! resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
          let freshSid =
            match resp.IsSuccessStatusCode, resp.Headers.TryGetValues(sessionIdHeader) with
            | true, (true, values) -> Seq.tryHead values
            | _ -> None
          match freshSid with
          | Some fresh ->
            // Captured before the re-send below, and before anything that
            // could write to stdout — the same #138 ordering, unchanged.
            captureSessionId fresh
            let! retry = postOnce client port stdout captureSessionId (Some fresh) msg
            match retry with
            | Written -> return Ok()
            | Answered(code, retryBody) ->
              return Error(ForwardError.Rejected(sprintf "daemon answered %d after re-running the handshake: %s" code retryBody))
          | None ->
            return Error(ForwardError.SessionInvalid "the daemon did not answer the re-handshake with a fresh session id")
        | _ ->
          return Error(ForwardError.Rejected(sprintf "daemon answered %d: %s" statusCode body))
    with ex ->
      return Error(ForwardError.Unreachable ex.Message)
  }

/// The long-lived GET stream for server-initiated messages (notifications,
/// server-to-client requests) not tied to any one POST. Runs until it
/// errors or the process exits; an error here means the daemon died or
/// dropped the connection mid-session, so the caller feeds it back as
/// `Event.HttpFailed`.
let openServerStream (client: HttpClient) (port: int) (stdout: StdoutWriter) (sessionId: string) : Task<Result<unit, string>> =
  task {
    try
      use req = new HttpRequestMessage(HttpMethod.Get, sprintf "http://localhost:%d/" port)
      req.Headers.Accept.ParseAdd("text/event-stream")
      req.Headers.Add(sessionIdHeader, sessionId)
      use! resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
      match resp.IsSuccessStatusCode with
      | false ->
        let! body = resp.Content.ReadAsStringAsync()
        return Error(sprintf "server stream answered %d: %s" (int resp.StatusCode) body)
      | true ->
        use! stream = resp.Content.ReadAsStreamAsync()
        use reader = new StreamReader(stream)
        do! drainSse reader stdout.WriteLine
        // A normal close of a supposedly-forever stream, mid-session, is
        // exactly the "daemon died" scenario.
        return Error "server stream closed"
    with ex ->
      return Error ex.Message
  }

let terminateSession (client: HttpClient) (port: int) (sessionId: string) : Task<unit> =
  task {
    try
      use req = new HttpRequestMessage(HttpMethod.Delete, sprintf "http://localhost:%d/" port)
      req.Headers.Add(sessionIdHeader, sessionId)
      use! _resp = client.SendAsync(req)
      ()
    with ex ->
      Log.warn "[mcp-stdio] session termination request failed: %s" ex.Message
  }

/// Read stdin line by line and post each as an `Event.StdinLine`, then
/// `Event.StdinClosed` on EOF. A blank line is not a message (JSON-RPC over
/// stdio never sends one) and is skipped rather than parsed and rejected.
let pumpStdin (post: Event -> unit) : Task<unit> =
  task {
    let mutable finished = false
    while not finished do
      let! line = Console.In.ReadLineAsync()
      match line with
      | null ->
        post Event.StdinClosed
        finished <- true
      | "" -> ()
      | raw -> post (Event.StdinLine(parseRpcMessage raw))
  }

/// Entry point for `sagefs mcp`. Blocks until stdin closes (or the bridge
/// gives up), returns the process exit code.
let runMcpStdio (mcpPort: int) : Task<int> =
  task {
    let stdout = openStdout ()
    use client = new HttpClient(Timeout = Timeouts.workerHttpRequest)
    // A member token for this run (`mint_member`), when the process that started `sagefs mcp` set one. It
    // goes in the HTTP header of every request the bridge makes, so it never appears in a JSON-RPC
    // message, in the bridge's rejection log, or in anything the model reads. One bridge is one run, so
    // one connection-wide header is the right grain here.
    match Capability.CapabilityTransport.headerFromEnvironment Environment.GetEnvironmentVariable with
    | Some(name, value) -> client.DefaultRequestHeaders.Add(name, value)
    | None -> ()
    let io: Io =
      { Probe = fun () -> task { let! info = DaemonState.probeDaemonHttpAsync mcpPort in return info.IsSome }
        StartDaemon = fun () -> startDaemonProcess mcpPort
        Forward = forward client mcpPort stdout
        OpenServerStream = openServerStream client mcpPort stdout
        TerminateSession = terminateSession client mcpPort
        PumpStdin = pumpStdin
        ProbeInterval = Timeouts.stdioBridgeProbeInterval }
    return! run defaultPolicy io stdout
  }
