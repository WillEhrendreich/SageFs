/// Honest latency numbers for the hot-reload path: how long after a save the running app serves the
/// new code, and how long until the daemon says so.
///
/// The first half is pure: reading a frame of the daemon's `/events` stream, and turning the moments
/// a save passed through into the time each stage took. The second half drives the daemon the
/// `--integration-hr` runner started. It times the real path and nothing else: the clock starts
/// before the first byte of the save is written and stops on a response the app itself sent or a
/// frame the daemon itself pushed.
module SageFs.Tests.HotReloadLatency

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open SageFs.Tests.LatencyStats

// ---------------------------------------------------------------------------------------------
// The pure half
// ---------------------------------------------------------------------------------------------

/// What the daemon's stream said about one session's reload.
[<RequireQualifiedAccess>]
type ReloadFrame =
  /// The frame is about another session.
  | OtherSession
  /// The frame carries no reload (`reloadReported` is absent or null).
  | NotAReload
  /// The worker started compiling `file` (empty when the frame names none).
  | Compiling of file: string
  | Finished of SageFs.ReloadCase
  /// The session's new worker began warming up: a restart has built and started it.
  | WorkerWarming
  /// The session's new worker is Ready.
  | WorkerReady
  /// A frame this reader cannot read. Named, so a worker that changes the wire is noticed.
  | Unreadable of detail: string

module ReloadFrame =
  /// Read one `data:` payload of the `/events` stream as far as session `sessionId`'s reload goes.
  let ofData (sessionId: string) (data: string) : ReloadFrame =
    try
      use doc = JsonDocument.Parse data
      let root = doc.RootElement
      // The ready frame names its session under its own key (`{"sessionReady":"<id>"}`), the rest under `sessionId`.
      let namedSession =
        match root.ValueKind with
        | JsonValueKind.Object ->
          [ "sessionId"; "sessionReady" ]
          |> List.tryPick (fun key ->
            match root.TryGetProperty key with
            | true, id when id.ValueKind = JsonValueKind.String -> Some (id.GetString())
            | _ -> None)
        | _ -> None
      let isOtherSession = namedSession |> Option.exists (fun id -> id <> sessionId)
      let isWarming =
        match root.ValueKind, root.TryGetProperty "warmupProgress" with
        | JsonValueKind.Object, (true, flag) -> flag.ValueKind = JsonValueKind.True
        | _ -> false
      match isOtherSession, isWarming, root.ValueKind, root.TryGetProperty "sessionReady" with
      | true, _, _, _ -> ReloadFrame.OtherSession
      | false, true, _, _ -> ReloadFrame.WorkerWarming
      | false, false, JsonValueKind.Object, (true, _) -> ReloadFrame.WorkerReady
      | false, false, _, _ ->
        match root.ValueKind, root.TryGetProperty "reloadReported" with
        | JsonValueKind.Object, (true, reload) when reload.ValueKind = JsonValueKind.Object ->
          match reload.GetProperty("state").GetString() with
          | "compiling" ->
            match reload.TryGetProperty "file" with
            | true, file when file.ValueKind = JsonValueKind.String -> ReloadFrame.Compiling (file.GetString())
            | _ -> ReloadFrame.Compiling ""
          | "finished" ->
            match reload.TryGetProperty "outcome" with
            | true, outcome when outcome.ValueKind = JsonValueKind.String ->
              match SageFs.ReloadCase.ofToken (outcome.GetString()) with
              | Ok case -> ReloadFrame.Finished case
              | Result.Error error -> ReloadFrame.Unreadable (SageFs.ReloadPayloadError.describe error)
            | _ -> ReloadFrame.Unreadable "a finished reload carries no outcome"
          | other -> ReloadFrame.Unreadable (sprintf "unknown reload state '%s'" other)
        | _ -> ReloadFrame.NotAReload
    with :? JsonException as ex -> ReloadFrame.Unreadable ex.Message

/// A moment a measurement may or may not have seen.
[<RequireQualifiedAccess>]
type Moment =
  | Observed of stamp: int64
  | NotObserved

/// The stages a save passes through, in the order the stream reports them.
[<RequireQualifiedAccess>]
type Stage =
  /// The worker started compiling the saved file: the file watcher, its debounce and the planning are behind it.
  | Compiling
  /// The worker applied the save and said so (a patch applied and not yet seen running, or a restart decided).
  | Applied
  /// A restart only: the session's new worker began warming up, so the rebuild is behind it.
  | Warming
  /// A restart only: the session's new worker is Ready.
  | Ready
  /// The app sent its first response carrying the new value.
  | Served
  /// The daemon's verdict reached `Patched`: the new code was seen running.
  | Confirmed

/// The moments one save passed through, as stopwatch stamps. `SavedAt` is taken just before the first byte is written.
type SaveStamps =
  { SavedAt: int64
    CompilingAt: Moment
    AppliedAt: Moment
    WarmingAt: Moment
    ReadyAt: Moment
    ServedAt: Moment
    /// When the request that got the first new answer was sent. It can be before the patch landed (a request in flight).
    AnswerSentAt: Moment
    ConfirmedAt: Moment }

/// How long after the save a stage was reached, or that it never was.
[<RequireQualifiedAccess>]
type Elapsed =
  | After of TimeSpan
  | Never

/// The series a latency gate judges. A closed set: each is printed under its own name and bounded on its own.
[<RequireQualifiedAccess>]
type Series =
  | PatchSaveToServed
  | PatchSaveToConfirmed
  | RestartSaveToServed

module Series =
  let all : Series list = [ Series.PatchSaveToServed; Series.PatchSaveToConfirmed; Series.RestartSaveToServed ]

  let name (series: Series) : string =
    match series with
    | Series.PatchSaveToServed -> "hr-patch-save-to-served"
    | Series.PatchSaveToConfirmed -> "hr-patch-save-to-confirmed"
    | Series.RestartSaveToServed -> "hr-restart-save-to-served"

  /// The path a series measures, for the line that breaks its stages down.
  let path (series: Series) : string =
    match series with
    | Series.PatchSaveToServed | Series.PatchSaveToConfirmed -> "hr-patch"
    | Series.RestartSaveToServed -> "hr-restart"

type Sample =
  { Compiling: Elapsed
    Applied: Elapsed
    Warming: Elapsed
    Ready: Elapsed
    /// Every sample was served: a save the app never showed is a failure, not a sample.
    Served: TimeSpan
    /// How long the request that got the first new answer took, sent to answered. A `Served` that is mostly
    /// this is the transport and not the patch.
    AnswerTook: Elapsed
    Confirmed: Elapsed }

[<RequireQualifiedAccess>]
type SampleRefusal =
  /// The app never served the new value, so there is no latency to report.
  | NeverServed
  /// A stage's stamp is older than the save, so the clock was read from the wrong event.
  | BeforeTheSave of Stage
  /// The series needs the verdict and a sample never reached it.
  | NeverConfirmed

module Sample =
  let private after (savedAt: int64) (stage: Stage) (moment: Moment) : Result<Elapsed, SampleRefusal> =
    match moment with
    | Moment.NotObserved -> Ok Elapsed.Never
    | Moment.Observed stamp when stamp < savedAt -> Result.Error (SampleRefusal.BeforeTheSave stage)
    | Moment.Observed stamp -> Ok (Elapsed.After (LtStream.elapsed savedAt stamp))

  let ofStamps (stamps: SaveStamps) : Result<Sample, SampleRefusal> =
    match stamps.ServedAt with
    | Moment.NotObserved -> Result.Error SampleRefusal.NeverServed
    | served ->
      after stamps.SavedAt Stage.Compiling stamps.CompilingAt
      |> Result.bind (fun compiling ->
        after stamps.SavedAt Stage.Applied stamps.AppliedAt
        |> Result.bind (fun applied ->
          after stamps.SavedAt Stage.Warming stamps.WarmingAt
          |> Result.bind (fun warming ->
            after stamps.SavedAt Stage.Ready stamps.ReadyAt
            |> Result.bind (fun ready ->
              after stamps.SavedAt Stage.Served served
              |> Result.bind (fun servedAfter ->
                after stamps.SavedAt Stage.Confirmed stamps.ConfirmedAt
                |> Result.map (fun confirmed ->
                  let answerTook =
                    match stamps.AnswerSentAt, served with
                    | Moment.Observed sent, Moment.Observed answered when answered >= sent -> Elapsed.After (LtStream.elapsed sent answered)
                    | _ -> Elapsed.Never
                  { Compiling = compiling
                    Applied = applied
                    Warming = warming
                    Ready = ready
                    Served = (match servedAfter with | Elapsed.After d -> d | Elapsed.Never -> TimeSpan.Zero)
                    AnswerTook = answerTook
                    Confirmed = confirmed }))))))

  let private confirmedOf (sample: Sample) : Result<TimeSpan, SampleRefusal> =
    match sample.Confirmed with
    | Elapsed.After d -> Ok d
    | Elapsed.Never -> Result.Error SampleRefusal.NeverConfirmed

  let private collect (read: Sample -> Result<TimeSpan, SampleRefusal>) (samples: Sample list) : Result<TimeSpan list, SampleRefusal> =
    samples
    |> List.fold
      (fun acc sample -> acc |> Result.bind (fun done' -> read sample |> Result.map (fun d -> d :: done')))
      (Ok [])
    |> Result.map List.rev

  /// The times of one series over the samples, or why they cannot be read.
  let series (series: Series) (samples: Sample list) : Result<TimeSpan list, SampleRefusal> =
    match series with
    | Series.PatchSaveToServed | Series.RestartSaveToServed -> samples |> collect (fun s -> Ok s.Served)
    | Series.PatchSaveToConfirmed -> samples |> collect confirmedOf

  let private reached (read: Sample -> Elapsed) (samples: Sample list) : TimeSpan list =
    samples |> List.choose (fun s -> match read s with | Elapsed.After d -> Some d | Elapsed.Never -> None)

  /// One line with every sample's save-to-served time in the order the saves were made, so a bimodal
  /// or drifting series can be seen and not only summarised.
  let servedLine (series: Series) (samples: Sample list) : string =
    let served =
      samples
      |> List.map (fun s -> sprintf "%.0f" s.Served.TotalMilliseconds)
      |> String.concat ","
    // How long the answering request itself took, beside it, so transport time can be told from patch time.
    let answer =
      match samples |> List.exists (fun s -> s.AnswerTook <> Elapsed.Never) with
      | false -> ""
      | true ->
        samples
        |> List.map (fun s -> match s.AnswerTook with | Elapsed.After d -> sprintf "%.0f" d.TotalMilliseconds | Elapsed.Never -> "-")
        |> String.concat ","
        |> sprintf " answer-ms=%s"
    sprintf "SAMPLES %s served-ms=%s%s" (Series.path series) served answer

  /// One line saying how long after the save each stage was reached (median and 95th percentile), so a
  /// wide spread can be traced to the stage that carries it. A stage no sample reached is left out, and
  /// one only some reached says how many.
  let stageLine (series: Series) (samples: Sample list) : string =
    let count = List.length samples
    let stage (label: string) (times: TimeSpan list) : string list =
      match summarize times with
      | Result.Error _ -> []
      | Ok summary ->
        let of' = match List.length times = count with | true -> "" | false -> sprintf "(%d of %d)" (List.length times) count
        [ sprintf "%s-p50=%.1fms%s" label summary.P50.TotalMilliseconds of'
          sprintf "%s-p95=%.1fms" label summary.P95.TotalMilliseconds ]
    let stages =
      [ stage "compiling" (reached (fun s -> s.Compiling) samples)
        stage "applied" (reached (fun s -> s.Applied) samples)
        stage "warming" (reached (fun s -> s.Warming) samples)
        stage "ready" (reached (fun s -> s.Ready) samples)
        stage "served" (samples |> List.map (fun s -> s.Served))
        stage "confirmed" (reached (fun s -> s.Confirmed) samples) ]
      |> List.concat
    sprintf "STAGES %s n=%d %s" (Series.path series) count (String.concat " " stages)

// ---------------------------------------------------------------------------------------------
// The half that needs the daemon
// ---------------------------------------------------------------------------------------------

/// A reload frame, stamped when this process read it.
type StampedFrame = { Frame: ReloadFrame; At: int64 }

/// The daemon's `/events` stream for one session, read on its own task.
type ReloadFeed =
  { Frames: ChannelReader<StampedFrame>
    /// Completes when the stream has said anything: the daemon replays its state on connect.
    Connected: Task
    Stop: unit -> Task }

let private openReloadFeed (mcpPort: int) (sessionId: string) : ReloadFeed =
  let channel = Channel.CreateUnbounded<StampedFrame>()
  let connected = TaskCompletionSource()
  let cts = new CancellationTokenSource()
  let http = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" mcpPort), Timeout = Timeout.InfiniteTimeSpan)
  let pump =
    task {
      try
        try
          use! response = http.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead, cts.Token)
          use! stream = response.Content.ReadAsStreamAsync(cts.Token)
          use reader = new StreamReader(stream, Encoding.UTF8)
          let mutable reading = true
          while reading do
            let! line = reader.ReadLineAsync(cts.Token)
            match line with
            | null -> reading <- false
            | l when l.StartsWith("data:", StringComparison.Ordinal) ->
              let stamp = Stopwatch.GetTimestamp()
              connected.TrySetResult() |> ignore
              match ReloadFrame.ofData sessionId (l.Substring("data:".Length).TrimStart()) with
              | ReloadFrame.OtherSession | ReloadFrame.NotAReload -> ()
              | frame -> channel.Writer.TryWrite { Frame = frame; At = stamp } |> ignore
            | _ -> ()
        with
        | :? OperationCanceledException -> ()
        | :? IOException -> ()
      finally
        channel.Writer.TryComplete() |> ignore
        http.Dispose()
    }
  { Frames = channel.Reader
    Connected = connected.Task
    Stop =
      fun () ->
        cts.Cancel()
        pump }

/// Throw away what the feed has queued, so a sample starts from frames that follow its own save.
let private drain (feed: ReloadFeed) : unit =
  let mutable frame = Unchecked.defaultof<StampedFrame>
  while feed.Frames.TryRead(&frame) do ()

/// The first response that said what was expected: when its request was sent and when it arrived.
type private Answer = { SentAt: int64; At: int64 }

/// Request `url` until a response says `expected`, and return the moment that response arrived.
/// The clock is the response, not the request: a poll that asks every `pollTight` still stamps the
/// first answer that carried the new value at the time it arrived, so the interval limits how soon
/// the answer is asked for and never how late it is stamped. A refused connection is the app being
/// restarted, and is asked again.
let private firstResponseSaying (http: HttpClient) (url: string) (expected: string) (ct: CancellationToken) : Task<Answer> =
  task {
    let mutable answer = Moment.NotObserved
    let mutable sentAt = 0L
    let mutable lastBody = "(no response)"
    while answer = Moment.NotObserved do
      try
        sentAt <- Stopwatch.GetTimestamp()
        let! body = http.GetStringAsync(url, ct)
        let stamp = Stopwatch.GetTimestamp()
        lastBody <- body.Trim()
        match lastBody = expected with
        | true -> answer <- Moment.Observed stamp
        | false -> ()
      with
      | :? HttpRequestException -> ()
      | :? OperationCanceledException when ct.IsCancellationRequested ->
        failwithf "%s never said '%s'. The last answer was '%s'" url expected lastBody
      | :? TaskCanceledException when not ct.IsCancellationRequested -> ()
      match answer with
      | Moment.NotObserved ->
        try
          do! Task.Delay(TestTimeouts.pollTight, ct)
        with :? OperationCanceledException -> failwithf "%s never said '%s'. The last answer was '%s'" url expected lastBody
      | Moment.Observed _ -> ()
    return { SentAt = sentAt; At = (match answer with | Moment.Observed stamp -> stamp | Moment.NotObserved -> 0L) }
  }

/// What a save is followed until.
[<RequireQualifiedAccess>]
type Following =
  /// A patch: until the verdict reaches `Patched`. A save that ends any other way is a failure here.
  | UntilPatched
  /// A restart: until the app serves the new value. The frames the stream sent meanwhile are read for the stages.
  | UntilServed

/// One save: the file, the new text, the route that must serve it and what it must say.
type Save =
  { Path: string
    Content: string
    Url: string
    Expected: string }

let private writeFile (path: string) (content: string) : Task<unit> =
  task {
    use cts = new CancellationTokenSource(TestTimeouts.fileLockRetryPatience)
    let mutable written = false
    while not written && not cts.IsCancellationRequested do
      try
        File.WriteAllText(path, content)
        written <- true
      with :? IOException ->
        do! Task.Delay(TestTimeouts.pollPage)
    match written with
    | true -> ()
    | false -> failwithf "%s could not be written within %O" path TestTimeouts.fileLockRetryPatience
  }

/// Make one save and follow it. The stamp before the write is the first byte of the save; every other
/// moment is the arrival of something the daemon or the app sent.
let private saveAndFollow (feed: ReloadFeed) (http: HttpClient) (following: Following) (budget: TimeSpan) (save: Save) : Task<SaveStamps> =
  task {
    drain feed
    use cts = new CancellationTokenSource(budget)
    let savedAt = Stopwatch.GetTimestamp()
    do! writeFile save.Path save.Content
    let served = firstResponseSaying http save.Url save.Expected cts.Token
    let mutable compilingAt = Moment.NotObserved
    let mutable appliedAt = Moment.NotObserved
    let mutable warmingAt = Moment.NotObserved
    let mutable readyAt = Moment.NotObserved
    let mutable confirmedAt = Moment.NotObserved
    // The daemon serves one compile at a time, so a verdict that comes before the compile of THIS file started
    // belongs to an earlier save (the journey before this one ends with an unawaited restore of another file).
    // A patched save is followed from its own `Compiling` frame on; a restart has no compile to wait for.
    let mutable ownCompileStarted =
      match following with
      | Following.UntilPatched -> false
      | Following.UntilServed -> true
    let isOwnFile (file: string) = Path.GetFileName file = Path.GetFileName save.Path
    let note (frame: StampedFrame) =
      match frame.Frame, following with
      | ReloadFrame.Compiling file, _ ->
        match isOwnFile file, compilingAt with
        | true, Moment.NotObserved ->
          compilingAt <- Moment.Observed frame.At
          ownCompileStarted <- true
        | _ -> ()
      | ReloadFrame.Finished _, Following.UntilPatched when not ownCompileStarted -> ()
      | ReloadFrame.Finished SageFs.ReloadCase.PatchPending, Following.UntilPatched -> appliedAt <- Moment.Observed frame.At
      | ReloadFrame.Finished SageFs.ReloadCase.Patched, Following.UntilPatched -> confirmedAt <- Moment.Observed frame.At
      | ReloadFrame.Finished other, Following.UntilPatched ->
        failwithf "the save of %s ended as %s, and a patched save was wanted" save.Path (SageFs.ReloadCase.token other)
      | ReloadFrame.Finished _, Following.UntilServed ->
        match appliedAt with
        | Moment.NotObserved -> appliedAt <- Moment.Observed frame.At
        | Moment.Observed _ -> ()
      | ReloadFrame.WorkerWarming, _ ->
        match warmingAt with
        | Moment.NotObserved -> warmingAt <- Moment.Observed frame.At
        | Moment.Observed _ -> ()
      | ReloadFrame.WorkerReady, _ ->
        match readyAt with
        | Moment.NotObserved -> readyAt <- Moment.Observed frame.At
        | Moment.Observed _ -> ()
      | ReloadFrame.Unreadable detail, _ -> failwithf "the stream sent a frame that could not be read: %s" detail
      | (ReloadFrame.OtherSession | ReloadFrame.NotAReload), _ -> ()
    try
      match following with
      | Following.UntilPatched ->
        while confirmedAt = Moment.NotObserved do
          let! frame = feed.Frames.ReadAsync(cts.Token)
          note frame
      | Following.UntilServed -> ()
      let! answer = served
      // What the stream sent while the app was being restarted has arrived by now: the stamps are kept as read.
      let mutable frame = Unchecked.defaultof<StampedFrame>
      while feed.Frames.TryRead(&frame) do note frame
      return
        { SavedAt = savedAt
          CompilingAt = compilingAt
          AppliedAt = appliedAt
          WarmingAt = warmingAt
          ReadyAt = readyAt
          ServedAt = Moment.Observed answer.At
          AnswerSentAt = Moment.Observed answer.SentAt
          ConfirmedAt = confirmedAt }
    with :? OperationCanceledException ->
      return failwithf "the save of %s was not followed to its end within %O (compiling %A, applied %A, confirmed %A)" save.Path budget compilingAt appliedAt confirmedAt
  }

let private samplesOf (stamps: SaveStamps list) : Sample list =
  stamps
  |> List.map (fun s ->
    match Sample.ofStamps s with
    | Ok sample -> sample
    | Result.Error refusal -> failwithf "a save could not be read as a sample: %A" refusal)

type private SessionRef = { Id: string }

/// The session whose working directory is `dir`, as `/api/sessions` reports it.
let private sessionOf (http: HttpClient) (mcpPort: int) (dir: string) : Task<SessionRef> =
  task {
    let! body = http.GetStringAsync(sprintf "http://localhost:%d/api/sessions" mcpPort)
    use doc = JsonDocument.Parse body
    let wanted = Path.GetFullPath dir
    return
      doc.RootElement.GetProperty("sessions").EnumerateArray()
      |> Seq.tryFind (fun s -> String.Equals(Path.GetFullPath(s.GetProperty("workingDirectory").GetString()), wanted, StringComparison.Ordinal))
      |> Option.map (fun s -> { Id = s.GetProperty("id").GetString() })
      |> Option.defaultWith (fun () -> failwithf "no session has working directory %s" wanted)
  }

let private postJson (http: HttpClient) (url: string) (json: string) : Task<int * string> =
  task {
    use content = new StringContent(json, Encoding.UTF8, "application/json")
    use! resp = http.PostAsync(url, content)
    let! text = resp.Content.ReadAsStringAsync()
    return int resp.StatusCode, text
  }

type private SessionIdBody = { SessionId: string }

type private SessionCreateBody =
  { Projects: string array
    WorkingDirectory: string
    Workflow: string }

// ---- the patched path -----------------------------------------------------------------------

/// What the patched path edits: two files, saved in turn. The watcher drops a second event for a file
/// within `Timeouts.doubleCompileGuard` of the last one it compiled, and nothing tells a client when
/// that window has closed, so the saves alternate between two files instead of waiting a guard's
/// worth out. Each edit puts a new tag in the body, so a response that carries it can only come from this save.
///
/// The files are taken as the journeys before this one left them (the called-callee journey leaves its
/// helper at "B"), so the edit replaces whatever string literal follows the anchor and not a literal
/// text, and what the route serves before the first save is read from the app.
type private PatchTarget =
  { File: string
    /// What comes right before the string literal the edits replace, once in the file.
    Before: string
    Literal: string -> string
    Route: string
    /// What the route serves with a tag in the literal.
    Served: string -> string }

let private greetingTarget =
  { File = "Greeting.fs"
    Before = "let greeting () = \""
    Literal = fun tag -> sprintf "hello from sagefs %s" tag
    Route = "/"
    Served = fun tag -> sprintf "<h1>hello from sagefs %s</h1>" tag }

let private calledTarget =
  { File = "CalledCallee.fs"
    Before = "let calledHelper () : string = \""
    Literal = fun tag -> sprintf "B-%s" tag
    Route = "/callee/called"
    Served = fun tag -> sprintf "B-%s" tag }

let private requireOnce (what: string) (find: string) (text: string) : unit =
  match text.Split([| find |], StringSplitOptions.None).Length - 1 with
  | 1 -> ()
  | n -> failwithf "the edit anchor for %s has to appear exactly once, it appears %d times: %s" what n find

/// `text` with the string literal after `target.Before` replaced by the one for `tag`.
let private withTag (target: PatchTarget) (tag: string) (text: string) : string =
  let start = text.IndexOf(target.Before, StringComparison.Ordinal) + target.Before.Length
  let stop = text.IndexOf('"', start)
  text.Substring(0, start) + target.Literal tag + text.Substring stop

/// Wait for the stream to say anything, so a save made next is made to a connected feed.
let private awaitConnected (feed: ReloadFeed) : Task<unit> =
  task {
    let! _ = Task.WhenAny(feed.Connected, Task.Delay TestTimeouts.patience)
    match feed.Connected.IsCompleted with
    | true -> ()
    | false -> failwithf "the /events stream said nothing within %O" TestTimeouts.patience
  }

/// Measure `samplesPerPath` saves after `warmupEdits` on the running net11 fixture app, each a patch
/// the session applies in place. Starts from the app the runner started and leaves the files as found.
let measurePatchedSaves () : Task<Sample list> =
  task {
    let env = HotReloadInlinedCalleeJourneyTests.Env.read
    let mcpPort = int (env HotReloadInlinedCalleeJourneyTests.Env.mcpPort)
    let dashboardPort = env HotReloadInlinedCalleeJourneyTests.Env.dashboardPort
    let appUrl = env HotReloadInlinedCalleeJourneyTests.Env.net11AppUrl
    let dir = env HotReloadInlinedCalleeJourneyTests.Env.net11FixtureDir
    use http = new HttpClient(Timeout = TestTimeouts.httpRequest)
    let! session = sessionOf http mcpPort dir
    let hotReload = sprintf "http://localhost:%s/api/sessions/%s/hotreload" dashboardPort session.Id
    let! watchStatus, watchBody = postJson http (hotReload + "/watch-all") "{}"
    match watchStatus with
    | 200 -> ()
    | status -> failwithf "watch-all answered %d: %s" status watchBody
    let feed = openReloadFeed mcpPort session.Id
    let targets = [| greetingTarget; calledTarget |]
    let originals = targets |> Array.map (fun t -> Path.Combine(dir, t.File), File.ReadAllText(Path.Combine(dir, t.File)))
    Array.iter2 (fun (t: PatchTarget) (_, text) -> requireOnce t.File t.Before text) targets originals
    // What each route serves before the first save, which is what the files going back must serve again.
    let! servedAsFound =
      targets
      |> Array.map (fun t ->
        task {
          let! body = http.GetStringAsync(appUrl + t.Route)
          return body.Trim()
        })
      |> Task.WhenAll
    let cleanup () : Task =
      task {
        // Each file goes back, the one saved last going last: two saves of one file inside the guard's window drop the second.
        for index in [ 0; 1 ] do
          let path, text = originals.[index]
          let target = targets.[index]
          // A file the measurement never changed is already as found, and writing it again is no save.
          // Best effort: a restore that fails is said and does not hide the failure that got us here.
          match File.ReadAllText path = text with
          | true -> ()
          | false ->
            try
              let! _ =
                saveAndFollow feed http Following.UntilPatched TestTimeouts.saveVerdict
                  { Path = path; Content = text; Url = appUrl + target.Route; Expected = servedAsFound.[index] }
              ()
            with ex ->
              eprintfn "restoring %s did not finish: %s" path ex.Message
              File.WriteAllText(path, text)
        let! _ = postJson http (hotReload + "/unwatch-all") "{}"
        do! feed.Stop()
      }
    return!
      LtStream.ensuring cleanup (fun () ->
        task {
          do! awaitConnected feed
          let stamps = ResizeArray<SaveStamps>()
          for i in 1 .. warmupEdits + samplesPerPath do
            let index = (i - 1) % targets.Length
            let target = targets.[index]
            let path, original = originals.[index]
            let tag = sprintf "v%03d" i
            let! saved =
              saveAndFollow feed http Following.UntilPatched TestTimeouts.saveVerdict
                { Path = path
                  Content = withTag target tag original
                  Url = appUrl + target.Route
                  Expected = target.Served tag }
            match i > warmupEdits with
            | true -> stamps.Add saved
            | false -> ()
          return samplesOf (List.ofSeq stamps)
        })
  }

// ---- the restart path -----------------------------------------------------------------------

/// What the restart fixture serves as shipped.
let private restartShipped = "served from a run_app app"

/// Measure `samplesPerPath` saves after `warmupEdits` to a web app `run_app` runs, on its own copy of
/// the RunAppRestartFixture in a session of the same daemon. A save to such an app is a restart
/// (`AppPlacement.InWorkerProcess`): SageFs rebuilds and relaunches it where it listened before.
let measureRestartedSaves () : Task<Sample list> =
  task {
    let env = HotReloadInlinedCalleeJourneyTests.Env.read
    let mcpPort = int (env HotReloadInlinedCalleeJourneyTests.Env.mcpPort)
    let daemon = sprintf "http://localhost:%d" mcpPort
    let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
    let fixtureSource = Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "RunAppRestartFixture")
    let dir = Path.Combine(Path.GetTempPath(), "sagefs-hr-latency", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    for file in Directory.EnumerateFiles(fixtureSource) |> Seq.filter (fun f -> Path.GetExtension f = ".fs" || Path.GetExtension f = ".fsproj") do
      File.Copy(file, Path.Combine(dir, Path.GetFileName file))
    let project = Path.Combine(dir, "RunAppRestartFixture.fsproj")
    let pagePath = Path.Combine(dir, "Page.fs")
    let original = File.ReadAllText pagePath
    requireOnce "Page.fs" (sprintf "\"%s\"" restartShipped) original
    use http = new HttpClient(Timeout = TestTimeouts.toolCallThatRestarts)
    let! built = SageFs.SessionBuild.runBuildAsync [ project ] dir |> Async.StartAsTask
    match built with
    | Ok _ -> ()
    | Result.Error err -> failwithf "the restart fixture must build through SageFs's own build path: %s" (SageFs.SageFsError.describe err)
    let created = SageFs.Json.serialize SageFs.Json.camelCase { Projects = [| project |]; WorkingDirectory = dir; Workflow = "HotReload" }
    let! createStatus, createBody = postJson http (daemon + "/api/sessions/create") created
    match createStatus with
    | 200 -> ()
    | status -> failwithf "creating the restart fixture's session answered %d: %s" status createBody
    use probe = new HttpClient(BaseAddress = Uri daemon, Timeout = TestTimeouts.httpRequest)
    let! ready, sessionsBody = HttpApiIntegrationTests.waitForReadySession probe dir TestTimeouts.sessionReadyColdBuild
    match ready with
    | true -> ()
    | false -> failwithf "the restart fixture's session never reached Ready. Sessions: %s" sessionsBody
    let! session = sessionOf http mcpPort dir
    let sessionUrl = daemon + "/api/sessions"
    let feed = openReloadFeed mcpPort session.Id
    let cleanup () : Task =
      task {
        let! _ = postJson http (sprintf "%s/%s/stop-app" sessionUrl session.Id) "{}"
        let! _ = postJson http (sessionUrl + "/stop") (SageFs.Json.serialize SageFs.Json.camelCase { SessionId = session.Id })
        do! feed.Stop()
        try Directory.Delete(dir, true) with _ -> ()
      }
    return!
      LtStream.ensuring cleanup (fun () ->
        task {
          let! runStatus, runBody = postJson http (sprintf "%s/%s/run-app" sessionUrl session.Id) "{}"
          match runStatus with
          | 200 -> ()
          | status -> failwithf "run_app answered %d: %s" status runBody
          use runDoc = JsonDocument.Parse runBody
          let appUrl =
            // The view is a record, so the daemon writes its fields as declared (`Urls`), not camelCased.
            let urls =
              runDoc.RootElement.EnumerateObject()
              |> Seq.tryFind (fun p -> String.Equals(p.Name, "urls", StringComparison.OrdinalIgnoreCase))
            match urls |> Option.bind (fun p -> p.Value.EnumerateArray() |> Seq.tryHead) with
            | Some url -> url.GetString().TrimEnd('/')
            | None -> failwithf "run_app reported no url: %s" runBody
          do! awaitConnected feed
          use first = new CancellationTokenSource(TestTimeouts.appOutputAppears)
          let! _ = firstResponseSaying http appUrl restartShipped first.Token
          let stamps = ResizeArray<SaveStamps>()
          for i in 1 .. warmupEdits + samplesPerPath do
            let edited = sprintf "%s v%03d" restartShipped i
            let! saved =
              saveAndFollow feed http Following.UntilServed TestTimeouts.appOutputAfterSave
                { Path = pagePath
                  Content = original.Replace(sprintf "\"%s\"" restartShipped, sprintf "\"%s\"" edited)
                  Url = appUrl
                  Expected = edited }
            match i > warmupEdits with
            | true -> stamps.Add saved
            | false -> ()
          return samplesOf (List.ofSeq stamps)
        })
  }
