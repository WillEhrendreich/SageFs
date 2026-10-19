/// The worker's request wire is two hand-written halves: the daemon encodes each `WorkerMessage` in
/// `HttpWorkerClient.toRoute` (an anonymous record per case) and the worker decodes it per route in
/// `WorkerHttpTransport.startServer` (`jsonProp doc "name"`, one by one). Nothing makes them agree, so a field renamed or
/// added on one side and not the other fails only at runtime, as a bare 500 (R-DOMAIN-8).
///
/// Making both halves read one shape would rewrite 26 routes. This pins the agreement instead: every case of
/// `WorkerMessage` is sent through the real client to the real server over HTTP, and the message the worker's handler
/// receives must equal the message the daemon sent. A case with no sample fails the coverage test, a case whose halves
/// drift fails the round trip, and a tampering handler proves the comparison can fail.
module SageFs.Tests.WorkerWireRoundTripTests

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Text
open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Features.LiveTesting
open SageFs.WorkerProtocol

let sampleTest (name: string) : TestCase =
  { Id = TestId.create name TestFramework.Expecto
    FullName = name
    DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

/// Text that stresses an encoder: quotes, a backslash, a newline, non-ASCII, and the characters a query string reserves.
let awkwardTexts =
  [ "plain"
    "quote \" back \\ slash\nnew line\ttab"
    "unicode ünï ✓ 日本"
    "a&b=c?d#e%20f+g" ]

/// One message of every case, with `text` in every string position and non-default values elsewhere, so a field the
/// decoder drops or swaps shows up as a different message.
let samplesWith (text: string) : WorkerMessage list =
  [ WorkerMessage.EvalCode(text, text + "-1")
    WorkerMessage.CheckCode(text, text + "-2")
    WorkerMessage.TypeCheckWithSymbols(text, text + ".fsx", text + "-3")
    WorkerMessage.GetCompletions(text, 7, text + "-4")
    WorkerMessage.CancelEval
    WorkerMessage.LoadScript(text + ".fsx", text + "-5")
    WorkerMessage.ResetSession(text + "-6")
    WorkerMessage.HardResetSession(true, text + "-7")
    WorkerMessage.GetStatus(text + "-8")
    WorkerMessage.GetLiveValues(text + "-9")
    WorkerMessage.EvaluateLiveMember(text, [ text; text + "/inner" ], text + "-10")
    WorkerMessage.SetValueWalk(ValueWalk.WalkEverything, text + "-11")
    WorkerMessage.RunTests([| sampleTest text; sampleTest (text + "2") |], 3, text + "-12")
    WorkerMessage.GetTestDiscovery(text + "-13")
    WorkerMessage.EvalLiveTestFile(text + ".fs", text, text + "-14")
    WorkerMessage.GetInstrumentationMaps(text + "-15")
    WorkerMessage.RunApp(text + ".fsproj", AppRun.PreviousAddress.ReuseAddress("http://127.0.0.1:5000/" + text), text + "-16")
    WorkerMessage.StopApp(AppRun.StopScope.OnlyRun text, text + "-17")
    WorkerMessage.AwaitAppChange(text, text + "-18")
    WorkerMessage.DebugTestBegin(sampleTest text, text + "-19")
    WorkerMessage.DebugTestContinue(text, TestTimeouts.patienceBrief, text + "-20")
    WorkerMessage.SetSaveSource(Features.TrunkFollow.SaveSource.LandedOnly, text + "-21")
    WorkerMessage.ApplySaves(
      [ { Path = text + ".fs"; Kind = Features.TrunkFollow.SaveKind.Changed }
        { Path = text + "2.fs"; Kind = Features.TrunkFollow.SaveKind.Deleted } ],
      text + "-22")
    WorkerMessage.Shutdown ]

let caseNameOf (message: WorkerMessage) : string = (fst (FSharpValue.GetUnionFields(message, typeof<WorkerMessage>))).Name

let allCaseNames : string list =
  FSharpType.GetUnionCases typeof<WorkerMessage> |> Array.map (fun case -> case.Name) |> List.ofArray

/// How the daemon puts a message on the wire: HTTP method, path (with its query) and the JSON body when there is one.
/// The real one is `HttpWorkerClient.toRoute`.
type WireEncoder = WorkerMessage -> string * string * string option

/// A message that did not arrive as it was sent: what the worker's handler received, or nothing when the worker
/// refused the request.
type Drift = { Sent: WorkerMessage; Received: WorkerMessage option }

/// Send every message, encoded by `encode`, over HTTP to a real worker server whose handler records what it is given,
/// and return the messages that did not arrive equal.
let driftOf (encode: WireEncoder)(messages: WorkerMessage list) : System.Threading.Tasks.Task<Drift list> =
  task {
    let received = ConcurrentQueue<WorkerMessage>()
    let handler (message: WorkerMessage) : Async<WorkerResponse> =
      async {
        received.Enqueue message
        return WorkerResponse.WorkerReady
      }
    let! server =
      WorkerHttpTransport.startServer
        handler
        (ref HotReloadState.empty)
        SageFs.Features.KeptState.Access.none
        []
        (fun () -> WarmupContext.empty)
        (fun () -> fun _ -> async { return Features.LiveTesting.TestResult.NotRun })
        (fun () -> HostAgent.AgentAnswered HostAgent.NoCoverage)
        0
    try
      use client = new HttpClient(BaseAddress = Uri server.BaseUrl)
      let mutable drift = []
      for message in messages do
        let httpMethod, path, body = encode message
        use request = new HttpRequestMessage(HttpMethod httpMethod, path)
        body |> Option.iter (fun json -> request.Content <- new StringContent(json, Encoding.UTF8, "application/json"))
        use! response = client.SendAsync request
        match response.IsSuccessStatusCode, received.TryDequeue() with
        | true, (true, arrived) when arrived = message -> ()
        | true, (true, arrived) -> drift <- drift @ [ { Sent = message; Received = Some arrived } ]
        | _ -> drift <- drift @ [ { Sent = message; Received = None } ]
      return drift
    finally
      (server :> IDisposable).Dispose()
  }

/// The encoding with one field name spelled differently, as a half that drifted would send it.
let renamedReplyId : WireEncoder =
  fun message ->
    let httpMethod, path, body = HttpWorkerClient.toRoute message
    httpMethod, path, body |> Option.map (fun json -> json.Replace("\"replyId\"", "\"replyID\""))

let hasBody (message: WorkerMessage) : bool =
  match HttpWorkerClient.toRoute message with
  | _, _, Some _ -> true
  | _, _, None -> false

[<Tests>]
let tests =
  testList "Worker request wire" [

    testCase "WHY: every case of WorkerMessage has a sample, so a case added without a round trip fails here" <| fun _ ->
      let sampled = samplesWith "x" |> List.map caseNameOf |> List.sort
      sampled |> Expect.equal "one sample per case, none missing and none extra" (List.sort allCaseNames)

    testTask "WHY: every WorkerMessage case reaches the worker's handler equal to what the daemon sent, so the two halves of the wire cannot drift" {
      let messages = awkwardTexts |> List.collect samplesWith
      let! drift = driftOf HttpWorkerClient.toRoute messages
      drift |> Expect.isEmpty (sprintf "messages that arrived different (client toRoute vs worker decoder): %A" drift)
    }

    testTask "WHY: the round trip has teeth, so an encoding that names a field differently is reported for exactly the messages whose body carries it" {
      let messages = samplesWith "x"
      let! drift = driftOf renamedReplyId messages
      drift |> List.map (fun d -> d.Sent) |> Expect.equal "the messages with a body drift, the others do not" (messages |> List.filter hasBody)
    }
  ]
