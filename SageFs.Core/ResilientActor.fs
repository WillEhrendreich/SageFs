namespace SageFs

open System
open System.Threading.Tasks
open SageFs.Utils

/// Resilient wrappers for MailboxProcessor loops and fire-and-forget tasks.
///
/// Actor loops (MailboxProcessor) can silently die if an unhandled exception
/// escapes the message-processing function. ResilientActor.wrapLoop catches
/// non-cancellation exceptions, logs them, increments an OTel counter, and
/// returns the previous state so the loop continues.
///
/// Fire-and-forget tasks (Task.Run |> ignore) can lose exceptions to the
/// unobserved task exception handler. SafeFireAndForget.startTask wraps
/// the work with structured logging and metrics.
[<RequireQualifiedAccess>]
module ResilientActor =

  /// Turns a reply function into one that runs at most once. A message that carries a reply and can also fault
  /// is answered by its handler on the way through and by `onFault` when a step throws; this keeps the second
  /// answer from reaching a caller that already has the first (a `Post` callback is not an `AsyncReplyChannel`,
  /// which tolerates a second reply, so it would run twice).
  let replyOnce (reply: 'Reply -> unit) : 'Reply -> unit =
    // `TrySetResult` succeeds for exactly one caller, which is the whole of the once-only rule.
    let answered = TaskCompletionSource<unit>()
    fun value ->
      match answered.TrySetResult() with
      | true -> reply value
      | false -> ()

  /// Wraps a MailboxProcessor loop body so that exceptions in message
  /// processing are caught, logged, and the loop continues with previous state.
  /// OperationCanceledException propagates (actor should stop on cancellation).
  ///
  /// `onFault` is the contract for a message a caller waits on: when the handler throws, `onFault msg ex`
  /// runs before the loop moves on, and it must answer the caller of `msg` (with a typed failure), because
  /// the handler that would have answered is gone. A message with no reply channel has nothing to answer
  /// and passes a no-op. `onFault` throwing is logged and swallowed: the loop outlives it.
  let inline wrapLoopAnswering
    (logger: ILogger)
    (actorName: string)
    (onFault: 'Msg -> exn -> unit)
    (processMessage: 'State -> 'Msg -> Async<'State>)
    : 'State -> 'Msg -> Async<'State> =
    fun state msg ->
      async {
        try
          return! processMessage state msg
        with
        | :? OperationCanceledException -> return! raise (OperationCanceledException())
        | ex ->
          logger.LogError(
            sprintf "[%s] Unhandled exception processing message, continuing with previous state: %s"
              actorName (string ex))
          Instrumentation.actorErrors.Add(
            1L,
            Collections.Generic.KeyValuePair("actor.name", actorName :> obj))
          let! answered = async { onFault msg ex } |> Async.Catch
          match answered with
          | Choice1Of2 () -> ()
          | Choice2Of2 answerEx ->
            logger.LogError(
              sprintf "[%s] The fault handler for the failed message threw as well: %s" actorName answerEx.Message)
          return state
      }

  /// `wrapLoopAnswering` for an actor whose messages carry no reply channel, so a throw leaves no caller
  /// waiting. An actor with a reply-carrying message uses `wrapLoopAnswering`: this one eats the reply.
  /// `ActorReplyContractTests` lists the call sites that still use it.
  let inline wrapLoop
    (logger: ILogger)
    (actorName: string)
    (processMessage: 'State -> 'Msg -> Async<'State>)
    : 'State -> 'Msg -> Async<'State> =
    wrapLoopAnswering logger actorName (fun _ _ -> ()) processMessage

  /// The caller's side of the same contract: post a message that carries a reply and wait at most `ceiling` for it.
  /// `None` means the actor did not answer inside the ceiling (stuck in a step that never returns), which the
  /// caller turns into its own typed failure. Replaces an untimed `PostAndAsyncReply`, which waits forever.
  let awaitReply
    (mailbox: MailboxProcessor<'Msg>)
    (ceiling: TimeSpan)
    (build: AsyncReplyChannel<'Reply> -> 'Msg)
    : Async<'Reply option> =
    mailbox.PostAndTryAsyncReply(build, int ceiling.TotalMilliseconds)

  /// A flush: completes when the actor answers, and faults with a `TimeoutException` that names the actor when it does not
  /// answer inside `ceiling`. Flush is the drain call, so an actor that never answers has no useful value to fall back to.
  let awaitFlush
    (actorName: string)
    (mailbox: MailboxProcessor<'Msg>)
    (ceiling: TimeSpan)
    (build: AsyncReplyChannel<unit> -> 'Msg)
    : Task =
    task {
      match! awaitReply mailbox ceiling build with
      | Some () -> ()
      | None -> do! Task.FromException(TimeoutException(sprintf "%s did not answer a flush within %A" actorName ceiling))
    }
    :> Task

[<RequireQualifiedAccess>]
module SafeFireAndForget =

  /// Wraps a fire-and-forget task so that unhandled exceptions are logged and
  /// metricked, not lost to TaskScheduler.UnobservedTaskException.
  /// OperationCanceledException is silently absorbed (expected during shutdown).
  let startTask (logger: ILogger) (name: string) (work: unit -> Task) =
    Task.Run(fun () ->
      task {
        try
          do! work ()
        with
        | :? OperationCanceledException -> ()
        | ex ->
          logger.LogWarning(
            sprintf "[%s] Fire-and-forget task failed: %s" name ex.Message)
          Instrumentation.fireAndForgetErrors.Add(
            1L,
            Collections.Generic.KeyValuePair("task.name", name :> obj))
      } :> Task
    ) |> ignore
