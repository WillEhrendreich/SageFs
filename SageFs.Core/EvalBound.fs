namespace SageFs

open System
open System.Diagnostics
open System.Threading.Tasks

/// How long one eval call may take, and how the figure was arrived at, so the answer to a caller can say whether the
/// number it read is the one it asked for.
[<RequireQualifiedAccess>]
type EvalLimit =
  /// The caller named no `timeout_seconds`: the product's default.
  | Default of TimeSpan
  /// The caller's own figure.
  | Asked of TimeSpan
  /// The caller asked for more than a worker will wait for, so this much applies.
  | Capped of askedSeconds: int * cap: TimeSpan

[<RequireQualifiedAccess>]
module EvalLimit =

  /// `requestedSeconds` as an MCP caller sends it: zero (or less) means "not given".
  let ofRequest (defaultLimit: TimeSpan) (cap: TimeSpan) (requestedSeconds: int) : EvalLimit =
    match requestedSeconds with
    | seconds when seconds <= 0 -> EvalLimit.Default defaultLimit
    | seconds when TimeSpan.FromSeconds(float seconds) > cap -> EvalLimit.Capped(seconds, cap)
    | seconds -> EvalLimit.Asked(TimeSpan.FromSeconds(float seconds))

  let span (limit: EvalLimit) : TimeSpan =
    match limit with
    | EvalLimit.Default span
    | EvalLimit.Asked span
    | EvalLimit.Capped(_, span) -> span

/// What a caller is told when its eval ran past its limit. The text names the figure that applied, what became of the eval,
/// what the session is like now, and what to do.
[<RequireQualifiedAccess>]
module EvalBoundMessage =

  let private seconds (span: TimeSpan) = sprintf "%.0f" span.TotalSeconds

  let private longer = sprintf "Pass a larger timeout_seconds (up to %s) for work that should take longer." (seconds Timeouts.evalCallCap)

  /// The eval was cancelled at the limit and has answered. The answer follows this text.
  let cancelledAtLimit (limit: TimeSpan) : string =
    sprintf
      "The call reached timeout_seconds (%s s) and its eval was cancelled: nothing it defined was kept and the definitions from before it are intact, unless the text below says the host was stopped. If the eval was queued behind another one, it never ran and the other eval was not touched. %s"
      (seconds limit)
      longer

  /// The eval was cancelled at the limit and still had not answered `waited` later.
  let didNotStop (limit: TimeSpan) (waited: TimeSpan) : string =
    sprintf
      "The eval ran past timeout_seconds (%s s). It was asked to stop and had not answered %s s later, so the session may still be running it. Call cancel_eval again, or hard_reset_fsi_session to start a fresh host (that loses the session's definitions). %s"
      (seconds limit)
      (seconds waited)
      longer

  /// An earlier statement of the same call used the whole limit.
  let budgetSpent (limit: TimeSpan) : string =
    sprintf "Not run: an earlier statement of this call used up its timeout_seconds (%s s). Send this statement in a call of its own." (seconds limit)

/// Where a call that waits for an eval is: the eval is still running, or it was asked to stop (at `elapsed` since the
/// call began) and the call is waiting for it to say so.
[<RequireQualifiedAccess>]
type EvalWaitStage =
  | Running
  | CancelAsked of at: TimeSpan

/// What a waiting call does next.
[<RequireQualifiedAccess>]
type EvalWaitStep =
  /// Look again after this long.
  | KeepWaiting of TimeSpan
  /// The limit is up and the eval still runs: ask it to stop.
  | AskCancel
  /// The eval was asked to stop and did not answer in time: answer the caller without it.
  | GiveUp

/// The decision behind every eval call's wait, with time as an argument so a simulation can run it and a real wait can
/// use the same function. A caller always gets an answer by `limit + answerWait`, however the eval behaves.
[<RequireQualifiedAccess>]
module EvalWait =

  let next (limit: TimeSpan) (answerWait: TimeSpan) (elapsed: TimeSpan) (stage: EvalWaitStage) : EvalWaitStep =
    match stage with
    | EvalWaitStage.Running ->
      match elapsed >= limit with
      | true -> EvalWaitStep.AskCancel
      | false -> EvalWaitStep.KeepWaiting(limit - elapsed)
    | EvalWaitStage.CancelAsked at ->
      match elapsed - at >= answerWait with
      | true -> EvalWaitStep.GiveUp
      | false -> EvalWaitStep.KeepWaiting(at + answerWait - elapsed)

  /// How a waiting call ended.
  [<RequireQualifiedAccess>]
  type Waited<'answer> =
    /// The eval answered within its limit.
    | Finished of 'answer
    /// The eval was cancelled at the limit and answered afterwards (with whatever the cancel produced).
    | FinishedAfterCancel of 'answer
    /// The eval was cancelled at the limit and still did not answer.
    | GaveUp
    /// The limit was already spent, so the eval was never started.
    | NotStarted

  /// What is done with a cancel that failed: it is logged with its whole exception, and the caller's wait goes on.
  let logCancelFailure (failure: exn) : unit =
    SageFs.Utils.Log.warn "[EvalWait] the cancel of a timed-out eval failed: %s" (failure.ToString())

  let private startCancel (observe: exn -> unit) (cancel: unit -> Task) : unit =
    // On the pool, so a thunk that throws before it returns a task becomes a faulted task; either way the failure is handed to
    // `observe` and not lost.
    let started = Task.Run(System.Func<Task>(fun () -> cancel ()))
    started.ContinueWith(
      (fun (finished: Task) ->
        match finished.IsFaulted with
        | true -> observe (finished.Exception.GetBaseException())
        | false -> ()),
      TaskContinuationOptions.ExecuteSynchronously
    )
    |> ignore

  let private waitFor
    (limit: TimeSpan)
    (answerWait: TimeSpan)
    (observe: exn -> unit)
    (call: Task<'answer>)
    (cancel: unit -> Task)
    : Task<Waited<'answer>> =
    task {
      let clock = Stopwatch.StartNew()
      let mutable stage = EvalWaitStage.Running
      let mutable outcome = ValueNone
      while outcome.IsNone do
        match call.IsCompleted, next limit answerWait clock.Elapsed stage with
        | true, _ ->
          let! answer = call
          outcome <-
            match stage with
            | EvalWaitStage.Running -> ValueSome(Waited.Finished answer)
            | EvalWaitStage.CancelAsked _ -> ValueSome(Waited.FinishedAfterCancel answer)
        | false, EvalWaitStep.KeepWaiting remaining ->
          let! _ = Task.WhenAny(call :> Task, Task.Delay remaining)
          ()
        | false, EvalWaitStep.AskCancel ->
          stage <- EvalWaitStage.CancelAsked clock.Elapsed
          // A cancel that itself fails or hangs must not cost the caller its answer: the wait below is already bounded.
          startCancel observe cancel
        | false, EvalWaitStep.GiveUp -> outcome <- ValueSome Waited.GaveUp
      return outcome.Value
    }

  /// Start `start` and wait for it under `limit`. At the limit `cancel` is invoked once, and the wait goes on for
  /// `answerWait` more. With no limit left `start` is never called.
  let run
    (limit: TimeSpan)
    (answerWait: TimeSpan)
    (observe: exn -> unit)
    (start: unit -> Task<'answer>)
    (cancel: unit -> Task)
    : Task<Waited<'answer>> =
    match limit > TimeSpan.Zero with
    | false -> Task.FromResult Waited.NotStarted
    | true -> waitFor limit answerWait observe (start ()) cancel

  /// `run`, settled into what an eval caller reads: the answer (and the note to put before it when it came after a
  /// cancel at the limit), or the refusal text when there was no answer, built with `refuse`.
  let reply
    (asked: TimeSpan)
    (left: TimeSpan)
    (answerWait: TimeSpan)
    (refuse: string -> 'refusal)
    (start: string -> Task<Result<'answer, 'refusal>>)
    (cancel: string -> Task)
    : Task<Result<'answer, 'refusal> * string> =
    task {
      // One request id for the call, handed to both: the cancel names the request it is for and no other.
      let requestId = Guid.NewGuid().ToString "N"
      let! waited = run left answerWait logCancelFailure (fun () -> start requestId) (fun () -> cancel requestId)
      return
        match waited with
        | Waited.Finished answer -> answer, ""
        | Waited.FinishedAfterCancel answer -> answer, EvalBoundMessage.cancelledAtLimit asked + "\n"
        | Waited.GaveUp -> Result.Error(refuse (EvalBoundMessage.didNotStop asked answerWait)), ""
        | Waited.NotStarted -> Result.Error(refuse (EvalBoundMessage.budgetSpent asked)), ""
    }
