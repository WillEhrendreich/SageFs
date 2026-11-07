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
    /// Deliver one notification. Faulting or running past the budget means the connection is not reading.
    Send: CancellationToken -> Task
    /// Free everything the connection holds. Called once, after a failed send.
    Release: unit -> Task }

/// Sending a notification to every connected client without letting one that stopped reading cost the daemon
/// memory. The MCP SDK writes each notification into a per-connection buffer and clears it only after the whole
/// write has reached the socket; a write that throws or is cancelled leaves the bytes there and every later
/// notification stacks on top. So the first failed send ends the connection: it is released, which disposes
/// the SDK's writer and its buffer, and the client reconnects when it next reads.
[<RequireQualifiedAccess>]
module McpFanout =

  /// Send to every target in parallel, each within `budget`. A target whose send throws or runs out of budget is
  /// released (within `release`, whatever it takes) and its id is returned. Nothing a target does can throw out of here.
  let broadcast (budget: TimeSpan) (release: TimeSpan) (targets: FanoutTarget[]) : Task<string[]> =
    task {
      let! results =
        targets
        |> Array.map (fun target ->
          task {
            use cts = new CancellationTokenSource(budget)
            let! outcome = EchoGuard.observe (fun () -> target.Send cts.Token)
            match outcome with
            | None -> return None
            | Some ex ->
              // One sink for every connection: a counter per connection would grow with every connection ever seen.
              EchoGuard.record EchoSink.NotificationDelivery ex
              return Some target
          })
        |> Task.WhenAll
      let dropped = results |> Array.choose id
      do!
        dropped
        |> Array.map (fun target ->
          EchoGuard.attempt EchoSink.ConnectionRelease (fun () -> target.Release().WaitAsync(release)))
        |> Task.WhenAll
      return dropped |> Array.map (fun target -> target.Id)
    }
