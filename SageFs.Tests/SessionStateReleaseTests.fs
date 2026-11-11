/// A stopped session's eval history and compilation state are released, so a daemon that lives for days does
/// not keep the history of every session id it ever served.
module SageFs.Tests.SessionStateReleaseTests

open System
open Expecto
open Expecto.Flip
open SageFs

/// `SageFs.McpTools` (Mcp.fs), spelled out because `SageFs.Server.McpTools` is a different module with the same name.
module Tools = SageFs.McpTools

[<Tests>]
let sessionStateTests =
  testList "per-session tool state" [
    testTask "WHY — stop_session — the stopped session's eval history is released" {
      let sessionId = sprintf "release-on-stop-%s" (Guid.NewGuid().ToString("N"))
      McpSessionState.featureStates.TryAdd(sessionId, ref Features.FeatureHooks.FeaturePushState.empty)
      |> Expect.isTrue "the fixture holds an eval history for the session"
      let! _ = Tools.stopSession (TestInfrastructure.sharedCtx ()) sessionId
      McpSessionState.featureStates.ContainsKey sessionId
      |> Expect.isFalse "stopping the session frees its EvalStore entry"
    }

    testCase "WHY — releaseSessionState — it frees the history, the compilation context and the type diagnostic, and only that session's" <| fun _ ->
      let gone = sprintf "gone-%s" (Guid.NewGuid().ToString("N"))
      let kept = sprintf "kept-%s" (Guid.NewGuid().ToString("N"))
      for sessionId in [ gone; kept ] do
        McpSessionState.featureStates.TryAdd(sessionId, ref Features.FeatureHooks.FeaturePushState.empty) |> ignore
        McpSessionState.compilationStates.TryAdd(sessionId, Middleware.CompilationContext.CompilationState.empty) |> ignore
        McpSessionState.typeIdentityDiagnostics.TryAdd(sessionId, "diagnostic") |> ignore
      McpSessionState.releaseSessionState gone
      McpSessionState.featureStates.ContainsKey gone |> Expect.isFalse "history released"
      McpSessionState.compilationStates.ContainsKey gone |> Expect.isFalse "compilation context released"
      McpSessionState.typeIdentityDiagnostics.ContainsKey gone |> Expect.isFalse "type diagnostic released"
      McpSessionState.featureStates.ContainsKey kept |> Expect.isTrue "another session's history stays"
      McpSessionState.releaseSessionState kept
  ]
