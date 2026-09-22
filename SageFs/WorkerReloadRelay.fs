/// The daemon listening to each worker's reload stream.
///
/// A save is decided inside the worker: it re-evaluates the file, patches what
/// it can, and only then knows the outcome (patched, restart needed, or live
/// state it kept). The daemon's own file watcher fires the moment the file
/// changes, which is before any of that, so a dashboard that refetches on the
/// file event renders the state from before the save and then has nothing to
/// tell it the worker finished. The kept-state notice was the first thing that
/// showed it: it was sitting in the worker, and an open page never saw it
/// until some unrelated event came along.
///
/// So the daemon holds one subscription per session to the same
/// `/__sagefs__/reload` stream the user's browser tab gets, and every event on
/// it tells the daemon "this worker has something new". It's a push. Nothing
/// polls.
module SageFs.Server.WorkerReloadRelay

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open SageFs
open SageFs.Utils

/// The worker's reload stream. Same path the DevReload script in the user's
/// page subscribes to (SageFs.Host's `Routes.devReload`, which the daemon
/// doesn't reference).
let reloadStreamPath = "/__sagefs__/reload"

/// An SSE line that carries an event. Comments (`: heartbeat`) and the
/// `retry:` line don't.
let private sseDataPrefix = "data:"

/// What the relay is doing for one session.
[<RequireQualifiedAccess>]
type Held =
  | NotListening
  | ListeningTo of workerUrl: string

/// Where the session's worker is right now, per the session snapshot.
[<RequireQualifiedAccess>]
type Worker =
  | NoWorker
  | At of workerUrl: string

module Worker =
  /// The snapshot lookup hands back an option. This is the one place it gets
  /// turned into something that says what it means.
  let ofLookup (url: string option) =
    match url with
    | Some u -> Worker.At u
    | None -> Worker.NoWorker

/// What to do about one session.
[<RequireQualifiedAccess>]
type Step =
  /// Nothing to listen to and nothing held.
  | Nothing
  /// Start listening to this worker.
  | Listen of workerUrl: string
  /// Already listening to this exact worker.
  | Keep
  /// The session has a different worker now (a restart or a hard reset):
  /// stop listening to the old one and listen to this one.
  | Move of workerUrl: string
  /// The worker went away. Stop listening.
  | Stop

/// Pure: what the relay should do for a session, given what it holds and
/// where the worker is.
let decide (held: Held) (worker: Worker) : Step =
  match held, worker with
  | Held.NotListening, Worker.NoWorker -> Step.Nothing
  | Held.NotListening, Worker.At url -> Step.Listen url
  | Held.ListeningTo current, Worker.At url when current = url -> Step.Keep
  | Held.ListeningTo _, Worker.At url -> Step.Move url
  | Held.ListeningTo _, Worker.NoWorker -> Step.Stop

type private Command =
  | Ensure of WorkerProtocol.SessionId
  /// A stream closed without being asked to (the worker died, or the
  /// connection dropped).
  | Ended of WorkerProtocol.SessionId * workerUrl: string

/// Read one worker's reload stream until it closes or `stop` fires, calling
/// `heard` for every event on it.
let private listen (http: HttpClient) (workerUrl: string) (stop: CancellationToken) (heard: unit -> unit) : Task<unit> = task {
  try
    use req = new HttpRequestMessage(HttpMethod.Get, workerUrl.TrimEnd('/') + reloadStreamPath)
    req.Headers.Accept.ParseAdd "text/event-stream"
    use! resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stop)
    resp.EnsureSuccessStatusCode() |> ignore
    use! stream = resp.Content.ReadAsStreamAsync(stop)
    use reader = new StreamReader(stream)
    let mutable closed = false
    while not closed do
      let! line = reader.ReadLineAsync(stop)
      match line with
      | null -> closed <- true
      | l when l.StartsWith(sseDataPrefix, StringComparison.Ordinal) -> heard ()
      | _ -> ()
  with
  | :? OperationCanceledException -> ()
  | :? HttpRequestException as ex -> Log.debug "[WorkerReloadRelay] %s: %s" workerUrl ex.Message
  | :? IOException as ex -> Log.debug "[WorkerReloadRelay] %s: %s" workerUrl ex.Message
}

/// Start the relay. `workerUrlOf` reads the session snapshot, `onEvent` is
/// called (from a background task) for every event a session's worker sends.
/// Returns `ensure`: call it whenever a session may have a new worker, or may
/// have lost one. It's idempotent, and cheap when nothing changed.
let start
  (workerUrlOf: WorkerProtocol.SessionId -> string option)
  (onEvent: WorkerProtocol.SessionId -> unit)
  (shutdown: CancellationToken)
  : WorkerProtocol.SessionId -> unit =
  // Its own client: the shared one has a request timeout, and this stream is
  // supposed to stay open for the life of the worker.
  let http = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)
  let agent =
    MailboxProcessor<Command>.Start((fun inbox ->
      let rec loop (listening: Map<WorkerProtocol.SessionId, string * CancellationTokenSource>) = async {
        let! command = inbox.Receive()
        let heldFor sid =
          match Map.tryFind sid listening with
          | Some (url, _) -> Held.ListeningTo url
          | None -> Held.NotListening
        let stopHeld sid =
          match Map.tryFind sid listening with
          | Some (_, cts) ->
            cts.Cancel()
            cts.Dispose()
          | None -> ()
        let open' sid url =
          let cts = CancellationTokenSource.CreateLinkedTokenSource shutdown
          let token = cts.Token
          task {
            do! listen http url token (fun () ->
              try onEvent sid
              with ex -> Log.warn "[WorkerReloadRelay] handling an event from %s threw: %s" url ex.Message)
            match token.IsCancellationRequested with
            | true -> ()
            | false -> inbox.Post(Ended(sid, url))
          }
          |> ignore
          Map.add sid (url, cts) listening
        match command with
        | Ensure sid ->
          match decide (heldFor sid) (Worker.ofLookup (workerUrlOf sid)) with
          | Step.Nothing
          | Step.Keep -> return! loop listening
          | Step.Listen url -> return! loop (open' sid url)
          | Step.Move url ->
            stopHeld sid
            return! loop (open' sid url)
          | Step.Stop ->
            stopHeld sid
            return! loop (Map.remove sid listening)
        | Ended (sid, url) ->
          // Only forget the stream that actually ended. If the session has
          // already moved to another worker, that one stays.
          match Map.tryFind sid listening with
          | Some (current, cts) when current = url ->
            cts.Dispose()
            task {
              try
                do! Task.Delay(Timeouts.workerReloadRelayRetry, shutdown)
                inbox.Post(Ensure sid)
              with :? OperationCanceledException -> ()
            }
            |> ignore
            return! loop (Map.remove sid listening)
          | _ -> return! loop listening
      }
      loop Map.empty), shutdown)
  fun sid -> agent.Post(Ensure sid)
