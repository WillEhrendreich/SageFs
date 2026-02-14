namespace SageFs

open System
open System.IO
open System.IO.Pipes
open System.Text
open System.Threading
open SageFs.WorkerProtocol

/// Length-prefixed JSON framing over named pipes.
/// [4 bytes big-endian length][UTF-8 JSON payload]
module NamedPipeTransport =

  let pipeName (sessionId: string) =
    sprintf "sagefs-session-%s" sessionId

  let writeMessage (stream: Stream) (json: string) = async {
    let bytes = Encoding.UTF8.GetBytes json
    let lenBytes =
      BitConverter.GetBytes(
        System.Net.IPAddress.HostToNetworkOrder(bytes.Length))
    do! stream.WriteAsync(lenBytes, 0, 4) |> Async.AwaitTask
    do! stream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
    do! stream.FlushAsync() |> Async.AwaitTask
  }

  let readMessage (stream: Stream) = async {
    let lenBuf = Array.zeroCreate<byte> 4
    let! bytesRead = stream.ReadAsync(lenBuf, 0, 4) |> Async.AwaitTask
    if bytesRead < 4 then
      return None
    else
      let len =
        System.Net.IPAddress.NetworkToHostOrder(
          BitConverter.ToInt32(lenBuf, 0))
      let buf = Array.zeroCreate<byte> len
      let mutable offset = 0
      while offset < len do
        let! n =
          stream.ReadAsync(buf, offset, len - offset) |> Async.AwaitTask
        if n = 0 then failwith "Pipe closed during read"
        offset <- offset + n
      return Some(Encoding.UTF8.GetString(buf))
  }

  let private sendRequest (stream: Stream) (msg: WorkerMessage) = async {
    let json = Serialization.serialize msg
    do! writeMessage stream json
    let! reply = readMessage stream
    match reply with
    | Some json -> return Serialization.deserialize<WorkerResponse> json
    | None -> return WorkerResponse.WorkerError SageFsError.PipeClosed
  }

  /// Client side: connect to a worker's named pipe, return a SessionProxy.
  let connect (name: string) (ct: CancellationToken) = async {
    let client =
      new NamedPipeClientStream(
        ".", name, PipeDirection.InOut, PipeOptions.Asynchronous)
    do! client.ConnectAsync(ct) |> Async.AwaitTask
    return (fun msg -> sendRequest client msg), (client :> IDisposable)
  }

  /// Server side: listen on a named pipe, dispatch to handler.
  /// Handles one connection, then returns (worker is 1:1 with pipe).
  let listen
    (name: string)
    (handler: WorkerMessage -> Async<WorkerResponse>)
    (ct: CancellationToken)
    = async {
    use server =
      new NamedPipeServerStream(
        name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous)
    do! server.WaitForConnectionAsync(ct) |> Async.AwaitTask
    let mutable running = true
    while running && not ct.IsCancellationRequested do
      let! msgJson = readMessage server
      match msgJson with
      | None -> running <- false
      | Some json ->
        let msg = Serialization.deserialize<WorkerMessage> json
        let! response = handler msg
        let respJson = Serialization.serialize response
        do! writeMessage server respJson
        match msg with
        | WorkerMessage.Shutdown -> running <- false
        | _ -> ()
  }
