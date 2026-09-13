module SageFs.Tests.Round6HardeningTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features

// ---------------------------------------------------------------------------
// W5 — ManifestPersistence payloadEnd uses List.min instead of List.tryHead
// ---------------------------------------------------------------------------
// Bug: payloadEnd used List.tryHead on a filtered+mapped list of directory offsets.
//      If sections are stored out of sorted order, tryHead picks the wrong boundary.
// Fix: List.min finds the true nearest upper bound regardless of insertion order.

open SageFs.Features.ManifestTypes

let private makeManifestWithTwoEntries () =
  { DaemonManifestData.Entries =
      [ { ManifestTypes.ManifestSessionEntry.SessionId = "sess-aaa"
          Projects = ["a.fsproj"]
          WorkingDir = "C:\\a"
          CreatedAt = DateTimeOffset.UtcNow
          StoppedAt = None }
        { ManifestTypes.ManifestSessionEntry.SessionId = "sess-bbb"
          Projects = ["b.fsproj"]
          WorkingDir = "C:\\b"
          CreatedAt = DateTimeOffset.UtcNow
          StoppedAt = None } ]
    DaemonManifestData.ActiveSessionId = Some "sess-aaa"
    DaemonManifestData.CreatedAtMs = 1234567890L }

[<Tests>]
let manifestPayloadBoundaryTests =
  testList "ManifestPersistence multi-section payload boundary" [

    testCase "round-trips two sessions without corruption" <| fun _ ->
      let data = makeManifestWithTwoEntries ()
      let bytes = ManifestWriter.write data
      bytes |> Expect.isNotNull "write should succeed"
      let roundTripped = ManifestReader.read bytes
      match roundTripped with
      | Error msg -> failwithf "read failed: %s" msg
      | Ok result ->
        result.Entries |> Expect.hasLength "should have 2 entries" 2

    testCase "round-tripped entry IDs match originals" <| fun _ ->
      let data = makeManifestWithTwoEntries ()
      let bytes = ManifestWriter.write data
      match ManifestReader.read bytes with
      | Error msg -> failwithf "read failed: %s" msg
      | Ok result ->
        result.Entries |> List.exists (fun e -> e.SessionId = "sess-aaa")
        |> Expect.isTrue "first session ID should survive round-trip"
        result.Entries |> List.exists (fun e -> e.SessionId = "sess-bbb")
        |> Expect.isTrue "second session ID should survive round-trip"

    testCase "ActiveSessionId survives round-trip" <| fun _ ->
      let data = makeManifestWithTwoEntries ()
      let bytes = ManifestWriter.write data
      match ManifestReader.read bytes with
      | Error msg -> failwithf "read failed: %s" msg
      | Ok result ->
        result.ActiveSessionId |> Expect.equal "ActiveSessionId should survive" (Some "sess-aaa")
  ]

// ---------------------------------------------------------------------------
// W10 — FeatureHooks KnownBindings is incrementally maintained (O(1) per eval)
// ---------------------------------------------------------------------------
// Bug: computeCellDepsPush rebuilt knownBindings by scanning all EvalHistory entries
//      on every SSE push — O(n) per push, O(n²) total.
// Fix: KnownBindings field on FeaturePushState, updated incrementally in recordEval.

open SageFs.Features.FeatureHooks

[<Tests>]
let knownBindingsIncrementalTests =
  testList "FeaturePushState.KnownBindings incremental update" [

    testCase "empty state has empty KnownBindings" <| fun _ ->
      FeaturePushState.empty.KnownBindings
      |> Map.isEmpty
      |> Expect.isTrue "empty state should have no bindings"

    testCase "recordEval with val binding updates KnownBindings" <| fun _ ->
      let result = "val x : int = 42"
      let state = recordEval "let x = 42" result 5L FeaturePushState.empty
      state.KnownBindings |> Map.containsKey "x"
      |> Expect.isTrue "binding 'x' should be in KnownBindings after eval"

    testCase "KnownBindings maps name to correct cell index" <| fun _ ->
      let s0 = recordEval "let a = 1" "val a : int = 1" 1L FeaturePushState.empty
      let s1 = recordEval "let b = 2" "val b : int = 2" 1L s0
      s1.KnownBindings |> Map.tryFind "a"
      |> Expect.equal "a should be cell 0" (Some 0)
      s1.KnownBindings |> Map.tryFind "b"
      |> Expect.equal "b should be cell 1" (Some 1)

    testCase "later binding with same name overwrites earlier (last-writer wins)" <| fun _ ->
      let s0 = recordEval "let x = 1" "val x : int = 1" 1L FeaturePushState.empty
      let s1 = recordEval "let x = 99" "val x : int = 99" 1L s0
      s1.KnownBindings |> Map.tryFind "x"
      |> Expect.equal "x should point to the latest cell" (Some 1)

    testCase "result without val lines does not add to KnownBindings" <| fun _ ->
      let s = recordEval "printfn \"hi\"" "hi" 1L FeaturePushState.empty
      s.KnownBindings |> Map.isEmpty
      |> Expect.isTrue "no val lines means no bindings added"

    testCase "computeCellDepsPush uses KnownBindings from state" <| fun _ ->
      let opts = System.Text.Json.JsonSerializerOptions()
      let s0 = recordEval "let z = 10" "val z : int = 10" 1L FeaturePushState.empty
      let s1 = recordEval "z + 1" "val it : int = 11" 1L s0
      // Just verify it doesn't throw and returns a result
      let newState, _ = computeCellDepsPush opts None s1
      newState.KnownBindings |> Map.containsKey "z"
      |> Expect.isTrue "z should still be in bindings after push"
  ]

