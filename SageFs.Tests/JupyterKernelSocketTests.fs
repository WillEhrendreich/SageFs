/// The Jupyter kernel over a real ZMQ socket.
///
/// Every other test of the kernel is in-process: the protocol, the HMAC, the request handling,
/// the state machine, the wire serialization. None of them opens a socket, so none of them can
/// catch a kernel that does not answer on the wire, and the feature is marked experimental for
/// exactly that reason.
///
/// This one starts the real kernel as a process, talks to it with a real ZMQ dealer socket, and
/// sends the frames a Jupyter client sends: the delimiter, the HMAC, the header, the parent, the
/// metadata and the content. The answer has to come back over that socket, framed and signed the
/// way the protocol says, and the value it carries has to be the one the daemon computed.
///
/// It needs a daemon, because the kernel forwards the code to the daemon's own `/exec`, the same
/// path every editor integration uses.
module SageFs.Tests.JupyterKernelSocketTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open NetMQ
open NetMQ.Sockets
open SageFs
open SageFs.JupyterKernel

module Integration = SageFs.Tests.TestInfrastructure.Integration
module Harness = SageFs.Tests.HttpApiIntegrationTests

let private delimiter = "<IDS|MSG>"

/// The connection file a Jupyter client writes and the kernel reads.
///
/// The five ZMQ ports are the kernel's own sockets (shell, iopub, stdin,
/// control, heartbeat). They are reserved through the shared allocator, so
/// they come from this tier's assigned range rather than from whatever the
/// OS hands out — the same reason the daemon's port pair does.
let private connectionFile (dir: string) (key: string) : string =
  let path = Path.Combine(dir, "connection.json")
  let shellPort, iopubPort, stdinPort, controlPort, hbPort =
    match SageFs.Tests.TestInfrastructure.TestPorts.reservePorts 5 with
    | [ shell; iopub; stdin; control; hb ] -> shell, iopub, stdin, control, hb
    | other -> failtestf "TestPorts.reservePorts 5 returned %A, not five ports" other
  let obj = {|
    transport = "tcp"
    ip = "127.0.0.1"
    shell_port = shellPort
    iopub_port = iopubPort
    stdin_port = stdinPort
    control_port = controlPort
    hb_port = hbPort
    key = key
    signature_scheme = "hmac-sha256"
  |}
  File.WriteAllText(path, SageFs.Json.serialize SageFs.Json.standard obj)
  path

/// The frames a Jupyter client sends: the delimiter, the HMAC, the header, the parent, the
/// metadata and the content, in that order.
let private requestFrames (key: string) (code: string) : string list =
  let header =
    { MsgId = "msg-1"
      Session = "session-1"
      Username = "tester"
      Date = DateTimeOffset.UtcNow
      MsgType = MessageType.toWire MessageType.ExecuteRequest
      Version = "5.3" }
  let headerJson = WireProtocol.serializeHeader header
  let parentJson = "{}"
  let metadataJson = "{}"
  let contentJson =
    {| code = code
       silent = false
       store_history = true
       allow_stdin = false
       user_expressions = Map.empty<string, string>
       stop_on_error = true |}
    |> SageFs.Json.serialize SageFs.Json.standard
  let hmac = WireProtocol.sign key headerJson parentJson metadataJson contentJson
  [ delimiter; hmac; headerJson; parentJson; metadataJson; contentJson ]

/// Read the reply frames off the socket: the HMAC, the header, the parent, the metadata and the
/// content. The router's identity frame is already gone — a dealer is given what the router
/// addressed to it.
let private readReply (socket: DealerSocket) (budget: TimeSpan) : string list =
  let deadline = DateTime.UtcNow.Add(budget)
  let mutable frames: string list = []
  while frames.Length < 5 && DateTime.UtcNow < deadline do
    let mutable incoming = NetMQMessage()
    if socket.TryReceiveMultipartMessage(budget, &incoming) then
      frames <- [ for i in 0 .. incoming.FrameCount - 1 do incoming.[i].ConvertToString() ]
  frames

/// Read the IOPub broadcast for one execution, until the outcome arrives.
///
/// IOPub is a PUB socket, so a subscriber that connects after the broadcast was
/// sent misses it — the caller has to subscribe BEFORE it sends the request.
/// One execution broadcasts several messages: a `status` (busy), then the
/// outcome (`execute_result` or `error`), then a `status` (idle). The frames
/// are: the topic, the delimiter, the HMAC, the header, the parent, the
/// metadata and the content — the content is the LAST frame and is what
/// carries the payload.
let private readIopub
  (subscriber: SubscriberSocket)
  (budget: TimeSpan)
  : (string * string) list =
  let deadline = DateTime.UtcNow.Add(budget)
  let poll = TestTimeouts.jupyterKernelSubscribe
  let mutable seen: (string * string) list = []
  let mutable outcomeSeen = false
  while not outcomeSeen && DateTime.UtcNow < deadline do
    let mutable incoming = NetMQMessage()
    if subscriber.TryReceiveMultipartMessage(poll, &incoming) then
      let topic = if incoming.FrameCount > 0 then incoming.[0].ConvertToString() else ""
      let content = if incoming.FrameCount > 0 then incoming.[incoming.FrameCount - 1].ConvertToString() else ""
      seen <- seen @ [ (topic, content) ]
      // The outcome is the message that says what happened; the statuses
      // around it say only that the kernel is busy or idle.
      if topic = "execute_result" || topic = "error" then outcomeSeen <- true
  seen

[<Tests>]
let jupyterKernelSocketTests =
  Integration.hostList "the Jupyter kernel over a real socket" [
    testTask "WHY — the kernel answers on a real ZMQ socket, because every other test of it is in-process and none of them can catch a kernel that never answers" {
      let workDir = Path.Combine(Path.GetTempPath(), "sagefs-jupyter-socket-" + Guid.NewGuid().ToString("N").Substring(0, 8))
      Directory.CreateDirectory workDir |> ignore
      let key = "0123456789abcdef0123456789abcdef"
      let connFile = connectionFile workDir key
      // The daemon binds TWO ports — the MCP one and its +1 dashboard neighbour —
      // so the pair has to be reserved together, not one port at a time.
      let port, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()

      // The awaited result is a plain .NET tuple, so the destructuring in the `let!`
      // leaves both names untyped until they are pinned. Annotating the pattern is
      // what gives the compiler their types.
      let! (daemonProc: Process), (httpClient: HttpClient) = Harness.startDaemonWithArgs port workDir [ "--no-resume" ]

      let exe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()
      let kernel = new ProcessStartInfo(exe)
      kernel.ArgumentList.Add("--mcp-port")
      kernel.ArgumentList.Add(string port)
      kernel.ArgumentList.Add("--jupyter")
      kernel.ArgumentList.Add(connFile)
      kernel.WorkingDirectory <- workDir
      kernel.RedirectStandardOutput <- true
      kernel.RedirectStandardError <- true
      let kernelProc = Process.Start(kernel)
      let kernelErr = kernelProc.StandardError.ReadToEndAsync()

      try
        // The kernel binds its sockets when it starts; give it a moment.
        let shellPort, iopubPort =
          let conn = File.ReadAllText(connFile) |> JsonDocument.Parse
          conn.RootElement.GetProperty("shell_port").GetInt32(),
          conn.RootElement.GetProperty("iopub_port").GetInt32()
        let connected = ref false
        let bindDeadline = DateTime.UtcNow.Add(TestTimeouts.jupyterKernelBind)
        while not !connected && DateTime.UtcNow < bindDeadline do
          try
            let probe = new DealerSocket()
            probe.Connect(sprintf "tcp://127.0.0.1:%d" shellPort)
            connected := true
            probe.Close()
          with _ -> do! Task.Delay TestTimeouts.pollQuick
        if not !connected then
          failtest "the kernel's shell socket never accepted a connection"

        // IOPub is a PUB socket: a subscriber that is not already connected
        // when the kernel broadcasts misses the broadcast entirely. So the
        // subscriber goes up FIRST, and the request goes out after.
        let subscriber = new SubscriberSocket()
        subscriber.Connect(sprintf "tcp://127.0.0.1:%d" iopubPort)
        subscriber.Subscribe("")
        // The subscription has to be registered before the broadcast; the
        // wait is for that to have happened, not for the kernel.
        do! Task.Delay TestTimeouts.jupyterKernelSubscribe

        let socket = new DealerSocket()
        socket.Connect(sprintf "tcp://127.0.0.1:%d" shellPort)
        try
          let message = NetMQMessage()
          for frame in requestFrames key "1 + 1" do
            message.Append(frame)
          socket.SendMultipartMessage(message)

          // The shell channel answers with the execute_reply; the VALUE
          // travels on IOPub, as an execute_result. A Jupyter client reads
          // the result from IOPub, so that is where this test looks for it.
          let reply = readReply socket TestTimeouts.jupyterKernelExec
          (reply.Length, 4) |> Expect.isGreaterThan "the kernel answered the request on the shell socket"

          // The daemon has no session for this working directory yet, so the
          // kernel creates one and retries — a cold session takes seconds to
          // warm up, which is why the budget here is generous.
          let iopub = readIopub subscriber TestTimeouts.jupyterKernelExec
          iopub |> List.isEmpty |> Expect.isFalse "the kernel broadcast the execution's outcome on IOPub"

          let results = iopub |> List.filter (fun (topic, _) -> topic = "execute_result")
          results |> List.isEmpty |> Expect.isFalse "the kernel published the result as an execute_result"
          let resultContent = results |> List.last |> snd
          resultContent |> Expect.stringContains "the result carries the value the daemon computed" "val it: int = 2"

          let errors = iopub |> List.filter (fun (topic, _) -> topic = "error")
          errors
          |> List.isEmpty
          |> Expect.isTrue (sprintf "the execution succeeded, with no error on IOPub (errors: %A)" errors)
        finally
          socket.Close()
          subscriber.Close()
      finally
        try if not kernelProc.HasExited then kernelProc.Kill true with _ -> ()
        Harness.killDaemon daemonProc
        httpClient.Dispose()
        try Directory.Delete(workDir, true) with _ -> ()
    }
  ]
