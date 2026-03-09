namespace SageFs

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

/// Jupyter kernel wire protocol implementation (v5.3).
/// Pure types and functions — no ZMQ dependency.
/// Bridges SageFs FSI sessions to Jupyter frontends.
module JupyterKernel =

  // ── Message Types ──

  /// All Jupyter wire protocol message types.
  [<RequireQualifiedAccess>]
  type MessageType =
    | ExecuteRequest
    | ExecuteReply
    | KernelInfoRequest
    | KernelInfoReply
    | CompleteRequest
    | CompleteReply
    | CheckCompleteRequest
    | CheckCompleteReply
    | Status
    | Stream
    | ExecuteResult
    | Error
    | ShutdownRequest
    | ShutdownReply
    | InterruptRequest
    | InterruptReply

  module MessageType =
    let private wireMap = [
      "execute_request", MessageType.ExecuteRequest
      "execute_reply", MessageType.ExecuteReply
      "kernel_info_request", MessageType.KernelInfoRequest
      "kernel_info_reply", MessageType.KernelInfoReply
      "complete_request", MessageType.CompleteRequest
      "complete_reply", MessageType.CompleteReply
      "is_complete_request", MessageType.CheckCompleteRequest
      "is_complete_reply", MessageType.CheckCompleteReply
      "status", MessageType.Status
      "stream", MessageType.Stream
      "execute_result", MessageType.ExecuteResult
      "error", MessageType.Error
      "shutdown_request", MessageType.ShutdownRequest
      "shutdown_reply", MessageType.ShutdownReply
      "interrupt_request", MessageType.InterruptRequest
      "interrupt_reply", MessageType.InterruptReply
    ]

    let private fromWire = wireMap |> Map.ofList
    let private toWireMap = wireMap |> List.map (fun (k, v) -> v, k) |> Map.ofList

    let parse (s: string) = fromWire |> Map.tryFind s
    let toWire (mt: MessageType) = toWireMap.[mt]

  // ── Wire Protocol Header ──

  type MessageHeader = {
    MsgId: string
    Session: string
    Username: string
    Date: DateTimeOffset
    MsgType: string
    Version: string
  }

  // ── Content Types ──

  type ExecuteRequestContent = {
    Code: string
    Silent: bool
    StoreHistory: bool
    AllowStdin: bool
  }

  type CompleteRequestContent = {
    Code: string
    CursorPos: int
  }

  [<RequireQualifiedAccess>]
  type MessageContent =
    | ExecuteRequest of ExecuteRequestContent
    | KernelInfoRequest
    | CompleteRequest of CompleteRequestContent
    | ShutdownRequest of restart: bool
    | CheckCompleteRequest of code: string
    | Raw of string

  type JupyterMessage = {
    Header: MessageHeader
    ParentHeader: MessageHeader option
    Metadata: Map<string, string>
    Content: MessageContent
  }

  // ── Connection Info ──

  type ConnectionInfo = {
    Transport: string
    Ip: string
    ShellPort: int
    IoPubPort: int
    StdinPort: int
    ControlPort: int
    HbPort: int
    Key: string
    SignatureScheme: string
  }

  module ConnectionInfo =
    let parse (json: string) : Result<ConnectionInfo, string> =
      try
        let doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        Ok {
          Transport = root.GetProperty("transport").GetString()
          Ip = root.GetProperty("ip").GetString()
          ShellPort = root.GetProperty("shell_port").GetInt32()
          IoPubPort = root.GetProperty("iopub_port").GetInt32()
          StdinPort = root.GetProperty("stdin_port").GetInt32()
          ControlPort = root.GetProperty("control_port").GetInt32()
          HbPort = root.GetProperty("hb_port").GetInt32()
          Key = root.GetProperty("key").GetString()
          SignatureScheme = root.GetProperty("signature_scheme").GetString()
        }
      with ex ->
        Error (sprintf "Failed to parse connection info: %s" ex.Message)

    let address (info: ConnectionInfo) (port: int) =
      sprintf "%s://%s:%d" info.Transport info.Ip port

  // ── HMAC Signing ──

  module WireProtocol =
    let sign (key: string) (header: string) (parent: string) (metadata: string) (content: string) : string =
      match String.IsNullOrEmpty key with
      | true -> ""
      | false ->
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key))
        let data =
          [| header; parent; metadata; content |]
          |> Array.map Encoding.UTF8.GetBytes
        for chunk in data do
          hmac.TransformBlock(chunk, 0, chunk.Length, chunk, 0) |> ignore
        hmac.TransformFinalBlock(Array.empty, 0, 0) |> ignore
        hmac.Hash |> Array.map (fun b -> b.ToString("x2")) |> String.concat ""

    let serializeHeader (h: MessageHeader) : string =
      let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
      let obj = {|
        msg_id = h.MsgId
        session = h.Session
        username = h.Username
        date = h.Date.ToString("o")
        msg_type = h.MsgType
        version = h.Version
      |}
      JsonSerializer.Serialize(obj, opts)

    let deserializeHeader (json: string) : Result<MessageHeader, string> =
      try
        let doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        Ok {
          MsgId = root.GetProperty("msg_id").GetString()
          Session = root.GetProperty("session").GetString()
          Username = root.GetProperty("username").GetString()
          Date =
            match root.TryGetProperty("date") with
            | true, el ->
              match DateTimeOffset.TryParse(el.GetString()) with
              | true, dto -> dto
              | false, _ -> DateTimeOffset.UtcNow
            | false, _ -> DateTimeOffset.UtcNow
          MsgType = root.GetProperty("msg_type").GetString()
          Version =
            match root.TryGetProperty("version") with
            | true, el -> el.GetString()
            | false, _ -> "5.3"
        }
      with ex ->
        Error (sprintf "Failed to deserialize header: %s" ex.Message)

    let serializeContent (content: MessageContent) : string =
      match content with
      | MessageContent.ExecuteRequest r ->
        let obj = {|
          code = r.Code
          silent = r.Silent
          store_history = r.StoreHistory
          allow_stdin = r.AllowStdin
          user_expressions = Map.empty<string, string>
          stop_on_error = true
        |}
        JsonSerializer.Serialize(obj)
      | MessageContent.KernelInfoRequest ->
        "{}"
      | MessageContent.CompleteRequest r ->
        let obj = {| code = r.Code; cursor_pos = r.CursorPos |}
        JsonSerializer.Serialize(obj)
      | MessageContent.ShutdownRequest restart ->
        let obj = {| restart = restart |}
        JsonSerializer.Serialize(obj)
      | MessageContent.CheckCompleteRequest code ->
        let obj = {| code = code |}
        JsonSerializer.Serialize(obj)
      | MessageContent.Raw json -> json

  // ── Kernel State Machine ──

  [<RequireQualifiedAccess>]
  type KernelStatus =
    | Idle
    | Busy
    | ShuttingDown

  type KernelState = {
    ExecutionCount: int
    Status: KernelStatus
    RestartOnShutdown: bool
  }

  module KernelState =
    let initial = { ExecutionCount = 0; Status = KernelStatus.Idle; RestartOnShutdown = false }

    let beginExecution (ks: KernelState) =
      { ks with ExecutionCount = ks.ExecutionCount + 1; Status = KernelStatus.Busy }

    let endExecution (ks: KernelState) =
      { ks with Status = KernelStatus.Idle }

    let shutdown (ks: KernelState) (restart: bool) =
      { ks with Status = KernelStatus.ShuttingDown; RestartOnShutdown = restart }

  // ── Language Info ──

  type LanguageInfo = {
    Name: string
    Version: string
    MimeType: string
    FileExtension: string
    PygmentsLexer: string
    CodemirrorMode: string
    NbconvertExporter: string
  }

  type KernelInfoReply = {
    ProtocolVersion: string
    Implementation: string
    ImplementationVersion: string
    LanguageInfo: LanguageInfo
    Banner: string
    HelpLinks: (string * string) list
  }

  // ── Execute Result Types ──

  type ExecuteOutput = {
    Output: string
    MimeType: string
  }

  type ExecuteError = {
    Ename: string
    Evalue: string
    Traceback: string list
  }

  type ExecuteReplyContent =
    | ExecuteReplyOk of {| ExecutionCount: int; Payload: Map<string, string> |}
    | ExecuteReplyError of {| ExecutionCount: int; Ename: string; Evalue: string; Traceback: string list |}

  // ── Complete Result ──

  type CompleteReplyContent = {
    Matches: string list
    CursorStart: int
    CursorEnd: int
    Status: string
  }

  // ── IsComplete Result ──

  [<RequireQualifiedAccess>]
  type CompleteStatus =
    | Complete
    | Incomplete of indent: string
    | Invalid
    | Unknown

  // ── Handler function types ──

  type ExecuteHandler = string -> bool -> Async<Result<ExecuteOutput, ExecuteError>>
  type CompleteHandler = string -> int -> Async<CompleteReplyContent>
  type IsCompleteHandler = string -> Async<CompleteStatus>

  // ── Protocol Handlers ──

  module Protocol =
    let kernelInfoReply () : KernelInfoReply =
      { ProtocolVersion = "5.3"
        Implementation = "sagefs"
        ImplementationVersion = "0.5.0"
        LanguageInfo = {
          Name = "fsharp"
          Version = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
          MimeType = "text/x-fsharp"
          FileExtension = ".fsx"
          PygmentsLexer = "fsharp"
          CodemirrorMode = "mllike"
          NbconvertExporter = "script"
        }
        Banner = "SageFs — F# Live Development Environment\nPowered by F# Interactive"
        HelpLinks = [
          "SageFs Documentation", "https://github.com/WillEhrendreich/SageFs"
        ] }

    let handleExecuteRequest
      (handler: ExecuteHandler)
      (executionCount: int)
      (request: ExecuteRequestContent)
      : Async<ExecuteReplyContent> =
      async {
        let! result = handler request.Code request.Silent
        match result with
        | Ok _output ->
          return ExecuteReplyOk {| ExecutionCount = executionCount; Payload = Map.empty |}
        | Error err ->
          return ExecuteReplyError {|
            ExecutionCount = executionCount
            Ename = err.Ename
            Evalue = err.Evalue
            Traceback = err.Traceback
          |}
      }

    let handleCompleteRequest
      (handler: CompleteHandler)
      (request: CompleteRequestContent)
      : Async<CompleteReplyContent> =
      handler request.Code request.CursorPos

    let handleIsComplete
      (handler: IsCompleteHandler)
      (code: string)
      : Async<CompleteStatus> =
      handler code

  // ── Kernel Spec Generation ──

  type KernelSpec = {
    Argv: string list
    DisplayName: string
    Language: string
    InterruptMode: string
  }

  module KernelSpec =
    let generate (name: string) (executablePath: string) : KernelSpec =
      { Argv = [ executablePath; "--jupyter"; "{connection_file}" ]
        DisplayName = sprintf "F# (SageFs — %s)" name
        Language = "fsharp"
        InterruptMode = "message" }

  // ── IOPub message types ──

  [<RequireQualifiedAccess>]
  type StreamName = Stdout | Stderr

  module StreamName =
    let toWire = function
      | StreamName.Stdout -> "stdout"
      | StreamName.Stderr -> "stderr"

  type IOPubMessage =
    | StatusMessage of KernelStatus
    | StreamOutput of StreamName * text: string
    | ExecuteResultMessage of executionCount: int * data: Map<string, string>
    | ErrorOutput of ename: string * evalue: string * traceback: string list

  // ── Message Routing ──

  /// Pure message router — maps incoming JupyterMessages to outbound responses.
  /// Returns the reply message content + IOPub side-effects as a pure list.
  module Router =

    type RouteResult = {
      Reply: MessageContent
      IOPub: IOPubMessage list
      NewState: KernelState
    }

    let route
      (executeHandler: ExecuteHandler)
      (completeHandler: CompleteHandler)
      (isCompleteHandler: IsCompleteHandler)
      (state: KernelState)
      (msg: JupyterMessage)
      : Async<RouteResult> =
      async {
        match msg.Content with
        | MessageContent.KernelInfoRequest ->
          let _reply = Protocol.kernelInfoReply ()
          return {
            Reply = MessageContent.Raw (WireProtocol.serializeContent MessageContent.KernelInfoRequest)
            IOPub = [ StatusMessage KernelStatus.Busy; StatusMessage KernelStatus.Idle ]
            NewState = state
          }

        | MessageContent.ExecuteRequest req ->
          let busy = KernelState.beginExecution state
          let! result = Protocol.handleExecuteRequest executeHandler busy.ExecutionCount req
          let iopub =
            [ StatusMessage KernelStatus.Busy ] @
            (match result with
             | ExecuteReplyOk r ->
               [ ExecuteResultMessage (r.ExecutionCount, Map.ofList ["text/plain", "ok"]) ]
             | ExecuteReplyError r ->
               [ ErrorOutput (r.Ename, r.Evalue, r.Traceback) ]) @
            [ StatusMessage KernelStatus.Idle ]
          let idle = KernelState.endExecution busy
          return { Reply = MessageContent.Raw "{}"; IOPub = iopub; NewState = idle }

        | MessageContent.CompleteRequest req ->
          let! _result = Protocol.handleCompleteRequest completeHandler req
          return {
            Reply = MessageContent.Raw "{}"
            IOPub = [ StatusMessage KernelStatus.Busy; StatusMessage KernelStatus.Idle ]
            NewState = state
          }

        | MessageContent.CheckCompleteRequest code ->
          let! _result = Protocol.handleIsComplete isCompleteHandler code
          return {
            Reply = MessageContent.Raw "{}"
            IOPub = [ StatusMessage KernelStatus.Busy; StatusMessage KernelStatus.Idle ]
            NewState = state
          }

        | MessageContent.ShutdownRequest restart ->
          let sd = KernelState.shutdown state restart
          return {
            Reply = MessageContent.Raw (sprintf """{"restart": %b}""" restart)
            IOPub = [ StatusMessage KernelStatus.ShuttingDown ]
            NewState = sd
          }

        | MessageContent.Raw _ ->
          return { Reply = MessageContent.Raw "{}"; IOPub = []; NewState = state }
      }

  // ── FSI Bridge ──

  /// Adapts SageFs's WorkerProtocol.SessionProxy to Jupyter handler function types.
  /// Pure transformation layer — no I/O, no ZMQ, fully testable with mock proxies.
  module FsiBridge =

    /// Create an ExecuteHandler from a SessionProxy function.
    /// Maps WorkerMessage.EvalCode → WorkerResponse.EvalResult → Jupyter ExecuteOutput/Error.
    let executeHandler (proxy: WorkerProtocol.SessionProxy) : ExecuteHandler =
      fun code _silent ->
        async {
          let replyId = Guid.NewGuid().ToString("N").[..7]
          let! response = proxy (WorkerProtocol.WorkerMessage.EvalCode(code, replyId))
          match response with
          | WorkerProtocol.WorkerResponse.EvalResult (_rid, result, diagnostics, _meta) ->
            match result with
            | Ok output ->
              return Ok { Output = output; MimeType = "text/plain" }
            | Error err ->
              let traceback =
                diagnostics
                |> List.map (fun d ->
                  sprintf "  (%d,%d)-(%d,%d): %s" d.StartLine d.StartColumn d.EndLine d.EndColumn d.Message)
              return Error {
                Ename = "FSharpError"
                Evalue = SageFsError.describe err
                Traceback = traceback
              }
          | WorkerProtocol.WorkerResponse.WorkerError err ->
            return Error {
              Ename = "WorkerError"
              Evalue = SageFsError.describe err
              Traceback = []
            }
          | other ->
            return Error {
              Ename = "UnexpectedResponse"
              Evalue = sprintf "Expected EvalResult, got %A" (other.GetType().Name)
              Traceback = []
            }
        }

    /// Create a CompleteHandler from a SessionProxy function.
    let completeHandler (proxy: WorkerProtocol.SessionProxy) : CompleteHandler =
      fun code pos ->
        async {
          let replyId = Guid.NewGuid().ToString("N").[..7]
          let! response = proxy (WorkerProtocol.WorkerMessage.GetCompletions(code, pos, replyId))
          match response with
          | WorkerProtocol.WorkerResponse.CompletionResult (_rid, completions) ->
            return {
              Matches = completions
              CursorStart = pos
              CursorEnd = pos
              Status = "ok"
            }
          | _ ->
            return { Matches = []; CursorStart = pos; CursorEnd = pos; Status = "ok" }
        }

    /// Create an IsCompleteHandler using simple heuristic (ends with ;;).
    let isCompleteHandler () : IsCompleteHandler =
      fun code ->
        async {
          let trimmed = code.TrimEnd()
          match trimmed.EndsWith(";;") with
          | true -> return CompleteStatus.Complete
          | false ->
            match trimmed.EndsWith("=") || trimmed.EndsWith("->") || trimmed.EndsWith("do") with
            | true -> return CompleteStatus.Incomplete "  "
            | false -> return CompleteStatus.Unknown
        }

    /// Wire a full Jupyter kernel from a SessionProxy.
    /// Returns the three handler functions ready for Protocol/Router use.
    let fromProxy (proxy: WorkerProtocol.SessionProxy) =
      let exec = executeHandler proxy
      let complete = completeHandler proxy
      let isComplete = isCompleteHandler ()
      exec, complete, isComplete

  // ── Kernel Lifecycle ──

  /// Pure kernel lifecycle — manages state transitions and message processing
  /// without any I/O. The transport layer (ZMQ) is plugged in separately.
  module KernelLifecycle =

    /// Configuration for a Jupyter kernel instance.
    type KernelConfig = {
      ConnectionFile: string
      SessionProxy: WorkerProtocol.SessionProxy
    }

    /// Events emitted during kernel lifecycle — consumed by transport layer.
    type KernelEvent =
      | SendReply of parentHeader: MessageHeader * content: string
      | PublishIOPub of msgType: string * content: string
      | ShutdownRequested of restart: bool

    /// Process a single incoming message, returning events and updated state.
    let processMessage
      (exec: ExecuteHandler)
      (complete: CompleteHandler)
      (isComplete: IsCompleteHandler)
      (state: KernelState)
      (msg: JupyterMessage)
      : Async<KernelEvent list * KernelState> =
      async {
        let! result = Router.route exec complete isComplete state msg
        let events =
          (result.IOPub |> List.map (fun pub ->
            match pub with
            | IOPubMessage.StatusMessage status ->
              let statusStr =
                match status with
                | KernelStatus.Idle -> "idle"
                | KernelStatus.Busy -> "busy"
                | KernelStatus.ShuttingDown -> "shutting_down"
              PublishIOPub ("status", sprintf """{"execution_state": "%s"}""" statusStr)
            | IOPubMessage.StreamOutput (name, text) ->
              PublishIOPub ("stream", sprintf """{"name": "%s", "text": %s}"""
                (StreamName.toWire name)
                (JsonSerializer.Serialize text))
            | IOPubMessage.ExecuteResultMessage (count, data) ->
              let dataJson = data |> Map.toList |> List.map (fun (k, v) -> sprintf "%s: %s" (JsonSerializer.Serialize k) (JsonSerializer.Serialize v)) |> String.concat ", "
              PublishIOPub ("execute_result", sprintf """{"execution_count": %d, "data": {%s}, "metadata": {}}""" count dataJson)
            | IOPubMessage.ErrorOutput (ename, evalue, traceback) ->
              let tbJson = traceback |> List.map JsonSerializer.Serialize |> String.concat ", "
              PublishIOPub ("error", sprintf """{"ename": %s, "evalue": %s, "traceback": [%s]}"""
                (JsonSerializer.Serialize ename) (JsonSerializer.Serialize evalue) tbJson)))
          @ [ SendReply (msg.Header, result.Reply |> function MessageContent.Raw s -> s | _ -> "{}") ]
          @ (match result.NewState.Status with
             | KernelStatus.ShuttingDown -> [ ShutdownRequested result.NewState.RestartOnShutdown ]
             | _ -> [])
        return events, result.NewState
      }

    /// Render a kernelspec JSON file for `jupyter kernelspec install`.
    let renderKernelSpecJson (spec: KernelSpec) : string =
      let argvJson = spec.Argv |> List.map JsonSerializer.Serialize |> String.concat ", "
      let displayJson = JsonSerializer.Serialize spec.DisplayName
      let langJson = JsonSerializer.Serialize spec.Language
      let interruptJson = JsonSerializer.Serialize spec.InterruptMode
      sprintf """{"argv": [%s], "display_name": %s, "language": %s, "interrupt_mode": %s}"""
        argvJson displayJson langJson interruptJson
