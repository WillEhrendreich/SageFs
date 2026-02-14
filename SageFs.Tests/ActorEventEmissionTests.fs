module SageFs.Tests.ActorEventEmissionTests

open System
open System.Collections.Generic
open System.Threading
open Expecto
open Expecto.Flip
open SageFs.AppState
open SageFs.ActorCreation
open SageFs.Features.Events

let private quietLogger = SageFs.Tests.TestInfrastructure.quietLogger

let private captured = Collections.Generic.List<SageFsEvent>()
let private onEvent evt = lock captured (fun () -> captured.Add(evt))

/// Single shared actor for all event emission tests
let private sharedActor = lazy(
  let args = mkCommonActorArgs quietLogger false onEvent []
  let result = createActor args |> Async.AwaitTask |> Async.RunSynchronously
  result.Actor)

[<Tests>]
let actorEventEmissionTests =
  testSequenced <| testList "Actor event emission" [

    testCase "actor emits SessionStarted and SessionReady on init"
    <| fun _ ->
      let _actor = sharedActor.Value
      let events = lock captured (fun () -> captured |> Seq.toList)
      events
      |> List.exists (function SessionStarted _ -> true | _ -> false)
      |> Expect.isTrue "should emit SessionStarted"
      events
      |> List.exists (function SessionReady -> true | _ -> false)
      |> Expect.isTrue "should emit SessionReady"

    testCase "actor emits EvalRequested and EvalCompleted on successful eval"
    <| fun _ ->
      let actor = sharedActor.Value
      lock captured (fun () -> captured.Clear())
      let request = { Code = "1 + 1;;"; Args = Map.empty }
      let _response = actor.PostAndAsyncReply(fun r -> Eval(request, CancellationToken.None, r)) |> Async.RunSynchronously
      let events = lock captured (fun () -> captured |> Seq.toList)
      events
      |> List.exists (function EvalRequested _ -> true | _ -> false)
      |> Expect.isTrue "should emit EvalRequested"
      events
      |> List.exists (function EvalCompleted _ -> true | _ -> false)
      |> Expect.isTrue "should emit EvalCompleted"

    testCase "actor emits EvalFailed on syntax error"
    <| fun _ ->
      let actor = sharedActor.Value
      lock captured (fun () -> captured.Clear())
      let request = { Code = "let x = ;;\n;;"; Args = Map.empty }
      let _response = actor.PostAndAsyncReply(fun r -> Eval(request, CancellationToken.None, r)) |> Async.RunSynchronously
      let events = lock captured (fun () -> captured |> Seq.toList)
      events
      |> List.exists (function EvalFailed _ -> true | _ -> false)
      |> Expect.isTrue "should emit EvalFailed"

    testCase "actor emits SessionReset on reset"
    <| fun _ ->
      let actor = sharedActor.Value
      lock captured (fun () -> captured.Clear())
      let _result = actor.PostAndAsyncReply(fun r -> ResetSession r) |> Async.RunSynchronously
      let events = lock captured (fun () -> captured |> Seq.toList)
      events
      |> List.exists (function SessionReset -> true | _ -> false)
      |> Expect.isTrue "should emit SessionReset"

    testCase "actor emits DiagnosticsChecked on diagnostics request"
    <| fun _ ->
      let actor = sharedActor.Value
      lock captured (fun () -> captured.Clear())
      let _diags = actor.PostAndAsyncReply(fun r -> GetDiagnostics("let x: int = \"oops\"", r)) |> Async.RunSynchronously
      // Event emission happens after reply — brief spin to let it propagate
      let mutable found = false
      for _ in 1..20 do
        if not found then
          Thread.Sleep(5)
          let events = lock captured (fun () -> captured |> Seq.toList)
          found <- events |> List.exists (function DiagnosticsChecked _ -> true | _ -> false)
      found
      |> Expect.isTrue "should emit DiagnosticsChecked"
  ]
