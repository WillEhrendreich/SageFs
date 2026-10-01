namespace SageFs

open System
open System.Threading
open System.Threading.Tasks

// ── How the live-testing pipeline waits for a rebuilt session ─────────
//
// After a rebuild restart the pipeline needs the session Ready with a
// streaming proxy before it runs tests. It used to poll ListSessions and
// GetStreamingTestProxy every 50 ms, then every 250 ms.
//
// The session manager already knows the moment that is true. A session is
// marked Ready only after WorkerReady has installed its worker URL, and the
// manager publishes its state after every step, so by the time AwaitReady
// answers Ok the URL that GetStreamingTestProxy reads is already published.
// One await is enough.
//
// This module is the await: it races the manager's answer against a deadline
// and a cancellation token. The deadline is the only timer. Generic in the
// error so it loads without the rest of the app.

module RebuildReadyWait =

  /// How the wait ended.
  [<RequireQualifiedAccess>]
  type Outcome<'e> =
    /// The manager answered Ok: the session is Ready.
    | Ready
    /// The manager answered with an error: the session faulted, stopped or went away.
    | Failed of 'e
    /// Nothing answered before the deadline.
    | DeadlineReached
    /// The rebuild was cancelled or superseded while waiting.
    | Cancelled

  /// Wait for `wait` to answer, for at most `deadline`, giving up early if `ct`
  /// is cancelled. A wait that is given up on is abandoned, not cancelled: the
  /// manager keeps the reply channel until the session settles, and a reply to
  /// a channel nobody reads is harmless.
  let await (wait: Async<Result<unit, 'e>>) (deadline: TimeSpan) (ct: CancellationToken) : Async<Outcome<'e>> =
    async {
      use deadlineSource = new CancellationTokenSource(deadline)
      let stopped = TaskCompletionSource<Outcome<'e>>(TaskCreationOptions.RunContinuationsAsynchronously)
      use _onCancel = ct.Register(fun () -> stopped.TrySetResult Outcome.Cancelled |> ignore)
      use _onDeadline = deadlineSource.Token.Register(fun () -> stopped.TrySetResult Outcome.DeadlineReached |> ignore)
      let answered =
        async {
          let! answer = wait
          return
            match answer with
            | Ok () -> Outcome.Ready
            | Error err -> Outcome.Failed err
        }
        |> Async.StartAsTask
      let! first = Task.WhenAny(answered, stopped.Task) |> Async.AwaitTask
      return! Async.AwaitTask first
    }
