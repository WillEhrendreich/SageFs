/// The isolated FSI host: a tiny program that owns ONE FsiEvaluationSession and speaks
/// FsiProtocol over a loopback socket. It is compiled by the project's own SDK against that SDK's own
/// FSharp.Compiler.Service and FSharp.Core (see FsiHost.fsproj), so the only assemblies in the process
/// next to the user's code are the SDK's and this file's. It references nothing from SageFs.
///
/// Lifecycle: start a loopback listener, print `FSIHOST_PORT=<n>`, accept the ONE parent connection,
/// send Ready, serve requests until Shutdown or until the connection closes (a dead parent therefore
/// takes the host down with it: no orphaned workers).
module SageFs.FsiHost.Program

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell
open SageFs.FsiHost.FsiProtocol

/// A TextWriter that forwards what is written as Output messages (flushed per line and on Flush).
type EventWriter(stream: OutputStream, send: Response -> unit) =
  inherit TextWriter()
  let buffer = StringBuilder()

  let flushBuffer () =
    if buffer.Length > 0 then
      let text = buffer.ToString()
      buffer.Clear() |> ignore
      send (Output(stream, text))

  override _.Encoding = Encoding.UTF8

  override _.Write(c: char) =
    lock buffer (fun () ->
      buffer.Append c |> ignore
      if c = '\n' then flushBuffer ())

  override _.Write(value: string) =
    lock buffer (fun () ->
      buffer.Append value |> ignore
      if not (isNull value) && value.Contains '\n' then flushBuffer ())

  override _.Flush() = lock buffer flushBuffer

let private severityOf (severity: FSharpDiagnosticSeverity) =
  match severity with
  | FSharpDiagnosticSeverity.Hidden -> DiagHidden
  | FSharpDiagnosticSeverity.Info -> DiagInfo
  | FSharpDiagnosticSeverity.Warning -> DiagWarning
  | FSharpDiagnosticSeverity.Error -> DiagError

let private toDiagnostic (d: FSharpDiagnostic) : FsiDiagnostic =
  { Severity = severityOf d.Severity
    ErrorNumber = d.ErrorNumber
    Message = d.Message
    StartLine = d.StartLine
    StartColumn = d.StartColumn
    EndLine = d.EndLine
    EndColumn = d.EndColumn }

/// The eval currently running, so Interrupt can reach it.
type private Running =
  { Cancel: CancellationTokenSource
    Thread: Thread }

let private run (argsFile: string) : int =
  let fsiArgs = File.ReadAllLines argsFile |> Array.filter (fun line -> line.Length > 0)

  let listener = TcpListener(IPAddress.Loopback, 0)
  listener.Start()
  printfn "FSIHOST_PORT=%d" (listener.LocalEndpoint :?> IPEndPoint).Port
  Console.Out.Flush()
  use client = listener.AcceptTcpClient()
  listener.Stop()
  use stream = client.GetStream()
  use reader = new StreamReader(stream, UTF8Encoding false)
  use writer = new StreamWriter(stream, UTF8Encoding false)
  writer.AutoFlush <- true
  let sendLock = obj ()

  let send (response: Response) =
    lock sendLock (fun () ->
      try writer.WriteLine(encodeResponse response) with _ -> ())

  let outWriter = new EventWriter(StdOut, send)
  let errWriter = new EventWriter(StdErr, send)
  // User code writing to the console shows up as Output, just like FSI's own printing.
  Console.SetOut outWriter
  Console.SetError errWriter

  let config = FsiEvaluationSession.GetDefaultConfiguration()
  use session =
    FsiEvaluationSession.Create(config, fsiArgs, new StreamReader(Stream.Null), outWriter, errWriter, collectible = true)

  send (
    Ready(
      System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
      typeof<unit>.Assembly.GetName().Version.ToString()
    )
  )

  let requests = new BlockingCollection<int64 * string>()
  let runningLock = obj ()
  let mutable running: Running option = None

  let evalLoop () =
    let mutable alive = true
    while alive do
      try
        let id, code = requests.Take()
        use cancel = new CancellationTokenSource()
        lock runningLock (fun () -> running <- Some { Cancel = cancel; Thread = Thread.CurrentThread })
        let outcome, diagnostics =
          try
            let result, diagnostics = session.EvalInteractionNonThrowing(code, cancel.Token)
            let outcome =
              match result with
              | Choice1Of2 _ -> EvalSucceeded
              | Choice2Of2 ex when cancel.IsCancellationRequested || (ex :? OperationCanceledException) -> EvalInterrupted
              | Choice2Of2 ex -> EvalFailed ex.Message
            outcome, diagnostics |> Array.map toDiagnostic |> Array.toList
          with
          | :? ThreadInterruptedException -> EvalInterrupted, []
          | ex -> EvalFailed ex.Message, []
        lock runningLock (fun () -> running <- None)
        outWriter.Flush()
        errWriter.Flush()
        send (EvalResult(id, outcome, diagnostics))
      with
      | :? ThreadInterruptedException -> () // an Interrupt that arrived between evals
      | :? InvalidOperationException -> alive <- false // requests completed: shutting down

  let evalThread = Thread(evalLoop, IsBackground = true, Name = "fsihost-eval")
  evalThread.Start()

  let mutable serving = true
  while serving do
    match reader.ReadLine() with
    | null -> serving <- false // the parent went away: exit with it
    | line ->
      match decodeRequest line with
      | Result.Error reason -> send (Output(StdErr, sprintf "[fsihost] rejected a request: %s\n" reason))
      | Result.Ok(Eval(id, code)) -> requests.Add((id, code))
      | Result.Ok Interrupt ->
        lock runningLock (fun () ->
          match running with
          | Some current ->
            current.Cancel.Cancel()
            current.Thread.Interrupt()
          | None -> ())
      | Result.Ok Shutdown -> serving <- false

  requests.CompleteAdding()
  0

[<EntryPoint>]
let main argv =
  match checkSupported (), argv with
  | Result.Error reason, _ ->
    eprintfn "fsihost: protocol check failed: %s" reason
    3
  | Result.Ok(), [| "--args-file"; argsFile |] -> run argsFile
  | Result.Ok(), _ ->
    eprintfn "usage: FsiHost --args-file <path>"
    2
