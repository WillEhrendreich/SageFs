module SageFs.Tests.WorkerFetchLogTests

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Server.DaemonMode

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private sid = SessionId.validate "abcd1234" |> Result.defaultWith (fun _ -> failwith "bad test session id")
let private url = "http://127.0.0.1:1"
let private handle : WorkerHandle = { Pid = 4242; Port = Some 1 }

let private sessionWith (status: SessionLifecycleStatus) : SessionInfo =
  { Id = sid
    Name = None
    Projects = [ "Foo.fsproj" ]
    WorkingDirectory = "/tmp/foo"
    SolutionRoot = None
    CreatedAt = epoch
    LastActivity = epoch
    Status = status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning }

/// A snapshot that still remembers the worker's URL, as the real one does for a
/// session whose worker has just gone away.
let private snapshotWith (status: SessionLifecycleStatus) : SessionManager.QuerySnapshot =
  { SessionManager.QuerySnapshot.empty with
      Sessions = Map.ofList [ sid, sessionWith status ]
      WorkerBaseUrls = Map.ofList [ sid, url ] }

let private absent : SessionManager.QuerySnapshot =
  { SessionManager.QuerySnapshot.empty with WorkerBaseUrls = Map.ofList [ sid, url ] }

let private notLive =
  [ "Stopped", SessionLifecycleStatus.Stopped
    "Restarting", SessionLifecycleStatus.Restarting (PreviousWorker.Was 4242)
    "Restarting cold", SessionLifecycleStatus.Restarting PreviousWorker.ColdStart
    "Faulted", SessionLifecycleStatus.Faulted (FaultReason.Reported "boom") ]

let private live =
  [ "Starting", SessionLifecycleStatus.Starting handle
    "Ready", SessionLifecycleStatus.Ready handle
    "Evaluating", SessionLifecycleStatus.Evaluating handle
    "Building", SessionLifecycleStatus.Building ("build", handle) ]

/// Counts every request that reaches the wire.
type private CountingHandler() =
  inherit HttpMessageHandler()
  let mutable calls = 0
  member _.Calls = calls
  override _.SendAsync(_request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
    Interlocked.Increment(&calls) |> ignore
    Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent "\"ok\""))

let private fetchThrough (snapshot: SessionManager.QuerySnapshot) : int * string option =
  let handler = new CountingHandler()
  use client = new HttpClient(handler)
  let result =
    fetchWorkerEndpoint client (fun () -> snapshot) sid "/warmup-context" 1.0 id
    |> fun t -> t.GetAwaiter().GetResult()
  handler.Calls, result

[<Tests>]
let tests =
  testList "worker fetch: no live worker means no fetch, and a refusal is only an error when the worker should be up" [

    testList "planWorkerFetch" [
      for (name, status) in notLive do
        testCase (sprintf "WHY — planWorkerFetch — %s never reaches the fetch, even though its worker URL is still in the snapshot, because the worker is gone and the refusal is normal" name) <| fun _ ->
          match planWorkerFetch (snapshotWith status) sid with
          | WorkerFetchPlan.NoLiveWorker _ -> ()
          | other -> failtestf "expected NoLiveWorker, got %A" other

      testCase "WHY — planWorkerFetch — an absent session never reaches the fetch, because there is nothing to ask" <| fun _ ->
        planWorkerFetch absent sid |> Expect.equal "absent" WorkerFetchPlan.SessionAbsent

      for (name, status) in live do
        testCase (sprintf "WHY — planWorkerFetch — %s with a published URL is fetched" name) <| fun _ ->
          planWorkerFetch (snapshotWith status) sid |> Expect.equal "fetch" (WorkerFetchPlan.FetchFrom url)

      testCase "WHY — planWorkerFetch — a live session that has published no URL yet is not fetched, because there is no endpoint to call" <| fun _ ->
        let snap = { snapshotWith (SessionLifecycleStatus.Starting handle) with WorkerBaseUrls = Map.empty }
        planWorkerFetch snap sid
        |> Expect.equal "no endpoint" (WorkerFetchPlan.NoEndpointYet (SessionLifecycleStatus.Starting handle))
    ]

    testList "connectionRefusedLogLevel" [
      testCase "WHY — connectionRefusedLogLevel — a refusal from a Ready session is an error, because its worker should be listening" <| fun _ ->
        connectionRefusedLogLevel (snapshotWith (SessionLifecycleStatus.Ready handle)) sid
        |> Expect.equal "ready" WorkerFetchLogLevel.ErrorLevel

      for (name, status) in notLive @ [ "Starting", SessionLifecycleStatus.Starting handle
                                        "Evaluating", SessionLifecycleStatus.Evaluating handle
                                        "Building", SessionLifecycleStatus.Building ("build", handle) ] do
        testCase (sprintf "WHY — connectionRefusedLogLevel — a refusal from a %s session is debug, because it is a normal state" name) <| fun _ ->
          connectionRefusedLogLevel (snapshotWith status) sid
          |> Expect.equal "not ready" WorkerFetchLogLevel.DebugLevel

      testCase "WHY — connectionRefusedLogLevel — a refusal for an absent session is debug, because the session is gone" <| fun _ ->
        connectionRefusedLogLevel absent sid |> Expect.equal "absent" WorkerFetchLogLevel.DebugLevel
    ]

    testList "isConnectionRefused" [
      testCase "WHY — isConnectionRefused — a connection error is a refusal" <| fun _ ->
        isConnectionRefused (HttpRequestException(HttpRequestError.ConnectionError, "refused"))
        |> Expect.isTrue "connection error"

      testCase "WHY — isConnectionRefused — a bad response is not a refusal, so it stays an error whatever the status" <| fun _ ->
        isConnectionRefused (HttpRequestException(HttpRequestError.InvalidResponse, "garbled"))
        |> Expect.isFalse "invalid response"
    ]

    testList "fetchWorkerEndpoint" [
      for (name, status) in notLive do
        testCase (sprintf "WHY — fetchWorkerEndpoint — a %s session makes no request at all" name) <| fun _ ->
          let calls, result = fetchThrough (snapshotWith status)
          calls |> Expect.equal "no request reached the wire" 0
          result |> Expect.isNone "nothing came back"

      testCase "WHY — fetchWorkerEndpoint — an absent session makes no request at all" <| fun _ ->
        let calls, result = fetchThrough absent
        calls |> Expect.equal "no request reached the wire" 0
        result |> Expect.isNone "nothing came back"

      testCase "WHY — fetchWorkerEndpoint — a Ready session is still asked" <| fun _ ->
        let calls, result = fetchThrough (snapshotWith (SessionLifecycleStatus.Ready handle))
        calls |> Expect.equal "one request" 1
        result |> Expect.isSome "the reply is parsed"
    ]
  ]
