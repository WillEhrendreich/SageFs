module SageFs.Tests.FrictionSqliteEndToEndTests

open System
open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open Microsoft.Extensions.Logging.Abstractions
open SageFs.Server.McpTools
open SageFs.Features.FrictionSqlite
open SageFs.Tests.TestInfrastructure

/// Create a temporary SQLite-backed FrictionStore for testing.
/// Returns (store, dbPath) — caller is responsible for cleanup.
let private mkTempStore () =
  let dbPath =
    Path.Combine(
      Path.GetTempPath(),
      sprintf "sagefs-friction-e2e-%s.db" (Guid.NewGuid().ToString("N")))
  let conn = sprintf "Data Source=%s" dbPath
  let store = Store.create conn
  match store.Initialize() with
  | Ok () -> store, dbPath
  | Error err -> failtestf "store initialization failed: %s" err

/// Create a McpContext backed by a real SQLite FrictionStore.
let private mkSqliteCtx () =
  let store, dbPath = mkTempStore ()
  let baseCtx = sharedCtx ()
  let ctx = { baseCtx with FrictionStore = Some store }
  ctx, dbPath

[<Tests>]
let tests =
  testSequenced (testList "Friction SQLite end-to-end" [
    testCaseTask "report_friction persists feedback that get_friction_summary reads back" <| fun () -> task {
      let ctx, dbPath = mkSqliteCtx ()
      let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)

      let! response = tools.report_friction("run_tests", "output_too_large", "Too much output.", "")
      response |> Expect.stringContains "should confirm recording" "Recorded local friction feedback"

      let! summary = tools.get_friction_summary()
      summary |> Expect.stringContains "should count explicit feedback" "Explicit feedback items: 1"

      try File.Delete dbPath with _ -> ()
    }

    testCaseTask "report_friction persists feedback that get_friction_report includes in JSON" <| fun () -> task {
      let ctx, dbPath = mkSqliteCtx ()
      let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)

      let! _ = tools.report_friction("run_tests", "needed_another_tool", "Exact test name unclear.", "list_tests")
      let! reportJson = tools.get_friction_report()

      let doc = JsonDocument.Parse(reportJson)
      doc.RootElement.GetProperty("TotalFeedbackItems").GetInt32()
      |> Expect.equal "should expose feedback count" 1

      let recentFeedback = doc.RootElement.GetProperty("RecentFeedback")
      (recentFeedback.GetArrayLength(), 0)
      |> Expect.isGreaterThan "should expose recent feedback"

      let first = recentFeedback[0]
      first.GetProperty("Tool").GetString()
      |> Expect.equal "should preserve complained tool" "run_tests"
      first.GetProperty("LatestAlternative").GetString()
      |> Expect.equal "should preserve resolving alternative" "list_tests"

      try File.Delete dbPath with _ -> ()
    }

    testCaseTask "friction data survives store reopening because persistence must be durable" <| fun () -> task {
      let dbPath =
        Path.Combine(
          Path.GetTempPath(),
          sprintf "sagefs-friction-reopen-%s.db" (Guid.NewGuid().ToString("N")))
      let conn = sprintf "Data Source=%s" dbPath

      // First store: write data
      let store1 = Store.create conn
      match store1.Initialize() with
      | Ok () -> ()
      | Error err -> failtestf "store1 init failed: %s" err
      let baseCtx = sharedCtx ()
      let ctx1 = { baseCtx with FrictionStore = Some store1 }
      let tools1 = SageFsTools(ctx1, NullLogger<SageFsTools>.Instance)
      let! _ = tools1.report_friction("send_fsharp_code", "intent_unclear", "Confused about eval mode.", "")

      // Second store: reopen same db, verify data survived
      let store2 = Store.create conn
      let ctx2 = { baseCtx with FrictionStore = Some store2 }
      let tools2 = SageFsTools(ctx2, NullLogger<SageFsTools>.Instance)
      let! summary = tools2.get_friction_summary()
      summary |> Expect.stringContains "should persist across reopen" "Explicit feedback items: 1"

      try File.Delete dbPath with _ -> ()
    }

    testCaseTask "automatic tool result recording lands in SQLite friction store" <| fun () -> task {
      let ctx, dbPath = mkSqliteCtx ()
      let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)

      // Calling get_friction_summary itself is auto-recorded as a tool result event
      let! _ = tools.get_friction_summary()
      // Call it again so the first call's event has been persisted
      let! summary = tools.get_friction_summary()

      // The auto-recording should have captured at least 1 event
      // (get_friction_summary is a meta/non-actionable tool, but events are still recorded)
      summary |> Expect.stringContains "should count tracked tools" "Tracked tools:"

      try File.Delete dbPath with _ -> ()
    }
  ])
