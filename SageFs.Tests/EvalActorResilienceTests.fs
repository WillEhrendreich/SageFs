module SageFs.Tests.EvalActorResilienceTests

open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState

let quietLogger = SageFs.Tests.TestInfrastructure.quietLogger

let createActorResult () =
  let args = SageFs.ActorCreation.mkCommonActorArgs quietLogger false ignore SageFs.Args.ProjectLoadConfig.empty true
  SageFs.ActorCreation.createActor args |> Async.AwaitTask |> Async.RunSynchronously

/// Bounded wait for the actor to reach a session state (real FSI warm-up).
let waitForSessionState (result: SageFs.ActorCreation.ActorResult) (state: SessionState) =
  SageFs.Tests.TestInfrastructure.waitFor 30000 (fun () -> result.GetSessionState() = state)

[<Tests>]
let evalActorResilienceTests =
  testList "[Integration] Eval actor resilience" [

    testCase "eval actor survives a handler exception and keeps processing commands" <| fun _ ->
      let result = createActorResult ()
      waitForSessionState result SessionState.Ready
      |> Expect.isTrue "session should reach Ready after warm-up"

      // Arm the test-only fault injector so the eval actor's message-processing
      // function throws once, then disarm it immediately. The subsequent probe
      // command must still be answered — proving an escaped handler exception
      // no longer kills the eval mailbox permanently.
      let armed = ref true
      SageFs.AppState.evalActorFaultInjector <- Some(fun () ->
        if armed.Value then
          armed.Value <- false
          failwith "injected eval-actor fault")

      try
        // Command 1: cheap command routed to the eval actor — its handler throws.
        result.Actor.Post(EnableStdout)

        // Command 2: AddMiddleware round-trips THROUGH the eval actor (the reply
        // is only sent by its EvalAddMiddleware handler). If the mailbox died,
        // this is never answered and the probe times out.
        let probe =
          result.Actor.PostAndAsyncReply(fun reply -> AddMiddleware([], reply))
          |> Async.StartAsTask
        let answered = probe.Wait(15000)

        answered
        |> Expect.isTrue "eval actor should process the next command after a handler exception"

        // The wrap preserves the previous state: the session stays Ready.
        result.GetSessionState()
        |> Expect.equal "session state preserved after handler exception" SessionState.Ready
      finally
        SageFs.AppState.evalActorFaultInjector <- None
  ]
