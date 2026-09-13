module SageFs.Tests.VscLiveTestStateTests

open Expecto
open Expecto.Flip
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
    s.Total |> Expect.equal "total 0" 0
    s.Passed |> Expect.equal "passed 0" 0
    s.Failed |> Expect.equal "failed 0" 0
    s.Running |> Expect.equal "running 0" 0
    s.Stale |> Expect.equal "stale 0" 0
    s.Disabled |> Expect.equal "disabled 0" 0)

  testCase "partial discovery adds tests without sweeping existing ones" (fun () ->
    let prior =
      { VscLiveTestState.empty with
          Tests = [| mkInfo "A" |] |> Array.map (fun t -> t.Id, t) |> Map.ofArray }
    let next, changes =
      VscLiveTestState.update
        (VscLiveTestEvent.TestsDiscovered ([| mkInfo "B" |], false, 0L))
        prior
    (next.Tests |> Map.containsKey (mkTestId "A")) |> Expect.isTrue "partial batch must not sweep A"
    (next.Tests |> Map.containsKey (mkTestId "B")) |> Expect.isTrue "partial batch must add B"
    changes |> Expect.equal "only TestsAdded is emitted" [ VscStateChange.TestsAdded [| mkInfo "B" |] ])
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

    (next.Tests |> Map.containsKey (mkTestId "A")) |> Expect.isTrue "A should survive the sweep"
    (next.Tests |> Map.containsKey (mkTestId "B")) |> Expect.isFalse "B (absent from the new discovery) must be swept"
    (next.Results |> Map.containsKey (mkTestId "B")) |> Expect.isFalse "B's stale result must be swept with it"
    next.DiscoveryGeneration |> Expect.equal "state should record the applied generation" 1L
    (changes
       |> List.exists (function
         | VscStateChange.TestsRemoved removed -> removed = [| mkTestId "B" |]
         | _ -> false)) |> Expect.isTrue "the sweep should emit TestsRemoved so the TestController drops the stale item")

  testCase "partial discovery (streaming) keeps merge semantics and never sweeps" (fun () ->
    let prior =
      { VscLiveTestState.empty with
          Tests = [| mkInfo "A" |] |> Array.map (fun t -> t.Id, t) |> Map.ofArray }
    // A partial batch mentioning only B must ADD B, not sweep A.
    let next, _ =
      VscLiveTestState.update
        (VscLiveTestEvent.TestsDiscovered ([| mkInfo "B" |], false, 1L))
        prior
    (next.Tests |> Map.containsKey (mkTestId "A")) |> Expect.isTrue "partial batch must not sweep A"
    (next.Tests |> Map.containsKey (mkTestId "B")) |> Expect.isTrue "partial batch must add B")

  testCase "same-generation complete discovery does not sweep (idempotent refresh)" (fun () ->
    let prior =
      { VscLiveTestState.empty with
          Tests = [| mkInfo "A"; mkInfo "B" |] |> Array.map (fun t -> t.Id, t) |> Map.ofArray
          DiscoveryGeneration = 1L }
    let next, _ =
      VscLiveTestState.update
        (VscLiveTestEvent.TestsDiscovered ([| mkInfo "A" |], true, 1L))
        prior
    (next.Tests |> Map.containsKey (mkTestId "B")) |> Expect.isTrue "a same-generation refresh must not sweep (the server already applied it)")
]
