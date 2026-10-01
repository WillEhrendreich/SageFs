/// The daemon-to-worker wire for the live-bindings pane: a click on a "not evaluated" row, and the walk mode. The
/// daemon's HTTP API for editors (`POST /api/sessions/{sid}/live-values/evaluate` and `.../mode`) takes the same
/// payloads, so one shape serves the dashboard, the daemon and sagefs.nvim.
module SageFs.Tests.LiveBindingsWireTests

open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

let private json (text: string) = JsonDocument.Parse text

[<Tests>]
let routeTests =
  testList "live-bindings worker routes" [
    testCase "EvaluateLiveMember is POST /live-values/evaluate with the binding, the path and the reply id" <| fun _ ->
      let method, path, body = HttpWorkerClient.toRoute (WorkerMessage.EvaluateLiveMember("box", [ "Items"; "Size" ], "r1"))
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/live-values/evaluate"
      match body with
      | None -> failtest "a click has a body"
      | Some text ->
        use doc = json text
        doc.RootElement.GetProperty("binding").GetString() |> Expect.equal "binding" "box"
        [ for label in doc.RootElement.GetProperty("path").EnumerateArray() -> label.GetString() ]
        |> Expect.equal "path" [ "Items"; "Size" ]
        doc.RootElement.GetProperty("replyId").GetString() |> Expect.equal "reply id" "r1"

    testCase "SetValueWalk is POST /live-values/mode and names the mode in words" <| fun _ ->
      let method, path, body = HttpWorkerClient.toRoute (WorkerMessage.SetValueWalk(WalkEverything, "r2"))
      method |> Expect.equal "method" "POST"
      path |> Expect.equal "path" "/live-values/mode"
      match body with
      | None -> failtest "a mode switch has a body"
      | Some text ->
        use doc = json text
        doc.RootElement.GetProperty("mode").GetString() |> Expect.equal "mode" (ValueWalk.name WalkEverything)

    testCase "a click counts as the session being used, a mode switch does not" <| fun _ ->
      WorkerMessage.isActivity (WorkerMessage.EvaluateLiveMember("box", [ "Size" ], "r1"))
      |> Expect.equal "a click runs the user's code" true
      WorkerMessage.isActivity (WorkerMessage.SetValueWalk(WalkOff, "r2"))
      |> Expect.equal "a mode switch is control, like a reset" false

    testCase "the worker's route table lists both routes as mutating" <| fun _ ->
      let mutating =
        SageFs.WorkerHttpTransport.routes
        |> List.filter (fun route -> SageFs.WorkerHttpTransport.WorkerRoute.access route = SageFs.WorkerHttpTransport.RouteAccess.Mutating)
        |> List.map SageFs.WorkerHttpTransport.WorkerRoute.path
      mutating |> Expect.contains "evaluate is a POST" "/live-values/evaluate"
      mutating |> Expect.contains "mode is a POST" "/live-values/mode"
  ]

[<Tests>]
let modeNameTests =
  testList "ValueWalk names on the wire" [
    testCase "every choice is read back from its own name, in any case" <| fun _ ->
      ValueWalk.all
      |> List.iter (fun choice ->
        ValueWalk.parse (ValueWalk.name choice) |> Expect.equal "exact" (Result.Ok choice)
        ValueWalk.parse ((ValueWalk.name choice).ToUpperInvariant()) |> Expect.equal "upper" (Result.Ok choice))

    testCase "a name that is not a choice is refused with the names that are" <| fun _ ->
      match ValueWalk.parse "Reckless" with
      | Result.Error unknown ->
        let text = ValueWalk.describeUnknown unknown
        text |> Expect.stringContains "says what it got" "Reckless"
        ValueWalk.all |> List.iter (fun choice -> text |> Expect.stringContains "lists the valid names" (ValueWalk.name choice))
      | Result.Ok other -> failtestf "expected a refusal, got %A" other
  ]

let private startWith (handler: WorkerMessage -> Async<WorkerResponse>) : System.Threading.Tasks.Task<WorkerHttpTransport.HttpWorkerServer> =
  WorkerHttpTransport.startServer
    handler
    (ref HotReloadState.empty)
    SageFs.Features.KeptState.Access.none
    []
    (fun () -> WarmupContext.empty)
    (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun })
    (fun () -> SageFs.HostAgent.AgentAnswered SageFs.HostAgent.NoCoverage)
    0

[<Tests>]
let roundTripTests =
  testList "live-bindings messages round-trip through the worker's HTTP server" [
    testTask "a click reaches the handler with its binding and path, and the outcome comes back" {
      let seen = ref None
      let handler (message: WorkerMessage) : Async<WorkerResponse> =
        async {
          match message with
          | WorkerMessage.EvaluateLiveMember(binding, path, replyId) ->
            seen.Value <- Some(binding, path)
            return WorkerResponse.LiveMemberResult(replyId, "BindingNotFound")
          | other -> return WorkerResponse.WorkerError(SageFsError.WorkerCommunicationFailed("test", sprintf "unexpected %A" other))
        }
      let! (server: WorkerHttpTransport.HttpWorkerServer) = startWith handler
      try
        let proxy = WorkerHttpTransport.httpProxy server.BaseUrl
        let! response = proxy (WorkerMessage.EvaluateLiveMember("box", [ "Items" ], "c1")) |> Async.StartAsTask
        match response with
        | WorkerResponse.LiveMemberResult(replyId, outcome) ->
          replyId |> Expect.equal "reply id" "c1"
          outcome |> Expect.equal "the outcome text" "BindingNotFound"
        | other -> failtestf "unexpected: %A" other
        seen.Value |> Expect.equal "the handler saw the click" (Some("box", [ "Items" ]))
      finally
        (server :> System.IDisposable).Dispose()
    }

    testTask "a mode switch reaches the handler as the choice it names" {
      let seen = ref None
      let handler (message: WorkerMessage) : Async<WorkerResponse> =
        async {
          match message with
          | WorkerMessage.SetValueWalk(mode, replyId) ->
            seen.Value <- Some mode
            return WorkerResponse.LiveValuesResult(replyId, "{}")
          | other -> return WorkerResponse.WorkerError(SageFsError.WorkerCommunicationFailed("test", sprintf "unexpected %A" other))
        }
      let! (server: WorkerHttpTransport.HttpWorkerServer) = startWith handler
      try
        let proxy = WorkerHttpTransport.httpProxy server.BaseUrl
        let! _ = proxy (WorkerMessage.SetValueWalk(WalkEverything, "m1")) |> Async.StartAsTask
        seen.Value |> Expect.equal "the handler saw the mode" (Some WalkEverything)
      finally
        (server :> System.IDisposable).Dispose()
    }
  ]
