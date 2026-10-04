/// ## SageFsIO Tests
///
/// The railway effect type over `SageFsError`: monad laws (so `bind`/`ret`
/// compose the way every other `bind`/`ret` in the codebase already does),
/// plus behavior tests for the boundary combinators (`ofExn`, `timeout`,
/// `race`, `retryUntil`) that route exceptions and time budgets through the
/// algebra instead of a raw exception or a bare bool.
module SageFsIOTests

open SageFs.Tests
open System
open System.Threading.Tasks
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

/// Start the effect and hand back a task a case awaits with `let!` instead of blocking a thread. The effect is
/// raced against `TestTimeouts.briefPatience`, so a hung effect fails the case instead of hanging the run.
let private runIO (io: SageFsIO<'A>) : Task<Result<'A, SageFsError>> =
  task {
    let running = Async.StartAsTask io
    let! winner = Task.WhenAny(running, Task.Delay TestTimeouts.briefPatience)
    match obj.ReferenceEquals(winner, running) with
    | true -> return! running
    | false -> return failwithf "the effect did not finish within %A" TestTimeouts.briefPatience
  }

// ── Monad laws ──────────────────────────────────────────────────────────

[<Tests>]
let monadLawTests =
  testList "SageFsIO monad laws" [

    testProperty "WHY — left identity: bind f (ret a) = f a, or bind couldn't be trusted to sequence a lifted value" <|
      fun (a: int) (fIsOk: bool) -> task {
        let f (x: int) : SageFsIO<int> = SageFsIO.ofResult (mkResult fIsOk (x + 1))
        let! lhs = SageFsIO.bind f (SageFsIO.ret a) |> runIO
        let! rhs = f a |> runIO
        lhs |> Expect.equal "bind f (ret a) = f a" rhs
      }

    testProperty "WHY — right identity: bind ret m = m, or bind couldn't be trusted to leave an unchanged computation alone" <|
      fun (mIsOk: bool) (n: int) -> task {
        let m : SageFsIO<int> = SageFsIO.ofResult (mkResult mIsOk n)
        let! lhs = SageFsIO.bind SageFsIO.ret m |> runIO
        let! rhs = m |> runIO
        lhs |> Expect.equal "bind ret m = m" rhs
      }

    testProperty "WHY — associativity: bind h (bind g m) = bind (fun x -> bind h (g x)) m, or chained binds could reassociate into a different result" <|
      fun (mIsOk: bool) (n: int) (gIsOk: bool) (hIsOk: bool) -> task {
        let m : SageFsIO<int> = SageFsIO.ofResult (mkResult mIsOk n)
        let g (x: int) : SageFsIO<int> = SageFsIO.ofResult (mkResult gIsOk (x + 1))
        let h (x: int) : SageFsIO<int> = SageFsIO.ofResult (mkResult hIsOk (x * 2))
        let! lhs = SageFsIO.bind h (SageFsIO.bind g m) |> runIO
        let! rhs = SageFsIO.bind (fun x -> SageFsIO.bind h (g x)) m |> runIO
        lhs |> Expect.equal "bind h (bind g m) = bind (fun x -> bind h (g x)) m" rhs
      }
  ]

// ── ofExn ───────────────────────────────────────────────────────────────

[<Tests>]
let ofExnTests =
  testList "SageFsIO.ofExn" [

    testCaseTask "WHY — a throwing async becomes Error (classify ex), never a raw exception" <| fun () -> task {
      let boom : Async<int> = async { return failwith "boom" }
      let classify (ex: exn) = SageFsError.EvalFailed ex.Message
      let! result = SageFsIO.ofExn classify boom |> runIO
      result |> Expect.equal "classified error" (Error (SageFsError.EvalFailed "boom"))
    }

    testCaseTask "WHY — the exact classifier passed in is the one applied, not some fixed default" <| fun () -> task {
      let boom : Async<int> = async { return raise (InvalidOperationException "nope") }
      let classify (ex: exn) = SageFsError.CheckFailed (sprintf "custom: %s" ex.Message)
      let! result = SageFsIO.ofExn classify boom |> runIO
      result |> Expect.equal "custom classification applied" (Error (SageFsError.CheckFailed "custom: nope"))
    }

    testCaseTask "WHY — a succeeding async still becomes Ok, unaffected by the presence of a classifier" <| fun () -> task {
      let succeed : Async<int> = async { return 42 }
      let classify (_: exn) = SageFsError.Unexpected (Exception "should never run")
      let! result = SageFsIO.ofExn classify succeed |> runIO
      result |> Expect.equal "Ok on success" (Ok 42)
    }

    testCaseTask "WHY — a cancellation is never classified as a domain failure; it propagates as a cancellation" <| fun () -> task {
      use cts = new Threading.CancellationTokenSource()
      let neverCompletes : Async<int> =
        async {
          do! Async.Sleep TestTimeouts.runawayEval
          return 0
        }
      let classify (_: exn) = SageFsError.Unexpected (Exception "must not classify a cancellation")
      let io = SageFsIO.ofExn classify neverCompletes
      let running = Async.StartAsTask(io, cancellationToken = cts.Token)
      cts.CancelAfter(TestTimeouts.settle)
      let! cancelled =
        task {
          try
            let! _ = running
            return false
          with :? OperationCanceledException -> return true
        }
      cancelled |> Expect.isTrue "cancellation propagated instead of being classified"
    }
  ]

// ── timeout ─────────────────────────────────────────────────────────────

[<Tests>]
let timeoutTests =
  testList "SageFsIO.timeout" [

    testCaseTask "WHY — an async that exceeds the span yields Error <the passed error>" <| fun () -> task {
      let slow : SageFsIO<int> =
        async {
          do! Async.Sleep(int TestTimeouts.slowWork.TotalMilliseconds)
          return Ok 1
        }
      let onTimeout = SageFsError.WorkerTimeout ("s1", "eval", TestTimeouts.deadlineTight.TotalSeconds)
      let! result = SageFsIO.timeout TestTimeouts.deadlineTight onTimeout slow |> runIO
      result |> Expect.equal "times out with the given error" (Error onTimeout)
    }

    testCaseTask "WHY — an async that finishes in time yields its own result, not the timeout error" <| fun () -> task {
      let fast : SageFsIO<int> = SageFsIO.ret 7
      let onTimeout = SageFsError.WorkerTimeout ("s1", "eval", TestTimeouts.deadlineRoomy.TotalSeconds)
      let! result = SageFsIO.timeout TestTimeouts.deadlineRoomy onTimeout fast |> runIO
      result |> Expect.equal "finishes with its own value" (Ok 7)
    }

    testCaseTask "WHY — a fast failure still beats the deadline with its own error, not the timeout error" <| fun () -> task {
      let fastFailure : SageFsIO<int> = SageFsIO.ofResult (Error (SageFsError.EvalFailed "fast failure"))
      let onTimeout = SageFsError.WorkerTimeout ("s1", "eval", TestTimeouts.deadlineRoomy.TotalSeconds)
      let! result = SageFsIO.timeout TestTimeouts.deadlineRoomy onTimeout fastFailure |> runIO
      result |> Expect.equal "own failure wins over the deadline" (Error (SageFsError.EvalFailed "fast failure"))
    }
  ]

// ── race ────────────────────────────────────────────────────────────────

[<Tests>]
let raceTests =
  testList "SageFsIO.race" [

    testCaseTask "WHY — race returns the faster side's result" <| fun () -> task {
      let slow : SageFsIO<string> =
        async {
          do! Async.Sleep TestTimeouts.slowWork
          return Ok "slow"
        }
      let fast : SageFsIO<string> =
        async {
          do! Async.Sleep TestTimeouts.fastRaceSide
          return Ok "fast"
        }
      let! result = SageFsIO.race slow fast |> runIO
      result |> Expect.equal "fast side wins" (Ok "fast")
    }

    testCaseTask "WHY — race also returns a fast failure over a slow success" <| fun () -> task {
      let slowSuccess : SageFsIO<string> =
        async {
          do! Async.Sleep TestTimeouts.slowWork
          return Ok "slow"
        }
      let fastFailure : SageFsIO<string> =
        async {
          do! Async.Sleep TestTimeouts.fastRaceSide
          return Error (SageFsError.EvalFailed "fast failure")
        }
      let! result = SageFsIO.race slowSuccess fastFailure |> runIO
      result |> Expect.equal "fast failure wins" (Error (SageFsError.EvalFailed "fast failure"))
    }
  ]

// ── retryUntil ──────────────────────────────────────────────────────────

[<Tests>]
let retryUntilTests =
  testList "SageFsIO.retryUntil" [

    testCaseTask "WHY — retries until the predicate holds, then stops" <| fun () -> task {
      let attempts = ref 0
      let io : SageFsIO<int> =
        async {
          attempts.Value <- attempts.Value + 1
          return
            match attempts.Value >= 3 with
            | true -> Ok attempts.Value
            | false -> Error (SageFsError.EvalFailed "not yet")
        }
      let! result = SageFsIO.retryUntil Result.isOk 10 io |> runIO
      result |> Expect.equal "stopped once Ok" (Ok 3)
      attempts.Value |> Expect.equal "attempted exactly 3 times" 3
    }

    testCaseTask "WHY — gives up after maxAttempts, returning the last (failing) outcome" <| fun () -> task {
      let attempts = ref 0
      let io : SageFsIO<int> =
        async {
          attempts.Value <- attempts.Value + 1
          return Error (SageFsError.EvalFailed "always fails")
        }
      let! result = SageFsIO.retryUntil Result.isOk 4 io |> runIO
      result |> Expect.equal "last failing outcome returned" (Error (SageFsError.EvalFailed "always fails"))
      attempts.Value |> Expect.equal "attempted exactly maxAttempts times" 4
    }

    testCaseTask "WHY — a predicate that holds immediately runs the effect exactly once" <| fun () -> task {
      let attempts = ref 0
      let io : SageFsIO<int> =
        async {
          attempts.Value <- attempts.Value + 1
          return Ok attempts.Value
        }
      let! result = SageFsIO.retryUntil Result.isOk 10 io |> runIO
      result |> Expect.equal "Ok on first attempt" (Ok 1)
      attempts.Value |> Expect.equal "only one attempt needed" 1
    }
  ]

// ── sageIO computation expression ──────────────────────────────────────

[<Tests>]
let sageIOBuilderTests =
  testList "sageIO builder" [

    testCaseTask "WHY — sageIO { let! } short-circuits on the first Error, like bind does" <| fun () -> task {
      let io =
        sageIO {
          let! a = SageFsIO.ret 1
          let! b = SageFsIO.ofResult (Error (SageFsError.EvalFailed "stop here") : Result<int, SageFsError>)
          let! c = SageFsIO.ret (a + b)
          return c
        }
      let! result = io |> runIO
      result |> Expect.equal "short-circuited" (Error (SageFsError.EvalFailed "stop here"))
    }

    testCaseTask "WHY — sageIO { let! } composes successes through to the final return" <| fun () -> task {
      let io =
        sageIO {
          let! a = SageFsIO.ret 1
          let! b = SageFsIO.ret 2
          return a + b
        }
      let! result = io |> runIO
      result |> Expect.equal "composed successfully" (Ok 3)
    }
  ]
