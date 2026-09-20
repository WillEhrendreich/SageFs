/// Minimal Expecto fixture for SageFs.Tests/McpToolOutcomeTests.fs
/// (outcome-gate-sweep.md Island D, Gap D — the MCP agent read-path:
/// list_tests / explain_test_failure / diagnose).
module SageFs.Tests.Fixtures.McpToolOutcome.Sample

open Expecto
open Expecto.Flip

let add a b = a + b

// ┌─ MCP-TOOL-OUTCOME DEMO: McpToolOutcomeTests.fs edits this line on disk ─┐
// │ (flips `a - b` to `a - b + 1`) to produce a real Passed→Failed         │
// │ transition for explain_test_failure/diagnose to report on — mirroring │
// │ HttpApiIntegrationTests.fs's own Hello.fs "add"/"add + 1" trick, but   │
// │ against this file's own `subtract` so no two test files ever edit the │
// │ same line of the same file.                                           │
let subtract a b = a - b
// └───────────────────────────────────────────────────────────────────────┘

let tests = testList "mcp tool outcome fixture" [
  testList "arithmetic" [
    test "add computes the sum" {
      add 2 3 |> Expect.equal "2 + 3 = 5" 5
    }
    test "subtract computes the difference" {
      subtract 10 4 |> Expect.equal "10 - 4 = 6" 6
    }
  ]
  testList "identity" [
    test "add zero is identity" {
      add 7 0 |> Expect.equal "7 + 0 = 7" 7
    }
  ]
]
