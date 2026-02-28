module SageFs.Vscode.TestControllerAdapter

open Fable.Core
open Fable.Core.JsInterop
open Vscode

open SageFs.Vscode.LiveTestingTypes
open SageFs.Vscode.JsHelpers

module Client = SageFs.Vscode.SageFsClient

// ── TestController lifecycle ─────────────────────────────────

type TestAdapter = {
  Controller: TestController
  Refresh: VscStateChange list -> unit
  Dispose: unit -> unit
}

let create
  (getClient: unit -> Client.Client option)
  : TestAdapter =

  let controller = Tests.createTestController "sagefs" "SageFs Live Tests"
  let testItemMap = System.Collections.Generic.Dictionary<string, TestItem>()
  let mutable activeRun: TestRun option = None
  let mutable endRunTimer: obj option = None

  let endActiveRun () =
    endRunTimer |> Option.iter jsClearInterval
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

  /// Schedule run.end() after 500ms debounce — resets on each new batch.
  let scheduleEndRun () =
    endRunTimer |> Option.iter jsClearInterval
    endRunTimer <- Some (jsSetTimeout (fun () -> endActiveRun ()) 500)

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
    let run = getOrCreateRun ()
    for result in results do
      let id = VscTestId.value result.Id
      match testItemMap.TryGetValue(id) with
      | true, item ->
        let durationMs = result.DurationMs |> Option.defaultValue 0.0
        match result.Outcome with
        | VscTestOutcome.Passed ->
          run.passed(item, durationMs)
        | VscTestOutcome.Failed msg ->
          let message = newTestMessage msg
          run.failed(item, message, durationMs)
        | VscTestOutcome.Skipped _ ->
          run.skipped item
        | VscTestOutcome.Running ->
          run.started item
        | VscTestOutcome.Errored msg ->
          let message = newTestMessage msg
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
          | Some items when items.Length > 0 -> items.[0].id
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

  let refresh (changes: VscStateChange list) =
    for change in changes do
      match change with
      | VscStateChange.TestsAdded tests ->
        for t in tests do
          ensureTestItem t |> ignore
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
    Dispose = fun () -> endActiveRun (); controller.dispose () }
