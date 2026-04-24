module SageFs.Tests.FaultedEvalTests

open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState

let private quietLogger = SageFs.Tests.TestInfrastructure.quietLogger

let private createFaultedActorResult () =
  let args =
    { SageFs.ActorCreation.mkCommonActorArgs quietLogger false ignore SageFs.Args.ProjectLoadConfig.empty true with
        OutStream = null :> TextWriter }
  SageFs.ActorCreation.createActor args |> Async.AwaitTask |> Async.RunSynchronously

[<Tests>]
let faultedEvalTests =
  testList "Faulted eval" [
    testCase "faulted session rejects eval without losing faulted state" <| fun _ ->
      let result = createFaultedActorResult ()
      let becameFaulted =
        SageFs.Tests.TestInfrastructure.waitFor 5000 (fun () -> result.GetSessionState() = SessionState.Faulted)
      becameFaulted
      |> Expect.isTrue "actor should enter faulted state when warmup fails"

      let request = { Code = """printfn "hello world";;"""; Args = Map.empty }
      let response =
        result.Actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))
        |> Async.RunSynchronously

      match response.EvaluationResult with
      | Error ex ->
        ex.Message
        |> Expect.stringContains "faulted eval should tell the user how to recover" "hard_reset_fsi_session"
      | Ok output ->
        failtestf "expected eval to fail for a faulted session, but got: %s" output

      result.GetSessionState()
      |> Expect.equal "faulted eval should not transition the session back to ready" SessionState.Faulted

    testCase "faulted session rejects enablestdout without crashing" <| fun _ ->
      let result = createFaultedActorResult ()
      let becameFaulted =
        SageFs.Tests.TestInfrastructure.waitFor 5000 (fun () -> result.GetSessionState() = SessionState.Faulted)
      becameFaulted
      |> Expect.isTrue "actor should enter faulted state when warmup fails"

      try
        result.Actor.Post(EnableStdout)
        Thread.Sleep(100)
        result.GetSessionState()
        |> Expect.equal "enablestdout on faulted should not transition session" SessionState.Faulted
      with ex ->
        failtestf "enablestdout on faulted session should not throw, but got: %s" ex.Message
   ]