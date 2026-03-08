module SageFs.Tests.FlakyClassificationPropertyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.LiveTesting

let private tid name = TestId.TestId name

let private mkWindow (outcomes: TestOutcome list) =
  let mutable w = ResultWindow.create 10
  for o in outcomes do
    w <- ResultWindow.add o w
  w

let private mkResult testName (result: TestResult) : TestRunResult =
  { TestId = tid testName
    TestName = testName
    Result = result
    Timestamp = DateTimeOffset.UtcNow
    Output = None }

let private fsCheckMsg original shrunk =
  sprintf "Falsifiable, after 42 tests\nOriginal:\n%s\nShrunk:\n%s" original shrunk

let private fsCheckMsgNoShrink original =
  sprintf "Falsifiable, after 10 tests\nOriginal:\n%s" original

let private truncate n (s: string) =
  match s.Length > n with
  | true -> s.Substring(0, n) + "..."
  | false -> s

// ── Phase 1: isFsCheckFailure regex robustness ──────────────────────

[<Tests>]
let regexRobustnessTests = testList "isFsCheckFailure regex robustness" [

  testCase "non-FsCheck messages never match (sampled)" <| fun _ ->
    let msgs = [
      "Expected 42 but got 0"; "System.TimeoutException"
      "NullReferenceException at line 5"; "Assert.Equal failure"
      "The test timed out after 5000ms"; "Connection refused"
      """{"error": "not found"}"""
      "should be 42.\nexpected: 42\n  actual: 0"
      String('x', 500); "Falsifiable"
    ]
    for msg in msgs do
      FlakyDetection.isFsCheckFailure msg
      |> Expect.isNone (sprintf "should not match: %s" (truncate 40 msg))

  testProperty "shrunk preferred over original" <| fun (a: byte, b: byte) ->
    let orig = sprintf "(%d, %d)" (int a) (int b)
    let shrunk = sprintf "(%d)" (int a % 5)
    let msg = fsCheckMsg orig shrunk
    match FlakyDetection.isFsCheckFailure msg with
    | Some extracted -> extracted = shrunk
    | None -> false

  testProperty "original extracted when no Shrunk" <| fun (a: byte, b: bool) ->
    let orig = sprintf "(%d, %b)" (int a) b
    let msg = fsCheckMsgNoShrink orig
    match FlakyDetection.isFsCheckFailure msg with
    | Some extracted -> extracted = orig
    | None -> false

  testProperty "whitespace always None" <| fun (n: byte) ->
    let ws = String(' ', int n)
    FlakyDetection.isFsCheckFailure ws |> Option.isNone

  testProperty "varying test counts parse" <| fun (n: uint16) ->
    let count = max 1 (int n)
    let msg = sprintf "Falsifiable, after %d tests\nOriginal:\n(42)" count
    FlakyDetection.isFsCheckFailure msg |> Option.isSome

  testProperty "tuple counterexamples with varying arity" <| fun (a: byte, b: byte, c: byte) ->
    let orig = sprintf "(%d, %d, %d)" (int a) (int b) (int c)
    let shrunk = sprintf "(%d, %d, %d)" 0 0 0
    let msg = fsCheckMsg orig shrunk
    match FlakyDetection.isFsCheckFailure msg with
    | Some extracted -> extracted = shrunk
    | None -> false
]

// ── Phase 2: classifyFlakiness state machine properties ─────────────

[<Tests>]
let classificationStateTests = testList "classifyFlakiness state machine properties" [

  testProperty "empty history always yields Insufficient" <| fun (n: byte) ->
    let name = sprintf "test_%d" (int n)
    FlakyDetection.classifyFlakiness (tid name) Map.empty Map.empty
    = FlakyClassification.Insufficient

  testProperty "all-pass window yields Stable" <| fun (count: byte) ->
    let n = (int count % 17) + 3
    let w = mkWindow (List.replicate n TestOutcome.Pass)
    let history = Map.ofList [ tid "prop", w ]
    FlakyDetection.classifyFlakiness (tid "prop") history Map.empty
    = FlakyClassification.Stable

  testProperty "all-fail window yields Stable (consistent failure)" <| fun (count: byte) ->
    let n = (int count % 17) + 3
    let w = mkWindow (List.replicate n TestOutcome.Fail)
    let history = Map.ofList [ tid "prop", w ]
    FlakyDetection.classifyFlakiness (tid "prop") history Map.empty
    = FlakyClassification.Stable

  testProperty "alternating yields Environmental when no FsCheck msg" <| fun (pairs: byte) ->
    let n = max 2 (int pairs % 10)
    let outcomes =
      [ for _ in 1..n do
          yield TestOutcome.Pass
          yield TestOutcome.Fail ]
    let w = mkWindow outcomes
    let history = Map.ofList [ tid "prop", w ]
    let results =
      Map.ofList [
        tid "prop",
        mkResult "prop"
          (TestResult.Failed
            (TestFailure.AssertionFailed "regular failure",
             TimeSpan.FromMilliseconds 10.0)) ]
    match FlakyDetection.classifyFlakiness (tid "prop") history results with
    | FlakyClassification.Environmental _ -> true
    | _ -> false

  testProperty "alternating yields PropertyCounterexample when FsCheck msg" <| fun (pairs: byte, shrunkVal: byte) ->
    let n = max 2 (int pairs % 10)
    let outcomes =
      [ for _ in 1..n do
          yield TestOutcome.Pass
          yield TestOutcome.Fail ]
    let w = mkWindow outcomes
    let s = sprintf "(%d)" (int shrunkVal)
    let history = Map.ofList [ tid "prop", w ]
    let msg = fsCheckMsg "(original)" s
    let results =
      Map.ofList [
        tid "prop",
        mkResult "prop"
          (TestResult.Failed
            (TestFailure.AssertionFailed msg,
             TimeSpan.FromMilliseconds 10.0)) ]
    match FlakyDetection.classifyFlakiness (tid "prop") history results with
    | FlakyClassification.PropertyCounterexample ce -> ce = s
    | _ -> false

  testProperty "classification is deterministic" <| fun (outcomes: TestOutcome list) ->
    let outcomes' =
      match outcomes with
      | [] -> [ TestOutcome.Pass; TestOutcome.Pass; TestOutcome.Pass ]
      | xs -> xs
    let w = mkWindow outcomes'
    let history = Map.ofList [ tid "det", w ]
    let c1 = FlakyDetection.classifyFlakiness (tid "det") history Map.empty
    let c2 = FlakyDetection.classifyFlakiness (tid "det") history Map.empty
    c1 = c2
]

// ── Phase 3: classification → quarantine integration ────────────────

[<Tests>]
let classificationQuarantineTests = testList "classification to quarantine integration" [

  testProperty "Environmental always DoQuarantine when not quarantined" <| fun (flips: byte) ->
    let f = max 2 (int flips)
    let id = tid "t1"
    let now = DateTimeOffset.UtcNow
    match QuarantineLogic.evaluate id (FlakyClassification.Environmental f) Map.empty now with
    | QuarantineAction.DoQuarantine (_, EnvironmentalFlaky (n, _)) -> n = f
    | _ -> false

  testProperty "PropertyCounterexample never quarantines" <| fun (n: byte) ->
    let ce = sprintf "(%d)" (int n)
    let id = tid "t1"
    let now = DateTimeOffset.UtcNow
    let notQ =
      QuarantineLogic.evaluate id
        (FlakyClassification.PropertyCounterexample ce) Map.empty now
    let isQ =
      QuarantineLogic.evaluate id
        (FlakyClassification.PropertyCounterexample ce)
        (Map.ofList [ id, EnvironmentalFlaky (3, now) ]) now
    notQ = QuarantineAction.NoChange && isQ = QuarantineAction.NoChange

  testProperty "Insufficient never changes quarantine" <| fun (hasEntry: bool) ->
    let id = tid "t1"
    let now = DateTimeOffset.UtcNow
    let q =
      match hasEntry with
      | true -> Map.ofList [ id, EnvironmentalFlaky (3, now) ]
      | false -> Map.empty
    QuarantineLogic.evaluate id FlakyClassification.Insufficient q now
    = QuarantineAction.NoChange

  testProperty "Stable releases Environmental quarantine" <| fun (flips: byte) ->
    let f = max 1 (int flips)
    let id = tid "t1"
    let now = DateTimeOffset.UtcNow
    let q = Map.ofList [ id, EnvironmentalFlaky (f, now) ]
    QuarantineLogic.evaluate id FlakyClassification.Stable q now
    = QuarantineAction.Release id

  testProperty "Stable does NOT release Manual quarantine" <| fun (n: byte) ->
    let id = tid "t1"
    let now = DateTimeOffset.UtcNow
    let q = Map.ofList [ id, ManualQuarantine (sprintf "reason %d" (int n), now) ]
    QuarantineLogic.evaluate id FlakyClassification.Stable q now
    = QuarantineAction.NoChange

  testProperty "Environmental idempotent when already quarantined" <| fun (flips: byte) ->
    let f = max 2 (int flips)
    let id = tid "t1"
    let now = DateTimeOffset.UtcNow
    let q = Map.ofList [ id, EnvironmentalFlaky (3, now) ]
    QuarantineLogic.evaluate id (FlakyClassification.Environmental f) q now
    = QuarantineAction.NoChange
]
