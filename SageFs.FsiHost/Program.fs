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
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Interactive.Shell
open SageFs.Features
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

/// One unit of work for the session thread.
type private Work =
  | RunEval of id: int64 * code: string
  | RunReadFlag of id: int64 * name: string
  | RunReadValue of id: int64 * name: string
  | RunReadLiveValues of id: int64 * generation: int64
  | RunCheck of id: int64 * text: string
  | RunCheckWithSymbols of id: int64 * filePath: string * text: string
  | RunComplete of id: int64 * text: string * caret: int
  | RunDescribe of id: int64 * completionsId: int64 * index: int
  | RunEvalConfig of id: int64 * content: string

let private typeNameOf (value: FsiValue) =
  match value.ReflectionType with
  | null -> ""
  | t -> t.Name

let private readFlag (session: FsiEvaluationSession) (name: string) : FlagReading =
  match session.TryFindBoundValue name with
  | None -> FlagWasUnbound
  | Some bound ->
    match bound.Value.ReflectionValue with
    | :? bool as value -> FlagWasBool value
    | _ -> FlagWasNotBool(typeNameOf bound.Value)

let private maxValueText = 4096

let private readValue (session: FsiEvaluationSession) (name: string) : ValueReading =
  match session.GetBoundValues() |> List.tryFind (fun bound -> bound.Name = name) with
  | None -> ValueUnbound
  | Some bound ->
    let text =
      try
        match bound.Value.ReflectionValue with
        | null -> "null"
        | value -> value.ToString()
      with ex -> sprintf "<%s while reading the value>" (ex.GetType().Name)
    let text = if text.Length > maxValueText then text.Substring(0, maxValueText) + "…" else text
    ValueText(typeNameOf bound.Value, text)

/// The session's bound values walked into the bounded tree the dashboard renders. A failed walk yields an empty
/// snapshot rather than an error: the watch window degrades, the session does not.
let private liveValues (session: FsiEvaluationSession) (generation: int64) : LiveValueTree.LiveValueSnapshot =
  try
    let boundValues =
      session.GetBoundValues()
      |> List.map (fun bound ->
        let value =
          try bound.Value.ReflectionValue
          with _ -> null
        let typeSignature =
          try typeNameOf bound.Value
          with _ -> ""
        (bound.Name, typeSignature, value))
    LiveValueTree.buildSnapshot "" generation boundValues
  with _ -> LiveValueTree.buildSnapshot "" generation []

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

  // Everything that touches the session runs on the one eval thread, in order: FSI sessions are not thread-safe.
  let requests = new BlockingCollection<Work>()
  // The candidates of the latest Complete, for Describe. Only the session thread reads or writes it.
  let lastCompletions = ref (0L, ([||]: DeclarationListItem[]))
  let runningLock = obj ()
  let mutable running: Running option = None

  let evalLoop () =
    let mutable alive = true
    while alive do
      try
        match requests.Take() with
        | RunReadFlag(id, name) -> send (FlagResult(id, readFlag session name))
        | RunReadValue(id, name) -> send (ValueResult(id, readValue session name))
        | RunReadLiveValues(id, generation) -> send (LiveValuesResult(id, liveValues session generation))
        | RunCheck(id, text) ->
          let diagnostics = try FcsQueries.check session text with _ -> []
          send (CheckResult(id, diagnostics))
        | RunCheckWithSymbols(id, filePath, text) ->
          let diagnostics, symbols = try FcsQueries.checkWithSymbols session filePath text with _ -> [], []
          send (SymbolsResult(id, diagnostics, symbols))
        | RunComplete(id, text, caret) ->
          // Keep the FCS items so a later Describe can produce a description for one of them.
          let items = try FcsQueries.candidates session text caret with _ -> [||]
          lastCompletions.Value <- (id, items)
          send (CompletionsResult(id, items |> Array.map FcsQueries.toCompletion |> Array.toList))
        | RunDescribe(id, completionsId, index) ->
          let latestId, items = lastCompletions.Value
          let text =
            match latestId = completionsId && index >= 0 && index < items.Length with
            | true -> (try FcsQueries.describe items.[index] with _ -> "")
            | false -> "" // a newer Complete replaced the list this index referred to
          send (DescriptionResult(id, text))
        | RunEvalConfig(id, content) -> send (ConfigResult(id, FcsQueries.evalConfig session content))
        | RunEval(id, code) ->
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
              outcome, diagnostics |> Array.map FcsQueries.toWire |> Array.toList
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
      | Result.Error reason -> send (Output(StdErr, sprintf "[fsihost] rejected a request: %s\n" (describeError reason)))
      | Result.Ok(Eval(id, code)) -> requests.Add(RunEval(id, code))
      | Result.Ok(ReadFlag(id, name)) -> requests.Add(RunReadFlag(id, name))
      | Result.Ok(ReadValue(id, name)) -> requests.Add(RunReadValue(id, name))
      | Result.Ok(ReadLiveValues(id, generation)) -> requests.Add(RunReadLiveValues(id, generation))
      | Result.Ok(Check(id, text)) -> requests.Add(RunCheck(id, text))
      | Result.Ok(CheckWithSymbols(id, filePath, text)) -> requests.Add(RunCheckWithSymbols(id, filePath, text))
      | Result.Ok(Complete(id, text, caret)) -> requests.Add(RunComplete(id, text, caret))
      | Result.Ok(Describe(id, completionsId, index)) -> requests.Add(RunDescribe(id, completionsId, index))
      | Result.Ok(EvalConfig(id, content)) -> requests.Add(RunEvalConfig(id, content))
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
    eprintfn "fsihost: protocol check failed: %s" (describeError reason)
    3
  | Result.Ok(), [| "--args-file"; argsFile |] -> run argsFile
  | Result.Ok(), _ ->
    eprintfn "usage: FsiHost --args-file <path>"
    2
