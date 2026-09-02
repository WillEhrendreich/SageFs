module SageFs.Vscode.TestControllerAdapter

open Fable.Core
open Fable.Core.JsInterop
open Vscode

open SageFs.Vscode.LiveTestingTypes
open SageFs.Vscode.JsHelpers
open SageFs.Vscode.SafeInterop

module Client = SageFs.Vscode.SageFsClient

// ── TestController lifecycle ─────────────────────────────────

let [<Literal>] private EndRunDebounceMs = 2000

type TestAdapter = {
  Controller: TestController
  Refresh: VscStateChange list -> unit
  RefreshNarratives: unit -> unit
  UpdateSourceLocations: obj array -> unit
  Reset: unit -> unit
  Dispose: unit -> unit
}

let create
  (getClient: unit -> Client.Client option)
  (getNarratives: unit -> Map<string, VscFailureNarrative>)
  : TestAdapter =

  let controller = Tests.createTestController "sagefs" "SageFs Live Tests"
  let testItemMap = System.Collections.Generic.Dictionary<string, TestItem>()
  let mutable activeRun: TestRun option = None
  let mutable endRunTimer: obj option = None
  let mutable lastResults: VscTestResult array = [||]

  let endActiveRun () =
    endRunTimer |> Option.iter jsClearTimeout
    endRunTimer <- None
    activeRun |> Option.iter (fun r -> r.``end`` ())
    activeRun <- None

  let getOrCreateRun () =
    match activeRun with
    | Some run -> run
    | None ->
      let request = createObj [ "include" ==> null; "exclude" ==> null ] :?> TestRunRequest
      let run = controller.createTestRun request
      activeRun <- Some run
      run

  /// Schedule run.end() after debounce — resets on each new batch.
  let scheduleEndRun () =
    endRunTimer |> Option.iter jsClearTimeout
    endRunTimer <- Some (jsSetTimeout (fun () -> endActiveRun ()) EndRunDebounceMs)

  let ensureTestItem (info: VscTestInfo) =
    let id = VscTestId.value info.Id
    match testItemMap.TryGetValue(id) with
    | true, item ->
      item.label <- info.DisplayName
      item
    | false, _ ->
      let uri =
        info.FilePath |> Option.map uriFile
      let item =
        match uri with
        | Some u -> controller.createTestItem(id, info.DisplayName, u)
        | None -> controller.createTestItem(id, info.DisplayName)
      match info.Line with
      | Some line ->
        item.range <- Some (newRange (line - 1) 0 (line - 1) 0)
      | None -> ()
      controller.items.add item
      testItemMap.[id] <- item
      item

  let applyResults (results: VscTestResult array) =
    lastResults <- results
    let run = getOrCreateRun ()
    let narratives = getNarratives ()
    for result in results do
      let id = VscTestId.value result.Id
      match testItemMap.TryGetValue(id) with
      | true, item ->
        let durationMs = result.DurationMs |> Option.defaultValue 0.0
        match result.Outcome with
        | VscTestOutcome.Passed ->
          run.passed(item, durationMs)
        | VscTestOutcome.Failed msg ->
          let enrichedMsg =
            match Map.tryFind id narratives with
            | Some n when n.Summary <> "" -> sprintf "%s\nℹ️ %s" msg n.Summary
            | _ -> msg
          let message = newTestMessage enrichedMsg
          run.failed(item, message, durationMs)
        | VscTestOutcome.Skipped _ ->
          run.skipped item
        | VscTestOutcome.Running ->
          run.started item
        | VscTestOutcome.Errored msg ->
          let enrichedMsg =
            match Map.tryFind id narratives with
            | Some n when n.Summary <> "" -> sprintf "%s\nℹ️ %s" msg n.Summary
            | _ -> msg
          let message = newTestMessage enrichedMsg
          run.failed(item, message, durationMs)
        | VscTestOutcome.Stale ->
          run.skipped item
        | VscTestOutcome.PolicyDisabled ->
          run.skipped item
      | false, _ -> ()
    scheduleEndRun ()

  let runHandler (request: TestRunRequest) (_token: CancellationToken) : JS.Promise<unit> =
    promise {
      match getClient () with
      | None -> ()
      | Some c ->
        let pattern =
          match request.``include`` with
          | Some items when items.Length > 0 ->
            match tryOfObj items.[0] with
            | Some item -> item.id
            | None -> ""
          | _ -> ""
        let! _result = Client.runTests pattern c
        ()
    }

  let _runProfile =
    controller.createRunProfile(
      "Run Tests",
      TestRunProfileKind.Run,
      (fun req token -> runHandler req token),
      true)

  let updateSourceLocations (locations: obj array) =
    for loc in locations do
      let testName = fieldString "TestName" loc |> Option.defaultValue ""
      let filePath = fieldString "FilePath" loc
      let startLine = fieldInt "StartLine" loc
      match testName with
      | "" -> ()
      | _ ->
        let matchingItem =
          testItemMap
          |> Seq.tryFind (fun kvp ->
            kvp.Key = testName || kvp.Value.label = testName)
          |> Option.map (fun kvp -> kvp.Value)
        match matchingItem, filePath with
        | Some item, Some fp ->
          item.uri <- Some (uriFile fp)
          match startLine with
          | Some line -> item.range <- Some (newRange (line - 1) 0 (line - 1) 0)
          | None -> ()
        | _ -> ()

  let refresh (changes: VscStateChange list) =
    for change in changes do
      match change with
      | VscStateChange.TestsAdded tests ->
        for t in tests do
          ensureTestItem t |> ignore
      | VscStateChange.TestsRemoved ids ->
        // Rediscovery sweep: drop the stale TestItems so the Test Explorer
        // and gutter decorations stop showing renamed/deleted tests.
        for id in ids do
          let idStr = VscTestId.value id
          match testItemMap.TryGetValue(idStr) with
          | true, _ ->
            controller.items.delete idStr
            testItemMap.Remove(idStr) |> ignore
          | false, _ -> ()
      | VscStateChange.TestsCompleted results ->
        applyResults results
      | VscStateChange.TestsStarted ids ->
        endActiveRun ()
        let run = getOrCreateRun ()
        for id in ids do
          let idStr = VscTestId.value id
          match testItemMap.TryGetValue(idStr) with
          | true, item -> run.started item
          | false, _ -> ()
      | _ -> ()

  { Controller = controller
    Refresh = refresh
    RefreshNarratives = fun () ->
      match lastResults with
      | [||] -> ()
      | results -> applyResults results
    UpdateSourceLocations = updateSourceLocations
    Reset = fun () ->
      endActiveRun ()
      testItemMap.Clear ()
      controller.items.replace [||]
    Dispose = fun () -> endActiveRun (); controller.dispose () }
