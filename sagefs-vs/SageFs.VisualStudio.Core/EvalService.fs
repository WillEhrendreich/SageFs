namespace SageFs.VisualStudio.Core

open System
open System.Threading

/// Holds the active eval cancellation token source and exposes cancel.
/// Centralises the eval lifecycle so all commands share one CTS.
type EvalCancellation() =
  let mutable cts: CancellationTokenSource = new CancellationTokenSource()
  let mutable isEvaluating = false

  /// Returns a fresh CancellationToken for a new eval, cancelling any in-flight eval.
  member _.StartNew() =
    let old = cts
    let next = new CancellationTokenSource()
    cts <- next
    isEvaluating <- true
    try old.Cancel(); old.Dispose() with _ -> ()
    next.Token

  /// Cancel any in-flight eval without starting a new one.
  member _.Cancel() =
    try cts.Cancel() with _ -> ()
    isEvaluating <- false

  member _.IsEvaluating = isEvaluating

  member _.Done() =
    isEvaluating <- false

  interface IDisposable with
    member this.Dispose() =
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
type EvalService(client: SageFsClient) =
  let cancellation = new EvalCancellation()

  member _.CancelPending() = cancellation.Cancel()

  member _.EvalAsync(request: EvalRequest, ct: System.Threading.CancellationToken) = task {
    let tok = cancellation.StartNew()
    use linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(tok, ct)
    try
      let! result = client.EvalWithContextAsync(request.Code, request.FilePath, request.EvalMode, request.BlockStartLine, linked.Token)
      cancellation.Done()
      return result
    with ex ->
      cancellation.Done()
      return { Output = ex.Message; Diagnostics = []; ExitCode = 1 }
  }

  interface System.IDisposable with
    member _.Dispose() = (cancellation :> System.IDisposable).Dispose()
