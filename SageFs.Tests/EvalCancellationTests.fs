module SageFs.Tests.EvalCancellationTests

open System.Threading
open Expecto
open Expecto.Flip
open SageFs.AppState

let quietLogger = SageFs.Tests.TestInfrastructure.quietLogger

let createActorResult () =
  let args = SageFs.ActorCreation.mkCommonActorArgs quietLogger false ignore []
  SageFs.ActorCreation.createActor args |> Async.AwaitTask |> Async.RunSynchronously

[<Tests>]
let evalCancellationTests =
  testList "[Integration] Eval cancellation" [

    testCase "CancelEval returns false when no eval is running"
    <| fun _ ->
      let result = createActorResult ()
      Thread.Sleep(500)
      let cancelled = result.CancelEval()
      cancelled
      |> Expect.isFalse "Should return false when no eval is in progress"

    testCase "CancelEval cancels a running eval"
    <| fun _ ->
      let result = createActorResult ()
      Thread.Sleep(500)

      // Start a long-running eval in the background
      let evalTask =
        async {
          let request = { Code = "System.Threading.Thread.Sleep(30000);;"; Args = Map.empty }
          return!
            result.Actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))
        }
        |> Async.StartAsTask

      // Give eval time to start
      Thread.Sleep(1000)

      // Cancel via direct function (bypasses mailbox)
      let cancelled = result.CancelEval()

      cancelled
      |> Expect.isTrue "Should return true when eval was cancelled"

      // The eval should complete (with an error) within a reasonable time
      let completed = evalTask.Wait(5000)
      completed
      |> Expect.isTrue "Eval should complete after cancellation"
  ]
