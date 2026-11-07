namespace SageFs.Tests

open System.Threading
open System.Threading.Tasks

/// Async work a test starts and then waits on, or proves still pending, starts without a pool thread.
///
/// `Async.StartAsTask` queues the first step of the computation, and a queued item waits until a pool thread is
/// free. The test process measured it (`SAGEFS_POOL_SAMPLE`, see `TestInfrastructure.PoolSampler`): CPU-bound
/// simulation cases and blocking cases hold the pool's threads at times, a marker item then waited more than a
/// second to start, and the pool adds a thread about every half second. A deadline put on work that is still queued
/// times the queue, and a case that waits a window to prove "nothing happened" passes because the work had not begun.
/// `asTask` runs the first step on the calling thread, which is running already, so the clock a test starts after
/// the call times the work.
///
/// Blocking work (a synchronous read, a loop that never yields) belongs on `TestInfrastructure.DedicatedThread.run`
/// instead: its first step would hold the caller. `PoolDeadlineRatchetTests` counts the spawns that still carry a
/// deadline.
module Started =
  /// Starts `work` on the calling thread and answers with its task. Its first step has run, up to its first real
  /// wait, when this returns.
  let asTask (work: Async<'T>) : Task<'T> =
    Async.StartImmediateAsTask work

  /// `asTask` with a token that cancels the task.
  let asTaskWith (token: CancellationToken) (work: Async<'T>) : Task<'T> =
    Async.StartImmediateAsTask(work, cancellationToken = token)

  /// Starts `work` and does not wait for it. Its first step runs on the calling thread.
  let andForget (work: Async<unit>) : unit =
    Async.StartImmediate work

/// A pool with every thread blocked, for the cases that prove work does not need one.
module StarvedPool =
  /// How many blocked items wait in the queue behind the ones that hold the pool's threads: while any of them is
  /// queued, the pool has to inject a thread per item ahead of a newcomer, about half a second each.
  let private behind = 12

  /// Runs `body` while every pool thread is blocked and more blocking work waits behind them, then lets them all go.
  /// `body` runs on the calling thread, which is running already. Work it queues to the pool cannot start until the
  /// blockers are released, so work that has run by the time `body` looks did not need the pool.
  let whileBlocked (body: unit -> Task<'T>) : Task<'T> =
    task {
      use release = new ManualResetEventSlim(false)
      let blockers = ResizeArray<Task>()
      let minWorkers, _ = ThreadPool.GetMinThreads()
      let saturating = max ThreadPool.ThreadCount minWorkers + behind
      for _ in 1 .. saturating do
        let finished = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        ThreadPool.UnsafeQueueUserWorkItem((fun _ -> release.WaitHandle.WaitOne() |> ignore; finished.SetResult()), null, false) |> ignore
        blockers.Add finished.Task
      let! ran = body () |> Async.AwaitTask |> Async.Catch
      release.Set()
      do! Task.WhenAll blockers
      match ran with
      | Choice1Of2 value -> return value
      | Choice2Of2 e ->
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw()
        return Unchecked.defaultof<'T>
    }
