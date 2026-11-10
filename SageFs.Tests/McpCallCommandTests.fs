module SageFs.Tests.McpCallCommandTests

/// `sagefs call` against a stand-in daemon on a real loopback socket: what it sends (the token on every request, the
/// arguments typed from the schema), what it prints and where, and that a name the schema does not have never reaches
/// the daemon. The stand-in speaks the shape the SDK's streamable-HTTP handler produces (see
/// McpStdioBridgeHealTests for how that shape was checked against the real daemon). The command against the real
/// daemon, with a token that keeps its identity across processes, is in McpCallIntegrationTests.

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpCall
open SageFs.Server

/// What one request to the stand-in carried.
type Seen =
  { HttpMethod: string
    Body: string
    Session: string option
    Token: string option }

type Reply =
  { Status: int
    Body: string
    SessionId: string option }

let tokenHeader = Capability.CapabilityTransport.headerName
let sessionHeader = "Mcp-Session-Id"

let reply (body: string) : Reply = { Status = 200; Body = body; SessionId = None }

/// A stand-in daemon: `answer` decides every response from the request, so a restart is modelled by the stand-in
/// really forgetting a session id.
type FakeDaemon(answer: Seen -> Reply) =
  let requests = ConcurrentQueue<Seen>()
  let listener = new HttpListener()
  let port = fst (TestInfrastructure.TestPorts.reservePair ())

  let serve () =
    while listener.IsListening do
      try
        let context = listener.GetContext()
        use reader = new StreamReader(context.Request.InputStream)
        let seen =
          { HttpMethod = context.Request.HttpMethod
            Body = reader.ReadToEnd()
            Session = context.Request.Headers.[sessionHeader] |> Option.ofObj
            Token = context.Request.Headers.[tokenHeader] |> Option.ofObj }
        requests.Enqueue seen
        let sent = answer seen
        let bytes = Encoding.UTF8.GetBytes sent.Body
        context.Response.StatusCode <- sent.Status
        sent.SessionId |> Option.iter (fun sid -> context.Response.Headers.[sessionHeader] <- sid)
        context.Response.ContentLength64 <- int64 bytes.Length
        context.Response.OutputStream.Write(bytes, 0, bytes.Length)
        context.Response.Close()
      with _ ->
        ()

  do
    listener.Prefixes.Add(sprintf "http://localhost:%d/" port)
    listener.Start()
    Task.Run serve |> ignore

  member _.Port = port
  member _.Requests = requests |> List.ofSeq
  member _.Stop() = try listener.Stop() with _ -> ()

let rpcMethodOf (body: string) : string =
  match body with
  | "" -> ""
  | _ ->
    use document = JsonDocument.Parse body
    match document.RootElement.TryGetProperty "method" with
    | true, found -> found.GetString()
    | false, _ -> ""

/// The request's id as the JSON it was written in: a number from `sagefs call`, a string from the bridge's heal.
let idOf (body: string) : string =
  use document = JsonDocument.Parse body
  document.RootElement.GetProperty("id").GetRawText()

let toolsList =
  """{"tools":[{"name":"run_tests","description":"Run tests in the session.","inputSchema":{"type":"object","properties":{"pattern":{"type":"string"},"timeout_seconds":{"type":["integer","null"]},"verbose":{"type":"boolean"}},"required":["pattern"]}},{"name":"get_daemon_status","description":"Daemon status.","inputSchema":{"type":"object","properties":{}}}]}"""

let rpcResult (id: string) (result: string) : string =
  sprintf """{"jsonrpc":"2.0","id":%s,"result":%s}""" id result

let textResult (text: string) (isError: bool) : string =
  let encoded = JsonSerializer.Serialize text
  sprintf """{"content":[{"type":"text","text":%s}],"isError":%s}""" encoded (match isError with true -> "true" | false -> "false")

/// A daemon that holds one session `sid-1`, lists two tools, and answers `tools/call` with `call`.
let daemonAnswering (call: Seen -> string) : Seen -> Reply =
  fun seen ->
    match seen.HttpMethod, rpcMethodOf seen.Body with
    | "DELETE", _ -> reply ""
    | _, "initialize" -> { reply (rpcResult (idOf seen.Body) """{"protocolVersion":"2024-11-05","capabilities":{}}""") with SessionId = Some "sid-1" }
    | _, "notifications/initialized" -> { Status = 202; Body = ""; SessionId = None }
    | _, "tools/list" -> reply (rpcResult (idOf seen.Body) toolsList)
    | _, "tools/call" -> reply (rpcResult (idOf seen.Body) (call seen))
    | _ -> { Status = 400; Body = "unexpected"; SessionId = None }

let running (port: int) : DaemonPresence =
  DaemonPresence.Running
    { Pid = 1
      Port = port
      DashboardPort = port + 1
      StartedAt = DateTime.UtcNow
      WorkingDirectory = "/"
      Version = "test"
      ApiVersion = None
      SessionCount = None
      ComponentFailures = [] }

type Run = { Exit: int; Out: string; Err: string }

let withDaemon (answer: Seen -> Reply) (body: FakeDaemon -> Task<unit>) : Task<unit> =
  task {
    let daemon = FakeDaemon answer
    try
      do! body daemon
    finally
      daemon.Stop()
  }

/// `sagefs call <words>` with `env` as the environment, against the stand-in on `port`.
let execute (presence: DaemonPresence) (port: int) (env: (string * string) list) (words: string) : Task<Run> =
  task {
    use out = new StringWriter()
    use err = new StringWriter()
    let getEnv name = env |> List.tryFind (fun (key, _) -> key = name) |> Option.map snd |> Option.toObj
    let! exit =
      McpCallCommand.execute
        (fun _ -> presence)
        (fun () -> port)
        getEnv
        (words.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
        out
        err
    return { Exit = exit; Out = out.ToString(); Err = err.ToString() }
  }

let callsOf (daemon: FakeDaemon) : Seen list =
  daemon.Requests |> List.filter (fun seen -> rpcMethodOf seen.Body = "tools/call")

[<Tests>]
let tests =
  testList "McpCallCommand" [

    testTask "WHY - --list prints who is calling and the tools, and the token rides the header of every request, never a message" {
      do!
        withDaemon (daemonAnswering (fun _ -> textResult "unused" false)) (fun daemon ->
          task {
            let! run = execute (running daemon.Port) daemon.Port [ "SAGEFS_MEMBER_TOKEN", "sfm_the-token" ] "--list"
            run.Exit |> Expect.equal (sprintf "exit 0 (stderr: %s)" run.Err) 0
            run.Out |> Expect.stringContains "who is calling" "identity: a member token from SAGEFS_MEMBER_TOKEN"
            run.Out |> Expect.stringContains "a tool and its first line" "run_tests"
            run.Out |> Expect.stringContains "the other tool" "get_daemon_status"
            run.Out.Contains "sfm_the-token" |> Expect.isFalse "the listing never shows the token"
            daemon.Requests |> List.isEmpty |> Expect.isFalse "the daemon was asked"
            daemon.Requests
            |> List.forall (fun seen -> seen.Token = Some "sfm_the-token")
            |> Expect.isTrue "every request, the handshake and the session end included, carried the token header"
            daemon.Requests |> List.exists (fun seen -> seen.HttpMethod = "DELETE") |> Expect.isTrue "the session is ended when the call is done"
            daemon.Requests
            |> List.forall (fun seen -> not (seen.Body.Contains "sfm_the-token"))
            |> Expect.isTrue "and no message holds it"
          })
    }

    testTask "WHY - a call types its flags from the schema and prints the tool's answer on stdout, exit 0" {
      do!
        withDaemon (daemonAnswering (fun _ -> textResult "3 passed" false)) (fun daemon ->
          task {
            let! run = execute (running daemon.Port) daemon.Port [] "run_tests --pattern=Foo --timeout_seconds 30 --verbose"
            run.Exit |> Expect.equal (sprintf "exit 0 (stderr: %s)" run.Err) 0
            run.Out |> Expect.equal "the answer and a newline" "3 passed\n"
            match callsOf daemon with
            | [ call ] ->
              use document = JsonDocument.Parse call.Body
              let arguments = document.RootElement.GetProperty("params").GetProperty("arguments")
              arguments.GetProperty("pattern").GetString() |> Expect.equal "a string" "Foo"
              arguments.GetProperty("timeout_seconds").ValueKind |> Expect.equal "a number, not the text 30" JsonValueKind.Number
              arguments.GetProperty("verbose").ValueKind |> Expect.equal "a bool, not the text" JsonValueKind.True
              document.RootElement.GetProperty("params").GetProperty("name").GetString() |> Expect.equal "the tool" "run_tests"
            | other -> failtestf "exactly one tools/call should have been sent: %A" other
          })
    }

    testTask "WHY - a wrong argument name fails, names the tool's real parameters, and the daemon is never asked to call anything" {
      do!
        withDaemon (daemonAnswering (fun _ -> textResult "should not run" false)) (fun daemon ->
          task {
            let! run = execute (running daemon.Port) daemon.Port [] "run_tests --testPattern Foo"
            run.Exit |> Expect.equal "exit 1" 1
            run.Out |> Expect.equal "nothing on stdout" ""
            run.Err |> Expect.stringContains "says the name is wrong" "'--testPattern' is not a parameter of run_tests"
            run.Err |> Expect.stringContains "gives the real one" "pattern (string, required)"
            callsOf daemon |> Expect.isEmpty "no tools/call reached the daemon"
            let! viaJson = execute (running daemon.Port) daemon.Port [] """run_tests --json {"testPattern":"Foo"}"""
            viaJson.Exit |> Expect.equal "--json is held to the same names" 1
            callsOf daemon |> Expect.isEmpty "still none"
          })
    }

    testTask "WHY - a tool that answers with an error goes to stderr with exit 1, and an anonymous caller is told what a token would change" {
      do!
        withDaemon (daemonAnswering (fun _ -> textResult "owned by another bound MCP connection" true)) (fun daemon ->
          task {
            let! anonymous = execute (running daemon.Port) daemon.Port [] "run_tests --pattern Foo"
            anonymous.Exit |> Expect.equal "exit 1" 1
            anonymous.Out |> Expect.equal "nothing on stdout" ""
            anonymous.Err |> Expect.stringContains "the gate's full text is kept" "owned by another bound MCP connection"
            anonymous.Err |> Expect.stringContains "and it says the call was anonymous" "carried no member token"
            let! asMember = execute (running daemon.Port) daemon.Port [ "SAGEFS_MEMBER_TOKEN", "sfm_x" ] "run_tests --pattern Foo"
            asMember.Exit |> Expect.equal "exit 1" 1
            asMember.Err.Contains "carried no member token" |> Expect.isFalse "a member is not told it was anonymous"
          })
    }

    testTask "WHY - a daemon that forgot the session (it restarted) costs one re-handshake, and the call still gets its answer" {
      let forgotten = ref false
      let inner = daemonAnswering (fun _ -> textResult "answered after the heal" false)
      do!
        withDaemon
          (fun seen ->
            match seen.HttpMethod, rpcMethodOf seen.Body, seen.Session with
            | "POST", "tools/call", Some "sid-1" when not forgotten.Value ->
              forgotten.Value <- true
              { Status = 404; Body = """{"error":{"code":-32001,"message":"Session not found"},"id":"","jsonrpc":"2.0"}"""; SessionId = None }
            | "POST", "initialize", _ when forgotten.Value -> { inner seen with SessionId = Some "sid-2" }
            | _ -> inner seen)
          (fun daemon ->
            task {
              let! run = execute (running daemon.Port) daemon.Port [] "run_tests --pattern Foo"
              run.Exit |> Expect.equal (sprintf "exit 0 (stderr: %s)" run.Err) 0
              run.Out |> Expect.equal "the answer" "answered after the heal\n"
              callsOf daemon |> List.map (fun seen -> seen.Session) |> Expect.equal "the same call, re-sent under the new session" [ Some "sid-1"; Some "sid-2" ]
            })
    }

    testTask "WHY - a daemon that refuses the request is said so, with its reason, and a token is not the reason it hides" {
      do!
        withDaemon (fun _ -> { Status = 403; Body = """{"error":"invalid_token","message":"that token was revoked"}"""; SessionId = None }) (fun daemon ->
          task {
            let! run = execute (running daemon.Port) daemon.Port [ "SAGEFS_MEMBER_TOKEN", "sfm_revoked" ] "--list"
            run.Exit |> Expect.equal "exit 1" 1
            run.Err |> Expect.stringContains "the daemon's own words" "that token was revoked"
            run.Err.Contains "sfm_revoked" |> Expect.isFalse "the token is not echoed"
          })
    }

    testTask "WHY - no daemon is the answer sagefs status gives, with the command to start one, and nothing is sent" {
      let port = fst (TestInfrastructure.TestPorts.reservePair ())
      let! run = execute DaemonPresence.NotRunning port [] "--list"
      run.Exit |> Expect.equal "exit 1" 1
      run.Out |> Expect.equal "nothing on stdout" ""
      run.Err |> Expect.stringContains "says so" "No daemon running"
      run.Err |> Expect.stringContains "and what to run" "claude mcp add sagefs -- sagefs mcp"
    }

    testTask "WHY - a daemon that is on no port at all still answers within the bound: the real probe finds nobody and the command says so" {
      let port = fst (TestInfrastructure.TestPorts.reservePair ())
      use out = new StringWriter()
      use err = new StringWriter()
      let! exit =
        McpCallCommand.execute
          (fun probed -> DaemonPresence.classify (DaemonState.readOnPort probed) None)
          (fun () -> port)
          (fun _ -> null)
          [ "--list" ]
          out
          err
      exit |> Expect.equal "exit 1" 1
      err.ToString() |> Expect.stringContains "says so" "No daemon running"
    }

    testTask "WHY - a usage mistake is exit 2 with the usage text, a blank --token is refused, and --help is exit 0" {
      let port = fst (TestInfrastructure.TestPorts.reservePair ())
      let! nothing = execute DaemonPresence.NotRunning port [] ""
      nothing.Exit |> Expect.equal "usage" 2
      nothing.Err |> Expect.stringContains "names the mistake" "name a tool to call"
      nothing.Err |> Expect.stringContains "and shows the usage" "usage: sagefs call --list"
      let! blank = execute DaemonPresence.NotRunning port [] "--list --token  "
      blank.Exit |> Expect.equal "blank token is a usage error" 2
      let! help = execute DaemonPresence.NotRunning port [] "--help"
      help.Exit |> Expect.equal "help" 0
      help.Out |> Expect.stringContains "prints the usage" "usage: sagefs call --list"
      let! listHelp = execute DaemonPresence.NotRunning port [] "--list --help"
      listHelp.Exit |> Expect.equal "help beside --list is the usage, with no daemon asked" 0
    }

    testTask "WHY - <tool> --help prints what the tool takes from the daemon's own schema, asks nothing to be called, and ends the session" {
      do!
        withDaemon (daemonAnswering (fun _ -> textResult "should not run" false)) (fun daemon ->
          task {
            let! run = execute (running daemon.Port) daemon.Port [] "run_tests --help"
            run.Exit |> Expect.equal (sprintf "exit 0 (stderr: %s)" run.Err) 0
            run.Err |> Expect.equal "nothing on stderr" ""
            run.Out |> Expect.stringStarts "the tool's own sentence" "run_tests: Run tests in the session."
            run.Out |> Expect.stringContains "a required parameter" "pattern (string, required)"
            run.Out |> Expect.stringContains "an optional one" "timeout_seconds (integer)"
            run.Out |> Expect.stringContains "a boolean" "verbose (boolean)"
            run.Out |> Expect.stringContains "the manual" "sagefs call discover_features --tool run_tests"
            callsOf daemon |> Expect.isEmpty "no tools/call reached the daemon"
            daemon.Requests |> List.exists (fun seen -> seen.HttpMethod = "DELETE") |> Expect.isTrue "the session is ended"
            let! noParameters = execute (running daemon.Port) daemon.Port [] "get_daemon_status --help"
            noParameters.Out |> Expect.stringContains "says it takes none" "get_daemon_status takes no parameters"
          })
    }

    testTask "WHY - <tool> --help for a name the daemon does not list says so, names the nearest tools and points at --list" {
      do!
        withDaemon (daemonAnswering (fun _ -> textResult "should not run" false)) (fun daemon ->
          task {
            let! run = execute (running daemon.Port) daemon.Port [ "SAGEFS_MEMBER_TOKEN", "sfm_x" ] "run_test --help"
            run.Exit |> Expect.equal "exit 1" 1
            run.Out |> Expect.equal "nothing on stdout" ""
            run.Err |> Expect.stringContains "says so" "lists no tool named 'run_test'"
            run.Err |> Expect.stringContains "names the nearest" "Nearest tools: run_tests."
            run.Err |> Expect.stringContains "points at the list" "sagefs call --list"
            callsOf daemon |> Expect.isEmpty "nothing was called"
          })
    }

    testTask "WHY - <tool> --help with no daemon says how to start one and still prints the usage" {
      let port = fst (TestInfrastructure.TestPorts.reservePair ())
      let! run = execute DaemonPresence.NotRunning port [] "run_tests --help"
      run.Exit |> Expect.equal "exit 1, the tool's help could not be given" 1
      run.Out |> Expect.stringContains "the usage is printed" "usage: sagefs call --list"
      run.Err |> Expect.stringContains "says so" "No daemon running"
      run.Err |> Expect.stringContains "and what to run" "claude mcp add sagefs -- sagefs mcp"
    }

    test "WHY - no tool the daemon registers takes a parameter named like one of the command's own options, so a flag is never taken from a tool" {
      for option in CommandOption.all do
        SageFs.Server.McpTools.RegisteredTools.takingParameter typeof<SageFs.Server.McpTools.SageFsTools> (CommandOption.name option)
        |> Expect.isEmpty (sprintf "no tool takes a parameter named %s" (CommandOption.name option))
    }

    test "WHY - the command line routes 'call' with every word after it, so a tool's own flags are never taken for the CLI's" {
      Program.CliCommand.parse [| "call"; "run_tests"; "--pattern"; "-v"; "--help" |]
      |> Expect.equal "all of it" (Program.CliCommand.Call [ "run_tests"; "--pattern"; "-v"; "--help" ])
    }
  ]
