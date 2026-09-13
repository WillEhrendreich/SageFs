/// ## SageFsIO Tests
///
/// The railway effect type over `SageFsError`: monad laws (so `bind`/`ret`
/// compose the way every other `bind`/`ret` in the codebase already does),
/// plus behavior tests for the boundary combinators (`ofExn`, `timeout`,
/// `race`, `retryUntil`) that route exceptions and time budgets through the
/// algebra instead of a raw exception or a bare bool.
module SageFsIOTests

open System
open Expecto
open Expecto.Flip
open SageFs

// ── Fixtures ────────────────────────────────────────────────────────────

/// Build a `Result<int, SageFsError>` from cheap FsCheck-generated inputs
/// (`bool`/`int`) rather than deriving an `Arbitrary<SageFsError>` for the
/// whole 30-case DU (several cases carry `exn`, which has no meaningful
/// generator) — two SessionNotFound variants distinguished by payload is
/// enough to exercise both the Ok and Error rails.
let private mkResult (isOk: bool) (n: int) : Result<int, SageFsError> =
  match isOk with
  | true -> Ok n
  | false -> SageFsError.SessionNotFound (string n) |> Error

let private runIO (io: SageFsIO<'A>) : Result<'A, SageFsError> =
  Async.RunSynchronously(io, timeout = 5000)

// ── Monad laws ──────────────────────────────────────────────────────────

[<Tests>]
let monadLawTests =
  testList "SageFsIO monad laws" [

    testProperty "WHY — left identity: bind f (ret a) = f a, or bind couldn't be trusted to sequence a lifted value" <|
      fun (a: int) (fIsOk: bool) ->
        let f (x: int) : SageFsIO<int> = SageFsIO.ofResult (mkResult fIsOk (x + 1))
        let lhs = SageFsIO.bind f (SageFsIO.ret a) |> runIO
        let rhs = f a |> runIO
        lhs |> Expect.equal "bind f (ret a) = f a" rhs

    testProperty "WHY — right identity: bind ret m = m, or bind couldn't be trusted to leave an unchanged computation alone" <|
      fun (mIsOk: bool) (n: int) ->
        let m : SageFsIO<int> = SageFsIO.ofResult (mkResult mIsOk n)
        let lhs = SageFsIO.bind SageFsIO.ret m |> runIO
        let rhs = m |> runIO
        lhs |> Expect.equal "bind ret m = m" rhs

    testProperty "WHY — associativity: bind h (bind g m) = bind (fun x -> bind h (g x)) m, or chained binds could reassociate into a different result" <|
      fun (mIsOk: bool) (n: int) (gIsOk: bool) (hIsOk: bool) ->
        let m : SageFsIO<int> = SageFsIO.ofResult (mkResult mIsOk n)
        let g (x: int) : SageFsIO<int> = SageFsIO.ofResult (mkResult gIsOk (x + 1))
        let h (x: int) : SageFsIO<int> = SageFsIO.ofResult (mkResult hIsOk (x * 2))
        let lhs = SageFsIO.bind h (SageFsIO.bind g m) |> runIO
        let rhs = SageFsIO.bind (fun x -> SageFsIO.bind h (g x)) m |> runIO
        lhs |> Expect.equal "bind h (bind g m) = bind (fun x -> bind h (g x)) m" rhs
  ]

// ── ofExn ───────────────────────────────────────────────────────────────

[<Tests>]
let ofExnTests =
  testList "SageFsIO.ofExn" [

    testCase "WHY — a throwing async becomes Error (classify ex), never a raw exception" <| fun _ ->
      let boom : Async<int> = async { return failwith "boom" }
      let classify (ex: exn) = SageFsError.EvalFailed ex.Message
      let result = SageFsIO.ofExn classify boom |> runIO
      result |> Expect.equal "classified error" (Error (SageFsError.EvalFailed "boom"))

    testCase "WHY — the exact classifier passed in is the one applied, not some fixed default" <| fun _ ->
      let boom : Async<int> = async { return raise (InvalidOperationException "nope") }
      let classify (ex: exn) = SageFsError.CheckFailed (sprintf "custom: %s" ex.Message)
      let result = SageFsIO.ofExn classify boom |> runIO
      result |> Expect.equal "custom classification applied" (Error (SageFsError.CheckFailed "custom: nope"))

    testCase "WHY — a succeeding async still becomes Ok, unaffected by the presence of a classifier" <| fun _ ->
      let succeed : Async<int> = async { return 42 }
      let classify (_: exn) = SageFsError.Unexpected (Exception "should never run")
      let result = SageFsIO.ofExn classify succeed |> runIO
      result |> Expect.equal "Ok on success" (Ok 42)

    testCase "WHY — a cancellation is never classified as a domain failure; it propagates as a cancellation" <| fun _ ->
      use cts = new Threading.CancellationTokenSource()
      let neverCompletes : Async<int> =
        async {
          do! Async.Sleep(60000)
          return 0
        }
      let classify (_: exn) = SageFsError.Unexpected (Exception "must not classify a cancellation")
      let io = SageFsIO.ofExn classify neverCompletes
      cts.CancelAfter(50)
      let mutable cancelled = false
      try
        Async.RunSynchronously(io, cancellationToken = cts.Token) |> ignore
      with :? OperationCanceledException -> cancelled <- true
      cancelled |> Expect.isTrue "cancellation propagated instead of being classified"
  ]

// ── timeout ─────────────────────────────────────────────────────────────

[<Tests>]
let timeoutTests =
  testList "SageFsIO.timeout" [

    testCase "WHY — an async that exceeds the span yields Error <the passed error>" <| fun _ ->
      let slow : SageFsIO<int> =
        async {
          do! Async.Sleep(2000)
          return Ok 1
        }
      let onTimeout = SageFsError.WorkerTimeout ("s1", "eval", 0.05)
      let result = SageFsIO.timeout (TimeSpan.FromMilliseconds 50.0) onTimeout slow |> runIO
      result |> Expect.equal "times out with the given error" (Error onTimeout)

    testCase "WHY — an async that finishes in time yields its own result, not the timeout error" <| fun _ ->
      let fast : SageFsIO<int> = SageFsIO.ret 7
      let onTimeout = SageFsError.WorkerTimeout ("s1", "eval", 5.0)
      let result = SageFsIO.timeout (TimeSpan.FromSeconds 5.0) onTimeout fast |> runIO
      result |> Expect.equal "finishes with its own value" (Ok 7)

    testCase "WHY — a fast failure still beats the deadline with its own error, not the timeout error" <| fun _ ->
      let fastFailure : SageFsIO<int> = SageFsIO.ofResult (Error (SageFsError.EvalFailed "fast failure"))
      let onTimeout = SageFsError.WorkerTimeout ("s1", "eval", 5.0)
      let result = SageFsIO.timeout (TimeSpan.FromSeconds 5.0) onTimeout fastFailure |> runIO
      result |> Expect.equal "own failure wins over the deadline" (Error (SageFsError.EvalFailed "fast failure"))
  ]

// ── race ────────────────────────────────────────────────────────────────

[<Tests>]
let raceTests =
  testList "SageFsIO.race" [

    testCase "WHY — race returns the faster side's result" <| fun _ ->
      let slow : SageFsIO<string> =
        async {
          do! Async.Sleep(2000)
          return Ok "slow"
        }
      let fast : SageFsIO<string> =
        async {
          do! Async.Sleep(10)
          return Ok "fast"
        }
      let result = SageFsIO.race slow fast |> runIO
      result |> Expect.equal "fast side wins" (Ok "fast")

    testCase "WHY — race also returns a fast failure over a slow success" <| fun _ ->
      let slowSuccess : SageFsIO<string> =
        async {
          do! Async.Sleep(2000)
          return Ok "slow"
        }
      let fastFailure : SageFsIO<string> =
        async {
          do! Async.Sleep(10)
          return Error (SageFsError.EvalFailed "fast failure")
        }
      let result = SageFsIO.race slowSuccess fastFailure |> runIO
      result |> Expect.equal "fast failure wins" (Error (SageFsError.EvalFailed "fast failure"))
  ]

// ── retryUntil ──────────────────────────────────────────────────────────

[<Tests>]
let retryUntilTests =
  testList "SageFsIO.retryUntil" [

    testCase "WHY — retries until the predicate holds, then stops" <| fun _ ->
      let mutable attempts = 0
      let io : SageFsIO<int> =
        async {
          attempts <- attempts + 1
          return
            match attempts >= 3 with
            | true -> Ok attempts
            | false -> Error (SageFsError.EvalFailed "not yet")
        }
      let result = SageFsIO.retryUntil Result.isOk 10 io |> runIO
      result |> Expect.equal "stopped once Ok" (Ok 3)
      attempts |> Expect.equal "attempted exactly 3 times" 3

    testCase "WHY — gives up after maxAttempts, returning the last (failing) outcome" <| fun _ ->
      let mutable attempts = 0
      let io : SageFsIO<int> =
        async {
          attempts <- attempts + 1
          return Error (SageFsError.EvalFailed "always fails")
        }
      let result = SageFsIO.retryUntil Result.isOk 4 io |> runIO
      result |> Expect.equal "last failing outcome returned" (Error (SageFsError.EvalFailed "always fails"))
      attempts |> Expect.equal "attempted exactly maxAttempts times" 4

    testCase "WHY — a predicate that holds immediately runs the effect exactly once" <| fun _ ->
      let mutable attempts = 0
      let io : SageFsIO<int> =
        async {
          attempts <- attempts + 1
          return Ok attempts
        }
      let result = SageFsIO.retryUntil Result.isOk 10 io |> runIO
      result |> Expect.equal "Ok on first attempt" (Ok 1)
      attempts |> Expect.equal "only one attempt needed" 1
  ]

// ── sageIO computation expression ──────────────────────────────────────

[<Tests>]
let sageIOBuilderTests =
  testList "sageIO builder" [

    testCase "WHY — sageIO { let! } short-circuits on the first Error, like bind does" <| fun _ ->
      let io =
        sageIO {
          let! a = SageFsIO.ret 1
          let! b = SageFsIO.ofResult (Error (SageFsError.EvalFailed "stop here") : Result<int, SageFsError>)
          let! c = SageFsIO.ret (a + b)
          return c
        }
      io |> runIO |> Expect.equal "short-circuited" (Error (SageFsError.EvalFailed "stop here"))

    testCase "WHY — sageIO { let! } composes successes through to the final return" <| fun _ ->
      let io =
        sageIO {
          let! a = SageFsIO.ret 1
          let! b = SageFsIO.ret 2
          return a + b
        }
      io |> runIO |> Expect.equal "composed successfully" (Ok 3)
  ]
