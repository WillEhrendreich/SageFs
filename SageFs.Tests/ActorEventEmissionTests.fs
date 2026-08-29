module SageFs.Tests.ActorEventEmissionTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.AppState
open SageFs.ActorCreation
open SageFs.Features.Events

let quietLogger = SageFs.Tests.TestInfrastructure.quietLogger

let captured = Collections.Generic.List<SageFsEvent>()
let onEvent evt = lock captured (fun () -> captured.Add(evt))

/// Single shared actor for all event emission tests
let sharedActor = lazy(
  let args = mkCommonActorArgs quietLogger false onEvent SageFs.Args.ProjectLoadConfig.empty true
  let result = createActor args |> Async.AwaitTask |> Async.RunSynchronously
  result.Actor)

[<Tests>]
let actorEventEmissionTests =
  testSequenced <| testList "Actor event emission" [

    testTask "actor emits SessionStarted and SessionReady on init" {
      let _actor = sharedActor.Value
      let events = lock captured (fun () -> captured |> Seq.toList)
      events
      |> List.exists (function SessionStarted _ -> true | _ -> false)
      |> Expect.isTrue "should emit SessionStarted"
      events
      |> List.exists (function SessionReady -> true | _ -> false)
      |> Expect.isTrue "should emit SessionReady"
    }

    testTask "actor emits EvalRequested and EvalCompleted on successful eval" {
      let actor = sharedActor.Value
      lock captured (fun () -> captured.Clear())
      let request = { Code = "1 + 1;;"; Args = Map.empty }
      let! _response =
        actor.PostAndAsyncReply(fun r -> Eval(request, CancellationToken.None, r))
        |> Async.StartAsTask
      let events = lock captured (fun () -> captured |> Seq.toList)
      events
      |> List.exists (function EvalRequested _ -> true | _ -> false)
      |> Expect.isTrue "should emit EvalRequested"
      events
      |> List.exists (function EvalCompleted _ -> true | _ -> false)
      |> Expect.isTrue "should emit EvalCompleted"
    }

    testTask "actor emits EvalFailed on syntax error" {
      let actor = sharedActor.Value
      lock captured (fun () -> captured.Clear())
      let request = { Code = "let x = ;;\n;;"; Args = Map.empty }
      let! _response =
        actor.PostAndAsyncReply(fun r -> Eval(request, CancellationToken.None, r))
        |> Async.StartAsTask
      let events = lock captured (fun () -> captured |> Seq.toList)
      events
      |> List.exists (function EvalFailed _ -> true | _ -> false)
      |> Expect.isTrue "should emit EvalFailed"
    }

    testTask "actor emits SessionReset on reset" {
      let actor = sharedActor.Value
      lock captured (fun () -> captured.Clear())
      let! _result =
        actor.PostAndAsyncReply(fun r -> ResetSession r)
        |> Async.StartAsTask
      let events = lock captured (fun () -> captured |> Seq.toList)
      events
      |> List.exists (function SessionReset -> true | _ -> false)
      |> Expect.isTrue "should emit SessionReset"
    }

    testTask "actor emits DiagnosticsChecked on diagnostics request" {
      let actor = sharedActor.Value
      lock captured (fun () -> captured.Clear())
      let! _diags =
        actor.PostAndAsyncReply(fun r -> GetDiagnostics("let x: int = \"oops\"", r))
        |> Async.StartAsTask
      // The event is emitted on the actor's own thread after the reply lands.
      let! ok = TestInfrastructure.awaitCondition 2000 (fun () ->
        lock captured (fun () ->
          captured
          |> Seq.exists (function DiagnosticsChecked _ -> true | _ -> false)))
      ok |> Expect.isTrue "should emit DiagnosticsChecked"
    }
  ]
