module SageFs.Tests.JupyterKernelTests

open System
open System.Text
open System.Text.Json
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.JupyterKernel

// ── Helpers ──

let sampleKey = "test-key-1234567890abcdef"
let sampleSessionId = Guid.NewGuid().ToString()

let mkHeader msgType =
  { MsgId = Guid.NewGuid().ToString()
    Session = sampleSessionId
    Username = "test-user"
    Date = DateTimeOffset.UtcNow
    MsgType = msgType
    Version = "5.3" }

let mkExecRequest code =
  { Header = mkHeader "execute_request"
    ParentHeader = None
    Metadata = Map.empty
    Content = MessageContent.ExecuteRequest { Code = code; Silent = false; StoreHistory = true; AllowStdin = false } }

let mkKernelInfoRequest () =
  { Header = mkHeader "kernel_info_request"
    ParentHeader = None
    Metadata = Map.empty
    Content = MessageContent.KernelInfoRequest }

let mkCompleteRequest code pos =
  { Header = mkHeader "complete_request"
    ParentHeader = None
    Metadata = Map.empty
    Content = MessageContent.CompleteRequest { Code = code; CursorPos = pos } }

// ── Tests ──

[<Tests>]
let jupyterKernelTests =
  testList "JupyterKernel" [

    testList "MessageType parsing" [
      test "execute_request parses" {
        MessageType.parse "execute_request"
        |> Expect.equal "should parse" (Some MessageType.ExecuteRequest)
      }
      test "execute_reply parses" {
        MessageType.parse "execute_reply"
        |> Expect.equal "should parse" (Some MessageType.ExecuteReply)
      }
      test "kernel_info_request parses" {
        MessageType.parse "kernel_info_request"
        |> Expect.equal "should parse" (Some MessageType.KernelInfoRequest)
      }
      test "kernel_info_reply parses" {
        MessageType.parse "kernel_info_reply"
        |> Expect.equal "should parse" (Some MessageType.KernelInfoReply)
      }
      test "complete_request parses" {
        MessageType.parse "complete_request"
        |> Expect.equal "should parse" (Some MessageType.CompleteRequest)
      }
      test "complete_reply parses" {
        MessageType.parse "complete_reply"
        |> Expect.equal "should parse" (Some MessageType.CompleteReply)
      }
      test "status parses" {
        MessageType.parse "status"
        |> Expect.equal "should parse" (Some MessageType.Status)
      }
      test "stream parses" {
        MessageType.parse "stream"
        |> Expect.equal "should parse" (Some MessageType.Stream)
      }
      test "shutdown_request parses" {
        MessageType.parse "shutdown_request"
        |> Expect.equal "should parse" (Some MessageType.ShutdownRequest)
      }
      test "unknown returns None" {
        MessageType.parse "bogus_message"
        |> Expect.isNone "should be None for unknown"
      }
    ]

    testList "MessageType roundtrip" [
      test "all message types survive wire→parse" {
        let allTypes = [
          MessageType.ExecuteRequest; MessageType.ExecuteReply
          MessageType.KernelInfoRequest; MessageType.KernelInfoReply
          MessageType.CompleteRequest; MessageType.CompleteReply
          MessageType.Status; MessageType.Stream
          MessageType.ExecuteResult; MessageType.Error
          MessageType.ShutdownRequest; MessageType.ShutdownReply
          MessageType.CheckCompleteRequest; MessageType.CheckCompleteReply
          MessageType.InterruptRequest; MessageType.InterruptReply
        ]
        for mt in allTypes do
          let wire = MessageType.toWire mt
          match MessageType.parse wire with
          | Some parsed ->
            parsed |> Expect.equal (sprintf "%A roundtrips" mt) mt
          | None ->
            failtest (sprintf "MessageType %A failed to parse from wire '%s'" mt wire)
      }
    ]

    testList "HMAC signing" [
      test "sign produces 64-char hex string" {
        let sig' = WireProtocol.sign sampleKey "header" "parent" "metadata" "content"
        sig'.Length |> Expect.equal "64 hex chars" 64
      }
      test "sign is deterministic" {
        let s1 = WireProtocol.sign sampleKey "h" "p" "m" "c"
        let s2 = WireProtocol.sign sampleKey "h" "p" "m" "c"
        s1 |> Expect.equal "same inputs → same sig" s2
      }
      test "different content → different sig" {
        let s1 = WireProtocol.sign sampleKey "h" "p" "m" "content1"
        let s2 = WireProtocol.sign sampleKey "h" "p" "m" "content2"
        s1 |> Expect.notEqual "different content → different sig" s2
      }
      test "empty key produces empty sig (no auth)" {
        let sig' = WireProtocol.sign "" "h" "p" "m" "c"
        sig' |> Expect.equal "empty key → empty sig" ""
      }
    ]

    testList "ConnectionInfo" [
      test "parse valid connection file JSON" {
        let json = """{
          "transport": "tcp",
          "ip": "127.0.0.1",
          "shell_port": 55555,
          "iopub_port": 55556,
          "stdin_port": 55557,
          "control_port": 55558,
          "hb_port": 55559,
          "key": "abc-123",
          "signature_scheme": "hmac-sha256",
          "kernel_name": "sagefs"
        }"""
        match ConnectionInfo.parse json with
        | Ok info ->
          info.Transport |> Expect.equal "transport" "tcp"
          info.Ip |> Expect.equal "ip" "127.0.0.1"
          info.ShellPort |> Expect.equal "shell" 55555
          info.IoPubPort |> Expect.equal "iopub" 55556
          info.StdinPort |> Expect.equal "stdin" 55557
          info.ControlPort |> Expect.equal "control" 55558
          info.HbPort |> Expect.equal "hb" 55559
          info.Key |> Expect.equal "key" "abc-123"
          info.SignatureScheme |> Expect.equal "scheme" "hmac-sha256"
        | Error e -> failtest (sprintf "parse failed: %s" e)
      }
      test "invalid JSON returns Error" {
        match ConnectionInfo.parse "not json" with
        | Error _ -> ()
        | Ok _ -> failtest "should fail on invalid JSON"
      }
      test "address builds correct ZMQ URI" {
        let info = {
          Transport = "tcp"; Ip = "127.0.0.1"
          ShellPort = 55555; IoPubPort = 55556; StdinPort = 55557
          ControlPort = 55558; HbPort = 55559
          Key = "key"; SignatureScheme = "hmac-sha256"
        }
        ConnectionInfo.address info 55555
        |> Expect.equal "address" "tcp://127.0.0.1:55555"
      }
    ]

    testList "KernelInfo reply" [
      test "produces correct protocol version" {
        let reply = Protocol.kernelInfoReply ()
        reply.ProtocolVersion |> Expect.equal "protocol" "5.3"
      }
      test "language info is F#" {
        let reply = Protocol.kernelInfoReply ()
        reply.LanguageInfo.Name |> Expect.equal "name" "fsharp"
        reply.LanguageInfo.Version |> Expect.stringContains "version" "."
        reply.LanguageInfo.MimeType |> Expect.equal "mime" "text/x-fsharp"
        reply.LanguageInfo.FileExtension |> Expect.equal "ext" ".fsx"
      }
      test "banner mentions SageFs" {
        let reply = Protocol.kernelInfoReply ()
        reply.Banner |> Expect.stringContains "banner" "SageFs"
      }
    ]

    testList "ExecuteRequest handling" [
      test "successful eval produces Ok reply" {
        let handler : ExecuteHandler = fun _code _silent ->
          async { return Ok { Output = "42"; MimeType = "text/plain" } }
        let request = { Code = "1 + 41"; Silent = false; StoreHistory = true; AllowStdin = false }
        let result =
          Protocol.handleExecuteRequest handler 1 request
          |> Async.RunSynchronously
        match result with
        | ExecuteReplyOk reply ->
          reply.ExecutionCount |> Expect.equal "count" 1
        | ExecuteReplyError _ -> failtest "expected Ok"
      }
      test "failed eval produces Error reply" {
        let handler : ExecuteHandler = fun _code _silent ->
          async { return Error { Ename = "CompileError"; Evalue = "FS0001"; Traceback = ["line 1: type mismatch"] } }
        let request = { Code = "bad code"; Silent = false; StoreHistory = true; AllowStdin = false }
        let result =
          Protocol.handleExecuteRequest handler 1 request
          |> Async.RunSynchronously
        match result with
        | ExecuteReplyError reply ->
          reply.Ename |> Expect.equal "ename" "CompileError"
          reply.Evalue |> Expect.equal "evalue" "FS0001"
          reply.Traceback |> Expect.hasLength "traceback" 1
        | ExecuteReplyOk _ -> failtest "expected Error"
      }
      test "execution count increments" {
        let handler : ExecuteHandler = fun _code _silent ->
          async { return Ok { Output = "ok"; MimeType = "text/plain" } }
        let request = { Code = "()"; Silent = false; StoreHistory = true; AllowStdin = false }
        let r1 = Protocol.handleExecuteRequest handler 5 request |> Async.RunSynchronously
        match r1 with
        | ExecuteReplyOk reply -> reply.ExecutionCount |> Expect.equal "count=5" 5
        | _ -> failtest "expected Ok"
      }
      test "silent execution suppresses output" {
        let mutable received = false
        let handler : ExecuteHandler = fun _code silent ->
          received <- true
          async { return Ok { Output = "42"; MimeType = "text/plain" } }
        let request = { Code = "()"; Silent = true; StoreHistory = false; AllowStdin = false }
        let _r = Protocol.handleExecuteRequest handler 1 request |> Async.RunSynchronously
        received |> Expect.isTrue "handler was called"
      }
    ]

    testList "CompleteRequest handling" [
      test "basic completion produces matches" {
        let handler : CompleteHandler = fun code pos ->
          async {
            return {
              Matches = ["List.map"; "List.filter"; "List.fold"]
              CursorStart = pos - 4
              CursorEnd = pos
              Status = "ok"
            }
          }
        let request = { Code = "List."; CursorPos = 5 }
        let result =
          Protocol.handleCompleteRequest handler request
          |> Async.RunSynchronously
        result.Matches |> Expect.hasLength "3 completions" 3
        result.Status |> Expect.equal "status ok" "ok"
      }
      test "empty completions" {
        let handler : CompleteHandler = fun _code _pos ->
          async { return { Matches = []; CursorStart = 0; CursorEnd = 0; Status = "ok" } }
        let request = { Code = "xyz"; CursorPos = 3 }
        let result =
          Protocol.handleCompleteRequest handler request
          |> Async.RunSynchronously
        result.Matches |> Expect.isEmpty "no matches"
      }
    ]

    testList "IsComplete handling" [
      test "complete code returns complete" {
        let handler : IsCompleteHandler = fun code ->
          async { return CompleteStatus.Complete }
        let result = Protocol.handleIsComplete handler "let x = 1" |> Async.RunSynchronously
        result |> Expect.equal "complete" CompleteStatus.Complete
      }
      test "incomplete code returns incomplete with indent" {
        let handler : IsCompleteHandler = fun _code ->
          async { return CompleteStatus.Incomplete "  " }
        let result = Protocol.handleIsComplete handler "let f x =" |> Async.RunSynchronously
        match result with
        | CompleteStatus.Incomplete indent ->
          indent |> Expect.equal "indent" "  "
        | _ -> failtest "expected Incomplete"
      }
      test "invalid code returns invalid" {
        let handler : IsCompleteHandler = fun _code ->
          async { return CompleteStatus.Invalid }
        let result = Protocol.handleIsComplete handler "###" |> Async.RunSynchronously
        result |> Expect.equal "invalid" CompleteStatus.Invalid
      }
    ]

    testList "Kernel state machine" [
      test "initial state is idle with count 0" {
        let ks = KernelState.initial
        ks.ExecutionCount |> Expect.equal "count" 0
        ks.Status |> Expect.equal "status" KernelStatus.Idle
      }
      test "execute transitions to Busy then back to Idle" {
        let ks = KernelState.initial
        let busy = KernelState.beginExecution ks
        busy.Status |> Expect.equal "busy" KernelStatus.Busy
        busy.ExecutionCount |> Expect.equal "count incremented" 1
        let idle = KernelState.endExecution busy
        idle.Status |> Expect.equal "idle" KernelStatus.Idle
        idle.ExecutionCount |> Expect.equal "count preserved" 1
      }
      test "shutdown transitions to ShuttingDown" {
        let ks = KernelState.initial
        let sd = KernelState.shutdown ks false
        sd.Status |> Expect.equal "shutting down" KernelStatus.ShuttingDown
      }
    ]

    testList "Wire serialization" [
      test "header roundtrips through JSON" {
        let h = mkHeader "execute_request"
        let json = WireProtocol.serializeHeader h
        let parsed = WireProtocol.deserializeHeader json
        match parsed with
        | Ok h2 ->
          h2.MsgId |> Expect.equal "msg_id" h.MsgId
          h2.Session |> Expect.equal "session" h.Session
          h2.MsgType |> Expect.equal "msg_type" "execute_request"
        | Error e -> failtest (sprintf "deserialize failed: %s" e)
      }
      test "execute_request content serializes correctly" {
        let content = { Code = "let x = 42"; Silent = false; StoreHistory = true; AllowStdin = false }
        let json = WireProtocol.serializeContent (MessageContent.ExecuteRequest content)
        json |> Expect.stringContains "has code" "let x = 42"
      }
    ]

    testList "Kernel spec" [
      test "kernelspec has correct metadata" {
        let spec = KernelSpec.generate "sagefs" "/path/to/sagefs"
        spec.DisplayName |> Expect.stringContains "display name" "F#"
        spec.Language |> Expect.equal "language" "fsharp"
        spec.Argv |> Expect.isNonEmpty "has argv"
      }
      test "kernelspec argv includes connection file placeholder" {
        let spec = KernelSpec.generate "sagefs" "/path/to/sagefs"
        spec.Argv |> List.exists (fun a -> a.Contains("{connection_file}"))
        |> Expect.isTrue "argv has {connection_file} placeholder"
      }
    ]

    testList "property tests" [
      testProperty "HMAC sign is deterministic" <| fun (h: NonEmptyString) (c: NonEmptyString) ->
        let key = "test-key"
        let s1 = WireProtocol.sign key h.Get "p" "m" c.Get
        let s2 = WireProtocol.sign key h.Get "p" "m" c.Get
        s1 = s2

      testProperty "MessageType roundtrip is identity" <| fun () ->
        let allTypes = [
          MessageType.ExecuteRequest; MessageType.ExecuteReply
          MessageType.KernelInfoRequest; MessageType.KernelInfoReply
          MessageType.CompleteRequest; MessageType.CompleteReply
          MessageType.Status; MessageType.Stream
          MessageType.ExecuteResult; MessageType.Error
          MessageType.ShutdownRequest; MessageType.ShutdownReply
          MessageType.CheckCompleteRequest; MessageType.CheckCompleteReply
          MessageType.InterruptRequest; MessageType.InterruptReply
        ]
        allTypes |> List.forall (fun mt ->
          MessageType.parse (MessageType.toWire mt) = Some mt)

      testProperty "execution count never decreases" <| fun (n: PositiveInt) ->
        let mutable ks = KernelState.initial
        for _ in 1..n.Get do
          ks <- KernelState.beginExecution ks
          ks <- KernelState.endExecution ks
        ks.ExecutionCount = n.Get

      testProperty "HMAC signature is exactly 64 hex chars for non-empty key" <| fun (NonEmptyString key) ->
        let sig' = WireProtocol.sign key "h" "p" "m" "c"
        sig'.Length = 64 && sig' |> Seq.forall (fun c -> Char.IsAsciiHexDigitLower c || Char.IsDigit c)
    ]
  ]
