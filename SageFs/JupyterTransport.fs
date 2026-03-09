namespace SageFs

open System
open System.Threading
open System.Text
open NetMQ
open NetMQ.Sockets
open SageFs.JupyterKernel

/// ZMQ transport for the Jupyter kernel protocol.
/// Wires 5 ZMQ sockets to KernelLifecycle.processMessage.
module JupyterTransport =

  let [<Literal>] private Delimiter = "<IDS|MSG>"

  /// Parse a multi-frame ZMQ message into a JupyterMessage.
  /// Frame layout: [identity..., delimiter, HMAC, header, parent, metadata, content]
  let parseFrames (key: string) (frames: NetMQMessage) : Result<byte[] list * JupyterMessage, string> =
    let mutable delimIdx = -1
    for i in 0..frames.FrameCount - 1 do
      match frames.[i].ConvertToString() = Delimiter with
      | true when delimIdx = -1 -> delimIdx <- i
      | _ -> ()
    match delimIdx with
    | -1 -> Error "No delimiter frame found"
    | di ->
      let identities = [ for i in 0..di-1 -> frames.[i].ToByteArray() ]
      let hmacFrame = frames.[di + 1].ConvertToString()
      let headerJson = frames.[di + 2].ConvertToString()
      let parentJson = frames.[di + 3].ConvertToString()
      let _metadataJson = frames.[di + 4].ConvertToString()
      let contentJson = frames.[di + 5].ConvertToString()

      // Verify HMAC
      let computed = WireProtocol.sign key headerJson parentJson "{}" contentJson
      match String.IsNullOrEmpty key || hmacFrame = computed with
      | false -> Error "HMAC verification failed"
      | true ->
        match WireProtocol.deserializeHeader headerJson with
        | Error e -> Error e
        | Ok header ->
          let parentHeader =
            match parentJson with
            | "{}" | "" | "null" -> None
            | _ ->
              match WireProtocol.deserializeHeader parentJson with
              | Ok h -> Some h
              | Error _ -> None
          let content =
            match MessageType.parse header.MsgType with
            | Some MessageType.ExecuteRequest ->
              let doc = System.Text.Json.JsonDocument.Parse(contentJson)
              let root = doc.RootElement
              MessageContent.ExecuteRequest {
                Code = root.GetProperty("code").GetString()
                Silent = match root.TryGetProperty("silent") with true, v -> v.GetBoolean() | _ -> false
                StoreHistory = match root.TryGetProperty("store_history") with true, v -> v.GetBoolean() | _ -> true
                AllowStdin = match root.TryGetProperty("allow_stdin") with true, v -> v.GetBoolean() | _ -> false
              }
            | Some MessageType.KernelInfoRequest -> MessageContent.KernelInfoRequest
            | Some MessageType.CompleteRequest ->
              let doc = System.Text.Json.JsonDocument.Parse(contentJson)
              let root = doc.RootElement
              MessageContent.CompleteRequest {
                Code = root.GetProperty("code").GetString()
                CursorPos = root.GetProperty("cursor_pos").GetInt32()
              }
            | Some MessageType.CheckCompleteRequest ->
              let doc = System.Text.Json.JsonDocument.Parse(contentJson)
              let root = doc.RootElement
              MessageContent.CheckCompleteRequest (root.GetProperty("code").GetString())
            | Some MessageType.ShutdownRequest ->
              let doc = System.Text.Json.JsonDocument.Parse(contentJson)
              let root = doc.RootElement
              MessageContent.ShutdownRequest (
                match root.TryGetProperty("restart") with true, v -> v.GetBoolean() | _ -> false)
            | _ -> MessageContent.Raw contentJson
          Ok (identities, {
            Header = header
            ParentHeader = parentHeader
            Metadata = Map.empty
            Content = content
          })

  /// Build outgoing frames from a reply.
  let buildReplyFrames
    (key: string)
    (identities: byte[] list)
    (parentHeader: MessageHeader)
    (replyType: string)
    (content: string)
    : NetMQMessage =
    let msg = NetMQMessage()
    for id in identities do
      msg.Append(id)
    msg.Append(Delimiter)
    let headerObj = {
      MsgId = Guid.NewGuid().ToString("N")
      Session = parentHeader.Session
      Username = parentHeader.Username
      Date = DateTimeOffset.UtcNow
      MsgType = replyType
      Version = "5.3"
    }
    let headerJson = WireProtocol.serializeHeader headerObj
    let parentJson = WireProtocol.serializeHeader parentHeader
    let metadataJson = "{}"
    let hmac = WireProtocol.sign key headerJson parentJson metadataJson content
    msg.Append(hmac)
    msg.Append(headerJson)
    msg.Append(parentJson)
    msg.Append(metadataJson)
    msg.Append(content)
    msg

  /// Build IOPub broadcast frames (no identity prefix).
  let buildIOPubFrames
    (key: string)
    (parentHeader: MessageHeader)
    (msgType: string)
    (content: string)
    : NetMQMessage =
    let msg = NetMQMessage()
    // IOPub topic = msg_type for PUB/SUB filtering
    msg.Append(Encoding.UTF8.GetBytes(msgType))
    msg.Append(Delimiter)
    let headerObj = {
      MsgId = Guid.NewGuid().ToString("N")
      Session = parentHeader.Session
      Username = parentHeader.Username
      Date = DateTimeOffset.UtcNow
      MsgType = msgType
      Version = "5.3"
    }
    let headerJson = WireProtocol.serializeHeader headerObj
    let parentJson = WireProtocol.serializeHeader parentHeader
    let metadataJson = "{}"
    let hmac = WireProtocol.sign key headerJson parentJson metadataJson content
    msg.Append(hmac)
    msg.Append(headerJson)
    msg.Append(parentJson)
    msg.Append(metadataJson)
    msg.Append(content)
    msg

  /// Process messages on a single ROUTER socket (Shell or Control).
  let private processSocket
    (socket: RouterSocket)
    (iopub: PublisherSocket)
    (key: string)
    (exec: ExecuteHandler)
    (complete: CompleteHandler)
    (isComplete: IsCompleteHandler)
    (state: KernelState ref)
    (ct: CancellationToken)
    =
    while not ct.IsCancellationRequested do
      let mutable incoming = NetMQMessage()
      match socket.TryReceiveMultipartMessage(TimeSpan.FromMilliseconds(100.0), &incoming) with
      | false -> ()
      | true ->
        match parseFrames key incoming with
        | Error _ -> ()
        | Ok (identities, jupyterMsg) ->
          let events, newState =
            KernelLifecycle.processMessage exec complete isComplete state.Value jupyterMsg
            |> Async.RunSynchronously
          state.Value <- newState
          for event in events do
            match event with
            | KernelLifecycle.SendReply (parentHeader, content) ->
              let replyType = parentHeader.MsgType.Replace("_request", "_reply")
              let frames = buildReplyFrames key identities parentHeader replyType content
              socket.SendMultipartMessage(frames)
            | KernelLifecycle.PublishIOPub (msgType, content) ->
              let frames = buildIOPubFrames key jupyterMsg.Header msgType content
              iopub.SendMultipartMessage(frames)
            | KernelLifecycle.ShutdownRequested _ -> ()

  /// Run the Jupyter kernel with 5 ZMQ sockets.
  let run (connInfo: ConnectionInfo) (exec: ExecuteHandler) (complete: CompleteHandler) (isComplete: IsCompleteHandler) (ct: CancellationToken) =
    let addr port = ConnectionInfo.address connInfo port
    use shell = new RouterSocket()
    use control = new RouterSocket()
    use iopub = new PublisherSocket()
    use stdin = new RouterSocket()
    use heartbeat = new ResponseSocket()

    shell.Bind(addr connInfo.ShellPort)
    control.Bind(addr connInfo.ControlPort)
    iopub.Bind(addr connInfo.IoPubPort)
    stdin.Bind(addr connInfo.StdinPort)
    heartbeat.Bind(addr connInfo.HbPort)

    let state = ref KernelState.initial

    // Heartbeat: echo back whatever arrives
    let hbThread = Thread(fun () ->
      while not ct.IsCancellationRequested do
        let ok, bytes = heartbeat.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(100.0))
        match ok with
        | false -> ()
        | true -> heartbeat.SendFrame(bytes))
    hbThread.IsBackground <- true
    hbThread.Start()

    // Shell message loop
    let shellThread = Thread(fun () ->
      processSocket shell iopub connInfo.Key exec complete isComplete state ct)
    shellThread.IsBackground <- true
    shellThread.Start()

    // Control message loop (same logic, separate socket)
    processSocket control iopub connInfo.Key exec complete isComplete state ct
