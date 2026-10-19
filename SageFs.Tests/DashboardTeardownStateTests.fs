/// "Stopping session id:..." is the daemon's fact, not a one-off patch.
///
/// The card used to exist only in the stop POST's own SSE response. The dashboard snapshot did not know a
/// stop was running, so any stream push computed around the click put the ordinary card back, with live
/// buttons, while the session was still going down. The same shape as the 1 Hz clobber the idle-stability
/// work fixed: a patch that nothing server-side remembers loses to the next render.
///
/// So the handler records the teardown in a registry before it asks for the stop and clears it after, the
/// card builder folds it into every card, and the POST response and every stream push render the same card.
module SageFs.Tests.DashboardTeardownStateTests

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open Falco.Markup
open Microsoft.AspNetCore.Http
open SageFs
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Server.Dashboard
open SageFs.Server.TeardownRegistry
open SageFs.Tests.DashboardRenderGateTests

let sid (n: int) : WorkerProtocol.SessionId =
  WorkerProtocol.SessionId.validate (sprintf "%08x" (0x0b000000 + n))
  |> Result.defaultValue (WorkerProtocol.SessionId.newId ())

let raw (id: WorkerProtocol.SessionId) = WorkerProtocol.SessionId.value id

let infoOf (id: WorkerProtocol.SessionId) : WorkerProtocol.SessionInfo =
  SageFs.Tests.DashboardParsingTests.SidebarCards.info (raw id) WorkerProtocol.SessionStatus.Ready []

/// A response body the test can read while the handler is still writing to it, and wait on without sleeping:
/// `NextWrite` completes when the next write lands.
type CaptureStream() =
  inherit Stream()
  let bytes = ResizeArray<byte>()
  let gate = obj ()
  let mutable wake = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
  member _.Text : string = lock gate (fun () -> Encoding.UTF8.GetString(bytes.ToArray()))
  member _.NextWrite : Task = lock gate (fun () -> wake.Task :> Task)
  member _.Append(chunk: byte[]) =
    let woken =
      lock gate (fun () ->
        bytes.AddRange chunk
        let previous = wake
        wake <- TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        previous)
    woken.TrySetResult () |> ignore
  override _.CanRead = false
  override _.CanSeek = false
  override _.CanWrite = true
  override _.Length = raise (NotSupportedException())
  override _.Position
    with get () = raise (NotSupportedException())
    and set _ = raise (NotSupportedException())
  override _.Flush() = ()
  override _.Read(_: byte[], _: int, _: int) : int = raise (NotSupportedException())
  override _.Seek(_: int64, _: SeekOrigin) : int64 = raise (NotSupportedException())
  override _.SetLength(_: int64) = raise (NotSupportedException())
  override this.Write(buffer: byte[], offset: int, count: int) = this.Append(Array.sub buffer offset count)
  override this.Write(buffer: ReadOnlySpan<byte>) = this.Append(buffer.ToArray())
  override this.WriteAsync(buffer: ReadOnlyMemory<byte>, _: CancellationToken) =
    this.Append(buffer.ToArray())
    ValueTask()
  override _.FlushAsync(_: CancellationToken) = Task.CompletedTask

/// Waits until `predicate` holds for what the stream has written so far. The next write is taken BEFORE the
/// text is read, so a write that lands in between is never missed.
let awaitText (out: CaptureStream) (predicate: string -> bool) : Task<bool> =
  task {
    let clock = Diagnostics.Stopwatch.StartNew()
    let mutable holds = false
    while not holds && clock.Elapsed < TestTimeouts.patienceInProcess do
      let next = out.NextWrite
      match predicate out.Text with
      | true -> holds <- true
      | false ->
        let! _ = Task.WhenAny(next, Task.Delay TestTimeouts.patienceInProcess)
        ()
    return holds
  }

let contextWith (body: string) (out: CaptureStream) (aborted: CancellationToken) : DefaultHttpContext =
  let ctx = DefaultHttpContext()
  ctx.Request.ContentType <- "application/json"
  let bytes = Encoding.UTF8.GetBytes body
  ctx.Request.ContentLength <- Nullable(int64 bytes.Length)
  ctx.Request.Body <- new MemoryStream(bytes)
  ctx.Response.Body <- out
  ctx.RequestAborted <- aborted
  ctx

/// Every opening tag the text carries for this session's card, in the order written.
let cardTags (html: string) (id: WorkerProtocol.SessionId) : string list =
  Regex.Matches(html, sprintf "<[a-z]+[^>]*id=\"session-card-%s\"[^>]*>" (raw id))
  |> Seq.map (fun m -> m.Value)
  |> List.ofSeq

let isStoppingTag (tag: string) : bool = tag.Contains "session-stopping"

/// What a stop did, and what a tab on the stream saw while it ran.
type StopRun =
  { /// The stopped card's tags in the stream before the click.
    Before: string list
    /// The stopped card's tags in the stream frames written after the POST's "Stopping" patch, while the stop was held.
    WhileStopping: string list
    /// The stopped card's tags in the frames written after the stop came back and one more change was published.
    AfterStop: string list }

/// How the held stop ends.
[<RequireQualifiedAccess>]
type StopEnds =
  | Removed
  | Refused
  | Throws

/// A tab on the stream is viewing `viewed`; another tab clicks Stop on `stopped`. The stop is held open on a
/// signal. While it is held a change is published (an agent badge appears) so the stream renders a new snapshot.
let runStop (ends: StopEnds) : Task<StopRun> =
  task {
    let viewed, stopped = sid 1, sid 2
    let current = ref [ infoOf viewed; infoOf stopped ]
    let badges = ref ([]: AgentBadge list)
    let queries =
      { mkQueries (ref 0) [] with
          GetAllSessions = fun () -> Task.FromResult current.Value
          GetSessionAgentBadges = fun _ -> badges.Value }
    let changed = Event<SseEvent>()
    let notify () = changed.Trigger(SseEvent.ModelChanged(0, 0))
    let infra =
      { mkInfra () with
          StateChanged = changed.Publish
          TriggerStateChange = notify }
    let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let stop (id: WorkerProtocol.SessionId) : Task<Result<string, string>> =
      task {
        do! release.Task
        match ends with
        | StopEnds.Removed ->
          current.Value <- current.Value |> List.filter (fun s -> s.Id <> id)
          return Result.Ok "stopped"
        | StopEnds.Refused -> return Result.Error "the worker would not stop"
        | StopEnds.Throws -> return raise (InvalidOperationException "the stop blew up")
      }
    use aborted = new CancellationTokenSource()
    let streamOut = new CaptureStream()
    let streamTask = createStreamHandler queries infra "tab-1" (contextWith "{}" streamOut aborted.Token)
    let! sawInitial = awaitText streamOut (fun text -> not (cardTags text stopped).IsEmpty)
    sawInitial |> Expect.isTrue "the stream rendered the stopped session's card before the click"
    let before = cardTags streamOut.Text stopped
    let body =
      sprintf "{\"%s\":\"%s\",\"%s\":\"tab-1\"}" Signals.ViewingSessionId (raw viewed) Signals.ClientId
    let postOut = new CaptureStream()
    let postTask =
      task {
        try
          do! createSessionActionHandler queries infra stop (SessionActionKind.TearDown Teardown.Stopping) stopped (contextWith body postOut CancellationToken.None)
        with :? InvalidOperationException -> ()
      }
    let! sawStopping = awaitText postOut (fun text -> text.Contains(sprintf "Stopping session id:%s" (raw stopped)))
    sawStopping |> Expect.isTrue "the POST said Stopping"
    let mark = streamOut.Text.Length
    badges.Value <- [ { Name = "agent-a"; IntentLabel = "editing"; CssClass = "agent-badge"; DetailLabel = "" } ]
    notify ()
    let! sawPush = awaitText streamOut (fun text -> text.Length > mark && not (cardTags (text.Substring mark) stopped).IsEmpty)
    sawPush |> Expect.isTrue "a push that carries the sidebar reached the stream while the stop was held"
    let whileStopping = cardTags (streamOut.Text.Substring mark) stopped
    let markAfterHold = streamOut.Text.Length
    release.SetResult ()
    do! postTask
    badges.Value <- []
    notify ()
    // A push computed while the stop was held can still be on its way, and it rightly says Stopping. What
    // counts is where the stream ends up, so wait for the last card it wrote to be an ordinary one.
    let lastTagIsOrdinary (text: string) =
      match cardTags (text.Substring markAfterHold) stopped with
      | [] -> false
      | tags -> not (isStoppingTag (List.last tags))
    let! settled =
      match ends with
      | StopEnds.Removed -> Task.FromResult false
      | StopEnds.Refused | StopEnds.Throws -> awaitText streamOut lastTagIsOrdinary
    let afterStop =
      match settled with
      | true -> [ List.last (cardTags (streamOut.Text.Substring markAfterHold) stopped) ]
      | false -> cardTags (streamOut.Text.Substring markAfterHold) stopped
    aborted.Cancel()
    do! streamTask
    return { Before = before; WhileStopping = whileStopping; AfterStop = afterStop }
  }

[<Tests>]
let behaviourTests =
  testList "a teardown is the daemon's fact, so every render of the sidebar says it" [

    testTask "WHY — no stream push after the Stopping patch puts the normal card back, because the buttons came back live while the session was still going down" {
      let! run = runStop StopEnds.Removed
      run.Before |> List.exists isStoppingTag |> Expect.isFalse "before the click the card is the ordinary one"
      run.WhileStopping |> Expect.isNonEmpty "the stream wrote the stopped session's card while the stop was held"
      run.WhileStopping
      |> List.filter (isStoppingTag >> not)
      |> Expect.isEmpty "every card the stream wrote while the stop was held is the Stopping card"
    }

    testTask "WHY — a stop the worker refused puts the normal card back, so a failed stop never leaves a card stuck on Stopping" {
      let! run = runStop StopEnds.Refused
      run.AfterStop |> Expect.isNonEmpty "the stream still lists the session after a refused stop"
      run.AfterStop
      |> List.filter isStoppingTag
      |> Expect.isEmpty "the card is the ordinary one again once the stop came back"
    }

    testTask "WHY — a stop that throws puts the normal card back too, because the registry is cleared on the way out of any exit" {
      let! run = runStop StopEnds.Throws
      run.AfterStop |> Expect.isNonEmpty "the stream still lists the session after a stop that threw"
      run.AfterStop
      |> List.filter isStoppingTag
      |> Expect.isEmpty "the card is the ordinary one again"
    }
  ]

[<Tests>]
let registryTests =
  let a = sid 1
  testList "the teardown registry" [

    testTask "WHY — the teardown is registered before the change is announced and cleared before the clearing is announced, so a render triggered by either sees the truth" {
      let registry = Registry()
      let seen = ResizeArray<Map<WorkerProtocol.SessionId, Teardown>>()
      let! _ =
        during registry (fun () -> seen.Add(registry.Current())) a Teardown.Stopping (fun () ->
          task { return Result<string, string>.Ok "stopped" })
      List.ofSeq seen
      |> Expect.equal "set when announced first, empty when announced second" [ Map.ofList [ a, Teardown.Stopping ]; Map.empty ]
    }

    testTask "WHY — a result that is an error clears the registration, so the failed stop's card is not stuck" {
      let registry = Registry()
      let! result =
        during registry ignore a Teardown.Purging (fun () -> task { return Result<string, string>.Error "no" })
      result |> Expect.equal "the refusal is handed back untouched" (Result.Error "no")
      registry.Current() |> Expect.isEmpty "nothing stays registered"
    }

    testTask "WHY — an exception clears the registration and is not swallowed" {
      let registry = Registry()
      let announced = ref 0
      let! thrown =
        task {
          try
            let! _ = during registry (fun () -> announced.Value <- announced.Value + 1) a Teardown.Stopping (fun () -> task { return failwith "boom" })
            return false
          with Failure _ -> return true
        }
      thrown |> Expect.isTrue "the exception reached the caller"
      registry.Current() |> Expect.isEmpty "nothing stays registered"
      announced.Value |> Expect.equal "announced once when set and once when cleared" 2
    }

    testTask "WHY — a second stop of the same session does not clear the first's card when it finishes first, because the card stays until the last one is done" {
      let registry = Registry()
      let firstGate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let secondGate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let run (gate: TaskCompletionSource<unit>) =
        during registry ignore a Teardown.Stopping (fun () ->
          task {
            do! gate.Task
            return Result<string, string>.Ok "stopped"
          })
      let first = run firstGate
      let second = run secondGate
      firstGate.SetResult()
      let! _ = first
      registry.Current() |> Map.containsKey a |> Expect.isTrue "the second stop is still running, so the card is still the Stopping card"
      secondGate.SetResult()
      let! _ = second
      registry.Current() |> Expect.isEmpty "the last one out clears it"
    }
  ]

[<Tests>]
let cardBuilderTests =
  testList "the sidebar cards carry the teardown" [

    testProperty "WHY — every listed id in the registry renders the Stopping card and no other id does, and an id that is not listed never appears" <| fun (count: byte) (marked: uint16) (purgeMask: uint16) ->
      let n = 1 + int count % 8
      let ids = [ for i in 1 .. n -> sid i ]
      let ghost = sid 200
      let kindOf i = if (int purgeMask >>> i) &&& 1 = 1 then Teardown.Purging else Teardown.Stopping
      let registered =
        ids
        |> List.indexed
        |> List.filter (fun (i, _) -> (int marked >>> i) &&& 1 = 1)
        |> List.map (fun (i, id) -> id, kindOf i)
      let teardowns = Map.ofList ((ghost, Teardown.Purging) :: registered)
      let cards = buildSessionCardsFrom (mkQueries (ref 0) []) teardowns None (ids |> List.map infoOf)
      let html = renderSessionsForSession "" cards false |> renderNode
      let stoppingCardOf kind id = renderTeardownCard kind id |> renderNode
      let registeredIds = registered |> List.map fst
      let listedRight =
        ids
        |> List.forall (fun id ->
          match registered |> List.tryFind (fun (r, _) -> r = id) with
          | Some (_, kind) -> html.Contains(stoppingCardOf kind id)
          | None -> html.Contains(stoppingCardOf Teardown.Stopping id) |> not && html.Contains(stoppingCardOf Teardown.Purging id) |> not)
      let others = ids |> List.filter (fun id -> not (List.contains id registeredIds))
      let othersAreOrdinary =
        others |> List.forall (fun id -> cardTags html id |> List.forall (isStoppingTag >> not))
      listedRight && othersAreOrdinary && not (html.Contains(raw ghost))

    testCase "WHY — a card being purged says Purging, because purging is not stopping and the card should not claim it is" <| fun _ ->
      let a = sid 1
      let cards = buildSessionCardsFrom (mkQueries (ref 0) []) (Map.ofList [ a, Teardown.Purging ]) None [ infoOf a ]
      renderSessionsForSession "" cards false
      |> renderNode
      |> Expect.stringContains "the card says what is happening" (sprintf "Purging session id:%s" (raw a))

    testCase "WHY — the Stopping card has no buttons, so the click cannot be made twice from it" <| fun _ ->
      renderTeardownCard Teardown.Stopping (sid 1)
      |> renderNode
      |> fun html -> html.Contains "<button" |> Expect.isFalse "no buttons on a card that is going away"
  ]
