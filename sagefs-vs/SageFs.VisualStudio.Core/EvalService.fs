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
