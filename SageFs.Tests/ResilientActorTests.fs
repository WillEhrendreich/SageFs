module SageFs.Tests.ResilientActorTests

open SageFs
open SageFs.Utils
open Expecto
open Expecto.Flip
open System

let private nullLogger =
  { new ILogger with
      member _.LogInfo _ = ()
      member _.LogDebug _ = ()
      member _.LogWarning _ = ()
      member _.LogError _ = () }

[<Tests>]
let resilientActorTests = testList "ResilientActor.wrapLoop" [
  testAsync "preserves state on successful message processing" {
    let processMsg (state: int) (msg: string) = async { return state + msg.Length }
    let wrapped = ResilientActor.wrapLoop nullLogger "test-actor" processMsg
    let! result = wrapped 0 "hello"
    result |> Expect.equal "should add message length" 5
  }

  testAsync "returns previous state when handler throws" {
    let processMsg (state: int) (_msg: string) = async {
      return failwith "boom"
    }
    let wrapped = ResilientActor.wrapLoop nullLogger "test-actor" processMsg
    let! result = wrapped 42 "crash"
    result |> Expect.equal "should preserve state" 42
  }

  testAsync "propagates OperationCanceledException" {
    let processMsg (_state: int) (_msg: string) = async {
      return raise (OperationCanceledException("shutting down"))
    }
    let wrapped = ResilientActor.wrapLoop nullLogger "test-actor" processMsg
    let! threw =
      async {
        try
          let! _ = wrapped 0 "cancel"
          return false
        with :? OperationCanceledException -> return true
      }
    threw |> Expect.isTrue "should propagate cancellation"
  }

  testAsync "continues processing after exception" {
    let mutable callCount = 0
    let processMsg (state: int) (msg: string) = async {
      callCount <- callCount + 1
      match msg with
      | "fail" -> return failwith "boom"
      | _ -> return state + 1
    }
    let wrapped = ResilientActor.wrapLoop nullLogger "test-actor" processMsg
    let! s1 = wrapped 0 "ok"
    let! s2 = wrapped s1 "fail"
    let! s3 = wrapped s2 "ok again"
    s1 |> Expect.equal "first ok" 1
    s2 |> Expect.equal "fail preserves" 1
    s3 |> Expect.equal "continues after fail" 2
    callCount |> Expect.equal "all calls made" 3
  }

  testAsync "handles async exceptions" {
    let processMsg (state: int) (_msg: string) = async {
      do! Async.Sleep TestTimeouts.yieldBeforeFault
      return failwith "async boom"
    }
    let wrapped = ResilientActor.wrapLoop nullLogger "test-actor" processMsg
    let! result = wrapped 99 "async-crash"
    result |> Expect.equal "should preserve state on async exception" 99
  }

  testAsync "handles different state types" {
    let processMsg (state: string list) (msg: string) = async {
      return msg :: state
    }
    let wrapped = ResilientActor.wrapLoop nullLogger "test-actor" processMsg
    let! result = wrapped [] "hello"
    result |> Expect.equal "should work with list state" ["hello"]
  }
]

[<Tests>]
let safeFireAndForgetTests = testList "SafeFireAndForget.startTask" [
  testTask "successful work runs to completion" {
    // The work says it ran, and the case awaits that: a fixed sleep guessed at how long a thread-pool turn takes.
    let ran = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
    SafeFireAndForget.startTask nullLogger "test-task" (fun () ->
      task { ran.TrySetResult () |> ignore } :> System.Threading.Tasks.Task)
    let! winner = System.Threading.Tasks.Task.WhenAny(ran.Task :> System.Threading.Tasks.Task, System.Threading.Tasks.Task.Delay TestTimeouts.patience)
    obj.ReferenceEquals(winner, ran.Task) |> Expect.isTrue "work should have run"
  }

  testTask "exceptions are caught not propagated" {
    let caught = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
    let capturingLogger =
      { new ILogger with
          member _.LogInfo _ = ()
          member _.LogDebug _ = ()
          member _.LogWarning _ = caught.TrySetResult () |> ignore
          member _.LogError _ = () }
    SafeFireAndForget.startTask capturingLogger "test-task" (fun () ->
      task { return failwith "boom" } :> System.Threading.Tasks.Task)
    let! winner = System.Threading.Tasks.Task.WhenAny(caught.Task :> System.Threading.Tasks.Task, System.Threading.Tasks.Task.Delay TestTimeouts.patience)
    obj.ReferenceEquals(winner, caught.Task) |> Expect.isTrue "exception should have been logged"
  }

  testAsync "cancellation is silent" {
    let mutable logCalled = false
    let capturingLogger =
      { new ILogger with
          member _.LogInfo _ = ()
          member _.LogDebug _ = ()
          member _.LogWarning _ = logCalled <- true
          member _.LogError _ = () }
    SafeFireAndForget.startTask capturingLogger "test-task" (fun () ->
      task { return raise (OperationCanceledException("cancel")) } :> System.Threading.Tasks.Task)
    do! Async.Sleep TestTimeouts.fireAndForgetSettle
    logCalled |> Expect.isFalse "cancellation should not be logged"
  }
]
