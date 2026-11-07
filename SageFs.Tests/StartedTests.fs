/// A test that puts a deadline on async work, or waits a window to prove nothing happened, must not have the pool's
/// queue inside that clock. These cases block every pool thread, which is what a busy suite does at times (the pool
/// sampler read a marker item waiting 1.36 s in one run), and show what each way of starting work does then.
module SageFs.Tests.StartedTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip

[<Tests>]
let tests =
  testList "Started" [

    testTask "WHY — with every pool thread blocked, Started.asTask has run its first step when it returns and Async.StartAsTask has not started, so a clock begun after it times the work" {
      let startedRan = ref false
      let queuedRan = ref false
      let! startedFirst, queuedFirst, started, queued =
        StarvedPool.whileBlocked (fun () ->
          task {
            let started = Started.asTask (async { startedRan.Value <- true; return 1 })
            let startedFirst = startedRan.Value
            // The twin: the way the sites used to start their work, from the same thread at the same moment.
            let queued = Async.StartAsTask (async { queuedRan.Value <- true; return 2 })
            let queuedFirst = queuedRan.Value
            return startedFirst, queuedFirst, started, queued
          })
      startedFirst |> Expect.isTrue "the work Started.asTask began had run its first step before the call returned"
      queuedFirst |> Expect.isFalse "Async.StartAsTask had not started its work: it waits for a pool thread, and none is free"
      let! startedAnswer = started
      startedAnswer |> Expect.equal "the started task carries the work's answer" 1
      let! queuedAnswer = queued
      queuedAnswer |> Expect.equal "the queued task answers once the pool is free again" 2
    }

    testTask "WHY — Started.andForget runs the first step of a loop at once, so a signal the loop raises first does not wait for a pool thread" {
      let raised = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
      let! signalled =
        StarvedPool.whileBlocked (fun () ->
          task {
            Started.andForget (async { raised.TrySetResult() |> ignore })
            return raised.Task.IsCompleted
          })
      signalled |> Expect.isTrue "the signal was raised before the call returned, with the pool blocked"
    }

    testTask "WHY — a failure in started work faults the task and is not thrown at the caller, so the case that waits sees it where it waits" {
      let started = Started.asTask (async { return failwith "the work failed" })
      started.IsFaulted |> Expect.isTrue "the task is faulted"
      let! raised =
        task {
          try
            let! _ = started
            return None
          with e -> return Some e.Message
        }
      raised |> Expect.equal "awaiting it raises the work's own failure" (Some "the work failed")
    }
  ]
