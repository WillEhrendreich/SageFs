module SageFs.Tests.ActorSplitTests

open System.Threading
open Expecto
open Expecto.Flip
open SageFs.AppState

let private quietLogger = SageFs.Tests.TestInfrastructure.quietLogger

let private createActorResult () =
  let args = SageFs.ActorCreation.mkCommonActorArgs quietLogger false ignore []
  SageFs.ActorCreation.createActor args |> Async.AwaitTask |> Async.RunSynchronously

[<Tests>]
let actorSplitTests =
  testList "[Integration] Actor split" [

    testCase "GetAppState responds during long eval"
    <| fun _ ->
      let result = createActorResult ()

      // Start a long-running eval in the background
      let evalTask =
        async {
          let request = { Code = "System.Threading.Thread.Sleep(5000);;"; Args = Map.empty }
          return!
            result.Actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))
        }
        |> Async.StartAsTask

      // Give eval time to start

      // Query should respond without waiting for eval to complete
      let queryTask =
        async {
          return!
            result.Actor.PostAndAsyncReply(fun reply -> GetAppState reply)
        }
        |> Async.StartAsTask

      // With the current single-actor design, this will timeout (proving the problem)
      // After the split, this should complete quickly
      let completed = queryTask.Wait(2000)
      completed
      |> Expect.isTrue "GetAppState should respond during eval"

      // Clean up — cancel the long eval
      result.CancelEval() |> ignore
      evalTask.Wait(3000) |> ignore

    testCase "Autocomplete responds during long eval"
    <| fun _ ->
      let result = createActorResult ()

      // Start a long-running eval
      let evalTask =
        async {
          let request = { Code = "System.Threading.Thread.Sleep(5000);;"; Args = Map.empty }
          return!
            result.Actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))
        }
        |> Async.StartAsTask


      // Autocomplete should respond during eval
      let queryTask =
        async {
          return!
            result.Actor.PostAndAsyncReply(fun reply -> Autocomplete("System.Console.", 15, "Console.", reply))
        }
        |> Async.StartAsTask

      let completed = queryTask.Wait(2000)
      completed
      |> Expect.isTrue "Autocomplete should respond during eval"

      result.CancelEval() |> ignore
      evalTask.Wait(3000) |> ignore

    testCase "GetDiagnostics responds during long eval"
    <| fun _ ->
      let result = createActorResult ()
      Thread.Sleep(500)

      let evalTask =
        async {
          let request = { Code = "System.Threading.Thread.Sleep(5000);;"; Args = Map.empty }
          return!
            result.Actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))
        }
        |> Async.StartAsTask


      let queryTask =
        async {
          return!
            result.Actor.PostAndAsyncReply(fun reply -> GetDiagnostics("let x = 1", reply))
        }
        |> Async.StartAsTask

      let completed = queryTask.Wait(2000)
      completed
      |> Expect.isTrue "GetDiagnostics should respond during eval"

      result.CancelEval() |> ignore
      evalTask.Wait(3000) |> ignore
  ]
