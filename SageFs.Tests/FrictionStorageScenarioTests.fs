module SageFs.Tests.FrictionStorageScenarioTests

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

let private mkEvent () =
  { OccurredAtUtc = DateTimeOffset.UtcNow
    Session = session "session-1"
    Tool = tool "run_tests"
    Intent = IntentKind.RunExactTest
    Outcome = FrictionOutcome.EncounteredBlocker BlockerKind.ExactTestNotFound
    Duration = duration 10
    FollowUp = FollowUp.FollowedByTool (tool "list_tests")
    ContextCost = ContextCost.Focused }

let private mkFeedback () =
  { OccurredAtUtc = DateTimeOffset.UtcNow
    Session = session "session-1"
    Tool = tool "targeted_verify"
    Kind = ExplicitFeedbackKind.ResultDidNotEstablishTrust
    ShortReason = "Needed exact loaded-state proof"
    AlternativeUsed = AlternativePath.ResolvedWithTool (tool "get_fsi_status") }

[<Tests>]
let tests =
  testSequenced (testList "Friction storage scenarios" [
    testCase "friction events survive reopening because workflow pain should remain inspectable" <| fun _ ->
      let dbPath = Path.Combine(Path.GetTempPath(), sprintf "sagefs-friction-%s.db" (Guid.NewGuid().ToString("N")))
      let conn = sprintf "Data Source=%s" dbPath
      let store = Store.create conn
      store.Initialize() |> ok |> ignore
      store.AppendEvent (mkEvent ()) |> ok |> ignore
      let reopened = Store.create conn
      reopened.ReadEvents() |> ok |> Expect.hasLength "the reopened store should preserve the event" 1
      try File.Delete dbPath with _ -> ()

    testCase "explicit feedback is read back exactly as recorded" <| fun _ ->
      let dbPath = Path.Combine(Path.GetTempPath(), sprintf "sagefs-feedback-%s.db" (Guid.NewGuid().ToString("N")))
      let conn = sprintf "Data Source=%s" dbPath
      let store = Store.create conn
      store.Initialize() |> ok |> ignore
      let feedback = mkFeedback ()
      store.AppendFeedback feedback |> ok |> ignore
      store.ReadFeedback()
      |> ok
      |> Expect.equal "feedback should round-trip" [ feedback ]
      try File.Delete dbPath with _ -> ()
  ])
