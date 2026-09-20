module SageFs.Tests.QuarantineTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Features.LiveTesting

// --- Helpers ---

let private tid name = TestId.TestId name
let private now = DateTimeOffset.UtcNow

let private mkTestCase name =
  { Id = tid name
    FullName = sprintf "Ns.%s" name
    DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.XUnit
    Category = TestCategory.Unit }

// --- Unit Tests ---

let evaluateTests = testList "QuarantineLogic.evaluate" [
  test "Environmental + not quarantined -> DoQuarantine" {
    let action = QuarantineLogic.evaluate (tid "t1") (FlakyClassification.Environmental 3) Map.empty now
    match action with
    | QuarantineAction.DoQuarantine (id, EnvironmentalFlaky (flips, _)) ->
      id |> Expect.equal "should target t1" (tid "t1")
      flips |> Expect.equal "should carry flip count" 3
    | other -> failtestf "expected DoQuarantine, got %A" other
  }

  test "Environmental + already quarantined -> NoChange" {
    let existing = Map.ofList [ tid "t1", EnvironmentalFlaky (2, now) ]
    QuarantineLogic.evaluate (tid "t1") (FlakyClassification.Environmental 4) existing now
    |> Expect.equal "already quarantined" QuarantineAction.NoChange
  }

  test "Stable + quarantined environmental -> Release" {
    let existing = Map.ofList [ tid "t1", EnvironmentalFlaky (3, now) ]
    match QuarantineLogic.evaluate (tid "t1") FlakyClassification.Stable existing now with
    | QuarantineAction.Release id -> id |> Expect.equal "should release t1" (tid "t1")
    | other -> failtestf "expected Release, got %A" other
  }

  test "Stable + not quarantined -> NoChange" {
    QuarantineLogic.evaluate (tid "t1") FlakyClassification.Stable Map.empty now
    |> Expect.equal "stable non-quarantined" QuarantineAction.NoChange
  }

  test "PropertyCounterexample never quarantines" {
    QuarantineLogic.evaluate (tid "t1") (FlakyClassification.PropertyCounterexample "(0)") Map.empty now
    |> Expect.equal "property bugs dont quarantine" QuarantineAction.NoChange
  }

  test "Insufficient -> NoChange" {
    QuarantineLogic.evaluate (tid "t1") FlakyClassification.Insufficient Map.empty now
    |> Expect.equal "insufficient no change" QuarantineAction.NoChange
  }

  test "ManualQuarantine not released by Stable" {
    let existing = Map.ofList [ tid "t1", ManualQuarantine ("CI flaky", now) ]
    QuarantineLogic.evaluate (tid "t1") FlakyClassification.Stable existing now
    |> Expect.equal "manual not auto-released" QuarantineAction.NoChange
  }

  test "ManualQuarantine not released by Environmental" {
    let existing = Map.ofList [ tid "t1", ManualQuarantine ("CI flaky", now) ]
    QuarantineLogic.evaluate (tid "t1") (FlakyClassification.Environmental 5) existing now
    |> Expect.equal "manual stays" QuarantineAction.NoChange
  }
]

let applyTests = testList "QuarantineLogic.apply" [
  test "DoQuarantine adds to map" {
    let reason = EnvironmentalFlaky (3, now)
    QuarantineLogic.apply (QuarantineAction.DoQuarantine (tid "t1", reason)) Map.empty
    |> Map.containsKey (tid "t1")
    |> Expect.isTrue "t1 should be quarantined"
  }

  test "Release removes from map" {
    let existing = Map.ofList [ tid "t1", EnvironmentalFlaky (2, now) ]
    QuarantineLogic.apply (QuarantineAction.Release (tid "t1")) existing
    |> Map.containsKey (tid "t1")
    |> Expect.isFalse "t1 should be released"
  }

  test "NoChange preserves map" {
    let existing = Map.ofList [ tid "t1", EnvironmentalFlaky (2, now) ]
    QuarantineLogic.apply QuarantineAction.NoChange existing
    |> Expect.equal "unchanged" existing
  }
]

let filterTests = testList "QuarantineLogic.filterQuarantined" [
  test "Quarantined test removed" {
    let q = Map.ofList [ tid "t1", EnvironmentalFlaky (3, now) ]
    QuarantineLogic.filterQuarantined q [| mkTestCase "t1" |]
    |> Array.length
    |> Expect.equal "should remove quarantined" 0
  }

  test "Non-quarantined test stays" {
    QuarantineLogic.filterQuarantined Map.empty [| mkTestCase "t1" |]
    |> Array.length
    |> Expect.equal "should keep" 1
  }

  test "Mixed keeps only non-quarantined" {
    let q = Map.ofList [ tid "t2", EnvironmentalFlaky (2, now) ]
    let result = QuarantineLogic.filterQuarantined q [| mkTestCase "t1"; mkTestCase "t2"; mkTestCase "t3" |]
    result |> Array.length |> Expect.equal "should keep 2" 2
    result |> Array.map (fun tc -> tc.DisplayName) |> Array.sort
    |> Expect.equal "t1 and t3" [| "t1"; "t3" |]
  }
]

let isQuarantinedTests = testList "QuarantineLogic.isQuarantined" [
  test "true for quarantined" {
    let q = Map.ofList [ tid "t1", EnvironmentalFlaky (2, now) ]
    QuarantineLogic.isQuarantined (tid "t1") q |> Expect.isTrue "should be quarantined"
  }

  test "false for non-quarantined" {
    QuarantineLogic.isQuarantined (tid "t1") Map.empty |> Expect.isFalse "not quarantined"
  }
]

// --- Property-Based Tests ---

let propertyTests = testList "Quarantine properties" [
  testProperty "evaluate then apply is idempotent for Environmental" (fun (flipCount: PositiveInt) ->
    let flips = flipCount.Get
    let testId = tid (sprintf "t-%d" flips)
    let action = QuarantineLogic.evaluate testId (FlakyClassification.Environmental flips) Map.empty now
    let q1 = QuarantineLogic.apply action Map.empty
    let action2 = QuarantineLogic.evaluate testId (FlakyClassification.Environmental flips) q1 now
    action2 = QuarantineAction.NoChange
  )

  testProperty "apply DoQuarantine then Release roundtrips to empty" (fun (name: NonEmptyString) ->
    let testId = tid name.Get
    let reason = EnvironmentalFlaky (2, now)
    let q =
      Map.empty
      |> QuarantineLogic.apply (QuarantineAction.DoQuarantine (testId, reason))
      |> QuarantineLogic.apply (QuarantineAction.Release testId)
    q |> Map.isEmpty
  )

  testProperty "apply NoChange is identity" (fun (names: string list) ->
    let q =
      names
      |> List.mapi (fun i n -> tid (sprintf "%s%d" n i), EnvironmentalFlaky (i, now))
      |> Map.ofList
    QuarantineLogic.apply QuarantineAction.NoChange q = q
  )

  testProperty "filterQuarantined removes exactly quarantined tests" (fun (qNames: Set<int>) (allNames: Set<int>) ->
    let quarantined =
      qNames
      |> Set.toList
      |> List.map (fun i -> tid (sprintf "t%d" i), EnvironmentalFlaky (1, now))
      |> Map.ofList
    let tests = allNames |> Set.toArray |> Array.map (fun i -> mkTestCase (sprintf "t%d" i))
    let result = QuarantineLogic.filterQuarantined quarantined tests
    let resultIds = result |> Array.map (fun tc -> tc.Id) |> Set.ofArray
    let quarantinedIds = quarantined |> Map.keys |> Set.ofSeq
    Set.intersect resultIds quarantinedIds |> Set.isEmpty
  )

  testProperty "isQuarantined consistent with Map.containsKey" (fun (name: NonEmptyString) (hasEntry: bool) ->
    let testId = tid name.Get
    let q =
      if hasEntry then Map.ofList [ testId, EnvironmentalFlaky (1, now) ]
      else Map.empty
    QuarantineLogic.isQuarantined testId q = (q |> Map.containsKey testId)
  )

  testProperty "ManualQuarantine survives any classification" (fun (flips: PositiveInt) ->
    let testId = tid "manual"
    let q = Map.ofList [ testId, ManualQuarantine ("reason", now) ]
    let classifications = [
      FlakyClassification.Stable
      FlakyClassification.Environmental flips.Get
      FlakyClassification.PropertyCounterexample "(0)"
      FlakyClassification.Insufficient
    ]
    classifications |> List.forall (fun c ->
      QuarantineLogic.evaluate testId c q now = QuarantineAction.NoChange)
  )

  testProperty "Environmental quarantine released only by Stable" (fun (flips: PositiveInt) ->
    let testId = tid "env"
    let q = Map.ofList [ testId, EnvironmentalFlaky (flips.Get, now) ]
    let stableAction = QuarantineLogic.evaluate testId FlakyClassification.Stable q now
    let envAction = QuarantineLogic.evaluate testId (FlakyClassification.Environmental flips.Get) q now
    let propAction = QuarantineLogic.evaluate testId (FlakyClassification.PropertyCounterexample "(0)") q now
    let insAction = QuarantineLogic.evaluate testId FlakyClassification.Insufficient q now
    match stableAction with
    | QuarantineAction.Release _ ->
      envAction = QuarantineAction.NoChange
      && propAction = QuarantineAction.NoChange
      && insAction = QuarantineAction.NoChange
    | _ -> false
  )
]

// --- Production wiring ---
// QuarantineLogic.evaluate/apply/isQuarantined/filterQuarantined were complete,
// correct, and property-tested above with ZERO non-test callers: a test could
// be classified flaky and the product would never demote it. This drives the
// REAL fold (SageFsUpdate.update, via applyBufferedTestResults's
// recordResult fold) and the REAL affected-test selection path
// (TuiEvent.AffectedTestsComputed), not QuarantineLogic directly.
let wiredIntoProductionTests = testList "QuarantineLogic wired into production" [
  test "a quarantined test is excluded from the next selection" {
    let flakyId = tid "FlakyTest"
    let flakyCase = mkTestCase "FlakyTest"

    // Seed FlakyHistory with two prior outcomes (Pass, Fail) so the batch's
    // next result completes a 3-sample window with 2 flips (Pass-Fail,
    // Fail-Pass) — enough to classify Environmental per FlakyDefaults
    // (minSamples=3, flipThreshold=2), which QuarantineLogic.evaluate
    // quarantines.
    let seededHistory =
      ResultWindow.create FlakyDefaults.windowSize
      |> ResultWindow.add TestOutcome.Pass
      |> ResultWindow.add TestOutcome.Fail

    let baseModel = SageFsModel.initial ()
    let seededState =
      { baseModel.LiveTesting.TestState with
          Activation = LiveTestingActivation.Active
          DiscoveredTests = [| flakyCase |]
          FlakyHistory = Map.ofList [ flakyId, seededHistory ] }
    let model =
      { baseModel with
          LiveTesting = { baseModel.LiveTesting with TestState = seededState } }

    let batchResult : TestRunResult =
      { TestId = flakyId
        TestName = "FlakyTest"
        Result = TestResult.Passed (TimeSpan.FromMilliseconds 5.0)
        Timestamp = now
        Output = None }

    // act: run the production result-merge path, then the production
    // affected-test selection path.
    let modelAfterResult, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (TuiEvent.TestResultsBatch (None, [| batchResult |])))
        model
    let _, effects =
      SageFsUpdate.update
        (SageFsMsg.Event (TuiEvent.AffectedTestsComputed ([| flakyId |], [])))
        modelAfterResult

    let selected =
      effects
      |> List.collect (function
        | SageFsEffect.TestCycle (TestCycleEffect.RunAffectedTests req) ->
          req.Tests |> Array.toList |> List.map (fun tc -> tc.Id)
        | _ -> [])

    selected
    |> List.contains flakyId
    |> Expect.isFalse "a quarantined test must not be selected"
  }
]

[<Tests>]
let allQuarantineTests = testList "Quarantine" [
  evaluateTests
  applyTests
  filterTests
  isQuarantinedTests
  propertyTests
  wiredIntoProductionTests
]
