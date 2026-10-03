/// The stdio bridge used to be unable to recover from a daemon restart.
///
/// The MCP session lives in the DAEMON'S MEMORY. `install-local --force`
/// restarts the daemon on purpose so a new build is served, which destroys
/// that session and turns the `Mcp-Session-Id` the bridge was still holding
/// into a 404 "Session not found" (measured: 200 before the restart, 404
/// after). `forward` turned that 404 into `ForwardError.Rejected` and NEVER
/// cleared the held id, so every later request failed identically for the
/// life of the client process — the fix this suite pins.
///
/// The subject is the REAL `McpStdioBridge.forward` over a REAL HTTP socket.
/// The stub daemon below is not a fiction: it speaks the same
/// streamable-HTTP shape the SDK's `StreamableHttpHandler` produces, verified
/// against the running daemon — 2xx carries an `Mcp-Session-Id` header and an
/// `text/event-stream` body, while the unknown-session answer is a bare 404
/// with `{"error":{"code":-32001,"message":"Session not found"}}` and NO
/// session header. That is the exact byte shape the classifier keys on, so a
/// stub that answered anything else could not make the test pass.
module SageFs.Tests.McpStdioBridgeHealTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.McpBridge

/// The header the SDK mints the session id under, and the id this stub hands out.
let sessionIdHeaderName = "Mcp-Session-Id"
let mintedSessionId = "sid-stub-1"

/// A stub daemon on a real loopback socket. `answer` decides every response
/// from the session id the request carries, so the "restart" is modelled by
/// the stub genuinely forgetting an id — not by a canned counter.
type StubDaemon(answer: int -> string option -> int * string) =
  let requests = ConcurrentQueue<string * string option>()
  let listener = new HttpListener()
  let port = StubDaemon.reservePort ()

  do
    // The bridge posts to `http://localhost:<port>/`, so the stub MUST bind the name it
    // will be reached by. Binding `127.0.0.1` while the client dials `localhost` fails
    // whenever `localhost` resolves to `::1` first — and the symptom is a bare
    // `<h1>Not Found</h1>` from some OTHER listener, which reads like the daemon answering
    // and quietly invalidates every case in this file.
    listener.Prefixes.Add(sprintf "http://localhost:%d/" port)
    listener.Start()
    Task.Run(fun () -> StubDaemon.serve listener requests answer) |> ignore

  member _.Port = port
  member _.Requests = requests
  member _.Stop() = try listener.Stop() with _ -> ()

  static member reservePort () =
    let probe = new TcpListener(IPAddress.Loopback, 0)
    probe.Start()
    let port = (probe.LocalEndpoint :?> IPEndPoint).Port
    probe.Stop()
    port

  static member private serve
    (listener: HttpListener)
    (requests: ConcurrentQueue<string * string option>)
    (answer: int -> string option -> int * string)
    : unit =
    while listener.IsListening do
      try
        let ctx = listener.GetContext()
        use reader = new StreamReader(ctx.Request.InputStream)
        let body = reader.ReadToEnd()
        let sid = ctx.Request.Headers.[sessionIdHeaderName] |> Option.ofObj
        requests.Enqueue(body, sid)
        let (status, text) = answer requests.Count sid
        let bytes = Encoding.UTF8.GetBytes text
        ctx.Response.StatusCode <- status
        if status < 300 then ctx.Response.Headers.[sessionIdHeaderName] <- mintedSessionId
        ctx.Response.ContentLength64 <- int64 bytes.Length
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
        ctx.Response.Close()
      with _ -> ()

/// The literal bytes the real daemon answers with for an id it has never
/// minted — captured from a live `curl` against 37749, not invented.
let private sessionNotFoundBody =
  """{"error":{"code":-32001,"message":"Session not found"},"id":"","jsonrpc":"2.0"}"""

let private okBody id =
  sprintf "event: message\ndata: {\"result\":{\"id\":%d,\"session\":\"%s\"}}\n\n" id mintedSessionId

/// Reserve a port, run `body`, and make sure the listener is torn down even
/// when the assertion inside `body` throws.
///
/// Returns a `Task`, because every caller is inside a `testTask` computation
/// expression and `let!` binds this one. Returning `unit` from an async-shaped
/// call site is the mismatch the compiler was reporting.
let withStubDaemon (answer: int -> string option -> int * string) (body: StubDaemon -> Task<unit>) : Task<unit> =
  task {
    let daemon = StubDaemon(answer)
    try
      do! body daemon
    finally
      daemon.Stop ()
  }

/// Records the moment of every write reaching the underlying stream, so a test
/// can put client-visible writes and session-id captures on ONE timeline. It
/// sits under a real `StdoutWriter`, so what is recorded is the production
/// framing rather than a reimplementation of it.
type private RecordingStream(timeline: ResizeArray<string>) =
  inherit MemoryStream()

  override _.Write(buffer: byte[], offset: int, count: int) =
    timeline.Add "write"
    base.Write(buffer, offset, count)

/// A production `StdoutWriter` over a `RecordingStream`.
let recordingWriter (timeline: ResizeArray<string>) =
  let stream = new RecordingStream(timeline)
  new SageFs.Server.McpStdioBridge.StdoutWriter(stream)

[<Tests>]
let tests =
  testList "The stdio bridge heals across a daemon restart" [

    testTask "WHY — a stale session id costs the client ONE re-send, not the rest of the process: the bridge clears it, re-mints it, and re-sends the same request under the new one" {
      // The stub forgets every id it is asked about UNTIL the client re-sends
      // under a freshly minted one — the shape of a daemon that restarted and
      // has no memory of the old session. Without the fix, the second POST
      // never happens and the client is left holding a dead id.
      let mutable sawFreshResend = false
      do!
        withStubDaemon
          (fun count sid ->
            match sid with
            | None -> 200, okBody 1 // the re-handshake: mints a fresh id
            | Some s when s = mintedSessionId && count >= 3 ->
                sawFreshResend <- true
                200, okBody 2
            | Some _ -> 404, sessionNotFoundBody)
          (fun daemon ->
            task {
              use stdoutStream = new MemoryStream()
              let stdout = SageFs.Server.McpStdioBridge.StdoutWriter(stdoutStream)
              let captured = ResizeArray<string>()
              use client = new HttpClient(Timeout = TestTimeouts.daemonRequest)
              let msg =
                RpcMessage.Request(
                  RpcId.N 2L,
                  "tools/call",
                  """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_daemon_status"}}"""
                )
              // Forward under a session id the daemon has never heard of.
              let! result =
                SageFs.Server.McpStdioBridge.forward client daemon.Port stdout captured.Add (Some "sid-from-the-old-daemon") msg
              result |> Expect.equal "the bridge healed the restart in place — the client's one request got its answer" (Ok())
              let written = Encoding.UTF8.GetString(stdoutStream.ToArray())
              written
              |> Expect.stringContains "the real answer reached stdout" "\"result\""
              sawFreshResend |> Expect.isTrue "the SAME request was re-sent under the freshly minted session id"
              captured
              |> Seq.toList
              |> Expect.equal "exactly one session id was captured: the one the re-handshake minted" [ mintedSessionId ]
              daemon.Requests
              |> Seq.toList
              |> List.map fst
              |> List.exists (fun b -> b.Contains "rehandshake")
              |> Expect.isTrue "the bridge ran the handshake itself, because the client (correctly) never will"
              // The client's stream must never see the re-handshake result.
              written.Contains "rehandshake"
              |> Expect.isFalse "the bridge's own handshake is not something the client asked for"
            })
    }

    testTask "a 400 is the daemon's real answer about THIS request, so it reaches the client untouched" {
      let mutable postCount = 0

      do!
        withStubDaemon
          (fun _ _ ->
            postCount <- postCount + 1
            400, """{"error":{"code":-32000,"message":"Bad Request: Mcp-Session-Id header is required"}}""")
          (fun daemon ->
            task {
              use stdoutStream = new MemoryStream()
              let stdout = SageFs.Server.McpStdioBridge.StdoutWriter(stdoutStream)
              let captured = ResizeArray<string>()
              use client = new HttpClient(Timeout = TestTimeouts.daemonRequest)
              let msg = RpcMessage.Request(RpcId.N 2L, "tools/call", """{"jsonrpc":"2.0","id":2,"method":"tools/call"}""")

              let! result =
                SageFs.Server.McpStdioBridge.forward client daemon.Port stdout captured.Add (Some "sid-1") msg

              match result with
              | Error(SageFs.Server.McpStdioBridge.ForwardError.Rejected reason) ->
                reason
                |> Expect.stringContains "the daemon's own words reach the caller" "Mcp-Session-Id header is required"
              | other -> failtestf "a 400 must be a Rejected, not a heal: %A" other

              postCount
              |> Expect.equal "no retry — a rejected request is the daemon's answer, not a bridge failure" 1

              captured |> Expect.isEmpty "the held session id is untouched: the daemon is not the problem here"

              Encoding.UTF8.GetString(stdoutStream.ToArray())
              |> Expect.equal "nothing was written to the client's stream" ""
            })
    }

    testTask "only the exact unknown-session 404 is healed: a 404 with any other body still reaches the client" {
      do!
        withStubDaemon
          (fun _ _ ->
            404, """{"error":{"code":-32000,"message":"No discovered tests matched the explicit run filters"}}""")
          (fun daemon ->
            task {
              use stdoutStream = new MemoryStream()
              let stdout = SageFs.Server.McpStdioBridge.StdoutWriter(stdoutStream)
              let captured = ResizeArray<string>()
              use client = new HttpClient(Timeout = TestTimeouts.daemonRequest)
              let msg = RpcMessage.Request(RpcId.N 2L, "tools/call", """{"jsonrpc":"2.0","id":2,"method":"tools/call"}""")

              let! result =
                SageFs.Server.McpStdioBridge.forward client daemon.Port stdout captured.Add (Some "sid-1") msg

              match result with
              | Error(SageFs.Server.McpStdioBridge.ForwardError.Rejected _) -> ()
              | other -> failtestf "an unrelated 404 must not be mistaken for a stale session: %A" other

              captured |> Expect.isEmpty "no id was minted, so nothing was cleared"
            })
    }

    testTask "a request forwarded with NO session id is never healed, because there was no stale id to clear" {
      // The retry is only ever earned by holding a stale id. Without one the
      // 404 is the daemon's answer and there is nothing for the bridge to fix.
      let mutable postCount = 0

      do!
        withStubDaemon
          (fun _ _ ->
            postCount <- postCount + 1
            404, sessionNotFoundBody)
          (fun daemon ->
            task {
              use stdoutStream = new MemoryStream()
              let stdout = SageFs.Server.McpStdioBridge.StdoutWriter(stdoutStream)
              let captured = ResizeArray<string>()
              use client = new HttpClient(Timeout = TestTimeouts.daemonRequest)
              let msg = RpcMessage.Request(RpcId.N 2L, "tools/call", """{"jsonrpc":"2.0","id":2,"method":"tools/call"}""")

              let! result =
                SageFs.Server.McpStdioBridge.forward client daemon.Port stdout captured.Add None msg

              match result with
              | Error(SageFs.Server.McpStdioBridge.ForwardError.Rejected _) -> ()
              | other -> failtestf "expected the daemon's own rejection to be reported: %A" other

              postCount |> Expect.equal "one POST — there was no session id to heal" 1
            })
    }

    testTask "the new session id is captured BEFORE anything is written to stdout, so issue #138's ordering cannot come back through the healing path" {
      // #138 is exactly this ordering, and the healing path is a second place
      // it could be got wrong. Record capture and write into ONE list: if a
      // capture ever lands after a write, the client can beat it and the next
      // request goes out with no session id.
      let timeline = ResizeArray<string>()
      let mutable postCount = 0

      do!
        withStubDaemon
          (fun _ sid ->
            postCount <- postCount + 1
            match sid with
            | None -> 200, okBody 1
            | Some _ when postCount >= 3 -> 200, okBody 2
            | Some _ -> 404, sessionNotFoundBody)
          (fun daemon ->
            task {
              let stdout = recordingWriter timeline
              use client = new HttpClient(Timeout = TestTimeouts.daemonRequest)
              let msg = RpcMessage.Request(RpcId.N 2L, "tools/call", """{"jsonrpc":"2.0","id":2,"method":"tools/call"}""")

              let! result =
                SageFs.Server.McpStdioBridge.forward
                  client
                  daemon.Port
                  stdout
                  (fun sid -> timeline.Add(sprintf "capture:%s" sid))
                  (Some "stale")
                  msg

              result |> Expect.equal "the heal succeeded" (Ok())

              timeline
              |> Seq.toList
              |> Expect.equal "every capture precedes every client-visible write" [ "capture:sid-stub-1"; "write" ]
            })
    }

    testCase "the classifier only fires on the daemon's exact unknown-session answer" <| fun _ ->
      // The production predicate, over the shapes that must and must not heal.
      [ 404, sessionNotFoundBody, true
        404, """{"error":{"code":-32001,"message":"Session not found"}}""", true
        400, sessionNotFoundBody, false // wrong status: a bad request, not a stale id
        404, """{"error":{"code":-32001,"message":"Forbidden"}}""", false // wrong message
        404, """{"error":{"code":-32000,"message":"Session not found"}}""", false // wrong code
        404, "Not Found", false // an HTML 404 from something that is not the MCP endpoint
        500, sessionNotFoundBody, false // a server fault is not a stale session
        200, sessionNotFoundBody, false // a 2xx carrying that text is not a 404 at all
      ]
      |> List.iter (fun (status, body, expected) ->
        SageFs.Server.McpStdioBridge.isSessionInvalid status body
        |> Expect.equal (sprintf "status %d with %s" status body) expected)
  ]
