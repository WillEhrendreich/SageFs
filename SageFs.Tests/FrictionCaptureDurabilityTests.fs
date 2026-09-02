module SageFs.Tests.FrictionCaptureDurabilityTests

/// Phase 4 P0 RED test (quality-gap plan: "Friction Feedback Audit → P0
/// automatic-capture defect"): pre-wrapper MCP failures must be durably
/// captured. recordToolFailure previously built
///   task { ... } |> Async.AwaitTask |> ignore
/// — an Async that was never started — so the SQLite append never ran.
///
/// The plan's first RED test: invoke the real capture filter with a
/// throwing/binding-failure handler, reopen SQLite, and require exactly one
/// persisted friction event.

open System
open System.Collections.Concurrent
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server.McpServer
open SageFs.Features.FrictionSqlite
open SageFs.Tests.TestInfrastructure

let private mkCtxWithStore (store: FrictionStore option) : McpTools.McpContext =
  { FrictionStore = store
    DiagnosticsChanged = (new Event<Features.DiagnosticsStore.T>()).Publish
    StateChanged = None
    SessionOps = SessionManagementOps.stub
    SessionMap = ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = Some ignore
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    ActivityTracker = AgentActivityTracker.create()
    LiveSnapshotSink = None }

[<Tests>]
let frictionCaptureDurabilityTests =
  testList "Friction capture durability" [

    testCase "recordToolFailure with a throwing handler persists exactly one event across reopen" <| fun _ ->
      let store = tempFrictionStore ()
      let tracker = McpServerTracker()
      let ctx = mkCtxWithStore (Some store)

      // The failure path: an exception from a tool call (missing argument).
      let ex = ArgumentException("Missing or invalid argument: code", "code")
      recordToolFailure ctx tracker ex

      // Reopen the SQLite database (new connection over the same file) and
      // require exactly one persisted event — proof the append actually ran.
      let events = store.ReadEvents()
      match events with
      | Ok evts ->
        evts
        |> Expect.hasLength "exactly one friction event must be persisted" 1
        let e = evts.Head
        e.Tool |> Features.FrictionTelemetryTypes.ToolName.value
        |> Expect.equal "tool name should be captured from the exception" "unknown (missing 'code' argument)"
        e.Outcome
        |> Expect.equal "outcome should record the invalid-request blocker"
            (Features.FrictionTelemetryTypes.FrictionOutcome.EncounteredBlocker
               Features.FrictionTelemetryTypes.BlockerKind.InvalidRequest)
      | Error err -> failwithf "ReadEvents failed: %s" err

    testCase "recordToolFailure with no store is a no-op that never throws" <| fun _ ->
      // The exception path must never throw — even with no store configured.
      let tracker = McpServerTracker()
      let ctx = mkCtxWithStore None
      recordToolFailure ctx tracker (ArgumentException("boom", "code"))
      // Reaching this line proves the no-store path did not throw.
      ()
  ]
