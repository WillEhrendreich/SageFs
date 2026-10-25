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
open SageFs.McpCompletions
open SageFs.Features.AutoCompletion

let private mkCtxWithWorkerResponse (workerResponse: WorkerProtocol.WorkerResponse) (sessionId: string) =
  let result = SageFs.Tests.TestInfrastructure.globalActorResult.Value
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["mcp"] <- sessionId
  let ops : SessionManagementOps = {
    CreateSession = fun _ _ _ -> Task.FromResult(Ok "test-session")
    ListSessions = fun () -> Task.FromResult("No sessions")
    StopSession = fun _ -> Task.FromResult(Ok "stopped")
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
               Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 4242; Port = None }
               Workflow = WorkflowTypes.SessionWorkflow.Interactive
               CreatedAt = DateTime.UtcNow
               LastActivity = DateTime.UtcNow
               ActiveProject = None
               ProjectRoles = []
               App = SageFs.AppRun.AppRunState.NotRunning; Rebuild = LastRebuild.NeverRebuilt; Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync })
    GetAllSessions = fun () -> Task.FromResult([])
    UpdateSessionStatus = fun _ _ -> Task.FromResult(())
    NotifyWorkerDied = fun _ -> ()
    ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
    ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
    AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
    EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
    AwaitReady = fun _ _ -> Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
    SwitchWorkflow = fun _ _ -> Task.FromResult(Result.Error (SageFsError.HardResetFailed "Not available"))
    GetAdoptedCore = fun _ -> Task.FromResult(None)
    RefetchLiveTestingState = fun _ -> System.Threading.Tasks.Task.FromResult(())
    GetWarmupProgress = fun _ -> Task.FromResult(None) }

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
    GetFeatureState = None; RecordEval = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveBindings = None
    CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None } : McpContext

[<Tests>]
let completionsContractTests =
  testList "Completions JSON contract" [

    testList "completion kinds come from the worker" [
      testTask "WHY — the daemon reports the kind FCS gave each candidate, so a union case under List is not drillable because its name is capitalised" {
        let wire =
          [ { DisplayText = "Cons"; ReplacementText = "Cons"; Kind = CompletionKind.EnumMember }
            { DisplayText = "Empty"; ReplacementText = "Empty"; Kind = CompletionKind.EnumMember }
            { DisplayText = "map"; ReplacementText = "map"; Kind = CompletionKind.Method }
            { DisplayText = "Module"; ReplacementText = "Module"; Kind = CompletionKind.Module } ]
        let ctx = mkCtxWithWorkerResponse (WorkerProtocol.WorkerResponse.CompletionResult("rid-k", CompletionAnswer.Answered(wire, CompletionTotal.Exact 4))) "aaa00001"
        let! answer = getCompletionsItems ctx "mcp" "List." 5 None None
        match answer with
        | CompletionAnswer.Answered(items, _) ->
          items
          |> List.map (fun (i: CompletionItem) -> i.DisplayText, CompletionKind.label i.Kind)
          |> Expect.equal "each candidate keeps the kind the worker sent" [ "Cons", "EnumMember"; "Empty", "EnumMember"; "map", "Method"; "Module", "Module" ]
        | CompletionAnswer.Unavailable reason -> failtestf "expected candidates, got %A" reason
      }
    ]

    testList "getCompletionsItems routing" [
      testTask "maps worker completion entries to structured items" {
        let ctx = mkCtxWithWorkerResponse (WorkerProtocol.WorkerResponse.CompletionResult("rid-1", CompletionFixtures.plainNames ["List"; "map ("; "x"])) "aaa00001"
        let! answer = getCompletionsItems ctx "mcp" "L" 1 None None
        let items = CompletionAnswer.itemsOrEmpty answer
        items
        |> List.map (fun (i: CompletionItem) -> i.DisplayText)
        |> Expect.equal "all three labels routed" ["List"; "map ("; "x"]
        items
        |> List.forall (fun (i: CompletionItem) -> i.ReplacementText = i.DisplayText)
        |> Expect.isTrue "replacement text equals display text"
      }

      testTask "WHY — a worker that listed nothing is an empty answer with an exact zero, not an unavailable one" {
        let ctx = mkCtxWithWorkerResponse (WorkerProtocol.WorkerResponse.CompletionResult("rid-2", CompletionFixtures.plainNames [])) "aaa00001"
        let! answer = getCompletionsItems ctx "mcp" "" 0 None None
        match answer with
        | CompletionAnswer.Answered([], CompletionTotal.Exact 0) -> ()
        | other -> failtestf "expected an empty exact answer, got %A" other
      }

      testTask "WHY — a worker that could not look is passed on as unavailable with its reason, so an editor never reads it as 'no members'" {
        let ctx =
          mkCtxWithWorkerResponse
            (WorkerProtocol.WorkerResponse.CompletionResult("rid-u", CompletionAnswer.Unavailable CompletionUnavailable.SessionNotActive)) "aaa00001"
        let! answer = getCompletionsItems ctx "mcp" "System.IO.Path." 15 None None
        match answer with
        | CompletionAnswer.Unavailable CompletionUnavailable.SessionNotActive -> ()
        | other -> failtestf "expected the worker's reason to survive, got %A" other
      }

      testTask "WHY — a reply that is not a completion list is unavailable, not an empty list" {
        let ctx = mkCtxWithWorkerResponse WorkerProtocol.WorkerResponse.WorkerReady "aaa00001"
        let! answer = getCompletionsItems ctx "mcp" "List." 5 None None
        match answer with
        | CompletionAnswer.Unavailable(CompletionUnavailable.WorkerUnreachable _) -> ()
        | other -> failtestf "expected WorkerUnreachable, got %A" other
      }

      testTask "WHY — getCompletionsItems — a request naming its session reaches that session because a working directory can match several (the VS Code Type Explorer knows its session id)" {
        let baseCtx = mkCtxWithWorkerResponse (WorkerProtocol.WorkerResponse.CompletionResult("rid-3", CompletionFixtures.plainNames ["Length"; "Substring ("])) "aaa00001"
        let named = "bbb00002"
        let ctx =
          { baseCtx with
              SessionOps =
                { baseCtx.SessionOps with
                    GetProxy = fun id ->
                      match WorkerProtocol.SessionId.value id = named with
                      | true -> baseCtx.SessionOps.GetProxy id
                      | false -> Task.FromResult None } }
        let! answer = getCompletionsItems ctx "http" "s." 2 (Some named) None
        answer
        |> CompletionAnswer.itemsOrEmpty
        |> List.map (fun (i: CompletionItem) -> i.DisplayText)
        |> Expect.equal "the named session's completions" ["Length"; "Substring ("]
      }
    ]

    testList "/api/completions payload contract" [
      testCase "structured items serialize to the VS Code parse contract" <| fun _ ->
        let items = [
          { DisplayText = "List"; ReplacementText = "List"; Kind = CompletionKind.Class; GetDescription = None }
          { DisplayText = "map"; ReplacementText = "map"; Kind = CompletionKind.Method; GetDescription = None }
        ]
        let json = formatCompletionsJson (CompletionAnswer.Answered(items, CompletionTotal.Exact 2))
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
        use doc = formatCompletionsJson (CompletionAnswer.Answered(items, CompletionTotal.Exact 1)) |> System.Text.Json.JsonDocument.Parse
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
          use doc = formatCompletionsJson (CompletionAnswer.Answered(items, CompletionTotal.Exact items.Length)) |> System.Text.Json.JsonDocument.Parse
          let serialized =
            doc.RootElement.GetProperty("completions").EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("label").GetString())
            |> Seq.toList
          serialized = labels

      testCase "WHY — the payload states the size of the set and whether it is exact, so an editor can say '50 of 212' instead of presenting 50 as everything" <| fun _ ->
        let items = [ { DisplayText = "a"; ReplacementText = "a"; Kind = CompletionKind.Method; GetDescription = None } ]
        use exact = formatCompletionsJson (CompletionAnswer.Answered(items, CompletionTotal.Exact 212)) |> System.Text.Json.JsonDocument.Parse
        exact.RootElement.GetProperty("count").GetInt32() |> Expect.equal "count is the items in this reply" 1
        exact.RootElement.GetProperty("total").GetInt32() |> Expect.equal "total is the size of the set" 212
        exact.RootElement.GetProperty("totalKind").GetString() |> Expect.equal "an exact total says so" "exact"
        use floor = formatCompletionsJson (CompletionAnswer.Answered(items, CompletionTotal.AtLeast 2000)) |> System.Text.Json.JsonDocument.Parse
        floor.RootElement.GetProperty("totalKind").GetString() |> Expect.equal "a floor says so" "atLeast"

      testCase "WHY — an unavailable answer carries no items and says why, so an editor can show the reason and not an empty popup" <| fun _ ->
        use doc =
          formatCompletionsJson (CompletionAnswer.Unavailable CompletionUnavailable.SessionNotActive)
          |> System.Text.Json.JsonDocument.Parse
        doc.RootElement.GetProperty("completions").GetArrayLength() |> Expect.equal "no items" 0
        doc.RootElement.GetProperty("unavailable").GetString() |> Expect.equal "the stable word for the reason" "SessionNotActive"
        doc.RootElement.GetProperty("reason").GetString() |> Expect.isNotEmpty "and what to do about it"
    ]
  ]
