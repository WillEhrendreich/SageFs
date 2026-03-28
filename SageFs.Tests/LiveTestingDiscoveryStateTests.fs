module SageFs.Tests.LiveTestingDiscoveryStateTests

open System
open System.Collections.Concurrent
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

let private mkCtxWithState (state: LiveTestState) : McpContext =
  let diagEvent = Event<Features.DiagnosticsStore.T>()
  let model =
    let initial = SageFsModel.initial()
    { initial with
        LiveTesting = { initial.LiveTesting with TestState = state } }
  { FrictionStore = None
    DiagnosticsChanged = diagEvent.Publish
    StateChanged = None
    SessionOps = SessionManagementOps.stub
    SessionMap = ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = Some ignore
    GetElmModel = Some (fun () -> model)
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    CancelAmbientTestRun = None
    ActivityTracker = SageFs.AgentActivityTracker.create() }

let private jsonBoolField (fieldName: string) (root: JsonElement) =
  root.GetProperty(fieldName).GetBoolean()

let private jsonStringField (fieldName: string) (root: JsonElement) =
  root.GetProperty(fieldName).GetString()

let private jsonStringArrayField (fieldName: string) (root: JsonElement) =
  root.GetProperty(fieldName).EnumerateArray() |> Seq.map (fun v -> v.GetString()) |> Seq.toArray

[<Tests>]
let tests =
  testList "Live testing discovery state" [
    test "inactive state is disabled" {
      LiveTestState.empty
      |> LiveTestState.discoveryState
      |> Expect.equal "inactive state should be disabled" LiveTestDiscoveryState.Disabled
    }

    test "active state with no discovery timestamp is discovering" {
      let state = { LiveTestState.empty with Activation = LiveTestingActivation.Active }
      state
      |> LiveTestState.discoveryState
      |> Expect.equal "active state should report discovery in progress before first discovery event" LiveTestDiscoveryState.Discovering
    }

    test "active state with zero tests after discovery is ready_zero_tests" {
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            LastDiscoveryTime = DateTimeOffset.UtcNow }

      state
      |> LiveTestState.discoveryState
      |> Expect.equal "active state should distinguish zero discovered tests after discovery ran" LiveTestDiscoveryState.ReadyZeroTests
    }

    test "active state with discovered tests is ready_with_tests" {
      let testCase = mkTestCase "sample test" TestFramework.Expecto TestCategory.Unit
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            DiscoveredTests = [| testCase |] }

      state
      |> LiveTestState.discoveryState
      |> Expect.equal "discovered tests should surface a ready_with_tests state" (LiveTestDiscoveryState.ReadyWithTests 1)
    }

    test "wire values are stable" {
      let cases =
        [ LiveTestDiscoveryState.Disabled, "disabled"
          LiveTestDiscoveryState.Discovering, "discovering"
          LiveTestDiscoveryState.ReadyZeroTests, "ready_zero_tests"
          LiveTestDiscoveryState.ReadyWithTests 2, "ready_with_tests" ]

      for discoveryState, expected in cases do
        discoveryState
        |> LiveTestDiscoveryState.toWireValue
        |> Expect.equal (sprintf "wire value should be pinned for %A" discoveryState) expected
    }

    test "discovering hint stays generic without state context" {
      LiveTestDiscoveryState.Discovering
      |> LiveTestDiscoveryState.hint
      |> Expect.equal "generic discovery hint should not assume priming is required" "Live testing is active and discovery is still in progress."
    }

    test "active state with queued discovery does not require priming eval" {
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            PendingDiscoverySessions = Set.ofList ["session-1"] }

      state
      |> LiveTestState.requiresPrimingEval
      |> Expect.isFalse "queued discovery should clear the priming requirement"
    }
  ]

[<Tests>]
let mcpDiscoverySemanticsTests =
  testList "Live testing MCP discovery semantics" [
    testTask "enable_live_testing explains automatic async cold-start discovery" {
      let ctx = mkCtxWithState LiveTestState.empty

      let! response = setLiveTesting ctx true

      response |> Expect.stringContains "enable should point callers at status" "get_live_test_status"
      response |> Expect.stringContains "enable should mention automatic initial discovery" "Initial discovery"
      response.Contains("no-op eval")
      |> Expect.isFalse "enable should no longer claim that callers must prime discovery with a no-op eval"
    }

    testTask "get_live_test_status reports pending discovery without eval priming" {
      let state = { LiveTestState.empty with Activation = LiveTestingActivation.Active }
      let ctx = mkCtxWithState state

      let! (json: string) = getLiveTestStatus ctx "copilot" None

      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root |> jsonStringField "DiscoveryState"
      |> Expect.equal "cold active session should still be discovering" "discovering"
      root |> jsonBoolField "DiscoveryRequiresEval"
      |> Expect.isFalse "cold active session should no longer require eval priming"
      root |> jsonStringField "DiscoveryHint"
      |> Expect.equal "status hint should stay generic while discovery is pending" "Live testing is active and discovery is still in progress."
    }

    testTask "get_live_test_status clears the priming requirement once discovery is queued" {
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            PendingDiscoverySessions = Set.ofList ["session-1"] }
      let ctx = mkCtxWithState state

      let! (json: string) = getLiveTestStatus ctx "copilot" None

      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root |> jsonStringField "DiscoveryState"
      |> Expect.equal "queued discovery should still report discovering" "discovering"
      root |> jsonBoolField "DiscoveryRequiresEval"
      |> Expect.isFalse "queued discovery should not require a priming eval"
      root |> jsonStringField "DiscoveryHint"
      |> fun hint -> hint.Contains("no-op eval")
      |> Expect.isFalse "queued discovery hint should no longer mention no-op eval priming"
    }

    testTask "get_test_trace reports pending discovery without eval priming" {
      let state = { LiveTestState.empty with Activation = LiveTestingActivation.Active }
      let ctx = mkCtxWithState state

      let! (json: string) = getTestTrace ctx

      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root |> jsonStringField "DiscoveryState"
      |> Expect.equal "trace should report discovery in progress" "discovering"
      root |> jsonBoolField "DiscoveryRequiresEval"
      |> Expect.isFalse "trace should no longer surface priming requirement"
      root |> jsonStringField "DiscoveryHint"
      |> Expect.equal "trace hint should stay generic while discovery is pending" "Live testing is active and discovery is still in progress."
    }

    testTask "get_test_trace clears the priming requirement once discovery is queued" {
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            PendingDiscoverySessions = Set.ofList ["session-1"] }
      let ctx = mkCtxWithState state

      let! (json: string) = getTestTrace ctx

      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root |> jsonStringField "DiscoveryState"
      |> Expect.equal "trace should still report discovery in progress" "discovering"
      root |> jsonBoolField "DiscoveryRequiresEval"
      |> Expect.isFalse "trace should clear priming once discovery is queued"
      root |> jsonStringField "DiscoveryHint"
      |> fun hint -> hint.Contains("no-op eval")
      |> Expect.isFalse "trace hint should no longer mention no-op eval when discovery is already queued"
    }

    testTask "get_live_test_status surfaces the last decision so callers can see when a rerun was exact versus conservative" {
      let decision =
        LiveTestingDecision.fromSelection
          (RerunCause.FileSaved "src/Module.fs")
          SelectionPrecision.ConservativeFallback
          [ "Module.add" ]
          [| "Module.Tests.should_add" |]
          [| "Architecture.Tests.should_hold" |]
          "The dependency graph could not narrow this compiled-file change, so SageFs conservatively queued all discovered tests behind a rebuild."
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            LastDecision = Some decision }
      let ctx = mkCtxWithState state

      let! (json: string) = getLiveTestStatus ctx "copilot" None

      let doc = JsonDocument.Parse(json)
      try
        let root = doc.RootElement
        let lastDecision = root.GetProperty("LastDecision")
        lastDecision |> jsonStringField "Precision"
        |> Expect.equal "status should expose fallback precision explicitly" "conservative_fallback"
        lastDecision |> jsonStringField "Trust"
        |> Expect.equal "status should expose approximate trust explicitly" "fresh_approximate"
        lastDecision |> jsonStringField "Cause"
        |> Expect.equal "status should expose the rerun cause" "file_saved"
        lastDecision |> jsonStringField "FilePath"
        |> Expect.equal "status should preserve the changed file path" "src/Module.fs"
        lastDecision |> jsonStringArrayField "ChangedSymbols"
        |> Expect.equal "status should preserve changed symbols" [| "Module.add" |]
        lastDecision |> jsonStringArrayField "SelectedTests"
        |> Expect.equal "status should preserve selected tests" [| "Module.Tests.should_add" |]
        lastDecision |> jsonStringArrayField "DeferredTests"
        |> Expect.equal "status should preserve deferred tests" [| "Architecture.Tests.should_hold" |]
      finally
        doc.Dispose()
    }

    testTask "get_test_trace surfaces the last decision so ambient status can explain why live testing stayed quiet" {
      let decision =
        LiveTestingDecision.fromSelection
          (RerunCause.KeystrokeBuffered "src/Architecture.fs")
          SelectionPrecision.SuppressedByPolicy
          [ "Architecture.Rule" ]
          [||]
          [| "Architecture.Tests.should_hold" |]
          "Affected tests were intentionally deferred by the current run policy, so ambient live testing stayed quiet on purpose."
      let state =
        { LiveTestState.empty with
            Activation = LiveTestingActivation.Active
            LastDecision = Some decision }
      let ctx = mkCtxWithState state

      let! (json: string) = getTestTrace ctx

      let doc = JsonDocument.Parse(json)
      try
        let root = doc.RootElement
        let lastDecision = root.GetProperty("LastDecision")
        lastDecision |> jsonStringField "Precision"
        |> Expect.equal "trace should expose suppression precision" "suppressed_by_policy"
        lastDecision |> jsonStringField "Trust"
        |> Expect.equal "trace should expose suppressed trust" "suppressed"
        lastDecision |> jsonStringField "Cause"
        |> Expect.equal "trace should expose buffered keystroke cause" "keystroke_buffered"
        lastDecision |> jsonStringField "Reason"
        |> Expect.stringContains "trace should preserve the human explanation" "stayed quiet on purpose"
      finally
        doc.Dispose()
    }
  ]
