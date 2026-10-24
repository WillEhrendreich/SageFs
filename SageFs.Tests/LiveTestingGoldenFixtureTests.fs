module SageFs.Tests.LiveTestingGoldenFixtureTests

open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip

let private repoRoot = RepoPaths.repoPathFull [||]

let private fixturePath name =
  Path.Combine(repoRoot, "SageFs.Tests", "fixtures", "LiveTesting", name)

let private readFixture name = File.ReadAllText(fixturePath name)

[<Tests>]
let tests =
  testList "Live testing golden fixtures" [
    testCase "summary fixture preserves the latest fallback decision contract for downstream clients" <| fun _ ->
      let json = readFixture "summary-with-fallback-decision.json"
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root.GetProperty("Total").GetInt32() |> Expect.equal "total should stay stable" 5
      let decision = root.GetProperty("LastDecision")
      decision.GetProperty("Precision").GetString() |> Expect.equal "precision should stay stable" "conservative_fallback"
      decision.GetProperty("Trust").GetString() |> Expect.equal "trust should stay stable" "fresh_approximate"
      decision.GetProperty("Reason").GetString() |> Expect.equal "reason should stay stable" "fallback rebuild"

    testCase "results batch fixture preserves the latest coverage-widened decision contract for downstream clients" <| fun _ ->
      let json = readFixture "results-batch-with-coverage-decision.json"
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      // The list is a fetch, not a frame: `entries` moved behind GET /api/live-testing/entries so a
      // frame's size tracks the run rather than the suite. What a downstream client still renders
      // from the wire itself is the tally and the decision, so those are what this pins — and the
      // absence of the list is asserted rather than skipped, because it is the contract.
      let mutable holder = Unchecked.defaultof<System.Text.Json.JsonElement>
      root.TryGetProperty("Entries", &holder)
      |> Expect.isFalse "the frame must not carry a list anymore; it is fetched"
      let decision = root.GetProperty("LastDecision")
      decision.GetProperty("Cause").GetString() |> Expect.equal "cause should stay stable" "keystroke_buffered"
      decision.GetProperty("Precision").GetString() |> Expect.equal "precision should stay stable" "coverage_approximation"
      decision.GetProperty("SelectedTests").GetArrayLength() |> Expect.equal "selected tests should stay stable" 2

    testCase "summary fixture preserves the latest suppressed-by-policy decision contract for downstream clients" <| fun _ ->
      let json = readFixture "summary-with-suppressed-decision.json"
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root.GetProperty("Stale").GetInt32() |> Expect.equal "stale count should stay stable" 2
      let decision = root.GetProperty("LastDecision")
      decision.GetProperty("Precision").GetString() |> Expect.equal "precision should stay stable" "suppressed_by_policy"
      decision.GetProperty("Trust").GetString() |> Expect.equal "trust should stay stable" "suppressed"
      decision.GetProperty("DeferredTests").GetArrayLength() |> Expect.equal "deferred test count should stay stable" 1
  ]
