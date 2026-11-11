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
    NamePath = []
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
// These drive the real fold (SageFsUpdate.update: applyBufferedTestResults records each sample with the cycle's
// input epoch and decides the quarantine before the rows are computed) and the real selection path
// (TuiEvent.AffectedTestsComputed), not QuarantineLogic directly.

let private wiredId = TestId.create "Ns.Tests.wired" TestFramework.Expecto
let private wiredCase : TestCase = { mkTestCase "wired" with Id = wiredId; Framework = TestFramework.Expecto }

let private activeModel () =
  let b = SageFsModel.initial ()
  { b with
      LiveTesting =
        { b.LiveTesting with
            TestState = { b.LiveTesting.TestState with Activation = LiveTestingActivation.Active; DiscoveredTests = [| wiredCase |] } } }

let private ran (passed: bool) (model: SageFsModel) =
  let result =
    match passed with
    | true -> TestResult.Passed FixtureDurations.usualResult
    | false -> TestResult.Failed (TestFailure.AssertionFailed "no", FixtureDurations.usualResult)
  let r : TestRunResult = { TestId = wiredId; TestName = wiredCase.FullName; Result = result; Timestamp = now; Output = None }
  SageFsUpdate.update (SageFsMsg.Event (TuiEvent.TestResultsBatch (None, [| r |]))) model |> fst

let private edited (n: int) (model: SageFsModel) =
  SageFsUpdate.update (SageFsMsg.BufferContentChanged (None, "Wired.fs", sprintf "let x = %d" n)) model |> fst

let private rowOf (model: SageFsModel) =
  LiveTestState.tryFindStatusEntry wiredId model.LiveTesting.TestState |> Option.map (fun e -> e.Status)

let private isQuarantined (model: SageFsModel) = model.LiveTesting.TestState.Quarantined |> Map.containsKey wiredId

/// One step of a session's life: an edit, or a run that passed (true) or failed (false).
type WiredStep =
  | Edit
  | Ran of passed: bool

let private play (steps: WiredStep list) =
  steps
  |> List.mapi (fun i step -> i, step)
  |> List.scan
    (fun model (i, step) ->
      match step with
      | Edit -> edited i model
      | Ran passed -> ran passed model)
    (activeModel ())

let wiredIntoProductionTests = testList "QuarantineLogic wired into production" [
  test "WHY — red then green across edits is not quarantined, because that is the TDD loop the product sells" {
    let model = activeModel () |> ran true |> edited 1 |> ran false |> edited 2 |> ran true
    model |> isQuarantined |> Expect.isFalse "pass, edit, fail, edit, pass is two verdicts, not a flake"
    rowOf model |> Expect.equal "the row shows the fix" (Some (TestRunStatus.Passed FixtureDurations.usualResult))
  }

  test "WHY — a test that flips with no edit between runs is quarantined, and its row never shows its last green" {
    let model = activeModel () |> ran true |> ran false |> ran true
    model |> isQuarantined |> Expect.isTrue "two flips under one epoch is a flake"
    match rowOf model with
    | Some (TestRunStatus.Quarantined _) -> ()
    | other -> failtestf "expected a Quarantined row, was %A" other
    let summary = TestSummary.fromStatuses LiveTestingActivation.Active [| (rowOf model).Value |]
    summary.Passed |> Expect.equal "a quarantined test is not counted as passing" 0
  }

  test "a quarantined test is excluded from the next selection" {
    let quarantined = activeModel () |> ran true |> ran false |> ran true
    let _, effects =
      SageFsUpdate.update (SageFsMsg.Event (TuiEvent.AffectedTestsComputed ([| wiredId |], []))) quarantined
    effects
    |> List.collect (function
      | SageFsEffect.TestCycle (TestCycleEffect.RunAffectedTests req) -> req.Tests |> Array.toList |> List.map (fun tc -> tc.Id)
      | _ -> [])
    |> List.contains wiredId
    |> Expect.isFalse "a quarantined test must not be selected"
  }

  testProperty "for any sequence of edits and results, a quarantined test is never counted as passing" (fun (steps: WiredStep list) ->
    play steps
    |> List.forall (fun model ->
      match isQuarantined model, rowOf model with
      | true, Some (TestRunStatus.Quarantined _) -> true
      | true, _ -> false
      | false, _ -> true))

  testProperty "a run between every two edits never quarantines" (fun (verdicts: bool list) ->
    verdicts
    |> List.collect (fun passed -> [ Edit; Ran passed ])
    |> play
    |> List.forall (isQuarantined >> not))
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
