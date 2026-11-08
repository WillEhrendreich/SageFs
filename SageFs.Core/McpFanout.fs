namespace SageFs

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

/// The decorations on an MCP tool call, and the notification path, whose failure is counted instead of
/// raised. A closed set: a sink named by a string at each call site would let two spellings count apart.
[<RequireQualifiedAccess>]
type EchoSink =
  /// Invocation, success and failure counters, and the audit line.
  | ToolCounters
  /// The friction record appended after a call.
  | Friction
  /// The trace span closed after a call.
  | Span
  /// Delivering one notification to a connected client.
  | NotificationDelivery
  /// Disposing a connection whose notification could not be delivered.
  | ConnectionRelease
  /// A subscriber of the daemon's model-change hook threw while a delivery ran on the thread pool.
  | ModelChangeDelivery

[<RequireQualifiedAccess>]
module EchoSink =
  /// The one place a sink becomes text (log lines, counters).
  let name =
    function
    | EchoSink.ToolCounters -> "tool-counters"
    | EchoSink.Friction -> "friction"
    | EchoSink.Span -> "span"
    | EchoSink.NotificationDelivery -> "notification-delivery"
    | EchoSink.ConnectionRelease -> "connection-release"
    | EchoSink.ModelChangeDelivery -> "model-change-delivery"

/// A decoration on a tool call (the friction record, the audit line, the event banner) is best effort: when it
/// fails, the call it decorates still returns its own result. `attempt` runs the decoration and swallows
/// whatever it throws, `OutOfMemoryException` included, because a daemon that has run out of heap would
/// otherwise fail every tool call twice, once for the decoration and once for the call. The failure is counted per
/// (sink, exception type) and written to the log once per pair, never once per call.
[<RequireQualifiedAccess>]
module EchoGuard =

  let private counts = ConcurrentDictionary<struct (EchoSink * string), int64 ref>()

  /// Count a failure. True when it is the first of its (sink, exception type): the caller logs only then.
  let note (sink: EchoSink) (ex: exn) : bool =
    let cell = counts.GetOrAdd(struct (sink, ex.GetType().Name), fun _ -> ref 0L)
    Interlocked.Increment(&cell.contents) = 1L

  /// How many times `sink` has failed, over every exception type.
  let failureCount (sink: EchoSink) : int64 =
    counts
    |> Seq.filter (fun kvp ->
      let struct (counted, _) = kvp.Key
      counted = sink)
    |> Seq.sumBy (fun kvp -> Volatile.Read(&kvp.Value.contents))

  /// Every sink with its failure count, for a status surface. Sinks that never failed are left out.
  let snapshot () : (EchoSink * int64) list =
    counts
    |> Seq.map (fun kvp ->
      let struct (sink, _) = kvp.Key
      sink, Volatile.Read(&kvp.Value.contents))
    |> Seq.groupBy fst
    |> Seq.map (fun (sink, rows) -> sink, rows |> Seq.sumBy snd)
    |> Seq.toList

  /// Clear the counters (tests).
  let reset () : unit = counts.Clear()

  /// Count a failure and log it when it is the first of its kind. Never throws.
  let record (sink: EchoSink) (ex: exn) : unit =
    match note sink ex with
    | true ->
      SageFs.Utils.Log.warn
        "[echo] %s failed (%s: %s); later failures of this kind are counted, not logged"
        (EchoSink.name sink) (ex.GetType().Name) ex.Message
    | false -> ()

  /// Run `work` and say how it ended: `None` when it completed, `Some` with what it threw (a synchronous throw, a faulted
  /// task, or a cancelled one). Never throws itself. The catch is `Guarded.call`'s, so this module adds no handler of its own.
  let observe (work: unit -> Task) : Task<exn option> =
    let started = ref Task.CompletedTask
    let thrown = ref None
    Guarded.call (fun ex -> thrown.Value <- Some ex) (fun () -> started.Value <- work ())
    match thrown.Value with
    | Some ex -> Task.FromResult(Some ex)
    | None ->
      started.Value.ContinueWith(
        (fun (finished: Task) ->
          match finished.IsFaulted, finished.IsCanceled with
          | true, _ -> Some(finished.Exception.GetBaseException())
          | false, true -> Some(OperationCanceledException() :> exn)
          | false, false -> None),
        TaskContinuationOptions.ExecuteSynchronously)

  /// Run `work`; whatever it throws is counted and logged once, and then dropped.
  let attempt (sink: EchoSink) (work: unit -> Task) : Task =
    observe work
    |> fun outcome -> outcome.ContinueWith((fun (finished: Task<exn option>) -> finished.Result |> Option.iter (record sink)), TaskContinuationOptions.ExecuteSynchronously)

  /// `attempt` for a synchronous decoration.
  let attemptSync (sink: EchoSink) (work: unit -> unit) : unit =
    Guarded.call (record sink) work

/// One connected MCP client as the notification fan-out sees it.
type FanoutTarget =
  { Id: string
    /// The size of the notification about to be sent. The SDK appends it to the connection's write buffer and clears it only
    /// once the whole write has reached the socket, so this is what the connection holds while the client has not read it.
    Bytes: int64
    /// Deliver one notification. Faulting means the connection is not usable. Running long is not a fault: the token is
    /// cancelled only when the connection is dropped, so a write that is still going is never cut off from outside.
    Send: CancellationToken -> Task
    /// Free everything the connection holds. Called once, after a failed send.
    Release: unit -> Task }

/// What the fan-out judges a connection by. A write that is still going after `Look` is not a fault: a starved pool or a
/// busy client takes longer than any fixed deadline, and cutting the write off leaves its bytes in the SDK's buffer and drops
/// a client that was reading. What costs the daemon memory is a connection that stopped reading, which shows as bytes sent
/// and never finished, so a connection is dropped when more than `MaxBufferedBytes` of notifications sit unfinished on it.
type FanoutPolicy =
  { /// How long a broadcast waits for one send before it judges the connection by its backlog. Waiting longer changes
    /// nothing about who is dropped, only how soon `broadcast` returns for a connection that is still going.
    Look: TimeSpan
    MaxBufferedBytes: int64 }

module FanoutPolicy =
  /// The most a connection may hold unfinished before it is dropped: a few hundred thousand bytes, which is thousands of
  /// notifications of the size the daemon sends and, over the 311 connections of the 2026-11-07 dump, tens of megabytes
  /// where the dump held 25 GB.
  [<Literal>]
  let StandardMaxBufferedBytes = 262_144L

  let standard (look: TimeSpan) : FanoutPolicy = { Look = look; MaxBufferedBytes = StandardMaxBufferedBytes }

[<RequireQualifiedAccess>]
type DropReason =
  /// The send threw.
  | SendFaulted
  /// More bytes sit unfinished on the connection than the policy allows.
  | BacklogTooLarge of unfinishedBytes: int64

module FanoutBacklog =
  /// Pure: whether `unfinishedBytes` on one connection are past what the policy allows.
  let decide (policy: FanoutPolicy) (unfinishedBytes: int64) : DropReason option =
    match unfinishedBytes > policy.MaxBufferedBytes with
    | true -> Some(DropReason.BacklogTooLarge unfinishedBytes)
    | false -> None

/// One send that has not finished: its size, and the source that cancels it when its connection is dropped.
type PendingSend = { Bytes: int64; Source: CancellationTokenSource }

/// The sends each connection has not finished, and the connections whose send failed after the broadcast had stopped
/// waiting. A send is recorded when it starts and removed when it ends, whoever is still waiting for it.
type FanoutLedger() =
  let gate = obj ()
  let unfinished = System.Collections.Generic.Dictionary<string, ResizeArray<PendingSend>>()
  let faulted = System.Collections.Generic.HashSet<string>()

  member _.Begin(id: string, bytes: int64, source: CancellationTokenSource) : PendingSend =
    lock gate (fun () ->
      let send = { Bytes = bytes; Source = source }
      match unfinished.TryGetValue id with
      | true, sends -> sends.Add send
      | false, _ -> unfinished.[id] <- ResizeArray [ send ]
      send)

  member _.Finish(id: string, send: PendingSend) : unit =
    lock gate (fun () ->
      match unfinished.TryGetValue id with
      | true, sends ->
        sends.Remove send |> ignore
        match sends.Count with
        | 0 -> unfinished.Remove id |> ignore
        | _ -> ()
      | false, _ -> ())
    send.Source.Dispose()

  /// The bytes on `id` that its client has not finished reading.
  member _.Unfinished(id: string) : int64 =
    lock gate (fun () ->
      match unfinished.TryGetValue id with
      | true, sends -> sends |> Seq.sumBy (fun send -> send.Bytes)
      | false, _ -> 0L)

  /// Cancel every send still going on `id`, so the lock a stuck write holds is released before the connection is disposed.
  member _.CancelAll(id: string) : unit =
    let sends = lock gate (fun () -> match unfinished.TryGetValue id with | true, sends -> List.ofSeq sends | false, _ -> [])
    for send in sends do
      try
        send.Source.Cancel()
      with :? ObjectDisposedException -> ()

  /// A send on `id` failed after the broadcast that started it had gone on: the next broadcast drops the connection.
  member _.MarkFaulted(id: string) : unit = lock gate (fun () -> faulted.Add id |> ignore)

  member _.HasFaulted(id: string) : bool = lock gate (fun () -> faulted.Contains id)

  /// The connection is gone: forget what is held for it.
  member _.Forget(id: string) : unit =
    lock gate (fun () ->
      unfinished.Remove id |> ignore
      faulted.Remove id |> ignore)

/// Sending a notification to every connected client without letting one that stopped reading cost the daemon
/// memory. The MCP SDK writes each notification into a per-connection buffer and clears it only after the whole
/// write has reached the socket; a write that throws or is cancelled leaves the bytes there and every later
/// notification stacks on top. So a failed send ends the connection, and so does a backlog past the policy: it is
/// released, which disposes the SDK's writer and its buffer, and the client reconnects when it next reads.
///
/// A send that is merely slow is left alone. The old rule cancelled every send at a fixed half second, which on a
/// starved thread pool dropped clients that were reading, and the cancelled write left its bytes behind anyway.
[<RequireQualifiedAccess>]
module McpFanout =

  /// Send to every target in parallel. A target whose send throws, or whose unfinished bytes pass the policy, is released
  /// (within `release`, whatever it takes) and its id is returned. A send still going after `policy.Look` and under the
  /// ceiling is left to finish, and its failure, if it comes, drops the connection at the next broadcast. Nothing a target
  /// does can throw out of here.
  let broadcast (policy: FanoutPolicy) (ledger: FanoutLedger) (release: TimeSpan) (targets: FanoutTarget[]) : Task<string[]> =
    task {
      let! results =
        targets
        |> Array.map (fun target ->
          task {
            match ledger.HasFaulted target.Id with
            | true -> return Some target
            | false ->
              let source = new CancellationTokenSource()
              let send = ledger.Begin(target.Id, target.Bytes, source)
              let observed = EchoGuard.observe (fun () -> target.Send source.Token)
              let ending =
                observed.ContinueWith(
                  (fun (finished: Task<exn option>) ->
                    ledger.Finish(target.Id, send)
                    finished.Result),
                  TaskContinuationOptions.ExecuteSynchronously
                )
              let! first = Task.WhenAny(ending :> Task, Task.Delay policy.Look)
              match Object.ReferenceEquals(first, ending) with
              | true ->
                match ending.Result with
                | None -> return None
                | Some ex ->
                  // One sink for every connection: a counter per connection would grow with every connection ever seen.
                  EchoGuard.record EchoSink.NotificationDelivery ex
                  return Some target
              | false ->
                match FanoutBacklog.decide policy (ledger.Unfinished target.Id) with
                | Some _ ->
                  EchoGuard.record EchoSink.NotificationDelivery (OperationCanceledException "the connection's unfinished notifications passed the backlog ceiling")
                  return Some target
                | None ->
                  // Still going and under the ceiling: leave it. If it fails later, the next broadcast drops the connection.
                  ending.ContinueWith(
                    (fun (late: Task<exn option>) ->
                      match late.Result with
                      | Some ex ->
                        EchoGuard.record EchoSink.NotificationDelivery ex
                        ledger.MarkFaulted target.Id
                      | None -> ()),
                    TaskContinuationOptions.ExecuteSynchronously
                  )
                  |> ignore
                  return None
          })
        |> Task.WhenAll
      let dropped = results |> Array.choose id
      do!
        dropped
        |> Array.map (fun target ->
          ledger.CancelAll target.Id
          ledger.Forget target.Id
          EchoGuard.attempt EchoSink.ConnectionRelease (fun () -> target.Release().WaitAsync(release)))
        |> Task.WhenAll
      return dropped |> Array.map (fun target -> target.Id)
    }
