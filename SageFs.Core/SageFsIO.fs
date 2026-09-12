namespace SageFs

open System
open System.Threading
open System.Threading.Tasks

/// A "railway" effect: an async computation that either succeeds with a
/// value or fails with a classified `SageFsError` — never a raw exception.
/// Every `SageFsIO` value already carries structured failure information, so
/// nothing downstream needs to sniff a message string or catch `exn` to
/// learn why an operation failed; it pattern-matches a `SageFsError` case.
type SageFsIO<'A> = Async<Result<'A, SageFsError>>

/// Combinators over `SageFsIO`. Pure composition — no IO of its own beyond
/// the Async plumbing the type already implies. Nothing here swallows an
/// exception: `ofExn` is the only boundary that catches, and it forces the
/// caller to classify what it caught.
[<RequireQualifiedAccess>]
module SageFsIO =

  /// Lift a plain value into a successful `SageFsIO`.
  let ret (value: 'A) : SageFsIO<'A> =
    async.Return(Ok value)

  /// Sequence two `SageFsIO` computations, short-circuiting on the first Error.
  let bind (f: 'A -> SageFsIO<'B>) (io: SageFsIO<'A>) : SageFsIO<'B> =
    async {
      let! result = io
      match result with
      | Ok value -> return! f value
      | Error err -> return Error err
    }

  /// Apply a pure function to a successful result, preserving errors.
  let map (f: 'A -> 'B) (io: SageFsIO<'A>) : SageFsIO<'B> =
    async {
      let! result = io
      return Result.map f result
    }

  /// Lift an already-computed `Result` into `SageFsIO`.
  let ofResult (result: Result<'A, SageFsError>) : SageFsIO<'A> =
    async.Return(result)

  /// Lift a throwing async into `SageFsIO`. `classify` is mandatory — it
  /// forces every call site to decide, at the boundary, which `SageFsError`
  /// an exception from THIS specific operation means, instead of flattening
  /// every failure into the same `Unexpected`/string shape. A cancellation
  /// is never classified as a domain failure: it propagates as a
  /// cancellation, exactly as any other Async/Task caller already expects.
  let ofExn (classify: exn -> SageFsError) (work: Async<'A>) : SageFsIO<'A> =
    async {
      try
        let! value = work
        return Ok value
      with
      | :? OperationCanceledException as cancellation -> return raise cancellation
      | ex -> return Error (classify ex)
    }

  /// Run two `SageFsIO` computations concurrently; the first to complete
  /// (success or failure) wins. The loser is cooperatively cancelled — it is
  /// never awaited, so a slow loser cannot delay the caller past the winner.
  let race (a: SageFsIO<'A>) (b: SageFsIO<'A>) : SageFsIO<'A> =
    async {
      let! ct = Async.CancellationToken
      use cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
      let taskA = Async.StartAsTask(a, cancellationToken = cts.Token)
      let taskB = Async.StartAsTask(b, cancellationToken = cts.Token)
      let! (winner: Task<Result<'A, SageFsError>>) =
        Task.WhenAny(taskA, taskB) |> Async.AwaitTask
      cts.Cancel()
      return! Async.AwaitTask winner
    }

  /// Race `io` against a deadline. `onTimeout` is the `SageFsError` to fail
  /// with when the span elapses first — the caller decides what a timeout
  /// MEANS for this specific operation (`WorkerTimeout`, `SessionNotRoutable`,
  /// ...) rather than this module inventing a generic one.
  let timeout (span: TimeSpan) (onTimeout: SageFsError) (io: SageFsIO<'A>) : SageFsIO<'A> =
    let clock : SageFsIO<'A> =
      async {
        do! Async.Sleep(span)
        return Error onTimeout
      }
    race io clock

  /// Retry `io` until `isDone` accepts the latest result or `maxAttempts`
  /// attempts are spent — the final attempt's outcome (success or failure)
  /// is returned as-is, whichever way the loop ends.
  let rec retryUntil (isDone: Result<'A, SageFsError> -> bool) (maxAttempts: int) (io: SageFsIO<'A>) : SageFsIO<'A> =
    async {
      let! result = io
      match isDone result || maxAttempts <= 1 with
      | true -> return result
      | false -> return! retryUntil isDone (maxAttempts - 1) io
    }

/// Computation-expression builder for `SageFsIO`, so call sites read like
/// ordinary `async { ... }` code while every `let!`/`return!` still carries
/// the railway's short-circuit-on-Error semantics.
type SageIOBuilder() =
  member _.Bind(io: SageFsIO<'A>, f: 'A -> SageFsIO<'B>) : SageFsIO<'B> =
    SageFsIO.bind f io
  member _.Return(value: 'A) : SageFsIO<'A> =
    SageFsIO.ret value
  member _.ReturnFrom(io: SageFsIO<'A>) : SageFsIO<'A> =
    io
  member _.Zero() : SageFsIO<unit> =
    SageFsIO.ret ()
  member _.Delay(f: unit -> SageFsIO<'A>) : SageFsIO<'A> =
    async.Delay(f)
  member _.Combine(a: SageFsIO<unit>, b: SageFsIO<'A>) : SageFsIO<'A> =
    SageFsIO.bind (fun () -> b) a
  member _.TryWith(io: SageFsIO<'A>, handler: exn -> SageFsIO<'A>) : SageFsIO<'A> =
    async.TryWith(io, handler)
  member _.TryFinally(io: SageFsIO<'A>, compensation: unit -> unit) : SageFsIO<'A> =
    async.TryFinally(io, compensation)
  member _.Using(resource: 'T, binder: 'T -> SageFsIO<'A>) : SageFsIO<'A> when 'T :> IDisposable =
    async.Using(resource, binder)

[<AutoOpen>]
module SageFsIOBuilder =
  /// `sageIO { let! v = someIO ... }` — the railway CE for `SageFsIO`.
  let sageIO = SageIOBuilder()
