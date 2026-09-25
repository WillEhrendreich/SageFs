module SageFs.Tests.LiveValuesPullTests

open System.Threading
open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs.AppState
open SageFs.WorkerProtocol

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private quietLogger = SageFs.Tests.TestInfrastructure.quietLogger

let private createActorResult () =
  let args = SageFs.ActorCreation.mkCommonActorArgs quietLogger false ignore SageFs.Args.ProjectLoadConfig.empty
  SageFs.ActorCreation.createActor args |> Async.AwaitTask |> Async.RunSynchronously

let private caseNames (t: System.Type) =
  FSharpType.GetUnionCases t |> Array.map (fun c -> c.Name) |> Set.ofArray

/// Roast-4 #2: the eval actor walked every bound value (reflection) and JSON-
/// serialized the whole live-value tree BEFORE `reply.Reply`, so every eval's
/// latency — and the mailbox's readiness for the next message — paid for a
/// watch-window feature. The snapshot must be pulled on demand, off the reply
/// path: a GetLiveValues request the daemon issues after the eval returns.
[<Tests>]
let liveValuesWireTests =
  testList "Live values are pulled, not attached to the eval reply" [
    testCase "WHY — WorkerMessage has a GetLiveValues request because the daemon must be able to ask for the snapshot after the eval reply instead of receiving it inside one" <| fun _ ->
      caseNames typeof<WorkerMessage>
      |> Set.contains "GetLiveValues"
      |> Expect.isTrue "WorkerMessage.GetLiveValues"

    testCase "WHY — WorkerResponse has a LiveValuesResult because the snapshot needs its own reply, correlated by replyId like every other query" <| fun _ ->
      caseNames typeof<WorkerResponse>
      |> Set.contains "LiveValuesResult"
      |> Expect.isTrue "WorkerResponse.LiveValuesResult"
  ]

[<Tests>]
let liveValuesReplyPathTests =
  Integration.hostList "Live values off the eval reply path" [
    testCase "WHY — an eval reply carries no liveValueSnapshot metadata because building it (reflection walk + JSON) must not sit between the eval finishing and the caller getting its result" <| fun _ ->
      let result = createActorResult ()
      Thread.Sleep(50)
      let request = { Code = "let liveProbe = 42;;"; Args = Map.empty }
      let response =
        result.Actor.PostAndAsyncReply(fun reply -> Eval(request, CancellationToken.None, reply))
        |> Async.RunSynchronously
      response.EvaluationResult |> Expect.isOk "the eval itself succeeds"
      response.Metadata |> Map.containsKey "liveValueSnapshot"
      |> Expect.isFalse "the snapshot is not on the reply"
      response.Metadata |> Map.containsKey "liveValueSnapshotError"
      |> Expect.isFalse "nor is a snapshot error"
  ]
