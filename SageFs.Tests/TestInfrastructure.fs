module SageFs.Tests.TestInfrastructure

open SageFs.ActorCreation
open SageFs.AppState
open SageFs.McpTools

let quietLogger =
  { new SageFs.Utils.ILogger with
      member _.LogDebug msg = ()
      member _.LogInfo msg = ()
      member _.LogError msg = ()
      member _.LogWarning msg = ()
  }

/// Shared Marten store for tests — uses the same Testcontainer as EventStoreTests
let testStore = lazy(
  let container = EventStoreTests.sharedContainer.Value
  SageFs.EventStore.configureStore (container.GetConnectionString())
)

/// Single shared actor result for all read-only tests across the entire test suite.
/// Created once on first access, reused everywhere.
let globalActorResult = lazy(
  let args = mkCommonActorArgs quietLogger false ignore []
  createActor args |> Async.AwaitTask |> Async.RunSynchronously
)

/// Create a McpContext backed by the global shared actor and Marten store
let sharedCtx () =
  let result = globalActorResult.Value
  let sessionId = SageFs.EventStore.createSessionId ()
  { McpContext.Actor = result.Actor; Store = testStore.Value; SessionId = sessionId; DiagnosticsChanged = result.DiagnosticsChanged; StateChanged = None; CancelEval = result.CancelEval; GetSessionState = result.GetSessionState; GetEvalStats = result.GetEvalStats; GetWarmupFailures = result.GetWarmupFailures; GetStartupConfig = result.GetStartupConfig; Mode = SageFs.SessionMode.Embedded; Dispatch = None; GetElmModel = None; GetElmRegions = None }

/// Create a McpContext with a custom session ID backed by the global shared actor
let sharedCtxWith sessionId =
  let result = globalActorResult.Value
  { McpContext.Actor = result.Actor; Store = testStore.Value; SessionId = sessionId; DiagnosticsChanged = result.DiagnosticsChanged; StateChanged = None; CancelEval = result.CancelEval; GetSessionState = result.GetSessionState; GetEvalStats = result.GetEvalStats; GetWarmupFailures = result.GetWarmupFailures; GetStartupConfig = result.GetStartupConfig; Mode = SageFs.SessionMode.Embedded; Dispatch = None; GetElmModel = None; GetElmRegions = None }
