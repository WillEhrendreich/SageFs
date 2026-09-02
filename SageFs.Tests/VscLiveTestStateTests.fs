module SageFs.Tests.VscLiveTestStateTests

open Expecto
open SageFs.Vscode.LiveTestingTypes

let private mkTestId (s: string) = VscTestId.create s

let private mkInfo id =
  { Id = mkTestId id
    DisplayName = id
    FullName = "Suite." + id
    FilePath = Some ("/proj/" + id + ".fs")
    Line = Some 1 }

let private mkResult id outcome =
  { Id = mkTestId id
    Outcome = outcome
    DurationMs = Some 1.0
    Output = None }

[<Tests>]
let tests = testList "VscLiveTestState contract tests" [
  testCase "empty state produces zero summary" (fun () ->
    let s = VscLiveTestState.summary VscLiveTestState.empty
    Expect.equal s.Total 0 "total 0"
    Expect.equal s.Passed 0 "passed 0"
    Expect.equal s.Failed 0 "failed 0"
    Expect.equal s.Running 0 "running 0"
    Expect.equal s.Stale 0 "stale 0"
    Expect.equal s.Disabled 0 "disabled 0")

  testCase "partial discovery adds tests without sweeping existing ones" (fun () ->
    let prior =
      { VscLiveTestState.empty with
          Tests = [| mkInfo "A" |] |> Array.map (fun t -> t.Id, t) |> Map.ofArray }
    let next, changes =
      VscLiveTestState.update
        (VscLiveTestEvent.TestsDiscovered ([| mkInfo "B" |], false, 0L))
        prior
    Expect.isTrue (next.Tests |> Map.containsKey (mkTestId "A")) "partial batch must not sweep A"
    Expect.isTrue (next.Tests |> Map.containsKey (mkTestId "B")) "partial batch must add B"
    Expect.equal changes [ VscStateChange.TestsAdded [| mkInfo "B" |] ] "only TestsAdded is emitted")
]

/// Rediscovery sweep (Phase 6): a COMPLETE discovery from a NEWER generation
/// must REPLACE tests/results — tests renamed/deleted server-side must not
/// linger as stale items/decorations. Partial batches keep merge semantics.
[<Tests>]
let sweepTests = testList "VscLiveTestState rediscovery sweep" [
  testCase "complete discovery from a newer generation sweeps removed tests and their results" (fun () ->
    let prior =
      { VscLiveTestState.empty with
          Tests = [| mkInfo "A"; mkInfo "B" |] |> Array.map (fun t -> t.Id, t) |> Map.ofArray
          Results =
            [| mkResult "A" VscTestOutcome.Passed
               mkResult "B" (VscTestOutcome.Failed "broke") |]
            |> Array.map (fun r -> r.Id, r) |> Map.ofArray }

    let next, changes =
      VscLiveTestState.update
        (VscLiveTestEvent.TestsDiscovered ([| mkInfo "A" |], true, 1L))
        prior

    Expect.isTrue (next.Tests |> Map.containsKey (mkTestId "A")) "A should survive the sweep"
    Expect.isFalse (next.Tests |> Map.containsKey (mkTestId "B")) "B (absent from the new discovery) must be swept"
    Expect.isFalse (next.Results |> Map.containsKey (mkTestId "B")) "B's stale result must be swept with it"
    Expect.equal next.DiscoveryGeneration 1L "state should record the applied generation"
    Expect.isTrue
      (changes
       |> List.exists (function
         | VscStateChange.TestsRemoved removed -> removed = [| mkTestId "B" |]
         | _ -> false))
      "the sweep should emit TestsRemoved so the TestController drops the stale item")

  testCase "partial discovery (streaming) keeps merge semantics and never sweeps" (fun () ->
    let prior =
      { VscLiveTestState.empty with
          Tests = [| mkInfo "A" |] |> Array.map (fun t -> t.Id, t) |> Map.ofArray }
    // A partial batch mentioning only B must ADD B, not sweep A.
    let next, _ =
      VscLiveTestState.update
        (VscLiveTestEvent.TestsDiscovered ([| mkInfo "B" |], false, 1L))
        prior
    Expect.isTrue (next.Tests |> Map.containsKey (mkTestId "A")) "partial batch must not sweep A"
    Expect.isTrue (next.Tests |> Map.containsKey (mkTestId "B")) "partial batch must add B")

  testCase "same-generation complete discovery does not sweep (idempotent refresh)" (fun () ->
    let prior =
      { VscLiveTestState.empty with
          Tests = [| mkInfo "A"; mkInfo "B" |] |> Array.map (fun t -> t.Id, t) |> Map.ofArray
          DiscoveryGeneration = 1L }
    let next, _ =
      VscLiveTestState.update
        (VscLiveTestEvent.TestsDiscovered ([| mkInfo "A" |], true, 1L))
        prior
    Expect.isTrue (next.Tests |> Map.containsKey (mkTestId "B")) "a same-generation refresh must not sweep (the server already applied it)")
]
