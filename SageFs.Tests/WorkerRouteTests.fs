/// The daemon/worker wire has one home for its paths, `WorkerRoute` in SageFs.Core. The client names a route by
/// its case and the transport serves the same cases, so a renamed path cannot compile on one side and 404 on the
/// other.
module SageFs.Tests.WorkerRouteTests

open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Features.LiveTesting
open SageFs.WorkerProtocol

let private caseNames (t: System.Type) =
  FSharpType.GetUnionCases t |> Array.map (fun case -> case.Name) |> Array.toList

let private aTest : TestCase =
  { Id = TestId.create "t" TestFramework.Expecto
    FullName = "t"
    DisplayName = "t"
    NamePath = []
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

/// One message of every WorkerMessage case. The test below fails when a case is added to the protocol and not here.
let private oneOfEachMessage : WorkerMessage list =
  [ WorkerMessage.EvalCode("1", "r")
    WorkerMessage.CheckCode("1", "r")
    WorkerMessage.TypeCheckWithSymbols("1", "f.fsx", "r")
    WorkerMessage.GetCompletions("1", 0, Features.AutoCompletion.CompletionLimit.EveryMember, "r")
    WorkerMessage.DescribeCompletion("1", 0, "One", "r")
    WorkerMessage.GetReferences("A.b", "r")
    WorkerMessage.GetHostProcess "r"
    WorkerMessage.CancelEval
    WorkerMessage.CancelEvalRequest "r"
    WorkerMessage.LoadScript("f.fsx", "r")
    WorkerMessage.ResetSession "r"
    WorkerMessage.HardResetSession(false, "r")
    WorkerMessage.LevelRepl "r"
    WorkerMessage.GetStatus "r"
    WorkerMessage.GetLiveValues "r"
    WorkerMessage.EvaluateLiveMember("b", [ "p" ], "r")
    WorkerMessage.SetValueWalk(WalkEverything, "r")
    WorkerMessage.RunTests([||], 1, "r")
    WorkerMessage.GetTestDiscovery "r"
    WorkerMessage.EvalLiveTestFile("f.fsx", "1", "r")
    WorkerMessage.GetInstrumentationMaps "r"
    WorkerMessage.RunApp("p.fsproj", AppRun.PreviousAddress.ReuseAddress "http://127.0.0.1:1", "r")
    WorkerMessage.StopApp(AppRun.StopScope.CurrentApp, "r")
    WorkerMessage.AwaitAppChange("run", "r")
    WorkerMessage.DebugTestBegin(aTest, "r")
    WorkerMessage.DebugTestContinue("tk", System.TimeSpan.Zero, "r")
    WorkerMessage.SetSaveSource(Features.TrunkFollow.SaveSource.LandedOnly, "r")
    WorkerMessage.ApplySaves([], "r")
    WorkerMessage.Shutdown ]

let private messageCaseName (msg: WorkerMessage) = (fst (FSharpValue.GetUnionFields(msg, typeof<WorkerMessage>))).Name

[<Tests>]
let tests =
  testList "worker routes" [
    testCase "WHY: a request finds its route by method and path, because the client and the transport meet on that pair" <| fun _ ->
      for case in FSharpType.GetUnionCases typeof<WorkerRoute> do
        let route = FSharpValue.MakeUnion(case, [||]) :?> WorkerRoute
        WorkerRoute.tryOfRequest { HttpMethod = WorkerRoute.httpMethod route; Path = WorkerRoute.path route }
        |> Expect.equal (sprintf "%s round-trips through its own path" case.Name) (Some route)

    testCase "WHY: no two routes share a method and a path, because the second would never be reached" <| fun _ ->
      WorkerRoute.all
      |> List.countBy (fun route -> WorkerRoute.httpMethod route, WorkerRoute.path route)
      |> List.filter (fun (_, count) -> count > 1)
      |> Expect.isEmpty "every method and path pair is unique"

    testCase "WHY: the route list is every case of the type, because a hand-kept list is how a case goes unserved" <| fun _ ->
      WorkerRoute.all |> List.map (fun route -> sprintf "%A" route) |> List.sort
      |> Expect.equal "all is every case" (caseNames typeof<WorkerRoute> |> List.sort)

    testCase "WHY: the transport serves exactly the routes of the type, because it has no table of its own" <| fun _ ->
      WorkerHttpTransport.routes |> List.sort
      |> Expect.equal "the transport's routes are the type's" (WorkerRoute.all |> List.sort)

    testCase "WHY: the sample has one message of every WorkerMessage case, so the next test covers the whole protocol" <| fun _ ->
      oneOfEachMessage |> List.map messageCaseName |> List.sort
      |> Expect.equal "every case has a sample" (caseNames typeof<WorkerMessage> |> List.sort)

    testCase "WHY: every path the client can call is a route the transport serves, because a 404 shows up only at runtime" <| fun _ ->
      for msg in oneOfEachMessage do
        let httpMethod, target, _ = HttpWorkerClient.toRoute msg
        let served =
          WorkerHttpTransport.routes
          |> List.exists (fun route ->
            WorkerRoute.httpMethod route = httpMethod && WorkerRoute.path route = WorkerRoute.withoutQuery target)
        served |> Expect.isTrue (sprintf "%s calls %s %s and the transport serves it" (messageCaseName msg) httpMethod target)

    testCase "WHY: the client names the route a message goes to, so the pair it sends is that route's method and path" <| fun _ ->
      for msg in oneOfEachMessage do
        let route = HttpWorkerClient.routeOf msg
        let httpMethod, target, _ = HttpWorkerClient.toRoute msg
        httpMethod |> Expect.equal (sprintf "%s method" (messageCaseName msg)) (WorkerRoute.httpMethod route)
        WorkerRoute.withoutQuery target |> Expect.equal (sprintf "%s path" (messageCaseName msg)) (WorkerRoute.path route)
  ]
