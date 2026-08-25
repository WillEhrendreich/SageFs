module SageFs.Tests.CompletionsContractTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open Microsoft.Extensions.Logging.Abstractions
open SageFs
open SageFs.McpTools
open SageFs.Features.AutoCompletion

let private mkCtxWithWorkerResponse (workerResponse: WorkerProtocol.WorkerResponse) (sessionId: string) =
  let result = SageFs.Tests.TestInfrastructure.globalActorResult.Value
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["mcp"] <- sessionId
  let ops : SessionManagementOps = {
    CreateSession = fun _ _ _ -> Task.FromResult(Ok "test-session")
    ListSessions = fun () -> Task.FromResult("No sessions")
    StopSession = fun _ -> Task.FromResult(Ok "stopped")
    DisposeSession = fun _ -> Task.FromResult(Ok "disposed")
    PurgeSession = fun _ -> Task.FromResult(Ok "purged")
    RestartSession = fun _ _ -> Task.FromResult(Ok "restarted")
    GetProxy = fun _ -> Task.FromResult(Some (fun _ -> async { return workerResponse }))
    GetSessionInfo = fun id ->
      Task.FromResult(
        Some { WorkerProtocol.SessionInfo.Id = id
               Name = None
               Projects = []
               WorkingDirectory = ""
               SolutionRoot = None
               Status = WorkerProtocol.SessionStatus.Ready
               FaultReason = None
               WorkerPid = Some 4242
               Workflow = WorkflowTypes.SessionWorkflow.Interactive
               CreatedAt = DateTime.UtcNow
               LastActivity = DateTime.UtcNow })
    GetAllSessions = fun () -> Task.FromResult([])
    UpdateSessionStatus = fun _ _ -> Task.FromResult(())
    GetStandbyInfo = fun () -> Task.FromResult(StandbyInfo.NoPool)
    NotifyWorkerDied = fun _ -> () }

  { FrictionStore = None
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = ops
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveSnapshotSink = None } : McpContext

[<Tests>]
let completionsContractTests =
  testList "Completions JSON contract" [

    testList "completionKindForLabel classification" [
      testCase "parenthesized labels classify as Method" <| fun _ ->
        completionKindForLabel "map (('a -> 'b) -> 'a list -> 'b list)"
        |> Expect.equal "function-shaped label" CompletionKind.Method
        completionKindForLabel "printfn (string -> unit)"
        |> Expect.equal "printfn label" CompletionKind.Method

      testCase "capitalized labels classify as Class" <| fun _ ->
        completionKindForLabel "String"
        |> Expect.equal "capitalized label" CompletionKind.Class
        completionKindForLabel "List"
        |> Expect.equal "List label" CompletionKind.Class

      testCase "lowercase labels classify as Variable" <| fun _ ->
        completionKindForLabel "x"
        |> Expect.equal "lowercase identifier" CompletionKind.Variable
        completionKindForLabel "listOfItems"
        |> Expect.equal "camelCase identifier" CompletionKind.Variable

      testCase "empty or symbol labels classify as Variable" <| fun _ ->
        completionKindForLabel ""
        |> Expect.equal "empty label" CompletionKind.Variable
    ]

    testList "getCompletionsItems routing" [
      testTask "maps worker completion labels to structured items" {
        let ctx = mkCtxWithWorkerResponse (WorkerProtocol.WorkerResponse.CompletionResult("rid-1", ["List"; "map ("; "x"])) "aaa00001"
        let! items = getCompletionsItems ctx "mcp" "L" 1 None
        items
        |> List.map (fun i -> i.DisplayText)
        |> Expect.equal "all three labels routed" ["List"; "map ("; "x"]
        items
        |> List.map (fun i -> CompletionKind.label i.Kind)
        |> Expect.equal "kinds classified from label shape" ["Class"; "Method"; "Variable"]
        items
        |> List.forall (fun i -> i.ReplacementText = i.DisplayText)
        |> Expect.isTrue "replacement text equals display text"
      }

      testTask "no completions returns empty list" {
        let ctx = mkCtxWithWorkerResponse (WorkerProtocol.WorkerResponse.CompletionResult("rid-2", [])) "aaa00001"
        let! items = getCompletionsItems ctx "mcp" "" 0 None
        items
        |> Expect.isEmpty "empty worker result stays empty"
      }
    ]

    testList "/api/completions payload contract" [
      testCase "structured items serialize to the VS Code parse contract" <| fun _ ->
        let items = [
          { DisplayText = "List"; ReplacementText = "List"; Kind = CompletionKind.Class; GetDescription = None }
          { DisplayText = "map"; ReplacementText = "map"; Kind = CompletionKind.Method; GetDescription = None }
        ]
        let json = SageFs.McpAdapter.formatCompletionsJson items
        use doc = System.Text.Json.JsonDocument.Parse(json)
        let completions = doc.RootElement.GetProperty("completions").EnumerateArray() |> Seq.toList
        completions.Length
        |> Expect.equal "two items" 2
        for c in completions do
          c.GetProperty("label").GetString()
          |> Expect.notEqual "" "label is present"
          c.GetProperty("kind").GetString()
          |> Expect.notEqual "" "kind is present"
          c.GetProperty("insertText").GetString()
          |> Expect.notEqual "" "insertText is present"
          // detail is optional — must be absent or a string, never break parsing
          match c.TryGetProperty("detail") with
          | true, d -> d.GetString() |> ignore
          | false, _ -> ()

      testCase "escapes description text that contains JSON control characters" <| fun _ ->
        let description = "A \"quoted\" value\nwith a tab\tand control \u0001"
        let items = [
          { DisplayText = "quoted"; ReplacementText = "quoted"; Kind = CompletionKind.Method
            GetDescription = Some (fun () -> [| FSharp.Compiler.Text.TaggedText(FSharp.Compiler.Text.TextTag.Text, description) |]) }
        ]
        use doc = SageFs.McpAdapter.formatCompletionsJson items |> System.Text.Json.JsonDocument.Parse
        let detail =
          doc.RootElement.GetProperty("completions").[0].GetProperty("detail").GetString()
        detail |> Expect.equal "description round-trips through JSON" description

      testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
        "arbitrary completion labels round-trip through the JSON editor contract" <|
        fun (labels: string list) ->
          let labels = labels |> List.map (fun label -> if isNull label then "" else label)
          let items =
            labels
            |> List.map (fun label ->
              { DisplayText = label; ReplacementText = label; Kind = CompletionKind.Variable; GetDescription = None })
          use doc = SageFs.McpAdapter.formatCompletionsJson items |> System.Text.Json.JsonDocument.Parse
          let serialized =
            doc.RootElement.GetProperty("completions").EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("label").GetString())
            |> Seq.toList
          serialized = labels
    ]
  ]
