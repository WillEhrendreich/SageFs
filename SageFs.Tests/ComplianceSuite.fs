module SageFs.Tests.ComplianceSuite

open Expecto

// ============================================================
// Compliance Test Suite
// ============================================================
// Curated set of behavioral contracts that define what a correct
// SageFs implementation must satisfy. Run independently via:
//   dotnet run --project SageFs.Tests -- --compliance
//
// Categories:
//   1. Protocol Surface — MCP tool registration, endpoint contracts
//   2. Serialization Contracts — JSON/text output stability
//   3. Elm Architecture Laws — determinism, commutativity, totality
//   4. Domain Invariants — type safety, monoid laws, algebraic properties

[<Tests>]
let complianceSuite = testList "[Compliance] Behavioral contracts" [

  testList "Protocol surface" [
    McpWireProtocolTests.sessionEventSerializationTests
    EndpointContractTests.endpointContractTests
  ]

  testList "Plugin output contracts" [
    PluginContractTests.sessionCtxRenderTests
    PluginContractTests.fileStatusRenderTests
    PluginContractTests.evalJsonTests
    PluginContractTests.formatStatusTests
    PluginContractTests.enhancedStatusTests
    PluginContractTests.startupInfoTests
    PluginContractTests.editorSplitTests
    PluginContractTests.completionTests
    PluginContractTests.completionJsonTests
    PluginContractTests.explorationJsonTests
    PluginContractTests.diagnosticsJsonTests
  ]

  testList "Elm architecture laws" [
    SageFsUpdatePropertyTests.sageFsUpdatePropertyTests
    MealyEquivalenceTests.mealyEquivalenceTests
  ]

  testList "Domain algebraic properties" [
    AlgebraicPropertyTests.allAlgebraicPropertyTests
    AffordancesPropertyTests.allAffordancesPropertyTests
    CellGridMonoidTests.cellOverlayTests
    CellGridMonoidTests.cellGridOverlayTests
    EventFoldPropertyTests.replayEquivalenceTests
    EventFoldPropertyTests.lastActivityMonotonicTests
    EventFoldPropertyTests.evalCountTests
    EventFoldPropertyTests.resetCountTests
    EventFoldPropertyTests.evalHistoryTests
    EventFoldPropertyTests.emptyStreamTests
    EventFoldPropertyTests.sessionEventRoundtripTests
  ]

  testList "Error handling contracts" [
    SageFsErrorPropertyTests.sageFsErrorPropertyTests
    McpFormatterPropertyTests.mcpFormatterPropertyTests
  ]

  testList "Lifecycle invariants" [
    LifecyclePropertyTests.tests
    BatchFlusherPropertyTests.batchFlusherPropertyTests
    SessionIdTests.sessionIdTests
  ]
]
