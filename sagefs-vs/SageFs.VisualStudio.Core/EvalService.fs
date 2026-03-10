namespace SageFs.VisualStudio.Core

open System
open System.Threading

/// Holds the active eval cancellation token source and exposes cancel.
/// Centralises the eval lifecycle so all commands share one CTS.
/// Includes a 5-second watchdog timer that fires when SSE disconnects
/// while an eval is in flight — matching the VS Code and Neovim pattern.
type EvalCancellation() =
  let mutable cts: CancellationTokenSource = new CancellationTokenSource()
  let mutable isEvaluating = false
  // Monotonic eval ID — prevents phantom "eval interrupted" notifications
  // when a new eval starts within the watchdog window of a previous eval.
  let mutable evalId = 0
  let mutable watchdogTimer: Timer option = None
  let evalInterrupted = Event<unit>()

  let cancelWatchdog () =
    match watchdogTimer with
    | Some t -> t.Dispose(); watchdogTimer <- None
    | None -> ()

  /// Returns a fresh CancellationToken for a new eval, cancelling any in-flight eval.
  member _.StartNew() =
    let old = cts
    let next = new CancellationTokenSource()
    cts <- next
    isEvaluating <- true
    Interlocked.Increment(&evalId) |> ignore
    cancelWatchdog ()
    try old.Cancel(); old.Dispose() with _ -> ()
    next.Token

  /// Cancel any in-flight eval without starting a new one.
  member _.Cancel() =
    try cts.Cancel() with _ -> ()
    isEvaluating <- false
    cancelWatchdog ()

  member _.IsEvaluating = isEvaluating

  member _.Done() =
    isEvaluating <- false
    cancelWatchdog ()

  /// Fires when the watchdog determines an eval was interrupted by daemon disconnect.
  [<CLIEvent>]
  member _.EvalInterrupted = evalInterrupted.Publish

  /// Called when SSE connection drops. Starts 5-second watchdog if eval is in flight.
  member _.NotifyDisconnected() =
    match isEvaluating with
    | true ->
      let capturedId = evalId
      cancelWatchdog ()
      let timer =
        new Timer(
          (fun _ ->
            // Only fire if the same eval is still in flight (no new eval started)
            match evalId = capturedId && isEvaluating with
            | true ->
              isEvaluating <- false
              cancelWatchdog ()
              evalInterrupted.Trigger()
            | false ->
              cancelWatchdog ()),
          null, 5000, Timeout.Infinite)
      watchdogTimer <- Some timer
    | false -> ()

  /// Called when SSE connection is restored. Cancels any pending watchdog timer.
  member _.NotifyReconnected() =
    cancelWatchdog ()

  /// Wire SSE connection events from a LiveTestingSubscriber to the eval watchdog.
  /// Call once after both instances are created (e.g., in DI registration).
  static member Wire(cancel: EvalCancellation, sub: LiveTestingSubscriber) =
    sub.ConnectionLost.Add(fun () -> cancel.NotifyDisconnected())
    sub.ConnectionRestored.Add(fun () -> cancel.NotifyReconnected())

  interface IDisposable with
    member this.Dispose() =
      cancelWatchdog ()
      try cts.Cancel(); cts.Dispose() with _ -> ()

/// Request to evaluate F# code with source context.
type EvalRequest = {
  Code: string
  FilePath: string
  EvalMode: string
  BlockStartLine: int
}

/// Wraps SageFsClient with cancellation, UI-thread assertion, and lifecycle management.
/// Must only be called from the UI thread (VS command infrastructure guarantees this
/// for commands; callers from other threads must marshal first).
type EvalService(client: SageFsClient, cancellation: EvalCancellation, ?liveTestingSubscriber: LiveTestingSubscriber) =
  let mutable connectionLostDuringEval = false

  do
    match liveTestingSubscriber with
    | Some sub ->
      sub.ConnectionLost.Add(fun () ->
        match cancellation.IsEvaluating with
        | true -> connectionLostDuringEval <- true
        | false -> ())
      sub.ConnectionRestored.Add(fun () ->
        connectionLostDuringEval <- false)
    | None -> ()

  member _.CancelPending() = cancellation.Cancel()

  member _.EvalAsync(request: EvalRequest, ct: System.Threading.CancellationToken) = task {
    connectionLostDuringEval <- false
    let tok = cancellation.StartNew()
    use linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(tok, ct)
    try
      let! result = client.EvalWithContextAsync(request.Code, request.FilePath, request.EvalMode, request.BlockStartLine, linked.Token)
      cancellation.Done()
      match connectionLostDuringEval with
      | true ->
        connectionLostDuringEval <- false
        return { Output = "⚠ Evaluation completed but daemon connection was lost during execution. Results may be incomplete."; Diagnostics = result.Diagnostics; ExitCode = result.ExitCode }
      | false -> return result
    with ex ->
      cancellation.Done()
      match connectionLostDuringEval with
      | true ->
        connectionLostDuringEval <- false
        return { Output = "⚠ Evaluation interrupted: daemon connection lost"; Diagnostics = []; ExitCode = 1 }
      | false ->
        return { Output = ex.Message; Diagnostics = []; ExitCode = 1 }
  }

  interface System.IDisposable with
    member _.Dispose() = (cancellation :> System.IDisposable).Dispose()
