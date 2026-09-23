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
    /// any) and write every JSON-RPC message the daemon answers with —
    /// single JSON or SSE, one message or several — to stdout. Forwarding
    /// IS the write: they are not two actions that could be reordered
    /// against each other. Returns the `Mcp-Session-Id` response header
    /// when the daemon sent a new one.
    Forward: string option -> RpcMessage -> Task<Result<string option, string>>
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
let private writeRejection (stdout: StdoutWriter) (msg: RpcMessage) (reason: string) =
  match msg with
  | RpcMessage.Request(id, _, _) ->
    let idJson =
      match id with
      | RpcId.S s -> System.Text.Json.JsonSerializer.Serialize(s: string)
      | RpcId.N n -> string n
    let errMsg = System.Text.Json.JsonSerializer.Serialize(reason: string)
    stdout.WriteLine(sprintf """{"jsonrpc":"2.0","id":%s,"error":{"code":-32000,"message":%s}}""" idJson errMsg)
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

  let performAsync (mailbox: MailboxProcessor<Cmd>) (sessionId: string option) (action: Action) : Task<unit> =
    task {
      // Every one of these runs fire-and-forget (`Task.Run` below, never
      // awaited) so an exception that escaped here would vanish silently —
      // exactly the kind of hang this bridge exists to never produce. Catch
      // and report instead: a probe/forward that throws becomes an
      // HttpFailed/DaemonStartFailed event, same as an IO function that
      // returns `Error` cleanly.
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
          match! io.Forward sessionId msg with
          | Ok(Some sid) -> post mailbox (Event.SessionIdCaptured sid)
          | Ok None -> ()
          | Error reason -> post mailbox (Event.HttpFailed reason)
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
          for action in actions do
            Task.Run<unit>(fun () -> performAsync inbox sessionId action) |> ignore
          return! loop state'
      }
      loop McpBridge.initial)

  Task.Run<unit>(fun () -> io.PumpStdin(post mailbox)) |> ignore
  post mailbox Event.Probe
  exitCode.Task

/// The daemon-spawn IO edge: start it exactly the way plain `sagefs` does —
/// same executable, same args, detached. We never wait on it and never tie
/// its lifetime to ours: other clients share it. Its own stdout/stderr are
/// redirected and drained to OUR stderr so nothing from it ever reaches our
/// stdout, which is the protocol stream.
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
      psi.ArgumentList.Add("--mcp-port")
      psi.ArgumentList.Add(string mcpPort)
      // A daemon on a custom port has to name an owner, or it is refused. Ours
      // is this bridge: the client spawned us, so a daemon we started for it
      // should not outlive us. The default port is shared and exempt, which is
      // why this only matters for a bridge asked for a port of its own.
      psi.ArgumentList.Add("--owner-pid")
      psi.ArgumentList.Add(string (Process.GetCurrentProcess().Id))
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

/// POST one JSON-RPC message to the daemon's streamable-HTTP endpoint and
/// write whatever it answers with to stdout — a single JSON object, or an
/// SSE stream carrying one or more messages before the daemon closes it.
let forward (client: HttpClient) (port: int) (stdout: StdoutWriter) (sessionId: string option) (msg: RpcMessage) : Task<Result<string option, string>> =
  task {
    try
      use content = new StringContent(rawOf msg, Encoding.UTF8, "application/json")
      use req = new HttpRequestMessage(HttpMethod.Post, sprintf "http://localhost:%d/" port, Content = content)
      req.Headers.Accept.ParseAdd("application/json")
      req.Headers.Accept.ParseAdd("text/event-stream")
      match sessionId with
      | Some sid -> req.Headers.Add(sessionIdHeader, sid)
      | None -> ()
      use! resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
      let capturedSid =
        match resp.Headers.TryGetValues(sessionIdHeader) with
        | true, values ->
          match Seq.tryHead values with
          | Some sid when Some sid <> sessionId -> Some sid
          | _ -> None
        | false, _ -> None
      match resp.IsSuccessStatusCode with
      | false ->
        let! body = resp.Content.ReadAsStringAsync()
        return Error(sprintf "daemon answered %d: %s" (int resp.StatusCode) body)
      | true ->
        let mediaType = resp.Content.Headers.ContentType |> Option.ofObj |> Option.map (fun ct -> ct.MediaType) |> Option.defaultValue ""
        match mediaType with
        | "text/event-stream" ->
          use! stream = resp.Content.ReadAsStreamAsync()
          use reader = new StreamReader(stream)
          do! drainSse reader stdout.WriteLine
          return Ok capturedSid
        | _ ->
          let! body = resp.Content.ReadAsStringAsync()
          match String.IsNullOrWhiteSpace body with
          | true -> () // 202 Accepted for a notification/response the client sent — nothing to write
          | false -> stdout.WriteLine(body.Trim())
          return Ok capturedSid
    with ex ->
      return Error ex.Message
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
    let io: Io =
      { Probe = fun () -> task { let! info = DaemonState.probeDaemonHttpAsync mcpPort in return info.IsSome }
        StartDaemon = fun () -> startDaemonProcess mcpPort
        Forward = forward client mcpPort stdout
        OpenServerStream = openServerStream client mcpPort stdout
        TerminateSession = terminateSession client mcpPort
        PumpStdin = pumpStdin
        ProbeInterval = TimeSpan.FromMilliseconds 500.0 }
    return! run defaultPolicy io stdout
  }
