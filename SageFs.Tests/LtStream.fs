/// The `--integration-lt` runner's daemon, driven the way an editor drives it, with the daemon's own
/// `/events` stream as the clock. Shared by the latency measurement and the live-testing journeys, so
/// every one of them reads the same frames the same way.
///
/// The runner sets SAGEFS_LT_MCP_PORT (the daemon's HTTP port) and SAGEFS_LT_FIXTURE_DIR (the
/// FromCSharp sample, whose Hello.fs defines `let add a b = a + b` and 11 Expecto tests). Both are
/// read lazily so a missing one fails only the journey that needs it.
module SageFs.Tests.LtStream

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// One frame of the daemon's SSE stream, stamped when this process read it.
type SseFrame =
  { Event: string
    Data: string
    ReceivedAt: int64 }

/// The daemon's `/events` stream, read on its own task. Frames queue in arrival order.
type SseFeed =
  { Frames: ChannelReader<SseFrame>
    Stop: unit -> Task }

/// The verdict a stream frame carries for one test.
[<RequireQualifiedAccess>]
type Verdict =
  | Passed
  | Failed
  | Other of case: string
  | NotInFrame

module LtEnv =
  let private required (name: string) =
    lazy
      (match Environment.GetEnvironmentVariable name with
       | null | "" -> failwithf "%s not set (run under --integration-lt)" name
       | value -> value)

  let mcpPort = required "SAGEFS_LT_MCP_PORT"
  let fixtureDir = required "SAGEFS_LT_FIXTURE_DIR"

let baseUrl () = Uri(sprintf "http://localhost:%s" LtEnv.mcpPort.Value)
let helloPath () = Path.Combine(LtEnv.fixtureDir.Value, "Hello.fs")

/// The line the measured edits flip. The suite has exactly one test that fails when it changes:
/// "add infers int" (3 + 4 = 7).
let greenAdd = "let add a b = a + b"
let redAdd = "let add a b = a + b + 1"
let affectedTest = "add infers int"

/// The wall-clock gap between two stopwatch timestamps.
let elapsed (startedAt: int64) (endedAt: int64) : TimeSpan =
  TimeSpan.FromSeconds(float (endedAt - startedAt) / float Stopwatch.Frequency)

let openFeed () : SseFeed =
  let channel = Channel.CreateUnbounded<SseFrame>()
  let cts = new CancellationTokenSource()
  let http = new HttpClient(BaseAddress = baseUrl (), Timeout = Timeout.InfiniteTimeSpan)
  let publish (event: string) (data: string) =
    channel.Writer.TryWrite { Event = event; Data = data; ReceivedAt = Stopwatch.GetTimestamp() } |> ignore
  let pump =
    task {
      try
        try
          use! response = http.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead, cts.Token)
          use! stream = response.Content.ReadAsStreamAsync(cts.Token)
          use reader = new StreamReader(stream, Encoding.UTF8)
          let mutable event = ""
          let data = StringBuilder()
          let mutable reading = true
          while reading do
            let! line = reader.ReadLineAsync(cts.Token)
            match line with
            | null -> reading <- false
            | "" ->
              match event with
              | "" -> ()
              | name -> publish name (data.ToString())
              event <- ""
              data.Clear() |> ignore
            | l when l.StartsWith("event:", StringComparison.Ordinal) -> event <- l.Substring("event:".Length).Trim()
            | l when l.StartsWith("data:", StringComparison.Ordinal) -> data.Append(l.Substring("data:".Length).TrimStart()) |> ignore
            | _ -> ()
        with
        | :? OperationCanceledException -> ()
        | :? IOException -> ()
      finally
        channel.Writer.TryComplete() |> ignore
        http.Dispose()
    }
  { Frames = channel.Reader
    Stop =
      fun () ->
        cts.Cancel()
        pump }

/// How much of a frame a timeout message quotes: enough to read the verdicts, short of a screenful per frame.
let private quotedFrameLength = 1500

/// One line per test of a `test_results_batch`: its name, its verdict and what it ran against. What a timeout
/// quotes, because the raw frame is mostly fields nobody reads.
let private batchSummary (data: string) : string =
  try
    use doc = JsonDocument.Parse data
    match doc.RootElement.TryGetProperty "Entries" with
    | false, _ -> data
    | true, entries ->
      entries.EnumerateArray()
      |> Seq.map (fun e ->
        let name = e.GetProperty("DisplayName").GetString()
        let status = e.GetProperty("Status").GetProperty("Case").GetString()
        let provenance =
          match e.TryGetProperty "Provenance" with
          | true, p -> p.GetProperty("Case").GetString()
          | false, _ -> "?"
        sprintf "%s=%s/%s" name status provenance)
      |> String.concat "; "
  with :? JsonException -> data

/// Read frames until one satisfies `predicate`, or `budget` passes. The error names what was seen.
let awaitFrame (feed: SseFeed) (what: string) (predicate: SseFrame -> bool) (budget: TimeSpan) : Task<Result<SseFrame, string>> =
  task {
    use cts = new CancellationTokenSource(budget)
    let seen = ResizeArray<string>()
    // The newest frame of the two kinds a verdict or a decision rides on, so a timeout says what the stream
    // last said and not only how many frames went by.
    let latest = System.Collections.Generic.Dictionary<string, string>()
    let mutable found : SseFrame voption = ValueNone
    try
      while found.IsNone do
        let! frame = feed.Frames.ReadAsync(cts.Token)
        seen.Add frame.Event
        match frame.Event with
        | "test_results_batch" -> latest[frame.Event] <- batchSummary frame.Data
        | "test_summary" -> latest[frame.Event] <- frame.Data
        | _ -> ()
        match predicate frame with
        | true -> found <- ValueSome frame
        | false -> ()
    with
    | :? OperationCanceledException
    | :? ChannelClosedException -> ()
    match found with
    | ValueSome frame -> return Ok frame
    | ValueNone ->
      return
        Result.Error(
          sprintf "%s: no matching frame within %s. Frames seen meanwhile: %s. Newest: %s"
            what (budget.ToString()) (String.Join(", ", seen |> Seq.countBy id |> Seq.map (fun (e, n) -> sprintf "%s x%d" e n)))
            (String.Join(" | ", latest |> Seq.map (fun kv -> sprintf "%s %s" kv.Key (match kv.Value.Length > quotedFrameLength with | true -> kv.Value.Substring(0, quotedFrameLength) | false -> kv.Value)))))
  }

/// `awaitFrame` that fails the test on a timeout.
let expectFrame (feed: SseFeed) (what: string) (predicate: SseFrame -> bool) (budget: TimeSpan) : Task<SseFrame> =
  task {
    match! awaitFrame feed what predicate budget with
    | Ok frame -> return frame
    | Result.Error error -> return failwith error
  }

/// Throw away whatever is queued, so the next wait only sees what happens after now.
let drain (feed: SseFeed) =
  let mutable frame = Unchecked.defaultof<SseFrame>
  while feed.Frames.TryRead(&frame) do
    ()

/// Run `read` over the parsed frame.
let withJson (frame: SseFrame) (read: JsonElement -> 'a) : 'a =
  use doc = JsonDocument.Parse frame.Data
  read doc.RootElement

/// The entry of a `test_results_batch` frame for one test, if the frame carries it.
let private entryOf (testName: string) (root: JsonElement) : JsonElement voption =
  match root.TryGetProperty "Entries" with
  | false, _ -> ValueNone
  | true, entries ->
    entries.EnumerateArray()
    |> Seq.tryFind (fun e -> e.GetProperty("DisplayName").GetString() = testName)
    |> function Some e -> ValueSome (e.Clone()) | None -> ValueNone

let verdictIn (testName: string) (frame: SseFrame) : Verdict =
  match frame.Event with
  | "test_results_batch" ->
    withJson frame (fun root ->
      match entryOf testName root with
      | ValueNone -> Verdict.NotInFrame
      | ValueSome entry ->
        match entry.GetProperty("Status").GetProperty("Case").GetString() with
        | "Passed" -> Verdict.Passed
        | "Failed" -> Verdict.Failed
        | other -> Verdict.Other other)
  | _ -> Verdict.NotInFrame

/// The provenance case a `test_results_batch` frame carries for one test ("" when not in the frame).
let provenanceIn (testName: string) (frame: SseFrame) : string =
  match frame.Event with
  | "test_results_batch" ->
    withJson frame (fun root ->
      match entryOf testName root with
      | ValueNone -> ""
      | ValueSome entry ->
        match entry.TryGetProperty "Provenance" with
        | true, p -> p.GetProperty("Case").GetString()
        | false, _ -> "")
  | _ -> ""

/// The `Fields` of a provenance case of one test, as text ("" when not in the frame).
let provenanceDetailIn (testName: string) (frame: SseFrame) : string =
  match frame.Event with
  | "test_results_batch" ->
    withJson frame (fun root ->
      match entryOf testName root with
      | ValueNone -> ""
      | ValueSome entry ->
        match entry.TryGetProperty "Provenance" with
        | true, p ->
          match p.TryGetProperty "Fields" with
          | true, fields -> fields.GetRawText()
          | false, _ -> ""
        | false, _ -> "")
  | _ -> ""

/// A `test_summary` frame that says the suite is settled with this many failures and nothing running.
let settledWith (failed: int) (frame: SseFrame) : bool =
  match frame.Event with
  | "test_summary" ->
    withJson frame (fun root ->
      root.GetProperty("Running").GetInt32() = 0
      && root.GetProperty("Stale").GetInt32() = 0
      && root.GetProperty("Failed").GetInt32() = failed
      && root.GetProperty("Total").GetInt32() > 0)
  | _ -> false

/// Whether the daemon's status says the suite is settled with this many failures and nothing running.
let private statusIsSettledWith (failed: int) (statusJson: string) : bool =
  use doc = JsonDocument.Parse statusJson
  let summary = doc.RootElement.GetProperty("Summary")
  summary.GetProperty("Running").GetInt32() = 0
  && summary.GetProperty("Stale").GetInt32() = 0
  && summary.GetProperty("Failed").GetInt32() = failed
  && summary.GetProperty("Total").GetInt32() > 0

/// Wait until the suite is settled with this many failures and nothing running. The stream may already
/// have said so (a frame read while waiting for something else is gone), so the daemon's own status is
/// asked first, and only if it is not settled yet does this wait for the next summary frame.
let awaitSettled (feed: SseFeed) (http: HttpClient) (failed: int) (budget: TimeSpan) : Task<unit> =
  task {
    let! status = http.GetStringAsync "/api/live-testing/status"
    match statusIsSettledWith failed status with
    | true -> ()
    | false ->
      let! _ = expectFrame feed (sprintf "settling with %d failing" failed) (settledWith failed) budget
      ()
  }

/// The run generation a `test_results_batch` frame is from (-1 for any other frame).
let generationIn (frame: SseFrame) : int64 =
  match frame.Event with
  | "test_results_batch" ->
    withJson frame (fun root ->
      let generation = root.GetProperty "Generation"
      match generation.ValueKind with
      | JsonValueKind.Number -> generation.GetInt64()
      | _ ->
        // A single-case union written as an object: the number is the only digits in it.
        System.Text.RegularExpressions.Regex.Match(generation.GetRawText(), "[0-9]+").Value |> int64)
  | _ -> -1L

/// A `test_results_batch` frame in which no row is still waiting for a real build to confirm it.
let private batchIsConfirmed (frame: SseFrame) : bool =
  match frame.Event with
  | "test_results_batch" ->
    withJson frame (fun root ->
      match root.TryGetProperty "Entries" with
      | true, entries ->
        entries.EnumerateArray()
        |> Seq.forall (fun e ->
          match e.TryGetProperty "Provenance" with
          | true, p -> p.GetProperty("Case").GetString() <> "Evaluated"
          | false, _ -> true)
      | false, _ -> false)
  | _ -> false

/// A `test_results_batch` frame in which some row ran against evaluated code that nothing has confirmed.
let hasEvaluatedRow (frame: SseFrame) : bool =
  match frame.Event with
  | "test_results_batch" -> not (batchIsConfirmed frame)
  | _ -> false

/// Wait until every row says what a real build made of it (none is `Evaluated`), so a journey starts from a
/// session whose last confirmation is over and cannot answer into the next journey's rows. Asks the daemon's
/// status first, for the same reason `awaitSettled` does.
let awaitConfirmed (feed: SseFeed) (http: HttpClient) (budget: TimeSpan) : Task<unit> =
  task {
    let! status = http.GetStringAsync "/api/live-testing/status"
    use doc = JsonDocument.Parse status
    match doc.RootElement.GetProperty("Unconfirmed").GetInt32() with
    | 0 -> ()
    | _ ->
      let! _ = expectFrame feed "every row confirmed by a build" batchIsConfirmed budget
      ()
  }

/// The `LastDecision` a `test_summary` frame carries: its precision, reason and selected tests.
let decisionIn (frame: SseFrame) : (string * string * string list) voption =
  match frame.Event with
  | "test_summary" ->
    withJson frame (fun root ->
      match root.TryGetProperty "LastDecision" with
      | true, d when d.ValueKind = JsonValueKind.Object ->
        ValueSome (
          d.GetProperty("Precision").GetString(),
          d.GetProperty("Reason").GetString(),
          [ for t in d.GetProperty("SelectedTests").EnumerateArray() -> t.GetString() ])
      | _ -> ValueNone)
  | _ -> ValueNone

/// The tests the `LastDecision` of a `test_summary` frame says it left out on purpose, by full name.
let deferredIn (frame: SseFrame) : string list =
  match frame.Event with
  | "test_summary" ->
    withJson frame (fun root ->
      match root.TryGetProperty "LastDecision" with
      | true, d when d.ValueKind = JsonValueKind.Object ->
        [ for t in d.GetProperty("DeferredTests").EnumerateArray() -> t.GetString() ]
      | _ -> [])
  | _ -> []

let sessionId (http: HttpClient) : Task<string> =
  task {
    let! body = http.GetStringAsync "/api/sessions"
    use doc = JsonDocument.Parse body
    return doc.RootElement.GetProperty("sessions").EnumerateArray() |> Seq.head |> fun s -> s.GetProperty("id").GetString()
  }

let postBuffer (http: HttpClient) (sid: string) (content: string) : Task =
  task {
    let payload = JsonSerializer.Serialize {| filePath = helloPath (); content = content |}
    use body = new StringContent(payload, Encoding.UTF8, "application/json")
    let! response = http.PostAsync(sprintf "/api/sessions/%s/buffer-changed" sid, body)
    response.EnsureSuccessStatusCode() |> ignore
  }

/// POST JSON and read the status and body back.
let postJson (http: HttpClient) (path: string) (payload: obj) : Task<int * string> =
  task {
    use body = new StringContent(JsonSerializer.Serialize payload, Encoding.UTF8, "application/json")
    use! response = http.PostAsync(path, body)
    let! text = response.Content.ReadAsStringAsync()
    return int response.StatusCode, text
  }

let getText (http: HttpClient) (path: string) : Task<string> = http.GetStringAsync path

/// Run `body`, then `cleanup` whether it passed or failed, then re-raise what `body` raised. A task
/// expression cannot await inside `finally`, and a journey that edits a file on disk has to put it
/// back and stop its feed no matter how it ended.
let ensuring (cleanup: unit -> Task) (body: unit -> Task<'a>) : Task<'a> =
  task {
    let mutable failure : exn voption = ValueNone
    let mutable result = Unchecked.defaultof<'a>
    try
      let! value = body ()
      result <- value
    with ex ->
      failure <- ValueSome ex
    do! cleanup ()
    match failure with
    | ValueSome ex -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
    | ValueNone -> ()
    return result
  }

/// The line number (1-based) of the first line of Hello.fs that contains `text`.
let lineOf (text: string) : int =
  File.ReadAllLines(helloPath ())
  |> Array.findIndex (fun l -> l.Contains text)
  |> (+) 1
