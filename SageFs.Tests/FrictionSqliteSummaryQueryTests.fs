module SageFs.Tests.FrictionSqliteSummaryQueryTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionTelemetry
open SageFs.Features.FrictionSqlite

let private ok = function
  | Ok value -> value
  | Error err -> failtestf "expected success, got error: %s" err

let private tool text = ToolName.create text |> ok
let private session text = SessionRef.create text |> ok
let private duration value = DurationMs.create value |> ok

let private mkEvent blocker =
  { OccurredAtUtc = DateTimeOffset.UtcNow
    Session = session "session-1"
    Tool = tool "run_tests"
    Intent = IntentKind.RunExactTest
    Outcome = FrictionOutcome.EncounteredBlocker blocker
    Duration = duration 6
    FollowUp = FollowUp.NoFollowUpYet
    ContextCost = ContextCost.Focused
    SageFsVersion = "" }

[<Tests>]
let tests =
  testSequenced (testList "Friction SQLite summary queries" [
    testCase "stored events still produce blocker summaries after readback" <| fun _ ->
      let dbPath = Path.Combine(Path.GetTempPath(), sprintf "sagefs-friction-summary-%s.db" (Guid.NewGuid().ToString("N")))
      let conn = sprintf "Data Source=%s" dbPath
      let store = Store.create conn
      store.Initialize() |> ok |> ignore
      store.AppendEvent (mkEvent BlockerKind.ExactTestNotFound) |> ok |> ignore
      store.AppendEvent (mkEvent BlockerKind.ExactTestNotFound) |> ok |> ignore
      let summaries = store.ReadEvents() |> ok |> Summaries.topBlockers
      summaries
      |> Expect.equal
        "readback should support the same summary logic"
        [ { Blocker = BlockerKind.ExactTestNotFound; Count = 2; MostAffectedTools = [ tool "run_tests" ] } ]
      try File.Delete dbPath with _ -> ()
  ])
