/// `WorkerPostReady.watchUntilReady` is started with `Async.Start` and nothing awaits it. If anything inside it throws, the
/// loop dies and the session is never told Ready, Faulted or timed out: it waits for an answer that cannot come. The guard
/// turns an unexpected exception into the timed-out outcome, with the exception's text, so the session always gets one.
module SageFs.Tests.WorkerPostReadyGuardTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

/// A worker that answers every status request with Ready.
let private readyProxy : SessionProxy =
  fun msg ->
    async {
      match msg with
      | WorkerMessage.GetStatus rid ->
        return
          WorkerResponse.StatusResult(
            rid,
            { Status = SessionStatus.Ready
              StatusMessage = None
              EvalCount = 0
              AvgDurationMs = FixtureDurations.notRunMsInt64
              MinDurationMs = FixtureDurations.notRunMsInt64
              MaxDurationMs = FixtureDurations.notRunMsInt64
              Projects = []
              CoreVersion = "0.0.0-test" })
      | other -> return failwithf "the watchdog asked for something other than status: %A" other
    }

[<Tests>]
let tests =
  testList "WorkerPostReady.watchUntilReady: the watchdog never dies silently" [
    testTask "WHY — watchUntilReady — an OnReady that throws still ends in an outcome, because the session is waiting for one" {
      let timedOut = TaskCompletionSource<string>()
      let outcomes : WorkerPostReady.WatchdogOutcomes =
        { OnReady = fun _ -> failwith "the ready handler blew up"
          OnFaulted = fun reason -> timedOut.TrySetException(Exception(sprintf "faulted instead: %s" reason)) |> ignore
          OnTimedOut = fun reason -> timedOut.TrySetResult reason |> ignore }
      let cts = new CancellationTokenSource()
      // StartAsTask, not Async.Start: an exception that escapes the loop faults this task instead of reaching the
      // thread pool's unhandled-exception path and taking the whole test process with it.
      let _watch = Async.StartAsTask(WorkerPostReady.watchUntilReady "guard-test" readyProxy cts.Token outcomes, cancellationToken = cts.Token)
      let! finished = Task.WhenAny(timedOut.Task, Task.Delay TestTimeouts.patience)
      cts.Cancel()
      match obj.ReferenceEquals(finished, timedOut.Task) with
      | false -> failtest "the session was left unanswered: no outcome after OnReady threw"
      | true ->
        timedOut.Task.Result |> Expect.stringContains "the exception's text reaches the session" "the ready handler blew up"
    }

    testTask "WHY — watchUntilReady — a clean Ready is reported as ready and not as a timeout" {
      let ready = TaskCompletionSource<unit>()
      let timedOut = TaskCompletionSource<string>()
      let outcomes : WorkerPostReady.WatchdogOutcomes =
        { OnReady = fun _ -> ready.TrySetResult() |> ignore
          OnFaulted = ignore
          OnTimedOut = fun reason -> timedOut.TrySetResult reason |> ignore }
      let cts = new CancellationTokenSource()
      let _watch = Async.StartAsTask(WorkerPostReady.watchUntilReady "guard-test" readyProxy cts.Token outcomes, cancellationToken = cts.Token)
      let! finished = Task.WhenAny(ready.Task, timedOut.Task, Task.Delay TestTimeouts.patience)
      cts.Cancel()
      obj.ReferenceEquals(finished, ready.Task) |> Expect.isTrue "Ready reached OnReady and nothing else"
    }
  ]
