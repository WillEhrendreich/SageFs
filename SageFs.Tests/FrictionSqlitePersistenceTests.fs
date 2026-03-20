module SageFs.Tests.FrictionSqlitePersistenceTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionSqlite

let private ok = function
  | Ok value -> value
  | Error err -> failtestf "expected success, got error: %s" err

let private tool text = ToolName.create text |> ok
let private session text = SessionRef.create text |> ok
let private duration value = DurationMs.create value |> ok

let private mkEvent toolName outcome =
  { OccurredAtUtc = DateTimeOffset.UtcNow
    Session = session "session-1"
    Tool = tool toolName
    Intent = IntentKind.VerifyChangedBehavior
    Outcome = outcome
    Duration = duration 4
    FollowUp = FollowUp.SessionEnded
    ContextCost = ContextCost.Tiny }

[<Tests>]
let tests =
  testSequenced (testList "Friction SQLite persistence" [
    testCase "append and readback preserves friction outcome semantics" <| fun _ ->
      let dbPath = Path.Combine(Path.GetTempPath(), sprintf "sagefs-friction-roundtrip-%s.db" (Guid.NewGuid().ToString("N")))
      let conn = sprintf "Data Source=%s" dbPath
      let store = Store.create conn
      store.Initialize() |> ok |> ignore
      let event = mkEvent "run_tests" (FrictionOutcome.RecoveredVia ResolutionKind.SolvedAfterReset)
      store.AppendEvent event |> ok |> ignore
      store.ReadEvents()
      |> ok
      |> Expect.equal "event should round-trip with recovery semantics" [ event ]
      try File.Delete dbPath with _ -> ()
  ])
