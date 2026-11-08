module SageFs.Tests.McpStdioBridgeE2ETests

open System
open System.Diagnostics
open System.Text
open System.Threading.Tasks
open Expecto
open Expecto.Flip

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private sageFsExe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

/// The one real end-to-end regression test: spawn the BUILT `sagefs mcp` as
/// a real process on a fresh port with no daemon running yet — the exact
/// shape of the bug report (a client that starts before the daemon gets
/// ECONNREFUSED and never recovers) — write real JSON-RPC to its stdin, and
/// assert a well-formed MCP response comes back on stdout with the tools in
/// it. Nothing here is faked: not the process, not the daemon it spawns, not
/// the protocol bytes.
let private runBridgeSmokeTest () : Task<unit> =
  task {
    let mcpPort, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
    let dataDir = IO.Path.Combine(IO.Path.GetTempPath(), "sagefs-test", "mcp-stdio-e2e", Guid.NewGuid().ToString("N"))

    let psi = ProcessStartInfo()
    psi.FileName <- sageFsExe
    psi.ArgumentList.Add("mcp")
    psi.ArgumentList.Add("--mcp-port")
    psi.ArgumentList.Add(string mcpPort)
    psi.ArgumentList.Add("--owner-pid")
    psi.ArgumentList.Add(string (System.Diagnostics.Process.GetCurrentProcess().Id))
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.StandardOutputEncoding <- Encoding.UTF8
    psi.StandardInputEncoding <- Encoding.UTF8
    psi.Environment["SAGEFS_DATA_DIR"] <- dataDir

    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    use proc = Process.Start(psi)
    let stderrBuf = Text.StringBuilder()
    proc.ErrorDataReceived.Add(fun e -> match e.Data with null -> () | line -> stderrBuf.AppendLine(line) |> ignore)
    proc.BeginErrorReadLine()

    // The daemon this spawns needs real warmup time (build-free but still
    // ASP.NET Core startup) — generous but bounded, same order of magnitude
    // as HttpApiIntegrationTests' own daemon-readiness budget.
    let readTimeout = TestTimeouts.daemonFirstOutput

    let readLineWithTimeout () : Task<string option> =
      task {
        let readTask = proc.StandardOutput.ReadLineAsync()
        let! completed = Task.WhenAny(readTask, Task.Delay(readTimeout))
        match Object.ReferenceEquals(completed, readTask) with
        | true -> return Option.ofObj readTask.Result
        | false -> return None
      }

    let daemonPid = ref (None: int option)
    try
      try
        do!
          proc.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"e2e-test","version":"0.0.1"}}}"""
          )
        do! proc.StandardInput.FlushAsync()

        let! initLine = readLineWithTimeout ()
        match initLine with
        | None ->
          failwith (
            sprintf
              "sagefs mcp produced no stdout line within %gs — exited=%b exitCode=%s stderr:\n%s"
              readTimeout.TotalSeconds
              proc.HasExited
              (if proc.HasExited then string proc.ExitCode else "n/a")
              (stderrBuf.ToString())
          )
        | Some line ->
          line |> Expect.stringContains "the initialize response names the server" "\"name\":\"SageFs\""
          line |> Expect.stringContains "the initialize response carries the request id back" "\"id\":1"

          // Confirm the daemon this bridge started is real, then hold its
          // pid for cleanup — probed the same way the CLI's own `status`
          // command does.
          match SageFs.DaemonState.readOnPort mcpPort with
          | Some info -> daemonPid.Value <- Some info.Pid
          | None -> ()

          do! proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""")
          do! proc.StandardInput.FlushAsync()

          let! toolsLine = readLineWithTimeout ()
          match toolsLine with
          | None -> failwith (sprintf "no tools/list response within %gs — stderr:\n%s" readTimeout.TotalSeconds (stderrBuf.ToString()))
          | Some line2 ->
            line2 |> Expect.stringContains "the tools/list response carries the request id back" "\"id\":2"
            line2 |> Expect.stringContains "the real tool catalogue came back, not an empty list" "\"send_fsharp_code\""
            line2 |> Expect.stringContains "explicit project creation tool is present" "\"create_project_session\""
            line2 |> Expect.stringContains "lease tool is present" "\"acquire_full_build_lease\""
            Expect.isFalse "retired create_session must not be registered" (line2.Contains("\"name\":\"create_session\""))
            Expect.isFalse "retired get_fsi_status must not be registered" (line2.Contains("\"name\":\"get_fsi_status\""))
            Expect.isFalse "retired get_startup_info must not be registered" (line2.Contains("\"name\":\"get_startup_info\""))
      finally
        // Let the bridge terminate its MCP session and exit cleanly.
        try
          proc.StandardInput.Close()
        with _ -> ()
        proc.WaitForExit(TestTimeouts.patience) |> ignore
        match proc.HasExited with
        | false -> (try proc.Kill() with _ -> ())
        | true -> ()
    finally
      // The daemon `sagefs mcp` spawned is detached by design (other
      // clients are meant to share it) — this test is the only "other
      // client", so it is responsible for taking it back down. Kill only
      // the exact pid this test observed, never by name.
      match daemonPid.Value with
      | Some pid ->
        try
          let d = Process.GetProcessById(pid)
          match d.HasExited with
          | false -> d.Kill()
          | true -> ()
        with _ -> ()
      | None -> ()
  }

/// Issue #138 reproduced literally: write `initialize`, `notifications/
/// initialized` AND `tools/list` to stdin before reading anything back —
/// exactly the shape of the bug report's own bash repro (`{ printf ...; sleep
/// 4; } | sagefs mcp`) and of a fast MCP client that doesn't serialize its
/// own writes against the bridge's replies. Unlike `runBridgeSmokeTest`
/// (which reads `initialize`'s reply before sending anything else), this
/// pipes all three lines into stdin cold, before the daemon this bridge
/// spawns is even reachable — so they all land in the bridge's `Pending`
/// queue together and get released as ONE batch (`McpBridge.drain`) the
/// moment the daemon answers healthy. That batch used to be dispatched via
/// independent, unordered `Task.Run` calls sharing one stale (pre-session)
/// `sessionId` snapshot, so every request after `initialize` went out with
/// no `Mcp-Session-Id` header and got rejected — the SAME race the
/// write-before-capture fix closes for a single reacting client, one level
/// up. If this regresses, `tools/list` comes back empty or as an error
/// instead of the real 50+-tool catalogue.
let private runPipelinedBatchReproTest () : Task<unit> =
  task {
    let mcpPort, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
    let dataDir = IO.Path.Combine(IO.Path.GetTempPath(), "sagefs-test", "mcp-stdio-e2e-batch", Guid.NewGuid().ToString("N"))

    let psi = ProcessStartInfo()
    psi.FileName <- sageFsExe
    psi.ArgumentList.Add("mcp")
    psi.ArgumentList.Add("--mcp-port")
    psi.ArgumentList.Add(string mcpPort)
    psi.ArgumentList.Add("--owner-pid")
    psi.ArgumentList.Add(string (System.Diagnostics.Process.GetCurrentProcess().Id))
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.StandardOutputEncoding <- Encoding.UTF8
    psi.StandardInputEncoding <- Encoding.UTF8
    psi.Environment["SAGEFS_DATA_DIR"] <- dataDir

    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    use proc = Process.Start(psi)
    let stderrBuf = Text.StringBuilder()
    proc.ErrorDataReceived.Add(fun e -> match e.Data with null -> () | line -> stderrBuf.AppendLine(line) |> ignore)
    proc.BeginErrorReadLine()

    let readTimeout = TestTimeouts.daemonFirstOutput
    let readLineWithTimeout () : Task<string option> =
      task {
        let readTask = proc.StandardOutput.ReadLineAsync()
        let! completed = Task.WhenAny(readTask, Task.Delay(readTimeout))
        match Object.ReferenceEquals(completed, readTask) with
        | true -> return Option.ofObj readTask.Result
        | false -> return None
      }

    let daemonPid = ref (None: int option)
    try
      try
        // All three, cold, before reading a single byte back — the whole
        // point of this test.
        do!
          proc.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"e2e-batch-test","version":"0.0.1"}}}"""
          )
        do! proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")
        do! proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")
        do! proc.StandardInput.FlushAsync()

        let! initLine = readLineWithTimeout ()
        match initLine with
        | None ->
          failwith (
            sprintf
              "sagefs mcp produced no stdout line within %gs — exited=%b exitCode=%s stderr:\n%s"
              readTimeout.TotalSeconds
              proc.HasExited
              (if proc.HasExited then string proc.ExitCode else "n/a")
              (stderrBuf.ToString())
          )
        | Some line ->
          line |> Expect.stringContains "the initialize response names the server" "\"name\":\"SageFs\""

          match SageFs.DaemonState.readOnPort mcpPort with
          | Some info -> daemonPid.Value <- Some info.Pid
          | None -> ()

          let! toolsLine = readLineWithTimeout ()
          match toolsLine with
          | None -> failwith (sprintf "no tools/list response within %gs — stderr:\n%s" readTimeout.TotalSeconds (stderrBuf.ToString()))
          | Some line2 ->
            line2 |> Expect.stringContains "tools/list carries its request id back — not rejected as a stray 400" "\"id\":2"
            line2
            |> Expect.stringContains
              "the real tool catalogue came back — the batch wasn't forwarded without the session id"
              "\"send_fsharp_code\""
      finally
        try
          proc.StandardInput.Close()
        with _ -> ()
        proc.WaitForExit(TestTimeouts.patience) |> ignore
        match proc.HasExited with
        | false -> (try proc.Kill() with _ -> ())
        | true -> ()
    finally
      match daemonPid.Value with
      | Some pid ->
        try
          let d = Process.GetProcessById(pid)
          match d.HasExited with
          | false -> d.Kill()
          | true -> ()
        with _ -> ()
      | None -> ()
  }

/// P9: the bridge must outlive the daemon it was talking to. `sagefs stop` ends the daemon an agent's bridge is
/// connected to, `install-local.fsx` does it on purpose to serve a new build, and a client that sees its MCP
/// server die on every upgrade has to be restarted by a person. Measured against the 0.6.940 tool on a
/// throwaway daemon: the bridge answered `sagefs mcp: the daemon became unreachable: server stream closed` on
/// stderr and exited 1, the instant the daemon's push stream closed.
///
/// Real processes throughout: a daemon, the BUILT `sagefs mcp` bridge on its stdio, the daemon killed by its
/// exact pid, a second daemon on the same port. The bridge has to stay alive, tell the client its lists may
/// have changed (the new daemon is a new MCP session and may be a new build), and answer the next request.
let private runDaemonUpgradeTest () : Task<unit> =
  task {
    let mcpPort, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
    let workingDir = IO.Path.GetTempPath()
    let! firstDaemon, firstClient = SageFs.Tests.HttpApiIntegrationTests.startDaemonWithArgs mcpPort workingDir [ "--no-resume" ]
    firstClient.Dispose()
    let secondDaemon = ref (None: Process option)

    let psi = ProcessStartInfo()
    psi.FileName <- sageFsExe
    psi.ArgumentList.Add("mcp")
    psi.ArgumentList.Add("--mcp-port")
    psi.ArgumentList.Add(string mcpPort)
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.StandardOutputEncoding <- Encoding.UTF8
    psi.StandardInputEncoding <- Encoding.UTF8
    psi.Environment["SAGEFS_DATA_DIR"] <- IO.Path.Combine(IO.Path.GetTempPath(), "sagefs-test", "mcp-stdio-upgrade", Guid.NewGuid().ToString("N"))
    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    use bridge = Process.Start(psi)
    let stderrBuf = Text.StringBuilder()
    bridge.ErrorDataReceived.Add(fun e -> match e.Data with null -> () | line -> stderrBuf.AppendLine(line) |> ignore)
    bridge.BeginErrorReadLine()

    let seen = Collections.Generic.List<string>()
    let describeBridge () =
      sprintf
        "bridge exited=%b exitCode=%s\nstdout lines it wrote: %A\nstderr:\n%s"
        bridge.HasExited
        (match bridge.HasExited with true -> string bridge.ExitCode | false -> "n/a")
        (List.ofSeq seen)
        (stderrBuf.ToString())
    let send (line: string) =
      task {
        do! bridge.StandardInput.WriteLineAsync(line)
        do! bridge.StandardInput.FlushAsync()
      }
    /// Reads stdout until a line carrying `"id":<id>` arrives (notifications on the way are kept in `seen`).
    let readAnswerTo (id: int) : Task<string option> =
      task {
        let deadline = DateTime.UtcNow + TestTimeouts.daemonFirstOutput
        let mutable answer = None
        let mutable stop = false
        while not stop do
          let remaining = deadline - DateTime.UtcNow
          match remaining > TimeSpan.Zero with
          | false -> stop <- true
          | true ->
            let readTask = bridge.StandardOutput.ReadLineAsync()
            let! completed = Task.WhenAny(readTask, Task.Delay remaining)
            match Object.ReferenceEquals(completed, readTask) with
            | false -> stop <- true
            | true ->
              match readTask.Result with
              | null -> stop <- true
              | line ->
                seen.Add line
                let answersId =
                  try
                    use doc = Text.Json.JsonDocument.Parse line
                    match doc.RootElement.TryGetProperty "id" with
                    | true, value -> value.ValueKind = Text.Json.JsonValueKind.Number && value.GetInt32() = id
                    | false, _ -> false
                  with _ -> false
                match answersId with
                | true ->
                  answer <- Some line
                  stop <- true
                | false -> ()
        return answer
      }

    try
      try
        do! send """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"upgrade-e2e","version":"0.0.1"}}}"""
        let! initAnswer = readAnswerTo 1
        initAnswer |> Option.isSome |> Expect.isTrue (sprintf "the bridge answered initialize against the first daemon\n%s" (describeBridge ()))
        do! send """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
        do! send """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""
        let! firstTools = readAnswerTo 2
        firstTools |> Option.isSome |> Expect.isTrue (sprintf "tools/list answered against the first daemon\n%s" (describeBridge ()))

        // The upgrade: the daemon the bridge talks to goes away, and another one takes its port.
        firstDaemon.Kill()
        firstDaemon.WaitForExit(TestTimeouts.patience) |> ignore
        let! replacement = SageFs.Tests.HttpApiIntegrationTests.startDaemonWithArgs mcpPort workingDir [ "--no-resume" ]
        secondDaemon.Value <- Some(fst replacement)
        (snd replacement).Dispose()

        bridge.HasExited |> Expect.isFalse (sprintf "the bridge outlives the daemon it was talking to\n%s" (describeBridge ()))

        do! send """{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}"""
        let! secondTools = readAnswerTo 3
        match secondTools with
        | None -> failwithf "no answer to tools/list after the daemon was replaced\n%s" (describeBridge ())
        | Some line -> line |> Expect.stringContains "the replacement daemon's tool list reached the client" "\"send_fsharp_code\""
        seen
        |> Seq.exists (fun line -> line.Contains "notifications/tools/list_changed")
        |> Expect.isTrue (sprintf "the client was told its tool list may have changed, because the daemon behind it is a new MCP session\n%s" (describeBridge ()))
      finally
        try bridge.StandardInput.Close() with _ -> ()
        bridge.WaitForExit(TestTimeouts.patience) |> ignore
        match bridge.HasExited with
        | false -> (try bridge.Kill() with _ -> ())
        | true -> ()
    finally
      for daemon in [ Some firstDaemon; secondDaemon.Value ] |> List.choose id do
        try
          match daemon.HasExited with
          | false -> daemon.Kill()
          | true -> ()
        with _ -> ()
  }

/// A daemon the test can upgrade on cue, and hold in the middle of minting the session that follows. Streamable
/// HTTP as the SDK speaks it (the bytes `McpStdioBridgeHealTests` pins): a 2xx `initialize` carries an
/// `Mcp-Session-Id`, an id the daemon never minted is a 404 `-32001 Session not found`, a GET with a live id is
/// an event stream that stays open. The dashboard port answers the bridge's health probe.
///
/// `Restart` is the upgrade: every session and every open stream goes, and the next `initialize` is held until
/// `ReleaseInitialize`, so a test can put stdin lines into the re-handshake's gap on purpose instead of hoping
/// to hit it.
type UpgradableDaemon(mcpPort: int) =
  let mcp = new Net.HttpListener()
  let dashboard = new Net.HttpListener()
  let live = Collections.Concurrent.ConcurrentDictionary<string, unit>()
  let streams = Collections.Concurrent.ConcurrentDictionary<Guid, string * Net.HttpListenerResponse>()
  let log = Collections.Concurrent.ConcurrentQueue<string * string option * int>()
  let streamOpened = new Threading.SemaphoreSlim(0)
  let mutable generation = 1
  let mutable minted = 0
  let mutable hold =
    let open' = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    open'.SetResult()
    open'
  let mutable arrived = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

  let sessionNotFound = """{"error":{"code":-32001,"message":"Session not found"},"id":"","jsonrpc":"2.0"}"""

  let reply (response: Net.HttpListenerResponse) (status: int) (body: string) : Task =
    task {
      let bytes = Encoding.UTF8.GetBytes body
      response.StatusCode <- status
      response.ContentType <- "application/json"
      response.ContentLength64 <- int64 bytes.Length
      do! response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
      response.Close()
    }

  let handleMcp (ctx: Net.HttpListenerContext) : Task =
    task {
      let request = ctx.Request
      let response = ctx.Response
      let sessionId = request.Headers.["Mcp-Session-Id"] |> Option.ofObj
      let isLive =
        match sessionId with
        | Some sid -> live.ContainsKey sid
        | None -> false
      match request.HttpMethod with
      | "POST" ->
        use reader = new IO.StreamReader(request.InputStream)
        let! body = reader.ReadToEndAsync()
        use doc = Text.Json.JsonDocument.Parse body
        let root = doc.RootElement
        let methodName =
          match root.TryGetProperty "method" with
          | true, m -> m.GetString()
          | false, _ -> ""
        let id =
          match root.TryGetProperty "id" with
          | true, value -> Some(value.GetRawText())
          | false, _ -> None
        log.Enqueue(methodName, sessionId, generation)
        match methodName, id with
        | "initialize", Some id ->
          arrived.TrySetResult() |> ignore
          do! hold.Task
          let fresh = sprintf "fake-%d" (lock live (fun () -> minted <- minted + 1; minted))
          live.[fresh] <- ()
          response.Headers.["Mcp-Session-Id"] <- fresh
          do!
            reply
              response
              200
              (sprintf
                """{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":"2025-06-18","capabilities":{"tools":{"listChanged":true}},"serverInfo":{"name":"UpgradableDaemon","version":"1"}}}"""
                id)
        | _, _ when not isLive -> do! reply response 404 sessionNotFound
        | _, None -> do! reply response 202 ""
        | _, Some id -> do! reply response 200 (sprintf """{"jsonrpc":"2.0","id":%s,"result":{"tools":[]}}""" id)
      | "GET" when isLive ->
        response.ContentType <- "text/event-stream"
        response.SendChunked <- true
        let bytes = Encoding.UTF8.GetBytes ": open\n\n"
        do! response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
        do! response.OutputStream.FlushAsync()
        streams.[Guid.NewGuid()] <- (sessionId.Value, response)
        streamOpened.Release() |> ignore
      | "DELETE" ->
        sessionId |> Option.iter (fun sid -> live.TryRemove sid |> ignore)
        do! reply response 200 ""
      | _ -> do! reply response 404 sessionNotFound
    }

  let handleDashboard (ctx: Net.HttpListenerContext) : Task =
    task {
      match ctx.Request.Url.AbsolutePath with
      | "/dashboard" -> do! reply ctx.Response 200 "ok"
      | _ -> do! reply ctx.Response 404 "not here"
    }

  /// The accept loop starts hot and awaits `GetContextAsync`; each request runs on until its first await. No pool
  /// thread is parked, and a held `initialize` does not hold up the next request.
  let serve (listener: Net.HttpListener) (handle: Net.HttpListenerContext -> Task) : unit =
    task {
      while listener.IsListening do
        try
          let! ctx = listener.GetContextAsync()
          (try handle ctx with _ -> Task.CompletedTask) |> ignore
        with _ -> ()
    }
    |> ignore

  do
    mcp.Prefixes.Add(sprintf "http://localhost:%d/" mcpPort)
    dashboard.Prefixes.Add(sprintf "http://localhost:%d/" (mcpPort + 1))
    mcp.Start()
    dashboard.Start()
    serve mcp handleMcp
    serve dashboard handleDashboard

  /// Every POST the daemon has answered: method, the session id it carried, and the daemon generation it hit.
  member _.Requests = log.ToArray()
  member _.Generation = generation
  member _.LiveSessions = live.Keys |> Seq.sort |> List.ofSeq
  member _.OpenStreams = streams.Values |> Seq.map fst |> Seq.sort |> List.ofSeq
  member _.StreamOpened = streamOpened
  /// Completes when an `initialize` has arrived since the last `Restart`.
  member _.InitializeArrived: Task = arrived.Task

  /// The upgrade: every session and every open stream ends, and the next `initialize` is held.
  member _.Restart() : unit =
    generation <- generation + 1
    hold <- TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    arrived <- TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    live.Clear()
    for entry in streams.Values do
      try (snd entry).Abort() with _ -> ()
    streams.Clear()

  member _.ReleaseInitialize() : unit = hold.TrySetResult() |> ignore

  /// Push a notification into every open stream of a session the daemon still knows.
  member _.Push(json: string) : Task =
    task {
      let bytes = Encoding.UTF8.GetBytes(sprintf "event: message\ndata: %s\n\n" json)
      for sid, response in streams.Values do
        match live.ContainsKey sid with
        | true ->
          do! response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
          do! response.OutputStream.FlushAsync()
        | false -> ()
    }

  interface IDisposable with
    member _.Dispose() =
      for listener in [ mcp; dashboard ] do
        try listener.Close() with _ -> ()

/// P17, with a real bridge process: stdin lines arrive while the bridge is minting its new session after a
/// daemon upgrade. The daemon is held in the middle of that `initialize`, so the gap is as long as the test
/// wants. Exactly one session is minted, exactly one push stream is open at the end, each pushed notification
/// reaches the client once, and every request the client sent in the gap is answered under the new session.
let runRehandshakeRaceTest () : Task<unit> =
  task {
    let mcpPort, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
    use daemon = new UpgradableDaemon(mcpPort)

    let psi = ProcessStartInfo()
    psi.FileName <- sageFsExe
    psi.ArgumentList.Add("mcp")
    psi.ArgumentList.Add("--mcp-port")
    psi.ArgumentList.Add(string mcpPort)
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.StandardOutputEncoding <- Encoding.UTF8
    psi.StandardInputEncoding <- Encoding.UTF8
    psi.Environment["SAGEFS_DATA_DIR"] <- IO.Path.Combine(IO.Path.GetTempPath(), "sagefs-test", "mcp-stdio-race", Guid.NewGuid().ToString("N"))
    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    use bridge = Process.Start(psi)
    let stderrBuf = Text.StringBuilder()
    bridge.ErrorDataReceived.Add(fun e -> match e.Data with null -> () | line -> stderrBuf.AppendLine(line) |> ignore)
    bridge.BeginErrorReadLine()

    // Everything the bridge writes to stdout, in order, with a signal per line, so the test waits for what it expects.
    let seen = Collections.Concurrent.ConcurrentQueue<string>()
    let wrote = new Threading.SemaphoreSlim(0)
    task {
      let mutable reading = true
      while reading do
        let! line = bridge.StandardOutput.ReadLineAsync()
        match line with
        | null -> reading <- false
        | text ->
          seen.Enqueue text
          wrote.Release() |> ignore
    }
    |> ignore

    let describe () =
      sprintf
        "stdout: %A\nstderr:\n%s\ndaemon requests (method, session, generation): %A\nlive sessions: %A\nopen streams: %A"
        (List.ofSeq seen)
        (stderrBuf.ToString())
        daemon.Requests
        daemon.LiveSessions
        daemon.OpenStreams
    let awaitLine (found: string -> bool) : Task<string option> =
      task {
        let deadline = DateTime.UtcNow + TestTimeouts.daemonFirstOutput
        let mutable result = None
        let mutable looking = true
        while looking do
          match seen.ToArray() |> Array.tryFind found with
          | Some line ->
            result <- Some line
            looking <- false
          | None ->
            let remaining = deadline - DateTime.UtcNow
            match remaining > TimeSpan.Zero with
            | false -> looking <- false
            | true ->
              let! signalled = wrote.WaitAsync remaining
              looking <- signalled
        return result
      }
    let send (line: string) =
      task {
        do! bridge.StandardInput.WriteLineAsync line
        do! bridge.StandardInput.FlushAsync()
      }
    let answerTo (id: int) = awaitLine (fun line -> line.Contains(sprintf "\"id\":%d," id) || line.Contains(sprintf "\"id\":%d}" id))
    let count (text: string) = seen.ToArray() |> Array.filter (fun line -> line.Contains text) |> Array.length

    try
        do! send """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"race-e2e","version":"0.0.1"}}}"""
        let! initialized = answerTo 1
        initialized |> Expect.isSome (sprintf "initialize was answered\n%s" (describe ()))
        do! send """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
        do! send """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""
        let! listed = answerTo 2
        listed |> Expect.isSome (sprintf "tools/list was answered by the first daemon\n%s" (describe ()))
        let! streamed = daemon.StreamOpened.WaitAsync TestTimeouts.daemonFirstOutput
        streamed |> Expect.isTrue (sprintf "the bridge opened its push stream\n%s" (describe ()))

        // The upgrade. The new daemon answers the health probe at once; its first `initialize` is held.
        daemon.Restart()
        let! arrived = Task.WhenAny([| daemon.InitializeArrived; Task.Delay TestTimeouts.daemonFirstOutput |])
        obj.ReferenceEquals(arrived, daemon.InitializeArrived)
        |> Expect.isTrue (sprintf "the bridge began minting a session on the new daemon\n%s" (describe ()))

        // The gap: the client keeps working while the bridge's `initialize` is still unanswered.
        do! send """{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}"""
        do! send """{"jsonrpc":"2.0","id":4,"method":"tools/list","params":{}}"""
        do! Task.Delay TestTimeouts.processSettle
        daemon.Requests
        |> Array.filter (fun (_, _, generation) -> generation = daemon.Generation)
        |> Array.map (fun (name, _, _) -> name)
        |> Expect.equal (sprintf "nothing but the bridge's own initialize reached the new daemon while the session was being minted\n%s" (describe ())) [| "initialize" |]

        daemon.ReleaseInitialize()
        let! third = answerTo 3
        third |> Expect.isSome (sprintf "the request sent in the gap was answered\n%s" (describe ()))
        let! fourth = answerTo 4
        fourth |> Expect.isSome (sprintf "so was the second\n%s" (describe ()))

        // Exactly one session was minted on the new daemon, and both requests rode it.
        daemon.Requests
        |> Array.filter (fun (name, _, generation) -> generation = daemon.Generation && name = "initialize")
        |> Array.length
        |> Expect.equal (sprintf "one session was minted for this bridge, not a second by a heal\n%s" (describe ())) 1
        daemon.Requests
        |> Array.filter (fun (name, _, generation) -> generation = daemon.Generation && name = "tools/list")
        |> Array.forall (fun (_, sid, _) -> sid = Some "fake-2")
        |> Expect.isTrue (sprintf "every request after the upgrade carried the new session id\n%s" (describe ()))

        // One push stream, so one copy of each notification.
        let! reopened = daemon.StreamOpened.WaitAsync TestTimeouts.daemonFirstOutput
        reopened |> Expect.isTrue (sprintf "the bridge opened a push stream on the new session\n%s" (describe ()))
        do! Task.Delay TestTimeouts.processSettle
        daemon.OpenStreams |> Expect.equal (sprintf "exactly one push stream is open\n%s" (describe ())) [ "fake-2" ]
        do! daemon.Push """{"jsonrpc":"2.0","method":"notifications/message","params":{"data":"p17-once"}}"""
        let! pushed = awaitLine (fun line -> line.Contains "p17-once")
        pushed |> Expect.isSome (sprintf "the pushed notification reached the client\n%s" (describe ()))
        do! Task.Delay TestTimeouts.processSettle
        count "p17-once" |> Expect.equal (sprintf "and reached it once\n%s" (describe ())) 1
        count "notifications/tools/list_changed" |> Expect.equal "the client was told once that its tool list may have changed" 1
    finally
      try bridge.StandardInput.Close() with _ -> ()
      bridge.WaitForExit(TestTimeouts.patience) |> ignore
      match bridge.HasExited with
      | false -> (try bridge.Kill() with _ -> ())
      | true -> ()
  }

[<Tests>]
let tests =
  testList "McpStdioBridge E2E" [
    Integration.hostCase
      "sagefs mcp: stdin lines arriving while the new session is minted after a daemon upgrade wait for it, and one session and one push stream result"
      (fun () -> runRehandshakeRaceTest () |> _.GetAwaiter() |> _.GetResult())

    Integration.hostCase
      "sagefs mcp: cold start with no daemon running answers initialize and tools/list on stdout"
      (fun () -> runBridgeSmokeTest () |> _.GetAwaiter() |> _.GetResult())

    Integration.hostCase
      "sagefs mcp: issue #138 — initialize, notifications/initialized and tools/list piped in before any reply, tools/list still gets the session id"
      (fun () -> runPipelinedBatchReproTest () |> _.GetAwaiter() |> _.GetResult())

    Integration.hostCase
      "sagefs mcp: the bridge outlives a daemon upgrade, tells the client its lists changed, and answers the next request"
      (fun () -> runDaemonUpgradeTest () |> _.GetAwaiter() |> _.GetResult())
  ]
